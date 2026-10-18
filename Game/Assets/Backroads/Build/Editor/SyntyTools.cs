using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Core;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine;

namespace Backroads.Build.Editor
{
    /// <summary>
    /// Synty pack upkeep (PLAN §4.7, AGENTS.md rule 3): Unity's own URP material conversion and a numbers-only audit.
    /// Reads the pack folders from synty-manifest.json. Never changes meshes.
    ///   Unity -batchmode -quit -projectPath Game -executeMethod Backroads.Build.Editor.SyntyTools.Convert
    ///   Unity -batchmode -quit -projectPath Game -executeMethod Backroads.Build.Editor.SyntyTools.Audit
    /// </summary>
    public static class SyntyTools
    {
        static readonly string[] BuiltInLitPrefixes = { "Legacy Shaders/", "Mobile/", "Nature/", "Particles/Standard" };
        static readonly string[] BuiltInLitNames = { "Standard", "Standard (Specular setup)", "Autodesk Interactive" };

        public static string[] PackFolders()
        {
            var manifest = BrJson.Obj(BrJson.Parse(File.ReadAllText(RepoPaths.InRepo("synty-manifest.json"))), "synty-manifest");
            var folders = new List<string>();
            foreach (var p in BrJson.Arr(manifest["packs"], "packs"))
                foreach (var f in BrJson.Arr(BrJson.Obj(p)["assetFolders"], "assetFolders"))
                    if (f is string s && !folders.Contains(s)) folders.Add(s);
            return folders.ToArray();
        }

        static readonly ShaderTagId LightMode = new ShaderTagId("LightMode");
        static readonly HashSet<string> UrpLightModes = new HashSet<string> { "", "SRPDefaultUnlit", "UniversalForward", "UniversalForwardOnly", "UniversalGBuffer", "Universal2D" };

        /// <summary>True when the material would render pink in URP: no shader, the error shader, unsupported, or no pass URP draws.</summary>
        public static bool LooksPink(Material m)
        {
            var sh = m != null ? m.shader : null;
            if (sh == null || sh.name == "Hidden/InternalErrorShader" || !sh.isSupported) return true;
            return !HasUrpPass(sh);
        }

        /// <summary>URP draws passes tagged with its own light modes, and untagged passes as SRPDefaultUnlit. Built-in lit passes (ForwardBase...) it skips.</summary>
        public static bool HasUrpPass(Shader sh)
        {
            for (int s = 0; s < sh.subshaderCount; s++)
                for (int p = 0; p < sh.GetPassCountInSubshader(s); p++)
                    if (UrpLightModes.Contains(sh.FindPassTagValue(s, p, LightMode).name ?? "")) return true;
            return false;
        }

