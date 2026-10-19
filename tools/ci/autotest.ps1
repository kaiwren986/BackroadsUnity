<#
.SYNOPSIS
  Runs a player autotest (PLAN 1.9): <build>\Backroads.exe -br-autotest <id> (Dev builds, reads
  StreamingAssets\Autotest\<id>.json, writes result.json), or -br-smoke (every build, writes
  smoke.json); exit code 0 = pass, 1 = fail.

.DESCRIPTION
  Every run takes artifacts\.gpu.lock (one GPU/audio autotest at a time). Perf runs (-Perf, -MinSpec
  or an id containing ".perf.") also take artifacts\machine.lock plus every Unity slot, wait for an
  idle machine, and are marked "invalid" (not failed) on battery or on the integrated GPU.
  -MinSpec: affinity = the first 6 E-cores (artifacts\env.json from env-check.ps1), -br-quality Low,
  -br-fps-cap 30, 1920x1080. -Fps N: -br-fps-cap N.
  Player: -Profile Dev (default for -Scenario) = builds\M<n>-dev, Client (default for -Smoke) =
  builds\M<n>; -Exe overrides. Output: artifacts\autotest\<id>[-fpsN|-minspec]\ (the player's files
  and gate.json; -Out overrides), gate JSON artifacts\gates\autotest.<id>[...].json.
  For m0.net.egress and every -Smoke run the script also polls the player's TCP/UDP endpoints
  (Get-NetTCPConnection / Get-NetUDPEndpoint); anything but loopback fails, except Unity's
  PlayerConnection in Development players.

.EXAMPLE
  tools\ci\autotest.ps1 -Scenario smoke -Milestone M0
  tools\ci\autotest.ps1 -Smoke -Profile Client -Milestone M0
  tools\ci\autotest.ps1 -Scenario m0.crash.recover
  tools\ci\autotest.ps1 -Scenario m2.drive.route9 -Fps 30
  tools\ci\autotest.ps1 -Scenario m1.perf.flythrough -MinSpec -Repeat 3
  tools\ci\autotest.ps1 -Exe builds\M0-dev\Backroads.exe -Scenario smoke -DryRun
