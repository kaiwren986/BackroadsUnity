# Shared helpers for the tools\ci scripts (PLAN 1.9-1.11, card T0.6, lane I).
# Dot-source AFTER env.ps1:
#     . "$PSScriptRoot\env.ps1"
#     . "$PSScriptRoot\ci-lib.ps1"
# Rules for every file in tools\ci: Windows PowerShell 5.1 syntax (no ?? ?. ternary or && ||),
# ASCII only (5.1 reads BOM-less files as ANSI), files written as UTF-8 without BOM and LF.

$script:BR_EXIT = @{ pass = 0; fail = 1; error = 2; blocked = 3; invalid = 4; skipped = 0; dryrun = 0 }
$script:BR_HELD_LOCKS = New-Object System.Collections.ArrayList
$script:BR_ENTRY_SCRIPT = $MyInvocation.PSCommandPath   # the script that dot-sourced this file
$script:BR_PLAYER_MARKER = '-br-ci'   # every player the scripts start carries it (README: player CLI)
$script:BR_LICENCE_PATTERN = 'No valid Unity Editor licen[cs]e|licen[cs]e (is )?(not valid|invalid|expired|not activated)|Failed to (activate|validate|update) (the )?licen[cs]e|Licen[cs]e (activation|validation) (has )?failed|LICENSE SYSTEM \[\d+\] .*(error|fail)|No ULF licen[cs]e found|User is not logged in|Aborting batchmode due to failure'

# ------------------------------------------------------------------------------------------------
# Logging, small utilities
# ------------------------------------------------------------------------------------------------
function Write-BrLog {
    param([string]$Message, [string]$Level = 'info')
    $ts = (Get-Date).ToString('HH:mm:ss')
    switch ($Level) {
        'warn'  { $c = 'Yellow'; $t = 'WARN ' }
        'error' { $c = 'Red';    $t = 'FAIL ' }
        'ok'    { $c = 'Green';  $t = 'ok   ' }
        'cmd'   { $c = 'Cyan';   $t = '>    ' }
        default { $c = 'Gray';   $t = '     ' }
    }
    Write-Host "[$ts] $t$Message" -ForegroundColor $c
}

function Get-BrFirst {
    # First argument that is neither $null nor an empty string.
    foreach ($a in $args) { if ($null -ne $a -and "$a" -ne '') { return $a } }
    return $null
}

function Get-BrExitCode([string]$Status) {
    if ($script:BR_EXIT.ContainsKey($Status)) { return $script:BR_EXIT[$Status] }
    return 2
}

function ConvertTo-BrRelPath([string]$Path) {
    if (-not $Path) { return $null }
    $full = $Path
    try { $full = [IO.Path]::GetFullPath($Path) } catch { }
    $root = $REPO.TrimEnd('\') + '\'
    if ($full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { $full = $full.Substring($root.Length) }
    return ($full -replace '\\', '/')
}

function Get-BrMedian([double[]]$Values) {
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $s = @($Values | Sort-Object)
    $n = $s.Count
    if ($n % 2 -eq 1) { return $s[($n - 1) / 2] }
    return ($s[$n / 2 - 1] + $s[$n / 2]) / 2.0
}

function Format-BrBytes([double]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GiB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MiB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N1} KiB' -f ($Bytes / 1KB)) }
    return ('{0} B' -f $Bytes)
}

# ------------------------------------------------------------------------------------------------
# Files and JSON (UTF-8 without BOM, LF line endings)
# ------------------------------------------------------------------------------------------------
function Write-BrText([string]$Path, [string]$Text, [switch]$Crlf) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $t = $Text -replace "`r`n", "`n"
    if ($Crlf) { $t = $t -replace "`n", "`r`n" }
    [IO.File]::WriteAllText($Path, $t, (New-Object System.Text.UTF8Encoding($false)))
}

function Read-BrText([string]$Path) {
    $t = [IO.File]::ReadAllText($Path)
    if ($t.Length -gt 0 -and $t[0] -eq [char]0xFEFF) { $t = $t.Substring(1) }
    return $t
}

function Read-BrJson([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return $null }
    $t = Read-BrText $Path
    if ($t.Trim().Length -eq 0) { return $null }
    return ($t | ConvertFrom-Json)
}

function ConvertFrom-BrJsonLarge([string]$Text) {
    # ConvertFrom-Json in 5.1 has a size limit; this returns Dictionary/ArrayList trees instead.
    Add-Type -AssemblyName System.Web.Extensions
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = [int]::MaxValue
    $ser.RecursionLimit = 256
    return ,$ser.DeserializeObject($Text)
}

function Write-BrJson([string]$Path, $Object) {
    Write-BrText $Path ((ConvertTo-Json -InputObject $Object -Depth 12) + "`n")
}

