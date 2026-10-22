using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using UnityEngine;

namespace Backroads.Roads
{
    /// <summary>
    /// roads-R3 step 3 (05 §6.3): where the road furniture stands. Every approach with a STOP, all-way STOP, YIELD or signal
    /// gets exactly one sign (or signal head) on its near side, on the right of the approaching traffic: at the stop line
    /// (or where 05 §5.4 would put one when the approach has none), moved up to 1 m upstream off a ramp or inlet, 0.6 m
    /// behind the kerb face in town (2.1 m mounting) or 0.6 m beyond the 1.2 m shoulder on unkerbed roads (1.8 m from the
    /// pavement edge, 1.5 m mounting), facing the traffic and turned 5° away from the road. All-way stops add the plaque on
    /// the STOP post. Junctions of two named streets get street-name blades on two corners across from each other, on a
    /// STOP post when one stands within 3 m, else on their own post 0.6 m behind the kerb at the middle of the corner.
    /// </summary>
    public static class RoadPropBuilder
    {
        public const int Version = 1;
        public const double SignBehindKerb = 0.6, SignTurnDeg = 5.0, MountTown = 2.1, MountRural = 1.5, MaxUpstream = 1.0, PlaqueDrop = 0.35, BladeOnPost = 3.0;

        public static List<RoadNetworkAsset.Prop> Build(RoadGraph g, RoadSurfaces surf, KerbNetwork kn, IReadOnlyList<StreetInfo> streets, KerbSettings st = null)
        {
            st = st ?? new KerbSettings();
            var props = new List<RoadNetworkAsset.Prop>();
            var streetOfEdge = new Dictionary<int, int>();
            if (streets != null) foreach (var s in streets) foreach (int e in s.edges) streetOfEdge[e] = s.id;
            var faces = new Dictionary<(int, int), List<KerbFace>>();
            if (kn != null)
                foreach (var f in kn.faces)
                {
                    if (!faces.TryGetValue((f.edge, f.side), out var l)) faces[(f.edge, f.side)] = l = new List<KerbFace>();
                    l.Add(f);
                }

            foreach (var ns in surf.nodes)
            {
                var n = g.nodes[ns.node];
                var stops = new List<(int arm, Vector3 p)>();
                for (int k = 0; k < ns.arms.Count; k++)
                {
                    var a = ns.arms[k];
                    var c = a.control;
                    if (c == Control.RoundaboutYield) { RoundaboutYields(ns, k, faces, streetOfEdge, props, st); continue; }
                    if (c == Control.None) continue;
                    var kind = c == Control.Yield ? RoadPropKind.Yield : c == Control.Signal ? RoadPropKind.Signal : RoadPropKind.Stop;
                    double sEdge = double.IsNaN(a.sEdge) ? IntersectedEdge(ns, a) : a.sEdge;
                    double s = a.sStop >= 0 ? a.sStop : Math.Min(Math.Max(a.sT + 0.3, sEdge + 1.22), sEdge + 9.14);
                    int side = a.atA ? 0 : 1;
                    var face = FaceAt(faces, a.edge, side, a.frame.P(s, a.hwL), out double fs);
                    // off a ramp or an inlet, upstream (away from the node) by up to 1 m
                    if (face != null)
                        for (double d = 0; d <= MaxUpstream + 1e-9; d += 0.25)
                        {
                            var fsd = FaceAt(faces, a.edge, side, a.frame.P(s + d, a.hwL), out double fq);
                            if (fsd == null || !Blocked(fsd, fq)) { s += d; face = fsd ?? face; fs = fq; break; }
                        }
                    bool kerbed = face != null ? face.kerbed : a.kerbed;
                    double off = kerbed ? SignBehindKerb : st.shoulder + SignBehindKerb;
                    var p2 = a.frame.P(s, a.hwL + off);
                    double y = GroundBeside(face, fs, off, kerbed, a, s, st);
                    var dir = a.frame.Dir(s);
                    float yaw = (float)(Math.Atan2(dir.x, dir.z) / Geo.Deg - SignTurnDeg);
                    var prop = new RoadNetworkAsset.Prop
                    {
                        kind = kind, position = new Vector3((float)p2.x, (float)y, (float)p2.z), yawDeg = Wrap(yaw), mount = (float)(kerbed ? MountTown : MountRural),
                        node = ns.node, arm = k, street = Street(streetOfEdge, a.edge), street2 = -1, fromKerb = (float)off, fromEdge = (float)(s - (double.IsNaN(sEdge) ? 0 : sEdge)),
                    };
                    props.Add(prop);
                    if (kind == RoadPropKind.Stop) stops.Add((k, prop.position));
                    if (c == Control.AllWayStop)
                    {
                        var plaque = prop;
                        plaque.kind = RoadPropKind.AllWayPlaque;
                        plaque.mount = (float)(prop.mount - PlaqueDrop);
                        props.Add(plaque);
                    }
                }
                Blades(g, ns, n, streetOfEdge, faces, stops, props, st);
            }
            return props;
        }

