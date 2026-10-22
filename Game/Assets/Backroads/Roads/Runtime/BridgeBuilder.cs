using System;
using System.Collections.Generic;
using System.Globalization;

namespace Backroads.Roads
{
    /// <summary>The roads-R4 bridge numbers (card step 2; our own deck, parapet, pier and abutment meshes).</summary>
    public sealed class BridgeSettings
    {
        /// <summary>Deck structure depth below the pavement edge (slab and girders), and the ground's clearance under it that makes a free span.</summary>
        public double depth = 1.0, clearance = 0.5;
        /// <summary>A concrete strip beside the pavement, then a solid concrete parapet (TL-4 height) on the deck edge.</summary>
        public double edgeStrip = 0.1, parapetWidth = 0.35, parapetHeight = 0.85;
        /// <summary>The abutment: at least this long under each deck end; its wing walls stand this far beyond the deck's outer face.</summary>
        public double minSeat = 1.0, wing = 1.0;
        /// <summary>Spans at most this long between piers; wall piers this thick, inset from the deck faces, footings this deep.</summary>
        public double maxSpan = 45.0, pierThickness = 1.2, pierInset = 1.0, footing = 2.0;
        /// <summary>Ground sampling step along the deck; where the road's section changes on the deck, the width tapers over this either side (R1's through mouth).</summary>
        public double step = 0.5, widthTaper = 1.5;
    }

    /// <summary>
    /// One bridge deck (roads-R4 step 2): the chain of graph edges on a bridge=* road between its two ends (a zone or class
    /// change on the bridge splits the road, not the bridge). The deck is the road's own ribbons (R1, straight between the
    /// abutment heights as R0o made them) plus the structure: a concrete edge strip and parapet each side, the slab
    /// underneath, wall piers across the free span and an abutment at each end. Under the abutments the ground is held at
    /// road level by the abutment plates (the wing walls retain it); everywhere else under the deck the DEM is untouched.
    /// Lane W reads <see cref="outline"/> to drop the ground's collision where the river runs under.
    /// </summary>
    public sealed class BridgeDeck
    {
        public sealed class Piece
        {
            public int edge;
            public bool reversed, kerbed;
            public double s0, s1, hwL, hwR;
        }

        public int id, edge, sourceRoad;
        public string osmWay = "", over = "";
        public double length;
        /// <summary>The edges in order along the deck, each with its range of the deck's arc length and its pavement half widths in the deck's direction.</summary>
        public readonly List<Piece> pieces = new List<Piece>();
        /// <summary>The widest pavement half widths and outer faces (pavement edge + strip + parapet), left (+u) and right.</summary>
        public double hwL, hwR, outerL, outerR;
        public double edgeStrip = 0.1, parapetWidth = 0.35, widthTaper = 1.5;
        /// <summary>Abutment zones: the first sA and the last sB metres of the deck are on fill; the free span lies between.</summary>
        public double sA, sB;
        /// <summary>Pier stations (arc length along the deck).</summary>
        public readonly List<double> piers = new List<double>();
        /// <summary>The deck footprint (outer faces, the whole deck), CCW, and the pavement height range.</summary>
        public D2[] outline = Array.Empty<D2>();
        public double yMin, yMax;
        /// <summary>The smallest soffit-to-ground clearance over the free span (NaN without a DEM), and where.</summary>
        public double clearanceMin = double.NaN, clearanceAt;
        public readonly List<string> notes = new List<string>();
        public RoadFrame frame;

        public double FreeSpan => Math.Max(0, length - sA - sB);

