# 02: engine-sim headless build probe

*Task: enginesim-build. Date: 2026-10-18. Machine: Core Ultra 9 275HX (8 P-cores + 16 E-cores, 24 logical), 31 GB RAM, Windows 11 26200, VS 2022 Community 17.14.36 (MSVC 19.44.35228, toolset 14.44.35207), Windows SDK 10.0.26100.0, CMake 4.3.4.*
*engine-sim checkout: `D:\Backroads-ref\engine-sim` @ `85f7c3b` (2023-01-22, the last upstream commit). It was not modified.*
*All work is in `D:\Backroads-ref\probe`.*

---

## TL;DR

**PROVED.** engine-sim's simulation core (rigid-body crank train, 1-D gas dynamics, combustion, ignition, synthesizer) builds headless with CMake and VS 2022 (x64 Release). It needs no delta-studio, no piranha, no flex/bison, no Boost and no network, and it runs without any renderer.

- **Build fixes needed:** one patched source file (`synthesizer.cpp` includes a delta-studio header it does not use) plus a 60-line replacement `CMakeLists.txt`. The upstream top-level CMake cannot work here: it unconditionally adds delta-studio and piranha and downloads googletest.
- **Scripting:** the piranha `.mr` path is **blocked**. flex/bison are not installed and piranha commits no generated parser files; it also needs Boost.Filesystem. I took the fallback: a C++ port of the shipped default engine `assets/engines/atg-video-2/01_subaru_ej25_eh.mr`, about 300 lines in `probe/src/ej25.cpp`.
- **The engine behaves like the real car.** It cranks, catches and idles at ~770-840 rpm. On a fixed load it runs at 3317 / 5106 / 5216 rpm for 25 / 60 / 100 % pedal. The wide-open-throttle (WOT) dyno peak is **229 Nm @ 4500 rpm and 170 hp @ 6500 rpm**. A real EJ25 is roughly 166 lb-ft (225 Nm) and 165-173 hp, depending on model year.
- **Cost, full quality (20 kHz, 8 fluid substeps), one thread:**
  - physics: **337 ms CPU per simulated second on a P-core** (436 ms on an E-core)
  - exhaust-convolution audio: **+99 ms**
  - total: **434 ms/s ≈ 0.43 of one P-core ≈ 7.2 ms of CPU per 60 fps frame**
- **Lightweight settings are much cheaper:**
  - 10 kHz / 4 substeps: **108 ms/s**, WOT torque about +5 %
  - 5 kHz / 2 substeps: **40 ms/s**, WOT torque about +29 % too high
- **Parallel engines scale almost linearly:** 16 full-quality engines physics-only use 7.5 cores, and each still runs ≥ 2× real time.
- **1 local + N remote:**
  - 1 local full-quality engine with audio + 23 remote engines at 10 kHz/4: **4.4 cores in total**
  - the same with remotes at 5 kHz/2: **2.0 cores**
  - the local engine stays ≥ 1.77× real time in both cases
- **Verdict:** **1 local engine-sim + N lightweight remote engines at 60 fps is feasible.** Conditions:
  1. Every engine runs on worker threads, never the Unity main thread.
  2. Remote engines use the reduced settings.
  3. Remote engines get no per-engine convolution reverb (it is ~99 ms/s each and compiler flags do not speed it up).
  4. Low-end player PCs get a quality tier and a cap on how many remote engines are fully simulated.
- **Audio:** `D:\Backroads-ref\probe\probe_out.wav` (37.3 s, 44.1 kHz mono 16-bit: start → idle → 25 / 60 / 100 % → idle). Its dominant pitch equals the simulated firing frequency (110 / 170 / 175 Hz at 3317 / 5106 / 5216 rpm). Default levels clip 0.4-3 % of samples under load, which a volume change fixes. Nobody has listened to it yet.

---

## 1. Exact build commands

Everything below runs from any shell; no admin rights and no network are needed.

```powershell
# configure (VS 2022, x64) - default flags
cmake -S D:\Backroads-ref\probe -B D:\Backroads-ref\probe\build -G "Visual Studio 17 2022" -A x64
# build the engine-sim core library and the probe
cmake --build D:\Backroads-ref\probe\build --config Release --target engine-sim probe
# run (full run is about 6 min; --quick roughly halves the benchmark durations)
D:\Backroads-ref\probe\build\Release\probe.exe                 # parts A-F
D:\Backroads-ref\probe\build\Release\probe.exe --parts AB      # only start/throttle/WAV + dyno sweep

# optional second variant: /fp:fast /arch:AVX2
cmake -S D:\Backroads-ref\probe -B D:\Backroads-ref\probe\build-fastmath -G "Visual Studio 17 2022" -A x64 -DPROBE_FAST_MATH=ON
cmake --build D:\Backroads-ref\probe\build-fastmath --config Release --target probe
```

