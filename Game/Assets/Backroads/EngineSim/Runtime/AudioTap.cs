using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Backroads.EngineSim
{
    /// <summary>
    /// Records what Unity's audio chain carries at this point (PLAN 1.9): after EngineSimAudio on the engine's AudioSource
    /// (what Unity received from the plugin), or on the AudioListener's GameObject (the mixed, spatialised output). The
    /// audio thread copies into a preallocated ring; the main thread drains it. No allocation on the audio thread.
    /// <para>
    /// Fed by the source (<see cref="EngineSimAudio.Tap"/>, M2): EngineSimAudio copies what esp_read_audio wrote straight
    /// into the tap, before Unity applies <c>AudioListener.volume</c> to the samples a later filter sees
    /// (FilterChain.ComponentOrderAndListenerVolume). So <c>-br-silent</c> (listener volume 0) mutes the output but not
    /// the tap; the tap's own filter callback then records nothing.
    /// </para>
    /// </summary>
    public sealed class AudioTap : MonoBehaviour
    {
        float[] _ring = new float[48000 * 8 * 2];  // 8 s of stereo at 48 kHz
        long _write;                                // written by the audio thread
        long _read;                                 // main thread
        public int Channels { get; private set; } = 2;
        public long Callbacks;
        public int LastBlockFrames;
        public long Overflowed;                     // frames the main thread did not drain in time
        public bool Recording = true;

        /// <summary>Set by EngineSimAudio when it feeds this tap directly (pre listener volume).</summary>
        public volatile bool FedBySource;

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (FedBySource) return;
            Feed(data, channels);
        }

        /// <summary>Audio thread: copies one block into the ring (no allocation).</summary>
        public void Feed(float[] data, int channels)
        {
            if (!Recording) return;
            Channels = channels;
            var ring = _ring;
            long w = Interlocked.Read(ref _write);
            long r = Interlocked.Read(ref _read);
            int n = data.Length;
            if (w + n - r > ring.Length) { Overflowed += n / Math.Max(1, channels); return; }
            for (int i = 0; i < n; i++) ring[(w + i) % ring.Length] = data[i];
            Interlocked.Exchange(ref _write, w + n);
            LastBlockFrames = n / Math.Max(1, channels);
            Callbacks++;
        }

        /// <summary>Main thread: moves what the audio thread captured into dst (interleaved). Returns samples moved.</summary>
        public int Drain(System.Collections.Generic.List<float> dst)
        {
            long w = Interlocked.Read(ref _write);
            long r = _read;
            int n = (int)(w - r);
            for (int i = 0; i < n; i++) dst.Add(_ring[(r + i) % _ring.Length]);
            Interlocked.Exchange(ref _read, w);
            return n;
        }

        public void Clear()
        {
            Interlocked.Exchange(ref _read, Interlocked.Read(ref _write));
        }

        /// <summary>16-bit PCM WAV of interleaved samples.</summary>
        public static void SaveWav(string path, float[] interleaved, int count, int channels, int sampleRate)
        {
            using (var f = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(f))
            {
                int bytes = count * 2;
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                w.Write(36 + bytes);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)channels);
                w.Write(sampleRate);
                w.Write(sampleRate * channels * 2);
                w.Write((short)(channels * 2));
                w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                w.Write(bytes);
                for (int i = 0; i < count; i++) w.Write((short)Mathf.RoundToInt(Mathf.Clamp(interleaved[i], -1f, 1f) * 32767f));
            }
        }
    }
}
