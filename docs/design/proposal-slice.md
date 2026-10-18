# Proposal: Vertical slice first

*Design proposal for the Unity version of Backroads. Date: 2026-10-18. Status: for the user's decision.*
*Evidence base: the seven research reports in `docs/unity/research/`, cited as (report §section), for example (01 §3.6) = `01-enginesim-core.md` section 3.6.*
*Nothing in this proposal has been built yet. Every number marked [tune] is a starting value to adjust after playing; every effort figure is an estimate.*

---

## 0. Summary

**The idea.** Put a playable Windows build in the user's hands after two weeks and a new one every two weeks after that. The first eight weeks build one thin but complete column through every hard system: canon Millbrook, one truck, engine-sim and proper intersections. Only then do we widen to traffic, offroad, online, the game loop and the new fleet.

**The vertical slice (VS-1, end of week 8):**
- Canon Millbrook (terrain, 24 roads, 59 lots and their buildings, 5,265 trees, 78 lamps), imported from a hash-verified export of the three.js build (03 §3).
- One drivable truck: the existing box truck, first on a straight port of the JS physics, then on a real driveline (03 §4.14, 06 §4).
- An engine-sim engine that makes the sound **and** produces the torque that moves the truck (01 §3.6).
- Proper intersections: filleted asphalt junctions, kerbs, sidewalks, corner ramps, centre lines, stop lines, crosswalks and STOP signs at Millbrook's 11 junctions, 5 L-bends and 6 dirt aprons (05 §1.2, §5).
- Chase camera, keyboard and gamepad, and a small HUD.

**What the user can play, and when:**

| Build | End of week | What you can do |
|---|---|---|
| M0 | 2 | Fly over canon Millbrook in Unity. Crank and rev a real engine-sim engine in an "Engine Bench" screen |
| M1 | 4 | Drive the truck anywhere on Millbrook, with engine-sim sound |
| M2 | 6 | Drive through proper intersections with markings and STOP signs |
| **M3 = VS-1** | **8** | engine-sim now drives the truck: it can stall, labour on hills and bounce off the limiter. Kerbs and sidewalks |
| M4 | 10 | "Pretty Millbrook": day and night, lit windows, street lamps, headlights, grass, water, a graphics menu |
| M5 | 13 | Traffic that stops at STOP signs, takes turns at the all-way stop and uses the roundabout. The truck gets its new modern body |
| M6 | 15 | Offroad: ruts, deep mud, low range, diff lock, winch, mud on the bodywork. The pickup |
| M7 | 17 | Online alpha: drive with friends, hear their engines, share the same ruts |
| M8 | 20 | The full Backroads loop (freight jobs, money, fuel, maps, save) and the rest of the JS fleet |
| M9+ | every 2 weeks | Semi with trailer, 6x6, rally coupe, GT race car, Steam, dedicated server, diesel and turbo work |

**Key choices:**

| Topic | Choice | Evidence |
|---|---|---|
| Repository | Same repo; new folders `unity\`, `native\`, `CanonData\`, `art\` next to the untouched JS game | §1.1 |
| Unity version | 6000.3 LTS. Start on the installed 6000.3.20f1, move to 6000.3.26f1 when the user updates it. Not 6000.5 (end of life) | 04 §1 |
| Render pipeline | URP 17.3, Forward+, GPU Resident Drawer | 04 §2, 07 §2 |
| Canon | Export the final booted state from JS and verify it by SHA-256. Never regenerate the world in C# | 03 §0, §3 |
| Terrain | `WorldGrid` NativeArrays are the physics truth; rendering is custom chunk meshes | 03 §4.1, §4.3; 06 §5.2 |
| Roads | Our own editor bake, `Backroads.Roads`, driven by the frozen polylines | 05 §0, §3 |
| Vehicle physics | Our own "BRV". It starts as a verbatim port of `vehicle.js` and grows a shaft solver | 03 §4.14; 06 §0 |
| engine-sim | Native DLL built from an unmodified upstream copy, ABI `esp_*` v1. Sound-only FOLLOW_RPM mode first (M1), then SLAVED_CRANK coupling (M3) | 01 §3.6, §7; 06 §4.4 |
| Engine audio | Render on the engine worker, hand over through a lock-free ring, play through `OnAudioFilterRead`; `IAudioGenerator` for 3D sound once a spike proves it | 01 §4 |
| Netcode | Netcode for GameObjects 2.13.3, owner-authoritative vehicles. Unity Relay for the alpha, Steam Datagram Relay at launch | 04 §3 |
| Art | Blender to FBX plus a JSON sidecar. Trucks first, race cars last | 07 §3, §8.1 |

**What slice-first trades away.** Report 03 orders the port by system (canon, then driving, then fleet, then roads, then game loop) and puts engine-sim, new vehicles and online last (03 §6). This proposal pulls engine-sim forward to M0/M1 and intersections to M2, and pushes jobs, the HUD map, the dozer and on-foot play to M8. For eight weeks the build is "a truck in a town", not "the Backroads game". In return, the riskiest pieces (the native plugin, Unity audio, the coupling, the junction mesher) are proven in the first two months, while the plan can still change cheaply.

---

## 1. Repository and project layout

### 1.1 One repository, new top-level folders

**Recommendation: keep everything in the existing private repo `kaiwren986/Backroads` at `D:\Backroads`.**
- The JS exporter and the C# importer change together. One commit can bump the CanonData format and its importer.
- One script (`tools\ci\ci-all.ps1`) can re-export from the oracle, check its hashes and test the Unity build.
- The user has one folder to back up and one repo to grant access to.
- The JS game stays exactly where it is and `Play.bat` keeps working.

The alternative, a new repo `kaiwren986/Backroads-Unity` at `D:\BackroadsUnity`, works too; only paths change. This is decision **U1** (§7.2).

```
D:\Backroads\
  index.html  server.mjs  Play.bat  style.css      the three.js game = the canon ORACLE (frozen, tag oracle-v1)
  js\                                              frozen, except the hash-neutral export additions in §1.7
  assets\                                          JS assets; towns\canon.json and buildings\buildings.glb are frozen
  tools\
    blender\                                       existing generators (vehicles.py, trees.py, buildings.py), unchanged
    blender\pipeline\                              NEW br_common/br_generate/br_uv/br_bake/br_lod/br_validate/br_export.py (07 §5.1)
    export\                                        NEW export-canon.mjs, golden-traces.mjs, package.json (Playwright, if approved)
    ci\                                            NEW PowerShell CI scripts (§1.8)
    build-vehicle.bat                              NEW (07 §5.1)
  CanonData\
    v1\                                            NEW export of the booted JS world (03 §3.3), Git LFS for *.f32 *.u8 *.png *.glb
    overlays\v1.1\                                 NEW road plates + roads-overrides.json (05 §3, §5.10)
  native\                                          NEW engine-sim plugin (§1.5)
  unity\                                           NEW Unity project root (Assets, Packages, ProjectSettings)
  art\
    vehicles\<id>\<id>.blend, brief.md             NEW hand-tunable vehicle sources (LFS) (07 §4.1)
    trims\TS_Vehicle_A.blend                       NEW trim sheet source
  builds\                                          gitignored: milestone player builds (builds\M1\Backroads.exe)
  artifacts\                                       gitignored: CI logs, screenshots, WAVs, reports
  docs\unity\research\ , design\ , milestones\     reports, proposals, per-milestone reports
```

Folders outside the repo, never committed: `D:\Backroads-ref\` (engine-sim reference checkout and probes), `D:\Backroads-ref\vehicle-refs\<id>\` (reference photos, 07 §8.3), `D:\SyntyPacks\` (user-downloaded Synty packs, 07 §10.1).

### 1.2 Unity project

- **Path:** `D:\Backroads\unity\` (short, to stay well under Windows path limits).
- **Editor:** `C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe` now; 6000.3.26f1 once the user installs it from Unity Hub (04 §1.2). The CI scripts read the editor path from one variable.
- **Project settings, set on day one:**
  - Colour space Linear. URP assets per quality tier, Universal Renderer on **Forward+**. SRP Batcher on. GPU Resident Drawer = Instanced Drawing, BatchRendererGroup Variants = Keep All (04 §2.2).
  - Enter Play Mode: "Do not reload Domain or Scene"; static state reset with `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (04 §1.2 rule 1).
  - `Time.fixedDeltaTime = 1/120`, Maximum Allowed Timestep 0.05 (06 §3.4). Physics solver TGS.
  - Active Input Handling = Input System only.
  - Audio: sample rate 48,000, DSP buffer "Best latency" (01 §4.4).
  - Player: Windows x64 only (04 §2.3). Scripting backend Mono until M3, IL2CPP from M4 (VS 2022 is installed). Burst on.
  - Read/Write on every mesh used by a MeshCollider (04 §1.2 rule 3). No dynamic batching (rule 6). Never embed Burst into `Packages\` (rule 4). No `com.unity.serialization` (rule 5).
  - Asset Serialization: Force Text.
- **Physics layers:** `Static`, `Road`, `Vehicle`, `VehicleHull`, `Wheel`, `Prop`, `Trigger`, `TerrainProxy`. The matrix follows 06 §5.2: hulls and wheels never collide with terrain colliders, because BRV handles ground contact against `WorldGrid` itself.

**Packages (pinned in `Packages\manifest.json`; versions from 04 §5):**

| Package | Version | Used from | Purpose |
|---|---|---|---|
| `com.unity.render-pipelines.universal`, `com.unity.shadergraph`, `com.unity.visualeffectgraph` | 17.3.0 (core) | M0 | URP, Shader Graph, VFX for dust and mud |
| `com.unity.burst` | 1.8.31 | M0 | WorldGrid sampling, meshing, BRV |
| `com.unity.collections` / `com.unity.mathematics` | 2.7.1 / 1.3.3 | M0 | NativeArrays, `float3` |
| `com.unity.cloud.gltfast` | 6.20.0 (not 7.0.0-exp.1) | M0 | Import the existing GLBs until the FBX pipeline replaces them |
| `com.unity.inputsystem` | 1.20.1 | M1 | Driving, OnFoot and UI action maps; rumble |
| `com.unity.test-framework` | the version bundled with the editor; record it in `packages-lock.json` at M0 | M0 | EditMode and PlayMode tests |
| `com.unity.formats.fbx` | 5.1.6 | M2 | FBX round-trips (optional) |
| `com.unity.splines` | 2.9.1 | M2 | Gizmos and nearest-point helpers only (05 §0) |
| `com.unity.memoryprofiler` | 1.1.12 | M4 | Memory checks |
| `com.unity.netcode.gameobjects` | 2.13.3 | M7 | Netcode |
| `com.unity.transport` | 2.7.4 | M7 | UDP, Relay |
| `com.unity.services.multiplayer` | 2.4.1 | M7 | Sessions with Relay for non-Steam builds |
| `com.unity.multiplayer.playmode` / `com.unity.multiplayer.tools` | 2.0.2 / 2.2.12 | M7 | Virtual players, network simulator |
| `com.unity.dedicated-server` | 2.0.2 | M10 | Headless server build profile |
| Facepunch.Steamworks + NGO Facepunch transport | 2.5.2 + 2.0.0 (git URL) | M9 | Steam lobbies and SDR |
| `com.unity.platformtoolkit` + `.steam` | 1.2.0 | M9 | Achievements, Steam Cloud saves |
| Clipper2 C# source (BSL-1.0) | latest release tag, pinned at M2 | M2 | Kerb loops, sidewalks, swept paths (05 §0) |
| LibTessDotNet (SGI-B 2.0) | 1.1.15 | M2 | Triangulation fallback (05 §0) |

Not used: Cinemachine (the camera is a straight port, 03 §4.19), `com.unity.vehicles` and `WheelCollider` (06 §3.1), Entities, Netcode for Entities (04 §3.4), Unity Terrain for rendering (§2.4).

**Folders inside the project:**

```
unity\Assets\
  Backroads\
    Code\<one folder per assembly, §1.3>
    Data\            ScriptableObjects: VehicleDefinitions, EngineCatalog, QualityPresets, SurfaceTable, Features
    Baked\Roads\     committed bake outputs (*.roadbake.bytes in LFS, small YAML SOs)
    Scenes\          Boot.unity, Millbrook.unity, EngineBench.unity, LightingTest.unity, Tests\*.unity
    Audio\           EngineLoops\<engine>\*.ogg (offline engine-sim renders), Sfx\
    UI\              UXML, USS, fonts (Barlow Condensed and Inter, SIL OFL; 03 §4.23)
    Settings\        URP assets, Volume profiles, Backroads.inputactions
  Art\               Vehicles\<Id>\, Wheels\, Materials\Vehicle\, Shaders\, Buildings\, Trees\, SyntyOverrides\ (07 §4.1)
  Plugins\x86_64\    EngineSimPlugin.dll (+ .pdb in development builds)
  StreamingAssets\
    EngineSim\       es\** library and engines\<id>\*.mr + IR *.wav (committed; read from disk at runtime, 01 §7.4)
    Canon\v1\        GENERATED from CanonData\v1 by the importer; gitignored
  ThirdParty\        Clipper2\, LibTessDotNet\ (committed with licences); Synty\ (gitignored, 03 §7)
