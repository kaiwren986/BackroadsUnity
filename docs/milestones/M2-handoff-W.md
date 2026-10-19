# M2 handoff: lane W, T2.5-T2.7 (deformation, controls and screens, cameras and HUD)

Branch `m2/w-drive` in `D:\Backroads-Unity-wt\w`, with `main` merged in (lanes R and A). Nothing was pushed, installed
or downloaded. The oracle was read only with `git show 552f7807:<path>`; `CanonData/v1` is untouched.

## Done

| Card | What | Where |
|---|---|---|
| T2.5 | **Integer ground kernels.** `StampKernels`: world.js `rut`, `brush`, `relax` in fixed point on `DeltaUm` (horizontal Q20 m, heights in µm, Q20 falloffs, a Q12-indexed brush falloff table, exact integer `d²` tests, largest-remainder berm split, JS clamps and caps), run as a synchronous Burst job or the same code managed. `cutBlade` and the deposits stay for M7. | `Game/Assets/Backroads/Deform/Runtime/StampKernels.cs` |
| T2.5 | **`RutStamp` (18 B, `Pack = 1`)**, provisional `BrushStamp` (12 B) and `RelaxStamp` (13 B); the stamp is quantised from the JS arguments and the stamp is applied (integer CORDIC for the direction). Draft **ADR-003**. | `Deform/Runtime/Stamps.cs`, `docs/adr/ADR-003-rutstamp.md` |
| T2.5 | **`DeformSystem : IStampSink`**, registered in `StampSinks.Current` when Millbrook loads (`MillbrookWorld`), cleared on unload. `DirtyRectQueue` (`NativeQueue<int4>`), tile versions and xxHash3 tile hashes (`TileTable`). H0 and BASE are rounded once to µm at load (two int32 layers, 33.6 MB). | `Deform/Runtime/DeformSystem.cs`, `Client/Runtime/MillbrookWorld.cs` |
| T2.5 | **Partial chunk updates and tree re-snap.** `TerrainChunkSystem.MarkDirty` / `FlushDirty` rewrite the dirty rows of the current LOD mesh in place (`Mesh.SetVertexBufferData`, `TerrainRegionJob`, skirts on touched edges), drop cached meshes of other LODs, never rebuild a chunk; a shared `TerrainVertexWriter` makes a partial update byte-identical to a rebuild. `VegetationRenderer.Resnap` ports props.js `onTerrainChanged` (16 m cells, mesh 0.2 m under the ground, ColliderGrid tree y range). | `Client.Rendering/Runtime/Terrain/`, `Client.Rendering/Runtime/VegetationRenderer.cs` |
| T2.5 | **JS reference.** `JsDeformReference`: a double-precision mirror of world.js on float32 arrays, proven bit-exact against all 10,000 per-call fnv1a hashes before it is used as the reference. | `Deform/Runtime/JsDeformReference.cs` |
| T2.6 | **`Content/core/bindings.json` v1**: one table for keyboard, mouse and gamepad, maps Driving, OnFoot (M7) and UI; tap/hold pairs (`hold`), `padVia`, `since`. All the browser game's keys plus tap I ignition / hold I starter, Left Shift / Left Ctrl gears, hold V clutch, F4 manual/auto, K diff lock, Esc menu, F3 debug, F10 checklist, F8 fly camera; every driving action has a gamepad route (Start menu, hold Start reset, D-pad up/down gears, D-pad left ignition / hold starter, D-pad right lights / hold diff lock, Y get in / hold winch, Back camera / hold manual, B clutch, L3 low range, R3 zoom). Deadzone 0.14 (a cut, as `input.js`). Draft **ADR-004**. | `Game/Assets/Content/core/bindings.json`, `docs/adr/ADR-004-bindings.md` |
| T2.6 | **Input driver.** `BindingTable` generates the Input System actions at start-up and holds the rules; `ClientInput` turns them into presses, levels, look and zoom and fills `DriveInput` (levels; press counters that only grow) for `PlayerVehicle.Current` every frame. Driving input is off while paused, in the fly camera, or while a UI text field has the keyboard. `Resources/BrClientConfig.asset` carries the table into builds. | `Client/Runtime/Input/`, `Client/Resources/` |
| T2.6 | **`settings.json`** in `BrPaths.DataRoot` (persistentDataPath; automated runs use their own folder), version 1: quality (through `QualityPreset` + `QualityApply`; `-br-quality` wins), master/engine/effects volumes, resolution and window mode, look speed and inversion, key changes (checked by the same rules; a bad set is ignored). Another version or a damaged file: defaults, the old file kept as `.bak`, and a toast. | `Client/Runtime/Settings/GameSettings.cs` |
| T2.6 | **Screens.** The Esc menu (`PauseMenu`, replacing the M1 stand-in; `QuickMenu.Current` kept for m1.ui.credits): Resume, Controls, Settings, Credits, playtest checklist, debug overlay, fly camera, the scenes in the build, Quit. It pauses the game (time scale 0, audio paused, mouse free); screens opened from it return to it with Esc or gamepad B. The F1 controls list and the Controls screen are generated from the table. Settings screen: quality, screen size and mode, volumes, look, time of day. F2 cycles Low/Mid/High (saved). Toasts. Credits and the F10 checklist kept. | `Client.UI/Runtime/` |
| T2.7 | **Cameras.** `VehicleCameraRig`: camera.js chase and hood cameras in Unity space (yaw follow 4/s and 1.5/s, look back when reversing, orbit recentring, distance with speed, terrain ray pull-in, ground and water clearance, FOV 60-76); mouse look with the cursor locked, right stick, wheel zoom (R3 steps on the pad). `CameraDirector`: fly when nobody drives or with F8, otherwise chase/hood (C or Back) on `PlayerVehicle.Current.Body` / `HoodAnchor`; autotests that script the camera keep it. Camera distance and height come from `IVehicleCameraHints` if the vehicle has it, else from its renderers' size (within 5 % of camera.js for the hatch, rally, pickup and truck). | `Client/Runtime/Cameras/` |
| T2.7 | **HUD and F3.** `HudView` (UI Toolkit, bottom right): name, LOW / WINCH / ENGINE OFF tags, km/h, gear (R, N, 1..), rpm bar (hot above 90 %), surface, "sunk N cm"; cached strings, an element changes only when its value changes, 0 bytes allocated per update (measured). `DebugOverlay` (F3): frame time p50/p95, simulation main-thread time per frame (`SimTiming`: the FixedUpdate player-loop phase), camera, quality, vehicle, DriveInput, ground counters; the engine-sim fields read `DebugOverlay.EngineStatsSource`, blank until the engine lane sets it. | `Client.UI/Runtime/HudView.cs`, `DebugOverlay.cs`, `Client/Runtime/ClientBootstrap.cs` |
| T2.7 | **Stub vehicle and autotests.** `StubDrivenVehicle` (Dev builds and the editor): a pickup-sized box that implements `IDrivenVehicle`, drives from `DriveInput` or waypoints, fills `VehicleHud`, toggles ignition / low range / winch from the counters and ruts soft ground through `StampSinks.Current` like vehicle.js `deform()`. `-br-stub-vehicle` puts it at the garage bay in a Dev build. Scenarios `m2.hud.stub` and `m2.ui.screens`. | `Testing/Runtime/Scenarios/World/`, `StreamingAssets/Autotest/m2.{hud.stub,ui.screens}.json` |

