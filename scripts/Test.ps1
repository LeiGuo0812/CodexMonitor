param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")

$result = Invoke-DotNet @("test", "tests\CodexQuotaMonitor.Core.Tests\CodexQuotaMonitor.Core.Tests.csproj", `
    "--configuration", $Configuration, "--nologo")
Write-Host $result.StandardOutput
if ($result.StandardError) { [Console]::Error.WriteLine($result.StandardError) }
exit $result.ExitCode
