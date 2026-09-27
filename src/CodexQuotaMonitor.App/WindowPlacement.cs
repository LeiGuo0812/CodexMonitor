using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexQuotaMonitor.App;

internal static class WindowPlacement
{
    public static void Activate(Window window)
    {
        if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter &&
            presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
            presenter.Restore();
        window.Activate();
        NativeWindowInterop.SetForegroundWindow(WindowNative.GetWindowHandle(window));
    }

    public static void AboveTaskbar(Window window, double widthDip, double heightDip, IntPtr anchor)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var bar = NativeWindowInterop.FindWindow("Shell_TrayWnd", null);
        var info = new NativeWindowInterop.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.MonitorInfo>() };
        if (!NativeWindowInterop.GetMonitorInfo(NativeWindowInterop.MonitorFromWindow(bar, 1), ref info)) return;
        var scale = Math.Max(96, NativeWindowInterop.GetDpiForWindow(hwnd)) / 96.0;
        var width = Math.Min((int)Math.Ceiling(widthDip * scale), info.Work.Width);
        var bottom = info.Work.Bottom;
        if (NativeWindowInterop.GetWindowRect(bar, out var taskbar) && taskbar.Top > info.Work.Top)
            bottom = Math.Min(bottom, taskbar.Top);
        var height = Math.Min((int)Math.Ceiling(heightDip * scale), bottom - info.Work.Top);
        var right = info.Work.Right;
        if (anchor != IntPtr.Zero && NativeWindowInterop.GetWindowRect(anchor, out var point)) right = point.Right;
        var left = Math.Clamp(right - width, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        window.AppWindow.MoveAndResize(new RectInt32(left, bottom - height, width, height));
    }

    public static void Center(Window window, double widthDip, double heightDip)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var monitor = NativeWindowInterop.MonitorFromWindow(hwnd, NativeWindowInterop.MonitorDefaultToPrimary);
        var info = new NativeWindowInterop.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.MonitorInfo>() };
        if (!NativeWindowInterop.GetMonitorInfo(monitor, ref info)) return;
        var scale = Math.Max(96, NativeWindowInterop.GetDpiForWindow(hwnd)) / 96.0;
        var margin = (int)Math.Ceiling(16 * scale);
        var width = Math.Min((int)Math.Ceiling(widthDip * scale), Math.Max(1, info.Work.Width - 2 * margin));
        var height = Math.Min((int)Math.Ceiling(heightDip * scale), Math.Max(1, info.Work.Height - 2 * margin));
        window.AppWindow.MoveAndResize(new RectInt32(info.Work.Left + (info.Work.Width - width) / 2,
            info.Work.Top + (info.Work.Height - height) / 2, width, height));
    }

    public static object ReadVerification(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        NativeWindowInterop.GetWindowRect(hwnd, out var bounds);
        var info = new NativeWindowInterop.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.MonitorInfo>() };
        NativeWindowInterop.GetMonitorInfo(NativeWindowInterop.MonitorFromWindow(hwnd, 1), ref info);
        var scale = Math.Max(96, NativeWindowInterop.GetDpiForWindow(hwnd)) / 96.0;
        var taskbar = NativeWindowInterop.FindWindow("Shell_TrayWnd", null);
        NativeWindowInterop.GetWindowRect(taskbar, out var bar);
        return new { WidthDip = bounds.Width / scale, HeightDip = bounds.Height / scale,
            TaskbarGapDip = (bar.Top - bounds.Bottom) / scale,
            Foreground = NativeWindowInterop.GetForegroundWindow() == hwnd,
            Dpi = scale * 96, Minimized = NativeWindowInterop.IsIconic(hwnd),
            WorkWidthDip = info.Work.Width / scale, WorkHeightDip = info.Work.Height / scale,
            WithinWorkArea = bounds.Left >= info.Work.Left && bounds.Top >= info.Work.Top && bounds.Right <= info.Work.Right && bounds.Bottom <= info.Work.Bottom,
            Centered = Math.Abs((bounds.Left + bounds.Right) - (info.Work.Left + info.Work.Right)) <= 2 &&
                Math.Abs((bounds.Top + bounds.Bottom) - (info.Work.Top + info.Work.Bottom)) <= 2 };
    }
}
