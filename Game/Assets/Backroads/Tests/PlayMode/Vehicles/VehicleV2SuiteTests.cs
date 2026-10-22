using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Vehicles;
using Backroads.Vehicles.Reference;
using NUnit.Framework;
using Unity.Mathematics;

namespace Backroads.Tests.PlayMode.Vehicles
{
    /// <summary>
    /// BRV v2 (card T3.5, step 3): the Unity self-trace baseline [S17], the v2 snapshot round trip, and the vehicle
    /// conformance suite v0 (the same manoeuvres for every vehicle and both models, one report).
    /// </summary>
    public partial class Vehicle
    {
        const string RecordSelfTracesEnv = "BR_RECORD_SELF_TRACES";

        static string SelfTraceDir(bool v2) =>
            Path.Combine(RepoRoot, "Game", "Assets", "Backroads", "Tests", "PlayMode", "Vehicles", "SelfTraces~", v2 ? "v2" : "v0");

        // ------------------------------------------------------------------ self-trace baseline [S17]

        /// <summary>
        /// The six golden-trace inputs replayed by Unity itself on the canon test world (pure v1, as the oracle traces) with
        /// V0 and with BRV v2, against the committed self-trace baseline (SelfTraces~/v0, v2): every trace gate within
        /// 2 % of the baseline (the winch completes), positions within 5 cm on the traces off the mud. This is the
        /// regression net once the world or the model moves away from the browser game (PLAN S17). Record a new baseline
        /// with BR_RECORD_SELF_TRACES=1 (then review the report and commit the files).
        /// </summary>
        [Test, Category("BrvV2")]
        public void SelfTraces()
        {
            var a = Canon();
            bool record = Environment.GetEnvironmentVariable(RecordSelfTracesEnv) == "1";
            var sb = new StringBuilder();
            sb.AppendLine("# Vehicle.SelfTraces (Unity self-trace baseline, S17)").AppendLine();
            sb.AppendLine($"Golden-trace inputs replayed by Unity on canon v1 (no plates, no traffic), against SelfTraces~ ({(record ? "RECORDING a new baseline" : "comparing")}).").AppendLine();
            sb.AppendLine("| Model | Trace | Gate | Baseline | Now | Delta | Max position difference | Result |").AppendLine("|---|---|---|---:|---:|---:|---:|---|");
            var fails = new List<string>();
            bool was = BrvFeatures.V2Driveline;
            try
            {
                foreach (bool v2 in new[] { false, true })
                {
                    Directory.CreateDirectory(SelfTraceDir(v2));
                    foreach (var id in TraceIds)
                    {
                        var input = GoldenTraceInput.Load(Path.Combine(RepoRoot, "tools", "oracle", "traces", id + ".json"));
                        BrvFeatures.V2Driveline = v2;
                        var world = FreshWorld(a);
                        GoldenTraceSamples run;
                        try { run = GoldenTraceReplay.Run(input, new GoldenTraceReplay.Options { World = world, Parking = a.placements.parking, Sink = new JsRutSink(world.Grid) }); }
                        finally { DisposeWorld(world); BrvFeatures.V2Driveline = was; }
                        string model = v2 ? "v2" : "V0";
                        var path = Path.Combine(SelfTraceDir(v2), id + ".json");
                        if (record)
                        {
                            File.WriteAllText(path, run.ToJson($"Unity self-trace ({model}) of {id} on canon v1, {DateTime.Now:yyyy-MM-dd}"));
                            run = GoldenTraceSamples.LoadReference(path);
                        }
                        if (!File.Exists(path)) { fails.Add($"{model} {id}: no baseline (record with {RecordSelfTracesEnv}=1)"); continue; }
                        var baseline = GoldenTraceSamples.LoadReference(path);
                        var g = GoldenTraceGates.Evaluate(input, baseline, run);
                        GoldenTraceGates.PerSecond(baseline, run, out _, out _, out _, out double maxPos);
                        bool mud = id == "hatch_bog_10s" || id == "winch_15m_tree";
                        // a gate the model never reaches in the trace's window (v2 is slower than the JS launch) is undefined
                        // in both: then the positions decide
                        bool undefinedBoth = g.relative && double.IsNaN(g.expected) && double.IsNaN(g.measured);
                        bool ok = undefinedBoth || (g.relative ? Math.Abs(g.DeltaPercent) <= 2.0 : g.pass);
                        if (!mud && maxPos > 0.05) ok = false;
                        sb.AppendLine(g.relative
                            ? $"| {model} | {id} | {g.name} | {g.expected:F3} {g.unit} | {g.measured:F3} {g.unit} | {g.DeltaPercent:+0.00;-0.00} % | {maxPos:E2} m | {(ok ? "pass" : "FAIL")} |"
                            : $"| {model} | {id} | {g.name} | | {(g.pass ? "completes" : "does not")} | | {maxPos:E2} m | {(ok ? "pass" : "FAIL")} |");
                        if (!ok) fails.Add($"{model} {id}: {g} (max position difference {maxPos:E2} m)");
                    }
                }
            }
            finally { BrvFeatures.V2Driveline = was; }
            Report("self-traces.md", sb.ToString());
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        // ------------------------------------------------------------------ v2 snapshot round trip

        /// <summary>
        /// Snapshot version 2 mid-drive (pickup on the track: launch, a turn, into the mud, braking), restored from bytes
        /// and continued: the restored run follows the uninterrupted one to within 1 mm and ends byte-identical.
        /// </summary>
        [Test, Category("BrvV2")]
        public void SnapshotRoundTripV2()
        {
            var world = BrvTestTrack.BuildWorld();
            try
            {
                using var rig = new TrackRig(2, world);
                int i = rig.Add("pickup");
                rig.Place(i, 50, 0, 0);                   // 20 m before the mud
                void Drive(int k)
                {
                    double t = k * Step;
                    rig.Input(i, fwd: t < 6 ? 0.6 : 0, back: t >= 6 ? 0.8 : 0, steer: t > 2 && t < 3 ? 0.3 : 0);
                    rig.Step();
                }
                const int At = 300, End = 960;
                for (int k = 0; k < At; k++) Drive(k);
                var bytes = BrvSnapshot.ToBytes(rig.Fleet.GetSnapshot(i));
                var path1 = new List<double3>();
                for (int k = At; k < End; k++) { Drive(k); path1.Add(rig.S(i).pos); }
                var end1 = BrvSnapshot.ToBytes(rig.Fleet.GetSnapshot(i));
                rig.Fleet.SetSnapshot(i, BrvSnapshot.FromBytes(bytes, rig.Fleet.Def(i).WheelCount));
                double maxErr = 0;
                for (int k = At; k < End; k++) { Drive(k); maxErr = Math.Max(maxErr, math.length(rig.S(i).pos - path1[k - At])); }
                var end2 = BrvSnapshot.ToBytes(rig.Fleet.GetSnapshot(i));
                TestContext.WriteLine($"SnapshotRoundTripV2: {bytes.Length} bytes at step {At}, max position difference {maxErr:E2} m over {End - At} steps; ends at z {rig.S(i).pos.z:F1} m (mud from z {BrvTestTrack.MudZ0})");
                Assert.Less(maxErr, 0.001);
                CollectionAssert.AreEqual(end1, end2, "the restored run ends in a different state");
            }
            finally { TrackRig.DisposeWorld(world); }
        }

        // ------------------------------------------------------------------ conformance suite v0

        sealed class Conf
        {
            public string id; public bool v2;
            public double t060 = double.NaN, vmax30, brake80 = double.NaN, latG = double.NaN, radius = double.NaN;
            public double hill20 = double.NaN, hill35 = double.NaN, steps = double.NaN, mud15 = double.NaN;
            public bool finite = true;
        }

        static bool Run(TrackRig rig, int i, int steps, Func<int, (double fwd, double back, double steer)> drive, Func<bool> stop = null)
        {
            for (int k = 0; k < steps; k++)
            {
                var (f, b, s) = drive(k);
                rig.Input(i, f, b, s);
                rig.Step();
                if (!TrackRig.Finite(rig.S(i))) return false;
                if (stop != null && stop()) return true;
            }
            return true;
        }

        static Conf Conform(BrvWorldData world, string id, bool v2)
        {
            var c = new Conf { id = id, v2 = v2 };
            // launch 0-60 km/h and the speed at the end of the pad (380 m), straight along its east edge
            using (var rig = new TrackRig(2, world))
            {
                int i = rig.Add(id, v2);
                rig.Place(i, BrvTestTrack.LaunchX, BrvTestTrack.PadZ0 + 5, 0);
                double z0 = rig.S(i).pos.z;
                c.finite &= Run(rig, i, 120 * 60, k =>
                {
                    if (double.IsNaN(c.t060) && rig.S(i).fwdSpeed * 3.6 >= 60) c.t060 = k * Step;
                    c.vmax30 = Math.Max(c.vmax30, rig.S(i).fwdSpeed * 3.6);
                    return (1, 0, 0);
                }, () => rig.S(i).pos.z - z0 >= 380);
            }
            // brake 80-0 km/h (foot brake full; ABS on v2)
            c.brake80 = BrakeStop(world, id, 80, v2, true, out _, out bool fin);
            c.finite &= fin;
            // skidpad: follow the 30 m circle, speed rising 0.3 m/s per second; best 1 s mean lateral acceleration
            // while the path stays within 1.5 m of the circle
            using (var rig = new TrackRig(2, world))
            {
                int i = rig.Add(id, v2);
                var ctr = BrvTestTrack.SkidpadCentre;
                double R = BrvTestTrack.SkidpadRadius;
                rig.Place(i, ctr.x + R, ctr.y, 0);            // on the circle, heading +z, the centre on the right (JS +x is left)
                var dd = rig.Fleet.Def(i);
                double wheelbase = dd.wheels[0].z - dd.wheels[2].z;
                var lat = new Queue<double>();
                double best = 0, sum = 0;
                bool lost = false;
                c.finite &= Run(rig, i, 120 * 60, k =>
                {
                    ref var s = ref rig.S(i);
                    double target = Math.Min(4 + 0.3 * k * Step, 40);
                    double dx = s.pos.x - ctr.x, dz = s.pos.z - ctr.y, r = Math.Sqrt(dx * dx + dz * dz);
                    // tangent for counter-clockwise travel in JS x-z (from +x heading +z)
                    double tx = -dz / r, tz = dx / r;
                    double fx = s.ez.x, fz = s.ez.z, fl = Math.Sqrt(fx * fx + fz * fz);
                    double headErr = Math.Atan2(fx * tz - fz * tx, fx * tx + fz * tz) ;      // + = heading left of the tangent
                    double v = s.fwdSpeed;
                    double ff = wheelbase / R * (1 + 0.055 * Math.Abs(v)) / dd.steer;   // kinematic lock for the circle (steer +1 = right)
                    double steer = BrvSim.Clamp(ff + 0.15 * (r - R) + 1.5 * headErr, -1, 1);
                    double thr = BrvSim.Clamp(0.3 + 0.5 * (target - v), 0, 1), brk = v > target + 1 ? 0.3 : 0;
                    double a = Math.Abs(TrackRig.YawRate(s)) * math.length(s.vel);
                    if (Math.Abs(r - R) < 1.5 && k * Step > 5) { lat.Enqueue(a); sum += a; if (lat.Count > 120) sum -= lat.Dequeue(); if (lat.Count == 120) best = Math.Max(best, sum / 120); }
                    else if (k * Step > 15) lost = true;
                    return (thr, brk, steer);
                }, () => lost);
                c.latG = best / BrvSim.G;
            }
            // turning circle: full lock at 2 m/s
            c.radius = TurningRadius(world, id, false, false, 2.0, out _, v2);
            // hill starts: from rest facing up the 20 % and 35 % ramps, full throttle (low range on the 35 %)
            foreach (int gi in new[] { 1, 3 })
            {
                using var rig = new TrackRig(2, world);
                int i = rig.Add(id, v2);
                var m = BrvTestTrack.RampMid(gi);
                rig.Place(i, m.x, BrvTestTrack.RampZ0 + 6, 0);
                if (gi == 3) rig.S(i).lowRange = true;
                double z0 = rig.S(i).pos.z;
                int at = -1;
                c.finite &= Run(rig, i, 120 * 30, k =>
                {
                    if (at < 0 && rig.S(i).pos.z - z0 >= 25) at = k;
                    return (1, 0, 0);
                }, () => at >= 0);
                double secs = at >= 0 ? at * Step : double.NaN;
                if (gi == 1) c.hill20 = secs; else c.hill35 = secs;
            }
            // 150 mm steps at walking pace: time to the top
            using (var rig = new TrackRig(2, world))
            {
                int i = rig.Add(id, v2);
                rig.Place(i, BrvTestTrack.StepsX, BrvTestTrack.StepsZ0 - 8, 0);
                double top = BrvTestTrack.StepsZ0 + BrvTestTrack.StepPitch * (BrvTestTrack.StepCount + 1);
                int done = -1;
                c.finite &= Run(rig, i, 120 * 40, k =>
                {
                    double v = rig.S(i).fwdSpeed;
                    if (done < 0 && rig.S(i).pos.z >= top) done = k;
                    return (BrvSim.Clamp(0.25 + 0.5 * (2.0 - v), 0, 1), v > 3 ? 0.3 : 0, 0);
                }, () => done >= 0);
                c.steps = done >= 0 ? done * Step : double.NaN;
            }
            // mud: into the 0.6 m mud patch at 5 m/s, half throttle, distance after 15 s
            using (var rig = new TrackRig(2, world))
            {
                int i = rig.Add(id, v2);
                double x = (BrvTestTrack.MudX0 + BrvTestTrack.MudX1) / 2;
                rig.Place(i, x, BrvTestTrack.MudZ0 - 4, 0);
                rig.Fleet.SetRolling(i, 5);
                double z0 = rig.S(i).pos.z;
                c.finite &= Run(rig, i, 120 * 15, k => (0.5, 0, 0));
                c.mud15 = rig.S(i).pos.z - z0;
            }
            return c;
        }

        /// <summary>
        /// Vehicle conformance suite v0 (card T3.5 step 3): for each M2 vehicle, V0 and v2 side by side on the test track
        /// - 0-60 km/h, speed after 380 m, 80-0 km/h braking, skidpad lateral g on the 30 m circle, turning circle at full
        /// lock, hill starts on 20 % and 35 % (low range), the 150 mm steps, 15 s in the mud. Writes
        /// artifacts/vehicles/conformance-v0.md. Checks v2 for sanity only (finite; launches, brakes, corners, climbs the
        /// 20 % ramp and the steps); the numbers are the tuning baseline for M4.
        /// </summary>
        [Test, Category("BrvV2")]
        public void ConformanceV0()
        {
            var world = BrvTestTrack.BuildWorld();
            var rows = new List<Conf>();
            try
            {
                foreach (var id in ClassicVehicleDefs.M2)
                    foreach (bool v2 in new[] { false, true })
                        rows.Add(Conform(world, id, v2));
            }
            finally { TrackRig.DisposeWorld(world); }
            string F(double v, string f = "F1") => double.IsNaN(v) ? "-" : v.ToString(f, Ci);
            var sb = new StringBuilder();
            sb.AppendLine("# Vehicle conformance suite v0 (BRV test track)").AppendLine();
            sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm}. V0 = the browser game's model (verbatim port); v2 = BRV v2 core (shafts, MF tyres from 06 section 3.7, ABS, torque-map engine, the JS automatic as an AMT). \"-\" = did not reach it.").AppendLine();
            sb.AppendLine("| Vehicle | Model | 0-60 km/h (s) | Speed at 380 m (km/h) | 80-0 km/h (m) | Skidpad (g) | Full-lock radius at 2 m/s (m) | 20 % hill start, 25 m (s) | 35 % low range, 25 m (s) | 150 mm steps (s) | Mud, 15 s (m) |");
            sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var c in rows)
                sb.AppendLine($"| {c.id} | {(c.v2 ? "v2" : "V0")} | {F(c.t060, "F2")} | {F(c.vmax30)} | {F(c.brake80)} | {F(c.latG, "F2")} | {F(c.radius, "F2")} | {F(c.hill20)} | {F(c.hill35)} | {F(c.steps)} | {F(c.mud15)} |");
            Report("conformance-v0.md", sb.ToString());
            TestContext.WriteLine(sb.ToString());
            var fails = new List<string>();
            foreach (var c in rows.Where(r => r.v2))
            {
                if (!c.finite) fails.Add($"{c.id}: NaN/Inf");
                if (!(c.t060 < 30)) fails.Add($"{c.id}: 0-60 km/h {F(c.t060)} s");
                if (!(c.brake80 < 50)) fails.Add($"{c.id}: 80-0 km/h in {F(c.brake80)} m");
                if (!(c.latG > 0.35 && c.latG < 1.3)) fails.Add($"{c.id}: skidpad {F(c.latG, "F2")} g");
                if (double.IsNaN(c.hill20)) fails.Add($"{c.id}: no 20 % hill start");
                if (double.IsNaN(c.steps)) fails.Add($"{c.id}: did not climb the 150 mm steps");
                if (!(c.radius > 2 && c.radius < 20)) fails.Add($"{c.id}: turning radius {F(c.radius, "F2")} m");
            }
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }
    }
}
