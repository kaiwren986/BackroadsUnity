# Lane I helper: runs one Unity batchmode -executeMethod on Game\ and returns Unity's exit code.
#   tools\ci\project-unity.ps1 -Method Backroads.Build.Editor.BuildCli.Build -Extra '-profile','Dev','-milestone','M0'
#   tools\ci\project-unity.ps1 -Method Backroads.Build.Editor.BuildCli.CompileServerScripts
# (The CI entry points are I-b's build-player.ps1 / unity-tests.ps1; this is the thin wrapper lane I used for M0.)
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Method,
    [string[]]$Extra = @(),
    [string]$Log = ''
)
. "$PSScriptRoot\env.ps1"
$ErrorActionPreference = 'Stop'
if ((Test-Path (Join-Path $ART 'machine.lock')) -or (Test-Path (Join-Path $LOCKS 'machine.lock'))) { Write-Host 'machine.lock exists: a perf gate is running'; exit 3 }   # $LOCKS: the main checkout's artifacts (machine-wide)
if (-not $Log) { $Log = Join-Path $ART ("unity-" + ($Method -replace '^.*\.', '') + ".log") }
$sw = [Diagnostics.Stopwatch]::StartNew()
$a = @('-batchmode', '-nographics', '-quit', '-projectPath', "`"$PROJ`"", '-executeMethod', $Method, '-logFile', "`"$Log`"") + $Extra
$p = Start-Process -FilePath $UNITY -ArgumentList $a -PassThru -NoNewWindow
$null = $p.Handle
$p.WaitForExit()
$code = $p.ExitCode
Get-Content $Log | Select-String -Pattern '\[BR\]|error CS|Build Finished' | Select-Object -Last 30 | ForEach-Object { $_.Line }
Write-Host ("project-unity: {0} exit {1} in {2:F0} s (log {3})" -f $Method, $code, $sw.Elapsed.TotalSeconds, $Log)
exit $code
