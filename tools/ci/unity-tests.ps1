<#
.SYNOPSIS
  Runs the Unity Test Framework in batchmode and turns the NUnit XML into a gate JSON (PLAN 1.9).

.DESCRIPTION
  Unity.exe -batchmode [-nographics] -projectPath Game -runTests -testPlatform EditMode|PlayMode
            -testCategory <cats> -testResults <xml> -logFile <log>
  -runTests exits by itself: -quit is never added. Takes one of the 4 Unity slots, refuses to start
  while the user is playing, waits while a perf gate holds machine.lock.
  Gate JSON: artifacts\gates\<GateName>.json (default unity.editmode / unity.playmode).

.EXAMPLE
  tools\ci\unity-tests.ps1 -Platform EditMode
  tools\ci\unity-tests.ps1 -Platform PlayMode -Category "Slow" -MinTests 0
  tools\ci\unity-tests.ps1 -Platform EditMode -Category "Repo;Architecture" -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('EditMode', 'PlayMode')][string]$Platform,
    # Semicolon list; "!X" excludes. Bare names are matched exactly. "all" = no category filter.
    [string]$Category = '!Slow;!PlayerOnly',
    [string]$TestFilter = '',
    [string]$Out = '',
    [string]$GateName = '',
    [switch]$NoGraphics,            # PlayMode only; EditMode always runs with -nographics
    [int]$MinTests = 1,             # fewer executed tests than this is a failure (0 for optional sets)
    [int]$TimeoutMin = 45,
    [string]$ProjectPath = '',
    [switch]$WaitForUser,
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

if (-not $ProjectPath) { $ProjectPath = $PROJ }
if (-not $GateName) { $GateName = 'unity.' + $Platform.ToLowerInvariant() }
if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
$budget = 600
$gate = New-BrGate -Gate $GateName -BudgetSeconds $budget -Out $Out -FallbackIfFail 'fix the failing tests on the lane branch; a lane gate that cannot pass by the milestone adopts its named fallback (PLAN 1.10)'

function ConvertTo-UnityCategoryFilter([string]$Spec) {
    # Unity treats every category as a regex (partial match). Anchor plain names so "Net" does not
    # also select "Network"; exclusions ("!X") are ANDed by the test framework, inclusions ORed.
    if (-not $Spec -or $Spec -eq 'all' -or $Spec -eq '*') { return '' }
    $parts = @()
    foreach ($raw in ($Spec -split ';')) {
        $c = $raw.Trim(); if (-not $c) { continue }
        $neg = $c.StartsWith('!'); if ($neg) { $c = $c.Substring(1) }
        if ($c -match '^[A-Za-z0-9_.]+$') { $c = '^' + [regex]::Escape($c) + '$' }
        if ($neg) { $c = '!' + $c }
        $parts += $c
    }
    return ($parts -join ';')
}

$xml = Join-Path $Out (($Platform.ToLowerInvariant()) + '.xml')
$log = Join-Path $Out (($Platform.ToLowerInvariant()) + '.log')
$catFilter = ConvertTo-UnityCategoryFilter $Category
$gate.conditions.platform = $Platform
$gate.conditions.category = $Category
$gate.conditions.unityCategoryFilter = $catFilter
$gate.conditions.testFilter = $TestFilter
$gate.thresholds.failed = 0
$gate.thresholds.minTests = $MinTests

$uargs = @('-batchmode')
if ($Platform -eq 'EditMode' -or $NoGraphics) { $uargs += '-nographics' }
$uargs += @('-projectPath', $ProjectPath, '-runTests', '-testPlatform', $Platform)
if ($catFilter) { $uargs += @('-testCategory', $catFilter) }
if ($TestFilter) { $uargs += @('-testFilter', $TestFilter) }
$uargs += @('-testResults', $xml)

try {
    $notReady = Test-BrUnityReady $ProjectPath
    $asmName = 'Backroads.Tests.' + $Platform
    $asm = @()
    if (Test-Path -LiteralPath (Join-Path $ProjectPath 'Assets')) {
        $asm = @(Get-ChildItem -LiteralPath (Join-Path $ProjectPath 'Assets') -Recurse -Filter "$asmName.asmdef" -File -ErrorAction SilentlyContinue)
    }
    if (-not $notReady -and $asm.Count -eq 0) { $notReady = "no $asmName.asmdef under $ProjectPath\Assets yet (test assemblies come from card T0.6, lane I-a)" }

    if ($DryRun) {
        if ($notReady) { Add-BrWarning $gate "not runnable yet: $notReady" }
        [void](Initialize-BrNativePlugin -DryRun)
        [void](Invoke-BrUnity -UnityArgs $uargs -LogFile $log -DryRun)
        Set-BrGateStatus $gate 'dryrun' 'dry run: command printed, nothing started'
    } elseif ($notReady) {
        Set-BrGateStatus $gate 'error' $notReady
    } elseif (-not (Assert-BrUserNotPlaying -What "Unity $Platform tests" -Wait:$WaitForUser)) {
        Set-BrGateStatus $gate 'blocked' ("the user is playing: $BR_PLAYING_REASON")
    } else {
        if (Test-Path -LiteralPath $xml) { Remove-Item -LiteralPath $xml -Force }
        $nat = Initialize-BrNativePlugin -LogFile (Join-Path $Out 'build-native.log')
        if ($nat) { throw $nat }
        $r = Invoke-BrUnity -UnityArgs $uargs -LogFile $log -TimeoutMin $TimeoutMin -Purpose "unity-tests $Platform"
        Add-BrEvidence $gate $log
        $gate.metrics.unityExitCode = $r.ExitCode
        $gate.metrics.unitySeconds = $r.Seconds
        if ($r.Problems -and $r.Problems.exitLine) { $gate.metrics.unityExitLine = $r.Problems.exitLine }
        $why = Get-BrUnityFailureReason $r
        if (Test-Path -LiteralPath $xml) {
            Add-BrEvidence $gate $xml
            $res = Read-BrNUnitResults $xml
            foreach ($k in @('total', 'passed', 'failed', 'skipped', 'inconclusive', 'durationSeconds', 'byCategory', 'failedTests')) { $gate.metrics[$k] = $res[$k] }
            if ($res.failed -gt 0) {
                Set-BrGateStatus $gate 'fail' ("$($res.failed) of $($res.total) tests failed; first: " + $res.failedTests[0].name)
            } elseif ($res.total -lt $MinTests) {
                Set-BrGateStatus $gate 'fail' "only $($res.total) tests ran (expected at least $MinTests; check -Category '$Category')"
            } elseif ($r.ExitCode -ne 0) {
                $m = "Unity exited with $($r.ExitCode) although no test failed"
                if ($why) { $m += ": $why" }
                Set-BrGateStatus $gate 'fail' $m
            } else {
                Set-BrGateStatus $gate 'pass' "$($res.passed) passed, $($res.skipped) skipped, 0 failed ($($res.total) total)"
            }
        } else {
            if (-not $why) {
                switch ($r.ExitCode) {
                    3 { $why = 'test run error (exit 3): see the log' }
                    4 { $why = "platform $Platform not found (exit 4)" }
                    default { $why = "Unity exited with $($r.ExitCode) and wrote no result file" }
                }
            }
            if ($r.Problems -and $r.Problems.compile.Count) { $gate.metrics.compileErrors = @($r.Problems.compile) }
            Set-BrGateStatus $gate 'error' $why
        }
    }
} catch {
    Set-BrGateStatus $gate 'error' ("unity-tests.ps1: " + $_.Exception.Message)
} finally {
    Unlock-BrAll
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
