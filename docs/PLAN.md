# Backroads in Unity: THE plan

*Status: the adopted plan, revision 2, for the user's go-ahead. Date: 2026-10-18.*
*Revision 2 applies three adversarial reviews (technical, sequencing, cost-legal). Every one of their 60 issues and the decision taken on it is listed in **Appendix R, Review log**. Issue ids such as [T1], [S7] or [C3] mark the places they changed.*
*Judged from three proposals in `docs/unity/design/`: `proposal-slice.md` (P-slice), `proposal-risk.md` (P-risk) and `proposal-online.md` (P-online). Evidence: the seven research reports, cited as (NN §x), e.g. (01 §3.5) = `research/01-enginesim-core.md` §3.5.*
*Facts checked on this machine today while writing: Unity 6000.3.20f1 and 6000.5.6f1 are installed with the **Windows Mono player only** (no IL2CPP module, no Dedicated Server module); the URP template `com.unity.template.3d-cross-platform-17.0.14.tgz` ships with 6000.3.20f1; Node v24.18.0, CMake 4.3.4, git-lfs 3.7.1, Microsoft Edge, Blender 4.5 and the VS installer are present; the oracle commit is `552f7807f4ad56547815c32a5e8407a7c324d556`; three.js 0.170.0 is loaded from jsDelivr by `index.html`. Nothing in `D:\Backroads` source or the engine-sim tree was changed.*
*Facts checked again for revision 2: `core.autocrlf=true` at system level in Git for Windows (`js/props.js` is `i/lf w/crlf`); `D:\Backroads-ref\engine-sim\assets\engines` holds 27 `.mr` files, of which 25 are engines and 2 are `radial.mr` helpers; the bundled NGO 2.13.0 contains `Runtime/Transports/SinglePlayer/SinglePlayerTransport.cs`; WSL2 has `Ubuntu-24.04`; `D:` has 469 GB free; the 01 probe folder `esprobe\` still exists in the session scratchpad.*
*Effort is in agent-days (AD): one AI agent working one day, tests included. All effort numbers are ±50 % estimates and are re-cut after M2. Calendar weeks assume the operating model of §1.11: about 4 lanes working 7 days a week while the laptop stays on.*

---

## In plain words

You get a new Windows build to play every one to three weeks. The first week number is the plan; the second is the latest it is likely to be if things slip (estimates are ±50 %, and each milestone keeps some time for fixing what you report). [S12]

- **Week 2 (by week 3):** fly over Millbrook in Unity and rev a real simulated engine on a test bench.
- **Weeks 3-4 (by week 5):** drive the trucks around Millbrook with engine-sim sound.
- **Weeks 5-6 (by week 8):** proper intersections with markings and STOP signs, and trucks that crash into buildings properly.
- **Weeks 7-9 (by week 11):** engine-sim actually drives the truck (it can stall, labour up hills and bounce off the limiter), and the town gets kerbs and sidewalks.
- **Weeks 10-11 (by week 14):** day and night with lit windows and street lamps; two windows on one PC can drive together.
- **Weeks 12-14 (by week 18):** traffic that obeys the signs, and a roundabout.
- **Weeks 15-16 (by week 21):** deep mud, low range, lockers and a winch.
- **Weeks 17-18 (by week 24):** drive with friends over the internet using a join code (no router setup), once you have created a free Unity Gaming Services project (U11a).
- **Weeks 19-21 (by week 27):** the whole Backroads game (freight jobs, money, fuel, saves), alone or online.
- After that, a new truck or race car every two to three weeks: semi with trailer, rally coupe, 6x6, GT car, trophy truck.

Millbrook comes out identical because Unity never re-creates it: the browser game exports its finished world once, every file is fingerprinted, and every test run checks the fingerprints. The browser game itself is never edited and keeps working with `Play.bat`.

**How the work runs (§1.11).** The weeks above assume the agents work around the clock while your laptop stays on, plugged in and awake. If they only work while you are at the laptop, expect two to three times longer. You start one coordinator session per milestone; anything that needs you (a download, a sign-in, a choice) collects in one list that you see once a day. [S1]

Your part: about an hour of play per build with a built-in checklist (press F10), and the decisions listed at the very end of this plan ("Decisions for you"). No agent ever uses your logins (Unity, Steam, Synty, GitHub, itch.io) or pushes code without your say-so.

---

## Why this plan

### Scores (1 = poor, 5 = excellent)

| Criterion | P-slice | P-risk | P-online | Why |
|---|---|---|---|---|
| User-visible progress | **5** | 2 | 3 | Slice: drivable in week 4, intersections week 6, engine-sim driving week 8, a build every 2 weeks. Risk: five plain "lab" builds, first drivable Millbrook in week 10. Online: drive in week 5, but engine-sim in weeks 9-13 and graphics in weeks 12-16 |
| Technical risk retired early | 4 | **5** | 3 | Slice proves the plugin and audio path in M0 and the coupling in week 8, but touches netcode only in week 16. Risk retires all five big risks by week 6 with numeric gates, timeboxes and pre-written fallbacks. Online retires netcode early but leaves engine-sim, the user's headline goal and the least-proven piece (01 §9 items 3-4, 06 §9.1), until weeks 9-13 |
| Architecture for free-to-play online | 3 | 4 | **5** | Slice: net-ready seams by convention only. Risk: frozen contracts with ADRs, integer stamps relative to canon heights (P-risk §2.2). Online: single player as a local host, a server build that compiles presentation out, reflection-based architecture tests, ownership transfer for coupled bodies, interest management, content handshake, conformance suites (P-online §1.4, §2.7, §5.3, §5.5) |
| Fidelity to the canon town and the user's goals | **5** | 4 | 4 | All three export instead of regenerating (03 §0). Slice delivers all four headline goals (Unity, intersections, engine-sim, graphics) inside 10 weeks. Risk and online put them behind infrastructure; risk delivers trucks in weeks 23-28 |
| Realism for an AI-agent team serving a non-developer | **5** | 3 | 3 | Slice: short milestones, one-hour playtests, a re-plan after the first milestone. Risk: five parallel spikes plus a Steam session with a friend in week 6. Online: ~15 % overhead up front; its M1 packs on-foot, dozer, winch and integer stamps into 25 AD |
| **Total (/25)** | **22** | 18 | 18 | |

**Spine: P-slice** (thin playable column through every hard system, then widen). It wins on the two criteria a non-developer feels most and is close on the rest.

### What was grafted, and from where

| Graft | From | Replaces in P-slice |
|---|---|---|
| New repo `D:\Backroads-Unity`; the JS repo is **never edited**: export hooks are applied to a temporary `git archive` snapshot | P-risk §1.1, P-online §1.7 (overlay on a temp copy) | Editing `js/props.js` in place inside the same repo |
| Hard gates with numeric thresholds, timeboxes, kill criteria and named fallbacks; gate result JSON | P-risk §0.1, §6.3, App. A | Mostly soft `[tune]` tolerances |
| F10 Playtest Checklist overlay that saves your answers for the agents | P-risk §1.7 | `PLAYTEST.md` only |
| `H = H_canon + DELTA_MM/1000` (delta relative to canon heights, not BASE) | P-risk §2.2, P-online §2.6 | Unstated reference (06 §5.2 said BASE, which breaks bit-identity at the 2,144 quarry-pile vertices, 03 §3.2) |
| Bit-exact LOD0 terrain vertex test; 12 × 4,096 camera-ray hits | P-risk S3-G3/G4 | Height probes only |
| Frozen `buildings.glb` and `trees.glb` through glTFast; glass merged at import by pane name | P-risk §2.5, 03 §3.3 | Re-exporting buildings from `buildings.py` in M4 (renumbers panes) |
| Engine-sim coast mode, `esp_get_stats`, `esp_debug_tap` | P-risk §3.2-3.3 | (absent) |
| BRV analytic test track, 1,000-seed stability fuzz, step-cost gate | P-risk S2 | (absent) |
| Client assemblies with `defineConstraints: ["!UNITY_SERVER"]`, architecture tests, a server canary from M0 | P-online §1.4 | Seams by convention |
| Single player through NGO local host (from M5, revision 2) | P-online §2.1 | Separate single-player path |
| `THIRD_PARTY_NOTICES` generated into every build, with a notices test (restored in revision 2) | P-online §1.2, §8 | Notices only at the end |
| Ownership transfer while bodies are coupled; interest management; content handshake; world persistence layout | P-online §5.3-5.6 | (absent) |
| Vehicle and engine conformance suites + editor content compiler (engine bake) | P-online §2.7 | (absent) |
| An automated **net probe** in M4-M5 (weeks 7-11; 4 instances, network simulator, stamp determinism), no Steam yet | P-risk S5 minus S5-G7 | Netcode first touched in week 16 |
| Both roundabout variants baked so you choose by looking | P-risk S4 | Decision on paper |

### New in this plan (none of the three proposals had these)

1. **No-install canon export.** Headless Microsoft Edge (already installed) loads the snapshot from a zero-dependency Node server, and the page POSTs every exported file back to that server. No Playwright, no npm install (§1.8).
2. **The team cannot open one Unity project twice.** Each lane works in its own git worktree with its own `Library`; at most 4 Unity processes at once; GPU and audio autotests are serialised by a lock; perf gates take the whole machine; agents back off while you play (§1.10).
3. **The installed editors lack IL2CPP and Dedicated Server modules.** All builds stay Mono until M14 [S18]. From M0 the server is the `ServerHeadless` profile: a Windows Mono player built with `UNITY_SERVER` defined, so server-only code is real and client code is compiled out [T15]. The shipped dedicated server is a Linux Mono build once you add that module (U4b) [C5].
4. **Sound-only rpm map for the first drive.** Instead of re-gearing the JS truck to fit the 454 (P-slice M1), engine-sim follows the classic model's rpm mapped linearly onto each engine's own idle-to-redline range. Classic physics stays verbatim; real gearing arrives with the coupling in M4.
5. **The 01 probe lives in a temporary session scratchpad.** You approve one copy of it to `D:\Backroads-ref\probe01\` before day 1 (a pre-approval, §7.2 U20), so it cannot vanish while the plan waits [S24].
6. **Engine coupling is timed in simulation steps, not wall-clock time.** Unity runs all fixed steps of a frame back to back, so the engine's torque is applied a fixed number of steps late (2-4) and every engine gate also runs at 30, 45 and 60 fps (§3.4) [T1].
7. **A min-spec proxy on this laptop.** From M1 every CPU gate also runs with the game restricted to 6 efficiency cores, Low tier, 30 fps cap (§1.9) [S8, T7].

### Rulings where the proposals or reports disagree

| Question | Options | Ruling | Evidence |
|---|---|---|---|
| Repository | same repo (P-slice) / new repo (P-risk, P-online) | New repo; oracle used read-only via `git archive` | 03 §0, §3.4 |
| Export automation | Playwright / manual / browser pane | Headless Edge + Node receiver; fallbacks: desktop-app browser pane, then you double-click `Export-Canon.bat` | 03 §3.4 |
| Deformation reference | BASE (06 §5.2) / canon H | Canon H | 03 §3.2 (H ≠ BASE at 2,144 vertices) |
| Per-building colours | MaterialPropertyBlock (03 §4.7) / material instances | Material instances; lit-window bits in the 6.3 Renderer Shader User Value, fallback one glass material instance per building | 04 §2.2, 07 §6.1 |
| Fork engine-sim for v1 | fork (02 §9, 06 §4.8) / no fork (01 §8) | No fork; all defects have plugin-side fixes; fork only for new physics, as a patch series | 01 §8 |
| Ship piranha (`.mr` scripts) | no (02 §9) / yes (01 §5.4) | Yes if you approve one download (U3c); C++ builders always available | 01 §5.4, 02 §4 |
| Local engine frequency | 20 kHz (02, 06) / script's own (01) | The script's authored `simulation_frequency`; tiers cut gas substeps 8 → 4 | 01 §6.2: LS −35 % and EJ25 +29 % at 5 kHz |
| Remote engines | FOLLOW_RPM (01 §6.3) / slaved PD (06 §4.6) | FOLLOW_RPM default, PD behind a flag, chosen by a listening test | P-risk §2.5 |
| Audio read | `readAudioOutput` in the filter (06 §4.4.6) / renderSync + SPSC ring (01 §4) | renderSync + lock-free ring | 01 §4.1 (the upstream read locks) |
| ABI | `bre_*` (06) / `esp_*` (01) | `esp_*` v1 | 01 §7.2 is a superset |
| Physics rate | 1/60-1/100 (04 §3.2) / 1/120 × 4 substeps (03, 06) | 1/120 × 4 | 03 §4.14, 06 §3.4 |
| First engine coupling | straight to SLAVED_CRANK / sound-only first | Staged: FOLLOW_RPM sound in M2, SLAVED_CRANK in M4 | P-slice §3.5 |
| Online timing | week 16 / spike weeks 2-6 / week 5-8 | Net-ready from M0, automated net probe in M4 (weeks 7-9), LAN preview in M5, playable alpha weeks 17-18 (revision 2) | 04 §3.2, 06 §5.6 |
| Terrain renderer | custom chunks (03 §4.3) / Unity Terrain (04 §2.2) | Custom chunks; Unity Terrain measured once for the record | 06 §5.1-5.2 |
| Damage dents | blendshapes (06 §6.4) / vertex deformation (07 §6.7) | Burst vertex deformation of a readable mesh | GPU Resident Drawer needs MeshRenderer (04 §2.2) |
| JS parity of ruts | byte-exact floats (03 §4.4) / integer kernel (06 §5.6) | Integer kernel in **int64 µm, stored as int32 µm** (not int16 mm); JS parity stated as an error curve over the call sequence and ±2 % volume [T2] | 06 §5.6; `js/world.js:137-177` moves heights by sub-mm amounts per call |
| Major road at junctions | straightest pair / class first | Class first (fixes nodes 2 and 5) | 05 §5.1 |
| Model transport | glTFast (03 §4.17) / FBX (07 §3) | FBX for new art; glTFast for frozen canon GLBs and today's vehicles until replaced | 07 §3.1 |
| Triangulation library | LibTessDotNet / own ear clipper | Own ear clipper + RingMesh (one fewer download); Clipper2 for offsets and unions | 05 §0, §5.5 |
| First vehicle through the art pipeline | pickup (P-risk) / box truck | Box truck: boxiest, highest procedural success, authentic gas V8 | 07 §8.1, 06 §4.8 |
| Editor DLL hot-reload | shadow-copy loader (P-online) / restart | Not in v1: native iteration uses `esp_cli` and ctest; Unity runs are fresh batchmode processes | 01 §7.4 |
| Coupling clock (revision 2) | one step late on a wall-clock cadence / a lag in steps | Lag `L` of 2-4 **steps**, bounded wait, every command queued, coast only on real hitches [T1] | Unity runs due FixedUpdates back to back; worker 2.9-4.6 ms per step (01 §6.2, 02 §6.3) |
| Steam wrapper (revision 2) | Facepunch + Platform Toolkit / one wrapper | **Facepunch.Steamworks only**, transport source vendored; no `com.unity.platformtoolkit.steam` [T6] | Platform Toolkit warns against a second Steam dispatch loop (04 §4 docs link); the community transport bundles its own Facepunch copy |
| Online with friends at M8 (revision 2) | direct IP / Unity Relay / Steam SDR on AppID 480 | Unity Relay join codes by default (U11a); direct IP as "advanced"; Steam from M10; AppID-480 builds never leave this PC [S7, C6] | 04 §3.5, §3.7; a real AppID needs 15-30 days of Steamworks paperwork (§5.7) |
| Single-player transport (revision 2) | own `LocalOnlyTransport` / NGO `SinglePlayerTransport` | NGO's `SinglePlayerTransport` [T17] | present in the bundled NGO 2.13.0 package |
| Scripting backend (revision 2) | IL2CPP from M5 / Mono | Mono for every build until M14; IL2CPP is an optional M14 decision [S18] | Windows-only game, no benefit before launch; one fewer Unity Hub action |
| Dedicated server OS (revision 2) | Windows / Linux | Linux Mono x64 (Windows optional), tested in this PC's WSL2 Ubuntu [C5] | a €5 VPS is Linux; P-online §1.5 had Linux |

---

## 1. Repository and project layout

### 1.1 New repository; the JS game stays the untouched oracle

- **Repo:** `D:\Backroads-Unity`, private GitHub `kaiwren986/Backroads-Unity` that you create when you want a remote (U1; recommended at M0 so the work has an off-machine copy). Agents commit locally; nobody pushes until you say so.
- **Backups until a remote exists** [S19]: `tools\ci\backup.ps1` runs nightly: `git bundle create --all` plus a copy of `.git\lfs\objects` to a backup folder you name (U21; a second drive or USB stick), keeping the last 7.
- **Oracle:** `D:\Backroads` at commit `552f7807f4ad56547815c32a5e8407a7c324d556`, recorded in `oracle.lock.json`. It is read with `git -c core.autocrlf=false archive` only, so the snapshot holds the committed LF bytes [T5]. `Play.bat` keeps working for side-by-side comparison.
- **Research and design docs** are copied (not moved) from `D:\Backroads\docs\unity\` into `docs\`.
- **Never committed:** reference photos (`D:\Backroads-ref\vehicle-refs\<id>\`, 07 §8.3), Synty packs and their imported folders in `Game\Assets` (§4.7), builds, artifacts, Unity `Library`, the Canon importer output (regenerated, §1.2).

### 1.2 Tree

```
D:\Backroads-Unity\
  README.md  AGENTS.md  CLAUDE.md  .gitattributes  .gitignore  oracle.lock.json  synty-manifest.json
  OWNERS.json                which lane owns each scene, prefab, ProjectSettings file and content folder [S11]
  third-party.json           licence registry: name, version, licence, source URL, files, shipped yes/no [C1]
  assets-provenance.json     every sound sample, font and texture not made here: URL, licence (CC0 / CC-BY only) [C1]
  content-provenance.json    one record per shipped asset: author (user / agent-script / agent-text / third-party), tool [C13]
  PRIVACY.md                 data inventory and deletion procedure (§5.9) [C7]
  CanonData\
    v1\                      the JS export, 03 §3.3 layout (LFS: *.f32 *.u8 *.png *.glb), manifest.json with SHA-256 per file
      terrain\probes.json    1,000 heightAt/normalAt/surfaceAt probes + 12 x 4,096 camera-ray hits
      gameplay\              vehicles.json (DEFS, META, derived ratios), jobs.json (CARGO, CARRIES, SUPPLY, DEMAND, TANK)
      reference\             static_props.glb, cameras.json, shots\*.png (12 cameras x 10:00/21:00), traces\*.json, deform\*.json
    v1.1\                    roads-overrides.json (hand-edited), road-plates.bin (sparse delta for H and BASE) + hash
    refs\<name>\             later oracle references with their own manifest (jobs\ payouts + fish timer at M9), so v1 never changes [S14]
    CHANGES.md               ledger of every approved deviation from v1: who, when, why, before/after hashes
  Game\                      THE UNITY PROJECT (Unity Hub: Add > D:\Backroads-Unity\Game)
    Assets\Backroads\<Assembly>\{Runtime,Editor,Shaders}\   one folder per assembly (§1.5)
    Assets\Backroads\World\Canon\v1\                        importer output (.bytes + ScriptableObjects), GITIGNORED: regenerated by
                                                            canon-import.ps1, cached by manifest hash, .meta GUIDs derived from the path [C15]
    Assets\Backroads\Roads\Baked\                           road bake output, committed, hash-checked
    Assets\Backroads\Scenes\  Boot, TestYard, Millbrook (thin bootstrap; content composed at runtime), EngineBench, BrvTestTrack,
                              LightingTest, Bake\MillbrookLighting (lighting-only bake scene, §4.1), Tests\*
    Assets\Backroads\Core\GameInfo.asset                    display title, company and product names, save-folder id (U18)
    Assets\Art\               Vehicles\<Id>\, Wheels\, Materials\Vehicle\, Shaders\, SyntyOverrides\ (07 §4.1)
    Assets\Content\core\      pack.json, VehicleDefinitions, EngineDefs, TyrePresets (P-online §2.7), bindings.json (§6.1 M2)
    Assets\Plugins\x86_64\    EngineSimPlugin.dll + EngineSimPlugin.version.json (committed at milestone tags only, LFS; §1.6)
    Assets\StreamingAssets\   EngineSim\es\** + engines\<fictional id>.mr (sanitised, §3.2) + ir\*.wav; Autotest\*.json; Playtest\M<n>.json
    Assets\ThirdParty\        Clipper2\ (BSL-1.0, with licence), Facepunch\ (MIT, from M10), Synty\ (GITIGNORED)
    Packages\manifest.json  packages-lock.json  ProjectSettings\ (written only by ProjectSetup.Apply)
  native\EngineSimPlugin\    C++ plugin (§1.6)
  art\
    vehicles\<id>\<id>.blend, brief.md, profile.json        (LFS) (07 §4.1, §8.2)
    trims\TS_Vehicle_A.blend
    pipeline\br_common.py br_generate.py br_uv.py br_bake.py br_lod.py br_validate.py br_export.py br_build.py br_canary.py
    legacy\vehicles.py trees.py buildings.py                 copied from the oracle snapshot; the originals stay untouched
  tools\
    oracle\                  make-snapshot.mjs, determinism.js, serve-export.mjs, exportUnity.js, goldenTraces.js, gen-hash.mjs,
                             run-export.ps1, Export-Canon.bat
    ci\                      env.ps1, env-check.ps1, *.ps1 gates (§1.9), notices.ps1, lfs-budget.ps1, backup.ps1, worktree-new.ps1
    build-vehicle.bat  Synty-Import.bat (you run it; agents never do, §4.7)
    winflexbison\            only after U3c; GITIGNORED
  docs\research\ docs\design\ docs\adr\ docs\spikes\ docs\milestones\ docs\gates\ docs\approvals\ docs\issues\ PLAN.md
  builds\                    GITIGNORED: builds\M<n>\Backroads.exe + PLAYTEST.md + THIRD_PARTY_NOTICES.txt + licenses\ + Play.bat, Host.bat, Join.bat
  artifacts\                 GITIGNORED: gate JSON, logs, WAV taps, screenshots, contact sheets, oracle snapshot, user-only\
