using System;
using System.Collections.Generic;
using System.Globalization;

namespace Backroads.Roads
{
    /// <summary>
    /// R0 of 05 §9: builds the cleaned road graph from the frozen canon polylines (05 §5.1), deterministically, in double
    /// precision and Unity space. Steps:
    ///  1. T-splits: every road end flagged startJ/endJ that lies within 1 m of another road splits that road there,
    ///     unless it lands within 2 m of that road's own ends (this also catches the 6 dirt ends trafficNet skips).
    ///  2. Zone split: Highway roads are split where they cross the town kerb zone (street hull + 20 m); the inside
    ///     pieces become MainStreet.
    ///  3. Pieces between split points become edges; canon points are kept unchanged (splits snap onto a canon point
    ///     within 1 cm, otherwise an interpolated point is inserted).
    ///  4. Edge ends within 1 m cluster into one node, positioned on the through road's centreline when one is split there.
    ///  5. Degree-2 nodes on one road bending less than 25 degrees are joined away; others are Bend or ClassChange.
    ///  6. Classification: DeadEnd, DirtApron (dirt meets pavement), Junction; junctions closer than 30 m are a ComplexPair.
    ///  7. Major road by class first, straightness as the tie-break (fixes nodes 2 and 5), with a provisional control
    ///     from 05 §6.2 (two-way stop, all-way stop at a street-by-street X, stop on dirt).
    /// It also estimates the corner fillets, tangent points and mouths with the straight-line formula of §5.3 for the debug
    /// view; R1 replaces those with the polyline corner solver.
    /// Nodes are numbered like traffic.json (and 05's tables) where a legacy node is within 1 m; new nodes follow.
    /// </summary>
    public static class RoadGraphBuilder
    {
        /// <summary>Bump when the output for the same input changes (stored in the baked asset).</summary>
        public const int Version = 1;

        struct Split
        {
            public double s;
            public SplitReason reason;
        }

        public static RoadGraph Build(RoadGraphInput input, RoadGraphSettings settings = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            settings = settings ?? new RoadGraphSettings();
            CheckInput(input);
            var g = new RoadGraph
            {
                settings = settings,
                canonManifestSha256 = input.canonManifestSha256 ?? "",
                inputRoads = input.roads.Count,
                inputPoints = input.PointCount,
            };
            var streetPts = new List<D2>();
            foreach (var r in input.roads)
                if (r.cls == RoadClass.Street)
                    foreach (var p in r.pts) streetPts.Add(p.XZ);
            g.zone = new TownZone(streetPts, settings.zoneBuffer);

            var splits = FindSplits(input, g.zone, settings);
            for (int pass = 0; ; pass++)
            {
                g.nodes.Clear(); g.edges.Clear(); g.arms.Clear();
                BuildEdges(g, input, splits);
                BuildNodes(g, input, settings);
                BuildArms(g, settings);
                Classify(g, settings);
                if (pass >= 8 || !RemoveSameRoadJoins(g, input, splits)) break;
            }
            FindComplexPairs(g, settings);
            PickMajorsAndControls(g, settings);
            SolveProvisionalCorners(g, input, settings);
            SetFlagsAndNotes(g, settings);
            return g;
        }

        static void CheckInput(RoadGraphInput input)
        {
            var ids = new HashSet<int>();
            foreach (var r in input.roads)
            {
                if (!ids.Add(r.id)) throw new ArgumentException($"road id {r.id} appears twice");
                if (r.pts == null || r.pts.Length < 2 || r.d == null || r.d.Length != r.pts.Length)
                    throw new ArgumentException($"road {r.id}: needs at least 2 points and one d per point");
                for (int i = 1; i < r.d.Length; i++)
                    if (!(r.d[i] >= r.d[i - 1])) throw new ArgumentException($"road {r.id}: d decreases at point {i}");
                if (!(r.hw > 0)) throw new ArgumentException($"road {r.id}: half-width must be positive");
            }
        }

        // ------------------------------------------------------------------ 1-2 splits

