# Spike S-AUD-1: 3D audio for generated engine audio (M1, lane V)

**Question (PLAN 3.6 item 4, T1.8).** Remote and AI cars need their engine-sim audio panned and attenuated by distance.
Which way of handing generated audio to Unity 6000.3.20f1 does that: (a) `OnAudioFilterRead` on a clip-less
`AudioSource` with `spatialBlend` 0 and 1, (b) a 6.3 `IAudioGenerator` whose realtime part is Burst code calling
`esp_read_audio` through `[DllImport]` (and does `ChannelBuffer` expose a pointer?), (c) a looping DC clip multiplied
by the filter, (d) a streamed `AudioClip` with a `PCMReaderCallback`? And how many underruns does each give at Best
latency?

**Answer.** Remote and AI engines use **(b), the `IAudioGenerator`** (`EspAudioGenerator`): it attenuates by 26.0 dB
at 20 m, pans hard left and right, ran as Burst code, and played a running engine for 5 s with **0 underruns**. The
DC clip x filter trick (c) measured the same and is the fallback. The local car keeps `EngineSimAudio`
(`OnAudioFilterRead`, `spatialBlend` 0), which the Engine Bench uses.

## How it was measured

- Autotest `m1.bench.audiospike` (kind `audio-spike`, `Testing/Runtime/Scenarios/EngineSim/AudioSpikeScenario.cs`)
  in the M1 Dev player, windowed at 2560x1440, 60 fps cap, on this laptop (Windows 11, Unity 6000.3.20f1). Output:
  48 kHz stereo, DSP buffer 256 frames x 4 (Best latency).
- The scenario switches off the scene's listeners and puts its own `AudioListener` at (0, 500, 0) facing +z, with an
  `AudioTap` on the same GameObject: the tap sees the final mix, after spatialisation.
- Each path plays a 440 Hz tone (amplitude 0.1) from a source at 1 m and 20 m in front of the listener and 5 m to its
  left (-x) and right (+x). Sources: logarithmic roll-off, min distance 1 m, max 500 m, Doppler 0, spread 0. The tap
  records 0.6 s after 0.4 s of settling; RMS per channel.
- `-br-silent` (autotest default) sets `AudioListener.volume = 0`, which **zeroes what any filter after the source's
  generator sees** (see "Side finding" below). The scenario plays at listener volume 0.01 (-40 dB, barely audible);
  every result is a ratio, so the level cancels.
- Then each path plays `f4_boxer_a` (cranked, then throttle 0.12, about 3,100 rpm) 2 m in front for 5 s; the plugin
  counts underruns (`esp_get_stats`, deltas over the 5 s).

