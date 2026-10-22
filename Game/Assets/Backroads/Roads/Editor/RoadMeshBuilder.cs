using System;
using System.Collections.Generic;
using System.Globalization;
using Backroads.Core.WorldBake;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>The road meshes of one town before chunking, and what the markings came to.</summary>
    public sealed class RoadMeshes
    {
        public readonly List<RoadMeshPart> parts = new List<RoadMeshPart>();
        public readonly List<string> notes = new List<string>();
        public double centreLineM, noPassingM, edgeLineM, warningM;
        public int stopLines, crosswalks, yieldLines;
    }

    /// <summary>
    /// roads-R3 steps 1, 4 and 5's geometry: the asphalt (R1's ribbons, split diagonally the same way everywhere, and
    /// junction meshes; paved roads only: gravel and dirt are the terrain's own surface), the concrete (the kerb face from
    /// gutter to top on every kerbed face, the walks from R2's kerbside partition, a 0.15 m skirt under each walk's back
    /// edge) and the markings (05 §5.8, MUTCD 11th edition): double yellow centre lines on MainStreet; on rural highways a
    /// broken yellow line (3.05 m dash, 9.15 m gap) with no-passing zones from passing sight distance (Table 3B-1, eye and
    /// object 1.07 m, zones at least 152 m, gaps under 122 m closed); none on residential streets, one-way or one-lane
    /// roads; centre lines end at the stop line, the crosswalk or the mouth, never on a junction surface; white edge lines
    /// 0.15 m wide 0.15 m in from the pavement edge on rural highways; 0.45 m stop lines from the centre line to 0.3 m
    /// short of the kerb at every STOP and signal approach; crosswalks (continental 0.6 m bars downtown, two 0.3 m
    /// transverse lines elsewhere); yellow detectable-warning strips 0.61 m deep along every lowered kerb. Paint is
    /// draped 4 mm over the surface it lies on, with dashes phase-locked to s = 0 of their edge. Vertex data per 05 §5.9:
    /// UV0 world planar (x/4, z/4), UV1 lane space (lateral, s) on ribbons, colour R wear (flat until R5), G wet (the
    /// gutter flowline, curb inlets), B patch id, A edge blend (0 at unkerbed pavement edges).
    /// </summary>
    public static class RoadMeshBuilder
    {
        public const int Version = 2;
        public const double PieceLength = 32.0, PaintLift = 0.004, SkirtDepth = 0.15;
        public static readonly Color32 White = new Color32(217, 217, 209, 255), Yellow = new Color32(217, 158, 26, 255), WarningYellow = new Color32(232, 186, 36, 255);
        static readonly Vector3 Up = new Vector3(0, 1, 0);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static RoadMeshes Build(RoadGraph g, RoadSurfaces surf, KerbNetwork kn, IReadOnlyList<StreetInfo> streets, KerbSettings st = null)
        {
            st = st ?? new KerbSettings();
            var m = new RoadMeshes();
            var asphalt = new SurfaceIndex();
            Ribbons(g, surf, m, asphalt);
            Junctions(g, surf, m, asphalt);
            Concrete(kn, st, m);
            Markings(g, surf, kn, streets, st, m, asphalt);
            Normals(m, asphalt);
            return m;
        }

        static bool Paved(RoadGraph g, int edge)
        {
            var e = g.edges[edge];
            return KerbRules.Paved(KerbRules.Surface(e.cls, g.RoadOf(e.sourceRoad)?.surface));
        }

        static Vector2 World(D3 p, double scale) => new Vector2((float)(p.x / scale), (float)(p.z / scale));
        static byte Patch(int id, int salt) => (byte)((id * 37 + salt * 101 + 17) & 255);

        // ------------------------------------------------------------------ asphalt

        static void Ribbons(RoadGraph g, RoadSurfaces surf, RoadMeshes m, SurfaceIndex idx)
        {
            var inlets = new List<D2>();
            foreach (var i in surf.inlets) inlets.Add(i.p.XZ);
            foreach (var r in surf.ribbons)
            {
                int P = r.profile.Length, n = r.stations.Count;
                if (n < 2) continue;
                // paved runs of stations: the whole ribbon, or only an unpaved road's aprons (roads-R4), their gravel end feathered
                var runs = new List<(int a, int b, bool apronEnd)>();
                if (Paved(g, r.edge)) runs.Add((0, n - 1, false));
                else
                {
                    int qa = 0, qb = n - 1;
                    if (r.apronA > 0) { while (qa + 1 < n && r.stationS[qa + 1] <= r.s0 + r.apronA + 1e-6) qa++; if (qa > 0) runs.Add((0, qa, true)); }
                    if (r.apronB > 0) { while (qb - 1 >= 0 && r.stationS[qb - 1] >= r.s1 - r.apronB - 1e-6) qb--; if (qb < n - 1) runs.Add((qb, n - 1, true)); }
                }
                foreach (var (ra, rb, apronEnd) in runs)
                {
                    int q0 = ra;
                    while (q0 < rb)
                    {
                        int q1 = q0 + 1;
                        while (q1 < rb && r.stationS[q1] - r.stationS[q0] < PieceLength) q1++;
                        var part = new RoadMeshPart { material = RoadMaterial.Asphalt, collider = true, smooth = 1, edge = r.edge };
                        var ix = new int[q1 - q0 + 1, P];
                        for (int q = q0; q <= q1; q++)
                            for (int k = 0; k < P; k++)
                            {
                                var p = r.stations[q][k];
                                bool edgeVert = k == 0 || k == P - 1;
                                bool gravelEnd = apronEnd && ((ra == 0 && q == rb) || (rb == n - 1 && q == ra));
                                byte wet = (byte)(r.kerbed && edgeVert ? 255 : NearInlet(inlets, p.XZ) ? 160 : 0);
                                byte blend = (byte)((!r.kerbed && edgeVert) || gravelEnd ? 0 : 255);
                                ix[q - q0, k] = part.Add(p, World(p, 4), new Vector2((float)r.profile[k], (float)r.stationS[q]), new Color32(0, wet, Patch(r.edge, 1), blend));
                            }
                        for (int q = q0; q < q1; q++)
                            for (int k = 0; k + 1 < P; k++)
                            {
                                int a = ix[q - q0, k], b = ix[q + 1 - q0, k], c = ix[q + 1 - q0, k + 1], d = ix[q - q0, k + 1];
                                part.Tri(a, b, c, Up);
                                part.Tri(a, c, d, Up);
                            }
                        idx.Add(part);
                        m.parts.Add(part);
                        q0 = q1;
                    }
                }
            }
        }

        static bool NearInlet(List<D2> inlets, D2 p)
        {
            foreach (var i in inlets) if (Math.Abs(i.x - p.x) < 1.5 && Math.Abs(i.z - p.z) < 1.5 && D2.Distance(i, p) < 1.5) return true;
            return false;
        }

        static void Junctions(RoadGraph g, RoadSurfaces surf, RoadMeshes m, SurfaceIndex idx)
        {
            foreach (var ns in surf.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback || ns.triangles.Count < 3) continue;
                ArmSurface main = null;
                foreach (var a in ns.arms) if (main == null || RoadClasses.Rank(a.cls) > RoadClasses.Rank(main.cls)) main = a;
                if (!Paved(g, main.edge)) continue;
                var part = new RoadMeshPart { material = RoadMaterial.Asphalt, collider = true, smooth = 1, node = ns.node };
                foreach (var v in ns.vertices) part.Add(v, World(v, 4), Vector2.zero, new Color32(0, 0, Patch(ns.node, 2), 255));
                for (int k = 0; k + 2 < ns.triangles.Count; k += 3) part.Tri(ns.triangles[k], ns.triangles[k + 1], ns.triangles[k + 2], Up);
                idx.Add(part);
                m.parts.Add(part);
            }
        }

        // ------------------------------------------------------------------ concrete

        static void Concrete(KerbNetwork kn, KerbSettings st, RoadMeshes m)
        {
            // walks: R2's kerbside partition (each piece convex; a fan from its first vertex)
            var groups = new Dictionary<(int, int), RoadMeshPart>();
            var order = new List<(int, int)>();
            foreach (var pc in kn.pieces)
            {
                if (pc.kind != KerbPieceKind.Walk) continue;
                var key = (pc.face, pc.seg / 16);
                if (!groups.TryGetValue(key, out var part))
                {
                    groups[key] = part = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, smooth = 2, edge = kn.faces[pc.face].edge };
                    order.Add(key);
                }
                int b0 = part.pos.Count;
                for (int k = 0; k < pc.poly.Length; k++)
                {
                    var p = new D3(pc.poly[k].x, pc.surfaceY[k], pc.poly[k].z);
                    part.Add(p, World(p, 2), Vector2.zero, new Color32(0, 0, Patch(pc.face, 3), 255));
                }
                for (int k = 1; k + 1 < pc.poly.Length; k++) part.Tri(b0, b0 + k, b0 + k + 1, Up);
            }
            foreach (var key in order) m.parts.Add(groups[key]);

            foreach (var f in kn.faces)
            {
                if (!f.kerbed || f.bridge) continue;
                // the kerb face, gutter to top, facing the street (flat shaded), in pieces of at most 32 m
                RoadMeshPart face = null, skirt = null;
                double start = 0;
                for (int i = 0; i + 1 < f.Count; i++)
                {
                    if (face == null || f.s[i] - start >= PieceLength)
                    {
                        face = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, edge = f.edge };
                        m.parts.Add(face);
                        if (f.walk) { skirt = new RoadMeshPart { material = RoadMaterial.Concrete, lods = 1, edge = f.edge }; m.parts.Add(skirt); }
                        start = f.s[i];
                    }
                    var d = f.kerb[i + 1] - f.kerb[i];
                    if (d.Length < 1e-6) continue;
                    var left = d.Normalized.Left;
                    var toStreet = new Vector3((float)-left.x, 0, (float)-left.z);
                    if (Math.Max(f.top[i] - f.gutter[i], f.top[i + 1] - f.gutter[i + 1]) > 0.005)
                    {
                        var c = new Color32(0, 0, Patch(f.id, 4), 255);
                        int a0 = face.Add(new D3(f.kerb[i].x, f.gutter[i], f.kerb[i].z), new Vector2((float)f.s[i], 0), Vector2.zero, c, toStreet);
                        int a1 = face.Add(new D3(f.kerb[i + 1].x, f.gutter[i + 1], f.kerb[i + 1].z), new Vector2((float)f.s[i + 1], 0), Vector2.zero, c, toStreet);
                        int b1 = face.Add(new D3(f.kerb[i + 1].x, f.top[i + 1], f.kerb[i + 1].z), new Vector2((float)f.s[i + 1], (float)(f.top[i + 1] - f.gutter[i + 1])), Vector2.zero, c, toStreet);
                        int b0 = face.Add(new D3(f.kerb[i].x, f.top[i], f.kerb[i].z), new Vector2((float)f.s[i], (float)(f.top[i] - f.gutter[i])), Vector2.zero, c, toStreet);
                        face.Tri(a0, a1, b1, toStreet);
                        face.Tri(a0, b1, b0, toStreet);
                    }
                    if (skirt != null && f.width[i] > st.kerbTop + 0.05 && f.width[i + 1] > st.kerbTop + 0.05)
                    {
                        var outward = new Vector3((float)left.x, 0, (float)left.z);
                        var c = new Color32(0, 0, 0, 255);
                        int a0 = skirt.Add(new D3(f.back[i].x, f.backY[i], f.back[i].z), Vector2.zero, Vector2.zero, c, outward);
                        int a1 = skirt.Add(new D3(f.back[i + 1].x, f.backY[i + 1], f.back[i + 1].z), Vector2.zero, Vector2.zero, c, outward);
                        int b1 = skirt.Add(new D3(f.back[i + 1].x, f.backY[i + 1] - SkirtDepth, f.back[i + 1].z), Vector2.zero, Vector2.zero, c, outward);
                        int b0 = skirt.Add(new D3(f.back[i].x, f.backY[i] - SkirtDepth, f.back[i].z), Vector2.zero, Vector2.zero, c, outward);
                        skirt.Tri(a0, a1, b1, outward);
                        skirt.Tri(a0, b1, b0, outward);
                    }
                }
            }
            m.parts.RemoveAll(p => p.tri.Count == 0);
        }

        // ------------------------------------------------------------------ markings (05 §5.8)

        /// <summary>A paint strip along a frame at lateral offset u, width w, over [s0, s1] (split every metre), draped.</summary>
        static void Strip(RoadMeshes m, SurfaceIndex idx, RoadFrame f, double s0, double s1, double u, double w, Color32 col, MarkingKind kind, byte lods, int edge, int node = -1, int arm = -1)
        {
            if (s1 - s0 < 0.02) return;
            var part = new RoadMeshPart { material = RoadMaterial.Paint, lods = lods, marking = kind, edge = edge, node = node, arm = arm };
            int steps = Math.Max(1, (int)Math.Ceiling((s1 - s0) / 2.0));
            int prevL = -1, prevR = -1;
            for (int k = 0; k <= steps; k++)
            {
                double s = s0 + (s1 - s0) * k / steps;
                var pl = f.P(s, u + 0.5 * w);
                var pr = f.P(s, u - 0.5 * w);
                int l = part.Add(Drape(idx, pl, f, s), new Vector2(0, (float)(s - s0)), Vector2.zero, col, Up);
                int r = part.Add(Drape(idx, pr, f, s), new Vector2(1, (float)(s - s0)), Vector2.zero, col, Up);
                if (k > 0) { part.Tri(prevR, r, l, Up); part.Tri(prevR, l, prevL, Up); }
                prevL = l; prevR = r;
            }
            m.parts.Add(part);
        }

        /// <summary>A paint band across a frame: lateral [u0, u1] by [s0, s1], split every 0.5 m across, draped.</summary>
        static void Band(RoadMeshes m, SurfaceIndex idx, RoadFrame f, double s0, double s1, double u0, double u1, Color32 col, MarkingKind kind, byte lods, int edge, int node, int arm)
        {
            if (u1 - u0 < 0.02 || s1 - s0 < 0.02) return;
            var part = new RoadMeshPart { material = RoadMaterial.Paint, lods = lods, marking = kind, edge = edge, node = node, arm = arm };
            int steps = Math.Max(1, (int)Math.Ceiling((u1 - u0) / 0.5));
            int prevA = -1, prevB = -1;
            for (int k = 0; k <= steps; k++)
            {
                double u = u0 + (u1 - u0) * k / steps;
                var pa = f.P(s0, u);
                var pb = f.P(s1, u);
                int a = part.Add(Drape(idx, pa, f, s0), new Vector2((float)(u - u0), 0), Vector2.zero, col, Up);
                int b = part.Add(Drape(idx, pb, f, s1), new Vector2((float)(u - u0), (float)(s1 - s0)), Vector2.zero, col, Up);
                if (k > 0) { part.Tri(prevA, a, b, Up); part.Tri(prevA, b, prevB, Up); }
                prevA = a; prevB = b;
            }
            m.parts.Add(part);
        }

        static D3 Drape(SurfaceIndex idx, D2 p, RoadFrame f, double s)
        {
            double y = idx.Height(p, out double h) ? h : f.Y(s);
            return new D3(p.x, y + PaintLift, p.z);
        }

        static void Markings(RoadGraph g, RoadSurfaces surf, KerbNetwork kn, IReadOnlyList<StreetInfo> streets, KerbSettings st, RoadMeshes m, SurfaceIndex idx)
        {
            var frames = new RoadFrame[g.edges.Count];
            foreach (var e in g.edges) frames[e.id] = new RoadFrame(e.pts);
            var noPass = NoPassingZones(g, streets, frames, m);
            var centreHalf = new Dictionary<int, double>();
            foreach (var r in surf.ribbons)
            {
                var e = g.edges[r.edge];
                var road = g.RoadOf(e.sourceRoad);
                if (road == null || !Paved(g, r.edge)) continue;
                var f = frames[r.edge];
                bool twoWay = road.oneway == 0 && (road.xs == null || road.xs.lanes != 1);
                bool main = e.cls == RoadClass.MainStreet, rural = e.cls == RoadClass.Highway && !road.inKerbZone;
                // where the centre line may run: from mouth to mouth, stopping at stop lines and crosswalks
                double a = r.s0, b = r.s1;
                foreach (var (node, atA) in new[] { (e.nodeA, true), (e.nodeB, false) })
                {
                    var ns = surf.Of(node);
                    if (ns == null) continue;
                    foreach (var arm in ns.arms)
                    {
                        if (arm.edge != e.id || arm.atA != atA || arm.mouth == null) continue;
                        double cut = arm.sMouth;
                        if (arm.sStop >= 0) cut = Math.Max(cut, arm.sStop + 0.5 * new SurfaceSettings().stopLine);
                        if (arm.crosswalk) cut = Math.Max(cut, arm.sCwOuter);
                        if (atA) a = Math.Max(a, cut); else b = Math.Min(b, e.Length - cut);
                    }
                }
                if (twoWay && (main || rural) && b - a > 0.5)
                {
                    centreHalf[r.edge] = 0.15;
                    if (main)
                    {
                        foreach (double u in new[] { -0.10, 0.10 })
                        {
                            Strip(m, idx, f, a, b, u, 0.10, Yellow, MarkingKind.CentreLine, 1, r.edge);
                        }
                        Strip(m, idx, f, a, b, 0, 0.40, Yellow, MarkingKind.CentreLine, 2, r.edge);
                        m.centreLineM += b - a;
                    }
                    else RuralCentre(m, idx, f, a, b, r.edge, noPass);
                }
                if (rural && b > a)
                {
                    double hwL = road.xs?.hwLeft ?? e.hw, hwR = road.xs?.hwRight ?? e.hw;
                    Strip(m, idx, f, r.s0, r.s1, hwL - 0.225, 0.15, White, MarkingKind.EdgeLine, 1, r.edge);
                    Strip(m, idx, f, r.s0, r.s1, -(hwR - 0.225), 0.15, White, MarkingKind.EdgeLine, 1, r.edge);
                    m.edgeLineM += 2 * (r.s1 - r.s0);
                }
            }

            var ss = new SurfaceSettings();
            foreach (var ns in surf.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback) continue;
                for (int k = 0; k < ns.arms.Count; k++)
                {
                    var a = ns.arms[k];
                    // an unpaved approach is painted only where its apron makes it paved (roads-R4)
                    if (!Paved(g, a.edge) && !(a.sStop >= 0 && SurfaceBuilder.PavedAt(g, surf, a.edge, a.atA ? a.sStop : g.edges[a.edge].Length - a.sStop))) continue;
                    var road = g.RoadOf(g.edges[a.edge].sourceRoad);
                    int ow = road?.oneway ?? 0;
                    bool inboundOnly = (ow == 1 && !a.atA) || (ow == -1 && a.atA);
                    // stop line: from the centre line (or the far kerb on one-way approaches) to 0.3 m short of the kerb
                    if (a.sStop >= 0 && (a.control == Control.MinorStop || a.control == Control.AllWayStop || a.control == Control.Signal))
                    {
                        double c0 = centreHalf.TryGetValue(a.edge, out double ch) ? ch : 0.0;
                        double u0 = inboundOnly ? -a.hwR + 0.3 : c0, u1 = a.hwL - 0.3;
                        double s0 = a.sStop - 0.5 * ss.stopLine, s1 = a.sStop + 0.5 * ss.stopLine;
                        Band(m, idx, a.frame, s0, s1, u0, u1, White, MarkingKind.StopLine, 1, a.edge, ns.node, k);
                        Band(m, idx, a.frame, s0 - 0.5 * ss.stopLine, s1 + 0.5 * ss.stopLine, u0, u1, White, MarkingKind.StopLine, 2, a.edge, ns.node, k);
                        m.stopLines++;
                    }
                    if (a.crosswalk)
                    {
                        bool downtown = g.downtown != null && g.downtown.Length >= 3 && TownZone.InPolygon(g.downtown, g.nodes[ns.node].p.XZ);
                        double uL = a.hwL - 0.15, uR = -a.hwR + 0.15;
                        if (downtown)
                        {
                            // continental: 0.6 m bars along the road, 0.6 m apart, one centred on the centre line
                            for (int j = -20; j <= 20; j++)
                            {
                                double uc = 1.2 * j;
                                double b0 = Math.Max(uR, uc - 0.3), b1 = Math.Min(uL, uc + 0.3);
                                if (b1 - b0 < 0.2) continue;
                                Band(m, idx, a.frame, a.sCwInner, a.sCwOuter, b0, b1, White, MarkingKind.Crosswalk, 1, a.edge, ns.node, k);
                            }
                        }
                        else
                        {
                            Band(m, idx, a.frame, a.sCwInner, a.sCwInner + 0.3, uR, uL, White, MarkingKind.Crosswalk, 1, a.edge, ns.node, k);
                            Band(m, idx, a.frame, a.sCwOuter - 0.3, a.sCwOuter, uR, uL, White, MarkingKind.Crosswalk, 1, a.edge, ns.node, k);
                        }
                        m.crosswalks++;
                    }
                }
            }
            Warnings(kn, st, m);
        }

        /// <summary>The rural centre line from the no-passing zones of each direction (u &lt; 0 is the lane of traffic running with the edge).</summary>
        static void RuralCentre(RoadMeshes m, SurfaceIndex idx, RoadFrame f, double a, double b, int edge, Dictionary<int, (List<(double, double)> with, List<(double, double)> against)> noPass)
        {
            noPass.TryGetValue(edge, out var z);
            bool In(List<(double, double)> l, double s) { if (l == null) return false; foreach (var (x0, x1) in l) if (s >= x0 && s <= x1) return true; return false; }
            // runs of constant (no passing with, no passing against) sampled every 0.5 m
            double runStart = a;
            bool pw = In(z.with, a), pa = In(z.against, a);
            for (double s = a; ; s = Math.Min(b, s + 0.5))
            {
                bool w = In(z.with, s), ag = In(z.against, s);
                bool end = s >= b - 1e-9;
                if (w != pw || ag != pa || end)
                {
                    double s1 = end ? b : s;
                    Centre(m, idx, f, runStart, s1, pw, pa, edge);
                    runStart = s1;
                    pw = w; pa = ag;
                }
                if (end) break;
            }
        }

        static void Centre(RoadMeshes m, SurfaceIndex idx, RoadFrame f, double s0, double s1, bool noWith, bool noAgainst, int edge)
        {
            if (s1 - s0 < 0.05) return;
            m.centreLineM += s1 - s0;
            if (noWith || noAgainst) m.noPassingM += s1 - s0;
            // LOD1: one line twice as wide, solid wherever either direction may not pass
            if (!noWith && !noAgainst)
            {
                Dashed(m, idx, f, s0, s1, 0, 0.10, 1, edge);
                Dashed(m, idx, f, s0, s1, 0, 0.20, 2, edge);
                return;
            }
            if (noWith) Solid(m, idx, f, s0, s1, -0.10, 0.10, 1, edge); else Dashed(m, idx, f, s0, s1, -0.10, 0.10, 1, edge);
            if (noAgainst) Solid(m, idx, f, s0, s1, 0.10, 0.10, 1, edge); else Dashed(m, idx, f, s0, s1, 0.10, 0.10, 1, edge);
            Solid(m, idx, f, s0, s1, 0, 0.40, 2, edge);
        }

        static void Solid(RoadMeshes m, SurfaceIndex idx, RoadFrame f, double s0, double s1, double u, double w, byte lods, int edge) =>
            Strip(m, idx, f, s0, s1, u, w, Yellow, MarkingKind.CentreLine, lods, edge);

        /// <summary>3.05 m dashes every 12.2 m, phase-locked to s = 0 of the edge.</summary>
        static void Dashed(RoadMeshes m, SurfaceIndex idx, RoadFrame f, double s0, double s1, double u, double w, byte lods, int edge)
        {
            const double period = 12.2, dash = 3.05;
            for (double k = Math.Floor(s0 / period) * period; k < s1; k += period)
            {
                double d0 = Math.Max(s0, k), d1 = Math.Min(s1, k + dash);
                if (d1 - d0 < 0.05) continue;
                int before = m.parts.Count;
                Strip(m, idx, f, d0, d1, u, w, Yellow, MarkingKind.CentreLine, lods, edge);
                if (m.parts.Count > before) m.parts[m.parts.Count - 1].dashed = true;
            }
        }

        /// <summary>Yellow detectable-warning strips 0.61 m deep behind the kerb top along every lowered kerb (LOD0).</summary>
        static void Warnings(KerbNetwork kn, KerbSettings st, RoadMeshes m)
        {
            foreach (var f in kn.faces)
            {
                if (!f.walk || f.corners.Count == 0) continue;
                RoadMeshPart part = null;
                int prevA = -1, prevB = -1;
                for (int i = 0; i < f.Count; i++)
                {
                    double h = KerbBuilder.KerbHeight(kn, f, f.t0 + f.s[i], st);
                    bool lowered = h <= st.dropped + 1e-6 && f.width[i] >= st.kerbTop + 0.4 && KerbBuilder.WarningAt(kn, f, f.t0 + f.s[i], st);
                    if (!lowered) { part = null; continue; }
                    double u0 = st.kerbTop, u1 = Math.Min(f.width[i], st.kerbTop + st.warningDepth);
                    var pa = f.kerb[i] + f.normal[i] * u0;
                    var pb = f.kerb[i] + f.normal[i] * u1;
                    double ya = f.top[i] + PaintLift, yb = f.top[i] + st.walkCrossSlope * (u1 - st.kerbTop) + PaintLift;
                    if (part == null)
                    {
                        part = new RoadMeshPart { material = RoadMaterial.Paint, lods = 1, marking = MarkingKind.Warning, edge = f.edge };
                        m.parts.Add(part);
                        prevA = prevB = -1;
                    }
                    int a = part.Add(new D3(pa.x, ya, pa.z), new Vector2(0, (float)f.s[i]), Vector2.zero, WarningYellow, Up);
                    int b = part.Add(new D3(pb.x, yb, pb.z), new Vector2(1, (float)f.s[i]), Vector2.zero, WarningYellow, Up);
                    if (prevA >= 0) { part.Tri(prevA, a, b, Up); part.Tri(prevA, b, prevB, Up); m.warningM += f.s[i] - f.s[i - 1]; }
                    prevA = a; prevB = b;
                }
            }
            m.parts.RemoveAll(p => p.tri.Count == 0);
        }

        // ------------------------------------------------------------------ no-passing zones (MUTCD §3B.02, Table 3B-1)

        /// <summary>Minimum passing sight distance by speed (MUTCD Table 3B-1), metres.</summary>
        public static double PassingSightDistance(double speedKmh)
        {
            double mph = speedKmh / 1.609344;
            double ft = mph <= 25 ? 450 : mph <= 40 ? 450 + (mph - 25) * 10 : 600 + (mph - 40) * 20;
            return ft * 0.3048;
        }

        /// <summary>
        /// Per rural-highway edge, the stretches where passing is not allowed for traffic running with the edge and against
        /// it: sight along the whole street (through junctions), eye and object 1.07 m above the road, the line of sight held
        /// within the pavement plus 2 m; zones closer than 122 m join, zones shorter than 152 m extend on in their direction.
        /// </summary>
        static Dictionary<int, (List<(double, double)> with, List<(double, double)> against)> NoPassingZones(RoadGraph g, IReadOnlyList<StreetInfo> streets, RoadFrame[] frames, RoadMeshes m)
        {
            var result = new Dictionary<int, (List<(double, double)>, List<(double, double)>)>();
            var chains = new List<List<(int edge, bool fwd)>>();
            if (streets != null)
                foreach (var s in streets) chains.AddRange(Chains(g, s.edges));
            else
                foreach (var e in g.edges) chains.Add(new List<(int, bool)> { (e.id, true) });
            foreach (var chain in chains)
            {
                bool any = false;
                foreach (var (edge, _) in chain) any |= g.edges[edge].cls == RoadClass.Highway && !(g.RoadOf(g.edges[edge].sourceRoad)?.inKerbZone ?? false);
                if (!any) continue;
                // the chain's centreline, with each point's edge and arc length on it
                var pts = new List<D3>();
                var owner = new List<(int edge, double s)>();
                foreach (var (edge, fwd) in chain)
                {
                    var e = g.edges[edge];
                    int n = e.pts.Length;
                    for (int k = 0; k < n; k++)
                    {
                        int i = fwd ? k : n - 1 - k;
                        if (pts.Count > 0 && k == 0) continue;
                        pts.Add(e.pts[i]);
                        owner.Add((edge, e.s[i]));
                    }
                }
                if (pts.Count < 3) continue;
                var cs = Polyline.ArcLength(pts);
                int N = pts.Count;
                var hw = new double[N];
                var psd = new double[N];
                for (int i = 0; i < N; i++)
                {
                    var road = g.RoadOf(g.edges[owner[i].edge].sourceRoad);
                    hw[i] = road?.xs != null ? 0.5 * road.xs.Width : g.edges[owner[i].edge].hw;
                    psd[i] = PassingSightDistance(road?.speedKmh ?? 55);
                }
                var noWith = new bool[N];
                var noAgainst = new bool[N];
                for (int i = 0; i < N; i++)
                {
                    noWith[i] = Sight(pts, cs, hw, i, +1, psd[i]) < psd[i];
                    noAgainst[i] = Sight(pts, cs, hw, i, -1, psd[i]) < psd[i];
                }
                var zw = Zones(cs, noWith, +1);
                var za = Zones(cs, noAgainst, -1);
                // back to each edge's own s, and its own direction
                foreach (var (edge, fwd) in chain)
                {
                    var e = g.edges[edge];
                    double c0 = double.MaxValue, c1 = double.MinValue;
                    for (int i = 0; i < N; i++) if (owner[i].edge == edge) { c0 = Math.Min(c0, cs[i]); c1 = Math.Max(c1, cs[i]); }
                    if (c1 < c0) continue;
                    var w = new List<(double, double)>();
                    var ag = new List<(double, double)>();
                    double L = e.Length;
                    foreach (var (x0, x1) in zw) Map(x0, x1, c0, c1, fwd, L, fwd ? w : ag);
                    foreach (var (x0, x1) in za) Map(x0, x1, c0, c1, fwd, L, fwd ? ag : w);
                    result[edge] = (w, ag);
                }
            }
            return result;
        }

        static void Map(double x0, double x1, double c0, double c1, bool fwd, double L, List<(double, double)> into)
        {
            double a = Math.Max(x0, c0), b = Math.Min(x1, c1);
            if (b <= a) return;
            if (fwd) into.Add((a - c0, b - c0));
            else into.Add((L - (b - c0), L - (a - c0)));
        }

        /// <summary>How far ahead (dir +1 along the chain, -1 against) the object stays in sight, up to `need`.</summary>
        static double Sight(List<D3> p, double[] cs, double[] hw, int i, int dir, double need)
        {
            const double eye = 1.07, lateral = 2.0;
            int n = p.Count;
            double best = 0;
            for (int j = i + dir; j >= 0 && j < n; j += dir)
            {
                double d = Math.Abs(cs[j] - cs[i]);
                D2 a = p[i].XZ, b = p[j].XZ;
                double ya = p[i].y + eye, yb = p[j].y + eye;
                bool clear = true;
                for (int k = i + dir; k != j && clear; k += dir)
                {
                    double t = Math.Abs(cs[k] - cs[i]) / Math.Max(1e-9, d);
                    double yLine = ya + (yb - ya) * t;
                    if (p[k].y > yLine) clear = false;
                    else if (Polyline.SegmentDistance(a, b, p[k].XZ) > hw[k] + lateral) clear = false;
                }
                if (!clear) return best;
                best = d;
                if (d >= need) return d;
            }
            return double.MaxValue; // the street ends in view: nothing hides an oncoming car
        }

        /// <summary>No-passing runs as intervals of chain arc length: gaps under 122 m closed, zones under 152 m extended in travel direction.</summary>
        static List<(double, double)> Zones(double[] cs, bool[] no, int dir)
        {
            const double minZone = 152.4, minGap = 121.9;
            var z = new List<(double, double)>();
            int n = cs.Length;
            for (int i = 0; i < n; i++)
            {
                if (!no[i]) continue;
                int j = i;
                while (j + 1 < n && no[j + 1]) j++;
                z.Add((cs[i], cs[j]));
                i = j;
            }
            var merged = new List<(double, double)>();
            foreach (var x in z)
            {
                if (merged.Count > 0 && x.Item1 - merged[merged.Count - 1].Item2 < minGap) merged[merged.Count - 1] = (merged[merged.Count - 1].Item1, x.Item2);
                else merged.Add(x);
            }
            double end = cs[n - 1];
            for (int k = 0; k < merged.Count; k++)
            {
                var (a, b) = merged[k];
                if (b - a < minZone)
                {
                    if (dir > 0) b = Math.Min(end, a + minZone);
                    else a = Math.Max(0, b - minZone);
                }
                merged[k] = (a, b);
            }
            return merged;
        }

        /// <summary>A street's edges as ordered paths (an edge list joined at the nodes where two of its edges meet).</summary>
        public static List<List<(int edge, bool fwd)>> Chains(RoadGraph g, IReadOnlyList<int> edges)
        {
            var set = new HashSet<int>(edges);
            var atNode = new Dictionary<int, List<int>>();
            foreach (int e in edges)
                foreach (int n in new[] { g.edges[e].nodeA, g.edges[e].nodeB })
                {
                    if (!atNode.TryGetValue(n, out var l)) atNode[n] = l = new List<int>();
                    l.Add(e);
                }
            var done = new HashSet<int>();
            var result = new List<List<(int, bool)>>();
            var sorted = new List<int>(edges);
            sorted.Sort();
            // start at ends (a node with one street edge), then whatever is left (loops)
            foreach (bool loops in new[] { false, true })
                foreach (int start in sorted)
                {
                    if (done.Contains(start)) continue;
                    var e0 = g.edges[start];
                    int endNode = atNode[e0.nodeA].Count == 1 ? e0.nodeA : atNode[e0.nodeB].Count == 1 ? e0.nodeB : -1;
                    if (endNode < 0 && !loops) continue;
                    if (endNode < 0) endNode = e0.nodeA;
                    var chain = new List<(int, bool)>();
                    int cur = start, node = endNode;
                    while (cur >= 0 && !done.Contains(cur))
                    {
                        done.Add(cur);
                        var e = g.edges[cur];
                        bool fwd = e.nodeA == node;
                        chain.Add((cur, fwd));
                        node = fwd ? e.nodeB : e.nodeA;
                        int next = -1;
                        foreach (int x in atNode[node]) if (x != cur && !done.Contains(x) && set.Contains(x)) { next = x; break; }
                        cur = next;
                    }
                    result.Add(chain);
                }
            return result;
        }

        // ------------------------------------------------------------------ normals

        /// <summary>Smoothing groups get area-weighted normals over every vertex at the same position (so chunks join without a crease).</summary>
        static void Normals(RoadMeshes m, SurfaceIndex asphalt)
        {
            var acc = new Dictionary<(int, long, long, long), Vector3>();
            (int, long, long, long) Key(int g, D3 p) => (g, (long)Math.Round(p.x * 1e4), (long)Math.Round(p.y * 1e4), (long)Math.Round(p.z * 1e4));
            foreach (var p in m.parts)
            {
                if (p.smooth == 0) continue;
                for (int k = 0; k + 2 < p.tri.Count; k += 3)
                {
                    D3 a = p.pos[p.tri[k]], b = p.pos[p.tri[k + 1]], c = p.pos[p.tri[k + 2]];
                    double ux = b.x - a.x, uy = b.y - a.y, uz = b.z - a.z, vx = c.x - a.x, vy = c.y - a.y, vz = c.z - a.z;
                    var n = new Vector3((float)(uy * vz - uz * vy), (float)(uz * vx - ux * vz), (float)(ux * vy - uy * vx));
                    foreach (int i in new[] { p.tri[k], p.tri[k + 1], p.tri[k + 2] })
                    {
                        var key = Key(p.smooth, p.pos[i]);
                        acc[key] = acc.TryGetValue(key, out var v) ? v + n : n;
                    }
                }
            }
            foreach (var p in m.parts)
            {
                if (p.smooth == 0) continue;
                for (int i = 0; i < p.pos.Count; i++)
                {
                    // asphalt: the surface's own slope half a metre either way (smooth over R1's long fan triangles,
                    // identical on both sides of every mouth and chunk border); walks: the averaged face normals
                    if (p.smooth == 1 && Slope(asphalt, p.pos[i], out var sn)) { p.nrm[i] = sn; continue; }
                    var n = acc.TryGetValue(Key(p.smooth, p.pos[i]), out var v) ? v : Up;
                    p.nrm[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Up;
                }
            }
        }

        static bool Slope(SurfaceIndex idx, D3 p, out Vector3 n)
        {
            const double h = 0.5;
            n = Up;
            bool xp = idx.Height(new D2(p.x + h, p.z), out double yxp), xm = idx.Height(new D2(p.x - h, p.z), out double yxm);
            bool zp = idx.Height(new D2(p.x, p.z + h), out double yzp), zm = idx.Height(new D2(p.x, p.z - h), out double yzm);
            if (!(xp || xm) || !(zp || zm)) return false;
            double gx = xp && xm ? (yxp - yxm) / (2 * h) : xp ? (yxp - p.y) / h : (p.y - yxm) / h;
            double gz = zp && zm ? (yzp - yzm) / (2 * h) : zp ? (yzp - p.y) / h : (p.y - yzm) / h;
            n = new Vector3((float)-gx, 1, (float)-gz).normalized;
            return true;
        }
    }
}
