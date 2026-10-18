# T0.2 Environment check (PLAN 1.9, "First two weeks" T0.2).
# Records tool versions, the Unity player variations, the WSL2 distributions, the power source and the
# E-core map into artifacts\env.json. Exits 1 when a required tool is missing or too old, or when the
# Unity project creation log shows a licence error (agents never sign in: the user opens Unity Hub).
# Read-only: it changes no setting and installs nothing.
[CmdletBinding()]
param(
    [string]$Out = ''
)
. "$PSScriptRoot\env.ps1"
$ErrorActionPreference = 'Stop'
if (-not $Out) { $Out = Join-Path $ART 'env.json' }

$failures = New-Object System.Collections.Generic.List[string]
$warnings = New-Object System.Collections.Generic.List[string]
function Fail([string]$m) { $failures.Add($m); Write-Host "FAIL  $m" -ForegroundColor Red }
function Warn([string]$m) { $warnings.Add($m); Write-Host "WARN  $m" -ForegroundColor Yellow }
function Ok([string]$m)   { Write-Host "ok    $m" }

function Get-SemVer([string]$text) {
    $m = [regex]::Match($text, '(\d+)\.(\d+)(?:\.(\d+))?')
    if (-not $m.Success) { return $null }
    $patch = 0; if ($m.Groups[3].Success) { $patch = [int]$m.Groups[3].Value }
    return New-Object Version ([int]$m.Groups[1].Value), ([int]$m.Groups[2].Value), $patch
}

# --- Native helper: power status and the per-logical-processor efficiency class ---------------------
if (-not ('BrEnvProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class BrEnvProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS {
        public byte ACLineStatus; public byte BatteryFlag; public byte BatteryLifePercent; public byte SystemStatusFlag;
        public int BatteryLifeTime; public int BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);

    public static int[] Power() {
        SYSTEM_POWER_STATUS s;
        if (!GetSystemPowerStatus(out s)) return new int[] { -1, -1, -1 };
        return new int[] { s.ACLineStatus, s.BatteryFlag, s.BatteryLifePercent };
    }

    // Returns pairs {logicalIndex, efficiencyClass}. RelationProcessorCore = 0.
    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: Relationship(4) Size(4) then PROCESSOR_RELATIONSHIP:
    // Flags(1) EfficiencyClass(1) Reserved(20) GroupCount(2) GROUP_AFFINITY[GroupCount] {Mask(8) Group(2) Reserved(6)}.
    public static int[] CoreClasses() {
        uint len = 0;
        GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
        if (len == 0) return new int[0];
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        var list = new List<int>();
        try {
            if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return new int[0];
            long off = 0;
            while (off < len) {
                IntPtr rec = new IntPtr(buf.ToInt64() + off);
                int rel = Marshal.ReadInt32(rec, 0);
                int size = Marshal.ReadInt32(rec, 4);
                if (size <= 0) break;
                if (rel == 0) {
                    int eff = Marshal.ReadByte(rec, 9);
                    int groups = Marshal.ReadInt16(rec, 30);
                    for (int g = 0; g < groups; g++) {
                        int ga = 32 + g * 16;
                        ulong mask = (ulong)Marshal.ReadInt64(rec, ga);
                        int group = Marshal.ReadInt16(rec, ga + 8);
                        for (int bit = 0; bit < 64; bit++)
                            if ((mask & (1UL << bit)) != 0) { list.Add(group * 64 + bit); list.Add(eff); }
                    }
                }
                off += size;
            }
        } finally { Marshal.FreeHGlobal(buf); }
        return list.ToArray();
    }
}
'@
}

$env_ = [ordered]@{
    schema      = 1
    generatedAt = (Get-Date).ToString('o')
    repo        = $REPO
}

