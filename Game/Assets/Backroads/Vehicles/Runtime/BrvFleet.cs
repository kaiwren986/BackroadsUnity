using System;
using System.Collections.Generic;
using Backroads.Deform;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace Backroads.Vehicles
{
    /// <summary>
    /// A set of BRV V0 vehicles sharing one world (no MonoBehaviour: the simulation core of PLAN 2.1, usable by
    /// VehicleSimSystem, the golden-trace replay, tests and later the server). Owns the native state and runs
    /// <see cref="VehicleStepJob"/> (Burst, on the calling thread) once per fixed step; rut calls are replayed into
    /// <see cref="Sink"/> (default Backroads.Deform.StampSinks.Current; null = no deformation) right after the step.
    /// </summary>
    public sealed unsafe class BrvFleet : IDisposable
    {
        public const int Capacity = 16;

        NativeArray<BrvState> _states;
        NativeArray<BrvDef> _defs;
        NativeArray<BrvInput> _inputs;
        NativeArray<BrvControlEvents> _events;
        NativeList<RutCall> _ruts;
        NativeList<TreeKnock> _knocks;
        readonly List<ClassicVehicleDef> _classic = new List<ClassicVehicleDef>();
        readonly BrvWorldData _world;
        readonly bool _ownsWorld;

        /// <summary>Run the step through Burst (default) or as plain managed C# (same code).</summary>
        public bool UseBurst = true;

        /// <summary>Where rut() calls go; null falls back to StampSinks.Current. Set <see cref="NoDeformation"/> to drop them.</summary>
        public IStampSink Sink;
        public bool NoDeformation;

        /// <summary>A vehicle knocked a tree over (props.js knockTree): for the vegetation presenter.</summary>
        public event Action<TreeKnock> TreeKnocked;

        public int Count { get; private set; }
        public long RutCalls { get; private set; }
        public long StepsRun { get; private set; }
        public BrvWorldData World => _world;

        public BrvFleet(BrvWorldData world, bool ownsWorld = false)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _ownsWorld = ownsWorld;
            _states = new NativeArray<BrvState>(Capacity, Allocator.Persistent);
            _defs = new NativeArray<BrvDef>(Capacity, Allocator.Persistent);
            _inputs = new NativeArray<BrvInput>(Capacity, Allocator.Persistent);
            _events = new NativeArray<BrvControlEvents>(Capacity, Allocator.Persistent);
            _ruts = new NativeList<RutCall>(64, Allocator.Persistent);
            _knocks = new NativeList<TreeKnock>(4, Allocator.Persistent);
        }

        public ClassicVehicleDef Classic(int i) => _classic[i];

        public int IndexOf(string id)
        {
            for (int i = 0; i < Count; i++) if (_classic[i].id == id) return i;
            return -1;
        }

        public int Add(ClassicVehicleDef def)
        {
            if (Count >= Capacity) throw new InvalidOperationException("fleet full");
            var d = BrvDef.Create(def);
            _defs[Count] = d;
            _states[Count] = BrvSim.Create(d);
            _inputs[Count] = default;
            _classic.Add(def);
            return Count++;
        }

        /// <summary>The live state of vehicle i (by reference: valid until the fleet is disposed).</summary>
        public ref BrvState State(int i)
        {
            if ((uint)i >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(i));
            return ref UnsafeUtility.ArrayElementAsRef<BrvState>(_states.GetUnsafePtr(), i);
        }

        public ref readonly BrvDef Def(int i)
        {
            if ((uint)i >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(i));
            return ref UnsafeUtility.ArrayElementAsRef<BrvDef>(_defs.GetUnsafeReadOnlyPtr(), i);
        }

        /// <summary>Snapshot out: a copy of the complete state.</summary>
        public BrvState GetSnapshot(int i) => State(i);

        /// <summary>Snapshot in: replaces the complete state.</summary>
        public void SetSnapshot(int i, in BrvState s) => State(i) = s;

        public void SetInput(int i, in BrvInput input) => _inputs[i] = input;

        public BrvInput GetInput(int i) => _inputs[i];

        /// <summary>Control events since the last call (and clears them).</summary>
        public BrvControlEvents TakeEvents(int i)
        {
            var e = _events[i];
            _events[i] = default;
            return e;
        }

        public void Place(int i, double xJs, double zJs, double yawJs, double extra = 0)
        {
            var w = _world.View;
            BrvSim.Place(ref State(i), Def(i), w, xJs, zJs, yawJs, extra);
        }

        public void Park(int i) => BrvSim.Park(ref State(i));

        public void ResetUpright(int i)
        {
            var w = _world.View;
            BrvSim.ResetUpright(ref State(i), Def(i), w);
        }

        /// <summary>settle(steps): brakes on, steps this vehicle alone, then no interpolation jump.</summary>
        public void Settle(int i, int steps = 240)
        {
            ref var s = ref State(i);
            s.ctrl.brake = 1; s.ctrl.handbrake = 1;
            for (int k = 0; k < steps; k++) Run(i);
            ref var s2 = ref State(i);
            s2.prevPos = s2.pos;
            s2.prevQuat = s2.quat;
        }

        /// <summary>One fixed step of the whole fleet: controls of the active inputs, every vehicle, then the pairs.</summary>
        public void Step()
        {
            Run(-1);
            // one-shot presses are consumed by the step
            for (int i = 0; i < Count; i++)
            {
                var inp = _inputs[i];
                inp.lightsPresses = inp.resetPresses = inp.lowRangePresses = inp.winchPresses = 0;
                _inputs[i] = inp;
            }
        }

        void Run(int only)
        {
            _ruts.Clear();
            _knocks.Clear();
            var job = new VehicleStepJob
            {
                States = _states, Defs = _defs, Inputs = _inputs, Events = _events, World = _world.View,
                Ruts = _ruts, Knocks = _knocks, Count = Count, Only = only, Dt = BrvSim.STEP,
            };
            if (UseBurst) job.Run();
            else job.Execute();
            StepsRun++;
            Flush();
        }

        void Flush()
        {
            if (_ruts.Length > 0)
            {
                RutCalls += _ruts.Length;
                var sink = NoDeformation ? null : Sink ?? StampSinks.Current;
                if (sink != null)
                    for (int k = 0; k < _ruts.Length; k++)
                    {
                        var r = _ruts[k];
                        sink.Rut(r.x, r.z, r.radius, r.depth, r.rate, r.wearAdd, r.berm, r.fx, r.fz);
                    }
            }
            if (_knocks.Length > 0 && TreeKnocked != null)
                for (int k = 0; k < _knocks.Length; k++) TreeKnocked(_knocks[k]);
        }

        // ------------------------------------------------------------------ Unity-space views of a vehicle

        /// <summary>COM position in Unity space.</summary>
        public static UnityEngine.Vector3 UnityPos(double3 js) => new UnityEngine.Vector3((float)-js.x, (float)js.y, (float)js.z);

        /// <summary>Body rotation in Unity space (BrCoords.Rot).</summary>
        public static UnityEngine.Quaternion UnityRot(double4 q) => new UnityEngine.Quaternion((float)q.x, (float)-q.y, (float)-q.z, (float)q.w);

        public static UnityEngine.Vector3 UnityDir(double3 js) => new UnityEngine.Vector3((float)-js.x, (float)js.y, (float)js.z);

        public void Dispose()
        {
            if (_states.IsCreated) _states.Dispose();
            if (_defs.IsCreated) _defs.Dispose();
            if (_inputs.IsCreated) _inputs.Dispose();
            if (_events.IsCreated) _events.Dispose();
            if (_ruts.IsCreated) _ruts.Dispose();
            if (_knocks.IsCreated) _knocks.Dispose();
            if (_ownsWorld) _world.Dispose();
        }
    }
}
