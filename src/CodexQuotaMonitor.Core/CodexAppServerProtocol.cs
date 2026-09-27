using System.Text.Json;

namespace CodexQuotaMonitor.Core;

public interface IAppServerLineTransport : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellationToken);
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);
}

public sealed class AppServerProcessExitedException(int? exitCode)
    : Exception("Codex app-server exited before replying.")
{
    public int? ExitCode { get; } = exitCode;
}

public sealed class CodexAppServerProtocolException(string code)
    : Exception($"Codex app-server protocol error: {code}")
{
    public string Code { get; } = code;
}

/// <summary>
/// One short-lived, read-only app-server session. It performs the documented handshake, requests
/// account identity and rate limits, and ignores unrelated notifications without losing IDs.
/// </summary>
public static class CodexAppServerProtocol
{
    public static async Task<QuotaQueryResult> ReadQuotaAsync(
        IAppServerLineTransport transport,
        DateTimeOffset retrievedAt,
        CancellationToken cancellationToken)
    {
        var initialize = await RequestAsync(transport, 1, "initialize", new
        {
            clientInfo = new
            {
                name = "codex_quota_monitor",
                title = "Codex Quota Monitor",
                version = "0.1.0"
            }
        }, cancellationToken).ConfigureAwait(false);

        if (TryGetProperty(initialize, "error", out var initializeError))
            throw new CodexAppServerProtocolException(ReadErrorCode(initializeError));

        await transport.WriteLineAsync(
            JsonSerializer.Serialize(new { method = "initialized" }),
            cancellationToken).ConfigureAwait(false);

        var account = await RequestAsync(transport, 2, "account/read", new { refreshToken = false }, cancellationToken)
            .ConfigureAwait(false);
        if (TryGetProperty(account, "error", out var accountError))
            return Failed(QuotaQueryStatus.Failed, ReadErrorCode(accountError), retrievedAt);

        var accountValue = TryGetProperty(account, "result", out var accountResult) &&
            TryGetProperty(accountResult, "account", out var parsedAccount)
                ? parsedAccount
                : default;
        if (accountValue.ValueKind != JsonValueKind.Object ||
            !StringComparer.Ordinal.Equals(ReadString(accountValue, "type"), "chatgpt"))
        {
            return Failed(QuotaQueryStatus.Unauthenticated, "CHATGPT_AUTH_REQUIRED", retrievedAt);
        }

        var limits = await RequestAsync(transport, 3, "account/rateLimits/read", parameters: null, cancellationToken)
            .ConfigureAwait(false);
        using var accountDocument = JsonDocument.Parse(account.GetRawText());
        using var limitsDocument = JsonDocument.Parse(limits.GetRawText());
        return CodexRateLimitParser.Parse(accountDocument.RootElement, limitsDocument.RootElement, retrievedAt);
    }

    private static async Task<JsonElement> RequestAsync(
        IAppServerLineTransport transport,
        int id,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        object request = parameters is null
            ? new { method, id }
            : new { method, id, @params = parameters };
        await transport.WriteLineAsync(JsonSerializer.Serialize(request), cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var line = await transport.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) throw new AppServerProcessExitedException(null);

            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { throw new CodexAppServerProtocolException("INVALID_JSON_LINE"); }

            using (document)
            {
                var message = document.RootElement;
                if (!TryGetProperty(message, "id", out var responseId))
                    continue; // Unsolicited notification.

                if (responseId.ValueKind == JsonValueKind.Number && responseId.TryGetInt32(out var numericId) && numericId == id)
                    return message.Clone();

                // App-server requests to the client are not part of quota reads. Acknowledge them
                // with a method-not-found error so the server does not remain blocked.
                if (TryGetProperty(message, "method", out var serverMethod) && serverMethod.ValueKind == JsonValueKind.String)
                {
                    await transport.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        id = responseId,
                        error = new { code = "METHOD_NOT_SUPPORTED", message = "Quota monitor only supports read methods." }
                    }), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static string ReadErrorCode(JsonElement error) =>
        TryGetProperty(error, "code", out var code) && code.ValueKind == JsonValueKind.String
            ? code.GetString() ?? "APP_SERVER_ERROR"
            : "APP_SERVER_ERROR";

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static QuotaQueryResult Failed(QuotaQueryStatus status, string code, DateTimeOffset retrievedAt) =>
        new(status, code, null, null, Array.Empty<QuotaWindow>(), ResetCreditsSummary.Unavailable, retrievedAt);
}
