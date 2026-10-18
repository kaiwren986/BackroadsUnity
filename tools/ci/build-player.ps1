<#
.SYNOPSIS
  Builds a player through BuildCli in batchmode (PLAN 1.9) and adds the full third-party notices.

.DESCRIPTION
  Unity.exe -batchmode -nographics -quit -projectPath Game
            -executeMethod Backroads.Build.Editor.BuildCli.Build -profile <P> -milestone M<n> -out <dir> -logFile <log>
  BuildCli (lane I-a) writes the player, Play.bat (creates and removes artifacts\.user-playing),
  PLAYTEST.md, <Product>_Data\StreamingAssets\build.json, Server.bat for servers, and the report
  artifacts\build-<profile>-<milestone>.json. Its exit codes: 0 built, 1 failed, 2 bad arguments,
  3 refused because artifacts\.user-playing exists. This script then replaces BuildCli's list-only
  THIRD_PARTY_NOTICES.txt with the one from notices.ps1 (licence texts + licenses\ folder).
  Folders: Client -> builds\M<n>\ (the user's build), Dev -> builds\M<n>-dev\ (autotests),
  ServerHeadless -> builds\M<n>-server\. -Out overrides.
  With -CompileServerScripts it runs BuildCli.CompileServerScripts (PLAN 1.5 rule 7) instead.
  Before Unity starts, tools\ci\build-native.ps1 runs if the plugin DLL is missing (otherwise Unity
  would delete the plugin's .meta files).

.EXAMPLE
  tools\ci\build-player.ps1 -Profile Dev -Milestone M0
  tools\ci\build-player.ps1 -Profile Client -Milestone M0
  tools\ci\build-player.ps1 -Profile ServerHeadless
  tools\ci\build-player.ps1 -CompileServerScripts
  tools\ci\build-player.ps1 -Profile Client -DryRun
#>
[CmdletBinding()]
param(
    [Alias('Profile')][ValidateSet('Dev', 'Client', 'ServerHeadless', 'ServerLinux')][string]$BuildProfile = 'Dev',
    [string]$Milestone = '',
    [switch]$CompileServerScripts,
    [string]$BuildDir = '',
    [string]$Out = '',
    [string]$GateName = '',
    [int]$TimeoutMin = 60,
    [switch]$SkipNotices,
    [switch]$WaitForUser,
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

$Milestone = Get-BrMilestone $Milestone
if (-not $GateName) {
    if ($CompileServerScripts) { $GateName = 'build.servercompile' } else { $GateName = 'build.' + $BuildProfile.ToLowerInvariant() }
}
if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
$budget = 900
if ($CompileServerScripts) { $budget = 300 }
$gate = New-BrGate -Gate $GateName -BudgetSeconds $budget -Out $Out -FallbackIfFail 'fix the build on the lane branch; main must always build (PLAN 1.10)'
if (-not $BuildDir -and -not $CompileServerScripts) { $BuildDir = Get-BrBuildOutDir $BuildProfile $Milestone }
if ($BuildDir -and -not [IO.Path]::IsPathRooted($BuildDir)) { $BuildDir = Join-Path $REPO $BuildDir }
$gate.conditions = [ordered]@{ profile = $BuildProfile; milestone = $Milestone; compileServerScripts = [bool]$CompileServerScripts; buildDir = (ConvertTo-BrRelPath $BuildDir) }

$method = 'Backroads.Build.Editor.BuildCli.Build'
if ($CompileServerScripts) { $method = 'Backroads.Build.Editor.BuildCli.CompileServerScripts' }
$log = Join-Path $Out 'build.log'
$uargs = @('-batchmode', '-nographics', '-quit', '-projectPath', $PROJ, '-executeMethod', $method)
if (-not $CompileServerScripts) { $uargs += @('-profile', $BuildProfile, '-milestone', $Milestone, '-out', $BuildDir) }

try {
    $notReady = Test-BrUnityReady
    if (-not $notReady) {
        $hits = @(Find-BrCSharp 'class\s+BuildCli\b')
        if ($hits.Count -eq 0) { $notReady = 'Backroads.Build.Editor.BuildCli is not in Game\Assets yet (lane I-a)' }
        elseif ($CompileServerScripts -and -not (Select-String -LiteralPath $hits -Pattern '\bCompileServerScripts\s*\(' -Quiet)) {
            $notReady = 'BuildCli has no CompileServerScripts() yet (PLAN 1.5 rule 7, lane I-a)'
        }
    }
    if (-not $notReady -and $BuildProfile -eq 'ServerLinux' -and -not $CompileServerScripts) {
        # BuildCli has no ServerLinux profile yet either; it arrives with U4b (M12).
        $notReady = 'ServerLinux needs "Linux Dedicated Server Build Support" in Unity Hub (decision U4b) and a ServerLinux profile in BuildCli'
    }

    if ($DryRun) {
        if ($notReady) { Add-BrWarning $gate "not runnable yet: $notReady" }
        [void](Initialize-BrNativePlugin -DryRun)
        [void](Invoke-BrUnity -UnityArgs $uargs -LogFile $log -DryRun)
        if (-not $CompileServerScripts -and -not $SkipNotices) { Write-BrLog "then: tools\ci\notices.ps1 -OutDir $(ConvertTo-BrRelPath $BuildDir) (full notices + licenses\)" 'cmd' }
        Set-BrGateStatus $gate 'dryrun' 'dry run: command printed, nothing started'
    } elseif ($notReady) {
        Set-BrGateStatus $gate 'error' $notReady
    } elseif (-not (Assert-BrUserNotPlaying -What 'a Unity build' -Wait:$WaitForUser)) {
        Set-BrGateStatus $gate 'blocked' ("the user is playing: $BR_PLAYING_REASON")
    } else {
        $nat = Initialize-BrNativePlugin -LogFile (Join-Path $Out 'build-native.log')
        if ($nat) { throw $nat }
        $since = Get-Date
        $r = Invoke-BrUnity -UnityArgs $uargs -LogFile $log -TimeoutMin $TimeoutMin -Purpose "build $BuildProfile $Milestone"
        Add-BrEvidence $gate $log
        $gate.metrics.unityExitCode = $r.ExitCode
        $gate.metrics.unitySeconds = $r.Seconds
        $why = Get-BrUnityFailureReason $r
        $report = $null
        if (-not $CompileServerScripts) {
            $report = Get-BrBuildReport $BuildProfile $Milestone
            $rp = Join-Path $ART "build-$BuildProfile-$Milestone.json"
            if ($report -and (Test-Path -LiteralPath $rp) -and (Get-Item -LiteralPath $rp).LastWriteTime -ge $since.AddSeconds(-2)) {
                Add-BrEvidence $gate $rp
                $gate.metrics.buildCli = $report
            } else { $report = $null }
        }
        if ($r.ExitCode -eq 3 -and -not $CompileServerScripts) {
            Set-BrGateStatus $gate 'blocked' 'BuildCli refused: artifacts\.user-playing appeared (the user started playing)'
        } elseif ($r.ExitCode -ne 0 -or $why) {
            if (-not $why) {
                $why = "Unity exited with $($r.ExitCode)"
                if ($report -and $report.error) { $why += ": $($report.error)" }
                if ($r.ExitCode -eq 2) { $why += ' (BuildCli: bad arguments)' }
            }
            if ($r.Problems -and $r.Problems.compile.Count) { $gate.metrics.compileErrors = @($r.Problems.compile) }
            Set-BrGateStatus $gate 'fail' $why
        } elseif ($CompileServerScripts) {
            Set-BrGateStatus $gate 'pass' 'player scripts compile with UNITY_SERVER'
        } else {
            $exe = $null
            if ($report -and $report.exe) { $exe = "$($report.exe)" }
            if (-not $exe -or -not (Test-Path -LiteralPath $exe)) {
                Set-BrGateStatus $gate 'fail' "BuildCli exited 0 but no player is at $exe (report artifacts\build-$BuildProfile-$Milestone.json)"
            } else {
                $dir = Split-Path -Parent $exe
                $problems = @()
                $stamp = Get-BrBuildStamp $exe
                if (-not $stamp) { $problems += 'no <Product>_Data\StreamingAssets\build.json in the player' }
                elseif ($stamp.profile -ne $BuildProfile) { $problems += "build.json says profile $($stamp.profile), expected $BuildProfile" }
                else { $gate.metrics.buildInfo = $stamp }
                if ($BuildProfile -eq 'Dev' -or $BuildProfile -eq 'Client') {
                    $pb = Join-Path $dir 'Play.bat'
                    if (-not (Test-Path -LiteralPath $pb)) { $problems += 'BuildCli wrote no Play.bat' }
                    elseif (-not (Select-String -LiteralPath $pb -Pattern '\.user-playing' -SimpleMatch -Quiet)) { $problems += 'Play.bat does not create artifacts\.user-playing' }
                    else { Add-BrEvidence $gate $pb }
                    $hasTesting = Test-Path -LiteralPath (Join-Path (Get-BrPlayerDataDir $exe) 'Managed\Backroads.Testing.dll')
                    $gate.metrics.hasTestingAssembly = $hasTesting
                    if ($BuildProfile -eq 'Dev' -and -not $hasTesting) { $problems += 'Dev build without Managed\Backroads.Testing.dll: autotests cannot run on it' }
                    if ($BuildProfile -eq 'Client' -and $hasTesting) { $problems += 'Client build contains Backroads.Testing.dll (testing code must not ship)' }
                }
                if (-not $SkipNotices) {
                    $nc = Invoke-BrChildScript -Script (Join-Path $PSScriptRoot 'notices.ps1') -Arguments @('-OutDir', $dir, '-Milestone', $Milestone, '-GateName', "$GateName.notices", '-Out', (Join-Path $Out 'notices')) -LogFile (Join-Path $Out 'notices.log')
                    if ($nc -ne 0) { $problems += "notices.ps1 failed (exit $nc)" } else { Add-BrEvidence $gate (Join-Path $dir 'THIRD_PARTY_NOTICES.txt') }
                }
                $files = @(Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction SilentlyContinue)
                $gate.metrics.exe = ConvertTo-BrRelPath $exe
                $gate.metrics.fileCount = $files.Count
                $gate.metrics.sizeBytes = [int64](($files | Measure-Object -Property Length -Sum).Sum)
                if ($problems.Count) { Set-BrGateStatus $gate 'fail' ($problems -join '; ') }
                else { Set-BrGateStatus $gate 'pass' ("$BuildProfile build at $(ConvertTo-BrRelPath $dir) (" + (Format-BrBytes $gate.metrics.sizeBytes) + ')') }
            }
        }
    }
} catch {
    Set-BrGateStatus $gate 'error' ("build-player.ps1: " + $_.Exception.Message)
} finally {
    Unlock-BrAll
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
