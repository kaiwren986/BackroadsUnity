<#
.SYNOPSIS
  Builds THIRD_PARTY_NOTICES.txt and licenses\ from third-party.json (PLAN 1.9, C1, C8) and checks
  that every shipped entry has a licence and a source.

.DESCRIPTION
  Only entries with "shipped": true go into the notices. For each one the full licence text is
  included for MIT, BSD, Apache-2.0, OFL-1.1, ODbL-1.0 and BSL-1.0. The text comes from, in order:
    1. the entry's "licenceFile" (repo-relative path), if set;
    2. the upstream file vendored next to it: native\EngineSimPlugin\vendor\LICENSES\<name>-*, or
       LICENSE / LICENSE.txt / LICENSE.md / COPYING / OFL.txt at the root of the entry's "files";
    3. the standard text in licenses\<id>.txt (agent-written copies, marked; reported as unverified).
  ODbL entries also get the Section 4.3 produced-work notice; licenses\osm\ (if present) is copied.
  CC-BY entries of assets-provenance.json get an attribution line.
  -Check only validates (no files written). Errors fail the gate; warnings do not.

.EXAMPLE
  tools\ci\notices.ps1 -Check
  tools\ci\notices.ps1 -OutDir builds\M0
#>
[CmdletBinding()]
param(
    [string]$OutDir = '',
    [string]$Registry = '',
    [string]$Milestone = '',
    [switch]$Check,
    [string]$Out = '',
    [string]$GateName = 'licence.notices',
    [switch]$DryRun
)
. "$PSScriptRoot\env.ps1"
. "$PSScriptRoot\ci-lib.ps1"