        /// <summary>The pavement half width at s on the left (+1) or right (-1), tapering linearly across a piece boundary.</summary>
        public double Hw(double s, int side)
        {
            double W(Piece p) => side > 0 ? p.hwL : p.hwR;
            for (int i = 0; i < pieces.Count; i++)
            {
                var p = pieces[i];
                if (s > p.s1 + 1e-9 && i + 1 < pieces.Count) continue;
                if (i > 0 && s < p.s0 + widthTaper) { double t = Math.Max(0, (s - (p.s0 - widthTaper)) / (2 * widthTaper)); return W(pieces[i - 1]) + (W(p) - W(pieces[i - 1])) * t; }
                if (i + 1 < pieces.Count && s > p.s1 - widthTaper) { double t = Math.Min(1, (s - (p.s1 - widthTaper)) / (2 * widthTaper)); return W(p) + (W(pieces[i + 1]) - W(p)) * t; }
                return W(p);
            }
            return pieces.Count > 0 ? W(pieces[pieces.Count - 1]) : 0;
        }

        public double Outer(double s, int side) => Hw(s, side) + edgeStrip + parapetWidth;

        bool KerbedAt(double s)
        {
            foreach (var p in pieces) if (s <= p.s1 + 1e-9) return p.kerbed;
            return false;
        }

        /// <summary>The pavement edge height at s on the left (+1) or right (-1) side (R1's crown and gutter pan, so the parapet sits on the ribbon's edge).</summary>
        public double EdgeY(double s, int side)
        {
            double l = Hw(s, 1), r = Hw(s, -1);
            return frame.Y(s) + SurfaceBuilder.Crown(side > 0 ? l : -r, l, r, KerbedAt(s), false, Surf);
        }

        static readonly SurfaceSettings Surf = new SurfaceSettings();
    }

    public static class BridgeBuilder
    {
        public const int Version = 1;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static bool OnBridge(RoadGraph g, GraphEdge e) => g.RoadOf(e.sourceRoad)?.bridge == true && e.pts != null && e.pts.Length >= 2;

        /// <summary>
        /// The decks: chains of bridge edges joined at two-arm nodes, each from its end with the smaller (x, z) to the
        /// other. over maps an OSM way to what it crosses (roads.json bridgeSpans).
        /// </summary>
        public static List<BridgeDeck> Build(RoadGraph g, IHeightField dem, BridgeSettings st = null, IReadOnlyDictionary<string, string> over = null)
        {
            st = st ?? new BridgeSettings();
            var res = new List<BridgeDeck>();
            var used = new HashSet<int>();
            // the bridge edge leaving node n other than `from`, when n joins exactly two bridge edges
            int Next(int node, int from)
            {
                var n = g.nodes[node];
                if (n.Degree != 2) return -1;
                foreach (var id in n.arms)
                {
                    var a = g.arms[id];
                    if (a.edge != from && OnBridge(g, g.edges[a.edge])) return a.edge;
                }
                return -1;
            }
            foreach (var e0 in g.edges)
            {
                if (used.Contains(e0.id) || !OnBridge(g, e0)) continue;
                // walk to one end of the chain, then collect it from there
                int cur = e0.id, node = e0.nodeA, guard = 0;
                while (guard++ < 64)
                {
                    int prev = Next(node, cur);
                    if (prev < 0 || prev == e0.id) break;
                    var pe = g.edges[prev];
                    node = pe.nodeA == node ? pe.nodeB : pe.nodeA;
                    cur = prev;
                }
                var chain = new List<(int edge, bool reversed)>();
                int startNode = node;
                guard = 0;
                while (cur >= 0 && guard++ < 64 && !used.Contains(cur))
                {
                    var e = g.edges[cur];
                    bool rev = e.nodeB == node && e.nodeA != node;
                    chain.Add((cur, rev));
                    used.Add(cur);
                    node = rev ? e.nodeA : e.nodeB;
                    cur = Next(node, cur);
                }
                // a stable direction: from the end with the smaller (x, z)
                D2 pa = g.nodes[startNode].p.XZ, pb = g.nodes[node].p.XZ;
                if (pb.x < pa.x - 1e-9 || (Math.Abs(pb.x - pa.x) <= 1e-9 && pb.z < pa.z))
                {
                    chain.Reverse();
                    for (int i = 0; i < chain.Count; i++) chain[i] = (chain[i].edge, !chain[i].reversed);
                }
                res.Add(Deck(g, chain, res.Count, dem, st, over));
            }
            return res;
        }

