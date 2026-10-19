using System;
using System.Collections.Generic;
using System.Diagnostics;
using Backroads.Core;
using Backroads.World;
using UnityEngine;

namespace Backroads.Vehicles
{
    /// <summary>
    /// Steps every BRV V0 vehicle in the scene at exactly 1/120 s (FixedUpdate, Time.fixedDeltaTime = 1/120): the
    /// player's controls, the vehicle step and the vehicle pairs (VehicleStepJob through <see cref="BrvFleet"/>), rut
    /// calls into Backroads.Deform.StampSinks.Current, then each engine's Step with the classic engine's state.
    /// Shared code (client and server); the presentation (models, interpolated bodies, IDrivenVehicle) is in
    /// Backroads.Vehicles.Client. Measures its own main-thread time per rendered frame (PLAN 6.1 M2 lane gate).
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class VehicleSimSystem : MonoBehaviour
    {
        public static VehicleSimSystem Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        readonly List<SimVehicle> _vehicles = new List<SimVehicle>();
        BrvWorldData _world;
        SimVehicle _player;

        public BrvFleet Fleet { get; private set; }
        public IReadOnlyList<SimVehicle> Vehicles => _vehicles;
        public SimVehicle Player => _player;
        public bool Paused;

        /// <summary>The player switched vehicles (old, new).</summary>
        public event Action<SimVehicle, SimVehicle> PlayerChanged;
        /// <summary>A vehicle knocked a tree over (props.js knockTree): for the vegetation presenter.</summary>
        public event Action<TreeKnock> TreeKnocked;

        // ---- timing (simulation main-thread time per rendered frame) ----
        readonly Stopwatch _sw = new Stopwatch();
        double _frameMs;
        int _frameSteps;
        readonly float[] _frameHistory = new float[4096];
        int _frameCount;
        public int StepsLastFrame { get; private set; }
        public double SimMsLastFrame { get; private set; }
        public long TotalSteps { get; private set; }

        /// <summary>Creates the system on a GameObject, bound to the loaded canon world (CanonRuntime).</summary>
        public static VehicleSimSystem Create(GameObject host)
        {
            var sys = host.AddComponent<VehicleSimSystem>();
            sys.Bind(CanonRuntime.Grid, CanonRuntime.Colliders, CanonRuntime.Asset != null ? CanonRuntime.Asset.vegetation : null);
            return sys;
        }

        public void Bind(WorldGrid grid, ColliderGrid colliders, VegetationSet vegetation)
        {
            if (Fleet != null) throw new InvalidOperationException("already bound");
            _world = BrvWorldData.Create(grid, colliders, vegetation);
            Fleet = new BrvFleet(_world);
            Fleet.TreeKnocked += k => TreeKnocked?.Invoke(k);
        }

        void Awake()
        {
            if (Current != null && Current != this) Log.Warn("a second VehicleSimSystem was created");
            Current = this;
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            foreach (var v in _vehicles) v.Dispose();
            _vehicles.Clear();
            Fleet?.Dispose();
            Fleet = null;
            _world?.Dispose();
            _world = null;
        }

        /// <summary>Adds a vehicle, places it (Unity-space position, Unity yaw in degrees) and settles it for 240 steps.</summary>
        public SimVehicle Spawn(ClassicVehicleDef def, Vector3 unityPos, float unityYawDeg, int settleSteps = 240)
        {
            int i = Fleet.Add(def);
            var v = new SimVehicle(this, i, def);
            _vehicles.Add(v);
            Fleet.Place(i, -unityPos.x, unityPos.z, -unityYawDeg * Mathf.Deg2Rad);
            if (settleSteps > 0) Fleet.Settle(i, settleSteps);
            Fleet.Park(i);
            return v;
        }

        /// <summary>Spawns at a browser-game parking bay (JS world space).</summary>
        public SimVehicle SpawnAtJs(ClassicVehicleDef def, double xJs, double zJs, double yawJs, int settleSteps = 240)
        {
            int i = Fleet.Add(def);
            var v = new SimVehicle(this, i, def);
            _vehicles.Add(v);
            Fleet.Place(i, xJs, zJs, yawJs);
            if (settleSteps > 0) Fleet.Settle(i, settleSteps);
            Fleet.Park(i);
            return v;
        }

        public SimVehicle Find(string id)
        {
            foreach (var v in _vehicles) if (v.Def.id == id) return v;
            return null;
        }

        /// <summary>enterVehicle(v): the player drives v (the previous vehicle is parked with its engine off).</summary>
        public void SetPlayer(SimVehicle v)
        {
            if (ReferenceEquals(v, _player)) return;
            var old = _player;
            old?.SetPlayer(false);
            _player = v;
            v?.SetPlayer(true);
            PlayerChanged?.Invoke(old, v);
        }

        /// <summary>The nearest other vehicle within <paramref name="maxDist"/> m of the player's (for E, get in/out).</summary>
        public SimVehicle NearestOther(SimVehicle from, float maxDist)
        {
            SimVehicle best = null;
            float bd = maxDist;
            foreach (var v in _vehicles)
            {
                if (v == from) continue;
                float d = Vector3.Distance(v.Position, from.Position);
                if (d < bd) { bd = d; best = v; }
            }
            return best;
        }

        void FixedUpdate()
        {
            if (Fleet == null || Paused) return;
            _sw.Restart();
            for (int i = 0; i < _vehicles.Count; i++) Fleet.SetInput(i, _vehicles[i].TakeStepInput());
            Fleet.Step();
            TotalSteps++;
            for (int i = 0; i < _vehicles.Count; i++)
            {
                var v = _vehicles[i];
                ref var s = ref Fleet.State(i);
                v.LastImpact = s.impact;
                s.impact = 0;
                v.LastEvents = Fleet.TakeEvents(i);
                v.AfterStep(BrvSim.STEP);
            }
            // E: get out of this vehicle into the nearest one parked within 12 m (in M2 there is no walking yet)
            if (_player != null && _player.TakeGetInOutPresses() > 0)
            {
                if (_player.State.speed < 3)
                {
                    var next = NearestOther(_player, 12f);
                    if (next != null) SetPlayer(next);
                }
            }
            _sw.Stop();
            _frameMs += _sw.Elapsed.TotalMilliseconds;
            _frameSteps++;
        }

        void Update()
        {
            // a frame boundary: the fixed steps of this frame have run
            SimMsLastFrame = _frameMs;
            StepsLastFrame = _frameSteps;
            if (_frameSteps > 0 || _frameCount > 0)
            {
                _frameHistory[_frameCount % _frameHistory.Length] = (float)_frameMs;
                _frameCount++;
            }
            _frameMs = 0;
            _frameSteps = 0;
        }

        /// <summary>The p-th percentile (0..100) of the simulation time per rendered frame over the last 4,096 frames.</summary>
        public float SimMsPercentile(float p)
        {
            int n = Math.Min(_frameCount, _frameHistory.Length);
            if (n == 0) return 0;
            var a = new float[n];
            Array.Copy(_frameHistory, a, n);
            Array.Sort(a);
            int k = Mathf.Clamp(Mathf.CeilToInt(p / 100f * n) - 1, 0, n - 1);
            return a[k];
        }

        public void ResetTiming() { _frameCount = 0; }

        /// <summary>Interpolation factor between the last two fixed steps for this rendered frame.</summary>
        public static float Alpha => Time.fixedDeltaTime > 0 ? Mathf.Clamp01((Time.time - Time.fixedTime) / Time.fixedDeltaTime) : 1f;
    }
}
