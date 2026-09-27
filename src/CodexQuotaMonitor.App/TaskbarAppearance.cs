using Microsoft.Win32;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CodexQuotaMonitor.App;

// Windows (shell) mode is independent of the application's light/dark mode.
internal sealed record TaskbarAppearance(Color Background, Color Foreground, Color Secondary,
    bool Dark, bool Transparency, bool HighContrast)
{
    // UI-thread only, process lifetime. Reuse WinRT wrappers instead of creating them per tick.
    private static readonly Lazy<UISettings> Ui = new(() => new UISettings());
    private static readonly Lazy<AccessibilitySettings> Accessibility = new(() => new AccessibilitySettings());
    private static TaskbarAppearance? _cached;
    private static long _checkedAt;
    private static bool _appsDark;
    private static bool _advancedEffects;
    private static bool _animations;
    internal static int ReadCount { get; private set; }
    internal static void Invalidate() => _cached = null;
    internal static bool AppsDark { get { Read(); return _appsDark; } }
    internal static bool AdvancedEffects { get { Read(); return _advancedEffects; } }
    internal static bool Animations { get { Read(); return _animations; } }

    public static TaskbarAppearance Read()
    {
        if (_cached is not null && Environment.TickCount64 - _checkedAt < 15000) return _cached;
        _checkedAt = Environment.TickCount64;
        ReadCount++;
        return _cached = ReadCurrent();
    }

    private static TaskbarAppearance ReadCurrent()
    {
        var ui = Ui.Value;
        var highContrast = Accessibility.Value.HighContrast;
        var appBackground = ui.GetColorValue(UIColorType.Background);
        _appsDark = appBackground.R * 0.2126 + appBackground.G * 0.7152 + appBackground.B * 0.0722 < 128;
        _advancedEffects = ui.AdvancedEffectsEnabled;
        _animations = ui.AnimationsEnabled;
        if (highContrast)
            return new(ui.GetColorValue(UIColorType.Background), ui.GetColorValue(UIColorType.Foreground),
                ui.GetColorValue(UIColorType.Foreground), false, false, true);

        using var personalize = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var dark = personalize?.GetValue("SystemUsesLightTheme") is int value ? value == 0 :
            ui.GetColorValue(UIColorType.Background).R < 128;
        var accent = dark && personalize?.GetValue("ColorPrevalence") is int accentValue && accentValue != 0;
        var background = accent ? ui.GetColorValue(UIColorType.AccentDark2) :
            dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        var foreground = dark ? Microsoft.UI.Colors.White : Color.FromArgb(255, 26, 26, 26);
        // Keep auxiliary labels opaque and readable over the shell's translucent material.
        var secondary = dark ? Color.FromArgb(255, 225, 225, 225) : Color.FromArgb(255, 64, 64, 64);
        return new(background, foreground, secondary, dark,
            _advancedEffects && personalize?.GetValue("EnableTransparency") is not 0, false);
    }
}
