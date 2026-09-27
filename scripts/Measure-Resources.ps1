param(
    [string]$ApplicationPath = (Join-Path $PSScriptRoot '..\publish\single-file\CodexQuotaMonitor.exe'),
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\.artifacts\resource-assessment.json'),
    [int]$AttachProcessId = 0,
    [switch]$IdleOnly,
    [ValidateRange(15, 3600)][int]$IdleSeconds = 180
)
$ErrorActionPreference = 'Stop'
if (!$AttachProcessId -and (Get-Process | Where-Object { $_.ProcessName -like 'CodexMonitor*' -or $_.ProcessName -eq 'CodexQuotaMonitor' })) {
    throw 'Close CodexMonitor before assessment. The script starts a fresh instance and leaves it running.'
}
# Only measure our PID and descendants. Never include the user's Codex desktop app.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public sealed class CqmResourceSample {
    public double Seconds, AppWorkingMiB, AppPrivateMiB, ChildWorkingMiB, ChildPrivateMiB, AppCpuSeconds, ChildCpuSeconds;
    public int ChildCount, Handles, Threads;
}
public sealed class CqmResourceTracker : IDisposable {
    readonly Process app;
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Dictionary<int, Process> children = new Dictionary<int, Process>();
    readonly Dictionary<int, double> cpu = new Dictionary<int, double>();
    public int ChildrenObserved { get { return children.Count; } }
    public int LogicalProcessors { get { return Environment.ProcessorCount; } }
    public CqmResourceTracker(int id) { app = Process.GetProcessById(id); var handle = app.Handle; }
    public CqmResourceSample Sample() {
        app.Refresh();
        if (app.HasExited) throw new InvalidOperationException("Monitored application exited.");
        var table = Processes();
        var parents = new HashSet<int> { app.Id };
        foreach (var pair in children) { if (!pair.Value.HasExited) parents.Add(pair.Key); }
        bool added;
        do {
            added = false;
            foreach (var entry in table) {
                int id = (int)entry.Id;
                if (!parents.Contains((int)entry.Parent) || id == app.Id || children.ContainsKey(id)) continue;
                try {
                    var child = Process.GetProcessById(id);
                    var handle = child.Handle; // Keep a handle to read lifetime CPU even after exit.
                    children.Add(id, child); parents.Add(id); added = true;
                } catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            }
        } while (added);
        var result = new CqmResourceSample {
            Seconds = clock.Elapsed.TotalSeconds, AppWorkingMiB = app.WorkingSet64 / 1048576.0,
            AppPrivateMiB = app.PrivateMemorySize64 / 1048576.0, AppCpuSeconds = Cpu(app.Handle),
            Handles = app.HandleCount, Threads = app.Threads.Count
        };
        foreach (var pair in children) {
            var child = pair.Value;
            try {
                cpu[pair.Key] = Cpu(child.Handle);
                child.Refresh();
                if (!child.HasExited) {
                    result.ChildCount++;
                    result.ChildWorkingMiB += child.WorkingSet64 / 1048576.0;
                    result.ChildPrivateMiB += child.PrivateMemorySize64 / 1048576.0;
                }
            } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }
        foreach (var value in cpu.Values) result.ChildCpuSeconds += value;
        return result;
    }
    public int CloseDetails() {
        int count = 0;
        EnumWindows((window, unused) => {
            uint id; GetWindowThreadProcessId(window, out id);
            if (id != app.Id) return true;
            var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
            if (title.ToString() == "Codex 额度监控") { PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero); count++; }
            return true;
        }, IntPtr.Zero);
        return count;
    }
    public void Dispose() { foreach (var child in children.Values) child.Dispose(); app.Dispose(); }
    static double Cpu(IntPtr handle) {
        long created, exited, kernel, user;
        return GetProcessTimes(handle, out created, out exited, out kernel, out user) ? (kernel + user) / 10000000.0 : 0;
    }
    static List<Entry> Processes() {
        var result = new List<Entry>(); var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try {
            var entry = new Entry { Size = (uint)Marshal.SizeOf(typeof(Entry)) };
            if (Process32First(snapshot, ref entry)) do { result.Add(entry); } while (Process32Next(snapshot, ref entry));
        } finally { CloseHandle(snapshot); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] struct Entry {
        public uint Size, Usage, Id; public UIntPtr Heap; public uint Module, Threads, Parent;
        public int BasePriority; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Exe;
    }
    delegate bool EnumCallback(IntPtr window, IntPtr data);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool Process32First(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern bool Process32Next(IntPtr snapshot, ref Entry entry);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr data);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder title, int length);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
