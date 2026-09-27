namespace CodexQuotaMonitor.Core;

public sealed class WidgetClickTracker
{
    private (int X, int Y, long Time)? _previous;
    public bool Release(int x, int y, long milliseconds, int doubleClickMilliseconds, int toleranceX, int toleranceY)
    {
        var previous = _previous;
        _previous = (x, y, milliseconds);
        if (previous is not { } first || milliseconds < first.Time ||
            milliseconds - first.Time > doubleClickMilliseconds ||
            Math.Abs(x - first.X) > toleranceX || Math.Abs(y - first.Y) > toleranceY) return false;
        _previous = null;
        return true;
    }
    public void Reset() => _previous = null;
}