        static BridgeDeck Deck(RoadGraph g, List<(int edge, bool reversed)> chain, int id, IHeightField dem, BridgeSettings st, IReadOnlyDictionary<string, string> over)
        {
            var d = new BridgeDeck { id = id, edge = chain[0].edge, sourceRoad = g.edges[chain[0].edge].sourceRoad, edgeStrip = st.edgeStrip, parapetWidth = st.parapetWidth, widthTaper = st.widthTaper };
            var pts = new List<D3>();
            double s = 0;
            foreach (var (eid, rev) in chain)
            {
                var e = g.edges[eid];
                var road = g.RoadOf(e.sourceRoad);
                double l = road?.xs?.hwLeft ?? e.hw, r = road?.xs?.hwRight ?? e.hw;
                var ep = new List<D3>(e.pts);
                if (rev) ep.Reverse();
                for (int i = 0; i < ep.Count; i++) if (pts.Count == 0 || i > 0) pts.Add(ep[i]);
                d.pieces.Add(new BridgeDeck.Piece { edge = eid, reversed = rev, kerbed = KerbRules.Kerbed(road, e.cls), s0 = s, s1 = s + e.Length, hwL = rev ? r : l, hwR = rev ? l : r });
                s += e.Length;
                foreach (var w in road?.osmWays ?? Array.Empty<string>())
                {
                    if (d.osmWay.Length == 0) d.osmWay = w;
                    if (d.over.Length == 0 && over != null && over.TryGetValue(w, out var o) && !string.IsNullOrEmpty(o)) { d.over = o; d.osmWay = w; }
                }
            }
            d.frame = new RoadFrame(pts);
            d.length = d.frame.Length;
            // the pieces' ranges on the joined frame (the arc lengths agree up to rounding)
            double scale = s > 0 ? d.length / s : 1;
            foreach (var p in d.pieces) { p.s0 *= scale; p.s1 *= scale; }
            foreach (var p in d.pieces) { d.hwL = Math.Max(d.hwL, p.hwL); d.hwR = Math.Max(d.hwR, p.hwR); }
            d.outerL = d.hwL + st.edgeStrip + st.parapetWidth;
            d.outerR = d.hwR + st.edgeStrip + st.parapetWidth;
            if (chain.Count > 1) d.notes.Add(string.Format(Inv, "{0} edges ({1}) joined into one deck", chain.Count, string.Join(" ", chain.ConvertAll(c => c.edge.ToString(Inv)))));
            Spans(d, dem, st);
            Outline(d);
            return d;
        }

        /// <summary>The ground under the deck (the highest of the centre and both outer faces) at s.</summary>
        public static double Ground(BridgeDeck d, IHeightField dem, double s)
        {
            double y = double.NegativeInfinity;
            foreach (double u in new[] { -d.Outer(s, -1), 0.0, d.Outer(s, 1) })
            {
                var p = d.frame.P(s, u);
                y = Math.Max(y, dem.Sample(p.x, p.z));
            }
            return y;
        }

        /// <summary>The soffit (the deck's underside) at s: the lower pavement edge less the structure depth.</summary>
        public static double Soffit(BridgeDeck d, double s, BridgeSettings st) => Math.Min(d.EdgeY(s, 1), d.EdgeY(s, -1)) - st.depth;

