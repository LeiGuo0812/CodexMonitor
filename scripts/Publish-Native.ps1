param([string]$OutputDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\native'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Build-Native.ps1') -Configuration Release
$target=[IO.Path]::GetFullPath($OutputDirectory)
if((Test-Path -LiteralPath $target) -and (Get-ChildItem -LiteralPath $target -Force | Select-Object -First 1)){throw 'Publish directory must be empty. Use a new output directory.'}
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -LiteralPath (Join-Path $root '.artifacts\native-build\Release\CodexMonitor.exe') -Destination (Join-Path $target 'CodexMonitor.exe')
Get-Item -LiteralPath (Join-Path $target 'CodexMonitor.exe') | Select-Object FullName,Length
