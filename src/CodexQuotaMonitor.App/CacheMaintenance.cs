using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexQuotaMonitor.App;

internal static class CacheMaintenance
{
    private static bool _cleanOnExit;
    internal const string MarkerName = "codex-monitor-cache-owner.txt";
    internal const string Owner = "CodexMonitor:8e992948-6bc8-4b72-8f26-4e2e7254a364";

    internal static string[] FindOwnedBundles(string root)
    {
        if (!Directory.Exists(root) || IsLink(root)) return [];
        var found = new List<string>();
        foreach (var app in Directory.EnumerateDirectories(root))
        {
            if (IsLink(app)) continue;
            foreach (var bundle in Directory.EnumerateDirectories(app))
            {
                if (IsOwnedBundle(root, bundle)) found.Add(Path.GetFullPath(bundle));
            }
        }
        return found.ToArray();
    }

    internal static bool IsOwnedBundle(string root, string bundle)
    {
        try
        {
            var basePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var path = Path.GetFullPath(bundle);
            var parent = Directory.GetParent(path);
            if (parent?.Parent is null || !string.Equals(parent.Parent.FullName, basePath, StringComparison.OrdinalIgnoreCase)) return false;
            if (IsLink(basePath) || IsLink(parent.FullName) || IsLink(path)) return false;
            var marker = Path.Combine(path, MarkerName);
            return File.Exists(marker) && !IsLink(marker) &&
                File.ReadAllText(marker).Trim() == Owner && File.Exists(Path.Combine(path, "CodexQuotaMonitor.dll"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    internal static string[] CacheRoots()
    {
        var roots = new List<string> { Path.Combine(Path.GetTempPath(), ".net") };
        var custom = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
        if (!string.IsNullOrWhiteSpace(custom)) roots.Add(Path.GetFullPath(custom));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // A system PowerShell process waits for this app to release its native DLLs.
    // The script is passed in memory; no helper EXE or script is written to disk.
    internal static async Task ClearAsync()
    {
        _cleanOnExit = true;
        using var cleanup = ScheduleCleanup(waitForExit: false);
        var error = cleanup.StandardError.ReadToEndAsync();
        await cleanup.WaitForExitAsync();
        if (cleanup.ExitCode != 0) throw new IOException("部分缓存未能清理：" + await error);
    }

    internal static void OnApplicationExit()
    {
        if (!_cleanOnExit) return;
        try { using var cleanup = ScheduleCleanup(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    internal static Process ScheduleCleanup(bool waitForExit = true)
    {
        var roots = CacheRoots();
        var active = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        // Preserve every copy of the active bundle hash until exit. Windows can
        // report the same extraction root using both long paths and 8.3 aliases.
        var activeHash = Path.GetFileName(active);
        var targets = roots.SelectMany(root => FindOwnedBundles(root).Select(path => new { Root = root, Path = path }))
            .Where(target => waitForExit || !string.Equals(Path.GetFileName(target.Path), activeHash, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var plan = new
        {
            WaitForExit = waitForExit, Pid = Environment.ProcessId,
            Started = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            MarkerName, Owner, Targets = targets,
            Diagnostic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaMonitor", "ui-failure.json")
        };
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan)));
        var script = "$plan = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" + CleanupScript;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardError = !waitForExit
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("无法启动缓存清理进程。");
    }

    private const string CleanupScript = """
        $ErrorActionPreference = 'Stop'
        if ($plan.WaitForExit) {
            $app = Get-Process -Id $plan.Pid -ErrorAction SilentlyContinue
            if ($app -and $app.StartTime.ToUniversalTime().Ticks -eq $plan.Started) {
                if (!$app.WaitForExit(60000)) { exit 1 }
            }
        }
        $guard = New-Object Threading.Mutex($false, 'Local\CodexQuotaMonitor.SingleInstance')
        $owned = $false
        try {
            if ($plan.WaitForExit) {
                try { $owned = $guard.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
                if (!$owned) { exit 1 }
            }
            foreach ($target in $plan.Targets) {
                $root = [IO.Path]::GetFullPath($target.Root).TrimEnd('\')
                $path = [IO.Path]::GetFullPath($target.Path).TrimEnd('\')
                $parent = [IO.Directory]::GetParent($path)
                if (!$parent -or !$parent.Parent -or $parent.Parent.FullName -ine $root) { exit 1 }
                if (!(Test-Path -LiteralPath $path)) { continue }
                foreach ($check in @($root, $parent.FullName, $path)) {
                    if ((Get-Item -LiteralPath $check -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }
                }
                $marker = Join-Path $path $plan.MarkerName
                if ((Get-Item -LiteralPath $marker -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }
                if ((Get-Content -LiteralPath $marker -Raw).Trim() -cne $plan.Owner) { exit 1 }
                if (!(Test-Path -LiteralPath (Join-Path $path 'CodexQuotaMonitor.dll') -PathType Leaf)) { exit 1 }
                # Never traverse links, even in a marked application cache.
                $pending = New-Object 'Collections.Generic.Queue[string]'
                $pending.Enqueue($path)
                while ($pending.Count -gt 0) {
                    foreach ($entry in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
                        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 1 }
                        if ($entry.PSIsContainer) { $pending.Enqueue($entry.FullName) }
                    }
                }
                for ($attempt = 0; $attempt -lt 20; $attempt++) {
                    try { Remove-Item -LiteralPath $path -Recurse -Force; break }
                    catch { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 500 }
                }
                if (!(Get-ChildItem -LiteralPath $parent.FullName -Force | Select-Object -First 1)) {
                    [IO.Directory]::Delete($parent.FullName, $false)
                }
            }
            if (Test-Path -LiteralPath $plan.Diagnostic -PathType Leaf) {
                Remove-Item -LiteralPath $plan.Diagnostic -Force
            }
        } finally {
            if ($owned) { $guard.ReleaseMutex() }
            $guard.Dispose()
        }
        """;
}
