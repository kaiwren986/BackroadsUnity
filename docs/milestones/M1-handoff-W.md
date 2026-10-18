# M1 handoff: lane W, T1.1-T1.5 (Millbrook in Unity)

Branch `m1/w-world` in `D:\Backroads-Unity-wt\w` (fast-forwarded to `main` at `a848bc1` before the first commit, so
the integrator's QualityPreset is used). Nothing was pushed, installed or downloaded; the oracle was read only through
`git -c core.autocrlf=false archive`. `CanonData/v1` is untouched.

Status: **draft, being filled in** (numbers below are measured unless marked "pending").

## Done

| Card | What | Where |
|---|---|---|
| T1.1 | `CanonImporter` (menu *Backroads > Import Canon v1*, *Backroads > Verify Canon v1 hashes*, `ImportCli.Run`, `EnsureImported()`, an `IPreprocessBuildWithReport` that verifies or imports canon before every build). Verifies every SHA-256 of `CanonData/v1/manifest.json` and `CanonData/refs/models/manifest.json` and aborts before writing anything. Writes `Assets/Backroads/World/Canon/v1/` (gitignored, path-derived GUIDs, cached by manifest hash + importer version): 7 layers as `.bytes`; `WorldInfo` (+ 12 reference cameras), `PlacementSet` (buildings, typed records, lamps, rings, footprints, boats, colliders, op types), `VegetationSet`, `SurfaceTable`, `LegacyTrafficNetwork`, `CanonModelSet` x2 (glTFast import of the frozen GLBs), `StaticPropsSet` (64 m chunks), 4 textures, and `Resources/BrCanonV1.asset` so every build (server included) carries canon. Import 4.3 s, verify of a current import 1.1 s. | `Game/Assets/Backroads/Canon/Editor/`, `Game/Assets/Backroads/World/Runtime/Canon/`, `CanonData/refs/models/` |
| T1.2 | `CanonRuntime` (Resources load, TextAsset.GetData copied once, `LoadAndVerify` for the server canary), `WorldGrid` (PLAN 2.6 layers, `DeltaUm` zeroed, `H = H0` until deformed, `RebuildH` FloatMode.Strict Burst job), Burst `WorldGridQueries` (+ `HeightQueryJob`, `RayQueryJob`), double `WorldGridQueriesRef` (world.js heightAt/normalAt/surfaceAt/rayTerrain mirrored, V8's Math.hypot and Math.round included; JS space, flipped at the API), `ColliderGrid` (16 m cells, CSR NativeArrays), `CanonTransforms` (the one JS-to-Unity conversion for buildings, trees, rocks, typed records, cameras). | `Game/Assets/Backroads/World/Runtime/` |
| T1.3 | `TerrainChunkBuildJob` (Burst, Mesh.MeshDataArray; terrain.js writeVertex/colorAt/surfColor/writeKind; PAL linear; skirts 1.2 + 0.7*step; x, normal-x and winding flipped) and `TerrainChunkSystem` (256 chunks, LOD 1/2/4/8 at 110/250/470 m x lodScale, +-10 m hysteresis, nearest-first, +-1 LOD cached). `SG_Terrain_V0`: hand-written URP HLSL port of the terrain.js fragment (detail noise at `float2(-x, z)`). | `Game/Assets/Backroads/Client.Rendering/Runtime/Terrain/`, `.../Shaders/SG_Terrain_V0.shader` |
| T1.4 | `Millbrook.unity` (thin bootstrap from `MillbrookSceneBuilder`), `MillbrookWorld` (composition root), `BuildingSpawner`, `VegetationRenderer` (LODGroup trees + rocks), `PropSpawner` (static_props chunks), `RoadRibbons` + `Backroads/Road` marking shader, `WaterSurface`, `DayNightPresenter` (sun/moon, gradient ambient as an explicit SH probe, exp2 fog, gradient sky with stars, night emission, lamp lights, light pools), `FreeFlyCamera`, `CameraBookmarks`; materials and `MillbrookLook.asset`. | `Game/Assets/Backroads/Client/`, `Game/Assets/Backroads/Client.Rendering/`, `Game/Assets/Backroads/Scenes/Millbrook.unity` |
| T1.5 | Autotests `m1.shots.canon` (kind `shots-canon`) and `m1.perf.flythrough` (kind `perf-flythrough`). | `Game/Assets/Backroads/Testing/Runtime/Scenarios/World/`, `Game/Assets/StreamingAssets/Autotest/m1.{shots.canon,perf.flythrough}.json` |
| Docs | This handoff; `docs/adr/ADR-001-canondata-v1.md` (draft). | `docs/` |

## Gates (PLAN 6.1 M1), expected vs measured

| Gate | Kind | Expected | Measured |
|---|---|---|---|
| `Canon.ManifestHashes` | headline | every manifest SHA-256 matches; 7 layer hashes = 03 §3.2 | pass: 54 + 2 files verified, 7 layers equal, imported `.bytes` equal |
| `Canon.Counts` | headline | M0 counts | pass: all (trees by species, colliders by kind, lamps, lots, 72 placements, footprints, rings, parking, boats, roads/points, traffic, rngCalls, bucket vertices, pane counts) |
| `World.HeightProbes` | headline | 1,000 probes: height within 1e-5 m, surface exact, normals within 1e-4 | pass: max height error **0 m** (bit-exact), max normal error 2.8e-17, surface exact |
| `World.RayHits` | headline | 12 x 4,096 rays within 1 mm; at most 0.1 % grazing outliers | pass: **49,152 / 49,152 bit-identical**, 0 outliers |
| `World.PlacementsRoundTrip` | headline | buildings, trees, typed records within 1e-4 m and 0.01 deg | pass: 72 + 5,265 + 155; max 1.5e-5 m, 4.4e-5 deg |
| `Render.TerrainLod0BitExact` | headline | all 256 LOD0 chunks bit-exact | pass: 4,260,096 grid vertices, 0 differ |
| `Render.PaneNamesResolve` | headline | all 11 models; House_B 22 panes | pass: 11 models, every placement's pane list equals its model's; 733 placed panes, 401 lit |
| `Render.StaticPropsBuckets` | headline | 7 bucket vertex counts and hashes | pass: 7 counts and position SHA-256s equal; chunks + building vertices = bucket; removed building vertices = buildings.glb share exactly |
| `m1.shots.canon` | headline | 24 shots + contact sheet next to the JS shots | pending final run (see below) |
| Terrain: all chunks <= 1.5 s at load | lane | <= 1,500 ms | EditMode: worst of 12 views **23 ms**; all 256 at LOD0 100-115 ms; player: 38 ms |
| `m1.perf.flythrough` p95 at 2560x1440 High | lane | <= 16.6 ms | pending (machine.lock) |
| Server canary loads canon | lane (T1.2) | `CANON v1 OK H=a8908d7f…` | EditMode `World.ServerCanaryLine`: `CANON v1 OK H=a8908d7f740ec10b... manifest=a112c66d5c51 trees=5265 colliders=4013`; ServerMain change is a REQUEST FOR I commit |

## Deviations and decisions

1. **Frozen models in `CanonData/refs/models/`.** `buildings.glb` and `trees.glb` are not in CanonData v1, which is
   frozen. They are byte copies of the oracle blobs with their own manifest (PLAN 1.2 allows `CanonData/refs/<name>/`
   "so v1 never changes"). The importer verifies them like v1.
2. **Buildings are removed from the merged props.** props.js merged all 72 GLB buildings into the same buckets as the
   other props (271,662 of 321,894 vertices). Rendering both would double every building, so the importer leaves out the
   triangles inside each placed building's bounds (+2 cm) and checks that exactly the models' share went (all 7 buckets
   equal). BuildingSpawner draws the buildings from buildings.glb.
3. **Hand-written HLSL instead of Shader Graph** (`SG_Terrain_V0`, `Backroads/Lit Color`, `Road`, `Water`, `Sky`,
   `Light Pool`). Shader Graph files are not practical to author and review headless; the shaders use URP's own PBR
   path (`UniversalFragmentPBR`), shadows, Forward+, fog, DOTS instancing. They sample world patterns at `float2(-x, z)`.
4. **Tree tint without property blocks:** `Renderer.SetShaderUserValue` (Unity 6.3) carries the packed tint
   (PLAN 1.5 rule 4 forbids property blocks); rocks use the same channel in a grey mode.
5. **Ambient** is an explicit SH probe solved from three trilight colours (the trilight colours alone did not update
   the probe at run time). **Reflections** come from the analytic sky in the shaders (`BrSkyColor`), not from probes,
   like three's sky PMREM.
6. **Shadow distance.** JS shadows cover +-75 m around the view's focus; URP's distance is from the camera. Without
   `-br-quality`, MillbrookWorld raises the project's 50 m to the tier's distance (High 400 m) and restores it on
   destroy; the shots use 250 m (Mid).
7. **Shots use the JS default quality** ("Medium": terrain lodScale 1, trees near 140 m, 6 lamp lights) and a frozen
   clock, like the export. Normal play uses QualityPreset (`-br-quality`, default High: lodScale 1.3, trees 190 m,
   QualityPreset lamp count).
8. **Not in M1** (known differences on the contact sheet): grass tufts (M5 GrassSystem), the two boats, parked
   vehicles and delivery-ring markers, three's exact ACES fit (URP's ACES desaturates bright emissives more and lifts
   the darkest tones; night moon and ambient are dimmed to compensate).
9. The importer also keeps the canon `ops` types and footprints so `Canon.Counts` reads everything from the assets.

## For I

Committed on this branch as **`5cbefca REQUEST FOR I: …`** (Repo.OwnersRespected flags these 4 files):

1. `Game/Assets/Backroads/Build/Editor/BuildCli.cs`: `Scenes` = `Assets/Backroads/Scenes/Millbrook.unity` first (when
   it exists), then `TestYard.unity` (V adds `EngineBench.unity`). Before `BuildPipeline.BuildPlayer`:
   `Backroads.Canon.Editor.CanonImporter.EnsureImported(out msg)`; on false fail the build with `msg`. `build.json`
   `canon` = `"v1 <manifest sha12> H=<heights sha8>"`.
2. `Game/Assets/Backroads/Build/Editor/Backroads.Build.Editor.asmdef`: + `Backroads.World`, `Backroads.Canon.Editor`.
3. `Game/Assets/Backroads/Server/Runtime/ServerMain.cs`, after `SERVER BOOT OK`:
   ```csharp
   if (Backroads.World.CanonRuntime.LoadAndVerify(out var canonLine)) Log.Info(canonLine);
   else Log.Error(canonLine);
   System.Console.Out.WriteLine(canonLine);
   ```
   and `Backroads.Server.asmdef`: + `Backroads.World`. `soak.ps1 -Clients 0` can then also require `CANON v1 OK`.
4. `build-player.ps1` needs no change: BuildCli imports canon itself. A fresh worktree may run
   `-executeMethod Backroads.Canon.Editor.ImportCli.Run` once (`tools\ci\project-unity.ps1 -Method
   Backroads.Canon.Editor.ImportCli.Run`), which `worktree-new.ps1` can call after the Library import (PLAN 1.10 lists
   "canon import" in the worktree step).
5. `gates.json` M1: `autotest.m1.shots.canon` is already listed (headline). Suggest a nightly entry for it as well.
6. `.gitignore` already ignores `Game/Assets/Backroads/World/Canon/` (+ `.meta`).
7. `-br-quality`: read through `QualityPreset` (TerrainLodScale, ChunkBuildsPerFrame, TreeNearRadius, LampLights,
   ShadowDistance when not requested).

## Next (M2, lane W)

- Deformation on `DeltaUm` (integer µm kernels, deform parity from `reference/deform/calls.json`), dirty rects to
  `TerrainChunkSystem` partial updates.
- Chase/hood cameras, input bindings, menus (M2 card).
- Look: grass (M5), boats, three's ACES fit as a custom tonemap if the user's F10 says the night looks different.

## Resume

```powershell
git -C D:\Backroads-Unity-wt\w log --oneline -8
cd D:\Backroads-Unity-wt\w
tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run          # import or verify canon
tools\ci\unity-tests.ps1 -Platform EditMode -Category "Canon;World;Render"       # lane W tests
tools\ci\project-unity.ps1 -Method Backroads.Client.Editor.MillbrookSceneBuilder.Build   # re-write materials + scene
tools\ci\build-player.ps1 -Profile Dev -Milestone M1
tools\ci\autotest.ps1 -Scenario m1.shots.canon -Profile Dev -Milestone M1
tools\ci\autotest.ps1 -Scenario m1.perf.flythrough -Profile Dev -Milestone M1
tools\ci\gate.ps1 -Tier merge -OwnersBase main
```
