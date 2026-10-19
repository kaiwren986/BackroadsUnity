# M2 handoff: lane V, worktree `vs` (T2.4 engine sound in vehicles)

Branch `m2/vs-sound` in `D:\Backroads-Unity-wt\vs`, cut from `main` at `c42a473`; `main` (lanes W, V, R and A merged,
`0e5f02c`) was merged in at `0a3005e`. Nothing was pushed, installed, downloaded or signed into. `D:\Backroads` (the
oracle) and `D:\Backroads-ref\engine-sim` were only read. **No ABI change**: every struct, size, function and status code
of ADR-002 is as frozen and `esp_version()` is still 1; six compatible refinements are written up in ADR-002's new
"M2 addendum" for the integrator to accept (deviation 1). The installed `Game\Assets\Plugins\x86_64\EngineSimPlugin.dll`
and `.version.json` are modified in the working tree and not committed (the integrator commits them at the `m2` tag,
built with `tools\ci\build-native.ps1 -Force`).

## In plain words

The truck now has a real engine voice: a 7.4-litre gas big-block V8 (`v8_bigblock_a`, decision U6), a line-by-line copy
of engine-sim's own big-block truck engine. Every vehicle's engine-sim engine follows the browser game's engine speed
and your foot (FOLLOW_RPM), as the plan says for M2. When you get in, the starter cranks it and it catches; the
tachometer shows engine-sim's speed. The rare 30 ms audio dropout of the M1 soak is understood: this laptop briefly
freezes (about 50 ms, every 10-20 minutes, all programs at once), and the old test then asked for more sound at once
than a real sound card would. The engine now keeps a slightly larger safety buffer (about 16 ms more) and the test
behaves like Unity's sound output; the last three 10-minute soaks were clean, and five real weaknesses found on the
way are fixed (one of them caused a short silence after every game hitch longer than 33 ms). On the Engine
Bench (Esc, Engine Bench) the V8 is the first engine, and **V** plays a "truck demo": the truck's classic engine pulls
through the gears and engine-sim follows it exactly as in the game.

## Done

