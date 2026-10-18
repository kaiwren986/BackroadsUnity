<#
.SYNOPSIS
  The three gate tiers (PLAN 1.9): merge (<= 20 min, every merge into main), nightly (<= 3 h, on
  main) and milestone (-Gate M<n>: last green nightly + Client build + the headline gates).

.DESCRIPTION
  Steps come from tools\ci\gates.json (tier steps + "include" + the milestone's extra steps). Each
  step runs one tools\ci script in its own PowerShell and writes artifacts\gates\<step>.json; the
  tier result (all steps, wall clock against the budget, machine facts) goes to
  artifacts\gates\<merge|nightly|M<n>>.json, and at the milestone tier also to docs\gates\M<n>.json
  plus docs\gates\M<n>\<step>.json.
  Locks: refuses to start while the user plays (-WaitForUser waits) and pauses before every heavy
  step while they play; the scripts themselves take the Unity slots, .gpu.lock and machine.lock.
  Merge stops at the first failed hard step unless -KeepGoing; the other tiers always run everything.
  At the milestone tier only headline steps decide; failed lane gates are listed, not blocking.

.EXAMPLE
  tools\ci\gate.ps1 -Tier merge
  tools\ci\gate.ps1 -Tier merge -SkipOracle -DryRun
  tools\ci\gate.ps1 -Tier nightly -WaitForUser
  tools\ci\gate.ps1 -Tier milestone -Gate M0
  tools\ci\gate.ps1 -Tier merge -Only unity.editmode,build.dev
  tools\ci\gate.ps1 -Tier merge -Skip vendor.check,native.ctest
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('merge', 'nightly', 'milestone')][string]$Tier,
    [string]$Gate = '',
    [switch]$SkipOracle,
    [string[]]$Only = @(),
    [string[]]$Skip = @(),
    [switch]$KeepGoing,
    [switch]$StrictBudget,
    [switch]$SkipNightlyCheck,
    [string]$OwnersBase = 'main',
    [string]$Config = '',
    [switch]$WaitForUser,
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

