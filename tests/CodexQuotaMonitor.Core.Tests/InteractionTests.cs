using CodexQuotaMonitor.Core;
using Xunit;

namespace CodexQuotaMonitor.Core.Tests;

public sealed class InteractionTests
{
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
