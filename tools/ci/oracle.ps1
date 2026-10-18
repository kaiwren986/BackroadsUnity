<#
.SYNOPSIS
  Oracle check for the gates (PLAN 1.8, 1.9): the snapshot of D:\Backroads plus the Node-only hash
  check; with -Export the full canon export and its verification.

.DESCRIPTION
  A thin wrapper around lane W's tools (it never touches D:\Backroads itself):
    node tools\oracle\make-snapshot.mjs     only when artifacts\oracle\<commit7>\ is missing (or -Fresh)
    node tools\oracle\gen-hash.mjs          six layer hashes in Node (exit 0 = match)
    tools\oracle\run-export.ps1             with -Export (headless Edge export + manifest check)
  Fails with a clear message when one of them is not on this branch yet.

.EXAMPLE
  tools\ci\oracle.ps1
  tools\ci\oracle.ps1 -Export
#>
[CmdletBinding()]
param(
    [switch]$Export,
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
if ($Export) { $budget = 900 }
$gate = New-BrGate -Gate $GateName -Headline $true -BudgetSeconds $budget -Out $Out -NoMachine -FallbackIfFail 'none: canon identity is a headline gate (PLAN 6.1)'
$snapDir = Join-Path (Join-Path $ART 'oracle') $ORACLE_COMMIT.Substring(0, 7)
$mk = Join-Path $REPO 'tools\oracle\make-snapshot.mjs'
$gh = Join-Path $REPO 'tools\oracle\gen-hash.mjs'
$rx = Join-Path $REPO 'tools\oracle\run-export.ps1'
$gate.conditions = [ordered]@{ oracle = $ORACLE; commit = $ORACLE_COMMIT; snapshot = (ConvertTo-BrRelPath $snapDir); export = [bool]$Export }

function Invoke-Step([string]$Name, [string]$File, [string[]]$StepArgs) {
    $log = Join-Path $Out "$Name.log"
    Write-BrLog ('"' + $File + '" ' + (ConvertTo-BrArgString $StepArgs)) 'cmd'
    $r = Invoke-BrNative -FilePath $File -ArgumentList $StepArgs -Echo
    Write-BrText $log (($r.Output -join "`n") + "`n")
    Add-BrEvidence $gate $log
    $gate.metrics["$($Name)ExitCode"] = $r.ExitCode
    return $r.ExitCode
}

try {
    $missing = @()
    if (-not $NODE) { $missing += 'node is not on PATH (env.ps1 $NODE)' }
    if (-not (Test-Path -LiteralPath $mk)) { $missing += 'tools\oracle\make-snapshot.mjs (lane W, T0.7a)' }
    if (-not (Test-Path -LiteralPath $gh)) { $missing += 'tools\oracle\gen-hash.mjs (lane W, T0.7d)' }
    if ($Export -and -not (Test-Path -LiteralPath $rx)) { $missing += 'tools\oracle\run-export.ps1 (lane W, T0.7c)' }
    $needSnap = $Fresh -or -not (Test-Path -LiteralPath $snapDir)
    if ($DryRun) {
        if ($missing.Count) { Add-BrWarning $gate ('not on this branch yet: ' + ($missing -join ', ')) }
        if ($needSnap) { Write-BrLog "node $(ConvertTo-BrRelPath $mk)" 'cmd' } else { Write-BrLog "snapshot cached at $(ConvertTo-BrRelPath $snapDir)" }
        Write-BrLog "node $(ConvertTo-BrRelPath $gh)" 'cmd'
        if ($Export) { Write-BrLog "powershell -File $(ConvertTo-BrRelPath $rx)" 'cmd' }
        Set-BrGateStatus $gate 'dryrun' 'dry run'
    } elseif ($missing.Count) {
        Set-BrGateStatus $gate 'error' ('not on this branch yet: ' + ($missing -join ', ') + '. Use gate.ps1 -SkipOracle until lane W merges.')
    } elseif ($Export -and -not (Assert-BrUserNotPlaying -What 'the canon export' -Wait:$WaitForUser)) {
        Set-BrGateStatus $gate 'blocked' ("the user is playing: $BR_PLAYING_REASON")
    } else {
        $ok = $true
        if ($needSnap) { if ((Invoke-Step 'snapshot' $NODE @($mk)) -ne 0) { $ok = $false; Set-BrGateStatus $gate 'fail' 'make-snapshot.mjs failed (see snapshot.log)' } }
        if ($ok -and (Invoke-Step 'genhash' $NODE @($gh)) -ne 0) { $ok = $false; Set-BrGateStatus $gate 'fail' 'gen-hash.mjs: the oracle layer hashes do not match (see genhash.log)' }
        if ($ok -and $Export) {
            $code = Invoke-BrChildScript -Script $rx -LogFile (Join-Path $Out 'export.log')
            Add-BrEvidence $gate (Join-Path $Out 'export.log')
            $gate.metrics.exportExitCode = $code
            if ($code -ne 0) { $ok = $false; Set-BrGateStatus $gate 'fail' "run-export.ps1 failed (exit $code, see export.log)" }
        }
        if ($ok) { Set-BrGateStatus $gate 'pass' $(if ($Export) { 'snapshot, Node hashes and canon export verified' } else { 'Node layer hashes match the oracle' }) }
    }
} catch {
    Set-BrGateStatus $gate 'error' ("oracle.ps1: " + $_.Exception.Message)
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
