param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\publish\single-file'),
    [switch]$DirectoryBundle
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
if ($env:OS -ne 'Windows_NT') { throw 'Publishing the WinUI app requires Windows.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
if (Get-ChildItem -LiteralPath $output -Force | Select-Object -First 1) {
    throw 'Choose an empty output directory, so stale files cannot enter the release.'
}
$singleFile = if ($DirectoryBundle) { 'false' } else { 'true' }
$result = Invoke-DotNet @('publish', 'src\CodexQuotaMonitor.App\CodexQuotaMonitor.App.csproj',
    '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', '--nologo',
    '-p:Platform=x64', '-p:WindowsAppSDKSelfContained=true', "-p:PublishSingleFile=$singleFile",
    '-p:DebugType=none', '-p:DebugSymbols=false', '-o', $output)
Write-Host $result.StandardOutput
if ($result.StandardError) { [Console]::Error.WriteLine($result.StandardError) }
if ($result.ExitCode -ne 0) { exit $result.ExitCode }
if ($DirectoryBundle) {
    $pri = Join-Path $script:ProjectRoot 'src\CodexQuotaMonitor.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\CodexQuotaMonitor.pri'
    if (!(Test-Path -LiteralPath $pri)) { throw 'The WinUI resource index was not produced.' }
    Copy-Item -LiteralPath $pri -Destination $output -Force
} else {
    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'CodexQuotaMonitor.exe') {
        throw 'Single-file publishing left external files; inspect the output before distributing it.'
    }
}
Write-Host "Published to: $output"
