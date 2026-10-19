using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Roads.Client
{
    /// <summary>
    /// The road graph drawn in the world (R0 debug view, Dev builds and RoadsDebug.unity): edge centrelines by class, nodes
    /// by kind with their numbers (05's numbering), major arms cyan, stop-controlled arms red, other arms white, provisional
    /// mouths yellow (orange when the arm is too short), provisional kerb fillets white, fillet tangent points magenta, the
    /// town kerb zone green, and a magenta ring on nodes that need decision U8. Drawn on top of the scene (ZTest Always).
    /// </summary>
    public sealed class RoadGraphOverlay : MonoBehaviour
    {
        public const string MaterialResource = "BrRoadsOverlay";
        public const float Lift = 0.35f;

        public RoadNetworkAsset Network { get; private set; }
        public Camera LabelCamera;
        /// <summary>Label height on screen, as a fraction of the distance to the camera.</summary>
        public float labelScale = 0.06f;

        GameObject _graph, _labels;
        readonly List<Transform> _labelTs = new List<Transform>();
        bool _visible = true, _showLabels = true;

        public bool Visible
        {
            get => _visible;
            set { _visible = value; Apply(); }
        }

        public bool ShowLabels
        {
            get => _showLabels;
            set { _showLabels = value; Apply(); }
        }

        public static RoadGraphOverlay Create(Transform parent, RoadNetworkAsset network, Material material)
        {
            var go = new GameObject("Road graph overlay (R0)");
            go.transform.SetParent(parent, false);
            var o = go.AddComponent<RoadGraphOverlay>();
            o.Build(network, material != null ? material : Resources.Load<Material>(MaterialResource));
            return o;
        }

        void Apply()
        {
            if (_graph != null) _graph.SetActive(_visible);
            if (_labels != null) _labels.SetActive(_visible && _showLabels);
        }

        public void Build(RoadNetworkAsset network, Material material)
        {
            Network = network;
            if (_graph != null) Destroy(_graph);
            if (_labels != null) Destroy(_labels);
            _labelTs.Clear();
            if (network == null || material == null)
            {
                Debug.LogWarning("[BR] road graph overlay: " + (network == null ? "no RoadNetworkAsset (bake it)" : "no overlay material"));
                return;
            }
            _graph = new GameObject("graph");
            _graph.transform.SetParent(transform, false);
            _graph.AddComponent<MeshFilter>().sharedMesh = BuildMesh(network);
            var r = _graph.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;

            _labels = new GameObject("labels");
            _labels.transform.SetParent(transform, false);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            Material labelMat = null, shadowMat = null;
            if (font != null)
            {
                // full white (the lines' material is dimmed for the tonemapper), drawn over a dark shadow copy
                labelMat = new Material(material) { name = "road graph labels", mainTexture = font.material.mainTexture, color = new Color(1.3f, 1.3f, 1.3f, 1f) };
                labelMat.renderQueue = material.renderQueue + 2;
                shadowMat = new Material(material) { name = "road graph label shadows", mainTexture = font.material.mainTexture, color = new Color(0.02f, 0.02f, 0.03f, 0.9f) };
                shadowMat.renderQueue = material.renderQueue + 1;
            }
            foreach (var n in network.nodes)
            {
                var go = new GameObject("node " + n.id);
                go.transform.SetParent(_labels.transform, false);
                go.transform.position = n.position + Vector3.up * (Lift + 1.5f);
                var tm = go.AddComponent<TextMesh>();
                tm.text = n.id.ToString();
                tm.anchor = TextAnchor.MiddleLeft;
                tm.fontSize = 64;
                tm.characterSize = 0.1f;
                tm.color = Color.white;
                if (font != null)
                {
                    tm.font = font;
                    go.GetComponent<MeshRenderer>().sharedMaterial = labelMat;
                    var sh = new GameObject("shadow");
                    sh.transform.SetParent(go.transform, false);
                    sh.transform.localPosition = new Vector3(0.05f, -0.05f, 0.01f);
                    var stm = sh.AddComponent<TextMesh>();
                    stm.text = tm.text; stm.anchor = tm.anchor; stm.fontSize = tm.fontSize; stm.characterSize = tm.characterSize;
                    stm.color = Color.white; stm.font = font;
                    sh.GetComponent<MeshRenderer>().sharedMaterial = shadowMat;
                }
                _labelTs.Add(go.transform);
            }
            Apply();
        }

        void LateUpdate() => FaceCamera(LabelCamera != null ? LabelCamera : Camera.main);

        /// <summary>Turns the labels towards the camera and scales them to a constant size on screen.</summary>
        public void FaceCamera(Camera cam)
        {
            if (cam == null || _labels == null || !_labels.activeSelf || Network == null) return;
            var rot = cam.transform.rotation;
            var right = cam.transform.right;
            for (int i = 0; i < _labelTs.Count && i < Network.nodes.Length; i++)
            {
                var anchor = Network.nodes[i].position + Vector3.up * Lift;
                float d = Vector3.Distance(cam.transform.position, anchor);
                float s = Mathf.Max(0.5f, d * labelScale);
                var t = _labelTs[i];
                t.rotation = rot;
                t.localScale = Vector3.one * s;
                t.position = anchor + right * (3.4f + 0.12f * s);
            }
        }

        // ------------------------------------------------------------------ mesh

        sealed class Builder
        {
            public readonly List<Vector3> pos = new List<Vector3>();
            public readonly List<Color32> col = new List<Color32>();
            public readonly List<int> idx = new List<int>();

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color32 k)
            {
                int i = pos.Count;
                pos.Add(a); pos.Add(b); pos.Add(c); pos.Add(d);
                col.Add(k); col.Add(k); col.Add(k); col.Add(k);
                idx.Add(i); idx.Add(i + 1); idx.Add(i + 2);
                idx.Add(i); idx.Add(i + 2); idx.Add(i + 3);
            }

            public void Segment(Vector3 a, Vector3 b, float width, Color32 k)
            {
                var d = b - a;
                d.y = 0;
                if (d.sqrMagnitude < 1e-8f) return;
                var n = new Vector3(-d.z, 0, d.x).normalized * (width * 0.5f);
                Quad(a - n, a + n, b + n, b - n, k);
            }

            public void Strip(IReadOnlyList<Vector3> pts, float width, Color32 k, bool closed = false)
            {
                for (int i = 0; i + 1 < pts.Count; i++) Segment(pts[i], pts[i + 1], width, k);
                if (closed && pts.Count > 2) Segment(pts[pts.Count - 1], pts[0], width, k);
            }

            public void Disc(Vector3 c, float r, Color32 k, int seg = 24)
            {
                int i0 = pos.Count;
                pos.Add(c); col.Add(k);
                for (int s = 0; s <= seg; s++)
                {
                    float a = s * Mathf.PI * 2 / seg;
                    pos.Add(c + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * r);
                    col.Add(k);
                }
                for (int s = 0; s < seg; s++) { idx.Add(i0); idx.Add(i0 + 1 + s); idx.Add(i0 + 2 + s); }
            }

            public void Ring(Vector3 c, float r, float width, Color32 k, int seg = 32)
            {
                var pts = new List<Vector3>();
                for (int s = 0; s < seg; s++) pts.Add(c + new Vector3(Mathf.Cos(s * Mathf.PI * 2 / seg), 0, Mathf.Sin(s * Mathf.PI * 2 / seg)) * r);
                Strip(pts, width, k, true);
            }
        }

        public static readonly Color32 MajorColor = new Color32(0, 229, 255, 255), StopColor = new Color32(255, 64, 64, 255),
            PlainColor = new Color32(240, 240, 240, 255), MouthColor = new Color32(255, 235, 59, 255), ShortMouthColor = new Color32(255, 120, 0, 255),
            FilletColor = new Color32(255, 255, 255, 220), TangentColor = new Color32(255, 64, 255, 255), ZoneColor = new Color32(140, 230, 140, 230),
            DecisionColor = new Color32(255, 0, 220, 255);

        public static Color32 KindColor(NodeKind k, int degree)
        {
            switch (k)
            {
                case NodeKind.Junction: return degree >= 4 ? new Color32(232, 52, 52, 255) : new Color32(255, 150, 30, 255);
                case NodeKind.DirtApron: return new Color32(205, 133, 63, 255);
                case NodeKind.Bend: return new Color32(79, 195, 247, 255);
                case NodeKind.DeadEnd: return new Color32(170, 170, 170, 255);
                case NodeKind.ClassChange: return new Color32(102, 187, 106, 255);
                default: return new Color32(255, 0, 255, 255);
            }
        }

        public static bool IsStopArm(in RoadNetworkAsset.Node n, int k) =>
            k != n.majorA && k != n.majorB && (n.control == Control.MinorStop || n.control == Control.AllWayStop) &&
            (n.kind == NodeKind.Junction || n.kind == NodeKind.DirtApron);

        public static Mesh BuildMesh(RoadNetworkAsset a)
        {
            var b = new Builder();
            Vector3 Up(Vector3 p) => p + Vector3.up * Lift;
            Vector3 Flat(Vector2 xz, float y) => new Vector3(xz.x, y, xz.y);

            if (a.townZone.Length > 2)
            {
                float zy = a.nodes.Length > 0 ? a.nodes[0].position.y + Lift : Lift;
                var zone = new List<Vector3>();
                foreach (var p in a.townZone) zone.Add(Flat(p, zy));
                b.Strip(zone, 1.0f, ZoneColor, true);
            }
            foreach (var e in a.edges)
            {
                var c = (Color)Backroads.Roads.PlainRibbonMesh.ClassColor(e.cls);
                var bright = (Color32)Color.Lerp(c, Color.white, 0.25f);
                var pts = new List<Vector3>(e.points.Length);
                foreach (var p in e.points) pts.Add(Up(p));
                b.Strip(pts, 0.6f, bright);
            }
            foreach (var n in a.nodes)
            {
                var np = new Vector2(n.position.x, n.position.z);
                float y = n.position.y + Lift;
                for (int k = 0; k < n.cornerCount; k++)
                {
                    var c = a.corners[n.firstCorner + k];
                    if (c.kind != CornerKind.Fillet) continue;
                    var I = a.arms[c.armI];
                    var J = a.arms[c.armJ];
                    Vector2 ti = np + I.dir * c.tI + new Vector2(-I.dir.y, I.dir.x) * I.halfWidth;
                    Vector2 tj = np + J.dir * c.tJ + new Vector2(J.dir.y, -J.dir.x) * J.halfWidth;
                    var arc = new List<Vector3>();
                    foreach (var p in Arc(c.centre, c.radius, ti, tj, np)) arc.Add(Flat(p, y));
                    b.Strip(arc, 0.4f, FilletColor);
                    b.Disc(Flat(ti, y), 0.7f, TangentColor, 12);
                    b.Disc(Flat(tj, y), 0.7f, TangentColor, 12);
                }
                for (int k = 0; k < n.armCount; k++)
                {
                    var arm = a.arms[n.firstArm + k];
                    bool major = k == n.majorA || k == n.majorB;
                    var colr = major ? MajorColor : IsStopArm(n, k) ? StopColor : PlainColor;
                    float len = Mathf.Min(arm.length, Mathf.Max(10f, arm.mouth));
                    b.Segment(Flat(np, y), Flat(np + arm.dir * len, y), 1.0f, colr);
                    if (arm.mouth > 0 && arm.mouth < arm.length)
                    {
                        var m = np + arm.dir * arm.mouth;
                        var nn = new Vector2(-arm.dir.y, arm.dir.x) * arm.halfWidth;
                        b.Segment(Flat(m - nn, y), Flat(m + nn, y), 0.6f, arm.shortArm ? ShortMouthColor : MouthColor);
                    }
                }
            }
            foreach (var n in a.nodes)
            {
                var c = Up(n.position);
                b.Disc(c, 3.1f, new Color32(10, 12, 16, 255));
                b.Disc(c, 2.6f, KindColor(n.kind, n.armCount));
                if ((n.flags & NodeFlags.ComplexPair) != 0) b.Ring(c, 5.2f, 0.8f, DecisionColor);
            }
            var mesh = new Mesh { name = "road_graph_overlay", indexFormat = b.pos.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.SetVertices(b.pos);
            mesh.SetColors(b.col);
            var uv = new Vector2[b.pos.Count];
            mesh.SetUVs(0, uv);
            mesh.SetTriangles(b.idx, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>The fillet arc from one tangent point to the other, on the side facing the node.</summary>
        public static List<Vector2> Arc(Vector2 c, float r, Vector2 from, Vector2 to, Vector2 node)
        {
            float a0 = Mathf.Atan2(from.y - c.y, from.x - c.x), a1 = Mathf.Atan2(to.y - c.y, to.x - c.x);
            float ccw = Mathf.Repeat(a1 - a0, 2 * Mathf.PI), cw = ccw - 2 * Mathf.PI;
            Vector2 mc = c + new Vector2(Mathf.Cos(a0 + ccw / 2), Mathf.Sin(a0 + ccw / 2)) * r;
            Vector2 mw = c + new Vector2(Mathf.Cos(a0 + cw / 2), Mathf.Sin(a0 + cw / 2)) * r;
            float sweep = (mc - node).sqrMagnitude <= (mw - node).sqrMagnitude ? ccw : cw;
            var pts = new List<Vector2>();
            int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(sweep) / (5 * Mathf.Deg2Rad)));
            for (int k = 0; k <= steps; k++)
            {
                float t = a0 + sweep * k / steps;
                pts.Add(c + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * r);
            }
            return pts;
        }
    }
}
