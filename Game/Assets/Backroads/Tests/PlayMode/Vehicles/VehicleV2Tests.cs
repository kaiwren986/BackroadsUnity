using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Vehicles;
using NUnit.Framework;
using Unity.Mathematics;

namespace Backroads.Tests.PlayMode.Vehicles
{
    /// <summary>
    /// BRV v2 lane gates (card T3.5, PLAN 6.1 M3) on the analytic test track: Vehicle.HillHold, Vehicle.AbsStop,
    /// Vehicle.OpenDiffAirborne, Vehicle.LockedDiffTurningCircle and Vehicle.StabilityFuzz (nightly; a short version runs
    /// on every merge). Category BrvV2 (the M3 nightly gate step unity.playmode.brv-v2).
    /// </summary>
    public partial class Vehicle
    {
        static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        const double Step = BrvSim.STEP;

        static void Report(string name, string text)
        {
            var path = Path.Combine(ArtifactsDir(), name);
            File.WriteAllText(path, text);
            TestContext.WriteLine("report: " + path);
        }

        // ------------------------------------------------------------------ hill hold

        /// <summary>
        /// Every M2 vehicle on the 10, 20, 30 and 35 % ramps, facing up and down, the driver's feet off the pedals (the
        /// auto-hold brake, as a driver waiting on a hill; no sleeping, the vehicle is driven): after 2 s to settle, the
        /// creep over the next 60 s is below 1 mm/s (06 P5).
        /// </summary>
        [Test, Category("BrvV2")]
        public void HillHold()
        {
            var world = BrvTestTrack.BuildWorld();
            var sb = new StringBuilder();
            sb.AppendLine("# Vehicle.HillHold (BRV v2)").AppendLine();
            sb.AppendLine("Driver's feet off the pedals (auto-hold brake 0.7), 2 s to settle, then 60 s. Creep = displacement / 60 s.").AppendLine();
            sb.AppendLine("| Grade | Facing | Vehicle | Creep (mm/s) | Max speed (mm/s) | Result |").AppendLine("|---|---|---|---:|---:|---|");
            var fails = new List<string>();
            double worst = 0;
            try
            {
                for (int gi = 0; gi < BrvTestTrack.Grades.Length; gi++)
                    foreach (bool up in new[] { true, false })
                    {
                        using var rig = new TrackRig(8, world);
                        var mid = BrvTestTrack.RampMid(gi);
                        var ids = ClassicVehicleDefs.M2;
                        var idx = new int[ids.Length];
                        for (int k = 0; k < ids.Length; k++)
                        {
                            idx[k] = rig.Add(ids[k]);
                            rig.Place(idx[k], mid.x - 7.5 + 5 * k, mid.y, up ? 0 : Math.PI);
                            rig.Input(idx[k]);
                        }
                        rig.Step(240);
                        var p0 = idx.Select(i => rig.S(i).pos).ToArray();
                        var vmax = new double[ids.Length];
                        for (int n = 0; n < 7200; n++)
                        {
                            rig.Step();
                            for (int k = 0; k < ids.Length; k++) vmax[k] = Math.Max(vmax[k], math.length(rig.S(idx[k]).vel));
                        }
                        for (int k = 0; k < ids.Length; k++)
                        {
                            var s = rig.S(idx[k]);
                            double creep = math.length(s.pos - p0[k]) / 60.0;
                            worst = Math.Max(worst, creep);
                            bool ok = creep < 1e-3 && TrackRig.Finite(s);
                            sb.AppendLine(string.Format(Ci, "| {0:P0} | {1} | {2} | {3:F4} | {4:F3} | {5} |", BrvTestTrack.Grades[gi], up ? "up" : "down", ids[k], creep * 1000, vmax[k] * 1000, ok ? "pass" : "FAIL"));
                            if (!ok) fails.Add(string.Format(Ci, "{0} on {1:P0} facing {2}: creep {3:F3} mm/s", ids[k], BrvTestTrack.Grades[gi], up ? "up" : "down", creep * 1000));
                        }
                    }
            }
            finally { TrackRig.DisposeWorld(world); }
            sb.AppendLine().AppendLine(string.Format(Ci, "Worst creep {0:F4} mm/s (gate < 1 mm/s).", worst * 1000));
            Report("brv-v2-hillhold.md", sb.ToString());
            TestContext.WriteLine(string.Format(Ci, "HillHold: worst creep {0:F4} mm/s", worst * 1000));
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        // ------------------------------------------------------------------ ABS stop

        /// <summary>Run one straight stop from <paramref name="kmh"/> on the pad with the foot brake full on; returns the distance (m).</summary>
        static double BrakeStop(BrvWorldData world, string id, double kmh, bool v2, bool abs, out double seconds, out bool finite)
        {
            using var rig = new TrackRig(2, world);
            int i = rig.Add(id, v2);
            rig.Place(i, BrvTestTrack.PadStart.x, BrvTestTrack.PadStart.y, 0);
            if (v2) rig.Fleet.SetAbs(i, abs);
            rig.Fleet.SetRolling(i, kmh / 3.6);
            rig.Input(i, back: 1);
            var p0 = rig.S(i).pos;
            int n = 0;
            finite = true;
            while (n < 120 * 20)
            {
                rig.Step();
                n++;
                if (!TrackRig.Finite(rig.S(i))) { finite = false; break; }
                // stopped: forward speed down to zero (the JS driver aid would shift into reverse 0.22 s later)
                if (rig.S(i).fwdSpeed < 0.05) break;
            }
            seconds = n * Step;
            var p = rig.S(i).pos;
            return math.length(new double2(p.x - p0.x, p.z - p0.z));
        }

        /// <summary>ABS stop from 100 km/h on dry asphalt in 36-45 m (the hatch, the road-tyre car); the others reported.</summary>
        [Test, Category("BrvV2")]
        public void AbsStop()
        {
            var world = BrvTestTrack.BuildWorld();
            var sb = new StringBuilder();
            sb.AppendLine("# Vehicle.AbsStop (BRV v2)").AppendLine();
            sb.AppendLine("Straight stop on the asphalt pad, foot brake full on from the speed given. Gate: the hatch from 100 km/h with ABS in 36-45 m.").AppendLine();
            sb.AppendLine("| Vehicle | From | Model | Distance (m) | Time (s) |").AppendLine("|---|---:|---|---:|---:|");
            double gate = double.NaN;
            try
            {
                foreach (var (id, kmh) in new[] { ("hatch", 100.0), ("rally", 100.0), ("pickup", 100.0), ("truck", 80.0) })
                {
                    foreach (var (v2, abs, label) in new[] { (true, true, "v2, ABS"), (true, false, "v2, no ABS"), (false, false, "V0 (JS)") })
                    {
                        double dist = BrakeStop(world, id, kmh, v2, abs, out double t, out bool fin);
                        Assert.IsTrue(fin, $"{id} {label}: NaN/Inf");
                        sb.AppendLine(string.Format(Ci, "| {0} | {1:F0} km/h | {2} | {3:F2} | {4:F2} |", id, kmh, label, dist, t));
                        TestContext.WriteLine(string.Format(Ci, "{0} {1}: {2:F2} m in {3:F2} s from {4:F0} km/h", id, label, dist, t, kmh));
                        if (id == "hatch" && v2 && abs) gate = dist;
                    }
                }
            }
            finally { TrackRig.DisposeWorld(world); }
            Report("brv-v2-abs.md", sb.ToString());
            Assert.That(gate, Is.InRange(36.0, 45.0), "ABS stop from 100 km/h (hatch, dry asphalt)");
        }

        // ------------------------------------------------------------------ open diff, one wheel in the air

        /// <summary>
        /// Raises the ground in a square of half-size <paramref name="half"/> m around (x, z) to height y (H, H0 and BASE):
        /// a jack stand under a body point, for tests.
        /// </summary>
        static void JackStand(BrvWorldData world, double x, double z, double half, float y)
        {
            var g = world.Grid;
            const double H = Backroads.World.WorldGrid.Half, C = Backroads.World.WorldGrid.Cell;
            int i0 = (int)Math.Ceiling((x - half + H) / C), i1 = (int)Math.Floor((x + half + H) / C);
            int j0 = (int)Math.Ceiling((z - half + H) / C), j1 = (int)Math.Floor((z + half + H) / C);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int k = Backroads.World.WorldGrid.Index(i, j);
                    g.H[k] = y; g.H0[k] = y; g.Base[k] = y;
                }
        }