Run: `tools\ci\build-player.ps1 -Profile Dev -Milestone M1`, then
`tools\ci\autotest.ps1 -Scenario m1.bench.audiospike -Fps 60`. Evidence: `artifacts\autotest\m1.bench.audiospike-fps60\`
(`spike_report.json`, `result.json`, listener WAVs per path).

## Results (2026-10-18, run at 19:54)

Tone, levels on the listener (dBFS at listener volume 0.01; -240 = digital silence):

| Path | 1 m | 20 m | Attenuation at 20 m | 5 m left: L / R | 5 m right: L / R | Pans |
|---|---|---|---|---|---|---|
| (a) filter, `spatialBlend` 0 | -63.0 | -63.0 | 0.0 dB | -63.0 / -63.0 | -63.0 / -63.0 | no (2D, as intended) |
| (a) filter, `spatialBlend` 1 | -63.0 | -89.0 | **26.0 dB** | -77.0 / -77.0 | -77.0 / -77.0 | **no** |
| (c) DC clip x filter | -66.0 | -92.0 | **26.0 dB** | -77.0 / -240 | -240 / -77.0 | **yes** |
| (b) `IAudioGenerator` (Burst) | -66.0 | -92.0 | **26.0 dB** | -77.0 / -240 | -240 / -77.0 | **yes** |
| (d) `PCMReaderCallback` stream | -66.0 | -92.0 | **26.0 dB** | -77.0 / -240 | -240 / -77.0 | **yes** |

26.0 dB is exactly the logarithmic roll-off, 20 log10(20 m / 1 m) = 26.02 dB; the gate is >= 12 dB. The 3 dB lower
level of the panned paths at 1 m is the centre of Unity's pan law.

Running engine, 5 s at Best latency (256-frame DSP buffer):

| Path | Underruns | Frames consumed | Callback block | Ring target | Notes |
|---|---|---|---|---|---|
| (a) filter, 2D (the local car's `EngineSimAudio` path) | 0 | 239,872 | 256 | 1,574 frames (33 ms) | |
| (c) DC clip x filter | 0 | 239,872 | 256 | 1,558 | |
| (b) `IAudioGenerator` | 0 | 239,872 | 256 | 1,554 | realtime part ran as **Burst** code (a `[BurstDiscard]` counter stayed 0) |
| (d) `PCMReaderCallback` | **12** (33,792 frames) | 196,608 | **4,096** | 1,556 | reads 85 ms at a time; the ring overflows between reads (34,756 frames dropped) and underruns in them |

## Findings per question

- **(a)** In 6000.3.20f1 a clip-less `AudioSource` with `spatialBlend = 1` and an `OnAudioFilterRead` generator **is
  attenuated by distance but not panned** (left and right equal at 90 degrees). The old issue-tracker reports ("no 3D
  at all") are half out of date. Not usable for remote cars.
- **(b)** `AudioSource.generator = EspAudioGenerator` works. `IAudioGenerator.CreateInstance` allocates a generator
  through `ControlContext.AllocateGenerator(realtime, control, ...)`; the realtime struct is `[BurstCompile]` and calls
  `esp_read_audio(IntPtr, float*, int, int)` through `[DllImport]` (Burst compiled it in the Dev player).
  **`ChannelBuffer` exposes no pointer**: only an indexer `[channel, frame]`, `frameCount`, `channelCount`, `Clear()`
  and a `Span<float>` constructor (checked by reflection on `UnityEngine.AudioModule`). So the realtime part reads into
  a `stackalloc` buffer and copies sample by sample (256 mono samples per block). Output is configured as mono, so the
  source pans and attenuates it like a mono clip; the mixer routing of the AudioSource applies.
- **(c)** The DC clip trick works: a looping clip of 1.0 carries Unity's distance gain and pan into the filter's input,
  and the filter multiplies its signal in. Same numbers as (b); costs one managed `OnAudioFilterRead` per remote car and
  a DC clip per source.
- **(d)** A streamed clip pans and attenuates, but Unity pulls 4,096 frames per callback (about 85 ms) from its own
  thread, which the frame-sized ring (PLAN 3.6) does not suit: 12 underruns in 5 s. Rejected.

## Side finding: `AudioListener.volume` and filters

`Backroads.Tests.PlayMode.EngineSim.FilterChain.ComponentOrderAndListenerVolume` (batchmode PlayMode) measured that
filters on one `AudioSource` run in component order, and that **`AudioListener.volume` scales the samples a later
filter sees**: 0.25 written by the first filter arrives at the second as 0.0025 at volume 0.01 and as 0 at volume 0.
So with `-br-silent` every `AudioTap` hears silence. `m1.bench.revcycle` therefore plays at -60 dB and divides the
volume out of its capture; the spike plays at -40 dB and uses ratios only.

## What M4 does with this

- Move `EspAudioGenerator` out of `Runtime/Spike/` (Dev-only today) into the remote-engine path (R1 sources), with the
  ring target fed by the generator's block size; keep the DC clip variant as the fallback behind a setting.
- Measure the cost of 23 generator instances (one per remote car, PLAN 3.8) and confirm Burst AOT compiles the
  realtime part in Client builds (`ProjectSetup` turns Burst AOT on).
- Re-run `m1.bench.audiospike` and `FilterChain` after every editor update; either changing its answer reopens this.
