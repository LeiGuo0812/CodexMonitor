# 内存原因分析与优化空间

日期：2026-09-27。功能版本：v1.0.2；内存基线测量对象为已发布的 v1.0.1。此次实现可保存的刷新频率，默认保持 60 秒；以下优化建议尚未实施，不宣称已降低内存占用。

## 实测结果

环境与完整主进程、查询子进程测量见 [v1.0.1 资源评估](resource-assessment-v1.0.1.md)。之前后台工作集平均约 157 MiB，打开详情和多次交互后约 215 MiB；关闭详情没有恢复到最初水平。

此次继续观察同一进程（已运行约 15–18 分钟），通过工作区本地的 dotnet-counters 10.0.745401 收集 System.Runtime。诊断工具未打包进 EXE。未强制 GC 或压缩工作集。

| 口径 / 时间段 | 实测结果 | 含义与限制 |
| --- | --- | --- |
| Windows 进程快照：工作集 | 238.88 MiB | 当前物理驻留，包含共享页 |
| 同一快照：私有工作集 | 113.37 MiB | 当前驻留且不与其他进程共享的页 |
| 同一快照：私有提交量 | 149.43 MiB | 私有已提交虚拟内存，不等于当前物理占用 |
| 随后 15 秒：GC Heap Size | 32.33–32.98 MiB | `GC.GetTotalMemory(false)` 的近似值，未强制回收，不等于全部都是存活对象 |
| 同一 15 秒：工作集 | 239.64–240.28 MiB | 仅主程序，不含查询子进程 |
| 前一段 45 秒：分配速率 | 平均 65.03 KiB/s | 44 个速率区间累计约 2.79 MiB；属于分配吞吐，不是泄漏速率 |
| 后一段 15 秒：分配速率 | 平均 45.10 KiB/s | 采样阶段不同，不能当作优化前后对照 |
| 两段采样：各代 GC 次数 | 均为 0 | 短时间累积的临时对象尚未被 GC 回收 |

第一段 Meter 计数器还显示：**最近一次 GC** 的堆大小约 1.18 MiB、提交空间约 15.93 MiB。由于采样期间没有 GC，这些是上次 GC 的统计，不能当作当前堆大小或当前提交上限；因此另外采集了上表的即时近似堆计数器。不能用 1.18 MiB 得出“托管内存几乎没有占用”的结论。

计数器原始 MB 使用十进制，表格统一换算 MiB。Windows 快照和两段计数器不是完全同一时刻；诊断连接本身也会产生开销。此次没有逐模块原生堆分配追踪，也没有数小时稳定性测量，不能据此证明或排除泄漏。

计数器口径来自 [Microsoft System.Runtime EventCounters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/available-counters) 与 [运行时 Meter 指标](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/built-in-metrics-runtime)。工作集和私有提交量分别参见 [WorkingSet64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.workingset64?view=net-10.0) 和 [PrivateMemorySize64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.privatememorysize64?view=net-10.0)。

## 代码中能确认的开销

### 1. WinUI 常驻及首次使用后的缓存

启动即创建 WinUI 数字条，所以即使没有打开详情，也已加载 .NET、XAML 和 Composition。进程工作集还包含共享 DLL、JIT 代码、原生对象及渲染资源。它与托管堆大小并不相等，不能直接相减精确归因。

详情打开后增加的对象、渲染资源和框架缓存，是内存不立即下降的合理解释，但具体各占多少仍需原生内存跟踪确认。单文件压缩减少分发体积，不会同步减少运行时占用；磁盘解压缓存也不是全部同时进入内存。

### 2. 数字条每秒重复读取样式并创建对象

见 [WidgetWindow.xaml.cs](../src/CodexQuotaMonitor.App/WidgetWindow.xaml.cs)、[TaskbarAppearance.cs](../src/CodexQuotaMonitor.App/TaskbarAppearance.cs) 和 [WindowAppearance.cs](../src/CodexQuotaMonitor.App/WindowAppearance.cs)。

