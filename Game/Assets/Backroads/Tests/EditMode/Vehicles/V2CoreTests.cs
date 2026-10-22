using System;
using System.IO;
using Backroads.Vehicles;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.Tests.EditMode.Vehicles
{
    /// <summary>
    /// BRV v2 core (card T3.5), the parts that need no simulation: the MF tyre tables against research 06 section 3.7,
    /// the v2 constants of the classic vehicles, snapshot version 2 with its version 1 reader (ADR-005), and the
    /// analytic test track's geometry.
    /// </summary>
    [TestFixture, Category("Vehicle"), Category("BrvV2")]
    public class VehicleV2Core
    {
        // 06 section 3.7: peak slip, CS = B C D, mu(0.5)/peak and mu(1)/peak (long); peak deg, CS, mu(15)/peak, mu(30)/peak (lat)
        static readonly (TyreClass c, double peak, double cs, double r1, double r2)[] Long =
        {
            (TyreClass.Road, 0.11, 20, 0.71, 0.62), (TyreClass.Slick, 0.10, 34, 0.76, 0.70), (TyreClass.AllTerrain, 0.13, 16, 0.77, 0.69),
            (TyreClass.MudTerrain, 0.16, 13, 0.85, 0.76), (TyreClass.Truck, 0.13, 15, 0.79, 0.70), (TyreClass.Gravel, 0.22, 9, 0.91, 0.82),
        };
        static readonly (TyreClass c, double peakDeg, double cs, double r1, double r2)[] Lat =
        {
            (TyreClass.Road, 7.5, 17, 0.95, 0.88), (TyreClass.Slick, 6.0, 30, 0.89, 0.83), (TyreClass.AllTerrain, 9.0, 13, 0.97, 0.92),
            (TyreClass.MudTerrain, 11, 10, 0.99, 0.95), (TyreClass.Truck, 11, 9.5, 0.99, 0.95), (TyreClass.Gravel, 15, 7.5, 1.00, 0.98),
        };

        static double ArgMax(MfCurve m, double hi, out double peak)
        {
            double best = 0; peak = -1;
            for (int i = 1; i <= 20000; i++)
            {
                double x = hi * i / 20000, v = m.Mu(x);
                if (v > peak) { peak = v; best = x; }
            }
            return best;
        }

        [Test]
        public void MfTablesMatch06()
        {
            foreach (var t in Long)
            {
                var m = TyreTables.Long(t.c);
                double x = ArgMax(m, 2, out double pk);
                Assert.AreEqual(t.peak, x, 0.005, $"{t.c} long: peak slip");
                Assert.AreEqual(m.D, pk, 1e-3, $"{t.c} long: peak = D");
                Assert.AreEqual(t.cs, m.Stiffness, 0.1, $"{t.c} long: CS");
                Assert.AreEqual(t.r1, m.Mu(0.5) / pk, 0.006, $"{t.c} long: mu(0.5)/peak");
                Assert.AreEqual(t.r2, m.Mu(1.0) / pk, 0.006, $"{t.c} long: mu(1)/peak");
                Assert.AreEqual(t.peak, m.peak, 1e-9, $"{t.c} long: tabulated peak");
            }
            const double Deg = Math.PI / 180;
            foreach (var t in Lat)
            {
                var m = TyreTables.Lat(t.c);
                double x = ArgMax(m, 1.5, out double pk);
                Assert.AreEqual(t.peakDeg, x / Deg, 0.2, $"{t.c} lat: peak slip angle");
                Assert.AreEqual(t.cs, m.Stiffness, 0.1, $"{t.c} lat: CS");
                Assert.AreEqual(t.r1, m.Mu(15 * Deg) / pk, 0.006, $"{t.c} lat: mu(15)/peak");
                Assert.AreEqual(t.r2, m.Mu(30 * Deg) / pk, 0.006, $"{t.c} lat: mu(30)/peak");
            }
            // surface multipliers: asphalt 1 for the road tyre, gravel 1 for the gravel tyre, mud scales with mudGrip
            Assert.AreEqual(1.0, TyreTables.SurfaceMu(4, TyreClass.Road, 1), 1e-12);
            Assert.AreEqual(1.0, TyreTables.SurfaceMu(5, TyreClass.Gravel, 1), 1e-12);
            Assert.AreEqual(2 * TyreTables.SurfaceMu(8, TyreClass.Truck, 0.5), TyreTables.SurfaceMu(8, TyreClass.Truck, 1.0), 1e-12);
            Assert.AreEqual(0.006, TyreTables.RollingResistance(4, TyreClass.Truck), 1e-12);
        }

        [Test]
        public void V2ConstantsFromClassic()
        {
            foreach (var id in ClassicVehicleDefs.M2)
            {
                var def = ClassicVehicleDefs.Get(id);
                var b = BrvDef.Create(def);
                var v = b.v2def;
                Assert.AreEqual(b.ratios.Length, v.gearRatios.Length, id);
                for (int g = 0; g < b.ratios.Length; g++)
                    Assert.AreEqual(b.ratios[g], v.gearRatios[g] * v.finalDrive, 1e-9 * b.ratios[g], $"{id} gear {g + 1}: gearbox x final drive = the JS overall ratio");
                Assert.AreEqual(1.0, v.gearRatios[v.gearRatios.Length - 1], 1e-12, $"{id}: top gear 1:1");
                Assert.AreEqual(b.revRatio, v.reverseRatio * v.finalDrive, 1e-9, $"{id}: reverse");
                Assert.AreEqual(def.brake * def.wheelR, v.brakeTorque, 1e-9, $"{id}: brake torque");
                Assert.AreEqual(def.mass * BrvSim.G / 4, v.fz0, 1e-9, $"{id}: rated load");
                Assert.Greater(v.crankInertia, 0); Assert.Greater(v.wheelInertiaFront, 0); Assert.Greater(v.wheelInertiaRear, 0);
                Assert.AreEqual(4, v.substeps, "06 section 3.4: 4 substeps");
                Assert.AreEqual(12, v.iterations, "06 section 3.4: 12 PGS iterations");
                Assert.IsFalse(b.v2, $"{id}: BrvDef.Create never turns v2 on (BrvFleet.Add does, behind the flag)");
            }
            Assert.AreEqual(TyreClass.Road, BrvDef.Create(ClassicVehicleDefs.Get("hatch")).v2def.tyre);
            Assert.AreEqual(TyreClass.Truck, BrvDef.Create(ClassicVehicleDefs.Get("truck")).v2def.tyre);
        }

        [Test]
        public void WheelStateFitsTenWheels()
        {
            var l = new FixedList4096Bytes<BrvWheel>();
            Assert.GreaterOrEqual(l.Capacity, BrvDef.MaxWheels, "BrvWheel grew past the FixedList4096Bytes capacity for 10 wheels");
        }

        static BrvState SampleState(out BrvDef d)
        {
            d = BrvDef.Create(ClassicVehicleDefs.Get("pickup"));
            var s = BrvSim.Create(d);
            var rnd = new System.Random(7);
            s.pos = new double3(rnd.NextDouble(), rnd.NextDouble(), rnd.NextDouble());
            s.vel = new double3(3.25, -0.5, 12.125);
            s.gear = 3; s.rpm = 2345.5; s.lowRange = true; s.steps = 1234;
            s.dl = new BrvDriveline
            {
                init = true, crankOmega = 245.75, crankAngle = 1.25, driveOmega = 61.5, clutch = 0.875,
                lamClutch = 0.0625, lamDiffFront = -0.03125, lamDiffRear = 0.5, lockRear = true, absOff = true, engineTorque = 301.25, tcCut = 0.375,
            };
            for (int i = 0; i < s.wheels.Length; i++)
            {
                ref var w = ref s.wheels.ElementAt(i);
                w.omega = 20 + i; w.lamB = 0.1 * i; w.lamT = -0.2 * i; w.lamL = 0.3; w.lamM = 0.01;
                w.anchorL = 1e-3 * i; w.anchorS = -2e-3; w.anchorB = 0.05; w.absCut = 0.25 * i; w.kappa = -0.08; w.alpha = 0.02;
            }
            return s;
        }

        [Test]
        public void SnapshotV2RoundTripAndV1Reader()
        {
            var s = SampleState(out var d);
            var v2 = BrvSnapshot.ToBytes(s);
            Assert.AreEqual(BrvSnapshot.Version, BitConverter.ToUInt16(v2, 4));
            var back = BrvSnapshot.FromBytes(v2, d.WheelCount);
            CollectionAssert.AreEqual(v2, BrvSnapshot.ToBytes(back), "version 2 round trip");
            Assert.AreEqual(s.dl.crankOmega, back.dl.crankOmega);
            Assert.AreEqual(s.dl.tcCut, back.dl.tcCut);
            Assert.IsTrue(back.dl.lockRear && back.dl.absOff && back.dl.init);
            Assert.AreEqual(s.wheels[3].absCut, back.wheels[3].absCut);

            // version 1 (frozen at m2): the same bytes as before T3.5, and readable by this build
            var v1 = BrvSnapshot.ToBytes(s, BrvSnapshot.Version1);
            Assert.AreEqual(1659, v1.Length, "ADR-005: 1,659 bytes for a four-wheel vehicle");
            Assert.AreEqual(1659 + 1 + 9 * 8 + 4 * 10 * 8, v2.Length, "version 2 = version 1 + the shaft block");
            for (int i = 6; i < v1.Length; i++) Assert.AreEqual(v1[i], v2[i], $"byte {i}: version 2 must start with the version 1 layout");
            var old = BrvSnapshot.FromBytes(v1, d.WheelCount);
            Assert.AreEqual(s.pos, old.pos); Assert.AreEqual(s.gear, old.gear); Assert.AreEqual(s.steps, old.steps);
            Assert.AreEqual(s.wheels[2].omega, old.wheels[2].omega);
            Assert.IsFalse(old.dl.init, "a version 1 snapshot carries no v2 block (the first v2 step re-syncs it)");
            Assert.AreEqual(0, old.dl.crankOmega);
            CollectionAssert.AreEqual(v1, BrvSnapshot.ToBytes(old, BrvSnapshot.Version1), "version 1 round trip");

            // refusals
            var bad = (byte[])v2.Clone(); bad[4] = 3;
            Assert.Throws<InvalidDataException>(() => BrvSnapshot.FromBytes(bad, d.WheelCount), "unknown version");
            Assert.Throws<InvalidDataException>(() => BrvSnapshot.FromBytes(v2, 6), "wheel count");
        }

        [Test]
        public void TestTrackGeometry()
        {
            for (int i = 0; i < BrvTestTrack.Grades.Length; i++)
            {
                var m = BrvTestTrack.RampMid(i);
                double g = (BrvTestTrack.Height(m.x, m.y + 1) - BrvTestTrack.Height(m.x, m.y - 1)) / 2;
                Assert.AreEqual(BrvTestTrack.Grades[i], g, 1e-9, $"ramp {i}");
                Assert.AreEqual(4, BrvTestTrack.Surface(m.x, m.y), "ramps are asphalt");
            }
            double z = BrvTestTrack.StepsZ0 + BrvTestTrack.StepPitch * 3.5;
            Assert.AreEqual(BrvTestTrack.StepHeight, BrvTestTrack.Height(0, z + BrvTestTrack.StepPitch) - BrvTestTrack.Height(0, z), 1e-9, "150 mm steps");
            Assert.AreEqual(8, BrvTestTrack.Surface(50, 45), "mud");
            Assert.AreEqual(BrvTestTrack.Ground - BrvTestTrack.PitDepth, BrvTestTrack.Height(BrvTestTrack.PitCentre.x, BrvTestTrack.PitCentre.y), 1e-9, "pit");
            Assert.AreEqual(4, BrvTestTrack.Surface(BrvTestTrack.PadStart.x, BrvTestTrack.PadStart.y), "pad");
            Assert.Greater(BrvTestTrack.Ground, BrvSim.WATER_Y + 1, "the track is above the water line");

            using var grid = BrvTestTrack.Build();
            int k = Backroads.World.WorldGrid.Index(1024 + 100 * 2, 1024 + 40 * 2);   // the pit centre vertex
            Assert.AreEqual((float)(BrvTestTrack.Ground - BrvTestTrack.PitDepth), grid.H[k]);
            Assert.AreEqual(grid.H[k], grid.Base[k], "BASE = H at the start (no sinkage)");
        }

        [Test]
        public void AlignedPlacementFollowsTheSlope()
        {
            var w = BrvTestTrack.BuildWorld();
            try
            {
                var d = BrvDef.Create(ClassicVehicleDefs.Get("pickup"));
                var s = BrvSim.Create(d);
                var m = BrvTestTrack.RampMid(3);
                BrvSim.PlaceAligned(ref s, d, w.View, m.x, m.y, 0);
                double pitch = Math.Atan(BrvTestTrack.Grades[3]);
                Assert.AreEqual(Math.Sin(pitch), s.ez.y, 1e-6, "nose up the 35 % ramp");
                Assert.AreEqual(Math.Cos(pitch), s.ey.y, 1e-6);
                Assert.AreEqual(0, s.ex.y, 1e-9, "no roll");
                BrvSim.PlaceAligned(ref s, d, w.View, m.x, m.y, Math.PI);
                Assert.AreEqual(-Math.Sin(pitch), s.ez.y, 1e-6, "nose down when facing downhill");
                var q = BrvSim.FromBasis(new double3(0, 0, -1), new double3(0, 1, 0), new double3(1, 0, 0));
                Assert.AreEqual(Math.Sin(Math.PI / 4), q.y, 1e-12, "yaw 90 degrees");
                Assert.AreEqual(Math.Cos(Math.PI / 4), q.w, 1e-12);
            }
            finally { var g = w.Grid; w.Dispose(); g.Dispose(); }
        }
    }
}
