# ADR-002: EngineSimPlugin C ABI v1 is a frozen contract

- **Status:** **accepted and frozen** at the `m1` tag (2026-10-18; M1 milestone gate 19/19, cosim matrix 33/33). Changes need a new ADR.
- **Date:** 2026-10-18
- **Owners:** lane V (vehicles and native plugin); changes need a new ADR, updated tests in the same commit (PLAN §1.10)
  and the integrator's merge.
- **Plan:** PLAN §3.3 (ABI), §3.4 (stepping and lag), §3.5 (coupling modes), §3.6 (audio), §3.8 (tiers), §1.9 [S5]
  (crash folder), §6.1 M1; research 01 §3.5-3.6, §7.1-7.4.

## Context

`EngineSimPlugin.dll` wraps engine-sim (vendored at `85f7c3b`, with scs `e009f4f` and piranha `432f0b1`) for Unity. The
C# side (`Backroads.EngineSim`, `Native.cs`), the Engine Bench, the autotests and from M2 `IVehicleEngine` all call it
through one C ABI. M0 shipped a stub (`esp_version`, `esp_build_info`, `esp_debug_crash_test`); M1 implements v1.
Freezing it now lets M2 (vehicles), M4 (remote engines, EngineBake) and the netcode build on it without re-checking it.

## Decision

`native/EngineSimPlugin/src/EngineSimPlugin.h` at the M1 tag **is** ABI v1. `esp_version()` returns 1. The C# mirror
is `Game/Assets/Backroads/EngineSim/Runtime/Native.cs`. Within v1 only compatible additions are allowed: new
functions, and new fields at the **end** of a struct (callers set `struct_size`; the plugin accepts any
`struct_size >= ` the v1 size and refuses smaller ones with `ESP_ERR_ARG`). Anything else is ABI v2: a new ADR, the
version bumped on both sides, and every test updated in the same commit.

### General rules

- `extern "C"`, `__cdecl`, `int32_t` status returns (`ESP_OK 0`, `ESP_ERR_ARG -1`, `ESP_ERR_COMPILE -2`,
  `ESP_ERR_STATE -3`, `ESP_ERR_TIMEOUT -4`, `ESP_ERR_INTERNAL -5`, `ESP_PENDING 1`). No C++ exception ever escapes
  (every entry point is guarded; an escaped exception becomes `ESP_ERR_INTERNAL` and a log line).
- Strings are UTF-8, NUL-terminated. Error text goes into caller buffers; diagnostics go to an internal queue that the
  caller drains with `esp_poll_log`. **The plugin never calls back into managed code from its own threads.**
- Every struct is `#pragma pack(1)` with no padding and starts with `uint32_t struct_size`. Sizes, asserted on both
  sides (`static_assert` in `esp_api.cpp`, `EngineSim.StructSizes` in EditMode):