```

**`.gitattributes`** [T5]:
- LFS: `*.f32 *.u8 *.bytes *.blend *.fbx *.glb *.png *.jpg *.exr *.wav *.ogg *.dll *.roadbake`.
- Line endings: `* text=auto eol=lf`, `*.bat *.cmd text eol=crlf`, and **`-text`** (bytes never converted) for every hashed tree: `CanonData/**`, `native/EngineSimPlugin/vendor/**`, `native/EngineSimPlugin/generated/**`, `Game/Assets/StreamingAssets/**`, `tools/oracle/traces/**`.
- Unity YAML: `*.unity *.prefab *.asset merge=unityyamlmerge`; T0.1 registers the editor's bundled `UnityYAMLMerge.exe` as that merge driver in the repo's local git config [S11].
- EditMode test `Repo.NoCrInHashedFiles` fails if any file under a hashed tree contains a CR byte. Reason: Git for Windows here has `core.autocrlf=true` system-wide, so without these rules every `git worktree add` would rewrite hashed JSON with CRLF and the Canon importer would abort.

`.gitignore` adds `Game/Library/ Game/Temp/ Game/Logs/ Game/obj/ Game/UserSettings/ Game/*.csproj Game/*.sln Game/Assets/Synty/ Game/Assets/Synty.meta Game/Assets/Backroads/World/Canon/ native/build*/ tools/winflexbison/ builds/ artifacts/ *.blend1`.

**LFS budget** [C15, S19]: Git LFS stores files uncompressed: canon v1 is roughly 100 MB raw (two 16.8 MB height layers, four 4.2 MB byte layers, 24 PNG shots, `static_props.glb`, probes, deform references), not the 30 MB gzip figure of 03 §3.3. Only CanonData is committed, never a second importer copy. The DLL is committed only at milestone tags (lanes build it, §1.6). `.blend` files and baked textures are committed at review points, not every iteration. `tools\ci\lfs-budget.ps1` runs in every milestone gate and fails above 6 GiB of LFS objects (GitHub Free allows 10 GiB storage and 10 GiB transfer per month, 07 §10.5); `git lfs prune` runs after each tag. You decide about paid LFS before the first push.

### 1.3 Unity project

- **Path:** `D:\Backroads-Unity\Game`.
- **Editor:** `C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe` from day 1. **One** update, to **6000.3.26f1** (or the newest 6.3 patch), which you install in Unity Hub (U4a) at the `m1` tag, **before the M2 worktrees are cut**, together with the U3a package bumps, so every `Library` re-imports once. If U4a is not done by then, the team stays on 6000.3.20f1 until the next milestone boundary [T21, S21]. **Never 6000.5** (end of life since 2026-09-02, 04 §1.1). Move to 6.7 LTS after its 2nd-3rd patch, at a milestone boundary (04 §1.2). The path lives only in `tools\ci\env.ps1`.
- **Created from** the bundled Universal 3D (URP) template (`-cloneFromTemplate ...\com.unity.template.3d-cross-platform-17.0.14.tgz`), then normalised by `Backroads.Build.Editor.ProjectSetup.Apply` (batchmode `-executeMethod`), which an EditMode test re-checks on every run:
  - Linear colour space; Universal Renderer on **Forward+**; SRP Batcher on; GPU Resident Drawer = Instanced Drawing; BatchRendererGroup Variants = Keep All (04 §2.2).
  - Enter Play Mode: "Do not reload Domain or Scene"; statics reset with `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` (04 §1.2 rule 1).
  - `Time.fixedDeltaTime = 1/120`, `Time.maximumDeltaTime = 4/120` (at most 4 catch-up steps per frame, so a slow frame cannot snowball into slower ones; below 30 fps game time slows instead) [S8]; `Physics.defaultSolverType = TemporalGaussSeidel` (06 §3.4).
  - Input: Input System only (package from M0) [S21]. Audio: 48,000 Hz, DSP buffer "Best latency" (01 §4.4).
  - Player: Windows x64; company and product names read from `GameInfo` (placeholders until U18; fixed before the first saves in M8, because Unity derives the save folder from them) [C12]; scripting backend **Mono** for every build until M14 [S18]; Burst on; Asset Serialization Force Text.
  - Privacy [C7]: `PlayerSettings.submitAnalytics = false` (hardware statistics off), Cloud Diagnostics off, no Unity Analytics package; asserted by `Build.ProjectSettings` and by autotest `m0.net.egress` (§1.9).
  - Forward-compatibility rules (04 §1.2): never store `GetInstanceID()`, Read/Write on every MeshCollider mesh, never embed Burst, no `com.unity.serialization`, no dynamic batching.
- **Physics layers:** `Static`, `Road`, `Vehicle`, `VehicleHull`, `Wheel`, `Prop`, `Trigger`, `TerrainProxy`, `Puppet`. Matrix per 06 §5.2: `VehicleHull` and `Wheel` never collide with `TerrainProxy` (BRV handles ground against WorldGrid); `Wheel` collides with `Static`, `Vehicle`, `Prop`, `Puppet` only.

### 1.4 Packages

M0 pins the versions **bundled** with 6000.3.20f1 (no download needed). After U3a, they move to the patch versions checked in 04 §5.

| Package | M0 pin (bundled) | Target pin (04 §5) | From | Purpose |
|---|---|---|---|---|
| `com.unity.render-pipelines.universal`, `com.unity.shadergraph`, `com.unity.visualeffectgraph` | 17.3.0 (core) | 17.3.0 | M0 | URP, Shader Graph, VFX |
| `com.unity.burst` | 1.8.29 | 1.8.31 | M0 | WorldGrid, meshing, BRV, stamps |
| `com.unity.collections` / `com.unity.mathematics` | 2.6.6 / 1.3.3 | 2.7.1 / 1.3.3 | M0 | NativeArrays, `float3` |
| `com.unity.inputsystem` | 1.19.0 | 1.20.1 | M0 [S21] | Fly camera and Engine Bench (M1); Driving, OnFoot, UI maps, rumble (M2) |
| `com.unity.test-framework` / `.performance` | editor default / 3.5.0 | record in `packages-lock.json` | M0 | EditMode, PlayMode, Perf |
| `com.unity.cloud.gltfast` (Apache-2.0, in the notices) | not bundled | 6.20.0 (not 7.0.0-exp.1) | M1 | Frozen canon GLBs, today's vehicles |
| `com.unity.formats.fbx` | 5.1.6 | 5.1.6 | M3 | FBX round trips |
| `com.unity.splines` | 2.9.0 | 2.9.1 | M3 | Gizmos and nearest-point helpers only (05 §0) |
| `com.unity.netcode.gameobjects` | 2.13.0 | **2.13.3** | M4 (net probe) | Netcode |
| `com.unity.transport` | 2.7.3 | 2.7.4 | M4 | UDP, loopback, debug simulator |
| `com.unity.multiplayer.playmode` / `.tools` | 2.0.2 / 2.2.9 | 2.0.2 / 2.2.12 | M4 | Virtual players, network simulator |
| `com.unity.dedicated-server` | 2.0.2 | 2.0.2 | M0 (profile), M12 (build) | Server profile, Multiplayer Roles |
| `com.unity.memoryprofiler` | 1.1.12 | 1.1.12 | M5 | Memory budgets |
| `com.unity.services.multiplayer` | 2.2.4 | 2.4.1 | M8 (default, U11a) [S7, C6] | Unity Relay join codes for friend sessions before Steam |
| Facepunch.Steamworks (MIT), **one copy** (`Facepunch.Steamworks.Win64.dll` + one `steam_api64.dll`) | release zip | 2.5.2 | M10 (U3d) | Steam lobbies, SDR P2P, auth tickets, achievements (`SteamUserStats`), Steam Cloud (`SteamRemoteStorage`) [T6] |
| NGO Facepunch transport (MIT) | **source only**: `FacepunchTransport.cs` (~400 lines) vendored into `Backroads.Net.Steam`, compiled against the single 2.5.2 copy; the package's own bundled Facepunch 2.3.2 DLLs are not imported | 2.0.0 source | M10 (U3d) | NGO transport over SDR; EditMode `Net.SingleSteamApi` asserts exactly one `steam_api64.dll` and one Facepunch assembly [T6] |
| Clipper2 C# source (BSL-1.0) | source | latest release tag, pinned at M3 (U3b) | M3 | Kerb loops, sidewalks, swept paths |
| `com.cqf.urpvolumetricfog` (MIT, OpenUPM) | — | latest, re-test on 6.7 | M5 optional | Volumetric fog on High |

**Not used:** Cinemachine (camera is a straight port, 03 §4.19), `com.unity.vehicles`, `WheelCollider` (06 §3.1), Entities and Netcode for Entities (04 §3.4), Unity Terrain for rendering, LibTessDotNet, Playwright, googletest/doctest (plain ctest executables), HDRP (04 §2.1), `com.unity.platformtoolkit.steam` (it runs its own Steam dispatch loop, which its documentation says must not run beside another wrapper's) [T6], Unity Analytics and Cloud Diagnostics [C7].

Every package and plugin above is listed in `third-party.json`; `Licence.NoticesComplete` (§1.9) fails when anything in `packages-lock.json` or under `Plugins/`, `StreamingAssets/EngineSim/`, `Fonts/` or `Audio/` has no entry [C1].

### 1.5 Assembly definitions

Shared assemblies compile into every build, including the server. Client assemblies carry `"defineConstraints": ["!UNITY_SERVER"]` and plug in at start-up through `Backroads.Core.PresentationRegistry` (`[RuntimeInitializeOnLoadMethod(AfterAssembliesLoaded)]`) (P-online §1.4). References only point down the table.

| Assembly | Kind | References | Owns (key types) |
|---|---|---|---|
| `Backroads.Core` | shared | Mathematics, Collections, Burst | `BrCoords` (03 §2), `SimTick`, `SimClock`, `IClock`, `ContentId`, `CommandLine`, `FeatureFlags`, `Log`, `PresentationRegistry`, `IVehicleEngine`, `EngineState`, `GameInfo` |
| `Backroads.World` | shared | Core | `WorldGrid`, `WorldGridQueries` (Burst float fast path for physics: `HeightAt`, `NormalAt`, `SurfaceAt`, `MudAt`, `SinkAt`, `RayTerrain`; plus `WorldGridQueriesRef`, a **double-precision** reference path that mirrors JS arithmetic for parity tests [T14]), `SurfaceTable`, `CanonRuntime`, `ColliderGrid`, `WorldInfo`, `PlacementSet`, `VegetationSet`, `LegacyTrafficNetwork` |
| `Backroads.Deform` | shared | Core, World | `RutStamp` (18 B, `Pack = 1`) [T13], `IStampSink` (local apply now, host-ordered log online), `StampKernels` (integer µm `Rut`, `Brush`, `CutBlade`, `Deposit*`, `Relax`) [T2], `StampLog`, `WorldTiles` (`TileMeta {version, hash}`), `DirtyRectQueue` |
| `Backroads.Roads` | shared | Core, World | `RoadNetworkAsset`, `LaneGraph` (`Lane`, `Connector`, `Conflict`), `ControlSet`, `SignalController`, `RoadPlates` (applied to WorldGrid at load) |
| `Backroads.Vehicles` | shared | Core, World, Deform | BRV: `ClassicVehicleDef`, `VehicleDefinition`, `DrivelineDef`, `TyrePreset`, `VehicleSetup`, `VehicleControls`, `VehicleStatus`, `BrvState`, `VehicleSimSystem`, `VehicleStepJob`, `Row`, `ShaftGraph`, `TyreModel`, `MudModel`, `TransmissionController`, `Winch`, `HitchDef`, `LegacyJsEngine`, `TorqueMapEngine`, `ReplicatedEngineState` |
| `Backroads.Traffic` | shared | Core, World, Roads, Vehicles | `TrafficSystem`, `TrafficDriver`, `JunctionArbiter`, `GhostAgent`, `TrafficLaneState` |
| `Backroads.Gameplay` | shared | Core, World, Vehicles | `EconomyRules`, `FreightBoard`, `Offer`, `JobLedger`, `Wallet`, `IEconomyAuthority` + `LocalEconomyAuthority`, `FuelStation`, `TowService`, `FootController` (on-foot logic) |
| `Backroads.Persistence` | shared | Core, World, Deform, Vehicles, Gameplay | `SaveService`, `WorldSave`, `PlayerProfile`, `SaveMigrations` |
| `Backroads.Net` | shared | all shared + `Unity.Netcode.Runtime`, `Unity.Networking.Transport` | `SessionLauncher` (NGO's `SinglePlayerTransport` for single player [T17]; UTP for LAN, Relay and servers), `ContentHandshake`, `VehicleStateCodec`, `SnapshotBatcher` (one message per recipient per 20 Hz tick [T18]), `SnapshotBuffer`, `RemoteVehiclePuppet`, `ContactImpulseRelay`, `StateValidator`, `DeformReplicator`, `TrafficReplicator`, `NetEconomyAuthority`, `ClockService`, `OwnershipBroker`, `InterestManager` |
| `Backroads.Net.Steam` | shared, `BACKROADS_STEAM` | Net, Facepunch.Steamworks | `SteamBootstrap` (the only `SteamClient.Init`), `FacepunchTransport` (vendored source), `SteamLobbyService`, `SteamAuth`, `SteamStats`, `SteamCloud` [T6] |
| `Backroads.Game` | shared | shared + Net | `GameBootstrap` (composition root), `GameLoop` (system order of 03 §4.27), `QualityPreset`, `CrashCollector` and `SessionMetrics` (in every build, §1.9) [S5], `SmokeRunner` (`-br-smoke`, in every build) [S18] |
| `Backroads.Server` | `UNITY_SERVER` | Game | `ServerMain` (CLI, `server.cfg`, autosave, ban list); present in the `ServerHeadless` build from M0 [T15] |
| `Backroads.EngineSim` | client | Core | `Native` (P/Invoke), `EngineSimEngine : IVehicleEngine`, `ReplicatedEngine`, `EngineSimAudio`, `AudioTap`, `EngineAudioScheduler`, `LoopBankPlayer`, `EngineCatalog`, `EngineDef` |
| `Backroads.Client.Rendering` | client | Core, World, Deform, Roads | `TerrainChunkSystem`, `TerrainChunkBuildJob`, `BuildingSpawner`, `PropSpawner`, `VegetationRenderer`, `GrassSystem`, `RoadChunkLoader`, `DayNightPresenter`, `WaterSurface`, `NightLights`, `QualityTiers` |
| `Backroads.Client` | client | shared + EngineSim + Client.Rendering | `ChaseCameraRig`, `HoodCamera`, `FreeFlyCamera`, `FootCamera`, `InputBindings` (from `bindings.json`), `SettingsStore` (`settings.json`, versioned) [S13], `VehicleVisual`, `WheelVisuals`, `CharacterVisual`, `EffectsEmitter`, `NonEngineAudio` |
| `Backroads.Client.UI` | client | Core, Vehicles, Gameplay, Net | UI Toolkit: `HudController`, `MinimapView`, `BigMapView`, `FreightBoardPanel`, `DebugOverlay` (F3), `PauseMenu` (Esc: Resume / Controls / Settings / Credits / Quit), `ControlsOverlay` (F1), `SettingsPanel`, `CreditsPanel` (notices and OSM credit), `GraphicsMenu` (F2), `SessionMenu`, `PlaytestChecklist` (F10, in every build) |
| `Backroads.Testing` | dev builds (`BACKROADS_TESTING`) | all runtime | `AutotestRunner`, `ScenarioRunner`, `MetricsRecorder` (full), `CameraBookmarks`, `ContactSheet`, `GoldenTraceRecorder`, `BotClient`, cheat client |
| Editor: `Backroads.Canon.Editor`, `Backroads.Roads.Editor`, `Backroads.Content.Editor`, `Backroads.Art.Editor`, `Backroads.Build.Editor` | editor | as needed (+ Clipper2) | `CanonImporter`; road bake stages 1-12 (05 §3); `ContentBuild`, `EngineBake`, conformance runner; `BackroadsModelPostprocessor`, `VehicleJsonImporter`, `VehiclePrefabBuilder`, `CaptureTurntable`; `BuildCli`, `ProjectSetup` |
| `Backroads.Tests.EditMode`, `.PlayMode`, `.Perf` | tests | as needed | Categories: Architecture, Repo, Canon, World, Deform, Roads, Vehicle, Coupling, EngineSim, Audio, Traffic, Net, Save, Look, Art, Content, Licence, Privacy, Perf, Slow, PlayerOnly |

**Rules, enforced by `Backroads.Tests.EditMode.Architecture` (reflection over compiled assemblies plus a source scan):**
1. Only `Backroads.Net` and `Backroads.Net.Steam` reference `Unity.Netcode.Runtime`.
2. No shared assembly references `Backroads.EngineSim` or any `Backroads.Client*`.
3. `Backroads.Vehicles` sees engines only through `IVehicleEngine`; BRV tests run without the DLL.
4. No `MaterialPropertyBlock` anywhere under `Assets/Backroads` (GPU Resident Drawer, 04 §2.2).
5. No stored `GetInstanceID()`.
6. Shared state changes only through authority interfaces (`IEconomyAuthority`, `IStampSink`, `IClock`); a source scan flags `Wallet.money =` outside the authority classes.
7. **Server compile check** (`Backroads.Build.Editor.BuildCli.CompileServerScripts`): `PlayerBuildInterface.CompilePlayerScripts` with `ScriptCompilationSettings.extraScriptingDefines = {"UNITY_SERVER"}` must succeed. This is the fast check in the merge tier; the real server is the `ServerHeadless` build below.
8. **Server contents** [T15]: `Build.ServerContents` inspects the `ServerHeadless` player (a Windows Mono player built with `BuildPlayerOptions.extraScriptingDefines = {"UNITY_SERVER"}`; from U4b also the Linux Mono server): its `Managed\` folder contains no `Backroads.Client*` or `Backroads.EngineSim` assembly and its plugins folder has no `EngineSimPlugin.dll` (the plugin importer excludes it under `UNITY_SERVER`). Servers therefore cannot load engine-sim, by construction rather than by a runtime flag.

### 1.6 Native plugin source

```
native\EngineSimPlugin\
  CMakeLists.txt  CMakePresets.json        our own CMake; never add_subdirectory(upstream) (01 §1.4; 02 §2)
  vendor\engine-sim\                       byte-identical copy of the git blobs of D:\Backroads-ref\engine-sim @ 85f7c3b (taken with
                                           `git -c core.autocrlf=false archive`, never from the CRLF working tree [T5]): src, include,
                                           scripting, es, assets\engines, LICENSE; submodules simple-2d-constraint-solver @ e009f4f and
                                           piranha @ 432f0b1 (delta-studio, csv-io, direct-to-video omitted)
  vendor\VENDOR.md, vendor\LICENSES\       commits; MIT texts for engine-sim, scs, piranha; flex's BSD notice from FlexLexer.h.
                                           piranha 432f0b1 has no LICENSE file: its LICENSE was added in 004fa15 (2022-08-16), whose
                                           only change against 432f0b1 is that file. VENDOR.md records this with the compare URL and
                                           vendor\LICENSES\piranha-LICENSE is that file (U3e); vendor-check allows exactly this extra [C9]
  shim\delta-studio\include\yds_core.h, shim\delta-studio\engines\basic\include\delta_basic_engine.h   empty (01 §1.3)
  generated\piranha\                       scanner.auto.cpp, parser.auto.cpp, parser.auto.h (+ bison location/stack headers), after U3c
  src\
    EngineSimPlugin.h, esp_api.cpp         C ABI v1 (§3.3); every call returns a status and catches all C++ exceptions
    unity_sim.h/.cpp                       UnitySim : PistonEngineSimulator: writeToSynthesizer() without lastValveLift[8],
                                           attach(), delay-filter re-init, synthesizer re-init at 48 kHz (01 §7.3, §8)
    unity_transmission.h                   clutchTorque() accessor (01 §3.2)
    generators.h                           SlavedCrankPD, WheelLoad (01 App. A.4)
    instance.h/.cpp, worker.cpp            worker thread, SPSC command/result rings (32 steps), step-indexed results, fractional
                                           step accumulator, coast mode with borrowed-step reconciliation (§3.4)
    crash_guard.cpp                        SEH filter on every plugin thread: MiniDumpWriteDump to %TEMP%\<company>\<product>\Crashes\ [S5]
    audio_render.h/.cpp, spsc_ring.h       renderSync (01 §4.3), float ring, drift trim, underrun decay, WAV tap
    fir_avx2.cpp, fir_sse2.cpp             CPUID-dispatched FIR (01 App. A.5)
    wav_io.cpp, resample.cpp               own 16-bit WAV reader; 44.1 -> 48 kHz IR resampling
    script_loader.cpp, piranha_path_std.cpp   piranha wrapper (absolute paths, compile mutex, output reset, wrapper .mr) (01 §5.4, A.6)
    builders\f4_boxer_a.cpp, v8_cross_a.cpp, v8_bigblock_a.cpp, i4_synth.cpp   C++ engine builders under fictional ids [C2]
                                           (the EJ25 port of 02 §4, the LS of 01 App. A.1, the 454 port, the 01 synthetic I4)
    mr_sanitise.cpp                        rewrites name: fields and node identifiers and strips comments in shipped .mr files [C2]
    offline.cpp                            esp_calibrate_inertia, esp_dyno_sweep, esp_render_offline (EngineBake and tests only, §3.5)
  tools\esp_cli.cpp                        load, sweep, calibrate, render-loops, render-wav, follow, cosim, bench (--affinity P|E), selftest
  tests\                                   ctest executables: esp_smoke, esp_dyno, esp_follow, esp_ring_stress, esp_step_accounting
                                           (timeScale 1 and 0.5), esp_cosim_matrix (01 §3.5 re-run with lag L = 1-4 and recorded frame-time
                                           traces, §3.4), esp_all_engines (piranha, report), esp_soak (ASan preset)
  probes\p01\, probes\p02\                 evidence: 01's bench.cpp/shim/piranha_path_std.cpp (from D:\Backroads-ref\probe01, U20); 02's probe
  patches\                                 EMPTY in v1; future fork work (turbo, engine brake, diesel) as a patch series applied to a build copy
