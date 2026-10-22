<#
.SYNOPSIS
  Lane A's building generator (card T3.4): palette, plan and FBX for the town's buildings and the fixtures.

.DESCRIPTION
  Runs Blender headless ($BLENDER from tools\ci\env.ps1; no Unity, so no Unity slot). Finds the town folder
  (TownData\<town>\v1, the only one with a manifest) so no script names the real town.
    art\buildings\gen\run.ps1                    # palette + every building of the five types + fixtures
    art\buildings\gen\run.ps1 -Only way/123,way/456
    art\buildings\gen\run.ps1 -Doors <sites-S3 doors.json>   # real door specs when lane S writes them
  Outputs:
    Game\Assets\Art\Buildings\Textures\T_Bld_Palette.png     the palette atlas (art\buildings\palette\palette.json lists it)
    Game\Assets\Art\Buildings\Millbrook\BLD_*.fbx + buildings.index.json
    Game\Assets\Art\Buildings\_Fixtures\BLD_fixture_*.fbx + buildings.index.json
    art\buildings\millbrook\instances.json, doors.placeholder.json (and the same under art\buildings\fixtures)
  Then import in Unity (art\pipeline\unity-art.ps1 -Steps building-materials,reimport-buildings) and run the tests.
#>
[CmdletBinding()]
param(
    [string]$Only = '',
    [string]$Doors = '',
    [switch]$NoFixtures,
    [switch]$NoTown
)
. "$PSScriptRoot\..\..\..\tools\ci\env.ps1"
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $BLENDER)) { throw "Blender not found at $BLENDER" }
Set-Location $REPO
$towns = @(Get-ChildItem (Join-Path $REPO 'TownData') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'v1\manifest.json') } | Sort-Object Name)
if ($towns.Count -ne 1) { throw "expected one TownData\<town>\v1, found $($towns.Count)" }
$townDir = 'TownData/' + $towns[0].Name + '/v1'
$profPath = 'Game/Assets/Content/sites/buildingProfile.json'
$gen = 'art/buildings/gen'

function Invoke-Blender([string[]]$a) {
    $all = @('-b', '--factory-startup', '--python') + $a
    $ErrorActionPreference = 'Continue'      # Blender writes to stderr; PowerShell 5.1 would turn that into errors
    $lines = & $BLENDER @all 2>&1 | ForEach-Object { "$_" }
    $lines | Where-Object { $_ -match 'br_buildings|palette:|plan:|Error|Traceback|File "|outside the style' }
    if ($LASTEXITCODE -ne 0 -or ($lines -match 'Traceback')) { throw "Blender failed (exit $LASTEXITCODE)" }
}

New-Item -ItemType Directory -Force 'Game/Assets/Art/Buildings/Textures', 'art/buildings/palette' | Out-Null
Invoke-Blender @("$gen/palette.py", '--', '--png', 'Game/Assets/Art/Buildings/Textures/T_Bld_Palette.png', '--json', 'art/buildings/palette/palette.json')
if (-not $NoTown) {
    $a = @("$gen/br_buildings.py", '--', '--town', $townDir, '--profile', $profPath, '--plan-out', 'art/buildings/millbrook',
           '--out-dir', 'Game/Assets/Art/Buildings/Millbrook')
    if ($Only) { $a += @('--only', $Only) }
    if ($Doors) { $a += @('--doors', $Doors) }
    Invoke-Blender $a
}
if (-not $NoFixtures -and -not $Only) {
    Invoke-Blender @("$gen/br_buildings.py", '--', '--town', 'fixtures', '--profile', $profPath, '--plan-out', 'art/buildings/fixtures',
                     '--out-dir', 'Game/Assets/Art/Buildings/_Fixtures')
}
