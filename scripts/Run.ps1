param(
    [switch]$Background,
    [switch]$Legacy
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$published = Join-Path $root "publish\win-x64\CodexQuotaMonitor.exe"
$candidates = @(
    (Join-Path $root 'publish\single-file\CodexQuotaMonitor.exe'),
    $published,
    (Join-Path $root "src\CodexQuotaMonitor.App\bin\x64\Release\net10.0-windows10.0.19041.0\CodexQuotaMonitor.exe"),
    (Join-Path $root "src\CodexQuotaMonitor.App\bin\Release\net10.0-windows10.0.19041.0\CodexQuotaMonitor.exe"),
    (Join-Path $root "src\CodexQuotaMonitor.App\bin\x64\Debug\net10.0-windows10.0.19041.0\CodexQuotaMonitor.exe"),
    (Join-Path $root "src\CodexQuotaMonitor.App\bin\Debug\net10.0-windows10.0.19041.0\CodexQuotaMonitor.exe")
)
if (!$Legacy) {
    $candidates = @(
        (Join-Path $root 'publish\native\CodexMonitor.exe'),
        (Join-Path $root '.artifacts\native-build\Release\CodexMonitor.exe'),
        (Join-Path $root '.artifacts\native-build\Debug\CodexMonitor.exe')
    )
}
$exe = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$exe) { throw "Build the app with scripts\Build.ps1 or publish it with scripts\Publish.ps1 first." }
$arguments = @()
if ($Background) { $arguments += "--background" }
if ($arguments.Count -gt 0) {
    Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden
} else {
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden
}
