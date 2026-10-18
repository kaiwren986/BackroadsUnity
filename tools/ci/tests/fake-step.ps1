# Test fixture for ci-selftest.ps1: a gate step that ends with the status it is told to.
[CmdletBinding()]
param(
    [ValidateSet('pass', 'fail', 'error', 'blocked', 'invalid', 'nojson')][string]$Mode = 'pass',
    [int]$Seconds = 0,
    [string]$Out = '',
    [string]$GateName = 'fake.step',
    [switch]$DryRun
)
. "$PSScriptRoot\..\env.ps1"
. "$PSScriptRoot\..\ci-lib.ps1"
if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
if ($Mode -eq 'nojson') { exit 0 }
$g = New-BrGate -Gate $GateName -BudgetSeconds 10 -Out $Out -NoMachine
if ($Seconds -gt 0 -and -not $DryRun) { Start-Sleep -Seconds $Seconds }
$g.metrics.mode = $Mode
Set-BrGateStatus $g $Mode "fake step asked to end with $Mode"
exit (Complete-BrGate $g -DryRun:$DryRun)
