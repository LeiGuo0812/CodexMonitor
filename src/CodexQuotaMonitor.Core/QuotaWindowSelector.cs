namespace CodexQuotaMonitor.Core;

/// <summary>
/// Stateful window selection. A missing 5-hour window is never interpreted as proof that the
/// account no longer has one after that window has been observed successfully.
/// </summary>
public sealed class QuotaWindowSelector
{
    private string? _accountFingerprint;
    private QuotaWindow? _lastFiveHourWindow;
    private QuotaWindow? _lastSelection;

    public QuotaSelection Select(QuotaQueryResult query)
    {
        if (query.Succeeded && query.AccountFingerprint is null)
        {
            // A successful but unidentified account must never inherit another account's cache.
            _accountFingerprint = null;
            _lastFiveHourWindow = null;
            _lastSelection = null;
        }

        var accountChanged = _accountFingerprint is not null &&
            query.AccountFingerprint is not null &&
            !StringComparer.Ordinal.Equals(_accountFingerprint, query.AccountFingerprint);

        if (accountChanged)
        {
            _lastFiveHourWindow = null;
            _lastSelection = null;
        }

        if (query.AccountFingerprint is not null)
        {
            _accountFingerprint = query.AccountFingerprint;
        }

        if (!query.Succeeded)
        {
            return new QuotaSelection(
                _lastSelection,
                query.Status == QuotaQueryStatus.Failed
                    ? QuotaSelectionStatus.QueryFailed
                    : QuotaSelectionStatus.WaitingForData,
                IsStale: _lastSelection is not null,
                query.ErrorCode);
        }

        var fiveHour = query.Windows.FirstOrDefault(window => window.Kind == QuotaWindowKind.FiveHour);
        if (fiveHour is not null)
        {
            _lastFiveHourWindow = fiveHour;
            _lastSelection = fiveHour;
            return new QuotaSelection(
                fiveHour,
                accountChanged ? QuotaSelectionStatus.AccountChanged : QuotaSelectionStatus.Current,
                IsStale: false);
        }

        if (_lastFiveHourWindow is not null)
        {
            _lastSelection = _lastFiveHourWindow;
            return new QuotaSelection(
                _lastFiveHourWindow,
                QuotaSelectionStatus.MissingPreviouslyKnownWindow,
                IsStale: true,
                "此前识别的 5 小时窗口暂未返回");
        }

        var week = query.Windows.FirstOrDefault(window => window.Kind == QuotaWindowKind.Week);
        if (week is not null)
        {
            _lastSelection = week;
            return new QuotaSelection(
                week,
                accountChanged ? QuotaSelectionStatus.AccountChanged : QuotaSelectionStatus.Current,
                IsStale: false);
        }

        _lastSelection = null;
        return new QuotaSelection(null, QuotaSelectionStatus.WaitingForData, IsStale: false);
    }

    public void Reset()
    {
        _accountFingerprint = null;
        _lastFiveHourWindow = null;
        _lastSelection = null;
    }
}
