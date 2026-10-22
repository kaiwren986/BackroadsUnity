using System;
using System.Collections.Generic;
using System.Globalization;

namespace Backroads.Roads
{
    /// <summary>A town's roads as the importer reads them (TownData roads.json, buildings.json, sites.json, the DEM, the manifest zones).</summary>
    public sealed class OsmRoadSource
    {
        public sealed class Node
        {
            public long id;
            public double x, z;
            public Dictionary<string, string> tags;
        }

        public sealed class Way
        {
            /// <summary>"way/123".</summary>
            public string osm;
            public long id;
            public long[] nodes;
            public Dictionary<string, string> tags;
        }

        public readonly Dictionary<long, Node> nodes = new Dictionary<long, Node>();
        public readonly List<Way> ways = new List<Way>();
        public IHeightField dem;
        /// <summary>KERB_ZONE and DOWNTOWN_ZONE (manifest zones), CCW; either may be null.</summary>
        public D2[] kerbZone, downtownZone;
        public readonly List<D2[]> footprints = new List<D2[]>();
        /// <summary>Mapped parking lots (sites.json parking): service ways inside them belong to the sites program.</summary>
        public readonly List<D2[]> parkingLots = new List<D2[]>();
        /// <summary>Half the play area (PLAY_AREA, 1,024 m for a 2 km town).</summary>
        public double playHalf = 1024;
        public string source = "", manifestSha256 = "";
    }

    /// <summary>The numbers of roads-R0o (card steps 2, 3 and 7), research 05 and the browser game's makeRoad.</summary>
    public sealed class OsmImportSettings
    {
        /// <summary>Longest centreline segment after resampling (metres); OSM vertices are kept.</summary>
        public double resample = 2.0;
        /// <summary>Height smoothing: a box of this radius in metres of arc length (the card's "box r 12 m"), this many passes.</summary>
        public double smoothRadius = 12.0;
        public int smoothPasses = 4;
        /// <summary>
        /// Grade limits (rise over run), with the real road's own grade (the smoothed DEM profile's) + gradeSlack as the
        /// floor: never flatter than the real road, and up to one point steeper so a junction warp settles within metres.
        /// </summary>
        public double maxGradePaved = 0.11, maxGradeDirt = 0.15, gradeSlack = 0.01;
        /// <summary>A road meeting a higher-ranked road warps onto its height over this distance (05 §5.6: 12 m).</summary>
        public double warpDistance = 12.0;
        /// <summary>EDGE_RULE: roads end this far inside the play-area edge.</summary>
        public double edgeInset = 50.0;
        /// <summary>A vertex where the road turns at least this much over +-cornerWindow metres becomes an L-bend node.</summary>
        public double cornerTurnDeg = 45.0, cornerWindow = 6.0, cornerClearance = 12.0;
        /// <summary>Pieces shorter than this are not split off (zone boundaries, the edge).</summary>
        public double minPiece = 2.0;
        /// <summary>Cross sections (card step 2): lane widths by class, parking lane, gutter pan, class defaults (kerb to kerb).</summary>
        public double laneHighway = 3.6, laneMain = 3.3, laneStreet = 3.3, laneDirt = 2.7, laneYard = 3.0, parkingLane = 2.4, gutter = 0.6;
        public double streetWidth = 9.0, ruralStreetWidth = 6.2, dirtWidth = 5.4, yardWidth = 6.0;
        /// <summary>
        /// Fitting to buildings (roads-R1): a side whose half-width would reach within fitMargin of a mapped building
        /// narrows to clear it, but never below one lane (floors by class; yards and alleys 1.5 m); buildings beyond a
        /// piece's ends (or within fitEnds of them) belong to the cross street and do not count.
        /// </summary>
        public double fitMargin = 0.3, fitEnds = 0.0, floorThrough = 3.3, floorStreet = 2.75, floorDirt = 1.8, floorYard = 1.5;
        /// <summary>Speeds by class (km/h) where maxspeed is not tagged: 55 mph highways, 25 mph in town, 10 mph yards.</summary>
        public double speedHighway = 88.5, speedTown = 40.2, speedDirt = 40.2, speedYard = 16.1;
        /// <summary>roads-R4 step 6: two junctions joined by a way segment shorter than this become one junction (05 §5.1 step 5).</summary>
        public double mergeJunctions = 8.0;
    }

    /// <summary>A road cut at the play-area edge (EDGE_RULE), for the report and lane W.</summary>
    public struct EdgeCut
    {
        public int road;
        public string osmWay;
        public D2 at;
        /// <summary>'N', 'E', 'S' or 'W'.</summary>
        public char side;
        /// <summary>Distance along the road from the cut to the next junction outside it, or NaN when none.</summary>
        public double junctionBeyond;
    }

    /// <summary>A mapped driveway, parking aisle or drive-through that meets a road: a sites-S1 driveway request with mapped = true.</summary>
    public struct MappedDriveway
    {
        public string osmWay, service;
        public long node;
        public D2 at;
        public int road;
        public double s;
    }

    /// <summary>A group of roads apart from the main network (OsmImportReport.groups).</summary>
    public struct RoadGroup
    {
        public bool kept;
        /// <summary>"connects outside the play area", "service roads reached only through a site way or a path (sites)" or "isolated in the map data".</summary>
        public string reason;
        public string[] osmWays;
        public double length;
        public D2 at;
    }

    /// <summary>A stretch steeper than the paved (11 %) or dirt (15 %) limit because the real road is that steep.</summary>
    public struct SteepStretch
    {
        /// <summary>The road it lies on, its extent along the road (arc length s0..s1) and where its steepest piece is.</summary>
        public int road;
        public double s0, s1;
        public D2 at;
        public double length, maxGrade, realGrade;
    }

    public sealed class OsmImportReport
    {
        public int waysRead, waysUsed, segments, chains, cornerBends, strokes, roads, samples;
        /// <summary>One line per skipped way: "way/123 highway=footway: not a road for vehicles".</summary>
        public readonly List<string> skipped = new List<string>();
        public readonly SortedDictionary<string, int> skippedByReason = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly List<EdgeCut> edgeCuts = new List<EdgeCut>();
        public readonly List<MappedDriveway> driveways = new List<MappedDriveway>();
        public readonly List<SteepStretch> steep = new List<SteepStretch>();
        public readonly List<string> notes = new List<string>();
        /// <summary>Heights: samples moved by a junction warp; largest change from the smoothed DEM profile outside warps and decks;
        /// segments over the limit and steeper than the real road (two junction heights too close for the limit; should be 0).</summary>
        public int warpedSamples, clampedSamples, gradeConflicts;
        public double maxDevFromSmoothed;
        /// <summary>Groups of roads not connected to the main network inside the play area: kept when they reach the edge
        /// (they connect outside it), dropped when they are service roads reached only through a site way or a path.</summary>
        public readonly List<RoadGroup> groups = new List<RoadGroup>();
        /// <summary>Final centreline height minus the bare-earth DEM, outside bridge decks (metres).</summary>
        public double demMeanAbs, demP95Abs, demMaxAbs;
        public int featuresPlaced, featuresOutside;
        /// <summary>Road sides narrowed to clear a building, and sides that cannot clear one even at one lane.</summary>
        public readonly List<string> fitted = new List<string>();
        public readonly List<string> cannotClear = new List<string>();
        /// <summary>roads-R4: junctions merged because a way joined them over less than mergeJunctions (one line each).</summary>
        public readonly List<string> mergedJunctions = new List<string>();
    }

    /// <summary>
    /// roads-R0o: a town's OpenStreetMap roads on its DEM, as RoadGraphInput for RoadGraphBuilder. Deterministic: the same
    /// source gives the same input, value for value. Steps:
    ///  1. Class from highway (card step 1); footways, site driveways and aisles and service ways inside mapped parking
    ///     lots are skipped with a reason.
    ///  2. Ways are split at every node they share with another kept way (and at their own repeated nodes) and chained
    ///     through two-way nodes whose two pieces carry the same class and tags; sharp corners (>= 45 degrees over +-6 m)
    ///     become L-bend nodes.
    ///  3. Centrelines resampled to <= 2 m (OSM vertices kept); heights from the DEM along strokes (a road carried
    ///     through its junctions, major roads first): box smoothing r 12 m x 4, grade limits (paved 11 %, dirt 15 %, the
    ///     real road's own grade as the floor), bridge decks as straight lines between the abutments, and every junction
    ///     at the height of its highest-ranked road, the others warped onto it over 12 m.
    ///  4. Split at the kerb zone (Highway -> MainStreet inside) and the downtown zone; clipped 50 m inside the play area
    ///     (EDGE_RULE), each cut listed.
    ///  5. Cross sections from width, lanes and parking tags, else class defaults; speeds from maxspeed, else class.
    ///  6. Mapped stop signs, give-way signs, signals and crossings as features with their direction of travel.
    /// </summary>
    public static class OsmRoadImporter
    {
        public const int Version = 2;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ data

        sealed class Seg
        {
            public int index;
            public OsmRoadSource.Way way;
            public RoadClass cls;
            public long[] nodes;
            public int from;
            public long First => nodes[0];
            public long Last => nodes[nodes.Length - 1];
        }