function Get-BrLogTail([string]$Path, [int]$Lines = 1) {
    # Reads the end of a log that another process (Unity) still has open.
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    try {
        $fs = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite,Delete')
        try {
            $len = $fs.Length; $take = [Math]::Min($len, 16384)
            [void]$fs.Seek($len - $take, [IO.SeekOrigin]::Begin)
            $buf = New-Object byte[] $take
            [void]$fs.Read($buf, 0, $take)
        } finally { $fs.Dispose() }
        $text = [Text.Encoding]::UTF8.GetString($buf)
        $all = @($text -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
        if ($all.Count -le $Lines) { return $all }
        return $all[($all.Count - $Lines)..($all.Count - 1)]
    } catch { return @() }
}

# ------------------------------------------------------------------------------------------------
# Processes
# ------------------------------------------------------------------------------------------------
function ConvertTo-BrArgString([string[]]$ArgList) {
    # Windows command-line quoting (CommandLineToArgvW rules).
    $parts = New-Object System.Collections.ArrayList
    foreach ($a in $ArgList) {
        if ($null -eq $a) { continue }
        if ($a -eq '') { [void]$parts.Add('""'); continue }
        if ($a -notmatch '[\s"]') { [void]$parts.Add($a); continue }
        $s = [regex]::Replace($a, '(\\*)"', { param($m) ($m.Groups[1].Value * 2) + '\"' })
        $s = [regex]::Replace($s, '(\\+)$', { param($m) $m.Groups[1].Value * 2 })
        [void]$parts.Add('"' + $s + '"')
    }
    return ($parts -join ' ')
}

function Invoke-BrNative {
    # Runs a console tool, returns @{ ExitCode; Output[] }. Never throws on stderr output.
    param([string]$FilePath, [string[]]$ArgumentList, [switch]$Echo)
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = New-Object System.Collections.ArrayList
    try {
        $clean = @($ArgumentList | Where-Object { $null -ne $_ -and $_ -ne '' })
        & $FilePath @clean 2>&1 | ForEach-Object {
            $l = "$_"
            [void]$out.Add($l)
            if ($Echo) { Write-Host $l }
        }
        $code = $LASTEXITCODE
    } catch {
        [void]$out.Add($_.Exception.Message)
        $code = 9009
    } finally { $ErrorActionPreference = $old }
    return [pscustomobject]@{ ExitCode = $code; Output = @($out) }
}

function Invoke-BrGit {
    # git -C <repo> ...; returns trimmed stdout (first line when -First) or $null on failure.
    param([string[]]$GitArgs, [string]$Repo = $REPO, [switch]$All)
    $r = Invoke-BrNative -FilePath 'git' -ArgumentList (@('-C', $Repo) + $GitArgs)
    if ($r.ExitCode -ne 0) { return $null }
    if ($All) { return @($r.Output) }
    if ($r.Output.Count -eq 0) { return '' }
    return ("" + $r.Output[0]).Trim()
}

function Stop-BrProcessTree([int]$ProcessId) {
    $r = Invoke-BrNative -FilePath 'taskkill.exe' -ArgumentList @('/PID', "$ProcessId", '/T', '/F')
    return $r.ExitCode
}

function Get-BrChildProcessIds([int]$ParentId) {
    $all = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$ParentId" -ErrorAction SilentlyContinue)
    $ids = New-Object System.Collections.ArrayList
    foreach ($p in $all) {
        [void]$ids.Add([int]$p.ProcessId)
        foreach ($c in (Get-BrChildProcessIds ([int]$p.ProcessId))) { [void]$ids.Add($c) }
    }
    return @($ids)
}

function Invoke-BrProcess {
    # Starts Unity, a player or a server and waits for THAT process only (with a timeout), with an
    # optional CPU affinity and a tick callback (scriptblock receiving the Process and the elapsed
    # TimeSpan, every TickSec seconds). ShellExecute is used so the child inherits none of our
    # handles: the Unity Licensing client outlives the editor, and with inherited pipe handles it
    # would keep a caller that reads our output waiting (Start-Process -Wait hangs for that reason).
    # The Process object keeps its handle, so ExitCode stays readable after the exit.
    param(
        [string]$FilePath, [string[]]$ArgumentList, [int]$TimeoutSec = 0, [string]$WorkingDirectory = '',
        [UInt64]$AffinityMask = 0, [scriptblock]$OnTick = $null, [int]$TickSec = 5, [string]$WindowStyle = ''
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.Arguments = ConvertTo-BrArgString $ArgumentList
    $psi.UseShellExecute = $true
    if (-not $WindowStyle) { $WindowStyle = Get-BrFirst $env:BR_PLAYER_WINDOWSTYLE 'Normal' }   # the self-test minimises its stubs
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]$WindowStyle
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $p = [System.Diagnostics.Process]::Start($psi)
    $affinitySet = $null
    if ($AffinityMask -ne 0) {
        try { $p.ProcessorAffinity = [IntPtr][Int64]$AffinityMask; $affinitySet = ('0x{0:X}' -f $AffinityMask) }
        catch { Write-BrLog "could not set affinity: $($_.Exception.Message)" 'warn' }
    }
    $timedOut = $false
    while (-not $p.WaitForExit([Math]::Max(1, $TickSec) * 1000)) {
        if ($OnTick) { try { & $OnTick $p $sw.Elapsed } catch { Write-BrLog "tick: $($_.Exception.Message)" 'warn' } }
        if ($TimeoutSec -gt 0 -and $sw.Elapsed.TotalSeconds -ge $TimeoutSec) {
            $timedOut = $true
            Write-BrLog "timeout after $TimeoutSec s: stopping pid $($p.Id) and its children" 'error'
            [void](Stop-BrProcessTree $p.Id)
            [void]$p.WaitForExit(15000)
            break
        }
    }
    $p.WaitForExit()
    $code = -1
    if (-not $timedOut) { $code = $p.ExitCode }
    return [pscustomobject]@{
        ExitCode = $code; TimedOut = $timedOut; Seconds = [Math]::Round($sw.Elapsed.TotalSeconds, 1)
        ProcessId = $p.Id; Affinity = $affinitySet
    }
}

function Invoke-BrChildScript {
    # Runs another tools\ci script in its own powershell.exe (so its locks, exit code and
    # $ErrorActionPreference are isolated), echoing and logging its output. Returns the exit code.
    # Output goes to a file that is echoed while the child runs; we wait for the child process itself,
    # never for the end of a pipe (a lingering grandchild such as the Unity Licensing client must
    # not keep the caller waiting).
    param([string]$Script, [string[]]$Arguments = @(), [string]$LogFile = '')
    $clean = @($Arguments | Where-Object { $null -ne $_ -and $_ -ne '' })
    $argStr = ConvertTo-BrArgString (@('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $Script) + $clean)
    Write-BrLog ("powershell -File " + (ConvertTo-BrRelPath $Script) + ' ' + (ConvertTo-BrArgString $clean)) 'cmd'
    $tmp = $false
    if (-not $LogFile) { $LogFile = Join-Path $ART ('child-' + [guid]::NewGuid().ToString('N') + '.log'); $tmp = $true }
    $d = Split-Path -Parent $LogFile
    if ($d -and -not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Force $d | Out-Null }
    $errFile = "$LogFile.stderr"
    $p = Start-Process -FilePath $POWERSHELL -ArgumentList $argStr -RedirectStandardOutput $LogFile -RedirectStandardError $errFile -NoNewWindow -PassThru
    $null = $p.Handle   # keeps ExitCode readable after the exit
    $state = @{ pos = [int64]0 }
    while (-not $p.WaitForExit(500)) { Write-BrNewOutput $LogFile $state $false }
    [void]$p.WaitForExit(2000)
    Write-BrNewOutput $LogFile $state $true
    $code = $p.ExitCode
    # A grandchild that inherited the redirect handles (the Licensing client, say) may still hold
    # both files open: read them with shared access and leave them if they cannot be deleted.
    $e = Read-BrSharedText $errFile
    if ($e.Trim()) {
        foreach ($l in ($e -split "`r?`n")) { if ($l.Trim()) { Write-Host "    [stderr] $l" } }
        try { [IO.File]::AppendAllText($LogFile, "`n[stderr]`n" + $e) } catch { }
    }
    try { Remove-Item -LiteralPath $errFile -Force -ErrorAction Stop } catch { }
    if ($tmp) { try { Remove-Item -LiteralPath $LogFile -Force -ErrorAction Stop } catch { } }
    return $code
}

function Read-BrSharedText([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    try {
        $fs = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite,Delete')
        try { $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::Default); return $sr.ReadToEnd() } finally { $fs.Dispose() }
    } catch { return '' }
}

function Write-BrNewOutput([string]$Path, [hashtable]$State, [bool]$Final) {
    # Echoes the complete lines appended to $Path since the last call ($State.pos).
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try {
        $fs = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite,Delete')
        try {
            if ($fs.Length -le $State.pos) { return }
            [void]$fs.Seek($State.pos, [IO.SeekOrigin]::Begin)
            $n = [int]($fs.Length - $State.pos)
            $buf = New-Object byte[] $n
            $got = $fs.Read($buf, 0, $n)
        } finally { $fs.Dispose() }
        if ($got -le 0) { return }
        $end = $got
        if (-not $Final) { $end = [Array]::LastIndexOf($buf, [byte]10, $got - 1) + 1 }
        if ($end -le 0) { return }
        $text = [Text.Encoding]::Default.GetString($buf, 0, $end)
        foreach ($l in ($text -split "`r?`n")) { if ($l -ne '') { Write-Host "    $l" } }
        $State.pos += $end
    } catch { }
}

# ------------------------------------------------------------------------------------------------
# Repo, milestone, build and machine facts
# ------------------------------------------------------------------------------------------------
function Get-BrMilestone([string]$Explicit = '') {
    if ($Explicit) {
        $m = $Explicit.Trim().ToUpperInvariant()
        if ($m -notmatch '^M\d+$') { throw "Milestone must look like M0, M1, ... (got '$Explicit')" }
        return $m
    }
    $branch = Invoke-BrGit @('rev-parse', '--abbrev-ref', 'HEAD')
    if ($branch -match '^m(\d+)/') { return "M$($Matches[1])" }
    $tags = Invoke-BrGit @('tag', '--list', 'm*') -All
    $max = -1
    foreach ($t in @($tags)) { if ("$t".Trim() -match '^m(\d+)$') { $n = [int]$Matches[1]; if ($n -gt $max) { $max = $n } } }
    return "M$($max + 1)"
}

function Get-BrNativeInfo {
    $vf = Join-Path $PROJ 'Assets\Plugins\x86_64\EngineSimPlugin.version.json'
    $j = $null
    try { $j = Read-BrJson $vf } catch { }
    if (-not $j) { return [pscustomobject]@{ sourceHash = $null; abi = $null; file = $null } }
    return [pscustomobject]@{
        sourceHash = (Get-BrFirst $j.sourceHash $j.source_hash $j.sourceSha256 $j.hash)
        abi = (Get-BrFirst $j.abi $j.ABI $j.espVersion $j.esp_version)
        file = (ConvertTo-BrRelPath $vf)
    }
}

function Get-BrBuildInfo {
    $sha = Invoke-BrGit @('rev-parse', 'HEAD')
    $branch = Invoke-BrGit @('rev-parse', '--abbrev-ref', 'HEAD')
    $dirtyOut = Invoke-BrGit @('status', '--porcelain', '--untracked-files=no') -All
    $n = Get-BrNativeInfo
    return [ordered]@{
        git = $sha; branch = $branch; dirty = (@($dirtyOut | Where-Object { "$_".Trim() -ne '' }).Count -gt 0)
        unity = $UNITY_VERSION; native = $n.sourceHash; abi = $n.abi
    }
}

function Get-BrEnvJson {
    $cands = @()
    if ($env:BR_ENV_JSON) { $cands += $env:BR_ENV_JSON }
    $cands += (Join-Path $ART 'env.json')
    $cands += (Join-Path $LOCKS 'env.json')
    foreach ($c in $cands) {
        if (Test-Path -LiteralPath $c) {
            try { $j = Read-BrJson $c; if ($j) { $j | Add-Member -NotePropertyName '_path' -NotePropertyValue $c -Force; return $j } } catch { }
        }
    }
    return $null
}

function Get-BrPowerSource {
    $r = [ordered]@{ source = 'unknown'; batteryPercent = $null }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $s = [System.Windows.Forms.SystemInformation]::PowerStatus
        switch ("$($s.PowerLineStatus)") { 'Online' { $r.source = 'mains' } 'Offline' { $r.source = 'battery' } }
        $pct = [double]$s.BatteryLifePercent
        if ($pct -ge 0 -and $pct -le 1) { $r.batteryPercent = [int][Math]::Round($pct * 100) }
    } catch { }
    return $r
}

function Get-BrPowerMode {
    $names = @{
        '00000000-0000-0000-0000-000000000000' = 'Balanced'
        'ded574b5-45a0-4f42-8737-46345c09c238' = 'Best performance'
        '961cc777-2547-4f9d-8174-7d86181b8a7a' = 'Best power efficiency'
        '3af9b8d9-7c97-431d-ad78-34a8bfea439f' = 'Better battery'
    }
    $r = [ordered]@{ overlayAc = $null; overlayDc = $null; scheme = $null }
    try {
        $k = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes' -ErrorAction Stop
        foreach ($pair in @(@('overlayAc', 'ActiveOverlayAcPowerScheme'), @('overlayDc', 'ActiveOverlayDcPowerScheme'))) {
            $g = "$($k.($pair[1]))".Trim().ToLowerInvariant()
            if (-not $g) { $r[$pair[0]] = 'Balanced (no overlay set)' }
            elseif ($names.ContainsKey($g)) { $r[$pair[0]] = $names[$g] }
            else { $r[$pair[0]] = $g }
        }
    } catch { }
    $pc = Invoke-BrNative -FilePath 'powercfg.exe' -ArgumentList @('/getactivescheme')
    if ($pc.ExitCode -eq 0 -and $pc.Output.Count) { $r.scheme = ("$($pc.Output[0])" -replace '^.*?:\s*', '').Trim() }
    return $r
}

function Test-BrDiscreteGpuName([string]$Name) {
    return ($Name -match 'NVIDIA|GeForce|Quadro|RTX|GTX|Radeon RX|Radeon Pro|Arc\(TM\) [AB]\d|Arc [AB]\d')
}

function Get-BrGpuInfo {
    $list = New-Object System.Collections.ArrayList
    try {
        foreach ($v in @(Get-CimInstance Win32_VideoController -ErrorAction Stop)) {
            [void]$list.Add([ordered]@{
                name = "$($v.Name)".Trim(); discrete = (Test-BrDiscreteGpuName "$($v.Name)")
                drivesDisplay = ($v.CurrentHorizontalResolution -gt 0); driver = "$($v.DriverVersion)"
            })
        }
    } catch { }
    $disc = @($list | Where-Object { $_.discrete })
    $active = $null
    if ($disc.Count) { $active = $disc[0].name } elseif ($list.Count) { $active = $list[0].name }
    return [ordered]@{ active = $active; discretePresent = ($disc.Count -gt 0); controllers = @($list); note = 'active = discrete GPU if present; the player reports the GPU it really used in result.json' }
}

function Get-BrHeavyProcesses {
    # Unity editors, Blender, players and native builds that would disturb a perf gate.
    $mine = @($PID) + @(Get-BrChildProcessIds $PID)
    $names = @('Unity.exe', 'blender.exe', 'Backroads.exe', 'BackroadsServer.exe', 'MSBuild.exe', 'cl.exe', 'link.exe', 'ctest.exe', 'esp_cli.exe')
    $filter = ($names | ForEach-Object { "Name='$_'" }) -join ' OR '
    $res = New-Object System.Collections.ArrayList
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter $filter -ErrorAction Stop)) {
            if ($mine -contains [int]$p.ProcessId) { continue }
            $cl = "$($p.CommandLine)"
            $kind = 'other'
            if ($p.Name -eq 'Unity.exe') {
                if ($cl -match 'AssetImportWorker') { $kind = 'unity-import-worker' }
                elseif ($cl -match '-batchmode') { $kind = 'unity-batch' } else { $kind = 'unity-editor' }
            } elseif ($p.Name -like 'Backroads*') {
                if (Test-BrAgentPlayer "$($p.ExecutablePath)" $cl) { $kind = 'player-agent' } else { $kind = 'player-interactive' }
            }
            [void]$res.Add([ordered]@{ name = "$($p.Name)"; pid = [int]$p.ProcessId; kind = $kind })
        }
    } catch { }
    return @($res)
}