- 定位定时器和倒计时定时器都每秒执行，正常可见状态下，两条路径都会调用 `UpdateTaskbarAppearance`。
- 每次读取任务栏外观都创建 `UISettings`、`AccessibilitySettings` 并读取注册表；跟随系统的主题计算还会再创建一个 `UISettings`。
- `WindowTag` 与 `StateMarker` 的画刷在样式未变化的提前返回之前就被重新创建。按每秒两次路径估算，正常跟随系统时约 6 个设置包装对象、4 个画刷/秒；这是代码路径估算，并非逐对象采样计数。
- 文字、提示、测量与定位也每秒重做，其中很多可见内容实际以分钟为单位变化。现有悬停渐变画刷已按颜色变化更新，不能将其全部算作每秒重建。

这些能解释持续分配压力，尚不能证明全部对象长期存活或构成泄漏。

### 3. 详情刷新时重建卡片

见 [DashboardWindow.xaml.cs](../src/CodexQuotaMonitor.App/DashboardWindow.xaml.cs)。刷新开始和结束都会更新 UI，额度与卡片区域使用清空集合后重新创建控件的方式，即使额度快照没有变化，也会产生 Grid、Border、TextBlock、画刷和提示对象。每秒时间更新中也有重复格式化和提示设置。

### 4. 查询子进程的短时占用

每次查询使用短生命周期 `codex app-server`；查询完成关闭或清理本程序启动的进程。已有单次并发控制，只保留最新额度快照，未发现不断累积历史数据的结构。先前测量的查询子进程工作集峰值约 110–151 MiB，与主程序不能简单相加视为独占 RAM，因为可能重复统计共享页。

因此，**延长刷新间隔会减少进程启动次数、瞬时占用出现频率和时间平均开销；不会直接降低主程序的框架常驻基线**。一分钟和三分钟不是必然完全相同的总资源消耗。

## 建议的优化顺序

| 优先级 | 修改 | 预期作用 | 成本与风险 |
| --- | --- | --- | --- |
| 1 | 复用系统外观对象与画刷；合并重复样式更新；系统外观事件驱动并保留低频校验 | 减少每秒托管与 WinRT 分配 | 较小；需验证主题、高对比和任务栏切换 |
| 2 | 文本变化时才赋值/测量；提示按内容缓存；保留每秒时钟判断 | 减少字符串、布局和提示对象开销 | 较小；需防止倒计时和边界布局不更新 |
| 3 | 详情卡片保留控件，按数据差异更新；忙碌状态只更新按钮/提示 | 减少每次刷新导致的整树分配 | 中等；需覆盖缺失字段、卡片数量变化及首次动画 |
| 4 | 反复开关窗口后跟踪对象保留与原生资源 | 查明是否存在生命周期问题 | 中等；现有时钟停止、Acrylic Controller Dispose 和 GDI 释放已存在，不能直接指认遗漏 |
| 5 | 数字条改为 Win32/DirectWrite，延迟启动 WinUI 详情 | 有机会显著降低首次后台的框架基线 | 大；涉及透明、DPI、输入、可访问性和布局。详情使用后若仍在同进程，框架未必卸载，完全释放可能需要独立详情进程 |

前 3 项值得优先做，但在同环境 A/B 测量前，不承诺能节省多少 MiB。强制 `GC.Collect` 或定时压缩工作集只会改变某些瞬时读数，不能替代减少对象创建和修复真实泄漏。

后续验收应同时记录主程序私有提交量、私有工作集、托管堆/分配率、查询子进程与 CPU；同条件重复打开/关闭详情，再进行数小时后台观察。

## 复现补测

已安装 dotnet-counters 时，可对本程序 PID 执行（输出目录需存在）：

```powershell
dotnet-counters collect --process-id <PID> --counters System.Runtime --duration 00:00:45 --refresh-interval 1 --format json --output .artifacts/managed-counters.json
dotnet-counters collect --process-id <PID> --counters 'EventCounters\System.Runtime[gc-heap-size,gc-committed,alloc-rate,gen-0-gc-count,gen-1-gc-count,gen-2-gc-count,working-set]' --duration 00:00:15 --refresh-interval 1 --format json --output .artifacts/live-heap.json
```

采样方法参见 [dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)。这些诊断输出与工具仅在忽略目录中，不进入源码发布或发行包。
