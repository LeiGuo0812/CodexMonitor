using System.Net.NetworkInformation;
using CodexQuotaMonitor.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace CodexQuotaMonitor.App;

public sealed class AppRuntime : IDisposable
{
    private static AppRuntime? _current;
    private readonly SettingsStore _settingsStore = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly QuotaMonitorService _monitor;
    private readonly RefreshSchedule _refreshSchedule;
    private readonly NetworkAvailabilityChangedEventHandler _networkAvailabilityHandler;
    private WidgetWindow? _widget;
    private DashboardWindow? _dashboard;
    private SettingsWindow? _settingsWindow;
    private SystemTrayIcon? _tray;
    private MonitorSettings _settings;
    private bool _disposed;
    private bool _clearingCache;

    public AppRuntime()
    {
        _settings = _settingsStore.Load();
        _refreshSchedule = new RefreshSchedule(_settings.RefreshIntervalSeconds);
        _monitor = new QuotaMonitorService(new CodexAppServerQuotaSource(() => _settings.CodexExecutablePath));
        _monitor.StateChanged += OnMonitorStateChanged;
        _networkAvailabilityHandler = (_, args) =>
        {
            if (args.IsAvailable) RefreshNow();
        };
        NetworkChange.NetworkAvailabilityChanged += _networkAvailabilityHandler;
        _current = this;
    }

    public MonitorSettings Settings => _settings;
    public MonitorViewState State => _monitor.Current;
    internal int ActiveRefreshIntervalSeconds => _refreshSchedule.IntervalSeconds;

    public void Start()
    {
        _widget = new WidgetWindow(this);
        _widget.ShowWithoutActivation();
        _tray = new SystemTrayIcon(HandleTrayAction, StartupRegistration.IsEnabled, () => _clearingCache);
        ApplySettingsToWindows();
        _ = Task.Run(RefreshLoopAsync);
    }

    public static void ActivateExistingInstance() => _current?.OpenDetails();

    public void OpenDetails()
    {
        if (_dashboard is not null)
        {
            WindowPlacement.Activate(_dashboard);
            _dashboard.ShowAtTaskbar();
            _widget?.RefreshPlacement();
            RefreshNow();
            return;
        }

        _dashboard = new DashboardWindow(this);
        _dashboard.Closed += (_, _) => _dashboard = null;
        WindowPlacement.Activate(_dashboard);
        _dashboard.ShowAtTaskbar();
        _widget?.RefreshPlacement();
        _ = _monitor.RefreshAsync(_lifetime.Token);
    }

