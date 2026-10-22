using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Backroads.Core.WorldBake;
using Clipper2Lib;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R2 steps 1-4 (05 §5.7): kerb loops by Clipper2 booleans. The footprint is every R1 ribbon corridor (pavement
    /// edge to pavement edge) and junction surface; the blocks are the play area minus the footprint; each block outline
    /// is a kerb loop (block on its left), so asphalt and kerb cannot disagree. Each loop segment is matched to the R1
    /// piece it lies on (a road side, a corner arc, a dead-end cap) and the loop splits into faces, one per road side
    /// between corners (corner arcs split at their middle). Faces get the kerb type and walk width (KerbRules: the kerb
    /// zone, sidewalk tags; downtown walks fill to the building line), heights (gutter, top of kerb, walk back edge), the
    /// corner ramps (depressed corners, perpendicular ramps on deep walks) and the forbidden intervals (corner returns,
    /// crosswalks, ramps, stop-line zones, inlets). Integer Clipper (0.1 mm) with collinear points kept: every loop
    /// vertex that is an R1 vertex keeps its exact double position and height. Deterministic.
    /// </summary>
    public static class KerbBuilder
    {
        public const int Version = 2;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>A piece of the footprint outline: a ribbon edge segment, a junction outline segment or a ribbon end.</summary>
        sealed class Prim
        {
            public D3 a, b;
            public KerbOwner owner;
            public int edge = -1, side = -1;
            /// <summary>Corner arcs: the node, corner position, segment k of n (k counted from the arm I tangent), and both sides.</summary>
            public int node = -1, corner = -1, arcK, arcN, edgeI, sideI, edgeJ, sideJ;
            /// <summary>Kerbs on arm I's and arm J's side: a corner between a kerbed street and an unkerbed road goes wholly to the street.</summary>
            public bool kerbI, kerbJ;
        }

        sealed class Seg
        {
            public D3 a, b;
            public KerbOwner owner;
            public int edge = -1, side = -1, node = -1, corner = -1;
            public Prim prim;
        }

        public static KerbNetwork Build(RoadGraph g, RoadGraphInput input, RoadSurfaces surf, double playHalf, KerbSettings st = null)
        {
            st = st ?? new KerbSettings();
            var kn = new KerbNetwork();
            var prims = new List<Prim>();
            var polys = new List<List<D3>>();
            var asphalt = new List<List<D3>>();
            Footprint(g, surf, st, polys, prims, asphalt);

            // the blocks: play area minus the footprint (NonZero over the clip paths = their union)
            double S = st.scale;
            var exact = new Dictionary<(long, long), D3>();
            var clip = new Paths64();
            foreach (var poly in polys)
            {
                var path = new Path64(poly.Count);
                foreach (var p in poly)
                {
                    var q = Q(p.x, p.z, S);
                    if (!exact.ContainsKey(q)) exact[q] = p;
                    path.Add(new Point64(q.Item1, q.Item2));
                }
                clip.Add(path);
            }
            long H = (long)Math.Round(playHalf * S);
            var rect = new Paths64 { new Path64 { new Point64(-H, -H), new Point64(H, -H), new Point64(H, H), new Point64(-H, H) } };
            var c = new Clipper64 { PreserveCollinear = true };
            c.AddSubject(rect);
            c.AddClip(clip);
            var solution = new Paths64();
            c.Execute(ClipType.Difference, FillRule.NonZero, solution);

            var grid = new PrimGrid(prims, 8.0);
            var rawLoops = new List<List<Seg>>();
            foreach (var path in solution)
            {
                double area = Clipper.Area(path) / (S * S);
                if (Math.Abs(area) < st.sliverArea) { kn.slivers++; continue; }
                var pts = new List<D3>(path.Count);
                foreach (var p in path)
                    pts.Add(exact.TryGetValue((p.X, p.Y), out var e) ? e : new D3(p.X / S, double.NaN, p.Y / S));
                var segs = Classify(pts, grid, playHalf, S);
                if (segs.Count >= 3) rawLoops.Add(segs);
            }
            // a stable loop order: by each loop's smallest vertex (x, then z)
            rawLoops.Sort((A, B) =>
            {
                var a = MinPoint(A);
                var b = MinPoint(B);
                int r = a.x.CompareTo(b.x);
                return r != 0 ? r : a.z.CompareTo(b.z);
            });

            var faces = new List<KerbFace>();
            foreach (var segs in rawLoops)
            {
                var loop = MakeLoop(segs, kn.loops.Count);
                kn.loops.Add(loop);
                Faces(g, loop, faces);
                for (int k = 0; k < loop.Count; k++)
                    if (loop.owner[k] == KerbOwner.Mouth || loop.owner[k] == KerbOwner.None)
                    {
                        var p = loop.pts[k];
                        kn.notes.Add(string.Format(Inv, "loop {0} segment {1} at ({2:F2}, {3:F2}) borders {4} (edge {5}): no ribbon or surface beyond it", loop.id, k, p.x, p.z, loop.owner[k], loop.edge[k]));
                    }
            }
            // face ids: by edge, side, then position along the edge
            var frames = new RoadFrame[g.edges.Count];
            foreach (var e in g.edges) frames[e.id] = new RoadFrame(e.pts);
            var keyed = new List<(KerbFace f, double s)>();
            foreach (var f in faces) keyed.Add((f, frames[f.edge].Project(f.kerb[f.Count / 2], 0, out _)));
            keyed.Sort((A, B) =>
            {
                int r = A.f.edge.CompareTo(B.f.edge);
                if (r != 0) return r;
                r = A.f.side.CompareTo(B.f.side);
                if (r != 0) return r;
                r = A.s.CompareTo(B.s);
                return r != 0 ? r : A.f.t0.CompareTo(B.f.t0);
            });
            for (int i = 0; i < keyed.Count; i++) { keyed[i].f.id = i; kn.faces.Add(keyed[i].f); }
            foreach (var l in kn.loops) l.faces.Clear();
            foreach (var f in kn.faces) kn.loops[f.loop].faces.Add(f.id);
            foreach (var l in kn.loops) l.faces.Sort((a, b) => kn.faces[a].t0.CompareTo(kn.faces[b].t0));

            var buildings = new BuildingIndex(input, 16.0);
            foreach (var f in kn.faces) Widths(g, f, buildings, st, kn);
            Corners(g, surf, kn, st);
            Tapers(kn);
            foreach (var f in kn.faces) Heights(kn, f, st);
            Forbidden(g, surf, kn, st, frames);
            Unfaced(g, kn);
            KerbPieceBuilder.Build(kn, st, asphalt);
            return kn;
        }

        static (long, long) Q(double x, double z, double s) => ((long)Math.Round(x * s), (long)Math.Round(z * s));

        static D2 MinPoint(List<Seg> segs)
        {
            var m = segs[0].a.XZ;
            foreach (var s in segs) if (s.a.x < m.x || (s.a.x == m.x && s.a.z < m.z)) m = s.a.XZ;
            return m;
        }

        // ------------------------------------------------------------------ the footprint and its pieces

        static void Footprint(RoadGraph g, RoadSurfaces surf, KerbSettings st, List<List<D3>> polys, List<Prim> prims, List<List<D3>> small)
        {
            var corridors = new List<(int edge, List<D3[]> stations)>();
            var withRibbon = new HashSet<int>();
            foreach (var r in surf.ribbons) { corridors.Add((r.edge, r.stations)); withRibbon.Add(r.edge); }
            // two junctions whose mouths (almost) touch have no ribbon between them: close the gap from mouth to mouth
            foreach (var e in g.edges)
            {
                if (withRibbon.Contains(e.id)) continue;
                ArmSurface A = null, B = null;
                var na = surf.Of(e.nodeA);
                var nb = surf.Of(e.nodeB);
                if (na != null) foreach (var a in na.arms) if (a.edge == e.id && a.atA && a.mouth != null) A = a;
                if (nb != null) foreach (var a in nb.arms) if (a.edge == e.id && !a.atA && a.mouth != null) B = a;
                if (A == null || B == null || A.mouth.Length != B.mouth.Length) continue;
                var endB = new D3[B.mouth.Length];
                for (int k = 0; k < endB.Length; k++) endB[k] = B.mouth[endB.Length - 1 - k];
                corridors.Add((e.id, new List<D3[]> { A.mouth, endB }));
            }
            foreach (var (edge, stations) in corridors)
            {
                var r = new { edge, stations, profile = stations[0] };
                int n = r.stations.Count, P = r.profile.Length;
                if (n < 2) continue;
                // right edge forward, the far end row, left edge back, the near end row: counter-clockwise, and every
                // end-row vertex is a junction mouth vertex, so the union closes without slivers
                var poly = new List<D3>(2 * n + 2 * P);
                for (int q = 0; q < n; q++) poly.Add(r.stations[q][0]);
                for (int k = 1; k < P - 1; k++) poly.Add(r.stations[n - 1][k]);
                for (int q = n - 1; q >= 0; q--) poly.Add(r.stations[q][P - 1]);
                for (int k = P - 2; k >= 1; k--) poly.Add(r.stations[0][k]);
                if (Geom2.SignedArea(ToD2(poly)) <= 0) continue;
                polys.Add(poly);
                for (int q = 0; q + 1 < n; q++) small.Add(new List<D3> { r.stations[q][0], r.stations[q + 1][0], r.stations[q + 1][P - 1], r.stations[q][P - 1] });
                for (int q = 0; q + 1 < n; q++)
                {
                    prims.Add(new Prim { a = r.stations[q][0], b = r.stations[q + 1][0], owner = KerbOwner.Side, edge = r.edge, side = 1 });
                    prims.Add(new Prim { a = r.stations[q][P - 1], b = r.stations[q + 1][P - 1], owner = KerbOwner.Side, edge = r.edge, side = 0 });
                }
                for (int k = 0; k + 1 < P; k++)
                {
                    prims.Add(new Prim { a = r.stations[0][k + 1], b = r.stations[0][k], owner = KerbOwner.Cap, edge = r.edge });
                    prims.Add(new Prim { a = r.stations[n - 1][k], b = r.stations[n - 1][k + 1], owner = KerbOwner.Cap, edge = r.edge });
                }
            }
            foreach (var ns in surf.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback || ns.polygon.Count < 3) continue;
                int m = ns.polygon.Count;
                int off = ns.ringMesh ? 1 + 2 * m : 0;
                if (ns.vertices.Count < off + m) continue;
                var poly = new List<D3>(m);
                for (int k = 0; k < m; k++) poly.Add(ns.vertices[off + k]);
                polys.Add(poly);
                small.Add(poly);
                for (int k = 0; k < m; k++) prims.Add(JunctionPrim(g, ns, poly[k], poly[(k + 1) % m]));
            }
        }

        static List<D2> ToD2(List<D3> p)
        {
            var o = new List<D2>(p.Count);
            foreach (var q in p) o.Add(q.XZ);
            return o;
        }

        /// <summary>Which piece of the junction outline a segment is: an arm's kerb line (extended before the node for miters), a corner arc, a mouth line.</summary>
        static Prim JunctionPrim(RoadGraph g, NodeSurface ns, D3 a, D3 b)
        {
            var q = new D2(0.5 * (a.x + b.x), 0.5 * (a.z + b.z));
            var p = new Prim { a = a, b = b, node = ns.node };
            double best = double.MaxValue;
            foreach (var X in ns.arms)
            {
                double dl = DistToOffset(X.frame, X.hwL, q, X.sMouth + 0.01);
                if (dl < best) { best = dl; p.owner = KerbOwner.Side; p.edge = X.edge; p.side = X.atA ? 0 : 1; p.corner = -1; }
                double dr = DistToOffset(X.frame, -X.hwR, q, X.sMouth + 0.01);
                if (dr < best) { best = dr; p.owner = KerbOwner.Side; p.edge = X.edge; p.side = X.atA ? 1 : 0; p.corner = -1; }
            }
            for (int ci = 0; ci < ns.corners.Count; ci++)
            {
                var c = ns.corners[ci];
                for (int k = 0; k + 1 < c.arc.Count; k++)
                {
                    double d = Polyline.SegmentDistance(c.arc[k], c.arc[k + 1], q);
                    if (d < best - 1e-9)
                    {
                        best = d;
                        var I = ns.arms[c.armI];
                        var J = ns.arms[c.armJ];
                        p.owner = KerbOwner.Side;
                        p.corner = ci; p.arcK = k; p.arcN = c.arc.Count - 1;
                        p.edgeI = I.edge; p.sideI = I.atA ? 0 : 1;
                        p.edgeJ = J.edge; p.sideJ = J.atA ? 1 : 0;
                        p.kerbI = KerbRules.Kerbed(g.RoadOf(g.edges[I.edge].sourceRoad), g.edges[I.edge].cls);
                        p.kerbJ = KerbRules.Kerbed(g.RoadOf(g.edges[J.edge].sourceRoad), g.edges[J.edge].cls);
                    }
                }
            }
            foreach (var X in ns.arms)
            {
                if (X.mouth == null || X.mouth.Length < 2) continue;
                double d = Polyline.SegmentDistance(X.mouth[0].XZ, X.mouth[X.mouth.Length - 1].XZ, q);
                // only a piece lying on the mouth line is a mouth; a connector R1 draws across the node (a through side whose
                // kerb lines do not meet) belongs to the nearest kerb line
                if (d < 1e-4 && d < best - 1e-9) { best = d; p.owner = KerbOwner.Mouth; p.edge = X.edge; p.side = -1; p.corner = -1; }
            }
            return p;
        }

        /// <summary>Distance from q to the offset line at u for s in [.., sMax], the first segment extended back past the node.</summary>
        static double DistToOffset(RoadFrame f, double u, D2 q, double sMax)
        {
            double best = double.MaxValue;
            for (int i = 0; i + 1 < f.Count && f.s[i] <= sMax; i++)
            {
                D2 A = f.c[i] + f.m[i] * u, B = f.c[i + 1] + f.m[i + 1] * u;
                D2 e = B - A;
                double l2 = e.Dot(e);
                double t = l2 > 0 ? (q - A).Dot(e) / l2 : 0;
                if (t > 1) t = 1;
                if (t < 0 && i > 0) t = 0;
                if (f.s[i] + t * (f.s[i + 1] - f.s[i]) > sMax) t = Math.Max(0, (sMax - f.s[i]) / Math.Max(1e-12, f.s[i + 1] - f.s[i]));
                best = Math.Min(best, D2.Distance(A + e * t, q));
            }
            return best;
        }

        // ------------------------------------------------------------------ loops

        sealed class PrimGrid
        {
            readonly List<Prim> m_Prims;
            readonly double m_Cell;
            readonly Dictionary<(int, int), List<int>> m_Cells = new Dictionary<(int, int), List<int>>();

            public PrimGrid(List<Prim> prims, double cell)
            {
                m_Prims = prims;
                m_Cell = cell;
                for (int i = 0; i < prims.Count; i++)
                {
                    var p = prims[i];
                    int x0 = C(Math.Min(p.a.x, p.b.x)), x1 = C(Math.Max(p.a.x, p.b.x)), z0 = C(Math.Min(p.a.z, p.b.z)), z1 = C(Math.Max(p.a.z, p.b.z));
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!m_Cells.TryGetValue((x, z), out var l)) m_Cells[(x, z)] = l = new List<int>();
                            l.Add(i);
                        }
                }
            }

            int C(double v) => (int)Math.Floor(v / m_Cell);

            public Prim Nearest(D2 q, out double distance)
            {
                distance = double.MaxValue;
                int best = -1;
                int cx = C(q.x), cz = C(q.z);
                for (int x = cx - 1; x <= cx + 1; x++)
                    for (int z = cz - 1; z <= cz + 1; z++)
                    {
                        if (!m_Cells.TryGetValue((x, z), out var l)) continue;
                        foreach (int i in l)
                        {
                            var p = m_Prims[i];
                            double d = Polyline.SegmentDistance(p.a.XZ, p.b.XZ, q);
                            if (d < distance - 1e-12 || (Math.Abs(d - distance) <= 1e-12 && i < best)) { distance = d; best = i; }
                        }
                    }
                return best >= 0 ? m_Prims[best] : null;
            }
        }

        /// <summary>Matches each loop segment to the footprint piece it lies on and fills in heights of new (intersection) points.</summary>
        static List<Seg> Classify(List<D3> pts, PrimGrid grid, double playHalf, double S)
        {
            int n = pts.Count;
            var segs = new List<Seg>(n + 4);
            double tol = 2.0 / S;
            for (int k = 0; k < n; k++)
            {
                D3 a = pts[k], b = pts[(k + 1) % n];
                bool onEdge = (Math.Abs(Math.Abs(a.x) - playHalf) < tol && Math.Abs(a.x - b.x) < tol) || (Math.Abs(Math.Abs(a.z) - playHalf) < tol && Math.Abs(a.z - b.z) < tol);
                if (onEdge) { segs.Add(new Seg { a = a, b = b, owner = KerbOwner.Edge }); continue; }
                var mid = new D2(0.5 * (a.x + b.x), 0.5 * (a.z + b.z));
                var p = grid.Nearest(mid, out _);
                if (p == null) { segs.Add(new Seg { a = a, b = b, owner = KerbOwner.None }); continue; }
                if (p.corner >= 0)
                {
                    // a corner arc: split at its middle between the two arms' faces
                    int half = p.arcN / 2;
                    bool odd = p.arcN % 2 == 1;
                    if (odd && p.arcK == half && p.kerbI == p.kerbJ)
                    {
                        var M = new D3(0.5 * (p.a.x + p.b.x), 0.5 * (p.a.y + p.b.y), 0.5 * (p.a.z + p.b.z));
                        double t = Param(a.XZ, b.XZ, M.XZ);
                        if (t > 1e-6 && t < 1 - 1e-6)
                        {
                            segs.Add(ArcSeg(a, M, p));
                            segs.Add(ArcSeg(M, b, p));
                            continue;
                        }
                    }
                    segs.Add(ArcSeg(a, b, p));
                    continue;
                }
                segs.Add(new Seg { a = a, b = b, owner = p.owner, edge = p.edge, side = p.side, prim = p });
            }
            // heights of points the boolean made (crossings of overlapping corridors): along the piece they lie on
            for (int k = 0; k < segs.Count; k++)
            {
                var s = segs[k];
                if (double.IsNaN(s.a.y)) s.a = Lift(s.a, s.prim ?? segs[(k + segs.Count - 1) % segs.Count].prim);
                if (double.IsNaN(s.b.y)) s.b = Lift(s.b, s.prim ?? segs[(k + 1) % segs.Count].prim);
            }
            for (int k = 0; k < segs.Count; k++)
            {
                // neighbours share their points exactly
                var nx = segs[(k + 1) % segs.Count];
                if (double.IsNaN(nx.a.y) || nx.a.x != segs[k].b.x || nx.a.z != segs[k].b.z) nx.a = segs[k].b;
            }
            return segs;
        }

        /// <summary>The arm whose face takes this arc piece: the nearer tangent's arm (k counted from arm I's tangent).</summary>
        static Seg ArcSeg(D3 a, D3 b, Prim p)
        {
            var mid = new D2(0.5 * (a.x + b.x), 0.5 * (a.z + b.z));
            double frac = (p.arcK + Param(p.a.XZ, p.b.XZ, mid)) / Math.Max(1, p.arcN);
            bool iSide = p.kerbI != p.kerbJ ? p.kerbI : frac < 0.5;
            return new Seg { a = a, b = b, owner = KerbOwner.Side, edge = iSide ? p.edgeI : p.edgeJ, side = iSide ? p.sideI : p.sideJ, node = p.node, corner = p.corner, prim = p };
        }

        static double Param(D2 a, D2 b, D2 q)
        {
            var e = b - a;
            double l2 = e.Dot(e);
            return l2 > 0 ? (q - a).Dot(e) / l2 : 0;
        }

        static D3 Lift(D3 q, Prim p)
        {
            if (p == null) return new D3(q.x, 0, q.z);
            double t = Math.Max(0, Math.Min(1, Param(p.a.XZ, p.b.XZ, q.XZ)));
            return new D3(q.x, p.a.y + (p.b.y - p.a.y) * t, q.z);
        }

        static bool SameFace(Seg a, Seg b) => a.owner == KerbOwner.Side && b.owner == KerbOwner.Side && a.edge == b.edge && a.side == b.side;

        static KerbLoop MakeLoop(List<Seg> segs, int id)
        {
            int n = segs.Count;
            // start at a change of what the kerb borders, so no face wraps the loop's start when it can be helped
            int start = 0;
            for (int k = 0; k < n; k++)
            {
                var prev = segs[(k + n - 1) % n];
                if (!SameFace(prev, segs[k]) || prev.owner != segs[k].owner) { start = k; break; }
            }
            var loop = new KerbLoop { id = id };
            double t = 0;
            for (int i = 0; i < n; i++)
            {
                var s = segs[(start + i) % n];
                loop.pts.Add(s.a);
                loop.t.Add(t);
                t += D2.Distance(s.a.XZ, s.b.XZ);
                loop.owner.Add(s.owner);
                loop.edge.Add(s.edge);
                loop.side.Add(s.side);
                loop.arcNode.Add(s.corner >= 0 ? s.node : -1);
                loop.arcCorner.Add(s.corner);
            }
            loop.Length = t;
            loop.area = Geom2.SignedArea(ToD2(loop.pts));
            loop.hole = loop.area < 0;
            return loop;
        }

        // ------------------------------------------------------------------ faces

        static void Faces(RoadGraph g, KerbLoop loop, List<KerbFace> faces)
        {
            int n = loop.Count;
            bool uniform = true;
            for (int k = 1; k < n && uniform; k++)
                if (loop.owner[k] != loop.owner[0] || loop.edge[k] != loop.edge[0] || loop.side[k] != loop.side[0]) uniform = false;
            int k0 = 0;
            while (k0 < n)
            {
                if (loop.owner[k0] != KerbOwner.Side) { k0++; continue; }
                int k1 = k0;
                while (k1 + 1 < n && loop.owner[k1 + 1] == KerbOwner.Side && loop.edge[k1 + 1] == loop.edge[k0] && loop.side[k1 + 1] == loop.side[k0]) k1++;
                var f = new KerbFace { edge = loop.edge[k0], side = loop.side[k0], loop = loop.id, t0 = loop.t[k0] };
                for (int k = k0; k <= k1 + 1; k++)
                {
                    int i = k % n;
                    var p = loop.pts[i];
                    f.kerb.Add(p.XZ);
                    f.gutter.Add(p.y);
                    f.normal.Add(Normal(loop, i));
                }
                f.t1 = f.t0 + (k1 + 1 < n ? loop.t[k1 + 1] : loop.Length) - loop.t[k0];
                double s = 0;
                f.s.Add(0);
                for (int k = 1; k < f.kerb.Count; k++) { s += D2.Distance(f.kerb[k - 1], f.kerb[k]); f.s.Add(s); }
                Meta(g, f);
                faces.Add(f);
                k0 = k1 + 1;
                if (uniform) break;
            }
        }

        /// <summary>The unit normal into the block at loop point i: the average of the neighbouring segments' left normals.</summary>
        static D2 Normal(KerbLoop loop, int i)
        {
            int n = loop.Count;
            D2 p = loop.pts[i].XZ, a = loop.pts[(i + n - 1) % n].XZ, b = loop.pts[(i + 1) % n].XZ;
            var n1 = (p - a).Normalized.Left;
            var n2 = (b - p).Normalized.Left;
            var m = (n1 + n2).Normalized;
            return m.Length > 0.5 ? m : n2;
        }

        static void Meta(RoadGraph g, KerbFace f)
        {
            var e = g.edges[f.edge];
            var road = g.RoadOf(e.sourceRoad);
            f.kerbed = KerbRules.Kerbed(road, e.cls);
            f.walk = KerbRules.Walk(road, e.cls, f.side);
            f.kind = f.kerbed ? KerbKind.Barrier150 : KerbKind.None;
            f.cls = (StreetClass)(byte)e.cls;
            f.speedKmh = road?.speedKmh ?? 0;
            f.oneway = road != null && road.oneway != 0;
            f.osmWay = road != null && road.osmWays != null && road.osmWays.Length > 0 ? road.osmWays[0] : "";
            f.bridge = road != null && (road.bridge || road.tunnel);
        }

        // ------------------------------------------------------------------ walk widths and building clearance (05 §8.4)

        sealed class BuildingIndex
        {
            public readonly List<D2[]> b = new List<D2[]>();
            readonly double m_Cell;
            readonly Dictionary<(int, int), List<int>> m_Cells = new Dictionary<(int, int), List<int>>();

            public BuildingIndex(RoadGraphInput input, double cell)
            {
                m_Cell = cell;
                if (input == null) return;
                foreach (var f in input.footprints)
                {
                    if (f.corners == null || f.corners.Length < 3) continue;
                    int id = b.Count;
                    b.Add(f.corners);
                    double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
                    foreach (var p in f.corners) { x0 = Math.Min(x0, p.x); x1 = Math.Max(x1, p.x); z0 = Math.Min(z0, p.z); z1 = Math.Max(z1, p.z); }
                    for (int x = C(x0); x <= C(x1); x++)
                        for (int z = C(z0); z <= C(z1); z++)
                        {
                            if (!m_Cells.TryGetValue((x, z), out var l)) m_Cells[(x, z)] = l = new List<int>();
                            l.Add(id);
                        }
                }
            }

            int C(double v) => (int)Math.Floor(v / m_Cell);

            public IEnumerable<int> Near(D2 p, double r)
            {
                var seen = new HashSet<int>();
                for (int x = C(p.x - r); x <= C(p.x + r); x++)
                    for (int z = C(p.z - r); z <= C(p.z + r); z++)
                        if (m_Cells.TryGetValue((x, z), out var l))
                            foreach (int i in l)
                                if (seen.Add(i)) yield return i;
            }

            /// <summary>Distance along the ray p + n t (t in [0, max]) to the first building wall, or +inf; 0 when p is inside one.</summary>
            public double Ray(D2 p, D2 n, double max)
            {
                double best = double.PositiveInfinity;
                var end = p + n * max;
                foreach (int i in Near(p + n * (0.5 * max), 0.5 * max + 0.1))
                {
                    var poly = b[i];
                    if (TownZone.InPolygon(poly, p)) return 0;
                    for (int k = 0, j = poly.Length - 1; k < poly.Length; j = k++)
                        if (Geom2.SegmentCross(p, end, poly[j], poly[k], out double ta, out _)) best = Math.Min(best, ta * max);
                }
                return best;
            }

            /// <summary>Distance from p to the nearest building (negative inside), +inf when none within r.</summary>
            public double Nearest(D2 p, double r)
            {
                double best = double.PositiveInfinity;
                foreach (int i in Near(p, r))
                {
                    var poly = b[i];
                    double d = Geom2.DistanceToBoundary(poly, p);
                    if (TownZone.InPolygon(poly, p)) d = -d;
                    if (d < best) best = d;
                }
                return best;
            }
        }

        static void Widths(RoadGraph g, KerbFace f, BuildingIndex bi, KerbSettings st, KerbNetwork kn)
        {
            var e = g.edges[f.edge];
            double rule = KerbRules.WalkWidth(e.cls, st);
            double worst = double.PositiveInfinity, worstS = 0, need = 0;
            bool anyDowntown = false;
            for (int i = 0; i < f.Count; i++)
            {
                var p = f.kerb[i];
                double w;
                if (!f.kerbed) w = 0;
                else if (!f.walk) w = st.kerbTop;
                else
                {
                    bool downtown = g.downtown != null && g.downtown.Length >= 3 && TownZone.InPolygon(g.downtown, p);
                    anyDowntown |= downtown;
                    if (downtown)
                    {
                        double d = bi.Ray(p, f.normal[i], st.kerbTop + st.downtownFill);
                        w = d <= st.kerbTop + st.downtownFill ? d : st.kerbTop + st.walkDowntown;
                    }
                    else
                    {
                        w = st.kerbTop + rule;
                        double d = bi.Ray(p, f.normal[i], w);
                        if (d < w) w = d;
                    }
                    double clear = bi.Nearest(p, st.kerbTop + st.downtownFill + 1);
                    double required = downtown ? st.downtownMinClear : rule;
                    if (clear < required && clear - required < worst - need) { worst = clear; worstS = f.s[i]; need = required; }
                    if (clear < f.minClearance || double.IsNaN(f.minClearance)) { if (!double.IsInfinity(clear)) { f.minClearance = clear; f.minClearanceS = f.s[i]; } }
                }
                f.width.Add(w);
            }
            if (!double.IsPositiveInfinity(worst))
                kn.clearance.Add(string.Format(Inv, "face {0} ({1} {2}, edge {3} {4}){5}: kerb {6:F2} m from a building at s {7:F1} (needs {8:F1} m)",
                    f.id, f.osmWay, f.cls, f.edge, f.side == 0 ? "left" : "right", anyDowntown ? " downtown" : "", worst, worstS, need));
        }

        // ------------------------------------------------------------------ corners and ramps (05 §5.7)

        static void Corners(RoadGraph g, RoadSurfaces surf, KerbNetwork kn, KerbSettings st)
        {
            foreach (var loop in kn.loops)
            {
                int n = loop.Count;
                int k = 0;
                // a corner arc may wrap the loop start: begin at a segment that is not on the same arc as its predecessor
                int first = 0;
                for (int q = 0; q < n; q++)
                {
                    int p = (q + n - 1) % n;
                    if (loop.arcCorner[q] < 0 || loop.arcNode[q] != loop.arcNode[p] || loop.arcCorner[q] != loop.arcCorner[p]) { first = q; break; }
                }
                while (k < n)
                {
                    int i = (first + k) % n;
                    if (loop.arcCorner[i] < 0) { k++; continue; }
                    int node = loop.arcNode[i], corner = loop.arcCorner[i];
                    int k1 = k;
                    while (k1 + 1 < n && loop.arcNode[(first + k1 + 1) % n] == node && loop.arcCorner[(first + k1 + 1) % n] == corner) k1++;
                    int iEnd = (first + k1) % n;
                    double t0 = loop.t[i];
                    double t1 = (iEnd + 1 < n ? loop.t[iEnd + 1] : loop.Length);
                    if (t1 < t0) t1 += loop.Length;
                    var ns = surf.Of(node);
                    var c = ns.corners[corner];
                    var kc = new KerbCorner { id = kn.corners.Count, node = node, corner = corner, loop = loop.id, kind = c.kind, radius = c.radius, t0 = t0, t1 = t1 };
                    // the faces before and after the arc (a corner between a kerbed street and an unkerbed road lies
                    // wholly on the street's face, so one of them may own none of it)
                    kc.faceA = FaceAt(kn, loop, t0 - 1e-6);
                    kc.faceB = FaceAt(kn, loop, t1 + 1e-6);
                    var o0 = kn.Face(FaceAt(kn, loop, t0 + 1e-6));
                    var o1 = kn.Face(FaceAt(kn, loop, t1 - 1e-6));
                    kc.kerbed = (o0 != null && o0.walk) || (o1 != null && o1.walk);
                    if (kc.kerbed) Ramp(g, kc, loop, kn, ns, c, st);
                    kn.corners.Add(kc);
                    k = k1 + 1;
                }
            }
            // every face knows the corners whose arc or ramps reach it, and the ones at its two ends
            foreach (var f in kn.faces)
            {
                var loop = kn.loops[f.loop];
                foreach (var c in kn.corners)
                {
                    if (c.loop != f.loop) continue;
                    double a = Math.Min(c.t0, c.ramp == RampKind.None ? c.t0 : c.dep0) - st.rampRun - 0.5, b = Math.Max(c.t1, c.ramp == RampKind.None ? c.t1 : c.dep1) + st.rampRun + 0.5;
                    if (Overlaps(a, b, f.t0, f.t1, loop.Length)) f.corners.Add(c.id);
                    if (f.cornerStart < 0 && InRange(f.t0, c.t0 - 1e-6, c.t1 + 1e-6, loop.Length)) f.cornerStart = c.id;
                    if (f.cornerEnd < 0 && InRange(f.t1, c.t0 - 1e-6, c.t1 + 1e-6, loop.Length)) f.cornerEnd = c.id;
                }
            }
        }

        static bool Overlaps(double a, double b, double f0, double f1, double L)
        {
            for (int w = -1; w <= 1; w++)
                if (a + w * L < f1 && b + w * L > f0) return true;
            return false;
        }

        /// <summary>The face of a loop covering loop arc length t (wrapping), or -1.</summary>
        static int FaceAt(KerbNetwork kn, KerbLoop loop, double t)
        {
            foreach (int id in loop.faces)
            {
                var f = kn.faces[id];
                if (InRange(t, f.t0, f.t1, loop.Length)) return id;
            }
            return -1;
        }

        static bool InRange(double t, double a, double b, double L)
        {
            for (int w = -1; w <= 1; w++)
            {
                double x = t + w * L;
                if (x >= a && x <= b) return true;
            }
            return false;
        }

        static void Ramp(RoadGraph g, KerbCorner kc, KerbLoop loop, KerbNetwork kn, NodeSurface ns, CornerSurface c, KerbSettings st)
        {
            double arc = kc.ArcLength, tm = 0.5 * (kc.t0 + kc.t1);
            // an alley mouth (roads-R4): the kerb drops along the whole return, flaring back over a metre each side
            if (ns.arms[c.armI].alley || ns.arms[c.armJ].alley)
            {
                kc.ramp = RampKind.AlleyMouth;
                kc.dep0 = kc.t0;
                kc.dep1 = kc.t1;
                return;
            }
            // a perpendicular ramp only where the walk is deep enough on the whole arc
            double minDepth = double.MaxValue;
            foreach (int fid in new[] { FaceAt(kn, loop, kc.t0 + 1e-6), FaceAt(kn, loop, kc.t1 - 1e-6) })
            {
                var f = kn.Face(fid);
                if (f == null) continue;
                for (int i = 0; i < f.Count; i++)
                    if (InRange(f.t0 + f.s[i], kc.t0, kc.t1, loop.Length) && f.walk) minDepth = Math.Min(minDepth, f.width[i]);
            }
            if (minDepth >= st.perpendicularDepth && minDepth < double.MaxValue)
            {
                kc.ramp = RampKind.Perpendicular;
                kc.open0 = kc.t0 + 0.25 * arc;
                kc.open1 = kc.t0 + 0.75 * arc;
                kc.dep0 = kc.open0 - 0.5 * st.perpRampWidth;
                kc.dep1 = kc.open1 + 0.5 * st.perpRampWidth;
                return;
            }
            kc.ramp = RampKind.Depressed;
            double len = Math.Max(st.depressedMin, Math.Min(st.depressedMax, 0.5 * arc));
            kc.dep0 = tm - 0.5 * len;
            kc.dep1 = tm + 0.5 * len;
            // a marked crosswalk lands on this corner's kerb: lower the kerb across its whole band
            foreach (var X in new[] { ns.arms[c.armI], ns.arms[c.armJ] })
            {
                if (!X.crosswalk) continue;
                bool isI = X == ns.arms[c.armI];
                // only a crosswalk that lands on a walk lowers the kerb (not one across an unkerbed alley mouth)
                var xr = g.RoadOf(g.edges[X.edge].sourceRoad);
                if (!KerbRules.Walk(xr, g.edges[X.edge].cls, isI ? (X.atA ? 0 : 1) : (X.atA ? 1 : 0))) continue;
                double u = isI ? X.hwL : -X.hwR;
                foreach (double sq in new[] { X.sCwInner, X.sCwOuter })
                {
                    var p = X.frame.P(sq, u);
                    if (LoopPosition(loop, p, kc.t0 - 12, kc.t1 + 12, out double tp, out double d) && d < 0.25)
                    {
                        kc.dep0 = Math.Min(kc.dep0, tp);
                        kc.dep1 = Math.Max(kc.dep1, tp);
                    }
                }
            }
        }

        /// <summary>The loop arc length of the point of the loop nearest p, searching t in [ta, tb] (wrapping).</summary>
        static bool LoopPosition(KerbLoop loop, D2 p, double ta, double tb, out double t, out double distance)
        {
            t = double.NaN;
            distance = double.MaxValue;
            int n = loop.Count;
            for (int k = 0; k < n; k++)
            {
                double s0 = loop.t[k], s1 = k + 1 < n ? loop.t[k + 1] : loop.Length;
                bool near = false;
                for (int w = -1; w <= 1 && !near; w++) near = s1 + w * loop.Length >= ta && s0 + w * loop.Length <= tb;
                if (!near) continue;
                D2 a = loop.pts[k].XZ, b = loop.pts[(k + 1) % n].XZ;
                double u = Math.Max(0, Math.Min(1, Param(a, b, p)));
                double d = D2.Distance(a + (b - a) * u, p);
                if (d < distance)
                {
                    distance = d;
                    double tt = s0 + u * (s1 - s0);
                    // report it in the window's own unrolled range
                    while (tt < ta - 1e-9) tt += loop.Length;
                    while (tt > tb + 1e-9) tt -= loop.Length;
                    t = tt;
                }
            }
            return !double.IsNaN(t);
        }

        // ------------------------------------------------------------------ heights

        /// <summary>The face height of the kerb above the gutter at loop arc length t: barrier, lowered at depressed corners and ramp openings.</summary>
        /// <summary>True where a pedestrian crossing lands (a depressed corner's lowered stretch, a perpendicular ramp opening): the detectable-warning strip goes there, not across an alley mouth.</summary>
        public static bool WarningAt(KerbNetwork kn, KerbFace f, double t, KerbSettings st)
        {
            if (!f.walk) return false;
            double L = kn.loops[f.loop].Length;
            foreach (var cid in f.corners)
            {
                var c = kn.corners[cid];
                if (c.ramp == RampKind.Depressed && InRange(t, c.dep0, c.dep1, L)) return true;
                if (c.ramp == RampKind.Perpendicular)
                {
                    double half = 0.5 * st.perpRampWidth;
                    if (InRange(t, c.open0 - half, c.open0 + half, L) || InRange(t, c.open1 - half, c.open1 + half, L)) return true;
                }
            }
            return false;
        }

        /// <summary>
        /// roads-R4 step 4: a kerbed face whose neighbour on the loop is unkerbed (the town edge, or a street corner onto a
        /// rural road) ends in a taper to flush, so the kerb never stops as a 150 mm step and the walk ramps down to the shoulder.
        /// </summary>
        static void Tapers(KerbNetwork kn)
        {
            foreach (var loop in kn.loops)
            {
                var fs = kn.faces.Where(f => f.loop == loop.id).OrderBy(f => f.t0).ToList();
                if (fs.Count < 2) continue;
                for (int i = 0; i < fs.Count; i++)
                {
                    KerbFace f = fs[i], next = fs[(i + 1) % fs.Count];
                    double gap = next.t0 - f.t1;
                    if (i == fs.Count - 1) gap += loop.Length;
                    if (Math.Abs(gap) > 1e-3) continue;
                    if (f.kerbed && !next.kerbed) { f.taperEnd = true; kn.tapers++; }
                    if (!f.kerbed && next.kerbed) { next.taperStart = true; kn.tapers++; }
                }
            }
        }

        public static double KerbHeight(KerbNetwork kn, KerbFace f, double t, KerbSettings st)
        {
            if (!f.kerbed) return 0;
            double h = st.barrier;
            var loop = kn.loops[f.loop];
            if (f.taperStart || f.taperEnd)
            {
                double ls = t - f.t0;
                if (ls < -1e-9) ls += loop.Length;
                if (f.taperStart) h = Math.Min(h, st.barrier * Math.Max(0, Math.Min(1, ls / st.taper)));
                if (f.taperEnd) h = Math.Min(h, st.barrier * Math.Max(0, Math.Min(1, (f.t1 - f.t0 - ls) / st.taper)));
            }
            foreach (var cid in f.corners)
            {
                var c = kn.corners[cid];
                if (c.ramp == RampKind.None) continue;
                if (c.ramp == RampKind.Depressed) h = Math.Min(h, Lowered(t, c.dep0, c.dep1, st.rampRun, loop.Length, st));
                else if (c.ramp == RampKind.AlleyMouth) h = Math.Min(h, Lowered(t, c.dep0, c.dep1, st.alleyFlare, loop.Length, st));
                else
                {
                    double half = 0.5 * st.perpRampWidth, flare = (st.barrier - st.dropped) / 0.10;
                    h = Math.Min(h, Lowered(t, c.open0 - half, c.open0 + half, flare, loop.Length, st));
                    h = Math.Min(h, Lowered(t, c.open1 - half, c.open1 + half, flare, loop.Length, st));
                }
            }
            return h;
        }

        static double Lowered(double t, double a, double b, double run, double L, KerbSettings st)
        {
            double best = st.barrier;
            for (int w = -1; w <= 1; w++)
            {
                double x = t + w * L;
                double d = x < a ? a - x : x > b ? x - b : 0;
                double h = d >= run ? st.barrier : st.dropped + (st.barrier - st.dropped) * d / run;
                best = Math.Min(best, h);
            }
            return best;
        }

        static void Heights(KerbNetwork kn, KerbFace f, KerbSettings st)
        {
            for (int i = 0; i < f.Count; i++)
            {
                double h = KerbHeight(kn, f, f.t0 + f.s[i], st);
                double top = f.gutter[i] + h;
                f.top.Add(top);
                double w = f.width[i];
                f.back.Add(f.kerb[i] + f.normal[i] * w);
                f.backY.Add(f.walk && w > st.kerbTop ? top + st.walkCrossSlope * (w - st.kerbTop) : top);
            }
        }

        // ------------------------------------------------------------------ forbidden intervals (card step 4)

        static void Forbidden(RoadGraph g, RoadSurfaces surf, KerbNetwork kn, KerbSettings st, RoadFrame[] frames)
        {
            var byKey = new Dictionary<(int, int), List<KerbFace>>();
            foreach (var f in kn.faces)
            {
                if (!byKey.TryGetValue((f.edge, f.side), out var l)) byKey[(f.edge, f.side)] = l = new List<KerbFace>();
                l.Add(f);
            }
            // corner returns and ramps, on the loop
            foreach (var c in kn.corners)
            {
                var loop = kn.loops[c.loop];
                var fa = kn.Face(c.faceA);
                var fb = kn.Face(c.faceB);
                double ba = fa != null ? Buffer(fa, st) : st.cornerReturnLocal, bb = fb != null ? Buffer(fb, st) : st.cornerReturnLocal;
                foreach (var f in kn.faces)
                {
                    if (f.loop != c.loop || (f != fa && f != fb && !f.corners.Contains(c.id))) continue;
                    AddLoopRange(f, loop, c.t0 - ba, c.t1 + bb, FrontageBlock.CornerReturn);
                    if (c.ramp == RampKind.Depressed) AddLoopRange(f, loop, c.dep0 - st.rampRun, c.dep1 + st.rampRun, FrontageBlock.Ramp);
                    else if (c.ramp == RampKind.Perpendicular)
                    {
                        double half = 0.5 * st.perpRampWidth + (st.barrier - st.dropped) / 0.10;
                        AddLoopRange(f, loop, c.open0 - half, c.open0 + half, FrontageBlock.Ramp);
                        AddLoopRange(f, loop, c.open1 - half, c.open1 + half, FrontageBlock.Ramp);
                    }
                }
            }
            // no surface (R1 fallbacks): the ribbons overlap at the node; keep driveways back from it
            foreach (var ns in surf.nodes)
            {
                if (ns.mode != SurfaceMode.Fallback) continue;
                var node = g.nodes[ns.node].p.XZ;
                foreach (var f in kn.faces)
                {
                    double reach = 15 + Buffer(f, st);
                    double lo = double.MaxValue, hi = double.MinValue;
                    for (int i = 0; i < f.Count; i++)
                        if (D2.Distance(f.kerb[i], node) <= reach) { lo = Math.Min(lo, f.s[i]); hi = Math.Max(hi, f.s[i]); }
                    if (hi >= lo) Add(f, lo, hi, FrontageBlock.CornerReturn);
                }
            }
            foreach (var ns in surf.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback) continue;
                foreach (var a in ns.arms)
                {
                    int leftSide = a.atA ? 0 : 1, rightSide = 1 - leftSide;
                    if (a.crosswalk)
                    {
                        foreach (var (u, side) in new[] { (a.hwL, leftSide), (-a.hwR, rightSide) })
                            OnFaces(byKey, a.edge, side, a.frame.P(a.sCwInner - st.crosswalkMargin, u), a.frame.P(a.sCwOuter + st.crosswalkMargin, u), FrontageBlock.Crosswalk);
                    }
                    if (a.sStop >= 0)
                    {
                        var road = g.RoadOf(g.edges[a.edge].sourceRoad);
                        int ow = road?.oneway ?? 0;
                        bool inboundOnly = (ow == 1 && !a.atA) || (ow == -1 && a.atA);
                        double s0 = Math.Max(0, a.sStop - st.stopZone), s1 = a.sStop + st.stopZone;
                        OnFaces(byKey, a.edge, leftSide, a.frame.P(s0, a.hwL), a.frame.P(s1, a.hwL), FrontageBlock.StopLine);
                        if (inboundOnly) OnFaces(byKey, a.edge, rightSide, a.frame.P(s0, -a.hwR), a.frame.P(s1, -a.hwR), FrontageBlock.StopLine);
                    }
                }
            }
            foreach (var inlet in surf.inlets)
            {
                if (inlet.edge < 0) continue;
                var p = inlet.p.XZ;
                foreach (int side in new[] { 0, 1 })
                {
                    if (!byKey.TryGetValue((inlet.edge, side), out var l)) continue;
                    foreach (var f in l)
                    {
                        double s = Project(f, p, out double d);
                        if (d <= 0.35) Add(f, s - st.inletHalf, s + st.inletHalf, FrontageBlock.Inlet);
                    }
                }
            }
            foreach (var f in kn.faces)
            {
                if (f.bridge) Add(f, 0, f.Length, FrontageBlock.Other);
                f.forbidden.Sort((a, b) =>
                {
                    int r = a.s0.CompareTo(b.s0);
                    if (r != 0) return r;
                    r = a.s1.CompareTo(b.s1);
                    return r != 0 ? r : ((byte)a.reason).CompareTo((byte)b.reason);
                });
            }
        }

        static double Buffer(KerbFace f, KerbSettings st) =>
            f.cls == StreetClass.MainStreet || f.cls == StreetClass.Highway ? st.cornerReturnMajor : st.cornerReturnLocal;

        /// <summary>A loop range [a, b] (may wrap) intersected with the face's own range, as face arc lengths.</summary>
        static void AddLoopRange(KerbFace f, KerbLoop loop, double a, double b, FrontageBlock why)
        {
            double L = loop.Length;
            for (int w = -1; w <= 1; w++)
            {
                double x0 = Math.Max(a + w * L, f.t0), x1 = Math.Min(b + w * L, f.t1);
                if (x1 > x0 + 1e-6) Add(f, x0 - f.t0, x1 - f.t0, why);
            }
        }

        static void OnFaces(Dictionary<(int, int), List<KerbFace>> byKey, int edge, int side, D2 p0, D2 p1, FrontageBlock why)
        {
            if (!byKey.TryGetValue((edge, side), out var l)) return;
            foreach (var f in l)
            {
                double s0 = Project(f, p0, out double d0), s1 = Project(f, p1, out double d1);
                if (d0 > 0.5 && d1 > 0.5) continue;
                if (s1 < s0) { var t = s0; s0 = s1; s1 = t; }
                if (s1 - s0 < 0.1) continue;
                Add(f, s0, s1, why);
            }
        }

        static void Add(KerbFace f, double s0, double s1, FrontageBlock why)
        {
            s0 = Math.Max(0, s0);
            s1 = Math.Min(f.Length, s1);
            if (s1 <= s0 + 1e-6) return;
            f.forbidden.Add(new ForbiddenInterval { s0 = (float)s0, s1 = (float)s1, reason = why });
        }

        /// <summary>The arc length of the face's kerb point nearest p, and the distance.</summary>
        public static double Project(KerbFace f, D2 p, out double distance)
        {
            distance = double.MaxValue;
            double best = 0;
            for (int i = 0; i + 1 < f.Count; i++)
            {
                double u = Math.Max(0, Math.Min(1, Param(f.kerb[i], f.kerb[i + 1], p)));
                double d = D2.Distance(f.kerb[i] + (f.kerb[i + 1] - f.kerb[i]) * u, p);
                if (d < distance) { distance = d; best = f.s[i] + u * (f.s[i + 1] - f.s[i]); }
            }
            return best;
        }

        static void Unfaced(RoadGraph g, KerbNetwork kn)
        {
            var has = new HashSet<(int, int)>();
            foreach (var f in kn.faces) has.Add((f.edge, f.side));
            foreach (var e in g.edges)
            {
                var road = g.RoadOf(e.sourceRoad);
                if (road != null && (road.bridge || road.tunnel)) continue;
                foreach (int side in new[] { 0, 1 })
                    if (!has.Contains((e.id, side)))
                        kn.unfaced.Add(string.Format(Inv, "edge {0} (R{1} {2}, {3:F1} m) {4} side: no face (covered by other roads or junction surfaces)",
                            e.id, e.sourceRoad, e.cls, e.Length, side == 0 ? "left" : "right"));
            }
        }
    }
}
