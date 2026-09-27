using System.Text.Json;
using Xunit;

namespace CodexQuotaMonitor.Core.Tests;

public sealed class RefreshScheduleTests
{
    [Fact]
    public void DefaultRemainsOneMinuteAndOldSettingIsPreserved()
    {
        Assert.Equal(60, new MonitorSettings().RefreshIntervalSeconds);
        Assert.Equal(60, JsonSerializer.Deserialize<MonitorSettings>("{}")!.RefreshIntervalSeconds);
        Assert.Equal(180, JsonSerializer.Deserialize<MonitorSettings>("{\"RefreshIntervalSeconds\":180}")!.RefreshIntervalSeconds);
    }

    [Theory]
    [InlineData(-1, 30)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(180, 180)]
    [InlineData(3600, 3600)]
    [InlineData(7200, 3600)]
    public void IntervalIsBounded(int input, int expected) => Assert.Equal(expected, RefreshSchedule.Normalize(input));

    [Fact]
    public void SavedChangesRecalculateRemainingWaitFromLastRefresh()
    {
        var schedule = new RefreshSchedule(60);
        Assert.Equal(TimeSpan.FromSeconds(40), schedule.RemainingDelay(TimeSpan.FromSeconds(20)));
        schedule.SetInterval(180);
        Assert.Equal(TimeSpan.FromSeconds(160), schedule.RemainingDelay(TimeSpan.FromSeconds(20)));
        schedule.SetInterval(30);
        Assert.Equal(TimeSpan.Zero, schedule.RemainingDelay(TimeSpan.FromSeconds(40)));
    }

    [Theory]
    [InlineData(60, 0, 60)]
    [InlineData(60, 1, 60)]
    [InlineData(60, 2, 120)]
    [InlineData(60, 5, 900)]
    [InlineData(180, 2, 360)]
    [InlineData(3600, 5, 3600)]
    public void FailureBackoffNeverShortensUserInterval(int interval, int failures, int expected)
        => Assert.Equal(expected, RefreshSchedule.DelaySeconds(interval, failures));

    [Fact]
    public async Task ShutdownCancelsWaitAndRepeatedChangesCoalesce()
    {
        var schedule = new RefreshSchedule(60);
        using var cancel = new CancellationTokenSource();
        var wait = schedule.WaitAsync(0, cancel.Token);
        for (var i = 0; i < 100; i++) schedule.SetInterval(i % 2 == 0 ? 60 : 180);
        Assert.Equal(180, schedule.IntervalSeconds);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
