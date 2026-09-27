# CodexMonitor

Windows 11 上的 Codex 额度与重置时间监控工具，使用 C#、.NET 10 和 WinUI 3。

![应用图标](src/CodexQuotaMonitor.App/Assets/cqm-preview.png)

## 下载与运行

从 [GitHub Releases](https://github.com/LeiGuo0812/CodexMonitor/releases) 下载 Windows x64 版本。

- 需要 Windows 11 x64，以及已经登录 ChatGPT 订阅账户的 Codex CLI 或 Codex 桌面版。
- 单 EXE 自包含发行包无需另外安装 .NET 或 Windows App SDK。
- 双击后常驻任务栏附近和系统托盘；双击数字条或托盘图标打开详情，右键打开操作菜单。
- **单文件指分发形式**：运行时自动释放内置依赖到系统缓存，不是运行全过程零落盘。EXE 所在目录无需附加 DLL。

## 功能

- 两行数字条：剩余额度、当前额度窗口的自然重置倒计时。
- 主额度优先显示 5 小时窗口；仅提供周额度时显示周额度。5 小时额度耗尽不自动切换到周额度。
- 详情固定展示 5 小时和周额度重置；无 5 小时数据时只显示“5小时额度不可用”，面板等宽纵向排列。
- 独立重置卡总数、逐卡截止时间和状态；可用卡显示“某时间前可用”。
- 透明任务栏背景、两侧细线、彩色窗口标签；悬停时均匀提亮为低亮度梭形与柔光边缘。
- 青绿色 Q 额度环托盘图标，按屏幕 DPI 加载合适尺寸。
- 紧凑磨砂详情、统一标题栏、进度条初次加载动画、人类可读日期和完整时间复制。
- 四套详情主题、自定义配色、字号与位置调整；详情与设置标题栏均融入主体磨砂背景；右键菜单快速开关开机启动。
- 独立“清除缓存”操作：使用中的运行库延迟到正常退出后清理。
- 自动、任务栏优先和固定上移模式；可拖出和拖回任务栏；全屏应用期间隐藏。
- 后台刷新、手动刷新、单实例运行、失效数据和连接异常提示。

数字条始终是独立窗口，并非 Windows 原生任务栏槽位。只有识别到可靠空位时才贴合，空间不足或无法识别时上移。

## 数据与隐私

通过本机 codex app-server 复用 Codex 登录状态，仅调用监控所需的只读账户和额度接口。不发起模型对话，不使用本地 token 消耗估算官方额度，不自动使用重置卡。

- 后台默认 60 秒刷新，可在设置中修改；失败后退避，相邻请求合并。
- 没有可信数据时显示未知状态，不填充演示数据。
- 额度、卡片及账户切换信息只保存在内存，不保存原始账户响应或凭据。
- 设置：%LOCALAPPDATA%\CodexQuotaMonitor\settings.json。
- 未处理的 UI 异常诊断：同目录下 ui-failure.json。
- 单文件依赖缓存由 .NET 管理，默认位于 `%TEMP%\.net\<EXE名称>\<版本哈希>\`；首次启动会写入运行库文件。设置 `DOTNET_BUNDLE_EXTRACT_BASE_DIR` 时使用该目录。

接口说明参见 [Codex app-server 文档](https://developers.openai.com/codex/app-server)。不同账户、套餐和 CLI 版本返回的额度窗口及卡片详情可能不同。

## 刷新频率

右键托盘图标或任务栏文字，打开“外观与位置”，在“数据刷新”中输入后台刷新间隔（分钟），点击“保存”。

- 默认 **1 分钟**，支持 **0.5–60 分钟**，可以输入小数，保存时换算为整秒。
- 保存后立即重新计算正在等待的后台刷新时间，无需重启；取消或直接关闭设置不会改变已保存的频率。
- 已有自定义频率保留，升级不会强制覆盖。失败时延长重试间隔，不会比设置值更频繁。
- 间隔从上一次后台查询结束计算；倒计时仍在本地每秒更新，手动刷新和打开详情会立即查询。
- 延长间隔可以减少查询子进程启动次数及其时间平均开销，但不会消除 WinUI / .NET 常驻开销，也不会改变界面倒计时的更新频率。

## 缓存清理与开机启动

右键托盘图标或任务栏文字，即可使用两个独立选项；设置页的“启动与维护”也保留相同入口：

- **清除缓存**：立即删除未使用的本程序版本缓存和异常日志，程序继续运行。当前运行库正在使用，会在正常退出后由隐藏的系统 PowerShell 进程清理；请等待几秒再重新启动。
- **退出**：继续使用右键菜单中的独立退出入口。仅在本次运行点击过“清除缓存”时，退出才触发延迟清理。
- 清理保留 `settings.json`、开机启动选项及 Codex 登录信息；下次运行 EXE 会重新释放必要依赖。
- 只清理带有本程序所有权标记的缓存，不清理其他应用的 `.net` 目录。若曾修改自定义解压目录，旧位置需在退出后手动清理：确认目录包含 `codex-monitor-cache-owner.txt` 且内容以 `CodexMonitor:` 开头，只删除对应版本哈希目录。
- **开机启动**：右键菜单中勾选代表已注册，点击即可开启或关闭，立即保存生效；不会顺带保存尚未确认的外观修改。设置页的开关仍需点击“保存”。启动项保存在当前用户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `CodexQuotaMonitor` 值，指向实际 EXE，而非解压缓存。
- 移动 EXE 后，请从新位置运行并重新保存设置，以更新启动路径。缓存清理本身不会关闭开机启动。

清理按钮无需退出或保存设置即可使用。若程序被强制结束，延迟清理可能无法执行，可下次运行时再次清理；清理进程被系统策略阻止时，可退出后手动删除已确认归属本程序的缓存目录。Windows 自行维护的预取、图标等系统记录不属于本程序缓存清理范围。

## 从源码构建

环境：Windows、.NET 10 SDK、Windows SDK 10.0.26100 或更新版本。依赖版本在项目文件中固定。

```powershell
# 可选：安装工作区本地 .NET SDK
.\scripts\Install-DotNet.ps1

# 核心回归测试与应用构建
.\scripts\Build.ps1 -Configuration Release

# 生成单文件版（目标目录需为空）
.\scripts\Publish.ps1

# 运行单文件版
.\scripts\Run.ps1

# 如需目录式发行包
.\scripts\Publish.ps1 -DirectoryBundle -OutputDirectory .\publish\directory-bundle
```

默认输出 publish/single-file/CodexQuotaMonitor.exe。程序内置运行库、WinUI 资源索引和图标，运行时自动解压；使用 Microsoft 支持的 [WinUI 单文件部署配置](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app#single-file-exe)。

## 验证

```powershell
.\scripts\Test.ps1 -Configuration Release

# 先退出正在运行的 CodexMonitor；会短暂打开本程序窗口并查询真实额度
.\scripts\Verify-Runtime.ps1

# 约 5 分钟，测量主程序及其查询子进程；会打开/关闭详情，完成后保留应用运行
.\scripts\Measure-Resources.ps1
```

必要的自动化测试和运行检查保留在仓库。自检会临时验证并恢复本程序启动项与设置，还会请求缓存清理，结束后检查解压缓存已移除。报告写入被 Git 忽略的 .artifacts 目录；历史调试报告、一次性探测工具和测试产物不进入发行包。覆盖范围与未验证项目见 [验证说明](docs/validation.md)。

实机内存与 CPU 测量方法、结果和限制见 [资源占用评估](docs/resource-assessment-v1.0.1.md)。

托管堆补测及原先的重复分配见 [内存原因分析](docs/memory-analysis-v1.0.2.md)。v1.0.3 已实现按需更新、控件复用和最小化暂停，修改前后的同场景测量见 [资源优化报告](docs/resource-optimization-v1.0.3.md)。

### 按需更新

- 详情和设置继续在打开时创建；详情最小化时暂停界面计时与内容更新，恢复时显示最新快照，关闭后释放动态卡片和计时器订阅。
- 系统外观对象复用；接收设置/主题变化通知，并以 15 秒校验补漏。默认一分钟的数据查询与这些界面检查相互独立。
- 数字条文字变化才重新排版，画刷颜色变化才更新；详情卡片保留控件，新增/减少卡片时才增删控件。
- 倒计时仍每秒检查边界；倒计时读条以 0.1 个百分点的变化精度更新，保留首次加载动画。
- 未使用定时强制 GC 或工作集裁剪。WinUI 与 .NET 仍随数字条常驻，关闭详情不会卸载整个框架。

## 目录

- src/CodexQuotaMonitor.Core：额度模型、选择规则、时间和交互逻辑。
- src/CodexQuotaMonitor.App：WinUI 窗口、原生任务栏及 Codex 接入。
- src/CodexQuotaMonitor.App/Assets：托盘图标与 SVG 源。
- scripts：构建、发布、图标生成和必要运行检查。
- tests/CodexQuotaMonitor.Core.Tests：核心回归测试。
- docs：验证与发布说明。

## 已知限制

- 首版面向单显示器 Windows 11 x64；不同任务栏布局、缩放和替代任务栏软件仍可能影响贴合位置。
- Windows 禁用高级视觉效果或启用高对比模式时使用回退外观。
- 单文件版并非零释放程序；依赖缓存和用户配置是正常运行所需。
- 当前发行包未进行代码签名。

透明窗口处理参考 WinUIEx；原始 MIT 许可见 [第三方声明](THIRD-PARTY-NOTICES.md)，并内置于单文件发行包中。
