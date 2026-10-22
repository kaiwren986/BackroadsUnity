using System;
using Backroads.Core.WorldBake;

namespace Backroads.Roads
{
    /// <summary>The roads-R2 numbers: research 05 §5.7 (kerb profile, walks, corner ramps, driveway cuts) and §5.10 (road plates).</summary>
    public sealed class KerbSettings
    {
        /// <summary>Walk widths from the kerb top's back edge (card step 1): residential streets, MainStreet outside downtown, downtown where no building line is near.</summary>
        public double walkStreet = 1.5, walkMain = 1.8, walkDowntown = 2.4;
        /// <summary>Downtown the walk fills to a building line this close to the kerb; closer than downtownMinClear is reported.</summary>
        public double downtownFill = 6.0, downtownMinClear = 1.0;
        /// <summary>Kerb top width behind the face (05 §5.7: face top at 0.012, top back at 0.15).</summary>
        public double kerbTop = 0.15;
        /// <summary>Face heights above the flowline by kerb type (05 §5.7).</summary>
        public double barrier = 0.15, mountable = 0.10, dropped = 0.025;
        /// <summary>Walk cross fall towards the street (design 1.5 %, PROWAG maximum 2 %).</summary>
        public double walkCrossSlope = 0.015;
        /// <summary>Unkerbed roads: a gravel shoulder this wide, starting this far below the pavement edge, falling at the crown slope.</summary>
        public double shoulder = 1.2, shoulderLip = 0.02;
        /// <summary>Behind a walk: a flat grass verge at the walk's back edge height (05 §5.10's flat zone).</summary>
        public double verge = 1.2;
        /// <summary>No driveway within the corner return plus this (card step 4): local streets, MainStreet and Highway.</summary>
        public double cornerReturnLocal = 3.0, cornerReturnMajor = 9.0;
        /// <summary>Depressed corners (05 §5.7): the lowered stretch is half the arc, clamped to these; ramps of rampRun either side.</summary>
        public double depressedMin = 2.4, depressedMax = 6.0, rampRun = 1.83, landing = 1.2, warningDepth = 0.61;
        /// <summary>roads-R4: an alley mouth's dropped kerb flares back to the barrier over this length each side (the corner ramp run: 6.8 %).</summary>
        public double alleyFlare = 1.83;
        /// <summary>roads-R4 step 4 (05 §5.12): where kerbs end at an unkerbed road the kerb tapers to flush over this length; the walk ramps down with it.</summary>
        public double taper = 3.0;
        /// <summary>Perpendicular ramps where the walk is at least this deep: two openings this wide per corner, run, landing.</summary>
        public double perpendicularDepth = 3.2, perpRampWidth = 1.5, perpRampRun = 2.0;
        /// <summary>Forbidden margins: stop-line zone half length, crosswalk margin, inlet half length.</summary>
        public double stopZone = 2.0, crosswalkMargin = 0.5, inletHalf = 0.75;
        /// <summary>05 §5.10: the ground under asphalt and under kerbs and walks.</summary>
        public double sinkAsphalt = 0.06, sinkWalk = 0.05;
        /// <summary>Clipper2 integer scale: 10,000 units per metre (0.1 mm).</summary>
        public double scale = 10000.0;
        /// <summary>Block loops smaller than this (m²) are slivers between nearly touching roads and are dropped (reported).</summary>
        public double sliverArea = 2.0;
    }

    /// <summary>Which roads get kerbs and walks (roads-R2 step 1); R1's gutter pans use the same rule.</summary>
    public static class KerbRules
    {
        /// <summary>
        /// Kerbed: a Street or MainStreet inside KERB_ZONE, or one anywhere with a mapped sidewalk on a side. Rural roads,
        /// highways outside the zone, service roads and dirt roads have unkerbed edges (a gravel shoulder).
        /// </summary>
        public static bool Kerbed(RoadGraphInput.Road road, RoadClass cls)
        {
            if (road == null || (cls != RoadClass.Street && cls != RoadClass.MainStreet)) return false;
            if (road.inKerbZone) return true;
            Sidewalks(road.sidewalk, out bool l, out bool r, out bool tagged);
            return tagged && (l || r);
        }

        /// <summary>A walk on the road's left (side 0) or right (side 1) of its points: the mapped sidewalk tags, else both sides of a kerbed road.</summary>
        public static bool Walk(RoadGraphInput.Road road, RoadClass cls, int side)
        {
            if (!Kerbed(road, cls)) return false;
            Sidewalks(road.sidewalk, out bool l, out bool r, out bool tagged);
            if (!tagged) return true;
            return side == 0 ? l : r;
        }

        /// <summary>
        /// Reads R0o's joined sidewalk tag ("sidewalk=both", "sidewalk:left=yes;sidewalk:right=no", already flipped to the
        /// road's direction): yes, both, separate and the side names mean a walk; no and none mean none.
        /// </summary>
        public static void Sidewalks(string tag, out bool left, out bool right, out bool tagged)
        {
            left = right = tagged = false;
            if (string.IsNullOrEmpty(tag)) return;
            foreach (var part in tag.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq).Trim(), v = part.Substring(eq + 1).Trim();
                bool on = v == "yes" || v == "separate" || v == "both";
                switch (k)
                {
                    case "sidewalk":
                        tagged = true;
                        left = on || v == "left";
                        right = on || v == "right";
                        break;
                    case "sidewalk:both": tagged = true; left = right = on; break;
                    case "sidewalk:left": tagged = true; left = on; break;
                    case "sidewalk:right": tagged = true; right = on; break;
                }
            }
        }

        /// <summary>The walk width (kerb top's back edge to the walk's back edge) by class outside downtown.</summary>
        public static double WalkWidth(RoadClass cls, KerbSettings st) => cls == RoadClass.MainStreet ? st.walkMain : st.walkStreet;

        /// <summary>The surface layer of a road plate (World SurfaceTable ids): asphalt, concrete, gravel or dirt.</summary>
        public static byte Surface(RoadClass cls, string surfaceTag)
        {
            string t = surfaceTag ?? "";
            if (t == "concrete" || t.StartsWith("concrete:", StringComparison.Ordinal) || t == "paving_stones") return 6;
            if (t == "asphalt" || t == "paved" || t == "chipseal") return 4;
            if (t == "gravel" || t == "fine_gravel" || t == "compacted" || t == "pebblestone") return 5;
            if (t == "dirt" || t == "ground" || t == "earth" || t == "mud" || t == "grass") return 1;
            switch (cls)
            {
                case RoadClass.Dirt: return 1;
                case RoadClass.Yard: return 5;
                default: return 4;
            }
        }

        /// <summary>Paved plates (asphalt, concrete) sink under the road mesh and take no ruts; gravel and dirt are the ground itself.</summary>
        public static bool Paved(byte surface) => surface == 4 || surface == 6;

        /// <summary>The surface layer for a site surface (driveway aprons).</summary>
        public static byte Surface(SiteSurface s)
        {
            switch (s)
            {
                case SiteSurface.Asphalt: return 4;
                case SiteSurface.Concrete: case SiteSurface.Pavers: return 6;
                case SiteSurface.Gravel: return 5;
                case SiteSurface.Dirt: return 1;
                default: return 0;
            }
        }
    }
}
