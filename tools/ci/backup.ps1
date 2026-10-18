<#
.SYNOPSIS
  Nightly backup until a remote exists (PLAN 1.1, S19): git bundle create --all plus a copy of
  .git\lfs\objects, keeping the last 7 snapshots.

.DESCRIPTION
  <Dest>\<yyyyMMdd-HHmmss>\backroads.bundle   every branch and tag of every worktree (git bundle --all)
  <Dest>\<yyyyMMdd-HHmmss>\lfs-oids.txt       the LFS objects that existed at that moment
  <Dest>\<yyyyMMdd-HHmmss>\backup.json        refs, sizes, hashes; RESTORE.txt says how to restore
  <Dest>\lfs-objects\aa\bb\<oid>              one shared copy of each LFS object (same layout as
                                              .git\lfs\objects; objects never change, so each is
                                              copied once and kept while a snapshot lists it)
  Only committed work is saved (uncommitted edits in worktrees are not). Pruning deletes only
  folders inside -Dest that this script made (name yyyyMMdd-HHmmss with a backup.json).
  -Dest defaults to D:\Backroads-Unity-backups until the user names a second drive (QUEUE U21).

.EXAMPLE
  tools\ci\backup.ps1
  tools\ci\backup.ps1 -Dest E:\BackroadsBackups -Verify
  tools\ci\backup.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [string]$Dest = '',
    [ValidateRange(1, 365)][int]$Keep = 7,
    [switch]$Verify,
    [string]$Out = '',
    [string]$GateName = 'backup',
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

