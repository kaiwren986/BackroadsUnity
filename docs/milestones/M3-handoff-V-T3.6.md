# M3 handoff: lane V, card T3.6 (PhysX chassis; statics from both worlds)

Branch `m3/v-physx-chassis` in `D:\Backroads-Unity-wt\v`, cut from `m3/v-brv-core` at `effebf2` (T3.5 done). Commits
`610ce05` (the chassis, the canon statics, the tests), `8e20299` (main `22a87a6` merged in: T3.5 on main, T3.2),
`3f3a9ad` (the engine RTF budget of `FollowInVehicle` counts in a perf run) and this handoff. Nothing was pushed, installed, downloaded or
signed into; Unity ran only through `tools\ci\*.ps1`. `D:\Backroads`, `CanonData\v1` and the shared contracts are
untouched.

**Card status: the test-world part is done; the Millbrook part (statics from the new world's bakes, `BuildingImpact` in
Millbrook) waits for lane W's T3.3 scene**, which is not on main yet (see Next).

## In plain words

Vehicles now have real bodies in Unity's physics engine. Every vehicle, on the browser game's model or on BRV v2, is a
PhysX rigid body with a box for its body and a ball at each wheel. The 4,013 trees, rocks, lamps, posts, fences and
buildings of the canon test world are solid PhysX objects instead of the browser game's flat 2D shapes. Hitting a
building stops you at the wall: the truck at 10 or 20 m/s never gets into it and bounces back a little, as in the
browser game. Two trucks driving into each other stop nose to nose. A fast vehicle still snaps a tree and drives on,
as before.

The vehicle model itself is unchanged. It still drives in its own double-precision steps, and PhysX only takes over in
a step where a vehicle actually touches something. So the six golden traces give exactly the same numbers through
PhysX (1e-13 m) as without it: nothing in them touches a static.

The game uses PhysX by default. `-br-js-statics` brings back the browser game's 2D statics (the card's fallback).

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **The chassis** (`BrvPhysxChassis`): per vehicle, V0 and v2 alike, a dynamic `Rigidbody` with `useGravity = false` (BRV applies gravity in its own step), BRV's mass, the principal inertia of its box (`1 / iI`) at the COM, no damping, `ContinuousSpeculative`, sleeping off, `maxDepenetrationVelocity` 3 m/s; a **hull box `UCX_Hull`** on layer `VehicleHull` (the JS box: generated, until the FBX bodies bring their `UCX_` hulls) with the browser game's wall response (restitution 0.2, friction 0.3); a **sphere per wheel** on layer `Wheel` at the hub (the suspension mount minus the current suspension length). The project's matrix lets `Wheel` meet only `Static`, `Vehicle`, `Prop` and `Puppet`; the body excludes `Road` and `TerrainProxy` (the ground is BRV's: heightfield, plates, hull points) | `Vehicles/Runtime/Physx/BrvPhysxChassis.cs`, `BrvChassisBody.cs` |
| 1 | **How BRV and PhysX share a step:** BRV steps the fleet and integrates in doubles exactly as before; `BeforeSimulate` puts each body at its pose from before BRV's integration with BRV's new velocities; PhysX simulates; `AfterSimulate` keeps BRV's result unless PhysX changed the linear velocity (a contact: PhysX returns it bit for bit otherwise; the angular velocity only picks up float noise, checked), and then BRV takes PhysX's pose and velocities and the impulse becomes the vehicle's `impact`. A body hit while its vehicle sleeps wakes it. In the game `VehicleSimSystem` writes in `FixedUpdate` and reads back right after Unity's physics step (`WaitForFixedUpdate`); tests step a scene of their own (`Step(PhysicsScene)`) | `BrvPhysxChassis.cs`, `VehicleSimSystem.cs`, `BrvState.prePos/preQuat` |
| 1 | **BRV in PhysX mode** (`BrvFleet.PhysxCollisions`, `BrvWorld.physx`): the browser game's 2D statics keep only the **tree knock-over** (decided before PhysX sees the tree: the tree's PhysX capsule switches off, the vehicle gets the JS impulse and drives on); every other static and the vehicle pairs are PhysX's | `BrvSim.cs` (`Statics`), `VehicleStepJob.cs`, `BrvFleet.cs`, `BrvWorld.cs` |
| 1 | **Editor validation:** `BrvChassisValidator` (menu *Backroads > Vehicles > Validate chassis (gravity off)*, EditMode `VehicleV2Core.ChassisGravityValidation`) fails any vehicle `Rigidbody` with gravity on in a prefab or a scene (a `BrvChassisBody`, or the `Vehicle` / `VehicleHull` layer); `BrvPhysxChassis.Validate` checks a hierarchy at runtime | `Vehicles/Editor/BrvChassisValidator.cs` |
| 2 | **Statics on the test world** (`PhysxStatics`): the 4,013 canon colliders (`ColliderGrid`) as PhysX statics on layer `Static`: circles as upright capsules (radius r, the straight part from `y0` to `y1`), boxes as boxes (2 hw x (y1 - y0) x 2 hd, turned by `rot`): 3,887 capsules and 126 boxes. Collider i is GameObject i, so a knocked-over tree turns its capsule off. Built by `VehicleSimSystem` for its world (the server too) | `Vehicles/Runtime/Physx/PhysxStatics.cs` |