`D:\Backroads-ref\probe\build.ps1 [-FastMath]` does the same in one step.

The probe CMake builds four targets straight out of the untouched engine-sim tree:

| target | sources | notes |
|---|---|---|
| `simple-2d-constraint-solver` (static) | `dependencies/submodules/simple-2d-constraint-solver/src/*.cpp` (29 files) | same list as its own CMakeLists, without gtest |
| `csv-io` (static) | `csv-io/src/information.cpp`, `csv_data.cpp` | upstream links it into `engine-sim`, but the core never calls it |
| **`engine-sim`** (static) | exactly the 41 `src/*.cpp` files of upstream `add_library(engine-sim ...)`; `synthesizer.cpp` comes from `probe/patched/` | output `build\Release\engine-sim.lib` |
| `probe` (exe) | `probe/src/probe.cpp`, `ej25.cpp`, `wav.cpp` | links `engine-sim` |

Compiler flags are MSVC defaults for a CMake Release build: `/O2 /Ob2 /DNDEBUG /MD`, `/fp:precise`, SSE2, C++17. I added `/W1 /MP`. The core compiled with **zero errors and zero warnings at /W1** on the first attempt once the one include was removed.

---

## 2. Every error encountered and what fixed it

| # | Where | Error | Fix |
|---|---|---|---|
| 1 | upstream top-level `CMakeLists.txt` (configured with `-DDISCORD_ENABLED=OFF -DDTV=OFF -DFETCHCONTENT_FULLY_DISCONNECTED=ON`, so nothing is downloaded) | `FetchContent_Declare(googletest URL https://github.com/google/googletest/archive/6092810...zip)` appears in **four** CMakeLists (engine-sim, scs, csv-io, piranha). With no network, no gtest targets exist, so `set_property could not find TARGET gmock/gmock_main/gtest/gtest_main` at `CMakeLists.txt:40-43` and in every submodule | Don't use upstream CMake. My own `probe/CMakeLists.txt` builds only the libraries |
| 2 | `dependencies/submodules/CMakeLists.txt:1` | `add_subdirectory(delta-studio)`: *"does not contain a CMakeLists.txt file"* (submodule not fetched). Then `set_property could not find TARGET delta-basic / delta-basic-demo / delta-core / delta-physics`. Upstream `engine-sim` also `target_link_libraries(... delta-basic)`, and `engine-sim-app` is always defined, with no option to skip it | Own CMake; the core library does not link delta |
| 3 | `dependencies/submodules/CMakeLists.txt` | `add_subdirectory(piranha)` runs **unconditionally**, even with `-DPIRANHA_ENABLED=OFF`. piranha then fails: `Could NOT find FLEX (missing: FLEX_EXECUTABLE)` (`piranha/CMakeLists.txt:31`). BISON and `find_package(Boost 1.40 COMPONENTS filesystem REQUIRED)` would fail next | Own CMake (see §3 for what piranha would need) |
| 4 | `src/synthesizer.cpp:4` | `#include "../include/delta.h"`, which includes `<delta-studio/include/yds_core.h>`. It is the **only** core file that touches delta-studio and it uses nothing from it | Copied to `probe/patched/synthesizer.cpp`: removed the include, added `#include <cstring>` defensively for `memset`, and rewrote `"../include/x.h"` as `"x.h"` so the copy compiles from a different folder |
| 5 | (not hit in Release; latent) `src/piston_engine_simulator.cpp:36` | `~PistonEngineSimulator()` has `assert(m_antialiasingFilters == nullptr)`, but that member no longer exists. It compiles only because `NDEBUG` removes the assert, so **Debug builds of the core cannot compile** (verified, see §8) | A Unity fork must delete that line or always build the plugin with `NDEBUG` |
| 6 | (CMake 4.x note) | none: all engine-sim CMakeLists say `cmake_minimum_required(VERSION 3.10)`, which CMake 4 still accepts | n/a |

No warning suppression or other source change was needed. The rest of the core is portable C++17 with only `std::thread` / `std::mutex` (in `Synthesizer`) as OS-level dependencies.

---

## 3. The piranha (`.mr` script) path: status and exact requirements

**Status: not built.** I did not download anything. Here is what is missing.

