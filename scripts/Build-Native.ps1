param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if(!(Test-Path -LiteralPath $vswhere)){throw 'Install Visual Studio 2022 Build Tools with Desktop development with C++ and a Windows 11 SDK.'}
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$installation){throw 'MSVC x64 build tools are missing.'}
$cmake=Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
if(!(Test-Path -LiteralPath $cmake)){$cmake=(Get-Command cmake -ErrorAction Stop).Source}
$build=Join-Path $root '.artifacts\native-build'
& $cmake -S (Join-Path $root 'native') -B $build -G 'Visual Studio 17 2022' -A x64
if($LASTEXITCODE -ne 0){throw 'Native CMake configuration failed.'}
& $cmake --build $build --config $Configuration --parallel 2
if($LASTEXITCODE -ne 0){throw 'Native build failed.'}
Write-Output (Join-Path $build "$Configuration\CodexMonitor.exe")
