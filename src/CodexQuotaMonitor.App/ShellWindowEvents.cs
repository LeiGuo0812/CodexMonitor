using Microsoft.UI.Dispatching;

namespace CodexQuotaMonitor.App;

// Out-of-context WinEvents run on the registering UI thread; no injection or global input hook.
internal sealed class ShellWindowEvents : IDisposable
{
    private readonly NativeWindowInterop.WinEventCallback _callback;
    private readonly IntPtr _foregroundHook;
    private readonly IntPtr _orderHook;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _changed;
    private bool _queued;
    private bool _disposed;
    public bool IsActive => _foregroundHook != IntPtr.Zero && _orderHook != IntPtr.Zero;
    public int Notifications { get; private set; }

    public ShellWindowEvents(Action changed)
    {
        _changed = changed;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _callback = OnEvent;
        // Skip own-process events to avoid feedback from our own SetWindowPos calls.
        _foregroundHook = NativeWindowInterop.SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, _callback, 0, 0, 2);
        _orderHook = NativeWindowInterop.SetWinEventHook(0x8002, 0x8004, IntPtr.Zero, _callback, 0, 0, 2);
    }

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId,
        uint eventThread, uint eventTime)
    {
        if (_disposed || (eventType != 0x0003 && objectId != 0)) return;
        Notifications++;
        // Run now, then once after the shell finishes the current activation/reorder sequence.
        _changed();
        if (_queued) return;
        _queued = _dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
        {
            _queued = false;
            if (!_disposed) _changed();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_foregroundHook != IntPtr.Zero) NativeWindowInterop.UnhookWinEvent(_foregroundHook);
        if (_orderHook != IntPtr.Zero) NativeWindowInterop.UnhookWinEvent(_orderHook);
        GC.KeepAlive(_callback);
    }
}