        /// <summary>
        /// The rally and the truck (rear-drive, open rear diffs) on three wheels: the rear right wheel hangs over the
        /// 2 m deep wheel pit and the body rests on a jack stand under its rear bottom centre, level, as in a workshop.
        /// At full throttle in first the open differential spins the free wheel up to the engine's limit, monotonically
        /// and without oscillation, while the rear left wheel on the ground does not spin (it gets the same small torque).
        /// </summary>
        [Test, Category("BrvV2")]
        public void OpenDiffAirborne()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Vehicle.OpenDiffAirborne (BRV v2)").AppendLine();
            sb.AppendLine("Rear right wheel over the 2 m deep wheel pit, the body on a jack stand under its rear bottom centre; full throttle for 6 s in first, open rear diff. " +
                          "Expected free-wheel speed at the limiter: twice the drive shaft speed over the final drive (the wheel on the ground stands still).").AppendLine();
            var fails = new List<string>();
            foreach (var id in new[] { "rally", "truck" })
            {
                var world = BrvTestTrack.BuildWorld();
                try
                {
                    using var rig = new TrackRig(2, world);
                    int i = rig.Add(id);
                    var d = rig.Fleet.Def(i);
                    var l = d.wheels[3];                     // rear right (vehicle.js layout: FL, FR, RL, RR)
                    double x = BrvTestTrack.PitCentre.x - l.mx, z = BrvTestTrack.PitCentre.y - l.mz;
                    // settle level on flat ground beside the pit, put the jack where the rear bottom centre will be, move over
                    const double Off = 8;
                    rig.Place(i, x + Off, z, 0);
                    ref var s0 = ref rig.S(i);
                    var h = d.hull[d.hull.Length - 5];       // bottom rear centre (BrvDef hull order)
                    var p = BrvSim.ToWorld(s0, h.x, h.y, h.z);
                    JackStand(world, p.x - Off, p.z, 0.75, (float)(p.y - 0.01));
                    s0.pos.x -= Off; s0.prevPos = s0.pos;
                    BrvSim.Wake(ref s0);
                    rig.Fleet.Settle(i, 480);
                    string Wheels() { var st = rig.S(i); var o = new StringBuilder(); for (int k = 0; k < st.wheels.Length; k++) o.Append(string.Format(Ci, " w{0}:{1}/{2:F0}N/{3:F1}", k, st.wheels[k].contact ? "c" : "-", st.wheels[k].load, st.wheels[k].omega)); return o.ToString(); }
                    sb.AppendLine($"- {id} after settling:{Wheels()}");
                    if (rig.S(i).wheels[3].contact) { fails.Add($"{id}: the rear right wheel should hang over the pit"); continue; }
                    if (!rig.S(i).wheels[2].contact) { fails.Add($"{id}: the rear left wheel should be on the ground"); continue; }
                    rig.Input(i, fwd: 1.0);
                    var w = new List<double>();
                    double slipMax = 0;
                    var p0 = rig.S(i).pos;
                    for (int n = 0; n < 120 * 6; n++)
                    {
                        rig.Step();
                        var st = rig.S(i);
                        w.Add(st.wheels[3].omega);
                        var rl = st.wheels[2];
                        slipMax = Math.Max(slipMax, Math.Abs(rl.omega * d.wheels[2].r - rl.vl));
                        if (st.wheels[3].contact) { fails.Add($"{id}: the airborne wheel touched down at step {n}"); break; }
                    }
                    double final = w.Skip(w.Count - 60).Average();
                    // monotone: after the first 0.1 s no step loses more than 0.1 % of the final speed
                    double worstDrop = 0;
                    for (int k = 12; k < w.Count; k++) worstDrop = Math.Max(worstDrop, w[k - 1] - w[k]);
                    // oscillation: sign changes of the step-to-step change above a 0.1 % noise floor, and the band once settled
                    int flips = 0, last = 0;
                    for (int k = 12; k < w.Count; k++)
                    {
                        double dw = w[k] - w[k - 1];
                        int sg = Math.Abs(dw) < 1e-3 * Math.Abs(final) ? 0 : Math.Sign(dw);
                        if (sg != 0 && last != 0 && sg != last) flips++;
                        if (sg != 0) last = sg;
                    }
                    var tail = w.Skip(w.Count - 240).ToList();
                    double band = tail.Max() - tail.Min();
                    int t90 = w.FindIndex(v => v >= 0.9 * final);
                    double moved = math.length(rig.S(i).pos - p0);
                    var v2d = d.v2def;
                    double expect = 2 * (v2d.redlineRpm * Math.PI / 30) / (v2d.gearRatios[0] * v2d.finalDrive);
                    string line = string.Format(Ci, "{0}: free wheel {1:F1} rad/s after 6 s (expected about {2:F1} at the limiter; 90 % at {3:F2} s), largest drop {4:F4} rad/s, {5} reversals above 0.1 %, " +
                        "band over the last 2 s {9:F3} rad/s ({10:P2}); rear left wheel slip at most {6:F3} m/s; chassis moved {7:F3} m; engine {8:F0} rpm",
                        id, final, expect, t90 * Step, worstDrop, flips, slipMax, moved, rig.S(i).rpm, band, band / final);
                    sb.AppendLine("- " + line);
                    sb.AppendLine($"- {id} at the end:{Wheels()}, gear {rig.S(i).gear}, clutch {rig.S(i).dl.clutch:F2}, drive {rig.S(i).dl.driveOmega:F1} rad/s");
                    TestContext.WriteLine(line);
                    if (final < 0.85 * expect) fails.Add($"{id}: the free wheel only reached {final:F1} rad/s (expected about {expect:F1})");
                    if (worstDrop > 1e-3 * final) fails.Add($"{id}: the free wheel slowed by {worstDrop:F4} rad/s in a step while spinning up");
                    if (flips > 2) fails.Add($"{id}: {flips} reversals (oscillation)");
                    if (band > 0.01 * final) fails.Add($"{id}: the settled free wheel wanders by {band:F3} rad/s");
                    if (slipMax > 0.2) fails.Add($"{id}: the wheel on the ground slipped at {slipMax:F3} m/s");
                    // the wheel on the ground carries the same torque as the free one (its inertia while it spins up): a nudge
                    if (moved > 0.25) fails.Add($"{id}: the chassis moved {moved:F3} m");
                }
                finally { TrackRig.DisposeWorld(world); }
            }
            Report("brv-v2-opendiff.md", sb.ToString());
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        // ------------------------------------------------------------------ locked diff, turning circle

