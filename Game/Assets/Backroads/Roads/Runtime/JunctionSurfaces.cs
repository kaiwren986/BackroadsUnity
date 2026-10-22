using System;
using System.Collections.Generic;
using System.Globalization;

namespace Backroads.Roads
{
    /// <summary>How a node's surface gets its heights (05 §5.6) or why it has none.</summary>
    public enum SurfaceMode : byte
    {
        None = 0,
        /// <summary>The major road's profile carried through with its crown; the other arms warp onto it.</summary>
        Major = 1,
        /// <summary>No major road: a least-squares plane through the mouth centres, clamped to 3 %.</summary>
        Plane = 2,
        /// <summary>A two-arm node (bend, join, class change): the road carried through.</summary>
        Through = 3,
        /// <summary>A hand-authored polygon from roads-overrides.</summary>
        Override = 4,
        /// <summary>No surface: the ribbons run to the node and overlap (the last resort, always reported).</summary>
        Fallback = 5,
        /// <summary>roads-R4: a roundabout (RoundaboutBuilder's plan): the plane through the mouths, the ring falling 2 % outward.</summary>
        Roundabout = 6,
    }

    /// <summary>Why a corner has the radius it has (the report lists every reduced corner and why).</summary>
    public enum CornerReason : byte { ClassPair = 0, MaxTangent = 1, ShortArm = 2, BuildingClearance = 3, Override = 4, NoFillet = 5, Roundabout = 6 }

    /// <summary>The R1 numbers: 05 §5.2-§5.6 and the card's CORNER_R.</summary>
    public sealed class SurfaceSettings
    {
        /// <summary>Kerb radii (05 §5.3, CORNER_R): street corners, then the downtown steps; two through roads; rural and service corners; L-bend inner corners.</summary>
        public double rStreet = 7.5, rDowntown = 6.0, rDowntownMin = 4.5, rThrough = 12.0, rRural = 9.0, rBendInner = 6.0, rMin = 2.0;
        /// <summary>roads-R4 step 5: an alley meets a street like a wide driveway, with small kerb returns.</summary>
        public double rAlley = 3.0;
        /// <summary>A 7.5 m kerb passing closer than this to a building steps down to 6 then 4.5 m; closer than clearanceMin is reported.</summary>
        public double clearanceWanted = 2.5, clearanceMin = 1.0, clearanceReach = 40.0;
        public double maxTangent = 25.0, backOff = 0.8;
        public int attempts = 6;
        public double mouthPad = 0.5, minMouth = 1.0, throughMouth = 1.5, arcStepDeg = 7.5, weld = 0.02;
        /// <summary>05 §5.4: crosswalk width, the stop line's width and its distances (4 ft behind a crosswalk; 4-30 ft from the intersected edge).</summary>
        public double crosswalk = 3.0, stopLine = 0.45, stopBehindCrosswalk = 1.22, stopMinFromEdge = 1.22, stopMaxFromEdge = 9.14, crosswalkFromEdge = 0.6;
        /// <summary>An approach steeper than this within 10 m of the stop line moves it to the flattest spot within +4 m.</summary>
        public double steepApproach = 0.06, flatSearch = 4.0;
        /// <summary>05 §5.6: warp length, crowns, the gutter pan, the clamp beyond the kerb line, the plane's largest slope.</summary>
        public double warp = 12.0, crownPaved = 0.02, crownGravel = 0.04, gutterPan = 0.45, gutterDrop = 0.027, beyondClamp = 0.15, planeMaxSlope = 0.03;
        /// <summary>Curb inlets: upstream of the kerb return tangent on uphill approaches, at flowline low points, every 90 m on grades.</summary>
        public double inletUpstream = 1.5, inletSpacing = 90.0;
        /// <summary>roads-R4 step 3 (05 §5.12): a gravel or dirt road meeting a paved junction is paved this far from the mouth.</summary>
        public double apron = 12.0;
        /// <summary>roads-R4 step 1: the roundabout generator's numbers.</summary>
        public RoundaboutSettings roundabout = new RoundaboutSettings();
    }

    /// <summary>One arm of a node with a surface, in its own frame (s outward from the node, +u = left of outward).</summary>
    public sealed class ArmSurface
    {
        public int arm, edge, node;
        public bool atA;
        public RoadClass cls;
        public RoadFrame frame;
        public double hwL, hwR;
        public bool kerbed, gravel;
        /// <summary>A service road tagged service=alley (roads-R4): small corners, no STOP, no marked crosswalk, a dropped-kerb mouth.</summary>
        public bool alley;
        public double maxMouth;
        /// <summary>Where the left and right kerbs leave this arm for their corners (0 for straight corners).</summary>
        public double tangentL, tangentR;
        public double sT, sEdge = double.NaN, sMouth;
        /// <summary>Stop line centre (or -1), crosswalk band (or -1).</summary>
        public double sStop = -1, sCwInner = -1, sCwOuter = -1;
        public bool crosswalk, stopMoved;
        public Control control;
        /// <summary>Lateral positions across the mouth, right to left, and the mouth vertices there (shared with the ribbon end).</summary>
        public double[] profile;
        public D3[] mouth;
    }

    public sealed class CornerSurface
    {
        public int node;
        /// <summary>Positions in the node's arm list: armJ is the next counter-clockwise.</summary>
        public int armI, armJ;
        public CornerKind kind;
        public double baseRadius, radius;
        public CornerReason reason;
        /// <summary>Where the kerb meets each arm (fillet tangents, miter or nose points), on I's left and J's right.</summary>
        public double sI, sJ;
        public D2 centre;
        public readonly List<D2> arc = new List<D2>();
        /// <summary>Signed distance from the solved kerb to the nearest building in the corner's block (negative = inside), NaN when none.</summary>
        public double clearance = double.NaN;
    }

    public sealed class NodeSurface
    {
        public int node;
        public SurfaceMode mode;
        public readonly List<ArmSurface> arms = new List<ArmSurface>();
        public readonly List<CornerSurface> corners = new List<CornerSurface>();
        /// <summary>The surface outline, CCW, welded; empty for Fallback.</summary>
        public List<D2> polygon = new List<D2>();
        /// <summary>The mesh: vertices with heights and triangles in Unity's winding (clockwise seen from above = facing up).</summary>
        public readonly List<D3> vertices = new List<D3>();
        public readonly List<int> triangles = new List<int>();
        public bool ringMesh;
        public double area, corridorArea;
        /// <summary>Plane mode: y = a x + b z + c.</summary>
        public double planeA, planeB, planeC;
        /// <summary>Major and Through modes: the through road's frame (arm majorA reversed, then arm majorB) and which arms.</summary>
        public RoadFrame through;
        public double throughNodeS;
        public int majorArmA = -1, majorArmB = -1;
        public string problem = "";
        /// <summary>roads-R4: the plan of a Roundabout node (mode Roundabout), else null.</summary>
        public RoundaboutGeometry roundabout;
    }

    /// <summary>A road between two mouths: stations along the edge, each a row of vertices right to left across the road.</summary>
    public sealed class RibbonSurface
    {
        public int edge;
        public double s0, s1;
        public bool kerbed;
        /// <summary>roads-R4: metres of paved apron from each end of an unpaved road (0: none), measured from s0 and back from s1.</summary>
        public double apronA, apronB;
        /// <summary>Lateral positions right to left in the edge's frame.</summary>
        public double[] profile;
        public readonly List<double> stationS = new List<double>();
        public readonly List<D3[]> stations = new List<D3[]>();
    }

    public sealed class Inlet
    {
        public D3 p;
        public int edge = -1, node = -1;
        public string why;
    }

    public sealed class RoadSurfaces
    {
        public readonly List<NodeSurface> nodes = new List<NodeSurface>();
        public readonly List<RibbonSurface> ribbons = new List<RibbonSurface>();
        public readonly List<Inlet> inlets = new List<Inlet>();
        public readonly List<string> notes = new List<string>();
        readonly Dictionary<int, NodeSurface> m_ByNode = new Dictionary<int, NodeSurface>();

