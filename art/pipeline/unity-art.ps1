<#
.SYNOPSIS
  Lane A's Unity batch steps (Backroads.Art.Editor.ArtBatch.Run) through the shared CI locks.

.DESCRIPTION
  Uses tools\ci\ci-lib.ps1: refuses while the user plays, takes one of the Unity slots (waits while a perf gate
  holds machine.lock), and with -Graphics also takes .gpu.lock (renders need a GPU, so no -nographics).
    art\pipeline\unity-art.ps1 -Steps materials
    art\pipeline\unity-art.ps1 -Steps probe,compare
    art\pipeline\unity-art.ps1 -Steps capture -Graphics
  Output: art\vehicles\truck\review\ (or -Out), log in artifacts\art\unity-art-<steps>.log.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Steps,
    [switch]$Graphics,
    [string]$Out = '',
    [int]$TimeoutMin = 30,
    [switch]$DryRun
)
. "$PSScriptRoot\..\..\tools\ci\env.ps1"
. "$PSScriptRoot\..\..\tools\ci\ci-lib.ps1"

$stepList = (@($Steps) -join ',') -replace '\s', ''
$logDir = Join-Path $ART 'art'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ('unity-art-' + ($stepList -replace ',', '-') + '.log')
$uargs = @('-batchmode')
if (-not $Graphics) { $uargs += '-nographics' }
$uargs += @('-quit', '-projectPath', $PROJ, '-executeMethod', 'Backroads.Art.Editor.ArtBatch.Run', '-brArtSteps', $stepList)
if ($Out) { $uargs += @('-brArtOut', $Out) }

if ($DryRun) { [void](Invoke-BrUnity -UnityArgs $uargs -LogFile $log -DryRun); exit 0 }
$why = Test-BrUnityReady
if ($why) { Write-BrLog $why 'error'; exit 2 }
if (-not (Assert-BrUserNotPlaying -What "Unity art steps $stepList")) { exit 3 }
$nat = Initialize-BrNativePlugin
if ($nat) { Write-BrLog $nat 'error'; exit 2 }
$gpu = $null
try {
    if ($Graphics) {
        $gpu = Enter-BrGpuLock -Purpose "art capture ($stepList)" -TimeoutSec 3600
        if (-not $gpu) { Write-BrLog '.gpu.lock did not free up' 'error'; exit 3 }
    }
    $r = Invoke-BrUnity -UnityArgs $uargs -LogFile $log -TimeoutMin $TimeoutMin -Purpose "art $stepList"
} finally { Unlock-BrAll }
$reason = Get-BrUnityFailureReason $r
Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Select-String -Pattern '\[BR\]|error CS|Exception' | Select-Object -Last 40 | ForEach-Object { $_.Line }
if ($reason) { Write-BrLog $reason 'error' }
Write-BrLog ("unity-art {0}: exit {1} in {2} s (log {3})" -f $stepList, $r.ExitCode, $r.Seconds, $log)
exit $r.ExitCode