        static List<Split>[] FindSplits(RoadGraphInput input, TownZone zone, RoadGraphSettings st)
        {
            int n = input.roads.Count;
            var raw = new List<Split>[n];
            for (int i = 0; i < n; i++) raw[i] = new List<Split>();

            for (int qi = 0; qi < n; qi++)
            {
                var q = input.roads[qi];
                for (int end = 0; end < 2; end++)
                {
                    if (!(end == 0 ? q.startJ : q.endJ)) continue;
                    var p = (end == 0 ? q.pts[0] : q.pts[q.pts.Length - 1]).XZ;
                    for (int ri = 0; ri < n; ri++)
                    {
                        if (ri == qi) continue;
                        var r = input.roads[ri];
                        var hit = Polyline.Project(r.pts, r.d, p);
                        if (hit.distance <= st.tJoinTolerance && hit.s > st.endClearance && r.Length - hit.s > st.endClearance)
                            raw[ri].Add(new Split { s = hit.s, reason = SplitReason.TJoin });
                    }
                }
            }

            for (int ri = 0; ri < n; ri++)
            {
                var r = input.roads[ri];
                if (r.cls != RoadClass.Highway) continue;
                bool prev = zone.Contains(r.pts[0].XZ);
                for (int i = 1; i < r.pts.Length; i++)
                {
                    bool cur = zone.Contains(r.pts[i].XZ);
                    if (cur == prev) continue;
                    double a = r.d[i - 1], b = r.d[i];
                    for (int k = 0; k < 60; k++)
                    {
                        double m = 0.5 * (a + b);
                        if (zone.Contains(Polyline.At(r.pts, r.d, m).XZ) == prev) a = m; else b = m;
                    }
                    raw[ri].Add(new Split { s = 0.5 * (a + b), reason = SplitReason.Zone });
                    prev = cur;
                }
            }

            var result = new List<Split>[n];
            for (int ri = 0; ri < n; ri++)
            {
                var r = input.roads[ri];
                var l = raw[ri];
                l.Sort((x, y) => x.s.CompareTo(y.s));
                var outL = new List<Split>();
                for (int k = 0; k < l.Count;)
                {
                    int j = k;
                    double sum = 0;
                    var why = SplitReason.None;
                    while (j < l.Count && l[j].s - l[k].s < st.clusterRadius) { sum += l[j].s; why |= l[j].reason; j++; }
                    double s = sum / (j - k);
                    int ci = NearestIndex(r.d, s);
                    if (Math.Abs(r.d[ci] - s) <= st.snapToCanonPoint) s = r.d[ci];
                    if (s >= st.clusterRadius && r.Length - s >= st.clusterRadius) outL.Add(new Split { s = s, reason = why });
                    k = j;
                }
                result[ri] = outL;
            }
            return result;
        }

        static int NearestIndex(double[] d, double s)
        {
            int lo = 0, hi = d.Length - 1;
            while (hi - lo > 1)
            {
                int m = (lo + hi) >> 1;
                if (d[m] <= s) lo = m; else hi = m;
            }
            return Math.Abs(d[lo] - s) <= Math.Abs(d[hi] - s) ? lo : hi;
        }

        // ------------------------------------------------------------------ 3 edges

        static void BuildEdges(RoadGraph g, RoadGraphInput input, List<Split>[] splits)
        {
            for (int ri = 0; ri < input.roads.Count; ri++)
            {
                var r = input.roads[ri];
                var b = new List<double> { 0 };
                foreach (var sp in splits[ri]) b.Add(sp.s);
                b.Add(r.Length);
                for (int k = 0; k < b.Count - 1; k++)
                {
                    double s0 = b[k], s1 = b[k + 1];
                    var pts = new List<D3> { Polyline.At(r.pts, r.d, s0) };
                    for (int i = 0; i < r.pts.Length; i++)
                        if (r.d[i] > s0 + 1e-9 && r.d[i] < s1 - 1e-9) pts.Add(r.pts[i]);
                    pts.Add(Polyline.At(r.pts, r.d, s1));
                    var e = new GraphEdge
                    {
                        id = g.edges.Count, sourceRoad = r.id, hw = r.hw, sStart = s0, sEnd = s1, pts = pts.ToArray(), cls = r.cls,
                        nodeA = -1, nodeB = -1,
                    };
                    e.s = Polyline.ArcLength(e.pts);
                    if (r.cls == RoadClass.Highway && g.zone.Contains(Polyline.At(r.pts, r.d, 0.5 * (s0 + s1)).XZ)) e.cls = RoadClass.MainStreet;
                    g.edges.Add(e);
                }
            }
        }