        static double TurningRadius(BrvWorldData world, string id, bool lockRear, bool lockFront, double speed, out double measuredSpeed, bool v2 = true)
        {
            using var rig = new TrackRig(2, world);
            int i = rig.Add(id, v2);
            rig.Place(i, BrvTestTrack.SkidpadCentre.x, BrvTestTrack.SkidpadCentre.y, 0);
            rig.Fleet.SetLockers(i, lockFront, lockRear);
            double sumV = 0, sumW = 0; int n = 0;
            for (int k = 0; k < 120 * 20; k++)
            {
                double v = rig.S(i).fwdSpeed;
                double thr = BrvSim.Clamp(0.15 + 0.6 * (speed - v), 0, 0.7);
                double brk = v > speed + 0.5 ? 0.3 : 0;
                rig.Input(i, thr, brk, 1.0);
                rig.Step();
                if (k >= 120 * 10) { sumV += math.length(rig.S(i).vel); sumW += Math.Abs(TrackRig.YawRate(rig.S(i))); n++; }
            }
            measuredSpeed = sumV / n;
            return (sumV / n) / (sumW / n);
        }

        /// <summary>Full lock at walking pace (2 m/s): a locked rear diff scrubs and widens the circle (rally, truck); both lockers on the pickup.</summary>
        [Test, Category("BrvV2")]
        public void LockedDiffTurningCircle()
        {
            var world = BrvTestTrack.BuildWorld();
            var sb = new StringBuilder();
            sb.AppendLine("# Vehicle.LockedDiffTurningCircle (BRV v2)").AppendLine();
            sb.AppendLine("| Vehicle | Open: radius (m) | Locked: radius (m) | Wider by |").AppendLine("|---|---:|---:|---:|");
            var fails = new List<string>();
            try
            {
                foreach (var (id, front) in new[] { ("rally", false), ("truck", false), ("pickup", true) })
                {
                    double rOpen = TurningRadius(world, id, false, false, 2.0, out double vOpen);
                    double rLock = TurningRadius(world, id, true, front, 2.0, out double vLock);
                    double wider = rLock / rOpen - 1;
                    sb.AppendLine(string.Format(Ci, "| {0}{1} | {2:F2} ({3:F2} m/s) | {4:F2} ({5:F2} m/s) | {6:P1} |", id, front ? " (front + rear)" : "", rOpen, vOpen, rLock, vLock, wider));
                    TestContext.WriteLine(string.Format(Ci, "{0}: open {1:F2} m, locked {2:F2} m ({3:P1})", id, rOpen, rLock, wider));
                    if (!(wider > 0.02)) fails.Add(string.Format(Ci, "{0}: locked {1:F2} m vs open {2:F2} m", id, rLock, rOpen));
                }
            }
            finally { TrackRig.DisposeWorld(world); }
            Report("brv-v2-lockeddiff.md", sb.ToString());
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        // ------------------------------------------------------------------ stability fuzz

        struct FuzzResult
        {
            public int seed; public string vehicle; public bool ok; public string why;
            public double maxV, maxW, maxWheel, maxRpm;
        }

        static FuzzResult FuzzOne(BrvWorldData world, int seed, double seconds)
        {
            var rnd = new System.Random(seed * 7919 + 17);
            var ids = ClassicVehicleDefs.M2;
            string id = ids[rnd.Next(ids.Length)];
            var r = new FuzzResult { seed = seed, vehicle = id, ok = true };
            using var rig = new TrackRig(2, world);
            int i = rig.Add(id);
            double x, z;
            switch (rnd.Next(6))
            {
                case 0: x = rnd.NextDouble() * 260 - 130; z = -400 + rnd.NextDouble() * 280; break;      // pad
                case 1: { int g = rnd.Next(4); x = BrvTestTrack.RampLaneX(g) + rnd.NextDouble() * 12 - 6; z = 10 + rnd.NextDouble() * 110; break; }
                case 2: x = rnd.NextDouble() * 16 - 8; z = 10 + rnd.NextDouble() * 60; break;            // steps
                case 3: x = 32 + rnd.NextDouble() * 36; z = 22 + rnd.NextDouble() * 46; break;           // mud
                case 4: x = 90 + rnd.NextDouble() * 20; z = 30 + rnd.NextDouble() * 20; break;           // pit apron
                default: x = 200 + rnd.NextDouble() * 250; z = -400 + rnd.NextDouble() * 800; break;      // grass
            }
            rig.Place(i, x, z, rnd.NextDouble() * 2 * Math.PI, settle: 0, extra: rnd.NextDouble() * 1.5);
            ref var s0 = ref rig.S(i);
            if (rnd.NextDouble() < 0.5) rig.Fleet.SetRolling(i, (rnd.NextDouble() < 0.85 ? 1 : -0.3) * rnd.NextDouble() * 30);
            s0.angVel = new double3(rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5, rnd.NextDouble() - 0.5) * 4;
            s0.lowRange = rnd.NextDouble() < 0.2;
            rig.Fleet.SetLockers(i, rnd.NextDouble() < 0.3, rnd.NextDouble() < 0.3);
            rig.Fleet.SetAbs(i, rnd.NextDouble() < 0.8);
            int steps = (int)(seconds * 120), next = 0;
            for (int n = 0; n < steps; n++)
            {
                if (n >= next)
                {
                    double fwd = rnd.NextDouble() < 0.6 ? rnd.NextDouble() : 0;
                    double back = rnd.NextDouble() < 0.3 ? rnd.NextDouble() : 0;
                    double steer = rnd.NextDouble() < 0.7 ? rnd.NextDouble() * 2 - 1 : 0;
                    double hand = rnd.NextDouble() < 0.1 ? 1 : 0;
                    rig.Input(i, fwd, back, steer, hand);
                    if (rnd.NextDouble() < 0.05) { var inp = rig.Fleet.GetInput(i); inp.lowRangePresses = 1; rig.Fleet.SetInput(i, inp); }
                    if (rnd.NextDouble() < 0.05) rig.Fleet.SetLockers(i, rnd.NextDouble() < 0.5, rnd.NextDouble() < 0.5);
                    next = n + 36 + rnd.Next(324);
                }
                rig.Step();
                ref var s = ref rig.S(i);
                if (!TrackRig.Finite(s)) { r.ok = false; r.why = $"NaN/Inf at step {n}"; break; }
                double v = math.length(s.vel), w = math.length(s.angVel);
                r.maxV = Math.Max(r.maxV, v);
                r.maxW = Math.Max(r.maxW, w);
                r.maxRpm = Math.Max(r.maxRpm, Math.Abs(s.rpm));
                for (int k = 0; k < s.wheels.Length; k++) r.maxWheel = Math.Max(r.maxWheel, Math.Abs(s.wheels[k].omega));
                if (v > 100) { r.ok = false; r.why = string.Format(Ci, "|v| = {0:F1} m/s at step {1}", v, n); break; }
                if (w > 50) { r.ok = false; r.why = string.Format(Ci, "|w| = {0:F1} rad/s at step {1}", w, n); break; }
            }
            return r;
        }

        static void Fuzz(int seeds, double seconds, string report)
        {
            var world = BrvTestTrack.BuildWorld();
            var results = new List<FuzzResult>();
            var sw = Stopwatch.StartNew();
            try { for (int k = 0; k < seeds; k++) results.Add(FuzzOne(world, k, seconds)); }
            finally { TrackRig.DisposeWorld(world); }
            double ms = sw.Elapsed.TotalMilliseconds;
            var bad = results.Where(r => !r.ok).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"# Vehicle.StabilityFuzz (BRV v2): {seeds} seeds x {seconds:F0} s").AppendLine();
            sb.AppendLine("Random vehicle, place (pad, ramps, steps, mud, pit, grass), heading, drop, speed, spin, low range, lockers, ABS; random pedals, steering, handbrake, low-range and locker toggles every 0.3-3 s.").AppendLine();
            sb.AppendLine(string.Format(Ci, "- Failures: {0} (gate 0: no NaN/Inf, |v| <= 100 m/s, |w| <= 50 rad/s)", bad.Count));
            sb.AppendLine(string.Format(Ci, "- Largest |v| {0:F1} m/s, largest chassis |w| {1:F2} rad/s, largest wheel |w| {2:F0} rad/s, largest engine {3:F0} rpm",
                results.Max(r => r.maxV), results.Max(r => r.maxW), results.Max(r => r.maxWheel), results.Max(r => r.maxRpm)));
            sb.AppendLine(string.Format(Ci, "- Wall time {0:F1} s ({1:F1} us per vehicle step including the checks)", ms / 1000, ms * 1000 / (seeds * seconds * 120)));
            foreach (var r in bad.Take(20)) sb.AppendLine($"- seed {r.seed} ({r.vehicle}): {r.why}");
            Report(report, sb.ToString());
            TestContext.WriteLine(sb.ToString());
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(20).Select(r => $"seed {r.seed} ({r.vehicle}): {r.why}")));
        }

        /// <summary>The nightly gate: 1,000 seeds x 60 s.</summary>
        [Test, Category("BrvV2"), Category("Slow"), Timeout(3_600_000)]
        public void StabilityFuzz() => Fuzz(1000, 60, "brv-v2-fuzz.md");

        /// <summary>The same fuzz, short (every merge).</summary>
        [Test, Category("BrvV2")]
        public void StabilityFuzzQuick() => Fuzz(40, 10, "brv-v2-fuzz-quick.md");
    }
}
