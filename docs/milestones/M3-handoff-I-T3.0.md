# M3 handoff: lane I, card T3.0 (town source and bake contracts)

Branch `m3/i-town-source` (worktree `D:\Backroads-Unity-wt\i`), from `main` at 0869750. Not merged: the integrator
merges it after `tools\ci\gate.ps1 -Tier merge` (see "Merge" below).

## Done

- **Downloads** (approved U23/U25), cached in `artifacts\world-src\stockport-oh\` (gitignored), pinned in its
  `sources.json` and copied into `TownData\stockport-oh\v1\sources.json`: one Overpass query (0.49 MB, overpass-api.de,
  OSM base 2026-10-20T02:17:06Z) and the 3DEP 1 m DEM as 9 overlapping tiles (29.6 MB raw F32), plus the ImageServer
  description and its catalog query (30 KB). Tile seams compared: 170,560 shared pixels, all bit-identical.
- **Tools** (zero-dependency Node): `tools\world\fetch-town.mjs`, `extract-town.mjs` (`--check` re-extracts into a
  temp folder and compares), `town-lib.mjs`, `dem-lib.mjs`, `classifier-fixture.mjs` (`--check` runs measure.mjs
  typeOf on the fixture); `tools\osm\measure.mjs` exports `typeOf`, `buildTownFrom`, `lotAnalysis`, and its `main()`
  runs only as a script; `tools\scout\render-map.mjs --town-data <dir>` (new `render-towndata.mjs`).
- **The extract** `TownData\stockport-oh\v1\` (ADR-007 §1): roads, buildings, sites, landuse, water, the DEM
  (`dem_1m.f32` in LFS), sources, credit, manifest. Re-run from the cache: byte-identical.
- **Contracts** `Game\Assets\Backroads\Core\Runtime\WorldBakeContracts.cs` and `BusinessClassifier.cs`
  (`Backroads.Core.WorldBake`); draft `docs\adr\ADR-007-world-bake-contracts.md`.
- **Tests** `Backroads.Tests.EditMode.Town`: `Town.ManifestHashes`, `Town.NoRealNames`, `Town.ClassifierFixture`,
  `Town.FrameRoundTrip`, all passing; `TownData` and `WorldData` joined `Repo.NoCrInHashedFiles` and AGENTS.md rule 7;
  `.gitattributes` has `TownData/** -text` and `WorldData/** -text`.
- **Gates** `tools\ci\gates.json` milestone M3 (nightly and milestone lists) from T3.7's table; steps whose tests are
  not on main yet carry `"pending"` and `"hard": false`.
- **Parameters** filled in `docs\cards\M3\sites-roads-overview.md` (origin, bbox, DEM, Y0, kerb and downtown zones,
  what the map really has). The map: `docs\milestones\M3\town-source.png`.

## Next

- Integrator: merge; when each lane lands its test or scenario, drop `pending` and `"hard": false` on its M3 step
  (T3.7). Mark ADR-007 accepted at the M3 tag together with ADR-006.
- Lane W takes over `TownData\` and `tools\world\` (OWNERS.json already says so).
- Lane S: 17 of the 20 Retail buildings are untagged 320-650 m² buildings typed by size (measure.mjs's rule); most are
  probably large houses or barns. S3 may want to treat an inferred Retail with no point of interest as a house or barn.

## Resume

```
node tools/world/extract-town.mjs stockport-oh --check
node tools/world/classifier-fixture.mjs --check
tools\ci\unity-tests.ps1 -Platform EditMode -Category Town
node tools/scout/render-map.mjs --town-data TownData/stockport-oh/v1
```

## Merge

The branch changes W-owned paths (`TownData\`, `tools\world\`), which the card gives to lane I, so run the merge
tier as `$env:BR_OWNERS_LANE = 'I,W'; tools\ci\gate.ps1 -Tier merge` (or merge with the integrator's own lanes).