        public NodeSurface Of(int node) => m_ByNode.TryGetValue(node, out var n) ? n : null;
        internal void Add(NodeSurface n) { nodes.Add(n); m_ByNode[n.node] = n; }
    }

    /// <summary>A hand-authored fix for one node (roads-overrides, keyed by OSM node id).</summary>
    public sealed class NodeOverride
    {
        public long osmNode;
        /// <summary>Force every corner of the node to this kerb radius (NaN: no).</summary>
        public double radius = double.NaN;
        /// <summary>A hand-drawn surface outline (CCW, local metres), or null.</summary>
        public D2[] polygon;
        /// <summary>Give up on a surface: the ribbons run to the node (reported).</summary>
        public bool ribbons;
        public string reason = "";
    }

    /// <summary>
    /// roads-R1: junction surfaces from the R0o graph (05 §5.2-§5.6, §5.12). For every node with two or more arms it builds
    /// the arms' edge lines from their cross sections (left and right separately), solves each corner (polyline fillets
    /// with the radius back-off, class radii, the building clearance step-down, miters outside L-bends), places the
    /// mouths, stop lines and crosswalk bands, assembles the surface polygon (every mouth vertex shared with its
    /// ribbon), triangulates it (a ring mesh when star-shaped, else ear clipping) and gives it heights: the major road's
    /// profile carried through with its crown, or a least-squares plane clamped to 3 %. Ribbons between the mouths carry
    /// the crown and the gutter pan and warp onto each junction over 12 m. Deterministic, double precision, plain C#.
    /// </summary>
    public static class SurfaceBuilder
    {
        public const int Version = 2;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static RoadSurfaces Build(RoadGraph g, RoadGraphInput input, IReadOnlyDictionary<long, NodeOverride> overrides = null, SurfaceSettings st = null)
        {
            st = st ?? new SurfaceSettings();
            var res = new RoadSurfaces();
            var frames = new RoadFrame[g.edges.Count];
            foreach (var e in g.edges) frames[e.id] = new RoadFrame(e.pts);
            var buildings = new List<D2[]>();
            if (input != null) foreach (var f in input.footprints) buildings.Add(f.corners);

            foreach (var n in g.nodes)
            {
                if (n.Degree < 2) continue;
                NodeOverride ov = null;
                if (overrides != null && n.osmNode != 0) overrides.TryGetValue(n.osmNode, out ov);
                res.Add(BuildNode(g, n, frames, buildings, ov, st, res.notes));
            }
            foreach (var e in g.edges) { var r = BuildRibbon(g, e, frames[e.id], res, st); if (r != null) res.ribbons.Add(r); }
            Aprons(g, res, st);
            Inlets(g, res, frames, st);
            return res;
        }

        // ------------------------------------------------------------------ arms

        static ArmSurface MakeArm(RoadGraph g, GraphNode n, int k, RoadFrame[] frames, SurfaceSettings st)
        {
            var a = g.Arm(n, k);
            var e = g.edges[a.edge];
            var road = g.RoadOf(e.sourceRoad);
            double eL = road?.xs?.hwLeft ?? e.hw, eR = road?.xs?.hwRight ?? e.hw;
            var s = new ArmSurface
            {
                arm = a.id, edge = e.id, node = n.id, atA = a.atA, cls = e.cls,
                frame = a.atA ? frames[e.id] : frames[e.id].Reversed(),
                hwL = a.atA ? eL : eR, hwR = a.atA ? eR : eL,
                kerbed = KerbRules.Kerbed(road, e.cls),
                gravel = e.cls == RoadClass.Dirt, control = a.control, alley = road?.service == "alley",
            };
            var far = g.nodes[g.FarNode(a)];
            double L = e.Length;
            if (far.Degree >= 3 || far.kind == NodeKind.Bend) s.maxMouth = Math.Min(0.45 * L, L - 10);
            else if (far.Degree == 2) s.maxMouth = L - st.throughMouth - 1;
            else s.maxMouth = L - 1;
            return s;
        }

        /// <summary>Lateral positions right to left for a road with these half-widths (the gutter pan lines on kerbed roads).</summary>
        public static double[] Profile(double hwL, double hwR, bool kerbed, double pan)
        {
            if (kerbed && hwL > 2 * pan && hwR > 2 * pan) return new[] { -hwR, -hwR + pan, 0, hwL - pan, hwL };
            return new[] { -hwR, 0, hwL };
        }

        // ------------------------------------------------------------------ one node

        static NodeSurface BuildNode(RoadGraph g, GraphNode n, RoadFrame[] frames, List<D2[]> buildings, NodeOverride ov, SurfaceSettings st, List<string> notes)
        {
            var ns = new NodeSurface { node = n.id };
            for (int k = 0; k < n.Degree; k++) ns.arms.Add(MakeArm(g, n, k, frames, st));
            bool through = n.Degree == 2;
            ns.majorArmA = through ? 0 : n.majorA;
            ns.majorArmB = through ? 1 : n.majorB;

            if (ov != null && ov.ribbons) return Fallback(ns, "roads-overrides: ribbons (" + ov.reason + ")", notes, n);
            foreach (var a in ns.arms)
                if (a.maxMouth < st.minMouth + st.mouthPad)
                    return ov?.polygon != null ? OverridePolygon(g, n, ns, ov, st, notes) : Fallback(ns, string.Format(Inv, "arm R{0} has {1:F1} m of room (another node too close)", g.edges[a.edge].sourceRoad, a.maxMouth), notes, n);
            if (ov?.polygon != null) return OverridePolygon(g, n, ns, ov, st, notes);
            if (n.kind == NodeKind.Roundabout && n.roundabout >= 0 && n.roundabout < g.roundabouts.Count) return RoundaboutNode(g, n, ns, st, notes);

            // corners
            for (int i = 0; i < n.Degree; i++)
            {
                var I = ns.arms[i];
                var J = ns.arms[(i + 1) % n.Degree];
                ns.corners.Add(SolveCorner(g, n, i, (i + 1) % n.Degree, I, J, buildings, ov, st));
            }
            // tangents, mouths, stop lines, crosswalks
            for (int i = 0; i < n.Degree; i++)
            {
                var c = ns.corners[i];
                var I = ns.arms[c.armI];
                var J = ns.arms[c.armJ];
                if (c.kind != CornerKind.Straight || c.sI >= 0)
                {
                    I.tangentL = Math.Max(I.tangentL, c.sI);
                    J.tangentR = Math.Max(J.tangentR, c.sJ);
                }
            }
            foreach (var a in ns.arms)
            {
                a.sT = Math.Max(a.tangentL, a.tangentR);
                a.sMouth = through && n.kind != NodeKind.Bend ? st.throughMouth : Math.Max(a.sT + st.mouthPad, st.minMouth);
                if (a.sMouth > a.maxMouth)
                {
                    notes.Add(string.Format(Inv, "node {0}: arm R{1} mouth {2:F1} m beyond its room {3:F1} m; kept at the room", n.id, g.edges[a.edge].sourceRoad, a.sMouth, a.maxMouth));
                    a.sMouth = Math.Max(st.minMouth, a.maxMouth);
                }
            }
            if (!through)
                foreach (var a in ns.arms) EdgeStopCrosswalk(g, n, ns, a, st);

            // heights first (the mouth vertices need them), then the outline
            Heights(n, ns, st);
            foreach (var a in ns.arms) MouthLine(ns, a, st);
            if (!Outline(ns, st, out string why))
            {
                if (ov?.polygon != null) return OverridePolygon(g, n, ns, ov, st, notes);
                return Fallback(ns, "the solved outline is not simple: " + why, notes, n);
            }
            Mesh(n, ns, st);
            ns.corridorArea = CorridorArea(ns);
            return ns;
        }

        static NodeSurface Fallback(NodeSurface ns, string why, List<string> notes, GraphNode n)
        {
            ns.mode = SurfaceMode.Fallback;
            ns.problem = why;
            ns.corners.Clear();
            ns.polygon.Clear();
            foreach (var a in ns.arms) { a.sMouth = 0; a.mouth = null; }
            notes.Add(string.Format(Inv, "node {0} ({1}, osm node {2}): no surface, ribbons overlap: {3}", n.id, n.kind, n.osmNode, why));
            return ns;
        }

