using System;
using System.Collections.Generic;
using System.Globalization;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Roads
{
    /// <summary>
    /// roads-R2 step 5 (overview contract 3): lane S asks for driveways on the kerb frontage, lane R answers with cuts.
    /// Requests are handled sorted by (faceId, s0, id) and the answer has one cut per request in the request list's order,
    /// so the same requests give the same cuts whatever order they come in. Each throat must clear the face's forbidden
    /// intervals: it moves by up to CUT_SHIFT_MAX (3 m, the smaller move first, forwards on a tie) or is rejected with the
    /// interval's reason. A mapped driveway (OSM) moves only off a corner return; it overrides the other intervals.
    /// Requests whose throats or flares meet merge into one cut (a shared driveway): the first by (s0, id) keeps it, the
    /// others answer Merged with mergedWith = its request id. Kerbed faces: Dropped25 over the throat with linear flares
    /// (1.0 m residential, 1.5 m commercial and fuel, 3 m truck, or the request's own), the walk crossing at its 1.5 %
    /// cross fall from the dropped kerb, the apron slab across the walk to the lot edge (the walk back edge). Unkerbed
    /// faces: the apron spans the 1.2 m shoulder, widened by the flares or radius returns (6-12 m for trucks), level
    /// with the pavement edge.
    /// </summary>
    public sealed class DrivewayCutter : IDrivewayCutter
    {
        public const float CutShiftMax = 3f;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public float residentialFlare = 1.0f, commercialFlare = 1.5f, truckFlare = 3.0f, fieldFlare = 1.0f;
        /// <summary>Unkerbed roads: default radius returns for trucks and fields (m).</summary>
        public float truckReturn = 9f, fieldReturn = 6f;
        public float shoulder = 1.2f, kerbTop = 0.15f, dropped = 0.025f, crossSlope = 0.015f;

        readonly IKerbFrontageSource m_Src;
        readonly Dictionary<int, (float flare, bool returns, SiteSurface surface)> m_Last = new Dictionary<int, (float, bool, SiteSurface)>();

        /// <summary>After Resolve: the flare (or return radius) and apron surface a baked, moved or merged request's cut was built with (road plates read them).</summary>
        public bool CutShape(int requestId, out float flare, out bool returns, out SiteSurface surface)
        {
            bool ok = m_Last.TryGetValue(requestId, out var v);
            flare = v.flare; returns = v.returns; surface = v.surface;
            return ok;
        }

        public DrivewayCutter(IKerbFrontageSource frontage)
        {
            m_Src = frontage ?? throw new ArgumentNullException(nameof(frontage));
        }

        sealed class Group
        {
            public int primary, face;
            public float s0, s1, flare;
            public bool mapped, kerbed, returns;
            public DrivewayRequest req;
        }

        public DrivewayCut[] Resolve(IReadOnlyList<DrivewayRequest> requests)
        {
            var cuts = new DrivewayCut[requests.Count];
            var order = new List<int>(requests.Count);
            for (int i = 0; i < requests.Count; i++) order.Add(i);
            order.Sort((a, b) =>
            {
                var A = requests[a];
                var B = requests[b];
                int r = A.faceId.CompareTo(B.faceId);
                if (r != 0) return r;
                r = A.s0.CompareTo(B.s0);
                if (r != 0) return r;
                r = A.id.CompareTo(B.id);
                return r != 0 ? r : a.CompareTo(b);
            });
            var groups = new List<Group>();
            var groupOf = new int[requests.Count];
            for (int i = 0; i < groupOf.Length; i++) groupOf[i] = -1;

            foreach (int i in order)
            {
                var r = requests[i];
                var cut = new DrivewayCut { requestId = r.id, faceId = r.faceId, kerb = KerbKind.Dropped25, mergedWith = -1 };
                cuts[i] = cut;
                var f = m_Src.Face(r.faceId);
                if (f == null) { Reject(cut, FrontageBlock.Other, string.Format(Inv, "no face {0}", r.faceId)); continue; }
                float L = f.Length;
                if (!(r.s1 > r.s0)) { Reject(cut, FrontageBlock.Other, "empty throat (s1 <= s0)"); continue; }
                if (r.s1 - r.s0 > L) { Reject(cut, FrontageBlock.Other, string.Format(Inv, "a {0:F1} m throat on a {1:F1} m face", r.s1 - r.s0, L)); continue; }
                bool kerbed = f.kerbKind != KerbKind.None;
                bool returns = !kerbed && (r.returnRadius > 0 || ((r.kind == DrivewayKind.Truck || r.kind == DrivewayKind.Field) && r.flareLength <= 0));
                float flare = Flare(r, kerbed);

                if (!Place(f, r.s0, r.s1, r.mapped, out float shift, out var blocker))
                {
                    Reject(cut, blocker.reason, string.Format(Inv, "{0} {1:F1}-{2:F1} m blocks the throat {3:F1}-{4:F1} m and no shift up to {5:F0} m clears it{6}",
                        blocker.reason, blocker.s0, blocker.s1, r.s0, r.s1, CutShiftMax, r.mapped ? " (mapped: moves only off a corner return)" : ""));
                    continue;
                }
                float s0 = r.s0 + shift, s1 = r.s1 + shift;
                cut.shift = shift;
                cut.outcome = shift != 0 ? CutOutcome.Moved : CutOutcome.Baked;

                // a shared driveway: throats or flares meet
                Group into = null;
                for (int k = groups.Count - 1; k >= 0; k--)
                {
                    var g = groups[k];
                    if (g.face != r.faceId) break;
                    if (s0 < g.s1 + g.flare + flare && s1 > g.s0 - g.flare - flare) { into = g; break; }
                }
                if (into != null)
                {
                    float u0 = Math.Min(into.s0, s0), u1 = Math.Max(into.s1, s1);
                    bool mappedBoth = into.mapped || r.mapped;
                    if (Clear(f, u0, u1, mappedBoth, out var across))
                    {
                        into.s0 = u0;
                        into.s1 = u1;
                        into.flare = Math.Max(into.flare, flare);
                        into.returns |= returns;
                        cut.outcome = CutOutcome.Merged;
                        cut.mergedWith = requests[into.primary].id;
                        groupOf[i] = groups.IndexOf(into);
                        continue;
                    }
                    if (s0 < into.s1 && s1 > into.s0)
                    {
                        Reject(cut, across.reason, string.Format(Inv, "overlaps request {0} across {1} {2:F1}-{3:F1} m", requests[into.primary].id, across.reason, across.s0, across.s1));
                        continue;
                    }
                }
                groups.Add(new Group { primary = i, face = r.faceId, s0 = s0, s1 = s1, flare = flare, mapped = r.mapped, kerbed = kerbed, returns = returns, req = r });
                groupOf[i] = groups.Count - 1;
            }

            // geometry once per group, shared by its members
            m_Last.Clear();
            for (int i = 0; i < cuts.Length; i++)
            {
                if (groupOf[i] < 0) continue;
                var g = groups[groupOf[i]];
                m_Last[requests[i].id] = (g.flare, g.returns, g.req.surface);
                var f = m_Src.Face(g.face);
                var c = cuts[i];
                c.s0 = g.s0;
                c.s1 = g.s1;
                Geometry(f, g, c);
            }
            return cuts;
        }

        static void Reject(DrivewayCut c, FrontageBlock why, string reason)
        {
            c.outcome = CutOutcome.Rejected;
            c.rejectedBy = why;
            c.reason = reason;
            c.apron = Array.Empty<double2>();
        }

        float Flare(in DrivewayRequest r, bool kerbed)
        {
            if (r.returnRadius > 0) return r.returnRadius;
            if (r.flareLength > 0) return r.flareLength;
            switch (r.kind)
            {
                case DrivewayKind.Truck: return kerbed ? truckFlare : truckReturn;
                case DrivewayKind.Field: return kerbed ? fieldFlare : fieldReturn;
                case DrivewayKind.Commercial:
                case DrivewayKind.FuelIn:
                case DrivewayKind.FuelOut: return commercialFlare;
                default: return residentialFlare;
            }
        }

        /// <summary>True when [s0, s1] lies on the face and clear of the intervals that bind it (mapped: corner returns only).</summary>
        static bool Clear(KerbFrontage f, float s0, float s1, bool mapped, out ForbiddenInterval blocker)
        {
            blocker = new ForbiddenInterval { s0 = 0, s1 = 0, reason = FrontageBlock.Other };
            if (s0 < -1e-4f || s1 > f.Length + 1e-4f) { blocker = new ForbiddenInterval { s0 = s0 < 0 ? s0 : f.Length, s1 = s0 < 0 ? 0 : s1, reason = FrontageBlock.Other }; return false; }
            if (f.forbidden == null) return true;
            foreach (var iv in f.forbidden)
            {
                if (mapped && iv.reason != FrontageBlock.CornerReturn) continue;
                if (s0 < iv.s1 && s1 > iv.s0) { blocker = iv; return false; }
            }
            return true;
        }

        /// <summary>The smallest shift (|shift| at most CUT_SHIFT_MAX, forwards on a tie) that clears the throat; false with the first blocker otherwise.</summary>
        static bool Place(KerbFrontage f, float s0, float s1, bool mapped, out float shift, out ForbiddenInterval blocker)
        {
            shift = 0;
            if (Clear(f, s0, s1, mapped, out blocker)) return true;
            var first = blocker;
            var candidates = new List<float> { -s0, f.Length - s1 };
            if (f.forbidden != null)
                foreach (var iv in f.forbidden)
                {
                    if (mapped && iv.reason != FrontageBlock.CornerReturn) continue;
                    candidates.Add(iv.s1 - s0);
                    candidates.Add(iv.s0 - s1);
                }
            float best = float.NaN;
            foreach (var c in candidates)
            {
                if (Math.Abs(c) > CutShiftMax + 1e-4f) continue;
                if (!Clear(f, s0 + c, s1 + c, mapped, out _)) continue;
                if (float.IsNaN(best) || Math.Abs(c) < Math.Abs(best) - 1e-6f || (Math.Abs(Math.Abs(c) - Math.Abs(best)) <= 1e-6f && c > best)) best = c;
            }
            blocker = first;
            if (float.IsNaN(best)) return false;
            shift = best;
            return true;
        }

        void Geometry(KerbFrontage f, Group g, DrivewayCut c)
        {
            float a = g.s0, b = g.s1;
            if (g.kerbed)
            {
                c.kerb = KerbKind.Dropped25;
                // across the walk over the throat: kerb line forwards, walk back edge backwards (block on the left: CCW)
                var front = Sample(f, a, b, false);
                var back = Sample(f, a, b, true);
                var apron = new List<double2>(front);
                for (int k = back.Count - 1; k >= 0; k--) apron.Add(back[k]);
                c.apron = apron.ToArray();
                c.crossSlope = crossSlope;
                c.lotEdgeA = At(f, a, true);
                c.lotEdgeB = At(f, b, true);
                c.lotEdgeYA = LotY(f, a);
                c.lotEdgeYB = LotY(f, b);
                return;
            }
            // unkerbed: the apron spans the shoulder; flares or radius returns widen it at the pavement edge
            c.kerb = KerbKind.None;
            // a radius return of R tangent to the pavement edge at a - R reaches the shoulder's outer edge (depth D) at
            // a - (R - sqrt(R^2 - (R - D)^2)); linear flares taper from a - R at the edge to a at the lot edge
            float R = g.flare, D = shoulder;
            float inset = g.returns && R > D ? R - (float)Math.Sqrt(R * R - (R - D) * (R - D)) : 0;
            float e0 = Math.Max(0, a - R), e1 = Math.Min(f.Length, b + R);
            float l0 = Math.Max(0, a - inset), l1 = Math.Min(f.Length, b + inset);
            var edge = Sample(f, e0, e1, false);
            var lot = Offset(f, l0, l1, D);
            var poly = new List<double2>(edge);
            for (int k = lot.Count - 1; k >= 0; k--) poly.Add(lot[k]);
            c.apron = poly.ToArray();
            c.crossSlope = 0;
            c.lotEdgeA = lot[0];
            c.lotEdgeB = lot[lot.Count - 1];
            c.lotEdgeYA = Interp(f, f.gutter, l0);
            c.lotEdgeYB = Interp(f, f.gutter, l1);
        }

        /// <summary>The walk back edge height across a dropped kerb: the dropped kerb top plus the walk's cross fall over the walk.</summary>
        float LotY(KerbFrontage f, float s)
        {
            float gutter = Interp(f, f.gutter, s);
            var k = At(f, s, false);
            var w = At(f, s, true);
            float depth = (float)math.distance(k, w);
            return gutter + dropped + crossSlope * Math.Max(0, depth - kerbTop);
        }

        /// <summary>Points of the kerb line (or the walk back edge) from a to b: the ends and every station between.</summary>
        static List<double2> Sample(KerbFrontage f, float a, float b, bool back)
        {
            var o = new List<double2> { At(f, a, back) };
            for (int i = 0; i < f.s.Length; i++) if (f.s[i] > a + 1e-4f && f.s[i] < b - 1e-4f) o.Add(back ? f.walkBack[i] : f.kerb[i]);
            o.Add(At(f, b, back));
            return o;
        }

        /// <summary>The kerb line offset into the block by d (unkerbed faces: the shoulder's outer edge), from a to b.</summary>
        static List<double2> Offset(KerbFrontage f, float a, float b, float d)
        {
            var o = new List<double2>();
            void Add(float s) { var p = At(f, s, false); o.Add(p + Normal(f, s) * d); }
            Add(a);
            for (int i = 0; i < f.s.Length; i++) if (f.s[i] > a + 1e-4f && f.s[i] < b - 1e-4f) Add(f.s[i]);
            Add(b);
            return o;
        }

        /// <summary>The left (block-side) unit normal of the kerb segment holding s.</summary>
        static double2 Normal(KerbFrontage f, float s)
        {
            int i = Seg(f, s, out _);
            var d = math.normalizesafe(f.kerb[i + 1] - f.kerb[i]);
            return new double2(-d.y, d.x);
        }

        static int Seg(KerbFrontage f, float s, out float t)
        {
            int n = f.s.Length;
            if (n < 2) { t = 0; return 0; }
            int lo = 0, hi = n - 1;
            if (s <= f.s[0]) { t = 0; return 0; }
            if (s >= f.s[n - 1]) { t = 1; return n - 2; }
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (f.s[m] <= s) lo = m; else hi = m; }
            float l = f.s[hi] - f.s[lo];
            t = l > 0 ? (s - f.s[lo]) / l : 0;
            return lo;
        }

        static double2 At(KerbFrontage f, float s, bool back)
        {
            int i = Seg(f, s, out float t);
            var p = back ? f.walkBack : f.kerb;
            return p[i] + (p[i + 1] - p[i]) * t;
        }

        static float Interp(KerbFrontage f, float[] v, float s)
        {
            int i = Seg(f, s, out float t);
            return v[i] + (v[i + 1] - v[i]) * t;
        }
    }
}