function Get-BrCoreInfo {
    $e = Get-BrEnvJson
    $r = [ordered]@{ logical = [Environment]::ProcessorCount; p = @(); e = @(); minSpecMask = $null; source = $null }
    if ($e -and $e.cpu) {
        $r.p = @($e.cpu.pCores); $r.e = @($e.cpu.eCores); $r.minSpecMask = $e.cpu.minSpecAffinityMask
        $r.source = ConvertTo-BrRelPath $e._path
    }
    return $r
}

function Get-BrMachineInfo {
    $cpu = $null; $os = $null; $load = $null
    try { $c = Get-CimInstance Win32_Processor -ErrorAction Stop | Select-Object -First 1; $cpu = "$($c.Name)".Trim(); $load = $c.LoadPercentage } catch { }
    try { $o = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop; $os = "$($o.Caption) $($o.BuildNumber)".Trim() } catch { }
    $power = Get-BrPowerSource
    $gpu = Get-BrGpuInfo
    $procs = Get-BrHeavyProcesses
    $m = [ordered]@{
        summary = "$cpu / $($gpu.active) / $os"
        power = $power.source; batteryPercent = $power.batteryPercent
        gpu = $gpu; powerMode = (Get-BrPowerMode); cores = (Get-BrCoreInfo)
        cpuLoadPercent = $load; otherProcs = @($procs)
    }
    return $m
}

