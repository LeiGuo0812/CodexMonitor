namespace CodexQuotaMonitor.Core;

/// <summary>Keep formatting and tooltips until their visible inputs change.</summary>
public sealed class WidgetPresentationCache
{
    private QuotaSelection? _selection;
    private DateTimeOffset? _updated;
    private string? _error;
    private long _countdown;
    private TimeZoneInfo? _zone;
    private WidgetPresentation? _presentation;

    public WidgetPresentation Get(QuotaSelection selection, DateTimeOffset now, DateTimeOffset? updated, string? error)
    {
        var countdown = QuotaFormatting.CountdownKey(selection.MainWindow?.ResetsAt, now);
        var zone = TimeZoneInfo.Local;
        if (_presentation is not null && selection == _selection && updated == _updated && error == _error &&
            countdown == _countdown && ReferenceEquals(zone, _zone)) return _presentation;
        _selection = selection;
        _updated = updated;
        _error = error;
        _countdown = countdown;
        _zone = zone;
        return _presentation = WidgetPresentationBuilder.Create(selection, now, updated, error);
    }
}