    public void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            WindowPlacement.Activate(_settingsWindow);
            return;
        }

        _settingsWindow = new SettingsWindow(this, _settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        WindowPlacement.Activate(_settingsWindow);
    }

    public void RefreshNow() => _ = _monitor.RefreshAsync(_lifetime.Token);

    internal Task<MonitorViewState> RefreshForVerificationAsync() => _monitor.RefreshAsync(_lifetime.Token);
    internal object? ReadWidgetVerification() => _widget?.ReadVerification();
    internal Task<object> VerifyHoverAsync() => _widget!.VerifyHoverAsync();
    internal object? ReadTrayVerification() => _tray?.ReadVerification();
    internal object? ReadMenuVerification() => _tray?.ReadMenuVerification();
    internal DashboardWindow? DashboardForVerification => _dashboard;
    internal SettingsWindow? SettingsForVerification => _settingsWindow;
    internal IntPtr WidgetHandle => _widget?.Handle ?? IntPtr.Zero;

    internal async Task<object> VerifyDragSnapAsync()
    {
        var original = _settings;
        var hwnd = WidgetHandle;
        NativeWindowInterop.GetWindowRect(hwnd, out var bounds);
        NativeWindowInterop.GetWindowRect(NativeWindowInterop.FindWindow("Shell_TrayWnd", null), out var bar);
        var outside = _widget!.SettingsForDrop(bounds.Left, bar.Top - bounds.Height - 80);
        ApplyPreviewSettings(outside);
        await Task.Delay(1200);
        var inside = _widget.SettingsForDrop(bounds.Left, bar.Top + (bar.Height - bounds.Height) / 2);
        ApplyPreviewSettings(inside);
        await Task.Delay(1200);
        var result = new { DragOutMode = outside.PositionMode.ToString(), DragBackMode = inside.PositionMode.ToString(), Widget = _widget.ReadVerification() };
        ApplyPreviewSettings(original);
        return result;
    }

    public void ApplyPreviewSettings(MonitorSettings settings)
    {
        _settings = settings;
        // Do not reload the editor that originated this preview while its event is running.
        _widget?.ApplySettings(settings);
        _dashboard?.ApplySettings(settings);
    }

    public string? CommitSettings(MonitorSettings settings)
    {
        _settings = _settingsStore.Save(settings);
        _refreshSchedule.SetInterval(_settings.RefreshIntervalSeconds);
        string? warning = null;
        // Preview already contains the new toggle value. Always sync on save.
        try { StartupRegistration.SetEnabled(_settings.StartWithWindows); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException or System.Security.SecurityException)
        {
            warning = "设置已保存，但开机启动注册失败；请检查当前用户注册表权限。";
        }
        ApplySettingsToWindows();
        return warning;
    }

    public void UpdateDraggedPosition(int leftPx, int topPx, bool wasInTaskbar)
    {
        _settings = _widget?.SettingsForDrop(leftPx, topPx) ?? _settings;
        _settings = _settingsStore.Save(_settings);
        ApplySettingsToWindows();
    }

    public void SetPositionMode(PositionMode mode) => CommitSettings(_settings with { PositionMode = mode });
    public void SetThemePreset(ThemePreset preset) => CommitSettings(_settings with
    {
        ThemePreset = preset,
        ThemeMode = preset is ThemePreset.Graphite or ThemePreset.Midnight ? ThemeMode.Dark : ThemeMode.Light
    });

    public void ShowContextMenu() => _tray?.ShowContextMenu();

    internal string? ToggleStartup(bool notify = true)
    {
        var enabled = !StartupRegistration.IsEnabled();
        var persisted = _settingsStore.Load();
        try
        {
            StartupRegistration.SetEnabled(enabled);
            try { _settingsStore.Save(persisted with { StartWithWindows = enabled }); }
            catch
            {
                StartupRegistration.SetEnabled(!enabled);
                throw;
            }
            // Preserve unsaved appearance edits, and keep Cancel from undoing this menu action.
            _settings = _settings with { StartWithWindows = enabled };
            _settingsWindow?.SyncStartupSetting(enabled);
            if (notify) _tray?.Notify("开机启动", enabled ? "已开启，登录 Windows 后自动运行。" : "已关闭开机启动。");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            const string error = "开机启动未能更新，请检查当前用户的设置文件和启动项权限。";
            if (notify) _tray?.Notify("开机启动", error);
            return error;
        }
    }

    internal async Task<bool> ClearCachesAsync(bool notify = true)
    {
        if (_clearingCache) return false;
        _clearingCache = true;
        try
        {
            await Task.Run(CacheMaintenance.ClearAsync);
            if (notify) _tray?.Notify("缓存清理完成", "已清理未使用的缓存；当前运行库将在正常退出后清理。设置及 Codex 登录信息保留。");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (notify) _tray?.Notify("缓存暂未完全清理", "程序可继续运行，将在正常退出时重试清理。设置及 Codex 登录信息保留。");
            return false;
        }
        finally { _clearingCache = false; }
    }

    public void ExitApplication() => Application.Current.Exit();

    public void ApplySettingsToWindows()
    {
        _widget?.ApplySettings(_settings);
        _dashboard?.ApplySettings(_settings);
        _settingsWindow?.ApplySettings(_settings);
        _tray?.Update(_monitor.Current);
    }

    private async Task RefreshLoopAsync()
    {
        var failures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            var state = await _monitor.RefreshAsync(_lifetime.Token).ConfigureAwait(false);
            failures = state.QueryError is null ? 0 : Math.Min(failures + 1, 5);
            try { await _refreshSchedule.WaitAsync(failures, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnMonitorStateChanged(MonitorViewState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _widget?.Update(state);
            _dashboard?.Update(state);
            _tray?.Update(state);
        });
    }

    private void HandleTrayAction(TrayAction action)
    {
        switch (action)
        {
            case TrayAction.OpenDetails: OpenDetails(); break;
            case TrayAction.Refresh: RefreshNow(); break;
            case TrayAction.OpenSettings: OpenSettings(); break;
            case TrayAction.AutomaticPosition: SetPositionMode(PositionMode.Automatic); break;
            case TrayAction.TaskbarPreferredPosition: SetPositionMode(PositionMode.TaskbarPreferred); break;
            case TrayAction.FixedAbovePosition: SetPositionMode(PositionMode.FixedAbove); break;
            case TrayAction.MistWhiteTheme: SetThemePreset(ThemePreset.MistWhite); break;
            case TrayAction.WarmSandTheme: SetThemePreset(ThemePreset.WarmSand); break;
            case TrayAction.GraphiteTheme: SetThemePreset(ThemePreset.Graphite); break;
            case TrayAction.MidnightTheme: SetThemePreset(ThemePreset.Midnight); break;
            case TrayAction.ToggleStartup: ToggleStartup(); break;
            case TrayAction.ClearCache: _ = ClearCachesAsync(); break;
            case TrayAction.Exit: ExitApplication(); break;
        }
    }

    private static NativeWindowInterop.Rect GetTaskbarRect()
    {
        var data = new NativeWindowInterop.AppBarData { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeWindowInterop.AppBarData>() };
        NativeWindowInterop.SHAppBarMessage(NativeWindowInterop.AbmGetTaskbarPos, ref data);
        if (data.Position.Width > 0 && data.Position.Height > 0) return data.Position;
        var taskbar = NativeWindowInterop.FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero && NativeWindowInterop.GetWindowRect(taskbar, out var rect)) return rect;
        return new NativeWindowInterop.Rect { Top = NativeWindowInterop.GetSystemMetrics(NativeWindowInterop.SmCyScreen) - 48 };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= _networkAvailabilityHandler;
        _lifetime.Cancel();
        _tray?.Dispose();
        _tray = null;
        _monitor.StateChanged -= OnMonitorStateChanged;
        _current = null;
        _lifetime.Dispose();
    }
}