```

### 1.3 Assembly definitions

| Assembly | References | Owns |
|---|---|---|
| `Backroads.Core` | Mathematics, Collections, Burst | `BrCoords` (the JS-to-Unity rule, 03 §2), units, fixed-step clock, `IVehicleEngine`, `VehicleState` and `RutStamp` structs, feature flags, logging |
| `Backroads.World` | Core | `CanonRuntimeLoader`, `WorldGrid` and its Burst queries, `SurfaceTable`, `ColliderGrid`, deformation kernels, dirty-rect queue |
| `Backroads.World.Rendering` | Core, World, URP | Terrain chunks, props, buildings, vegetation, grass, water, lamps, sky and day/night |
| `Backroads.Roads` | Core, World | Runtime road chunks, `LaneGraph`, `ControlSet`, `SignalController` |
| `Backroads.EngineSim` | Core | P/Invoke `Native`, `EngineSimEngine`, `EngineSimAudio`, `EngineLoopPlayer`, `EngineAudioBudget`, `TorqueMapEngine` |
| `Backroads.Vehicles` | Core, World | BRV: `VehicleDefinition`, `VehicleSimSystem`, `VehicleStepJob`, tyres, shaft solver, transmission controller, winch, `LegacyJsEngine` |
| `Backroads.Traffic` | Core, World, Roads, Vehicles | `TrafficSystem`, `TrafficDriver`, `JunctionArbiter`, ghosts |
| `Backroads.Gameplay` | Core, World, Vehicles | Jobs, economy, fuel, tow, save; all money changes go through `IEconomyAuthority` |
| `Backroads.CameraRig` | Core, World | Chase, hood, free-fly and on-foot cameras |
| `Backroads.UI` | Core, Vehicles, Gameplay | UI Toolkit HUD, minimap, big map, debug overlay, menus |
| `Backroads.Net` | Core, World, Vehicles, Traffic, Gameplay, NGO | Session service, vehicle sync, remote proxies, stamp log, host validation |
| `Backroads.App` | everything | `GameBootstrap`, `GameLoop`, quality presets, `AutotestRunner` (command-line test driver) |
| `Backroads.Editor` (Editor only) | everything | `CanonImporter`, build scripts, `BackroadsModelPostprocessor`, `VehiclePrefabBuilder` (07 §3.3) |
| `Backroads.Roads.Editor` (Editor only) | Roads, World, Clipper2, LibTessDotNet | The road and intersection bake (05 §3) |
| `Backroads.Tests.EditMode`, `Backroads.Tests.PlayMode` | as needed | Tests, grouped by `[Category]`: Canon, Roads, Vehicle, EngineSim, Traffic, Net, Perf, Slow |

Rules: `Backroads.EngineSim` never references Vehicles. Vehicles see an engine only through `IVehicleEngine` in Core, so the JS torque curve, engine-sim and baked torque maps are interchangeable. Gameplay never references Net; Net adapts Gameplay's request API. These seams are what let three or four agents work in parallel without colliding.

### 1.4 Data export tool and the oracle (summary; details in §1.7)

`js\exportUnity.js` runs inside the booted JS game and writes `CanonData\v1\` (03 §3.4). `tools\export\export-canon.mjs` drives it headlessly through the installed Microsoft Edge with Playwright. `CanonImporter` in Unity verifies every hash and generates runtime data.

### 1.5 Native plugin source

```
native\
  CMakeLists.txt  CMakePresets.json            our own CMake; lists sources from the untouched vendored tree (01 §1.4, App. A; 02 §1)
  third_party\
    engine-sim\                                byte-identical copy of upstream 85f7c3b (41 core .cpp, scripting\, es\)
    simple-2d-constraint-solver\               e009f4f (29 .cpp)
    piranha\                                   432f0b1, plus its MIT licence text (01 §5.4)
    VENDOR.md                                  commits, licences; tools\vendor-check.ps1 diffs the copy against D:\Backroads-ref\engine-sim
    doctest\                                   single-header test framework (MIT)
  shim\delta-studio\...                        two empty headers that satisfy the one unused include (01 §1.3)
  generated\piranha\                           scanner.auto.cpp, parser.auto.cpp/.h, generated once (01 §5.4; decision U4)
  src\
    esp_api.h/.cpp                             C ABI v1 (01 §7.2), exception firewall, status codes
    unity_sim.h/.cpp                           UnitySim : PistonEngineSimulator, UnityTransmission, writeToSynthesizer override (01 §7.3, A.3)
    coupling.cpp                               SlavedCrankPD, WheelLoad force generators (01 A.4)
    worker.cpp                                 one worker thread per engine, step_begin/step_end
    audio_ring.h                               SPSC float ring (01 §4.4)
    synth_render.cpp  fir_avx2.cpp             renderSync + AVX2 FIR with CPUID dispatch (01 §4.3, A.5)
    ir_wav.cpp  resample.cpp                   own WAV reader, 44.1 to 48 kHz IR resampling
    script_loader.cpp  piranha_path_std.cpp    piranha wrapper with absolute paths and a compile mutex; Boost-free Path (01 §5.4, A.6)
    builders\ej25.cpp  builders\chev454.cpp    C++ engine builders (fallback when piranha is unavailable; 02 §4)
  tools\esp_cli\                               headless runner: dyno sweep, WAV render, follow and coupling scenarios, selftest
  tests\                                       ctest suite (§6, M0)
```

Build settings (01 §7.1): `/MT` static CRT, `NDEBUG` always (upstream Debug builds do not compile, 02 §8), `/O2 /fp:fast`, `/arch:AVX2` only in `fir_avx2.cpp` with an SSE2 fallback, C++17. A second preset builds with `/fsanitize=address` for soak tests. The output DLL is copied to `unity\Assets\Plugins\x86_64\`. Unity locks a loaded native DLL until the editor restarts (01 §7.4), so the copy script refuses to run while an editor holds the file and says so; batchmode runs are fresh processes and are unaffected.

### 1.6 Blender pipeline

- Sources: `art\vehicles\<id>\<id>.blend` (after the user's optional hand tuning) and `brief.md` (fictional brand, inspirations, deliberate deviations, dimensions) (07 §4.1, §8.2).
- Scripts: `tools\blender\pipeline\br_*.py` wrap the existing `vehicles.py` builders: save a `.blend`, add sockets, consolidate to ≤ 9 materials, build LOD0 to LOD3 from subdivision levels, UVs, Cycles bakes, validation and export (07 §5.1, §8.5).
- Command: `tools\build-vehicle.bat <id>` runs `"%LOCALAPPDATA%\Programs\Blender Foundation\Blender 4.5\blender.exe" -b art\vehicles\<id>\<id>.blend --factory-startup --python tools\blender\pipeline\br_build.py -- --id <id> --unity D:\Backroads\unity` (07 §5.1).
- Outputs: `unity\Assets\Art\Vehicles\<Id>\VH_<Class>_<Variant>.fbx`, `<id>.vehicle.json` (wheel sockets in Unity space, tyre sizes, bounds, tri counts), `T_*.png`, `build_report.json`, a 6-view review sheet and an overlay render against the reference (07 §5.2). GLB is still written for the JS game.
- Unity side: `BackroadsModelPostprocessor` (material remap by name, `UCX_` convex hulls, custom properties to `BrNodeProps`) and `VehiclePrefabBuilder` (generated base prefab plus a user-owned Prefab Variant) (07 §3.3). A ScriptedImporter turns `<id>.vehicle.json` into a `VehicleDefinition`, so mesh sockets and physics can never drift apart (07 §4.4).
- Bakes run on the RTX 5080 with Cycles and OptiX, headless, about 10 to 60 s per 2048 map (07 §5.1).

### 1.7 How the three.js build stays the canon oracle

1. **Freeze.** Tag the JS game as `oracle-v1`. After that, the only permitted JS changes are the export additions below, each followed by a re-check of every hash and count in 03 §3.2.
2. **Hash-neutral export additions** (03 §3.4; decision U2):
   - New file `js\exportUnity.js`, run with `index.html?export=unity` right after boot. It refuses to run unless `sha256(W.WEAR) = 85473f6f…`, i.e. nobody has rutted the ground yet.
   - Three one-line additions to `props.js`: `export const treeList = trees`, `props.rocks = spots`, and `rec()` always appending to `props.allOps`. None of them consumes random numbers.
   - The exporter also writes data the Unity port needs for parity, all read-only: `gameplay\vehicles.json` (DEFS, META, the derived ratio tables of 03 §4.14), `gameplay\jobs.json` (CARGO, CARRIES, SUPPLY, DEMAND, TANK), `terrain\probes.json` (1,000 seeded `[x, z, heightAt, normalAt, surfaceAt]`), `reference\cameras.json` and `reference\shots\*.png` (12 fixed cameras at 10:00 and 21:00, 03 §3.6).
3. **Automated export.** `node tools\export\export-canon.mjs --out artifacts\oracle --verify CanonData\v1\manifest.json` starts `server.mjs`, opens the game in the installed Edge through Playwright (`channel: 'msedge'`, `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`, project-local dev dependency), runs the exporter and compares every SHA-256 with the committed manifest (03 §3.4). If the user does not approve Playwright (U3), the fallback is a one-click "Export canon" in the JS game using the File System Access API, plus a verify script.
4. **Golden traces.** `tools\export\golden-traces.mjs` feeds scripted inputs to `G.update(1/120)` and records pose, velocity, rpm, gear and mud sink every 0.1 s (03 §3.6). The input scripts are JSON files that the Unity `AutotestRunner` replays unchanged.
5. **The oracle stays playable.** `Play.bat` still runs the browser game, so the user can always compare the two side by side.
6. **Never change canon by editing JS generation.** World changes are versioned overlays on the exported layers, made in the Unity editor (`CanonData\overlays\v1.1` for road plates, later `v2`) (03 §4.2).

**What "identical" means.** Content and layout identical: the same heightfield (hash), the same road polylines, the same lots, buildings, colours, lit-window flags, trees, rocks, lamps and colliders (hashes and counts). Pixels are not identical, because the renderer is new. Intersections *add* geometry (fillets, kerbs, sidewalks, markings, signs, a sparse height overlay) and must not move any canon object; a test enforces this (§6, M2). The one change that would alter the look of the square, the harbor-road realignment for a 4-leg roundabout, is the user's decision (05 §7.1; U8).

### 1.8 CI and batchmode scripts

There is no cloud CI: the native build needs Windows and MSVC, screenshots need the GPU, and agents must not push. CI is a set of PowerShell scripts that agents (and the user) run locally. Each writes to `artifacts\ci\<yyyyMMdd-HHmm>\` and ends with a pass/fail table in `report.md`.

| Script | What it runs |
|---|---|
| `tools\ci\ci-oracle.ps1` | `node tools\export\export-canon.mjs --verify ...` |
| `tools\ci\ci-native.ps1` | `cmake --preset vs2022-x64-release -S native`, `cmake --build --preset release --parallel 16`, `ctest --preset release --output-on-failure`, copy the DLL |
| `tools\ci\ci-unity-tests.ps1` | EditMode then PlayMode tests (commands below) |
| `tools\ci\ci-build.ps1 -Milestone M1` | Windows player build to `builds\M1\` plus `PLAYTEST.md` |
| `tools\ci\ci-autotest.ps1 -Milestone M1` | Runs the built player with each autotest scenario of that milestone |
| `tools\ci\ci-art.ps1 -Id truck` | `tools\build-vehicle.bat truck`, then the Unity import tests |
| `tools\ci\ci-all.ps1` | All of the above in order; the milestone gate |

Core commands:

```powershell
$UNITY = "C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe"   # one place to change
$PROJ  = "D:\Backroads\unity"; $OUT = "D:\Backroads\artifacts\ci\$(Get-Date -Format yyyyMMdd-HHmm)"

