using CodexQuotaMonitor.Core;
using Xunit;

namespace CodexQuotaMonitor.Core.Tests;

public sealed class PresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("available")]
    [InlineData("active")]
    public void AvailableCreditShowsDeadlineBeforeWhichItCanBeUsed(string status)
    {
        var credit = new ResetCredit(null, null, status, null, Now.AddDays(1), null, null);
        Assert.Equal("还剩 1 天到期\n明天（周一）12:00 前可用",
            QuotaFormatting.FormatCreditValidity(credit, Now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData("used", "已使用")]
    [InlineData("consumed", "已使用")]
    [InlineData("expired", "已到期")]
    [InlineData(null, "状态未知")]
    [InlineData("revoked", "revoked")]
    public void UnavailableOrUnknownCreditDoesNotPromiseUseBeforeDeadline(string? status, string expected)
    {
        var credit = new ResetCredit(null, null, status, null, Now.AddDays(1), null, null);
        var text = QuotaFormatting.FormatCreditValidity(credit, Now);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("前可用", text);
    }

    [Fact]
    public void ExpiredTimestampOverridesStaleAvailableStatus()
    {
        var credit = new ResetCredit(null, null, "available", null, Now, null, null);
        Assert.StartsWith("已到期", QuotaFormatting.FormatCreditValidity(credit, Now));
        Assert.Equal("到期时间未知 · 可用", QuotaFormatting.FormatCreditValidity(credit with { ExpiresAt = null }, Now));
    }

    [Theory]
    [InlineData(0, "今天（周日）12:00")]
    [InlineData(1, "明天（周一）12:00")]
    [InlineData(2, "后天（周二）12:00")]
    [InlineData(7, "10月4日（周日）12:00")]
    [InlineData(-1, "昨天（周六）12:00")]
    public void FriendlyDatesUseCalendarDays(int days, string expected) =>
        Assert.Equal(expected, QuotaFormatting.FormatReadableDate(Now.AddDays(days), Now, TimeZoneInfo.Utc));

    [Fact]
    public void FriendlyDateConvertsBeforeComparingDays()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test+8", TimeSpan.FromHours(8), "test", "test");
        Assert.Equal("明天（周一）01:00", QuotaFormatting.FormatReadableDate(Now.AddHours(5), Now, zone));
    }

    [Fact]
    public void FriendlyDateIncludesDifferentYear() =>
        Assert.StartsWith("2027年1月1日", QuotaFormatting.FormatReadableDate(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), Now, TimeZoneInfo.Utc));

    [Fact]
    public void UnknownAndElapsedTimesRemainExplicit()
    {
        Assert.Equal("时间未知", QuotaFormatting.FormatReadableDate(null, Now));
        Assert.Equal("重置时间未知", QuotaFormatting.FormatTimeRemaining(null, Now));
        Assert.Equal("等待确认重置", QuotaFormatting.FormatTimeRemaining(Now, Now));
        Assert.Equal("还剩 2 小时 14 分钟", QuotaFormatting.FormatTimeRemaining(Now.AddHours(2).AddMinutes(14), Now));
        Assert.Equal("刚刚更新", QuotaFormatting.FormatUpdatedAgo(Now.AddSeconds(-12), Now));
    }

    [Theory]
    [InlineData(5040, 50)]
    [InlineData(10080, 100)]
    [InlineData(0, 0)]
    [InlineData(-30, 0)]
    public void WeeklyTimeBarUsesServerPeriod(int minutes, double expected)
    {
        var week = new QuotaWindow("week", "周额度", QuotaWindowKind.Week, 0, Now.AddMinutes(minutes), 10080, null);
        Assert.Equal(expected, QuotaFormatting.ResetTimeRemainingPercent(week, Now));
        Assert.Equal(100, week.RemainingPercent); // Time and quota progress are independent.
    }

    [Fact]
    public void MissingOrInvalidPeriodHasNoInventedProgress()
    {
        var week = new QuotaWindow("week", "周额度", QuotaWindowKind.Week, 50, Now.AddDays(8), 10080, null);
        Assert.Null(QuotaFormatting.ResetTimeRemainingPercent(week, Now));
        Assert.Null(QuotaFormatting.ResetTimeRemainingPercent(week with { WindowDurationMinutes = null }, Now));
        Assert.Null(QuotaFormatting.ResetTimeRemainingPercent(week with { ResetsAt = null }, Now));
        Assert.Null(QuotaFormatting.ResetTimeRemainingPercent(null, Now));
    }

    [Fact]
    public void PresetsHaveDistinctBackgroundsAndTimeBarAccents()
    {
        var themes = new[] { ThemePreset.MistWhite, ThemePreset.WarmSand, ThemePreset.Graphite, ThemePreset.Midnight }
            .Select(preset => ThemeColors.For(new MonitorSettings { ThemePreset = preset })).ToArray();
        Assert.Equal(4, themes.Select(theme => theme.Background).Distinct().Count());
        Assert.All(themes, theme => Assert.NotEqual(theme.Accent, theme.WeeklyAccent));
    }
}
