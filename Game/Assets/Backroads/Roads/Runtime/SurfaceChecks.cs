using System;
using System.Collections.Generic;
using System.Globalization;

namespace Backroads.Roads
{
    /// <summary>roads-R1 "done when" over a bake (05 §8 items 1 and 2, the kerb-to-building check, the reduced-radius report).</summary>
    public static class SurfaceChecks
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public sealed class Result
        {
            public int surfaces, ringMeshes, earClipped, fallbacks, overrides, triangles;
            public readonly List<string> notSimple = new List<string>();
            public readonly List<string> areaOff = new List<string>();
            public readonly List<string> seams = new List<string>();
            public readonly List<string> kerbInBuilding = new List<string>();
            /// <summary>Kerbs entering a building on a road the importer already flagged: a building within one lane of its centreline (a map conflict).</summary>
            public readonly List<string> knownMapConflicts = new List<string>();
            public readonly List<string> reducedCorners = new List<string>();
            public readonly List<string> fallbackNodes = new List<string>();
            /// <summary>Irregular junctions between 5 % and 15 % from the sweep (reported, allowed).</summary>
            public readonly List<string> irregularAreas = new List<string>();
            public double worstAreaRatio = 1, worstIrregularRatio = 1, maxSeamError;
            public int seamsChecked;
        }

        /// <summary>Why a junction's outline cannot match the sweep closely, or "".</summary>
        public static string Irregular(GraphNode n, NodeSurface ns)
        {
            var why = new List<string>();
            foreach (var c in ns.corners) if (c.kind == CornerKind.Nose) { why.Add("a nose corner"); break; }
            foreach (var c in ns.corners)
                if (c.kind == CornerKind.Fillet && c.reason == CornerReason.BuildingClearance && c.radius < 4.5 - 1e-9) { why.Add("a corner a building forces under 4.5 m"); break; }
            if ((n.flags & NodeFlags.ComplexPair) != 0) why.Add("a complex pair (R4)");
            if ((n.flags & NodeFlags.ShortArm) != 0) why.Add("a short arm");
            if ((n.flags & NodeFlags.AcuteArms) != 0) why.Add("acute arms");
            if (!double.IsNaN(n.bestPairBend) && n.bestPairBend > 0.6) why.Add("roads bending over 35 degrees through it");
            return string.Join(", ", why);
        }

        public static Result Run(RoadGraph g, RoadGraphInput input, RoadSurfaces s, double areaTolerance = 0.05, double irregularTolerance = 0.15)
        {
            var r = new Result();
            var buildings = new List<D2[]>();
            if (input != null) foreach (var f in input.footprints) buildings.Add(f.corners);
            foreach (var ns in s.nodes)
            {
                var n = g.nodes[ns.node];
                if (ns.mode == SurfaceMode.Fallback)
                {
                    r.fallbacks++;
                    r.fallbackNodes.Add(string.Format(Inv, "node {0} ({1}, osm node {2}) at ({3:F0}, {4:F0}): {5}", n.id, n.kind, n.osmNode, n.p.x, n.p.z, ns.problem));
                    continue;
                }
                r.surfaces++;
                if (ns.mode == SurfaceMode.Override) r.overrides++;
                if (ns.ringMesh) r.ringMeshes++; else r.earClipped++;
                r.triangles += ns.triangles.Count / 3;
                string why = "";
                if (Geom2.SignedArea(ns.polygon) <= 0) r.notSimple.Add($"node {n.id}: not counter-clockwise");
                else if (!Geom2.IsSimple(ns.polygon, out why)) r.notSimple.Add($"node {n.id}: {why}");
                // the card's area rule is for junctions; a two-arm node's square outer corner is area no vehicle sweeps.
                // Irregular junctions (a nose corner, a complex pair for R4, a short or acute arm) are held to a looser
                // bound that still catches a broken outline: the sweep and the kerbs legitimately differ there.
                if (ns.corridorArea > 0 && n.Degree >= 3)
                {
                    double ratio = ns.area / ns.corridorArea;
                    string irregular = Irregular(n, ns);
                    string line = string.Format(Inv, "node {0} ({1}, {2} arms) at ({3:F0}, {4:F0}): area {5:F1} m2 vs corridors {6:F1} m2 ({7:+0.0;-0.0} %){8}",
                        n.id, n.kind, n.Degree, n.p.x, n.p.z, ns.area, ns.corridorArea, (ratio - 1) * 100, irregular.Length > 0 ? "; irregular: " + irregular : "");
                    if (irregular.Length == 0)
                    {
                        if (Math.Abs(ratio - 1) > Math.Abs(r.worstAreaRatio - 1)) r.worstAreaRatio = ratio;
                        if (Math.Abs(ratio - 1) > areaTolerance) r.areaOff.Add(line);
                    }
                    else
                    {
                        if (Math.Abs(ratio - 1) > Math.Abs(r.worstIrregularRatio - 1)) r.worstIrregularRatio = ratio;
                        if (Math.Abs(ratio - 1) > irregularTolerance) r.areaOff.Add(line);
                        else if (Math.Abs(ratio - 1) > areaTolerance) r.irregularAreas.Add(line);
                    }
                }
                foreach (var c in ns.corners)
                    if (c.kind == CornerKind.Fillet && c.radius < c.baseRadius - 1e-6 || c.kind == CornerKind.Nose)
                        r.reducedCorners.Add(string.Format(Inv, "node {0} corner {1}: {2} {3:F2} m (class radius {4:F1} m): {5}{6}", n.id, c.armI, c.kind, c.radius, c.baseRadius, Why(c.reason),
                            double.IsNaN(c.clearance) ? "" : string.Format(Inv, "; building clearance {0:F2} m", c.clearance)));
                KerbVsBuildings(g, n, ns, buildings, r);
            }
            Seams(g, s, r);
            foreach (var rb in s.ribbons) RibbonVsBuildings(g, rb, buildings, r);
            return r;
        }

