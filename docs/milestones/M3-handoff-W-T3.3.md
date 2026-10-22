# M3 handoff: lane W, card T3.3 (the new Millbrook scene)

Branch `m3/w-scene` in `D:\Backroads-Unity-wt\w`, cut from `m3/w-terrain` at `ab86ce9` (T3.2, now on main). Commits:
`fe16b56` (code, scene, tests, autotests), `df21056` (`WorldData\millbrook\v1` re-baked at `fe16b56`), `ab179cf`
(main `eb86272` merged in: T3.5 and Roads.Validation, no conflicts), `9f8a95b` (the village street view looks along a
street; dusk shots at 20:00), `9e6edb6` (WorldData re-baked at `9f8a95b`: only `places.json` changes), `67fd8c4` (the
first handoff), `6ce2620` (the `ActiveWorld` contract for lane V; five garage bays as the canon's), **`6336827` (T3.3 for
I, reviewed by the integrator at merge: `Millbrook.unity` in `EditorBuildSettings.asset`, approved by the
coordinator)**, `ee80888` (WorldData re-baked at `6336827`: only the bays in `places.json`) and this update. Nothing was
pushed, installed, downloaded or signed into.

## In plain words

The real town is now a place you can fly around in. `Scenes\Millbrook.unity` loads the baked world in about 1.4 s in
the Dev player (15 s the first time a new build runs) and shows the 2 km square: the lidar ground in 1,024 chunks, the
river at its two levels with the water spilling over the dam and the lock beside the bridge, lane R's roads, walks and
signs, 245 of lane A's building models and 21 grey boxes where lane A has no model yet, about 21,000 trees, bushes and
reeds, rocks on the cliffs, and the sky and sun of M1 with day and night (] and [ move the hour). It runs fast: the
flythrough's slowest 5 % of frames take about 7 ms at 2560x1440 on High, a little better than the old 1 km test world.

Trees grow where the land says: mixed woods in the forest and on steep hollow slopes, a line of trees along the river,
a few in the meadows, hedgerows of bushes along the field edges, reeds in the marsh and on the muddy banks, dead snags
in the bog. Nothing grows on roads, walks, pads, plates or water.

The places of the old test world are found again in the new town with their game names: Millbrook Garage (with its five
parking bays), Pine Hollow Mill by the dam, Route 9 Fuel, River Landing, Harlow Farm, Greystone Quarry, the Freight
Depot and Blackwater Bog. Twelve fixed camera views look at them; the autotest takes each at noon and at dusk. Contact
sheets in `M3-handoff-W-T3.3/`:
- `t33-shots-1200.jpg`: the 12 views at noon.
- `t33-shots-2000.jpg`: the same at 20:00 (sunset).