if (-not $Dest) { $Dest = $BACKUP_DEST }
$Dest = [IO.Path]::GetFullPath($Dest).TrimEnd('\')
if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
$gate = New-BrGate -Gate $GateName -Hard $false -BudgetSeconds 900 -Out $Out -NoMachine -FallbackIfFail 'copy the repository folder by hand; create the private GitHub remote (U1)'
$gate.conditions = [ordered]@{ dest = $Dest; keep = $Keep; verify = [bool]$Verify }

function Get-LfsOids {
    $store = Join-Path $GIT_COMMON 'lfs\objects'
    if (-not (Test-Path -LiteralPath $store)) { return @() }
    return @(Get-ChildItem -LiteralPath $store -Recurse -File | Where-Object { $_.Name -match '^[0-9a-f]{64}$' })
}

try {
    # Never inside the repository or a lane worktree: worktrees are pruned at every milestone tag.
    foreach ($inside in @($MAIN_REPO, $REPO, $WORKTREES)) {
        if (-not $inside) { continue }
        $root = [IO.Path]::GetFullPath($inside).TrimEnd('\') + '\'
        if (($Dest + '\').StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "-Dest must be outside the repository and the worktrees ($inside)" }
    }
    if ($Dest.Length -le 3) { throw "-Dest must be a folder, not a drive root ($Dest)" }
    if ([IO.Path]::GetPathRoot($Dest) -eq [IO.Path]::GetPathRoot($MAIN_REPO)) {
        Add-BrWarning $gate "backups go to the same drive as the repository ($([IO.Path]::GetPathRoot($Dest))): this protects against mistakes, not against a disk failure (decision U21 in docs\approvals\QUEUE.md)"
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $snap = Join-Path $Dest $stamp
    $bundle = Join-Path $snap 'backroads.bundle'
    $lfsDest = Join-Path $Dest 'lfs-objects'

    if ($DryRun) {
        Write-BrLog "git -C $MAIN_REPO bundle create $bundle --all" 'cmd'
        Write-BrLog "git -C $MAIN_REPO bundle verify $bundle" 'cmd'
        Write-BrLog "copy new files from $GIT_COMMON\lfs\objects to $lfsDest; list them in $snap\lfs-oids.txt" 'cmd'
        $old = @()
        if (Test-Path -LiteralPath $Dest) { $old = @(Get-ChildItem -LiteralPath $Dest -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'backup.json')) } | Sort-Object Name -Descending) }
        $drop = @($old | Select-Object -Skip ([Math]::Max(0, $Keep - 1)))
        Write-BrLog ("keep the newest $Keep snapshots; would delete: " + $(if ($drop.Count) { ($drop | ForEach-Object { $_.Name }) -join ', ' } else { 'none' })) 'cmd'
        Set-BrGateStatus $gate 'dryrun' 'dry run'
    } else {
        New-Item -ItemType Directory -Force $snap | Out-Null
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $b = Invoke-BrNative -FilePath 'git' -ArgumentList @('-C', $MAIN_REPO, 'bundle', 'create', $bundle, '--all')
        if ($b.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $bundle)) { throw ('git bundle create failed: ' + (($b.Output | Select-Object -Last 3) -join ' ')) }
        $vb = Invoke-BrNative -FilePath 'git' -ArgumentList @('-C', $MAIN_REPO, 'bundle', 'verify', $bundle)
        if ($vb.ExitCode -ne 0) { throw ('git bundle verify failed: ' + (($vb.Output | Select-Object -Last 3) -join ' ')) }
        $refs = @(Invoke-BrGit @('for-each-ref', '--format=%(refname) %(objectname)') -Repo $MAIN_REPO -All)
        $gate.metrics.bundleBytes = (Get-Item -LiteralPath $bundle).Length
        $gate.metrics.refs = $refs.Count
        $gate.metrics.bundleSha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()

        # LFS objects: copy the ones the shared store lacks (or has with a different size).
        $objs = Get-LfsOids
        $copied = 0; $copiedBytes = [int64]0; $bad = @()
        foreach ($o in $objs) {
            $rel = $o.FullName.Substring((Join-Path $GIT_COMMON 'lfs\objects').Length).TrimStart('\')
            $target = Join-Path $lfsDest $rel
            $need = $true
            if (Test-Path -LiteralPath $target) { if ((Get-Item -LiteralPath $target).Length -eq $o.Length) { $need = $false } }
            if ($need) {
                $td = Split-Path -Parent $target
                if (-not (Test-Path -LiteralPath $td)) { New-Item -ItemType Directory -Force $td | Out-Null }
                Copy-Item -LiteralPath $o.FullName -Destination $target -Force
                $copied++; $copiedBytes += $o.Length
            }
            if ($Verify) {
                $h = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($h -ne $o.Name) { $bad += $o.Name }
            }
        }
        Write-BrText (Join-Path $snap 'lfs-oids.txt') ((@($objs | ForEach-Object { $_.Name }) -join "`n") + "`n")
        $gate.metrics.lfsObjects = $objs.Count
        $gate.metrics.lfsBytes = [int64](($objs | Measure-Object -Property Length -Sum).Sum)
        $gate.metrics.lfsCopiedObjects = $copied
        $gate.metrics.lfsCopiedBytes = $copiedBytes
        if ($Verify) { $gate.metrics.lfsVerifyFailures = $bad }

        $info = [ordered]@{
            createdAt = (Get-Date).ToString('o'); repo = $MAIN_REPO; head = (Invoke-BrGit @('rev-parse', 'main') -Repo $MAIN_REPO)
            bundle = 'backroads.bundle'; bundleBytes = $gate.metrics.bundleBytes; bundleSha256 = $gate.metrics.bundleSha256
            refs = $refs; lfsObjects = $objs.Count; lfsStore = '..\lfs-objects'
        }
        Write-BrJson (Join-Path $snap 'backup.json') $info
        $restore = @(
            'Restoring this Backroads backup (written by tools\ci\backup.ps1)',
            '',
            '1. git clone backroads.bundle D:\Backroads-Unity-restored',
            '   (or: git -C <existing clone> fetch <this folder>\backroads.bundle "refs/*:refs/*")',
            '2. Copy the LFS objects into the new clone:',
            '   robocopy ..\lfs-objects D:\Backroads-Unity-restored\.git\lfs\objects /E',
            '3. cd D:\Backroads-Unity-restored ; git lfs install --local ; git lfs checkout',
            '4. git branch -a lists every lane branch that existed at backup time.',
            '',
            'Only committed work is in the bundle. lfs-oids.txt lists the LFS objects of this snapshot.'
        )
        Write-BrText (Join-Path $snap 'RESTORE.txt') (($restore -join "`n") + "`n") -Crlf

        # Prune: keep the newest $Keep snapshots made by this script, then drop unreferenced LFS copies.
        $all = @(Get-ChildItem -LiteralPath $Dest -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'backup.json')) } | Sort-Object Name -Descending)
        $drop = @($all | Select-Object -Skip $Keep)
        foreach ($d in $drop) { Write-BrLog "pruning old snapshot $($d.Name)"; Remove-Item -LiteralPath $d.FullName -Recurse -Force }
        $kept = @($all | Select-Object -First $Keep)
        $want = @{}
        foreach ($k in $kept) {
            $lf = Join-Path $k.FullName 'lfs-oids.txt'
            if (Test-Path -LiteralPath $lf) { foreach ($l in (Get-Content -LiteralPath $lf)) { if ($l) { $want[$l.Trim()] = $true } } }
        }
        $gc = 0
        if (Test-Path -LiteralPath $lfsDest) {
            foreach ($f in @(Get-ChildItem -LiteralPath $lfsDest -Recurse -File)) {
                if ($f.Name -match '^[0-9a-f]{64}$' -and -not $want.ContainsKey($f.Name)) { Remove-Item -LiteralPath $f.FullName -Force; $gc++ }
            }
        }
        $gate.metrics.snapshotsKept = $kept.Count
        $gate.metrics.snapshotsPruned = @($drop | ForEach-Object { $_.Name })
        $gate.metrics.lfsObjectsPruned = $gc
        $gate.metrics.seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        Write-BrText (Join-Path $Dest 'README.txt') ("Backroads backups made by D:\Backroads-Unity\tools\ci\backup.ps1 (newest $Keep kept).`nEach dated folder has RESTORE.txt. lfs-objects\ is shared by all of them.`n") -Crlf
        Add-BrEvidence $gate (Join-Path $snap 'backup.json')
        if ($bad.Count) { Set-BrGateStatus $gate 'fail' "$($bad.Count) LFS copies do not match their SHA-256" }
        else { Set-BrGateStatus $gate 'pass' ("snapshot ${stamp}: bundle " + (Format-BrBytes $gate.metrics.bundleBytes) + ", $($refs.Count) refs, $($objs.Count) LFS objects ($copied new); $($kept.Count) kept") }
    }
} catch {
    Set-BrGateStatus $gate 'fail' ("backup.ps1: " + $_.Exception.Message)
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
