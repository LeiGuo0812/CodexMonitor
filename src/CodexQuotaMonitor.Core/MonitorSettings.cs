namespace CodexQuotaMonitor.Core;

public enum PositionMode
{
    Automatic,
    TaskbarPreferred,
    FixedAbove
}

public enum ThemeMode
{
    FollowSystem,
    Light,
    Dark
}

public enum ThemePreset
{
    MistWhite,
    WarmSand,
    Graphite,
    Midnight,
    Custom
}

public sealed record MonitorSettings
{
    public string? CodexExecutablePath { get; init; }
    public PositionMode PositionMode { get; init; } = PositionMode.Automatic;
    public ThemeMode ThemeMode { get; init; } = ThemeMode.FollowSystem;
    public ThemePreset ThemePreset { get; init; } = ThemePreset.MistWhite;
    public string CustomBackground { get; init; } = "#E8EDF3";
    public string CustomPrimaryText { get; init; } = "#17212D";
    public string CustomSecondaryText { get; init; } = "#526174";
    public string CustomAccent { get; init; } = "#3B73C5";
    public double WidgetOpacity { get; init; } = 0.92;
    public double DetailsOpacity { get; init; } = 0.96;
    public double FontSize { get; init; } = 15;
    public int HorizontalOffsetDip { get; init; } = -360;
    public int VerticalOffsetDip { get; init; } = 12;
    public int RefreshIntervalSeconds { get; init; } = RefreshSchedule.DefaultSeconds;
    public bool StartWithWindows { get; init; }
}

public sealed record ThemeColors(string Background, string PrimaryText, string SecondaryText, string Accent,
    string WeeklyAccent = "#8653D9")
{
    public static ThemeColors For(MonitorSettings settings) => settings.ThemePreset switch
    {
        ThemePreset.MistWhite => new("#E9F3FF", "#12263F", "#506887", "#256FD4", "#8854CF"),
        ThemePreset.WarmSand => new("#F5E2C9", "#3B291D", "#795B43", "#A04F21", "#197F93"),
        ThemePreset.Graphite => new("#202828", "#EFF8F4", "#AEC5BE", "#63D7B1", "#E5AF65"),
        ThemePreset.Midnight => new("#191B39", "#F1EDFF", "#BCB3DE", "#BA9FFF", "#50D9D0"),
        _ => new(settings.CustomBackground, settings.CustomPrimaryText, settings.CustomSecondaryText, settings.CustomAccent)
    };
}

public sealed record WidgetPresentation(
    string MainLine,
    string WindowTag,
    string ResetLine,
    string StateMarker,
    string Tooltip);

public static class WidgetPresentationBuilder
{
    public static WidgetPresentation Create(
        QuotaSelection selection,
        DateTimeOffset now,
        DateTimeOffset? lastSuccessfulUpdate,
        string? queryError = null)
    {
        var window = selection.MainWindow;
        var main = window?.RemainingLabel ?? "剩余 --";
        var tag = window?.Kind switch
        {
            QuotaWindowKind.FiveHour => "5h",
            QuotaWindowKind.Week => "周",
            QuotaWindowKind.Other => "其他",
            _ => ""
        };
        var reset = QuotaFormatting.FormatCountdown(window?.ResetsAt, now);
        var marker = queryError is not null
            ? "连接异常"
            : selection.IsStale
                ? "数据陈旧"
                : lastSuccessfulUpdate is null
                    ? "等待数据"
                    : "已连接";

        var fullReset = QuotaFormatting.FormatFullLocalDate(window?.ResetsAt);
        var updated = lastSuccessfulUpdate is null
            ? "尚无成功更新"
            : $"{QuotaFormatting.FormatFullLocalDate(lastSuccessfulUpdate)}";
        var detail = selection.Status switch
        {
            QuotaSelectionStatus.MissingPreviouslyKnownWindow => "此前的 5 小时额度字段暂缺，保留上次额度并等待确认",
            QuotaSelectionStatus.AccountChanged => "Codex 账户已变化，正在使用新账户数据",
            _ => queryError is null ? "连接正常" : "查询失败"
        };
        var tooltip = $"{window?.Name ?? "额度未知"} · 自然重置：{fullReset}\n更新时间：{updated}\n状态：{detail}";
        return new WidgetPresentation(main, tag, $"重置 {reset}", marker, tooltip);
    }
}
