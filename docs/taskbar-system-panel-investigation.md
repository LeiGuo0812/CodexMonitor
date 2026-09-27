# 任务栏系统面板遮挡调查（2026-09-27）

## 显示桌面按钮的短暂遮挡

2026-09-27，用户报告点击任务栏最右侧“显示桌面”后数字条短暂消失。对 v2.0.1 的只读采样确认：数字条保持 `IsWindowVisible=true`、`IsIconic=false`、`DWMWA_CLOAKED=0`，位置和 owner 均未改变；中心命中由数字条变为 `Shell_TrayWnd`。两次进入桌面的遮挡分别约 46 毫秒和 666 毫秒，一次恢复窗口的遮挡约 14 毫秒。问题发生在前景转为 `Progman` 或恢复普通应用之后的任务栏重排，并非最小化或 Peek 淡出。

此前短时检查仅由 Explorer 的 `ForegroundStaging` / `XamlExplorerHostIslandWindow` 触发，遗漏了显示桌面与恢复窗口。修复将同一任务栏进程的任务栏本身、`Progman`、`WorkerW` 前景事件纳入现有 350 毫秒过渡检查。任务栏前景事件可在恢复到普通应用前启动检查。沿用原有实际遮挡判断、2 毫秒等待定时器及过渡后取消机制，不增加常驻高频轮询，不修改窗口所有权、坐标、虚拟桌面隐藏和全屏隐藏逻辑。


本机复测中，连续三次“显示桌面 → 恢复窗口”均未采样到任务栏覆盖数字条；用户同时检查任务视图与音量面板后反馈“没有异常了”。此结果限于本机实际交互，不等同于所有 Windows 版本每一合成帧的保证。

## 原生预览版 3：任务视图与同类面板

任务视图复现时，数字条 HWND 未改变、`IsWindowVisible=true`、`DWMWA_CLOAKED=0`，坐标保持 `(2954,2081)-(3166,2155)`。数字条和任务栏均处于 band 6，但中心命中变为 `Shell_TrayWnd`，前景窗口为 Explorer 的 `XamlExplorerHostIslandWindow`。这属于同层重排遮挡，与窗口被销毁或跨虚拟桌面隐藏不同。退出任务视图后的普通层同样可能保留错误顺序；一次针对性的 `SetWindowPos(HWND_TOPMOST)` 后命中立即恢复数字条。

新逻辑在系统窗口事件和低频驻留检查中判断实际命中：只有数字条已经吸附、未主动隐藏、未被 DWM cloak，且遮挡者为任务栏本身或其子窗口时，才恢复数字条的层级。操作保留位置、尺寸、焦点和任务栏自身顺序。可见性恢复直接在相关 WinEvent 回调执行，包含 Shell 容器的 OBJID_CLIENT 重排；布局消息合并，仅失败重试有 500 毫秒退避；正常可见时不重复置顶，其他应用或直接覆盖该位置的弹出面板不会触发抢占。

系统进程名改为大小写无关识别，本机实际 `Explorer.EXE` 不再漏认，同时覆盖开始菜单、搜索、Shell 和系统输入宿主。原有“系统暂时拆分 owner 期间延后解除”的拖动修复继续保留。系统窗口层级与隐藏的定义参见 [Window Features](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features) 和 [DWMWA_CLOAKED](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute)。

自动检查仍不能代替各面板的实际交互复测；虚拟桌面切换与其他 Windows 构建版本属于单独的验证范围。
### 切换瞬间的闪动

最初的恢复逻辑消除了持续遮挡；用户确认开始、搜索、通知／日历和音量正常，但任务视图切换仍短暂闪动。去掉消息排队和成功恢复间隔后仍复现。诊断显示，任务栏盖住文字前没有向本程序发送可用于拦截的 `WM_WINDOWPOSCHANGING`；遮挡出现在 `ForegroundStaging` 与后续任务视图前景事件之间。普通短时 Win32 定时器也仍记录到约 15～30 毫秒的遮挡，用户确认未解决闪动。

当前实现只在任务栏所属进程的 `ForegroundStaging` / `XamlExplorerHostIslandWindow` 成为前景时，启动 350 毫秒的过渡监测。使用按需创建的高精度等待定时器，请求间隔为 2 毫秒，并在同一 UI 线程中与消息一起等待；系统调度不保证严格的 2 毫秒延迟。过渡期间延后需要同步调用 Explorer 的布局查询。结束、拖出或主动隐藏后取消定时器，不增加工作线程，也不修改系统全局计时精度。若等待定时器不可用则退回短时 Win32 定时器。正常可见时只读取状态，不执行置顶。

相关 API 参见 [CreateWaitableTimerExW](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw) 和 [MsgWaitForMultipleObjectsEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-msgwaitformultipleobjectsex)。最终视觉结果以本机交互复测为准，不能从编译结果推断零闪动。
最终人工复测：用户连续开关任务视图并检查音量后，确认“任务视图不再闪动，音量正常”。只读采样显示打开任务视图期间数字条保持原位置可见；关闭过渡中仍有一次约 15 毫秒的任务栏命中记录，用户未观察到闪动，因此不将此结论扩大为每个合成帧、所有 Windows 版本均绝无遮挡。平时恢复普通消息等待，短时监测结束后不再进行高频轮询。

本轮自动检查：构建成功，74 项核心检查通过。最终原生运行检查中两次真实查询均成功，刷新状态已结束，菜单／设置同步、详情对齐、绘制资源释放与数字条重建均通过，用户配置哈希保持不变。较早的一轮检查曾有第二次请求超时，程序正常结束刷新并保留已有数据；最终重跑已恢复成功。自动检查未模拟系统面板，视觉结论来自上面的用户交互反馈。
## 原生预览版 2：窗口所有权方案