        /// <summary>A road piece with its points, as it moves through the steps (chains, then pieces of chains).</summary>
        sealed class Chain
        {
            public int id;
            public RoadClass cls;
            public OsmRoadSource.Way first;
            public bool firstReversed;
            public readonly List<string> ways = new List<string>();
            public List<D2> p = new List<D2>();
            public List<double> y = new List<double>();
            /// <summary>The smoothed DEM profile at each point (the real road's grade comes from it).</summary>
            public List<double> yr = new List<double>();
            /// <summary>OSM node id at each point (0 for inserted points).</summary>
            public List<long> nid = new List<long>();
            /// <summary>The way (index into segWays) the stretch from point i to i + 1 belongs to, and whether the chain runs against it.</summary>
            public List<int> seg = new List<int>();
            public readonly List<(OsmRoadSource.Way way, bool reversed)> segWays = new List<(OsmRoadSource.Way, bool)>();
            public bool cutStart, cutEnd, deck;
            public bool inKerb, inDowntown;
            /// <summary>The nearest building corner to the centreline on each side (away from the ends), +inf when none.</summary>
            public double nearL = double.PositiveInfinity, nearR = double.PositiveInfinity;
            /// <summary>A mapped building stands inside the road's one-lane floor: no width clears it (reported).</summary>
            public bool buildingConflict;
            public double[] S()
            {
                var s = new double[p.Count];
                for (int i = 1; i < p.Count; i++) s[i] = s[i - 1] + D2.Distance(p[i - 1], p[i]);
                return s;
            }
            public double Length => S()[p.Count - 1];
        }

        // ------------------------------------------------------------------ entry

        public static RoadGraphInput Import(OsmRoadSource src, OsmImportSettings st, out OsmImportReport report)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.dem == null) throw new ArgumentException("the source has no DEM");
            st = st ?? new OsmImportSettings();
            report = new OsmImportReport();

            var kept = new List<(OsmRoadSource.Way way, RoadClass cls)>();
            var siteWays = new List<OsmRoadSource.Way>();
            var ways = new List<OsmRoadSource.Way>(src.ways);
            ways.Sort((a, b) => a.id.CompareTo(b.id));
            foreach (var w in ways)
            {
                report.waysRead++;
                string why = Classify(w, src, out var cls, out bool siteWay);
                if (why != null)
                {
                    if (siteWay) siteWays.Add(w);
                    Skip(report, w, why);
                    continue;
                }
                kept.Add((w, cls));
            }
            report.waysUsed = kept.Count;

            src = MergeCloseJunctions(src, kept, siteWays, st, report);
            var segs = Split(kept, src, report);
            var chains = ChainUp(segs, src, report);
            chains = SplitCorners(chains, st, report);
            foreach (var c in chains) Resample(c, st.resample);
            Heights(chains, src, st, report);
            var pieces = new List<Chain>();
            foreach (var c in chains) pieces.AddRange(SplitZones(c, src, st));
            var clipped = new List<Chain>();
            foreach (var c in pieces) clipped.AddRange(Clip(c, src.playHalf - st.edgeInset, st, report));
            clipped = Groups(clipped, report);
            NearestBuildings(clipped, src, st);

            var input = new RoadGraphInput
            {
                source = src.source ?? "", sourceManifestSha256 = src.manifestSha256 ?? "",
                zonePolygon = src.kerbZone ?? Array.Empty<D2>(), downtownPolygon = src.downtownZone,
            };
            for (int i = 0; i < clipped.Count; i++) clipped[i].id = i;
            foreach (var c in clipped) input.roads.Add(ToRoad(c, st, report));
            report.roads = input.roads.Count;
            for (int i = 0; i < report.edgeCuts.Count; i++)
            {
                var e = report.edgeCuts[i];
                e.road = FindRoad(clipped, e.at);
                report.edgeCuts[i] = e;
            }
            SteepStretches(clipped, st, report);
            Features(clipped, src, input, report);
            Driveways(clipped, siteWays, src, report);
            foreach (var f in src.footprints) input.AddFootprint(f);

