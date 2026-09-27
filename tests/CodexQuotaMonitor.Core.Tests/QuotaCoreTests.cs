using System.Text.Json;
using CodexQuotaMonitor.Core;
using Xunit;

namespace CodexQuotaMonitor.Core.Tests;

public sealed class QuotaCoreTests
{
    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private const string FingerprintA = "account-a";
    private const string FingerprintB = "account-b";

    [Fact]
    public void ChoosesFiveHourAcrossLimitBucketsByDurationNotPrimaryName()
    {
        var query = Query(FingerprintA,
            Window("week.primary", QuotaWindowKind.Week, 42, CapturedAt.AddDays(3)),
            Window("short.secondary", QuotaWindowKind.FiveHour, 73, CapturedAt.AddHours(2)));

        var selection = new QuotaWindowSelector().Select(query);

        Assert.Equal("short.secondary", selection.MainWindow!.Key);
        Assert.Equal(QuotaWindowKind.FiveHour, selection.MainWindow.Kind);
    }

    [Fact]
    public void SelectsWeekWhenSuccessfulSnapshotHasNoFiveHourWindow()
    {
        var selection = new QuotaWindowSelector().Select(
            Query(FingerprintA, Window("codex.primary", QuotaWindowKind.Week, 58, CapturedAt.AddDays(2))));

        Assert.Equal(QuotaWindowKind.Week, selection.MainWindow!.Kind);
        Assert.Equal(QuotaSelectionStatus.Current, selection.Status);
    }

    [Fact]
    public void ExhaustedFiveHourWindowRemainsTheMainWindow()
    {
        var selection = new QuotaWindowSelector().Select(Query(FingerprintA,
            Window("codex.primary", QuotaWindowKind.FiveHour, 100, CapturedAt.AddHours(1)),
            Window("codex.secondary", QuotaWindowKind.Week, 90, CapturedAt.AddDays(2))));

        Assert.Equal(QuotaWindowKind.FiveHour, selection.MainWindow!.Kind);
        Assert.Equal("剩余 0%", selection.MainWindow.RemainingLabel);
    }

    [Fact]
    public void RequestFailureDoesNotMeanFiveHourWindowDisappeared()
    {
        var selector = new QuotaWindowSelector();
        var first = selector.Select(Query(FingerprintA, Window("codex.primary", QuotaWindowKind.FiveHour, 21, CapturedAt.AddHours(1))));
        var failed = selector.Select(Failure(QuotaQueryStatus.Failed, "TIMEOUT"));

        Assert.Equal(first.MainWindow, failed.MainWindow);
        Assert.Equal(QuotaSelectionStatus.QueryFailed, failed.Status);
        Assert.True(failed.IsStale);
    }

    [Fact]
    public void MissingKnownFiveHourWindowIsRetainedAndMarkedStale()
    {
        var selector = new QuotaWindowSelector();
        selector.Select(Query(FingerprintA, Window("codex.primary", QuotaWindowKind.FiveHour, 17, CapturedAt.AddHours(1))));
        var later = selector.Select(Query(FingerprintA, Window("codex.primary", QuotaWindowKind.Week, 49, CapturedAt.AddDays(4))));

        Assert.Equal(QuotaWindowKind.FiveHour, later.MainWindow!.Kind);
        Assert.Equal(QuotaSelectionStatus.MissingPreviouslyKnownWindow, later.Status);
        Assert.True(later.IsStale);
    }

    [Fact]
    public void AccountSwitchClearsOldWindowBeforeSelectingNewAccount()
    {
        var selector = new QuotaWindowSelector();
        selector.Select(Query(FingerprintA, Window("codex.primary", QuotaWindowKind.FiveHour, 17, CapturedAt.AddHours(1))));
        var switched = selector.Select(Query(FingerprintB, Window("codex.primary", QuotaWindowKind.Week, 51, CapturedAt.AddDays(2))));

        Assert.Equal(QuotaWindowKind.Week, switched.MainWindow!.Kind);
        Assert.Equal(QuotaSelectionStatus.AccountChanged, switched.Status);
        Assert.False(switched.IsStale);
    }