## Gates, expected vs measured

| Gate | Kind | Expected | Measured |
|---|---|---|---|
| `Deform.MatchesJs` | lane | 10,000 reference calls replayed in order; max height error ≤ 0.5 mm over the first 1,000 and ≤ 2 mm over all, as a curve; displaced volume ±2 % | **pass**. JS reference bit-exact on all 10,000 calls (return value, changed-vertex count, fnv1a). Max height error **0.0191 mm** (first 1,000) and **0.0191 mm** (all). Curve (call: mm): 1: 0.0038, 10: 0.0038, 100: 0.0076, 500: 0.0191, 1,000 to 10,000: 0.0191 (flat; every call in `artifacts\deform\MatchesJs-curve.csv`). Displaced volume JS 108.8318 m³, integer 108.8312 m³ (**-0.0006 %**); net -52.9683 / -52.9680 m³. 41,209 vertices touched; WEAR differs at 892 (by at most 2 of 255), SURFACE nowhere. 10,000 stamps in 19 ms. |
| `Deform.SubMmConverges` | lane | 0.3 mm-per-call ruts reach JS's target depth within 1 % | **pass**. Dirt: target 75.600 mm, JS 75.600 mm (99 % after 330 calls), integer 75.602 mm (after 326); the 4 rut vertices within 0.0019 mm of JS. Grass: target 37.800 mm, integer 37.800 mm (218 vs 221 calls). Slow pass (0.02 m per call, 3 passes): worst difference 0.34 mm on a 28.7 mm rut (limit 1 mm). Berm vertices differ by up to 0.26 mm (no limit set). |
| `Deform.Deterministic` | lane | same stamp log → identical tile hashes, Burst on and off | **pass**. 10,200 stamps (10,000 reference calls + 200 relax), 113 of 4,096 tiles changed; tile hashes differing Burst vs managed **0**, Burst vs Burst **0**; DeltaUm identical everywhere; the Burst path really ran Burst code. |
| `Input.NoDuplicateBindings` | headline | no key or button bound twice within one action map | **pass**: Driving 25 entries, OnFoot 8, UI 10; no duplicate in any map nor between UI and Driving/OnFoot; generated Input System maps match the table; the test catches an injected duplicate. Also `Input.CardBindings` (the card's list, every DriveInput field fed) and `Input.PathsResolve` (every path exists on a keyboard, mouse and gamepad): pass. |
| `Settings.RoundTrip` | headline | settings survive a restart; an unknown version falls back to defaults with a message | **pass**: every field back after a fresh load; quality applies through QualityPreset; key changes apply and a clashing one is caught. `Settings.UnknownVersionFallsBack`: version 99 → defaults, message "settings.json is version 99, this build reads version 1; using the defaults", old file kept as `.bak`; damaged file → defaults + message; missing file → defaults, no message. |
| T2.7 done-when (Dev build, stub `IDrivenVehicle`) | lane, feeds `m2.drive.route9` | both cameras follow it, the HUD shows the values, F3 toggles, no HUD allocation per frame | **pass** (`m2.hud.stub`, 24 s). Chase camera 8.2-8.6 m behind (pickup camDist 7.6 m × speed factor), looking at the vehicle (dot 0.977); hood camera 0.00 m from `HoodAnchor`; HUD speed equal to the vehicle's in every checked frame; **0 bytes** allocated in 2,772 HUD updates; F3 opens and closes; F8 fly and back; W, tap I, hold I (starter), X reach DriveInput. Bog run (14 s at 18 km/h through Blackwater Bog): 3,328 stamps, 1,139 partial chunk updates and no chunk rebuilt for them, 24 trees re-snapped, 13.9 ms of stamp time in total (4.2 µs per stamp), HUD "sunk 22 cm", simulation p95 0.05 ms and frame p95 6.4 ms. |
| T2.6 screens | lane | Esc menu, Controls, Settings, Credits, F1 | **pass** (`m2.ui.screens`): Esc opens and pauses, Controls (97 labels from the table), Settings (Mid applied and saved to settings.json), Credits, Esc back each time, Esc resumes, F1 shows and hides. |
| `m1.ui.credits`, `m0.playtest.f10` | kept | pass | **pass** on the M2 Dev build (the new menu keeps the M1 names; F10 now goes through the UI map). |
| `m1.shots.canon` | kept | pass | **pass**, unchanged: mean SSIM 0.869, MAD 0.0315 (as M1). `smoke` pass. |
| Lane W EditMode | | | Deform 6/6, Input 5/5, World 9/9, Render 4/4, Canon 4/4. |
| `gate.ps1 -Tier merge -OwnersBase main` | lane | green | **7 of 8 steps green in 134 s** (oracle hash, vendor check, native ctest, PlayMode 3/3, server compile, Dev build 221.5 MiB, smoke). `unity.editmode` 119/120: the one failure is `Repo.OwnersRespected`, which lists exactly the four files of the `REQUEST FOR I` commit (For I item 1); `Build.ServerContents` skipped (no server build in the worktree). |

## Deviations and decisions

1. **RutStamp layout.** 18 B as the card says, but not 06 §5.6's fields: positions in Q20 m (not mm), radius Q16 m, depth 16 µm, rate 8 µm, the rolling direction as a 16-bit angle (bit 7 of the berm byte flags it), wear in quarters; no `flags`/`surfHint`. Measured on the reference calls with the same kernels: millimetre positions give 13.5 mm max error, 06's layout 16.2 mm, this one 0.019 mm (ADR-003).
2. The direction's length is dropped (the kernel's spoil cone uses the unit direction; JS's is about 1° narrower on a 20° slope).
3. Rut falloff from exact integer `d²` (Q20); the Q12 table serves brush's smoothstep (where a square root would be needed).
4. Berm remainder by largest remainder, ties by ring order (PLAN §2.6 said "to the largest weight"; this keeps every share within 1 µm).
5. WEAR may differ from JS by 1-2 units (fixed-point truncation); SURFACE matched everywhere in the reference run.
6. Partial updates grow each dirty rectangle by 8 vertices (JS by 1), so a partial update equals a rebuild (the surface blend reads up to 8 vertices away).
7. The Esc menu pauses the game (time scale 0, `AudioListener.pause`).
8. Gamepad extras beyond the card: R3 zoom steps, B clutch and back in menus, Y hold winch, D-pad right lights / hold diff lock, Back hold manual/auto; the Settings screen has time of day ±1 h (the gamepad route for `[` `]`); F8 fly camera.
9. bindings.json reaches builds through `Resources/BrClientConfig.asset` (a reference, no copy); the Input System actions are generated in code. The template `Assets/InputSystem_Actions.inputactions` is untouched and unused.
10. Fonts: Barlow Condensed and Inter are not bundled yet (a download needs approval), so the HUD uses the default font.
11. `Client.UI` now references `Client`, `Client.Rendering`, `World` and `Deform` (client-to-client; rule 2 holds).

## For I

1. **`REQUEST FOR I` commit `b990df9`**: `StreamingAssets/Autotest/m2.hud.stub.json` and `m2.ui.screens.json` (+ metas). Suggested OWNERS entries: `"Game/Assets/StreamingAssets/Autotest/m2.hud.": "W"`, `"Game/Assets/StreamingAssets/Autotest/m2.ui.": "W"`.
2. **Folder metas not committed:** `Game/Assets/Content.meta` and `Game/Assets/Content/core.meta` belong to lane V's `Content/`; take lane V's (or commit what Unity makes) to avoid an add/add conflict.
3. **gates.json:** M2 headline `Input.NoDuplicateBindings`, `Settings.RoundTrip` (EditMode category `Input`); lane `Deform.MatchesJs`, `Deform.SubMmConverges`, `Deform.Deterministic` (category `Deform`); autotests `m2.hud.stub`, `m2.ui.screens` for the M2 milestone list.
4. **Contract notes (no change made):**
   - `IStampSink` takes floats: positions are float32 (15-30 µm apart at 256-512 m) before quantisation. Effect on MatchesJs: 0.019 mm instead of 0.015 mm. Double overloads would remove it if the contract is ever revised.
   - `IDrivenVehicle` has no camera distance/height. Until it does, a vehicle can implement `Backroads.Client.IVehicleCameraHints` (camera.js: hatch 6.4 / 2.1, rally 6.6 / 2.0, pickup 7.6 / 2.6, truck 12.5 / 4.2, dozer 10.5 / 4.0); otherwise the rig estimates from the renderers. Suggest moving the hints into Core in M3.
   - `DriveInput` has blade **presses**; JS holds Q/Z. M7 may want levels.
5. **For lane V at the merge:** call `StampSinks.Current?.Rut(...)` per vehicle.js `deform()` (it is set while Millbrook is loaded); put `HoodAnchor` at the driver's eye (camera.js: seat y − com.y + 0.25, seat z − com.z + 0.2); `VehicleHud.Surface` from a fixed string table keeps the HUD allocation-free.
6. **Engine stats in F3:** set `Backroads.Client.UI.DebugOverlay.EngineStatsSource` to the player's `EngineSimEngine` stats (rpm, torque, MAP, AFR, worker ms, ring fill, underruns, coast events). Client code may reference EngineSim, so a small adapter in `Client` works too.
7. **Volumes:** settings.json stores engine and effects volumes, but only the master volume is applied (`AudioListener.volume`). Engine-sim audio needs a hook (for example a Core `AudioVolumes`) to read `SettingsStore.Current.engineVolume`.
8. **Pause and audio:** the menu pauses audio; if engine-sim counts underruns while paused, `m2.drive.route9` should not open the menu (it does not need to).
9. **F7** (lane R's roads overlay, Dev builds) reads the keyboard directly; add it to the bindings.json UI map if it ships beyond Dev builds.
10. **For T2.10 (`m2.drive.route9`):** `CameraDirector.Main`, `OverlayController.Current.Hud` (`SpeedShown`, `GearShown`, `HudView.AllocatedBytes`) and `SimTiming.LastFrameMs` are there to check; `WorldScenarioUtil.Script` also takes the camera director.

## Next (lane W)

- M3: road plates and road meshes into the same dirty-rectangle path; mud detail and wetness (06 §5.2); the ColliderGrid for trees knocked down.
- Fonts (Barlow Condensed, Inter) once approved; a rebinding screen on top of `BindingOverride`.
- `cutBlade`, deposits and the frozen Brush/Relax stamps with the dozer (M7).

## Resume

```powershell
git -C D:\Backroads-Unity-wt\w log --oneline -12
cd D:\Backroads-Unity-wt\w
tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run                 # import or verify canon
tools\ci\unity-tests.ps1 -Platform EditMode -Category "Deform;Input;World;Render;Canon"  # lane W tests (28)
tools\ci\project-unity.ps1 -Method Backroads.Client.Editor.MillbrookSceneBuilder.Build   # scene + BrClientConfig
tools\ci\build-player.ps1 -Profile Dev -Milestone M2
tools\ci\autotest.ps1 -Scenario m2.hud.stub -Profile Dev -Milestone M2
tools\ci\autotest.ps1 -Scenario m2.ui.screens -Profile Dev -Milestone M2
builds\M2-dev\Backroads.exe -br-stub-vehicle        # drive the stub yourself (W/S/A/D, C, F3, F8, Esc)
tools\ci\gate.ps1 -Tier merge -OwnersBase main -KeepGoing
```

Prototype of the kernels against the JS mirror outside Unity (dotnet 9, scratch only): the same `StampKernels.cs`,
`Stamps.cs` and `JsDeformReference.cs` compile in a console project; that is how the stamp layouts above were compared.
