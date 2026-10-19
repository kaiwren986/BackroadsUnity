# M2 handoff: lane V, T2.1-T2.3 (BRV V0, four vehicles in Millbrook, BRV v2 head start)

Branch `m2/v-brv` in `D:\Backroads-Unity-wt\v`, cut from `main` at `c42a473`; `main` at `4fb372e` (lane A merged) was
merged in at `4ffebd0` (no newer `main` commits at hand-off). Nothing was pushed, installed, downloaded or signed into.
The oracle was read only with `git -C D:\Backroads -c core.autocrlf=false archive 552f7807 assets/vehicles` and
`git show 552f7807:js/<file>.js`; `D:\Backroads`, `CanonData\v1` and the shared contracts (`VehicleContracts.cs`,
`StampSink.cs`) are untouched. Unity runs went through `tools\ci\*.ps1` only.

## Done

| Card | What | Where |
|---|---|---|
| T2.1 | **BRV V0: `js/vehicle.js` line by line.** `BrvSim` (step order of the JS: gravity, water, suspension rays against the heightfield with the fixed-point `rayDown`, PGS tyres with mud grip, sinkage drag and spin, track steer, hull contacts, 2D statics against the props.js collider grid with tree knock-over, winch rope, drag, damping, integration, island clamp, ruts every 2nd step, dirt, stuck timer, fuel, sleep), `LegacyJsEngine` (torque curve, automatic gearbox shifting on ground speed, low range x2.6 with top gear 2, AWD on low, rev limiter, engine braking, submerged cut-out), `BrvSim.Drive` (driver aids: brake then reverse, auto-hold), `BrvSim.Controls` (main.js `vehicleControls`: L, R, X, G with `findAnchor`/`toggleWinch`, winch reel, engine on), `CollideVehicles`. IEEE doubles like the JS, JS world space inside, flipped at the Unity API (`BrvFleet.UnityPos/UnityRot`). World heights through lane W's double-precision `WorldGridQueriesRef` (the JS-mirror path). | `Game/Assets/Backroads/Vehicles/Runtime/BrvSim.cs`, `LegacyJsEngine.cs`, `BrvDriverInput.cs` |
| T2.1 | **`BrvState` + `VehicleStepJob`.** `BrvState` is the whole per-vehicle state, blittable (wheels in a `FixedList4096Bytes`); `BrvDef` the constants (layout, hull, ratios). `VehicleStepJob` (Burst, `CompileSynchronously`) runs one fixed step of a fleet in the JS loop order: the player's controls, every vehicle's step, every pair. `BrvFleet` owns the native state, runs the job with `.Run()` on the main thread and replays the recorded rut calls into `Backroads.Deform.StampSinks.Current` (null = no deformation) right after each step; knocked trees come out as events. Snapshot in/out: `GetSnapshot`/`SetSnapshot` (struct copy) and `BrvSnapshot` (byte form v1, draft ADR-005). | `.../Vehicles/Runtime/BrvState.cs`, `BrvDef.cs`, `BrvWorld.cs`, `VehicleStepJob.cs`, `BrvFleet.cs`, `BrvSnapshot.cs` |
| T2.1 | **`ClassicVehicleDef`**: the DEFS/META table for hatch, rally, pickup, truck, dozer with the U9 names (**Kestrel 4x4**, Badger D3) and the Unity engine ids (truck `v8_bigblock_a`, pickup `v8_cross_a`, rally `f4_boxer_a`, hatch `i4_vvt_a` then `i4_synth`). It is C# (no runtime file to load, server included); EditMode tests check it field by field against `CanonData/v1/gameplay/vehicles.json`, so the real name "Ranger" never enters a build. | `.../Vehicles/Runtime/ClassicVehicleDef.cs` |
| T2.1 | **Golden-trace replay** (`GoldenTraceReplay`): boots like the oracle harness (every vehicle placed, settled and parked on its garage bay in VEHICLE_ORDER, the player in the pickup), places and settles the trace vehicle, then feeds the open-loop channels and key hits through `vehicleControls` one step per frame; `GoldenTraceGates` computes the M2 numbers the same way from the oracle's and the C# samples, plus a per-second table. A small JSON reader (`MiniJson`). | `.../Vehicles/Runtime/Reference/` |
| T2.2 | **`VehicleSimSystem`** (shared MonoBehaviour): 1/120 s `FixedUpdate`, input from `SimVehicle` (DriveInput levels + presses counted since the last step; the first `SetInput` after becoming the player's vehicle only sets the baseline), rut calls to the sink, engines stepped with `EngineState` of `LegacyJsEngine`, E (get in/out) at < 3 m/s moves the player into the nearest vehicle within 12 m, simulation main-thread time per rendered frame (p50/p95/max). **`SimVehicle`** asks `VehicleEngines.Create` for its engine when the player gets in (disposes it when they get out), attaches it to the exhaust socket, `Step`s it every fixed step, and serves `VehicleHud`. | `.../Vehicles/Runtime/VehicleSimSystem.cs`, `SimVehicle.cs` |
| T2.2 | **`Backroads.Vehicles.Client`** (new client assembly, `!UNITY_SERVER`): `VehicleSpawner` builds itself whenever a scene named `Millbrook` loads (Millbrook.unity is not edited), puts pickup, hatch, rally and truck on parking bays 0-3 (VEHICLE_ORDER; the dozer's bay 4 stays empty until M7), settles each for 240 steps before it is shown, starts the player in the truck and keeps `PlayerVehicle.Current` on the driven vehicle; `-br-no-vehicles` leaves them out. `VehicleView : IDrivenVehicle`: body interpolated between the last two fixed steps, wheels on their suspension with steering and spin (right side mirrored, truck duals), hood anchor (camera.js), exhaust socket (META), lights as vehicleModels.js `setLights` (head, tail, brake, reverse, indicators; two headlight spots on the player's vehicle), the horn (audio.js: 392 + 494 Hz squares, 1.8 kHz low-pass), LODGroup at about 40 m forced to LOD0 for the player's vehicle. `FallingTrees` tips over knocked trees as props.js does. | `.../Vehicles/Client/` |
| T2.2 | **Models**: the oracle's `assets/vehicles/{hatch,rally,pickup}.glb` (byte copies, glTFast) in `Content/core/vehicles/models/`, and lane A's `Art/Vehicles/Truck/truck.glb` (same bytes as the oracle, one LFS object). `VehicleModelBuilder` (menu *Backroads > Vehicles > Build models*, or `-executeMethod Backroads.Vehicles.Editor.VehicleModelBuilder.BuildCli`) writes `Content/core/vehicles/Resources/BrVehicleModels.asset` (meshes + glTF material values) and two URP/Lit templates, so builds need no glTFast materials. Triangle counts equal 03 §4.17 (hatch 20,638 / 2,881, wheel 3,924 / 252, ...). Paint, glass and lights are swapped as vehicleModels.js does (the truck is green, META defaultPaint). | `Game/Assets/Content/core/vehicles/`, `.../Vehicles/Editor/` |
| T2.2 | **Autotest `m2.vehicles.drive`** (kind `vehicles-drive`): Millbrook starts with the four vehicles settled on the bays and the player in the truck; E switches into the nearest parked vehicle and back; L and H; a scripted DriveInput drives the truck off its bay (0.6 throttle, 3 s), brakes it to a stop and lets go; night shot with the headlights; reports sim time per frame. The scenario file is a `REQUEST FOR I` commit. | `Testing/Runtime/Scenarios/Vehicles/`, `StreamingAssets/Autotest/m2.vehicles.drive.json` |
| T2.3 | **BRV v2 head start behind `BrvFeatures.V2Driveline`** (off; `-br-brv-v2` or a test turns it on): `Shaft` (inertia, ω, angle, explicit torque), `Row` (soft bounded velocity constraint over the chassis and up to 3 shafts, warm-started λ, 06 §3.6), `ShaftGraph` (explicit torques, then 12 PGS sweeps per substep, 06 §3.8) with clutch, gear, open differential (with final drive), locker and brake rows; `DrivelineDef`/`DrivelineBuilder` build crank - clutch - gearbox - output - axle diffs - wheels for a classic vehicle (JS overall ratios split into gearbox x final drive). Nothing in V0 reads it. | `.../Vehicles/Runtime/V2/` |
| Docs | This handoff; **draft `docs/adr/ADR-005-brv-snapshot.md`** (BRV snapshot byte format v1, proposed to freeze at `m2`). | `docs/` |

## Gates (cards T2.1-T2.3), expected vs measured

All on this laptop, 2026-10-19, at `4f4c316` (tests) and the Dev build of that commit. Reports:
`artifacts\vehicles\golden-traces.md` (+ one CSV per trace with both sample sets), `artifacts\autotest\m2.vehicles.drive[-minspec]\`.

### Vehicle.GoldenTraces (headline, PlayMode)

Pure v1 terrain (no road plates or road colliders), no traffic, deformation by a test-only float `world.js rut()`
(`JsRutSink`) on a private grid copy (lane W's integer sink is not merged yet; see For I 6).

| Trace | Gate | Tolerance | Oracle | C# | Delta | Result |
|---|---|---|---:|---:|---:|---|
| `truck_0_80_route9` | 0-80 km/h time ¹ | ±7 % | 17.099 s | 17.099 s | **+0.0 %** | **pass** |
| `pickup_0_100_route9` | 0-100 km/h time ¹ | ±7 % | 6.741 s | 6.741 s | **+0.0 %** | **pass** |
| `pickup_brake_80` | 80 → 0 km/h braking distance ² | ±8 % | 22.250 m | 22.250 m | **+0.0 %** | **pass** |
| `truck_quarry_low` | low-range quarry climb time ³ | ±10 % | 27.547 s | 27.547 s | **+0.0 %** | **pass** |
| `hatch_bog_10s` | Blackwater Bog sink depth at 10 s | ±15 % | 0.288 m | 0.288 m | **+0.0 %** | **pass** (stuck 6.56 s in both) |
| `winch_15m_tree` | winch 15 m to a tree completes | completes | hooked at step 1, rope 14.93 → 4.79 m | hooked at step 1 on the same snag (0.00 m), rope 14.93 → 4.82 m (+0.7 %), out of the bog | | **pass** |

How close the port is, per trace (largest position difference over all 0.1 s samples; speed per second):
truck 0-80 **8.2e-13 m**, pickup 0-100 **3.6e-13 m**, pickup braking **2.6e-13 m**, quarry **4.1e-13 m** (gear and rpm
identical every second), hatch bog **8.8e-5 m** (0.00 km/h mean speed difference), winch **1.10 m** (mean 0.41 km/h, max
1.59 km/h; the mud chaos amplifies deviation 1 below from the first second).

¹ The trace lifts the throttle at the step where the speed reaches the target, so the 0.1 s samples peak just under it
(79.98, 99.98 km/h); the time is taken where the samples cross 99 % of the target, identically for both. The oracle
lifted at 17.39 s and 6.85 s. ² From the first sample after the brake onset (78.1 km/h in both) to the first sample
under 0.3 m/s. ³ Time to 90 % of the oracle's elevation gain (+8.59 m). The recorded trace is **unloaded** (the harness
never calls `setPayload`), so "loaded truck" is measured as recorded; see For I 9.

### Lane gates

| Gate | Expected | Measured |
|---|---|---|
| PlayMode `Vehicle.SnapshotRoundTrip` | < 1 mm | **pass: 0 m.** Snapshot to bytes (1,659 B) mid-drive on asphalt (step 400 of the pickup run) and in the bog with the winch hooked (step 600), restored, continued 680 and 1,200 steps: position difference 0, end state byte-identical |
| PlayMode `Vehicle.SettleParked` | 4 vehicles on the bays settle within 240 steps to \|v\| < 0.01 m/s | **pass**: \|v\| = 0 (all four asleep, as in the JS), every wheel in contact; wheel suspension lengths equal the browser game's own boot settle (vehicles.json `derived`) to **2.2e-10 m** |
| Dev build starts in the truck at the garage and drives with a scripted DriveInput | yes | **pass** (`m2.vehicles.drive`, Dev build, 2560x1440 High): player in the "Hauler 7.5t"; bays within 0.010 m, parked \|v\| 6e-5 m/s; E switched into the Sprint RS and back; L, H; drove 15.6 m, top 25.9 km/h, braked from 25.9 km/h to a stop in 0.74 s over 2.74 m, no impacts; engine none (silent until lane vs merges) |
| Simulation main-thread time per rendered frame, p95 | ≤ 3.5 ms at High; ≤ 12 ms on the proxy | **pass: 0.016 ms** at High (p50 0.011, max 0.04; whole-frame p95 7.6 ms including the night shot); **0.047 ms** on the min-spec proxy (`-MinSpec`: 6 E-cores, Low, 30 fps = 4 steps per frame; max 0.09 ms). One driven vehicle, three parked (asleep), no engine-sim yet |
| T2.3 flag off = V0 | golden traces unchanged | **pass**: V0 never touches the v2 code; the traces above ran with the flag off; EditMode `VehicleV2.FlagOffByDefault` |
| T2.3 flag on: free shaft and two-shaft clutch | expected angular-momentum exchange | **pass** (EditMode `VehicleV2.*`, 5 tests): free shaft ω = ω0 + Tt/I to 1e-9; clutch (0.25 kg·m² at 300 rad/s onto 0.05 kg·m², 400 N·m) passes exactly its capacity each slipping substep, total angular momentum constant to 1e-9, locks at substep 15 = **31.25 ms as predicted**, common speed 250.0000 rad/s; gear row reflects inertia (I_in R² + I_out); pickup skeleton: wheels turn at crank / first-gear overall ratio |

Also green: EditMode `Vehicle.ClassicDefsMatchCanon`, `DerivedMatchCanon` (ratios, mount height, inertia, hull, wheel
layout to 1e-13), `DisplayNames`, `TopSpeeds` (148/187/137/97 km/h); PlayMode `Vehicle.BurstMatchesManaged` (Burst and
plain C# agree to 2e-13 m); the full EditMode suite 101/102 (the one failure is `Repo.OwnersRespected`, listing exactly
the two `REQUEST FOR I` files); full PlayMode 6/6; `build-player.ps1 -CompileServerScripts` (UNITY_SERVER) green; Dev
build, `smoke` and `m1.shots.canon` pass (vehicles now stand on the bays as in the JS shots; mean SSIM 0.870, M1 0.869).

## Deviations and decisions

1. **Rut timing.** Burst cannot call the managed sink, so rut calls are recorded and replayed into
   `StampSinks.Current` right after the step. The wheel ruts (`deform`) already happened at the end of the step in the
   JS, so they are exact. The **hull-point mud ruts** happened inside `hullContacts` in the JS, so the later hull points
   of the same step saw them; here they see them one step later. This is the main reason the winch trace drifts
   (1.10 m, rope +0.7 %); all gates pass. Rut arguments become floats at the contract (`IStampSink` takes floats).
2. **drive() every fixed step** with dt = 1/120 (the JS ran it once per rendered frame with the frame dt). Identical at
   120 fps (the traces); smoother steering-rate limiting at 60 and 30 fps.
3. **Gate metrics** are defined in `GoldenTraceGates` (notes ¹-³ above); the oracle's numbers come out of the same code.
4. **Switching vehicles**: E at < 3 m/s into the nearest vehicle within 12 m (no walking in M2). In practice that is the
   garage, where they are parked. Lane W already sends `GetInOutPresses`; no new binding is needed.
5. **Ignition**: getting in starts the engine (the JS); tap I turns it off and on again (`Starter` is reported to the
   engine for 0.5 s after it starts, or while I is held below 80 % of idle).
6. **Dirt on the bodywork** (`dirtify` shader) is not drawn yet; `BrvState.dirt` is simulated. Lights and headlight
   spots are re-lit for URP (artistic, as lane W did for the world). Vehicle materials are two URP/Lit templates, not
   lane A's slot materials (those come with the FBX path).
7. **Knocked trees** fall through `FallingTrees`, which finds lane W's tree object by name
   (`Vegetation/<species>/t<index>`); tree colliders are not re-snapped when the ground under them is rutted (props.js
   `onTerrainChanged`).
8. **Burst** runs the step on the main thread (`job.Run()`), so step order and stamp order are those of the JS.
9. The **dozer** is not spawned (M7, on v2). `BrvTestTrack.unity` was not needed and not made.

## For I

1. **`Game/Assets/StreamingAssets/Autotest/m2.vehicles.drive.json`** is committed on this branch as `807a7bc` and
   `e0753d5` (`REQUEST FOR I`). Keep it, or add `"Game/Assets/StreamingAssets/Autotest/m2.vehicles.": "V"` to
   `OWNERS.json`. **`tools\ci\gates.json`**: add `autotest.m2.vehicles.drive` (M2 lane gate for "drives with a scripted
   DriveInput", simulation p95 ≤ 3.5 ms) and the same with `-MinSpec` (≤ 12 ms on the proxy).
2. **Architecture tests**: add `Backroads.Vehicles.Client` to `TestUtil.Client` so rules 2 and 8 treat it as a client
   assembly (it has `!UNITY_SERVER`; `TestUtil.IsClientName` only knows `Backroads.Client*`), and let
   `Build.ServerContents` require its absence from the server's `Managed\`.
3. **Contracts (no change needed, please write it into the doc comments at the next contract edit):**
   `DriveInput.Steer` +1 = right (D), as the browser game and lane W's `ClientInput`. `VehicleHud.Gear` is the JS gear
   (-1 R, 1..n); the HUD shows **P** when `Ignition` is false (hud.js). `VehicleHud.Rpm` is the engine's `DisplayRpm`
   when an engine is attached, else the classic rpm. `VehicleHud.SunkCm` is shown by hud.js only above 8 cm.
4. **Feature flags**: `BrvFeatures.V2Driveline` (`-br-brv-v2`) lives in Backroads.Vehicles until Core has `FeatureFlags`
   (PLAN 1.5).
5. **`content-provenance.json`**: add the three vehicle GLBs in `Game/Assets/Content/core/vehicles/models/` (byte
   copies of the oracle's `assets/vehicles/{hatch,rally,pickup}.glb`, made by the oracle's `tools/blender/vehicles.py`;
   git blobs 6f5d161c, fc2a56f5, 48df7c2e at 552f7807). The truck uses lane A's copy.
6. **Golden traces with lane W's sink**: after W merges, `Vehicle.GoldenTraces` should also run against W's integer
   `IStampSink` (swap `JsRutSink` for it in `VehicleTests.GoldenTraces`, or add a second case). The bog and winch traces
   are the ones that depend on it.
7. **Lane W**: `VegetationRenderer` could offer `Knock(treeIndex, dirXJs, dirZJs)` (and re-snap tree colliders on
   dirty rects, props.js `onTerrainChanged`); `FallingTrees` would then call it instead of finding the object by name.
   `VehicleSimSystem.TreeKnocked` is the event.
8. **Denylist**: PLAN §3.10's `Content.NoRealMarks` list contains "D3", and U9 named the dozer "Badger D3". It only
   matters when the dozer ships (M7): either an exception for the approved name or a different model number.
9. **"Loaded truck" quarry climb**: the recorded trace is unloaded. If the gate should be loaded, lane W re-records
   `truck_quarry_low` with a payload (`setPayload`) in `tools/oracle` (`BrvSim.SetPayload` exists on this side).
10. **Engines**: nothing to do; vehicles call `VehicleEngines.Create` when the player gets in (truck `v8_bigblock_a`,
    pickup `v8_cross_a`, rally `f4_boxer_a`, hatch `i4_vvt_a` then `i4_synth`) and `Step` every fixed step. Until lane
    vs's factory is registered they are silent and the HUD shows the classic rpm.
11. `-br-no-vehicles` keeps Millbrook empty (for lane W's comparisons, if wanted).

## Next (M3, lane V)

- BRV v2 core on the `ShaftGraph`/`Row` skeleton: wheel ω as a solver DOF, MF tyre rows (06 §3.6-3.7), driveline rows
  per vehicle, 4 substeps x 12 iterations, golden traces as the regression net; snapshot v2 (ADR-005).
- The PhysX chassis (V′, 06 V1) and wheel sphere colliders; vehicle-vehicle contacts move to PhysX.
- Freeze ADR-005 (open questions: winch target id, scratch fields).

## Resume

```powershell
git -C D:\Backroads-Unity-wt\v log --oneline -12
cd D:\Backroads-Unity-wt\v
tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run                     # canon (gitignored import)
tools\ci\unity-tests.ps1 -Platform EditMode -Category "Vehicle"                             # 9 tests
tools\ci\unity-tests.ps1 -Platform PlayMode -Category "Vehicle" -NoGraphics                 # GoldenTraces, SnapshotRoundTrip, SettleParked, BurstMatchesManaged
tools\ci\project-unity.ps1 -Method Backroads.Vehicles.Editor.VehicleModelBuilder.BuildCli   # only when a vehicle GLB changes
tools\ci\build-player.ps1 -Profile Dev -Milestone M2
tools\ci\autotest.ps1 -Scenario m2.vehicles.drive              # add -MinSpec for the proxy number
```
