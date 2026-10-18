# 01: engine-sim core analysis and how it runs inside Unity

*Task: enginesim-core. Date: 2026-10-18.*

*Sources:*
- *engine-sim `D:\Backroads-ref\engine-sim` @ `85f7c3b` (2023-01-22, the last commit of the open-source upstream).*
- *Submodules: piranha `432f0b1`, simple-2d-constraint-solver (`scs`) `e009f4f`, csv-io `2112c55`. delta-studio and direct-to-video are not fetched.*

*Nothing in `D:\Backroads` or `D:\Backroads-ref\engine-sim` was modified. Only this report was written. I built and ran a probe in my scratchpad: `C:\Users\kaiwr\AppData\Local\Temp\claude\D--Backroads\3c88f822-b5dc-4e61-a4a0-41a5ef8b7b47\scratchpad\esprobe\` (see Appendix A). The scratchpad is temporary, so the load-bearing code is reproduced in this report.*

*Machine: Core Ultra 9 275HX, MSVC 19.44 (VS 2022), CMake 4.3.4.*

*Sibling reports this one cross-checks:*
- *`02-enginesim-build-probe.md`: an independent headless build with an EJ25 port. Its numbers agree with mine.*
- *`06-vehicle-physics-offroad.md`: proposes "Option D", in which Unity owns the crank. I tested Option D numerically, §3.6.*

---

## 0. TL;DR

1. **The core is headless-clean.** The `engine-sim` library is 41 `.cpp` files. They contain exactly **one** delta-studio include, `src/synthesizer.cpp:4` (`#include "../include/delta.h"`), and it is unused.
   - The core has no UI, Discord or video code and no csv-io calls.
   - The CMake `target_link_libraries(engine-sim ... delta-basic)` (`CMakeLists.txt:137-140`) is unnecessary.
   - I built the core **without patching any file**: two empty shim headers satisfy the `<delta-studio/...>` includes.
   - The scripting target (`engine-sim-script-interpreter`) depends only on piranha.
2. **piranha needs flex and bison at build time.** Bison must be ≥ 3.2, with the `lalr1.cc` skeleton and the C++ variant; flex needs `%option c++`. **No generated parser files are committed.** piranha also needs Boost.Filesystem, but only in `src/path.cpp`.
   - I wrote a std::filesystem replacement for `path.cpp` (§5.4) and compiled **all of piranha except the one parser-dependent file, plus the 4 scripting sources**, with MSVC and no Boost.
   - The only remaining blocker is running win_flex_bison once.
3. **The simulation API is small.**
   - Lifecycle: `Engine::createSimulator()` → `PistonEngineSimulator::initialize/loadSimulation`, then per frame `startFrame(dt)`, `while (simulateStep()) {}`, `endFrame()`.
   - The default rate is 10 kHz (scripts set 5-40 kHz), with 8 gas substeps per step.
   - Inputs are plain public fields and setters: `Engine::setSpeedControl`, `IgnitionModule::m_enabled`, `StarterMotor::m_enabled`, `Transmission::changeGear/setClutchPressure`, `Dynamometer::m_*`.
4. **Load reaches the crank only through `atg_scs` constraints and force generators.** These are the clutch `ClutchConstraint` to a single reflected-inertia "vehicle" body, `VehicleDragConstraint`, the dyno and the starter.
   - An external physics engine can plug in **without forking**: subclass `PistonEngineSimulator` (its members are `protected`), remove `m_vehicleDrag`, and add an `atg_scs::ForceGenerator` or `Constraint`.
   - Torques are read back from `Constraint::F_t`.