# --- Unity ------------------------------------------------------------------------------------------
$uInfo = [ordered]@{ path = $UNITY; expectedVersion = $UNITY_VERSION; exists = (Test-Path $UNITY) }
if ($uInfo.exists) {
    $uInfo.fileVersion = (Get-Item $UNITY).VersionInfo.ProductVersion
    $varDir = Join-Path $UNITY_DATA 'PlaybackEngines\windowsstandalonesupport\Variations'
    $uInfo.windowsStandaloneVariations = @()
    if (Test-Path $varDir) { $uInfo.windowsStandaloneVariations = @(Get-ChildItem $varDir -Directory | ForEach-Object Name) }
    $uInfo.il2cpp = [bool]($uInfo.windowsStandaloneVariations | Where-Object { $_ -match 'il2cpp' })
    $uInfo.mono = [bool]($uInfo.windowsStandaloneVariations | Where-Object { $_ -match 'mono' })
    $tpl = Join-Path $UNITY_DATA 'Resources\PackageManager\ProjectTemplates\com.unity.template.3d-cross-platform-17.0.14.tgz'
    $uInfo.urpTemplate = (Test-Path $tpl)
    if ($UNITY_VERSION -notmatch '^6000\.3\.') { Fail "Unity $UNITY_VERSION is not a 6000.3.x editor" }
    if (-not $uInfo.mono) { Fail 'Unity has no Windows Mono player variation (WindowsStandaloneSupport)' }
    else { Ok "Unity $UNITY_VERSION, variations: $($uInfo.windowsStandaloneVariations -join ', ')" }
} else { Fail "Unity editor not found at $UNITY" }
$env_.unity = $uInfo

# --- Blender 4.5 ------------------------------------------------------------------------------------
$bInfo = [ordered]@{ path = $BLENDER; exists = (Test-Path $BLENDER) }
if ($bInfo.exists) {
    $bv = (& $BLENDER --factory-startup --version 2>$null | Select-Object -First 1)
    $bInfo.version = "$bv".Trim()
    $v = Get-SemVer $bInfo.version
    if (-not $v -or $v.Major -ne 4 -or $v.Minor -ne 5) { Fail "Blender 4.5 expected, found '$($bInfo.version)'" }
    else { Ok $bInfo.version }
} else { Fail "Blender not found at $BLENDER" }
$env_.blender = $bInfo

# --- Node, CMake, git, git-lfs ----------------------------------------------------------------------
function Probe-Tool([string]$name, [string]$path, [string[]]$args_, [Version]$min) {
    $r = [ordered]@{ path = $path; exists = [bool]($path -and (Test-Path $path)) }
    if ($r.exists) {
        $r.version = ("$(& $path @args_ 2>$null | Select-Object -First 1)").Trim()
        $v = Get-SemVer $r.version
        if ($min -and (-not $v -or $v -lt $min)) { Fail "$name >= $min expected, found '$($r.version)'" }
        else { Ok "$name $($r.version)" }
    } else { Fail "$name not found" }
    return $r
}
$env_.node   = Probe-Tool 'Node' $NODE @('--version') ([Version]'20.0.0')
$env_.cmake  = Probe-Tool 'CMake' $CMAKE @('--version') ([Version]'3.21.0')
$gitPath = (Get-Command git -ErrorAction SilentlyContinue).Source
$env_.git    = Probe-Tool 'git' $gitPath @('--version') $null
$lfs = [ordered]@{ exists = $false }
if ($gitPath) {
    $lv = ("$(& $gitPath lfs version 2>$null)").Trim()
    if ($LASTEXITCODE -eq 0 -and $lv) { $lfs.exists = $true; $lfs.version = $lv; Ok $lv } else { Fail 'git-lfs not found' }
}
$env_.gitLfs = $lfs