# ------------------------------------------------------------------------------------------------
# Run folders and the gate JSON (PLAN 1.10)
# ------------------------------------------------------------------------------------------------
function New-BrRunDir([string]$Name) {
    $base = Join-Path $ART (Get-Date -Format 'yyyyMMdd-HHmm')
    $dir = Join-Path $base $Name
    $i = 2
    while (Test-Path -LiteralPath $dir) { $dir = Join-Path $base "$Name-$i"; $i++ }
    New-Item -ItemType Directory -Force $dir | Out-Null
    return $dir
}

function New-BrGate {
    param([string]$Gate, [bool]$Hard = $true, [bool]$Headline = $false, $BudgetSeconds = $null,
          [string]$Out = '', [string]$FallbackIfFail = $null, [switch]$NoMachine)
    $tier = $env:BR_GATE_TIER
    if (-not $tier) { $tier = 'adhoc' }
    $machine = $null
    if (-not $NoMachine) { $machine = Get-BrMachineInfo }
    $g = [ordered]@{
        schema = 1; gate = $Gate; tier = $tier; status = 'error'; hard = $Hard; headline = $Headline
        message = ''
        startedAt = (Get-Date).ToString('o'); finishedAt = $null
        build = (Get-BrBuildInfo)
        machine = $machine
        wallClock = [ordered]@{ seconds = $null; budget = $BudgetSeconds; pausedSeconds = 0; overBudget = $false }
        conditions = [ordered]@{}
        metrics = [ordered]@{}
        thresholds = [ordered]@{}
        evidence = (New-Object System.Collections.ArrayList)
        warnings = (New-Object System.Collections.ArrayList)
        fallbackIfFail = $FallbackIfFail
        exitCode = $null
        out = (ConvertTo-BrRelPath $Out)
        run = $env:BR_GATE_RUN
        script = (ConvertTo-BrRelPath $BR_ENTRY_SCRIPT)
    }
    $g['_sw'] = [Diagnostics.Stopwatch]::StartNew()
    $g['_out'] = $Out
    return $g
}

function Add-BrEvidence($Gate, [string]$Path) {
    if (-not $Path) { return }
    $rel = ConvertTo-BrRelPath $Path
    if (-not ($Gate.evidence -contains $rel)) { [void]$Gate.evidence.Add($rel) }
}

function Add-BrWarning($Gate, [string]$Text) {
    [void]$Gate.warnings.Add($Text)
    Write-BrLog $Text 'warn'
}

function Set-BrGateStatus($Gate, [string]$Status, [string]$Message = '') {
    $Gate.status = $Status
    if ($Message) { $Gate.message = $Message }
}

function Complete-BrGate($Gate, [switch]$DryRun) {
    # Writes artifacts\gates\<gate>.json (not for dry runs) and <out>\gate.json; returns the exit code.
    if ($Gate.Contains('_sw')) {
        $Gate.wallClock.seconds = [Math]::Round($Gate['_sw'].Elapsed.TotalSeconds, 1)
        $Gate.Remove('_sw')
    }
    $out = $Gate['_out']; $Gate.Remove('_out')
    if ($DryRun -and $Gate.status -eq 'pass') { $Gate.status = 'dryrun' }
    $Gate.finishedAt = (Get-Date).ToString('o')
    $active = 0
    if ($null -ne $Gate.wallClock.seconds) { $active = $Gate.wallClock.seconds - $Gate.wallClock.pausedSeconds }
    if ($Gate.wallClock.budget -and $active -gt $Gate.wallClock.budget) { $Gate.wallClock.overBudget = $true }
    $Gate.exitCode = Get-BrExitCode $Gate.status
    if ($out) { Write-BrJson (Join-Path $out 'gate.json') $Gate }
    if (-not $DryRun) { Write-BrJson (Join-Path (Join-Path $ART 'gates') ($Gate.gate + '.json')) $Gate }
    $lvl = 'ok'
    if ($Gate.status -eq 'fail' -or $Gate.status -eq 'error') { $lvl = 'error' }
    elseif ($Gate.status -ne 'pass' -and $Gate.status -ne 'dryrun' -and $Gate.status -ne 'skipped') { $lvl = 'warn' }
    $msg = "gate $($Gate.gate): $($Gate.status) in $($Gate.wallClock.seconds) s"
    if ($Gate.message) { $msg += " - $($Gate.message)" }
    Write-BrLog $msg $lvl
    return $Gate.exitCode
}

# ------------------------------------------------------------------------------------------------
# Locks (PLAN 1.9 / 1.10). machine.lock, .gpu.lock and the Unity slots are held through an open
# file handle, so a crashed holder releases them automatically. .user-playing is a plain file made
# by Play.bat.
# ------------------------------------------------------------------------------------------------
function Get-BrLockPath([string]$Name) {
    switch ($Name) {
        'machine' { return (Join-Path $LOCKS 'machine.lock') }
        'gpu'     { return (Join-Path $LOCKS '.gpu.lock') }
        'user'    { return (Join-Path $LOCKS '.user-playing') }
        default   { return (Join-Path (Join-Path $LOCKS '.unity-slots') "slot-$Name.lock") }
    }
}

function Lock-BrFile([string]$Path, [string]$Purpose = '') {
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $fs = $null
    try { $fs = New-Object IO.FileStream($Path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read) }
    catch { return $null }
    $info = "pid=$PID`nscript=$BR_ENTRY_SCRIPT`npurpose=$Purpose`nrepo=$REPO`nsince=$((Get-Date).ToString('o'))`n"
    $bytes = [Text.Encoding]::ASCII.GetBytes($info)
    $fs.SetLength(0); $fs.Write($bytes, 0, $bytes.Length); $fs.Flush()
    [void]$script:BR_HELD_LOCKS.Add($fs)
    return $fs
}