5. **I tested four coupling partitions numerically.** Each was compared with a monolithic 10 kHz reference: an LS V8 with wheelspin launches on μ = 1.0 and 0.35, and a grip cruise. **All four were stable at 50 Hz.**
   - **A, plugin owns crank + clutch + driven-wheel spin:** the most accurate. Mean rpm error was 4-43 rpm and Δv ≤ 0.06 m/s.
   - **C, Unity owns wheel spin and sends a speed reset:** fine.
   - **D, Unity owns the crank and engine-sim follows a PD (06's design):** also stable. engine-sim tracked Unity's crank to 10-36 rpm RMS, but it deviated 270-350 rpm from the reference while bouncing on the rev limiter, because of the 1-step torque lag.
   - **Recommendation:** use **D for the BRV vehicles** proposed in 06. Its multi-axle drivelines with diffs cannot live in `atg_scs`, which supports at most 2 bodies per constraint.
   - Keep **A as the high-fidelity / low-latency mode** for simple drivelines, and **C** for any third-party wheel physics. The ABI in §7 supports all of them.
6. **Audio.**
   - The `Synthesizer` takes one sample per exhaust system per sim step, upsamples linearly to **44.1 kHz** (hard-coded at `simulator.cpp:207`) behind a 1.9 kHz anti-alias filter, and convolves with an impulse response (IR) of ≤ 10,000 taps.
   - It renders on its own `std::thread` behind a mutex and condition variable, to an int16 ring capped at 2000 samples.
   - The app's latency is on the order of 0.15-0.25 s.
   - **For Unity:** don't start the audio thread. Render synchronously on the engine worker right after each step batch, using the public members (`renderSync`, §4.3), and push into a **lock-free SPSC float ring**. The audio thread pulls with `OnAudioFilterRead` or, on Unity 6.3+, an `IAudioGenerator`.
   - Re-initialise the synthesizer at the Unity output rate (48 kHz) and resample the IRs once at load.
   - Absorb clock drift with a ±0.5 % trim of `Synthesizer::setInputSampleRate`.
7. **CPU (measured, one thread, `/O2 /fp:fast /arch:AVX2`):**

   | engine (10 kHz, 8 gas substeps) | physics, % of a core |
   |---|---|
   | I4 | 14.9 % |
   | I6 | 25.0 % |
   | V8 | 34.7 % |
   | V12 | 51.1 % |

   - The stock scalar convolution adds about 12-13 % per exhaust channel. It is **not** auto-vectorised.
   - A hand-written AVX2 FIR cuts that 7.7× (3.92 → 0.51 µs/sample for 10,000 taps).
   - Halving the gas substeps (8 → 4) cuts a V8 from 34.4 to 22.0 µs/step with torque within 1 %.
   - Lowering the sim **frequency** changes the engine (LS: 719 → 466 Nm at 5 kHz), so it is not a free LOD.
   - **Budget:** one full engine for the local player, at most 2-4 nearby remote engines in "follow-rpm" mode, and pre-rendered engine-sim loops for everything else.
8. **No fork is needed to ship v1.** Every upstream defect that matters, including the out-of-bounds `static double lastValveLift[8]`, has a plugin-side workaround: a subclass override, a shim, or a call-order fix (§8).
   - A fork is needed only for **new physics**: diesel / compression ignition, turbo, engine brake.

---

## 1. CMake targets and dependencies (Q1)

### 1.1 `engine-sim` (static, `CMakeLists.txt:47-141`)

**Sources: 41 `.cpp` files in `src/`**

`audio_buffer camshaft crankshaft combustion_chamber connecting_rod convolution_filter cylinder_bank cylinder_head delay_filter derivative_filter direct_throttle_linkage dynamometer engine exhaust_system feedback_comb_filter filter fuel function gas_system gaussian_filter governor ignition_module impulse_response intake jitter_filter leveling_filter low_pass_filter part piston piston_engine_simulator simulator standard_valvetrain starter_motor synthesizer throttle transmission utilities valvetrain vehicle vehicle_drag_constraint vtec_valvetrain`

**Headers**
- The matching `include/*.h` files, plus `application_settings.h` and `units.h`.
- Headers used but not listed in CMake: `constants.h`, `ring_buffer.h`, `butterworth_low_pass_filter.h`, `scs.h`.

**Link line:** `simple-2d-constraint-solver csv-io delta-basic`; include dir `dependencies/submodules`.

**What the core actually needs**
- **scs (`atg_scs`) is the only real dependency.** It comes in through `include/scs.h` → `../dependencies/submodules/simple-2d-constraint-solver/include/scs.h`.
- **csv-io is linked but never used by the core.** Only `src/engine_sim_application.cpp:12` includes `csv_io.h`.
- **delta-basic is linked but never used by the core** (§1.3).
- The core uses no Discord, DTV or piranha code. The `ATG_ENGINE_SIM_{DISCORD_ENABLED,VIDEO_CAPTURE,PIRANHA_ENABLED}` macros appear only in `engine_sim_application.{h,cpp}`.

### 1.2 `engine-sim-script-interpreter` (static, `CMakeLists.txt:145-192`, only if `PIRANHA_ENABLED`)

**Sources:** `scripting/src/{channel_types,compiler,engine_context,language_rules}.cpp`, plus 30 `scripting/include/*_node.h` headers. Most of the builder logic lives in these headers (`actions.h`, `engine_node.h`, …).

**Links:** `csv-io piranha`.
- csv-io is again unused (grep finds no `csv` in `scripting/`).
- It reaches the core only through `scripting/include/engine_sim.h`, which includes core headers.
- It reaches piranha through `scripting/include/piranha.h` → `<piranha/include/piranha.h>`.
- It contains **no delta, ysi or UI includes** (grep for `ys[A-Z]` and `delta` in `scripting/` is empty).

### 1.3 Every delta/ysi/UI include reachable from the core

| file:line | include | used? |
|---|---|---|
| `src/synthesizer.cpp:4` | `#include "../include/delta.h"` → `<delta-studio/include/yds_core.h>` and `<delta-studio/engines/basic/include/delta_basic_engine.h>` (`include/delta.h:4-5`) | **No.** No `ys*`/`dbasic` symbol appears in any core file (grep over all 41 sources and their headers) |

That is the only one.

The remaining delta, UI and DTV includes live in app-only files:
- `include/{engine_sim_application,geometry_generator,shaders,simulation_object,ui_element,ui_utilities}.h`, `include/dtv.h`
- every `*_cluster`, `*_gauge`, `*_object`, `ui_*` and `oscilloscope*` file

None of them is in the `engine-sim` target.

**Verified by building.** My probe CMake compiled the 41 core files and all 29 scs files and linked a benchmark against them.
- The only extra was `shim/delta-studio/include/yds_core.h` and `shim/delta-studio/engines/basic/include/delta_basic_engine.h`, both empty, placed first on the include path.
- The result was **0 errors and 0 warnings at /W1**.
- Report 02 removed the include from a patched copy instead. The shim route needs **no patched source**.

### 1.4 Why the upstream CMake cannot be used as-is

The upstream CMake fails here for four reasons:
1. `dependencies/submodules/CMakeLists.txt:1` runs `add_subdirectory(delta-studio)` unconditionally (the submodule is not fetched), and `set_property(TARGET delta-basic …)` fails right after it.
2. `add_subdirectory(piranha)` (line 26) is unconditional too. piranha then runs `find_package(FLEX/BISON REQUIRED)` (piranha `CMakeLists.txt:31-32`) and `find_package(Boost 1.40 COMPONENTS filesystem REQUIRED)` (line 54).
3. Four CMakeLists `FetchContent` googletest from the network.
4. `engine-sim-app` is always defined.

**Use our own CMake** that lists sources from the untouched tree, as in Appendix A (and 02 §2).

---

## 2. Simulation API and the app loop (Q2)

### 2.1 Classes

- `Simulator` (`include/simulator.h`, `src/simulator.cpp`) holds:
  - the `atg_scs::RigidBodySystem *m_system`
  - the public `Dynamometer m_dyno` and `StarterMotor m_starterMotor`
  - the private `Synthesizer m_synthesizer` (reachable through `synthesizer()`)
  - the step counter

  `Simulator` also declares a private `m_vehicleMass` / `m_vehicleDrag` that nothing uses. The live ones are in `PistonEngineSimulator`.
- `PistonEngineSimulator : Simulator` (`include/piston_engine_simulator.h`) builds the piston, rod and crank constraint graph. It overrides `simulateStep_()` (combustion and gas) and `writeToSynthesizer()`, both `protected virtual`. All its members are `protected`: constraint arrays, `m_vehicleMass`, `m_vehicleDrag`, `m_delayFilters`, `m_engine`, `m_transmission`, `m_vehicle`.
- `Engine : Part` (`include/engine.h`) owns arrays of crankshafts, banks, heads, pistons, rods, chambers, exhausts and intakes. It also owns the `IgnitionModule`, `Fuel` and `Throttle*`.

### 2.2 Lifecycle

| phase | call (file:line) | what happens |
|---|---|---|
| build engine | piranha script → `es_script::Compiler::execute()` → `Output{engine, vehicle, transmission}` (`scripting/src/compiler.cpp`), or C++ (Appendix A) | `Engine::initialize` allocates the parts; the nodes in `scripting/include/*_node.h` fill them |
| create | `Engine::createSimulator(vehicle, transmission)` (`src/engine.cpp:386-396`) | `new PistonEngineSimulator`, `initialize({NsvOptimized})`, `loadSimulation(this, vehicle, transmission)`, `setFluidSimulationSteps(8)` |
| initialize | `Simulator::initialize` (`simulator.cpp:28`) | `OptimizedNsvRigidBodySystem` with `GaussSeidelSleSolver` (max 128 iterations, `m_minDelta` 0.1, `gauss_seidel_sle_solver.cpp:9-10`); allocates 512 dyno torque bins |
| loadSimulation | `PistonEngineSimulator::loadSimulation` (`piston_engine_simulator.cpp:39-186`) | crank `FixedPositionConstraint`s (ks 5000, kd 10); `RotationFrictionConstraint` (±crank friction torque); `ClutchConstraint` links between multiple cranks; `transmission->addToSystem` (clutch: crank ↔ `m_vehicleMass`); `VehicleDragConstraint` (lines 101-102); `m_vehicleMass` body (m = I = 1); per cylinder a `LineConstraint` (bore) and 2 `LinkConstraint`s (rod ends), with the `CombustionChamber` as a `ForceGenerator`; dyno and starter constraints; `placeAndInitialize()` (geometry, gas init, exhaust delay filters, ignition reset); `initializeSynthesizer()` |
| app setup | `EngineSimApplication::loadEngine` (`src/engine_sim_application.cpp:437-510`) | `setSimulationFrequency(engine->getSimulationFrequency())`; synthesizer `AudioParameters` from the engine (`jitter`, `noise`, `hf_gain`); loads each exhaust's IR WAV with delta's `ysWindowsAudioWaveFile` → `synthesizer().initializeImpulseResponse(…, i)`; `startAudioRenderingThread()` |
| per frame | `startFrame(dt)` (`simulator.cpp:64`) | `m_steps = round(dt·speed / (1/f))` (line 75). If the synthesizer input latency < target (0.1 s), `m_steps = (m_steps+1)·1.1`; if it is above, `(m_steps−1)·0.9` (lines 77-86). Also zeroes the intake flow-rate accumulators |
| per step | `simulateStep()` (`simulator.cpp:95-153`) | `m_system->process(h,1)` (rigid bodies, constraints, force generators) → `engine->update` (throttle linkage → intakes) → `vehicle->update` → `transmission->update` (clutch limits **for the next step**, line 111) → filtered speed → crank-angle drift fix → dyno torque binning → `simulateStep_()` → `writeToSynthesizer()` |
| `simulateStep_()` | `piston_engine_simulator.cpp:283-321` | `IgnitionModule::update` → `chamber->ignite()` on spark events → `chamber->update` (volume, valve flow coefficients) → **`m_fluidSimulationSteps` (8) gas substeps** over exhausts, intakes and chamber flows |
| end frame | `PistonEngineSimulator::endFrame` (`:332`) → `Simulator::endFrame` → `synthesizer.endInputBlock()` | normalises intake flow to mol/s; hands the input block to the audio thread |
| teardown | `releaseSimulation()` (ends the audio thread, `system->reset()`, `destroy()`); `delete`; `engine->destroy()` | `PistonEngineSimulator::destroy` (`:346`) **does not** call `Simulator::destroy` / `Synthesizer::destroy` (leak; §8) |

**Frequencies**
- Default `m_simulationFrequency = 10000` (`simulator.cpp:13`).
- The script `simulation_frequency` defaults to 10000 (`es/objects/objects.mr:113`). Shipped engines use 5,000 (Ferrari 412 T2 V12) up to 40,000 (Honda TRX520 single).
- Steps per frame at 60 fps: 167. The app clamps `frame_dt` to [1/200, 1/30] s and uses `1/avgFramerate` clamped to 30-1000 fps (`engine_sim_application.cpp:203, 234-235`).
- The gas dynamics run at 8× the step rate (80 kHz at 10 kHz).

### 2.3 Inputs

| input | how to set it | notes |
|---|---|---|
| throttle | `Engine::setSpeedControl(s ∈ [0,1])` (`engine.cpp:139`) | `DirectThrottleLinkage`: plate value `1 − s^γ` (γ = `throttle_gamma`), applied to all intakes each step in `Engine::update` → `setThrottle`. Plate leakage at idle comes from `idle_throttle_plate_position`. The `Governor` throttle (used by `02_kohler_ch750.mr`) treats `s` as a target speed. The app smooths the pedal per frame: `m_speedSetting = 0.5·target + 0.5·prev` (`:798`) |
| ignition | `engine->getIgnitionModule()->m_enabled` (public bool, **default false**) | The rev limiter is inside `IgnitionModule::update` (spark cut for `limiter_duration` once `|ω| > rev_limit`) |
| starter | `sim->m_starterMotor.m_enabled` | Torque and speed come from the script (`starter_torque`, `starter_speed`). It is a speed-target constraint with one-sided torque limit `m_maxTorque` |
| gear | `transmission->changeGear(int)`: −1 = neutral, 0…n−1 | Energy-conserving: `I' = m_car·(r_tire/(diff·gear))²` and `v' = sqrt(2E/I')` (`transmission.cpp:59-81`) |
| clutch | `transmission->setClutchPressure(p ∈ [0,1])` | Applied in `Transmission::update`: limits `±MaxClutchTorque·p`, or 0 in neutral |
| dyno | `sim->m_dyno.m_enabled / m_hold / m_rotationSpeed` (rad/s, positive) | With `hold && enabled` it is a stiff speed lock (limits ±`m_maxTorque` = 10,000 lb·ft) |
| sim speed / rate | `setSimulationSpeed(x)` (slow motion), `setSimulationFrequency(Hz)` | The synthesizer input rate is reset to `f·speed` each `startFrame` |
| audio | `synthesizer().setAudioParameters({volume, convolution, dF_F_mix, inputSampleNoise, airNoise, levelerTarget, …})` | Mutex-protected |

The app's key handling for all of this is `EngineSimApplication::processEngineInput()` (`:670-926`):
- Q/W/E/R set throttle targets of 0.01, 0.1, 0.2 and 1.0.
- A toggles ignition and S holds the starter.
- D and H toggle the dyno; G with the scroll wheel sets the hold speed.
- Up/Down change gear.
- Shift depresses the clutch, smoothed with a 1 ms RC (1 s while Space is held).

### 2.4 Outputs

| quantity | read via | unit |
|---|---|---|
| rpm, crank speed | `Engine::getRpm()` (abs of crank 0 `v_theta`), `getSpeed()` (rad/s), `isSpinningCw()` | **The crank spins with negative `v_theta`** |
| torque, power | `Simulator::getFilteredDynoTorque()` (mean of 512 crank-angle bins over a 4π cycle), `getDynoPower()` = that × ω | N·m, W. **Only meaningful with the dyno enabled.** No "engine torque" getter exists |
| clutch torque | `Transmission::m_clutchConstraint.F_t[0][0]` (protected; subclass to read) | N·m on the crank. Positive while driving the car |
| manifold pressure | `Engine::getManifoldPressure()` (mean of the intakes' `GasSystem::pressure()`) | Pa absolute |
| airflow | `Engine::getIntakeFlowRate()` | mol/s (normalised in `PistonEngineSimulator::endFrame`) |
| air-fuel ratio (AFR), O₂ | `getIntakeAfr()`, `getExhaustO2()` | mass ratio, fraction |
| fuel used | `getTotalFuelMassConsumed()`, `getTotalVolumeFuelConsumed()` | kg, m³ |
| throttle | `getThrottlePlateAngle()`, `getThrottle()` (1 = closed) | rad, 0-1 |
| per cylinder | `getChamber(i)->m_system.pressure()/temperature()`, `getLastTimestepExhaustFlow()` | Pa, K, mol |
| vehicle | `getVehicle()->getSpeed()` (from rotating-mass energy, never negative), `getTravelledDistance()` | m/s, m |
| performance | `getAverageProcessingTime()` (EMA, µs per frame), `getSynthesizerInputLatency()` | |

Units are SI throughout (`include/units.h`). `units::rpm(x)` converts rpm → rad/s.

### 2.5 The app loop: the logic to copy

`EngineSimApplication::run()` (`:347-422`) calls `processEngineInput()` then `process(frameLength)` once per rendered frame, on the **main thread**.

The relevant logic of `process()` (`:202-312`, condensed and MIT-licensed):

```cpp
frame_dt = clamp(frame_dt, 1/200.0, 1/30.0);
m_simulator->setSimulationSpeed(speed);                           // 1, or slow-mo keys
m_simulator->startFrame(1 / clamp(avgFramerate, 30, 1000));
while (m_simulator->simulateStep()) { m_oscCluster->sample(); }   // N steps
m_simulator->endFrame();                                          // -> Synthesizer::endInputBlock()

// audio: keep the device buffer ~0.1 s ahead of the play cursor (1 s looping 16-bit mono buffer at 44.1 kHz)
safeWrite = m_audioSource->GetCurrentWritePosition();
target    = safeWrite + 0.1 s;   maxWrite = distance(m_writePointer, target);
if (lead > 0.5 s) m_writePointer = safeWrite + 0.05 s;             // resync after a hitch
if (currentLead > newLead) maxWrite = 0;
n = m_simulator->readAudioOutput(maxWrite, samples);               // Synthesizer::readAudioOutput (locks m_lock0)
LockBufferSegment(...); copy n samples; Unlock; commitBlock(n);
```

At startup (`loadEngine`), the app:
1. creates the simulator
2. sets the frequency and audio parameters
3. loads the IRs
4. calls `startAudioRenderingThread()`

`initialize()` (`:169-184`) creates a 1 s `ysAudioSource` buffer in Loop mode.

---

## 3. Drivetrain, load, and coupling to Unity (Q3)

### 3.1 What engine-sim's "car" is

The car is a single rotating mass.

**Bodies**
- `PistonEngineSimulator::m_vehicleMass`, linked to the output crank by the `Transmission`'s `atg_scs::ClutchConstraint`.
- That constraint is one velocity row: `J = [θ₁: −1, θ₂: +1]`, `v_bias = 0`, limits `[minTorque, maxTorque]` (`scs/src/clutch_constraint.cpp`). It is a Coulomb clutch: locked below capacity, slipping above.

**`Vehicle`** (`src/vehicle.cpp`) is only parameters: mass, drag coefficient, frontal area, diff ratio, tyre radius, rolling resistance.
- `Transmission::changeGear` sets the body inertia to `m·(r/(diff·gear))²`. This is the vehicle mass reflected to the crank.
- `Vehicle::getSpeed()` derives speed from the body's energy.

**`VehicleDragConstraint`** (`src/vehicle_drag_constraint.cpp`) is a one-body velocity row that drives ω → 0. Its torque limit is `[−√(I/m)·(F_roll + ½ρv²C_dA), 0]`; `√(I/m)` is the reflected wheel radius.

**`Dynamometer` / `StarterMotor`** (`src/dynamometer.cpp`, `starter_motor.cpp`) are one-body velocity rows toward ±`m_rotationSpeed` with one- or two-sided torque limits. `Dynamometer::getTorque()` returns ∓`F_t[0][0]`.

**Solver**
- `OptimizedNsvRigidBodySystem::process` is a single-stage semi-implicit Euler step (`nsv_ode_solver.cpp`).
- The constraint solve is velocity-level with limits scaled by `dt`, using projected Gauss-Seidel (`optimized_nsv_rigid_body_system.cpp:113-272`).
- Constraint torques are stored back into `Constraint::F_x/F_y/F_t[row][body]` (`propagateResults`).
- Combustion acts through `CombustionChamber::apply` (a `ForceGenerator`): the force on the piston along the bore, plus friction.

**So load is applied to the crank only via constraints and force generators in the `atg_scs` system.**

### 3.2 Can an external engine supply the load and read back torque? Yes, without forking

1. Subclass `PistonEngineSimulator` (`class UnitySim : public PistonEngineSimulator`). Every member needed is `protected`.
2. Replicate `Engine::createSimulator` (6 lines) to instantiate the subclass.
3. After `loadSimulation`, call `m_system->removeConstraint(&m_vehicleDrag)`.
4. Add your own `atg_scs::ForceGenerator`, which can add torque to `SystemState::t[body]`, or your own `atg_scs::Constraint`, through `getSystem()->addForceGenerator/addConstraint`.
5. Read the clutch torque from `F_t` of the clutch constraint.

Reaching the clutch constraint needs one extra step. It is a `protected` member of `Transmission`, and scripts create plain `new Transmission` (`scripting/include/actions.h:512`). So the plugin should **create its own `class UnityTransmission : public Transmission { double clutchTorque() const { return m_clutchConstraint.F_t[0][0]; } }`** and pass that to `loadSimulation`, ignoring the script's transmission.

`m_vehicleMass` can be reused as the external "driveline proxy" body. Set `.I` and `.v_theta` directly after `changeGear`.

**Constraint:** `atg_scs::Constraint` supports at most **2 bodies** (`MaxBodyCount = 2`, `scs/include/constraint.h`). An open differential (3 shafts) therefore cannot be expressed inside engine-sim; driven wheels have to be lumped into one body. This is why multi-axle drivelines belong in Unity (§3.6).

### 3.3 Sign conventions and gotchas

- The engine spins **negative**: forward is `v_theta < 0`.
- The clutch `F_t[0][0]` (torque on the crank) is **positive** while the engine drives the car. I verified this empirically.
- Torque the plugin applies to the proxy body that *resists* forward motion must be **positive**.
- **Clutch-limit lag:** `Transmission::update` runs *after* `m_system->process` (`simulator.cpp:107` vs `:111`), so new clutch limits apply one step late. On the first step after a gear change, the clutch constraint still has its constructor default of ±`DBL_MAX`. My first co-sim run measured a spurious 590 N·m on step 0 from this. **Call `transmission->update(0)` right after `setClutchPressure/changeGear`.**
- `startFrame` adds ±10 % steps based on synthesizer latency (§2.2), so engine-sim time is not locked to the caller's clock. Two fixes are needed:
  - `setTargetSynthesizerLatency(0.0)`, with the synthesizer input fully consumed before `endFrame` (§4.3), so `latency == target == 0` and the step count is exactly `round(dt·f)`.
  - Call `synthesizer().endInputBlock()` once after initialisation, because `Synthesizer::m_latency` is uninitialised until then.
  - Pass `n/f` (with a fractional accumulator) as `dt`.

### 3.4 Stability at Unity 50-120 Hz vs engine-sim at ~10 kHz

**Stiff elements**

| element | magnitude | where it must be integrated |
|---|---|---|
| clutch lock (crank 0.457 kg·m² calibrated, proxy 0.06-0.2 kg·m²) | infinitely stiff, constraint | inside an implicit/constraint solver, never across a 50 Hz boundary as explicit torque |
| tyre longitudinal slip at low speed: `dF/dω = μF_z·B·C·r/v` | e.g. 7.9 kN · 19 · 0.254 / 8 m/s ≈ 4.8 kN·s, so a 2 kg·m² wheel has λ ≈ 600 s⁻¹ | explicit integration needs h < 2/λ ≈ 3.3 ms; fine at 10 kHz or in an implicit 480 Hz solver, **not** explicit at 50 Hz |
| engine torque vs speed | |∂T/∂ω| ≲ 0.5 N·m·s/rad (06 §4.5) | can cross the boundary with 1-2 steps of lag |

**Principle: never split a stiff loop across the Unity ↔ engine-sim boundary.** Each candidate partition puts the stiff parts on one side:

- **A (plugin owns the rotating driveline).** The proxy body is `I_D = J_in + J_wheels/G²` at the gearbox input. Unity sends the tyre's longitudinal force law per step, `F(ω_w)` at frozen chassis speed `v` (the full curve, or **B**: linearised `F0 + c·(ω − ω0)`), as a `ForceGenerator`. The plugin integrates crank + clutch + wheel spin at 10 kHz and returns mean `F` (applied to the chassis by Unity) and the end-of-step `ω_w`.
- **C (Unity owns wheel spin).** Each step Unity resets `m_vehicleMass.v_theta = −ω_w·G` (kinematic) and applies a constant load `F(ω_w)·r/G`. The plugin returns the mean clutch torque, which Unity applies to its wheel integrator.
- **D (Unity owns the crank: 06 "Option D").** Unity's shaft solver integrates crank, clutch, gearbox, diffs and wheels implicitly with substeps.
  - engine-sim is in neutral. A `ForceGenerator` PD slaves its crank to Unity's crank-speed trajectory (`f_c` = 8 Hz, `K_p = J·(2πf_c)²`, `K_d = 1.4·(2πf_c)·J`).
  - It returns `T_ind = J_es·Δω/h − mean(T_pd)`, which Unity applies **one step later**.

### 3.5 Measured: co-simulation experiment (probe `bench.exe out cosim`)

**Setup**
- Engine: the LS V8 rebuilt in C++ from `07_gm_ls.mr`, at 10 kHz with 8 gas substeps.
- Car: 1614 kg; driven-axle load 7.9 kN; magic-formula tyre `F = μF_z·sin(1.9·atan(10κ))`; driven-wheel inertia 2 kg·m²; gearbox 0.02 kg·m²; clutch capacity 678 N·m; drag 200 N + ½ρC_dAv².
- Script: starter 0-1.2 s, idle, clutch ramp 2.0-2.5 s at 25 % throttle, then the scenario throttle until 6.5 s.
- **Reference:** everything at 10 kHz, with the chassis updated every engine step.
- Metrics: final speed difference, mean |Δrpm| vs the reference after 2 s, and "slip-chatter" (RMS step-to-step change in slip ratio after 3 s).

Mean |Δrpm| in the launch scenarios is inflated by chaotic rev-limiter bouncing: any tiny difference shifts the limiter phase. Δv is the robust accuracy metric there.

| variant | 2nd-gear WOT launch, μ = 1.0 (ref v = 15.04 m/s) | 2nd-gear WOT launch, μ = 0.35 mud (ref 8.88) | 4th-gear 30 % cruise, grip, clutch locked (ref 30.38) |
|---|---|---|---|
| **A** 50 Hz, full tyre curve in plugin | Δv +0.06, \|Δrpm\| 17, chatter 0.040 (ref 0.040) | −0.00, 43, 0.051 (ref 0.051) | +0.02, **4**, 0.0005 (ref 0.0005) |
| A 100 Hz | +0.04, 118, 0.021 | −0.00, 94, 0.026 | −0.02, 7, 0.0005 |
| **B** 50 Hz, linearised tyre | +0.08, 68, 0.040 | +0.01, 44, 0.051 | −0.01, 5, 0.0011 |
| **C** 50 Hz, Unity explicit wheel + reset | +0.02, 122, 0.045 | −0.00, 37, 0.060 | +0.00, 4, 0.0013 |
| C 100 Hz | +0.16, 72, 0.022 | −0.01, 26, 0.028 | +0.00, 4, 0.0008 |
| C 500 Hz | −0.01, 49, 0.004 | −0.00, 73, 0.006 | −0.04, 8, 0.0008 |
| **D** 120 Hz × 4 substeps, PD-slaved, 1-step lag | −0.10, 346, 0.017; tracking 21 rpm RMS | −0.01, 272; 21 rpm | −0.08, 17; 10 rpm |
| D 50 Hz × 4 | +0.06, 348, 0.041; 35 rpm | −0.01, 280; 36 rpm | −0.04, 11; 10 rpm |

Calibrated `J_es` (ignition-off ramp up and down through 2000 rpm, `J = (T_up − T_down)/2α`) = **0.4570 kg·m²** against the crankshaft body's 0.4537. Pistons and rods add only 0.7 %.

**Findings**
1. **No variant diverged or produced NaN.** The feared 50 Hz instability does not appear when the stiff parts stay on one side.
2. **A is the most faithful** at 50 Hz (Δv ≤ 0.06 m/s; 4 rpm in cruise) with zero added latency. Linearising the tyre (B) costs little.
3. **C works.** In wheelspin, engine inertia (crank 0.457 × G² ≈ 23 kg·m² at the wheel) dominates the 2 kg·m² wheel, and the constant load stays consistent with the reset speed. Chatter doubles at 50 Hz in grip and falls with Unity-side substeps.
4. **D** is stable at 50 and 120 Hz. engine-sim tracks Unity's crank to 10-36 rpm RMS, which bounds what the *sound* hears.
   - Its rpm departs most from the reference while bouncing on the rev limiter: final rpm 6387-6932 vs 5658-6134. This is the 1-step torque lag acting on spark cut, as 06 §4.5 item 4 predicted.
   - Mitigations: run at 120 Hz, add the Unity-side fuel-cut backstop that 06 proposes, or return `∂T_ind/∂ω` so Unity can extrapolate within the step.

### 3.6 Recommended coupling

**For Backroads' own vehicles (06's "BRV" shaft solver): use D**, with these specifics:
- engine-sim transmission in neutral with clutch limits 0 (`changeGear(−1); update(0)`), and `m_vehicleDrag` removed.
- The `SlavedCrankPD` `ForceGenerator` (Appendix A.4) on `getOutputCrankshaft()->m_body`, with gains as above and `|T| ≤ 4 × peak`.
- Unity passes the crank-speed samples at substep boundaries. The plugin linearly interpolates the command per engine step and integrates the angle error itself.
- `T_ind` is returned through an SPSC result queue. Unity never blocks on it.
- The starter can be either engine-sim's (`m_starterMotor`) or Unity's: both act on the same crank speed.

Why D for BRV:
- Diffs, lockers, low range, 6×6 and trailers need a multi-body shaft solver, and `atg_scs` cannot hold a diff (§3.2).
- D never stalls the physics thread. A V8 at 10k/8 costs 2.9 ms of worker time per 120 Hz step.
- The same path serves remote cars.

**Use A** ("PROXY" mode) when:
- the vehicle has one driven axle with a locked or spool diff (race cars, karts), or
- D's limiter lag is audible or felt.

How A runs:
- Unity hands the plugin the reduced driveline (`G`, inertias) and the per-step tyre force law.
- The plugin owns driven-wheel ω and returns mean `F_x` and `ω_w`.
- It needs a fork-join each step: kick the worker at the start of `FixedUpdate`, join at the end.

**Use C** ("KINEMATIC" mode) for third-party physics that owns wheel spin (WheelCollider-style).

**Always**
- Fractional step accumulator; `setTargetSynthesizerLatency(0)`; `transmission->update(0)` after control changes.
- Open the clutch in Unity during shifts (velocity jumps on a locked clutch cause torque spikes).
- `G = 0` (neutral) means clutch pressure 0.
- Reverse is a negative `G`.

---

## 4. Audio (Q4)

### 4.1 How the Synthesizer makes samples

**Input, per sim step**

`PistonEngineSimulator::writeToSynthesizer` (`piston_engine_simulator.cpp:370-413`) computes one sample per exhaust system, summed over cylinders:
- per-cylinder pulse = `attenuation³ · 1600 · ((P_runner − 1 atm) + 0.1·dyn⁺ + 0.1·dyn⁻)`
  - `attenuation = min(|rpm|, 40)/40`
  - the pulse is delayed by `(header + exhaust length)/343 m/s` in a `DelayFilter`
- each pulse is scaled by `sound_attenuation · audio_volume / cylinders / length²`
- the result goes to `Synthesizer::writeInput(double* perChannel)`

**Upsampling:** `writeInput` (`synthesizer.cpp:168-195`) resamples from the sim rate to the audio rate by **linear interpolation**, through a **1.9 kHz** Butterworth anti-alias filter (`:73`). It writes into per-channel `RingBuffer<float>`s with a capacity of 44,100.
- So the physical signal bandwidth is < 2 kHz whatever the sim rate. Higher `simulation_frequency` improves gas-dynamics accuracy, not audio bandwidth.

**Render, per output sample, per channel** (`renderAudio(int)`, `:281-331`):
1. jitter filter (time jitter with `std::default_random_engine`)
2. 10 Hz DC removal
3. derivative mix `dF_F_mix` (the "hf gain")
4. low-frequency air noise (`rand()` through a 2 kHz low-pass)
5. **direct-form convolution with the IR** (`ConvolutionFilter::f`, up to 10,000 taps: `min(10000, last |x|>100)`, `:99`)

Then, on the summed signal:

6. sum the channels
7. anti-alias at 0.45·fs
8. `LevelingFilter` automatic gain control (target 30,000, gain 1e-5…1.9) × `volume`
9. clamp to **int16**

It clips 0.4-3 % of samples under load (02 §6.7); lower `volume` to about 0.5.

**Rates and buffers**
- `audioSampleRate = 44100`, `inputBufferSize = 44100`, `audioBufferSize = 44100`, `inputChannelCount = exhaust systems` (`simulator.cpp:204-212`, hard-coded).
- `Synthesizer::initialize` pre-fills the output ring with 44,100 zeros (`:81-83`).

**Shipped IRs** (`es/sound-library`) are all 44.1 kHz mono 16-bit. Taps used after clipping:

| IR | taps used |
|---|---|
| `smooth_39` | 6,907 |
| `minimal_muffling_02` | 5,636 |
| `mild_exhaust` | 1,893 |
| `sharp_01` | 2,022 |
| `test_engine_16` | 10,000 (capped) |
| `mild_exhaust_reverb` | 10,000 (capped) |

**Threading**
- `startAudioRenderingThread()` runs `audioRenderingThread()` in a `std::thread`, looping `renderAudio()` (`:222-256`).
- It waits on `m_cv0` under `m_lock0` until the input is non-empty, the output holds fewer than **2000** samples, and the block is not yet processed.
- It copies the input to `transferBuffer`, renders outside the lock and notifies.
- The main thread's `endInputBlock()` removes consumed input under a **different** mutex (`m_inputLock`), and `writeInput` writes without a lock. That is a benign-on-x86 data race.
- `readAudioOutput` locks `m_lock0` and **will block** while the render thread holds it, so it must not be called from a real-time audio callback.

**App latency:** the 0.1 s input backlog target, plus ≤ 2000 synthesizer samples (45 ms), plus the 0.1 s device lead gives **≈ 0.15-0.25 s**, and 1 s of extra delay at start-up (the pre-filled zeros).

### 4.2 What Unity needs instead

**Don't start the synthesizer thread.** Render **synchronously on the engine worker right after each batch of steps**, then push float samples into a **lock-free SPSC ring** read by the audio thread.

**Sample rate.** Unity typically mixes at 48 kHz (`AudioSettings.outputSampleRate`). In the subclass, after `loadSimulation`:
1. call `synthesizer().destroy()`, then `synthesizer().initialize(p)` with `audioSampleRate = inputBufferSize = audioBufferSize = 48000` and `inputChannelCount = exhaust count`
2. resample each IR 44.1 → 48 kHz before `initializeImpulseResponse`

All the synthesizer filters take `m_audioSampleRate`, so this needs no source change.

**Exhaust delay filters.** Re-initialise `m_delayFilters[i].initialize(delay, simFrequency)`: upstream hard-codes **10000.0** (`piston_engine_simulator.cpp:226`), which is wrong for 20 kHz engines.

**Convolution cost.** `ConvolutionFilter::f` is not auto-vectorised.
- Mine: 3.92 µs/sample at 10,000 taps, even with `/fp:fast /arch:AVX2`. 02 measured the same.
- A hand-written AVX2 FIR over a duplicated history buffer runs at **0.51 µs/sample** (Appendix A.5).
- A uniformly partitioned FFT convolution would be cheaper still.
- `Synthesizer`'s members are public (`//protected:` is commented out in `synthesizer.h`), so the plugin can re-implement the ~40-line `renderAudio(int)` with its own FIR **without forking**.

### 4.3 Synchronous render (works today, tested)

```cpp
// After `while (sim->simulateStep()) {}` and BEFORE sim->endFrame():
static int renderSync(Synthesizer &s, SpscRing &out) {
    const int n = (int)std::min<size_t>(s.m_inputChannels[0].data.size(), (size_t)s.m_inputBufferSize);
    for (int c = 0; c < s.m_inputChannelCount; ++c)
        s.m_inputChannels[c].data.read(n, s.m_inputChannels[c].transferBuffer);
    s.m_inputSamplesRead = n;                 // endFrame() -> endInputBlock() removes them; m_latency becomes 0
    for (int c = 0; c < s.m_inputChannelCount; ++c) {
        s.m_filters[c].airNoiseLowPass.setCutoffFrequency(s.m_audioParameters.airNoiseFrequencyCutoff, s.m_audioSampleRate);
        s.m_filters[c].jitterFilter.setJitterScale(s.m_audioParameters.inputSampleNoise);
    }
    float tmp[4096]; int k = 0;
    for (int i = 0; i < n; ++i) {
        tmp[k++] = s.renderAudio(i) * (1.0f / 32768.0f);   // or a plugin-side renderSample() with SIMD FIR
        if (k == 4096) { out.push(tmp, k); k = 0; }
    }
    out.push(tmp, k);
    return n;
}
```

Ordering matters: render **before** `endFrame()`, so `endInputBlock()` leaves `m_latency = 0` and the `startFrame` step-count fudge stays off (§3.3).

In the probe this produced 6 s WAVs per engine, e.g. `out-fast/8cyl.wav`. Their spectra peak at harmonics of the engine cycle frequency (rpm/120). 02 measured pitch tracking the firing frequency on its EJ25 output.

### 4.4 Lock-free handoff and clock drift

**The ring**
- Single producer (the engine worker), single consumer (the audio thread).
- Power-of-two float ring, 16,384 frames (340 ms at 48 kHz, enough to survive a 100-200 ms hitch burst).
- `std::atomic<uint64_t> write, read`, each on its own cache line. The producer `store(write, release)`; the consumer `load(write, acquire)`.
- The producer never touches `read`: when the ring is full it drops the newest samples.

**Underrun:** hold and exponentially decay the last sample over about 2 ms to avoid clicks, and count the event.

**Fill target:** one physics step + one DSP buffer + about 10 ms. For example 8.3 ms + 5.3 ms + 10 ms ≈ 24 ms at 120 Hz with Best latency (256 frames at 48 kHz), or ≈ 50 ms at 50 Hz with a 1024-frame buffer.

**Drift.** Sim time follows physics time, which follows `Time.fixedDeltaTime`; the DSP clock is independent. In the producer, after each `startFrame`, call

`synthesizer().setInputSampleRate(f · simSpeed · (1 + ε))`, with `ε = clamp(Kp·(fill − target)/target, ±0.005)`, smoothed.

A higher claimed input rate means fewer output samples per step. That is a built-in resampler: ±0.5 % is ±8.6 cents of pitch, which is inaudible on engine noise.

**Pause and slow motion.** Pass `Time.timeScale` as `setSimulationSpeed` so the produced audio stays real-time (pitch drops, as in the app's time warp). While physics is paused, nothing is produced and the ring fades out.

### 4.5 Getting samples into Unity

| option | thread | spatial 3D / mixer | effort | verdict |
|---|---|---|---|---|
| **`OnAudioFilterRead(float[] data, int channels)`** on a GameObject with an `AudioSource` (no clip; the filter "plays") | audio thread, ~every DSP buffer | Mixer group routing works. Whether `spatialBlend`/3D attenuation applies to filter-generated audio is **not documented**, and old issue-tracker entries say it does not. Test in 6000.3; workaround: play a looping DC clip of 1.0 and multiply | lowest | **v1 for the local car** (often effectively 2D or near the listener anyway) |
| **Unity 6.3+ Scriptable Audio `IAudioGenerator`** (`AudioSource.generator`; `GeneratorInstance.IControl<T>` + a Burst `IRealtime` struct with `Process(in RealtimeContext, Pipe, ChannelBuffer, Arguments)`) | real-time audio thread, Burst | a real `AudioSource` input, so 3D, Doppler and mixer work | medium | **Preferred** once validated. Burst can call native plug-in functions via `[DllImport]` with primitive or pointer arguments (Burst docs), so `esp_read_audio(IntPtr, float*, int, int)` qualifies. Needs a spike: confirm `ChannelBuffer` exposes a pointer or span, or copy per frame |
| Native Audio Plugin SDK (C++ DSP effect on an `AudioMixer` group) | mixer thread, no managed code | per mixer group, not per source (3D needs the Spatializer SDK) | high | only if managed callbacks prove too slow (unlikely) |
| `AudioClip.Create(..., stream: true, PCMReaderCallback)` | Unity's streaming callback; block size and latency unclear | full `AudioSource` features | low | a fallback if the other two fail |

Whichever path is used, the audio thread must only call `esp_read_audio` (lock-free, no allocation). No logging or managed allocation is allowed in the callback.

---

## 5. Engine definitions (Q5)

### 5.1 How `.mr` scripts define an engine

The `.mr` files are piranha: a declarative node-graph language. `es/engine_sim.mr` imports the library:
- `es/objects/objects.mr` defines the parts, with defaults:
  - `engine`, `crankshaft`, `rod_journal`
  - `piston` and `piston_parameters`, `connecting_rod` and `connecting_rod_parameters`
  - `cylinder_bank`, `cylinder_head` / `generic_cylinder_head`, `camshaft`
  - `standard_valvetrain` / `vtec_valvetrain`, `intake`, `exhaust_system`
  - `ignition_module` / `ignition_wire`, `fuel`
  - `direct_throttle_linkage` / `governor`, `vehicle`, `transmission`, `function`, `impulse_response`
- `es/actions/actions.mr` defines the verbs: `add_rod_journal`, `add_cylinder`, `set_cylinder_head`, `add_lobe`, `add_sample`, `connect_wire`, `k_carb(cfm)`, `k_28inH2O(cfm)`, `harmonic_cam_lobe(...)`, `set_engine/vehicle/transmission`.
- Also `units.mr`, `utilities.mr` (inertia helpers), `part-library/` (heads, camshafts, intakes, distributors for Chevy BBC/SBC and V-twins) and `sound-library/impulse_responses.mr` (`ir_lib.default_0`, …).

**What a file contains.** An engine file (see `assets/engines/atg-video-2/07_gm_ls.mr`, 455 lines) declares:
- ignition wires
- a head node with intake and exhaust port **flow tables** (lift in thou → cfm at 28 inH₂O)
- a camshaft node with **lobe centrelines in firing order**
- the `engine(...)`: name, starter, redline, `throttle_gamma`, `fuel(...)`, `hf_gain`/`noise`/`jitter`, `simulation_frequency`
- the `crankshaft` (throw, masses, inertia, friction, tdc) and rod-journal angles
- piston and rod parameters; `intake` (plenum, `k_carb` flow constants, idle plate)
- `exhaust_system`s (length, flow, `audio_volume`, IR)
- `cylinder_bank`s with `.add_cylinder(piston, rod, journal, intake, exhaust, wire, primary_length)`
- `harmonic_cam_lobe`s (duration at 0.050", γ, lift)
- a timing-advance `function`, and the `ignition_module` with `.connect_wire(wire, angle)` in firing order
- a vehicle and transmission node, and `public node main { set_engine(...) set_vehicle(...) set_transmission(...) }`

**How it is turned into objects.** The C++ side (`scripting/include/*_node.h`) builds the objects in `EngineNode::buildEngine` (`engine_node.h`). The `Compiler::output()` static collects `engine`, `vehicle` and `transmission` (`actions.h:35-37, 512-538`).

**Loading caveats**
- `assets/main.mr` imports a theme and **one** engine file, then calls `main()`.
- **Older files** (`audi/i5.mr`, `bmw/M52B28.mr`, `chevrolet/*.mr`, `kohler/kohler_ch750.mr`) have **no `main`** node. Their top-level node is e.g. `public node M52B28` or `chev_truck_454`, so a loader must call `set_engine(<node>())` itself.
- IR filenames resolve relative to the `.mr` file that declares them (`impulse_response_node.h`), not the current working directory.

### 5.2 Engines that ship (27 files)

| folder | file → engine name | cyl | `simulation_frequency` | redline |
|---|---|---|---|---|
| atg-video-1 | `01_honda_trx520` → Honda TRX520 (ATV) | 1 | 40,000 | 5000 |
| | `02_kohler_ch750` → Kohler CH750 (governor) | 2 | 30,000 | 3600 |
| | `03_harley_davidson_shovelhead` | 2 | 35,000 | 5000 |
| | `04_hayabusa` → Suzuki Hayabusa I4 | 4 | 20,000 | 11000 |
| | `05_honda_vtec` → Honda B18C5 (VTEC I4) | 4 | 20,000 | 8400 |
| | `06_subaru_ej25` → Subaru EJ25 (flat-4) | 4 | 20,000 | 6500 |
| | `07_audi_i5` → Audi 2.3 inline 5 | 5 | 17,000 | 6000 |
| | `08_radial_5` | 5 | 12,000 | 3000 |
| atg-video-2 | `01/02_subaru_ej25_eh/uh` (equal / unequal headers) | 4 | 20,000 | 6500 |
| | `03_2jz` → 2JZ I6 | 6 | 10,000 | 6000 |
| | `04/05/06` → generic 60° V6, odd-fire V6, even-fire V6 | 6 | default 10,000 | 5500 |
| | `07_gm_ls` → GM LS V8 | 8 | 10,000 | 6500 |
| | `08_ferrari_f136_v8` | 8 | 10,000 | 9000 |
| | `09_radial_9` | 9 | 7,500 | 3000 |
| | `10_lfa_v10` → 1LR-GUE V10 | 10 | 6,500 | 9000 |
| | `11_merlin_v12` → Merlin V-1650-9 | 12 | 7,000 | 3000 |
| | `12_ferrari_412_t2` → F1 V12 | 12 | 5,000 | 18000 |
| audi | `i5` → Audi 2.2 inline 5 | 5 | default | 7500 |
| bmw | `M52B28` → BMW M52 I6 | 6 | default | 7000 |
| chevrolet | `chev_truck_454`, `engine_03_for_e1` → Chev 454 V8 | 8 | default | 5500 |
| kohler | `kohler_ch750` | 2 | default | 3600 |

`radial.mr` (two copies) is a helper.

**Four shipped engines (9, 10 and 12 cylinders) overflow `lastValveLift[8]`** (§8).

### 5.3 How hard is authoring new engines

**Effort.** Each engine is 150-500 lines, mostly boilerplate.

- **Copy and modify.** Starting from the nearest shipped archetype (the I4 VTEC, the 2JZ or M52 for I6, the LS or 454 for V8, the V6s) and changing bore, stroke, rod, masses, cams, flows, ratios and IR is **1-3 hours**.
- **Tuning** to a target torque curve takes iterative dyno sweeps (the plugin should expose `esp_dyno_sweep`): roughly **0.5-2 days** per engine.
- **The hard parts:** crank journal angles and firing order (crossplane vs flatplane, odd-fire), cam centrelines, and plausible head-flow tables.
- **Tooling:** the community web tool **Enginette** (github.com/enginette/enginette) generates `.mr` from a form.

**Calibration is not automatic.**
- My C++ rebuild of `07_gm_ls.mr` gives **715 N·m at 4000 rpm WOT** for 5.33 L. That is well above a production 5.3 L LS (≈ 450 N·m).
  - Either the script is tuned hot (`max_burning_efficiency: 1.0`) or my port deviates. This is unverified because piranha can't run here.
- 02's EJ25 port matched the real engine within a few percent.
- Plan a dyno-vs-spec pass per engine.

**Racing engines** are well covered by shipped examples:
- F136 V8 at 9000 rpm, LFA V10 at 9000, 412 T2 V12 at 18000 rpm (at only 5 kHz), Hayabusa at 11000.
- Keep high-rev, many-cylinder engines at the author's chosen frequency. Results are frequency-sensitive (§6).

**Diesel I6 for trucks: not modelled.**
- Combustion is spark-triggered: `IgnitionModule` fires `CombustionChamber::ignite`, which requires equivalence ratio 0.5-1.9.
- Fuel is premixed in the intake at a fixed ratio (`Intake::process`); load control is the throttle plate.
- engine-sim has no compression ignition, injection quantity control, turbocharger or exhaust brake. grep for turbo, supercharg, diesel and inject finds nothing relevant.

**A diesel stand-in:**
- Start from `03_2jz.mr` or `M52B28.mr`.
- Long stroke and big bore (~100 mm × 120 mm class), heavy flywheel, small `chamber_volume` (high compression).
- Fuel `energy_density` ≈ 45.5 kJ/g and `molecular_afr` = 17.75 (C₁₂H₂₃: 12 + 23/4 mol O₂ per mol; the default 12.5 is C₈H₁₈).
- Fixed early timing curve, redline 2500-3200, low `throttle_gamma` or the `governor` throttle.
- A harsher IR; layer turbo whistle and injector tick as Unity samples.

True diesel knock or clatter and boost need a fork.
- A third-party fork, `github.com/kyan0s-git/engine-sim`, claims turbo and supercharger support, a "simulation thread", an engineering gas model and a vendored pre-generated parser (`PIRANHA_REGENERATE_PARSER` OFF).
- But its history starts **2026-10-11** (≥ 248 commits in a week, some from `claude/*` branches). Treat it as unvetted reference material, not a dependency.
- Upstream itself is frozen: the README says it moved to `Engine-Simulator/engine-sim-community-edition`, which is closed-source binary distribution, v0.1.14a.

### 5.4 Does piranha need flex/bison? Yes; there is no pre-generated parser

**Build requirements**
- `piranha/CMakeLists.txt:31-46`: `find_package(FLEX REQUIRED)`, `find_package(BISON REQUIRED)`, then `flex_target(... flex-bison/scanner.l → scanner.auto.cpp/.h)` and `bison_target(... flex-bison/specification.y → parser.auto.cpp/.h)`.
- `specification.y`: `%require "3.2"`, `%skeleton "lalr1.cc"`, `api.value.type variant`, custom `api.location.type`.
- `scanner.l`: `%option c++`, `yyclass="piranha::Scanner"`. `FlexLexer.h` is vendored (`piranha/dependencies/libraries/flex/include`).
- The only consumer is `src/ir_compilation_unit.cpp`, through `include/scanner.h` (`#include <parser.auto.h>`).
- **Boost.Filesystem** is used only by `src/path.cpp` (and forward-declared in `include/path.h`).

**Verified on this machine**
- With my `piranha_path_std.cpp` (std::filesystem; it defines `boost::filesystem::path` as a subclass of `std::filesystem::path`, so `path.h` is untouched), **all piranha sources except `ir_compilation_unit.cpp`, plus `scripting/src/{channel_types,compiler,engine_context,language_rules}.cpp`, compile with MSVC 19.44 and no Boost.**
- What is left: run **win_flex_bison 2.5.25** (portable zip, `win_flex` 2.6.4 + `win_bison` 3.8.2 per 02 §3) **once**, and commit `scanner.auto.cpp`, `parser.auto.cpp` and `parser.auto.h` into the plugin's vendor tree, so later builds never need flex or bison.
- This needs the user's approval to download a tool into a project `tools/` folder; nothing is installed system-wide.

**Licence**
- piranha at `432f0b1` has no LICENSE file.
- The GitHub repository `ange-yaghi/piranha` now shows an **MIT** licence and LICENSE file. Ship that text in third-party notices.

**Runtime loading pitfalls**
- `es_script::Compiler::initialize` adds search paths relative to the **current working directory** (`../../es/`, `../es/`, `es/`) and writes `error_log.log` to it.
- Imports resolve first relative to the importing file, then the CWD, then the search paths (`piranha/src/compiler.cpp:44-60, 117-133`).
- `Compiler::output()` is a process-wide static that is never reset.

So the plugin should:
1. reuse `es_script::LanguageRules` with its own `piranha::Compiler` wrapper and an **absolute** search path to `StreamingAssets/EngineSim/es`
2. reset `*es_script::Compiler::output() = {}` before each compile
3. hold a global mutex around compiles
4. generate a tiny wrapper `.mr` (`import "engine_sim.mr"`, `import "<abs engine path>"`, then `main()` or `set_engine(<node>())`) in a temp folder

**Recommendation (differs from 02, which preferred JSON with no piranha):**
- **Ship piranha in v1** (MIT, about 40 small files plus 3 generated). The 27 shipped engines and community `.mr` engines then work with zero porting errors.
- Optionally add a JSON engine format later for in-Unity editing. The C++ builder is proven by 02's EJ25 and my LS rebuild (Appendix A).

---

## 6. Threading and CPU (Q6)

### 6.1 Threading in the original

- **Physics runs on the app's main thread** (`run()` → `process()`).
- The synthesizer has its own thread (§4.1). There is no intra-engine parallelism.

**Engines are independent objects except for shared statics:**
- `static double lastValveLift[8]` (`piston_engine_simulator.cpp:379`): shared, and written out of bounds for more than 8 cylinders.
- `Function::DefaultGaussianFilter`, lazily created in the first `Function` constructor (`function.cpp:19-22`): an initialisation race.
- `es_script::Compiler::s_output`.
- `rand()` (`combustion_chamber.cpp:211`, `synthesizer.cpp:288, 298`) is per-thread state in the MSVC UCRT. That is safe, but every thread starts the same sequence.

With the §8 workarounds, **one engine per worker thread is safe**; 02 §6.4 measured near-linear scaling to 16 engines.

### 6.2 Measured cost

Probe `bench.exe`; real-time fraction of one core = µs/step × f_sim. Single thread, unpinned, LS cylinder geometry; I4, I6 and V12 are synthetic.

| engine (10 kHz, 8 gas substeps) | bodies / constraint rows | default `/O2 /fp:precise` µs/step (idle / WOT) | `/fp:fast /arch:AVX2` µs/step (idle / WOT) | real-time core (fast) | synth, scalar FIR, µs/sample | synth core at 44.1 kHz |
|---|---|---|---|---|---|---|
| I4, 1 exhaust | 10 / 27 | 16.0 / 16.5 | 14.4 / 14.9 | **14.9 %** | 2.79 (1 ch) | 12.3 % |
| I6, 1 exhaust | 14 / 37 | 24.7 / 28.3 | 20.9 / 25.0 | **25.0 %** | 3.04 | 13.4 % |
| V8 (LS), 2 exhausts | 18 / 47 | 41.2 / 43.0 | 34.6 / 34.7 | **34.7 %** | 5.95 (2 ch) | 26.2 % |
| V12 60°, 2 exhausts | 26 / 67 | 65.2 / 66.6 | 49.5 / 51.1 | **51.1 %** | 5.48 | 24.2 % |
| V8 at **20 kHz** | 18 / 47 | 37.8 / 40.2 | 30.2 / 30.7 | **61.3 %** | 5.98 | 26.4 % |

**Where the time goes** (V8 WOT at 4000 rpm, fast build):
- Rigid body plus constraints: **10.2 µs**.
- Gas dynamics: **≈ 3.0 µs per gas substep**.
- The rigid-body part scales with **(constraint rows)²**: `GaussSeidelSleSolver::solveWithLimits` forms a dense `m_M = J·W·Jᵀ` every step (I4 3.5 µs at 27 rows vs V8 10.2 µs at 47 rows).

**Reduced settings**

| setting | µs/step | real-time core | WOT torque at 4000 rpm |
|---|---|---|---|
| V8 10 kHz, 8 substeps | 34.4 | 34.4 % | 719 N·m |
| V8 10 kHz, **4** substeps | 22.0 | **22.0 %** | 715 (−0.5 %) |
| V8 10 kHz, 2 substeps | 16.8 | 16.8 % | 728 |
| V8 **5 kHz**, 4 substeps | 24.0 | 12.0 % | **466 (−35 %)** |
| V8 5 kHz, 2 substeps | 18.9 | 9.4 % | 459 |
| I4 10 kHz, 8 substeps | 14.7 | 14.7 % | 357 |
| I4 5 kHz, 2 substeps | 7.4 | 3.7 % | 237 (−34 %) |

- **The sign of the frequency sensitivity depends on the engine.** 02 measured the EJ25 at **+29 %** at 5 kHz; I measured the LS at −35 %.
- **The simulation frequency is part of an engine's tuning**, not a free quality knob. Gas substeps 8 → 4 is the safe saving.

**Convolution:**
- `ConvolutionFilter` scalar: 3.92 µs/sample at 10,000 taps, 17.3 % of a core per channel at 44.1 kHz.
- AVX2 hand-written: 0.51 µs, **2.25 %**.

**Local-engine budget** (V8, AVX2 FIR, 48 kHz):
- About **37-40 % of a P-core** at 10k/8, about **25 %** at 10k/4. This machine's E-cores are about 25-30 % slower (02 §6.3).
- Per fixed step: **7 ms at 50 Hz, or 2.9 ms at 120 Hz**, of worker time.
- **It must not run on Unity's main thread.**
- A typical free-to-play PC, assumed at about half this machine's per-core speed, needs about 0.5-0.8 of a core for the local V8. Expose a quality tier: 10k/8, 10k/4, and a cheaper IR.

### 6.3 Remote players and AI traffic

| tier | who | method | CPU |
|---|---|---|---|
| R0 | local player | full engine-sim, coupling mode D or A, audio | 0.25-0.6 core |
| R1 | 1-4 nearest remote players | **engine-sim in FOLLOW mode**: dyno hold (`m_dyno.m_enabled = m_hold = true`) with `m_rotationSpeed` lerped per engine step to the replicated, interpolated rpm. Pedal = replicated throttle; ignition and starter replicated. 10 kHz, 4 gas substeps, AVX2 FIR, 3D audio. The sound responds to load naturally (manifold pressure follows throttle against the held speed). No torque is returned | ≈ 0.25 core each (V8), less for I4/I6 |
| R2 | further remote players and AI | **pre-rendered engine-sim loops**: at load or build time, `esp_render_offline` renders 1-2 s steady-state loops on a grid (e.g. idle to redline every 250-500 rpm × {overrun/closed, 50 %, WOT}) with the dyno hold. Runtime is a cheap crossfading granular player driven by the same replicated rpm and throttle. This is the approach of the community "Engine Sim Recorder" for BeamNG and of the forfex fork's `engine-sim-export` | < 0.5 % for dozens of cars; about 1-3 MB per engine as Vorbis |
| R3 | far away | volume-culled or a single drone layer | ~0 |

**Network payload per vehicle at 10-20 Hz:**
- rpm (u16), throttle (u8), MAP or load (u8), and a flags byte (ignition, starter, limiter, clutch, gear).
- About 6 bytes, interpolated on a ~100 ms buffer.
- engine-sim is **not deterministic** (`rand()`, floating-point order), so replicate state, not inputs.

**Server.** Running engine-sim server-side for every player (≈ 0.25+ core each) does not fit a free-to-play budget.
- An authoritative server should use **torque maps baked from `esp_dyno_sweep`**, which 06 §4.7 also proposes for AI.
- Clients run engine-sim for feel and sound.

---

## 7. C ABI for `EngineSimPlugin.dll` and the C# side (Q7)

### 7.1 Principles

- `extern "C"`, `__cdecl`, `__declspec(dllexport)`.
- Every function returns an `int32_t` status and catches all C++ exceptions internally.
- Plain, blittable structs with `uint32_t struct_size` first, for versioning. Strings are UTF-8 `const char*` arguments, never struct members.
- Opaque handle.

**Thread contract**
- Every function except `esp_read_audio` is called from one control thread (Unity's main thread).
- `esp_read_audio` is called **only** from the audio thread and is lock-free.
- `esp_step_begin` hands work to the engine's own worker thread; `esp_step_end` joins it.

**Build settings**
- Static CRT (`/MT`), so there is no redistributable to ship.
- `NDEBUG` always: upstream `~PistonEngineSimulator` asserts on a member that no longer exists (`m_antialiasingFilters`, 02 §2 item 5), so Debug cannot compile otherwise.
- `/fp:fast` (about 10 % faster).
- `/arch:AVX2` only for the FIR kernel, with CPUID dispatch and an SSE2 fallback.

### 7.2 Header

```c
// EngineSimPlugin.h - ABI version 1
#pragma once
#include <stdint.h>
#ifdef __cplusplus
#define ESP_API extern "C" __declspec(dllexport)
#else
#define ESP_API __declspec(dllexport)
#endif

typedef struct EspEngine EspEngine;            /* opaque */

enum { ESP_OK = 0, ESP_ERR_ARG = -1, ESP_ERR_COMPILE = -2, ESP_ERR_STATE = -3,
       ESP_ERR_TIMEOUT = -4, ESP_ERR_INTERNAL = -5, ESP_PENDING = 1 };

enum EspMode {                                  /* driveline coupling, see section 3 */
    ESP_MODE_FREE = 0,          /* engine-sim crank free (neutral/menu/dyno); no external load            */
    ESP_MODE_PROXY = 1,         /* A/B: plugin integrates crank+clutch+driven-wheel proxy; Unity: tyre law */
    ESP_MODE_KINEMATIC = 2,     /* C: Unity imposes driveline speed; plugin returns clutch torque          */
    ESP_MODE_SLAVED_CRANK = 3,  /* D: Unity owns crank; plugin follows trajectory, returns T_ind           */
    ESP_MODE_FOLLOW_RPM = 4     /* remote/AI: dyno-hold at replicated rpm, sound only                      */
};

typedef struct EspCreateDesc {
    uint32_t struct_size;
    int32_t  audio_sample_rate;     /* AudioSettings.outputSampleRate; 0 = no audio                 */
    int32_t  audio_ring_frames;     /* power of two, e.g. 16384                                       */
    int32_t  audio_target_frames;   /* ring fill target, e.g. 2400 (50 ms @ 48 kHz)                  */
    int32_t  sim_frequency;         /* 0 = script's simulation_frequency                              */
    int32_t  fluid_steps;           /* 0 = 8                                                          */
    int32_t  worker_thread;         /* 1 = dedicated worker (esp_step_begin/end), 0 = caller thread   */
    int32_t  fir_max_taps;          /* 0 = 10000                                                      */
} EspCreateDesc;

typedef struct EspEngineInfo {
    uint32_t struct_size;
    char     name[64];
    int32_t  cylinders, banks, exhaust_systems, intakes, sim_frequency;
    double   displacement_m3, redline_rad_s, starter_torque_nm, starter_speed_rad_s;
    double   crank_inertia_kgm2;    /* calibrated J_es (esp_calibrate_inertia), else crankshaft I      */
    int32_t  script_gear_count;     /* from the script's transmission, for defaults only               */
    double   script_gear_ratios[12], script_final_drive, script_tire_radius_m, script_max_clutch_nm;
} EspEngineInfo;

typedef struct EspControls {
    uint32_t struct_size;
    float    throttle;              /* 0..1 -> Engine::setSpeedControl                                 */
    float    clutch;                /* 0..1 engagement -> Transmission::setClutchPressure (+update(0)) */
    int32_t  ignition;              /* IgnitionModule::m_enabled                                       */
    int32_t  starter;               /* StarterMotor::m_enabled                                         */
    float    sim_speed;             /* Time.timeScale -> setSimulationSpeed                            */
} EspControls;

typedef struct EspDrivelineIn {
    uint32_t struct_size;
    int32_t  mode;                  /* EspMode                                                         */
    double   ratio;                 /* gearbox-input speed / driven-wheel speed (gear*final); 0 = neutral; <0 reverse */
    double   max_clutch_torque_nm;
    /* PROXY (A/B) */
    double   input_inertia;         /* kg m^2 at gearbox input (disc, input shaft)                     */
    double   wheel_inertia;         /* kg m^2 at the driven wheels (sum)                               */
    double   wheel_speed;           /* rad/s, Unity's driven-wheel speed (KINEMATIC: imposed; PROXY: used if resync) */
    int32_t  resync_wheel_speed;    /* PROXY: 1 = overwrite plugin wheel speed this step               */
    int32_t  tyre_model;            /* PROXY: 0 = linear (load_* below), 1 = magic formula (mf_*)      */
    double   load_torque_nm;        /* resisting wheel torque at wheel_speed (tyre reaction, rolling)  */
    double   load_slope;            /* dT/domega >= 0 at wheel_speed, N m s/rad                        */
    double   mf_B, mf_C, mf_D_n, mf_E, contact_speed_ms, tyre_radius_m; /* full curve at frozen chassis speed */
    double   brake_torque_nm;       /* driven-wheel brake capacity (bounded friction row, stable at 0) */
    /* FOLLOW_RPM */
    double   follow_rpm;            /* ramped from the previous value across the step                  */
} EspDrivelineIn;

typedef struct EspStepOut {
    uint32_t struct_size;
    int32_t  steps;                 /* engine steps run                                                */
    double   sim_time_s;
    double   rpm, crank_speed;      /* end of step, rad/s forward-positive                             */
    double   indicated_torque_nm;   /* SLAVED: J_es*dw/h - mean(T_pd); other modes: estimate           */
    double   clutch_torque_nm;      /* mean torque through the clutch, forward-positive                */
    double   wheel_speed;           /* PROXY: end-of-step driven-wheel speed                           */
    double   wheel_force_mean_n;    /* PROXY: mean tyre force (apply to chassis at contact patches)    */
    double   manifold_pressure_pa, intake_flow_mol_s, throttle_open, afr, fuel_used_l;
    double   cpu_ms;                /* worker CPU for this step                                        */
    int32_t  audio_frames_buffered, audio_underruns;
    int32_t  rev_limited_hint;      /* rpm >= redline-50 with ignition on (no direct getter upstream)  */
} EspStepOut;

typedef struct EspAudioParams {     /* Synthesizer::AudioParameters subset                              */
    uint32_t struct_size;
    float volume, convolution, hf_gain, low_freq_noise, high_freq_noise, leveler_target;
} EspAudioParams;

ESP_API int32_t    esp_version(void);
ESP_API EspEngine* esp_create(const EspCreateDesc* desc, const char* es_library_dir_utf8, const char* temp_dir_utf8);
ESP_API void       esp_destroy(EspEngine* e);   /* stop the AudioSource first; the ring stays valid until destroy returns */

ESP_API int32_t esp_load_script(EspEngine* e, const char* mr_path_utf8, const char* top_node_utf8 /* NULL = "main" */,
                                char* err_utf8, int32_t err_capacity);
ESP_API int32_t esp_get_info(EspEngine* e, EspEngineInfo* out);

ESP_API int32_t esp_set_controls(EspEngine* e, const EspControls* c);      /* latched, applied at next step */
ESP_API int32_t esp_set_audio_params(EspEngine* e, const EspAudioParams* p);

/* Synchronous: runs round((acc+dt)*f) steps on the calling thread. */
ESP_API int32_t esp_step(EspEngine* e, double dt, const EspDrivelineIn* in,
                         const double* crank_samples, int32_t n_samples, EspStepOut* out);
/* Asynchronous: same work on the engine's worker. end() with timeout_us = 0 polls (returns ESP_PENDING). */
ESP_API int32_t esp_step_begin(EspEngine* e, double dt, const EspDrivelineIn* in,
                               const double* crank_samples, int32_t n_samples);
ESP_API int32_t esp_step_end(EspEngine* e, EspStepOut* out, int32_t timeout_us);

/* Audio thread only. Lock-free. Writes frames*channels interleaved floats (mono duplicated); zero/decay on underrun. */
ESP_API int32_t esp_read_audio(EspEngine* e, float* dst, int32_t frames, int32_t channels);

/* Offline tools (blocking, never while stepping): */
ESP_API int32_t esp_calibrate_inertia(EspEngine* e, double* j_eff_kgm2);
ESP_API int32_t esp_dyno_sweep(EspEngine* e, double rpm_from, double rpm_to, int32_t points, float throttle,
                               double settle_s, double* torque_nm, double* power_w);
ESP_API int32_t esp_render_offline(EspEngine* e, double rpm, float throttle, double seconds, float* dst, int32_t frames);
```

### 7.3 Inside the plugin (sketch)

```cpp
class UnitySim final : public PistonEngineSimulator {           // engine-sim untouched; all access is protected/public
public:
    void attach(UnityTransmission *t) {                         // call right after loadSimulation()
        m_system->removeConstraint(&m_vehicleDrag);             // our load instead of engine-sim's road model
        m_system->addForceGenerator(&m_wheelLoad);              // PROXY/KINEMATIC load on m_vehicleMass
        m_system->addForceGenerator(&m_slavePd);                // SLAVED_CRANK PD on the output crank
        m_slavePd.crank = &m_engine->getOutputCrankshaft()->m_body;
        m_wheelLoad.proxy = &m_vehicleMass;
        for (int i = 0; i < m_engine->getCylinderCount(); ++i)   // fix the hard-coded 10 kHz delay filters
            m_delayFilters[i].initialize(exhaustDelay(i), getSimulationFrequency());
    }
    atg_scs::RigidBody &proxy() { return m_vehicleMass; }
protected:
    void writeToSynthesizer() override;                          // upstream body minus `static double lastValveLift[8]`
    WheelLoad m_wheelLoad; SlavedCrankPD m_slavePd;
};

int Instance::step(double dt, const EspDrivelineIn &in, const double *crank, int nCrank, EspStepOut &o) {
    applyLatchedControls();                                      // throttle, ignition, starter, clutch -> tr->update(0)
    configureMode(in);                                           // proxy I and v_theta, PD enable, dyno follow, etc.
    acc += dt * f * simSpeed;  const int n = (int)acc;  acc -= n;
    sim->startFrame(n / (f * simSpeed));                         // exact n steps (target latency is 0)
    sim->synthesizer().setInputSampleRate(f * simSpeed * (1.0 + driftTrim()));
    Accumulators a{};
    for (int k = 0; sim->simulateStep(); ++k) {
        if (in.mode == ESP_MODE_SLAVED_CRANK) pd.track(lerp(crank, nCrank, (k + 0.5) / n));
        if (in.mode == ESP_MODE_FOLLOW_RPM)   sim->m_dyno.m_rotationSpeed = lerp(prevRpm, in.follow_rpm, (k + 1.0) / n);
        a.add(tr->clutchTorque(), wheelLoad.lastForce, pd.lastT);
    }
    if (audio) renderSync(sim->synthesizer(), ring);             // section 4.3; before endFrame
    sim->endFrame();
    fill(o, a);                                                  // means, rpm, MAP (Engine getters), cpu_ms, ring fill
    return ESP_OK;
}
```

**Load sequence in `esp_load_script`:**
1. Take the global compile mutex.
2. Write the wrapper `.mr` to the temp folder.
3. `*es_script::Compiler::output() = {}`.
4. Run a `piranha::Compiler(&rules)` with the absolute es search path and the `.mr` extension.
5. `execute()`.
6. `new UnitySim`; `initialize({NsvOptimized})`; `loadSimulation(engine, ownVehicle, ownUnityTransmission)`; `setFluidSimulationSteps(...)`; `setSimulationFrequency(...)`; `attach(...)`.
7. Re-initialise the synthesizer at the output rate; load the IR WAVs with our own reader, resample, and `initializeImpulseResponse`.
8. Audio parameters from the engine (`getInitialJitter/Noise/HighFrequencyGain`), with `volume` around 0.5.
9. `setTargetSynthesizerLatency(0)`; `synthesizer().endInputBlock()`.
10. `tr->changeGear(0)`, `setClutchPressure(0)`, `update(0)`.
11. Construct one `Function` on the calling thread first, so `DefaultGaussianFilter` is not created concurrently.

**Destroy:** stop the worker; `synthesizer().destroy()` (upstream never does this); `sim->destroy()`; `engine->destroy()`; delete. Functions, camshafts and valvetrains leak per load (small; pool engines if they spawn often).

### 7.4 C# side

```csharp
// Assets/Scripts/EngineSim/EngineSimNative.cs   (DLL at Assets/Plugins/x86_64/EngineSimPlugin.dll)
using System; using System.Runtime.InteropServices; using System.Text;

namespace Backroads.EngineSim {
  public enum EspMode { Free = 0, Proxy = 1, Kinematic = 2, SlavedCrank = 3, FollowRpm = 4 }

  [StructLayout(LayoutKind.Sequential)] public struct EspCreateDesc {
      public uint structSize; public int audioSampleRate, audioRingFrames, audioTargetFrames,
                                         simFrequency, fluidSteps, workerThread, firMaxTaps; }
  [StructLayout(LayoutKind.Sequential)] public struct EspControls {
      public uint structSize; public float throttle, clutch; public int ignition, starter; public float simSpeed; }
  [StructLayout(LayoutKind.Sequential)] public struct EspDrivelineIn {
      public uint structSize; public int mode; public double ratio, maxClutchTorqueNm,
      inputInertia, wheelInertia, wheelSpeed; public int resyncWheelSpeed, tyreModel;
      public double loadTorqueNm, loadSlope, mfB, mfC, mfDn, mfE, contactSpeedMs, tyreRadiusM, brakeTorqueNm, followRpm; }
  [StructLayout(LayoutKind.Sequential)] public struct EspStepOut {
      public uint structSize; public int steps; public double simTimeS, rpm, crankSpeed, indicatedTorqueNm,
      clutchTorqueNm, wheelSpeed, wheelForceMeanN, manifoldPressurePa, intakeFlowMolS, throttleOpen, afr, fuelUsedL, cpuMs;
      public int audioFramesBuffered, audioUnderruns, revLimitedHint; }

  public static class Native {
      const string Dll = "EngineSimPlugin";
      [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_version();
      [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
      public static extern IntPtr esp_create(in EspCreateDesc d, byte[] esDirUtf8, byte[] tempDirUtf8);
      [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void esp_destroy(IntPtr e);
      [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
      public static extern int esp_load_script(IntPtr e, byte[] mrPathUtf8, byte[] topNodeUtf8, byte[] err, int errCap);
      [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_set_controls(IntPtr e, in EspControls c);
      [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
      public static extern int esp_step_begin(IntPtr e, double dt, in EspDrivelineIn d, double[] crank, int n);
      [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int esp_step_end(IntPtr e, out EspStepOut o, int timeoutUs);
      [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
      public static extern int esp_read_audio(IntPtr e, [In, Out] float[] dst, int frames, int channels);  // blittable: pinned, no copy
      public static byte[] Utf8(string s) => s == null ? null : Encoding.UTF8.GetBytes(s + "\0");
  }

  // One per vehicle. Pipelined (mode D): kick after the BRV step, collect at the next step, never block.
  public sealed class EngineSimEngine : IDisposable {
      public IntPtr Handle { get; private set; }
      readonly double[] crankSamples = new double[9];
      EspStepOut last; bool inFlight;
      public EngineSimEngine(string esDir, string tempDir, string mrPath, string topNode = null) {
          var d = new EspCreateDesc { structSize = (uint)Marshal.SizeOf<EspCreateDesc>(),
              audioSampleRate = UnityEngine.AudioSettings.outputSampleRate, audioRingFrames = 16384,
              audioTargetFrames = UnityEngine.AudioSettings.outputSampleRate / 25, workerThread = 1 };
          Handle = Native.esp_create(d, Native.Utf8(esDir), Native.Utf8(tempDir));
          var err = new byte[2048];
          if (Native.esp_load_script(Handle, Native.Utf8(mrPath), Native.Utf8(topNode), err, err.Length) != 0)
              throw new Exception(Encoding.UTF8.GetString(err).TrimEnd('\0'));
      }
      public EspStepOut Collect() {                     // start of FixedUpdate k+1 (or end of k for fork-join modes)
          if (inFlight && Native.esp_step_end(Handle, out var o, 0) == 0) { last = o; inFlight = false; }
          return last;                                  // if still pending: reuse last (06: baked-map fallback after N misses)
      }
      public void Kick(float dt, in EspDrivelineIn d, ReadOnlySpan<double> crank) {
          crank.CopyTo(crankSamples);
          if (!inFlight) { Native.esp_step_begin(Handle, dt, d, crankSamples, crank.Length); inFlight = true; }
      }
      public void Dispose() { if (Handle != IntPtr.Zero) { Native.esp_step_end(Handle, out _, 100000); Native.esp_destroy(Handle); Handle = IntPtr.Zero; } }
  }

  [UnityEngine.RequireComponent(typeof(UnityEngine.AudioSource))]
  public sealed class EngineSimAudio : UnityEngine.MonoBehaviour {
      public EngineSimEngine Engine;                    // set by the vehicle; disable this component before Dispose()
      void OnAudioFilterRead(float[] data, int channels) {
          var h = Engine?.Handle ?? IntPtr.Zero;
          if (h == IntPtr.Zero) { Array.Clear(data, 0, data.Length); return; }
          Native.esp_read_audio(h, data, data.Length / channels, channels);   // no allocation, no logging here
      }
  }
}
```

**Unity 6.3+ alternative.** Implement `IAudioGenerator` on a MonoBehaviour, with a `[BurstCompile] struct Realtime : GeneratorInstance.IRealtime`. Its `Process(in RealtimeContext, Pipe, ChannelBuffer buffer, Arguments)` calls a `[DllImport]` `esp_read_audio(IntPtr, float*, int, int)` into a scratch buffer and copies it into `buffer[ch, frame]`. Pass the handle at creation, or through a `Pipe` message.
- The interface names are from the 6000.3 manual page "Create a generator".
- Verify the exact signatures and whether `ChannelBuffer` exposes a raw pointer, in a 30-minute spike in 6000.3.20f1.

**Editor note.** Unity loads native DLLs once and keeps them locked until the Editor restarts. For fast iteration, load a copy of the DLL with `LoadLibrary`/`GetProcAddress` from a thin C# loader, or accept Editor restarts.

**Assets layout.** Put `es/**` and the engine `.mr`/`.wav` files under `Assets/StreamingAssets/EngineSim/`. These are raw files readable from the filesystem on Windows standalone.

---

## 8. Upstream defects that matter, and plugin-side workarounds (no fork)

| # | defect (file:line) | effect | workaround in plugin |
|---|---|---|---|
| 1 | `static double lastValveLift[8]` written for every cylinder (`piston_engine_simulator.cpp:379, 400`) | out-of-bounds write for 9-, 10- and 12-cylinder engines (4 shipped); shared by every instance and thread | override `writeToSynthesizer()` (protected virtual) with the same body minus the array (Appendix A.3). Tested with a V12 |
| 2 | `#include "../include/delta.h"` (`synthesizer.cpp:4`) | needs delta-studio headers | empty shim headers at `shim/delta-studio/...` |
| 3 | piranha `path.cpp` uses Boost | Boost dependency | `piranha_path_std.cpp` (compiled) instead of `path.cpp` |
| 4 | piranha has no generated parser | flex/bison needed | run win_flex_bison once and commit the outputs |
| 5 | `startFrame` ±10 % step adaptation (`simulator.cpp:77-86`); uninitialised `Synthesizer::m_latency` | engine time ≠ physics time | `setTargetSynthesizerLatency(0)`, `endInputBlock()` once at init, synchronous drain before `endFrame` |
| 6 | `Transmission::update` after `process` (`simulator.cpp:107-111`) | clutch limits lag one step; ±`DBL_MAX` on the first step | call `transmission->update(0)` after every control change |
| 7 | `DelayFilter::initialize(delay, 10000.0)` (`piston_engine_simulator.cpp:226`) | wrong exhaust delay for engines not at 10 kHz | re-initialise in `attach()` |
| 8 | 44.1 kHz hard-coded (`simulator.cpp:204-212`) | rate mismatch with Unity's 48 kHz | `synthesizer().destroy(); initialize(48 kHz)`, and resample the IRs |
| 9 | `PistonEngineSimulator::destroy` skips `Simulator::destroy`; `~Simulator` asserts; script `Function`/`Camshaft`/`Valvetrain` never freed | leaks; Debug asserts | build with `NDEBUG`; call `synthesizer().destroy()` ourselves; pool engines |
| 10 | `~PistonEngineSimulator` asserts on a nonexistent member | Debug build fails (02) | `NDEBUG` for the vendored sources |
| 11 | `Function::DefaultGaussianFilter` lazy static (`function.cpp:19-22`); `Compiler::s_output` static; CWD-relative search paths and `error_log.log` | thread races; path problems | construct one `Function` at plugin init; global compile mutex; own `piranha::Compiler` wrapper with absolute paths |
| 12 | `readAudioOutput`/`renderAudio` share `m_lock0`; `writeInput` is unlocked | blocking or racy on an audio thread | never start the synthesizer thread; `renderSync` + SPSC ring |
| 13 | output clips (leveler target 30,000 → int16) | distortion | `volume` ≈ 0.5 |
| 14 | `ConvolutionFilter::f` scalar | 12-17 % of a core per channel | plugin-side render with an AVX2 or FFT FIR, using the public `Synthesizer` members |

**A fork is only needed for new physics:** compression ignition, boost, exhaust brake, and optionally a sparse or banded constraint solver to speed up 10-12 cylinder engines.

Keep the vendored copy **unmodified**: a git subtree of upstream `85f7c3b` plus submodule commits, with MIT notices for engine-sim, scs and piranha. All changes live in plugin sources.

---

## 9. Open questions and how to resolve them

1. **piranha end-to-end.** Get approval to fetch win_flex_bison 2.5.25 into `D:\Backroads\tools\` (not system-wide). Generate the parser, build the plugin, then load and dyno-sweep all 27 shipped engines headless. Compare each with its expected character, e.g. 02's EJ25 against the script.
2. **The LS torque discrepancy** (715 N·m) — the same run decides whether my rebuild or the script is off.
3. **Unity audio paths.** In 6000.3.20f1, test `OnAudioFilterRead` with `spatialBlend = 1` (is it attenuated and panned?) and an `IAudioGenerator` with Burst DllImport. Measure underruns at Best latency.
4. **D in BRV.** Run 06's probe P4 scenarios (clutch dump, stall, neutral rev, semi in top gear) with the real BRV solver and engine-sim's spark-cut limiter. Measure overshoot vs a 2-step lag, and add a `∂T/∂ω` feed-forward if needed.
5. **Listening tests** at 10k/8 vs 10k/4, AVX2 FIR vs reference, and 48 kHz re-initialisation vs 44.1 + resampling. Nobody has listened yet; the probe WAVs are in `scratchpad/esprobe/out-fast/` and 02's are in `D:\Backroads-ref\probe\`.
6. **Min-spec CPU.** Run the probe on a 6-core, ~3 GHz laptop to set the quality tiers.
7. **Diesel/turbo fork scope.** If wanted, evaluate kyan0s-git/engine-sim's forced-induction code as a *reference* after a licence and code review. Do not depend on it.

---

## Appendix A: Probe (evidence)

**Location:** `C:\Users\kaiwr\AppData\Local\Temp\claude\D--Backroads\3c88f822-b5dc-4e61-a4a0-41a5ef8b7b47\scratchpad\esprobe\`

**Files**
- `CMakeLists.txt`: scs, the 41 core sources, the delta shim, `piranha_path_shim`, `piranha_check`.
- `bench.cpp`: about 900 lines.
- `piranha_path_std.cpp`.
- `shim/`.

**Build and run**
1. `cmake -S . -B build-fast -G "Visual Studio 17 2022" -A x64 -DES_FAST_MATH=ON`
2. `cmake --build build-fast --config Release --parallel 16`
3. Run modes:
   - `build-fast\Release\bench.exe out-fast`: CPU table and WAVs
   - `... out-fast breakdown`: substeps, frequency, FIR
   - `... out-fast cosim`: the §3.5 table

**A.1 Building engines in C++**

`buildEngine(Spec)` mirrors `EngineNode::buildEngine`:
- `Engine::initialize`, crank and journals, intake, exhausts
- per bank: `CylinderBank`, `Piston`, `ConnectingRod`, two `Camshaft`s with harmonic lobes (math copied from `GenerateHarmonicCamLobeNode`, `actions.h:437-463`), `StandardValvetrain`, `CylinderHead` with `k_28inH2O` flow tables, per-cylinder intake and exhaust via `setIntake/setExhaustSystem`
- `IgnitionModule::setFiringOrder`, `Fuel` (`maxDilutionEffect = 10`, the `.mr` default), `CombustionChamber::initialize`

The firing relation used is lobe centreline = φ ± (360° ± LSA), with φ ≡ journal angle (mod 360) on bank 0; it was derived from `07_gm_ls.mr`. Idle came out at 700-815 rpm for all four test engines.

**A.2 Simulator creation without delta**

```cpp
ProbeSim *s = new ProbeSim;  Simulator::Parameters p; p.systemType = Simulator::SystemType::NsvOptimized;
s->initialize(p); s->loadSimulation(e, v, t); s->setFluidSimulationSteps(8);
s->setSimulationFrequency((int)e->getSimulationFrequency());
/* audio params from engine; own WAV reader -> synthesizer().initializeImpulseResponse(pcm, n, vol, i) */
s->setTargetSynthesizerLatency(0.0); s->synthesizer().endInputBlock();
e->getIgnitionModule()->m_enabled = true;   // defaults to false
```

**A.3 `writeToSynthesizer` override (fix for defect 1)**

The body is identical to upstream (`piston_engine_simulator.cpp:370-413`) except that the `static double lastValveLift[8]` declaration and its assignment are removed. It is used for the V12 runs above.

**A.4 Coupling generators**

```cpp
class WheelLoad : public atg_scs::ForceGenerator {           // modes A/B/C: load on the driveline proxy (m_vehicleMass)
public: atg_scs::RigidBody *proxy; double G, v; Tyre tyre; int mode; double F0, dF, w0, Fconst;
    void apply(atg_scs::SystemState *s) override {
        const int i = proxy->index; const double w = -s->v_theta[i] / G;       // forward wheel speed
        const double F = mode == 0 ? tyreForce(w, v, tyre) : mode == 1 ? F0 + dF * (w - w0) : Fconst;
        s->t[i] += F * tyre.r / G; }                                           // resists forward (negative) spin
};
class SlavedCrankPD : public atg_scs::ForceGenerator {       // mode D
public: atg_scs::RigidBody *crank; double kp, kd, cmd, err, lastT, Tmax = 3000; bool enabled;
    void apply(atg_scs::SystemState *s) override {
        if (!enabled) { lastT = 0; return; }
        const double w = -s->v_theta[crank->index];
        double T = std::clamp(-kp * err - kd * (w - cmd), -Tmax, Tmax);
        s->t[crank->index] += -T; lastT = T; }
};
// per engine step (mode D): pd.err += (w_es - cmd) / f;   T_ind = J*(w_end - w_start)/h - mean(lastT)
```

**A.5 AVX2 FIR (0.51 µs/sample at 10,000 taps)**

Keep the input history twice (`hist[pos]` and `hist[pos+N]`) so the window `&hist[pos] … +N` is contiguous. Run 4 independent `_mm256_fmadd_ps` accumulators over 32 taps per iteration, then a horizontal sum and a scalar tail. The output matched the scalar `ConvolutionFilter` checksum (0.199122 vs 0.199123).

**A.6 Boost-free `piranha::Path`**

```cpp
#include <filesystem>
namespace boost { namespace filesystem {
    class path : public std::filesystem::path { public: using std::filesystem::path::path; path() = default;
        path(const std::filesystem::path &p) : std::filesystem::path(p) {} }; } }
#include "piranha/include/path.h"          // unmodified header; then implement Path:: with std::filesystem
// equivalent(...) via std::filesystem::equivalent(a, b, ec) (fallback: lexically_normal compare),
// is_complete() -> is_absolute(), exists() -> std::filesystem::exists()
```

## Appendix B: Web sources used

- Unity Manual: [Scriptable audio processors and generators (6000.3)](https://docs.unity3d.com/Manual/audio-scriptable-processors-generators.html)
- Unity Manual: [Create a generator (6000.3)](https://docs.unity3d.com/Manual/audio-scriptable-processors-example-creating-a-generator.html)
- Unity Scripting API: [IAudioGenerator](https://docs.unity3d.com/ScriptReference/Audio.IAudioGenerator.html)
- Unity Scripting API: [AudioSource.generator](https://docs.unity3d.com/ScriptReference/AudioSource-generator.html)
- Unity Scripting API: [OnAudioFilterRead (5.3)](https://docs.unity3d.com/es/530/ScriptReference/MonoBehaviour.OnAudioFilterRead.html)
- Unity Scripting API: [AudioSource.spatialBlend](https://docs.unity3d.com/ScriptReference/AudioSource-spatialBlend.html)
- Unity issue tracker: [OnAudioFilterRead audio disappears with spatialize](https://issuetracker-mig.prd.it.unity3d.com/issues/audio-sound-generated-by-onaudiofilterread-disappears-when-spatialize-is-enabled)
- Unity issue tracker: [spatial blend has no effect with OnAudioFilterRead](https://issuetracker-mig.prd.it.unity3d.com/issues/spatial-blend-parameter-of-an-audio-source-has-no-effect-when-using-onaudiofilterread-to-generate-audio)
- Burst manual: [DllImport and internal calls](https://docs.unity3d.com/Packages/com.unity.burst@1.8/manual/csharp-burst-intrinsics-dllimport.html)
- [engine-sim Community projects wiki](https://github.com/ange-yaghi/engine-sim/wiki/Community-projects)
- [Engine-Simulator/engine-sim-community-edition](https://github.com/Engine-Simulator/engine-sim-community-edition)
- [ange-yaghi/piranha](https://github.com/ange-yaghi/piranha) (MIT licence shown)
- [kyan0s-git/engine-sim](https://github.com/kyan0s-git/engine-sim) and [its commit history](https://github.com/kyan0s-git/engine-sim/commits/master)
- [forfex/engine-sim](https://github.com/forfex/engine-sim) (audio export for games)
