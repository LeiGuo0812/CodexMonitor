using CodexQuotaMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace CodexQuotaMonitor.App;

internal static class WindowAppearance
{
    private static MonitorSettings? _paletteSettings;
    private static bool _paletteDark;
    private static ThemeColors? _palette;

    public static ThemeColors Colors(MonitorSettings settings)
    {
        var dark = settings.ThemeMode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => IsSystemDark()
        };
        if (_palette is not null && _paletteSettings == settings && _paletteDark == dark) return _palette;
        _paletteSettings = settings;
        _paletteDark = dark;
        return _palette = settings.ThemePreset switch
        {
            ThemePreset.MistWhite when dark => new("#172D44", "#EAF5FF", "#B0CBE1", "#7EBEFF", "#C8A1FF"),
            ThemePreset.WarmSand when dark => new("#38251D", "#FFF1DF", "#D7B99A", "#F2B16D", "#6ED8E1"),
            ThemePreset.Graphite when !dark => new("#DCEAE4", "#18352C", "#506C61", "#19785C", "#986B23"),
            ThemePreset.Midnight when !dark => new("#E9DEFC", "#31234B", "#716084", "#7950BD", "#157F84"),
            _ => ThemeColors.For(settings)
        };
    }

    public static void ApplyGlass(Window window, MonitorSettings settings)
    {
        var colors = Colors(settings);
        var tint = ParseColor(colors.Background, Microsoft.UI.Colors.Gray);
        if (GlassBackdrop.IsAvailable)
        {
            if (window.SystemBackdrop is not GlassBackdrop glass)
            {
                glass = new GlassBackdrop();
                glass.Update(tint, settings.DetailsOpacity);
                window.SystemBackdrop = glass;
            }
            else glass.Update(tint, settings.DetailsOpacity);
        }
        else window.SystemBackdrop = null;

        window.AppWindow.TitleBar.BackgroundColor = window.ExtendsContentIntoTitleBar ? Microsoft.UI.Colors.Transparent : tint;
        window.AppWindow.TitleBar.InactiveBackgroundColor = window.ExtendsContentIntoTitleBar ? Microsoft.UI.Colors.Transparent : tint;
        window.AppWindow.TitleBar.ForegroundColor = ParseColor(colors.PrimaryText, Microsoft.UI.Colors.Black);
        window.AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        window.AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        window.AppWindow.TitleBar.ButtonForegroundColor = ParseColor(colors.PrimaryText, Microsoft.UI.Colors.Black);
        window.AppWindow.TitleBar.ButtonInactiveForegroundColor = ParseColor(colors.SecondaryText, Microsoft.UI.Colors.Gray);
    }

    public static void ApplyTheme(FrameworkElement element, MonitorSettings settings)
    {
        var color = ParseColor(Colors(settings).Background, Microsoft.UI.Colors.White);
        element.RequestedTheme = (color.R * 0.2126 + color.G * 0.7152 + color.B * 0.0722) < 128
            ? ElementTheme.Dark : ElementTheme.Light;
    }

    public static SolidColorBrush Translucent(string color, double opacity)
    {
        var parsed = ParseColor(color, Microsoft.UI.Colors.White);
        return new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), parsed.R, parsed.G, parsed.B));
    }

    public static LinearGradientBrush GlassSurface(ThemeColors colors, double opacity)
    {
        var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = Translucent(colors.Background, opacity).Color });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = Translucent(colors.Accent, opacity * 0.16).Color });
        return gradient;
    }

    public static LinearGradientBrush PanelSurface(ThemeColors colors, string tint)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Translucent(tint, 0.22).Color });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Translucent(colors.Background, 0.48).Color });
        return brush;
    }

    public static void ApplyBackdrop(Window window)
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
                TaskbarAppearance.AdvancedEffects && !TaskbarAppearance.Read().HighContrast)
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
                return;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // If the OS setting or system backdrop is unavailable, retain the opaque color surface.
        }
        window.SystemBackdrop = null;
    }

    public static void ApplyTheme(FrameworkElement element, ThemeMode mode)
    {
        element.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    public static Color ParseColor(string text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var value = text.Trim().TrimStart('#');
        if (value.Length != 6 || !byte.TryParse(value.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var red) ||
            !byte.TryParse(value.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var green) ||
            !byte.TryParse(value.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
            return fallback;
        return Color.FromArgb(255, red, green, blue);
    }

    public static SolidColorBrush BackgroundBrush(string color, double opacity, Color fallback)
    {
        var parsed = ParseColor(color, fallback);
        var alpha = (byte)Math.Round(255 * Math.Clamp(opacity, 0.4, 1));
        return new SolidColorBrush(Color.FromArgb(alpha, parsed.R, parsed.G, parsed.B));
    }

    private static bool IsSystemDark()
    {
        try
        {
            return TaskbarAppearance.AppsDark;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
