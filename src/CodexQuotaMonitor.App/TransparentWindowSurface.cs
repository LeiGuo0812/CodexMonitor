using System.Runtime.InteropServices;

namespace CodexQuotaMonitor.App;

// DWM setup and black WM_ERASEBKGND clearing follow WinUIEx's TransparentTintBackdrop.
// Attribution/license: THIRD-PARTY-NOTICES.md. Owns only this app's widget HWND.
internal sealed class TransparentWindowSurface : IDisposable
{
    private IntPtr _window;
    private readonly SubclassProc _callback;
    private const nuint SubclassId = 0x43514D;
    public int DwmResult { get; private set; }
    public int BackgroundClears { get; private set; }
    public bool Attached => _window != IntPtr.Zero;

    public TransparentWindowSurface(IntPtr window)
    {
        _window = window;
        _callback = WindowProc;
        if (!SetWindowSubclass(window, _callback, SubclassId, 0))
            throw new InvalidOperationException("Cannot attach the transparent widget surface.");
        ConfigureComposition();
        var dc = GetDC(window);
        if (dc != IntPtr.Zero)
        {
            try { ClearBackground(dc); }
            finally { ReleaseDC(window, dc); }
        }
    }

    private void ConfigureComposition()
    {
        var margins = new Margins();
        DwmExtendFrameIntoClientArea(_window, ref margins);
        // An empty off-client region enables per-pixel composition without another blur layer.
        var region = CreateRectRgn(-2, -2, -1, -1);
        if (region == IntPtr.Zero) { DwmResult = unchecked((int)0x8007000E); return; }
        try
        {
            var blur = new BlurBehind { Flags = 3, Enable = 1, Region = region };
            DwmResult = DwmEnableBlurBehindWindow(_window, ref blur);
        }
        finally { DeleteObject(region); }
        var noRounding = 1; // DWMWCP_DONOTROUND; rounded shapes are supplied by XAML when floating.
        DwmSetWindowAttribute(_window, 33, ref noRounding, sizeof(int));
        var noBorder = -2; // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(_window, 34, ref noBorder, sizeof(int));
    }

    private bool ClearBackground(IntPtr dc)
    {
        if (dc == IntPtr.Zero || !GetClientRect(_window, out var rect)) return false;
        // GDI black has zero alpha in the redirection surface. The default light-theme
        // erase paints an opaque-looking white base even when the XAML brush is transparent.
        if (FillRect(dc, ref rect, GetStockObject(4)) == 0) return false; // BLACK_BRUSH (borrowed)
        BackgroundClears++;
        return true;
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0014 && ClearBackground((IntPtr)wParam)) return new IntPtr(1); // WM_ERASEBKGND
        if (message == 0x031E) ConfigureComposition(); // WM_DWMCOMPOSITIONCHANGED
        if (message == 0x0082) // WM_NCDESTROY
        {
            RemoveWindowSubclass(hwnd, _callback, SubclassId);
            _window = IntPtr.Zero;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_window == IntPtr.Zero) return;
        RemoveWindowSubclass(_window, _callback, SubclassId);
        _window = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind { public uint Flags; public int Enable; public IntPtr Region; public int Transition; }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref BlurBehind blur);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeWindowInterop.Rect rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref NativeWindowInterop.Rect rect, IntPtr brush);
}
