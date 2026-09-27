using System.Runtime.InteropServices;
using System.Text;
using CodexQuotaMonitor.Core;

namespace CodexQuotaMonitor.App;

internal enum TrayAction
{
    OpenDetails,
    Refresh,
    OpenSettings,
    AutomaticPosition,
    TaskbarPreferredPosition,
    FixedAbovePosition,
    MistWhiteTheme,
    WarmSandTheme,
    GraphiteTheme,
    MidnightTheme,
    ToggleStartup,
    ClearCache,
    Exit
}

internal sealed class SystemTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint WmLeftButtonDoubleClick = 0x0203;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NotifyIconAdd = 0;
    private const uint NotifyIconModify = 1;
    private const uint NotifyIconDelete = 2;
    private const uint NotifyIconVersion = 4;
    private const uint NotifyFlagMessage = 1;
    private const uint NotifyFlagIcon = 2;
    private const uint NotifyFlagTip = 4;
    private const uint MenuString = 0x00000000;
    private const uint MenuPopup = 0x00000010;
    private const uint MenuChecked = 0x00000008;
    private const uint MenuDisabled = 0x00000001;
    private const uint MenuSeparator = 0x00000800;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x0010;

    private readonly Action<TrayAction> _onAction;
    private readonly Func<bool> _startupEnabled;
    private readonly Func<bool> _cacheBusy;
    private readonly Action _appearanceChanged;
    private readonly WndProcDelegate _windowProc;
    private readonly string _className = $"CQM.Tray.{Environment.ProcessId}";
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _icon;
    private bool _ownsIcon;
    private int _iconPixels;
    private bool _lastNotifySucceeded;
    private IntPtr _hwnd;
    private string _tooltip = "Codex 额度监控";
    private readonly WidgetPresentationCache _presentationCache = new();
    private WidgetPresentation? _presentation;
    private bool _disposed;

    public SystemTrayIcon(Action<TrayAction> onAction, Func<bool> startupEnabled, Func<bool> cacheBusy, Action appearanceChanged)
    {
        _onAction = onAction;
        _startupEnabled = startupEnabled;
        _cacheBusy = cacheBusy;
        _appearanceChanged = appearanceChanged;
        _windowProc = WindowProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProc),
            Instance = GetModuleHandle(null),
            ClassName = _className
        };
        RegisterClassEx(ref windowClass);
        _hwnd = CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, windowClass.Instance, IntPtr.Zero);

        AddOrUpdateIcon(NotifyIconAdd);
        Shell_NotifyIcon(NotifyIconVersion, CreateData());
    }

    public void Update(MonitorViewState state)
    {
        var presentation = _presentationCache.Get(state.Selection, DateTimeOffset.Now,
            state.LastSuccessfulUpdate, state.QueryError);
        if (ReferenceEquals(_presentation, presentation)) return;
        _presentation = presentation;
        var tooltip = $"{presentation.MainLine} · {presentation.WindowTag}\n{presentation.ResetLine}\n{presentation.StateMarker}";
        if (_tooltip == tooltip) return;
        _tooltip = tooltip;
        AddOrUpdateIcon(NotifyIconModify);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == CallbackMessage)
        {
            var eventCode = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
            if (eventCode == WmLeftButtonDoubleClick) _onAction(TrayAction.OpenDetails);
            else if (eventCode is WmRightButtonUp or WmContextMenu) ShowContextMenu();
        }
        else if (message == _taskbarCreatedMessage)
        {
            AddOrUpdateIcon(NotifyIconAdd);
        }
        else if (message is 0x02E0 or 0x007E or 0x001A or 0x031A or 0x0320 or 0x001E)
        {
            // DPI, display, settings, theme, DWM color or system time changed.
            if (message is 0x001A or 0x001E) TimeZoneInfo.ClearCachedData();
            _appearanceChanged();
            AddOrUpdateIcon(NotifyIconModify);
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void ShowContextMenu()
    {
        var menu = BuildContextMenu();
        uint selected;
        try
        {
            GetCursorPos(out var point);
            SetForegroundWindow(_hwnd);
            selected = TrackPopupMenu(menu, TrackRightButton | TrackReturnCommand, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { DestroyMenu(menu); }
        _onAction(ActionForCommand(selected));
    }

    private IntPtr BuildContextMenu()
    {
        var menu = CreatePopupMenu();
        var positionMenu = CreatePopupMenu();
        var themeMenu = CreatePopupMenu();
        AppendMenu(menu, MenuString, 1, "打开额度详情");
        AppendMenu(menu, MenuString, 2, "立即刷新");
        AppendMenu(menu, MenuString, 3, "外观与位置设置");
        AppendMenu(positionMenu, MenuString, 10, "自动");
        AppendMenu(positionMenu, MenuString, 11, "任务栏优先");
        AppendMenu(positionMenu, MenuString, 12, "固定上移");
        AppendMenu(themeMenu, MenuString, 20, "雾白");
        AppendMenu(themeMenu, MenuString, 21, "暖砂");
        AppendMenu(themeMenu, MenuString, 22, "石墨");
        AppendMenu(themeMenu, MenuString, 23, "午夜");
        AppendMenu(menu, MenuPopup, new UIntPtr(unchecked((ulong)positionMenu.ToInt64())), "位置模式");
        AppendMenu(menu, MenuPopup, new UIntPtr(unchecked((ulong)themeMenu.ToInt64())), "主题预设");
        AppendMenu(menu, MenuSeparator, 0, string.Empty);
        AppendMenu(menu, MenuString | (_startupEnabled() ? MenuChecked : 0), 30, "开机启动");
        AppendMenu(menu, MenuString | (_cacheBusy() ? MenuDisabled : 0), 31, _cacheBusy() ? "正在清除缓存…" : "清除缓存");
        AppendMenu(menu, MenuSeparator, 0, string.Empty);
        AppendMenu(menu, MenuString, 99, "退出");
        return menu;
    }

    private static TrayAction ActionForCommand(uint selected) => selected switch
        {
            1 => TrayAction.OpenDetails,
            2 => TrayAction.Refresh,
            3 => TrayAction.OpenSettings,
            10 => TrayAction.AutomaticPosition,
            11 => TrayAction.TaskbarPreferredPosition,
            12 => TrayAction.FixedAbovePosition,
            20 => TrayAction.MistWhiteTheme,
            21 => TrayAction.WarmSandTheme,
            22 => TrayAction.GraphiteTheme,
            23 => TrayAction.MidnightTheme,
            30 => TrayAction.ToggleStartup,
            31 => TrayAction.ClearCache,
            99 => TrayAction.Exit,
            _ => (TrayAction)(-1)
        };

    internal object ReadMenuVerification()
    {
        var menu = BuildContextMenu();
        try
        {
            var startup = GetMenuState(menu, 30, 0);
            var cache = GetMenuState(menu, 31, 0);
            return new { StartupPresent = startup != uint.MaxValue, CachePresent = cache != uint.MaxValue,
                StartupChecked = (startup & MenuChecked) != 0, CacheDisabled = (cache & MenuDisabled) != 0,
                CommandsMapped = ActionForCommand(30) == TrayAction.ToggleStartup && ActionForCommand(31) == TrayAction.ClearCache };
        }
        finally { DestroyMenu(menu); }
    }

    public void Notify(string title, string message)
    {
        if (_disposed) return;
        var data = CreateData();
        data.Flags = 0x10; // NIF_INFO: brief, nonmodal feedback for menu operations.
        data.InfoTitle = title;
        data.Info = message;
        data.InfoFlags = 1;
        Shell_NotifyIcon(NotifyIconModify, data);
    }

    private void AddOrUpdateIcon(uint operation)
    {
        if (_hwnd == IntPtr.Zero) return;
        LoadIconForTaskbarDpi();
        _lastNotifySucceeded = Shell_NotifyIcon(operation, CreateData());
    }

    private void LoadIconForTaskbarDpi()
    {
        var taskbar = NativeWindowInterop.FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar == IntPtr.Zero ? NativeWindowInterop.GetDpiForSystem() : NativeWindowInterop.GetDpiForWindow(taskbar);
        var pixels = Math.Max(16, GetSystemMetricsForDpi(49, Math.Max(96, dpi))); // SM_CXSMICON
        if (_icon != IntPtr.Zero && _iconPixels == pixels) return;
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "cqm.ico");
        var icon = File.Exists(path) ? LoadImage(IntPtr.Zero, path, ImageIcon, pixels, pixels, LoadFromFile) : IntPtr.Zero;
        var owns = icon != IntPtr.Zero;
        if (icon == IntPtr.Zero) icon = LoadIcon(IntPtr.Zero, new IntPtr(32512));
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = icon;
        _ownsIcon = owns;
        _iconPixels = pixels;
    }

    internal object ReadVerification() => new { CustomIconLoaded = _ownsIcon && _icon != IntPtr.Zero,
        IconPixels = _iconPixels, Registered = _lastNotifySucceeded };

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _hwnd,
        Id = 1,
        Flags = NotifyFlagMessage | NotifyFlagIcon | NotifyFlagTip,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
        Info = string.Empty,
        InfoTitle = string.Empty,
        TimeoutOrVersion = NotifyIconVersion
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            Shell_NotifyIcon(NotifyIconDelete, CreateData());
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        UnregisterClass(_className, GetModuleHandle(null));
        GC.KeepAlive(_windowProc);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr idOrSubmenu, string text);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string text);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
