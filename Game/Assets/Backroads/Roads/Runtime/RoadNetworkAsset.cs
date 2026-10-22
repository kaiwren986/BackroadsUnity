using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.Serialization;

namespace Backroads.Roads
{
    /// <summary>
    /// The baked road network (R0 of 05 §9; roads-R0o for towns): the cleaned graph of 05 §5.1 with arms, provisional
    /// corners and mouths, in Unity space. Written by Backroads.Roads.Editor.RoadNetworkBaker into
    /// Assets/Backroads/Roads/Baked/Resources/ (committed, hash-checked by EditMode Roads.BakedAssetUpToDate and
    /// OsmRoads.TownAssetUpToDate); never generated at run time. Two assets: BrRoadNetwork (the canon test world) and
    /// BrRoadNetwork_(town) (a TownData town). Ground-plane vectors are stored as Vector2(x, z). The schema is frozen
    /// only at M3 (PLAN 1.10), so R1-R4 may still extend it.
    /// Format 2 (roads-R0o): the road table (OSM ways, tags, cross sections), per-approach controls with their source,
    /// mapped features, the downtown zone, the play-area edge cuts, mapped driveways and steep stretches.
    /// Format 3 (roads-R1): OSM node ids, per-approach mouth / stop line / crosswalk distances, the junction surfaces
    /// (outline, height mode, corners with radius and reason) and the curb inlets. The meshes are baked by R3.
    /// </summary>
    public sealed class RoadNetworkAsset : ScriptableObject
    {
        public const string ResourceName = "BrRoadNetwork";
        public const int FormatVersion = 3;

        /// <summary>The Resources name of a town's network (TownData/(town)/v1).</summary>
        public static string TownResourceName(string townId) => ResourceName + "_" + townId;

        [Serializable]
        public struct Node
        {
            public int id;
            /// <summary>traffic.json node id (05's node numbers), or -1 for nodes the legacy network did not have.</summary>
            public int legacyId;
            /// <summary>The OSM node here (towns), or 0: the key of roads-overrides.</summary>
            public long osmNode;
            public Vector3 position;
            public NodeKind kind;
            public Control control;
            public ControlSource controlSource;
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
            /// <summary>What traffic arriving along this arm must do, where it came from, and the mapped sign (OSM node, metres from the node).</summary>
            public Control control;
            public ControlSource controlSource;
            public long controlOsmNode;
            public float controlDistance;
            /// <summary>R1, metres from the node: the mouth (where the ribbon meets the junction surface), the stop line centre and
            /// the crosswalk band (-1 when none); the stop line moved to a flatter spot.</summary>
            public float sMouth, sStop, sCwInner, sCwOuter;
            public bool crosswalk, stopMoved;
        }

        /// <summary>An R1 corner of a junction surface (arms as positions in the node's arm list; armJ is the next counter-clockwise).</summary>
        [Serializable]
        public struct SurfaceCorner
        {
            public int armI, armJ;
            public CornerKind kind;
            public float baseRadius, radius;
            public CornerReason reason;
            public Vector2 centre;
            public float clearance;
            public bool hasClearance;
            public Vector2[] arc;
        }

        /// <summary>An R1 junction surface: the outline (CCW, local metres), how it gets its heights, its corners.</summary>
        [Serializable]
        public sealed class Surface
        {
            public int node;
            public SurfaceMode mode;
            public Vector2[] outline = Array.Empty<Vector2>();
            public float area, corridorArea;
            /// <summary>Plane mode: y = planeA x + planeB z + planeC.</summary>
            public float planeA, planeB, planeC;
            public SurfaceCorner[] corners = Array.Empty<SurfaceCorner>();
            public int triangles;
            public string problem = "";
        }

        [Serializable]
        public struct InletProp
        {
            public Vector3 position;
            public int edge, node;
            public string why;
        }