        static void Spans(BridgeDeck d, IHeightField dem, BridgeSettings st)
        {
            double L = d.length;
            d.yMin = double.MaxValue; d.yMax = double.MinValue;
            for (double s = 0; ; s = Math.Min(L, s + st.step))
            {
                double y = d.frame.Y(s);
                d.yMin = Math.Min(d.yMin, y); d.yMax = Math.Max(d.yMax, y);
                if (s >= L) break;
            }
            if (dem == null)
            {
                d.sA = d.sB = Math.Min(st.minSeat, 0.25 * L);
                d.notes.Add("no DEM: abutments at the minimum seat, no clearance check");
            }
            else
            {
                // each abutment ends where the ground first falls clear of the soffit
                bool Clear(double s) => Soffit(d, s, st) - Ground(d, dem, s) >= st.clearance;
                double a = st.minSeat;
                while (a < L && !Clear(a)) a += st.step;
                double b = st.minSeat;
                while (b < L && !Clear(L - b)) b += st.step;
                if (a + b >= L - st.minSeat)
                {
                    d.sA = d.sB = 0.5 * L;
                    d.notes.Add(string.Format(Inv, "the ground stays within {0:F1} m of the soffit along the whole deck: on fill, no free span", st.clearance));
                }
                else
                {
                    d.sA = a; d.sB = b;
                    double worst = double.MaxValue, at = 0;
                    for (double s = a; s <= L - b + 1e-9; s += st.step)
                    {
                        double c = Soffit(d, s, st) - Ground(d, dem, s);
                        if (c < worst) { worst = c; at = s; }
                    }
                    d.clearanceMin = worst; d.clearanceAt = at;
                    if (worst < st.clearance)
                        d.notes.Add(string.Format(Inv, "the ground rises to {0:F2} m under the soffit at s {1:F1} m inside the free span", worst, at));
                }
            }
            double F = d.FreeSpan;
            if (F <= 0) return;
            int n = Math.Max(1, (int)Math.Ceiling(F / st.maxSpan - 1e-9));
            for (int i = 1; i < n; i++) d.piers.Add(d.sA + F * i / n);
        }

        static void Outline(BridgeDeck d)
        {
            var ss = Stations(d, 4.0);
            var poly = new List<D2>();
            foreach (var s in ss) poly.Add(d.frame.P(s, d.Outer(s, 1)));
            for (int i = ss.Count - 1; i >= 0; i--) poly.Add(d.frame.P(ss[i], -d.Outer(ss[i], -1)));
            if (Geom2.SignedArea(poly) < 0) poly.Reverse();
            d.outline = poly.ToArray();
        }

        /// <summary>
        /// Stations along the deck for the meshes: the frame's vertices plus at most `step` apart, the width tapers at the
        /// piece boundaries, and the given extra stations.
        /// </summary>
        public static List<double> Stations(BridgeDeck d, double step, params double[] extra)
        {
            var l = new List<double>();
            var f = d.frame;
            for (int i = 0; i + 1 < f.Count; i++)
            {
                double a = f.s[i], b = f.s[i + 1];
                int n = Math.Max(1, (int)Math.Ceiling((b - a) / step - 1e-9));
                for (int k = 0; k < n; k++) l.Add(a + (b - a) * k / n);
            }
            l.Add(f.Length);
            for (int i = 1; i < d.pieces.Count; i++) { l.Add(d.pieces[i].s0 - d.widthTaper); l.Add(d.pieces[i].s0 + d.widthTaper); }
            foreach (var x in extra) l.Add(x);
            l.RemoveAll(x => x < 0 || x > f.Length);
            l.Sort();
            var res = new List<double>();
            foreach (var x in l) if (res.Count == 0 || x - res[res.Count - 1] > 1e-6) res.Add(x);
            return res;
        }

        public static string Describe(BridgeDeck d)
        {
            string piers = d.piers.Count == 0 ? "no piers" : d.piers.Count + " piers at " + string.Join(", ", d.piers.ConvertAll(p => p.ToString("F1", Inv))) + " m";
            return string.Format(Inv, "deck {0} edges {1} (R{2}, {3}) over {4}: {5:F1} m, pavement {6:F2}-{7:F2} m, deck up to {8:F2} m wide; abutments {9:F1} m and {10:F1} m on fill, free span {11:F1} m, {12}; least clearance under the soffit {13}",
                d.id, string.Join(" ", d.pieces.ConvertAll(p => p.edge.ToString(Inv))), d.sourceRoad, d.osmWay, d.over.Length > 0 ? d.over : "(not given)", d.length, d.yMin, d.yMax, d.outerL + d.outerR,
                d.sA, d.sB, d.FreeSpan, piers, double.IsNaN(d.clearanceMin) ? "n/a" : string.Format(Inv, "{0:F2} m at s {1:F1}", d.clearanceMin, d.clearanceAt));
        }
    }
}
