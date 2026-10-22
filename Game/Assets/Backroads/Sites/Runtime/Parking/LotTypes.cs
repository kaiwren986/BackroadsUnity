using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites
{
    // The parking engine's input and output (card sites-S2, design docs/design/site-layouts.md §5). Plan coordinates are
    // local metres of the town frame (x east, z north in .y); polygons CCW, not closed. Yaw: degrees clockwise from
    // north (+z), Unity's rotation about Y. Enum values are stable (append only).

    public enum StallKind : byte { Standard = 0, Accessible = 1, Van = 2, Compact = 3, Trailer = 4, Motorcycle = 5 }

    /// <summary>Which way traffic may use an entrance.</summary>
    public enum EntranceFlow : byte { TwoWay = 0, EntryOnly = 1, ExitOnly = 2 }

    /// <summary>What lies beyond a lot edge: a neighbour's land, the sidewalk, a building wall, or an open frontage.</summary>
    public enum LotEdgeKind : byte { Property = 0, Walk = 1, Building = 2, Open = 3 }

    public enum LaneKind : byte { Aisle = 0, Spine = 1, FireLane = 2, DriveThrough = 3 }

    public enum IslandKind : byte { End = 0, Intermediate = 1 }

    public enum MarkColour : byte { White = 0, Yellow = 1, Blue = 2, Red = 3 }

    /// <summary>Painted symbols: the International Symbol of Accessibility, a direction arrow, FIRE LANE lettering.</summary>
    public enum MarkSymbol : byte { Accessible = 0, Arrow = 1, FireLane = 2 }

    /// <summary>A driveway into the lot: the throat on the lot's boundary (a to b, either order).</summary>
    [Serializable]
    public sealed class LotEntrance
    {
        public double2 a, b;
        public EntranceFlow flow = EntranceFlow.TwoWay;
        /// <summary>The street it opens onto (exits onto MainStreet and Highway get a stop bar).</summary>
        public StreetClass street = StreetClass.Street;
        public bool truck;
        /// <summary>The DrivewayRequest this entrance came from, or -1.</summary>
        public int requestId = -1;

        public double2 Mid => (a + b) * 0.5;
        public double Width => math.distance(a, b);
    }

    /// <summary>The building the lot serves: its footprint (kept clear) and its main door (accessible stalls go nearest).</summary>
    [Serializable]
    public sealed class LotBuilding
    {
        public double2[] footprint;
        public bool hasDoor;
        public double2 door;
    }

    /// <summary>What the site plan wants from the lot.</summary>
    [Serializable]
    public sealed class LotTargets
    {
        /// <summary>Stalls wanted; 0 = as many as fit. Lots are always packed full; the target steers the choice.</summary>
        public int stalls;
        public bool paved = true;
        /// <summary>A paved lot in the kerb zone: Barrier150 around the lot and its islands.</summary>
        public bool kerbed;
        /// <summary>Accessible stalls by the 2010 ADA table (off for trailer and truck lots).</summary>
        public bool accessible = true;
        /// <summary>A painted fire lane along the building's lot-side face (big boxes).</summary>
        public bool fireLane;
        /// <summary>A one-way drive-through lane around the building (fast food), stacking 6-8 cars.</summary>
        public bool driveThrough;
        public StallKind stallKind = StallKind.Standard;
    }

    /// <summary>Everything the packer reads.</summary>
    [Serializable]
    public sealed class LotInput
    {
        public double2[] lot;
        /// <summary>Per lot edge (vertex i to i + 1); null = all Property.</summary>
        public LotEdgeKind[] edgeKinds;
        public List<LotEntrance> entrances = new List<LotEntrance>();
        public LotBuilding building;
        /// <summary>Along the main frontage street; zero = the first entrance's throat.</summary>
        public double2 frontageDir;
        /// <summary>From the lot towards the street; zero = towards the first entrance.</summary>
        public double2 toStreet;
        /// <summary>A mapped aisle's direction (sites-S3: lots keep their mapped aisles); zero = none. Tried first and
        /// preferred by a small score bonus.</summary>
        public double2 preferredAisle;
        /// <summary>Areas where no stall may go (mapped no-parking zones, a neighbour's apron).</summary>
        public List<double2[]> noPark = new List<double2[]>();
        public LotTargets targets = new LotTargets();
        public ulong seed;
    }

    /// <summary>
    /// One stall: a parallelogram (a rectangle at 90 degrees) with its front on the aisle. poly = front-left... in CCW
    /// order; front and back are the midpoints of the aisle-side and head edges.
    /// </summary>
    [Serializable]
    public sealed class Stall
    {
        public double2[] poly;
        public StallKind kind;
        public double2 front, back;
        /// <summary>Stall angle to its aisle, degrees (90, 60 or 45).</summary>
        public float angle;
        /// <summary>Index into LotLayout.lanes of the aisle it opens onto.</summary>
        public int lane;
        /// <summary>Index of the stall run (one row piece between islands, ends or lanes).</summary>
        public int run;
    }

    /// <summary>A drive aisle or lane: centreline a to b (one-way lanes run a to b), width, footprint polygon.</summary>
    [Serializable]
    public sealed class LotLane
    {
        public LaneKind kind;
        public double2 a, b;
        public float width;
        public bool oneWay;
        public double2[] poly;
    }

    [Serializable]
    public sealed class Island
    {
        public IslandKind kind;
        public double2[] poly;
        public bool kerbed;
    }

    [Serializable]
    public sealed class LotKerb
    {
        public double2[] line;
        public bool closed;
        public KerbKind kind;
    }

    /// <summary>A concrete wheel stop (1.8 x 0.15 x 0.12 m bar, our own mesh): its two ends.</summary>
    [Serializable]
    public struct WheelStop
    {
        public double2 a, b;
        public int stall;
    }

    [Serializable]
    public sealed class MarkLine
    {
        public double2 a, b;
        public float width;
        public MarkColour colour;
    }

    [Serializable]
    public struct MarkSign
    {
        public MarkSymbol symbol;
        public double2 pos;
        public float yaw, size;
        public MarkColour colour;
    }

    /// <summary>Diagonal hatching inside a polygon (access aisles, painted islands).</summary>
    [Serializable]
    public sealed class MarkHatch
    {
        public double2[] poly;
        public MarkColour colour;
        public float spacing, angleDeg;
    }

    /// <summary>A kerb ramp from an access aisle up to the walk (dir points from the aisle towards the walk).</summary>
    [Serializable]
    public struct KerbRamp
    {
        public double2 pos, dir;
        public float width;
        public int stall;
    }

    /// <summary>A prop the lot asks for (key from the design's prop table, reason = the rule that placed it).</summary>
    [Serializable]
    public struct LotProp
    {
        public string key;
        public double2 pos;
        public float yaw;
        public string reason;
    }

    [Serializable]
    public sealed class DriveThroughLane
    {
        /// <summary>The lane's centreline in driving order (building on the driver's left).</summary>
        public double2[] path;
        public float width;
        /// <summary>Cars that queue from the lane's start to the pickup window.</summary>
        public int stackCars;
        public double2 window, orderBoard;
        /// <summary>The lane's footprint as convex pieces (kept clear of stalls and aisles).</summary>
        public List<double2[]> pieces = new List<double2[]>();
    }

    [Serializable]
    public sealed class LotReport
    {
        public int version;
        public ulong seed;
        public int candidates;
        public int dirIndex;
        public float dirDeg, angle, phase;
        public int spineMode;
        public bool squeezed;
        /// <summary>Stalls before the accessible conversion.</summary>
        public int capacity;
        public int total, accessible, van, required, requiredVan;
        public int deadEnds;
        public float avgHops, score;
        /// <summary>The 90-degree module (two stalls and an aisle) and whether it lies in the measured p25-p75 band.</summary>
        public float module;
        public bool moduleInBand;
        public bool valid;
        public List<string> notes = new List<string>();
    }

    /// <summary>The parking engine's answer for one lot.</summary>
    [Serializable]
    public sealed class LotLayout
    {
        public double2[] lot;
        public List<LotEntrance> entrances = new List<LotEntrance>();
        public List<Stall> stalls = new List<Stall>();
        public List<LotLane> lanes = new List<LotLane>();
        public List<Island> islands = new List<Island>();
        public List<LotKerb> kerbs = new List<LotKerb>();
        public List<WheelStop> wheelStops = new List<WheelStop>();
        public List<MarkLine> lines = new List<MarkLine>();
        public List<MarkSign> signs = new List<MarkSign>();
        public List<MarkHatch> hatches = new List<MarkHatch>();
        public List<KerbRamp> ramps = new List<KerbRamp>();
        public List<double2> lamps = new List<double2>();
        public List<LotProp> props = new List<LotProp>();
        public DriveThroughLane driveThrough;
        public double2[] fireLane;
        public LotReport report = new LotReport();

        public int Count(StallKind k)
        {
            int n = 0;
            foreach (var s in stalls) if (s.kind == k) n++;
            return n;
        }

        public int AccessibleCount => Count(StallKind.Accessible) + Count(StallKind.Van);
    }
}