# EditMode tests (no GPU needed). -runTests exits by itself; do not add -quit.
& $UNITY -batchmode -nographics -projectPath $PROJ -runTests -testPlatform EditMode `
         -testResults "$OUT\editmode.xml" -logFile "$OUT\editmode.log"

# PlayMode tests (physics, engine-sim, traffic, netcode with Multiplayer Play Mode)
& $UNITY -batchmode -projectPath $PROJ -runTests -testPlatform PlayMode -testCategory "!Slow" `
         -testResults "$OUT\playmode.xml" -logFile "$OUT\playmode.log"

# Player build
& $UNITY -batchmode -nographics -quit -projectPath $PROJ `
         -executeMethod Backroads.Editor.Build.BuildPlayer.Windows -milestone M1 -logFile "$OUT\build.log"

# Player autotest on the real build (GPU, audio device); exit code 0 = pass
D:\Backroads\builds\M1\Backroads.exe -screen-fullscreen 0 -screen-width 2560 -screen-height 1440 `
    -br-autotest drive-route9 -br-out "$OUT\autotest" -logFile "$OUT\player.log"
```

**`AutotestRunner`** (in `Backroads.App`) parses `-br-autotest <scenario>`. A scenario is JSON: spawn point, vehicle, time of day, an input script (the same format as the JS golden traces) and checks. It writes `trace.csv`, `frametimes.csv`, `audio.wav` (captured with Unity's `AudioRenderer`), `audio_report.json` (dominant pitch by autocorrelation, RMS, clipping, ring underruns, computed in C#, so no Python is needed), screenshots, a contact sheet composed in-player next to the JS reference shots, and `result.json`. Then it quits with exit code 0 or 1.

Every build shows a small version stamp (milestone, git hash, canon manifest hash) so the user can say which build a problem came from.

### 1.9 Git rules

- `.gitattributes` LFS: `CanonData/**/*.f32`, `CanonData/**/*.u8`, `*.blend`, `*.fbx`, `*.glb`, `*.png`, `*.wav`, `*.ogg`, `*.exr`, `*.roadbake.bytes`. CanonData v1 is about 30 MB compressed (03 §3.3). GitHub Free gives 10 GiB of LFS storage and 10 GiB of bandwidth a month (07 §10.5), which is plenty if builds and artifacts stay out.
- `.gitignore` additions: `unity/Library/`, `unity/Temp/`, `unity/Logs/`, `unity/obj/`, `unity/UserSettings/`, `unity/Assets/StreamingAssets/Canon/`, `unity/Assets/ThirdParty/Synty/`, `native/build*/`, `native/tools/win_flex_bison/`, `builds/`, `artifacts/`.
- Synty: commit `synty-manifest.json` (pack, version, storefront, local zip path) and an editor check that warns when a listed pack is missing (07 §10.5).
- Workflow: agents work on local branches (`m2/roads-bake`) and merge into `main` only after `ci-all.ps1` passes. `main` is always playable. Unfinished features ship hidden behind `Features` flags. Pushing to GitHub happens only when the user says so.

---

## 2. Architecture

### 2.1 Threads

| Thread | Runs |
|---|---|
| Main (Unity) | Bootstrap, `FixedUpdate` at 120 Hz (BRV scheduling, traffic, gameplay), `Update` (camera, interpolation, rendering updates, HUD, net send and receive) |
| Unity job workers | Burst jobs: terrain meshing, WorldGrid queries, `VehicleStepJob`, grass culling, deformation, road meshes at load |
| One engine worker per simulated engine (native) | engine-sim steps at 5 to 40 kHz, synchronous synthesizer render, push into the audio ring (01 §4.2) |
| Unity audio thread | `OnAudioFilterRead` / `IAudioGenerator`: only `esp_read_audio`, which is lock-free and allocation-free (01 §7.1) |

### 2.2 Data flow

```
EDITOR TIME
  JS oracle (tag oracle-v1) --js/exportUnity.js--> CanonData\v1 (SHA-256 manifest)
        --CanonImporter (verify) --> StreamingAssets\Canon\v1\*.bin + ScriptableObjects (WorldInfo, Placements, Vegetation, SurfaceTable)
  CanonData\v1\world.json + traffic.json + roads-overrides.json
        --RoadBake (Backroads.Roads.Editor, 12 stages, 05 §3)--> Baked\Roads\*.roadbake.bytes, LaneGraph.asset, ControlSet.asset
                                                              + CanonData\overlays\v1.1\road_plates.bin (sparse height and surface delta)
  art\vehicles\<id>.blend --br_build--> VH_*.fbx + <id>.vehicle.json --postprocessor/prefab builder--> prefab + VehicleDefinition
  native\third_party\engine-sim --CMake--> EngineSimPlugin.dll --> Assets\Plugins\x86_64
  EngineSimPlugin (offline) --esp_dyno_sweep / esp_render_offline--> torque maps + engine loop .ogg for AI (06 §4.7)

RUNTIME
  GameBootstrap: WorldGrid.Load(heights, base, surface, ..., + road plates) -> ColliderGrid
               -> TerrainChunkSystem, PropSpawner, VegetationRenderer, RoadChunkLoader (+ MeshColliders cooked in jobs)
  FixedUpdate (1/120 s):
     InputSampler -> VehicleSimSystem
         collect engine results (non-blocking) -> WorldGrid + spherecast queries (Burst) -> VehicleStepJob (4 substeps)
         -> impulses -> kick engine workers with crank-speed samples -> RutStamps -> DeformQueue
     TrafficSystem (LaneGraph, ControlSet, JunctionArbiter) -> drives BRV LOD1 vehicles
     Gameplay (JobSystem via IEconomyAuthority)
  Update: DeformQueue -> WorldGrid -> dirty rects -> terrain / grass / tree re-snap; camera; VFX; HUD; Net
  Audio thread: EngineSimAudio.OnAudioFilterRead -> esp_read_audio(ring)
