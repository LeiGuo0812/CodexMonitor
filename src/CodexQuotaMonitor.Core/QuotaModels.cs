namespace CodexQuotaMonitor.Core;

public enum QuotaWindowKind
{
    Unknown,
    FiveHour,
    Week,
    Other
}

public sealed record QuotaWindow(
    string Key,
    string Name,
    QuotaWindowKind Kind,
    double? UsedPercent,
    DateTimeOffset? ResetsAt,
    int? WindowDurationMinutes,
    string? RateLimitReachedType)
{
    public double? RemainingPercent => UsedPercent is >= 0 and <= 100 ? 100 - UsedPercent.Value : null;

    public string RemainingLabel
    {
        get
        {
            var remaining = RemainingPercent;
            if (remaining is null) return "剩余 --";
            if (remaining == 0) return "剩余 0%";
            if (remaining < 1) return "剩余 <1%";
            return $"剩余 {Math.Round(remaining.Value, MidpointRounding.AwayFromZero):0}%";
        }
    }
}

public enum ResetCreditDetailsStatus
{
    Unavailable,
    CountOnly,
    Complete,
    Partial
}

public sealed record ResetCredit(
    string? Id,
    string? ResetType,
    string? Status,
    DateTimeOffset? GrantedAt,
    DateTimeOffset? ExpiresAt,
    string? Title,
    string? Description);

public sealed record ResetCreditsSummary(
    int? AvailableCount,
    IReadOnlyList<ResetCredit> Credits,
    ResetCreditDetailsStatus DetailsStatus)
{
    public static ResetCreditsSummary Unavailable { get; } =
        new(null, Array.Empty<ResetCredit>(), ResetCreditDetailsStatus.Unavailable);

    public IEnumerable<ResetCredit> OrderedCredits => Credits
        .OrderBy(credit => credit.ExpiresAt is null)
        .ThenBy(credit => credit.ExpiresAt);
}

public enum QuotaQueryStatus
{
    Success,
    Unauthenticated,
    Unsupported,
    Failed
}

public sealed record QuotaQueryResult(
    QuotaQueryStatus Status,
    string? ErrorCode,
    string? AccountFingerprint,
    string? PlanType,
    IReadOnlyList<QuotaWindow> Windows,
    ResetCreditsSummary ResetCredits,
    DateTimeOffset RetrievedAt,
    string? RateLimitReachedType = null)
{
    public bool Succeeded => Status == QuotaQueryStatus.Success;
}

public enum QuotaSelectionStatus
{
    Current,
    WaitingForData,
    QueryFailed,
    MissingPreviouslyKnownWindow,
    AccountChanged
}

public sealed record QuotaSelection(
    QuotaWindow? MainWindow,
    QuotaSelectionStatus Status,
    bool IsStale,
    string? StatusDetail = null);