        // ------------------------------------------------------------------ 4 nodes

        sealed class TmpNode
        {
            public D3 p;
            public bool onSplit;
            public int order, legacy = -1;
            public double legacyDist = double.MaxValue;
        }

        static void BuildNodes(RoadGraph g, RoadGraphInput input, RoadGraphSettings st)
        {
            var lengthOf = new Dictionary<int, double>();
            foreach (var r in input.roads) lengthOf[r.id] = r.Length;
            var tmp = new List<TmpNode>();
            var endA = new int[g.edges.Count];
            var endB = new int[g.edges.Count];
            foreach (var e in g.edges)
            {
                for (int end = 0; end < 2; end++)
                {
                    var p = end == 0 ? e.pts[0] : e.pts[e.pts.Length - 1];
                    bool interior = end == 0 ? e.sStart > 0 : e.sEnd < lengthOf[e.sourceRoad];
                    int best = -1;
                    double bd = double.MaxValue;
                    for (int k = 0; k < tmp.Count; k++)
                    {
                        double d = D2.Distance(tmp[k].p.XZ, p.XZ);
                        if (d <= st.clusterRadius && d < bd) { bd = d; best = k; }
                    }
                    if (best < 0)
                    {
                        tmp.Add(new TmpNode { p = p, onSplit = interior, order = tmp.Count });
                        best = tmp.Count - 1;
                    }
                    else if (interior && !tmp[best].onSplit)
                    {
                        tmp[best].p = p;
                        tmp[best].onSplit = true;
                    }
                    if (end == 0) endA[e.id] = best; else endB[e.id] = best;
                }
            }

            // traffic.json numbering: each legacy node goes to the nearest node within the radius (closest claim wins)
            foreach (var L in input.legacyNodes)
            {
                int best = -1;
                double bd = double.MaxValue;
                for (int k = 0; k < tmp.Count; k++)
                {
                    double d = D2.Distance(tmp[k].p.XZ, L.p);
                    if (d <= st.legacyMatchRadius && d < bd) { bd = d; best = k; }
                }
                if (best >= 0 && bd < tmp[best].legacyDist) { tmp[best].legacy = L.id; tmp[best].legacyDist = bd; }
            }
            var order = new List<TmpNode>(tmp);
            order.Sort((a, b) =>
            {
                int ka = a.legacy >= 0 ? a.legacy : int.MaxValue, kb = b.legacy >= 0 ? b.legacy : int.MaxValue;
                return ka != kb ? ka.CompareTo(kb) : a.order.CompareTo(b.order);
            });
            var newId = new int[tmp.Count];
            for (int i = 0; i < order.Count; i++)
            {
                newId[order[i].order] = i;
                g.nodes.Add(new GraphNode { id = i, legacyId = order[i].legacy, p = order[i].p });
            }
            foreach (var e in g.edges)
            {
                e.nodeA = newId[endA[e.id]];
                e.nodeB = newId[endB[e.id]];
            }
        }

        // ------------------------------------------------------------------ arms

