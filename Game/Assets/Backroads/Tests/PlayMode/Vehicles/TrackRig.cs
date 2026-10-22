using System;
using Backroads.Vehicles;
using Unity.Mathematics;

namespace Backroads.Tests.PlayMode.Vehicles
{
    /// <summary>
    /// A fleet on the BRV test track (no canon, no deformation) for the v2 lane gates: add vehicles as V0 or v2, place
    /// them on the track features, feed driver input and step.
    /// </summary>
    sealed class TrackRig : IDisposable
    {
        public readonly BrvWorldData World;
        public readonly BrvFleet Fleet;
        readonly bool _ownsWorld;

        public TrackRig(int capacity = BrvFleet.DefaultCapacity, BrvWorldData shared = null)
        {
            _ownsWorld = shared == null;
            World = shared ?? BrvTestTrack.BuildWorld();
            Fleet = new BrvFleet(World, false, capacity) { NoDeformation = true };
        }

        public static void DisposeWorld(BrvWorldData w)
        {
            var g = w.Grid;
            w.Dispose();
            g.Dispose();
        }

        /// <summary>Adds a classic vehicle stepping with BRV v2 (or V0).</summary>
        public int Add(string id, bool v2 = true)
        {
            bool was = BrvFeatures.V2Driveline;
            BrvFeatures.V2Driveline = v2;
            try
            {
                int i = Fleet.Add(ClassicVehicleDefs.Get(id));
                if (Fleet.IsV2(i) != v2) throw new InvalidOperationException($"{id}: v2 = {Fleet.IsV2(i)}");
                return i;
            }
            finally { BrvFeatures.V2Driveline = was; }
        }

        /// <summary>Places vehicle i aligned with the ground at (x, z) JS, heading yaw, then settles it with the brakes on.</summary>
        public void Place(int i, double x, double z, double yaw, int settle = 240, double extra = 0)
        {
            var w = World.View;
            BrvSim.PlaceAligned(ref Fleet.State(i), Fleet.Def(i), w, x, z, yaw, extra);
            ref var s = ref Fleet.State(i);
            s.engineOn = true;
            s.fuel = 1;
            if (settle > 0) Fleet.Settle(i, settle);
        }

        public ref BrvState S(int i) => ref Fleet.State(i);

        /// <summary>The driver's pedals and steering for vehicle i (vehicleControls runs for it every step).</summary>
        public void Input(int i, double fwd = 0, double back = 0, double steer = 0, double hand = 0)
        {
            var inp = Fleet.GetInput(i);
            inp.active = true; inp.ignition = true;
            inp.fwd = fwd; inp.back = back; inp.steer = steer; inp.hand = hand;
            Fleet.SetInput(i, inp);
        }

        public void Step(int n = 1)
        {
            for (int k = 0; k < n; k++) Fleet.Step();
        }

        public void Dispose()
        {
            Fleet.Dispose();
            if (_ownsWorld) DisposeWorld(World);
        }

        public static bool Finite(in BrvState s)
        {
            bool F(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
            bool F3(double3 v) => F(v.x) && F(v.y) && F(v.z);
            if (!F3(s.pos) || !F3(s.vel) || !F3(s.angVel) || !F(s.quat.x) || !F(s.quat.y) || !F(s.quat.z) || !F(s.quat.w)) return false;
            if (!F(s.dl.crankOmega) || !F(s.dl.driveOmega) || !F(s.rpm)) return false;
            for (int k = 0; k < s.wheels.Length; k++)
            {
                var w = s.wheels[k];
                if (!F(w.omega) || !F(w.load) || !F(w.len) || !F(w.lamT) || !F(w.lamL) || !F(w.lamB)) return false;
            }
            return true;
        }

        /// <summary>Yaw rate about the body's up axis, rad/s.</summary>
        public static double YawRate(in BrvState s) => math.dot(s.angVel, s.ey);
    }
}
