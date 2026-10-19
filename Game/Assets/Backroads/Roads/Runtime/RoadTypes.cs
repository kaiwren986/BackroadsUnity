using System;

namespace Backroads.Roads
{
    /// <summary>Road classes (05 §4). R0 produces Highway, MainStreet (a through road inside the town kerb zone), Street and Dirt.</summary>
    public enum RoadClass : byte { Highway, MainStreet, Street, Dirt, Yard, Driveway }

    /// <summary>What a graph node is after cleanup (05 §4, §5.1 steps 4 and 6).</summary>
    public enum NodeKind : byte { Junction, Bend, Join, DeadEnd, Roundabout, DirtApron, ClassChange }

    /// <summary>Traffic control at a node (05 §4, §6.2). R0 assigns a provisional control from the §6.2 rules; overrides come with R4/R5.</summary>
    public enum Control : byte { None, Yield, MinorStop, AllWayStop, Signal, RoundaboutYield }

    /// <summary>Corner between two neighbouring arms (05 §4 Corner.Kind, §5.3).</summary>
    public enum CornerKind : byte { Fillet, Straight, Miter, Nose }

    /// <summary>Why a road was split at a point (05 §5.1 steps 1 and 3).</summary>
    [Flags]
    public enum SplitReason : byte { None = 0, TJoin = 1, Zone = 2 }

    /// <summary>Things the bake reports at a node for later steps (R1-R4) or for a user decision.</summary>
    [Flags]
    public enum NodeFlags : ushort
    {
        None = 0,
        /// <summary>Another junction is closer than 30 m (05 §5.1 step 5); needs an override (nodes 0 + 1: decision U8).</summary>
        ComplexPair = 1 << 0,
        /// <summary>An arm is shorter than the room its corners need (R1 backs the radius off or the node needs an override).</summary>
        ShortArm = 1 << 1,
        /// <summary>An approach is steeper than 6 % within 20 m (05 §5.6: the stop line goes to the flattest spot).</summary>
        SteepApproach = 1 << 2,
        /// <summary>Downtown corner context (a commercial lot within 30 m of the corner): 6 m kerb radius (05 §5.3).</summary>
        Downtown = 1 << 3,
        /// <summary>The smallest angle between two arms is under 60 degrees (an acute Y).</summary>
        AcuteArms = 1 << 4,
    }

    public static class RoadClasses
    {
        /// <summary>
        /// Functional rank for the major-road pick (05 §5.1 step 7, "highest class pair first"). Highway and MainStreet rank
        /// equal: MainStreet is the same through road inside the town zone, so a class change at a junction never decides it.
        /// </summary>
        public static int Rank(RoadClass c)
        {
            switch (c)
            {
                case RoadClass.Highway:
                case RoadClass.MainStreet: return 3;
                case RoadClass.Street: return 2;
                case RoadClass.Dirt: return 1;
                default: return 0;
            }
        }

        public static bool IsPaved(RoadClass c) => c != RoadClass.Dirt && c != RoadClass.Yard;

        public static bool IsThroughRoad(RoadClass c) => c == RoadClass.Highway || c == RoadClass.MainStreet;

        /// <summary>The class of a canon road from world.json's kind/paved (05 §5.1: street ? Street : paved ? Highway : Dirt).</summary>
        public static RoadClass FromCanon(string kind, bool paved)
        {
            if (kind != null && kind.StartsWith("street", StringComparison.Ordinal)) return RoadClass.Street;
            return paved ? RoadClass.Highway : RoadClass.Dirt;
        }
    }
}
