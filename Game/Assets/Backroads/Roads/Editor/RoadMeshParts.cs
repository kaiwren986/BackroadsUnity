using System;
using System.Collections.Generic;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>The road materials, in submesh order (05 §5.9).</summary>
    public enum RoadMaterial : byte { Asphalt = 0, Concrete = 1, Paint = 2 }

    /// <summary>What a marking is (for the checks and for LOD1's subset).</summary>
    public enum MarkingKind : byte { CentreLine = 0, EdgeLine = 1, StopLine = 2, Crosswalk = 3, YieldLine = 4, Warning = 5 }

    /// <summary>
    /// One primitive of the road meshes (roads-R3): a ribbon piece of at most 32 m, a whole junction, a kerb-face piece, a
    /// walk piece, a marking strip. It goes whole into the 64 m chunk holding its centroid (05 §5.13: triangles are not
    /// split at chunk borders). Vertices in world space (double), triangles in Unity's winding (front = clockwise from
    /// the viewer, normal = cross(b - a, c - a)).
    /// </summary>
    public sealed class RoadMeshPart
    {
        public RoadMaterial material;
        /// <summary>Bit 0: in LOD0, bit 1: in LOD1.</summary>
        public byte lods = 3;
        public bool collider;
        /// <summary>Normals are averaged over vertices at the same position within the same smoothing group (0 = flat, keep as given).</summary>
        public int smooth;
        public MarkingKind marking;
        /// <summary>A dash of a broken line (phase-locked to s = 0 of its edge).</summary>
        public bool dashed;
        public int node = -1, arm = -1, edge = -1;
        public readonly List<D3> pos = new List<D3>();
        public readonly List<Vector3> nrm = new List<Vector3>();
        public readonly List<Vector2> uv0 = new List<Vector2>(), uv1 = new List<Vector2>();
        public readonly List<Color32> col = new List<Color32>();
        public readonly List<int> tri = new List<int>();

        public int Add(D3 p, Vector2 a, Vector2 b, Color32 c, Vector3 n = default)
        {
            pos.Add(p); uv0.Add(a); uv1.Add(b); col.Add(c); nrm.Add(n);
            return pos.Count - 1;
        }

        /// <summary>Adds triangle (i, j, k) wound so its front faces along `front`.</summary>
        public void Tri(int i, int j, int k, Vector3 front)
        {
            D3 a = pos[i], b = pos[j], c = pos[k];
            double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            if (nx * front.x + ny * front.y + nz * front.z < 0) { tri.Add(i); tri.Add(k); tri.Add(j); }
            else { tri.Add(i); tri.Add(j); tri.Add(k); }
        }

        public D2 Centroid
        {
            get
            {
                double x = 0, z = 0;
                foreach (var p in pos) { x += p.x; z += p.z; }
                return pos.Count > 0 ? new D2(x / pos.Count, z / pos.Count) : default;
            }
        }

        public int Triangles => tri.Count / 3;
    }

    /// <summary>Heights of a triangle soup in plan (the asphalt, the walks), for draping markings onto it.</summary>
    public sealed class SurfaceIndex
    {
        readonly List<(D3 a, D3 b, D3 c)> m_T = new List<(D3, D3, D3)>();
        readonly Dictionary<(int, int), List<int>> m_Cells = new Dictionary<(int, int), List<int>>();
        const double Cell = 4.0;
        static int C(double v) => (int)Math.Floor(v / Cell);

        public void Add(RoadMeshPart p)
        {
            for (int k = 0; k + 2 < p.tri.Count; k += 3) Add(p.pos[p.tri[k]], p.pos[p.tri[k + 1]], p.pos[p.tri[k + 2]]);
        }

        public void Add(D3 a, D3 b, D3 c)
        {
            int id = m_T.Count;
            m_T.Add((a, b, c));
            double x0 = Math.Min(a.x, Math.Min(b.x, c.x)), x1 = Math.Max(a.x, Math.Max(b.x, c.x)), z0 = Math.Min(a.z, Math.Min(b.z, c.z)), z1 = Math.Max(a.z, Math.Max(b.z, c.z));
            for (int x = C(x0); x <= C(x1); x++)
                for (int z = C(z0); z <= C(z1); z++)
                {
                    if (!m_Cells.TryGetValue((x, z), out var l)) m_Cells[(x, z)] = l = new List<int>();
                    l.Add(id);
                }
        }

        /// <summary>The height of the highest triangle over p (draped paint must clear every surface it lies on).</summary>
        public bool Height(D2 p, out double y)
        {
            y = double.NegativeInfinity;
            if (!m_Cells.TryGetValue((C(p.x), C(p.z)), out var l)) return false;
            bool hit = false;
            foreach (int i in l)
            {
                var (a, b, c) = m_T[i];
                D2 v0 = b.XZ - a.XZ, v1 = c.XZ - a.XZ, v2 = p - a.XZ;
                double den = v0.Cross(v1);
                if (Math.Abs(den) < 1e-14) continue;
                double u = v2.Cross(v1) / den, v = v0.Cross(v2) / den;
                if (u < -1e-6 || v < -1e-6 || u + v > 1 + 1e-6) continue;
                double h = a.y + u * (b.y - a.y) + v * (c.y - a.y);
                if (h > y) y = h;
                hit = true;
            }
            return hit;
        }
    }
}