```

- **Flags** (01 §7.1): `/MT`, `NDEBUG` always (upstream Debug does not compile, 02 §8), `/O2 /fp:fast`, C++17, `/arch:AVX2` only on `fir_avx2.cpp` with an SSE2 fallback. Presets: `vs2022-x64` (Release) and `vs2022-x64-asan` (`/fsanitize=address`) for soaks.
- **Targets:** `scs` (static, 29 files), `enginesim_core` (static, 41 files, shim first on the include path), `piranha` and `es_script` (static, only with `-DESP_WITH_PIRANHA=ON`), `EngineSimPlugin` (shared), `esp_cli`, tests.
- **Output:** `tools\ci\build-native.ps1` copies the DLL to `Game\Assets\Plugins\x86_64\` and writes `EngineSimPlugin.version.json` (ABI, source hash, piranha on/off). It refuses to overwrite a DLL that an open editor has locked and says so (01 §7.4). Builds are cached in `artifacts\native-cache\<source hash>\`, and `worktree-new.ps1` builds or restores the DLL for each new worktree. The DLL is **committed only at milestone tags** so a clone of a tag plays without a C++ build [S19]; `Native.Selftest` compares `esp_build_info()` with the version file and fails on a stale DLL.
- **Windows x64 only.** Steam Deck runs it under Proton (04 §2.3); servers never load engine-sim (01 §6.3).

### 1.7 Blender pipeline

- **Blender:** `%LOCALAPPDATA%\Programs\Blender Foundation\Blender 4.5\blender.exe`, always `-b --factory-startup` (03 §4.28). Cycles + OptiX on the RTX 5080 for bakes, about 10-60 s per 2048 map (07 §5.1).
- **Scripts:** `art\pipeline\br_*.py` wrap the copied `art\legacy\vehicles.py` builders (07 §5.1): save a `.blend`, drop `SOCKET_*` empties, consolidate to ≤ 9 materials via `SLOT_MAP`, LOD0-3 from subdivision levels, UV0/UV1, Cycles bakes, `br_validate` against the 07 §8.4 budgets, export.
- **Command:** `tools\build-vehicle.bat <id>` = `"%LOCALAPPDATA%\Programs\Blender Foundation\Blender 4.5\blender.exe" -b art\vehicles\<id>\<id>.blend --factory-startup --python art\pipeline\br_build.py -- --id <id> --unity D:\Backroads-Unity\Game`.
- **Outputs:** `Game\Assets\Art\Vehicles\<Id>\VH_<Class>_<Variant>.fbx` (07 §3.2 flags), `<id>.vehicle.json` (wheel sockets in Unity space, tyre sizes, bounds, tris per LOD), `T_*.png`, `build_report.json`, a 6-view EEVEE sheet, a proportion-review sheet (side and front silhouettes next to each inspiration's silhouette at the same wheelbase, for review only, never as a tracing guide) [C4], a Unity batchmode turntable at noon/dusk/night × mud 0/0.5/1 (07 §5.2). GLB is also written (mods later).
- **Unity side:** `BackroadsModelPostprocessor` (material remap by name, `UCX_` convex hulls, custom props → `BrNodeProps`), `VehicleJsonImporter` (ScriptedImporter, sidecar → `VehicleDefinition` geometry), `VehiclePrefabBuilder` (generated base prefab + your Prefab Variant) (07 §3.3, §4.4). The **axis canary** (`MARK_Front.z > 0`, `MARK_Left.x < 0`, `MARK_Up.y > 0`, identity root, scale 1) gates every import (07 §3.2).

### 1.8 Data export tool, and how the three.js build stays the canon oracle

1. **Never edited, never regenerated.** Unity only imports the exported final state. A C# re-implementation would shift the RNG stream and move every later tree (03 §0.1, §3.1).
2. **Snapshot, not checkout.** `node tools\oracle\make-snapshot.mjs` runs `git -c core.autocrlf=false -C D:\Backroads archive 552f7807` into `artifacts\oracle\552f780\`, then applies the export hooks **to the snapshot only**, each as an exact-match replacement that fails unless its anchor occurs exactly once (all anchors are unique today):
   - `const trees = [];` → append `export const treeList = trees;`
   - `const spots = [];` (in `buildRocks`) → also set `props.rocks = spots;`
   - `function rec(...op) { if (REC) REC.push(op); }` → also append every op to `props.allOps` (created as `[]`).
   - **Typed-prop wrappers** [T3]. 03 §3.4 said `allOps` captures typed props with their final `y`; it does not. `rec()` is called only for model, box, fence, parking, concrete, claim, mailbox and tree (`js/props.js:769-815`), `rec('model', …)` carries no `y`, `sx` or `sz` (line 407), and `lamp()` stores only the bulb head (lines 485-495). So the snapshot renames each of these builders to `<name>__o` and adds a wrapper that records its arguments and pure reads, then calls the original:
     - `placeModel` → after the call, `props.typed` gets `{type:'model', name, x, z, rot, y, sx, sz, M: elements}` from its returned `{M, y, sx, sz}`;
     - `lamp` → `{type:'lamp', x, z, faceRot, h, y: W.heightAt(x, z) − 0.1}` before the call;
     - `silo`, `dock`, `fuelStation`, `crusher`, `sawShed` → their arguments (`dock` covers the quay crane, whose pose follows from `q`).
     - Pieces placed inline or by RNG scatter (the crane geometry, the fountain, container stacks, hay bales, log piles) are not given transforms: they render from `reference\static_props.glb`, and the gate checks that file's 7 bucket vertex counts and hashes (03 §3.2) instead.
     - Wrappers call no RNG. The export proves it: all layer hashes, all counts and `rngCalls = 1220` (03 §3.2) must be unchanged.
   - Injects `determinism.js` as the **first** module in the snapshot's `index.html`, before the game's module [T4]: `Math.random` becomes a seeded mulberry32 (seed recorded in the manifest; the world already uses its own mulberry32 streams, so world hashes are unaffected, which the export proves); `performance.now` and `Date.now` read a scripted clock; `requestAnimationFrame` is wrapped so the exporter can freeze the loop and step it explicitly. Every `Math.random` draw can be logged, so oracle references that consume random numbers (traffic, star placement, job offers) can be replayed in C# with the same uniforms.
   - Copies `exportUnity.js` and `goldenTraces.js` into the snapshot's `js\` and injects their module tag.
3. **No-install automation.** `tools\oracle\run-export.ps1` starts `node tools\oracle\serve-export.mjs --root artifacts\oracle\552f780 --out CanonData\v1 --port 8131` (zero dependencies, like the oracle's `server.mjs`; binds 127.0.0.1 only; serves files and accepts `POST /__export/<relpath>`, path-sanitised, plus `POST /__export/done`), then launches `msedge.exe --headless=new --no-first-run --user-data-dir=artifacts\oracle\edge-profile --window-size=1600,900 "http://127.0.0.1:8131/index.html?export=unity"`. If headless WebGL fails, it retries with `--use-angle=swiftshader --enable-unsafe-swiftshader` [verify on day 3]; the manifest records which renderer ran (`WEBGL_debug_renderer_info`), and SwiftShader shots are re-captured through the next fallback [S16]. Fallbacks, in order: the desktop app's browser pane (agent-driven, 03 used it at port 8130), then you double-click `tools\oracle\Export-Canon.bat` (normal Edge window). three.js 0.170.0 still comes from jsDelivr exactly as `Play.bat` does; the page itself fetches the module URL again, hashes it with `crypto.subtle.digest('SHA-256')` and POSTs the hash, because the Node server never sees that request [T4].
4. **What `exportUnity.js` writes** (03 §3.3 plus), in this order [T4]:
   1. freezes the game loop (wrapped `requestAnimationFrame`, `G.noRender`) and the scripted clock, checks `sha256(W.WEAR) = 85473f6f…` (03 §3.4), and **in the same synchronous task** copies every array it will export with `slice()`, so traffic and the 60 Hz deformation cannot change H, WEAR or SURFACE between the check and the POSTs;
   2. POSTs from the copies: the 7 terrain layers; `world.json`, `placements.json` (with `panes` names, `lit` strings and the `typed` records above), `vegetation.json`, `traffic.json`; `textures\*.png`; `reference\static_props.glb`; `source\canon.json`, `source\townProfile.json`; `terrain\probes.json` (1,000 seeded probes + 12 × 4,096 ray hits); `gameplay\vehicles.json`, `gameplay\jobs.json`; `reference\cameras.json`;
   3. renders `shots\*.png` (12 cameras × 10:00/21:00) with the clock frozen, so tree wind, water and grass do not move between runs [S16];
   4. **last**, because they mutate the live module arrays: `reference\deform\` (10,000 seeded `rut`/`brush` calls with the resulting sparse height deltas, for the parity test).
   The job-payout and fish-timer references move to M9 (`CanonData\refs\jobs\`, own manifest, produced by calling `jobs.js` with a logged uniform stream that the C# test replays) [S14, T4].
5. **Golden traces.** `?trace=<id>` feeds a scripted input file through `G.update(1/120)` with `G.noRender`, the seeded PRNG and **no AI traffic** (the harness removes all traffic cars before the first step), and POSTs `pos/quat/vel/rpm/gear/mudSink` every 0.1 s (03 §3.6) [T4]. Traces are recorded on v1 terrain, so Unity replays them with `FeatureFlags.RoadPlates = off` and `RoadColliders = off` (pure v1); from M3 a separate Unity self-trace baseline on v1.1 guards regressions [S17]. The input files (`tools\oracle\traces\*.json`) are replayed unchanged by Unity's `AutotestRunner`.
6. **Quick check without a browser.** `node tools\oracle\gen-hash.mjs` imports the snapshot's `world.js` in Node 24 and checks H, BASE, ROADD, MUD, WEAR, TINT in 0.6 s (03 §3.2 measured Node and Chromium bit-identical). It runs in every CI pass.
7. **Canon changes are versioned overlays.** v1 is the export. v1.1 is `roads-overrides.json` + `road-plates.bin`, applied in one importer step to **both** `H0` and `BASE` (05 §5.10), with both results hashed [T10]. Tests: **every height outside the road-plate mask is bit-identical to v1**; all 59 lot pads stay within 0.05 m (P-risk S4-G5); `Canon.BaseMatchesH`: `BASE − H0` after plates equals v1's `BASE − H` at every vertex (zero everywhere except the 2,144 quarry-pile vertices, which plates never touch), so mud sinkage, dug tint, grass rejection and rut targets see no phantom change. Every deviation gets a `CanonData\CHANGES.md` entry with your approval (P-online §1.7).
8. **What "identical" means.** Content and layout identical: heightfield hashes, road polylines, lots, buildings, colours, lit-window flags, trees, rocks, lamps, colliders (hashes and counts). Pixels are not identical (new renderer); the 12-camera contact sheets show Unity next to JS. Intersections *add* geometry and may only change ground inside road footprints + 15 m (U15).

### 1.9 CI and batchmode scripts

CI is local (cloud CI would need your Unity licence and sign-in). Every script writes `artifacts\<yyyyMMdd-HHmm>\…`, exits non-zero on a failed hard gate, and writes `artifacts\gates\<gate>.json` including its wall-clock time against its budget.

**Three tiers** [S10], so 4-5 lanes can merge in a day:

| Tier | When | Budget | Contents |
|---|---|---|---|
| `gate.ps1 -Tier merge` | every merge into `main` | ≤ 20 min | oracle hash check (Node), vendor check, native ctest (not soaks), EditMode, PlayMode `!Slow`, server compile check, Dev player build, `smoke` autotest |
| `gate.ps1 -Tier nightly` | 02:00 (or when the machine is idle), on `main` | ≤ 3 h | everything `Slow`: soaks, fuzz, perf on an idle machine (median of 3 runs), min-spec proxy, fps-variant coupling runs, `ServerHeadless` build and soaks, LFS budget, backup |
| `gate.ps1 -Tier milestone -Gate M<n>` | milestone end | — | last green nightly + Client build + the milestone's headline gates on that exact build |

**Machine locks** [S9]:
- `Play.bat` (and every launcher you use) creates `artifacts\.user-playing` while the game runs. While it exists, agents start no Unity build, Blender bake, soak or perf gate, and running ones pause at their next checkpoint, so your playtest does not stutter.
- Perf gates take the exclusive `artifacts\machine.lock`: no other Unity, Blender or player process may run.
- The gate JSON `machine` field records power source, active GPU (discrete or integrated), Windows power mode, other heavy processes and P/E-core residency. A perf run on battery or on the integrated GPU is marked **invalid**, not failed, and is re-run later.

**Min-spec proxy** [S8, T7]: `autotest.ps1 -MinSpec` starts the player with its process affinity restricted to 6 efficiency cores (which logical processors are E-cores is read once by `env-check.ps1` through `GetLogicalProcessorInformationEx`), Low tier, `-br-fps-cap 30`, 1920×1080. 01 §6.2 and 02 §6.3 measured this laptop's E-cores 25-30 % slower than its P-cores, so this stands in for a typical 6-core player PC until you test a real one (U14). An E-core here is still about 1.4× faster than the "half this machine's per-core speed" PC of 02 §9, which is why proxy gates ask for an engine real-time factor of at least 1.5: that is about 1.05 on the half-speed PC.

| Script | Runs |
|---|---|
| `tools\ci\env.ps1` | `$UNITY`, `$BLENDER`, `$NODE`, `$EDGE`, `$PROJ = D:\Backroads-Unity\Game`, oracle path |
| `tools\ci\env-check.ps1` | Versions and modules → `artifacts\env.json`; fails on missing tools |
| `tools\ci\vendor-check.ps1` | `git hash-object --no-filters` of every vendored file vs the blob ids from `git -C D:\Backroads-ref\engine-sim ls-tree -r 85f7c3b` (and the two submodules at their commits), so the result does not depend on anyone's `autocrlf` [T5]; the only allowed extra is `vendor\LICENSES\piranha-LICENSE` [C9]; any other difference fails |
| `tools\ci\build-native.ps1 [-Asan]` | `cmake --preset vs2022-x64 -S native\EngineSimPlugin`; `cmake --build --preset release --parallel 16`; `ctest --preset release --output-on-failure`; copy DLL |
| `tools\ci\oracle.ps1` | `make-snapshot.mjs`, `gen-hash.mjs`; with `-Export`, `run-export.ps1` and manifest verification |
| `tools\ci\canon-import.ps1` | `-executeMethod Backroads.Canon.Editor.CanonImporter.ImportCli -canon D:\Backroads-Unity\CanonData\v1`; skipped when the cached output's manifest hash matches |
| `tools\ci\worktree-new.ps1 -Lane <x> -Topic <t>` | `git worktree add`, native build or cache restore, canon import, first Library import; prints the time taken |
| `tools\ci\unity-tests.ps1 -Platform EditMode` (or `PlayMode`) `[-Category X]` | see commands below |
| `tools\ci\roads-bake.ps1` | `-executeMethod Backroads.Roads.Editor.RoadBakeCli.Bake -verifyHash` |
| `tools\ci\build-player.ps1 -Profile Client` (or `Dev`, `ServerHeadless`, from U4b `ServerLinux`) `-Milestone M<n>` | `-executeMethod Backroads.Build.Editor.BuildCli.Build` → `builds\M<n>\` + `PLAYTEST.md` + `THIRD_PARTY_NOTICES.txt` + `licenses\` + `.bat` launchers |
| `tools\ci\autotest.ps1 -Exe <exe> -Scenario <id> [-MinSpec] [-Fps 30\|45\|60]` | runs the player with `-br-autotest`; takes the GPU/audio lock (and `machine.lock` for perf) |
| `tools\ci\soak.ps1 -Clients 4 -Minutes 10 -Net 150,20,2 [-ServerLinux]` | `ServerHeadless` server + N bot clients, **all bound to 127.0.0.1** (`-br-bind 127.0.0.1`) so Windows Firewall never prompts [S23]; with `-ServerLinux` the server runs in WSL2 Ubuntu-24.04 [C5]; diffs hash logs (from M4) |
| `tools\ci\art.ps1 -Id <id>` | `build-vehicle.bat <id>` then the Art import tests |
| `tools\ci\notices.ps1` | builds `THIRD_PARTY_NOTICES.txt` and `licenses\` (licence texts, OSM credit, `townProfile.json`) from `third-party.json` for every build folder and the in-game Credits panel [C1, C8] |
| `tools\ci\lfs-budget.ps1`, `tools\ci\backup.ps1` | LFS size gate (fails above 6 GiB); nightly `git bundle` + LFS object copy (§1.1) |
| `tools\ci\gate.ps1 -Tier merge\|nightly\|milestone [-Gate M<n>]` | see the tiers above; writes the gate JSON and a contact sheet |

```powershell
. D:\Backroads-Unity\tools\ci\env.ps1     # $UNITY = "C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Unity.exe"; $PROJ; $OUT
# EditMode (no GPU). -runTests exits by itself: never add -quit.
& $UNITY -batchmode -nographics -projectPath $PROJ -runTests -testPlatform EditMode -testResults "$OUT\editmode.xml" -logFile "$OUT\editmode.log"
# PlayMode (drop -nographics when a test renders)
& $UNITY -batchmode -projectPath $PROJ -runTests -testPlatform PlayMode -testCategory "!Slow" -testResults "$OUT\playmode.xml" -logFile "$OUT\playmode.log"
# Player build
& $UNITY -batchmode -nographics -quit -projectPath $PROJ -executeMethod Backroads.Build.Editor.BuildCli.Build -profile Dev -milestone M2 -logFile "$OUT\build.log"
# Player autotest on the real GPU; exit code 0 = pass
D:\Backroads-Unity\builds\M2\Backroads.exe -screen-fullscreen 0 -screen-width 2560 -screen-height 1440 -br-autotest m2.drive.route9 -br-out "$OUT\m2.drive.route9" -br-silent -logFile "$OUT\player.log"
# Same scenario at a fixed 30 fps (vSync off, Application.targetFrameRate = 30)
D:\Backroads-Unity\builds\M2\Backroads.exe -screen-fullscreen 0 -br-fps-cap 30 -br-autotest m2.drive.route9 -br-out "$OUT\m2.drive.route9-30" -br-silent -logFile "$OUT\player30.log"
# Smoke test on the exact Client build you and your friends receive (compiled into every build) [S18]
D:\Backroads-Unity\builds\M2\Backroads.exe -br-smoke -br-out "$OUT\smoke" -logFile "$OUT\smoke.log"
# Server canary: the ServerHeadless build (UNITY_SERVER defined) [T15]
D:\Backroads-Unity\builds\M2-server\BackroadsServer.exe -batchmode -nographics -br-bind 127.0.0.1 -br-quit-after 10 -logFile "$OUT\server.log"
```

**`AutotestRunner`** parses `-br-autotest <id>` (scenarios in `StreamingAssets\Autotest\<id>.json`: spawn, vehicle, time of day, fps cap, an input script in the golden-trace format, checks) and writes `trace.csv`, `frametimes.csv` (frame time and simulation main-thread time per rendered frame), `audio_tap.wav`, `audio_report.json` (pitch, RMS, clip count, underruns, computed in C#), screenshots, a contact sheet next to the JS shots, and `result.json`; then quits with 0 or 1. `-br-silent` mutes the master mixer; analysis reads the plugin's `esp_debug_tap` (what engine-sim produced) and an `AudioTap` filter on the engine AudioSource (what Unity received), so tests never blare through the speakers. Every build shows a version stamp (milestone, git hash, canon manifest hash, ESP source hash).

**Pitch check** [T12]: a detected period passes if it equals k × the firing interval for k ∈ {1, 2, 4, 8}, or if the spectrum peaks at harmonics of the cycle frequency rpm/120. Each `EngineDef` declares its expected orders (`pitchOrders`), because cross-plane V8s with two exhausts and per-cylinder primaries peak at cycle harmonics (01 §4.3), while 02 §6.7's autocorrelation method was validated only on the equal-header EJ25.

**Audio in batchmode** [T20]: M0's `Audio.BatchmodeDspRuns` asserts that `OnAudioFilterRead` runs at least 100 times per second in a batchmode PlayMode run. If it does not, every audio assertion (underruns, pitch, attenuation) is tagged `PlayerOnly` and runs only in player autotests (`m1.bench.revcycle`, `m2.drive.route9` and later), which do not use `-batchmode`, so "0 underruns" can never pass vacuously.

**Crash capture** [S5]: `CrashCollector` (every build, including yours) writes a `session.lock` file at start and deletes it on a clean exit. On the next start, if it is still there, it copies `Player-prev.log`, `%TEMP%\<company>\<product>\Crashes\*` (Unity's and the plugin's minidumps) and the plugin log into `LocalLow\…\playtest\crash-<time>\` and shows "The last session crashed; a report was saved for the agents". `SessionMetrics` records a light frame-time histogram, underruns, coast events and torque-map fallbacks in your own sessions, next to the F10 answers. Autotest `m0.crash.recover` starts the player with `-br-crash-test` (forces a native fault on an engine worker), relaunches it and checks that the report folder exists.

**Network egress** [C7]: autotest `m0.net.egress` runs single player for 60 s and asserts the process has no outbound TCP or UDP endpoints other than loopback (`Get-NetTCPConnection` / `Get-NetUDPEndpoint` by process id).

### 1.10 Working agreements for the agent team

- **Lanes:** I integrator (CI, builds, merges, `PLAYTEST.md`, milestone report, next milestone's task cards), W world and rendering (incl. camera, input, HUD basics), V vehicles and native plugin (a second V agent, V′, joins in M3 for the PhysX chassis), R roads and traffic, A art (Blender), N netcode and server (from M4), G gameplay and UI (from M7). Typical concurrency 4 lanes + integrator; peak 5 + integrator. Fewer agents stretch the calendar; the order does not change.
- **Worktrees:** one Unity project cannot be open twice. Each lane works in a worktree made by `tools\ci\worktree-new.ps1` (`git worktree add D:\Backroads-Unity-wt\<lane> -b <milestone>/<lane>-<topic>`, native DLL, canon import), with its own `Game\Library`. At most 4 Unity processes at once (`artifacts\.unity-slots`), GPU and audio autotests one at a time (`artifacts\.gpu.lock`), perf gates alone (`artifacts\machine.lock`), and nothing heavy while you play (`artifacts\.user-playing`, §1.9). The A lane mostly runs Blender, the N lane mostly runs built players.
- **Disk:** budget about 10 GB per `Library` and 6 worktrees, so about 60 GB plus builds; `D:` has 469 GB free today. Worktrees are pruned (`git worktree remove`) at every milestone tag [S10].
- **Merging:** lanes merge into `main` only through the integrator after `gate.ps1 -Tier merge` passes (≤ 20 min). `main` is always playable; unfinished features hide behind `FeatureFlags`.
- **Asset ownership** [S11]: every scene, prefab, `ProjectSettings` file and content folder has exactly one owning lane in `OWNERS.json`. `Millbrook.unity` stays a thin bootstrap; content is composed at runtime from ScriptableObjects and prefabs that one lane each owns. `ProjectSetup.Apply` is the only writer of `ProjectSettings`. `Repo.OwnersRespected` fails a merge when a lane's branch changes an asset it does not own. The merge driver is `UnityYAMLMerge` (§1.2); an integrator never resolves a YAML conflict by keeping one side.
- **Frozen contracts** (change only with an ADR in `docs\adr\` and updated tests in the same commit): CanonData v1 format (frozen M1), ESP ABI v1 `esp_version() == 1` including the step-lag semantics of §3.4 (M1), `IVehicleEngine` / `VehicleControls` / `VehicleStatus` / `BrvState` and the input binding table `bindings.json` (M2), WorldGrid public API and layer list incl. `DeltaUm` (M2), `RutStamp` 18 B (M2), `RoadNetworkAsset` / `LaneGraph` schema (M3), `VehicleStateMsg` 38 B (**M8**, once trailer, winch, blade and tyre-pressure state exist) [S20], `IEconomyAuthority` (M9), save schema v1 (M9). Every wire and file struct uses `[StructLayout(LayoutKind.Sequential, Pack = 1)]` and has a size-assert test (`UnsafeUtility.SizeOf<T>() == N`) [T13].
- **Timebox rule** (P-risk §6.3): when a work item reaches 150 % of its estimate without passing its hard gate, its written fallback is adopted and the milestone report tells you why.
- **Headline gates and lane gates** [S4]: each milestone lists **headline gates** (the build boots, canon identity, and what you will play) and **lane gates** (everything else). Only headline gates decide whether your build ships. A failed lane gate is reported in `docs\milestones\M<n>.md`, its named fallback is adopted or it is carried over to the next milestone, and it never holds back your build.
- **Gate JSON** (P-risk App. A): `{gate, tier, status, hard, headline, build{git, unity, native, abi}, machine{power, gpu, powerMode, cores, otherProcs}, wallClock{seconds, budget}, metrics{}, thresholds{}, evidence[], fallbackIfFail}`; committed to `docs\gates\` at each milestone. Test names: `Backroads.Tests.<Platform>.<Subsystem>.<Case>`; autotest ids: `m<n>.<area>.<case>`. Every CPU gate names its quality tier, its core class (P-pinned, E-pinned, unpinned) and its fps cap [T7].
- **Your feedback:** every build has an **F10 Playtest Checklist** (items from `StreamingAssets\Playtest\M<n>.json`, Yes / No / comment), saved to `%USERPROFILE%\AppData\LocalLow\<company>\<product>\playtest\<build>.json`, which the agents read. Every "No" becomes an issue in `docs\issues\` with an owning lane and is fixed from that milestone's feedback reserve [S12].
- **Feedback reserve** [S12]: each milestone from M2 on reserves about 15 % of its agent-days for fixing what you reported on the previous build, spent first, in the lanes that own the issues (included in §6.3). Unused reserve flows to the milestone's lane gates.
- **Approvals:** every download and every sign-in is yours to approve or do (§7.2). Agents never type your passwords, never push, never make or edit 3D models from Synty assets, and never commit Synty files (07 §10.4, §4.7).
- **Unanswered decisions** [S12]: every decision in §7.2 has a due date and a default. If you have not answered by the due date, the recommended default applies and the milestone report says so; anything that costs money, signs you up for something, or touches your logins never defaults to "yes".

### 1.11 Operating model: how the agent team actually runs [S1]

- **One coordinator per milestone.** You start one coordinator session at the start of each milestone (from `AGENTS.md`'s "start a milestone" line). It starts the lane sessions as background sessions, merges, builds and writes the report. You do not need to start lanes yourself.
- **What agents may run without asking.** You approve, once, a fixed allowlist in the project's Claude Code settings: Unity in batchmode, the built players, `cmake`/`ctest`/MSBuild, `node` on `tools\`, Blender in background mode, PowerShell scripts under `tools\ci\`, and `git` commits to local branches. Pushes, downloads, sign-ins, installs and anything outside the repo are **not** on it. The allowlist is written as a proposal in T0.1; it takes effect only when you accept it.
- **One approvals list.** Anything that needs you (a download, a Unity Hub action, a decision) is added to `docs\approvals\QUEUE.md` with the decision id and its default, and shown to you once a day and at milestone end. Lanes keep working on other cards while an item waits.
- **Resumable work.** Every task card ends with a committed `HANDOFF.md` (what is done, what is next, the exact command to resume), so a fresh session can pick up a lane after a crash or a full context.
- **Agent rules in one short file.** `AGENTS.md` at the repo root (and a one-line `CLAUDE.md` pointing to it) holds the hard rules (never edit `D:\Backroads` or the engine-sim tree under `D:\Backroads-ref`, never push, never touch Synty, the lock protocol, frozen contracts), the canonical commands and where the task cards are [S22]. The integrator writes the next milestone's task cards, each with a "Done when", as the last task of every milestone (T1.10 for M2) [S22].
- **The laptop.** The calendar assumes agents run about 7 days a week, overnight included, so the laptop stays on, plugged in, with sleep disabled while a milestone runs (you set this; agents do not change power settings). Perf gates run on mains power only. If agents run only while you are at the laptop, multiply the weeks by 2-3.
- **Capacity.** 4 lanes × 7 days ≈ 28 lane-days per week against 12-21 AD of milestone work per week; the critical path (V lane until M4, then R and N) sets the dates, not raw capacity.

---

## 2. Architecture

### 2.1 Layers and topologies

```
 Client presentation (!UNITY_SERVER): terrain/road/building/tree/grass/water/sky rendering, VFX, UI Toolkit HUD and maps,
   cameras, input, vehicle visuals, engine-sim plugin + audio, non-engine audio
        ▲ reads state, sends controls and requests
 Backroads.Net (the only NGO code): sessions, handshake, vehicle replication, stamps, traffic, economy, clock, ownership, interest
        ▲ authorities run here when IsServer (single player: in-process local host on SinglePlayerTransport from M5)
 Simulation core (plain C# + Burst, no NGO, no MonoBehaviour logic): WorldGrid + integer stamp kernels, Roads runtime,
   BRV vehicles (owner or AI), Traffic (authority), Economy rules (authority), Persistence, SimClock
        ▲ loaded identically everywhere and hash-checked: CanonData v1 (+v1.1), RoadNetworkAsset, content packs
```

| Topology | Authorities | Simulates player vehicles | Renders | engine-sim |
|---|---|---|---|---|
| Single player (M0-M4: direct; from M5: NGO local host on `SinglePlayerTransport`) [T17, S20] | in-process | its own | yes | local engine |
| Listen host (friends) over UTP: LAN (M5), Unity Relay join code (M8 default), direct IP (advanced); Steam SDR from M10 | in-process on the host | each client its own | yes | own + remote tiers |
| Headless server: `ServerHeadless` (Windows Mono, `UNITY_SERVER`) from M0; Linux Mono `BackroadsServer.x86_64` once U4b is installed [T15, C5] | yes | none (kinematic puppets) | no | never: not even in the build (§1.5 rule 8) |

### 2.2 Subsystems and boundaries

| Subsystem | Assembly | Key types | Inputs → outputs | First playable |
|---|---|---|---|---|
| **Data import** | Canon.Editor, World | `CanonManifest`, `CanonImporter.ImportCli`, `CanonRuntime`, `WorldInfo`, `PlacementSet`, `VegetationSet`, `LegacyTrafficNetwork` (golden only) | `CanonData\v1` → verified `.bytes` + ScriptableObjects; aborts on any hash mismatch (03 §3.5); runtime maps `TextAsset.GetData<T>()` straight into NativeArrays | M1 |
| **WorldGrid** | World, Deform | `WorldGrid` (JS layout `k = j·2049 + i`, flipped only at the API: `HeightAt(p) = HeightAtJs(-p.x, p.z)`, 03 §2), `WorldTiles`, `DirtyRectQueue` (`NativeQueue<int4>`) | Physics truth for wheels, hulls, character, camera (06 §5.2); stamps in, dirty rects out | M1 |
| **Terrain rendering** | Client.Rendering | `TerrainChunkSystem` (256 chunks of 64 m, LOD steps 1/2/4/8 at 110/250/470 m × lodScale, skirts), `TerrainChunkBuildJob` (`Mesh.MeshDataArray`), `SG_Terrain` | Reads WorldGrid; partial updates with `SetVertexBufferData` from dirty rects (03 §4.3); winding and normal-x flipped (03 §2); vertex colours written linear | M1 (vertex colours), M3-M5 (full shader) |
| **Static world** | Client.Rendering | `BuildingSpawner` (frozen `buildings.glb`, material instances for Wall/Roof/Door/Awning/Accent/Cladding, glass merged per building with pane IDs from names), `PropSpawner` (`static_props.glb` buckets in 64 m chunks; typed records from the wrapper hooks of §1.8.2 for lamps, silos, dock, fuel station, crusher, saw shed, used for lights and later 1:1 prefab swaps), `VegetationRenderer` (5,265 LODGroup trees), `GrassSystem` (`Graphics.RenderMeshIndirect`, per-tile `mulberry32(tileKey·7919+13)`, 03 §4.10), `NightLights`, `WaterSurface` | `placements.json`, `vegetation.json` → GameObjects under the GPU Resident Drawer; the same spawners also fill the lighting-only bake scene (§4.1) | M1, M5 |
| **Roads and intersections bake** | Roads.Editor | `RoadGraphImporter`, `GraphCleanup`, `CornerSolver`, `JunctionSurfaceBuilder`, `HeightModel`, `KerbBuilder`, `MarkingBuilder`, `RoadPlateStamper`, `ChunkBuilder`, `LaneGraphBuilder`, `ControlAssigner`, `RoadBakeValidator` | Frozen polylines + `roads-overrides.json` → meshes, MeshColliders, plates, lanes, controls, sign placements from one geometric model (05 §3-§5); never at runtime | M3 |
| **Roads runtime** | Roads, Client.Rendering | `RoadNetworkAsset`, `LaneGraph`, `ControlSet`, `SignalController`, `RoadChunkLoader` | Same colliders on client and server; lanes and controls for traffic | M3 |
| **BRV physics + drivetrain** | Vehicles | V0: `ClassicVehicleDef`, verbatim `vehicle.js` model (03 §4.14). v2: `VehicleDefinition`, `DrivelineDef`, `ShaftGraph`, `Row`, `TyreModel` (MF secant rows, 06 §3.6-3.7), `MudModel` (Bekker/Janosi, 06 §5.3), `TransmissionController` (manual, sequential, AMT, torque converter), `Winch`, `HitchDef` | `VehicleControls` in; `VehicleStatus`, impulses, crank samples, `RutStamp`s out; 1/120 s × 4 substeps × 12 PGS iterations (06 §3.4). Chassis: V0 collides through `ColliderGrid` 2D statics (M2); the PhysX chassis (06 V1: `Rigidbody`, `useGravity = false`, `UCX_` hulls, wheel sphere colliders) arrives in **M3** for every vehicle [T8, S2] | M2 (V0), M3 (PhysX chassis, v2 core), M4 (v2 drives) |
| **Engines** | Core, Vehicles, EngineSim | `IVehicleEngine { Collect(); Kick(crankSamples, pedal, ignition); }`: `LegacyJsEngine`, `EngineSimEngine`, `TorqueMapEngine`, `ReplicatedEngine` | Indicated torque into the crank shaft `L` steps late (§3.4) | M2 |
| **engine-sim plugin and audio** | EngineSim + native | `Native`, `EngineSimEngine`, `EngineSimAudio`, `AudioTap`, `EngineAudioScheduler`, `LoopBankPlayer`, `EngineDef` | §3 | M1 (bench), M2 (sound), M4 (drives) |
| **AI traffic** | Traffic | `TrafficSystem` (authority), `TrafficDriver` (pure pursuit, IDM), `JunctionArbiter` (rules from `Conflict.rule`), `GhostAgent` | Port of `traffic.js` (03 §4.21) on the baked `LaneGraph` with the 05 §5.14 control rules; seeded `Unity.Mathematics.Random`; BRV LOD1 cars with `TorqueMapEngine` | M6 (hidden from M5) |
| **Jobs and economy** | Gameplay, Net | `EconomyRules` (tables from `gameplay\jobs.json`), `FreightBoard`, `JobLedger`, `Wallet`, `IEconomyAuthority` (`RequestLoad`, `RequestUnload`, `RequestRefuel`, `RequestWash`, `RequestTow`, `RequestCancelJob`) | Rules ported from `jobs.js` (03 §4.22); every change is a request, so single player and online share one path | M9 (rules from M7, week 15) |
| **UI** | Client.UI | `HudController`, `MinimapView` (`map.png`, `px = -x_u + 512`), `BigMapView`, `FreightBoardPanel`, `DebugOverlay` (F3: rpm, torque, MAP, AFR, worker ms, ring fill, underruns, coast events), `GraphicsMenu` (F2), `SessionMenu`, `PlaytestChecklist` (F10) | UI Toolkit, Barlow Condensed + Inter (OFL, 03 §4.23); Esc menu, F1 controls overlay and saved settings from M2 [S13]; Credits panel with notices from M1 [C1] | M2 (minimal), M9 (full) |
| **Camera** | Client | `ChaseCameraRig`, `HoodCamera`, `FreeFlyCamera`, `FootCamera` | Straight port of `camera.js` with the terrain pull-in ray (03 §4.19); handedness rewritten (03 §2) | M1 (free-fly), M2 (chase, hood) |
| **Netcode** | Net, Net.Steam | §5 | Owner-authoritative vehicles, host-authoritative world | M4 probe, M5 LAN preview, M8 alpha |
| **Save and progression** | Persistence | `SaveService`, `WorldSave`, `PlayerProfile`, `SaveMigrations` | World save + per-player profiles (§5.6); same files for single player and host | M8 (minimal world save), M9 (profiles, economy) [S15] |
| **App and tests** | Game, Testing | `GameBootstrap`, `GameLoop`, `AutotestRunner`, `PlaytestChecklist` | System order follows the JS loop (03 §4.27) | M0 |

### 2.3 Threads

| Thread | Work |
|---|---|
| Main | `FixedUpdate` at 120 Hz (BRV scheduling, traffic decisions at 60 Hz, gameplay, net receive/send). Unity runs all due fixed steps **back to back at the start of each frame** (1 at 120 fps, 2 at 60, 4 at 30), so all budgets are stated per rendered frame at the tier's fps [T1, S8]. Then `Update`/`LateUpdate` (interpolation, camera, ≤ 2 terrain chunk rebuilds per frame, grass tiles, VFX, HUD, day/night) |
| Unity job workers (`JobsUtility.JobWorkerCount = max(2, logicalCores − 2 − liveEngineThreads)`) | `WheelQueries`, `VehicleStepJob`, `TerrainChunkBuildJob`, stamp kernels, grass/tree culling, road chunk meshing at load |
| One native worker per full engine-sim instance (local R0 + 0-4 R1) | engine steps at the script's frequency, synchronous synthesizer render, audio ring push (01 §4.2) |
| Unity audio thread | `esp_read_audio` only: lock-free, allocation-free, no logging (01 §7.1) |

### 2.4 One fixed step (1/120 s), owner client

1. `Net.ReceiveAll()`: remote snapshots → puppets (`MovePosition`/`MoveRotation`); queue contact impulses and stamps.
2. `engine.Collect()` for each local engine: wait at most 1.0 ms for the result of step k − L (§3.4; the wait counts in the main-thread budget); reuse the last torque if it is still pending; after 4 consecutive stale steps use `TorqueMapEngine` until results catch up (06 §4.5 item 5).
3. `WheelQueries.Schedule()`: WorldGrid sampling + `SpherecastCommand` against `Static | Road`; complete.
4. `VehicleStepJob`: 4 substeps × 12 PGS iterations → net `dP`/`dL`, 5 crank-ω samples, stamps (06 §3.8).
5. Apply impulses to Rigidbodies (`useGravity = false`; BRV owns gravity); `engine.Kick(crankSamples, pedal, ignition)`: always queued, never skipped.
6. `StampKernels.Apply(stamps)` (integer µm); dirty rects queued; stamps queued for the network.
7. Host only: traffic at 60 Hz; economy triggers.
8. PhysX simulates chassis against buildings, kerbs, props and puppets (from M3; in M2 the V0 model uses the JS 2D statics against `ColliderGrid`).
9. Every 6th step (20 Hz): one batched snapshot message per recipient (§5.3); every 12th step (10 Hz) while deforming: `StampBatchMsg`.

### 2.5 Editor-time data flow

```
D:\Backroads @552f780 --git archive--> snapshot (+hooks) --headless Edge--> CanonData\v1 (SHA-256 manifest)
CanonData\v1 --CanonImporter (verify)--> Assets\Backroads\World\Canon\v1 (.bytes + SOs)
CanonData\v1 world.json + traffic.json + CanonData\v1.1\roads-overrides.json --RoadBake (12 stages)--> Roads\Baked (+ v1.1\road-plates.bin)
art\vehicles\<id>.blend --br_build--> VH_*.fbx + <id>.vehicle.json --postprocessor + prefab builder--> prefab + VehicleDefinition
native\EngineSimPlugin --CMake--> EngineSimPlugin.dll --> Assets\Plugins\x86_64
EngineDef --ContentBuild/EngineBake (esp_calibrate_inertia, esp_dyno_sweep, esp_render_offline)--> J_eff (baked; used at runtime), torque map, loop bank
          (shipped .mr files pass mr_sanitise: fictional names, no comments; only engines listed in Content\core\pack.json ship)
```

### 2.6 WorldGrid memory (2049² vertices, identical on client and server)

| Layer | Type | Size | Mutable | Notes |
|---|---|---|---|---|
| `H0` canon heights (v1 + v1.1 plates) | float32 | 16.8 MB | no | hash-checked |
| `BASE` (v1 + the same v1.1 plates) [T10] | float32 | 16.8 MB | no | undeformed reference for kernels (ruts = BASE − H); hash-checked |
| `DeltaUm` [T2] | **int32 µm** | 16.8 MB | yes | **relative to H0**; authoritative networked state; range ±2,147 m covers BASE − 12 m dozer cuts |
| `H` = `H0 + (float)(DeltaUm·1e-6)` | float32 | 16.8 MB | derived | computed in a `FloatMode.Strict` job; where nothing was deformed, H equals canon exactly |
| `SURFACE`, `WEAR`, `MUD`, `WET`, `COMPACT`, `NODEFORM` | u8 ×6 | 25 MB | yes (stamps) | ROAD/CONCRETE locked (03 §4.4) |
| `ROADD` | u8 | 4.2 MB | no | |
| `TINT` | float32 129²×2 | 0.13 MB | no | |

About 113 MB in total. Tiles: 32×32 cells (16 m), 4,096 tiles, `TileMeta {uint version; ulong hash}` with xxHash3 over `DeltaUm` and the u8 layers (06 §5.6); a tile resend is 4 KB raw per height layer before deflate.

**Why micrometres** [T2]: the JS rut kernel (`js/world.js:137-177`) lowers each vertex per call by `dh = min(H − target, rate·f·(0.5 + 0.5·soft))` with `rate = 0.002 + 0.11·|v|·dt` off mud (03 §4.14), about 0.45 mm per call at a ring vertex with f = 0.3 on grass, and spreads the berm share over 10-20 ring vertices at 0.1-1 mm each. At 1 mm resolution these increments round to zero or pile onto the same vertices, so ruts and berms would look different from the browser game and the parity gate could not pass. The kernel works in int64 µm with one fixed rounding rule per call (round half away from zero on each `dh` and on each berm share, remainder to the ring vertex with the largest weight, ties by index), which tracks JS's per-call result to within the float32 spacing of JS's own `H` array (about 1 µm at town heights, 15 µm on the highest ground at 175 m).

---

## 3. The engine-sim plan

### 3.1 Build: vendor, don't fork

- Copy upstream `85f7c3b`, scs `e009f4f` and piranha `432f0b1` byte-for-byte into `native\EngineSimPlugin\vendor\`; `vendor-check.ps1` proves it. All fixes live in our sources (01 §8): `lastValveLift[8]` removed by overriding `writeToSynthesizer()`; delta include satisfied by two empty shims; delay filters re-initialised at the engine's own frequency; synthesizer re-initialised at 48 kHz with IRs resampled once; `setTargetSynthesizerLatency(0)` + one `endInputBlock()` at init + render before `endFrame()`; `transmission->update(0)` after every control change; one `Function` constructed on the main thread at plugin init; `NDEBUG` always; synthesizer `volume` ≈ 0.5 (02 §6.7 clipping).
- 01 §1.3 built the 41 core files and 29 scs files this way with 0 errors and 0 warnings and no patched source; 02 proved the same core with one patched file. We follow 01.

### 3.2 The piranha / flex-bison question

piranha needs flex and bison at build time (bison ≥ 3.2 with `lalr1.cc`; flex with `%option c++`), and upstream commits no generated parser (01 §5.4, 02 §3). Everything else in piranha compiles with MSVC and no Boost once `path.cpp` is replaced by `piranha_path_std.cpp` (01 §5.4, App. A.6).

- **Recommended (U3c):** you approve one download of the portable **win_flex_bison 2.5.25** zip (win_flex 2.6.4 + win_bison 3.8.2, github.com/lexxmark/winflexbison/releases) into `D:\Backroads-Unity\tools\winflexbison\` (gitignored; nothing installed system-wide). An agent generates `scanner.auto.cpp`, `parser.auto.cpp`, `parser.auto.h` once, commits them to `native\EngineSimPlugin\generated\piranha\` with the tool's SHA-256, and builds with `-DESP_WITH_PIRANHA=ON -DESP_REGENERATE_PARSER=OFF`. All **25 engine scripts** then load with zero porting error (the folder has 27 `.mr` files; the two `radial.mr` files are helpers imported by the radial engines, not engines) [T13], and community engines become mod content later.
- **Licence** [C9]: piranha at `432f0b1` has no LICENSE file. Its MIT LICENSE was added five days later in `004fa15`, whose only change against `432f0b1` is that file. `VENDOR.md` records this with the compare URL, and `vendor\LICENSES\piranha-LICENSE` is that file (one small download, U3e). The generated scanner and parser keep their original headers (flex's BSD notice, which requires reproduction in binary distributions; bison's GPL-3 header with the Bison exception), checked by `Licence.GeneratedHeadersIntact`; the flex notice goes into `THIRD_PARTY_NOTICES.txt`.
- **Loader rules** (01 §5.4): absolute search path to `StreamingAssets\EngineSim\es`; reset `*es_script::Compiler::output() = {}` before each compile; one global compile mutex; a generated wrapper `.mr` in a temp folder that calls `main()` or, for older files without `main` (`chevrolet\chev_truck_454.mr`, `bmw\M52B28.mr`, `audi\i5.mr`), `set_engine(<node>())`.
- **If you say no:** nothing in the schedule moves. `esp_load_builtin(name)` serves C++ builders: `f4_boxer_a` (the EJ25 port of 02 §4, validated: 229 N·m at 4,500 rpm, 170 hp at 6,500 rpm), `v8_cross_a` (the LS of 01 App. A.1), `i4_synth` (01 probe), and `v8_bigblock_a` (the 454, a 1-3 h port plus a dyno check, 01 §5.3). Each further engine is another 1-3 h port.
- **No real brand names reach players** [C2]. The upstream `.mr` files carry real names inside them (`name:` fields such as the Honda, Ferrari, Harley-Davidson, Suzuki, BMW, GM, Subaru and Chevrolet ones), not only in their file names. So:
  - `ContentBuild`/`EngineBake` writes every shipped `.mr` through `mr_sanitise` (rewrites `name:` fields and node identifiers to the fictional id, strips comments) and ships only the engines listed in `Content\core\pack.json` under fictional ids (`v8_bigblock_a.mr`).
  - The C++ builtins are compiled in under fictional ids only (above).
  - The full 25-engine catalogue appears only in `Dev` builds, which never leave this PC; Client builds list roster engines only.
  - UI and store text say "engine brake" (not the trademark "Jake brake") and "torque-sensing differential" (not "Torsen").
  - `Content.NoRealMarks` runs in every gate, on the source tree and on the built player's `Data` folder: it scans `StreamingAssets`, `Assets\Content`, UXML/USS, localisation strings and `PLAYTEST.md` against a denylist (Honda, VTEC, Subaru, Ferrari, GM, Chev, Chevrolet, BMW, Audi, Toyota, 2JZ, Harley, Hayabusa, Suzuki, Kohler, Merlin, Rolls, Ford, Ranger, Isuzu, Hino, Freightliner, Cascadia, Volvo, VNL, Scania, Ural, Tatra, Zetros, Mercedes, GR86, Yaris, Caterpillar, D3, Jake, Torsen, Baja). Real names stay in the vendor tree, internal docs and each vehicle's `brief.md` (07 §4.1, §9).

### 3.3 ABI v1

Adopt 01 §7.2 (`esp_version`, `esp_create`, `esp_destroy`, `esp_load_script`, `esp_get_info`, `esp_set_controls`, `esp_set_audio_params`, `esp_step`, `esp_step_begin`, `esp_step_end`, `esp_read_audio`, `esp_calibrate_inertia`, `esp_dyno_sweep`, `esp_render_offline`; modes `FREE`, `PROXY`, `KINEMATIC`, `SLAVED_CRANK`, `FOLLOW_RPM`; blittable structs starting with `struct_size`; `extern "C"`, `__cdecl`; status codes; exceptions caught inside) with three revision-2 changes:
- **Step-indexed results** [T1]: every command carries its physics step index; `esp_step_end(e, step_index, timeout_us, out)` returns that step's result or `ESP_PENDING`. The result struct adds `dT_dw` (the engine's ∂T_ind/∂ω estimate) for the lag feed-forward of §3.4.
- **Time** [T16]: `dt` passed in is **physics (game) time**, and the step count is `n = round(Σdt·f)`. `sim_speed` (`Time.timeScale`) no longer multiplies the step count, as 01 §7.3's sketch did (`acc += dt·f·simSpeed`, which double-counts slow motion when `dt` is already game time); it only scales the synthesizer input rate `f·timeScale·(1 + ε)` so slow motion keeps audio real-time.
- `esp_calibrate_inertia` is for `EngineBake` and the conformance suite only; at runtime `J_eff` comes from the baked `EngineDef` [T19].

```c
ESP_API int32_t esp_load_builtin(EspEngine* e, const char* name_utf8, char* err_utf8, int32_t err_cap); /* "f4_boxer_a","v8_cross_a","v8_bigblock_a","i4_synth" */
ESP_API int32_t esp_get_stats(EspEngine* e, EspStats* out);   /* steps run/behind, stale steps, Collect wait ms, coast events and borrowed steps,
                                                                   cpu_ms EMA, real-time factor, underruns, ring fill and target, P/E-core share */
ESP_API int32_t esp_set_quality(EspEngine* e, int32_t fluid_steps, int32_t fir_max_taps);          /* runtime tier switch */
ESP_API int32_t esp_set_worker_options(EspEngine* e, const EspWorkerOptions* o); /* priority, power-throttling off, coast_max_ms */
ESP_API int32_t esp_debug_tap(EspEngine* e, const char* wav_path_utf8, double seconds);           /* dev builds: tee the ring to WAV */
ESP_API int32_t esp_poll_log(char* dst_utf8, int32_t capacity);  /* main thread; no callbacks from native threads */
ESP_API int32_t esp_build_info(char* dst_utf8, int32_t capacity); /* ABI, source hash, flags, piranha on/off */
```

C# checks `esp_version() == 1` at start. On a mismatch, a missing DLL or `ESP_ERR_STATE`, that vehicle switches to its `TorqueMapEngine` and loop-bank sound, logs once, and carries on: a plugin fault never ends a session (P-online §3.3).

### 3.4 Threading

- One dedicated native worker per full-simulation engine (`EspCreateDesc.worker_thread = 1`). The main thread latches controls and kicks/collects; the audio thread only reads the ring (01 §7.1). One engine per thread is safe with the §3.1 fixes (01 §6.1); 02 §6.4 measured near-linear scaling to 16 engines.
- **Unity's clock as it really runs** [T1]. Unity does not space `FixedUpdate`s 8.3 ms apart: it runs every due fixed step **back to back at the start of a frame**, with only BRV and PhysX (about 1-2 ms) between them: 1 step per frame at 120 fps, 2 at 60, 4 at 30. The worker needs 2.9 ms (V8 10k/8, 01 §6.2) to 3.6-4.6 ms (EJ25 20k/8 with IR on a P-/E-core, 02 §6.3) per step, so it cannot return step k before step k+1 starts. Revision 1's "collect k−1 with timeout 0" would therefore have used 2-step-old torque on half the steps at 60 fps, 3 of 4 at 30 fps, switched to the torque map every frame below 25 fps, and triggered coast mode on every frame below 60 fps. The coupling is therefore defined in **simulation steps, never wall-clock time**:
  - **Fixed lag in steps.** Step k applies `T_ind(k − L)`. `L = clamp(S, 2, 4)`, where S is the number of fixed steps Unity will run in the current frame, computed before them by an `EarlyUpdate` player-loop hook as `floor((Time.time − Time.fixedTime) / Time.fixedDeltaTime)` (at most 4, §1.3). L changes only at a frame boundary; when it grows, the last torque is simply used again. At 120 and 60 fps L = 2; at 30 fps L = 4. Each step then needs only results kicked in earlier frames, which the worker computed during the rest of those frames, as long as the engine runs faster than real time (which the real-time-factor gates below guarantee).
  - **Bounded wait.** `Collect` waits at most 1.0 ms for result k − L; the wait is counted in the main-thread budget and in `esp_get_stats`. Only if it is still missing is the last torque reused (a stale step); 4 consecutive stale steps switch to `TorqueMapEngine` until results catch up.
  - **Every command is queued.** Kicks are never skipped or dropped; the command ring holds 32 steps.
  - **Feed-forward over the lag.** BRV applies `T_ind(k − L) + dT_dw·(ω_k − ω_{k−L})`, clamped, so a longer lag costs little near the limiter; 06 §4.5's loop gain (0.04 at 17 ms) stays below 0.1 at L = 4 (33 ms).
  - **Coast mode only on a real hitch:** no command for more than `max(2 steps, 1.5 × the last frame time)`. The worker then keeps stepping with the last command for up to 300 ms so the sound continues. The coasted steps are **borrowed**: when commands resume, the worker subtracts them from the step count of the next commands (never below zero), so engine time realigns with physics time; the PD integral resets and the event is counted. Commands of the current frame are never dropped (P-risk §3.3, corrected).
  - **Before ABI v1 freezes (M1):** `esp_cosim_matrix` re-runs 01 §3.5's co-simulation with L = 1, 2, 3 and 4 and with L sequences replayed from frame-time traces recorded at 30, 45, 60 and 120 fps; it reports Δv, tracking RMS and limiter overshoot per case. If L = 4 fails the P4 criteria, the fallbacks, in order, are: (1) a longer `Collect` wait that caps L at 3 (costs main-thread time, counted); (2) on that quality tier the vehicle drives on `TorqueMapEngine` with engine-sim on `FOLLOW_RPM` (the M4 kill fallback, applied per tier).
- **Exact time:** fractional step accumulator over physics time; `n = round(Σdt·f)`; engine time equals physics time step for step (01 §3.3). `Time.timeScale` only scales the synthesizer input rate (§3.3) [T16]; ctest `esp_step_accounting` runs at timeScale 1 and 0.5.
- **PROXY note** (M13): PROXY needs a fork-join inside each step (01 §3.6), so at 30 fps its four joins per frame sit on the main thread; M13 measures it on the min-spec proxy before any car ships with it.
- **Scheduling:** Windows put unfocused engine threads on E-cores (02 §6.4: only 22-50 % of frames on P-cores). Workers call `SetThreadInformation(ThreadPowerThrottling)` with throttling off and run above normal priority (from M4, `esp_set_worker_options`); `esp_get_stats` reports P/E residency.
- **CPU gates** [T7] are stated as an engine **real-time factor** (simulated seconds per worker second, RTF), which is independent of the script's frequency, so a 20 kHz engine automatically gets twice the µs-per-step budget of a 10 kHz one. Every gate names its tier and core class: **Ultra** RTF ≥ 1.5 on an E-core-pinned `esp_cli bench --affinity E`; **High** RTF ≥ 2.0 in the focused player, unpinned; **Low** RTF ≥ 1.5 on the min-spec proxy (§1.9). µs per step and P/E residency are reported next to each gate.

### 3.5 Coupling: staged, every stage playable

| Stage | Milestone | Mode (01 §7.2) | What moves the vehicle | What engine-sim does |
|---|---|---|---|---|
| Engine Bench | M1 | `FREE` (+ dyno hold for sweeps) | nothing | starter, ignition, throttle; live torque curve |
| First drive | M2 | `FOLLOW_RPM` | the classic JS model (`LegacyJsEngine`, verbatim) | dyno-hold at `rpm_es = idle_es + (rpm_js − idle_js)·(0.97·redline_es − idle_es)/(redline_js − idle_js)`, real pedal: real combustion and exhaust sound, load heard through manifold pressure (01 §6.3) |
| **VS-1** | M4 | `SLAVED_CRANK` (Option D) | engine-sim's net indicated torque on BRV's crank shaft, L = 2-4 steps late with feed-forward (§3.4) | its crank follows Unity's crank trajectory through an 8 Hz PD; firing pulses pass through (01 §3.4-3.6, 06 §4.4) |
| GT race car | M13 | `PROXY` (Option A) for single-driven-axle cars if D's limiter lag is felt | plugin integrates crank + clutch + driven-wheel spin against Unity's tyre law | most accurate: Δv ≤ 0.06 m/s, 4 rpm in cruise (01 §3.5, an LS V8 in a 1,614 kg car; M13 re-runs the reference for the GT) |
| Remote players, near AI | M8 | `FOLLOW_RPM` | (owner's state) | sound only |

SLAVED_CRANK details (01 §3.6, 06 §4.4-4.5): engine-sim transmission in neutral with clutch limits 0 (`changeGear(-1); update(0)`) and `m_vehicleDrag` removed; `J_eff` **baked** by `EngineBake` with `esp_calibrate_inertia` (01 measured 0.4570 vs 0.4537 kg·m² for the LS) and read from `EngineDef` at runtime, because the calibration is 3.5 s of simulated ramps (1.2-2.5 s of CPU per engine) and would hitch every spawn or remote join; a self-test checks that the bake's ESP source hash matches the loaded DLL [T19]. `J_eff` is used as BRV's crank inertia, extra flywheel added on the Unity side only; `K_p = J·(2πf_c)²`, `K_d = 1.4·(2πf_c)·J`, `f_c = min(8 Hz, idle firing frequency/4)`, torque clamp 4 × peak; the clutch opens during shifts; neutral = clutch pressure 0; reverse = negative ratio. **Limiter:** engine-sim's own spark cut plus a Unity fuel-cut backstop at redline + 150 rpm, because D's lag let rpm deviate 270-350 rpm on the limiter in 01 §3.5; the `dT_dw` feed-forward of §3.4 is always on, and if you can still feel it: two-step averaging, then PROXY for that vehicle. The M2 path stays selectable as **"classic engine"** until the M4 coupling gate passes, so coupling never blocks a build. The coupling mode is a field on `VehicleDefinition`.

### 3.6 Audio path

1. Never start engine-sim's synthesizer thread. Render synchronously on the engine worker after each step batch, before `endFrame()` (`renderSync`, 01 §4.3), with the AVX2 FIR (0.51 µs/sample at 10,000 taps instead of 3.92, 01 §4.2).
2. Synthesizer re-initialised at `AudioSettings.outputSampleRate` (48 kHz), IRs resampled once at load (01 §4.2).
3. Lock-free SPSC float ring of 16,384 frames. Audio arrives in bursts of one frame's worth of steps plus worker time, so the fill target is sized from the measured frame time [T1]: `target = p95 frame time + worker time for one frame's steps + one DSP buffer (5.3 ms) + 5 ms`, re-computed every 2 s with hysteresis: about 24 ms at 120 fps, 35 ms at 60 fps and 55-60 ms at 30 fps. ±0.5 % drift trim via `setInputSampleRate`; underrun = hold and decay over 2 ms, counted (01 §4.4).
4. **Unity, local car:** `EngineSimAudio.OnAudioFilterRead` calls only `esp_read_audio` (01 §7.4). **Spike S-AUD-1 in M1** (01 §9.3) answers, in 6000.3.20f1: (a) does `spatialBlend = 1` attenuate and pan filter-generated audio (old issue-tracker entries say no); (b) does a 6.3 `IAudioGenerator` with a Burst `[DllImport] esp_read_audio(IntPtr, float*, int, int)` work, and does `ChannelBuffer` expose a pointer; (c) the looping DC-clip × filter workaround; (d) `AudioClip.Create(..., stream: true, PCMReaderCallback)` as the last fallback. Remote and AI engines use whichever passes the pan/attenuation test.
5. **Mixer:** `Master > Engines (Local, Remote) > Vehicle (Tyres, Impacts) > Ambience > UI`; remote engines share one reverb bus instead of per-engine convolution (02 §7 item 6).
6. Tyres, gravel, wind, horn, thuds: looping AudioSources (ported WebAudio synths first; any sample must be CC0 or CC-BY, recorded in `assets-provenance.json`, and each download approved like U3 [C1]) driven by the same state values as `audio.js` (03 §4.24).

### 3.7 Remote players and AI engines

| Tier | Who | Method | CPU |
|---|---|---|---|
| R0 | your vehicle | full engine-sim at the script frequency, SLAVED_CRANK (FOLLOW_RPM before M4), full IR | 0.25-0.6 core (01 §6.3) |
| R1 | nearest audible remotes, cap by tier (Low 1, Mid 2, High 4); nearest AI within 60 m when a slot is free | `FOLLOW_RPM` with replicated rpm, pedal, ignition, starter; 4 gas substeps; short IR; 3D | ≈ 0.25 core each for a V8 (01 §6.3); 0.11-0.17 at 10k/4 (02 §6.5) |
| R2 | further remotes, AI traffic | loop banks baked by `esp_render_offline` (1-2 s loops every 250 rpm × {closed, 50 %, WOT}, Vorbis) played by `LoopBankPlayer` (granular crossfade on rpm and load) | < 0.5 % for dozens; 1-3 MB per engine |
| R3 | far away | culled or one drone layer | ~0 |

`EngineAudioScheduler` re-ranks every 0.5 s with hysteresis by loudness × proximity × in-view. AI and the server use `TorqueMapEngine` from `esp_dyno_sweep` (06 §4.7). engine-sim is not deterministic (`rand()`), so the network carries engine **state** (rpm u16, pedal u8, load u8, gear, clutch, flags), never inputs and never audio (01 §6.3, 06 §4.6). A PD-slaved R1 variant stays behind a flag until the listening test picks one.

### 3.8 CPU budget and quality tiers

Measured here (01 §6.2, 02 §6): V8 at 10 kHz/8 = 34.7 % of a P-core, 2.9 ms of worker time per 120 Hz step; EJ25 at 20 kHz/8 with IR audio = 434 ms per simulated second on a P-core and 547 ms on an E-core (RTF 2.26 / 1.81); 1 local + 23 remote engines at 10k/4 = 4.4 cores, unpinned. A half-speed player PC needs about twice that (02 §9). Gates use the real-time factor per tier and core class of §3.4, and the min-spec proxy of §1.9 [T7, S8]. Main-thread budgets are per **rendered frame** at the tier's fps: on Low at 30 fps, 4 fixed steps share one 33 ms frame.

| Tier | Local engine | R1 cap | For |
|---|---|---|---|
| Ultra | script frequency, 8 gas substeps, full IR (≤ 10,000 taps) | 4 | this laptop |
| High (default) | script frequency, 4 substeps (torque within 1 %, 01 §6.2) | 2-3 | mid-range PCs |
| Low | script frequency, 4 substeps, IR ≤ 2,000 taps | 1 | 6-core / Steam Deck |

**The simulation frequency is never a quality knob**: it changes the engine (LS −35 % torque at 5 kHz; EJ25 +29 %).

### 3.9 The diesel and turbo gap

engine-sim has no compression ignition, no forced induction and no engine brake (01 §5.3, 06 §2 item 8).
1. **Now, honest:** the box truck uses the Chevrolet 454 gas V8; real gas medium-duty trucks exist, so it is authentic (06 §4.8). This changes the JS truck's diesel voice (`audio.js` "rough" clatter, idle 650, redline 2,700) (U6).
2. **Unity-side layers, labelled as layers:** a turbo whistle and boost gauge from a first-order spool fed by engine-sim's intake flow and manifold pressure; a compression-release engine brake ("engine brake" in the game; "Jake brake" is a trademark [C2]) as a retard torque `T ≈ P_eb(rpm)/ω` (300-450 kW for 13-15 L, 06 §4.8 item 8) plus a bark synthesised or sampled under §3.6 item 6's rules.
3. **Diesel-character stand-in for the semi and 6x6** (M10/M12): an I6 from the `03_2jz` / `M52B28` archetype, ~100 × 120 mm, high compression, `molecular_afr` 17.75, `energy_density` ≈ 45.5 kJ/g, redline 2,500-3,200, governor throttle, harsh IR, injector-tick samples (01 §5.3). It will still sound like a big petrol engine; or `11_merlin_v12` at ~3,000 rpm (06 §4.8).
4. **Optional fork track F (U12, after M9):** patches in `native\EngineSimPlugin\patches\`: forced induction (boost on the intake's upstream atmosphere, spool lag, wastegate; 06 §4.8 item 6), then compression-release braking (item 8), then compression ignition (item 7, weeks of research). `kyan0s-git/engine-sim` (history from 2026-10-11) is reference reading only after licence and code review (01 §5.3).

### 3.10 Engine roster

| Vehicle | Engine (upstream → shipped id) | Without piranha | Status |
|---|---|---|---|
| Engine Bench, rally coupe | `01_subaru_ej25_eh` → `f4_boxer_a` | builtin `f4_boxer_a` | validated against the real engine (02 §6.2) |
| **Box truck (hero)** | `chevrolet\chev_truck_454` → `v8_bigblock_a` | builtin `v8_bigblock_a` (port in M2) | needs a dyno-vs-spec pass (01 §5.3) |
| Pickup | `07_gm_ls` → `v8_cross_a`, or `04_60_degree_v6` → `v6_60_a` | builtin `v8_cross_a` | LS rebuild ran hot (715 N·m at 4,000 rpm); the piranha run settles script vs port (01 §9.2) |
| Hatch | `05_honda_vtec` → `i4_vvt_a` (no "VTEC", a Honda trademark [C2]) | builtin `i4_synth` placeholder | as shipped |
| Semi, 6x6 | diesel-character I6 stand-in, or `11_merlin_v12` → `v12_aero_a` | port | placeholder (§3.9) |
| GT | `08_ferrari_f136_v8` → `v8_flatplane_a` | port | as shipped |
| Trophy truck | `07_gm_ls` → `v8_cross_a` | builtin `v8_cross_a` | as above |

Every `EngineDef` passes the **engine conformance suite** before shipping: loads; idles in its declared range; dyno peak within ±10 % of its brief; 60 s of WOT and limiter bouncing with no NaN; real-time factor within the tier gates of §3.4; `J_eff` error < 3 % (06 P3); loop bank and torque map baked; shipped `.mr` sanitised and `Content.NoRealMarks` clean (P-online §2.7).

---

## 4. Graphics in URP, and the Blender vehicle pipeline

### 4.1 Renderer ("Synty-plus" look, 07 §2)

| Feature | Setting | Evidence |
|---|---|---|
| Renderer | URP 17.3 Universal, **Forward+** (no 8-lights-per-object limit: headlights, indicators, 78 real lamp lights). Forward+ ignores URP's per-object light limit, so the tiers limit the **number of real lights enabled** instead (§4.3) [T9] | 04 §2.2, 07 §2 |
| Draw calls | GPU Resident Drawer + GPU occlusion culling; MeshRenderers only; no MPB; per-renderer bits via the 6.3 Renderer Shader User Value (spike S-GFX-1 in M5) | 04 §2.2 |
| Shadows | main light, cascades per tier; shadows only on the player's headlights | 04 §2.3 |
| AO | SSAO renderer feature (GTAO with 6.7) | 04 §2.2 |
| Reflections | **a realtime reflection probe that follows the player vehicle** (128 px, time-sliced, "via scripting") + box-projected baked probes in town (baked in the bake scene below) | 07 §2 |
| Indirect | gradient ambient driven by the day/night curves; Adaptive Probe Volumes with sky occlusion baked once for frozen Millbrook (timeboxed in M5, fallback: gradient only); Surface Cache GI, SSR, GTAO evaluated on the 6.7 upgrade | 04 §1.2, §2.2 |
| Bake scene [T11] | Terrain, buildings and trees are built at runtime, and APV and baked reflection probes only see static objects present at bake time. So `Backroads.Canon.Editor.LightingBakeBuilder` generates `Scenes\Bake\MillbrookLighting.unity` with terrain LOD0 chunks, buildings and trees as Contribute-GI static objects, bakes APV and the town reflection probes there, and the game loads the baked data additively. Inside the 2 AD APV timebox | 04 §2.2 |
| Sky | port of the `sky.js` clock and curves (start 16.2 h, sunrise 5.5, sunset 20, night factor, fog table, exposure 0.95 + 0.45·night) driving a procedural sky Shader Graph, sun/moon swap, fog | 03 §4.11 |
| Post | Bloom (Kawase/Dual, threshold ≈ 1.0), ACES, Split Toning + Hue-vs-Sat + Channel Mixer reproducing the JS `GradeShader`, vignette; motion blur off; UI after post | 03 §4.12 |
| Upscaling | STP (needs TAA); 0.7 render scale on Low | 04 §2.2 |
| Decals | URP Decal feature (DBuffer) for skid marks, mud splats, oil, patch repairs only; **road markings are geometry** | 05 §5.8 |
| Volumetrics | optional `com.cqf.urpvolumetricfog` (MIT) on High | 04 §2.2 |

### 4.2 Look by milestone

| Milestone | Look work |
|---|---|
| M1 | Terrain chunks with JS per-vertex colours (03 §4.3 PAL rules), JS-style road ribbons (edge lines, dashed centre, `roadFade`), frozen buildings with canon colours, typed props from `static_props.glb`, 5,265 LODGroup trees, water plane, sun + gradient ambient, ACES |
| M2 | Today's vehicles via glTFast with name-remapped materials (paint with clear coat, glass, lamps), headlight spots |
| M3 | Asphalt, concrete and marking materials (world-planar UVs, vertex-colour traffic wear, 05 §5.9); simple sign meshes; **`SG_Terrain` two-surface shader port** begins (detail noise sampled at `float2(-x, z)`, ragged boundary, grass, pebbles, gravel, 4 m slabs, rock strata, puddles, tyre streaks; 03 §4.3) |
| M4 | Truck through the FBX pipeline with `SG_CarPaint`, `SG_VehicleGlass`, `SG_VehicleLights`, `SG_MudLayer`; kerbs, gutters, sidewalks; day/night clock with procedural sky, moon, fog |
| **M5 "Pretty Millbrook"** | Windows lit by canon pane flags at night (glass merged per building, pane IDs resolved by name); 78 lamp lights; headlights with cookies (player) and fake beam cones (traffic); tree wind (03 §4.8 formula) with LOD cross-fade; grass port; URP water with depth fade; follow reflection probe; post grade; APV (timeboxed); truck trim sheet and bakes; Low/Mid/High presets and the F2 menu |
| M6 | Truck's new modern body; traffic cars with merged LOD meshes and `M_VehicleFar` |
| M7 | VFX Graph dust, clods, mud, splash, smoke with the JS emission rules (03 §4.13); mud building up on bodywork; tyre tracks |
| M10+ | Liveries (UV1 + `LiveryCompositor`), damage (vertex dents, detachables, glass), 6.7 features |

Acceptance of the look is by contact sheets and by playing: the 12 canon cameras at 10:00 and 21:00 next to the JS shots, plus `LightingTest` at noon/dusk/night × mud 0/0.5/1. The bar is "matches or beats the browser look" (07 §11 phase C).

### 4.3 Quality tiers (04 §2.3)

| Setting | Low (Deck class) | Mid (GTX 1070 class) | High (RTX 3070+, your 5080) |
|---|---|---|---|
| Render scale | 0.7 + STP, 30-45 fps cap | 1.0 (or 0.85 + STP) | 1.0 |
| Shadows | 2 cascades, 1024, 120 m | 4, 2048, 250 m | 4, 4096, 400 m |
| SSAO / volumetrics | off / off | half-res / off | full / on |
| Grass density, distance | 25 %, 40 m | 60 %, 80 m | 100 %, 150 m |
| Real lights enabled [T9]: street-lamp light pool (nearest by distance), additional-light shadow atlas, real headlights | 6 pooled lamp lights (like the JS pool), no additional-light shadows, own headlights only; others fake cones | 16 pooled, 1024 atlas, own + 4 nearest | all 78, 2048 atlas, own (shadowed) + 8 nearest |
| Engine audio tier (§3.8) | Low | High | Ultra |

Gameplay-side values (tree near radius 90/140/190 m, terrain lodScale 0.7/1/1.3, builds per frame 1/2/3; 03 §4.12) live in a `QualityPreset` ScriptableObject next to Unity's Quality levels. The chosen tier, resolution and volumes persist in `settings.json` (M2). The Forward+ light-cluster cost at night is measured on Low (`m5.perf.night-low`) [T9]. Memory budgets [S8]: Low ≤ 4 GB RAM and ≤ 3 GB VRAM, Mid ≤ 8 GB RAM and ≤ 6 GB VRAM (a GTX 1070 has 8 GB), High ≤ 12 GB RAM; measured with `com.unity.memoryprofiler` from M5.

### 4.4 Shaders

`SG_Terrain`, `SG_Road` (Asphalt, Concrete, Markings with `Offset -1,-1`, Gravel), `SG_TreeWind`, grass HLSL + compute cull, `SG_Water`, `SG_Sky`, `SG_BuildingGlass` (lit mask), and the vehicle set of 07 §6: `SG_CarPaint` (clear coat, 3 paint zones from MASK.A, flakes, livery in UV1, mud and damage masks), `SG_VehicleTrim` (shared 2048 trim sheet with a palette strip), `SG_VehicleGlass`, `SG_VehicleLights` (vertex channels R head/DRL, G tail/brake, B indicator, A reverse; HDR intensities), `SG_Tyre`, `SG_MudLayer` (1:1 port of `dirtify()`), `SG_BR_SyntyLit` (our override material for Synty props). Per-car colours are material instances.

### 4.5 Blender vehicle pipeline (07 §11 phases mapped to milestones)

| Phase | Milestone | Done when |
|---|---|---|
| A0 canary | M0 | `Art.Canary` passes (§1.7) |
| A1 FBX postprocessor, material remap, LOD Group from `_LOD` names | M2 | Today's truck imported via both glTFast and FBX; remap by name works on first import (07 §12: check `AddRemap` vs `OnAssignMaterialModel`) |
| B generator upgrade (sockets, ≤ 9 materials, LOD0-3, sidecar, renames) | M3 | `build-vehicle.bat truck` → `VH_Truck_Box75.fbx` within the box-truck budget (90-150k tris LOD0, ≤ 9 materials, 07 §8.4) and a valid `build_report.json`; prefab built with no hand steps |
| C shaders | M4 | Truck in `LightingTest` matches or beats the three.js look |
| D bakes and trim sheet | M5 | Panel lines and AO visible; ≥ 400 px/m; headless bake < 5 min |
| E first photo-inspired body | M6 | You sign off the review sheet; `brief.md` lists 3+ inspirations and at least 3 named deviations per inspiration; F10 asks "Would a player name a real model at a glance?" and a "yes" sends it back [C4] |
| F Synty probe (optional, U10) | M5-M6 | Only after Synty's written reply and your model-training confirmation (§4.7); **you** run `Synty-Import.bat`; the audit script writes `synty_audit.json` (names, counts, pink-material flags) and agents read that file only; dressing decision recorded [C3] |
| G livery | M11 | A rally coupe with 3 liveries, numbers readable on both sides |

Per vehicle (07 §8.2, revised [C4]): brief (fictional brand, 2-4 real inspirations you choose, dimensions from spec sheets, "do not copy" list) → **blended proportion profile** (the median of the inspirations' wheelbase, track, overhangs, cab and bed or box lengths and heights; never one car's photo scaled to its wheelbase) → profile cage from that blend → **your optional 1-3 h hand-tuning pass (move vertices only)** → scripted build → review sheets (the proportion sheet shows each inspiration's silhouette beside ours for comparison only) → you comment, the agent edits and rebuilds. Grille, lamp signature and badge are our own designs from the first cage. A short takedown procedure (`docs\legal\takedown.md`: hide the vehicle by content flag, swap the model, reply) keeps 07 §9 item 10's one-click swap usable.

### 4.6 Which vehicles first, and why

Procedural generation is strongest on boxy classes and weakest on sculpted sports cars (07 §8.1); truck physics (shafts, diffs, low range) comes before race-car aero (06 §8). Order:

| # | Vehicle (asset id) | Inspired by (class only; 06 §7.1) | Engine | Milestone |
|---|---|---|---|---|
| 0 | Today's five, renamed (U9) | — | §3.10 | M2 (dozer M7) |
| 1 | **Cab-over box truck** `VH_Truck_Box75` | Isuzu NPR / Hino / Ford F-650 class | 454 gas V8 + 6AT | new body M6 |
| 2 | **Mid-size double-cab pickup** `VH_Pickup_MidA` | Ranger / Hilux class, part-time 4x4, rear locker | LS V8 or 60° V6 + 10AT | M7 (physics), new body M9 |
| 3 | **Semi tractor 6x4** `VH_Truck_Tractor6x4` + **53 ft dry van** `VH_Trailer_Van53` | Cascadia / VNL / Scania class | diesel stand-in + engine-brake/turbo layers | M10 |
| 4 | **Rally coupe** `VH_Rally_CoupeA` | GR86 / GR Yaris Rally2 class | EJ25 | M11 |
| 5 | **6x6 offroad truck** `VH_Truck_6x6A` | Ural / Tatra / Zetros class, CTIS | diesel stand-in or V12 | M12 |
| 6 | **GT3-inspired** `VH_GT_A` | GT3 class | F136-class V8, PROXY coupling | M13 |
| 7 | **Trophy truck** `VH_Trophy_A` | Baja trophy truck class | LS V8 | M14 |

Every vehicle follows 07 §9: fictional brand and model name everywhere (UI, files, code ids, store pages), 3+ mixed inspirations, no badges, no signature grilles, lamp signatures or iconic silhouettes, `brief.md` as the good-faith record. "Ranger 4x4" and "Muncher D3" are renamed before they appear in any Unity build; placeholder display names until you pick (U9). The real names in the "Inspired by" column above stay in internal documents and `brief.md` only; `Content.NoRealMarks` (§3.2) keeps them out of every build.

### 4.7 Synty

Decided 2026-10-18 (U10). Packs owned: POLYGON City, POLYGON Explorer Kit and POLYGON Pro Racer (one-time licences). Synty's one-time licence (version of 9 July 2026, 07 §10.4) forbids putting its assets into datasets used by generative AI, generating 3D models with generative-AI programs from them, uploading them to third-party 3D-generation services, and using them to promote generative AI. Model training on your Claude sessions is switched off (date in `synty-manifest.json`), and you chose to keep exactly the licence's lines:
1. **Allowed:** agents import the packs with Unity's importer into `Game` (pack folders gitignored and listed in `synty-manifest.json`), run Unity's URP material conversion, audit them (`artifacts\synty_audit.json`: names, counts, triangles, pink materials), look at them in Unity and in screenshots, and place, scale and arrange their prefabs in scripts and scenes.
2. **No AI-made or AI-edited models:** no reshaped, cut, merged or regenerated Synty meshes. The road bake and scene tools store **prefab GUIDs only** and never merge Synty geometry into road-chunk or any other baked meshes (`Art.NoSyntyInBakedMeshes`).
3. **Nothing uploaded:** no Synty file or render goes to an AI 3D or image generation tool or any third-party generation service.
4. **Nothing committed:** Synty files never enter git. Files inside the pack folders are changed only by Unity itself; materials we make for Synty props live in our own `Assets\Art\SyntyOverrides\` (committed; they reference Synty textures by GUID only).
5. No Synty asset is reachable from any mod SDK or mod-loading path (`Content.NoSyntyInModPath`; Synty's licence also excludes game-creation software, U17).
6. No agent ever uses your Synty login. Synty's written answer to the email in `docs\approvals\QUEUE.md` is optional extra reassurance, not a prerequisite.

Buy one-time licences, not SyntyPass (07 §10.4). Uses: street furniture placed by the road bake (STOP/YIELD signs, signal heads, hydrants, benches, bins; 05 §10), work-site and bog dressing, placeholder traffic. Canon Millbrook buildings stay the frozen Blender models (03 §7). Suggested first buy: Town + Construction (+ Swamp Marshland), about $155 (07 §10.2).

---

## 5. Online plan

### 5.1 Stack (04 §3.7)

| Layer | Choice |
|---|---|
| Netcode | NGO 2.13.3 on 6000.3 (3.x after the 6.7 move); custom compact messages via `CustomMessagingManager`, not stock `NetworkTransform` (04 §3.4) |
| Single player | NGO local host on NGO's own `SinglePlayerTransport` from **M5** (shipped in the bundled 2.13.0; host-only by design) [T17, S20]; fallback: UTP bound to 127.0.0.1 on an ephemeral port. Spike S-NET-1 shrinks to "the single-player suite passes through it" |
| Friends over the internet before Steam (**M8 default**) | Unity Relay join codes via `com.unity.services.multiplayer` 2.4.1 Sessions: no port forwarding, works behind carrier-grade NAT, does not show anyone's home IP. Needs the Unity Gaming Services project you create (U11a; no payment card is needed for Relay). Sessions capped at 8 players; you set a usage alert in the UGS dashboard, because going over a free-tier limit blocks all UGS services for the project until payment details are added [S7, C6] |
| LAN, direct IP ("advanced" in the menu), loopback tests, dedicated servers | Unity Transport 2.7.4, UDP 7777. Direct IP needs the host to forward UDP 7777 and shows the host's IP; the menu says so in plain words. Automated tests and soaks bind 127.0.0.1 only; only `Host.bat` binds 0.0.0.0, and `PLAYTEST.md` tells you to expect one Windows Firewall prompt and to allow private networks [S23] |
| Steam friends and public lobbies (M10) | Facepunch.Steamworks 2.5.2 (one copy) + the vendored Facepunch transport source over **Steam Datagram Relay** (relayed, NAT-proof, hides IPs) [T6]; AppID 480 only on this PC for development: builds with AppID 480 never leave it [C10] |
| Testing | Multiplayer Play Mode 2.0.2 (4 virtual players), Multiplayer Tools 2.2.12 network simulator (100-200 ms, 2 % loss) and its stats monitor for the bandwidth gates, multi-instance players via `soak.ps1` |
| Fallback | Mirror 96.x if NGO blocks (all netcode lives in `Backroads.Net`, enforced by tests); our own `SteamSocketsTransport` if the Facepunch transport misbehaves |

### 5.2 Authority model

| State | Authority | How it moves |
|---|---|---|
| Player vehicle (pose, velocities, wheels, drivetrain) | **owner client** (04 §3.2) | `VehicleStateMsg` 38 B, 20 Hz, unreliable-sequenced, batched per recipient; host relays by interest tier |
| Engine (engine-sim) | owner; never the server | rpm, pedal, load, gear, clutch, flags inside `VehicleStateMsg` |
| Validation | host/server | speed > spec vmax × 1.15 + 2 m/s, jump > v·dt + 0.5 m, outside the island, > 3 g sustained → warn, then kick (P-online §2.2) |
| Terrain deformation | owner computes integer stamps, **host orders them** | `StampBatchMsg` reliable every 0.1 s; host log rebroadcast; 1 Hz tile hashes; mismatched tile resent (4 KB raw `DeltaUm` + u8 layers, deflated); late joiners get all non-zero tiles (06 §5.6) |
| AI traffic | host | `TrafficReplicator`: 7 B lane states (`lane`, `s`, `speed`) at 10 Hz; full state when knocked off its lane (04 §3.2) |
| Economy (money, offers, loads, payouts, fuel, tow) | host | `IEconomyAuthority` requests as `[Rpc(SendTo.Server)]`; deliveries detected by the host's own ring triggers (speed < 1.3 m/s, 1.6 s timer) |
| Time of day | host | `ClockService` at 1 Hz; clients extrapolate |
| Fallen trees, knocked props | host confirms an owner's report | `TreeFellMsg`; 5,265-bit set for late joiners |
| Trailers, straps, winch to another vehicle | **owner of the puller** while coupled | §5.4 |
| Contact between a local car and a remote puppet | the side that detects it | `ContactImpulseMsg` (point, normal, impulse) to the other owner |

No prediction or rollback: engine-sim and PhysX are not deterministic (01 §6.3, 06 §4.6). The 5,265 trees, 4,013 colliders and buildings are data with stable indices, never NetworkObjects.

### 5.3 Messages, bandwidth, interest management

```csharp
// All wire structs: [StructLayout(LayoutKind.Sequential, Pack = 1)] + a size-assert test [T13]. Draft until frozen at M8 [S20].
struct VehicleStateMsg { ushort tick; ulong pos /*3x21 bit over 1,024 m*/; uint rot /*smallest-three*/; half3 vel, angVel;
  sbyte steer; byte throttle, brake, flags0; ushort rpmQ /*0.25 rpm*/; byte load, clutch; sbyte gear; byte flags1, boostOrEngineBrake, reserved; } // 38 B
struct RutStamp { int xMm, zMm; ushort radiusMm, depthMm, rateUm; byte bermPct, flags, wearAdd, surfHint; }                                    // 18 B
struct TrafficLaneState { byte car; ushort lane; ushort sQ; byte speedQ; byte flags; }                                                           // 7 B
```

(Revision 1 called these 40 B and 16 B. The fields add up to 38 B and 18 B packed; with default C# alignment `VehicleStateMsg` would be 48 B.)

- Remote vehicles: kinematic puppets on layer `Puppet`, Hermite interpolation on a 100 ms buffer; within 60 m of the local car, extrapolated to the present with projective velocity blending, at most 250 ms (04 §3.2).
- **Batching** [T18]: the host sends **one** snapshot message per recipient per 20 Hz tick, with that recipient's vehicles concatenated, under a registered message id (no per-vehicle named message, which would add about 10 B each). Stamps, clock and traffic ride in their own batched messages.
- **Overhead model**, written into every bandwidth gate: host upload = recipients × 20 Hz × (vehicles × 38 B + NGO batch header + 40 B UDP/IP/UTP headers per packet, 04 §3.3) × 1.05, plus stamps. At 16 players that is 15 × 20 × (15 × 38 + about 50) ≈ 186 KB/s for vehicles alone; at 8 players about 44 KB/s. Gates compare the Multiplayer Tools measurement with this model and with a hard cap.
- Budget: ≤ 12 KB/s down per client at 16 players; stamps ≈ 4 wheels × 20 stamps/s × 18 B ≈ 1.4 KB/s per vehicle in mud (06 §5.6, corrected to 18 B).
- `InterestManager` (M10): 64 m spatial hash; Full ≤ 250 m (20 Hz vehicles, 10 Hz AI), Mid 250-600 m (5 Hz / 2 Hz), Far (1 Hz map dots); 1,200 B per client per tick with priority accumulation; stamps sent at once only within 300 m, otherwise pulled on approach (P-online §5.3).
- `ContentHandshake` sends the content manifest hash (canon, roads bake, content packs) in connection approval; a mismatch is refused with a readable reason.
- Anti-cheat: none at launch; host checks above, stamp sanity (radius/depth within spec, ≤ 4 per wheel per 0.1 s, within 3 m of the owner's wheels), Steam auth tickets, per-server ban lists keyed on SteamID (04 §4; server logs store hashed SteamIDs by default, §5.9). A cheating player-host cannot be stopped; dedicated servers are the answer.

### 5.4 Coupled bodies: one owner while attached (P-online §5.5)

| Coupling | New owner | Flow |
|---|---|---|
| Trailer hitch, fifth wheel | tractor's owner | `AttachMsg` → `OwnershipBroker` → `NetworkObject.ChangeOwnership` → new owner restores `BrvState` from the latest snapshot and creates the `ConfigurableJoint` (06 §6.1) |
| Winch or kinetic strap to another player's vehicle | puller's owner | as above; the towed player sees "Being towed by X" |
| Winch to a tree, rock or lamp | none (static anchor) | local rope row; over-tension uproot reported to the host |
| AI car pushed by a player | stays host-owned | `ContactImpulseMsg` to the host |

Ownership returns 1 s after detach through the same snapshot/restore, whose round trip is tested to < 1 mm in the vehicle conformance suite.

### 5.5 Sessions and hosts

Default 8 players in the alpha, 16 on Steam (24 with interest management), 32-48 on dedicated servers (04 §3.3). NGO client-host has no host migration: autosave every 60 s (minimal world save from M8), a clean "host left" screen, one-click "host again with the same players" (04 §3.2). Dedicated server: `com.unity.dedicated-server` 2.0.2 profile, headless, no rendering, audio or engine-sim; built as **Linux Mono x64** (`BackroadsServer.x86_64`; Mono needs no IL2CPP cross-toolchain; Windows server optional) once you add "Linux Dedicated Server Build Support" (U4b), and soaked inside this PC's WSL2 Ubuntu-24.04 with Windows bot clients over localhost [C5]; `server.cfg` (name, password, max players, traffic density, autosave interval, ban list) as in Motor Town (04 §3.1); released later as a free Steam tool app players run or rent.

### 5.6 Persistence

`saves\<slot>\` (single player and listen hosts) or `server-data\<world>\`: `world.json` (save schema version, canon and content hashes for information, region, time, AI seed), `tiles.bin` (sparse deflated tiles with non-zero version, each with the hash of its `H0` tile), `trees.bin` (fallen-tree bits), `vehicles.json` (parked vehicles with snapshots), `players\<PlayerId>.json` (money, stats, garage, active job; 03 §4.25 fields plus a version). Atomic writes (temp + rename). Progression is per server by default (U16); Steam Cloud for your single-player profile from M10.

**Saves survive content drops** [S6]:
- The **save schema version** (with a `SaveMigrations` chain, one step per version) is separate from the multiplayer `ContentHandshake` hash. Adding content (a vehicle, an engine, a livery) never invalidates a save; only the handshake requires matching content between players.
- Each tile in `tiles.bin` stores the hash of the `H0` tile it was made against. On load, a tile whose base changed (for example a road-plate edit near a junction) is reset to undeformed; all other tiles load as saved, and the load report says how many were reset.
- A garage vehicle whose content has gone is refunded at its purchase price; a parked vehicle whose content has gone is removed.
- From `m9` on, every milestone tag commits a fixture save to `Game\Assets\Tests\Saves\m<n>\`, and every later gate runs `Save.LoadsPreviousMilestones`.
- M8 ships the **minimal world save** (tiles, trees, time, parked vehicles) for "host again"; M9 adds profiles, garage and economy [S15].

### 5.7 Cost to you

Corrected in revision 2 [C5, C10, C11]:
- **Steam Direct:** $100 per app, once. Steam's own pages say the paperwork takes 15-30 days, there is a 30-day wait between paying the fee and releasing, a Coming Soon page must be up for at least two weeks, and review takes 1-5 days. So you start U11b at M8 (week 17) for an AppID by M10, and the earliest public Steam release is fee date + 30 days.
- **SDR, lobbies, Cloud, Workshop:** no published charge. That is "no published charge; confirm in the Steamworks admin pages", not a guaranteed $0 (04 §3.5 notes dedicated-server SDR use needs a talk with Valve). Whether a free dedicated-server tool app costs another $100 is unknown (04 §3.7 item 4); ask Steamworks support before M12.
- **Community dedicated servers:** $0 to you.
- **Optional official EU server:** a self-managed **Linux** VPS (Hetzner CX23, 2 vCPU / 4 GB) ≈ €5/month (04 §3.5). A Windows server costs far more: Windows is not an official Hetzner Cloud image, and licensed Windows on a dedicated server is about €55+/month. Hence the Linux server build (§5.5).
- **Unity Relay** (friend sessions before Steam): the free bandwidth is 3 GiB per average CCU, at most 150 GiB/month. At 1k monthly players (about 8 average CCU, 04 §3.6) that is about 24 GiB free, not 150 GiB, so about (279 − 24) × $0.09 ≈ **$23/month** (≈ $48 if both directions are billed), and more if the measured traffic exceeds 04's 32 B, no-mud model. From M5 every milestone report prints "Relay cost per 1k monthly players" from the bytes the net probe actually measures. Relay stays on the itch.io/test path, capped at 8 players per session, with your usage alert.
- **Unity Personal:** free up to $200k of revenue and funding; the splash screen is optional in Unity 6; no runtime fee (04 §4).

### 5.8 Single player and multiplayer at each milestone

| Milestone | Single player | Multiplayer |
|---|---|---|
| M0-M3 | yes | none; net-ready: architecture tests, authority interfaces, integer stamps (M2), `BrvState` snapshot round trip (M2), `ServerHeadless` canary and server compile check every CI run |
| M4 | yes | net probe (agents only): 2-4 instances on loopback with network simulator, classic vehicles, ruts, determinism gates |
| M5-M7 | yes, **through the local host** (`SinglePlayerTransport`) from M5 [S20] | **LAN preview** (`Host.bat` / `Join.bat`: two windows on one PC or two PCs at home), classic vehicles, shared ruts, no traffic, marked "preview" |
| **M8 Online alpha** | yes | 2-8 players by **Relay join code** (default, U11a) or LAN; direct IP under "advanced"; BRV v2 vehicles with engine-sim, remote engine sound, AI traffic, mud, winch and strap ownership transfer; minimal world save and "host again" |
| M9 | yes | jobs and money host-authoritative; per-host saves with profiles; late join with tiles |
| M10 | yes | Steam lobbies, friend invites, SDR, auth tickets, interest management, 16 players; Steam Cloud (all through Facepunch) |
| M12 | yes | Linux dedicated server build (needs U4b), 32 players, persistent world |
| later | yes | Workshop mods (U17, with its conditions), global progression (U16) |

### 5.9 Privacy [C7]

Online play makes the game handle personal data: SteamIDs, IP addresses (direct IP and dedicated servers), ban lists and per-player profiles. If you run an official server, you are the data controller for EU players under the GDPR. The plan keeps the data small and documented:

| Data | Where | Why | Kept for |
|---|---|---|---|
| SteamID (from M10) | host memory; `players\<PlayerId>.json`; ban list | identity, progression per server, bans | profile: until deleted on request; ban list: until lifted |
| IP address | host and server memory only; server logs | connecting | logs 14 days, then deleted |
| Player profile (money, stats, garage) | the host's or server's save folder | progression | until the world is deleted or on request |
| Crash reports, F10 answers, session metrics | your own PC (`LocalLow\…\playtest\`) | fixing bugs | until you delete them; never uploaded by the game |
| Unity Relay / Authentication (M8 on, U11a) | Unity Gaming Services | joining friends without port forwarding | per Unity's terms; no profile data stored there in v1 |

- `server.cfg` logs **hashed** SteamIDs by default; `PRIVACY.md` (repo root, and linked from the in-game Credits panel) has the table above and the delete-on-request procedure for an official server (delete the player's profile file and log lines; confirm within 30 days).
- `PRIVACY.md` must exist before any public build (itch.io or Steam). No text chat in v1. No analytics, no hardware statistics (§1.3), checked by `m0.net.egress`.

---

## 6. Milestone roadmap

### 6.1 Order and dependencies

```
M0 Bootstrap (wk 1) ─► M1 Millbrook + Engine Bench (wk 2) ─┬─► M2 First drive (wk 3-4) ───────────────────────────┐
                                                           ├─► R lane: road bake from wk 3 ─► M3 Proper intersections (wk 5-6)
                                                           └─► V lane: BRV v2 from wk 4 (after V0); V′: PhysX chassis wk 5-6 ─┴─► M4 VS-1 (wk 7-9)
M4 ─► M5 Pretty Millbrook + LAN preview (wk 10-11) ─► M6 Living town (wk 12-14) ─┐
M4 ─► M7 Offroad (wk 15-16) ──────────────────────────────────────────────────────┼─► M8 Online alpha (wk 17-18) ─► M9 The Backroads game (wk 19-21) ─► M10-M14 drops
N lane: net probe (M4) ─► LAN preview + single player on the local host (M5) ─────┘
Art: A0 (M0) ─► A1 (M2) ─► B (M3) ─► C (M4) ─► D (M5) ─► E truck body (M6) ─► pickup (M9) ─► semi (M10) ─► rally (M11) ─► 6x6 (M12) ─► GT (M13) ─► trophy (M14)
G lane: economy rules from wk 15 (during M7) ─► M9
```

Weeks are planned weeks; §6.3 gives the latest likely week for each milestone. M6 and M7 depend only on M4/M5 and can swap (U7); the dependency set for each order is written under M6.

**Definition of done** [S4]:
- **Headline gates** (listed per milestone below) decide whether your build ships. They always include: the `-br-smoke` run on the exact Client build you receive, canon identity (oracle hashes and every `Canon` test), and the milestone's "You play" scenarios as player autotests with no crash.
- **Lane gates** (everything else) are reported in `docs\milestones\M<n>.md` with their measured numbers; a failed one adopts its named fallback or is carried over to the next milestone with an owner, and never holds back your build.
- Every milestone also delivers `builds\M<n>\` with `PLAYTEST.md`, F10 items, `THIRD_PARTY_NOTICES.txt` and a contact sheet; `docs\milestones\M<n>.md` (gate table, screenshots, measured numbers, known issues, decisions due and the defaults that applied); a local tag `m<n>`; `HANDOFF.md` files; and the next milestone's task cards.

**Continuous gates from M2 on** (nightly tier, on `main`):
- canon identity (S3-G1/G4), `Repo.NoCrInHashedFiles`, `Content.NoRealMarks`, `Licence.NoticesComplete`, LFS budget, bake determinism (from M3), `Save.LoadsPreviousMilestones` (from the `m9` tag);
- a 10-minute audio underrun check in the player at 60 and 30 fps;
- this laptop at High, 2560×1440: p95 frame ≤ 16.6 ms until M5 and ≤ 11 ms after; simulation main-thread time per rendered frame p95 ≤ 3.5 ms;
- the min-spec proxy (§1.9: 6 E-cores, Low, 30 fps, 1920×1080): engine real-time factor ≥ 1.5, main thread ≤ 70 % of the 33 ms frame (≤ 23 ms) and simulation ≤ 12 ms per rendered frame at p95, 0 underruns in 10 min [S8, T7].

---

### M0 Bootstrap (week 1; ≈ 11 AD: I 5.5, V 2, W 3, A 0.5)

**Deliverables:**
- Repo (§1.2) with docs copied; `AGENTS.md` + `CLAUDE.md`; `OWNERS.json`; `third-party.json`; `.gitattributes` with LFS, line-ending and `-text` rules and the UnityYAMLMerge driver; the proposed agent allowlist and Synty deny rules in `docs\approvals\` for you to accept (§1.11); `docs\approvals\QUEUE.md`; `HANDOFF.md` template.
- `env-check.ps1` (incl. E-core map and power source); probe evidence preserved; engine-sim vendored from git blobs and `vendor-check.ps1`.
- Native CMake skeleton building `scs` + `enginesim_core` into `EngineSimPlugin.dll` exporting `esp_version` and `esp_build_info`, with `crash_guard.cpp`.
- Unity project from the URP template with `ProjectSetup.Apply` (incl. Input System, privacy settings, `maximumDeltaTime`, `GameInfo` placeholders); all asmdefs as stubs; `BrCoords`; Architecture and Repo tests; server compile check and the `ServerHeadless` build.
- CI scripts with the three tiers, the machine locks, `worktree-new.ps1`, `backup.ps1`, `lfs-budget.ps1`, `notices.ps1`; `BuildCli`; `AutotestRunner`, `SmokeRunner`, `MetricsRecorder`, `ContactSheet`, `PlaytestChecklist`, `CrashCollector`, `SessionMetrics`.
- Oracle snapshot with the determinism module and the typed-prop wrappers; export overlay; **CanonData v1 exported and committed**. Axis canary.

**Headline gates:**
- `env-check.ps1` passes; `vendor-check.ps1` reports 0 differences; `build-native.ps1` green (`esp_smoke`: DLL loads, `esp_version() == 1`).
- `oracle.ps1 -Export`: the seven layer SHA-256s equal 03 §3.2 (H `a8908d7f…`, BASE `2eda4082…`, SURFACE after props `8788d405…`, ROADD `8d31f109…`, MUD `2192f4c8…`, WEAR `85473f6f…`, TINT `08452b46…`); counts: 5,265 trees (Pine 933, Spruce 447, Oak 1,005, Birch 798, Poplar 182, Maple 140, Bush 1,491, Snag 18, Reeds 251), 4,013 colliders, 78 lamps, 59 lots, 59 model placements (each with `y`, `sx`, `sz` and its matrix), 77 lamp-post records with `faceRot` and `h`, 87 footprints, 7 rings, 5 parking bays, 2 boats, 24 roads with 1,722 points, traffic 21 nodes / 53 lanes / 108 connectors / 5 loops, `rngCalls` 1220; `static_props.glb` bucket vertex counts bld 261,066 · metal 27,276 · winLit 14,436 · winDark 11,952 · bulb 6,228 · neon 216 · signRed 720 (03 §3.2).
- A second export run gives an identical manifest, ignoring `exportedAt`, `userAgent` and `reference/shots/*`; the shots match the first run perceptually (SSIM ≥ 0.99), and the manifest names the renderer used [T4, S16].
- `build-player.ps1 -Profile Dev -Milestone M0`, `smoke` exit 0; the `ServerHeadless` canary logs `SERVER BOOT OK` and exits 0; `Build.ServerContents` passes.

**Lane gates:** EditMode `Build.ProjectSettings` (every §1.3 setting), `Architecture.*` (§1.5 rules 1-8), `Repo.NoCrInHashedFiles`, `Repo.OwnersRespected`, `Core.CoordsCanary` (JS +x → Unity −x; JS yaw +π/2 → −90°), `Art.Canary`, `Licence.NoticesComplete`; `Audio.BatchmodeDspRuns` (decides the `PlayerOnly` tagging, §1.9); autotests `m0.crash.recover` and `m0.net.egress`; `gate.ps1 -Tier merge` measured at ≤ 20 min.

**You see:** `builds\M0\Play.bat` opens a grey test yard with the canary markers on a turntable and the F10 checklist ("Did the game open? Did F10 show this list?"). It proves the toolchain works on your PC.

### M1 Millbrook in Unity + Engine Bench (week 2; ≈ 15 AD: W 8, V 5.5, I 1.5)

**Deliverables:** `CanonImporter` (menu *Backroads > Import Canon v1* and `ImportCli`); `CanonRuntime`; `WorldGrid` with Burst float queries and the double-precision reference path; `ColliderGrid`; terrain chunks v0; buildings (frozen GLB, canon colours), trees, props from `static_props.glb` in 64 m chunks and the typed records; JS-style ribbons, water, sun + gradient ambient; `FreeFlyCamera` with 12 bookmarks and POI keys; **ESP v1, trimmed** [S14]: FREE, FOLLOW_RPM, `esp_dyno_sweep`, step-indexed results, coast mode with borrowed steps, renderSync ring with the frame-sized target, stats, tap, builtins `f4_boxer_a` + `v8_cross_a` + `i4_synth`, AVX2 FIR, `esp_cli`, ctest suite incl. `esp_cosim_matrix`; piranha and `mr_sanitise` if U3c arrives; `Backroads.EngineSim` C# side; **Engine Bench** scene (roster engines in Client builds; the full list only in Dev); spike S-AUD-1 report; Credits panel with `THIRD_PARTY_NOTICES` and the OpenStreetMap credit (this is the first build that carries the DLL) [C1, C8]; min-spec proxy harness; server canary loads canon. `esp_calibrate_inertia`, `esp_render_offline`, `esp_set_quality` and `esp_set_worker_options` move to M4, where conformance first uses them.

**Headline gates:**
- EditMode `Canon.ManifestHashes`, `Canon.Counts` (as M0), `World.HeightProbes` (reference path: 1,000 probes, height within 1e-5 m, surface exact, normals within 1e-4), `World.RayHits` (reference path: 12 × 4,096 rays within 1 mm of JS; at most 0.1 % outliers, each a grazing ray with |dir·n| < 0.05 and within one 0.5 m march step) [T14], `World.PlacementsRoundTrip` (every building and tree transform and every typed record of §1.8.2 within 1e-4 m and 0.01° after the flip), `Render.TerrainLod0BitExact` (all 256 LOD0 chunk vertex positions bit-exact against `BrCoords.Pos(i·0.5−512, H[k], j·0.5−512)`), `Render.PaneNamesResolve` (pane names found for all 11 models; House_B 22 panes), `Render.StaticPropsBuckets` (the 7 bucket vertex counts and hashes).
- Autotest `m1.shots.canon`: 24 screenshots + contact sheet next to the JS shots.
- Native ctest: EJ25 (`f4_boxer_a`) cranks and idles at 700-900 rpm within 3 s of ignition; WOT dyno torque 229 ± 7 N·m at 4,500 ± 500 rpm and 170 ± 6 hp at 6,500 rpm (02 §6.2); FOLLOW_RPM tracks a 1,000 → 4,000 rpm ramp with < 30 rpm RMS; step accounting over 600 s = round(Σdt·f) ± 1 at timeScale 1 and 0.5.
- Autotest `m1.bench.revcycle` (player, at 60 and at 30 fps): 20 s scripted rev cycle; pitch per the §1.9 rule ±3 %; 0 underruns; clipping < 0.1 %.

**Lane gates:**
- Rendered WAV pitch per the §1.9 rule at 25/60/100 % pedal (02 §6.7); 10-minute ASan soak with 0 reports, < 1 MB growth, 0 steady-state underruns (nightly).
- `esp_cosim_matrix` report for L = 1-4 and replayed 30/45/60/120 fps frame traces (Δv, tracking RMS, limiter overshoot); **ESP ABI v1 freezes on this report** (§3.4) [T1].
- Real-time factor (§3.4): `f4_boxer_a` at Ultra ≥ 1.5 E-pinned; at Low ≥ 1.5 on the min-spec proxy. Main-thread cost of engine-sim calls ≤ 0.10 ms per `FixedUpdate` at p99.
- Perf `m1.perf.flythrough`: p95 frame ≤ 16.6 ms at 2560×1440 High; all 256 chunks built ≤ 1.5 s at load; on the proxy p95 ≤ 33 ms at Low [tune].
- With piranha: `esp_all_engines` as a **report table** for all 25 engines (compile, crank, idle ±15 % for 5 s, 10 s at WOT with no NaN, the 9/10/12-cylinder ones included, which proves the `lastValveLift` override); hard only for the roster engines of §3.10 [S14, T13].
- `docs\spikes\S-AUD-1.md` names the remote-audio path, with measured attenuation (≥ 12 dB at 20 m) and pan. `Licence.OsmAttribution`; `Licence.GeneratedHeadersIntact` (with piranha).

**You see and play:** fly over Millbrook (WASD + mouse; keys 1-0, −, = jump to the 12 bookmarks; same views as the contact sheet), with `Play.bat` open beside it: same hills, roads, houses, trees. Open the Engine Bench: tap I for ignition, hold I to crank the flat-four (like turning a key), hear it catch, rev it with W, press "Dyno sweep" to see its torque curve. The Credits screen lists every licence. F10: "Same town? Does the engine sound like a real engine?"

### M2 First drive (weeks 3-4; ≈ 28 AD: V 14, W 9, A 2, feedback reserve 3)

**Deliverables:**
- **BRV V0:** verbatim `vehicle.js` (suspension rays against WorldGrid, PGS tyres, JS engine and automatic gearbox, low range, AWD-on-low, hull contacts, 2D statics against `ColliderGrid`, water, mud grip and sink, winch v0) at 1/120 s with render interpolation (03 §4.14 phase 1), already shaped as `BrvState`/`VehicleStepJob` with snapshot in/out. Hatch, rally, pickup and truck drivable; truck is the spawn default; the dozer waits for M7, where it is built once on v2.
- **BRV v2 head start (V, behind a flag, after V0):** shaft degrees of freedom and the row skeleton, so v2 gets its full 13 AD over weeks 4-6 [S3].
- **Deformation (W):** integer µm `StampKernels` for `rut`, `brush`, `relax` (+ `cutBlade`/deposits stubbed for M7) on `DeltaUm`; `RutStamp` 18 B frozen; `DirtyRectQueue`, partial chunk updates, tree re-snap (03 §4.4, 06 §5.6).
- **Engine sound:** `LegacyJsEngine` drives; engine-sim runs FOLLOW_RPM with the §3.5 rpm map from the exhaust socket; HUD tach shows the mapped rpm. Truck → `v8_bigblock_a` (piranha, or the C++ port done this milestone), pickup → `v8_cross_a`, rally → `f4_boxer_a`, hatch → `i4_vvt_a` or `i4_synth`.
- **Controls and screens** [S13]: one binding table, `Content\core\bindings.json`, for keyboard and gamepad, frozen as a contract. It keeps the JS bindings of 03 §4.26 (S brake/reverse, E get in/out, Q/Z blade, X low range, F1 help) and adds: tap I ignition, hold I starter; Left Shift / Left Ctrl gear up / down, hold V clutch and F4 manual/auto (used from M4); K diff lock (M7); Esc menu; F3 debug; F10 checklist. Every action has a gamepad binding (for example Start = menu, hold Start = reset upright; D-pad up/down = gear; D-pad left = ignition, hold = starter). EditMode `Input.NoDuplicateBindings` fails on any key or button bound twice within one action map. Plus the Esc menu (Resume / Controls / Settings / Credits / Quit), the F1 controls overlay, and `settings.json` in `persistentDataPath` with a version field (quality, volumes, resolution, bindings).
- Input System actions (Driving map, deadzone 0.14; 03 §4.26); chase and hood cameras; minimal UI Toolkit HUD (speed, gear, rpm bar, surface, "sunk N cm"); F3 overlay; garage spawn bays with 240 settle steps.
- Golden traces recorded from the oracle (§1.8.5: seeded, no traffic, v1 terrain). Art A1.

**Headline gates:**
- PlayMode `Vehicle.GoldenTraces` (per-second statistics, 03 §3.6; pure v1: `RoadPlates` and `RoadColliders` off): truck 0-80 km/h time ±7 %; pickup 0-100 km/h ±7 %; braking 80 → 0 distance ±8 %; loaded truck low-range quarry climb time ±10 %; hatch Blackwater Bog sink depth at 10 s ±15 %; winch 15 m to a tree completes. **Fallback:** if a trace misses after tuning, the build ships with the delta recorded and you judge the feel (U5a).
- Autotest `m2.drive.route9` (player, at 60 and at 30 fps): pitch per the §1.9 rule ±3 % through the gears; 0 underruns; p95 frame ≤ 16.6 ms at High.
- `Input.NoDuplicateBindings`; `Settings.RoundTrip` (settings survive a restart; an unknown version falls back to defaults with a message).

**Lane gates:**
- EditMode `Deform.MatchesJs` [T2]: the 10,000 reference calls replayed in order, reported as a curve of max height error against call index: ≤ 0.5 mm over the first 1,000 calls, ≤ 2 mm over all 10,000; displaced volume ±2 %. `Deform.SubMmConverges`: a rut of 0.3 mm per call reaches JS's target depth within 1 %. `Deform.Deterministic` (same stamp log → identical tile hashes, Burst on and off; 06 P6).
- PlayMode `Vehicle.SettleParked` (4 vehicles on the bays settle within 240 steps to |v| < 0.01 m/s), `Vehicle.SnapshotRoundTrip` (< 1 mm), `EngineSim.FollowInVehicle` (engine-sim rpm tracks the mapped rpm < 40 rpm RMS; per roster engine, real-time factor ≥ 2.0 at High unpinned and ≥ 1.5 on the proxy, so 20 kHz engines get their double budget automatically) [T7].
- Simulation main-thread time per rendered frame p95 ≤ 3.5 ms at High on this laptop and ≤ 12 ms on the proxy.

**You play:** start in the truck at the Millbrook garage and drive anywhere: Route 9 to the fuel station, the dirt road to the farm, into the bog (it ruts and grabs like the browser game). Switch vehicles at the garage. You hear a real big-block V8 rise and fall with your foot. Esc pauses, F1 shows the controls, and your settings are remembered. F10: "Does it handle like the browser game? Does the sound follow your foot?" (first feel sign-off, U5a).

### M3 Proper intersections (weeks 5-6; R lane from week 3; ≈ 41 AD: R 14, V 10, V′ 3.5, W 5, A 3, feedback reserve 5.5)

**Deliverables:**
- **Roads bake** stages R0, R1, R3 of 05 §9 plus plates and controls: graph import and cleanup (T-splits incl. the 6 dirt ends, 1 m clustering, town-zone class split into MainStreet/Highway, L-bends; **major road by class first**, fixing nodes 2 and 5, 05 §5.1); corner fillets with the radius back-off (6 m downtown at nodes 7 and 9, 7.5 m residential, 12 m in the fuel yard, 9 m + 10 m paved apron at dirt roads; 05 §5.3); mouths and stop lines at the flattest spot (nodes 5 and 17 are −9 %); junction polygons, RingMesh, crown warp, major-road plateau (05 §5.4-5.6); MUTCD markings (double yellow on MainStreet, rural broken yellow and edge lines, 0.45 m stop lines, continental crosswalks downtown, none on residential streets, nothing carried through junctions; 05 §5.8); 64 m road chunks with LOD0/LOD1 and baked MeshColliders (05 §5.13); road plates into `CanonData\v1.1`, applied to `H0` **and** `BASE`, with lot pads re-applied (05 §5.10) [T10]; controls and placeholder STOP/YIELD signs per 05 §6.2-6.3 (two-way stops where streets meet highways, the all-way stop at node 16, stops on dirt approaches). The square (nodes 0+1) stays "legacy" until U8.
- **BRV v2 core (V, behind a flag, continuing M2's head start):** wheel ω and shafts as PGS degrees of freedom, clutch/gearbox/open and locked diff/brake rows **with ABS** [S3], MF secant tyres with the 06 §3.7 tables, rolling resistance, lumping; `BrvTestTrack` scene on an analytic WorldGrid (flat pad, 10/20/30/35 % ramps, 150 mm steps, mud patch, 30 m skidpad); vehicle conformance suite v0.
- **PhysX chassis (V′, 06 V1)** [T8, S2]: every vehicle, V0 and v2 alike, gets a `Rigidbody` (`useGravity = false`, BRV impulses), `UCX_` convex hulls (generated boxes for today's GLB vehicles until FBX bodies arrive), and wheel sphere colliders against `Static | Prop | Puppet`; the 4,013 canon colliders become PhysX statics on layer `Static` (circles → capsules, boxes → boxes, `y0`-`y1`); this replaces the JS 2D statics and vehicle-vehicle SAT. Editor validation fails any vehicle `Rigidbody` with gravity on.
- **W:** `SG_Terrain` two-surface port (first pass). **A:** phase B (`VH_Truck_Box75.fbx` replaces `truck.glb`).

**Headline gates:**
- EditMode `Roads.Validation` (05 §8): bake 0 errors in ≤ 5 s, ≤ 60k triangles; every junction surface simple and CCW, area within ±5 % of its connector-corridor union; every mouth vertex shared bit-exactly, seam height error < 1 mm; terrain ≤ surface − 0.03 m 0.25 m inside every footprint and within 0.1 m of the edge 1 m outside; exactly one STOP per controlled approach, on its right, 0.6-2 m from the kerb, ≤ 15.2 m from the intersected edge; stop lines only with STOP; **bake twice → identical hashes**, also in a fresh worktree (which proves the line-ending rules).
- EditMode `Canon.PlacementsUnchanged` (every building, prop, tree, lamp, collider bit-identical to v1), `Canon.PlatesMask` (outside the plate mask H is bit-identical to v1; all 59 lot pads within 0.05 m), `Canon.BaseMatchesH` (§1.8.7).
- Autotests `m3.shots.junctions` (11 junctions, 5 L-bends, 6 dirt aprons, 3 heights, noon and night) and `m3.drive.downtown` (classic truck through nodes 2, 7, 9, 16: no vertical seam spike > 0.3 g); PlayMode `Vehicle.BuildingImpact`: the truck driven into a building at 10 m/s and at 20 m/s stops at the wall with no tunnelling [T8].

**Lane gates:**
- EditMode `Roads.Synthetic` (a 30° Y, a 5-arm star, two junctions 25 m apart, a T on a 15 m curve, an arm shorter than its tangent all bake or fail with a clear message); `Roads.ArmLanesMatchLegacy` (arm lanes within 0.3 m of `traffic.json` at unchanged junctions); `Art.TruckBudget`.
- PlayMode on `BrvTestTrack` (P-risk S2): `Perf.Vehicles.StepCost` ≤ 30 µs per vehicle per step on a P-core (as 06 P1 states it; also reported E-pinned), 50 vehicles ≤ 1.0 ms wall; `Vehicle.StabilityFuzz` 1,000 seeds × 60 s (nightly) with 0 NaN/Inf, |v| ≤ 100 m/s, |ω| ≤ 50 rad/s; `Vehicle.HillHold` 10-35 % for 60 s creep < 1 mm/s; open diff with one wheel airborne spins it up without oscillation; locked diff widens the turning circle; ABS stop from 100 km/h in 36-45 m.
- A Unity self-trace baseline on v1.1 for the trace scenarios, used for regressions from now on [S17].

**Fallbacks:** a node the solver cannot handle gets a hand-authored polygon in `roads-overrides.json`; last resort per node, JS-style overlapping ribbons. BRV v2 convergence trouble → dense LDLᵀ solve for ≤ 16 shafts (06 §9 item 4). PhysX chassis late → V0 keeps the `ColliderGrid` statics and OBB checks against puppet boxes until M7, and the milestone report tells you the LAN preview has no car-to-car contact until then. **Kill criterion:** if fuzz and hill hold cannot pass together by the end of M4, the classic model with FOLLOW_RPM sound keeps shipping and the coordinator writes a re-plan with you (`docs\milestones\replan-brv.md`) listing what changes: M7's lockers and transfer case on V0's low range, M8 netcode on V0, and the semi, 6x6 and GT drops (M10-M13), which need v2 and would slip or shrink [S3].

**You play:** drive downtown: junctions are single clean asphalt surfaces with rounded corners, white stop lines, zebra crossings near the centre, STOP signs on the right; the centre line stops at the junction; side streets ramp onto the main road's camber; dirt roads meet the highway with a short paved apron and a STOP sign. Hit a building and the truck stops against it properly. F10: "Do these look like real small-town intersections?"

### M4 VS-1: engine-sim drives the truck (weeks 7-9; ≈ 37 AD: V 12, N 5, R 6, A 4, W 4, I 1, feedback reserve 5)

**Deliverables:**
- **SLAVED_CRANK coupling** into BRV v2 with the step-lag design of §3.4 (L = 2-4, bounded wait, every command queued, `dT_dw` feed-forward, coast with borrowed steps), baked inertia, PD gains, torque clamp, stale-result fallback, spark-cut limiter + fuel-cut backstop (§3.5); ignition key and starter; the engine can stall and restart. Truck driveline per 06 §7.3 (454, 6-speed ratios 3.10/1.81/1.41/1.00/0.71/0.61, final 5.29) with **AMT** (auto-clutch from rpm, 06 §4.2.1; JS shift-on-ground-speed rules, 06 §4.2.2) and a **manual** mode (F4 toggles; hold V for the clutch; Left Shift / Left Ctrl gear up / down). "Classic engine" stays selectable. Wheel spherecasts against road and kerb colliders (06 §3.5). Vehicle and engine conformance suites v1 run on the truck, the 454 and the EJ25.
- Moved here from M1 [S14]: `esp_calibrate_inertia`, `esp_render_offline`, `esp_set_quality` (runtime tier switch), `esp_set_worker_options` (priority, power throttling off); `EngineBake` writes `J_eff` into each `EngineDef` [T19].
- **R:** roads stage R2: Clipper2 kerb loops, Barrier150 kerbs and gutters, 1.5 m residential walks, storefront walks, depressed corner ramps with detectable-warning texture, dropped kerbs at the 51 driveway and apron cuts (05 §5.7).
- **A:** phase C shaders on the truck. **W:** day/night clock, procedural sky, moon, fog; terrain shader second pass.
- **N:** net probe part 1: `SessionLauncher` (host/join/server over UTP, bound to 127.0.0.1), `VehicleStateCodec` (38 B draft), `SnapshotBatcher`, `SnapshotBuffer`, `RemoteVehiclePuppet`, `ContactImpulseRelay`, `DeformReplicator`, `ContentHandshake`, `BotClient`, `soak.ps1`; S-NET-1 reduced to "the single-player suite passes through `SinglePlayerTransport`" [T17].

**Headline gates:**
- PlayMode `Coupling.P4Matrix` (06 §4.5, §8) **at fixed 30, 45, 60 and 120 fps** (`Application.targetFrameRate` with vSync off, plus `Time.captureDeltaTime` in PlayMode; `-br-fps-cap` in the player) [T1]: clutch dump at 3,500 rpm in 1st; stall by braking in 2nd (manual); WOT rev in neutral; idle in gear against the brakes; full load in top gear at 90 km/h. Pass at every frame rate: no NaN or divergence; stall and restart work; mean rpm on the limiter within redline + 300; and the **tracking error** (Unity crank rpm minus engine-sim crank rpm, high-passed above 2 Hz) stays within ±15 rpm in the 100-140 Hz band **with the engine's own firing frequency ±10 % excluded** [T12]. (The 454 in top gear at 90 km/h runs about 1,830 rpm and fires near 122 Hz; that physical ripple, ±20-30 rpm per 02 §6.1, is meant to pass through.)
- Autotest `m4.vs1.run` (player; at High unpinned, and on the min-spec proxy at Low and 30 fps): scripted lap with a stall, restart, hill start and WOT pull; 0 underruns; torque-map fallback on ≤ 0.1 % of steps in 10 min; stale steps ≤ 0.5 %; coast events only where the script injects a hitch; engine real-time factor ≥ 2.0 (High) and ≥ 1.5 (proxy); simulation main-thread time per rendered frame p95 ≤ 3.5 ms (High) and ≤ 12 ms (proxy) [T7, S8].
- PlayMode `Vehicle.KerbBump` (150 mm kerb at 10 km/h lifts the wheel ≥ 0.12 m); autotest `m4.shots.kerbs`.

**Lane gates:**
- PlayMode `Coupling.InertiaCalibration` (baked J_eff within 3 %, 06 P3); `Vehicle.HillHold` for the truck; `Vehicle.LaunchContinuity` **[tune]**: 0-80 km/h against the M4 self-trace and, for information only, the M2 JS trace. The JS truck (760 N·m, overall first ratio 30.8) and the 454 + AMT (overall first 3.10 × 5.29 = 16.4) differ 2-3× in launch wheel torque, so this is a tuning target, not a regression test [T21].
- `esp_cli bench --affinity E` at Ultra: real-time factor ≥ 1.5 for the 454 and the EJ25.
- EditMode `Roads.KerbsVsCanon` (05 §8 item 4): kerb to building ≥ walk width (1.5 m residential, 1.0 m at downtown corners); every mailbox, tree and lamp outside the walk band or moved by an explicit override; sidewalk cross slope ≤ 2.1 %, ramps ≤ 8.33 %.
- Net probe (P-risk S5): `soak.ps1 -Clients 4 -Minutes 10 -Net 150,20,2`: interpolated remote error ≤ 0.5 m at 20 m/s, near extrapolated ≤ 1.0 m p95, 0 jumps > 2 m; a 10,000-stamp log applied in two processes and in a Burst-off build gives identical tile hashes; a late joiner matches within 5 s.

**Fallbacks:** D fails → PROXY for single-axle drivelines, KINEMATIC for the rest (same ABI); L = 4 fails at 30 fps → the §3.4 fallback order. **Kill criterion, judged on the min-spec proxy** [S8]: if one local engine cannot hold a real-time factor of 1.5 at Low there, engine-sim becomes "sound + dyno map" on that tier (FOLLOW_RPM for every vehicle, BRV on `TorqueMapEngine`) and stays SLAVED_CRANK on the tiers where it holds; the sound is still engine-sim's.

**You play (the vertical slice):** tap I, then hold I: the 454 cranks, catches and idles with a lope. Pull away and hear it labour up the hill past the depot. Switch to manual (F4; hold V for the clutch, Left Shift / Left Ctrl to change gear), brake hard in 2nd and it stalls; restart it. Floor it in neutral and it bounces off the limiter. The truck bumps up kerbs; downtown has sidewalks, corner ramps and dropped kerbs at driveways; the sun sets and night falls. F10: "Does this driveline feel right?" (U5b).

### M5 Pretty Millbrook + LAN preview (weeks 10-11; ≈ 36 AD: W 12, R 8, N 5, A 4, V 3, feedback reserve 4)

**Deliverables:** the M5 look work of §4.2; quality presets with the real-light pools of §4.3 and the F2 graphics menu (saved in `settings.json`); art phase D; spike S-GFX-1 (Renderer Shader User Value from Shader Graph on 6000.3; fallback one glass material instance per building); APV and town reflection probes baked in the lighting-only bake scene (§4.1), timeboxed to 2 AD [T11]; memory budgets measured (§4.3). **R:** `traffic.js` port starts on the legacy `traffic.json` network, hidden. **N:** LAN preview (`Host.bat`, `Join.bat`) and **single player through the NGO local host on `SinglePlayerTransport`** [S20], so M8 needs no migration. **V:** headlights, lamp sockets, `EngineAudioScheduler` groundwork. (Revision 1's 16-client bandwidth, cheat-client, owner-to-owner collision and 30-minute server soak gates move to M8, where the alpha they protect lands [S20].)

**Headline gates:** EditMode `Canon.WindowsLit` (per building, lit panes = number of 1s in its canon `lit` string, matched by pane name); `Look.NoErrorShaders` (no error shader, no missing texture in Millbrook); autotests `m5.shots.canon` and `m5.shots.lighting` (noon, dusk, night × mud 0, 0.5, 1); perf `m5.perf.flythrough-high` p95 ≤ 11 ms at 2560×1440 on High; `PlayMode -Category SinglePlayer` passes through the local host; `Net.LanPreview`: two instances on this PC for 10 min, remote error ≤ 0.5 m p95, identical stamp tile hashes.

**Lane gates:** `m5.perf.flythrough-low` on the proxy p95 ≤ 33 ms at 1920×1080 Low [tune]; `m5.perf.night-low` (21:00, all light pools, Forward+ cluster cost) [tune]; draw calls ≤ 2,000 High and ≤ 1,200 Low [tune]; memory budgets of §4.3; APV inside its timebox (fallback: gradient ambient only); the Relay cost line in the report (§5.7).

**You play:** press ] and [ to move time. Watch the sun set behind the ridge, street lamps and shop windows come on, headlights sweep the road; grass sways, trees move, the lake reflects the sky. Try the F2 menu. Try `Host.bat` and `Join.bat` (two windows, or two PCs at home; Windows may ask once whether to allow the game on private networks: say yes): drive the classic vehicles together and see each other's ruts. F10: "Prettier than the browser version? Any setting that looks wrong?"

### M6 Living town (weeks 12-14; ≈ 39 AD: R 16, V 8, A 8, W 2, feedback reserve 5)

**Prerequisite:** U8. In week 12 the R lane bakes **both** roundabout variants (4-leg with the harbor road realigned, 05 §7.1; 3-leg with a moved T) into a preview build so you choose by driving them; if you have not chosen by the end of week 12, the recommended 4-leg variant applies.

**Deliverables:** roads R4: roundabout (ICD 32 m, 5.5 m circulatory, 3 m truck apron, splitter islands, yield lines; fountain moves to the island; delivery ring moves where you choose); R5-R6: lanes and connectors (right turns concentric with the kerb fillet, left turns as tangent arcs), design-truck swept paths, conflicts, controls, the optional signal at node 2 with fixed timing, street-name blades, speed-limit signs (05 §5.14, §6.3). Traffic AI on the new `LaneGraph`: 12 cars (6 hatch, 4 pickup, 2 truck), ghosts beyond 225 m, materialise within 175 m out of view, pure pursuit, IDM, junction arbitration (MinorStop, AllWayStop first-come-first-served with ties yielding right, RoundaboutYield, Signal), rescue rules (03 §4.21). **BRV LOD1 is defined here** (06 §4.7), together with **v2 definitions for the hatch and the pickup** (on-road driveline, conformance v0), so AI runs on what exists [S15]; `TorqueMapEngine`; `ContentBuild`/`EngineBake` bakes torque maps and loop banks; R1 slot for the nearest AI car. Art phase E: the truck's new modern cab-over body.

**Headline gates:** EditMode `Roads.Lanes` (05 §8 item 5: every approach lane has a connector; every departure lane reachable; no connector tighter than 1/6 m⁻¹ car or 1/9 m⁻¹ truck unless `noTruck`/`encroaches`; conflicts symmetric); PlayMode `Traffic.StopCompliance` (front bumper at rest within 0.5 m of the stop line before proceeding); autotest `m6.drive.town` (10 min among traffic: no crash, no deadlock in view).

**Lane gates:** PlayMode `Traffic.Soak` (Slow, nightly, 30 min): 0 AI-AI collisions in junctions, 0 deadlocks > 60 s, every car ≥ 20 junction movements, all-way-stop order = arrival order, no roundabout entry with a circulating car within 22 m upstream. Perf: player + 12 AI, simulation main-thread time per rendered frame p95 ≤ 4.5 ms at High and ≤ 14 ms on the proxy, traffic ≤ 1.5 ms. Engine bake: torque map within ±3 % of a live dyno sweep. CPU: 1 local + 4 R1 + 12 R2 ≤ 1.6 cores at High unpinned; 1 local + 1 R1 + 12 R2 ≤ 1.0 core on the proxy at Low. Art: your sign-off on the review sheet; `brief.md` with 3+ inspirations and named deviations.

**If you pick offroad first (U7):** M7 then creates the pickup's v2 definition itself and keeps the dozer; BRV LOD1 and the hatch definition stay in M6, which then reuses M7's pickup. Nothing else moves.

**You play:** cars and trucks drive around town, stop at STOP signs, take turns at the all-way stop, go round the roundabout, and you hear them pass. Your truck has its new modern body.

### M7 Offroad (weeks 15-16; ≈ 24 AD: V 9, W 6, A 4, N 2, feedback reserve 3)

**Deliverables:** soft-soil model (Bekker sinkage, Janosi-Hanamoto traction, JS resistance terms, 06 §5.3) blended with the MF curves; compaction; wetness; tyre pressure parameter (CTIS-ready); low range, transfer case, selectable lockers (K), torque-converter automatic; **the pickup's full offroad v2 driveline** on M6's definition (part-time 4x4, rear locker, 10AT, LS engine-sim on SLAVED_CRANK); winch v2 (rope row, drum motor curve, snatch block) and kinetic strap (06 §5.7); **the dozer, built once, on v2** (blade and tracks, `cutBlade`, deposits; 03 §4.15) [S15]; VFX Graph particles with the JS rules; mud on the bodywork; tyre, gravel and wind sounds; reset upright; tow to garage. (The PhysX chassis is already in place from M3.) **N:** `OwnershipBroker` for winch-to-vehicle and strap. **G** starts the economy rules for M9.

**Headline gates:** PlayMode `Offroad.BogStuckRecover` (pickup in 2WD gets stuck in Blackwater Bog: < 0.6 m/s for 5 s at full throttle; with low range + locker it gets out within 30 s; with the winch to a tree it gets out); autotest `m7.offroad.bog` (scripted: stuck, lock, winch out; no crash).

**Lane gates:** golden traces hard for the dozer (20 s blade-cut volume ±15 %) and winch (15 m ±10 %); `Perf.Deform` (four wheels rutting at 60 Hz ≤ 0.5 ms per frame on average at High, ≤ 1.5 ms on the proxy, ≤ 2 chunk rebuilds per frame); vehicle conformance for the pickup (P5 hill hold, 4L 1st at 3,000 rpm = 9.4 km/h ±3 %, 06 §7.3); `Net.OwnershipRoundTrip` < 1 mm.

**You play:** leave the road: tyres dig ruts with berms, the bog grabs the truck, mud flies and coats the bodywork. Use low range, lock the diff, winch out to a tree or have the pickup strap you out. Push dirt with the dozer.

### M8 Online alpha (weeks 17-18; ≈ 25 AD: N 15, V 3, W 2, I 2, feedback reserve 3)

**Prerequisites:** U11a, the Unity Gaming Services project for Relay (if it is not there by week 17, the alpha ships for LAN only and says so); U18's company and product names, because the first saves are written now (if unanswered, the M0 placeholders become permanent). Start the Steamworks sign-up (U11b) now so the AppID exists by M10.

**Deliverables:** §5.2-5.4 for client-host sessions of up to 8 players on BRV v2 vehicles: `SessionMenu` with **Host** (shows a Relay join code), **Join with code**, LAN, and **Advanced: direct IP** (with a plain-words port-forwarding note) [S7, C6]; remote engines on R1/R2; traffic replication; `ClockService`; tree-fall replication; ownership transfer; **minimal world save, autosave and "host again"** (§5.6) [S15]; `StateValidator` and the cheat client (`-br-cheat speed3x|teleport50`), moved from M5; `VehicleStateMsg` frozen at 38 B by ADR [S20]. **Share-Build** step: `build-player.ps1 -Profile Client -Share` writes `builds\M8\share\Backroads-M8.zip` (size budget ≤ 3 GB [tune]) containing the game, `PLAYTEST-FRIENDS.md` (unzip, Windows SmartScreen's "More info → Run anyway" for an unsigned game, the firewall prompt), `THIRD_PARTY_NOTICES.txt`, `licenses\` and `PRIVACY.md`. **You** upload it wherever you choose (recommended: a private itch.io page); agents never upload. AppID-480 Steam test builds never go into the zip [C10].

**Headline gates:** `Net.RelayLoopback` (two clients over Relay from this PC; needs U11a); the net probe gates re-run with BRV v2, mud and traffic; `PlayMode -Category SinglePlayer`; `Save.MinimalRoundTrip` (host saves, quits, hosts again: identical tile hashes, time, fallen trees, parked vehicles); `-br-smoke` on the zip's exe after unzipping it into a fresh folder.

**Lane gates:** a 30-min 4-instance soak (127.0.0.1) ends with identical terrain tile hashes on every client and on a late joiner; AI positions agree within 2 m (p95 1.5 m); host upload with 8 players within the §5.3 overhead model × 1.05 and ≤ 1.0 Mbit/s; head-on collision at 2 × 10 m/s and 150 ms RTT → both owners get the impulse, no overlap > 200 ms, no post-hit launch; cheat client kicked within 1 s; `ServerHeadless` with 8 bots for 30 min < 1 core and < 1.5 GB; 4 R1 engines + loops ≤ 1.5 cores of worker time at High unpinned; the Relay cost line (§5.7).

**You check:** host a session, read the join code to a friend (who downloaded the zip from wherever you put it), or run two copies; see and hear each other's trucks, push each other around, share ruts, strap each other out of the bog; quit and "host again" with the world as you left it.

### M9 The Backroads game (weeks 19-21; ≈ 30 AD: G 14, A 6, V 5, I 1, feedback reserve 4)

**Deliverables:** jobs and economy through `IEconomyAuthority` (boards with 3 offers, cargo table, payout formula, damage, fish timer, refuel, wash, tow; 03 §4.22); the payout and fish-timer references exported from the oracle into `CanonData\refs\jobs\` with the logged uniform stream (§1.8.4) [S14, T4]; full HUD with job tracker, freight board, toasts, rotating minimap and big map from `map.png` (03 §4.23); persistence: profiles, garage and economy on top of M8's world save, `SaveMigrations`, save schema v1 frozen, the first fixture save at the `m9` tag (§5.6); get in and out on foot with a simple character controller and the shovel (03 §4.18); hatch and rally on BRV v2 with conformance (the dozer is already done); traffic bypass manoeuvre; the pickup's new body; `PRIVACY.md` linked from the Credits panel (needed before any public build).

**Headline gates:** `Jobs.DeliveryFlow` (accept, load 1.6 s, scripted depot → farm drive, unload, payout as expected); `Save.RoundTrip` (save → kill → restart: identical tile hashes, money, time, fallen trees, parked vehicles); autotest `m9.play.session` (30 scripted minutes: accept, deliver, refuel, save, quit, reload).

**Lane gates:** `Jobs.PayoutFormula` (1,000 inputs, replaying the logged uniforms, equal the oracle's `jobs.js` results within $1); `Jobs.DamagePayout`; `Net.EconomyAuthority` (a client cannot change money; a cheat client cannot complete a delivery outside the ring or above 1.3 m/s); `Save.AddContentKeepsSave` (add a vehicle to the content pack: the save still loads); `Save.TileBaseChanged` (a tile whose `H0` changed resets, the rest load); UI screenshot autotests of every HUD panel.

**You play:** everything the browser game does, in Unity, alone or with friends: freight boards, deliveries, money, fuel, wash, tow, on foot, all five original vehicles under their new names, saved between sessions, and saves that keep working when new vehicles arrive.

### 6.2 M10+: widen (a playable drop every 2-3 weeks)

| Drop | Weeks | Content | Key acceptance |
|---|---|---|---|
| M10 | 22-24 | Semi tractor + 53 ft trailer (kingpin `ConfigurableJoint`, TGS 20/8 iterations, air brakes, 18-speed or AMT, inter-axle diff, 06 §6.1); diesel stand-in with turbo and engine-brake layers; Steam lobbies, invites, SDR, auth tickets, achievements and Cloud, **all through the one Facepunch.Steamworks copy** [T6]; interest management (U11b, U3d) | Autopilot reverses a loaded trailer onto the depot dock within 0.3 m; jackknife at 40 km/h recovers; semi at 1,500 rpm in top gear = **111 ± 5 km/h** (06 §7.3 computes 30.7 m/s) [T13]; trailer ownership moves with < 1 mm error; `Net.SingleSteamApi`; 16-client host upload within the §5.3 overhead model × 1.05 and ≤ 240 KB/s; you and a friend play 15 min over Steam on different networks, on your own AppID |
| M11 | 25-26 | Rally coupe (gravel tyres, sequential box, front torque-sensing diff, rear LSD; 06 §7.3); point-to-point time trials on the dirt roads; liveries (07 phase G) | Gravel stage time repeatable ±2 % over 5 autopilot runs; 3 liveries readable on both sides; timing gates agree across clients |
| M12 | 27-29 | 6x6 with CTIS and lockable centre and cross-axle diffs (06 §5.3); **Linux** dedicated server build (needs U4b) soaked in WSL2 [C5] | The 6x6 crosses the bog at 100 kPa and gets stuck at 350 kPa; 16 clients on the Linux server in WSL2 on this PC; 24 h server run with autosaves and < 200 MB growth; `Build.ServerContents` on the Linux build; the tool-app fee question answered by Steamworks support |
| M13 | 30-32 | GT3-inspired car: aero maps, slicks, tyre temperature, setup sheet, slipstream (06 §6.3); PROXY coupling; race events with host-validated timing | Downforce at 200 km/h within ±10 % of 580 kg; PROXY cruise rpm within 10 rpm of a **monolithic co-simulation reference re-run for the GT drivetrain** (01 §3.5's method with the F136-class engine and 06 §7.3's GT ratios; 01's own reference is an LS in a 1,614 kg car) [T13]; PROXY's per-step fork-join measured on the min-spec proxy; 8-bot race finishing order identical on every client |
| M14 | 33-34 | Trophy truck (long travel, progressive bypass dampers); performance and polish pass; Steam Playtest build; IL2CPP decision (U4c, optional) [S18]; Steam content survey answers drafted from `content-provenance.json` [C13] | Clean install runs on a friend's PC; your 12-camera look sign-off at 4 times of day; title clearance (U18) done before the Coming Soon page, which is up ≥ 14 days before release; release no earlier than the Steam fee date + 30 days [C10] |
| Track F (U12) | after M9, parallel | engine-sim fork patches: forced induction → compression-release brake → compression ignition (§3.9) | Boost trace responds to spool; engine-brake torque within ±15 % of the Unity-side model; your listening sign-off |
| Unity 6.7 | when 6.7 LTS has 2-3 patches | Upgrade at a milestone boundary behind the full gate suite; evaluate GTAO, SSR, Surface Cache GI | All gates green on 6.7 |

### 6.3 Effort summary

| Milestone | Planned weeks (latest likely) | Agent-days | of which feedback reserve | Lanes busy |
|---|---|---|---|---|
| M0 Bootstrap | 1 (2) | 11 | — | I, V, W, A |
| M1 Millbrook + Engine Bench | 2 (3) | 15 | — | W, V, I |
| M2 First drive | 3-4 (5) | 28 | 3 | V, W, A (+R starts roads) |
| M3 Proper intersections | 5-6 (8) | 41 | 5.5 | R, V, V′, W, A |
| M4 VS-1 | 7-9 (11) | 37 | 5 | V, N, R, A, W, I |
| M5 Pretty Millbrook + LAN preview | 10-11 (14) | 36 | 4 | W, R, N, A, V |
| M6 Living town | 12-14 (18) | 39 | 5 | R, V, A, W |
| M7 Offroad | 15-16 (21) | 24 | 3 | V, W, A, N (+G starts) |
| M8 Online alpha | 17-18 (24) | 25 | 3 | N, V, W, I |
| M9 The Backroads game | 19-21 (27) | 30 | 4 | G, A, V, I |
| **Total M0-M9** | **21 weeks (27 at the late end)** | **≈ 286** | **32.5** | 3-5 lanes + integrator |

Revision 1 said 230 AD in 20 weeks. Revision 2 adds about 56 AD: the feedback reserve (32.5); BRV v2 at a realistic size and the PhysX chassis moved forward (+10.5) [S3, S2]; crash capture, notices, privacy, determinism and operating-model infrastructure in M0 (+3); controls, menus and settings (+1.5); single player on the local host in M5 and the minimal save in M8 (+3); smaller items (+5). It removes the dozer rebuild and the M0 payout export. The "latest likely" column adds about 30 %, in line with the ±50 % estimates and P-online's advice to plan with a 30 % buffer. Integrator time after M1 (about 1 AD per week) is not in these numbers. Anchors: roads ≈ 31 days (05 §9); full parity 5-7 part-time months excluding engine-sim and online (03 §5); the three proposals' totals were 210 AD (P-slice, to its M8), 223 AD (P-risk, to its M8) and ~290 AD (P-online, to its M10). **The plan is re-cut after M2** using the measured velocity of M0-M2. Your time: about an hour per build plus the decisions as they fall due.

---

## 7. Risks and decisions

### 7.1 Top risks

| # | Risk | Retired by | Mitigation / fallback |
|---|---|---|---|
| 1 | **Engine coupling at real frame rates** (revision 2): Unity runs fixed steps back to back, so a wall-clock design misfires below 60 fps, exactly on the Low tier [T1] | M1 `esp_cosim_matrix` with L = 1-4 and replayed frame traces; M4 P4 matrix at 30/45/60/120 fps | Lag defined in steps (L = 2-4), bounded wait, every command queued, `dT_dw` feed-forward, coast only on real hitches with borrowed steps, audio ring sized from frame time; fallbacks: longer wait capping L at 3, then torque map + FOLLOW_RPM on that tier |
| 2 | **Unity audio path:** filter-generated audio may not spatialise; `IAudioGenerator` details unverified (01 §4.5, §9.3); audio may not run in batchmode | S-AUD-1 in M1; `Audio.BatchmodeDspRuns` in M0 | Local engine needs no 3D; DC-clip × filter; `PCMReaderCallback` streaming; Native Audio Plugin SDK last; audio assertions move to player-only runs if batchmode audio is dead |
| 3 | **SLAVED_CRANK feel:** 270-350 rpm limiter deviation from the lag (01 §3.5); never yet run inside a real BRV (06 §9.1) | M4 P4 matrix | 120 Hz, spark-cut + fuel-cut backstop, `dT_dw` feed-forward, PROXY per vehicle, "classic engine" switch keeps builds shippable |
| 4 | **CPU on players' PCs:** 0.25-0.6 core here for the local engine, about double on a half-speed PC (01 §6.2, 02 §9); this laptop is about twice as fast per core as the target | min-spec proxy from M1, M4 kill check on the proxy, U14 | Tiers that never touch frequency, AVX2 FIR, R1 caps, loop banks, torque-map fallback, `maximumDeltaTime = 4/120`, per-frame budgets; a real 6-core laptop test before M8 if you can borrow one |
| 5 | **Canon drift:** handedness slips, pane renumbering, RNG-consuming hooks (03 §2, §3.3, §3.4); plus, found in review, CRLF conversion on checkout, a live game sampled mid-export, typed props not captured, float32 parity limits [T3, T4, T5, T14] | M0/M1 canon gates, every CI run | Export not regenerate; snapshot with `autocrlf=false`; `-text` on hashed trees; determinism module; frozen loop and synchronous copies; wrapper hooks proved by hashes and `rngCalls`; double-precision reference queries; bit-exact LOD0; plate-mask identity; `BASE` and `H0` plated together |
| 6 | **Intersection geometry:** skewed or short arms, the 13.8 m harbor junction, tight downtown corners (05 §1.2-1.3, §9) | M3 validation | Radius back-off, explicit overrides, synthetic cases, legacy fallback per node, both roundabout variants for you to pick |
| 7 | **BRV v2 convergence or feel regression** (inertia ratios up to ~240:1, 06 §9 item 4); BRV v2 is the critical path to M4 | M3 test track, M4 | 13 AD over weeks 4-6 instead of 6, lumping, up to 20 iterations, dense LDLᵀ shaft solve, golden traces, "classic handling" preset; kill criterion triggers a written re-plan with you |
| 8 | **Agent operations on one laptop:** one Unity project opened twice, contested GPU and audio, perf gates disturbed by other work, Unity YAML merge conflicts, sessions that die, your playtests stuttering [S1, S9, S10, S11] | §1.10-1.11 from M0 | Worktrees with own `Library`, Unity slot, GPU, machine and `.user-playing` locks, `OWNERS.json` + UnityYAMLMerge, a ≤ 20 min merge tier with nightly soaks, `HANDOFF.md` on every card, one approvals list, invalid-not-failed perf runs on battery |
| 9 | **Approvals or installs delayed** (downloads, Unity Hub actions, your answers) | §7.2 defaults | Bundled package pins, C++ engine builders, Mono builds, `ServerHeadless`, compile-only server check; every decision has a due date and a default that never spends money or signs you up |
| 10 | **Netcode:** coupled bodies jitter, no host migration, cheating hosts, friends who cannot connect (04 §3.2, §4) | net probe M4-M5, M8 | Relay join codes by default, one owner while coupled, autosave + host again, host validation, Steam auth tickets, dedicated servers later |
| 11 | **Deformation cost or divergence** | M2, M4, M7 | Integer µm kernel relative to canon H, tile hashes and resend, budgeted partial rebuilds |
| 12 | **Diesel and turbo authenticity** (06 §9 item 3) | accepted for v1 | Honest stand-ins, labelled Unity-side layers, optional fork track F |
| 13 | **Native crash or DLL lock** (01 §8 already found memory-corruption defects upstream) | M0 crash capture, M1 ASan soak | `CrashCollector` + SEH minidumps on plugin threads, exception firewall in every ABI call, version check with torque-map fallback, copy script that refuses a locked DLL |
| 14 | **Licences and IP:** Synty's generative-AI clause; vehicle trade dress; real brand names inside engine files and ids; notices; OpenStreetMap attribution; the game title [C1-C4, C8, C9, C12] | from M0-M1, each build | §4.7's seven-step Synty procedure; blended proportions, named deviations, a "would a player name it?" check and a takedown procedure; `mr_sanitise` + `Content.NoRealMarks`; notices and Credits from M1; OSM credit; title clearance (U18) |
| 15 | **Privacy** once players connect (SteamIDs, IPs, profiles) [C7] | §5.9 before any public build | Data inventory, hashed SteamIDs in logs, 14-day log retention, delete-on-request, no analytics or chat, egress test |
| 16 | **Saves breaking across drops** [S6] | M8-M9, every gate after `m9` | Save schema separate from the content handshake, per-tile base hashes, refunds, fixture saves per tag |
| 17 | **Unity version churn** (6.3 → 6.7 → Unity 7 CoreCLR, 04 §1.1) | continuous | 04 §1.2 rules from day one; one patch update at the `m1` tag; upgrades only at milestone boundaries behind all gates |
| 18 | **Batchmode needs an activated Unity licence; headless WebGL may fail; three.js comes from a CDN** | M0 day 1-3 | You sign in to Unity Hub once (agents never do); export fallbacks in §1.8.3; three.js hashed in the page |
| 19 | **Data loss before a remote exists** [S19] | M0 | Nightly `git bundle` + LFS copy to your backup folder (U21); create the private GitHub remote early (U1) |
| 20 | **Scope creep and estimate error** | re-plan after M2 | Timebox rule, feature flags, `main` always playable, 15 % feedback reserve, headline/lane gate split, date ranges instead of promises |

The revision-1 risk "the 01 probe sits in a temporary scratchpad" is now decision U20, answered before day 1; if the folder is gone, the PD and wheel-load generators, `piranha_path_std.cpp` and the LS builder are rebuilt from 01 App. A (about 1 AD).

### 7.2 Decision register (for the agents; the plain-words version is the last section)

| # | Decision | Recommended | Needed by | If unanswered |
|---|---|---|---|---|
| U1 | New repo `D:\Backroads-Unity` (you create the private GitHub `kaiwren986/Backroads-Unity` when you want a remote), or a `unity\` folder inside `D:\Backroads` | New repo; create the private remote at M0 for an off-machine copy | Day 1 | New repo, no remote; nightly local backups |
| U2 | Allow the canon export: a temporary snapshot of `D:\Backroads` at 552f780 with the read-only hooks (incl. the typed-prop wrappers and the determinism module) and the exporter, loaded in headless Edge (your `D:\Backroads` stays untouched) | Yes | Day 1 | Blocks M0's canon work; nothing else can stand in |
| U3 | Downloads, each approved once: **a)** Unity Package Manager fetching the pinned packages (incl. glTFast 6.20.0); **b)** Clipper2 C# source (M3); **c)** the portable win_flex_bison 2.5.25 zip into `tools\winflexbison\` (unlocks all 25 engine scripts); **d)** the Facepunch.Steamworks 2.5.2 release and the community transport's source (M10); **e)** piranha's LICENSE file from commit 004fa15 (one text file, M1); **f)** each CC0 or CC-BY sound sample, listed with URL and licence | Yes to all | a: day 1; e: week 2; c: week 1-2; b: week 3; f: as needed; d: M10 | No download: bundled pins, C++ builders, synthesised sounds; the schedule holds except Steam |
| U4 | Unity Hub actions (yours): **a)** update 6000.3 to the newest 6.3 patch at the `m1` tag (end of week 2), once; **b)** add "Linux Dedicated Server Build Support" (Windows server optional); **c)** add IL2CPP only if M14 decides to use it; keep signed in to Unity Hub so batchmode works; never build on 6000.5 | Yes | a: end of week 2; b: before M12; c: M14 | a: stay on 6000.3.20f1 until the next boundary; b: no Linux server (M12 server part waits) |
| U5 | Feel sign-offs: **a)** does M2 drive like the browser game; **b)** does the M4 engine-sim driveline feel right | Play and tell us (F10) | end of M2, end of M4 | The build ships with the measured deltas; the question stays open |
| U6 | Truck engine: authentic big-block gas V8 (changes the JS truck's diesel voice) or a diesel-character I6 stand-in (days of tuning, still petrol-sounding) | Gas V8 | M2 | Gas V8 |
| U7 | After VS-1: traffic (M6) before offroad (M7), or the other way round | Traffic first | M4 | Traffic first |
| U8 | Roads (05 §9): 4-leg roundabout with the harbor road realigned, or 3-leg; where the town delivery point goes; one signal at node 2 or none; keep the −9 % approaches; keep canon street widths | 4-leg; kerbside loading bay on Main Street; no signal at first; keep; keep | end of week 12 (after driving both variants) | The recommended answers |
| U9 | Fictional brand and model names (rename "Ranger 4x4" and "Muncher D3"); 2-4 real inspirations per new vehicle; you collect reference photos (agents never bulk-download) | Agents propose names, you pick | M2 (renames), M6 (first new body) | Neutral placeholders ("Pickup A", "Dozer A") |
| U10 | Synty: which packs (one-time licences) and the agents' rules (§4.7) | **Decided 2026-10-18:** City, Explorer Kit and Pro Racer; training off; agents import, audit, set up materials and arrange prefabs, never make or edit models from them, upload them or commit them | done | n/a |
| U11 | Online services: **a)** a Unity Gaming Services project for Relay (no card needed), with a usage alert; **b)** a Steamworks partner account, $100 app fee, tax and bank forms, AppID; a friend for the Steam test | a: yes; b: start at M8 | a: week 17 (M8); b: start week 17 for an AppID by M10 | a: the alpha is LAN-only; b: no Steam, M10's Steam part waits |
| U12 | Fund the engine-sim fork track (turbo, engine brake, diesel) | Decide after M9 | after M9 | Not started |
| U13 | Race venues: Millbrook's own roads first, or a new track as a CanonData v2 region | Millbrook roads first | M11 | Millbrook roads |
| U14 | Minimum-spec PC (sets the Low tier); can you borrow a 6-core laptop for one test | GTX 1070 / 6-core class; Deck under Proton as a bonus; the min-spec proxy runs regardless | M5 | GTX 1070 / 6-core class |
| U15 | Accept canon v1.1: road plates may change ground only inside road footprints + 15 m, everything else bit-identical; ruts made during play are identical across players but only statistically equal to JS ruts | Accept | M2 | Accept (versioned, reversible) |
| U16 | Progression per server (Motor Town style) or global (Unity Authentication + Cloud Save free tier) | Per server | M9 | Per server |
| U17 | Workshop modding, with conditions: no Synty asset reachable from the mod SDK or loading path (tested); the Steam Workshop legal agreement link and a takedown contact; a licence field per mod; short terms of use before Workshop opens; keeps GLB + `.mr` runtime loading alive | Yes, later, with the conditions | M10 | Not opened |
| U18 | Name clearance and names: a trademark search for the title (USPTO, EUIPO/TMview, classes 9, 28 and 41; Steam and itch.io) before any public page; a neutral publisher (company) name and the product name, fixed before the first saves | Keep "Backroads" as the working title; clear it before a Coming Soon page; pick your publisher name before M8 | names: week 17 (M8); clearance: before any public page (M12-M14) | Placeholders `BRDev` / `Backroads` become permanent internally; no public page |
| U19 | Operating model (§1.11): laptop on, plugged in and awake while a milestone runs; accept the agents' command allowlist once; start one coordinator per milestone | Yes | Day 1 | Agents work only while you are present (calendar ×2-3) |
| U20 | Copy the 01 probe folder (`esprobe\`, in a temporary session scratchpad) to `D:\Backroads-ref\probe01\` now, before it can be cleaned up | Yes, now | before day 1 | Rebuilt from 01 App. A in M0-M1 (≈ 1 AD) |
| U21 | Backup folder for nightly bundles: a second drive or USB stick | A second physical drive | M0 | `D:\Backroads-Backups` (protects against mistakes, not against disk failure) |
| U22 | Paid Git LFS, if the repo would exceed 6 GiB before the first push | Not needed yet; prune | before the first push | No paid LFS |
| — | When agents may push to GitHub; how often you want builds | You say | any time | Never push; a build per milestone |

---

## First two weeks

**Before day 1 (you, about 15 minutes):** say yes or no to U1, U2, U3a, U19 and U20 (and U3c and U3e if you can); open Unity Hub once and check you are signed in; set the laptop not to sleep while plugged in. Nothing else is needed from you until the M0 build, apart from accepting the agents' allowlist when the coordinator shows it on day 1.

### Day plan

| Days | Integrator (I) | Vehicles + native (V) | World (W) | Art (A) |
|---|---|---|---|---|
| 1 | T0.1 repo, `AGENTS.md`, attributes, allowlist proposal; T0.2 env check | T0.3 preserve probes, T0.4 vendor | T0.7a snapshot script, determinism module, typed wrappers | — |
| 2 | T0.5 Unity project + `ProjectSetup` | T0.4 CMake skeleton, crash guard | T0.7b exporter | T0.8 canary |
| 3 | T0.6 asmdefs, Architecture/Repo tests, server compile check, `ServerHeadless` | T0.4 ctest; start T1.6 | T0.7c receiver + headless Edge | — |
| 4 | T0.6 CI tiers, locks, `BuildCli`, `AutotestRunner`, smoke, crash capture, F10 | T1.6 `UnitySim`, worker, step-indexed rings | T0.7d verify, export twice, commit CanonData | — |
| 5 | T0.9 M0 gate + build | T1.6 ring, renderSync, FIR | T0.7e traces and deform references | — |
| 6-7 | merges; M1 autotest scenarios; min-spec proxy harness | T1.6 builtins, cosim matrix, ctest | T1.1 importer, T1.2 WorldGrid (incl. reference path) | — |
| 8-10 | `PLAYTEST.md`, F10 items, Credits/notices | T1.7 C# side + Engine Bench; T1.8 S-AUD-1 | T1.3 terrain chunks, T1.4 static world | A1 head start (FBX of `truck.glb` via Blender) |
| 11-12 | M1 gate dry run (merge and nightly tiers) | ASan soak; piranha if U3c | T1.5 shots, perf | — |
| 13-14 | **T1.9 M1 gate, build, report**; T1.10 M2 task cards | fixes | fixes | — |

### M0 task cards (week 1)

Every card ends with a committed `HANDOFF.md` (done, next, the resume command) [S1].

**T0.1 Create the repository** (I, 1 AD). `New-Item -ItemType Directory D:\Backroads-Unity`; `git -C D:\Backroads-Unity init -b main`; `git -C D:\Backroads-Unity lfs install --local`. Write `.gitattributes` (§1.2: LFS patterns, `* text=auto eol=lf`, `*.bat *.cmd text eol=crlf`, `-text` on every hashed tree, `merge=unityyamlmerge` on `*.unity *.prefab *.asset`) and register the merge driver in the repo's local config (`merge.unityyamlmerge.driver` = the 6000.3.20f1 editor's `Data\Tools\UnityYAMLMerge.exe merge -p %O %B %A %A`). Write `.gitignore` (§1.2), `README.md` (how to play a build, where reports are), `AGENTS.md` (hard rules, lock protocol, frozen contracts, canonical commands, where task cards live, how to resume) and a one-line `CLAUDE.md` pointing to it, `OWNERS.json`, `third-party.json` (seeded with engine-sim, scs, piranha, flex, bison, Clipper2, glTFast, the Unity packages, Barlow Condensed and Inter, OpenStreetMap), `docs\approvals\QUEUE.md`, and the proposed agent allowlist and Synty deny rules as `docs\approvals\claude-settings.proposed.json` (they take effect only when you accept them). `oracle.lock.json` = `{"repo":"D:\\Backroads","remote":"kaiwren986/Backroads","commit":"552f7807f4ad56547815c32a5e8407a7c324d556","seeds":{"world":20251015,"planner":20279734,"props":4242},"three":"0.170.0"}`, `synty-manifest.json` = `{"packs":[],"trainingOffConfirmed":null,"syntyWrittenReply":null}`. Copy (not move) the docs: `robocopy D:\Backroads\docs\unity D:\Backroads-Unity\docs /E`. First commit. *Done when* `git log` shows one commit, `git lfs track` lists the patterns and `git check-attr text CanonData/v1/manifest.json` prints `unset`.

**T0.2 Environment check** (I, 0.25 AD). `tools\ci\env.ps1` sets `$UNITY`, `$BLENDER`, `$NODE`, `$EDGE` (`C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`), `$PROJ`. `tools\ci\env-check.ps1` records Unity 6000.3.x and its `PlaybackEngines\WindowsStandaloneSupport\Variations` (today: Mono only), Blender 4.5, Node ≥ 20 (today 24.18.0), CMake ≥ 3.21 (today 4.3.4), git-lfs (3.7.1), VS 2022 C++ via `vswhere -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64`, Edge, the WSL2 distributions, the power source, and which logical processors are E-cores (`GetLogicalProcessorInformationEx`, efficiency class). After T0.5 it also scans `artifacts\create.log` for a licence error and, if found, stops with "please open Unity Hub and sign in" (agents never sign in). *Done when* `artifacts\env.json` exists and the script exits 0.

**T0.3 Preserve probe evidence** (V, 0.25 AD). Copy `D:\Backroads-ref\probe\{CMakeLists.txt, build.ps1, src\*, patched\*}` → `native\EngineSimPlugin\probes\p02\` (keep the WAVs out of git). Copy `CMakeLists.txt`, `bench.cpp`, `piranha_path_std.cpp` and `shim\` from `D:\Backroads-ref\probe01\` (made under U20) → `probes\p01\`; if U20 was not done and the folder is gone, note it in `docs\milestones\M0.md` and plan to rebuild from 01 App. A. *Done when* both folders are committed (or the gap is recorded).

**T0.4 Vendor engine-sim and the CMake skeleton** (V, 1.75 AD). From `D:\Backroads-ref\engine-sim` (read only), export the committed blobs with `git -c core.autocrlf=false -C D:\Backroads-ref\engine-sim archive 85f7c3b src include scripting es assets/engines LICENSE` and, for each submodule, `git -c core.autocrlf=false -C <submodule> archive <commit>` (simple-2d-constraint-solver @ e009f4f, piranha @ 432f0b1) into `native\EngineSimPlugin\vendor\engine-sim\…` [T5]. Record `rev-parse HEAD` and `submodule status` in `vendor\VENDOR.md`, plus the piranha licence note (§1.6). Write `tools\ci\vendor-check.ps1` (blob ids from `ls-tree -r` vs `git hash-object --no-filters`). Add the two empty shim headers and `crash_guard.cpp`. Write `CMakeLists.txt` and `CMakePresets.json` (configure presets `vs2022-x64` → `native\build`, `vs2022-x64-asan` → `native\build-asan`; build preset `release`; test preset `release`) with targets `scs`, `enginesim_core`, `EngineSimPlugin` (exports `esp_version` = 1 and `esp_build_info`), test `esp_smoke` (LoadLibrary + version). Write `tools\ci\build-native.ps1` (§1.9, with the source-hash cache). *Done when* `vendor-check.ps1` reports 0 differences, `build-native.ps1` is green, and the DLL + `EngineSimPlugin.version.json` sit in `Game\Assets\Plugins\x86_64\`.

**T0.5 Unity project** (I, 1 AD). `& $UNITY -batchmode -quit -createProject D:\Backroads-Unity\Game -cloneFromTemplate "C:\Program Files\Unity\Hub\Editor\6000.3.20f1\Editor\Data\Resources\PackageManager\ProjectTemplates\com.unity.template.3d-cross-platform-17.0.14.tgz" -logFile artifacts\create.log` [if `-cloneFromTemplate` is rejected, create an empty project and let `ProjectSetup.Apply` create the URP assets]. Pin `Packages\manifest.json` to the M0 column of §1.4 (incl. `com.unity.inputsystem` 1.19.0; glTFast 6.20.0 once U3a is a yes). Write `Backroads.Build.Editor.ProjectSetup.Apply` for every §1.3 setting (incl. `maximumDeltaTime = 4/120`, `submitAnalytics = false`, Cloud Diagnostics off, `GameInfo` placeholders `BRDev` / `Backroads`), layer and collision-matrix entry, and run it with `-executeMethod`. EditMode `Build.ProjectSettings` asserts each one. *Done when* the test passes in batchmode.

**T0.6 Assemblies, harness and CI** (I, 2.75 AD). Create every asmdef of §1.5 as a stub folder with references and define constraints (`!UNITY_SERVER` on client assemblies, `BACKROADS_TESTING` on Testing, `allowUnsafeCode` on EngineSim and Vehicles). `BrCoords` (03 §2) + `Core.CoordsCanary`. `Architecture` tests for rules 1-6, `BuildCli.CompileServerScripts` for rule 7, `Build.ServerContents` for rule 8; `Repo.NoCrInHashedFiles`, `Repo.OwnersRespected`, `Licence.NoticesComplete`. `BuildCli.Build` (profiles `Dev`, `Client`, `ServerHeadless`; writes `builds\M<n>\` with `Play.bat`, `PLAYTEST.md`, notices). `AutotestRunner` with scenario `smoke` (boot, 3 s, screenshot, exit 0), `SmokeRunner` (`-br-smoke`, every build), `MetricsRecorder`, `SessionMetrics`, `CrashCollector` (+ `-br-crash-test`), `ContactSheet`, `PlaytestChecklist` (F10, reads `StreamingAssets\Playtest\M<n>.json`, saves answers), the version stamp; autotests `m0.crash.recover` and `m0.net.egress`; `Audio.BatchmodeDspRuns`. Scripts: `unity-tests.ps1`, `build-player.ps1`, `autotest.ps1` (GPU lock, `machine.lock`, `-MinSpec`, `-Fps`), `soak.ps1` (127.0.0.1), `gate.ps1` (three tiers, wall-clock budget in the gate JSON), `worktree-new.ps1`, `backup.ps1`, `lfs-budget.ps1`, `notices.ps1`; `Play.bat` creates and removes `artifacts\.user-playing`. *Done when* `gate.ps1 -Tier merge -SkipOracle` is green in ≤ 20 min.

**T0.7 Canon export** (W, 3 AD).
- **a)** `tools\oracle\make-snapshot.mjs`: `git -c core.autocrlf=false -C D:\Backroads archive --format=tar 552f7807` piped into `tar -x -C artifacts\oracle\552f780` (or Node's tar handling); then the exact-match hooks of §1.8.2, each failing unless its anchor occurs once: the three one-liners, and the typed wrappers on `function placeModel(name, x, z, rot, w, d, colors, rng, litChance, lits = null) {`, `function lamp(x, z, faceRot, h = 6.5) {`, `function silo(x, z, r = 3, h = 12) {`, `function dock(q) {`, `function fuelStation(p, rng) {`, `function crusher(x, z, rot) {` and `function sawShed(x, z, rot) {`; inject `determinism.js` as the first module and the exporter's module tag into the snapshot's `index.html`.
- **b)** `tools\oracle\exportUnity.js`: waits for `window.__game`; freezes the loop and clock; checks `sha256(W.WEAR)` and copies every exported array with `slice()` in the same task; POSTs every §1.8.4 file from the copies (binary layers as `ArrayBuffer`, JSON at full precision, `static_props.glb` via three's `GLTFExporter`, shots via `canvas.toBlob` right after `renderer.render` with the clock frozen); hashes the three.js module in the page; records the WebGL renderer; runs the deform references last; then `POST /__export/done`.
- **c)** `tools\oracle\serve-export.mjs` (zero-dependency `node:http` bound to 127.0.0.1: static files from the snapshot; `POST /__export/<relpath>` → `--out` with path sanitising; `done` writes `manifest.json` with SHA-256 per file, the PRNG seed and the renderer, and exits 0) and `tools\oracle\run-export.ps1` (server on port 8131, headless Edge per §1.8.3, waits up to 10 min, kills Edge, verifies). `Export-Canon.bat` is the manual fallback.
- **d)** `tools\oracle\gen-hash.mjs` (Node-only six-layer check). Verify the M0 gate hashes and counts, export twice and compare manifests, commit `CanonData\v1\`.
- **e)** `goldenTraces.js` with input files `tools\oracle\traces\{truck_0_80_route9, pickup_0_100_route9, pickup_brake_80, truck_quarry_low, hatch_bog_10s, winch_15m_tree}.json` (seeded, no AI traffic); `reference\deform\` (10,000 seeded calls, run last). The payout and fish-timer references wait for M9 (`CanonData\refs\jobs\`).
*Done when* the M0 oracle gate is green and `CanonData\v1` is committed with LFS.

**T0.8 Axis canary** (A, 0.5 AD). `art\pipeline\br_canary.py` exports `CANARY.fbx` with `MARK_Front` at game (0, 0, 2), `MARK_Left` at (1, 0, 0), `MARK_Up` at (0, 1, 0) using the 07 §3.2 FBX flags; a minimal `BackroadsModelPostprocessor` (`bakeAxisConversion = false`, scale 1); EditMode `Art.Canary`. *Done when* the test passes.

**T0.9 M0 gate and build** (I, 0.5 AD). `gate.ps1 -Tier milestone -Gate M0`; `builds\M0\` with the test yard (`TestYard.unity`: grey plane, canary markers on a turntable); `docs\milestones\M0.md` with the gate table, LFS usage, merge-tier time and the approvals queue. Commit the DLL; tag `m0`; prune worktrees.

### M1 task cards (week 2)

**T1.1 Canon importer** (W, 1.5 AD). `Backroads.Canon.Editor.CanonImporter` (menu and `ImportCli`): verify every manifest SHA-256 (abort on mismatch), copy layers to `Assets\Backroads\World\Canon\v1\*.bytes` with path-derived `.meta` GUIDs (gitignored, cached by manifest hash), build `WorldInfo`, `PlacementSet` (incl. typed records), `VegetationSet`, `SurfaceTable`, `LegacyTrafficNetwork` ScriptableObjects. Tests `Canon.ManifestHashes`, `Canon.Counts`.

**T1.2 WorldGrid** (W, 2 AD). `CanonRuntime` maps `TextAsset.GetData<float>()`/`<byte>()` into NativeArrays; `WorldGrid` (§2.6 layers, `DeltaUm` zeroed); Burst `WorldGridQueries` (float, for physics) and `WorldGridQueriesRef` (double precision, mirroring JS's `heightAt` and its 0.5 m / 7-bisection ray march, `js/world.js:68-118`) in JS coordinates, flipped at the API (03 §2, §4.1) [T14]; `ColliderGrid` (16 m cells, 03 §4.7). Tests `World.HeightProbes`, `World.RayHits`, `World.PlacementsRoundTrip`. The server canary loads canon and logs `CANON v1 OK H=a8908d7f…`.

**T1.3 Terrain chunks v0** (W, 2 AD). `TerrainChunkSystem` + `TerrainChunkBuildJob` (256 chunks, LOD 1/2/4/8 at 110/250/470 m, ±10 m hysteresis, skirts 1.2 + 0.7·step, nearest-first builds; 03 §4.3), per-vertex colours from the PAL rules written linear, winding and normal-x flipped, `SG_Terrain_V0` (vertex colour + detail noise at `float2(-x, z)`). Test `Render.TerrainLod0BitExact`; perf: all chunks ≤ 1.5 s at load.

**T1.4 Static world and fly camera** (W, 2 AD). `BuildingSpawner` (frozen `buildings.glb` via glTFast; one material instance per recolourable slot; transforms from `placements.json`), `Render.PaneNamesResolve`; `VegetationRenderer` (LODGroup prefabs from `trees.glb`, y = ground − 0.2, instance tint 03 §4.8); props from `reference\static_props.glb` split into 64 m chunks, `Render.StaticPropsBuckets`; JS-style road ribbons with the 03 §4.5 marking shader; water plane at y = 0; sun + gradient ambient; `FreeFlyCamera` with `CameraBookmarks` (the 12 exported cameras on keys 1-0, −, =) and POI jumps. [Fallback if U3a is still open: Blender converts the three GLBs to FBX headless, names preserved, mapped by name.]

**T1.5 Shots and perf** (W, 0.5 AD). Autotests `m1.shots.canon` (24 shots at 1600×900 to match the JS shots, contact sheet) and `m1.perf.flythrough` (scripted path, 2560×1440 High; and on the min-spec proxy).

**T1.6 ESP plugin v1, trimmed** (V, 3.5 AD) [S14]. `UnitySim`, `UnityTransmission`, `SlavedCrankPD`/`WheelLoad` (compiled, exercised in M4), `Instance` with worker thread, step-indexed SPSC command/result rings, fractional accumulator over physics time, coast mode with borrowed steps (§3.4); modes FREE and FOLLOW_RPM; `esp_dyno_sweep`; `renderSync` + float ring with the frame-sized target + drift trim + underrun decay; AVX2/SSE2 FIR; own WAV reader + 44.1 → 48 kHz resampler; synthesizer re-init; builtins `f4_boxer_a` (from `probes\p02\src\ej25.cpp`), `v8_cross_a` and `i4_synth` (from `probes\p01\bench.cpp`, else rebuilt from 01 App. A.1); `esp_get_stats`, `esp_debug_tap`, `esp_poll_log`; `esp_cli` (incl. `bench --affinity P|E`); the ctest suite of the M1 gate incl. `esp_step_accounting` at timeScale 0.5 and `esp_cosim_matrix` with L = 1-4 and replayed frame traces; ASan soak. If U3c is a yes: run win_flex_bison once, commit `generated\piranha\`, build piranha + es_script with `piranha_path_std.cpp`, `script_loader.cpp`, `mr_sanitise`, `esp_load_script`, and the `esp_all_engines` report. (`esp_calibrate_inertia`, `esp_render_offline`, `esp_set_quality`, `esp_set_worker_options` are M4 work.)

**T1.7 C# side and Engine Bench** (V, 1.5 AD). `Backroads.EngineSim.Native` (01 §7.4 structs and P/Invoke, `Pack = 1` size asserts), `EngineSimEngine`, `EngineSimAudio` (`OnAudioFilterRead` → `esp_read_audio`), `AudioTap`, `EngineDef` (with `pitchOrders`) + `EngineCatalog` (roster engines in Client builds; all 25 with piranha in Dev builds only), `EngineBench.unity` with a UI Toolkit panel: engine list, ignition (tap I), starter (hold I), throttle (W / right trigger), "Dyno sweep" plot, live rpm, torque, MAP, AFR, worker ms, real-time factor, ring fill and target, underruns, coast events. Credits panel from `notices.ps1` output. Autotest `m1.bench.revcycle` at 60 and 30 fps.

**T1.8 Spike S-AUD-1** (V, 0.5 AD). In 6000.3.20f1: `OnAudioFilterRead` with `spatialBlend` 0 and 1 (RMS at 1 m and 20 m, pan left/right via `AudioTap` on a listener-side capture), `IAudioGenerator` with a Burst `[DllImport]` into `esp_read_audio`, the DC-clip trick, `PCMReaderCallback`; underruns at Best latency. Write `docs\spikes\S-AUD-1.md` with the chosen remote path.

**T1.9 M1 gate, build, report** (I, 1 AD). `gate.ps1 -Tier milestone -Gate M1` (after a green nightly); `builds\M1\` (`Play.bat` opens Millbrook; a menu entry opens the Engine Bench); `PLAYTEST.md` in plain words; F10 items ("Same town as the browser game? Which view looks most different? Does the engine sound real? Any crackles?"); `docs\milestones\M1.md` with the contact sheet, dyno plot, measured CPU (P, E and proxy), the cosim-matrix report, the audio spike result and the decisions due next (U4a, U5a, U6, U9 renames, U15); freeze CanonData v1 and ESP ABI v1 (ADR-001, ADR-002, the latter including the step-lag semantics); commit the DLL; tag `m1`. Then, **before any M2 worktree is cut**, you do the U4a editor update if you have approved it and the agents apply the U3a package bumps, so every `Library` re-imports once.

**T1.10 M2 task cards** (I, 0.5 AD) [S22]. Write `docs\cards\M2\*.md` from §6.1 M2, one card per lane task, each with inputs, steps, "Done when" and the gate it feeds; queue U5a, U6, U9 and U15 in `docs\approvals\QUEUE.md` with their due dates and defaults. Then the M2 lanes start: V on BRV V0, W on deformation, camera, input and the menus, R on the road graph import (R0), A on A1. The same card-writing task closes every later milestone.

---

## Appendix R. Review log

Three adversarial reviews (technical, sequencing, cost-legal) raised 60 issues. Each was checked against the research reports and, where a reviewer disagreed with a report, against the code or the machine. Result: 58 accepted, 2 partly accepted (C4, C15), none rejected outright. Ids: T = technical, S = sequencing, C = cost-legal.

**Evidence checked by the editor:** the rut kernel at `js/world.js:137-177` (sub-mm `dh` per call; targets relative to `BASE`); `js/props.js` (`rec()` call sites at 407, 465, 769, 783, 807-815; `placeModel` 400-409 records no `y`/`sx`/`sz`; `silo` and `lamp` 474-495 never call `rec`); `Math.random` and `performance.now` in `traffic.js`, `jobs.js`, `sky.js`, `props.js:1027`, `main.js`, `camera.js`, `particles.js`, `audio.js`; `git config --show-origin core.autocrlf` (system, true) and `git ls-files --eol js/props.js` (`i/lf w/crlf`); 27 `.mr` files incl. two `radial.mr` helpers; `SinglePlayerTransport.cs` inside the bundled NGO 2.13.0 tarball; WSL2 `Ubuntu-24.04`; only `windowsstandalonesupport` in PlaybackEngines; 01 §3.5-3.6, §6.2, §7.3 (`acc += dt * f * simSpeed`); 02 §6.3-6.4; 04 §3.3, §3.5-3.7; 05 §5.10; 06 §4.5, §5.6, §7.3 (semi 30.7 m/s); 07 §9-10. Online-only claims (the Facepunch transport's bundled 2.3.2 DLLs, Platform Toolkit's dispatch-loop warning, the piranha `004fa15` compare, Steam lead times, UGS billing wording, trademark records) were not re-fetched; each fix that rests on them is cheap and safe even if a detail differs.

| Id | Lens · severity | Issue (short) | Decision | Where the plan changed |
|---|---|---|---|---|
| T1 | tech · blocker | Coupling assumed FixedUpdates 8.3 ms apart; Unity runs them back to back, so collect/coast/stale/ring logic misfires below 60 fps | **Accept.** Confirmed by Unity's player loop and 01 §6.2 / 02 §6.3 worker costs | §3.4 rewritten (lag L = 2-4 steps, bounded wait, queued commands, feed-forward, borrowed-step coast), §3.3 step-indexed ABI, §3.6 ring target, §2.4, M1 cosim matrix, M4 P4 at 30/45/60/120 fps, risk 1 |
| T2 | tech · major | int16 mm deltas cannot hold sub-mm rut steps; ≤ 2 mm parity unreachable | **Accept.** `world.js:137-177` confirms per-call sub-mm `dh` and berm shares | §2.6 `DeltaUm` int32 µm, int64 kernel with a fixed rounding rule; rulings table; M2 error-curve and convergence tests; tile resend 4 KB |
| T3 | tech · major | Hooks do not capture typed props; `rec('model')` lacks y/sx/sz; `lamp()` lacks faceRot | **Accept** (both fixes combined). Confirmed in `props.js` | §1.8.2 wrapper hooks for `placeModel`, `lamp`, `silo`, `dock`, `fuelStation`, `crusher`, `sawShed`; inline/RNG-scattered pieces gated by `static_props.glb` bucket hashes; M0/M1 gates |
| T4 | tech · major | Oracle uses unseeded `Math.random` and wall clock; live loop runs during export; three.js hash unseen by Node | **Accept.** grep confirmed | §1.8.2 `determinism.js`; §1.8.4 frozen loop, synchronous `slice()` copies, deform refs last, payouts to M9 with logged uniforms; in-page three.js hash; traces without traffic |
| T5 | tech · major | `core.autocrlf=true` would rewrite hashed text files in every worktree | **Accept.** Confirmed on this machine | §1.2 `.gitattributes` eol and `-text` rules, `Repo.NoCrInHashedFiles`; snapshot and vendoring via `git -c core.autocrlf=false archive`; vendor-check on blob ids |
| T6 | tech · major | Facepunch transport bundles its own Facepunch 2.3.2; Platform Toolkit forbids a second Steam loop | **Accept** | §1.4 one Facepunch.Steamworks copy + vendored transport source; Platform Toolkit dropped; §1.5 `Backroads.Net.Steam`; `Net.SingleSteamApi`; M10 |
| T7 | tech · major | CPU gates use P-core numbers without a tier, though budgets were meant to be E-core | **Accept** | §3.4 real-time-factor gates per tier and core class; §1.10 gate JSON; M1, M2, M4, M6 gates; `esp_cli bench --affinity` |
| T8 | tech · minor | PhysX chassis listed only in M7 but assumed from M4 | **Accept** (merged with S2) | PhysX chassis (06 V1) in M3 on a second V agent; `Vehicle.BuildingImpact`; §2.2, §2.4, M7 |
| T9 | tech · minor | Forward+ ignores the per-object light limit, so that tier knob does nothing | **Accept** | §4.1 note; §4.3 tier row = real lights enabled (lamp pool, shadow atlas, headlights); `m5.perf.night-low` |
| T10 | tech · minor | Plates applied to H0 only; BASE − H paths would see phantom sinkage | **Accept.** 05 §5.10 says H and BASE | §1.8.7, §2.6, `Canon.BaseMatchesH`, M3 |
| T11 | tech · minor | APV and baked probes would bake an empty scene (runtime spawners) | **Accept** | §4.1 lighting-only bake scene inside the 2 AD APV timebox; §1.2 tree; M5 |
| T12 | tech · minor | Pitch and P4 gates fail correct implementations (cycle harmonics; 122 Hz firing ripple) | **Accept** | §1.9 pitch rule with k ∈ {1,2,4,8} and cycle harmonics, `pitchOrders`; M4 P4 measures tracking error with the firing band excluded |
| T13 | tech · minor | Struct sizes, semi speed, engine count, M13 reference wrong | **Accept.** Recomputed: 18 B, 38 B, 110.6 km/h, 25 engines | §5.3 `Pack = 1` + size asserts; §1.10 contracts; M10 111 ± 5 km/h; §3.2 25 engines; M13 re-run reference |
| T14 | tech · minor | 1e-5 m and 1 mm parity below float32 precision | **Accept** | §1.5 `WorldGridQueriesRef` (double); M1 tolerances with a 0.1 % grazing-ray allowance; T1.2 |
| T15 | tech · minor | `-br-mode server` client binary compiles the server out and keeps client code in | **Accept** | `ServerHeadless` (UNITY_SERVER) from M0; §1.5 rule 8 `Build.ServerContents`; §1.9 canary; §2.1 |
| T16 | tech · minor | `timeScale` double-counted in the step count | **Accept.** 01 §7.3 sketch confirmed | §3.3 time semantics, §3.4, `esp_step_accounting` at 0.5 |
| T17 | tech · minor | NGO already ships `SinglePlayerTransport` | **Accept.** Found in the bundled 2.13.0 | §1.5, §2.1, §5.1, M4 S-NET-1 reduced, M5 |
| T18 | tech · minor | 16-client bandwidth gate leaves ~2 % headroom; no batching specified | **Accept** | §5.3 one batched message per recipient per tick, written overhead model; gates in M8 and M10 |
| T19 | tech · minor | Inertia calibration at every spawn would hitch | **Accept** | §3.3, §3.5 baked `J_eff` with source-hash self-test; M4 EngineBake |
| T20 | tech · minor | Batchmode audio may not run, so "0 underruns" could pass vacuously | **Accept** | §1.9 `Audio.BatchmodeDspRuns`, `PlayerOnly` category; audio gates in player autotests |
| T21 | tech · minor | Two editor-update dates; LaunchContinuity compares different trucks | **Accept** | §1.3 single update at the `m1` tag; U4a; M4 `LaunchContinuity` is `[tune]` |
| S1 | seq · blocker | No operating model: who starts lanes, permissions, overnight, resume, keep-awake | **Accept** | new §1.11; "In plain words"; U19; task cards end with `HANDOFF.md` |
| S2 | seq · major | PhysX chassis after the milestones that need it | **Accept** (same fix as T8) | M3 V′ lane, +3.5 AD |
| S3 | seq · major | BRV v2 at half the source budget; ABS gated but not built; kill without re-plan | **Accept** | 13 AD over weeks 4-6 (M2 head start + M3); ABS in M3 deliverables; written re-plan on kill |
| S4 | seq · major | Your build waits on unrelated lanes' gates | **Accept** | §1.10 headline vs lane gates; every milestone split; definition of done |
| S5 | seq · major | No crash capture or session data | **Accept** | `CrashCollector`, SEH minidumps, `SessionMetrics`, `m0.crash.recover` (§1.9, §1.6, M0) |
| S6 | seq · major | Saves refused on any content change | **Accept** | §5.6 save schema separate from handshake, per-tile base hash, refunds, fixture saves, `Save.LoadsPreviousMilestones` |
| S7 | seq · major | "Online with friends" in week 17 needs port forwarding; no distribution; notices late | **Accept** (Relay route) | §5.1 Relay default; M8 Share-Build; notices from M1; U11a needed by M8; Steamworks start at M8 |
| S8 | seq · major | Perf gates only on a fast laptop; budgets per FixedUpdate; catch-up spiral | **Accept** | §1.9 min-spec proxy; per-frame budgets; `maximumDeltaTime = 4/120`; M4 kill on the proxy; RAM/VRAM budgets |
| S9 | seq · major | Agents compete with your playtests and with perf gates | **Accept** | §1.9 `.user-playing` and `machine.lock`, invalid-on-battery, median of 3 |
| S10 | seq · major | No fast merge tier; disk not budgeted | **Accept** | §1.9 merge/nightly/milestone tiers with wall-clock budgets; §1.10 disk and pruning |
| S11 | seq · major | Unity YAML merge conflicts across lanes | **Accept** | thin `Millbrook.unity`, `OWNERS.json`, `Repo.OwnersRespected`, UnityYAMLMerge driver (§1.2, §1.10) |
| S12 | seq · major | No time to act on feedback; no default for unanswered decisions; exact weeks promised; kerbs claim | **Accept** | 15 % feedback reserve; F10 "No" → issue; defaults in §7.2; date ranges; kerbs moved to weeks 7-9 in plain words |
| S13 | seq · major | Key clashes; no pause, help, settings | **Accept** | M2 `bindings.json` contract, `Input.NoDuplicateBindings`, starter on hold I, gears on Shift/Ctrl, clutch V, Esc menu, F1, `settings.json` |
| S14 | seq · major | Weeks 1-2 overloaded by grafts | **Accept** | four ESP tools moved to M4; 25-engine run as a report; payout references to M9 (`CanonData\refs\`) |
| S15 | seq · major | Hidden inversions: autosave before persistence; AI on v2 before v2 vehicles; dozer built twice | **Accept** | minimal world save in M8; BRV LOD1 + v2 hatch/pickup definitions in M6; dozer once on v2 in M7; U7 dependency sets |
| S16 | seq · minor | Shot PNGs break the identical-manifest gate | **Accept** (merged with T4) | shots excluded from identity, SSIM ≥ 0.99, frozen clock, renderer recorded |
| S17 | seq · minor | Golden traces recorded on v1 compared on v1.1 | **Accept** | traces replayed with `RoadPlates`/`RoadColliders` off; Unity self-trace baseline from M3 |
| S18 | seq · minor | Shipped builds never tested; IL2CPP adds risk without benefit | **Accept** | Mono until M14; `-br-smoke` and F10 in every build; smoke on the exact Client and share-zip builds |
| S19 | seq · minor | No backups; LFS grows without limit | **Accept** (merged with C15) | `backup.ps1`, U1 remote at M0, U21; DLL at tags only; importer output regenerated; `lfs-budget.ps1` |
| S20 | seq · minor | Wire format frozen too early; M5 gates too heavy; two single-player paths | **Accept** | `VehicleStateMsg` frozen at M8; M5 gates cut, StateValidator and cheat tests to M8; single player on the local host from M5 |
| S21 | seq · minor | Input System listed from M2 but needed earlier; editor date inconsistent | **Accept** | §1.4 Input System from M0; one update date (T21) |
| S22 | seq · minor | No short agent-instructions file; no step writes the M2 cards | **Accept** | `AGENTS.md` + `CLAUDE.md` in T0.1; T1.10 and a card-writing task at every milestone end |
| S23 | seq · minor | Soaks would trigger Windows Firewall prompts | **Accept** | soaks and tests bind 127.0.0.1; only `Host.bat` binds 0.0.0.0, explained in `PLAYTEST.md` |
| S24 | seq · minor | Probe evidence copied only after go-ahead; scratchpad may vanish | **Accept.** The folder still exists today | U20 pre-approval copy to `D:\Backroads-ref\probe01\`; T0.3 copies from there |
| C1 | legal · major | Notices only at M14; registry incomplete (glTFast, flex, fog, samples) | **Accept** | `third-party.json`, `notices.ps1`, `Licence.NoticesComplete`, Credits panel from M1, `assets-provenance.json` (CC0/CC-BY only) |
| C2 | legal · major | Real brand names inside shipped `.mr` files, ids and terms | **Accept.** Names confirmed in the vendor tree | `mr_sanitise`, roster-only Client builds, renamed ids and builtins, "engine brake"/"torque-sensing", `Content.NoRealMarks` (§3.2, §3.10, §1.6) |
| C3 | legal · major | Synty AI clause enforced only by convention | **Accept**; the deny rules and allowlist are proposals you accept, not settings agents change | §4.7 seven-step procedure; phase F gating; `-br-no-synty`; `artifacts\user-only\`; `Art.NoSyntyInBakedMeshes`; U10 |
| C4 | legal · major | Vehicle workflow traces one real car | **Partly accept.** Blended proportions, named deviations, the review question and a takedown procedure are in. The automated silhouette-IoU < 0.85 test is not: it would need you to draw outlines, and no legal standard maps to an IoU number, so it would give false assurance | §1.7, §4.5 phase E, per-vehicle workflow |
| C5 | legal · major | €5 server is Linux; plan built Windows-only | **Accept.** WSL2 Ubuntu-24.04 confirmed | U4b Linux Dedicated Server; §5.5, §5.7, M12; `soak.ps1 -ServerLinux` |
| C6 | legal · major | Week-17 online likely fails for most friends; no distribution; SmartScreen; UGS overage | **Accept** (merged with S7) | Relay default, usage alert, 8-player cap, Share-Build, SmartScreen note, `Net.RelayLoopback` |
| C7 | legal · major | No privacy section; hardware statistics on | **Accept** | §5.9; §1.3 privacy settings; `m0.net.egress`; `PRIVACY.md`; hashed SteamIDs |
| C8 | legal · minor | OSM attribution and ODbL §4.6 | **Accept** | Credits text and `licenses\osm\townProfile.json` with the method from M1; `Licence.OsmAttribution` |
| C9 | legal · minor | piranha commit predates its LICENSE; flex/bison notices | **Accept** | §1.6 VENDOR note and copied LICENSE (U3e); flex notice; `Licence.GeneratedHeadersIntact` |
| C10 | legal · minor | Steam lead times and unverified $0 claims | **Accept** | §5.7 timeline; U11b starts at M8; M14 Coming Soon and release rules; AppID-480 builds stay on this PC |
| C11 | legal · minor | Relay cost understated | **Accept.** 04 §3.5 itself called the free tier ambiguous | §5.7 ≈ $23-48 at 1k players; measured cost line from M5 |
| C12 | legal · minor | Title and company name not cleared; names drive the save path | **Accept** | U18; `GameInfo`; placeholders until M8 |
| C13 | legal · minor | No record of agent-made content for Steam/itch AI disclosure | **Accept** | `content-provenance.json`; M14 survey draft |
| C14 | legal · minor | Workshop has no legal limits | **Accept** | U17 conditions; `Content.NoSyntyInModPath` |
| C15 | legal · minor | LFS use understated; duplicates | **Partly accept.** The importer copy is no longer committed and the DLL only at tags; a 6 GiB budget gate is in. The `.blend` files stay in LFS (they are the art source), committed at review points rather than never | §1.2 LFS budget, §1.6, §1.9 |

Where a reviewer offered alternatives, the choice was: S7/C6, Unity Relay join codes (Steam SDR on AppID 480 was not chosen, because the plan keeps test-AppID builds on this PC, C10, and a real AppID takes 15-30 days of paperwork); T1, a lag that follows the steps per frame (L = 2-4) rather than a fixed L = 2, because the worker cannot finish four steps inside one 30 fps burst; T6, Facepunch only rather than Platform Toolkit only, because lobbies and SDR need Facepunch anyway; T3, wrapper hooks plus bucket hashes rather than either alone.

---

## Decisions for you

The plain-words version of §7.2. Each item says what you choose and what we recommend. If an answer is not in by the time it is needed, a safe default applies (listed in §7.2), and nothing that costs money, signs you up or uses your logins ever happens by default.

**Before we start**
1. **Go ahead with this plan in a new, separate project folder** (`D:\Backroads-Unity`). Your browser game is never changed. *Recommended: yes.*
2. **Let the agents copy the finished Millbrook out of a temporary copy of the browser game.** *Recommended: yes.*
3. **Save the early engine experiment now.** It sits in a temporary folder that may be cleaned up; one copy to `D:\Backroads-ref\probe01\` keeps it. *Recommended: yes, today.*
4. **How the agents work.** Leave the laptop on, plugged in and not sleeping while a milestone runs, and approve once a fixed list of safe things they may do without asking (build, test, save work locally; never upload, sign in or install). *Recommended: yes. If they only work while you are at the laptop, everything takes two to three times longer.*
5. **Free downloads**, each approved once: Unity's add-on packages, a small library for drawing kerbs and sidewalks, a small tool that unlocks all 25 ready-made engines, one licence text file, and later the Steam connection library. *Recommended: yes to all.*
6. **Backups.** Pick a second drive or USB stick for nightly backups, and create your private GitHub copy early. *Recommended: yes, at the first build.*

**In the first weeks**
7. **Update Unity once**, to the newest 6.3 version, at the end of week 2 (one click in Unity Hub). Never use Unity 6.5. *Recommended: yes.*
8. **The truck's engine:** an authentic big petrol V8 (real trucks of this size use one) or a diesel-like stand-in that will still sound like petrol. *Recommended: the petrol V8.*
9. **Made-up names.** "Ranger 4x4" and "Muncher D3" contain real brand or model names and must change; real brand names are also removed from the engines. The agents suggest names and you pick. *Recommended: yes.*
10. **New intersections may reshape the ground only right next to roads**; everything else in Millbrook stays exactly as it is. *Recommended: accept.*
11. **Play and tell us** (press F10 in the game): does the week-4 build drive like the browser game, and does the engine-driven truck feel right around week 9?

**Around weeks 10-14**
12. **The town-square roundabout.** A proper four-way roundabout needs the harbor road moved slightly near the square, which changes how the square looks. The alternative keeps the road and makes a three-way roundabout with a T-junction close by. You drive both in a preview build around week 12. *Recommended: the four-way roundabout with the harbor road moved.*
13. **Real vehicles to take inspiration from.** For each new vehicle, pick 2-4 real ones it should feel like and collect a few photos yourself; ours will blend them and never copy one. *Recommended: yes.*
14. **Synty art packs (optional).** If you want them, buy the one-time licences (Town + Construction, plus Swamp, about $155), not the subscription. Synty's licence forbids its art ending up in AI training, so first switch off "use my chats to improve the model" in your Claude privacy settings, email Synty and wait for their written OK, and run the import tool yourself; the agents are then blocked from reading those files. *Recommended: decide around week 10; skipping it is fine.*

**Before playing with friends (around week 17)**
15. **A free Unity online-services account**, so friends join with a code: no router settings, no payment card. Set a usage alert in it. *Recommended: yes, by week 17.*
16. **Steam.** Sign up as a Steam developer, pay the $100 fee and fill in the tax and bank forms. The paperwork takes 15-30 days, and Steam makes you wait 30 days between paying and releasing. *Recommended: start around week 17, so Steam play works by about week 22.*
17. **Publisher name and game title.** Pick a publisher name before the first saved games (week 17). "Backroads" is already used by others (a travel company holds trademarks with that word, and there is a small game of that name on itch.io), so run a name check before any public store page. *Recommended: keep "Backroads" as the working title and check it before going public.*
18. **Where friends get the game.** You upload each shared build wherever you like; agents never upload. *Recommended: a private itch.io page.*

**Later (defaults are fine)**
19. Traffic before offroad *(recommended)*; progress kept per online server, like Motor Town *(recommended)*; races on Millbrook's own roads first *(recommended)*; aim at a GTX 1070 / 6-core class PC, and borrow one for a test if you can *(recommended)*.
20. **Your own online server** (optional, about €5 a month on Linux). If you run one, you are responsible for players' data; a short privacy page is prepared for it. *Recommended: not before the dedicated-server drop (about week 27).*
21. **Player mods on Steam Workshop** later, with rules (no Synty art, no real logos, a way to remove reported mods). *Recommended: yes, later.*
22. **Extra engine research** (turbo, engine brake, real diesel) as a separate track after the full game. *Recommended: decide after week 21.*
23. **When agents may upload code to GitHub, and how often you want builds.** *Recommended: you upload, or tell us when agents may; one build per milestone.*