function Unlock-BrFile($Lock) {
    if (-not $Lock) { return }
    $p = $Lock.Name
    try { $Lock.Dispose() } catch { }
    try { [IO.File]::Delete($p) } catch { }
    [void]$script:BR_HELD_LOCKS.Remove($Lock)
}

function Unlock-BrAll { foreach ($l in @($script:BR_HELD_LOCKS)) { Unlock-BrFile $l } }

function Test-BrHoldsLock([string]$Path) {
    foreach ($l in $script:BR_HELD_LOCKS) { if ($l.Name -eq $Path) { return $true } }
    return $false
}

function Test-BrLockHeld([string]$Path) {
    # True when another process holds the lock. A leftover file without a holder (its holder crashed)
    # is free; it is deleted so that scripts which only test for the file (build-native.ps1,
    # project-unity.ps1) do not wait for a lock nobody holds.
    if (Test-BrHoldsLock $Path) { return $false }
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try { $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read); $fs.Dispose() }
    catch { return $true }
    try { [IO.File]::Delete($Path) } catch { }
    return $false
}

function Get-BrLockHolder([string]$Path) {
    try {
        $fs = New-Object IO.FileStream($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]'ReadWrite,Delete')
        try { $sr = New-Object IO.StreamReader($fs); $t = $sr.ReadToEnd() } finally { $fs.Dispose() }
        return (($t -split "`n" | Where-Object { $_ -match '^(pid|purpose|repo)=' }) -join ' ')
    } catch { return '?' }
}

function Get-BrLockStatus {
    $slots = @()
    for ($i = 1; $i -le $UNITY_SLOTS; $i++) {
        $p = Get-BrLockPath "$i"
        if (Test-BrLockHeld $p) { $slots += (Get-BrLockHolder $p) }
    }
    $u = Get-BrLockPath 'user'
    return [ordered]@{
        userPlaying = (Test-Path -LiteralPath $u)
        machine = $(if (Test-BrLockHeld (Get-BrLockPath 'machine')) { Get-BrLockHolder (Get-BrLockPath 'machine') } else { $null })
        gpu = $(if (Test-BrLockHeld (Get-BrLockPath 'gpu')) { Get-BrLockHolder (Get-BrLockPath 'gpu') } else { $null })
        unitySlotsUsed = $slots.Count; unitySlots = @($slots)
    }
}

function Enter-BrLockFile {
    param([string]$Path, [string]$Purpose, [int]$TimeoutSec = 3600, [int]$PollSec = 5, [switch]$RespectMachineLock)
    $sw = [Diagnostics.Stopwatch]::StartNew(); $said = $false
    $machine = Get-BrLockPath 'machine'
    while ($true) {
        $blocked = $false
        if ($RespectMachineLock -and (Test-BrLockHeld $machine)) { $blocked = $true; $why = "machine.lock (perf gate: $(Get-BrLockHolder $machine))" }
        if (-not $blocked) {
            $l = Lock-BrFile $Path $Purpose
            if ($l) { return $l }
            $why = "$(Split-Path -Leaf $Path) ($(Get-BrLockHolder $Path))"
        }
        if (-not $said) { Write-BrLog "waiting for $why" 'warn'; $said = $true }
        if ($sw.Elapsed.TotalSeconds -ge $TimeoutSec) { Write-BrLog "gave up waiting after $TimeoutSec s for $why" 'error'; return $null }
        Start-Sleep -Milliseconds (Get-BrPollMs $sw $TimeoutSec $PollSec)
    }
}

function Get-BrPollMs($Stopwatch, [int]$TimeoutSec, [int]$PollSec) {
    # Sleep one poll interval, but never past the timeout (so short timeouts are honoured).
    $left = ($TimeoutSec - $Stopwatch.Elapsed.TotalSeconds) * 1000
    return [int][Math]::Max(100, [Math]::Min($PollSec * 1000, $left))
}

function Enter-BrUnitySlot([string]$Purpose, [int]$TimeoutSec = 3600) {
    # One of the $UNITY_SLOTS slots; waits while a perf gate holds machine.lock.
    $sw = [Diagnostics.Stopwatch]::StartNew(); $said = $false
    $machine = Get-BrLockPath 'machine'
    while ($true) {
        if ((Test-BrHoldsLock $machine) -or -not (Test-BrLockHeld $machine)) {
            for ($i = 1; $i -le $UNITY_SLOTS; $i++) {
                $l = Lock-BrFile (Get-BrLockPath "$i") $Purpose
                if ($l) { Write-BrLog "took Unity slot $i of $UNITY_SLOTS"; return $l }
            }
            $why = "a free Unity slot (all $UNITY_SLOTS in use)"
        } else { $why = "machine.lock (perf gate: $(Get-BrLockHolder $machine))" }
        if (-not $said) { Write-BrLog "waiting for $why" 'warn'; $said = $true }
        if ($sw.Elapsed.TotalSeconds -ge $TimeoutSec) { Write-BrLog "gave up waiting after $TimeoutSec s for $why" 'error'; return $null }
        Start-Sleep -Milliseconds (Get-BrPollMs $sw $TimeoutSec 5)
    }
}

function Enter-BrGpuLock([string]$Purpose, [int]$TimeoutSec = 3600) {
    return (Enter-BrLockFile -Path (Get-BrLockPath 'gpu') -Purpose $Purpose -TimeoutSec $TimeoutSec -RespectMachineLock)
}

function Enter-BrMachineLock {
    # Perf gates: machine.lock, then every Unity slot and the GPU lock (so nothing else starts),
    # then waits until no other heavy process runs. Returns @{ Ok; Reason; Procs }.
    param([string]$Purpose, [int]$TimeoutSec = 3600, [int]$IdleTimeoutSec = 900)
    $m = Enter-BrLockFile -Path (Get-BrLockPath 'machine') -Purpose $Purpose -TimeoutSec $TimeoutSec
    if (-not $m) { return [pscustomobject]@{ Ok = $false; Reason = 'machine.lock not available'; Procs = @() } }
    for ($i = 1; $i -le $UNITY_SLOTS; $i++) {
        $s = Enter-BrLockFile -Path (Get-BrLockPath "$i") -Purpose "$Purpose (perf: all slots)" -TimeoutSec $TimeoutSec
        if (-not $s) { return [pscustomobject]@{ Ok = $false; Reason = "Unity slot $i still busy"; Procs = @() } }
    }
    $g = Enter-BrLockFile -Path (Get-BrLockPath 'gpu') -Purpose $Purpose -TimeoutSec $TimeoutSec
    if (-not $g) { return [pscustomobject]@{ Ok = $false; Reason = '.gpu.lock still busy'; Procs = @() } }
    $sw = [Diagnostics.Stopwatch]::StartNew(); $said = $false
    while ($true) {
        $procs = @(Get-BrHeavyProcesses)
        if ($procs.Count -eq 0) { return [pscustomobject]@{ Ok = $true; Reason = ''; Procs = @() } }
        if (-not $said) { Write-BrLog ("perf gate waiting for an idle machine: " + (($procs | ForEach-Object { "$($_.name)#$($_.pid)($($_.kind))" }) -join ', ')) 'warn'; $said = $true }
        if ($sw.Elapsed.TotalSeconds -ge $IdleTimeoutSec) {
            return [pscustomobject]@{ Ok = $false; Reason = 'machine not idle'; Procs = $procs }
        }
        Start-Sleep -Seconds 10
    }
}

