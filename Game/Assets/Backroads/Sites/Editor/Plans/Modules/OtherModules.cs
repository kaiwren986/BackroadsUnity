using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// Fuel stations (design §4.2): the mapped canopy (else one 7.8 x 16.4 m, its long side along the road, 9 m from it,
    /// for lane A to model), a concrete forecourt 6 m round it and out to the street, pump islands under it (one row on a
    /// canopy up to 9 m deep, rows across a deeper one 7.5 m apart) with dispensers for lane A, two wide aprons (FuelIn
    /// and FuelOut, 10 m) 25-65 m apart or one on each street of a corner, the shop's door facing the canopy, a few
    /// stalls by the shop, the price pylon by the way in, bollards and bins at the islands, air and cans by the shop.
    /// </summary>
    public static class FuelModule
    {
        public static void Plan(PlanBuilder pb)
        {
            var canopyB = pb.buildings.FirstOrDefault(x => x.business == BusinessType.Fuel && x.tags.TryGetValue("building", out var v) && (v == "roof" || v == "canopy"));
            var shop = pb.buildings.FirstOrDefault(x => x != canopyB && !x.Accessory);
            var face = pb.frame.face;
            double2[] canopy;
            if (canopyB != null) canopy = canopyB.poly;
            else
            {
                double cu = shop != null ? 0.5 * (pb.Box(shop).minU + pb.Box(shop).maxU) : 0;
                canopy = pb.frame.Rect(cu - 8.2, cu + 8.2, 9.0, 16.8);
                pb.Model("fuel.canopy", canopy, 5.0f, "design §7a: the canopy at its measured size (lane A)");   // a roof: the forecourt runs under it
            }
            var cbox = pb.frame.Box(canopy);
            var rect = Poly2.MinAreaRect(canopy);
            // Forecourt: 6 m round the canopy and out to the street.
            var fore = Clip.Union(Clip.Inflate(Clip.Region(canopy), 6.0, JoinType.Miter), pb.Zone(cbox.minU - 6, cbox.maxU + 6, 0, cbox.minV));
            pb.Surface(Clip.Intersect(fore, pb.Free(1.0)), SiteSurface.Concrete, SurfaceUse.Forecourt);
            Islands(pb, rect);

            if (face != null)
            {
                var (lo, hi) = pb.Range(face.faceId);
                double uIn = cbox.minU - 4, uOut = cbox.maxU + 4;
                var other = pb.parcel.frontage.FirstOrDefault(f => f.faceId != face.faceId && pb.ctx.fronts.TryGetValue(f.faceId, out var fl) && !fl.alley && f.Length >= 14);
                Throat tIn, tOut = default;
                if (pb.parcel.corner && other.Length >= 14)
                {
                    tIn = pb.Entrance(face, pb.SAt(0.5 * (uIn + uOut)), 10, DrivewayKind.FuelIn, SiteSurface.Concrete, lo, hi);
                    var of = pb.ctx.fronts[other.faceId];
                    tOut = pb.Entrance(of, StreetFrame.SOn(of, Poly2.Centroid(canopy)), 10, DrivewayKind.FuelOut, SiteSurface.Concrete, other.s0, other.s1);
                }
                else if (hi - lo >= 36)
                {
                    tIn = pb.Entrance(face, pb.SAt(uIn), 10, DrivewayKind.FuelIn, SiteSurface.Concrete, lo, hi);
                    tOut = pb.Entrance(face, pb.SAt(uOut), 10, DrivewayKind.FuelOut, SiteSurface.Concrete, lo, hi);
                }
                else tIn = pb.Entrance(face, pb.SAt(0.5 * (uIn + uOut)), 10, DrivewayKind.Commercial, SiteSurface.Concrete, lo, hi);
                foreach (var t in new[] { tIn, tOut })
                {
                    if (!t.ok) continue;
                    var c = Poly2.Centroid(canopy);
                    pb.Surface(PlanBuilder.Strip(new[] { t.Centre, t.Centre + t.inward * Math.Max(2, pb.frame.ToUV(c).y - cbox.maxV + (cbox.maxV - cbox.minV)) }, 10), SiteSurface.Concrete, SurfaceUse.Forecourt);
                }
                if (tIn.ok)
                    pb.Model("fuel.pylon", Rot(tIn.Centre + tIn.inward * 2.0 - Poly2.Perp(tIn.inward) * 8.0, tIn.inward, 1.6, 0.6), 6f, "design §7a: price pylon by the way in (lane A)");
            }
            if (shop != null)
            {
                var door = pb.Door(shop, DoorKind.Main, Poly2.Centroid(canopy) - shop.centroid, 1.8);
                var svc = pb.Door(shop, DoorKind.Service, shop.centroid - Poly2.Centroid(canopy), 1.0, null, 0, door.wallEdge);
                var (dp, dout) = PlanBuilder.DoorAt(shop, door);
                pb.Surface(PlanBuilder.Strip(new[] { dp + dout * 0.2, dp + dout * 2.5 }, 8.0), SiteSurface.Concrete, SurfaceUse.Walk);
                // A few stalls beside the shop (the side with more room), entered from the forecourt.
                var sb = pb.Box(shop);
                Paths64 best = null;
                foreach (var (u0, u1) in new[] { (sb.minU - 15.0, sb.minU - 1.0), (sb.maxU + 1.0, sb.maxU + 15.0) })
                {
                    var z = Clip.Intersect(pb.Zone(u0, u1, sb.minV - 4, sb.maxV), pb.Open);
                    if (best == null || Clip.Area(z) > Clip.Area(best)) best = z;
                }
                if (best != null && Clip.Area(best) >= 150)
                {
                    var e = LotHelper.EntryNear(best, Poly2.Centroid(canopy), 6.0);
                    LotHelper.Pack(pb, best, new List<LotEntrance> { e }, new LotTargets { stalls = 8, kerbed = false }, 2, SiteSurface.Asphalt, "shop stalls");
                }
                pb.Prop("fuel.air", dp + dout * 3 + Poly2.Perp(dout) * 7, dout, "design §7: air by the shop");
                pb.Prop("fuel.cans", dp + dout * 1.2 + Poly2.Perp(dout) * 2.2, dout, "design §7: fuel cans by the shop");
                pb.Prop("water.barrel", dp + dout * 1.2 - Poly2.Perp(dout) * 2.2, dout, "design §7: water barrel by the shop");
                var (sp, so) = PlanBuilder.DoorAt(shop, svc);
                pb.Prop("dumpster", sp + so * 3 + Poly2.Perp(so) * 3, so, "design §7: dumpster behind");
            }
            pb.plan.source = PlanSource.Rules;
        }

        static void Islands(PlanBuilder pb, Poly2.Rect r)
        {
            // Islands 1.2 m wide: one row along a canopy up to 9 m deep, else rows across it 7.5 m apart [doc].
            var islands = new List<(double2 c, double2 axis, double len)>();
            if (2 * r.halfShort <= 9.0) islands.Add((r.centre, r.axis, Math.Max(3, 2 * r.halfLong - 4)));
            else
            {
                int n = Math.Max(1, (int)Math.Floor(2 * r.halfLong / 7.5));
                for (int i = 0; i < n; i++)
                {
                    double t = -r.halfLong + (2 * r.halfLong) * (i + 0.5) / n;
                    islands.Add((r.centre + r.axis * t, r.Short, Math.Max(3, 2 * r.halfShort - 3)));
                }
            }
            foreach (var (c, axis, len) in islands)
            {
                var poly = Rot(c, axis, len, 1.2);
                pb.Model("fuel.island", poly, 0.15f, "design §4.2: pump island under the canopy (lane A)");
                foreach (var t in new[] { -len / 3, len / 3 })
                    pb.Model("fuel.dispenser", Rot(c + axis * t, axis, 0.9, 0.6), 1.8f, "design §7: fuel dispenser (lane A; Synty has none)");
                pb.Prop("bollard", c + axis * (len * 0.5 + 0.6), axis, "design §7: bollard at the island end");
                pb.Prop("bollard", c - axis * (len * 0.5 + 0.6), -axis, "design §7: bollard at the island end");
                pb.Prop("trash.island", c + axis * (len * 0.5 - 0.4), axis, "design §7: bin at the island");
            }
        }

        /// <summary>A rectangle centred on c with its length along axis (CCW).</summary>
        public static double2[] Rot(double2 c, double2 axis, double len, double wid)
        {
            axis = math.normalizesafe(axis, new double2(1, 0));
            var s = Poly2.Perp(axis);
            var a = axis * (len * 0.5);
            var b = s * (wid * 0.5);
            return new[] { c - a - b, c + a - b, c + a + b, c - a + b };
        }
    }

    /// <summary>
    /// Warehouses and industry (design §4.2): dock doors on the long wall facing away from the street (side or rear, the
    /// one with room), a concrete apron 35-40 m deep in front of them, a truck entrance (12 m) and a staff lot with its own
    /// entrance by the office door, trailer stalls (3.7 x 16 m) in the yard, a chain-link fence round the yard with a
    /// sliding gate at the truck drive, a boom gate, barriers, cones, pallets and skips.
    /// </summary>
    public static class IndustrialModule
    {
        public static void Plan(PlanBuilder pb)
        {
            var b = pb.main;
            var box = pb.Box(b);
            var pbx = pb.ParcelBox;
            var face = pb.frame.face;
            // Dock wall: rear when 20 m of room behind, else the side with more room.
            var (s0, s1) = LotHelper.SpanAt(pb, Math.Max(0.5, 0.5 * (box.minV + box.maxV)), 0.5 * (box.minU + box.maxU));
            double rear = pbx.maxV - box.maxV, left = box.minU - s0, right = s1 - box.maxU;
            string dockSide = rear >= 20 ? "rear" : left >= right ? "left" : "right";
            double2 dockDir = dockSide == "rear" ? pb.frame.v : dockSide == "left" ? -pb.frame.u : pb.frame.u;
            double room = dockSide == "rear" ? rear : dockSide == "left" ? left : right;
            var office = pb.Door(b, DoorKind.Main, -pb.frame.v, 1.2);
            var (op, oo) = PlanBuilder.DoorAt(b, office);
            // Dock doors along the dock wall, 4 m apart.
            int edge = -1;
            double best = double.MinValue;
            for (int i = 0; i < b.poly.Length; i++)
            {
                var e = b.poly[(i + 1) % b.poly.Length] - b.poly[i];
                double len = math.length(e);
                var outw = new double2(e.y, -e.x) / Math.Max(len, 1e-9);
                double sc = math.dot(outw, dockDir) * 100 + len;
                if (sc > best) { best = sc; edge = i; }
            }
            var p0 = b.poly[edge];
            var p1 = b.poly[(edge + 1) % b.poly.Length];
            double L = math.distance(p0, p1);
            int n = Math.Max(1, Math.Min(8, (int)Math.Floor((L - 4) / 4.0)));
            var ed = math.normalizesafe(p1 - p0);
            var outward = new double2(ed.y, -ed.x);
            double start = 0.5 * L - 2.0 * (n - 1);
            for (int i = 0; i < n; i++)
            {
                double off = start + 4.0 * i;
                pb.plan.doors.Add(new DoorSpec { building = b.osm, part = 0, kind = DoorKind.Dock, wallEdge = edge, offset = (float)off, width = 3.0f, sillY = float.NaN });
                var dp = p0 + ed * off;
                pb.Model("dock.leveller", FuelModule.Rot(dp + outward * 1.25, outward, 2.5, 2.4), 0.3f, "design §7a: dock leveller (lane A)");
            }
            double depth = Math.Max(12, Math.Min(38, room - 1));
            var mid = (p0 + p1) * 0.5;
            var apron = Clip.Region(FuelModule.Rot(mid + outward * (depth * 0.5 + 0.2), outward, depth, Math.Min(L + 6, n * 4 + 12)));
            pb.Surface(apron, SiteSurface.Concrete, SurfaceUse.Apron);
            pb.Prop("barrier.short", p0 + ed * Math.Max(0.5, start - 2.5) + outward * 1.5, outward, "design §7: barrier at the dock end");
            pb.Prop("barrier.short", p0 + ed * Math.Min(L - 0.5, start + 4.0 * (n - 1) + 2.5) + outward * 1.5, outward, "design §7: barrier at the dock end");

            double2? driveEnd = null;
            if (face != null)
            {
                var (lo, hi) = pb.Range(face.faceId);
                double uTruck = dockSide == "left" ? box.minU - Math.Min(left, 14) * 0.5 : dockSide == "right" ? box.maxU + Math.Min(right, 14) * 0.5 : (left >= right ? box.minU - Math.Min(left, 14) * 0.5 : box.maxU + Math.Min(right, 14) * 0.5);
                var truck = pb.Entrance(face, pb.SAt(uTruck), 12, DrivewayKind.Truck, SiteSurface.Asphalt, lo, hi);
                if (truck.ok)
                {
                    var ut = pb.frame.ToUV(truck.Centre).x;
                    var line = new List<double2> { truck.Centre, pb.frame.FromUV(ut, dockSide == "rear" ? box.maxV + depth * 0.5 : 0.5 * (box.minV + box.maxV)) };
                    pb.Surface(PlanBuilder.Strip(line, 9.0), SiteSurface.Asphalt, SurfaceUse.Drive);
                    driveEnd = line[1];
                    // Fence across the drive 8 m in, with a sliding gate; the yard fence runs round the back of the parcel.
                    var g0 = pb.frame.FromUV(ut - 6, 8);
                    var g1 = pb.frame.FromUV(ut + 6, 8);
                    pb.plan.gates.Add(new Gate { a = g0, b = g1, kind = FenceKind.ChainLink, sliding = true });
                    pb.Prop("boom.gate", pb.frame.FromUV(ut + 6.5, 7.5), -pb.frame.v, "design §7: boom gate at the truck gate");
                    Fence(pb, 8.0, ut - 6, ut + 6);
                }
                // Staff lot by the office door.
                var staff = Clip.Intersect(Clip.Inflate(Clip.Region(FuelModule.Rot(op + oo * 12, pb.frame.u, 34, 22)), 0.0), pb.Free(2.5));
                var tStaff = pb.Entrance(face, pb.SAt(pb.frame.ToUV(op).x), 7.3, DrivewayKind.Commercial, SiteSurface.Asphalt, lo, hi);
                if (tStaff.ok)
                {
                    var e = LotHelper.EntranceOnLot(pb, staff, tStaff, 7.3);
                    LotHelper.Pack(pb, staff, new List<LotEntrance> { e }, new LotTargets { stalls = 20, kerbed = pb.kerbZone }, 3, SiteSurface.Asphalt, "staff lot");
                }
            }
            // Trailer stalls in the yard piece the truck drive reaches, entered from the drive's end; the rest of the yard gravel.
            var yard = Clip.Intersect(pb.Zone(pbx.minU, pbx.maxU, 10, pbx.maxV), pb.Free(2.0));
            var yardPieces = Clip.Pieces(Clip.Intersect(yard, pb.Open));
            if (yardPieces.Count > 0)
            {
                var near = driveEnd ?? Poly2.Centroid(Clip.Pts(yardPieces[0][0]));
                var piece = yardPieces.Where(x => Clip.Area(x) > 600)
                    .OrderBy(x => Poly2.BoundaryDistance(Clip.Pts(x[0]), near, out _)).ThenByDescending(x => Clip.Area(x)).FirstOrDefault();
                if (piece != null)
                {
                    var e = LotHelper.EntryNear(piece, near, 12.0);
                    LotHelper.Pack(pb, piece, new List<LotEntrance> { e }, new LotTargets { stallKind = StallKind.Trailer, paved = false, accessible = false }, 1, SiteSurface.Gravel, "trailer stalls");
                }
                foreach (var y in Clip.Pieces(Clip.Intersect(yard, pb.Open))) pb.Surface(y, SiteSurface.Gravel, SurfaceUse.Yard);
            }
            for (int i = 0; i < 4; i++)
                pb.Prop(i % 2 == 0 ? "pallets" : "skip", (p0 + p1) * 0.5 + outward * (depth + 2) + ed * (-8 + 5 * i), outward, "design §7: pallets and skips in the yard");
            pb.Prop("cone", (p0 + p1) * 0.5 + outward * (depth * 0.5) + ed * (L * 0.5 + 3), outward, "design §7: cones by the apron");
            pb.plan.source = PlanSource.Rules;
        }

        /// <summary>A chain-link fence round the parcel's edge from v = vFrom back (both sides and the rear), with an opening u0..u1 at vFrom.</summary>
        static void Fence(PlanBuilder pb, double vFrom, double u0, double u1)
        {
            var inner = Clip.Pieces(Clip.Intersect(pb.region, pb.Zone(-5000, 5000, vFrom, 5000)));
            if (inner.Count == 0) return;
            var r = Clip.Pts(inner[0][0]);
            // Walk the ring and drop the stretch along v = vFrom between u0 and u1 (the gate).
            var line = new List<double2>();
            int start = 0;
            double bestD = double.MaxValue;
            var gateMid = pb.frame.FromUV(0.5 * (u0 + u1), vFrom);
            for (int i = 0; i < r.Length; i++)
            {
                double d = Poly2.SegDist(gateMid, r[i], r[(i + 1) % r.Length]);
                if (d < bestD) { bestD = d; start = i; }
            }
            // Start at the gate post on the far side, go round, end at the near post.
            var gb = pb.frame.FromUV(u1, vFrom);
            var ga = pb.frame.FromUV(u0, vFrom);
            line.Add(gb);
            for (int k = 1; k <= r.Length; k++) line.Add(r[(start + k) % r.Length]);
            line.Add(ga);
            pb.plan.fences.Add(new Fence { line = line.ToArray(), kind = FenceKind.ChainLink, height = 2.0f });
        }
    }

    /// <summary>Schools add a bus bay and a drop-off lane in front to the civic lot (design §4.2, [doc]).</summary>
    public static class CivicModule
    {
        public static void Plan(PlanBuilder pb, bool hasMappedLot)
        {
            CommercialModule.Plan(pb, hasMappedLot);
            if (pb.parcel.type != BusinessType.School || pb.frame.face == null) return;
            var box = pb.Box(pb.main);
            if (box.minV < 9) { pb.plan.notes.Add("no room for a bus bay in front"); return; }
            double cu = 0.5 * (box.minU + box.maxU);
            // A 3.5 m one-way drop-off lane along the front and a 4 x 15 m bus bay by the street.
            pb.Surface(pb.Zone(cu - 15, cu + 15, 0.5, 4.5), SiteSurface.Asphalt, SurfaceUse.Apron);
            pb.Surface(pb.Zone(box.minU - 4, box.maxU + 4, box.minV - 6.0, box.minV - 2.5), SiteSurface.Asphalt, SurfaceUse.Drive);
            pb.Prop("bench", pb.frame.FromUV(cu, 5.5), -pb.frame.v, "design §4.2: bench at the bus bay");
        }
    }

    /// <summary>
    /// Farms, quarries, mills and harbours (design §4.2): a yard round the buildings (gravel or dirt) reached by a long
    /// drive or haul road from a field or truck cut; per kind the farm's fences, mailbox, grain bins and bales, the
    /// quarry's gate, scale house and paved first 15 m, the mill's apron, log decks or elevator leg, the harbour's trailer
    /// stalls, ramp and piers (models for lane A where Synty has none).
    /// </summary>
    public static class RuralModule
    {
        public static void Plan(PlanBuilder pb)
        {
            var t = pb.parcel.type;
            var b = pb.main;
            var hull = Poly2.ConvexHull(pb.buildings.SelectMany(x => x.poly).ToList()).ToArray();
            double grow = t == BusinessType.Quarry ? 25 : t == BusinessType.Mill ? 20 : 15;
            var yard = Clip.Intersect(Clip.Inflate(Clip.Region(hull), grow), pb.Free(0.5));
            var face = pb.frame.face;
            var main = pb.Door(b, t == BusinessType.Farm ? DoorKind.Main : DoorKind.Bay, -pb.frame.v, t == BusinessType.Farm ? 0.95 : 4.0);
            foreach (var x in pb.buildings.Skip(1).Where(x => !x.Accessory || x.area > 60))
                pb.Door(x, DoorKind.Bay, Poly2.Centroid(hull) - x.centroid, 3.6);
            var box = pb.frame.Box(hull);
            Throat th = default;
            if (face != null)
            {
                var (lo, hi) = pb.Range(face.faceId);
                var kind = t == BusinessType.Farm ? DrivewayKind.Field : DrivewayKind.Truck;
                double w = t == BusinessType.Farm ? 5.0 : t == BusinessType.Harbor ? 7.3 : 12.0;
                th = pb.Entrance(face, pb.SAt(0.5 * (box.minU + box.maxU)), w, kind, SiteSurface.Gravel, lo, hi);
            }
            if (t == BusinessType.Harbor) HarborTrailers(pb, box);
            pb.Surface(yard, pb.Rng("yard").Value() < 0.7 ? SiteSurface.Gravel : SiteSurface.Dirt, SurfaceUse.Yard);
            if (th.ok)
            {
                double u = pb.frame.ToUV(th.Centre).x;
                var drive = new[] { th.Centre, pb.frame.FromUV(u, Math.Max(2, box.minV - grow + 1)) };
                double dw = t == BusinessType.Quarry ? 9.0 : t == BusinessType.Farm ? 4.0 : 7.0;
                if (t == BusinessType.Quarry)
                {
                    // The first 15 m paved, then the gravel haul road; gate and scale house by it.
                    var mid = th.Centre + pb.frame.v * Math.Min(15, math.distance(drive[0], drive[1]));
                    pb.Surface(PlanBuilder.Strip(new[] { drive[0], mid }, dw), SiteSurface.Asphalt, SurfaceUse.Apron);
                    pb.Surface(PlanBuilder.Strip(new[] { mid, drive[1] }, dw), SiteSurface.Gravel, SurfaceUse.Drive);
                    pb.plan.gates.Add(new Gate { a = pb.frame.FromUV(u - dw * 0.5, 20), b = pb.frame.FromUV(u + dw * 0.5, 20), kind = FenceKind.ChainLink });
                    pb.Prop("boom.gate", pb.frame.FromUV(u + dw * 0.5 + 0.5, 19.5), -pb.frame.v, "design §7: gate at the haul road");
                    pb.Model("quarry.scale", pb.frame.Rect(u + dw * 0.5 + 3, u + dw * 0.5 + 6, 24, 28), 3f, "design §7: scale house by the haul road (lane A)");
                    pb.Prop("portaloo", pb.frame.FromUV(u + dw * 0.5 + 8, 26), pb.frame.u, "design §7: quarry");
                    for (int i = 0; i < 3; i++) pb.Prop("cone", pb.frame.FromUV(u - dw * 0.5 - 1, 22 + 3 * i), pb.frame.u, "design §7: cones at the gate");
                }
                else pb.Surface(PlanBuilder.Strip(drive, dw), SiteSurface.Gravel, SurfaceUse.Drive);
                if (t == BusinessType.Farm) pb.Prop("mailbox", pb.frame.FromUV(u + 4, 1.2), -pb.frame.v, "design §7: mailbox at the road");
            }
            var c = Poly2.Centroid(hull);
            switch (t)
            {
                case BusinessType.Farm:
                    pb.Model("farm.grainbin", Circle(c + pb.frame.u * (box.maxU - 0.5 * (box.minU + box.maxU) + 8), 3.0), 9f, "design §7: grain bin (lane A)");
                    pb.Model("farm.grainbin", Circle(c + pb.frame.u * (box.maxU - 0.5 * (box.minU + box.maxU) + 15), 3.0), 9f, "design §7: grain bin (lane A)");
                    pb.Model("farm.haybales", FuelModule.Rot(c - pb.frame.u * (0.5 * (box.maxU - box.minU) + 8), pb.frame.u, 6, 3), 2f, "design §7: hay bales (lane A)");
                    pb.Prop("barrel.wood", c + pb.frame.v * (0.5 * (box.maxV - box.minV) + 4), pb.frame.u, "design §7: farm");
                    pb.Prop("sack.stack", c + pb.frame.v * (0.5 * (box.maxV - box.minV) + 4) + pb.frame.u * 2, pb.frame.u, "design §7: farm");
                    // Paddock fence along the parcel's back half.
                    var back = Clip.Pieces(Clip.Intersect(pb.region, pb.Zone(-5000, 5000, box.maxV + grow, 5000)));
                    if (back.Count > 0)
                    {
                        var ring = Clip.Pts(back[0][0]);
                        pb.plan.fences.Add(new Fence { line = ring.Concat(new[] { ring[0] }).ToArray(), kind = FenceKind.Farm, height = 1.2f });
                    }
                    break;
                case BusinessType.Mill:
                    bool saw = b.tags.TryGetValue("craft", out var cr) && cr == "sawmill";
                    pb.Surface(Clip.Intersect(Clip.Inflate(Clip.Region(b.poly), 8, JoinType.Miter), pb.Free(0.3)), SiteSurface.Concrete, SurfaceUse.Apron);
                    if (saw)
                    {
                        for (int i = 0; i < 4; i++) pb.Prop("log", c + pb.frame.u * (12 + i * 2.5) + pb.frame.v * 6, pb.frame.v, "design §7: log deck");
                        for (int i = 0; i < 3; i++) pb.Prop("plank.stack", c - pb.frame.u * (12 + i * 3) + pb.frame.v * 6, pb.frame.u, "design §7: lumber stacks");
                        pb.Model("mill.kiln", FuelModule.Rot(c + pb.frame.v * 22, pb.frame.u, 10, 6), 5f, "design §7: kiln (lane A)");
                    }
                    else
                    {
                        pb.Model("mill.elevatorLeg", Circle(c + pb.frame.u * 8, 1.5), 30f, "design §7: elevator leg (lane A)");
                        pb.Model("mill.truckScale", FuelModule.Rot(c - pb.frame.v * 14, pb.frame.u, 20, 3.5), 0.3f, "design §4.2: truck scale at the pit shed (lane A)");
                    }
                    break;
                case BusinessType.Harbor:
                {
                    var pbx = pb.ParcelBox;
                    pb.Model("harbor.ramp", pb.frame.Rect(-3, 3, pbx.maxV - 8, pbx.maxV + 6), 0.5f, "design §7: concrete boat ramp (lane A)");
                    pb.Model("harbor.pier", pb.frame.Rect(10, 13, pbx.maxV - 2, pbx.maxV + 40), 1f, "design §7: timber pier (lane A)");
                    pb.Model("harbor.pier", pb.frame.Rect(-16, -13, pbx.maxV - 2, pbx.maxV + 40), 1f, "design §7: timber pier (lane A)");
                    pb.Prop("bench", pb.frame.FromUV(8, pbx.maxV - 6), -pb.frame.v, "design §7: harbour");
                    pb.Prop("sign.warning", pb.frame.FromUV(-5, pbx.maxV - 9), -pb.frame.v, "design §7: harbour");
                    break;
                }
                case BusinessType.Quarry:
                    pb.Model("quarry.crusher", FuelModule.Rot(c + pb.frame.u * 20, pb.frame.u, 12, 8), 8f, "design §7: crusher (lane A)");
                    for (int i = 0; i < 4; i++) pb.Prop("barrier", c - pb.frame.u * (18 + 2.5 * i) + pb.frame.v * 10, pb.frame.u, "design §7: barriers");
                    break;
            }
            pb.plan.source = PlanSource.Rules;
        }

        /// <summary>The harbour's trailer stalls (3.6 x 12 m on a 12 m aisle [doc]) behind its buildings, before the yard.</summary>
        static void HarborTrailers(PlanBuilder pb, (double minU, double maxU, double minV, double maxV) box)
        {
            var pbx = pb.ParcelBox;
            var trailers = Clip.Intersect(pb.Zone(pbx.minU, pbx.maxU, box.maxV + 4, pbx.maxV - 8), pb.Free(2));
            var dims = new ParkingDims { trailerWidth = 3.6, trailerLength = 12, trailerAisle = 12 };
            var pieces = Clip.Pieces(Clip.Intersect(trailers, pb.Open));
            if (pieces.Count == 0 || Clip.Area(pieces[0]) <= 400) { pb.plan.notes.Add("no room for trailer parking"); return; }
            var y = Clip.Pts(pieces[0][0]);
            var yb = pb.frame.Box(y);
            var input = new LotInput
            {
                lot = y, frontageDir = pb.frame.u, toStreet = -pb.frame.v, seed = pb.seed,
                targets = new LotTargets { stallKind = StallKind.Trailer, paved = false, accessible = false },
            };
            input.entrances.Add(LotHelper.EntryNear(pieces[0], pb.frame.FromUV(0.5 * (yb.minU + yb.maxU), yb.minV), 8.0));
            var lay = ParkingPacker.Pack(input, dims);
            if (lay.stalls.Count > 0) { pb.Surface(pieces[0], SiteSurface.Gravel, SurfaceUse.Lot); pb.plan.lots.Add(lay); }
            else pb.plan.notes.Add("trailer parking: no stall fits");
        }

        static double2[] Circle(double2 c, double r)
        {
            var p = new double2[16];
            for (int i = 0; i < 16; i++) { double a = i * Math.PI / 8; p[i] = c + new double2(Math.Cos(a), Math.Sin(a)) * r; }
            return p;
        }
    }
}