1. **flex + bison: required, not installed, and not pre-generated.**
   - Generated parser files are **not** in the piranha tree. A search found no `*.auto.*`, `location.hh` or `stack.hh`; the only "parser" hits are the hand-written `ir_parser_structure.*`.
   - piranha's CMake generates `scanner.auto.cpp/.h` from `flex-bison/scanner.l` and `parser.auto.cpp/.h` from `flex-bison/specification.y` at configure time.
   - `specification.y` needs **bison ≥ 3.2** (`%require "3.2"`, `%skeleton "lalr1.cc"`, `api.value.type variant`).
   - `scanner.l` needs a flex that supports `%option c++` (`yyclass="piranha::Scanner"`). `FlexLexer.h` is vendored at `piranha/dependencies/libraries/flex/include`.
   - **win_flex_bison 2.5.25** (portable zip, `win_flex` 2.6.4 + `win_bison` 3.8.2, from github.com/lexxmark/winflexbison/releases) meets both requirements. CMake's `FindFLEX`/`FindBISON` already search for the names `win_flex` / `win_bison`. Put the unzipped folder on `PATH`, or pass `-DFLEX_EXECUTABLE=...\win_flex.exe -DBISON_EXECUTABLE=...\win_bison.exe`.
2. **Boost.Filesystem (compiled library): required, not installed** (no vcpkg; `VC\vcpkg\vcpkg.exe` is absent).
   - It is used **only** in `piranha/src/path.cpp` (60 lines) and `include/path.h`, for `path`, `/`, `parent_path`, `is_absolute`, `exists` and `equivalent`.
   - A patched copy using C++17 `std::filesystem` would remove Boost entirely, at an estimated 30 minutes of work.
3. **googletest download** in piranha's CMake. As in §2, skip the test targets in a custom CMake.
4. Then compile `engine-sim-script-interpreter`: `scripting/src/{channel_types,compiler,engine_context,language_rules}.cpp`, against piranha with `ATG_ENGINE_SIM_PIRANHA_ENABLED`. The API is `es_script::Compiler::initialize() / compile(path) / execute() → Output{engine, vehicle, transmission}` (see `EngineSimApplication::loadScript()`).

Estimated effort once win_flex_bison is available (the user would download it, or approve its download): about 1-2 hours. **Licence note:** the piranha submodule at `432f0b1` has **no LICENSE file**, and its README does not mention one. engine-sim itself, simple-2d-constraint-solver and csv-io are MIT. Check github.com/ange-yaghi/piranha before redistributing piranha in a game. A cheap way to avoid the question is to not ship piranha at all (see §9).

---

## 4. Engine without scripting: the C++ EJ25 port

`probe/src/ej25.cpp`, `buildSubaruEj25()`, is a line-by-line translation of `assets/engines/atg-video-2/01_subaru_ej25_eh.mr`, which is what `assets/main.mr` loads by default. Defaults come from `es/objects/objects.mr`, `es/actions/actions.mr`, `es/utilities/utilities.mr` and `es/sound-library/impulse_responses.mr`. The call order mirrors `es_script::EngineNode::buildEngine()` (`scripting/include/engine_node.h`):

1. `Engine::initialize`: 1 crank, 2 banks, 4 cylinders, 1 intake, 1 exhaust; `DirectThrottleLinkage` with γ = 2; 20 kHz.
2. `Crankshaft::initialize`, then 4 rod-journal angles.
3. Per bank:
   - `CylinderBank`, `Piston`, `ConnectingRod`
   - two `Camshaft`s using the harmonic lobe generator copied from `GenerateHarmonicCamLobeNode`
   - `StandardValvetrain`
   - `CylinderHead` with flow tables built through `GasSystem::k_28inH2O`
   - `ExhaustSystem` and `Intake`
