using System;
using System.Collections.Generic;
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
using UnityEngine.SceneManagement;

namespace Backroads.Tests.PlayMode.Vehicles
{
    /// <summary>A scene with its own PhysX world (stepped by hand) for the chassis tests.</summary>
    sealed class PhysxRig : IDisposable
    {
        public readonly Scene Scene;
        public readonly PhysicsScene Physics;
        public readonly GameObject Root;

        public PhysxRig(string name)
        {
            Scene = SceneManager.CreateScene(name + " " + Guid.NewGuid().ToString("N").Substring(0, 8), new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            Physics = Scene.GetPhysicsScene();
            Root = new GameObject(name);
            SceneManager.MoveGameObjectToScene(Root, Scene);
        }

        public void Dispose()
        {
            if (Scene.IsValid() && Scene.isLoaded) SceneManager.UnloadSceneAsync(Scene);
        }
    }

    /// <summary>PhysX chassis (card T3.6): Vehicle.BuildingImpact (headline), the M2 traces through PhysX, vehicle against vehicle.</summary>
    public partial class Vehicle
    {
        // ------------------------------------------------------------------ the chassis bodies

        /// <summary>Every vehicle, V0 and v2, gets a gravity-free Rigidbody with BRV's mass and inertia, a hull box and wheel spheres on their layers.</summary>
        [Test]
        public void ChassisBodies()
        {
            var world = BrvTestTrack.BuildWorld();
            using var rig = new PhysxRig("chassis");
            try
            {
                using var fleet = new BrvFleet(world) { NoDeformation = true };
                bool was = BrvFeatures.V2Driveline;
                foreach (var id in ClassicVehicleDefs.M2)
                    foreach (bool v2 in new[] { false, true })
                    {
                        BrvFeatures.V2Driveline = v2;
                        try { fleet.Add(ClassicVehicleDefs.Get(id)); } finally { BrvFeatures.V2Driveline = was; }
                    }
                using var chassis = new BrvPhysxChassis(fleet, rig.Root.transform);
                Assert.AreEqual(fleet.Count, chassis.Count);
                Assert.IsTrue(fleet.PhysxCollisions);
                for (int i = 0; i < fleet.Count; i++)
                {
                    var rb = chassis.RigidbodyOf(i);
                    var s = fleet.State(i);
                    var d = fleet.Def(i);
                    Assert.IsFalse(rb.useGravity, "gravity is BRV's");
                    Assert.AreEqual((float)s.mass, rb.mass);
                    Assert.AreEqual((float)(1 / s.iI.y), rb.inertiaTensor.y, 1e-3f * rb.inertiaTensor.y);
                    Assert.AreEqual(LayerMask.NameToLayer("VehicleHull"), chassis.HullOf(i).gameObject.layer);
                    Assert.AreEqual((float)(2 * d.bhl), chassis.HullOf(i).size.z, 1e-5f);
                    for (int w = 0; w < d.wheels.Length; w++)
                    {
                        Assert.AreEqual(LayerMask.NameToLayer("Wheel"), chassis.WheelOf(i, w).gameObject.layer);
                        Assert.AreEqual((float)d.wheels[w].r, chassis.WheelOf(i, w).radius, 1e-6f);
                    }
                    Assert.AreEqual(0, rb.excludeLayers.value & (1 << LayerMask.NameToLayer("Static")), "the hull meets the statics");
                    Assert.AreNotEqual(0, rb.excludeLayers.value & (1 << LayerMask.NameToLayer("Road")), "the ground is BRV's: no Road contacts");
                }
                Assert.IsEmpty(BrvPhysxChassis.Validate(chassis.Root.gameObject));
                // the validation catches a vehicle body with gravity on
                chassis.RigidbodyOf(0).useGravity = true;
                Assert.AreEqual(1, BrvPhysxChassis.Validate(chassis.Root.gameObject).Count);
                chassis.RigidbodyOf(0).useGravity = false;
            }
            finally { TrackRig.DisposeWorld(world); }
        }

        // ------------------------------------------------------------------ building impact (headline)

