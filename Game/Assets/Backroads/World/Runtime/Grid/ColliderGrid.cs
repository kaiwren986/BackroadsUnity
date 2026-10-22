using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace Backroads.World
{
    /// <summary>
    /// The JS static collider grid (props.js lines 12-55, 03 section 4.7): 16 m cells over the world's grid (64 x 64 on
    /// the canon test world, 128 x 128 on the new Millbrook; T3.1, from the WorldGridLayout), circles and rotated boxes
    /// with a height span. Records stay in JS space; Query flips Unity positions at the boundary. Used by the V0 vehicle
    /// statics (M2) and traffic clearance. Built once from PlacementSet.colliders; flat NativeArrays so Burst jobs can
    /// read it (cell c = j * CellsPerSide + i covers JS x in [i * 16 - Half, +16]).
    /// </summary>
    public sealed class ColliderGrid : IDisposable
    {
        public const float CellSize = 16f;

        /// <summary>16 m cells per side (layout.ColliderCellsPerSide) and the world's half size in metres.</summary>
        public int CellsPerSide { get; private set; }
        public float Half { get; private set; }

        public struct Record
        {
            public byte type;      // 0 circle, 1 box
            public byte kind;      // index into Kinds
            public float x, z, r;  // r: circle radius, or the box's bounding radius hypot(hw, hd)
            public float hw, hd, rot, cos, sin, y0, y1;
            public int tree;       // VegetationSet tree index for tree circles, else -1
        }

        public static readonly string[] Kinds = { "tree", "rock", "lamp", "post", "pump", "boat", "building", "fence", "logs", "bale", "dock" };

        public NativeArray<Record> Records;
        /// <summary>CSR layout: the records of cell c are CellItems[CellStart[c] .. CellStart[c + 1]).</summary>
        public NativeArray<int> CellStart;
        public NativeArray<int> CellItems;

        int[] _stamp;
        int _stampValue;

        public int Count => Records.Length;

        public static int KindIndex(string kind)
        {
            int i = Array.IndexOf(Kinds, kind);
            return i < 0 ? 255 : i;
        }

        public static ColliderGrid Build(PlacementSet.Collider[] src, WorldGridLayout layout, Allocator allocator = Allocator.Persistent)
        {
            if (!layout.IsValid) throw new ArgumentException("invalid WorldGridLayout");
            var g = new ColliderGrid { CellsPerSide = layout.ColliderCellsPerSide, Half = layout.Half };
            int cells = g.CellsPerSide;
            int n = src?.Length ?? 0;
            g.Records = new NativeArray<Record>(n, allocator);
            var cellLists = new List<int>[cells * cells];
            for (int c = 0; c < cellLists.Length; c++) cellLists[c] = new List<int>();
            for (int i = 0; i < n; i++)
            {
                var s = src[i];
                bool box = s.type == PlacementSet.ColliderType.Box;
                var rec = new Record
                {
                    type = (byte)(box ? 1 : 0), kind = (byte)KindIndex(s.kind), x = s.x, z = s.z,
                    hw = s.hw, hd = s.hd, rot = s.rot, cos = (float)Math.Cos(s.rot), sin = (float)Math.Sin(s.rot),
                    y0 = s.y0, y1 = s.y1, tree = s.tree,
                };
                // insert() uses the circle radius, or Math.hypot(hw, hd) for boxes
                rec.r = box ? (float)Math.Sqrt((double)s.hw * s.hw + (double)s.hd * s.hd) : s.r;
                g.Records[i] = rec;
                g.CellRange(s.x, s.z, rec.r, out int i0, out int i1, out int j0, out int j1);
                for (int j = j0; j <= j1; j++)
                    for (int ii = i0; ii <= i1; ii++)
                        cellLists[j * cells + ii].Add(i);
            }
            g.CellStart = new NativeArray<int>(cells * cells + 1, allocator);
            int total = 0;
            for (int c = 0; c < cellLists.Length; c++) { g.CellStart[c] = total; total += cellLists[c].Count; }
            g.CellStart[cells * cells] = total;
            g.CellItems = new NativeArray<int>(total, allocator);
            int p = 0;
            foreach (var l in cellLists) foreach (var idx in l) g.CellItems[p++] = idx;
            g._stamp = new int[n];
            return g;
        }

        void CellRange(float x, float z, float r, out int i0, out int i1, out int j0, out int j1)
        {
            float half = Half;
            int last = CellsPerSide - 1;
            i0 = Math.Max(0, (int)Math.Floor((x - r + half) / CellSize));
            i1 = Math.Min(last, (int)Math.Floor((x + r + half) / CellSize));
            j0 = Math.Max(0, (int)Math.Floor((z - r + half) / CellSize));
            j1 = Math.Min(last, (int)Math.Floor((z + r + half) / CellSize));
        }

        /// <summary>queryColliders(x, z, r): every record whose cells overlap the query square, deduplicated (JS space).</summary>
        public void QueryJs(float x, float z, float r, List<int> results)
        {
            results.Clear();
            if (++_stampValue == int.MaxValue) { Array.Clear(_stamp, 0, _stamp.Length); _stampValue = 1; }
            CellRange(x, z, r, out int i0, out int i1, out int j0, out int j1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int c = j * CellsPerSide + i;
                    for (int q = CellStart[c]; q < CellStart[c + 1]; q++)
                    {
                        int idx = CellItems[q];
                        if (_stamp[idx] == _stampValue) continue;
                        _stamp[idx] = _stampValue;
                        results.Add(idx);
                    }
                }
        }

        /// <summary>Same query from a Unity-space position.</summary>
        public void Query(float3 unityPos, float r, List<int> results) => QueryJs(-unityPos.x, unityPos.z, r, results);

        /// <summary>Unity-space centre and yaw (degrees) of a record, for gizmos and PhysX proxies.</summary>
        public static float3 UnityCentre(in Record c) => new float3(-c.x, (c.y0 + c.y1) * 0.5f, c.z);
        public static float UnityYawDeg(in Record c) => -c.rot * 57.29578f;

        public void Dispose()
        {
            if (Records.IsCreated) Records.Dispose();
            if (CellStart.IsCreated) CellStart.Dispose();
            if (CellItems.IsCreated) CellItems.Dispose();
        }
    }
}
