using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites
{
    // Blocks and parcels (card sites-S1, design docs/design/site-layouts.md §3). Local metres of the town frame (x east,
    // z north in .y); outer rings CCW, holes CW, not closed. Enum values are stable (append only).

    /// <summary>A parcel holds a building (and its accessory buildings), or is open land: unclaimed frontage, a mapped
    /// area nobody's building serves, or public land (parks, pitches, cemeteries).</summary>
    public enum ParcelKind : byte { Building = 0, Vacant = 1, Public = 2 }

    /// <summary>What a mapped fixed area is (card S1 step 2).</summary>
    public enum FixedKind : byte
    {
        Parking = 0, Fuel = 1, Farmyard = 2, Industrial = 3, Retail = 4, Commercial = 5, Quarry = 6, Harbour = 7, Marina = 8,
        Works = 9, Pier = 10, CaravanSite = 11,
    }

    /// <summary>A stretch of a KerbFrontage face that a parcel fronts (arc length s0..s1 along the face).</summary>
    [Serializable]
    public struct FrontageRef
    {
        public int faceId;
        public float s0, s1;
        public StreetClass street;
        public string osmWay;
        public float Length => s1 - s0;
    }

    /// <summary>
    /// A block face as the site program reads it (roads-R2's KerbFrontage): the kerb line and the walk's back edge at the
    /// same arc lengths s (s[0] = 0), running with the block on its left. Parcels front the back edge; driveway requests
    /// use s.
    /// </summary>
    [Serializable]
    public sealed class FrontLine
    {
        public int faceId, block = -1;
        public string osmWay;
        public StreetClass street;
        public bool kerbed, alley, bridge;
        public double2[] kerb, back;
        public float[] s;
        public float Length => s == null || s.Length == 0 ? 0f : s[s.Length - 1];

        /// <summary>The back edge at arc length t (clamped), and the unit normal into the block there.</summary>
        public double2 BackAt(double t, out double2 normal)
        {
            int n = back.Length;
            normal = new double2(0, 1);
            if (n == 1) return back[0];
            t = Math.Max(0, Math.Min(t, s[n - 1]));
            int i = 0;
            while (i < n - 2 && s[i + 1] < t) i++;
            double seg = s[i + 1] - s[i];
            double u = seg > 1e-9 ? (t - s[i]) / seg : 0;
            var d = back[i + 1] - back[i];
            if (math.lengthsq(d) < 1e-18) d = kerb[i + 1] - kerb[i];
            if (math.lengthsq(d) > 1e-18) normal = Poly2.Perp(math.normalize(d));
            return back[i] + (back[i + 1] - back[i]) * u;
        }

        /// <summary>The kerb line at arc length t (clamped).</summary>
        public double2 KerbAt(double t)
        {
            int n = kerb.Length;
            if (n == 1) return kerb[0];
            t = Math.Max(0, Math.Min(t, s[n - 1]));
            int i = 0;
            while (i < n - 2 && s[i + 1] < t) i++;
            double seg = s[i + 1] - s[i];
            double u = seg > 1e-9 ? (t - s[i]) / seg : 0;
            return kerb[i] + (kerb[i + 1] - kerb[i]) * u;
        }
    }

    /// <summary>A mapped lot, yard or works area kept exactly as mapped (card S1 step 2).</summary>
    [Serializable]
    public sealed class FixedArea
    {
        public int id;
        public string osm;
        public FixedKind kind;
        public double2[] outer;
        public double2[][] holes = Array.Empty<double2[]>();
        public double area;
        /// <summary>The buildings it serves (indices into the town's building list): the nearest non-house outline within
        /// 30 m, and a second one within 6 m of the first's distance (shared). Empty when it serves none.</summary>
        public int[] servedBy = Array.Empty<int>();
        public int parcel = -1;
    }

    /// <summary>A block: one kerb loop of the street graph cut back to the walk's back edge, bounded at RURAL_DEPTH from
    /// its frontage where no road closes it. May hold holes (a road ring inside the block) and be split in pieces.</summary>
    [Serializable]
    public sealed class Block
    {
        public int id;
        /// <summary>The R2 kerb loop it comes from.</summary>
        public int loop;
        public List<double2[]> outers = new List<double2[]>();
        public List<double2[]> holes = new List<double2[]>();
        /// <summary>KerbFrontage faces around it.</summary>
        public int[] faces = Array.Empty<int>();
        public double area;
        /// <summary>True when RURAL_DEPTH cut it.</summary>
        public bool rural;
    }

    /// <summary>One parcel (design §2.2).</summary>
    [Serializable]
    public sealed class Parcel
    {
        public int id, block;
        public ParcelKind kind;
        /// <summary>Outer ring (CCW) and holes (CW).</summary>
        public double2[] outer;
        public double2[][] holes = Array.Empty<double2[]>();
        public double area;
        /// <summary>Indices into the town's building list; [0] is the main building, the rest its accessory buildings.</summary>
        public int[] buildings = Array.Empty<int>();
        public BusinessType type;
        public BuildingForm form;
        /// <summary>The faces it fronts, longest first.</summary>
        public FrontageRef[] frontage = Array.Empty<FrontageRef>();
        public bool corner, alley;
        /// <summary>Area over frontage length (metres), or the farthest point from the frontage when it has none.</summary>
        public float depth;
        public int[] fixedAreas = Array.Empty<int>();
        /// <summary>No frontage of its own: the path from the parcel to the nearest frontage across others (a flag lot or a
        /// farm down a long drive), and that face; else null and -1.</summary>
        public double2[] easement;
        public int easementFace = -1;
        /// <summary>Public land use (park, pitch, cemetery) for Public parcels.</summary>
        public string landuse;
        public double2 Centroid => outer != null && outer.Length > 0 ? Poly2.Centroid(outer) : 0;
    }

    /// <summary>The town's buildings as the site program sees them (from TownData buildings.json).</summary>
    [Serializable]
    public sealed class SiteBuilding
    {
        public int index;
        public string osm;
        public double2[] poly;
        public double2 centroid;
        public double area;
        public BusinessType business;
        public BuildingForm form;
        public string type;
        public bool inPlay, inferred;
        /// <summary>The street the extract matched it to ("way/123"), or null.</summary>
        public string street;
        public float setback = float.NaN;
        /// <summary>Floor height (buildings.json "y": the DEM at the centroid, where lane W's pad and lane A's model stand), or NaN.</summary>
        public double floorY = double.NaN;
        public Dictionary<string, string> tags = new Dictionary<string, string>();

        /// <summary>Garages, sheds and free-standing roofs get no parcel of their own.</summary>
        public bool Accessory => business == BusinessType.Accessory || business == BusinessType.Roof;

        /// <summary>A roof without walls (a fuel canopy, a carport, a shelter): surfaces run under it.</summary>
        public bool OpenRoof => tags != null && tags.TryGetValue("building", out var v) && (v == "roof" || v == "canopy" || v == "carport");
    }
}