```

### 2.3 Subsystems and boundaries

| Subsystem | Assembly | Key types | Inputs → outputs | First playable |
|---|---|---|---|---|
| **Data import** | Editor, World | `CanonManifest`, `CanonImporter`, `CanonRuntimeLoader`, `WorldInfo`, `PlacementSet`, `VegetationSet`, `LegacyTrafficNetwork` (golden reference only) | `CanonData\v1` → verified binaries + SOs. Aborts on any hash mismatch (03 §3.5) | M0 |
| **WorldGrid** | World | `WorldGrid` (NativeArrays `H`, `BASE`, `SURFACE`, `WEAR`, `ROADD`, `MUD`, `TINT`; from M6 `WET`, `COMPACT`, `DELTA_MM`), `WorldGridQueries.HeightAt/NormalAt/SurfaceAt/MudAt/RayTerrain`, `ColliderGrid`, `DirtyRectQueue` | JS array layout inside, Unity coordinates at the API: `HeightAt(p) = HeightAtJs(-p.x, p.z)` (03 §2, §4.1). The physics truth for wheels and hulls (06 §5.2) | M0 |
| **Terrain rendering** | World.Rendering | `TerrainChunkSystem`, `TerrainChunkBuildJob`, `TerrainLodPolicy`, `SG_Terrain` | 16×16 chunks of 64 m, LOD steps 1/2/4/8 at 110/250/470 m × lodScale, skirts, partial updates with `SetVertexBufferData` (03 §4.3) | M0 (simple colours), M4 (full shader) |
| **Props, buildings, vegetation** | World.Rendering | `PropSpawner`, `BuildingInstance`, `LampLights`, `VegetationRenderer`, `GrassSystem`, `WaterSurface` | `placements.json`, `vegetation.json` → GameObjects under the GPU Resident Drawer; per-building colours as material instances, not MaterialPropertyBlocks (04 §2.2, 07 §6.1) | M0 (static_props.glb), M4 (prefabs, lit windows, grass) |
| **Roads and intersections bake** | Roads.Editor | `RoadGraphImporter`, `GraphCleanup`, `CornerSolver`, `JunctionSurfaceBuilder`, `HeightModel`, `KerbBuilder`, `MarkingBuilder`, `RoadPlateStamper`, `LaneGraphBuilder`, `ControlAssigner`, `RoadBakeValidator` | Frozen polylines + overrides → meshes, colliders, plates, lanes, controls, sign placements, all from one geometric model (05 §3, §4, §5). Never runs at runtime (05 §3) | M2 |
| **Roads runtime** | Roads | `RoadChunk`, `LaneGraph` (`Lane`, `Connector`, `Conflict`), `ControlSet`, `SignalController` | Loads the bake, cooks colliders in jobs, exposes lanes and controls to traffic | M2 |
| **BRV vehicle physics** | Vehicles | `VehicleDefinition`, `VehicleSetup`, `BrVehicle`, `VehicleSimSystem`, `VehicleStepJob`, `Row`, `ShaftGraph`, `TyreModel`, `TransmissionController`, `Winch` | 1/120 s fixed step, 4 internal substeps (06 §3.4). M1: verbatim JS model (03 §4.14). M3: wheel and shaft DOFs in the PGS solver, MF secant tyres (06 §3.6, §4.1). M6: PhysX chassis for 3D collisions, mud model (06 §5.3) | M1 |
| **Engines** | Core (`IVehicleEngine`), Vehicles, EngineSim | `LegacyJsEngine` (JS torque curve), `EngineSimEngine`, `TorqueMapEngine`, `ReplicatedEngine` | `IVehicleEngine.Collect()` → indicated torque, rpm; `Kick(crank samples, pedal, ignition)` | M1 |
| **engine-sim plugin and audio** | EngineSim + native | `Native`, `EngineSimEngine`, `EngineSimAudio`, `EngineLoopPlayer`, `EngineAudioBudget`, `EngineCatalog` | §3 | M0 (bench), M1 (truck) |
| **AI traffic** | Traffic | `TrafficSystem`, `TrafficDriver`, `JunctionArbiter`, `GhostCar` | Port of `traffic.js` (03 §4.21) driving BRV LOD1 cars with `TorqueMapEngine`; uses the baked `LaneGraph` and the control rules of 05 §5.14 | M5 (hidden from M4) |
| **Jobs and economy** | Gameplay | `JobBoard`, `JobSystem`, `Wallet`, `FuelStation`, `TowService`, `IEconomyAuthority` (`LocalAuthority`, `HostAuthority`) | Tables from `gameplay\jobs.json`, rules ported from `jobs.js` (03 §4.22). Every change is a request, so single player and online share one code path (04 §3.2) | M8 |
| **UI** | UI | `HudController`, `MinimapView`, `BigMapView`, `DebugOverlay` (F3: rpm, torque, MAP, AFR, worker ms, ring fill, underruns), `GraphicsMenu` | UI Toolkit (03 §4.23) | M1 (minimal), M8 (full) |
| **Camera** | CameraRig | `ChaseCameraRig`, `HoodCamera`, `FreeFlyCamera`, `FootCamera` | Straight port of `camera.js` with the terrain pull-in ray (03 §4.19) | M0 (free-fly), M1 (chase) |
| **Netcode** | Net | `BrNetworkManager`, `SessionService` (Relay or Steam), `VehicleNetSync`, `RemoteVehicleProxy`, `ContactImpulseMessage`, `StampLog`, `HostValidator` | Owner-authoritative vehicles, custom 40-byte state at 20 Hz, host-simulated AI, integer rut-stamp log (04 §3.2, 06 §4.6, §5.6) | M7 |
| **Save and progression** | Gameplay | `SaveService`, `SaveDataV1` | JSON in `Application.persistentDataPath\save.json`, same fields as the JS save plus `version` (03 §4.25); Steam Cloud from M9 | M8 |
| **App** | App | `GameBootstrap`, `GameLoop`, `QualityPreset`, `AutotestRunner` | System order follows the JS loop (03 §4.27) | M0 |

### 2.4 Notes on the main choices

- **Terrain: custom mesh, not Unity Terrain.** The per-vertex two-surface look, the parity tests and the road plates all live on the JS grid layout (03 §4.3, 05 §5.10). Vehicles never touch a terrain collider (06 §5.2), so the rendering choice cannot affect feel. An invisible `TerrainCollider` for loose props comes later only if needed.
- **Buildings in two steps.** M0 shows `reference\static_props.glb` (the exact JS geometry, split into 64 m chunks), giving instant visual parity (03 §4.7). M4 replaces it with prefabs re-exported from `buildings.py`, with one glass mesh per building and per-pane IDs in vertex colour (07 §6.8). Canon lit flags are mapped by pane name and centroid, never by index, because re-running `buildings.py` renumbers panes (03 §3.3).
- **Vehicles in two steps.** M1 is a verbatim port of `vehicle.js` with its own integrator, so it feels like the browser game and can be checked against golden traces (03 §4.14 phase 1; 06 V0). M3 adds wheel ω and shaft rows to the same PGS solver (06 §3.6, §4.1), which engine-sim needs. M6 moves the chassis to a PhysX `Rigidbody` with `useGravity = false` (06 §3.4, V1).
- **Coordinates.** One rule everywhere: Unity = (−x, y, z), Unity yaw = −JS yaw, matrices `S·M·S` (03 §2). Handedness-sensitive JS code (steering sign, left/right offsets in traffic, camera shoulder, minimap) is rewritten, not copied; the list is in 03 §2. An axis "canary" asset and test guards the Blender export too (07 §3.2).

### 2.5 Contracts frozen per milestone

Four interfaces change only through an explicit version bump, so lanes can work in parallel: CanonData v1 (format and manifest), the ESP ABI v1 (`esp_version() == 1`), `IVehicleEngine`, and the `LaneGraph` asset format. A change to any of them needs a note in the milestone report and updated tests in the same commit.

---

## 3. The engine-sim plan

### 3.1 Native plugin build

- **Vendor, don't fork.** Copy upstream engine-sim `85f7c3b`, scs `e009f4f` and piranha `432f0b1` byte-for-byte into `native\third_party\`. `vendor-check.ps1` proves the copy is unmodified. All fixes live in our sources (01 §8): the `lastValveLift[8]` out-of-bounds write is removed by overriding `writeToSynthesizer()`; the delta include is satisfied by empty shim headers; the 10 kHz exhaust delay filters, the 44.1 kHz synthesizer rate, the latency-driven step adaptation and the one-step clutch-limit lag all have plugin-side workarounds (01 §8 items 1-14). Report 06 §4.8 assumed a fork for these; 01 then showed by building that none is needed, so this proposal follows 01.
- **Our own CMake** compiles the 41 core files and the 29 scs files (01 §1.3: 0 errors, 0 warnings), and links nothing from delta-studio, csv-io, Discord or googletest (02 §2).
- **Targets:** `scs` (static), `enginesim_core` (static), `piranha` (static, `path.cpp` replaced by `piranha_path_std.cpp`), `es_script` (static, the 4 scripting sources), `EngineSimPlugin` (shared), `esp_cli` (exe), `esp_tests` (exe, ctest).

### 3.2 The piranha / flex-bison question

piranha needs flex and bison at build time (bison ≥ 3.2 with `lalr1.cc`, flex with `%option c++`), and no generated parser is committed upstream (01 §5.4, 02 §3). Everything else in piranha already compiles with MSVC and no Boost (01 §5.4).

**Recommendation (decision U4):** with the user's approval, download the portable win_flex_bison 2.5.25 zip (win_flex 2.6.4, win_bison 3.8.2) once into `native\tools\win_flex_bison\` (gitignored, nothing installed system-wide), generate `scanner.auto.cpp`, `parser.auto.cpp` and `parser.auto.h`, and commit them to `native\generated\piranha\`. Later builds never need flex or bison (CMake option `ESP_REGENERATE_PARSER=OFF`). This loads all 27 shipped `.mr` engines and community engines with zero porting error (01 §5.2, §5.4). piranha is MIT on GitHub; ship its licence text in third-party notices (01 §5.4).

**If the user says no,** nothing in the schedule moves. The plugin also exposes `esp_load_builtin(name)`, backed by C++ engine builders: 02's EJ25 port is proven against the real engine (229 N·m at 4,500 rpm, 02 §6.2), and the box truck's Chevrolet 454 is ported the same way (1-3 hours plus a dyno check, 01 §5.3). Report 02 preferred this route (02 §9); 01 preferred piranha. The ABI serves both.

Loader rules either way (01 §5.4): absolute search path to `StreamingAssets\EngineSim\es`, reset `es_script::Compiler::output()` before each compile, a global compile mutex, a generated wrapper `.mr` that calls `main()` or `set_engine(<node>())` (older files such as `chevrolet/chev_truck_454.mr` have no `main`, 01 §5.1), and one `Function` constructed on the main thread before any engine is built (01 §6.1).

### 3.3 ABI

Adopt 01 §7.2 as ESP ABI v1, unchanged (`esp_create`, `esp_load_script`, `esp_get_info`, `esp_set_controls`, `esp_step`, `esp_step_begin`/`esp_step_end`, `esp_read_audio`, `esp_calibrate_inertia`, `esp_dyno_sweep`, `esp_render_offline`), plus three additions:

```c
ESP_API int32_t esp_load_builtin(EspEngine* e, const char* builder_name_utf8, char* err_utf8, int32_t err_capacity); /* C++ builders */
ESP_API int32_t esp_poll_log(EspEngine* e, char* dst_utf8, int32_t capacity);        /* main thread; no callbacks from native threads */
ESP_API int32_t esp_set_thread_qos(EspEngine* e, int32_t high_qos);                  /* opt the worker out of Windows power throttling */
```

`esp_set_thread_qos` exists because Windows parked report 02's unfocused engine threads on E-cores (02 §6.4); it calls `SetThreadInformation(ThreadPowerThrottling)` with throttling off. Every call returns a status and catches all C++ exceptions inside the DLL (01 §7.1), so a bad engine file shows an error message instead of crashing the game.

### 3.4 Threading

- Each simulated engine gets its own native worker thread (`EspCreateDesc.worker_thread = 1`). The main thread only latches controls and kicks or collects steps; the audio thread only reads the ring (01 §7.1).
- **Pipelining, never blocking.** `VehicleSimSystem` collects step *k−1* at the start of `FixedUpdate` *k* (`esp_step_end(..., timeout_us = 0)`), runs BRV, then kicks step *k* with the crank-speed samples of its 4 substeps (01 §7.4, 06 §4.4). If results are missing for more than 4 steps (a hitch), the vehicle uses the engine's baked torque map for those steps and engine-sim resumes from the newest command without catching up (06 §4.5 item 5).
- **Clock.** A fractional step accumulator, `setTargetSynthesizerLatency(0)`, one `endInputBlock()` at init and the render-before-`endFrame` order keep engine-sim time locked to physics time (01 §3.3). `Time.timeScale` is passed as the simulation speed (01 §4.4).
- **Budget** (01 §6.2, 02 §6): a local V8 at 10 kHz with 8 gas substeps costs about 37-40 % of a P-core including the AVX2 FIR, i.e. about 2.9 ms of worker time per 120 Hz step. That is fine on the 24-thread laptop; a free-to-play PC needs the quality tiers in §3.6.

### 3.5 Coupling: staged, so every stage is playable

| Stage | Mode (01 §7.2) | What drives the truck | What engine-sim does | Why this order |
|---|---|---|---|---|
| M0 Engine Bench | `ESP_MODE_FREE` | nothing | Starter, ignition, throttle, optional dyno hold; live torque curve | Proves build, audio path and CPU on day 10 |
| M1 First drive | `ESP_MODE_FOLLOW_RPM` | The ported JS torque curve (`LegacyJsEngine`) | Dyno-hold at the JS rpm with the real pedal: real combustion, intake and exhaust sound, load heard through manifold pressure (01 §6.3 tier R1) | Engine-sim sound in week 4 without waiting for the new driveline. The same mode later serves remote players, so nothing is thrown away |
| M3 VS-1 | `ESP_MODE_SLAVED_CRANK` (Option D) | engine-sim's net indicated torque, applied to BRV's crank shaft with one step of lag | Its crank follows Unity's crank-speed trajectory through an 8 Hz PD; firing pulses pass through (01 §3.4, 06 §4.4) | The long-term coupling for BRV: multi-axle drivelines with diffs cannot live inside engine-sim, whose constraints take at most 2 bodies (01 §3.2). 01 ran it stable at 50 and 120 Hz (01 §3.5) |
| M12 GT race car | `ESP_MODE_PROXY` (Option A) | engine-sim integrates crank, clutch and the lumped driven axle against Unity's tyre law | as left | Most accurate mode, zero added lag (Δv ≤ 0.06 m/s, 4 rpm in cruise, 01 §3.5) for single-driven-axle race cars where D's limiter lag would be felt (01 §3.6) |

Details for M3, all from 01 §3.6 and 06 §4.4-4.5:
- engine-sim's own transmission is in neutral with clutch limits 0 (`changeGear(-1); update(0)`), and its road-load constraint is removed.
- Crank inertia is calibrated at load with `esp_calibrate_inertia` (01 measured 0.4570 vs the crankshaft's 0.4537 kg·m²); BRV's crank uses exactly that value, with any extra flywheel added on the Unity side only (06 §4.4.4).
- PD gains `K_p = J·(2πf_c)²`, `K_d = 1.4·(2πf_c)·J`, f_c = min(8 Hz, idle firing frequency / 4), torque clamp 4 × peak.
- Rev limiting uses engine-sim's own spark cut, plus a Unity fuel-cut backstop at redline + 150 rpm, because D's one-step lag lets rpm overshoot while bouncing on the limiter (270-350 rpm against the reference in 01 §3.5). If the user can feel it, the fixes are, in order: a `∂T/∂ω` feed-forward, averaging over two steps, or PROXY mode for that vehicle.
- Open the clutch during shifts; neutral means clutch pressure 0; reverse is a negative ratio (01 §3.6).
- Until probe P4 passes (§6, M3), the M1 path stays available as a "classic engine" switch, so a coupling problem never blocks a build.

### 3.6 Audio path

1. Never start engine-sim's synthesizer thread. Render synchronously on the engine worker right after each batch of steps (`renderSync`, 01 §4.3), with our AVX2 FIR (0.51 µs per sample at 10,000 taps instead of 3.92, 01 §4.2).
2. Re-initialise the synthesizer at `AudioSettings.outputSampleRate` (48 kHz) and resample each impulse response once at load (01 §4.2).
3. Hand samples over through a lock-free single-producer, single-consumer float ring of 16,384 frames. Fill target ≈ one physics step + one DSP buffer + 10 ms (about 24 ms at 120 Hz with Best latency). Absorb DSP clock drift with a ±0.5 % trim of the synthesizer input rate. On underrun, decay the last sample over 2 ms and count it (01 §4.4).
4. Set synthesizer `volume` to about 0.5, because default levels clip 0.4-3 % of samples (02 §6.7).
5. **Unity side.** `EngineSimAudio.OnAudioFilterRead` for the local engine from M0 (01 §4.5, lowest effort). The M0 spike also tests, in 6000.3.20f1: (a) whether `spatialBlend = 1` attenuates and pans filter-generated audio (old issue-tracker entries say no), and (b) a Unity 6.3 `IAudioGenerator` with a Burst `[DllImport]` into `esp_read_audio` (01 §4.5, §7.4). Remote and AI engines use whichever passes; the fallback is a looping DC clip of 1.0 multiplied in the filter, then `AudioClip.Create(..., stream: true, PCMReaderCallback)`.
6. **Mixer:** `Master > Engines (Local, Remote) > Vehicle SFX > Ambience > UI`, with one shared reverb bus for remote engines instead of per-engine convolution (02 §7 item 6).
7. Tyre squeal, gravel, wind, horn and impacts are looping AudioSources driven by the same state values the JS synth uses (03 §4.24).

### 3.7 Remote players and AI engines

| Tier | Who | Method | Cost |
|---|---|---|---|
| R0 | the local player | Full engine-sim, SLAVED_CRANK (or FOLLOW_RPM before M3), audio | 0.25-0.6 core (01 §6.3) |
| R1 | the 2-4 nearest remote players; the nearest AI car within 60 m if a slot is free | engine-sim FOLLOW_RPM at the script frequency with 4 gas substeps, replicated rpm, pedal and ignition; 3D audio | about 0.25 core each for a V8 (01 §6.3) |
| R2 | other remotes and AI | Loops rendered offline with `esp_render_offline` on an rpm × throttle grid (every 250-500 rpm × closed / 50 % / WOT), played by a crossfading `EngineLoopPlayer` | under 0.5 % CPU for dozens of cars; 1-3 MB per engine (01 §6.3) |
| R3 | far away | culled, or one drone layer | 0 |

`EngineAudioBudget` assigns tiers every 0.25 s by distance and loudness and caps R1 by quality tier (High 4, Mid 2, Low 1). AI physics uses `TorqueMapEngine` built from `esp_dyno_sweep` (06 §4.7). The dedicated server never runs engine-sim (01 §6.3, 04 §3.7). engine-sim is not deterministic, so the network carries engine state (rpm, throttle, load, flags: about 6-8 bytes), never inputs (01 §6.3, 06 §4.6).

**Quality tiers for the local engine:** High = script frequency with 8 gas substeps; Mid and Low = script frequency with 4 substeps (torque within 1 %, 01 §6.2). The simulation frequency itself is never lowered as a quality knob, because it changes the engine (the LS lost 35 % torque at 5 kHz; the EJ25 gained 29 %, 01 §6.2, 02 §6.3).

### 3.8 The diesel and turbo gap

engine-sim has no compression ignition, no forced induction and no engine brake (01 §5.3, 06 §2 item 8). Plan:

1. **Now: honest stand-ins.** The slice truck uses the Chevrolet 454 gas V8; real gas medium-duty trucks exist, so this is authentic rather than a stand-in (06 §4.8). The JS truck was voiced as a 6-cylinder diesel (idle 650, redline 2,700 rpm, "rough" clatter in `audio.js`), so this changes its character; that is decision U6.
2. **Unity-side layers, no fork:** a turbo whistle driven by a spool model fed from engine-sim's intake flow and manifold pressure, and a Jake brake as a Unity retarding torque `T ≈ P_jake(rpm)/ω` with a sampled bark (06 §4.8 item 8). These are labelled as layers, not simulation.
3. **Diesel-character stand-in for the semi and the 6x6** (M9-M10): an I6 built from `03_2jz.mr` or `M52B28.mr` with a long stroke, heavy flywheel, small chamber volume, diesel fuel constants (`molecular_afr` 17.75, `energy_density` ≈ 45.5 kJ/g) and a 2,500-3,200 rpm redline (01 §5.3). It will still sound like a big petrol engine (06 §4.8).
4. **Fork track "engine-sim-br", after M8 and only if the user wants it (U12):** forced induction (boost pressure on the intake's upstream atmosphere with spool lag and wastegate, 06 §4.8 item 6), then compression-release braking (item 8), then compression ignition (item 7: weeks of research). The fork lives beside the untouched vendored copy so both can be compared. The third-party `kyan0s-git/engine-sim` fork (history starting 2026-10-11) is reference reading only after a licence and code review, never a dependency (01 §5.3, §9.7).

### 3.9 Engine roster

| Vehicle | Engine | Source | Status |
|---|---|---|---|
| Engine Bench (M0) | Subaru EJ25 flat-4 | `esp_load_builtin("ej25")` from 02, or `01_subaru_ej25_eh.mr` | validated against the real engine (02 §6.2) |
| **Box truck (slice)** | Chevrolet 454 V8 | `chevrolet/chev_truck_454.mr` or the C++ port | needs a dyno-vs-spec pass (01 §5.3) |
| Pickup | GM LS V8 or 60° V6 | `07_gm_ls.mr` / `04_60_degree_v6.mr` | LS rebuild ran hot (715 N·m at 4,000 rpm); check against the script once piranha runs (01 §5.3, §9.2) |
| Hatch | Honda B18C5 VTEC I4 | `05_honda_vtec.mr` | as shipped |
| Rally coupe | Subaru EJ25 | as above | validated |
| Semi, 6x6 | diesel-character I6 stand-in (§3.8), or Merlin V12 / 454 with a heavy flywheel | 06 §4.8 | placeholder |
| GT race car | Ferrari F136-class V8 | `08_ferrari_f136_v8.mr` | as shipped |

Names inside the game are fictional (07 §9); the `.mr` file names stay internal.

---

## 4. Graphics in URP and the Blender vehicle pipeline

### 4.1 Renderer setup (from M0, tuned in M4)

- URP 17.3, Universal Renderer, Forward+ (no 8-lights-per-object limit for headlights and lamps, 07 §2), SRP Batcher, GPU Resident Drawer with GPU occlusion culling (04 §2.2).
- Renderer features: SSAO; Decals (DBuffer) for skid marks, mud splats, oil and patch repairs, not for long road lines (05 §5.8, 07 §6.6).
- Main light shadows with cascades per tier (§4.3). A realtime reflection probe that follows the player vehicle (128 px, time-sliced): the biggest single win for car paint (07 §2). Box-projected baked probes in town.
- Post: Bloom (Kawase/Dual, threshold about 1.0 so HDR lamps bloom), ACES tonemapping to match the JS look, Color Adjustments and Split Toning reproducing the JS grade (desaturated yellow-greens, cool shadows, warm highlights, contrast ×1.05, vignette; 03 §4.12), STP upscaling on lower tiers (04 §2.2).
- Indirect light: gradient ambient driven by the day/night curves from M4. Adaptive Probe Volumes with sky occlusion are baked once for the frozen canon (04 §2.2) as an M9+ improvement. On the 6.7 LTS upgrade, evaluate Surface Cache GI, SSR and GTAO (04 §1.2).

### 4.2 What gets prettier at each milestone

| Milestone | Look work |
|---|---|
| M0 | Terrain with per-vertex colours from the JS palette rules (03 §4.3), JS-style asphalt ribbons, buildings from `static_props.glb`, trees from `trees.glb` in LOD Groups, a water plane, sun plus gradient ambient, ACES |
| M1 | Truck from `truck.glb` with name-remapped materials (paint with clear coat, glass, lights), headlight spots |
| M2 | Asphalt, concrete and markings materials with world-planar UVs and vertex-colour traffic wear (05 §5.9); simple sign meshes |
| M3 | Truck re-exported through the FBX pipeline with `SG_CarPaint`, `SG_VehicleGlass`, `SG_VehicleLights`, `SG_MudLayer` (07 §6.1-6.5); kerbs, sidewalks, gutters |
| **M4 "Pretty Millbrook"** | Terrain shader port (detail noise, two-surface ragged boundary, grass, soil, gravel, concrete slabs, rock strata, puddles; 03 §4.3); day/night clock and curves from `sky.js` with a procedural sky shader, moon and fog (03 §4.11); 78 lamps as real lights, windows lit by pane ID at night (07 §6.8); tree wind in Shader Graph with LOD cross-fade (03 §4.8); grass port with `Graphics.RenderMeshIndirect` and compute culling (04 §2.2); URP water with depth fade (03 §4.9); truck trim sheet and bakes (07 §7); graphics menu |
| M5 | Truck's new modern body (07 §11 phase E); traffic cars with merged LOD meshes |
| M6 | VFX Graph dust, clods, mud, splash and smoke with the JS emission rules (03 §4.13); mud accumulation on bodywork; tyre tracks |
| M9+ | APV bake, volumetric fog (MIT `com.cqf.urpvolumetricfog`, re-test on 6.7; 04 §2.2), liveries, damage |

The look is accepted by the user from contact sheets and by playing: the 12 canon cameras at 10:00 and 21:00 side by side with the JS shots (03 §3.6), plus a lighting test scene at noon, dusk and night with mud at 0, 0.5 and 1 (07 §5.2, §11 phase C). The bar is "matches or beats the browser look".

### 4.3 Quality tiers (from 04 §2.3)

| Setting | Low (Steam Deck class) | Mid (GTX 1070 class) | High (RTX 3070+) |
|---|---|---|---|
| Render scale | 0.7 + STP, 30-45 fps cap | 1.0 (or 0.85 + STP) | 1.0 |
| Shadows | 2 cascades, 1024, 120 m | 4, 2048, 250 m | 4, 4096, 400 m |
| SSAO | off or half-res | half-res | full |
| Grass | 25 %, 40 m | 60 %, 80 m | 100 %, 150 m |
| Extra lights per object | 2 | 4 | 8 |
| Local engine | script Hz, 4 gas substeps | script Hz, 4 | script Hz, 8 |
| R1 remote engines | 1 | 2 | 4 |

Gameplay-side values (tree near radius, terrain lodScale, builds per frame) live in a `QualityPreset` ScriptableObject next to Unity's Quality levels (03 §4.12).

### 4.4 Blender vehicle pipeline (runs as its own lane from M1)

Phases from 07 §11, mapped onto milestones:

| Phase (07 §11) | Milestone | Done when |
|---|---|---|
| A. Probe: FBX export, axis canary, postprocessor | M1 | Canary test passes: `MARK_Front.z > 0`, `MARK_Left.x < 0`, `MARK_Up.y > 0`, root rotation identity, scale 1; LOD Group built from `_LOD` names; materials remapped (07 §3.2) |
| B. Generator upgrade: sockets, ≤ 9 materials, LOD0-3, JSON sidecar, renames | M2 | `build-vehicle.bat truck` yields an FBX within budget and a valid `build_report.json`; Unity builds the prefab with no hand steps |
| C. Shaders | M3 | Truck in the lighting test scene matches or beats the three.js look |
| D. Bakes and trim sheet | M4 | Panel lines and AO visible; ≥ 400 px/m; headless bake under 5 min |
| E. First photo-inspired vehicle | M5 | User signs off the review sheet; `brief.md` lists 3+ inspirations and the deviations |
| F. Synty probe (optional) | M4-M5 | `synty_audit.json` has no pink materials; dressing decision recorded |
| G. Livery and race cars | M11-M12 | A rally car with 3 liveries and readable numbers on both sides |

Budgets per class are 07 §8.4 (for example box truck or semi tractor 90-150k triangles at LOD0, 9 materials, 2048 body maps).

### 4.5 Which vehicles first, and why

Procedural generation is strongest on boxy classes and weakest on sculpted sports cars (07 §8.1), and the physics work for trucks (shafts, diffs, low range) comes before race-car aero (06 §8). So:

1. **Box truck (slice vehicle).** Today's `truck.glb` drives in M1-M4. In M5 it gets a new modern cab-over body inspired by the Isuzu NPR / Ford F-650 class (06 §7.1) under a fictional brand.
2. **Midsize double-cab pickup** (Ranger / Hilux class, part-time 4x4 with a rear locker; 06 §7.1). The offroad hero of M6; new body around M8.
3. **Semi tractor + 53 ft trailer** (Cascadia / VNL / Scania class, 6x4; kingpin `ConfigurableJoint`, 06 §6.1). M9.
4. **6x6 offroad truck** (Ural / Tatra / Zetros class, CTIS; 06 §5.3). M10.
5. **Rally coupe** (GR86 / GR Yaris Rally2 class). M11, with time trials on the dirt roads.
6. **GT race car** (GT3-inspired; aero, slicks, PROXY coupling; 06 §6.3). M12.
7. Trophy truck later. The JS hatch and dozer move to BRV in M8 with new materials, and get new bodies only if the user wants.

Every new vehicle follows 07 §9: fictional brand and model name, 3+ mixed inspirations, no badges, no signature grilles or lamp signatures. "Ranger 4x4" and "Muncher D3" are renamed before they appear in a Unity build (07 §9 item 2; decision U9).

### 4.6 Synty

Optional, user-supplied (decision U10). The user signs in and downloads the packs; agents never see or use the login (03 §7, 04 §6, 07 §10.1). Packs go to `D:\SyntyPacks\`, are imported into `Assets\ThirdParty\Synty\` (gitignored), and agents only write scripts that place prefabs by path or GUID. They never open, convert or modify Synty meshes or textures, because of the licence's generative-AI clause (07 §10.4). Good uses here: street furniture placed by the road bake (STOP and YIELD signs, signal heads, hydrants, benches, bins; 05 §10), work-site and bog dressing, and placeholder traffic or race cars. Canon Millbrook buildings stay the Blender models (03 §7).

---

## 5. Online plan

### 5.1 Stack (04 §3.7)

- **Netcode for GameObjects 2.13.3** on 6000.3 (3.x after the 6.7 move), client-host topology first, dedicated server later.
- **Transports per build profile:** Unity Transport 2.7.4 for direct IP and LAN tests and for Unity Relay through Multiplayer Services Sessions 2.4.1 (`CreateSessionAsync` with `WithRelayNetwork()`), free under 50 average CCU, for itch.io and test builds. Facepunch.Steamworks 2.5.2 with the community Facepunch transport 2.0.0 over Steam Datagram Relay for the Steam build.
- **Testing:** Multiplayer Play Mode 2.0.2 (up to 4 virtual players) and Multiplayer Tools 2.2.12 network simulator with a 100-200 ms, 2 % loss preset (04 §3.7).

### 5.2 Authority and messages

- **Vehicles are owner-authoritative.** Each client simulates its own vehicle at full quality; the host validates and relays (04 §3.2, 06 §4.6). No prediction or rollback is needed.
- **State message:** 04's 32-byte vehicle state plus 06's 8 bytes of drivetrain state (rpm, pedal, load, gear, clutch, flags, turbo/Jake), sent at 20 Hz with `CustomMessagingManager` on `NetworkDelivery.UnreliableSequenced`, not with stock `NetworkTransform` (04 §3.4).
- **Remote vehicles** are kinematic proxies: 100 ms Hermite interpolation, dead-reckoned to the present near the local player, at most 250 ms of extrapolation. Hits send a contact-impulse message to the owner (04 §3.2). Their engines run on R1/R2 audio (§3.7).
- **AI traffic** is simulated by the host and replicated as lane state (lane id, distance along it, speed) at 5-10 Hz; a car knocked off its lane switches to full state until it recovers (04 §3.2).
- **Terrain deformation** is replicated as 16-byte integer `RutStamp`s applied by an integer kernel, so every machine computes bit-identical heights without relying on float determinism. The host keeps a log and sends per-tile hashes every second; mismatched tiles are resent (2 KB raw); late joiners get all changed tiles (06 §5.6).
- **Economy** (offers, payouts, money) is host-authoritative from the first day it exists, because single player also goes through `IEconomyAuthority` (03 §4.22, 04 §3.2).
- **Validation and anti-cheat:** host-side checks on speed, position jumps and world bounds; kick and ban lists keyed on SteamID; Steam auth tickets. No anti-cheat driver (04 §4).
- **Host leaves:** no host migration in NGO client-host (04 §3.2). Save continuously, end the session cleanly, offer "host again with the same players". Dedicated servers avoid it.

### 5.3 Single player and multiplayer at each milestone

| Milestone | Single player | Multiplayer |
|---|---|---|
| M0-M6 | yes | None, but net-ready: `VehicleState` is used from M1 by the trace recorder and replay, `RutStamp` and the integer kernel from M6, and economy-as-requests is designed in |
| M7 Online alpha | yes | Client-host for 2-8 players over direct IP/LAN, and over Unity Relay if the user has created a Unity Gaming Services project (U11). Trucks and pickup, AI traffic, ruts, remote engine sound. No jobs yet |
| M8 Game loop | yes | Jobs and money host-authoritative, per-player save on the host |
| M9 Steam | yes | Steam lobbies, friend invites and SDR (needs the user's Steamworks account and app, U11). Default session size 16, at most 24 with interest management (04 §3.3) |
| M10 | yes | Headless dedicated server build (`com.unity.dedicated-server` 2.0.2), free for players to run or rent, Motor Town style (04 §3.1); 32-48 players |
| later | yes | Steam Cloud saves; optional global progression with Unity Authentication + Cloud Save (free tier) (04 §4) |

### 5.4 Cost

Steam with player-hosted SDR sessions costs the user US$100 once and $0 a month at any player count; Unity Relay is free under 50 average CCU and about $12 a month at 1k monthly players (04 §3.6). itch.io is free for early public alphas (04 §4).

---

## 6. Milestone roadmap

### 6.1 Order and dependencies

```
M0 Millbrook + Engine Bench
 ├─► M1 First drive (JS physics port + FOLLOW_RPM sound)
 │     └─► M3 VS-1: engine-sim drives the truck (BRV shafts + SLAVED_CRANK) ◄─┐
 └─► M2 Proper intersections (bake: junctions, markings, signs, plates) ───────┤ (kerbs, colliders)
                                                                               │
 M3 ─► M4 Pretty Millbrook ─► M5 Living town (lanes, roundabout, traffic AI) ─► M7 Online alpha ─► M8 Game loop ─► M9+ widen
 M3 ─► M6 Offroad (deformation, mud, low range, winch) ───────────────────────► M7
 Art lane: A (M1) ─► B (M2) ─► C (M3) ─► D (M4) ─► E new truck body (M5) ─► pickup (M8) ─► semi (M9) ─► 6x6 ─► rally ─► GT
 Traffic port starts hidden in M4 on the legacy traffic.json network and switches to the new LaneGraph in M5.
