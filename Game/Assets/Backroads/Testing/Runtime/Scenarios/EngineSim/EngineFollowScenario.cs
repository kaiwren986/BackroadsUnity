using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Backroads.Core;
using Backroads.EngineSim;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Backroads.Testing
{
    /// <summary>
    /// m2.engine.follow (PLAN 6.1 M2 lane gate EngineSim.FollowInVehicle, in the player; card T2.4): every roster engine,
    /// through IVehicleEngine (VehicleEngines.Create, as a vehicle gets it), follows the scripted classic-engine drive of
    /// FollowRamp in FixedUpdate at this run's tier (-br-quality; autotest.ps1 -MinSpec = Low on the min-spec proxy).
    /// Gates per engine: tracking RMS of engine-sim's rpm against the mapped rpm of the same step &lt; 40 rpm; real-time
    /// factor &gt;= 2.0 at High/Ultra or &gt;= 1.5 at Low (PLAN 3.4, 6.1 M2); 0 audio underruns from 1 s after the key turns
    /// (the engine's creation frames are reported, not gated, as in m1.bench.revcycle). Writes follow_report.json and one
    /// follow_(engine).csv per engine.
    /// </summary>
    public sealed class EngineFollowScenario : IAutotestScenario
    {
        public string Kind => "engine-follow";

        [Serializable]
        sealed class Params
        {
            public string[] engines = { "v8_bigblock_a", "v8_cross_a", "f4_boxer_a", "i4_synth" };
            public float maxRmsRpm = 40f;
            public float minRtfHigh = 2.0f;
            public float minRtfLow = 1.5f;
            public int maxUnderruns = 0;
        }

        [Serializable]
        sealed class Row
        {
            public string vehicle, engine, map, tier;
            public int simFrequency, fluidSteps, irTaps, steps, compared, staleSteps, coastEvents, underruns, underrunsAtStart;
            public double rmsRpm, maxAbsRpm, lagRmsRpm, rtf, workerMs, pCoreShare, mainThreadP99Ms, ringTargetMs;
            public bool pass;
        }

        [Serializable]
        sealed class Report
        {
            public string tier;
            public float fps;
            public List<Row> rows = new List<Row>();
            public List<string> failures = new List<string>();
        }

        public IEnumerator Run(AutotestContext ctx)
        {
            var p = ctx.Params<Params>();
            var tier = QualityPreset.Current.EngineAudio;
            float minRtf = tier == EngineAudioTier.Low ? p.minRtfLow : p.minRtfHigh;
            var report = new Report { tier = tier.ToString() };
            if (Object.FindAnyObjectByType<AudioListener>() == null) new GameObject("FollowListener", typeof(AudioListener));
            int frames = 0;
            float run0 = Time.unscaledTime;
            foreach (var id in p.engines)
            {
                if (!FollowRamp.TryFind(id, out var v)) { report.failures.Add($"{id}: no FollowRamp vehicle"); continue; }
                var ie = VehicleEngines.Create(id);
                var e = ie as EngineSimVehicleEngine;
                if (e == null) { report.failures.Add($"{id}: VehicleEngines.Create returned {(ie == null ? "null" : ie.GetType().Name)}"); ie?.Dispose(); continue; }
                var go = new GameObject("FollowRamp " + v.Name);
                var d = go.AddComponent<FollowRampDriver>();
                d.Engine = e;
                d.Vehicle = v;
                e.AttachTo(go.transform);
                var row = new Row { vehicle = v.Name, engine = id, tier = tier.ToString() };
                EspStats s0 = default;
                bool atStart = false;
                float t0 = Time.unscaledTime;
                while (!d.Done && Time.unscaledTime - t0 < 60f)
                {
                    if (!atStart && d.T >= FollowRamp.KeyOnS + 1.0f)
                    {
                        atStart = true;
                        s0 = e.Engine.Stats();
                        e.Engine.ResetCostStats();
                    }
                    frames++;
                    yield return null;
                }
                var s = e.Engine.Stats();
                var tr = e.TrackingStats;
                row.map = e.Map.ToString();
                row.simFrequency = e.Engine.Info.simFrequency;
                row.fluidSteps = e.Engine.Info.fluidSteps;
                row.irTaps = e.Engine.Info.irTaps;
                row.steps = d.Steps;
                row.compared = tr.Count;
                row.rmsRpm = tr.Rms;
                row.maxAbsRpm = tr.MaxAbs;
                row.lagRmsRpm = tr.LagRms;
                row.rtf = s.rtf;
                row.workerMs = s.workerMsEma;
                row.pCoreShare = s.pCoreShare;
                row.staleSteps = s.staleSteps;
                row.coastEvents = s.coastEvents;
                row.underrunsAtStart = s0.audioUnderruns;
                row.underruns = s.audioUnderruns - s0.audioUnderruns;
                row.mainThreadP99Ms = e.Engine.CostPercentileMs(99);
                row.ringTargetMs = s.ringTargetFrames * 1000.0 / Math.Max(1, AudioSettings.outputSampleRate);
                var f = new List<string>();
                if (!d.Done) f.Add("the ramp did not finish in 60 s");
                if (tr.Count < 500) f.Add($"only {tr.Count} FOLLOW_RPM steps compared");
                if (tr.Rms >= p.maxRmsRpm) f.Add($"tracking RMS {tr.Rms:F1} rpm >= {p.maxRmsRpm}");
                if (s.rtf < minRtf) f.Add($"RTF {s.rtf:F2} < {minRtf} at {tier}");
                if (row.underruns > p.maxUnderruns) f.Add($"{row.underruns} underruns");
                row.pass = f.Count == 0;
                foreach (var x in f) report.failures.Add($"{v.Name} ({id}): {x}");
                report.rows.Add(row);
                ctx.Note($"{v.Name} -> {id}: {row.map}; RMS {row.rmsRpm:F2} rpm, lag RMS {row.lagRmsRpm:F1}, RTF {row.rtf:F2}, " +
                         $"underruns {row.underruns} (+{row.underrunsAtStart} at creation), main thread p99 {row.mainThreadP99Ms:F3} ms");
                ctx.Metric($"rms_{id}", row.rmsRpm);
                ctx.Metric($"rtf_{id}", row.rtf);
                ctx.Metric($"underruns_{id}", row.underruns);
                ctx.Metric($"main_p99_ms_{id}", row.mainThreadP99Ms);
                File.WriteAllText(Path.Combine(ctx.OutDir, $"follow_{id}.csv"), d.Csv.ToString());
                ctx.Evidence(Path.Combine(ctx.OutDir, $"follow_{id}.csv"));
                e.Dispose();
                Object.Destroy(go);
                yield return null;
            }
            report.fps = frames / Mathf.Max(0.001f, Time.unscaledTime - run0);
            ctx.Metric("fps", report.fps);
            File.WriteAllText(Path.Combine(ctx.OutDir, "follow_report.json"), JsonUtility.ToJson(report, true));
            ctx.Evidence(Path.Combine(ctx.OutDir, "follow_report.json"));
            foreach (var x in report.failures) ctx.Fail(x);
        }
    }
}