        /// <summary>
        /// roads-R4 (05 §5.11): a roundabout entry gets a YIELD on its right just outside the yield line, behind the
        /// (flared) kerb, and a second on the raised splitter on its left, 0.6 m in from the splitter's kerb.
        /// </summary>
        static void RoundaboutYields(NodeSurface ns, int k, Dictionary<(int, int), List<KerbFace>> faces, Dictionary<int, int> streetOfEdge, List<RoadNetworkAsset.Prop> props, KerbSettings st)
        {
            var a = ns.arms[k];
            var G = ns.roundabout;
            if (G == null) return;
            var L = G.legs[k];
            var sst = new SurfaceSettings();
            double s = G.Ro + 1.5;
            int side = a.atA ? 0 : 1;
            var face = FaceAt(faces, a.edge, side, a.frame.P(s, L.EdgeL(s)), out double fs);
            bool kerbed = face != null ? face.kerbed : a.kerbed;
            double off = kerbed ? SignBehindKerb : st.shoulder + SignBehindKerb;
            var p2 = a.frame.P(s, L.EdgeL(s) + off);
            double y = face != null ? GroundBeside(face, fs, off, kerbed, a, s, st) : SurfaceBuilder.JunctionHeight(ns, p2, sst);
            var dir = a.frame.Dir(s);
            float facing = (float)(Math.Atan2(dir.x, dir.z) / Geo.Deg);
            props.Add(new RoadNetworkAsset.Prop
            {
                kind = RoadPropKind.Yield, position = new Vector3((float)p2.x, (float)y, (float)p2.z), yawDeg = Wrap(facing - (float)SignTurnDeg), mount = (float)(kerbed ? MountTown : MountRural),
                node = ns.node, arm = k, street = Street(streetOfEdge, a.edge), street2 = -1, fromKerb = (float)off, fromEdge = (float)(s - G.Ro),
            });
            if (L.splitters.Count == 0) return;
            double ss = L.splitterS0 + 1.5;
            double half = Math.Min(L.flare, L.FlareAt(ss));
            if (half < SignBehindKerb + 0.2) return;
            var q = a.frame.P(ss, half - SignBehindKerb);
            props.Add(new RoadNetworkAsset.Prop
            {
                kind = RoadPropKind.Yield, position = new Vector3((float)q.x, (float)(SurfaceBuilder.JunctionHeight(ns, q, sst) + sst.roundabout.islandKerb), (float)q.z),
                yawDeg = Wrap(facing + (float)SignTurnDeg), mount = (float)MountTown, node = ns.node, arm = k, street = Street(streetOfEdge, a.edge), street2 = -1,
                fromKerb = (float)SignBehindKerb, fromEdge = (float)(ss - G.Ro),
            });
        }

        static float Wrap(float deg)
        {
            while (deg < 0) deg += 360;
            while (deg >= 360) deg -= 360;
            return deg;
        }

        static int Street(Dictionary<int, int> map, int edge) => map.TryGetValue(edge, out int s) ? s : -1;

        static bool Blocked(KerbFace f, double s)
        {
            foreach (var iv in f.forbidden)
                if ((iv.reason == FrontageBlock.Ramp || iv.reason == FrontageBlock.Inlet) && s >= iv.s0 && s <= iv.s1) return true;
            return false;
        }

        /// <summary>The face of (edge, side) whose kerb passes nearest p (within 1 m), and p's arc length on it.</summary>
        static KerbFace FaceAt(Dictionary<(int, int), List<KerbFace>> faces, int edge, int side, D2 p, out double s)
        {
            s = 0;
            if (!faces.TryGetValue((edge, side), out var l)) return null;
            KerbFace best = null;
            double bd = 1.0;
            foreach (var f in l)
            {
                double fs = Project(f, p, out double d);
                if (d < bd) { bd = d; best = f; s = fs; }
            }
            return best;
        }

        static double Project(KerbFace f, D2 p, out double distance)
        {
            distance = double.MaxValue;
            double best = 0;
            for (int i = 0; i + 1 < f.Count; i++)
            {
                var e = f.kerb[i + 1] - f.kerb[i];
                double l2 = e.Dot(e), t = l2 > 0 ? Math.Max(0, Math.Min(1, (p - f.kerb[i]).Dot(e) / l2)) : 0;
                double d = D2.Distance(f.kerb[i] + e * t, p);
                if (d < distance) { distance = d; best = f.s[i] + t * (f.s[i + 1] - f.s[i]); }
            }
            return best;
        }

        static double Interp(List<double> s, List<double> v, double q)
        {
            int n = s.Count;
            if (q <= s[0]) return v[0];
            if (q >= s[n - 1]) return v[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (s[m] <= q) lo = m; else hi = m; }
            double t = (q - s[lo]) / Math.Max(1e-12, s[hi] - s[lo]);
            return v[lo] + (v[hi] - v[lo]) * t;
        }