if ($Tier -eq 'milestone' -and -not $Gate) { throw 'the milestone tier needs -Gate M<n>' }
$milestone = Get-BrMilestone $Gate
$tierName = $Tier
if ($Tier -eq 'milestone') { $tierName = $milestone }
if (-not $Config) { $Config = Join-Path $PSScriptRoot 'gates.json' }
$cfg = Read-BrJson $Config
if (-not $cfg) { throw "cannot read $Config" }
$Only = @($Only | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$Skip = @($Skip | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

$runDir = New-BrRunDir "gate-$tierName"
$env:BR_GATE_TIER = $Tier
$env:BR_GATE_RUN = ConvertTo-BrRelPath $runDir
# Repo.OwnersRespected (EditMode) checks the branch's changes against this ref; without it the test is
# skipped. The lane comes from the branch name unless BR_OWNERS_LANE is set (lane I-a handoff, item 11).
if (-not $env:BR_OWNERS_BASE -and -not $env:BR_OWNERS_DIFF -and $OwnersBase) { $env:BR_OWNERS_BASE = $OwnersBase }
$budget = $cfg.budgetSeconds.$Tier
$tg = New-BrGate -Gate $tierName -Headline ($Tier -eq 'milestone') -BudgetSeconds $budget -Out $runDir -FallbackIfFail 'fix or revert the failing step on the lane branch; at a milestone, a failed lane gate adopts its fallback (PLAN 1.10)'
$tg.tier = $Tier
$tg.conditions = [ordered]@{ tier = $Tier; milestone = $milestone; skipOracle = [bool]$SkipOracle; only = $Only; skip = $Skip; keepGoing = [bool]($KeepGoing -or $Tier -ne 'merge'); config = (ConvertTo-BrRelPath $Config) }

# ---------------------------------------------------------------- compose the step list
function Get-TierSteps([string]$Name, [hashtable]$Seen) {
    if ($Seen.ContainsKey($Name)) { return @() }
    $Seen[$Name] = $true
    $t = $cfg.tiers.$Name
    if (-not $t) { throw "gates.json has no tier '$Name'" }
    $list = @()
    foreach ($inc in @($t.include)) { if ($inc) { $list += Get-TierSteps "$inc" $Seen } }
    $list += @($t.steps)
    $ms = $cfg.milestones.$milestone
    if ($ms -and $ms.$Name) { $list += @($ms.$Name) }
    return $list
}
$steps = New-Object System.Collections.ArrayList
$ids = @{}
foreach ($s in (Get-TierSteps $Tier @{})) {
    if (-not $s -or -not $s.id) { continue }
    if ($ids.ContainsKey($s.id)) { $steps[$ids[$s.id]] = $s } else { $ids[$s.id] = $steps.Count; [void]$steps.Add($s) }
}

function Test-Pattern([string]$Id, [string[]]$Patterns) {
    foreach ($p in $Patterns) { if ($Id -like $p) { return $true } }
    return $false
}

function Get-StepFlag($Step, [string]$Name, [bool]$Default) {
    $v = $Step.$Name
    if ($null -eq $v) { return $Default }
    return [bool]$v
}

# ---------------------------------------------------------------- run
$results = New-Object System.Collections.ArrayList
$stop = $false
$playingAtStart = $false
if (-not $DryRun) {
    if (-not (Assert-BrUserNotPlaying -What "the $Tier gate" -Wait:$WaitForUser)) { $playingAtStart = $true }
}
Write-BrLog ("gate $tierName ($Tier tier, $milestone): " + $steps.Count + ' steps, budget ' + $(if ($budget) { "$budget s" } else { 'none' }) + ", run folder $(ConvertTo-BrRelPath $runDir)")

foreach ($s in $steps) {
    $id = "$($s.id)"
    $hard = Get-StepFlag $s 'hard' $true
    $headline = Get-StepFlag $s 'headline' $false
    $heavy = Get-StepFlag $s 'heavy' $false
    $perf = Get-StepFlag $s 'perf' $false
    $stepDir = Join-Path $runDir $id
    $rec = [ordered]@{ id = $id; status = 'error'; hard = $hard; headline = $headline; seconds = 0; message = ''; exitCode = $null; json = $null; owner = $s.owner }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $skipWhy = $null
    if ($playingAtStart) { $rec.status = 'blocked'; $rec.message = "not started: the user is playing ($BR_PLAYING_REASON)" }
    elseif ($stop) { $skipWhy = 'not run: an earlier hard step failed (merge tier stops early; -KeepGoing runs everything)' }
    elseif ($Only.Count -and -not (Test-Pattern $id $Only)) { $skipWhy = 'not selected (-Only)' }
    elseif ($Skip.Count -and (Test-Pattern $id $Skip)) { $skipWhy = 'skipped on request (-Skip)' }
    elseif ((Get-StepFlag $s 'oracle' $false) -and $SkipOracle) { $skipWhy = 'skipped on request (-SkipOracle)' }
    if ($skipWhy) { $rec.status = 'skipped'; $rec.message = $skipWhy }

    if ($rec.status -eq 'error') {
        Write-BrLog "---- step $id" 'info'
        New-Item -ItemType Directory -Force $stepDir | Out-Null
        if ($s.builtin -eq 'lastNightly') {
            if ($SkipNightlyCheck) { $rec.status = 'skipped'; $rec.message = 'skipped on request (-SkipNightlyCheck)' }
            else {
                $nj = $null
                try { $nj = Read-BrJson (Join-Path $ART 'gates\nightly.json') } catch { }
                if (-not $nj) { $rec.status = 'fail'; $rec.message = 'no nightly gate result in artifacts\gates\nightly.json: run gate.ps1 -Tier nightly first' }
                elseif ($nj.status -ne 'pass') { $rec.status = 'fail'; $rec.message = "the last nightly ($($nj.finishedAt)) is $($nj.status), not green" }
                else {
                    $head = Invoke-BrGit @('rev-parse', 'HEAD')
                    $ng = "$($nj.build.git)"
                    $anc = Invoke-BrNative -FilePath 'git' -ArgumentList @('-C', $REPO, 'merge-base', '--is-ancestor', $ng, 'HEAD')
                    if ($anc.ExitCode -ne 0) { $rec.status = 'fail'; $rec.message = "the last green nightly ($ng) is not an ancestor of HEAD" }
                    else {
                        $rec.status = 'pass'; $rec.message = "last green nightly $($ng.Substring(0, 7)) at $($nj.finishedAt)"
                        if ($ng -ne $head) {
                            $n = Invoke-BrGit @('rev-list', '--count', "$ng..HEAD")
                            $rec.message += " ($n commits since; they passed the merge tier)"
                        }
                    }
                }
            }
        } else {
            $script = Join-Path $PSScriptRoot "$($s.script)"
            $gateJson = Get-StepFlag $s 'gateJson' $false
            $sargs = @()
            foreach ($a in @($s.args)) { if ($null -ne $a) { $sargs += ("$a" -replace '\{milestone\}', $milestone) } }
            $missingParam = $null
            if (-not (Test-Path -LiteralPath $script)) {
                $rec.status = 'error'
                $own = ''
                if ($s.owner) { $own = " (lane $($s.owner))" }
                $rec.message = "tools/ci/$($s.script) is not on this branch yet$own"
            } elseif ($s.requiresParam) {
                try { if (-not (Get-Command $script).Parameters.ContainsKey("$($s.requiresParam)")) { $missingParam = "$($s.requiresParam)" } } catch { $missingParam = "$($s.requiresParam)" }
            }
            if ($missingParam) { $rec.status = 'skipped'; $rec.message = "tools/ci/$($s.script) has no -$missingParam switch yet" }
            elseif (Test-Path -LiteralPath $script) {
                if ($perf -and -not $DryRun -and (Get-BrPowerSource).source -eq 'battery') {
                    $rec.status = 'invalid'; $rec.message = 'perf step on battery: not run (re-run on mains power)'
                } else {
                    if ($heavy -and -not $DryRun) {
                        if (-not (Wait-BrUserDone -What "step $id" -Gate $tg)) { $rec.status = 'blocked'; $rec.message = 'the user kept playing for 8 h' }
                    }
                    if ($rec.status -eq 'error') {
                        if ($gateJson) { $sargs += @('-Out', $stepDir, '-GateName', $id) }
                        if ($DryRun -and $gateJson) { $sargs += '-DryRun' }
                        $log = Join-Path $stepDir "$id.log"
                        if ($DryRun -and -not $gateJson) {
                            Write-BrLog ("powershell -File tools/ci/$($s.script) " + (ConvertTo-BrArgString $sargs) + '   (foreign script: not run in a dry run)') 'cmd'
                            $rec.status = 'dryrun'; $rec.message = 'dry run'
                        } else {
                            $code = Invoke-BrChildScript -Script $script -Arguments $sargs -LogFile $log
                            $rec.exitCode = $code
                            $jsonPath = Join-Path $stepDir 'gate.json'
                            if ($gateJson -and -not $DryRun) { $jsonPath = Join-Path $ART "gates\$id.json" }
                            $sj = $null
                            if ($gateJson) { try { $sj = Read-BrJson $jsonPath } catch { } }
                            if ($sj -and "$($sj.run)" -ne $env:BR_GATE_RUN) { $sj = $null }   # left over from an older run
                            if ($sj) {
                                $rec.status = "$($sj.status)"; $rec.message = "$($sj.message)"
                                $sj.hard = $hard; $sj.headline = $headline; $sj.tier = $Tier
                                if ($s.fallbackIfFail) { $sj.fallbackIfFail = "$($s.fallbackIfFail)" }
                                Write-BrJson $jsonPath $sj
                                if ($jsonPath -ne (Join-Path $stepDir 'gate.json')) { Write-BrJson (Join-Path $stepDir 'gate.json') $sj }
                                $rec.json = ConvertTo-BrRelPath $jsonPath
                            } elseif ($gateJson) {
                                $rec.status = 'error'; $rec.message = "tools/ci/$($s.script) wrote no gate JSON (exit $code)"
                            } else {
                                # Foreign script (another lane's): exit code decides; we write its gate JSON.
                                $st = 'fail'
                                if ($code -eq 0) { $st = 'pass' }
                                $tail = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Where-Object { $_.Trim() } | Select-Object -Last 1)
                                $rec.status = $st
                                $rec.message = "exit $code" + $(if ($tail.Count) { ': ' + "$($tail[0])".Trim() } else { '' })
                                $fj = [ordered]@{
                                    schema = 1; gate = $id; tier = $Tier; status = $st; hard = $hard; headline = $headline; message = $rec.message
                                    startedAt = (Get-Date).AddSeconds(-$sw.Elapsed.TotalSeconds).ToString('o'); finishedAt = (Get-Date).ToString('o')
                                    build = $tg.build; machine = $null
                                    wallClock = [ordered]@{ seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1); budget = $null; pausedSeconds = 0; overBudget = $false }
                                    conditions = [ordered]@{ script = "tools/ci/$($s.script)"; args = $sargs }
                                    metrics = [ordered]@{ exitCode = $code }; thresholds = [ordered]@{ exitCode = 0 }
                                    evidence = @((ConvertTo-BrRelPath $log)); warnings = @(); fallbackIfFail = $s.fallbackIfFail; exitCode = (Get-BrExitCode $st)
                                    out = (ConvertTo-BrRelPath $stepDir); run = $env:BR_GATE_RUN; script = "tools/ci/$($s.script)"
                                }
                                if (-not $DryRun) { Write-BrJson (Join-Path $ART "gates\$id.json") $fj }
                                Write-BrJson (Join-Path $stepDir 'gate.json') $fj
                                $rec.json = ConvertTo-BrRelPath (Join-Path $ART "gates\$id.json")
                            }
                        }
                    }
                }
            }
        }
    }
    $rec.seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
    $lvl = 'ok'
    if ($rec.status -eq 'fail' -or $rec.status -eq 'error') { $lvl = 'error' } elseif ($rec.status -ne 'pass' -and $rec.status -ne 'dryrun') { $lvl = 'warn' }
    if ($rec.status -ne 'skipped' -or $rec.message -notlike 'not selected*') { Write-BrLog ("step {0}: {1} ({2} s) {3}" -f $id, $rec.status, $rec.seconds, $rec.message) $lvl }
    if ($rec.json) { Add-BrEvidence $tg (Join-Path $REPO $rec.json) }
    [void]$results.Add($rec)
    if ($Tier -eq 'merge' -and -not $KeepGoing -and $hard -and ($rec.status -eq 'fail' -or $rec.status -eq 'error')) { $stop = $true }
}