| Struct | Bytes | Purpose |
|---|---|---|
| `EspCreateDesc` | 40 | audio rate, ring and first target, DSP buffer, frequency (0 = the engine's own), gas substeps, worker on/off, IR cap, flags |
| `EspEngineInfo` | 272 | name (fictional id for builtins), geometry, redline, starter, crank inertia, script gearbox, rev limit, IR taps, source kind |
| `EspControls` | 32 | throttle, clutch, ignition, starter, sim_speed, frame_dt_s, frame_index (latched, copied into every command) |
| `EspDrivelineIn` | 160 | mode and per-mode inputs (FREE load, FOLLOW_RPM target, SLAVED_CRANK PD parameters; PROXY fields reserved) |
| `EspStepOut` | 152 | per-step result: steps, step_index, sim time, rpm, crank speed, indicated torque, dT/dw, torques, MAP, flow, throttle plate, AFR, fuel, CPU ms, ring fill, underruns, limiter hint, flags |
| `EspAudioParams` | 28 | volume (default 0.5), convolution, HF gain, noises, leveler target |
| `EspStats` | 208 | accounting (commands, steps run / target / borrowed, coast), lag (stale steps, waits), CPU (RTF, P-core share), audio (underruns, ring fill and target, produced / consumed / dropped, clipped, drift trim, frame p95, worker frame ms) |
| `EspWorkerOptions` | 24 | priority, power-throttling off, coast_max_ms, affinity |

### Functions (v1)

| Group | Calls |
|---|---|
| Library | `esp_version`, `esp_build_info` (one-line JSON: abi, git, sourceHash, piranha, asan, crt, fp, fir, builtins, vendor commits), `esp_set_crash_dir`, `esp_poll_log`, `esp_debug_crash_test` |
| Engines | `esp_create(desc, es_library_dir, temp_dir)`, `esp_destroy`, `esp_load_builtin` (`f4_boxer_a`, `v8_cross_a`, `i4_synth`), `esp_load_script` (piranha `.mr`, builds with `ESP_WITH_PIRANHA`), `esp_get_info` |
| Controls | `esp_set_controls`, `esp_set_audio_params`, `esp_set_quality(fluid_steps, fir_max_taps)`, `esp_set_worker_options` |
| Stepping | `esp_step` (synchronous), `esp_step_begin(e, step_index, dt, in, crank_samples, n)`, `esp_step_end(e, step_index, timeout_us, out)` |
| Audio | `esp_read_audio(e, dst, frames, channels)` (audio thread) |
| Diagnostics | `esp_get_stats`, `esp_debug_tap` |
| Offline | `esp_dyno_sweep`, `esp_calibrate_inertia`, `esp_render_offline`, `esp_mr_sanitise` |

`esp_set_quality`, `esp_set_worker_options`, `esp_calibrate_inertia` and `esp_render_offline` were planned for M4
(PLAN 6.1 M1 [S14]); they are implemented and covered by tests in v1 already, so M4 adds no ABI.

Coupling modes: `ESP_MODE_FREE` (0), `ESP_MODE_SLAVED_CRANK` (3) and `ESP_MODE_FOLLOW_RPM` (4) are implemented.
`ESP_MODE_PROXY` (1) and `ESP_MODE_KINEMATIC` (2) keep their numbers and fields but return `ESP_ERR_ARG` in v1
(PROXY is M13 material, PLAN 3.5).

### Thread contract

- Calls on one handle come from **one control thread at a time** (Unity's main thread for live engines). This relaxes
  research 01's "main thread only": a background thread may own a *separate* handle (the Engine Bench's dyno-sweep
  engine does).
- `esp_read_audio` is the only call from the audio thread; it is lock-free and allocation-free. It is safe from
  `OnAudioFilterRead` and from a Burst-compiled `IAudioGenerator` realtime part (S-AUD-1).
- `esp_create`, `esp_load_builtin` and `esp_load_script` may run on any thread; script compiles share one global mutex.
- `esp_poll_log`, `esp_build_info`, `esp_version`, `esp_set_crash_dir`: any thread.
- `esp_destroy` stops the worker and frees the ring: the caller stops audio first (`EngineSimAudio.Detach` waits for
  a running callback to leave the native call).

### Time, steps and the step lag (frozen semantics)

1. **Accounting.** `dt` is physics (game) time. After the command for physics step k the engine has run
   `round(Σdt · f)` steps in total (f = the engine's simulation frequency), minus borrowed coast steps not yet repaid.
   `dt > 0.25 s` is capped (`ESP_OUT_CAPPED`). Gate: `esp_step_accounting` (600 s, |error| <= 1 step) at timeScale 1
   and 0.5.
2. **timeScale.** `EspControls.sim_speed` never changes the step count; it scales only the synthesizer's input rate,
   to `f · sim_speed · (1 + ε)` with the drift trim ε within ±0.5 %, so slow motion keeps the audio real-time.
3. **Step-indexed results.** Each command carries the caller's monotonic physics step index. `esp_step_end(k)`
   returns step k's result, waiting at most `timeout_us`, or `ESP_PENDING`; results stay for the last 64 steps
   (`ESP_RESULT_SLOTS`), older ones return `ESP_ERR_ARG`. Commands are queued in a 32-slot ring
   (`ESP_COMMAND_SLOTS`) and never skipped or dropped; `esp_step_begin` returns `ESP_ERR_TIMEOUT` only if the ring
   stays full for 50 ms.
4. **The lag L is the caller's.** At physics step k Unity collects step k − L with
   `L = clamp(S, 2, 4)`, S = the fixed steps Unity runs this frame, computed before them in EarlyUpdate
   (`FixedStepFrame`); L changes only at frame boundaries. The collect waits at most 1,000 µs
   (`EngineSimEngine.CollectWaitUs`); a missing result reuses the previous one (a stale step, counted). The
   `dT_dw` estimate in every result lets the caller feed the lag forward (PLAN 3.4); `esp_cosim_matrix` measures
   L = 1-4 and recorded 30/45/60/120 fps frame traces against an unlagged reference.
5. **Frames.** `EspControls.frame_dt_s` is the duration of the frame that issues the commands and `frame_index`
   groups commands into frames. Unity must take `frame_dt_s` from the frame (EarlyUpdate), **not** from
   `Time.unscaledDeltaTime` inside FixedUpdate, which is the fixed step (found in M1; it halved the 60 fps ring
   target).
6. **Coast mode with borrowed steps.** When no command has arrived for `max(2 · step wall time, 1.5 · frame_dt_s,
   10 ms)`, the worker keeps stepping with the last command, up to `coast_max_ms` (default 300 ms), so the sound
   continues through a hitch. Coast steps are borrowed: the next commands run `max(0, target − executed)` steps until
   engine time is back on physics time (`ESP_OUT_AFTER_COAST`, `ESP_OUT_REPAID`); the PD integral resets and the event
   is counted. **Audio consequence:** repayment means the engine produces fewer samples right after a long hitch, so
   a hitch longer than the ring target plus `coast_max_ms` can still underrun once; the counter shows it. A
   **lockstep** caller (one that waits for each result before sending the next command, as tools and tests do)
   idles the worker by construction and would coast between every pair of commands when the worker is slow (seen
   under ASan); such callers create the engine with `ESP_CREATE_NO_COAST`. Unity never runs in lockstep (it collects
   step k − L).
7. **Worker scheduling (Unity's choice, not ABI).** `esp_set_worker_options` sets priority, power throttling and
   affinity. Measured in M1 on the min-spec proxy (6 E-cores, all busy): a worker at normal or above-normal priority,
   boosted when its event is set, preempts Unity's main thread for a whole command (main-thread cost p99 2.7 ms);
   below normal it costs 0.009 ms p99 with 0 underruns. Unity uses -1 on the Low tier and +1 otherwise
   (`EngineSimEngine.DefaultWorkerPriority`); M4 revisits this with many engines.

### Audio (renderSync, PLAN 3.6)

- The worker renders audio in step with the simulation (upstream's per-sample chain plus the plugin's own
  AVX2/SSE2 FIR, IR capped by `fir_max_taps`, 44.1 → 48 kHz sinc resampling of the IRs) into a float SPSC ring
  (16,384 frames by default).
- Fill target = p95 frame time + the worker's time per frame + one DSP buffer + 5 ms, recomputed every 2 s (raised
  at once, lowered after two agreeing periods). Drift trim keeps the fill at the target (±0.5 % input rate).
- Underrun: `esp_read_audio` holds the last sample and decays it over 2 ms, and counts one event; events count only
  while the producer is active. The ring primes and fades in after creation.
- `esp_debug_tap` (Dev tool) tees the produced audio into a 16-bit WAV, written on the worker when full or stopped:
  a debug-only stall of a few ms, never used in play.

### Crash folder and test hook (carried from M0, deviations 4 and 6)

- `esp_set_crash_dir(dir)`: where the crash guard writes minidumps; default `%TEMP%\BRDev\Backroads\Crashes`, env
  override `BR_CRASH_DIR`. Unity calls it once with `%TEMP%\<companyName>\<productName>\Crashes`
  (`EngineSimEngine.SetCrashFolder`, from the Engine Bench and later the vehicle code) so a GameInfo rename (U18)
  needs no plugin change.
- `esp_debug_crash_test(magic)`: with `0x43524153` ("CRAS") it faults on a plugin thread under the crash guard (a
  minidump, then the process crashes); any other value is `ESP_ERR_ARG`. Used by `m0.crash.recover`
  (`-br-crash-test`) and the `esp_crash_guard` ctest. It stays in v1.

### Build facts that belong to the contract

- MSVC, `/MT` (static CRT: the DLL imports only `KERNEL32` and `dbghelp`), `/O2 /fp:fast`, `NDEBUG`, C++17, x64.
- `ESP_WITH_PIRANHA=ON` (U3c approved): the generated scanner and parser in `generated/piranha/` are compiled in;
  `esp_load_script` and `esp_mr_sanitise` work. Without it they return `ESP_ERR_STATE`.
- `ESP_LINK_WHOLE_CORE` is off (M0 deviation 5): the linker keeps what the plugin calls.
- `EngineSimPlugin.version.json` beside the DLL repeats `abi`, `git`, `sourceHash` and `piranha` from
  `esp_build_info`; `EngineSim.NativeSelftest` checks they match.

## Consequences

- C# and native agree byte for byte; `EngineSim.StructSizes` and `esp_abi` fail on any drift.
- M2's `IVehicleEngine` wraps `EngineSimEngine` (SLAVED_CRANK, PLAN 3.5) without new calls; M4 remote engines use
  FOLLOW_RPM and `esp_read_audio` from an `IAudioGenerator` (S-AUD-1) without new calls.
- PROXY and KINEMATIC need ABI v1 additions (new trailing fields are allowed) or v2 when they arrive.

## Tests that hold the contract

ctest (`build-native.ps1`): `esp_smoke`, `esp_abi`, `esp_crash_guard`, `esp_ring_stress`, `esp_dsp`, `esp_start`,
`esp_dyno`, `esp_follow`, `esp_step_accounting`, `esp_step_accounting_ts05`, `esp_pitch`, `esp_mr_sanitise`; heavy:
`esp_cosim_matrix`, `esp_bench_ultra_e/p`, `esp_all_engines`, `esp_soak_release`; ASan: `esp_soak`. Unity:
`Backroads.Tests.EditMode.EngineSim.*`, `Backroads.Tests.PlayMode.EngineSim.FilterChain.*`, autotests
`m1.bench.revcycle` and `m1.bench.audiospike`.

## M2 addendum (lane V, card T2.4; compatible, no ABI change; **accepted by the integrator at the M2 merge, 2026-10-19**)

Nothing in the header changed but one comment: every struct, size, function and status code is as frozen above, and
`esp_version()` is still 1. Three behaviours were refined while fixing the M1 release-soak underrun; they are
compatible extensions in the sense of "Decision" above (a value that was unused now means something; internal timing):

1. **Worker audio class.** `EspWorkerOptions.priority = 3` (before: clamped to 2, HIGHEST; no caller used it) puts the
   worker in the audio class: it joins MMCSS "Pro Audio" on itself (`avrt.dll` is loaded at run time, so the DLL still
   imports only KERNEL32 and dbghelp), or runs at `THREAD_PRIORITY_TIME_CRITICAL` if MMCSS fails; any other value leaves
   it. Unity uses 3 on the High and Ultra tiers and keeps -1 on Low (item 7 above). Why: in an M2 release soak every
   thread at or below above-normal priority stopped for about 50 ms while the time-critical audio consumer kept reading,
   and the 33 ms ring ran dry (M2-handoff-VS). Test: `esp_abi` (3 and back to 0).
2. **Fill target cadence (Audio, second bullet).** The "p95 frame time" of the fill target is now the p95 of each
   frame's longer of `frame_dt_s` and the kick-to-kick time to the next frame that sends commands. Above 120 fps (or in
   a fast editor loop) most frames send no command and audio arrives once per fixed step; the target must cover that
   cadence (measured: target 13.1 ms with a push every 8.3 ms and a 21 ms DSP buffer gave an underrun).
3. **Low-water coast (item 6).** Besides the hitch threshold, the worker also coasts when the ring would run dry before
   the next command: the fill has fallen to one DSP buffer + one coast batch of worker time + 3 ms; not while commands
   are repaying borrowed steps with no steps of their own (that only borrowed again). Timed worker waits use a
   high-resolution waitable timer. In FREE and SLAVED_CRANK borrowing and repayment are unchanged
   (`esp_step_accounting` still exact).
4. **FOLLOW_RPM repays from spare audio (items 1 and 6).** In FOLLOW_RPM with audio, a command repays borrowed steps
   only from audio the ring holds above its target and keeps the rest as debt (`EspStats.steps_borrowed`), so engine
   time may run ahead of physics time in this mode. Why: after a hitch Unity advances physics by at most
   `maximumDeltaTime` (33 ms), so most coasted steps are never earned back, and repaying them at once ran commands with
   no steps: about 65 ms of silence after a 100 ms frame (`m2.vehicles.drive`). FOLLOW_RPM is sound only (the vehicle
   model drives), so the debt costs nothing. Test: `esp_follow_hitch` (heavy): 0 underruns through 100 and 250 ms
   main-thread stalls with Unity's clock; `--repay-all` (`ESP_FOLLOW_REPAY_ALL=1`) shows the M1 rule for comparison.
5. **The audio thread wakes the worker (Thread contract, Audio).** `esp_read_audio` stays lock-free and
   allocation-free; when the fill falls to the worker's low-water mark it now calls `SetEvent` on the worker's wake
   event once per low episode (a non-blocking kernel call), so the low-water coast follows the audio clock and not
   only the worker's own timer. Why: in M2 release soaks the worker and the frame loop sometimes woke 45-50 ms after
   their timers while the audio thread kept running.
6. **What Unity passes as the DSP buffer (Unity's choice).** `EspCreateDesc.audio_dsp_buffer_frames` is Unity's whole
   output buffer (block x buffers, 256 x 4 = 1,024 frames at Best latency), not one block, so the fill target holds at
   its lowest what Unity's mixer can drain at once after a stall: about 49 ms at 60 fps instead of 33 (PLAN 3.6's "one
   DSP buffer"). Why: in M2 release soaks whole-machine stalls of about 50 ms came every 10-20 minutes on this shared
   laptop; afterwards the output refills its whole buffer at once while the ring held 19-37 ms. The soak harness now
   models that output (`DeviceConsumer`: it refills at most 4 blocks after a stall and counts the rest as device gaps;
   M1's harness read every missed block from the ring, up to 100 ms, which no audio device does).