if (-not $Registry) { $Registry = Join-Path $REPO 'third-party.json' }
if (-not $OutDir) { $OutDir = Join-Path $ART 'notices' }
if (-not [IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $REPO $OutDir }
if (-not $Out) { $Out = New-BrRunDir $GateName } else { New-Item -ItemType Directory -Force $Out | Out-Null }
$gate = New-BrGate -Gate $GateName -BudgetSeconds 60 -Out $Out -NoMachine -FallbackIfFail 'add the missing licence, source or text to third-party.json before the build ships'
$Milestone = Get-BrMilestone $Milestone

$LIC = [ordered]@{
    'MIT'          = @{ file = 'MIT.txt';          full = $true;  title = 'MIT License';                  url = 'https://opensource.org/license/mit';                     needsCopyright = $true }
    'BSD-2-Clause' = @{ file = 'BSD-2-Clause.txt'; full = $true;  title = 'BSD 2-Clause License';         url = 'https://opensource.org/license/bsd-2-clause';            needsCopyright = $true }
    'BSD-3-Clause' = @{ file = 'BSD-3-Clause.txt'; full = $true;  title = 'BSD 3-Clause License';         url = 'https://opensource.org/license/bsd-3-clause';            needsCopyright = $true }
    'BSD'          = @{ file = $null;              full = $true;  title = 'BSD-style licence';            url = $null;                                                    needsCopyright = $true }
    'Apache-2.0'   = @{ file = 'Apache-2.0.txt';   full = $true;  title = 'Apache License 2.0';           url = 'https://www.apache.org/licenses/LICENSE-2.0';            needsCopyright = $false }
    'OFL-1.1'      = @{ file = 'OFL-1.1.txt';      full = $true;  title = 'SIL Open Font License 1.1';    url = 'https://openfontlicense.org';                            needsCopyright = $false }
    'ODbL-1.0'     = @{ file = 'ODbL-1.0.txt';     full = $true;  title = 'Open Database License 1.0';    url = 'https://opendatacommons.org/licenses/odbl/1-0/';         needsCopyright = $false }
    'BSL-1.0'      = @{ file = 'BSL-1.0.txt';      full = $true;  title = 'Boost Software License 1.0';   url = 'https://www.boost.org/LICENSE_1_0.txt';                  needsCopyright = $false }
    'Unity-Companion' = @{ file = $null;           full = $false; title = 'Unity Companion License';      url = 'https://unity.com/legal/licenses/unity-companion-license'; needsCopyright = $false }
    'GPL'          = @{ file = $null;              full = $true;  title = 'GNU GPL (with exception)';     url = 'https://www.gnu.org/licenses/gpl-3.0.html';              needsCopyright = $true }
    'CC0-1.0'      = @{ file = $null;              full = $false; title = 'CC0 1.0 (public domain)';      url = 'https://creativecommons.org/publicdomain/zero/1.0/';    needsCopyright = $false }
    'CC-BY-4.0'    = @{ file = $null;              full = $false; title = 'Creative Commons BY 4.0';      url = 'https://creativecommons.org/licenses/by/4.0/';          needsCopyright = $false }
}

function Get-LicenceId($Entry) {
    if ($Entry.licenceId) { return "$($Entry.licenceId)" }
    $l = "$($Entry.licence)"
    if ($l -match '^\s*MIT\b') { return 'MIT' }
    if ($l -match 'Apache[- ]?(License )?2') { return 'Apache-2.0' }
    if ($l -match '\bOFL\b|Open Font License') { return 'OFL-1.1' }
    if ($l -match 'ODbL|Open Database License') { return 'ODbL-1.0' }
    if ($l -match 'BSL-1\.0|Boost Software') { return 'BSL-1.0' }
    if ($l -match 'BSD-3') { return 'BSD-3-Clause' }
    if ($l -match 'BSD-2') { return 'BSD-2-Clause' }
    if ($l -match '\bBSD') { return 'BSD' }
    if ($l -match 'Unity Companion') { return 'Unity-Companion' }
    if ($l -match '\bGPL') { return 'GPL' }
    if ($l -match '\bCC0') { return 'CC0-1.0' }
    if ($l -match 'CC[- ]BY') { return 'CC-BY-4.0' }
    return $null
}

function Get-Slug([string]$Name) {
    $s = ($Name -split '[\s(]')[0].ToLowerInvariant()
    return ($s -replace '[^a-z0-9._-]', '')
}

function Find-UpstreamLicence($Entry) {
    if ($Entry.licenceFile) {
        $p = Join-Path $REPO ("$($Entry.licenceFile)" -replace '/', '\')
        if (Test-Path -LiteralPath $p) { return $p }
        return $null
    }
    $slug = Get-Slug "$($Entry.name)"
    $lic = Join-Path $REPO 'native\EngineSimPlugin\vendor\LICENSES'
    if ($slug -and (Test-Path -LiteralPath $lic)) {
        $hit = @(Get-ChildItem -LiteralPath $lic -File | Where-Object { $_.Name -like "$slug-*" } | Select-Object -First 1)
        if ($hit.Count) { return $hit[0].FullName }
    }
    foreach ($pat in ("$($Entry.files)" -split ',')) {
        $root = ($pat.Trim() -replace '/\*\*$', '' -replace '/\*$', '' -replace '/', '\')
        if (-not $root -or $root -match '[*?]') { continue }
        $dir = Join-Path $REPO $root
        if (-not (Test-Path -LiteralPath $dir -PathType Container)) { continue }
        foreach ($n in @('LICENSE', 'LICENSE.txt', 'LICENSE.md', 'COPYING', 'OFL.txt')) {
            $p = Join-Path $dir $n
            if (Test-Path -LiteralPath $p) { return $p }
        }
    }
    return $null
}

function Get-TemplateBody([string]$Path) {
    # Strips the "# ..." header block of an agent-written copy (up to the "# ----" line).
    $lines = @((Read-BrText $Path) -split "`r?`n")
    $start = 0
    for ($i = 0; $i -lt [Math]::Min($lines.Count, 20); $i++) { if ($lines[$i] -match '^# -{10,}') { $start = $i + 1; break } }
    return (($lines[$start..($lines.Count - 1)]) -join "`n").Trim("`n")
}

$errors = New-Object System.Collections.ArrayList
$rule = ('-' * 100)
try {
    $reg = Read-BrJson $Registry
    if (-not $reg -or -not $reg.entries) { throw "$Registry has no entries" }
    $all = @($reg.entries)
    $shipped = @($all | Where-Object { $_.shipped -eq $true })
    $gate.metrics.entries = $all.Count
    $gate.metrics.shippedEntries = $shipped.Count

    $items = New-Object System.Collections.ArrayList
    $unverified = @{}
    foreach ($e in $shipped) {
        $n = "$($e.name)".Trim()
        if (-not $n) { [void]$errors.Add('a shipped entry has no name'); continue }
        if (-not "$($e.licence)".Trim()) { [void]$errors.Add("$n : no licence") }
        if (-not "$($e.source)".Trim()) { [void]$errors.Add("$n : no source URL") }
        if (-not "$($e.version)".Trim()) { Add-BrWarning $gate "$n : no version" }
        $id = Get-LicenceId $e
        if (-not $id) { [void]$errors.Add("$n : licence '$($e.licence)' is not one notices.ps1 knows (set licenceId)"); continue }
        $info = $LIC[$id]
        $up = Find-UpstreamLicence $e
        if ($e.licenceFile -and -not $up) { [void]$errors.Add("$n : licenceFile $($e.licenceFile) does not exist") }
        $tpl = $null
        if (-not $up -and $info.file) { $tpl = Join-Path $REPO ('licenses\' + $info.file) }
        if ($info.full -and -not $up -and -not ($tpl -and (Test-Path -LiteralPath $tpl))) {
            [void]$errors.Add("$n : $id needs its full licence text, but there is no upstream file and no licenses\$($info.file)")
        }
        if ($info.needsCopyright -and -not $up -and -not "$($e.copyright)".Trim()) { [void]$errors.Add("$n : $id requires the copyright notice, but the entry has no copyright") }
        if ($id -eq 'OFL-1.1' -and -not $up -and -not "$($e.copyright)".Trim()) { Add-BrWarning $gate "$n : no copyright line; set licenceFile to the font's own OFL.txt when the font is added" }
        if ($id -eq 'GPL' -and -not $up) { [void]$errors.Add("$n : GPL-licensed and shipped: needs its exact licence and exception text via licenceFile") }
        if ($tpl -and (Test-Path -LiteralPath $tpl) -and -not $up) {
            if (Select-String -LiteralPath $tpl -Pattern '^# AGENT-WRITTEN COPY' -Quiet) { $unverified[$info.file] = $true }
        }
        [void]$items.Add([pscustomobject]@{ Entry = $e; Name = $n; Id = $id; Info = $info; Upstream = $up; Template = $tpl })
    }
    $gate.metrics.licences = @($items | ForEach-Object { $_.Id } | Select-Object -Unique)
    $gate.metrics.upstreamTexts = @($items | Where-Object { $_.Upstream } | ForEach-Object { ConvertTo-BrRelPath $_.Upstream })
    $gate.metrics.unverifiedTexts = @($unverified.Keys | Sort-Object)
    if ($unverified.Count) { Add-BrWarning $gate ("agent-written licence texts in use (verify before any public release): " + (@($unverified.Keys | Sort-Object) -join ', ')) }

    # assets-provenance.json: CC-BY needs attribution, anything else must be CC0 (PLAN 1.2)
    $assets = @()
    $ap = Read-BrJson (Join-Path $REPO 'assets-provenance.json')
    if ($ap) { if ($ap.entries) { $assets = @($ap.entries) } else { $assets = @($ap) } }
    $attrib = @()
    foreach ($a in $assets) {
        $an = Get-BrFirst $a.name $a.title $a.file
        $al = "$(Get-BrFirst $a.licence $a.license)"
        $src = Get-BrFirst $a.url $a.source
        if (-not $src) { [void]$errors.Add("asset $an : no source URL") }
        if ($al -match 'CC[- ]?BY') { $attrib += "$an by $(Get-BrFirst $a.author 'unknown author'), $al, $src" }
        elseif ($al -notmatch 'CC0') { [void]$errors.Add("asset $an : licence '$al' is neither CC0 nor CC-BY") }
    }
    $gate.metrics.assetEntries = $assets.Count

    if ($errors.Count) {
        $gate.metrics.errors = @($errors)
        Set-BrGateStatus $gate 'fail' ("$($errors.Count) problems: " + (@($errors) -join '; '))
    } elseif ($Check -or $DryRun) {
        if ($DryRun) { Write-BrLog "would write $OutDir\THIRD_PARTY_NOTICES.txt and $OutDir\licenses\" 'cmd' }
        Set-BrGateStatus $gate 'pass' "$($shipped.Count) shipped entries: each has a licence, a source and its licence text"
    } else {
        $git = $gate.build.git
        $short = ''
        if ($git) { $short = $git.Substring(0, 7) }
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine('THIRD-PARTY NOTICES')
        [void]$sb.AppendLine('===================')
        [void]$sb.AppendLine("Backroads $Milestone (git $short). Generated $((Get-Date).ToString('yyyy-MM-dd')) by tools/ci/notices.ps1 from third-party.json.")
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('This program uses the Unity engine, (c) Unity Technologies, under the Unity Terms of Service.')
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('Backroads includes the third-party software, fonts and data listed below, each used under its')
        [void]$sb.AppendLine('licence. The licence texts follow the list and are also in the licenses folder next to this file.')
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine($rule); [void]$sb.AppendLine('COMPONENTS'); [void]$sb.AppendLine($rule)
        foreach ($it in $items) {
            $e = $it.Entry
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine($it.Name)
            if ($e.version) { [void]$sb.AppendLine("  Version:   $($e.version)") }
            [void]$sb.AppendLine("  Licence:   $($e.licence)")
            if ($e.copyright) { [void]$sb.AppendLine("  Copyright: $($e.copyright)") }
            [void]$sb.AppendLine("  Source:    $($e.source)")
            if ($e.note) { [void]$sb.AppendLine("  Note:      $($e.note)") }
            if ($it.Info.url -and -not $it.Info.full) { [void]$sb.AppendLine("  Licence text: $($it.Info.url)") }
        }
        $odbl = @($items | Where-Object { $_.Id -eq 'ODbL-1.0' })
        if ($odbl.Count) {
            [void]$sb.AppendLine(''); [void]$sb.AppendLine($rule); [void]$sb.AppendLine('DATA NOTICES (ODbL 1.0, Section 4.3)'); [void]$sb.AppendLine($rule)
            foreach ($it in $odbl) {
                $dbName = $it.Name
                if ($dbName -match 'OpenStreetMap') { $dbName = 'OpenStreetMap' }
                [void]$sb.AppendLine('')
                [void]$sb.AppendLine("Contains information from $dbName ($($it.Entry.source)), which is made available here")
                [void]$sb.AppendLine("under the Open Database License (ODbL) ($($LIC['ODbL-1.0'].url)).")
                if ($it.Entry.copyright) { [void]$sb.AppendLine("$($it.Entry.copyright).") }
            }
            if (Test-Path -LiteralPath (Join-Path $REPO 'licenses\osm')) { [void]$sb.AppendLine(''); [void]$sb.AppendLine('How the OpenStreetMap measurements were used is described in licenses/osm/.') }
        }
        if ($attrib.Count) {
            [void]$sb.AppendLine(''); [void]$sb.AppendLine($rule); [void]$sb.AppendLine('SOUNDS, TEXTURES AND OTHER ASSETS (Creative Commons BY)'); [void]$sb.AppendLine($rule)
            foreach ($l in $attrib) { [void]$sb.AppendLine("  $l") }
        }
        [void]$sb.AppendLine(''); [void]$sb.AppendLine($rule); [void]$sb.AppendLine('LICENCE TEXTS'); [void]$sb.AppendLine($rule)
        # Upstream files verbatim, one per component; templates once per licence with every holder's line.
        foreach ($it in @($items | Where-Object { $_.Upstream })) {
            [void]$sb.AppendLine(''); [void]$sb.AppendLine("=== $($it.Name): $($it.Info.title) (from $(Split-Path -Leaf $it.Upstream)) ===")
            [void]$sb.AppendLine(''); [void]$sb.AppendLine(((Read-BrText $it.Upstream) -replace "`r`n", "`n").Trim("`n"))
        }
        foreach ($id in $LIC.Keys) {
            $group = @($items | Where-Object { $_.Id -eq $id -and -not $_.Upstream -and $_.Template })
            if ($group.Count -eq 0) { continue }
            $holders = @($group | Where-Object { $_.Entry.copyright } | ForEach-Object { "$($_.Entry.copyright)" })
            $cr = ($holders -join "`n")
            if (-not $cr) { $cr = 'Copyright the respective authors (see the component list above)' }
            $body = (Get-TemplateBody $group[0].Template).Replace('{{COPYRIGHT}}', $cr)
            [void]$sb.AppendLine(''); [void]$sb.AppendLine("=== $($LIC[$id].title) (applies to: " + (($group | ForEach-Object { $_.Name }) -join ', ') + ') ===')
            [void]$sb.AppendLine(''); [void]$sb.AppendLine($body)
        }
        $notices = Join-Path $OutDir 'THIRD_PARTY_NOTICES.txt'
        Write-BrText $notices $sb.ToString()
        $ld = Join-Path $OutDir 'licenses'
        New-Item -ItemType Directory -Force $ld | Out-Null
        foreach ($it in $items) {
            if ($it.Upstream) {
                $leaf = Split-Path -Leaf $it.Upstream
                $slug = Get-Slug $it.Name
                if (-not $leaf.StartsWith("$slug-", [StringComparison]::OrdinalIgnoreCase)) { $leaf = "$slug-$leaf" }
                Copy-Item -LiteralPath $it.Upstream -Destination (Join-Path $ld $leaf) -Force
            }
            elseif ($it.Template -and (Test-Path -LiteralPath $it.Template)) { Copy-Item -LiteralPath $it.Template -Destination (Join-Path $ld (Split-Path -Leaf $it.Template)) -Force }
        }
        $osm = Join-Path $REPO 'licenses\osm'
        if (Test-Path -LiteralPath $osm) { Copy-Item -LiteralPath $osm -Destination $ld -Recurse -Force }
        Add-BrEvidence $gate $notices
        $gate.metrics.noticesBytes = (Get-Item -LiteralPath $notices).Length
        $gate.metrics.outDir = ConvertTo-BrRelPath $OutDir
        Set-BrGateStatus $gate 'pass' ("$($shipped.Count) shipped entries -> " + (ConvertTo-BrRelPath $notices))
    }
} catch {
    Set-BrGateStatus $gate 'error' ("notices.ps1: " + $_.Exception.Message)
}
exit (Complete-BrGate $gate -DryRun:$DryRun)
