param(
    [string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\publish\single-file'),
    [string]$ApplicationName = 'CodexQuotaMonitor.exe',
    [string]$ReportPath = (Join-Path $PSScriptRoot '..\.artifacts\runtime-verification.json')
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name @('CodexQuotaMonitor', [IO.Path]::GetFileNameWithoutExtension($ApplicationName)) -ErrorAction SilentlyContinue) {
    throw 'Close Codex Quota Monitor before running verification (single-instance app).'
}
$appDirectory = [IO.Path]::GetFullPath($ApplicationDirectory)
$report = [IO.Path]::GetFullPath($ReportPath)
New-Item -ItemType Directory -Force -Path (Split-Path $report) | Out-Null
$info = New-Object Diagnostics.ProcessStartInfo
$info.FileName = Join-Path $appDirectory $ApplicationName
$info.WorkingDirectory = $appDirectory
$info.Arguments = '--verify-runtime "' + $report + '"'
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.EnvironmentVariables['PATH'] = [Environment]::GetEnvironmentVariable('PATH','Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH','User')
$started = [DateTime]::UtcNow
$app = [Diagnostics.Process]::Start($info)
Write-Output ('Verification PID: ' + $app.Id)
if (!$app.WaitForExit(60000)) { throw 'Verification did not finish within 60 seconds.' }
if ($app.ExitCode -ne 0) { throw ('Verification process exited with ' + $app.ExitCode) }
if (!(Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTimeUtc -lt $started) {
    throw 'The process did not produce a fresh verification report.'
}
$result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if ($result.VerificationError) { throw ('Verification failed: ' + $result.VerificationError) }
if (!$result.ResourceReuse.Rows.UnchangedReuses -or !$result.ResourceReuse.Rows.ChangedReuses -or
    !$result.ResourceReuse.Rows.RemovedRowsReleased -or !$result.ResourceReuse.Rows.DuplicateCardsPreserved -or
    !$result.ResourceReuse.Widget.BrushesReused -or $result.ResourceReuse.Widget.ExtraMeasurements -gt 1 -or
    $result.ResourceReuse.Widget.ExtraSystemReads -gt 1 -or !$result.ResourceReuse.MinimizedClockStopped -or
    !$result.ResourceReuse.RestoredClockRunning) { throw 'On-demand resource reuse or minimized clock suspension failed.' }
if (!$result.Startup.HostPathRegistered -or !$result.Startup.Disabled -or !$result.Startup.SavedAfterPreview) {
    throw 'Startup registration did not track the single-file host after settings preview.'
}
if (!$result.Startup.PositionMenuChecks) { throw 'Position menu must check exactly the selected mode and persist it.' }
if (!$result.Startup.MenuToggleWorks -or !$result.Startup.MenuPreservesDraft -or !$result.Menu.StartupPresent -or
    !$result.Menu.CachePresent -or !$result.Menu.CommandsMapped -or !$result.Menu.CacheBusyDisabled -or !$result.Menu.CacheEnabledAfter) {
    throw 'Context-menu startup/cache entries or their state transitions failed.'
}
if (!$result.Cache.ActivePreserved -or !$result.Cache.CleanupRequested) { throw 'Live cache cleanup failed.' }
if (!$result.RefreshEditor.PreviewOnly -or !$result.RefreshEditor.CancelRestored -or !$result.RefreshEditor.BoundsCorrect -or
    $result.RefreshEditor.DefaultSeconds -ne 60 -or !$result.Startup.RefreshPreviewDidNotReschedule -or !$result.Startup.RefreshSaveRescheduled) {
    throw 'Refresh interval defaults, editor or live scheduling checks failed.'
}
if ($result.Cache.Bundled) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ((Test-Path -LiteralPath $result.Cache.ActiveDirectory) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (Test-Path -LiteralPath $result.Cache.ActiveDirectory) { throw 'Deferred cache cleanup did not remove the active bundle after exit.' }
}
if (!$result.Hover.Highlighted -or !$result.Hover.Restored -or !$result.Hover.SpindleEdges -or !$result.Hover.SoftGlow -or
    !$result.Hover.UniformReducedLight -or !$result.Hover.IdleLinesReplaced -or !$result.Hover.LightOverlay -or !$result.Hover.HitTestingPreserved -or
    !$result.Tray.CustomIconLoaded -or !$result.Tray.Registered) {
    throw 'Hover transition or custom tray icon check failed; inspect the report.'
}
if ($result.FirstError -or $result.SecondError -or !$result.RefreshedAgain) {
    throw 'Real quota refresh did not succeed twice; inspect the report.'
}
if (!$result.PreviewCancelRestoredSettings -or [Math]::Abs($result.Dashboard.Bounds.TaskbarGapDip) -gt 1 -or
    !$result.Dashboard.Bounds.WithinWorkArea -or $result.Dashboard.Bounds.Minimized -or
    !$result.NarrowLayout.CompactLayout -or !$result.NarrowLayout.ContentFitsWidth) {
    throw 'A settings or window layout check failed; inspect the report.'
}
if ($result.DragSnap.DragOutMode -ne 'FixedAbove' -or $result.DragSnap.DragBackMode -ne 'TaskbarPreferred' -or
    !$result.Dashboard.UnifiedTitleBar -or $result.Dashboard.InitialAnimationsStarted -lt 1) {
    throw 'A drag, title bar or initial animation check failed; inspect the report.'
}
foreach ($sample in $result.WidgetSamples) {
    if (!$sample.UsesTaskbarPalette) { throw 'The widget is not using the Windows taskbar palette.' }
    if (!$sample.NativeTransparencyAttached -or $sample.NativeTransparencyResult -lt 0 -or $sample.NativeBackgroundClears -lt 1) {
        throw 'Native window transparency setup or background clearing failed.'
    }
    if ($sample.IsTaskbarSlot -and !$sample.SystemHighContrast -and
        (!$sample.TransparentTaskbarSurface -or $sample.SurfaceAlpha -ne 0 -or !$sample.SideEdgesVisible -or
         $sample.SideEdgeAlpha -le 0 -or $sample.SideEdgeAlpha -ge 255)) {
        throw 'Docked widget must have zero background alpha and two translucent side edges.'
    }
    if (!$sample.SystemHighContrast -and (!$sample.TagUsesThemeAccent -or !$sample.StatusHasIndependentColor)) {
        throw 'The widget tag or status color was not restored.'
    }
    $expectedVisible = !$sample.Placement.HiddenForFullscreen
    if (!$sample.ShellEventsActive -or $sample.Visible -ne $expectedVisible -or
        ($sample.Visible -and !$sample.AboveTaskbar) -or $sample.WidthDip -gt 270 -or $sample.HeightDip -gt 65) {
        throw 'A widget visibility or event registration check failed; inspect the report.'
    }
}
foreach ($check in $result.SettingsChecks) {
    if (!$check.UnifiedTitleBar) { throw 'Settings caption buttons and content must share a single themed surface.' }
    if ($check.WidgetPaletteCount -ne 1) { throw 'Changing application themes changed the taskbar widget palette.' }
    if (!$result.FinalWidget.SystemHighContrast -and $check.TagColorCount -lt 4) { throw 'Widget tag colors did not follow theme changes.' }
}
if (!$result.Dashboard.FiveHourAvailable -and !$result.Dashboard.FiveHourUnavailableOnly) {
    throw 'The unavailable five-hour panel is showing something other than its unavailable message.'
}
if (!$result.Dashboard.FiveHourUnavailableStacked -or !$result.NarrowLayout.FiveHourUnavailableStacked) {
    throw 'The unavailable five-hour panel must stack above the weekly panel at full content width and compact height.'
}
if ($result.Dashboard.Bounds.Foreground -and !$result.FinalWidget.Visible) {
    throw 'The widget failed to return after restoring the details window.'
}
Get-Content -LiteralPath $report -Raw
