using System;
using System.Collections.Generic;

namespace Backroads.Roads
{
    /// <summary>Tolerances and per-context kerb radii of 05 §5.1 and §5.3. The defaults are the research values.</summary>
    public sealed class RoadGraphSettings
    {
        /// <summary>A flagged road end within this distance of another road becomes a T split there (§5.1 step 1).</summary>
        public double tJoinTolerance = 1.0;
        /// <summary>No split closer than this to the other road's own ends (§5.1 step 1: "lands > 2 m from that road's ends").</summary>
        public double endClearance = 2.0;
        /// <summary>Ends and split points closer than this are one node (§5.1 step 2; trafficNet used 6 m).</summary>
        public double clusterRadius = 1.0;
        /// <summary>A split this close to a canon point snaps onto it, so canon points are kept and no sliver segments appear.</summary>
        public double snapToCanonPoint = 0.01;
        /// <summary>Town kerb zone = convex hull of the street centrelines, buffered by this (§5.1 step 3).</summary>
        public double zoneBuffer = 20.0;
        /// <summary>Degree-2 nodes bending less than this (same class) are joined; more is a Bend (§5.1 step 4).</summary>
        public double joinDeflectionDeg = 25.0;
        /// <summary>Two junctions closer than this need an override (§5.1 step 5).</summary>
        public double complexJunctionDistance = 30.0;
        /// <summary>roads-R4 (05 §5.11): a roundabout's legs at least this many degrees apart, else a reported exception.</summary>
        public double minRoundaboutLegGap = 70.0;
        /// <summary>No major road if the best pair bends more than this, in radians (§5.1 step 7; trafficNet's 0.6).</summary>
        public double majorMaxBend = 0.6;
        /// <summary>Arm direction sampled at max(this, 1.5 hw) along the arm (§5.2).</summary>
        public double armSampleMin = 10.0;
        /// <summary>A node within this distance of a traffic.json node takes its number (so ids match 05's tables).</summary>
        public double legacyMatchRadius = 1.0;
        /// <summary>Approach grade window and threshold (§5.6 reports grades over 6 % near a stop line).</summary>
        public double gradeWindow = 20.0, steepGrade = 0.06;
        /// <summary>Arms closer than this angle are flagged as an acute Y.</summary>
        public double acuteArmDeg = 60.0;
        /// <summary>
        /// A Street/through-road corner whose 7.5 m kerb would pass closer than this to a building is a tight downtown corner
        /// and gets the 6 m radius (05 §1.3 measured 1.2 m at node 9 and 2.1 m at node 7, 3.3 m or more elsewhere; §7 plan).
        /// </summary>
        public double downtownClearance = 2.5;
        /// <summary>How far along each arm the kerb clearance looks for buildings.</summary>
        public double clearanceReach = 40.0;

        /// <summary>Kerb-face radii by corner context (§5.3).</summary>
        public double radiusStreetStreet = 7.5, radiusDowntown = 6.0, radiusThroughThrough = 12.0, radiusDirtPaved = 9.0, radiusBendInner = 6.0;
        /// <summary>Acute corners: the radius shrinks so the tangent points stay within this distance (§5.3).</summary>
        public double maxTangent = 25.0;
        /// <summary>Mouth = furthest fillet tangent + this (§5.4).</summary>
        public double mouthPad = 0.5;
    }

    public sealed class GraphNode
    {
        public int id;
        /// <summary>traffic.json node id when one is within settings.legacyMatchRadius, else -1.</summary>
        public int legacyId = -1;
        /// <summary>The OSM node at this graph node (towns), or 0: the stable key of roads-overrides.</summary>
        public long osmNode;
        public D3 p;
        public NodeKind kind;
        public Control control;
        /// <summary>Tag when any approach's control comes from a mapped sign or signal, Rule when 05 §6.2 set it, None without control.</summary>
        public ControlSource controlSource;
        public NodeFlags flags;
        /// <summary>Global arm ids, sorted counter-clockwise seen from above (05 §4).</summary>
        public readonly List<int> arms = new List<int>();
        /// <summary>Global corner ids; corners[i] joins arms[i] to arms[(i + 1) % n].</summary>
        public readonly List<int> corners = new List<int>();
        /// <summary>Positions in <see cref="arms"/> of the through road, or -1 (05 §4 majorA/majorB).</summary>
        public int majorA = -1, majorB = -1;
        /// <summary>How far the best-scoring arm pair bends from straight (radians), even when it was rejected as major.</summary>
        public double bestPairBend = double.NaN;
        /// <summary>Smallest angle between neighbouring arms (radians); NaN below 2 arms.</summary>
        public double minArmGap = double.NaN;
        /// <summary>roads-R4: the index in <see cref="RoadGraph.roundabouts"/> when kind is Roundabout, else -1.</summary>
        public int roundabout = -1;
        public int Degree => arms.Count;
        public bool IsJunctionLike => arms.Count >= 3;
        public bool HasMajor => majorA >= 0;
    }

    public sealed class GraphEdge
    {
        public int id, nodeA, nodeB;
        /// <summary>Index of the canon road (world.json roads[] id) this edge is a piece of.</summary>
        public int sourceRoad;
        public RoadClass cls;
        public double hw;
        /// <summary>Where the piece starts and ends on the source road (canon d).</summary>
        public double sStart, sEnd;
        /// <summary>Centreline from nodeA to nodeB: canon points unchanged, plus interpolated split points at the ends.</summary>
        public D3[] pts;
        /// <summary>2D arc length from nodeA.</summary>
        public double[] s;
        public double Length => s[s.Length - 1];
    }

