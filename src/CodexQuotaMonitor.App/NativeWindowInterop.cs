using System.Runtime.InteropServices;
using System.Text;

namespace CodexQuotaMonitor.App;

internal static class NativeWindowInterop
{
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const long WsCaption = 0x00C00000L;
    internal const long WsThickFrame = 0x00040000L;
    internal const long WsSystemMenu = 0x00080000L;
    internal const long WsMinimizeBox = 0x00020000L;
    internal const long WsMaximizeBox = 0x00010000L;
    internal const long WsPopup = unchecked((long)0x80000000);
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;
    internal const int SwShownoactivate = 4;
    internal const uint MonitorDefaultToPrimary = 1;
    internal const uint AbmGetTaskbarPos = 0x00000005;
    internal const uint AbmGetState = 0x00000004;
    internal const uint AbsAutoHide = 0x00000001;
    internal const int SmCxScreen = 0;
    internal const int SmCyScreen = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AppBarData
    {
        public uint Size;
        public IntPtr Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Position;
        public IntPtr Parameter;
    }

    internal delegate bool EnumChildCallback(IntPtr hwnd, IntPtr lParam);

    internal delegate void WinEventCallback(IntPtr hook, uint eventType, IntPtr hwnd,
        int objectId, int childId, uint eventThread, uint eventTime);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventCallback callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    internal static extern bool EnumChildWindows(IntPtr parent, EnumChildCallback callback, IntPtr lParam);

    [DllImport("shell32.dll", EntryPoint = "SHAppBarMessage")]
    internal static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

    internal static long GetWindowStyle(IntPtr hwnd, int index) => GetWindowLongPtr(hwnd, index).ToInt64();

    internal static void SetWindowStyle(IntPtr hwnd, int index, long style) =>
        SetWindowLongPtr(hwnd, index, new IntPtr(style));
}
