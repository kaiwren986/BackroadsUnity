using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>What every site plan reads (card sites-S3 inputs).</summary>
    public sealed class PlanContext
    {
        public string town;
        public SiteTownData data;
        public ParcelSet parcels;
        public readonly Dictionary<int, FrontLine> fronts = new Dictionary<int, FrontLine>();
        /// <summary>R2's frontage with its forbidden intervals (throats are placed clear of them); may be null.</summary>
        public IKerbFrontageSource kerbs;
        /// <summary>R2's cutter (S asks, R answers); null leaves every request as asked.</summary>
        public IDrivewayCutter cutter;
        public SiteProfile profile;
        public SiteTemplates templates;
        public ParkingDims dims = ParkingDims.Default;
        public bool useTemplates = true;
        /// <summary>The cutter's answers by request id (passes 2 and 3).</summary>
        public Dictionary<int, DrivewayCut> answers;
        /// <summary>Kerb zone (paved lots get Barrier150 kerbs inside it).</summary>
        public double2[] kerbZone;

        public PlanContext(string town, SiteTownData data, ParcelSet parcels)
        {
            this.town = town;
            this.data = data;
            this.parcels = parcels;
            if (parcels != null) foreach (var f in parcels.blocks.fronts) fronts[f.faceId] = f;
            kerbZone = data?.kerbZone;
        }

        public bool InKerbZone(double2 p) => kerbZone != null && kerbZone.Length > 2 && Poly2.Contains(kerbZone, p);
    }

    /// <summary>A resolved (or, in the first pass, hoped-for) driveway throat on a face, with its lot-side edge.</summary>
    public struct Throat
    {
        public bool ok;
        public int request;
        public FrontLine face;
        public double s0, s1;
        /// <summary>The throat's lot-side edge (the walk's back edge, or the shoulder's outer edge on unkerbed roads), a to b along the face.</summary>
        public double2 a, b;
        public double2 inward;
        public DrivewayCut cut;
        public double2 Centre => (a + b) * 0.5;
        public double Width => math.distance(a, b);
    }

    /// <summary>
    /// One parcel's plan under construction: the frame, the parcel's region, claimed surfaces (kept disjoint), driveway
    /// requests (ids stable across passes: parcel id x 64 + ordinal), doors, props and model requests.
    /// </summary>
    public sealed class PlanBuilder
    {
        public readonly PlanContext ctx;
        public readonly Parcel parcel;
        public readonly SitePlan plan;
        public readonly Paths64 region;
        public readonly List<SiteBuilding> buildings = new List<SiteBuilding>();
        public readonly SiteBuilding main;
        public readonly ulong seed;
        public StreetFrame frame;
        public bool kerbZone;
        readonly Paths64 m_Used = new Paths64();
        Paths64 m_Blocked;
        int m_Req;

        public PlanBuilder(PlanContext ctx, Parcel parcel, Paths64 region)
        {
            this.ctx = ctx;
            this.parcel = parcel;
            this.region = region;
            plan = new SitePlan { parcel = parcel.id, type = parcel.type, source = PlanSource.Rules };
            foreach (var bi in parcel.buildings) buildings.Add(ctx.data.buildings[bi]);
            main = buildings.Count > 0 ? buildings[0] : null;
            seed = SiteSeed.Of(ctx.town, main != null ? main.osm : "parcel/" + parcel.id);
            // Frame: the longest street (non-alley) frontage; else any; else the easement.
            int fi = -1;
            for (int i = 0; i < parcel.frontage.Length && fi < 0; i++)
                if (ctx.fronts.TryGetValue(parcel.frontage[i].faceId, out var fl) && !fl.alley) fi = i;
            if (fi < 0 && parcel.frontage.Length > 0) fi = 0;
            if (fi >= 0 && ctx.fronts.TryGetValue(parcel.frontage[fi].faceId, out var face)) frame = StreetFrame.Of(face, parcel.frontage[fi]);
            else if (parcel.easement != null && parcel.easement.Length >= 2)
            {
                var e = parcel.easement;
                frame = StreetFrame.Facing(e[e.Length - 1], e[e.Length - 2] - e[e.Length - 1]);
            }
            else frame = StreetFrame.Facing(Poly2.Centroid(parcel.outer), new double2(0, 1));
            kerbZone = ctx.InKerbZone(main != null ? main.centroid : Poly2.Centroid(parcel.outer));
        }

        public SiteRng Rng(string draw) => new SiteRng(seed, draw);

        // ------------------------------------------------------------------------------------------ regions
        /// <summary>The parcel minus the buildings grown by clear (metres).</summary>
        public Paths64 Free(double clear)
        {
            var b = new Paths64();
            foreach (var x in buildings) if (!x.OpenRoof) b.AddRange(Clip.Region(x.poly));
            if (b.Count == 0) return region;
            return Clip.Difference(region, Clip.Inflate(Clip.Union(b), clear, JoinType.Miter));
        }

        /// <summary>A frame rectangle (u0..u1 x v0..v1) on the parcel.</summary>
        public Paths64 Zone(double u0, double u1, double v0, double v1) => Clip.Intersect(Clip.Region(frame.Rect(u0, u1, v0, v1)), region);

        /// <summary>Everything a surface may not cover: buildings, mapped areas the parcel does not own, other parcels'
        /// requests' aprons (added as cuts resolve).</summary>
        Paths64 Blocked
        {
            get
            {
                if (m_Blocked != null) return m_Blocked;
                var b = new Paths64();
                foreach (var x in buildings) if (!x.OpenRoof) b.AddRange(Clip.Region(x.poly));   // surfaces run under canopies and carports
                m_Blocked = Clip.Union(b);
                return m_Blocked;
            }
        }

        public void Block(double2[] poly)
        {
            if (poly == null || poly.Length < 3) return;
            m_Blocked = Clip.Union(Blocked, Clip.Region(poly));
        }

        /// <summary>What is still free for surfaces: the parcel minus buildings and earlier surfaces.</summary>
        public Paths64 Open => Clip.Difference(Clip.Difference(region, Blocked), m_Used);

        /// <summary>Adds a surface (clipped to the parcel, kept off buildings and earlier surfaces). Returns what was kept.</summary>
        public Paths64 Surface(Paths64 area, SiteSurface surface, SurfaceUse use, float wear = 0)
        {
            var keep = Clip.Difference(Clip.Difference(Clip.Intersect(area, region), Blocked), m_Used);
            foreach (var piece in Clip.Pieces(keep))
            {
                if (Clip.Area(piece) < 0.5) continue;
                var outer = Clip.Pts(piece[0]);
                var holes = new List<double2[]>();
                for (int i = 1; i < piece.Count; i++) holes.Add(Clip.Pts(piece[i]));
                plan.surfaces.Add(new SurfacePoly { outer = outer, holes = holes.ToArray(), surface = surface, use = use, wear = wear });
                m_Used.AddRange(piece);
            }
            return keep;
        }

        /// <summary>A strip of the given width along a polyline (butt ends), as a region.</summary>
        public static Paths64 Strip(IReadOnlyList<double2> line, double width)
        {
            var p = new Paths64 { Clip.Path(line) };
            return Clipper.InflatePaths(p, width * 0.5 * Clip.Scale, JoinType.Miter, EndType.Butt, 3.0);
        }

        // ------------------------------------------------------------------------------------------ driveways
        public static float FlareOf(DrivewayKind k, bool kerbed) =>
            k == DrivewayKind.Truck ? 3f : k == DrivewayKind.Field ? (kerbed ? 1.5f : 0f) : k == DrivewayKind.Residential ? 1f : 1.5f;

        /// <summary>
        /// Asks for a driveway on a face, wanted centred on sWanted, kept within [lo, hi] of the face (the parcel's
        /// frontage) and, where possible, clear of R2's forbidden intervals with its flares. In later passes the cutter's
        /// answer decides the throat; a rejected one is asked again once at the next clear spot (card S3 step 4).
        /// </summary>
        public Throat Entrance(FrontLine face, double sWanted, double width, DrivewayKind kind, SiteSurface surface, double lo, double hi,
            bool mapped = false, string osmWay = null)
        {
            double L = face.Length;
            lo = Math.Max(0, lo);
            hi = Math.Min(L, hi);
            width = Math.Min(width, Math.Max(2.5, hi - lo));
            double flare = FlareOf(kind, face.kerbed);
            double s = Clear(face, sWanted, width, flare, lo, hi, mapped, double.NaN);
            int id = parcel.id * 64 + (++m_Req);
            var req = Request(id, face, s, width, kind, surface, mapped, osmWay);
            plan.driveways.Add(req);
            if (ctx.answers == null || !ctx.answers.TryGetValue(id, out var cut)) return Optimistic(id, face, req);
            if (cut.outcome != CutOutcome.Rejected) return FromCut(id, face, cut);
            // Re-plan once: the next clear spot away from the blocker.
            double s2 = Clear(face, sWanted, width, flare, lo, hi, mapped, s);
            int id2 = parcel.id * 64 + (++m_Req);
            if (double.IsNaN(s2) || Math.Abs(s2 - s) < 0.5)
            {
                plan.notes.Add($"driveway on face {face.faceId} at {s:F1} m rejected ({cut.rejectedBy}: {cut.reason}); no other spot");
                return new Throat { ok = false, request = id, face = face, cut = cut };
            }
            var req2 = Request(id2, face, s2, width, kind, surface, mapped, osmWay);
            plan.driveways.Add(req2);
            plan.notes.Add($"driveway on face {face.faceId} at {s:F1} m rejected ({cut.rejectedBy}); asked again at {s2:F1} m");
            if (!ctx.answers.TryGetValue(id2, out var cut2)) return Optimistic(id2, face, req2);
            if (cut2.outcome == CutOutcome.Rejected)
            {
                plan.notes.Add($"driveway on face {face.faceId} at {s2:F1} m rejected too ({cut2.rejectedBy}: {cut2.reason})");
                return new Throat { ok = false, request = id2, face = face, cut = cut2 };
            }
            return FromCut(id2, face, cut2);
        }

        DrivewayRequest Request(int id, FrontLine face, double s, double width, DrivewayKind kind, SiteSurface surface, bool mapped, string osm)
        {
            double L = face.Length;
            double s0 = Math.Max(0, s - width * 0.5), s1 = Math.Min(L, s0 + width);
            s0 = Math.Max(0, s1 - width);
            return new DrivewayRequest
            {
                id = id, faceId = face.faceId, s0 = (float)s0, s1 = (float)s1, kind = kind, flareLength = 0, returnRadius = 0,
                surface = surface, mapped = mapped, osmWay = osm,
            };
        }

        Throat Optimistic(int id, FrontLine face, DrivewayRequest r)
        {
            var a = face.BackAt(r.s0, out var n0);
            var b = face.BackAt(r.s1, out var n1);
            return new Throat { ok = true, request = id, face = face, s0 = r.s0, s1 = r.s1, a = a, b = b, inward = math.normalizesafe(n0 + n1) };
        }

        Throat FromCut(int id, FrontLine face, DrivewayCut cut)
        {
            face.BackAt(cut.s0, out var n0);
            face.BackAt(cut.s1, out var n1);
            var t = new Throat { ok = true, request = id, face = face, s0 = cut.s0, s1 = cut.s1, a = cut.lotEdgeA, b = cut.lotEdgeB, inward = math.normalizesafe(n0 + n1), cut = cut };
            if (math.lengthsq(t.a - t.b) < 1e-6) { t.a = face.BackAt(cut.s0, out _); t.b = face.BackAt(cut.s1, out _); }
            if (cut.apron != null && cut.apron.Length >= 3) Block(Poly2.Ccw(cut.apron));   // R's apron (unkerbed: over the shoulder)
            return t;
        }

        /// <summary>The s nearest sWanted (other than avoid) whose throat and flares clear R2's forbidden intervals, inside [lo, hi].</summary>
        double Clear(FrontLine face, double sWanted, double width, double flare, double lo, double hi, bool mapped, double avoid)
        {
            double half = width * 0.5;
            double a = lo + half, b = hi - half;
            if (a > b) return Math.Max(lo, Math.Min(hi, sWanted));
            var kf = ctx.kerbs?.Face(face.faceId);
            var forb = kf?.forbidden;
            bool Ok(double s)
            {
                if (forb == null) return true;
                foreach (var iv in forb)
                {
                    if (mapped && iv.reason != FrontageBlock.CornerReturn) continue;
                    if (s - half - flare < iv.s1 && s + half + flare > iv.s0) return false;
                }
                return true;
            }
            double start = Math.Max(a, Math.Min(b, sWanted));
            for (double d = 0; d <= (b - a) + 1e-9; d += 0.5)
                foreach (var s in new[] { start + d, start - d })
                {
                    if (s < a - 1e-9 || s > b + 1e-9) continue;
                    if (!double.IsNaN(avoid) && Math.Abs(s - avoid) < width) continue;
                    if (Ok(s)) return s;
                }
            return double.IsNaN(avoid) ? start : double.NaN;
        }

        /// <summary>The parcel's frontage range on a face (min s0, max s1 over its refs on that face).</summary>
        public (double lo, double hi) Range(int faceId)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var f in parcel.frontage)
                if (f.faceId == faceId) { lo = Math.Min(lo, f.s0); hi = Math.Max(hi, f.s1); }
            return lo <= hi ? (lo, hi) : (0, 0);
        }

        /// <summary>s on the frame's face for a frame u (the face point nearest the frame's (u, 0)).</summary>
        public double SAt(double u) => StreetFrame.SOn(frame.face, frame.FromUV(u, 0));

        // ------------------------------------------------------------------------------------------ doors, props, models
        /// <summary>A door on the wall of b that faces dir most (walls at least minLen long), centred on the wall or
        /// placed nearest `near`; appended to the plan (design §7a).</summary>
        public DoorSpec Door(SiteBuilding b, DoorKind kind, double2 dir, double width, double2? near = null, double minLen = 0, int avoidEdge = -1)
        {
            var poly = b.poly;
            int n = poly.Length;
            int best = -1;
            double bestScore = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                if (i == avoidEdge) continue;
                var e = poly[(i + 1) % n] - poly[i];
                double len = math.length(e);
                if (len < Math.Max(minLen, width + 0.6)) continue;
                var outward = new double2(e.y, -e.x) / len;
                double score = math.dot(outward, math.normalizesafe(dir)) + len * 1e-3;
                if (score > bestScore + 1e-9) { bestScore = score; best = i; }
            }
            if (best < 0)
            {
                for (int i = 0; i < n; i++)
                {
                    double len = math.distance(poly[i], poly[(i + 1) % n]);
                    if (len > bestScore) { bestScore = len; best = i; }
                }
            }
            var p0 = poly[best];
            var p1 = poly[(best + 1) % n];
            double L = math.distance(p0, p1);
            double off = L * 0.5;
            if (near.HasValue)
            {
                var ab = p1 - p0;
                off = math.clamp(math.dot(near.Value - p0, ab) / Math.Max(1e-9, L), 0, L);
            }
            off = math.clamp(off, width * 0.5 + 0.3, Math.Max(width * 0.5 + 0.3, L - width * 0.5 - 0.3));
            var d = new DoorSpec { building = b.osm, part = 0, kind = kind, wallEdge = best, offset = (float)Math.Round(off, 3), width = (float)width, sillY = float.NaN };
            plan.doors.Add(d);
            return d;
        }

        /// <summary>The world position of a door's centre and the wall's outward normal.</summary>
        public static (double2 pos, double2 outward) DoorAt(SiteBuilding b, DoorSpec d)
        {
            var p0 = b.poly[d.wallEdge];
            var p1 = b.poly[(d.wallEdge + 1) % b.poly.Length];
            var e = math.normalizesafe(p1 - p0);
            return (p0 + e * d.offset, new double2(e.y, -e.x));
        }

        public void Prop(string key, double2 pos, double2 facing, string reason)
        {
            if (!Clip.Contains(region, pos)) return;   // props stay on their parcel
            plan.props.Add(new PropSpot { key = key, pos = pos, yaw = (float)Poly2.Yaw(math.normalizesafe(facing, new double2(0, 1))), reason = reason });
        }

        public void Model(string key, double2[] footprint, float height, string reason) =>
            plan.models.Add(new ModelRequest { key = key, footprint = footprint, height = height, reason = reason });

        /// <summary>The building's box in the frame.</summary>
        public (double minU, double maxU, double minV, double maxV) Box(SiteBuilding b) => frame.Box(b.poly);

        /// <summary>The parcel's box in the frame.</summary>
        public (double minU, double maxU, double minV, double maxV) ParcelBox => frame.Box(parcel.outer);
    }
}
