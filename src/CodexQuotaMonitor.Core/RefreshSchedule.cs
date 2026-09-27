using System.Diagnostics;

namespace CodexQuotaMonitor.Core;

/// <summary>Saved refresh interval; previews do not change the active schedule.</summary>
public sealed class RefreshSchedule(int seconds)
{
    public const int DefaultSeconds = 60;
    public const int MinimumSeconds = 30;
    public const int MaximumSeconds = 3600;
    private int _seconds = Normalize(seconds);
    private readonly SemaphoreSlim _changed = new(0, 1);

    public int IntervalSeconds => Volatile.Read(ref _seconds);
    public static int Normalize(int seconds) => Math.Clamp(seconds, MinimumSeconds, MaximumSeconds);

    public void SetInterval(int seconds)
    {
        if (Interlocked.Exchange(ref _seconds, Normalize(seconds)) == Normalize(seconds)) return;
        // Coalesce edits; the waiter always reads the most recent saved interval.
        try { _changed.Release(); } catch (SemaphoreFullException) { }
    }

    public static int DelaySeconds(int intervalSeconds, int consecutiveFailures)
    {
        var interval = Normalize(intervalSeconds);
        var multiplier = 1 << Math.Clamp(consecutiveFailures - 1, 0, 4);
        return Math.Min(Math.Max(interval, 900), interval * multiplier);
    }

    public TimeSpan RemainingDelay(TimeSpan sinceLastRefresh, int consecutiveFailures = 0)
        => TimeSpan.FromSeconds(Math.Max(0, DelaySeconds(IntervalSeconds, consecutiveFailures) - sinceLastRefresh.TotalSeconds));

    public async Task WaitAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = RemainingDelay(elapsed.Elapsed, consecutiveFailures);
            if (remaining <= TimeSpan.Zero) return;
            if (!await _changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false)) return;
        }
    }

}