| Card step | What | Where |
|---|---|---|
| 1 | **`EngineSimVehicleEngine` : `IVehicleEngine`**, registered with `VehicleEngines.Register` at BeforeSceneLoad (client builds; the assembly is `!UNITY_SERVER`). Per fixed step from `EngineState`: running (ignition, rpm at least half the vehicle's idle) = FOLLOW_RPM at `RpmMap` (PLAN 3.5: `rpm_es = idle_es + (rpm_js - idle_js)(0.97 redline_es - idle_es)/(redline_js - idle_js)`; proportional below idle; capped at the rev limit), throttle = the pedal; with the starter held at the key turn, engine-sim first cranks and catches on its own starter (FREE) until 75 % of its idle; key off = FREE, it runs down. `DisplayRpm` = the mapped rpm (engine-sim's own while cranking or running down). `AttachTo` parents its clip-less 2D AudioSource (`EngineSimAudio`, S-AUD-1's local-car path) to the exhaust socket; the native engine lives as long as that GameObject; the source pauses while `Time.timeScale` is 0. Creation costs 4-6 ms on the main thread (measured). Tracking statistics for the gates. | `Game/Assets/Backroads/EngineSim/Runtime/Vehicle/` |
| 2 | **`v8_bigblock_a`**: C++ port of upstream's gas big-block truck script and its part-library nodes (bore 4.25 in, stroke 4.0 in, peanut-port heads, stock cam 108/113 deg, 650 cfm carburettor, distributor 1-8-4-3-6-5-7-2, two exhausts, IR `smooth_39`). Roster: `v8_bigblock_a` (truck), `v8_cross_a` (pickup), `f4_boxer_a` (rally), `i4_synth` (hatch; `i4_vvt_a` not ported, the hatch's first choice is logged as "not in this build's catalog"). Measured idles in `EngineDef.idleRpm`: 918 / 676 / 766 / 729 rpm. | `native/EngineSimPlugin/src/builders/v8_bigblock_a.cpp`, `EngineCatalog.cs` |
| 3 | **Release-soak underrun** (M1 carry): diagnosed and fixed, see "The soak underrun" below. Worker diagnostics (every batch noted; an underrun logs the batches before it; a summary line with the steady-state margins), a high-resolution worker timer, a low-water coast, the worker audio class (MMCSS "Pro Audio", priority 3), the fill target covering the command cadence, FOLLOW_RPM repaying borrowed steps from spare audio, the audio thread waking the worker; Unity passes its whole output buffer (256 x 4) as the DSP buffer; the soak harness models Unity's audio output (`DeviceConsumer`). `build-native.ps1 -Soak N` runs N soaks in a row under machine.lock. | `native/EngineSimPlugin/src/{worker,instance,audio_render,platform}.cpp`, `tools/ci/build-native.ps1` |
| 4 | **`-br-silent` no longer silences `AudioTap`**: `EngineSimAudio.Tap` is fed with what `esp_read_audio` wrote, before Unity applies `AudioListener.volume`; the bench's tap is source-fed; `m1.bench.revcycle` needs no listener-volume trick any more. | `EngineSimAudio.cs`, `AudioTap.cs` |
| 5 | **`build-native.ps1` leaves an identical installed DLL alone**: no copy when the installed version file already names the same abi, git and sourceHash of committed sources (any `-Heavy` or `-Force` rebuild of unchanged sources). | `tools/ci/build-native.ps1` |
| 6 | **Tests**: PlayMode `EngineSim.FollowInVehicle.TracksMappedRpmAtHigh` and `.SourceFedTapIgnoresListenerVolume`; EditMode `EngineSim.VehicleEngine.{RpmMapFollowsPlan35, RosterHasTheVehicleEngines, UnknownEngineIsNull}`; autotests `m2.engine.follow` (kind `engine-follow`: the gate in the player, incl. the min-spec proxy) and `m2.bench.v8` (kind `bench-shot`: the V8 on the bench, pitch rule, dyno sweep, truck demo, screenshots, WAVs); ctest `esp_bigblock` (quick), `esp_follow_hitch` (heavy); `esp_cli roster`. | `Tests/*/EngineSim/`, `Testing/Runtime/Scenarios/EngineSim/`, `native/EngineSimPlugin/tools/harness/roster.cpp` |
| extra | **Engine Bench**: opens on the V8 (`-br-bench-engine <id>` picks another); **V** / "Truck demo" plays `FollowRamp` through `IVehicleEngine` with a live readout (classic rpm, engine-sim rpm, rpm map, tracking, MAP, ring, RTF). `FollowRamp` (the scripted classic-engine drive) is shared by the bench, the PlayMode test and the autotest. `EngineSimLog` keeps multi-line plugin entries together. | `Bench/EngineBench.cs`, `Vehicle/FollowRamp.cs` |

## Gates (card T2.4), expected vs measured

