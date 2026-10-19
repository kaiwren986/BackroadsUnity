# M1 handoff: lane W, T1.1-T1.5 (Millbrook in Unity)

Branch `m1/w-world` in `D:\Backroads-Unity-wt\w`. Before the first commit it was fast-forwarded to `main` at `a848bc1`
so the integrator's QualityPreset (`-br-quality`) is used. Nothing was pushed, installed or downloaded. The oracle was
read only through `git -c core.autocrlf=false archive`, and `CanonData/v1` is untouched.

## Done

| Card | What | Where |
|---|---|---|
| T1.1 | **`CanonImporter`.** Entry points: menu *Backroads > Import Canon v1*, *Backroads > Verify Canon v1 hashes*, `ImportCli.Run` for `-executeMethod`, `EnsureImported()`, and an `IPreprocessBuildWithReport` that verifies (or imports) canon before every player build. It checks every SHA-256 of `CanonData/v1/manifest.json` and `CanonData/refs/models/manifest.json` and aborts before anything is written. Output goes to `Assets/Backroads/World/Canon/v1/` (gitignored, path-derived GUIDs, cached by manifest hash + importer version): 7 layers as `.bytes`, `WorldInfo` (+ the 12 reference cameras), `PlacementSet` (buildings, typed records, lamps, rings, footprints, boats, colliders, op types), `VegetationSet`, `SurfaceTable`, `LegacyTrafficNetwork`, `CanonModelSet` x2 (glTFast import of the frozen GLBs), `StaticPropsSet` (64 m chunks), 4 textures, and `Resources/BrCanonV1.asset`, so every build, the server included, carries canon. Import 4.3 s; a cached import re-verifies in 1.1 s. | `Game/Assets/Backroads/Canon/Editor/`, `Game/Assets/Backroads/World/Runtime/Canon/`, `CanonData/refs/models/` |
| T1.2 | **World runtime.** `CanonRuntime`: loads from Resources and copies the TextAsset.GetData views once; `LoadAndVerify` serves the server canary. `WorldGrid`: PLAN 2.6 layers, `DeltaUm` zeroed, `H = H0` until deformed, `RebuildH` as a FloatMode.Strict Burst job. Burst `WorldGridQueries` (+ `HeightQueryJob`, `RayQueryJob`). Double-precision `WorldGridQueriesRef`: a line-by-line mirror of world.js heightAt/normalAt/surfaceAt/rayTerrain, including V8's `Math.hypot` and JS `Math.round`; it works in JS space and flips at the API. `ColliderGrid`: 16 m cells, CSR NativeArrays. `CanonTransforms`: the one JS-to-Unity conversion for buildings, trees, rocks, typed records and cameras. | `Game/Assets/Backroads/World/Runtime/` |
| T1.3 | **Terrain.** `TerrainChunkBuildJob` (Burst, Mesh.MeshDataArray) ports terrain.js writeVertex/colorAt/surfColor/writeKind: PAL linear, skirts 1.2 + 0.7*step, x, normal-x and winding flipped. `TerrainChunkSystem`: 256 chunks, LOD 1/2/4/8 at 110/250/470 m x lodScale, +-10 m hysteresis, nearest-first builds, +-1 LOD cached; only LOD 0-1 cast shadows. `SG_Terrain_V0` is a hand-written URP HLSL port of the terrain.js fragment, with detail noise at `float2(-x, z)`. | `.../Client.Rendering/Runtime/Terrain/`, `.../Client.Rendering/Shaders/SG_Terrain_V0.shader` |
| T1.4 | **The scene and its builders.** `Millbrook.unity` (thin bootstrap written by `MillbrookSceneBuilder`) and `MillbrookWorld` (composition root, QualityPreset-driven LODs). `BuildingSpawner`: 72 buildings, 222 slot materials, 733 panes, 401 lit. `VegetationRenderer`: 5,265 LODGroup trees plus 358 rocks; the tint is passed in `Renderer.SetShaderUserValue`. `PropSpawner`: 46 static-prop chunks. `RoadRibbons` with the `Backroads/Road` marking shader (21 paved roads), `WaterSurface`, and `DayNightPresenter` (details below). `FreeFlyCamera`: WASD, hold right mouse to look, Q/E, Shift, wheel. `CameraBookmarks`: 1-0, -, = for the 12 views, P / Shift+P for places, T for 10:00/21:00, [ ] for hours, H for help. The materials and `MillbrookLook.asset` are in `Client.Rendering/Materials/`. | `.../Client/`, `.../Client.Rendering/`, `.../Scenes/Millbrook.unity` |
| T1.5 | **Autotests.** `m1.shots.canon` (kind `shots-canon`) and `m1.perf.flythrough` (kind `perf-flythrough`). | `.../Testing/Runtime/Scenarios/World/`, `Game/Assets/StreamingAssets/Autotest/m1.{shots.canon,perf.flythrough}.json` |
| Docs | This handoff; `docs/adr/ADR-001-canondata-v1.md` (draft for T1.9). | `docs/` |