        // ------------------------------------------------------------------ corners (05 §5.3)

        static CornerSurface SolveCorner(RoadGraph g, GraphNode n, int i, int j, ArmSurface I, ArmSurface J, List<D2[]> buildings, NodeOverride ov, SurfaceSettings st)
        {
            var aI = g.arms[I.arm];
            var aJ = g.arms[J.arm];
            double theta = Geo.WrapPositive(aJ.angle - aI.angle);
            if (n.Degree == 2 && theta == 0) theta = 2 * Math.PI;
            var c = new CornerSurface { node = n.id, armI = i, armJ = j };
            double reach = Math.Min(I.maxMouth, J.maxMouth) + 30;

            // a join or class change: both sides taper straight from mouth to mouth
            if (n.Degree == 2 && n.kind != NodeKind.Bend)
            {
                c.kind = CornerKind.Straight;
                return c;
            }
            if (theta >= 200 * Geo.Deg)
            {
                // reflex (the outside of an L-bend): the kerb lines meet in a miter
                c.kind = CornerKind.Miter;
                if (I.frame.FirstCrossing(I.hwL, J.frame, -J.hwR, reach, reach, out double sa, out double sb, out D2 p))
                {
                    c.sI = sa; c.sJ = sb; c.centre = p;
                    return c;
                }
                // the usual case: the outer kerb lines meet behind the node; extend their first segments to the square corner
                D2 a0 = I.frame.P(0, I.hwL), a1 = I.frame.P(Math.Min(1, I.frame.Length), I.hwL);
                D2 b0 = J.frame.P(0, -J.hwR), b1 = J.frame.P(Math.Min(1, J.frame.Length), -J.hwR);
                D2 da = a1 - a0, db = b1 - b0;
                double den = da.Cross(db);
                if (Math.Abs(den) > 1e-9)
                {
                    double t = (b0 - a0).Cross(db) / den;
                    var q = a0 + da * t;
                    if (D2.Distance(q, n.p.XZ) < 4 * Math.Max(I.hwL, J.hwR))
                    {
                        c.sI = c.sJ = 0;
                        c.centre = q;
                        return c;
                    }
                }
                c.kind = CornerKind.Straight;
                c.sI = c.sJ = -1;
                return c;
            }
            if (theta >= 160 * Geo.Deg)
            {
                // the through side: when the road bends at the node, its kerb lines cross near it; the outline turns
                // there (otherwise the offsets at the node would make a small bow-tie)
                c.kind = CornerKind.Straight;
                c.sI = c.sJ = -1;
                if (I.frame.FirstCrossing(I.hwL, J.frame, -J.hwR, reach, reach, out double sa, out double sb, out D2 p) && sa < I.maxMouth && sb < J.maxMouth)
                {
                    c.sI = sa; c.sJ = sb; c.centre = p;
                }
                return c;
            }

            bool clearanceRule;
            double r = BaseRadius(n, I, J, st, out clearanceRule);
            if (ov != null && !double.IsNaN(ov.radius)) { r = ov.radius; clearanceRule = false; c.reason = CornerReason.Override; }
            c.baseRadius = r;
            double sin = Math.Sin(theta), cot = Math.Cos(theta) / sin, k1 = 1 / sin + cot;
            double tI0 = J.hwR / sin + I.hwL * cot, tJ0 = I.hwL / sin + J.hwR * cot;
            double rMax = (st.maxTangent - Math.Max(tI0, tJ0)) / k1;
            if (r > rMax && c.reason != CornerReason.Override) { r = Math.Max(rMax, 0.5); c.reason = CornerReason.MaxTangent; }

            if (!Fillet(I, J, r, n, st, c, out double used, out CornerReason backReason))
            {
                Nose(I, J, reach, st, c);
                return c;
            }
            if (used < r - 1e-9 && c.reason == CornerReason.ClassPair) c.reason = backReason;

            if (buildings.Count > 0)
            {
                c.clearance = Clearance(n, I, J, c, buildings, st);
                if (clearanceRule)
                    foreach (var step in new[] { st.rDowntown, st.rDowntownMin })
                    {
                        if (!(c.clearance < st.clearanceWanted) || c.radius <= step) continue;
                        var c2 = new CornerSurface { node = n.id, armI = i, armJ = j, baseRadius = c.baseRadius };
                        if (!Fillet(I, J, step, n, st, c2, out _, out _)) break;
                        c2.clearance = Clearance(n, I, J, c2, buildings, st);
                        c2.reason = CornerReason.BuildingClearance;
                        c = c2;
                    }
                // a kerb inside a building: shrink further, whatever the class
                while (c.clearance < 0 && c.radius * st.backOff >= st.rMin)
                {
                    var c2 = new CornerSurface { node = n.id, armI = i, armJ = j, baseRadius = c.baseRadius };
                    if (!Fillet(I, J, c.radius * st.backOff, n, st, c2, out _, out _)) break;
                    c2.clearance = Clearance(n, I, J, c2, buildings, st);
                    c2.reason = CornerReason.BuildingClearance;
                    c = c2;
                }
            }
            return c;
        }

        static double BaseRadius(GraphNode n, ArmSurface I, ArmSurface J, SurfaceSettings st, out bool clearanceRule)
        {
            clearanceRule = false;
            if (n.kind == NodeKind.Bend) return st.rBendInner;
            if (I.alley || J.alley) return st.rAlley;
            RoadClass a = I.cls, b = J.cls;
            if (a == RoadClass.Dirt || b == RoadClass.Dirt || a == RoadClass.Yard || b == RoadClass.Yard) return st.rRural;
            if (RoadClasses.IsThroughRoad(a) && RoadClasses.IsThroughRoad(b)) return st.rThrough;
            if (a == RoadClass.Highway || b == RoadClass.Highway) return st.rRural;
            clearanceRule = true;
            return st.rStreet;
        }

