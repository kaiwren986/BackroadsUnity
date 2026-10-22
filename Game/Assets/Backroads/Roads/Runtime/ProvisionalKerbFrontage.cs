using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using Unity.Mathematics;

namespace Backroads.Roads
{
    /// <summary>
    /// R0o's stand-in for R2's kerb frontage (sites-roads overview, contract 2), so sites-S1 can start: one face per side
    /// of every edge, its kerb line the centreline offset by the road's half-width on that side, the walk back edge a
    /// further kerb (0.15 m) + walk (1.5 m on streets, 1.8 m on MainStreet, 3.0 m downtown) inside the kerb zone, and no
    /// driveway within the provisional junction mouth + 10 m of a junction or bend. Faces run with the block on their
    /// left (the street on their right): a road's left side in the edge's direction, its right side against it. Face ids
    /// are edge id * 2 (+1 for the right side). R2 replaces all of this with block faces from the kerb loops.
    /// </summary>
    public sealed class ProvisionalKerbFrontage : IKerbFrontageSource
    {
        public const float KerbWidth = 0.15f, KerbHeight = 0.15f, WalkStreet = 1.5f, WalkMain = 1.8f, WalkDowntown = 3.0f, CornerClearance = 10f;

        readonly List<KerbFrontage> m_Faces = new List<KerbFrontage>();
        readonly Dictionary<int, KerbFrontage> m_ById = new Dictionary<int, KerbFrontage>();

        public bool Provisional => true;
        public IReadOnlyList<KerbFrontage> Faces => m_Faces;
        public KerbFrontage Face(int faceId) => m_ById.TryGetValue(faceId, out var f) ? f : null;

        public ProvisionalKerbFrontage(RoadNetworkAsset a)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            foreach (var e in a.edges)
            {
                var r = a.RoadById(e.sourceRoad);
                if (e.cls == RoadClass.Driveway || e.points.Length < 2) continue;
                for (int side = 0; side < 2; side++)
                {
                    var f = Build(a, e, r, side == 1);
                    m_Faces.Add(f);
                    m_ById[f.faceId] = f;
                }
            }
        }

        static KerbFrontage Build(RoadNetworkAsset a, RoadNetworkAsset.Edge e, RoadNetworkAsset.Road r, bool right)
        {
            int n = e.points.Length;
            float hw = r != null ? (right ? r.hwRight : r.hwLeft) : e.halfWidth;
            bool kerbed = r != null && r.inKerbZone && (e.cls == RoadClass.Street || e.cls == RoadClass.MainStreet);
            float walk = !kerbed ? 0f : e.cls == RoadClass.MainStreet ? (r.inDowntown ? WalkDowntown : WalkMain) : WalkStreet;
            var pts = new D3[n];
            for (int i = 0; i < n; i++) pts[i] = new D3(e.points[i].x, e.points[i].y, e.points[i].z);

            var kerb = new double2[n];
            var back = new double2[n];
            var y = new float[n];
            for (int k = 0; k < n; k++)
            {
                // the face runs against the edge on the right side
                int i = right ? n - 1 - k : k;
                var nl = Polyline.MiterLeft(pts, i);
                var dir = right ? nl * -1 : nl;
                var c = pts[i].XZ;
                var kp = c + dir * hw;
                var bp = c + dir * (hw + (kerbed ? KerbWidth + walk : 0));
                kerb[k] = new double2(kp.x, kp.z);
                back[k] = new double2(bp.x, bp.z);
                y[k] = (float)pts[i].y;
            }
            var s = new float[n];
            for (int k = 1; k < n; k++) s[k] = s[k - 1] + (float)math.distance(kerb[k - 1], kerb[k]);
            float L = s[n - 1];
            var top = new float[n];
            var walkY = new float[n];
            for (int k = 0; k < n; k++)
            {
                top[k] = y[k] + (kerbed ? KerbHeight : 0f);
                walkY[k] = top[k];
            }

            // provisional corner returns at junction-like ends and bends: the arm's mouth + 10 m
            var forbidden = new List<ForbiddenInterval>();
            int startNode = right ? e.nodeB : e.nodeA, endNode = right ? e.nodeA : e.nodeB;
            float startClear = Clearance(a, startNode, e.id, startNode == e.nodeA), endClear = Clearance(a, endNode, e.id, endNode == e.nodeA);
            if (startClear > 0) forbidden.Add(new ForbiddenInterval { s0 = 0, s1 = Math.Min(L, startClear), reason = FrontageBlock.CornerReturn });
            if (endClear > 0) forbidden.Add(new ForbiddenInterval { s0 = Math.Max(0, L - endClear), s1 = L, reason = FrontageBlock.CornerReturn });

            return new KerbFrontage
            {
                faceId = e.id * 2 + (right ? 1 : 0),
                osmWay = r != null && r.osmWays.Length > 0 ? r.osmWays[0] : "",
                kerb = kerb, s = s, topOfKerb = top, gutter = y, walkBack = back, walkBackY = walkY,
                kerbKind = kerbed ? KerbKind.Barrier150 : KerbKind.None,
                streetClass = (StreetClass)(byte)e.cls,
                speedKmh = r != null ? r.speedKmh : 0,
                oneway = r != null && r.oneway != 0,
                forbidden = forbidden.ToArray(),
                provisional = true,
            };
        }

        static float Clearance(RoadNetworkAsset a, int node, int edge, bool atA)
        {
            var n = a.nodes[node];
            if (n.armCount < 3 && n.kind != NodeKind.Bend && n.kind != NodeKind.DirtApron) return 0;
            for (int k = 0; k < n.armCount; k++)
            {
                var arm = a.arms[n.firstArm + k];
                if (arm.edge == edge && arm.atA == atA) return arm.mouth + CornerClearance;
            }
            return CornerClearance;
        }
    }
}