## Done when, expected vs measured

All on this machine, 2026-10-22. Reports in `artifacts\vehicles\`.

| Gate | Expected | Measured |
|---|---|---|
| PlayMode `Vehicle.BuildingImpact` (headline), test world | the truck driven into a building at 10 and 20 m/s stops at the wall, no tunnelling | **pass** (`building-impact.md`): canon building collider 2 (a 30 m face with a clear, flat approach), the truck rolling square at it from 8 m. V0 at 10 m/s: contact at 9.70 m/s, deepest into the wall **0.000 m**, rebound 1.35 m/s (restitution 0.2), 1 contact step; V0 at 20 m/s: 19.89 m/s, 0.000 m, rebound 3.30 m/s; v2 at 10 and 20 m/s: 0.000 m, rebound 1.40 / 3.34 m/s. The same impact through the game's path (`Vehicle.BuildingImpactInGame`: `VehicleSimSystem`, the 4,013 statics, Unity's own physics step) at 15 m/s: 0.000 m, 2 contact steps |
| `Vehicle.BuildingImpact`, Millbrook | the same in Millbrook | **waits for T3.3** (Next) |
| Every M2 vehicle test still passes on the test world | pass | **pass**: `GoldenTraces`, `SnapshotRoundTrip`, `SettleParked`, `BurstMatchesManaged` unchanged; and **`Vehicle.GoldenTracesPhysx`**: the six traces through the PhysX chassis with the canon statics as PhysX statics: every gate passes with **0 PhysX contact steps** and the M2 deltas (8.2e-13 m ... 1.10 m for the winch, as without PhysX). In the Dev build, with the chassis on by default: `m2.vehicles.drive` **passes with M2's numbers exactly** (the truck drives 15.61 m to 25.95 km/h and stops in 0.74 s over 2.74 m; parked vehicles 1e-4 m/s; simulation p95 0.08 ms), `m2.drive.route9` **passes** (210 m, 0 underruns) |

Also green: `Vehicle.VehiclesCollidePhysx` (two trucks head-on at 8 m/s each, V0 and v2: they meet at step 138-140 and
stop nose to nose, centres 6.53 m apart where the noses touch at 6.50 m; momentum along the road 0 before and after),
`Vehicle.ChassisBodies` (gravity off, BRV's mass and inertia, layers, sizes, the hull excludes `Road`; the validation
catches a body with gravity on), and every BRV v2 lane gate of T3.5 (unchanged: PhysX only enters where vehicles touch
statics or each other).

## Waiting on you

Nothing to decide. (Drive C: ran full during this card; you freed it, 247 GB free since. See For I 3.)

## Where this differs from the card and research 06

1. **BRV keeps integrating the chassis** in doubles; PhysX's pose and velocities are taken only in a step with a
   contact. 06 §3.4 lets PhysX integrate every step. This way V0 stays bit-identical where nothing is touched (the
   golden traces), and PhysX still owns every collision.
2. **Hull**: one generated box per vehicle (the JS box), as the card allows until the FBX bodies arrive.
3. **Wheel spheres** meet statics only (the project's layer matrix); against the ground the wheels stay BRV's rays (wheel
   spherecasts against kerbs and road colliders are M4, `Vehicle.KerbBump`).
4. **Tree knock-over** stays the browser game's rule (BRV decides before PhysX), so a fast vehicle still snaps a tree.
5. **Static props that move** (Prop layer, bales, logs) are statics in M3: none of the canon colliders is dynamic.
6. Outside the card: **`EngineSim.FollowInVehicle.TracksMappedRpmAtHigh`** (lane V's M2 test) checked engine-sim's
   real-time factor against the 2.0 budget in every run. With four lanes on one machine it failed two merge gates
   (RTF 1.71 and 1.90; 2.32-5.14 when run alone). A perf number counts only in a perf run (AGENTS.md), so the 2.0
   budget is now checked under `BR_PERF_GATE=1` and other runs check a real-time floor of 1.25 and log a warning below
   2.0. The rpm tracking check (< 40 rpm RMS) is unchanged.

## For I

1. **`gates.json`**: `unity.playmode.building-impact` can drop `pending` once the Millbrook part lands (the test-world
   half passes now); `Backroads.Tests.PlayMode.Vehicles.Vehicle.BuildingImpact` is the name.
2. **The perf runner for Unity tests** (T3.5 For I 2) would also make `FollowInVehicle`'s RTF budget a real gate again
   (it reads `BR_PERF_GATE=1`).
3. **Drive C: ran full** during this card (about 0.5 GB free; the lanes' temp and caches were under 2 GB, the rest
   was the user's data and a 25 GB page file). Unity runs that write `%TEMP%` failed ("not enough space on the disk":
   a T3.6 gate failed on lane W's canon test). Lane V ran its passing gate with `$env:TEMP` and `$env:TMP` pointed at
   `artifacts\tmp` on D:. The user has since freed C: (247 GB free); nothing was deleted by lane V.

## For W

- **T3.3**: put the Millbrook statics on layer `Static` (grey-box building colliders, then A's `UCX_`; trees; R's road
  furniture; S's props on `Prop` if they can move). The vehicles' chassis then meets them with no further change on
  lane V's side. Road and kerb surfaces stay on `Road` (the chassis ignores them; the wheels' kerb contact is M4).
- Trees in Millbrook: if they should snap like the canon trees, they need a `ColliderGrid` (or a list) with each tree's
  knock-over momentum, as canon v1's; otherwise they are plain statics.

## Next

- **Millbrook part (after T3.3 is on main)**: merge main; switch `VehicleSpawner` (scene name and garage bays) and
  `VehicleSimSystem.Bind` from `CanonRuntime` to lane W's `Backroads.World.ActiveWorld` (Grid, Colliders, Vegetation,
  garage bays; the contract is in W's T3.3 handoff under "For V"), so both the canon test world and Millbrook bind the
  same way; take the Millbrook statics from the world (its scene colliders on `Static`, and a collider grid for the
  trees if W provides one); run `Vehicle.BuildingImpact` on a Millbrook grey-box building (the same test, the world as
  a parameter); re-run the gates; finish this handoff.
- **M4**: wheel spherecasts against kerbs and road colliders; remote vehicles as kinematic puppets (`Puppet`).

## Resume

```powershell
git -C D:\Backroads-Unity-wt\v log --oneline -6
cd D:\Backroads-Unity-wt\v
tools\ci\project-unity.ps1 -Method Backroads.Canon.Editor.ImportCli.Run
tools\ci\unity-tests.ps1 -Platform EditMode -Category "Vehicle"
tools\ci\unity-tests.ps1 -Platform PlayMode -Category "Vehicle;!Slow" -NoGraphics
tools\ci\unity-tests.ps1 -Platform PlayMode -NoGraphics -TestFilter "Backroads.Tests.PlayMode.Vehicles.Vehicle.BuildingImpact"
```

Play with `-br-js-statics` to compare with the browser game's 2D statics.

## Merge gate

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'V'; tools\ci\gate.ps1 -Tier merge` (lane V paths only), with
`$env:TEMP` / `$env:TMP` on D: (For I 3): **pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1354\gate-merge`,
on `3f3a9ad`, main `22a87a6` merged in): oracle.hash; vendor.check (584 files, 0 differences); native.ctest;
unity.editmode 273 passed, 1 skipped, 0 failed (with `Repo.OwnersRespected` and `VehicleV2Core.ChassisGravityValidation`);
unity.playmode 25 of 25 (`BuildingImpact`, `BuildingImpactInGame`, `GoldenTracesPhysx`, `VehiclesCollidePhysx`,
`ChassisBodies`, every M2 and BRV v2 vehicle test); build.servercompile; build.dev (231 MiB); autotest.smoke. Then
`m2.vehicles.drive` and `m2.drive.route9` on that build (above). `EngineSimPlugin.dll` restored afterwards.

Earlier runs: two failed only on `FollowInVehicle`'s RTF under load (Differences 6), one on lane W's canon test
writing to the full C: drive (For I 3), and one did not start because lane W's perf run held `machine.lock` (it waited).