function Test-BrAgentPlayer([string]$ExePath, [string]$CommandLine) {
    # A player the agents started: it carries -br-ci (every script adds it), or it runs from a lane
    # worktree (D:\Backroads-Unity-wt\...), where the user never plays. Anything else is the user.
    if ($CommandLine -match [regex]::Escape($BR_PLAYER_MARKER)) { return $true }
    if ($ExePath -and $WORKTREES) {
        $wt = $WORKTREES.TrimEnd('\') + '\'
        if ($ExePath.StartsWith($wt, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }
    return $false
}

function Get-BrInteractivePlayers {
    $res = @()
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='Backroads.exe'" -ErrorAction Stop)) {
            if (-not (Test-BrAgentPlayer "$($p.ExecutablePath)" "$($p.CommandLine)")) { $res += [int]$p.ProcessId }
        }
    } catch { }
    return $res
}

function Test-BrUserPlaying {
    # Playing = a Backroads.exe that no script started is running, or Play.bat's lock is fresh.
    # A lock older than 3 minutes with no game running is stale (Play.bat window closed) and removed.
    $lock = Get-BrLockPath 'user'
    $pids = @(Get-BrInteractivePlayers)
    if ($pids.Count -gt 0) { return [pscustomobject]@{ Playing = $true; Reason = "Backroads.exe running interactively (pid $($pids -join ','))" } }
    if (Test-Path -LiteralPath $lock) {
        $age = (Get-Date) - (Get-Item -LiteralPath $lock).LastWriteTime
        if ($age.TotalMinutes -lt 3) { return [pscustomobject]@{ Playing = $true; Reason = 'artifacts\.user-playing (game starting)' } }
        Write-BrLog ("stale artifacts\.user-playing (no game running, {0:N0} min old): removing it" -f $age.TotalMinutes) 'warn'
        try { Remove-Item -LiteralPath $lock -Force } catch { }
    }
    return [pscustomobject]@{ Playing = $false; Reason = '' }
}

function Assert-BrUserNotPlaying {
    # Start-of-script check. Without -Wait it refuses (returns $false); with -Wait it waits.
    param([string]$What, [switch]$Wait, [int]$MaxWaitMin = 480)
    $s = Test-BrUserPlaying
    $script:BR_PLAYING_REASON = $s.Reason
    if (-not $s.Playing) { return $true }
    if (-not $Wait) {
        Write-BrLog "not starting $What while the user is playing ($($s.Reason)). Run it later, or pass -WaitForUser to wait." 'warn'
        return $false
    }
    return (Wait-BrUserDone -What $What -MaxWaitMin $MaxWaitMin)
}

function Wait-BrUserDone {
    param([string]$What, [int]$MaxWaitMin = 480, $Gate = $null)
    $s = Test-BrUserPlaying
    if (-not $s.Playing) { return $true }
    Write-BrLog "pausing before $What : the user is playing ($($s.Reason))" 'warn'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($s.Playing) {
        if ($sw.Elapsed.TotalMinutes -ge $MaxWaitMin) { return $false }
        Start-Sleep -Seconds 30
        $s = Test-BrUserPlaying
    }
    if ($Gate) { $Gate.wallClock.pausedSeconds += [Math]::Round($sw.Elapsed.TotalSeconds, 1) }
    Write-BrLog ("resuming after {0:N0} s" -f $sw.Elapsed.TotalSeconds)
    return $true
}

# ------------------------------------------------------------------------------------------------
# Unity editor (batchmode)
# ------------------------------------------------------------------------------------------------
function Test-BrUnityReady([string]$ProjectPath = $PROJ) {
    # Returns $null when Unity can run on the project, else the reason.
    if (-not (Test-Path -LiteralPath $UNITY)) { return "Unity $UNITY_VERSION is not installed at $UNITY (env.ps1)" }
    $pv = Join-Path $ProjectPath 'ProjectSettings\ProjectVersion.txt'
    if (-not (Test-Path -LiteralPath $pv)) {
        return "no Unity project at $ProjectPath yet (ProjectSettings\ProjectVersion.txt missing; card T0.5 / lane I-a creates Game\)"
    }
    $line = Get-Content -LiteralPath $pv | Where-Object { $_ -match '^m_EditorVersion:' } | Select-Object -First 1
    if ($line -match '^m_EditorVersion:\s*(\S+)') {
        if ($Matches[1] -ne $UNITY_VERSION) { return "the project is on Unity $($Matches[1]) but env.ps1 says $UNITY_VERSION" }
    }
    $lf = Join-Path $ProjectPath 'Temp\UnityLockfile'
    if (Test-Path -LiteralPath $lf) {
        try { $fs = [IO.File]::Open($lf, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $fs.Dispose() }
        catch { return "the project $ProjectPath is open in another Unity editor (Temp\UnityLockfile is locked); close it or use your own worktree" }
    }
    return $null
}

function Find-BrCSharp([string]$Pattern, [string]$Root = (Join-Path $PROJ 'Assets')) {
    # Files under Game\Assets whose C# source matches a regex (used to check the C# side exists).
    if (-not (Test-Path -LiteralPath $Root)) { return @() }
    $hits = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $Root -Recurse -Filter '*.cs' -File -ErrorAction SilentlyContinue)) {
        if ($f.FullName -match '\\ThirdParty\\Synty\\') { continue }
        if (Select-String -LiteralPath $f.FullName -Pattern $Pattern -Quiet) { $hits += $f.FullName }
    }
    return $hits
}

function Get-BrUnityLogProblems([string]$Log) {
    $r = [ordered]@{ licence = @(); compile = @(); executeMethod = @(); buildFailed = $false; exitLine = $null }
    if (-not (Test-Path -LiteralPath $Log)) { return $r }
    $r.licence = @(Select-String -LiteralPath $Log -Pattern $BR_LICENCE_PATTERN | Select-Object -First 5 | ForEach-Object { $_.Line.Trim() })
    $r.compile = @(Select-String -LiteralPath $Log -Pattern 'error CS\d{4}' | ForEach-Object { $_.Line.Trim() } | Select-Object -Unique | Select-Object -First 20)
    $r.executeMethod = @(Select-String -LiteralPath $Log -Pattern 'executeMethod (class|method) .*(could not be found|not found)|Invalid method name|executeMethod method .* threw' | Select-Object -First 5 | ForEach-Object { $_.Line.Trim() })
    $r.buildFailed = [bool](Select-String -LiteralPath $Log -Pattern 'Build Finished, Result: Failure|Error building Player|BuildFailedException' -Quiet)
    $x = Select-String -LiteralPath $Log -Pattern 'Exiting batchmode|Test run completed\. Exiting with code' | Select-Object -Last 1
    if ($x) { $r.exitLine = $x.Line.Trim() }
    return $r
}