        /// <summary>The polyline fillet of 05 §5.3 with the radius back-off (x 0.8 when the offsets do not cross or the arm is too short).</summary>
        static bool Fillet(ArmSurface I, ArmSurface J, double r0, GraphNode n, SurfaceSettings st, CornerSurface c, out double used, out CornerReason why)
        {
            double r = r0;
            why = CornerReason.ClassPair;
            used = r;
            for (int attempt = 0; attempt < st.attempts; attempt++, r *= st.backOff)
            {
                double limI = I.maxMouth - st.mouthPad, limJ = J.maxMouth - st.mouthPad;
                if (!I.frame.FirstCrossing(I.hwL + r, J.frame, -(J.hwR + r), limI + r + 5, limJ + r + 5, out double sa, out double sb, out D2 C))
                {
                    why = CornerReason.NoFillet;
                    continue;
                }
                if (sa > limI || sb > limJ) { why = CornerReason.ShortArm; continue; }
                c.kind = CornerKind.Fillet;
                c.radius = r;
                c.sI = sa;
                c.sJ = sb;
                c.centre = C;
                c.arc.Clear();
                D2 tI = I.frame.P(sa, I.hwL), tJ = J.frame.P(sb, -J.hwR);
                double a0 = (tI - C).Angle, a1 = (tJ - C).Angle;
                double ccw = Geo.WrapPositive(a1 - a0), cw = ccw - 2 * Math.PI;
                var node = n.p.XZ;
                double dCcw = D2.Distance(C + D2.FromAngle(a0 + ccw / 2) * r, node), dCw = D2.Distance(C + D2.FromAngle(a0 + cw / 2) * r, node);
                double sweep = dCcw <= dCw ? ccw : cw;
                int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (st.arcStepDeg * Geo.Deg)));
                c.arc.Add(tI);
                for (int k = 1; k < steps; k++) c.arc.Add(C + D2.FromAngle(a0 + sweep * k / steps) * r);
                c.arc.Add(tJ);
                used = r;
                return true;
            }
            return false;
        }

        /// <summary>The degenerate fallback (05 §5.3): the kerb lines' crossing, chamfered 0.6 m.</summary>
        static void Nose(ArmSurface I, ArmSurface J, double reach, SurfaceSettings st, CornerSurface c)
        {
            c.kind = CornerKind.Nose;
            c.reason = CornerReason.NoFillet;
            c.radius = 0;
            if (I.frame.FirstCrossing(I.hwL, J.frame, -J.hwR, reach, reach, out double sa, out double sb, out D2 p))
            {
                c.sI = Math.Min(I.maxMouth - st.mouthPad, sa + 0.6);
                c.sJ = Math.Min(J.maxMouth - st.mouthPad, sb + 0.6);
                c.centre = p;
                c.arc.Add(I.frame.P(c.sI, I.hwL));
                c.arc.Add(J.frame.P(c.sJ, -J.hwR));
            }
            else
            {
                c.sI = Math.Min(I.maxMouth - st.mouthPad, I.hwL + J.hwR);
                c.sJ = Math.Min(J.maxMouth - st.mouthPad, I.hwL + J.hwR);
                c.arc.Add(I.frame.P(c.sI, I.hwL));
                c.arc.Add(J.frame.P(c.sJ, -J.hwR));
            }
        }

        /// <summary>
        /// The solved kerb's clearance from buildings in the corner's block (05 §1.3's measure on the real kerb): the
        /// smallest distance from I's left kerb (reach metres out to the tangent), the arc and J's right kerb to a building
        /// outline in the wedge; negative when the kerb enters a building. NaN when no building is near.
        /// </summary>
        static double Clearance(GraphNode n, ArmSurface I, ArmSurface J, CornerSurface c, List<D2[]> buildings, SurfaceSettings st)
        {
            var kerb = new List<D2>();
            double reach = st.clearanceReach;
            var left = I.frame.Offset(I.hwL);
            for (int k = I.frame.Count - 1; k >= 0; k--)
                if (I.frame.s[k] > c.sI && I.frame.s[k] <= c.sI + reach) kerb.Add(left[k]);
            kerb.AddRange(c.arc);
            var right = J.frame.Offset(-J.hwR);
            for (int k = 0; k < J.frame.Count; k++)
                if (J.frame.s[k] > c.sJ && J.frame.s[k] <= c.sJ + reach) kerb.Add(right[k]);
            var node = n.p.XZ;
            D2 dI = I.frame.Dir(0), dJ = J.frame.Dir(0);
            double best = double.NaN;
            foreach (var b in buildings)
            {
                bool near = false, wedge = false;
                foreach (var q in b)
                {
                    var v = q - node;
                    if (v.Length < reach + 15) near = true;
                    if (dI.Cross(v) > 0 && dJ.Cross(v) < 0) wedge = true;
                }
                if (!near || !wedge) continue;
                double d = double.MaxValue;
                bool inside = false;
                for (int k = 0; k < kerb.Count; k++)
                {
                    if (TownZone.InPolygon(b, kerb[k])) { inside = true; d = Math.Min(d, -Geom2.DistanceToBoundary(b, kerb[k])); continue; }
                    if (inside) continue;
                    d = Math.Min(d, Geom2.DistanceToBoundary(b, kerb[k]));
                    if (k + 1 < kerb.Count)
                        for (int e = 0, f = b.Length - 1; e < b.Length; f = e++)
                        {
                            d = Math.Min(d, Polyline.SegmentDistance(kerb[k], kerb[k + 1], b[e]));
                            if (Geom2.SegmentCross(kerb[k], kerb[k + 1], b[f], b[e], out _, out _)) { inside = true; d = Math.Min(d, -0.01); }
                        }
                }
                if (double.IsNaN(best) || d < best) best = d;
            }
            return best;
        }

        // ------------------------------------------------------------------ 05 §5.4: the intersected edge, stop lines, crosswalks

        static void EdgeStopCrosswalk(RoadGraph g, GraphNode n, NodeSurface ns, ArmSurface a, SurfaceSettings st)
        {
            double sEdge = double.NaN;
            foreach (var b in ns.arms)
            {
                if (b == a) continue;
                foreach (var u in new[] { b.hwL, -b.hwR })
                    if (a.frame.FirstCrossing(0, b.frame, u, a.maxMouth + 5, 60, out double sa, out _, out _) && (double.IsNaN(sEdge) || sa < sEdge)) sEdge = sa;
            }
            a.sEdge = sEdge;
            double edge = double.IsNaN(sEdge) ? 0 : sEdge;
            a.crosswalk = MarkedCrosswalk(g, n, a);
            if (a.crosswalk)
            {
                a.sCwOuter = Math.Max(a.sT, edge + st.crosswalkFromEdge + st.crosswalk);
                a.sCwInner = a.sCwOuter - st.crosswalk;
            }
            bool stopLine = a.control == Control.MinorStop || a.control == Control.AllWayStop || a.control == Control.Signal;
            if (!stopLine) return;
            double s = a.crosswalk ? a.sCwOuter + st.stopBehindCrosswalk + st.stopLine / 2
                : Math.Min(Math.Max(a.sT + 0.3, edge + st.stopMinFromEdge), edge + st.stopMaxFromEdge);
            double limit = a.frame.Length - 1;
            // 05 §5.4 / §5.6: on a steep approach the stop line goes to the flattest spot within +4 m
            double steep = 0;
            for (double q = Math.Max(0, s - 10); q <= Math.Min(limit, s + 10); q += 0.5) steep = Math.Max(steep, Math.Abs(a.frame.Grade(q)));
            if (steep > st.steepApproach)
            {
                double best = s, bestG = Math.Abs(a.frame.Grade(s));
                for (double q = s; q <= Math.Min(limit, s + st.flatSearch) + 1e-9; q += 0.25)
                {
                    double gq = Math.Abs(a.frame.Grade(q));
                    if (gq < bestG - 1e-9) { bestG = gq; best = q; }
                }
                a.stopMoved = best != s;
                s = best;
            }
            a.sStop = Math.Min(s, limit);
        }

        static bool MarkedCrosswalk(RoadGraph g, GraphNode n, ArmSurface a)
        {
            foreach (var f in g.features)
                if (f.kind == RoadFeatureKind.Crossing && f.node == n.id && f.arm == a.arm &&
                    (f.crossing == "marked" || f.crossing == "zebra" || f.crossing == "traffic_signals" || f.crossing == "uncontrolled")) return true;
            // 05 §5.8: marked at junctions of a main street downtown and at signals (never across an alley mouth: the walk
            // crosses it at the dropped kerb, roads-R4)
            if (a.alley) return false;
            if (n.control == Control.Signal) return true;
            if (g.downtown == null || g.downtown.Length < 3 || !TownZone.InPolygon(g.downtown, n.p.XZ) || n.Degree < 3) return false;
            foreach (var id in n.arms) if (g.arms[id].cls == RoadClass.MainStreet) return true;
            return false;
        }

        // ------------------------------------------------------------------ 05 §5.6 heights

        static void Heights(GraphNode n, NodeSurface ns, SurfaceSettings st)
        {
            if (ns.majorArmA >= 0 && ns.majorArmB >= 0)
            {
                ns.mode = n.Degree == 2 ? SurfaceMode.Through : SurfaceMode.Major;
                var A = ns.arms[ns.majorArmA];
                var B = ns.arms[ns.majorArmB];
                var pts = new List<D3>();
                // arm A from up to 80 m out back to (not including) the node, then arm B from the node out to 80 m
                var ra = A.frame;
                int ka = 1;
                while (ka < ra.Count - 1 && ra.s[ka] < 80) ka++;
                for (int k = ka; k >= 1; k--) pts.Add(new D3(ra.c[k].x, ra.y[k], ra.c[k].z));
                int nodeIndex = pts.Count;
                var rb = B.frame;
                int kb = 1;
                while (kb < rb.Count - 1 && rb.s[kb] < 80) kb++;
                for (int k = 0; k <= kb; k++) pts.Add(new D3(rb.c[k].x, rb.y[k], rb.c[k].z));
                ns.through = new RoadFrame(pts);
                ns.throughNodeS = ns.through.s[nodeIndex];
                return;
            }
            ns.mode = SurfaceMode.Plane;
            // least-squares plane through the arms' centre points at their mouths
            double sx = 0, sz = 0, sy = 0, sxx = 0, szz = 0, sxz = 0, sxy = 0, szy = 0;
            int m = 0;
            foreach (var a in ns.arms)
            {
                var p = a.frame.Centre(a.sMouth);
                double y = a.frame.Y(a.sMouth);
                double x = p.x - n.p.x, z = p.z - n.p.z;
                sx += x; sz += z; sy += y; sxx += x * x; szz += z * z; sxz += x * z; sxy += x * y; szy += z * y; m++;
            }
            double mx = sx / m, mz = sz / m, my = sy / m;
            double cxx = sxx / m - mx * mx, czz = szz / m - mz * mz, cxz = sxz / m - mx * mz, cxy = sxy / m - mx * my, czy = szy / m - mz * my;
            double det = cxx * czz - cxz * cxz;
            double ga = 0, gb = 0;
            if (Math.Abs(det) > 1e-9) { ga = (cxy * czz - czy * cxz) / det; gb = (czy * cxx - cxy * cxz) / det; }
            double slope = Math.Sqrt(ga * ga + gb * gb);
            if (slope > st.planeMaxSlope) { ga *= st.planeMaxSlope / slope; gb *= st.planeMaxSlope / slope; }
            ns.planeA = ga;
            ns.planeB = gb;
            ns.planeC = my - ga * (mx + n.p.x) - gb * (mz + n.p.z);
        }

        /// <summary>The surface height at a plan point (05 §5.6 JunctionHeight).</summary>
        public static double JunctionHeight(NodeSurface ns, D2 q, SurfaceSettings st)
        {
            if (ns.mode == SurfaceMode.Roundabout && ns.roundabout != null)
            {
                // the plane through the mouths; the circulatory roadway falls outward from the island (05 §5.11)
                var G = ns.roundabout;
                double r = D2.Distance(q, G.c);
                return ns.planeA * q.x + ns.planeB * q.z + ns.planeC + st.roundabout.outwardSlope * (G.Ro - Math.Max(G.Ri, Math.Min(G.Ro, r)));
            }
            if (ns.mode == SurfaceMode.Plane || ns.mode == SurfaceMode.Override || ns.through == null)
                return ns.planeA * q.x + ns.planeB * q.z + ns.planeC;
            ns.through.ToFrame(q, out double s, out double u);
            // the side of the node decides which arm's section applies; on arm A (s before the node) left and right swap
            bool onA = s < ns.throughNodeS;
            var arm = onA ? ns.arms[ns.majorArmA] : ns.arms[ns.majorArmB];
            double hwLeft = onA ? arm.hwR : arm.hwL, hwRight = onA ? arm.hwL : arm.hwR;
            // roads-R4 step 4: where a kerbed road meets an unkerbed one the gutter pan runs out (the kerbed ribbon warps onto this)
            bool kerbed = ns.arms[ns.majorArmA].kerbed && ns.arms[ns.majorArmB].kerbed;
            return ns.through.Y(s) + Crown(u, hwLeft, hwRight, kerbed, arm.gravel, st);
        }

        /// <summary>The cross slope at lateral offset u (05 §5.6): crown, the gutter pan on kerbed roads, the last slope continued beyond the edge clamped to 0.15 m.</summary>
        public static double Crown(double u, double hwL, double hwR, bool kerbed, bool gravel, SurfaceSettings st)
        {
            double c = gravel ? st.crownGravel : st.crownPaved;
            double hw = u >= 0 ? hwL : hwR, a = Math.Abs(u);
            bool pan = kerbed && hw > 2 * st.gutterPan;
            double flat = pan ? hw - st.gutterPan : hw;
            if (a <= flat) return -c * a;
            double atFlat = -c * flat;
            double slope = pan ? st.gutterDrop / st.gutterPan : c;
            if (a <= hw) return atFlat - slope * (a - flat);
            double atEdge = pan ? atFlat - st.gutterDrop : atFlat;
            return atEdge - Math.Min(slope * (a - hw), st.beyondClamp);
        }

        /// <summary>The mouth line across an arm at sMouth, right to left, with junction heights: these vertices are shared with the ribbon.</summary>
        static void MouthLine(NodeSurface ns, ArmSurface a, SurfaceSettings st)
        {
            a.profile = Profile(a.hwL, a.hwR, a.kerbed, st.gutterPan);
            a.mouth = new D3[a.profile.Length];
            for (int k = 0; k < a.profile.Length; k++)
            {
                var p = a.frame.P(a.sMouth, a.profile[k]);
                a.mouth[k] = new D3(p.x, JunctionHeight(ns, p, st), p.z);
            }
        }

        // ------------------------------------------------------------------ 05 §5.5 outline and mesh

        struct OutlinePoint
        {
            public D2 p;
            public bool mouth;
            public D3 v;
        }

        static bool Outline(NodeSurface ns, SurfaceSettings st, out string why)
        {
            var P = new List<OutlinePoint>();
            int n = ns.arms.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ns.arms[i];
                foreach (var v in a.mouth) P.Add(new OutlinePoint { p = v.XZ, mouth = true, v = v });
                var c = ns.corners[i];
                var b = ns.arms[(i + 1) % n];
                // two-arm joins and class changes taper straight from mouth to mouth (05 §5.12: the kerb taper)
                if (n == 2 && c.kind == CornerKind.Straight) continue;
                bool crossing = c.kind == CornerKind.Straight && c.sI >= 0;
                double stopA = c.kind == CornerKind.Straight && !crossing ? 0 : c.sI, startB = c.kind == CornerKind.Straight && !crossing ? 0 : c.sJ;
                var left = a.frame.Offset(a.hwL);
                for (int k = a.frame.Count - 1; k >= 0; k--)
                    if (a.frame.s[k] < a.sMouth - 1e-9 && a.frame.s[k] > stopA + 1e-9) P.Add(new OutlinePoint { p = left[k] });
                switch (c.kind)
                {
                    case CornerKind.Fillet:
                    case CornerKind.Nose:
                        foreach (var q in c.arc) P.Add(new OutlinePoint { p = q });
                        break;
                    case CornerKind.Miter:
                        P.Add(new OutlinePoint { p = c.centre });
                        break;
                    case CornerKind.Straight when crossing:
                        P.Add(new OutlinePoint { p = c.centre });
                        break;
                    default:
                        P.Add(new OutlinePoint { p = a.frame.P(0, a.hwL) });
                        P.Add(new OutlinePoint { p = b.frame.P(0, -b.hwR) });
                        break;
                }
                var right = b.frame.Offset(-b.hwR);
                for (int k = 0; k < b.frame.Count; k++)
                    if (b.frame.s[k] > startB + 1e-9 && b.frame.s[k] < b.sMouth - 1e-9) P.Add(new OutlinePoint { p = right[k] });
            }
            return FinishOutline(ns, P, st, out why);
        }

        /// <summary>Welds the outline points (never dropping a mouth vertex), checks it, and sets the polygon and boundary vertices.</summary>
        static bool FinishOutline(NodeSurface ns, List<OutlinePoint> P, SurfaceSettings st, out string why)
        {
            // weld: drop near-duplicates, never a mouth vertex
            var W = new List<OutlinePoint>();
            foreach (var q in P)
            {
                if (W.Count > 0 && D2.Distance(W[W.Count - 1].p, q.p) <= st.weld)
                {
                    if (q.mouth && !W[W.Count - 1].mouth) W[W.Count - 1] = q;
                    continue;
                }
                W.Add(q);
            }
            while (W.Count > 2 && D2.Distance(W[0].p, W[W.Count - 1].p) <= st.weld && !W[W.Count - 1].mouth) W.RemoveAt(W.Count - 1);
            var poly = new List<D2>();
            foreach (var q in W) poly.Add(q.p);
            ns.polygon = poly;
            ns.area = Geom2.SignedArea(poly);
            if (ns.area <= 0) { why = string.Format(Inv, "clockwise or empty (area {0:F1})", ns.area); return false; }
            if (!Geom2.IsSimple(poly, out why)) return false;
            ns.vertices.Clear();
            foreach (var q in W) ns.vertices.Add(q.mouth ? q.v : new D3(q.p.x, JunctionHeight(ns, q.p, st), q.p.z));
            why = "";
            return true;
        }

        static void Mesh(GraphNode n, NodeSurface ns, SurfaceSettings st)
        {
            int m = ns.polygon.Count;
            var node = n.p.XZ;
            ns.triangles.Clear();
            if (Geom2.IsStarShaped(ns.polygon, node))
            {
                // 05 §5.5 RingMesh: a centre fan and rings at 1/3 and 2/3 (interior vertices for the crown)
                ns.ringMesh = true;
                var boundary = new List<D3>(ns.vertices);
                ns.vertices.Clear();
                ns.vertices.Add(new D3(node.x, JunctionHeight(ns, node, st), node.z));
                for (int ring = 1; ring <= 2; ring++)
                    for (int k = 0; k < m; k++)
                    {
                        var q = node + (ns.polygon[k] - node) * (ring / 3.0);
                        ns.vertices.Add(new D3(q.x, JunctionHeight(ns, q, st), q.z));
                    }
                ns.vertices.AddRange(boundary);
                int R(int ring, int k) => ring == 0 ? 0 : 1 + (ring - 1) * m + (k % m);
                for (int k = 0; k < m; k++)
                {
                    Up(ns.triangles, 0, R(1, k), R(1, k + 1));
                    for (int ring = 1; ring < 3; ring++)
                    {
                        Up(ns.triangles, R(ring, k), R(ring + 1, k), R(ring + 1, k + 1));
                        Up(ns.triangles, R(ring, k), R(ring + 1, k + 1), R(ring, k + 1));
                    }
                }
                return;
            }
            ns.ringMesh = false;
            var tris = Geom2.EarClip(ns.polygon);
            for (int k = 0; k + 2 < tris.Count; k += 3) Up(ns.triangles, tris[k], tris[k + 1], tris[k + 2]);
        }

        /// <summary>Adds a triangle given counter-clockwise in plan, in Unity's front-face order (clockwise seen from above).</summary>
        static void Up(List<int> t, int a, int b, int c) { t.Add(a); t.Add(c); t.Add(b); }

        // ------------------------------------------------------------------ roundabouts (roads-R4 step 1, 05 §5.11)

        /// <summary>
        /// A Roundabout node: RoundaboutBuilder's plan (inscribed circle, flared legs, entry and exit curves) as the
        /// surface outline, each gap between legs one corner (entry curve, ring arc, exit curve), every entry a yield (no
        /// stop line), a crosswalk 6 m behind the yield line on legs with walks. Heights: the plane through the mouths,
        /// the circulatory roadway falling 2 % outward from the island.
        /// </summary>
        static NodeSurface RoundaboutNode(RoadGraph g, GraphNode n, NodeSurface ns, SurfaceSettings st, List<string> notes)
        {
            var rb = g.roundabouts[n.roundabout];
            var arms = new List<(RoadFrame frame, double hwL, double hwR, double angle, double maxMouth, bool walk)>();
            foreach (var a in ns.arms)
            {
                var d = a.frame.Dir(0);
                var e = g.edges[a.edge];
                var road = g.RoadOf(e.sourceRoad);
                bool walk = KerbRules.Walk(road, e.cls, 0) || KerbRules.Walk(road, e.cls, 1);
                arms.Add((a.frame, a.hwL, a.hwR, Math.Atan2(d.z, d.x), a.maxMouth, walk));
            }
            var G = RoundaboutBuilder.Build(n.id, n.roundabout, n.p.XZ, rb, arms, st.roundabout);
            ns.roundabout = G;
            foreach (var x in G.notes) notes.Add(string.Format(Inv, "node {0} roundabout ({1}): {2}", n.id, rb.osm, x));
            int deg = n.Degree;
            for (int i = 0; i < deg; i++)
            {
                var a = ns.arms[i];
                var L = G.legs[i];
                a.tangentL = L.tEntry; a.tangentR = L.tExit;
                a.sT = Math.Max(L.tEntry, L.tExit);
                a.sMouth = L.sMouth;
                a.sEdge = G.Ro;
                a.sStop = -1;
                a.crosswalk = L.sCwInner > 0;
                a.sCwInner = L.sCwInner; a.sCwOuter = L.sCwOuter;
            }
            for (int i = 0; i < deg; i++)
            {
                int j = (i + 1) % deg;
                var c = new CornerSurface
                {
                    node = n.id, armI = i, armJ = j, kind = CornerKind.Fillet, baseRadius = G.gapEntryRadius[i], radius = G.gapEntryRadius[i],
                    reason = CornerReason.Roundabout, sI = G.legs[i].tEntry, sJ = G.legs[j].tExit, centre = G.c,
                };
                c.arc.AddRange(G.gaps[i]);
                ns.corners.Add(c);
            }
            ns.majorArmA = ns.majorArmB = -1;
            Heights(n, ns, st);
            ns.mode = SurfaceMode.Roundabout;
            foreach (var a in ns.arms) MouthLine(ns, a, st);

            var P = new List<OutlinePoint>();
            for (int i = 0; i < deg; i++)
            {
                var a = ns.arms[i];
                var L = G.legs[i];
                foreach (var v in a.mouth) P.Add(new OutlinePoint { p = v.XZ, mouth = true, v = v });
                var ss = EdgeStations(a.frame, L.tEntry, a.sMouth, L.sFlare);
                for (int k = ss.Count - 1; k >= 0; k--) P.Add(new OutlinePoint { p = a.frame.P(ss[k], L.EdgeL(ss[k])) });
                foreach (var q in G.gaps[i]) P.Add(new OutlinePoint { p = q });
                var b = ns.arms[(i + 1) % deg];
                var M = G.legs[(i + 1) % deg];
                foreach (var s in EdgeStations(b.frame, M.tExit, b.sMouth, M.sFlare)) P.Add(new OutlinePoint { p = b.frame.P(s, -M.EdgeR(s)) });
            }
            if (!FinishOutline(ns, P, st, out string why))
                return Fallback(ns, "the roundabout outline is not simple: " + why, notes, n);
            Mesh(n, ns, st);
            ns.corridorArea = CorridorArea(ns);
            return ns;
        }

        /// <summary>Stations strictly between a and b: the frame's vertices and `extra`.</summary>
        static List<double> EdgeStations(RoadFrame f, double a, double b, double extra)
        {
            var l = new List<double>();
            for (int k = 0; k < f.Count; k++) if (f.s[k] > a + 1e-6 && f.s[k] < b - 1e-6) l.Add(f.s[k]);
            if (extra > a + 1e-6 && extra < b - 1e-6) l.Add(extra);
            l.Sort();
            return l;
        }

        // ------------------------------------------------------------------ overrides

        static NodeSurface OverridePolygon(RoadGraph g, GraphNode n, NodeSurface ns, NodeOverride ov, SurfaceSettings st, List<string> notes)
        {
            ns.mode = SurfaceMode.Override;
            ns.corners.Clear();
            var poly = new List<D2>(ov.polygon);
            if (Geom2.SignedArea(poly) < 0) poly.Reverse();
            // the plane through the arms' centres where they leave the polygon; each arm's mouth is where its centreline exits
            double sx = 0, sz = 0, sy = 0;
            foreach (var a in ns.arms)
            {
                a.sMouth = 0;
                for (double s = 0; s <= a.frame.Length; s += 0.25)
                    if (!TownZone.InPolygon(poly, a.frame.Centre(s))) { a.sMouth = s; break; }
                var p = a.frame.Centre(a.sMouth);
                sx += p.x; sz += p.z; sy += a.frame.Y(a.sMouth);
            }
            int m = ns.arms.Count;
            ns.planeA = ns.planeB = 0;
            ns.planeC = sy / m;
            foreach (var a in ns.arms) MouthLine(ns, a, st);
            // splice each mouth line into the outline where its centreline crosses it
            var outline = new List<D2>(poly);
            foreach (var a in ns.arms)
            {
                var mid = a.frame.Centre(a.sMouth);
                int best = 0;
                double bd = double.MaxValue;
                for (int k = 0; k < outline.Count; k++)
                {
                    double d = Polyline.SegmentDistance(outline[k], outline[(k + 1) % outline.Count], mid);
                    if (d < bd) { bd = d; best = k; }
                }
                var ins = new List<D2>();
                foreach (var v in a.mouth) ins.Add(v.XZ);
                outline.InsertRange(best + 1, ins);
            }
            ns.polygon = Geom2.Weld(outline, st.weld);
            ns.area = Geom2.SignedArea(ns.polygon);
            if (!Geom2.IsSimple(ns.polygon, out string why) || ns.area <= 0)
                return Fallback(ns, "the roads-overrides polygon is not simple once the mouths are in: " + why, notes, n);
            ns.vertices.Clear();
            foreach (var q in ns.polygon)
            {
                D3 v = default;
                bool shared = false;
                foreach (var a in ns.arms) foreach (var mv in a.mouth) if (mv.x == q.x && mv.z == q.z) { v = mv; shared = true; }
                ns.vertices.Add(shared ? v : new D3(q.x, JunctionHeight(ns, q, st), q.z));
            }
            var tris = Geom2.EarClip(ns.polygon);
            for (int k = 0; k + 2 < tris.Count; k += 3) Up(ns.triangles, tris[k], tris[k + 1], tris[k + 2]);
            ns.corridorArea = CorridorArea(ns);
            notes.Add(string.Format(Inv, "node {0} (osm node {1}): surface from roads-overrides ({2})", n.id, n.osmNode, ov.reason));
            return ns;
        }

        // ------------------------------------------------------------------ the area cross-check (05 §8.1)

        /// <summary>
        /// The area the vehicles sweep through the junction (05 §8.1's connector corridors): each arm's own roadway from the
        /// node to its mouth; every pair of arms a band the width of the roads along a quadratic curve from one mouth centre
        /// through the node to the other; and the right turns between neighbouring arms, concentric with the kerb fillet
        /// (05 §5.14) in their own lane, from the kerb out to the centreline. Rasterised at 0.1 m and unioned; the outline's area is
        /// checked against it (±5 %, junctions). It does not use the outline itself, so a broken outline shows.
        /// </summary>
        public static double CorridorArea(NodeSurface ns)
        {
            if (ns.arms.Count < 2 || ns.polygon.Count < 3) return 0;
            const double cell = 0.1;
            double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
            foreach (var p in ns.polygon) { minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minZ = Math.Min(minZ, p.z); maxZ = Math.Max(maxZ, p.z); }
            minX -= 5; minZ -= 5; maxX += 5; maxZ += 5;
            int w = (int)Math.Ceiling((maxX - minX) / cell), h = (int)Math.Ceiling((maxZ - minZ) / cell);
            var grid = new bool[w * h];
            var node = ns.arms[0].frame.c[0];
            // each arm's own roadway from the node to its mouth, along its real centreline
            foreach (var A in ns.arms)
            {
                var f = A.frame;
                double prev = 0;
                for (int k = 1; k < f.Count && prev < A.sMouth; k++)
                {
                    double s1 = Math.Min(f.s[k], A.sMouth);
                    FillQuad(grid, w, h, minX, minZ, cell, f.P(prev, A.hwL), f.P(s1, A.hwL), f.P(s1, -A.hwR), f.P(prev, -A.hwR));
                    prev = s1;
                }
            }
            // right turns between neighbouring arms sweep concentric with the kerb fillet (05 §5.14), in their own lane: the
            // annulus from the kerb radius out to the centreline, over the fillet's angle
            var neighbours = new HashSet<(int, int)>();
            foreach (var c in ns.corners)
            {
                if (c.kind != CornerKind.Fillet || c.arc.Count < 2) continue;
                neighbours.Add((Math.Min(c.armI, c.armJ), Math.Max(c.armI, c.armJ)));
                var I = ns.arms[c.armI];
                var J = ns.arms[c.armJ];
                double width = 0.5 * (I.hwL + J.hwR);
                for (int k = 0; k + 1 < c.arc.Count; k++)
                {
                    D2 p0 = c.arc[k], p1 = c.arc[k + 1];
                    D2 o0 = c.centre + (p0 - c.centre).Normalized * (c.radius + width), o1 = c.centre + (p1 - c.centre).Normalized * (c.radius + width);
                    FillQuad(grid, w, h, minX, minZ, cell, p0, p1, o1, o0);
                }
            }
            for (int i = 0; i < ns.arms.Count; i++)
                for (int j = i + 1; j < ns.arms.Count; j++)
                {
                    var A = ns.arms[i];
                    var B = ns.arms[j];
                    D2 pa = A.frame.Centre(A.sMouth), pb = B.frame.Centre(B.sMouth);
                    double ha = 0.5 * (A.hwL + A.hwR), hb = 0.5 * (B.hwL + B.hwR);
                    const int N = 24;
                    D2 prevL = default, prevR = default;
                    for (int k = 0; k <= N; k++)
                    {
                        double t = (double)k / N;
                        D2 p = pa * ((1 - t) * (1 - t)) + node * (2 * t * (1 - t)) + pb * (t * t);
                        D2 d = (node - pa) * (2 * (1 - t)) + (pb - node) * (2 * t);
                        if (d.Length < 1e-9) d = pb - pa;
                        var nl = d.Normalized.Left;
                        double hw = ha + (hb - ha) * t;
                        D2 L = p + nl * hw, R = p - nl * hw;
                        if (k > 0) FillQuad(grid, w, h, minX, minZ, cell, prevL, L, R, prevR);
                        prevL = L; prevR = R;
                    }
                }
            int count = 0;
            foreach (var b in grid) if (b) count++;
            return count * cell * cell;
        }

        static void FillQuad(bool[] grid, int w, int h, double x0, double z0, double cell, D2 a, D2 b, D2 c, D2 d)
        {
            var poly = new[] { a, b, c, d };
            double minZ = Math.Min(Math.Min(a.z, b.z), Math.Min(c.z, d.z)), maxZ = Math.Max(Math.Max(a.z, b.z), Math.Max(c.z, d.z));
            int j0 = Math.Max(0, (int)Math.Floor((minZ - z0) / cell)), j1 = Math.Min(h - 1, (int)Math.Ceiling((maxZ - z0) / cell));
            var xs = new List<double>();
            for (int j = j0; j <= j1; j++)
            {
                double zc = z0 + (j + 0.5) * cell;
                xs.Clear();
                for (int k = 0; k < 4; k++)
                {
                    D2 p = poly[k], q = poly[(k + 1) % 4];
                    if ((p.z <= zc && q.z > zc) || (q.z <= zc && p.z > zc)) xs.Add(p.x + (zc - p.z) / (q.z - p.z) * (q.x - p.x));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int i0 = Math.Max(0, (int)Math.Ceiling((xs[k] - x0) / cell - 0.5)), i1 = Math.Min(w - 1, (int)Math.Floor((xs[k + 1] - x0) / cell - 0.5));
                    for (int i = i0; i <= i1; i++) grid[j * w + i] = true;
                }
            }
        }

        // ------------------------------------------------------------------ ribbons (card step 6)

        static RibbonSurface BuildRibbon(RoadGraph g, GraphEdge e, RoadFrame f, RoadSurfaces res, SurfaceSettings st)
        {
            var road = g.RoadOf(e.sourceRoad);
            double hwL = road?.xs?.hwLeft ?? e.hw, hwR = road?.xs?.hwRight ?? e.hw;
            bool kerbed = KerbRules.Kerbed(road, e.cls), gravel = e.cls == RoadClass.Dirt;
            ArmSurface armA = null, armB = null;
            var nA = res.Of(e.nodeA);
            var nB = res.Of(e.nodeB);
            if (nA != null) foreach (var a in nA.arms) if (a.edge == e.id && a.atA) armA = a;
            if (nB != null) foreach (var a in nB.arms) if (a.edge == e.id && !a.atA) armB = a;
            double L = f.Length;
            double s0 = armA != null && armA.mouth != null ? armA.sMouth : 0, s1 = armB != null && armB.mouth != null ? L - armB.sMouth : L;
            if (s1 - s0 < 0.05)
            {
                res.notes.Add(string.Format(Inv, "edge {0} (R{1}): no ribbon, the mouths at nodes {2} and {3} overlap ({4:F2} m left)", e.id, e.sourceRoad, e.nodeA, e.nodeB, s1 - s0));
                return null;
            }
            var r = new RibbonSurface { edge = e.id, s0 = s0, s1 = s1, kerbed = kerbed, profile = Profile(hwL, hwR, kerbed, st.gutterPan) };
            int P = r.profile.Length;
            // the corrections each end's junction asks for, per lateral position (constant along the road; 05 §5.6 ArmY)
            var dA = new double[P];
            var dB = new double[P];
            D3[] endA = null, endB = null;
            if (armA != null && armA.mouth != null && armA.mouth.Length == P)
            {
                endA = armA.mouth;
                for (int k = 0; k < P; k++) dA[k] = endA[k].y - (f.Y(s0) + Crown(r.profile[k], hwL, hwR, kerbed, gravel, st));
            }
            if (armB != null && armB.mouth != null && armB.mouth.Length == P)
            {
                endB = new D3[P];
                for (int k = 0; k < P; k++) endB[k] = armB.mouth[P - 1 - k];
                for (int k = 0; k < P; k++) dB[k] = endB[k].y - (f.Y(s1) + Crown(r.profile[k], hwL, hwR, kerbed, gravel, st));
            }
            double lw = Math.Min(st.warp, 0.5 * (s1 - s0));
            var ss = new List<double> { s0 };
            for (int i = 0; i < f.Count; i++) if (f.s[i] > s0 + 1e-6 && f.s[i] < s1 - 1e-6) ss.Add(f.s[i]);
            ss.Add(s1);
            for (int q = 0; q < ss.Count; q++)
            {
                double s = ss[q];
                if (q == 0 && endA != null) { r.stationS.Add(s); r.stations.Add(endA); continue; }
                if (q == ss.Count - 1 && endB != null) { r.stationS.Add(s); r.stations.Add(endB); continue; }
                double wA = 1 - Smooth((s - s0) / lw), wB = 1 - Smooth((s1 - s) / lw);
                var row = new D3[P];
                for (int k = 0; k < P; k++)
                {
                    var p = f.P(s, r.profile[k]);
                    double y = f.Y(s) + Crown(r.profile[k], hwL, hwR, kerbed, gravel, st) + wA * dA[k] + wB * dB[k];
                    row[k] = new D3(p.x, y, p.z);
                }
                r.stationS.Add(s);
                r.stations.Add(row);
            }
            if ((armA != null && armA.mouth != null && armA.mouth.Length != P) || (armB != null && armB.mouth != null && armB.mouth.Length != P))
                res.notes.Add(string.Format(Inv, "edge {0}: the ribbon's section differs from a mouth's; the seam is not shared", e.id));
            return r;
        }

        static double Smooth(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return t * t * (3 - 2 * t);
        }

        // ------------------------------------------------------------------ paved aprons (roads-R4 step 3, 05 §5.12)

        static bool EdgePaved(RoadGraph g, int edge)
        {
            var e = g.edges[edge];
            return KerbRules.Paved(KerbRules.Surface(e.cls, g.RoadOf(e.sourceRoad)?.surface));
        }

        /// <summary>
        /// Where an unpaved road (gravel, dirt; not an alley) meets a junction whose own surface is paved, its first
        /// `apron` metres from the mouth are paved too, so gravel never spills onto the asphalt and the stop line has paint.
        /// </summary>
        static void Aprons(RoadGraph g, RoadSurfaces res, SurfaceSettings st)
        {
            foreach (var r in res.ribbons)
            {
                if (EdgePaved(g, r.edge)) continue;
                var e = g.edges[r.edge];
                foreach (var (node, atA) in new[] { (e.nodeA, true), (e.nodeB, false) })
                {
                    var ns = res.Of(node);
                    if (ns == null || ns.mode == SurfaceMode.Fallback || ns.arms.Count < 2) continue;
                    ArmSurface self = null, main = null;
                    foreach (var a in ns.arms)
                    {
                        if (a.edge == e.id && a.atA == atA) self = a;
                        if (main == null || RoadClasses.Rank(a.cls) > RoadClasses.Rank(main.cls)) main = a;
                    }
                    if (self == null || self.alley || !EdgePaved(g, main.edge)) continue;
                    double len = Math.Min(st.apron, 0.5 * (r.s1 - r.s0));
                    if (atA) r.apronA = len; else r.apronB = len;
                }
            }
        }

        /// <summary>Is the road surface at arc length s of an edge paved (its class and tags, or a paved apron)?</summary>
        public static bool PavedAt(RoadGraph g, RoadSurfaces surf, int edge, double s)
        {
            if (EdgePaved(g, edge)) return true;
            foreach (var r in surf.ribbons)
            {
                if (r.edge != edge) continue;
                if (r.apronA > 0 && s <= r.s0 + r.apronA + 1e-6) return true;
                if (r.apronB > 0 && s >= r.s1 - r.apronB - 1e-6) return true;
            }
            // inside a junction surface: paved when the junction is
            var e = g.edges[edge];
            foreach (var (node, atA) in new[] { (e.nodeA, true), (e.nodeB, false) })
            {
                var ns = surf.Of(node);
                if (ns == null || ns.mode == SurfaceMode.Fallback) continue;
                foreach (var a in ns.arms)
                    if (a.edge == edge && a.atA == atA && (atA ? s : e.Length - s) <= a.sMouth + 1e-6)
                    {
                        ArmSurface main = null;
                        foreach (var b in ns.arms) if (main == null || RoadClasses.Rank(b.cls) > RoadClasses.Rank(main.cls)) main = b;
                        return EdgePaved(g, main.edge);
                    }
            }
            return false;
        }

        // ------------------------------------------------------------------ curb inlets (05 §5.6)

        static void Inlets(RoadGraph g, RoadSurfaces res, RoadFrame[] frames, SurfaceSettings st)
        {
            // upstream of each kerb return tangent on an uphill approach (water runs down into the junction)
            foreach (var ns in res.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback || ns.mode == SurfaceMode.Through) continue;
                foreach (var a in ns.arms)
                {
                    if (!a.kerbed || a.frame.Grade(a.sMouth + 5, 5) <= 0.005) continue;
                    foreach (var side in new[] { 1, -1 })
                    {
                        double sT = side > 0 ? a.tangentL : a.tangentR;
                        double s = Math.Max(sT, a.sMouth) + st.inletUpstream;
                        if (a.crosswalk && s >= a.sCwInner && s <= a.sCwOuter) s = a.sCwOuter + st.inletUpstream;
                        var p = a.frame.P(s, side > 0 ? a.hwL : -a.hwR);
                        res.inlets.Add(new Inlet { p = new D3(p.x, a.frame.Y(s), p.z), node = ns.node, edge = a.edge, why = "upstream of the kerb return" });
                    }
                }
            }
            // flowline low points and every 90 m on kerbed ribbons
            foreach (var r in res.ribbons)
            {
                if (!r.kerbed) continue;
                foreach (int k in new[] { 0, r.profile.Length - 1 })
                {
                    double last = double.NegativeInfinity;
                    for (int q = 1; q + 1 < r.stations.Count; q++)
                    {
                        double y0 = r.stations[q - 1][k].y, y1 = r.stations[q][k].y, y2 = r.stations[q + 1][k].y;
                        bool low = y1 < y0 - 1e-3 && y1 < y2 - 1e-3;
                        bool spaced = r.stationS[q] - (double.IsNegativeInfinity(last) ? r.s0 : last) >= st.inletSpacing;
                        if (!low && !spaced) continue;
                        last = r.stationS[q];
                        res.inlets.Add(new Inlet { p = r.stations[q][k], edge = r.edge, why = low ? "flowline low point" : "every 90 m on a grade" });
                    }
                }
            }
        }
    }
}
