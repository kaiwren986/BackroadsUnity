# Proposal "Risk first": Backroads in Unity

*Status: proposal for review. Date: 2026-10-18. Author: architect agent (angle: retire the biggest technical risks first).*
*Evidence base: the seven research reports in `D:\Backroads\docs\unity\research\`, cited as (NN §x), for example (01 §3.5) = report 01, section 3.5.*
*Nothing in `D:\Backroads` source or the engine-sim tree was changed to write this. Every path below that does not exist yet is a proposal.*

---

## 0. Summary

**In plain words.** Before we build lots of game, we build five small "lab" programs. Each one proves that one hard thing works, and each has a pass/fail test with numbers. If a lab fails, we switch to a fallback that is already written down, instead of finding out months later. New trucks, race cars, Synty dressing and polish start only after all five labs pass. You get something to play from week 2 (the lab builds), a drivable Millbrook with a simulated engine around week 10, and online play with a friend around week 19.

### 0.1 The five spikes and their gates

| Spike | Risk it retires | Headline pass/fail gate | Fallback if it fails | Timebox |
|---|---|---|---|---|
| **S1 engine-sim plugin** | The engine-sim core cannot run inside Unity in real time, on worker threads, with clean low-latency audio and a stable coupling to Unity's driveline | `EngineSimPlugin.dll` built from an **unmodified** vendored engine-sim; 1 local V8 + 4 remote engines in a player build; 0 audio underruns in 10 min; exact step accounting; the 01 §3.5 coupling matrix reproduced (mode D Δv ≤ 0.15 m/s, no NaN) | Mode A or C coupling (same ABI); fewer remote engines; engine-sim as "sound + dyno map" only | 10 agent-days |
| **S2 BRV physics at 480 Hz** | Our own vehicle solver (wheel and shaft rows in one implicit PGS) is unstable, too slow, or jitters when parked | ≤ 30 µs per vehicle per 1/120 s step; 1,000 fuzz runs with 0 NaN; parked creep < 1 mm/s on 10–35 % grades; semi low-gear clutch dump without oscillation; engine-sim mode D inside BRV passes the 06 §4.5 P4 matrix | Verbatim JS model (03 §4.14 phase 1) at 120 Hz, engine-sim coupled in FOLLOW/C mode | 12 agent-days |
| **S3 canon export + identical terrain** | Millbrook does not come out identical in Unity | All seven layer SHA-256s equal 03 §3.2; all counts equal (5,265 trees, 4,013 colliders, 59 lots…); terrain mesh vertices **bit-exact** to `heights.f32`; 1,000 height probes within 1e-5 m; every placement within 1e-4 m | Manual one-click export from the browser; Unity Terrain renderer at ~3 mm quantisation (physics stays exact) | 8 agent-days |
| **S4 road/intersection mesher** | "Proper intersections" cannot be generated watertight and drivable on frozen canon geometry | Millbrook bake with 0 errors; every seam shared bit-exactly and < 1 mm height error; bake twice gives identical hashes; a car drives all 11 junctions with no seam spike > 0.3 g; kerbs keep ≥ 1.0 m from buildings | Per-node manual overrides in `roads-overrides.json`; flat ribbons as in JS for any node that still fails | 12 agent-days |
| **S5 owner-authoritative netcode + Steam SDR** | Online driving looks bad (rubber-banding, collisions), costs money, or Steam relay does not work for player hosting | 4 clients at 150 ms RTT / 2 % loss: remote error ≤ 0.5 m (far) and ≤ 1.0 m p95 (near), 0 teleports; host upload ≤ 200 KB/s at 16 players; integer rut stamps give identical tile hashes; you and a friend drive together over Steam for 15 min | Unity Relay (free tier) instead of Steam for tests; Mirror 96.x instead of NGO; own thin Steam transport | 8 agent-days |

Two integration checks follow: **I1** (engine-sim inside BRV, S1×S2) and **I2** (a BRV vehicle drives the baked canon junctions, S2×S3×S4). Then a **G1 review**: you play the lab builds (about an hour), and every failed gate either has its fallback adopted or comes to you as a decision.

### 0.2 Key choices in one screen

| Topic | Choice | Evidence |
|---|---|---|
| Repo | New private repo `kaiwren986/Backroads-Unity` at `D:\Backroads-Unity`. The three.js repo stays the frozen canon oracle | 03 §0, §3 |
| Unity | 6000.3 LTS (start on the installed 6000.3.20f1, update to 6000.3.26f1 before M1). Not 6000.5 (end of life). Move to 6.7 LTS after it has had 2–3 patches | 04 §1 |
| Render pipeline | URP 17.3, Forward+, GPU Resident Drawer, STP, SSAO, Decals, APV with sky occlusion | 04 §2, 07 §2 |
| Vehicle physics | Own "BRV" solver in C# + Burst: `fixedDeltaTime` 1/120, 4 substeps (480 Hz), 12 PGS iterations; PhysX only for the chassis against the world | 06 §0, §3.4 |
| engine-sim | Native DLL from unmodified upstream `85f7c3b` plus our plugin code; coupling mode D (Unity owns the crank) for BRV, mode A for simple race drivelines; audio rendered on the engine thread into a lock-free ring | 01 §0, §3.6, §4, §7 |
| Terrain | `WorldGrid` NativeArrays from the export are physics truth; custom chunk meshes render them | 03 §4.1, §4.3; 06 §5.2 |
| Roads | Own editor-time bake, `Backroads.Roads`, from the exported road graph; outputs meshes, colliders, terrain plates, lane graph, controls | 05 §0, §3 |
| Netcode | NGO 2.13.3, owner-authoritative vehicles, host-authoritative economy, Steam P2P over SDR via the Facepunch transport | 04 §3 |
| Art | FBX from scripted Blender 4.5 builds for new art; the frozen canon GLBs through glTFast 6.20.0; fictional brands only | 07 §0, §3, §9 |

---

## 1. Repository and project layout

### 1.1 New repo, not a folder

**Recommendation: a new repo `D:\Backroads-Unity`** (private GitHub `kaiwren986/Backroads-Unity`, which you create; agents commit locally and never push without your say-so).

Reasons:
- The JS repo is the **canon oracle** (03 §0.1–0.2). Keeping it small, frozen and separate makes it obvious that nothing in Unity can change canon by accident.
- The Unity project brings Library caches, Git LFS binaries (about 60 MB of canon layers, FBX, textures) and a C++ build tree. None of that belongs in the oracle.
- Synty files must stay out of git (07 §10.5); a fresh `.gitignore` can do that from the first commit.

The oracle is used through a **pinned, separate checkout**: a git worktree `D:\Backroads-oracle` of `D:\Backroads` at tag `oracle-v1`, created with your approval. Your own `D:\Backroads` working copy can keep changing without touching canon.

### 1.2 Tree

```
D:\Backroads-Unity\
  README.md                      how to build, play and run the gates
  .gitattributes                 LFS: *.f32 *.u8 *.bytes *.fbx *.blend *.png *.wav *.ogg *.exr
  .gitignore                     Game/Library Temp Logs UserSettings obj; Builds/ artifacts/ native/build*/;
                                 Game/Assets/ThirdParty/Synty/
  oracle.lock.json               { repo, worktree "D:\Backroads-oracle", commit, tag "oracle-v1",
                                   seeds {world 20251015, planner 20279734, props 4242} }
  CanonData/
    v1/                          the JS export, exactly as 03 §3.3 (manifest.json, terrain/*.f32|u8, world.json,
                                 placements.json, vegetation.json, traffic.json, textures/, reference/, source/)
      terrain/probes.json        1,000 heightAt/normalAt/surfaceAt probes + 12×4,096 camera-ray hits (§6.3 S3)
      traces/*.json              golden physics traces from the JS harness (03 §3.6)
    v1.1/
      roads-overrides.json       hand-edited road overrides (05 §3): R3 realignment, per-node controls, radii
      road-plates.bin            generated sparse terrain delta (05 §5.10), with its own hash
  Game/                          the Unity 6000.3 project
    Packages/manifest.json       pinned versions (§1.3)
    ProjectSettings/
    Assets/
      Backroads/<Subsystem>/{Runtime,Editor,Shaders}/   one assembly per subsystem (§1.4)
      Backroads/World/Generated/ canon import output (bytes + ScriptableObjects), committed, LFS
      Backroads/Roads/Baked/     road bake output, committed, hash-checked
      Art/                       Vehicles/<Id>/VH_*.fbx, Wheels/, Materials/, Shaders/, Textures/ (07 §4.1)
      Plugins/x86_64/EngineSimPlugin.dll   built by tools/ci/build-native.ps1, committed with
                                 EngineSimPlugin.version.json (source hash, ABI version)
      StreamingAssets/EngineSim/ es/** (piranha library), engines/<fictional id>.mr, ir/*.wav
      ThirdParty/Clipper2/, LibTessDotNet/, Facepunch.Steamworks/   vendored with licence files
      ThirdParty/Synty/          git-ignored; restored from synty-manifest.json
      Tests/                     EditMode, PlayMode and Perf test assemblies; lab scenes
  native/
    EngineSimPlugin/             C++ plugin, CMake (§1.5)
  art/
    vehicles/<id>/<id>.blend, brief.md, profile.json   Blender sources (LFS) (07 §4.1)
    trims/TS_Vehicle_A.blend
    pipeline/br_*.py             scripted Blender build (07 §5.1); vehicles.py copied in from the JS repo at 552f780
  tools/
    oracle/                      gen-hash.mjs, export-canon.mjs, record-traces.mjs (Node 24, Playwright + Edge)
    ci/                          *.ps1 batchmode wrappers (§1.7), env.ps1 (Unity and Blender paths)
  docs/
    design/                      this proposal and its successors
    adr/                         one file per architecture decision that changes a frozen contract
    gates/                       gate results committed after each review
  artifacts/                     git-ignored test output (gate JSON, logs, WAV taps, screenshots, contact sheets)
  Builds/                        git-ignored player builds
```

Reference photos never enter any repo: `D:\Backroads-ref\vehicle-refs\<id>\` (07 §8.3). Synty downloads live in `D:\SyntyPacks\` (07 §10.1).

### 1.3 Unity project settings and packages

**Editor:** `C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe` for M0 and the spikes; 6000.3.26f1 from M1 (decision D4; 04 §1.2).

**Project settings set in M0** (each has a check in the EditMode suite):
- Linear colour space; URP asset with the Universal Renderer on **Forward+**; SRP Batcher on; Graphics › BatchRendererGroup Variants **Keep All**; GPU Resident Drawer **Instanced Drawing** (04 §2.2).
- `Time.fixedDeltaTime = 1/120`, `Time.maximumDeltaTime = 0.05` (06 §3.4).
- Physics: `defaultSolverType = TemporalGaussSeidel`; layers `Static`, `Vehicle`, `VehicleHull`, `Wheel`, `Prop`, `TerrainProxy`, `Trigger`, with the 06 §5.2 matrix (`VehicleHull` × `TerrainProxy` **off**).
- Enter Play Mode: "Do not reload Domain or Scene" from day one, with static resets in `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (04 §1.2).
- Every mesh used by a `MeshCollider` is Read/Write; no dynamic batching; no `com.unity.serialization`; Burst never embedded (04 §1.2).

**`Game/Packages/manifest.json` (versions from 04 §5, checked 2026-10-18):**

| Package | Version | Used by |
|---|---|---|
| `com.unity.render-pipelines.universal`, `com.unity.shadergraph`, `com.unity.visualeffectgraph` | 17.3.0 (core) | rendering, VFX dust/mud |
| `com.unity.inputsystem` | 1.20.1 | input (03 §4.26) |
| `com.unity.burst` / `com.unity.collections` / `com.unity.mathematics` | 1.8.31 / 2.7.1 / 1.3.3 | BRV, WorldGrid, meshing |
| `com.unity.cloud.gltfast` | 6.20.0 (not 7.0.0-exp) | frozen canon GLBs (`buildings.glb`, `trees.glb`) |
| `com.unity.formats.fbx` | 5.1.6 | FBX round trips |
| `com.unity.splines` | 2.9.1 | editor gizmos for roads only (05 §0) |
| `com.unity.netcode.gameobjects` / `com.unity.transport` | 2.13.3 / 2.7.4 | netcode |
| `com.unity.multiplayer.playmode` / `com.unity.multiplayer.tools` | 2.0.2 / 2.2.12 | local multi-client tests, network simulator |
| `com.unity.dedicated-server` | 2.0.2 | headless server build profile |
| `com.unity.services.multiplayer` | 2.4.1 | Unity Relay for non-Steam test builds only |
| `com.community.netcode.transport.facepunch` | 2.0.0 (git URL `…/multiplayer-community-contributions.git?path=/Transports/com.community.netcode.transport.facepunch`) | NGO over Steam |
| `com.unity.memoryprofiler` | 1.1.12 | memory checks |
| `com.unity.test-framework`, `com.unity.test-framework.performance` | the versions Package Manager offers on 6000.3, recorded in the manifest at M0 | all automated gates |
| Not used | `com.unity.entities`, `com.unity.vehicles`, `WheelCollider`, Cinemachine (03 §4.19 says a straight camera port is simpler) | |

Vendored source (not packages): **Clipper2** C# (BSL-1.0), **LibTessDotNet 1.1.15** (SGI-B 2.0) (05 §0); **Facepunch.Steamworks 2.5.2** (MIT) (04 §3.7). Downloading these is a step you approve (D-list in §7.2).

### 1.4 Assemblies

One assembly definition per subsystem. References only point "down" the list, so each layer can be tested without the ones above it.

| Assembly (`Game/Assets/Backroads/…`) | Contents | References |
|---|---|---|
| `Backroads.Core` | `BrCoords` (03 §2), `GameLoop`, fixed-step clock, `IServiceRegistry`, settings, logging | — |
| `Backroads.Canon` | `WorldGrid`, `SurfaceTable`, `CanonManifest`, `WorldInfo`, `Placements`, `VegetationSet`, `LegacyTrafficNet` (golden `traffic.json`) | Core, Collections, Mathematics, Burst |
| `Backroads.Canon.Editor` | `CanonImporter` (03 §3.5), `ManifestVerifier`, `CanonSceneBuilder` | Canon |
| `Backroads.Deform` | integer `rut/brush/cutBlade/deposit*/relax` kernels on `DELTA_MM`, `RutStamp`, `StampLog`, `TileHash` (06 §5.6) | Canon |
| `Backroads.Terrain` | `TerrainChunkSystem`, `ChunkMeshJob`, LOD/skirts, dirty-rect consumer, `SG_Terrain` | Canon, Deform |
| `Backroads.Roads` / `.Editor` | runtime `RoadNetwork`, `LaneGraph`, `Controls`, `SignalController`; editor bake steps 1–12 (05 §3) | Canon |
| `Backroads.Environment` | buildings, typed props, lamps, trees, rocks, grass, water, boats, sky clock and lighting | Canon, Terrain |
| `Backroads.EngineSim` | P/Invoke `Native`, `EngineSimEngine`, audio filter/generator, `EngineDefinition`, loop banks, `EngineAudioDirector` | Core |
| `Backroads.Vehicles` | BRV: `VehicleDefinition`, `VehicleSimSystem`, `VehicleStepJob`, rows, tyres, shafts, suspension, hull, winch, hitches, `IVehicleEngine`, `TorqueMapEngine`, `VehicleVisual` | Core, Canon, Deform |
| `Backroads.EngineSim.Vehicles` | `EngineSimVehicleEngine : IVehicleEngine` (the only bridge, so BRV tests run without the DLL) | EngineSim, Vehicles |
| `Backroads.Traffic` | `TrafficSystem`, `AIDriver`, `JunctionArbiter`, roundabout gap logic, ghosts | Roads, Vehicles |
| `Backroads.Gameplay` | jobs/economy behind `IEconomyAuthority`, fuel, tow, wash, on-foot character, shovel, `SaveService` | Vehicles, Environment |
| `Backroads.CameraRig` | chase, hood and on-foot cameras (03 §4.19) | Vehicles, Canon |
| `Backroads.UI` | UI Toolkit HUD, minimap, big map, freight board, menus | Gameplay |
| `Backroads.Net` | NGO bootstrap, `TransportSelector`, `VehicleStateCodec`, `SnapshotBuffer`, `RemoteVehiclePuppet`, `HostValidator`, `StampReplicator`, `TrafficReplicator`, `EconomyRpc` | Vehicles, Deform, Traffic, Gameplay |
| `Backroads.Platform.Steam` | Facepunch.Steamworks init, lobby, invites, auth tickets (compiled only with `BR_STEAM`) | Net |
| `Backroads.Testing` | `AutotestRunner` (command line), `ScenarioRunner`, `MetricsRecorder`, `CameraBookmarks`, `ContactSheet`, `PlaytestChecklist` (F10 overlay) | all runtime |
| `Backroads.Build.Editor`, `Backroads.Art.Editor` | `BuildCli`; `BackroadsModelPostprocessor`, `VehiclePrefabBuilder`, `CaptureTurntable` (07 §3.3, §5.2) | |
| `Backroads.Tests.EditMode`, `.PlayMode`, `.Perf` | test assemblies | |

### 1.5 Native plugin source

```
native/EngineSimPlugin/
  CMakeLists.txt            our own (01 §1.4, App. A; 02 §1): targets scs, engine_sim_core, piranha*, es_script*,
                            EngineSimPlugin (SHARED), esp_tests, cosim_bench      (* only with -DESP_WITH_PIRANHA=ON)
  cmake/flags.cmake         /MT /O2 /fp:fast /DNDEBUG (01 §7.1); fir_avx2.cpp alone gets /arch:AVX2
  vendor/engine-sim/        upstream 85f7c3b, UNMODIFIED (git subtree), with submodules
                            simple-2d-constraint-solver@e009f4f and piranha@432f0b1; delta-studio and csv-io omitted
  vendor/LICENSES/          engine-sim MIT, scs MIT, piranha MIT (text from github.com/ange-yaghi/piranha, 01 §5.4)
  shim/delta-studio/include/yds_core.h                           empty (01 §1.3)
  shim/delta-studio/engines/basic/include/delta_basic_engine.h   empty
  generated/piranha/        scanner.auto.cpp, parser.auto.cpp, parser.auto.h: generated once (decision D3), committed
  src/
    EngineSimPlugin.h, esp_api.cpp      C ABI v1 (01 §7.2) plus the test additions in §3.2
    unity_sim.{h,cpp}                   UnitySim : PistonEngineSimulator: writeToSynthesizer() override without
                                        lastValveLift[8], attach(), delay-filter fix, synthesizer re-init (01 §7.3, §8)
    unity_transmission.h                clutch-torque accessor (01 §3.2)
    generators.h                        SlavedCrankPD, WheelLoad (01 App. A.4)
    instance.{h,cpp}                    worker thread, command and result SPSC rings, step accumulator, coast mode
    audio_render.{h,cpp}                renderSync (01 §4.3), SpscAudioRing, drift trim, underrun decay, WAV tap
    fir_avx2.cpp, fir_sse2.cpp          CPUID-dispatched FIR (01 App. A.5)
    ir_loader.cpp                       16-bit WAV reader + 44.1→48 kHz resampler
    script_loader.cpp                   piranha wrapper: absolute paths, compile mutex, output reset, wrapper .mr (01 §5.4)
    piranha_path_std.cpp                Boost-free piranha::Path (01 App. A.6)
    engines_cpp/ej25.cpp, ls_v8.cpp     C++ engine builders (02 §4; 01 App. A.1), always available
    offline.cpp                         esp_calibrate_inertia, esp_dyno_sweep, esp_render_offline
  tests/                    CTest executables (no googletest download): esp_smoke, esp_dyno, esp_ring_stress,
                            esp_cosim_matrix (re-creates 01 §3.5, whose probe lived in a temporary scratchpad),
                            esp_all_engines (piranha), plus an AddressSanitizer configuration (/fsanitize=address)
```

Only Windows x64 is built. Steam Deck runs the Windows build under Proton (04 §2.3), and the dedicated server never loads engine-sim (04 §3.7), so no Linux `.so` is needed.

### 1.6 Blender pipeline and data export tool

- **Blender:** `%LOCALAPPDATA%\Programs\Blender Foundation\Blender 4.5\blender.exe`, always `-b --factory-startup` (03 §4.28). The pipeline scripts of 07 §5.1 (`br_common`, `br_generate`, `br_uv`, `br_bake`, `br_lod`, `br_validate`, `br_export`, `br_build`) live in `art/pipeline/`. The JS repo's `tools/blender/*.py` stay untouched as the source of the frozen GLBs.
- **Canon export tool** (`tools/oracle/`):
  - `gen-hash.mjs`: imports `world.js` from the oracle worktree in Node 24, runs `generate(20251015)` (0.6 s), and checks the H, BASE, ROADD, MUD, WEAR and TINT hashes against `CanonData/v1/manifest.json`. It needs **no JS change** (03 §3.2 measured Node and Chromium as bit-identical).
  - `export-canon.mjs`: drives the installed Microsoft Edge with Playwright (`channel: 'msedge'`, `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`, project-local dev dependency) to run `index.html?export=unity` and write `CanonData/v1/` (03 §3.4). The fallback is the manual route: you open that URL and click **Save** (File System Access API).
  - `record-traces.mjs`: feeds scripted inputs through `G.update(1/120)` and writes the 03 §3.6 golden traces.
- The JS-side exporter is a **new file** `js/exportUnity.js` plus the three one-line exposures in `props.js` that 03 §3.4 lists. They go on branch `oracle-export` of the JS repo, then tag `oracle-v1`, **only with your approval (D2)**, and only after re-verifying every hash and count in 03 §3.2.

### 1.7 CI and batchmode scripts

CI is local. It runs on this machine and is driven by agents. Cloud CI would need Unity licence activation with your credentials, so it is out of scope. Every script exits non-zero on a failed hard gate and writes `artifacts/gates/<gate>.json`.

| Script | What it runs |
|---|---|
| `tools/ci/env.ps1` | `$UNITY = "C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe"`, `$BLENDER`, `$NODE`, the oracle path |
| `tools/ci/build-native.ps1 [-Piranha]` | `cmake -S native/EngineSimPlugin -B native/build -G "Visual Studio 17 2022" -A x64 [-DESP_WITH_PIRANHA=ON]`; `cmake --build native/build --config Release --parallel 16`; `ctest --test-dir native/build -C Release --output-on-failure`; copies the DLL and writes `EngineSimPlugin.version.json` |
| `tools/ci/unity-tests.ps1 -Platform EditMode` | `& $UNITY -batchmode -nographics -projectPath Game -runTests -testPlatform EditMode -testResults artifacts/editmode.xml -logFile artifacts/editmode.log` |
| `tools/ci/unity-tests.ps1 -Platform PlayMode` | the same with `-testPlatform PlayMode`, without `-nographics` when a test renders |
| `tools/ci/build-player.ps1 -Profile <name>` | `& $UNITY -batchmode -quit -projectPath Game -executeMethod Backroads.Build.Editor.BuildCli.Build -profile <name> -out Builds/<name>`. Profiles: `Lab-S1` … `Lab-S5`, `Client`, `Server` (Dedicated Server) |
| `tools/ci/autotest.ps1 -Exe <exe> -Scenario <id>` | `<exe> -screen-width 1920 -screen-height 1080 -autotest <id> -results artifacts/<id>.json -shots artifacts/shots/<id> -logFile artifacts/<id>.log`; waits, then checks the thresholds |
| `tools/ci/oracle-verify.ps1` | `node tools/oracle/gen-hash.mjs --oracle D:\Backroads-oracle` |
| `tools/ci/canon-export.ps1` / `canon-import.ps1` | the export (D2); then `-executeMethod Backroads.Canon.Editor.CanonImporter.ImportCli -canon CanonData/v1` |
| `tools/ci/roads-bake.ps1` | `-executeMethod Backroads.Roads.Editor.RoadBakeCli.Bake -verifyHash` |
| `tools/ci/blender-vehicle.ps1 -Id <id>` | `& $BLENDER -b art/vehicles/<id>/<id>.blend --factory-startup --python art/pipeline/br_build.py -- --id <id> --unity Game` (07 §5.1) |
| `tools/ci/gate.ps1 -Gate <S1…S5,I1,I2,M1…>` | runs everything a gate needs and writes the JSON plus a contact sheet PNG for you to look at |

Audio and screenshot gates run in a **normal windowed player**, not batchmode, so the real audio device and GPU are used. The agent launches it, and it plays through the laptop speakers for a few minutes. The plugin's WAV tap (§3.2) records what the engine produced, and the C# analysers (pitch by autocorrelation, RMS, clip count, as 02 §6.7 did in Python) run inside the test, so no extra tools are needed.

**For you as a non-developer:** every build has an **F10 Playtest Checklist** overlay. It lists that milestone's "you check" items with Yes / No / comment, and saves to `%USERPROFILE%\AppData\LocalLow\Backroads\Backroads\playtest\<build>.json`, which the agents read.

### 1.8 How the three.js build stays the canon oracle

1. **Generation is never ported.** A C# re-implementation would differ in the last float bit, shift the RNG stream and move every later tree (03 §0.1, §4.2). Unity only ever **imports** the exported final state.
2. **Pinned.** `oracle.lock.json` names the commit. `oracle-verify` runs on every CI pass in 0.6 s, and the importer refuses any file whose SHA-256 differs from the manifest (03 §3.5).
3. **Additive JS changes only:** the exporter file and the three exposures, hash-verified (03 §3.4). The frozen `buildings.glb` and `trees.glb` are hashed as well, because rebuilding `buildings.py` renumbers window panes and breaks canon lit flags (03 §3.3).
4. **Canon changes are versioned patches, never edits.** v1 is the export. v1.1 is the road-plates delta and the overrides from S4 (05 §3, §5.10), stored separately with its own hash and a test that **every vertex outside the road-plate mask is bit-identical to v1**.
5. **The oracle also supplies the test truth:** height probes, camera-ray hits, reference screenshots at 12 cameras × 10:00/21:00 (03 §3.6), golden physics traces, and deformation call logs.
6. **It stays playable** (`Play.bat`), so you can compare the browser game and the Unity build side by side.

---

## 2. Architecture

### 2.1 Subsystem map

```
             EDITOR / BAKE TIME                                    RUNTIME (client; the server strips render/audio/engine-sim)
 CanonData/v1 ──► CanonImporter ──► World/Generated (bytes, SOs) ──► WorldGrid (H, BASE, SURFACE, WEAR, ROADD, MUD, TINT,
      │                                                                 WET, COMPACT, DELTA_MM) ◄── Deform kernels ◄── RutStamps
      │                                                                    │  dirty rects                     ▲
      └──► RoadBake (steps 1-12) ──► Roads/Baked: meshes, colliders,       ├──► TerrainChunkSystem (render)    │
            ▲  roads-overrides.json   LaneGraph, Controls, RoadPlates ─────┤                                   │
            │                                                              ├──► Environment (buildings, trees, │
 art/*.blend ─► Blender br_build ─► FBX + vehicle.json ─► VehicleDefinition│     grass, lamps, water, sky)     │
 StreamingAssets/EngineSim/*.mr ─► EngineDefinition (+ TorqueMap, LoopBank)│                                   │
                                                                           ▼                                   │
   Input ─► VehicleControls ─► VehicleSimSystem (BRV, 120 Hz × 4) ──────────────────────────────── stamps ─────┘
                                   │  crank ω samples ▲ T_ind               │ pose, rpm, flags
                                   ▼                  │                     ├──► CameraRig, VehicleVisual, VFX, AudioDirector
                         EngineSimPlugin.dll (1 thread per full engine) ──► audio ring ──► Unity audio thread
   TrafficSystem (host) ─► AIDriver ─► VehicleControls (AI)  ──► LaneGraph / Controls
   Gameplay (jobs, fuel, save) ─► IEconomyAuthority (local or host)  ──► UI (HUD, maps)
   Net: VehicleSnapshot ⇄ RemoteVehiclePuppet; StampLog; TrafficReplicator; EconomyRpc; HostValidator
```

### 2.2 Subsystem boundaries and key types

| Subsystem | Owns | Key types | Talks to others only through |
|---|---|---|---|
| **Data import** | Verifying and converting `CanonData` | `CanonManifest`, `CanonImporter.ImportCli`, `WorldInfo` (POIs, roads, lots, rings, lamps, parking), `Placements`, `VegetationSet` | ScriptableObjects and `.bytes` assets in `World/Generated` |
| **WorldGrid** | The physics truth for ground | `WorldGrid` (NativeArrays in JS layout, flipped at the API: `HeightAt(Vector3)` = `HeightAtJs(-x, z)`, 03 §2); `SurfaceId`; `DirtyRectQueue` (`NativeQueue<int4>`) | Burst-safe static sampling functions; dirty rects |
| **Deformation** | All ground edits | `RutStamp` (16 B integer, 06 §5.6), `DeformKernels`, `StampLog`, `TileHash` (xxHash32 per 32×32 tile) | stamps in, dirty rects out |
| **Terrain rendering** | Chunk meshes and the terrain shader | `TerrainChunkSystem` (256 chunks of 64 m, LOD steps 1/2/4/8, skirts, 03 §4.3), `ChunkMeshJob` (`Mesh.MeshDataArray`), `SG_Terrain` | reads WorldGrid, consumes dirty rects |
| **Roads** | Every road surface, kerb, marking, lane and control | `RoadNetwork`, `RoadEdge`, `RoadNode`, `Arm`, `Corner`, `CrossSection`, `Lane`, `Connector`, `Conflict`, `Control` (05 §4) | baked assets; `RoadPlates` delta into WorldGrid at load; `LaneGraph` for traffic |
| **Environment** | Static world and time of day | `BuildingSpawner` (material instances for colours; window lit bits in the 6.3 Renderer Shader User Value), `TreeSet` (LODGroup prefabs), `GrassTiles` (`RenderMeshIndirect`, per-tile `mulberry32(tileKey·7919+13)` exactly as JS, 03 §4.10), `DayNightClock` (sky.js curves, 03 §4.11) | reads WorldGrid and Placements |
| **BRV vehicles** | Chassis, wheels, shafts, tyres, hulls, winch, hitches | `VehicleDefinition`, `VehicleSetup`, `VehicleControls` (struct), `VehicleSimSystem`, `VehicleStepJob`, `Row`, `ShaftGraph`, `TyreModel` (MF secant + soft soil), `IVehicleEngine` (06 §3, §4) | `VehicleControls` in; `VehicleStatus` and stamps out; `IVehicleEngine` |
| **engine-sim** | Combustion, crank dynamics, engine sound | `EngineSimEngine` (`Kick`/`Collect`), `EngineDefinition`, `EngineAudioDirector` (R0–R3 tiers), `TorqueMap`, `LoopBank` | `IVehicleEngine` (via the bridge assembly); its own audio ring |
| **AI traffic** | AI drivers, junction arbitration, ghosts, fleet rebalance | `TrafficSystem`, `AIDriver` (pure pursuit, IDM), `JunctionArbiter` (from `Conflict.rule`), `GhostCar` (03 §4.21) | writes `VehicleControls` for AI vehicles; reads `LaneGraph` |
| **Jobs and economy** | Boards, cargo, pay, fuel, tow, wash | `JobSystem`, `CargoTable`, `IEconomyAuthority` (`RequestLoad`, `RequestDeliver`, `RequestRefuel`, `RequestTow`) (03 §4.22) | requests only; the authority is local in single-player and the host online |
| **UI** | HUD, maps, menus | UI Toolkit (UXML/USS), `HudPresenter`, `MinimapView` (map.png), `FreightBoardView` (03 §4.23) | read-only view models |
| **Camera** | Chase, hood, foot | `ChaseCamera`, `HoodCamera`, `FootCamera` (03 §4.19) | vehicle pose, WorldGrid ray |
| **Netcode** | Sessions and replication | `NetBootstrap`, `VehicleSnapshot` (40 B, §5.2), `SnapshotBuffer`, `RemoteVehiclePuppet`, `ContactImpulseRelay`, `HostValidator`, `StampReplicator`, `TrafficReplicator` | NGO custom messages and RPCs |
| **Save and progression** | Profile and world progress | `SaveService` (`save.json`, versioned, 03 §4.25), `ProgressStore` (per server, keyed by SteamID), optional sparse deformation deltas | Gameplay events |

**WorldGrid detail that keeps the terrain identical online.** 06 §5.2 proposes `DELTA_MM` relative to BASE, but canon H already differs from BASE at the 2,144 quarry-pile vertices (03 §3.2). Quantising those would break bit-identity. So: **`H = H_canon + DELTA_MM · 0.001`, with `DELTA_MM` (int16) relative to the canon heights**. The integer kernels work on `Hmm0 = round(H_canon · 1000)` (int32, computed once at import; IEEE-deterministic) plus `DELTA_MM`. Where nothing was deformed, H is the canon float exactly.

### 2.3 Threads and the per-step data flow

| Thread | Work |
|---|---|
| Main | `FixedUpdate` orchestration at 120 Hz; `Update`/`LateUpdate`: interpolation, camera, terrain dirty flush (≤ 2 chunk rebuilds per frame), grass tiles, VFX, HUD, day/night |
| Unity job workers (count = cores − 2 − native engine threads, via `JobsUtility.JobWorkerCount`) | `WheelQueries`, `VehicleStepJob`, `ChunkMeshJob`, deformation kernels, grass and tree culling |
| One native thread per full engine (local R0 plus 0–4 remote R1) | engine-sim steps plus synchronous audio render (01 §4.2) |
| Unity audio thread | `esp_read_audio` from `OnAudioFilterRead` or an `IAudioGenerator` (lock-free, no allocation) |

**Per fixed step (1/120 s):**
1. `Net.ReceiveAll()`: remote snapshots go to puppets (kinematic `MovePosition`/`MoveRotation`); queue incoming contact impulses and stamps.
2. `EngineSim.Collect()` for each local engine, never blocking (`esp_step_end(timeout 0)`). After 4 stale steps, `TorqueMap` takes over (06 §4.4.3).
3. `WheelQueries.Schedule()`: WorldGrid sampling plus `SpherecastCommand` against `Static`; complete.
4. `VehicleStepJob`: 4 substeps × 12 PGS iterations. Outputs: net impulses `dP`/`dL`, 5 crank-ω samples, stamps (06 §3.8).
5. Apply the impulses to Rigidbodies (`useGravity = false`, because BRV owns gravity). `EngineSim.Kick(crankSamples, pedal, ignition)`.
6. `Deform.Apply(stamps)` with the integer kernels; dirty rects queued.
7. Traffic decisions (host only) at 60 Hz.
8. PhysX simulates chassis against buildings, kerbs, props and puppets.
9. Every 6th step (20 Hz): `Net.SendOwnedSnapshots()`. Every 30th step: stamp batch (06 §5.6).

### 2.4 Contracts frozen at the G1 review

Changing any of these after G1 needs an ADR in `docs/adr/`:
- the `EngineSimPlugin.h` ABI v1;
- `IVehicleEngine`, `VehicleControls`, `VehicleStatus`;
- the `WorldGrid` public API and layer list;
- the `CanonData` v1 format;
- the `RoadNetwork`/`LaneGraph` asset schema;
- `VehicleSnapshot` and `RutStamp`;
- `IEconomyAuthority`.

### 2.5 Where the reports disagree, and what this proposal picks

| Question | Options in the reports | Pick | Why |
|---|---|---|---|
| Ship piranha (`.mr` scripts)? | 01 §5.4: yes, MIT, generate the parser once. 02 §9: no, use JSON/C++ ports | **Yes if D3 is approved**, with C++ builders always available | 27 engines with zero porting errors; S1 is not blocked either way |
| Fork engine-sim? | 01 §8: not for v1. 02 §9, 06 §4.8: fork | **Unmodified vendor tree for v1**; fork only for diesel, turbo and engine brake (M9) | Every defect that matters has a plugin-side workaround that 01 tested |
| Local engine frequency | 02 §6.3: 20 kHz. 01 §6.2: frequency is part of the tuning | **Each engine's authored `simulation_frequency`**; quality tiers cut fluid substeps 8→4 | 01 measured −35 % (LS) and 02 +29 % (EJ25) at 5 kHz: the sign depends on the engine |
| Remote engines | 01 §6.3: FOLLOW_RPM (dyno hold). 06 §4.6: same slaved PD | **FOLLOW_RPM by default**; PD variant behind a flag, chosen by the S1 listening test | Simpler; remote idle being slightly "too smooth" (06 §4.3 C) is acceptable |
| Audio read | 06 §4.4.6: `readAudioOutput` in `OnAudioFilterRead`. 01 §4.1: it locks and can block | **01's renderSync plus SPSC float ring** | Nothing blocks on the audio thread |
| ABI name | 06 `bre_*`; 01 `esp_*` with all coupling modes | **`esp_*` v1** | Superset |
| Physics rate | 04 §3.2: 1/60–1/100. 03, 06: 1/120 (+4 substeps) | **1/120 × 4** | The JS tuning was done at 120 Hz; stiff rows need 480 Hz |
| Transport format | 03 §4.17: glTFast. 07 §3: FBX | **FBX for all new art; glTFast for the frozen canon GLBs** | Keeps pane names and order for canon lit flags; FBX gives LOD groups and material remap |
| Per-building colours | 03 §4.7: MaterialPropertyBlock. 04 §2.2, 07 §6.1: MPB breaks the Resident Drawer | **Material instances for colours; lit-window bits in the Renderer Shader User Value** | The largest pane count is 22 (House_B), so the bits fit in 32 |
| Damage dents | 06 §6.4: blendshapes. 07 §6.7: avoid them (SkinnedMeshRenderer) | **Burst vertex deformation of a readable mesh** | Stays Resident-Drawer friendly |
| Deformation parity | 03 §4.4: byte-for-byte with JS floats. 06 §5.6: integer fixed point | **Integer kernel**; JS parity within ≤ 2 mm per call sequence | Bit-exact on every client is worth more than bit-exact with JS |
| Major road at junctions | trafficNet: straightest pair. 05 §5.1: class first | **Class first** (fixes nodes 2 and 5); `traffic.json` stays golden for unchanged junctions | MUTCD §2B.07 (05 §0) |
| Terrain renderer | 03 §4.3: custom chunks. 04 §2.2: Terrain Lit | **Custom chunks**; Unity Terrain only measured in S3 | Bit-exact vertices; physics never depends on it (06 §5.1) |

---

## 3. The engine-sim plan

### 3.1 Building the native plugin, and the piranha / flex-bison question

- **Core:** the 41 core `.cpp` files plus 29 scs files, compiled from the untouched tree with our CMake. Two empty shim headers satisfy the single unused delta include (`synthesizer.cpp:4`). 01 §1.3 built this with 0 errors and 0 warnings and **no patched source**. 02 patched a copy instead; we use the shim route.
- **Flags:** `/MT` (no redistributable), always `NDEBUG` (the Debug build cannot compile because of the stale `m_antialiasingFilters` assert, 02 §2 item 5), `/fp:fast` (about 10 % faster with unchanged torque, 02 §6.6), and AVX2 only for the FIR kernel, with CPUID dispatch to SSE2 (01 §7.1).
- **piranha needs flex and bison at build time. No generated parser is committed upstream** (01 §5.4, 02 §3). The plan:
  1. Boost is replaced by `piranha_path_std.cpp` (01 compiled all of piranha except `ir_compilation_unit.cpp` with MSVC this way).
  2. **With your approval (D3)**, an agent downloads the portable **win_flex_bison 2.5.25** zip (win_flex 2.6.4, win_bison 3.8.2) into `D:\Backroads-Unity\tools\winflexbison\`. Nothing is installed system-wide. It runs once, and `scanner.auto.cpp`, `parser.auto.cpp` and `parser.auto.h` are committed under `native/EngineSimPlugin/generated/piranha/` with the tool's hash recorded. After that, builds never need flex or bison.
  3. **Licence:** piranha at `432f0b1` has no LICENSE file; the GitHub repo now shows MIT (01 §5.4). We ship that text in the third-party notices.
  4. **Runtime loading** (01 §5.4): our own `piranha::Compiler` wrapper with an absolute search path to `StreamingAssets/EngineSim/es`; reset `es_script::Compiler::output()` before each compile; a global compile mutex; and a tiny wrapper `.mr` (`import "engine_sim.mr"`, the engine file, then `main()` or `set_engine(<node>())` for the older files that have no `main`).
- **Without D3:** the C++ builders (EJ25 from 02 §4, LS V8 from 01 App. A) carry S1. Each further engine is a 1–3 hour C++ port (01 §5.3), or a JSON engine format later (02 §9).
- **Shipped engine files get fictional ids** at build time (for example `v8_flatplane_a.mr`); real engine names stay only in the vendor tree, because file names in a build are visible to players (07 §4.1, §9).

### 3.2 ABI

ABI v1 is exactly 01 §7.2:
- `extern "C"`, `__cdecl`, every call returns `int32_t` status, and C++ exceptions are caught inside;
- blittable structs that start with `struct_size`;
- an opaque `EspEngine*`;
- the modes `FREE`, `PROXY` (A/B), `KINEMATIC` (C), `SLAVED_CRANK` (D) and `FOLLOW_RPM`;
- `esp_step` / `esp_step_begin` / `esp_step_end`, `esp_read_audio` (audio thread only, lock-free), and the offline `esp_calibrate_inertia`, `esp_dyno_sweep`, `esp_render_offline`.

Three test-only additions:
- `esp_debug_tap(EspEngine*, const char* wavPath, double seconds)`: tees the ring into a WAV file for automated analysis;
- `esp_get_stats(EspEngine*, EspStats*)`: steps run, coast events, worker CPU, P/E-core residency;
- `esp_set_coast(EspEngine*, int maxMs)`: see §3.3.

The C# side checks `esp_version() == 1` at start. On a mismatch or a missing DLL, the vehicle silently uses `TorqueMapEngine` and the game logs an error, so a broken plugin never stops the game. Agents rebuild the DLL between batchmode runs, which are fresh processes, so the Editor's DLL lock (01 §7.4) only affects interactive sessions. For those, use the shadow-copy loader or restart the Editor.

### 3.3 Threading

- **One dedicated native worker per full-simulation engine.** Engines are independent apart from four shared statics, and each has a plugin-side fix (01 §6.1):
  - `lastValveLift[8]`: removed in our override;
  - `Function::DefaultGaussianFilter`: one `Function` is constructed on the main thread at plugin init;
  - `Compiler::s_output`: compile mutex;
  - `rand()`: per thread in the UCRT.
- **Pipelined, never blocking.** At the end of fixed step k, `Kick` pushes the 5 crank-ω boundary samples, the pedal and the ignition. At the start of step k+1, `Collect` polls. The torque arrives with one step of lag, which 06 §4.5 and 01 §3.5 show is stable (loop gain ≈ 0.04).
- **Exact time.** We use a fractional step accumulator, `setTargetSynthesizerLatency(0)`, one `endInputBlock()` at init, rendering **before** `endFrame()`, and `transmission->update(0)` after every control change. Engine time then equals physics time step for step (01 §3.3, §8 items 5–6).
- **Coast mode (new, validated in S1).** During a main-thread hitch, `FixedUpdate` stops kicking, so the audio ring would drain in about 50 ms. If no command arrives for 2 step periods, the worker keeps stepping on its own clock with the last command (for up to `maxMs` = 300), so the sound continues. When commands resume, it discards the stale ones and resets the PD integral.
- **Scheduling.** 02 §6.4 saw Windows put unfocused engine threads on E-cores. S1 records P/E residency with `esp_get_stats`, and tries a raised thread priority and opting the threads out of power throttling. The budget uses E-core numbers until measured otherwise.

### 3.4 Coupling choice

| Vehicle | Mode | Details |
|---|---|---|
| All BRV vehicles with diffs, transfer cases, trailers (pickups, trucks, 6x6, dozer) | **D, SLAVED_CRANK** (01 §3.6, 06 §4.4) | engine-sim in neutral, clutch limits 0, `m_vehicleDrag` removed; `SlavedCrankPD` with `f_c = min(8 Hz, f_firing,idle/4)`, `Kp = J(2πf_c)²`, `Kd = 1.4·(2πf_c)·J`, `|T| ≤ 4×` peak; returns `T_ind = J_es·Δω/h − mean(T_pd)`; `J_es` calibrated at load (LS: 0.4570 vs 0.4537 kg·m², 01 §3.5); Unity-side fuel cut at redline + 150 rpm as the limiter backstop |
| Race cars with one driven axle and a spool or locked diff, if D's limiter lag is audible or felt | **A, PROXY** | The plugin owns crank, clutch and driven-wheel spin against the per-step tyre law; fork-join inside `FixedUpdate`; the most accurate mode (Δv ≤ 0.06 m/s, 4 rpm in cruise, 01 §3.5) |
| Third-party physics, or a fallback | **C, KINEMATIC** | Unity imposes driveline speed; the plugin returns clutch torque |
| Remote players' cars and near AI | **FOLLOW_RPM** | dyno hold at the replicated rpm; sound only |

The mode is a field on `VehicleDefinition` (`CouplingMode`), so it can change per vehicle without code changes. The known weak spot of D: on the rev limiter it deviated 270–350 rpm from the monolithic reference because of the 1-step torque lag (01 §3.5). The backstop, running at 120 Hz, and an optional `∂T/∂ω` feed-forward are the mitigations, and gate S1-G6 and the I1 check measure it.

### 3.5 Audio path

1. **Never start engine-sim's synthesizer thread.** After each batch of steps, the engine worker renders synchronously (`renderSync`, 01 §4.3) into a **lock-free SPSC float ring** of 16,384 frames (340 ms at 48 kHz).
2. **Re-initialise the synthesizer at `AudioSettings.outputSampleRate`** (normally 48 kHz). Resample each impulse response once at load, and re-initialise the exhaust delay filters at the engine's own frequency (01 §4.2, §8 items 7–8).
3. **AVX2 FIR:** 3.92 → 0.51 µs per sample at 10,000 taps, from 17.3 % of a core per channel down to 2.25 % (01 §4.2, §6.2). A partitioned FFT convolution can come later.
4. **Drift trim** of ±0.5 % through `setInputSampleRate`, driven by the ring fill level (≈ ±8.6 cents, inaudible). `volume ≈ 0.5` stops the int16 clipping that 02 §6.7 measured. Underruns hold and decay over about 2 ms.
5. **Unity side, two implementations, both tried in S1:**
   - `EngineSimAudioFilter` (`OnAudioFilterRead`): the v1 path for the local car. Whether 3D attenuation applies to filter-generated audio is undocumented (01 §4.5). If it does not, use the looping DC-clip trick.
   - `EngineSimAudioGenerator` (6.3 `IAudioGenerator` with a Burst `[DllImport]` into `esp_read_audio`): the **preferred** path once confirmed, because it is a real `AudioSource` input with 3D, Doppler and the mixer. 01 §7.4 leaves open whether `ChannelBuffer` exposes a pointer; S1 answers it.
6. **Mixer:** `Engine/Local`, `Engine/Remote`, `Vehicle/Tyres`, `Ambience`, `UI`. Non-engine sounds (tyres, gravel, wind, horn, impacts) are looping AudioSources with CC0 samples or ported WebAudio synths, driven by the same state values (03 §4.24).

### 3.6 Remote and AI engines

| Tier (01 §6.3) | Who | Method | CPU |
|---|---|---|---|
| R0 | you | full engine, mode D or A, IR audio | 0.25–0.6 core |
| R1 | the nearest audible remote players, capped by tier (Ultra 4, High 2–3, Low 1; §3.8) | FOLLOW_RPM: dyno hold at the replicated rpm, replicated pedal, ignition and starter; 4 fluid substeps; AVX2 FIR; 3D | ≈ 0.25 core each (V8), less for I4/I6 |
| R2 | further players and all AI traffic | **baked loops**: at build time `esp_render_offline` renders 1–2 s steady loops on an rpm grid (every 250–500 rpm × {closed, 50 %, WOT}); at runtime a granular crossfade player is driven by rpm and load. AI physics uses `TorqueMapEngine` from `esp_dyno_sweep` (06 §4.7) | < 0.5 % for dozens of cars; 1–3 MB per engine as Vorbis |
| R3 | far away | culled or one drone layer | ~0 |

engine-sim is not deterministic (`rand()`), so the network carries **state, not inputs**: rpm u16, throttle u8, load u8 and a flags byte, inside the vehicle snapshot (01 §6.3, §5.2 below). The dedicated server never runs engine-sim (01 §6.3, 04 §3.7). Loop banks are baked by `Backroads.EngineSim.Editor.EngineBakeMenu` in batchmode and committed per engine.

### 3.7 Engine roster and the diesel/turbo gap

**The gap.** engine-sim has no compression ignition, no injection control, no turbocharger and no engine brake (01 §5.3, 06 §2 item 8). Every truck engine would sound like a petrol engine. Be clear with players about what is simulated:

| Vehicle (fictional id) | Engine (upstream source → shipped id) | Honest status |
|---|---|---|
| Hatch `VH_Hatch_A` | `05_honda_vtec` → `i4_vtec_a` | fully simulated |
| Rally coupe `VH_Rally_CoupeA` | `01_subaru_ej25_eh` (C++ port exists, 02 §4) → `f4_boxer_a` | fully simulated |
| Pickup `VH_Pickup_MidA` | `07_gm_ls` or `04_60_degree_v6` → `v8_cross_a` / `v6_60_a` | fully simulated |
| Box truck `VH_Truck_Box75` | `chev_truck_454` → `v8_bigblock_a` | **fully simulated and realistic**: gas medium-duty trucks exist (06 §4.8) |
| Semi `VH_Semi_6x4A`, 6x6 `VH_Offroad_6x6A` | **diesel stand-in**: a long-stroke inline-6 built from the `03_2jz`/`M52B28` archetype with ~100 × 120 mm bore and stroke, high compression, `molecular_afr` 17.75, `energy_density` 45.5 kJ/g, redline 2,500–3,200, governor throttle, harsh IR (01 §5.3). Or `11_merlin_v12` at about 3,000 rpm (06 §4.8) | simulated petrol-cycle stand-in, with sampled turbo whistle and injector tick layers driven by load and a spool estimate; Jake brake done in Unity as retard torque `P_jake(rpm)/ω` (300–450 kW for 13–15 L) plus a bark sample (06 §4.8 item 8) |
| GT `VH_GT_A` | `08_ferrari_f136_v8` → `v8_flatplane_a` | fully simulated |
| Trophy truck `VH_Trophy_A` | `07_gm_ls` | fully simulated |
| Hypercar (later) | `10_lfa_v10` → `v10_a` (needs our `lastValveLift` override, already in v1) | fully simulated |

**Calibration.** Every engine gets a dyno-vs-spec pass with `esp_dyno_sweep`, 0.5–2 days each (01 §5.3). The LS rebuild gives 715 N·m at 4,000 rpm, well above a production 5.3 L (≈ 450 N·m). The piranha run (D3) settles whether the script or the C++ port is off (01 §9 item 2).

**M9 (optional) closes the gap** with an engine-sim fork:
- forced induction: boost pressure upstream of the intake, with a spool lag from exhaust flow and a wastegate clamp (06 §4.8 item 6);
- a compression-release engine brake (item 8);
- then compression ignition, which is research measured in weeks (item 7).

The third-party fork `kyan0s-git/engine-sim` is reference reading only, after a licence and code review. It is not a dependency (01 §5.3).

### 3.8 CPU budget and quality tiers

Measured on this machine, single thread, fast build (01 §6.2; 02 §6.3–6.5):
- V8 at 10 kHz/8: 34.7 % of a core; 2.9 ms of worker time per 120 Hz step.
- EJ25 at 20 kHz/8 with IR audio: 0.43 core.
- 1 local + 23 remotes at 10k/4: 4.4 cores.

| Engine quality | Local engine | R1 cap | Use |
|---|---|---|---|
| Ultra | authored frequency, 8 fluid substeps, full IR (≤ 10,000 taps) | 4 | this laptop |
| High (default) | authored frequency, 4 substeps (torque within 1 %, 01 §6.2) | 2–3 | mid-range PCs |
| Low | authored frequency, 4 substeps, IR clipped to ≤ 2,000 taps | 1 | 6-core / Steam Deck |

Never lower the simulation frequency as a quality knob: it changes the engine (01 §6.2).

---

## 4. Graphics in URP and the Blender vehicle pipeline

### 4.1 URP setup for "pretty good graphics"

The target look is **"Synty-plus"**: a clean, low-frequency stylised world with more detailed, smoothly shaded hero vehicles, where mud and wetness carry the surface detail (07 §2).

| Feature | Setting | Evidence |
|---|---|---|
| Renderer | Forward+ (no 8-lights-per-object limit: headlights, indicators and 78 real lamp lights at night) | 04 §2.2, 07 §2 |
| Draw calls | GPU Resident Drawer + GPU occlusion culling; MeshRenderers only; **no MaterialPropertyBlocks**; per-renderer data through the 6.3 Renderer Shader User Value | 04 §2.2 |
| Shadows | main light, 4 cascades, 2048, 250 m on High (tiers in 04 §2.3); shadows only on the player's headlights | 04 §2.3, 07 §2 |
| AO | SSAO renderer feature (GTAO arrives with 6.7) | 04 §2.2 |
| Reflections | **a realtime reflection probe that follows the player car** (128 px, time-sliced, refreshed by script), the biggest single win for car paint; box-projected baked probes in town | 07 §2 |
| Indirect light | APV with sky occlusion, baked once for the frozen town and island; ambient gradient driven by the day/night clock; optional day/dusk/night scenario blending | 04 §2.2 |
| Sky and day/night | port of the sky.js clock and curves (sun path, night factor, fog colour table, exposure 0.95 + 0.45·night); procedural sky shader; moon light swap | 03 §4.11 |
| Post | Bloom (6.3 Kawase/Dual), ACES or Neutral tonemapping, Split Toning + Hue-vs-Sat + Channel Mixer reproducing the JS `GradeShader`, vignette; motion blur off by default; UI drawn after post | 03 §4.12, 04 §2.2 |
| Upscaling | STP (needs TAA); 0.7 render scale on Steam Deck | 04 §2.2 |
| Decals | URP Decal feature (DBuffer) for skid marks, mud splats, oil and patch repairs only. Road lines are **geometry** | 05 §5.8 |
| Volumetrics | optional `com.cqf.urpvolumetricfog` (MIT, OpenUPM) on High; re-test it on 6.7 | 04 §2.2 |

### 4.2 World look

| Element | Plan | Evidence |
|---|---|---|
| Terrain | Port the two-surface shader: per-vertex colour/colour2/blend/wear/wet/kind attributes packed into `Color` + `TEXCOORD0–3`; detail-noise sampling at `float2(-x, z)` for exact pattern parity; crisp `cel` steps; per-kind tones (grass, pebbles, gravel, 4 m concrete slabs, rock strata), mud swirls, puddles on flat mud, tyre streaks | 03 §4.3 |
| Roads | Baked meshes with four materials (Asphalt, Concrete, Markings, Gravel); world-planar asphalt UVs (4 m tile); vertex colour R = traffic wear baked from the lane graph, G = wetness; markings are draped geometry 4 mm above the surface with `Offset -1,-1`; LOD1 at 250–600 m with 2× wider lines | 05 §5.8–5.9, §5.13 |
| Buildings | Frozen `buildings.glb` through glTFast. A post-import step merges each model's `Glass` panes into one mesh with a per-pane ID in vertex colour, resolved **by pane name** against `placements.json`. Colours are material instances | 03 §3.3, §4.7; 07 §6.8 |
| Trees | 5,265 GameObjects with LODGroup from `trees.glb`; vertex-colour Shader Graph with the JS wind formula; LOD cross-fade | 03 §4.8, 07 §6.8 |
| Grass | Burst tile builder ported 1:1 (deterministic per tile), `Graphics.RenderMeshIndirect` with a compute cull | 03 §4.10, 04 §2.2 |
| Water | URP water shader with depth fade on the y = 0 plane; underwater tint already lives in the terrain colours | 03 §4.9 |
| Particles | VFX Graph for dust, clods, smoke, mud and splash, using the JS emission rules | 03 §4.13 |

### 4.3 Vehicle shaders (07 §6)

- `SG_CarPaint`: clear coat, 3 paint zones from the MASK, flakes, a livery in UV1, mud and damage masks.
- `SG_VehicleTrim`: the shared 2048 trim sheet with a palette strip.
- `SG_VehicleGlass`: transparent, preserve specular, wiper mask.
- `SG_VehicleLights`: one material for all lamps; vertex channels R head/DRL, G tail/brake, B indicator, A reverse; HDR intensities so Bloom picks them up.
- `SG_MudLayer`: a 1:1 port of `dirtify()` (07 §6.5), used by vehicles, terrain props and Synty override materials (`SG_BR_SyntyLit`).
- Per-car colours are material instances. Damage is Burst vertex deformation plus `DMG_*` detachables on breakable joints (07 §6.7).

### 4.4 Blender vehicle pipeline (07 §3–§8)

```
art/vehicles/<id>/profile.json + brief.md ─► br_generate (cage + parts, sockets) ─► <id>.blend
   ─► [you: 1–3 h hand tuning, move vertices only] ─► br_build: validate ─► hi-poly ─► LOD0..3 ─► UVs ─► bake (Cycles, OptiX)
   ─► FBX + GLB + <id>.vehicle.json + PNGs ─► Game/Assets/Art/Vehicles/<Id>/ ─► BackroadsModelPostprocessor
   ─► VehiclePrefabBuilder (generated base prefab + your Prefab Variant) ─► review sheets
```

- **Transport:** FBX with `apply_scale_options='FBX_SCALE_ALL'`, `axis_forward='-Z'`, `axis_up='Y'`, `bake_space_transform=True`, triangulated before the bake, `use_custom_props=True` (07 §3.2). The **axis canary** asset (`MARK_Front` / `MARK_Left` / `MARK_Up`) runs in every build with an EditMode assertion (07 §3.2).
- **Naming:** `Body_LOD0..3`, `SOCKET_Wheel_*`, `SOCKET_Light_*`, `SOCKET_Exhaust_n`, `SOCKET_Hitch`, `PART_*` with hinge props, `DMG_*`, `UCX_*` convex hulls; wheels as separate `WH_*` assets (07 §4.2).
- **The `vehicle.json` sidecar** becomes `VehicleDefinition` (wheel sockets, radii, bounds), so physics and mesh can never drift apart (07 §4.4). Tuning tables (06 §7) live in a companion `VehicleTuning` asset.
- **Budgets and gates** come from 07 §8.4: for example a pickup at 70–100k triangles at LOD0, ≤ 9 materials, at least 400 px/m. `br_validate` fails the build when they are broken.
- **Review loop for a non-artist:** a 6-view EEVEE sheet, an ortho side view over your reference photo at 50 % opacity, and a Unity batchmode turntable at noon, dusk and night with mud 0 / 0.5 / 1 (07 §5.2). You comment ("hood higher"), the agent edits the profile and rebuilds.

### 4.5 Which vehicles first

The first vehicle through each stage is the one that retires the most risk for the class behind it.

| Order | Vehicle | Why this order | Milestone |
|---|---|---|---|
| 0 | Existing hatch, pickup, rally, truck and dozer GLBs (glTFast) | No art work before the gates; the existing pickup is the S2/M1 test car | M1 |
| 1 | **Mid-size double-cab pickup** `VH_Pickup_MidA` (Ranger/Hilux class proportions, 06 §7.1) | Pipeline pilot (07 phase E); it is the player's default car; boxy enough to generate well (07 §8.1) | M2 (art track) |
| 2 | **Box truck** `VH_Truck_Box75` (NPR/F-650 class) | Highest procedural success (07 §8.1); its gas big-block V8 is fully and honestly simulated (06 §4.8); proves modular bodies | M6 |
| 3 | **Semi 6x4 + 53 ft trailer** | The hardest physics: kingpin joint, 18-speed gearbox, inter-axle diff, 30:1 mass and inertia ratios (06 §6.1, §9 items 4–5) | M6 |
| 4 | **6x6 offroad truck** (Ural/Tatra/Zetros class) | The mud showcase: CTIS, lockers, full-time 4WD (06 §5.3) | M6 |
| 5 | **Rally coupe** (GR86/GR Yaris Rally2 class) | The first race car: the EJ25 already exists (02 §4), gravel tyres, sequential box, liveries | M7 |
| 6 | **GT3-inspired car** | Aero maps, slicks, tyre temperature, setup sheet (06 §6.3); the least procedural body (07 §8.1), so the livery does much of the work | M7 |
| 7 | **Trophy truck** | Long travel, progressive bypass dampers; offroad racing in Motor Town style | M7 |

**Synty vehicles** (Town, City, Pro Racer) may stand in as traffic or race grids. Agents only place them by prefab path and never open the files (07 §10.4).

### 4.6 Synty and legal rules

- **Synty:**
  - You download the packs yourself to `D:\SyntyPacks\`; no agent uses your login (07 §10.1).
  - Buy one-time licences, not SyntyPass (07 §10.4).
  - `Assets/ThirdParty/Synty/` is git-ignored and listed in `synty-manifest.json` (07 §10.5).
  - Agents never read, convert or reshape Synty meshes or textures (the generative-AI clause, 07 §10.4).
  - Canon footprints stay authoritative; Synty pieces must fit the lots (07 §10.6).
- **Vehicles:**
  - Fictional brands and names everywhere, including file names. Rename "Ranger 4x4" and "Muncher D3" now (07 §9).
  - Three or more inspirations per design, no badges, no signature grilles or silhouettes, and a `brief.md` for each vehicle recording its inspirations and deviations.

---

## 5. Online plan

### 5.1 Stack

| Layer | Choice | Evidence |
|---|---|---|
| Netcode | NGO 2.13.3 (3.x after the 6.7 upgrade); custom messages, not stock `NetworkTransform` | 04 §3.4 |
| Player-hosted transport | Steam networking sockets P2P via the community Facepunch transport 2.0.0 + Facepunch.Steamworks 2.5.2. Valve relays automatically over **SDR**, which hides IPs and handles NAT | 04 §3.4, §3.7 |
| Dedicated server transport | UnityTransport 2.7.4, direct UDP 7777. SDR for dedicated servers needs Valve's approval | 04 §3.5 |
| Non-Steam test builds (itch.io) | UnityTransport + Unity Relay through `com.unity.services.multiplayer` 2.4.1 Sessions (free under 50 average CCU) | 04 §3.5–3.7 |
| Lobby | Steam lobby with friends invites and a public list; Unity Sessions for non-Steam builds | 04 §3.7 |
| Testing | Multiplayer Play Mode 2.0.2 (up to 4 virtual players) and multi-instance players; Network Simulator presets of 100–200 ms and 2 % loss | 04 §3.7 |
| Development AppID | **Steam AppID 480** (Valve's public test app) until your own AppID exists (D6). It needs the Steam client running and signed in, by you | — |

### 5.2 Authority model and messages

- **Vehicles are owner-authoritative.** Each client simulates its own car at full BRV quality. The host validates and relays. Remote cars are kinematic puppets with Hermite interpolation on a ~100 ms buffer. Near cars are extrapolated to the present with projective-velocity-blending dead reckoning (at most 250 ms), so collisions line up. No rollback or prediction is needed (04 §3.2).
- **`VehicleSnapshot`, 40 bytes, unreliable-sequenced at 20 Hz.** The 32 bytes of 04 §3.2: tick u16; position 3×21 bits over 1,024 m; smallest-three rotation; half-float linear and angular velocity; steer, throttle, brake, flags; rpm u16. Plus 8 bytes of drivetrain state from 06 §4.6: engine load, gear, clutch, diff locks and low range, turbo/Jake state, damage sequence, two spare.
- **Collisions:** the local car hits the remote puppet and reacts. A `ContactImpulse` RPC (point, normal, impulse) goes to the other owner, who applies it (04 §3.2).
- **Host validation** (`HostValidator`): speed above the vehicle's spec plus a margin, position jumps larger than v·dt + ε, out-of-world positions, impossible acceleration → reject, then kick. Steam auth tickets prevent SteamID spoofing (04 §4).
- **Economy is host-authoritative.** Delivery triggers run on the host. Clients only send `IEconomyAuthority` requests (03 §4.22, 04 §3.2).
- **AI traffic is host-simulated.** Lane state (`laneId`, `s`, `speed`) is replicated at 5–10 Hz; a knocked car switches to full snapshots until it recovers (04 §3.2).
- **Terrain deformation:**
  - Owners quantise integer `RutStamp`s (16 B) **before** applying them locally, and send batches every 0.25 s.
  - The host appends them to a global log and rebroadcasts. Every second it sends `(tile, version, xxHash32)`; a mismatched tile is resent (2 KB raw).
  - Late joiners get all non-zero tiles, deflated (06 §5.6).
- **Engine sound is never sent.** Each client renders remote engines with FOLLOW_RPM or loop banks from the snapshot (§3.6).

### 5.3 Sessions, hosting, cost

- **Session size:** 16 by default for player-hosted sessions, 24 maximum with interest management (full rate within 250 m, 4 Hz beyond), 32–48 on dedicated servers (04 §3.3). The host uploads about 156 KB/s (1.25 Mbit/s) at 16 players (04 §3.3), plus about 25 % for the larger 40 B snapshot.
- **Host leaves:** there is no host migration in NGO client-hosted sessions. Progress is saved continuously and the game offers "host a new session with the same players". Dedicated servers avoid the problem (04 §3.2).
- **Dedicated server:** a `com.unity.dedicated-server` 2.0.2 build profile (headless; no rendering, audio or engine-sim). Released later as a free Steam tool app, Motor Town style; players run it or rent it (04 §3.1, §3.7).
- **Cost to you:**
  - $100 once for Steam Direct (D6) and **$0 a month** at any player count with SDR and player hosting (04 §3.6).
  - Unity Personal is free up to $200k; the splash screen is optional (04 §4).
  - No anti-cheat at launch: host checks and per-server ban lists keyed on SteamID (04 §4).
- **Saves:**
  - Local JSON plus Steam Cloud through `com.unity.platformtoolkit.steam` 1.2.0 once you have an AppID.
  - Online progression is per server, keyed by SteamID.
  - Global progression (Unity Authentication + Cloud Save) is a later decision (04 §4).

### 5.4 Single-player and multiplayer at each milestone

| Milestone | Single-player | Multiplayer |
|---|---|---|
| M0 | test scene | none |
| Spikes | lab builds S1–S4 | S5 net lab only: 2–4 test cars over local UDP, then you and a friend over Steam SDR |
| M1 Millbrook Drive | full island drive, pickup and hatch | none; all code goes through `VehicleControls`/`IEconomyAuthority`, so nothing has to be torn out later |
| M2 Offroad | mud, ruts, winch, dozer, on foot | none |
| M3 Living town | AI traffic, signs, roundabout | none (traffic is already host-shaped) |
| M4 Online free-roam | everything above | co-op free roam for 2–8 players over Steam (16 is the target), shared mud, towing each other; dedicated server alpha; optional itch.io build with Unity Relay |
| M5 Jobs and progress | jobs, money, saves | host-authoritative jobs, per-server progress, Steam Cloud |
| M6 Trucks | trucks, trailers | trailers replicated, convoy jobs |
| M7 Race cars | race cars, time trials | race sessions with host-validated timing |
| M8 Launch prep | polish | Steam Playtest; dedicated server tool app |

---

## 6. Roadmap

### 6.1 Order and dependencies

```
 M0 bootstrap ──┬── S1 engine-sim ──────────────┐
  (week 1)      ├── S2 BRV 480 Hz ──────────────┼── I1 (S1×S2) ──┐
                ├── S3 canon + terrain ──┬──────┘                 ├── G1 review ── M1 ── M2 ── M3 ──────────┬── M5 ── M6 ── M7 ── M8
                │                        └── S4 roads ── I2 ──────┤   (week 6)                  └── M4 ──────┘
                └── S5 netcode + SDR ─────────────────────────────┘
 Art track (after G1, parallel with M1–M2): 07 phases A–E on the pickup ──► ready for M6
 Optional M9 diesel/turbo fork: may start after M5, in parallel with M6
```

- **Effort** is in **agent-days (ad)**: one AI agent working a scoped task for a day, including test runs.
- **The calendar** assumes 3–4 agents in parallel during the spikes and 2–3 afterwards, with you reviewing and playing about twice a week.
- Reports 03 §5 and 05 §9 are the sizing anchors: parity is about 5–7 months for one part-time developer, and the roads are about 31 days.

### 6.2 M0: Bootstrap (5 ad, week 1)

**Deliverables:**
- the repo of §1.2;
- the Unity project with the settings and packages of §1.3, and all asmdefs as stubs;
- `BrCoords` with tests;
- `Backroads.Testing` (autotest runner, metrics JSON, camera bookmarks, contact sheet, F10 checklist);
- the CI scripts of §1.7;
- a native CMake skeleton that builds scs and the engine-sim core into `EngineSimPlugin.dll` exporting `esp_version`;
- the oracle worktree and `oracle-verify`;
- the FBX axis canary plus a glTFast import of `hatch.glb` (07 phase A).

**Acceptance tests (agent):** `tools/ci/gate.ps1 -Gate M0` on a clean clone runs:
- `build-native` with CTest green;
- the EditMode suite: `BrCoords` round trips, the project-settings assertions, canary `MARK_Front.z > 0`, `MARK_Left.x < 0`, `MARK_Up.y > 0`;
- a PlayMode smoke test;
- `build-player -Profile Client`, then `autotest -Scenario smoke` exits 0;
- `oracle-verify`: the six Node-reproducible layer hashes (H, BASE, ROADD, MUD, WEAR, TINT) equal 03 §3.2.

**You see:** `Builds/Client/Backroads.exe` opens a grey test yard with the old hatch model on a turntable and the F10 checklist.

### 6.3 The spike phase (54 ad, weeks 2–6)

Each spike has a **timebox**. At 150 % of the timebox without a pass, its fallback is adopted and you are told why. A spike's lab build is a deliberately plain scene; looks do not matter yet.

#### S1: engine-sim native plugin, threads and audio (10 ad)

- **Scope:**
  - §1.5 in full: unmodified vendor tree, shims, ABI v1, worker threads, coast mode, renderSync ring, 48 kHz re-init, AVX2 FIR, IR loader, C++ EJ25 and LS builders, and piranha if D3 arrives;
  - both Unity audio paths;
  - `esp_cosim_matrix`, which re-creates 01 §3.5 natively;
  - the lab scene `S1_EngineLab`: an engine on a stand with throttle, ignition, starter, gear and a quality-tier selector, plus 4 "remote" engines on a FOLLOW_RPM rpm script placed around the listener.
- **Hard gates:**
  - **S1-G1 build:** native build 0 errors; CTest 100 %; the ASan configuration runs every engine test with zero reports.
  - **S1-G2 engines:**
    - EJ25 WOT sweep peaks at 229 ± 7 N·m at 4,500 ± 500 rpm and 170 ± 6 hp at 6,500 rpm (02 §6.2).
    - LS at 715 ± 25 N·m at 4,000 rpm (01 §5.3).
    - With piranha: all 27 shipped engines compile, crank, idle (rpm within ±15 % for 5 s) and run 10 s at WOT with no NaN, the 9/10/12-cylinder engines included.
  - **S1-G3 exact time:** over 600 s, engine steps = round(Σdt·f) ± 1.
  - **S1-G4 threads:**
    - main-thread cost of all engine-sim calls ≤ 0.10 ms per `FixedUpdate` at p99, with no blocking calls;
    - local V8 worker ≤ 3.5 ms per 1/120 step at p95 in the player (01 measured 2.9 ms);
    - torque-map fallback on ≤ 0.1 % of steps in 10 min.
  - **S1-G5 audio:** in a 10-min player run with a 512-frame DSP buffer at 48 kHz:
    - 0 underruns; clipping ≤ 0.01 % of samples;
    - dominant pitch within ±3 % of rpm·cyl/120 at idle, 2,500, 4,000 and 5,500 rpm;
    - pedal step → RMS rise ≤ 60 ms;
    - a 100 ms main-thread hitch gives 0 underruns; a 250 ms hitch gives no click and recovers within 100 ms;
    - with `spatialBlend = 1` at 20 m, RMS falls by ≥ 12 dB and pans correctly on at least one of the two paths.
  - **S1-G6 coupling** (`esp_cosim_matrix`):
    - mode D at 120 Hz × 4: Δv ≤ 0.15 m/s on the three 01 §3.5 scenarios, rpm tracking RMS ≤ 40 rpm, no NaN;
    - mode A at 50 and 120 Hz: Δv ≤ 0.10 m/s;
    - with the backstop, mean rpm on the limiter within 300 rpm of the reference.
  - **S1-G7 CPU:** 1 local V8 (Ultra) + 4 FOLLOW_RPM remotes ≤ 1.6 cores in total; every engine ≥ 1.5× real time.
- **Agent runs:** `gate.ps1 -Gate S1` (native tests, then the `Lab-S1` build, then the `s1.audio.steady`, `s1.audio.hitch`, `s1.audio.spatial` and `s1.cpu` autotests).
- **You check** in `Lab-S1`:
  - Does it sound like a real engine at idle, cruise and redline?
  - Blind A/B: Ultra vs High, AVX2 vs scalar FIR, FOLLOW_RPM vs PD for the remotes.
  - Pick your default tier.
- **Fallbacks:**
  - D fails → A for single-axle drivelines and C for the rest (same ABI).
  - No 3D on either audio path → the DC-clip trick, or the Native Audio Plugin SDK (01 §4.5).
  - CPU fails → High/Low tiers and an R1 cap of 1.
  - **Kill criterion:** if one local engine cannot hold 1.5× real time in a player build, engine-sim becomes "sound + dyno map": FOLLOW_RPM for every car and BRV uses `TorqueMapEngine`. The sound is still engine-sim's.

#### S2: BRV physics stability at 480 Hz (12 ad)

- **Scope:**
  - `Backroads.Vehicles` core from 06 §3–§4: PGS over soft bounded rows with warm start; wheel ω and shafts as solver DOFs; MF secant tyres with the friction ellipse and the low-speed floor; rolling resistance; clutch, gearbox (manual, sequential, AMT, torque converter), open/locked/LSD/Torsen diffs, transfer case, brakes with ABS; lumping of rigid chains; explicit suspension with bump stops; hull rows against WorldGrid; a PhysX Rigidbody chassis with `useGravity = false`; a kingpin `ConfigurableJoint`.
  - `TorqueMapEngine` as the engine, so S2 does not wait for S1.
  - Terrain: an analytic WorldGrid (flat pad, 10/20/30/35 % ramps, 150 mm steps, a constant mud patch, a 30 m skidpad) through the real `WorldGrid` API.
  - Test definitions: hatch, pickup and semi + trailer, with JS DEFS values mapped to the 06 §7 tables.
- **Hard gates:**
  - **S2-G1 cost** (06 P1, `Backroads.Tests.Perf.Vehicles.StepCost`): ≤ 30 µs per vehicle per step (4 substeps × 12 iterations, Burst, player build); 50 vehicles in parallel jobs ≤ 1.0 ms wall time.
  - **S2-G2 robustness** (`StabilityFuzz.Seeds_0_999`): 1,000 seeded 60 s runs (random inputs, ramps, kerbs, 2 m drops, rollovers) with 0 NaN/Inf, no chassis |v| > 100 m/s and no |ω| > 50 rad/s.
  - **S2-G3 parking** (06 P5): every definition on 10–35 % grades for 60 s with the parking brake creeps < 1 mm/s; parked on the flat, chassis speed RMS < 2 mm/s and wheel ω RMS < 0.01 rad/s.
  - **S2-G4 driveline:**
    - an open diff with one wheel in the air spins that wheel up without oscillation;
    - locked-diff scrub measurably widens the turning circle;
    - low-range crawl up 35 % holds ≤ 3 m/s steady;
    - ABS stop from 100 km/h on asphalt in 36–45 m;
    - the semi's lowest gear (≈ 51:1, a 3 kg·m² crank against 32 kg·m² wheels, 06 §9 item 4) survives a clutch dump with no crank oscillation > ±15 rpm after 0.5 s, and locked rows converge within 1 % in ≤ 20 iterations.
  - **S2-G5 trailer:** an 8.6 t tractor + 27 t trailer through a 90° turn at 15 km/h, an 80→0 stop and a 30 m reverse: kingpin drift < 5 cm, no NaN.
  - **Soft gate (warn only):** pickup 0–100 km/h and 80→0 km/h within ±10 % of the JS golden traces once S3 has recorded them. It becomes hard in M2.
- **You check** in `Lab-S2`: drive the pickup and hatch on the ramps, park on a slope, launch, do donuts, hit kerbs, tow the trailer. "Does it feel at least as good as the browser game? Any shaking when parked?"
- **Fallbacks:**
  - convergence fails → a dense LDLᵀ solve for the ≤ 16 shafts with active-set clamping (06 §9 item 4);
  - trailer drift → `ArticulationBody` (06 §6.1);
  - **kill criterion:** if the row solver cannot pass G2 and G3 together, port `vehicle.js` verbatim (03 §4.14 phase 1) at 120 Hz and couple engine-sim in FOLLOW/C mode.

#### S3: canon export and identical terrain (8 ad)

- **Scope:**
  - the JS exporter (D2) and the export drivers;
  - `CanonData/v1` with `probes.json`, camera-ray hits, reference shots and golden traces;
  - `CanonImporter`, `WorldGrid`, `TerrainChunkSystem` (flat-shaded colours are enough);
  - static placement of buildings (frozen GLB), trees, rocks and typed props (with `static_props.glb` as the visual fallback, 03 §4.7);
  - a fly camera with 12 bookmarks;
  - a half-day measurement of the Unity Terrain alternative (06 P2) for the record.
- **Hard gates** (`Backroads.Tests.EditMode.Canon.*`):
  - **S3-G1 hashes:**
    - every manifest file verifies;
    - the layer SHA-256s equal 03 §3.2 (H `a8908d7f…`, BASE `2eda4082…`, SURFACE `8788d405…`, ROADD `8d31f109…`, MUD `2192f4c8…`, WEAR `85473f6f…`, TINT `08452b46…`);
    - after the JS exposures are added, the oracle still produces the same hashes, headless and in the browser.
  - **S3-G2 counts:** 5,265 trees (by species as in 03 §3.2), 4,013 colliders, 78 lamps, 59 lots, 24 roads, 53 lanes, 108 connectors, 5 loops, 59 model placements, 87 footprints.
  - **S3-G3 sampling:** `HeightAtJs` (double precision) at 1,000 probes within 1e-5 m; surface exact; normals within 1e-4; 12 cameras × 4,096 rays hit within 1 mm of the JS `rayTerrain` hits.
  - **S3-G4 identity:** all 256 LOD0 chunk meshes are **bit-exact** against `BrCoords.Pos(i·0.5−512, H[k], j·0.5−512)` (positions are multiples of 0.5 in local chunk space, so they are exact in float32); every building, tree and typed-prop transform within 1e-4 m and 0.01°; `buildings.glb`/`trees.glb` hashes equal the manifest; pane names resolve for all 11 models.
  - **S3-G5 performance:** all 256 chunks built in ≤ 1.5 s at load; a dirty-chunk rebuild ≤ 1 ms on a worker; terrain ≤ 4 ms GPU at 1440p High; chunk meshes updated under the GPU Resident Drawer without errors.
- **You check** in `Lab-S3`: fly around with the browser game open beside it and step through the 12 bookmarks (keys 1–0, −, =). "Same hills, same houses, same trees, in the same places?" A generated contact sheet shows Unity next to the JS shot for each bookmark.
- **Fallbacks:**
  - no Playwright → you run the export manually once (2 minutes);
  - a hash moves after the JS exposures → revert and export trees from collider records, re-deriving bushes and reeds (03 §3.4);
  - chunk rendering too slow → Unity Terrain tiles at 16-bit (≈ 3 mm, 03 §4.3), which changes only the visuals; physics stays exact.

#### S4: road and intersection mesher (12 ad)

- **Scope:**
  - 05 work items R0–R3 plus the parts of R4–R6 needed for Millbrook: import, graph cleanup (T-split, 1 m clustering, zone split, bends, the complex-junction error), arms, `SolveCorner` fillets with the radius back-off, mouths and stop lines, surface polygons with `RingMesh`, plateau and crown-warp heights, Clipper2 kerb loops and sidewalks, depressed corners and the 51 driveway cuts, markings, road plates, 64 m chunks with LOD and baked MeshColliders, basic lanes and connectors, and validation (05 §5, §8).
  - **Both roundabout variants** (4-leg with the R3 realignment, and 3-leg with a moved T, 05 §7.1) are baked, so you can choose by looking.
- **Hard gates** (`Backroads.Tests.EditMode.Roads.*`):
  - **S4-G1:** the Millbrook bake finishes with 0 errors in ≤ 5 s; ≤ 60k triangles in total.
  - **S4-G2:** every mouth vertex is shared bit-exactly between ribbon and junction; height mismatch < 1 mm along every seam and kerb loop.
  - **S4-G3:** all junction polygons are simple and CCW, with area within ±5 % of the connector-corridor union.
  - **S4-G4:** baking twice, and on a clean clone in batchmode, gives identical asset hashes.
  - **S4-G5:** terrain sits ≤ surface − 0.03 m 0.25 m inside every footprint and within 0.1 m of the edge height 1 m outside; **outside the road-plate mask, H is bit-identical to v1**; all 59 lot pads stay within 0.05 m.
  - **S4-G6:** kerb to building ≥ 1.0 m at downtown corners (6 m radii at nodes 7 and 9, 05 §1.3); every mailbox, tree and lamp is outside the walk band or moved by an explicit override.
  - **S4-G7:** lanes at unchanged junctions within 0.3 m of `traffic.json`; every approach has a connector; conflicts are symmetric.
  - The five synthetic cases also bake (30° Y, 5-arm star, two junctions 25 m apart, a T on a 15 m curve, an arm shorter than its tangent).
- **I2 (with S2 and S3):** a scripted BRV pickup drives every lane path through all 11 junctions and the roundabout at 40 km/h: no seam spike > 0.3 g vertical, no wheel snag, and a 150 mm kerb registers as a real bump.
- **You check** in `Lab-S4`: drive and fly through the junctions in both roundabout variants. "Do these look like real small-town intersections? Which roundabout do you want?" That answers D5.
- **Fallbacks:**
  - a node the solver cannot handle → a hand-authored polygon in `roads-overrides.json` (there are only about 30 nodes);
  - LOD cross-fade shimmers under the Resident Drawer → cross-fade off (05 §5.13);
  - last resort per node → JS-style overlapping ribbons.

#### S5: owner-authoritative netcode with Steam SDR (8 ad)

- **Scope:**
  - `Backroads.Net` and `Backroads.Platform.Steam` (§5.1–5.2);
  - a stub kinematic test car, swapped for the S2 pickup once G2 passes;
  - the integer rut kernel with tile hashes;
  - a cheat client (`-cheat speed3x|teleport50`);
  - the `Server` build profile.
- **Hard gates:**
  - **S5-G1** (agent, 4 player instances on UTP, simulator at 150 ms RTT, 2 % loss, 20 ms jitter, 10 min): remote error vs the owner's logged truth ≤ 0.5 m for interpolated cars at 20 m/s and ≤ 1.0 m p95 for extrapolated near cars; 0 jumps > 2 m in one frame.
  - **S5-G2:** in a bandwidth test with 16 synthetic clients, host upload ≤ 200 KB/s and each client ≤ 2 KB/s up.
  - **S5-G3:** a head-on collision at 2 × 10 m/s and 150 ms RTT: both owners receive the impulse; no overlap lasts > 200 ms; no post-hit launch (speed ≤ pre-hit + 2 m/s).
  - **S5-G4:** the cheat client is flagged and kicked within 1 s.
  - **S5-G5** (06 P6): a 10,000-stamp log applied in two processes and in a Burst-off build gives identical tile hashes; a late joiner matches.
  - **S5-G6:** the headless server runs 8 bot clients for 30 min with < 1 core and < 1.5 GB.
  - **S5-G7 (you and a friend):** you host on AppID 480; a friend on another network joins through a Steam invite; you drive together for 15 min. Pass = connected through SDR, no IPs exchanged, both rate the smoothness ≥ 4 of 5.
- **Fallbacks:**
  - the Facepunch transport misbehaves with NGO 2.13 → a thin `SteamSocketsTransport : NetworkTransport` of our own on Facepunch.Steamworks;
  - SDR blocked → Unity Relay for testing;
  - NGO blocked → Mirror 96.x (04 §3.4).
  - Server-authoritative prediction stays explicitly rejected (04 §3.2).

#### I1 and the G1 review (4 ad, week 6)

- **I1 (S1 × S2):** the BRV pickup with the LS V8 in mode D runs the 06 §4.5 P4 matrix (clutch dump at 6,000 rpm in low-range 1st, stall by braking in 2nd, WOT rev in neutral, idle in Drive against the brakes, a 36 t semi in top gear at 100 km/h). Pass: no rpm oscillation > ±15 rpm at 120 Hz, no divergence, stall and restart work, and `J_eff` calibration error < 3 % (06 P3).
- **G1 review:**
  - The agents commit `docs/gates/G1.md` with every gate's JSON, the fallbacks adopted, and the measured numbers for your minimum-spec decision.
  - You play the five lab builds and fill in the F10 checklists, about an hour in total.
  - The contracts of §2.4 freeze.

### 6.4 Milestones after the gate

Every milestone ends with:
- a `Client` build;
- `gate.ps1 -Gate M<n>` green;
- your F10 checklist.

**Continuous gates from M1 on:**
- the full EditMode and PlayMode suites;
- S3-G1/G4 canon identity;
- S4-G4 bake determinism;
- a 10-minute audio underrun check;
- a performance budget: High at 1440p ≥ 60 fps at p95 on this laptop; main thread ≤ 8 ms.

#### M1: Millbrook Drive (single-player vertical slice; 22 ad; weeks 7–10)

- **Deliverables:**
  - canon import productionised;
  - the terrain shader port (two-surface look);
  - the road bake integrated with your roundabout choice (v1.1) and signs from the node controls;
  - buildings with night windows, trees with wind, rocks, typed props, the 78 lamps as real lights, water;
  - BRV pickup (LS V8) and hatch (VTEC I4) with engine-sim and audio;
  - camera port (chase and hood), Input System actions;
  - day/night clock and the URP stack of §4.1 (first pass);
  - a minimal HUD (speed, rpm, gear, clock), garage spawn bays, Low/Mid/High presets, an audio tier setting.
- **Acceptance (agent):**
  - `M1.DriveLoop`: a scripted pickup follows a 3 km route through 6 junctions and the roundabout with no stall and no NaN, ± 10 % of the planned time.
  - `M1.Perf` across 12 bookmarks and the route.
  - The `M1.Shots` contact sheet, 12 bookmarks × 10:00/21:00, beside the JS reference shots.
  - A 10-min underrun check.
- **You play:** spawn in the Millbrook garage in the pickup, drive anywhere on the island, hear the V8, watch day turn to night, and drive through real intersections with kerbs, markings and stop signs. The ground does not deform yet.

#### M2: Offroad and mud (22 ad; weeks 11–14)

- **Deliverables:**
  - integer deformation kernels and render sync;
  - the mud and soft-soil model (06 §5.3) with ruts, berms and compaction;
  - water and buoyancy, hydro-lock;
  - low range, transfer case, lockers;
  - winch v2 (06 §5.7);
  - dozer blade and tracks (03 §4.15);
  - VFX particles and `SG_MudLayer` dirt;
  - on-foot character with the shovel (placeholder rig);
  - tow and reset.
  - Art track: the pipeline pilot `VH_Pickup_MidA` replaces the old pickup GLB.
- **Acceptance (agent):** the JS golden traces become **hard** gates:
  - pickup 0–100 km/h and braking ± 10 %;
  - bog sink depth ± 15 % and stuck time ± 25 %;
  - quarry low-range climb ± 15 %;
  - dozer 20 s cut volume ± 15 %;
  - winch 15 m ± 10 %.
  - Deformation: JS parity ≤ 2 mm per call sequence; 4 wheels rutting ≤ 0.5 ms CPU per frame; ≤ 2 chunk rebuilds per frame.
  - P5 hill hold re-run for every definition.
- **You play:** drive into Blackwater Bog, get stuck, get out with low range, a locker or the winch; push dirt with the dozer; walk and dig.

#### M3: Living Millbrook (22 ad; weeks 15–18)

- **Deliverables:**
  - the full lane graph with conflicts and controls (stops, all-way stop at node 16, roundabout yields, the optional signal at node 2 per D5);
  - sign, lamp and inlet props;
  - the traffic AI port (12 cars, ghosts, rebalance, rescue, 03 §4.21) on the new lanes;
  - AI LOD (06 §4.7) with `TorqueMapEngine`, R2 loop banks, and R1 FOLLOW_RPM for the nearest one or two.
- **Acceptance (agent):** `M3.Traffic30min` soak:
  - 0 AI-AI collisions at junctions;
  - 0 deadlocks longer than 60 s;
  - ≥ 95 % of stop-controlled entries stop at `sStop` below 0.5 m/s;
  - roundabout entries yield;
  - trucks never use `noTruck` connectors;
  - traffic ≤ 1.5 ms CPU per frame.
- **You play:** watch cars stop at stop signs, take turns at the all-way stop and yield at the roundabout, and hear them pass.

#### M4: Online free-roam (16 ad; weeks 17–19, overlapping M3)

- **Deliverables:**
  - S5 productionised on the real vehicles and world: Steam host/join UI;
  - remote engine tiers;
  - stamp log, tile hashes and late join;
  - traffic replication;
  - the dedicated server alpha;
  - an optional itch.io build with Unity Relay (D14).
- **Acceptance (agent):**
  - S5-G1…G6 re-run with BRV, mud and traffic;
  - a 30-min 4-instance soak ends with equal terrain tile hashes on every client and the late joiner;
  - AI positions agree within 2 m;
  - host upload ≤ 1.0 Mbit/s with 8 players.
- **You play:** with a friend over Steam, drive together, get stuck together, winch each other out.

#### M5: Jobs, HUD and progress (15 ad; weeks 20–22)

- **Deliverables:**
  - `JobSystem` (boards, cargo, damage, pay, fuel, wash, tow) behind `IEconomyAuthority`;
  - UI Toolkit HUD, minimap, big map, labels and freight board;
  - `SaveService` with Steam Cloud once you have an AppID;
  - the fleet garage.
- **Acceptance (agent):**
  - the payout formula equals `jobs.js` for 1,000 seeded inputs;
  - a scripted depot → farm delivery pays the expected amount;
  - a cheat client cannot complete a delivery;
  - money and time survive a restart.
- **You play:** take a freight job, deliver it, get paid, quit, come back to the same money.

#### M6: Trucks (30 ad; weeks 23–28)

- **Deliverables:**
  - the Blender pipeline productionised (07 phases B–E);
  - `VH_Truck_Box75`, `VH_Semi_6x4A` + 53 ft trailer, `VH_Offroad_6x6A` with CTIS;
  - air brakes, Jake brake, diesel stand-ins with sample layers;
  - damage layers (detachables, glass, vertex dents);
  - trailer jobs.
- **Acceptance (agent):**
  - `br_validate` green per vehicle (budgets, sockets, ≤ 9 materials, wheel radius within 2 % of `vehicle.json`);
  - the V5 checks of 06 §8: jackknife recovers, the 6x6 crosses the bog with CTIS, the semi does 105 ± 5 km/h at 1,500 rpm in top gear (06 §7.3);
  - every engine's dyno sweep inside its target band.
- **You play:** each truck, hauling a trailer onto the depot dock. You also hand-tune each cage in Blender (1–3 h each, D13) and sign off each review sheet.

#### M7: Race cars (22 ad; weeks 29–33)

- **Deliverables:**
  - rally coupe, GT and trophy truck;
  - aero, setup sheet, tyre temperature, slipstream (06 §6.3);
  - the livery compositor with fictional sponsors (07 §6.6);
  - time trials and point-to-point events on closed island roads and a dirt stage;
  - online race sessions with host-validated timing.
- **Acceptance (agent):**
  - GT downforce at 200 km/h within ± 10 % of 580 kg (06 §6.3);
  - the rally car's gravel stage time is repeatable within ± 2 % over 5 autopilot runs;
  - a teleporting cheater is disqualified.
- **You play:** race a friend; choose liveries.

#### M8: Looks and launch prep (15 ad; weeks 34–36)

- **Deliverables:**
  - the graphics polish of §4.1 (APV bake, follow probe, grading LUT, optional volumetrics, the Steam Deck Low tier);
  - Synty dressing for the work sites and street furniture (D7);
  - performance passes;
  - third-party notices (engine-sim, scs and piranha MIT; Clipper2 BSL; LibTess SGI-B; OSM ODbL attribution; OFL fonts; Facepunch MIT);
  - a Steam Playtest build.
- **Acceptance:**
  - the full performance matrix;
  - a licence-notice test (every vendored folder has an entry);
  - the build installs and runs on a friend's clean PC.
- **You:** sign off the 12-bookmark look sheet at 4 times of day.

#### M9 (optional): Diesel and turbo fork (20–40 ad, can start after M5)

- **Deliverables:** the fork patches of 06 §4.8 items 6–8 in `native/EngineSimPlugin/fork/` (forced induction first, then the engine brake, then compression-ignition research).
- **Acceptance:**
  - a boost-pressure trace that responds to spool;
  - engine-brake retard torque within ± 15 % of the Unity-side model it replaces;
  - your listening sign-off.

### 6.5 Timeline and effort

| Phase | Agent-days | Calendar | First playable for you |
|---|---|---|---|
| M0 | 5 | week 1 | test yard |
| S1–S5 + I1/I2 + G1 | 54 | weeks 2–6 | five lab builds |
| M1 | 22 | weeks 7–10 | **Millbrook drive with a simulated V8** |
| M2 | 22 | weeks 11–14 | mud and recovery |
| M3 | 22 | weeks 15–18 | traffic |
| M4 | 16 | weeks 17–19 | **online with a friend** |
| M5 | 15 | weeks 20–22 | jobs and money |
| M6 | 30 | weeks 23–28 | trucks and trailers |
| M7 | 22 | weeks 29–33 | race cars |
| M8 | 15 | weeks 34–36 | Playtest build |
| **Total to M8** | **≈ 223** | **≈ 36 weeks** | |

These are estimates. The spike results re-plan everything after week 6.

---

## 7. Risks and decisions

### 7.1 Top risks

| # | Risk | Retired by | Mitigation / fallback |
|---|---|---|---|
| 1 | Mode D's 1-step torque lag shows as 270–350 rpm error on the rev limiter (01 §3.5) | S1-G6, I1 | Unity fuel-cut backstop, optional `∂T/∂ω` feed-forward, mode A for single-axle cars |
| 2 | engine-sim CPU on players' PCs (0.5–1.1 cores for the local engine on a half-speed PC, 02 §9) | S1-G7, G1 min-spec | Tiers that never touch frequency; AVX2 FIR; R1 caps; loop banks; the torque-map fallback |
| 3 | 3D spatialisation of procedurally generated audio is undocumented (01 §4.5) | S1-G5 | `IAudioGenerator`; the DC-clip trick; the Native Audio Plugin SDK |
| 4 | piranha needs a downloaded tool; its licence file is missing at 432f0b1 (01 §5.4) | D3 | C++ builders; MIT text from upstream GitHub in the notices |
| 5 | A native crash takes down the game or the Editor | S1-G1 (ASan) | Exceptions caught at the ABI; the out-of-bounds write removed in our override; fuzzed CTest; DLL-version check with a torque-map fallback |
| 6 | PGS convergence at inertia ratios up to about 240:1 (semi crank reflected through 51:1 vs dual wheels, 06 §9 item 4) | S2-G4 | Lumping; up to 20 iterations; a dense LDLᵀ shaft solve |
| 7 | Canon drift: JS changes, a `buildings.py` rebuild renumbering panes, RNG-consuming exposures (03 §3.3–3.4) | S3-G1/G4, continuous | Pinned oracle worktree, hashes on every run, frozen GLBs, panes mapped by name |
| 8 | Proper intersections conflict with canon: the 13.8 m complex junction, tight downtown corners, −9 % approaches, the wrong major road (05 §0) | S4, D5 | Versioned v1.1 overrides; bit-identity outside the plate mask; radius rules; flattest-spot stop lines |
| 9 | Terrain rendering cost and the deformation view lagging (03 §4.3, 06 P2) | S3-G5, M2 | Physics never depends on the renderer; budgeted rebuilds; Unity Terrain as an alternative view |
| 10 | Online collisions feel wrong without server authority; no host migration (04 §3.2) | S5-G3, M4 | Contact-impulse RPC, near-car extrapolation, autosave, "host again" |
| 11 | Deformation diverges between clients (03 §4.4) | S5-G5 | Integer `DELTA_MM` relative to canon H, tile hashes, tile resend |
| 12 | Diesel and turbo authenticity (01 §5.3, 06 §9 item 3) | — (accepted for v1) | Honest stand-ins, sample layers, Unity-side Jake brake; M9 fork |
| 13 | GPU Resident Drawer limits: no MPB, LOD cross-fade unverified (04 §2.2, 05 §5.13) | S3-G5, S4 | Material instances and Renderer Shader User Value; cross-fade off if it shimmers |
| 14 | Unity version churn: 6.7 LTS, then Unity 7 CoreCLR (04 §1) | continuous | The forward-compatibility rules of 04 §1.2 from day one; upgrade only at a milestone boundary, behind the full gate suite |
| 15 | Licences: Synty's generative-AI clause; vehicle trade dress (07 §9, §10.4) | process | Agents never touch Synty files; fictional brands; `brief.md` per vehicle; ask Synty in writing |
| 16 | The Steam test needs a second person and account, so agents cannot run it alone | S5-G7 | Agents run S5-G1…G6 locally; you schedule one 15-minute session with a friend |
| 17 | Agent coordination and scope creep | G1 contracts | Frozen contracts with ADRs; one gate script per milestone; timeboxes with automatic fallbacks |

### 7.2 Decisions only you can make

| # | Decision | Recommendation | Needed by |
|---|---|---|---|
| D1 | New repo `Backroads-Unity` (you create the private GitHub repo) vs a subfolder of `D:\Backroads`; whether agents may push | New repo; agents commit locally, you push or grant push | M0 |
| D2 | Approve the additive JS exporter (`js/exportUnity.js` + three one-line exposures in `props.js`, on a branch, hash-verified) and the oracle worktree; approve Playwright as a project-local dev dependency, or do the export by hand | Approve both | S3 day 1 |
| D3 | Approve downloading the portable win_flex_bison 2.5.25 zip into the repo's `tools/` (nothing installed) to unlock the 27 `.mr` engines | Approve | S1 week 1 (optional) |
| D4 | Update Unity to 6000.3.26f1 in Unity Hub; don't use 6000.5; move to 6.7 LTS later | Yes | before M1 |
| D5 | Roundabout: 4-leg with the harbor road realigned (recommended) or 3-leg with a moved T; where the town delivery point goes; one signal at node 2 or none; keep the −9 % approaches; keep the canon street widths (05 §9) | 4-leg; a kerbside loading bay on Main Street; no signal at first; keep both | G1 (after Lab-S4) |
| D6 | Become a Steamworks partner ($100, tax and bank forms) and create the AppID; until then AppID 480; find a friend for the S5 test | Do it before M4 | S5 (friend), M4 (AppID) |
| D7 | Which Synty packs, bought as one-time licences; download them to `D:\SyntyPacks\`; email Synty about AI coding assistants | Town + Construction (+ Swamp), about $155 (07 §10.2) | M3 for street furniture, M8 at the latest |
| D8 | Fictional brand and model names; 2–4 real inspirations per vehicle; collect reference photos yourself | Rename "Ranger 4x4" and "Muncher D3" now | M2 (pickup), M6 |
| D9 | Minimum-spec target: GTX 1070-class, Steam Deck, or laptops with integrated graphics (07 §12) | GTX 1070 class + Deck at the Low tier | G1 |
| D10 | Diesel authenticity: accept stand-ins for v1, or fund the M9 fork (weeks of research) | Stand-ins now, decide on M9 after M5 | M5 |
| D11 | Engine sound sign-off and default quality tier, from your listening tests | — | G1 |
| D12 | Driving-feel sign-off for BRV against the browser game | — | G1, M2 |
| D13 | Your time for Blender hand tuning (1–3 h per vehicle) and review sheets | Yes, for hero vehicles | M2 art track, M6 |
| D14 | An itch.io alpha with Unity Relay (needs a Unity Cloud project and an itch account, both signed in by you) or Steam only | Steam Playtest only, unless you want an earlier public alpha | M4 |
| D15 | Accept canon v1.1: road plates may change terrain only inside road footprints + 15 m, with everything else bit-identical | Accept | G1 |

Agents never sign in to Synty, Steam, Unity, GitHub or itch.io for you, never enter your passwords, and never push without your go-ahead. Every download (packages, the win_flex_bison zip, Facepunch.Steamworks, reference photos) is a step you approve.

---

## Appendix A: Gate result format

Every gate script writes `artifacts/gates/<gate>.json`. The orchestrator reads it, and the G1 and milestone reviews commit it to `docs/gates/`:

```json
{
  "gate": "S1-G5", "status": "pass", "hard": true,
  "build": { "git": "<sha>", "unity": "6000.3.20f1", "native": "<EngineSimPlugin source hash>", "abi": 1 },
  "machine": "Core Ultra 9 275HX / RTX 5080 Laptop / Windows 11 26200",
  "metrics":    { "underruns": 0, "clipPct": 0.004, "pitchErrPct": [0.8, 1.1, 0.6, 1.9], "pedalToSoundMs": 41 },
  "thresholds": { "underruns": 0, "clipPct": 0.01, "pitchErrPct": 3.0, "pedalToSoundMs": 60 },
  "evidence": ["artifacts/s1.audio.steady.wav", "artifacts/shots/s1/contact.png"],
  "fallbackIfFail": "IAudioGenerator path; DC-clip trick (proposal-risk §6.3 S1)"
}
```

Test names follow `Backroads.Tests.<EditMode|PlayMode|Perf>.<Subsystem>.<Case>`, and autotest scenario ids follow `<spike or milestone>.<area>.<case>` (for example `s2.park.grades`, `m3.traffic.soak30`), so a failing gate points straight at its test.
