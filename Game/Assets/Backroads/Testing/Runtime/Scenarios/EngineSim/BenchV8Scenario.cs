using System;
using System.Collections;
using System.IO;
using Backroads.EngineSim;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// m2.bench.v8 (card T2.4 "the truck engine sounds like a big-block V8 on the Engine Bench"): the Engine Bench with the
    /// truck's v8_bigblock_a: crank it on the starter, idle, rev it against the bench load, run the dyno sweep (screenshot
    /// bench_v8_dyno), then the truck demo through IVehicleEngine (screenshot bench_v8_demo mid-pull). Records what Unity
    /// received (the source-fed AudioTap) as bench_v8.wav and demo_v8.wav for listening. Gates: it starts within 3 s, idles
    /// inside the engine's declared range, the pitch rule of PLAN 1.9 (within 3 %) holds over the last 1.5 s of the rev
    /// hold, the sweep's peak torque is within 10 % of the catalog's, the demo tracks the
    /// mapped rpm with less than 40 rpm RMS, no underruns while revving and during the demo (after its first second).
    /// </summary>
    public sealed class BenchV8Scenario : IAutotestScenario
    {
        public string Kind => "bench-shot";

        [Serializable]
        sealed class Params
        {
            public string engine = "v8_bigblock_a";
            public float revPedal = 0.45f;
            public float peakTolerance = 0.10f;
            public float maxRmsRpm = 40f;
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            var bench = EngineBench.Current != null ? EngineBench.Current : EngineBench.Create();
            yield return null;
            yield return null;
            var def = EngineCatalog.Find(p.engine);
            if (def == null) { ctx.Fail("no engine " + p.engine); yield break; }
            if (bench.Def != def && !bench.Select(def)) { ctx.Fail("engine did not load: " + bench.LastError); yield break; }
            var engine = bench.Engine;
            if (engine == null) { ctx.Fail("engine did not load: " + bench.LastError); yield break; }
            int rate = AudioSettings.outputSampleRate;
            bench.ScriptControl = true;
            bench.LoadOn = true;
            bench.ScriptIgnition = true;
            bench.ScriptStarter = true;
            bench.ScriptThrottle = 0f;
            var tap = bench.Tap;
            var captured = new System.Collections.Generic.List<float>(rate * 2 * 30);
            tap.Clear();
            tap.Recording = true;

            // crank like a driver: hold the starter until it runs on its own
            float starterRpm = (float)(engine.Info.starterSpeedRadS * 9.5492966);
            float t0 = Time.unscaledTime, released = -1f, above = 0f;
            while (Time.unscaledTime - t0 < 4f)
            {
                float rpm = (float)bench.LastStep.rpm;
                above = rpm > 1.3f * starterRpm ? above + Time.unscaledDeltaTime : 0f;
                if (above >= 0.25f) { released = Time.unscaledTime - t0; break; }
                tap.Drain(captured);
                yield return null;
            }
            bench.ScriptStarter = false;
            ctx.Metric("starter_release_s", released);
            if (released < 0 || released > 3f) ctx.Fail($"{def.id} did not start within 3 s on its starter");
            // idle 2 s, measured over the second
            double idleSum = 0;
            int idleN = 0;
            for (float w = Time.unscaledTime; Time.unscaledTime - w < 2f;)
            {
                if (Time.unscaledTime - w > 1f) { idleSum += bench.LastStep.rpm; idleN++; }
                tap.Drain(captured);
                yield return null;
            }
            double idle = idleN > 0 ? idleSum / idleN : 0;
            ctx.Metric("idle_rpm", idle);
            if (idle < def.idleRpmMin || idle > def.idleRpmMax) ctx.Fail($"idle {idle:F0} rpm outside {def.idleRpmMin:F0}-{def.idleRpmMax:F0}");
            var s0 = engine.Stats();
            // blip and rev against the road-like load; the last 1.5 s of the 3 s hold is the pitch window (PLAN 1.9 rule)
            int winFrom = -1, winTo = -1;
            double winRpm = 0;
            int winN = 0;
            foreach (var (pedal, secs) in new[] { (1f, 0.6f), (0f, 1.0f), (p.revPedal, 3.0f) })
            {
                bench.ScriptThrottle = pedal;
                bool hold = pedal == p.revPedal && secs == 3.0f;
                for (float w = Time.unscaledTime; Time.unscaledTime - w < secs;)
                {
                    tap.Drain(captured);
                    if (hold && Time.unscaledTime - w >= secs - 1.5f)
                    {
                        if (winFrom < 0) winFrom = captured.Count;
                        winTo = captured.Count;
                        winRpm += bench.LastStep.rpm;
                        winN++;
                    }
                    yield return null;
                }
            }
            ctx.Metric("rev_rpm", bench.LastStep.rpm);
            int ch = Math.Max(1, tap.Channels);
            if (winN > 0 && winTo - winFrom > 4096 * ch)
            {
                var x = captured.ToArray();
                var r = PitchAnalysis.Check(x, winFrom, (winTo - winFrom) / ch, ch, rate, winRpm / winN, def.pitchOrders, 0.03);
                ctx.Note($"pitch at {winRpm / winN:F0} rpm, {p.revPedal * 100:F0} % pedal against the load: {r}");
                ctx.Metric("pitch_rpm", winRpm / winN);
                ctx.Metric("pitch_pass", r.Pass ? 1 : 0);
                if (!r.Pass) ctx.Fail($"pitch at {winRpm / winN:F0} rpm: acf {r.acfHz:F1} Hz (order {r.acfOrder:F2}), peak {r.peakHz:F1} Hz (order {r.peakOrder:F2})");
            }
            else ctx.Fail("no audio in the pitch window");
            var s1 = engine.Stats();
            ctx.Metric("underruns_rev", s1.audioUnderruns - s0.audioUnderruns);
            if (s1.audioUnderruns - s0.audioUnderruns > 0) ctx.Fail($"{s1.audioUnderruns - s0.audioUnderruns} underruns while revving");
            tap.Drain(captured);
            tap.Recording = false;
            AudioTap.SaveWav(Path.Combine(ctx.OutDir, "bench_v8.wav"), captured.ToArray(), captured.Count, Math.Max(1, tap.Channels), rate);
            ctx.Evidence(Path.Combine(ctx.OutDir, "bench_v8.wav"));

            // dyno sweep, then the screenshot with the curve drawn
            bench.StartSweep();
            float ts = Time.unscaledTime;
            while ((bench.Sweep.Running || bench.SweepPoints.Count < bench.Sweep.Total) && bench.Sweep.Error == null && Time.unscaledTime - ts < 120f)
                yield return null;
            if (bench.Sweep.Error != null) ctx.Fail("dyno sweep: " + bench.Sweep.Error);
            double peakT = 0, peakRpm = 0, peakHp = 0, peakHpRpm = 0;
            foreach (var pt in bench.SweepPoints)
            {
                if (pt.torqueNm > peakT) { peakT = pt.torqueNm; peakRpm = pt.rpm; }
                if (pt.powerW / 745.7 > peakHp) { peakHp = pt.powerW / 745.7; peakHpRpm = pt.rpm; }
            }
            ctx.Metric("sweep_points", bench.SweepPoints.Count);
            ctx.Metric("peak_torque_nm", peakT);
            ctx.Metric("peak_torque_rpm", peakRpm);
            ctx.Metric("peak_hp", peakHp);
            ctx.Metric("peak_hp_rpm", peakHpRpm);
            ctx.Note($"{def.displayName}: idle {idle:F0} rpm, sweep peak {peakT:F0} N m at {peakRpm:F0} rpm, {peakHp:F0} hp at {peakHpRpm:F0} rpm");
            if (Math.Abs(peakT - def.peakTorqueNm) > p.peakTolerance * def.peakTorqueNm)
                ctx.Fail($"sweep peak {peakT:F0} N m is not within {p.peakTolerance * 100:F0} % of {def.peakTorqueNm:F0}");
            bench.ScriptThrottle = 0.25f;
            for (float w = Time.unscaledTime; Time.unscaledTime - w < 1.0f;) yield return null;
            yield return ctx.Screenshot("bench_v8_dyno");

            // the truck demo through IVehicleEngine, screenshot mid-pull
            if (!bench.StartDemo()) { ctx.Fail("vehicle demo: " + bench.LastError); bench.ScriptControl = false; yield break; }
            var demoTap = bench.Tap;
            var demoAudio = new System.Collections.Generic.List<float>(rate * 2 * 12);
            demoTap.Clear();
            demoTap.Recording = true;
            bool shot = false;
            EspStats d0 = default;
            bool d0Set = false;
            var demo = bench.Demo;
            var driver = bench.DemoDriver;
            double rms = 0, lagRms = 0;
            int compared = 0;
            EspStats d1 = default;
            while (bench.DemoRunning && driver != null && !driver.Done)
            {
                if (!d0Set && driver.T >= FollowRamp.KeyOnS + 1f) { d0 = demo.Engine.Stats(); d0Set = true; }
                demoTap.Drain(demoAudio);
                if (!shot && driver.T >= 4.0f)
                {
                    shot = true;
                    yield return ctx.Screenshot("bench_v8_demo");
                    continue;
                }
                if (driver.T > FollowRamp.EndS - 0.2f && demo.Engine != null)
                {
                    rms = demo.TrackingStats.Rms;
                    lagRms = demo.TrackingStats.LagRms;
                    compared = demo.TrackingStats.Count;
                    d1 = demo.Engine.Stats();
                }
                yield return null;
            }
            demoTap.Drain(demoAudio);
            AudioTap.SaveWav(Path.Combine(ctx.OutDir, "demo_v8.wav"), demoAudio.ToArray(), demoAudio.Count, Math.Max(1, demoTap.Channels), rate);
            ctx.Evidence(Path.Combine(ctx.OutDir, "demo_v8.wav"));
            int demoUnderruns = d1.audioUnderruns - d0.audioUnderruns;
            ctx.Metric("demo_rms_rpm", rms);
            ctx.Metric("demo_lag_rms_rpm", lagRms);
            ctx.Metric("demo_compared", compared);
            ctx.Metric("demo_underruns", demoUnderruns);
            ctx.Metric("demo_rtf", d1.rtf);
            if (compared < 500) ctx.Fail($"demo: only {compared} FOLLOW_RPM steps compared");
            if (rms >= p.maxRmsRpm) ctx.Fail($"demo tracking RMS {rms:F1} rpm");
            if (demoUnderruns > 0) ctx.Fail($"demo: {demoUnderruns} underruns");
            for (float w = Time.unscaledTime; Time.unscaledTime - w < 0.5f;) yield return null;
            bench.ScriptControl = false;
        }
    }
}
