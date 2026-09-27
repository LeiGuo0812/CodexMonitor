using System.Text.Json;
using CodexQuotaMonitor.Core;
using Microsoft.Win32;

namespace CodexQuotaMonitor.App;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexQuotaMonitor",
        "settings.json");

    public MonitorSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new MonitorSettings();
            var settings = JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(_settingsPath), JsonOptions);
            return Normalize(settings ?? new MonitorSettings());
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new MonitorSettings();
        }
    }

    public MonitorSettings Save(MonitorSettings settings)
    {
        settings = Normalize(settings);
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporaryPath, _settingsPath, overwrite: true);
        return settings;
    }

    private static MonitorSettings Normalize(MonitorSettings settings) => settings with
    {
        WidgetOpacity = double.IsFinite(settings.WidgetOpacity) ? Math.Clamp(settings.WidgetOpacity, 0.4, 1) : 0.92,
        DetailsOpacity = double.IsFinite(settings.DetailsOpacity) ? Math.Clamp(settings.DetailsOpacity, 0.4, 1) : 0.96,
        FontSize = double.IsFinite(settings.FontSize) ? Math.Clamp(settings.FontSize, 13, 18) : 15,
        HorizontalOffsetDip = Math.Clamp(settings.HorizontalOffsetDip, -4000, 0),
        VerticalOffsetDip = Math.Clamp(settings.VerticalOffsetDip, 0, 160),
        RefreshIntervalSeconds = RefreshSchedule.Normalize(settings.RefreshIntervalSeconds),
        PositionMode = Enum.IsDefined(settings.PositionMode) ? settings.PositionMode : PositionMode.Automatic,
        ThemeMode = Enum.IsDefined(settings.ThemeMode) ? settings.ThemeMode : ThemeMode.FollowSystem,
        ThemePreset = Enum.IsDefined(settings.ThemePreset) ? settings.ThemePreset : ThemePreset.MistWhite
    };
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexQuotaMonitor";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return false; }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null) throw new IOException("无法访问当前用户的启动项。");
        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) throw new InvalidOperationException("启动路径不可用。");
        key.SetValue(ValueName, $"\"{exe}\" --background", RegistryValueKind.String);
    }
}
