using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexQuotaMonitor.Core;

/// <summary>Parses the read-only account and rate-limit replies returned by Codex app-server.</summary>
public static class CodexRateLimitParser
{
    public static QuotaQueryResult Parse(
        JsonElement accountReply,
        JsonElement rateLimitReply,
        DateTimeOffset retrievedAt)
    {
        if (TryGetErrorCode(accountReply, out var accountError))
            return Failed(QuotaQueryStatus.Failed, accountError, retrievedAt);
        if (!TryGetObjectProperty(accountReply, "result", out var accountResult) ||
            !TryGetProperty(accountResult, "account", out var account) ||
            account.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return Failed(QuotaQueryStatus.Unauthenticated, "ACCOUNT_UNAVAILABLE", retrievedAt);
        }

        var type = ReadString(account, "type");
        if (!StringComparer.Ordinal.Equals(type, "chatgpt"))
        {
            return Failed(QuotaQueryStatus.Unauthenticated, "CHATGPT_AUTH_REQUIRED", retrievedAt);
        }

        var fingerprint = CreateAccountFingerprint(type, ReadString(account, "email"));
        var plan = ReadString(account, "planType");
        if (TryGetErrorCode(rateLimitReply, out var rateError))
            return Failed(QuotaQueryStatus.Unsupported, rateError, retrievedAt, fingerprint, plan);

        if (!TryGetObjectProperty(rateLimitReply, "result", out var rateResult))
            return Failed(QuotaQueryStatus.Unsupported, "RATE_LIMIT_RESPONSE_MISSING", retrievedAt, fingerprint, plan);

        var hasByLimit = TryGetProperty(rateResult, "rateLimitsByLimitId", out var byLimit) &&
            byLimit.ValueKind == JsonValueKind.Object;
        JsonElement legacy = default;
        var hasLegacy = TryGetProperty(rateResult, "rateLimits", out legacy) && legacy.ValueKind == JsonValueKind.Object;
        if (!hasByLimit && !hasLegacy)
            return Failed(QuotaQueryStatus.Unsupported, "RATE_LIMIT_FIELDS_UNAVAILABLE", retrievedAt, fingerprint, plan);

        var windows = new List<QuotaWindow>();
        if (hasByLimit)
        {
            foreach (var bucket in byLimit.EnumerateObject())
                AddBucketWindows(windows, bucket.Name, bucket.Value);
        }
        else
        {
            var bucketId = ReadString(legacy, "limitId") ?? "codex";
            AddBucketWindows(windows, bucketId, legacy);
        }

        var credits = ParseResetCreditsSummary(rateResult);
        var reachedType = windows.FirstOrDefault(window => window.RateLimitReachedType is not null)?.RateLimitReachedType;

        return new QuotaQueryResult(
            QuotaQueryStatus.Success,
            null,
            fingerprint,
            plan,
            windows,
            credits,
            retrievedAt,
            reachedType);
    }

    private static void AddBucketWindows(List<QuotaWindow> windows, string bucketId, JsonElement bucket)
    {
        var limitName = ReadString(bucket, "limitName");
        var reachedType = ReadString(bucket, "rateLimitReachedType");
        foreach (var member in new[] { "primary", "secondary" })
        {
            if (!TryGetProperty(bucket, member, out var window) || window.ValueKind != JsonValueKind.Object)
                continue;

            var duration = ReadInt32(window, "windowDurationMins");
            var kind = IdentifyWindowKind(duration, limitName, bucketId, member);
            var name = kind switch
            {
                QuotaWindowKind.FiveHour => "5 小时",
                QuotaWindowKind.Week => "周额度",
                _ => string.IsNullOrWhiteSpace(limitName)
                    ? $"{bucketId} · {member}"
                    : $"{limitName} · {member}"
            };

            windows.Add(new QuotaWindow(
                $"{bucketId}.{member}",
                name,
                kind,
                ReadPercent(window, "usedPercent"),
                ReadUnixSeconds(window, "resetsAt"),
                duration,
                reachedType));
        }
    }

