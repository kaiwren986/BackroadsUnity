using System;
using Backroads.Vehicles;
using NUnit.Framework;
using Unity.Collections;

namespace Backroads.Tests.EditMode.Vehicles
{
    /// <summary>
    /// BRV v2 head start (card T2.3): the shaft graph and its PGS rows, behind BrvFeatures.V2Driveline (off by default).
    /// A free shaft follows T = I dω/dt; a two-shaft clutch row exchanges angular momentum at its torque capacity and
    /// conserves the total until the shafts lock at the common speed.
    /// </summary>
    [TestFixture, Category("Vehicle")]
    public class VehicleV2
    {
        const double H = 1.0 / 480;   // 1/120 s x 4 substeps (06 section 3.4)
        bool _was;

        [SetUp] public void On() { _was = BrvFeatures.V2Driveline; BrvFeatures.V2Driveline = true; }
        [TearDown] public void Off() => BrvFeatures.V2Driveline = _was;

        [Test]
        public void FlagOffByDefault()
        {
            BrvFeatures.V2Driveline = false;
            Assert.Throws<InvalidOperationException>(() => ShaftGraph.Create(Allocator.Temp).Dispose());
            Assert.IsFalse(Backroads.Core.CommandLine.Has(BrvFeatures.V2Flag), "the editor was started with the v2 flag");
        }

        [Test]
        public void FreeShaft()
        {
            var g = ShaftGraph.Create(Allocator.Temp);
            try
            {
                int s = g.AddShaft(0.2, 10);
                var sh = g.Shafts[s]; sh.torque = 50; g.Shafts[s] = sh;
                var body = default(RowBody);
                for (int k = 0; k < 240; k++) g.Substep(H, ref body, false);
                double t = 240 * H;
                Assert.AreEqual(10 + 50 / 0.2 * t, g.Shafts[s].omega, 1e-9, "omega = omega0 + T t / I");
                // explicit (symplectic) Euler: angle = sum of omega_k h
                double angle = 0, w = 10;
                for (int k = 0; k < 240; k++) { w += 50 / 0.2 * H; angle += w * H; }
                Assert.AreEqual(angle, g.Shafts[s].angle, 1e-9);
                Assert.AreEqual(0.2 * g.Shafts[s].omega, g.TotalAngularMomentum(), 1e-12);
            }
            finally { g.Dispose(); }
        }

        [Test]
        public void ClutchExchangesAngularMomentum()
        {
            var g = ShaftGraph.Create(Allocator.Temp);
            try
            {
                const double Ia = 0.25, Ib = 0.05, w0 = 300, cap = 400;
                int a = g.AddShaft(Ia, w0), b = g.AddShaft(Ib, 0);
                g.AddClutch(a, b, cap);
                double L0 = g.TotalAngularMomentum();
                double tLock = w0 / (cap * (1 / Ia + 1 / Ib));        // slip at full capacity until the speeds meet
                double wLock = L0 / (Ia + Ib);
                var body = default(RowBody);
                int lockedAt = -1;
                for (int k = 1; k <= 60; k++)
                {
                    double wbBefore = g.Shafts[b].omega;
                    g.Substep(H, ref body, false);
                    double L = g.TotalAngularMomentum();
                    Assert.AreEqual(L0, L, 1e-9 * L0, $"substep {k}: total angular momentum");
                    double dLb = (g.Shafts[b].omega - wbBefore) * Ib;
                    Assert.LessOrEqual(dLb, cap * H * (1 + 1e-9), $"substep {k}: exchange above the capacity");
                    if (lockedAt < 0 && Math.Abs(g.Shafts[a].omega - g.Shafts[b].omega) < 1e-6) lockedAt = k;
                    if (lockedAt < 0) Assert.AreEqual(cap * H, dLb, 1e-9, $"substep {k}: slipping, the clutch passes its full capacity");
                }
                TestContext.WriteLine($"clutch locks at substep {lockedAt} ({lockedAt * H * 1000:F2} ms), expected {tLock * 1000:F2} ms; common speed {g.Shafts[a].omega:F4} rad/s (expected {wLock:F4})");
                Assert.That(lockedAt, Is.InRange((int)Math.Floor(tLock / H), (int)Math.Ceiling(tLock / H) + 1), "lock time");
                Assert.AreEqual(wLock, g.Shafts[a].omega, 1e-6);
                Assert.AreEqual(wLock, g.Shafts[b].omega, 1e-6);
            }
            finally { g.Dispose(); }
        }

        [Test]
        public void GearReflectsInertia()
        {
            var g = ShaftGraph.Create(Allocator.Temp);
            try
            {
                const double Iin = 0.3, Iout = 2.0, R = 3.5, T = 200;
                int i = g.AddShaft(Iin), o = g.AddShaft(Iout);
                g.AddGear(i, o, R);
                var sh = g.Shafts[i]; sh.torque = T; g.Shafts[i] = sh;
                var body = default(RowBody);
                for (int k = 0; k < 480; k++) g.Substep(H, ref body, false);
                // locked gear: the output sees T R through I_in R^2 + I_out
                double alphaOut = T * R / (Iin * R * R + Iout);
                Assert.AreEqual(alphaOut * 1.0, g.Shafts[o].omega, 1e-3 * alphaOut);
                Assert.AreEqual(R * g.Shafts[o].omega, g.Shafts[i].omega, 1e-6 * g.Shafts[i].omega);
            }
            finally { g.Dispose(); }
        }

        [Test]
        public void ClassicDrivelineSkeleton()
        {
            var g = ShaftGraph.Create(Allocator.Temp);
            try
            {
                var def = ClassicVehicleDefs.Get("pickup");
                var d = DrivelineDef.FromClassic(def);
                var s = DrivelineBuilder.Build(ref g, d, 1);
                Assert.AreEqual(3 + 4, g.Shafts.Length);
                var cr = g.Shafts[s.crank]; cr.torque = 300; g.Shafts[s.crank] = cr;
                var body = default(RowBody);
                for (int k = 0; k < 240; k++) g.Substep(H, ref body, false);
                double overall = d.gearRatios[0] * d.finalDrive;
                var bdef = BrvDef.Create(def);
                Assert.AreEqual(bdef.ratios[0], overall, 1e-9, "gearbox x final drive = the JS first-gear overall ratio");
                for (int w = 0; w < 4; w++)
                    Assert.AreEqual(g.Shafts[s.crank].omega / overall, g.Shafts[s.wheels[w]].omega, 1e-3 * g.Shafts[s.crank].omega / overall, $"wheel {w}");
                TestContext.WriteLine($"pickup skeleton: crank {g.Shafts[s.crank].omega:F2} rad/s, wheels {g.Shafts[s.wheels[0]].omega:F3} rad/s after 0.5 s at 300 N m");
            }
            finally { g.Dispose(); }
        }
    }
}
