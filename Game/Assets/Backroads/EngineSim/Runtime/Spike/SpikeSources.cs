#if UNITY_EDITOR || BACKROADS_TESTING
using System;
using System.Threading;
using UnityEngine;

namespace Backroads.EngineSim
{
    /// <summary>
    /// Spike S-AUD-1 (a) and (c), Dev builds only: audio made in OnAudioFilterRead on an AudioSource, either written over
    /// the (empty) input like EngineSimAudio does, or multiplied into the input of a looping DC clip (the "DC clip x filter"
    /// workaround: the clip's channel carries Unity's distance gain and pan, the filter supplies the signal). The signal is
    /// a test tone or the engine ring (esp_read_audio).
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed unsafe class SpikeFilterSource : MonoBehaviour
    {
        public enum Mode { Overwrite, MultiplyInput }

        public Mode How = Mode.Overwrite;
        public float ToneHz = 440f;
        public float ToneAmplitude = 0.1f;

        IntPtr _engine;
        int _inCallback;
        int _rate = 48000;
        double _phase;
        readonly float[] _mono = new float[16384];
        public long Callbacks;
        public int LastFrames, LastChannels;
        public double InputAbsMean;   // mean |input| of the last block (Multiply: the DC clip after Unity's gain)

        void Awake() => _rate = AudioSettings.outputSampleRate;

        public void SetEngine(IntPtr engine)
        {
            Volatile.Write(ref _engine, IntPtr.Zero);
            var spin = new SpinWait();
            while (Volatile.Read(ref _inCallback) != 0) spin.SpinOnce();
            Volatile.Write(ref _engine, engine);
        }

        void OnDestroy() => SetEngine(IntPtr.Zero);

        void OnAudioFilterRead(float[] data, int channels)
        {
            Interlocked.Exchange(ref _inCallback, 1);
            try
            {
                if (channels <= 0) return;
                int frames = Math.Min(data.Length / channels, _mono.Length);
                var h = Volatile.Read(ref _engine);
                if (h != IntPtr.Zero)
                {
                    fixed (float* p = _mono) Native.esp_read_audio(h, p, frames, 1);
                }
                else
                {
                    double inc = 2 * Math.PI * ToneHz / _rate;
                    for (int i = 0; i < frames; i++) { _mono[i] = ToneAmplitude * (float)Math.Sin(_phase); _phase += inc; }
                    _phase %= 2 * Math.PI;
                }
                double inAbs = 0;
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < channels; c++)
                    {
                        int k = i * channels + c;
                        inAbs += Math.Abs(data[k]);
                        data[k] = How == Mode.Overwrite ? _mono[i] : data[k] * _mono[i];
                    }
                InputAbsMean = inAbs / Math.Max(1, frames * channels);
                LastFrames = frames;
                LastChannels = channels;
                Callbacks++;
            }
            finally { Volatile.Write(ref _inCallback, 0); }
        }

        /// <summary>A looping clip of constant 1.0 (the carrier for <see cref="Mode.MultiplyInput"/>).</summary>
        public static AudioClip DcClip()
        {
            int rate = AudioSettings.outputSampleRate;
            var clip = AudioClip.Create("spike-dc", rate, 1, rate, false);
            var ones = new float[rate];
            for (int i = 0; i < ones.Length; i++) ones[i] = 1f;
            clip.SetData(ones, 0);
            return clip;
        }
    }

    /// <summary>
    /// Spike S-AUD-1 (d), Dev builds only: a streamed AudioClip whose PCMReaderCallback reads the engine ring (or a tone).
    /// </summary>
    public sealed unsafe class SpikePcmStream
    {
        public float ToneHz = 440f, ToneAmplitude = 0.1f;
        IntPtr _engine;
        int _rate;
        double _phase;
        public long Callbacks;
        public int LastFrames, MaxFrames;
        public AudioClip Clip { get; private set; }

        public SpikePcmStream(IntPtr engine)
        {
            _engine = engine;
            _rate = AudioSettings.outputSampleRate;
            // 10 minutes of mono; looping, so it never ends during a test.
            Clip = AudioClip.Create("spike-stream", _rate * 600, 1, _rate, true, OnRead);
        }

        public void Release() => Volatile.Write(ref _engine, IntPtr.Zero);

        void OnRead(float[] data)
        {
            int frames = data.Length;
            var h = Volatile.Read(ref _engine);
            if (h != IntPtr.Zero)
            {
                fixed (float* p = data) Native.esp_read_audio(h, p, frames, 1);
            }
            else
            {
                double inc = 2 * Math.PI * ToneHz / _rate;
                for (int i = 0; i < frames; i++) { data[i] = ToneAmplitude * (float)Math.Sin(_phase); _phase += inc; }
                _phase %= 2 * Math.PI;
            }
            LastFrames = frames;
            if (frames > MaxFrames) MaxFrames = frames;
            Callbacks++;
        }
    }
}
#endif