function Invoke-BrUnity {
    # Runs the editor in batchmode on one Unity slot. Returns the Invoke-BrProcess result, plus
    # Problems (licence / compile / executeMethod) read from the log.
    param([string[]]$UnityArgs, [string]$LogFile, [int]$TimeoutMin = 60, [switch]$DryRun, [string]$Purpose = 'unity')
    $all = @($UnityArgs) + @('-logFile', $LogFile)
    Write-BrLog ('"' + $UNITY + '" ' + (ConvertTo-BrArgString $all)) 'cmd'
    if ($DryRun) { return [pscustomobject]@{ ExitCode = 0; TimedOut = $false; Seconds = 0; DryRun = $true; Problems = $null; NoSlot = $false } }
    $slot = Enter-BrUnitySlot -Purpose $Purpose -TimeoutSec 3600
    if (-not $slot) { return [pscustomobject]@{ ExitCode = -1; TimedOut = $false; Seconds = 0; DryRun = $false; Problems = $null; NoSlot = $true } }
    $state = @{ next = 120 }
    $tick = {
        param($p, $el)
        if ($el.TotalSeconds -ge $state.next) {
            $state.next += 120
            $tail = @(Get-BrLogTail $LogFile 1)
            $last = ''
            if ($tail.Count) { $last = $tail[0]; if ($last.Length -gt 140) { $last = $last.Substring(0, 140) } }
            Write-BrLog ("Unity running {0:N0} s: {1}" -f $el.TotalSeconds, $last)
        }
    }
    try {
        $r = Invoke-BrProcess -FilePath $UNITY -ArgumentList $all -TimeoutSec ($TimeoutMin * 60) -OnTick $tick -TickSec 5
    } finally { Unlock-BrFile $slot }
    $r | Add-Member -NotePropertyName DryRun -NotePropertyValue $false
    $r | Add-Member -NotePropertyName NoSlot -NotePropertyValue $false
    $r | Add-Member -NotePropertyName Problems -NotePropertyValue (Get-BrUnityLogProblems $LogFile)
    return $r
}

function Get-BrUnityFailureReason($Result) {
    # Plain-words reason for a failed Unity run, or $null.
    if ($Result.NoSlot) { return 'no Unity slot became free within 60 min' }
    if ($Result.TimedOut) { return 'Unity timed out' }
    $p = $Result.Problems
    if ($p -and $p.licence.Count) { return 'Unity licence error: please open Unity Hub and sign in (agents never sign in). Log: ' + $p.licence[0] }
    if ($p -and $p.compile.Count) { return "scripts do not compile ($($p.compile.Count) errors), first: " + $p.compile[0] }
    if ($p -and $p.executeMethod.Count) { return 'executeMethod failed: ' + $p.executeMethod[0] }
    return $null
}

# ------------------------------------------------------------------------------------------------
# NUnit 3 results (Unity Test Framework -testResults)
# ------------------------------------------------------------------------------------------------
function Read-BrNUnitResults([string]$Path, [int]$MaxFailures = 50) {
    [xml]$x = Read-BrText $Path
    $run = $x.SelectSingleNode('/test-run')
    if (-not $run) { throw "$Path is not an NUnit 3 result file (no <test-run>)" }
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $dur = 0.0
    [void][double]::TryParse("$($run.GetAttribute('duration'))", [Globalization.NumberStyles]::Float, $inv, [ref]$dur)
    $r = [ordered]@{
        result = $run.GetAttribute('result')
        total = [int]$run.GetAttribute('total'); passed = [int]$run.GetAttribute('passed'); failed = [int]$run.GetAttribute('failed')
        inconclusive = [int]$run.GetAttribute('inconclusive'); skipped = [int]$run.GetAttribute('skipped')
        durationSeconds = [Math]::Round($dur, 2)
        byCategory = [ordered]@{}
        failedTests = (New-Object System.Collections.ArrayList)
    }
    foreach ($tc in @($x.SelectNodes('//test-case'))) {
        $cats = @()
        foreach ($n in @($tc.SelectNodes('ancestor-or-self::*/properties/property[@name="Category"]'))) { $cats += $n.GetAttribute('value') }
        $cats = @($cats | Select-Object -Unique)
        if ($cats.Count -eq 0) { $cats = @('Uncategorized') }
        $res = $tc.GetAttribute('result')
        foreach ($c in $cats) {
            if (-not $r.byCategory.Contains($c)) { $r.byCategory[$c] = [ordered]@{ total = 0; failed = 0 } }
            $r.byCategory[$c].total++
            if ($res -eq 'Failed') { $r.byCategory[$c].failed++ }
        }
        if ($res -eq 'Failed' -and $r.failedTests.Count -lt $MaxFailures) {
            $msg = ''
            $m = $tc.SelectSingleNode('failure/message')
            if ($m) { $msg = $m.InnerText.Trim(); if ($msg.Length -gt 400) { $msg = $msg.Substring(0, 400) + '...' } }
            [void]$r.failedTests.Add([ordered]@{ name = $tc.GetAttribute('fullname'); categories = $cats; message = $msg })
        }
    }
    return $r
}

# ------------------------------------------------------------------------------------------------
# Builds (builds\M<n>...). BuildCli writes artifacts\build-<profile>-<milestone>.json (exe, success)
# and <Product>_Data\StreamingAssets\build.json (profile, git, milestone) into every player.
# ------------------------------------------------------------------------------------------------
function Get-BrBuildOutDir([string]$BuildProfile, [string]$Milestone) {
    # The folder build-player.ps1 passes to BuildCli as -out. Client is the user's build in
    # builds\M<n>\ (Play.bat); Dev sits beside it in builds\M<n>-dev\ so both exist at once.
    switch ($BuildProfile) {
        'Dev'            { return (Join-Path $BUILDS "$Milestone-dev") }
        'Client'         { return (Join-Path $BUILDS $Milestone) }
        'ServerHeadless' { return (Join-Path $BUILDS "$Milestone-server") }
        'ServerLinux'    { return (Join-Path $BUILDS "$Milestone-linux-server") }
    }
    throw "unknown build profile '$BuildProfile'"
}

function Get-BrBuildCandidates([string]$BuildProfile, [string]$Milestone) {
    # Where a player of this profile may be: our -out folder first, then BuildCli's defaults.
    switch ($BuildProfile) {
        'Dev'            { $dirs = @("$Milestone-dev", $Milestone); $exe = 'Backroads.exe' }
        'Client'         { $dirs = @($Milestone, "$Milestone-client"); $exe = 'Backroads.exe' }
        'ServerHeadless' { $dirs = @("$Milestone-server"); $exe = 'BackroadsServer.exe' }
        'ServerLinux'    { $dirs = @("$Milestone-linux-server"); $exe = 'BackroadsServer.x86_64' }
        default { throw "unknown build profile '$BuildProfile'" }
    }
    return [pscustomobject]@{ Dirs = @($dirs | ForEach-Object { Join-Path $BUILDS $_ }); Exe = $exe }
}

function Get-BrBuildReport([string]$BuildProfile, [string]$Milestone) {
    try { return (Read-BrJson (Join-Path $ART "build-$BuildProfile-$Milestone.json")) } catch { return $null }
}

function Get-BrBuildStamp([string]$ExePath) {
    # BuildInfo written by BuildCli: { milestone, git, dirty, profile, unity, builtAtUtc, canon, esp }.
    $f = Join-Path (Get-BrPlayerDataDir $ExePath) 'StreamingAssets\build.json'
    try { return (Read-BrJson $f) } catch { return $null }
}

