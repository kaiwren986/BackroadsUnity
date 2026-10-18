using System.Collections;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Backroads.Tests.PlayMode
{
    /// <summary>Backroads.Tests.PlayMode.Audio.*</summary>
    [TestFixture]
    public class Audio
    {
        /// <summary>Counts DSP callbacks on the listener (the final mix) and on a playing source.</summary>
        sealed class DspCounter : MonoBehaviour
        {
            public int Calls;
            public int Samples;
            void OnAudioFilterRead(float[] data, int channels)
            {
                Interlocked.Increment(ref Calls);
                Interlocked.Add(ref Samples, data.Length / Mathf.Max(1, channels));
            }
        }

        /// <summary>
        /// PLAN 1.9 [T20]: does OnAudioFilterRead run at least 100 times per second in a batchmode PlayMode run?
        /// The answer is written to artifacts/audio-batchmode.json and decides AudioPolicy (see AudioPolicy.cs):
        /// if DSP does not run, every audio assertion is tagged PlayerOnly and runs only in player autotests,
        /// so "0 underruns" can never pass vacuously. The test is Inconclusive (not failed) when DSP does not run.
        /// </summary>
        [UnityTest]
        public IEnumerator BatchmodeDspRuns()
        {
            var listenerGo = new GameObject("dsp-listener");
            if (Object.FindAnyObjectByType<AudioListener>() == null) listenerGo.AddComponent<AudioListener>();
            var onListener = listenerGo.GetComponent<AudioListener>() != null ? listenerGo.AddComponent<DspCounter>() : null;

            var srcGo = new GameObject("dsp-source");
            var src = srcGo.AddComponent<AudioSource>();
            int rate = AudioSettings.outputSampleRate > 0 ? AudioSettings.outputSampleRate : 48000;
            src.clip = AudioClip.Create("dsp-tone", rate, 1, rate, false);
            var tone = new float[rate];
            for (int i = 0; i < tone.Length; i++) tone[i] = 0.05f * Mathf.Sin(2 * Mathf.PI * 220f * i / rate);
            src.clip.SetData(tone, 0);
            src.loop = true;
            src.volume = 0f; // never audible
            var onSource = srcGo.AddComponent<DspCounter>();
            src.Play();

            yield return null;
            float t0 = Time.realtimeSinceStartup;
            int s0 = onSource.Calls, l0 = onListener != null ? onListener.Calls : 0;
            while (Time.realtimeSinceStartup - t0 < 2f) yield return null;
            float secs = Time.realtimeSinceStartup - t0;
            float sourcePerSec = (onSource.Calls - s0) / secs;
            float listenerPerSec = onListener != null ? (onListener.Calls - l0) / secs : 0f;
            float best = Mathf.Max(sourcePerSec, listenerPerSec);
            bool runs = best >= 100f;

            AudioSettings.GetDSPBufferSize(out int bufLen, out int numBufs);
            var json = "{\n" +
                       $"  \"batchmode\": {(Application.isBatchMode ? "true" : "false")},\n" +
                       $"  \"graphicsDevice\": \"{SystemInfo.graphicsDeviceType}\",\n" +
                       $"  \"outputSampleRate\": {AudioSettings.outputSampleRate},\n" +
                       $"  \"dspBufferLength\": {bufLen},\n  \"dspBuffers\": {numBufs},\n" +
                       $"  \"driverCaps\": \"{AudioSettings.driverCapabilities}\",\n" +
                       $"  \"sourceCallbacksPerSecond\": {sourcePerSec.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},\n" +
                       $"  \"listenerCallbacksPerSecond\": {listenerPerSec.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)},\n" +
                       $"  \"dspRuns\": {(runs ? "true" : "false")},\n" +
                       $"  \"policy\": \"{(runs ? "audio assertions may run in batchmode PlayMode" : "audio assertions are PlayerOnly")}\"\n}}\n";
            try
            {
                var art = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "artifacts"));
                Directory.CreateDirectory(art);
                File.WriteAllText(Path.Combine(art, "audio-batchmode.json"), json);
            }
            catch (System.Exception e) { Debug.LogWarning("[BR] audio-batchmode.json not written: " + e.Message); }
            TestContext.WriteLine(json);

            Object.Destroy(srcGo);
            Object.Destroy(listenerGo);
            if (!runs)
                Assert.Inconclusive($"DSP ran {best:F0} callbacks/s (< 100) in this PlayMode run (batchmode={Application.isBatchMode}): audio assertions are PlayerOnly");
            Assert.GreaterOrEqual(best, 100f);
        }
    }
}
