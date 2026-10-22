using System;
using System.Collections.Generic;

namespace Backroads.Roads
{
    /// <summary>
    /// A road's cross section (roads-R0o step 2): pavement edge to centreline on each side (kerb face to kerb face in the
    /// kerb zone), from the tags in this order: width, then lanes x lane width (+ parking lanes), then the class default.
    /// Left is the left of the road's direction of travel along its points.
    /// </summary>
    public sealed class CrossSection
    {
        public double hwLeft, hwRight;
        public int lanes;
        public double laneWidth;
        /// <summary>Parking lane width on each side (0 when none).</summary>
        public double parkingLeft, parkingRight;
        /// <summary>Gutter pan width included on each side (kerbed MainStreet only).</summary>
        public double gutter;
        /// <summary>"width", "lanes" or "class": which rule set the width.</summary>
        public string source = "class";
        public double Width => hwLeft + hwRight;
        public double HalfWidth => 0.5 * (hwLeft + hwRight);
    }

    /// <summary>
    /// What the road graph is built from. Two sources fill it: the canon road polylines (CanonData/v1 world.json roads[],
    /// RoadGraphCanon, the test world) with the lots and the legacy traffic nodes (traffic.json, only to number the nodes
    /// as 05 does); or a town's OpenStreetMap roads on its DEM (OsmRoadImporter, TownData, roads-R0o) with the mapped
    /// signs and crossings, the building footprints and the kerb and downtown zones. Everything is in Unity space (canon:
    /// x = -JS x; towns: the local frame, x east, z north). Plain C# so the builder runs and is tested without a scene.
    /// </summary>
    public sealed class RoadGraphInput
    {
        public sealed class Road
        {
            public int id;
            public string kind;
            public bool paved, startJ, endJ;
            public double hw;
            /// <summary>Centreline in Unity space; d = canon distance along the road (2D arc length, exactly as JS wrote it).</summary>
            public D3[] pts;
            public double[] d;
            public RoadClass cls;
            public double Length => d[d.Length - 1];

            // --- OSM roads only (roads-R0o); empty or default for the canon
            /// <summary>The OSM ways this road was chained from, in order ("way/123").</summary>
            public string[] osmWays = Array.Empty<string>();
            /// <summary>The OSM highway tag (secondary, residential, service...) and service tag, or "".</summary>
            public string highway = "", service = "";
            public CrossSection xs;
            /// <summary>One-way: +1 along the points, -1 against them, 0 both ways.</summary>
            public int oneway;
            public bool bridge, tunnel, roundabout;
            public int layer;
            /// <summary>The OSM surface tag, or "".</summary>
            public string surface = "";
            /// <summary>The OSM sidewalk tag (sidewalk=*, or sidewalk:left/right/both joined), or "" (R2 reads it).</summary>
            public string sidewalk = "";
            /// <summary>Parking tags as mapped ("parking:both=no"...), or "".</summary>
            public string parking = "";
            /// <summary>Speed in km/h: maxspeed when tagged, else the class default.</summary>
            public double speedKmh;
            /// <summary>Inside the town kerb zone (KERB_ZONE) or the downtown zone (DOWNTOWN_ZONE) at its midpoint.</summary>
            public bool inKerbZone, inDowntown;
            /// <summary>The road was cut at the play-area edge at its first or last point (EDGE_RULE).</summary>
            public bool cutStart, cutEnd;
        }

        /// <summary>A mapped sign, signal or crossing on a road, at arc length s (roads-R0o step 5).</summary>
        public sealed class Feature
        {
            public RoadFeatureKind kind;
            public int road;
            public double s;
            /// <summary>Traffic it applies to: +1 travelling along the road's points, -1 against them, 0 both or unknown.</summary>
            public int direction;
            public long osmNode;
            /// <summary>The OSM crossing tag for crossings (marked, unmarked, zebra, traffic_signals...), else "".</summary>
            public string crossing = "";
        }

        public struct Lot
        {
            public D2 p;
            public string zone;
        }

        public struct LegacyNode
        {
            public int id;
            public D2 p;
        }

        /// <summary>A building footprint outline (Unity xz): the 4 corners of a rectangle, or a polygon around a circle.</summary>
        public struct Footprint
        {
            public D2[] corners;
        }

