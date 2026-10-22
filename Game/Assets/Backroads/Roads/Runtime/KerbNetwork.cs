using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;

namespace Backroads.Roads
{
    /// <summary>What a stretch of a block loop borders (roads-R2): a road side, a corner arc, a dead-end cap, the play-area edge.</summary>
    public enum KerbOwner : byte { None = 0, Side = 1, Cap = 2, Mouth = 3, Edge = 4 }

    /// <summary>How a kerbed corner lets people down to the street (05 §5.7).</summary>
    public enum RampKind : byte { None = 0, Depressed = 1, Perpendicular = 2 }

    /// <summary>
    /// One block outline (roads-R2, 05 §5.7): the boundary of (play area − roads and junction surfaces), with the block on
    /// its left. Outer loops run counter-clockwise, holes (a road ring standing alone inside a block) clockwise. The
    /// points are the kerb face line (the pavement edge on unkerbed roads) with y = the asphalt height there (the gutter
    /// flowline on kerbed streets); they are the R1 junction outlines' and ribbons' own vertices wherever the boolean kept them.
    /// </summary>
    public sealed class KerbLoop
    {
        public int id;
        public bool hole;
        public double area;
        public readonly List<D3> pts = new List<D3>();
        /// <summary>Arc length at each point (t[0] = 0); the closing segment ends at Length.</summary>
        public readonly List<double> t = new List<double>();
        public double Length;
        /// <summary>Per segment k (pts[k] to pts[k + 1], the last one closing): what it borders, and for road sides which (edge, side).</summary>
        public readonly List<KerbOwner> owner = new List<KerbOwner>();
        public readonly List<int> edge = new List<int>(), side = new List<int>();
        /// <summary>Per segment: the node and corner position when the segment is on a corner arc, else -1.</summary>
        public readonly List<int> arcNode = new List<int>(), arcCorner = new List<int>();
        public readonly List<int> faces = new List<int>();

        public int Count => pts.Count;
    }

    /// <summary>
    /// A kerb return: the arc of one R1 corner on a block loop, from t0 to t1 (loop arc length; t1 may run past the loop's
    /// length when the arc wraps its start). faceA ends on it (the arm J side), faceB starts on it (the arm I side).
    /// </summary>
    public sealed class KerbCorner
    {
        public int id, node, corner, loop;
        public CornerKind kind;
        public double radius, t0, t1;
        public int faceA = -1, faceB = -1;
        /// <summary>A walk on at least one side: the corner gets a ramp.</summary>
        public bool kerbed;
        public RampKind ramp;
        /// <summary>The lowered stretch on the loop (Dropped25, detectable warning strip along it); ramps run rampRun either side.</summary>
        public double dep0, dep1;
        /// <summary>Perpendicular ramps: the two openings' centres on the loop.</summary>
        public double open0, open1;
        public double ArcLength => t1 - t0;
    }

    /// <summary>
    /// One block face (overview contract 2) as R2 builds it: one side of one road edge between two corners (half of each
    /// corner arc), running with the block on its left. Stations are the loop's points; widths are kerb face to walk back edge.
    /// </summary>
    public sealed class KerbFace
    {
        public int id, edge, side, loop;
        /// <summary>The face's range on its loop (t1 may exceed the loop length when the face wraps the loop start).</summary>
        public double t0, t1;
        public bool kerbed, walk, bridge;
        public KerbKind kind;
        public StreetClass cls;
        public double speedKmh;
        public bool oneway;
        public string osmWay = "";
        public readonly List<D2> kerb = new List<D2>(), back = new List<D2>();
        public readonly List<double> s = new List<double>(), gutter = new List<double>(), top = new List<double>(), width = new List<double>(), backY = new List<double>();
        /// <summary>Unit normals into the block at each station (the average of the neighbouring segments' left normals).</summary>
        public readonly List<D2> normal = new List<D2>();
        public readonly List<ForbiddenInterval> forbidden = new List<ForbiddenInterval>();
        public int cornerStart = -1, cornerEnd = -1;
        /// <summary>Every kerb return whose arc, lowered stretch or ramps reach this face.</summary>
        public readonly List<int> corners = new List<int>();
        /// <summary>The smallest kerb-to-building distance on the face and where (NaN when no building is near).</summary>
        public double minClearance = double.NaN, minClearanceS;

        public int Count => kerb.Count;
        public double Length => s.Count == 0 ? 0 : s[s.Count - 1];
    }

    public enum KerbPieceKind : byte { Walk = 0, Verge = 1, Shoulder = 2 }

    /// <summary>
    /// One convex piece of the kerbside partition (walks, then verges behind them, then gravel shoulders on unkerbed
    /// roads; no two pieces overlap): its outline (CCW), the finished surface height at each vertex (the walk's top, the
    /// verge or shoulder ground) and the ground the road plates set under it. R3 meshes walks from the same pieces.
    /// </summary>
    public sealed class KerbPiece
    {
        public KerbPieceKind kind;
        public int face, seg, part;
        public D2[] poly;
        public double[] surfaceY, groundY;
    }

    /// <summary>The R2 bake result: block loops, faces, corners and the findings.</summary>
    public sealed class KerbNetwork
    {
        public readonly List<KerbLoop> loops = new List<KerbLoop>();
        public readonly List<KerbFace> faces = new List<KerbFace>();
        public readonly List<KerbCorner> corners = new List<KerbCorner>();
        public readonly List<KerbPiece> pieces = new List<KerbPiece>();
        public readonly List<string> notes = new List<string>();
        /// <summary>Edge sides (non-bridge) with no face: covered by other roads (listed).</summary>
        public readonly List<string> unfaced = new List<string>();
        /// <summary>Kerbed stations closer to a building than the walk needs (05 §8.4), one line per face.</summary>
        public readonly List<string> clearance = new List<string>();
        public int slivers;

        public KerbFace Face(int id) => id >= 0 && id < faces.Count ? faces[id] : null;
    }
}