`DayNightPresenter` covers:
- the sky.js sun and moon;
- the ambient, as an explicit SH probe;
- exp2 fog;
- the gradient sky with stars;
- analytic sky reflections;
- night emission;
- lamp lights and light pools.

## Gates (PLAN 6.1 M1), expected vs measured

| Gate | Kind | Expected | Measured |
|---|---|---|---|
| `Canon.ManifestHashes` | headline | every manifest SHA-256 matches; 7 layer hashes = 03 §3.2 | **pass**: 54 + 2 files verified, 7 layers equal, imported `.bytes` equal. Also `Canon.MismatchAbortsImport` (one flipped byte aborts before writing) and `Canon.PathDerivedGuids` |
| `Canon.Counts` | headline | M0 counts | **pass**: trees by species, colliders by kind, 78 lamps / 77 posts, 59 lots, 72 placements (59 lot + 13 site), footprints, 7 rings, 5 bays, 2 boats, 24 roads / 1,722 points, traffic 21/53/108/5 (1 car-only), rngCalls 1220, bucket vertices, pane counts |
| `World.HeightProbes` | headline | 1,000 probes: height within 1e-5 m, surface exact, normals within 1e-4 | **pass**: max height error **0 m** (bit-exact), max normal error 2.8e-17, surface exact |
| `World.RayHits` | headline | 12 x 4,096 rays within 1 mm; at most 0.1 % grazing outliers | **pass**: **49,152 / 49,152 bit-identical**, 0 outliers (1.25 s) |
| `World.PlacementsRoundTrip` | headline | buildings, trees, typed records within 1e-4 m and 0.01 deg | **pass**: 72 + 5,265 + 155; max 1.5e-5 m, 4.4e-5 deg; building fronts flip correctly |
| `Render.TerrainLod0BitExact` | headline | all 256 LOD0 chunks bit-exact | **pass**: 4,260,096 grid vertices, 0 differ (skirts, normals, winding checked too) |
| `Render.PaneNamesResolve` | headline | all 11 models; House_B 22 panes | **pass**: 11 models; every placement's pane list equals its model's; lit renderers = 401 lit flags |
| `Render.StaticPropsBuckets` | headline | 7 bucket vertex counts and hashes | **pass**: 7 counts and position SHA-256s equal; chunks + building vertices = bucket; position sums agree; removed building vertices = buildings.glb share in all 7 buckets |
| `m1.shots.canon` | headline | 24 shots + contact sheet next to the JS shots; image difference per shot as a metric | **pass**: 24 shots at 1600x900 in 11 s. Mean SSIM **0.869**, mean MAD **0.0315**. Day: SSIM 0.905-0.958 (mean 0.939), MAD 0.027-0.039. Night: SSIM 0.708-0.877 (mean 0.799), MAD 0.020-0.057. Contact sheet `artifacts\autotest\m1.shots.canon\contact_sheet.png` (JS left, Unity right) |
| All chunks built at load | lane | <= 1,500 ms | **pass**: EditMode worst of 12 views 23 ms (all 256 at LOD0: 100-115 ms); player 12-31 ms |
| `m1.perf.flythrough` p95 at 2560x1440 High | lane | <= 16.6 ms | **borderline**. Gate config (`-Repeat 3`): 18.66 / 17.55 / 17.47 ms, median **17.55 ms** (fail by 1 ms). Other single runs: 15.66, 19.65, 19.70 ms; noise about +-2 ms on this laptop. p50 12-16 ms, main thread p50 1.8 ms (GPU-bound). The display (NVIDIA output 1920x1080) cannot hold a 2560x1440 window, so the flight renders off screen at 2560x1440. At 1920x1080 on screen: p95 9.33 ms |
| `m1.perf.flythrough` min-spec proxy, Low | lane | p95 <= 33 ms | **pass**: p95 33.43 ms at the 30 fps cap (gate = cap frame time + 0.5 ms); GPU p95 5.35 ms, main thread p95 11.9 ms on 6 E-cores, render thread p95 2.2 ms |
| Server canary loads canon | lane (T1.2) | `CANON v1 OK H=a8908d7f…` | **pass** in the ServerHeadless player: `CANON v1 OK H=a8908d7f740ec10b... manifest=a112c66d5c51 trees=5265 colliders=4013` after `SERVER BOOT OK`; `soak.ps1 -Clients 0` pass |
| `gate.ps1 -Tier merge` | lane | green, <= 20 min | **7/8 steps green in 140 s**. The 8th, `unity.editmode`, has 78/79 tests passing; the one failure is `Repo.OwnersRespected`, which flags exactly the 4 REQUEST FOR I files below |
| Dev build opens Millbrook; both autotests run | lane | yes | **yes**. `builds\M1-dev` (219.5 MiB) boots into Millbrook, ready in 0.86-1.85 s. `smoke`, `m1.shots.canon` and `m1.perf.flythrough` (normal and `-MinSpec`) all pass |

