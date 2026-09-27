using Xunit;

namespace CodexQuotaMonitor.Core.Tests;

public sealed class PresentationCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StableTextReusesPresentationAcrossTicks()
    {
        var cache = new WidgetPresentationCache();
        var window = new QuotaWindow("week", "周额度", QuotaWindowKind.Week, 30, Now.AddDays(3).AddMinutes(30), 10080, null);
        var selection = new QuotaSelection(window, QuotaSelectionStatus.Current, false);
        var first = cache.Get(selection, Now, Now, null);
        for (var second = 1; second < 60; second++)
            Assert.Same(first, cache.Get(selection with { MainWindow = window with { } }, Now.AddSeconds(second), Now, null));
    }

    [Fact]
    public void NewDataErrorsAndUpdatedTimeInvalidateCache()
    {
        var cache = new WidgetPresentationCache();
        var selection = new QuotaSelection(null, QuotaSelectionStatus.WaitingForData, false);
        var initial = cache.Get(selection, Now, null, null);
        var updated = cache.Get(selection, Now, Now, null);
        Assert.NotSame(initial, updated);
        var error = cache.Get(selection, Now, Now, "TIMEOUT");
        Assert.Equal("连接异常", error.StateMarker);
        Assert.NotSame(updated, error);
        var changed = cache.Get(selection with { IsStale = true }, Now, Now, null);
        Assert.Equal("数据陈旧", changed.StateMarker);
        Assert.NotSame(error, changed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(3600)]
    [InlineData(3660)]
    [InlineData(86400)]
    [InlineData(90000)]
    public void CacheMatchesFormattersAcrossEveryDisplayBoundary(int remainingSeconds)
    {
        var cache = new WidgetPresentationCache();
        var window = new QuotaWindow("five", "5小时", QuotaWindowKind.FiveHour, 10, Now.AddSeconds(remainingSeconds), 300, null);
        var selection = new QuotaSelection(window, QuotaSelectionStatus.Current, false);
        for (var offset = -2; offset <= 2; offset++)
        {
            var time = Now.AddSeconds(offset);
            Assert.Equal(WidgetPresentationBuilder.Create(selection, time, Now), cache.Get(selection, time, Now, null));
        }
    }

    [Fact]
    public void EqualCountdownKeysAlwaysHaveEqualVisibleText()
    {
        long? previousKey = null;
        string? countdown = null;
        string? readable = null;
        // Cover the whole 0..49-hour span and all minute/hour/day transitions.
        for (var second = -1; second <= 49 * 3600; second++)
        {
            var expiry = Now.AddSeconds(second);
            var key = QuotaFormatting.CountdownKey(expiry, Now);
            var currentCountdown = QuotaFormatting.FormatCountdown(expiry, Now);
            var currentReadable = QuotaFormatting.FormatTimeRemaining(expiry, Now);
            if (key == previousKey) { Assert.Equal(countdown, currentCountdown); Assert.Equal(readable, currentReadable); }
            previousKey = key;
            countdown = currentCountdown;
            readable = currentReadable;
        }
        Assert.NotEqual(QuotaFormatting.CountdownKey(null, Now), QuotaFormatting.CountdownKey(Now, Now));
    }

    [Fact]
    public void ClockMovingBackwardsRecomputesExpiredCountdown()
    {
        var cache = new WidgetPresentationCache();
        var selection = new QuotaSelection(new QuotaWindow("five", "5小时", QuotaWindowKind.FiveHour, 100, Now, 300, null), QuotaSelectionStatus.Current, false);
        Assert.Contains("等待确认", cache.Get(selection, Now, Now, null).ResetLine);
        Assert.Contains("2分钟", cache.Get(selection, Now.AddMinutes(-2), Now, null).ResetLine);
    }
}