    [Fact]
    public void UnidentifiedSuccessfulAccountDoesNotReusePreviousSelection()
    {
        var selector = new QuotaWindowSelector();
        selector.Select(Query(FingerprintA, Window("codex.primary", QuotaWindowKind.FiveHour, 17, CapturedAt.AddHours(1))));
        var unidentified = selector.Select(Query(null, Window("codex.primary", QuotaWindowKind.Week, 51, CapturedAt.AddDays(2))));
        var missing = selector.Select(Query(null));

        Assert.Equal(QuotaWindowKind.Week, unidentified.MainWindow!.Kind);
        Assert.Null(missing.MainWindow);
        Assert.Equal(QuotaSelectionStatus.WaitingForData, missing.Status);
    }

    [Fact]
    public void MissingZeroAndTinyRemainingValuesStayDistinct()
    {
        var missing = Window("x", QuotaWindowKind.FiveHour, null, null);
        var zero = Window("z", QuotaWindowKind.FiveHour, 100, null);
        var tiny = Window("t", QuotaWindowKind.FiveHour, 99.5, null);

        Assert.Equal("剩余 --", missing.RemainingLabel);
        Assert.Equal("剩余 0%", zero.RemainingLabel);
        Assert.Equal("剩余 <1%", tiny.RemainingLabel);
    }