All on this laptop, 2026-10-19, on the final commit unless stated. PlayMode in the editor (batchmode); autotests in the
M2 Dev player at 2560x1440, 60 fps cap, `-br-silent`; reports in `artifacts\autotest\<id>[-fps60|-minspec]\`.

| Gate | Expected | Measured |
|---|---|---|
| `EngineSim.FollowInVehicle` tracking (PlayMode, High tier, unpinned) | engine-sim rpm vs the mapped rpm < 40 rpm RMS, per roster engine | **pass: 0.00 rpm RMS** on all four (truck `v8_bigblock_a`, pickup `v8_cross_a`, rally `f4_boxer_a`, hatch `i4_synth`), 959-993 FOLLOW steps each, 0 repaying results, 0 underruns. The newest result against this step's target (the 2-4 step lag of PLAN 3.4, "as heard"): 39.6 / 47.1 / 45.4 / 46.4 rpm RMS |
| `EngineSim.FollowInVehicle` RTF at High (PlayMode, unpinned) | >= 2.0 per roster engine | **pass: 3.58 / 3.63 / 4.52 / 8.12** (truck / pickup / rally / hatch; 10, 10, 20, 10 kHz; 4 gas substeps) |
| Same in the player (`m2.engine.follow`, `-Quality Mid` = High engine tier, 60 fps) | RMS < 40, RTF >= 2.0, 0 underruns | **pass**: 0.00 rpm RMS all four; RTF **3.86 / 3.80 / 4.71 / 8.27**; 0 underruns; main-thread cost p99 0.004 ms per FixedUpdate; ring target 47-50 ms |
| Player default quality (High render = Ultra engine tier: 8 substeps, IR up to 10,000 taps) | same | **pass**: 0.00 rpm RMS; RTF **2.76 / 2.75 / 3.16 / 5.79**; 0 underruns; p99 0.004 ms |
| RTF on the min-spec proxy (`m2.engine.follow -MinSpec`: first 6 E-cores 0xFC, Low tier, 30 fps) | >= 1.5 per roster engine | **pass: 3.01 / 3.00 / 3.29 / 4.41**; tracking 0.00 rpm RMS; 0 underruns; ring target 70-74 ms; p99 0.006-0.016 ms |
| Release soak x3 in a row (`build-native.ps1 -Soak 3`: `f4_boxer_a` Ultra, 20 kHz, 8 substeps, 6,134 IR taps, 10 min each) | 0 underruns in 3 consecutive soaks | **pass: 0, 0, 0** on the final commit `b1faaff` (series 4, `artifacts\gates\build-native-soak.json`): memory growth 0.027-0.031 MB, RTF 3.22-3.23; the soaks met 6, 0 and 1 stalls of the whole machine (up to 48 ms), which the modelled audio device absorbs as device gaps. Before the harness modelled the output, 5 underruns in 4 of 10 soaks, every one after such a stall (below) |
| `-br-silent` does not silence `AudioTap` | a tap still records the engine | **pass**: PlayMode `SourceFedTapIgnoresListenerVolume` -13.9 dBFS at `AudioListener.volume` 0; `m1.bench.revcycle` under `-br-silent` 4/4 pitch windows, 0 underruns, tap fed by the source, no listener-volume trick |
| `build-native.ps1 -Heavy` leaves an identical installed DLL alone | no rewrite when the sources are unchanged | **pass**: the final `-Heavy` (and the earlier one) rebuilt the same committed sources and printed "the installed DLL is already this build (same abi, git and sourceHash); left alone"; a build of uncommitted sources is still copied |
| The truck sounds like a big-block V8 on the Engine Bench (`m2.bench.v8`) | starts, idles, pitch rule, dyno | **pass**: starter released at 1.24 s, idle 819 rpm (bench load on), pitch rule at 4,193 rpm (ACF order 1.00, peak order 3.02), sweep 531 N m at 2,500 rpm / 232 hp at 4,000; truck demo through `IVehicleEngine`: tracking 0.00 rpm RMS, 0 underruns up to its screenshot (1 right after the capture's stall, not gated). Listen: `artifacts\autotest\m2.bench.v8-fps60\bench_v8.wav`, `demo_v8.wav` |
| `esp_bigblock` (port vs script) | report; within 5 % | **pass: 0.00 %** at all 10 points (table below) |
| `esp_follow_hitch` (heavy): FOLLOW_RPM through 100 and 250 ms main-thread stalls | 0 underruns | **pass: 0** around the 100 ms and the 250 ms stall (Unity's clock: at most 4 fixed steps per frame); 283 ms of borrowed steps kept as debt, as designed. With M1's repayment (`--repay-all`) the same run went silent after each stall (seen as ~65 ms silences in `m2.vehicles.drive` before the rule) |
| Integration with lane V's vehicles (`m2.vehicles.drive`, main merged) | engines attach and play | **pass**: the truck gets `v8_bigblock_a` (created in 4.0-4.8 ms), switching to the rally makes `f4_boxer_a`, back to the truck; 0 underruns (one before the FOLLOW repayment rule) |
| Regression: `m1.bench.revcycle` at 60 fps | M1 gates | **pass**: 4/4 pitch windows, 0 underruns, clipping 0, RTF 3.10, p99 0.004 ms, ring target 52 ms |
| Native quick ctest / heavy | green | quick **13/13** (incl. `esp_bigblock`); heavy **6/6**: cosim matrix 33/33, bench Ultra E-pinned RTF 2.47 (gate >= 1.5) and P-pinned 3.14, all 25 upstream engines, release soak 0 underruns (a fourth clean soak on the final build), `esp_follow_hitch` |
| EditMode / PlayMode | green | EditMode **130 passed, 2 skipped, 0 failed** (lane V 14/14); PlayMode **9/9** (lane V 3/3) |
| Dev player build | builds | **pass**, `builds\M2-dev`, 225.1 MiB |

### The truck's V8 (`esp_bigblock`, ctest quick tier)

WOT, 2 s settle + 1 s average per point, the port and the upstream script through the same DLL:

| rpm | 1,000 | 1,500 | 2,000 | 2,500 | 3,000 | 3,500 | 4,000 | 4,500 | 5,000 | 5,500 |
|---|---|---|---|---|---|---|---|---|---|---|
| port N m | 403.2 | 474.7 | 531.7 | **536.9** | 521.7 | 467.3 | 415.1 | 333.9 | 255.8 | 187.4 |
| script N m | 403.2 | 474.7 | 531.7 | 536.9 | 521.7 | 467.3 | 415.1 | 333.9 | 255.8 | 187.4 |
| port hp | 56.6 | 100.0 | 149.3 | 188.5 | 219.8 | 229.7 | **233.2** | 211.0 | 179.6 | 144.7 |

Peak 537 N m (396 lb ft) at 2,500 rpm, 233 hp at 4,000 rpm; difference 0.00 % at every point (both are deterministic in
one process); starts on its starter in 0.98 s, idles at 918 rpm (903-931), like the script. Redline 5,500, limiter
7,000 rpm. In the Engine Bench (`m2.bench.v8`): idle 819 rpm with the bench's road-like load on, sweep 531 N m at
2,500 rpm and 232 hp at 4,000, pitch rule passes at 4,194 rpm (ACF order 1.00, peak order 3.02).

### The soak underrun

M1 left one ~30 ms underrun in about one of three 10-minute release soaks (`f4_boxer_a` Ultra: 20 kHz, 8 gas
substeps, 6,134 IR taps; a frame loop at 60 fps; a time-critical audio consumer). M2 first made it visible: the worker
now notes every batch it produces; a counted underrun logs the batches before it, the worker's last wait (deadline,
wake, by timer or event) and when the audio thread last woke it; a summary gives the steady-state margins (longest gap
between pushes, lowest fill before a push, wake lateness, most descheduled). Then thirteen 10-minute soaks (plus a
5-minute M1-conditions baseline) in four series, each on the newest build:

| Series (build) | Changes | Soaks: underruns | What the log showed |
|---|---|---|---|
| 1 (`f425557`) | diagnostics, high-resolution worker timer, low-water coast; soak worker scheduled like Unity (+1, no EcoQoS) | 0, 0, **2** | both: last push normal (fill 35-37 ms), then no push for 52.7 / 48.8 ms while the frame loop stalled 55 ms; the consumer kept reading |
| baseline, 5 min | M1's scheduling | 0 | lowest fill 15.9 ms, no coasting |
| 2 (`5c89430`) | + worker audio class (MMCSS "Pro Audio"), fill target covers the command cadence | 0, 0, **1** | gap 46.3 ms, frame loop 48 ms; also a 121.5 ms stall of every thread incl. the consumer (not counted: the consumer reset its clock after 100 ms) |
| heavy (`7929183`) | + audio-thread wake, FOLLOW repayment rule | 0 | a 121.7 ms whole-process stall again; worker wake lateness otherwise at most 0.3 ms |
| 3 (`7929183`) | same | **1**, 0, **1** | (1) the worker's wait ended 28.5 ms after its deadline at the same moment the consumer woke and signalled; (2) the worker stopped for 46 ms *inside* a batch (wall 48.7 ms, CPU 2.7 ms); the frame loop stalled 51-54 ms both times |
| 4 (`b1faaff`, final) | the harness models the audio output; Unity passes its whole output buffer | **0, 0, 0** | soak 3 had the same whole-machine stall (audio thread stopped 47.8 ms, worker woke 31.9 ms late, gap 55.1 ms): now 1 device gap (21.3 ms lost on the device side), 0 ring underruns. Soak 1 ran on a busy machine (126 frames over 25 ms, 6 device gaps, 122 coast events): 0 underruns |

**Diagnosis.** All five counted underruns have one shape: every thread of the process stopped at once for 46-53 ms
(the frame loop, the worker at MMCSS priority, once in the middle of a batch, and the time-critical consumer), on a
laptop shared with four other agent lanes and desktop apps, about every 10-20 minutes; two 121 ms stalls of the same
kind went uncounted. No priority helps against that; the worker's own wake-ups were never late otherwise (0.2-0.3 ms).
The underrun itself came from **the harness's consumer**, which after waking read every missed block from the ring at
once (up to 100 ms of audio). No audio device does that: Unity's output at Best latency (256 frames x 4, S-AUD-1)
refills at most its own 21 ms buffer and the rest is a glitch on the device side, as for any program during such a
stall. **Fixes:** the soak harness now models that output (`DeviceConsumer`, device gaps counted separately;
`--m1-baseline` keeps M1's consumer), and Unity passes its whole output buffer as the "DSP buffer", so the ring holds
at its lowest what the output can drain at once after a stall (fill target about 49 ms at 60 fps instead of 33; 16 ms
more latency, under the classic engine's own 71 ms rpm smoothing). Note on the worker summary: a command that only
repays borrowed steps (no steps, no push) is noted with fill 0, so "pushes with fill < 2 ms" counts those after coast
events (series 4: 129, 27 and 1 after 122, 26 and 1 coast events), not low fills; the lowest real fill is in the
underrun reports (none in series 4). Along the way the investigation found and fixed
five real weaknesses, each now in ADR-002's addendum: the worker's timed waits could last a whole 15.6 ms timer tick
(high-resolution timer); a frame later than its usual gap but shorter than the hitch threshold could empty the ring
(low-water coast, woken by the audio thread too); the fill target ignored that frames above 120 fps send no commands
(cadence); the low-water coast and repayment could alternate forever after a hitch; and in FOLLOW_RPM, repaying steps
borrowed through a hitch longer than 33 ms left up to the hitch's length of silence afterwards (repay from spare audio
only; `esp_follow_hitch`).

### Roster engines (`esp_cli roster`)

| Engine | Vehicle (JS idle-redline) | f | Starter release | Idle (no pedal) | WOT peak | Peak power | Map (JS -> engine-sim) |
|---|---|---|---|---|---|---|---|
| `v8_bigblock_a` | truck (650-2,700) | 10 kHz | 0.97 s | 918 | 537 N m @ 2,100-2,500 | 233 hp @ 3,900-4,000 | 918-5,335 rpm, x2.155 |
| `v8_cross_a` | pickup (750-5,600) | 10 kHz | 1.62 s | 676 | 725 N m @ 3,300 | 486 hp @ 6,000 | 676-6,305 rpm, x1.161 |
| `f4_boxer_a` | rally (950-7,600) | 20 kHz | 2.10 s | 766 | 229 N m @ 4,600 | 166 hp @ 6,000 | 766-6,305 rpm, x0.833 |
| `i4_synth` | hatch (850-6,800) | 10 kHz | 1.47 s | 729 | 360 N m @ 3,300 | 247 hp @ 6,000 | 729-6,305 rpm, x0.937 |

## Deviations and decisions

1. **ADR-002 M2 addendum instead of a new ADR** (`docs/adr/ADR-002-esp-abi-v1.md`, end): no struct, size, function or
   status code changed, so no ABI v2; six compatible refinements are written up for the integrator to accept:
   (1) `EspWorkerOptions.priority = 3` = the worker's audio class (MMCSS "Pro Audio", `TIME_CRITICAL` fallback; the
   value was unused and clamped to 2); (2) the fill target's frame time covers the command cadence (frames shorter
   than the fixed step); (3) the low-water coast (not while commands repay with no steps), high-resolution worker
   timer; (4) **FOLLOW_RPM repays borrowed steps only from spare audio** and keeps the rest as debt, so in this mode
   engine time may run ahead of physics time (FREE and SLAVED_CRANK unchanged; `esp_step_accounting` exact);
   (5) `esp_read_audio` sets the worker's wake event once per low episode (still lock-free and allocation-free);
   (6) Unity passes its whole output buffer (block x buffers) as `audio_dsp_buffer_frames`, so the fill target is about
   49 ms at 60 fps and 70-74 ms at 30 fps (PLAN 3.6 said about 35 and 55-60).
2. **Unity's worker scheduling**: High and Ultra now run the worker in the audio class (3); Low keeps -1 (M1
   deviation 12); `-br-esp-priority` accepts -2..3.
3. **The release soak runs the worker like Unity** (audio class, power throttling off). M1's harness ran it at normal
   priority with power throttling allowed, which no game build does; `esp_cli soak --m1-baseline` keeps M1's
   scheduling for comparison (a 5-minute baseline had 0 underruns).
4. **Tracking is measured step-aligned**: engine-sim's result for physics step k against the mapped rpm sent with step k
   (what FOLLOW_RPM achieves). The newest result against the current step's target includes the 2-4 step lag of PLAN 3.4
   and is reported beside it (about 40-47 rpm RMS at 60 fps, 77-92 at 30). Results of commands that only repaid
   borrowed steps after a hitch (no steps run) are counted, not compared.
5. **`DisplayRpm`** is the mapped rpm while following; while cranking or running down after the key is off it is
   engine-sim's own rpm (the vehicle's classic rpm is 0 then).
6. **Key turn with the starter held**: engine-sim cranks and catches on its own starter (FREE) until 75 % of its idle,
   then follows (lane V's `SimVehicle` holds the starter for 0.5 s after the engine comes on). Without the starter the
   dyno spins it up to the mapped idle in one step.
7. **Hatch engine**: `i4_synth` (the card allows it); `i4_vvt_a` is not ported. The hatch asks for `i4_vvt_a` first;
   that is logged once as info.
8. **The Engine Bench opens on the V8** (was the flat-four) and has the vehicle demo; `FollowRamp` is in the runtime
   assembly (not dev-only) so the demo works in Client builds.
9. **Autotest gates exclude the engine's creation frames and screenshots** (as `m1.bench.revcycle` does): underruns
   count from 1 s after the key turns; `m2.engine.follow` starts after 30 calm frames; `m2.bench.v8` gates the demo up
   to its screenshot and reports the rest (0 in the final run).
10. **Engine creation is synchronous** on the main thread when the player gets in: 4-6 ms measured, so no async load.
11. **Pause**: the engine's AudioSource pauses while `Time.timeScale` is 0, so a pause does not run the ring dry.
12. Unity 6000.3 sets `m_EnableMultiplayerRoles: 1` in `ProjectSettings/MultiplayerManager.asset` on the first import of
    a fresh worktree; reverted here (I owns ProjectSettings).
13. **The soak harness models Unity's audio output** (`DeviceConsumer`: one 256-frame block per period, after a stall at
    most 4 blocks, the rest counted as device gaps) and passes 1,024 frames as the DSP buffer like Unity. M1's consumer
    read every missed block from the ring after a stall; `--m1-baseline` keeps it for comparison.
14. The worker summary's "pushes with fill < 2 ms" also counts repaying commands (noted with fill 0); a small fix for
    the next native change (set the note's fill before the early return in `runSteps`).

## For I

1. **`3b698be` REQUEST FOR I**: the scenario files `m2.engine.follow.json` and `m2.bench.v8.json` and two `OWNERS.json`
   prefixes for them (`Game/Assets/StreamingAssets/Autotest/m2.engine.` and `m2.bench.` -> V, like `m1.bench.`).
2. **Accept ADR-002's M2 addendum** (deviation 1), or ask for a separate ADR. It is compatible (esp_version 1).
3. **HUD tach redline (contract)**: `VehicleHud.Rpm` is now engine-sim's mapped rpm (truck: 918-5,335) while lane V's
   `SimVehicle` still fills `RedlineRpm` with the classic redline (truck 2,700), so the tach bar overflows. Suggest
   adding `float DisplayRedlineRpm { get; }` to `IVehicleEngine` (`EngineSimVehicleEngine.DisplayRedlineRpm` exists:
   the engine's redline on the `DisplayRpm` scale; the vehicle's redline maps to 97 % of it) and having `SimVehicle` use
   it when an engine is attached.
4. **`tools\ci\gates.json` (M2)**: lane steps `autotest.m2.engine.follow` at `-Fps 60` and at `-Fps 60 -Quality Mid`,
   `autotest.m2.engine.follow -MinSpec` (perf), `autotest.m2.bench.v8` (screenshots); nightly `native.soak` =
   `build-native.ps1 -Soak 3` (perf, machine.lock, about 32 min) beside `native.heavy`. Note that `autotest.ps1` writes
   `-Quality Mid` and default runs to the same folder (`<id>-fps60`).
5. **`m2.drive.route9` (T2.10)**: tap the player's engine before the listener volume:
   `var e = vehicle.Engine as EngineSimVehicleEngine; var tap = e.AudioObject.AddComponent<AudioTap>(); e.Audio.Tap = tap;`
   (then `-br-silent` is fine). For the pitch rule use `e.EngineRpm` (engine-sim's own rpm, not `DisplayRpm`) and
   `e.Def.pitchOrders`; the V8 passes at 4,194 rpm. Count underruns from 1 s after the engine is created, and not across a
   screenshot (a capture stalls the main thread for longer than the plugin coasts). `e.TrackingStats` gives the tracking.
6. **Engine ids for vehicles** (lane V's `ClassicVehicleDef`, already right): truck `v8_bigblock_a`, pickup `v8_cross_a`,
   rally `f4_boxer_a`, hatch `i4_vvt_a` then `i4_synth`.
7. **M1 carries done here**: the release-soak underrun (see above), `-br-silent` vs `AudioTap` (GameBootstrap may keep
   `AudioListener.volume = 0`), `-Heavy` and identical DLLs. Still yours from M1: the server scene list, `build.json`'s
   plugin stamp.
8. `ProjectSettings/MultiplayerManager.asset`: Unity flips `m_EnableMultiplayerRoles` to 1 on a fresh import (deviation
   12); commit one value so worktrees stop showing it as modified.
9. **The DLL for the tag**: `tools\ci\build-native.ps1 -Force` on the merged commit, then commit
   `Game/Assets/Plugins/x86_64/EngineSimPlugin.dll` and `.version.json`. A later plain or `-Heavy` run leaves it alone.

## Next (lane V)

- M4: SLAVED_CRANK (Option D) with `J_eff` from EngineBake; the M2 path stays as "classic engine"; revisit the FOLLOW
  repayment rule (addendum item 4) for SLAVED_CRANK, where engine time must stay on physics time.
- `i4_vvt_a` for the hatch (port of the upstream VTEC example or a sanitised script) when wanted.
- Remote engines (M8): `EspAudioGenerator` from S-AUD-1 with FOLLOW_RPM; the audio class per engine needs a look with
  23 engines (MMCSS is per thread).

## Resume

```powershell
cd D:\Backroads-Unity-wt\vs                        # branch m2/vs-sound
git log --oneline main..HEAD
tools\ci\build-native.ps1                          # quick ctest (13 tests incl. esp_bigblock), installs the DLL
tools\ci\build-native.ps1 -Soak 3                  # three 10-minute release soaks in a row (machine.lock, ~32 min)
tools\ci\build-native.ps1 -Heavy                   # cosim, bench E/P, all engines, release soak, esp_follow_hitch
tools\ci\unity-tests.ps1 -Platform EditMode
tools\ci\unity-tests.ps1 -Platform PlayMode        # EngineSim.FollowInVehicle (about 45 s)
tools\ci\build-player.ps1 -Profile Dev -Milestone M2
tools\ci\autotest.ps1 -Scenario m2.engine.follow -Fps 60                  # Ultra engine tier (default quality)
tools\ci\autotest.ps1 -Scenario m2.engine.follow -Fps 60 -Quality Mid     # High engine tier
tools\ci\autotest.ps1 -Scenario m2.engine.follow -MinSpec                 # Low tier on the min-spec proxy
tools\ci\autotest.ps1 -Scenario m2.bench.v8 -Fps 60                       # screenshots bench_v8_dyno, bench_v8_demo
tools\ci\autotest.ps1 -Scenario m1.bench.revcycle -Fps 60
native\build\Release\esp_cli.exe bigblock          # port vs script
native\build\Release\esp_cli.exe roster            # crank, idle, WOT peak of every builtin
```
