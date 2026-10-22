using System;
using System.Collections.Generic;
using Clipper2Lib;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R2: the kerbside partition (05 §5.7, §5.10). Per kerb segment a walk trapezoid (kerb face to walk back edge,
    /// along the segment's normal), fans where the kerb turns away from the block, then the verge behind each walk and the
    /// gravel shoulder beside each unkerbed road. Pieces are taken in that order (walks of every face, then verges, then
    /// shoulders; faces by id, segments in order) and each keeps only what no earlier piece holds, so the partition has
    /// no overlaps and every point gets one surface and one ground height, from one kerb segment. A piece left whole
    /// stays one convex polygon; a clipped one is ear-clipped into triangles. Heights come from the piece's own function
    /// at each vertex: the walk is the kerb top, then rises at the walk's cross fall; the ground under it runs from the
    /// asphalt's sunk level at the kerb face to the walk's back edge less 0.05 m; verge and shoulder ground is the surface.
    /// </summary>
    public static class KerbPieceBuilder
    {
        sealed class Raw
        {
            public KerbPieceKind kind;
            public int face, seg;
            public D2[] poly;
            public Func<D2, (double s, double g)> f;
        }

        public static void Build(KerbNetwork kn, KerbSettings st, IReadOnlyList<List<D3>> asphalt = null)
        {
            var raws = new List<Raw>();
            foreach (var kind in new[] { KerbPieceKind.Walk, KerbPieceKind.Verge, KerbPieceKind.Shoulder })
                foreach (var loop in kn.loops)
                {
                    KerbFace prev = null;
                    foreach (int fid in loop.faces)
                    {
                        var f = kn.faces[fid];
                        if (f.bridge) { prev = null; continue; }
                        for (int i = 0; i + 1 < f.Count; i++) Segment(f, i, kind, st, raws);
                        for (int i = 1; i + 1 < f.Count; i++) Wedge(f, i, f, i, kind, st, raws);
                        if (prev != null && Math.Abs(prev.t1 - f.t0) < 1e-6) Wedge(prev, prev.Count - 1, f, 0, kind, st, raws);
                        prev = f;
                    }
                    if (loop.faces.Count >= 2)
                    {
                        var first = kn.faces[loop.faces[0]];
                        var last = kn.faces[loop.faces[loop.faces.Count - 1]];
                        if (!first.bridge && !last.bridge && Math.Abs(last.t1 - loop.Length) < 1e-6 && first.t0 < 1e-6) Wedge(last, last.Count - 1, first, 0, kind, st, raws);
                    }
                }
            // walks of every face before any verge: sort stably by kind (the loops above already run kind by kind)
            Partition(raws, kn.pieces, st, asphalt);
            NearestEdgeHeights(kn, st);
        }

        /// <summary>
        /// Verges and shoulders take their height from the nearest point of the paved edge they lie behind (the walk's
        /// back edge; the pavement edge of an unkerbed road), not from the kerb segment that made the piece: at sloped
        /// corners and along ramps the two differ, and the ground 1 m out must follow the edge it sits beside.
        /// </summary>
        static void NearestEdgeHeights(KerbNetwork kn, KerbSettings st)
        {
            var back = new EdgeIndex();
            var open = new EdgeIndex();
            foreach (var f in kn.faces)
            {
                if (f.bridge) continue;
                double slope = f.cls == Backroads.Core.WorldBake.StreetClass.Dirt ? 0.04 : 0.02;
                for (int i = 0; i + 1 < f.Count; i++)
                    if (f.kerbed) back.Add(f.back[i], f.back[i + 1], f.backY[i], f.backY[i + 1], 0);
                    else open.Add(f.kerb[i], f.kerb[i + 1], f.gutter[i], f.gutter[i + 1], slope);
            }
            foreach (var p in kn.pieces)
            {
                if (p.kind == KerbPieceKind.Walk) continue;
                var idx = p.kind == KerbPieceKind.Verge ? back : open;
                for (int k = 0; k < p.poly.Length; k++)
                {
                    if (!idx.Nearest(p.poly[k], out double y, out double d, out double slope)) continue;
                    if (p.kind == KerbPieceKind.Shoulder) y = y - st.shoulderLip - Math.Min(slope * d, 0.15);
                    p.surfaceY[k] = y;
                    p.groundY[k] = y;
                }
            }
        }

        sealed class EdgeIndex
        {
            readonly List<(D2 a, D2 b, double ya, double yb, double slope)> m_E = new List<(D2, D2, double, double, double)>();
            readonly Dictionary<(int, int), List<int>> m_Cells = new Dictionary<(int, int), List<int>>();
            const double C = 4.0;
            static int Cx(double v) => (int)Math.Floor(v / C);

            public void Add(D2 a, D2 b, double ya, double yb, double slope)
            {
                int id = m_E.Count;
                m_E.Add((a, b, ya, yb, slope));
                for (int x = Cx(Math.Min(a.x, b.x)); x <= Cx(Math.Max(a.x, b.x)); x++)
                    for (int z = Cx(Math.Min(a.z, b.z)); z <= Cx(Math.Max(a.z, b.z)); z++)
                    {
                        if (!m_Cells.TryGetValue((x, z), out var l)) m_Cells[(x, z)] = l = new List<int>();
                        l.Add(id);
                    }
            }

            /// <summary>The nearest edge point within about 4 m: its height, distance and the road's cross slope.</summary>
            public bool Nearest(D2 p, out double y, out double dist, out double slope)
            {
                y = double.NaN; dist = double.MaxValue; slope = 0;
                int best = -1;
                for (int x = Cx(p.x) - 1; x <= Cx(p.x) + 1; x++)
                    for (int z = Cx(p.z) - 1; z <= Cx(p.z) + 1; z++)
                    {
                        if (!m_Cells.TryGetValue((x, z), out var l)) continue;
                        foreach (int i in l)
                        {
                            var e = m_E[i];
                            var d = e.b - e.a;
                            double l2 = d.Dot(d), t = l2 > 0 ? Math.Max(0, Math.Min(1, (p - e.a).Dot(d) / l2)) : 0;
                            double dd = D2.Distance(e.a + d * t, p);
                            if (dd < dist - 1e-12 || (Math.Abs(dd - dist) <= 1e-12 && i < best)) { dist = dd; best = i; y = e.ya + (e.yb - e.ya) * t; slope = e.slope; }
                        }
                    }
                return best >= 0;
            }
        }

        // ------------------------------------------------------------------ raw pieces and their height functions

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        static void Segment(KerbFace f, int i, KerbPieceKind kind, KerbSettings st, List<Raw> raws)
        {
            D2 k0 = f.kerb[i], k1 = f.kerb[i + 1];
            var d = k1 - k0;
            double len = d.Length;
            if (len < 1e-6) return;
            var dir = d * (1 / len);
            var n = dir.Left;
            double w0 = f.width[i], w1 = f.width[i + 1];
            double g0 = f.gutter[i], g1 = f.gutter[i + 1], t0 = f.top[i], t1 = f.top[i + 1], b0 = f.backY[i], b1 = f.backY[i + 1];
            void Local(D2 q, out double t, out double u)
            {
                t = Math.Max(0, Math.Min(1, (q - k0).Dot(dir) / len));
                u = (q - k0).Dot(n);
            }
            switch (kind)
            {
                case KerbPieceKind.Walk:
                    if (!f.kerbed || w0 <= 1e-3 || w1 <= 1e-3) return;
                    raws.Add(new Raw
                    {
                        kind = kind, face = f.id, seg = i, poly = new[] { k0, k1, k1 + n * w1, k0 + n * w0 },
                        f = q =>
                        {
                            Local(q, out double t, out double u);
                            return Walk(u, Lerp(g0, g1, t), Lerp(t0, t1, t), Lerp(b0, b1, t), Lerp(w0, w1, t), st);
                        },
                    });
                    return;
                case KerbPieceKind.Verge:
                    if (!f.kerbed) return;
                    raws.Add(new Raw
                    {
                        kind = kind, face = f.id, seg = i, poly = new[] { k0 + n * w0, k1 + n * w1, k1 + n * (w1 + st.verge), k0 + n * (w0 + st.verge) },
                        f = q => { Local(q, out double t, out _); double y = Lerp(b0, b1, t); return (y, y); },
                    });
                    return;
                default:
                    if (f.kerbed) return;
                    double slope = f.cls == Backroads.Core.WorldBake.StreetClass.Dirt ? 0.04 : 0.02;
                    raws.Add(new Raw
                    {
                        kind = kind, face = f.id, seg = i, poly = new[] { k0, k1, k1 + n * st.shoulder, k0 + n * st.shoulder },
                        f = q =>
                        {
                            Local(q, out double t, out double u);
                            double y = Lerp(g0, g1, t) - st.shoulderLip - Math.Min(slope * Math.Max(0, u), 0.15);
                            return (y, y);
                        },
                    });
                    return;
            }
        }

        /// <summary>The walk's surface and the plate ground at distance u from the kerb face.</summary>
        static (double s, double g) Walk(double u, double gutter, double top, double back, double w, KerbSettings st)
        {
            u = Math.Max(0, Math.Min(w, u));
            double s = u <= st.kerbTop ? top : top + st.walkCrossSlope * (u - st.kerbTop);
            double g = gutter - st.sinkAsphalt + (w > 1e-6 ? u / w : 1) * (back - st.sinkWalk - gutter + st.sinkAsphalt);
            return (s, g);
        }

        /// <summary>Where the kerb turns away from the block the segment pieces open a gap: fans around the kerb point.</summary>
        static void Wedge(KerbFace fa, int ia, KerbFace fb, int ib, KerbPieceKind kind, KerbSettings st, List<Raw> raws)
        {
            if (ia < 1 || ib + 1 >= fb.Count) return;
            D2 p = fb.kerb[ib], a = fa.kerb[ia - 1], b = fb.kerb[ib + 1];
            var d0 = (p - a).Normalized;
            var d1 = (b - p).Normalized;
            if (d0.Cross(d1) >= -1e-9) return;
            double a0 = d0.Left.Angle, sweep = Geo.Wrap(d1.Left.Angle - a0);
            if (sweep >= 0) return;
            bool kerbed = fa.kerbed || fb.kerbed;
            var f = fb;
            double gutter = f.gutter[ib], top = Math.Min(fa.top[ia], fb.top[ib]), back = Math.Min(fa.backY[ia], fb.backY[ib]);
            double w = Math.Max(fa.width[ia], fb.width[ib]);
            double inner, outer;
            Func<D2, (double, double)> fn;
            switch (kind)
            {
                case KerbPieceKind.Walk:
                    if (!kerbed || w <= 1e-3) return;
                    inner = 0; outer = w;
                    fn = q => Walk(D2.Distance(q, p), gutter, top, back, w, st);
                    break;
                case KerbPieceKind.Verge:
                    if (!kerbed) return;
                    inner = 0; outer = w + st.verge;
                    fn = q => (back, back);
                    break;
                default:
                    if (kerbed) return;
                    double slope = f.cls == Backroads.Core.WorldBake.StreetClass.Dirt ? 0.04 : 0.02;
                    inner = 0; outer = st.shoulder;
                    fn = q => { double y = gutter - st.shoulderLip - Math.Min(slope * D2.Distance(q, p), 0.15); return (y, y); };
                    break;
            }
            int steps = Math.Max(1, (int)Math.Ceiling(-sweep / (30 * Geo.Deg)));
            for (int k = 0; k < steps; k++)
            {
                var u0 = D2.FromAngle(a0 + sweep * k / steps);
                var u1 = D2.FromAngle(a0 + sweep * (k + 1) / steps);
                raws.Add(new Raw { kind = kind, face = fb.id, seg = ib, poly = new[] { p + u0 * inner, p + u1 * outer, p + u0 * outer }, f = fn });
            }
        }

        // ------------------------------------------------------------------ the partition

        const double Scale = 10000.0, Cell = 8.0;

        static void Partition(List<Raw> raws, List<KerbPiece> pieces, KerbSettings st, IReadOnlyList<List<D3>> asphalt)
        {
            var taken = new List<Path64>();
            var cells = new Dictionary<(int, int), List<int>>();
            int Cx(double v) => (int)Math.Floor(v / Cell);
            var seen = new HashSet<int>();
            var parts = new Dictionary<(KerbPieceKind, int, int), int>();
            void Take(Path64 path, double x0, double x1, double z0, double z1)
            {
                int tid = taken.Count;
                taken.Add(path);
                for (int x = Cx(x0); x <= Cx(x1); x++)
                    for (int z = Cx(z0); z <= Cx(z1); z++)
                    {
                        if (!cells.TryGetValue((x, z), out var l)) cells[(x, z)] = l = new List<int>();
                        l.Add(tid);
                    }
            }
            // the asphalt is taken first: no walk, verge or shoulder lies on a road (a narrow block, a tight corner)
            if (asphalt != null)
                foreach (var a in asphalt)
                {
                    var p2 = new D2[a.Count];
                    for (int k = 0; k < p2.Length; k++) p2[k] = a[k].XZ;
                    var ccw = CCW(p2);
                    if (ccw == null) continue;
                    double ax0 = double.MaxValue, ax1 = double.MinValue, az0 = double.MaxValue, az1 = double.MinValue;
                    foreach (var q in ccw) { ax0 = Math.Min(ax0, q.x); ax1 = Math.Max(ax1, q.x); az0 = Math.Min(az0, q.z); az1 = Math.Max(az1, q.z); }
                    Take(ToPath(ccw), ax0, ax1, az0, az1);
                }
            foreach (var r in raws)
            {
                var poly = CCW(r.poly);
                if (poly == null) continue;
                double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
                foreach (var q in poly) { x0 = Math.Min(x0, q.x); x1 = Math.Max(x1, q.x); z0 = Math.Min(z0, q.z); z1 = Math.Max(z1, q.z); }
                var clip = new Paths64();
                seen.Clear();
                for (int x = Cx(x0); x <= Cx(x1); x++)
                    for (int z = Cx(z0); z <= Cx(z1); z++)
                        if (cells.TryGetValue((x, z), out var l))
                            foreach (int id in l)
                                if (seen.Add(id)) clip.Add(taken[id]);
                var subject = ToPath(poly);
                List<D2[]> keep;
                if (clip.Count == 0 || !Touches(subject, clip)) keep = new List<D2[]> { poly };
                else
                {
                    var c = new Clipper64();
                    c.AddSubject(subject);
                    c.AddClip(clip);
                    var sol = new Paths64();
                    c.Execute(ClipType.Difference, FillRule.NonZero, sol);
                    keep = new List<D2[]>();
                    foreach (var path in sol)
                    {
                        if (Math.Abs(Clipper.Area(path)) / (Scale * Scale) < 1e-4) continue;
                        var pts = new D2[path.Count];
                        for (int k = 0; k < pts.Length; k++) pts[k] = new D2(path[k].X / Scale, path[k].Y / Scale);
                        var ccw = CCW(pts);
                        if (ccw == null) continue;
                        if (Convex(ccw)) { keep.Add(ccw); continue; }
                        var tris = Geom2.EarClip(ccw);
                        for (int k = 0; k + 2 < tris.Count; k += 3)
                        {
                            var t = CCW(new[] { ccw[tris[k]], ccw[tris[k + 1]], ccw[tris[k + 2]] });
                            if (t != null) keep.Add(t);
                        }
                    }
                }
                foreach (var pc in keep)
                {
                    var key = (r.kind, r.face, r.seg);
                    parts.TryGetValue(key, out int part);
                    parts[key] = part + 1;
                    var piece = new KerbPiece { kind = r.kind, face = r.face, seg = r.seg, part = part, poly = pc, surfaceY = new double[pc.Length], groundY = new double[pc.Length] };
                    for (int k = 0; k < pc.Length; k++)
                    {
                        var (s, g) = r.f(pc[k]);
                        piece.surfaceY[k] = s;
                        piece.groundY[k] = g;
                    }
                    pieces.Add(piece);
                }
                // what this piece held is taken (the whole raw piece: its clipped parts plus what was already taken)
                Take(subject, x0, x1, z0, z1);
            }
        }

        static Path64 ToPath(D2[] p)
        {
            var path = new Path64(p.Length);
            foreach (var q in p) path.Add(new Point64((long)Math.Round(q.x * Scale), (long)Math.Round(q.z * Scale)));
            return path;
        }

        /// <summary>A cheap reject: true when some clip path's box meets the subject's box.</summary>
        static bool Touches(Path64 s, Paths64 clip)
        {
            var b = Clipper.GetBounds(s);
            foreach (var c in clip)
            {
                var r = Clipper.GetBounds(c);
                if (r.left < b.right && r.right > b.left && r.top < b.bottom && r.bottom > b.top) return true;
            }
            return false;
        }

        static D2[] CCW(D2[] p)
        {
            var w = Geom2.Weld(p, 1e-6);
            if (w.Count < 3) return null;
            double a = Geom2.SignedArea(w);
            if (Math.Abs(a) < 1e-4) return null;
            if (a < 0) w.Reverse();
            return w.ToArray();
        }

        static bool Convex(D2[] v)
        {
            int n = v.Length;
            for (int i = 0; i < n; i++)
            {
                D2 a = v[i], b = v[(i + 1) % n], c = v[(i + 2) % n];
                if ((b - a).Cross(c - b) < -1e-12) return false;
            }
            return true;
        }
    }
}
