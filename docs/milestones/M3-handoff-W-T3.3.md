# M3 handoff: lane W, card T3.3 (the new Millbrook scene)

Branch `m3/w-scene` in `D:\Backroads-Unity-wt\w`, cut from `m3/w-terrain` at `ab86ce9` (T3.2, now on main). Commits:
`fe16b56` (code, scene, tests, autotests), `df21056` (`WorldData\millbrook\v1` re-baked at `fe16b56`), `ab179cf`
(main `eb86272` merged in: T3.5 and Roads.Validation, no conflicts), `9f8a95b` (the village street view looks along a
street; dusk shots at 20:00), `9e6edb6` (WorldData re-baked at `9f8a95b`: only `places.json` changes) and this
handoff. Nothing was pushed, installed, downloaded or signed into.

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

The places of the old test world are found again in the new town with their game names: Millbrook Garage (with its four
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
| 4 | **Places** (`PlacesStep`, provisional until lane G's W7): `places.json` with the pois (garage = the car-repair building; mill = the largest non-house building of 300 m² or more by the dam; fuel; landing = the slipway; farm = the largest field; town centre = the commercial centroid; quarry, depot and bog at the scout's spots), the garage's four bays in front of it facing it (`parking`), and the 12 camera views, each the best of 16 directions by line of sight (the street view stands on the carriageway) | `NatureStep.cs` (`PlacesStep`) |
| 5 | **Map**: `map.png` (2,048², 1 m per pixel): surfaces, 10 m and 50 m contours, water, roads, walks, buildings; in the asset as `map` | `WorldDataExtras.Map` |
| 6 | **Vehicles at the new garage**: the data is there (`ActiveWorld.Pois` `garage`, `ActiveWorld.Parking` the four bays, `ActiveWorld.Colliders`); lane V's spawner still binds to the canon (see For V). The free-fly camera covers the whole square (extent = half + 150 m) | `ActiveWorld` |

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

- **`EditorBuildSettings.asset`** (yours): `Millbrook.unity` has to be listed after `CanonTestWorld.unity`, because
  `BuildCli.Scenes` lists it once the file exists; until then `Build.ProjectSettings("build.ScenesMatchBuildCli")`
  fails. The one-line change is not in this branch (no edits outside lane W).
- **Client builds**: `BuildCli.ScenesFor(Client)` drops `CanonTestWorld` once `Millbrook.unity` exists, so a Client
  build starts in the new town, where no vehicle spawns until lane V binds the spawner to `ActiveWorld`. Keep the canon
  test world in Client builds until then, or merge V's binding first.
- `WorldDataBuildHook` (W) imports WorldData before any player build once the scene exists; `BuildCli` could call
  `WorldDataImporter.EnsureImported` next to the canon's, for a clearer error.
- `gates.json`: `autotest.m3.shots.millbrook` and `autotest.m3.perf.flythrough-high` can drop `pending` when this
  lands (the perf one stays report only until M5).
- LFS: `map.png` (0.6 MB) and the two contact sheets (0.8 MB); the new WorldData JSON is plain text
  (`vegetation.json` 1.4 MB). The Dev build grows from 231 to 483 MiB (the imported layers and lane A's models).

## For V

- `ActiveWorld` (`World/Runtime/WorldData/WorldDataRuntime.cs`): `Name`, `Grid`, `Colliders`, `Vegetation`,
  `Parking`, `Pois`, `Changed`, `TryGrid(out grid)` (the canon when only it is loaded). `TownWorld` and
  `MillbrookWorld` set it. Your T3.5 note (camera ground on non-canon grids) is fixed here: `Ground`, `FreeFlyCamera`
  and `VehicleCameraRig` read it.
- To spawn at the new garage: bind `VehicleSpawner` and `VehicleSimSystem` to `ActiveWorld` (grid, colliders, the
  `garage` poi and the four bays in `Parking`). Deck collision (`structures.json` decks, `overRiver`) is in
  `WorldDataAsset.decks` for dropping the ground's collision under the bridge.

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
- **Vehicles at the new garage** need lane V's binding (For V); the scene is free-fly until then.
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

T3.3 is lane W's last M3 card (T3.7, the integration, is lane I's). Open: lane V's spawner on `ActiveWorld`;
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

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'W'; tools\ci\gate.ps1 -Tier merge -KeepGoing` on `9e6edb6` (main
`eb86272` merged in): **7 of 8 steps pass, 1 fails** (2026-10-22, run `artifacts\20261022-1401\gate-merge`):
oracle.hash; vendor.check (584 files); native.ctest; **unity.editmode 273 passed, 1 skipped (ServerContents: no server
build), 1 failed**: `Build.ProjectSettings("build.ScenesMatchBuildCli")`, because `EditorBuildSettings.asset` (lane
I's) does not list `Millbrook.unity` yet (see For I; a ready patch is in the notes folder,
`lane-W-T3.3-EditorBuildSettings-for-I.patch`). `Repo.OwnersRespected` 50 changed paths, all lane W;
`Town.NoRealNames`, `Repo.NoCrInHashedFiles`, `World.BakeDeterministic`, `World.ManifestHashes`,
`World.ImportMatchesData` and `GridLayout.StepCostMatchesM2` pass. unity.playmode 21 of 21 (with
`Look.NoErrorShaders`); build.servercompile; build.dev (482.6 MiB); autotest.smoke. The first run without
`-KeepGoing` (`20261022-1354`) stopped at the same single failure.

Once lane I's line is in (or the coordinator lets W add it in a labelled commit, as in T3.1), the gate should pass 8
of 8: nothing else failed.

The native step's `EngineSimPlugin.dll` was restored afterwards (lane V's, committed only at milestone tags).