Full EditMode results for the lane-W categories: Canon 4/4, World 9/9, Render 4/4.

## Deviations and decisions

1. **Frozen models in `CanonData/refs/models/`.** `buildings.glb` and `trees.glb` are not part of the frozen CanonData v1.
   They are byte copies of the oracle blobs (`e547a79…`, `8b6a0e0…`) with their own manifest. PLAN 1.2 allows
   `CanonData/refs/<name>/` "so v1 never changes". The importer verifies them like v1.
2. **Buildings are taken out of the merged props.**
   - props.js merged all 72 GLB buildings into the same 7 buckets as the other props: 271,662 of 321,894 vertices.
   - Rendering both would draw every building twice. The importer therefore leaves out the triangles inside each placed
     building's bounds (+2 cm).
   - `Render.StaticPropsBuckets` proves that exactly the models' share was removed, in all 7 buckets.
3. **Hand-written HLSL instead of Shader Graph** (`SG_Terrain_V0`, `Backroads/Lit Color`, `Road`, `Water`, `Sky`,
   `Light Pool`). Shader Graph files cannot reasonably be authored or reviewed headless. These shaders go through
   URP's own PBR path, with shadows, Forward+, fog and DOTS instancing.
4. **Tree tint without property blocks.** `Renderer.SetShaderUserValue` (Unity 6.3) carries the packed tint, since PLAN
   1.5 rule 4 bans property blocks. Rocks use the same channel in a grey mode.
5. **Ambient and reflections.**
   - The trilight colours alone did not update the ambient probe at run time, so the ambient is an SH probe solved from
     three colours.
   - Reflections come from the analytic sky (`BrSkyColor`), not from probes, the way three reflects its sky PMREM. A
     realtime probe left a stale day reflection at night.
6. **Shadow distance.**
   - JS shadows cover +-75 m around the view's focus, while URP measures from the camera.
   - Without `-br-quality`, MillbrookWorld raises the project's 50 m to the tier distance (High: 400 m). It restores the
     old value on destroy. The shots use 250 m (Mid).
   - Terrain LOD 2-3 chunks never cast shadows.
7. **The shots use the JS default "Medium" quality**: lodScale 1, trees near 140 m, 6 lamp lights, and a frozen clock, as
   the export did. Normal play follows QualityPreset (default High).
8. **Tonemapping compensation.** Three's ACES fit crushes dark tones and keeps emissive hues; URP's ACES does neither.
   To compensate, night moon (x0.6) and ambient are dimmed and emission is scaled by 0.6 (`MillbrookLook`). Night
   SSIM went from 0.38-0.72 to 0.71-0.88.
9. **Not in M1** (visible on the contact sheet): grass tufts (M5), the two boats, parked vehicles and delivery-ring
   markers (gameplay), and three's exact tonemapper.
10. **The perf flythrough renders off screen** when the requested window (`-screen-width/-height`) does not fit the display.
11. **STP guard.** If STP upscaling is on but its compute shaders are missing from the player, MillbrookWorld falls back
    to bilinear with a warning. See For I item 5.

## For I

The first four items are committed on this branch as **`5cbefca REQUEST FOR I: …`**. `Repo.OwnersRespected` lists
exactly these four files.

1. **`Game/Assets/Backroads/Build/Editor/BuildCli.cs`:**
   - `Scenes` = `Assets/Backroads/Scenes/Millbrook.unity` first (when it exists), then `TestYard.unity`. Lane V adds
     `EngineBench.unity`.
   - Before `BuildPipeline.BuildPlayer`, call `Backroads.Canon.Editor.CanonImporter.EnsureImported(out msg)`. On false,
     fail the build with `msg`.
   - Set `build.json` `canon` = `"v1 <manifest sha12> H=<heights sha8>"`.
