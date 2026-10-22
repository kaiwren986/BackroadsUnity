using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// Standalone commercial sites (design §4.2): shops, offices, restaurants, fast food, supermarkets, motels, car repair,
    /// apartments and (with their own shares) civic buildings and churches. The lot goes in front, beside or behind the
    /// building by the measured shares of its kind among the positions that fit (front: the setback holds a parking
    /// module; side: a gap of 13 m; rear: a side drive or an alley reaches it), sized to the measured lot-to-building
    /// ratio, packed by the parking engine; one entrance (two above ~60 stalls or on corners); the main door facing the
    /// lot, a service door behind, a dumpster behind; per kind: the drive-through, the fire lane and truck dock, motel
    /// rooms along the lot, car-repair bays with their apron and a fenced yard.
    /// </summary>
    public static class CommercialModule
    {
        sealed class Kind
        {
            public string profileKey;
            public double ratio;
            public double front, side, rear;
            public bool driveThrough, fireLane, dock, bays, rooms;
            public double mainDoor = 1.8;
        }

        static Kind KindOf(BusinessType t)
        {
            switch (t)
            {
                case BusinessType.Office: return new Kind { profileKey = "office", ratio = 1.74, front = 0.08, side = 0.42, rear = 0.39 };
                case BusinessType.Restaurant: return new Kind { profileKey = "restaurant", ratio = 2.11, front = 0.25, side = 0.38, rear = 0.33 };
                case BusinessType.FastFood: return new Kind { profileKey = "fast_food", ratio = 2.87, front = 0.29, side = 0.57, rear = 0.14, driveThrough = true };
                case BusinessType.Supermarket: return new Kind { profileKey = "supermarket", ratio = 1.30, front = 0.30, side = 0.55, rear = 0.15, fireLane = true, dock = true, mainDoor = 3.0 };
                case BusinessType.Motel: return new Kind { profileKey = "motel", ratio = 1.01, front = 0.36, side = 0.50, rear = 0.07, rooms = true, mainDoor = 1.0 };
                case BusinessType.CarRepair: return new Kind { profileKey = "car_repair", ratio = 2.10, front = 0.40, side = 0.40, rear = 0.20, bays = true, mainDoor = 1.0 };
                case BusinessType.Apartments: return new Kind { profileKey = "apartments", ratio = 0.87, front = 0.20, side = 0.30, rear = 0.25, mainDoor = 1.2 };
                case BusinessType.Civic: return new Kind { profileKey = "civic", ratio = 0.76, front = 0.20, side = 0.46, rear = 0.32, mainDoor = 2.0 };
                case BusinessType.Church: return new Kind { profileKey = "church", ratio = 1.55, front = 0.07, side = 0.45, rear = 0.45, mainDoor = 2.0 };
                case BusinessType.School: return new Kind { profileKey = "school", ratio = 0.40, front = 0.13, side = 0.66, rear = 0.22, mainDoor = 2.0 };
                default: return new Kind { profileKey = "shop", ratio = 1.47, front = 0.206, side = 0.373, rear = 0.31 };
            }
        }

        public static void Plan(PlanBuilder pb, bool hasMappedLot)
        {
            var b = pb.main;
            var k = KindOf(pb.parcel.type);
            var prof = pb.ctx.profile;
            if (prof != null)
            {
                k.ratio = prof.P50($"parking.byServedType.{k.profileKey}.lotToBuildingArea", k.ratio);
                var sh = prof.Shares($"parking.byServedType.{k.profileKey}.position");
                foreach (var (key, w) in sh)
                {
                    if (key == "front") k.front = w;
                    else if (key == "side") k.side = w;
                    else if (key == "rear") k.rear = w;
                }
            }
            if (k.dock) k.rear = 0;   // the truck dock and its apron take the back of a supermarket
            var box = pb.Box(b);
            var face = pb.frame.face;
            double cu = 0.5 * (box.minU + box.maxU);

            // The drive-through lane first (the lot takes what is left); then where can the lot go?
            if (k.driveThrough) DriveThrough(pb, b);
            var pbx = pb.ParcelBox;
            var free = Clip.Intersect(pb.Free(2.5), Clip.Inflate(pb.Open, -0.5, JoinType.Miter));
            var zones = new List<(LotPosition pos, string side, Paths64 zone, double weight)>();
            if (face != null && box.minV - 2.5 >= 12.8)
                zones.Add((LotPosition.Front, "", Clip.Intersect(pb.Zone(pbx.minU - 1, pbx.maxU + 1, 0, box.minV - 2.5), free), k.front));
            var (s0, s1) = LotHelper.SpanAt(pb, Math.Max(0.5, 0.5 * (box.minV + box.maxV)), cu);
            if (box.minU - 2.0 - s0 >= 13) zones.Add((LotPosition.Side, "left", Clip.Intersect(pb.Zone(pbx.minU - 1, box.minU - 2.0, 0, box.maxV + 6), free), k.side * 0.5));
            if (s1 - box.maxU - 2.0 >= 13) zones.Add((LotPosition.Side, "right", Clip.Intersect(pb.Zone(box.maxU + 2.0, pbx.maxU + 1, 0, box.maxV + 6), free), k.side * 0.5));
            bool alley = pb.parcel.frontage.Any(f => pb.ctx.fronts.TryGetValue(f.faceId, out var fl) && fl.alley);
            double gapL = box.minU - s0, gapR = s1 - box.maxU;
            bool sideDrive = Math.Max(gapL, gapR) >= 5.0;
            if (pbx.maxV - box.maxV - 2.0 >= 12.8 && (sideDrive || alley))
                zones.Add((LotPosition.Rear, "", Clip.Intersect(pb.Zone(pbx.minU - 1, pbx.maxU + 1, box.maxV + 2.0, pbx.maxV + 1), free), k.rear));

            // The lot: the seeded pick by the measured shares first, then the others by share, the first whose trimmed lot
            // still holds a parking row (MinLotThickness) and 200 m².
            LotPosition chosen = LotPosition.Front;
            string chosenSide = "";
            Paths64 lot = null;
            if (!hasMappedLot && zones.Count > 0 && face != null)
            {
                int pick = pb.Rng("lot-position").Pick(zones.Select((z, i) => (i, z.weight)).ToList());
                var order = new List<int> { pick };
                order.AddRange(Enumerable.Range(0, zones.Count).Where(i => i != pick).OrderByDescending(i => zones[i].weight).ThenBy(i => i));
                double target = Math.Max(250, k.ratio * b.area);
                foreach (var i in order)
                {
                    var (pos, side, zone, _) = zones[i];
                    double t = LotHelper.MinLotThickness;
                    Paths64 cand;
                    if (pos == LotPosition.Front) cand = LotHelper.TrimToArea(pb, zone, new double2(0, 0), new double2(0, 1), target, t);
                    else if (pos == LotPosition.Rear) cand = LotHelper.TrimToArea(pb, zone, new double2(0, box.maxV + 2.0), new double2(0, 1), target, t);
                    else if (side == "left") cand = LotHelper.TrimToArea(pb, zone, new double2(box.minU - 2.0, 0), new double2(-1, 0), target, t);
                    else cand = LotHelper.TrimToArea(pb, zone, new double2(box.maxU + 2.0, 0), new double2(1, 0), target, t);
                    if (LotHelper.Thickness(cand) < t - 0.5 || Clip.Area(cand) < 200) continue;
                    chosen = pos; chosenSide = side; lot = cand;
                    break;
                }
            }
            double2 lotDir = lot != null ? Poly2.Centroid(Clip.Pts(Clip.Pieces(lot)[0][0])) - b.centroid : -pb.frame.v;
            if (lot == null || math.lengthsq(lotDir) < 1e-6) lotDir = -pb.frame.v;
            var mainDoor = pb.Door(b, DoorKind.Main, chosen == LotPosition.Rear || hasMappedLot || lot == null ? -pb.frame.v : lotDir, k.mainDoor);
            var service = pb.Door(b, DoorKind.Service, pb.frame.v, 1.0, null, 0, mainDoor.wallEdge);

            if (lot != null) PlaceLot(pb, k, lot, chosen, chosenSide, box, gapL, gapR, alley);
            else if (!hasMappedLot) pb.plan.notes.Add("no room for a lot: customers park on the street");

            // A walk along the door wall (front of shop) and from the street to the door.
            var (dpos, dout) = PlanBuilder.DoorAt(b, mainDoor);
            pb.Surface(PlanBuilder.Strip(new[] { dpos + dout * 0.2, dpos + dout * 2.4 }, Math.Max(3.0, k.mainDoor + 2)), SiteSurface.Concrete, SurfaceUse.Walk);
            if (face != null && face.kerbed)
            {
                var q = pb.frame.ToUV(dpos);
                if (q.y > 2) pb.Surface(PlanBuilder.Strip(new[] { pb.frame.FromUV(q.x, 0), pb.frame.FromUV(q.x, q.y - 0.3) }, 1.5), SiteSurface.Concrete, SurfaceUse.Walk);
            }

            if (k.bays) Bays(pb, b, box);
            if (k.rooms) Rooms(pb, b, mainDoor);
            if (k.dock) Dock(pb, b, box);

            // Props: bin and bench by the door, dumpster behind.
            pb.Prop("trash.bin", dpos + dout * 1.5 + Poly2.Perp(dout) * (k.mainDoor * 0.5 + 1.0), dout, "design §7: bin by the door");
            if (pb.parcel.type == BusinessType.Retail || pb.parcel.type == BusinessType.Restaurant)
                pb.Prop("bench", dpos + dout * 1.6 - Poly2.Perp(dout) * (k.mainDoor * 0.5 + 2.0), dout, "design §7: bench by the shop front");
            var (spos, sout) = PlanBuilder.DoorAt(b, service);
            pb.Prop("dumpster", spos + sout * 3.0 + Poly2.Perp(sout) * 3.0, sout, "design §7: dumpster behind every commercial building");
            if (pb.Rng("pallets").Value() < 0.5) pb.Prop("pallets", spos + sout * 2.5 - Poly2.Perp(sout) * 3.0, sout, "design §7: pallets and boxes behind");
            if (pb.plan.source != PlanSource.Mapped) pb.plan.source = PlanSource.Rules;
        }

        static void PlaceLot(PlanBuilder pb, Kind k, Paths64 lot, LotPosition pos, string side, (double minU, double maxU, double minV, double maxV) box,
            double gapL, double gapR, bool alley)
        {
            var face = pb.frame.face;
            var (lo, hi) = pb.Range(face.faceId);
            var surface = LotHelper.LotSurface(pb, "lot-surface");
            var entrances = new List<LotEntrance>();
            var lb = pb.frame.Box(Clip.Pts(Clip.Pieces(lot).FirstOrDefault()?[0] ?? new Path64()));
            double expected = Clip.Area(lot) / 30.0;
            bool two = (expected > 60 || pb.parcel.corner) && lb.maxU - lb.minU > 40;
            var targets = new LotTargets { paved = surface != SiteSurface.Gravel, kerbed = pb.kerbZone && surface != SiteSurface.Gravel, fireLane = k.fireLane };
            if (pos == LotPosition.Rear)
            {
                var alleyRef = pb.parcel.frontage.Where(f => pb.ctx.fronts.TryGetValue(f.faceId, out var fl) && fl.alley).ToList();
                if (alley && alleyRef.Count > 0)
                {
                    var af = pb.ctx.fronts[alleyRef[0].faceId];
                    var c = Poly2.Centroid(Clip.Pts(Clip.Pieces(lot)[0][0]));
                    var t = pb.Entrance(af, StreetFrame.SOn(af, c), 6.0, DrivewayKind.Commercial, surface, alleyRef[0].s0, alleyRef[0].s1);
                    if (t.ok)
                    {
                        var d = math.normalizesafe(t.inward);
                        var side2 = Poly2.Perp(d) * 3.0;
                        var hit = t.Centre + d * 0.3;
                        entrances.Add(new LotEntrance { a = hit - side2, b = hit + side2, street = af.street, requestId = t.request });
                    }
                }
                else
                {
                    bool left = gapL >= gapR;
                    double w = Math.Max(gapL, gapR) >= 8.5 ? 6.0 : 4.5;
                    double ud = left ? box.minU - 1.0 - w * 0.5 : box.maxU + 1.0 + w * 0.5;
                    var t = pb.Entrance(face, pb.SAt(ud), w, DrivewayKind.Commercial, surface, lo, hi);
                    if (t.ok)
                    {
                        double u = pb.frame.ToUV(t.Centre).x;
                        var drive = new[] { t.Centre, pb.frame.FromUV(u, box.maxV + 2.5) };
                        var e = new LotEntrance { a = pb.frame.FromUV(u - w * 0.5, box.maxV + 2.2), b = pb.frame.FromUV(u + w * 0.5, box.maxV + 2.2), requestId = t.request,
                            street = face.street };
                        entrances.Add(e);
                        var lay = LotHelper.Pack(pb, lot, entrances, targets, 3, surface, "rear lot");
                        pb.Surface(PlanBuilder.Strip(drive, w), surface, SurfaceUse.Drive);
                        if (lay == null) pb.plan.notes.Add("rear lot dropped");
                        return;
                    }
                }
            }
            else
            {
                var picks = two ? new[] { lb.minU + 0.25 * (lb.maxU - lb.minU), lb.minU + 0.75 * (lb.maxU - lb.minU) } : new[] { 0.5 * (lb.minU + lb.maxU) };
                foreach (var u in picks)
                {
                    var t = pb.Entrance(face, pb.SAt(u), 7.3, DrivewayKind.Commercial, surface, lo, hi);
                    if (t.ok) entrances.Add(LotHelper.EntranceOnLot(pb, lot, t, 7.3));
                }
            }
            if (entrances.Count == 0) { pb.plan.notes.Add($"{pos} lot has no entrance: dropped"); return; }
            var layout = LotHelper.Pack(pb, lot, entrances, targets, 3, surface, $"{pos} lot");
            foreach (var e in entrances)
            {
                // The drive from the throat to the lot edge (zero length when the lot meets the street).
                var c = (e.a + e.b) * 0.5;
                foreach (var d in pb.plan.driveways)
                    if (d.id == e.requestId && pb.ctx.fronts.TryGetValue(d.faceId, out var df))
                    {
                        var start = df.BackAt(0.5 * (d.s0 + d.s1), out _);
                        if (math.distance(start, c) > 0.5) pb.Surface(PlanBuilder.Strip(new[] { start, c }, 7.3), surface, SurfaceUse.Drive);
                    }
            }
            if (layout == null) pb.plan.notes.Add($"{pos} lot dropped");
        }

        /// <summary>The drive-through lane round the building (S2's lane on its own), the pickup window on the driver's side, the
        /// order board: a site element the lot keeps clear of.</summary>
        static void DriveThrough(PlanBuilder pb, SiteBuilding b)
        {
            var dt = ParkingPacker.DriveThrough(b.poly, -pb.frame.v, pb.ctx.dims);
            var lane = new Paths64();
            foreach (var piece in dt.pieces) lane.AddRange(Clip.Region(piece));
            var kept = pb.Surface(Clip.Union(lane), SiteSurface.Asphalt, SurfaceUse.Drive);
            double share = Clip.Area(kept) / Math.Max(1e-6, Clip.Area(Clip.Union(lane)));
            if (share < 0.9) pb.plan.notes.Add($"the drive-through lane only partly fits the parcel ({100 * share:F0} %)");
            pb.plan.driveThrough = dt;
            pb.Door(b, DoorKind.DriveThroughWindow, dt.window - b.centroid, 1.2, dt.window);
            pb.Prop("menu.board", dt.orderBoard, dt.orderBoard - b.centroid, "design §4.2: order board on the drive-through");
            if (dt.stackCars < pb.ctx.dims.stackMin) pb.plan.notes.Add($"the drive-through stacks {dt.stackCars} cars");
        }

        static void Bays(PlanBuilder pb, SiteBuilding b, (double minU, double maxU, double minV, double maxV) box)
        {
            // Two or three service bays on the street wall with a full-width concrete apron; a fenced gravel yard behind.
            int n = box.maxU - box.minU >= 14 ? 3 : 2;
            for (int i = 0; i < n; i++)
            {
                double u = box.minU + (box.maxU - box.minU) * (i + 1) / (n + 1);
                pb.Door(b, DoorKind.Bay, -pb.frame.v, 3.6, pb.frame.FromUV(u, box.minV));
            }
            pb.Surface(pb.Zone(box.minU, box.maxU, Math.Max(0.3, box.minV - 8), box.minV), SiteSurface.Concrete, SurfaceUse.Apron);
            var pbx = pb.ParcelBox;
            if (pbx.maxV - box.maxV > 8)
            {
                var yard = pb.Zone(box.minU - 4, box.maxU + 4, box.maxV + 1, Math.Min(pbx.maxV, box.maxV + 22));
                var kept = pb.Surface(yard, SiteSurface.Gravel, SurfaceUse.Yard);
                foreach (var piece in Clip.Pieces(kept))
                {
                    var ring = Clip.Pts(piece[0]);
                    pb.plan.fences.Add(new Fence { line = ring.Concat(new[] { ring[0] }).ToArray(), kind = FenceKind.ChainLink, height = 1.8f });
                    break;
                }
                for (int i = 0; i < 3; i++) pb.Prop("tyre.stack", pb.frame.FromUV(box.minU + 2 + i * 1.5, box.maxV + 2.5), pb.frame.u, "design §7: tyre stacks in the yard");
            }
            pb.Prop("jack.stand", pb.frame.FromUV(box.maxU - 1.5, box.minV - 3), pb.frame.u, "design §7: car repair apron");
        }

        static void Rooms(PlanBuilder pb, SiteBuilding b, DoorSpec office)
        {
            // One room door every 4 m along the office's wall (one storey), besides the office door.
            var p0 = b.poly[office.wallEdge];
            var p1 = b.poly[(office.wallEdge + 1) % b.poly.Length];
            double L = math.distance(p0, p1);
            int rooms = Math.Max(0, Math.Min(16, (int)Math.Floor(L / 4.0) - 1));
            for (int i = 0; i < rooms; i++)
            {
                double off = 2.0 + i * 4.0;
                if (Math.Abs(off - office.offset) < 2.0) continue;
                pb.plan.doors.Add(new DoorSpec { building = b.osm, part = 0, kind = DoorKind.Service, wallEdge = office.wallEdge, offset = (float)off, width = 0.9f, sillY = float.NaN });
            }
        }

        static void Dock(PlanBuilder pb, SiteBuilding b, (double minU, double maxU, double minV, double maxV) box)
        {
            // Two dock doors on the rear wall with a concrete apron behind, as deep as the parcel allows (35-40 m [doc]).
            var pbx = pb.ParcelBox;
            double depth = Math.Min(38, pbx.maxV - box.maxV - 1);
            if (depth < 12) { pb.plan.notes.Add("no room for a truck dock behind"); return; }
            double cu = 0.5 * (box.minU + box.maxU);
            pb.Door(b, DoorKind.Dock, pb.frame.v, 3.0, pb.frame.FromUV(cu - 2.0, box.maxV));
            pb.Door(b, DoorKind.Dock, pb.frame.v, 3.0, pb.frame.FromUV(cu + 2.0, box.maxV));
            pb.Surface(pb.Zone(cu - 12, cu + 12, box.maxV + 0.2, box.maxV + depth), SiteSurface.Concrete, SurfaceUse.Apron);
            pb.Model("dock.leveller", pb.frame.Rect(cu - 3.5, cu - 0.5, box.maxV, box.maxV + 2.5), 0.3f, "design §7a: dock levellers (lane A)");
            pb.Model("dock.leveller", pb.frame.Rect(cu + 0.5, cu + 3.5, box.maxV, box.maxV + 2.5), 0.3f, "design §7a: dock levellers (lane A)");
        }
    }

    /// <summary>
    /// Street-front buildings (design §4.2 Downtown): no front lot (the walk fills from kerb to wall, R2); the main door on
    /// the street wall, a service door behind; a rear lot off the alley where one runs behind and the depth holds a
    /// parking row, else a side drive where a 7 m gap exists, else a small service yard at the back door.
    /// </summary>
    public static class DowntownModule
    {
        public static void Plan(PlanBuilder pb, bool hasMappedLot)
        {
            var b = pb.main;
            var box = pb.Box(b);
            var mainDoor = pb.Door(b, DoorKind.Main, -pb.frame.v, 1.8, pb.frame.FromUV(0.5 * (box.minU + box.maxU), box.minV));
            var service = pb.Door(b, DoorKind.Service, pb.frame.v, 1.0, null, 0, mainDoor.wallEdge);
            var (spos, sout) = PlanBuilder.DoorAt(b, service);
            var pbx = pb.ParcelBox;
            double rearDepth = pbx.maxV - box.maxV - 1.0;
            var alleyRef = pb.parcel.frontage.Where(f => pb.ctx.fronts.TryGetValue(f.faceId, out var fl) && fl.alley).ToList();
            var surface = LotHelper.LotSurface(pb, "rear-surface");
            bool done = hasMappedLot;
            if (!done && alleyRef.Count > 0 && rearDepth >= 6)
            {
                var af = pb.ctx.fronts[alleyRef[0].faceId];
                var rear = Clip.Intersect(pb.Zone(pbx.minU - 1, pbx.maxU + 1, box.maxV + 1.0, pbx.maxV + 1), pb.Free(1.0));
                var t = pb.Entrance(af, StreetFrame.SOn(af, spos), 6.0, DrivewayKind.Commercial, surface, alleyRef[0].s0, alleyRef[0].s1);
                if (t.ok && Clip.Area(rear) >= 120)
                {
                    var d = math.normalizesafe(t.inward);
                    var e = new LotEntrance { a = t.Centre + d * 0.3 - Poly2.Perp(d) * 3, b = t.Centre + d * 0.3 + Poly2.Perp(d) * 3, street = af.street, requestId = t.request };
                    var lay = LotHelper.Pack(pb, rear, new List<LotEntrance> { e }, new LotTargets { kerbed = false, paved = surface != SiteSurface.Gravel }, 2, surface, "rear lot off the alley");
                    done = lay != null;
                }
                if (!done && t.ok)
                {
                    // A service yard from the alley to the back door.
                    pb.Surface(PlanBuilder.Strip(new[] { t.Centre, spos + sout * 0.3 }, 4.0), surface, SurfaceUse.Apron);
                    done = true;
                }
            }
            if (!done && rearDepth >= 13)
            {
                var (s0, s1) = LotHelper.SpanAt(pb, Math.Max(0.5, 0.5 * (box.minV + box.maxV)), 0.5 * (box.minU + box.maxU));
                double gapL = box.minU - s0, gapR = s1 - box.maxU;
                if (Math.Max(gapL, gapR) >= 7.0 && pb.frame.face != null)
                {
                    bool left = gapL >= gapR;
                    double ud = left ? box.minU - 1.0 - 2.5 : box.maxU + 1.0 + 2.5;
                    var (lo, hi) = pb.Range(pb.frame.face.faceId);
                    var t = pb.Entrance(pb.frame.face, pb.SAt(ud), 5.0, DrivewayKind.Commercial, surface, lo, hi);
                    if (t.ok)
                    {
                        double u = pb.frame.ToUV(t.Centre).x;
                        var rear = Clip.Intersect(pb.Zone(pbx.minU - 1, pbx.maxU + 1, box.maxV + 1.0, pbx.maxV + 1), pb.Free(1.0));
                        var e = new LotEntrance { a = pb.frame.FromUV(u - 2.5, box.maxV + 1.2), b = pb.frame.FromUV(u + 2.5, box.maxV + 1.2), requestId = t.request, street = pb.frame.face.street };
                        var lay = LotHelper.Pack(pb, rear, new List<LotEntrance> { e }, new LotTargets { kerbed = false, paved = surface != SiteSurface.Gravel }, 2, surface, "rear lot by a side drive");
                        pb.Surface(PlanBuilder.Strip(new[] { t.Centre, pb.frame.FromUV(u, box.maxV + 1.5) }, 5.0), surface, SurfaceUse.Drive);
                        done = lay != null;
                    }
                }
            }
            if (!done && rearDepth >= 3)
            {
                pb.Surface(PlanBuilder.Strip(new[] { spos + sout * 0.2, spos + sout * Math.Min(6, rearDepth) }, 4.0), SiteSurface.Concrete, SurfaceUse.Apron);
            }
            if (rearDepth >= 3) pb.Prop("dumpster", spos + sout * 2.5 + Poly2.Perp(sout) * 2.5, sout, "design §7: dumpster behind every commercial building");
            if (pb.plan.source != PlanSource.Mapped) pb.plan.source = PlanSource.Rules;
            pb.plan.downtown = true;
        }
    }
}