#>
[CmdletBinding()]
param(
    [string]$Scenario = '',
    [switch]$Smoke,
    [int]$SmokeSeconds = 0,
    [string]$Exe = '',
    [Alias('Profile')][ValidateSet('', 'Dev', 'Client')][string]$BuildProfile = '',
    [string]$Milestone = '',
    [switch]$MinSpec,
    [ValidateSet(0, 30, 45, 60, 120)][int]$Fps = 0,
    [switch]$Perf,
    [ValidateRange(1, 9)][int]$Repeat = 1,
    [int]$TimeoutSec = 600,
    [int]$Width = 0,
    [int]$Height = 0,
    [string]$Quality = '',
    [string]$AffinityMask = '',
    [string[]]$ExtraArgs = @(),
    [switch]$NoEgressCheck,
    [int]$IdleTimeoutSec = 900,
    [string]$Out = '',
    [string]$GateName = '',
    [switch]$Loud,
    [switch]$Force,
    [switch]$WaitForUser,
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

# ---------------------------------------------------------------- arguments
if ($Smoke -and $Scenario) { throw 'use either -Smoke or -Scenario <id>' }
if (-not $Smoke -and -not $Scenario) { throw 'pass -Scenario <id> (e.g. smoke, m0.crash.recover) or -Smoke' }
if ($Scenario -and $Scenario -notmatch '^[a-z0-9]+(\.[a-z0-9-]+)*$') { throw "scenario ids look like m<n>.<area>.<case> (got '$Scenario')" }
$Milestone = Get-BrMilestone $Milestone
$id = $Scenario
if ($Smoke) { $id = 'br-smoke' }
if (-not $BuildProfile) { if ($Smoke) { $BuildProfile = 'Client' } else { $BuildProfile = 'Dev' } }
$isPerf = [bool]($Perf -or $MinSpec -or $id -match '\.perf\.')
$checkEgress = (-not $NoEgressCheck) -and ($Smoke -or $id -eq 'm0.net.egress')
$suffix = ''
if ($MinSpec) { $suffix = '-minspec' } elseif ($Fps -gt 0) { $suffix = "-fps$Fps" }
if (-not $GateName) { $GateName = "autotest.$id$suffix" }
if ($MinSpec) {
    if ($Fps -eq 0) { $Fps = 30 }
    if (-not $Quality) { $Quality = 'Low' }
    if ($Width -eq 0) { $Width = 1920; $Height = 1080 }
}
if ($Width -eq 0) { $Width = 2560 }
if ($Height -eq 0) { $Height = 1440 }
$resultName = 'result.json'
if ($Smoke) { $resultName = 'smoke.json' }

if (-not $Out) {
    # Default folder: keep the previous run as <id>.prev.
    $Out = Join-Path (Join-Path $ART 'autotest') "$id$suffix"
    if ((Test-Path -LiteralPath $Out) -and -not $DryRun) {
        $prev = "$Out.prev"
        if (Test-Path -LiteralPath $prev) { Remove-Item -LiteralPath $prev -Recurse -Force }
        Move-Item -LiteralPath $Out -Destination $prev
    }
} elseif ((Test-Path -LiteralPath $Out) -and -not $DryRun) {
    # A folder given by the caller (gate.ps1 keeps its step log there): only clear stale results.
    foreach ($n in @('result.json', 'smoke.json', 'player.log', 'gate.json')) { Remove-Item -LiteralPath (Join-Path $Out $n) -Force -ErrorAction SilentlyContinue }
    Get-ChildItem -LiteralPath $Out -Directory -Filter 'run*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force $Out | Out-Null

$gate = New-BrGate -Gate $GateName -BudgetSeconds ([Math]::Max(60, $TimeoutSec * $Repeat)) -Out $Out -FallbackIfFail 'see the scenario''s fallback in the milestone card'
$coreClass = 'unpinned'
if ($MinSpec) { $coreClass = 'E-pinned (6 E-cores)' } elseif ($AffinityMask) { $coreClass = "pinned $AffinityMask" }
$fpsLabel = 'uncapped'
if ($Fps -gt 0) { $fpsLabel = "$Fps" }
$qLabel = 'player default'
if ($Quality) { $qLabel = $Quality }
$gate.conditions = [ordered]@{
    scenario = $id; profile = $BuildProfile; milestone = $Milestone; quality = $qLabel; coreClass = $coreClass
    fpsCap = $fpsLabel; resolution = "${Width}x$Height"; perf = $isPerf; repeat = $Repeat; minSpecProxy = [bool]$MinSpec; egressCheck = $checkEgress
}

function Get-PlayerArgs([string]$RunOut, [string[]]$Mode) {
    $a = @('-screen-fullscreen', '0', '-screen-width', "$Width", '-screen-height', "$Height") + $Mode
    $a += @('-br-out', $RunOut, $BR_PLAYER_MARKER)
    if (-not $Loud) { $a += '-br-silent' }
    if ($Fps -gt 0) { $a += @('-br-fps-cap', "$Fps") }
    if ($Quality) { $a += @('-br-quality', $Quality) }
    $a += @('-logFile', (Join-Path $RunOut 'player.log'))
    $a += @($ExtraArgs | Where-Object { $_ })
    return $a
}

function Get-ExitText([int]$Code, [bool]$TimedOut) {
    if ($TimedOut) { return 'timed out (killed)' }
    if ($Code -eq 0) { return 'exit 0' }
    if ($Code -eq 1) { return 'exit 1 (scenario failed)' }
    return ('crashed or aborted (exit {0} / 0x{1:X8})' -f $Code, ([BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$Code), 0)))
}

function Read-Result([string]$RunOut) {
    $p = Join-Path $RunOut $resultName
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    try { return (Read-BrJson $p) } catch { Add-BrWarning $gate "$resultName is not valid JSON: $($_.Exception.Message)"; return $null }
}

function Get-ResultMetrics($Res) {
    # Scalar fields of result.json / smoke.json (frames, p95FrameMs, exceptions, ...), plus a nested
    # "metrics" object if a scenario writes one.
    $m = [ordered]@{}
    if (-not $Res) { return $m }
    foreach ($pp in $Res.PSObject.Properties) {
        if ($pp.Name -in @('status', 'id', 'kind', 'build', 'startedUtc', 'metrics')) { continue }
        $v = $pp.Value
        if ($null -eq $v -or $v -is [ValueType] -or $v -is [string]) { $m[$pp.Name] = $v }
    }
    if ($Res.metrics) { foreach ($pp in $Res.metrics.PSObject.Properties) { $m[$pp.Name] = $pp.Value } }
    return $m
}

$mask = [UInt64]0
try {
    # ------------------------------------------------------------ resolve the player
    $notReady = $null
    if ($Exe) {
        if (-not [IO.Path]::IsPathRooted($Exe)) { $Exe = Join-Path $REPO $Exe }
        if (-not (Test-Path -LiteralPath $Exe)) { $notReady = "no player at $Exe" }
    } else {
        $Exe = Resolve-BrPlayerExe $BuildProfile $Milestone
        if (-not $Exe) {
            $Exe = Join-Path (Get-BrBuildOutDir $BuildProfile $Milestone) 'Backroads.exe'
            $notReady = "no $BuildProfile build for $Milestone yet: run tools\ci\build-player.ps1 -Profile $BuildProfile -Milestone $Milestone"
        }
    }
    $hasTesting = $false; $stamp = $null
    if (-not $notReady) {
        $gate.metrics.exe = ConvertTo-BrRelPath $Exe
        $stamp = Get-BrBuildStamp $Exe
        if ($stamp) { $gate.metrics.buildGit = $stamp.git; $gate.metrics.buildProfile = $stamp.profile; $gate.metrics.buildMilestone = $stamp.milestone }
        $hasTesting = Test-Path -LiteralPath (Join-Path (Get-BrPlayerDataDir $Exe) 'Managed\Backroads.Testing.dll')
        if (-not $Smoke -and -not $hasTesting) {
            $notReady = "$(ConvertTo-BrRelPath $Exe) has no Backroads.Testing assembly (Managed\Backroads.Testing.dll): -br-autotest needs a Dev build"
        }
        if (-not $Smoke -and $hasTesting) {
            $scen = Join-Path (Get-BrPlayerDataDir $Exe) "StreamingAssets\Autotest\$id.json"
            if (-not (Test-Path -LiteralPath $scen)) { $notReady = "this build has no StreamingAssets\Autotest\$id.json (AutotestRunner reads the scenario from there)" }
        }
    }
    $devBuild = $hasTesting -or ($stamp -and $stamp.profile -eq 'Dev')

    # ------------------------------------------------------------ affinity (min-spec proxy)
    if ($AffinityMask) {
        $mask = [Convert]::ToUInt64(($AffinityMask -replace '^0x', ''), 16)
    } elseif ($MinSpec) {
        $ci = Get-BrCoreInfo
        if (-not $ci.minSpecMask -and @($ci.e).Count -ge 6) {
            $m = [UInt64]0; foreach ($e in (@($ci.e) | Select-Object -First 6)) { $m = $m -bor ([UInt64]1 -shl [int]$e) }; $ci.minSpecMask = ('0x{0:X}' -f $m)
        }
        if (-not $ci.minSpecMask -or $ci.minSpecMask -eq '0x0') {
            if (-not $notReady) { $notReady = 'the E-core map is unknown: run tools\ci\env-check.ps1 (writes artifacts\env.json) or pass -AffinityMask 0x...' }
        } else {
            $mask = [Convert]::ToUInt64(("$($ci.minSpecMask)" -replace '^0x', ''), 16)
            $gate.conditions.eCores = @($ci.e | Select-Object -First 6)
            $gate.conditions.coreMapSource = $ci.source
        }
    }
    if ($mask -ne 0) { $gate.conditions.affinityMask = ('0x{0:X}' -f $mask) }

    # ------------------------------------------------------------ dry run / refusal
    $mode = @('-br-autotest', $id)
    if ($Smoke) { $mode = @('-br-smoke'); if ($SmokeSeconds -gt 0) { $mode += @('-br-smoke-seconds', "$SmokeSeconds") } }
    if ($DryRun) {
        if ($notReady) { Add-BrWarning $gate "not runnable yet: $notReady" }
        $locks = '.gpu.lock'
        if ($isPerf) { $locks = 'machine.lock + all Unity slots + .gpu.lock, then wait for an idle machine; invalid on battery' }
        Write-BrLog "locks: $locks" 'cmd'
        for ($k = 1; $k -le $Repeat; $k++) {
            $ro = $Out; if ($Repeat -gt 1) { $ro = Join-Path $Out "run$k" }
            if ($mask -ne 0) { Write-BrLog ('affinity 0x{0:X}' -f $mask) 'cmd' }
            Write-BrLog ('"' + $Exe + '" ' + (ConvertTo-BrArgString (Get-PlayerArgs $ro $mode))) 'cmd'
        }
        Write-BrLog "reads $resultName and the exit code (0 pass, 1 fail)" 'cmd'
        if ($checkEgress) { Write-BrLog 'polls Get-NetTCPConnection/Get-NetUDPEndpoint for the player every 2 s; loopback only' 'cmd' }
        Set-BrGateStatus $gate 'dryrun' 'dry run: commands printed, nothing started'
    } elseif ($notReady) {
        Set-BrGateStatus $gate 'error' $notReady
    } elseif (-not (Assert-BrUserNotPlaying -What "autotest $id" -Wait:$WaitForUser)) {
        Set-BrGateStatus $gate 'blocked' ("the user is playing: $BR_PLAYING_REASON")
    } else {
        # -------------------------------------------------------- locks and perf validity
        $invalid = @()
        $go = $true
        if ($isPerf) {
            if ((Get-BrPowerSource).source -eq 'battery') { $invalid += 'on battery at start'; if (-not $Force) { $go = $false } }
            if ($go) {
                $ml = Enter-BrMachineLock -Purpose "perf autotest $id$suffix" -IdleTimeoutSec $IdleTimeoutSec
                if (-not $ml.Ok) { $go = $false; $invalid += "machine not available for a perf run: $($ml.Reason)"; $gate.metrics.busyProcesses = @($ml.Procs) }
            }
            if (-not $go) { Set-BrGateStatus $gate 'invalid' (($invalid -join '; ') + ' (perf runs count only on mains power and an idle machine; re-run later)') }
        } else {
            $gl = Enter-BrGpuLock -Purpose "autotest $id$suffix"
            if (-not $gl) { $go = $false; Set-BrGateStatus $gate 'blocked' '.gpu.lock not available within 60 min' }
        }

        if ($go) {
            $runs = New-Object System.Collections.ArrayList
            $state = @{ seen = @{}; list = (New-Object System.Collections.ArrayList); next = 0 }
            $egressTick = {
                param($p, $el)
                if ($el.TotalSeconds -lt $state.next) { return }
                $state.next = $el.TotalSeconds + 2
                $ids = @($p.Id) + @(Get-BrChildProcessIds $p.Id)
                foreach ($ep in (Get-BrProcessEndpoints $ids)) {
                    $key = "$($ep.proto) $($ep.local):$($ep.localPort) $($ep.remote):$($ep.remotePort) $($ep.state)"
                    if (-not $state.seen.ContainsKey($key)) { $state.seen[$key] = $true; [void]$state.list.Add($ep) }
                }
            }
            for ($k = 1; $k -le $Repeat; $k++) {
                $ro = $Out
                if ($Repeat -gt 1) { $ro = Join-Path $Out "run$k"; New-Item -ItemType Directory -Force $ro | Out-Null }
                $tick = $null; $tickSec = 5
                if ($checkEgress) { $tick = $egressTick; $tickSec = 1 }
                $pa = Get-PlayerArgs $ro $mode
                Write-BrLog ('"' + $Exe + '" ' + (ConvertTo-BrArgString $pa)) 'cmd'
                $r = Invoke-BrProcess -FilePath $Exe -ArgumentList $pa -TimeoutSec $TimeoutSec -AffinityMask $mask -OnTick $tick -TickSec $tickSec -WorkingDirectory (Split-Path -Parent $Exe)
                $res = Read-Result $ro
                $runInfo = [ordered]@{ run = $k; exitCode = $r.ExitCode; result = (Get-ExitText $r.ExitCode $r.TimedOut); seconds = $r.Seconds; affinity = $r.Affinity; resultFile = ($null -ne $res); status = $null; failures = @(); metrics = (Get-ResultMetrics $res) }
                if ($res) {
                    $runInfo.status = "$($res.status)"
                    $runInfo.failures = @($res.failures | Where-Object { $_ })
                    if ($res.firstProblem -and $runInfo.failures.Count -eq 0 -and $r.ExitCode -ne 0) { $runInfo.failures = @("$($res.firstProblem)") }
                    if ($null -ne $res.developmentBuild) { $devBuild = [bool]$res.developmentBuild }
                    if ($res.endpoints -and $checkEgress) {
                        foreach ($ep in @($res.endpoints)) {
                            $o = [pscustomobject]@{ proto = ("$(Get-BrFirst $ep.protocol $ep.proto)".ToLowerInvariant() -replace '6$', ''); pid = 0; local = "$(Get-BrFirst $ep.localAddress $ep.local)"; localPort = [int](Get-BrFirst $ep.localPort 0); remote = "$(Get-BrFirst $ep.remoteAddress $ep.remote)"; remotePort = [int](Get-BrFirst $ep.remotePort 0); state = "$(Get-BrFirst $ep.state 'Bound')" }
                            if ($o.proto -in @('tcp', 'udp')) {
                                $key = "$($o.proto) $($o.local):$($o.localPort) $($o.remote):$($o.remotePort) $($o.state)"
                                if (-not $state.seen.ContainsKey($key)) { $state.seen[$key] = $true; [void]$state.list.Add($o) }
                            }
                        }
                    }
                }
                $logLine = $null
                $pl = Join-Path $ro 'player.log'
                if (Test-Path -LiteralPath $pl) {
                    $ll = Select-String -LiteralPath $pl -Pattern '\[BR\].*(SMOKE OK|AUTOTEST \S+ (PASS|FAIL))' | Select-Object -Last 1
                    if ($ll) { $logLine = $ll.Line.Trim() }
                }
                $runInfo.logVerdict = $logLine
                if (Test-Path -LiteralPath $ro) {
                    foreach ($f in @(Get-ChildItem -LiteralPath $ro -File | Sort-Object Name | Select-Object -First 30)) { Add-BrEvidence $gate $f.FullName }
                }
                [void]$runs.Add($runInfo)
            }
            $gate.metrics.runs = @($runs)

            # ---------------------------------------------------- verdict
            $passRuns = @($runs | Where-Object { $_.exitCode -eq 0 }).Count
            $needed = [Math]::Floor($Repeat / 2) + 1
            $problems = @()
            if ($passRuns -lt $needed) {
                $why = @()
                foreach ($ru in $runs) {
                    $t = $ru.result
                    if (@($ru.failures).Count) { $t += ': ' + (@($ru.failures) -join '; ') }
                    $why += $t
                }
                $problems += ('player: ' + ($why -join ' | '))
            }
            $noFile = @($runs | Where-Object { -not $_.resultFile -and $_.exitCode -eq 0 })
            if ($noFile.Count) { $problems += "the player exited 0 but wrote no $resultName into -br-out" }
            $disagree = @($runs | Where-Object { $_.resultFile -and $_.exitCode -eq 0 -and $_.status -and $_.status -ne 'pass' })
            if ($disagree.Count) { $problems += "exit 0 but $resultName says '$($disagree[0].status)'" }
            if ($Repeat -gt 1) {
                $med = [ordered]@{}
                foreach ($name in @($runs[0].metrics.Keys)) {
                    $vals = @()
                    foreach ($ru in $runs) { $v = $ru.metrics[$name]; if ($v -is [ValueType] -and -not ($v -is [bool])) { $vals += [double]$v } }
                    if ($vals.Count -eq $Repeat) { $med[$name] = Get-BrMedian $vals }
                }
                $gate.metrics.median = $med
            } else {
                foreach ($name in @($runs[0].metrics.Keys)) { $gate.metrics[$name] = $runs[0].metrics[$name] }
            }
            if ($checkEgress) {
                $viol = @(); $allowed = @()
                foreach ($ep in $state.list) {
                    $t = Test-BrEndpoint $ep $devBuild
                    $line = "$($ep.proto) $($ep.local):$($ep.localPort) -> $($ep.remote):$($ep.remotePort) $($ep.state)"
                    if (-not $t.Ok) { $viol += "$line : $($t.Reason)" } elseif ($t.Allowed) { $allowed += "$line : $($t.Reason)" }
                }
                $gate.metrics.endpointsSeen = $state.list.Count
                $gate.metrics.egressViolations = $viol
                $gate.metrics.egressAllowed = $allowed
                $gate.metrics.devBuild = $devBuild
                $gate.thresholds.egressViolations = 0
                if ($devBuild -and $allowed.Count) { Add-BrWarning $gate 'Development player: Unity PlayerConnection sockets tolerated; the Client build (-Smoke -Profile Client) gives the strict answer' }
                if ($viol.Count) { $problems += ('network egress: ' + ($viol -join '; ')) }
            }
            if ($isPerf) {
                $pw2 = (Get-BrPowerSource).source
                if ($pw2 -eq 'battery') { $invalid += 'on battery at the end of the run' }
                $gate.metrics.powerAtEnd = $pw2
            }
            if ($problems.Count) { Set-BrGateStatus $gate 'fail' ($problems -join '; ') }
            elseif ($invalid.Count) { Set-BrGateStatus $gate 'invalid' (($invalid | Select-Object -Unique) -join '; ') }
            else { Set-BrGateStatus $gate 'pass' ("$passRuns of $Repeat runs passed" + $(if ($runs[0].logVerdict) { " ($($runs[0].logVerdict -replace '^.*\[BR\]\s*', ''))" } else { '' })) }
        }
    }
} catch {
    Set-BrGateStatus $gate 'error' ("autotest.ps1: " + $_.Exception.Message)
} finally {
    Unlock-BrAll
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