# ---------------------------------------------------------------- verdict
$deciding = @($results | Where-Object { $_.hard })
if ($Tier -eq 'milestone') { $deciding = @($results | Where-Object { $_.hard -and $_.headline }) }
$failed = @($deciding | Where-Object { $_.status -eq 'fail' -or $_.status -eq 'error' })
$blocked = @($deciding | Where-Object { $_.status -eq 'blocked' })
$invalid = @($deciding | Where-Object { $_.status -eq 'invalid' })
$tg.metrics.steps = @($results)
$tg.metrics.counts = [ordered]@{}
foreach ($st in @('pass', 'fail', 'error', 'skipped', 'blocked', 'invalid', 'dryrun')) { $tg.metrics.counts[$st] = @($results | Where-Object { $_.status -eq $st }).Count }
if ($Tier -eq 'milestone') {
    $tg.metrics.laneGateFailures = @($results | Where-Object { -not $_.headline -and ($_.status -eq 'fail' -or $_.status -eq 'error') } | ForEach-Object { "$($_.id): $($_.message)" })
}
$skippedHard = @($deciding | Where-Object { $_.status -eq 'skipped' -and $_.message -notlike 'not selected*' -and $_.message -notlike 'not run:*' })
foreach ($k in $skippedHard) { Add-BrWarning $tg "hard step $($k.id) $($k.message)" }

