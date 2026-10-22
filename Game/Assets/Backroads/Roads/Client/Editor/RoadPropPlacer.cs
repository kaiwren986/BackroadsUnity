using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Backroads.Roads.Client.Editor
{
    /// <summary>
    /// roads-R3 step 3: puts a town's road props (RoadNetworkAsset.props) into the open scene. STOP and YIELD signs and signal heads are the
    /// user's Synty prefabs placed as they are (AGENTS.md rule 3: instances only, never edited or merged), found by path in
    /// Assets/Synty; where the packs are not imported (a lane worktree) a plain post and plate of our own stands in. The
    /// all-way plaque and the street-name blades are ours (plates on a post, a TextMesh with the street's name or its
    /// placeholder; the Synty blade has its own printed name), as is the post a signal head hangs from. Lane W's scene
    /// build calls Place; re-running replaces the previous "Road props" object.
    /// </summary>
    public static class RoadPropPlacer
    {
        public const string Root = "Road props (R3)";
        const string Props = "Assets/Synty/PolygonCity/Prefabs/Props/";

        /// <summary>Synty prefab per kind, and the yaw that turns its sign face to look along +z (checked on the packs).</summary>
        public static readonly Dictionary<RoadPropKind, (string path, float yaw)> Prefabs = new Dictionary<RoadPropKind, (string, float)>
        {
            { RoadPropKind.Stop, (Props + "SM_Prop_Sign_Stop_01.prefab", 0f) },
            { RoadPropKind.Yield, (Props + "SM_Prop_Sign_GiveWay_01.prefab", 0f) },
            { RoadPropKind.Signal, (Props + "SM_Prop_TrafficLight_01.prefab", 0f) },
        };

        public static GameObject Place(RoadNetworkAsset a, Transform parent = null)
        {
            var old = GameObject.Find(Root);
            if (old != null) UnityEngine.Object.DestroyImmediate(old);
            var root = new GameObject(Root);
            if (parent != null) root.transform.SetParent(parent, false);
            int synty = 0, own = 0;
            foreach (var p in a.props)
            {
                var go = Make(a, p, out bool fromPack);
                if (fromPack) synty++; else own++;
                go.transform.SetParent(root.transform, false);
            }
            Debug.Log($"[BR] road props: {a.props.Length} ({synty} Synty prefabs, {own} of our own)");
            return root;
        }

        static GameObject Make(RoadNetworkAsset a, RoadNetworkAsset.Prop p, out bool fromPack)
        {
            fromPack = false;
            var rot = Quaternion.Euler(0, p.yawDeg, 0);
            if (Prefabs.TryGetValue(p.kind, out var pf))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(pf.path);
                if (prefab != null)
                {
                    var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    inst.name = $"{p.kind} n{p.node}a{p.arm}";
                    fromPack = true;
                    if (p.kind != RoadPropKind.Signal)
                    {
                        inst.transform.SetPositionAndRotation(p.position, rot * Quaternion.Euler(0, pf.yaw, 0));
                        return inst;
                    }
                    // the Synty head hangs below its pivot: on a post of our own, its bottom at the mounting height
                    var holder = new GameObject($"Signal n{p.node}a{p.arm}");
                    holder.transform.SetPositionAndRotation(p.position, rot);
                    float top = p.mount + 1.15f;
                    Prim(holder.transform, PrimitiveType.Cylinder, new Vector3(0, top * 0.5f, -0.2f), new Vector3(0.12f, top * 0.5f, 0.12f), new Color(0.3f, 0.32f, 0.3f));
                    inst.transform.SetParent(holder.transform, false);
                    inst.transform.localPosition = new Vector3(0, top, 0);
                    inst.transform.localRotation = Quaternion.Euler(0, pf.yaw, 0);
                    return holder;
                }
            }
            return Placeholder(a, p, rot);
        }

        /// <summary>A post and a plate of our own (no Synty geometry): red STOP, white-and-red YIELD, white plaque, green blades, a dark signal head.</summary>
        static GameObject Placeholder(RoadNetworkAsset a, RoadNetworkAsset.Prop p, Quaternion rot)
        {
            var go = new GameObject($"{p.kind} n{p.node}a{p.arm} (placeholder)");
            go.transform.SetPositionAndRotation(p.position, rot);
            float top = p.mount + (p.kind == RoadPropKind.Signal ? 1.0f : 0.8f);
            if (p.kind != RoadPropKind.AllWayPlaque)
                Prim(go.transform, PrimitiveType.Cylinder, new Vector3(0, top * 0.5f, -0.03f), new Vector3(0.06f, top * 0.5f, 0.06f), new Color(0.55f, 0.56f, 0.58f));
            switch (p.kind)
            {
                case RoadPropKind.Stop:
                    Prim(go.transform, PrimitiveType.Cube, new Vector3(0, p.mount + 0.38f, 0), new Vector3(0.76f, 0.76f, 0.03f), new Color(0.75f, 0.05f, 0.05f));
                    break;
                case RoadPropKind.Yield:
                    Prim(go.transform, PrimitiveType.Cube, new Vector3(0, p.mount + 0.45f, 0), new Vector3(0.9f, 0.8f, 0.03f), new Color(0.95f, 0.95f, 0.95f));
                    break;
                case RoadPropKind.AllWayPlaque:
                    Prim(go.transform, PrimitiveType.Cube, new Vector3(0, p.mount + 0.1f, 0.005f), new Vector3(0.45f, 0.15f, 0.02f), new Color(0.75f, 0.05f, 0.05f));
                    break;
                case RoadPropKind.Signal:
                    Prim(go.transform, PrimitiveType.Cube, new Vector3(0, p.mount + 0.5f, 0), new Vector3(0.35f, 1.0f, 0.3f), new Color(0.08f, 0.08f, 0.08f));
                    break;
                case RoadPropKind.StreetName:
                    Prim(go.transform, PrimitiveType.Cube, new Vector3(0, p.mount + 0.05f, 0), new Vector3(0.05f, 0.15f, 0.9f), new Color(0.05f, 0.38f, 0.16f));
                    Prim(go.transform, PrimitiveType.Cube, new Vector3(0, p.mount + 0.25f, 0), new Vector3(0.9f, 0.15f, 0.05f), new Color(0.05f, 0.38f, 0.16f));
                    Names(a, p, go.transform);
                    break;
            }
            return go;
        }

        static void Prim(Transform parent, PrimitiveType t, Vector3 at, Vector3 size, Color c)
        {
            var g = GameObject.CreatePrimitive(t);
            UnityEngine.Object.DestroyImmediate(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = at;
            g.transform.localScale = size;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader != null)
            {
                var m = new Material(shader) { color = c };
                g.GetComponent<MeshRenderer>().sharedMaterial = m;
            }
        }

        /// <summary>The two street names on the blades (our TextMesh, never on the Synty mesh).</summary>
        static void Names(RoadNetworkAsset a, RoadNetworkAsset.Prop p, Transform parent)
        {
            string N(int s) => s >= 0 && s < a.streets.Length ? a.streets[s].name : "";
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            int k = 0;
            foreach (var (name, yaw, h) in new[] { (N(p.street), 0f, p.mount + 0.25f), (N(p.street2), 90f, p.mount + 0.05f) })
            {
                if (string.IsNullOrEmpty(name)) continue;
                foreach (float side in new[] { 0f, 180f })
                {
                    var t = new GameObject($"Name {k++}");
                    t.transform.SetParent(parent, false);
                    t.transform.localPosition = new Vector3(0, h, 0);
                    t.transform.localRotation = Quaternion.Euler(0, yaw + side, 0) * Quaternion.Euler(0, 0, 0);
                    t.transform.Translate(0, 0, -0.04f, Space.Self);
                    var tm = t.AddComponent<TextMesh>();
                    tm.text = name.ToUpperInvariant();
                    tm.font = font;
                    tm.fontSize = 48;
                    tm.characterSize = 0.012f;
                    tm.anchor = TextAnchor.MiddleCenter;
                    tm.alignment = TextAlignment.Center;
                    tm.color = Color.white;
                    if (font != null) t.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                }
            }
        }
    }
}
