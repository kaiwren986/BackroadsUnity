<#
.SYNOPSIS
  Self-test for the tools\ci scripts: syntax under this PowerShell (run it with Windows PowerShell
  5.1), the shared library, every script's -DryRun, the not-ready errors, lfs-budget, notices,
  backup (into a scratch folder), gate composition, worktree-new (in a scratch clone), the contract
  with lane I-a's C# (BuildCli and player switches) and, unless -Quick, end-to-end autotests against a
  stub player compiled from StubPlayer.cs.

.DESCRIPTION
  Everything runs with BR_LOCKS_DIR pointing at a scratch folder, so the real machine locks are
  never touched. Scratch output: artifacts\selftest\<stamp>\. artifacts\gates\merge.json and
  nightly.json are restored afterwards; selftest.* gate files are removed. Exit 0 = all passed.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\ci\tests\ci-selftest.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File tools\ci\tests\ci-selftest.ps1 -Quick
#>
[CmdletBinding()]
param([switch]$Quick, [string]$OnlyGroup = '')

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path "$PSScriptRoot\..\..\..").Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scratch = Join-Path $repoRoot "artifacts\selftest\$stamp"
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:BR_LOCKS_DIR = Join-Path $scratch 'locks'
. "$PSScriptRoot\..\env.ps1"
. "$PSScriptRoot\..\ci-lib.ps1"
$CI = Split-Path -Parent $PSScriptRoot
$FIX = Join-Path $PSScriptRoot 'fixtures'