if ($DryRun) { Set-BrGateStatus $tg 'dryrun' ("dry run of $($steps.Count) steps") }
elseif ($failed.Count) { Set-BrGateStatus $tg 'fail' ("$($failed.Count) failed: " + (($failed | ForEach-Object { $_.id }) -join ', ')) }
elseif ($blocked.Count) { Set-BrGateStatus $tg 'blocked' ('blocked: ' + (($blocked | ForEach-Object { $_.id }) -join ', ')) }
elseif ($invalid.Count) { Set-BrGateStatus $tg 'invalid' ('invalid (re-run on mains power / idle machine): ' + (($invalid | ForEach-Object { $_.id }) -join ', ')) }
else {
    $passN = @($results | Where-Object { $_.status -eq 'pass' }).Count
    Set-BrGateStatus $tg 'pass' ("$passN steps passed" + $(if ($tg.metrics.counts.skipped) { ", $($tg.metrics.counts.skipped) skipped" } else { '' }))
}
$elapsed = $tg['_sw'].Elapsed.TotalSeconds - $tg.wallClock.pausedSeconds
if ($budget -and $elapsed -gt $budget) {
    Add-BrWarning $tg ("over the $Tier budget: {0:N0} s of {1} s" -f $elapsed, $budget)
    if ($StrictBudget -and $tg.status -eq 'pass') { Set-BrGateStatus $tg 'fail' ("passed but over the $budget s budget (-StrictBudget)") }
}

$code = Complete-BrGate $tg -DryRun:$DryRun
if ($Tier -eq 'milestone' -and -not $DryRun -and $tg.status -ne 'blocked') {
    $docs = Join-Path $REPO 'docs\gates'
    $md = Join-Path $docs $tierName
    New-Item -ItemType Directory -Force $md | Out-Null
    Copy-Item -LiteralPath (Join-Path $ART "gates\$tierName.json") -Destination (Join-Path $docs "$tierName.json") -Force
    foreach ($r in $results) { if ($r.json -and (Test-Path -LiteralPath (Join-Path $REPO $r.json))) { Copy-Item -LiteralPath (Join-Path $REPO $r.json) -Destination (Join-Path $md "$($r.id).json") -Force } }
    Write-BrLog "copied the gate JSON to docs\gates\$tierName.json and docs\gates\$tierName\ (commit them with the milestone report)" 'ok'
}
Write-Host ''
Write-Host ("{0,-34} {1,-8} {2,7}  {3}" -f 'step', 'status', 'seconds', 'message')
foreach ($r in $results) {
    $m = "$($r.message)"
    if ($m.Length -gt 90) { $m = $m.Substring(0, 90) + '...' }
    Write-Host ("{0,-34} {1,-8} {2,7}  {3}" -f $r.id, $r.status, $r.seconds, $m)
}
exit $code