        static void BuildArms(RoadGraph g, RoadGraphSettings st)
        {
            var ends = new List<(int edge, bool atA)>[g.nodes.Count];
            for (int i = 0; i < ends.Length; i++) ends[i] = new List<(int, bool)>();
            foreach (var e in g.edges)
            {
                ends[e.nodeA].Add((e.id, true));
                ends[e.nodeB].Add((e.id, false));
            }
            foreach (var n in g.nodes)
            {
                var list = new List<GraphArm>();
                foreach (var (edge, atA) in ends[n.id])
                {
                    var e = g.edges[edge];
                    double L = e.Length;
                    double want = Math.Min(Math.Max(st.armSampleMin, 1.5 * e.hw), L);
                    var q = Polyline.At(e.pts, e.s, atA ? want : L - want);
                    var dir = q.XZ - n.p.XZ;
                    if (dir.Length < 1e-9)
                        dir = atA ? e.pts[1].XZ - e.pts[0].XZ : e.pts[e.pts.Length - 2].XZ - e.pts[e.pts.Length - 1].XZ;
                    dir = dir.Normalized;

                    double win = Math.Min(st.gradeWindow, L);
                    double y0 = atA ? e.pts[0].y : e.pts[e.pts.Length - 1].y;
                    double y1 = Polyline.At(e.pts, e.s, atA ? win : L - win).y;
                    double maxG = 0;
                    for (int i = 0; i < e.pts.Length - 1; i++)
                    {
                        double sa = atA ? e.s[i] : L - e.s[i + 1];
                        if (sa >= win) continue;
                        double len = e.s[i + 1] - e.s[i];
                        if (len < 0.5) continue;
                        maxG = Math.Max(maxG, Math.Abs(e.pts[i + 1].y - e.pts[i].y) / len);
                    }
                    list.Add(new GraphArm
                    {
                        node = n.id, edge = edge, atA = atA, sourceRoad = e.sourceRoad, cls = e.cls, hw = e.hw,
                        dir = dir, angle = dir.Angle, length = L, grade = win > 0 ? (y1 - y0) / win : 0, maxGrade = maxG,
                    });
                }
                list.Sort((a, b) =>
                {
                    int c = a.angle.CompareTo(b.angle);
                    if (c != 0) return c;
                    c = a.edge.CompareTo(b.edge);
                    return c != 0 ? c : (a.atA ? 0 : 1).CompareTo(b.atA ? 0 : 1);
                });
                foreach (var a in list)
                {
                    a.id = g.arms.Count;
                    g.arms.Add(a);
                    n.arms.Add(a.id);
                }
                if (list.Count >= 2)
                {
                    double gap = double.MaxValue;
                    for (int i = 0; i < list.Count; i++)
                        gap = Math.Min(gap, Geo.WrapPositive(list[(i + 1) % list.Count].angle - list[i].angle));
                    n.minArmGap = list.Count == 2 ? Math.Min(gap, 2 * Math.PI - gap) : gap;
                }
            }
        }

        // ------------------------------------------------------------------ 5-6 classify

        static void Classify(RoadGraph g, RoadGraphSettings st)
        {
            foreach (var n in g.nodes)
            {
                int deg = n.arms.Count;
                bool dirt = false, paved = false;
                foreach (var id in n.arms)
                {
                    if (g.arms[id].cls == RoadClass.Dirt) dirt = true; else paved = true;
                }
                if (deg <= 1) n.kind = NodeKind.DeadEnd;
                else if (deg == 2)
                {
                    GraphArm a = g.Arm(n, 0), b = g.Arm(n, 1);
                    if (dirt && paved) n.kind = NodeKind.DirtApron;
                    else if (a.cls != b.cls) n.kind = NodeKind.ClassChange;
                    else n.kind = Geo.Bend(a.angle, b.angle) < st.joinDeflectionDeg * Geo.Deg ? NodeKind.Join : NodeKind.Bend;
                }
                else n.kind = dirt && paved ? NodeKind.DirtApron : NodeKind.Junction;
            }
        }

        /// <summary>Join nodes between two pieces of the same road: drop that split and rebuild. True when one was dropped.</summary>
        static bool RemoveSameRoadJoins(RoadGraph g, RoadGraphInput input, List<Split>[] splits)
        {
            bool any = false;
            foreach (var n in g.nodes)
            {
                if (n.kind != NodeKind.Join) continue;
                GraphEdge a = g.edges[g.Arm(n, 0).edge], b = g.edges[g.Arm(n, 1).edge];
                if (a.sourceRoad != b.sourceRoad || a == b) continue;
                double s = a.sEnd == b.sStart ? a.sEnd : b.sEnd == a.sStart ? b.sEnd : double.NaN;
                if (double.IsNaN(s)) continue;
                int ri = input.roads.FindIndex(r => r.id == a.sourceRoad);
                if (splits[ri].RemoveAll(x => x.s == s) > 0) any = true;
            }
            return any;
        }

        static void FindComplexPairs(RoadGraph g, RoadGraphSettings st)
        {
            for (int i = 0; i < g.nodes.Count; i++)
            {
                var a = g.nodes[i];
                if (!a.IsJunctionLike) continue;
                for (int j = i + 1; j < g.nodes.Count; j++)
                {
                    var b = g.nodes[j];
                    if (!b.IsJunctionLike) continue;
                    double d = D2.Distance(a.p.XZ, b.p.XZ);
                    if (d >= st.complexJunctionDistance) continue;
                    g.complexPairs.Add(new ComplexPair { a = a.id, b = b.id, distance = d });
                    a.flags |= NodeFlags.ComplexPair;
                    b.flags |= NodeFlags.ComplexPair;
                }
            }
        }