先前尝试的是子窗口挂接（`SetParent` / `WS_CHILD`），此次保留 `WS_POPUP`，只使用 `SetWindowLongPtrW(GWLP_HWNDPARENT)` 设置任务栏为 owner。官方描述了 [Owned Windows 的层级关系](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#owned-windows)；特定 Windows 版本中系统 band 联动行为仍以实机观察为准，不假定跨版本保证。

本机临时实验的 60 秒记录：

- 正常阶段：widget band 1、taskbar band 1，owner 为任务栏。
- 面板打开阶段（采样第 28–55 秒）：widget band 6、taskbar band 6，owner 暂时报告为 0；数字条中心的命中窗口始终是 `CodexMonitor.Native.Widget`。
- 面板关闭后（第 56 秒起）：两者回到 band 1，owner 恢复。
- 整段数字条坐标保持 `(2954,2081)-(3166,2155)`。用户确认文字始终可见。

将方案接入原生程序后，用户再次确认两次打开／关闭均可见，随后反馈重复切换时发生短暂闪动。已去除每 250 毫秒重新置顶，避免在系统临时拆分所有权组时主动 show/raise，并按系统进程路径排除 Shell 过渡窗体被误判为全屏。消除闪动不能仅凭编译或静态坐标检查宣称完成，需要继续观察。

随后以约 50 毫秒间隔采样，确认拖动前开关面板时命中始终为数字条；面板仍处于 band 6 时拖出并重吸附后，数字条停留在 band 6，任务栏却正常在 band 1 / 6 间切换，再次开面板时命中变为 `Shell_TrayWnd`。用户反馈与该记录一致。原因是拖动期间立即清除 owner，破坏了 Windows 临时保存的所有权组。修复为：已建立关系且 owner 暂时报告为 0 时不更改关系，等系统恢复 owner 后再按当前悬浮／吸附模式处理；位置移动即时进行。

最终复测：用户按“打开音量面板 → 拖出 → 拖回吸附 → 重新打开音量面板”操作后，明确反馈“仍可见且不闪动”。100 秒采样记录中所有权在面板关闭后恢复，widget 与 taskbar 都回到 band 1；未再出现此前拖动后 widget 被留在 band 6 的情况。此结论限于本机、本次操作，不代替其他 Windows 构建版本和 Explorer 重启的验证。

吸附时建立 owner 关系，拖出时解除；保留任务栏内位置。后台控制 HWND 独立于任务栏，数字条被销毁时可重建；托盘与异步查询投递给控制 HWND。生命周期检查覆盖本程序数字条销毁／重建，未主动重启用户的 Explorer。

产品没有调用 `SetWindowBand`、`CreateWindowInBand`，没有注入 Explorer，也没有启用 UIAccess；`GetWindowBand` 仅用于本次忽略目录下的诊断脚本。正常程序不产生这些采样日志。

以下为旧架构的历史调查，结论仅适用于当时已经验证的原型。

## 发行状态

历史处理：v1.0.7 曾撤回为草稿，公开版回退至 v1.0.6。现发布原生 v2.0.0，并按用户要求删除旧 Release 与草稿；源码提交和标签历史保留。以下旧架构调查不代表当前原生版的显示结果。

## 本机观察

- 打开 Windows 音量／快速设置面板后，数字条的窗口仍可见、未被 DWM cloak，物理坐标保持 `(2959,2081)-(3166,2155)`。
- 任务栏坐标为 `(0,2076)-(3840,2160)`。诊断读取到任务栏显示 band 从 1 变为 6，数字条仍为 1。
- 数字条中心的 `WindowFromPoint` 返回 `Shell_TrayWnd`，该区域实际截图没有文字；这是任务栏遮挡，不是数字条主动隐藏或移动。
- 现有 `GetWindow(GW_HWNDPREV)` 遍历没有检测到这个跨 band 的遮挡。窗口仍 visible、坐标不变、普通 topmost 均不足以证明用户能看到文字。
- v1.0.7 使用直接子窗口查找保住了通知区边界，解决了这一阶段的坐标回退，但没有解决可见性。因此撤回其完整修复声明。

## 被否定的原型

1. 任务栏提升期间挂接原生窗口：`SetParent` 失败，错误 87；直接创建子窗口曾返回错误 5。首次用户看到的测试文字不能作为正确位置显示的证据。
2. 在普通任务栏状态下先挂接原生窗口：`GetParent` 与物理坐标核对成功；面板打开后仍没有文字，截图与用户观察一致。
3. 在普通状态下直接创建原生 `WS_CHILD` 窗口，定期恢复子窗口顶层位置：面板打开后仍没有文字，截图与用户观察一致。

原型没有写入产品代码。临时验证窗口已结束；诊断图像只截取本程序数字条区域，存于忽略目录 `.artifacts`。

## 结论与边界

本机实测排除了“改坐标”“普通置顶”“直接换成原生子窗口”作为完整修复。仅将语言改为 C++／Rust，也没有证据表明能绕过此显示层级行为。尚未验证出既保持现有便携部署、又能让独立数字条在系统面板打开时始终显示于任务栏内的实现。

UIAccess 不是本程序应采用的常规置顶补丁。Microsoft 要求其用于辅助技术，并要求签名与受保护安装位置，明确不应仅用于显示在系统 UI 上方：
[Security Considerations for Assistive Technologies](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-securityoverview)。

后续如采用固定上缘或打开面板时避让，须明确这是位置行为取舍，不得描述为“任务栏内始终可见”的修复。尚未发布替代修复版。
