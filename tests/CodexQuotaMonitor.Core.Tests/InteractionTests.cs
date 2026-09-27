using CodexQuotaMonitor.Core;
using Xunit;

namespace CodexQuotaMonitor.Core.Tests;

public sealed class InteractionTests
{
    [Fact]
    public void TwoClicksWithinWindowsThresholdOpenOnce()
    {
        var tracker = new WidgetClickTracker();
        Assert.False(tracker.Release(100, 100, 1000, 500, 4, 4));
        Assert.True(tracker.Release(103, 98, 1499, 500, 4, 4));
        Assert.False(tracker.Release(103, 98, 1600, 500, 4, 4));
    }

    [Theory]
    [InlineData(100, 100, 1501)]
    [InlineData(105, 100, 1400)]
    [InlineData(100, 105, 1400)]
    public void SlowOrDistantClicksDoNotOpen(int x, int y, long at)
    {
        var tracker = new WidgetClickTracker();
        tracker.Release(100, 100, 1000, 500, 4, 4);
        Assert.False(tracker.Release(x, y, at, 500, 4, 4));
    }

    [Fact]
    public void DragInvalidatesFirstClick()
    {
        var tracker = new WidgetClickTracker();
        tracker.Release(100, 100, 1000, 500, 4, 4);
        tracker.Reset();
        Assert.False(tracker.Release(100, 100, 1200, 500, 4, 4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.75)]
    public void DropNearTaskbarSnapsButOutsideStaysFloating(double scale)
    {
        Assert.True(WidgetDropPolicy.IsNearTaskbar(800 * scale, 976 * scale, 0, 952 * scale, 1920 * scale, 1000 * scale, scale));
        Assert.True(WidgetDropPolicy.IsNearTaskbar(800 * scale, 944 * scale, 0, 952 * scale, 1920 * scale, 1000 * scale, scale));
        Assert.False(WidgetDropPolicy.IsNearTaskbar(800 * scale, 900 * scale, 0, 952 * scale, 1920 * scale, 1000 * scale, scale));
        Assert.False(WidgetDropPolicy.IsNearTaskbar(-100 * scale, 976 * scale, 0, 952 * scale, 1920 * scale, 1000 * scale, scale));
    }
}