$results = New-Object System.Collections.ArrayList
function Check([string]$Group, [string]$Name, [scriptblock]$Body) {
    if ($OnlyGroup -and $Group -ne $OnlyGroup) { return }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    try {
        $null = & $Body
        [void]$results.Add([pscustomobject]@{ Group = $Group; Name = $Name; Ok = $true; Error = '' })
        Write-Host ("PASS  {0,-10} {1} ({2:N1} s)" -f $Group, $Name, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    } catch {
        [void]$results.Add([pscustomobject]@{ Group = $Group; Name = $Name; Ok = $false; Error = $_.Exception.Message })
        Write-Host ("FAIL  {0,-10} {1}: {2}" -f $Group, $Name, $_.Exception.Message) -ForegroundColor Red
    }
}
function Assert($Cond, [string]$Msg) { if (-not $Cond) { throw $Msg } }
function Run([string]$Script, [string[]]$ScriptArgs = @()) {
    $p = $Script
    if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path $CI $Script }
    $r = Invoke-BrNative -FilePath $POWERSHELL -ArgumentList (@('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $p) + $ScriptArgs)
    $r | Add-Member -NotePropertyName Text -NotePropertyValue ($r.Output -join "`n")
    return $r
}
function Gate([string]$Name) { return (Read-BrJson (Join-Path $ART "gates\$Name.json")) }
function Start-Holder([string]$Code, [int]$Seconds) {
    # A separate PowerShell that runs $Code (with env + lib loaded) and then sleeps, holding its locks.
    $f = Join-Path $scratch ("holder-" + [guid]::NewGuid().ToString('N') + '.ps1')
    $body = ". '$CI\env.ps1'; . '$CI\ci-lib.ps1'; $Code; Start-Sleep -Seconds $Seconds"
    Write-BrText $f $body
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $POWERSHELL; $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$f`""; $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Milliseconds 2500
    return $p
}

$saved = @{}
foreach ($n in @('merge', 'nightly')) {
    $p = Join-Path $ART "gates\$n.json"
    if (Test-Path -LiteralPath $p) { $saved[$n] = Read-BrText $p }
}

Write-Host "ci-selftest: PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition)); scratch $scratch; locks $LOCKS"
if ($PSVersionTable.PSVersion.Major -ne 5) { Write-Host 'WARN  run this under Windows PowerShell 5.1 (powershell.exe) to check 5.1 compatibility' -ForegroundColor Yellow }

try {
# ================================================================ static
Check 'static' 'every tools\ci script parses (this PowerShell)' {
    $bad = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $CI -Recurse -Filter '*.ps1' -File)) {
        $tok = $null; $err = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tok, [ref]$err)
        foreach ($e in @($err)) { $bad += "$($f.Name):$($e.Extent.StartLineNumber) $($e.Message)" }
    }
    Assert ($bad.Count -eq 0) ($bad -join '; ')
}
Check 'static' 'ASCII only (5.1 reads BOM-less files as ANSI)' {
    $bad = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $CI -Recurse -File -Include '*.ps1', '*.json', '*.bat', '*.cs', '*.md')) {
        $b = [IO.File]::ReadAllBytes($f.FullName)
        $n = 0; foreach ($x in $b) { if ($x -gt 127) { $n++ } }
        if ($n) { $bad += "$($f.Name) ($n bytes)" }
    }
    Assert ($bad.Count -eq 0) ('non-ASCII: ' + ($bad -join ', '))
}
Check 'static' 'no PowerShell 7-only operators (?? ?. ternary pipeline chains)' {
    $bad = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $CI -Recurse -Filter '*.ps1' -File)) {
        $tok = $null; $err = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$tok, [ref]$err)
        foreach ($t in @($tok)) {
            $k = "$($t.Kind)"
            if ($k -in @('QuestionQuestion', 'QuestionQuestionEquals', 'QuestionDot', 'QuestionLBracket', 'AndAnd', 'OrOr')) { $bad += "$($f.Name):$($t.Extent.StartLineNumber) $k" }
        }
    }
    Assert ($bad.Count -eq 0) ($bad -join '; ')
}
Check 'static' 'gates.json: valid, unique ids, scripts exist or are another lane''s' {
    $cfg = Read-BrJson (Join-Path $CI 'gates.json')
    $foreign = @('vendor-check.ps1', 'build-native.ps1', 'env-check.ps1')
    foreach ($tn in @('merge', 'nightly', 'milestone')) {
        $t = $cfg.tiers.$tn
        Assert $t "tier $tn missing"
        $ids = @($t.steps | ForEach-Object { $_.id })
        Assert (($ids | Select-Object -Unique).Count -eq $ids.Count) "duplicate ids in $tn"
        foreach ($s in @($t.steps)) {
            if ($s.builtin) { continue }
            Assert ((Test-Path -LiteralPath (Join-Path $CI $s.script)) -or ($foreign -contains $s.script)) "$tn/$($s.id): $($s.script) missing"
        }
    }
    Assert ($cfg.budgetSeconds.merge -eq 1200 -and $cfg.budgetSeconds.nightly -eq 10800) 'budgets are not 20 min / 3 h'
}
# Contract with lane I-a's C# (catches drift when either side changes; skipped without Game\).
$bc = Join-Path $PROJ 'Assets\Backroads\Build\Editor\BuildCli.cs'
if (Test-Path -LiteralPath $bc) {
    Check 'contract' 'BuildCli: -profile -milestone -out, exit 3 on .user-playing, report, Play.bat with the lock' {
        $t = Read-BrText $bc
        foreach ($w in @('Arg("-profile")', 'Arg("-milestone")', 'Arg("-out")', 'build-{o.profile}-{o.milestone}.json', '.user-playing', 'start \"\" /wait', 'CompileServerScripts', 'milestone + "-server"')) {
            Assert ($t.Contains($w)) "BuildCli.cs no longer contains $w"
        }
    }
    Check 'contract' 'player switches used by the scripts exist in Game\Assets\Backroads' {
        $src = (@(Get-ChildItem -LiteralPath (Join-Path $PROJ 'Assets\Backroads') -Recurse -Filter '*.cs' -File | ForEach-Object { Read-BrText $_.FullName }) -join "`n")
        foreach ($w in @('"-br-autotest"', '"-br-smoke"', '"-br-out"', '"-br-silent"', '"-br-fps-cap"', '"-br-bind"', '"-br-quit-after"', '"-br-smoke-seconds"', 'SERVER BOOT OK', '"result.json"', '"smoke.json"')) {
            Assert ($src.Contains($w)) "no $w in the C# sources"
        }
    }
    Check 'contract' 'every autotest scenario in gates.json for M0 has its StreamingAssets\Autotest\<id>.json' {
        $cfg = Read-BrJson (Join-Path $CI 'gates.json')
        $ids = @()
        foreach ($tn in @('merge', 'nightly', 'milestone')) {
            $all = @($cfg.tiers.$tn.steps) + @($cfg.milestones.M0.$tn)
            foreach ($s in $all) { if ($s -and $s.script -eq 'autotest.ps1') { $i = [array]::IndexOf(@($s.args), '-Scenario'); if ($i -ge 0) { $ids += @($s.args)[$i + 1] } } }
        }
        foreach ($i in ($ids | Select-Object -Unique)) {
            Assert (Test-Path -LiteralPath (Join-Path $PROJ "Assets\StreamingAssets\Autotest\$i.json")) "missing scenario $i.json"
        }
    }
}

# ================================================================ lib
Check 'lib' 'ConvertTo-BrArgString quoting' {
    $s = ConvertTo-BrArgString @('a', 'b c', 'C:\P F\', 'say "hi"', '', '!^Slow$;x')
    Assert ($s -eq 'a "b c" "C:\P F\\" "say \"hi\"" "" !^Slow$;x') "got: $s"
}
Check 'lib' 'Get-BrMedian' {
    Assert ((Get-BrMedian @(9, 7, 8)) -eq 8) 'odd'
    Assert ((Get-BrMedian @(1, 2, 3, 4)) -eq 2.5) 'even'
}
Check 'lib' 'Get-BrMilestone' {
    Assert ((Get-BrMilestone 'm3') -eq 'M3') 'm3'
    $threw = $false; try { [void](Get-BrMilestone 'Mx') } catch { $threw = $true }
    Assert $threw 'Mx accepted'
    Assert ((Get-BrMilestone) -match '^M\d+$') 'default'
}
Check 'lib' 'Read-BrNUnitResults on a Unity-shaped NUnit 3 file' {
    $r = Read-BrNUnitResults (Join-Path $FIX 'nunit-sample.xml')
    Assert ($r.total -eq 5 -and $r.passed -eq 3 -and $r.failed -eq 1 -and $r.skipped -eq 1) "counts $($r.total)/$($r.passed)/$($r.failed)/$($r.skipped)"
    Assert ([Math]::Abs($r.durationSeconds - 12.345) -lt 0.01) "duration $($r.durationSeconds)"
    Assert ($r.failedTests.Count -eq 1 -and $r.failedTests[0].name -eq 'Backroads.Tests.EditMode.Repo.OwnersRespected') 'failed test name'
    Assert ($r.failedTests[0].message -match 'owned by V') 'failure message'
    Assert ($r.byCategory['Repo'].total -eq 2 -and $r.byCategory['Repo'].failed -eq 1) 'Repo category (inherited from the fixture)'
    Assert ($r.byCategory['Slow'].total -eq 1 -and $r.byCategory['Architecture'].total -eq 2) 'test-level categories'
    Assert ($r.byCategory['Uncategorized'].total -eq 1) 'uncategorized'
}
Check 'lib' 'Test-BrEndpoint rules (egress / loopback)' {
    function E($proto, $local, $lp, $remote, $rp, $state) { [pscustomobject]@{ proto = $proto; local = $local; localPort = $lp; remote = $remote; remotePort = $rp; state = $state } }
    Assert (Test-BrEndpoint (E 'tcp' '127.0.0.1' 5000 '127.0.0.1' 6000 'Established') $false).Ok 'loopback tcp'
    Assert (-not (Test-BrEndpoint (E 'tcp' '192.168.1.5' 5000 '93.184.216.34' 443 'Established') $false).Ok) 'outbound tcp'
    Assert (-not (Test-BrEndpoint (E 'tcp' '0.0.0.0' 55000 '0.0.0.0' 0 'Listen') $false).Ok) 'client listen 0.0.0.0'
    $d = Test-BrEndpoint (E 'tcp' '0.0.0.0' 55000 '0.0.0.0' 0 'Listen') $true
    Assert ($d.Ok -and $d.Allowed) 'dev PlayerConnection listen'
    Assert (Test-BrEndpoint (E 'tcp' '::1' 7777 '::' 0 'Listen') $false).Ok 'ipv6 loopback listen'
    Assert (-not (Test-BrEndpoint (E 'udp' '0.0.0.0' 54997 '' 0 'Bound') $false).Ok) 'udp wildcard (client)'
    Assert (Test-BrEndpoint (E 'udp' '0.0.0.0' 54997 '' 0 'Bound') $true).Allowed 'udp wildcard (dev)'
}
Check 'lib' 'locks: a held machine.lock is seen, refused, and freed when its holder exits' {
    $p = Start-Holder "[void](Lock-BrFile (Get-BrLockPath 'machine') 'selftest holder')" 4
    $path = Get-BrLockPath 'machine'
    Assert (Test-BrLockHeld $path) 'not seen as held'
    Assert ($null -eq (Lock-BrFile $path 'second')) 'second holder got it'
    Assert ((Get-BrLockHolder $path) -match 'selftest holder') 'holder info'
    $p.WaitForExit()
    Assert (-not (Test-BrLockHeld $path)) 'still held after the holder exited'
    $l = Lock-BrFile $path 'me'
    Assert $l 'could not take it afterwards'
    Unlock-BrFile $l
    Assert (-not (Test-Path -LiteralPath $path)) 'lock file left behind'
}
Check 'lib' 'locks: a crashed holder (killed) releases its lock' {
    $p = Start-Holder "[void](Lock-BrFile (Get-BrLockPath 'gpu') 'to be killed')" 60
    Assert (Test-BrLockHeld (Get-BrLockPath 'gpu')) 'not held'
    $p.Kill(); $p.WaitForExit()
    Assert (-not (Test-BrLockHeld (Get-BrLockPath 'gpu'))) 'still held after kill'
}
Check 'lib' 'Unity slots: at most 4; a 5th waits and times out' {
    $p = Start-Holder "for (`$i = 1; `$i -le 4; `$i++) { [void](Enter-BrUnitySlot 'holder' 10) }" 6
    $st = Get-BrLockStatus
    Assert ($st.unitySlotsUsed -eq 4) "slots used: $($st.unitySlotsUsed)"
    $l = Enter-BrUnitySlot 'fifth' 2
    Assert ($null -eq $l) 'got a 5th slot'
    $p.WaitForExit()
    $l = Enter-BrUnitySlot 'after' 5
    Assert $l 'no slot after the holder exited'
    Unlock-BrFile $l
}
Check 'lib' 'Unity slots wait while a perf gate holds machine.lock' {
    $p = Start-Holder "[void](Lock-BrFile (Get-BrLockPath 'machine') 'perf holder')" 5
    $l = Enter-BrUnitySlot 'blocked by perf' 2
    Assert ($null -eq $l) 'took a slot during a perf gate'
    $p.WaitForExit()
}
Check 'lib' '.user-playing: fresh = playing, stale = removed' {
    $u = Get-BrLockPath 'user'
    Write-BrText $u "Play.bat test`n"
    Assert (Test-BrUserPlaying).Playing 'fresh lock not seen'
    Assert (-not (Assert-BrUserNotPlaying -What 'selftest')) 'Assert did not refuse'
    (Get-Item -LiteralPath $u).LastWriteTime = (Get-Date).AddMinutes(-10)
    Assert (-not (Test-BrUserPlaying).Playing) 'stale lock still counts'
    Assert (-not (Test-Path -LiteralPath $u)) 'stale lock not removed'
}
Check 'lib' 'agent players (-br-ci, or run from a lane worktree) are not the user' {
    Assert (Test-BrAgentPlayer 'D:\Backroads-Unity\builds\M0-dev\Backroads.exe' 'Backroads.exe -br-autotest smoke -br-ci') 'marker'
    Assert (Test-BrAgentPlayer (Join-Path $WORKTREES 'i\builds\M0\Backroads.exe') 'Backroads.exe -br-smoke') 'worktree path'
    Assert (-not (Test-BrAgentPlayer 'D:\Backroads-Unity\builds\M0\Backroads.exe' '"D:\Backroads-Unity\builds\M0\Backroads.exe"')) 'the user build counted as an agent'
    Assert (-not (Test-BrAgentPlayer 'C:\Users\x\Desktop\M0\Backroads.exe' 'Backroads.exe')) 'a copy elsewhere counted as an agent'
}
Check 'lib' 'gate JSON has every PLAN 1.10 field' {
    $r = Run 'tests\fake-step.ps1' @('-Mode', 'pass', '-GateName', 'selftest.shape')
    Assert ($r.ExitCode -eq 0) "exit $($r.ExitCode)"
    $j = Gate 'selftest.shape'
    foreach ($k in @('gate', 'tier', 'status', 'hard', 'headline', 'build', 'machine', 'wallClock', 'metrics', 'thresholds', 'evidence', 'fallbackIfFail')) {
        Assert ($j.PSObject.Properties.Name -contains $k) "missing $k"
    }
    foreach ($k in @('git', 'unity', 'native', 'abi')) { Assert ($j.build.PSObject.Properties.Name -contains $k) "missing build.$k" }
    foreach ($k in @('seconds', 'budget')) { Assert ($j.wallClock.PSObject.Properties.Name -contains $k) "missing wallClock.$k" }
    Assert ($j.status -eq 'pass' -and $j.tier -eq 'adhoc' -and $j.build.unity -eq $UNITY_VERSION) 'values'
}
Check 'lib' 'machine facts: power, GPU, power mode, cores, other processes' {
    $m = Get-BrMachineInfo
    Assert ($m.power -in @('mains', 'battery', 'unknown')) "power $($m.power)"
    Assert ($m.gpu.controllers.Count -ge 1) 'no GPU listed'
    Assert ($m.Contains('powerMode') -and $m.Contains('cores') -and $m.Contains('otherProcs')) 'fields'
}

# ================================================================ dry runs and not-ready errors
$dry = @(
    @('unity-tests.ps1', @('-Platform', 'EditMode')), @('unity-tests.ps1', @('-Platform', 'PlayMode', '-Category', 'Slow', '-MinTests', '0')),
    @('build-player.ps1', @('-Profile', 'Dev', '-Milestone', 'M0')), @('build-player.ps1', @('-Profile', 'Client')), @('build-player.ps1', @('-Profile', 'ServerHeadless')), @('build-player.ps1', @('-CompileServerScripts')),
    @('autotest.ps1', @('-Scenario', 'smoke')), @('autotest.ps1', @('-Smoke')), @('autotest.ps1', @('-Scenario', 'm2.drive.route9', '-Fps', '30')),
    @('autotest.ps1', @('-Scenario', 'm1.perf.flythrough', '-MinSpec', '-Repeat', '3')), @('autotest.ps1', @('-Scenario', 'm0.crash.recover')), @('autotest.ps1', @('-Scenario', 'm0.net.egress')),
    @('soak.ps1', @('-Clients', '0')), @('soak.ps1', @('-Clients', '4', '-Minutes', '10', '-Net', '150,20,2')),
    @('oracle.ps1', @()), @('oracle.ps1', @('-Export')), @('lfs-budget.ps1', @()), @('notices.ps1', @()), @('backup.ps1', @('-Dest', (Join-Path ([IO.Path]::GetTempPath()) 'br-ci-selftest-bk-dry'))),
    @('worktree-new.ps1', @('-Lane', 'zz', '-Topic', 'selftest', '-Root', (Join-Path $scratch 'wt-dry')))
)
foreach ($d in $dry) {
    $name = "$($d[0]) $($d[1] -join ' ') -DryRun"
    Check 'dryrun' $name {
        $r = Run $d[0] (@($d[1]) + @('-DryRun'))
        Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): " + ($r.Output | Select-Object -Last 3) -join ' | ')
        Assert ($r.Text -match '>    ') 'printed no command'
    }
}
Check 'dryrun' 'soak -Clients 4 prints server and bot commands, all on 127.0.0.1' {
    $r = Run 'soak.ps1' @('-Clients', '2', '-Net', '150,20,2', '-DryRun')
    Assert (@($r.Output | Where-Object { $_ -match '-br-bind 127\.0\.0\.1' }).Count -eq 3) 'not every process binds 127.0.0.1'
    Assert ($r.Text -match '-br-connect 127\.0\.0\.1:7777' -and $r.Text -match '-br-net 150,20,2') 'bot args'
}
Check 'dryrun' 'unity-tests anchors categories and never adds -quit' {
    $r = Run 'unity-tests.ps1' @('-Platform', 'EditMode', '-Category', '!Slow;Net', '-DryRun')
    Assert ($r.Text -match '-testCategory !\^Slow\$;\^Net\$') 'category filter'
    Assert ($r.Text -notmatch ' -quit') '-quit with -runTests'
}
# Never start Unity here: point unity-tests at an empty folder, and run build-player only when Game\ is absent.
$notReady = @(
    @('unity-tests.ps1', @('-Platform', 'EditMode', '-ProjectPath', (Join-Path $scratch 'no-project')), 'no Unity project'),
    @('autotest.ps1', @('-Scenario', 'smoke', '-Milestone', 'M98'), 'no Dev build'),
    @('autotest.ps1', @('-Smoke', '-Milestone', 'M98'), 'no Client build'),
    @('soak.ps1', @('-Clients', '0', '-Milestone', 'M98'), 'no ServerHeadless build'),
    @('soak.ps1', @('-Clients', '2', '-ServerExe', $POWERSHELL, '-ClientExe', $POWERSHELL), 'bot clients need BotClient')
)
if (-not (Test-Path -LiteralPath (Join-Path $PROJ 'ProjectSettings\ProjectVersion.txt'))) { $notReady += , @('build-player.ps1', @('-Profile', 'Dev'), 'no Unity project') }
foreach ($n in $notReady) {
    Check 'notready' "$($n[0]) fails clearly (exit 2) without the C# side" {
        $r = Run $n[0] $n[1]
        Assert ($r.ExitCode -eq 2) "exit $($r.ExitCode)"
        Assert ($r.Text -match $n[2]) ("message: " + ($r.Output | Select-Object -Last 1))
    }
}

# ================================================================ real runs of the light scripts
Check 'lfs' 'lfs-budget measures history and the local store' {
    $r = Run 'lfs-budget.ps1' @('-GateName', 'selftest.lfs')
    Assert ($r.ExitCode -eq 0) "exit $($r.ExitCode)"
    $j = Gate 'selftest.lfs'
    Assert ($null -ne $j.metrics.reachableBytes -and $null -ne $j.metrics.localStoreBytes) 'metrics missing'
    Assert ($j.thresholds.reachableBytes -eq [int64](6 * 1GB)) 'budget is not 6 GiB'
    if ($j.metrics.reachableBytes -gt 0) {
        $r2 = Run 'lfs-budget.ps1' @('-GateName', 'selftest.lfs-tiny', '-MaxGiB', '0.0000001')
        Assert ($r2.ExitCode -eq 1) "tiny budget did not fail (exit $($r2.ExitCode))"
    }
}
Check 'notices' 'notices -Check passes on third-party.json' {
    $r = Run 'notices.ps1' @('-Check', '-GateName', 'selftest.notices-check')
    Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): " + ($r.Output | Select-Object -Last 2) -join ' ')
}
Check 'notices' 'notices builds THIRD_PARTY_NOTICES.txt and licenses\' {
    $od = Join-Path $scratch 'notices'
    $r = Run 'notices.ps1' @('-OutDir', $od, '-GateName', 'selftest.notices')
    Assert ($r.ExitCode -eq 0) "exit $($r.ExitCode)"
    $t = Read-BrText (Join-Path $od 'THIRD_PARTY_NOTICES.txt')
    foreach ($w in @('engine-sim', 'simple-2d-constraint-solver', 'MIT License', 'Apache License', 'SIL OPEN FONT LICENSE', 'Open Database License', 'Contains information from OpenStreetMap', 'Copyright 2022 AngeTheGreat')) {
        Assert ($t.Contains($w)) "missing '$w'"
    }
    Assert (-not $t.Contains('piranha')) 'a non-shipped entry is in the notices'
    Assert (-not $t.Contains('AGENT-WRITTEN')) 'the agent-copy header leaked into the notices'
    Assert (-not $t.Contains("`r")) 'CR in output'
    foreach ($f in @('Apache-2.0.txt', 'OFL-1.1.txt', 'ODbL-1.0.txt')) { Assert (Test-Path -LiteralPath (Join-Path $od "licenses\$f")) "licenses\$f" }
    $up = Join-Path $REPO 'native\EngineSimPlugin\vendor\LICENSES\engine-sim-LICENSE'
    if (Test-Path -LiteralPath $up) {
        Assert (Test-Path -LiteralPath (Join-Path $od 'licenses\engine-sim-LICENSE')) 'the vendored engine-sim LICENSE was not used'
        Assert ($t.Contains((Read-BrText $up).Trim().Split("`n")[0].Trim())) 'the upstream licence text is not in the notices'
    } else { Assert (Test-Path -LiteralPath (Join-Path $od 'licenses\MIT.txt')) 'licenses\MIT.txt' }
}
Check 'notices' 'notices -Check fails on a shipped entry without source or licence' {
    $bad = Join-Path $scratch 'third-party-bad.json'
    Write-BrText $bad '{ "entries": [ { "name": "nosrc", "version": "1", "licence": "MIT", "copyright": "c", "source": "", "shipped": true }, { "name": "nolic", "version": "1", "licence": "", "source": "https://x", "shipped": true }, { "name": "mitnocopy", "version": "1", "licence": "MIT", "source": "https://x", "shipped": true } ] }'
    $r = Run 'notices.ps1' @('-Check', '-Registry', $bad, '-GateName', 'selftest.notices-bad')
    Assert ($r.ExitCode -eq 1) "exit $($r.ExitCode)"
    $j = Gate 'selftest.notices-bad'
    $errs = @($j.metrics.errors) -join "`n"
    foreach ($w in @('nosrc : no source', 'nolic : no licence', 'mitnocopy : MIT requires the copyright')) { Assert ($errs.Contains($w)) "missing error '$w' in: $errs" }
}
$bkScratch = Join-Path ([IO.Path]::GetTempPath()) "br-ci-selftest-$stamp"
Check 'backup' 'backup: bundle + LFS copy into a scratch folder, keeps 7, prunes LFS copies' {
    $dest = Join-Path $bkScratch 'backups'
    for ($i = 1; $i -le 8; $i++) {
        $old = Join-Path $dest ('2026010{0}-000000' -f $i)
        New-Item -ItemType Directory -Force $old | Out-Null
        Write-BrText (Join-Path $old 'backup.json') '{}'
        Write-BrText (Join-Path $old 'lfs-oids.txt') "`n"
    }
    New-Item -ItemType Directory -Force (Join-Path $dest 'keep-me') | Out-Null
    $orphan = Join-Path $dest ('lfs-objects\00\00\' + ('0' * 64))
    Write-BrText $orphan 'orphan'
    $r = Run 'backup.ps1' @('-Dest', $dest, '-Verify', '-GateName', 'selftest.backup')
    Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): " + ($r.Output | Select-Object -Last 2) -join ' ')
    $snaps = @(Get-ChildItem -LiteralPath $dest -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' })
    Assert ($snaps.Count -eq 7) "snapshots kept: $($snaps.Count)"
    Assert (Test-Path -LiteralPath (Join-Path $dest 'keep-me')) 'deleted a folder it did not make'
    Assert (-not (Test-Path -LiteralPath (Join-Path $dest '20260101-000000'))) 'oldest not pruned'
    Assert (-not (Test-Path -LiteralPath $orphan)) 'orphan LFS copy not pruned'
    $new = $snaps | Sort-Object Name -Descending | Select-Object -First 1
    $v = Invoke-BrNative -FilePath 'git' -ArgumentList @('-C', $MAIN_REPO, 'bundle', 'verify', (Join-Path $new.FullName 'backroads.bundle'))
    Assert ($v.ExitCode -eq 0) 'bundle does not verify'
    $j = Gate 'selftest.backup'
    $oids = @(Get-Content -LiteralPath (Join-Path $new.FullName 'lfs-oids.txt') | Where-Object { $_ })
    Assert ($oids.Count -eq $j.metrics.lfsObjects) 'oid list'
    foreach ($o in $oids) { Assert (Test-Path -LiteralPath (Join-Path $dest ('lfs-objects\' + $o.Substring(0, 2) + '\' + $o.Substring(2, 2) + '\' + $o))) "LFS object $o not copied" }
    Assert (@($j.metrics.lfsVerifyFailures).Count -eq 0) 'verify failures'
}
Check 'backup' 'backup refuses a destination inside the repository or a worktree' {
    foreach ($bad in @((Join-Path $REPO 'artifacts\bk'), (Join-Path $MAIN_REPO 'bk'), (Join-Path $WORKTREES 'bk'))) {
        $r = Run 'backup.ps1' @('-Dest', $bad, '-GateName', 'selftest.backup-bad')
        Assert ($r.ExitCode -eq 1) "exit $($r.ExitCode) for $bad"
        Assert (-not (Test-Path -LiteralPath $bad)) "created $bad"
    }
}
Remove-Item -LiteralPath $bkScratch -Recurse -Force -ErrorAction SilentlyContinue

# ================================================================ gate composition
$gcfg = Join-Path $FIX 'gates-test.json'
Check 'gate' 'merge tier: fail-fast on a missing hard step; soft failures do not count' {
    $r = Run 'gate.ps1' @('-Tier', 'merge', '-Config', $gcfg)
    Assert ($r.ExitCode -eq 1) "exit $($r.ExitCode)"
    $j = Gate 'merge'
    $s = @{}; foreach ($x in $j.metrics.steps) { $s[$x.id] = $x }
    Assert ($s['selftest.ok'].status -eq 'pass') 'ok'
    Assert ($s['selftest.soft'].status -eq 'fail' -and -not $s['selftest.soft'].hard) 'soft'
    Assert ($s['selftest.missing'].status -eq 'error' -and $s['selftest.missing'].message -match 'not on this branch') 'missing'
    Assert ($s['selftest.after'].status -eq 'skipped' -and $s['selftest.after'].message -match 'earlier hard step') 'fail-fast'
    Assert ($j.wallClock.overBudget -eq $true -and $j.wallClock.budget -eq 1) 'budget'
    Assert ((Gate 'selftest.ok').tier -eq 'merge') 'child tier'
}
Check 'gate' 'merge tier -KeepGoing -Skip -SkipOracle: green, requiresParam skip, foreign exit code' {
    $r = Run 'gate.ps1' @('-Tier', 'merge', '-Config', $gcfg, '-KeepGoing', '-SkipOracle', '-Skip', 'selftest.missing')
    Assert ($r.ExitCode -eq 0) "exit $($r.ExitCode)"
    $j = Gate 'merge'
    $s = @{}; foreach ($x in $j.metrics.steps) { $s[$x.id] = $x }
    Assert ($s['selftest.oracle'].status -eq 'skipped' -and $s['selftest.oracle'].message -match 'SkipOracle') 'skip oracle'
    Assert ($s['selftest.param'].status -eq 'skipped' -and $s['selftest.param'].message -match '-Asan') 'requiresParam'
    Assert ($s['selftest.nojson'].status -eq 'error') 'gateJson step without JSON'
    Assert ($s['selftest.foreign'].status -eq 'pass') 'foreign step'
    Assert ((Gate 'selftest.foreign').metrics.exitCode -eq 0) 'foreign JSON written by gate.ps1'
    Assert (@($j.warnings | Where-Object { $_ -match 'selftest.missing' }).Count -ge 1) 'warning for a skipped hard step'
}
Check 'gate' 'merge tier -StrictBudget fails a green run that is over budget' {
    $r = Run 'gate.ps1' @('-Tier', 'merge', '-Config', $gcfg, '-KeepGoing', '-Skip', 'selftest.missing', '-StrictBudget')
    Assert ($r.ExitCode -eq 1) "exit $($r.ExitCode)"
}
Check 'gate' 'nightly tier includes merge, later definitions override, keeps going' {
    $r = Run 'gate.ps1' @('-Tier', 'nightly', '-Config', $gcfg)
    Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): " + ($r.Output | Select-Object -Last 12) -join ' | ')
    $j = Gate 'nightly'
    $ids = @($j.metrics.steps | ForEach-Object { $_.id })
    Assert ($ids[0] -eq 'selftest.ok' -and $ids[-1] -eq 'selftest.nightly') ('order: ' + ($ids -join ','))
    Assert ((@($j.metrics.steps | Where-Object { $_.id -eq 'selftest.missing' }))[0].status -eq 'pass') 'override'
}
Check 'gate' 'milestone tier: last green nightly, headline decides, lane failure listed, docs\gates copy' {
    $r = Run 'gate.ps1' @('-Tier', 'milestone', '-Gate', 'M99', '-Config', $gcfg)
    try {
        Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): " + ($r.Output | Select-Object -Last 8) -join ' | ')
        $j = Gate 'M99'
        $s = @{}; foreach ($x in $j.metrics.steps) { $s[$x.id] = $x }
        Assert ($s['selftest.nightly-green'].status -eq 'pass') ('nightly check: ' + $s['selftest.nightly-green'].message)
        Assert ($s['selftest.m99'].status -eq 'pass') 'milestone overlay step'
        Assert (@($j.metrics.laneGateFailures).Count -eq 1) 'lane failure not listed'
        Assert ($j.headline -eq $true) 'tier headline'
        Assert (Test-Path -LiteralPath (Join-Path $REPO 'docs\gates\M99.json')) 'docs\gates\M99.json'
        Assert (Test-Path -LiteralPath (Join-Path $REPO 'docs\gates\M99\selftest.headline.json')) 'docs\gates\M99\<step>.json'
    } finally {
        Remove-Item -LiteralPath (Join-Path $REPO 'docs\gates\M99.json') -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $REPO 'docs\gates\M99') -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $ART 'gates\M99.json') -Force -ErrorAction SilentlyContinue
    }
}
Check 'gate' 'gate refuses to start while the user is playing (exit 3)' {
    Write-BrText (Get-BrLockPath 'user') "selftest`n"
    try {
        $r = Run 'gate.ps1' @('-Tier', 'merge', '-Config', $gcfg, '-Only', 'selftest.ok')
        Assert ($r.ExitCode -eq 3) "exit $($r.ExitCode)"
    } finally { Remove-Item -LiteralPath (Get-BrLockPath 'user') -Force -ErrorAction SilentlyContinue }
}
Check 'gate' 'real gates.json composes every tier in a dry run' {
    foreach ($a in @(@('-Tier', 'merge'), @('-Tier', 'nightly'), @('-Tier', 'milestone', '-Gate', 'M0'), @('-Tier', 'milestone', '-Gate', 'M1'))) {
        $r = Run 'gate.ps1' (@($a) + @('-DryRun'))
        Assert ($r.ExitCode -eq 0) ("$($a -join ' '): exit $($r.ExitCode): " + ($r.Output | Select-Object -Last 3) -join ' | ')
    }
}

# ================================================================ worktree-new in a scratch clone
Check 'worktree' 'worktree-new creates the folder and branch from main and copies the DLL' {
    $clone = Join-Path $scratch 'clone'
    $env:GIT_LFS_SKIP_SMUDGE = '1'
    try {
        $c = Invoke-BrNative -FilePath 'git' -ArgumentList @('clone', '-q', '--no-hardlinks', $MAIN_REPO, $clone)
        Assert ($c.ExitCode -eq 0) ('clone: ' + ($c.Output -join ' '))
        $ci2 = Join-Path $clone 'tools\ci'
        New-Item -ItemType Directory -Force $ci2 | Out-Null
        Copy-Item -Path (Join-Path $CI '*') -Destination $ci2 -Recurse -Force
        $plug = Join-Path $clone 'Game\Assets\Plugins\x86_64'
        New-Item -ItemType Directory -Force $plug | Out-Null
        Write-BrText (Join-Path $plug 'EngineSimPlugin.dll') 'fake dll'
        Write-BrText (Join-Path $plug 'EngineSimPlugin.version.json') '{"abi":1,"sourceHash":"abc"}'
        $root = Join-Path $scratch 'wt'
        $r = Run (Join-Path $ci2 'worktree-new.ps1') @('-Lane', 'zz', '-Topic', 'selftest', '-Root', $root)
        Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): " + ($r.Output | Select-Object -Last 3) -join ' | ')
        $wt = Join-Path $root 'zz'
        Assert (Test-Path -LiteralPath (Join-Path $wt 'AGENTS.md')) 'worktree not checked out'
        $b = Invoke-BrGit @('rev-parse', '--abbrev-ref', 'HEAD') -Repo $wt
        Assert ($b -eq 'm0/zz-selftest') "branch $b"
        Assert (Test-Path -LiteralPath (Join-Path $wt 'Game\Assets\Plugins\x86_64\EngineSimPlugin.dll')) 'DLL not copied'
        Assert ($r.Text -match 'Next steps') 'no next steps'
        $r2 = Run (Join-Path $ci2 'worktree-new.ps1') @('-Lane', 'zz', '-Topic', 'selftest', '-Root', $root)
        Assert ($r2.ExitCode -eq 1 -and $r2.Text -match 'already exists') 'second run did not refuse'
        $r3 = Run (Join-Path $ci2 'worktree-new.ps1') @('-Lane', 'Bad Lane', '-Topic', 'x', '-Root', $root)
        Assert ($r3.ExitCode -ne 0) 'bad lane accepted'
    } finally { Remove-Item Env:\GIT_LFS_SKIP_SMUDGE -ErrorAction SilentlyContinue }
}