        static string Why(CornerReason k)
        {
            switch (k)
            {
                case CornerReason.MaxTangent: return "acute corner, tangent points kept within 25 m";
                case CornerReason.ShortArm: return "short arm, the corner backs off to fit before the next node";
                case CornerReason.BuildingClearance: return "a building close to the kerb";
                case CornerReason.Override: return "roads-overrides";
                case CornerReason.NoFillet: return "no fillet fits (nose)";
                default: return "class pair";
            }
        }

        /// <summary>Every ribbon end shares its junction's mouth vertices exactly; the height error along each mouth seam.</summary>
        static void Seams(RoadGraph g, RoadSurfaces s, Result r)
        {
            foreach (var rb in s.ribbons)
            {
                var e = g.edges[rb.edge];
                for (int end = 0; end < 2; end++)
                {
                    var ns = s.Of(end == 0 ? e.nodeA : e.nodeB);
                    if (ns == null || ns.mode == SurfaceMode.Fallback) continue;
                    ArmSurface arm = null;
                    foreach (var a in ns.arms) if (a.edge == e.id && a.atA == (end == 0)) arm = a;
                    if (arm?.mouth == null) continue;
                    var row = end == 0 ? rb.stations[0] : rb.stations[rb.stations.Count - 1];
                    int P = row.Length;
                    r.seamsChecked++;
                    if (arm.mouth.Length != P) { r.seams.Add($"edge {e.id} end {end}: ribbon section {P} vertices, mouth {arm.mouth.Length}"); continue; }
                    for (int k = 0; k < P; k++)
                    {
                        var m = arm.mouth[end == 0 ? k : P - 1 - k];
                        var v = row[k];
                        double d = Math.Abs(m.y - v.y);
                        r.maxSeamError = Math.Max(r.maxSeamError, Math.Max(d, Math.Max(Math.Abs(m.x - v.x), Math.Abs(m.z - v.z))));
                        if (m.x != v.x || m.y != v.y || m.z != v.z) r.seams.Add(string.Format(Inv, "edge {0} end {1}: vertex {2} differs from the junction's by {3:E2} m", e.id, end, k, d));
                        // the junction's mesh holds the same vertex
                        bool inMesh = false;
                        foreach (var q in ns.vertices) if (q.x == m.x && q.y == m.y && q.z == m.z) { inMesh = true; break; }
                        if (!inMesh) r.seams.Add($"node {ns.node}: mouth vertex {k} of edge {e.id} is not in the junction mesh");
                    }
                }
            }
        }