        /// <summary>The finished ground where a post stands: the walk (kerbed) or just beyond the shoulder (unkerbed).</summary>
        static double GroundBeside(KerbFace f, double s, double off, bool kerbed, ArmSurface a, double sa, KerbSettings st)
        {
            if (f == null) return a.frame.Y(sa);
            double gutter = Interp(f.s, f.gutter, s);
            if (kerbed)
            {
                double top = Interp(f.s, f.top, s);
                return top + st.walkCrossSlope * Math.Max(0, off - st.kerbTop);
            }
            double slope = f.cls == StreetClass.Dirt ? 0.04 : 0.02;
            return gutter - st.shoulderLip - Math.Min(slope * st.shoulder, 0.15);
        }

        /// <summary>Where the arm's centreline meets the nearest other arm's pavement edge (05 §5.4's intersected edge).</summary>
        static double IntersectedEdge(NodeSurface ns, ArmSurface a)
        {
            double best = double.NaN;
            foreach (var b in ns.arms)
            {
                if (b == a) continue;
                foreach (var u in new[] { b.hwL, -b.hwR })
                    if (a.frame.FirstCrossing(0, b.frame, u, 60, 60, out double sa, out _, out _) && (double.IsNaN(best) || sa < best)) best = sa;
            }
            return double.IsNaN(best) ? Math.Max(a.hwL, a.hwR) : best;
        }

        /// <summary>Street-name blades on two corners across from each other (05 §6.3), naming the corner's two streets.</summary>
        static void Blades(RoadGraph g, NodeSurface ns, GraphNode n, Dictionary<int, int> streetOfEdge, Dictionary<(int, int), List<KerbFace>> faces,
            List<(int arm, Vector3 p)> stops, List<RoadNetworkAsset.Prop> props, KerbSettings st)
        {
            if (ns.arms.Count < 3 || ns.mode == SurfaceMode.Fallback) return;
            var cands = new List<int>();
            for (int k = 0; k < ns.corners.Count; k++)
            {
                var c = ns.corners[k];
                int sa = Street(streetOfEdge, ns.arms[c.armI].edge), sb = Street(streetOfEdge, ns.arms[c.armJ].edge);
                if (sa < 0 || sb < 0 || sa == sb || c.arc.Count < 2) continue;
                cands.Add(k);
            }
            if (cands.Count == 0) return;
            // two corners as far apart round the node as possible (diagonal on a 4-way)
            var pick = new List<int> { cands[0] };
            if (cands.Count > 1)
            {
                int best = cands[1], m = ns.corners.Count;
                foreach (int k in cands)
                {
                    int d = Math.Min((k - cands[0] + m) % m, (cands[0] - k + m) % m);
                    int db = Math.Min((best - cands[0] + m) % m, (cands[0] - best + m) % m);
                    if (d > db) best = k;
                }
                if (best != cands[0]) pick.Add(best);
            }
            foreach (int k in pick)
            {
                var c = ns.corners[k];
                var I = ns.arms[c.armI];
                var J = ns.arms[c.armJ];
                var mid = c.arc[c.arc.Count / 2];
                var inward = (c.centre - mid).Normalized;
                if (c.kind != CornerKind.Fillet || inward.Length < 0.5) inward = (mid - n.p.XZ).Normalized;
                var at = mid + inward * SignBehindKerb;
                // on a STOP post of this corner when one stands close
                Vector3 pos = new Vector3((float)at.x, 0, (float)at.z);
                bool onPost = false;
                foreach (var (arm, sp) in stops)
                {
                    if (arm != c.armI && arm != c.armJ) continue;
                    if (D2.Distance(new D2(sp.x, sp.z), at) <= BladeOnPost) { pos = sp; onPost = true; break; }
                }
                if (!onPost)
                {
                    var face = FaceAt(faces, I.edge, I.atA ? 0 : 1, mid, out double fs) ?? FaceAt(faces, J.edge, J.atA ? 1 : 0, mid, out fs);
                    double y = face != null ? (face.kerbed ? Interp(face.s, face.top, fs) + st.walkCrossSlope * (SignBehindKerb - st.kerbTop) : Interp(face.s, face.gutter, fs)) : n.p.y;
                    pos.y = (float)y;
                }
                var dir = I.frame.Dir(0);
                props.Add(new RoadNetworkAsset.Prop
                {
                    kind = RoadPropKind.StreetName, position = pos, yawDeg = Wrap((float)(Math.Atan2(dir.x, dir.z) / Geo.Deg)),
                    mount = (float)(MountTown + (onPost ? 0.9 : 0.6)), node = ns.node, arm = c.armI,
                    street = Street(streetOfEdge, I.edge), street2 = Street(streetOfEdge, J.edge), fromKerb = (float)SignBehindKerb, fromEdge = 0,
                });
            }
        }
    }
}