function Resolve-BrPlayerExe([string]$BuildProfile, [string]$Milestone) {
    # The candidates whose build.json names this profile, newest first; then BuildCli's last report.
    $c = Get-BrBuildCandidates $BuildProfile $Milestone
    $found = @()
    foreach ($d in $c.Dirs) {
        $p = Join-Path $d $c.Exe
        if (-not (Test-Path -LiteralPath $p)) { continue }
        $s = Get-BrBuildStamp $p
        if ($s -and $s.profile -and $s.profile -ne $BuildProfile) { continue }
        $found += [pscustomobject]@{ Exe = $p; At = "$(if ($s) { $s.builtAtUtc })" }
    }
    if ($found.Count) { return (@($found | Sort-Object At -Descending)[0]).Exe }
    $r = Get-BrBuildReport $BuildProfile $Milestone
    if ($r -and $r.success -and $r.exe -and (Test-Path -LiteralPath "$($r.exe)")) { return "$($r.exe)" }
    return $null
}

function Get-BrPlayerDataDir([string]$ExePath) {
    $d = Split-Path -Parent $ExePath
    return (Join-Path $d (([IO.Path]::GetFileNameWithoutExtension($ExePath)) + '_Data'))
}

function Test-BrNativePluginReady {
    # Unity deletes a .meta whose asset is missing, so the plugin DLL must exist before Unity opens
    # Game\ (lane I-a handoff, deviation 6). Returns $null when fine, else what is missing.
    $dir = Join-Path $PROJ 'Assets\Plugins\x86_64'
    $missing = @()
    foreach ($m in @(Get-ChildItem -LiteralPath $dir -Filter '*.meta' -File -ErrorAction SilentlyContinue)) {
        $asset = $m.FullName.Substring(0, $m.FullName.Length - 5)
        if (-not (Test-Path -LiteralPath $asset)) { $missing += (Split-Path -Leaf $asset) }
    }
    if ($missing.Count) { return ($missing -join ', ') }
    return $null
}

function Initialize-BrNativePlugin([switch]$DryRun, [string]$LogFile = '') {
    # Runs tools\ci\build-native.ps1 (cache hit: seconds) when the plugin is missing. Returns $null or an error.
    $miss = Test-BrNativePluginReady
    if (-not $miss) { return $null }
    $bn = Join-Path $PSScriptRoot 'build-native.ps1'
    if (-not (Test-Path -LiteralPath $bn)) { return "Game\Assets\Plugins\x86_64 lacks $miss and tools\ci\build-native.ps1 is not on this branch (lane V)" }
    if ($DryRun) { Write-BrLog "first: tools\ci\build-native.ps1 ($miss missing; Unity would delete the orphan .meta files)" 'cmd'; return $null }
    Write-BrLog "$miss missing: running tools\ci\build-native.ps1 before Unity opens the project"
    $code = Invoke-BrChildScript -Script $bn -LogFile $LogFile
    if ($code -ne 0) { return "tools\ci\build-native.ps1 failed (exit $code); Unity was not started, so the plugin .meta files are safe" }
    $miss = Test-BrNativePluginReady
    if ($miss) { return "build-native.ps1 passed but $miss is still missing" }
    return $null
}

function Get-BrPlayerAppInfo([string]$ExePath) {
    # <Product>_Data\app.info holds the company name and the product name (one per line).
    $f = Join-Path (Get-BrPlayerDataDir $ExePath) 'app.info'
    if (-not (Test-Path -LiteralPath $f)) { return $null }
    $l = @(Get-Content -LiteralPath $f)
    $company = ''; $product = ''
    if ($l.Count -ge 1) { $company = "$($l[0])".Trim() }
    if ($l.Count -ge 2) { $product = "$($l[1])".Trim() }
    return [pscustomobject]@{ Company = $company; Product = $product }
}

# ------------------------------------------------------------------------------------------------
# Network endpoints of a process (m0.net.egress, soak bind check)
# ------------------------------------------------------------------------------------------------
function Test-BrLoopbackAddress([string]$Address) {
    return ($Address -match '^127\.' -or $Address -eq '::1' -or $Address -match '^::ffff:127\.')
}

function Get-BrProcessEndpoints([int[]]$ProcessIds) {
    $res = New-Object System.Collections.ArrayList
    foreach ($id in $ProcessIds) {
        try {
            foreach ($c in @(Get-NetTCPConnection -OwningProcess $id -ErrorAction SilentlyContinue)) {
                [void]$res.Add([pscustomobject]@{ proto = 'tcp'; pid = $id; local = "$($c.LocalAddress)"; localPort = [int]$c.LocalPort; remote = "$($c.RemoteAddress)"; remotePort = [int]$c.RemotePort; state = "$($c.State)" })
            }
        } catch { }
        try {
            foreach ($u in @(Get-NetUDPEndpoint -OwningProcess $id -ErrorAction SilentlyContinue)) {
                [void]$res.Add([pscustomobject]@{ proto = 'udp'; pid = $id; local = "$($u.LocalAddress)"; localPort = [int]$u.LocalPort; remote = ''; remotePort = 0; state = 'Bound' })
            }
        } catch { }
    }
    return @($res)
}

function Test-BrEndpoint($Endpoint, [bool]$DevBuild = $false) {
    # Returns @{ Ok; Allowed; Reason }. Loopback is always fine. A Development player's
    # PlayerConnection (TCP listen 54997-55511, UDP discovery) is tolerated and reported.
    $e = $Endpoint
    if ($e.proto -eq 'tcp') {
        if ($e.state -eq 'Bound') {
            # bind() without listen() or connect(): no traffic and no firewall prompt (a client socket mid-connect).
            return [pscustomobject]@{ Ok = $true; Allowed = $false; Reason = '' }
        }
        if ($e.state -eq 'Listen') {
            if (Test-BrLoopbackAddress $e.local) { return [pscustomobject]@{ Ok = $true; Allowed = $false; Reason = '' } }
            if ($DevBuild -and $e.localPort -ge 54997 -and $e.localPort -le 55511) { return [pscustomobject]@{ Ok = $true; Allowed = $true; Reason = 'Development PlayerConnection listen port' } }
            return [pscustomobject]@{ Ok = $false; Allowed = $false; Reason = "listens on $($e.local):$($e.localPort) (not loopback; Windows Firewall would prompt)" }
        }
        if ((Test-BrLoopbackAddress $e.remote) -or $e.remote -eq '0.0.0.0' -or $e.remote -eq '::') { return [pscustomobject]@{ Ok = $true; Allowed = $false; Reason = '' } }
        return [pscustomobject]@{ Ok = $false; Allowed = $false; Reason = "outbound TCP to $($e.remote):$($e.remotePort) ($($e.state))" }
    }
    if (Test-BrLoopbackAddress $e.local) { return [pscustomobject]@{ Ok = $true; Allowed = $false; Reason = '' } }
    if ($DevBuild) { return [pscustomobject]@{ Ok = $true; Allowed = $true; Reason = 'UDP socket in a Development player (PlayerConnection discovery)' } }
    return [pscustomobject]@{ Ok = $false; Allowed = $false; Reason = "UDP socket bound to $($e.local):$($e.localPort) (not loopback)" }
}
