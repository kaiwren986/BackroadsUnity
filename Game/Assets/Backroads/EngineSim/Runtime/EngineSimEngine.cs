using System;
using System.Diagnostics;
using System.IO;
using Backroads.Core;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Backroads.EngineSim
{
    /// <summary>
    /// One engine-sim engine on its own native worker (PLAN 3.4). Call <see cref="FixedStep"/> once per FixedUpdate: it
    /// latches the controls, collects the result of step k - L (L = <see cref="FixedStepFrame.Lag"/>, waiting at most
    /// <see cref="CollectWaitUs"/>), then queues step k. Every command is queued; a missing result reuses the last one
    /// (a stale step). The main-thread cost of these native calls is measured per FixedUpdate.
    /// IVehicleEngine (M2) wraps this class; in M1 the Engine Bench drives it in FREE mode.
    /// </summary>
    public sealed unsafe class EngineSimEngine : IDisposable
    {
        public struct Options
        {
            public int audioSampleRate;     // 0 = no audio
            public int dspBufferFrames;
            public int fluidSteps;          // 0 = 8 (Ultra); High and Low use 4
            public int firMaxTaps;          // 0 = 10000
            public bool workerThread;
            public int flags;

            /// <summary>Audio at the output rate, own worker, and the PLAN 3.8 tier of -br-quality (QualityPreset.EngineAudio).</summary>
            public static Options ForPlayback()
            {
                AudioSettings.GetDSPBufferSize(out int dsp, out _);
                var o = new Options { audioSampleRate = AudioSettings.outputSampleRate, dspBufferFrames = dsp, workerThread = true };
                o.ApplyTier(QualityPreset.Current.EngineAudio);
                return o;
            }

            /// <summary>PLAN 3.8: Ultra = 8 gas substeps, IR up to 10,000 taps; High = 4 substeps; Low = 4 substeps, IR up to 2,000 taps.
            /// The simulation frequency is never a quality knob.</summary>
            public void ApplyTier(EngineAudioTier tier)
            {
                fluidSteps = tier == EngineAudioTier.Ultra ? 8 : 4;
                firMaxTaps = tier == EngineAudioTier.Low ? 2000 : 10000;
            }
        }

        public const int CollectWaitUs = 1000;   // PLAN 3.4: bounded wait, counted in the main-thread budget

        /// <summary>
        /// Worker priority (-2..2) for this run's tier. PLAN 3.4 runs workers above normal; measured on the min-spec proxy
        /// (6 E-cores, Low, 30 fps) that makes the woken worker preempt Unity's main thread for a whole command (main-thread
        /// cost p95 2.3 ms, p99 2.7 ms at priority 0 or 1); below normal it is 0.009 ms p99 with 0 underruns and RTF 3.1.
        /// So the Low tier runs the worker below normal; the other tiers above normal.
        /// </summary>
        public static int DefaultWorkerPriority => QualityPreset.Current.EngineAudio == EngineAudioTier.Low ? -1 : 1;

        IntPtr _h;
        readonly object _lock = new object();
        EspControls _controls;
        EspStepOut _last;
        bool _haveLast;
        long _next;            // step index of the next kick
        long _lastCollected = -1;
        int _staleRun;

        // main-thread cost per FixedUpdate (ms), a ring for percentiles
        readonly float[] _cost = new float[4096];
        int _costCount, _costNext;
        static readonly double s_TickMs = 1000.0 / Stopwatch.Frequency;

        public EngineDef Def { get; }
        public EspEngineInfo Info;
        public IntPtr Handle => _h;
        public bool Valid => _h != IntPtr.Zero;
        public EspStepOut Last => _last;
        public bool HaveResult => _haveLast;
        public int StaleRun => _staleRun;
        public long StepsKicked => _next;

        EngineSimEngine(EngineDef def) { Def = def; }

        /// <summary>Creates and loads an engine; null (with a reason) if the plugin is missing or the engine fails.</summary>
        public static EngineSimEngine Create(EngineDef def, Options o, out string error)
        {
            error = null;
            if (!Native.Available(out var why)) { error = why; return null; }
            var e = new EngineSimEngine(def);
            var desc = new EspCreateDesc
            {
                structSize = EspCreateDesc.Size, audioSampleRate = o.audioSampleRate, audioDspBufferFrames = o.dspBufferFrames,
                fluidSteps = o.fluidSteps, firMaxTaps = o.firMaxTaps, workerThread = o.workerThread ? 1 : 0, flags = o.flags,
            };
            var es = Native.Utf8(EngineCatalog.EsDirFor(def));
            var tmp = Native.Utf8(EngineCatalog.TempDir);
            fixed (byte* pe = es)
            fixed (byte* pt = tmp)
                e._h = Native.esp_create(desc, pe, pt);
            if (e._h == IntPtr.Zero) { error = "esp_create failed: " + EngineSimLog.Drain(); return null; }

            var err = new byte[4096];
            int r;
            fixed (byte* perr = err)
            {
                if (def.source == EngineSource.Builtin)
                {
                    var n = Native.Utf8(def.builtinName ?? def.id);
                    fixed (byte* pn = n) r = Native.esp_load_builtin(e._h, pn, perr, err.Length);
                }
                else
                {
                    var p = Native.Utf8(def.scriptPath);
                    var t = string.IsNullOrEmpty(def.topNode) ? null : Native.Utf8(def.topNode);
                    fixed (byte* pp = p)
                    fixed (byte* ptop = t)
                        r = Native.esp_load_script(e._h, pp, ptop, perr, err.Length);
                }
            }
            if (r != EspStatus.Ok)
            {
                error = $"{def.id}: {EspStatus.Name(r)} {Native.FromUtf8(err)}";
                e.Dispose();
                return null;
            }
            e.Info.structSize = EspEngineInfo.Size;
            Native.esp_get_info(e._h, ref e.Info);
            e._controls = new EspControls { structSize = EspControls.Size, simSpeed = 1f };
            e._last.structSize = EspStepOut.Size;
            return e;
        }

        public float Throttle { get => _controls.throttle; set => _controls.throttle = Mathf.Clamp01(value); }
        public bool Ignition { get => _controls.ignition != 0; set => _controls.ignition = value ? 1 : 0; }
        public bool Starter { get => _controls.starter != 0; set => _controls.starter = value ? 1 : 0; }

        /// <summary>
        /// One physics step (FixedUpdate): collect step k - L, then queue step k with dt (physics time). Returns the
        /// result to use this step (the last one again if k - L is not there after the bounded wait).
        /// </summary>
        public EspStepOut FixedStep(double dt, in EspDrivelineIn driveline)
        {
            if (_h == IntPtr.Zero) return _last;
            long t0 = Stopwatch.GetTimestamp();
            _controls.simSpeed = Time.timeScale;
            _controls.frameDtS = FixedStepFrame.FrameDt > 0f ? FixedStepFrame.FrameDt : Time.unscaledDeltaTime;
            _controls.frameIndex = Time.frameCount;
            Native.esp_set_controls(_h, _controls);

            long want = _next - FixedStepFrame.Lag;
            if (want > _lastCollected && want >= 0)
            {
                var o = new EspStepOut { structSize = EspStepOut.Size };
                int r = Native.esp_step_end(_h, want, CollectWaitUs, ref o);
                if (r == EspStatus.Ok) { _last = o; _haveLast = true; _lastCollected = want; _staleRun = 0; }
                else if (r == EspStatus.Pending) _staleRun++;
                else _lastCollected = want;  // too old: skip it
            }
            int k = Native.esp_step_begin(_h, _next, dt, driveline, null, 0);
            if (k == EspStatus.Ok) _next++;
            else if (_next % 600 == 0) Debug.LogWarning($"[EngineSim] esp_step_begin {EspStatus.Name(k)}");
            float ms = (float)((Stopwatch.GetTimestamp() - t0) * s_TickMs);
            _cost[_costNext] = ms;
            _costNext = (_costNext + 1) % _cost.Length;
            if (_costCount < _cost.Length) _costCount++;
            return _last;
        }

        /// <summary>Main-thread cost of the native calls per FixedUpdate: percentile p (0-100) over the last 4,096 steps.</summary>
        public float CostPercentileMs(float p)
        {
            if (_costCount == 0) return 0f;
            var v = new float[_costCount];
            Array.Copy(_cost, v, _costCount);
            Array.Sort(v);
            int i = Mathf.Clamp(Mathf.CeilToInt(p / 100f * v.Length) - 1, 0, v.Length - 1);
            return v[i];
        }

        public void ResetCostStats() { _costCount = 0; _costNext = 0; }

        public EspStats Stats()
        {
            var s = new EspStats { structSize = EspStats.Size };
            if (_h != IntPtr.Zero) Native.esp_get_stats(_h, ref s);
            return s;
        }

        public bool DebugTap(string wavPath, double seconds)
        {
            if (_h == IntPtr.Zero) return false;
            var p = wavPath == null ? null : Native.Utf8(wavPath);
            fixed (byte* pp = p) return Native.esp_debug_tap(_h, pp, seconds) == EspStatus.Ok;
        }

        public void SetWorker(int priority, bool noPowerThrottling)
        {
            if (_h == IntPtr.Zero) return;
            Native.esp_set_worker_options(_h, new EspWorkerOptions
            {
                structSize = EspWorkerOptions.Size, priority = priority, powerThrottlingOff = noPowerThrottling ? 1 : 0, coastMaxMs = 300,
            });
        }

        /// <summary>Stop the AudioSource (or detach EngineSimAudio) first: the ring stays valid until this returns.</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                if (_h == IntPtr.Zero) return;
                EngineSimAudio.DetachAll(_h);
                Native.esp_destroy(_h);
                _h = IntPtr.Zero;
            }
            EngineSimLog.Flush();
        }

        /// <summary>The crash folder for plugin minidumps: %TEMP%\(company)\(product)\Crashes, like Unity's (PLAN 1.9 [S5]).</summary>
        public static void SetCrashFolder()
        {
            if (!Native.Available(out _)) return;
            var dir = Path.Combine(Path.GetTempPath(), Application.companyName, Application.productName, "Crashes");
            var b = Native.Utf8(dir);
            fixed (byte* p = b) Native.esp_set_crash_dir(p);
        }
    }
}
