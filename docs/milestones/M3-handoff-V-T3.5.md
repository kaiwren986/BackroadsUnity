# M3 handoff: lane V, card T3.5 (BRV v2 core behind a flag)

Branch `m3/v-brv-core` in `D:\Backroads-Unity-wt\v`, cut from `main` at `bbe542f`. Commits `4743413` (shafts, MF tyres,
ABS, test track, lane gates), `06cd934` (conformance suite v0, self-traces, perf, crawl control), `47b5d98` (overspeed
protection, ADR-005 version 2), `2ae0cc6` (the perf tests' CPU helper out of the vehicles path), `3dc6eff` (this
handoff, first version), `0b0736d` (main `c5c147f` merged in: T3.1, S2, T3.4), `bc242dd` (step 2: the track on its own
`WorldGridLayout`, the `BrvTestTrack` scene) and this update. Nothing was pushed, installed, downloaded or signed into; Unity ran only through `tools\ci\*.ps1`. `D:\Backroads`,
`CanonData\v1` and the shared contracts are untouched.

**Card status: done** (steps 1, 2 and 3).

## In plain words

BRV v2, the vehicle model that engine-sim will drive in M4, now runs behind the `-br-brv-v2` switch. Without the switch
the game drives exactly as before: V0, the browser game's model, still matches its golden traces to 1e-13 m as in M2.