    [Fact]
    public void ParsesFiveHourAndWeekByActualWindowDuration()
    {
        using var account = JsonDocument.Parse("""{"id":1,"result":{"account":{"type":"chatgpt","email":"unit@example.invalid","planType":"pro"}}}""");
        using var limits = JsonDocument.Parse("""
        {
          "id":2,
          "result":{
            "rateLimitsByLimitId":{
              "codex":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":10080,"resetsAt":1791073118},"secondary":{"usedPercent":99.5,"windowDurationMins":300,"resetsAt":1790900000}},
              "codex_other":{"limitId":"codex_other","primary":{"usedPercent":101,"windowDurationMins":60,"resetsAt":1791073118000},"secondary":null}
            },
            "rateLimits":{"limitId":"codex","primary":{"usedPercent":25,"windowDurationMins":10080,"resetsAt":1791073118}},
            "rateLimitResetCredits":null
          }
        }
        """);

        var query = CodexRateLimitParser.Parse(account.RootElement, limits.RootElement, CapturedAt);
        var week = Assert.Single(query.Windows, window => window.Kind == QuotaWindowKind.Week);
        var fiveHour = Assert.Single(query.Windows, window => window.Kind == QuotaWindowKind.FiveHour);
        var invalid = Assert.Single(query.Windows, window => window.Kind == QuotaWindowKind.Other);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791073118), week.ResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790900000), fiveHour.ResetsAt);
        Assert.Null(invalid.UsedPercent); // Out-of-range data is unknown, never clamped to a plausible value.
        Assert.Null(invalid.ResetsAt); // Milliseconds are rejected because the protocol specifies seconds.
        Assert.Equal("剩余 --", invalid.RemainingLabel);
    }

    [Fact]
    public void ConvertsAbsoluteResetTimeIntoRequestedWindowsTimezone()
    {
        var value = new DateTimeOffset(2026, 10, 3, 10, 30, 0, TimeSpan.Zero);
        var china = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time");

        Assert.Equal("2026-10-03 18:30:00（UTC+08:00）", QuotaFormatting.FormatFullDate(value, china));
    }

    [Theory]
    [InlineData(-1, "等待确认重置")]
    [InlineData(30, "<1分钟")]
    [InlineData(60, "1分钟")]
    [InlineData(60 * 23, "23分钟")]
    [InlineData(60 * 60 * 2 + 60 * 14, "2小时14分")]
    [InlineData(60 * 60 * 24 * 3 + 60 * 60 * 6, "3天6小时")]
    public void FormatsNaturalCountdownBoundaries(int secondsUntilReset, string expected)
    {
        var reset = CapturedAt.AddSeconds(secondsUntilReset);
        Assert.Equal(expected, QuotaFormatting.FormatCountdown(reset, CapturedAt));
    }

    [Fact]
    public void UnknownResetTimeIsNotReplacedByCreditExpiry()
    {
        Assert.Equal("重置时间未知", QuotaFormatting.FormatCountdown(null, CapturedAt));
    }

    [Fact]
    public void ResetCreditCountAndPartialDetailsRemainIndependent()
    {
        var summary = ParseCredits("""
        {"availableCount":4,"credits":[{"id":"one","resetType":"codexRateLimits","status":"available","grantedAt":1790000000,"expiresAt":1791000000},{"id":"two","resetType":"codexRateLimits","status":"available","grantedAt":1790000000,"expiresAt":1791000000},{"id":"three","resetType":"codexRateLimits","status":"available","grantedAt":1790000000,"expiresAt":null}]}
        """);

        Assert.Equal(4, summary.AvailableCount);
        Assert.Equal(ResetCreditDetailsStatus.Partial, summary.DetailsStatus);
        Assert.Equal(3, summary.Credits.Count);
        Assert.Equal(2, summary.OrderedCredits.Count(credit => credit.ExpiresAt == DateTimeOffset.FromUnixTimeSeconds(1791000000)));
        Assert.Null(summary.OrderedCredits.Last().ExpiresAt);
    }

    [Fact]
    public void ResetCreditCountOnlyAndNoCreditsAreDifferentFromFailure()
    {
        Assert.Equal(ResetCreditDetailsStatus.CountOnly, ParseCredits("""{"availableCount":3,"credits":null}""").DetailsStatus);
        var noCredits = ParseCredits("""{"availableCount":0,"credits":[]}""");
        Assert.Equal(ResetCreditDetailsStatus.Complete, noCredits.DetailsStatus);
        Assert.Empty(noCredits.Credits);
        Assert.Equal(ResetCreditDetailsStatus.Unavailable, ResetCreditsSummary.Unavailable.DetailsStatus);
    }

    [Fact]
    public async Task AppServerHandshakeUsesReadOnlyMethodsAndMatchesRequestIds()
    {
        var transport = FakeTransport.CreateSuccessful();
        var result = await CodexAppServerProtocol.ReadQuotaAsync(transport, CapturedAt, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "initialize", "initialized", "account/read", "account/rateLimits/read" }, transport.SentMethods);
        Assert.DoesNotContain(transport.SentMethods, method => method.StartsWith("turn/", StringComparison.Ordinal));
        Assert.Equal(QuotaWindowKind.Week, Assert.Single(result.Windows).Kind);
    }

    [Fact]
    public async Task AppServerProtocolErrorsAreReturnedAsFailures()
    {
        var transport = FakeTransport.CreateWithAccountError();
        var result = await CodexAppServerProtocol.ReadQuotaAsync(transport, CapturedAt, CancellationToken.None);

        Assert.Equal(QuotaQueryStatus.Failed, result.Status);
        Assert.Equal("ACCOUNT_DENIED", result.ErrorCode);
    }

    [Fact]
    public async Task AppServerReadTimeoutCanBeCancelled()
    {
        var transport = FakeTransport.CreateThatStallsAfterInitialize();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CodexAppServerProtocol.ReadQuotaAsync(transport, CapturedAt, timeout.Token));
    }

    [Fact]
    public async Task AppServerProcessExitIsNotTreatedAsAnEmptyAccount()
    {
        var transport = FakeTransport.CreateExitedAfterInitialize(7);

        var exception = await Assert.ThrowsAsync<AppServerProcessExitedException>(() =>
            CodexAppServerProtocol.ReadQuotaAsync(transport, CapturedAt, CancellationToken.None));

        Assert.Equal(7, exception.ExitCode);
    }

    [Fact]
    public async Task MonitorClearsOldSnapshotWhenKnownAccountChangesButRatesFail()
    {
        var source = new SequenceQuotaSource(
            Query(FingerprintA, Window("codex.primary", QuotaWindowKind.FiveHour, 22, CapturedAt.AddHours(1))),
            new QuotaQueryResult(QuotaQueryStatus.Unsupported, "SCHEMA_CHANGED", FingerprintB, "pro",
                Array.Empty<QuotaWindow>(), ResetCreditsSummary.Unavailable, CapturedAt.AddMinutes(1)));
        var monitor = new QuotaMonitorService(source);

        await monitor.RefreshAsync();
        var switched = await monitor.RefreshAsync();

        Assert.Null(switched.Selection.MainWindow);
        Assert.Empty(switched.AvailableWindows);
        Assert.Equal("SCHEMA_CHANGED", switched.QueryError);
    }

    [Fact]
    public async Task MonitorDoesNotKeepQuotaAfterAuthenticationIsLost()
    {
        var source = new SequenceQuotaSource(
            Query(FingerprintA, Window("codex.primary", QuotaWindowKind.Week, 35, CapturedAt.AddDays(2))),
            Failure(QuotaQueryStatus.Unauthenticated, "CHATGPT_AUTH_REQUIRED"));
        var monitor = new QuotaMonitorService(source);

        await monitor.RefreshAsync();
        var unauthenticated = await monitor.RefreshAsync();

        Assert.Null(unauthenticated.Selection.MainWindow);
        Assert.Empty(unauthenticated.AvailableWindows);
        Assert.Null(unauthenticated.LastSuccessfulUpdate);
    }

    [Fact]
    public async Task ConcurrentRefreshRequestsShareOneInFlightQuery()
    {
        var completion = new TaskCompletionSource<QuotaQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DeferredQuotaSource(completion.Task);
        var monitor = new QuotaMonitorService(source);

        var first = monitor.RefreshAsync();
        var second = monitor.RefreshAsync();
        Assert.Same(first, second);
        Assert.Equal(1, source.CallCount);
        completion.SetResult(Query(FingerprintA, Window("codex.primary", QuotaWindowKind.Week, 35, CapturedAt.AddDays(2))));
        await first;
    }

    private static ResetCreditsSummary ParseCredits(string json)
    {
        using var document = JsonDocument.Parse($"{{\"rateLimitResetCredits\":{json}}}");
        return CodexRateLimitParser.ParseResetCreditsSummary(document.RootElement);
    }

    private static QuotaWindow Window(string key, QuotaWindowKind kind, double? used, DateTimeOffset? reset) =>
        new(key, key, kind, used, reset, kind == QuotaWindowKind.FiveHour ? 300 : kind == QuotaWindowKind.Week ? 10080 : 60, null);

    private static QuotaQueryResult Query(string? account, params QuotaWindow[] windows) =>
        new(QuotaQueryStatus.Success, null, account, "pro", windows, ResetCreditsSummary.Unavailable, CapturedAt);

    private static QuotaQueryResult Failure(QuotaQueryStatus status, string code) =>
        new(status, code, null, null, Array.Empty<QuotaWindow>(), ResetCreditsSummary.Unavailable, CapturedAt);

    private sealed class FakeTransport : IAppServerLineTransport
    {
        private readonly Queue<string> _replies;
        private readonly Func<CancellationToken, Task<string?>>? _reader;
        private FakeTransport(Queue<string> replies, Func<CancellationToken, Task<string?>>? reader = null)
        {
            _replies = replies;
            _reader = reader;
        }

        public List<string> SentMethods { get; } = [];

        public static FakeTransport CreateSuccessful() => new(new Queue<string>([
            "{\"method\":\"account/updated\",\"params\":{}}",
            "{\"id\":1,\"result\":{\"userAgent\":\"codex-test\"}}",
            "{\"method\":\"account/rateLimits/updated\",\"params\":{}}",
            "{\"id\":2,\"result\":{\"account\":{\"type\":\"chatgpt\",\"email\":\"test@example.invalid\",\"planType\":\"pro\"}}}",
            "{\"id\":3,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":58,\"windowDurationMins\":10080,\"resetsAt\":1791073118}},\"rateLimitResetCredits\":{\"availableCount\":0,\"credits\":[]}}}"
        ]));

        public static FakeTransport CreateWithAccountError() => new(new Queue<string>([
            "{\"id\":1,\"result\":{}}",
            "{\"id\":2,\"error\":{\"code\":\"ACCOUNT_DENIED\",\"message\":\"redacted\"}}"
        ]));

        public static FakeTransport CreateThatStallsAfterInitialize() => new(new Queue<string>([
            "{\"id\":1,\"result\":{}}"
        ]), StallAsync);

        public static FakeTransport CreateExitedAfterInitialize(int exitCode) => new(new Queue<string>([
            "{\"id\":1,\"result\":{}}"
        ]), _ => Task.FromException<string?>(new AppServerProcessExitedException(exitCode)));

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(line);
            SentMethods.Add(document.RootElement.GetProperty("method").GetString()!);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            _reader is not null ? _reader(cancellationToken) : Task.FromResult<string?>(_replies.Dequeue());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async Task<string?> StallAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class SequenceQuotaSource(params QuotaQueryResult[] results) : ICodexQuotaSource
    {
        private readonly Queue<QuotaQueryResult> _results = new(results);
        public Task<QuotaQueryResult> ReadQuotaAsync(CancellationToken cancellationToken) => Task.FromResult(_results.Dequeue());
    }

    private sealed class DeferredQuotaSource(Task<QuotaQueryResult> pending) : ICodexQuotaSource
    {
        public int CallCount { get; private set; }
        public Task<QuotaQueryResult> ReadQuotaAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return pending;
        }
    }
}
