using CodexQuotaMonitor.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace CodexQuotaMonitor.App;

public sealed partial class SettingsWindow : Window
{
    private readonly AppRuntime _runtime;
    private MonitorSettings _original;
    private MonitorSettings _draft;
    private string? _verifiedPath;
    // XAML setters (for example Slider.Minimum) can raise events during InitializeComponent.
    private bool _loading = true;
    private bool _saved;
    private bool _closed;
    private bool _previewQueued;

    public SettingsWindow(AppRuntime runtime, MonitorSettings settings)
    {
        _runtime = runtime;
        _original = settings;
        _draft = settings;
        InitializeComponent();
        Closed += (_, _) =>
        {
            _closed = true;
            if (!_saved) _runtime.ApplyPreviewSettings(_original);
            SystemBackdrop = null;
        };
        LoadControls(settings);
        WindowPlacement.Center(this, 600, 760);
    }

    public void ApplySettings(MonitorSettings settings)
    {
        if (_closed) return;
        _draft = settings;
        LoadControls(settings);
    }

    internal void SyncStartupSetting(bool enabled)
    {
        if (_closed) return;
        _original = _original with { StartWithWindows = enabled };
        _draft = _draft with { StartWithWindows = enabled };
        _loading = true;
        StartupSwitch.IsOn = enabled;
        _loading = false;
    }

    private void LoadControls(MonitorSettings settings)
    {
        _loading = true;
        PositionModeBox.SelectedIndex = (int)settings.PositionMode;
        ThemeModeBox.SelectedIndex = (int)settings.ThemeMode;
        ThemePresetBox.SelectedIndex = (int)settings.ThemePreset;
        WidgetOpacitySlider.Value = settings.WidgetOpacity;
        DetailsOpacitySlider.Value = settings.DetailsOpacity;
        FontSizeSlider.Value = settings.FontSize;
        HorizontalOffsetSlider.Value = settings.HorizontalOffsetDip;
        VerticalOffsetSlider.Value = settings.VerticalOffsetDip;
        BackgroundColorBox.Text = settings.CustomBackground;
        PrimaryColorBox.Text = settings.CustomPrimaryText;
        SecondaryColorBox.Text = settings.CustomSecondaryText;
        AccentColorBox.Text = settings.CustomAccent;
        CodexPathBox.Text = settings.CodexExecutablePath ?? string.Empty;
        StartupSwitch.IsOn = settings.StartWithWindows;
        _loading = false;
        UpdatePreview();
    }