        /// <summary>A road the edges are pieces of (sourceRoad): canon roads carry only id, class and length; town roads their OSM data.</summary>
        [Serializable]
        public sealed class Road
        {
            public int id;
            public RoadClass cls;
            public float length;
            public string[] osmWays = Array.Empty<string>();
            public string highway = "", service = "";
            /// <summary>Cross section: centreline to pavement edge (kerb face) left and right of the direction of travel along the points.</summary>
            public float hwLeft, hwRight, laneWidth, parkingLeft, parkingRight, gutter;
            public int lanes;
            /// <summary>"width", "lanes" or "class" (which rule set the width); "" for the canon.</summary>
            public string widthSource = "";
            /// <summary>+1 one-way along the points, -1 against them, 0 both ways.</summary>
            public int oneway;
            public bool bridge, tunnel, roundabout;
            public int layer;
            public string surface = "", sidewalk = "", parking = "";
            public float speedKmh;
            public bool inKerbZone, inDowntown, cutStart, cutEnd;
            /// <summary>A mapped building stands within one lane of the centreline (a map conflict no width clears).</summary>
            public bool buildingConflict;
        }

        /// <summary>A mapped sign, signal or crossing on an edge (s metres from nodeA); node and arm are what it controls, or -1.</summary>
        [Serializable]
        public struct Feature
        {
            public RoadFeatureKind kind;
            public long osmNode;
            public int edge;
            public float s;
            public int direction, node, arm;
            public string crossing, problem;
        }

        /// <summary>A road cut at the play-area edge: where, on which side ("N", "E", "S", "W"), and how far its next node lies beyond the cut.</summary>
        [Serializable]
        public struct Cut
        {
            public int road;
            public string osmWay;
            public Vector2 at;
            public string side;
            public float beyond;
        }

        [Serializable]
        public struct Driveway
        {
            public string osmWay, service;
            public long osmNode;
            public Vector2 at;
            public int road;
            public float s;
        }

        [Serializable]
        public struct Steep
        {
            public int road;
            public Vector2 at;
            public float length, maxGrade, realGrade;
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
        public int builderVersion, importerVersion, surfaceVersion;
        /// <summary>What the bake read: "CanonData/v1" or "TownData/(town)/v1", and the SHA-256 of its manifest.json.</summary>
        public string source = "";
        [FormerlySerializedAs("canonManifestSha256")]
        public string sourceManifestSha256;
        /// <summary>SHA-256 of the content below (ComputeHash), written by the bake.</summary>
        public string contentHash;
        public int inputRoads, inputPoints;

        [Header("Graph")]
        public Node[] nodes = Array.Empty<Node>();
        public Arm[] arms = Array.Empty<Arm>();
        public Corner[] corners = Array.Empty<Corner>();
        public Edge[] edges = Array.Empty<Edge>();
        public Road[] roads = Array.Empty<Road>();
        public Feature[] features = Array.Empty<Feature>();

        [Header("Surfaces (R1)")]
        public Surface[] surfaces = Array.Empty<Surface>();
        public InletProp[] inlets = Array.Empty<InletProp>();

        [Header("Findings")]
        public Pair[] complexPairs = Array.Empty<Pair>();
        /// <summary>The town kerb zone outline (canon: street hull + buffer; towns: KERB_ZONE), (x, z).</summary>
        public Vector2[] townZone = Array.Empty<Vector2>();
        /// <summary>DOWNTOWN_ZONE (towns), (x, z).</summary>
        public Vector2[] downtownZone = Array.Empty<Vector2>();
        public Cut[] edgeCuts = Array.Empty<Cut>();
        public Driveway[] mappedDriveways = Array.Empty<Driveway>();
        public Steep[] steep = Array.Empty<Steep>();
        public string[] notes = Array.Empty<string>();

        /// <summary>The canon test world's network.</summary>
        public static RoadNetworkAsset Load() => Resources.Load<RoadNetworkAsset>(ResourceName);

        /// <summary>A town's network (TownData/(town)/v1), or null when it was not baked.</summary>
        public static RoadNetworkAsset LoadTown(string townId) => Resources.Load<RoadNetworkAsset>(TownResourceName(townId));

