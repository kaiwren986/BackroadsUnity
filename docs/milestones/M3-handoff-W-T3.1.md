# M3 handoff: lane W, card T3.1 (world size from the manifest; the old Millbrook becomes the test world)

Branch `m3/w-world-size` in `D:\Backroads-Unity-wt\w`, cut from `main` at `bbe542f`. Commits: `4226bac`
(`WorldGridLayout`), `f853074` (the scene rename, lane W's side), `ff2fd25` (this handoff, first version), `184207d`
(**T3.1 for I and V**, the rename's edits outside lane W, approved by the coordinator) and this update. Nothing was
pushed, installed, downloaded or signed into.

## In plain words

The world's size is no longer built into the code. It now comes from the world's own manifest: the old test town
stays 1 km square (2,049 ground points a side, every 0.5 m), and the new real town will be 2 km square (4,097 a side).
Everything that reads the ground uses the size it is given: heights and ray casts, the terrain meshes (16 x 16 chunks
on the old town, 32 x 32 on the new one), ruts and digging, the static collision grid and the trees that follow the
ground. On the old town every number is the same as before, bit for bit (the probes, the camera rays, the deformation
references, the terrain vertices), and the ground queries cost what they did in M2 (0.1 % and 2.3 % slower, budget
5 %). A made-up 2 km world (flat, with a ramp) passes the probe, ray, terrain, collision and digging checks at both
corners.

The old Millbrook scene is renamed `CanonTestWorld.unity` (the same scene, the same GUID); the name Millbrook goes to
the new town in T3.3. The rename needed three edits outside lane W (the build's scene list, the editor's build
settings and the vehicle spawner's scene name); the coordinator approved them as one separate commit, `184207d`, for
the integrator to review at merge (listed under For I and For V).

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **`WorldGridLayout`** from the world's manifest (`"grid"`: `VN`, `cell`, `half`, `tintN`, `tintCell`; `N` and `size` checked): v1 2,049 / 0.5 / 512 / 129, Millbrook 4,097 / 0.5 / 1,024 / 257 (`WorldGridLayout.CanonV1`, `.Millbrook`, `.FromManifestJson`, `.FromManifestFile`, `.Create` with checks: cell a power of two, whole 64 m chunks, tint grid spanning the square). Blittable; it rides in `WorldGridView.Layout`, so every Burst job gets it as a field. `WorldGrid.Layout`; `FromLayers` / `FromBytes` take the layout; `FromCanon` takes the canon's (`WorldGrid.LayoutOf`); `WorldGrid.FromFunctions` builds synthetic grids (tests, lane V's test track). No compile-time world size is left in lane W's code | `World/Runtime/Grid/WorldGridLayout.cs`, `WorldGrid.cs` |
| 1 | **Queries**: `WorldGridQueries` (Burst float path) and `WorldGridQueriesRef` (double, JS arithmetic) read the view's layout; `(x + Half) * InvCell` equals world.js's `(x + HALF) / CELL` bit for bit because the cell is a power of two. `NearestKJs(layout, x, z)`, `InBoundsJs(view, x, z)` | `WorldGridQueries.cs`, `WorldGridQueriesRef.cs` |
| 1 | **`ColliderGrid`**: 16 m cells, `CellsPerSide` from the layout (64 / 128), `Build(colliders, layout)` | `ColliderGrid.cs`, `CanonRuntime.cs` |
| 1 | **Deformation**: `DeformLayers` carry `N`, `VN`, `CellShift`, `OriginQ20`; the integer kernels read them; `TileTable` has N / 32 tiles per side (64 / 128); `JsDeformReference` takes a layout (v1 by default) | `Deform/Runtime/StampKernels.cs`, `DeformSystem.cs`, `JsDeformReference.cs` |
| 1 | **Terrain chunk count follows the layout**: `TerrainChunkSystem.ChunksPerSide` / `ChunkCount` per grid (16 / 32 per side), chunk origins at `cx * 64 - Half`; `TerrainVertexWriter` places vertices at `i * Cell - Half` and reads `TintN` / `TintStep` from the layout; trees re-snap on cells from the canon's layout | `Client.Rendering/Runtime/Terrain/*`, `VegetationRenderer.cs`, `MillbrookWorld.cs` (log line) |
| 2 | **`Millbrook.unity` → `CanonTestWorld.unity`** (git rename with its `.meta`, same GUID; still loads CanonData v1). `MillbrookSceneBuilder` writes it (menu *Backroads > Build canon test world scene*); the Esc menu lists *Canon test world* when the scene is in the build (*Drive in Millbrook* returns with T3.3); `WorldScenarioUtil` names the new file. The class names (`MillbrookWorld`, `MillbrookLook`, `MillbrookSceneBuilder`) stay: lanes R, V and I reference them | `Scenes/CanonTestWorld.unity`, `Client/Editor/MillbrookSceneBuilder.cs`, `Client.UI/Runtime/PauseMenu.cs`, `Testing/Runtime/Scenarios/World/WorldScenarioUtil.cs` |
| 3 | **v1 bit-identical**: see the measurements | |

New tests: `GridLayout` (EditMode World: `LayoutFromManifest`, `MillbrookLayoutAndChecks`, `LargeGridProbes`,
`LargeGridRays`, `LargeGridTerrainCorners`, `LargeGridColliders`, `StepCostMatchesM2`) and `DeformLargeGrid.CornersSmoke`
(EditMode Deform).

## Done when, expected vs measured

| Item | Measured |
|---|---|
| Every EditMode, PlayMode and M2 autotest passes unchanged on `CanonTestWorld` | EditMode and PlayMode: see Merge below. Canon parity unchanged: `World.HeightProbes` max \|dh\| 0, `World.RayHits` 49,152 of 49,152 bit-identical, `Deform.MatchesJs` mirror bit-exact on 10,000 calls (max error 0.0191 mm, as before), `Deform.Deterministic` 0 tiles differing, `Render.TerrainLod0BitExact` 4,260,096 vertices, 0 differ, `World.ServerCanaryLine` `H=a8908d7f`. M2 autotests on the Dev build: **pass** except one that fails the same way before this card: `m2.drive.route9` (default and `-Fps 30`), `m2.vehicles.drive` (the four vehicles spawn on the garage bays of `CanonTestWorld`), `m2.hud.stub`, `m2.ui.screens`, `m2.bench.v8 -Fps 60`, `m1.shots.canon`, `m1.ui.credits` all pass on the renamed build (`f853074` with the edits of `184207d`). `m2.engine.follow -Fps 60` fails at Ultra (RTF 1.75-1.82 < 2), as it does on lane R's build of `3901af2` from before T3.1 (RTF 1.64-2.00) and on `4226bac` (1.77-1.86): see For V |
| A synthetic 4,097² layout (flat, with a ramp) passes `WorldGrid` probe tests and a deformation smoke test at both corners | **pass**. `LargeGridProbes`: 4,013 probes (the four corners exactly, just inside them, the kink, 4,000 seeded) heights within 1.9e-6 m (reference) and 4.4e-5 m (float) of the function, normals within 3.4e-6, surfaces exact, nearest-vertex and in-bounds clamps right at the corners. `LargeGridRays`: 615 rays (down everywhere, slanted onto the flat at the (-, -) corner and the ramp at the (+, +) corner, upward misses) within 1.95 mm (the march's resolution). `LargeGridTerrainCorners`: 32 x 32 chunks, corner chunks (0, 0), (31, 31), (31, 0) bit-exact at `i*0.5 - 1024`. `LargeGridColliders`: 128 x 128 cells, records at both corners found. `DeformLargeGrid.CornersSmoke`: 36 stamps (ruts, brushes, relax) at both corners and on the last cells before the edges: 120 vertices changed, all within 6 m of the stamps, max 0.002 mm from the JS mirror on the same layout, dirty rectangles and tile versions at the corners only (tile (64, 64) untouched), Burst and managed give the same integers |
| Perf: the step cost of `heightAt` / `rayTerrain` within 5 % of M2 | **pass** (`StepCostMatchesM2`: Burst, one thread, interleaved with a verbatim copy of M2's compile-time code, best of 11): `heightAt` 16.42 ns vs 16.40 ns (1.001), `rayTerrain` 5.72 µs vs 5.59 µs per ray (1.023) over the 49,152 reference-camera rays; both give M2's numbers bit for bit (1,000,000 heights, 49,152 rays). Written to `artifacts\world\grid-step-cost.json` |

## For I (the coordinator)

**In this branch, commit `184207d` "T3.1 for I and V (reviewed by the integrator at merge)"**, approved by the
coordinator; only the rename, nothing else outside lane W. Files:

- `Game/Assets/Backroads/Build/Editor/BuildCli.cs` (I): `CanonTestWorldScenePath` (`Scenes/CanonTestWorld.unity`)
  replaces the old `MillbrookScenePath` as the first scene. `MillbrookScenePath` now names the new town's scene (T3.3)
  and is listed when its file exists (after the test world, so Dev builds and the M2 autotests still start in the canon
  test world). `ScenesFor(profile)`: Client builds leave the canon test world out once the new Millbrook scene exists
  (card step 2, Dev-only from M3 on; until T3.3 it is the only drivable world, so Client builds keep it until then).
  `BuildProfile` builds `ScenesFor(profile)`; the editor's build settings keep `Scenes` (`build.ScenesMatchBuildCli`).
- `Game/ProjectSettings/EditorBuildSettings.asset` (I): the first scene's path, same GUID.

No other I-owned file names the old scene file: `tools/ci/gates.json` mentions only the new town, and PLAN, ADR-006
and the M3 cards describe the rename itself (historical handoffs left as they are).

Also for I:

- With T3.3 the M2 autotests and `WorldScenarioUtil` must keep finding the canon test world when the new Millbrook is in
  the build too (today: "the first scene of the build"); I will raise it with T3.3.
- `tools\ci\worktree-new.ps1` lists `tools\ci\canon-import.ps1` as a next step, but that script does not exist. The
  canon imports itself on the first test run (`CanonImporter.EnsureImported`), or
  `tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run`.
- `Core/Runtime/BrCoords.cs` still has `VN = 2049` and `HALF = 512f` (unused); they could go or be marked v1 only.

## For V

- **In commit `184207d`**: `Game/Assets/Backroads/Vehicles/Client/VehicleSpawner.cs` `SceneName` `"Millbrook"` →
  `"CanonTestWorld"` (and its summary). The four M2 vehicles spawn on the canon test world's garage bays as before
  (`m2.vehicles.drive`, `m2.drive.route9` pass).
- `Vehicles/Runtime/BrvSim.cs` still assumes v1: `HALF = 512.0` (the world-edge clamp `HALF - 6`) and `CGN = 64` with
  `HALF` in the collider cell lookup. On a 4,097² world read `world.grid.Layout.Half` and the collider grid's
  `CellsPerSide` (`BrvWorld` has both). `Tests/PlayMode/Vehicles/JsRutSink.cs` uses `WorldGrid.N` / `WorldGrid.VN`
  (now `[Obsolete]`; use `WorldGridLayout.CanonV1` or the grid's layout). `WorldGrid.FromFunctions(layout, height,
  surface)` builds a synthetic world for `BrvTestTrack`. API summary: notes folder, `lane-W-T3.1-WorldGridLayout.md`.
- `m2.engine.follow -Fps 60` fails on this machine at Ultra: RTF 1.75-1.86 < 2 on this branch's builds, and the same on
  lane R's build of `3901af2` (before T3.1: RTF 1.64-2.00). Not from this card; worth a look in lane V.

## For R

- `Roads/Editor/RoadGraphMap.cs` (the canon map background) reads `WorldGrid.VN` / `Half` / `Cell`, now `[Obsolete]`
  v1 constants: `WorldGrid.LayoutOf(canon)` or `WorldGridLayout.CanonV1` instead. It compiles and works as before.

## Where this differs from the card

- **`WorldGrid.N`, `VN`, `Count`, `Cell`, `Half`, `TintN` are kept** as `[Obsolete]` v1 constants so the two files of
  lanes R and V that read CanonData v1 directly still compile (two CS0618 warnings there); nothing in lane W uses them.
- **The canon's layout** is read from the imported canon's world info (its `grid` block, imported from `world.json`,
  the same block as `manifest.json`'s; `LayoutFromManifest` checks they agree), so no canon re-import was needed.
- **Not resized**: the canon's static prop chunks (`StaticPropsSet`, 16 x 16, a CanonData v1 format) and the fly
  camera's ±700 m clamp. Both are the old town's; T3.3 brings the new town's own.
- **The rename's edits outside lane W** are one separate commit (`184207d`), approved by the coordinator; the gate
  ran with `BR_OWNERS_LANE='W,I,V'`.
- **Perf** is measured in one process against a verbatim copy of M2's code rather than across two builds, so machine
  load affects both sides equally.

## Next

T3.2 (terrain from the DEM, the river, land surfaces, the plates API, the `WorldData` export) on `m3/w-terrain`, cut
from this branch.

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category "World;Deform;Render"
tools\ci\autotest.ps1 -Scenario m2.drive.route9 -Profile Dev -Milestone M3
```

## Merge

**Final, `184207d`**: `$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'W,I,V'; tools\ci\gate.ps1 -Tier merge`
(lane W paths, plus the I and V files of `184207d`): **pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1131\gate-merge`): oracle.hash; vendor.check (584 files); native.ctest; unity.editmode 213 passed, 1 skipped (ServerContents: no server build), 0 failed, `Repo.OwnersRespected` 31 changed paths checked for lanes W, I, V; unity.playmode 9 of 9; build.servercompile; build.dev (230.6 MiB); autotest.smoke.

Earlier runs, lane W only (`BR_OWNERS_LANE = 'W'`):

- **`4226bac`** (`WorldGridLayout`, the scene not yet renamed): **pass, 8 of 8 steps** (run
  `artifacts\20261022-1054\gate-merge`): unity.editmode 213 passed, 1 skipped, 0 failed; unity.playmode 9 of 9;
  build.dev (231 MiB); autotest.smoke.
- **`f853074`** with the I and V edits applied but not yet committed: **pass, 8 of 8** (`artifacts\20261022-1120\gate-merge`).
  A run before it (`20261022-1111`) failed one PlayMode test, `EngineSim.FollowInVehicle.TracksMappedRpmAtHigh`
  (RTF 1.15 < 2, audio underruns), while another lane's first Library import had about 33 shader compilers running
  (68 % CPU); it passed on the rerun once the machine was quiet. That test does not load a scene.

The native step's `EngineSimPlugin.dll` was restored after each run (lane V's, committed only at milestone tags).
