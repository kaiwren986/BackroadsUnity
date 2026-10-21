using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace Backroads.Core.WorldBake
{
    // M3 contracts of the world bake (ADR-006 stages W0-W8, ADR-007): the town source (TownData, I), terrain and plates
    // (lane W), roads, kerbs and driveway cuts (lane R), sites (lane S) and buildings (lane A). Every lane codes against
    // these types and nothing else; changes go through the integrator with an ADR-007 update (AGENTS.md rule 6).
    //
    // Units and frame everywhere: metres in the town's local frame (TownFrame): x east, z north, y up = elevation - Y0.
    // Plan positions are double2 with .x = x (east) and .y = z (north); heights are separate floats or doubles.
    // Polygons are counter-clockwise seen from above (x right, z up), not closed (first != last). The flip to the world
    // grid's JS layout (x_js = -x) happens once, in lane W's WorldData export, never here.

    /// <summary>
    /// The business type of a building (site-layouts design §4.1). Values are stable (byte, serialized); append only.
    /// From the classifier (BusinessClassifier, the rules of tools/osm/measure.mjs typeOf): fuel_shop and fuel_canopy
    /// are both Fuel, shop is Retail, roof (a free-standing roof or canopy away from fuel) is Roof. Downtown is a site
    /// plan type for street-front buildings (BuildingForm.Streetfront), never a classifier answer.
    /// </summary>
    public enum BusinessType : byte
    {
        Unknown = 0, House = 1, Apartments = 2, Accessory = 3, Downtown = 4, Retail = 5, Office = 6, Restaurant = 7, FastFood = 8,
        Supermarket = 9, Fuel = 10, CarRepair = 11, Motel = 12, Civic = 13, Church = 14, School = 15, Warehouse = 16, Industrial = 17,
        Farm = 18, Quarry = 19, Mill = 20, Harbor = 21, Roof = 22,
    }

    /// <summary>buildings.json "form": street-front commerce (wall within 4 m of the assumed road edge, or 10 m and sharing a
    /// wall, no lot in front) or a standalone commercial or civic building; None for houses and the rest.</summary>
    public enum BuildingForm : byte { None = 0, Standalone = 1, Streetfront = 2 }

    // ------------------------------------------------------------------------------------------------ the frame

    /// <summary>
    /// The town's local frame (ADR-006 §3, TownData manifest.json "frame"): UTM on WGS84 in the town's zone, then
    /// x = easting - originEasting, z = northing - originNorthing, y = elevation (3DEP, NAVD88) - Y0, with Y0 the lowest
    /// river point in the extract minus 2 m. The origin is the centre of the 2,048 m play area. Grid metres, not ground
    /// metres: UTM's scale factor (about 0.9997 here) is kept, so OSM vectors and the DEM line up exactly.
    /// </summary>
    [Serializable]
    public sealed class TownFrame
    {
        public int zone;
        public bool northern = true;
        public double originEasting, originNorthing;
        /// <summary>Elevation of y = 0 (metres, NAVD88).</summary>
        public double y0;
        /// <summary>Half the play area (1,024 m) and half the extract square (play area + 300 m skirt = 1,324 m).</summary>
        public double playHalf = 1024, extractHalf = 1324;

        public TownFrame() { }

        public TownFrame(int zone, double originEasting, double originNorthing, double y0, bool northern = true)
        {
            this.zone = zone; this.originEasting = originEasting; this.originNorthing = originNorthing; this.y0 = y0; this.northern = northern;
        }

        /// <summary>Latitude and longitude (degrees, WGS84) to local plan (x east, z north).</summary>
        public double2 ToLocal(double lat, double lon)
        {
            Utm.Forward(lat, lon, zone, out var e, out var n);
            return new double2(e - originEasting, n - originNorthing);
        }

        /// <summary>Local plan (x east, z north) to latitude and longitude (degrees, WGS84).</summary>
        public void ToLatLon(double2 local, out double lat, out double lon) =>
            Utm.Inverse(local.x + originEasting, local.y + originNorthing, zone, out lat, out lon);

        /// <summary>Local y of an elevation (metres, NAVD88), and back.</summary>
        public double YOf(double elevation) => elevation - y0;
        public double ElevationOf(double y) => y + y0;

        /// <summary>The Unity position of a local point (x, y, z): the local frame is Unity's frame.</summary>
        public static float3 ToUnity(double2 plan, double y) => new float3((float)plan.x, (float)y, (float)plan.y);

        public bool InPlayArea(double2 p) => math.abs(p.x) <= playHalf && math.abs(p.y) <= playHalf;
        public bool InExtract(double2 p) => math.abs(p.x) <= extractHalf && math.abs(p.y) <= extractHalf;
    }

    /// <summary>
    /// Transverse Mercator (UTM) on WGS84 by Krueger's series to third order in n, exactly as tools/scout/geo.mjs (good
    /// to well under a millimetre inside a zone). Northern hemisphere: northing has no false northing, as in the JS.
    /// </summary>
    public static class Utm
    {
        const double A = 6378137.0, F = 1.0 / 298.257223563, K0 = 0.9996, FalseEasting = 500000.0;
        static readonly double N = F / (2 - F);
        static readonly double N2 = N * N, N3 = N2 * N;
        static readonly double AA = A / (1 + N) * (1 + N2 / 4 + N2 * N2 / 64);
        static readonly double[] Alpha = { N / 2 - 2 * N2 / 3 + 5 * N3 / 16, 13 * N2 / 48 - 3 * N3 / 5, 61 * N3 / 240 };
        static readonly double[] Beta = { N / 2 - 2 * N2 / 3 + 37 * N3 / 96, N2 / 48 + N3 / 15, 17 * N3 / 480 };
        static readonly double[] Delta = { 2 * N - 2 * N2 / 3 - 2 * N3, 7 * N2 / 3 - 8 * N3 / 5, 56 * N3 / 15 };
        static readonly double E2N = 2 * Math.Sqrt(N) / (1 + N);
        const double D2R = Math.PI / 180;

        public static int ZoneOf(double lon) => (int)Math.Floor((lon + 180) / 6) + 1;
        public static double CentralMeridian(int zone) => (zone - 1) * 6 - 180 + 3;

        static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));

        public static void Forward(double lat, double lon, int zone, out double easting, out double northing)
        {
            double phi = lat * D2R, lam = (lon - CentralMeridian(zone)) * D2R;
            double s = Math.Sin(phi);
            double t = Math.Sinh(Atanh(s) - E2N * Atanh(E2N * s));
            double xi = Math.Atan2(t, Math.Cos(lam)), eta = Atanh(Math.Sin(lam) / Math.Sqrt(1 + t * t));
            double e = eta, n = xi;
            for (int j = 1; j <= 3; j++)
            {
                e += Alpha[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
                n += Alpha[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
            }
            easting = FalseEasting + K0 * AA * e;
            northing = K0 * AA * n;
        }

        public static void Inverse(double easting, double northing, int zone, out double lat, out double lon)
        {
            double xi = northing / (K0 * AA), eta = (easting - FalseEasting) / (K0 * AA);
            double xp = xi, ep = eta;
            for (int j = 1; j <= 3; j++)
            {
                xp -= Beta[j - 1] * Math.Sin(2 * j * xi) * Math.Cosh(2 * j * eta);
                ep -= Beta[j - 1] * Math.Cos(2 * j * xi) * Math.Sinh(2 * j * eta);
            }
            double chi = Math.Asin(Math.Sin(xp) / Math.Cosh(ep));
            double phi = chi;
            for (int j = 1; j <= 3; j++) phi += Delta[j - 1] * Math.Sin(2 * j * chi);
            double lam = Math.Atan2(Math.Sinh(ep), Math.Cos(xp));
            lat = phi / D2R;
            lon = CentralMeridian(zone) + lam / D2R;
        }
    }

    // ------------------------------------------------------------------------------------------------ plates (R, S, A -> W)

    /// <summary>What a plate grades (overview contract 4). Road and Walk come from lane R (R2), Lot and Yard from lane S
    /// (S4), Pad (building pads) from lane A / W.</summary>
    public enum PlateKind : byte { Road = 0, Walk = 1, Lot = 2, Yard = 3, Pad = 4 }

    /// <summary>How a plate gives its target heights.</summary>
    public enum PlateHeights : byte
    {
        /// <summary>y = plane.x * x + plane.y * z + plane.z.</summary>
        Plane = 0,
        /// <summary>The least-squares plane through fitPoints (lane W fits it, then clamps it to maxSlope).</summary>
        LeastSquares = 1,
        /// <summary>vertexY[i] at polygon[i], linear across the polygon (W triangulates it deterministically).</summary>
        PerVertex = 2,
    }

    /// <summary>The fixed application stages (ADR-007): every road plate before every site plate before every pad, then the blend.</summary>
    public enum PlateStage : byte { Roads = 0, Sites = 1, Buildings = 2 }

    /// <summary>
    /// A grading and surface plate (overview contract 4). Inside the polygon the ground is set to the target heights and
    /// the surface layer to <see cref="surface"/>; outside it, over <see cref="blend"/> metres, the ground eases back to
    /// what was there before this stage. Nothing outside polygon + blend changes.
    /// </summary>
    [Serializable]
    public sealed class Plate
    {
        /// <summary>Stable, unique id (e.g. "road:way/123:4", "lot:way/456"); the last tie-breaker of the order.</summary>
        public string id;
        public PlateKind kind;
        /// <summary>Local plan, CCW, not closed.</summary>
        public double2[] polygon;
        /// <summary>Surface layer id of the CanonData v1 surface table (World SurfaceTable): Grass 0, Dirt 1, Sand 2, Rock 3,
        /// Road 4, Gravel 5, Concrete 6, Track 7, Mud 8.</summary>
        public byte surface;
        public PlateHeights heights;
        /// <summary>Plane: y = plane.x * x + plane.y * z + plane.z (local metres).</summary>
        public double3 plane;
        /// <summary>LeastSquares: (x, y, z) points the plane is fitted through.</summary>
        public double3[] fitPoints;
        /// <summary>PerVertex: target y per polygon vertex.</summary>
        public float[] vertexY;
        /// <summary>Largest grade allowed anywhere on the plate (rise over run, 0.05 = 5 %).</summary>
        public float maxSlope;
        /// <summary>Width of the blend band outside the polygon, metres (side slopes at most 1V:3H inside it).</summary>
        public float blend;
        /// <summary>Within a stage, higher priority is applied later and wins where plates overlap.</summary>
        public int priority;
        /// <summary>Paved surfaces that vehicles must not deform (R2: NoDeform on asphalt, concrete, kerbs, walks).</summary>
        public bool noDeform;

        public PlateStage Stage => PlateOrder.StageOf(kind);
    }

    /// <summary>The one plate order every lane and every bake uses (ADR-007).</summary>
    public static class PlateOrder
    {
        public static PlateStage StageOf(PlateKind k) =>
            k == PlateKind.Road || k == PlateKind.Walk ? PlateStage.Roads : k == PlateKind.Pad ? PlateStage.Buildings : PlateStage.Sites;

        /// <summary>Stage, then priority (ascending: later wins), then id (ordinal). Submission order never matters.</summary>
        public static int Compare(Plate a, Plate b)
        {
            int c = ((int)a.Stage).CompareTo((int)b.Stage);
            if (c != 0) return c;
            c = a.priority.CompareTo(b.priority);
            if (c != 0) return c;
            return string.CompareOrdinal(a.id, b.id);
        }

        public static readonly Comparison<Plate> Comparison = Compare;
    }

    /// <summary>Lane W's terrain bake takes the plates (W implements; R and S submit). It sorts them by PlateOrder and
    /// applies them after every plate is in; the same plates give the same bytes.</summary>
    public interface IPlateSink
    {
        void Submit(Plate plate);
    }

    // ------------------------------------------------------------------------------------------------ kerb frontage (R -> S)

    /// <summary>Street classes as lane R builds them (mirrors Backroads.Roads.RoadClass value for value).</summary>
    public enum StreetClass : byte { Highway = 0, MainStreet = 1, Street = 2, Dirt = 3, Yard = 4, Driveway = 5 }

    /// <summary>Kerb profiles (research 05 §5.7): none on unkerbed roads, 150 mm barrier, 100 mm mountable, 25 mm dropped.</summary>
    public enum KerbKind : byte { None = 0, Barrier150 = 1, Mountable100 = 2, Dropped25 = 3 }

    /// <summary>Why a stretch of frontage takes no driveway.</summary>
    public enum FrontageBlock : byte { CornerReturn = 0, Crosswalk = 1, Ramp = 2, StopLine = 3, Inlet = 4, Pole = 5, Other = 6 }

    [Serializable]
    public struct ForbiddenInterval
    {
        /// <summary>Arc length along the face's kerb line, metres, s0 &lt; s1.</summary>
        public float s0, s1;
        public FrontageBlock reason;
    }

    /// <summary>
    /// One block face (overview contract 2): the kerb face line (or the pavement edge on unkerbed roads) with its arc
    /// length and heights, the sidewalk back edge sampled at the same arc lengths, the street's class, speed and one-way,
    /// and the intervals where no driveway may start. Arrays kerb, s, topOfKerb, gutter, walkBack and walkBackY have the
    /// same length; s[0] = 0 and s increases. The face runs with the block on its left (so the street is on the right).
    /// </summary>
    [Serializable]
    public sealed class KerbFrontage
    {
        public int faceId;
        /// <summary>The OSM way of the street ("way/123").</summary>
        public string osmWay;
        public double2[] kerb;
        public float[] s;
        public float[] topOfKerb;
        public float[] gutter;
        /// <summary>Sidewalk back edge at the same s (equal to kerb where there is no walk), and its y: lot plates meet it exactly at every cut.</summary>
        public double2[] walkBack;
        public float[] walkBackY;
        public KerbKind kerbKind;
        public StreetClass streetClass;
        public float speedKmh;
        public bool oneway;
        public ForbiddenInterval[] forbidden;
        /// <summary>True for R0o's stand-in (centreline offset by half-width + kerb + walk, 10 m corner clearance only).</summary>
        public bool provisional;

        public float Length => s == null || s.Length == 0 ? 0f : s[s.Length - 1];
    }

    /// <summary>Lane R publishes the frontage (R implements; S reads). Faces are ordered by faceId.</summary>
    public interface IKerbFrontageSource
    {
        bool Provisional { get; }
        IReadOnlyList<KerbFrontage> Faces { get; }
        /// <summary>The face with this id, or null.</summary>
        KerbFrontage Face(int faceId);
    }

    // ------------------------------------------------------------------------------------------------ driveway cuts (S <-> R)

    public enum DrivewayKind : byte { Residential = 0, Commercial = 1, Truck = 2, FuelIn = 3, FuelOut = 4, Field = 5 }

    /// <summary>Lot and drive surfaces (site-layouts design §2.2).</summary>
    public enum SiteSurface : byte { Asphalt = 0, Concrete = 1, Gravel = 2, Dirt = 3, Pavers = 4, Grass = 5 }

    /// <summary>A site asks for a driveway (overview contract 3).</summary>
    [Serializable]
    public struct DrivewayRequest
    {
        /// <summary>Unique per bake (lane S assigns).</summary>
        public int id;
        public int faceId;
        /// <summary>The throat along the face, metres of arc length.</summary>
        public float s0, s1;
        public DrivewayKind kind;
        /// <summary>Linear flare length per side (m), or 0 when returnRadius is used.</summary>
        public float flareLength;
        /// <summary>Radius returns (m) for truck entrances on unkerbed roads, or 0.</summary>
        public float returnRadius;
        public SiteSurface surface;
        /// <summary>True for a driveway or service way mapped in OSM: it moves only when it hits a corner return.</summary>
        public bool mapped;
        /// <summary>The mapped OSM way ("way/123"), or null.</summary>
        public string osmWay;
    }

    public enum CutOutcome : byte { Baked = 0, Moved = 1, Merged = 2, Rejected = 3 }

    /// <summary>Lane R's answer to one request (overview contract 3).</summary>
    [Serializable]
    public sealed class DrivewayCut
    {
        public int requestId;
        public CutOutcome outcome;
        public int faceId;
        /// <summary>The throat as baked (after a move); undefined when Rejected.</summary>
        public float s0, s1;
        /// <summary>How far the throat moved along s (at most CUT_SHIFT_MAX, 3 m).</summary>
        public float shift;
        /// <summary>Merged: the request whose cut this one shares; otherwise -1.</summary>
        public int mergedWith = -1;
        public KerbKind kerb = KerbKind.Dropped25;
        /// <summary>The apron slab across the walk to the lot edge, CCW.</summary>
        public double2[] apron;
        /// <summary>Cross slope of the walk crossing (at most 0.02).</summary>
        public float crossSlope;
        /// <summary>Where the lot plate must meet the cut: the apron's lot-side edge and its heights.</summary>
        public double2 lotEdgeA, lotEdgeB;
        public float lotEdgeYA, lotEdgeYB;
        /// <summary>Rejected: the interval that blocked it and a reason for the report.</summary>
        public FrontageBlock rejectedBy;
        public string reason;
    }

    /// <summary>Lane R bakes the cuts (R implements; S asks). Deterministic: requests are handled sorted by (faceId, s0, id),
    /// and the answer has one cut per request, in the request list's order.</summary>
    public interface IDrivewayCutter
    {
        DrivewayCut[] Resolve(IReadOnlyList<DrivewayRequest> requests);
    }

    // ------------------------------------------------------------------------------------------------ doors (S -> A)

    public enum DoorKind : byte { Main = 0, Service = 1, Garage = 2, Dock = 3, Bay = 4, DriveThroughWindow = 5 }

    /// <summary>
    /// A door decided by the site plan (design §7a): lane A models an opening there, so stalls, aprons and docks line up.
    /// The wall edge is footprint edge wallEdge (vertex wallEdge to wallEdge + 1, wrapping) of the building's CCW
    /// polygon in buildings.json; offset is the distance from that vertex to the door's centre along the edge.
    /// </summary>
    [Serializable]
    public struct DoorSpec
    {
        /// <summary>buildings.json "osm" ("way/123") and "part" (0 unless the building has several outer rings).</summary>
        public string building;
        public int part;
        public DoorKind kind;
        public int wallEdge;
        public float offset;
        public float width;
        /// <summary>Local y of the sill where the site plate meets the door; NaN when the site does not grade there.</summary>
        public float sillY;
    }
}
