using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Backroads.Canon.Editor;
using Backroads.Client.Rendering;
using Backroads.Core;
using Backroads.World;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Backroads.Tests.EditMode.World
{
    /// <summary>Render.* gates of PLAN 6.1 M1 (T1.3, T1.4): terrain LOD0 bit-exactness, pane names, static prop buckets.</summary>
    [TestFixture, Category("Render")]
    public class Render
    {
        /// <summary>
        /// All 256 LOD0 chunks: every grid vertex position bit-exact against BrCoords.Pos(i*0.5 - 512, H[k], j*0.5 - 512);
        /// skirts at H - 1.9 (double, then float32, as JS); normals unit length with x flipped; winding facing up.
        /// </summary>
        [Test]
        public void TerrainLod0BitExact()
        {
            var grid = WorldTestUtil.Grid();
            int side = grid.Layout.ChunksPerSide, VN = grid.Layout.VN;
            Assert.AreEqual(16, side, "the canon test world has 16 x 16 chunks");
            var chunks = new int2[side * side];
            var lods = new int[chunks.Length];
            for (int i = 0; i < chunks.Length; i++) chunks[i] = new int2(i % side, i / side);
            var sw = Stopwatch.StartNew();
            var meshes = TerrainChunkSystem.BuildMeshes(grid.View, chunks, lods);
            double ms = sw.Elapsed.TotalMilliseconds;
            try
            {
                long checkedVerts = 0, bad = 0;
                var fails = new List<string>();
                for (int m = 0; m < meshes.Length; m++)
                {
                    var v = meshes[m].vertices;
                    var nrm = meshes[m].normals;
                    Assert.AreEqual(TerrainChunkBuildJob.VertexCount(0), v.Length);
                    int vn = 129, iB = chunks[m].x * 128, jB = chunks[m].y * 128;
                    for (int r = 0; r <= 128; r++)
                        for (int c = 0; c <= 128; c++)
                        {
                            int i = iB + c, j = jB + r, k = j * VN + i;
                            var want = BrCoords.Pos(i * 0.5f - 512f, grid.H[k], j * 0.5f - 512f);
                            var got = v[r * vn + c];
                            checkedVerts++;
                            if (Bits(got.x) != Bits(want.x) || Bits(got.y) != Bits(want.y) || Bits(got.z) != Bits(want.z))
                            {
                                bad++;
                                if (fails.Count < 10) fails.Add($"chunk {chunks[m]} vertex ({i}, {j}): {got:R} vs {want:R}");
                            }
                        }
                    // skirts: edge 0, first vertex sits 1.9 m below (step 1)
                    int s0 = vn * vn;
                    float skirtWant = (float)((double)grid.H[jB * VN + iB] - 1.9);
                    if (Bits(v[s0].y) != Bits(skirtWant)) fails.Add($"chunk {chunks[m]} skirt y {v[s0].y:R} vs {skirtWant:R}");
                    // normals: unit, and the flat-ish ground faces up; JS normal x flipped
                    int kc = (jB + 64) * VN + iB + 64;
                    float nx = grid.H[kc - 1] - grid.H[kc + 1], nz = grid.H[kc - VN] - grid.H[kc + VN];
                    var nWant = new Vector3(-nx, 1, nz).normalized;
                    if ((nrm[64 * vn + 64] - nWant).magnitude > 1e-5f) fails.Add($"chunk {chunks[m]} normal {nrm[64 * vn + 64]} vs {nWant}");
                    // winding: the first grid triangle's geometric normal points up (front face toward the sky)
                    var tri = meshes[m].triangles;
                    var gn = Vector3.Cross(v[tri[1]] - v[tri[0]], v[tri[2]] - v[tri[0]]);
                    if (gn.y <= 0) fails.Add($"chunk {chunks[m]} first triangle faces down ({gn})");
                }
                TestContext.WriteLine($"TerrainLod0BitExact: {meshes.Length} chunks, {checkedVerts} grid vertices, {bad} differ; all 256 LOD0 chunks built in {ms:F0} ms");
                Assert.AreEqual(256, meshes.Length);
                Assert.AreEqual(256L * 129 * 129, checkedVerts);
                Assert.IsEmpty(fails, string.Join("\n", fails));
            }
            finally
            {
                foreach (var m in meshes) Object.DestroyImmediate(m);
            }
        }

        /// <summary>Load-time build of all 256 chunks at their LOD for each reference camera, timed (gate: at most 1.5 s).</summary>
        [Test]
        public void TerrainBuildAllTimed()
        {
            var a = WorldTestUtil.Canon();
            var grid = WorldTestUtil.Grid();
            var mat = new Material(Shader.Find("Hidden/InternalErrorShader"));
            var root = new GameObject("terrain-test");
            try
            {
                double worst = 0;
                foreach (var cam in a.world.cameras)
                {
                    var sys = new TerrainChunkSystem(grid, mat, root.transform) { LodScale = 1.3f };
                    var pos = CanonTransforms.Camera(cam).position;
                    double ms = sys.BuildAll(pos);
                    worst = Math.Max(worst, ms);
                    TestContext.WriteLine($"{cam.id}: all chunks in {ms:F0} ms (LOD0 {sys.CountAtLod(0)}, LOD1 {sys.CountAtLod(1)}, LOD2 {sys.CountAtLod(2)}, LOD3 {sys.CountAtLod(3)})");
                    for (int cz = 0; cz < 16; cz++)
                        for (int cx = 0; cx < 16; cx++)
                            Assert.GreaterOrEqual(sys.LodOf(cx, cz), 0, "chunk not built");
                    sys.Dispose();
                }
                TestContext.WriteLine($"worst load-time build {worst:F0} ms");
                Assert.LessOrEqual(worst, 1500, "all chunks built within 1.5 s at load");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(mat);
            }
        }

        /// <summary>
        /// Pane names resolve for all 11 models of buildings.glb (glTFast import, three.js-sanitised names in traverse
        /// order): the counts of 03 section 3.3 (House_B 22), every placement's panes list equals its model's panes,
        /// lit strings have one flag per pane, and BuildingSpawner gives exactly the lit panes the lit material.
        /// </summary>
        [Test]
        public void PaneNamesResolve()
        {
            var a = WorldTestUtil.Canon();
            var e = CanonJson.Obj(CanonJson.Get(WorldTestUtil.Expected(), "panes"));
            var fails = new List<string>();
            Assert.AreEqual(11, a.buildings.models.Length, "models in buildings.glb");
            foreach (var kv in e)
            {
                var m = a.buildings.Find(kv.Key);
                if (m == null) { fails.Add("model missing: " + kv.Key); continue; }
                int want = (int)CanonJson.Num(kv.Value);
                if (m.PaneCount != want) fails.Add($"{kv.Key}: {m.PaneCount} panes, expected {want}");
                foreach (var p in m.parts.Where(p => p.isPane))
                    if (!System.Text.RegularExpressions.Regex.IsMatch(p.node, @"^Glass(\.\d{3})?$")) fails.Add($"{kv.Key}: pane node {p.node}");
            }
            Assert.AreEqual(22, a.buildings.Find("House_B").PaneCount, "House_B panes");

            int litFlags = 0;
            foreach (var b in a.placements.buildings)
            {
                var m = a.buildings.Find(b.model);
                var names = m.parts.Where(p => p.isPane).Select(p => p.threeName).ToArray();
                if (!names.SequenceEqual(b.panes)) fails.Add($"{b.model} at ({b.x:F1}, {b.z:F1}): panes [{string.Join(",", b.panes.Take(4))}...] vs model [{string.Join(",", names.Take(4))}...]");
                if (b.lit.Length != names.Length) fails.Add($"{b.model}: lit string {b.lit.Length} flags for {names.Length} panes");
                litFlags += b.lit.Count(c => c == '1');
            }

            var look = ScriptableObject.CreateInstance<MillbrookLook>();
            var sh = Shader.Find("Hidden/InternalErrorShader");
            look.buildingPart = new Material(sh); look.buildingMetal = new Material(sh); look.bulb = new Material(sh); look.sign = new Material(sh);
            look.glassLit = new Material(sh) { name = "lit" }; look.glassDark = new Material(sh) { name = "dark" };
            var root = new GameObject("pane-test");
            try
            {
                var spawner = new BuildingSpawner(a, look, root.transform);
                Assert.AreEqual(72, spawner.Buildings);
                int litRenderers = root.GetComponentsInChildren<MeshRenderer>().Count(r => r.sharedMaterial == look.glassLit);
                Assert.AreEqual(litFlags, spawner.LitPanes, "lit panes counted");
                Assert.AreEqual(litFlags, litRenderers, "renderers with the lit window material");
                TestContext.WriteLine($"PaneNamesResolve: 11 models, {spawner.Panes} placed panes, {spawner.LitPanes} lit, {spawner.MaterialInstances} recolourable material instances");
                spawner.Destroy();
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach (var m in new[] { look.buildingPart, look.buildingMetal, look.bulb, look.sign, look.glassLit, look.glassDark }) Object.DestroyImmediate(m);
                Object.DestroyImmediate(look);
            }
            Assert.IsEmpty(fails, string.Join("\n", fails.Take(30)));
        }

        /// <summary>
        /// static_props.glb: the 7 bucket vertex counts and POSITION hashes (M0 gate, manifest staticProps), read again
        /// from the GLB; the importer's split keeps every vertex (chunks + building vertices = bucket, position sums agree);
        /// the removed building vertices equal what buildings.glb contributes.
        /// </summary>
        [Test]
        public void StaticPropsBuckets()
        {
            var a = WorldTestUtil.Canon();
            var glb = GlbReader.Load(System.IO.Path.Combine(CanonImporter.SourceDir, "reference", "static_props.glb"));
            var manifest = CanonJson.Obj(CanonJson.Get(WorldTestUtil.Json("manifest.json"), "staticProps"));
            var counts = CanonJson.Obj(CanonJson.Get(WorldTestUtil.Expected(), "staticPropsVertices"));
            var fails = new List<string>();
            var kept = new long[7];
            var sums = new double[7, 3];
            foreach (var c in a.staticProps.chunks)
            {
                var v = c.mesh.vertices;
                for (int s = 0; s < c.buckets.Length; s++)
                {
                    var tri = c.mesh.GetTriangles(s);
                    kept[c.buckets[s]] += tri.Length;
                    foreach (var i in tri) { sums[c.buckets[s], 0] += -v[i].x; sums[c.buckets[s], 1] += v[i].y; sums[c.buckets[s], 2] += v[i].z; }
                }
            }
            for (int b = 0; b < 7; b++)
            {
                var name = StaticPropsSet.BucketNames[b];
                var node = glb.FindNode(name);
                var prim = glb.Primitives((int)CanonJson.Num(glb.Node(node), "mesh"))[0];
                int count = glb.Count(prim.position);
                string sha;
                using (var h = System.Security.Cryptography.SHA256.Create()) sha = CanonRuntime.Hex(h.ComputeHash(glb.RawBytes(prim.position)));
                var want = CanonJson.Obj(manifest[name]);
                if (count != (int)CanonJson.Num(counts, name)) fails.Add($"{name}: {count} vertices, expected {CanonJson.Num(counts, name)}");
                if (sha != CanonJson.Str(want, "positionSha256")) fails.Add($"{name}: position hash {sha}");
                var st = a.staticProps.buckets[b];
                if (st.vertices != count || st.positionSha256 != sha) fails.Add($"{name}: imported stats differ");
                if (st.buildingVertices + st.keptVertices != count) fails.Add($"{name}: {st.buildingVertices} + {st.keptVertices} != {count}");
                if (kept[b] != st.keptVertices) fails.Add($"{name}: chunks hold {kept[b]} vertices, stats say {st.keptVertices}");
                if (st.buildingVertices != st.expectedBuildingVertices) fails.Add($"{name}: removed {st.buildingVertices} building vertices, buildings.glb accounts for {st.expectedBuildingVertices}");
                for (int k = 0; k < 3; k++)
                {
                    double whole = sums[b, k] + st.buildingSum[k];
                    if (Math.Abs(whole - st.sourceSum[k]) > 1e-3 * Math.Max(1, Math.Abs(st.sourceSum[k]) / 1e5)) fails.Add($"{name}: position sum[{k}] {whole} vs GLB {st.sourceSum[k]}");
                }
                TestContext.WriteLine($"{name}: {count} vertices, sha {sha.Substring(0, 16)}, {st.buildingVertices} in buildings, {st.keptVertices} in chunks");
            }
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }

        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
    }
}