        /// <summary>Built-in shaders that draw in URP but should move to URP's own shaders (legacy particles, Standard...).</summary>
        public static bool IsBuiltIn(Material m)
        {
            var n = m != null && m.shader != null ? m.shader.name : "";
            return BuiltInLitNames.Contains(n) || BuiltInLitPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal));
        }

        static List<Material> Materials(string[] folders) =>
            AssetDatabase.FindAssets("t:Material", folders)
                .Select(g => AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(m => m != null).ToList();

        public static void Convert()
        {
            int code = 0;
            try
            {
                var folders = PackFolders();
                if (folders.Length == 0) throw new Exception("synty-manifest.json lists no assetFolders");
                var all = Materials(folders);
                var pink = all.Where(m => LooksPink(m) || IsBuiltIn(m)).ToList();
                Log.Info($"SyntyTools.Convert: {all.Count(LooksPink)} pink-looking and {all.Count(IsBuiltIn)} built-in-shader materials before, in {string.Join(", ", folders)}");
                // URP's own upgraders, applied to the Synty folders only. (Converters.RunInBatchMode throws in URP 17.3:
                // it instantiates every converter type, and Base2DMaterialUpgrader has no default constructor.)
                var upgraders = MaterialUpgrader.FetchAllUpgradersForPipeline(typeof(UniversalRenderPipelineAsset));
                foreach (var m in pink)
                {
                    string path = AssetDatabase.GetAssetPath(m), shader = m.shader != null ? m.shader.name : "(none)", msg = "";
                    if (AssetDatabase.IsSubAsset(m)) { Log.Warn($"SyntyTools.Convert: {path} ({shader}) is embedded in a model; left alone"); continue; }
                    bool ok = MaterialUpgrader.Upgrade(m, upgraders, MaterialUpgrader.UpgradeFlags.None, ref msg);
                    if (ok) EditorUtility.SetDirty(m);
                    Log.Info($"SyntyTools.Convert: {path} ({shader}) -> {(ok ? m.shader.name : "no upgrader: " + msg)}");
                }
                AssetDatabase.SaveAssets();
                int after = Materials(folders).Count(LooksPink);
                Log.Info($"SyntyTools.Convert: {after} pink-looking materials after");
            }
            catch (Exception e)
            {
                Log.Error("SyntyTools.Convert failed: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static void Audit()
        {
            int code = 0;
            try
            {
                var outPath = CommandLine.Get("-br-out", Path.Combine(RepoPaths.Artifacts, "synty_audit.json"));
                var sb = new StringBuilder();
                sb.Append("{\n  \"unity\": ").Append(Q(Application.unityVersion)).Append(",\n  \"packs\": [");
                var folders = PackFolders();
                for (int i = 0; i < folders.Length; i++)
                {
                    sb.Append(i == 0 ? "\n" : ",\n");
                    AuditFolder(folders[i], sb);
                }
                sb.Append("\n  ]\n}\n");
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                File.WriteAllText(outPath, sb.ToString().Replace("\r", ""), new UTF8Encoding(false));
                Log.Info("SyntyTools.Audit: wrote " + outPath);
            }
            catch (Exception e)
            {
                Log.Error("SyntyTools.Audit failed: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static void AuditFolder(string folder, StringBuilder sb)
        {
            var f = new[] { folder };
            var prefabs = AssetDatabase.FindAssets("t:Prefab", f).Select(AssetDatabase.GUIDToAssetPath).ToList();
            var rows = new List<(string name, int tris, int lods, Vector3 size, int wheels)>();
            foreach (var path in prefabs)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                var lodGroup = go.GetComponentInChildren<LODGroup>(true);
                IEnumerable<Renderer> rs = go.GetComponentsInChildren<Renderer>(true);
                if (lodGroup != null && lodGroup.lodCount > 0) rs = lodGroup.GetLODs()[0].renderers.Where(r => r != null);
                int tris = 0;
                var b = new Bounds();
                bool hasB = false;
                foreach (var r in rs)
                {
                    // TryGetComponent, not GetComponent()?.: Unity's missing-component placeholder is not C# null.
                    Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
                    if (mesh == null) continue;
                    for (int s = 0; s < mesh.subMeshCount; s++) tris += (int)(mesh.GetIndexCount(s) / 3);
                    var m = go.transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
                    var mb = mesh.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1)));
                        if (!hasB) { b = new Bounds(p, Vector3.zero); hasB = true; } else b.Encapsulate(p);
                    }
                }
                int wheels = go.GetComponentsInChildren<Transform>(true).Count(t => t.name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0);
                rows.Add((go.name, tris, lodGroup != null ? lodGroup.lodCount : 0, b.size, wheels));
            }

            var mats = Materials(f);
            var pink = mats.Where(LooksPink).Select(m => m.name).ToList();
            var shaders = mats.GroupBy(m => m.shader != null ? m.shader.name : "(none)").OrderByDescending(g => g.Count()).ToList();
            var texGuids = AssetDatabase.FindAssets("t:Texture2D", f);
            int maxTex = 0;
            foreach (var g in texGuids)
                if (AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(g)) is TextureImporter ti)
                {
                    ti.GetSourceTextureWidthAndHeight(out var w, out var h);
                    maxTex = Math.Max(maxTex, Math.Max(w, h));
                }
            var tri = rows.Select(r => r.tris).OrderBy(x => x).ToList();
            int Pct(double q) => tri.Count == 0 ? 0 : tri[Math.Min(tri.Count - 1, (int)(q * (tri.Count - 1) + 0.5))];
            var groups = rows.GroupBy(r => Prefix(r.name)).OrderByDescending(g => g.Count()).ToList();

            sb.Append("    {\n      \"folder\": ").Append(Q(folder));
            sb.Append(",\n      \"prefabs\": ").Append(rows.Count);
            sb.Append(",\n      \"prefabsByPrefix\": {").Append(string.Join(", ", groups.Select(g => Q(g.Key) + ": " + g.Count()))).Append('}');
            sb.Append(",\n      \"trisLod0\": {\"min\": ").Append(Pct(0)).Append(", \"median\": ").Append(Pct(0.5)).Append(", \"p90\": ").Append(Pct(0.9)).Append(", \"max\": ").Append(Pct(1)).Append('}');
            sb.Append(",\n      \"heaviest\": [").Append(string.Join(", ", rows.OrderByDescending(r => r.tris).Take(5).Select(r => "[" + Q(r.name) + ", " + r.tris + "]"))).Append(']');
            sb.Append(",\n      \"withLodGroup\": ").Append(rows.Count(r => r.lods > 0));
            sb.Append(",\n      \"materials\": ").Append(mats.Count);
            sb.Append(",\n      \"pinkMaterials\": ").Append(pink.Count);
            sb.Append(",\n      \"pinkExamples\": [").Append(string.Join(", ", pink.Take(20).Select(Q))).Append(']');
            sb.Append(",\n      \"builtInShaderMaterials\": [").Append(string.Join(", ", mats.Where(IsBuiltIn).Select(m => Q(m.name + " (" + m.shader.name + ")")))).Append(']');
            sb.Append(",\n      \"shaders\": {").Append(string.Join(", ", shaders.Select(g => Q(g.Key) + ": " + g.Count()))).Append('}');
            sb.Append(",\n      \"textures\": ").Append(texGuids.Length).Append(", \"maxTextureSize\": ").Append(maxTex);
            sb.Append(",\n      \"scripts\": ").Append(AssetDatabase.FindAssets("t:MonoScript", f).Length);
            sb.Append(",\n      \"scenes\": ").Append(AssetDatabase.FindAssets("t:Scene", f).Length);
            sb.Append(",\n      \"vehicles\": [");
            var veh = rows.Where(r => r.name.StartsWith("SM_Veh", StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.name).ToList();
            for (int i = 0; i < veh.Count; i++)
            {
                var v = veh[i];
                sb.Append(i == 0 ? "\n        " : ",\n        ").Append("{\"name\": ").Append(Q(v.name))
                  .Append(", \"size\": [").Append(F(v.size.x)).Append(", ").Append(F(v.size.y)).Append(", ").Append(F(v.size.z))
                  .Append("], \"tris\": ").Append(v.tris).Append(", \"wheelParts\": ").Append(v.wheels).Append('}');
            }
            sb.Append(veh.Count > 0 ? "\n      ]" : "]");
            sb.Append("\n    }");
        }

        static string Prefix(string name)
        {
            var parts = name.Split('_');
            return parts.Length >= 2 ? parts[0] + "_" + parts[1] : parts[0];
        }

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }
}
