using Microsoft.Win32;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace CodexQuotaMonitor.App;

// Windows (shell) mode is independent of the application's light/dark mode.
internal sealed record TaskbarAppearance(Color Background, Color Foreground, Color Secondary,
    bool Dark, bool Transparency, bool HighContrast)
{
    public static TaskbarAppearance Read()
    {
        var ui = new UISettings();
        var highContrast = new AccessibilitySettings().HighContrast;
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
            ui.AdvancedEffectsEnabled && personalize?.GetValue("EnableTransparency") is not 0, false);
    }
}
