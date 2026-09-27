using CodexQuotaMonitor.Core;

namespace CodexQuotaMonitor.App;

internal sealed class TaskbarPositioner
{
    private IntPtr _hwnd;
    private NativeWindowInterop.Rect _candidate;
    private int _candidateHits;
    private bool _hasApplied;
    private bool _hiddenForFullscreen;
    private int _widthDip = 148;
    private int _heightDip = 42;
    private readonly System.Text.StringBuilder _foregroundClass = new(256);
    private static readonly string[] TaskListClasses = ["MSTaskSwWClass", "MSTaskListWClass", "TaskListThumbnailWnd"];
    private static readonly string[] TrayClasses = ["TrayNotifyWnd"];

    public void SetSize(int widthDip, int heightDip)
    {
        _widthDip = Math.Clamp(widthDip, 118, 260);
        _heightDip = Math.Clamp(heightDip, 42, 64);
    }

    public void Attach(IntPtr hwnd)
    {
        _hwnd = hwnd;
        var style = NativeWindowInterop.GetWindowStyle(hwnd, NativeWindowInterop.GwlStyle);
        style &= ~(NativeWindowInterop.WsCaption | NativeWindowInterop.WsThickFrame |
                   NativeWindowInterop.WsSystemMenu | NativeWindowInterop.WsMinimizeBox |
                   NativeWindowInterop.WsMaximizeBox);
        style |= NativeWindowInterop.WsPopup;
        NativeWindowInterop.SetWindowStyle(hwnd, NativeWindowInterop.GwlStyle, style);

        var exStyle = NativeWindowInterop.GetWindowStyle(hwnd, NativeWindowInterop.GwlExStyle);
        exStyle |= NativeWindowInterop.WsExToolWindow | NativeWindowInterop.WsExNoActivate;
        NativeWindowInterop.SetWindowStyle(hwnd, NativeWindowInterop.GwlExStyle, exStyle);
        NativeWindowInterop.SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
            NativeWindowInterop.SwpNoMove | NativeWindowInterop.SwpNoSize |
            NativeWindowInterop.SwpNoActivate | NativeWindowInterop.SwpFrameChanged);
    }

    public bool IsTaskbarSlot { get; private set; }
    internal bool HiddenForFullscreen => _hiddenForFullscreen;
    internal void ResetPlacement() => _hasApplied = false;

    internal object Diagnostics()
    {
        var foreground = NativeWindowInterop.GetForegroundWindow();
        var name = new System.Text.StringBuilder(256);
        NativeWindowInterop.GetClassName(foreground, name, name.Capacity);
        NativeWindowInterop.GetWindowThreadProcessId(foreground, out var processId);
        return new { Handle = _hwnd.ToInt64(), HiddenForFullscreen, ForegroundClass = name.ToString(),
            ForegroundIsOwn = processId == Environment.ProcessId, WidthDip = _widthDip, HeightDip = _heightDip };
    }

    public void Update(MonitorSettings settings)
    {
        if (_hwnd == IntPtr.Zero) return;

        var dpi = NativeWindowInterop.GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = NativeWindowInterop.GetDpiForSystem();
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96.0;
        var width = (int)Math.Ceiling(_widthDip * scale);
        var height = (int)Math.Ceiling(_heightDip * scale);

        var hasTaskbarSlot = TryTaskbarSlot(settings, width, height, scale, out var taskbarSlot);
        var target = hasTaskbarSlot ? taskbarSlot : AboveTaskbar(settings, width, height, scale);
        IsTaskbarSlot = hasTaskbarSlot;

        if (IsFullscreenForeground())
        {
            // Even a hidden first launch needs its compact bounds; otherwise WinUI keeps
            // the large default window size, which also corrupts subsequent drop geometry.
            if (!_hasApplied)
            {
                NativeWindowInterop.SetWindowPos(_hwnd, IntPtr.Zero, target.Left, target.Top, width, height,
                    NativeWindowInterop.SwpNoActivate | NativeWindowInterop.SwpNoZOrder);
                _hasApplied = true;
            }
            NativeWindowInterop.ShowWindow(_hwnd, 0);
            _hiddenForFullscreen = true;
            return;
        }

        if (!NativeWindowInterop.GetWindowRect(_hwnd, out var current)) current = default;
        if (!_hasApplied || _hiddenForFullscreen || !SamePosition(current, target))
        {
            if (SamePosition(_candidate, target)) _candidateHits++;
            else { _candidate = target; _candidateHits = 1; }

            // A small debounce absorbs transient taskbar re-layouts during Explorer changes.
            if (_hasApplied && _candidateHits < 2 && !_hiddenForFullscreen) return;
            NativeWindowInterop.SetWindowPos(_hwnd, new IntPtr(-1), target.Left, target.Top, width, height,
                NativeWindowInterop.SwpNoActivate | NativeWindowInterop.SwpShowWindow);
            _hasApplied = true;
            _hiddenForFullscreen = false;
        }
        else if (!NativeWindowInterop.IsWindowVisible(_hwnd) || IsBehindTaskbar())
        {
            _candidateHits = 0;
            // Explorer can raise its own topmost taskbar over us without changing our bounds.
            // Restore on shell events (timer is a fallback), without activating the widget.
            NativeWindowInterop.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0,
                NativeWindowInterop.SwpNoMove | NativeWindowInterop.SwpNoSize |
                NativeWindowInterop.SwpNoActivate | NativeWindowInterop.SwpShowWindow);
        }
    }

    internal bool IsBehindTaskbar()
    {
        var taskbar = NativeWindowInterop.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        var current = NativeWindowInterop.GetWindow(_hwnd, 3); // GW_HWNDPREV, toward top of Z order
        for (var count = 0; current != IntPtr.Zero && count < 512; count++)
        {
            if (current == taskbar) return true;
            current = NativeWindowInterop.GetWindow(current, 3);
        }
        return false;
    }

    internal static MonitorSettings SettingsForDrop(MonitorSettings settings, int left, int top, int width, int height, double scale)
    {
        var taskbar = NativeWindowInterop.FindWindow("Shell_TrayWnd", null);
        if (!NativeWindowInterop.GetWindowRect(taskbar, out var bar)) return settings;
        if (WidgetDropPolicy.IsNearTaskbar(left + width / 2.0, top + height / 2.0,
            bar.Left, bar.Top, bar.Right, bar.Bottom, scale))
        {
            var docked = settings with { PositionMode = PositionMode.TaskbarPreferred, HorizontalOffsetDip = -360, VerticalOffsetDip = 12 };
            if (TryTaskbarSlot(docked, width, height, scale, out var slot))
            {
                var adjusted = docked with { HorizontalOffsetDip = Math.Clamp(-360 + (int)Math.Round((left - slot.Left) / scale), -4000, 0) };
                if (TryTaskbarSlot(adjusted, width, height, scale, out _)) return adjusted;
            }
            // Let normal placement fall back above if the taskbar has no safe gap.
            return docked;
        }
        var info = new NativeWindowInterop.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.MonitorInfo>() };
        NativeWindowInterop.GetMonitorInfo(NativeWindowInterop.MonitorFromWindow(taskbar, 1), ref info);
        return settings with
        {
            PositionMode = PositionMode.FixedAbove,
            HorizontalOffsetDip = Math.Clamp((int)Math.Round((left - (info.Work.Right - width)) / scale), -4000, 0),
            VerticalOffsetDip = Math.Clamp((int)Math.Round((bar.Top - top - height) / scale), 0, 160)
        };
    }

    private static bool TryTaskbarSlot(
        MonitorSettings settings,
        int width,
        int height,
        double scale,
        out NativeWindowInterop.Rect slot)
    {
        slot = default;
        if (settings.PositionMode == PositionMode.FixedAbove) return false;

        var taskbar = NativeWindowInterop.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        var data = new NativeWindowInterop.AppBarData { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.AppBarData>() };
        NativeWindowInterop.SHAppBarMessage(NativeWindowInterop.AbmGetTaskbarPos, ref data);
        var taskbarRect = data.Position;
        if (taskbarRect.Width <= 0 || taskbarRect.Height <= 0)
        {
            if (!NativeWindowInterop.GetWindowRect(taskbar, out taskbarRect)) return false;
        }

        var state = new NativeWindowInterop.AppBarData { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.AppBarData>() };
        var appBarState = NativeWindowInterop.SHAppBarMessage(NativeWindowInterop.AbmGetState, ref state).ToUInt64();
        if ((appBarState & NativeWindowInterop.AbsAutoHide) != 0) return false;

        // Only place a widget in the taskbar when Win32 exposes both bounds used to calculate a
        // verified gap between the application list and notification area. Otherwise use the
        // above-taskbar placement; guessed coordinates can cover taskbar controls.
        var listRect = FindVisibleChild(taskbar, TaskListClasses);
        var trayRect = FindVisibleChild(taskbar, TrayClasses);
        if (listRect is null || trayRect is null) return false;
        if (data.Edge is not 1 and not 3) return false; // ABE_TOP and ABE_BOTTOM.
        if (height > taskbarRect.Height) return false;

        var gapStart = listRect.Value.Right;
        var gapEnd = trayRect.Value.Left;
        var padding = (int)Math.Ceiling(8 * scale);
        var offset = (int)Math.Round((settings.HorizontalOffsetDip + 360) * scale);
        var x = gapEnd - padding - width + offset;
        if (x < gapStart + padding || x + width > gapEnd - padding) return false;

        var y = taskbarRect.Top + (taskbarRect.Height - height) / 2;
        slot = new NativeWindowInterop.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        return true;
    }

    private NativeWindowInterop.Rect AboveTaskbar(MonitorSettings settings, int width, int height, double scale)
    {
        var taskbar = NativeWindowInterop.FindWindow("Shell_TrayWnd", null);
        var taskbarData = new NativeWindowInterop.AppBarData { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.AppBarData>() };
        NativeWindowInterop.SHAppBarMessage(NativeWindowInterop.AbmGetTaskbarPos, ref taskbarData);
        var taskbarRect = taskbarData.Position;
        if (taskbarRect.Width <= 0 || taskbarRect.Height <= 0)
        {
            if (taskbar == IntPtr.Zero || !NativeWindowInterop.GetWindowRect(taskbar, out taskbarRect))
            {
                taskbarRect = new NativeWindowInterop.Rect
                {
                    Left = 0,
                    Top = NativeWindowInterop.GetSystemMetrics(NativeWindowInterop.SmCyScreen) - (int)(48 * scale),
                    Right = NativeWindowInterop.GetSystemMetrics(NativeWindowInterop.SmCxScreen),
                    Bottom = NativeWindowInterop.GetSystemMetrics(NativeWindowInterop.SmCyScreen)
                };
            }
        }

        var monitor = NativeWindowInterop.MonitorFromWindow(_hwnd, NativeWindowInterop.MonitorDefaultToPrimary);
        var info = new NativeWindowInterop.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.MonitorInfo>() };
        var work = NativeWindowInterop.GetMonitorInfo(monitor, ref info) ? info.Work : taskbarRect;
        var x = work.Right - width + (int)Math.Round(settings.HorizontalOffsetDip * scale);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        var y = taskbarData.Edge == 1
            ? taskbarRect.Bottom + (int)Math.Round(settings.VerticalOffsetDip * scale)
            : taskbarRect.Top - height - (int)Math.Round(settings.VerticalOffsetDip * scale);
        y = Math.Max(work.Top, y);
        return new NativeWindowInterop.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    private bool IsFullscreenForeground()
    {
        var foreground = NativeWindowInterop.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _hwnd) return false;
        var className = _foregroundClass;
        className.Clear();
        NativeWindowInterop.GetClassName(foreground, className, className.Capacity);
        NativeWindowInterop.GetWindowThreadProcessId(foreground, out var processId);
        if (!NativeWindowInterop.GetWindowRect(foreground, out var foregroundRect)) return false;
        var monitor = NativeWindowInterop.MonitorFromWindow(foreground, NativeWindowInterop.MonitorDefaultToPrimary);
        var info = new NativeWindowInterop.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.MonitorInfo>() };
        if (!NativeWindowInterop.GetMonitorInfo(monitor, ref info)) return false;
        const int tolerance = 2;
        var coversMonitor = Math.Abs(foregroundRect.Left - info.Monitor.Left) <= tolerance &&
               Math.Abs(foregroundRect.Top - info.Monitor.Top) <= tolerance &&
               Math.Abs(foregroundRect.Right - info.Monitor.Right) <= tolerance &&
               Math.Abs(foregroundRect.Bottom - info.Monitor.Bottom) <= tolerance;
        return WidgetVisibilityPolicy.ShouldHide(className.ToString(),
            NativeWindowInterop.IsWindowVisible(foreground), NativeWindowInterop.IsIconic(foreground),
            processId == Environment.ProcessId,
            NativeWindowInterop.IsZoomed(foreground) &&
                (NativeWindowInterop.GetWindowStyle(foreground, NativeWindowInterop.GwlStyle) & NativeWindowInterop.WsCaption) != 0,
            coversMonitor);
    }

    private static NativeWindowInterop.Rect? FindVisibleChild(IntPtr parent, IReadOnlyCollection<string> classNames)
    {
        NativeWindowInterop.Rect? found = null;
        var className = new System.Text.StringBuilder(256);
        NativeWindowInterop.EnumChildCallback callback = (hwnd, _) =>
        {
            className.Clear();
            NativeWindowInterop.GetClassName(hwnd, className, className.Capacity);
            if (classNames.Contains(className.ToString(), StringComparer.Ordinal) &&
                NativeWindowInterop.IsWindowVisible(hwnd) &&
                NativeWindowInterop.GetWindowRect(hwnd, out var rect) && rect.Width > 0 && rect.Height > 0)
            {
                found = rect;
                return false;
            }
            return true;
        };
        NativeWindowInterop.EnumChildWindows(parent, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    private static bool SamePosition(NativeWindowInterop.Rect a, NativeWindowInterop.Rect b) =>
        Math.Abs(a.Left - b.Left) < 3 && Math.Abs(a.Top - b.Top) < 3 && Math.Abs(a.Width - b.Width) < 3 && Math.Abs(a.Height - b.Height) < 3;
}
