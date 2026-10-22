using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Backroads.Canon.Editor;
using Backroads.Core;
using Backroads.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Tests.EditMode.World
{
    /// <summary>
    /// World.HeightProbes, World.RayHits, World.PlacementsRoundTrip (PLAN 6.1 M1 headline gates, T1.2) plus the float
    /// path, the collider grid and the server canary line. Probes and rays come from CanonData/v1/terrain/probes.json.
    /// </summary>
    [TestFixture, Category("World")]
    public partial class World
    {
        WorldGridView _g;

        [OneTimeSetUp]
        public void SetUp() => _g = WorldTestUtil.Grid().View;

        static List<object> Probes() => CanonJson.Arr(CanonJson.Get(WorldTestUtil.Json("terrain/probes.json"), "probes"));
        static List<object> Rays() => CanonJson.Arr(CanonJson.Get(WorldTestUtil.Json("terrain/probes.json"), "rays"));

        /// <summary>1,000 seeded probes: reference-path height within 1e-5 m, surface exact, normal within 1e-4 (Unity API, x flipped).</summary>
        [Test]
        public void HeightProbes()
        {
            var probes = Probes();
            Assert.AreEqual(1000, probes.Count);
            double maxDh = 0, maxDn = 0;
            int surfFails = 0;
            var fails = new List<string>();
            foreach (var o in probes)
            {
                var p = CanonJson.Doubles(o);
                double x = p[0], z = p[1];
                var unity = new double3(-x, 0, z);
                double h = WorldGridQueriesRef.HeightAt(_g, unity);
                var n = WorldGridQueriesRef.NormalAt(_g, unity);
                var nJsInUnity = new double3(-p[3], p[4], p[5]);
                double dh = Math.Abs(h - p[2]);
                double dn = math.cmax(math.abs(n - nJsInUnity));
                maxDh = Math.Max(maxDh, dh);
                maxDn = Math.Max(maxDn, dn);
                byte s = WorldGridQueriesRef.SurfaceAt(_g, unity);
                if (s != (byte)p[6]) surfFails++;
                if (dh > 1e-5 || dn > 1e-4 || s != (byte)p[6])
                    if (fails.Count < 10) fails.Add($"({x}, {z}): h {h} vs {p[2]}, n {n} vs {nJsInUnity}, surface {s} vs {p[6]}");
            }
            TestContext.WriteLine($"HeightProbes: max |dh| {maxDh:E3} m, max |dn| {maxDn:E3}, surface mismatches {surfFails}");
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        /// <summary>The Burst float path stays within 2 mm of the reference on the same probes (informational bound).</summary>
        [Test]
        public void FloatPathMatchesReference()
        {
            var probes = Probes();
            int n = probes.Count;
            var input = new float3[n];
            for (int i = 0; i < n; i++) { var p = CanonJson.Doubles(probes[i]); input[i] = new float3((float)-p[0], 0, (float)p[1]); }
            using var pts = new NativeArray<float3>(input, Allocator.TempJob);
            using var hs = new NativeArray<float>(n, Allocator.TempJob);
            using var ns = new NativeArray<float3>(n, Allocator.TempJob);
            using var ss = new NativeArray<byte>(n, Allocator.TempJob);
            new HeightQueryJob { Grid = _g, Points = pts, Heights = hs, Normals = ns, Surfaces = ss }.Schedule(n, 64).Complete();
            double maxDh = 0, maxDn = 0;
            int sf = 0;
            for (int i = 0; i < n; i++)
            {
                var p = CanonJson.Doubles(probes[i]);
                maxDh = Math.Max(maxDh, Math.Abs(hs[i] - p[2]));
                maxDn = Math.Max(maxDn, math.cmax(math.abs((double3)ns[i] - new double3(-p[3], p[4], p[5]))));
                if (ss[i] != (byte)p[6]) sf++;
            }
            TestContext.WriteLine($"float path: max |dh| {maxDh:E3} m, max |dn| {maxDn:E3}, surface mismatches {sf} (nearest-vertex ties on float inputs)");
            Assert.Less(maxDh, 2e-3, "float heights");
            Assert.Less(maxDn, 2e-2, "float normals");
            Assert.LessOrEqual(sf, 5, "float surface lookups");
        }

        /// <summary>
        /// 12 x 4,096 camera rays through the reference ray march: within 1 mm of JS; at most 0.1 % outliers, each a
        /// grazing ray (|dir . n| &lt; 0.05) within one 0.5 m step.
        /// </summary>
        [Test]
        public void RayHits()
        {
            var rays = Rays();
            Assert.AreEqual(12, rays.Count);
            int total = 0, exact = 0, within = 0;
            double maxD = 0;
            var outliers = new List<string>();
            var badOutliers = new List<string>();
            var sw = Stopwatch.StartNew();
            foreach (var co in rays)
            {
                var c = CanonJson.Obj(co);
                var o = CanonJson.Doubles(CanonJson.Get(c, "origin"));
                var origin = new double3(-o[0], o[1], o[2]);
                foreach (var ho in CanonJson.Arr(CanonJson.Get(c, "hits")))
                {
                    var h = CanonJson.Doubles(ho);
                    var dir = new double3(-h[0], h[1], h[2]);
                    double t = WorldGridQueriesRef.RayTerrain(_g, origin, dir, 1500, 0.5);
                    total++;
                    double d = Math.Abs(t - h[3]);
                    if (t == h[3]) exact++;
                    bool sameKind = (t < 0) == (h[3] < 0);
                    if (sameKind && d <= 1e-3) { within++; maxD = Math.Max(maxD, d); continue; }
                    var hitT = h[3] > 0 ? h[3] : t;
                    var hit = origin + dir * hitT;
                    var nrm = WorldGridQueriesRef.NormalAt(_g, hit);
                    double dn = Math.Abs(math.dot(dir, nrm));
                    var line = $"{CanonJson.Str(c, "camera")}: t {t} vs {h[3]}, |dir.n| {dn:F4}";
                    outliers.Add(line);
                    if (!(sameKind && dn < 0.05 && d <= 0.5)) badOutliers.Add(line);
                }
            }
            TestContext.WriteLine($"RayHits: {total} rays, {exact} bit-identical, {within} within 1 mm (max {maxD:E2} m), {outliers.Count} outliers, {sw.ElapsedMilliseconds} ms");
            foreach (var l in outliers.Take(20)) TestContext.WriteLine("  outlier " + l);
            Assert.AreEqual(12 * 4096, total);
            Assert.LessOrEqual(outliers.Count, total / 1000, "more than 0.1 % outliers");
            Assert.IsEmpty(badOutliers, "outliers that are not grazing rays within one 0.5 m step:\n" + string.Join("\n", badOutliers.Take(20)));
        }

        /// <summary>The Burst float ray march agrees with JS for most rays (informational: reported, loosely bounded).</summary>
        [Test]
        public void FloatRayMarch()
        {
            var rays = Rays();
            var origins = new List<float3>();
            var dirs = new List<float3>();
            var want = new List<double>();
            foreach (var co in rays)
            {
                var c = CanonJson.Obj(co);
                var o = CanonJson.Doubles(CanonJson.Get(c, "origin"));
                foreach (var ho in CanonJson.Arr(CanonJson.Get(c, "hits")))
                {
                    var h = CanonJson.Doubles(ho);
                    origins.Add(new float3((float)-o[0], (float)o[1], (float)o[2]));
                    dirs.Add(new float3((float)-h[0], (float)h[1], (float)h[2]));
                    want.Add(h[3]);
                }
            }
            using var on = new NativeArray<float3>(origins.ToArray(), Allocator.TempJob);
            using var dn = new NativeArray<float3>(dirs.ToArray(), Allocator.TempJob);
            using var hits = new NativeArray<float>(origins.Count, Allocator.TempJob);
            var sw = Stopwatch.StartNew();
            new RayQueryJob { Grid = _g, Origins = on, Dirs = dn, Hits = hits, MaxT = 1500, Step = 0.5f }.Schedule(origins.Count, 64).Complete();
            long ms = sw.ElapsedMilliseconds;
            int within1mm = 0, within5cm = 0;
            for (int i = 0; i < want.Count; i++)
            {
                double d = Math.Abs(hits[i] - want[i]);
                if (d <= 1e-3) within1mm++;
                if (d <= 0.05) within5cm++;
            }
            TestContext.WriteLine($"float rays: {within1mm}/{want.Count} within 1 mm, {within5cm} within 5 cm, Burst job {ms} ms");
            Assert.GreaterOrEqual(within5cm, want.Count * 0.98, "float ray march");
        }

        /// <summary>
        /// Every building and tree transform and every typed record survives the JS -> Unity flip and back within
        /// 1e-4 m and 0.01 degrees, compared with the JSON values; building fronts point the flipped way.
        /// </summary>
        [Test]
        public void PlacementsRoundTrip()
        {
            var a = WorldTestUtil.Canon();
            var fails = new List<string>();
            double maxP = 0, maxA = 0;
            void P(string what, Vector3 unity, double jx, double jy, double jz)
            {
                double d = Math.Max(Math.Abs(-unity.x - jx), Math.Max(Math.Abs(unity.y - jy), Math.Abs(unity.z - jz)));
                maxP = Math.Max(maxP, d);
                if (d > 1e-4) fails.Add($"{what}: position off by {d:E2} m");
            }
            void Yaw(string what, Quaternion q, double yawJs)
            {
                // the Unity yaw about +Y, mapped back to JS: rotJs = -yawUnity
                var f = q * Vector3.forward;
                double yawU = Math.Atan2(f.x, f.z);
                double back = -yawU;
                double d = Math.Abs(Math.IEEERemainder(back - yawJs, 2 * Math.PI)) * 180 / Math.PI;
                maxA = Math.Max(maxA, d);
                if (d > 0.01) fails.Add($"{what}: yaw off by {d:F4} deg");
            }

            // buildings (JSON values, not the asset's, so the import is covered too)
            var bj = CanonJson.Arr(CanonJson.Get(WorldTestUtil.Json("placements.json"), "buildings"));
            Assert.AreEqual(bj.Count, a.placements.buildings.Length);
            for (int i = 0; i < bj.Count; i++)
            {
                var j = CanonJson.Obj(bj[i]);
                var pose = CanonTransforms.Building(a.placements.buildings[i]);
                var name = $"building {i} {CanonJson.Str(j, "model")}";
                P(name, pose.position, CanonJson.Num(j, "x"), CanonJson.Num(j, "y"), CanonJson.Num(j, "z"));
                Yaw(name, pose.rotation, CanonJson.Num(j, "rot"));
                if (Math.Abs(pose.scale.x - CanonJson.Num(j, "sx")) > 1e-5 || Math.Abs(pose.scale.z - CanonJson.Num(j, "sz")) > 1e-5 || Math.Abs(pose.scale.y - 1) > 1e-5)
                    fails.Add($"{name}: scale {pose.scale}");
                // the whole matrix: S * M_unity * S == M_js
                var mU = pose.Matrix;
                var e = CanonJson.Doubles(CanonJson.Get(j, "M"));
                var back = BrCoords.Mat(new[] { mU.m00, mU.m10, mU.m20, mU.m30, mU.m01, mU.m11, mU.m21, mU.m31, mU.m02, mU.m12, mU.m22, mU.m32, mU.m03, mU.m13, mU.m23, mU.m33 });
                for (int k = 0; k < 16; k++)
                {
                    double got = back[k % 4, k / 4];
                    if (Math.Abs(got - e[k]) > 1e-4) { fails.Add($"{name}: M[{k}] {got} vs {e[k]}"); break; }
                }
                // handedness: the model's front (local +Z) maps to the flipped JS front
                var fJs = new Vector3((float)e[8], (float)e[9], (float)e[10]).normalized;
                var fU = (mU.MultiplyVector(Vector3.forward)).normalized;
                if ((fU - BrCoords.Dir(fJs.x, fJs.y, fJs.z)).magnitude > 1e-4) fails.Add($"{name}: front {fU} vs flipped {fJs}");
            }

            // trees
            var tj = CanonJson.Arr(CanonJson.Get(WorldTestUtil.Json("vegetation.json"), "trees"));
            Assert.AreEqual(tj.Count, a.vegetation.trees.Length);
            for (int i = 0; i < tj.Count; i++)
            {
                var t = CanonJson.Doubles(tj[i]);
                var pose = CanonTransforms.Tree(a.vegetation.trees[i]);
                P($"tree {i}", pose.position + new Vector3(0, 0.2f, 0), t[0], t[1], t[2]);
                Yaw($"tree {i}", pose.rotation, t[5]);
                if (Math.Abs(pose.scale.x - t[4]) > 1e-5) fails.Add($"tree {i}: scale {pose.scale.x} vs {t[4]}");
                if (a.vegetation.trees[i].sp != (int)t[3]) fails.Add($"tree {i}: species");
            }

            // typed records
            var yj = CanonJson.Arr(CanonJson.Get(WorldTestUtil.Json("placements.json"), "typed"));
            Assert.AreEqual(yj.Count, a.placements.typed.Length);
            for (int i = 0; i < yj.Count; i++)
            {
                var j = CanonJson.Obj(yj[i]);
                var rec = a.placements.typed[i];
                var pose = CanonTransforms.Typed(rec);
                var type = CanonJson.Str(j, "type");
                var name = $"typed {i} {type}";
                if (rec.type != type) fails.Add($"{name}: type {rec.type}");
                if (type == "dock")
                {
                    var q = CanonJson.Obj(CanonJson.Get(j, "q"));
                    P(name, pose.position, CanonJson.Num(q, "x"), 0, CanonJson.Num(q, "z"));
                    Yaw(name, pose.rotation, Math.Atan2(CanonJson.Num(q, "ux"), CanonJson.Num(q, "uz")));
                    continue;
                }
                P(name, pose.position, CanonJson.Num(j, "x"), CanonJson.Num(j, "y", 0), CanonJson.Num(j, "z"));
                double yaw = type == "lamp" ? CanonJson.Num(j, "faceRot") : CanonJson.Num(j, "rot", 0);
                Yaw(name, pose.rotation, yaw);
                foreach (var kv in j)
                {
                    if (!(kv.Value is double v) || kv.Key == "x" || kv.Key == "y" || kv.Key == "z" || kv.Key == "rot") continue;
                    if (!rec.TryGet(kv.Key, out var got) || Math.Abs(got - v) > 1e-9) fails.Add($"{name}: {kv.Key} {got} vs {v}");
                }
            }
            TestContext.WriteLine($"PlacementsRoundTrip: {bj.Count} buildings, {tj.Count} trees, {yj.Count} typed records; max position error {maxP:E2} m, max yaw error {maxA:E2} deg");
            Assert.IsEmpty(fails, string.Join("\n", fails.Take(40)));
        }

        /// <summary>The 12 reference cameras convert to Unity poses that look at their flipped targets.</summary>
        [Test]
        public void CamerasLookAtTargets()
        {
            var w = WorldTestUtil.Canon().world;
            foreach (var c in w.cameras)
            {
                var pose = CanonTransforms.Camera(c);
                var want = (BrCoords.Pos(c.target.x, c.target.y, c.target.z) - pose.position).normalized;
                var fwd = pose.rotation * Vector3.forward;
                Assert.Less((fwd - want).magnitude, 1e-4f, c.id + " forward");
                Assert.Greater((pose.rotation * Vector3.up).y, 0f, c.id + " up");
            }
        }

        /// <summary>ColliderGrid (16 m cells) returns exactly the records a brute-force overlap of their cell squares finds.</summary>
        [Test]
        public void ColliderGridQueries()
        {
            var a = WorldTestUtil.Canon();
            using var grid = ColliderGrid.Build(a.placements.colliders, WorldGrid.LayoutOf(a));
            Assert.AreEqual(64, grid.CellsPerSide, "16 m cells over the canon test world");
            Assert.AreEqual(4013, grid.Count);
            var rng = new Unity.Mathematics.Random(7);
            var res = new List<int>();
            for (int q = 0; q < 300; q++)
            {
                float x = rng.NextFloat(-500, 500), z = rng.NextFloat(-500, 500), r = rng.NextFloat(0.5f, 20f);
                grid.QueryJs(x, z, r, res);
                Assert.AreEqual(res.Count, res.Distinct().Count(), "duplicates");
                // every record whose centre lies within r of the query must be found
                for (int i = 0; i < grid.Count; i++)
                {
                    var c = grid.Records[i];
                    if ((c.x - x) * (c.x - x) + (c.z - z) * (c.z - z) <= r * r) Assert.Contains(i, res, $"query ({x}, {z}, {r}) misses record {i}");
                }
            }
        }

        /// <summary>The server canary line (T1.2): "CANON v1 OK H=a8908d7f...".</summary>
        [Test]
        public void ServerCanaryLine()
        {
            WorldTestUtil.Canon();
            Assert.IsTrue(CanonRuntime.LoadAndVerify(out var line), line);
            StringAssert.StartsWith("CANON v1 OK H=a8908d7f", line);
            TestContext.WriteLine(line);
        }

        /// <summary>DeltaUm starts zeroed and H equals H0 bit for bit; RebuildH keeps that where nothing was deformed.</summary>
        [Test]
        public void DeltaZeroMeansCanon()
        {
            var g = WorldTestUtil.Grid();
            for (int k = 0; k < g.Layout.Count; k += 997) Assert.AreEqual(0, g.DeltaUm[k]);
            g.RebuildH(0, 0, g.Layout.N, g.Layout.N);
            for (int k = 0; k < g.Layout.Count; k += 13)
                Assert.AreEqual(BitConverter.SingleToInt32Bits(g.H0[k]), BitConverter.SingleToInt32Bits(g.H[k]), "H != H0 at " + k);
        }
    }
}
