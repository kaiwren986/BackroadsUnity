using System;
using System.Collections.Generic;
using System.Globalization;

namespace Backroads.Roads
{
    /// <summary>The roads-R4 roundabout numbers (05 §5.11: a compact single-lane roundabout; mini-roundabouts per FHWA).</summary>
    public sealed class RoundaboutSettings
    {
        /// <summary>Circulatory roadway width, truck apron width; a mapped ring under miniIcd is a mini-roundabout (card step 1).</summary>
        public double circW = 5.5, apronW = 3.0, miniIcd = 26.0;
        /// <summary>Mini-roundabouts: the diameter when the node has none mapped, the clamp for a mapped one, the circulatory width.</summary>
        public double miniDefault = 18.0, miniMin = 14.0, miniMax = 27.0, miniCircW = 4.5;
        /// <summary>Raised splitter islands: width at the yield line, length, nose half width; the legs flare by half the width each side.</summary>
        public double splitterWidth = 2.5, splitterLength = 15.0, nose = 0.3, flareTaper = 8.0;
        /// <summary>Entry and exit kerb radii (mini: smaller); the pedestrian cut-through through the splitter.</summary>
        public double rEntry = 15.0, rExit = 20.0, rMiniEntry = 5.0, rMiniExit = 6.0, cutThrough = 1.8;
        /// <summary>Crosswalks one car length behind the yield line, this wide (two 0.3 m lines).</summary>
        public double crosswalkBack = 6.0, crosswalkWidth = 3.0;
        /// <summary>The circulatory roadway falls outward at this slope (away from the island).</summary>
        public double outwardSlope = 0.02;
        /// <summary>Kerb heights: the central island and splitters (barrier), the truck apron (mountable Apron75), a mini's dome.</summary>
        public double islandKerb = 0.15, apronKerb = 0.075, miniDome = 0.10;
        public double arcStepDeg = 5.0, mouthPad = 0.5;
    }

    /// <summary>One leg of a roundabout in its arm's frame (s outward from the centre, +u = left of outward = the entry side).</summary>
    public sealed class RoundaboutLeg
    {
        /// <summary>Position in the node's arm list.</summary>
        public int arm;
        public double hwL, hwR;
        /// <summary>The legs widen by `flare` each side up to sFlare, back to their own width at sMouth (room for the splitter).</summary>
        public double flare, sFlare, sMouth;
        /// <summary>Where the entry (+u) and exit (-u) kerbs leave the leg for their curves.</summary>
        public double tEntry, tExit;
        /// <summary>The yield line on the entry half, at the inscribed circle (s where the leg axis crosses it).</summary>
        public double sYield;
        /// <summary>Crosswalk band (or -1 when the leg has no walk).</summary>
        public double sCwInner = -1, sCwOuter = -1;
        /// <summary>The raised splitter island in one or two pieces (the cut-through), CCW plan polygons; none on a mini.</summary>
        public readonly List<D2[]> splitters = new List<D2[]>();
        public double splitterS0, splitterS1;
        public RoadFrame frame;

        /// <summary>The flared half width added at s (full to sFlare, linear to 0 at sMouth).</summary>
        public double FlareAt(double s) => flare <= 0 ? 0 : s <= sFlare ? flare : s >= sMouth ? 0 : flare * (sMouth - s) / Math.Max(1e-9, sMouth - sFlare);
        public double EdgeL(double s) => hwL + FlareAt(s);
        public double EdgeR(double s) => hwR + FlareAt(s);
    }

    /// <summary>A roundabout's plan geometry (roads-R4 step 1, 05 §5.11), built from the mapped ring and the graph node's legs.</summary>
    public sealed class RoundaboutGeometry
    {
        public int node, index;
        public string osm = "";
        public D2 c;
        public bool mini;
        /// <summary>Inscribed circle diameter, its radius, the island side of the circulatory roadway, the central island (apron's inner edge).</summary>
        public double icd, Ro, Ri, Ra;
        public double ringRadiusMapped;
        public readonly List<RoundaboutLeg> legs = new List<RoundaboutLeg>();
        /// <summary>Per gap between leg i and the next leg counter-clockwise: the outer kerb from leg i's entry tangent to leg i+1's exit tangent.</summary>
        public readonly List<List<D2>> gaps = new List<List<D2>>();
        public readonly List<double> gapEntryRadius = new List<double>(), gapExitRadius = new List<double>();
        public readonly List<string> notes = new List<string>();
        public double minLegGapDeg;
    }

