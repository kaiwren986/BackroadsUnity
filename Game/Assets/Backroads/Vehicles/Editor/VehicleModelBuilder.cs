using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Backroads.Vehicles.Client;
using Backroads.Vehicles.Reference;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Vehicles.Editor
{
    /// <summary>
    /// Builds Content/core/vehicles/Resources/BrVehicleModels.asset from today's browser-game vehicle GLBs (byte copies
    /// of the oracle's assets/vehicles/*.glb, imported by glTFast; the truck is lane A's copy in Art/Vehicles/Truck)
    /// and writes the two URP/Lit template materials. Menu Backroads > Vehicles > Build models, or
    /// -executeMethod Backroads.Vehicles.Editor.VehicleModelBuilder.BuildCli.
    /// </summary>
    public static class VehicleModelBuilder
    {
        public const string Root = "Assets/Content/core/vehicles";
        public const string OutPath = Root + "/Resources/" + VehicleModelSet.ResourceName + ".asset";
        public const string OpaquePath = Root + "/Materials/M_VehicleOpaque.mat";
        public const string GlassPath = Root + "/Materials/M_VehicleGlass.mat";

        public static readonly Dictionary<string, string> Sources = new Dictionary<string, string>
        {
            { "hatch", Root + "/models/hatch.glb" },
            { "rally", Root + "/models/rally.glb" },
            { "pickup", Root + "/models/pickup.glb" },
            { "truck", "Assets/Art/Vehicles/Truck/truck.glb" },
        };

        static readonly Regex Suffix = new Regex(@"\.\d+$");

        [MenuItem("Backroads/Vehicles/Build models")]
        public static void BuildMenu() => Debug.Log(Build());

        public static void BuildCli()
        {
            try
            {
                Debug.Log("[BR] " + Build());
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[BR] vehicle models failed: " + e);
                EditorApplication.Exit(1);
            }
        }

        public static string Build()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", Root, "Resources"));
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "..", Root, "Materials"));
            var opaque = Template(OpaquePath, false);
            var glass = Template(GlassPath, true);
            var set = AssetDatabase.LoadAssetAtPath<VehicleModelSet>(OutPath);
            bool create = set == null;
            if (create) set = ScriptableObject.CreateInstance<VehicleModelSet>();
            set.opaqueTemplate = opaque;
            set.glassTemplate = glass;
            var models = new List<VehicleModelSet.Model>();
            var sb = new StringBuilder();
            foreach (var kv in Sources)
            {
                AssetDatabase.ImportAsset(kv.Value, ImportAssetOptions.ForceSynchronousImport);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(kv.Value);
                if (go == null) throw new Exception($"glTFast did not import {kv.Value}");
                var m = new VehicleModelSet.Model
                {
                    id = kv.Key, source = kv.Value,
                    body = Part(go, "Body", true), bodyLod1 = Part(go, "Body_LOD1", false),
                    wheel = Part(go, "Wheel", true), wheelLod1 = Part(go, "Wheel_LOD1", false), caliper = Part(go, "Caliper", false),
                    materials = Materials(Path.Combine(Application.dataPath, "..", kv.Value)),
                };
                models.Add(m);
                sb.Append($"{kv.Key}: body {Tris(m.body)} tris ({m.body.materials.Length} materials), LOD1 {Tris(m.bodyLod1)}, wheel {Tris(m.wheel)}/{Tris(m.wheelLod1)}; ");
            }
            set.models = models.ToArray();
            if (create) AssetDatabase.CreateAsset(set, OutPath);
            EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return "vehicle models built: " + sb;
        }

        static int Tris(VehicleModelSet.Part p) => p?.mesh == null ? 0 : (int)(Enumerable.Range(0, p.mesh.subMeshCount).Sum(i => (long)p.mesh.GetIndexCount(i)) / 3);

        static VehicleModelSet.Part Part(GameObject root, string name, bool required)
        {
            var t = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => x.name == name);
            if (t == null)
            {
                if (required) throw new Exception($"{root.name}: no node {name}");
                return null;
            }
            var mf = t.GetComponent<MeshFilter>();
            var mr = t.GetComponent<MeshRenderer>();
            if (mf == null || mf.sharedMesh == null) throw new Exception($"{root.name}/{name}: no mesh");
            var mats = mr != null ? mr.sharedMaterials : new Material[0];
            var names = new string[mf.sharedMesh.subMeshCount];
            for (int i = 0; i < names.Length; i++) names[i] = i < mats.Length && mats[i] != null ? Suffix.Replace(mats[i].name, "") : "";
            return new VehicleModelSet.Part { mesh = mf.sharedMesh, materials = names };
        }

        /// <summary>The GLB's glTF materials (baseColorFactor, metallic, roughness, alphaMode, emissiveFactor).</summary>
        static VehicleModelSet.MaterialInfo[] Materials(string glbPath)
        {
            var b = File.ReadAllBytes(glbPath);
            int len = BitConverter.ToInt32(b, 12);
            var json = MiniJson.O(MiniJson.Parse(Encoding.UTF8.GetString(b, 20, len)));
            var list = new List<VehicleModelSet.MaterialInfo>();
            foreach (var mo in MiniJson.A(MiniJson.Get(json, "materials")))
            {
                var m = MiniJson.O(mo);
                var info = new VehicleModelSet.MaterialInfo { name = Suffix.Replace(MiniJson.S(m, "name") ?? "", "") };
                if (MiniJson.Get(m, "pbrMetallicRoughness") is Dictionary<string, object> pbr)
                {
                    if (MiniJson.Get(pbr, "baseColorFactor") is List<object> c)
                        info.baseColor = new Color((float)MiniJson.D(c[0]), (float)MiniJson.D(c[1]), (float)MiniJson.D(c[2]), c.Count > 3 ? (float)MiniJson.D(c[3]) : 1f);
                    var mf = MiniJson.D(pbr, "metallicFactor"); info.metallic = double.IsNaN(mf) ? 1f : (float)mf;
                    var rf = MiniJson.D(pbr, "roughnessFactor"); info.roughness = double.IsNaN(rf) ? 1f : (float)rf;
                }
                info.blend = MiniJson.S(m, "alphaMode") == "BLEND";
                if (MiniJson.Get(m, "emissiveFactor") is List<object> e)
                    info.emissive = new Color((float)MiniJson.D(e[0]), (float)MiniJson.D(e[1]), (float)MiniJson.D(e[2]), 1f);
                if (list.All(x => x.name != info.name)) list.Add(info);
            }
            return list.ToArray();
        }

        static Material Template(string path, bool transparent)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool create = m == null;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) throw new Exception("URP Lit shader not found");
            if (create) m = new Material(shader);
            m.shader = shader;
            m.SetFloat("_Cull", (float)CullMode.Off);
            m.SetFloat("_WorkflowMode", 1);
            m.SetFloat("_ReceiveShadows", 1);
            if (transparent)
            {
                m.SetFloat("_Surface", 1);
                m.SetFloat("_Blend", 0);
                m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                m.SetFloat("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.DisableKeyword("_EMISSION");
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = (int)RenderQueue.Transparent;
                m.SetShaderPassEnabled("DepthOnly", false);
                m.SetShaderPassEnabled("ShadowCaster", false);
                m.SetColor("_BaseColor", new Color(0.04f, 0.07f, 0.09f, 0.36f));
                m.SetFloat("_Smoothness", 0.97f);
            }
            else
            {
                m.SetFloat("_Surface", 0);
                m.SetFloat("_ZWrite", 1);
                m.SetFloat("_SrcBlend", (float)BlendMode.One);
                m.SetFloat("_DstBlend", (float)BlendMode.Zero);
                m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                m.SetColor("_EmissionColor", Color.black);
                m.SetOverrideTag("RenderType", "Opaque");
                m.renderQueue = (int)RenderQueue.Geometry;
                m.SetColor("_BaseColor", Color.grey);
                m.SetFloat("_Smoothness", 0.5f);
            }
            if (create) AssetDatabase.CreateAsset(m, path);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