# --- Visual Studio 2022 C++ -------------------------------------------------------------------------
$vs = [ordered]@{ vswhere = $VSWHERE; exists = (Test-Path $VSWHERE) }
if ($vs.exists) {
    $json = & $VSWHERE -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -version '[17.0,18.0)' -format json
    $inst = @($json | ConvertFrom-Json)
    if ($inst.Count -gt 0 -and $inst[0]) {
        $vs.installationPath = $inst[0].installationPath
        $vs.installationVersion = $inst[0].installationVersion
        $vs.displayName = $inst[0].displayName
        $vs.vcTools = $true
        Ok "$($vs.displayName) $($vs.installationVersion) with VC tools"
    } else { $vs.vcTools = $false; Fail 'Visual Studio 2022 with the C++ x64 tools (VC.Tools.x86.x64) not found' }
} else { Fail "vswhere not found at $VSWHERE" }
$env_.vs2022 = $vs

# --- Edge -------------------------------------------------------------------------------------------
$eInfo = [ordered]@{ path = $EDGE; exists = (Test-Path $EDGE) }
if ($eInfo.exists) { $eInfo.version = (Get-Item $EDGE).VersionInfo.ProductVersion; Ok "Edge $($eInfo.version)" }
else { Fail "Edge not found at $EDGE" }
$env_.edge = $eInfo

# --- WSL2 distributions (needed only from U4b; never fatal) -----------------------------------------
$wsl = [ordered]@{ distros = @() }
$wslExe = Join-Path $env:SystemRoot 'System32\wsl.exe'
if (Test-Path $wslExe) {
    $old = $env:WSL_UTF8; $env:WSL_UTF8 = '1'
    try {
        $lines = & $wslExe --list --verbose 2>$null
        foreach ($l in $lines | Select-Object -Skip 1) {
            $t = ("$l" -replace "`0", '').Trim()
            if (-not $t) { continue }
            $default = $t.StartsWith('*')
            $parts = ($t.TrimStart('*').Trim()) -split '\s+'
            if ($parts.Count -ge 3) {
                $wsl.distros += [ordered]@{ name = $parts[0]; state = $parts[1]; version = [int]$parts[2]; default = $default }
            }
        }
    } catch { Warn "wsl --list failed: $_" } finally { $env:WSL_UTF8 = $old }
    $wsl2 = @($wsl.distros | Where-Object { $_.version -eq 2 })
    if ($wsl2.Count) { Ok "WSL2: $(($wsl2 | ForEach-Object { $_.name }) -join ', ')" } else { Warn 'No WSL2 distribution (needed only for the Linux server, U4b)' }
} else { Warn 'wsl.exe not present' }
$env_.wsl = $wsl

# --- Power source and Windows power plan (read-only) ------------------------------------------------
$p = [BrEnvProbe]::Power()
$src = switch ($p[0]) { 1 { 'mains' } 0 { 'battery' } default { 'unknown' } }
$power = [ordered]@{ source = $src; batteryPercent = $(if ($p[2] -eq 255) { $null } else { $p[2] }); batteryFlag = $p[1] }
try {
    $scheme = (& powercfg.exe /getactivescheme 2>$null | Select-Object -First 1)
    $power.activeScheme = ("$scheme" -replace '^.*?:\s*', '').Trim()
} catch { }
if ($src -ne 'mains') { Warn "Power source is $($src): perf gates will be marked invalid until on mains" } else { Ok 'Power: mains' }
$env_.power = $power