    private void SettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ReferenceEquals(sender, ThemePresetBox) && ThemePresetBox.SelectedIndex < 4)
        {
            _loading = true;
            ThemeModeBox.SelectedIndex = ThemePresetBox.SelectedIndex >= 2 ? (int)ThemeMode.Dark : (int)ThemeMode.Light;
            _loading = false;
        }
        UpdateDraftFromControls();
    }
    private void SettingChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdateDraftFromControls();
    private void SettingChanged(object sender, RoutedEventArgs e) => UpdateDraftFromControls();

    private void UpdateDraftFromControls()
    {
        if (_loading) return;
        var colors = ReadCustomColors(out var colorsValid);
        if (!colorsValid)
        {
            ContrastHint.Text = "请使用 #RRGGBB 格式的六位十六进制颜色值。";
            return;
        }

        _draft = _draft with
        {
            PositionMode = (PositionMode)Math.Max(0, PositionModeBox.SelectedIndex),
            ThemeMode = (ThemeMode)Math.Max(0, ThemeModeBox.SelectedIndex),
            ThemePreset = (ThemePreset)Math.Max(0, ThemePresetBox.SelectedIndex),
            WidgetOpacity = WidgetOpacitySlider.Value,
            DetailsOpacity = DetailsOpacitySlider.Value,
            FontSize = FontSizeSlider.Value,
            HorizontalOffsetDip = (int)Math.Round(HorizontalOffsetSlider.Value),
            VerticalOffsetDip = (int)Math.Round(VerticalOffsetSlider.Value),
            CustomBackground = colors.Background,
            CustomPrimaryText = colors.Primary,
            CustomSecondaryText = colors.Secondary,
            CustomAccent = colors.Accent,
            CodexExecutablePath = string.IsNullOrWhiteSpace(CodexPathBox.Text) ? null : CodexPathBox.Text.Trim(),
            StartWithWindows = StartupSwitch.IsOn
        };
        QueuePreview();
    }

    private void QueuePreview()
    {
        if (_previewQueued || _closed) return;
        _previewQueued = DispatcherQueue.TryEnqueue(() =>
        {
            _previewQueued = false;
            if (_closed || _saved) return;
            _runtime.ApplyPreviewSettings(_draft);
            UpdatePreview();
        });
    }

    private void ColorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) UpdateDraftFromControls();
    }

    private void CodexPathTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _verifiedPath = null;
        UpdateDraftFromControls();
        CodexPathStatus.Text = string.IsNullOrWhiteSpace(CodexPathBox.Text)
            ? "自动查找 Codex CLI"
            : "路径待验证；保存时会执行 codex.exe --version。";
    }

    private void UpdatePreview()
    {
        var colors = WindowAppearance.Colors(_draft);
        var background = WindowAppearance.ParseColor(colors.Background, Color.FromArgb(255, 232, 237, 243));
        var primary = WindowAppearance.ParseColor(colors.PrimaryText, Colors.Black);
        var secondary = WindowAppearance.ParseColor(colors.SecondaryText, Colors.Gray);
        var taskbar = TaskbarAppearance.Read();
        PreviewSurface.Background = new SolidColorBrush(taskbar.Background);
        PreviewMain.Foreground = new SolidColorBrush(taskbar.Foreground);
        PreviewReset.Foreground = new SolidColorBrush(taskbar.Secondary);
        PreviewTag.Foreground = new SolidColorBrush(taskbar.HighContrast ? taskbar.Foreground :
            WindowAppearance.ParseColor(colors.Accent, Colors.Blue));
        PreviewState.Foreground = new SolidColorBrush(taskbar.HighContrast ? taskbar.Foreground : Color.FromArgb(255, 49, 150, 108));
        PreviewMain.FontSize = _draft.FontSize;
        WindowAppearance.ApplyTheme(SettingsRoot, _draft);
        WindowAppearance.ApplyGlass(this, _draft);
        SettingsRoot.Background = WindowAppearance.Translucent(colors.Background, SystemBackdrop is GlassBackdrop ? 0.1 : 1);
        SettingsRoot.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(primary));
        var primaryContrast = ContrastRatio(background, primary);
        var secondaryContrast = ContrastRatio(background, secondary);
        ContrastHint.Text = primaryContrast < 4.5 || secondaryContrast < 3
            ? $"可读性偏低：主文字对比度 {primaryContrast:0.0}:1，辅助文字 {secondaryContrast:0.0}:1。请调亮文字或加深背景。"
            : $"详情颜色对比度良好：主文字 {primaryContrast:0.0}:1，辅助文字 {secondaryContrast:0.0}:1。数字条正文跟随任务栏，标签保留主题强调色。";
    }

    private (string Background, string Primary, string Secondary, string Accent) ReadCustomColors(out bool valid)
    {
        var background = BackgroundColorBox.Text.Trim();
        var primary = PrimaryColorBox.Text.Trim();
        var secondary = SecondaryColorBox.Text.Trim();
        var accent = AccentColorBox.Text.Trim();
        valid = ValidColor(background) && ValidColor(primary) && ValidColor(secondary) && ValidColor(accent);
        return (background, primary, secondary, accent);
    }

    private static bool ValidColor(string value)
    {
        value = value.Trim().TrimStart('#');
        return value.Length == 6 && value.All(Uri.IsHexDigit);
    }

    private static double ContrastRatio(Color background, Color foreground)
    {
        var first = RelativeLuminance(background);
        var second = RelativeLuminance(foreground);
        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255.0;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private async void BrowseCodexButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        CodexPathBox.Text = file.Path;
        var version = await VerifyCodexCliAsync(file.Path);
        if (version is null)
        {
            CodexPathStatus.Text = "所选程序未通过 codex --version 检查。请选 Codex CLI 的 codex.exe。";
            return;
        }
        _verifiedPath = Path.GetFullPath(file.Path);
        CodexPathStatus.Text = $"路径已验证：{version}";
        UpdateDraftFromControls();
    }

    private async Task<string?> VerifyCodexCliAsync(string path)
    {
        return await CodexExecutableLocator.ReadCliVersionAsync(path, CancellationToken.None);
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _ = ReadCustomColors(out var colorsValid);
        if (!colorsValid)
        {
            CodexPathStatus.Text = "请先修正自定义颜色格式，再保存设置。";
            return;
        }
        UpdateDraftFromControls();
        if (!string.IsNullOrWhiteSpace(_draft.CodexExecutablePath))
        {
            var normalized = Path.GetFullPath(_draft.CodexExecutablePath);
            if (!StringComparer.OrdinalIgnoreCase.Equals(normalized, _verifiedPath))
            {
                var version = await VerifyCodexCliAsync(normalized);
                if (version is null)
                {
                    CodexPathStatus.Text = "Codex CLI 路径验证失败。请更正路径，或留空以启用自动查找。";
                    return;
                }
                _verifiedPath = normalized;
            }
        }

        var warning = _runtime.CommitSettings(_draft);
        _saved = true;
        if (warning is not null)
        {
            CodexPathStatus.Text = warning;
            var dialog = new ContentDialog
            {
                Title = "设置已保存",
                Content = warning,
                CloseButtonText = "确定",
                XamlRoot = SettingsPage.XamlRoot
            };
            await dialog.ShowAsync();
        }
        Close();
    }

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        ClearCacheButton.IsEnabled = false;
        CacheStatus.Text = "正在清理缓存…";
        try
        {
            var success = await _runtime.ClearCachesAsync(notify: false);
            if (!_closed) CacheStatus.Text = success
                ? "已清理未使用的缓存和异常日志；当前运行库将在正常退出后清理。程序可继续使用，设置已保留。"
                : "部分缓存暂未清理或已有清理任务，将在正常退出时重试。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            if (!_closed) CacheStatus.Text = "部分缓存暂未清理，将在正常退出时重试。也可退出后按 README 中的路径手动清理。";
        }
        finally { if (!_closed) ClearCacheButton.IsEnabled = true; }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _saved = true;
        _runtime.ApplyPreviewSettings(_original);
        Close();
    }

    private void DefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        _draft = new MonitorSettings();
        LoadControls(_draft);
        _runtime.ApplyPreviewSettings(_draft);
    }

    internal async Task<object> VerifyPresetPreviewAsync()
    {
        var distinct = new HashSet<string>();
        var widgetPalettes = new HashSet<string>();
        var tagColors = new HashSet<string>();
        for (var index = 0; index < 24; index++)
        {
            ThemePresetBox.SelectedIndex = index % 5;
            ThemeModeBox.SelectedIndex = index % 3;
            distinct.Add(WindowAppearance.Colors(_draft).Background);
            WidgetOpacitySlider.Value = 0.4 + index % 7 * 0.1;
            DetailsOpacitySlider.Value = 0.4 + index % 7 * 0.1;
            FontSizeSlider.Value = 13 + index % 6;
            await Task.Delay(45); // Let composition, layout and event callbacks run between edits.
            var widget = System.Text.Json.JsonSerializer.SerializeToElement(_runtime.ReadWidgetVerification());
            widgetPalettes.Add(widget.GetProperty("PaletteSignature").GetString()!);
            tagColors.Add(widget.GetProperty("TagColor").GetString()!);
        }
        var result = new { Opened = true, EditCycles = 24, DistinctPresets = distinct.Count,
            WidgetPaletteCount = widgetPalettes.Count,
            TagColorCount = tagColors.Count,
            PreviewVisible = PreviewMain.Text.Length > 0, Bounds = WindowPlacement.ReadVerification(this),
            FooterOutsideScroller = ReferenceEquals(SettingsActions.Parent, SettingsRoot),
            FooterHasLayout = SettingsActions.ActualHeight > 0 };
        CancelButton_Click(this, new RoutedEventArgs());
        return result;
    }
}
