using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Sites
{
    /// <summary>
    /// The parking engine (card sites-S2, design §5): lays out one lot. Pure C# in double precision, deterministic: the
    /// search runs in a fixed order and keeps the first best candidate, so the same input always gives the same layout.
    ///
    /// The search, for every candidate aisle direction (the lot's long and short axes, perpendicular and parallel to the
    /// frontage, parallel and perpendicular to the building), stall angle (90 degrees two-way; 60 and 45 one-way where a
    /// double 90-degree module does not fit), squeeze (the profile's p25 stall where it fits one more row), spine mode
    /// (lanes from the entrances only; plus an end lane at side entrances; plus end lanes at both ends of the aisles) and
    /// module phase (0.25 m steps): stack double-loaded modules across the lot, single-loaded rows where room is left, drive
    /// lanes ("spines") that connect the aisles to the entrances, cut stall rows into runs, and score the result by
    /// capacity against the target, dead-end aisles, alignment with the building and hops from the entrance.
    /// The winner is then dressed (accessible stalls, islands, lamps, kerbs, wheel stops, markings: ParkingDressing).
    /// </summary>
    public static class ParkingPacker
    {
        /// <summary>Bumped whenever a rule changes the output for the same input (design §10).</summary>
        public const int Version = 1;

        public static LotLayout Pack(double2[] lot, IReadOnlyList<LotEntrance> entrances, LotBuilding building, LotTargets targets, ulong seed, ParkingDims dims = null)
        {
            var input = new LotInput { lot = lot, building = building, targets = targets ?? new LotTargets(), seed = seed };
            if (entrances != null) input.entrances.AddRange(entrances);
            return Pack(input, dims);
        }

        public static LotLayout Pack(LotInput input, ParkingDims dims = null) => new ParkingSearch(input, dims ?? ParkingDims.Default).Run();
    }

    sealed partial class ParkingSearch
    {
        const double Eps = 1e-7;
        /// <summary>No stall within this depth inside an entrance throat.</summary>
        public const double EntranceClear = 6.0;

        // ------------------------------------------------------------------------------------------ state
        readonly LotInput m_In;
        readonly ParkingDims m_D;
        readonly LotTargets m_T;
        readonly LotLayout m_Out = new LotLayout();
        double2[] m_Lot;
        LotEdgeKind[] m_Kinds;
        readonly List<LotEntrance> m_Ents = new List<LotEntrance>();
        readonly List<double2> m_EntIn = new List<double2>();      // inward unit normal per entrance (world)
        double2[] m_Bldg;
        double2 m_Door;
        Poly2.Rect m_BldgRect;
        readonly List<Obs> m_StallObs = new List<Obs>(), m_LaneObs = new List<Obs>();
        double m_StallW, m_StallL;
        bool m_Trailer;

        /// <summary>An area to keep clear; lane = stalls ending against it get an end island (entrance throats).</summary>
        struct Obs { public double2[] poly; public double clear; public bool lane; }

        sealed class Frame
        {
            public int index;
            public double2 d;
            public double2[] lot;
            public double minU, maxU, minV, maxV;
            public readonly List<Obs> stallObs = new List<Obs>(), laneObs = new List<Obs>();
            public readonly List<FEnt> ents = new List<FEnt>();
            public bool anySide;
        }

        struct FEnt
        {
            public int src;
            public double2 a, b, mid, n;
            public double width, lo, hi;          // lo..hi: the throat's extent across the aisles (v) or along them (u)
            public bool vertical;                 // enters across the rows (|n.v| >= |n.u|)
            public EntranceFlow flow;
        }

        struct Iv { public double a, b; public bool laneA, laneB; }

        sealed class Elem
        {
            public bool aisle;
            public double v0, v1;
            public int front;          // rows: +1 = stall fronts on v1 (aisle above), -1 = on v0 (aisle below)
            public int aisleElem;      // rows: the aisle element they open onto
            public int ordinal;        // aisles: order from the bottom (one-way flow alternates)
            public readonly List<Iv> free = new List<Iv>();
        }

        struct Spine { public double u0, u1, t0, t1; public int ent; }

        struct SpineSeg { public double u0, u1, t0, t1; public int spine; public bool conn; public int hops; }

        struct AisleSeg
        {
            public int elem;
            public double a, b;
            public bool conn, deadA, deadB, usable, entA, entB;
            public int hops, flow, stalls;
            /// <summary>The flow of a side entrance attached at end A / B ((int)EntranceFlow), or -1.</summary>
            public int flowA, flowB;
        }

        /// <summary>One candidate's geometry parameters.</summary>
        struct Geo { public double angle, w, l, D, W, pu, o, sin; public bool squeeze, oneWay; }

        struct Score { public double score; public int cap, deadEnds; public double avgHops; }

        // ------------------------------------------------------------------------------------------ scratch (reused)
        readonly List<double> m_Xs = new List<double>();
        readonly List<Iv> m_Blk = new List<Iv>(), m_Merged = new List<Iv>(), m_Tmp = new List<Iv>(), m_Tmp2 = new List<Iv>();
        readonly List<Elem> m_El = new List<Elem>();
        readonly List<Elem> m_ElPool = new List<Elem>();
        readonly List<AisleSeg> m_Aisles = new List<AisleSeg>();
        readonly List<Spine> m_Spines = new List<Spine>();
        readonly List<SpineSeg> m_SpSegs = new List<SpineSeg>();
        readonly List<int> m_Queue = new List<int>();
        static readonly Comparison<Iv> s_ByA = (x, y) => x.a.CompareTo(y.a);

        public ParkingSearch(LotInput input, ParkingDims dims)
        {
            m_In = input ?? throw new ArgumentNullException(nameof(input));
            m_D = dims;
            m_T = input.targets ?? new LotTargets();
        }

        // ------------------------------------------------------------------------------------------ entry
        public LotLayout Run()
        {
            var rep = m_Out.report;
            rep.version = ParkingPacker.Version;
            rep.seed = m_In.seed;
            if (!Prepare(rep)) { rep.valid = false; return m_Out; }

            var dirs = Directions();
            var frames = new List<Frame>(dirs.Count);
            for (int i = 0; i < dirs.Count; i++) frames.Add(MakeFrame(i, dirs[i]));

            Score best = new Score { score = double.NegativeInfinity };
            Frame bestF = null;
            Geo bestG = default;
            int bestMode = 0;
            double bestPhase = 0;
            int candidates = 0;
            foreach (var f in frames)
            {
                foreach (var g in Geometries(f))
                {
                    double period = 2 * g.D + g.W;
                    int phases = Math.Max(1, (int)Math.Ceiling(period / m_D.phaseStep - 1e-9));
                    for (int k = 0; k < phases; k++)
                    {
                        double phase = k * m_D.phaseStep;
                        PreparePhase(f, g, phase);
                        for (int mode = 0; mode < 3; mode++)
                        {
                            if (mode == 1 && !f.anySide) continue;   // mode 1 differs from 0 only at side entrances
                            candidates++;
                            var s = Evaluate(f, g, mode, false);
                            if (s.score > best.score + 1e-9)
                            {
                                best = s; bestF = f; bestG = g; bestMode = mode; bestPhase = phase;
                            }
                        }
                    }
                }
            }
            rep.candidates = candidates;
            if (bestF == null || best.cap <= 0)
            {
                rep.notes.Add("no stall fits this lot with its entrances");
                rep.valid = false;
                FinishEmpty();
                return m_Out;
            }

            // Materialize the winner (same code path as the search, recording geometry), then dress it.
            PreparePhase(bestF, bestG, bestPhase);
            var plan = new Plan { f = bestF, g = bestG };
            Evaluate(bestF, bestG, bestMode, true, plan);
            rep.dirIndex = bestF.index;
            rep.dirDeg = (float)Poly2.AxisDeg(bestF.d);
            rep.angle = (float)bestG.angle;
            rep.phase = (float)bestPhase;
            rep.spineMode = bestMode;
            rep.squeezed = bestG.squeeze;
            rep.capacity = best.cap;
            rep.deadEnds = best.deadEnds;
            rep.avgHops = (float)best.avgHops;
            rep.score = (float)best.score;
            Dress(plan);
            return m_Out;
        }

        // ------------------------------------------------------------------------------------------ input
        bool Prepare(LotReport rep)
        {
            if (m_In.lot == null || m_In.lot.Length < 3) { rep.notes.Add("lot polygon has fewer than 3 points"); return false; }
            var clean = Poly2.Clean(m_In.lot, out var keep);
            if (clean.Count < 3 || Poly2.Area(clean) < 1) { rep.notes.Add("lot polygon is degenerate"); return false; }
            int n = clean.Count;
            var kinds = new LotEdgeKind[n];
            for (int i = 0; i < n; i++)
                kinds[i] = m_In.edgeKinds != null && keep[i] < m_In.edgeKinds.Length ? m_In.edgeKinds[keep[i]] : LotEdgeKind.Property;
            if (Poly2.SignedArea(clean) < 0)
            {
                clean.Reverse();
                var k2 = new LotEdgeKind[n];
                for (int k = 0; k < n; k++) k2[k] = kinds[((n - 2 - k) % n + n) % n];
                kinds = k2;
            }
            m_Lot = clean.ToArray();
            m_Kinds = kinds;
            m_Out.lot = m_Lot;

            m_Trailer = m_T.stallKind == StallKind.Trailer;
            m_StallW = m_Trailer ? m_D.trailerWidth : m_D.stallWidth;
            m_StallL = m_Trailer ? m_D.trailerLength : m_D.stallLength;

            foreach (var e in m_In.entrances)
            {
                if (e == null) continue;
                var along = e.b - e.a;
                double len = math.length(along);
                if (len < 1) { rep.notes.Add("entrance narrower than 1 m skipped"); continue; }
                var nrm = Poly2.Perp(along / len);
                var mid = e.Mid;
                bool l = Poly2.Contains(m_Lot, mid + nrm * 0.3), r = Poly2.Contains(m_Lot, mid - nrm * 0.3);
                if (!l && r) nrm = -nrm;
                else if (l == r)
                {
                    var c = Poly2.Centroid(m_Lot);
                    if (math.dot(c - mid, nrm) < 0) nrm = -nrm;
                    rep.notes.Add($"entrance at ({mid.x:F1}, {mid.y:F1}) is not on the lot edge");
                }
                m_Ents.Add(e);
                m_EntIn.Add(nrm);
                m_Out.entrances.Add(e);
            }
            if (m_Ents.Count == 0) { rep.notes.Add("lot has no entrance"); return false; }

            if (m_In.building != null && m_In.building.footprint != null && m_In.building.footprint.Length >= 3)
            {
                m_Bldg = Poly2.Ccw(m_In.building.footprint);
                m_BldgRect = Poly2.MinAreaRect(m_Bldg);
                if (m_In.building.hasDoor) m_Door = m_In.building.door;
                else
                {
                    // The footprint edge nearest the lot's centroid: its midpoint.
                    var c = Poly2.Centroid(m_Lot);
                    double bd = double.MaxValue;
                    for (int i = 0; i < m_Bldg.Length; i++)
                    {
                        var a = m_Bldg[i];
                        var b = m_Bldg[(i + 1) % m_Bldg.Length];
                        double d = Poly2.SegDist(c, a, b);
                        if (d < bd - 1e-9) { bd = d; m_Door = (a + b) * 0.5; }
                    }
                }
                m_StallObs.Add(new Obs { poly = m_Bldg, clear = m_D.buildingClear });
                m_LaneObs.Add(new Obs { poly = m_Bldg, clear = m_D.laneBuildingClear });
            }
            else m_Door = Poly2.Centroid(m_Lot);

            foreach (var z in m_In.noPark)
                if (z != null && z.Length >= 3) m_StallObs.Add(new Obs { poly = Poly2.Ccw(z), clear = 0 });
            // Keep the first metres inside every entrance clear of stalls (the throat plus 0.5 m each side, 6 m deep).
            for (int i = 0; i < m_Ents.Count; i++)
            {
                var e = m_Ents[i];
                var inw = m_EntIn[i];
                var t = math.normalize(e.b - e.a);
                var a = e.a - t * 0.5;
                var b = e.b + t * 0.5;
                m_StallObs.Add(new Obs { poly = Poly2.Ccw(new[] { a, b, b + inw * EntranceClear, a + inw * EntranceClear }), clear = 0, lane = true });
            }

            if (m_T.fireLane) MakeFireLane(rep);
            if (m_T.driveThrough) MakeDriveThrough(rep);
            return true;
        }

        List<double2> Directions()
        {
            var dirs = new List<double2>();
            void Add(double2 d)
            {
                if (math.lengthsq(d) < 1e-12) return;
                d = Poly2.Axis(d);
                double a = Poly2.AxisDeg(d);
                foreach (var e in dirs)
                {
                    double diff = Math.Abs(Poly2.AxisDeg(e) - a);
                    if (Math.Min(diff, 180 - diff) < 1.0) return;
                }
                dirs.Add(d);
            }
            var r = Poly2.MinAreaRect(m_Lot);
            Add(r.axis);
            Add(Poly2.Perp(r.axis));
            var front = m_In.frontageDir;
            if (math.lengthsq(front) < 1e-12) front = m_Ents[0].b - m_Ents[0].a;
            Add(Poly2.Perp(front));
            Add(front);
            if (m_Bldg != null)
            {
                Add(m_BldgRect.axis);
                Add(Poly2.Perp(m_BldgRect.axis));
            }
            return dirs;
        }

        Frame MakeFrame(int index, double2 d)
        {
            var f = new Frame { index = index, d = d, lot = new double2[m_Lot.Length] };
            f.minU = f.minV = double.MaxValue;
            f.maxU = f.maxV = double.MinValue;
            for (int i = 0; i < m_Lot.Length; i++)
            {
                var p = Poly2.ToFrame(m_Lot[i], d);
                f.lot[i] = p;
                f.minU = Math.Min(f.minU, p.x); f.maxU = Math.Max(f.maxU, p.x);
                f.minV = Math.Min(f.minV, p.y); f.maxV = Math.Max(f.maxV, p.y);
            }
            foreach (var o in m_StallObs) f.stallObs.Add(new Obs { poly = ToFrame(o.poly, d), clear = o.clear, lane = o.lane });
            foreach (var o in m_LaneObs) f.laneObs.Add(new Obs { poly = ToFrame(o.poly, d), clear = o.clear });
            for (int i = 0; i < m_Ents.Count; i++)
            {
                var e = m_Ents[i];
                var a = Poly2.ToFrame(e.a, d);
                var b = Poly2.ToFrame(e.b, d);
                var n = Poly2.ToFrame(m_EntIn[i], d);
                bool vertical = Math.Abs(n.y) >= Math.Abs(n.x);
                var fe = new FEnt
                {
                    src = i, a = a, b = b, mid = (a + b) * 0.5, n = n, width = math.distance(a, b), vertical = vertical, flow = e.flow,
                    lo = vertical ? Math.Min(a.x, b.x) : Math.Min(a.y, b.y),
                    hi = vertical ? Math.Max(a.x, b.x) : Math.Max(a.y, b.y),
                };
                if (!vertical) f.anySide = true;
                f.ents.Add(fe);
            }
            return f;
        }

        static double2[] ToFrame(double2[] p, double2 d)
        {
            var r = new double2[p.Length];
            for (int i = 0; i < p.Length; i++) r[i] = Poly2.ToFrame(p[i], d);
            return r;
        }

        IEnumerable<Geo> Geometries(Frame f)
        {
            double across = f.maxV - f.minV - 2 * m_D.edgeMargin;
            var nominal = MakeGeo(90, m_StallW, m_StallL, false);
            yield return nominal;
            if (m_Trailer) yield break;
            // Squeeze (the p25 stall) only where a small lot needs it: it holds at most one double module at full size and
            // the squeezed stall fits another row.
            var sq = MakeGeo(90, m_D.squeezeWidth, m_D.squeezeLength, true);
            int rowsNominal = RowsAcross(across + m_D.edgeMargin, nominal);
            if ((sq.l < nominal.l - 1e-9 || sq.w < nominal.w - 1e-9) && rowsNominal <= 2 && RowsAcross(across + m_D.edgeMargin, sq) > rowsNominal)
                yield return sq;
            if (across < 2 * nominal.D + nominal.W - 1e-9)
            {
                yield return MakeGeo(60, m_StallW, m_StallL, false);
                yield return MakeGeo(45, m_StallW, m_StallL, false);
            }
        }

        /// <summary>Stall rows that fit across an extent (double modules, then one single-loaded row).</summary>
        static int RowsAcross(double extent, Geo g)
        {
            double period = 2 * g.D + g.W;
            int k = (int)Math.Floor((extent + 1e-9) / period);
            double rem = extent - k * period;
            return 2 * k + (rem + 1e-9 >= g.D + g.W ? 1 : 0);
        }

        Geo MakeGeo(double angle, double w, double l, bool squeeze)
        {
            double a = angle * Math.PI / 180;
            double sin = angle >= 89.9 ? 1 : Math.Sin(a);
            double D = ParkingDims.Depth(w, l, angle);
            return new Geo
            {
                angle = angle, w = w, l = l, D = D, W = m_D.Aisle(angle, m_T.stallKind), sin = sin,
                pu = w / sin, o = angle >= 89.9 ? 0 : D * Math.Cos(a) / sin, squeeze = squeeze, oneWay = angle < 89.9,
            };
        }

        // ------------------------------------------------------------------------------------------ one phase
        void PreparePhase(Frame f, Geo g, double phase)
        {
            foreach (var old in m_El) m_ElPool.Add(old);
            m_El.Clear();
            double m = m_D.edgeMargin;
            double vLo = f.minV + m, vHi = f.maxV - m;
            double s = vLo + phase;
            double D = g.D, W = g.W, period = 2 * D + W;
            // A single-loaded module below the first double module: aisle, then a row facing down onto it.
            if (s - D >= vLo - 1e-9 && s - D - W >= f.minV - 1e-9)
            {
                int ai = AddElem(true, s - D - W, s - D, 0, -1);
                AddElem(false, s - D, s, -1, ai);
            }
            double e = s;
            while (e + period <= vHi + 1e-9)
            {
                int ai = m_El.Count + 1;
                AddElem(false, e, e + D, +1, ai);
                AddElem(true, e + D, e + D + W, 0, -1);
                AddElem(false, e + D + W, e + period, -1, ai);
                e += period;
            }
            // A single-loaded module on top: a row facing up onto an aisle that may use the edge margin.
            if (e + D <= vHi + 1e-9 && e + D + W <= f.maxV + 1e-9)
            {
                AddElem(false, e, e + D, +1, m_El.Count + 1);
                AddElem(true, e + D, e + D + W, 0, -1);
            }
            int ord = 0;
            m_Aisles.Clear();
            for (int i = 0; i < m_El.Count; i++)
            {
                var el = m_El[i];
                if (el.aisle)
                {
                    el.ordinal = ord++;
                    StripFree(f, 0, el.v0, el.v1, 0, f.laneObs, el.free);
                    foreach (var iv in el.free)
                        if (iv.b - iv.a > 1e-6) m_Aisles.Add(new AisleSeg { elem = i, a = iv.a, b = iv.b });
                }
                else StripFree(f, 0, el.v0, el.v1, m, f.stallObs, el.free);
            }
        }

        int AddElem(bool aisle, double v0, double v1, int front, int aisleElem)
        {
            Elem el;
            if (m_ElPool.Count > 0) { el = m_ElPool[m_ElPool.Count - 1]; m_ElPool.RemoveAt(m_ElPool.Count - 1); }
            else el = new Elem();
            el.aisle = aisle; el.v0 = v0; el.v1 = v1; el.front = front; el.aisleElem = aisleElem; el.ordinal = 0;
            el.free.Clear();
            m_El.Add(el);
            return m_El.Count - 1;
        }

        // ------------------------------------------------------------------------------------------ one candidate
        Score Evaluate(Frame f, Geo g, int mode, bool record, Plan plan = null)
        {
            double lw = m_D.laneTwoWay;
            // 1. Spines: drive lanes from the entrances (and lot ends) across the aisles.
            m_Spines.Clear();
            double aLo = double.MaxValue, aHi = double.MinValue;
            foreach (var el in m_El) if (el.aisle) { aLo = Math.Min(aLo, el.v0); aHi = Math.Max(aHi, el.v1); }
            if (aLo > aHi) return new Score { score = double.NegativeInfinity };
            for (int i = 0; i < f.ents.Count; i++)
            {
                var e = f.ents[i];
                double w = e.flow == EntranceFlow.TwoWay ? lw : m_D.laneOneWay;
                if (e.vertical)
                {
                    double far = e.mid.y;
                    bool any = false;
                    foreach (var el in m_El)
                    {
                        if (!el.aisle) continue;
                        if (e.n.y > 0 ? el.v1 > e.mid.y : el.v0 < e.mid.y) { far = e.n.y > 0 ? Math.Max(far, el.v1) : Math.Min(far, el.v0); any = true; }
                    }
                    if (!any) continue;
                    m_Spines.Add(new Spine { u0 = e.mid.x - w * 0.5, u1 = e.mid.x + w * 0.5, t0 = Math.Min(e.mid.y, far), t1 = Math.Max(e.mid.y, far), ent = i });
                }
                else
                {
                    bool aligned = AlignedAisle(e) >= 0;
                    if (!aligned || mode >= 1)
                    {
                        double u0 = e.n.x > 0 ? e.mid.x : e.mid.x - lw;
                        m_Spines.Add(new Spine { u0 = u0, u1 = u0 + lw, t0 = Math.Min(aLo, e.lo), t1 = Math.Max(aHi, e.hi), ent = i });
                    }
                }
            }
            int aisleCount = 0;
            foreach (var el in m_El) if (el.aisle) aisleCount++;
            if (mode == 2 && aisleCount < 2) return new Score { score = double.NegativeInfinity };   // end lanes link two or more aisles
            if (mode == 2)
            {
                AddEndSpine(f.minU, f.minU + lw, aLo, aHi);
                AddEndSpine(f.maxU - lw, f.maxU, aLo, aHi);
            }

            // 2. Spine segments: where each spine actually runs inside the lot.
            m_SpSegs.Clear();
            for (int s = 0; s < m_Spines.Count; s++)
            {
                var sp = m_Spines[s];
                StripFree(f, 1, sp.u0, sp.u1, 0, f.laneObs, m_Tmp);
                foreach (var iv in m_Tmp)
                {
                    double t0 = Math.Max(iv.a, sp.t0), t1 = Math.Min(iv.b, sp.t1);
                    if (t1 - t0 > 1e-6) m_SpSegs.Add(new SpineSeg { u0 = sp.u0, u1 = sp.u1, t0 = t0, t1 = t1, spine = s, hops = int.MaxValue });
                }
            }

            // 3. Connectivity from the entrances (breadth first; aisle nodes 0..A-1, spine nodes A..).
            int A = m_Aisles.Count;
            for (int i = 0; i < A; i++)
            {
                var a = m_Aisles[i];
                a.conn = false; a.hops = int.MaxValue; a.entA = a.entB = false; a.stalls = 0; a.usable = false; a.flowA = a.flowB = -1;
                m_Aisles[i] = a;
            }
            m_Queue.Clear();
            for (int i = 0; i < f.ents.Count; i++)
            {
                var e = f.ents[i];
                if (e.vertical)
                {
                    for (int k = 0; k < m_SpSegs.Count; k++)
                    {
                        var sg = m_SpSegs[k];
                        if (m_Spines[sg.spine].ent != i) continue;
                        if (e.mid.y >= sg.t0 - 3.0 && e.mid.y <= sg.t1 + 3.0) Reach(A + k, 0);
                    }
                }
                else
                {
                    int ae = AlignedAisle(e);
                    if (ae >= 0)
                        for (int k = 0; k < A; k++)
                        {
                            var a = m_Aisles[k];
                            if (a.elem != ae) continue;
                            if (e.n.x > 0 && Math.Abs(a.a - e.mid.x) <= 3.0) { a.entA = true; a.flowA = (int)e.flow; m_Aisles[k] = a; Reach(k, 0); }
                            else if (e.n.x < 0 && Math.Abs(a.b - e.mid.x) <= 3.0) { a.entB = true; a.flowB = (int)e.flow; m_Aisles[k] = a; Reach(k, 0); }
                        }
                    for (int k = 0; k < m_SpSegs.Count; k++)
                    {
                        var sg = m_SpSegs[k];
                        if (m_Spines[sg.spine].ent != i) continue;
                        if (e.hi >= sg.t0 - 0.5 && e.lo <= sg.t1 + 0.5) Reach(A + k, 0);
                    }
                }
            }
            for (int qi = 0; qi < m_Queue.Count; qi++)
            {
                int node = m_Queue[qi];
                int h = node < A ? m_Aisles[node].hops : m_SpSegs[node - A].hops;
                if (node < A)
                {
                    var a = m_Aisles[node];
                    var el = m_El[a.elem];
                    for (int k = 0; k < m_SpSegs.Count; k++) if (Touch(a, el, m_SpSegs[k])) Reach(A + k, h + 1);
                }
                else
                {
                    var sg = m_SpSegs[node - A];
                    for (int k = 0; k < A; k++) if (Touch(m_Aisles[k], m_El[m_Aisles[k].elem], sg)) Reach(k, h + 1);
                    for (int k = 0; k < m_SpSegs.Count; k++)
                    {
                        if (k == node - A) continue;
                        var o = m_SpSegs[k];
                        if (Math.Min(sg.u1, o.u1) - Math.Max(sg.u0, o.u0) > 1.0 && Math.Min(sg.t1, o.t1) - Math.Max(sg.t0, o.t0) > 1.0) Reach(A + k, h + 1);
                    }
                }
            }

            // 4. Dead ends and one-way usability.
            for (int k = 0; k < A; k++)
            {
                var a = m_Aisles[k];
                if (!a.conn) continue;
                var el = m_El[a.elem];
                bool openA = a.entA, openB = a.entB;
                for (int s = 0; s < m_SpSegs.Count && !(openA && openB); s++)
                {
                    var sg = m_SpSegs[s];
                    if (!sg.conn || !Touch(a, el, sg)) continue;
                    // A lane opens an aisle end only when it leads somewhere: to an entrance or past this aisle.
                    if (m_Spines[sg.spine].ent < 0 && sg.t0 >= el.v0 - 1.0 && sg.t1 <= el.v1 + 1.0) continue;
                    if (sg.u0 <= a.a + lw + 0.5) openA = true;
                    if (sg.u1 >= a.b - lw - 0.5) openB = true;
                }
                a.deadA = !openA; a.deadB = !openB;
                // One-way aisles run from an entry-only entrance or towards an exit-only one; otherwise they alternate.
                int fl = el.ordinal % 2 == 0 ? 1 : -1;
                if (a.flowA == (int)EntranceFlow.EntryOnly || a.flowB == (int)EntranceFlow.ExitOnly) fl = 1;
                else if (a.flowB == (int)EntranceFlow.EntryOnly || a.flowA == (int)EntranceFlow.ExitOnly) fl = -1;
                a.flow = g.oneWay ? fl : 0;
                a.usable = !g.oneWay || (openA && openB);
                m_Aisles[k] = a;
            }

            // 5. Stall rows: each row against the usable pieces of its aisle, minus the spines.
            int cap = 0;
            m_Merged.Clear();
            for (int r = 0; r < m_El.Count; r++)
            {
                var row = m_El[r];
                if (row.aisle || row.aisleElem < 0 || row.aisleElem >= m_El.Count) continue;
                // Spine blocks for this row (lane ends get end islands).
                m_Blk.Clear();
                foreach (var sg in m_SpSegs)
                    if (sg.conn && Math.Min(sg.t1, row.v1) - Math.Max(sg.t0, row.v0) > 1e-6)
                        m_Blk.Add(new Iv { a = sg.u0, b = sg.u1, laneA = true, laneB = true });
                Merge(m_Blk, m_Merged);
                SubtractInto(row.free, m_Merged, m_Tmp);
                for (int k = 0; k < A; k++)
                {
                    var a = m_Aisles[k];
                    if (a.elem != row.aisleElem || !a.conn || !a.usable) continue;
                    int flow = a.flow;
                    double o = g.o;
                    foreach (var iv in m_Tmp)
                    {
                        double ra = Math.Max(iv.a, a.a), rb = Math.Min(iv.b, a.b);
                        if (rb - ra < g.pu) continue;
                        bool islA = iv.laneA && ra == iv.a, islB = iv.laneB && rb == iv.b;
                        int n = CountRun(ra, rb, islA, islB, g.pu, o);
                        if (n <= 0) continue;
                        cap += n;
                        a.stalls += n;
                        if (record) plan.AddRun(this, f, g, row, r, k, ra, rb, islA, islB, flow, n);
                    }
                    m_Aisles[k] = a;
                }
            }

            // 6. Score.
            int dead = 0;
            double hopSum = 0;
            for (int k = 0; k < A; k++)
            {
                var a = m_Aisles[k];
                if (a.stalls <= 0) continue;
                dead += (a.deadA ? 1 : 0) + (a.deadB ? 1 : 0);
                hopSum += (double)a.hops * a.stalls;
            }
            double avgHops = cap > 0 ? hopSum / cap : 0;
            int T = m_T.stalls;
            double capScore = T > 0 ? (cap >= T ? T + 0.2 * (cap - T) : cap) : cap;
            double align = 0;
            if (m_Bldg != null)
            {
                double c = math.dot(f.d, m_BldgRect.axis);
                align = Math.Abs(2 * c * c - 1);   // |cos 2x|: 1 parallel or perpendicular, 0 at 45 degrees
            }
            double score = capScore + 2.0 * align - 0.5 * dead - 0.05 * avgHops - (g.squeeze ? 1.5 : 0) - (g.oneWay ? 0.5 : 0);
            if (cap == 0) score = -1e9 + capScore;
            if (record) plan.Finish(this, f, g);
            return new Score { score = score, cap = cap, deadEnds = dead, avgHops = avgHops };
        }

        void AddEndSpine(double u0, double u1, double t0, double t1)
        {
            foreach (var s in m_Spines)
                if (Math.Min(s.u1, u1) - Math.Max(s.u0, u0) > 0.5 * (u1 - u0) && s.t0 <= t0 + 1e-6 && s.t1 >= t1 - 1e-6) return;
            m_Spines.Add(new Spine { u0 = u0, u1 = u1, t0 = t0, t1 = t1, ent = -1 });
        }

        /// <summary>The aisle element a side entrance runs straight into (throat overlaps the aisle by 3 m), or -1.</summary>
        int AlignedAisle(FEnt e)
        {
            double need = Math.Min(3.0, 0.5 * e.width);
            for (int i = 0; i < m_El.Count; i++)
            {
                var el = m_El[i];
                if (!el.aisle) continue;
                if (Math.Min(el.v1, e.hi) - Math.Max(el.v0, e.lo) >= need) return i;
            }
            return -1;
        }

        static bool Touch(AisleSeg a, Elem el, SpineSeg s) =>
            Math.Min(a.b, s.u1) - Math.Max(a.a, s.u0) >= Math.Min(2.5, 0.5 * (s.u1 - s.u0)) &&
            Math.Min(el.v1, s.t1) - Math.Max(el.v0, s.t0) >= Math.Min(2.5, 0.5 * (el.v1 - el.v0));

        void Reach(int node, int hops)
        {
            int A = m_Aisles.Count;
            if (node < A)
            {
                var a = m_Aisles[node];
                if (a.conn && a.hops <= hops) return;
                a.conn = true; a.hops = hops;
                m_Aisles[node] = a;
            }
            else
            {
                var s = m_SpSegs[node - A];
                if (s.conn && s.hops <= hops) return;
                s.conn = true; s.hops = hops;
                m_SpSegs[node - A] = s;
            }
            m_Queue.Add(node);
        }

        /// <summary>Stalls of one run: end islands at lane ends, then stalls with an intermediate island every islandEvery.</summary>
        int CountRun(double ra, double rb, bool islA, bool islB, double pu, double o)
        {
            double L = RunLength(ra, rb, islA, islB, o);
            if (L < pu - 1e-9) return 0;
            int K = m_D.islandEvery;
            double iw = m_D.islandWidth;
            if (K <= 0) return (int)Math.Floor((L + 1e-9) / pu);
            double blk = K * pu + iw;
            int g = (int)Math.Floor((L + iw + 1e-9) / blk);
            if (g == 0) return (int)Math.Floor((L + 1e-9) / pu);
            double rem = L - g * blk;
            int extra = rem >= pu - 1e-9 ? (int)Math.Floor((rem + 1e-9) / pu) : 0;
            return g * K + extra;
        }

        double RunLength(double ra, double rb, bool islA, bool islB, double o) =>
            (rb - (islB ? m_D.endIsland : 0)) - (ra + (islA ? m_D.endIsland : 0)) - o;

        // ------------------------------------------------------------------------------------------ strips
        static double Along(double2 p, int axis) => axis == 0 ? p.x : p.y;
        static double Across(double2 p, int axis) => axis == 0 ? p.y : p.x;

        /// <summary>
        /// Free intervals of a strip of the frame's lot. axis 0: the horizontal strip lo..hi in v, intervals in u; axis 1:
        /// the vertical strip lo..hi in u, intervals in v. A position is free when its cross segment over the strip lies
        /// inside the lot at least `margin` (L-infinity, so at least that far in plan) from the lot's boundary, and clear
        /// of every obstacle by the obstacle's clearance. Exact for the lot (edge by edge); one interval per obstacle.
        /// </summary>
        void StripFree(Frame f, int axis, double lo, double hi, double margin, List<Obs> obs, List<Iv> res)
        {
            res.Clear();
            var P = f.lot;
            int n = P.Length;
            double mid = 0.5 * (lo + hi);
            m_Xs.Clear();
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double ca = Across(P[j], axis), cb = Across(P[i], axis);
                if ((ca <= mid) != (cb <= mid))
                {
                    double aa = Along(P[j], axis), ab = Along(P[i], axis);
                    m_Xs.Add(aa + (mid - ca) * (ab - aa) / (cb - ca));
                }
            }
            if (m_Xs.Count < 2) return;
            m_Xs.Sort();
            m_Blk.Clear();
            double elo = lo - margin + Eps, ehi = hi + margin - Eps;
            for (int i = 0, j = n - 1; i < n; j = i++)
                if (ClipAlong(P[j], P[i], axis, elo, ehi, out var x0, out var x1))
                    m_Blk.Add(new Iv { a = x0 - margin, b = x1 + margin });
            foreach (var o in obs)
            {
                double lo2 = lo - o.clear + Eps, hi2 = hi + o.clear - Eps;
                double bmin = double.MaxValue, bmax = double.MinValue;
                var q = o.poly;
                for (int i = 0, j = q.Length - 1; i < q.Length; j = i++)
                    if (ClipAlong(q[j], q[i], axis, lo2, hi2, out var x0, out var x1)) { bmin = Math.Min(bmin, x0); bmax = Math.Max(bmax, x1); }
                if (bmin <= bmax) m_Blk.Add(new Iv { a = bmin - o.clear, b = bmax + o.clear, laneA = o.lane, laneB = o.lane });
            }
            Merge(m_Blk, m_Merged);
            m_Tmp2.Clear();
            for (int k = 0; k + 1 < m_Xs.Count; k += 2) m_Tmp2.Add(new Iv { a = m_Xs[k], b = m_Xs[k + 1] });
            SubtractInto(m_Tmp2, m_Merged, res);
        }

        /// <summary>The along-axis extent of segment pq inside the across band [lo, hi]; false when it misses the band.</summary>
        static bool ClipAlong(double2 p, double2 q, int axis, double lo, double hi, out double x0, out double x1)
        {
            double cp = Across(p, axis), cq = Across(q, axis), ap = Along(p, axis), aq = Along(q, axis);
            x0 = x1 = 0;
            double t0 = 0, t1 = 1;
            double dc = cq - cp;
            if (Math.Abs(dc) < 1e-15)
            {
                if (cp < lo || cp > hi) return false;
            }
            else
            {
                double ta = (lo - cp) / dc, tb = (hi - cp) / dc;
                if (ta > tb) { var t = ta; ta = tb; tb = t; }
                t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
                if (t0 > t1) return false;
            }
            double xa = ap + (aq - ap) * t0, xb = ap + (aq - ap) * t1;
            x0 = Math.Min(xa, xb); x1 = Math.Max(xa, xb);
            return true;
        }

        /// <summary>Sorted, merged copy of src (lane flags or-ed).</summary>
        static void Merge(List<Iv> src, List<Iv> dst)
        {
            dst.Clear();
            if (src.Count == 0) return;
            src.Sort(s_ByA);
            var cur = src[0];
            for (int i = 1; i < src.Count; i++)
            {
                var x = src[i];
                if (x.a <= cur.b + 1e-9)
                {
                    if (x.b > cur.b) cur.b = x.b;
                    cur.laneA |= x.laneA; cur.laneB |= x.laneB;
                }
                else { dst.Add(cur); cur = x; }
            }
            dst.Add(cur);
        }

        /// <summary>dst = src minus the merged blocks; a cut end is a lane end when its block is a lane.</summary>
        static void SubtractInto(List<Iv> src, List<Iv> blocks, List<Iv> dst)
        {
            dst.Clear();
            foreach (var s in src)
            {
                double cur = s.a;
                bool laneLeft = s.laneA;
                foreach (var b in blocks)
                {
                    if (b.b <= cur || b.a >= s.b) continue;
                    bool lane = b.laneA || b.laneB;
                    if (b.a > cur + 1e-9) dst.Add(new Iv { a = cur, b = b.a, laneA = laneLeft, laneB = lane });
                    if (b.b > cur) { cur = b.b; laneLeft = lane; }
                    if (cur >= s.b) break;
                }
                if (s.b > cur + 1e-9) dst.Add(new Iv { a = cur, b = s.b, laneA = laneLeft, laneB = s.laneB });
            }
        }

        // ------------------------------------------------------------------------------------------ the recorded plan
        /// <summary>The winning candidate's geometry in the frame (u along the aisles, v across), kept for the dressing.</summary>
        sealed class Plan
        {
            public Frame f;
            public Geo g;
            public readonly List<StallRun> runs = new List<StallRun>();
            public readonly Dictionary<int, int> laneOfAisle = new Dictionary<int, int>();
            public readonly List<(double u0, double u1, double v0, double v1, int flow, bool oneWay, LaneKind kind)> lanes =
                new List<(double, double, double, double, int, bool, LaneKind)>();

            public void AddRun(ParkingSearch s, Frame f, Geo g, Elem row, int rowIdx, int aisle, double ra, double rb, bool islA, bool islB, int flow, int n)
            {
                var run = new StallRun
                {
                    row = rowIdx, v0 = row.v0, v1 = row.v1, front = row.front, flow = flow, D = g.D, o = g.o, angle = g.angle,
                    ra = ra, rb = rb, islandA = islA, islandB = islB, aisle = aisle,
                };
                // Lay the items: stalls at pitch pu with an island after every islandEvery stalls; centre the run between two
                // end islands, otherwise start against the first end.
                double start = ra + (islA ? s.m_D.endIsland : 0);
                double L = s.RunLength(ra, rb, islA, islB, g.o);
                int K = s.m_D.islandEvery;
                double used = n * g.pu + (K > 0 ? (n - 1) / K : 0) * s.m_D.islandWidth;
                double shift = islA && islB ? Math.Max(0, (L - used) * 0.5) : 0;
                double u = start + shift + (flow < 0 ? g.o : 0);
                run.u0 = u;
                for (int i = 0; i < n; i++)
                {
                    if (K > 0 && i > 0 && i % K == 0) { run.items.Add(new Item { kind = ItemKind.Island, w = s.m_D.islandWidth }); u += s.m_D.islandWidth; }
                    run.items.Add(new Item { kind = ItemKind.Stall, w = g.pu, stall = s.m_T.stallKind });
                    u += g.pu;
                }
                runs.Add(run);
            }

            public void Finish(ParkingSearch s, Frame f, Geo g)
            {
                // Lanes: every connected aisle piece and spine piece.
                for (int k = 0; k < s.m_Aisles.Count; k++)
                {
                    var a = s.m_Aisles[k];
                    if (!a.conn) continue;
                    var el = s.m_El[a.elem];
                    laneOfAisle[k] = lanes.Count;
                    lanes.Add((a.a, a.b, el.v0, el.v1, a.flow, g.oneWay && a.usable, LaneKind.Aisle));
                }
                foreach (var sg in s.m_SpSegs)
                    if (sg.conn) lanes.Add((sg.u0, sg.u1, sg.t0, sg.t1, 0, false, LaneKind.Spine));
                foreach (var r in runs) r.lane = laneOfAisle.TryGetValue(r.aisle, out var li) ? li : -1;
            }
        }

        enum ItemKind : byte { Stall = 0, Island = 1, AccessAisle = 2 }

        struct Item
        {
            public ItemKind kind;
            public double w;
            public StallKind stall;
        }

        /// <summary>One stall run in the frame: items laid along u from u0, fronts on the aisle side of the row strip.</summary>
        sealed class StallRun
        {
            public int row, aisle, lane;
            public double v0, v1, D, o, angle;
            public int front, flow;
            public double ra, rb, u0;
            public bool islandA, islandB;
            public readonly List<Item> items = new List<Item>();

            public double FrontV => front > 0 ? v1 : v0;
            public double BackV => front > 0 ? v0 : v1;
            public double Lean => flow * o;
            public double End
            {
                get
                {
                    double u = u0;
                    foreach (var it in items) u += it.w;
                    return u;
                }
            }
            /// <summary>Column extent of the items (front and back edges together).</summary>
            public double ColStart => u0 + Math.Min(0, Lean);
            public double ColEnd => End + Math.Max(0, Lean);

            /// <summary>The parallelogram of an item from u to u + w (CCW in the frame).</summary>
            public double2[] Quad(double u, double w)
            {
                double vf = FrontV, vb = BackV, l = Lean;
                var f0 = new double2(u, vf);
                var f1 = new double2(u + w, vf);
                var b1 = new double2(u + w + l, vb);
                var b0 = new double2(u + l, vb);
                return front > 0 ? new[] { b0, b1, f1, f0 } : new[] { f0, f1, b1, b0 };
            }
        }
    }
}