    public static class RoundaboutBuilder
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>
        /// The geometry of one roundabout: dimensions from the mapped ring (its centreline is the circulatory roadway's
        /// middle) or the mini node's diameter, then per leg the flare and splitter, the entry and exit curves tangent to
        /// the inscribed circle, the mouth, the yield line and the crosswalk. maxMouth limits each leg (another node).
        /// </summary>
        public static RoundaboutGeometry Build(int node, int index, D2 c, RoadGraphInput.Roundabout rb, IReadOnlyList<(RoadFrame frame, double hwL, double hwR, double angle, double maxMouth, bool walk)> arms,
            RoundaboutSettings st = null)
        {
            st = st ?? new RoundaboutSettings();
            var G = new RoundaboutGeometry { node = node, index = index, osm = rb.osm, c = c, ringRadiusMapped = rb.ringRadius };
            double circW;
            if (rb.mini)
            {
                G.mini = true;
                G.icd = rb.diameter > 0 ? Math.Max(st.miniMin, Math.Min(st.miniMax, rb.diameter)) : st.miniDefault;
                circW = st.miniCircW;
            }
            else
            {
                circW = st.circW;
                G.icd = 2 * (rb.ringRadius + 0.5 * circW);
                if (G.icd < st.miniIcd) { G.mini = true; G.notes.Add(string.Format(Inv, "the mapped ring gives an ICD of {0:F1} m (< {1:F0} m): a mini-roundabout", G.icd, st.miniIcd)); circW = Math.Min(circW, Math.Max(st.miniCircW, 0.5 * G.icd - 1.5)); }
            }
            G.Ro = 0.5 * G.icd;
            G.Ri = Math.Max(1.0, G.Ro - circW);
            double apron = G.mini ? 0 : Math.Min(st.apronW, Math.Max(0, G.Ri - 2.0));
            if (!G.mini && apron < st.apronW) G.notes.Add(string.Format(Inv, "truck apron narrowed to {0:F1} m by the mapped ring", apron));
            G.Ra = G.Ri - apron;

            int n = arms.Count;
            G.minLegGapDeg = 360;
            for (int i = 0; i < n; i++)
            {
                double gap = Geo.WrapPositive(arms[(i + 1) % n].angle - arms[i].angle);
                if (n == 1) gap = 2 * Math.PI;
                G.minLegGapDeg = Math.Min(G.minLegGapDeg, gap / Geo.Deg);
            }
            foreach (var a in arms)
            {
                var L = new RoundaboutLeg { arm = G.legs.Count, hwL = a.hwL, hwR = a.hwR, frame = a.frame, sYield = G.Ro };
                L.flare = G.mini ? 0 : 0.5 * st.splitterWidth;
                G.legs.Add(L);
            }
            double rE = G.mini ? st.rMiniEntry : st.rEntry, rX = G.mini ? st.rMiniExit : st.rExit;

            // the outer kerb between each leg's entry side and the next leg's exit side
            var phiE = new double[n];
            var phiX = new double[n];
            var tE = new double[n];
            var tX = new double[n];
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                var I = G.legs[i];
                var J = G.legs[j];
                double span = n == 1 ? 2 * Math.PI : Geo.WrapPositive(arms[j].angle - arms[i].angle);
                double k = 1.0;
                double ri = rE, rj = rX, ti = 0, tj = 0, ai = 0, aj = 0;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    ri = rE * k; rj = rX * k;
                    double oi = I.hwL + I.flare, oj = J.hwR + J.flare;
                    ti = Math.Sqrt(Math.Max(0, (G.Ro + ri) * (G.Ro + ri) - (oi + ri) * (oi + ri)));
                    tj = Math.Sqrt(Math.Max(0, (G.Ro + rj) * (G.Ro + rj) - (oj + rj) * (oj + rj)));
                    ai = Math.Atan2(oi + ri, ti);
                    aj = Math.Atan2(oj + rj, tj);
                    if (ai + aj <= span - 1e-6) break;
                    k *= 0.75;
                }
                if (ai + aj > span - 1e-6)
                    G.notes.Add(string.Format(Inv, "legs {0} and {1} are only {2:F0} degrees apart: their kerb curves meet on the ring", i, j, span / Geo.Deg));
                else if (k < 1.0)
                    G.notes.Add(string.Format(Inv, "legs {0} and {1} ({2:F0} degrees apart): entry and exit radii cut to {3:F1} m and {4:F1} m", i, j, span / Geo.Deg, ri, rj));
                G.gapEntryRadius.Add(ri);
                G.gapExitRadius.Add(rj);
                tE[i] = ti; tX[j] = tj;
                phiE[i] = arms[i].angle + ai;
                phiX[j] = arms[i].angle + span - aj;
                // the polyline: entry fillet (leg i, +u), the ring arc, exit fillet (leg j, -u)
                var pts = new List<D2>();
                D2 qi = c + D2.FromAngle(arms[i].angle) * ti + D2.FromAngle(arms[i].angle).Left * (I.hwL + I.flare + ri);
                D2 qj = c + D2.FromAngle(arms[j].angle) * tj - D2.FromAngle(arms[j].angle).Left * (J.hwR + J.flare + rj);
                Arc(pts, qi, ri, Math.Atan2(-D2.FromAngle(arms[i].angle).Left.z, -D2.FromAngle(arms[i].angle).Left.x), Math.Atan2((c - qi).z, (c - qi).x), false, st.arcStepDeg);
                double a0 = phiE[i], a1 = phiX[j];
                if (a1 > a0) Arc(pts, c, G.Ro, a0, a1, true, st.arcStepDeg);
                Arc(pts, qj, rj, Math.Atan2((c - qj).z, (c - qj).x), Math.Atan2(D2.FromAngle(arms[j].angle).Left.z, D2.FromAngle(arms[j].angle).Left.x), false, st.arcStepDeg);
                G.gaps.Add(Dedupe(pts));
            }

            for (int i = 0; i < n; i++)
            {
                var L = G.legs[i];
                L.tEntry = tE[i];
                L.tExit = tX[i];
                double sT = Math.Max(L.tEntry, L.tExit);
                L.sFlare = sT + st.mouthPad;
                L.sMouth = L.flare > 0 ? L.sFlare + st.flareTaper : L.sFlare;
                // the crosswalk lies on the surface: the mouth moves out past it
                if (arms[i].walk) L.sMouth = Math.Max(L.sMouth, G.Ro + st.crosswalkBack + st.crosswalkWidth + st.mouthPad);
                double room = arms[i].maxMouth;
                if (L.sMouth > room)
                {
                    G.notes.Add(string.Format(Inv, "leg {0}: its mouth at {1:F1} m is beyond its room {2:F1} m; the flare is cut short", i, L.sMouth, room));
                    L.sMouth = Math.Max(sT + 0.1, room);
                    L.sFlare = Math.Min(L.sFlare, L.sMouth);
                }
                if (arms[i].walk)
                {
                    L.sCwInner = G.Ro + st.crosswalkBack;
                    L.sCwOuter = L.sCwInner + st.crosswalkWidth;
                    if (L.sCwOuter > L.sMouth - 0.5) { L.sCwInner = L.sCwOuter = -1; G.notes.Add(string.Format(Inv, "leg {0}: no room for its crosswalk before the mouth", i)); }
                }
                Splitter(G, L, st);
            }
            return G;
        }

        /// <summary>The raised splitter: a rounded nose at the yield line, sides at the flare's edges (so each lane keeps its width), a point near the mouth; cut through at the crosswalk.</summary>
        static void Splitter(RoundaboutGeometry G, RoundaboutLeg L, RoundaboutSettings st)
        {
            if (G.mini || L.flare <= st.nose) return;
            double s0 = G.Ro + 0.3, s1 = Math.Min(G.Ro + st.splitterLength, L.sMouth - 0.5);
            // the tip where the flare narrows to the nose
            if (L.sMouth > L.sFlare) s1 = Math.Min(s1, L.sMouth - (L.sMouth - L.sFlare) * st.nose / L.flare);
            if (s1 - s0 < 3.0) { G.notes.Add(string.Format(Inv, "leg {0}: no room for a raised splitter", L.arm)); return; }
            L.splitterS0 = s0; L.splitterS1 = s1;
            double Half(double s) => Math.Max(st.nose, Math.Min(L.flare, L.FlareAt(s)));
            var pieces = new List<(double a, double b)>();
            if (L.sCwInner > 0)
            {
                double mid = 0.5 * (L.sCwInner + L.sCwOuter), h = 0.5 * st.cutThrough;
                if (mid - h > s0 + 1.0) pieces.Add((s0, mid - h));
                if (s1 > mid + h + 1.0) pieces.Add((mid + h, s1));
            }
            else pieces.Add((s0, s1));
            foreach (var (a, b) in pieces)
            {
                var poly = new List<D2>();
                // right side outward, the far end, left side back, the near end (a semicircle at the ring end)
                // the rounded nose at the ring end: the sides start where its half circle ends
                bool roundNear = Math.Abs(a - s0) < 1e-9;
                double r = Half(a), start = roundNear ? a + r : a;
                var ss = new List<double> { start };
                for (double s = Math.Ceiling(start); s < b; s += 1.0) if (s > start + 1e-6) ss.Add(s);
                if (L.sFlare > start && L.sFlare < b) ss.Add(L.sFlare);
                ss.Add(b);
                ss.Sort();
                foreach (var s in ss) poly.Add(L.frame.P(s, -Half(s)));
                for (int q = ss.Count - 1; q >= 0; q--) poly.Add(L.frame.P(ss[q], Half(ss[q])));
                if (roundNear)
                {
                    var cc = L.frame.P(start, 0);
                    var dir = L.frame.Dir(a);
                    double ang = Math.Atan2(dir.z, dir.x);
                    // from +u round the near end to -u
                    for (int k = 0; k <= 8; k++)
                    {
                        double t = ang + Math.PI / 2 + Math.PI * k / 8;
                        poly.Add(cc + new D2(Math.Cos(t), Math.Sin(t)) * r);
                    }
                }
                if (Geom2.SignedArea(poly) < 0) poly.Reverse();
                L.splitters.Add(Dedupe(poly).ToArray());
            }
        }

        /// <summary>Points on a circle from angle a0 to a1 (counter-clockwise when ccw, else the short way), at most stepDeg apart, both ends included.</summary>
        static void Arc(List<D2> pts, D2 centre, double r, double a0, double a1, bool ccw, double stepDeg)
        {
            double d = a1 - a0;
            if (ccw) { while (d < 0) d += 2 * Math.PI; }
            else { while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; }
            int m = Math.Max(1, (int)Math.Ceiling(Math.Abs(d) / (stepDeg * Geo.Deg)));
            for (int k = 0; k <= m; k++)
            {
                double t = a0 + d * k / m;
                pts.Add(centre + new D2(Math.Cos(t), Math.Sin(t)) * r);
            }
        }

        static List<D2> Dedupe(List<D2> p)
        {
            var o = new List<D2>();
            foreach (var q in p) if (o.Count == 0 || D2.Distance(o[o.Count - 1], q) > 0.01) o.Add(q);
            while (o.Count > 2 && D2.Distance(o[0], o[o.Count - 1]) <= 0.01) o.RemoveAt(o.Count - 1);
            return o;
        }

        public static string Describe(RoundaboutGeometry G)
        {
            int splitters = 0, crosswalks = 0;
            foreach (var L in G.legs) { if (L.splitters.Count > 0) splitters++; if (L.sCwInner > 0) crosswalks++; }
            return string.Format(Inv, "node {0} ({1}){2}: ICD {3:F1} m (mapped ring radius {4:F1} m), ring {5:F1}-{6:F1} m, central island {7:F1} m; {8} legs, smallest gap {9:F0} degrees; {10} raised splitters, {11} crosswalks{12}",
                G.node, G.osm, G.mini ? " mini" : "", G.icd, G.ringRadiusMapped, G.Ri, G.Ro, G.Ra, G.legs.Count, G.minLegGapDeg, splitters, crosswalks,
                G.notes.Count > 0 ? "; " + string.Join("; ", G.notes) : "");
        }
    }
}
