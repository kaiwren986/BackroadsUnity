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
        /// same one every run). JS space. Above <paramref name="minGround"/> (the canon's sea level is 0 m), and at least
        /// 12 m from the garage bays (the parked vehicles stay out of the way).
        /// </summary>
        static Approach FindBuildingApproach(ColliderGrid g, in BrvWorld w, double minGround, PlacementSet.Parking[] avoid)
        {
            int bk = ColliderGrid.KindIndex("building");
            double lim = w.grid.Layout.Half - 40;
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
                    if (Math.Abs(start.x) > lim || Math.Abs(start.y) > lim) continue;
                    double h0 = WorldGridQueriesRef.HeightAtJs(w.grid, start.x, start.y);
                    if (h0 < minGround) continue;
                    bool ok = true;
                    for (double t = 0.5; t <= 14 && ok; t += 1)
                    {
                        var p = fc + n * t;
                        if (avoid != null)
                            foreach (var b in avoid)
                                if (math.distance(new double2(b.x, b.z), p) < 12) { ok = false; break; }
                        if (!ok) break;
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

        public static readonly string[] Worlds = { "canon", "millbrook" };

        /// <summary>
        /// A world for the chassis tests, loaded as its scene's root loads it: "canon" (CanonData v1: a private copy of the
        /// grid, so nothing deforms the shared one) or a WorldData world ("millbrook": WorldDataRuntime's grid and collider
        /// grid, unloaded afterwards if the test loaded it). Inconclusive when the world is not imported.
        /// </summary>
        sealed class TestWorld : IDisposable
        {
            public string Name;
            public WorldGrid Grid;
            public ColliderGrid Colliders;
            public VegetationSet Vegetation;
            public PlacementSet.Parking[] Bays;
            public WorldInfo.Poi[] Pois;
            public BrvWorldData Data;
            public double MinGround;
            bool _ownsGrid, _unloadWorldData;

            public static TestWorld Load(string name)
            {
                if (name == "canon")
                {
                    var a = Canon();
                    var g = WorldGrid.FromCanon(a);
                    var c = CanonRuntime.Colliders;
                    return new TestWorld
                    {
                        Name = name, Grid = g, Colliders = c, Vegetation = a.vegetation, Bays = a.placements.parking, Pois = a.world.pois,
                        Data = BrvWorldData.Create(g, c, a.vegetation), MinGround = 1.0, _ownsGrid = true,
                    };
                }
                var asset = WorldDataRuntime.Asset(name);
                if (asset == null) Assert.Inconclusive(WorldDataRuntime.NotImportedMessage(name));
                bool was = WorldDataRuntime.IsLoaded && WorldDataRuntime.Loaded == asset;
                WorldDataRuntime.Load(asset);
                var grid = WorldDataRuntime.Grid;
                var col = WorldDataRuntime.Colliders;
                return new TestWorld
                {
                    Name = name, Grid = grid, Colliders = col, Vegetation = asset.vegetation, Bays = asset.parking, Pois = asset.pois,
                    Data = BrvWorldData.Create(grid, col, asset.vegetation), MinGround = double.NegativeInfinity, _unloadWorldData = !was,
                };
            }

            public void Dispose()
            {
                Data?.Dispose();
                if (_ownsGrid) Grid?.Dispose();
                if (_unloadWorldData) WorldDataRuntime.Unload();
            }
        }

        /// <summary>
        /// Vehicle.BuildingImpact (PLAN 6.1 M3 headline, card T3.6) in both worlds, the canon test world and the new
        /// Millbrook (their static colliders as PhysX statics): the truck, V0 and v2, driven square into a building at 10
        /// and at 20 m/s stops at the wall: PhysX takes the contact, the truck's front never gets more than 0.15 m into the
        /// wall, its speed into the wall is gone within 0.25 s, and it never comes out on the other side.
        /// </summary>
        [Test]
        public void BuildingImpact()
        {
            var sb = new StringBuilder();
            var ci = CultureInfo.InvariantCulture;
            sb.AppendLine("# Vehicle.BuildingImpact (PhysX chassis, both worlds)").AppendLine();
            var fails = new List<string>();
            foreach (var worldName in Worlds)
            {
                using var tw = TestWorld.Load(worldName);
                var colliders = tw.Colliders;
                Assert.NotNull(colliders, worldName + ": no static colliders");
                var ap = FindBuildingApproach(colliders, tw.Data.View, tw.MinGround, tw.Bays);
                Assert.GreaterOrEqual(ap.collider, 0, worldName + ": no building with a clear approach");
                var bc = colliders.Records[ap.collider];
                sb.AppendLine($"## {worldName}").AppendLine();
                sb.AppendLine(string.Format(ci, "{0} static colliders. Building collider {1} at ({2:F1}, {3:F1}) JS, face {4:F1} m wide, approach along ({5:F3}, {6:F3}); the truck starts with its centre 8 m from the face.",
                    colliders.Count, ap.collider, bc.x, bc.z, ap.width, -ap.n.x, -ap.n.y)).AppendLine();
                sb.AppendLine("| Model | Speed (m/s) | At contact (m/s) | Deepest into the wall (m) | Speed into the wall 0.25 s later (m/s) | Rebound (m/s) | Contact steps | Result |");
                sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
                foreach (bool v2 in new[] { false, true })
                    foreach (double speed in new[] { 10.0, 20.0 })
                    {
                        using var rig = new PhysxRig("impact");
                        using var statics = PhysxStatics.Build(colliders, rig.Root.transform);
                        using var fleet = new BrvFleet(tw.Data) { NoDeformation = true };
                        bool was = BrvFeatures.V2Driveline;
                        BrvFeatures.V2Driveline = v2;
                        int i;
                        try { i = fleet.Add(ClassicVehicleDefs.Get("truck")); } finally { BrvFeatures.V2Driveline = was; }
                        var start = ap.face + ap.n * 8;
                        var head = -ap.n;
                        var wv = tw.Data.View;
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
                        if (!ok) fails.Add(string.Format(ci, "{0}, {1} at {2} m/s: contact step {3}, deepest {4:F3} m, speed into the wall 0.25 s after contact {5:F2} m/s, finite {6}",
                            worldName, v2 ? "v2" : "V0", speed, contactStep, deepest, after, finite));
                    }
                sb.AppendLine();
            }
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
        /// The game's path in both worlds (T3.6): the world set as its scene's root sets it (ActiveWorld), VehicleSpawner
        /// puts the four M2 vehicles on that world's garage bays (settled, within 1.5 m of the bays, parked still, the
        /// player in the truck), VehicleSimSystem binds that world (every static collider a PhysX static, the chassis in
        /// the default physics scene, Unity's own physics step and the read-back after it); then the truck, taken to a
        /// building, rolls into it at 15 m/s and stops at the wall. Clearing the world removes the vehicles.
        /// </summary>
        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator WorldsInGame()
        {
            foreach (var worldName in Worlds)
            {
                var tw = TestWorld.Load(worldName);
                try
                {
                    ActiveWorld.Set(worldName, tw.Grid, tw.Colliders, tw.Vegetation, tw.Bays, tw.Pois);
                    float t0 = Time.realtimeSinceStartup;
                    while ((Backroads.Vehicles.Client.VehicleSpawner.Current == null || (!Backroads.Vehicles.Client.VehicleSpawner.Current.Ready && Backroads.Vehicles.Client.VehicleSpawner.Current.Error == null))
                           && Time.realtimeSinceStartup - t0 < 30) yield return null;
                    var sp = Backroads.Vehicles.Client.VehicleSpawner.Current;
                    Assert.NotNull(sp, worldName + ": no VehicleSpawner for the world");
                    Assert.IsNull(sp.Error, sp.Error);
                    Assert.IsTrue(sp.Ready, worldName + ": the vehicles did not spawn within 30 s");
                    var sys = sp.SimSystem;
                    Assert.AreEqual(worldName, sys.WorldName);
                    Assert.AreEqual(ClassicVehicleDefs.M2.Length, sys.Vehicles.Count);
                    Assert.AreEqual(BrvFeatures.V2Driveline, sys.Fleet.IsV2(0));
                    Assert.NotNull(sys.Statics, worldName + ": no PhysX statics");
                    Assert.AreEqual(tw.Colliders.Count, sys.Statics.Count, worldName + ": every static collider is a PhysX static");
                    Assert.AreEqual(Backroads.Vehicles.Client.VehicleSpawner.StartVehicle, sys.Player.Def.id);
                    long s0 = sys.TotalSteps;
                    while (sys.TotalSteps - s0 < 120) yield return new WaitForFixedUpdate();
                    float worstBay = 0, worstV = 0;
                    foreach (var v in sys.Vehicles)
                    {
                        var b = tw.Bays[Array.IndexOf(ClassicVehicleDefs.Order, v.Def.id)];
                        var pos = v.Position; pos.y = 0;
                        worstBay = Mathf.Max(worstBay, Vector3.Distance(pos, new Vector3(-b.x, 0, b.z)));
                        worstV = Mathf.Max(worstV, v.Velocity.magnitude);
                    }
                    TestContext.WriteLine($"{worldName}: {sys.Vehicles.Count} vehicles on the garage bays (worst {worstBay:F3} m off, parked {worstV:F4} m/s), " +
                                          $"{sys.Statics.Count} PhysX statics ({sys.Statics.Boxes} boxes, {sys.Statics.Capsules} capsules), PhysX contact steps while parked {sys.Chassis.ContactSteps}");
                    Assert.LessOrEqual(worstBay, 1.5f, worldName + ": a vehicle is off its bay");
                    Assert.Less(worstV, 0.01f, worldName + ": a parked vehicle moves");

                    // the truck into a building, through the game's step
                    var ap = FindBuildingApproach(tw.Colliders, sys.Fleet.World.View, tw.MinGround, tw.Bays);
                    Assert.GreaterOrEqual(ap.collider, 0, worldName + ": no building with a clear approach");
                    var truck = sys.Player;
                    var start = ap.face + ap.n * 8;
                    var head = -ap.n;
                    var wv = sys.Fleet.World.View;
                    BrvSim.PlaceAligned(ref truck.State, sys.Fleet.Def(truck.Index), wv, start.x, start.y, Math.Atan2(head.x, head.y));
                    sys.Fleet.Settle(truck.Index, 120);
                    sys.Fleet.SetRolling(truck.Index, 15);
                    ReleaseBrakes(truck);
                    long c0 = sys.Chassis.ContactSteps;
                    var d = sys.Fleet.Def(truck.Index);
                    double deepest = 0;
                    s0 = sys.TotalSteps;
                    while (sys.TotalSteps - s0 < 360)
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
                    TestContext.WriteLine($"{worldName} in game: the truck into building collider {ap.collider} at 15 m/s: PhysX contact steps {sys.Chassis.ContactSteps - c0}, " +
                                          $"deepest into the wall {deepest:F3} m, speed into the wall at the end {vIn:F2} m/s");
                    Assert.Greater(sys.Chassis.ContactSteps - c0, 0, worldName + ": the truck never touched the wall");
                    Assert.LessOrEqual(deepest, 0.15, worldName + ": the truck went into the wall");
                    Assert.LessOrEqual(vIn, 0.1, worldName + ": the truck is still going into the wall");
                }
                finally
                {
                    ActiveWorld.Clear(tw.Grid);
                }
                yield return null;
                Assert.IsNull(Backroads.Vehicles.Client.VehicleSpawner.Current, worldName + ": the vehicles stayed after the world was cleared");
                tw.Dispose();
            }
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
