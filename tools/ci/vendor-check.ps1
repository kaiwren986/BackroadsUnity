# vendor-check.ps1: proves native\EngineSimPlugin\vendor\ is a byte-identical copy of the upstream git blobs (PLAN 1.9, 3.1).
#
# Expected: blob ids from `git ls-tree -r` in D:\Backroads-ref\engine-sim @ 85f7c3b (src include scripting es
# assets/engines LICENSE) and in its submodules simple-2d-constraint-solver @ e009f4f and piranha @ 432f0b1.
# Actual: `git hash-object --no-filters` of every vendored file, so the result never depends on autocrlf [T5].
# vendor\LICENSES\ holds verified copies; piranha-LICENSE is the only file with no counterpart at the vendored
# commits [C9]; bison-NOTICE.txt is lines 1-31 of generated\piranha\parser.auto.cpp (U3c, M1).
# vendor\VENDOR.md and LICENSES\* must not contain CR bytes (Repo.NoCrInHashedFiles).
#
# Usage:  tools\ci\vendor-check.ps1            exit 0 = 0 differences, 1 = differences, 2 = cannot run
# Writes: artifacts\gates\vendor-check.json
param([int]$ShowMax = 50)

. "$PSScriptRoot\env.ps1"
$ErrorActionPreference = 'Stop'
$sw = [Diagnostics.Stopwatch]::StartNew()

# ---- what was vendored (change together with vendor\VENDOR.md) ----
$ES_COMMIT      = '85f7c3b959a908ed5232ede4f1a4ac7eafe6b630'
$ES_PATHS       = @('src', 'include', 'scripting', 'es', 'assets/engines', 'LICENSE')
$SUBMODULES     = @(
    @{ Name = 'simple-2d-constraint-solver'; Commit = 'e009f4ff1c9c4c5874e865e893cdb62e208fb2b3' },
    @{ Name = 'piranha';                     Commit = '432f0b122bb1663b686c553c7e7269300afac3bc' }
)
$PIRANHA_LICENSE_COMMIT = 'ff2955daddb2a7e0bae93f8e444a9601bf5bf111'   # piranha origin/master in the local clone
$PIRANHA_LICENSE_BLOB   = '231f3f1baf112ac7a71efd68d7be22107c64abb0'
$FLEX_HEADER            = 'dependencies/submodules/piranha/dependencies/libraries/flex/include/FlexLexer.h'
$FLEX_NOTICE_LINES      = @(5, 28)                                       # 1-based, inclusive
$BISON_SOURCE           = 'native/EngineSimPlugin/generated/piranha/parser.auto.cpp'   # repo-relative (U3c, M1)
$BISON_NOTICE_LINES     = @(1, 31)                                       # copyright, GPL notice and the Bison exception

$vendorDir = Join-Path $REPO 'native\EngineSimPlugin\vendor'
$esDir     = Join-Path $vendorDir 'engine-sim'
$licDir    = Join-Path $vendorDir 'LICENSES'
$env:GIT_OPTIONAL_LOCKS = '0'   # never refresh the reference checkout's index

function Invoke-Git([string]$Dir, [string[]]$GitArgs) {
    $out = & git.exe -c core.autocrlf=false -c core.quotepath=off -C $Dir @GitArgs
    if ($LASTEXITCODE -ne 0) { throw "git -C $Dir $($GitArgs -join ' ') failed ($LASTEXITCODE)" }
    if ($null -eq $out) { return , @() }
    return , @($out)
}

function Read-Tree([string]$Dir, [string]$Commit, [string[]]$Paths, [string]$Prefix, [hashtable]$Into) {
    $lines = Invoke-Git $Dir (@('ls-tree', '-r', $Commit, '--') + $Paths)
    foreach ($l in $lines) {
        if (-not $l) { continue }
        $tab = $l.IndexOf("`t")
        $meta = $l.Substring(0, $tab).Split(' ')
        $path = $l.Substring($tab + 1)
        if ($path.StartsWith('"')) { throw "unusual path name needs -z parsing: $path" }
        if ($meta[1] -ne 'blob' -or ($meta[0] -ne '100644' -and $meta[0] -ne '100755')) {
            throw "unsupported tree entry (mode $($meta[0]) $($meta[1])): $path"
        }
        $Into[$Prefix + $path] = $meta[2]
    }
    return $lines.Count
}

$report = [ordered]@{
    gate = 'vendor-check'; tier = 'merge'; status = 'fail'; hard = $true; headline = $true
    metrics = [ordered]@{}; differences = @(); evidence = @('native/EngineSimPlugin/vendor/VENDOR.md')
}
$diffs = New-Object System.Collections.Generic.List[string]

