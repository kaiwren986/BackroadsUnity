using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Backroads.Core;
using Backroads.EngineSim;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Backroads.Tests.PlayMode.EngineSim
{
    /// <summary>
    /// Backroads.Tests.PlayMode.EngineSim.FollowInVehicle.* (PLAN 6.1 M2 lane gate, card T2.4): IVehicleEngine on engine-sim
    /// driven by a scripted EngineState (FollowRamp: the classic JS engine's rpm, key on, full-pedal pulls with an upshift,
    /// lift-off, key off) in FixedUpdate, as a vehicle would, without a vehicle. Per roster engine at the High tier
    /// (4 gas substeps), unpinned: engine-sim's rpm tracks the mapped rpm of the same physics step with < 40 rpm RMS, and
    /// the worker's real-time factor is >= 2.0. The min-spec proxy half of the gate (RTF >= 1.5 on Low) is the player
    /// autotest m2.engine.follow with autotest.ps1 -MinSpec.
    /// </summary>
    [TestFixture]
    public class FollowInVehicle
    {
        const double MaxRmsRpm = 40.0;
        const double MinRtfHigh = 2.0;

        static void RequirePlugin()
        {
            if (!Native.Available(out var why)) Assert.Fail("EngineSimPlugin: " + why + " (run tools\\ci\\build-native.ps1)");
        }

        [UnityTest]
        public IEnumerator TracksMappedRpmAtHigh()
        {
            RequirePlugin();
            var lines = new List<string>();
            var failures = new List<string>();
            // a listener so the audio path runs as in the game, muted so a test run stays quiet
            var listener = Object.FindAnyObjectByType<AudioListener>() == null ? new GameObject("listener", typeof(AudioListener)) : null;
            float volumeBefore = AudioListener.volume;
            AudioListener.volume = 0f;
            foreach (var v in FollowRamp.Roster)
            {
                var e = EngineSimVehicleEngine.Create(v.EngineId, EngineAudioTier.High);
                Assert.IsNotNull(e, v.EngineId + " did not load");
                var go = new GameObject("FollowRamp " + v.Name);
                var d = go.AddComponent<FollowRampDriver>();
                d.Engine = e;
                d.Vehicle = v;
                e.AttachTo(go.transform);
                float t0 = Time.realtimeSinceStartup;
                bool sawFollow = false, sawFree = false;
                while (!d.Done && Time.realtimeSinceStartup - t0 < 60f)
                {
                    sawFollow |= e.Phase == EngineSimVehicleEngine.EnginePhase.Following;
                    sawFree |= e.Phase == EngineSimVehicleEngine.EnginePhase.Free;
                    yield return null;
                }
                var tr = e.TrackingStats;
                var s = e.Engine.Stats();
                var map = e.Map;
                string line = $"{v.Name} -> {v.EngineId}: {map}; tracking RMS {tr.Rms:F2} rpm (max {tr.MaxAbs:F1}) over {tr.Count} steps ({tr.Repaid} repaying), " +
                              $"newest result vs this step's target RMS {tr.LagRms:F1} rpm (the L-step lag); RTF {s.rtf:F2} " +
                              $"({e.Engine.Info.simFrequency} Hz, {e.Engine.Info.fluidSteps} substeps), worker {s.workerMsEma:F2} ms/step, " +
                              $"stale {s.staleSteps}, coast {s.coastEvents}, underruns {s.audioUnderruns}, P-core share {s.pCoreShare * 100:F0} %";
                lines.Add(line);
                if (!d.Done) failures.Add(v.Name + ": the ramp did not finish in 60 s");
                if (!sawFollow || tr.Count < 500) failures.Add($"{v.Name}: only {tr.Count} FOLLOW_RPM steps compared");
                if (!sawFree) failures.Add(v.Name + ": never in FREE (key off) mode");
                if (tr.Rms >= MaxRmsRpm) failures.Add($"{v.Name}: tracking RMS {tr.Rms:F1} rpm >= {MaxRmsRpm}");
                if (s.rtf < MinRtfHigh) failures.Add($"{v.Name}: RTF {s.rtf:F2} < {MinRtfHigh} at High");
                e.Dispose();
                Object.Destroy(go);
                yield return null;
            }
            AudioListener.volume = volumeBefore;
            if (listener != null) Object.Destroy(listener);
            TestContext.WriteLine(string.Join("\n", lines));
            Debug.Log("[EngineSim] FollowInVehicle\n" + string.Join("\n", lines));
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        /// <summary>
        /// M1 carry (card T2.4 step 4): -br-silent sets AudioListener.volume = 0, which zeroes what a filter after a source's
        /// generator sees; a tap fed by EngineSimAudio itself still records the engine.
        /// </summary>
        [UnityTest]
        public IEnumerator SourceFedTapIgnoresListenerVolume()
        {
            RequirePlugin();
            float before = AudioListener.volume;
            var listener = Object.FindAnyObjectByType<AudioListener>() == null ? new GameObject("listener", typeof(AudioListener)) : null;
            AudioListener.volume = 0f;
            FollowRamp.TryFind("i4_synth", out var v);
            var e = EngineSimVehicleEngine.Create(v.EngineId, EngineAudioTier.Low);
            Assert.IsNotNull(e);
            var go = new GameObject("tap ramp");
            var d = go.AddComponent<FollowRampDriver>();
            d.Engine = e;
            d.Vehicle = v;
            var tap = e.AudioObject.AddComponent<AudioTap>();
            e.Audio.Tap = tap;
            tap.Recording = true;
            var samples = new List<float>(48000 * 8);
            float t0 = Time.realtimeSinceStartup;
            while (d.T < 4.5f && Time.realtimeSinceStartup - t0 < 30f)
            {
                tap.Drain(samples);
                yield return null;
            }
            tap.Drain(samples);
            int callbacks = e.Audio.Callbacks;
            e.Dispose();
            Object.Destroy(go);
            if (listener != null) Object.Destroy(listener);
            AudioListener.volume = before;
            if (callbacks == 0) Assert.Inconclusive("no DSP callbacks in this run");
            double sum = 0;
            foreach (var x in samples) sum += x * x;
            double rmsDb = samples.Count > 0 ? 10 * Math.Log10(Math.Max(1e-24, sum / samples.Count)) : -240;
            TestContext.WriteLine($"tap with AudioListener.volume 0: {samples.Count} samples, RMS {rmsDb:F1} dBFS, {tap.Callbacks} tap blocks");
            Assert.Greater(tap.Callbacks, 0, "the tap was never fed");
            Assert.Greater(rmsDb, -60.0, "the source-fed tap is silent at AudioListener.volume 0");
        }
    }
}