        struct Approach { public int collider; public double2 face, n; public double width; }

        /// <summary>
        /// A building face with a clear, nearly flat 14 m approach above the water: the first in collider order (the
        /// same one every run). JS space.
        /// </summary>
        static Approach FindBuildingApproach(ColliderGrid g, in BrvWorld w)
        {
            int bk = ColliderGrid.KindIndex("building");
            var near = new List<int>();
            for (int ci = 0; ci < g.Count; ci++)
            {
                var c = g.Records[ci];
                if (c.type != 1 || c.kind != bk) continue;
                double cs = Math.Cos(c.rot), sn = Math.Sin(c.rot);
                var a1 = new double2(cs, -sn); var a2 = new double2(sn, cs);
                foreach (var (n, half, width) in new[] { (a1, (double)c.hw, 2.0 * c.hd), (-a1, (double)c.hw, 2.0 * c.hd), (a2, (double)c.hd, 2.0 * c.hw), (-a2, (double)c.hd, 2.0 * c.hw) })
                {
                    if (width < 6) continue;
                    var fc = new double2(c.x, c.z) + n * half;
                    var start = fc + n * 8;
                    if (Math.Abs(start.x) > 480 || Math.Abs(start.y) > 480) continue;
                    double h0 = WorldGridQueriesRef.HeightAtJs(w.grid, start.x, start.y);
                    if (h0 < 1.0) continue;
                    bool ok = true;
                    for (double t = 0.5; t <= 14 && ok; t += 1)
                    {
                        var p = fc + n * t;
                        if (Math.Abs(WorldGridQueriesRef.HeightAtJs(w.grid, p.x, p.y) - h0) > 0.35) ok = false;
                        near.Clear();
                        g.QueryJs((float)p.x, (float)p.y, 4f, near);
                        foreach (int o in near)
                        {
                            if (o == ci) continue;
                            var r = g.Records[o];
                            if (math.distance(new double2(r.x, r.z), p) < 4 + r.r) { ok = false; break; }
                        }
                    }
                    if (ok) return new Approach { collider = ci, face = fc, n = n, width = width };
                }
            }
            return new Approach { collider = -1 };
        }

