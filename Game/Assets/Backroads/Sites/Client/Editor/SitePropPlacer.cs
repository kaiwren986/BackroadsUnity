using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Backroads.Sites.Client.Editor
{
    /// <summary>
    /// card sites-S4 step 4: puts a town's site props (SitePropSet, baked by SiteBake) into the open scene. Props are the
    /// user's Synty prefabs placed as they are (AGENTS.md rule 3: instances only, never edited or merged), found by path in
    /// Assets/Synty and stacked bottom to top (a lamp's pole, then its head on the pole's top); where the packs are not
    /// imported (a lane worktree), or the packs have nothing for a key (accessible-stall signs, menu boards), a simple stand-in
    /// of our own (posts, plates, boxes) takes its place. Fences: the Synty fence piece repeated along each run (scaled to fit
    /// the run exactly), or posts and rails of our own; gates and lane A's site models (ModelRequests) are boxes of our own
    /// until lane A models them. Lane W's scene build calls Place; re-running replaces the previous "Site props" object.
    /// </summary>
    public static class SitePropPlacer
    {
        public const string Root = "Site props (S4)";
        public const string FencePrefab = "Assets/Synty/PolygonCity/Prefabs/Environments/SM_Env_Fence_01.prefab";
        public const string FenceEndPrefab = "Assets/Synty/PolygonCity/Prefabs/Environments/SM_Env_Fence_End_01.prefab";

        public static GameObject Place(string townId, Transform parent = null)
        {
            var set = SitePropSet.LoadTown(townId);
            if (set == null) { Debug.LogWarning("[BR] no site props baked for " + townId + " (SitesBakeCli.Bake)"); return null; }
            return Place(set, parent);
        }

        public static GameObject Place(SitePropSet set, Transform parent = null)
        {
            var old = GameObject.Find(Root);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var root = new GameObject(Root);
            if (parent != null) root.transform.SetParent(parent, false);
            int synty = 0, own = 0;
            var cache = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var p in set.props)
            {
                var go = Make(p, cache, out bool fromPack);
                if (fromPack) synty++; else own++;
                go.transform.SetParent(root.transform, false);
            }
            var fences = new GameObject("Fences");
            fences.transform.SetParent(root.transform, false);
            int pieces = 0;
            foreach (var f in set.fences) pieces += Fence(f, fences.transform, cache);
            foreach (var g in set.gates) Gate(g, fences.transform);
            var models = new GameObject("Lane A site models (stand-ins)");
            models.transform.SetParent(root.transform, false);
            foreach (var m in set.models) Model(m, models.transform);
            Debug.Log($"[BR] site props: {set.props.Length} ({synty} Synty prefabs, {own} of our own), {set.fences.Length} fence runs ({pieces} pieces), {set.gates.Length} gates, {set.models.Length} lane A stand-ins");
            return root;
        }

        static GameObject Load(string path, Dictionary<string, GameObject> cache)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (!cache.TryGetValue(path, out var go)) cache[path] = go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            return go;
        }

        static GameObject Make(SitePropSet.Placement p, Dictionary<string, GameObject> cache, out bool fromPack)
        {
            fromPack = false;
            var rot = Quaternion.Euler(0, p.yaw, 0);
            if (p.prefabs != null && p.prefabs.Length > 0)
            {
                var prefabs = new List<GameObject>();
                foreach (var path in p.prefabs)
                {
                    var pf = Load(path, cache);
                    if (pf == null) { prefabs.Clear(); break; }
                    prefabs.Add(pf);
                }
                if (prefabs.Count > 0)
                {
                    fromPack = true;
                    var holder = new GameObject($"{p.key} p{p.parcel}");
                    holder.transform.SetPositionAndRotation(p.position, rot);
                    float top = 0;
                    foreach (var pf in prefabs)
                    {
                        var inst = (GameObject)PrefabUtility.InstantiatePrefab(pf);
                        inst.transform.SetParent(holder.transform, false);
                        inst.transform.localPosition = new Vector3(0, top, 0);
                        top = Top(inst, holder.transform);
                    }
                    return holder;
                }
            }
            return StandIn(p, rot);
        }

        /// <summary>The top of an instance's renderers in the holder's space.</summary>
        static float Top(GameObject inst, Transform holder)
        {
            float top = 0;
            foreach (var r in inst.GetComponentsInChildren<Renderer>())
                top = Math.Max(top, holder.InverseTransformPoint(r.bounds.max).y);
            return top;
        }

        // ------------------------------------------------------------------------------------------ our own stand-ins

        static readonly Color Grey = new Color(0.55f, 0.56f, 0.58f), Dark = new Color(0.15f, 0.15f, 0.16f), Blue = new Color(0.15f, 0.36f, 0.66f),
            White = new Color(0.92f, 0.92f, 0.9f), Green = new Color(0.12f, 0.35f, 0.18f), Brown = new Color(0.42f, 0.3f, 0.2f), Orange = new Color(0.9f, 0.45f, 0.1f);

        /// <summary>Posts, plates and boxes of our own (no Synty geometry), by key; keys the packs lack keep a box collider.</summary>
        static GameObject StandIn(SitePropSet.Placement p, Quaternion rot)
        {
            var go = new GameObject($"{p.key} p{p.parcel} (ours)");
            go.transform.SetPositionAndRotation(p.position, rot);
            var t = go.transform;
            bool collider = p.prefabs == null || p.prefabs.Length == 0;
            switch (p.key)
            {
                case "lamp":
                    Prim(t, PrimitiveType.Cylinder, new Vector3(0, 3.5f, 0), new Vector3(0.18f, 3.5f, 0.18f), Grey);
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 7.0f, 0.5f), new Vector3(0.35f, 0.15f, 1.1f), Dark);
                    break;
                case "sign.accessible":
                case "sign.vanAccessible":
                    Prim(t, PrimitiveType.Cylinder, new Vector3(0, 1.1f, 0), new Vector3(0.06f, 1.1f, 0.06f), Grey);
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 2.0f, 0.04f), new Vector3(0.3f, 0.45f, 0.02f), Blue);
                    if (p.key == "sign.vanAccessible") Prim(t, PrimitiveType.Cube, new Vector3(0, 1.65f, 0.04f), new Vector3(0.3f, 0.15f, 0.02f), White);
                    break;
                case "sign.parking":
                case "sign.entrance":
                case "sign.exit":
                case "sign.warning":
                    Prim(t, PrimitiveType.Cylinder, new Vector3(0, 1.1f, 0), new Vector3(0.06f, 1.1f, 0.06f), Grey);
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 2.0f, 0.04f), new Vector3(0.45f, 0.45f, 0.02f), p.key == "sign.warning" ? Orange : White);
                    break;
                case "menu.board":
                    Prim(t, PrimitiveType.Cube, new Vector3(-0.7f, 0.8f, 0), new Vector3(0.08f, 1.6f, 0.08f), Dark);
                    Prim(t, PrimitiveType.Cube, new Vector3(0.7f, 0.8f, 0), new Vector3(0.08f, 1.6f, 0.08f), Dark);
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 1.3f, 0), new Vector3(1.6f, 1.1f, 0.12f), Dark);
                    break;
                case "mailbox":
                    Prim(t, PrimitiveType.Cylinder, new Vector3(0, 0.5f, 0), new Vector3(0.08f, 0.5f, 0.08f), Brown);
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 1.1f, 0), new Vector3(0.22f, 0.22f, 0.48f), Dark);
                    break;
                case "bollard":
                    Prim(t, PrimitiveType.Cylinder, new Vector3(0, 0.5f, 0), new Vector3(0.16f, 0.5f, 0.16f), new Color(0.95f, 0.8f, 0.1f));
                    break;
                case "dumpster":
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 0.65f, 0), new Vector3(1.8f, 1.3f, 1.1f), Green);
                    break;
                case "trash.bin":
                case "trash.island":
                    Prim(t, PrimitiveType.Cylinder, new Vector3(0, 0.45f, 0), new Vector3(0.55f, 0.45f, 0.55f), Dark);
                    break;
                case "bench":
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 0.45f, 0), new Vector3(1.6f, 0.08f, 0.45f), Brown);
                    break;
                case "cone":
                    Prim(t, PrimitiveType.Cylinder, new Vector3(0, 0.35f, 0), new Vector3(0.3f, 0.35f, 0.3f), Orange);
                    break;
                case "boom.gate":
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 0.5f, 0), new Vector3(0.4f, 1.0f, 0.4f), White);
                    Prim(t, PrimitiveType.Cube, new Vector3(3f, 0.95f, 0), new Vector3(6f, 0.1f, 0.1f), new Color(0.85f, 0.1f, 0.1f));
                    break;
                default:
                    Prim(t, PrimitiveType.Cube, new Vector3(0, 0.4f, 0), new Vector3(0.8f, 0.8f, 0.8f), Grey);
                    break;
            }
            if (collider) AddBounds(go);
            return go;
        }

        static int Fence(SitePropSet.FenceRun f, Transform parent, Dictionary<string, GameObject> cache)
        {
            if (f.line == null || f.line.Length < 2) return 0;
            var run = new GameObject($"Fence p{f.parcel} ({f.kind})");
            run.transform.SetParent(parent, false);
            var piece = f.kind == FenceKind.Farm ? null : Load(FencePrefab, cache);
            float pieceLen = 0;
            Vector3 axis = Vector3.right, centre = Vector3.zero;
            if (piece != null)
            {
                var b = PrefabBounds(piece);
                bool alongX = b.size.x >= b.size.z;
                pieceLen = alongX ? b.size.x : b.size.z;
                axis = alongX ? Vector3.right : Vector3.forward;
                centre = new Vector3(b.center.x, 0, b.center.z);
                if (pieceLen < 0.2f) piece = null;
            }
            int n = 0;
            for (int i = 0; i + 1 < f.line.Length; i++)
            {
                Vector3 a = f.line[i], c = f.line[i + 1];
                var d = c - a;
                d.y = 0;
                float len = d.magnitude;
                if (len < 0.05f) continue;
                var dir = d / len;
                if (piece != null)
                {
                    int k = Math.Max(1, Mathf.RoundToInt(len / pieceLen));
                    float scale = len / (k * pieceLen);
                    var turn = Quaternion.FromToRotation(axis, dir);
                    for (int j = 0; j < k; j++)
                    {
                        var inst = (GameObject)PrefabUtility.InstantiatePrefab(piece);
                        inst.transform.SetParent(run.transform, false);
                        var at = Vector3.Lerp(a, c, (j + 0.5f) / k);
                        var s = axis == Vector3.right ? new Vector3(scale, 1, 1) : new Vector3(1, 1, scale);
                        // the piece's own centre (its pivot may sit at one end) lands on the spot
                        inst.transform.SetPositionAndRotation(at - turn * Vector3.Scale(centre, s), turn);
                        inst.transform.localScale = s;
                        n++;
                    }
                }
                else
                {
                    // posts every 2.4 m and two rails of our own
                    int k = Math.Max(1, Mathf.CeilToInt(len / 2.4f));
                    for (int j = 0; j <= k; j++)
                        Prim(run.transform, PrimitiveType.Cube, Vector3.Lerp(a, c, (float)j / k) + Vector3.up * (f.height * 0.5f), new Vector3(0.1f, f.height, 0.1f), Brown);
                    foreach (float h in new[] { 0.35f, 0.8f })
                    {
                        var rail = Prim(run.transform, PrimitiveType.Cube, (a + c) * 0.5f + Vector3.up * (f.height * h), new Vector3(0.05f, 0.08f, len), f.kind == FenceKind.ChainLink ? Grey : Brown);
                        rail.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                    }
                    n += k;
                }
            }
            return n;
        }

        static void Gate(SitePropSet.GateSpan g, Transform parent)
        {
            var d = g.b - g.a;
            d.y = 0;
            float len = d.magnitude;
            if (len < 0.1f) return;
            var go = Prim(parent, PrimitiveType.Cube, (g.a + g.b) * 0.5f + Vector3.up * 0.6f, new Vector3(0.06f, 1.2f, len), g.kind == FenceKind.ChainLink ? Grey : Brown);
            go.name = $"Gate p{g.parcel}";
            go.transform.rotation = Quaternion.LookRotation(d / len, Vector3.up);
        }

        static void Model(SitePropSet.ModelBox m, Transform parent)
        {
            if (m.footprint == null || m.footprint.Length < 3) return;
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            foreach (var q in m.footprint) { x0 = Math.Min(x0, q.x); z0 = Math.Min(z0, q.y); x1 = Math.Max(x1, q.x); z1 = Math.Max(z1, q.y); }
            // the footprint's own axes: its first edge
            var e = m.footprint[1] - m.footprint[0];
            float yaw = Mathf.Atan2(e.x, e.y) * Mathf.Rad2Deg;
            var rot = Quaternion.Euler(0, yaw, 0);
            var inv = Quaternion.Inverse(rot);
            float u0 = float.MaxValue, v0 = float.MaxValue, u1 = float.MinValue, v1 = float.MinValue;
            foreach (var q in m.footprint)
            {
                var l = inv * new Vector3(q.x, 0, q.y);
                u0 = Math.Min(u0, l.x); v0 = Math.Min(v0, l.z); u1 = Math.Max(u1, l.x); v1 = Math.Max(v1, l.z);
            }
            var centre = rot * new Vector3((u0 + u1) * 0.5f, 0, (v0 + v1) * 0.5f);
            var go = Prim(parent, PrimitiveType.Cube, new Vector3(centre.x, m.y + m.height * 0.5f, centre.z), new Vector3(u1 - u0, m.height, v1 - v0), new Color(0.7f, 0.7f, 0.72f));
            go.transform.rotation = rot;
            go.name = $"model {m.key} p{m.parcel}";
            go.AddComponent<BoxCollider>();
        }

        static Bounds PrefabBounds(GameObject prefab)
        {
            var b = new Bounds(Vector3.zero, Vector3.zero);
            bool any = false;
            foreach (var mf in prefab.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                var mb = mf.sharedMesh.bounds;
                var m = mf.transform.localToWorldMatrix;
                foreach (var c in new[] { mb.min, mb.max, new Vector3(mb.min.x, mb.min.y, mb.max.z), new Vector3(mb.max.x, mb.min.y, mb.min.z) })
                {
                    var w = m.MultiplyPoint3x4(c);
                    if (!any) { b = new Bounds(w, Vector3.zero); any = true; } else b.Encapsulate(w);
                }
            }
            return b;
        }

        static void AddBounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            var box = go.AddComponent<BoxCollider>();
            box.center = go.transform.InverseTransformPoint(b.center);
            box.size = Quaternion.Inverse(go.transform.rotation) * b.size;
            box.size = new Vector3(Math.Abs(box.size.x), Math.Abs(box.size.y), Math.Abs(box.size.z));
        }

        static GameObject Prim(Transform parent, PrimitiveType t, Vector3 at, Vector3 size, Color c)
        {
            var g = GameObject.CreatePrimitive(t);
            UnityEngine.Object.DestroyImmediate(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = at;
            g.transform.localScale = size;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null) g.GetComponent<MeshRenderer>().sharedMaterial = new Material(shader) { color = c };
            return g;
        }
    }
}
