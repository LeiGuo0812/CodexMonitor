using System.Text.Json;

namespace CodexQuotaMonitor.App;

// Opt-in local verification for the real WinUI dispatcher and controls.
// Reports contain only availability, errors and layout measurements, never account or quota values.
internal static class RuntimeVerification
{
    public static async Task RunAsync(AppRuntime runtime, string reportPath)
    {
        object report;
        DashboardWindow? dashboard = null;
        try
        {
            var original = runtime.Settings;
            var startup = VerifyStartup(runtime);
            runtime.OpenDetails();
            dashboard = runtime.DashboardForVerification!;
            var settingsChecks = new List<object>();
            for (var pass = 0; pass < 3; pass++)
            {
                runtime.OpenSettings();
                await Task.Delay(250);
                settingsChecks.Add(await runtime.SettingsForVerification!.VerifyPresetPreviewAsync());
            }
            var first = await runtime.RefreshForVerificationAsync();
            await Task.Delay(1200);
            var second = await runtime.RefreshForVerificationAsync();
            await Task.Delay(1500); // Drain the dispatcher and allow native position ticks.
            runtime.OpenDetails();
            dashboard = runtime.DashboardForVerification!;
            dashboard.Update(second);
            await Task.Delay(1000);
            var wide = dashboard.ReadVerification();
            WindowPlacement.AboveTaskbar(dashboard, 360, 740, runtime.WidgetHandle);
            await Task.Delay(350);
            var narrow = dashboard.ReadVerification();
            dashboard.ShowAtTaskbar();
            await Task.Delay(350);
            // The user may be working in a full-screen app. Bring our own window through
            // its normal restore path before sampling the non-full-screen visibility case.
            if (dashboard.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter beforeDrag)
                beforeDrag.Minimize();
            runtime.OpenDetails();
            await Task.Delay(1200);
            var dragSnap = await runtime.VerifyDragSnapAsync();
            var hover = await runtime.VerifyHoverAsync();
            var samples = new List<object?>();
            for (var index = 0; index < 6; index++)
            {
                samples.Add(runtime.ReadWidgetVerification());
                await Task.Delay(1000);
            }
            // Exercise the same restore path as reopening a minimized details window.
            if (dashboard.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                presenter.Minimize();
            runtime.OpenDetails();
            await Task.Delay(350);
            await runtime.RefreshForVerificationAsync();
            await Task.Delay(250);
            await CacheMaintenance.ClearAsync();
            var activeBundle = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var activePreserved = Directory.Exists(activeBundle);
            report = new
            {
                Startup = startup,
                Cache = new { ActivePreserved = activePreserved, CleanupRequested = true,
                    Bundled = CacheMaintenance.CacheRoots().Any(root => CacheMaintenance.IsOwnedBundle(root, activeBundle)),
                    ActiveDirectory = activeBundle },
                FirstError = first.QueryError,
                SecondError = second.QueryError,
                RefreshedAgain = second.LastSuccessfulUpdate > first.LastSuccessfulUpdate,
                HasCreditCount = second.ResetCredits.AvailableCount is not null,
                HasCreditDetails = second.ResetCredits.Credits.Count > 0,
                SettingsChecks = settingsChecks,
                PreviewCancelRestoredSettings = runtime.Settings == original,
                Dashboard = dashboard.ReadVerification(),
                WideLayout = wide,
                NarrowLayout = narrow,
                DragSnap = dragSnap,
                Hover = hover,
                Tray = runtime.ReadTrayVerification(),
                FinalWidget = runtime.ReadWidgetVerification(),
                WidgetSamples = samples
            };
        }
        catch (Exception exception)
        {
            report = new { VerificationError = exception.GetType().Name, exception.HResult,
                MaintenanceError = exception is IOException ? exception.Message : null,
                exception.StackTrace, InnerError = exception.InnerException?.GetType().Name,
                InnerStack = exception.InnerException?.StackTrace };
        }
        finally
        {
            dashboard?.Close();
        }
        try
        {
            await File.WriteAllTextAsync(Path.GetFullPath(reportPath),
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            runtime.ExitApplication();
        }
    }

    private static object VerifyStartup(AppRuntime runtime)
    {
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string name = "CodexQuotaMonitor";
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runPath, writable: true);
        var previousValue = key.GetValue(name);
        var previousKind = previousValue is null ? Microsoft.Win32.RegistryValueKind.String : key.GetValueKind(name);
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaMonitor", "settings.json");
        var previousFile = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var original = runtime.Settings;
        try
        {
            var enabled = original with { StartWithWindows = true };
            runtime.ApplyPreviewSettings(enabled);
            var enableWarning = runtime.CommitSettings(enabled);
            var hostPathRegistered = Equals(key.GetValue(name), $"\"{Environment.ProcessPath}\" --background");
            var disabled = original with { StartWithWindows = false };
            runtime.ApplyPreviewSettings(disabled);
            var disableWarning = runtime.CommitSettings(disabled);
            return new { HostPathRegistered = hostPathRegistered, Disabled = key.GetValue(name) is null,
                SavedAfterPreview = enableWarning is null && disableWarning is null };
        }
        finally
        {
            if (previousValue is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, previousValue, previousKind);
            if (previousFile is null) File.Delete(path);
            else File.WriteAllBytes(path, previousFile);
            runtime.ApplyPreviewSettings(original);
        }
    }
}
