<#
.SYNOPSIS
  The site program's bake (lane S, card sites-S5; PLAN 1.9 bake entry points): bakes every town's site plans,
  surfaces and props twice in batchmode and checks them, or with -VerifyHash only checks that the committed bake is
  current.

.DESCRIPTION
  Unity.exe -batchmode -nographics -quit -projectPath Game
            -executeMethod Backroads.Sites.Editor.SitesBakeCli.Bake [-verifyHash] -logFile <log>
  SitesBakeCli.Bake bakes each town twice (parcels S1, plans S3, grading, meshes, plates and props S4), fails when the two
  bakes differ or the bake reports a problem, writes artifacts\sites\(town)\surfaces-report.txt and the renders, and
  saves Game\Assets\Backroads\Sites\Baked (chunks, props, manifest_(town).json), tools\world\sites\(town)\driveways.json
  and Game\Assets\StreamingAssets\Autotest\m3.shots.sites.json. With -VerifyHash it saves nothing and fails when the
  committed assets, driveway file or manifest differ from a fresh bake (exit 1). Takes a Unity slot like every Unity run.
  Writes a gate JSON (artifacts\gates\<GateName>.json) like tools\ci's gate scripts, so the integrator can add it as a
  step (suggested id "sites.bake", -VerifyHash, merge tier).

.EXAMPLE
  tools\sites\sites-bake.ps1
  tools\sites\sites-bake.ps1 -VerifyHash
  tools\sites\sites-bake.ps1 -VerifyHash -DryRun
#>
[CmdletBinding()]
param(
    [switch]$VerifyHash,
    [string]$Out = '',
    [string]$GateName = 'sites.bake',
    [int]$TimeoutMin = 30,
    [switch]$WaitForUser,
    [switch]$DryRun
)
$ci = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'tools\ci'
. (Join-Path $ci 'env.ps1')
. (Join-Path $ci 'ci-lib.ps1')

if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
$gate = New-BrGate -Gate $GateName -BudgetSeconds 600 -Out $Out -FallbackIfFail 'run tools\sites\sites-bake.ps1 (without -VerifyHash) on the lane S branch and commit the bake'
$gate.conditions = [ordered]@{ verifyHash = [bool]$VerifyHash }
$log = Join-Path $Out 'sites-bake.log'
$uargs = @('-batchmode', '-nographics', '-quit', '-projectPath', $PROJ, '-executeMethod', 'Backroads.Sites.Editor.SitesBakeCli.Bake')
if ($VerifyHash) { $uargs += '-verifyHash' }

try {
    $notReady = Test-BrUnityReady
    if (-not $notReady -and @(Find-BrCSharp 'class\s+SitesBakeCli\b').Count -eq 0) { $notReady = 'Backroads.Sites.Editor.SitesBakeCli is not in Game\Assets (lane S)' }
    if ($DryRun) {
        if ($notReady) { Add-BrWarning $gate "not runnable yet: $notReady" }
        [void](Invoke-BrUnity -UnityArgs $uargs -LogFile $log -DryRun)
        Set-BrGateStatus $gate 'dryrun' 'dry run: command printed, nothing started'
    } elseif ($notReady) {
        Set-BrGateStatus $gate 'error' $notReady
    } elseif (-not (Assert-BrUserNotPlaying -What 'the site bake' -Wait:$WaitForUser)) {
        Set-BrGateStatus $gate 'blocked' ("the user is playing: $BR_PLAYING_REASON")
    } else {
        $r = Invoke-BrUnity -UnityArgs $uargs -LogFile $log -TimeoutMin $TimeoutMin -Purpose ("sites bake" + $(if ($VerifyHash) { ' -verifyHash' } else { '' }))
        Add-BrEvidence $gate $log
        $gate.metrics.unityExitCode = $r.ExitCode
        $gate.metrics.unitySeconds = $r.Seconds
        $lines = @(Get-Content -LiteralPath $log -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\[BR\] sites: ' })
        $gate.metrics.summary = @($lines | Where-Object { $_ -match 'plans baked|current|stale|differ' } | ForEach-Object { $_.Substring(12) })
        foreach ($t in Get-ChildItem (Join-Path $REPO 'artifacts\sites') -Directory -ErrorAction SilentlyContinue) {
            $rep = Join-Path $t.FullName 'surfaces-report.txt'
            if (Test-Path -LiteralPath $rep) { Add-BrEvidence $gate $rep }
        }
        $why = Get-BrUnityFailureReason $r
        if ($r.ExitCode -ne 0 -or $why) {
            if (-not $why) { $why = "SitesBakeCli.Bake exited with $($r.ExitCode)" }
            $err = @($lines | Where-Object { $_ -match 'stale|differ|FAILED|problem' } | Select-Object -First 3)
            if ($err.Count) { $why += ': ' + ($err -join '; ') }
            Set-BrGateStatus $gate 'fail' $why
        } elseif ($VerifyHash) {
            Set-BrGateStatus $gate 'pass' 'the committed site bake, driveway file and manifest are current; two bakes agree'
        } else {
            Set-BrGateStatus $gate 'pass' 'baked twice, the same hash; saved (commit Sites\Baked, tools\world\sites, m3.shots.sites.json)'
        }
    }
} catch {
    Set-BrGateStatus $gate 'error' ("sites bake: " + $_.Exception.Message)
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