            // every kept way is in a road now, or says why not
            var inRoads = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in input.roads) foreach (var w in r.osmWays) inRoads.Add(w);
            var dropped = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in report.groups) if (!g.kept) foreach (var w in g.osmWays) dropped.Add(w);
            foreach (var (w, _) in kept)
            {
                if (inRoads.Contains(w.osm)) continue;
                Skip(report, w, dropped.Contains(w.osm) ? "service roads reached only through a site way or a path (sites)" : "outside the play area (EDGE_RULE: roads end 50 m inside the edge)");
                report.waysUsed--;
            }
            return input;
        }

        static void Skip(OsmImportReport r, OsmRoadSource.Way w, string why)
        {
            string hw = Tag(w.tags, "highway");
            r.skipped.Add($"{w.osm} highway={hw}{(Tag(w.tags, "service").Length > 0 ? " service=" + Tag(w.tags, "service") : "")}: {why}");
            r.skippedByReason.TryGetValue(why, out int n);
            r.skippedByReason[why] = n + 1;
        }

        static string Tag(Dictionary<string, string> t, string k) => t != null && t.TryGetValue(k, out var v) && v != null ? v : "";

        // ------------------------------------------------------------------ 1 classes

        static readonly HashSet<string> Unpaved = new HashSet<string>(StringComparer.Ordinal)
            { "unpaved", "gravel", "dirt", "ground", "compacted", "fine_gravel", "earth", "mud", "sand", "grass", "pebblestone", "rock", "woodchips" };

        /// <summary>Null when the way is a road for the graph (cls set), else why it is skipped. siteWay: the sites program takes it.</summary>
        static string Classify(OsmRoadSource.Way w, OsmRoadSource src, out RoadClass cls, out bool siteWay)
        {
            cls = RoadClass.Street;
            siteWay = false;
            string hw = Tag(w.tags, "highway"), service = Tag(w.tags, "service");
            if (w.nodes == null || w.nodes.Length < 2) return "fewer than two nodes";
            if (Tag(w.tags, "area") == "yes") return "an area, not a road line";
            switch (hw)
            {
                case "motorway": case "trunk": case "primary": case "secondary": case "tertiary":
                case "motorway_link": case "trunk_link": case "primary_link": case "secondary_link": case "tertiary_link":
                    cls = RoadClass.Highway; break;
                case "residential": case "unclassified": case "living_street": case "road":
                    cls = RoadClass.Street; break;
                case "track":
                    cls = RoadClass.Dirt; break;
                case "service":
                    if (service == "driveway" || service == "parking_aisle" || service == "drive-through" || service == "shared_driveway")
                    {
                        siteWay = true;
                        return "a site driveway or aisle (sites-S1 takes it as a mapped driveway)";
                    }
                    cls = RoadClass.Yard; break;
                case "footway": case "path": case "cycleway": case "steps": case "pedestrian": case "bridleway": case "corridor":
                case "platform": case "elevator": case "sidewalk": case "crossing":
                    return "not a road for vehicles";
                case "construction": case "proposed": case "abandoned": case "disused": case "razed":
                    return "not built or not in use";
                default:
                    return "unknown highway class";
            }
            if (Unpaved.Contains(Tag(w.tags, "surface"))) cls = RoadClass.Dirt;
            if (cls == RoadClass.Yard && src.parkingLots.Count > 0)
            {
                var mid = Mid(w, src);
                foreach (var lot in src.parkingLots)
                    if (TownZone.InPolygon(lot, mid))
                    {
                        siteWay = true;
                        return "a service way inside a mapped parking lot (sites)";
                    }
            }
            return null;
        }

        static D2 Mid(OsmRoadSource.Way w, OsmRoadSource src)
        {
            var pts = new List<D2>();
            foreach (var id in w.nodes) if (src.nodes.TryGetValue(id, out var n)) pts.Add(new D2(n.x, n.z));
            if (pts.Count == 0) return new D2(0, 0);
            double total = 0;
            for (int i = 1; i < pts.Count; i++) total += D2.Distance(pts[i - 1], pts[i]);
            double half = total / 2, acc = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                double l = D2.Distance(pts[i - 1], pts[i]);
                if (acc + l >= half && l > 0) return pts[i - 1] + (pts[i] - pts[i - 1]) * ((half - acc) / l);
                acc += l;
            }
            return pts[0];
        }

        // ------------------------------------------------------------------ 2 split, chain, corners

        /// <summary>
        /// roads-R4 step 6: two junctions (nodes where three or more road ends meet) joined directly by a way segment shorter
        /// than mergeJunctions become one junction at their mean position, so the pair gets one surface, one set of corners
        /// and one control (R1 cannot fit two surfaces in 4-5 m). Every way and site way is rewritten onto the merged node;
        /// a way left with one node is dropped (reported). Returns a new source; the given one is not changed.
        /// </summary>
        static OsmRoadSource MergeCloseJunctions(OsmRoadSource src, List<(OsmRoadSource.Way way, RoadClass cls)> kept, List<OsmRoadSource.Way> siteWays, OsmImportSettings st, OsmImportReport report)
        {
            var arms = new Dictionary<long, int>();
            foreach (var (w, _) in kept)
            {
                var l = Dedupe(w.nodes);
                for (int i = 0; i < l.Count; i++)
                {
                    arms.TryGetValue(l[i], out int a);
                    arms[l[i]] = a + (i == 0 || i == l.Count - 1 ? 1 : 2);
                }
            }
            var parent = new Dictionary<long, long>();
            long Find(long x) { while (parent.TryGetValue(x, out long p) && p != x) x = p; return x; }
            var pairs = new List<(long a, long b, double d, string way)>();
            foreach (var (w, _) in kept)
            {
                var l = Dedupe(w.nodes);
                for (int i = 0; i + 1 < l.Count; i++)
                {
                    long a = l[i], b = l[i + 1];
                    if (a == b || arms[a] < 3 || arms[b] < 3) continue;
                    var na = src.nodes[a];
                    var nb = src.nodes[b];
                    double d = Math.Sqrt((na.x - nb.x) * (na.x - nb.x) + (na.z - nb.z) * (na.z - nb.z));
                    if (d >= st.mergeJunctions) continue;
                    pairs.Add((Math.Min(a, b), Math.Max(a, b), d, w.osm));
                }
            }
            if (pairs.Count == 0) return src;
            pairs.Sort((x, y) => x.a != y.a ? x.a.CompareTo(y.a) : x.b.CompareTo(y.b));
            foreach (var (a, b, _, _) in pairs)
            {
                if (!parent.ContainsKey(a)) parent[a] = a;
                if (!parent.ContainsKey(b)) parent[b] = b;
                long ra = Find(a), rb = Find(b);
                if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
            // each group sits at its members' mean position under its smallest id
            var members = new SortedDictionary<long, List<long>>();
            foreach (var id in parent.Keys)
            {
                long r = Find(id);
                if (!members.TryGetValue(r, out var l)) members[r] = l = new List<long>();
                l.Add(id);
            }
            var alias = new Dictionary<long, long>();
            var merged = new OsmRoadSource
            {
                dem = src.dem, kerbZone = src.kerbZone, downtownZone = src.downtownZone, playHalf = src.playHalf, source = src.source, manifestSha256 = src.manifestSha256,
            };
            foreach (var kv in src.nodes) merged.nodes[kv.Key] = kv.Value;
            foreach (var kv in members)
            {
                kv.Value.Sort();
                double x = 0, z = 0;
                foreach (var id in kv.Value) { x += src.nodes[id].x; z += src.nodes[id].z; alias[id] = kv.Key; }
                var rep = src.nodes[kv.Key];
                merged.nodes[kv.Key] = new OsmRoadSource.Node { id = rep.id, x = x / kv.Value.Count, z = z / kv.Value.Count, tags = rep.tags };
                var pair = pairs.Find(p => Find(p.a) == kv.Key);
                report.mergedJunctions.Add(string.Format(Inv, "osm nodes {0} ({1:F1} m apart on {2}) became one junction at ({3:F1}, {4:F1})",
                    string.Join(" and ", kv.Value), pair.d, pair.way, x / kv.Value.Count, z / kv.Value.Count));
            }
            OsmRoadSource.Way Rewrite(OsmRoadSource.Way w)
            {
                bool hit = false;
                foreach (var id in w.nodes) if (alias.ContainsKey(id)) { hit = true; break; }
                if (!hit) return w;
                var l = new List<long>();
                foreach (var id in w.nodes)
                {
                    long r = alias.TryGetValue(id, out long a) ? a : id;
                    if (l.Count == 0 || l[l.Count - 1] != r) l.Add(r);
                }
                return new OsmRoadSource.Way { osm = w.osm, id = w.id, nodes = l.ToArray(), tags = w.tags };
            }
            var map = new Dictionary<OsmRoadSource.Way, OsmRoadSource.Way>();
            foreach (var w in src.ways) { var r = Rewrite(w); map[w] = r; merged.ways.Add(r); }
            for (int i = kept.Count - 1; i >= 0; i--)
            {
                var r = map[kept[i].way];
                if (r.nodes.Length < 2) { Skip(report, kept[i].way, "collapsed into a merged junction"); kept.RemoveAt(i); report.waysUsed--; continue; }
                kept[i] = (r, kept[i].cls);
            }
            for (int i = 0; i < siteWays.Count; i++) siteWays[i] = map[siteWays[i]];
            merged.footprints.AddRange(src.footprints);
            merged.parkingLots.AddRange(src.parkingLots);
            return merged;
        }

        static List<long> Dedupe(long[] nodes)
        {
            var l = new List<long>(nodes.Length);
            foreach (var id in nodes) if (l.Count == 0 || l[l.Count - 1] != id) l.Add(id);
            return l;
        }

        static List<Seg> Split(List<(OsmRoadSource.Way way, RoadClass cls)> kept, OsmRoadSource src, OsmImportReport report)
        {
            var count = new Dictionary<long, int>();
            var clean = new List<long[]>();
            foreach (var (w, _) in kept)
            {
                var l = new List<long>();
                foreach (var id in w.nodes)
                {
                    if (!src.nodes.ContainsKey(id)) throw new ArgumentException($"{w.osm}: node {id} is not in the source");
                    if (l.Count == 0 || l[l.Count - 1] != id) l.Add(id);
                }
                clean.Add(l.ToArray());
                foreach (var id in l) { count.TryGetValue(id, out int c); count[id] = c + 1; }
            }
            var segs = new List<Seg>();
            for (int k = 0; k < kept.Count; k++)
            {
                var n = clean[k];
                if (n.Length < 2) continue;
                int a = 0;
                for (int i = 1; i < n.Length; i++)
                {
                    if (i < n.Length - 1 && count[n[i]] < 2) continue;
                    var part = new long[i - a + 1];
                    Array.Copy(n, a, part, 0, part.Length);
                    segs.Add(new Seg { index = segs.Count, way = kept[k].way, cls = kept[k].cls, nodes = part, from = a });
                    a = i;
                }
            }
            report.segments = segs.Count;
            return segs;
        }

        /// <summary>The tags that must agree for two pieces to be one road, oriented (left/right and one-way follow the travel direction).</summary>
        static string Key(Seg s, bool reversed)
        {
            var t = s.way.tags;
            int ow = Oneway(t);
            if (reversed) ow = -ow;
            string L(string side) => Tag(t, "parking:" + side) + "|" + Tag(t, "parking:lane:" + side) + "|" + Tag(t, "sidewalk:" + side);
            string left = L(reversed ? "right" : "left"), right = L(reversed ? "left" : "right");
            string sw = Tag(t, "sidewalk");
            if (reversed) sw = sw == "left" ? "right" : sw == "right" ? "left" : sw;
            return string.Join("§", new[]
            {
                ((int)s.cls).ToString(Inv), Tag(t, "highway"), Tag(t, "service"), Tag(t, "bridge"), Tag(t, "tunnel"), Tag(t, "layer"),
                Tag(t, "junction"), Tag(t, "surface"), Tag(t, "width"), Tag(t, "lanes"), Tag(t, "maxspeed"), ow.ToString(Inv),
                Tag(t, "parking:both") + "|" + Tag(t, "parking:lane:both") + "|" + Tag(t, "sidewalk:both"), left, right, sw,
            });
        }

        static int Oneway(Dictionary<string, string> t)
        {
            string o = Tag(t, "oneway");
            if (o == "yes" || o == "true" || o == "1") return 1;
            if (o == "-1" || o == "reverse") return -1;
            if (Tag(t, "junction") == "roundabout" || Tag(t, "junction") == "circular") return o == "no" ? 0 : 1;
            return 0;
        }

        static List<Chain> ChainUp(List<Seg> segs, OsmRoadSource src, OsmImportReport report)
        {
            var at = new Dictionary<long, List<(Seg seg, bool atStart)>>();
            void Add(long v, Seg s, bool st)
            {
                if (!at.TryGetValue(v, out var l)) at[v] = l = new List<(Seg, bool)>();
                l.Add((s, st));
            }
            foreach (var s in segs) { Add(s.First, s, true); Add(s.Last, s, false); }

            // the other piece through a two-way node, oriented to continue the road, or null
            (Seg seg, bool reversed)? Next(Seg cur, bool curReversed, bool forward)
            {
                long v = forward ? (curReversed ? cur.First : cur.Last) : (curReversed ? cur.Last : cur.First);
                var l = at[v];
                if (l.Count != 2) return null;
                var other = l[0].seg == cur && l[0].atStart == (forward ? curReversed : !curReversed) ? l[1] : l[0];
                if (other.seg == cur) return null; // a closed way meeting itself
                bool rev = forward ? !other.atStart : other.atStart;
                string a = forward ? Key(cur, curReversed) : Key(other.seg, rev);
                string b = forward ? Key(other.seg, rev) : Key(cur, curReversed);
                if (a != b) return null;
                return (other.seg, rev);
            }

            var used = new bool[segs.Count];
            var chains = new List<Chain>();
            var order = new List<Seg>(segs);
            order.Sort((a, b) => a.way.id != b.way.id ? a.way.id.CompareTo(b.way.id) : a.from.CompareTo(b.from));
            foreach (var s0 in order)
            {
                if (used[s0.index]) continue;
                // walk back to the chain's first piece (stops on returning to s0: a loop starts at its smallest piece)
                Seg first = s0;
                bool firstRev = false;
                var seen = new HashSet<int> { s0.index };
                while (true)
                {
                    var p = Next(first, firstRev, false);
                    if (p == null || used[p.Value.seg.index] || !seen.Add(p.Value.seg.index)) break;
                    first = p.Value.seg;
                    firstRev = p.Value.reversed;
                }
                var c = new Chain { cls = first.cls, first = first.way, firstReversed = firstRev };
                Seg cur = first;
                bool rev = firstRev;
                while (true)
                {
                    used[cur.index] = true;
                    AppendSeg(c, cur, rev, src);
                    var n = Next(cur, rev, true);
                    if (n == null || used[n.Value.seg.index]) break;
                    cur = n.Value.seg;
                    rev = n.Value.reversed;
                }
                c.id = chains.Count;
                chains.Add(c);
            }
            report.chains = chains.Count;
            return chains;
        }

        static void AppendSeg(Chain c, Seg s, bool reversed, OsmRoadSource src)
        {
            int wi = c.segWays.Count;
            c.segWays.Add((s.way, reversed));
            if (c.ways.Count == 0 || c.ways[c.ways.Count - 1] != s.way.osm) c.ways.Add(s.way.osm);
            int n = s.nodes.Length;
            for (int k = 0; k < n; k++)
            {
                long id = s.nodes[reversed ? n - 1 - k : k];
                if (k == 0 && c.p.Count > 0) { c.seg[c.seg.Count - 1] = wi; continue; } // shared node with the previous piece
                var nd = src.nodes[id];
                c.p.Add(new D2(nd.x, nd.z));
                c.y.Add(0);
                c.yr.Add(0);
                c.nid.Add(id);
                c.seg.Add(wi);
            }
        }

        static List<Chain> SplitCorners(List<Chain> chains, OsmImportSettings st, OsmImportReport report)
        {
            var result = new List<Chain>();
            foreach (var c in chains)
            {
                var s = c.S();
                double L = s[s.Length - 1];
                var cand = new List<(int i, double turn)>();
                for (int i = 1; i < c.p.Count - 1; i++)
                {
                    if (s[i] < st.cornerClearance || L - s[i] < st.cornerClearance) continue;
                    var a = PointAt(c.p, s, s[i] - st.cornerWindow);
                    var b = PointAt(c.p, s, s[i] + st.cornerWindow);
                    D2 din = c.p[i] - a, dout = b - c.p[i];
                    if (din.Length < 1e-6 || dout.Length < 1e-6) continue;
                    double turn = Math.Abs(Geo.Wrap(dout.Angle - din.Angle));
                    if (turn >= st.cornerTurnDeg * Geo.Deg) cand.Add((i, turn));
                }
                // keep local maxima at least cornerClearance apart (the larger turn wins, then the earlier point)
                cand.Sort((x, y) => y.turn != x.turn ? y.turn.CompareTo(x.turn) : x.i.CompareTo(y.i));
                var cuts = new List<int>();
                foreach (var k in cand)
                {
                    bool near = false;
                    foreach (var q in cuts) if (Math.Abs(s[q] - s[k.i]) < st.cornerClearance) near = true;
                    if (!near) cuts.Add(k.i);
                }
                cuts.Sort();
                report.cornerBends += cuts.Count;
                int start = 0;
                foreach (var cut in cuts)
                {
                    result.Add(SliceIdx(c, start, cut));
                    start = cut;
                }
                result.Add(start == 0 ? c : SliceIdx(c, start, c.p.Count - 1));
            }
            for (int i = 0; i < result.Count; i++) result[i].id = i;
            return result;
        }

        static D2 PointAt(List<D2> p, double[] s, double sq)
        {
            if (sq <= 0) return p[0];
            if (sq >= s[s.Length - 1]) return p[p.Count - 1];
            int lo = 0, hi = s.Length - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (s[m] <= sq) lo = m; else hi = m; }
            double l = s[hi] - s[lo];
            return l > 0 ? p[lo] + (p[hi] - p[lo]) * ((sq - s[lo]) / l) : p[lo];
        }

        /// <summary>A copy of points a..b (inclusive) with the chain's attributes.</summary>
        static Chain SliceIdx(Chain c, int a, int b)
        {
            var r = Shell(c);
            for (int i = a; i <= b; i++)
            {
                r.p.Add(c.p[i]); r.y.Add(c.y[i]); r.yr.Add(c.yr[i]); r.nid.Add(c.nid[i]); r.seg.Add(c.seg[Math.Min(i, c.seg.Count - 1)]);
            }
            r.cutStart = a == 0 && c.cutStart;
            r.cutEnd = b == c.p.Count - 1 && c.cutEnd;
            return r;
        }

        static Chain Shell(Chain c)
        {
            var r = new Chain { cls = c.cls, first = c.first, firstReversed = c.firstReversed, deck = c.deck, inKerb = c.inKerb, inDowntown = c.inDowntown };
            r.segWays.AddRange(c.segWays);
            r.ways.AddRange(c.ways);
            return r;
        }

        /// <summary>A copy of the stretch s0..s1 of the chain; the ends are interpolated when they fall between points.</summary>
        static Chain SliceS(Chain c, double[] s, double s0, double s1)
        {
            var r = Shell(c);
            void AddAt(double sq)
            {
                int lo = 0, hi = s.Length - 1;
                while (hi - lo > 1) { int m = (lo + hi) >> 1; if (s[m] <= sq) lo = m; else hi = m; }
                if (Math.Abs(s[lo] - sq) < 1e-9) { r.p.Add(c.p[lo]); r.y.Add(c.y[lo]); r.yr.Add(c.yr[lo]); r.nid.Add(c.nid[lo]); r.seg.Add(c.seg[lo]); return; }
                if (Math.Abs(s[hi] - sq) < 1e-9) { r.p.Add(c.p[hi]); r.y.Add(c.y[hi]); r.yr.Add(c.yr[hi]); r.nid.Add(c.nid[hi]); r.seg.Add(c.seg[Math.Min(hi, c.seg.Count - 1)]); return; }
                double t = (sq - s[lo]) / (s[hi] - s[lo]);
                r.p.Add(c.p[lo] + (c.p[hi] - c.p[lo]) * t);
                r.y.Add(c.y[lo] + (c.y[hi] - c.y[lo]) * t);
                r.yr.Add(c.yr[lo] + (c.yr[hi] - c.yr[lo]) * t);
                r.nid.Add(0);
                r.seg.Add(c.seg[lo]);
            }
            AddAt(s0);
            for (int i = 0; i < s.Length; i++)
                if (s[i] > s0 + 1e-9 && s[i] < s1 - 1e-9) { r.p.Add(c.p[i]); r.y.Add(c.y[i]); r.yr.Add(c.yr[i]); r.nid.Add(c.nid[i]); r.seg.Add(c.seg[Math.Min(i, c.seg.Count - 1)]); }
            AddAt(s1);
            r.cutStart = s0 <= 1e-9 && c.cutStart;
            r.cutEnd = s1 >= s[s.Length - 1] - 1e-9 && c.cutEnd;
            return r;
        }

        // ------------------------------------------------------------------ 3 resample and heights

        static void Resample(Chain c, double step)
        {
            var p = new List<D2>(); var y = new List<double>(); var yr = new List<double>(); var nid = new List<long>(); var seg = new List<int>();
            for (int i = 0; i < c.p.Count; i++)
            {
                if (i > 0)
                {
                    double l = D2.Distance(c.p[i - 1], c.p[i]);
                    int k = (int)Math.Ceiling(l / step - 1e-9);
                    for (int j = 1; j < k; j++)
                    {
                        p.Add(c.p[i - 1] + (c.p[i] - c.p[i - 1]) * ((double)j / k)); y.Add(0); yr.Add(0); nid.Add(0); seg.Add(c.seg[i - 1]);
                    }
                }
                p.Add(c.p[i]); y.Add(0); yr.Add(0); nid.Add(c.nid[i]); seg.Add(c.seg[Math.Min(i, c.seg.Count - 1)]);
            }
            c.p = p; c.y = y; c.yr = yr; c.nid = nid; c.seg = seg;
            c.deck = Tag(c.first.tags, "bridge").Length > 0 && Tag(c.first.tags, "bridge") != "no";
        }

        sealed class StrokePart
        {
            public Chain chain;
            public bool reversed;
        }

        static void Heights(List<Chain> chains, OsmRoadSource src, OsmImportSettings st, OsmImportReport report)
        {
            // chain ends at each node
            var at = new SortedDictionary<long, List<(Chain c, bool atStart)>>();
            void Add(long v, Chain c, bool s)
            {
                if (!at.TryGetValue(v, out var l)) at[v] = l = new List<(Chain, bool)>();
                l.Add((c, s));
            }
            foreach (var c in chains) { Add(c.nid[0], c, true); Add(c.nid[c.nid.Count - 1], c, false); }

            // pair the ends that continue through each node: class first, then straightness (05 §5.1 step 7)
            var link = new Dictionary<(int chain, bool atStart), (Chain c, bool atStart)>();
            foreach (var kv in at)
            {
                var l = kv.Value;
                var dirs = new D2[l.Count];
                for (int i = 0; i < l.Count; i++) dirs[i] = LeaveDir(l[i].c, l[i].atStart, 10);
                var pairs = new List<(double score, int i, int j, double bend)>();
                for (int i = 0; i < l.Count; i++)
                    for (int j = i + 1; j < l.Count; j++)
                    {
                        if (l[i].c == l[j].c) continue;
                        int ri = RoadClasses.Rank(l[i].c.cls), rj = RoadClasses.Rank(l[j].c.cls);
                        double bend = Geo.Bend(dirs[i].Angle, dirs[j].Angle);
                        if (l.Count > 2 && bend > 0.6) continue;
                        pairs.Add((10 * Math.Min(ri, rj) + (ri == rj ? 2 : 0) - bend, i, j, bend));
                    }
                pairs.Sort((a, b) => b.score != a.score ? b.score.CompareTo(a.score) : a.i != b.i ? a.i.CompareTo(b.i) : a.j.CompareTo(b.j));
                var taken = new bool[l.Count];
                foreach (var pr in pairs)
                {
                    if (taken[pr.i] || taken[pr.j]) continue;
                    taken[pr.i] = taken[pr.j] = true;
                    link[(l[pr.i].c.id, l[pr.i].atStart)] = l[pr.j];
                    link[(l[pr.j].c.id, l[pr.j].atStart)] = l[pr.i];
                }
            }

            // strokes: chains joined through their linked ends
            var inStroke = new bool[chains.Count];
            var strokes = new List<List<StrokePart>>();
            foreach (var c0 in chains)
            {
                if (inStroke[c0.id]) continue;
                Chain first = c0;
                bool firstRev = false;
                var seen = new HashSet<int> { c0.id };
                while (true)
                {
                    // the end we enter through: the start of an unreversed chain, the end of a reversed one
                    if (!link.TryGetValue((first.id, !firstRev), out var prev) || inStroke[prev.c.id] || !seen.Add(prev.c.id)) break;
                    first = prev.c;
                    firstRev = prev.atStart; // the previous chain must end at the shared node: reversed when its start is there
                }
                var stroke = new List<StrokePart>();
                Chain cur = first;
                bool rev = firstRev;
                while (true)
                {
                    inStroke[cur.id] = true;
                    stroke.Add(new StrokePart { chain = cur, reversed = rev });
                    if (!link.TryGetValue((cur.id, rev), out var next) || inStroke[next.c.id]) break;
                    cur = next.c;
                    rev = !next.atStart;
                }
                strokes.Add(stroke);
            }
            report.strokes = strokes.Count;

            var lengths = new double[strokes.Count];
            var ranks = new int[strokes.Count];
            var firstId = new int[strokes.Count];
            for (int k = 0; k < strokes.Count; k++)
            {
                firstId[k] = int.MaxValue;
                foreach (var part in strokes[k])
                {
                    lengths[k] += part.chain.Length;
                    ranks[k] = Math.Max(ranks[k], RoadClasses.Rank(part.chain.cls));
                    firstId[k] = Math.Min(firstId[k], part.chain.id);
                }
            }
            var orderIdx = new List<int>();
            for (int k = 0; k < strokes.Count; k++) orderIdx.Add(k);
            orderIdx.Sort((a, b) => ranks[a] != ranks[b] ? ranks[b].CompareTo(ranks[a]) : lengths[a] != lengths[b] ? lengths[b].CompareTo(lengths[a]) : firstId[a].CompareTo(firstId[b]));

            var fixedY = new Dictionary<long, double>();
            var demDev = new List<double>();
            foreach (int k in orderIdx) Stroke(k, strokes[k], src, st, fixedY, report, demDev);
            demDev.Sort();
            if (demDev.Count > 0)
            {
                double sum = 0;
                foreach (var d in demDev) sum += d;
                report.demMeanAbs = sum / demDev.Count;
                report.demP95Abs = demDev[Math.Min(demDev.Count - 1, (int)(0.95 * demDev.Count))];
                report.demMaxAbs = demDev[demDev.Count - 1];
            }
        }

        static D2 LeaveDir(Chain c, bool atStart, double dist)
        {
            var s = c.S();
            double L = s[s.Length - 1], d = Math.Min(dist, L);
            var p0 = atStart ? c.p[0] : c.p[c.p.Count - 1];
            var q = PointAt(c.p, s, atStart ? d : L - d);
            var v = q - p0;
            return v.Length > 1e-9 ? v.Normalized : new D2(1, 0);
        }

        static void Stroke(int strokeId, List<StrokePart> parts, OsmRoadSource src, OsmImportSettings st, Dictionary<long, double> fixedY, OsmImportReport report, List<double> demDev)
        {
            // concatenate (a shared node appears once)
            var p = new List<D2>();
            var nid = new List<long>();
            var owner = new List<(Chain c, int i)>();
            var deck = new List<bool>();
            var dirt = new List<bool>();
            foreach (var part in parts)
            {
                var c = part.chain;
                int n = c.p.Count;
                for (int k = 0; k < n; k++)
                {
                    int i = part.reversed ? n - 1 - k : k;
                    if (k == 0 && p.Count > 0)
                    {
                        // the joint: deck only when both sides are deck
                        deck[deck.Count - 1] = deck[deck.Count - 1] && c.deck;
                        continue;
                    }
                    p.Add(c.p[i]); nid.Add(c.nid[i]); owner.Add((c, i)); deck.Add(c.deck); dirt.Add(c.cls == RoadClass.Dirt);
                }
            }
            int N = p.Count;
            var s = new double[N];
            for (int i = 1; i < N; i++) s[i] = s[i - 1] + D2.Distance(p[i - 1], p[i]);
            var raw = new double[N];
            for (int i = 0; i < N; i++) raw[i] = src.dem.Sample(p[i].x, p[i].z);

            // bridge decks: straight between the abutments (the first and last sample of each deck run, inclusive)
            var decks = DeckRuns(deck, parts, N);
            foreach (var (a, b) in decks) Line(raw, s, a, b);

            // smoothing (05 / makeRoad: symmetric box that shrinks at the ends, so the ends stay put), decks kept straight
            var y = (double[])raw.Clone();
            for (int pass = 0; pass < st.smoothPasses; pass++)
            {
                y = BoxSmooth(y, s, st.smoothRadius);
                foreach (var (a, b) in decks) Line(y, s, a, b);
            }
            var smoothRef = (double[])y.Clone();

            // grade limit with the real road's own grade (the smoothed DEM profile's) as the floor: never flatter than the
            // real road, so on lidar it only bites where a junction warp below would make the road steeper than it is
            var limit = new double[N];
            var realGrade = new double[N];
            for (int i = 1; i < N; i++)
            {
                double ds = s[i] - s[i - 1];
                realGrade[i] = ds > 1e-9 ? Math.Abs(smoothRef[i] - smoothRef[i - 1]) / ds : 0;
                bool onDeck = deck[i] && deck[i - 1];
                limit[i] = onDeck ? double.PositiveInfinity : Math.Max(dirt[i] ? st.maxGradeDirt : st.maxGradePaved, realGrade[i] + st.gradeSlack);
            }
            var pinned = new bool[N];
            pinned[0] = pinned[N - 1] = true;
            foreach (var (a, b) in decks) for (int i = a; i <= b; i++) pinned[i] = true;
            GradeLimit(y, s, limit, pinned);

            // junction heights: nodes already fixed by a higher-ranked stroke pull this one onto them (warp over 12 m)
            var pins = new List<(int i, double delta)>();
            for (int i = 0; i < N; i++)
                if (nid[i] != 0 && fixedY.TryGetValue(nid[i], out var fy) && IsNodeSample(i, owner)) pins.Add((i, fy - y[i]));
            var corr = Correction(s, pins, st.warpDistance);
            for (int i = 0; i < N; i++) y[i] += corr[i];
            foreach (var (i, _) in pins) pinned[i] = true;
            foreach (var (a, b) in decks) Line(y, s, a, b);
            var before = (double[])y.Clone();
            GradeLimit(y, s, limit, pinned);

            for (int i = 0; i < N; i++)
            {
                bool warped = Math.Abs(corr[i]) > 1e-9;
                if (warped) report.warpedSamples++;
                if (Math.Abs(y[i] - before[i]) > 1e-3) report.clampedSamples++;
                if (!warped && !deck[i]) report.maxDevFromSmoothed = Math.Max(report.maxDevFromSmoothed, Math.Abs(y[i] - smoothRef[i]));
                if (!deck[i]) demDev.Add(Math.Abs(y[i] - raw[i]));
            }
            report.samples += N;

            // write back (heights and the smoothed reference, decks counting as their straight line), fix this stroke's nodes
            var refAt = new Dictionary<long, double>();
            for (int i = 0; i < N; i++)
            {
                var (c, ci) = owner[i];
                c.y[ci] = y[i];
                c.yr[ci] = deck[i] ? y[i] : smoothRef[i];
                if (nid[i] != 0 && IsNodeSample(i, owner))
                {
                    refAt[nid[i]] = c.yr[ci];
                    if (!fixedY.ContainsKey(nid[i])) fixedY[nid[i]] = y[i];
                }
            }
            // the joints appear once in the stroke: copy their height into the next chain's first point too
            foreach (var part in parts)
            {
                var c = part.chain;
                for (int e = 0; e < 2; e++)
                {
                    int i = e == 0 ? 0 : c.p.Count - 1;
                    if (fixedY.TryGetValue(c.nid[i], out var fy)) c.y[i] = fy;
                    if (refAt.TryGetValue(c.nid[i], out var fr)) c.yr[i] = fr;
                }
            }
        }

        /// <summary>
        /// Steep stretches on the final roads: segments over the paved (11 %) or dirt (15 %) limit, merged per road. Each is
        /// allowed only where the real road (the smoothed DEM profile) is that steep, within gradeSlack; any other is a
        /// conflict (two junction heights too close for the limit), counted in the report.
        /// </summary>
        static void SteepStretches(List<Chain> pieces, OsmImportSettings st, OsmImportReport report)
        {
            foreach (var c in pieces)
            {
                if (c.deck) continue;
                var s = c.S();
                double lim = c.cls == RoadClass.Dirt ? st.maxGradeDirt : st.maxGradePaved;
                for (int i = 1; i < c.p.Count; i++)
                {
                    double ds = s[i] - s[i - 1];
                    if (ds < 1e-6) continue;
                    double g = Math.Abs(c.y[i] - c.y[i - 1]) / ds;
                    if (g <= lim + 1e-6) continue;
                    double real = Math.Abs(c.yr[i] - c.yr[i - 1]) / ds;
                    if (g > real + st.gradeSlack + 1e-6) report.gradeConflicts++;
                    int last = report.steep.Count - 1;
                    if (last >= 0 && report.steep[last].road == c.id && s[i - 1] - report.steep[last].s1 < 2.5)
                    {
                        var r = report.steep[last];
                        r.s1 = s[i];
                        r.length = r.s1 - r.s0;
                        if (g > r.maxGrade) { r.maxGrade = g; r.at = c.p[i]; }
                        r.realGrade = Math.Max(r.realGrade, real);
                        report.steep[last] = r;
                    }
                    else report.steep.Add(new SteepStretch { road = c.id, s0 = s[i - 1], s1 = s[i], length = ds, at = c.p[i], maxGrade = g, realGrade = real });
                }
            }
        }

        /// <summary>True for samples that are a chain's first or last point (graph nodes); interior OSM nodes are just shape.</summary>
        static bool IsNodeSample(int i, List<(Chain c, int i)> owner)
        {
            var (c, ci) = owner[i];
            return ci == 0 || ci == c.p.Count - 1;
        }

        static List<(int a, int b)> DeckRuns(List<bool> deck, List<StrokePart> parts, int n)
        {
            var runs = new List<(int, int)>();
            for (int i = 0; i < n;)
            {
                if (!deck[i]) { i++; continue; }
                int a = i;
                while (i < n && deck[i]) i++;
                int b = i - 1;
                // include the abutment samples on either side
                runs.Add((Math.Max(0, a - 1), Math.Min(n - 1, b + 1)));
            }
            return runs;
        }

        static void Line(double[] y, double[] s, int a, int b)
        {
            if (b <= a) return;
            double L = s[b] - s[a];
            for (int i = a + 1; i < b; i++) y[i] = y[a] + (y[b] - y[a]) * ((s[i] - s[a]) / L);
        }

        /// <summary>One box pass: the mean of the piecewise-linear profile over s +- r, r shrinking at the ends (exact integral, spacing-independent).</summary>
        static double[] BoxSmooth(double[] y, double[] s, double R)
        {
            int n = y.Length;
            var F = new double[n];
            for (int i = 1; i < n; i++) F[i] = F[i - 1] + 0.5 * (y[i] + y[i - 1]) * (s[i] - s[i - 1]);
            double Integral(double sq)
            {
                if (sq <= s[0]) return 0;
                if (sq >= s[n - 1]) return F[n - 1];
                int lo = 0, hi = n - 1;
                while (hi - lo > 1) { int m = (lo + hi) >> 1; if (s[m] <= sq) lo = m; else hi = m; }
                double l = s[hi] - s[lo], t = l > 0 ? (sq - s[lo]) / l : 0;
                double yq = y[lo] + (y[hi] - y[lo]) * t;
                return F[lo] + 0.5 * (y[lo] + yq) * (sq - s[lo]);
            }
            var o = new double[n];
            for (int i = 0; i < n; i++)
            {
                double r = Math.Min(R, Math.Min(s[i] - s[0], s[n - 1] - s[i]));
                o[i] = r < 1e-6 ? y[i] : (Integral(s[i] + r) - Integral(s[i] - r)) / (2 * r);
            }
            return o;
        }

        /// <summary>makeRoad's grade limit: forward and backward clamps, six passes; pinned samples never move.</summary>
        static void GradeLimit(double[] y, double[] s, double[] limit, bool[] pinned)
        {
            int n = y.Length;
            for (int pass = 0; pass < 6; pass++)
            {
                for (int i = 1; i < n; i++)
                {
                    if (pinned[i]) continue;
                    double g = limit[i] * (s[i] - s[i - 1]);
                    y[i] = Math.Min(Math.Max(y[i], y[i - 1] - g), y[i - 1] + g);
                }
                for (int i = n - 2; i >= 0; i--)
                {
                    if (pinned[i]) continue;
                    double g = limit[i + 1] * (s[i + 1] - s[i]);
                    y[i] = Math.Min(Math.Max(y[i], y[i + 1] - g), y[i + 1] + g);
                }
            }
        }

        /// <summary>A correction exact at the pins: linear between pins up to 2W apart, else a tent of half-width W around each.</summary>
        static double[] Correction(double[] s, List<(int i, double delta)> pins, double W)
        {
            int n = s.Length;
            var c = new double[n];
            if (pins.Count == 0) return c;
            pins.Sort((a, b) => a.i.CompareTo(b.i));
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                while (k < pins.Count && pins[k].i < i) k++;
                bool hasR = k < pins.Count, hasL = k > 0 || (hasR && pins[k].i == i);
                if (hasR && pins[k].i == i) { c[i] = pins[k].delta; continue; }
                var L = k > 0 ? pins[k - 1] : (-1, 0.0);
                var R = hasR ? pins[k] : (-1, 0.0);
                if (k > 0 && hasR && s[R.Item1] - s[L.Item1] <= 2 * W)
                {
                    double t = (s[i] - s[L.Item1]) / (s[R.Item1] - s[L.Item1]);
                    c[i] = L.Item2 + (R.Item2 - L.Item2) * t;
                    continue;
                }
                double v = 0;
                if (k > 0) v += L.Item2 * Math.Max(0, 1 - (s[i] - s[L.Item1]) / W);
                if (hasR) v += R.Item2 * Math.Max(0, 1 - (s[R.Item1] - s[i]) / W);
                c[i] = v;
            }
            return c;
        }

        // ------------------------------------------------------------------ 4 zones and the play-area edge

        static List<Chain> SplitZones(Chain c, OsmRoadSource src, OsmImportSettings st)
        {
            var polys = new List<D2[]>();
            if (c.cls == RoadClass.Highway || c.cls == RoadClass.Street)
            {
                if (src.kerbZone != null) polys.Add(src.kerbZone);
                if (c.cls == RoadClass.Highway && src.downtownZone != null) polys.Add(src.downtownZone);
            }
            var s = c.S();
            double L = s[s.Length - 1];
            var cuts = new List<double>();
            foreach (var poly in polys)
                for (int i = 1; i < c.p.Count; i++)
                    for (int j = 0; j < poly.Length; j++)
                        if (SegmentHit(c.p[i - 1], c.p[i], poly[j], poly[(j + 1) % poly.Length], out double t))
                            cuts.Add(s[i - 1] + t * (s[i] - s[i - 1]));
            var pieces = new List<Chain>();
            foreach (var (a, b) in Intervals(cuts, L, st.minPiece))
                pieces.Add(a <= 1e-9 && b >= L - 1e-9 ? c : SliceS(c, s, a, b));
            foreach (var pc in pieces)
            {
                var m = MidPoint(pc);
                pc.inKerb = src.kerbZone != null && TownZone.InPolygon(src.kerbZone, m);
                pc.inDowntown = src.downtownZone != null && TownZone.InPolygon(src.downtownZone, m);
                if (pc.cls == RoadClass.Highway && pc.inKerb) pc.cls = RoadClass.MainStreet;
            }
            return pieces;
        }

        /// <summary>The stretches between sorted cut points, dropping cuts that would leave a piece shorter than minPiece.</summary>
        static List<(double a, double b)> Intervals(List<double> cuts, double L, double minPiece)
        {
            cuts.Sort();
            var keep = new List<double> { 0 };
            foreach (var x in cuts)
                if (x - keep[keep.Count - 1] >= minPiece && L - x >= minPiece) keep.Add(x);
            keep.Add(L);
            var r = new List<(double, double)>();
            for (int i = 0; i + 1 < keep.Count; i++) r.Add((keep[i], keep[i + 1]));
            return r;
        }

        static D2 MidPoint(Chain c)
        {
            var s = c.S();
            return PointAt(c.p, s, 0.5 * s[s.Length - 1]);
        }

        static bool SegmentHit(D2 a, D2 b, D2 c, D2 d, out double t)
        {
            t = 0;
            D2 r = b - a, q = d - c;
            double den = r.Cross(q);
            if (Math.Abs(den) < 1e-12) return false;
            D2 w = c - a;
            t = w.Cross(q) / den;
            double u = w.Cross(r) / den;
            // [0, 1): a boundary through a point counts once, on the segment that starts there
            return t >= 0 && t < 1 && u >= 0 && u <= 1;
        }

        static List<Chain> Clip(Chain c, double H, OsmImportSettings st, OsmImportReport report)
        {
            bool In(D2 p) => Math.Abs(p.x) <= H && Math.Abs(p.z) <= H;
            var s = c.S();
            double L = s[s.Length - 1];
            var cuts = new List<double>();
            var square = new[] { new D2(-H, -H), new D2(H, -H), new D2(H, H), new D2(-H, H) };
            for (int i = 1; i < c.p.Count; i++)
                for (int j = 0; j < 4; j++)
                    if (SegmentHit(c.p[i - 1], c.p[i], square[j], square[(j + 1) % 4], out double t))
                        cuts.Add(s[i - 1] + t * (s[i] - s[i - 1]));
            var result = new List<Chain>();
            if (cuts.Count == 0)
            {
                if (In(MidPoint(c))) result.Add(c);
                return result;
            }
            cuts.Sort();
            var bounds = new List<double> { 0 };
            bounds.AddRange(cuts);
            bounds.Add(L);
            for (int k = 0; k + 1 < bounds.Count; k++)
            {
                double a = bounds[k], b = bounds[k + 1];
                if (b - a < st.minPiece) continue;
                if (!In(PointAt(c.p, s, 0.5 * (a + b)))) continue;
                var pc = SliceS(c, s, a, b);
                if (k > 0) { pc.cutStart = true; AddCut(report, c, s, a, pc.p[0], H, false); }
                if (k + 2 < bounds.Count) { pc.cutEnd = true; AddCut(report, c, s, b, pc.p[pc.p.Count - 1], H, true); }
                result.Add(pc);
            }
            return result;
        }

        static void AddCut(OsmImportReport report, Chain c, double[] s, double at, D2 p, double H, bool leavingForward)
        {
            char side = Math.Abs(p.x - H) < 1e-6 ? 'E' : Math.Abs(p.x + H) < 1e-6 ? 'W' : Math.Abs(p.z - H) < 1e-6 ? 'N' : 'S';
            int i = 0;
            while (i < c.seg.Count - 1 && s[i + 1] < at) i++;
            // distance from the cut to the chain's own end beyond it (a junction or a dead end in the skirt)
            double beyond = leavingForward ? s[s.Length - 1] - at : at;
            report.edgeCuts.Add(new EdgeCut { road = -1, osmWay = c.segWays[c.seg[i]].way.osm, at = p, side = side, junctionBeyond = beyond });
        }

        /// <summary>
        /// Connectivity inside the play area: pieces joined at shared ends. The group with the most road is the network;
        /// another group stays when it reaches the play-area edge (it connects outside), and is dropped when it is only
        /// service and dirt roads (reached through a site driveway, aisle or path: the sites program's); anything else
        /// stays and is reported as isolated in the map data.
        /// </summary>
        static List<Chain> Groups(List<Chain> pieces, OsmImportReport report)
        {
            int n = pieces.Count;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            var owner = new Dictionary<string, int>(StringComparer.Ordinal);
            string Key(Chain c, int i) => c.nid[i] != 0 ? "n" + c.nid[i].ToString(Inv) : "p" + Math.Round(c.p[i].x * 1000).ToString(Inv) + "," + Math.Round(c.p[i].z * 1000).ToString(Inv);
            for (int k = 0; k < n; k++)
                foreach (int i in new[] { 0, pieces[k].p.Count - 1 })
                {
                    string key = Key(pieces[k], i);
                    if (owner.TryGetValue(key, out int other)) { int a = Find(k), b = Find(other); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
                    else owner[key] = k;
                }
            var groups = new SortedDictionary<int, List<Chain>>();
            for (int k = 0; k < n; k++)
            {
                int r = Find(k);
                if (!groups.TryGetValue(r, out var l)) groups[r] = l = new List<Chain>();
                l.Add(pieces[k]);
            }
            int main = -1;
            double best = -1;
            foreach (var kv in groups)
            {
                double len = 0;
                foreach (var c in kv.Value) len += c.Length;
                if (len > best + 1e-9) { best = len; main = kv.Key; }
            }
            var kept = new List<Chain>();
            foreach (var kv in groups)
            {
                if (kv.Key == main) { kept.AddRange(kv.Value); continue; }
                bool edge = false, service = true;
                double len = 0;
                var ways = new List<string>();
                foreach (var c in kv.Value)
                {
                    edge |= c.cutStart || c.cutEnd;
                    service &= c.cls == RoadClass.Yard || c.cls == RoadClass.Dirt;
                    len += c.Length;
                    foreach (var sw in c.segWays) if (!ways.Contains(sw.way.osm)) ways.Add(sw.way.osm);
                }
                bool keep = edge || !service;
                var g = new RoadGroup
                {
                    kept = keep, osmWays = ways.ToArray(), length = len, at = kv.Value[0].p[0],
                    reason = edge ? "connects outside the play area" : service ? "service roads reached only through a site way or a path (sites)" : "isolated in the map data",
                };
                report.groups.Add(g);
                if (keep) kept.AddRange(kv.Value);
            }
            return kept;
        }

        static int FindRoad(List<Chain> pieces, D2 at)
        {
            foreach (var c in pieces)
                if (D2.Distance(c.p[0], at) < 1e-6 || D2.Distance(c.p[c.p.Count - 1], at) < 1e-6) return c.id;
            return -1;
        }

        static int NearestRoad(List<Chain> pieces, D2 at)
        {
            int best = -1;
            double bd = double.MaxValue;
            foreach (var c in pieces)
                for (int i = 1; i < c.p.Count; i++)
                {
                    double d = Polyline.SegmentDistance(c.p[i - 1], c.p[i], at);
                    if (d < bd) { bd = d; best = c.id; }
                }
            return bd < 0.5 ? best : -1;
        }

        // ------------------------------------------------------------------ 5 roads and cross sections

        /// <summary>For each piece, the nearest building corner on each side of the centreline, ignoring the ends (the cross streets' buildings).</summary>
        static void NearestBuildings(List<Chain> pieces, OsmRoadSource src, OsmImportSettings st)
        {
            if (src.footprints.Count == 0) return;
            foreach (var c in pieces)
            {
                var s = c.S();
                double L = s[s.Length - 1];
                double lo = Math.Min(st.fitEnds, 0.25 * L), hi = L - lo;
                double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
                foreach (var p in c.p) { minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minZ = Math.Min(minZ, p.z); maxZ = Math.Max(maxZ, p.z); }
                foreach (var b in src.footprints)
                {
                    bool near = false;
                    foreach (var v in b) if (v.x > minX - 15 && v.x < maxX + 15 && v.z > minZ - 15 && v.z < maxZ + 15) { near = true; break; }
                    if (!near) continue;
                    foreach (var v in b)
                    {
                        // the foot of the perpendicular on the centreline, and the side
                        double best = double.MaxValue, sv = 0, side = 0;
                        for (int i = 0; i + 1 < c.p.Count; i++)
                        {
                            D2 a = c.p[i], e = c.p[i + 1] - a;
                            double l2 = e.Dot(e);
                            double t = l2 > 0 ? Math.Max(0, Math.Min(1, (v - a).Dot(e) / l2)) : 0;
                            double d = D2.Distance(a + e * t, v);
                            if (d < best) { best = d; sv = s[i] + t * (s[i + 1] - s[i]); side = e.Cross(v - a); }
                        }
                        // beyond either end (the foot clamps to an end point): the cross street's building, not this road's
                        if (sv <= lo + 1e-9 || sv >= hi - 1e-9) continue;
                        if (side >= 0) c.nearL = Math.Min(c.nearL, best); else c.nearR = Math.Min(c.nearR, best);
                    }
                }
            }
        }

        static RoadGraphInput.Road ToRoad(Chain c, OsmImportSettings st, OsmImportReport report)
        {
            var t = c.first.tags;
            var pts = new D3[c.p.Count];
            for (int i = 0; i < pts.Length; i++) pts[i] = new D3(c.p[i].x, c.y[i], c.p[i].z);
            var xs = CrossSectionOf(c, st);
            FitToBuildings(c, xs, st, report);
            int ow = Oneway(t);
            if (c.firstReversed) ow = -ow;
            // the ways this piece really covers (a piece of a chain may cover only some of the chain's ways)
            var ways = new List<string>();
            for (int i = 0; i + 1 < c.p.Count; i++)
            {
                string w = c.segWays[c.seg[i]].way.osm;
                if (ways.Count == 0 || ways[ways.Count - 1] != w) ways.Add(w);
            }
            var r = new RoadGraphInput.Road
            {
                id = c.id, cls = c.cls, kind = c.cls.ToString().ToLowerInvariant(), paved = RoadClasses.IsPaved(c.cls), startJ = false, endJ = false,
                hw = xs.HalfWidth, pts = pts, d = Polyline.ArcLength(pts), osmWays = ways.ToArray(), highway = Tag(t, "highway"), service = Tag(t, "service"),
                xs = xs, oneway = ow, bridge = c.deck, tunnel = Tag(t, "tunnel").Length > 0 && Tag(t, "tunnel") != "no",
                roundabout = Tag(t, "junction") == "roundabout" || Tag(t, "junction") == "circular", surface = Tag(t, "surface"),
                sidewalk = SidewalkTag(t, c.firstReversed), parking = ParkingTag(t, c.firstReversed), speedKmh = Speed(t, c.cls, st),
                inKerbZone = c.inKerb, inDowntown = c.inDowntown, cutStart = c.cutStart, cutEnd = c.cutEnd,
                osmStart = c.nid[0], osmEnd = c.nid[c.nid.Count - 1], buildingConflict = c.buildingConflict,
            };
            int.TryParse(Tag(t, "layer"), NumberStyles.Integer, Inv, out r.layer);
            return r;
        }

        static string SidewalkTag(Dictionary<string, string> t, bool reversed)
        {
            var parts = new List<string>();
            string sw = Tag(t, "sidewalk");
            if (sw.Length > 0)
            {
                if (reversed) sw = sw == "left" ? "right" : sw == "right" ? "left" : sw;
                parts.Add("sidewalk=" + sw);
            }
            foreach (var side in new[] { "both", "left", "right" })
            {
                string v = Tag(t, "sidewalk:" + side);
                if (v.Length == 0) continue;
                string s2 = reversed && side != "both" ? (side == "left" ? "right" : "left") : side;
                parts.Add("sidewalk:" + s2 + "=" + v);
            }
            return string.Join(";", parts);
        }

        static string ParkingTag(Dictionary<string, string> t, bool reversed)
        {
            var parts = new List<string>();
            foreach (var key in new[] { "parking:both", "parking:left", "parking:right", "parking:lane:both", "parking:lane:left", "parking:lane:right" })
            {
                string v = Tag(t, key);
                if (v.Length == 0) continue;
                string k2 = key;
                if (reversed) k2 = key.EndsWith(":left", StringComparison.Ordinal) ? key.Replace(":left", ":right") : key.EndsWith(":right", StringComparison.Ordinal) ? key.Replace(":right", ":left") : key;
                parts.Add(k2 + "=" + v);
            }
            return string.Join(";", parts);
        }

        /// <summary>Parking lane on one side (left or right of the road's direction): tagged lane, parallel, diagonal, perpendicular or street_side.</summary>
        static bool? ParkingOn(Dictionary<string, string> t, string side)
        {
            foreach (var key in new[] { "parking:" + side, "parking:lane:" + side, "parking:both", "parking:lane:both" })
            {
                string v = Tag(t, key);
                if (v.Length == 0) continue;
                return v == "lane" || v == "parallel" || v == "diagonal" || v == "perpendicular" || v == "street_side" || v == "marked";
            }
            return null;
        }

        static CrossSection CrossSectionOf(Chain c, OsmImportSettings st)
        {
            var t = c.first.tags;
            string L = c.firstReversed ? "right" : "left", R = c.firstReversed ? "left" : "right";
            var xs = new CrossSection();
            bool main = c.cls == RoadClass.MainStreet;
            bool? pl = ParkingOn(t, L), pr = ParkingOn(t, R);
            // MainStreet: parking both sides downtown unless tagged otherwise; elsewhere only where tagged
            bool parkL = pl ?? (main && c.inDowntown), parkR = pr ?? (main && c.inDowntown);
            xs.gutter = main ? st.gutter : 0;
            double lane = c.cls == RoadClass.Highway ? st.laneHighway : main ? st.laneMain : c.cls == RoadClass.Dirt ? st.laneDirt : c.cls == RoadClass.Yard ? st.laneYard : st.laneStreet;
            double width = ParseWidth(Tag(t, "width"));
            int lanes = 0;
            int.TryParse(Tag(t, "lanes"), NumberStyles.Integer, Inv, out lanes);
            if (width > 0)
            {
                xs.source = "width";
                xs.hwLeft = xs.hwRight = width / 2;
                xs.lanes = lanes;
                xs.laneWidth = lanes > 0 ? width / lanes : lane;
                return xs;
            }
            if (lanes > 0 || main || c.cls == RoadClass.Highway)
            {
                xs.source = lanes > 0 ? "lanes" : "class";
                xs.lanes = lanes > 0 ? lanes : Oneway(t) != 0 && c.cls != RoadClass.Highway ? 1 : 2;
                xs.laneWidth = lane;
                xs.parkingLeft = parkL ? st.parkingLane : 0;
                xs.parkingRight = parkR ? st.parkingLane : 0;
                double half = xs.lanes * lane / 2;
                xs.hwLeft = half + xs.parkingLeft + xs.gutter;
                xs.hwRight = half + xs.parkingRight + xs.gutter;
                return xs;
            }
            double w = c.cls == RoadClass.Street ? (c.inKerb ? st.streetWidth : st.ruralStreetWidth) : c.cls == RoadClass.Dirt ? st.dirtWidth : st.yardWidth;
            xs.source = "class";
            xs.lanes = Oneway(t) != 0 ? 1 : 2;
            xs.laneWidth = lane;
            xs.parkingLeft = pl == true ? st.parkingLane : 0;
            xs.parkingRight = pr == true ? st.parkingLane : 0;
            xs.hwLeft = w / 2;
            xs.hwRight = w / 2;
            return xs;
        }

        /// <summary>Narrows a side that would reach a building (keeping fitMargin clear), down to one lane; reports both outcomes.</summary>
        static void FitToBuildings(Chain c, CrossSection xs, OsmImportSettings st, OsmImportReport report)
        {
            double floor = c.cls == RoadClass.Yard ? st.floorYard : c.cls == RoadClass.Dirt ? st.floorDirt
                : c.cls == RoadClass.Street ? st.floorStreet : st.floorThrough;
            for (int side = 0; side < 2; side++)
            {
                double near = side == 0 ? c.nearL : c.nearR, hw = side == 0 ? xs.hwLeft : xs.hwRight;
                if (double.IsInfinity(near) || hw <= near - st.fitMargin) continue;
                double want = near - st.fitMargin;
                double fit = Math.Max(floor, want);
                if (fit >= hw) continue;
                string where = string.Format(Inv, "{0} {1} side ({2}) at ({3:F0}, {4:F0})", c.cls, side == 0 ? "left" : "right", c.ways.Count > 0 ? c.ways[0] : "?", c.p[c.p.Count / 2].x, c.p[c.p.Count / 2].z);
                if (side == 0) { xs.hwLeft = fit; xs.parkingLeft = Math.Min(xs.parkingLeft, Math.Max(0, fit - 0.5 * xs.lanes * xs.laneWidth - xs.gutter)); }
                else { xs.hwRight = fit; xs.parkingRight = Math.Min(xs.parkingRight, Math.Max(0, fit - 0.5 * xs.lanes * xs.laneWidth - xs.gutter)); }
                if (!xs.source.EndsWith("+buildings", StringComparison.Ordinal)) xs.source += "+buildings";
                report.fitted.Add(string.Format(Inv, "{0}: {1:F2} -> {2:F2} m (building {3:F2} m from the centreline{4})", where, hw, fit, near,
                    want < floor && near > floor ? string.Format(Inv, "; one lane leaves only {0:F2} m clear", near - floor) : ""));
                if (near <= floor)
                {
                    report.cannotClear.Add(string.Format(Inv, "{0}: a building {1:F2} m from the centreline, inside one lane ({2:F2} m)", where, near, floor));
                    c.buildingConflict = true;
                }
            }
        }

        /// <summary>OSM width: metres ("7", "7.5 m"), feet ("24'", "24 ft"), feet and inches ("24'6\""); 0 when unreadable.</summary>
        public static double ParseWidth(string v)
        {
            if (string.IsNullOrEmpty(v)) return 0;
            v = v.Trim().ToLowerInvariant().Replace(',', '.');
            int ft = v.IndexOf('\'');
            if (ft > 0)
            {
                double.TryParse(v.Substring(0, ft), NumberStyles.Float, Inv, out double f);
                double inch = 0;
                var rest = v.Substring(ft + 1).Replace("\"", "").Trim();
                if (rest.Length > 0) double.TryParse(rest, NumberStyles.Float, Inv, out inch);
                return f * 0.3048 + inch * 0.0254;
            }
            double k = 1;
            if (v.EndsWith("ft", StringComparison.Ordinal)) { k = 0.3048; v = v.Substring(0, v.Length - 2); }
            else if (v.EndsWith("m", StringComparison.Ordinal)) v = v.Substring(0, v.Length - 1);
            return double.TryParse(v.Trim(), NumberStyles.Float, Inv, out double x) && x > 0 ? x * k : 0;
        }

        static double Speed(Dictionary<string, string> t, RoadClass cls, OsmImportSettings st)
        {
            string v = Tag(t, "maxspeed").Trim().ToLowerInvariant();
            if (v.Length > 0)
            {
                double k = v.EndsWith("mph", StringComparison.Ordinal) ? 1.609344 : 1;
                var num = v.Replace("mph", "").Replace("km/h", "").Replace("kmh", "").Trim();
                if (double.TryParse(num, NumberStyles.Float, Inv, out double x) && x > 0) return Math.Round(x * k, 1);
            }
            switch (cls)
            {
                case RoadClass.Highway: return st.speedHighway;
                case RoadClass.Dirt: return st.speedDirt;
                case RoadClass.Yard: case RoadClass.Driveway: return st.speedYard;
                default: return st.speedTown;
            }
        }

        // ------------------------------------------------------------------ 6 features and mapped driveways

        static void Features(List<Chain> pieces, OsmRoadSource src, RoadGraphInput input, OsmImportReport report)
        {
            var placed = new HashSet<long>();
            var tagged = new SortedDictionary<long, OsmRoadSource.Node>();
            foreach (var kv in src.nodes) if (FeatureKind(kv.Value.tags, out _)) tagged[kv.Key] = kv.Value;
            var onRoads = new HashSet<long>();
            foreach (var c in pieces)
            {
                var s = c.S();
                for (int i = 0; i < c.p.Count; i++)
                {
                    long id = c.nid[i];
                    if (id == 0 || !tagged.TryGetValue(id, out var nd)) continue;
                    onRoads.Add(id);
                    if (!placed.Add(id)) continue;
                    FeatureKind(nd.tags, out var kind);
                    var (way, reversed) = c.segWays[c.seg[Math.Min(i, c.seg.Count - 1)]];
                    string dir = Tag(nd.tags, "direction");
                    if (dir.Length == 0) dir = Tag(nd.tags, kind == RoadFeatureKind.Signals ? "traffic_signals:direction" : "stop:direction");
                    int d = dir == "forward" ? 1 : dir == "backward" ? -1 : 0;
                    if (reversed) d = -d;
                    input.features.Add(new RoadGraphInput.Feature { kind = kind, road = c.id, s = s[i], direction = d, osmNode = id, crossing = Tag(nd.tags, "crossing") });
                }
            }
            report.featuresPlaced = placed.Count;
            foreach (var kv in tagged)
                if (!onRoads.Contains(kv.Key) && OnAnyKeptWay(kv.Key, src)) report.featuresOutside++;
        }

        static bool OnAnyKeptWay(long node, OsmRoadSource src)
        {
            foreach (var w in src.ways)
            {
                if (Classify(w, src, out _, out _) != null) continue;
                foreach (var id in w.nodes) if (id == node) return true;
            }
            return false;
        }

        static bool FeatureKind(Dictionary<string, string> t, out RoadFeatureKind kind)
        {
            kind = RoadFeatureKind.Crossing;
            string hw = Tag(t, "highway");
            if (hw == "stop") { kind = Tag(t, "stop") == "all" ? RoadFeatureKind.AllWayStop : RoadFeatureKind.Stop; return true; }
            if (hw == "give_way") { kind = RoadFeatureKind.GiveWay; return true; }
            if (hw == "traffic_signals") { kind = RoadFeatureKind.Signals; return true; }
            if (hw == "crossing" || Tag(t, "crossing").Length > 0) { kind = RoadFeatureKind.Crossing; return true; }
            return false;
        }

        static void Driveways(List<Chain> pieces, List<OsmRoadSource.Way> siteWays, OsmRoadSource src, OsmImportReport report)
        {
            var where = new Dictionary<long, (Chain c, int i)>();
            foreach (var c in pieces)
                for (int i = 0; i < c.p.Count; i++)
                    if (c.nid[i] != 0 && !where.ContainsKey(c.nid[i])) where[c.nid[i]] = (c, i);
            foreach (var w in siteWays)
            {
                if (Tag(w.tags, "highway") != "service") continue;
                foreach (var id in w.nodes)
                {
                    if (!where.TryGetValue(id, out var hit)) continue;
                    var s = hit.c.S();
                    report.driveways.Add(new MappedDriveway { osmWay = w.osm, service = Tag(w.tags, "service"), node = id, at = hit.c.p[hit.i], road = hit.c.id, s = s[hit.i] });
                }
            }
        }
    }
}
