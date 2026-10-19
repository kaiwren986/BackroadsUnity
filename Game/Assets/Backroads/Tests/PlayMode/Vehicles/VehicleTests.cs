using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Vehicles;
using Backroads.Vehicles.Reference;
using Backroads.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Tests.PlayMode.Vehicles
{
    /// <summary>
    /// BRV V0 gates (PLAN 6.1 M2, cards T2.1 and T2.2): Vehicle.GoldenTraces (headline), Vehicle.SnapshotRoundTrip and
    /// Vehicle.SettleParked (lane). They run on the imported canon v1 (pure v1: no road plates, no road colliders) and
    /// without the engine-sim DLL (Architecture rule 3: the vehicles see engines only through IVehicleEngine).
    /// </summary>
    [TestFixture, Category("Vehicle")]
    public class Vehicle
    {
        static readonly string[] TraceIds =
        {
            "truck_0_80_route9", "pickup_0_100_route9", "pickup_brake_80", "truck_quarry_low", "hatch_bog_10s", "winch_15m_tree",
        };

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        static CanonAsset Canon()
        {
            var a = CanonRuntime.Asset;
            if (a == null) Assert.Inconclusive(CanonRuntime.NotImportedMessage);
            return a;
        }

        static BrvWorldData FreshWorld(CanonAsset a)
        {
            var grid = WorldGrid.FromCanon(a);
            return BrvWorldData.Create(grid, CanonRuntime.Colliders, a.vegetation);
        }

        static void DisposeWorld(BrvWorldData w)
        {
            var g = w.Grid;
            w.Dispose();
            g.Dispose();
        }

        static string ArtifactsDir()
        {
            var d = Path.Combine(RepoRoot, "artifacts", "vehicles");
            Directory.CreateDirectory(d);
            return d;
        }

        // ------------------------------------------------------------------ golden traces

        [Test]
        public void GoldenTraces()
        {
            var a = Canon();
            var report = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            report.AppendLine("# Vehicle.GoldenTraces");
            report.AppendLine();
            report.AppendLine($"BRV V0 (verbatim vehicle.js port) against the oracle's golden traces (CanonData/v1/reference/traces), {DateTime.Now:yyyy-MM-dd HH:mm}.");
            report.AppendLine("Deformation: world.js rut() in doubles on a private grid copy (JsRutSink). Pure v1 terrain, no traffic.");
            report.AppendLine();
            var gates = new List<GoldenTraceGates.Gate>();
            var details = new StringBuilder();
            foreach (var id in TraceIds)
            {
                var input = GoldenTraceInput.Load(Path.Combine(RepoRoot, "tools", "oracle", "traces", id + ".json"));
                var oracle = GoldenTraceSamples.LoadReference(Path.Combine(RepoRoot, "CanonData", "v1", "reference", "traces", id + ".json"));
                Assert.AreEqual(oracle.inputSha256, input.sha256, $"{id}: the input file is not the one the reference was recorded from");
                var world = FreshWorld(a);
                var sink = new JsRutSink(world.Grid);
                var sw = Stopwatch.StartNew();
                GoldenTraceSamples run;
                try
                {
                    run = GoldenTraceReplay.Run(input, new GoldenTraceReplay.Options { World = world, Parking = a.placements.parking, Sink = sink });
                }
                finally { DisposeWorld(world); }
                double ms = sw.Elapsed.TotalMilliseconds;
                var g = GoldenTraceGates.Evaluate(input, oracle, run);
                gates.Add(g);
                var table = GoldenTraceGates.PerSecond(oracle, run, out double meanKmh, out double maxKmh, out double gearMatch, out double maxPos);
                details.AppendLine($"## {id}");
                details.AppendLine();
                details.AppendLine(input.about);
                details.AppendLine();
                details.AppendLine($"- Gate: {g}");
                details.AppendLine(string.Format(ci, "- Per second: mean |speed difference| {0:F2} km/h, max {1:F2} km/h, gear agreement {2:P0}; largest position difference over all samples {5:E2} m; {3} rut calls; replay {4:F0} ms",
                    meanKmh, maxKmh, gearMatch, sink.Calls, ms, maxPos));
                details.AppendLine();
                details.AppendLine(table);
                TestContext.WriteLine(g.ToString());
                TestContext.WriteLine(string.Format(ci, "  per second: mean |dv| {0:F2} km/h, max {1:F2} km/h, gear match {2:P0}, max |dpos| {5:E2} m, rut calls {3}, {4:F0} ms", meanKmh, maxKmh, gearMatch, sink.Calls, ms, maxPos));
                WriteCsv(id, oracle, run);
            }
            report.AppendLine("| Trace | Gate | Oracle | C# | Delta | Tolerance | Result |");
            report.AppendLine("|---|---|---:|---:|---:|---:|---|");
            foreach (var g in gates)
            {
                if (g.relative)
                    report.AppendLine(string.Format(ci, "| {0} | {1} | {2:F3} {3} | {4:F3} {3} | {5:+0.0;-0.0} % | +-{6:0} % | {7} |", g.trace, g.name, g.expected, g.unit, g.measured, g.DeltaPercent, g.tolerance * 100, g.pass ? "pass" : "MISS"));
                else
                    report.AppendLine($"| {g.trace} | {g.name} | completes | {(g.pass ? "completes" : "does not")} | | | {(g.pass ? "pass" : "FAIL")} |");
            }
            report.AppendLine();
            foreach (var g in gates) if (!string.IsNullOrEmpty(g.note)) report.AppendLine($"- {g.trace}: {g.note}");
            report.AppendLine();
            report.Append(details);
            var path = Path.Combine(ArtifactsDir(), "golden-traces.md");
            File.WriteAllText(path, report.ToString());
            TestContext.WriteLine("report: " + path);
            var misses = gates.Where(g => !g.pass).Select(g => g.ToString()).ToList();
            Assert.IsEmpty(misses, string.Join("\n", misses));
        }

        static void WriteCsv(string id, GoldenTraceSamples oracle, GoldenTraceSamples run)
        {
            var sb = new StringBuilder();
            sb.Append("source,").AppendLine(string.Join(",", GoldenTraceSamples.Fields));
            foreach (var (name, s) in new[] { ("oracle", oracle), ("csharp", run) })
                foreach (var r in s.rows)
                    sb.Append(name).Append(',').AppendLine(string.Join(",", r.Select(v => v.ToString("R", CultureInfo.InvariantCulture))));
            File.WriteAllText(Path.Combine(ArtifactsDir(), id + ".csv"), sb.ToString());
        }

        // ------------------------------------------------------------------ snapshot round trip

        /// <summary>
        /// Snapshot out to bytes mid-drive, then restore and continue: the restored vehicle follows the uninterrupted one
        /// to within 1 mm (exactly, in practice: the snapshot keeps every double). Covers the asphalt pickup run and the
        /// winch-out-of-the-bog run (mud, ruts, winch rope).
        /// </summary>
        [Test]
        public void SnapshotRoundTrip()
        {
            var a = Canon();
            double worst = 0;
            foreach (var (id, at) in new[] { ("pickup_0_100_route9", 400), ("winch_15m_tree", 600) })
            {
                var input = GoldenTraceInput.Load(Path.Combine(RepoRoot, "tools", "oracle", "traces", id + ".json"));
                var world = FreshWorld(a);
                try
                {
                    var sink = new JsRutSink(world.Grid);
                    using var fleet = new BrvFleet(world) { Sink = sink };
                    int v = fleet.Add(ClassicVehicleDefs.Get(input.vehicle));
                    fleet.Place(v, input.startX, input.startZ, input.startYaw);
                    fleet.Settle(v);
                    ref var s0 = ref fleet.State(v);
                    BrvSim.Wake(ref s0); s0.driven = true; s0.engineOn = true; s0.fuel = 1;
                    void Drive(int k)
                    {
                        var inp = new BrvInput { active = true, ignition = true, fwd = input.throttle[k], back = input.brake[k], steer = input.steer[k], hand = input.handbrake[k] > 0.5 ? 1 : 0 };
                        foreach (var (step, key) in input.hits) if (step == k) { if (key == "KeyG") inp.winchPresses++; if (key == "KeyX") inp.lowRangePresses++; }
                        fleet.SetInput(v, inp);
                        fleet.Step();
                    }
                    for (int k = 0; k < at; k++) Drive(k);
                    var bytes = BrvSnapshot.ToBytes(fleet.GetSnapshot(v));
                    var h0 = world.Grid.H.ToArray(); var w0 = world.Grid.Wear.ToArray(); var su0 = world.Grid.Surface.ToArray();
                    var path1 = new List<double3>();
                    for (int k = at; k < input.steps; k++) { Drive(k); path1.Add(fleet.State(v).pos); }
                    var end1 = fleet.GetSnapshot(v);
                    // the world's own state (deformation) is restored separately; the vehicle comes back from the bytes
                    world.Grid.H.CopyFrom(h0); world.Grid.Wear.CopyFrom(w0); world.Grid.Surface.CopyFrom(su0);
                    fleet.SetSnapshot(v, BrvSnapshot.FromBytes(bytes, fleet.Def(v).WheelCount));
                    double maxErr = 0;
                    for (int k = at; k < input.steps; k++)
                    {
                        Drive(k);
                        maxErr = Math.Max(maxErr, math.length(fleet.State(v).pos - path1[k - at]));
                    }
                    var end2 = fleet.GetSnapshot(v);
                    Assert.AreEqual(BrvSnapshot.ToBytes(end1), BrvSnapshot.ToBytes(end2), $"{id}: the restored run ends in a different state");
                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture, "SnapshotRoundTrip {0}: snapshot at step {1}, {2} bytes, max position difference {3:E2} m over {4} steps",
                        id, at, bytes.Length, maxErr, input.steps - at));
                    worst = Math.Max(worst, maxErr);
                }
                finally { DisposeWorld(world); }
            }
            Assert.Less(worst, 0.001, "snapshot round trip drifted by 1 mm or more");
        }

        // ------------------------------------------------------------------ settle on the garage bays

        /// <summary>
        /// The four M2 vehicles placed on the Millbrook garage bays (VEHICLE_ORDER) settle within 240 steps to
        /// |v| &lt; 0.01 m/s, and their wheel compressions equal the browser game's after its own boot settle
        /// (vehicles.json "derived" wheels[].len) within 1 mm.
        /// </summary>
        [Test]
        public void SettleParked()
        {
            var a = Canon();
            var derived = (Dictionary<string, object>)MiniJson.Get(MiniJson.O(MiniJson.Parse(File.ReadAllText(Path.Combine(RepoRoot, "CanonData", "v1", "gameplay", "vehicles.json")))), "derived");
            var world = FreshWorld(a);
            try
            {
                using var fleet = new BrvFleet(world) { NoDeformation = true };
                var fails = new List<string>();
                double worstLen = 0;
                foreach (var id in ClassicVehicleDefs.M2)
                {
                    int i = fleet.Add(ClassicVehicleDefs.Get(id));
                    var bay = a.placements.parking[Array.IndexOf(ClassicVehicleDefs.Order, id)];
                    fleet.Place(i, bay.x, bay.z, bay.yaw);
                    fleet.Settle(i, 240);
                    fleet.Park(i);
                    var s = fleet.State(i);
                    double v = math.length(s.vel), w = math.length(s.angVel);
                    var wheels = MiniJson.A(MiniJson.Get(MiniJson.O(MiniJson.Get(derived, id)), "wheels"));
                    var lens = new StringBuilder();
                    for (int k = 0; k < s.wheels.Length; k++)
                    {
                        double want = MiniJson.D(MiniJson.O(wheels[k]), "len");
                        double dl = Math.Abs(s.wheels[k].len - want);
                        worstLen = Math.Max(worstLen, dl);
                        lens.Append(string.Format(CultureInfo.InvariantCulture, " {0:F5}/{1:F5}", s.wheels[k].len, want));
                        if (dl > 0.001) fails.Add($"{id} wheel {k}: len {s.wheels[k].len:F5} m, browser game {want:F5} m");
                        if (!s.wheels[k].contact) fails.Add($"{id} wheel {k}: no ground contact");
                    }
                    TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}: |v| {1:E2} m/s, |w| {2:E2} rad/s, asleep {3}, wheel len C#/JS{4}", id, v, w, s.asleep, lens));
                    if (v >= 0.01) fails.Add($"{id}: |v| = {v:F4} m/s after 240 settle steps");
                }
                TestContext.WriteLine($"SettleParked: worst wheel length difference {worstLen:E2} m");
                Assert.IsEmpty(fails, string.Join("\n", fails));
            }
            finally { DisposeWorld(world); }
        }
    }
}
