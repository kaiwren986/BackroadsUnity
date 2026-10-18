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
            var chunks = new int2[TerrainChunkSystem.ChunkCount];
            var lods = new int[chunks.Length];
            for (int i = 0; i < chunks.Length; i++) chunks[i] = new int2(i % 16, i / 16);
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
                            int i = iB + c, j = jB + r, k = j * WorldGrid.VN + i;
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
                    float skirtWant = (float)((double)grid.H[jB * WorldGrid.VN + iB] - 1.9);
                    if (Bits(v[s0].y) != Bits(skirtWant)) fails.Add($"chunk {chunks[m]} skirt y {v[s0].y:R} vs {skirtWant:R}");
                    // normals: unit, and the flat-ish ground faces up; JS normal x flipped
                    int kc = (jB + 64) * WorldGrid.VN + iB + 64;
                    float nx = grid.H[kc - 1] - grid.H[kc + 1], nz = grid.H[kc - WorldGrid.VN] - grid.H[kc + WorldGrid.VN];
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

        static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
    }
}
