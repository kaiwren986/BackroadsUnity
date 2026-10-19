using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Backroads.EngineSim
{
    /// <summary>
    /// Plays an engine: OnAudioFilterRead calls only esp_read_audio (PLAN 3.6 item 4, research 01 7.4). Put it on a playing
    /// AudioSource without a clip; the callback generates the audio. Lock-free and allocation-free on the audio thread.
    /// Detaching waits until a running callback has left the native call, so the engine can be destroyed right after.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class EngineSimAudio : MonoBehaviour
    {
        static readonly List<EngineSimAudio> s_All = new List<EngineSimAudio>();

        IntPtr _handle;          // read on the audio thread
        int _inCallback;         // 1 while the audio thread is inside esp_read_audio
        public int Callbacks;    // for diagnostics (audio thread writes, main thread reads)
        public int LastFrames;

        public void Attach(EngineSimEngine engine)
        {
            Detach();
            Volatile.Write(ref _handle, engine != null ? engine.Handle : IntPtr.Zero);
            lock (s_All) if (!s_All.Contains(this)) s_All.Add(this);
        }

        public void Detach()
        {
            Volatile.Write(ref _handle, IntPtr.Zero);
            var spin = new SpinWait();
            while (Volatile.Read(ref _inCallback) != 0) spin.SpinOnce();
        }

        /// <summary>Called by EngineSimEngine.Dispose: every component that plays this handle lets go of it first.</summary>
        internal static void DetachAll(IntPtr handle)
        {
            lock (s_All)
                foreach (var a in s_All)
                    if (a != null && a._handle == handle) a.Detach();
        }

        void OnDestroy()
        {
            Detach();
            lock (s_All) s_All.Remove(this);
        }

        unsafe void OnAudioFilterRead(float[] data, int channels)
        {
            Interlocked.Exchange(ref _inCallback, 1);
            try
            {
                var h = Volatile.Read(ref _handle);
                if (h == IntPtr.Zero || channels <= 0)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }
                int frames = data.Length / channels;
                fixed (float* p = data) Native.esp_read_audio(h, p, frames, channels);
                LastFrames = frames;
                Callbacks++;
            }
            finally
            {
                Volatile.Write(ref _inCallback, 0);
            }
        }
    }
}