        public Road RoadById(int id)
        {
            foreach (var r in roads) if (r.id == id) return r;
            return null;
        }

        public Arm ArmOf(in Node n, int k) => arms[n.firstArm + k];

        public int CountKind(NodeKind k)
        {
            int c = 0;
            foreach (var n in nodes) if (n.kind == k) c++;
            return c;
        }

        /// <summary>Fills this asset from a built graph (float conversion) and, for a town, its import report and R1 surfaces; stamps the content hash.</summary>
        public void SetFrom(RoadGraph g, OsmImportReport report = null, RoadSurfaces surf = null)
        {
            formatVersion = FormatVersion;
            builderVersion = RoadGraphBuilder.Version;
            importerVersion = report != null ? OsmRoadImporter.Version : 0;
            surfaceVersion = surf != null ? SurfaceBuilder.Version : 0;
            source = g.source ?? "";
            sourceManifestSha256 = g.sourceManifestSha256 ?? "";
            inputRoads = g.inputRoads;
            inputPoints = g.inputPoints;

            nodes = new Node[g.nodes.Count];
            for (int i = 0; i < nodes.Length; i++)
            {
                var n = g.nodes[i];
                nodes[i] = new Node
                {
                    id = n.id, legacyId = n.legacyId, osmNode = n.osmNode, position = V3(n.p), kind = n.kind, control = n.control, controlSource = n.controlSource, flags = n.flags,
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
                    control = a.control, controlSource = a.controlSource, controlOsmNode = a.controlOsmNode, controlDistance = (float)a.controlDistance,
                    sMouth = 0, sStop = -1, sCwInner = -1, sCwOuter = -1,
                };
            }
            var surfList = new List<Surface>();
            var inletList = new List<InletProp>();
            if (surf != null)
            {
                foreach (var ns in surf.nodes)
                {
                    foreach (var a in ns.arms)
                    {
                        var x = arms[a.arm];
                        x.sMouth = (float)a.sMouth; x.sStop = (float)a.sStop; x.sCwInner = (float)a.sCwInner; x.sCwOuter = (float)a.sCwOuter;
                        x.crosswalk = a.crosswalk; x.stopMoved = a.stopMoved;
                        arms[a.arm] = x;
                    }
                    var cs = new SurfaceCorner[ns.corners.Count];
                    for (int k = 0; k < cs.Length; k++)
                    {
                        var c = ns.corners[k];
                        var arc = new Vector2[c.arc.Count];
                        for (int q = 0; q < arc.Length; q++) arc[q] = V2(c.arc[q]);
                        cs[k] = new SurfaceCorner
                        {
                            armI = c.armI, armJ = c.armJ, kind = c.kind, baseRadius = (float)c.baseRadius, radius = (float)c.radius, reason = c.reason,
                            centre = V2(c.centre), hasClearance = !double.IsNaN(c.clearance), clearance = double.IsNaN(c.clearance) ? 0 : (float)c.clearance, arc = arc,
                        };
                    }
                    var shape = new Vector2[ns.polygon.Count];
                    for (int k = 0; k < shape.Length; k++) shape[k] = V2(ns.polygon[k]);
                    surfList.Add(new Surface
                    {
                        node = ns.node, mode = ns.mode, outline = shape, area = (float)ns.area, corridorArea = (float)ns.corridorArea,
                        planeA = (float)ns.planeA, planeB = (float)ns.planeB, planeC = (float)ns.planeC, corners = cs, triangles = ns.triangles.Count / 3, problem = ns.problem ?? "",
                    });
                }
                foreach (var i in surf.inlets) inletList.Add(new InletProp { position = V3(i.p), edge = i.edge, node = i.node, why = i.why ?? "" });
            }
            surfaces = surfList.ToArray();
            inlets = inletList.ToArray();
            roads = new Road[g.roads.Count];
            for (int i = 0; i < roads.Length; i++)
            {
                var r = g.roads[i];
                var xs = r.xs;
                roads[i] = new Road
                {
                    id = r.id, cls = r.cls, length = (float)r.Length, osmWays = r.osmWays ?? Array.Empty<string>(), highway = r.highway ?? "", service = r.service ?? "",
                    hwLeft = (float)(xs?.hwLeft ?? r.hw), hwRight = (float)(xs?.hwRight ?? r.hw), laneWidth = (float)(xs?.laneWidth ?? 0),
                    parkingLeft = (float)(xs?.parkingLeft ?? 0), parkingRight = (float)(xs?.parkingRight ?? 0), gutter = (float)(xs?.gutter ?? 0),
                    lanes = xs?.lanes ?? 0, widthSource = xs?.source ?? "", oneway = r.oneway, bridge = r.bridge, tunnel = r.tunnel, roundabout = r.roundabout,
                    layer = r.layer, surface = r.surface ?? "", sidewalk = r.sidewalk ?? "", parking = r.parking ?? "", speedKmh = (float)r.speedKmh,
                    inKerbZone = r.inKerbZone, inDowntown = r.inDowntown, cutStart = r.cutStart, cutEnd = r.cutEnd, buildingConflict = r.buildingConflict,
                };
            }
            features = new Feature[g.features.Count];
            for (int i = 0; i < features.Length; i++)
            {
                var f = g.features[i];
                features[i] = new Feature
                {
                    kind = f.kind, osmNode = f.osmNode, edge = f.edge, s = (float)f.s, direction = f.direction, node = f.node, arm = f.arm,
                    crossing = f.crossing ?? "", problem = f.problem ?? "",
                };
            }
            var dz = new List<Vector2>();
            if (g.downtown != null) foreach (var p in g.downtown) dz.Add(V2(p));
            downtownZone = dz.ToArray();
            var cuts = new List<Cut>();
            var drives = new List<Driveway>();
            var steeps = new List<Steep>();
            if (report != null)
            {
                foreach (var c in report.edgeCuts)
                    cuts.Add(new Cut { road = c.road, osmWay = c.osmWay ?? "", at = V2(c.at), side = c.side.ToString(), beyond = (float)c.junctionBeyond });
                foreach (var d in report.driveways)
                    drives.Add(new Driveway { osmWay = d.osmWay ?? "", service = d.service ?? "", osmNode = d.node, at = V2(d.at), road = d.road, s = (float)d.s });
                foreach (var s in report.steep)
                    if (s.road >= 0) steeps.Add(new Steep { road = s.road, at = V2(s.at), length = (float)s.length, maxGrade = (float)s.maxGrade, realGrade = (float)s.realGrade });
            }
            edgeCuts = cuts.ToArray();
            mappedDriveways = drives.ToArray();
            steep = steeps.ToArray();
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
                w.Write(formatVersion); w.Write(builderVersion); w.Write(importerVersion); w.Write(surfaceVersion); w.Write(source ?? ""); w.Write(inputRoads); w.Write(inputPoints);
                w.Write(nodes.Length);
                foreach (var n in nodes)
                {
                    w.Write(n.id); w.Write(n.legacyId); w.Write(n.osmNode); W(w, n.position); w.Write((byte)n.kind); w.Write((byte)n.control); w.Write((byte)n.controlSource); w.Write((ushort)n.flags);
                    w.Write(n.firstArm); w.Write(n.armCount); w.Write(n.firstCorner); w.Write(n.cornerCount); w.Write(n.majorA); w.Write(n.majorB);
                    w.Write(n.bestPairBendDeg); w.Write(n.minArmGapDeg);
                }
                w.Write(arms.Length);
                foreach (var a in arms)
                {
                    w.Write(a.id); w.Write(a.node); w.Write(a.edge); w.Write(a.sourceRoad); w.Write(a.atA); w.Write((byte)a.cls); w.Write(a.halfWidth);
                    W(w, a.dir); w.Write(a.angleDeg); w.Write(a.length); w.Write(a.grade); w.Write(a.maxGrade);
                    w.Write(a.tangentL); w.Write(a.tangentR); w.Write(a.mouth); w.Write(a.maxMouth); w.Write(a.shortArm);
                    w.Write((byte)a.control); w.Write((byte)a.controlSource); w.Write(a.controlOsmNode); w.Write(a.controlDistance);
                    w.Write(a.sMouth); w.Write(a.sStop); w.Write(a.sCwInner); w.Write(a.sCwOuter); w.Write(a.crosswalk); w.Write(a.stopMoved);
                }
                w.Write(surfaces.Length);
                foreach (var s in surfaces)
                {
                    w.Write(s.node); w.Write((byte)s.mode); w.Write(s.area); w.Write(s.corridorArea); w.Write(s.planeA); w.Write(s.planeB); w.Write(s.planeC);
                    w.Write(s.triangles); w.Write(s.problem ?? "");
                    w.Write(s.outline.Length);
                    foreach (var p in s.outline) W(w, p);
                    w.Write(s.corners.Length);
                    foreach (var c in s.corners)
                    {
                        w.Write(c.armI); w.Write(c.armJ); w.Write((byte)c.kind); w.Write(c.baseRadius); w.Write(c.radius); w.Write((byte)c.reason);
                        W(w, c.centre); w.Write(c.clearance); w.Write(c.hasClearance);
                        w.Write(c.arc.Length);
                        foreach (var p in c.arc) W(w, p);
                    }
                }
                w.Write(inlets.Length);
                foreach (var i in inlets) { W(w, i.position); w.Write(i.edge); w.Write(i.node); w.Write(i.why ?? ""); }
                w.Write(roads.Length);
                foreach (var r in roads)
                {
                    w.Write(r.id); w.Write((byte)r.cls); w.Write(r.length);
                    w.Write(r.osmWays.Length);
                    foreach (var s in r.osmWays) w.Write(s ?? "");
                    w.Write(r.highway ?? ""); w.Write(r.service ?? "");
                    w.Write(r.hwLeft); w.Write(r.hwRight); w.Write(r.laneWidth); w.Write(r.parkingLeft); w.Write(r.parkingRight); w.Write(r.gutter);
                    w.Write(r.lanes); w.Write(r.widthSource ?? ""); w.Write(r.oneway); w.Write(r.bridge); w.Write(r.tunnel); w.Write(r.roundabout); w.Write(r.layer);
                    w.Write(r.surface ?? ""); w.Write(r.sidewalk ?? ""); w.Write(r.parking ?? ""); w.Write(r.speedKmh);
                    w.Write(r.inKerbZone); w.Write(r.inDowntown); w.Write(r.cutStart); w.Write(r.cutEnd); w.Write(r.buildingConflict);
                }
                w.Write(features.Length);
                foreach (var f in features)
                {
                    w.Write((byte)f.kind); w.Write(f.osmNode); w.Write(f.edge); w.Write(f.s); w.Write(f.direction); w.Write(f.node); w.Write(f.arm);
                    w.Write(f.crossing ?? ""); w.Write(f.problem ?? "");
                }
                w.Write(downtownZone.Length);
                foreach (var p in downtownZone) W(w, p);
                w.Write(edgeCuts.Length);
                foreach (var c in edgeCuts) { w.Write(c.road); w.Write(c.osmWay ?? ""); W(w, c.at); w.Write(c.side ?? ""); w.Write(c.beyond); }
                w.Write(mappedDriveways.Length);
                foreach (var d in mappedDriveways) { w.Write(d.osmWay ?? ""); w.Write(d.service ?? ""); w.Write(d.osmNode); W(w, d.at); w.Write(d.road); w.Write(d.s); }
                w.Write(steep.Length);
                foreach (var s in steep) { w.Write(s.road); W(w, s.at); w.Write(s.length); w.Write(s.maxGrade); w.Write(s.realGrade); }
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
