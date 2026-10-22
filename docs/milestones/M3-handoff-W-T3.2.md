# M3 handoff: lane W, card T3.2 (terrain from the DEM, the river, land surfaces, the plates API, the WorldData export)

Branch `m3/w-terrain` in `D:\Backroads-Unity-wt\w`, cut from `m3/w-world-size` at `ac17549` (T3.1, approved, not yet
on main). Commits: `c7070f4` (the bake code and tests), `4d4a1b4` (the first `WorldData\millbrook\v1`), `16ddb17` (no real
town name in the code or the manifest), `1c4a88f` (the manifest re-baked at `16ddb17`; layers unchanged) and this
handoff. Nothing was pushed, installed, downloaded or signed into. **Merge T3.1 first.**

## In plain words

The new Millbrook now has its real ground. The lidar elevation of Stockport is laid on the game's 2 km grid every
half metre without inventing detail, plus a few centimetres of seeded roughness. The river has two flat water levels:
5.40 m above the dam and 2.03 m below it, 3.4 m apart. Its bed is dug 1.5-3 m under the water with banks that slope
in over 3 m. The dam is a concrete crest just under the upper water, and the lock beside the bridge is a walled chamber.
The ground is painted from the map: meadow, fields in crop rows, the wood's forest floor, rock on cliffs, mud on the
river banks, sand on the riverbed, gravel at the planned quarry, and the planned marsh at the creek mouth. Then every
road, walk, verge and bridge abutment of lane R grades the ground under it, and a flat pad goes under every building.
The plates go through the order agreed in ADR-007, and any lane can add its own (lane S's lots next).

The result is exported as `WorldData\millbrook\v1` in the same file formats as the old test world, with a manifest of
hashes. Baking again, with or without Burst, gives exactly the same bytes. Pictures (1 m or 0.5 m per pixel, hillshade
with surface colours, water blue) in `M3-handoff-W-T3.2/`:
- `t32-overview.png`: the whole 2 km square.
- `t32-bridge.png`: the bridge and its approaches.
- `t32-weir-lock.png`: the dam and the lock.
- `t32-marsh.png` and `t32-quarry.png`: the two added places.

The bake's numbers are in `bake-report.txt` there.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **W1 terrain**: the 1 m DEM resampled to the 4,097² grid at 0.5 m by bicubic convolution (Keys, a = −0.5: it passes through every lidar sample; the card's "bicubic", no new detail), plus a seeded micro-noise of 2-4 cm (two octaves of hashed value noise, 1.6 m and 0.7 m, tied to the local frame), under the lidar's own vertical noise. `base` = this natural ground with the river; `heights` = base after the plates. One kernel for a Burst job and the managed run | `World/Editor/Bake/TerrainSteps.cs` (`DemKernel`) |
| 2 | **The river**: one flat level per reach, the DEM's median inside it (5.401 m above the dam, 2.033 m below; the lidar's own hydro-flattened water differs by tile, 5.20-5.41 and 2.000-2.033). Every vertex the lidar shows as water (at most 0.3 m over the level) is carved: 1.5 m under the surface 3 m from the shore, 3 m in mid-channel (over the next 17 m); higher ground inside the polygon stays (banks the polygon overlaps). **The dam**: a concrete crest along the mapped weir line, 3 m wide, 0.1 m under the upper level, so the water spills over it (visual only). **The lock**: a walled chamber along the mapped lock channel, 10.6 m inside, 1 m walls 1 m over the upper level, floor 3 m under the lower level, emptied to the lower level. Ground beside the river (within 6 m, outside the polygons) that lies up to 0.3 m under the nearest level is lifted 5 cm over it (31,725 vertices; the lidar's 2.000 water spills a little past the polygon) | `RiverStep` |
| 3 | **Land surfaces**, `mud` and `tint`: meadow (grass) by default and on parks, pitches, the cemetery; farmland in 2 m crop rows (1.5 m dirt, 0.5 m grass) along each field's longest side; the wood's forest floor (dirt, darker and damper tint); the scout's quarry (gravel, a ragged 45 m disc); the scout's **marsh at the creek mouth** (bog mud, MUD 120-230, on the low flat river bottom within 150 m of the scout's spot, a ragged edge with pools); riverbed sand; **riverbank mud** within 3 m of the water and 1 m over it; **rock on slopes over 60 %**. The 8 m tint grid: two octaves of noise (−0.72..0.72 like v1) shifted by land use | `SurfaceStep` |
| 4 | **Plates API** (`PlateSink : IPlateSink`, ADR-007): plates are collected and applied once, sorted by `PlateOrder` (stage, priority, id). Per stage, every plate sets the grid vertices inside its polygon (even-odd) to its target and surface, and a later plate wins. Then the stage's blend bands: a vertex outside every plate of the stage eases linearly from the nearest plate edge back to its height before the stage, `before + (edge − before)(1 − d/blend)`, so blend = 3 × the step keeps 1V:3H. A vertex inside an earlier stage's plate is never touched by a later stage's band, and where bands overlap the strongest wins. Heights: `Plane`; `LeastSquares` (fitted, slope clamped to `maxSlope`); `PerVertex` (the fan from vertex 0 when convex, exactly lane R's `PlateField`, ear clipping otherwise). One kernel runs as a Burst job (`FloatMode.Strict`) or managed, with the same bytes. The sorted list is hashed; the same plates in any order give the same bytes. **Sources**: lane R's `RoadPlateBuilder.Build` (88,024 plates: ribbons, junctions, walks, verges, shoulders, the bridge abutments), W's building pads (`PadStep`: 266, flat at the DEM height at each footprint's centroid, blend 3 × the largest corner step, 1.5-12 m) and `WorldBakePlateSources`, a registry other lanes add their plates through (lane S's lots, see For S) | `PlateSink.cs`, `PadStep`, `WorldBake.cs` |
| 5 | **W8 export**: `WorldData\millbrook\v1\terrain\{heights,base}.f32`, `{surface,wear,roadd,mud}.u8`, `tint.f32` in the CanonData v1 formats at the 4,097² layout, in JS space (x_js = −x, flipped once here). `ROADD` is the chamfer distance to the carriageway (`road:`, `abutment:`, `apron:` plates) in 0.5 m steps, `WEAR` zero. Also `water.json` (the reaches with their levels and polygons, the weir crest, the lock chamber and walls) and `structures.json` (lane R's bridge decks with `overRiver`, for dropping the ground's collision under the deck in T3.3). `manifest.json` holds the grid block (`WorldGridLayout.FromManifestFile` reads it), the frame, the TownData manifest's hash, the bake's commit and versions, the plate counts and list hash, and bytes and SHA-256 of every file. `WorldBakeCli.Export` / `Verify` (batchmode), menu *Backroads > World > Bake Millbrook (T3.2)*, report and previews in `artifacts\world\millbrook\` | `WorldDataExport.cs`, `WorldBakeCli.cs` |

## Done when, expected vs measured

| Item | Measured |
|---|---|
| `World.BakeDeterministic`: bake twice (also in a fresh worktree), identical hashes | **pass**: a Burst bake and a managed bake give the same SHA-256 for all nine files and the same plate list hash, and both equal the committed manifest. Three exports in separate Unity processes (the last from the clean commit `c7070f4`) wrote the same hashes. The test compares a fresh bake with the committed hashes, so the merge gate on main repeats it in a different checkout |
| `World.ManifestHashes` | **pass**: every file is listed with its size and SHA-256, and they match; the grid block reads back as `WorldGridLayout.Millbrook`; the TownData manifest hash is the current one; no CR in the JSON |
| `World.RiverLevels`: water flat per reach within 2 cm; nothing dry below it inside the river polygon | **pass**: each reach's surface is one level (flat by construction, 0 cm); inside the polygons, away from plates and the dam and lock, 1,171,391 vertices: none awash (every one at least 0.1 m under the water or 0.25 m over it); beside the river (within 6 m) 76,591 vertices: none in the 0.3 m just under the level |
| `World.DemFidelity`: outside roads, lots and the river, heights within 0.10 m of the lidar at 1,000 seeded points | **pass**: max 0.071 m, mean 0.010 m (the micro-noise and the bicubic between samples) |
| Extra: `World.PlatesMatchRoadReference` | **pass**: at all 706,940 grid vertices a road-stage plate sets, the height equals lane R's own reference (`PlateField`) to 0.0000 m |
| Extra: `World.PavementCoversTerrain` (R2's grid check at 0.5 m) | **pass**: at 200,000 points in fully paved cells, the ground as the game reads it rises over the plate target (5-6 cm under the paving) by 30 cm at worst, and over 3 cm only at 30 points (0.015 %), every one in a cell across a height step between two of lane R's paved pieces (For R) |
| Extra: `WorldPlates.*` (6 tests on synthetic grids) | **pass**: linear blend and its edge, order independence (reversed, shuffled), priority and stages, earlier stages protected from later bands, least squares clamped, a concave PerVertex plate, Burst = managed, bad plates rejected |

EditMode World, Deform and Render: **39 passed**. The bake takes about 21 s (lane R's plates 4.6 s, the plates 0.7 s
with Burst); the World tests about 80 s, both bakes included.

## For I

- **Merge order**: this branch is stacked on T3.1 (`m3/w-world-size`). The gate below ran with
  `BR_OWNERS_BASE = ac17549` (T3.1's tip), so `Repo.OwnersRespected` checked exactly T3.2's changes for lane W.
- **ADR-007**: the applier rules W chose are worth a line there: linear bands (so blend = 3 × step is 1V:3H); a later
  stage's band never changes an earlier stage's plate; overlapping bands, the strongest wins; `maxSlope` applies to
  `LeastSquares` plates only; `noDeform` is carried by the surface (asphalt 4 and concrete 6 are locked by the
  deformation kernels; CanonData v1 has no NODEFORM layer).
- `WorldData\` adds about 197 MiB of LFS objects (the store was at 0.27 of 6 GiB). A re-bake changes only the layers
  whose bytes change.

## For S

- Register lot plates with `WorldBakePlateSources.Register("sites", townId => plates)` from S's editor assembly
  (`[InitializeOnLoadMethod]`); `WorldBake.Run` submits them, ADR-007's order places them after R's roads and before the
  pads, and the manifest lists the source. Details: notes folder, `lane-W-T3.2-plates-for-S.md`.
- Driveway cuts: lane R's builder lowers the walks under driveways when it gets `(cuts, cutter)`; W calls it without
  cuts today. To be wired with S4.

## For R

- **Height steps between paved pieces**: 30 of 200,000 sampled points rise more than 3 cm over the plate target. The
  worst is at local (251.7, 538.1), where `walk:f00068:0003:4` meets a piece 0.31 m higher; others are on
  `walk:f00199`, `walk:f00270`, `walk:f00100`, `road:n0021`, `road:e0056`, 3-10 cm. The test lists them. Where your
  meshes step there too nothing shows; otherwise the ground can poke up within a cell of the step.
- 16,557 of the 88,024 road plates cover no grid vertex (pieces smaller than the 0.5 m spacing); their neighbours cover
  the ground, and the reference check above passes everywhere.
- `BridgeBuilder.Build` gets no `over` map from W, so `structures.json` has `over: ""`; W sets `overRiver` itself by
  testing the deck outline against the river polygons (the river bridge: `true`).

## Where this differs from the card

- **Micro-noise** is 2-4 cm (the card says 2-5), so the fidelity bound keeps a margin.
- **One level per reach**: the lidar's water is flattened per tile (8.5 cm and 3.3 cm spreads), so each reach takes the
  DEM's median and the bed is carved under it. The water surface is flat by construction.
- **Dam and lock** are terrain only (a crest, walls, a chamber; visual only, as the card says). The spill sheet over
  the crest and the water meshes are T3.3's, from `water.json`.
- **Checked by eye**: the bridge approaches meet the banks with lane R's abutment plates, and the river stays open
  under the free span (`t32-bridge.png`). The lock's walls stand along the east bank at the mapped gates
  (`t32-weir-lock.png`). The chamber is placed along the mapped lock line, not traced from the lidar.
- **Gravel** comes from the quarry only: the extract maps no other gravel or quarry land use. The depot is T3.3's
  (with a pad from S's site plan).
- **Building pads** are W's own (ADR-007's open question 1, default: W computes them from the DEM); lane A or S can
  replace them through the plate sources.
- **No NODEFORM layer** (not a CanonData v1 format); see For I.
- **No real name**: the bake finds the town from the data (the only TownData extract, or `-town`), and the WorldData
  manifest names its source by the TownData manifest's hash and the in-game name, not by the extract's folder
  (`Town.NoRealNames` scans every `.cs` file; WorldData ships with T3.3).

## Next

T3.3: the new `Millbrook.unity` on `m3/w-scene`, cut from this branch. The runtime loads `WorldData` (an importer like
the canon's), the 1,024 chunks, water from `water.json`, the ground's collision dropped under the decks from
`structures.json`, grey-box buildings on the pads, first nature, places, the minimap; vehicles at the new garage.

## Resume

```
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldBakeCli.Export    (re-bake, writes WorldData\millbrook\v1)
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldBakeCli.Verify    (bake in memory, compare with the manifest)
tools\ci\unity-tests.ps1 -Platform EditMode -Category "World;Deform;Render"
```

## Merge

`$env:BR_OWNERS_BASE = 'ac17549'; $env:BR_OWNERS_LANE = 'W'; tools\ci\gate.ps1 -Tier merge` (T3.2's changes only, lane W paths;
`ac17549` is T3.1's tip, which main does not have yet): **pass, 8 of 8 steps** (2026-10-22, run
`artifacts\20261022-1236\gate-merge`, on `1c4a88f`): oracle.hash; vendor.check (584 files); native.ctest; unity.editmode 225
passed, 1 skipped (ServerContents: no server build), 0 failed, `Repo.OwnersRespected` 33 changed paths for lane W,
`Town.NoRealNames` pass; unity.playmode 9 of 9; build.servercompile; build.dev (230.6 MiB); autotest.smoke. A first
run (`20261022-1229`, on `4d4a1b4`) failed `Town.NoRealNames` (the extract's id as a default in three `.cs` files),
fixed in `16ddb17`. `GridLayout.StepCostMatchesM2` passed on a busy machine (heightAt 0.894, rayTerrain 1.039 of M2).

The native step's `EngineSimPlugin.dll` was restored afterwards (lane V's, committed only at milestone tags).