'@
$appPath = [IO.Path]::GetFullPath($ApplicationPath)
$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $appPath
$info.WorkingDirectory = Split-Path $appPath
$info.Arguments = '--background'
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$info.EnvironmentVariables['PATH'] = [Environment]::GetEnvironmentVariable('PATH','Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH','User')
if ($AttachProcessId) {
    $app = Get-Process -Id $AttachProcessId
    if (![string]::Equals($app.Path, $appPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Attached process path does not match ApplicationPath.' }
} else { $app = [Diagnostics.Process]::Start($info) }
$tracker = [CqmResourceTracker]::new($app.Id)
$samples = [Collections.Generic.List[object]]::new()
$summaries = [Collections.Generic.List[object]]::new()
$activation = $null

function Measure-Phase([string]$Name, [int]$Seconds, [bool]$RepeatedRefresh = $false) {
    Write-Host ("Measuring {0}: {1}s" -f $Name,$Seconds)
    $phase = [Diagnostics.Stopwatch]::StartNew()
    $rows = [Collections.Generic.List[object]]::new()
    $nextRefresh = 0.0
    $first = $tracker.Sample()
    while ($phase.Elapsed.TotalSeconds -lt $Seconds) {
        if ($RepeatedRefresh -and $phase.Elapsed.TotalSeconds -ge $nextRefresh) {
            $null = $activation.Set()
            $nextRefresh += 3
        }
        $row = $tracker.Sample()
        $rows.Add($row)
        $samples.Add([pscustomobject]@{Phase=$Name; Metrics=$row})
        Start-Sleep -Milliseconds 250
    }
    $last = $tracker.Sample()
    $duration = $last.Seconds - $first.Seconds
    $cpu = ($last.AppCpuSeconds - $first.AppCpuSeconds) / $duration * 100 / $tracker.LogicalProcessors
    $childCpu = ($last.ChildCpuSeconds - $first.ChildCpuSeconds) / $duration * 100 / $tracker.LogicalProcessors
    $summary = [pscustomobject]@{
        Phase=$Name; Seconds=[Math]::Round($duration,2); Samples=$rows.Count
        AppWorkingMeanMiB=[Math]::Round(($rows.AppWorkingMiB | Measure-Object -Average).Average,2)
        AppWorkingPeakMiB=[Math]::Round(($rows.AppWorkingMiB | Measure-Object -Maximum).Maximum,2)
        AppPrivateMeanMiB=[Math]::Round(($rows.AppPrivateMiB | Measure-Object -Average).Average,2)
        AppPrivatePeakMiB=[Math]::Round(($rows.AppPrivateMiB | Measure-Object -Maximum).Maximum,2)
        AppPrivateStartMiB=[Math]::Round($first.AppPrivateMiB,2); AppPrivateEndMiB=[Math]::Round($last.AppPrivateMiB,2)
        ChildWorkingPeakMiB=[Math]::Round(($rows.ChildWorkingMiB | Measure-Object -Maximum).Maximum,2)
        ChildPrivatePeakMiB=[Math]::Round(($rows.ChildPrivateMiB | Measure-Object -Maximum).Maximum,2)
        CombinedWorkingPeakMiB=[Math]::Round((($rows | ForEach-Object {$_.AppWorkingMiB+$_.ChildWorkingMiB}) | Measure-Object -Maximum).Maximum,2)
        CombinedPrivatePeakMiB=[Math]::Round((($rows | ForEach-Object {$_.AppPrivateMiB+$_.ChildPrivateMiB}) | Measure-Object -Maximum).Maximum,2)
        AppCpuPercent=[Math]::Round($cpu,3); ChildCpuPercent=[Math]::Round($childCpu,3)
        ConcurrentChildrenPeak=($rows.ChildCount | Measure-Object -Maximum).Maximum
        HandlesStart=$first.Handles; HandlesEnd=$last.Handles; ThreadsEnd=$last.Threads
    }
    $summaries.Add($summary)
    $summary | ConvertTo-Json -Compress | Write-Host
}
try {
    if ($IdleOnly) { Measure-Phase 'ExtendedIdle' $IdleSeconds }
    else {
    Measure-Phase 'Warmup' 15
    $activation = [Threading.EventWaitHandle]::OpenExisting('Local\CodexQuotaMonitor.Activate')
    Measure-Phase 'Background' 120
    $null = $activation.Set()
    Measure-Phase 'Details' 45
    Measure-Phase 'RepeatedRefresh' 60 $true
    $closed = $tracker.CloseDetails()
    if ($closed -ne 1) { throw "Expected one details window, closed $closed." }
    Measure-Phase 'AfterClosingDetails' 60
    }
    $report = [pscustomobject]@{
        MeasuredAtUtc=[DateTime]::UtcNow.ToString('o'); ApplicationVersion=(Get-Item -LiteralPath $appPath).VersionInfo.ProductVersion
        ApplicationSha256=(Get-FileHash -LiteralPath $appPath -Algorithm SHA256).Hash.ToLowerInvariant()
        LogicalProcessors=$tracker.LogicalProcessors; SamplingIntervalMilliseconds=250; ChildrenObserved=$tracker.ChildrenObserved
        Notes=@('Working set includes shared pages; combined working sets may double-count shared pages.',
            'Private memory is committed virtual memory, not private physical working set.',
            'CPU percentages are normalized across all logical processors.',
            'Very short child processes or peaks between samples can be missed. No forced garbage collection or working-set trimming.',
            'Only the monitored app and its descendants are included; other Codex processes are excluded.')
        Summary=$summaries; Samples=$samples
    }
    $reportFile = [IO.Path]::GetFullPath($ReportPath)
    New-Item -ItemType Directory -Force -Path (Split-Path $reportFile) | Out-Null
    $report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $reportFile -Encoding utf8
    Write-Host "Saved: $reportFile; application remains running (PID $($app.Id))."
} finally {
    if ($activation) { $activation.Dispose() }
    $tracker.Dispose()
    $app.Dispose()
}
