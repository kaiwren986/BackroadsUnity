using System;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.IntegerTime;
using UnityEngine;
using UnityEngine.Audio;

namespace Backroads.EngineSim
{
    /// <summary>
    /// Spike S-AUD-1 (b), Unity 6.3 scriptable audio: an IAudioGenerator whose realtime part (a Burst struct) either reads
    /// the engine ring with a [DllImport] call to esp_read_audio, or makes a test tone. Set as AudioSource.generator, so the
    /// AudioSource's 3D panning, attenuation and mixer routing apply. ChannelBuffer offers per-sample indexing only (no
    /// pointer), so the samples go through a stack buffer. Counters live in unmanaged memory so the main thread can see
    /// that the realtime part ran, and whether it ran as Burst code.
    /// </summary>
    public sealed unsafe class EspAudioGenerator : MonoBehaviour, IAudioGenerator
    {
        public IntPtr EngineHandle;      // 0 = tone
        public float ToneHz = 440f;
        public float ToneAmplitude = 0.05f;

        public struct Counters
        {
            public long calls, frames, managedCalls;
            public double phase;
            public int lastFrames, lastChannels;
        }

        Counters* _counters;
        public Counters Read => _counters != null ? *_counters : default;

        void Awake()
        {
            _counters = (Counters*)Marshal.AllocHGlobal(sizeof(Counters));
            *_counters = default;
        }

        void OnDestroy()
        {
            if (_counters != null) Marshal.FreeHGlobal((IntPtr)_counters);
            _counters = null;
        }

        public bool isFinite => false;
        public bool isRealtime => true;
        public DiscreteTime? length => null;

        public GeneratorInstance CreateInstance(ControlContext context, AudioFormat? nestedFormat,
                                                ProcessorInstance.CreationParameters creationParameters)
        {
            var rt = new Realtime { engine = EngineHandle, counters = _counters, toneHz = ToneHz, amplitude = ToneAmplitude };
            return context.AllocateGenerator(rt, new Control(), nestedFormat, creationParameters);
        }

        [BurstCompile(CompileSynchronously = true)]
        public struct Realtime : GeneratorInstance.IRealtime
        {
            public IntPtr engine;
            public Counters* counters;
            public float toneHz, amplitude;
            public int sampleRate;

            public bool isFinite => false;
            public bool isRealtime => true;
            public DiscreteTime? length => null;

            public void Update(ProcessorInstance.UpdatedDataContext context, ProcessorInstance.Pipe pipe) { }

            public GeneratorInstance.Result Process(in RealtimeContext context, ProcessorInstance.Pipe pipe, ChannelBuffer buffer,
                                                    GeneratorInstance.Arguments args)
            {
                int frames = buffer.frameCount, channels = buffer.channelCount;
                if (frames <= 0) return 0;
                float* tmp = stackalloc float[frames];
                if (engine != IntPtr.Zero)
                {
                    Native.esp_read_audio(engine, tmp, frames, 1);
                }
                else
                {
                    double ph = counters != null ? counters->phase : 0;
                    double inc = 2 * Math.PI * toneHz / (sampleRate > 0 ? sampleRate : 48000);
                    for (int i = 0; i < frames; i++)
                    {
                        tmp[i] = amplitude * (float)Math.Sin(ph);
                        ph += inc;
                    }
                    if (counters != null) counters->phase = ph % (2 * Math.PI);
                }
                for (int c = 0; c < channels; c++)
                    for (int i = 0; i < frames; i++) buffer[c, i] = tmp[i];
                if (counters != null)
                {
                    counters->calls++;
                    counters->frames += frames;
                    counters->lastFrames = frames;
                    counters->lastChannels = channels;
                    MarkManaged(counters);
                }
                return frames;
            }

            [BurstDiscard]
            static void MarkManaged(Counters* c) => c->managedCalls++;  // stripped when Burst compiles Process
        }

        public struct Control : GeneratorInstance.IControl<Realtime>
        {
            public void Configure(ControlContext context, ref Realtime realtime, in AudioFormat format,
                                  out GeneratorInstance.Setup setup, ref GeneratorInstance.Properties properties)
            {
                realtime.sampleRate = format.sampleRate;
                setup = new GeneratorInstance.Setup(format.speakerMode, format.sampleRate);
            }

            public void Dispose(ControlContext context, ref Realtime realtime) { }
            public void Update(ControlContext context, ProcessorInstance.Pipe pipe) { }

            public ProcessorInstance.Response OnMessage(ControlContext context, ProcessorInstance.Pipe pipe, ProcessorInstance.Message message) =>
                ProcessorInstance.Response.Unhandled;
        }
    }
}