# --- CPU and the E-core map -------------------------------------------------------------------------
$cpuInfo = Get-CimInstance Win32_Processor | Select-Object -First 1
$pairs = [BrEnvProbe]::CoreClasses()
$cores = @()
for ($i = 0; $i -lt $pairs.Count; $i += 2) { $cores += [ordered]@{ logical = $pairs[$i]; efficiencyClass = $pairs[$i + 1] } }
$classes = @($cores | ForEach-Object { $_.efficiencyClass } | Sort-Object -Unique)
$cpu = [ordered]@{
    name = "$($cpuInfo.Name)".Trim(); physicalCores = $cpuInfo.NumberOfCores; logicalProcessors = $cpuInfo.NumberOfLogicalProcessors
    hybrid = ($classes.Count -gt 1); efficiencyClasses = $classes; perLogical = $cores
}
if ($classes.Count -gt 1) {
    $minClass = ($classes | Measure-Object -Minimum).Minimum
    $cpu.eCores = @($cores | Where-Object { $_.efficiencyClass -eq $minClass } | ForEach-Object { $_.logical })
    $cpu.pCores = @($cores | Where-Object { $_.efficiencyClass -ne $minClass } | ForEach-Object { $_.logical })
} else { $cpu.eCores = @(); $cpu.pCores = @($cores | ForEach-Object { $_.logical }) }
$mask = [uint64]0; foreach ($e in ($cpu.eCores | Select-Object -First 6)) { if ($e -lt 64) { $mask = $mask -bor ([uint64]1 -shl $e) } }
$cpu.minSpecAffinityMask = ('0x{0:X}' -f $mask)
if ($cores.Count -eq 0) { Fail 'GetLogicalProcessorInformationEx returned no cores' }
elseif ($cpu.eCores.Count -lt 6) { Warn "Fewer than 6 E-cores ($($cpu.eCores.Count)); the min-spec proxy (PLAN 1.9) cannot use 6" }
else { Ok "CPU $($cpu.name): $($cpu.pCores.Count) P + $($cpu.eCores.Count) E logical processors; E = $($cpu.eCores -join ',')" }
$env_.cpu = $cpu
$env_.memoryGiB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)

# --- Unity licence (after T0.5: the project-creation log) -------------------------------------------
$lic = [ordered]@{ createLog = (Join-Path $ART 'create.log') }
$lic.createLogExists = Test-Path $lic.createLog
$lic.error = $false
if ($lic.createLogExists) {
    $pat = 'No valid Unity Editor licen[cs]e|licen[cs]e (is )?(not valid|invalid|expired|not activated)|Failed to (activate|validate|update) (the )?licen[cs]e|Licen[cs]e (activation|validation) (has )?failed|LICENSE SYSTEM \[\d+\] .*(error|fail)|No ULF licen[cs]e found|User is not logged in|Batchmode quit.*licen[cs]e|Aborting batchmode due to failure'
    $hits = @(Select-String -Path $lic.createLog -Pattern $pat -AllMatches | Select-Object -First 5)
    # "Access token is unavailable; failed to update" is logged on every batchmode start even when the
    # entitlement resolves (seen in the 6000.3.20f1 create.log), so it is recorded but not fatal.
    $soft = @(Select-String -Path $lic.createLog -Pattern 'Access token is unavailable' | Select-Object -First 1)
    $lic.accessTokenWarning = ($soft.Count -gt 0)
    $lic.entitlementResolved = [bool](Select-String -Path $lic.createLog -Pattern 'Successfully resolved entitlement' -Quiet)
    if ($hits.Count) {
        $lic.error = $true
        $lic.lines = @($hits | ForEach-Object { $_.Line.Trim() })
        Fail 'Unity licence error in artifacts\create.log: please open Unity Hub and sign in (agents never sign in)'
    } elseif ($soft.Count -and -not $lic.entitlementResolved) {
        $lic.error = $true
        $lic.lines = @($soft | ForEach-Object { $_.Line.Trim() })
        Fail 'Unity has no access token and no entitlement in artifacts\create.log: please open Unity Hub and sign in (agents never sign in)'
    } else { Ok 'create.log: no licence error' }
} else { $lic.note = 'create.log not present yet (written by T0.5)' }
$env_.unityLicence = $lic

$env_.failures = @($failures)
$env_.warnings = @($warnings)
$env_.status = $(if ($failures.Count) { 'fail' } else { 'pass' })

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$jsonText = ($env_ | ConvertTo-Json -Depth 8) -replace "`r`n", "`n"
[IO.File]::WriteAllText($Out, $jsonText + "`n", (New-Object Text.UTF8Encoding $false))
Write-Host "wrote $Out ($($env_.status))"
if ($lic.error) { Write-Host 'please open Unity Hub and sign in' -ForegroundColor Red }
if ($failures.Count) { exit 1 }
exit 0
