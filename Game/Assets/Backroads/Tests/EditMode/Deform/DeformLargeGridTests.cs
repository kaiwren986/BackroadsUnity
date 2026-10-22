using System;
using System.Collections.Generic;
using Backroads.Deform;
using Backroads.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Backroads.Tests.EditMode.Deform
{
    /// <summary>
    /// T3.1 deformation smoke test on a synthetic 4,097 x 4,097 world (the new Millbrook's layout, flat dirt): ruts and
    /// brushes at both corners (and on the last cells before the edges) change the ground where the JS mirror on the
    /// same layout does, within 0.5 mm; the dirty rectangles and tile versions land at the corners; the Burst and the
    /// managed kernels give the same integers. (As in world.js, ruts and brushes stop one vertex short of the edge; relax
    /// may move soil onto an edge vertex from its inner neighbour.)
    /// </summary>
    [TestFixture, Category("Deform")]
    public class DeformLargeGrid
    {
        const float Ground = 5f;
        const byte Dirt = 1;

        /// <summary>(JS x, z): near each corner, then on the last cells before the edges.</summary>
        static readonly float[][] Spots =
        {
            new[] { -1020.25f, -1019.75f }, new[] { 1020.5f, 1019.25f },
            new[] { -1023.75f, -1023.25f }, new[] { 1023.5f, 1023.75f },
        };

        static void Calls(IStampSink sink, JsDeformReference js)
        {
            foreach (var p in Spots)
            {
                float x = p[0], z = p[1];
                for (int n = 0; n < 6; n++)
                {
                    sink?.Rut(x + n * 0.25f, z, 0.75f, 0.08f, 0.02f, 6f, 0.45f, 1f, 0f);
                    js?.Rut(x + n * 0.25f, z, 0.75, 0.08, 0.02, 6, 0.45, 1, 0);
                }
                sink?.Brush(x, z + 1f, 1.5f, -0.05f);
                js?.Brush(x, z + 1f, 1.5, -0.05);
                sink?.Brush(x + 1f, z - 1f, 1f, 0.03f);
                js?.Brush(x + 1f, z - 1f, 1, 0.03);
                sink?.Relax(x, z, 2f, 2, 0.36f);
                js?.Relax(x, z, 2, 2, 0.36);
            }
        }

        [Test]
        public void CornersSmoke()
        {
            var layout = WorldGridLayout.Millbrook;
            using var grid = WorldGrid.FromFunctions(layout, (x, z) => Ground, (x, z) => Dirt);
            using var d = new DeformSystem(grid);
            Assert.AreEqual(128, d.Tiles.TilesPerSide, "16 m tiles per side");
            var js = new JsDeformReference(layout, grid.H0.ToArray(), grid.Base.ToArray(), grid.Surface.ToArray(), grid.Wear.ToArray());

            // the integer kernels (Burst) and the JS mirror on the same calls
            Calls(d, js);
            int vn = layout.VN, n = layout.N;
            double maxErr = 0;
            int changed = 0, outside = 0, edge = 0;
            var fails = new List<string>();
            for (int k = 0; k < layout.Count; k++)
            {
                float h = grid.H[k];
                double err = Math.Abs((double)h - js.H[k]);
                if (err > maxErr) maxErr = err;
                if (grid.DeltaUm[k] == 0 && js.H[k] == Ground) continue;
                changed++;
                int i = k % vn, j = k / vn;
                if (i == 0 || j == 0 || i == n || j == n) edge++;
                // every change lies within 6 m of a corner spot
                float x = layout.X(i), z = layout.X(j);
                bool near = false;
                foreach (var p in Spots) if (Math.Abs(x - p[0]) < 6 && Math.Abs(z - p[1]) < 6) near = true;
                if (!near) { outside++; if (fails.Count < 5) fails.Add($"change at vertex ({i}, {j}) = ({x}, {z}), far from the corners"); }
                // H = H0 + (float)(DeltaUm * 1e-6), rounded to float32 (C# may keep the sum in double unless it is cast)
                float wantH = grid.DeltaUm[k] == 0 ? grid.H0[k] : (float)(grid.H0[k] + (float)(grid.DeltaUm[k] * 1e-6));
                if (BitConverter.SingleToInt32Bits(grid.H[k]) != BitConverter.SingleToInt32Bits(wantH) && fails.Count < 10)
                    fails.Add($"H at ({i}, {j}) is {grid.H[k]:R}, not H0 + DeltaUm = {wantH:R}");
            }
            int rects = 0, cornerRects = 0;
            while (d.Dirty.TryDequeue(out var r))
            {
                rects++;
                if ((r.z < 32 && r.w < 32) || (r.x > n - 32 && r.y > n - 32)) cornerRects++;
            }
            uint vLo = d.Tiles.Version(0, 0), vHi = d.Tiles.Version(127, 127), vMid = d.Tiles.Version(64, 64);
            TestContext.WriteLine($"CornersSmoke: {d.StampsApplied} stamps, {changed} vertices changed (edge {edge}, far {outside}), max |H - JS| {maxErr * 1000:F3} mm; " +
                                  $"{rects} dirty rectangles ({cornerRects} at the corners); tile versions (0,0) {vLo}, (127,127) {vHi}, (64,64) {vMid}; Burst {d.LastRanBurst}");
            Assert.IsTrue(d.LastRanBurst, "the Burst path ran managed code");
            Assert.Greater(changed, 100, "the corner stamps changed little");
            Assert.IsEmpty(fails, string.Join("\n", fails));
            Assert.LessOrEqual(maxErr, 0.5e-3, "heights differ from the JS mirror by more than 0.5 mm");
            Assert.AreEqual(rects, cornerRects, "a dirty rectangle away from the corners");
            Assert.Greater(vLo, 0u, "tile (0, 0) untouched");
            Assert.Greater(vHi, 0u, "tile (127, 127) untouched");
            Assert.AreEqual(0u, vMid, "tile (64, 64) touched");

            // the managed kernels give the same integers on the same stamps (fresh ground: undo everything first)
            var burstDelta = grid.DeltaUm.ToArray();
            ulong hLo = d.TileHash(0, 0), hHi = d.TileHash(127, 127);
            Reset(grid);
            d.UseBurst = false;
            Calls(d, null);
            int diff = 0;
            for (int k = 0; k < layout.Count; k++) if (grid.DeltaUm[k] != burstDelta[k]) diff++;
            Assert.AreEqual(0, diff, "DeltaUm differs between the Burst and the managed kernels");
            Assert.AreEqual(hLo, d.TileHash(0, 0), "tile (0, 0) hash");
            Assert.AreEqual(hHi, d.TileHash(127, 127), "tile (127, 127) hash");
        }

        static unsafe void Reset(WorldGrid g)
        {
            UnsafeUtility.MemClear(g.DeltaUm.GetUnsafePtr(), (long)g.DeltaUm.Length * 4);
            UnsafeUtility.MemClear(g.Wear.GetUnsafePtr(), g.Wear.Length);
            g.H.CopyFrom(g.H0);
            for (int k = 0; k < g.Surface.Length; k++) g.Surface[k] = Dirt;
        }
    }
}
