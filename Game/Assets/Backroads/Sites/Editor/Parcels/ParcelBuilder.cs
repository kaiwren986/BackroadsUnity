using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>Tunable numbers of the parcel split (card sites-S1, design §3).</summary>
    public sealed class ParcelSettings
    {
        /// <summary>Voronoi sites along the block edges and along the footprints (metres apart).</summary>
        public double ringSpacing = 1.5, footprintSpacing = 1.5;
        /// <summary>Leftover pieces smaller than this join the neighbour they share most boundary with (m²).</summary>
        public double minSliver = 50;
        /// <summary>A fuel canopy joins the nearest shop within this distance (research 08 §1.6: 35 m).</summary>
        public double fuelGroup = 35;
        /// <summary>A mapped lot serves the nearest non-house building within 30 m, shared with a second within 6 m (08 §1.5).</summary>
        public double fixedServe = 30, fixedShare = 6;
        /// <summary>Frontage sampling step along the walk's back edge (metres).</summary>
        public double frontageStep = 0.5;
        /// <summary>A lot line straightened between two buildings stays at least this far from both (metres).</summary>
        public double lineClearance = 1.0;

        /// <summary>How far a building claims the block's edge (reach) and its head start against neighbours (weight), by type.</summary>
        public static (double reach, double weight) Claim(BusinessType t)
        {
            // Reach: houses hold up to ~80 m of an open road (an acre-sized rural lot) and fill the village's 21 m spacing
            // by the split; shops ~90 m (research 08: fuel entrances 52 m apart, lots 1.1 x the building's width); civic,
            // industrial and farm sites more.
            switch (t)
            {
                case BusinessType.House:
                case BusinessType.Apartments:
                case BusinessType.Unknown:
                    return (45, 0);
                case BusinessType.Supermarket:
                case BusinessType.Fuel:
                    return (60, 5);
                case BusinessType.Civic:
                case BusinessType.Church:
                case BusinessType.School:
                    return (80, 5);
                case BusinessType.Warehouse:
                case BusinessType.Industrial:
                    return (90, 5);
                case BusinessType.Farm:
                case BusinessType.Quarry:
                case BusinessType.Mill:
                case BusinessType.Harbor:
                    return (200, 10);
                default:
                    return (50, 3);
            }
        }
    }

    /// <summary>The parcels of a town (card sites-S1) with what they were built from.</summary>
    public sealed class ParcelSet
    {
        public string town;
        public SiteTownData data;
        public BlockBuilder.Result blocks;
        public readonly List<Parcel> parcels = new List<Parcel>();
        /// <summary>Parcel regions (Clipper, mm), indexed like parcels.</summary>
        public readonly List<Paths64> regions = new List<Paths64>();
        public readonly List<FixedArea> fixedAreas = new List<FixedArea>();
        /// <summary>Mapped driveways, parking aisles and service ways that meet a street, as DrivewayRequests (mapped = true).</summary>
        public readonly List<DrivewayRequest> requests = new List<DrivewayRequest>();
        /// <summary>The parcel of each request (same index).</summary>
        public readonly List<int> requestParcel = new List<int>();
        /// <summary>Parcel id of each building (index into data.buildings), or -1.</summary>
        public int[] parcelOfBuilding;
        public readonly List<string> notes = new List<string>();
        public int sliversMerged, linesStraightened;
        public string hash;

        public Parcel ParcelOf(int building) => parcelOfBuilding != null && building >= 0 && building < parcelOfBuilding.Length && parcelOfBuilding[building] >= 0
            ? parcels[parcelOfBuilding[building]] : null;
    }

    /// <summary>
    /// sites-S1 steps 2-4: parcels on each block (BlockBuilder). The split is a Voronoi diagram of sites along the block's
    /// edges and along the footprints: a footprint's sites belong to its building, an edge site to the nearest building
    /// within its type's reach (else nobody). Each building's parcel is the union of its sites' cells, so parcel lines run
    /// perpendicular to the street where they meet it (cells of neighbouring edge sites meet on the perpendicular
    /// bisector) and halfway between buildings behind it; edge sites nobody claims leave vacant land. Public land (parks,
    /// pitches, cemeteries) is cut out, mapped lots and yards (fixed areas) are added to the building they serve, slivers
    /// join a neighbour. Every parcel records the faces it fronts (by the walk's back edge), corner, alley, depth, and an
    /// easement to the street when it has no frontage. Mapped driveways become DrivewayRequests on their parcel.
    /// </summary>
    public static class ParcelBuilder
    {
        public const int Version = 1;
        /// <summary>Room kept round a building that stands inside another's mapped area (metres).</summary>
        public const double StandingRoom = 3.0;

        sealed class Owner
        {
            public int main;
            public readonly List<int> members = new List<int>();
            public BusinessType type;
            public BuildingForm form;
            public double reach, weight;
            public double2 centroid;
            public int block = -1;
            public Paths64 region;
        }

        sealed class Piece
        {
            public ParcelKind kind;
            public Paths64 region;
            public Owner owner;
            public int fixedArea = -1;
            public string landuse;
            public long key;
        }

        static readonly HashSet<string> PublicLeisure = new HashSet<string> { "park", "pitch", "playground", "garden", "recreation_ground", "sports_centre", "dog_park" };
        static readonly HashSet<string> PublicLanduse = new HashSet<string> { "cemetery", "recreation_ground", "village_green" };
        static readonly HashSet<string> DrivewayServices = new HashSet<string> { "driveway", "shared_driveway", "parking_aisle", "drive-through", "" };

        public static ParcelSet Build(SiteTownData data, BlockBuilder.Result blocks, ParcelSettings st = null)
        {
            st = st ?? new ParcelSettings();
            var set = new ParcelSet { town = data.townId, data = data, blocks = blocks };
            var B = data.buildings;
            set.parcelOfBuilding = Enumerable.Repeat(-1, B.Count).ToArray();

            // ---------------------------------------------------------------- owners (fuel canopies join their shop)
            var owners = new List<Owner>();
            var ownerOf = new Dictionary<int, Owner>();
            var canopies = new List<int>();
            foreach (var b in B)
            {
                if (!b.inPlay || b.Accessory) continue;
                if (b.business == BusinessType.Fuel && IsRoof(b)) { canopies.Add(b.index); continue; }
                var o = NewOwner(b);
                owners.Add(o);
                ownerOf[b.index] = o;
            }
            foreach (var ci in canopies)
            {
                var c = B[ci];
                Owner best = null;
                double bd = st.fuelGroup;
                foreach (var o in owners)
                {
                    var m = B[o.main];
                    if (m.business == BusinessType.House || m.business == BusinessType.Apartments) continue;
                    double d = Gap(c.poly, m.poly);
                    if (d < bd - 1e-9) { bd = d; best = o; }
                }
                if (best != null)
                {
                    best.members.Add(ci);
                    best.type = BusinessType.Fuel;
                    (best.reach, best.weight) = ParcelSettings.Claim(BusinessType.Fuel);
                    ownerOf[ci] = best;
                }
                else
                {
                    var o = NewOwner(c);
                    owners.Add(o);
                    ownerOf[ci] = o;
                }
            }
            owners.Sort((x, y) => x.main.CompareTo(y.main));

            // ---------------------------------------------------------------- blocks of the owners
            var regions = blocks.regions;
            foreach (var o in owners)
            {
                o.block = BlockAt(regions, o.centroid, 10.0);
                if (o.block < 0) set.notes.Add($"{B[o.main].osm} ({o.type}): centroid in no block (road, walk or water): no parcel");
            }

            // ---------------------------------------------------------------- fixed areas and public land
            BuildFixedAreas(data, set);
            foreach (var f in set.fixedAreas)
            {
                var near = new List<(double d, Owner o)>();
                foreach (var o in owners)
                {
                    var m = B[o.main];
                    if (m.business == BusinessType.House || m.business == BusinessType.Apartments) continue;
                    double d = double.MaxValue;
                    foreach (var mi in o.members) d = Math.Min(d, Gap(f.outer, B[mi].poly));
                    if (d <= st.fixedServe) near.Add((d, o));
                }
                near.Sort((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : x.o.main.CompareTo(y.o.main));
                var served = new List<int>();
                if (near.Count > 0) served.Add(near[0].o.main);
                if (near.Count > 1 && near[1].d <= st.fixedShare) served.Add(near[1].o.main);
                f.servedBy = served.ToArray();
            }
            var publicAreas = new List<SiteTownData.MappedArea>();
            foreach (var a in data.landuse)
            {
                var lei = a.Tag("leisure");
                var lu = a.Tag("landuse");
                if ((lei != null && PublicLeisure.Contains(lei)) || (lu != null && PublicLanduse.Contains(lu)) || a.Tag("amenity") == "grave_yard") publicAreas.Add(a);
            }
            publicAreas.Sort((x, y) => string.CompareOrdinal(x.osm, y.osm));

            // ---------------------------------------------------------------- per block
            var pieces = new List<(int block, Piece p)>();
            for (int bi = 0; bi < blocks.blocks.Count; bi++)
            {
                var region = regions[bi];
                var mine = owners.Where(o => o.block == bi).ToList();
                var local = new List<Piece>();

                // 1. Voronoi of edge and footprint sites.
                if (mine.Count > 0) Split(region, mine, B, st);

                // 2. Public land: cut out of parcels whose building stands outside it.
                var publics = new List<Piece>();
                foreach (var a in publicAreas)
                {
                    var pa = new Paths64();
                    foreach (var (o, hs) in a.polys) pa.AddRange(Clip.Region(o, hs));
                    var pb = Clip.Intersect(Clip.Union(pa), region);
                    if (Clip.Area(pb) < 1) continue;
                    foreach (var pp in publics) pb = Clip.Difference(pb, pp.region);
                    foreach (var o in mine)
                    {
                        if (Clip.Contains(pb, o.centroid)) pb = Clip.Difference(pb, o.region);
                        else o.region = Clip.Difference(o.region, pb);
                    }
                    foreach (var piece in Clip.Pieces(pb))
                        if (Clip.Area(piece) >= 1) publics.Add(new Piece { kind = ParcelKind.Public, region = piece, landuse = a.Tag("leisure") ?? a.Tag("landuse") ?? a.Tag("amenity") });
                }

                // 3. Fixed areas: added to the building they serve (when it is on this block), else a parcel of their own.
                var fixedPieces = new List<Piece>();
                var hosts = new HashSet<Owner>();
                foreach (var f in set.fixedAreas)
                {
                    var fr = Clip.Region(f.outer, f.holes);
                    var fb = Clip.Intersect(fr, region);
                    if (Clip.Area(fb) < 1) continue;
                    Owner host = f.servedBy.Length > 0 && ownerOf.TryGetValue(f.servedBy[0], out var h) && h.block == bi ? h : null;
                    // A building standing inside the mapped area (a warden's house in a campground) keeps its footprint
                    // plus StandingRoom; the area takes the rest.
                    var standing = new Paths64();
                    foreach (var o in mine)
                        if (o != host && Clip.Contains(fb, o.centroid))
                            foreach (var mi in o.members) standing.AddRange(Clip.Region(B[mi].poly));
                    if (standing.Count > 0) fb = Clip.Difference(fb, Clip.Inflate(Clip.Union(standing), StandingRoom, JoinType.Miter));
                    foreach (var o in mine) if (o != host) o.region = Clip.Difference(o.region, fb);
                    foreach (var pp in publics) pp.region = Clip.Difference(pp.region, fb);
                    foreach (var pp in fixedPieces) fb = Clip.Difference(fb, pp.region);
                    if (host != null)
                    {
                        hosts.Add(host);
                        var joined = Clip.Pieces(Clip.Union(host.region, fb));
                        // The host keeps the piece with its building; a lot that does not touch it is a parcel of its own.
                        host.region = PickPiece(joined, host.centroid);
                        foreach (var rest in joined)
                            if (!ReferenceEquals(rest, host.region) && Clip.Area(Clip.Intersect(rest, fb)) >= 1)
                                fixedPieces.Add(new Piece { kind = ParcelKind.Vacant, region = Clip.Intersect(rest, fb), fixedArea = f.id });
                    }
                    else
                        foreach (var piece in Clip.Pieces(fb))
                            if (Clip.Area(piece) >= 1) fixedPieces.Add(new Piece { kind = ParcelKind.Vacant, region = piece, fixedArea = f.id });
                }

                // 4. One piece per building: the one holding its building; the rest goes back to the leftover.
                foreach (var o in mine)
                {
                    var ps = Clip.Pieces(o.region);
                    o.region = ps.Count == 0 ? new Paths64() : PickPiece(ps, o.centroid);
                    local.Add(new Piece { kind = ParcelKind.Building, region = o.region, owner = o });
                }
                local.AddRange(fixedPieces);
                foreach (var pp in publics) foreach (var piece in Clip.Pieces(pp.region)) if (Clip.Area(piece) >= 1) local.Add(new Piece { kind = ParcelKind.Public, region = piece, landuse = pp.landuse });

                // 5. Leftover: slivers join the neighbour they share most edge with, the rest is vacant land.
                var used = new Paths64();
                foreach (var p in local) used.AddRange(p.region);
                var left = Clip.Difference(region, Clip.Union(used));
                foreach (var piece in Clip.Pieces(left))
                {
                    double a = Clip.Area(piece);
                    if (a < 1e-6) continue;
                    if (a < st.minSliver)
                    {
                        Piece best = null;
                        double bestLen = Math.Min(0.5, Math.Sqrt(a) * 0.5);
                        foreach (var p in local)
                        {
                            if (p.region.Count == 0) continue;
                            double len = Clip.SharedLength(piece, p.region);
                            if (len > bestLen + 1e-9 || (best != null && Math.Abs(len - bestLen) <= 1e-9 && p.kind < best.kind)) { bestLen = len; best = p; }
                        }
                        if (best != null)
                        {
                            var merged = Clip.Pieces(Clip.Union(best.region, piece));
                            if (merged.Count == 1)
                            {
                                best.region = merged[0];
                                if (best.owner != null) best.owner.region = best.region;
                                set.sliversMerged++;
                                continue;
                            }
                        }
                    }
                    local.Add(new Piece { kind = ParcelKind.Vacant, region = piece });
                }

                set.linesStraightened += Straighten(local, B, hosts, st.lineClearance);

                foreach (var p in local) p.key = Clip.Key(p.region);
                local.Sort((x, y) =>
                {
                    int c = x.kind.CompareTo(y.kind);
                    if (c != 0) return c;
                    if (x.owner != null && y.owner != null) return x.owner.main.CompareTo(y.owner.main);
                    c = x.fixedArea.CompareTo(y.fixedArea);
                    return c != 0 ? c : x.key.CompareTo(y.key);
                });
                foreach (var p in local) if (p.region.Count > 0) pieces.Add((bi, p));
            }

            // ---------------------------------------------------------------- parcels
            foreach (var (bi, p) in pieces)
            {
                var parcel = new Parcel { id = set.parcels.Count, block = bi, kind = p.kind, area = Clip.Area(p.region), landuse = p.landuse };
                SetRings(parcel, p.region);
                if (p.owner != null)
                {
                    parcel.buildings = p.owner.members.ToArray();
                    parcel.type = p.owner.type;
                    parcel.form = p.owner.form;
                    foreach (var m in p.owner.members) set.parcelOfBuilding[m] = parcel.id;
                }
                var fixedIds = new List<int>();
                if (p.fixedArea >= 0) fixedIds.Add(p.fixedArea);
                foreach (var f in set.fixedAreas)
                {
                    if (f.id == p.fixedArea) continue;
                    if (p.owner != null && f.servedBy.Length > 0 && f.servedBy[0] == p.owner.main && Clip.Area(Clip.Intersect(Clip.Region(f.outer, f.holes), p.region)) >= 1) fixedIds.Add(f.id);
                }
                parcel.fixedAreas = fixedIds.ToArray();
                foreach (var fid in fixedIds) if (set.fixedAreas[fid].parcel < 0) set.fixedAreas[fid].parcel = parcel.id;
                set.parcels.Add(parcel);
                set.regions.Add(p.region);
            }

            // Accessory buildings join the parcel they stand on.
            foreach (var b in B)
            {
                if (!b.inPlay || set.parcelOfBuilding[b.index] >= 0) continue;
                if (!b.Accessory && !ownerOf.ContainsKey(b.index)) continue;
                int pid = ParcelAt(set, b.centroid, 5.0);
                if (pid < 0) { set.notes.Add($"{b.osm} ({b.business}): on no parcel"); continue; }
                var p = set.parcels[pid];
                if (!b.Accessory)
                {
                    set.notes.Add($"{b.osm} ({b.business}): main building without a parcel of its own (on parcel {pid})");
                    continue;
                }
                p.buildings = p.buildings.Concat(new[] { b.index }).ToArray();
                set.parcelOfBuilding[b.index] = pid;
            }

            Frontage(set, st);
            Easements(set);
            MappedDriveways(set);
            set.hash = Hash(set);
            return set;
        }

        static Owner NewOwner(SiteBuilding b)
        {
            var o = new Owner { main = b.index, type = b.business, form = b.form, centroid = Poly2.Centroid(b.poly) };
            o.members.Add(b.index);
            (o.reach, o.weight) = ParcelSettings.Claim(b.business);
            return o;
        }

        static bool IsRoof(SiteBuilding b) => b.tags.TryGetValue("building", out var v) && (v == "roof" || v == "canopy");

        /// <summary>Distance between two polygons (0 when they touch or one holds the other).</summary>
        static double Gap(double2[] a, double2[] b)
        {
            if (a.Length > 0 && Poly2.Contains(b, a[0])) return 0;
            if (b.Length > 0 && Poly2.Contains(a, b[0])) return 0;
            return Poly2.BoundaryGap(a, b);
        }

        static int BlockAt(List<Paths64> regions, double2 p, double tol)
        {
            for (int i = 0; i < regions.Count; i++) if (Clip.Contains(regions[i], p)) return i;
            int best = -1;
            double bd = tol;
            for (int i = 0; i < regions.Count; i++)
                foreach (var path in regions[i])
                {
                    double d = Poly2.BoundaryDistance(Clip.Pts(path), p, out _);
                    if (d < bd - 1e-9) { bd = d; best = i; }
                }
            return best;
        }

        static int ParcelAt(ParcelSet set, double2 p, double tol)
        {
            for (int i = 0; i < set.regions.Count; i++) if (Clip.Contains(set.regions[i], p)) return i;
            int best = -1;
            double bd = tol;
            for (int i = 0; i < set.regions.Count; i++)
                foreach (var path in set.regions[i])
                {
                    double d = Poly2.BoundaryDistance(Clip.Pts(path), p, out _);
                    if (d < bd - 1e-9) { bd = d; best = i; }
                }
            return best;
        }

        static Paths64 PickPiece(List<Paths64> ps, double2 c)
        {
            foreach (var p in ps) if (Clip.Contains(p, c)) return p;
            return ps.Count > 0 ? ps[0] : new Paths64();
        }

        static void SetRings(Parcel parcel, Paths64 region)
        {
            var holes = new List<double2[]>();
            foreach (var path in region)
            {
                var pts = Clip.Pts(path);
                if (Clipper.Area(path) > 0 && parcel.outer == null) parcel.outer = pts;
                else if (Clipper.Area(path) < 0) holes.Add(pts);
            }
            parcel.holes = holes.ToArray();
        }

        // ------------------------------------------------------------------------------------------ the Voronoi split
        static void Split(Paths64 region, List<Owner> owners, List<SiteBuilding> B, ParcelSettings st)
        {
            var pts = new List<double2>();
            var lab = new List<int>();
            // Edge sites: every ring of the block, sampled; owner = the nearest building within its reach.
            foreach (var path in region)
            {
                var ring = Clip.Pts(path);
                for (int i = 0; i < ring.Length; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Length];
                    double len = math.distance(a, b);
                    int n = Math.Max(1, (int)Math.Ceiling(len / st.ringSpacing - 1e-9));
                    for (int k = 0; k < n; k++)
                    {
                        var p = a + (b - a) * ((double)k / n);
                        pts.Add(p);
                        lab.Add(Claimant(p, owners, B));
                    }
                }
            }
            // Footprint sites: every member's outline, sampled.
            for (int oi = 0; oi < owners.Count; oi++)
                foreach (var mi in owners[oi].members)
                {
                    var ring = B[mi].poly;
                    for (int i = 0; i < ring.Length; i++)
                    {
                        var a = ring[i];
                        var b = ring[(i + 1) % ring.Length];
                        double len = math.distance(a, b);
                        int n = Math.Max(1, (int)Math.Ceiling(len / st.footprintSpacing - 1e-9));
                        for (int k = 0; k < n; k++) { pts.Add(a + (b - a) * ((double)k / n)); lab.Add(oi); }
                    }
                }
            var dl = new Delaunay2(pts);
            var cells = dl.Cells();
            var per = new Paths64[owners.Count];
            for (int i = 0; i < per.Length; i++) per[i] = new Paths64();
            for (int i = 0; i < cells.Length; i++)
            {
                if (cells[i] == null || lab[i] < 0 || cells[i].Length < 3) continue;
                var path = Clip.Path(cells[i]);
                if (Clipper.Area(path) < 0) path.Reverse();
                per[lab[i]].Add(path);
            }
            for (int oi = 0; oi < owners.Count; oi++)
                owners[oi].region = per[oi].Count == 0 ? new Paths64() : Clip.Intersect(Clip.Union(per[oi]), region);
        }

        static int Claimant(double2 p, List<Owner> owners, List<SiteBuilding> B)
        {
            int best = -1;
            double bd = double.MaxValue;
            for (int oi = 0; oi < owners.Count; oi++)
            {
                var o = owners[oi];
                double d = double.MaxValue;
                foreach (var mi in o.members)
                {
                    var poly = B[mi].poly;
                    double x = Poly2.Contains(poly, p) ? 0 : Poly2.BoundaryDistance(poly, p, out _);
                    if (x < d) d = x;
                }
                if (d > o.reach) continue;
                double w = d - o.weight;
                if (w < bd - 1e-9) { bd = w; best = oi; }
            }
            return best;
        }

        // ------------------------------------------------------------------------------------------ straight lot lines
        /// <summary>
        /// Surveyed lot lines: every boundary two parcels share (a chain of Voronoi edges) becomes the straight segment
        /// between its ends when both parcels stay simple, the segment stays lineClearance from their buildings, and neither
        /// parcel holds a mapped area or public land (their outlines stay exactly as mapped). Both parcels take the same
        /// segment, so they still tile the block. Returns the number of lines straightened.
        /// </summary>
        static int Straighten(List<Piece> local, List<SiteBuilding> B, HashSet<Owner> hosts, double clearance)
        {
            var rings = new List<Path64>(local.Count);
            for (int i = 0; i < local.Count; i++)
            {
                var p = local[i];
                bool ok = p.region.Count == 1 && Clipper.Area(p.region[0]) > 0 && p.fixedArea < 0 && p.kind != ParcelKind.Public && (p.owner == null || !hosts.Contains(p.owner));
                rings.Add(ok ? new Path64(p.region[0]) : null);
            }
            var owner = new Dictionary<(long, long, long, long), int>();
            void Index(int i, bool add)
            {
                var r = rings[i];
                for (int k = 0; k < r.Count; k++)
                {
                    var a = r[k];
                    var b = r[(k + 1) % r.Count];
                    var key = (a.X, a.Y, b.X, b.Y);
                    if (add) owner[key] = i; else owner.Remove(key);
                }
            }
            for (int i = 0; i < rings.Count; i++) if (rings[i] != null) Index(i, true);
            int count = 0;
            var tried = new HashSet<(int, long, long, long, long)>();
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i] == null) continue;
                for (int guard = 0; guard < 256; guard++)
                {
                    var r = rings[i];
                    int n = r.Count;
                    var nb = new int[n];
                    for (int k = 0; k < n; k++)
                    {
                        var a = r[k];
                        var b = r[(k + 1) % n];
                        nb[k] = owner.TryGetValue((b.X, b.Y, a.X, a.Y), out var j) && j != i ? j : -1;
                    }
                    // The first untried run of >= 2 edges shared with one neighbour j > i, not covering the whole ring.
                    int k0 = -1, k1 = -1, jn = -1;
                    for (int k = 0; k < n && k0 < 0; k++)
                    {
                        int j = nb[k];
                        if (j <= i || nb[(k - 1 + n) % n] == j) continue;   // a run starts here
                        int len = 1;
                        while (len < n && nb[(k + len) % n] == j) len++;
                        if (len < 2 || len >= n) continue;
                        var s0 = r[k];
                        var s1 = r[(k + len) % n];
                        if (tried.Contains((i, s0.X, s0.Y, s1.X, s1.Y))) continue;
                        k0 = k; k1 = (k + len - 1) % n; jn = j;
                    }
                    if (k0 < 0) break;
                    var A = r[k0];
                    var Bp = r[(k1 + 1) % n];
                    var np = new Path64();
                    for (int k = (k1 + 1) % n; ; k = (k + 1) % n) { np.Add(r[k]); if (k == k0) break; }
                    var q = rings[jn];
                    int m = q.Count, qb = q.IndexOf(Bp), qa = q.IndexOf(A);
                    tried.Add((i, A.X, A.Y, Bp.X, Bp.Y));
                    if (qb < 0 || qa < 0) continue;
                    var nq = new Path64();
                    for (int k = qa; ; k = (k + 1) % m) { nq.Add(q[k]); if (k == qb) break; }
                    if (!Simple(np) || !Simple(nq) || Math.Abs(Clipper.Area(np) + Clipper.Area(nq) - Clipper.Area(r) - Clipper.Area(q)) > 1e3 ||
                        !Clears(np, local[i], B, A, Bp, clearance) || !Clears(nq, local[jn], B, A, Bp, clearance))
                        continue;
                    // The new line must stay inside the two parcels' union (else it would cut into a third one).
                    var before = Clipper.Union(new Paths64 { r, q }, FillRule.NonZero);
                    var after = Clipper.Union(new Paths64 { np, nq }, FillRule.NonZero);
                    if (Math.Abs(Clipper.Area(Clipper.Difference(after, before, FillRule.NonZero))) > 1e3 ||
                        Math.Abs(Clipper.Area(Clipper.Intersect(new Paths64 { np }, new Paths64 { nq }, FillRule.NonZero))) > 1e3)
                        continue;
                    Index(i, false); Index(jn, false);
                    rings[i] = np; rings[jn] = nq;
                    Index(i, true); Index(jn, true);
                    count++;
                }
            }
            for (int i = 0; i < local.Count; i++)
            {
                if (rings[i] == null) continue;
                local[i].region = new Paths64 { rings[i] };
                if (local[i].owner != null) local[i].owner.region = local[i].region;
            }
            return count;
        }

        static bool Simple(Path64 p)
        {
            if (p.Count < 3) return false;
            double a = Clipper.Area(p);
            if (a <= 0) return false;
            var u = Clipper.Union(new Paths64 { p }, FillRule.NonZero);
            return u.Count == 1 && Math.Abs(Clipper.Area(u[0]) - a) <= 1e3;
        }

        /// <summary>The piece's buildings stay inside it and at least clearance from the new segment.</summary>
        static bool Clears(Path64 ring, Piece piece, List<SiteBuilding> B, Point64 a, Point64 b, double clearance)
        {
            if (piece.owner == null) return true;
            var pa = Clip.D(a);
            var pb = Clip.D(b);
            var poly = Clip.Pts(ring);
            foreach (var mi in piece.owner.members)
            {
                var f = B[mi].poly;
                foreach (var v in f) if (!Poly2.Contains(poly, v)) return false;
                for (int k = 0; k < f.Length; k++)
                    if (Poly2.SegSegDist(pa, pb, f[k], f[(k + 1) % f.Length]) < clearance) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------------------------------ fixed areas
        static void BuildFixedAreas(SiteTownData data, ParcelSet set)
        {
            var list = new List<(FixedKind kind, SiteTownData.MappedArea a)>();
            foreach (var a in data.parking) list.Add((FixedKind.Parking, a));
            foreach (var a in data.fuel) list.Add((FixedKind.Fuel, a));
            foreach (var a in data.yards.Concat(data.landuse))
            {
                var k = KindOf(a);
                if (k.HasValue) list.Add((k.Value, a));
            }
            foreach (var (kind, a) in list)
                foreach (var (outer, holes) in a.polys)
                {
                    if (!PolyInPlay(data, outer)) continue;
                    set.fixedAreas.Add(new FixedArea
                    {
                        id = set.fixedAreas.Count, osm = a.osm, kind = kind, outer = outer, holes = holes ?? Array.Empty<double2[]>(),
                        area = Math.Abs(Poly2.SignedArea(outer)) - (holes ?? Array.Empty<double2[]>()).Sum(h => Math.Abs(Poly2.SignedArea(h))),
                    });
                }
        }

        static bool PolyInPlay(SiteTownData data, double2[] p)
        {
            foreach (var q in p) if (data.InPlay(q)) return true;
            return false;
        }

        static FixedKind? KindOf(SiteTownData.MappedArea a)
        {
            switch (a.Tag("landuse"))
            {
                case "farmyard": return FixedKind.Farmyard;
                case "industrial": return FixedKind.Industrial;
                case "retail": return FixedKind.Retail;
                case "commercial": return FixedKind.Commercial;
                case "quarry": return FixedKind.Quarry;
                case "harbour":
                case "port": return FixedKind.Harbour;
            }
            if (a.Tag("leisure") == "marina") return FixedKind.Marina;
            if (a.Tag("man_made") == "works") return FixedKind.Works;
            if (a.Tag("man_made") == "pier") return FixedKind.Pier;
            if (a.Tag("tourism") == "caravan_site" || a.Tag("tourism") == "camp_site") return FixedKind.CaravanSite;
            if (a.Tag("amenity") == "parking") return FixedKind.Parking;
            if (a.Tag("amenity") == "fuel") return FixedKind.Fuel;
            return null;
        }

        // ------------------------------------------------------------------------------------------ frontage
        static void Frontage(ParcelSet set, ParcelSettings st)
        {
            var bounds = set.regions.Select(r => Clip.Bounds(r)).ToList();
            var runs = new Dictionary<int, List<FrontageRef>>();
            foreach (var f in set.blocks.fronts)
            {
                if (f.block < 0 || f.bridge) continue;
                double L = f.Length;
                if (L < 0.5) continue;
                int cur = -1;
                double start = 0;
                int steps = Math.Max(1, (int)Math.Ceiling(L / st.frontageStep));
                for (int k = 0; k <= steps; k++)
                {
                    int pid = -1;
                    double t = Math.Min(L, k * st.frontageStep);
                    if (k < steps)
                    {
                        double tm = Math.Min(L, (k + 0.5) * st.frontageStep);
                        var q = f.BackAt(tm, out var n) + n * 0.15;
                        for (int i = 0; i < set.parcels.Count; i++)
                        {
                            if (set.parcels[i].block != f.block) continue;
                            var (mn, mx) = bounds[i];
                            if (q.x < mn.x - 0.01 || q.x > mx.x + 0.01 || q.y < mn.y - 0.01 || q.y > mx.y + 0.01) continue;
                            if (Clip.Contains(set.regions[i], q)) { pid = i; break; }
                        }
                    }
                    if (pid != cur || k == steps)
                    {
                        if (cur >= 0 && t - start >= 0.5)
                        {
                            if (!runs.TryGetValue(cur, out var l)) runs[cur] = l = new List<FrontageRef>();
                            if (l.Count > 0 && l[l.Count - 1].faceId == f.faceId && Math.Abs(l[l.Count - 1].s1 - start) < 1e-3)
                            {
                                var last = l[l.Count - 1];
                                last.s1 = (float)t;
                                l[l.Count - 1] = last;
                            }
                            else l.Add(new FrontageRef { faceId = f.faceId, s0 = (float)start, s1 = (float)t, street = f.street, osmWay = f.osmWay });
                        }
                        cur = pid;
                        start = t;
                    }
                }
            }
            var alleyFace = new HashSet<int>(set.blocks.fronts.Where(f => f.alley).Select(f => f.faceId));
            var faces = set.blocks.fronts.ToDictionary(f => f.faceId);
            foreach (var p in set.parcels)
            {
                var l = runs.TryGetValue(p.id, out var r) ? r : new List<FrontageRef>();
                l.Sort((x, y) =>
                {
                    int c = alleyFace.Contains(x.faceId).CompareTo(alleyFace.Contains(y.faceId));
                    if (c != 0) return c;
                    c = y.Length.CompareTo(x.Length);
                    return c != 0 ? c : x.faceId.CompareTo(y.faceId);
                });
                p.frontage = l.ToArray();
                double main = 0;
                var dirs = new List<(string way, double2 d)>();
                foreach (var fr in l)
                {
                    bool alley = alleyFace.Contains(fr.faceId);
                    if (alley) p.alley = true;
                    else
                    {
                        main += fr.Length;
                        if (fr.Length >= 3 && faces.TryGetValue(fr.faceId, out var fl)) dirs.Add((fr.osmWay ?? "", KerbDir(fl, 0.5 * (fr.s0 + fr.s1))));
                    }
                }
                // A corner parcel fronts two different streets that meet at an angle (not a lot running through to a
                // parallel street, nor one street whose OSM way is split).
                p.corner = false;
                for (int a = 0; a < dirs.Count && !p.corner; a++)
                    for (int b = a + 1; b < dirs.Count && !p.corner; b++)
                        if (dirs[a].way != dirs[b].way && Math.Abs(math.dot(dirs[a].d, dirs[b].d)) <= Math.Cos(35 * Math.PI / 180)) p.corner = true;
                if (!p.alley) p.alley = NearAlley(set, p);
                double front = main > 0 ? main : l.Sum(x => x.Length);
                p.depth = front > 0.5 ? (float)(p.area / front) : 0;
            }
        }

        /// <summary>Unit direction of a face's kerb line at arc length t.</summary>
        static double2 KerbDir(FrontLine f, double t)
        {
            int n = f.kerb.Length;
            int i = 0;
            while (i < n - 2 && f.s[i + 1] < t) i++;
            var d = f.kerb[Math.Min(i + 1, n - 1)] - f.kerb[i];
            return math.lengthsq(d) > 1e-18 ? math.normalize(d) : new double2(1, 0);
        }

        static bool NearAlley(ParcelSet set, Parcel p)
        {
            foreach (var w in set.data.service)
            {
                if (w.Service != "alley") continue;
                foreach (var q in w.pts)
                    if (Poly2.Contains(p.outer, q) || Poly2.BoundaryDistance(p.outer, q, out _) <= 3.0) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------ easements
        static void Easements(ParcelSet set)
        {
            foreach (var p in set.parcels)
            {
                if (p.frontage.Length > 0) continue;
                var from = p.buildings.Length > 0 ? Poly2.Centroid(set.data.buildings[p.buildings[0]].poly) : Poly2.Centroid(p.outer);
                double best = double.MaxValue;
                double2 at = 0;
                int face = -1;
                // The nearest face (its own block's first: a flag lot reaches its own street).
                for (int pass = 0; pass < 2 && face < 0; pass++)
                    foreach (var f in set.blocks.fronts)
                    {
                        if (f.bridge || f.block < 0 || (pass == 0 && f.block != p.block)) continue;
                        for (double t = 0; t <= f.Length + 1e-9; t += 2.0)
                        {
                            var q = f.BackAt(t, out _);
                            double d = math.distance(q, from);
                            if (d < best - 1e-9) { best = d; at = q; face = f.faceId; }
                        }
                    }
                if (face < 0) { set.notes.Add($"parcel {p.id}: no frontage and no face to reach"); continue; }
                p.easement = new[] { from, at };
                p.easementFace = face;
                // A mapped service way that starts on the parcel and reaches the street is the real easement.
                foreach (var w in set.data.service)
                {
                    if (w.pts.Length < 2) continue;
                    bool starts = Poly2.Contains(p.outer, w.pts[0]) || Poly2.Contains(p.outer, w.pts[w.pts.Length - 1]);
                    if (!starts) continue;
                    if (Crossing(set, w, out var fid, out _, out _)) { p.easement = w.pts; p.easementFace = fid; break; }
                }
            }
        }

        // ------------------------------------------------------------------------------------------ mapped driveways
        static void MappedDriveways(ParcelSet set)
        {
            var ways = set.data.service.Where(w => DrivewayServices.Contains(w.Service ?? "")).ToList();
            ways.Sort((x, y) => OsmId(x.osm).CompareTo(OsmId(y.osm)));
            int id = 1, outside = 0;
            var alleyOrRoad = new HashSet<string>(set.blocks.fronts.Select(f => f.osmWay), StringComparer.Ordinal);
            foreach (var w in ways)
            {
                if (w.pts.Length < 2) continue;
                if (w.pts.All(p => !set.data.InPlay(p))) { outside++; continue; }
                if (alleyOrRoad.Contains(w.osm)) continue;   // the road graph took it as a road (it has faces of its own)
                var crossings = Crossings(set, w);
                if (crossings.Count == 0 && NearKerb(set, w, 6.0, out var c0)) crossings.Add(c0);
                if (crossings.Count == 0)
                {
                    set.notes.Add($"{w.osm} ({w.Service ?? "service"}): joins no street (it branches off another service way or ends short of one)");
                    continue;
                }
                foreach (var c in crossings)
                {
                    var face = set.blocks.fronts.First(f => f.faceId == c.face);
                    // The parcel: 3 m in along the way, or further on in its direction when it stops short (a stub in the walk).
                    int pid = ParcelAt(set, c.inside, 0.5);
                    var dir = c.inside - c.at;
                    if (math.lengthsq(dir) > 1e-9) dir = math.normalize(dir);
                    for (int k = 1; pid < 0 && k <= 20 && math.lengthsq(dir) > 0; k++) pid = ParcelAt(set, c.at + dir * k, 0);
                    var parcel = pid >= 0 ? set.parcels[pid] : null;
                    var (kind, width) = Throat(w, parcel);
                    double half = width * 0.5;
                    double s0 = Math.Max(0, c.s - half), s1 = Math.Min(face.Length, c.s + half);
                    set.requests.Add(new DrivewayRequest
                    {
                        id = id++, faceId = c.face, s0 = (float)s0, s1 = (float)s1, kind = kind, flareLength = 0, returnRadius = 0,
                        surface = SurfaceOf(w, face), mapped = true, osmWay = w.osm,
                    });
                    set.requestParcel.Add(pid);
                }
            }
            if (outside > 0) set.notes.Add($"{outside} mapped service ways lie outside the play area");
        }

        /// <summary>A way vertex within maxDist of a face's kerb line (a way that ends on a road's centreline without crossing
        /// the kerb, e.g. into a junction mouth): the crossing at its projection.</summary>
        static bool NearKerb(ParcelSet set, SiteTownData.ServiceWay w, double maxDist, out Cross c)
        {
            c = default;
            double best = maxDist;
            bool found = false;
            double total = 0;
            var cum = new double[w.pts.Length];
            for (int i = 1; i < w.pts.Length; i++) { total += math.distance(w.pts[i - 1], w.pts[i]); cum[i] = total; }
            for (int vi = 0; vi < w.pts.Length; vi++)
            {
                if (vi != 0 && vi != w.pts.Length - 1) continue;   // ends only: a driveway meets the street at one end
                var p = w.pts[vi];
                foreach (var f in set.blocks.fronts)
                {
                    if (f.block < 0 || f.bridge) continue;
                    for (int k = 0; k + 1 < f.kerb.Length; k++)
                    {
                        var a = f.kerb[k];
                        var b = f.kerb[k + 1];
                        var ab = b - a;
                        double l2 = math.dot(ab, ab);
                        double u = l2 > 0 ? math.clamp(math.dot(p - a, ab) / l2, 0, 1) : 0;
                        var q = a + ab * u;
                        double d = math.distance(p, q);
                        if (d < best - 1e-9)
                        {
                            best = d;
                            found = true;
                            double along = cum[vi];
                            double toward = vi == 0 ? 1 : -1;
                            c = new Cross { face = f.faceId, s = f.s[k] + u * (f.s[k + 1] - f.s[k]), at = q, inside = PointAlong(w.pts, cum, along + toward * 3.0), along = along };
                        }
                    }
                }
            }
            return found;
        }

        struct Cross { public int face; public double s; public double2 at, inside; public double along; }

        static bool Crossing(ParcelSet set, SiteTownData.ServiceWay w, out int face, out double s, out double2 at)
        {
            var l = Crossings(set, w);
            face = l.Count > 0 ? l[0].face : -1;
            s = l.Count > 0 ? l[0].s : 0;
            at = l.Count > 0 ? l[0].at : 0;
            return l.Count > 0;
        }

        /// <summary>Where a service way crosses a face's kerb line (one per face, nearest the way's ends), with a point 3 m inside.</summary>
        static List<Cross> Crossings(ParcelSet set, SiteTownData.ServiceWay w)
        {
            var found = new Dictionary<int, Cross>();
            double total = 0;
            var cum = new double[w.pts.Length];
            for (int i = 1; i < w.pts.Length; i++) { total += math.distance(w.pts[i - 1], w.pts[i]); cum[i] = total; }
            foreach (var f in set.blocks.fronts)
            {
                if (f.block < 0 || f.bridge) continue;
                for (int i = 0; i + 1 < w.pts.Length; i++)
                {
                    var a = w.pts[i];
                    var b = w.pts[i + 1];
                    for (int k = 0; k + 1 < f.kerb.Length; k++)
                    {
                        if (!SegX(a, b, f.kerb[k], f.kerb[k + 1], out double t, out double u)) continue;
                        double along = cum[i] + t * math.distance(a, b);
                        double endDist = Math.Min(along, total - along);
                        double s = f.s[k] + u * (f.s[k + 1] - f.s[k]);
                        var at = a + (b - a) * t;
                        // Step 3 m along the way towards its far end, into the block.
                        double toward = along < total - along ? 1 : -1;
                        var inside = PointAlong(w.pts, cum, along + toward * 3.0);
                        if (!found.TryGetValue(f.faceId, out var old) || endDist < Math.Min(old.along, total - old.along))
                            found[f.faceId] = new Cross { face = f.faceId, s = s, at = at, inside = inside, along = along };
                    }
                }
            }
            var r = found.Values.ToList();
            r.Sort((x, y) => x.along.CompareTo(y.along));
            return r;
        }

        static double2 PointAlong(double2[] pts, double[] cum, double d)
        {
            d = Math.Max(0, Math.Min(d, cum[cum.Length - 1]));
            for (int i = 0; i + 1 < pts.Length; i++)
                if (d <= cum[i + 1] || i + 2 == pts.Length)
                {
                    double seg = cum[i + 1] - cum[i];
                    return seg > 1e-9 ? pts[i] + (pts[i + 1] - pts[i]) * ((d - cum[i]) / seg) : pts[i];
                }
            return pts[0];
        }

        static bool SegX(double2 a, double2 b, double2 c, double2 d, out double t, out double u)
        {
            t = u = 0;
            var r = b - a;
            var s = d - c;
            double den = Poly2.Cross(r, s);
            if (Math.Abs(den) < 1e-12) return false;
            var ca = c - a;
            t = Poly2.Cross(ca, s) / den;
            u = Poly2.Cross(ca, r) / den;
            return t >= 0 && t <= 1 && u >= 0 && u <= 1;
        }

        static (DrivewayKind, double) Throat(SiteTownData.ServiceWay w, Parcel p)
        {
            double width = double.NaN;
            if (w.tags.TryGetValue("width", out var ws) && double.TryParse(ws.Replace("m", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var wv)) width = Math.Max(3.0, Math.Min(12.0, wv));
            var svc = w.Service ?? "";
            var t = p != null ? p.type : BusinessType.Unknown;
            DrivewayKind kind;
            double dw;
            if (svc == "drive-through") { kind = DrivewayKind.Commercial; dw = 4.5; }
            else if (t == BusinessType.Farm) { kind = DrivewayKind.Field; dw = 5.0; }
            else if (t == BusinessType.Warehouse || t == BusinessType.Industrial || t == BusinessType.Quarry || t == BusinessType.Mill) { kind = DrivewayKind.Truck; dw = 12.0; }
            else if (svc == "parking_aisle" || (p != null && p.kind == ParcelKind.Building && t != BusinessType.House && t != BusinessType.Apartments && t != BusinessType.Unknown) || (p != null && p.fixedAreas.Length > 0))
            { kind = DrivewayKind.Commercial; dw = 7.3; }
            else { kind = DrivewayKind.Residential; dw = svc == "shared_driveway" ? 4.5 : 3.6; }
            return (kind, double.IsNaN(width) ? dw : width);
        }

        static SiteSurface SurfaceOf(SiteTownData.ServiceWay w, FrontLine f)
        {
            switch (w.tags.TryGetValue("surface", out var s) ? s : null)
            {
                case "asphalt":
                case "paved": return SiteSurface.Asphalt;
                case "concrete":
                case "concrete:plates": return SiteSurface.Concrete;
                case "paving_stones":
                case "sett":
                case "bricks": return SiteSurface.Pavers;
                case "gravel":
                case "fine_gravel":
                case "compacted":
                case "unpaved":
                case "pebblestone": return SiteSurface.Gravel;
                case "dirt":
                case "ground":
                case "earth":
                case "mud": return SiteSurface.Dirt;
                case "grass": return SiteSurface.Grass;
            }
            return f.kerbed ? SiteSurface.Asphalt : SiteSurface.Gravel;
        }

        static long OsmId(string osm)
        {
            if (osm == null) return 0;
            int k = osm.IndexOf('/');
            return k >= 0 && long.TryParse(osm.Substring(k + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        // ------------------------------------------------------------------------------------------ hash
        /// <summary>SHA-256 over every parcel (rings in mm, buildings, frontage, flags), fixed area and request.</summary>
        public static string Hash(ParcelSet set)
        {
            var sb = new StringBuilder(1 << 16);
            var ci = CultureInfo.InvariantCulture;
            sb.Append("v").Append(Version).Append(';').Append(set.town).Append('\n');
            void Ring(double2[] r) { if (r == null) { sb.Append('-'); return; } foreach (var q in r) sb.Append(Math.Round(q.x * 1000).ToString("R", ci)).Append(',').Append(Math.Round(q.y * 1000).ToString("R", ci)).Append(';'); sb.Append('|'); }
            foreach (var p in set.parcels)
            {
                sb.Append(p.id).Append(':').Append(p.block).Append(':').Append((int)p.kind).Append(':').Append((int)p.type).Append(':').Append(p.corner ? 'c' : '-').Append(p.alley ? 'a' : '-');
                Ring(p.outer);
                foreach (var h in p.holes) Ring(h);
                foreach (var b in p.buildings) sb.Append(b).Append(',');
                foreach (var f in p.frontage) sb.Append(f.faceId).Append('@').Append(Math.Round(f.s0 * 1000).ToString("R", ci)).Append('-').Append(Math.Round(f.s1 * 1000).ToString("R", ci)).Append(',');
                foreach (var f in p.fixedAreas) sb.Append('F').Append(f);
                if (p.easement != null) { sb.Append('E').Append(p.easementFace); Ring(p.easement); }
                sb.Append('\n');
            }
            foreach (var f in set.fixedAreas) { sb.Append(f.id).Append(f.osm).Append((int)f.kind).Append(f.parcel); foreach (var s in f.servedBy) sb.Append(',').Append(s); sb.Append('\n'); }
            for (int i = 0; i < set.requests.Count; i++)
            {
                var r = set.requests[i];
                sb.Append(r.id).Append(':').Append(r.faceId).Append(':').Append(Math.Round(r.s0 * 1000).ToString("R", ci)).Append('-').Append(Math.Round(r.s1 * 1000).ToString("R", ci))
                  .Append((int)r.kind).Append((int)r.surface).Append(r.osmWay).Append('>').Append(set.requestParcel[i]).Append('\n');
            }
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var hex = new StringBuilder(64);
                foreach (var x in h) hex.Append(x.ToString("x2", ci));
                return hex.ToString();
            }
        }
    }
}