        public readonly List<Road> roads = new List<Road>();
        public readonly List<Lot> lots = new List<Lot>();
        public readonly List<LegacyNode> legacyNodes = new List<LegacyNode>();
        public readonly List<Footprint> footprints = new List<Footprint>();
        public readonly List<Feature> features = new List<Feature>();

        /// <summary>
        /// The town kerb zone as a polygon (TownData manifest zones.kerbZone, CCW). When set it replaces 05 §5.1's street hull
        /// + 20 m: a real town has residential streets far outside its village, so the hull would swallow the whole map.
        /// </summary>
        public D2[] zonePolygon;
        /// <summary>The downtown zone (TownData manifest zones.downtownZone, CCW), or null.</summary>
        public D2[] downtownPolygon;
        /// <summary>Where the input came from: "CanonData/v1" or "TownData/(town)/v1".</summary>
        public string source = "";

        /// <summary>Adds a building footprint polygon (Unity xz).</summary>
        public void AddFootprint(D2[] corners) => footprints.Add(new Footprint { corners = corners });

        /// <summary>
        /// Adds a footprint given in JS space: a rectangle (centre, half extents, three.js yaw) like world.js stampConcrete, whose
        /// local (lx, lz) sits at (cx + c lx + s lz, cz - s lx + c lz); or, without a rectangle, a circle of radius r (8 points).
        /// </summary>
        public void AddJsFootprint(double cx, double cz, double rot, double hw, double hd, bool hasRect, double r)
        {
            D2[] pts;
            if (hasRect)
            {
                double c = Math.Cos(rot), s = Math.Sin(rot);
                pts = new D2[4];
                int k = 0;
                foreach (var (lx, lz) in new[] { (hw, hd), (-hw, hd), (-hw, -hd), (hw, -hd) })
                    pts[k++] = new D2(-(cx + c * lx + s * lz), cz - s * lx + c * lz);
            }
            else
            {
                pts = new D2[8];
                for (int k = 0; k < 8; k++) pts[k] = new D2(-(cx + r * Math.Cos(k * Math.PI / 4)), cz + r * Math.Sin(k * Math.PI / 4));
            }
            footprints.Add(new Footprint { corners = pts });
        }
        /// <summary>SHA-256 of the source manifest (CanonData/v1/manifest.json or TownData/(town)/v1/manifest.json); empty for synthetic input.</summary>
        public string sourceManifestSha256 = "";

        public int PointCount
        {
            get
            {
                int c = 0;
                foreach (var r in roads) c += r.pts.Length;
                return c;
            }
        }

        /// <summary>Adds a road given in JS coordinates (x, y, z, d per point), converting to Unity space.</summary>
        public Road AddJsRoad(int id, string kind, bool paved, double hw, bool startJ, bool endJ, double[] x, double[] y, double[] z, double[] d)
        {
            if (x.Length != y.Length || x.Length != z.Length || x.Length != d.Length || x.Length < 2)
                throw new ArgumentException($"road {id}: point arrays differ in length or have fewer than 2 points");
            var r = new Road
            {
                id = id, kind = kind, paved = paved, hw = hw, startJ = startJ, endJ = endJ,
                pts = new D3[x.Length], d = (double[])d.Clone(), cls = RoadClasses.FromCanon(kind, paved),
            };
            for (int i = 0; i < x.Length; i++) r.pts[i] = new D3(-x[i], y[i], z[i]);
            roads.Add(r);
            return r;
        }

        /// <summary>Adds a road given directly in Unity space; d is recomputed as the 2D arc length.</summary>
        public Road AddUnityRoad(int id, RoadClass cls, double hw, bool startJ, bool endJ, IList<D3> pts)
        {
            var arr = new D3[pts.Count];
            pts.CopyTo(arr, 0);
            var r = new Road
            {
                id = id, kind = cls == RoadClass.Street ? "street" : cls == RoadClass.Dirt ? "dirt" : "paved",
                paved = cls != RoadClass.Dirt, hw = hw, startJ = startJ, endJ = endJ, pts = arr, d = Polyline.ArcLength(arr), cls = cls,
            };
            roads.Add(r);
            return r;
        }
    }
}
