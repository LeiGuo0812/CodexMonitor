using Microsoft.UI.Xaml;

namespace CodexQuotaMonitor.App;

public sealed partial class App : Application
{
    private AppRuntime? _runtime;
    private readonly string[] _arguments;

    public App(string[]? arguments = null)
    {
        _arguments = arguments ?? [];
        // MRT otherwise derives the PRI name from the host EXE. Downloads can be renamed.
        ResourceManagerRequested += (_, request) => request.CustomResourceManager =
            new Microsoft.Windows.ApplicationModel.Resources.ResourceManager(
                Path.Combine(AppContext.BaseDirectory, "CodexQuotaMonitor.pri"));
        InitializeComponent();
        UnhandledException += (_, args) => UiFailureLog.Record(args.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _runtime?.Dispose();
            CacheMaintenance.OnApplicationExit();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _runtime = new AppRuntime();
        _runtime.Start();
        if (_arguments is ["--verify-runtime", var reportPath])
            _ = RuntimeVerification.RunAsync(_runtime, reportPath);
    }

}
