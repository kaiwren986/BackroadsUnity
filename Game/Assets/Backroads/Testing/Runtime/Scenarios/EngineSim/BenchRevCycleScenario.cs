using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Backroads.EngineSim;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m1.bench.revcycle (PLAN 6.1 M1 headline): the Engine Bench runs a scripted 20 s rev cycle on f4_boxer_a: crank with
    /// the starter, idle, then 25 / 60 / 100 % pedal against the bench's road-like load (steady rpm, like research 02),
    /// then back to idle. Gates: the pitch rule of PLAN 1.9 within +-3 % in every steady window, 0 underruns, clipping
    /// below 0.1 %. Measured on what Unity received (an AudioTap after EngineSimAudio); the plugin's own tap
    /// (esp_debug_tap) is saved beside it. Also reports main-thread cost per FixedUpdate (p99), real-time factor, stale
    /// collects and coast events. Writes audio_report.json, unity_tap.wav, esp_tap.wav and steps.csv.
    /// </summary>
    public sealed class BenchRevCycleScenario : IAutotestScenario
    {
        public string Kind => "bench-revcycle";

        [Serializable]
        sealed class Params
        {
            public string engine = "f4_boxer_a";
            public float cycleSeconds = 20f;
            public bool load = true;
            public float pitchTolerance = 0.03f;
            public float maxClipFraction = 0.001f;
            public int maxUnderruns = 0;
            public float mainThreadP99MsBudget = 0.10f;
        }

        [Serializable]
        sealed class Window
        {
            public string name;
            public float fromS, toS, pedal;
            public double rpm, cycleHz, acfHz, acfOrder, peakHz, peakOrder, rmsDb;
            public bool pass;
        }

        [Serializable]
        sealed class Report
        {
            public string engine;
            public int sampleRate, channels, dspBufferFrames;
            public float fps, cycleSeconds;
            public double starterReleaseS, clipFraction, mainThreadP99Ms, mainThreadP50Ms, rtf, workerMs;
            public int underruns, underrunFrames, staleSteps, coastEvents, tapCallbacks, tapBlockFrames;
            public long framesProduced, framesConsumed, framesDropped;
            public double ringTargetMs, frameP95Ms;
            public List<Window> windows = new List<Window>();
            public List<string> failures = new List<string>();
        }

        // pedal schedule: (start, end, pedal); steady windows are the last 1.5 s of each hold
        static readonly (string name, float from, float to, float pedal)[] s_Holds =
        {
            ("idle", 3.0f, 5.0f, 0f), ("25%", 5.0f, 9.0f, 0.25f), ("60%", 9.0f, 13.0f, 0.60f), ("100%", 13.0f, 17.0f, 1.0f),
        };

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            var bench = EngineBench.Current != null ? EngineBench.Current : EngineBench.Create();
            yield return null;  // Start: panel and the default engine
            yield return null;
            var def = EngineCatalog.Find(p.engine);
            if (def == null) { ctx.Fail("no engine " + p.engine); yield break; }
            if (bench.Def != def && !bench.Select(def)) { ctx.Fail("engine did not load: " + bench.LastError); yield break; }
            if (bench.Engine == null) { ctx.Fail("engine did not load: " + bench.LastError); yield break; }
            var engine = bench.Engine;
            bench.ScriptControl = true;
            bench.LoadOn = p.load;
            bench.ScriptIgnition = true;
            bench.ScriptStarter = true;
            bench.ScriptThrottle = 0f;
            int rate = AudioSettings.outputSampleRate;
            AudioSettings.GetDSPBufferSize(out int dsp, out _);

            var report = new Report { engine = def.id, sampleRate = rate, dspBufferFrames = dsp, cycleSeconds = p.cycleSeconds };
            var tap = bench.Tap;
            tap.Clear();
            tap.Recording = true;
            engine.DebugTap(Path.Combine(ctx.OutDir, "esp_tap.wav"), p.cycleSeconds + 1.0);
            engine.ResetCostStats();
            var captured = new List<float>(rate * 2 * (int)(p.cycleSeconds + 2));
            var rpmT = new List<(float t, double rpm)>(8192);
            float starterRpm = (float)(engine.Info.starterSpeedRadS * 9.5492966);
            float t0 = Time.unscaledTime, released = -1f, above = 0f;
            long tapStartSamples = 0;
            bool tapStarted = false;
            int frames = 0;

            while (true)
            {
                float t = Time.unscaledTime - t0;
                if (t >= p.cycleSeconds) break;
                var last = engine.Last;
                rpmT.Add((t, last.rpm));
                if (released < 0)
                {
                    above = last.rpm > 1.3f * starterRpm ? above + Time.unscaledDeltaTime : 0f;
                    if (above >= 0.25f || t > 3.0f) { released = t; bench.ScriptStarter = false; }
                }
                float pedal = 0f;
                foreach (var h in s_Holds)
                    if (t >= h.from && t < h.to)
                        pedal = h.pedal == 0f ? 0f : Mathf.Min(h.pedal, (t - h.from) / 0.5f * h.pedal + 0.0001f);
                bench.ScriptThrottle = pedal;
                int before = captured.Count;
                tap.Drain(captured);
                if (!tapStarted && captured.Count > 0) { tapStarted = true; tapStartSamples = before; }
                frames++;
                yield return null;
            }
            bench.ScriptThrottle = 0f;
            tap.Drain(captured);
            tap.Recording = false;
            engine.DebugTap(null, 0);  // flush the plugin's tap at the next render
            float elapsed = Time.unscaledTime - t0;
            report.fps = frames / Mathf.Max(0.001f, elapsed);
            report.starterReleaseS = released;
            int ch = Mathf.Max(1, tap.Channels);
            report.channels = ch;
            report.tapCallbacks = (int)tap.Callbacks;
            report.tapBlockFrames = tap.LastBlockFrames;

            // what Unity received: channel 0 of the tap; sample index from capture time (the tap starts with t0)
            int mono = captured.Count / ch;
            var x = new float[mono];
            for (int i = 0; i < mono; i++) x[i] = captured[i * ch];
            AudioTap.SaveWav(Path.Combine(ctx.OutDir, "unity_tap.wav"), captured.ToArray(), captured.Count, ch, rate);
            ctx.Evidence(Path.Combine(ctx.OutDir, "unity_tap.wav"));
            ctx.Evidence(Path.Combine(ctx.OutDir, "esp_tap.wav"));
            report.clipFraction = PitchAnalysis.ClipFraction(x, x.Length);

            foreach (var h in s_Holds)
            {
                var w = new Window { name = h.name, fromS = h.to - 1.5f, toS = h.to, pedal = h.pedal };
                double sum = 0;
                int n = 0;
                foreach (var (t, rpm) in rpmT)
                    if (t >= w.fromS && t < w.toS) { sum += rpm; n++; }
                w.rpm = n > 0 ? sum / n : 0;
                int i0 = Mathf.Clamp((int)(w.fromS * rate), 0, x.Length), i1 = Mathf.Clamp((int)(w.toS * rate), 0, x.Length);
                if (i1 - i0 > 2048 && w.rpm > 100)
                {
                    var r = PitchAnalysis.Check(x, i0, i1 - i0, 1, rate, w.rpm, def.pitchOrders, p.pitchTolerance);
                    w.cycleHz = r.cycleHz; w.acfHz = r.acfHz; w.acfOrder = r.acfOrder; w.peakHz = r.peakHz; w.peakOrder = r.peakOrder;
                    w.pass = r.Pass;
                    w.rmsDb = PitchAnalysis.RmsDb(x, i0, i1 - i0, 1);
                    ctx.Note($"pitch {h.name}: {r}");
                }
                report.windows.Add(w);
                ctx.Metric($"pitch_{h.name.Replace("%", "pct")}_rpm", w.rpm);
                ctx.Metric($"pitch_{h.name.Replace("%", "pct")}_pass", w.pass ? 1 : 0);
                if (!w.pass) report.failures.Add($"pitch {h.name} at {w.rpm:F0} rpm: acf {w.acfHz:F1} Hz (order {w.acfOrder:F2}), peak {w.peakHz:F1} Hz (order {w.peakOrder:F2})");
            }

            var s = engine.Stats();
            report.underruns = s.audioUnderruns;
            report.underrunFrames = s.audioUnderrunFrames;
            report.staleSteps = s.staleSteps;
            report.coastEvents = s.coastEvents;
            report.rtf = s.rtf;
            report.workerMs = s.workerMsEma;
            report.framesProduced = s.audioFramesProduced;
            report.framesConsumed = s.audioFramesConsumed;
            report.framesDropped = s.audioFramesDropped;
            report.ringTargetMs = s.ringTargetFrames * 1000.0 / rate;
            report.frameP95Ms = s.frameP95Ms;
            report.mainThreadP99Ms = engine.CostPercentileMs(99);
            report.mainThreadP50Ms = engine.CostPercentileMs(50);

            if (released < 0 || released > 3.0f) report.failures.Add($"engine did not start within 3 s (starter released at {released:F2} s)");
            if (report.underruns > p.maxUnderruns) report.failures.Add($"{report.underruns} underruns ({report.underrunFrames} frames)");
            if (report.clipFraction >= p.maxClipFraction) report.failures.Add($"clipping {report.clipFraction * 100:F3} % >= {p.maxClipFraction * 100:F2} %");
            if (report.tapCallbacks == 0) report.failures.Add("the AudioTap saw no audio callbacks");
            if (report.mainThreadP99Ms > p.mainThreadP99MsBudget)
                ctx.Note($"main-thread cost p99 {report.mainThreadP99Ms:F3} ms per FixedUpdate is over the {p.mainThreadP99MsBudget:F2} ms lane budget");

            ctx.Metric("fps", report.fps);
            ctx.Metric("underruns", report.underruns);
            ctx.Metric("underrun_frames", report.underrunFrames);
            ctx.Metric("clip_fraction", report.clipFraction);
            ctx.Metric("main_thread_p99_ms", report.mainThreadP99Ms);
            ctx.Metric("main_thread_p50_ms", report.mainThreadP50Ms);
            ctx.Metric("rtf", report.rtf);
            ctx.Metric("worker_ms", report.workerMs);
            ctx.Metric("stale_steps", report.staleSteps);
            ctx.Metric("coast_events", report.coastEvents);
            ctx.Metric("ring_target_ms", report.ringTargetMs);
            ctx.Metric("frame_p95_ms", report.frameP95Ms);
            ctx.Metric("starter_release_s", report.starterReleaseS);
            ctx.Metric("dsp_buffer_frames", dsp);

            var rows = new StringBuilder("t,rpm\n");
            foreach (var (t, rpm) in rpmT) rows.Append(t.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(rpm.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllText(Path.Combine(ctx.OutDir, "steps.csv"), rows.ToString());
            File.WriteAllText(Path.Combine(ctx.OutDir, "audio_report.json"), JsonUtility.ToJson(report, true));
            ctx.Evidence(Path.Combine(ctx.OutDir, "audio_report.json"));
            yield return ctx.Screenshot("bench_revcycle");
            foreach (var f in report.failures) ctx.Fail(f);
            bench.ScriptControl = false;
        }
    }
}