# ================================================================ end to end with the stub player
if (-not $Quick) {
    $bdir = Join-Path $scratch 'builds\M0-dev'
    $exe = Join-Path $bdir 'Backroads.exe'
    $data = Join-Path $bdir 'Backroads_Data'
    $env:BR_PLAYER_WINDOWSTYLE = 'Minimized'   # the stub is a console program: keep its windows out of the way
    Check 'e2e' 'compile the stub player (a Dev-shaped build folder)' {
        New-Item -ItemType Directory -Force (Join-Path $data 'Managed'), (Join-Path $data 'StreamingAssets\Autotest') | Out-Null
        Add-Type -TypeDefinition (Read-BrText (Join-Path $PSScriptRoot 'StubPlayer.cs')) -OutputAssembly $exe -OutputType ConsoleApplication
        Write-BrText (Join-Path $data 'Managed\Backroads.Testing.dll') 'stub'
        foreach ($s in @('smoke', 'stub.pass', 'stub.fail', 'stub.liar', 'stub.crash', 'stub.hang', 'stub.noresult', 'stub.perf', 'm0.crash.recover', 'm0.net.egress')) {
            Write-BrText (Join-Path $data "StreamingAssets\Autotest\$s.json") ('{ "id": "' + $s + '" }')
        }
        Write-BrJson (Join-Path $data 'StreamingAssets\build.json') ([ordered]@{ milestone = 'M0'; git = 'stub'; profile = 'Dev'; builtAtUtc = (Get-Date).ToUniversalTime().ToString('o') })
        Assert (Test-Path -LiteralPath $exe) 'no exe'
    }
    $stubOk = Test-Path -LiteralPath $exe
    function AT([string[]]$A) { return (Run 'autotest.ps1' (@('-Exe', $exe, '-Width', '640', '-Height', '360') + $A)) }
    if ($stubOk) {
        Check 'e2e' 'pass: exit 0, result.json fields copied, -br-ci and -br-silent passed' {
            $r = AT @('-Scenario', 'stub.pass', '-GateName', 'selftest.at-pass', '-Out', (Join-Path $scratch 'at\pass'))
            Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): " + ($r.Output | Select-Object -Last 2) -join ' ')
            $res = Read-BrJson (Join-Path $scratch 'at\pass\result.json')
            Assert (@($res.args) -contains '-br-ci' -and @($res.args) -contains '-br-silent' -and @($res.args) -contains '-br-out') 'marker/silent/out'
            $j = Gate 'selftest.at-pass'
            Assert ($j.metrics.p95FrameMs -eq 7.5 -and $j.metrics.frames -eq 180) 'metrics copied'
            Assert ($j.conditions.coreClass -eq 'unpinned' -and $j.conditions.fpsCap -eq 'uncapped') 'conditions'
            Assert ("$($j.message)" -match 'AUTOTEST stub.pass PASS') "log verdict: $($j.message)"
        }
        Check 'e2e' 'fail: exit 1 with the scenario''s failure text' {
            $r = AT @('-Scenario', 'stub.fail', '-GateName', 'selftest.at-fail', '-Out', (Join-Path $scratch 'at\fail'))
            Assert ($r.ExitCode -eq 1 -and (Gate 'selftest.at-fail').message -match 'canary was not where') "exit $($r.ExitCode)"
        }
        Check 'e2e' 'exit 0 but result.json says fail: fails' { $r = AT @('-Scenario', 'stub.liar', '-GateName', 'selftest.at-liar', '-Out', (Join-Path $scratch 'at\liar')); Assert ($r.ExitCode -eq 1) "exit $($r.ExitCode)" }
        Check 'e2e' 'crash: exit 1 with the crash code' {
            $r = AT @('-Scenario', 'stub.crash', '-GateName', 'selftest.at-crash', '-Out', (Join-Path $scratch 'at\crash'))
            Assert ($r.ExitCode -eq 1 -and (Gate 'selftest.at-crash').message -match 'C0000005') "exit $($r.ExitCode)"
        }
        Check 'e2e' 'hang: killed at -TimeoutSec' {
            $r = AT @('-Scenario', 'stub.hang', '-TimeoutSec', '4', '-GateName', 'selftest.at-hang', '-Out', (Join-Path $scratch 'at\hang'))
            Assert ($r.ExitCode -eq 1 -and (Gate 'selftest.at-hang').message -match 'timed out') "exit $($r.ExitCode)"
            Assert (@(Get-Process -Name Backroads -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -eq 0) 'stub still running'
        }
        Check 'e2e' 'exit 0 without result.json fails' { $r = AT @('-Scenario', 'stub.noresult', '-GateName', 'selftest.at-nores', '-Out', (Join-Path $scratch 'at\nores')); Assert ($r.ExitCode -eq 1) "exit $($r.ExitCode)" }
        Check 'e2e' 'a scenario the build does not have: exit 2 before starting' {
            $r = AT @('-Scenario', 'm9.not.there', '-GateName', 'selftest.at-noscen', '-Out', (Join-Path $scratch 'at\noscen'))
            Assert ($r.ExitCode -eq 2 -and (Gate 'selftest.at-noscen').message -match 'Autotest\\m9.not.there.json') "exit $($r.ExitCode)"
        }
        Check 'e2e' '-Fps 30 and -AffinityMask reach the player' {
            $r = AT @('-Scenario', 'stub.pass', '-Fps', '30', '-AffinityMask', '0x3', '-GateName', 'selftest.at-fps', '-Out', (Join-Path $scratch 'at\fps'))
            Assert ($r.ExitCode -eq 0) "exit $($r.ExitCode)"
            $res = Read-BrJson (Join-Path $scratch 'at\fps\result.json')
            $a = @($res.args)
            Assert ($a[[array]::IndexOf($a, '-br-fps-cap') + 1] -eq '30') 'fps cap'
            Assert ($res.affinity -eq '0x3') "affinity $($res.affinity)"
        }
        Check 'e2e' '-Repeat 3: majority and medians' {
            $r = AT @('-Scenario', 'stub.perf', '-Repeat', '3', '-GateName', 'selftest.at-rep', '-Out', (Join-Path $scratch 'at\rep'))
            Assert ($r.ExitCode -eq 0) "exit $($r.ExitCode)"
            $j = Gate 'selftest.at-rep'
            Assert ($j.metrics.median.p95FrameMs -eq 8) "median $($j.metrics.median.p95FrameMs)"
            Assert (@($j.metrics.runs).Count -eq 3) 'runs'
        }
        Check 'e2e' '-MinSpec: E-core affinity, Low, 30 fps, 1920x1080 (or invalid when the machine is busy)' {
            $env:BR_ENV_JSON = Join-Path $FIX 'env.json'
            try {
                $r = Run 'autotest.ps1' @('-Exe', $exe, '-Scenario', 'stub.pass', '-MinSpec', '-IdleTimeoutSec', '3', '-GateName', 'selftest.at-minspec', '-Out', (Join-Path $scratch 'at\minspec'))
            } finally { Remove-Item Env:\BR_ENV_JSON -ErrorAction SilentlyContinue }
            $j = Gate 'selftest.at-minspec'
            Assert ($j.conditions.affinityMask -eq '0xFC' -and $j.conditions.quality -eq 'Low' -and $j.conditions.fpsCap -eq '30' -and $j.conditions.resolution -eq '1920x1080') 'conditions'
            if ($r.ExitCode -eq 0) {
                $res = Read-BrJson (Join-Path $scratch 'at\minspec\result.json')
                Assert ($res.affinity -eq '0xFC') "affinity $($res.affinity)"
                Assert (@($res.args) -contains 'Low') 'quality arg'
            } else {
                Assert ($r.ExitCode -eq 4 -and $j.status -eq 'invalid') "exit $($r.ExitCode) status $($j.status)"
                Write-Host "      (machine busy: the perf run was marked invalid, as designed: $($j.message))"
            }
        }
        Check 'e2e' '-Smoke runs -br-smoke and reads smoke.json' {
            $r = AT @('-Smoke', '-SmokeSeconds', '2', '-GateName', 'selftest.at-smoke', '-Out', (Join-Path $scratch 'at\smoke'))
            Assert ($r.ExitCode -eq 0) "exit $($r.ExitCode): $((Gate 'selftest.at-smoke').message)"
            $res = Read-BrJson (Join-Path $scratch 'at\smoke\smoke.json')
            Assert (@($res.args) -contains '-br-smoke' -and @($res.args) -contains '-br-smoke-seconds') 'flags'
            $j = Gate 'selftest.at-smoke'
            Assert ($j.conditions.egressCheck -eq $true -and $j.metrics.frames -eq 1300) 'egress check / metrics'
        }
        Check 'e2e' 'm0.crash.recover runs like any scenario (the player relaunches itself)' {
            $r = AT @('-Scenario', 'm0.crash.recover', '-GateName', 'selftest.at-crashrec', '-Out', (Join-Path $scratch 'at\crashrec'))
            Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): " + (Gate 'selftest.at-crashrec').message)
        }
        Check 'e2e' 'm0.net.egress: polls the player''s sockets, loopback is fine' {
            $r = AT @('-Scenario', 'm0.net.egress', '-GateName', 'selftest.at-egress', '-Out', (Join-Path $scratch 'at\egress'))
            $j = Gate 'selftest.at-egress'
            Assert ($r.ExitCode -eq 0) ("exit $($r.ExitCode): $($j.message)")
            Assert ($j.metrics.endpointsSeen -ge 1) 'no endpoint seen'
            Assert (@($j.metrics.egressViolations).Count -eq 0) 'violations'
        }
        Check 'e2e' 'autotest refuses while the user plays (exit 3) and waits for .gpu.lock' {
            Write-BrText (Get-BrLockPath 'user') "selftest`n"
            try { $r = AT @('-Scenario', 'stub.pass', '-GateName', 'selftest.at-user', '-Out', (Join-Path $scratch 'at\user')) } finally { Remove-Item -LiteralPath (Get-BrLockPath 'user') -Force -ErrorAction SilentlyContinue }
            Assert ($r.ExitCode -eq 3) "exit $($r.ExitCode)"
            Assert ((Gate 'selftest.at-user').message -match 'user-playing') 'reason not in the message'
            $p = Start-Holder "[void](Lock-BrFile (Get-BrLockPath 'gpu') 'selftest gpu holder')" 10
            $r = AT @('-Scenario', 'stub.pass', '-GateName', 'selftest.at-gpu', '-Out', (Join-Path $scratch 'at\gpu'))
            $p.WaitForExit()
            Assert ($r.ExitCode -eq 0) "exit $($r.ExitCode)"
            Assert ($r.Text -match 'waiting for \.gpu\.lock') 'did not wait for the GPU lock'
        }
        Check 'e2e' 'a child script whose grandchild outlives it does not hang the caller' {
            # Like Unity's Licensing client: the child starts a long-lived process and exits at once.
            $f = Join-Path $scratch 'spawner.ps1'
            Write-BrText $f "Start-Process -FilePath '$exe' -ArgumentList '-br-autotest','stub.hang' -NoNewWindow; Write-Host 'spawned'; exit 7"
            $sw = [Diagnostics.Stopwatch]::StartNew()
            $code = Invoke-BrChildScript -Script $f -LogFile (Join-Path $scratch 'spawner.log')
            $t = $sw.Elapsed.TotalSeconds
            Get-Process -Name Backroads -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force
            Assert ($code -eq 7) "exit $code"
            Assert ($t -lt 20) "took $t s"
            Assert ((Read-BrSharedText (Join-Path $scratch 'spawner.log')) -match 'spawned') 'output not logged'
        }
    }
    Remove-Item Env:\BR_PLAYER_WINDOWSTYLE -ErrorAction SilentlyContinue
}
} finally {
    foreach ($n in @('merge', 'nightly')) {
        $p = Join-Path $ART "gates\$n.json"
        if ($saved.ContainsKey($n)) { Write-BrText $p $saved[$n] } else { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }
    }
    Get-ChildItem -LiteralPath (Join-Path $ART 'gates') -Filter 'selftest.*.json' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

$fail = @($results | Where-Object { -not $_.Ok })
Write-Host ''
Write-Host ("ci-selftest: {0} checks, {1} failed (PowerShell {2})" -f $results.Count, $fail.Count, $PSVersionTable.PSVersion)
foreach ($f in $fail) { Write-Host ("  FAIL {0} / {1}: {2}" -f $f.Group, $f.Name, $f.Error) -ForegroundColor Red }
Write-BrJson (Join-Path $scratch 'selftest.json') ([ordered]@{ powershell = "$($PSVersionTable.PSVersion)"; checks = $results.Count; failed = $fail.Count; results = @($results) })
if ($fail.Count) { exit 1 }
exit 0