In v2 every spinning part (the engine's crank, the gearbox, each wheel) is a real spinning shaft, and one solver handles
the clutch, gears, differentials, brakes and tyres together, four times per physics step. Tyres follow research 06's
Magic Formula tables, so grip peaks and falls off with slip like a real tyre. ABS holds braking wheels just under that
peak, and in low range a crawl control stops the driven wheels digging in.

A new scene, `BrvTestTrack`, holds the test track: a flat pad with a skidpad, 10/20/30/35 % ramps, 150 mm steps, a mud
patch and a wheel pit, on a world grid of its own. Open it in the editor and drive the four vehicles there on v2. On
that track every M2 vehicle stands still on a 35 % ramp with the driver's feet off the pedals. The hatch stops from 100 km/h in 40 m with
ABS. With one wheel in the air, the open differential spins that wheel up smoothly to the engine's limit. A locked
differential widens the turning circle by 7-12 %. 1,000 random 60-second runs never produce a NaN or a runaway.

One vehicle costs 12 µs per physics step (budget 30 µs), and 50 vehicles take 0.59 ms per step (budget 1.0 ms). These
are provisional: they were not measured under `machine.lock`.

v2 does not yet feel like the browser game. With real tyre tables it grips less (skidpad 0.6-0.9 g against the browser
game's 1.0-1.2 g), stops longer and launches slower. Tuning that is M4's job (`Vehicle.LaunchContinuity` [tune]).

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Shafts and rows.** `BrvSim.StepV2` (`BrvDef.v2`, set by `BrvFleet.Add` from `BrvFeatures.V2Driveline`): V0's rays, hull contacts, statics, winch, water, drag, integration and ruts, with the suspension and the tyres in 4 substeps x 12 PGS iterations (06 §3.4, §3.8). Shafts: crank, gearbox output (the input lumped into it while a gear is engaged), one per wheel, **one per axle while its locker is on** (lumping, 06 §4.1). Rows: auto-clutch (bounded by 1.5 x peak torque x engagement), open axle diffs with the final drive (the JS AWD keeps its locked centre: both diffs on the output shaft), brake + rolling resistance + bearing per wheel, tyre long (chassis + wheel shaft) and lat (chassis, at the JS roll-influence arm) with a friction ellipse, the JS mud drag. Each wheel's **brake and tyre-long rows are solved as one exact 2x2 box QP** (the light wheel between the heavy chassis and the brake converges at 0.99 per sweep in plain PGS). At walking pace the tyre rows turn rigid, and **low-speed anchors** (tyre deflection, brake slip angle, pulled back over 80 ms) remove the solver's residual creep | `Vehicles/Runtime/V2/BrvSimV2.cs`, `BrvState.cs` (`BrvDriveline`, the v2 wheel fields), `BrvDef.cs`, `BrvFleet.cs` |
| 1 | **MF secant tyres** with the 06 §3.7 tables (6 tyre classes, long and lat B C D E, peak slips), the surface table (multipliers on D, C_rr per surface; mud = the JS GRIP ratio x the vehicle's mudGrip), load sensitivity pD2, the JS wear and water terms; V_LOW 0.5 m/s. Hatch = road tyre, rally = gravel, pickup = all-terrain, truck = truck 22.5" | `V2/BrvV2Def.cs` (`TyreTables`, `MfCurve`, `BrvV2Def.FromClassic`) |
| 1 | **ABS** (06 §4.1): while the foot brake is on and the wheel rolls above 1 m/s, the brake row may only slow the wheel to the speed that holds 90 % of the tyre's peak slip, never lower: an ideal modulator solved implicitly with the tyre (no pressure cycling). **Crawl control** in low range (06 §3.6, the JS "claws instead of digging"): a driven wheel's slip is capped at the same target by its brake, and the engine's torque is cut while a driven wheel slips past it (an open diff otherwise sends the spinning wheel more torque than its brake holds) | `BrvSimV2.cs` |
| 1 | **Engine and gearbox until engine-sim (M4):** the JS torque curve on a real crank (x 0.86 driveline efficiency), idle governor, a soft limiter (98-102 % of redline), engine drag (the JS engine-brake slope). The **JS automatic as an AMT**: shifts on ground speed as the JS, auto-clutch from crank rpm (closed from just above idle to the JS launch rpm, which rises with the throttle), fuel cut and rev-matching while the clutch is open in a shift, overspeed protection (the clutch opens at once above 115 % of redline). The gearbox splits the JS overall ratios into gearbox x final drive (top gear 1:1), so v2 shifts and tops out where V0 does | `BrvSimV2.cs` (`V2Gearbox`), `BrvV2Def.cs` |
| 3 | **Snapshot version 2 with the version 1 reader** (ADR-005 updated): version 1, byte for byte, plus the shaft block (driveline, warm starts, anchors, crawl cut; 2,052 bytes for four wheels); version 1 snapshots still load (the first v2 step re-syncs the shafts) | `BrvSnapshot.cs`, `docs/adr/ADR-005-brv-snapshot.md` |
| 3 | **Vehicle conformance suite v0** (`Vehicle.ConformanceV0`): every M2 vehicle, V0 and v2 side by side on the track: 0-60 km/h, speed after 380 m, 80-0 km/h, skidpad lateral g on the 30 m circle (a path-following driver), full-lock radius at 2 m/s, hill starts on 20 % and 35 % (low range), the 150 mm steps, 15 s of mud; `artifacts\vehicles\conformance-v0.md` | `Tests/PlayMode/Vehicles/VehicleV2SuiteTests.cs` |
| 3 | **Unity self-trace baseline [S17]** (`Vehicle.SelfTraces`): the six golden-trace inputs replayed by Unity on canon v1 with V0 and with v2, against a committed baseline in the oracle's JSON format (12 files, 0.5 MB; `GoldenTraceSamples.ToJson`); gates within 2 %, positions within 5 cm off the mud. Re-record with `BR_RECORD_SELF_TRACES=1` | `Tests/PlayMode/Vehicles/SelfTraces~/v0`, `v2`, `Vehicles/Runtime/Reference/GoldenTrace.cs` |
| 2 | **The test track**: `BrvTestTrack` builds an analytic WorldGrid on a `WorldGridLayout` of its own (`BrvTestTrack.Layout`, 1,024 m at 0.5 m; T3.1), or on any layout that holds it: asphalt pad 300 x 400 m with a launch lane and the 30 m skidpad, four asphalt ramp lanes at 10/20/30/35 % (40 m up, 12 m plateau, 40 m down), ten 150 mm concrete steps, a 40 x 50 m patch of 0.6 m mud, a 2 m deep wheel pit in an asphalt apron. `BrvSim.PlaceAligned` places a vehicle on a slope. `BrvFleet` takes a capacity (50 for the perf gate), `SetLockers`, `SetAbs`, `SetRolling`; a world without static colliders gets empty collider arrays (jobs need valid containers) | `Vehicles/Runtime/TestTrack/BrvTestTrack.cs`, `BrvFleet.cs`, `BrvWorld.cs` |
| 2 | **BRV reads the world's size from its grid** (T3.1): the browser game's island clamp is `layout.Half - 6` (was 512 - 6) and the static-collider lookup uses the collider grid's `CellsPerSide` (`BrvWorld.colliderCells`, was 64); `JsRutSink` and the tests index through the grid's layout. V0 on canon v1 is unchanged (golden traces 8.2e-13 m as before); `Vehicle.WorldEdgeFollowsLayout` holds V0 and v2 at 250 m on a 512 m layout | `BrvSim.cs`, `BrvWorld.cs`, `Tests/PlayMode/Vehicles/JsRutSink.cs` |
| 2 | **The `BrvTestTrack` scene** (`Scenes/BrvTestTrack.unity`: a camera, a sun, the default sky and `BrvTestTrackWorld`; written by `BrvTestTrackSceneBuilder`, menu *Backroads > Vehicles > Build BrvTestTrack scene*). When it loads it builds the track, draws it with lane W's `TerrainChunkSystem` (`MillbrookLook`'s terrain material), keeps ruts with lane W's `DeformSystem`, puts the four M2 vehicles on the south end of the pad on BRV v2 (`-br-brv-v0` keeps them on V0 for a comparison) and the player in the truck; lane W's camera rig and input follow `PlayerVehicle.Current` as in the canon test world. `Vehicle.TestTrackScene` loads the scene file and drives the truck off (13.5 m in 4 s). Pictures: `M3-handoff-V-T3.5/t35-00-testtrack-chase.png` (behind the truck on the pad, the ramps ahead) and `t35-01-testtrack-ramps.png` (the four ramps and the step lane) | `Vehicles/Client/BrvTestTrackWorld.cs`, `Vehicles/Editor/BrvTestTrackSceneBuilder.cs`, `Scenes/BrvTestTrack.unity`, `Tests/PlayMode/Vehicles/TestTrackSceneTests.cs` |

## Done when, expected vs measured

All on this machine (AMD Ryzen 9 5950X, 16 cores of one class), 2026-10-22, Burst on, other lanes' Unity jobs running.
Reports in `artifacts\vehicles\`.

| Gate | Expected | Measured |
|---|---|---|
| `Perf.Vehicles.StepCost` | ≤ 30 µs per vehicle per step on a P-core (also E-pinned); 50 vehicles ≤ 1.0 ms wall | **provisional pass**: 13.9 / 11.9 / 11.8 µs per vehicle per step for 1 / 20 / 50 vehicles pinned to one core; 50 vehicles 0.59 ms per step (median, p95 0.61 ms). Not under `machine.lock` (no perf mode in `unity-tests.ps1`, For I 2). **No E-pinned number**: this CPU has no E-cores (the test pins an E-core when the CPU is hybrid) |
| `Vehicle.StabilityFuzz` (nightly) | 1,000 seeds x 60 s: 0 NaN/Inf, \|v\| ≤ 100 m/s, \|ω\| ≤ 50 rad/s | **pass: 0 failures**. Random vehicle, place (pad, ramps, steps, mud, pit, grass), heading, drop up to 1.5 m, speed up to 30 m/s, spin, low range, lockers, ABS; random pedals, steering, handbrake and low-range/locker toggles every 0.3-3 s. Largest \|v\| 30.2 m/s, chassis \|ω\| 8.0 rad/s, wheel \|ω\| 151 rad/s, engine 7,818 rpm; 112 s wall. A 40-seed x 10 s version (`StabilityFuzzQuick`) runs on every merge |
| `Vehicle.HillHold` | 10-35 % for 60 s, creep < 1 mm/s | **pass: 0.0000 mm/s** for all 32 cases (4 vehicles x 4 grades x facing up and down), driver's feet off the pedals (auto-hold brake 0.7, driven, so no sleeping); largest speed 0.003 mm/s |
| Open diff, one wheel airborne | spins it up without oscillation | **pass** (`Vehicle.OpenDiffAirborne`): body on a jack stand, rear right wheel over the pit, full throttle in first. Rally: free wheel 80.2 rad/s (78.8 expected at the limiter), 90 % in 0.68 s, never slows by more than 0.04 % in a step, 0 reversals above 0.1 %, settled band 0.04 %; rear left wheel slip ≤ 0.05 m/s. Truck: 18.7 rad/s (18.4 expected), band 0.06 %, slip ≤ 0.02 m/s |
| Locked diff widens the turning circle | wider | **pass** (`Vehicle.LockedDiffTurningCircle`, full lock at 2 m/s): rally 5.02 → 5.63 m (+12.1 %), truck 8.45 → 9.02 m (+6.8 %), pickup front and rear 6.86 → 7.33 m (+6.9 %) |
| ABS stop from 100 km/h | 36-45 m | **pass: 40.4 m** (hatch, road tyres, `Vehicle.AbsStop`). For the record: without ABS 62.8 m; rally 58.4 m (gravel tyres on asphalt), pickup 47.1 m, truck from 80 km/h 32.3 m; V0 (the JS) 32.5 / 30.6 / 34.7 / 24.1 m |
| Unity self-trace baseline [S17] | recorded on the test world | **done**: 12 traces (V0 and v2) on canon v1, compared on every merge (`Vehicle.SelfTraces`): V0 within 7e-7 m of the baseline, v2 the same. v2's 0-80 and 0-100 km/h gates are undefined (v2 does not reach the target inside the oracle's throttle window), so those two compare positions only |
| Vehicle conformance suite v0 | exists | **done**, table below |

Conformance suite v0 (`conformance-v0.md`; "-" = not reached):

| Vehicle | Model | 0-60 km/h (s) | At 380 m (km/h) | 80-0 km/h (m) | Skidpad (g) | Full lock at 2 m/s (m) | 20 % start, 25 m (s) | 35 % low range, 25 m (s) | 150 mm steps (s) | Mud, 15 s (m) |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| pickup | V0 | 2.87 | 136.8 | 22.3 | 1.10 | 6.81 | 3.1 | 5.7 | 17.4 | 164.9 |
| pickup | v2 | 3.99 | 136.6 | 30.4 | 0.76 | 6.86 | 3.9 | 10.6 | 19.0 | 115.1 |
| hatch | V0 | 4.17 | 137.8 | 20.9 | 1.16 | 5.11 | 4.5 | 7.0 | 17.9 | 29.7 |
| hatch | v2 | 6.13 | 130.5 | 26.1 | 0.88 | 4.81 | 9.9 | 10.7 | 19.4 | 13.2 |
| rally | V0 | 2.69 | 171.6 | 19.6 | 1.24 | 5.31 | 3.2 | 4.7 | 17.5 | 44.6 |
| rally | v2 | 6.33 | 155.2 | 37.7 | 0.59 | 5.02 | 9.6 | - | 18.8 | 17.3 |
| truck | V0 | 8.08 | 95.8 | 24.1 | 0.95 | 9.03 | 4.7 | 22.9 | 17.9 | 40.2 |
| truck | v2 | 9.91 | 94.2 | 32.3 | 0.66 | 8.45 | 8.0 | 27.8 | 20.1 | 17.5 |

Also green: EditMode `VehicleV2Core` (MF tables against 06 §3.7: peak slip within 0.005, CS within 0.1, falloff within
0.006, for all 12 curves; v2 constants; the 10-wheel state still fits; snapshot version 2 and the version 1 reader;
track geometry; aligned placement), PlayMode `Vehicle.SnapshotRoundTripV2` (version 2 bytes mid-drive on the track,
restored: 0 m over 660 steps, byte-identical end state), and every M2 vehicle test unchanged (`GoldenTraces` with the same
deltas as M2, `SnapshotRoundTrip`, `SettleParked`, `BurstMatchesManaged`).

## Waiting on you

- **Kill criterion** (fuzz and hill hold together by the end of M4): **not at risk**. Both pass now, on the same build.
- Nothing to decide. Later, M4: how close v2's grip and launches should come to the browser game's feel (the "classic
  handling" preset of PLAN risk 7) is a tuning call to make on the test track with you.

## Where this differs from the card and research 06

1. **Hull contacts, statics, winch, water and ruts** stay V0's once-per-step code, not rows in the substep solver (06
   §3.8 puts the hull rows there). Positions are frozen in the step, as 06 says; the hull worked unchanged in every test.
2. **The 2x2 block solve and the low-speed anchors** are additions to 06's plain PGS. Without them the hill hold creeps
   (a soft row at V_LOW creeps about 8 mm/s, and the brake-wheel-chassis chain converges at 0.99 per sweep).
3. **ABS** is an implicit slip target (an ideal modulator), not 06's ECU that modulates the bound when κ < -1.2 κ_peak
   (that cycled and stopped the hatch in 45.0 m, at the edge of the gate).
4. **Engine**: a torque map (the JS curve) on the crank; engine-sim's indicated torque replaces it in M4. **Gearbox**:
   the JS automatic as an AMT, with no torque converter (06 §4.2.3) and no manual mode yet (M4).
5. **Mud** is the JS resistance as a chassis drag row and the JS grip ratio x mudGrip, not the Janosi soil model (06
   §5.3, milestone V4). v2 crawls through the mud patch less far than V0 (table above).
6. **Hill hold** is tested with the driver's auto-hold. 06 P5's "brakes off, in gear" does not apply to an AMT, whose
   clutch opens at idle; it comes back with M4's manual gearbox.
7. **Lockers** have no key yet (M7: lockers and the transfer case); `BrvFleet.SetLockers` for tests and tools.
8. The **dozer** (tracked) stays on V0 until M7.
9. **Perf** numbers are provisional (see For I 2), and this CPU has no E-cores.
10. **Self-traces** run on canon v1 (pure v1, like the M2 golden traces), the canon test world's data.
11. **Snapshot version 2** is a draft until the `m4` tag (M4's coupling may add to the shaft block; ADR-005).
12. **The test-track scene's camera** has no ground collision: lane W's `VehicleCameraRig` and `FreeFlyCamera` read the
    ground from `CanonRuntime` only (For W). The terrain shader blends surfaces, so the asphalt edges look soft.

## For I

1. **`gates.json`**: `unity.playmode.brv-v2` (M3 nightly and milestone, category `BrvV2`) can drop `pending` when this
   branch is merged; the category name is final. It runs the 1,000-seed fuzz (about 2 min), the hill hold, ABS, both
   diff tests, the conformance suite, the self-traces (needs the imported canon) and the v2 snapshot test. The merge
   tier already runs all of them except the 1,000-seed fuzz (category `Slow`).
2. **A perf runner for Unity tests**: `unity-tests.ps1` has no perf mode, so `Perf.Vehicles.StepCost`
   (`Backroads.Tests.Perf.Vehicles.StepCost`, category `Perf`) cannot take `machine.lock`. Suggested: `-Perf` takes
   `machine.lock`, every slot and the GPU lock, waits for an idle machine, refuses on battery, and sets `BR_PERF_GATE=1`
   for Unity. The test then asserts the budget itself (outside such a run it allows 3x the budget). Then a gate step
   (`perf: true`) for M3.
3. **`BuildCli.Scenes`**: add `Assets/Backroads/Scenes/BrvTestTrack.unity` to Dev builds if you want the test track
   in the player (then an autotest can drive it); today it opens in the editor and in PlayMode tests.
4. Two new test assemblies (lane V paths): `Backroads.Tests.Perf.Vehicles` (PlayMode, `Tests/Perf/Vehicles/`) and
   `Backroads.Tests.Perf.EngineSim` (`Tests/Perf/EngineSim/`: `CpuCores`, P-core/E-core detection and thread pinning
   for perf tests; it lives there because Architecture rule 3 allows no `DllImport` under a `/Vehicles/` path).

## For W

- `VehicleCameraRig`, `FreeFlyCamera` and `Ground` take the ground from `CanonRuntime` only, so on the test track (and
  on any other grid) the camera has no ground collision. A "current world grid" that scenes register would fix both.

## Next

- **T3.6** (PhysX chassis), on `m3/v-physx-chassis` cut from this branch: the test-world part first (PhysX statics from
  the 4,013 canon colliders, `Vehicle.BuildingImpact` on the canon test world); the Millbrook statics after T3.3.
- **M4**: SLAVED_CRANK coupling into BRV v2 (engine-sim's indicated torque replaces the torque map), the truck driveline
  and AMT/manual of PLAN M4, launch and grip tuning on the test track (`Vehicle.LaunchContinuity`).

## Resume

```powershell
git -C D:\Backroads-Unity-wt\v log --oneline -6
cd D:\Backroads-Unity-wt\v
tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run              # canon (self-traces, golden traces)
tools\ci\unity-tests.ps1 -Platform EditMode -Category "Vehicle"
tools\ci\unity-tests.ps1 -Platform PlayMode -Category "BrvV2;Perf;!Slow" -NoGraphics  # lane gates without the long fuzz
tools\ci\unity-tests.ps1 -Platform PlayMode -Category "BrvV2" -NoGraphics -TimeoutMin 60   # with the 1,000-seed fuzz
$env:BR_RECORD_SELF_TRACES='1'; tools\ci\unity-tests.ps1 -Platform PlayMode -NoGraphics -TestFilter "Backroads.Tests.PlayMode.Vehicles.Vehicle.SelfTraces"   # only after an intended v2 change
```

Rewrite the scene only after changing its builder: `tools\ci\project-unity.ps1 -Method
Backroads.Vehicles.Editor.BrvTestTrackSceneBuilder.BuildCli`. Run the game on v2 with `-br-brv-v2`; open
`Scenes/BrvTestTrack.unity` in the editor and press Play to drive the track.

## Merge gate

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'V'; tools\ci\gate.ps1 -Tier merge` (lane V paths only), on the
finished card: **pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1223\gate-merge`, on `bc242dd`, main
`c5c147f` merged in): oracle.hash; vendor.check (584 files, 0 differences); native.ctest; unity.editmode 253 passed,
1 skipped, 0 failed (with `Repo.OwnersRespected`); unity.playmode 20 of 20 (the BrvV2 lane gates except the 1,000-seed
fuzz, the test-track scene, the world-edge test, `Perf.Vehicles.StepCost`, every M2 vehicle test); build.servercompile;
build.dev (231 MiB); autotest.smoke. `EngineSimPlugin.dll` restored afterwards. Then the nightly
`Vehicle.StabilityFuzz` on the same code: 1,000 seeds x 60 s, 0 failures (the same numbers as before the port).

Before step 2 (steps 1 and 3 only): **pass, 8 of 8** (run `artifacts\20261022-1139\gate-merge`, on `2ae0cc6`): oracle.hash; vendor.check
(584 files, 0 differences); native.ctest; unity.editmode 211 passed, 1 skipped, 0 failed (with `Repo.OwnersRespected`);
unity.playmode 18 of 18 (the BrvV2 lane gates except the 1,000-seed fuzz, `Perf.Vehicles.StepCost`, every M2 vehicle
test); build.servercompile; build.dev (231 MiB); autotest.smoke. The native step's `EngineSimPlugin.dll` was restored
afterwards (committed only at milestone tags).

The first run failed one test, `Architecture.VehiclesSeesEnginesOnlyThroughInterface`: the perf test's `DllImport`s sat
under a `/Vehicles/` path. Fixed in `2ae0cc6`.

With that Dev build, `tools\ci\autotest.ps1 -Scenario m2.vehicles.drive -ExtraArgs '-br-brv-v2'` **passes on v2**: the
four vehicles settle on the bays (5e-5 m/s), E switches vehicles, the truck drives 10.6 m to 17.7 km/h and brakes to a
stop in 0.95 s over 2.3 m, simulation p95 0.05 ms per frame. Without the switch it gives M2's V0 numbers exactly (15.6 m,
25.95 km/h, 0.74 s, 2.7 m).
