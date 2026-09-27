param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "Common.ps1")
if ($env:OS -ne "Windows_NT") {
    throw "The WinUI 3 app can only be built on Windows. Run this script in Windows PowerShell or PowerShell 7."
}

$versionResult = Invoke-DotNet @("--version")
if ($versionResult.ExitCode -ne 0) { throw $versionResult.StandardError }
$sdkVersion = $versionResult.StandardOutput.Trim()
if ([version]($sdkVersion -split "-")[0] -lt [version]"10.0.100") {
    throw "Codex Quota Monitor targets .NET 10 LTS; found SDK $sdkVersion."
}

if (!$SkipTests) {
    $testResult = Invoke-DotNet @("test", "tests\CodexQuotaMonitor.Core.Tests\CodexQuotaMonitor.Core.Tests.csproj", `
        "--configuration", $Configuration, "--nologo")
    Write-Host $testResult.StandardOutput
    if ($testResult.StandardError) { [Console]::Error.WriteLine($testResult.StandardError) }
    if ($testResult.ExitCode -ne 0) { exit $testResult.ExitCode }
}
$buildResult = Invoke-DotNet @("build", "src\CodexQuotaMonitor.App\CodexQuotaMonitor.App.csproj", `
    "--configuration", $Configuration, "--nologo", "-p:Platform=x64")
Write-Host $buildResult.StandardOutput
if ($buildResult.StandardError) { [Console]::Error.WriteLine($buildResult.StandardError) }
exit $buildResult.ExitCode