        /// <summary>
        /// Vehicle.BuildingImpact (PLAN 6.1 M3 headline, card T3.6) on the canon test world: the truck, V0 and v2, driven
        /// square into a building at 10 and at 20 m/s stops at the wall: PhysX takes the contact, the truck's front never
        /// gets more than 0.15 m into the wall, its speed into the wall is gone within 0.25 s, and it never comes out on
        /// the other side. (Millbrook's statics come with T3.3.)
        /// </summary>
        [Test]
        public void BuildingImpact()
        {
            var a = Canon();
            var colliders = CanonRuntime.Colliders;
            var world = FreshWorld(a);
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("# Vehicle.BuildingImpact (PhysX chassis, canon test world)").AppendLine();
            var fails = new List<string>();
            try
            {
                var ap = FindBuildingApproach(colliders, world.View);
                Assert.GreaterOrEqual(ap.collider, 0, "no building with a clear approach on the canon test world");
                var bc = colliders.Records[ap.collider];
                sb.AppendLine(string.Format(ci, "Building collider {0} at ({1:F1}, {2:F1}) JS, face {3:F1} m wide, approach along ({4:F3}, {5:F3}); the truck starts with its centre 8 m from the face.",
                    ap.collider, bc.x, bc.z, ap.width, -ap.n.x, -ap.n.y)).AppendLine();
                sb.AppendLine("| Model | Speed (m/s) | At contact (m/s) | Deepest into the wall (m) | Speed into the wall 0.25 s later (m/s) | Rebound (m/s) | Contact steps | Result |");
                sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
                foreach (bool v2 in new[] { false, true })
                    foreach (double speed in new[] { 10.0, 20.0 })
                    {
                        using var rig = new PhysxRig("impact");
                        using var statics = PhysxStatics.Build(colliders, rig.Root.transform);
                        using var fleet = new BrvFleet(world) { NoDeformation = true };
                        bool was = BrvFeatures.V2Driveline;
                        BrvFeatures.V2Driveline = v2;
                        int i;
                        try { i = fleet.Add(ClassicVehicleDefs.Get("truck")); } finally { BrvFeatures.V2Driveline = was; }
                        var start = ap.face + ap.n * 8;
                        var head = -ap.n;
                        var wv = world.View;
                        BrvSim.PlaceAligned(ref fleet.State(i), fleet.Def(i), wv, start.x, start.y, Math.Atan2(head.x, head.y));
                        fleet.State(i).engineOn = true;
                        fleet.Settle(i, 120);
                        using var chassis = new BrvPhysxChassis(fleet, rig.Root.transform);
                        fleet.Knocked(statics);
                        fleet.SetRolling(i, speed);
                        ref var s0 = ref fleet.State(i);
                        s0.ctrl.brake = 0; s0.ctrl.handbrake = 0; s0.ctrl.throttle = 0;
                        var d = fleet.Def(i);
                        double deepest = 0, atContact = double.NaN, after = double.NaN, rebound = 0;
                        int contactStep = -1, contacts = 0;
                        bool finite = true;
                        for (int k = 0; k < 120 * 4; k++)
                        {
                            double vIn = math.dot(new double2(fleet.State(i).vel.x, fleet.State(i).vel.z), head);
                            chassis.Step(rig.Physics);
                            var s = fleet.State(i);
                            if (!TrackRig.Finite(s)) { finite = false; break; }
                            if (chassis.ContactsLastStep > 0) { contacts++; if (contactStep < 0) { contactStep = k; atContact = vIn; } }
                            // the truck's front corners against the face plane (positive = in front of the wall)
                            for (int sx = -1; sx <= 1; sx += 2)
                                for (int sy = -1; sy <= 1; sy += 2)
                                {
                                    var p = BrvSim.ToWorld(s, sx * d.bhw, d.bcy + sy * d.bhh, d.bcz + d.bhl);
                                    double dist = math.dot(new double2(p.x, p.z) - ap.face, ap.n);
                                    deepest = Math.Max(deepest, -dist);
                                }
                            double v = math.dot(new double2(s.vel.x, s.vel.z), head);
                            if (contactStep >= 0 && k == contactStep + 30) after = v;
                            if (contactStep >= 0) rebound = Math.Max(rebound, -v);
                        }
                        bool ok = finite && contactStep >= 0 && deepest <= 0.15 && after <= 0.1;
                        sb.AppendLine(string.Format(ci, "| {0} | {1:F0} | {2:F2} | {3:F3} | {4:F2} | {5:F2} | {6} | {7} |",
                            v2 ? "v2" : "V0", speed, atContact, deepest, after, rebound, contacts, ok ? "pass" : "FAIL"));
                        if (!ok) fails.Add(string.Format(ci, "{0} at {1} m/s: contact step {2}, deepest {3:F3} m, speed into the wall 0.25 s after contact {4:F2} m/s, finite {5}",
                            v2 ? "v2" : "V0", speed, contactStep, deepest, after, finite));
                    }
            }
            finally { DisposeWorld(world); }
            Report("building-impact.md", sb.ToString());
            TestContext.WriteLine(sb.ToString());
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        static void ReleaseBrakes(SimVehicle v)
        {
            ref var s = ref v.State;
            s.ctrl.brake = 0; s.ctrl.handbrake = 0; s.ctrl.throttle = 0;
        }

        /// <summary>
        /// The game's path for the same impact: VehicleSimSystem on the canon test world (the 4,013 canon colliders as
        /// PhysX statics, the chassis in the default physics scene, Unity's own physics step after FixedUpdate and the
        /// read-back after it). The truck rolls into the building at 15 m/s and stops at the wall.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator BuildingImpactInGame()
        {
            var a = Canon();
            var host = new GameObject("BuildingImpactInGame");
            try
            {
                var sys = VehicleSimSystem.Create(host);
                Assert.NotNull(sys.Chassis, "the PhysX chassis is on by default");
                Assert.NotNull(sys.Statics);
                Assert.AreEqual(CanonRuntime.Colliders.Count, sys.Statics.Count, "every canon collider is a PhysX static");
                TestContext.WriteLine($"canon statics: {sys.Statics.Count} ({sys.Statics.Boxes} boxes, {sys.Statics.Capsules} capsules)");
                var ap = FindBuildingApproach(CanonRuntime.Colliders, sys.Fleet.World.View);
                var start = ap.face + ap.n * 8;
                var head = -ap.n;
                var truck = sys.SpawnAtJs(ClassicVehicleDefs.Get("truck"), start.x, start.y, Math.Atan2(head.x, head.y), 120);
                sys.Fleet.SetRolling(truck.Index, 15);
                ReleaseBrakes(truck);
                var d = sys.Fleet.Def(truck.Index);
                double deepest = 0;
                long t0 = sys.TotalSteps;
                while (sys.TotalSteps - t0 < 360)
                {
                    yield return new WaitForFixedUpdate();
                    var s = truck.State;
                    Assert.IsTrue(TrackRig.Finite(s));
                    for (int sx = -1; sx <= 1; sx += 2)
                    {
                        var p = BrvSim.ToWorld(s, sx * d.bhw, d.bcy, d.bcz + d.bhl);
                        deepest = Math.Max(deepest, -math.dot(new double2(p.x, p.z) - ap.face, ap.n));
                    }
                }
                double vIn = math.dot(new double2(truck.State.vel.x, truck.State.vel.z), head);
                TestContext.WriteLine($"in game: {sys.TotalSteps - t0} steps, PhysX contact steps {sys.Chassis.ContactSteps}, deepest into the wall {deepest:F3} m, speed into the wall at the end {vIn:F2} m/s");
                Assert.Greater(sys.Chassis.ContactSteps, 0, "the truck never touched the wall");
                Assert.LessOrEqual(deepest, 0.15, "the truck went into the wall");
                Assert.LessOrEqual(vIn, 0.1, "the truck is still going into the wall");
            }
            finally { UnityEngine.Object.Destroy(host); }
            yield return null;
        }

        // ------------------------------------------------------------------ the M2 traces through PhysX

        /// <summary>
        /// The six golden traces with the PhysX chassis and the canon statics as PhysX statics (instead of the browser
        /// game's 2D statics): every M2 trace gate still passes, and the run is reported against the plain replay.
        /// </summary>
        [Test]
        public void GoldenTracesPhysx()
        {
            var a = Canon();
            var sb = new StringBuilder();
            sb.AppendLine("# Vehicle.GoldenTracesPhysx (the M2 traces through the PhysX chassis)").AppendLine();
            sb.AppendLine("| Trace | Gate | Oracle | PhysX chassis | Delta | Steps with a PhysX contact | Largest position difference to the oracle |").AppendLine("|---|---|---:|---:|---:|---:|---:|");
            var fails = new List<string>();
            foreach (var id in TraceIds)
            {
                var input = GoldenTraceInput.Load(Path.Combine(RepoRoot, "tools", "oracle", "traces", id + ".json"));
                var oracle = GoldenTraceSamples.LoadReference(Path.Combine(RepoRoot, "CanonData", "v1", "reference", "traces", id + ".json"));
                var world = FreshWorld(a);
                using var rig = new PhysxRig("traces");
                PhysxStatics statics = null;
                BrvPhysxChassis chassis = null;
                GoldenTraceSamples run;
                try
                {
                    run = GoldenTraceReplay.Run(input, new GoldenTraceReplay.Options
                    {
                        World = world, Parking = a.placements.parking, Sink = new JsRutSink(world.Grid),
                        Attach = fleet =>
                        {
                            statics = PhysxStatics.Build(CanonRuntime.Colliders, rig.Root.transform);
                            chassis = new BrvPhysxChassis(fleet, rig.Root.transform);
                            fleet.Knocked(statics);
                            return () => { chassis.BeforeSimulate(); rig.Physics.Simulate((float)BrvSim.STEP); chassis.AfterSimulate(); };
                        },
                    });
                }
                finally { chassis?.Dispose(); statics?.Dispose(); DisposeWorld(world); }
                var g = GoldenTraceGates.Evaluate(input, oracle, run);
                GoldenTraceGates.PerSecond(oracle, run, out _, out _, out _, out double maxPos);
                sb.AppendLine(g.relative
                    ? $"| {id} | {g.name} | {g.expected:F3} {g.unit} | {g.measured:F3} {g.unit} | {g.DeltaPercent:+0.0;-0.0} % | {chassis?.ContactSteps} | {maxPos:E2} m |"
                    : $"| {id} | {g.name} | completes | {(g.pass ? "completes" : "does not")} | | {chassis?.ContactSteps} | {maxPos:E2} m |");
                TestContext.WriteLine($"{g} (PhysX contact steps {chassis?.ContactSteps})");
                if (!g.pass) fails.Add(g.ToString());
            }
            Report("golden-traces-physx.md", sb.ToString());
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        // ------------------------------------------------------------------ vehicle against vehicle

        /// <summary>Two trucks head-on at 8 m/s each on the test track: PhysX stops them nose to nose (no overlap past 0.3 m), momentum kept.</summary>
        [Test]
        public void VehiclesCollidePhysx()
        {
            var world = BrvTestTrack.BuildWorld();
            using var rig = new PhysxRig("v2v");
            try
            {
                foreach (bool v2 in new[] { false, true })
                {
                    using var tr = new TrackRig(2, world);
                    int a = tr.Add("truck", v2), b = tr.Add("truck", v2);
                    double z0 = -300;
                    tr.Place(a, 0, z0 - 12, 0, settle: 120);
                    tr.Place(b, 0, z0 + 12, Math.PI, settle: 120);
                    using var chassis = new BrvPhysxChassis(tr.Fleet, rig.Root.transform);
                    tr.Fleet.SetRolling(a, 8); tr.Fleet.SetRolling(b, 8);
                    foreach (int k in new[] { a, b }) { tr.S(k).ctrl.brake = 0; tr.S(k).ctrl.handbrake = 0; }
                    var d = tr.Fleet.Def(a);
                    double front = d.bcz + d.bhl, closest = double.PositiveInfinity, p0 = 0, pAfter = double.NaN;
                    int contact = -1;
                    for (int k = 0; k < 360; k++)
                    {
                        p0 = tr.S(a).mass * tr.S(a).vel.z + tr.S(b).mass * tr.S(b).vel.z;
                        chassis.Step(rig.Physics);
                        Assert.IsTrue(TrackRig.Finite(tr.S(a)) && TrackRig.Finite(tr.S(b)));
                        closest = Math.Min(closest, tr.S(b).pos.z - tr.S(a).pos.z);
                        if (contact < 0 && chassis.ContactsLastStep > 0)
                        {
                            contact = k;
                            pAfter = tr.S(a).mass * tr.S(a).vel.z + tr.S(b).mass * tr.S(b).vel.z;
                            TestContext.WriteLine($"{(v2 ? "v2" : "V0")}: contact at step {k}, momentum along z before {p0:F0}, after {pAfter:F0} kg m/s");
                        }
                    }
                    TestContext.WriteLine($"{(v2 ? "v2" : "V0")}: closest centres {closest:F3} m (noses touch at {2 * front:F3} m); speeds after 3 s {tr.S(a).speed:F2}, {tr.S(b).speed:F2} m/s");
                    Assert.GreaterOrEqual(contact, 0, "the trucks never met");
                    Assert.GreaterOrEqual(closest, 2 * front - 0.3, "the trucks went into each other");
                    Assert.AreEqual(0, pAfter, 0.1 * 5400 * 8, "momentum along the road (both trucks equal: about 0)");
                }
            }
            finally { TrackRig.DisposeWorld(world); }
        }
    }

    static class FleetPhysxTestExtensions
    {
        /// <summary>Knocked-over trees stop colliding in PhysX too.</summary>
        public static void Knocked(this BrvFleet fleet, PhysxStatics statics) => fleet.TreeKnocked += k => statics.Knock(k.collider);
    }
}