4. `IgnitionModule` with firing order 0/180/360/540° and the timing curve.
5. `Fuel` (note: the script default `max_dilution_effect` is **10.0**, not the C++ default 50.0), then `CombustionChamber`s.
6. `Vehicle` ("impreza") and the 6-speed `Transmission`.
7. `engine->createSimulator()`, `setSimulationFrequency(20000)`, synthesizer audio parameters, and the impulse response read from `es/sound-library/new/minimal_muffling_02.wav` (my own 16-bit WAV reader replaces delta's `ysWindowsAudioWaveFile`).
8. **`IgnitionModule::m_enabled = true`.** It defaults to false and the GUI toggles it with a key.

The result has a displacement of 2.457 L (EJ25 nominal 2.457 L) and the dyno curve in §6.2, which suggests the port is faithful.

**Headless frame loop**, identical to `EngineSimApplication::process()` without UI:

```cpp
sim->startFrame(1/60.0);
while (sim->simulateStep()) { /* per-step load update */ }
sim->endFrame();
```

Audio is pumped on the **same thread** instead of `Synthesizer::audioRenderingThread()`: `renderAudio()`, `readAudioOutput()` and `endInputBlock()` run until the input ring is empty. This works without editing engine-sim because `Synthesizer`'s members are public (`//protected:` is commented out).

**Load:** engine-sim's `Dynamometer` (public member `Simulator::m_dyno`) is used as a brake. With `m_rotationSpeed = 0` and `m_hold = false`, its torque saturates at `m_maxTorque`, which the probe sets every solver step to `T = 200 Nm·(rpm/5000)²` (a road-load-like curve). The same object with `m_hold = true` is a speed-holding dyno, used for the sweep.

---

## 5. What probe.exe does

| part | content |
|---|---|
| A | build → crank (starter on until rpm > 1.3 × starter speed for 0.25 s) → idle 3 s → **10 s each at 25 %, 60 %, 100 % pedal** against the fixed load (prints rpm, torque, power and MAP every second) → idle 2 s. Audio on; writes `probe_out.wav` |
| B | WOT dyno-hold sweep at 2000-6500 rpm (2 s settle + 1 s average each) |
| C | single-thread cost of 14 configurations, each a fresh engine: start, 2 s warm-up at 60 % on the fixed load, then 6 s timed, then a WOT dyno-hold torque check at 4000 rpm. Threads hard-pinned with `SetThreadAffinityMask` to the P-core or E-core set (from `GetSystemCpuSetInformation` `EfficiencyClass`) |
| D | N = 1, 2, 4, 8, 16 identical full-quality engines on N threads, with timed sections synchronised to overlap. D1 with audio, D2 physics only |
| E | 1 "local" full-quality engine with audio + N = 3, 7, 15, 23 "remote" lightweight engines (10 kHz/4 or 5 kHz/2, physics only), all concurrent |
| F | 14 s clips at the lightweight settings: `probe_out_10k4.wav`, `probe_out_5k2.wav` |

**Timing method:**
- CPU time is `QueryThreadCycleTime` divided by a TSC rate calibrated at start-up (3.055 GHz on this machine).
- Wall time is `steady_clock`.
- The first probe version used `GetThreadTimes`, which turned out to be tick-sampled at 15.6 ms and misattributed time between physics and audio. That log is kept as `run_quick_v1_getthreadtimes.txt` for reference only; ignore its numbers.
- "ms/sim-s" means milliseconds of CPU per second of simulated engine time.
- "RTx" is the real-time factor: values above 1 mean the engine runs faster than real time.
- Logs: `D:\Backroads-ref\probe\run_full.txt` (default build) and `run_fastmath_BC.txt`.

---

## 6. Results

### 6.1 Start, idle, throttle on a fixed load (Part A, 20 kHz / 8 substeps, audio on)

- Starter released after 2.02 s simulated at 733 rpm.
- Idle with no load and pedal 0: 838 → 760 → 795 rpm; manifold pressure (MAP) 38-40 kPa.

| pedal | steady rpm | torque | power | MAP |
|---|---|---|---|---|
| 25 % | 3317 | 88.0 Nm (64.9 lb-ft) | 30.6 kW (41.0 hp) | 66 kPa |
| 60 % | 5106 | 208.6 Nm (153.9 lb-ft) | 111.5 kW (149.6 hp) | 96.5 kPa |
| 100 % | 5216 | 217.7 Nm (160.5 lb-ft) | 118.9 kW (159.4 hp) | 99.7 kPa |

Steady state is reached within about 1 s of each pedal step, with ±20-30 rpm cycle ripple. The whole run is 37.3 s simulated in 746,478 solver steps.

### 6.2 WOT dyno sweep (Part B) vs. the real engine

| rpm | 2000 | 2500 | 3000 | 3500 | 4000 | 4500 | 5000 | 5500 | 6000 | 6500 |
|---|---|---|---|---|---|---|---|---|---|---|
| Nm | 169.8 | 184.6 | 212.1 | 215.3 | 225.0 | **229.4** | 225.9 | 211.2 | 196.0 | 186.5 |
| hp | 47.7 | 64.8 | 89.4 | 105.8 | 126.4 | 144.9 | 158.6 | 163.1 | 165.2 | **170.2** |

A real 2.5 L SOHC EJ25 makes about 225 Nm (166 lb-ft) at ~4000-4400 rpm and 165-173 hp at 5600-6000 rpm, depending on model year. So the hand-built engine matches the published figures closely. That also says the C++ port of the script is correct.

### 6.3 Single-thread CPU cost (Part C, steady 60 % pedal, 6 s timed)

| configuration (sim Hz / fluid substeps) | physics ms/sim-s | audio ms/sim-s | **total** | RTx | WOT@4000 Nm (fidelity) |
|---|---|---|---|---|---|
| 20k/8 + audio + IR, **P-core** | 335.6 | 98.8 | **434.4** | 2.26 | 224.2 |
| 20k/8 + audio + IR, **E-core** | 434.9 | 112.1 | **547.0** | 1.81 | 224.2 |
| 20k/8 + audio + IR, unpinned (57 % of frames on P) | 382.4 | 105.6 | 488.1 | 2.03 | 224.1 |
| 20k/8 physics only, P | 336.8 | 0 | 336.8 | 2.95 | 222.1 |
| 20k/8 physics only, E | 436.3 | 0 | 436.3 | 2.27 | 222.1 |
| 20k/8 audio with 1-tap IR (no reverb), P | 334.4 | **1.5** | 335.9 | 2.96 | 224.2 |
| 20k/4 physics only, P | 210.6 | 0 | 210.6 | 4.40 | 222.5 |
| 10k/8 physics only, P | 171.2 | 0 | 171.2 | 5.80 | 227.8 |
| **10k/4 physics only, P** | **108.5** | 0 | **108.5** | 9.16 | 235.0 (+5 %) |
| 10k/4 physics only, E | 138.9 | 0 | 138.9 | 7.14 | 235.0 |
| 10k/4 + audio + IR, P | 110.6 | 98.5 | 209.1 | 4.76 | 234.2 |
| 5k/4 physics only, P | 56.6 | 0 | 56.7 | 17.55 | 289.2 (+29 %) |
| **5k/2 physics only, P** | **40.3** | 0 | **40.3** | 24.70 | 289.9 (+29 %) |
| 5k/2 physics only, E | 50.8 | 0 | 50.8 | 19.52 | 289.9 |

What the table shows:

- **Physics cost** is roughly proportional to solver steps plus fluid substeps.
  - 20k/8 → 10k/4 is 3.1× cheaper.
  - 5k/2 is 8.4× cheaper.
  - E-cores are about 25-30 % slower than P-cores.
- **Audio cost is almost entirely the exhaust impulse-response convolution.**
  - `ConvolutionFilter::f()` is a direct-form FIR. For `minimal_muffling_02.wav` it has **5636 taps per output sample**: 248 M multiply-adds per second.
  - Replacing the impulse response (IR) with a 1-tap identity drops audio to 1.5 ms/s.
  - Audio cost is independent of simulation frequency.
- **Fidelity:**
  - Part-load steady state is nearly identical across all settings: 60 % on the fixed load gives 5106-5175 rpm and 208.6-214.2 Nm.
  - WOT torque drifts upward as the time step grows: about +5 % at 10 kHz, but **+29 % at 5 kHz**.
  - Keep the player's own engine at 20 kHz (or 20k/4) so its torque is accurate. Use 10k/4 for nearby remote cars and 5k/2 only where accuracy does not matter (distant sound or rpm).

### 6.4 N full-quality engines in parallel (Part D, 20k/8, unpinned threads)

| N | D1: physics + audio, per-engine CPU ms/sim-s | slowest RTx | cores used | D2: physics only, per-engine CPU | slowest RTx | cores used |
|---|---|---|---|---|---|---|
| 1 | 501.6 | 1.98 | 0.50 | 405.1 | 2.45 | 0.41 |
| 2 | 508.1 | 1.95 | 1.02 | 413.5 | 2.39 | 0.83 |
| 4 | 557.2 | 1.77 | 2.23 | 433.2 | 2.26 | 1.73 |
| 8 | 548.5 | 1.79 | 4.39 | 446.2 | 2.17 | 3.57 |
| 16 | 599.6 | 1.59 | 9.59 | 466.0 | 2.09 | 7.46 |

- Engines are fully independent: own `Simulator`, own `atg_scs` system, no locks. Scaling is close to linear, and even 16 engines at full quality each run 1.6-2.1× faster than real time.
- **Windows put many of these unpinned threads on E-cores** (only 22-50 % of sampled frames were on P-cores), which is why per-engine cost here sits between the P-core and E-core numbers in §6.3.
- That is probably because the console process is not the focused window. Windows 11 / Thread Director steer background work toward E-cores.
- A focused Unity player would likely get more P-core time. Budget with the E-core column to be safe.

### 6.5 The target scenario: 1 local + N remote (Part E, all concurrent, unpinned)

| remote setting | N remote | local engine (20k/8 + audio + IR) CPU ms/sim-s, RTx | remote mean CPU ms/sim-s, slowest RTx | **all engines, cores** |
|---|---|---|---|---|
| 10k/4 physics | 3 | 465.8, 2.12 | 117.9, 8.4 | 0.82 |
| 10k/4 physics | 7 | 460.4, 2.16 | 127.6, 7.6 | 1.35 |
| 10k/4 physics | 15 | 485.9, 2.05 | 151.2, 6.4 | 2.75 |
| 10k/4 physics | 23 | 487.0, 1.94 | 170.8, 4.8 | **4.41** |
| 5k/2 physics | 3 | 450.1, 2.19 | 45.3, 21.4 | 0.59 |
| 5k/2 physics | 7 | 466.6, 2.13 | 49.3, 19.4 | 0.81 |
| 5k/2 physics | 15 | 516.0, 1.92 | 58.8, 16.1 | 1.40 |
| 5k/2 physics | 23 | 551.2, 1.77 | 63.5, 12.6 | **2.01** |

### 6.6 /fp:fast /arch:AVX2 build (`build-fastmath`, Parts B and C)

Single thread, P-core pinned unless stated; log `run_fastmath_BC.txt`.

| configuration | default build total ms/sim-s | /fp:fast /arch:AVX2 total | change |
|---|---|---|---|
| 20k/8 physics only, P | 336.8 | 299.8 | −11 % |
| 20k/8 physics only, E | 436.3 | 365.7 | −16 % |
| 20k/8 + audio + IR, P | 434.4 (audio 98.8) | 393.7 (audio **98.5**) | audio unchanged |
| 10k/4 physics only, P | 108.5 | 99.0 | −9 % |
| 5k/2 physics only, P | 40.3 | 37.2 | −8 % |

- The WOT sweep is unchanged within ±3.5 Nm (e.g. 222.1 vs 225.0 Nm at 4000 rpm, 199.5 vs 196.0 Nm at 6000 rpm), and both builds give 170.2 hp at 6500 rpm.
- `/fp:fast` is a safe ~10 % physics win.
- MSVC does **not** vectorise `ConvolutionFilter::f()`, even with `/fp:fast /arch:AVX2`. That loop has wrapped two-part indexing over member pointers. The audio cost needs a code change (FFT / partitioned convolution or hand-written SIMD), not a compiler flag.

### 6.7 Audio output

| file | content |
|---|---|
| `D:\Backroads-ref\probe\probe_out.wav` | 37.3 s, 44.1 kHz, mono, 16-bit: crank → idle → 25 % → 60 % → 100 % → idle, at 20 kHz / 8 substeps with the `minimal_muffling_02` IR. Exact output of the Synthesizer, including its leveler (auto-gain) |
| `D:\Backroads-ref\probe\probe_out_10k4.wav` | 14 s: idle → 60 % → 100 % → idle at 10 kHz / 4 substeps |
| `D:\Backroads-ref\probe\probe_out_5k2.wav` | 14 s, same sequence at 5 kHz / 2 substeps |

An objective check, made with `python -I wavstats.py` (scratchpad): per-second RMS, peak, clipped-sample count, and the dominant 20-600 Hz period by autocorrelation. A 4-cylinder four-stroke fires at rpm/30 Hz.

| segment (probe_out.wav) | engine rpm | expected firing freq | measured dominant | RMS | clipped samples / s |
|---|---|---|---|---|---|
| idle | 760-840 | 25-28 Hz | 25-28 Hz | −17 to −20 dBFS | 0 |
| 25 % | 3317 | 110.6 Hz | 110.2 Hz | −4.0 dBFS | ~800 (1.8 %) |
| 60 % | 5106 | 170.2 Hz | 169.6 Hz | −5.0 dBFS | ~1400 (3.2 %) |
| 100 % | 5216 | 173.9 Hz | 175.0 Hz | −3.1 dBFS | ~190 (0.4 %) |

- **The pitch matches the firing frequency in every segment**, so the audio really is driven by the simulated exhaust pulses.
- **The synthesizer hard-clips under load.** It saturates `int16`: `levelerTarget = 30000`, `volume = 1.0`, then `std::lround` is clamped to `INT16_MAX`. In Unity, lower `Synthesizer::AudioParameters::volume` or `levelerTarget` (e.g. volume 0.5); this is a plain parameter change.
- **10k/4 clip:** pitch is still correct at idle (31 Hz), 60 % (169.6 Hz) and 100 % (175 Hz).
- **5k/2 clip:** correct at 100 % (175-178 Hz). At idle and cranking, though, the dominant frequency is 216-344 Hz instead of ~27 Hz, and the level is about 10 dB louder. **Low-rpm sound breaks down at 5 kHz.** At 60 % it locks onto half the firing frequency (86.8 Hz), which points to large cylinder-to-cylinder variation.
- Conclusion: 10k/4 is acceptable for audible remote cars; 5k/2 only for distant or barely audible ones.

I could not listen to these files. Someone should listen to judge quality, especially the lower-rate clips. The synthesizer upsamples the simulation-rate exhaust signal to 44.1 kHz behind a 1.9 kHz anti-alias filter, so at 5 kHz the input band (≤ 2.5 kHz) is already tight.

---

## 7. Engine-sim core issues that matter for a Unity port

All are in the untouched upstream code at `85f7c3b`; nothing upstream has changed since January 2023.

1. **Out-of-bounds write for engines with more than 8 cylinders.** `PistonEngineSimulator::writeToSynthesizer()` (`src/piston_engine_simulator.cpp:379`) declares `static double lastValveLift[8]` and writes `lastValveLift[i]` for every cylinder.
   - The shipped `10_lfa_v10.mr`, `11_merlin_v12.mr`, `12_ferrari_412_t2.mr` and `09_radial_9.mr` all write past the array, which is memory corruption.
   - The array is also a function-level **static shared by every engine instance and thread**. For ≤ 8 cylinders that is a harmless data race, because the value is never read.
   - Fix in a fork by deleting the two lines.
2. **Lazy static init race.** `Function::DefaultGaussianFilter` is created by the first `Function` constructor. Construct one `Function` on the main thread before building engines on workers; the probe does this.
3. **Debug builds don't compile:** see §2 item 5.
4. **Audio rate is hard-wired to 44.1 kHz.** `Simulator::initializeSynthesizer()` hard-codes `audioSampleRate = 44100`. Unity's default output rate is usually 48 kHz, so either resample (for example in `OnAudioFilterRead`) or change that value in a fork.
5. **100 ms default audio latency.** `Simulator::startFrame()` adapts the steps per frame to keep the synthesizer input buffer near `m_targetSynthesizerLatency = 0.1` s, adding 10 % more or fewer steps. That means about 100 ms between throttle and sound by default. `setTargetSynthesizerLatency()` can lower it, and needs testing at 30-50 ms. My probe drains audio immediately, so it always ran about 10 % "ahead". The numbers above are normalised by **actual** simulated time (sum of `getFrameIterationCount()`), so they are not affected.
6. **The convolution reverb is the expensive part of audio** (~99 ms/s for a 5636-tap IR) and compiler flags do not fix it (§6.6). For many audible engines, replace `ConvolutionFilter` in a fork with a partitioned FFT convolution, which should cut the cost by about 20-50×. Alternatively, mix remote engines' dry signals into one shared reverb bus.
7. **Not deterministic.** `CombustionChamber` uses `rand()` for burn-efficiency randomness and the synthesizer uses `rand()` for noise. Do not try lockstep networking. Replicate inputs and state (pedal, gear, clutch, rpm) and let each client re-simulate remote engines only for sound.
8. **Teardown leaks:** `PistonEngineSimulator::destroy()` never calls `Simulator::destroy()` / `Synthesizer::destroy()`, and `Function`, `Camshaft` and `Valvetrain` objects are never freed. This is harmless for a few engines per session. If cars spawn and despawn often, fix it in the fork or pool engines.
9. **Driving a Unity drivetrain without forking.** Use `Simulator::m_dyno` in hold mode as a speed-coupled clutch:
   - each step, set `m_rotationSpeed` from the Unity drivetrain (wheel ω × gear ratio × final drive)
   - set `m_maxTorque` to the clutch capacity
   - read back `Dynamometer::getTorque()` / `getFilteredDynoTorque()` as the torque to feed into Unity's wheels

   Part B shows the hold mode tracks the set speed exactly and reports torque. Dynamic coupling to a 50-100 Hz Unity physics loop is **untested**; it may need sub-stepping or a filtered torque to stay stable.
10. **Output clipping:** with the default `AudioParameters`, 0.4-3 % of samples clip under load (§6.7). Lower `volume` or `levelerTarget`.

---

## 8. Verification notes

- **Debug compile failure (§2 item 5):** verified. `cmake --build D:\Backroads-ref\probe\build --config Debug --target engine-sim` fails with `piston_engine_simulator.cpp(36,5): error C2065: 'm_antialiasingFilters': undeclared identifier`. Release builds are fine.
- `IgnitionModule::m_enabled` defaults to `false`. Forgetting to set it gives an engine that cranks but never fires.
- `Synthesizer::initialize()` pre-fills 1 s (44,100 samples) of silence into the output ring. Drain it at start, or the game hears 1 s of extra latency.

---

## 9. Verdict and recommended shape for Unity (60 fps)

**Feasible, with conditions.**

**Budget on this machine:**
- Local engine at full quality with audio: **≈ 0.43-0.55 cores**, i.e. 7-9 ms of CPU per 16.7 ms frame. It must run on its own worker thread, never Unity's main thread.
- Remote engines at 10 kHz/4 cost **0.11-0.17 cores each**; at 5 kHz/2, **0.04-0.06 cores each**.
- 1 local + 15 remote = 2.75 cores (10k/4) or 1.4 cores (5k/2), out of 24 logical processors.
- In the 23-remote case (24 engines), every engine still ran ≥ 1.77× real time.

**A typical free-to-play player PC** (assume a 6-core CPU with roughly half this machine's per-core speed; a guess, to be verified):
- local engine ≈ 0.9-1.1 cores
- remote 10k/4 ≈ 0.25-0.35 cores each

So plan for:
- a **quality tier**: local engine at 20k/8, 20k/4 or 10k/4
- a cap of about **4-8 fully simulated remote engines** (nearest or loudest)
- distant cars on a cheaper sound path: 5k/2 engine-sim, or sample/granular audio driven by replicated rpm and throttle

**Suggested integration:**
- **Native plugin.** Build `engine-sim.lib` plus a thin C API into one x64 DLL (`Assets/Plugins/x86_64/`). Example exports: `es_create(desc) / es_set_controls / es_step(dt) / es_read_audio / es_get_state`.
- **Threading.** One worker thread per engine, or a job pool. Feed audio to Unity through a lock-free ring buffer read in `OnAudioFilterRead` or a Native Audio Plugin, and resample 44.1 → 48 kHz.
- **Engines as data, not scripts.** Don't ship piranha. Describe engines in JSON with the fields used in `ej25.cpp` (about 40 numbers and 6 curves per engine), and build them with C++ code like `buildSubaruEj25()`. To convert the ~25 shipped `.mr` engines, either:
  - hand-port them (the EJ25 took about 300 lines), or
  - build piranha once on a dev machine (§3, needs win_flex_bison) and write a small exporter that runs `es_script::Compiler` and dumps the node parameters to JSON.
- **Fork engine-sim** (MIT) for the fixes in §7 items 1, 3, 4, 6 and 8.

**Risks still open:**
- Drivetrain coupling stability (§7 item 9).
- Audio quality at reduced rates: needs a listening test.
- Behaviour of the Windows scheduler with Unity in the foreground.
- No 48 kHz support without a code change.

---

## 10. Files

| path | what |
|---|---|
| `D:\Backroads-ref\probe\CMakeLists.txt` | headless build of `simple-2d-constraint-solver`, `csv-io`, `engine-sim`, `probe` (option `PROBE_FAST_MATH`) |
| `D:\Backroads-ref\probe\patched\synthesizer.cpp` | the only patched engine-sim source (delta include removed) |
| `D:\Backroads-ref\probe\src\ej25.cpp`, `ej25.h` | C++ construction of the shipped Subaru EJ25 engine, vehicle and transmission |
| `D:\Backroads-ref\probe\src\probe.cpp` | headless runner, load model, single-thread audio pump, benchmarks A-F |
| `D:\Backroads-ref\probe\src\wav.cpp`, `wav.h` | 16-bit WAV read (impulse responses) and write (output) |
| `D:\Backroads-ref\probe\build.ps1` | one-shot configure and build |
| `D:\Backroads-ref\probe\build\Release\engine-sim.lib`, `probe.exe` | build outputs (default flags) |
| `D:\Backroads-ref\probe\build-fastmath\Release\probe.exe` | `/fp:fast /arch:AVX2` variant |
| `D:\Backroads-ref\probe\run_full.txt` | full log, default build (all tables above). The "(DID NOT START)" tag on its Part A summary line is a print bug (the flag was not copied into that result). The engine did start: "started: yes", line 8. Fixed in `probe.cpp` afterwards |
| `D:\Backroads-ref\probe\run_fastmath_BC.txt` | fast-math log, Parts B and C |
| `D:\Backroads-ref\probe\run_quick_v1_getthreadtimes.txt` | first quick run; CPU split invalid (tick-sampled timer), kept for the record |
| `D:\Backroads-ref\probe\probe_out.wav`, `probe_out_10k4.wav`, `probe_out_5k2.wav` | synthesized audio |
