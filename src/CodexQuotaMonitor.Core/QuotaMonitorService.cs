namespace CodexQuotaMonitor.Core;

public interface ICodexQuotaSource
{
    Task<QuotaQueryResult> ReadQuotaAsync(CancellationToken cancellationToken);
}

public sealed record MonitorViewState(
    QuotaSelection Selection,
    IReadOnlyList<QuotaWindow> AvailableWindows,
    ResetCreditsSummary ResetCredits,
    DateTimeOffset? LastSuccessfulUpdate,
    bool IsRefreshing,
    string? QueryError,
    string? PlanType);

/// <summary>Single-flight refresh state. UI callers can request refresh without blocking the UI thread.</summary>
public sealed class QuotaMonitorService(ICodexQuotaSource source)
{
    private readonly object _gate = new();
    private readonly QuotaWindowSelector _selector = new();
    private Task<MonitorViewState>? _inflight;
    private MonitorViewState _state = new(
        new QuotaSelection(null, QuotaSelectionStatus.WaitingForData, false),
        Array.Empty<QuotaWindow>(),
        ResetCreditsSummary.Unavailable,
        null,
        false,
        null,
        null);
    private string? _knownAccountFingerprint;

    public event Action<MonitorViewState>? StateChanged;

    public MonitorViewState Current => _state;

    public Task<MonitorViewState> RefreshAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_inflight is { IsCompleted: false }) return _inflight;
            _inflight = RefreshCoreAsync(cancellationToken);
            return _inflight;
        }
    }

    private async Task<MonitorViewState> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        Publish(_state with { IsRefreshing = true });
        QuotaQueryResult result;
        try
        {
            result = await source.ReadQuotaAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = new QuotaQueryResult(QuotaQueryStatus.Failed, "CANCELLED", null, null,
                Array.Empty<QuotaWindow>(), ResetCreditsSummary.Unavailable, DateTimeOffset.UtcNow);
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                TimeoutException => "TIMEOUT",
                AppServerProcessExitedException => "APP_SERVER_EXITED",
                CodexAppServerProtocolException protocol => protocol.Code,
                _ => "QUERY_FAILED"
            };
            result = new QuotaQueryResult(QuotaQueryStatus.Failed, code, null, null,
                Array.Empty<QuotaWindow>(), ResetCreditsSummary.Unavailable, DateTimeOffset.UtcNow);
        }

        if (result.Succeeded && result.AccountFingerprint is null)
        {
            _selector.Reset();
            var selection = _selector.Select(result);
            _knownAccountFingerprint = null;
            _state = new MonitorViewState(
                selection,
                result.Windows,
                result.ResetCredits,
                result.RetrievedAt,
                false,
                "ACCOUNT_IDENTITY_UNAVAILABLE",
                result.PlanType);
        }
        else if (!result.Succeeded && result.Status == QuotaQueryStatus.Unauthenticated)
        {
            _selector.Reset();
            _knownAccountFingerprint = null;
            _state = new MonitorViewState(
                new QuotaSelection(null, QuotaSelectionStatus.WaitingForData, false),
                Array.Empty<QuotaWindow>(),
                ResetCreditsSummary.Unavailable,
                null,
                false,
                result.ErrorCode ?? "CHATGPT_AUTH_REQUIRED",
                null);
        }
        else if (!result.Succeeded && result.AccountFingerprint is not null &&
                 _knownAccountFingerprint is not null &&
                 !StringComparer.Ordinal.Equals(result.AccountFingerprint, _knownAccountFingerprint))
        {
            _selector.Reset();
            _knownAccountFingerprint = result.AccountFingerprint;
            _state = new MonitorViewState(
                new QuotaSelection(null, QuotaSelectionStatus.WaitingForData, false),
                Array.Empty<QuotaWindow>(),
                ResetCreditsSummary.Unavailable,
                null,
                false,
                result.ErrorCode ?? "QUERY_FAILED",
                result.PlanType);
        }
        else if (!result.Succeeded && _knownAccountFingerprint is null)
        {
            _selector.Reset();
            var selection = _selector.Select(result);
            _state = new MonitorViewState(selection, Array.Empty<QuotaWindow>(), ResetCreditsSummary.Unavailable,
                null, false, result.ErrorCode ?? "QUERY_FAILED", null);
        }
        else
        {
            if (result.AccountFingerprint is not null &&
                _knownAccountFingerprint is not null &&
                !StringComparer.Ordinal.Equals(_knownAccountFingerprint, result.AccountFingerprint))
            {
                _state = new MonitorViewState(
                    new QuotaSelection(null, QuotaSelectionStatus.WaitingForData, false),
                    Array.Empty<QuotaWindow>(),
                    ResetCreditsSummary.Unavailable,
                    null,
                    false,
                    null,
                    null);
            }

            var selection = _selector.Select(result);
            if (result.Succeeded)
            {
                _knownAccountFingerprint = result.AccountFingerprint;
                _state = new MonitorViewState(selection, result.Windows, result.ResetCredits,
                    result.RetrievedAt, false,
                    result.AccountFingerprint is null ? "ACCOUNT_IDENTITY_UNAVAILABLE" : null,
                    result.PlanType);
            }
            else
            {
                _state = _state with
                {
                    Selection = selection,
                    IsRefreshing = false,
                    QueryError = result.ErrorCode ?? "QUERY_FAILED"
                };
            }
        }

        Publish(_state with { IsRefreshing = false });
        return _state;
    }

    private void Publish(MonitorViewState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }
}
