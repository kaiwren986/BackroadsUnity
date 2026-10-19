using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace Backroads.Roads
{
    /// <summary>
    /// The baked road network (R0 of 05 §9): the cleaned graph of 05 §5.1 with arms, provisional corners and mouths, in Unity
    /// space. Written by Backroads.Roads.Editor.RoadNetworkBaker into Assets/Backroads/Roads/Baked/Resources/ (committed,
    /// hash-checked by EditMode Roads.BakedAssetUpToDate); never generated at run time. Ground-plane vectors are stored as
    /// Vector2(x, z). The schema is frozen only at M3 (PLAN 1.10), so R1-R4 may still extend it.
    /// </summary>
    public sealed class RoadNetworkAsset : ScriptableObject
    {
        public const string ResourceName = "BrRoadNetwork";
        public const int FormatVersion = 1;

        [Serializable]
        public struct Node
        {
            public int id;
            /// <summary>traffic.json node id (05's node numbers), or -1 for nodes the legacy network did not have.</summary>
            public int legacyId;
            public Vector3 position;
            public NodeKind kind;
            public Control control;
            public NodeFlags flags;
            public int firstArm, armCount, firstCorner, cornerCount;
            /// <summary>Through road as positions within this node's arms (0..armCount-1), or -1.</summary>
            public int majorA, majorB;
            public float bestPairBendDeg, minArmGapDeg;
            public bool HasMajor => majorA >= 0;
        }

        [Serializable]
        public struct Arm
        {
            public int id, node, edge, sourceRoad;
            public bool atA;
            public RoadClass cls;
            public float halfWidth;
            /// <summary>Unit direction (x, z) away from the node and its angle atan2(z, x) in degrees (counter-clockwise from above).</summary>
            public Vector2 dir;
            public float angleDeg, length, grade, maxGrade;
            /// <summary>Provisional (straight-line) tangent distances and mouth, metres from the node along the arm.</summary>
            public float tangentL, tangentR, mouth, maxMouth;
            public bool shortArm;
        }

        [Serializable]
        public struct Corner
        {
            public int id, node, armI, armJ;
            public CornerKind kind;
            public float openingDeg, radius, tI, tJ;
            /// <summary>Building clearance of a 7.5 m kerb at this corner (05 §1.3), metres, when hasClearance.</summary>
            public float clearance;
            public bool hasClearance;
            public Vector2 centre;
            public bool downtown;
        }

        [Serializable]
        public sealed class Edge
        {
            public int id, nodeA, nodeB, sourceRoad;
            public RoadClass cls;
            public float halfWidth;
            /// <summary>Where this piece starts and ends on its canon road (world.json d).</summary>
            public double sStart, sEnd;
            public float length;
            /// <summary>Centreline from nodeA to nodeB (canon heights) and its 2D arc length.</summary>
            public Vector3[] points;
            public float[] s;
        }

        [Serializable]
        public struct Pair
        {
            public int a, b;
            public float distance;
        }

        [Header("Identity")]
        public int formatVersion = FormatVersion;
        public int builderVersion;
        /// <summary>SHA-256 of CanonData/v1/manifest.json the bake read.</summary>
        public string canonManifestSha256;
        /// <summary>SHA-256 of the content below (ComputeHash), written by the bake.</summary>
        public string contentHash;
        public int inputRoads, inputPoints;

        [Header("Graph")]
        public Node[] nodes = Array.Empty<Node>();
        public Arm[] arms = Array.Empty<Arm>();
        public Corner[] corners = Array.Empty<Corner>();
        public Edge[] edges = Array.Empty<Edge>();

        [Header("Findings")]
        public Pair[] complexPairs = Array.Empty<Pair>();
        /// <summary>The town kerb zone outline (street hull + buffer), (x, z).</summary>
        public Vector2[] townZone = Array.Empty<Vector2>();
        public string[] notes = Array.Empty<string>();

        public static RoadNetworkAsset Load() => Resources.Load<RoadNetworkAsset>(ResourceName);

        public Arm ArmOf(in Node n, int k) => arms[n.firstArm + k];

        public int CountKind(NodeKind k)
        {
            int c = 0;
            foreach (var n in nodes) if (n.kind == k) c++;
            return c;
        }

        /// <summary>Fills this asset from a built graph (float conversion) and stamps the content hash.</summary>
        public void SetFrom(RoadGraph g)
        {
            formatVersion = FormatVersion;
            builderVersion = RoadGraphBuilder.Version;
            canonManifestSha256 = g.canonManifestSha256 ?? "";
            inputRoads = g.inputRoads;
            inputPoints = g.inputPoints;

            nodes = new Node[g.nodes.Count];
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = g.nodes[i];
                nodes[i] = new Node
                {
                    id = n.id, legacyId = n.legacyId, position = V3(n.p), kind = n.kind, control = n.control, flags = n.flags,
                    firstArm = n.arms.Count > 0 ? n.arms[0] : 0, armCount = n.arms.Count,
                    firstCorner = n.corners.Count > 0 ? n.corners[0] : 0, cornerCount = n.corners.Count,
                    majorA = n.majorA, majorB = n.majorB,
                    bestPairBendDeg = double.IsNaN(n.bestPairBend) ? -1 : (float)(n.bestPairBend / Geo.Deg),
                    minArmGapDeg = double.IsNaN(n.minArmGap) ? -1 : (float)(n.minArmGap / Geo.Deg),
                };
                for (int k = 1; k < n.arms.Count; k++)
                    if (n.arms[k] != n.arms[0] + k) throw new InvalidOperationException($"node {n.id}: arms are not contiguous");
                for (int k = 1; k < n.corners.Count; k++)
                    if (n.corners[k] != n.corners[0] + k) throw new InvalidOperationException($"node {n.id}: corners are not contiguous");
            }
            arms = new Arm[g.arms.Count];
            for (int i = 0; i < arms.Length; i++)
            {
                var a = g.arms[i];
                arms[i] = new Arm
                {
                    id = a.id, node = a.node, edge = a.edge, sourceRoad = a.sourceRoad, atA = a.atA, cls = a.cls, halfWidth = (float)a.hw,
                    dir = V2(a.dir), angleDeg = (float)(a.angle / Geo.Deg), length = (float)a.length, grade = (float)a.grade, maxGrade = (float)a.maxGrade,
                    tangentL = (float)a.tangentL, tangentR = (float)a.tangentR, mouth = (float)a.mouth, maxMouth = (float)a.maxMouth, shortArm = a.shortArm,
                };
            }
            corners = new Corner[g.corners.Count];
            for (int i = 0; i < corners.Length; i++)
            {
                var c = g.corners[i];
                corners[i] = new Corner
                {
                    id = c.id, node = c.node, armI = c.armI, armJ = c.armJ, kind = c.kind, openingDeg = (float)(c.opening / Geo.Deg),
                    radius = (float)c.radius, tI = (float)c.tI, tJ = (float)c.tJ, centre = V2(c.centre), downtown = c.downtown,
                    hasClearance = !double.IsNaN(c.clearance), clearance = double.IsNaN(c.clearance) ? 0 : (float)c.clearance,
                };
            }
            edges = new Edge[g.edges.Count];
            for (int i = 0; i < edges.Length; i++)
            {
                var e = g.edges[i];
                var pts = new Vector3[e.pts.Length];
                var s = new float[e.pts.Length];
                for (int k = 0; k < pts.Length; k++) { pts[k] = V3(e.pts[k]); s[k] = (float)e.s[k]; }
                edges[i] = new Edge
                {
                    id = e.id, nodeA = e.nodeA, nodeB = e.nodeB, sourceRoad = e.sourceRoad, cls = e.cls, halfWidth = (float)e.hw,
                    sStart = e.sStart, sEnd = e.sEnd, length = (float)e.Length, points = pts, s = s,
                };
            }
            complexPairs = new Pair[g.complexPairs.Count];
            for (int i = 0; i < complexPairs.Length; i++)
                complexPairs[i] = new Pair { a = g.complexPairs[i].a, b = g.complexPairs[i].b, distance = (float)g.complexPairs[i].distance };
            var outline = g.zone != null && g.zone.Hull.Length >= 3 ? g.zone.Outline() : new System.Collections.Generic.List<D2>();
            townZone = new Vector2[outline.Count];
            for (int i = 0; i < townZone.Length; i++) townZone[i] = V2(outline[i]);
            notes = g.notes.ToArray();
            contentHash = ComputeHash();
        }

        static Vector3 V3(D3 p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
        static Vector2 V2(D2 p) => new Vector2((float)p.x, (float)p.z);

        /// <summary>SHA-256 (hex) over every content field in a fixed binary order; identity fields other than the versions are left out.</summary>
        public string ComputeHash()
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(formatVersion); w.Write(builderVersion); w.Write(inputRoads); w.Write(inputPoints);
                w.Write(nodes.Length);
                foreach (var n in nodes)
                {
                    w.Write(n.id); w.Write(n.legacyId); W(w, n.position); w.Write((byte)n.kind); w.Write((byte)n.control); w.Write((ushort)n.flags);
                    w.Write(n.firstArm); w.Write(n.armCount); w.Write(n.firstCorner); w.Write(n.cornerCount); w.Write(n.majorA); w.Write(n.majorB);
                    w.Write(n.bestPairBendDeg); w.Write(n.minArmGapDeg);
                }
                w.Write(arms.Length);
                foreach (var a in arms)
                {
                    w.Write(a.id); w.Write(a.node); w.Write(a.edge); w.Write(a.sourceRoad); w.Write(a.atA); w.Write((byte)a.cls); w.Write(a.halfWidth);
                    W(w, a.dir); w.Write(a.angleDeg); w.Write(a.length); w.Write(a.grade); w.Write(a.maxGrade);
                    w.Write(a.tangentL); w.Write(a.tangentR); w.Write(a.mouth); w.Write(a.maxMouth); w.Write(a.shortArm);
                }
                w.Write(corners.Length);
                foreach (var c in corners)
                {
                    w.Write(c.id); w.Write(c.node); w.Write(c.armI); w.Write(c.armJ); w.Write((byte)c.kind); w.Write(c.openingDeg);
                    w.Write(c.radius); w.Write(c.tI); w.Write(c.tJ); W(w, c.centre); w.Write(c.downtown); w.Write(c.hasClearance); w.Write(c.clearance);
                }
                w.Write(edges.Length);
                foreach (var e in edges)
                {
                    w.Write(e.id); w.Write(e.nodeA); w.Write(e.nodeB); w.Write(e.sourceRoad); w.Write((byte)e.cls); w.Write(e.halfWidth);
                    w.Write(e.sStart); w.Write(e.sEnd); w.Write(e.length);
                    w.Write(e.points.Length);
                    foreach (var p in e.points) W(w, p);
                    foreach (var s in e.s) w.Write(s);
                }
                w.Write(complexPairs.Length);
                foreach (var p in complexPairs) { w.Write(p.a); w.Write(p.b); w.Write(p.distance); }
                w.Write(townZone.Length);
                foreach (var p in townZone) W(w, p);
                w.Write(notes.Length);
                foreach (var s in notes) w.Write(s ?? "");
                w.Flush();
                using (var sha = SHA256.Create())
                {
                    var h = sha.ComputeHash(ms.ToArray());
                    var sb = new System.Text.StringBuilder(64);
                    foreach (var b in h) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
        }

        static void W(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void W(BinaryWriter w, Vector2 v) { w.Write(v.x); w.Write(v.y); }
    }
}
