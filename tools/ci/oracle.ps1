<#
.SYNOPSIS
  Oracle check for the gates (PLAN 1.8, 1.9): canon identity in Node, and with -Export the full
  canon export twice (the M0 headline gate).

.DESCRIPTION
  A thin wrapper around lane W's tools (it never touches D:\Backroads itself):
    node tools\oracle\gen-hash.mjs                       the CI check: imports the snapshot's world.js
                                                         and checks the layers (makes the snapshot
                                                         artifacts\oracle\552f780\ when it is missing)
    node tools\oracle\verify-canon.mjs CanonData\v1      re-checks the committed CanonData\v1 files
    tools\oracle\run-export.ps1 -Twice                   with -Export: headless Edge export into
                                                         artifacts (never into CanonData\v1), twice,
                                                         compared; takes .gpu.lock
  -Fresh re-makes the snapshot first (node tools\oracle\make-snapshot.mjs).
  Fails with a clear message when one of them is not on this branch.

.EXAMPLE
  tools\ci\oracle.ps1
  tools\ci\oracle.ps1 -Export
#>
[CmdletBinding()]
param(
    [switch]$Export,
    [switch]$Once,
    [switch]$Fresh,
    [string]$Out = '',
    [string]$GateName = '',
    [switch]$WaitForUser,
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

if (-not $GateName) { if ($Export) { $GateName = 'oracle.export' } else { $GateName = 'oracle.hash' } }
if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
$budget = 120
if ($Export) { $budget = 1800 }
$gate = New-BrGate -Gate $GateName -Headline $true -BudgetSeconds $budget -Out $Out -NoMachine -FallbackIfFail 'none: canon identity is a headline gate (PLAN 6.1)'
$mk = Join-Path $REPO 'tools\oracle\make-snapshot.mjs'
$gh = Join-Path $REPO 'tools\oracle\gen-hash.mjs'
$vc = Join-Path $REPO 'tools\oracle\verify-canon.mjs'
$rx = Join-Path $REPO 'tools\oracle\run-export.ps1'
$canon = Join-Path $REPO 'CanonData\v1'
$gate.conditions = [ordered]@{ oracle = $ORACLE; commit = $ORACLE_COMMIT; canon = (ConvertTo-BrRelPath $canon); export = [bool]$Export; twice = [bool]($Export -and -not $Once) }

function Invoke-Step([string]$Name, [string[]]$StepArgs) {
    $log = Join-Path $Out "$Name.log"
    Write-BrLog ('node ' + (ConvertTo-BrArgString $StepArgs)) 'cmd'
    $r = Invoke-BrNative -FilePath $NODE -ArgumentList $StepArgs -Echo
    Write-BrText $log (($r.Output -join "`n") + "`n")
    Add-BrEvidence $gate $log
    $gate.metrics["$($Name)ExitCode"] = $r.ExitCode
    $last = @($r.Output | Where-Object { "$_".Trim() } | Select-Object -Last 1)
    if ($last.Count) { $gate.metrics["$($Name)Summary"] = "$($last[0])".Trim() }
    return $r.ExitCode
}

try {
    $missing = @()
    if (-not $NODE) { $missing += 'node is not on PATH (env.ps1 $NODE)' }
    if (-not (Test-Path -LiteralPath $gh)) { $missing += 'tools\oracle\gen-hash.mjs (lane W)' }
    if ($Fresh -and -not (Test-Path -LiteralPath $mk)) { $missing += 'tools\oracle\make-snapshot.mjs (lane W)' }
    if ($Export -and -not (Test-Path -LiteralPath $rx)) { $missing += 'tools\oracle\run-export.ps1 (lane W)' }
    $haveCanon = (Test-Path -LiteralPath (Join-Path $canon 'manifest.json')) -and (Test-Path -LiteralPath $vc)
    $exArgs = @()
    if (-not $Once) { $exArgs += '-Twice' }
    if ($DryRun) {
        if ($missing.Count) { Add-BrWarning $gate ('not on this branch yet: ' + ($missing -join ', ')) }
        if ($Fresh) { Write-BrLog "node $(ConvertTo-BrRelPath $mk)" 'cmd' }
        Write-BrLog "node $(ConvertTo-BrRelPath $gh)" 'cmd'
        if ($haveCanon) { Write-BrLog "node $(ConvertTo-BrRelPath $vc) $(ConvertTo-BrRelPath $canon)" 'cmd' }
        if ($Export) { Write-BrLog ("locks: .gpu.lock; powershell -File $(ConvertTo-BrRelPath $rx) " + ($exArgs -join ' ')) 'cmd' }
        Set-BrGateStatus $gate 'dryrun' 'dry run'
    } elseif ($missing.Count) {
        Set-BrGateStatus $gate 'error' ('not on this branch yet: ' + ($missing -join ', ') + '. Use gate.ps1 -SkipOracle until they are there.')
    } elseif ($Export -and -not (Assert-BrUserNotPlaying -What 'the canon export' -Wait:$WaitForUser)) {
        Set-BrGateStatus $gate 'blocked' ("the user is playing: $BR_PLAYING_REASON")
    } else {
        $ok = $true
        if ($Fresh) { if ((Invoke-Step 'snapshot' @($mk)) -ne 0) { $ok = $false; Set-BrGateStatus $gate 'fail' 'make-snapshot.mjs failed (see snapshot.log)' } }
        if ($ok -and (Invoke-Step 'genhash' @($gh)) -ne 0) { $ok = $false; Set-BrGateStatus $gate 'fail' 'gen-hash.mjs: the oracle world does not match the expected hashes (see genhash.log)' }
        if ($ok -and $haveCanon) {
            if ((Invoke-Step 'verifycanon' @($vc, $canon)) -ne 0) { $ok = $false; Set-BrGateStatus $gate 'fail' 'verify-canon.mjs: CanonData\v1 does not match its manifest or the expected numbers (see verifycanon.log)' }
        } elseif ($ok) { Add-BrWarning $gate 'CanonData\v1 or verify-canon.mjs is missing: only the generator was checked' }
        if ($ok -and $Export) {
            $gl = Enter-BrGpuLock -Purpose 'canon export (headless Edge)'
            if (-not $gl) { $ok = $false; Set-BrGateStatus $gate 'blocked' '.gpu.lock not available within 60 min' }
            else {
                $code = Invoke-BrChildScript -Script $rx -Arguments $exArgs -LogFile (Join-Path $Out 'export.log')
                Unlock-BrFile $gl
                Add-BrEvidence $gate (Join-Path $Out 'export.log')
                $gate.metrics.exportExitCode = $code
                if ($code -ne 0) { $ok = $false; Set-BrGateStatus $gate 'fail' "run-export.ps1 failed (exit $code, see export.log)" }
            }
        }
        if ($ok) {
            $m = 'gen-hash'
            if ($haveCanon) { $m += ' + verify-canon' }
            if ($Export) { $m += ' + export' + $(if ($Once) { '' } else { ' twice (identical)' }) }
            Set-BrGateStatus $gate 'pass' "$m green"
        }
    }
} catch {
    Set-BrGateStatus $gate 'error' ("oracle.ps1: " + $_.Exception.Message)
} finally {
    Unlock-BrAll
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
