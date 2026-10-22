# M3 handoff: lane V, card T3.6 (PhysX chassis; statics from both worlds)

Branch `m3/v-physx-chassis` in `D:\Backroads-Unity-wt\v`, cut from `m3/v-brv-core` at `effebf2` (T3.5 done). Commits
`610ce05` (the chassis, the canon statics, the tests), `8e20299` (main `22a87a6` merged in), `3f3a9ad` (the engine RTF
budget of `FollowInVehicle` counts in a perf run), `1805dd6` (this handoff, test-world part; merged into main as
`b5ef1d0`), `1ff460f` (main `54e5362` merged in: lane W's T3.3, the new Millbrook and `ActiveWorld`), `6ebd244` (the
Millbrook part) and this update. Nothing was pushed, installed, downloaded or signed into; Unity ran only through
`tools\ci\*.ps1`. `D:\Backroads`, `CanonData\v1`, `WorldData` and the shared contracts are untouched.

**Card status: done, both worlds.**

## In plain words

Vehicles have real bodies in Unity's physics engine. Every vehicle, on the browser game's model or on BRV v2, is a
PhysX rigid body with a box for its body and a ball at each wheel. The trees, rocks, lamps, posts, fences and buildings
of the world are solid PhysX objects instead of the browser game's flat 2D shapes: the canon test world's 4,013 and the
new Millbrook's 15,867 (266 buildings and 15,601 tree trunks). Hitting a building stops you at the wall: the truck at
10 or 20 m/s never gets into it and bounces back a little, as in the browser game, in both worlds. Two trucks driving
into each other stop nose to nose. A fast vehicle still snaps a tree and drives on, as before.

