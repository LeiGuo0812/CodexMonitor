param(
    [string]$ApplicationPath=(Join-Path (Split-Path $PSScriptRoot -Parent) '.artifacts\native-build\Release\CodexMonitor.exe'),
    [string]$ReportPath=(Join-Path (Split-Path $PSScriptRoot -Parent) '.artifacts\native-runtime.json')
)
$ErrorActionPreference='Stop'
$exe=[IO.Path]::GetFullPath($ApplicationPath)
$report=[IO.Path]::GetFullPath($ReportPath)
if(!(Test-Path -LiteralPath $exe)){throw 'Build the native application first.'}
if(Test-Path -LiteralPath $report){throw 'Use a new report path so an earlier result cannot be mistaken for this run.'}
New-Item -ItemType Directory -Force -Path (Split-Path $report) | Out-Null
$settings=Join-Path $env:LOCALAPPDATA 'CodexQuotaMonitor\settings.json'
$before=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
$process=Start-Process -FilePath $exe -ArgumentList @('--verify-native','--report',('"'+$report+'"')) -PassThru -WindowStyle Hidden
$deadline=[DateTime]::UtcNow.AddSeconds(90)
while(!$process.WaitForExit(1000)){
    if([DateTime]::UtcNow -gt $deadline){throw "Native verification exceeded 90 seconds (PID $($process.Id))."}
}
if($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report)){throw 'Native verification did not produce a complete report.'}
$after=if(Test-Path -LiteralPath $settings){(Get-FileHash -LiteralPath $settings).Hash}else{''}
if($before -ne $after){throw 'Verification changed the settings file.'}
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if(!$result.querySucceeded -or !$result.trayAdded -or !$result.widgetVisible -or
   !$result.observations.controlsSynchronized -or !$result.observations.menuSynchronized -or
   !$result.observations.clickHandlersReuseDetails -or !$result.observations.viewsReleased -or
   $result.observations.detailsBottomGapPixels -ne 0){throw "Native checks failed; inspect $report"}
Write-Output $report
