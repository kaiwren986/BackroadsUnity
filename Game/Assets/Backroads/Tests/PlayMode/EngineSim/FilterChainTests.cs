using System.Collections;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Backroads.Tests.PlayMode.EngineSim
{
    /// <summary>
    /// Backroads.Tests.PlayMode.EngineSim.FilterChain.*: how Unity 6000.3 runs OnAudioFilterRead components on one
    /// AudioSource, which decides where an AudioTap must sit to see what EngineSimAudio wrote, and what -br-silent
    /// (AudioListener.volume = 0) does to the data a source filter sees.
    /// </summary>
    [TestFixture]
    public class FilterChain
    {
        sealed class Writer : MonoBehaviour
        {
            public float Value = 0.25f;
            public int Calls;
            void OnAudioFilterRead(float[] data, int channels)
            {
                for (int i = 0; i < data.Length; i++) data[i] = Value;
                Interlocked.Increment(ref Calls);
            }
        }

        sealed class Recorder : MonoBehaviour
        {
            public float MaxAbs;
            public int Calls;
            void OnAudioFilterRead(float[] data, int channels)
            {
                float m = 0;
                for (int i = 0; i < data.Length; i++) m = Mathf.Max(m, Mathf.Abs(data[i]));
                MaxAbs = Mathf.Max(MaxAbs, m);
                Interlocked.Increment(ref Calls);
            }
        }

        static IEnumerator Measure(float listenerVolume, bool recorderFirst, float[] result)
        {
            float before = AudioListener.volume;
            AudioListener.volume = listenerVolume;
            var listener = Object.FindAnyObjectByType<AudioListener>() == null ? new GameObject("listener", typeof(AudioListener)) : null;
            var go = new GameObject("chain");
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = true;
            Recorder rec;
            Writer w;
            if (recorderFirst) { rec = go.AddComponent<Recorder>(); w = go.AddComponent<Writer>(); }
            else { w = go.AddComponent<Writer>(); rec = go.AddComponent<Recorder>(); }
            src.Play();
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < 0.6f) yield return null;
            result[0] = rec.MaxAbs;
            result[1] = w.Calls;
            result[2] = rec.Calls;
            src.Stop();
            Object.Destroy(go);
            if (listener != null) Object.Destroy(listener);
            AudioListener.volume = before;
            yield return null;
        }

        /// <summary>
        /// Measured 2026-10-18 in 6000.3.20f1 (batchmode PlayMode): filters run in component order (a recorder after
        /// the writer sees 0.25, one before it sees the empty input), and AudioListener.volume scales the samples a later
        /// filter on the same source sees (0.25 at volume 0.01 arrives as 0.0025; at volume 0, as 0). So -br-silent
        /// silences any AudioTap: audio autotests set a small listener volume and divide it out (S-AUD-1).
        /// </summary>
        [UnityTest]
        public IEnumerator ComponentOrderAndListenerVolume()
        {
            var r = new float[3];
            yield return Measure(1f, false, r);
            if (r[1] == 0) Assert.Inconclusive("no DSP callbacks in this run");
            var q = new float[3];
            yield return Measure(1f, true, q);
            var z = new float[3];
            yield return Measure(0f, false, z);
            var v = new float[3];
            yield return Measure(0.01f, false, v);
            TestContext.WriteLine($"writer then recorder, volume 1: recorder max {r[0]:F4} ({r[1]} / {r[2]} calls)");
            TestContext.WriteLine($"recorder then writer, volume 1: recorder max {q[0]:F4}");
            TestContext.WriteLine($"writer then recorder, AudioListener.volume 0: recorder max {z[0]:F4} (writer calls {z[1]})");
            TestContext.WriteLine($"writer then recorder, AudioListener.volume 0.01: recorder max {v[0]:F5}");
            Assert.AreEqual(0.25f, r[0], 1e-4f, "a later filter component did not see the earlier one's output");
            Assert.AreEqual(0f, q[0], 1e-6f, "an earlier filter component saw a later one's output");
            Assert.AreEqual(0.0025f, v[0], 2e-5f, "AudioListener.volume no longer scales what a later filter sees: revisit AudioTap use in autotests");
        }
    }
}
