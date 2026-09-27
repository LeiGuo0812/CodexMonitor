using System.Diagnostics;
using System.Security;
using CodexQuotaMonitor.Core;

namespace CodexQuotaMonitor.App;

internal sealed class CodexAppServerQuotaSource(Func<string?> executablePath) : ICodexQuotaSource
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _versionLock = new(1, 1);
    private string? _verifiedPath;
    private DateTimeOffset _verifiedAt;

    public async Task<QuotaQueryResult> ReadQuotaAsync(CancellationToken cancellationToken)
    {
        var candidates = CodexExecutableLocator.FindCandidates(executablePath());
        if (candidates.Count == 0)
            return Failure("CODEX_NOT_FOUND");

        string? path = null;
        foreach (var candidate in candidates)
        {
            if (!await VerifyCliAsync(candidate, cancellationToken).ConfigureAwait(false)) continue;
            path = candidate;
            break;
        }
        if (path is null)
            return Failure("CODEX_NOT_A_CLI");

        using var timeout = new CancellationTokenSource(QueryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await using var transport = new CodexAppServerProcess(path);
            return await CodexAppServerProtocol.ReadQuotaAsync(transport, DateTimeOffset.UtcNow, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Failure("TIMEOUT");
        }
        catch (AppServerProcessExitedException exception)
        {
            return Failure(exception.ExitCode is null ? "APP_SERVER_EXITED" : $"APP_SERVER_EXITED_{exception.ExitCode}");
        }
        catch (CodexAppServerProtocolException exception)
        {
            return Failure(exception.Code);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Failure("CODEX_START_FAILED");
        }
        catch (SecurityException)
        {
            return Failure("CODEX_PATH_ACCESS_DENIED");
        }
        catch (IOException)
        {
            return Failure("APP_SERVER_IO_ERROR");
        }
    }

    private async Task<bool> VerifyCliAsync(string path, CancellationToken cancellationToken)
    {
        await _versionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(path, _verifiedPath) &&
                DateTimeOffset.UtcNow - _verifiedAt < TimeSpan.FromMinutes(15)) return true;
            var version = await CodexExecutableLocator.ReadCliVersionAsync(path, cancellationToken).ConfigureAwait(false);
            if (version is null) return false;
            _verifiedPath = path;
            _verifiedAt = DateTimeOffset.UtcNow;
            return true;
        }
        finally
        {
            _versionLock.Release();
        }
    }

    private static QuotaQueryResult Failure(string code) => new(
        QuotaQueryStatus.Failed,
        code,
        null,
        null,
        Array.Empty<QuotaWindow>(),
        ResetCreditsSummary.Unavailable,
        DateTimeOffset.UtcNow);
}

internal static class CodexExecutableLocator
{
    public static string? Resolve(string? configuredPath)
        => FindCandidates(configuredPath).FirstOrDefault();

    public static IReadOnlyList<string> FindCandidates(string? configuredPath) => FindCandidates(
        configuredPath,
        Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    internal static IReadOnlyList<string> FindCandidates(
        string? configuredPath, string searchPath, string userProfile, string localAppData)
    {
        var found = new List<string>();
        void Add(string candidate)
        {
            if (File.Exists(candidate))
            {
                var full = Path.GetFullPath(candidate);
                if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
            }
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            Add(configured);
            if (!configured.Contains(Path.DirectorySeparatorChar) && !configured.Contains(Path.AltDirectorySeparatorChar))
            {
                foreach (var candidate in SearchPath(configured, searchPath)) Add(candidate);
            }
            return found; // An explicit selection must not silently use another installation.
        }

        foreach (var candidate in SearchPath("codex.exe", searchPath)) Add(candidate);
        Add(Path.Combine(userProfile, ".local", "bin", "codex.exe"));

        // Explorer does not inherit the private PATH that Codex gives its development shell.
        // The desktop app ships the real CLI in a versioned bin directory; verify it with
        // --version before using it, and rescan on each refresh to survive desktop updates.
        var desktopRoot = Path.Combine(localAppData, "OpenAI", "Codex");
        var binRoot = Path.Combine(desktopRoot, "bin");
        try
        {
            if (Directory.Exists(binRoot))
                foreach (var directory in new DirectoryInfo(binRoot).EnumerateDirectories()
                             .OrderByDescending(directory => directory.LastWriteTimeUtc))
                    Add(Path.Combine(directory.FullName, "codex.exe"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Continue with other installation locations if this directory is unavailable.
        }
        Add(Path.Combine(desktopRoot, "bin", "codex.exe"));
        Add(Path.Combine(desktopRoot, "resources", "codex.exe"));
        return found;
    }

    public static async Task<string?> ReadCliVersionAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory
        };
        process.StartInfo.ArgumentList.Add("--version");
        try
        {
            if (!process.Start()) return null;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            var output = string.IsNullOrWhiteSpace(stdout.Result) ? stderr.Result : stdout.Result;
            var firstLine = output.Trim().Split('\n')[0].Trim();
            return process.ExitCode == 0 && firstLine.StartsWith("codex-cli ", StringComparison.OrdinalIgnoreCase)
                ? firstLine
                : null;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SearchPath(string name, string searchPath)
    {
        foreach (var directory in searchPath
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? candidate = null;
            try
            {
                var combined = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(combined)) candidate = Path.GetFullPath(combined);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries and continue looking.
            }
            if (candidate is not null) yield return candidate;
        }
    }
}

internal sealed class CodexAppServerProcess : IAppServerLineTransport
{
    private readonly Process _process;
    private readonly Task _stderrConsumer;
    private int _disposed;

    public CodexAppServerProcess(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("app-server");
        // Stdio is the default across CLI versions. Older bundled CLIs reject --stdio.
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!_process.Start()) throw new System.ComponentModel.Win32Exception("Codex CLI did not start.");
        }
        catch { _process.Dispose(); throw; }
        // Discard bytes with a bounded pooled buffer; no task wrapper or per-line strings.
        _stderrConsumer = _process.StandardError.BaseStream.CopyToAsync(Stream.Null, 4096);
    }

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await _process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is not null) return line;
        int? exitCode = _process.HasExited ? _process.ExitCode : null;
        throw new AppServerProcessExitedException(exitCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _process.StandardInput.Close(); }
        catch (InvalidOperationException) { }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await _process.WaitForExitAsync().ConfigureAwait(false); }
            catch (InvalidOperationException) { }
        }

        try { await _stderrConsumer.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (IOException) { }
        _process.Dispose();
    }
}