The full-size shots (1600x900) are in `artifacts\autotest\m3.shots.millbrook\` on this worktree.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **The scene**: `Scenes\Millbrook.unity`, a thin bootstrap (camera, sun, volume, the `World` root with `TownWorld`). `TownWorld` builds the rest at run time from the imported WorldData: the 1,024 terrain chunks with LODs (M1's `TerrainChunkSystem` on the 4,097² layout), the river (one flat surface per reach polygon at its level, the lock chamber at the lower level, and a sheet over the dam from the crest 3 m downstream), lane R's road chunks (`RoadChunkLoader.Spawn`, R4's baked chunks, not R0's ribbons) and lane R's props (placed into the scene by `RoadPropPlacer` when the scene is built, 213, none Synty), the deformation, the day/night lighting. `WorldDataImporter` turns `WorldData\millbrook\v1` into `WorldDataAsset` (gitignored, per worktree, like the canon import; checks every file's hash; a player build imports it in a pre-build hook). The world becomes `ActiveWorld`: `Ground`, `FreeFlyCamera` and `VehicleCameraRig` now read the active world instead of the canon only | `World/Editor/Bake/WorldDataImporter.cs`, `WorldDataBuildHook.cs`, `MillbrookTownSceneBuilder.cs`, `World/Runtime/WorldData/`, `Client/Runtime/TownWorld.cs` |
| 2 | **Grey-box buildings and `BuildingSet`**: one record per footprint (osm, part) in `WorldDataAsset.buildings`. Where lane A's `buildings.index.json` has the building, its model is placed at its `origin` (no rotation, as lane A's note says), with lane A's LODs and colliders; otherwise a grey box: the footprint from 0.6 m under the pad up to its storeys (`levels` tag, else the type's p50 storeys from `buildingProfile.json`) × 3 m + 0.4 m, a flat roof or a 2.5 m gable over the oriented rectangle by type (tag, else the type's roof share), coloured with swatches of lane A's palette atlas so one material (`M_Bld_Atlas`) draws them all, a MeshCollider. Every building also has a box collider in the static collider grid (lane V's) | `WorldDataImporter.Buildings`, `GreyBox`, `Colliders` |
| 3 | **First nature** (bake v2, `NatureStep`): jittered lattices with hashed choices (no randomness at run time), written as CanonData v1's `vegetation.json` with the canon's species. Forest at 7 m (wood surface 85 %, hollow slopes 22-45 % up to 80 %, thinned near roads), riparian trees 4-25 m from the water, meadow trees at 35 m, hedgerows (a bush every 2.6 m, 1.2 m outside field edges, some oak and maple), reeds at 2.4 m in the marsh and on muddy banks, snags at 30 m in the marsh, rocks at 20 m on rock. Result: 20,886 (Oak 5,032, Maple 4,407, Poplar 2,280, Birch 1,480, Pine 1,225, Spruce 711, Snag 466, Bush 2,073, Reeds 3,212) and 153 rocks | `World/Editor/Bake/NatureStep.cs` |
| 4 | **Places** (`PlacesStep`, provisional until lane G's W7): `places.json` with the pois (garage = the car-repair building; mill = the largest non-house building of 300 m² or more by the dam; fuel; landing = the slipway; farm = the largest field; town centre = the commercial centroid; quarry, depot and bog at the scout's spots), the garage's five bays in front of it facing it, 5.6 m apart as the canon's (`parking`), and the 12 camera views, each the best of 16 directions by line of sight (the street view stands on the carriageway) | `NatureStep.cs` (`PlacesStep`) |
| 5 | **Map**: `map.png` (2,048², 1 m per pixel): surfaces, 10 m and 50 m contours, water, roads, walks, buildings; in the asset as `map` | `WorldDataExtras.Map` |
| 6 | **Vehicles at the new garage: lands with T3.6** (lane V, after T3.3 is on main). W's part is done: `ActiveWorld` serves both worlds the same way (grid, static colliders, vegetation, five garage bays, places; contract under For V), and `ActiveWorldScenes.ServesBothWorlds` checks it in both scenes. Lane V switches `VehicleSpawner` and `VehicleSimSystem.Bind` from `CanonRuntime` to it. The free-fly camera covers the whole square (extent = half + 150 m) | `World/Runtime/WorldData/WorldDataRuntime.cs` (`ActiveWorld`) |

## Done when, expected vs measured

| Item | Measured |
|---|---|
| Autotest `m3.shots.millbrook`: 12 fixed cameras, noon and dusk | **pass**: 24 shots of 24 (the 12 views at 12:00 and 20:00), 0 errors, 0 exceptions, in a Dev player (`M3-9e6edb6`, RTX 4090, High). The town was ready 15.5 s after its scene started (timed steps: data 0.1 s, the 1,024 chunks 0.03 s, roads 0.9 s, buildings 0.05 s, trees 0.5 s; the rest is in the first frames, see below the table). Checked by eye: every view shows what it names (see Where this differs for the weak ones) |
| Perf `m3.perf.flythrough-high` re-measured on the new world (report only) | **reported** (gate config: `-Quality High -Repeat 3`, 2560x1440 on screen, Dev player `M3-9e6edb6`, RTX 4090, Ryzen 9 5950X, nothing else on the machine): a 48 s flight through the 12 views and a low pass down the village street, 11,700-12,000 frames per run. p50 **4.12-4.23 ms**, p95 **6.99-7.17 ms**, p99 8.23-8.41 ms, worst frame 11.6-12.7 ms; 2,542 chunk builds per flight; the town ready in 1.3-1.4 s. For scale: M1's 1 km canon world measured p95 7.6-7.8 ms on the same machine, and M5's target is p95 <= 11 ms |
| `Look.NoErrorShaders` on the new scene | **pass** (PlayMode, with a graphics device): Millbrook.unity loads with no error in the log; 45,854 renderers and the sky, 567 materials: no missing material, no error shader, every shader supported and with a URP pass (the built-in Standard shader would draw magenta), no reference to a missing texture |
| Extra: `World.ImportMatchesData` (EditMode) | **pass**: the import carries the manifest's layout and every layer at its size, a water mesh per reach polygon plus the lock and the weir sheet, all 266 buildings (each a model or a grey box), the model for every building in lane A's index, every tree of `vegetation.json`, a collider per building and per trunk, the 12 views, the garage, the 2,048 map, lane R's chunks |
| Extra: bake determinism | the re-bake at `fe16b56` wrote the same bytes for every file as the bake before it (v2 adds `vegetation.json`, `places.json`, `buildings.json`, `map.png`; terrain, `water.json` and `structures.json` unchanged); the re-bake at `9f8a95b` changed only `places.json`; `World.BakeDeterministic` and `World.ManifestHashes` pass |

The town loads in 1.3-1.4 s once the player has run before. The first start of a fresh build took 15-16 s in both
shots runs, with the same timed steps, so the extra time is in the first frames: most likely the driver compiling
shaders on first use (a shader warm-up, M5's look work, would cover it).

## For I

- **`EditorBuildSettings.asset`** (yours), commit `6336827`, labelled "T3.3 for I (reviewed by the integrator at
  merge)", as the coordinator approved: `Millbrook.unity` listed after `CanonTestWorld.unity`, because `BuildCli.Scenes`
  lists it once the file exists (`Build.ProjectSettings("build.ScenesMatchBuildCli")`). Nothing else outside lane W.
- **Client builds**: `BuildCli.ScenesFor(Client)` drops `CanonTestWorld` once `Millbrook.unity` exists, so a Client
  build starts in the new town, where no vehicle spawns until lane V's T3.6 binds the spawner to `ActiveWorld`. Keep
  the canon test world in Client builds until T3.6 lands, or accept a free-fly Client build in between.
- `WorldDataBuildHook` (W) imports WorldData before any player build once the scene exists; `BuildCli` could call
  `WorldDataImporter.EnsureImported` next to the canon's, for a clearer error.
- `gates.json`: `autotest.m3.shots.millbrook` and `autotest.m3.perf.flythrough-high` can drop `pending` when this
  lands (the perf one stays report only until M5).
- LFS: `map.png` (0.6 MB) and the two contact sheets (0.8 MB); the new WorldData JSON is plain text
  (`vegetation.json` 1.4 MB). The Dev build grows from 231 to 483 MiB (the imported layers and lane A's models).

## For V

The contract of `Backroads.World.ActiveWorld` (`World/Runtime/WorldData/WorldDataRuntime.cs`), for T3.6's switch of
`VehicleSpawner` and `VehicleSimSystem.Bind` from `CanonRuntime`. The doc comment in the code says the same.

| Member | What it is | Canon test world | New Millbrook |
|---|---|---|---|
| `Name` | the world's name; null when nothing is set | `"canon"` | `"millbrook"` (the WorldData world id) |
| `Grid` | the `WorldGrid` (heights, surfaces, mud, deformation layers) on its `WorldGridLayout`; query it as `CanonRuntime.Grid` | `CanonRuntime.Grid` (2,049², 0.5 m) | `WorldDataRuntime.Grid` (4,097², 0.5 m) |
| `Colliders` | the static `ColliderGrid`: a box per building, a circle per tree trunk; JS space, `PlacementSet.Collider` form; a collider's `tree` indexes `Vegetation.trees` | `CanonRuntime.Colliders` | built from `WorldDataAsset.colliders` (266 buildings, the trunks) |
| `Vegetation` | the `VegetationSet` (trees, bushes, reeds, rocks; JS space) | the canon's | `vegetation.json` (20,886) |
| `GarageBays` | `PlacementSet.Parking[]`, JS space (x, z, yaw; `x_js = −x`, `yaw_js = −yaw_unity`) in VEHICLE_ORDER: bay i is `ClassicVehicleDefs.Order[i]`'s, 0-3 the four M2 vehicles, 4 the dozer's (empty until M7). `SpawnAtJs(def, bay.x, bay.z, bay.yaw)` takes them as they are | the canon's `placements.json` parking (5) | `places.json` parking (5, 5.6 m apart in front of the garage, facing it) |
| `Pois`, `TryGetPoi(id)` | the places, `world.json` pois form, JS space | town, depot, farm, quarry, mill, harbor, fuel (no "garage") | garage, mill, fuel, landing, farm, town, quarry, depot, bog |
| `IsSet`, `Changed`, `TryGrid(out grid)` | see the lifecycle | | |

**Lifecycle.** Empty at start (statics reset on `SubsystemRegistration`). The scene's world root calls `Set` once
everything is built (`MillbrookWorld` at the end of its `Start`, just before `Ready`; `TownWorld` likewise), then
`Changed` is raised. When that world's grid is unloaded (`CanonRuntime.Unload`, `WorldDataRuntime.Unload`: the root's
`OnDestroy`, a scene change, quitting), `Clear(grid)` empties it and raises `Changed`; a `Clear` with a grid that is not
the active one does nothing. So: wait for `IsSet` (or `Changed`), take what you need, and drop your references on the
`Changed` that clears or replaces the world. The grids, arrays and sets belong to the world root; never dispose them.
`TryGrid` falls back to the canon when nothing is set and the canon is loaded (the M2 paths keep working). Any other
scene with its own grid (your test track) can call `Set` as well.

**Today**: `Ground`, `FreeFlyCamera` and `VehicleCameraRig` read it (your T3.5 note about the camera's ground on other
grids is fixed). `VehicleSpawner` (scene name `CanonTestWorld`, `CanonRuntime.Asset.placements.parking`) and
`VehicleSimSystem.Bind` are unchanged: no V edits from W. `ActiveWorldScenes.ServesBothWorlds` (PlayMode) loads both
scenes and checks every row above, five bays on land in each.

**Not in the contract yet**: the bridge decks (`WorldDataAsset.decks`, from `structures.json` with `overRiver`) for
dropping the ground's collision under the deck. Say if T3.6 wants them on `ActiveWorld`.

## For R

- `m3.shots.junctions`: the scene's hooks are `TownWorld.SetHour`, `FreezeClock`, `GoTo(id)`, `TryBookmark`,
  `SettleTerrain`, and `TownScenarioUtil.Load` / `Script` in `Testing/Runtime/Scenarios/World/MillbrookScenarios.cs`
  (internal to `Backroads.Testing`, so usable from `Scenarios/Roads/`). Notes folder: `lane-W-T3.3-scene-for-R-V.md`.
- `RoadPropPlacer` saves a new material per prop into the scene (547 inline materials, a 4.7 MB scene file); a shared
  material per prop kind would make the scene small and the props batch.

## For A

- 245 of your 247 models are placed; the other two (`way/436180778`, `way/436180779`) lie outside the play square
  (`inPlay: false` in TownData) and are not in the world. Grey boxes remain for 16 shops, 2 accessory buildings, the
  fuel canopy, the school and the car-repair garage (21).

## Where this differs from the card

- **Grass tufts** are not placed: `art\` has no tuft model yet (the eight tree species are the canon's).
- **Vehicles at the new garage** land with lane V's T3.6 (step 6, For V); the scene is free-fly until then.
- **Site meshes** (S4) are not in yet; the pads under the buildings are W's (T3.2), and the places have no site-plan
  pad (S3's plans are on lane S's branch).
- **The minimap texture** is in the data and the asset; there is no minimap UI in M3.
- **Road props** are lane R's own stand-ins where the Synty packs are not imported.
- **Places** are found by rules from the data (above), not hand-placed; lane G replaces them in M9 (W7).
- **Twelve views**: the card names eight places and asks for 12 cameras; the other four are the garage, the farm, the
  depot and the river from the hill.
- **Seen in the shots, not fixed here** (look work, M5 or later):
  - The **world's edge** shows from the overview: the terrain stops at the square's border, with fog behind it (the
    canon was an island in the sea). A skirt or far terrain around the square would hide it.
  - **Fog** is the canon's, tuned for a 1 km island: the far half of the 2 km square is hazy at noon.
  - The **water's normal map** tiles in visible stripes on the wide upper reach (the canon's texture and scale).
  - The **farm** and **river** views have a plain brown field in front (the crop rows do not read from there), and
    the **depot** view shows trees and meadow: the depot has no building until S's site plan lands.
  - The **asphalt** reads almost black at noon (lane R's material under M1's light).

## Next

T3.3 is lane W's last M3 card (T3.7, the integration, is lane I's). Open: lane V's spawner on `ActiveWorld` (T3.6);
grass tufts when `art\` has a model; S4's site meshes into `TownWorld` when they land; the look items above.

## Resume

```
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldBakeCli.Export          (re-bake WorldData)
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldDataImporter.Run         (import into the asset)
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.MillbrookTownSceneBuilder.Build   (rewrite Millbrook.unity)
tools\ci\unity-tests.ps1 -Platform EditMode -Category "World;Deform;Render"
tools\ci\unity-tests.ps1 -Platform PlayMode -Category World
tools\ci\build-player.ps1 -Profile Dev -Milestone M3
tools\ci\autotest.ps1 -Scenario m3.shots.millbrook -Profile Dev -Milestone M3
tools\ci\autotest.ps1 -Scenario m3.perf.flythrough -Quality High -Profile Dev -Milestone M3
```

## Merge

**Final**: `$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'W,I'; tools\ci\gate.ps1 -Tier merge -KeepGoing` on
`ee80888` (main `eb86272` merged in; lane I is in the list only for the labelled commit `6336827`): **pass, 8 of 8
steps** (2026-10-22, run `artifacts\20261022-1416\gate-merge`): oracle.hash; vendor.check (584 files); native.ctest;
unity.editmode 274 passed, 1 skipped (ServerContents: no server build), 0 failed, `Repo.OwnersRespected` 56 changed
paths for lanes W,I, `Build.ProjectSettings("build.ScenesMatchBuildCli")`, `Town.NoRealNames`,
`Repo.NoCrInHashedFiles`, `World.BakeDeterministic`, `World.ManifestHashes` and `World.ImportMatchesData` (with the five
bays) pass; unity.playmode 22 of 22 (with `Look.NoErrorShaders` and `ActiveWorldScenes.ServesBothWorlds`);
build.servercompile; build.dev (482.9 MiB); autotest.smoke. Unity's temp files went to `artifacts\tmp` on D:.

The shots and the perf flythrough above ran on `9e6edb6`; the commits after it change the garage bays, `ActiveWorld`'s
names and the build scene list, nothing that draws.

Main has moved on to `91b0690` (sites-S3) since `eb86272`; it touches none of this branch's paths and merges cleanly
(`git merge-tree`), so it was not merged in again.

Before the approval: the same gate with `BR_OWNERS_LANE = 'W'` on `9e6edb6` passed 7 of 8 (run
`20261022-1401`), failing only `build.ScenesMatchBuildCli`, which `6336827` fixes.

The native step's `EngineSimPlugin.dll` was restored afterwards (lane V's, committed only at milestone tags).
