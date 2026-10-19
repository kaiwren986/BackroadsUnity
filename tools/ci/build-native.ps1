# build-native.ps1: builds EngineSimPlugin.dll, runs ctest and installs the DLL into Game\Assets\Plugins\x86_64\
# (PLAN 1.6, 1.9).
#
#   tools\ci\build-native.ps1          preset vs2022-x64 (native\build): configure, build Release, quick ctest, install
#   tools\ci\build-native.ps1 -Heavy   the same, always built here (no cache shortcut), then, holding machine.lock, the
#                                      CPU-heavy reports (ctest label heavy: esp_cosim_matrix, esp_bench_*, esp_all_engines)
#   tools\ci\build-native.ps1 -Asan    preset vs2022-x64-asan (native\build-asan): configure, build, then, holding
#                                      machine.lock, every ctest but the heavy ones, incl. the 10-minute soak; installs nothing
#   -Force        rebuild even if the cache already holds this source hash
#   -Parallel N   build parallelism (default 16; at most 4 while artifacts\.user-playing exists)
#
# The quick ctest run (merge tier) skips the labels heavy and soak. Test reports (JSON, WAV) are copied from
# native\build*\reports\ into artifacts\<stamp>\native-reports\ and summarised in the gate JSON (metrics.reports).
#
# Cache: <main checkout>\artifacts\native-cache\<source hash>\, shared by every worktree. The source hash is SHA-256 over
# every tracked or untracked-but-not-ignored file under native\EngineSimPlugin (probes\ excluded) plus this script.
# Unchanged sources skip the build: the cached DLL and version file are installed and esp_smoke re-checks the copy.
# Installed: EngineSimPlugin.dll and EngineSimPlugin.version.json {abi, git, sourceHash, built, piranha}; the abi, git and
# sourceHash values are read back from the DLL's own esp_build_info(). Neither file is committed except at milestone tags.
#
# Exit: 0 green; 1 build or test failed; 3 a perf gate holds artifacts\machine.lock (or it stayed busy); 4 the installed
# DLL is locked.
# Writes: artifacts\<yyyyMMdd-HHmm>\build-native*.log, artifacts\gates\build-native[-asan|-heavy].json
param([switch]$Asan, [switch]$Heavy, [switch]$Force, [int]$Parallel = 16, [int]$LockTimeoutSec = 3600)

. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"
$ErrorActionPreference = 'Stop'
$env:MSBUILDDISABLENODEREUSE = '1'   # leave no idle MSBuild nodes behind (they count as heavy processes for perf gates)
$sw = [Diagnostics.Stopwatch]::StartNew()

$srcRel = 'native/EngineSimPlugin'
$src = Join-Path $REPO 'native\EngineSimPlugin'
$suffix = if ($Asan) { '-asan' } else { '' }
$gateName = if ($Asan) { 'build-native-asan' } elseif ($Heavy) { 'build-native-heavy' } else { 'build-native' }
$configurePreset = "vs2022-x64$suffix"
$preset = "release$suffix"                     # build and test preset
$buildDir = Join-Path $REPO "native\build$suffix"
$outDir = Join-Path $buildDir 'Release'
if (-not $CMAKE) { Write-Host 'build-native: cmake not found on PATH (see tools\ci\env-check.ps1)' -ForegroundColor Red; exit 1 }
$CTEST = Join-Path (Split-Path $CMAKE) 'ctest.exe'

