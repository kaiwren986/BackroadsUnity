using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// Delaunay triangulation of a point set (Bowyer-Watson with a walking point location) and the Voronoi cells of its
    /// points. Deterministic: points are inserted in a fixed spatial order; coordinates are taken relative to the set's
    /// centre. Callers jitter or deduplicate points closer than ~1 mm (duplicates are skipped).
    /// </summary>
    public sealed class Delaunay2
    {
        readonly List<double2> m_P = new List<double2>();   // relative coordinates; the last 3 are the super triangle
        readonly double2 m_Origin;
        // Triangles: vertices v[3t..3t+2] CCW; neighbours n[3t + i] = the triangle across the edge opposite vertex i.
        readonly List<int> m_V = new List<int>(), m_N = new List<int>();
        readonly List<bool> m_Dead = new List<bool>();
        readonly List<double2> m_Cc = new List<double2>();
        readonly List<double> m_R2 = new List<double>();
        readonly int m_Count;
        int m_Last;
        public readonly bool[] skipped;
        /// <summary>A triangle incident to each input point (or -1 when skipped).</summary>
        int[] m_Inc;

        public int Count => m_Count;
        public double2 Point(int i) => m_P[i] + m_Origin;

        public Delaunay2(IReadOnlyList<double2> pts)
        {
            m_Count = pts.Count;
            skipped = new bool[m_Count];
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pts) { minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y); }
            m_Origin = new double2(Math.Round((minX + maxX) * 0.5), Math.Round((minY + maxY) * 0.5));
            foreach (var p in pts) m_P.Add(p - m_Origin);
            double span = Math.Max(Math.Max(maxX - minX, maxY - minY), 1.0);
            double big = span * 64;
            int s0 = m_P.Count;
            m_P.Add(new double2(-big, -big));
            m_P.Add(new double2(big, -big));
            m_P.Add(new double2(0, big));
            AddTri(s0, s0 + 1, s0 + 2, -1, -1, -1);
            m_Inc = new int[m_Count];
            for (int i = 0; i < m_Count; i++) m_Inc[i] = -1;
            // Spatial order: cells of a coarse grid in a serpentine, then by index (deterministic and local).
            var order = new int[m_Count];
            for (int i = 0; i < m_Count; i++) order[i] = i;
            double cell = Math.Max(span / Math.Max(1, Math.Sqrt(m_Count / 4.0)), 1e-3);
            var key = new long[m_Count];
            for (int i = 0; i < m_Count; i++)
            {
                long gx = (long)Math.Floor((pts[i].x - minX) / cell), gy = (long)Math.Floor((pts[i].y - minY) / cell);
                long sx = (gy & 1) == 0 ? gx : 1_000_000 - gx;
                key[i] = (gy * 2_000_000 + sx) * 4_000_000 + i;
            }
            Array.Sort(key, order);
            foreach (var i in order) Insert(i);
        }

        // ------------------------------------------------------------------------------------------ predicates
        static double Orient(double2 a, double2 b, double2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

        int AddTri(int a, int b, int c, int na, int nb, int nc)
        {
            int t = m_Dead.Count;
            m_V.Add(a); m_V.Add(b); m_V.Add(c);
            m_N.Add(na); m_N.Add(nb); m_N.Add(nc);
            m_Dead.Add(false);
            Circum(m_P[a], m_P[b], m_P[c], out var cc, out var r2);
            m_Cc.Add(cc); m_R2.Add(r2);
            return t;
        }

        static void Circum(double2 a, double2 b, double2 c, out double2 cc, out double r2)
        {
            double bx = b.x - a.x, by = b.y - a.y, cx = c.x - a.x, cy = c.y - a.y;
            double d = 2 * (bx * cy - by * cx);
            if (Math.Abs(d) < 1e-300) { cc = (a + b + c) / 3; r2 = double.MaxValue; return; }
            double b2 = bx * bx + by * by, c2 = cx * cx + cy * cy;
            double ux = (cy * b2 - by * c2) / d, uy = (bx * c2 - cx * b2) / d;
            cc = new double2(a.x + ux, a.y + uy);
            r2 = ux * ux + uy * uy;
        }

        bool InCircle(int t, double2 p)
        {
            // Exact enough for jittered input: the classic determinant relative to p.
            var a = m_P[m_V[3 * t]] - p;
            var b = m_P[m_V[3 * t + 1]] - p;
            var c = m_P[m_V[3 * t + 2]] - p;
            double det = (a.x * a.x + a.y * a.y) * (b.x * c.y - c.x * b.y)
                       - (b.x * b.x + b.y * b.y) * (a.x * c.y - c.x * a.y)
                       + (c.x * c.x + c.y * c.y) * (a.x * b.y - b.x * a.y);
            return det > 0;
        }

        int Locate(double2 p)
        {
            int t = m_Last;
            if (t < 0 || m_Dead[t]) { t = m_Dead.Count - 1; while (m_Dead[t]) t--; }
            for (int guard = 0; guard < 1_000_000; guard++)
            {
                bool moved = false;
                for (int i = 0; i < 3; i++)
                {
                    int a = m_V[3 * t + (i + 1) % 3], b = m_V[3 * t + (i + 2) % 3];
                    if (Orient(m_P[a], m_P[b], p) < 0)
                    {
                        int nb = m_N[3 * t + i];
                        if (nb < 0) return t;
                        t = nb; moved = true; break;
                    }
                }
                if (!moved) return t;
            }
            throw new InvalidOperationException("Delaunay2: point location did not converge");
        }

        readonly List<int> m_Cavity = new List<int>();
        readonly List<(int a, int b, int outer)> m_Rim = new List<(int, int, int)>();
        readonly Stack<int> m_Stack = new Stack<int>();
        readonly HashSet<int> m_InCavity = new HashSet<int>();

        void Insert(int pi)
        {
            var p = m_P[pi];
            int t0 = Locate(p);
            // Skip duplicates (within 1 mm of a vertex of the containing triangle).
            for (int i = 0; i < 3; i++)
                if (math.distancesq(m_P[m_V[3 * t0 + i]], p) < 1e-6) { skipped[pi] = true; return; }
            m_Cavity.Clear(); m_Rim.Clear(); m_InCavity.Clear();
            m_Stack.Push(t0);
            m_InCavity.Add(t0);
            while (m_Stack.Count > 0)
            {
                int t = m_Stack.Pop();
                m_Cavity.Add(t);
                for (int i = 0; i < 3; i++)
                {
                    int nb = m_N[3 * t + i];
                    int a = m_V[3 * t + (i + 1) % 3], b = m_V[3 * t + (i + 2) % 3];
                    if (nb >= 0 && !m_InCavity.Contains(nb) && (InCircle(nb, p) || Orient(m_P[a], m_P[b], p) <= 0))
                    {
                        // Grow across every edge whose far triangle's circle holds p, and across any edge p does not see
                        // from the inside (keeps the cavity star-shaped when the predicates round).
                        m_InCavity.Add(nb);
                        m_Stack.Push(nb);
                    }
                    else if (nb < 0 || !m_InCavity.Contains(nb)) m_Rim.Add((a, b, nb));
                }
            }
            // Rim edges seen from a cavity triangle whose neighbour later joined the cavity are interior: drop them.
            var rim = new List<(int a, int b, int outer)>(m_Rim.Count);
            foreach (var e in m_Rim) if (e.outer < 0 || !m_InCavity.Contains(e.outer)) rim.Add(e);
            foreach (var t in m_Cavity) m_Dead[t] = true;
            // New triangles (p, a, b) for each rim edge; link them through their shared vertices.
            var byStart = new Dictionary<int, int>(rim.Count);
            var byEnd = new Dictionary<int, int>(rim.Count);
            var made = new List<int>(rim.Count);
            foreach (var (a, b, outer) in rim)
            {
                int t = AddTri(pi, a, b, outer, -1, -1);
                made.Add(t);
                if (outer >= 0)
                    for (int i = 0; i < 3; i++)
                    {
                        int x = m_V[3 * outer + (i + 1) % 3], y = m_V[3 * outer + (i + 2) % 3];
                        if (x == b && y == a) { m_N[3 * outer + i] = t; break; }
                    }
                byStart[a] = t;
                byEnd[b] = t;
            }
            // In triangle (p, a, b): the edge opposite a is (b, p) -> the triangle starting at b; opposite b is (p, a) -> the one ending at a.
            foreach (var t in made)
            {
                int a = m_V[3 * t + 1], b = m_V[3 * t + 2];
                m_N[3 * t + 1] = byStart.TryGetValue(b, out var tb) ? tb : -1;
                m_N[3 * t + 2] = byEnd.TryGetValue(a, out var ta) ? ta : -1;
            }
            m_Last = made.Count > 0 ? made[0] : t0;
        }

        // ------------------------------------------------------------------------------------------ output

        /// <summary>Live triangles with only real vertices, as vertex index triples (CCW).</summary>
        public List<int> Triangles()
        {
            var r = new List<int>();
            for (int t = 0; t < m_Dead.Count; t++)
            {
                if (m_Dead[t]) continue;
                int a = m_V[3 * t], b = m_V[3 * t + 1], c = m_V[3 * t + 2];
                if (a >= m_Count || b >= m_Count || c >= m_Count) continue;
                r.Add(a); r.Add(b); r.Add(c);
            }
            return r;
        }

        /// <summary>
        /// The Voronoi cell of every point as a CCW polygon (circumcentres of its triangles in order), in absolute coordinates;
        /// cells of hull points run out to the super triangle's circumcentres (far away, finite). Null for skipped points.
        /// </summary>
        public double2[][] Cells()
        {
            var cells = new double2[m_Count][];
            var inc = new int[m_Count];
            for (int i = 0; i < m_Count; i++) inc[i] = -1;
            for (int t = 0; t < m_Dead.Count; t++)
            {
                if (m_Dead[t]) continue;
                for (int k = 0; k < 3; k++) { int v = m_V[3 * t + k]; if (v < m_Count && inc[v] < 0) inc[v] = t; }
            }
            var ring = new List<double2>();
            for (int v = 0; v < m_Count; v++)
            {
                if (inc[v] < 0) continue;
                ring.Clear();
                int start = inc[v], t = start, guard = 0;
                do
                {
                    ring.Add(m_Cc[t] + m_Origin);
                    int k = m_V[3 * t] == v ? 0 : m_V[3 * t + 1] == v ? 1 : 2;
                    // Turn counter-clockwise around v: cross the edge (v, v[k+2]), the one opposite v[k+1].
                    int across = (k + 1) % 3;
                    t = m_N[3 * t + across];
                    if (++guard > 10000) break;
                } while (t >= 0 && t != start);
                cells[v] = ring.ToArray();
            }
            return cells;
        }
    }
}
