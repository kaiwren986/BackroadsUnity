using System.Collections.Generic;
using System.Text.RegularExpressions;
using Backroads.World;
using UnityEditor;
using UnityEngine;

namespace Backroads.Canon.Editor
{
    /// <summary>
    /// Reads glTFast's import of a frozen canon GLB into a CanonModelSet: one model per top-level glTF node, its mesh
    /// nodes in three.js traverse order, each with the glTF material names of its submeshes.
    /// </summary>
    public static class CanonModelSets
    {
        static readonly Regex Suffix = new Regex(@"\.\d+$");
        static readonly Regex Reserved = new Regex(@"[\[\]\.:\/]");
        static readonly Regex Space = new Regex(@"\s");

        /// <summary>three.js PropertyBinding.sanitizeNodeName, which GLTFLoader applies to node names.</summary>
        public static string ThreeName(string gltfName) => Reserved.Replace(Space.Replace(gltfName ?? "", "_"), "");

        public static string MaterialName(Material m) => m == null ? "" : Suffix.Replace(m.name ?? "", "");

        public static CanonModelSet Build(string glbAssetPath, string sha256, List<string> notes)
        {
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(glbAssetPath);
            if (root == null) throw new CanonImportException($"glTFast did not import {glbAssetPath} (is com.unity.cloud.gltfast installed?)");
            var set = ScriptableObject.CreateInstance<CanonModelSet>();
            set.source = glbAssetPath;
            set.sha256 = sha256;
            var models = new List<CanonModelSet.Model>();
            foreach (Transform top in root.transform)
            {
                var parts = new List<CanonModelSet.Part>();
                var bounds = new Bounds();
                bool any = false;
                Walk(top, top, parts, ref bounds, ref any);
                models.Add(new CanonModelSet.Model { name = top.name, parts = parts.ToArray(), bounds = bounds });
            }
            set.models = models.ToArray();
            var glb = GlbReader.Load(CanonImporter.ToFull(glbAssetPath));
            var names = new List<string>();
            var colors = new List<Color>();
            foreach (var mo in glb.Materials)
            {
                var m = CanonJson.Obj(mo);
                var name = Suffix.Replace(CanonJson.Str(m, "name") ?? "", "");
                if (names.Contains(name)) continue;
                var pbr = CanonJson.Get(m, "pbrMetallicRoughness") as Dictionary<string, object>;
                var f = CanonJson.Floats(CanonJson.Get(pbr, "baseColorFactor"));
                names.Add(name);
                colors.Add(f.Length >= 3 ? new Color(f[0], f[1], f[2], 1) : Color.white);
            }
            set.materialNames = names.ToArray();
            set.materialColors = colors.ToArray();
            int meshes = 0;
            foreach (var m in set.models) meshes += m.parts.Length;
            notes.Add($"{System.IO.Path.GetFileName(glbAssetPath)}: {set.models.Length} models, {meshes} mesh nodes");
            return set;
        }

        static void Walk(Transform t, Transform modelRoot, List<CanonModelSet.Part> parts, ref Bounds bounds, ref bool any)
        {
            var mf = t.GetComponent<MeshFilter>();
            var mr = t.GetComponent<MeshRenderer>();
            if (mf != null && mf.sharedMesh != null)
            {
                var m = modelRoot.worldToLocalMatrix * t.localToWorldMatrix;
                var mats = mr != null ? mr.sharedMaterials : new Material[0];
                var names = new string[mf.sharedMesh.subMeshCount];
                bool pane = names.Length > 0;
                for (int i = 0; i < names.Length; i++)
                {
                    names[i] = i < mats.Length ? MaterialName(mats[i]) : "";
                    if (names[i] != "Glass") pane = false;
                }
                parts.Add(new CanonModelSet.Part
                {
                    node = t.name, threeName = ThreeName(t.name), mesh = mf.sharedMesh, materials = names,
                    localPosition = m.GetColumn(3), localRotation = m.rotation, localScale = m.lossyScale, isPane = pane,
                });
                var b = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            foreach (Transform c in t) Walk(c, modelRoot, parts, ref bounds, ref any);
        }
    }
}
