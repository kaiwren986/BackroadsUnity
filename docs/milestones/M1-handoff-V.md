# M1 handoff: lane V, T1.6-T1.8 (engine-sim plugin v1, the C# side and Engine Bench, spike S-AUD-1)

Branch `m1/v-esp` in `D:\Backroads-Unity-wt\v`, cut from `main` at `2f618e8`; `main` (with lane W, `26ef0f4`) was
merged in at `a86b2f2`. Nothing was pushed, installed or signed into. The only download was the allowlisted
win_flex_bison zip (U3c, approved by the user). `D:\Backroads` and `D:\Backroads-ref\engine-sim` were only read.
The installed `Game\Assets\Plugins\x86_64\EngineSimPlugin.dll` and `.version.json` are modified in the working tree
and not committed (the integrator commits them at the `m1` tag, built with `-Force`).

## Done

| Card | What | Where |
|---|---|---|
| T1.6 | **ESP plugin v1.** `UnitySim` and `UnityTransmission` (engine-sim's simulator without its transmission, the FREE / FOLLOW_RPM / SLAVED_CRANK couplings, `SlavedCrankPD`, wheel load); an `Instance` per engine with its own worker; a 32-slot command ring and 64 step-indexed result slots (seqlock); the `round(Σdt·f)` accumulator over physics time; coast mode with borrowed steps; `esp_dyno_sweep`; renderSync audio into a float SPSC ring with the frame-sized fill target, drift trim (±0.5 %), underrun hold/decay, priming and fade-in; own AVX2/SSE2 FIR (`ESP_FIR` override), own WAV reader and Kaiser-sinc 44.1→48 kHz resampler for the IRs, synthesizer re-init; builtins `f4_boxer_a`, `v8_cross_a`, `i4_synth`; `esp_get_stats`, `esp_debug_tap`, `esp_poll_log`, `esp_set_crash_dir`; also `esp_set_quality`, `esp_set_worker_options`, `esp_calibrate_inertia`, `esp_render_offline` (planned for M4, done now). `esp_cli` (start, sweep, follow, accounting, pitch, bench `--affinity P\|E --quality`, soak, cosim, all-engines, sanitise-all). | `native/EngineSimPlugin/src/`, `tools/` |
| T1.6 | **ctest suite** (quick, merge tier): `esp_smoke`, `esp_crash_guard`, `esp_abi`, `esp_dsp`, `esp_ring_stress`, `esp_start`, `esp_dyno`, `esp_follow`, `esp_step_accounting` (ts 1), `esp_step_accounting_ts05`, `esp_pitch`, `esp_mr_sanitise`. Heavy (`-Heavy`, machine.lock): `esp_cosim_matrix` (L = 1-4 plus recorded 30/45/60/120 fps Unity frame traces), `esp_bench_ultra_e/p`, `esp_all_engines`, `esp_soak_release`. ASan (`-Asan`, machine.lock): every non-heavy test plus the 10-minute `esp_soak`. | `native/EngineSimPlugin/CMakeLists.txt`, `tests/`, `tests/traces/` |
| T1.6 | **piranha (U3c).** win_flex_bison 2.5.25 fetched with the allowlisted curl into `tools\winflexbison\` (gitignored), run once; scanner and parser committed (no CR) with sizes and SHA-256 in `GENERATED.md`; `piranha` + `es_script` targets with `piranha_path_std.cpp`; `script_loader.cpp` (`esp_load_script`), `mr_sanitise.cpp` (`esp_mr_sanitise`); `esp_all_engines` for the 25 upstream scripts. Release tag `v2.5.25`, zip SHA-256 `8d324b62…6ac135` in `VENDOR.md`. `vendor/LICENSES/bison-NOTICE.txt` (lines 1-31 of the parser, verified by vendor-check) so notices.ps1 can ship the GPL-with-exception entry. | `native/EngineSimPlugin/generated/piranha/`, `vendor/VENDOR.md`, `vendor/LICENSES/`, `tools/ci/vendor-check.ps1` |
| T1.6 | **build-native.ps1**: `-Heavy`, `-Asan` (both take `artifacts\machine.lock` via `Enter-BrMachineLock`), `-Force`, `-Parallel`, `-LockTimeoutSec`; quick ctest skips `heavy|soak`; reports copied to `artifacts\<stamp>\native-reports\` and summarised in the gate JSON. | `tools/ci/build-native.ps1` |
| T1.7 | **`Backroads.EngineSim`.** `Native.cs` (ABI v1 structs, `Pack = 1`, sizes asserted), `EngineSimEngine` (FixedStep: latch controls, collect step k − L with a 1 ms bounded wait, queue step k; main-thread cost ring with p50/p99; tier from `QualityPreset.EngineAudio`), `FixedStepFrame` (EarlyUpdate hook: S, L = clamp(S, 2, 4), the frame's dt), `EngineSimAudio` (OnAudioFilterRead → `esp_read_audio` only, safe detach), `AudioTap`, `EngineSimLog`, `EngineDef` (with `pitchOrders`) and `EngineCatalog` (roster under fictional ids; the 25 upstream scripts only with `UNITY_EDITOR \|\| BACKROADS_TESTING`), `PitchAnalysis` (the PLAN 1.9 rule, same method as the native harness). | `Game/Assets/Backroads/EngineSim/Runtime/` |
| T1.7 | **Engine Bench.** UI Toolkit panel built in code (`Resources/BrPanelSettings`): engine list, tap I = ignition, hold I = starter, W / right trigger = throttle, L = road-like load, D = "Dyno sweep" plot (second, silent engine on a background thread), live rpm, torque, MAP, AFR, throttle plate, worker ms, RTF, P-core share, ring fill/target, underruns, coast events, stale collects, drift trim, main-thread p99, frame p95, tier. `EngineBench.unity` (camera, listener, light; written by `BenchSceneBuilder`); the bench builds itself whenever a scene named `EngineBench` loads, and in any first scene with `-br-bench`. Credits skipped (the integrator owns it). | `.../EngineSim/Runtime/Bench/`, `.../EngineSim/Editor/`, `Scenes/EngineBench.unity` |
| T1.7 | **Autotest `m1.bench.revcycle`** (kind `bench-revcycle`): crank f4_boxer_a, idle, 25 / 60 / 100 % pedal against the bench load, back to idle, 20 s; gates the pitch rule (±3 %) in the last 1.5 s of each hold, 0 underruns over the cycle, clipping < 0.1 %, on what Unity received (an AudioTap after EngineSimAudio); saves `unity_tap.wav`, `esp_tap.wav`, `steps.csv`, `audio_report.json`. | `Testing/Runtime/Scenarios/EngineSim/`, `StreamingAssets/Autotest/m1.bench.revcycle.json` |
| T1.7 | **Tests.** EditMode `Backroads.Tests.EditMode.EngineSim` (11): `StructSizes`, `PluginRefusesWrongStructSize`, `NativeSelftest`, `ImpulseResponsesShipped`, `BuiltinsCrank`, `WorkerCollectsInOrder`, `LagFromSteps`, `PitchRuleSynthetic`, `CatalogNoRealMarks`, `UpstreamScriptsAreDevOnly`, `GeneratedSources.GeneratedHeadersIntact`. PlayMode `Backroads.Tests.PlayMode.EngineSim.FilterChain.ComponentOrderAndListenerVolume`. | `Tests/EditMode/EngineSim/`, `Tests/PlayMode/EngineSim/` |
| T1.8 | **Spike S-AUD-1** (`m1.bench.audiospike`, kind `audio-spike`; Dev-only spike sources) and `docs/spikes/S-AUD-1.md`: remote engines use the `IAudioGenerator` path. | `.../EngineSim/Runtime/Spike/`, `docs/spikes/S-AUD-1.md` |
| Docs | This handoff; `docs/adr/ADR-002-esp-abi-v1.md` (draft for T1.9: frozen ABI, step-lag semantics, coast/borrow incl. its audio consequence, crash folder, `esp_debug_crash_test`). | `docs/` |

## Gates (PLAN 6.1 M1), expected vs measured

All on this laptop, 2026-10-18. Native gates: release build of `ad64e2c` (quick tier re-run green on `5d1902d`); Unity gates: Dev player of `d53605f` unless stated. Reports: `native\build\reports\` and
`artifacts\<stamp>\native-reports\`; autotests in `artifacts\autotest\<id>[-fpsN]\`.

| Gate | Expected | Measured |
|---|---|---|
| f4_boxer_a cranks and idles (`esp_start`) | 700-900 rpm within 3 s | **pass**: starter released at 2.10 s, 849 rpm at 3 s; idle mean 777 rpm (731-850) |
| WOT dyno (`esp_dyno`) | 229 ± 7 N·m at 4,500 ± 500 rpm; 170 ± 6 hp at 6,500 | **pass**: peak **229.2 N·m at 4,500 rpm**; **170.3 hp at 6,500 rpm** (10 points 2,000-6,500) |
| FOLLOW_RPM ramp 1,000 → 4,000 rpm (`esp_follow`) | < 30 rpm RMS | **pass**: 0.0 rpm RMS, max 0.0 (the dyno hold is a speed constraint in engine-sim; it would only lag at the absorber's torque limit). Hold torque 24.9 N·m, MAP 43.3 kPa |
| Step accounting, 600 s (`esp_step_accounting`) | steps = round(Σdt·f) ± 1 at timeScale 1 and 0.5 | **pass**: ts 1: 3,000,042 run = 3,000,042 target (2 coast events, 1,260 coast steps all repaid); ts 0.5: 3,000,000 = 3,000,000 (630 repaid); audio ratio 1.000000 both |
| WAV pitch at 25 / 60 / 100 % (`esp_pitch`) | PLAN 1.9 rule ±3 %, no clipping | **pass**: 9/9 one-second windows; 25 %: 3,317 rpm, ACF 27.65 Hz = cycle frequency; 60 %: 5,106 rpm, peak 170.25 Hz = firing frequency 170.2; 100 %: 5,215 rpm, peak 173.75 = 173.85; clipping 0.0000 % |
| ASan soak, 10 min (`-Asan`, on `d53605f`) | 0 ASan reports, < 1 MB growth, 0 steady-state underruns | **pass**: `build-native.ps1 -Asan` 12/12 in 1,594 s; soak (`i4_synth`, 5 kHz, 2 substeps, 256 taps; deviation 3): **0 ASan reports, 0 underruns, growth 0.047 MB**, RTF 1.77 under ASan. History: the first full run failed (1.44 MB growth, `esp_follow`, `esp_dsp`; fixed in `5d1902d`, deviation 13); of three soaks after the fix, two had 0 underruns and one had a single 29 ms underrun (ASan runs about 10x slower, little real-time headroom) |
| Release soak, 10 min (`esp_soak_release`, f4_boxer_a Ultra: 20 kHz, 8 substeps, 6,134 IR taps) | 0 steady-state underruns, < 1 MB growth | **pass**: **0 underruns** (0 after 10 s, 0 in all), memory growth **0.023 MB**, RTF 2.55; 90 stale collects and 820 coast events from the harness's wall-clock pacing (60 fps, rev cycle 0-70 % against a road load), none audible as underruns |
| Cosim matrix (`esp_cosim_matrix`) | report; L = 1-4 and replayed 30/45/60/120 fps traces pass | **pass** (16.5 s, J_eff 0.4523 kg·m²): all 33 cases of 3 scenarios pass (finite, \|Δv\| ≤ 0.25 m/s, cruise oscillation ≤ 15 rpm amplitude). Launch µ 1.0 at L4: Δv −0.030 m/s, limiter overshoot 182 rpm (286 without the backstop); cruise at L4: track RMS 10.2 rpm, oscillation 3.3 rpm. Recorded traces: 30 fps → 4 steps/frame (L4), 45 → 2-3, 60 → 2, 120 → 1 (L = 2) |
| RTF f4_boxer_a Ultra, E-pinned (`esp_bench_ultra_e`) | ≥ 1.5 | **pass: 2.46** (20.4 µs/step, mask 0x3fc3fc, P-core share 0). P-pinned: 3.07 |
| RTF f4_boxer_a Low on the min-spec proxy | ≥ 1.5 | **pass: 3.15** in the Dev player (`autotest.ps1 -Scenario m1.bench.revcycle -MinSpec`: first 6 E-cores, `-br-quality Low` = 4 substeps and IR ≤ 2,000 taps, 30 fps, 1920x1080); the revcycle passes there too: 4/4 pitch windows, 0 underruns, ring target 56 ms |
| Main-thread cost of engine-sim calls | ≤ 0.10 ms per FixedUpdate at p99 | **pass: 0.0079 ms** at 60 fps, 0.0067 at 30 (also 0.0070 at 45, 0.0072 at 120), **0.0089 ms on the min-spec proxy** (p50 0.0005-0.003). On the proxy it was 2.7 ms p99 until the Low tier ran the worker below normal priority (deviation 12) |
| `m1.bench.revcycle` at 60 fps | pitch ±3 %, 0 underruns, clipping < 0.1 % | **pass**: 4/4 windows (idle 688 rpm ACF order 3.990 of 4; 25 % 3,321 rpm order 1.005 of 1; 60 % 5,114 rpm order 1.005; 100 % 5,217 rpm order 0.503 of 0.5; worst error 0.6 %), **0 underruns**, clipping 0, ring target 32.8 ms, RTF 3.15, 1 coast event |
| `m1.bench.revcycle` at 30 fps | same | **pass**: 4/4 (worst 0.5 %), **0 underruns**, clipping 0, ring target 54.9 ms, frame p95 33.3 ms, 0 coast events. Also 45 fps and 120 fps (`ad64e2c` player): pass, 0 underruns |
| `esp_all_engines` (25 upstream scripts) | report | **25/25 pass** (compile, crank on the starter, stable no-pedal speed, WOT finite). Table below |
| `esp_mr_sanitise` | report | 25/25 load under their fictional id; 22 clean; 3 keep a real word in an es-library reference (`vtec` in `05_honda_vtec`; `chev` in both `chevrolet/` scripts), so they need a sanitised es library before they can ship |
| S-AUD-1 | a remote path with ≥ 12 dB at 20 m and pan | **`IAudioGenerator`: 26.0 dB at 20 m, hard pan, 0 underruns** (S-AUD-1.md) |
| EditMode / PlayMode | green | **pass**: EditMode 89 passed, 2 skipped, 0 failed (lane V 11/11); PlayMode 2 passed (lane V `FilterChain` 1/1) |
| Client build compiles without the Dev-only code | yes | **pass**: `build-player.ps1 -Profile Client -Milestone M1` to a scratch folder, 158 MiB, notices pass (deleted afterwards) |
| `gate.ps1 -Tier merge` | green (Repo.OwnersRespected may flag REQUEST FOR I commits) | **7/8 steps green in 96 s** (`-KeepGoing`): oracle.hash, vendor.check, native.ctest, unity.playmode, build.servercompile, build.dev, autotest.smoke. `unity.editmode`: 90/91; the one failure is `Repo.OwnersRespected`, flagging exactly the three REQUEST FOR I files (`third-party.json`, `BuildCli.cs`, `EditorBuildSettings.asset`) |

### `esp_all_engines`

Real names, so Dev builds only. "Idle" is the no-pedal speed after the start (stable within 15 %), not a tuned idle.

| Script | Cyl | f (Hz) | Idle rpm | WOT max rpm | RTF |
|---|---|---|---|---|---|
| atg-video-1/01_honda_trx520 | 1 | 40,000 | 1,262 | 6,088 | 3.59 |
| atg-video-1/02_kohler_ch750 | 2 | 30,000 | 1,588 | 4,108 | 3.14 |
| atg-video-1/03_harley_davidson_shovelhead | 2 | 35,000 | 816 | 5,536 | 2.20 |
| atg-video-1/04_hayabusa | 4 | 20,000 | 1,406 | 11,582 | 2.38 |
| atg-video-1/05_honda_vtec | 4 | 20,000 | 760 | 9,463 | 2.33 |
| atg-video-1/06_subaru_ej25 | 4 | 20,000 | 844 | 6,630 | 2.30 |
| atg-video-1/07_audi_i5 | 5 | 17,000 | 960 | 6,518 | 2.16 |
| atg-video-1/08_radial_5 | 5 | 12,000 | 834 | 3,532 | 2.28 |
| atg-video-2/01_subaru_ej25_eh | 4 | 20,000 | 775 | 6,869 | 2.38 |
| atg-video-2/02_subaru_ej25_uh | 4 | 20,000 | 774 | 6,856 | 2.37 |
| atg-video-2/03_2jz | 6 | 10,000 | 982 | 6,545 | 2.68 |
| atg-video-2/04_60_degree_v6 | 6 | 10,000 | 729 | 5,643 | 2.57 |
| atg-video-2/05_odd_fire_v6 | 6 | 10,000 | 936 | 5,661 | 2.59 |
| atg-video-2/06_even_fire_v6 | 6 | 10,000 | 919 | 5,643 | 2.63 |
| atg-video-2/07_gm_ls | 8 | 10,000 | 695 | 6,838 | 2.01 |
| atg-video-2/08_ferrari_f136_v8 | 8 | 10,000 | 979 | 9,319 | 1.94 |
| atg-video-2/09_radial_9 | 9 | 7,500 | 601 | 3,521 | 1.93 |
| atg-video-2/10_lfa_v10 | 10 | 6,500 | 899 | 9,532 | 2.18 |
| atg-video-2/11_merlin_v12 | 12 | 7,000 | 2,523 | 3,517 | 2.00 |
| atg-video-2/12_ferrari_412_t2 | 12 | 5,000 | 3,781 | 18,547 | 2.29 |
| audi/i5 | 5 | 10,000 | 6,410 | 7,505 | 3.11 |
| bmw/M52B28 | 6 | 10,000 | 1,038 | 8,033 | 2.72 |
| chevrolet/chev_truck_454 | 8 | 10,000 | 924 | 7,001 | 2.13 |
| chevrolet/engine_03_for_e1 | 8 | 10,000 | 864 | 7,001 | 2.20 |
| kohler/kohler_ch750 | 2 | 10,000 | 1,516 | 5,023 | 7.91 |

Three no-pedal speeds are odd and worth a listen before anyone builds on them: `11_merlin_v12` (2,523), `12_ferrari_412_t2`
(3,781, WOT 18,547 rpm) and `audi/i5` (6,410). They are what the scripts do in this harness (no throttle model
beyond the pedal), not plugin failures.

## Deviations and decisions

1. **Thread contract relaxed** from "main thread only" to "one control thread per handle" (ADR-002), so the bench's
   dyno sweep runs on its own handle on a background thread.
2. **M4 calls implemented now** (`esp_set_quality`, `esp_set_worker_options`, `esp_calibrate_inertia`,
   `esp_render_offline`): they are part of ABI v1, so M4 adds no ABI.
3. **ASan soak uses a light configuration** (`i4_synth`, 5 kHz, 2 gas substeps, 256 FIR taps). Under ASan engine-sim
   runs about 10x slower (f4_boxer_a RTF 0.26), so the full-quality engine cannot keep real time there; the
   full-quality 10-minute soak runs in the release build instead (`esp_soak_release`, heavy).
4. **Frame dt.** `EspControls.frame_dt_s` comes from `FixedStepFrame.FrameDt` (EarlyUpdate). Inside FixedUpdate,
   `Time.unscaledDeltaTime` is the fixed step; with it the 60 fps ring target was 24.7 ms (now 33 ms) and the coast
   threshold fell below one frame. Written into ADR-002 as frozen semantics.
5. **`AudioListener.volume` scales what any filter after the source's generator sees** (measured, PlayMode
   `FilterChain`); `-br-silent` sets 0. `m1.bench.revcycle` plays at -60 dB and divides it out; `m1.bench.audiospike`
   plays at -40 dB and uses ratios. Any other lane's audio autotest needs the same.
6. **Revcycle counts over the cycle only.** The bench creates its engine while the first scene finishes loading
   (frames of 2.7 s, 0.8 s, 0.2 s), which coast mode cannot bridge; that one underrun is reported as
   `underruns_before_cycle`, not gated. The counters are read before the debug-tap WAV is written (that write stalls
   the worker for a few ms; debug only).
7. **The Engine Bench scene holds no client script**: the bench builds itself on scene load, so server builds that
   carry the scene list never meet a missing MonoBehaviour.
8. **FOLLOW_RPM error is 0** by construction (engine-sim's dyno is a speed constraint); the gate passes trivially and
   would only detect the absorber hitting its torque limit.
9. **Frame traces** in `tests/traces/` are Unity frametimes of `m1.bench.revcycle` at 30/45/60/120 fps caps (bench
   over Millbrook), first 10 load frames dropped. The 120 fps trace was recorded with `-ExtraArgs -br-fps-cap 120`
   because `autotest.ps1 -Fps` accepts 0/30/45/60 only.
10. **Spike sources are Dev-only** (`UNITY_EDITOR || BACKROADS_TESTING`); M4 moves `EspAudioGenerator` into the
    remote-engine path.
11. **M0 deviations carried:** 4 `esp_debug_crash_test` is in ADR-002; 5 `ESP_LINK_WHOLE_CORE` is off by default;
    6 `esp_set_crash_dir` added and called from Unity with `%TEMP%\<company>\<product>\Crashes`; 7 the native cache
    stays in the main checkout's `artifacts\native-cache`; 8 a dirty cache entry is not reused after a commit, so the
    `m1` tag build should use `build-native.ps1 -Force`.
12. **Worker priority by tier (changes PLAN 3.4 for the Low tier).** On the min-spec proxy (6 E-cores, all busy) a
    worker at normal or above-normal priority, boosted on wake, preempts Unity's main thread for a whole command:
    main-thread cost p95 2.3 ms, p99 2.7 ms with no collect waits at all (priority 0 and 1 measured alike). Below normal:
    p99 0.009 ms, 0 underruns, RTF 3.1 instead of 3.8. `EngineSimEngine.DefaultWorkerPriority` is therefore -1 on the
    Low tier and +1 (the plan's above normal) on High and Ultra; `-br-esp-priority` overrides it. M4 should revisit this
    with 23 remote engines (for example: below normal everywhere plus wake-boost off).
13. **ASan findings, fixed** (`5d1902d`): the fill controller copied two 256-entry vectors every 2 s; ASan's quarantine
    turned that churn into 1.44 MB of apparent growth (now one scratch buffer: 0.035 and 0.043 MB). `esp_follow` drives
    in lockstep, so under ASan the idle worker coasted and every command only repaid borrowed steps (RMS 2,119 rpm); the
    gate now uses `ESP_CREATE_NO_COAST`. `esp_dsp`'s AVX2-faster-than-scalar check is skipped under ASan.

## For I

Commits on this branch that change files lane V does not own (Repo.OwnersRespected flags them by design):

- `4fdc45b` **REQUEST FOR I: third-party.json** — engine-sim `files` += `Game/Assets/StreamingAssets/EngineSim/**`
  (two IRs copied unchanged from `es/sound-library`); piranha, flex and bison `shipped: true` with versions flex 2.6.4 /
  bison 3.8.2 (win_flex_bison 2.5.25). simple-2d-constraint-solver and piranha already name their submodule paths
  (M0 deviation 3 is done). Without it `Licence.NoticesComplete` fails.
- `6dd1dc5` **REQUEST FOR I: build scene list** — `BuildCli.Scenes` = Millbrook, EngineBench, TestYard (each only when
  its file exists), and `EditorBuildSettings.asset` as `ProjectSetup.Apply` writes it (44 checks, 0 failed). The Esc
  menu's "Engine Bench" entry appears once this is in.

Requests without a commit:

1. `tools\ci\gates.json`: nightly steps `native.heavy` (`build-native.ps1 -Heavy`, perf, hard false, owner V) and
   `native.asan` (`build-native.ps1 -Asan`, owner V); M1 milestone steps `autotest.m1.bench.revcycle` at `-Fps 60` and
   `-Fps 30` (owner V); optionally `m1.bench.audiospike` (spike evidence, not a gate).
2. `autotest.ps1 -Fps`: accept 120 (`ValidateSet(0, 30, 45, 60)` today) so the 120 fps trace can be re-recorded
   without `-ExtraArgs`.
3. `Licence.GeneratedHeadersIntact` (PLAN 6.1 M1) exists as `Backroads.Tests.EditMode.EngineSim.GeneratedSources.GeneratedHeadersIntact`;
   alias or move it into the `Licence` fixture if the exact name matters. `Content.NoRealMarks` is covered for the
   engine roster by `EngineSim.CatalogNoRealMarks` (C# fields and the plugin's engine names); the build-wide scan is I's.
4. Audio autotests of other lanes: mind deviation 5 (`-br-silent` zeroes every `AudioTap`).
5. Before shipping any upstream-derived script (M2+): sanitise the es library references for the 3 engines above
   (`vtec`, `chev`).
6. `IVehicleEngine` (M2) wraps `EngineSimEngine`; nothing for it is in this branch.

## Next (lane V)

- M2: `IVehicleEngine` over `EngineSimEngine` in SLAVED_CRANK; the BRV test track; `v8_bigblock_a`.
- M4: productise `EspAudioGenerator` for remote/AI engines (ring target from its block size, cost of 23 instances,
  Burst AOT in Client builds); EngineBake with `esp_calibrate_inertia` / `esp_render_offline`.

## Resume

```powershell
cd D:\Backroads-Unity-wt\v                         # branch m1/v-esp
git log --oneline -15
tools\ci\vendor-check.ps1                          # 584 files, 5 licence files, 0 differences
tools\ci\build-native.ps1                          # quick ctest (12 tests), installs the DLL
tools\ci\build-native.ps1 -Heavy                   # cosim matrix, bench E/P, all engines, release soak (machine.lock)
tools\ci\build-native.ps1 -Asan                    # ASan build, every non-heavy test + 10-min soak (machine.lock)
tools\ci\unity-tests.ps1 -Platform EditMode
tools\ci\unity-tests.ps1 -Platform PlayMode
tools\ci\build-player.ps1 -Profile Dev -Milestone M1
tools\ci\autotest.ps1 -Scenario m1.bench.revcycle -Fps 60
tools\ci\autotest.ps1 -Scenario m1.bench.revcycle -Fps 30
tools\ci\autotest.ps1 -Scenario m1.bench.audiospike -Fps 60
tools\ci\gate.ps1 -Tier merge
```
