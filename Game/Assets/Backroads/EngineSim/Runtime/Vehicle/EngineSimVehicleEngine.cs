using System;
using System.Collections.Generic;
using Backroads.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Backroads.EngineSim
{
    /// <summary>
    /// <see cref="IVehicleEngine"/> on engine-sim for M2 (PLAN 3.5 "First drive", card T2.4): the vehicle model's classic
    /// engine drives; engine-sim follows its rpm in FOLLOW_RPM through the <see cref="RpmMap"/>, with the real pedal, so
    /// the sound is real combustion and exhaust and the load is heard through manifold pressure.
    /// Per fixed step, from <see cref="EngineState"/>:
    /// <list type="bullet">
    /// <item>running (ignition on and the vehicle's rpm at least half its idle): FOLLOW_RPM at the mapped rpm, throttle =
    /// the pedal; <see cref="DisplayRpm"/> is the mapped rpm. If the starter is held when the key turns, engine-sim first
    /// cranks and catches in FREE on its own starter until it is near its idle (the real cranking sound);</item>
    /// <item>otherwise FREE with engine-sim's own starter and ignition (cranking, catching, running down after the key is
    /// turned off): <see cref="DisplayRpm"/> is engine-sim's rpm.</item>
    /// </list>
    /// The sound comes from a clip-less 2D AudioSource (EngineSimAudio, S-AUD-1: the local car's path) that
    /// <see cref="AttachTo"/> parents to the exhaust socket. The native engine lives as long as that GameObject: destroying
    /// it (with the vehicle) disposes the engine too. Registered with <see cref="VehicleEngines"/> in client builds (this
    /// assembly is not compiled for UNITY_SERVER).
    /// </summary>
    public sealed class EngineSimVehicleEngine : IVehicleEngine
    {
        public enum EnginePhase { Off, Free, Following }

        /// <summary>A follow target below this share of the vehicle's idle means "not running" (starting or stopping).</summary>
        public const float RunningShareOfIdle = 0.5f;

        /// <summary>With the starter held at the key turn, engine-sim cranks in FREE until its rpm reaches this share of its idle.</summary>
        public const float CatchShareOfIdle = 0.75f;

        /// <summary>Tracking of engine-sim's rpm against the mapped rpm, over FOLLOW_RPM steps (EngineSim.FollowInVehicle).</summary>
        public struct Tracking
        {
            /// <summary>Results compared with the mapped rpm of the same physics step.</summary>
            public int Count;
            public double SumSq, MaxAbs;
            /// <summary>The newest result against this step's mapped rpm (includes the L-step result lag, PLAN 3.4).</summary>
            public int LagCount;
            public double LagSumSq, LagMaxAbs;
            /// <summary>Results of commands that ran no steps: they repaid steps borrowed while coasting through a hitch
            /// (ADR-002 item 6), so the engine could not move to their target. Counted, not compared.</summary>
            public int Repaid;
            public double Rms => Count > 0 ? Math.Sqrt(SumSq / Count) : 0;
            public double LagRms => LagCount > 0 ? Math.Sqrt(LagSumSq / LagCount) : 0;
        }

        static readonly HashSet<string> s_Failed = new HashSet<string>();

        /// <summary>Main-thread time of the last successful Create (native build of the engine and its IRs).</summary>
        public static double LastCreateMs { get; private set; }
        static bool s_CrashFolderSet;
        static int s_LogFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Failed.Clear();
            s_CrashFolderSet = false;
            s_LogFrame = -1;
        }

        /// <summary>Client builds: VehicleEngines.Create(id) makes one of these (after VehicleEngines' own reset).</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Register() => VehicleEngines.Register(id => Create(id));

        /// <summary>The factory registered with VehicleEngines: the tier of -br-quality (QualityPreset.EngineAudio).</summary>
        public static IVehicleEngine Create(string engineId) => Create(engineId, QualityPreset.Current.EngineAudio);

        /// <summary>An engine for a catalog id at a tier, or null (unknown id, plugin missing, load failure; logged once per id).</summary>
        public static EngineSimVehicleEngine Create(string engineId, EngineAudioTier tier)
        {
            var def = EngineCatalog.Find(engineId);
            if (def == null) { Warn(engineId, $"no engine '{engineId}' in the catalog"); return null; }
            if (!s_CrashFolderSet) { s_CrashFolderSet = true; EngineSimEngine.SetCrashFolder(); }
            var o = EngineSimEngine.Options.ForPlayback();
            o.ApplyTier(tier);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var engine = EngineSimEngine.Create(def, o, out var err);
            if (engine == null) { Warn(engineId, err); return null; }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Log.Info($"[EngineSim] vehicle engine {engineId} ({tier}, {engine.Info.simFrequency} Hz, {engine.Info.fluidSteps} substeps, " +
                     $"IR {engine.Info.irTaps} taps) created in {ms:F1} ms");
            LastCreateMs = ms;
            engine.SetWorker(EngineSimEngine.WorkerPriorityFromCommandLine(tier), true);   // PLAN 3.4 by tier; M2 audio class
            return new EngineSimVehicleEngine(def, engine, tier);
        }

        static void Warn(string id, string why)
        {
            if (s_Failed.Add(id ?? "")) Log.Warn($"[EngineSim] vehicle engine '{id}' unavailable, the vehicle stays silent: {why}");
        }

        // ---- instance ----
        EngineSimEngine _engine;
        GameObject _go;
        AudioSource _source;
        EngineSimAudio _audio;
        bool _disposed;
        float _mapIdleJs = -1f, _mapRedlineJs = -1f;
        RpmMap _map;
        readonly long[] _targetStep = new long[64];
        readonly float[] _target = new float[64];
        long _lastCounted = -1;
        bool _following;
        Tracking _tracking;

        EngineSimVehicleEngine(EngineDef def, EngineSimEngine engine, EngineAudioTier tier)
        {
            Def = def;
            Tier = tier;
            _engine = engine;
            for (int i = 0; i < _targetStep.Length; i++) _targetStep[i] = -1;
            _go = new GameObject("EngineSim " + def.id);
            _source = _go.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0f;     // the local car (S-AUD-1): 2D; remote engines take the IAudioGenerator path (M4)
            _source.dopplerLevel = 0f;
            _source.priority = 0;
            _audio = _go.AddComponent<EngineSimAudio>();
            _audio.Attach(engine);
            _go.AddComponent<Lifetime>().Owner = this;
            _source.Play();
        }

        public EngineDef Def { get; }
        public EngineAudioTier Tier { get; }
        public string EngineId => Def.id;
        /// <summary>The engine-sim engine (null once disposed).</summary>
        public EngineSimEngine Engine => _engine;
        public AudioSource Source => _source;
        public EngineSimAudio Audio => _audio;
        public GameObject AudioObject => _go;
        public EnginePhase Phase { get; private set; } = EnginePhase.Off;
        public float DisplayRpm { get; private set; }
        /// <summary>The redline on the same scale as DisplayRpm (engine-sim's; the vehicle's redline maps to 97 % of it).
        /// For the HUD tach once IVehicleEngine carries it (For I, M2-handoff-VS).</summary>
        public float DisplayRedlineRpm => _engine != null && _engine.Info.redlineRadS > 0 ? (float)(_engine.Info.redlineRadS * 9.5492966) : Def.redlineRpm;
        /// <summary>The mapped rpm sent this step (0 when not following).</summary>
        public float TargetRpm { get; private set; }
        /// <summary>engine-sim's rpm in the newest result (step k - L).</summary>
        public float EngineRpm { get; private set; }
        public RpmMap Map => _map;
        public Tracking TrackingStats => _tracking;
        public void ResetTracking() => _tracking = default;

        /// <summary>The map for the vehicle's idle and redline: idle_es is the engine's own idle, redline_es its redline.</summary>
        public RpmMap MapFor(float idleJs, float redlineJs)
        {
            if (idleJs != _mapIdleJs || redlineJs != _mapRedlineJs)
            {
                _mapIdleJs = idleJs;
                _mapRedlineJs = redlineJs;
                float redlineEs = _engine != null && _engine.Info.redlineRadS > 0 ? (float)(_engine.Info.redlineRadS * 9.5492966) : Def.redlineRpm;
                float limitEs = _engine != null && _engine.Info.revLimitRadS > 0 ? (float)(_engine.Info.revLimitRadS * 9.5492966) : 0f;
                _map = new RpmMap(idleJs, redlineJs, Def.IdleRpm, redlineEs, limitEs);
            }
            return _map;
        }

        public void Step(in EngineState state, float dt)
        {
            if (_engine == null) return;
            var map = MapFor(state.IdleRpm, state.RedlineRpm);
            float mapped = map.Map(state.Rpm);
            bool wantsRun = state.Ignition && state.Rpm >= RunningShareOfIdle * map.IdleJs;
            // Key turned with the starter held: let engine-sim crank and catch on its own starter (the real cranking
            // sound) until it is near its idle, then follow. Once following, it follows until the vehicle stops it.
            float engineRpmNow = _engine.HaveResult ? (float)_engine.Last.rpm : 0f;
            bool cranking = wantsRun && !_following && state.Starter && engineRpmNow < CatchShareOfIdle * map.IdleEs;
            bool running = wantsRun && !cranking;
            _following = running;
            EspDrivelineIn d;
            if (running)
            {
                d = EspDrivelineIn.Of(EspMode.FollowRpm);
                d.followRpm = mapped;
                _engine.Starter = false;
            }
            else
            {
                d = EspDrivelineIn.Of(EspMode.Free);
                _engine.Starter = state.Starter;
            }
            _engine.Ignition = state.Ignition;
            _engine.Throttle = state.Throttle;
            long k = _engine.StepsKicked;   // the index this step's command gets
            var o = _engine.FixedStep(dt, d);
            if (_engine.StepsKicked > k)
            {
                int slot = (int)(k & 63);
                _targetStep[slot] = running ? k : -1;
                _target[slot] = mapped;
            }
            Phase = running ? EnginePhase.Following : (state.Ignition || state.Starter || o.rpm > 1 ? EnginePhase.Free : EnginePhase.Off);
            TargetRpm = running ? mapped : 0f;
            EngineRpm = _engine.HaveResult ? (float)o.rpm : 0f;
            DisplayRpm = running ? mapped : Mathf.Max(0f, EngineRpm);

            if (_engine.HaveResult && o.stepIndex > _lastCounted)
            {
                _lastCounted = o.stepIndex;
                int slot = (int)(o.stepIndex & 63);
                if (_targetStep[slot] == o.stepIndex && o.steps == 0) _tracking.Repaid++;
                else if (_targetStep[slot] == o.stepIndex)
                {
                    double e = o.rpm - _target[slot];
                    _tracking.Count++;
                    _tracking.SumSq += e * e;
                    _tracking.MaxAbs = Math.Max(_tracking.MaxAbs, Math.Abs(e));
                    if (running)
                    {
                        double lag = o.rpm - mapped;
                        _tracking.LagCount++;
                        _tracking.LagSumSq += lag * lag;
                        _tracking.LagMaxAbs = Math.Max(_tracking.LagMaxAbs, Math.Abs(lag));
                    }
                }
            }
            if (Time.frameCount != s_LogFrame) { s_LogFrame = Time.frameCount; EngineSimLog.Flush(); }
        }

        public void AttachTo(Transform exhaust)
        {
            if (_go == null) return;
            _go.transform.SetParent(exhaust, false);
            _go.transform.localPosition = Vector3.zero;
            _go.transform.localRotation = Quaternion.identity;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_source != null) _source.Stop();
            if (_audio != null) _audio.Detach();
            _engine?.Dispose();
            _engine = null;
            if (_go != null)
            {
                var life = _go.GetComponent<Lifetime>();
                if (life != null) life.Owner = null;
                if (Application.isPlaying) Object.Destroy(_go);
                else Object.DestroyImmediate(_go);
            }
            _go = null;
            _source = null;
            _audio = null;
            Phase = EnginePhase.Off;
            DisplayRpm = TargetRpm = EngineRpm = 0f;
        }

        /// <summary>
        /// Ties the native engine to its GameObject: destroyed with the vehicle, the engine stops too. While the game is
        /// paused (Time.timeScale 0: no FixedUpdate, so no commands) the AudioSource pauses with it, so the audio thread
        /// stops reading instead of running the ring dry; on resume the coasted audio plays out while the borrowed steps
        /// are repaid (ADR-002 item 6), with no underrun.
        /// </summary>
        sealed class Lifetime : MonoBehaviour
        {
            internal EngineSimVehicleEngine Owner;
            bool _paused;

            void Update()
            {
                var o = Owner;
                if (o == null || o._source == null) return;
                bool paused = Time.timeScale <= 0f;
                if (paused == _paused) return;
                _paused = paused;
                if (paused) o._source.Pause();
                else o._source.UnPause();
            }

            void OnDestroy()
            {
                var o = Owner;
                Owner = null;
                if (o == null) return;
                o._go = null;   // already being destroyed
                o.Dispose();
            }
        }
    }
}
