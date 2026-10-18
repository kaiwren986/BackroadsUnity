using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace Backroads.Testing
{
    /// <summary>
    /// Full per-frame metrics for autotests (PLAN 1.9): frametimes.csv with the frame time, the simulation main-thread
    /// time and the main-thread CPU time (FrameTimingManager, enabled by ProjectSetup) of every rendered frame.
    /// sim_ms is what simulation systems report through SimMsThisFrame; it stays 0 until the simulation exists (M2).
    /// </summary>
    public sealed class MetricsRecorder : MonoBehaviour
    {
        public struct Sample { public int frame; public float frameMs; public float simMs; public float mainThreadMs; }

        readonly List<Sample> _samples = new List<Sample>(4096);
        readonly FrameTiming[] _timing = new FrameTiming[1];
        public bool Recording = true;

        /// <summary>Simulation systems add their main-thread time here each frame (from M2: VehicleSimSystem, GameLoop).</summary>
        public static float SimMsThisFrame;

        public IReadOnlyList<Sample> Samples => _samples;

        void LateUpdate()
        {
            if (!Recording) { SimMsThisFrame = 0f; return; }
            FrameTimingManager.CaptureFrameTimings();
            float main = 0f;
            if (FrameTimingManager.GetLatestTimings(1, _timing) > 0) main = (float)_timing[0].cpuMainThreadFrameTime;
            _samples.Add(new Sample { frame = Time.frameCount, frameMs = Time.unscaledDeltaTime * 1000f, simMs = SimMsThisFrame, mainThreadMs = main });
            SimMsThisFrame = 0f;
        }

        public void WriteCsv(string path)
        {
            var sb = new StringBuilder("frame,frame_ms,sim_ms,main_thread_ms\n");
            foreach (var s in _samples)
                sb.Append(s.frame).Append(',')
                  .Append(s.frameMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.simMs.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.mainThreadMs.ToString("F3", CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllText(path, sb.ToString());
        }

        public float Percentile(float p)
        {
            if (_samples.Count == 0) return 0f;
            var v = new List<float>(_samples.Count);
            foreach (var s in _samples) v.Add(s.frameMs);
            v.Sort();
            int i = Mathf.Clamp(Mathf.CeilToInt(p / 100f * v.Count) - 1, 0, v.Count - 1);
            return v[i];
        }

        public long ReservedMemoryMb => Profiler.GetTotalReservedMemoryLong() / (1024 * 1024);
    }
}