    public sealed class GraphArm
    {
        public int id, node, edge, sourceRoad;
        /// <summary>True when the arm leaves the node at the edge's A end.</summary>
        public bool atA;
        public RoadClass cls;
        public double hw;
        /// <summary>Unit direction from the node to the arm's sample point (§5.2) and its angle atan2(z, x).</summary>
        public D2 dir;
        public double angle;
        public double length;
        /// <summary>Rise per metre over the first gradeWindow metres outward (+ = uphill away from the node); steepest 0.5 m+ piece in that window.</summary>
        public double grade, maxGrade;
        /// <summary>
        /// Provisional (straight-line, §5.3 formula) fillet tangent distances on the left and right kerb lines and the mouth,
        /// in metres from the node along the arm. R1 replaces them with the polyline corner solver.
        /// </summary>
        public double tangentL, tangentR, mouth, maxMouth;
        public bool shortArm;
        /// <summary>What traffic arriving at the node along this arm must do (MinorStop = a STOP sign on this approach).</summary>
        public Control control;
        public ControlSource controlSource;
        /// <summary>The mapped sign's OSM node and its distance from the node along the arm (0 for rule controls).</summary>
        public long controlOsmNode;
        public double controlDistance;
    }

    /// <summary>A mapped feature placed on the graph (roads-R0o step 5).</summary>
    public sealed class GraphFeature
    {
        public RoadFeatureKind kind;
        public long osmNode;
        public int edge;
        /// <summary>Arc length on the edge from nodeA.</summary>
        public double s;
        /// <summary>+1 applies to traffic towards the edge's nodeB, -1 towards nodeA, 0 both or unknown.</summary>
        public int direction;
        /// <summary>The node it controls and the approach arm there, or -1 (crossings mid-block, or a sign that controls nothing).</summary>
        public int node = -1, arm = -1;
        public string crossing = "";
        /// <summary>Empty when placed; else why the feature controls nothing (reported).</summary>
        public string problem = "";
    }

    public sealed class GraphCorner
    {
        public int id, node, armI, armJ;
        public CornerKind kind;
        /// <summary>Opening angle from arm I counter-clockwise to arm J (radians).</summary>
        public double opening;
        public double radius;
        /// <summary>Tangent distances along arm I (its left kerb) and arm J (its right kerb), straight-line estimate.</summary>
        public double tI, tJ;
        /// <summary>Fillet centre (Fillet) or kerb-line intersection (Miter); the node for Straight.</summary>
        public D2 centre;
        /// <summary>Tight downtown corner (6 m radius, see RoadGraphSettings.downtownClearance).</summary>
        public bool downtown;
        /// <summary>
        /// Signed distance from the nearest building corner in this corner's block to a 7.5 m filleted kerb line (05 §1.3's
        /// measure; negative = the kerb would cut the building). NaN when no street arm meets here or no building is near.
        /// </summary>
        public double clearance = double.NaN;
    }

    public struct ComplexPair
    {
        public int a, b;
        public double distance;
    }

    /// <summary>The cleaned road graph of 05 §5.1 (R0), double precision, Unity space.</summary>
    public sealed class RoadGraph
    {
        public RoadGraphSettings settings;
        public readonly List<GraphNode> nodes = new List<GraphNode>();
        public readonly List<GraphEdge> edges = new List<GraphEdge>();
        public readonly List<GraphArm> arms = new List<GraphArm>();
        public readonly List<GraphCorner> corners = new List<GraphCorner>();
        public readonly List<ComplexPair> complexPairs = new List<ComplexPair>();
        public readonly List<GraphFeature> features = new List<GraphFeature>();
        /// <summary>Human-readable findings for the later steps (complex pairs, short arms, steep approaches, acute arms).</summary>
        public readonly List<string> notes = new List<string>();
        public TownZone zone;
        /// <summary>The downtown zone polygon (towns), or null.</summary>
        public D2[] downtown;
        /// <summary>The input roads (edges point at them through sourceRoad); the asset keeps their tag data.</summary>
        public List<RoadGraphInput.Road> roads = new List<RoadGraphInput.Road>();
        /// <summary>roads-R4: the mapped roundabouts (the input's); a Roundabout node points at its entry.</summary>
        public List<RoadGraphInput.Roundabout> roundabouts = new List<RoadGraphInput.Roundabout>();
        public string source = "", sourceManifestSha256 = "";
        public int inputRoads, inputPoints;

        public RoadGraphInput.Road RoadOf(int sourceRoad)
        {
            foreach (var r in roads) if (r.id == sourceRoad) return r;
            return null;
        }

        public GraphArm Arm(GraphNode n, int k) => arms[n.arms[k]];

        public int CountKind(NodeKind k)
        {
            int c = 0;
            foreach (var n in nodes) if (n.kind == k) c++;
            return c;
        }

        public GraphNode NodeByLegacy(int legacyId)
        {
            foreach (var n in nodes) if (n.legacyId == legacyId) return n;
            return null;
        }

        /// <summary>The other node of an arm's edge.</summary>
        public int FarNode(GraphArm a)
        {
            var e = edges[a.edge];
            return a.atA ? e.nodeB : e.nodeA;
        }
    }
}
