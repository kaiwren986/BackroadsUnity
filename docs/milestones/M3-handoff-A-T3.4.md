# M3 handoff: lane A, card T3.4 (Blender building generator, phase 1: style guide and the first five types)

Branch `m3/a-buildings` in `D:\Backroads-Unity-wt\a`, cut from `main` at `bbe542f`. Commits: `a0c12f3` (generator,
style guide, models, tests) and the handoff commit after it. Nothing was pushed, installed, downloaded or signed into.
POLYGON City was imported into this worktree with Unity's own importer (from the user's downloaded package, as
`synty-manifest.json` describes) to measure it; the pack folders are gitignored, no Synty file is committed, and no
Synty mesh, texture or render went into any script, model or AI tool.

## In plain words

Millbrook's buildings now come out of our own Blender generator instead of grey boxes. It reads each building's real
footprint from the town map, decides its storeys, roof and colours from the measured building profile (always the same
answer for the same building), and builds it in the Synty style: flat colours from one palette texture, hard edges,
window frames, sills and cornices that stand out from the wall. Houses get gables or hips, cross gables on L and T
shapes, front porches with steps where the yard is deep enough, chimneys and shutters. The street-front shops are
brick blocks with shopfronts, awnings, cornices and blank shared side walls. Garages and machine sheds get their big
doors toward the street or the driveway; barns get sliding doors and a hay door; warehouses get dock doors at the side
or back; the old hotel by the dam becomes the four-storey mill with a cupola. Every building has three levels of detail,
collision shapes, a named glass mesh for night lighting later, and door markers where the site plan's doors are.
247 of the town's 269 buildings are modelled (all 223 houses, the 8 street-front shops, 13 garages, 2 machine sheds and
the mill); the other 22 (standalone shops, the school, the fuel canopy, the car wash, two shelters) stay grey boxes
until phase 2. Pictures in `art/buildings/review/`: the types side by side from the street and from behind
(`types_*.png`), the town's buildings on the real ground around downtown, the mill and a residential street
(`millbrook_*.png`), and one house at its three levels of detail (`house_lod0_lod1_lod2.png`).

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Style guide**: the Synty City buildings measured in Unity, numbers only (`SyntyStyleAudit`: 76 prefabs; one material and a 1024 atlas per building; saturation median 0.18, value median 0.53; windows 1.02 × 1.59 m; storey pitch 2.76 m; bands 0.19 m; ledges 0.16 m; 4k-18k triangles per building), and our rules from them: palette limits, materials, trim and ledge sizes, sizes per type, model structure, LODs, budgets. **Our palette**: 98 flat swatches of our own Ohio river-village colours on a 512 atlas, inside the measured saturation and value ranges (`palette.py` refuses anything outside). Contact sheet of ours beside Synty's, local only | `art/buildings/style-guide.md`, `art/buildings/palette/palette.json`, `Game/Assets/Art/Buildings/Textures/T_Bld_Palette.png`, `Game/Assets/Backroads/Art/Editor/SyntyStyleAudit.cs`, `artifacts/art/contact-sheet.png` (gitignored) |
| 2 | **Generator** (Blender 4.5 headless, plain Python geometry): `plan.py` turns each building of the extract into an instance (model type, storeys and roof from its tags, then the profile's North American numbers drawn with blake2b-64(TOWN_ID, osm, part), then the 08b §5 [doc] heights and pitches; ground from the DEM; street side; shared walls; colours; porch; doors). `build.py` builds walls on the footprint (simplified within 0.08 m) cut on a grid with real openings, roofs over the footprint's rectangle split (gable, hip, pyramid, gambrel, lean-to, flat with parapet and cornice; valleys where a wing's ridge runs into the main roof), porches, stoops, chimneys, cupola, shopfronts, awnings, dock bumpers; LOD0/1/2; convex hulls; door markers; a budget fallback. `br_buildings.py` writes one FBX per building through the A1 exporter (deterministic bytes, no tangents) and `buildings.index.json`. `run.ps1` does palette, town and fixtures in 52 s | `art/buildings/gen/` |
| 3 | **The five types**: the frame house, the brick commercial block, the barn and machine shed family (barn, machine shed, garage, shed), the warehouse (docks from the door specs), the mill. Fixtures cover what the town lacks (2 barns, 2 warehouses, a shared-wall row of 3, L, T and hipped houses, a cottage, a garage, a shed, a mill) | `art/buildings/fixtures/`, `Game/Assets/Art/Buildings/_Fixtures/` |
| 4 | **Batch**: every building of those types, deterministic per OSM id, imported through the A1 pipeline into `Game/Assets/Art/Buildings/Millbrook/` (247 FBX, 33.7 MB in LFS). Import rules for `Assets/Art/Buildings/`: two project materials (`M_Bld_Atlas`, `M_Bld_Glass`, remapped by name on the first import), no tangents, colliders only from `UCX_`, one LOD Group from the names. `buildings.index.json` is the hand-over to lane W's `BuildingSet` (below) | `Game/Assets/Art/Buildings/`, `Game/Assets/Art/Materials/Buildings/`, `BackroadsModelPostprocessor.cs` (version 3), `BuildingMaterials.cs` |
| + | Review renders in Unity (URP, the town's buildings on DEM ground) and the contact sheet: `art\pipeline\unity-art.ps1 -Steps building-capture,contact-sheet -Graphics` | `BuildingCapture.cs`, `art/buildings/review/` |

## Done when, expected vs measured

| Test | Expected | Measured |
|---|---|---|
| `Art.BuildingsBudget` | LOD0 ≤ 6k triangles for a house, ≤ 15k for a block or the mill; ≤ 3 materials; colliders present; panes named | **pass**. 262 models (247 town + 15 fixtures). LOD0 max: house 5,893 / 6,000 (median 2,468; one house uses the reduced-detail fallback), block 3,886 / 15,000, mill 6,877 (town) and 10,414 (fixture) / 15,000, barn family 920 / 6,000, warehouse 1,956 / 15,000. LOD1 about 15 % and LOD2 about 6 % of LOD0. Two materials on every model (the project's `M_Bld_Atlas` and `M_Bld_Glass`). 706 convex colliders, none rendered. 7,280 panes, each model's `Panes_LOD0..2` on `M_Bld_Glass` with a pane id in every vertex colour. One 3-level LOD Group per model. 0 triangles dropped by the importer |
| `Art.BuildingsDeterministic` | generate twice → identical FBX hashes | **pass**: one building of each model type of the town plus all 15 fixtures, generated twice by Blender from the test into two empty folders: identical, and equal to the committed files and the index's sha256. Also checked by hand: the full batch (262 FBX and both indexes) regenerated in a fresh process is byte-identical to the commit |
| `Art.DoorsMatchSites` | every `DoorSpec` from S3 has an opening within 0.1 m | **pass against the placeholder specs** (S3 has not landed): 381 door specs on 247 buildings, worst distance 3 mm, every opening at least the spec's width, and the LOD0 wall open across every doorway |
| `m3.shots.sites` shows them | | **not yet**: the buildings appear once lane W's `BuildingSet` (T3.3) places them from the index and lane S's autotest exists |
| Sign-off sheet | our five types beside the style guide's numbers | `art/buildings/style-guide.md` §7 and the review renders |

Art tests 6 of 6 (`Canary`, `TruckFbxMatchesGlb`, `LodGroupFromNames` unchanged). Merge gate: see the last section.

## Waiting on you

Nothing blocks the merge. Three choices are mine by default; say if you want them otherwise:

1. **Which buildings count as the five types.** The map tags almost nothing here (255 of 269 buildings are plain
   `building=yes`, typed by size), and the town has no mapped barn, warehouse or mill. So: 15 of the 17 accessory
   buildings are built by the barn-and-machine-shed generator (13 garages, 2 machine sheds; the two shelters stay grey); the 17
   standalone "shops" (untagged buildings of 320-750 m² the classifier calls retail) stay grey boxes until phase 2;
   warehouses exist only as fixtures. **Default:** keep this; phase 2 adds a standalone shop (and church, school, fuel)
   model. **Alternative:** build the standalone shops now with the block generator in a one-storey, free-standing form.
2. **The mill.** The building 20 m from the dam's weir (mapped as a hotel, 554 m², the old mill building) is built as
   the four-storey mill with a cupola. Rule: any non-house building of 300 m² or more within 40 m of a weir, dam or lock.
3. **Heights** are North American standards (08b §5): real eaves and ridges would come from the lidar point cloud
   (design §14 #5, a download that needs your approval, not requested here).

## Where this differs from the card

- **Doors are placeholders.** Lane S's sites-S3 writes the real `DoorSpec`s later; until then `plan.py` places doors
  by the design's rules (site-layouts §7a) and writes them in the `DoorSpec` fields to
  `art/buildings/millbrook/doors.placeholder.json`. When S3 lands: `art\buildings\gen\run.ps1 -Doors <S3 file>`
  regenerates every building against the real specs, and `Art.DoorsMatchSites` checks those instead.
- **Runner location**: `art\buildings\gen\run.ps1` (and `art\pipeline\unity-art.ps1` for the Unity steps), not
  `tools\ci\` (lane I's). Blender takes no Unity slot.
- **Two materials**, not three; glass is opaque (no interiors); night lighting waits for M5's shader (the pane ids are in).
- **The barn family includes garages and sheds** (the town's accessory buildings); **warehouses** are fixtures only.
- **Footprint simplification** 0.08 m instead of the design's 0.2 m, so door openings stay within the 0.1 m test.
  Roofs sit on the footprint squared up within 15°; the walls stay on the real footprint and the overhang covers the
  difference.
- **Porches** stand outside the map's footprint (2.0-2.4 m deep, only where the setback is 4 m or more); their outlines
  are in the index (`porch`) for the site plan.
- `Art.BuildingsDeterministic` regenerates a sample (one of each model type of the town and all fixtures) to keep the
  test under a minute; the full batch was checked by hand (above).
- The import-rule version bump (2 → 3) makes Unity reimport every model once per worktree (Synty's too where imported).

## For lane W (T3.3 `BuildingSet`): the hand-over

- Read `Assets/Art/Buildings/Millbrook/buildings.index.json`: one entry per modelled building with `osm`, `part`,
  `model`, `family`, `fbx` (file name in that folder), `origin` (TownFrame metres = Unity axes), `eave`, `height`,
  `groundMin`/`groundMax` (relative to the floor), `porch` (town-frame outline or null), `doors`, triangle counts,
  `sha256`. For each `buildings.json` building whose `osm` + `part` is in the index, instantiate the FBX at `origin`
  with **no rotation** instead of the grey box; keep the grey box for the rest.
- The model's origin is the footprint centroid **at floor height**, from the DEM under the footprint: houses, the mill,
  shops and warehouses from the highest ground plus a plinth (houses 0.5-0.9 m, the mill 0.4 m, others 0.15 m),
  lowered by part of the slope on steep lots so the uphill wall cuts into the bank; garages, sheds and barns from the
  low side, so their big doors open near grade. Walls run down to `groundMin` − 0.6 m, so terrain within that range
  never shows a gap. If your terrain bake wants building pads (`PlateKind.Pad`), the footprint and `origin.y` are the
  pad.
- Colliders, the LOD Group and the materials come with the model; set static flags as you place them.

## For lane S

- `doors.placeholder.json` shows the format the generator reads: `{"doors": [{building, part, kind, wallEdge, offset,
  width, sillY}]}` with `kind` as a `DoorKind` name or number and `sillY` null for NaN. Please write S3's file the same
  way (any path); the door markers are named by each spec's index among its building's specs, in file order.
- Door sills sit at the model's floor (`origin.y`); garage, bay and dock doors open at floor level, house doors at the
  top of the steps. `ModelRequest`s (canopy, dispensers, pylon, grain bins, scale house, dock levellers) are phase 2.

## For lane I

1. `tools\ci\gates.json`: `unity.editmode.art-buildings` can drop `pending` and go `hard` when this merges (T3.7).
2. A `tools\ci\art.ps1` (PLAN §1.9) could wrap `art\buildings\gen\run.ps1` and `art\pipeline\unity-art.ps1`.
3. `tools\ci\canon-import.ps1`, named by `worktree-new.ps1`, does not exist; the canon output appeared with the first
   test run's editor load (`Game/Assets/Backroads/World/Canon/v1`), so nothing was needed here.

## Next (phase 2, M4)

The remaining types (standalone shop, church, school, fuel shop with canopy, dispensers and price pylon, car repair
with bays, apartments, motel); S3's real door specs and `ModelRequest`s; M5's building shader (night windows by pane
id, wall value check against the contact sheet); lidar heights if approved.

## Resume

```powershell
cd D:\Backroads-Unity-wt\a
art\buildings\gen\run.ps1                                     # palette, the town's buildings, fixtures (Blender, ~1 min)
art\pipeline\unity-art.ps1 -Steps building-materials          # import and the two materials (first time)
tools\ci\unity-tests.ps1 -Platform EditMode -TestFilter Backroads.Tests.EditMode.Art
art\pipeline\unity-art.ps1 -Steps building-capture -Graphics  # review renders -> art\buildings\review
# style audit and contact sheet need POLYGON City imported in this worktree (Unity's importer; gitignored):
art\pipeline\unity-art.ps1 -Steps synty-style
art\pipeline\unity-art.ps1 -Steps contact-sheet -Graphics     # -> artifacts\art\contact-sheet.png, never committed
```

## Merge

`$env:BR_OWNERS_BASE='main'; $env:BR_OWNERS_LANE='A'; tools\ci\gate.ps1 -Tier merge` on `a0c12f3`: **pass**, 8 of 8
steps in 624 s (2026-10-22 11:45): oracle hash, vendor check, native ctest, EditMode **208 passed, 1 skipped, 0
failed** (with `Repo.OwnersRespected` on lane A's paths and the three building tests), PlayMode 9 of 9, server compile,
Dev build (231 MiB), smoke autotest. The native plugin the gate rebuilt was restored (`git restore`, lane V's files).
Lane A paths only: `art/`, `Game/Assets/Art/`, `Game/Assets/Backroads/Art/`, `Game/Assets/Backroads/Tests/EditMode/Art/`,
this file. 271 files in LFS (262 FBX, 9 PNG; about 37 MB).