        // ------------------------------------------------------------------ 7 major road and provisional control

        static void PickMajorsAndControls(RoadGraph g, RoadGraphSettings st)
        {
            foreach (var n in g.nodes)
            {
                n.majorA = n.majorB = -1;
                n.control = Control.None;
                if (n.kind == NodeKind.DeadEnd || n.kind == NodeKind.Bend || n.kind == NodeKind.Join || n.kind == NodeKind.ClassChange) continue;
                int deg = n.arms.Count;
                if (deg < 3)
                {
                    if (n.kind == NodeKind.DirtApron) n.control = Control.MinorStop;
                    continue;
                }
                double bestScore = double.MinValue, bestBend = 0;
                int bi = -1, bj = -1;
                for (int i = 0; i < deg; i++)
                    for (int j = i + 1; j < deg; j++)
                    {
                        GraphArm a = g.Arm(n, i), b = g.Arm(n, j);
                        int ra = RoadClasses.Rank(a.cls), rb = RoadClasses.Rank(b.cls);
                        double bend = Geo.Bend(a.angle, b.angle);
                        double score = 10 * Math.Min(ra, rb) + (ra == rb ? 2 : 0) - bend;
                        if (score > bestScore + 1e-12) { bestScore = score; bestBend = bend; bi = i; bj = j; }
                    }
                n.bestPairBend = bestBend;
                bool straightEnough = bestBend <= st.majorMaxBend;
                bool allSame = true;
                foreach (var id in n.arms) if (g.arms[id].cls != g.Arm(n, 0).cls) allSame = false;

                if (n.kind == NodeKind.DirtApron)
                {
                    n.control = Control.MinorStop;
                    if (straightEnough) { n.majorA = bi; n.majorB = bj; }
                }
                else if ((n.flags & NodeFlags.ComplexPair) != 0)
                {
                    // Unresolved until an override (05 §5.1 step 5): no control yet; keep the class-first pick for the view.
                    if (straightEnough) { n.majorA = bi; n.majorB = bj; }
                }
                else if (deg >= 4 && allSame) n.control = Control.AllWayStop; // §6.2 rule 4; equal classes + all-way stop: no major
                else if (!straightEnough) n.control = Control.AllWayStop;
                else
                {
                    n.control = Control.MinorStop;
                    n.majorA = bi;
                    n.majorB = bj;
                }
            }
        }

        // ------------------------------------------------------------------ provisional corners and mouths (05 §5.3, straight-line)

