# 06 — Vehicle physics for Backroads in Unity: offroad, trucks, trailers, race cars, engine-sim coupling

*Task: vehicle-physics. Date: 2026-10-18. Inputs read: `D:\Backroads\js\vehicle.js` (1,008 lines) and `js/world.js` (deformation); engine-sim at `D:\Backroads-ref\engine-sim` @ `85f7c3b` (`include/vehicle.h`, `transmission.h`, `simulator.h`, `piston_engine_simulator.h`, `dynamometer.h`, `starter_motor.h`, `vehicle_drag_constraint.h`, `src/*.cpp` for these, and `dependencies/submodules/simple-2d-constraint-solver`); sibling reports `02-enginesim-build-probe.md` (measured engine-sim CPU cost), `03-port-map.md` (§4.4, §4.14 to §4.16) and `04-unity-stack-online.md` (netcode). Nothing was built or modified for this report. No sign-ins.*

**Labels used below:** **[code]** = read from source on disk; **[measured]** = measured on this machine (by report 02); **[doc]** = vendor documentation or store page (fetched 2026-10-18); **[calc]** = my calculation; **[est]** = my estimate, needs a probe; **[tune]** = starting value for gameplay tuning, not a lab value.

---

## 0. TL;DR: decisions

| # | Decision | Why, in one line |
|---|---|---|
| 1 | **Write our own vehicle physics ("BRV", Backroads Vehicle) in C# + Burst.** Start from the verbatim port of `vehicle.js` (03 §4.14, phase 1), then add the upgrades in this report. Do **not** use `WheelCollider`. Do not build on NWH, VPP or EVP. | engine-sim coupling needs us to own the driveline integration, and mud needs us to own the tyre–terrain contact. No asset gives both, and the JS model already has the feel the user likes. |
| 2 | **Physics rate:** `Time.fixedDeltaTime = 1/120` (JS parity). BRV runs **4 internal substeps (480 Hz)** for wheels, tyres and driveline. PhysX integrates the chassis once per fixed step. | The JS tuning was done at 120 Hz. 480 Hz plus an implicit solver keeps stiff tyres and locked diffs stable. |
| 3 | **One solver for everything that is stiff:** a projected Gauss–Seidel (PGS) pass over *bounded, soft velocity constraints* ("rows"): tyre long/lat, rolling resistance, brakes, clutch, gears, diffs, winch rope, hull-vs-ground. | It is the same idea as the JS tyres and as engine-sim's own `atg_scs` solver. Wheel spin becomes implicit, so it is stable at 0 km/h and holds on slopes. |
| 4 | **Tyre model:** Magic-Formula (Pacejka) curves turned into *secant-stiffness soft constraints*, a friction ellipse, load sensitivity, and a low-speed floor. On soft ground: Janosi–Hanamoto traction plus sinkage-driven resistance. Parameter tables are in §3.7. | You get MF shapes at speed and static friction when parked, with no low-speed jitter. |
| 5 | **engine-sim coupling: Unity owns every rotating shaft, the crankshaft included.** engine-sim runs **asynchronously on its own worker thread** at 20 kHz. Its crank is **slaved to Unity's crank-speed trajectory through a band-limited (~4–8 Hz) virtual dyno** (a PD `ForceGenerator`). It returns *net indicated torque*, averaged per physics step. Unity applies that torque with 1–2 steps (8–17 ms) of latency. | The loop across the thread boundary has very low gain (§4.5), so it is stable by construction. It never blocks the physics thread. Combustion and sound stay fully emergent, and the firing-pulse rpm ripple passes through. |
| 6 | **Remote players' engines use the same slaved path** (replicated rpm trajectory + pedal) at 10 kHz/4. AI traffic uses torque maps baked from engine-sim dyno sweeps. | One code path. The CPU cost is measured in 02 (local ≈ 0.43 core, remote ≈ 0.11 core). |
| 7 | **engine-sim gaps:** no diesel and no turbo **[code]**. Use stand-in engines now, and plan a fork for compression ignition, boost and engine brake. The out-of-bounds `static lastValveLift[8]` must be fixed in a fork (02 §7). | Semis, box trucks and 6x6s otherwise sound like petrol V8s. |
| 8 | **Terrain:** the Burst `WorldGrid` arrays (03 §4.1) are the physics truth for wheels **and** hull contacts. The PhysX terrain collider is only for loose props, and it may lag. Rendering is a separate choice (03 §4.3). Mud is a sinkage model on top of the JS rut functions. | Wheels never wait on a collider rebuild, and nothing in the vehicle feel depends on `SetHeights` cost. |
| 9 | **Online:** vehicles are owner-authoritative (04 §3.2). Terrain deformation is replicated as **integer fixed-point stamps** (bit-exact on every client), with per-tile hashes and tile resend. | Burst float determinism across CPUs is not needed if the stamp math is integer. |
| 10 | **Trailers:** a PhysX `ConfigurableJoint` at the kingpin or ball, the TGS solver, 20 solver iterations. Trailers are BRV vehicles without drive. `ArticulationBody` is a later option for road trains. **Race cars:** same core plus aero maps and a setup sheet. **Damage:** Blender blendshape panels, detachables, and mechanical damage mapped to physics and engine-sim parameters. | |

**Note on the relayed user request (Synty login):** this report does not touch it. Report 03 §7 already says how to handle it. The user signs in to Synty or the Asset Store **himself**, downloads the packs, and tells the agent the folder. Agents must not be given or enter account passwords. Vehicle physics is independent of which art is used (§9.4).

---

## 1. What exists today (`vehicle.js`) and what carries over

Fixed step `STEP = 1/120`, own integrator, impulse-based. Report 03 §4.14 inventories the model in full. Here is what each part becomes in BRV:

| JS feature (file:function) | Keep / change in BRV | Notes |
|---|---|---|
| 3 downward rays per wheel at ±0.55 r against the heightfield (`suspension`, `rayDown`) | **Keep** for terrain (sample `WorldGrid` directly, in Burst). **Add** PhysX `SpherecastCommand` / `RaycastCommand` batches against static colliders (rocks, kerbs, bridges, flatbeds) and take the deepest hit | The JS has no 3D obstacles under wheels; Unity will |
| Spring `F = k·comp + c·compVel` clamped [0, 5mg], slope scaling, bump-stop impulse, anti-roll impulses | **Keep.** Add progressive spring and bump-stop curves and air-spring load levelling for trucks | Explicit springs are fine at 120 Hz (§3.4) |
| PGS tyres, 4 iterations, long + lat rows with `μ·load·dt` limits and a friction circle (`tyres`) | **Keep the solver, change the rows:** add wheel ω as a solver DOF, MF-shaped secant stiffness, ellipse, load sensitivity (§3.6) | This is the single biggest change |
| Drive impulse + "spin" heuristic (no wheel ω state), `engineForce`, `torqueCurve` | **Replace** with the shaft solver (§4.1) driven by engine-sim (§4.4) | engine-sim needs a real crank speed and real wheel speeds |
| Auto gearbox shifting on ground speed, low range ×2.6, `awdOnLow` | **Keep the controller logic** (shift on ground speed, not wheel speed) inside the new transmission controller (§4.2) | That rule was learned the hard way in mud |
| Mud: `mudGrip`, `mudRes = load·(0.04 + 0.12·mud + 0.3s² + 0.12s + 0.1·mud·min(|v|,6))`, sink = BASE − H | **Keep as the starting tuning.** Re-express it as sinkage plus traction (§5.3) | These constants are already tuned for the game's feel |
| Ruts every 2nd step via `W.rut` with berms and directional spoil (`deform`) | **Keep** (03 §4.4 ports `rut` verbatim). Make the networked version integer (§5.6) | |
| Hull points vs heightfield, PGS, scraping friction, mud ruts from hull (`hullContacts`) | **Keep in BRV** (rows in the same solver). PhysX chassis colliders **ignore the terrain layer** (§5.2) | Removes collider-lag problems with deep sinkage |
| Statics: 2D OBB SAT vs circles and boxes (`statics`, `resolveStatic`); vehicle–vehicle SAT | **Replace** with PhysX convex hull colliders on the chassis Rigidbody | True 3D collisions with buildings, rocks, other vehicles |
| Winch: pull-only rope, max pull 1.3·m·g, 0.9 m/s reel (`winchStep`) | **Keep** as a row with a motor curve (§5.7) | |
| Water buoyancy and damping (`water`) | **Keep**, plus wheel drag and engine water ingestion (§5.5) | |
| Tracked dozer (`trackSteer`, `bladeWork`) | **Keep** (03 §4.15); tracks become two shafts (§4.2.7) | |
| Sleep after 1.2 s still | **Keep** (also sleep the engine-sim instance's audio for parked AI) | |
| AI: full physics within 175 m, ghosts beyond 225 m (`traffic.js`) | **Keep** the LOD (§4.7) | |

---

## 2. What engine-sim actually exposes for a drivetrain [code]

This matters because the coupling design follows from it.

1. **engine-sim's "car" is a single rotating mass.** `PistonEngineSimulator::loadSimulation` adds one `atg_scs::RigidBody m_vehicleMass`, connected to the output crankshaft by the transmission's `ClutchConstraint` and resisted by a `VehicleDragConstraint`. There are no wheels, no tyre slip, no differentials and no chassis.
   - `Transmission::changeGear(g)` sets that body's inertia to `I = m_car · (r_tire / (diffRatio · gearRatio[g]))²`. It conserves *rotational kinetic energy*, not momentum, across the shift (`src/transmission.cpp`).
   - `Vehicle::getSpeed()` is derived from that energy: `v = sqrt(2·½Iω² / m)`.
   - `Vehicle::linearForceToVirtualTorque(F) = sqrt(I/m)·F`.
2. **The clutch is a bounded velocity constraint.** `atg_scs::ClutchConstraint` is one row, `J = [−1 on body1 θ, +1 on body2 θ]`, so it drives ω₂ − ω₁ → 0. Its torque limits are `[m_minTorque, m_maxTorque]`.
   - `Transmission::update()` sets the limits to `±MaxClutchTorque·clutchPressure`, or 0 in gear −1 (neutral, the default).
   - The default `MaxClutchTorque` is 1000 lb·ft.
   - So it is a Coulomb clutch: locked below capacity, slipping above.
3. **Road load is a resistance-only constraint.** `VehicleDragConstraint` is a velocity row with limits `[−(F_rr + ½ρv²C_dA) converted to "virtual torque", 0]`. It can resist motion but never reverse it.
4. **Dyno and starter are speed servos.**
   - `Dynamometer` (public member `Simulator::m_dyno`) is a velocity row with `v_bias = ±m_rotationSpeed` and limits ±`m_maxTorque` (default 10,000 lb·ft). `m_hold` makes it two-sided.
   - `getTorque()` returns the constraint torque `F_t[0][0]`.
   - `StarterMotor` (public `m_starterMotor`) is the same shape.
5. **Stepping.** `Simulator::startFrame(dt)` computes `m_steps = round(dt·speed/timestep)`. It then **adds or removes 10 % of the steps to keep the synthesizer's input buffer at `m_targetSynthesizerLatency` (0.1 s)**, so engine-sim time is slaved to the audio buffer, not to a physics clock.
   - After that, `simulateStep()` runs once per step: `m_system->process(dt,1)`, `engine->update`, `vehicle->update`, `transmission->update`, then `PistonEngineSimulator::simulateStep_` (ignition, combustion chambers, `m_fluidSimulationSteps` = 8 gas substeps), then `writeToSynthesizer()`.
   - `endFrame()` closes the input block.
   - The default frequency is 10,000 Hz (`simulator.cpp`). Scripts set `simulation_frequency` (10,000 by default in `es/objects/objects.mr`); the EJ25 in report 02 uses 20,000.
6. **Sign convention.** The crank turns with **negative** `v_theta` (`Engine::isSpinningCw()` returns `v_theta <= 0`). `getRpm()` uses `abs`.
7. **Extension points that need no patch.**
   - `Simulator::getSystem()` is public. `RigidBodySystem::addForceGenerator()` is public, and `ForceGenerator::apply(SystemState*)` can add torque through `state->t[body.index]` and read `state->v_theta[...]`.
   - `Engine::setSpeedControl()` / `setThrottle()`, `IgnitionModule::m_enabled` and the public `m_dyno` / `m_starterMotor` are directly usable.
   - `PistonEngineSimulator`'s protected `m_crankshaftFrictionConstraints` can be reached from a subclass (useful for damage, §6.4). `Engine::createSimulator` is 6 lines and can be replicated to instantiate the subclass.
8. **Gaps.**
   - No forced induction and no compression-ignition model. A search of `include/ src/ es/ assets/` for turbo, diesel, supercharg and boost finds nothing relevant.
   - Only SI engines ship: I4, flat-4, V6, V8, V10, V12, radials, Kohler single.
   - No engine-brake (compression-release) valve event.
   - `rand()` is used in `combustion_chamber.cpp:211` and `synthesizer.cpp`, so it is not deterministic.
   - `static double lastValveLift[8]` in `PistonEngineSimulator::writeToSynthesizer()` is shared by all instances and written out of bounds for more than 8 cylinders. Report 02 §7 item 1 found this independently.

**Consequence:** engine-sim's own vehicle model must be switched off: transmission permanently in neutral, so the clutch limits are 0. Everything from the flywheel outward then lives in Unity.

---

## 3. Part 1: wheels, suspension and tyres

### 3.1 Unity `WheelCollider`: why not

`WheelCollider` wraps PhysX's vehicle wheel query and suspension. Unity 6 still uses PhysX 4.1 for 3D as far as I know; verify in the 6000.3 or 6000.5 release notes.

| Limitation | Effect on Backroads |
|---|---|
| One **raycast** per wheel along the suspension axis. No width, no sweep, contact only on that ray | Wheels clip through kerbs, rocks and rut walls. No climbing a step with the front of the tyre. Snaps on sharp edges |
| Friction is a two-segment `WheelFrictionCurve` (extremum/asymptote + stiffness), applied internally | No MF shape, no load sensitivity control, no soft-soil traction, and no hook for sinkage or mud resistance |
| Torque goes in through `motorTorque` / `brakeTorque` per wheel; you can only read rpm and cannot set ω or couple wheels | No implicit diffs, lockers or clutch lock. Explicit torque splitting between wheels oscillates with locked diffs. Coupling to an external crank is awkward |
| Substeps are global per rigidbody: `ConfigureVehicleSubsteps(speedThreshold, stepsBelow, stepsAbove)` | Cannot substep our driveline in step with the tyres |
| Linear spring/damper with a hard stop at full travel; suspension snaps to full extension with no travel time when airborne; Unity staff say "by design" ([forum](https://forum.unity.com/threads/wheel-collider-issue.625003/)) | Weak for long-travel offroad (trophy trucks, 6x6 articulation) |
| Known Unity 6 regression: wheels sink or lift when Rigidbody settings change, fixed in 6000.0.25f ([thread](https://discussions.unity.com/t/wheel-colliders-drop-through-the-ground-if-any-settings-on-the-rigidbody-are-changed-in-unity-6/952334)) | Fragile |
| Ground is PhysX colliders only | Wheels would need the PhysX terrain collider to be up to date after every rut (§5) |

Verdict (the same as 03 §4.14): **no `WheelCollider`.**

### 3.2 Existing vehicle physics assets (checked 2026-10-18)

| Asset | Price / licence | Wheel and tyre | Drivetrain | External engine? | Mud / deformable terrain | Fit |
|---|---|---|---|---|---|---|
| **NWH Vehicle Physics 2** v14.3.1 (2026-09-23), URP/HDRP only, original Unity 6000.0.80 [doc] | $60, Asset Store "Extension Asset", full C# source | Includes **WheelController 3D**: sphere cast or multicast ground detection, "modified Pacejka" with `FrictionPreset` ScriptableObjects, load-sensitive, progressive springs, dual-speed dampers [doc] | Component powertrain: `EngineComponent` (ICE or electric, turbo/supercharger, idle PID, stall), `ClutchComponent` (automatic, user input, manual; slip torque, creep), `TransmissionComponent` (manual, automatic, CVT, external `shiftDelegate`), `DifferentialComponent` (open, locked, LSD with preload and ramp angles, Torsen TBR), `WheelComponent`; substeps coordinated by `WheelControllerManager`; trailers (`TrailerHitchModule`, `TrailerModule`); mesh `DamageHandler`; Mirror and PUN2 multiplayer [doc] | Not documented. You would edit `EngineComponent`'s torque source in the source code; its powertrain is its own explicit solver | Surface presets only. No sinkage or ruts | Best commercial option. **Useful as a reference** (trailer and damage UX), but replacing its tyre–ground contact and engine means fighting the asset. Not recommended as the base |
| **WheelController 3D** (standalone) v14.3.1 [doc] | $34 | As above | None | n/a | No | Not needed if we write BRV |
| **Vehicle Physics Pro** (Edy) [doc] | Community: free (4 wheels, **1 vehicle**, desktop only). Professional: €590 + €190/yr (only for entities under €200k revenue). Enterprise: €5,900 + €1,900/yr with source | Own tyre model | Block graph: custom blocks derive from `VehiclePhysics.Block` and implement `ComputeStateUpstream` (passes `L` angular momentum, `I` inertia, `Tr` reaction torque upstream) and `EvaluateTorqueDownstream`; inertial blocks add `GetState` / `SetSubstepState` / `GetSubstepDerivative` / `SetState` [doc] | Plausible as a custom engine block, but not documented | No | CE's one-vehicle limit rules out traffic. Pro is expensive and closed |
| **Edy's Vehicle Physics (EVP)** v5.5.2 (2025-01-16) [doc] | $60 | Built on `WheelCollider` | Simple | No | No | No |
| **Realistic Car Controller** v4.5.0 (2025-07-15) [doc] / RCC Pro (~$50, unverified) | $40 | `WheelCollider` (to be confirmed for Pro) | Simple | No | No | No |
| **Randomation Vehicle Physics 2** (GitHub, JustInvoke) | **MIT** (sounds are mixed CC; check them) | Raycast suspension | Gearbox, CVT, automatic/manual | Code is ours to change | No | Old (tested to Unity 2019.2, last push 2022). Readable reference only |
| **TLabVehiclePhysics** (GitHub, TLabAltoh) | MIT | Pacejka + lookup table | Clutch and gears | Code is ours | No | Small, Unity 2022.3, last push 2024. Reference only |
| **sergeymakeev/ArcadeCarPhysics** | MIT (check the repo) | Arcade raycast | None | n/a | No | Arcade feel only |

None of them simulate sinkage, ruts or mud, and none is designed to take an external crankshaft simulator. That decides it: **BRV**. Optionally buy NWH VP2 ($60) to read how a mature product structures trailers, damage, input and FFB. Don't make it a dependency. The Asset Store EULA allows it in a free game, but its source must stay out of any public repo.

### 3.3 Why a custom BRV is not as big as it sounds

- The JS already contains about 70 % of it: suspension, PGS tyres, hull contacts, ruts, winch, water, tracks.
- What is new: wheel and shaft DOFs in the same PGS (about 300 lines), MF curves (about 50), the engine bridge (about 400 C# + 400 C++), trailers (Unity joints) and aero (about 50).
- Everything is plain arrays and a fixed-size loop, which Burst compiles well. Estimated cost: **≤ 30 µs per vehicle per fixed step** at 4 substeps and 12 PGS iterations (24 rows, 12 iterations, 4 substeps ≈ 1,150 row updates of about 20 flops each) [est].

### 3.4 Timestep and loop order

| Setting | Value | Reason |
|---|---|---|
| `Time.fixedDeltaTime` | **1/120 s** | JS parity (03 §4.14); the tuning was done at 120 Hz |
| `Time.maximumDeltaTime` | 0.05 s (at most 6 catch-up steps) | Like the JS's cap of 8 |
| BRV substeps | **4** (480 Hz); race cars may use 6 | Wheel and driveline rows are implicit, so this is for accuracy, not stability |
| PGS iterations per substep | 12, warm-started (λ × 0.9 carried over) | |
| Springs and dampers | explicit, evaluated per substep (h_s ≈ 2.1 ms) | Stiffest case, a GT corner at 3.4 Hz: ω_n·h_s = 21.4 × 0.0021 ≈ 0.045, far inside explicit stability (< 2). Bump stops use the JS impulse [calc] |
| PhysX solver | `Physics.defaultSolverType = TemporalGaussSeidel` (TGS); default iterations 8/2; 20/8 for jointed vehicles | TGS handles joint chains and mass ratios better |
| Rigidbody | `useGravity = false` on BRV bodies (**BRV applies gravity inside its substeps**). `interpolation = Interpolate`. Set `centerOfMass` and `inertiaTensor` explicitly. `maxAngularVelocity = 50`. `collisionDetectionMode = ContinuousSpeculative` | If PhysX added gravity *after* BRV's friction rows, a parked truck would creep about 0.5·g·sinθ·h per step. The JS also adds gravity first |

**Order inside `FixedUpdate`** (one `VehicleSimSystem` for all vehicles):

```csharp
void FixedUpdate() {
    float h = Time.fixedDeltaTime;                        // 1/120
    foreach (var v in active) v.ReadBody();               // pos, rot, linearVelocity, angularVelocity, world inverse inertia
    var q = WheelQueries.Schedule(active);                // Burst: WorldGrid sampling + SpherecastCommand.ScheduleBatch for statics
    foreach (var v in active) v.engine?.PollResults();    // non-blocking: latest engine-sim torque (§4.4)
    q.Complete();
    new VehicleStepJob { h = h, substeps = 4 }.Schedule(active.Count, 1).Complete();  // §3.8
    foreach (var v in active) {
        v.rb.AddForce(v.dP, ForceMode.Impulse);           // net impulse BRV computed (incl. gravity)
        v.rb.AddTorque(v.dL, ForceMode.Impulse);
        v.engine?.PushCommand(v.crankSamples, v.pedal, v.ignition);   // §4.4
        DeformQueue.Add(v.stamps);                        // §5.4, applied after the step
    }
}   // PhysX then integrates and resolves chassis-vs-world collisions
```

BRV works on a **local copy** of the chassis velocity during substeps and writes back one net impulse (03 §4.14 phase 2). Positions are frozen within a fixed step, which is standard for substepped vehicles.

### 3.5 Contact and suspension

For each wheel, in Burst:

1. **Terrain contact:** sample `WorldGrid` heights at 3 points along the circumference (±0.55 r, as in the JS) × 2 across the width (±0.35 w). For each point, `len = t − sqrt(r² − off²)`; take the minimum. The normal comes from `normalAt`. Surface id, mud depth and sink come from the nearest cell.
2. **Static-collider contact:** `SpherecastCommand` with radius `min(r, w/2)` from the wheel top along −suspension axis, max distance = travel + r, on layer mask `Static | Vehicles(flatbeds)`. Convert the hit to "suspension length" the same way, and keep `hit.normal` and the collider's ground velocity (`Rigidbody.GetPointVelocity` if it moves, e.g. a ferry or a flatbed). Take whichever of (1) and (2) is shallower in suspension length.
3. **Suspension force** along the suspension axis `u` (body up):
   - `F = k(x)·x + c(ẋ)·ẋ + F_bump(x)`, clamped to [0, 5mg].
   - `k(x)`: linear, or progressive (`k0·(1 + p·(x/travel)²)`, p ≈ 1–3 for leaf and offroad).
   - `c(ẋ)`: dual-speed (bump `c_b`, rebound `c_r ≈ 1.5–3·c_b`, with a knee at 0.1 m/s).
   - `F_bump = k_bs·(x − 0.85·travel)²` beyond 85 % travel, plus the JS's bump-stop impulse at 100 %.
   - Apply as an impulse at the contact point. Keep the JS's `clamp(n·u, 0.3, 1)` scaling.
4. **Step climbing.** When the contact normal tilts away from `u` (the face of a kerb or rock), add the horizontal part of `F·n` at the contact point. This lets the front of a tyre "climb". Limit it to `|n_h| > 0.3` to avoid jitter on ordinary slopes.
5. **Anti-roll:** `F = k_arb·(x_L − x_R)` per axle (JS).
6. **Solid axles** (pickup rear, trucks, 6x6): run the per-wheel springs as above, then *visually* tilt the axle beam from the two compressions. For rock crawling, optionally make the axle a 2-DOF sub-body (heave + roll) whose position the two wheels share. That is a later upgrade.
7. **Wheel colliders for walls:** each wheel also gets a PhysX `SphereCollider` (radius r, on a layer that collides with walls, props and other vehicles but **not** with Terrain or Ground). Wheels then bump into buildings and other cars without fighting the suspension.

### 3.6 Tyre model: MF-shaped soft constraints

**Per wheel in contact, per substep:**

```
// contact frame
fwd  = normalize(projectOnPlane(steerRot * bodyFwd, n));  side = cross(n, fwd)
vP   = chassisVelAt(cp) - groundVelAt(cp)                  // patch velocity
vx   = dot(vP, fwd);   vy = dot(vP, side)
vref = max(|vx|, V_LOW)                                    // V_LOW = 0.5 m/s  [tune]
Fz   = suspension load (frozen for the fixed step), with load sensitivity:
       muScale = surf.mu * tyre.mu * (1 + tyre.pD2 * (Fz - Fz0)/Fz0)   // pD2 ≈ -0.08 road, -0.15 slick

// slips (wheel ω is a solver DOF, so it changes between iterations)
kappa = (ω*r - vx) / vref
alpha = atan2(vy, vref)             // optional relaxation: alpha' += (vref/σ)*(alpha - alpha')*hs

// MF magnitudes  F = Fz·D·sin(C·atan(Bx − E(Bx − atan Bx)))
FxMF  = Fz*muScale*MF(|kappa|, tyre.long)     FyMF = Fz*muScale*MF(|alpha|, tyre.lat)
// secant stiffness (always > 0), so the soft row reproduces MF at the current slip, stably
Cx = |kappa| > 1e-3 ? FxMF/|kappa| : Fz*muScale*BCD_long
Cy = |alpha| > 1e-3 ? FyMF/|tan(alpha)| : Fz*muScale*BCD_lat
```

**Rows** (soft constraint: `J·v + (γ/h_s)·λ = 0`, bounded; `effMass = 1/(J M⁻¹ Jᵀ + γ/h_s)`):

| Row | Jacobian | Target | Compliance γ | Bounds (impulse per substep) |
|---|---|---|---|---|
| Tyre longitudinal | chassis `+fwd` at cp, wheel shaft `−r` | `vx − ωr = 0` | `vref / Cx` | ±Fx,max·h_s, with Fx,max = muScale·Fz·D_long (peak) |
| Tyre lateral | chassis `+side` at the roll-influence arm `rS` (JS) | `vy = 0` | `vref / Cy` | ±Fy,max·h_s |
| Friction ellipse | after each long/lat pair: if `(λx/λx,max)² + (λy/λy,max)² > 1`, scale both back onto the ellipse | | | |
| Rolling resistance | wheel shaft | `ω = 0` | 0 | ±C_rr·Fz·r·h_s |

Why this works:
- At speed, γ is large and the row acts as an MF spring that saturates at the curve. Past the peak the secant shrinks, so the falloff appears.
- Near standstill, `vref → V_LOW` makes γ small, so the row becomes nearly rigid static friction. Parked trucks hold on a 30 % slope with no jitter. This was the reason the JS used PGS in the first place.
- The longitudinal row includes the wheel's ω, so the classic stiff "wheel spin" ODE is solved implicitly. An explicit 2 ms step is unstable below about 10 m/s for a car wheel [calc: τ = J·v/(C_κ·r²) ≈ 1.2·10/(80,000·0.31²) ≈ 1.6 ms].
- The JS's "low range claws instead of digging" behaviour is no longer a special case. Low range multiplies wheel torque, and the implicit row naturally limits slip until the bound saturates. If the old feel is wanted, a "crawl control" ECU can cap the requested wheel slip at κ_peak.

**Optional extras for race cars:**
- Relaxation length σ ≈ 0.4 m (car) to 0.9 m (truck) on α.
- Camber: `μ_y × (1 − k_γ·(γ − γ_opt)²)` with k_γ ≈ 2 /rad², γ_opt ≈ −2.5°.
- Simple tyre temperature (§6.3).

### 3.7 Tyre parameter tables [calc + tune]

I fitted coefficients so that the peak slip, the normalised cornering/slip stiffness (CS = B·C·D, per unit slip, at Fz0) and the falloff match typical values for each tyre class. A Python fit is in my scratchpad; the formula is `B = CS/(C·D)`, `E = (B·x_p − tan(π/2C)) / (B·x_p − atan(B·x_p))`. For reference, MathWorks' standard table is dry B 10 / C 1.9 / D 1 / E 0.97, wet 12 / 2.3 / 0.82 / 1, snow 5 / 2 / 0.3 / 1, ice 4 / 2 / 0.1 / 1 ([MathWorks](https://www.mathworks.com/help/sdl/ref/tireroadinteractionmagicformula.html)).

**Longitudinal (slip ratio κ), on dry asphalt** (D is μ peak before the surface multiplier):

| Tyre class | B | C | D | E | Peak κ | CS (per unit κ) | μ(κ=0.5)/peak | μ(κ=1)/peak |
|---|---|---|---|---|---|---|---|---|
| Road car (195/55R16) | 12.1 | 1.65 | 1.00 | −0.17 | 0.11 | 20 | 0.71 | 0.62 |
| Race slick | 13.3 | 1.55 | 1.65 | −0.68 | 0.10 | 34 | 0.76 | 0.70 |
| All-terrain (265/70R17) | 10.9 | 1.60 | 0.92 | −0.18 | 0.13 | 16 | 0.77 | 0.69 |
| Mud-terrain (35×12.5) | 9.9 | 1.55 | 0.85 | −0.04 | 0.16 | 13 | 0.85 | 0.76 |
| Truck 22.5" (295/75R22.5) | 11.7 | 1.60 | 0.80 | 0.05 | 0.13 | 15 | 0.79 | 0.70 |
| Rally gravel tyre | 7.5 | 1.50 | 0.80 | −0.13 | 0.22 | 9 | 0.91 | 0.82 |

**Lateral (slip angle α, radians):**

| Tyre class | B | C | D | E | Peak α | CS (/rad) | μ(15°)/peak | μ(30°)/peak |
|---|---|---|---|---|---|---|---|---|
| Road car | 12.8 | 1.40 | 0.95 | −0.63 | 7.5° | 17 | 0.95 | 0.88 |
| Race slick | 12.9 | 1.45 | 1.60 | −1.27 | 6.0° | 30 | 0.89 | 0.83 |
| All-terrain | 10.9 | 1.35 | 0.88 | −0.89 | 9.0° | 13 | 0.97 | 0.92 |
| Mud-terrain | 9.4 | 1.30 | 0.82 | −1.13 | 11° | 10 | 0.99 | 0.95 |
| Truck 22.5" | 9.4 | 1.30 | 0.78 | −1.14 | 11° | 9.5 | 0.99 | 0.95 |
| Rally gravel | 7.7 | 1.25 | 0.78 | −1.18 | 15° | 7.5 | 1.00 | 0.98 |

**Surface multipliers on D, plus rolling resistance** (the JS `GRIP[]` mapped in; the shape stays per tyre) [tune]:

| Surface (JS `SURF`) | μ multiplier, road / AT / MT / truck / gravel / slick | C_rr | Soft-soil model (§5.3)? |
|---|---|---|---|
| ROAD asphalt (1.08 in JS) | 1.0 / 0.95 / 0.9 / 1.0 / 0.85 / 1.0 | 0.012 (truck 0.006) | no |
| CONCRETE (1.02) | 0.97 / 0.93 / 0.88 / 0.97 / 0.85 / 0.95 | 0.012 | no |
| GRAVEL (0.86) | 0.72 / 0.80 / 0.82 / 0.75 / 1.0 / 0.55 | 0.020 | thin (d = 0.03 m) |
| TRACK dirt road (0.9) | 0.75 / 0.85 / 0.88 / 0.78 / 1.0 / 0.55 | 0.025 | thin |
| GRASS (0.8) | 0.62 / 0.72 / 0.80 / 0.65 / 0.85 / 0.45 | 0.05 | yes (turf) |
| DIRT (0.74) | 0.68 / 0.80 / 0.85 / 0.70 / 0.95 / 0.50 | 0.04 | yes |
| SAND (0.64) | 0.55 / 0.68 / 0.70 / 0.58 / 0.75 / 0.40 | 0.08 + sinkage | yes |
| ROCK (0.9) | 0.85 / 0.92 / 0.95 / 0.85 / 0.9 / 0.80 | 0.015 | no |
| MUD (0.44) | from §5.3 (Janosi), multiplied by the JS `mudGrip` as a "lug factor" | sinkage | yes |
| Wet (any), wetness w ∈ [0,1] | × (1 − 0.25·w) asphalt; × (1 − 0.4·w) grass and dirt | | |

Load sensitivity uses `pD2` (road −0.08, AT −0.08, truck −0.05, slick −0.15) with `Fz0` = the tyre's rated static load. The JS wear term `(1 − 0.18·wear·soft)` stays.

### 3.8 The BRV substep, in pseudocode

```csharp
// VehicleStepJob.Execute(i): one vehicle, Burst-compiled
var v = vehicles[i];  float hs = h / substeps;
for (int s = 0; s < substeps; s++) {
    v.linVel += gravity * hs;                                  // BRV owns gravity
    Suspension(ref v, hs);                                     // explicit springs/dampers/ARB -> impulses
    rows.Clear();
    for (each wheel w in contact) AddTyreRows(ref v, w, rows);           // long (chassis+shaft), lat, rolling res.
    AddDrivelineRows(ref v, rows);                             // clutch, gears, diffs, brakes, TC lockup (§4.1)
    AddHullRows(ref v, rows);                                  // hull points vs WorldGrid (JS hullContacts)
    if (v.winch.active) AddWinchRow(ref v, rows);
    // external shaft torques (explicit): engine-sim indicated torque, starter, torque converter, Jake brake
    v.shaftOmega[CRANK] += (v.engineTorque + v.starterTorque - v.jakeTorque) * hs / v.shaftInertia[CRANK];
    TorqueConverter(ref v, hs);                                // §4.2.3, explicit (low stiffness)
    for (int it = 0; it < 12; it++)
        foreach (ref Row r in rows) {
            float jv = r.Dot(v);                               // J·v over chassis (6) + up to 3 shafts
            float dl = -(jv - r.bias + r.gammaOverH * r.lambda) * r.effMass;
            float nl = math.clamp(r.lambda + dl, r.lo, r.hi);
            r.Apply(ref v, nl - r.lambda);  r.lambda = nl;
            if (r.isTyreLat) ProjectFrictionEllipse(ref v, r.pairIndex);
        }
    IntegrateShaftAngles(ref v, hs);
    v.crankSamples[s] = v.shaftOmega[CRANK];                   // trajectory for engine-sim (§4.4)
    AccumulateStamps(ref v, s);                                // rut stamps every 2nd substep pair (JS cadence 60 Hz)
}
v.dP = v.mass * (v.linVel - v.linVel0);  v.dL = InertiaWorld(v) * (v.angVel - v.angVel0);
```

`Row` is a small struct: chassis Jacobian (`float3 lin, ang`), up to 3 `(shaftIndex, coefficient)` pairs, `bias`, `gammaOverH`, `lo`, `hi`, `effMass` and warm-start `lambda`. Its effective mass is `lin·lin/m + angᵀ·I⁻¹_world·ang + Σ jₖ²/Iₖ + γ/h_s`.

---

## 4. Part 2: drivetrain, and coupling it to engine-sim

### 4.1 Shaft model

All rotating parts are 1-D "shaft" bodies (ω, inertia). Connections are rows in the same PGS:

| Element | Row(s) | Bounds | Notes |
|---|---|---|---|
| Crank (engine) | external torque `T_ind` from engine-sim (§4.4), starter row `ω_crank → ω_starter` | starter [0, T_starter] | `J_crank` = engine-sim's calibrated effective inertia (§4.4.4) plus optional extra flywheel |
| Clutch (dry, manual, AMT, race) | `ω_crank − ω_gbIn = 0` | ±T_cap·engagement | T_cap ≈ 1.3–1.6 × peak engine torque (race 2.0). Engagement comes from the pedal or the auto-clutch (§4.2.1) |
| Gearbox | `ω_gbIn − R_g·ω_gbOut = 0` | unbounded | Removed in neutral and **during the shift interval** (torque interrupt) |
| Lumping | Rigid gear chains (gearbox output → propshafts → diff carriers when the transfer case is locked) are merged into one shaft with reflected inertia `Σ Iₖ/Rₖ²` | | Improves PGS conditioning a lot. Rebuild on gear or transfer-case change |
| Transfer case, part-time (locked) | `ω_P − R_tc·ω_front = 0`, `ω_P − R_tc·ω_rear = 0` (or lumped) | unbounded | R_tc = 1.0 high, 2.48–4.0 low (§4.2.5) |
| Centre diff, full-time (open, or LSD or viscous) | `ω_P·R_tc − (b·ω_F + (1−b)·ω_R) = 0` | unbounded | b = front torque share (0.4 typical). Lock row `ω_F − ω_R = 0` with ±T_lock |
| Axle diff, open | `R_fd·ω_carrier − ½(ω_L + ω_R) = 0` | unbounded | Equal torque split falls out of the Jacobian |
| Axle diff, locked (locker) | the open row plus `ω_L − ω_R = 0` | unbounded | Selectable locker = add or remove the second row |
| Axle diff, clutch LSD | plus `ω_L − ω_R = 0` | ±(T_preload + k_ramp·|T_in|), k_ramp = tan(ramp angle)·k_clutch; `|T_in|` = last substep's carrier row λ/h | Separate power/coast ramp (e.g. 45°/60°) |
| Axle diff, Torsen | plus `ω_L − ω_R = 0` | ±((TBR − 1)/(TBR + 1))·|T_in| | TBR 2.5–4 |
| Inter-axle diff (tandem trucks, "power divider") | like a centre diff, b = 0.5, lockable | | |
| Brakes | `ω_wheel = 0` | ±T_brake·pedal·bias (+ handbrake on the rear) | ABS: ECU modulates the bound when κ < −1.2·κ_peak |
| Tyre | §3.6 (couples the wheel shaft and the chassis) | | |

Typical inertias [est]: car wheel+tyre 1.0–1.2 kg·m²; LT 33" 3–4; truck 22.5" single ≈ 16 (dual ≈ 32); 6x6 1300 mm ≈ 60; slick ≈ 2. Gearbox input 0.01–0.03. Propshaft 0.01–0.05.

### 4.2 Driveline components

#### 4.2.1 Clutch control
- **Manual (player pedal):** engagement = 1 − pedal^1.5, rate-limited to 4/s down and 8/s up.
- **Auto-clutch** (keyboard players and AMTs): target engagement from rpm, `e = smoothstep(rpm_idle + 150, rpm_idle + 900 + 1500·throttle, rpm)`. It disengages below `idle + 100` to prevent stalls.
- **Launch control (race):** hold rpm at the set value until release.

#### 4.2.2 Gearboxes

| Type | Behaviour | Shift time (torque interrupt) | Used on |
|---|---|---|---|
| Manual H-pattern | Player shifts; must be in neutral → gear with clutch < 0.3 or rpm matched (else grind and refuse) | player-controlled | hatch, older pickups, 6x6, semi (18-speed "range/splitter" as 2 extra buttons) |
| Sequential (race) | Up/down paddles; ECU blips the throttle (engine-sim pedal 0.6 for 80 ms) on downshift and cuts ignition on upshift | 50–80 ms | rally, GT, trophy truck |
| AMT / automated manual | Sequential plus auto-clutch plus the JS shift logic (shift on *ground speed*, up at 0.9·redline with throttle, down per the JS thresholds) | 300–500 ms (trucks), 150 ms (cars) | semis (Eaton Endurant-style 12-speed), box truck option |
| Torque-converter automatic | Planetary ratios plus a torque converter (§4.2.3); lockup clutch in gears ≥ 3 above about 50 km/h; shift points from a throttle × speed map (JS rule as the default) | 250–400 ms with overlap (torque does not drop to 0) | pickups (10-speed), box truck (Allison-style 6-speed), SUVs |

#### 4.2.3 Torque converter (automatics)
- Pump torque `T_p = sign·(N_pump / K(SR))²`, with N in rpm and K the "K-factor" in rpm/√(N·m). Example: 2,200 rpm stall at 500 N·m gives K ≈ 98.
- Turbine torque `T_t = TR(SR)·T_p`, with `TR = 2.0 − SR/0.85` for SR < 0.85 and 1.0 above (coupling point). SR = N_turbine/N_pump.
- Apply it explicitly between crank and gearbox input as a torque pair. Stiffness is low [calc: dT/dω ≈ 3.8 N·m·s/rad at 2000 rpm, so h_s·3.8/0.3 = 0.026 ≪ 1, stable]. The lockup clutch is a clutch row.
- This gives creep at idle in Drive, realistic launches and soft shifts.

#### 4.2.4 Differentials and lockers (gameplay)
- **Offroad trucks:** open front/rear by default, with selectable lockers (rear, then front) and a lockable inter-axle diff for 6x6 and tandem drives.
- **Locked diffs** add tyre scrub in turns. This is emergent: the locked row forces equal ω, and the tyre rows fight it.
- **Race:** clutch LSD (preload 50–150 N·m, ramps 45/60°) or Torsen (TBR 3). Rally: front Torsen, rear clutch LSD, centre viscous (T_lock = c_v·|Δω|, c_v ≈ 15 N·m·s/rad).

#### 4.2.5 Transfer case and low range [doc/est]

| Vehicle type | High | Low | Mode |
|---|---|---|---|
| Midsize pickup (Ford Ranger / Toyota Hilux class) | 1.0 | 2.48–2.72 | part-time 4H/4L (locked centre), 2H |
| Full-size pickup (F-150 class) | 1.0 | 2.64 | 2H / 4A (clutch centre) / 4H / 4L |
| Rock-crawler (Wrangler Rubicon class) | 1.0 | 4.0 | part-time |
| 6x6 offroad truck (Ural / Tatra / Zetros inspired) | 1.0 | 2.0–2.3 | full-time with a lockable centre diff |
| Box truck, semi | none (deep 1st gear instead) | | |

The JS used ×2.6 on every ratio and capped top gear at 2. Keep "gear cap in low" as an ECU rule on automatics.

#### 4.2.6 AWD modes
- Part-time 4WD: centre locked by rows.
- Full-time: open centre with bias + lock.
- On-demand: front axle connected by a clutch row with `T_lock = k·|Δω|` (wet clutch). This is the modern "4A" mode and the hatch's optional AWD.

#### 4.2.7 Tracked vehicles (dozer)
- Two track shafts (L/R). Each road-wheel contact gets a long row tying the chassis velocity at that contact to the track speed.
- Steering: clutch-brake (brake row on one side) or differential steer (`ω_L − ω_R = k·steer` row).
- Keep the JS's `simple` force model as the fallback until then (03 §4.15).

### 4.3 Coupling engine-sim: the options

engine-sim produces crankshaft dynamics at 10–20 kHz. Unity's driveline and tyres run at 480 Hz. The question is **who integrates the crank** and **what crosses the thread boundary**.

| Option | Who integrates the crank | Crosses the boundary | Must run synchronously? | Stability | Sound and feel | Effort |
|---|---|---|---|---|---|---|
| **A. engine-sim owns crank + clutch** against a *proxy driveline* (two bodies: rotating parts `J_rot/G²` and vehicle `m·r²/G²`, joined by a bounded "tyre grip" clutch, plus resistance torque). The proxy is reset to Unity's speeds every step; Unity applies engine-sim's average clutch torque to its driveline | engine-sim | U→E: proxy inertias, grip capacity, speeds. E→U: clutch torque | **Yes**: a stiff locked-clutch loop with 1-step lag oscillates | OK only if synchronous; resets cause 120 Hz steps in crank speed (audible buzz) unless softened | Clutch dumps fully inside engine-sim | High; engine-sim sits on the physics critical path (≈ 3.6 ms per step at 20k/8 [measured: 434 ms/s ÷ 120]) |
| **B. Port Unity's driveline into engine-sim's `atg_scs` system** (wheels and diffs as engine-sim bodies; tyres as bounded constraints) | engine-sim | Tyre states each step | Yes | Good | Excellent | Very high: duplicates the tyre and diff model in C++ and makes engine-sim the driveline host |
| **C. Unity owns all shafts incl. crank; engine-sim slaved by a rigid speed-hold dyno** (02 §7 item 9) | Unity | U→E: crank speed. E→U: dyno torque | No (async, 1–2 step lag) | Good: engine torque depends weakly on rpm | Firing-pulse rpm ripple is erased (the dyno holds the speed exactly), so idle sounds slightly "too smooth" | Low |
| **D (recommended). C with a band-limited virtual dyno:** a PD torque that tracks Unity's crank speed only below f_c ≈ 4–8 Hz, so engine-sim's crank is free at firing frequencies | Unity (low frequency); engine-sim (high-frequency ripple) | U→E: crank-speed samples (4 per step), pedal, ignition. E→U: **net indicated torque** per step | **No** | Good (§4.5) | Fully emergent combustion, pumping, friction, idle lope, stall; ripple preserved | Low–medium |

### 4.4 Recommended coupling (Option D) in detail

#### 4.4.1 Data flow

```
Unity FixedUpdate k (t_k → t_k + h, h = 1/120 s)
  BRV substeps: crank ω integrated with T_ind(latest available) + clutch/TC rows
  push  Cmd{ t_k, h, ω_crank[0..4] at substep boundaries, pedal, ignition, fuelCut }  ──► SPSC ring ──►
                                                                     engine thread (one per engine-sim instance)
                                                                       for each 20 kHz step in [t_k, t_k+h):
                                                                         ω_cmd = lerp(samples, t)
                                                                         PD ForceGenerator: T_c = −Kp·e − Kd·(ω_es − ω_cmd)
                                                                         simulateStep()
                                                                       Res{ k, T_ind = J_es·Δω_es/h − mean(T_c), rpm, MAP, AFR }
  ◄── SPSC ring ◄─────────────────────────────────────────────────────
Unity FixedUpdate k+1 or k+2: PollResults() → v.engineTorque = T_ind(k)
Audio thread: OnAudioFilterRead ← Synthesizer::readAudioOutput (44.1 kHz int16 → float, resampled to the output rate)
```

- **What engine-sim sees:** a crank whose low-frequency speed follows Unity, with the throttle and ignition the driver gave.
- **What Unity sees:** the torque the combustion, friction and pumping actually produced.
- **The load:** wheel load never crosses the boundary directly. It reaches engine-sim *implicitly*, through the crank trajectory: a heavy load makes Unity's crank accelerate slowly, and engine-sim follows. This answers the task's "engine-sim gets wheel load torque at 100 Hz" question: it gets the *effect* of the load, which is what a real crank feels.

#### 4.4.2 Native side (C++ plugin, our code only)

```cpp
// BackroadsEngine.dll: engine-sim core (as built by probe 02) + this file
class SlavedCrank : public atg_scs::ForceGenerator {        // added with sim->getSystem()->addForceGenerator(&pd)
public:
    atg_scs::RigidBody *crank; double J, kp, kd, cmd = 0, err = 0, lastT = 0; double sgn = -1; // crank spins with v_theta < 0
    void apply(atg_scs::SystemState *s) override {
        const int i = crank->index;
        const double w = sgn * s->v_theta[i];                // + = normal rotation
        double T = -kp * err - kd * (w - cmd);
        T = std::clamp(T, -Tmax, Tmax);                      // Tmax ≈ 4×peak torque; if saturated, also bleed err
        s->t[i] += sgn * T;  lastT = T;
    }
    double Tmax = 2000;
};

void EngineThread(Instance &e) {
    // setup once: engine built from data (02 §4); transmission->changeGear(-1) (neutral => clutch limits 0);
    // m_dyno.m_enabled = false; m_starterMotor.m_enabled = false (starter lives in Unity);
    // ignition->m_enabled per command; Function constructed on main thread first (02 §7 item 2)
    const double dt = 1.0 / e.sim->getSimulationFrequency();          // 1/20000
    for (;;) {
        Cmd c; if (!e.cmds.pop(c)) { e.wait(); continue; }
        e.engine->setSpeedControl(c.pedal);
        e.engine->getIgnitionModule()->m_enabled = c.ignition && !c.fuelCut;
        e.acc += c.h;                                                  // fractional steps: 1/120 s = 166.67 steps
        const int n = (int)std::floor(e.acc / dt);  e.acc -= n * dt;
        e.sim->startFrame(2.0 * n * dt);                               // zero-patch: oversize so >= n steps are allowed
        const double w0 = e.omega();  double sumT = 0;
        for (int k = 0; k < n; ++k) {
            e.pd.cmd = SampleLerp(c.omega, (k + 0.5) / n);             // 5 boundary samples -> linear in time
            e.sim->simulateStep();                                      // processForces() calls pd.apply()
            e.pd.err += (e.omega() - e.pd.cmd) * dt;                   // integrate tracking error ourselves
            sumT += e.pd.lastT;                                         // (crank angle is reset by engine-sim each step)
        }
        e.sim->endFrame();
        Res r { c.index, e.pd.J * (e.omega() - w0) / (n * dt) - sumT / n, e.engine->getRpm(), /* MAP, AFR, ... */ };
        e.results.push(r);
    }
}
```

- **Zero-patch caveat.** `startFrame`'s ±10 % latency adaptation is defeated by oversizing `dt` and stopping after `n` steps. `PistonEngineSimulator::endFrame` then divides intake flow by the wrong frame time; that only affects a UI number.
- In the fork (recommended anyway, see §4.8), add `Simulator::simulateSteps(int n)` and remove the adaptation.
- Audio drift between physics time and the DSP clock is then handled on the Unity side (§4.4.6).

**C API** (to align with 02 §9's sketch):

```c
BRE_API void*  bre_create(const BreEngineDesc* desc, int simHz, int fluidSubsteps, int audio /*0/1*/);
BRE_API void   bre_destroy(void* h);
BRE_API int    bre_push_command(void* h, const BreCmd* c);    // lock-free SPSC, never blocks
BRE_API int    bre_pop_result(void* h, BreRes* r);            // returns 0 if none
BRE_API int    bre_read_audio(void* h, int16_t* dst, int n);  // called from OnAudioFilterRead
BRE_API double bre_inertia(void* h);                          // calibrated J_eff (§4.4.4)
BRE_API int    bre_dyno_sweep(void* h, float* rpm, float* tq, int n, float pedal); // offline torque map for AI (§4.7)
```

#### 4.4.3 Unity side

```csharp
public sealed class EngineSimEngine : IVehicleEngine {
    IntPtr h; double tInd; int lastIndex = -1; int staleSteps;
    public float CrankInertia { get; }                      // from bre_inertia(), plus optional flywheel
    public float IndicatedTorque => (float)tInd;             // consumed by BRV as external crank torque
    public void PollResults() {
        bool got = false;
        while (Native.bre_pop_result(h, out var r) != 0) { tInd = r.torque; lastIndex = r.index; got = true; }
        staleSteps = got ? 0 : staleSteps + 1;
        if (staleSteps > 4) tInd = fallbackMap.Torque(rpm, pedal);   // engine thread starved: degrade gracefully
    }
    public void PushCommand(ReadOnlySpan<float> crankOmega, float pedal, bool ign) { /* fill BreCmd, bre_push_command */ }
}
// IVehicleEngine also has TorqueMapEngine (AI/fallback) and ReplicatedEngine (remote: drives the same native path)
```

#### 4.4.4 Inertia consistency
Unity's crank inertia **must equal** engine-sim's effective crank-train inertia, or `T_ind` is mis-scaled.
- It is not just `Crankshaft::getMomentOfInertia()`: pistons and rods add angle-dependent inertia.
- **Calibrate at load:** with ignition off, apply a known PD target ramp (e.g. 300 → 1,300 rpm in 0.5 s), measure the PD torque, and fit `J_eff = mean(T_c)/α`. Subtract friction from a constant-speed segment.
- Any *extra* flywheel or clutch mass is added only on the Unity side. That is physically correct: it sits downstream of engine-sim's crank.

#### 4.4.5 Gains and engine-sim settings

| Parameter | Value | Reason |
|---|---|---|
| Local engine | 20 kHz, 8 fluid substeps, audio + IR, own P-core-preferred thread | 02: about 0.43 core [measured]; WOT torque accurate (10 kHz is +5 %, 5 kHz +29 %) |
| PD bandwidth f_c | min(8 Hz, f_idle_firing/4), where f_firing = rpm/60 · cyl/2 | Pass the firing pulses (4-cyl idle 25 Hz → f_c ≈ 6 Hz; single-cyl 12 Hz → 3 Hz) |
| K_p, K_d | `K_p = J·(2πf_c)²`, `K_d = 2·0.7·(2πf_c)·J`. For J = 0.3, f_c = 8 Hz: K_p ≈ 760 N·m/rad, K_d ≈ 21 N·m·s/rad | Critically damped tracking |
| Explicit-PD stability inside engine-sim | `K_d·dt/J = 21 × 5e−5 / 0.3 ≈ 0.0035 ≪ 2` [calc] | Safe |
| `T_max` clamp | 4 × peak torque; on saturation, decay `err` by 50 % per step | A missed shift or clutch dump can't fling engine-sim's crank |

#### 4.4.6 Audio clock
- engine-sim time is slaved to physics time, and physics time is real time on average.
- In `OnAudioFilterRead`, keep the synthesizer output ring at about 50–80 ms by resampling 44.1 kHz → `AudioSettings.outputSampleRate` with a ratio adjusted ±0.5 % by a slow PI loop on the fill level.
- Drain the 1 s of pre-filled silence at start (02 §8).
- Lower `Synthesizer::AudioParameters::volume` to about 0.5 to avoid clipping (02 §6.7).
- The pedal goes to engine-sim **immediately**, not lagged. Pedal-to-sound latency is therefore the audio buffer (≈ 50–80 ms), like engine-sim standalone.

### 4.5 Why Option D is stable: numbers [calc]

1. **Crank in Unity with delayed torque.** `J·ω̇ = T_ind(ω(t − τ)) − T_load`, with τ = 1–2 steps (8–17 ms).
   - The engine torque curve's slope is small: about ±150 N·m across about 600 rad/s, so |∂T/∂ω| ≲ 0.5 N·m·s/rad (a little more at the rev limiter).
   - Delayed-feedback loop gain ≈ |∂T/∂ω|·τ/J = 0.5 × 0.017/0.2 ≈ 0.04 ≪ 1. Stable for every engine and gear, including neutral.
   - Everything stiff (clutch lock, tyres, diffs) is *inside* one implicit solver at 480 Hz, never across the thread boundary.
2. **engine-sim's crank tracking.** It is a PD at 4–8 Hz with K_d·dt/J ≈ 0.0035, so it is stable by a factor of about 500. Tracking error at constant acceleration α is `J·α/K_p`. For a 4,000 rpm/s rev (≈ 420 rad/s²): 0.3 × 420/760 ≈ 0.17 rad of angle and about 1 % of speed. Negligible.
3. **No 120 Hz artifacts.** engine-sim's crank speed is never reset. It follows a piecewise-linear (C⁰) command filtered by an 8 Hz second-order loop, so steps at physics rate are attenuated by about (8/120)² ≈ 0.4 % [calc].
4. **Rev limiter overshoot.** With up to 17 ms of torque lag, a free-revving light engine (ω̇ ≈ 1,500 rad/s²) overshoots a torque-based limiter by about 25 rad/s (240 rpm). Use **engine-sim's own spark-cut limiter** (`IgnitionModule` `revLimit`): it acts on engine-sim's crank, which tracks Unity's within 1 %. Add a Unity-side ECU fuel-cut at +150 rpm as the backstop.
5. **Starving.** If the engine thread misses more than 4 steps (a hitch), Unity falls back to the baked torque map (§4.7) for the missing steps. engine-sim then resumes from the latest command and does not try to catch up on stale steps.

**Probe P4 (§8)** must confirm these numbers:
- clutch dump at 6,000 rpm in low-range 1st
- stall by braking in 2nd
- WOT rev in neutral
- idle in Drive against the brakes
- a 36 t semi in top gear at 100 km/h

Pass criteria: no rpm oscillation above ±15 rpm at 120 Hz, and no divergence.

### 4.6 Latency, determinism, and multiplayer

| Topic | Design |
|---|---|
| Throttle → torque at the wheels | ≤ 1 step for the pedal to reach engine-sim, plus 1–2 steps for the torque to return: **8–25 ms**. Imperceptible next to human reaction and the 50–80 ms audio buffer |
| Determinism | **Not required, and not attempted.** PhysX is not cross-machine deterministic, and engine-sim uses `rand()`. Vehicles are owner-authoritative (04 §3.2). Replays record state, not inputs |
| Owner's vehicle | Simulated only by its owner, at full quality |
| State message | Extend 04's compact vehicle state (20 Hz, unreliable) by about 8 bytes of drivetrain state: crank rpm (u16, 0.25 rpm), pedal (u8), engine load (u8), gear (i8), clutch (u8), flags (ignition, starter, lights, horn, brake, diff locks, low range: u8), turbo or Jake state (u8) |
| Remote vehicles (physics) | Kinematic `Rigidbody` puppets driven by snapshot interpolation (`MovePosition`/`MoveRotation`), so local cars bump them physically. On contact, the local side sends an "impulse received" event so the owner applies the reaction. Wheels are cosmetic: local terrain sampling for visual compression, spin from the replicated speed |
| Remote vehicles (engine) | **The same native path, with `ReplicatedEngine`:** the command stream's ω samples come from interpolating replicated rpm (resampled to the physics rate), plus the replicated pedal and ignition. The torque result is ignored. Sound is therefore identical to what the owner hears. Quality: 10 kHz / 4 substeps (0.11 core [measured]) for the nearest 4–8 audible remotes, without per-engine convolution (shared reverb bus, 02 §7 item 6). 5 kHz / 2 or baked loops beyond that |
| Trailers and attachments | Replicate the attach event (trailer id, hitch type) and the trailer's own snapshot. The owner of the tractor owns the trailer while attached |

### 4.7 AI traffic engines and vehicle LOD

| LOD | Range (JS values) | Physics | Engine |
|---|---|---|---|
| 0 | player, and AI within 60 m that you interact with | full BRV, 4 substeps | engine-sim 10k/4 (if one of the audible slots is free), else the torque map |
| 1 | AI within 175 m (JS `R_IN`) | BRV, 2 substeps, 8 PGS iterations, no rut stamping on paved roads | `TorqueMapEngine`: `T(rpm, pedal)` baked from `bre_dyno_sweep` at 20k/8 per engine definition, plus a simple crank inertia. Audio is granular loops recorded offline from engine-sim on an rpm × load grid (every 250 rpm × {0, 50, 100 %}) |
| 2 | beyond 225 m (JS `R_OUT`) | ghost on the lane spline (JS) | none |

Recording loops from engine-sim is fine licence-wise: engine-sim is MIT, and the output audio is ours.

### 4.8 engine-sim fork patch list (vehicle-relevant)

Vendor engine-sim as a fork (MIT) in the plugin repo, not in `D:\Backroads-ref`:

1. Delete `static double lastValveLift[8]` and its write (`src/piston_engine_simulator.cpp:379`, plus the write in the loop). This is an out-of-bounds write for engines with more than 8 cylinders (V10, V12, radial-9). **Required** before shipping any V10 or V12 stand-in.
2. Add `Simulator::simulateSteps(int n)`, without the latency adaptation.
3. Remove the delta include and the stale `m_antialiasingFilters` assert (02 §2).
4. Parameterise the 44.1 kHz audio rate (02 §7 item 4).
5. **Per-cylinder spark enable** (misfire damage) and an exposed crank friction torque (oil starvation).
6. **Forced induction (phase 2).** Give the intake's upstream "atmosphere" a boost pressure `p_boost(t) = p_atm + Δp_max·spool(t)·f(rpm)`, where spool is a first-order lag of the exhaust flow (`getTotalExhaustFlow()` exists), plus a wastegate clamp. A turbo whistle is a synth layer driven by spool.
7. **Compression ignition (phase 3, research).** Fuel-injection timing, ignition-delay correlation, then premixed spike plus diffusion burn. This is a real project: estimate weeks, not days.
8. **Compression-release engine brake (phase 3).** An extra exhaust-valve event near TDC on the compression stroke, switchable. Until then, do it in Unity: retard torque `T_jake ≈ P_jake(rpm)/ω`, with P_jake ≈ 300–450 kW at 2,100 rpm for a 13–15 L engine [est], plus a sampled bark layer.

**Stand-ins until 6–8 exist** (scripts that ship in `assets/engines/`):

| Vehicle | engine-sim stand-in | Comment |
|---|---|---|
| Hatch | `atg-video-1/05_honda_vtec.mr` | NA I4, revvy |
| Rally coupe | `atg-video-2/01_subaru_ej25_eh.mr` (02 already ported it to C++) | flat-4 rumble |
| Pickup | `atg-video-2/07_gm_ls.mr` or `04_60_degree_v6.mr` | V8 or V6 |
| Box truck | `chevrolet/chev_truck_454.mr` | Gas big-block. Real gas medium-duty trucks exist (e.g. 7.3 L gas V8 options), so this is not even a stand-in |
| Semi, 6x6 | `atg-video-2/11_merlin_v12.mr` (after fork patch 1), redline about 3,000 rpm, or the 454 with a heavy flywheel | Placeholder for a 13–15 L diesel. It will sound like a big petrol engine, not a diesel |
| GT race car | `atg-video-2/08_ferrari_f136_v8.mr` | |
| Hypercar or exotic | `atg-video-2/10_lfa_v10.mr` (after fork patch 1) | |

---

## 5. Part 3: deformable terrain, mud and water

### 5.1 Options compared

| Option | Wheel contact | Chassis contact | Rendering | Cost of an edit | Verdict |
|---|---|---|---|---|---|
| **A. Unity `TerrainData` as truth** | `TerrainCollider` raycasts, or `TerrainData.GetInterpolatedHeight` (main thread, managed) | `TerrainCollider` | Unity Terrain | `SetHeights` recomputes LOD and the collider per call; forum reports 0.1–0.2 ms for a 33×33 patch on an i7-5820K, and `SyncHeightmap` stalls on the GPU ([forum](https://discussions.unity.com/t/syncheightmaps-and-applydelayedheightmapmodification-performance/738857), [forum](https://discussions.unity.com/t/terrain-api-performance-questions-requests/758998)) | No: main-thread-only API, 16-bit storage, physics coupled to rebuild cost |
| **B. `WorldGrid` Burst arrays as truth; Terrain or mesh as the view (recommended)** | Direct Burst sampling (immediate, exact) | **BRV hull rows against `WorldGrid`**; PhysX hull colliders ignore the Terrain layer | Either Unity Terrain tiles via budgeted `SetHeightsDelayLOD` ([doc](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/TerrainData.SetHeightsDelayLOD.html): "does not update the LOD information"; call `SyncHeightmap` afterwards) **or** custom chunk meshes (03 §4.3 recommends these for parity) | Physics: microseconds. View: budgeted, may lag 0.1–0.5 s | **Yes.** Vehicle feel is independent of the renderer and of collider cost |
| C. Custom chunk meshes + `MeshCollider` per 16 m tile, re-cooked with `Physics.BakeMesh` in a job | (as B) | MeshCollider | Custom | about 0.1–0.5 ms per tile bake [est] | Fine for loose props; not needed for vehicles under B |
| D. GPU-only deformation (compute or RT stamps + displacement) | Needs `AsyncGPUReadback` (1–3 frames late, not deterministic) | n/a | Displacement shader | ~0 CPU | Visual detail only (fine mud tracks, splatter), never physics truth |

SpinTires/MudRunner, the reference for mud, does the same split ([Game Developer, Zagrebelnyy](https://www.gamedeveloper.com/programming/mud-and-water-of-spintires-mudrunner)):
- 1 km levels in 16 m blocks.
- A coarse base terrain (25×25 per block).
- A 128×128 mud texture per block (12.5 cm) that holds mud height, offset and blend.
- CPU mud–vehicle physics, with "empirical primitives" drawn into the mud layer.
- The GPU renders the heightfield; only blocks near the truck are active.

### 5.2 Recommended terrain architecture (physics view)

- **`WorldGrid`** (03 §4.1): 2049² vertices at 0.5 m.
  - `H`, `BASE` (float); `SURFACE`, `WEAR`, `MUD` (u8).
  - Add `WET` (u8 wetness) and `COMPACT` (u8 compaction count).
  - Add **`DELTA_MM` (int16, mm, relative to BASE)** as the networked deformation layer, with H = BASE + DELTA_MM/1000 kept in sync.
  - Size ≈ 2049² × (4 + 4 + 2 + 5) B ≈ 63 MB.
- **Tiles:** 32×32 cells (16 m, like SpinTires), each with a dirty flag, a version counter and an xxHash32 of `DELTA_MM`.
- **Mud detail layer (optional, visual):** 0.125 m R16 texture per mud tile, GPU-stamped from the same stamps (SpinTires-like look). Physics stays at 0.5 m.
- **Collision layers:**
  - `VehicleHull` × `Terrain`: **off**. BRV hull rows handle it, which allows high-centring in your own ruts with zero lag.
  - `VehicleHull` × `Static`, `Vehicle`, `Prop`: on.
  - `Prop` × `Terrain`: on, against an invisible `TerrainCollider` (or chunk MeshColliders) updated with a budget of ≤ 4 tiles per frame. Loose props can tolerate 0.5 s of lag.
- **View sync:** 03 owns this choice. If Unity Terrain renders, push ≤ 4 dirty tiles per frame with `SetHeightsDelayLOD(col0, row0, float[33,33])` from preallocated arrays, nearest-first. Call `SyncHeightmap()` per terrain tile when it has been dirty and idle for ≥ 2 s.
  - Prefer 4×4 Terrain tiles of 513² over one 2049², so LOD sync and collider updates touch less data [est; probe P2].
  - Unity 6.3 added Terrain Shader Graph, so ruts, wetness and mud tint can come from our own textures ([doc](https://docs.unity3d.com/Manual/terrain-shader-graph.html); tessellation is not supported there, according to a forum reply).

### 5.3 Mud and soft-soil model

It is physically inspired (Bekker pressure–sinkage, Mohr–Coulomb shear with Janosi–Hanamoto slip), uses gameplay constants, and its defaults reproduce the JS.

**Per wheel on a soft cell (soft depth d > 0.02 m):**

```
// ground pressure: inflation + carcass; CTIS lowers p_infl
p_g   = p_infl + p_carcass                         // kPa; p_carcass ≈ 30 (car), 60 (truck)
A     = Fz / (1000 * p_g)                          // m², contact area
L     = A / b                                      // contact length (b = tread width)
// equilibrium sinkage (Bekker-like p = k·z^n, capped by the soft layer, deeper when spinning)
z_eq  = min(d, pow(p_g / soil.k, 1/soil.n)) * (1 + soil.dig * clamp(|kappa| - 0.2, 0, 1))
z_eq  = min(z_eq, d + 0.05)                         // hard pan below the mud
// the rut is real geometry: deform() lowers H toward (BASE - z_eq) at the JS rate, so the wheel sits in it
// traction cap (Mohr–Coulomb), lugs bite into cohesion; tread clogs with wet mud unless it spins
lug   = tyre.lugFactor * (1 - 0.4*wet*tyre.clogs) * (1 + 0.2*clamp(|kappa|-0.3,0,1)*tyre.selfClean)
Fmax  = A*1000*soil.c*lug + Fz*tan(soil.phi)
// Janosi–Hanamoto, averaged over the patch: monotonic in slip, no peak
mu_soft(kappa) = (Fmax/Fz) * (1 - (soil.K/(|kappa|*L)) * (1 - exp(-|kappa|*L/soil.K)))
// resistance (keep the JS expression; s = sink/r with sink = BASE - H under the wheel)
R_comp = Fz*(0.04 + 0.12*mud + 0.3*s*s + 0.12*s)    // as rolling-resistance row on the wheel shaft
R_visc = Fz*0.1*mud*min(|v|, 6)                      // suction: chassis row along fwd at the contact
// blend with the hard-surface MF by soft depth: w_soft = smoothstep(0.02, 0.08, d)
```

- In the tyre long row, the bound uses `lerp(MF_peak, mu_soft(κ), w_soft)·Fz`, and the secant uses the blended curve.
- The lateral row uses Fmax with its own K_y ≈ 1.5·K.

**Soil table** [tune; Bekker/Wong-style forms with gameplay values, not lab data]:

| Soil (JS surface / MUD byte) | soft depth d (m) | k (kPa/mⁿ) | n | c (kPa) | φ (°) | K (m) | dig | Repeated passes |
|---|---|---|---|---|---|---|---|---|
| Gravel, dirt track | 0.03 | 30,000 | 1.0 | 2 | 38 | 0.015 | 0.2 | compacts (firmer) |
| Dry dirt | 0.06 | 9,000 | 0.9 | 6 | 32 | 0.015 | 0.3 | compacts |
| Grass, turf | 0.08 | 6,000 | 0.9 | 8 | 28 | 0.02 | 0.4 | turns to DIRT at wear > 170 (JS) |
| Sand | 0.30 | 1,500 | 1.1 | 1 | 30 | 0.012 | 0.8 | loosens |
| MUD 0.2–0.6 m | MUD | 900 | 0.8 | 4 | 12 | 0.025 | 1.0 | remoulds: wetter, softer |
| Bog, MUD > 0.6 m | MUD | 450 | 0.7 | 2.5 | 6 | 0.03 | 1.2 | remoulds |
| Wet modifier (WET = w) | | k × (1 − 0.5w) | | c × (1 − 0.5w) | φ − 6w | | | |

**Sanity check** [calc]: a pickup on AT tyres at 220 kPa (p_g ≈ 250) in MUD sinks z = (250/900)^(1/0.8) ≈ 0.20 m (s ≈ 0.5). The same pickup aired down to 100 kPa (p_g ≈ 130) sinks about 0.09 m. A loaded semi at about 760 kPa bottoms out at the hard pan (0.81 m before the cap). A 6x6 with CTIS at 100 kPa (p_g ≈ 160) sinks about 0.12 m, about 0.18·r on its 0.65 m tyres. This gives exactly the gameplay levers wanted: tyres, pressure, lockers and low range all matter.

**Lug factors and tread** [tune]:

| Tyre | lugFactor | clogs | selfClean |
|---|---|---|---|
| road | 1.0 | 1.0 | 0 |
| AT | 1.25 | 0.6 | 0.5 |
| MT | 1.6 | 0.2 | 1.0 |
| truck highway | 1.1 | 0.8 | 0.2 |
| offroad truck | 1.8 | 0.2 | 1.0 |
| chains | 2.2 | 0 | 1.0 |

The JS `mudGrip` (0.75–1.6) maps onto lugFactor.

**Tyre pressure (CTIS):** 6x6 and expedition vehicles get 100–350 kPa from the cockpit. Low pressure gives a larger patch, less sinkage, more cohesion area, more rolling resistance on hard ground, and a de-beading risk above 40 km/h.

### 5.4 Ruts, stamping and the dozer

- Keep `rut()`, `brush()`, `cutBlade()`, the deposits and `relax()` exactly as 03 §4.4 ports them, including directional spoil and berms.
- **Change for online:** the operations run on `DELTA_MM` with integer math (§5.6). The float `H` is derived from it.
- **Cadence:** like the JS, stamp each wheel every 2nd fixed step (60 Hz) when moving or spinning. Hull points in mud stamp every 4th step.
- **Compaction:** each pass increments `COMPACT`. Firm soils multiply k by `(1 + 0.15·COMPACT)` (follow the ruts to drive more easily). Mud and bog *lower* k and raise WET (churned).
- **Recovery (optional, persistent worlds):** ruts relax toward BASE at about 1 mm/min in sun, and WET dries over time.

### 5.5 Water

- **Hull:** buoyancy and damping as in the JS, plus drag `½·ρ_w·C_d·A_sub·v²` with C_d ≈ 1.0 and A_sub = submerged frontal area.
- **Wheels:** extra rolling drag `0.5·ρ_w·b·depth·v²`.
- **Engine:** if the air-intake height (per vehicle; snorkel upgrade raises it) is below the water level, hydro-lock. Set `IgnitionModule::m_enabled = false`, apply a large crank friction (fork patch 5) or force the crank to 0 in Unity, and record damage.
- Wet brakes get μ × 0.6 for 5 s after leaving water.
- WET rises on cells under water and in rut puddles. Optionally, ruts below the local water table fill with puddles, like SpinTires.

### 5.6 Networking terrain deformation

04 §3.2 already chose "wheels emit rut stamps, host merges and rebroadcasts, late joiners get deflate int16 tiles". This report adds what makes that bit-exact and cheap:

1. **Integer stamp format** (16 bytes):

   ```
   struct RutStamp {
     int32 xMm, zMm;            // world position, mm
     u16 radiusMm, depthMm, rateUm;
     u8 bermPct;                // spoil fraction to berm
     u8 flags;                  // dirX/dirZ quantized to 6 bits, tracked, hull
     u8 wearAdd;
     u8 surfHint;
   }
   ```

   The owner quantises *before* applying, and applies the quantised stamp locally. Every machine therefore runs the same integer code on the same integers.
2. **Integer kernel.** `rut()` is rewritten with fixed point: f = 1 − (d/r)² as a Q12 lookup table indexed by `(dx² + dz²)·k`; heights in int32 mm in registers, stored as int16 `DELTA_MM`; the berm share is an integer divide with remainder spread in a fixed order. Integer math is exact on every CPU, so there is no dependence on Burst `FloatMode.Deterministic` (which 03 flagged as "verify").
3. **Order and authority.**
   - Owners send stamp batches (≈ 4 per 100 ms per wheel, merged) to the host with a sequence number.
   - The host appends them to a global log and rebroadcasts. Clients apply other players' stamps in log order; owners skip their own, which they already applied.
   - Divergence is possible only where two vehicles stamp the same cells within one latency window.
   - Every second, the host sends `(tileId, version, hash)` for recently changed tiles. A mismatched client gets the authoritative tile: 32×32 int16 = 2 KB raw, about 0.3–0.8 KB deflated [est].
4. **Bandwidth** [calc]: 4 wheels × 20 merged stamps/s × 16 B ≈ 1.3 KB/s per vehicle while deforming. That is about 10 KB/s downstream with 8 vehicles in mud. Dozer blade ops are stamps too (`cutBlade` parameters are about 24 B).
5. **Late join and persistence:** send all tiles with a non-zero delta (sparse, deflated). A persistent server saves the same set to disk (03 §4.25 already suggests sparse per-chunk deltas).

### 5.7 Winch and recovery gear

- **Rope row** (pull-only), between the hook point and the anchor (a static point, or a point on another body):
  - `C = |p_hook − p_anchor| − L ≤ 0`
  - bias `min(1.2, 3·(dist − L))` (JS Baumgarte)
  - compliance `γ = L/(EA)` (rope stretch: synthetic EA ≈ 3 MN, steel ≈ 8 MN)
  - bounds [0, T_rated·h]
- **Drum motor curve:** reeling speed `v_line = v0·(1 − T/T_stall)`, with v0 ≈ 0.15 m/s (realistic) or 0.9 m/s (JS gameplay; keep it as a "speed" upgrade). A heavily stuck truck slows the winch, which is realistic and readable.
- **Ratings** [doc-typical]: pickup 12,000 lb (53 kN); heavy truck 10–15 t (100–150 kN); 6x6 PTO winch 15–20 t. The JS's `1.3·m·g` is the default if a vehicle has no rating.
- **Snatch block:** halves speed, doubles pull (an anchor-side pulley object).
- **Tree anchor strength:** the JS `treeStrength()`. Over-tension uproots the tree (knock-over event), which makes it interesting.
- **Kinetic recovery strap and tow rope** between vehicles: the same row with 20 % stretch (low EA) and the bias set to snatch. It makes multiplayer rescue fun.

---

## 6. Part 4: trailers, semis, race cars and damage

### 6.1 Trailers and articulated joints

**The trailer is a BRV vehicle** with no engine: its own Rigidbody, wheel rows, brakes (air-line delay 0.3 s on semis), ABS, landing gear (2 stiff "legs" as suspension rays when lowered), and payload mass and CoM. It is joined by a PhysX `ConfigurableJoint`, created on the trailer, `connectedBody` = towing vehicle.

| Hitch | Linear | Joint axes | Angular limits | Notes |
|---|---|---|---|---|
| **Fifth wheel / kingpin** (semi) | x/y/z `Locked` | `axis = up` (twist = yaw), `secondaryAxis = right` (swing1 = pitch, swing2 = roll) | Twist (yaw) `Limited` ±85° (jackknife stop; the cab collider does the rest); swing1 (pitch) ±12°; swing2 (roll) ±3° with spring 2×10⁶ N·m/rad and damper 10⁵ | Kingpin 0.3–0.6 m ahead of the drive tandem centre (load transfer onto the drive axles). Auto-couple when the kingpin is within 0.15 m and the fifth-wheel height is OK (reverse under) |
| Ball hitch (bumper pull) | Locked | as above | yaw ±80°, pitch ±25°, roll ±20° | Tongue weight 10–15 % of trailer mass |
| Gooseneck / 5th wheel in a pickup bed | Locked | | yaw ±90°, pitch ±15°, roll ±5° | |
| Drawbar trailer with dolly | two joints: hitch eye (ball-like, pitch ±20°, yaw ±70°) + dolly turntable (yaw free ±60°) | | | |
| Road train (2–3 trailers) | joint chain | | | If the chain drifts, switch the whole combination to `ArticulationBody` (reduced coordinates, no drift). The BRV API then targets `ArticulationBody.AddForceAtPosition` |

**Joint settings:**
- `enableCollision = true` with the kingpin area on non-colliding layers.
- `projectionMode = PositionAndRotation` (projection distance 0.05, angle 3°).
- Tractor and trailer `solverIterations = 20`, `solverVelocityIterations = 8`, TGS.

**Mass ratios:** a 9 t tractor with a 30 t trailer is 3.3:1, comfortable for PhysX. Avoid connecting very light bodies (dollies under 300 kg) to very heavy ones; give dollies a realistic 1–1.5 t.

**Semi-specific physics:**
- **Steering:** a single steer axle. Tandem drive axles resist turning through tyre scrub (emergent, gives realistic understeer). Self-steer or lift axles are optional rows.
- **Drive:** 6x4 with an inter-axle diff (lockable) and cross-axle diff locks; offroad 6x6 adds a front axle.
- **Air suspension:** load-levelling (k scales with axle load so f stays at about 1.2 Hz; ride height constant). The air brake system has a reservoir, compressor and spring parking brakes that apply when pressure is low.
- **Retarders:** Jake brake (§4.8), plus an optional hydraulic retarder as a speed-proportional torque on the gearbox output.
- **Trailer sway** is emergent (CoM height, tyre CS, hitch offset). Tankers can get a slosh pendulum: mass 30 % of the liquid, f ≈ 0.6 Hz.

### 6.2 Varied trucks: parameters that change the feel most

| Parameter | Pickup | Box truck | Semi tractor | 6x6 offroad |
|---|---|---|---|---|
| CoG height loaded (m) | 0.85 | 1.4 | 1.2 (tractor), 1.9 (loaded trailer) | 1.5 |
| Mass empty → loaded (t) | 2.15 → 3.15 | 4.5 → 8.8 | 8.6 tractor; 6.8 → 27 trailer; GCW 36.3 (US 80,000 lb) | 11 → 18 |
| Tyre CS lat (/rad) | 13 | 10 | 9.5 | 10 (MT-like) |
| Steering lock and rate | 34°, fast | 40°, slower | 45° (wheel angle), slow, power assisted | 35°, slow |

### 6.3 Race cars: aero and setup

**Aero model** (per car):
- Drag `F_d = ½ρv²·CdA` at the centre of pressure.
- Downforce split into front and rear points: `F_f = ½ρv²·ClA·bal·g_f(h_f)` and `F_r = ½ρv²·ClA·(1 − bal)·g_r(h_r)`, where `g(h)` is a ride-height sensitivity curve (e.g. 1.0 at nominal, 1.1 at −10 mm, falling to 0.6 below the stall height for ground-effect cars).
- Pitch sensitivity shifts `bal` (±2 % per degree).
- **Slipstream:** if within 30 m behind another car and within a 6° cone, reduce CdA by up to 25 % and ClA by up to 15 %, scaled by distance. Replicated positions are enough.
- **DRS/wing:** a ClA/CdA preset switch.

| Car | CdA (m²) | ClA (m²) | Aero balance (front) | Downforce at 200 km/h [calc, ρ = 1.225] |
|---|---|---|---|---|
| Road hatch | 0.65 | −0.05 (lift) | n/a | −10 kg |
| Rally coupe (Rally2-style wing) | 0.75 | 0.6 | 45 % | 115 kg |
| GT3-inspired | 0.95 | 3.0 | 42 % | 580 kg |
| Prototype / formula-inspired | 1.1 | 4.5 | 40 % | 870 kg |
| Trophy truck | 1.6 | 0.3 | n/a | 60 kg |

**Setup sheet** (a `VehicleSetup` ScriptableObject, editable in a garage UI):

| Item | Range | Effect / implementation |
|---|---|---|
| Spring frequency F/R | road 1.2–1.6 Hz, rally 1.8–2.2, GT 2.8–3.6, offroad 0.8–1.3 | `k = m_corner·(2πf)²` |
| Damping ratio (bump/rebound) | road 0.25–0.35, GT 0.5–0.7 | `c = 2ζ·sqrt(k·m_corner)`, rebound 1.5× |
| Anti-roll F/R | 0–150 % | `k_arb` |
| Ride height | ±30 mm | rest length; feeds aero `g(h)` |
| Camber | 0 to −4° | lateral μ peak shift (§3.6 extras) |
| Toe | −0.3 to +0.3° | steer offset per wheel |
| Brake bias | 50–70 % front | split of `T_brake` |
| Diff preload / ramps | 0–200 N·m, 30–85° | LSD rows (§4.1) |
| Final drive and gears | from a ratio set | gearbox ratios |
| Tyre pressure | ±40 kPa | CS × (1 + 0.15·Δp/p0), μ × (1 − 0.1·(Δp/p0)²), rolling resistance |
| Wing angle | 0–10 steps | ClA and CdA from a table |

**Tyre temperature (race mode, optional):**
- Core temperature `T += (P_slip·k_h − (T − T_amb)·k_c·(1 + v/30))·dt`, where `P_slip = |Fx·v_sx| + |Fy·v_sy|`.
- μ multiplier peaks at 1.0 at 85–95 °C (slick) and falls to 0.85 at 50 °C and at 130 °C.
- Wear reduces D slowly. It is cheap and adds strategy.

### 6.4 Damage

Damage fits the stylised (Synty-plus) look and stays cheap online:

| Layer | Implementation | Online |
|---|---|---|
| **Visual panels** | Per-zone blendshapes authored by the Blender generator (front, rear, left, right, roof; 2 levels each). Impact energy per zone (from PhysX `OnCollisionEnter` relative velocity × mass, and BRV hull-row impulses) drives the weights. Optional NWH-style vertex displacement for hero cars, budgeted per frame | 5–10 bytes of zone weights |
| **Detachables** | Bumpers, mirrors, doors, hood as child Rigidbodies on `FixedJoint`s with `breakForce`/`breakTorque`; despawn after 60 s | Detach events |
| **Glass** | Material swap (cracked → missing) | bits |
| **Wheels / suspension** | Bent: toe offset ±2° and radial wobble; broken spring: k × 0.5, ride height −40 mm; puncture: pressure → 0 over 3 s (radius −15 %, μ × 0.6, C_rr × 4, CS × 0.5) | per-wheel byte |
| **Driveline** | Clutch wear (T_cap down with slip energy); gearbox grind on bad shifts; diff overheat with lockers on asphalt | bytes |
| **Engine** (engine-sim) | Radiator hit → temperature rises → ECU power limit (pedal cap) → misfire (fork patch 5: disable one spark plug) → seized (crank friction up). Hydro-lock (§5.5). Oil hit → friction torque × 3 through the subclass access to `m_crankshaftFrictionConstraints`. All of these *sound* right automatically, because engine-sim simulates them | 1–2 bytes |

---

## 7. Vehicle roster: starting parameter tables [doc-typical / est / tune]

"Inspired by" means proportions and numbers only. Naming, likeness and legal questions are covered in 07.

### 7.1 Body, mass, tyres

| id | Inspired by | Curb (kg) / max | CoG h (m) | Wheelbase / track (m) | Layout | Tyre (size, r m, class) | Wheel I (kg·m²) |
|---|---|---|---|---|---|---|---|
| hatch | VW Polo / Toyota Yaris class | 1,150 / 1,550 | 0.52 | 2.55 / 1.50 | FWD (opt. on-demand AWD) | 195/55R16, 0.31, road | 1.1 |
| rally | Toyota GR86 / GR Yaris Rally2 class | 1,250 / 1,350 | 0.48 | 2.57 / 1.55 | RWD or AWD | 205/65R15, 0.32, gravel | 1.2 |
| pickup | Ford Ranger / Toyota Hilux class | 2,150 / 3,150 | 0.80 | 3.27 / 1.62 | 4x4 part-time + rear locker | 265/70R17, 0.40, AT (MT option 35", 0.445) | 3.7 |
| hdpickup | F-250 / Ram 2500 class | 3,300 / 4,600 | 0.90 | 3.85 / 1.75 | 4x4, dual rear option | LT275/70R18, 0.42, AT | 4.5 |
| boxtruck | Isuzu NPR / Ford F-650 class | 4,500 / 8,800 | 1.10 empty / 1.45 loaded | 4.30 / 1.70 (dual rear) | RWD (4x4 variant) | 245/70R19.5, 0.42, truck | 9 (dual 18) |
| semi | Freightliner Cascadia / Volvo VNL / Scania class (6x4) | 8,600 tractor | 1.15 | 5.8 (to tandem centre) / 2.05 | 6x4, inter-axle diff, diff locks | 295/75R22.5, 0.51, truck | 16 (dual 32) |
| trailer53 | 53 ft dry van | 6,800 / 27,000 | 1.4 empty / 1.9 loaded | kingpin → tandem 12.2 | none | 295/75R22.5 duals | 32 |
| offroad6x6 | Ural-4320 / Tatra / Mercedes Zetros class | 11,000 / 18,000 | 1.50 | 3.85 + 1.40 / 2.05 | 6x6 full-time, lockable centre + cross-axle, CTIS | 1300×530-533, 0.65, offroad | 60 |
| trophy | Baja trophy truck class | 2,900 | 0.85 | 3.05 / 2.10 | 4x4 or RWD | 39×13.5R17, 0.495, MT | 12 |
| gt | GT3-inspired (e.g. 911 GT3 R / 296 GT3 class) | 1,300 incl. driver | 0.42 | 2.50 / 1.65 | RWD, clutch LSD | 30/68R18 F (0.34), 31/71R18 R (0.355), slick | 2.0 |
| dozer | Cat D3 class (JS) | 9,000 | 0.85 | tracks | tracked | n/a | n/a |

Chassis inertia: use the JS box formula `I = m·k·(h² + l², w² + l², w² + h²)/12` with the JS `def.inertia` multipliers (1.15–1.6) as the starting point. Override per vehicle once the Blender models carry real component masses.

### 7.2 Suspension and brakes

| id | f F / R (Hz) | k F / R per wheel (kN/m) [calc] | ζ | Travel F/R (m) | ARB F/R (kN/m) | Brake torque F / R per wheel (N·m) |
|---|---|---|---|---|---|---|
| hatch | 1.5 / 1.7 | 27 / 26 | 0.35 (JS used about 0.55) | 0.16 / 0.18 | 9 (JS) | 1,900 / 900 |
| rally | 2.0 / 2.1 | 46 / 49 | 0.5 | 0.22 / 0.22 | 14 (JS) | 2,600 / 1,600 |
| pickup | 1.3 / 1.6 (leaf, empty) | 33 / 38 | 0.3 | 0.22 / 0.25 | 13 (JS) | 4,000 / 2,600 |
| boxtruck | 1.6 / 2.0 empty (1.2 loaded) | 111 / 170 | 0.3 | 0.12 / 0.14 | 52 (JS) | 7,000 / 9,000 |
| semi | 1.6 steer / 1.2 air (levelled) | 283 / load-dependent | 0.25 | 0.10 / 0.12 | 80 | 14,000 / 18,000 (trailer 18,000) |
| offroad6x6 | 1.2 | 91 | 0.3 | 0.30 | 30 | 12,000 |
| trophy | 0.9 (progressive, 3-stage bypass) | 19 base | 0.4 | 0.65 / 0.85 | 0 | 4,000 / 3,000 |
| gt | 3.2 / 3.4 | 117 / 155 | 0.65 | 0.06 / 0.07 | 60 / 45 | 4,500 / 3,000 (bias 60 %) |

### 7.3 Driveline

| id | engine-sim stand-in (§4.8) | Gearbox | Ratios (1st … top), reverse | Final | Transfer low | Clutch or TC |
|---|---|---|---|---|---|---|
| hatch | 05_honda_vtec | 6MT | 3.58 / 2.02 / 1.35 / 1.03 / 0.81 / 0.68, R 3.6 | 4.06 | n/a | T_cap 300 N·m |
| rally | 01_subaru_ej25_eh | 6 sequential | 3.20 / 2.25 / 1.75 / 1.42 / 1.20 / 1.03 | 4.30 | n/a | 450 N·m; front Torsen TBR 3, rear LSD 80 N·m 45/60°, centre viscous 15 N·m·s/rad (AWD) |
| pickup | 07_gm_ls or 04_60_degree_v6 | 10AT (Ford 10R-style) | 4.70 / 2.99 / 2.15 / 1.77 / 1.52 / 1.28 / 1.00 / 0.85 / 0.69 / 0.64, R 4.87 | 3.73 | 2.72 | TC K ≈ 100, TR 2.0, lockup ≥ 3rd |
| boxtruck | chev_truck_454 | 6AT (Allison 2000-style) | 3.10 / 1.81 / 1.41 / 1.00 / 0.71 / 0.61, R 4.49 | 5.29 | n/a | TC K ≈ 110 |
| semi | merlin_v12 (placeholder) | 18-speed manual (Eaton Fuller RTLO-18918B-style) or 12 AMT | 14.40 / 12.29 / 8.56 / 7.30 / 6.05 / 5.16 / 4.38 / 3.74 / 3.20 / 2.73 / 2.29 / 1.95 / 1.62 / 1.38 / 1.17 / 1.00 / 0.86 / 0.73 | 3.55 | n/a | 2,800 N·m (dry twin plate) |
| offroad6x6 | merlin_v12 or 454 heavy flywheel | 5MT × 2-speed TC | 6.4 / 3.8 / 2.3 / 1.4 / 1.0 | 7.3 (incl. hubs) | 2.15 | 1,400 N·m |
| trophy | 07_gm_ls | 3AT (race TH400-style) | 2.48 / 1.48 / 1.00 | 4.86 | n/a | TC K ≈ 130 |
| gt | 08_ferrari_f136_v8 | 6 sequential | 3.08 / 2.19 / 1.72 / 1.41 / 1.19 / 1.03 | 3.40 | n/a | 700 N·m; LSD preload 80 N·m, 45/60° |

**Sanity check** [calc]: the semi in top gear at 1,500 rpm is 157 rad/s ÷ (0.73 × 3.55) × 0.507 m = 30.7 m/s (111 km/h); lowest gear at 2,100 rpm is 220 ÷ (14.40 × 3.55) × 0.507 = 2.2 m/s (7.8 km/h). The pickup in 4L 1st at 3,000 rpm is 314 ÷ (4.70 × 3.73 × 2.72) × 0.40 = 2.6 m/s (9.4 km/h).

---

## 8. Milestones and probes

| Milestone | Content | Done when |
|---|---|---|
| **V0** (with 03 phase 1) | Verbatim JS port at 1/120 (own integrator), parity traces | Golden traces match (03 §3.6) |
| **V1** | PhysX Rigidbody chassis (`useGravity = false`, BRV impulses), wheel spherecasts vs statics, PhysX hull vs buildings, hull vs `WorldGrid` in BRV | Hatch and pickup drive through Millbrook, bump kerbs, and hit buildings |
| **V2** | Shaft solver: wheel ω DOF, clutch, gearbox, TC, open/locked/LSD diffs, transfer case; MF secant tyres; `TorqueMapEngine` | Test suite: 30 % slope hold (creep < 1 mm/s), launch, locked-diff scrub, low-range crawl, ABS stop |
| **V3** | engine-sim plugin (02's build + fork patches 1–3) with Option D coupling, audio | Probe P4 passes; listening test |
| **V4** | Mud/soil model, CTIS, integer stamps, Terrain view sync | Probe P2 numbers within budget; the pickup gets stuck and recovers with a locker or winch |
| **V5** | Trailers, semi, 6x6, winch v2, Jake brake | A loaded 53 ft trailer reversed onto a dock; jackknife recovers; the 6x6 crosses the bog with CTIS |
| **V6** | Race cars: aero, setup, tyre temperature, slipstream; damage layers | GT lap times plausible for the track length; damage readable |
| **V7** | Online: drivetrain state, remote engines, impulse events, stamp log with hashes | 8 players in mud with consistent terrain hashes after 10 min |

**Probes** (each a small scene with a log):

| Probe | Measures | Budget or pass |
|---|---|---|
| P1 | BRV step cost: 1 / 20 / 50 vehicles, Burst, 4 substeps | ≤ 30 µs per vehicle per step on a P-core |
| P2 | `SetHeightsDelayLOD` of 33×33 patches × 4 per frame and `SyncHeightmap` cost, on one 2049² terrain vs 16 tiles of 513²; `TerrainCollider` update included | ≤ 0.5 ms per frame on average; Sync ≤ 2 ms per tile, run when idle |
| P3 | engine-sim J_eff calibration vs analytic, per stand-in engine | < 3 % error, else J is wrong |
| P4 | Coupling stability matrix (§4.5) at 1/120 with 1 and 2 steps of result lag | No ripple > ±15 rpm at 120 Hz; no divergence; stall and restart work |
| P5 | Hill hold and creep: all vehicles parked on 10–35 % grades for 60 s, brakes on and off (in gear) | creep < 1 mm/s with brakes |
| P6 | Integer stamp determinism: the same stamp log on two machines (and a Burst on/off build) | identical tile hashes |

---

## 9. Risks, unknowns, and how to resolve them

1. **Coupling dynamics untested.** Option D is analysed, not yet run. Probe P4 settles it.
   - If ripple shows, lower f_c or average `T_ind` over 2 steps.
   - If torque is noisy at very low rpm (lugging), that is physical (the engine shudders). Low-pass it at 30 Hz for the crank but keep it for camera shake.
2. **engine-sim CPU** is measured (02). The risk is the *player's* PC. Report 02 suggests a quality tier (20k/8, 20k/4, 10k/4 for the local engine) and caps on remote engines. The coupling is the same at any rate.
3. **Diesel and turbo authenticity.** This is a big content gap for trucks. Stand-ins first, the fork later (§4.8 items 6–8). This is the one place where "fully simulate the engines" is not yet achievable for diesels.
4. **PGS convergence** with large shaft inertia ratios. Example: a semi crank of about 3 kg·m², reflected through 51:1 in the lowest gear, is about 7,800 kg·m² at the wheels, against 32 kg·m² for the dual wheels. Low range on the pickup is similar. Lumping rigid chains (§4.1) fixes most of it. If needed, use 20 iterations, or replace the shaft subset with a small dense LDLᵀ solve plus active-set clamping (≤ 16 shafts).
5. **Joint chains for road trains.** Try TGS + projection first, `ArticulationBody` second.
6. **Unity Terrain sync cost** (P2). If it is too slow, render with custom chunks (03's recommendation anyway). Physics doesn't change.
7. **Motor Town's actual model is unknown.** It is UE4, by one developer plus a part-time artist ([Steam FAQ](https://steamcommunity.com/app/1369670/discussions/0/3195864877891895633)), and no tech write-up exists. To my knowledge it has no deformable mud. Treat it as a feel reference only (vehicle variety, trucking, simcade handling). SpinTires is the mud reference.
8. **Gravity inside BRV.** Any code that forgets `useGravity = false` on BRV bodies double-counts gravity. Add an editor validation.
9. **Synty vehicles** (if used for traffic):
   - They need separate wheel meshes with centred pivots (check per pack).
   - Physics never reads the mesh: wheel sockets come from the prefab builder (07), and colliders are simple convex hulls.
   - The licence forbids redistribution, so git-ignore them (03 §7).

---

## 10. Sources

- engine-sim source on disk, `D:\Backroads-ref\engine-sim` @ `85f7c3b`: `include/vehicle.h`, `transmission.h`, `simulator.h`, `piston_engine_simulator.h`, `dynamometer.h`, `starter_motor.h`, `vehicle_drag_constraint.h`, `engine.h`; `src/vehicle.cpp`, `transmission.cpp`, `simulator.cpp`, `piston_engine_simulator.cpp`, `dynamometer.cpp`, `vehicle_drag_constraint.cpp`, `engine.cpp`, `engine_sim_application.cpp`; `dependencies/submodules/simple-2d-constraint-solver/include/{constraint.h, clutch_constraint.h, force_generator.h, system_state.h, rigid_body.h}`, `src/{clutch_constraint.cpp, rigid_body_system.cpp, optimized_nsv_rigid_body_system.cpp}`.
- Backroads JS: `D:\Backroads\js\vehicle.js`, `js/world.js`, `js/traffic.js`.
- Sibling reports: `D:\Backroads\docs\unity\research\02-enginesim-build-probe.md` (measured CPU costs and core issues), `03-port-map.md` (§4.3, §4.4, §4.14–4.16, §7), `04-unity-stack-online.md` (§3.2 authority model, netcode).
- [NWH Vehicle Physics 2, Asset Store](https://assetstore.unity.com/packages/tools/physics/nwh-vehicle-physics-2-166252): $60, v14.3.1, 2026-09-23, URP/HDRP.
- [NWH VP2 manual: VehicleController (powertrain, trailer, damage)](https://www.nwhcoding.com/VehiclePhysics/manual/VehicleController.html) and the [docs index](https://www.nwhcoding.com/VehiclePhysics/).
- [WheelController 3D docs](https://www.nwhcoding.com/WheelController3D/); [Asset Store](https://assetstore.unity.com/packages/tools/physics/wheel-controller-3d-74512): $34.
- [Vehicle Physics Pro licensing](https://vehiclephysics.com/about/licensing/); [VPP custom blocks](https://vehiclephysics.com/advanced/custom-blocks/); [VPP Community Edition](https://assetstore.unity.com/packages/tools/physics/vehiclephysics-pro-community-edition-153556).
- [Edy's Vehicle Physics](https://assetstore.unity.com/packages/slug/403); [Realistic Car Controller](https://assetstore.unity.com/packages/tools/physics/realistic-car-controller-16296).
- [Randomation Vehicle Physics open-source announcement](https://discussions.unity.com/t/randomation-vehicle-physics-2-0-now-open-source-on-github/571805); [TLabVehiclePhysics](https://awesome.ecosyste.ms/projects/github.com%2Ftlabaltoh%2Ftlabvehiclephysics); [ArcadeCarPhysics](https://github.com/sergeymakeev/arcadecarphysics).
- [Unity 6 WheelCollider regression thread](https://discussions.unity.com/t/wheel-colliders-drop-through-the-ground-if-any-settings-on-the-rigidbody-are-changed-in-unity-6/952334); [WheelCollider suspension snapping thread](https://forum.unity.com/threads/wheel-collider-issue.625003/).
- Unity scripting reference: [`TerrainData.SetHeightsDelayLOD`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/TerrainData.SetHeightsDelayLOD.html), [`CopyActiveRenderTextureToHeightmap`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/TerrainData.CopyActiveRenderTextureToHeightmap.html), [`TerrainHeightmapSyncControl`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/TerrainHeightmapSyncControl.html), [`SyncHeightmap`](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/TerrainData.SyncHeightmap.html).
- Terrain performance forum threads: [SyncHeightmap / ApplyDelayedHeightmapModification](https://discussions.unity.com/t/syncheightmaps-and-applydelayedheightmapmodification-performance/738857), [Terrain API performance](https://discussions.unity.com/t/terrain-api-performance-questions-requests/758998), [Unity support: why SetHeights is slow](https://support.unity.com/hc/en-us/articles/205486596-Why-is-the-TerrainData-SetHeight-so-slow).
- [Unity 6.3 Terrain Shader Graph manual](https://docs.unity3d.com/Manual/terrain-shader-graph.html); [announcement thread](https://discussions.unity.com/t/terrain-shaders-in-shader-graph-new-in-unity-6-3/1683627).
- [Mud and water of Spintires: MudRunner (Game Developer)](https://www.gamedeveloper.com/programming/mud-and-water-of-spintires-mudrunner); [80.lv MudRunner article](https://80.lv/articles/mudrunner-porting-a-successful-title-to-console/).
- [MathWorks: Tire-Road Interaction (Magic Formula), typical coefficients](https://www.mathworks.com/help/sdl/ref/tireroadinteractionmagicformula.html).
- [Motor Town developer FAQ (Steam)](https://steamcommunity.com/app/1369670/discussions/0/3195864877891895633).
- Background, not fetched (from memory; verify before relying on exact numbers): Bekker pressure–sinkage and Janosi–Hanamoto shear in J.Y. Wong, *Theory of Ground Vehicles*; Pacejka, *Tire and Vehicle Dynamics*; published gear ratios of Ford 10R80, Allison 2000-series and Eaton Fuller RTLO-18918B; typical transfer-case low ratios (Ford 2.64/2.72, Toyota 2.566, Jeep Rubicon 4.0).