```

M5 and M6 depend only on M3 and can swap places if the user would rather go offroad before traffic (U7).

**Lanes.** An AI agent team of three or four lanes plus an integrator: **W** world and rendering, **V** vehicles and native, **R** roads and traffic, **A** art (Blender), **N** netcode from M7, **G** gameplay and UI from M8. The integrator owns CI, builds, `PLAYTEST.md` and the milestone report.

**Definition of done for every milestone:**
1. `tools\ci\ci-all.ps1` passes: oracle hashes, native ctest, EditMode, PlayMode, player build, player autotests.
2. Every `Canon` test is green, i.e. canon content is unchanged.
3. The performance budget of that milestone is met on this laptop.
4. `builds\M<n>\Backroads.exe` exists with `PLAYTEST.md` (what to try, what is not in yet, how to report) and a contact sheet.
5. `docs\unity\milestones\M<n>.md` holds the CI table, screenshots, measured numbers, known issues and open decisions.
6. A local tag `m<n>`; pushing waits for the user.

Effort is in **agent-days (AD)**: one AI agent working one day, including tests and fixes. The figures are estimates (±50 %); M0 and M1 calibrate the team's real speed, and the plan is re-cut after M1. They sit within the research estimates: about 30 AD for roads (05 §9: 31 days) and 5-7 part-time months for full parity excluding engine-sim and online (03 §5).

---

### M0: "Millbrook in Unity" + Engine Bench (weeks 1-2, about 20 AD: W 8, V 8, integrator 4)

**Deliverables**
- Repo folders, `.gitattributes`, `.gitignore`, Unity project `unity\` with the settings and packages of §1.2, the assemblies of §1.3, CI scripts of §1.8.
- Oracle: `js\exportUnity.js` and the three hash-neutral `props.js` lines (U2), `tools\export\export-canon.mjs` (U3), and `CanonData\v1\` committed: manifest, terrain layers, `world.json`, `placements.json`, `vegetation.json`, `traffic.json`, textures, `reference\static_props.glb`, shots, cameras, probes, `gameplay\*.json` (03 §3.3 plus §1.7).
- `CanonImporter` (Backroads > Import Canon v1), `CanonRuntimeLoader`, `WorldGrid` with Burst queries and the coordinate flip, `ColliderGrid`.
- Terrain chunk meshes (4 LODs, skirts, per-vertex colours from the JS palette rules, a simple Shader Graph). JS-style road ribbons with the edge-line and dashed-centre shader (03 §4.5). Buildings from `static_props.glb` in 64 m chunks. 5,265 trees from `trees.glb` with LOD Groups. Lamps as emissive only. Water plane. Sun and gradient ambient.
- Free-fly camera; keys 1-7 jump to the POIs.
- Native: vendored sources, CMake, `EngineSimPlugin.dll` with FREE and FOLLOW_RPM modes, `renderSync`, the ring, the AVX2 FIR, `esp_cli`, the `ej25` builder; piranha if U4 is approved.
- **Engine Bench** screen: choose an engine, toggle ignition, hold the starter, throttle with W or the trigger, live rpm, torque, manifold pressure and worker ms, and a "dyno sweep" button that plots the torque curve.
- Audio spike report: `OnAudioFilterRead` with `spatialBlend` 0 and 1, `IAudioGenerator` with Burst `DllImport`, underruns at Best latency (01 §9.3).

**Acceptance tests (agent-run)**
- `ci-oracle.ps1`: a fresh export reproduces the 03 §3.2 hashes: H `a8908d7f…`, BASE `2eda4082…`, SURFACE after props `8788d405…`, ROADD `8d31f109…`, MUD `2192f4c8…`, WEAR `85473f6f…`, TINT `08452b46…`.
- EditMode `Canon.ManifestHashes`, `Canon.Counts`: trees 5,265 (Pine 933, Spruce 447, Oak 1,005, Birch 798, Poplar 182, Maple 140, Bush 1,491, Snag 18, Reeds 251); colliders 4,013; lamps 78; lots 59; roads 24 with 1,722 points; traffic nodes 21, lanes 53, connectors 108, loops 5 (03 §3.2).
- EditMode `World.HeightProbes`: the 1,000 exported probes match within 1e-5 m for height and exactly for surface (03 §3.5). `Core.CoordsCanary`: JS (+x) maps to Unity (−x); a JS yaw of +π/2 maps to −90°.
- EditMode `World.PlacementsRoundTrip`: every building's position, rotation, scale and colours equal `placements.json` after the Unity flip, to float precision.
- Native ctest `esp_tests`: EJ25 cranks and idles at 700-900 rpm within 3 s of ignition; WOT dyno-hold torque at 4,500 rpm is 229 ± 12 N·m (02 §6.2); the rendered WAV's dominant pitch is the firing frequency (rpm/30 for a four) ±3 % at 25, 60 and 100 % pedal (02 §6.7); FOLLOW_RPM tracks a 1,000 → 4,000 rpm ramp with < 30 rpm RMS error; a 10-minute ASan soak has no errors, no growth above 1 MB and no underruns at steady state; a V12 (`11_merlin_v12.mr`, if piranha is in) runs 60 s clean under ASan, proving the `lastValveLift` override (01 §8 item 1).
- Player autotest `shots-canon`: 24 screenshots (12 cameras × 10:00 and 21:00) plus a contact sheet next to the JS shots.
- Player autotest `enginebench`: 20 s scripted rev cycle recorded through `AudioRenderer`; `audio_report.json` shows pitch tracking (±3 %), zero underruns and < 0.1 % clipped samples.
- Perf autotest `flythrough`: p95 frame time < 16.6 ms at 2560×1440 on this laptop.

**What the user sees and plays.** Double-click `builds\M0\Backroads.exe`. Fly over Millbrook with WASD and the mouse; press 1-7 to jump to town, depot, farm, quarry, mill, harbor and fuel. Open `Play.bat` next to it: the hills, roads, houses and trees are in the same places. Open the Engine Bench, hold S to crank the flat-four, hear it catch, and rev it.

### M1: "First drive" (weeks 3-4, about 18 AD: V 11, W 4, A 2, integrator 1)

**Deliverables**
- `Backroads.Vehicles` V0: a verbatim port of `vehicle.js` (suspension rays against `WorldGrid`, PGS tyres, the JS engine and automatic gearbox, low range, hull contacts, statics against `ColliderGrid`, water, mud grip and sink from the static MUD layer) at 1/120 s with render interpolation (03 §4.14). All five DEFS are ported; only the truck is exposed in the build.
- The truck from `truck.glb` through glTFast with materials remapped by name and mirrored right-side wheels (03 §4.17).
- **Engine:** `LegacyJsEngine` drives; engine-sim runs the Chevrolet 454 in FOLLOW_RPM for sound, from the exhaust socket. So that a 454 sounds right at the JS truck's rpm, the truck's engine block is remapped power-preservingly: redline 2,700 → 4,500 rpm, idle 650 → 700, torque 760 → 456 N·m. The JS ratio derivation (top ratio = ω_red·r/vmax, 03 §4.14) then rescales every gear so that wheel force at every road speed, top speed (97 km/h), shift points and engine braking are unchanged, because they all scale with redline (03 §4.14).
- Input System actions (Driving map, keyboard and gamepad, deadzone 0.14; 03 §4.26). Chase and hood cameras ported (03 §4.19). Minimal UI Toolkit HUD: speed, gear, rpm bar, surface name. F3 debug overlay.
- Golden traces recorded from the JS build for the truck: 0 → 80 km/h on Route 9, braking 60 → 0, a loaded low-range climb up the quarry road, driving into Blackwater Bog (03 §3.6).
- Art lane: phase A (FBX canary, postprocessor) (07 §11).

**Acceptance tests**
- PlayMode `Vehicle.GoldenTraces` [tune tolerances]: 0-80 km/h time within ±7 % of JS; stopping distance from 60 km/h within ±8 %; quarry climb time within ±10 %; bog sink depth within ±15 %. Compared as per-second statistics, not exact positions, because contact dynamics are chaotic (03 §3.6).
- PlayMode `Vehicle.Truck454RemapEquivalent`: the remapped truck reproduces the unremapped trace within 2 %.
- PlayMode `Vehicle.SettleParked`: five vehicles placed on the garage bays settle within 240 steps to |v| < 0.01 m/s (03 §4.27).
- PlayMode `EngineSim.FollowInVehicle`: during the 0-80 trace, engine-sim rpm tracks the JS rpm with < 40 rpm RMS error, zero ring underruns, worker time < 3.5 ms per step.
- EditMode `Art.Canary` (07 §3.2).
- Player autotest `drive-route9`: WAV pitch follows the firing frequency (rpm/15 for a V8) ±3 % through the gears; p95 frame time < 16.6 ms.

**What the user sees and plays.** You start in the truck at the Millbrook garage. Drive anywhere: Route 9 to the fuel station, the dirt road to the farm, into the bog. The handling should feel like the browser game, and you hear a big V8 that rises and falls with your foot and drops at each gear change. Tell us whether the feel matches; this is the first sign-off (U5).

### M2: "Proper intersections" (weeks 5-6, about 24 AD: R 13, V 5, A 5, integrator 1)

**Deliverables**
- `Backroads.Roads.Editor` stages R0, R1 and R3 of 05 §9:
  - graph import and cleanup: T-splits including the 6 dirt road ends, 1 m node clustering, town-zone class split into MainStreet and Highway, L-bends, and **major road chosen by road class first**, which fixes nodes 2 and 5 where the code today makes the streets major (05 §5.1);
  - per-class kerb-line radii: 6 m downtown (nodes 7 and 9, where a 7.5 m radius would leave only 1.2-2.1 m to the nearest building), 7.5 m residential, 12 m in the fuel yard, 9 m with 10 m paved aprons at dirt roads (05 §1.3, §5.3);
  - mouths, stop lines at the flattest spot (nodes 5 and 17 are at −9 %), junction polygons, ring meshes, crown warp and the major-road plateau (05 §5.4-5.6);
  - markings after MUTCD 11th edition: double yellow on MainStreet, rural broken yellow and edge lines, 0.45 m stop lines, continental crosswalks downtown, no centre line on residential streets, nothing carried through junctions (05 §5.8);
  - 64 m road chunks with LOD0 and LOD1, MeshColliders cooked in jobs (05 §5.13);
  - road plates as a sparse height and surface overlay (`CanonData\overlays\v1.1`), applied to `WorldGrid` at load, with lot pads re-applied afterwards (05 §5.10);
  - control assignment and placeholder STOP and YIELD signs placed per 05 §6.2-6.3: two-way stops where streets meet highways, the all-way stop at node 16, stops on dirt approaches.
- The square (nodes 0 and 1) keeps its current look until the user decides on the roundabout (U8); a temporary override marks it "legacy".
- V lane: BRV shaft solver behind a feature flag. A lane: phase B; the truck is re-exported through the pipeline as `VH_Truck_Box75.fbx` and replaces the GLB.

**Acceptance tests**
- EditMode `Roads.Validation` (05 §8): every junction surface simple and counter-clockwise; every mouth vertex shared bit-exactly between ribbon and junction, height seams < 1 mm; terrain at least 0.03 m below the surface 0.25 m inside every footprint and within 0.1 m of the edge 1 m outside; grades reported; exactly one STOP per controlled approach, on its right, 0.6-2 m from the kerb, ≤ 15.2 m from the intersected edge; yield lines only with YIELD signs, stop lines only with STOP; baking twice gives identical hashes.
- EditMode `Roads.Synthetic`: a 30° Y, a 5-arm star, two junctions 25 m apart, a T on a 15 m curve and an arm shorter than its tangent all bake or fail with a clear error (05 §8).
- EditMode `Canon.PlacementsUnchanged`: after the bake every building, prop, tree, lamp and collider transform is bit-identical to `CanonData\v1`, and the plates change no height inside a lot pad.
- EditMode `Roads.ArmLanesMatchLegacy`: arm lane lines lie within 0.3 m of the `traffic.json` lanes at unchanged junctions (05 §5.14).
- EditMode `Art.TruckBudget`: `build_report.json` within the 07 §8.4 budget, ≤ 9 materials, sockets present, wheel radius within 2 % of the sidecar.
- Player autotest `shots-junctions`: close-ups of the 11 junctions, 5 L-bends and 6 dirt aprons in a contact sheet; `drive-downtown` passes through nodes 2, 7, 9 and 16 with no z-fighting reported by the shot reviewer.

**What the user sees and plays.** Drive downtown: junctions are single clean asphalt surfaces with rounded corners, white stop lines, zebra crossings near the centre and STOP signs on the right. The centre line stops at the junction instead of running through. Side streets ramp onto the main road's camber. Dirt roads meet the highway with a short paved apron and a STOP sign.

### M3: VS-1, "engine-sim drives the truck" (weeks 7-8, about 23 AD: V 11, R 7, A 4, integrator 1)

**Deliverables**
- BRV shaft solver: wheel ω as a solver degree of freedom, clutch row, gearbox row with the torque interrupt during shifts, open rear differential, brake rows, rolling resistance; rigid chains lumped; magic-formula secant tyre rows with the truck 22.5" table and the JS surface multipliers (06 §3.6-3.7, §4.1). JS suspension, hull contacts and statics are kept.
- AMT controller: auto-clutch from rpm (06 §4.2.1) plus the JS shift-on-ground-speed rules (06 §4.2.2). The box-truck driveline of 06 §7.3 re-geared for the 454 (final 5.29).
- SLAVED_CRANK coupling with calibrated inertia, PD gains, torque clamp, stale-result fallback, engine-sim spark-cut limiter plus the Unity fuel-cut backstop (§3.5). Ignition key and starter; the engine can stall and restart.
- Wheel spherecasts against road and kerb colliders, so a kerb is a real bump (06 §3.5 item 2).
- Roads stage R2: Clipper2 kerb loops, Barrier150 kerbs and gutters, 1.5 m residential walks, storefront walks to the building line, depressed corner ramps with detectable warning texture, dropped kerbs at the 51 driveway and apron cuts (05 §5.7).
- Art phase C: `SG_CarPaint`, `SG_VehicleGlass`, `SG_VehicleLights`, `SG_MudLayer` on the truck.

**Acceptance tests**
- PlayMode `Coupling.P4Matrix` (06 §4.5, §8): clutch dump at 3,500 rpm in 1st, stall by braking in 2nd, WOT rev in neutral, idle in gear against the brakes, full load in top gear at 90 km/h. Pass: no rpm oscillation above ±15 rpm in the 120 Hz band, no NaN or divergence, stall and restart work.
- PlayMode `Coupling.InertiaCalibration`: J_eff within 3 % of the analytic value (06 P3).
- PlayMode `Vehicle.HillHold` (06 P5): truck parked on 10-35 % grades for 60 s with brakes on creeps < 1 mm/s.
- PlayMode `Vehicle.LaunchContinuity` [tune]: 0-80 km/h within ±15 % of the M1 trace, so the feel does not jump.
- PlayMode `Vehicle.KerbBump`: at 10 km/h a 150 mm kerb lifts the wheel by at least 0.12 m.
- EditMode `Roads.KerbsVsCanon` (05 §8 item 4): kerb to building footprint at least the walk width (1.5 m residential, 1.0 m at downtown corners); every mailbox, tree and lamp outside the walk band; sidewalk cross slope ≤ 2.1 %, ramps ≤ 8.33 %.
- Player autotest `vs1-run`: a scripted lap of town with a stall, restart, hill start and WOT pull. Rev-limiter rpm stays within redline +300 rpm; main thread `FixedUpdate` p95 < 2 ms; engine worker < 3 ms per step; zero underruns.

**What the user sees and plays: the vertical slice.** Press I for ignition and hold S: the 454 cranks, catches and idles with a lope. Pull away and hear it labour on the hill past the depot. Brake hard in 2nd and it stalls; restart it. Floor it in neutral and it bounces off the limiter. The truck bumps up kerbs; downtown has sidewalks, corner ramps and dropped kerbs at driveways. Tell us whether this driveline feels right (U5).

### M4: "Pretty Millbrook" (weeks 9-10, about 32 AD: W 14, A 5, V 3, R 10 for the hidden traffic port)

**Deliverables**
- The look work in §4.2 for M4: terrain shader port, day/night clock with procedural sky, moon and fog, building prefabs with recolouring and lit windows, 78 lamp lights, headlights with cookies (player) and fake beams (traffic), tree wind and cross-fade, grass, water, the post stack, the reflection probe that follows the player.
- Quality presets Low, Mid and High (§4.3) with an F2 graphics menu; IL2CPP player builds from now on.
- Art phase D: trim sheet and bakes for the truck.
- R lane: the `traffic.js` port starts on the legacy `traffic.json` network, hidden behind a flag.

**Acceptance tests**
- EditMode `Canon.WindowsLit`: for every building, the number of lit panes equals the number of 1s in its canon `lit` string, matched by pane name or centroid.
- EditMode `Look.NoErrorShaders`: no renderer in the Millbrook scene uses an error shader; no missing textures.
- Player autotests `shots-canon` (again) and `shots-lighting`: noon, dusk and night × mud 0, 0.5, 1 contact sheets.
- Perf autotest `flythrough-high`: p95 ≤ 11 ms (90 fps) at 2560×1440 on High; `flythrough-low` at 1920×1080 p95 ≤ 8 ms as a proxy for weaker PCs [tune]; draw calls from `ProfilerRecorder` ≤ 2,000 on High and ≤ 1,200 on Low [tune].

**What the user sees and plays.** Press ] and [ to move time. Watch the sun set behind the ridge, the street lamps and shop windows come on, the truck's headlights sweep the road. Grass sways, trees move in the wind, the lake reflects the sky. Try the graphics menu.

### M5: "Living town" (weeks 11-13, about 30 AD: R 16, V 5, A 8, integrator 1)

**Prerequisite:** the user's road decisions U8 (05 §9).

**Deliverables**
- Roads R4: a 4-leg roundabout at the square (ICD 32 m, 5.5 m circulatory roadway, 3 m truck apron, splitter islands, yield lines) with R3 realigned as 05 §7.1 proposes, or the user's alternative; the fountain moves to the island; the delivery ring moves where the user chooses.
- Roads R5 and R6: lanes, connectors (right turns concentric with the kerb fillet, left turns as tangent arcs), design-truck swept-path checks, conflicts, controls, the optional signal at node 2 with fixed timings, street-name blades and speed-limit signs (05 §5.14, §6.3).
- Traffic AI on the new `LaneGraph`: the JS fleet of 12 (6 hatch, 4 pickup, 2 truck), ghosts beyond 225 m, materialising within 175 m out of view, pure pursuit, IDM, junction arbitration under MinorStop, AllWayStop (first come, first served, ties yield right), RoundaboutYield and Signal, and the rescue rules (03 §4.21). The bypass manoeuvre waits for M8.
- AI cars on BRV LOD1 with `TorqueMapEngine`; offline engine loops; `EngineAudioBudget` with R1 for the nearest AI car.
- Art phase E: the slice truck's new modern cab-over body under a fictional brand.

**Acceptance tests**
- EditMode `Roads.Lanes` (05 §8 item 5): every approach lane has a connector; every departure lane is reachable; no connector tighter than 1/6 m⁻¹ (car) or 1/9 m⁻¹ (truck) unless marked `noTruck` or `encroaches`; conflicts are symmetric.
- PlayMode `Traffic.Soak` (Slow category, 30 minutes): no AI-AI collision inside a junction, no deadlock longer than 60 s, every car completes at least 20 junction movements, all-way stop order follows arrival order, no roundabout entry while a circulating car is within 22 m upstream.
- PlayMode `Traffic.StopCompliance`: on stop-controlled approaches every AI front bumper comes to rest within 0.5 m of the stop line before proceeding.
- Perf: player + 12 AI cars, `FixedUpdate` p95 < 4 ms.
- Art: user sign-off on the new truck's review sheet; `brief.md` lists the inspirations and deviations.

**What the user sees and plays.** Cars and trucks drive around town, stop at STOP signs, take turns at the all-way stop and go round the new roundabout. Your truck has a modern body.

### M6: "Offroad" (weeks 14-15, about 21 AD: V 10, W 6, A 5)

**Deliverables**
- Deformation port (`rut`, `brush`, `cutBlade`, deposits, `relax`) as Burst jobs on a `DELTA_MM` int16 layer with the integer kernel of 06 §5.6, so online play later needs no rewrite; terrain, grass and tree re-snap from dirty rects (03 §4.4).
- The soft-soil model (Bekker sinkage, Janosi-Hanamoto traction, the JS resistance terms) blended with the hard-surface tyre curves (06 §5.3); compaction; wetness.
- Low range, AWD-on-low, a selectable rear diff lock, the winch as a rope row with a drum motor curve (06 §5.7); reset upright; tow to the garage.
- PhysX chassis: `Rigidbody` with `useGravity = false`, convex hulls against buildings, rocks and other vehicles (06 §3.4, V1).
- VFX Graph particles and the JS emission rules; mud building up on the bodywork; tyre, gravel and wind sounds.
- The JS pickup on BRV as the second drivable vehicle (part-time 4x4, rear locker).

**Acceptance tests**
- EditMode `Deform.MatchesJs`: a scripted list of 10,000 `rut`/`brush` calls gives heights within 2 mm and displaced volume within 2 % of the JS float kernel's result.
- EditMode `Deform.Deterministic` (06 P6): the same stamp log gives identical tile hashes on two runs and with Burst on and off.
- PlayMode `Offroad.BogStuckRecover`: in 2WD the truck gets stuck in Blackwater Bog (below 0.6 m/s for 5 s at full throttle); with low range and diff lock it gets out within 30 s; with the winch to a tree it gets out.
- PlayMode `Perf.Deform`: four wheels rutting at 60 Hz cost ≤ 0.5 ms per frame on average for the terrain update.

**What the user sees and plays.** Leave the road: tyres dig ruts with berms, the bog grabs the truck, mud flies and coats the bodywork. Use low range, lock the diff, winch out to a tree. Switch to the pickup and try the same.

### M7: "Online alpha" (weeks 16-17, about 17 AD: N 14, V 3)

**Prerequisite:** U11 for Relay (the user creates a Unity Gaming Services project); without it the alpha runs over direct IP and LAN.

**Deliverables:** §5.2 in full for client-host sessions of up to 8 players: host and join UI with a join code, vehicle state messages, proxies, contact impulses, host validation, AI lane-state replication, the rut-stamp log with tile hashes and resends, remote engine sound on R1/R2.

**Acceptance tests**
- PlayMode (Multiplayer Play Mode, network simulator 150 ms RTT, 2 % loss) `Net.TwoToFourPlayersDrive`: proxy position error against the owner's time-aligned true position < 1.0 m at p95 at 20 m/s; no proxy jump > 2 m; downstream < 12 KB/s per client at 4 players (04 §3.3).
- PlayMode `Net.TerrainConsistency`: 4 players rutting the same area for 10 minutes end with identical tile hashes everywhere (06 §8 V7).
- PlayMode `Net.HostValidation`: a client reporting twice its vehicle's top speed is kicked within 2 s.
- Player autotest `remote-engines`: 4 R1 engines plus loops stay within 1.5 cores of worker time.

**What the user sees and plays.** Host a session and give a friend the code, or run two copies on this PC. See each other's trucks, hear each other's engines, push each other around, and see the same ruts.

### M8: "The Backroads game" (weeks 18-20, about 26 AD: G 14, V 6, A 6)

**Deliverables:** jobs and economy through `IEconomyAuthority` (03 §4.22); the full HUD with job tracker, freight boards, toasts, rotating minimap and big map from `map.png` (03 §4.23); save JSON v1 every 10 s (03 §4.25); fuel, wash, tow; the hatch, rally and dozer (blade and tracks) on BRV under their new fictional names; get in and out on foot with a simple character controller (03 §4.18); traffic bypass manoeuvre; the pickup's new body.

**Acceptance tests:** PlayMode `Jobs.DeliveryFlow` (accept, load 1.6 s, scripted drive, unload, payout equal to the JS formula); `Jobs.DamagePayout`; `Save.RoundTrip`; `Net.EconomyAuthority` (a client cannot change money; payouts only come from host triggers); UI screenshot autotests of every HUD panel.

**What the user sees and plays.** Everything the browser game does, in Unity, alone or with friends: pick up freight at the boards, deliver it, earn money, refuel, tow, on-foot get-in and get-out, with all five original vehicles.

### M9 onward: widen (a playable drop every 2 weeks)

| Drop | Content | Key acceptance test |
|---|---|---|
| M9 | Semi tractor + 53 ft trailer with fifth-wheel coupling (06 §6.1); Steam lobbies and SDR (U11); diesel-character I6 stand-in | Reverse a loaded trailer onto the depot dock; jackknife recovers; two Steam accounts join through an invite |
| M10 | 6x6 offroad truck with CTIS; dedicated server build | The 6x6 crosses the bog with CTIS; 16 clients on a headless server on this PC |
| M11 | Rally coupe; point-to-point time trials on the dirt roads; liveries (07 phase G) | Three liveries readable on both sides; timing gates in multiplayer agree |
| M12 | GT race car with aero, slicks, setup sheet, PROXY coupling (06 §6.3); race event | Plausible lap times; PROXY rpm within 10 rpm of the 01 §3.5 reference in cruise |
| later | Unity 6.7 LTS upgrade after its 2nd-3rd patch (04 §1.2), APV bake, damage (06 §6.4), turbo and Jake layers, the engine-sim-br fork if wanted (U12), a race track as a CanonData v2 extension if wanted (U13) | |

### 6.2 Effort summary

| Milestone | Weeks | Agent-days | Main lanes |
|---|---|---|---|
| M0 | 1-2 | 20 | W, V |
| M1 | 3-4 | 18 | V, W, A |
| M2 | 5-6 | 24 | R, V, A |
| M3 (VS-1) | 7-8 | 23 | V, R, A |
| M4 | 9-10 | 32 | W, R, A |
| M5 | 11-13 | 30 | R, V, A |
| M6 | 14-15 | 21 | V, W, A |
| M7 | 16-17 | 17 | N, V |
| M8 | 18-20 | 26 | G, V, A |
| **Total M0-M8** | **20 weeks** | **about 210** | 2-4 lanes busy at a time |

The user's time per milestone: about an hour of play with `PLAYTEST.md`, plus the decisions in §7.2 when they come due.

---

## 7. Risks and decisions

### 7.1 Top risks

| # | Risk | Mitigation |
|---|---|---|
| 1 | **Unity audio path.** Filter-generated audio may not spatialise; the `IAudioGenerator` API details are unverified (01 §4.5, §9.3) | Spike in M0 week 1; local engine needs no spatialisation; fallbacks: DC-clip multiply, then `PCMReaderCallback` streaming |
| 2 | **SLAVED_CRANK feel.** Limiter overshoot of 270-350 rpm against the reference (01 §3.5); never run inside a real BRV (06 §9.1) | 120 Hz physics, engine-sim spark cut plus the Unity fuel-cut backstop, `∂T/∂ω` feed-forward, PROXY mode per vehicle; the M1 "classic engine" switch keeps builds shippable until P4 passes |
| 3 | **CPU on players' PCs.** The local V8 costs 0.25-0.6 core here; a typical free-to-play PC may need twice that (01 §6.2, 02 §9) | Quality tiers (§3.6, §4.3), AVX2 FIR, R1 caps, offline loops; measure on a 6-core laptop before M7 (01 §9.6; U14) |
| 4 | **Canon drift.** Handedness mistakes, pane renumbering, RNG-sensitive placements (03 §0, §2, §3.3) | Export instead of regenerate; hash manifest; `Canon.*` tests in every milestone gate; placements-unchanged test after every road bake; canary assets; pane mapping by name or centroid |
| 5 | **Intersection geometry.** Skewed or short arms, the 13.8 m harbor junction next to the square, tight downtown corners (05 §1.2, §1.3, §9) | Radius back-off loop, bake errors with clear messages, explicit `roads-overrides.json`, the synthetic test cases, the "legacy" override for the square until U8 |
| 6 | **Feel regression** when BRV replaces the JS model | Golden traces from the oracle, launch-continuity test, user sign-off at M1 and M3, keep the V0 model as a selectable "classic handling" preset until the user prefers the new one |
| 7 | **Scope creep and agent collisions** in a 4-lane team | Assembly boundaries (§1.3), frozen contracts (§2.5), feature flags, `main` always playable, re-plan after M1 |
| 8 | **Deformation cost** when four wheels rut the terrain at 60 Hz | Budgeted dirty rects, partial vertex-buffer updates, measured in M6 (06 P2) |
| 9 | **Diesel and turbo authenticity** for semis and 6x6s (06 §9.3) | Honest stand-ins and labelled layers now; the fork is a separate, later decision (§3.8) |
| 10 | **Netcode.** No host migration; player hosts can cheat (04 §3.2, §4) | Continuous saves, clean session end, host-side validation, Steam auth tickets, dedicated server option (M10) |
| 11 | **Native crash or DLL lock.** A C++ fault kills the game; Unity locks the DLL in the editor (01 §7.4) | Exception firewall in every ABI call, ASan soak in CI, compile mutex, copy script that refuses to overwrite a locked DLL |
| 12 | **Licences and IP.** Synty's AI clause (07 §10.4); real vehicle names and designs (07 §9) | Agents never touch Synty files, gitignored packs; fictional brands, 3+ inspirations, renames before the first Unity build of each vehicle |
| 13 | **Unity version churn** (6.3 to 6.7 to Unity 7 with CoreCLR) (04 §1.1) | Stay on 6.3 LTS, follow the forward-compatibility rules from day one, upgrade as its own step after 6.7's 2nd-3rd patch |
| 14 | **Tooling approvals delayed** (Playwright, win_flex_bison) | Fallbacks that do not move the schedule: manual one-click export; C++ engine builders |
| 15 | **Git LFS quota** (10 GiB on GitHub Free) | Builds, artifacts, Library and Synty stay out of git; review usage at every milestone report |

### 7.2 Decisions only the user can make

| # | Decision | Recommended default | Needed by |
|---|---|---|---|
| U1 | Same repo with a `unity\` folder, or a new repo | Same repo (§1.1) | M0, day 1 |
| U2 | Allow the hash-neutral export additions to the frozen JS game (`js\exportUnity.js` + 3 lines in `props.js`) | Yes (03 §3.4) | M0, day 1 |
| U3 | Allow a project-local Playwright dev dependency (it uses the installed Edge) for automated exports and golden traces | Yes; otherwise a manual one-click export | M0 |
| U4 | Allow downloading the portable win_flex_bison 2.5.25 zip into `native\tools\` once, to generate the piranha parser | Yes, so all 27 shipped engines load (01 §5.4); otherwise C++-ported engines only | M0 (M1 at the latest) |
| U5 | Handling sign-off: does M1 feel like the browser game, and does M3's engine-sim driveline feel right | Play and tell us | end of M1, end of M3 |
| U6 | Slice truck engine: authentic Chevrolet 454 gas V8 (changes the JS truck's diesel character), or a tuned diesel-character I6 stand-in (days of tuning, still petrol-sounding) | 454 (06 §4.8) | M1 |
| U7 | After VS-1: traffic (M5) before offroad (M6), or the other way round | Traffic first | M3 |
| U8 | Roads (05 §9): realign the harbor road for a 4-leg roundabout or keep 3 legs; where the town delivery point goes; one signal at node 2 or none; keep the −9 % approaches; keep the canon street widths | 4-leg roundabout; kerbside loading bay on MainStreet; no signal; keep grades; keep widths | M5 |
| U9 | Fictional brand and model names; renames of "Ranger 4x4" and "Muncher D3"; 2-4 real inspirations per new vehicle; approve downloading reference photos per vehicle | Agent proposes names, user picks | M5 (first new body) |
| U10 | Synty: which packs, one-time licence (recommended) or SyntyPass; download them to `D:\SyntyPacks\` yourself (never share the login); email Synty about AI coding assistants | Optional; Town + Construction one-time if wanted (07 §10.2) | M4 if wanted |
| U11 | Online services: create a Unity Gaming Services project for Relay; later a Steamworks partner account with the US$100 app fee and tax forms | Relay for the alpha, Steam for launch (04 §3.7, §4) | M7 (Relay), M9 (Steam) |
| U12 | Invest in an engine-sim fork for turbo, engine brake and diesel | Decide after M8 | after M8 |
| U13 | Race content: events on Millbrook's existing roads, or a new track as a CanonData v2 extension | Existing roads first | M11 |
| U14 | Minimum-spec PC to target (sets the Low tier and budgets); can you borrow a 6-core laptop for one test | GTX 1070 / 6-core class (04 §2.3) | M4 |

Also needed from the user, but not decisions: updating Unity to 6000.3.26f1 in Unity Hub when convenient (04 §1.2), and saying when agents may push to GitHub.