# The main checkout holds what all worktrees share: the locks and the native cache.
$commonGit = (& git -C $REPO rev-parse --path-format=absolute --git-common-dir).Trim()
$commonRoot = Split-Path -Parent ($commonGit -replace '/', '\')
$machineLock = Get-BrLockPath 'machine'
if (-not ($Asan -or $Heavy) -and (Test-BrLockHeld $machineLock)) {
    Write-Host "build-native: a perf gate holds $machineLock; nothing else may run. Try again when it is gone." -ForegroundColor Yellow
    exit 3
}
if (Test-Path -LiteralPath (Get-BrLockPath 'user')) {
    $Parallel = [Math]::Min($Parallel, 4)
    Write-Host "build-native: the user is playing (artifacts\.user-playing); building with -Parallel $Parallel"
}

$runDir = Join-Path $ART (Get-Date -Format 'yyyyMMdd-HHmm')
New-Item -ItemType Directory -Force $runDir | Out-Null
$logPath = Join-Path $runDir "$gateName.log"
$log = New-Object IO.StreamWriter($logPath, $false, (New-Object Text.UTF8Encoding $false))
$script:lastOutput = @()

# Runs a native command; its output goes to the log (and to the console with -Echo). Throws on a non-zero exit.
function Invoke-Logged([string]$Step, [string]$Exe, [string[]]$ArgList, [switch]$Echo) {
    $line = "==> ${Step}: $Exe $($ArgList -join ' ')"
    Write-Host $line
    $log.WriteLine($line)
    $lines = New-Object System.Collections.Generic.List[string]
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'   # Windows PowerShell turns native stderr lines into error records
    try {
        & $Exe @ArgList 2>&1 | ForEach-Object {
            $l = if ($_ -is [Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ }
            $log.WriteLine($l)
            $lines.Add($l)
            if ($Echo) { Write-Host "    $l" }
        }
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $eap
        $log.Flush()
    }
    $script:lastOutput = $lines.ToArray()
    if ($code -ne 0) {
        if (-not $Echo) { $lines | Select-Object -Last 30 | ForEach-Object { Write-Host "    $_" } }
        throw "$Step failed (exit $code); full log: $logPath"
    }
}

function Get-Sha256Hex([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

# SHA-256 over "<sha256 of file>  <path>\n" for every build input, sorted by path (ordinal).
function Get-SourceHash {
    $list = & git -c core.quotepath=off -C $REPO ls-files -co --exclude-standard -- $srcRel 'tools/ci/build-native.ps1'
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed' }
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($f in $list) { if ($f -and -not $f.StartsWith("$srcRel/probes/")) { [void]$set.Add($f) } }
    $paths = New-Object string[] $set.Count
    $set.CopyTo($paths)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $manifest = New-Object Text.StringBuilder
    $count = 0
    foreach ($p in $paths) {
        $full = Join-Path $REPO $p
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }   # deleted in the working tree
        [void]$manifest.Append((Get-Sha256Hex ([IO.File]::ReadAllBytes($full)))).Append('  ').Append($p).Append("`n")
        $count++
    }
    $text = $manifest.ToString()
    return @{ Hash = (Get-Sha256Hex ([Text.Encoding]::UTF8.GetBytes($text))); Count = $count; Manifest = $text }
}

function Install-Plugin([string]$FromDir) {
    $plugins = Join-Path $PROJ 'Assets\Plugins\x86_64'
    New-Item -ItemType Directory -Force $plugins | Out-Null
    $dst = Join-Path $plugins 'EngineSimPlugin.dll'
    if (Test-Path $dst) {
        try { ([IO.File]::Open($dst, 'Open', 'ReadWrite', 'None')).Dispose() }
        catch {
            Write-Host "build-native: $dst is locked, most likely by an open Unity editor (it keeps native DLLs loaded)." -ForegroundColor Red
            Write-Host 'build-native: close the editor and run tools\ci\build-native.ps1 again.' -ForegroundColor Red
            $script:exitCode = 4
            throw "installed DLL is locked: $dst"
        }
    }
    Copy-Item (Join-Path $FromDir 'EngineSimPlugin.dll') $dst -Force
    Copy-Item (Join-Path $FromDir 'EngineSimPlugin.version.json') $plugins -Force
    Invoke-Logged 'check installed DLL' (Join-Path $FromDir 'esp_smoke.exe') @($dst) -Echo
    return $plugins
}

# Holds machine.lock (every Unity slot and the GPU lock too) and waits for an idle machine, like a perf gate.
function Enter-NativeHeavy([string]$What) {
    $r = Enter-BrMachineLock -Purpose "build-native $What" -TimeoutSec $LockTimeoutSec -IdleTimeoutSec 900
    if (-not $r.Ok) {
        $script:exitCode = 3
        throw "machine.lock for $What : $($r.Reason) $(($r.Procs | ForEach-Object { "$($_.name)#$($_.pid)" }) -join ', ')"
    }
    Write-Host "build-native: holding machine.lock for $What"
}

# Copies native\build*\reports\* to the run folder; returns { name: pass } from the JSON reports.
function Copy-Reports {
    $from = Join-Path $buildDir 'reports'
    $res = [ordered]@{}
    if (-not (Test-Path $from)) { return $res }
    $to = Join-Path $runDir 'native-reports'
    New-Item -ItemType Directory -Force $to | Out-Null
    foreach ($f in Get-ChildItem $from -File) {
        Copy-Item $f.FullName $to -Force
        if ($f.Extension -eq '.json') {
            try { $j = Get-Content -Raw $f.FullName | ConvertFrom-Json; $res[$f.BaseName] = [bool]$j.pass } catch { $res[$f.BaseName] = $null }
        }
    }
    $script:gate.evidence += @($to)
    return $res
}

$script:gate = [ordered]@{
    gate = $gateName; tier = $(if ($Asan -or $Heavy) { 'nightly' } else { 'merge' }); status = 'fail'; hard = $true
    headline = (-not ($Asan -or $Heavy)); build = [ordered]@{}; metrics = [ordered]@{}; evidence = @($logPath)
}
$gate = $script:gate
$exit = 1
$script:exitCode = 1
try {
    $source = Get-SourceHash
    $head = (& git -C $REPO rev-parse HEAD).Trim()
    $dirty = & git -C $REPO status --porcelain -- $srcRel ":!$srcRel/probes" 'tools/ci/build-native.ps1'
    $gitId = if ($dirty) { "$head-dirty" } else { $head }
    $gate.build = [ordered]@{ git = $gitId; native = $source.Hash; abi = 1; preset = $configurePreset }
    $gate.metrics['sourceFiles'] = $source.Count
    Write-Host "build-native: source hash $($source.Hash) ($($source.Count) files), git $gitId"

    $cacheRoot = Join-Path $commonRoot 'artifacts\native-cache'
    $cacheDir = Join-Path $cacheRoot $source.Hash
    $cacheHit = (-not $Asan) -and (-not $Heavy) -and (-not $Force) -and
        (Test-Path (Join-Path $cacheDir 'EngineSimPlugin.dll')) -and
        (Test-Path (Join-Path $cacheDir 'EngineSimPlugin.version.json')) -and
        (Test-Path (Join-Path $cacheDir 'esp_smoke.exe'))
    if ($cacheHit) {
        # A DLL built from uncommitted sources is reused only while they stay uncommitted; once they are committed the
        # rebuild (one file) stamps the commit. A clean entry keeps the first commit these sources were built at.
        $cachedGit = [string](Get-Content -Raw (Join-Path $cacheDir 'EngineSimPlugin.version.json') | ConvertFrom-Json).git
        if ($cachedGit.EndsWith('-dirty') -and $cachedGit -ne $gitId) {
            $cacheHit = $false
            Write-Host "build-native: cached DLL was built from uncommitted sources ($cachedGit); rebuilding"
        }
    }
    $gate.metrics['cacheHit'] = $cacheHit

    if ($cacheHit) {
        Write-Host "build-native: cache hit, $cacheDir (unchanged sources; no rebuild)"
    } else {
        Push-Location $src
        try {
            Invoke-Logged 'configure' $CMAKE @('--preset', $configurePreset, "-DESP_SOURCE_HASH=$($source.Hash)", "-DESP_GIT_COMMIT=$gitId")
            Invoke-Logged 'build' $CMAKE @('--build', '--preset', $preset, '--parallel', "$Parallel")
            $warnings = @($script:lastOutput | Where-Object { $_ -match ': warning [A-Z]+\d+' } | Sort-Object -Unique)
            $gate.metrics['warnings'] = $warnings.Count
            if ($Asan) {
                # Under ASan everything is slower and the soak must run in real time: hold the machine.
                Enter-NativeHeavy 'the ASan tests and soak'
                Invoke-Logged 'ctest' $CTEST @('--preset', $preset, '--output-on-failure', '-LE', 'heavy') -Echo
            } else {
                Invoke-Logged 'ctest' $CTEST @('--preset', $preset, '--output-on-failure', '-LE', 'heavy|soak') -Echo
            }
            $summary = $script:lastOutput | Where-Object { $_ -match 'tests passed' } | Select-Object -Last 1
            $gate.metrics['ctest'] = "$summary".Trim()
        } finally {
            Pop-Location
        }

        if (-not $Asan) {
            $dll = Join-Path $outDir 'EngineSimPlugin.dll'
            $smoke = Join-Path $outDir 'esp_smoke.exe'
            Invoke-Logged 'build info' $smoke @($dll, '--info')
            $infoJson = ($script:lastOutput -join "`n").Trim()
            $info = $infoJson | ConvertFrom-Json
            if ($info.abi -ne 1 -or $info.sourceHash -ne $source.Hash -or $info.git -ne $gitId) {
                throw "esp_build_info() does not match this build: $infoJson"
            }
            $version = [ordered]@{
                abi = $info.abi; git = $info.git; sourceHash = $info.sourceHash
                built = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'); piranha = $info.piranha
            }
            # Fill a temporary folder, then rename it, so a half-written cache entry is never used.
            $tmp = "$cacheDir.tmp$PID"
            New-Item -ItemType Directory -Force $tmp | Out-Null
            Copy-Item $dll, (Join-Path $outDir 'EngineSimPlugin.pdb'), $smoke $tmp
            $utf8 = New-Object Text.UTF8Encoding $false
            [IO.File]::WriteAllText((Join-Path $tmp 'EngineSimPlugin.version.json'), ($version | ConvertTo-Json) + "`n", $utf8)
            [IO.File]::WriteAllText((Join-Path $tmp 'source-manifest.txt'), $source.Manifest, $utf8)
            Copy-Item $logPath (Join-Path $tmp 'build-native.log')
            if (Test-Path $cacheDir) { Remove-Item -Recurse -Force $cacheDir }   # -Force, or an entry built from uncommitted sources
            Rename-Item $tmp (Split-Path -Leaf $cacheDir)
            Write-Host "build-native: cached in $cacheDir"
        }
    }

    if (-not $Asan) {
        $plugins = Install-Plugin $cacheDir
        $gate.evidence += @((Join-Path $plugins 'EngineSimPlugin.dll'), (Join-Path $plugins 'EngineSimPlugin.version.json'))
        Write-Host "build-native: installed EngineSimPlugin.dll + EngineSimPlugin.version.json in $plugins"
        Get-Content (Join-Path $plugins 'EngineSimPlugin.version.json') | ForEach-Object { Write-Host "    $_" }
    }
    if ($Heavy) {
        Enter-NativeHeavy 'the heavy native reports'
        Push-Location $src
        try {
            Invoke-Logged 'ctest heavy' $CTEST @('--preset', $preset, '--output-on-failure', '-L', 'heavy') -Echo
            $summary = $script:lastOutput | Where-Object { $_ -match 'tests passed' } | Select-Object -Last 1
            $gate.metrics['ctestHeavy'] = "$summary".Trim()
        } finally {
            Pop-Location
        }
    }
    $gate.status = 'pass'
    $exit = 0
} catch {
    Write-Host "build-native: FAILED: $_" -ForegroundColor Red
    $gate.metrics['error'] = "$_"
    $exit = $script:exitCode
} finally {
    try { $gate.metrics['reports'] = Copy-Reports } catch { }
    Unlock-BrAll
    $log.Dispose()
}

$gate.wallClock = [ordered]@{ seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1); budget = $(if ($Asan -or $Heavy) { 3600 } else { 900 }) }
$gates = Join-Path $ART 'gates'
New-Item -ItemType Directory -Force $gates | Out-Null
[IO.File]::WriteAllText((Join-Path $gates "$gateName.json"), ($gate | ConvertTo-Json -Depth 5) + "`n", (New-Object Text.UTF8Encoding $false))
$color = if ($exit -eq 0) { 'Green' } else { 'Red' }
Write-Host ("build-native: {0} in {1:N1} s (log {2})" -f $gate.status.ToUpper(), $sw.Elapsed.TotalSeconds, $logPath) -ForegroundColor $color
exit $exit
