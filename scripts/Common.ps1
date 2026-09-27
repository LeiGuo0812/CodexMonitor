$script:ProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$localSdk = Join-Path $script:ProjectRoot ".tools\dotnet\dotnet.exe"
$script:DotNetExecutable = if (Test-Path -LiteralPath $localSdk) {
    $localSdk
} elseif ($dotnetCommand) {
    $dotnetCommand.Source
} else {
    Join-Path $script:ProjectRoot ".tools\dotnet\dotnet.exe"
}

function ConvertTo-CommandLineArgument([string]$Value) {
    if ($Value -notmatch '[\s"]') { return $Value }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $slashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($slashes * 2) + 1)))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) {
            [void]$builder.Append(('\' * $slashes))
            $slashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * ($slashes * 2))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-DotNet([string[]]$Arguments) {
    if (!(Test-Path -LiteralPath $script:DotNetExecutable)) {
        throw "Install .NET 10 SDK or run scripts\Install-DotNet.ps1 first."
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $script:DotNetExecutable
    $startInfo.WorkingDirectory = $script:ProjectRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = (($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join " ")
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (!$process.Start()) { throw "Unable to start dotnet.exe." }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()
    [pscustomobject]@{ ExitCode = $exitCode; StandardOutput = $stdout; StandardError = $stderr }
}