        static void SolveProvisionalCorners(RoadGraph g, RoadGraphInput input, RoadGraphSettings st)
        {
            foreach (var n in g.nodes)
            {
                int k = n.arms.Count;
                if (k < 2) continue;
                // 05 §1.3 / §7: a node is a tight downtown node when any street/through-road corner's 7.5 m kerb would pass
                // closer than downtownClearance to a building; then all its street/through-road corners take the 6 m radius.
                var clearance = new double[k];
                bool downtownNode = false;
                for (int i = 0; i < k; i++)
                {
                    GraphArm I = g.Arm(n, i), J = g.Arm(n, (i + 1) % k);
                    double theta = Geo.WrapPositive(J.angle - I.angle);
                    clearance[i] = double.NaN;
                    if (theta >= 160 * Geo.Deg || theta <= 0 || n.kind == NodeKind.Bend) continue;
                    if (I.cls != RoadClass.Street && J.cls != RoadClass.Street) continue;
                    clearance[i] = KerbClearance(n.p.XZ, I, J, theta, st.radiusStreetStreet, input.footprints, st.clearanceReach);
                    if (IsStreetThrough(I.cls, J.cls) && clearance[i] < st.downtownClearance) downtownNode = true;
                }
                for (int i = 0; i < k; i++)
                {
                    GraphArm I = g.Arm(n, i), J = g.Arm(n, (i + 1) % k);
                    double theta = Geo.WrapPositive(J.angle - I.angle);
                    if (k == 2 && theta == 0) theta = 2 * Math.PI;
                    var c = new GraphCorner { id = g.corners.Count, node = n.id, armI = I.id, armJ = J.id, opening = theta, centre = n.p.XZ };
                    if (theta >= 200 * Geo.Deg)
                    {
                        c.kind = CornerKind.Miter;
                        c.centre = KerbIntersection(n.p.XZ, I, J) ?? n.p.XZ;
                    }
                    else if (theta >= 160 * Geo.Deg) c.kind = CornerKind.Straight;
                    else
                    {
                        double r = BaseRadius(n, I, J, st);
                        c.clearance = clearance[i];
                        if (downtownNode && IsStreetThrough(I.cls, J.cls))
                        {
                            c.downtown = true;
                            r = st.radiusDowntown;
                        }
                        double sin = Math.Sin(theta), cot = Math.Cos(theta) / sin, k1 = 1 / sin + cot;
                        double tI0 = J.hw / sin + I.hw * cot, tJ0 = I.hw / sin + J.hw * cot;
                        double rMax = (st.maxTangent - Math.Max(tI0, tJ0)) / k1;
                        if (r > rMax) r = rMax;
                        if (r < 0.5)
                        {
                            c.kind = CornerKind.Nose;
                            c.tI = Math.Max(0, tI0);
                            c.tJ = Math.Max(0, tJ0);
                            c.centre = KerbIntersection(n.p.XZ, I, J) ?? n.p.XZ;
                        }
                        else
                        {
                            c.kind = CornerKind.Fillet;
                            c.radius = r;
                            c.tI = tI0 + r * k1;
                            c.tJ = tJ0 + r * k1;
                            c.centre = n.p.XZ + I.dir * c.tI + I.dir.Left * (I.hw + r);
                        }
                    }
                    g.corners.Add(c);
                    n.corners.Add(c.id);
                    if (c.kind == CornerKind.Fillet || c.kind == CornerKind.Nose)
                    {
                        I.tangentL = Math.Max(I.tangentL, c.tI);
                        J.tangentR = Math.Max(J.tangentR, c.tJ);
                    }
                }
            }
            foreach (var a in g.arms)
            {
                double t = Math.Max(a.tangentL, a.tangentR);
                a.mouth = t > 0 ? t + st.mouthPad : 0;
                var far = g.nodes[g.FarNode(a)];
                bool farHasCorners = far.IsJunctionLike || far.kind == NodeKind.Bend;
                a.maxMouth = farHasCorners ? Math.Min(0.45 * a.length, a.length - 10) : a.length - 1;
                a.shortArm = a.mouth > 0 && a.mouth > a.maxMouth;
            }
        }

        static bool IsStreetThrough(RoadClass a, RoadClass b) =>
            (a == RoadClass.Street && RoadClasses.IsThroughRoad(b)) || (b == RoadClass.Street && RoadClasses.IsThroughRoad(a));

        static double BaseRadius(GraphNode n, GraphArm I, GraphArm J, RoadGraphSettings st)
        {
            if (n.kind == NodeKind.Bend) return st.radiusBendInner;
            if (I.cls == RoadClass.Dirt || J.cls == RoadClass.Dirt) return st.radiusDirtPaved;
            if (RoadClasses.IsThroughRoad(I.cls) && RoadClasses.IsThroughRoad(J.cls)) return st.radiusThroughThrough;
            return st.radiusStreetStreet;
        }

        /// <summary>
        /// 05 §1.3's corner measure: the signed distance from the nearest building corner inside the corner's wedge to the
        /// filleted kerb line of radius r (straight-line arms): I's left kerb from reach metres out to its tangent point, the
        /// arc, then J's right kerb out again. Positive = inside the block, negative = the kerb would cut the building.
        /// NaN when no building corner lies in the wedge within reach.
        /// </summary>
        static double KerbClearance(D2 node, GraphArm I, GraphArm J, double theta, double r, List<RoadGraphInput.Footprint> buildings, double reach)
        {
            double sin = Math.Sin(theta), cot = Math.Cos(theta) / sin;
            double tI = (J.hw + r) / sin + (I.hw + r) * cot, tJ = (I.hw + r) / sin + (J.hw + r) * cot;
            D2 C = node + I.dir * tI + I.dir.Left * (I.hw + r);
            D2 TI = node + I.dir * tI + I.dir.Left * I.hw, TJ = node + J.dir * tJ + J.dir.Right * J.hw;
            var kerb = new List<D2> { node + I.dir * Math.Max(reach, tI + 1) + I.dir.Left * I.hw, TI };
            double a0 = (TI - C).Angle, a1 = (TJ - C).Angle;
            double ccw = Geo.WrapPositive(a1 - a0), cw = ccw - 2 * Math.PI;
            double midCcw = D2.Distance(C + D2.FromAngle(a0 + ccw / 2) * r, node), midCw = D2.Distance(C + D2.FromAngle(a0 + cw / 2) * r, node);
            double sweep = midCcw <= midCw ? ccw : cw;
            int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (5 * Geo.Deg)));
            for (int k = 1; k < steps; k++) kerb.Add(C + D2.FromAngle(a0 + sweep * k / steps) * r);
            kerb.Add(TJ);
            kerb.Add(node + J.dir * Math.Max(reach, tJ + 1) + J.dir.Right * J.hw);

