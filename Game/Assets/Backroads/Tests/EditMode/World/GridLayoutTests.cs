using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Backroads.Canon.Editor;
using Backroads.Client.Rendering;
using Backroads.Core;
using Backroads.World;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Backroads.Tests.EditMode.World
{
    /// <summary>
    /// T3.1 (ADR-006 section 4): the grid's size comes from the world's manifest (WorldGridLayout). The canon test world
    /// keeps its 2,049 x 2,049 layout and its numbers bit for bit; a synthetic 4,097 x 4,097 world (flat, with a ramp)
    /// passes the probe, ray, terrain-chunk and collider checks at both corners; the float queries cost what they did in M2.
    /// </summary>
    [TestFixture, Category("World")]
    public class GridLayout
    {
        // ------------------------------------------------------------------ the layout

        /// <summary>CanonData/v1/manifest.json's grid block gives the canon test world's layout, and the runtime grid uses it.</summary>
        [Test]
        public void LayoutFromManifest()
        {
            var fromManifest = WorldGridLayout.FromManifestFile(Path.Combine(CanonImporter.RepoRoot, "CanonData", "v1", "manifest.json"));
            Assert.AreEqual(WorldGridLayout.CanonV1, fromManifest, "manifest grid vs CanonV1");
            Assert.AreEqual(2049, fromManifest.VN);
            Assert.AreEqual(2048, fromManifest.N);
            Assert.AreEqual(0.5f, fromManifest.Cell);
            Assert.AreEqual(512f, fromManifest.Half);
            Assert.AreEqual(129, fromManifest.TintN);
            Assert.AreEqual(16, fromManifest.ChunksPerSide);
            Assert.AreEqual(64, fromManifest.ColliderCellsPerSide);
            Assert.AreEqual(19, fromManifest.CellShift);
            Assert.AreEqual(512L << 20, fromManifest.OriginQ20);
            Assert.AreEqual(2.0f, fromManifest.InvCell);
            Assert.AreEqual(16f, fromManifest.TintStep);

            var a = WorldTestUtil.Canon();
            Assert.AreEqual(fromManifest, WorldGrid.LayoutOf(a), "imported canon (world info) vs manifest");
            var g = WorldTestUtil.Grid();
            Assert.AreEqual(fromManifest, g.Layout, "CanonRuntime.Grid");
            Assert.AreEqual(fromManifest, g.View.Layout, "the view carries the layout");
            Assert.AreEqual(2049 * 2049, g.H.Length);
            TestContext.WriteLine("canon test world: " + fromManifest);
        }

        /// <summary>The new Millbrook's layout (ADR-006 section 4) from a WorldData-style manifest, and the checks on bad ones.</summary>
        [Test]
        public void MillbrookLayoutAndChecks()
        {
            const string manifest = "{ \"format\": \"backroads-world\", \"grid\": { \"size\": 2048, \"half\": 1024, \"cell\": 0.5, \"N\": 4096, " +
                                    "\"VN\": 4097, \"waterY\": 0, \"tintN\": 257, \"tintCell\": 8 }, \"files\": {} }";
            var l = WorldGridLayout.FromManifestJson(manifest);
            Assert.AreEqual(WorldGridLayout.Millbrook, l);
            Assert.AreEqual(4097 * 4097, l.Count);
            Assert.AreEqual(32, l.ChunksPerSide, "terrain chunks per side");
            Assert.AreEqual(128, l.ColliderCellsPerSide, "16 m collider cells per side");
            Assert.AreEqual(1024L << 20, l.OriginQ20);
            Assert.AreEqual(4096 - 1e-3f, l.MaxF);
            TestContext.WriteLine("Millbrook: " + l);

            Assert.Throws<ArgumentException>(() => WorldGridLayout.FromManifestJson("{ \"grid\": { \"VN\": 4097, \"cell\": 0.5, \"half\": 1000, \"tintN\": 257, \"tintCell\": 8 } }"), "span");
            Assert.Throws<ArgumentException>(() => WorldGridLayout.FromManifestJson("{ \"grid\": { \"VN\": 4097, \"cell\": 0.5, \"half\": 1024, \"tintN\": 129, \"tintCell\": 8 } }"), "tint span");
            Assert.Throws<ArgumentException>(() => WorldGridLayout.FromManifestJson("{ \"grid\": { \"VN\": 2049, \"cell\": 0.6, \"half\": 614.4, \"tintN\": 129, \"tintCell\": 9.6 } }"), "cell not a power of two");
            Assert.Throws<ArgumentException>(() => WorldGridLayout.FromManifestJson("{ \"grid\": { \"VN\": 2001, \"cell\": 0.5, \"half\": 500, \"tintN\": 126, \"tintCell\": 8 } }"), "not whole chunks");
            Assert.Throws<ArgumentException>(() => WorldGridLayout.FromManifestJson("{ \"grid\": { \"VN\": 4097, \"cell\": 0.5, \"half\": 1024, \"tintN\": 257, \"tintCell\": 8, \"N\": 4095 } }"), "N");
            Assert.Throws<ArgumentException>(() => WorldGridLayout.FromManifestJson("{ \"format\": 1 }"), "no grid");
        }

        // ------------------------------------------------------------------ a synthetic 4,097 x 4,097 world

        // Flat at 5 m, then a 10 % ramp up in +x (JS) from x = 600 m to the edge; the kink lies on a vertex column, so
        // bilinear interpolation reproduces the function exactly. Surfaces by quadrant: 0 (x < 0, z < 0), 1, 2, 3.
        const float Flat = 5f, RampX = 600f, Slope = 0.1f;
        static double H(double x) => x > RampX ? Flat + Slope * (x - RampX) : Flat;
        static byte Quadrant(double x, double z) => (byte)((x < 0 ? 0 : 1) + (z < 0 ? 0 : 2));

        WorldGrid _big;

        WorldGrid Big()
        {
            if (_big != null) return _big;
            var sw = Stopwatch.StartNew();
            _big = WorldGrid.FromFunctions(WorldGridLayout.Millbrook, (x, z) => (float)H(x), (x, z) => Quadrant(x, z));
            TestContext.WriteLine($"synthetic 4,097^2 grid built in {sw.ElapsedMilliseconds} ms");
            return _big;
        }

        [OneTimeTearDown]
        public void TearDown()
        {
            _big?.Dispose();
            _big = null;
        }

        static IEnumerable<double2> ProbePoints(float half, int n, uint seed)
        {
            // the four corners exactly, points just inside them, the ramp's kink, then seeded points over the whole square
            foreach (var sx in new[] { -1, 1 })
                foreach (var sz in new[] { -1, 1 })
                {
                    yield return new double2(sx * half, sz * half);
                    yield return new double2(sx * (half - 0.13), sz * (half - 0.37));
                    yield return new double2(sx * (half - 3.21), sz * (half - 1.73));
                }
            yield return new double2(RampX, 17.25);
            var rng = new Unity.Mathematics.Random(seed);
            for (int i = 0; i < n; i++) yield return new double2(rng.NextDouble(-half, half), rng.NextDouble(-half, half));
        }

        /// <summary>
        /// Heights (reference and float path, JS and Unity space), normals and surfaces of the 4,097^2 world agree with the
        /// function it was made from, everywhere and at both corners; edges clamp; DeltaUm zero means H = H0.
        /// </summary>
        [Test]
        public void LargeGridProbes()
        {
            var g = Big();
            var v = g.View;
            var l = g.Layout;
            Assert.AreEqual(4097, l.VN);
            Assert.AreEqual(4097 * 4097, g.H.Length);
            double maxRef = 0, maxF = 0, maxN = 0;
            int surfBad = 0, n = 0;
            var fails = new List<string>();
            foreach (var p in ProbePoints(l.Half, 4000, 31))
            {
                n++;
                double x = p.x, z = p.y;
                // clamped like world.js heightAt: the last cell's far edge is read at N - 1e-4 cells
                double xc = Math.Min(x, l.Half - 1e-4 * l.Cell);
                double want = H(xc);
                double href = WorldGridQueriesRef.HeightAtJs(v, x, z);
                double hrefU = WorldGridQueriesRef.HeightAt(v, new double3(-x, 0, z));
                float hf = WorldGridQueries.HeightAtJs(v, (float)x, (float)z);
                float hfU = WorldGridQueries.HeightAt(v, new float3((float)-x, 0, (float)z));
                maxRef = Math.Max(maxRef, Math.Abs(href - want));
                maxF = Math.Max(maxF, Math.Abs(hf - want));
                if (Math.Abs(href - want) > 1e-5 || Math.Abs(hf - want) > 2e-4 || href != hrefU || hf != hfU)
                    if (fails.Count < 10) fails.Add($"({x:F3}, {z:F3}): ref {href:R} / {hrefU:R}, float {hf:R} / {hfU:R}, want {want:R}");
                // normals, away from the kink: flat (0, 1, 0); ramp (slope, 1, 0) normalised in Unity space (x flipped)
                if (Math.Abs(x - RampX) > 1.5 && Math.Abs(x) < l.Half - 1 && Math.Abs(z) < l.Half - 1)
                {
                    var wantN = x > RampX ? math.normalize(new double3(Slope, 1, 0)) : new double3(0, 1, 0);
                    var nr = WorldGridQueriesRef.NormalAt(v, new double3(-x, 0, z));
                    var nf = WorldGridQueries.NormalAt(v, new float3((float)-x, 0, (float)z));
                    double dn = Math.Max(math.cmax(math.abs(nr - wantN)), math.cmax(math.abs((double3)nf - wantN)));
                    maxN = Math.Max(maxN, dn);
                    if (dn > 1e-4 && fails.Count < 10) fails.Add($"({x:F3}, {z:F3}): normal ref {nr}, float {nf}, want {wantN}");
                }
                // surfaces: the nearest vertex's quadrant (skip probes within a cell of an axis)
                if (Math.Abs(x) > 0.5 && Math.Abs(z) > 0.5)
                {
                    byte want4 = Quadrant(x, z);
                    if (WorldGridQueriesRef.SurfaceAtJs(v, x, z) != want4 || WorldGridQueries.SurfaceAtJs(v, (float)x, (float)z) != want4) surfBad++;
                }
            }
            // nearest vertex at and beyond the corners
            Assert.AreEqual(0, WorldGridQueries.NearestKJs(l, -l.Half, -l.Half), "nearest at (-half, -half)");
            Assert.AreEqual(l.Count - 1, WorldGridQueries.NearestKJs(l, l.Half, l.Half), "nearest at (half, half)");
            Assert.AreEqual(l.Count - 1, WorldGridQueries.NearestKJs(l, l.Half + 50, l.Half + 50), "clamped beyond (half, half)");
            Assert.AreEqual(l.VN - 1, WorldGridQueriesRef.NearestKJs(l, l.Half, -l.Half - 9), "clamped corner (half, -half)");
            Assert.AreEqual(l.N * l.VN, WorldGridQueriesRef.NearestKJs(l, -l.Half, l.Half), "corner (-half, half)");
            Assert.AreEqual(3, WorldGridQueries.SurfaceAt(v, new float3(-(l.Half - 0.2f), 0, l.Half - 0.2f)), "surface at the (+, +) corner, Unity space");
            Assert.AreEqual(0, WorldGridQueries.SurfaceAt(v, new float3(l.Half - 0.2f, 0, -(l.Half - 0.2f))), "surface at the (-, -) corner, Unity space");
            Assert.IsTrue(l.InBoundsJs(l.Half - 2.5f, -(l.Half - 2.5f)));
            Assert.IsFalse(l.InBoundsJs(l.Half - 1.5f, 0));
            Assert.IsFalse(WorldGridQueries.InBoundsJs(v, 0, -(l.Half - 1f)));
            // H stays H0 where nothing was deformed, over the whole grid
            g.RebuildH(0, 0, l.N, l.N);
            int hDiff = 0;
            for (int k = 0; k < l.Count; k += 7) if (BitConverter.SingleToInt32Bits(g.H[k]) != BitConverter.SingleToInt32Bits(g.H0[k])) hDiff++;
            TestContext.WriteLine($"LargeGridProbes: {n} probes; max |dh| reference {maxRef:E2} m, float {maxF:E2} m; max |dn| {maxN:E2}; surface mismatches {surfBad}; H != H0 at {hDiff} sampled vertices");
            Assert.IsEmpty(fails, string.Join("\n", fails));
            Assert.AreEqual(0, surfBad, "surface lookups");
            Assert.AreEqual(0, hDiff, "RebuildH changed undeformed heights");
        }

        /// <summary>
        /// Rays on the 4,097^2 world: straight down everywhere, slanted onto the flat near the (-, -) corner and onto the
        /// ramp near the (+, +) corner, all within the march's resolution of the exact hit (reference and Burst float
        /// paths); rays that leave upwards miss.
        /// </summary>
        [Test]
        public void LargeGridRays()
        {
            var g = Big();
            var v = g.View;
            float half = g.Layout.Half;
            var origins = new List<double3>();
            var dirs = new List<double3>();
            var want = new List<double>();
            var rng = new Unity.Mathematics.Random(77);
            // straight down from 200 m
            foreach (var p in ProbePoints(half - 0.01f, 400, 5))
            {
                origins.Add(new double3(p.x, 200, p.y)); dirs.Add(new double3(0, -1, 0));
                want.Add(200 - H(Math.Min(p.x, half - 1e-4 * g.Layout.Cell)));
            }
            // slanted onto the flat near (-half, -half) and onto the ramp near (+half, +half), JS space
            for (int i = 0; i < 200; i++)
            {
                bool ramp = (i & 1) == 1;
                double s = ramp ? 1 : -1;
                double ox = s * (half - rng.NextDouble(30, 60)), oz = s * (half - rng.NextDouble(30, 60));
                var o = new double3(ox, H(ox) + rng.NextDouble(10, 40), oz);
                var d = math.normalize(new double3(s * rng.NextDouble(0.1, 0.5), -1, s * rng.NextDouble(0.1, 0.5)));
                // o.y + t d.y = Flat + (ramp ? Slope (o.x + t d.x - RampX) : 0)
                double t = ramp ? (Flat + Slope * (o.x - RampX) - o.y) / (d.y - Slope * d.x) : (Flat - o.y) / d.y;
                double hx = o.x + t * d.x, hz = o.z + t * d.z;
                if (Math.Abs(hx) > half - 0.5 || Math.Abs(hz) > half - 0.5 || (ramp && hx <= RampX)) continue;
                origins.Add(o); dirs.Add(d); want.Add(t);
            }
            // upwards: a miss
            origins.Add(new double3(half - 10, 50, half - 10)); dirs.Add(math.normalize(new double3(0.3, 1, 0.2))); want.Add(-1);
            origins.Add(new double3(-half + 10, 50, -half + 10)); dirs.Add(math.normalize(new double3(-0.3, 1, -0.2))); want.Add(-1);

            int n = origins.Count;
            using var on = new NativeArray<float3>(origins.Select(o => new float3((float)-o.x, (float)o.y, (float)o.z)).ToArray(), Allocator.TempJob);
            using var dn = new NativeArray<float3>(dirs.Select(d => new float3((float)-d.x, (float)d.y, (float)d.z)).ToArray(), Allocator.TempJob);
            using var hits = new NativeArray<float>(n, Allocator.TempJob);
            new RayQueryJob { Grid = v, Origins = on, Dirs = dn, Hits = hits, MaxT = 1500, Step = 0.5f }.Schedule(n, 32).Complete();
            var fails = new List<string>();
            double maxRef = 0, maxF = 0;
            for (int i = 0; i < n; i++)
            {
                var o = origins[i]; var d = dirs[i];
                double tr = WorldGridQueriesRef.RayTerrain(v, new double3(-o.x, o.y, o.z), new double3(-d.x, d.y, d.z), 1500, 0.5);
                double tf = hits[i];
                if (want[i] < 0)
                {
                    if (tr != -1 || tf != -1) fails.Add($"ray {i} should miss: ref {tr}, float {tf}");
                    continue;
                }
                // 0.5 m march + 7 bisections: the hit is within 0.5 / 2^8 m of the exact one along the ray
                double er = Math.Abs(tr - want[i]), ef = Math.Abs(tf - want[i]);
                maxRef = Math.Max(maxRef, er); maxF = Math.Max(maxF, ef);
                if ((er > 0.0025 || ef > 0.004) && fails.Count < 10) fails.Add($"ray {i} from {o} dir {d}: ref {tr:F4}, float {tf:F4}, want {want[i]:F4}");
            }
            TestContext.WriteLine($"LargeGridRays: {n} rays; max |dt| reference {maxRef:E2} m, float {maxF:E2} m");
            Assert.GreaterOrEqual(n, 450);
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        /// <summary>
        /// The terrain chunker follows the layout: 32 x 32 chunks on the 4,097^2 world; the corner chunks (0, 0) and
        /// (31, 31) put every grid vertex at BrCoords.Pos(i*0.5 - 1024, H, j*0.5 - 1024) bit for bit, and their bounds
        /// sit at the corners.
        /// </summary>
        [Test]
        public void LargeGridTerrainCorners()
        {
            var g = Big();
            var l = g.Layout;
            var root = new GameObject("large-terrain-test");
            try
            {
                var sys = new TerrainChunkSystem(g, null, root.transform);
                Assert.AreEqual(32, sys.ChunksPerSide);
                Assert.AreEqual(1024, sys.ChunkCount);
                var b0 = sys.BoundsOf(0, 0);
                var b1 = sys.BoundsOf(31, 31);
                Assert.AreEqual(l.Half, b0.max.x, 1e-3, "chunk (0, 0) starts at JS x = -half (Unity x = +half)");
                Assert.AreEqual(-l.Half, b0.min.z, 1e-3);
                Assert.AreEqual(-l.Half, b1.min.x, 1e-3, "chunk (31, 31) ends at JS x = +half");
                Assert.AreEqual(l.Half, b1.max.z, 1e-3);
                var chunks = new[] { new int2(0, 0), new int2(31, 31), new int2(31, 0) };
                var meshes = TerrainChunkSystem.BuildMeshes(g.View, chunks, new[] { 0, 0, 0 });
                try
                {
                    long bad = 0, checkedVerts = 0;
                    var fails = new List<string>();
                    for (int m = 0; m < meshes.Length; m++)
                    {
                        var vtx = meshes[m].vertices;
                        int vn = 129, iB = chunks[m].x * 128, jB = chunks[m].y * 128;
                        for (int r = 0; r <= 128; r++)
                            for (int c = 0; c <= 128; c++)
                            {
                                int i = iB + c, j = jB + r, k = j * l.VN + i;
                                var want = BrCoords.Pos(i * 0.5f - 1024f, g.H[k], j * 0.5f - 1024f);
                                var got = vtx[r * vn + c];
                                checkedVerts++;
                                bool same = BitConverter.SingleToInt32Bits(got.x) == BitConverter.SingleToInt32Bits(want.x) &&
                                            BitConverter.SingleToInt32Bits(got.y) == BitConverter.SingleToInt32Bits(want.y) &&
                                            BitConverter.SingleToInt32Bits(got.z) == BitConverter.SingleToInt32Bits(want.z);
                                if (!same && fails.Count < 10) fails.Add($"chunk {chunks[m]} vertex ({i}, {j}): {got:R} vs {want:R}");
                                if (!same) bad++;
                            }
                    }
                    TestContext.WriteLine($"LargeGridTerrainCorners: {checkedVerts} corner-chunk vertices, {bad} differ");
                    Assert.IsEmpty(fails, string.Join("\n", fails));
                }
                finally { foreach (var m in meshes) Object.DestroyImmediate(m); }
                sys.MarkDirty(l.N - 3, l.N - 3, l.N, l.N);
                Assert.AreEqual(1, sys.DirtyChunks, "a dirty rectangle at the far corner marks the corner chunk");
                sys.Dispose();
            }
            finally { Object.DestroyImmediate(root); }
        }

        /// <summary>The static collider grid follows the layout: 128 x 128 cells of 16 m; records at both corners are found.</summary>
        [Test]
        public void LargeGridColliders()
        {
            var l = WorldGridLayout.Millbrook;
            var src = new[]
            {
                new PlacementSet.Collider { type = PlacementSet.ColliderType.Circle, kind = "rock", x = -l.Half + 3, z = -l.Half + 4, r = 1.5f, y0 = 0, y1 = 2, tree = -1 },
                new PlacementSet.Collider { type = PlacementSet.ColliderType.Box, kind = "building", x = l.Half - 6, z = l.Half - 5, hw = 3, hd = 2, rot = 0.4f, y0 = 0, y1 = 6, tree = -1 },
                new PlacementSet.Collider { type = PlacementSet.ColliderType.Circle, kind = "tree", x = 0.5f, z = -0.5f, r = 0.6f, y0 = 0, y1 = 9, tree = 0 },
            };
            using var grid = ColliderGrid.Build(src, l);
            Assert.AreEqual(128, grid.CellsPerSide);
            Assert.AreEqual(l.Half, grid.Half);
            var res = new List<int>();
            grid.QueryJs(-l.Half + 2, -l.Half + 2, 3, res);
            CollectionAssert.AreEqual(new[] { 0 }, res, "(-, -) corner");
            grid.QueryJs(l.Half - 4, l.Half - 4, 2, res);
            CollectionAssert.AreEqual(new[] { 1 }, res, "(+, +) corner");
            grid.Query(new float3(-(l.Half - 4), 1, l.Half - 4), 2, res);
            CollectionAssert.AreEqual(new[] { 1 }, res, "(+, +) corner from Unity space");
            grid.QueryJs(0, 0, 1, res);
            CollectionAssert.AreEqual(new[] { 2 }, res, "centre");
            Assert.GreaterOrEqual(grid.CellStart[128 * 128], 3, "every record is in some cell");
        }

        // ------------------------------------------------------------------ Perf: the step cost of heightAt / rayTerrain

        /// <summary>world.js heightAt / rayTerrain exactly as M2 had them (compile-time 2,049 x 2,049 at 0.5 m over 1,024 m).</summary>
        static class M2Queries
        {
            const int N = 2048, VN = 2049;
            const float Half = 512f, Cell = 0.5f;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static float HeightAtJs(in WorldGridView g, float x, float z)
            {
                float fx = (x + Half) / Cell, fz = (z + Half) / Cell;
                fx = math.clamp(fx, 0f, N - 1e-3f);
                fz = math.clamp(fz, 0f, N - 1e-3f);
                int i = (int)fx, j = (int)fz;
                float tx = fx - i, tz = fz - j;
                int k = j * VN + i;
                float a = g.H[k], b = g.H[k + 1], c = g.H[k + VN], d = g.H[k + VN + 1];
                return (a + (b - a) * tx) * (1f - tz) + (c + (d - c) * tx) * tz;
            }

            public static float RayTerrainJs(in WorldGridView g, float3 o, float3 d, float maxT, float step = 0.5f)
            {
                if (o.y - HeightAtJs(g, o.x, o.z) <= 0f) return 0f;
                float tPrev = 0f;
                for (float t = math.min(step, maxT); ; t = math.min(t + step, maxT))
                {
                    float3 p = o + d * t;
                    if (p.y - HeightAtJs(g, p.x, p.z) <= 0f)
                    {
                        float a = tPrev, b = t;
                        for (int k = 0; k < 7; k++)
                        {
                            float m = (a + b) * 0.5f;
                            float3 q = o + d * m;
                            if (q.y - HeightAtJs(g, q.x, q.z) > 0f) a = m; else b = m;
                        }
                        return (a + b) * 0.5f;
                    }
                    if (t >= maxT) return -1f;
                    tPrev = t;
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct HeightBench : IJob
        {
            public WorldGridView G;
            public bool M2;
            [ReadOnly] public NativeArray<float2> P;
            public NativeArray<float> Out;

            public void Execute()
            {
                if (M2) for (int i = 0; i < P.Length; i++) Out[i] = M2Queries.HeightAtJs(G, P[i].x, P[i].y);
                else for (int i = 0; i < P.Length; i++) Out[i] = WorldGridQueries.HeightAtJs(G, P[i].x, P[i].y);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct RayBench : IJob
        {
            public WorldGridView G;
            public bool M2;
            [ReadOnly] public NativeArray<float3> O, D;
            public NativeArray<float> Out;

            public void Execute()
            {
                if (M2) for (int i = 0; i < O.Length; i++) Out[i] = M2Queries.RayTerrainJs(G, O[i], D[i], 1500f, 0.5f);
                else for (int i = 0; i < O.Length; i++) Out[i] = WorldGridQueries.RayTerrainJs(G, O[i], D[i], 1500f, 0.5f);
            }
        }

        /// <summary>
        /// T3.1 Perf: on the canon test world, heightAt and rayTerrain from the layout give exactly M2's numbers (bit for
        /// bit, 1,000,000 heights and the 49,152 reference-camera rays) and cost at most 5 % more per step than M2's
        /// compile-time code (Burst, one thread; the two run interleaved, best of 41 short runs, re-measured up to 3 times;
        /// inconclusive when every attempt ran on a busy machine).
        /// </summary>
        [Test]
        public void StepCostMatchesM2()
        {
            var v = WorldTestUtil.Grid().View;
            var rng = new Unity.Mathematics.Random(2026);
            var ptsArr = new float2[1_000_000];
            for (int i = 0; i < ptsArr.Length; i++) ptsArr[i] = new float2(rng.NextFloat(-500f, 500f), rng.NextFloat(-500f, 500f));
            using var pts = new NativeArray<float2>(ptsArr, Allocator.TempJob);
            var os = new List<float3>();
            var ds = new List<float3>();
            foreach (var co in CanonJson.Arr(CanonJson.Get(WorldTestUtil.Json("terrain/probes.json"), "rays")))
            {
                var c = CanonJson.Obj(co);
                var o = CanonJson.Doubles(CanonJson.Get(c, "origin"));
                foreach (var ho in CanonJson.Arr(CanonJson.Get(c, "hits")))
                {
                    var h = CanonJson.Doubles(ho);
                    os.Add(new float3((float)o[0], (float)o[1], (float)o[2]));
                    ds.Add(new float3((float)h[0], (float)h[1], (float)h[2]));
                }
            }
            using var on = new NativeArray<float3>(os.ToArray(), Allocator.TempJob);
            using var dn = new NativeArray<float3>(ds.ToArray(), Allocator.TempJob);
            using var hNow = new NativeArray<float>(pts.Length, Allocator.TempJob);
            using var hM2 = new NativeArray<float>(pts.Length, Allocator.TempJob);
            using var rNow = new NativeArray<float>(on.Length, Allocator.TempJob);
            using var rM2 = new NativeArray<float>(on.Length, Allocator.TempJob);

            var hb = new HeightBench { G = v, P = pts };
            var rb = new RayBench { G = v, O = on, D = dn };
            double Time(Action a) { var sw = Stopwatch.StartNew(); a(); return sw.Elapsed.TotalMilliseconds; }
            void RunH(bool m2) { hb.M2 = m2; hb.Out = m2 ? hM2 : hNow; hb.Run(); }
            void RunR(bool m2) { rb.M2 = m2; rb.Out = m2 ? rM2 : rNow; rb.Run(); }

            // warm up (compiles both), then compare the numbers bit for bit
            RunH(false); RunH(true); RunR(false); RunR(true);
            int hDiff = 0, rDiff = 0;
            for (int i = 0; i < pts.Length; i++) if (BitConverter.SingleToInt32Bits(hNow[i]) != BitConverter.SingleToInt32Bits(hM2[i])) hDiff++;
            for (int i = 0; i < on.Length; i++) if (BitConverter.SingleToInt32Bits(rNow[i]) != BitConverter.SingleToInt32Bits(rM2[i])) rDiff++;
            Assert.AreEqual(0, hDiff, "heightAt from the layout differs from M2's");
            Assert.AreEqual(0, rDiff, "rayTerrain from the layout differs from M2's");

            // timing on slices (200,000 heights, 12,288 rays): many short runs, interleaved, best of each; a run on a busy
            // machine (the median far above the best) proves nothing either way, so it is re-measured, and if every
            // attempt is busy the test is inconclusive rather than failed
            var hbT = new HeightBench { G = v, P = pts.GetSubArray(0, 200000) };
            var rbT = new RayBench { G = v, O = on.GetSubArray(0, 12288), D = dn.GetSubArray(0, 12288) };
            void TH(bool m2) { hbT.M2 = m2; hbT.Out = (m2 ? hM2 : hNow).GetSubArray(0, 200000); hbT.Run(); }
            void TR(bool m2) { rbT.M2 = m2; rbT.Out = (m2 ? rM2 : rNow).GetSubArray(0, 12288); rbT.Run(); }
            double Best(List<double> l) => l.Min();
            double Median(List<double> l) { var c = l.OrderBy(x => x).ToList(); return c[c.Count / 2]; }
            double hRatio = 0, rRatio = 0, hNs = 0, hM2Ns = 0, rUs = 0, rM2Us = 0;
            bool passed = false, quietFail = false;
            for (int attempt = 1; attempt <= 3 && !passed; attempt++)
            {
                var hn = new List<double>(); var hm = new List<double>(); var rn = new List<double>(); var rm = new List<double>();
                for (int rep = 0; rep < 41; rep++)
                {
                    bool first = (rep & 1) == 0;   // alternate which one runs first
                    if (first) { hn.Add(Time(() => TH(false))); hm.Add(Time(() => TH(true))); rn.Add(Time(() => TR(false))); rm.Add(Time(() => TR(true))); }
                    else { hm.Add(Time(() => TH(true))); hn.Add(Time(() => TH(false))); rm.Add(Time(() => TR(true))); rn.Add(Time(() => TR(false))); }
                }
                hRatio = Best(hn) / Best(hm);
                rRatio = Best(rn) / Best(rm);
                hNs = Best(hn) * 1e6 / 200000; hM2Ns = Best(hm) * 1e6 / 200000; rUs = Best(rn) * 1e3 / 12288; rM2Us = Best(rm) * 1e3 / 12288;
                double noise = Math.Max(Math.Max(Median(hm) / Best(hm), Median(hn) / Best(hn)), Math.Max(Median(rm) / Best(rm), Median(rn) / Best(rn)));
                bool busy = noise > 1.15;
                TestContext.WriteLine($"StepCostMatchesM2 attempt {attempt}: heightAt {hNs:F2} ns vs M2 {hM2Ns:F2} ns ({hRatio:F3}); rayTerrain {rUs:F2} us vs M2 {rM2Us:F2} us ({rRatio:F3}) per ray; " +
                                      $"median/best {noise:F2}{(busy ? " (busy machine)" : "")}");
                passed = hRatio <= 1.05 && rRatio <= 1.05;
                if (!passed && !busy) quietFail = true;
            }
            WriteReport(hNs, hM2Ns, rUs, rM2Us);
            if (passed) return;
            if (!quietFail) Assert.Inconclusive("the machine was too busy to time the step cost (median far above the best in every attempt); rerun on a quiet machine");
            Assert.LessOrEqual(hRatio, 1.05, "heightAt step cost more than 5 % above M2");
            Assert.LessOrEqual(rRatio, 1.05, "rayTerrain step cost more than 5 % above M2");
        }

        static void WriteReport(double hNs, double hM2Ns, double rUs, double rM2Us)
        {
            try
            {
                var dir = Path.Combine(CanonImporter.RepoRoot, "artifacts", "world");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "grid-step-cost.json"), string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{{\n  \"heightAtNs\": {0:F3},\n  \"heightAtM2Ns\": {1:F3},\n  \"rayTerrainUs\": {2:F3},\n  \"rayTerrainM2Us\": {3:F3}\n}}\n", hNs, hM2Ns, rUs, rM2Us));
            }
            catch (Exception e) { TestContext.WriteLine("could not write artifacts/world/grid-step-cost.json: " + e.Message); }
        }
    }
}
