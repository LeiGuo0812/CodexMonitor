using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace CodexQuotaMonitor.App;

internal static class Program
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _activationSignal;
    private static readonly CancellationTokenSource Shutdown = new();

    [STAThread]
    private static void Main(string[] args)
    {
        const string mutexName = "Local\\CodexQuotaMonitor.SingleInstance";
        const string signalName = "Local\\CodexQuotaMonitor.Activate";
        _mutex = new Mutex(initiallyOwned: true, mutexName, out var isPrimary);
        if (!isPrimary)
        {
            try { EventWaitHandle.OpenExisting(signalName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        _activationSignal = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initializationParams =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App(args);
            _ = Task.Run(() =>
            {
                var handles = new WaitHandle[] { _activationSignal!, Shutdown.Token.WaitHandle };
                while (WaitHandle.WaitAny(handles) == 0)
                {
                    dispatcher.TryEnqueue(AppRuntime.ActivateExistingInstance);
                }
            });
        });
        Shutdown.Cancel();
        _activationSignal?.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
