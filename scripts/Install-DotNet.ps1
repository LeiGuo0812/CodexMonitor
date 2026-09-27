param(
    [string]$InstallDirectory = (Join-Path $PSScriptRoot "..\.tools\dotnet")
)

$ErrorActionPreference = "Stop"
$resolvedInstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
New-Item -ItemType Directory -Force -Path $resolvedInstallDirectory | Out-Null
$installerPath = Join-Path $env:TEMP "dotnet-install-codex-quota.ps1"
Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installerPath
& $installerPath -Channel 10.0 -InstallDir $resolvedInstallDirectory -NoPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $resolvedInstallDirectory "dotnet.exe") --list-sdks