        static void KerbVsBuildings(RoadGraph g, GraphNode n, NodeSurface ns, List<D2[]> buildings, Result r)
        {
            // the outline without its mouth lines: the kerb returns
            var mouth = new HashSet<long>();
            foreach (var a in ns.arms) if (a.mouth != null) foreach (var v in a.mouth) mouth.Add(Key(v.XZ));
            var p = ns.polygon;
            bool known = false;
            foreach (var a in ns.arms) known |= g.RoadOf(g.edges[a.edge].sourceRoad)?.buildingConflict == true;
            for (int i = 0; i < p.Count; i++)
            {
                D2 a = p[i], b = p[(i + 1) % p.Count];
                if (mouth.Contains(Key(a)) && mouth.Contains(Key(b))) continue;
                foreach (var bd in buildings)
                    if (Hits(a, b, bd))
                    {
                        string msg = string.Format(Inv, "node {0} at ({1:F0}, {2:F0}): the kerb return enters a building at ({3:F1}, {4:F1})", n.id, n.p.x, n.p.z, a.x, a.z);
                        (known ? r.knownMapConflicts : r.kerbInBuilding).Add(msg);
                        return;
                    }
            }
        }

        static void RibbonVsBuildings(RoadGraph g, RibbonSurface rb, List<D2[]> buildings, Result r)
        {
            var e = g.edges[rb.edge];
            var road = g.RoadOf(e.sourceRoad);
            var f = new RoadFrame(e.pts);
            foreach (int k in new[] { 0, rb.profile.Length - 1 })
                for (int q = 0; q + 1 < rb.stations.Count; q++)
                {
                    D2 a = rb.stations[q][k].XZ, b = rb.stations[q + 1][k].XZ;
                    foreach (var bd in buildings)
                        if (Hits(a, b, bd))
                        {
                            // how far the building really is from the centreline, on this side
                            double near = double.MaxValue;
                            foreach (var v in bd)
                            {
                                f.ToFrame(v, out _, out double u);
                                if ((k == 0 ? -u : u) > 0) near = Math.Min(near, Math.Abs(u));
                            }
                            string msg = string.Format(Inv, "edge {0} (R{1}, {2}{3}): its {4} edge ({5:F2} m out) enters a building at ({6:F1}, {7:F1}) whose nearest corner is {8:F2} m from the centreline",
                                rb.edge, e.sourceRoad, e.cls, road != null && road.inDowntown ? ", downtown" : "", k == 0 ? "right" : "left",
                                Math.Abs(rb.profile[k]), a.x, a.z, near);
                            (road != null && road.buildingConflict ? r.knownMapConflicts : r.kerbInBuilding).Add(msg);
                            return;
                        }
                }
        }

        static bool Hits(D2 a, D2 b, D2[] poly)
        {
            if (TownZone.InPolygon(poly, a) || TownZone.InPolygon(poly, b)) return true;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if (Geom2.SegmentCross(a, b, poly[j], poly[i], out _, out _)) return true;
            return false;
        }

        static long Key(D2 p) => (long)Math.Round(p.x * 1000) * 10000019L + (long)Math.Round(p.z * 1000);

        public static string Summary(Result r) => string.Format(Inv,
            "{0} surfaces ({1} ring meshes, {2} ear-clipped, {3} overrides), {4} fallbacks, {5} triangles; not simple {6}; junction areas off {7} (regular worst {8:+0.0;-0.0} %, irregular worst {9:+0.0;-0.0} %, {10} irregular over 5 %); seams {11} checked, {12} problems, worst {13:E1} m; kerbs in buildings {14} (+{15} known map conflicts); corners reduced {16}",
            r.surfaces, r.ringMeshes, r.earClipped, r.overrides, r.fallbacks, r.triangles, r.notSimple.Count, r.areaOff.Count, (r.worstAreaRatio - 1) * 100,
            (r.worstIrregularRatio - 1) * 100, r.irregularAreas.Count, r.seamsChecked, r.seams.Count, r.maxSeamError, r.kerbInBuilding.Count, r.knownMapConflicts.Count, r.reducedCorners.Count);
    }
}
