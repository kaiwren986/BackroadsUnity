<#
.SYNOPSIS
  Creates a lane worktree (PLAN 1.10): D:\Backroads-Unity-wt\<lane> on branch
  <milestone>/<lane>-<topic>, cut from main.

.DESCRIPTION
  git -C <main checkout> worktree add D:\Backroads-Unity-wt\<lane> -b m<n>/<lane>-<topic> main
  Then copies the native plugin (EngineSimPlugin.dll + version.json) from the main checkout when
  the new worktree has none (the DLL is committed only at milestone tags); if there is none to copy
  it runs the new worktree's build-native.ps1 (shared cache; -NoNativeBuild skips). It checks free disk space
  and the number of worktrees (about 10 GB of Library each, 6 worktrees budgeted), checks that no
  hashed file was checked out with CRLF, and prints the next steps and the time taken.
  Canon import and the first Library import are listed as next steps (they need Unity and the
  canon importer); run them yourself in the new worktree.

.EXAMPLE
  tools\ci\worktree-new.ps1 -Lane w -Topic terrain
  tools\ci\worktree-new.ps1 -Lane v -Topic esp-ring -Milestone M1
  tools\ci\worktree-new.ps1 -Lane r -Topic lanegraph -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Lane,
    [Parameter(Mandatory = $true)][string]$Topic,
    [string]$Milestone = '',
    [string]$Base = 'main',
    [string]$Root = '',
    [switch]$NoNativeBuild,
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

$sw = [Diagnostics.Stopwatch]::StartNew()
$Lane = $Lane.Trim().ToLowerInvariant()
$Topic = $Topic.Trim().ToLowerInvariant()
if ($Lane -notmatch '^[a-z][a-z0-9]{0,7}$') { throw "-Lane is a short lowercase id such as w, v, r, a, n, g, i or ib (got '$Lane')" }
if ($Topic -notmatch '^[a-z0-9][a-z0-9-]{0,40}$') { throw "-Topic is lowercase words joined by '-' (got '$Topic')" }
if (-not $Root) { $Root = $WORKTREES }
if ($Milestone) { $Milestone = Get-BrMilestone $Milestone } else {
    $Milestone = 'M0'
    $tags = Invoke-BrGit @('tag', '--list', 'm*') -Repo $MAIN_REPO -All
    $max = -1
    foreach ($t in @($tags)) { if ("$t".Trim() -match '^m(\d+)$') { $n = [int]$Matches[1]; if ($n -gt $max) { $max = $n } } }
    $Milestone = "M$($max + 1)"
}
$branch = $Milestone.ToLowerInvariant() + '/' + $Lane + '-' + $Topic
$path = Join-Path $Root $Lane

# OWNERS.json has a "" key, which ConvertFrom-Json in 5.1 rejects: read it as dictionaries.
$letter = $Lane.Substring(0, 1).ToUpperInvariant()
try {
    $owners = ConvertFrom-BrJsonLarge (Read-BrText (Join-Path $MAIN_REPO 'OWNERS.json'))
    if ($owners -and $owners.ContainsKey('lanes') -and -not $owners['lanes'].ContainsKey($letter)) {
        Write-BrLog "lane '$Lane' does not start with a lane letter from OWNERS.json ($(@($owners['lanes'].Keys) -join ', '))" 'warn'
    }
} catch { Write-BrLog "could not read OWNERS.json: $($_.Exception.Message)" 'warn' }

Write-BrLog "main checkout: $MAIN_REPO"
Write-BrLog "new worktree:  $path"
Write-BrLog "new branch:    $branch (from $Base)"

$problems = @()
if (Test-Path -LiteralPath $path) { $problems += "$path already exists (remove it with: git -C $MAIN_REPO worktree remove $path)" }
$exists = Invoke-BrGit @('rev-parse', '--verify', '--quiet', "refs/heads/$branch") -Repo $MAIN_REPO
if ($exists) { $problems += "branch $branch already exists" }
$baseSha = Invoke-BrGit @('rev-parse', '--verify', '--quiet', $Base) -Repo $MAIN_REPO
if (-not $baseSha) { $problems += "base '$Base' does not exist" }

$wtList = @(Invoke-BrGit @('worktree', 'list', '--porcelain') -Repo $MAIN_REPO -All | Where-Object { "$_" -like 'worktree *' })
$drive = [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($Root))
$freeGB = $null
try { $freeGB = [Math]::Round((New-Object IO.DriveInfo($drive)).AvailableFreeSpace / 1GB, 1) } catch { }
if ($wtList.Count -ge 7) { Write-BrLog "$($wtList.Count - 1) lane worktrees exist already (budget: 6, PLAN 1.10); prune finished ones with git worktree remove" 'warn' }
if ($null -ne $freeGB -and $freeGB -lt 40) { Write-BrLog "only $freeGB GB free on $drive (a Unity Library takes about 10 GB)" 'warn' }

