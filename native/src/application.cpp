#include "application.h"
#include <windowsx.h>
#include <shellapi.h>
#include <dwmapi.h>
#include <uxtheme.h>
#include <psapi.h>
#include <fstream>
#include <sstream>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <iphlpapi.h>
#include <netioapi.h>
namespace cqm
{
static constexpr UINT TrayMessage = WM_APP + 1, QueryMessage = WM_APP + 2, ShellMessage = WM_APP + 3,
                      CleanupMessage = WM_APP + 4, ValidationMessage = WM_APP + 5,
                      NetworkMessage = WM_APP + 6;
static constexpr UINT TickTimer = 1, HoverTimer = 2, AnimationTimer = 3, ShellTransitionTimer = 4;
enum ControlId
{
    PositionControl = 200,
    // 201 was the legacy independent light/dark selector.
    ThemeControl = 202,
    OpacityControl,
    FontControl,
    HorizontalControl,
    VerticalControl,
    BackgroundControl,
    PrimaryControl,
    SecondaryControl,
    AccentControl,
    RefreshControl,
    PathControl
};
static constexpr int SliderOffset = 1000;
static bool sliderControl(int id)
{
    return id == OpacityControl || id == FontControl || id == HorizontalControl || id == VerticalControl ||
           id == RefreshControl;
}
static double sliderFactor(int id)
{
    return id == OpacityControl ? 100. : id == FontControl ? 10. : id == RefreshControl ? 2. : 1.;
}
static float scaleFor(HWND h)
{
    return std::max(96U, GetDpiForWindow(h)) / 96.f;
}
static std::wstring planLabel(const std::string &plan)
{
    if (plan.empty())
        return L"正在读取 Codex 账户";
    for (auto [key, label] : std::initializer_list<std::pair<const char *, const wchar_t *>>{
             {"pro", L"Pro"},
             {"plus", L"Plus"},
             {"prolite", L"Pro Lite"},
             {"team", L"Team"},
             {"business", L"Business"},
             {"self_serve_business_usage_based", L"Business"},
             {"enterprise", L"Enterprise"},
             {"enterprise_cbp_usage_based", L"Enterprise"},
             {"edu", L"Edu"},
             {"free", L"Free"}})
        if (plan == key)
            return L"ChatGPT " + std::wstring(label);
    return L"ChatGPT " + wide(plan);
}
static std::wstring restrictionLabel(const std::string &code)
{
    if (code == "rate_limit_reached")
        return L"当前额度窗口已达到使用限制。";
    if (code == "workspace_owner_credits_depleted")
        return L"工作区所有者额度已耗尽。";
    if (code == "workspace_member_credits_depleted")
        return L"工作区成员额度已耗尽。";
    if (code == "workspace_owner_usage_limit_reached")
        return L"工作区所有者使用上限已达到。";
    if (code == "workspace_member_usage_limit_reached")
        return L"工作区成员使用上限已达到。";
    return L"Codex 服务端报告当前账户受限。";
}
static D2D1_RECT_F box(float x, float y, float w, float h)
{
    return {x, y, x + w, y + h};
}
static bool contains(D2D1_RECT_F r, float x, float y)
{
    return x >= r.left && x < r.right && y >= r.top && y < r.bottom;
}
static std::wstring textOf(HWND h)
{
    int n = GetWindowTextLengthW(h);
    std::wstring s(n + 1, 0);
    GetWindowTextW(h, s.data(), n + 1);
    s.resize(n);
    return s;
}
static int hitAt(const View &v, float x, float y)
{
    for (size_t i = 0; i < v.hits.size(); i++)
        if (contains(v.hits[i].rect, x, y))
            return (int)i;
    return -1;
}
Application *Application::active_ = nullptr;
Application::~Application()
{
    closing_ = true;
    stop_ = true;
    if (networkNotification_)
        CancelMibChangeNotify2(networkNotification_);
    if (foregroundHook_)
        UnhookWinEvent(foregroundHook_);
    if (shellHook_)
        UnhookWinEvent(shellHook_);
    if (queryThread_.joinable())
        queryThread_.join();
    if (cleanupThread_.joinable())
        cleanupThread_.join();
    if (validationThread_.joinable())
        validationThread_.join();
    removeTray();
    if (editor_.hwnd)
        DestroyWindow(editor_.hwnd);
    if (details_.hwnd)
        DestroyWindow(details_.hwnd);
    if (widget_)
        DestroyWindow(widget_);
    if (controller_)
        DestroyWindow(controller_);
    if (tooltip_)
        DestroyWindow(tooltip_);
    if (icon_)
        DestroyIcon(icon_);
    if (controlFont_)
        DeleteObject(controlFont_);
    if (editBrush_)
        DeleteObject(editBrush_);
    BufferedPaintUnInit();
    active_ = nullptr;
}
int Application::run()
{
    active_ = this;
    INITCOMMONCONTROLSEX init{sizeof(init), ICC_STANDARD_CLASSES | ICC_WIN95_CLASSES | ICC_BAR_CLASSES};
    InitCommonControlsEx(&init);
    BufferedPaintInit();
    saved_ = settings_ = loadSettings();
    saved_.startup = settings_.startup = startupEnabled();
    colors_ = palette(settings_);
    taskbarDark_ = systemDark();
    contrast_ = highContrast();
    animate_ = animationsEnabled();
    WNDCLASSEXW wc{sizeof(wc)};
    wc.style = CS_DBLCLKS;
    wc.hInstance = instance_;
    wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    wc.lpfnWndProc = widgetProc;
    wc.lpszClassName = L"CodexMonitor.Native.Widget";
    if (!RegisterClassExW(&wc))
        throw std::runtime_error("WINDOW_CLASS_FAILED");
    wc.lpszClassName = L"CodexMonitor.Native.Controller";
    if (!RegisterClassExW(&wc))
        throw std::runtime_error("CONTROLLER_CLASS_FAILED");
    controller_ =
        CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, L"CodexMonitor Controller",
                        WS_POPUP, 0, 0, 0, 0, nullptr, nullptr, instance_, this);
    if (!controller_)
        throw std::runtime_error("CONTROLLER_CREATE_FAILED");
    wc.lpfnWndProc = viewProc;
    wc.lpszClassName = L"CodexMonitor.Native.View";
    wc.hIcon = LoadIconW(instance_, MAKEINTRESOURCEW(101));
    RegisterClassExW(&wc);
    createWidget();
    activateEvent_.reset(CreateEventW(nullptr, FALSE, FALSE,
                                      verify_ ? L"Local\\CodexQuotaMonitor.NativeVerifyActivate"
                                              : L"Local\\CodexQuotaMonitor.Activate"));
    taskbarCreated_ = RegisterWindowMessageW(L"TaskbarCreated");
    icon_ = (HICON)LoadImageW(instance_, MAKEINTRESOURCEW(101), IMAGE_ICON,
                              GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForWindow(widget_)),
                              GetSystemMetricsForDpi(SM_CYSMICON, GetDpiForWindow(widget_)), LR_DEFAULTCOLOR);
    addTray();
    foregroundHook_ = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nullptr, shellEvent,
                                      0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    shellHook_ = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_REORDER, nullptr, shellEvent, 0, 0,
                                 WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    NotifyIpInterfaceChange(
        AF_UNSPEC,
        [](PVOID context, PMIB_IPINTERFACE_ROW, MIB_NOTIFICATION_TYPE type)
        {
            if (type != MibInitialNotification)
                PostMessageW((HWND)context, NetworkMessage, 0, 0);
        },
        controller_, FALSE, &networkNotification_);
    if (!verify_ && saved_.startup)
    {
        try
        {
            setStartup(true);
        }
        catch (...)
        {
            notify(L"开机启动路径未能更新，请在右键菜单重新设置。");
        }
    }
    place(true);
    drawWidget();
    SetTimer(controller_, TickTimer, 1000, nullptr);
    nextRefresh_ = now();
    if (verify_)
        verifyStarted_ = GetTickCount64();
    refresh();
    if (verify_)
    {
        openDetails();
    }
    MSG message{};
    for (;;)
    {
        if (shellTransitionTimer_ && shellTransitionUntil_)
        {
            HANDLE timer = shellTransitionTimer_.get();
            DWORD result = MsgWaitForMultipleObjectsEx(1, &timer, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
            if (result == WAIT_OBJECT_0)
                checkShellTransition();
            else if (result == WAIT_FAILED)
            {
                CancelWaitableTimer(timer);
                shellTransitionUntil_ = 0;
            }
            if (!PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                continue;
            if (message.message == WM_QUIT)
                break;
        }
        else if (GetMessageW(&message, nullptr, 0, 0) <= 0)
            break;
        if (editor_.hwnd && (message.hwnd == editor_.hwnd || IsChild(editor_.hwnd, message.hwnd)) &&
            IsDialogMessageW(editor_.hwnd, &message))
            continue;
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return (int)message.wParam;
}
void CALLBACK Application::shellEvent(HWINEVENTHOOK, DWORD event, HWND h, LONG object, LONG, DWORD, DWORD)
{
    if (!active_ || active_->closing_ || !h || !active_->controller_)
        return;
    // OUTOFCONTEXT callbacks run on our registering UI thread. Restore ordering
    // here rather than waiting for another posted message/compositor frame.
    DWORD eventProcess = 0, shellProcess = 0;
    GetWindowThreadProcessId(h, &eventProcess);
    if (active_->taskbarOwner_)
        GetWindowThreadProcessId(active_->taskbarOwner_, &shellProcess);
    bool taskbarProcess = shellProcess && eventProcess == shellProcess;
    // Shell containers also report child-order changes as OBJID_CLIENT.
    if (object != OBJID_WINDOW &&
        !(taskbarProcess && event == EVENT_OBJECT_REORDER && object == OBJID_CLIENT))
        return;
    if (event == EVENT_SYSTEM_FOREGROUND && taskbarProcess && active_->docked_ && !active_->drag_)
    {
        wchar_t name[128]{};
        GetClassNameW(h, name, (int)std::size(name));
        if (wcscmp(name, L"ForegroundStaging") == 0 || wcscmp(name, L"XamlExplorerHostIslandWindow") == 0)
        {
            // Task View changes the taskbar order between foreground events,
            // without sending WINDOWPOSCHANGING to our widget. Watch only this
            // short transition; visible text never triggers a raise.
            active_->beginShellTransition();
        }
    }
    if (event == EVENT_SYSTEM_FOREGROUND || taskbarProcess)
        active_->restoreTaskbarVisibility();
    if (!active_->shellEventPending_)
    {
        active_->shellEventPending_ = true;
        if (!PostMessageW(active_->controller_, ShellMessage, 0, 0))
            active_->shellEventPending_ = false;
    }
}
LRESULT CALLBACK Application::widgetProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    auto self = (Application *)GetWindowLongPtrW(h, GWLP_USERDATA);
    if (m == WM_NCCREATE)
    {
        self = (Application *)((CREATESTRUCTW *)l)->lpCreateParams;
        SetWindowLongPtrW(h, GWLP_USERDATA, (LONG_PTR)self);
    }
    if (!self)
        return DefWindowProcW(h, m, w, l);
    try
    {
        return self->widgetMessage(h, m, w, l);
    }
    catch (const std::exception &e)
    {
        writeDiagnostic(e.what());
        return 0;
    }
}
LRESULT CALLBACK Application::viewProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    auto self = (Application *)GetWindowLongPtrW(h, GWLP_USERDATA);
    if (m == WM_NCCREATE)
    {
        auto cs = (CREATESTRUCTW *)l;
        self = (Application *)cs->lpCreateParams;
        SetWindowLongPtrW(h, GWLP_USERDATA, (LONG_PTR)self);
        if (cs->dwExStyle & WS_EX_CONTROLPARENT)
            SetPropW(h, L"CQM.Settings", (HANDLE)1);
    }
    if (!self)
        return DefWindowProcW(h, m, w, l);
    // Extended styles can be cleared while destroying a window. Preserve its identity
    // through WM_NCDESTROY, and never resurrect a closed HWND on a later callback.
    bool editor = GetPropW(h, L"CQM.Settings") != nullptr;
    auto &v = editor ? self->editor_ : self->details_;
    if (m == WM_NCCREATE)
        v.hwnd = h;
    LRESULT result = 0;
    try
    {
        result = self->viewMessage(v, editor, h, m, w, l);
    }
    catch (const std::exception &e)
    {
        writeDiagnostic(e.what());
        result = DefWindowProcW(h, m, w, l);
    }
    if (m == WM_NCDESTROY)
    {
        RemovePropW(h, L"CQM.Settings");
        SetWindowLongPtrW(h, GWLP_USERDATA, 0);
    }
    return result;
}
LRESULT Application::widgetMessage(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == taskbarCreated_ && taskbarCreated_)
    {
        trayAdded_ = false;
        addTray();
        place(true);
        return 0;
    }
    switch (m)
    {
    case WM_MOUSEACTIVATE:
        return MA_NOACTIVATE;
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT:
    {
        PAINTSTRUCT ps{};
        BeginPaint(h, &ps);
        drawWidget();
        EndPaint(h, &ps);
        return 0;
    }
    case WM_TIMER:
        if (w == TickTimer)
            tick();
        else if (w == ShellTransitionTimer)
            checkShellTransition();
        else if (w == HoverTimer)
        {
            float target = hover_ ? 1.f : 0.f;
            hoverAmount_ += std::clamp(target - hoverAmount_, -.14f, .14f);
            drawWidget();
            if (abs(hoverAmount_ - target) < .01f)
                KillTimer(h, HoverTimer);
        }
        return 0;
    case WM_SETTINGCHANGE:
    case WM_THEMECHANGED:
    case WM_DWMCOMPOSITIONCHANGED:
        appearance();
        place(true);
        return 0;
    case WM_DISPLAYCHANGE:
    case WM_DPICHANGED:
        widgetCanvas_.reset();
        place(true);
        drawWidget();
        return 0;
    case WM_POWERBROADCAST:
        if (w == PBT_APMRESUMEAUTOMATIC || w == PBT_APMRESUMESUSPEND)
        {
            place(true);
            refresh();
        }
        return TRUE;
    case WM_TIMECHANGE:
        drawWidget();
        updateTooltip();
        redrawViews();
        refresh();
        return 0;
    case NetworkMessage:
        // Interface parameter notifications can be noisy (VPNs and virtual adapters).
        // Do not turn every notification into a back-to-back quota request.
        if (!state_.refreshing && now() - lastNetworkRefresh_ >= 30 &&
            (!queryStarted_ || GetTickCount64() - queryStarted_ >= 30000))
        {
            lastNetworkRefresh_ = now();
            refresh();
        }
        return 0;
    case ShellMessage:
        shellEventPending_ = false;
        if (!drag_)
            place();
        return 0;
    case QueryMessage:
        receiveQuery();
        return 0;
    case CleanupMessage:
    {
        if (cleanupThread_.joinable())
            cleanupThread_.join();
        std::optional<CleanupResult> result;
        {
            std::lock_guard lock(resultMutex_);
            result = std::exchange(cleanupResult_, {});
        }
        cacheBusy_ = false;
        notify(result && result->failed
                   ? L"可清理的缓存已移除；部分旧缓存被占用或无法访问。退出旧版后可再次清理。"
                   : L"缓存已清理。配置与 Codex 登录信息已保留。");
        return 0;
    }
    case ValidationMessage:
    {
        if (validationThread_.joinable())
            validationThread_.join();
        bool ok = false;
        {
            std::lock_guard lock(resultMutex_);
            ok = validationResult_.value_or(false);
            validationResult_.reset();
        }
        saving_ = false;
        if (editor_.hwnd)
        {
            if (ok)
            {
                commitSettings(pendingSave_);
                closeView(editor_, false);
                refresh();
            }
            else
            {
                editorError_ = L"路径未通过 codex --version 验证，请更正或留空。";
                InvalidateRect(editor_.hwnd, nullptr, FALSE);
            }
        }
        return 0;
    }
    case TrayMessage:
    {
        UINT event = LOWORD(l);
        if (event == NIN_SELECT || event == NIN_KEYSELECT || event == WM_LBUTTONUP ||
            event == WM_LBUTTONDBLCLK)
            openDetails();
        else if (event == WM_CONTEXTMENU || event == WM_RBUTTONUP)
            menu();
        return 0;
    }
    case WM_CONTEXTMENU:
        menu();
        return 0;
    case WM_LBUTTONDOWN:
    case WM_LBUTTONDBLCLK:
    {
        GetCursorPos(&press_);
        cursor_ = press_;
        GetWindowRect(h, &dragBounds_);
        down_ = true;
        drag_ = false;
        SetCapture(h);
        return 0;
    }
    case WM_MOUSEMOVE:
    {
        if (!hover_)
        {
            hover_ = true;
            TRACKMOUSEEVENT t{sizeof(t), TME_LEAVE, h, 0};
            TrackMouseEvent(&t);
            if (animate_)
                SetTimer(h, HoverTimer, 16, nullptr);
            else
            {
                hoverAmount_ = 1;
                drawWidget();
            }
        }
        if (down_ && (w & MK_LBUTTON))
        {
            POINT p{};
            GetCursorPos(&p);
            float scale = scaleFor(h);
            if (!drag_ && (abs(p.x - press_.x) > 6 * scale || abs(p.y - press_.y) > 6 * scale))
                drag_ = true;
            if (drag_)
            {
                int dx = p.x - cursor_.x, dy = p.y - cursor_.y;
                OffsetRect(&dragBounds_, dx, dy);
                cursor_ = p;
                docked_ = false;
                attachTaskbar(nullptr);
                RECT bar{};
                GetWindowRect(FindWindowW(L"Shell_TrayWnd", nullptr), &bar);
                snapReady_ = shouldSnapToTaskbar(dragBounds_, bar, scale);
                SetWindowPos(h, HWND_TOPMOST, dragBounds_.left, dragBounds_.top, 0, 0,
                             SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                drawWidget();
            }
        }
        return 0;
    }
    case WM_MOUSELEAVE:
        hover_ = false;
        if (animate_)
            SetTimer(h, HoverTimer, 16, nullptr);
        else
        {
            hoverAmount_ = 0;
            drawWidget();
        }
        return 0;
    case WM_LBUTTONUP:
        if (down_)
        {
            bool wasDrag = drag_;
            down_ = drag_ = false;
            snapReady_ = false;
            ReleaseCapture();
            if (wasDrag)
            {
                float scale = scaleFor(h);
                HWND barWindow = FindWindowW(L"Shell_TrayWnd", nullptr);
                RECT bar{};
                GetWindowRect(barWindow, &bar);
                int width = dragBounds_.right - dragBounds_.left,
                    height = dragBounds_.bottom - dragBounds_.top;
                Settings changed = saved_;
                if (shouldSnapToTaskbar(dragBounds_, bar, scale))
                {
                    changed.position = 1;
                    changed.horizontal = -360;
                    auto p = placeWidget(changed, width, height, scale);
                    if (p.docked)
                        changed.horizontal =
                            std::clamp(-360 + (int)round((dragBounds_.left - p.rect.left) / scale), -4000, 0);
                    changed.vertical = 12;
                }
                else
                {
                    MONITORINFO mi{sizeof(mi)};
                    GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTOPRIMARY), &mi);
                    changed.position = 2;
                    changed.horizontal = (int)round((dragBounds_.left - (mi.rcWork.right - width)) / scale);
                    changed.vertical = (int)round((bar.top - dragBounds_.bottom) / scale);
                }
                changed.normalize();
                saved_ = changed;
                settings_.position = changed.position;
                settings_.horizontal = changed.horizontal;
                settings_.vertical = changed.vertical;
                if (!verify_)
                    saveSettings(saved_);
                fillControls();
                place(true);
                drawWidget();
            }
            else
                openDetails();
        }
        return 0;
    case WM_CAPTURECHANGED:
    case WM_CANCELMODE:
    {
        bool interrupted = down_ || drag_;
        down_ = drag_ = false;
        snapReady_ = false;
        if (interrupted)
        {
            place(true);
            drawWidget();
        }
        return 0;
    }
    case WM_CLOSE:
        command(Quit);
        return 0;
    case WM_DESTROY:
        if (h == widget_)
        {
            widget_ = nullptr;
            taskbarOwner_ = nullptr;
            // Owned windows may be destroyed when Explorer recreates the taskbar.
            // The independent controller keeps the tray, worker and timer alive.
            if (tooltip_ && IsWindow(tooltip_))
                DestroyWindow(tooltip_);
            tooltip_ = nullptr;
            widgetCanvas_.reset();
            hidden_ = down_ = drag_ = hover_ = false;
        }
        if (h == controller_)
        {
            controller_ = nullptr;
            if (!closing_)
                PostQuitMessage(0);
        }
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}
void Application::tick()
{
    if (closing_)
        return;
    if ((!widget_ || !IsWindow(widget_)) && FindWindowW(L"Shell_TrayWnd", nullptr))
    {
        widget_ = nullptr;
        if (tooltip_ && IsWindow(tooltip_))
            DestroyWindow(tooltip_);
        tooltip_ = nullptr;
        widgetCanvas_.reset();
        taskbarOwner_ = nullptr;
        hidden_ = down_ = drag_ = hover_ = false;
        createWidget();
    }
    bool queryReady = false;
    {
        std::lock_guard lock(resultMutex_);
        queryReady = queryResult_.has_value();
    }
    if (queryReady)
        receiveQuery(); // Also recover completion if a posted message was lost/consumed.
    if (activateEvent_ && WaitForSingleObject(activateEvent_.get(), 0) == WAIT_OBJECT_0)
        openDetails();
    if (!drag_)
        place();
    if (now() >= nextRefresh_ && !state_.refreshing)
        refresh();
    std::wstring signature = (state_.selected ? state_.selected->label() : L"剩余 --") +
                             (state_.selected ? countdown(state_.selected->reset) : L"重置时间未知") +
                             wide(state_.error) + (state_.stale ? L"stale" : L"");
    if (signature != widgetSignature_)
    {
        widgetSignature_ = signature;
        drawWidget();
        updateTooltip();
    }
    if (details_.hwnd && !IsIconic(details_.hwnd))
    {
        std::wstring clock =
            signature + std::to_wstring((now() - state_.updated) / 60) +
            (state_.refreshing ? L"busy" + std::to_wstring((GetTickCount64() - queryStarted_) / 1000)
                               : L"idle");
        for (auto &q : state_.data.windows)
        {
            clock += countdown(q.reset) + std::to_wstring((int)(q.timePercent().value_or(-1) * 10));
        }
        for (auto &c : state_.data.credits)
            clock += c.validity();
        if (clock != detailClock_)
        {
            detailClock_ = std::move(clock);
            InvalidateRect(details_.hwnd, nullptr, FALSE);
        }
    }
    if (verify_)
    {
        auto elapsed = GetTickCount64() - verifyStarted_;
        auto sample = [&](const char *name)
        {
            PROCESS_MEMORY_COUNTERS_EX m{};
            m.cb = sizeof(m);
            GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS *)&m, sizeof(m));
            verification_[name] = {{"privateBytes", m.PrivateUsage},
                                   {"workingSetBytes", m.WorkingSetSize},
                                   {"gdiObjects", GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS)},
                                   {"userObjects", GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS)}};
        };
        if (elapsed > 10000 && verifyStep_ == 0)
        {
            sample("detailsOpen");
            openSettings();
            SetWindowPos(editor_.hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            verifyStep_++;
        }
        if (elapsed > 22000 && verifyStep_ == 1)
        {
            ++verifyStep_;
            sample("settingsOpen");
            bool synchronized = true;
            auto original = settings_, originalSaved = saved_;
            for (int i = 0; i < 72; i++)
            {
                settings_.preset = i % 5;
                settings_.position = i % 3;
                fillControls();
                appearance();
                synchronized &=
                    SendMessageW(controls_[ThemeControl], CB_GETCURSEL, 0, 0) == settings_.preset &&
                    SendMessageW(controls_[PositionControl], CB_GETCURSEL, 0, 0) == settings_.position;
            }
            bool menuSync = true;
            for (int position = 0; position < 3; position++)
            {
                command(PositionAuto + position);
                menuSync &= SendMessageW(controls_[PositionControl], CB_GETCURSEL, 0, 0) == position;
                HMENU popup = buildMenu();
                auto submenu = GetSubMenu(popup, 3);
                for (int i = 0; i < 3; i++)
                    menuSync &= ((GetMenuState(submenu, PositionAuto + i, MF_BYCOMMAND) & MF_CHECKED) != 0) ==
                                (i == position);
                DestroyMenu(popup);
            }
            SendMessageW(controls_[PositionControl], CB_SETCURSEL, 1, 0);
            readControls(false);
            HMENU popup = buildMenu();
            menuSync &= (GetMenuState(GetSubMenu(popup, 3), PositionTaskbar, MF_BYCOMMAND) & MF_CHECKED) != 0;
            DestroyMenu(popup);
            saved_ = originalSaved;
            settings_ = original;
            verification_["themeIterations"] = 72;
            bool sliders = true;
            for (int id : {OpacityControl, FontControl, HorizontalControl, VerticalControl, RefreshControl})
            {
                auto slider = controls_[id + SliderOffset];
                int originalPosition = (int)SendMessageW(slider, TBM_GETPOS, 0, 0);
                int minimum = (int)SendMessageW(slider, TBM_GETRANGEMIN, 0, 0);
                SendMessageW(slider, TBM_SETPOS, TRUE, minimum);
                SendMessageW(editor_.hwnd, WM_HSCROLL, TB_THUMBTRACK, (LPARAM)slider);
                sliders &=
                    abs(wcstod(textOf(controls_[id]).c_str(), nullptr) - minimum / sliderFactor(id)) < .001;
                SendMessageW(slider, TBM_SETPOS, TRUE, originalPosition);
                SendMessageW(editor_.hwnd, WM_HSCROLL, TB_ENDTRACK, (LPARAM)slider);
            }
            settings_ = original;
            fillControls();
            appearance();
            verification_["slidersSynchronized"] = sliders;
            drawDetails();
            bool labelsFit = true;
            for (auto &hit : details_.hits)
                if (hit.action == SettingsPage || hit.action == ToggleUtc || hit.action == Refresh)
                    labelsFit &=
                        drawing_.measure(hit.label, 11, true).width <= hit.rect.right - hit.rect.left - 16;
            verification_["detailButtonLabelsFit"] = labelsFit;
            HWND controller = controller_;
            DestroyWindow(widget_);
            createWidget();
            place(true);
            drawWidget();
            verification_["widgetRecovery"] = widget_ && IsWindow(widget_) && controller_ == controller &&
                                              IsWindow(controller_) && trayAdded_;
            verification_["controlsSynchronized"] = synchronized;
            verification_["menuSynchronized"] = menuSync;
            HWND existing = details_.hwnd;
            widgetMessage(widget_, WM_LBUTTONDOWN, MK_LBUTTON, 0);
            widgetMessage(widget_, WM_LBUTTONUP, 0, 0);
            widgetMessage(widget_, WM_LBUTTONDBLCLK, MK_LBUTTON, 0);
            widgetMessage(widget_, WM_LBUTTONUP, 0, 0);
            widgetMessage(widget_, TrayMessage, 0, WM_LBUTTONUP);
            widgetMessage(widget_, TrayMessage, 0, WM_LBUTTONDBLCLK);
            verification_["clickHandlersReuseDetails"] =
                details_.hwnd == existing && IsWindowVisible(existing) != FALSE;
            RECT detailRect{}, barRect{};
            GetWindowRect(details_.hwnd, &detailRect);
            GetWindowRect(FindWindowW(L"Shell_TrayWnd", nullptr), &barRect);
            verification_["detailsBottomGapPixels"] = barRect.top - detailRect.bottom;
            closeView(editor_, true);
            closeView(details_, false);
            verification_["viewsReleased"] =
                !editor_.hwnd && !details_.hwnd && !editor_.canvas && !details_.canvas;
        }
        if (elapsed > 30000 && verifyStep_ == 2)
        {
            sample("closedViews");
            verifyStep_++;
        }
    }
    if (verify_ && GetTickCount64() - verifyStarted_ > 35000 && !state_.refreshing)
    {
        finishVerification();
        command(Quit);
    }
}
void Application::refresh()
{
    if (state_.refreshing || closing_)
        return;
    if (queryThread_.joinable())
        queryThread_.join();
    state_.refreshing = true;
    queryStarted_ = GetTickCount64();
    if (verify_)
        verification_["queryStarts"].push_back(queryStarted_ - verifyStarted_);
    redrawViews();
    auto path = saved_.codex;
    queryThread_ = std::thread(
        [this, path, target = controller_]
        {
            auto result = queryCodex(path, stop_);
            {
                std::lock_guard lock(resultMutex_);
                queryResult_ = std::move(result);
            }
            PostMessageW(target, QueryMessage, 0, 0);
        });
}
void Application::receiveQuery()
{
    std::optional<Query> q;
    {
        std::lock_guard lock(resultMutex_);
        q = std::exchange(queryResult_, {});
    }
    if (!q)
        return;
    if (queryThread_.joinable())
        queryThread_.join();
    if (verify_)
        verification_["queryCompletions"].push_back({{"elapsedMs", GetTickCount64() - queryStarted_},
                                                     {"success", q->success},
                                                     {"error", q->error},
                                                     {"creditDetails", q->creditDetails}});
    failures_ = q->success ? 0 : std::min(failures_ + 1, 5);
    state_.apply(std::move(*q));
    nextRefresh_ = now() + std::min(std::max(saved_.refresh, 900),
                                    saved_.refresh * (1 << std::clamp(failures_ - 1, 0, 4)));
    place(true);
    drawWidget();
    updateTooltip();
    if (details_.hwnd && !details_.initialFit && state_.updated)
    {
        fitDetails();
        details_.initialFit = true;
        if (animate_)
        {
            details_.opened = GetTickCount64();
            details_.animation = 0;
            SetTimer(details_.hwnd, AnimationTimer, 16, nullptr);
        }
    }
    redrawViews();
}
void Application::appearance()
{
    colors_ = palette(settings_);
    taskbarDark_ = systemDark();
    contrast_ = highContrast();
    animate_ = animationsEnabled();
    if (editBrush_)
        DeleteObject(editBrush_);
    auto c = blend(colors_.background, colors_.primary, .05f);
    editBrush_ = CreateSolidBrush(RGB((BYTE)(c.r * 255), (BYTE)(c.g * 255), (BYTE)(c.b * 255)));
    for (auto view : {&details_, &editor_})
        if (view->hwnd)
            colors_.glass = applyGlass(view->hwnd, colors_.dark, colors_.glass);
    drawWidget();
    redrawViews();
}
void Application::createWidget()
{
    widget_ = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                              L"CodexMonitor.Native.Widget", L"Codex 额度监控", WS_POPUP, 0, 0, 200, 60,
                              nullptr, nullptr, instance_, this);
    if (!widget_)
        throw std::runtime_error("WIDGET_CREATE_FAILED");
    taskbarOwner_ = nullptr;
    tooltip_ = CreateWindowExW(WS_EX_TOPMOST, TOOLTIPS_CLASSW, nullptr,
                               WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX, CW_USEDEFAULT, CW_USEDEFAULT,
                               CW_USEDEFAULT, CW_USEDEFAULT, widget_, nullptr, instance_, nullptr);
    SendMessageW(tooltip_, TTM_SETMAXTIPWIDTH, 0, 450);
    TOOLINFOW ti{sizeof(ti)};
    ti.uFlags = TTF_IDISHWND | TTF_SUBCLASS;
    ti.hwnd = widget_;
    ti.uId = (UINT_PTR)widget_;
    ti.lpszText = (LPWSTR)L"Codex 额度监控";
    SendMessageW(tooltip_, TTM_ADDTOOLW, 0, (LPARAM)&ti);
    updateTooltip();
}
void Application::attachTaskbar(HWND owner)
{
    if (!widget_ || taskbarOwner_ == owner)
        return;
    // The shell temporarily reports GW_OWNER == null while promoting its group.
    // Removing ownership in that interval breaks the shell's saved relationship
    // and strands this window in the elevated layer after the panel closes.
    // Defer detaching until Windows restores the owner; dragging itself is free.
    if (taskbarOwner_ && IsWindow(taskbarOwner_) && !GetWindow(widget_, GW_OWNER))
        return;
    SetLastError(ERROR_SUCCESS);
    auto previous = SetWindowLongPtrW(widget_, GWLP_HWNDPARENT, (LONG_PTR)owner);
    if (previous || GetLastError() == ERROR_SUCCESS)
        taskbarOwner_ = owner;
    // Windows may temporarily report no owner while moving an ownership group
    // between shell layers. Do not repeatedly reattach during that transition.
}
void Application::restoreTaskbarVisibility()
{
    if (repairingOcclusion_ || !docked_ || !taskbarOwner_ || !IsWindow(taskbarOwner_) || hidden_ || drag_ ||
        !IsWindowVisible(widget_))
        return;
    DWORD cloaked = 0;
    if (SUCCEEDED(DwmGetWindowAttribute(widget_, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked)
        return;
    RECT r{};
    if (!GetWindowRect(widget_, &r))
        return;
    HWND hit = WindowFromPoint({(r.left + r.right) / 2, (r.top + r.bottom) / 2});
    // Task View can reorder the promoted group without hiding or moving us.
    // Recover only a confirmed taskbar occlusion, never over another application's
    // window, a flyout overlapping this area, or an inactive virtual desktop.
    if (hit != taskbarOwner_ && (!hit || GetAncestor(hit, GA_ROOT) != taskbarOwner_))
        return;
    auto current = GetTickCount64();
    if (current < occlusionRetryAfter_)
        return;
    repairingOcclusion_ = true;
    if (!SetWindowPos(widget_, HWND_TOPMOST, 0, 0, 0, 0,
                      SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE | SWP_NOOWNERZORDER))
        occlusionRetryAfter_ = current + 500;
    repairingOcclusion_ = false;
}
void Application::beginShellTransition()
{
    shellTransitionUntil_ = GetTickCount64() + 350;
    if (!shellTransitionTimer_)
        shellTransitionTimer_.reset(CreateWaitableTimerExW(
            nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_MODIFY_STATE | SYNCHRONIZE));
    LARGE_INTEGER due{};
    due.QuadPart = -20000; // Two milliseconds; active only during the shell transition.
    if (shellTransitionTimer_ &&
        SetWaitableTimer(shellTransitionTimer_.get(), &due, 2, nullptr, nullptr, FALSE))
        KillTimer(controller_, ShellTransitionTimer);
    else if (!SetTimer(controller_, ShellTransitionTimer, USER_TIMER_MINIMUM, nullptr))
        shellTransitionUntil_ = 0;
}
void Application::checkShellTransition()
{
    restoreTaskbarVisibility();
    if (GetTickCount64() < shellTransitionUntil_ && docked_ && !drag_ && !hidden_ && !closing_)
        return;
    if (shellTransitionTimer_)
        CancelWaitableTimer(shellTransitionTimer_.get());
    KillTimer(controller_, ShellTransitionTimer);
    shellTransitionUntil_ = 0;
    if (!shellEventPending_ && !closing_)
        shellEventPending_ = PostMessageW(controller_, ShellMessage, 0, 0) != FALSE;
}
void Application::place(bool immediate)
{
    if (!widget_ || drag_)
        return;
    // Appbar layout queries synchronously call Explorer. During its animation
    // those calls can delay visibility recovery by several compositor frames.
    if (!immediate && shellTransitionUntil_ && GetTickCount64() < shellTransitionUntil_ && docked_ &&
        !hidden_)
    {
        restoreTaskbarVisibility();
        return;
    }
    float scale = scaleFor(widget_);
    float font = (float)settings_.font;
    std::wstring main = state_.selected ? state_.selected->label() : L"剩余 --",
                 reset = L"重置 " + countdown(state_.selected ? state_.selected->reset : std::nullopt);
    auto measureKey = main + L"\n" + reset + std::to_wstring(font);
    if (measureKey != measuredText_)
    {
        measuredText_ = measureKey;
        auto first = drawing_.measure(main, font, true),
             second = drawing_.measure(reset, std::max(10.f, font - 4));
        measuredWidth_ = (int)ceil(std::clamp(std::max(first.width + 35, second.width) + 22, 118.f, 260.f));
        measuredHeight_ = (int)ceil(std::clamp(first.height + second.height + 6, 42.f, 64.f));
    }
    int width = (int)ceil(measuredWidth_ * scale), height = (int)ceil(measuredHeight_ * scale);
    auto p = placeWidget(settings_, width, height, scale);
    RECT current{};
    GetWindowRect(widget_, &current);
    if (fullscreenForeground(widget_))
    {
        if (!hidden_)
        {
            ShowWindow(widget_, SW_HIDE);
            hidden_ = true;
        }
        return;
    }
    bool moved = memcmp(&current, &p.rect, sizeof(RECT)) != 0;
    if (moved && !immediate && !hidden_)
    {
        if (memcmp(&candidate_, &p.rect, sizeof(RECT)) == 0)
            candidateHits_++;
        else
        {
            candidate_ = p.rect;
            candidateHits_ = 1;
        }
        if (candidateHits_ < 2)
            return;
    }
    bool styleChanged = docked_ != p.docked;
    docked_ = p.docked;
    attachTaskbar(docked_ ? FindWindowW(L"Shell_TrayWnd", nullptr) : nullptr);
    restoreTaskbarVisibility();
    // During a shell-layer transition Windows temporarily detaches the owner
    // group. Its compositor controls visibility until the transition finishes.
    // Keep existing geometry/visibility during the animation. The targeted check
    // above may repair ordering only when the taskbar actually covers our text.
    if (docked_ && taskbarOwner_ && IsWindow(taskbarOwner_) && !GetWindow(widget_, GW_OWNER) && !hidden_ &&
        !immediate)
        return;
    if (moved || hidden_ || !IsWindowVisible(widget_))
    {
        SetWindowPos(widget_, HWND_TOPMOST, p.rect.left, p.rect.top, width, height,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);
        hidden_ = false;
        drawWidget();
    }
    else if (styleChanged)
        drawWidget();
    else if (!taskbarOwner_)
    {
        HWND above = GetWindow(widget_, GW_HWNDPREV), bar = FindWindowW(L"Shell_TrayWnd", nullptr);
        for (int i = 0; above && i < 512; i++, above = GetWindow(above, GW_HWNDPREV))
            if (above == bar)
            {
                SetWindowPos(widget_, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE);
                break;
            }
    }
}
void Application::drawWidget()
{
    if (!widget_ || hidden_)
        return;
    float scale = scaleFor(widget_);
    RECT rect{};
    GetClientRect(widget_, &rect);
    float w = rect.right / scale, h = rect.bottom / scale;
    if (!widgetCanvas_.begin(widget_, true, scale, Color{0, 0, 0, 1.f / 255}))
        return;
    Color primary = taskbarDark_ ? color(L"#F3F3F3") : color(L"#1B1B1B"),
          secondary = taskbarDark_ ? color(L"#CBCBCB") : color(L"#525252");
    if (contrast_)
    {
        primary = colors_.primary;
        secondary = colors_.secondary;
    }
    if (!docked_)
        widgetCanvas_.rect(box(0, 0, w, h), taskbarDark_ ? color(L"#262626", .94f) : color(L"#F0F0F0", .95f),
                           12);
    if (snapReady_)
        widgetCanvas_.border(box(1, 1, w - 2, h - 2), color(taskbarDark_ ? L"#91C5FF" : L"#286DB7"), 11,
                             1.5f);
    if (hoverAmount_ > 0)
        widgetCanvas_.rect(box(1, 1, w - 2, h - 2),
                           color(L"#FFFFFF", hoverAmount_ * (taskbarDark_ ? .19f : .37f)), 6);
    if (docked_)
    {
        for (float cx : {3.5f, w - 3.5f})
        {
            widgetCanvas_.line(cx, 7, cx, h - 7, alpha(primary, .25f * (1 - hoverAmount_)));
            if (hoverAmount_ > 0)
            {
                auto glow = taskbarDark_ ? color(L"#DFEDFF") : color(L"#FFFFFF");
                widgetCanvas_.spindle(cx, 5, h - 10, 7, alpha(glow, .078f * hoverAmount_));
                widgetCanvas_.spindle(cx, 5, h - 10, 4, alpha(glow, .125f * hoverAmount_));
                widgetCanvas_.spindle(cx, 5, h - 10, 2, alpha(glow, .549f * hoverAmount_));
            }
        }
    }
    auto q = state_.selected;
    std::wstring main = q ? q->label() : L"剩余 --";
    float font = (float)settings_.font;
    float line = drawing_.measure(main, font, true).height;
    float y = (h - line - drawing_.measure(L"重置", std::max(10.f, font - 4)).height) / 2;
    widgetCanvas_.text(main, box(11, y, w - 22, line), font, primary, true);
    float x = 11 + drawing_.measure(main, font, true).width + 7;
    std::wstring tag = q ? (q->kind == Kind::Week       ? L"周"
                            : q->kind == Kind::FiveHour ? L"5h"
                                                        : L"其他")
                         : L"";
    auto tagColor = contrast_ ? primary : color(taskbarDark_ ? L"#91C5FF" : L"#286DB7");
    widgetCanvas_.text(tag, box(x, y + 4, 24, 18), 10, tagColor, true);
    x += drawing_.measure(tag, 10, true).width + 7;
    // The taskbar marker is decorative and stable; query state lives in details.
    widgetCanvas_.text(L"●", box(x, y + 5, 12, 16), 8, tagColor, true);
    widgetCanvas_.text(L"重置 " + countdown(q ? q->reset : std::nullopt),
                       box(11, y + line, w - 22, h - y - line), std::max(10.f, font - 4), secondary);
    widgetCanvas_.end();
}
void Application::updateTooltip()
{
    auto q = state_.selected;
    tooltipText_ = (q ? q->name : L"额度未知") + L" · 自然重置：" + fullDate(q ? q->reset : std::nullopt) +
                   L"\n更新时间：" +
                   fullDate(state_.updated ? std::optional<Seconds>(state_.updated) : std::nullopt) + L"\n" +
                   (state_.missingFive ? L"5 小时字段暂缺，保留上次数据" : errorLabel(state_.error));
    TOOLINFOW t{sizeof(t)};
    t.hwnd = widget_;
    t.uId = (UINT_PTR)widget_;
    t.lpszText = tooltipText_.data();
    SendMessageW(tooltip_, TTM_UPDATETIPTEXTW, 0, (LPARAM)&t);
    if (trayAdded_)
    {
        NOTIFYICONDATAW n{sizeof(n)};
        n.hWnd = controller_;
        n.uID = 1;
        n.uFlags = NIF_TIP;
        auto label = (q ? q->label() : L"剩余 --") + L" · " + countdown(q ? q->reset : std::nullopt);
        wcsncpy_s(n.szTip, label.c_str(), _TRUNCATE);
        Shell_NotifyIconW(NIM_MODIFY, &n);
    }
}
void Application::addTray()
{
    NOTIFYICONDATAW n{sizeof(n)};
    n.hWnd = controller_;
    n.uID = 1;
    n.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    n.uCallbackMessage = TrayMessage;
    n.hIcon = icon_;
    wcscpy_s(n.szTip, L"Codex 额度监控");
    trayAdded_ = Shell_NotifyIconW(NIM_ADD, &n) != FALSE;
    if (trayAdded_)
    {
        n.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &n);
    }
}
void Application::removeTray()
{
    if (trayAdded_)
    {
        NOTIFYICONDATAW n{sizeof(n)};
        n.hWnd = controller_;
        n.uID = 1;
        Shell_NotifyIconW(NIM_DELETE, &n);
        trayAdded_ = false;
    }
}
void Application::notify(const std::wstring &text)
{
    NOTIFYICONDATAW n{sizeof(n)};
    n.hWnd = controller_;
    n.uID = 1;
    n.uFlags = NIF_INFO;
    n.dwInfoFlags = NIIF_INFO;
    wcscpy_s(n.szInfoTitle, L"CodexMonitor");
    wcsncpy_s(n.szInfo, text.c_str(), _TRUNCATE);
    Shell_NotifyIconW(NIM_MODIFY, &n);
}
HMENU Application::buildMenu()
{
    HMENU menu = CreatePopupMenu(), position = CreatePopupMenu(), theme = CreatePopupMenu();
    for (int i = 0; i < 3; i++)
        AppendMenuW(position, MF_STRING | (settings_.position == i ? MF_CHECKED : 0), PositionAuto + i,
                    std::initializer_list<const wchar_t *>{L"自动", L"任务栏优先", L"固定上移"}.begin()[i]);
    const wchar_t *themes[] = {L"雾白", L"暖砂", L"石墨", L"午夜", L"自定义"};
    for (int i = 0; i < 5; i++)
        AppendMenuW(theme, MF_STRING | (settings_.preset == i ? MF_CHECKED : 0), ThemeMist + i, themes[i]);
    AppendMenuW(menu, MF_STRING, Details, L"额度详情");
    AppendMenuW(menu, MF_STRING | (state_.refreshing ? MF_GRAYED : 0), Refresh, L"刷新");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_POPUP, (UINT_PTR)position, L"位置模式");
    AppendMenuW(menu, MF_POPUP, (UINT_PTR)theme, L"主题预设");
    AppendMenuW(menu, MF_STRING, SettingsPage, L"外观与位置…");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING | (startupEnabled() ? MF_CHECKED : 0), Startup, L"开机启动");
    AppendMenuW(menu, MF_STRING | (cacheBusy_ ? MF_GRAYED : 0), ClearCache,
                cacheBusy_ ? L"清理中…" : L"清除缓存");
    AppendMenuW(menu, MF_SEPARATOR, 0, nullptr);
    AppendMenuW(menu, MF_STRING, Quit, L"退出");
    return menu;
}
void Application::menu()
{
    HMENU popup = buildMenu();
    POINT cursor{};
    GetCursorPos(&cursor);
    SetForegroundWindow(widget_);
    int selected =
        TrackPopupMenu(popup, TPM_RETURNCMD | TPM_RIGHTBUTTON, cursor.x, cursor.y, 0, widget_, nullptr);
    DestroyMenu(popup);
    PostMessageW(widget_, WM_NULL, 0, 0);
    if (selected)
        command(selected);
}
void Application::command(int id)
{
    if (id >= PositionAuto && id <= PositionAbove)
    {
        auto copy = saved_;
        copy.position = id - PositionAuto;
        if (!verify_)
            saveSettings(copy);
        saved_ = copy;
        settings_.position = copy.position;
        fillControls();
        place(true);
        drawWidget();
        return;
    }
    if (id >= ThemeMist && id <= ThemeCustom)
    {
        auto copy = saved_;
        copy.preset = id - ThemeMist;
        if (!verify_)
            saveSettings(copy);
        saved_ = copy;
        settings_.preset = copy.preset;
        fillControls();
        appearance();
        return;
    }
    switch (id)
    {
    case Details:
        openDetails();
        break;
    case Refresh:
        refresh();
        break;
    case SettingsPage:
        openSettings();
        break;
    case Startup:
        if (verify_)
            break;
        try
        {
            bool enabled = !startupEnabled();
            setStartup(enabled);
            saved_.startup = settings_.startup = enabled;
            saveSettings(saved_);
        }
        catch (...)
        {
            notify(L"开机启动设置失败，请检查设置目录与注册表访问权限。");
        }
        break;
    case ClearCache:
        if (verify_)
            break;
        if (!cacheBusy_)
        {
            if (cleanupThread_.joinable())
                cleanupThread_.join();
            cacheBusy_ = true;
            cleanupThread_ = std::thread(
                [this, target = controller_]
                {
                    CleanupResult r;
                    try
                    {
                        r = clearCaches();
                    }
                    catch (...)
                    {
                        r.failed++;
                    }
                    {
                        std::lock_guard lock(resultMutex_);
                        cleanupResult_ = r;
                    }
                    PostMessageW(target, CleanupMessage, 0, 0);
                });
        }
        break;
    case Quit:
        closing_ = true;
        stop_ = true;
        removeTray();
        if (widget_)
            DestroyWindow(widget_);
        PostQuitMessage(0);
        break;
    case Save:
        readControls(true);
        break;
    case Cancel:
        if (!saving_)
            closeView(editor_, true);
        break;
    case Defaults:
        if (!saving_)
        {
            settings_ = Settings{};
            settings_.startup = saved_.startup;
            fillControls();
            appearance();
            place(true);
        }
        break;
    case Browse:
    {
        auto path = chooseExecutable(editor_.hwnd);
        if (!path.empty())
            SetWindowTextW(controls_[PathControl], path.c_str());
        break;
    }
    case CopyFive:
    case CopyWeek:
    {
        auto q = state_.find(id == CopyFive ? Kind::FiveHour : Kind::Week);
        if (!q && id == CopyFive)
            q = state_.rememberedFive;
        copyText(details_.hwnd, fullDate(q ? q->reset : std::nullopt, details_.utc));
        break;
    }
    case ToggleUtc:
        details_.utc = !details_.utc;
        InvalidateRect(details_.hwnd, nullptr, FALSE);
        break;
    }
}
void Application::commitSettings(const Settings &s)
{
    Settings copy = s;
    copy.normalize();
    copy.startup = startupEnabled();
    if (!verify_)
        saveSettings(copy);
    saved_ = settings_ = copy;
    nextRefresh_ = now() + saved_.refresh;
    appearance();
    place(true);
}
void Application::openDetails()
{
    if (verify_)
        verification_["events"].push_back({{"action", "openDetails"},
                                           {"at", GetTickCount64() - verifyStarted_},
                                           {"existing", (uintptr_t)details_.hwnd}});
    if (details_.hwnd)
    {
        ShowWindow(details_.hwnd, SW_RESTORE);
        fitDetails();
        SetForegroundWindow(details_.hwnd);
        if (!queryStarted_ || GetTickCount64() - queryStarted_ >= 10000)
            refresh();
        return;
    }
    details_ = View{};
    details_.canvas = std::make_unique<Canvas>(drawing_);
    details_.scale = scaleFor(widget_);
    details_.opened = GetTickCount64();
    details_.animation = animate_ ? 0.f : 1.f;
    HWND h = CreateWindowExW(WS_EX_APPWINDOW, L"CodexMonitor.Native.View", L"Codex 额度监控",
                             WS_POPUP | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX, 0, 0,
                             (int)(380 * details_.scale), (int)(730 * details_.scale), nullptr, nullptr,
                             instance_, this);
    if (!h)
        throw std::runtime_error("DETAILS_CREATE_FAILED");
    details_.hwnd = h;
    colors_.glass = applyGlass(h, colors_.dark, colors_.glass);
    fitDetails();
    ShowWindow(h, SW_SHOWNORMAL);
    SetWindowPos(h, verify_ ? HWND_TOPMOST : nullptr, 0, 0, 0, 0,
                 SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | (verify_ ? SWP_NOACTIVATE : SWP_NOZORDER));
    SetForegroundWindow(h);
    if (animate_)
        SetTimer(h, AnimationTimer, 16, nullptr);
    if (!queryStarted_ || GetTickCount64() - queryStarted_ >= 10000)
        refresh();
}
void Application::fitDetails()
{
    if (!details_.hwnd)
        return;
    float scale = details_.scale;
    float creditsHeight = 84;
    for (auto &c : state_.data.credits)
        creditsHeight += drawing_.measure(c.validity(), 11, false, 300).height + 32;
    float height = 36 + 76 + 136 + 12 + (state_.find(Kind::FiveHour) ? 198.f : 48.f + 12 + 198.f) + 12 +
                   creditsHeight + 90;
    for (auto &q : state_.data.windows)
        if (!state_.selected || q.key != state_.selected->key)
            if (q.kind != Kind::Week)
                height += 90;
    HWND bar = FindWindowW(L"Shell_TrayWnd", nullptr);
    RECT taskbar{}, widget{};
    GetWindowRect(bar, &taskbar);
    GetWindowRect(widget_, &widget);
    MONITORINFO mi{sizeof(mi)};
    GetMonitorInfoW(MonitorFromWindow(widget_, MONITOR_DEFAULTTOPRIMARY), &mi);
    int w = (int)round(380 * scale),
        h = (int)round(std::min(height, (mi.rcWork.bottom - mi.rcWork.top) / scale - 12) * scale);
    h = std::max((int)(400 * scale), h);
    int x = std::clamp((widget.left + widget.right - w) / 2, mi.rcWork.left,
                       std::max(mi.rcWork.left, mi.rcWork.right - w));
    int y = std::max(mi.rcWork.top, taskbar.top - h);
    SetWindowPos(details_.hwnd, nullptr, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    details_.scale = scaleFor(details_.hwnd);
}
void Application::closeView(View &v, bool revert)
{
    if (revert)
    {
        settings_ = saved_;
        appearance();
        place(true);
    }
    HWND h = v.hwnd;
    BOOL destroyed = h ? DestroyWindow(h) : TRUE;
    if (verify_)
        verification_["events"].push_back({{"action", &v == &editor_ ? "closeSettings" : "closeDetails"},
                                           {"at", GetTickCount64() - verifyStarted_},
                                           {"handle", (uintptr_t)h},
                                           {"destroyed", destroyed != FALSE},
                                           {"stillExists", h && IsWindow(h) != FALSE}});
    v.hwnd = nullptr;
    v.tooltip = nullptr;
    v.tips.clear();
    v.canvas.reset();
    v.hits.clear();
    if (&v == &editor_)
    {
        controls_.clear();
        controlRects_.clear();
        if (controlFont_)
        {
            DeleteObject(controlFont_);
            controlFont_ = nullptr;
        }
        editorError_.clear();
    }
}
void Application::redrawViews()
{
    if (details_.hwnd && !IsIconic(details_.hwnd))
        InvalidateRect(details_.hwnd, nullptr, FALSE);
    if (editor_.hwnd && !IsIconic(editor_.hwnd))
        InvalidateRect(editor_.hwnd, nullptr, FALSE);
}
void Application::card(Canvas &c, D2D1_RECT_F r, Color tint)
{
    auto surface =
        colors_.contrast ? colors_.background : blend(colors_.surface, tint, colors_.dark ? .12f : .13f);
    c.rect(r, alpha(surface, colors_.glass ? .96f : 1.f), 16);
    c.border(r, alpha(tint, colors_.contrast ? 1.f : colors_.dark ? .32f : .28f), 16);
}
void Application::button(View &v, int id, const std::wstring &label, D2D1_RECT_F rect, bool accentButton)
{
    int index = (int)v.hits.size();
    v.hits.push_back({id, rect, label});
    auto c = accentButton ? colors_.accent : colors_.primary;
    v.canvas->rect(rect, alpha(c, index == v.hover ? .2f : accentButton ? .14f : .07f), 7);
    if (v.focus == index)
        v.canvas->border(rect, colors_.accent, 7, 1.3f);
    auto size = drawing_.measure(label, 11, true);
    v.canvas->text(label,
                   box(rect.left + std::max(8.f, (rect.right - rect.left - size.width) / 2),
                       rect.top + (rect.bottom - rect.top - size.height) / 2, rect.right - rect.left - 16,
                       size.height + 1),
                   11, c, true);
}
void Application::tip(View &v, int id, const std::wstring &text, D2D1_RECT_F rect)
{
    if (!v.tooltip)
    {
        v.tooltip =
            CreateWindowExW(WS_EX_TOPMOST, TOOLTIPS_CLASSW, nullptr, WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX,
                            0, 0, 0, 0, v.hwnd, nullptr, instance_, nullptr);
        SendMessageW(v.tooltip, TTM_SETMAXTIPWIDTH, 0, 520);
    }
    bool added = v.tips.contains(id);
    bool changed = !added || v.tips[id] != text;
    if (changed)
        v.tips[id] = text;
    TOOLINFOW tool{sizeof(tool)};
    tool.hwnd = v.hwnd;
    tool.uId = id;
    tool.uFlags = TTF_SUBCLASS;
    tool.lpszText = v.tips[id].data();
    tool.rect = {(LONG)(rect.left * v.scale), (LONG)(rect.top * v.scale), (LONG)(rect.right * v.scale),
                 (LONG)(rect.bottom * v.scale)};
    if (!added)
        SendMessageW(v.tooltip, TTM_ADDTOOLW, 0, (LPARAM)&tool);
    else
    {
        if (changed)
            SendMessageW(v.tooltip, TTM_UPDATETIPTEXTW, 0, (LPARAM)&tool);
        SendMessageW(v.tooltip, TTM_NEWTOOLRECTW, 0, (LPARAM)&tool);
    }
}
void Application::chrome(View &v, const std::wstring &label, float w)
{
    v.canvas->text(label, box(20, 10, w - 116, 20), 11, colors_.secondary);
    auto &c = *v.canvas;
    int index = (int)v.hits.size();
    v.hits.push_back({Minimize, box(w - 88, 0, 44, 36), L"最小化"});
    if (index == v.hover)
        c.rect(box(w - 88, 0, 44, 36), alpha(colors_.primary, .1f));
    c.line(w - 70, 19, w - 58, 19, colors_.primary);
    index = (int)v.hits.size();
    v.hits.push_back({Close, box(w - 44, 0, 44, 36), L"关闭"});
    if (index == v.hover)
        c.rect(box(w - 44, 0, 44, 36), color(L"#C42B1C"));
    auto tint = index == v.hover ? color(L"#FFFFFF") : colors_.primary;
    c.line(w - 27, 13, w - 17, 23, tint);
    c.line(w - 17, 13, w - 27, 23, tint);
}
void Application::drawDetails()
{
    auto &v = details_;
    if (!v.hwnd || !v.canvas)
        return;
    RECT r{};
    GetClientRect(v.hwnd, &r);
    float w = r.right / v.scale, h = r.bottom / v.scale;
    auto &c = *v.canvas;
    if (!c.begin(v.hwnd, false, v.scale,
                 alpha(colors_.background, backdropOpacity(colors_, settings_.detailsOpacity))))
        return;
    v.hits.clear();
    chrome(v, L"CODEX  /  额度监控", w);
    c.text(L"额度概览", box(18, 42, w - 120, 33), 23, colors_.primary, true);
    c.text(planLabel(state_.data.plan), box(18, 77, w - 150, 20), 11, colors_.secondary);
    auto refreshLabel = state_.refreshing
                            ? L"刷新中 " + std::to_wstring((GetTickCount64() - queryStarted_) / 1000) + L"秒"
                            : L"刷新";
    button(v, Refresh, refreshLabel, box(w - 119, 47, 101, 32), true);
    float bottom = h - 72;
    float y = 108 - v.scroll;
    float left = 18, right = w - 18, width = w - 36;
    c.clip({0, 104, w, bottom});
    auto q = state_.selected;
    card(c, {left, y, right, y + 130}, colors_.accent);
    c.text(q ? q->name : L"当前额度窗口", box(left + 16, y + 14, width - 150, 22), 13, colors_.primary, true);
    c.text(state_.missingFive ? L"字段暂缺"
           : state_.stale     ? L"数据陈旧"
           : state_.updated   ? L"当前窗口"
                              : L"等待数据",
           box(right - 100, y + 16, 88, 20), 11, colors_.secondary);
    c.text(q ? q->label() : L"剩余 --", box(left + 16, y + 40, width - 32, 45), 32, colors_.primary, true);
    c.progress(box(left + 16, y + 94, width - 32, 10), q ? q->remaining() : std::nullopt, colors_.accent,
               colors_.track, v.animation);
    for (auto &limit : state_.data.windows)
        if (!limit.restriction.empty())
        {
            c.text(restrictionLabel(limit.restriction), box(left + 16, y + 110, width - 32, 18), 10,
                   colors_.secondary);
            break;
        }
    y += 142;
    auto five = state_.find(Kind::FiveHour);
    if (!five && state_.missingFive)
        five = state_.rememberedFive;
    auto week = state_.find(Kind::Week);
    auto timeCard = [&](float x, float top, float cw, const std::wstring &title, std::optional<Quota> quota,
                        Color tint, int copy)
    {
        card(c, box(x, top, cw, 198), tint);
        c.text(title, box(x + 13, top + 12, cw - 26, 20), 12, colors_.primary, true);
        c.text(countdown(quota ? quota->reset : std::nullopt), box(x + 13, top + 39, cw - 26, 44), 16, tint,
               true);
        c.progress(box(x + 13, top + 86, cw - 26, 10), quota ? quota->timePercent() : std::nullopt, tint,
                   colors_.track, v.animation);
        c.text(readable(quota ? quota->reset : std::nullopt, v.utc), box(x + 13, top + 106, cw - 26, 36), 11,
               colors_.secondary);
        auto dateRect = box(x + 13, top + 106, cw - 26, 36);
        dateRect.top = std::max(104.f, dateRect.top);
        dateRect.bottom = std::max(dateRect.top, std::min(bottom, dateRect.bottom));
        tip(v, copy,
            L"本地：" + fullDate(quota ? quota->reset : std::nullopt) + L"\nUTC：" +
                fullDate(quota ? quota->reset : std::nullopt, true),
            dateRect);
        if (quota)
            c.text(quota->label(), box(x + 13, top + 139, cw - 26, 20), 11, colors_.secondary);
        auto b = box(x + 13, top + 163, 72, 25);
        if (b.bottom > 104 && b.top < bottom)
        {
            button(v, copy, L"复制时间", b);
            v.hits.back().rect.top = std::max(104.f, b.top);
            v.hits.back().rect.bottom = std::min(bottom, b.bottom);
        }
    };
    if (five)
    {
        float half = (width - 10) / 2;
        timeCard(left, y, half, L"5 小时重置", five, colors_.accent, CopyFive);
        timeCard(left + half + 10, y, half, L"周额度重置", week, colors_.weekly, CopyWeek);
        y += 210;
    }
    else
    {
        card(c, box(left, y, width, 44), colors_.accent);
        c.text(state_.updated ? L"5 小时额度不可用" : L"正在确认 5 小时额度",
               box(left + 13, y + 12, width - 26, 22), 12, colors_.secondary);
        y += 56;
        timeCard(left, y, width, L"周额度重置", week, colors_.weekly, CopyWeek);
        y += 210;
    }
    for (auto &other : state_.data.windows)
    {
        if ((q && other.key == q->key) || other.kind == Kind::Week || other.kind == Kind::FiveHour)
            continue;
        card(c, box(left, y, width, 92), colors_.secondary);
        c.text(other.name + L" · " + other.label(), box(left + 14, y + 12, width - 28, 24), 12,
               colors_.primary, true);
        c.progress(box(left + 14, y + 44, width - 28, 10), other.remaining(), colors_.accent, colors_.track,
                   v.animation);
        c.text(L"重置 " + countdown(other.reset), box(left + 14, y + 65, width - 28, 20), 11,
               colors_.secondary);
        y += 104;
    }
    float creditHeight = 82;
    for (auto &cr : state_.data.credits)
        creditHeight += drawing_.measure(cr.validity(), 11, false, width - 56).height + 36;
    card(c, box(left, y, width, creditHeight), colors_.credit);
    c.text(L"重置卡", box(left + 16, y + 14, width - 130, 24), 14, colors_.primary, true);
    c.text(state_.data.creditCount ? std::to_wstring(*state_.data.creditCount) + L" 张" : L"数量未知",
           box(right - 112, y + 12, 96, 30), 20, colors_.primary, true);
    std::wstring status = state_.creditDetailsStale ? L"上次明细 · " + readable(state_.creditsUpdated)
                          : state_.data.creditDetails == Query::CountOnly
                              ? L"数量已更新 · 接口本次未提供到期明细"
                          : state_.data.creditDetails == Query::Complete ? L"按到期时间排序"
                          : state_.data.creditDetails == Query::Partial  ? L"仅返回部分到期详情"
                                                                         : L"到期详情暂不可用";
    c.text(status, box(left + 16, y + 47, width - 32, 22), 11, colors_.secondary);
    float cy = y + 76;
    int creditIndex = 0;
    for (auto &cr : state_.data.credits)
    {
        ++creditIndex;
        auto validity = cr.validity();
        float ch = drawing_.measure(validity, 11, false, width - 56).height + 30;
        c.rect(box(left + 12, cy, width - 24, ch), alpha(colors_.surface, .68f), 9);
        c.text((cr.title.empty() ? L"重置卡" : cr.title) + L" · " + std::to_wstring(creditIndex),
               box(left + 24, cy + 5, width - 48, 20), 11, colors_.primary, true);
        c.text(validity, box(left + 24, cy + 24, width - 48, ch - 23), 11, colors_.secondary);
        auto tipRect = box(left + 12, cy, width - 24, ch);
        tipRect.top = std::max(104.f, tipRect.top);
        tipRect.bottom = std::max(tipRect.top, std::min(bottom, tipRect.bottom));
        tip(v, 1000 + creditIndex,
            L"到期：" + fullDate(cr.expires) + L"\nUTC：" + fullDate(cr.expires, true) + L"\n发放：" +
                fullDate(cr.granted),
            tipRect);
        cy += ch + 6;
    }
    for (auto it = v.tips.begin(); it != v.tips.end();)
    {
        if (it->first > 1000 + creditIndex)
        {
            TOOLINFOW t{sizeof(t)};
            t.hwnd = v.hwnd;
            t.uId = it->first;
            SendMessageW(v.tooltip, TTM_DELTOOLW, 0, (LPARAM)&t);
            it = v.tips.erase(it);
        }
        else
            ++it;
    }
    y += creditHeight + 12;
    v.total = y + v.scroll - 104;
    c.unclip();
    c.rect(box(0, bottom, w, h - bottom), alpha(colors_.background, colors_.glass ? .2f : 1));
    std::wstring updated =
        state_.updated
            ? (now() - state_.updated < 60 ? L"刚刚更新"
                                           : std::to_wstring((now() - state_.updated) / 60) + L" 分钟前更新")
            : L"尚未成功更新";
    c.text(updated, box(18, bottom + 9, w - 202, 20), 11, colors_.primary);
    c.text(state_.missingFive                        ? L"5 小时字段暂缺，等待确认"
           : state_.error.empty() && !state_.updated ? L"正在连接 Codex"
                                                     : errorLabel(state_.error),
           box(18, bottom + 38, w - 36, 30), 10, colors_.secondary);
    button(v, ToggleUtc, v.utc ? L"本地时间" : L"UTC", box(w - 180, bottom + 6, 68, 28));
    button(v, SettingsPage, L"外观与位置", box(w - 106, bottom + 6, 88, 28));
    if (v.total > bottom - 104)
    {
        float visible = bottom - 104;
        float thumb = std::max(24.f, visible * visible / v.total);
        float top = 104 + (visible - thumb) * v.scroll / std::max(1.f, v.total - visible);
        c.rect(box(w - 7, top, 3, thumb), alpha(colors_.secondary, .4f), 2);
    }
    c.end();
}
void Application::openSettings()
{
    if (editor_.hwnd)
    {
        ShowWindow(editor_.hwnd, SW_RESTORE);
        SetForegroundWindow(editor_.hwnd);
        return;
    }
    editor_ = View{};
    editor_.canvas = std::make_unique<Canvas>(drawing_);
    editor_.scale = scaleFor(widget_);
    editor_.opened = GetTickCount64();
    editorError_.clear();
    float s = editor_.scale;
    MONITORINFO mi{sizeof(mi)};
    GetMonitorInfoW(MonitorFromWindow(widget_, MONITOR_DEFAULTTOPRIMARY), &mi);
    int width = (int)(440 * s), height = std::min<int>((int)(790 * s), mi.rcWork.bottom - mi.rcWork.top - 16);
    HWND h = CreateWindowExW(WS_EX_APPWINDOW | WS_EX_CONTROLPARENT, L"CodexMonitor.Native.View",
                             L"Codex 额度监控设置", WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN,
                             mi.rcWork.right - width - 24,
                             mi.rcWork.top + (mi.rcWork.bottom - mi.rcWork.top - height) / 2, width, height,
                             nullptr, nullptr, instance_, this);
    if (!h)
        throw std::runtime_error("SETTINGS_CREATE_FAILED");
    editor_.hwnd = h;
    editor_.scale = scaleFor(h);
    controlFont_ =
        CreateFontW(-(int)(13 * editor_.scale), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                    OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH, L"Segoe UI");
    editorSync_ = true;
    createControl(PositionControl, L"COMBOBOX", L"", 20, 132, 400, 240, CBS_DROPDOWNLIST | WS_VSCROLL);
    createControl(ThemeControl, L"COMBOBOX", L"", 20, 218, 400, 240, CBS_DROPDOWNLIST | WS_VSCROLL);
    const wchar_t *positions[] = {L"自动：可靠空位时贴合任务栏", L"任务栏优先：不遮挡系统控件",
                                  L"固定上移：位于任务栏上缘"};
    for (auto value : positions)
        SendMessageW(controls_[PositionControl], CB_ADDSTRING, 0, (LPARAM)value);
    for (auto value :
         {L"雾白 · 瓷白与雾蓝", L"暖砂 · 象牙与琥珀", L"石墨 · 炭灰与银蓝", L"午夜 · 墨蓝与月紫", L"自定义"})
        SendMessageW(controls_[ThemeControl], CB_ADDSTRING, 0, (LPARAM)value);
    createControl(OpacityControl, L"EDIT", L"", 20, 332, 185, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(FontControl, L"EDIT", L"", 235, 332, 185, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(HorizontalControl, L"EDIT", L"", 20, 402, 185, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(VerticalControl, L"EDIT", L"", 235, 402, 185, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(BackgroundControl, L"EDIT", L"", 20, 486, 185, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(PrimaryControl, L"EDIT", L"", 235, 486, 185, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(SecondaryControl, L"EDIT", L"", 20, 552, 185, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(AccentControl, L"EDIT", L"", 235, 552, 185, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(RefreshControl, L"EDIT", L"", 20, 782, 400, 30, ES_AUTOHSCROLL | WS_BORDER);
    for (int id : {OpacityControl, FontControl, HorizontalControl, VerticalControl, RefreshControl})
    {
        auto r = controlRects_[id];
        controlRects_[id].left = r.right - 58;
        createControl(id + SliderOffset, TRACKBAR_CLASSW, L"", r.left, r.top, r.right - r.left - 68, 30,
                      TBS_HORZ | TBS_NOTICKS);
        auto slider = controls_[id + SliderOffset];
        int min = id == OpacityControl      ? 40
                  : id == FontControl       ? 130
                  : id == HorizontalControl ? -4000
                  : id == RefreshControl    ? 1
                                            : 0;
        int max = id == OpacityControl      ? 100
                  : id == FontControl       ? 180
                  : id == HorizontalControl ? 0
                  : id == RefreshControl    ? 120
                                            : 160;
        SendMessageW(slider, TBM_SETRANGEMIN, FALSE, min);
        SendMessageW(slider, TBM_SETRANGEMAX, TRUE, max);
        SendMessageW(slider, TBM_SETPAGESIZE, 0, id == HorizontalControl ? 50 : 5);
        SetWindowTheme(slider, L"", L"");
        SetWindowTextW(slider, id == OpacityControl      ? L"磨砂色调浓度"
                               : id == FontControl       ? L"数字条字号"
                               : id == HorizontalControl ? L"水平偏移"
                               : id == VerticalControl   ? L"上移距离"
                                                         : L"刷新间隔");
    }
    createControl(PathControl, L"EDIT", L"", 20, 902, 315, 30, ES_AUTOHSCROLL | WS_BORDER);
    createControl(Browse, L"BUTTON", L"浏览…", 345, 902, 75, 30, BS_PUSHBUTTON);
    // Real button HWNDs preserve Tab/Enter/Esc behavior for the settings form.
    createControl(Defaults, L"BUTTON", L"恢复默认", 20, 0, 88, 32, BS_PUSHBUTTON);
    createControl(Cancel, L"BUTTON", L"取消", 258, 0, 74, 32, BS_PUSHBUTTON);
    createControl(Save, L"BUTTON", L"保存", 342, 0, 78, 32, BS_DEFPUSHBUTTON);
    editorSync_ = false;
    fillControls();
    appearance();
    layoutControls();
    ShowWindow(h, SW_SHOWNORMAL);
    SetWindowPos(h, nullptr, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW | SWP_NOZORDER);
    SetForegroundWindow(h);
}
void Application::createControl(int id, const wchar_t *cls, const std::wstring &label, float x, float y,
                                float width, float height, DWORD style)
{
    HWND h = CreateWindowExW(0, cls, label.c_str(), WS_CHILD | WS_VISIBLE | WS_TABSTOP | style, 0, 0, 0, 0,
                             editor_.hwnd, (HMENU)(INT_PTR)id, instance_, nullptr);
    if (!h)
        throw std::runtime_error("SETTINGS_CONTROL_FAILED");
    controls_[id] = h;
    controlRects_[id] = box(x, y, width, height);
    SendMessageW(h, WM_SETFONT, (WPARAM)controlFont_, TRUE);
    SendMessageW(h, EM_SETLIMITTEXT, id == PathControl ? 32700 : 64, 0);
    SetWindowSubclass(
        h,
        [](HWND child, UINT m, WPARAM w, LPARAM l, UINT_PTR, DWORD_PTR ref) -> LRESULT
        {
            auto self = (Application *)ref;
            if (m == WM_PAINT)
            {
                PAINTSTRUCT ps{};
                HDC dc = BeginPaint(child, &ps);
                RECT r{};
                GetClientRect(child, &r);
                HDC buffered = nullptr;
                HPAINTBUFFER buffer = BeginBufferedPaint(dc, &r, BPBF_TOPDOWNDIB, nullptr, &buffered);
                if (buffer)
                {
                    DefSubclassProc(child, WM_PRINTCLIENT, (WPARAM)buffered, PRF_CLIENT | PRF_ERASEBKGND);
                    BufferedPaintSetAlpha(buffer, &r, 255);
                    EndBufferedPaint(buffer, TRUE);
                }
                else
                    DefSubclassProc(child, WM_PRINTCLIENT, (WPARAM)dc, PRF_CLIENT | PRF_ERASEBKGND);
                EndPaint(child, &ps);
                return 0;
            }
            if (m == WM_MOUSEWHEEL)
            {
                int id = GetDlgCtrlID(child);
                if ((id == PositionControl || id == ThemeControl) &&
                    SendMessageW(child, CB_GETDROPPEDSTATE, 0, 0))
                    return DefSubclassProc(child, m, w, l);
                SendMessageW(self->editor_.hwnd, m, w, l);
                return 0;
            }
            if (m == WM_KEYDOWN && w == VK_ESCAPE)
            {
                self->command(Cancel);
                return 0;
            }
            if (m == WM_KEYDOWN && w == VK_RETURN)
            {
                self->command(Save);
                return 0;
            }
            return DefSubclassProc(child, m, w, l);
        },
        1, (DWORD_PTR)this);
}
void Application::layoutControls()
{
    if (!editor_.hwnd)
        return;
    RECT r{};
    GetClientRect(editor_.hwnd, &r);
    float height = r.bottom / editor_.scale, scale = editor_.scale;
    float available = std::max(100.f, height - 170);
    editor_.total = 960;
    editor_.scroll = std::clamp(editor_.scroll, 0.f, std::max(0.f, editor_.total - available));
    for (auto &[id, h] : controls_)
    {
        auto b = controlRects_[id];
        float y = id >= Save && id <= Defaults ? height - 49 : b.top - editor_.scroll;
        float visibleHeight = (id == PositionControl || id == ThemeControl) ? 28 : b.bottom - b.top;
        bool visible = (id >= Save && id <= Defaults) || (y >= 99 && y + visibleHeight <= height - 90);
        ShowWindow(h, visible ? SW_SHOWNA : SW_HIDE);
        SetWindowPos(h, nullptr, (int)(b.left * scale), (int)(y * scale), (int)((b.right - b.left) * scale),
                     (int)((b.bottom - b.top) * scale), SWP_NOZORDER | SWP_NOACTIVATE);
        EnableWindow(h, !saving_);
    }
    InvalidateRect(editor_.hwnd, nullptr, FALSE);
}
void Application::fillControls()
{
    if (!editor_.hwnd || controls_.empty())
        return;
    editorSync_ = true;
    SendMessageW(controls_[PositionControl], CB_SETCURSEL, settings_.position, 0);
    SendMessageW(controls_[ThemeControl], CB_SETCURSEL, settings_.preset, 0);
    auto number = [&](int id, double v)
    {
        std::wostringstream s;
        s << v;
        SetWindowTextW(controls_[id], s.str().c_str());
        if (sliderControl(id))
            SendMessageW(controls_[id + SliderOffset], TBM_SETPOS, TRUE, (LPARAM)round(v * sliderFactor(id)));
    };
    number(OpacityControl, settings_.detailsOpacity);
    number(FontControl, settings_.font);
    number(HorizontalControl, settings_.horizontal);
    number(VerticalControl, settings_.vertical);
    number(RefreshControl, settings_.refresh / 60.);
    SetWindowTextW(controls_[BackgroundControl], settings_.background.c_str());
    SetWindowTextW(controls_[PrimaryControl], settings_.primary.c_str());
    SetWindowTextW(controls_[SecondaryControl], settings_.secondary.c_str());
    SetWindowTextW(controls_[AccentControl], settings_.accent.c_str());
    SetWindowTextW(controls_[PathControl], settings_.codex.c_str());
    editorSync_ = false;
}
void Application::readControls(bool save)
{
    if (editorSync_ || saving_ || !editor_.hwnd)
        return;
    Settings s = settings_;
    editorError_.clear();
    s.position = (int)SendMessageW(controls_[PositionControl], CB_GETCURSEL, 0, 0);
    s.preset = (int)SendMessageW(controls_[ThemeControl], CB_GETCURSEL, 0, 0);
    auto number = [&](int id, double min, double max, double &out)
    {
        auto text = textOf(controls_[id]);
        wchar_t *end = nullptr;
        double value = wcstod(text.c_str(), &end);
        if (text.empty() || end == text.c_str() || *end || !std::isfinite(value) || value < min ||
            value > max)
            return false;
        out = value;
        return true;
    };
    double opacity = 0, font = 0, x = 0, y = 0, refresh = 0;
    bool numbers = number(OpacityControl, .4, 1, opacity) && number(FontControl, 13, 18, font) &&
                   number(HorizontalControl, -4000, 0, x) && number(VerticalControl, 0, 160, y) &&
                   number(RefreshControl, .5, 60, refresh);
    s.background = textOf(controls_[BackgroundControl]);
    s.primary = textOf(controls_[PrimaryControl]);
    s.secondary = textOf(controls_[SecondaryControl]);
    s.accent = textOf(controls_[AccentControl]);
    s.codex = textOf(controls_[PathControl]);
    bool valid =
        validColor(s.background) && validColor(s.primary) && validColor(s.secondary) && validColor(s.accent);
    if (!numbers || !valid)
    {
        editorError_ =
            !numbers ? L"请检查数值范围：刷新 0.5–60 分钟，字号 13–18。" : L"颜色格式应为 #RRGGBB。";
        InvalidateRect(editor_.hwnd, nullptr, FALSE);
        return;
    }
    s.detailsOpacity = opacity;
    s.font = font;
    s.horizontal = (int)round(x);
    s.vertical = (int)round(y);
    s.refresh = (int)round(refresh * 60);
    s.normalize();
    for (auto [id, value] : {std::pair{OpacityControl, opacity},
                             {FontControl, font},
                             {HorizontalControl, x},
                             {VerticalControl, y},
                             {RefreshControl, refresh}})
        SendMessageW(controls_[id + SliderOffset], TBM_SETPOS, TRUE, (LPARAM)round(value * sliderFactor(id)));
    settings_ = s;
    appearance();
    place(true);
    if (!save)
        return;
    if (!s.codex.empty())
    {
        saving_ = true;
        pendingSave_ = s;
        editorError_ = L"正在验证 Codex CLI…";
        layoutControls();
        if (validationThread_.joinable())
            validationThread_.join();
        validationThread_ = std::thread(
            [this, path = s.codex, target = controller_]
            {
                bool ok = verifyCodex(path, stop_);
                {
                    std::lock_guard lock(resultMutex_);
                    validationResult_ = ok;
                }
                PostMessageW(target, ValidationMessage, 0, 0);
            });
    }
    else
    {
        commitSettings(s);
        closeView(editor_, false);
        this->refresh();
    }
}
void Application::drawSettings()
{
    auto &v = editor_;
    if (!v.hwnd || !v.canvas)
        return;
    RECT r{};
    GetClientRect(v.hwnd, &r);
    float w = r.right / v.scale, h = r.bottom / v.scale;
    auto &c = *v.canvas;
    if (!c.begin(v.hwnd, false, v.scale,
                 alpha(colors_.background, backdropOpacity(colors_, settings_.detailsOpacity))))
        return;
    v.hits.clear();
    chrome(v, L"CODEX  /  设置", w);
    c.text(L"外观与位置", box(20, 44, w - 40, 36), 23, colors_.primary, true);
    c.clip({0, 99, w, h - 90});
    auto label =
        [&](const std::wstring &text, float x, float y, float width = 400, float size = 12, bool bold = true)
    {
        c.text(text, box(x, y - v.scroll, width, 42), size, bold ? colors_.primary : colors_.secondary, bold);
    };
    label(L"位置模式", 20, 106);
    label(L"详情与设置主题", 20, 180);
    label(L"雾白、暖砂为浅色；石墨、午夜为深色。", 20, 260, 400, 11, false);
    label(L"磨砂色调浓度（0.4–1）", 20, 306, 195);
    label(L"数字条字号（13–18）", 235, 306, 185);
    label(L"水平偏移（−4000–0）", 20, 376, 195);
    label(L"上移距离（0–160）", 235, 376, 185);
    label(L"自定义背景色", 20, 460, 195);
    label(L"主文字色", 235, 460, 185);
    label(L"辅助文字色", 20, 526, 195);
    label(L"强调色", 235, 526, 185);
    label(L"数字条示例 · 正文跟随系统任务栏", 20, 604, 400, 11, false);
    Color bg = taskbarDark_ ? color(L"#262626") : color(L"#F0F0F0"),
          fg = taskbarDark_ ? color(L"#EEEEEE") : color(L"#1B1B1B");
    c.rect(box(20, 631 - v.scroll, 400, 65), bg, 12);
    c.text(L"剩余 78%", box(34, 640 - v.scroll, 130, 25), (float)settings_.font, fg, true);
    c.text(L"周 ·", box(150, 644 - v.scroll, 40, 20), 10,
           contrast_ ? fg : color(taskbarDark_ ? L"#91C5FF" : L"#286DB7"), true);
    c.text(L"重置 2小时14分", box(34, 666 - v.scroll, 350, 20), 11, alpha(fg, .75f));
    auto luminance = [](Color color)
    {
        auto channel = [](float v) { return v <= .04045f ? v / 12.92 : pow((v + .055) / 1.055, 2.4); };
        return .2126 * channel(color.r) + .7152 * channel(color.g) + .0722 * channel(color.b);
    };
    double a = luminance(colors_.background), b = luminance(colors_.primary);
    double contrast = (std::max(a, b) + .05) / (std::min(a, b) + .05);
    wchar_t hint[100]{};
    swprintf_s(hint, L"详情文字对比度 %.1f:1%s", contrast,
               contrast < 4.5 ? L" · 建议调整文字与背景颜色" : L" · 可读性良好");
    label(hint, 20, 705, 400, 11, false);
    label(L"后台刷新间隔（分钟）", 20, 752);
    label(L"默认 1 分钟，可设置 0.5–60 分钟。保存后生效；倒计时独立更新。", 20, 824, 400, 11, false);
    label(L"Codex CLI 路径（留空自动查找）", 20, 874);
    label(L"只读查询复用 Codex 登录状态；路径在保存前验证。", 20, 946, 400, 11, false);
    label(L"开机启动与清除缓存请使用右键菜单。\n清理保留设置与 Codex 登录信息。", 20, 988, 400, 11, false);
    c.unclip();
    c.rect(box(0, h - 90, w, 90), alpha(colors_.background, colors_.glass ? .3f : 1));
    c.text(editorError_, box(20, h - 88, w - 40, 34), 11,
           editorError_.empty() ? colors_.secondary : colors_.accent);
    float visible = h - 190;
    if (v.total > visible)
    {
        float thumb = std::max(24.f, visible * visible / v.total);
        float top = 99 + (visible - thumb) * v.scroll / std::max(1.f, v.total - visible);
        c.rect(box(w - 7, top, 3, thumb), alpha(colors_.secondary, .4f), 2);
    }
    c.end();
}
LRESULT Application::viewMessage(View &v, bool editor, HWND h, UINT m, WPARAM w, LPARAM l)
{
    switch (m)
    {
    case WM_NCCALCSIZE:
        if (w)
            return 0;
        break;
    case WM_NCHITTEST:
    {
        POINT p{GET_X_LPARAM(l), GET_Y_LPARAM(l)};
        ScreenToClient(h, &p);
        RECT r{};
        GetClientRect(h, &r);
        float s = scaleFor(h);
        int x = p.x, y = p.y;
        if (!editor && !IsZoomed(h))
        {
            int edge = (int)(6 * s);
            bool left = x < edge, right = x >= r.right - edge, top = y < edge, bottom = y >= r.bottom - edge;
            if (top && left)
                return HTTOPLEFT;
            if (top && right)
                return HTTOPRIGHT;
            if (bottom && left)
                return HTBOTTOMLEFT;
            if (bottom && right)
                return HTBOTTOMRIGHT;
            if (left)
                return HTLEFT;
            if (right)
                return HTRIGHT;
            if (top)
                return HTTOP;
            if (bottom)
                return HTBOTTOM;
        }
        if (y < 36 * s && x < r.right - 88 * s)
            return HTCAPTION;
        return HTCLIENT;
    }
    case WM_GETMINMAXINFO:
    {
        auto p = (MINMAXINFO *)l;
        float s = scaleFor(h);
        p->ptMinTrackSize = {(LONG)((editor ? 440 : 360) * s), (LONG)(400 * s)};
        return 0;
    }
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT:
    {
        PAINTSTRUCT paint{};
        BeginPaint(h, &paint);
        if (editor)
            drawSettings();
        else
            drawDetails();
        EndPaint(h, &paint);
        return 0;
    }
    case WM_SIZE:
        if (w == SIZE_MINIMIZED)
        {
            if (v.canvas)
                v.canvas->reset();
            KillTimer(h, AnimationTimer);
            v.animation = 1;
        }
        else
        {
            if (editor)
                layoutControls();
            InvalidateRect(h, nullptr, FALSE);
        }
        return 0;
    case WM_DPICHANGED:
    {
        v.scale = HIWORD(w) / 96.f;
        auto r = (RECT *)l;
        SetWindowPos(h, nullptr, r->left, r->top, r->right - r->left, r->bottom - r->top,
                     SWP_NOZORDER | SWP_NOACTIVATE);
        if (v.canvas)
            v.canvas->reset();
        if (editor)
        {
            if (controlFont_)
                DeleteObject(controlFont_);
            controlFont_ = CreateFontW(-(int)(13 * v.scale), 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
                                       DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, L"Segoe UI");
            for (auto [_, child] : controls_)
                SendMessageW(child, WM_SETFONT, (WPARAM)controlFont_, TRUE);
            layoutControls();
        }
        return 0;
    }
    case WM_TIMER:
        if (w == AnimationTimer)
        {
            if (IsIconic(h))
            {
                v.animation = 1;
                KillTimer(h, AnimationTimer);
            }
            else
            {
                float t = std::min(1.f, (GetTickCount64() - v.opened) / 420.f);
                v.animation = 1 - powf(1 - t, 3);
                InvalidateRect(h, nullptr, FALSE);
                if (t >= 1)
                    KillTimer(h, AnimationTimer);
            }
        }
        return 0;
    case WM_MOUSEWHEEL:
    {
        RECT r{};
        GetClientRect(h, &r);
        float visible = r.bottom / v.scale - (editor ? 190 : 176);
        v.scroll = std::clamp(v.scroll - GET_WHEEL_DELTA_WPARAM(w) / 120.f * 54, 0.f,
                              std::max(0.f, v.total - visible));
        if (editor)
            layoutControls();
        InvalidateRect(h, nullptr, FALSE);
        return 0;
    }
    case WM_LBUTTONDOWN:
    {
        RECT r{};
        GetClientRect(h, &r);
        float x = GET_X_LPARAM(l) / v.scale, y = GET_Y_LPARAM(l) / v.scale, height = r.bottom / v.scale;
        if (x > r.right / v.scale - 13 && y > (editor ? 99 : 104) && y < height - (editor ? 90 : 72))
        {
            v.scrollDragging = true;
            SetCapture(h);
            SendMessageW(h, WM_MOUSEMOVE, MK_LBUTTON, l);
        }
        return 0;
    }
    case WM_MOUSEMOVE:
    {
        if (v.scrollDragging && (w & MK_LBUTTON))
        {
            RECT r{};
            GetClientRect(h, &r);
            float top = editor ? 99.f : 104.f, visible = r.bottom / v.scale - top - (editor ? 90 : 72);
            float fraction = std::clamp((GET_Y_LPARAM(l) / v.scale - top) / std::max(1.f, visible), 0.f, 1.f);
            v.scroll = fraction * std::max(0.f, v.total - visible);
            if (editor)
                layoutControls();
            InvalidateRect(h, nullptr, FALSE);
            return 0;
        }
        int hit = hitAt(v, GET_X_LPARAM(l) / v.scale, GET_Y_LPARAM(l) / v.scale);
        if (hit != v.hover)
        {
            v.hover = hit;
            InvalidateRect(h, nullptr, FALSE);
        }
        TRACKMOUSEEVENT t{sizeof(t), TME_LEAVE, h, 0};
        TrackMouseEvent(&t);
        return 0;
    }
    case WM_MOUSELEAVE:
        v.hover = -1;
        InvalidateRect(h, nullptr, FALSE);
        return 0;
    case WM_LBUTTONUP:
    {
        if (v.scrollDragging)
        {
            v.scrollDragging = false;
            ReleaseCapture();
            return 0;
        }
        int hit = hitAt(v, GET_X_LPARAM(l) / v.scale, GET_Y_LPARAM(l) / v.scale);
        if (hit >= 0)
        {
            int action = v.hits[hit].action;
            if (action == Close)
            {
                if (!editor || !saving_)
                    closeView(v, editor);
            }
            else if (action == Minimize)
                ShowWindow(h, SW_MINIMIZE);
            else
                command(action);
        }
        return 0;
    }
    case WM_CAPTURECHANGED:
        v.scrollDragging = false;
        return 0;
    case WM_KEYDOWN:
        if (w == VK_ESCAPE)
        {
            if (!editor || !saving_)
                closeView(v, editor);
            return 0;
        }
        if (!editor && w == VK_TAB && !v.hits.empty())
        {
            int delta = GetKeyState(VK_SHIFT) < 0 ? -1 : 1;
            v.focus = (v.focus + delta + (int)v.hits.size()) % (int)v.hits.size();
            InvalidateRect(h, nullptr, FALSE);
            return 0;
        }
        if (!editor && (w == VK_RETURN || w == VK_SPACE) && v.focus >= 0 && v.focus < (int)v.hits.size())
        {
            int action = v.hits[v.focus].action;
            if (action == Close)
                closeView(v, false);
            else if (action == Minimize)
                ShowWindow(h, SW_MINIMIZE);
            else
                command(action);
            return 0;
        }
        if (w == VK_NEXT || w == VK_PRIOR)
        {
            v.scroll = std::max(0.f, v.scroll + (w == VK_NEXT ? 200 : -200));
            if (editor)
                layoutControls();
            InvalidateRect(h, nullptr, FALSE);
            return 0;
        }
        break;
    case WM_HSCROLL:
        if (editor && l && !editorSync_ && !saving_)
        {
            int id = GetDlgCtrlID((HWND)l) - SliderOffset;
            if (sliderControl(id))
            {
                double value = SendMessageW((HWND)l, TBM_GETPOS, 0, 0) / sliderFactor(id);
                std::wostringstream text;
                text << value;
                editorSync_ = true;
                SetWindowTextW(controls_[id], text.str().c_str());
                editorSync_ = false;
                readControls(false);
                return 0;
            }
        }
        break;
    case WM_NOTIFY:
        if (editor && l)
        {
            auto hdr = (NMHDR *)l;
            if (hdr->code == NM_CUSTOMDRAW && sliderControl((int)hdr->idFrom - SliderOffset))
            {
                auto draw = (NMCUSTOMDRAW *)l;
                if (draw->dwDrawStage == CDDS_PREPAINT)
                    return CDRF_NOTIFYITEMDRAW;
                if (draw->dwDrawStage == CDDS_ITEMPREPAINT)
                {
                    Color tint = draw->dwItemSpec == TBCD_THUMB
                                     ? colors_.accent
                                     : blend(colors_.background, colors_.primary, .24f);
                    HBRUSH brush = CreateSolidBrush(
                        RGB((BYTE)(tint.r * 255), (BYTE)(tint.g * 255), (BYTE)(tint.b * 255)));
                    auto oldBrush = SelectObject(draw->hdc, brush);
                    auto oldPen = SelectObject(draw->hdc, GetStockObject(NULL_PEN));
                    auto r = draw->rc;
                    int radius = draw->dwItemSpec == TBCD_THUMB ? (int)(8 * v.scale) : (int)(4 * v.scale);
                    RoundRect(draw->hdc, r.left, r.top, r.right, r.bottom, radius, radius);
                    SelectObject(draw->hdc, oldPen);
                    SelectObject(draw->hdc, oldBrush);
                    DeleteObject(brush);
                    return CDRF_SKIPDEFAULT;
                }
            }
        }
        break;
    case WM_COMMAND:
        if (editor)
        {
            int id = LOWORD(w), notification = HIWORD(w);
            if (id == IDCANCEL)
            {
                command(Cancel);
                return 0;
            }
            if (id == IDOK)
            {
                command(Save);
                return 0;
            }
            if (id == Save || id == Cancel || id == Defaults || id == Browse)
            {
                command(id);
                return 0;
            }
            if (notification == EN_CHANGE || notification == CBN_SELCHANGE)
            {
                readControls(false);
                return 0;
            }
        }
        break;
    case WM_CTLCOLOREDIT:
    case WM_CTLCOLORLISTBOX:
    case WM_CTLCOLORSTATIC:
    {
        HDC dc = (HDC)w;
        auto fg = colors_.primary, bg = blend(colors_.background, fg, .05f);
        SetTextColor(dc, RGB((BYTE)(fg.r * 255), (BYTE)(fg.g * 255), (BYTE)(fg.b * 255)));
        SetBkColor(dc, RGB((BYTE)(bg.r * 255), (BYTE)(bg.g * 255), (BYTE)(bg.b * 255)));
        if (!editBrush_)
            editBrush_ = CreateSolidBrush(RGB((BYTE)(bg.r * 255), (BYTE)(bg.g * 255), (BYTE)(bg.b * 255)));
        return (LRESULT)editBrush_;
    }
    case WM_SETTINGCHANGE:
    case WM_THEMECHANGED:
    case WM_DWMCOMPOSITIONCHANGED:
        appearance();
        return 0;
    case WM_CLOSE:
        if (!editor || !saving_)
            closeView(v, editor);
        return 0;
    case WM_DESTROY:
        KillTimer(h, AnimationTimer);
        v.hwnd = nullptr;
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}
void Application::finishVerification()
{
    if (report_.empty())
        return;
    PROCESS_MEMORY_COUNTERS_EX memory{};
    memory.cb = sizeof(memory);
    GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS *)&memory, sizeof(memory));
    RECT widget{}, details{};
    GetWindowRect(widget_, &widget);
    if (details_.hwnd)
        GetWindowRect(details_.hwnd, &details);
    Json j = {{"version", "2.0.0-native-preview.4"},
              {"querySucceeded", state_.updated != 0},
              {"queryError", state_.error},
              {"refreshing", state_.refreshing},
              {"quotaWindows", state_.data.windows.size()},
              {"hasResetCreditCount", state_.data.creditCount.has_value()},
              {"trayAdded", trayAdded_},
              {"docked", docked_},
              {"widgetVisible", IsWindowVisible(widget_) != FALSE},
              {"widgetRect", {widget.left, widget.top, widget.right, widget.bottom}},
              {"detailsRect", {details.left, details.top, details.right, details.bottom}},
              {"privateBytes", memory.PrivateUsage},
              {"workingSetBytes", memory.WorkingSetSize},
              {"gdiObjects", GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS)},
              {"userObjects", GetGuiResources(GetCurrentProcess(), GR_USEROBJECTS)},
              {"singleExecutable", true},
              {"usesDotNet", false},
              {"taskbarOwnerRequested", taskbarOwner_ != nullptr},
              {"systemPanelOcclusionAutomaticallyVerified", false}};
    j["observations"] = verification_;
    std::ofstream out(report_, std::ios::binary);
    if (out)
        out << j.dump(2);
}
} // namespace cqm
