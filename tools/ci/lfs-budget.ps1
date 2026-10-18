<#
.SYNOPSIS
  LFS size gate (PLAN 1.2): fails when the LFS objects reachable from any ref exceed 6 GiB.

.DESCRIPTION
  Measures (1) every LFS object referenced anywhere in history (git lfs ls-files --all), which is
  what GitHub would store after a push of all refs, and (2) the local store .git\lfs\objects
  (shared by all worktrees). Gates on (1); reports (2) and suggests git lfs prune when the store
  holds much more than history needs. Also warns about large blobs committed without LFS.

.EXAMPLE
  tools\ci\lfs-budget.ps1
  tools\ci\lfs-budget.ps1 -MaxGiB 5
#>
[CmdletBinding()]
param(
    [double]$MaxGiB = 0,
    [int]$LargeBlobMiB = 5,
    [string]$Out = '',
    [string]$GateName = 'lfs.budget',
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

if ($MaxGiB -le 0) { $MaxGiB = $LFS_BUDGET_GIB }
if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
$gate = New-BrGate -Gate $GateName -BudgetSeconds 120 -Out $Out -NoMachine -FallbackIfFail 'git lfs prune after the tag; commit .blend and baked textures only at review points; ask the user about paid LFS (U22)'
$max = [int64]($MaxGiB * 1GB)
$gate.thresholds.reachableBytes = $max
$gate.thresholds.warnAtBytes = [int64]($max * 0.8)

try {
    if ($DryRun) {
        Write-BrLog "git -C $REPO lfs ls-files --all --json   (unique oids, sum of sizes)" 'cmd'
        Write-BrLog "sum of file sizes under $GIT_COMMON\lfs\objects" 'cmd'
        Write-BrLog "git -C $REPO ls-tree -r -l HEAD   (blobs over $LargeBlobMiB MiB that are not LFS pointers)" 'cmd'
        Set-BrGateStatus $gate 'dryrun' 'dry run'
    } else {
        $v = Invoke-BrNative -FilePath 'git' -ArgumentList @('-C', $REPO, 'lfs', 'version')
        if ($v.ExitCode -ne 0) { throw 'git-lfs is not available (git lfs version failed)' }
        $gate.metrics.gitLfs = "$($v.Output[0])".Trim()

        # (1) reachable from every ref, whole history
        $ls = Invoke-BrNative -FilePath 'git' -ArgumentList @('-C', $REPO, 'lfs', 'ls-files', '--all', '--json')
        if ($ls.ExitCode -ne 0) { throw ('git lfs ls-files --all --json failed: ' + (($ls.Output | Select-Object -First 3) -join ' ')) }
        $text = (@($ls.Output | Where-Object { $_ -notmatch '^(warning|error):' }) -join "`n")
        $byOid = @{}; $byDir = @{}
        if ($text.Trim()) {
            $j = ConvertFrom-BrJsonLarge $text
            $files = @()
            if ($j -and $j.ContainsKey('files') -and $j['files']) { $files = @($j['files']) }
            foreach ($f in $files) {
                $oid = "$($f['oid'])"; $size = [int64]$f['size']; $name = "$($f['name'])"
                if (-not $byOid.ContainsKey($oid)) {
                    $byOid[$oid] = [pscustomobject]@{ oid = $oid; size = $size; name = $name }
                    $top = ($name -split '/')[0]
                    if ($name -match '^(Game/Assets/[^/]+|CanonData/[^/]+|[^/]+)') { $top = $Matches[1] }
                    if (-not $byDir.ContainsKey($top)) { $byDir[$top] = [int64]0 }
                    $byDir[$top] += $size
                }
            }
        }
        $reach = [int64](($byOid.Values | Measure-Object -Property size -Sum).Sum)
        $gate.metrics.reachableObjects = $byOid.Count
        $gate.metrics.reachableBytes = $reach
        $gate.metrics.reachable = Format-BrBytes $reach
        $dirs = [ordered]@{}
        foreach ($k in @($byDir.Keys | Sort-Object { -$byDir[$_] })) { $dirs[$k] = $byDir[$k] }
        $gate.metrics.byFolderBytes = $dirs
        $gate.metrics.largest = @($byOid.Values | Sort-Object size -Descending | Select-Object -First 10 | ForEach-Object { [ordered]@{ name = $_.name; bytes = $_.size } })

        # (2) local store
        $store = Join-Path $GIT_COMMON 'lfs\objects'
        $local = [int64]0; $localCount = 0
        if (Test-Path -LiteralPath $store) {
            $m = Get-ChildItem -LiteralPath $store -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum
            $local = [int64]$m.Sum; $localCount = $m.Count
        }
        $gate.metrics.localStoreBytes = $local
        $gate.metrics.localStoreObjects = $localCount
        $gate.metrics.localStore = Format-BrBytes $local
        if ($local -gt ($reach * 1.5) -and $local -gt 512MB) { Add-BrWarning $gate ("the local LFS store (" + (Format-BrBytes $local) + ") is much larger than history needs: run git lfs prune after the next tag") }

        # (3) big blobs committed without LFS (they count against the normal repo size instead)
        $tree = Invoke-BrNative -FilePath 'git' -ArgumentList @('-C', $REPO, 'ls-tree', '-r', '-l', 'HEAD')
        $big = @()
        foreach ($line in $tree.Output) {
            if ("$line" -match '^\d+\s+blob\s+[0-9a-f]+\s+(\d+)\t(.+)$') {
                $sz = [int64]$Matches[1]
                if ($sz -ge ($LargeBlobMiB * 1MB)) { $big += [ordered]@{ name = $Matches[2]; bytes = $sz } }
            }
        }
        $gate.metrics.largeNonLfsBlobs = $big
        foreach ($b in $big) { Add-BrWarning $gate ("$($b.name) is " + (Format-BrBytes $b.bytes) + ' in plain git (not LFS); check .gitattributes') }

        $pct = 0
        if ($max -gt 0) { $pct = [Math]::Round(100.0 * $reach / $max, 1) }
        $gate.metrics.percentOfBudget = $pct
        if ($reach -gt $max) {
            Set-BrGateStatus $gate 'fail' ("LFS history holds " + (Format-BrBytes $reach) + " > budget $MaxGiB GiB")
        } else {
            if ($reach -gt $gate.thresholds.warnAtBytes) { Add-BrWarning $gate "LFS history is at $pct % of the $MaxGiB GiB budget" }
            Set-BrGateStatus $gate 'pass' ((Format-BrBytes $reach) + " in $($byOid.Count) LFS objects ($pct % of $MaxGiB GiB); local store " + (Format-BrBytes $local))
        }
        Write-BrJson (Join-Path $Out 'lfs-objects.json') @($byOid.Values | Sort-Object size -Descending)
        Add-BrEvidence $gate (Join-Path $Out 'lfs-objects.json')
    }
} catch {
    Set-BrGateStatus $gate 'error' ("lfs-budget.ps1: " + $_.Exception.Message)
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