    private static QuotaWindowKind IdentifyWindowKind(int? duration, params string?[] semanticFields)
    {
        if (duration == 300) return QuotaWindowKind.FiveHour;
        if (duration == 10080) return QuotaWindowKind.Week;

        var semantics = string.Join(' ', semanticFields.Where(value => value is not null)).ToLowerInvariant();
        if (semantics.Contains("5h", StringComparison.Ordinal) ||
            semantics.Contains("five_hour", StringComparison.Ordinal) ||
            semantics.Contains("five-hour", StringComparison.Ordinal))
            return QuotaWindowKind.FiveHour;
        if (semantics.Contains("weekly", StringComparison.Ordinal) ||
            semantics.Contains("week", StringComparison.Ordinal) ||
            semantics.Contains("周", StringComparison.Ordinal))
            return QuotaWindowKind.Week;
        return duration is null ? QuotaWindowKind.Unknown : QuotaWindowKind.Other;
    }

    public static ResetCreditsSummary ParseResetCreditsSummary(JsonElement rateResult)
    {
        if (!TryGetProperty(rateResult, "rateLimitResetCredits", out var summary) ||
            summary.ValueKind != JsonValueKind.Object)
            return ResetCreditsSummary.Unavailable;

        var count = ReadInt32(summary, "availableCount");
        if (count is < 0) count = null;
        if (!TryGetProperty(summary, "credits", out var rows) || rows.ValueKind == JsonValueKind.Null)
            return new ResetCreditsSummary(count, Array.Empty<ResetCredit>(), ResetCreditDetailsStatus.CountOnly);
        if (rows.ValueKind != JsonValueKind.Array)
            return new ResetCreditsSummary(count, Array.Empty<ResetCredit>(), ResetCreditDetailsStatus.Unavailable);

        var credits = new List<ResetCredit>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            credits.Add(new ResetCredit(
                ReadString(row, "id"),
                ReadString(row, "resetType"),
                ReadString(row, "status"),
                ReadUnixSeconds(row, "grantedAt"),
                ReadUnixSeconds(row, "expiresAt"),
                ReadString(row, "title"),
                ReadString(row, "description")));
        }

        var detailStatus = count is not null && credits.Count < count.Value
            ? ResetCreditDetailsStatus.Partial
            : ResetCreditDetailsStatus.Complete;
        return new ResetCreditsSummary(count, credits, detailStatus);
    }

    private static string? CreateAccountFingerprint(string type, string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{type.ToLowerInvariant()}\n{email.Trim().ToLowerInvariant()}"));
        return Convert.ToHexString(bytes);
    }

    private static QuotaQueryResult Failed(
        QuotaQueryStatus status,
        string? code,
        DateTimeOffset retrievedAt,
        string? accountFingerprint = null,
        string? planType = null) =>
        new(status, code, accountFingerprint, planType, Array.Empty<QuotaWindow>(), ResetCreditsSummary.Unavailable, retrievedAt);

    private static bool TryGetErrorCode(JsonElement reply, out string? code)
    {
        code = null;
        if (!TryGetProperty(reply, "error", out var error) || error.ValueKind != JsonValueKind.Object)
            return false;
        code = ReadString(error, "code") ?? "APP_SERVER_ERROR";
        return true;
    }

    private static double? ReadPercent(JsonElement element, string property)
    {
        if (!TryGetProperty(element, property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var result) || !double.IsFinite(result) || result is < 0 or > 100)
            return null;
        return result;
    }

    private static DateTimeOffset? ReadUnixSeconds(JsonElement element, string property)
    {
        if (!TryGetProperty(element, property, out var value) || value.ValueKind == JsonValueKind.Null ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var seconds))
            return null;
        try { return DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static int? ReadInt32(JsonElement element, string property)
    {
        if (!TryGetProperty(element, property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result)) return null;
        return result;
    }

    private static string? ReadString(JsonElement element, string property) =>
        TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetObjectProperty(JsonElement element, string property, out JsonElement value) =>
        TryGetProperty(element, property, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetProperty(JsonElement element, string property, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value);
    }
}