            double best = double.NaN;
            foreach (var b in buildings)
                foreach (var p in b.corners)
                {
                    var v = p - node;
                    if (v.Length > reach + 10 || I.dir.Cross(v) <= 0 || J.dir.Cross(v) >= 0) continue;
                    double d = double.MaxValue, side = 0;
                    for (int s = 0; s < kerb.Count - 1; s++)
                    {
                        double ds = Polyline.SegmentDistance(kerb[s], kerb[s + 1], p);
                        if (ds < d) { d = ds; side = (kerb[s + 1] - kerb[s]).Cross(p - kerb[s]); }
                    }
                    double signed = side <= 0 ? d : -d; // the block is to the right of the kerb as traversed
                    if (double.IsNaN(best) || signed < best) best = signed;
                }
            return best;
        }

        /// <summary>Intersection of arm I's left kerb line and arm J's right kerb line (straight-line arms), or null when parallel.</summary>
        static D2? KerbIntersection(D2 node, GraphArm I, GraphArm J)
        {
            D2 a = I.dir, b = J.dir * -1;
            double det = a.Cross(b);
            if (Math.Abs(det) < 1e-9) return null;
            D2 w = J.dir.Right * J.hw - I.dir.Left * I.hw;
            double u = w.Cross(b) / det;
            return node + I.dir.Left * I.hw + I.dir * u;
        }

        // ------------------------------------------------------------------ flags and notes

        static void SetFlagsAndNotes(RoadGraph g, RoadGraphSettings st)
        {
            var inv = CultureInfo.InvariantCulture;
            foreach (var p in g.complexPairs)
                g.notes.Add(string.Format(inv, "complex pair: nodes {0} and {1} are {2:F2} m apart (< {3:F0} m); they need an override before R1 can shape them",
                    p.a, p.b, p.distance, st.complexJunctionDistance));
            foreach (var n in g.nodes)
            {
                foreach (var id in n.arms)
                {
                    var a = g.arms[id];
                    if (a.shortArm)
                    {
                        n.flags |= NodeFlags.ShortArm;
                        g.notes.Add(string.Format(inv, "short arm: node {0} ({1}) arm R{2} is {3:F2} m long; its provisional mouth needs {4:F1} m (room {5:F1} m)",
                            n.id, n.kind, a.sourceRoad, a.length, a.mouth, a.maxMouth));
                    }
                    if (n.IsJunctionLike && Math.Abs(a.grade) > st.steepGrade)
                    {
                        n.flags |= NodeFlags.SteepApproach;
                        g.notes.Add(string.Format(inv, "steep approach: node {0} ({1}) arm R{2} {3:+0.0;-0.0} % over the first {4:F0} m",
                            n.id, n.kind, a.sourceRoad, a.grade * 100, Math.Min(st.gradeWindow, a.length)));
                    }
                }
                if (n.IsJunctionLike && n.minArmGap < st.acuteArmDeg * Geo.Deg)
                {
                    n.flags |= NodeFlags.AcuteArms;
                    g.notes.Add(string.Format(inv, "acute arms: node {0} ({1}) has arms only {2:F1} degrees apart", n.id, n.kind, n.minArmGap / Geo.Deg));
                }
                foreach (var cid in n.corners)
                    if (g.corners[cid].downtown) n.flags |= NodeFlags.Downtown;
            }
        }
    }
}