2. **`Game/Assets/Backroads/Build/Editor/Backroads.Build.Editor.asmdef`:** add `Backroads.World` and
   `Backroads.Canon.Editor`.
3. **`Game/Assets/Backroads/Server/Runtime/ServerMain.cs`**, after `SERVER BOOT OK`:
   ```csharp
   if (Backroads.World.CanonRuntime.LoadAndVerify(out var canonLine)) Log.Info(canonLine);
   else Log.Error(canonLine);
   System.Console.Out.WriteLine(canonLine);
   ```
   Also add `Backroads.World` to `Backroads.Server.asmdef`. `soak.ps1 -Clients 0` can then require `CANON v1 OK` too
   (verified: it is logged).
4. **`build-player.ps1` needs no change**, because BuildCli imports canon itself. Two further suggestions:
   - `worktree-new.ps1` can run `tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run` once after
     the Library import (PLAN 1.10 lists "canon import" in the worktree step).
   - `gates.json` could add `autotest.m1.shots.canon` to the M1 nightly (today it is milestone only).
5. **STP is stripped from players.**
   - QualityApply sets `upscalingFilter = STP` for Low, but no pipeline asset uses STP at build time. URP therefore
     strips STP's compute shaders, and every post-processed frame throws `NullReferenceException` in `STP.Execute`.
     The first `-MinSpec` run logged 1,547 exceptions.
   - Fix: enable STP on `Mobile_RPAsset` in ProjectSetup (so the resources ship), or drop STP from the Low tier.
   - MillbrookWorld now detects the missing resources and falls back to bilinear, so lane W's runs pass meanwhile.
6. **ServerHeadless uses the client scene list.** Millbrook's client components are compiled out of the server, so it
   logs 8 "missing script" warnings. A server-only scene list (TestYard or an empty boot scene) would avoid that.
7. **`gate.ps1 -Tier merge` rewrites `Game/Assets/Plugins/x86_64/EngineSimPlugin.version.json`** (native.ctest
   installs a fresh build time). Lanes must not commit it; I restored it.
8. **`-br-quality` is read through `QualityPreset`:** TerrainLodScale, ChunkBuildsPerFrame, TreeNearRadius,
   LampLights, and ShadowDistance when the flag is not given.
9. **Perf gate fallback** (the lane gate is borderline, median 17.55 ms against 16.6 ms). Measured levers, each about
   1-3 ms at 2560x1440, within the +-2 ms noise:
   - render scale 0.5: p95 12.0 ms;
   - shadow map 2048 instead of 4096 at High;
   - trees near 140 m instead of 190 m.

   Suggested: decide at the M1 gate with `-Repeat 3` on mains power, a cool machine and a 2560x1440-capable display.

## Next (M2, lane W)

- Deformation on `DeltaUm`: integer µm kernels, deform parity from `reference/deform/calls.json`, dirty rects into
  `TerrainChunkSystem` partial updates (`SetVertexBufferData`).
- Chase and hood cameras, input bindings, menus (M2 card).
- Look:
  - a perf pass on the GPU at 2560x1440 (shadow cascades, tree LOD0 count);
  - three's ACES fit as a custom tonemap if F10 says the night looks different;
  - boats;
  - grass (M5).

## Resume

```powershell
git -C D:\Backroads-Unity-wt\w log --oneline -10
cd D:\Backroads-Unity-wt\w
tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run                 # import or verify canon
tools\ci\unity-tests.ps1 -Platform EditMode -Category "Canon;World;Render"               # lane W tests (17)
tools\ci\project-unity.ps1 -Method Backroads.Client.Editor.MillbrookSceneBuilder.Build   # re-write materials + scene
tools\ci\build-player.ps1 -Profile Dev -Milestone M1
tools\ci\autotest.ps1 -Scenario m1.shots.canon -Profile Dev -Milestone M1
tools\ci\autotest.ps1 -Scenario m1.perf.flythrough -Repeat 3 -Profile Dev -Milestone M1
tools\ci\autotest.ps1 -Scenario m1.perf.flythrough -MinSpec -Profile Dev -Milestone M1
tools\ci\build-player.ps1 -Profile ServerHeadless -Milestone M1; tools\ci\soak.ps1 -Clients 0 -Milestone M1
tools\ci\gate.ps1 -Tier merge -KeepGoing
```
