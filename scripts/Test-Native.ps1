$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$cmake=Join-Path $installation 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$build=Join-Path $root '.artifacts\native-tests'
& $cmake -S (Join-Path $root 'native') -B $build -G 'Visual Studio 17 2022' -A x64 -DCQM_BUILD_TESTS=ON
if($LASTEXITCODE -ne 0){throw 'CMake configuration failed.'}
& $cmake --build $build --config Release --target CodexMonitorCoreTests --parallel 2
if($LASTEXITCODE -ne 0){throw 'Native test build failed.'}
& (Join-Path $build 'Release\CodexMonitorCoreTests.exe')
if($LASTEXITCODE -ne 0){throw 'Native tests failed.'}