if ($problems.Count) { foreach ($p in $problems) { Write-BrLog $p 'error' }; exit 1 }

$cmd = @('-C', $MAIN_REPO, 'worktree', 'add', $path, '-b', $branch, $Base)
Write-BrLog ('git ' + (ConvertTo-BrArgString $cmd)) 'cmd'
if ($DryRun) {
    Write-BrLog "then copy Game\Assets\Plugins\x86_64\EngineSimPlugin.dll (+ .version.json) from $MAIN_REPO if present and missing, else run build-native.ps1 in the new worktree" 'cmd'
    Write-BrLog 'dry run: nothing created'
    exit 0
}
if (-not (Test-Path -LiteralPath $Root)) { New-Item -ItemType Directory -Force $Root | Out-Null }
$r = Invoke-BrNative -FilePath 'git' -ArgumentList $cmd -Echo
if ($r.ExitCode -ne 0) { Write-BrLog "git worktree add failed (exit $($r.ExitCode))" 'error'; exit 1 }

# Native plugin: committed only at milestone tags, so copy the main checkout's build when missing.
$plugSrc = Join-Path $MAIN_REPO 'Game\Assets\Plugins\x86_64'
$plugDst = Join-Path $path 'Game\Assets\Plugins\x86_64'
$copied = @()
foreach ($f in @('EngineSimPlugin.dll', 'EngineSimPlugin.version.json', 'EngineSimPlugin.dll.meta', 'EngineSimPlugin.version.json.meta')) {
    $s = Join-Path $plugSrc $f; $d = Join-Path $plugDst $f
    if ((Test-Path -LiteralPath $s) -and -not (Test-Path -LiteralPath $d)) {
        New-Item -ItemType Directory -Force $plugDst | Out-Null
        Copy-Item -LiteralPath $s -Destination $d
        $copied += $f
    }
}
if ($copied.Count) { Write-BrLog ("copied the native plugin from the main checkout: " + ($copied -join ', ')) 'ok' }
$nativeOk = Test-Path -LiteralPath (Join-Path $plugDst 'EngineSimPlugin.dll')
$bn = Join-Path $path 'tools\ci\build-native.ps1'
if (-not $nativeOk -and -not $NoNativeBuild -and (Test-Path -LiteralPath $bn)) {
    # Unity deletes the plugin's committed .meta files if it opens Game\ without the DLL, so build
    # (or restore from the shared cache, seconds) before anyone opens the new worktree.
    Write-BrLog 'no plugin DLL yet: running the new worktree''s tools\ci\build-native.ps1 (cache hit: seconds; cold: minutes)'
    $code = Invoke-BrChildScript -Script $bn -LogFile (Join-Path $path 'artifacts\worktree-new-build-native.log')
    $nativeOk = ($code -eq 0) -and (Test-Path -LiteralPath (Join-Path $plugDst 'EngineSimPlugin.dll'))
    if (-not $nativeOk) { Write-BrLog "build-native.ps1 failed (exit $code): run it in the new worktree before opening Unity" 'error' }
}
if (-not $nativeOk) { Write-BrLog 'no EngineSimPlugin.dll in the new worktree: run tools\ci\build-native.ps1 there BEFORE opening Unity (it deletes orphan .meta files)' 'warn' }

# Hashed trees must never be checked out with CRLF (PLAN 1.2, Repo.NoCrInHashedFiles).
$eol = Invoke-BrGit @('ls-files', '--eol', '--', 'CanonData', 'native/EngineSimPlugin/vendor', 'native/EngineSimPlugin/generated', 'Game/Assets/StreamingAssets', 'tools/oracle/traces') -Repo $path -All
$crlf = @($eol | Where-Object { "$_" -match '\sw/crlf\s' })
if ($crlf.Count) { Write-BrLog "$($crlf.Count) hashed files were checked out with CRLF; check .gitattributes. First: $($crlf[0])" 'error' }

$elapsed = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
Write-BrLog "worktree ready in $elapsed s" 'ok'
Write-Host ''
Write-Host 'Next steps:'
Write-Host "  1. cd $path"
Write-Host "  2. Work only here; commit on $branch. End each commit message with the Co-Authored-By line."
Write-Host '  3. First Library import (10-20 min, takes a Unity slot):  tools\ci\unity-tests.ps1 -Platform EditMode'
Write-Host '     Canon import once CanonData exists:                     tools\ci\canon-import.ps1'
Write-Host '     Native plugin (before Unity opens Game\, after native\ changes): tools\ci\build-native.ps1'
Write-Host ('  4. Unity Hub: Add > ' + (Join-Path $path 'Game') + ' (never open the same project twice).')
Write-Host '  5. Before asking the integrator to merge:                  tools\ci\gate.ps1 -Tier merge'
Write-Host '  6. End the card with a committed HANDOFF.md (done, next, resume command).'
exit 0