The vehicles now go where the game is. In the new Millbrook the four M2 vehicles stand on the five garage bays in front
of the new garage (the dozer's bay stays empty until M7) and you start in the truck; on the canon test world they stand
where they always did. When the world goes away (a scene change), its vehicles go with it.

The vehicle model itself is unchanged. It still drives in its own double-precision steps, and PhysX only takes over in
a step where a vehicle actually touches something. So the six golden traces give exactly the same numbers through
PhysX (1e-13 m) as without it: nothing in them touches a static.

The game uses PhysX by default. `-br-js-statics` brings back the browser game's 2D statics (the card's fallback).

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **The chassis** (`BrvPhysxChassis`): per vehicle, V0 and v2 alike, a dynamic `Rigidbody` with `useGravity = false` (BRV applies gravity in its own step), BRV's mass, the principal inertia of its box (`1 / iI`) at the COM, no damping, `ContinuousSpeculative`, sleeping off, `maxDepenetrationVelocity` 3 m/s; a **hull box `UCX_Hull`** on layer `VehicleHull` (the JS box: generated, until the FBX bodies bring their `UCX_` hulls) with the browser game's wall response (restitution 0.2, friction 0.3); a **sphere per wheel** on layer `Wheel` at the hub (the suspension mount minus the current suspension length). The project's matrix lets `Wheel` meet only `Static`, `Vehicle`, `Prop` and `Puppet`; the body excludes `Road`, `TerrainProxy`, `Trigger`, `Water` (the ground is BRV's: heightfield, plates, hull points) and `Default` (see Differences 7) | `Vehicles/Runtime/Physx/BrvPhysxChassis.cs`, `BrvChassisBody.cs` |
| 1 | **How BRV and PhysX share a step:** BRV steps the fleet and integrates in doubles exactly as before; `BeforeSimulate` puts each body at its pose from before BRV's integration with BRV's new velocities; PhysX simulates; `AfterSimulate` keeps BRV's result unless PhysX changed the linear velocity (a contact: PhysX returns it bit for bit otherwise; the angular velocity only picks up float noise, checked), and then BRV takes PhysX's pose and velocities and the impulse becomes the vehicle's `impact`. A body hit while its vehicle sleeps wakes it. In the game `VehicleSimSystem` writes in `FixedUpdate` and reads back right after Unity's physics step (`WaitForFixedUpdate`); tests step a scene of their own (`Step(PhysicsScene)`) | `BrvPhysxChassis.cs`, `VehicleSimSystem.cs`, `BrvState.prePos/preQuat` |
| 1 | **BRV in PhysX mode** (`BrvFleet.PhysxCollisions`, `BrvWorld.physx`): the browser game's 2D statics keep only the **tree knock-over** (decided before PhysX sees the tree: the tree's PhysX capsule switches off, the vehicle gets the JS impulse and drives on); every other static and the vehicle pairs are PhysX's | `BrvSim.cs` (`Statics`), `VehicleStepJob.cs`, `BrvFleet.cs`, `BrvWorld.cs` |
| 1 | **Editor validation:** `BrvChassisValidator` (menu *Backroads > Vehicles > Validate chassis (gravity off)*, EditMode `VehicleV2Core.ChassisGravityValidation`) fails any vehicle `Rigidbody` with gravity on in a prefab or a scene (a `BrvChassisBody`, or the `Vehicle` / `VehicleHull` layer); `BrvPhysxChassis.Validate` checks a hierarchy at runtime | `Vehicles/Editor/BrvChassisValidator.cs` |
| 2 | **Statics from both worlds** (`PhysxStatics`): the world's static collider grid (`ColliderGrid`, JS space) as PhysX statics on layer `Static`: circles as upright capsules (radius r, the straight part from `y0` to `y1`), boxes as boxes (2 hw x (y1 - y0) x 2 hd, turned by `rot`). Canon: 3,887 capsules and 126 boxes; Millbrook: 15,601 capsules and 266 boxes. Collider i is GameObject i, so a knocked-over tree turns its capsule off. Built by `VehicleSimSystem` for its world (the server too) | `Vehicles/Runtime/Physx/PhysxStatics.cs` |
| 2 | **The world the game is in** (lane W's `Backroads.World.ActiveWorld`, T3.3): `VehicleSimSystem.Create` binds `ActiveWorld` (grid, static colliders, vegetation; `WorldName`), and the canon (`CanonRuntime`) only when no world is set (tests and tools that load the canon themselves). `VehicleSpawner` no longer looks for a scene name: it follows `ActiveWorld.Changed`, puts pickup, hatch, rally and truck on `ActiveWorld.GarageBays` 0-3 (VEHICLE_ORDER), settles them, puts the player in the truck, and destroys its vehicles when their world is cleared or replaced. A world without a garage gets none, and a scene that runs its own vehicles keeps them: the BRV test track sets itself as the active world (`"brv-test-track"`, no garage), so cameras and ground queries read it. `FallingTrees` and the `m2.vehicles.drive` scenario read the active world | `Vehicles/Runtime/VehicleSimSystem.cs`, `Vehicles/Client/VehicleSpawner.cs`, `BrvTestTrackWorld.cs`, `FallingTrees.cs`, `Testing/Runtime/Scenarios/Vehicles/VehicleDriveScenario.cs` |

## Done when, expected vs measured

All on this machine, 2026-10-22. Reports in `artifacts\vehicles\`.

| Gate | Expected | Measured |
|---|---|---|
| PlayMode `Vehicle.BuildingImpact` (headline), both worlds | the truck driven into a building at 10 and 20 m/s stops at the wall, no tunnelling, in the test world and in Millbrook | **pass** (`building-impact.md`). The truck rolls square at a building face from 8 m (the first face in collider order with a clear, flat 14 m approach, 12 m from the garage bays). **Canon**, building collider 2 (a 30.4 m face): V0 at 10 m/s contact at 9.70 m/s, deepest into the wall **0.000 m**, rebound 1.35 m/s; V0 at 20: 0.000 m, rebound 3.30; v2 at 10 / 20: 0.000 m, rebound 1.40 / 3.34. **Millbrook**, building collider 0 (an 11.5 m grey-box face): V0 at 10 m/s contact at 9.72 m/s, **0.000 m**, rebound 1.21 m/s; V0 at 20: 0.000 m, rebound 3.23; v2 at 10 / 20: 0.000 m, rebound 1.08 / 2.93. Speed into the wall 0.25 s after contact: negative (rebounding) in all eight |
| Vehicles spawn at the new garage; the M2 vehicles still spawn on the test world | the four M2 vehicles on the world's garage bays | **pass** (`Vehicle.WorldsInGame`: each world set as its root sets it, then the game's own path: `VehicleSpawner`, `VehicleSimSystem`, Unity's physics step). Canon: 4 vehicles, worst 0.010 m off its bay, parked 0.0000 m/s, 4,013 PhysX statics, 0 contact steps while parked. Millbrook: 4 vehicles, worst **0.027 m** off its bay, parked 0.0000 m/s, **15,867** PhysX statics, 0 contact steps while parked, settled in 248 ms. Then the truck into a building at 15 m/s through the game's step: canon 0.000 m into the wall, Millbrook **0.000 m**, stopped (0.02 m/s). Clearing the world removes the vehicles |
| Every M2 vehicle test still passes on the test world | pass | **pass**: `GoldenTraces`, `SnapshotRoundTrip`, `SettleParked`, `BurstMatchesManaged` unchanged; and **`Vehicle.GoldenTracesPhysx`**: the six traces through the PhysX chassis with the canon statics as PhysX statics: every gate passes with **0 PhysX contact steps** and the M2 deltas (8.2e-13 m ... 1.10 m for the winch, as without PhysX). In the Dev build of this gate, with the chassis on by default: `m2.vehicles.drive` **passes with M2's numbers** (the truck drives 15.61 m to 26.0 km/h; simulation p95 0.07 ms), `m2.drive.route9` **passes** (210 m, 0 underruns), and lane W's `m3.shots.millbrook` passes with the four vehicles on the new garage's bays (`vehicles: 4 on the millbrook garage bays, settled in 244 ms; 15867 PhysX statics`; the pickup is in the garage shot) |

Also green: `Vehicle.VehiclesCollidePhysx` (two trucks head-on at 8 m/s each, V0 and v2: they meet at step 138-140 and
stop nose to nose, centres 6.53 m apart where the noses touch at 6.50 m; momentum along the road 0 before and after),
`Vehicle.ChassisBodies` (gravity off, BRV's mass and inertia, layers, sizes, the hull excludes `Road`; the validation
catches a body with gravity on), and every BRV v2 lane gate of T3.5 (unchanged: PhysX only enters where vehicles touch
statics or each other).

## Waiting on you

Nothing to decide.

## Where this differs from the card and research 06

1. **BRV keeps integrating the chassis** in doubles; PhysX's pose and velocities are taken only in a step with a
   contact. 06 §3.4 lets PhysX integrate every step. This way V0 stays bit-identical where nothing is touched (the
   golden traces), and PhysX still owns every collision.
2. **Hull**: one generated box per vehicle (the JS box), as the card allows until the FBX bodies arrive.
3. **Wheel spheres** meet statics only (the project's layer matrix); against the ground the wheels stay BRV's rays (wheel
   spherecasts against kerbs and road colliders are M4, `Vehicle.KerbBump`).
4. **Tree knock-over** stays the browser game's rule (BRV decides before PhysX), so a fast vehicle still snaps a tree.
   Millbrook's tree trunks are in its collider grid as the canon's are, so the same rule applies there (not measured in
   Millbrook).
5. **Static props that move** (Prop layer, bales, logs) are statics in M3: none of the colliders is dynamic.
6. Outside the card: **`EngineSim.FollowInVehicle.TracksMappedRpmAtHigh`** (lane V's M2 test) checked engine-sim's
   real-time factor against the 2.0 budget in every run. With four lanes on one machine it failed two merge gates
   (RTF 1.71 and 1.90; 2.32-5.14 when run alone). A perf number counts only in a perf run (AGENTS.md), so the 2.0
   budget is now checked under `BR_PERF_GATE=1` and other runs check a real-time floor of 1.25 and log a warning below
   2.0. The rpm tracking check (< 40 rpm RMS) is unchanged.
7. **Millbrook's statics come from `ActiveWorld.Colliders`, not its scene colliders.** In the new Millbrook the road
   chunks (asphalt, concrete and kerb faces) and the grey-box buildings carry MeshColliders on `Default`, not on
   `Road` / `Static`. The chassis leaves `Default` out (a hull against the road mesh would fight BRV's ground), and
   meets the buildings and trees as `PhysxStatics` built from the world's collider grid, the same way as on the canon.
   Once lane W puts the road chunks on `Road` and the building colliders on `Static` (For W), nothing changes on lane
   V's side; if the scene's own building colliders on `Static` replace the grid's boxes, `PhysxStatics` can skip boxes.

## For I

1. **`gates.json`**: `unity.playmode.building-impact` can drop `pending` and go hard now (both worlds pass);
   `Backroads.Tests.PlayMode.Vehicles.Vehicle.BuildingImpact` is the name. It needs the WorldData import in the
   worktree (`Backroads.World.Bake.WorldDataImporter.Run`, as W's gates); without it the Millbrook half is inconclusive.
2. **The perf runner for Unity tests** (T3.5 For I 2) would also make `FollowInVehicle`'s RTF budget a real gate again
   (it reads `BR_PERF_GATE=1`).
3. `unity-tests.ps1 -Out` with a relative path writes the results under `Game\` (Unity resolves it against the project)
   while the script looks under the repo root, and reports "wrote no result file". An absolute path works.

## For W

- **Millbrook scene colliders**: put the road chunks on layer `Road` (`RoadChunkLoader.Spawn(..., layer)`) and the
  grey-box and lane A building colliders on `Static` (S's props on `Prop` if they can move). The chassis ignores
  `Default` today (Differences 7), so nothing breaks in between.
- **The Millbrook scene now has the vehicles**: the four M2 vehicles at the garage and the truck's engine, as the canon
  test world has had since M2. `m3.shots.millbrook` (the garage view) and `m3.perf.flythrough` see them;
  `-br-no-vehicles` leaves them out if a shot or a perf run should not have them.
- **Water**: BRV's water is still the canon's sea level (`BrvSim.WATER_Y = 0`). Millbrook's river and lock have their
  own levels; a vehicle driven into them does not float or drown yet. Lane V would take each reach's level from the
  world (a water-level query on `ActiveWorld`, or the reach polygons) in a later card.

## Next

- **M4**: wheel spherecasts against kerbs and road colliders; remote vehicles as kinematic puppets (`Puppet`).
- BRV water from the world's water levels (For W, Water).

## Resume

```powershell
git -C D:\Backroads-Unity-wt\v log --oneline -8
cd D:\Backroads-Unity-wt\v
tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldDataImporter.Run
tools\ci\unity-tests.ps1 -Platform EditMode -Category "Vehicle"
tools\ci\unity-tests.ps1 -Platform PlayMode -Category "Vehicle;!Slow" -NoGraphics
tools\ci\unity-tests.ps1 -Platform PlayMode -TestFilter "Backroads.Tests.PlayMode.Vehicles.Vehicle.BuildingImpact;Backroads.Tests.PlayMode.Vehicles.Vehicle.WorldsInGame"
```

Play with `-br-js-statics` to compare with the browser game's 2D statics.

## Merge gate

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'V'; tools\ci\gate.ps1 -Tier merge` (lane V paths only): **pass,
8 of 8 steps in 825 s** (2026-10-22, run `artifacts\20261022-1518\gate-merge`, on `6ebd244`, main `54e5362` merged in):
oracle.hash; vendor.check (584 files, 0 differences); native.ctest; unity.editmode 342 passed, 1 skipped, 0 failed
(with `Repo.OwnersRespected` and `VehicleV2Core.ChassisGravityValidation`); unity.playmode 27 of 27 (`BuildingImpact` in
both worlds, `WorldsInGame`, `GoldenTracesPhysx`, `VehiclesCollidePhysx`, `ChassisBodies`, every M2 and BRV v2 vehicle
test, and lane W's `ActiveWorldScenes.ServesBothWorlds` with the vehicles now spawning in both scenes); build.servercompile;
build.dev (485 MiB); autotest.smoke. Then, on that build: `m2.vehicles.drive`, `m2.drive.route9` and
`m3.shots.millbrook` pass (above; results in `artifacts\t36\`). `EngineSimPlugin.dll` restored afterwards.

The test-world part's gate (8 of 8 on `3f3a9ad`, run `artifacts\20261022-1354\gate-merge`) is in the history of this
file.
