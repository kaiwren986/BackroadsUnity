using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites
{
    // The dressing of the winning layout (card sites-S2 steps 3-6): accessible stalls by the 2010 ADA table nearest the
    // main door, islands, lamp spots, kerbs, wheel stops, markings, stop bars, the fire lane and the drive-through lane.
    sealed partial class ParkingSearch
    {
        void Dress(Plan plan)
        {
            var f = plan.f;
            foreach (var l in plan.lanes) m_Out.lanes.Add(MakeLane(f, l.u0, l.u1, l.v0, l.v1, l.flow, l.oneWay, l.kind));
            if (m_T.accessible && !m_Trailer) Accessible(plan);
            EmitRuns(plan);
            Lamps(plan);
            Kerbs();
            WheelStops();
            OneWayArrows();
            Entrances();
            FireLaneAndDriveThrough();
            Report();
        }

        void FinishEmpty()
        {
            Kerbs();
            Entrances();
            FireLaneAndDriveThrough();
            Report();
        }

        static double2 W(Frame f, double2 uv) => Poly2.FromFrame(uv, f.d);

        static double2[] W(Frame f, double2[] uv)
        {
            var r = new double2[uv.Length];
            for (int i = 0; i < uv.Length; i++) r[i] = Poly2.FromFrame(uv[i], f.d);
            return r;
        }

        static LotLane MakeLane(Frame f, double u0, double u1, double v0, double v1, int flow, bool oneWay, LaneKind kind)
        {
            var poly = W(f, new[] { new double2(u0, v0), new double2(u1, v0), new double2(u1, v1), new double2(u0, v1) });
            double2 a, b;
            float width;
            if (kind == LaneKind.Spine)
            {
                double um = (u0 + u1) * 0.5;
                a = W(f, new double2(um, v0)); b = W(f, new double2(um, v1));
                width = (float)(u1 - u0);
            }
            else
            {
                double vm = (v0 + v1) * 0.5;
                a = W(f, new double2(u0, vm)); b = W(f, new double2(u1, vm));
                if (flow < 0) { var t = a; a = b; b = t; }
                width = (float)(v1 - v0);
            }
            return new LotLane { kind = kind, a = a, b = b, width = width, oneWay = oneWay, poly = poly };
        }

        // ------------------------------------------------------------------------------------------ accessible stalls
        List<StallKind> AccessibleKinds(int n)
        {
            var l = new List<StallKind>(n);
            int v = Ada.RequiredVan(n);
            for (int i = 0; i < n; i++) l.Add(i < v ? StallKind.Van : StallKind.Accessible);
            return l;
        }

        /// <summary>Width (across the stalls) of kinds[from .. from + count): pairs share one access aisle (ADA 502.3.4).</summary>
        double GroupWidth(List<StallKind> kinds, int from, int count)
        {
            double w = 0;
            for (int i = 0; i < count; i += 2)
            {
                bool pair = i + 1 < count;
                bool van = kinds[from + i] == StallKind.Van || (pair && kinds[from + i + 1] == StallKind.Van);
                w += (pair ? 2 : 1) * m_D.accWidth + (van ? m_D.vanAisle : m_D.accAisle);
            }
            return w;
        }

        void Accessible(Plan plan)
        {
            var g = plan.g;
            int total = 0;
            foreach (var r in plan.runs) foreach (var it in r.items) if (it.kind == ItemKind.Stall) total++;
            if (total == 0) return;
            // The smallest count that meets the table for the lot's total after the conversion.
            int n = 1;
            for (; n < total; n++)
            {
                int k = (int)Math.Ceiling(GroupWidth(AccessibleKinds(n), 0, n) / g.w - 1e-9);
                if (n >= Ada.Required(total - k + n)) break;
            }
            var kinds = AccessibleKinds(n);
            int idx = 0, guard = 0;
            while (idx < kinds.Count && guard++ < 64)
            {
                StallRun bestRun = null;
                int bestStart = -1, bestK = 0, bestM = 0;
                double bestDist = double.MaxValue;
                foreach (var r in plan.runs)
                {
                    double pu = g.pu;
                    double sin = r.angle >= 89.9 ? 1 : Math.Sin(r.angle * Math.PI / 180);
                    int i = 0;
                    while (i < r.items.Count)
                    {
                        if (r.items[i].kind != ItemKind.Stall || r.items[i].stall != StallKind.Standard) { i++; continue; }
                        int j = i;
                        while (j < r.items.Count && r.items[j].kind == ItemKind.Stall && r.items[j].stall == StallKind.Standard) j++;
                        int len = j - i;
                        int m = 0, k = 0;
                        for (int mm = Math.Min(kinds.Count - idx, len); mm >= 1; mm--)
                        {
                            int kk = (int)Math.Ceiling(GroupWidth(kinds, idx, mm) / g.w - 1e-9);
                            if (kk <= len) { m = mm; k = kk; break; }
                        }
                        if (m > 0)
                        {
                            for (int s = i; s + k <= j; s++)
                            {
                                double u = ItemU(r, s) + k * pu * 0.5;
                                var c = W(plan.f, new double2(u, r.FrontV));
                                double d = math.distance(c, m_Door) - 0.01 * m;
                                if (d < bestDist - 1e-9) { bestDist = d; bestRun = r; bestStart = s; bestK = k; bestM = m; }
                            }
                        }
                        i = j;
                    }
                }
                if (bestRun == null)
                {
                    m_Out.report.notes.Add($"accessible: no room for {kinds.Count - idx} of {kinds.Count} stalls");
                    break;
                }
                // Replace bestK standard stalls with the group; spare width goes to the access aisles.
                double sinR = bestRun.angle >= 89.9 ? 1 : Math.Sin(bestRun.angle * Math.PI / 180);
                var group = new List<Item>();
                int aisles = 0;
                for (int q = 0; q < bestM; q += 2)
                {
                    bool pair = q + 1 < bestM;
                    var k1 = kinds[idx + q];
                    var k2 = pair ? kinds[idx + q + 1] : StallKind.Standard;
                    bool van = k1 == StallKind.Van || k2 == StallKind.Van;
                    group.Add(new Item { kind = ItemKind.Stall, w = m_D.accWidth / sinR, stall = k1 });
                    group.Add(new Item { kind = ItemKind.AccessAisle, w = (van ? m_D.vanAisle : m_D.accAisle) / sinR });
                    aisles++;
                    if (pair) group.Add(new Item { kind = ItemKind.Stall, w = m_D.accWidth / sinR, stall = k2 });
                }
                double spare = bestK * g.pu - GroupWidth(kinds, idx, bestM) / sinR;
                if (spare > 1e-9 && aisles > 0)
                    for (int q = 0; q < group.Count; q++)
                        if (group[q].kind == ItemKind.AccessAisle) { var it = group[q]; it.w += spare / aisles; group[q] = it; }
                bestRun.items.RemoveRange(bestStart, bestK);
                bestRun.items.InsertRange(bestStart, group);
                idx += bestM;
            }
        }

        static double ItemU(StallRun r, int index)
        {
            double u = r.u0;
            for (int i = 0; i < index; i++) u += r.items[i].w;
            return u;
        }

        // ------------------------------------------------------------------------------------------ stalls, islands, lines
        void EmitRuns(Plan plan)
        {
            var f = plan.f;
            bool kerbed = m_T.kerbed && m_T.paved;
            for (int ri = 0; ri < plan.runs.Count; ri++)
            {
                var r = plan.runs[ri];
                double u = r.u0;
                // End islands against drive lanes.
                if (r.islandA && r.ColStart - r.ra > 0.3) AddIsland(f, IslandKind.End, Rect(r.ra, r.ColStart, r.v0, r.v1), kerbed);
                if (r.islandB && r.rb - r.ColEnd > 0.3) AddIsland(f, IslandKind.End, Rect(r.ColEnd, r.rb, r.v0, r.v1), kerbed);
                for (int i = 0; i < r.items.Count; i++)
                {
                    var it = r.items[i];
                    var q = W(f, r.Quad(u, it.w));
                    var fr = (q[r.front > 0 ? 2 : 0] + q[r.front > 0 ? 3 : 1]) * 0.5;
                    var bk = (q[r.front > 0 ? 0 : 2] + q[r.front > 0 ? 1 : 3]) * 0.5;
                    switch (it.kind)
                    {
                        case ItemKind.Stall:
                            m_Out.stalls.Add(new Stall { poly = q, kind = it.stall, front = fr, back = bk, angle = (float)r.angle, lane = r.lane, run = ri });
                            break;
                        case ItemKind.Island:
                            AddIsland(f, IslandKind.Intermediate, null, kerbed, q);
                            break;
                        case ItemKind.AccessAisle:
                            if (m_T.paved) m_Out.hatches.Add(new MarkHatch { poly = q, colour = MarkColour.Blue, spacing = 0.6f, angleDeg = 45 });
                            var dir = math.normalize(bk - fr);
                            m_Out.ramps.Add(new KerbRamp { pos = bk, dir = dir, width = (float)(it.w * (r.angle >= 89.9 ? 1 : Math.Sin(r.angle * Math.PI / 180))), stall = m_Out.stalls.Count - 1 });
                            break;
                    }
                    if (it.kind == ItemKind.Stall && it.stall != StallKind.Standard && it.stall != StallKind.Trailer)
                    {
                        var dir = math.normalize(bk - fr);
                        var centre = (fr + bk) * 0.5 + dir * (r.D * 0.15);
                        if (m_T.paved) m_Out.signs.Add(new MarkSign { symbol = MarkSymbol.Accessible, pos = centre, yaw = (float)Poly2.Yaw(-dir), size = 1.5f, colour = MarkColour.Blue });
                        m_Out.props.Add(new LotProp
                        {
                            key = it.stall == StallKind.Van ? "sign.vanAccessible" : "sign.accessible", pos = bk + dir * 0.3, yaw = (float)Poly2.Yaw(-dir),
                            reason = "ADA 216.5: accessible stall sign at the stall head",
                        });
                    }
                    u += it.w;
                }
                // Stall lines: every item boundary that touches a stall or an access aisle.
                if (m_T.paved)
                {
                    u = r.u0;
                    for (int i = 0; i <= r.items.Count; i++)
                    {
                        bool left = i > 0 && r.items[i - 1].kind != ItemKind.Island;
                        bool right = i < r.items.Count && r.items[i].kind != ItemKind.Island;
                        if (left || right)
                        {
                            bool blue = (i > 0 && IsAcc(r.items[i - 1])) || (i < r.items.Count && IsAcc(r.items[i]));
                            var a = W(f, new double2(u, r.FrontV));
                            var b = W(f, new double2(u + r.Lean, r.BackV));
                            m_Out.lines.Add(new MarkLine { a = a, b = b, width = (float)m_D.lineWidth, colour = blue ? MarkColour.Blue : MarkColour.White });
                        }
                        if (i < r.items.Count) u += r.items[i].w;
                    }
                }
            }
        }

        static bool IsAcc(Item it) => it.kind == ItemKind.AccessAisle || (it.kind == ItemKind.Stall && (it.stall == StallKind.Accessible || it.stall == StallKind.Van));

        static double2[] Rect(double u0, double u1, double v0, double v1) =>
            new[] { new double2(u0, v0), new double2(u1, v0), new double2(u1, v1), new double2(u0, v1) };

        void AddIsland(Frame f, IslandKind kind, double2[] uv, bool kerbed, double2[] world = null)
        {
            var poly = world ?? W(f, uv);
            m_Out.islands.Add(new Island { kind = kind, poly = poly, kerbed = kerbed });
            if (m_T.paved && !kerbed) m_Out.hatches.Add(new MarkHatch { poly = poly, colour = MarkColour.White, spacing = 0.6f, angleDeg = 45 });
        }

        // ------------------------------------------------------------------------------------------ lamps
        void Lamps(Plan plan)
        {
            var lamps = m_Out.lamps;
            foreach (var isl in m_Out.islands)
            {
                var c = Poly2.Centroid(isl.poly);
                if (NearestLamp(c) >= m_D.lampMin - 1e-9) lamps.Add(c);
            }
            // Rows without islands close enough: poles on the head line at stall-line points, no stretch of a row farther
            // than lampMax / 2 from a pole, poles at least lampMin apart.
            double reach = m_D.lampMax * 0.5;
            foreach (var r in plan.runs)
            {
                var bounds = new List<double>();
                double u = r.u0;
                bounds.Add(u + r.Lean);
                foreach (var it in r.items) { u += it.w; bounds.Add(u + r.Lean); }
                double h0 = r.u0 + r.Lean, h1 = r.End + r.Lean;
                var p0 = W(plan.f, new double2(h0, r.BackV));
                var p1 = W(plan.f, new double2(h1, r.BackV));
                double L = math.distance(p0, p1);
                if (L < 1e-6) continue;
                for (double t = 0; t <= L + 1e-9; t += 0.5)
                {
                    var p = math.lerp(p0, p1, t / L);
                    if (NearestLamp(p) <= reach) continue;
                    double placed = -1;
                    for (double t2 = Math.Min(t + 0.9 * reach, L); t2 >= t - 1e-9; t2 -= 0.5)
                    {
                        // Snap to the nearest stall-line point on the head line.
                        double ub = SnapBound(bounds, h0 + (h1 - h0) * (t2 / L));
                        var q = W(plan.f, new double2(ub, r.BackV));
                        if (NearestLamp(q) >= m_D.lampMin - 1e-9) { lamps.Add(q); placed = t2; break; }
                    }
                    if (placed < 0) lamps.Add(p);
                }
            }
        }

        static double SnapBound(List<double> bounds, double u)
        {
            double best = u, bd = double.MaxValue;
            foreach (var b in bounds) { double d = Math.Abs(b - u); if (d < bd) { bd = d; best = b; } }
            return best;
        }

        double NearestLamp(double2 p)
        {
            double best = double.MaxValue;
            foreach (var q in m_Out.lamps) best = Math.Min(best, math.distance(p, q));
            return best;
        }

        // ------------------------------------------------------------------------------------------ kerbs
        void Kerbs()
        {
            bool kerbed = m_T.kerbed && m_T.paved;
            if (!kerbed) return;
            int n = m_Lot.Length;
            var s = new double[n + 1];
            for (int i = 0; i < n; i++) s[i + 1] = s[i] + math.distance(m_Lot[i], m_Lot[(i + 1) % n]);
            double total = s[n];
            // Throats as arc-length intervals (the shorter way round).
            var gaps = new List<(double a, double b)>();
            foreach (var e in m_Ents)
            {
                double sa = ArcOf(e.a, s), sb = ArcOf(e.b, s);
                if (sa > sb) { var t = sa; sa = sb; sb = t; }
                if (sb - sa <= total * 0.5) gaps.Add((sa, sb));
                else { gaps.Add((sb, total)); gaps.Add((0, sa)); }
            }
            var segs = new List<(double2 p, double2 q)>();
            for (int i = 0; i < n; i++)
            {
                if (m_Kinds[i] == LotEdgeKind.Building || m_Kinds[i] == LotEdgeKind.Open) continue;
                double a = s[i], b = s[i + 1];
                var cuts = new List<(double, double)> { (a, b) };
                foreach (var gp in gaps)
                {
                    var next = new List<(double, double)>();
                    foreach (var (x0, x1) in cuts)
                    {
                        if (gp.b <= x0 || gp.a >= x1) { next.Add((x0, x1)); continue; }
                        if (gp.a > x0 + 1e-6) next.Add((x0, gp.a));
                        if (gp.b < x1 - 1e-6) next.Add((gp.b, x1));
                    }
                    cuts = next;
                }
                foreach (var (x0, x1) in cuts)
                {
                    if (x1 - x0 < 0.05) continue;
                    segs.Add((PointAt(i, x0 - a, s), PointAt(i, x1 - a, s)));
                }
            }
            // Chain the pieces into polylines; one closed loop when nothing interrupts the kerb.
            var lines = new List<List<double2>>();
            foreach (var (p, q) in segs)
            {
                if (lines.Count > 0)
                {
                    var last = lines[lines.Count - 1];
                    if (math.distancesq(last[last.Count - 1], p) < 1e-10) { last.Add(q); continue; }
                }
                lines.Add(new List<double2> { p, q });
            }
            bool closed = false;
            if (lines.Count > 1)
            {
                var first = lines[0];
                var last = lines[lines.Count - 1];
                if (math.distancesq(last[last.Count - 1], first[0]) < 1e-10)
                {
                    last.RemoveAt(last.Count - 1);
                    last.AddRange(first);
                    lines.RemoveAt(0);
                }
            }
            else if (lines.Count == 1)
            {
                var l = lines[0];
                if (math.distancesq(l[l.Count - 1], l[0]) < 1e-10) { l.RemoveAt(l.Count - 1); closed = true; }
            }
            foreach (var l in lines) m_Out.kerbs.Add(new LotKerb { line = l.ToArray(), closed = closed, kind = KerbKind.Barrier150 });
            foreach (var isl in m_Out.islands)
                if (isl.kerbed) m_Out.kerbs.Add(new LotKerb { line = isl.poly, closed = true, kind = KerbKind.Barrier150 });
        }

        double ArcOf(double2 p, double[] s)
        {
            int n = m_Lot.Length;
            double best = double.MaxValue, arc = 0;
            for (int i = 0; i < n; i++)
            {
                var a = m_Lot[i];
                var b = m_Lot[(i + 1) % n];
                var ab = b - a;
                double l2 = math.dot(ab, ab);
                double t = l2 > 0 ? math.clamp(math.dot(p - a, ab) / l2, 0, 1) : 0;
                double d = math.distance(p, a + ab * t);
                if (d < best - 1e-9) { best = d; arc = s[i] + t * math.sqrt(l2); }
            }
            return arc;
        }

        double2 PointAt(int edge, double along, double[] s)
        {
            var a = m_Lot[edge];
            var b = m_Lot[(edge + 1) % m_Lot.Length];
            double len = s[edge + 1] - s[edge];
            return len > 0 ? a + (b - a) * (along / len) : a;
        }

        // ------------------------------------------------------------------------------------------ wheel stops
        void WheelStops()
        {
            bool kerbed = m_T.kerbed && m_T.paved;
            for (int i = 0; i < m_Out.stalls.Count; i++)
            {
                var st = m_Out.stalls[i];
                if (st.kind == StallKind.Trailer) continue;
                var dir = math.normalize(st.back - st.front);
                var h = st.back;
                bool stop = false;
                if (m_Bldg != null && (Poly2.BoundaryDistance(m_Bldg, h, out _) <= 2.5 || Poly2.Contains(m_Bldg, h + dir))) stop = true;
                if (!stop && Poly2.BoundaryDistance(m_Lot, h, out int edge) <= 1.5)
                {
                    var k = m_Kinds[edge];
                    stop = k == LotEdgeKind.Walk || k == LotEdgeKind.Building || !kerbed;
                }
                if (!stop) continue;
                var c = h - dir * m_D.wheelStopInset;
                var side = Poly2.Perp(dir) * (m_D.wheelStopLength * 0.5);
                m_Out.wheelStops.Add(new WheelStop { a = c - side, b = c + side, stall = i });
            }
        }

        // ------------------------------------------------------------------------------------------ arrows, entrances
        void OneWayArrows()
        {
            if (!m_T.paved) return;
            foreach (var l in m_Out.lanes)
            {
                if (!l.oneWay || l.kind != LaneKind.Aisle) continue;
                double L = math.distance(l.a, l.b);
                if (L < 6) continue;
                var dir = (l.b - l.a) / L;
                for (double t = 5; t <= L - 3 + 1e-9; t += 15)
                    m_Out.signs.Add(new MarkSign { symbol = MarkSymbol.Arrow, pos = l.a + dir * t, yaw = (float)Poly2.Yaw(dir), size = 3f, colour = MarkColour.White });
            }
        }

        void Entrances()
        {
            for (int i = 0; i < m_Ents.Count; i++)
            {
                var e = m_Ents[i];
                var n = m_EntIn[i];
                var mid = e.Mid;
                double w = e.Width;
                // Stop bar on exits onto MainStreet and Highway: across the outbound half (the whole throat on an exit
                // only), 1 m inside the lot.
                if (m_T.paved && e.flow != EntranceFlow.EntryOnly && (e.street == StreetClass.MainStreet || e.street == StreetClass.Highway))
                {
                    var outDir = -n;
                    var right = new double2(outDir.y, -outDir.x);
                    var p0 = mid + n * 1.0;
                    double2 a = e.flow == EntranceFlow.ExitOnly ? p0 - right * (w * 0.5) : p0;
                    double2 b = p0 + right * (w * 0.5);
                    m_Out.lines.Add(new MarkLine { a = a, b = b, width = (float)m_D.stopBarWidth, colour = MarkColour.White });
                }
                // A parking sign beside the entrance (on the entering driver's right), entry / exit signs on lots with two.
                var enterRight = new double2(n.y, -n.x);
                string key = m_Ents.Count >= 2 && e.flow == EntranceFlow.EntryOnly ? "sign.entrance" : m_Ents.Count >= 2 && e.flow == EntranceFlow.ExitOnly ? "sign.exit" : "sign.parking";
                m_Out.props.Add(new LotProp
                {
                    key = key, pos = mid + enterRight * (w * 0.5 + 1.0) + n * 1.0, yaw = (float)Poly2.Yaw(-n),
                    reason = "design §7: sign at the entrance",
                });
            }
        }

        // ------------------------------------------------------------------------------------------ fire lane, drive-through
        double2[] m_FireLane;
        double2 m_FireA, m_FireB;
        DriveThroughLane m_Dt;

        void MakeFireLane(LotReport rep)
        {
            if (m_Bldg == null) { rep.notes.Add("fire lane asked for without a building"); return; }
            var r = m_BldgRect;
            var c = Poly2.Centroid(m_Lot);
            var sides = new[] { (r.axis, r.halfLong, r.halfShort), (-r.axis, r.halfLong, r.halfShort), (r.Short, r.halfShort, r.halfLong), (-r.Short, r.halfShort, r.halfLong) };
            double best = double.MinValue;
            double2 nrm = 0;
            double he = 0, hl = 0;
            foreach (var (sn, sh, sl) in sides)
            {
                double score = math.dot(c - r.centre, sn) + (2 * sl >= 15 ? 1e6 : 0);
                if (score > best + 1e-9) { best = score; nrm = sn; he = sh; hl = sl; }
            }
            var fc = r.centre + nrm * he;
            var along = Poly2.Perp(nrm);
            double c0 = m_D.buildingClear, c1 = m_D.buildingClear + m_D.fireLaneWidth;
            m_FireLane = Poly2.Ccw(new[] { fc + nrm * c0 - along * hl, fc + nrm * c0 + along * hl, fc + nrm * c1 + along * hl, fc + nrm * c1 - along * hl });
            m_FireA = fc + nrm * c0 - along * hl;
            m_FireB = fc + nrm * c0 + along * hl;
            m_StallObs.Add(new Obs { poly = m_FireLane, clear = 0 });
        }

        void MakeDriveThrough(LotReport rep)
        {
            if (m_Bldg == null) { rep.notes.Add("drive-through asked for without a building"); return; }
            var r = m_BldgRect;
            double width = m_D.driveThroughWidth, off = 1.0 + width * 0.5;
            var rr = r;
            rr.halfLong += off;
            rr.halfShort += off;
            var cs = rr.Corners();
            var toStreet = m_In.toStreet;
            if (math.lengthsq(toStreet) < 1e-12) toStreet = m_Ents.Count > 0 ? m_Ents[0].Mid - r.centre : new double2(0, -1);
            int rear = 0;
            double bestDot = double.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                var e = cs[(k + 1) % 4] - cs[k];
                var outward = math.normalize(new double2(e.y, -e.x));
                double d = math.dot(outward, toStreet);
                if (d < bestDot - 1e-9) { bestDot = d; rear = k; }
            }
            int prev = (rear + 3) % 4, win = (rear + 1) % 4;
            double Len(int k) => math.distance(cs[k], cs[(k + 1) % 4]);
            double2 Dir(int k) => math.normalize(cs[(k + 1) % 4] - cs[k]);
            double need = 0.5 * (m_D.stackMin + m_D.stackMax) * m_D.carSpacing;
            double winAt = 0.6 * Len(win);
            double have = Len(rear) + winAt;
            double x = math.clamp(need - have, 0, Len(prev));
            var path = new List<double2>();
            if (x > 1e-6) path.Add(cs[rear] - Dir(prev) * x);
            path.Add(cs[rear]);
            path.Add(cs[win]);
            var exitCorner = cs[(win + 1) % 4];
            path.Add(exitCorner);
            path.Add(exitCorner + Dir(win) * 4.0);
            var dt = new DriveThroughLane { path = path.ToArray(), width = (float)width, stackCars = (int)Math.Floor((x + have) / m_D.carSpacing + 1e-9) };
            var winOut = math.normalize(new double2(Dir(win).y, -Dir(win).x));
            dt.window = cs[win] + Dir(win) * winAt - winOut * off;
            double boardAt = x + have - 3 * m_D.carSpacing;
            dt.orderBoard = AlongPath(path, boardAt, out var boardDir) + Poly2.Perp(boardDir) * (width * 0.5 + 0.5);
            for (int i = 0; i + 1 < path.Count; i++)
            {
                var a = path[i];
                var b = path[i + 1];
                var d = math.normalize(b - a);
                var nn = Poly2.Perp(d) * (width * 0.5);
                var a2 = a - d * (width * 0.5);
                var b2 = b + d * (width * 0.5);
                var piece = Poly2.Ccw(new[] { a2 - nn, b2 - nn, b2 + nn, a2 + nn });
                dt.pieces.Add(piece);
                m_StallObs.Add(new Obs { poly = piece, clear = 0.3 });
                m_LaneObs.Add(new Obs { poly = piece, clear = 0.3 });
            }
            if (dt.stackCars < m_D.stackMin) rep.notes.Add($"drive-through stacks {dt.stackCars} cars (< {m_D.stackMin})");
            foreach (var p in path)
                if (!Poly2.Contains(m_Lot, p)) { rep.notes.Add("drive-through lane leaves the lot"); break; }
            m_Dt = dt;
        }

        static double2 AlongPath(List<double2> path, double t, out double2 dir)
        {
            dir = new double2(1, 0);
            for (int i = 0; i + 1 < path.Count; i++)
            {
                double L = math.distance(path[i], path[i + 1]);
                dir = (path[i + 1] - path[i]) / Math.Max(L, 1e-12);
                if (t <= L || i + 2 == path.Count) return path[i] + dir * math.clamp(t, 0, L);
                t -= L;
            }
            return path.Count > 0 ? path[0] : 0;
        }

        void FireLaneAndDriveThrough()
        {
            if (m_FireLane != null)
            {
                m_Out.fireLane = m_FireLane;
                var along = math.normalize(m_FireB - m_FireA);
                var nrm = -Poly2.Perp(along);
                var c = Poly2.Centroid(m_FireLane);
                if (math.dot(c - m_FireA, nrm) < 0) nrm = -nrm;
                m_Out.lanes.Add(new LotLane
                {
                    kind = LaneKind.FireLane, a = m_FireA + nrm * (m_D.fireLaneWidth * 0.5), b = m_FireB + nrm * (m_D.fireLaneWidth * 0.5),
                    width = (float)m_D.fireLaneWidth, poly = m_FireLane,
                });
                if (m_T.paved)
                {
                    m_Out.lines.Add(new MarkLine { a = m_FireA, b = m_FireB, width = 0.15f, colour = MarkColour.Red });
                    double L = math.distance(m_FireA, m_FireB);
                    for (double t = 7.5; t <= L - 3; t += 15)
                        m_Out.signs.Add(new MarkSign { symbol = MarkSymbol.FireLane, pos = m_FireA + along * t + nrm * (m_D.fireLaneWidth * 0.5), yaw = (float)Poly2.Yaw(along), size = 2.5f, colour = MarkColour.Red });
                }
            }
            if (m_Dt != null)
            {
                m_Out.driveThrough = m_Dt;
                for (int i = 0; i + 1 < m_Dt.path.Length; i++)
                    m_Out.lanes.Add(new LotLane { kind = LaneKind.DriveThrough, a = m_Dt.path[i], b = m_Dt.path[i + 1], width = m_Dt.width, oneWay = true, poly = m_Dt.pieces[i] });
                if (m_T.paved)
                {
                    var path = new List<double2>(m_Dt.path);
                    double total = 0;
                    for (int i = 0; i + 1 < path.Count; i++) total += math.distance(path[i], path[i + 1]);
                    for (double t = 4; t <= total - 2; t += 10)
                    {
                        var p = AlongPath(path, t, out var d);
                        m_Out.signs.Add(new MarkSign { symbol = MarkSymbol.Arrow, pos = p, yaw = (float)Poly2.Yaw(d), size = 3f, colour = MarkColour.White });
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------ report
        void Report()
        {
            var rep = m_Out.report;
            rep.total = m_Out.stalls.Count;
            rep.accessible = m_Out.AccessibleCount;
            rep.van = m_Out.Count(StallKind.Van);
            bool ada = m_T.accessible && !m_Trailer;
            rep.required = ada ? Ada.Required(rep.total) : 0;
            rep.requiredVan = ada ? Ada.RequiredVan(rep.required) : 0;
            rep.module = (float)(rep.squeezed ? 2 * m_D.squeezeLength + m_D.aisleTwoWay : m_D.Module90);
            rep.moduleInBand = rep.module >= m_D.moduleP25 - 1e-6 && rep.module <= m_D.moduleP75 + 1e-6;
            if (ada && rep.accessible < rep.required) rep.notes.Add($"accessible {rep.accessible} < required {rep.required}");
            if (ada && rep.van < Ada.RequiredVan(rep.accessible)) rep.notes.Add($"van {rep.van} < required {Ada.RequiredVan(rep.accessible)}");
            rep.valid = rep.total > 0 && (!ada || (rep.accessible >= rep.required && rep.van >= Ada.RequiredVan(rep.accessible)));
        }
    }
}