try {
    if (-not (Test-Path (Join-Path $ENGINESIM_REF '.git'))) { throw "reference checkout not found: $ENGINESIM_REF" }
    if (-not (Test-Path $esDir)) { throw "vendored tree not found: $esDir" }

    # ---- expected blobs ----
    $expected = @{}
    $n = Read-Tree $ENGINESIM_REF $ES_COMMIT $ES_PATHS '' $expected
    $report.metrics['engine-sim'] = $n
    $gitlinks = @{}
    foreach ($l in (Invoke-Git $ENGINESIM_REF @('ls-tree', $ES_COMMIT, '--', 'dependencies/submodules/'))) {
        $tab = $l.IndexOf("`t"); $meta = $l.Substring(0, $tab).Split(' ')
        if ($meta[1] -eq 'commit') { $gitlinks[$l.Substring($tab + 1)] = $meta[2] }
    }
    foreach ($s in $SUBMODULES) {
        $rel = "dependencies/submodules/$($s.Name)"
        if ($gitlinks[$rel] -ne $s.Commit) { $diffs.Add("GITLINK  $rel is $($gitlinks[$rel]) in $ES_COMMIT, expected $($s.Commit)") }
        $n = Read-Tree (Join-Path $ENGINESIM_REF $rel) $s.Commit @() "$rel/" $expected
        $report.metrics[$s.Name] = $n
    }

    # ---- actual files ----
    $files = @(Get-ChildItem -LiteralPath $esDir -Recurse -File -Force | ForEach-Object {
        $_.FullName.Substring($esDir.Length + 1).Replace('\', '/')
    })
    $actual = @{}
    for ($i = 0; $i -lt $files.Count; $i += 100) {
        $chunk = $files[$i..([Math]::Min($i + 99, $files.Count - 1))]
        $ids = Invoke-Git $esDir (@('hash-object', '--no-filters', '--') + $chunk)
        if ($ids.Count -ne $chunk.Count) { throw "hash-object returned $($ids.Count) ids for $($chunk.Count) files" }
        for ($j = 0; $j -lt $chunk.Count; $j++) { $actual[$chunk[$j]] = $ids[$j] }
    }

    foreach ($p in ($expected.Keys | Sort-Object)) {
        if (-not $actual.ContainsKey($p)) { $diffs.Add("MISSING  engine-sim/$p") }
        elseif ($actual[$p] -ne $expected[$p]) { $diffs.Add("CHANGED  engine-sim/$p (blob $($expected[$p]), file $($actual[$p]))") }
    }
    foreach ($p in ($actual.Keys | Sort-Object)) { if (-not $expected.ContainsKey($p)) { $diffs.Add("EXTRA    engine-sim/$p") } }

    # ---- CR bytes: allowed only where the file equals its upstream blob ----
    $crFiles = @()
    foreach ($p in $files) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $esDir $p))
        if ([Array]::IndexOf($bytes, [byte]13) -ge 0) {
            $crFiles += $p
            if ($actual[$p] -ne $expected[$p]) { $diffs.Add("CR       engine-sim/$p contains CR bytes and differs from its upstream blob") }
        }
    }

    # ---- vendor\ top level: only engine-sim\, LICENSES\, VENDOR.md ----
    foreach ($item in Get-ChildItem -LiteralPath $vendorDir -Force) {
        if (@('engine-sim', 'LICENSES', 'VENDOR.md') -notcontains $item.Name) { $diffs.Add("EXTRA    $($item.Name)") }
    }
    $vendorMd = Join-Path $vendorDir 'VENDOR.md'
    if (-not (Test-Path $vendorMd)) { $diffs.Add('MISSING  VENDOR.md') }
    elseif ([Array]::IndexOf([IO.File]::ReadAllBytes($vendorMd), [byte]13) -ge 0) { $diffs.Add('CR       VENDOR.md contains CR bytes') }

    # ---- LICENSES\: each file verified against its source ----
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $flexLines = $latin1.GetString([IO.File]::ReadAllBytes((Join-Path $esDir $FLEX_HEADER))).Split("`n")
    $flexNotice = ($flexLines[($FLEX_NOTICE_LINES[0] - 1)..($FLEX_NOTICE_LINES[1] - 1)] -join "`n") + "`n"
    $bisonLines = $latin1.GetString([IO.File]::ReadAllBytes((Join-Path $REPO ($BISON_SOURCE -replace '/', '\')))).Split("`n")
    $bisonNotice = ($bisonLines[($BISON_NOTICE_LINES[0] - 1)..($BISON_NOTICE_LINES[1] - 1)] -join "`n") + "`n"
    $licExpected = [ordered]@{
        'engine-sim-LICENSE'                  = @{ Blob = $expected['LICENSE'] }
        'simple-2d-constraint-solver-LICENSE' = @{ Blob = $expected['dependencies/submodules/simple-2d-constraint-solver/LICENSE'] }
        'piranha-LICENSE'                     = @{ Blob = $PIRANHA_LICENSE_BLOB }
        'flex-NOTICE.txt'                     = @{ Text = $flexNotice; From = "lines $($FLEX_NOTICE_LINES -join '-') of $FLEX_HEADER" }
        'bison-NOTICE.txt'                    = @{ Text = $bisonNotice; From = "lines $($BISON_NOTICE_LINES -join '-') of $BISON_SOURCE" }
    }
    # piranha-LICENSE: when the source commit is still in the local clone, confirm it still has that blob.
    $pir = Join-Path $ENGINESIM_REF 'dependencies/submodules/piranha'
    $ErrorActionPreference = 'Continue'
    & git.exe -C $pir cat-file -e "$PIRANHA_LICENSE_COMMIT^{commit}" 2>&1 | Out-Null
    $havePiranhaLicenseCommit = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = 'Stop'
    if ($havePiranhaLicenseCommit) {
        $src = (Invoke-Git $pir @('rev-parse', "${PIRANHA_LICENSE_COMMIT}:LICENSE"))[0]
        if ($src -ne $PIRANHA_LICENSE_BLOB) { $diffs.Add("LICENSE  piranha ${PIRANHA_LICENSE_COMMIT}:LICENSE is $src, expected $PIRANHA_LICENSE_BLOB") }
    }
    $licFiles = if (Test-Path $licDir) { @(Get-ChildItem -LiteralPath $licDir -Force | ForEach-Object Name) } else { @() }
    foreach ($name in $licExpected.Keys) {
        $path = Join-Path $licDir $name
        if ($licFiles -notcontains $name) { $diffs.Add("MISSING  LICENSES/$name"); continue }
        $bytes = [IO.File]::ReadAllBytes($path)
        if ([Array]::IndexOf($bytes, [byte]13) -ge 0) { $diffs.Add("CR       LICENSES/$name contains CR bytes") }
        $e = $licExpected[$name]
        if ($e.Blob) {
            $id = (Invoke-Git $licDir @('hash-object', '--no-filters', '--', $name))[0]
            if ($id -ne $e.Blob) { $diffs.Add("CHANGED  LICENSES/$name (blob $($e.Blob), file $id)") }
        } elseif ($latin1.GetString($bytes) -cne $e.Text) {
            $diffs.Add("CHANGED  LICENSES/$name is not $($e.From)")
        }
    }
    foreach ($name in $licFiles) { if (-not $licExpected.Contains($name)) { $diffs.Add("EXTRA    LICENSES/$name") } }

    $report.metrics['expected'] = $expected.Count
    $report.metrics['vendored'] = $files.Count
    $report.metrics['licenses'] = $licFiles.Count
    $report.metrics['filesWithCr'] = $crFiles.Count
    $report.metrics['filesWithCrList'] = $crFiles
    $report.metrics['differences'] = $diffs.Count
    $report.differences = @($diffs)
    $report.status = if ($diffs.Count -eq 0) { 'pass' } else { 'fail' }
    $exit = if ($diffs.Count -eq 0) { 0 } else { 1 }
} catch {
    $report.status = 'error'
    $report.metrics['error'] = "$_"
    Write-Host "vendor-check: cannot run: $_" -ForegroundColor Red
    $exit = 2
}

$report.wallClock = [ordered]@{ seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2); budget = 60 }
$gates = Join-Path $ART 'gates'
New-Item -ItemType Directory -Force $gates | Out-Null
$json = $report | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $gates 'vendor-check.json'), $json + "`n", (New-Object Text.UTF8Encoding $false))

if ($exit -ne 2) {
    $diffs | Select-Object -First $ShowMax | ForEach-Object { Write-Host "  $_" }
    if ($diffs.Count -gt $ShowMax) { Write-Host "  ... and $($diffs.Count - $ShowMax) more (artifacts\gates\vendor-check.json)" }
    $m = $report.metrics
    Write-Host ("vendor-check: {0} vendored files vs {1} upstream blobs (engine-sim {2}, scs {3}, piranha {4}), {5} licence files; " -f `
        $m['vendored'], $m['expected'], $m['engine-sim'], $m['simple-2d-constraint-solver'], $m['piranha'], $m['licenses']) -NoNewline
    Write-Host ("{0} differences ({1}, {2:N1} s)" -f $diffs.Count, $report.status.ToUpper(), $sw.Elapsed.TotalSeconds) `
        -ForegroundColor $(if ($exit -eq 0) { 'Green' } else { 'Red' })
}
exit $exit
