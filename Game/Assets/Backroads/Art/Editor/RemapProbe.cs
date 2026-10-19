using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// 07 section 12: "AddRemap inside OnPreprocessModel vs OnAssignMaterialModel: check which one remaps reliably on
    /// the first import". Each hook imports a fresh copy of a model (new GUID, no .meta) and counts the material slots
    /// that point at project slot materials after the first and the second import, whether the remap ended up in the
    /// .meta, and which files the import left next to the model. Writes artifacts/art/remap-probe.json.
    ///   Unity -batchmode -nographics -quit -executeMethod Backroads.Art.Editor.ArtBatch.Run -brArtSteps probe
    /// </summary>
    public static class RemapProbe
    {
        public const string ProbeRoot = "Assets/Art/Vehicles/_Probe";

        [Serializable]
        public class Measure
        {
            public int slots;
            public int remapped;
            public int embedded;
            public int other;
            public string[] materials;
        }

        [Serializable]
        public class HookResult
        {
            public string hook;
            public Measure firstImport;
            public Measure secondImport;
            public int externalObjectsInImporter;
            public int materialSubAssets;
            public bool remapInMeta;
            public string[] filesLeftBeside;
            public string verdict;
        }

        [Serializable]
        public class Report
        {
            public string source;
            public string chosen;
            public HookResult[] hooks;
        }

        /// <summary>Copies <paramref name="source"/> to <paramref name="dir"/> under a new name and imports it for the first time.</summary>
        public static string ImportFreshCopy(string source, string dir, string fileName)
        {
            ArtMaterials.EnsureFolder(dir);
            var path = dir + "/" + fileName;
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
            File.Copy(Full(source), Full(path), true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            return path;
        }

        public static string Full(string assetPath) =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", assetPath));

        public static Measure MeasureSlots(string modelPath)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var m = new Measure();
            var names = new List<string>();
            if (go == null) { m.materials = new string[0]; return m; }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in r.sharedMaterials)
                {
                    m.slots++;
                    var p = mat == null ? "" : AssetDatabase.GetAssetPath(mat);
                    if (p.StartsWith(VehicleMaterialSlots.MaterialRoot, StringComparison.Ordinal)) m.remapped++;
                    else if (p == modelPath) m.embedded++;
                    else m.other++;
                    names.Add($"{r.name}:{(mat == null ? "null" : mat.name)}@{(p == modelPath ? "embedded" : p)}");
                }
            }
            m.materials = names.ToArray();
            return m;
        }

        public static Report Run(string source)
        {
            var saved = BackroadsModelPostprocessor.Hook;
            var results = new List<HookResult>();
            try
            {
                foreach (BackroadsModelPostprocessor.RemapHook hook in Enum.GetValues(typeof(BackroadsModelPostprocessor.RemapHook)))
                {
                    BackroadsModelPostprocessor.Hook = hook;
                    var dir = $"{ProbeRoot}/{hook}";
                    var path = ImportFreshCopy(source, dir, "VH_Probe.fbx");
                    var r = new HookResult { hook = hook.ToString(), firstImport = MeasureSlots(path) };
                    var mi = (ModelImporter)AssetImporter.GetAtPath(path);
                    r.externalObjectsInImporter = mi.GetExternalObjectMap().Count;
                    r.materialSubAssets = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>().Count();
                    AssetDatabase.SaveAssets();   // what the editor does on exit: persist dirty importer settings
                    r.remapInMeta = File.ReadAllText(Full(path) + ".meta").Contains("type: UnityEngine:Material");
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                    r.secondImport = MeasureSlots(path);
                    r.filesLeftBeside = Directory.GetFileSystemEntries(Full(dir))
                        .Select(Path.GetFileName).Where(f => f != "VH_Probe.fbx" && f != "VH_Probe.fbx.meta").OrderBy(f => f, StringComparer.Ordinal).ToArray();
                    bool first = r.firstImport.slots > 0 && r.firstImport.remapped == r.firstImport.slots;
                    bool second = r.secondImport.slots > 0 && r.secondImport.remapped == r.secondImport.slots;
                    r.verdict = first && second && r.filesLeftBeside.Length == 0 ? "remaps on the first import"
                        : first && second ? "remaps, but leaves files beside the model"
                        : second ? "remaps only from the second import"
                        : "does not remap";
                    results.Add(r);
                }
            }
            finally
            {
                BackroadsModelPostprocessor.Hook = saved;
                AssetDatabase.DeleteAsset(ProbeRoot);
            }
            return new Report { source = source, chosen = saved.ToString(), hooks = results.ToArray() };
        }

        public static string ToJson(Report r) => JsonUtility.ToJson(r, true).Replace("\r", "") + "\n";
    }
}
