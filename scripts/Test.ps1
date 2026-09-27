param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Legacy
)

$ErrorActionPreference = "Stop"
if (!$Legacy) {
    & (Join-Path $PSScriptRoot 'Test-Native.ps1')
    return
}
. (Join-Path $PSScriptRoot "Common.ps1")

$result = Invoke-DotNet @("test", "tests\CodexQuotaMonitor.Core.Tests\CodexQuotaMonitor.Core.Tests.csproj", `
    "--configuration", $Configuration, "--nologo")
Write-Host $result.StandardOutput
if ($result.StandardError) { [Console]::Error.WriteLine($result.StandardError) }
exit $result.ExitCode
