using System;
using System.Collections.Generic;
using System.Globalization;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R2 step 6 (05 §5.10, overview contract 4): the road plates lane W's terrain bake applies. Small convex
    /// pieces with per-vertex heights, so W's deterministic triangulation reproduces them exactly: ribbon quads (station
    /// to station, gutter line to gutter line), junction triangles (R1's own mesh), and the kerbside partition
    /// (KerbPieceBuilder: walks, then a flat 1.2 m verge behind them, or a gravel shoulder beside unkerbed roads) so the
    /// ground 1 m outside every paved edge stays at the edge's height. Paved plates sit under the meshes (asphalt
    /// 0.06 m, kerb and walk 0.05 m; the walk plate runs from the asphalt's level at the kerb face up to the walk's back
    /// edge, so a coarse terrain grid never pokes through the gutter) and take no ruts; gravel and dirt are the ground
    /// itself. Priorities: verge and shoulder 0, walks 10, ribbons 20, junctions 30, driveway aprons 40 (later wins), so
    /// asphalt wins wherever pieces overlap. Bridges and tunnels get none (the ground stays under the deck) but for the
    /// abutment plates at bridge ends (roads-R4, priority 25).
    /// </summary>
    public static class RoadPlateBuilder
    {
        public const int Version = 2;
        public const int PriorityVerge = 0, PriorityWalk = 10, PriorityRibbon = 20, PriorityAbutment = 25, PriorityJunction = 30, PriorityIsland = 35, PriorityApron = 40;
        public const byte Grass = 0, Gravel = 5, Concrete = 6;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static List<Plate> Build(RoadGraph g, RoadSurfaces surf, KerbNetwork kn, IHeightField ground, KerbSettings st = null,
            IReadOnlyList<DrivewayCut> cuts = null, DrivewayCutter cutter = null)
        {
            st = st ?? new KerbSettings();
            var plates = new List<Plate>();
            Ribbons(g, surf, st, plates);
            Junctions(g, surf, st, plates);
            Pieces(kn, ground, plates);
            if (cuts != null && cutter != null) Aprons(kn, cuts, cutter, st, plates);
            Abutments(BridgeBuilder.Build(g, ground), new BridgeSettings(), st, plates);
            Islands(surf, st, plates);
            return plates;
        }

        /// <summary>
        /// roads-R4: a roundabout's raised islands over its junction plate (priority 35): the central island is grass at its
        /// kerb height (2 cm under the concrete band), the truck apron and the splitters concrete under their tops. A
        /// mini-roundabout's dome is on the asphalt plate.
        /// </summary>
        static void Islands(RoadSurfaces surf, KerbSettings st, List<Plate> plates)
        {
            var sst = new SurfaceSettings();
            foreach (var ns in surf.nodes)
            {
                if (ns.mode != SurfaceMode.Roundabout || ns.roundabout == null) continue;
                var G = ns.roundabout;
                var rs = sst.roundabout;
                D3 V(double r, double a, double dy)
                {
                    var p = G.c + new D2(Math.Cos(a), Math.Sin(a)) * r;
                    return new D3(p.x, SurfaceBuilder.JunctionHeight(ns, p, sst) + dy, p.z);
                }
                if (!G.mini)
                {
                    int N = Math.Max(24, (int)Math.Ceiling(2 * Math.PI * G.Ri / 2.0));
                    var c = new D3(G.c.x, SurfaceBuilder.JunctionHeight(ns, G.c, sst) + rs.islandKerb - 0.02, G.c.z);
                    for (int k = 0; k < N; k++)
                    {
                        double a0 = 2 * Math.PI * k / N, a1 = 2 * Math.PI * (k + 1) / N;
                        Piece(plates, string.Format(Inv, "island:n{0:D4}:{1:D3}", ns.node, k), PlateKind.Road, Grass, false, PriorityIsland, 2f,
                            new[] { c, V(G.Ra, a0, rs.islandKerb - 0.02), V(G.Ra, a1, rs.islandKerb - 0.02) }, 0);
                        if (G.Ri - G.Ra > 0.05)
                            Piece(plates, string.Format(Inv, "truckapron:n{0:D4}:{1:D3}", ns.node, k), PlateKind.Road, Concrete, true, PriorityIsland, 2f,
                                new[] { V(G.Ri, a0, rs.apronKerb), V(G.Ri, a1, rs.apronKerb), V(G.Ra, a1, rs.apronKerb), V(G.Ra, a0, rs.apronKerb) }, st.sinkWalk);
                    }
                }
                foreach (var L in G.legs)
                    for (int q = 0; q < L.splitters.Count; q++)
                    {
                        var poly = L.splitters[q];
                        var tris = Geom2.EarClip(poly);
                        for (int k = 0; k + 2 < tris.Count; k += 3)
                        {
                            D3 W(D2 p) => new D3(p.x, SurfaceBuilder.JunctionHeight(ns, p, sst) + rs.islandKerb, p.z);
                            Piece(plates, string.Format(Inv, "splitter:n{0:D4}:{1}:{2}:{3:D3}", ns.node, L.arm, q, k / 3), PlateKind.Road, Concrete, true, PriorityIsland, 2f,
                                new[] { W(poly[tris[k]]), W(poly[tris[k + 1]]), W(poly[tris[k + 2]]) }, st.sinkWalk);
                        }
                    }
            }
        }

        /// <summary>
        /// roads-R4 step 2: under each bridge end's abutment zone (where the ground does not fall clear of the soffit) the
        /// fill is held at road level across the deck and its wing walls: pavement-edge heights at the edges and wings, the
        /// centreline's in the middle, 0.06 m under the pavement like a ribbon. The DEM under the free span is untouched.
        /// </summary>
        public static void Abutments(IReadOnlyList<BridgeDeck> decks, BridgeSettings bst, KerbSettings st, List<Plate> plates)
        {
            foreach (var d in decks)
            {
                if (d.FreeSpan <= 0 && d.sA <= 0) continue;
                var ss = BridgeBuilder.Stations(d, 2.0, d.sA, d.length - d.sB);
                // lateral lines: the right wing, the right pavement edge, the centre, the left pavement edge, the left wing
                double U(double s, int k) => k == 0 ? -(d.Outer(s, -1) + bst.wing) : k == 1 ? -d.Hw(s, -1) : k == 2 ? 0.0 : k == 3 ? d.Hw(s, 1) : d.Outer(s, 1) + bst.wing;
                for (int i = 0; i + 1 < ss.Count; i++)
                {
                    double s0 = ss[i], s1 = ss[i + 1];
                    bool zone = s1 <= d.sA + 1e-6 || s0 >= d.length - d.sB - 1e-6;
                    if (!zone) continue;
                    for (int k = 0; k < 4; k++)
                    {
                        D3 V(double s, int j)
                        {
                            double u = U(s, j);
                            var p = d.frame.P(s, u);
                            double y = j == 2 ? d.frame.Y(s) : d.EdgeY(s, j > 2 ? 1 : -1);
                            return new D3(p.x, y, p.z);
                        }
                        bool pavement = k == 1 || k == 2;
                        string id = string.Format(Inv, "abutment:d{0:D2}:e{1:D4}:{2:D4}:{3}", d.id, d.edge, i, k);
                        Piece(plates, id, PlateKind.Road, pavement ? (byte)4 : Concrete, true, PriorityAbutment, 2f,
                            new[] { V(s0, k), V(s1, k), V(s1, k + 1), V(s0, k + 1) }, st.sinkAsphalt);
                    }
                }
            }
        }

        public static int Submit(IEnumerable<Plate> plates, IPlateSink sink)
        {
            int n = 0;
            foreach (var p in plates) { sink.Submit(p); n++; }
            return n;
        }

        static bool OnStructure(RoadGraph g, int edge)
        {
            var road = g.RoadOf(g.edges[edge].sourceRoad);
            return road != null && (road.bridge || road.tunnel);
        }

        // ------------------------------------------------------------------ asphalt

        static void Ribbons(RoadGraph g, RoadSurfaces surf, KerbSettings st, List<Plate> plates)
        {
            foreach (var r in surf.ribbons)
            {
                if (OnStructure(g, r.edge)) continue;
                var e = g.edges[r.edge];
                var road = g.RoadOf(e.sourceRoad);
                byte own = KerbRules.Surface(e.cls, road?.surface);
                int P = r.profile.Length;
                for (int q = 0; q + 1 < r.stations.Count; q++)
                {
                    // roads-R4: an unpaved road's paved apron at a junction is asphalt like the junction
                    bool apron = !KerbRules.Paved(own) && InApron(r, r.stationS[q], r.stationS[q + 1]);
                    byte surface = apron ? (byte)4 : own;
                    bool paved = KerbRules.Paved(surface);
                    double sink = paved ? st.sinkAsphalt : 0;
                    for (int k = 0; k + 1 < P; k++)
                    {
                        var A = r.stations[q][k];
                        var B = r.stations[q + 1][k];
                        var C = r.stations[q + 1][k + 1];
                        var D = r.stations[q][k + 1];
                        string id = string.Format(Inv, "road:e{0:D4}:{1:D4}:{2}", r.edge, q, k);
                        Piece(plates, id, PlateKind.Road, surface, paved, PriorityRibbon, 2f, new[] { A, B, C, D }, sink);
                    }
                }
            }
        }

        /// <summary>The stretch [a, b] of a ribbon lies within one of its paved aprons (roads-R4).</summary>
        public static bool InApron(RibbonSurface r, double a, double b) =>
            (r.apronA > 0 && b <= r.s0 + r.apronA + 1e-6) || (r.apronB > 0 && a >= r.s1 - r.apronB - 1e-6);

        static void Junctions(RoadGraph g, RoadSurfaces surf, KerbSettings st, List<Plate> plates)
        {
            foreach (var ns in surf.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback || ns.triangles.Count < 3) continue;
                bool allStructure = true;
                ArmSurface main = null;
                foreach (var a in ns.arms)
                {
                    if (!OnStructure(g, a.edge)) allStructure = false;
                    if (main == null || RoadClasses.Rank(a.cls) > RoadClasses.Rank(main.cls)) main = a;
                }
                if (allStructure) continue;
                var road = g.RoadOf(g.edges[main.edge].sourceRoad);
                byte surface = KerbRules.Surface(main.cls, road?.surface);
                bool paved = KerbRules.Paved(surface);
                double sink = paved ? st.sinkAsphalt : 0;
                for (int k = 0; k + 2 < ns.triangles.Count; k += 3)
                {
                    // R1 stores triangles facing up (clockwise from above); the plate takes them counter-clockwise
                    var a = ns.vertices[ns.triangles[k]];
                    var b = ns.vertices[ns.triangles[k + 2]];
                    var c = ns.vertices[ns.triangles[k + 1]];
                    Piece(plates, string.Format(Inv, "road:n{0:D4}:{1:D5}", ns.node, k / 3), PlateKind.Road, surface, paved, PriorityJunction, 2f, new[] { a, b, c }, sink);
                }
            }
        }

        // ------------------------------------------------------------------ walks, verges, shoulders

        /// <summary>The kerbside partition (KerbPieceBuilder): one plate per piece, at the piece's ground heights.</summary>
        static void Pieces(KerbNetwork kn, IHeightField ground, List<Plate> plates)
        {
            foreach (var p in kn.pieces)
            {
                var v = new D3[p.poly.Length];
                for (int k = 0; k < v.Length; k++) v[k] = new D3(p.poly[k].x, p.groundY[k], p.poly[k].z);
                string id = string.Format(Inv, "{0}:f{1:D5}:{2:D4}:{3}", p.kind == KerbPieceKind.Walk ? "walk" : p.kind == KerbPieceKind.Verge ? "verge" : "shoulder", p.face, p.seg, p.part);
                switch (p.kind)
                {
                    case KerbPieceKind.Walk: Piece(plates, id, PlateKind.Walk, Concrete, true, PriorityWalk, 2f, v, 0); break;
                    case KerbPieceKind.Verge: Piece(plates, id, PlateKind.Walk, Grass, false, PriorityVerge, Blend(ground, v), v, 0); break;
                    default: Piece(plates, id, PlateKind.Road, Gravel, false, PriorityVerge, Blend(ground, v), v, 0); break;
                }
            }
        }

        // ------------------------------------------------------------------ driveway aprons (after Resolve)

        static void Aprons(KerbNetwork kn, IReadOnlyList<DrivewayCut> cuts, DrivewayCutter cutter, KerbSettings st, List<Plate> plates)
        {
            foreach (var span in CutSpans(kn, cuts, cutter))
            {
                var f = span.face;
                var c = new { span.s0, span.s1 };
                float flare = (float)span.flare;
                var surface = span.surface;
                double a = c.s0 - flare, b = c.s1 + flare;
                string key = string.Format(Inv, "apron:f{0:D5}:{1:F2}", f.id, c.s0);
                int piece = 0;
                // kerbed: pieces between every bend of the profile (throat and flare ends, stations, where the kerb already
                // there crosses it), so the ground follows the lowered walk exactly; unkerbed: between stations
                var bends = f.kerbed ? CutBreaks(f, c.s0, c.s1, flare, st, true) : null;
                int count = f.kerbed ? bends.Count - 1 : f.Count - 1;
                for (int q = 0; q < count; q++)
                {
                    int i;
                    double sa, sb;
                    if (f.kerbed) { sa = bends[q]; sb = bends[q + 1]; i = Seg(f.s, 0.5 * (sa + sb), out _); }
                    else { i = q; sa = Math.Max(a, f.s[i]); sb = Math.Min(b, f.s[i + 1]); }
                    if (sb <= sa + 1e-6) continue;
                    var k0 = At(f, sa);
                    var k1 = At(f, sb);
                    var n = (f.kerb[i + 1] - f.kerb[i]).Normalized.Left;
                    double g0 = Interp(f.s, f.gutter, sa), g1 = Interp(f.s, f.gutter, sb);
                    if (f.kerbed)
                    {
                        double w0 = Interp(f.s, f.width, sa), w1 = Interp(f.s, f.width, sb);
                        double h0 = CutKerbHeight(f, sa, c.s0, c.s1, flare, st), h1 = CutKerbHeight(f, sb, c.s0, c.s1, flare, st);
                        double y0 = g0 + h0 + st.walkCrossSlope * Math.Max(0, w0 - st.kerbTop), y1 = g1 + h1 + st.walkCrossSlope * Math.Max(0, w1 - st.kerbTop);
                        Piece(plates, string.Format(Inv, "{0}:{1:D3}", key, piece++), PlateKind.Walk, Concrete, true, PriorityApron, 2f,
                            new[] { new D3(k0.x, g0 - st.sinkAsphalt, k0.z), new D3(k1.x, g1 - st.sinkAsphalt, k1.z), Lift(k1 + n * w1, y1 - st.sinkWalk), Lift(k0 + n * w0, y0 - st.sinkWalk) }, 0);
                    }
                    else
                    {
                        byte sf = KerbRules.Surface(surface);
                        bool paved = KerbRules.Paved(sf);
                        double sink = paved ? st.sinkWalk : 0;
                        Piece(plates, string.Format(Inv, "{0}:{1:D3}", key, piece++), PlateKind.Road, sf, paved, PriorityApron, 2f,
                            new[] { new D3(k0.x, g0 - sink, k0.z), new D3(k1.x, g1 - sink, k1.z), Lift(k1 + n * st.shoulder, g1 - sink), Lift(k0 + n * st.shoulder, g0 - sink) }, 0);
                    }
                }
            }
        }

        /// <summary>One resolved cut on one face (s along that face): its throat, flare and apron surface.</summary>
        public struct CutSpan
        {
            public KerbFace face;
            public double s0, s1, flare;
            public SiteSurface surface;
            public DrivewayCut cut;
            /// <summary>The cut's reach carried on from the face it was cut in onto the next kerbed face of the loop.</summary>
            public bool carried;
        }

        /// <summary>
        /// The cuts by face, one per baked group (merged requests share theirs; rejected ones are left out). A kerbed cut
        /// whose throat or flare reaches past its face's end carries on along the next kerbed face of the block loop (the
        /// two faces meet there without a corner), so the walk comes down on both sides of the face boundary.
        /// </summary>
        public static List<CutSpan> CutSpans(KerbNetwork kn, IReadOnlyList<DrivewayCut> cuts, DrivewayCutter cutter)
        {
            var o = new List<CutSpan>();
            if (cuts == null || cutter == null) return o;
            var done = new HashSet<(int, float, float)>();
            foreach (var c in cuts)
            {
                if (c == null || c.outcome == CutOutcome.Rejected) continue;
                if (!done.Add((c.faceId, c.s0, c.s1))) continue;
                var f = kn.Face(c.faceId);
                if (f == null || f.bridge || !cutter.CutShape(c.requestId, out float flare, out _, out var surface)) continue;
                o.Add(new CutSpan { face = f, s0 = c.s0, s1 = c.s1, flare = flare, surface = surface, cut = c });
                if (!f.kerbed) continue;
                if (c.s0 - flare < -1e-6 && Neighbour(kn, f, -1) is KerbFace p)
                    o.Add(new CutSpan { face = p, s0 = c.s0 + p.Length, s1 = c.s1 + p.Length, flare = flare, surface = surface, cut = c, carried = true });
                if (c.s1 + flare > f.Length + 1e-6 && Neighbour(kn, f, 1) is KerbFace q)
                    o.Add(new CutSpan { face = q, s0 = c.s0 - f.Length, s1 = c.s1 - f.Length, flare = flare, surface = surface, cut = c, carried = true });
            }
            return o;
        }

        /// <summary>The kerbed face (not on a bridge) that ends where f starts (dir -1) or starts where f ends (dir +1) on its loop, or null.</summary>
        static KerbFace Neighbour(KerbNetwork kn, KerbFace f, int dir)
        {
            var loop = kn.loops[f.loop];
            double L = loop.Length;
            bool Same(double x, double y) { double d = Math.Abs(x - y); return d < 1e-6 || Math.Abs(d - L) < 1e-6; }
            foreach (int id in loop.faces)
            {
                var g = kn.faces[id];
                if (g.id == f.id || !g.kerbed || g.bridge) continue;
                if (dir < 0 ? Same(g.t1, f.t0) : Same(g.t0, f.t1)) return g;
            }
            return null;
        }

        /// <summary>
        /// The kerb face height along a cut on a kerbed face as the meshes draw it: the cut's Dropped profile, or the kerb
        /// already there (the face's stations, linear between) where that is lower (a corner ramp, a taper).
        /// </summary>
        public static double CutKerbHeight(KerbFace f, double s, double s0, double s1, double flare, KerbSettings st) =>
            Math.Min(OldKerbHeight(f, s), Dropped(s, s0, s1, flare, st));

        static double OldKerbHeight(KerbFace f, double s)
        {
            int i = Seg(f.s, s, out double t);
            double h0 = f.top[i] - f.gutter[i], h1 = f.top[i + 1] - f.gutter[i + 1];
            return h0 + (h1 - h0) * t;
        }

        /// <summary>
        /// Where a cut's kerb profile bends on a face, sorted, within the face and the cut's reach: the flare and throat ends,
        /// where the kerb already there crosses the profile, and (stations true) the face's stations between them.
        /// </summary>
        public static List<double> CutBreaks(KerbFace f, double s0, double s1, double flare, KerbSettings st, bool stations)
        {
            double a = Math.Max(0, s0 - flare), b = Math.Min(f.Length, s1 + flare);
            var o = new List<double> { a, b };
            foreach (var x in new[] { s0, s1 }) if (x > a && x < b) o.Add(x);
            var inner = new List<double>();
            for (int i = 0; i < f.Count; i++) if (f.s[i] > a && f.s[i] < b) inner.Add(f.s[i]);
            var all = new List<double>(o);
            all.AddRange(inner);
            all.Sort();
            // both the old height (linear between stations) and the profile (linear between its ends) are linear here
            for (int k = 0; k + 1 < all.Count; k++)
            {
                double p = all[k], q = all[k + 1];
                if (q - p < 1e-6) continue;
                double dp = OldKerbHeight(f, p) - Dropped(p, s0, s1, flare, st), dq = OldKerbHeight(f, q) - Dropped(q, s0, s1, flare, st);
                if ((dp > 1e-9 && dq < -1e-9) || (dp < -1e-9 && dq > 1e-9)) o.Add(p + (q - p) * dp / (dp - dq));
            }
            if (stations) o.AddRange(inner);
            o.Sort();
            var u = new List<double>();
            foreach (var x in o) if (u.Count == 0 || x - u[u.Count - 1] > 1e-6) u.Add(x);
            return u;
        }

        /// <summary>Kerb face height along a cut: dropped over the throat, linear back to the barrier over each flare.</summary>
        public static double Dropped(double s, double s0, double s1, double flare, KerbSettings st)
        {
            double d = s < s0 ? s0 - s : s > s1 ? s - s1 : 0;
            if (flare <= 1e-6) return d > 0 ? st.barrier : st.dropped;
            return d >= flare ? st.barrier : st.dropped + (st.barrier - st.dropped) * d / flare;
        }

        static D2 At(KerbFace f, double s)
        {
            int i = Seg(f.s, s, out double t);
            return f.kerb[i] + (f.kerb[i + 1] - f.kerb[i]) * t;
        }

        static double Interp(List<double> s, List<double> v, double q)
        {
            int i = Seg(s, q, out double t);
            return v[i] + (v[i + 1] - v[i]) * t;
        }

        static int Seg(List<double> s, double q, out double t)
        {
            int n = s.Count;
            if (q <= s[0]) { t = 0; return 0; }
            if (q >= s[n - 1]) { t = 1; return n - 2; }
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (s[m] <= q) lo = m; else hi = m; }
            double l = s[hi] - s[lo];
            t = l > 0 ? (q - s[lo]) / l : 0;
            return lo;
        }

        // ------------------------------------------------------------------ pieces

        static D3 Lift(D2 p, double y) => new D3(p.x, y, p.z);

        /// <summary>05 §5.10: side slopes at most 1V:3H, so the blend is 3 x the height step to the ground, 2-12 m.</summary>
        static float Blend(IHeightField ground, params D3[] outer)
        {
            if (ground == null) return 2f;
            double dh = 0;
            foreach (var p in outer) dh = Math.Max(dh, Math.Abs(p.y - ground.Sample(p.x, p.z)));
            return (float)Math.Max(2, Math.Min(12, 3 * dh));
        }

        /// <summary>
        /// One convex piece with per-vertex targets (y - sink). A quad that is not convex (a pinched inner curve) is split
        /// into its two triangles; slivers under 1 cm² are dropped. Counter-clockwise in plan.
        /// </summary>
        static void Piece(List<Plate> plates, string id, PlateKind kind, byte surface, bool noDeform, int priority, float blend, D3[] v, double sink)
        {
            if (v.Length == 4 && !Convex(v))
            {
                Piece(plates, id + "a", kind, surface, noDeform, priority, blend, new[] { v[0], v[1], v[2] }, sink);
                Piece(plates, id + "b", kind, surface, noDeform, priority, blend, new[] { v[0], v[2], v[3] }, sink);
                return;
            }
            double area = 0;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i++) area += v[j].x * v[i].z - v[i].x * v[j].z;
            area *= 0.5;
            if (Math.Abs(area) < 1e-4) return;
            if (area < 0) Array.Reverse(v);
            var poly = new double2[v.Length];
            var ys = new float[v.Length];
            for (int i = 0; i < v.Length; i++) { poly[i] = new double2(v[i].x, v[i].z); ys[i] = (float)(v[i].y - sink); }
            plates.Add(new Plate
            {
                id = id, kind = kind, polygon = poly, surface = surface, heights = PlateHeights.PerVertex, vertexY = ys,
                maxSlope = (float)Math.Ceiling(MaxGrade(poly, ys) * 1000 + 1e-9) / 1000f, blend = blend, priority = priority, noDeform = noDeform,
            });
        }

        static bool Convex(D3[] v)
        {
            int sign = 0;
            for (int i = 0; i < v.Length; i++)
            {
                D3 a = v[i], b = v[(i + 1) % v.Length], c = v[(i + 2) % v.Length];
                double cr = (b.x - a.x) * (c.z - b.z) - (b.z - a.z) * (c.x - b.x);
                if (Math.Abs(cr) < 1e-12) continue;
                int s = cr > 0 ? 1 : -1;
                if (sign == 0) sign = s;
                else if (s != sign) return false;
            }
            return sign != 0;
        }

        /// <summary>The steepest grade of the piece's fan triangles (from vertex 0).</summary>
        static double MaxGrade(double2[] p, float[] y)
        {
            double best = 0;
            for (int k = 1; k + 1 < p.Length; k++) best = Math.Max(best, Grade(p[0], y[0], p[k], y[k], p[k + 1], y[k + 1]));
            return best;
        }

        static double Grade(double2 a, double ya, double2 b, double yb, double2 c, double yc)
        {
            double2 u = b - a, v = c - a;
            double det = u.x * v.y - u.y * v.x;
            if (Math.Abs(det) < 1e-12) return 0;
            double du = yb - ya, dv = yc - ya;
            double gx = (du * v.y - dv * u.y) / det, gz = (dv * u.x - du * v.x) / det;
            return Math.Sqrt(gx * gx + gz * gz);
        }
    }

    /// <summary>
    /// A reference evaluation of plates (the tests' stand-in for lane W's applier): at a plan point the plate that wins by
    /// ADR-007's order (stage, priority, id) among those containing it, and its target height by the fan triangulation
    /// from vertex 0 (exact for the convex pieces R2 submits). Points outside every plate have no target.
    /// </summary>
    public sealed class PlateField
    {
        readonly List<Plate> m_Plates;
        readonly double m_Cell;
        readonly Dictionary<(int, int), List<int>> m_Cells = new Dictionary<(int, int), List<int>>();

        public PlateField(IEnumerable<Plate> plates, double cell = 4.0)
        {
            m_Plates = new List<Plate>(plates);
            m_Plates.Sort(PlateOrder.Comparison);
            m_Cell = cell;
            for (int i = 0; i < m_Plates.Count; i++)
            {
                var p = m_Plates[i].polygon;
                double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
                foreach (var q in p) { x0 = Math.Min(x0, q.x); x1 = Math.Max(x1, q.x); z0 = Math.Min(z0, q.y); z1 = Math.Max(z1, q.y); }
                for (int x = C(x0); x <= C(x1); x++)
                    for (int z = C(z0); z <= C(z1); z++)
                    {
                        if (!m_Cells.TryGetValue((x, z), out var l)) m_Cells[(x, z)] = l = new List<int>();
                        l.Add(i);
                    }
            }
        }

        int C(double v) => (int)Math.Floor(v / m_Cell);

        public int Count => m_Plates.Count;

        public bool Sample(D2 q, out double y, out Plate winner)
        {
            y = double.NaN;
            winner = null;
            if (!m_Cells.TryGetValue((C(q.x), C(q.z)), out var l)) return false;
            for (int k = l.Count - 1; k >= 0; k--)
            {
                var pl = m_Plates[l[k]];
                if (Interpolate(pl, new double2(q.x, q.z), out y)) { winner = pl; return true; }
            }
            return false;
        }

        static bool Interpolate(Plate pl, double2 q, out double y)
        {
            y = double.NaN;
            var p = pl.polygon;
            for (int k = 1; k + 1 < p.Length; k++)
            {
                double2 a = p[0], b = p[k], c = p[k + 1];
                double2 v0 = b - a, v1 = c - a, v2 = q - a;
                double den = v0.x * v1.y - v1.x * v0.y;
                if (Math.Abs(den) < 1e-14) continue;
                double u = (v2.x * v1.y - v1.x * v2.y) / den, v = (v0.x * v2.y - v2.x * v0.y) / den;
                const double eps = -1e-9;
                if (u < eps || v < eps || u + v > 1 - eps) continue;
                double ya = pl.heights == PlateHeights.PerVertex ? pl.vertexY[0] : Plane(pl, a);
                double yb = pl.heights == PlateHeights.PerVertex ? pl.vertexY[k] : Plane(pl, b);
                double yc = pl.heights == PlateHeights.PerVertex ? pl.vertexY[k + 1] : Plane(pl, c);
                y = ya + u * (yb - ya) + v * (yc - ya);
                return true;
            }
            return false;
        }

        static double Plane(Plate pl, double2 p) => pl.plane.x * p.x + pl.plane.y * p.y + pl.plane.z;
    }
}
