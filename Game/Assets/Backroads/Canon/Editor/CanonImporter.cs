using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backroads.World;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace Backroads.Canon.Editor
{
    /// <summary>Thrown when the canon cannot be imported, most importantly on any SHA-256 mismatch (03 section 3.5).</summary>
    public sealed class CanonImportException : Exception
    {
        public CanonImportException(string message) : base(message) { }
    }

    /// <summary>
    /// T1.1: imports CanonData/v1 (and the frozen models in CanonData/refs/models) into
    /// Assets/Backroads/World/Canon/v1 (gitignored). Every SHA-256 in the manifests is verified first and any mismatch
    /// aborts. The layers are copied to .bytes with path-derived .meta GUIDs; world.json, placements.json,
    /// vegetation.json, traffic.json and cameras.json become ScriptableObjects; the GLBs are imported by glTFast;
    /// static_props.glb is split into 64 m chunks. The result is cached by the manifest hash.
    /// Entry points: menu Backroads > Import Canon v1, ImportCli.Run (batchmode), EnsureImported (builds, tests).
    /// </summary>
    public static class CanonImporter
    {
        /// <summary>Bump when the importer output changes, so cached imports are redone.</summary>
        public const int ImporterVersion = 4;

        public const string OutDir = "Assets/Backroads/World/Canon/v1";
        public const string ResourcesDir = OutDir + "/Resources";
        public const string ModelsDir = OutDir + "/Models";
        public const string CanonAssetPath = ResourcesDir + "/" + CanonAsset.ResourceName + ".asset";

        public static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        /// <summary>Tests point the importer at a tampered copy to prove a mismatch aborts; null in normal use.</summary>
        public static string SourceDirOverride;
        public static string SourceDir => SourceDirOverride ?? Path.Combine(RepoRoot, "CanonData", "v1");
        public static string ModelsSourceDir => Path.Combine(RepoRoot, "CanonData", "refs", "models");

        static readonly (string src, string name, bool isFloat)[] LayerFiles =
        {
            ("terrain/heights.f32", "heights", true),
            ("terrain/base.f32", "base", true),
            ("terrain/surface.u8", "surface", false),
            ("terrain/wear.u8", "wear", false),
            ("terrain/roadd.u8", "roadd", false),
            ("terrain/mud.u8", "mud", false),
            ("terrain/tint.f32", "tint", true),
        };

        public sealed class Result
        {
            public bool imported;   // false when the cached import was current
            public double seconds;
            public string manifestSha256;
            public List<string> notes = new List<string>();
        }

        // ------------------------------------------------------------------ entry points

        [MenuItem("Backroads/Import Canon v1")]
        static void MenuImport()
        {
            try
            {
                var r = Import(force: true);
                EditorUtility.DisplayDialog("Canon v1", $"Imported in {r.seconds:F1} s.\n\n" + string.Join("\n", r.notes), "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Canon v1 import failed", e.Message, "OK");
            }
        }

        [MenuItem("Backroads/Verify Canon v1 hashes")]
        static void MenuVerify()
        {
            var errors = VerifySources(out var sha);
            if (errors.Count == 0) EditorUtility.DisplayDialog("Canon v1", $"All hashes match.\nmanifest {sha}", "OK");
            else EditorUtility.DisplayDialog("Canon v1: hash mismatch", string.Join("\n", errors.Take(20)), "OK");
        }

        /// <summary>
        /// For builds and tests: verifies the sources and the imported layers, and imports when the cached import is
        /// missing or stale. Returns true when the canon is ready; false with the reason otherwise (never throws).
        /// </summary>
        public static bool EnsureImported(out string message)
        {
            try
            {
                var r = Import(force: false);
                message = r.imported ? $"canon v1 imported in {r.seconds:F1} s" : $"canon v1 current (manifest {Short(r.manifestSha256)}, verified in {r.seconds:F1} s)";
                return true;
            }
            catch (Exception e)
            {
                message = "canon v1: " + e.Message;
                Debug.LogError("[BR] " + message);
                return false;
            }
        }

        /// <summary>Same as EnsureImported(out _), throwing CanonImportException on failure.</summary>
        public static void EnsureImported()
        {
            if (!EnsureImported(out var msg)) throw new CanonImportException(msg);
        }

        public static CanonAsset LoadImported() => AssetDatabase.LoadAssetAtPath<CanonAsset>(CanonAssetPath);

        /// <summary>The import. With force false a current cache is only re-verified.</summary>
        public static Result Import(bool force)
        {
            var sw = Stopwatch.StartNew();
            var r = new Result();
            var errors = VerifySources(out var manifestSha);
            r.manifestSha256 = manifestSha;
            if (errors.Count > 0)
                throw new CanonImportException($"{errors.Count} hash mismatch(es), import aborted:\n" + string.Join("\n", errors.Take(30)));

            string why = "forced";
            if (!force && IsCurrent(manifestSha, out why))
            {
                var layerErrors = VerifyImportedLayers(LoadImported());
                if (layerErrors.Count == 0)
                {
                    r.seconds = sw.Elapsed.TotalSeconds;
                    return r;
                }
                r.notes.Add("re-import: " + string.Join("; ", layerErrors));
            }
            else if (!force) r.notes.Add("import: " + why);

            DoImport(manifestSha, r);
            var after = VerifyImportedLayers(LoadImported());
            if (after.Count > 0) throw new CanonImportException("imported layers do not match the manifest: " + string.Join("; ", after));
            r.imported = true;
            r.seconds = sw.Elapsed.TotalSeconds;
            Debug.Log($"[BR] canon v1 imported in {r.seconds:F1} s (manifest {Short(manifestSha)}). " + string.Join(" ", r.notes));
            return r;
        }

        // ------------------------------------------------------------------ verification

        /// <summary>Checks every file of CanonData/v1/manifest.json and CanonData/refs/models/manifest.json (bytes and SHA-256).</summary>
        public static List<string> VerifySources(out string manifestSha256)
        {
            var errors = new List<string>();
            manifestSha256 = null;
            var manifestPath = Path.Combine(SourceDir, "manifest.json");
            if (!File.Exists(manifestPath)) { errors.Add("missing " + manifestPath); return errors; }
            manifestSha256 = Sha256File(manifestPath);
            VerifyManifestFiles(SourceDir, errors);
            VerifyManifestFiles(ModelsSourceDir, errors);
            return errors;
        }

        /// <summary>Every file listed in (dir)/manifest.json, checked for size and SHA-256. Empty list = all good.</summary>
        public static List<string> VerifyManifestDir(string dir)
        {
            var errors = new List<string>();
            VerifyManifestFiles(dir, errors);
            return errors;
        }

        static void VerifyManifestFiles(string dir, List<string> errors)
        {
            var path = Path.Combine(dir, "manifest.json");
            if (!File.Exists(path)) { errors.Add("missing " + path); return; }
            var m = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(path)), path);
            var files = CanonJson.Obj(CanonJson.Get(m, "files"), "files");
            if (files.Count == 0) errors.Add(path + " lists no files");
            foreach (var kv in files)
            {
                var f = CanonJson.Obj(kv.Value, kv.Key);
                var full = Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) { errors.Add("missing " + kv.Key); continue; }
                long bytes = new FileInfo(full).Length;
                long want = (long)CanonJson.Num(f, "bytes", -1);
                if (want >= 0 && bytes != want) { errors.Add($"{kv.Key}: {bytes} bytes, manifest says {want} (LFS pointer not pulled?)"); continue; }
                var sha = Sha256File(full);
                var wantSha = CanonJson.Str(f, "sha256");
                if (!string.Equals(sha, wantSha, StringComparison.OrdinalIgnoreCase)) errors.Add($"{kv.Key}: sha256 {sha} != manifest {wantSha}");
            }
        }

        public static Dictionary<string, (long bytes, string sha256)> ManifestFiles(string dir)
        {
            var m = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"))));
            var d = new Dictionary<string, (long, string)>();
            foreach (var kv in CanonJson.Obj(CanonJson.Get(m, "files")))
            {
                var f = CanonJson.Obj(kv.Value);
                d[kv.Key] = ((long)CanonJson.Num(f, "bytes", -1), CanonJson.Str(f, "sha256"));
            }
            return d;
        }

        static bool IsCurrent(string manifestSha, out string why)
        {
            var a = LoadImported();
            if (a == null) { why = "no import yet"; return false; }
            if (a.manifestSha256 != manifestSha) { why = "manifest changed"; return false; }
            if (a.importerVersion != ImporterVersion) { why = $"importer version {a.importerVersion} -> {ImporterVersion}"; return false; }
            if (a.world == null || a.placements == null || a.vegetation == null || a.surfaces == null || a.traffic == null) { why = "data assets missing"; return false; }
            if (a.buildings == null || a.trees == null || a.staticProps == null) { why = "model assets missing"; return false; }
            if (a.detail == null || a.waterNormal == null || a.dirt == null || a.map == null) { why = "textures missing"; return false; }
            foreach (var l in a.Layers) if (l.data == null) { why = "layer missing: " + l.path; return false; }
            if (a.buildings.sha256 != ModelSha("buildings.glb") || a.trees.sha256 != ModelSha("trees.glb")) { why = "models changed"; return false; }
            why = null;
            return true;
        }

        static string ModelSha(string file) => ManifestFiles(ModelsSourceDir).TryGetValue(file, out var e) ? e.sha256 : "?";

        /// <summary>The 7 imported .bytes layers hash to the manifest values.</summary>
        public static List<string> VerifyImportedLayers(CanonAsset a)
        {
            var errors = new List<string>();
            if (a == null) { errors.Add("no CanonAsset"); return errors; }
            foreach (var l in a.Layers)
            {
                if (l.data == null) { errors.Add(l.path + " not imported"); continue; }
                var full = Path.Combine(RepoRoot, "Game", AssetDatabase.GetAssetPath(l.data));
                var sha = File.Exists(full) ? Sha256File(full) : "missing";
                if (!string.Equals(sha, l.sha256, StringComparison.OrdinalIgnoreCase)) errors.Add($"{l.path}: imported copy {Short(sha)} != {Short(l.sha256)}");
            }
            return errors;
        }

        public static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var s = File.OpenRead(path);
            return CanonRuntime.Hex(sha.ComputeHash(s));
        }

        static string Short(string s) => string.IsNullOrEmpty(s) ? "?" : s.Substring(0, Math.Min(12, s.Length));

        // ------------------------------------------------------------------ the import itself

        static void DoImport(string manifestSha, Result r)
        {
            var files = ManifestFiles(SourceDir);
            var models = ManifestFiles(ModelsSourceDir);
            Directory.CreateDirectory(ToFull(OutDir));
            Directory.CreateDirectory(ToFull(ResourcesDir));
            Directory.CreateDirectory(ToFull(ModelsDir));

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var (src, name, _) in LayerFiles)
                    CopyWithMeta(Path.Combine(SourceDir, src), $"{OutDir}/{name}.bytes", TextScriptMeta);
                // a .meta with only the GUID: Unity (glTFast's importer) fills in its default settings
                CopyWithMeta(Path.Combine(ModelsSourceDir, "buildings.glb"), $"{ModelsDir}/buildings.glb", "");
                CopyWithMeta(Path.Combine(ModelsSourceDir, "trees.glb"), $"{ModelsDir}/trees.glb", "");
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (var (_, name, _) in LayerFiles) AssetDatabase.ImportAsset($"{OutDir}/{name}.bytes", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset($"{ModelsDir}/buildings.glb", ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset($"{ModelsDir}/trees.glb", ImportAssetOptions.ForceSynchronousImport);

            var json = new Dictionary<string, Dictionary<string, object>>();
            Dictionary<string, object> J(string rel)
            {
                if (!json.TryGetValue(rel, out var o)) json[rel] = o = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(SourceDir, rel))), rel);
                return o;
            }

            var world = CanonDataBuilders.BuildWorldInfo(J("world.json"), J("reference/cameras.json"));
            var surfaces = CanonDataBuilders.BuildSurfaceTable(J("world.json"));
            var placements = CanonDataBuilders.BuildPlacements(J("placements.json"));
            var vegetation = CanonDataBuilders.BuildVegetation(J("vegetation.json"));
            var traffic = CanonDataBuilders.BuildTraffic(J("traffic.json"));

            world = SaveAsset(world, $"{OutDir}/WorldInfo.asset");
            surfaces = SaveAsset(surfaces, $"{OutDir}/SurfaceTable.asset");
            placements = SaveAsset(placements, $"{OutDir}/PlacementSet.asset");
            vegetation = SaveAsset(vegetation, $"{OutDir}/VegetationSet.asset");
            traffic = SaveAsset(traffic, $"{OutDir}/LegacyTrafficNetwork.asset");

            var detail = SaveTexture(Path.Combine(SourceDir, "textures/detail.png"), $"{OutDir}/detail.asset", linear: true, TextureWrapMode.Repeat, 4);
            var waterN = SaveTexture(Path.Combine(SourceDir, "textures/water_n.png"), $"{OutDir}/water_n.asset", linear: true, TextureWrapMode.Repeat, 4);
            var dirt = SaveTexture(Path.Combine(SourceDir, "textures/dirt.png"), $"{OutDir}/dirt.asset", linear: true, TextureWrapMode.Repeat, 1);
            var map = SaveTexture(Path.Combine(SourceDir, "textures/map.png"), $"{OutDir}/map.asset", linear: false, TextureWrapMode.Clamp, 1);

            var buildingSet = CanonModelSets.Build($"{ModelsDir}/buildings.glb", models["buildings.glb"].sha256, r.notes);
            buildingSet = SaveAsset(buildingSet, $"{OutDir}/BuildingModels.asset");
            var treeSet = CanonModelSets.Build($"{ModelsDir}/trees.glb", models["trees.glb"].sha256, r.notes);
            treeSet = SaveAsset(treeSet, $"{OutDir}/TreeModels.asset");

            var props = StaticPropsBaker.Bake(Path.Combine(SourceDir, "reference/static_props.glb"), Path.Combine(ModelsSourceDir, "buildings.glb"),
                                              placements, ManifestStaticProps(), r.notes, out var meshes);
            props.glbSha256 = files["reference/static_props.glb"].sha256;
            props = SaveAssetWithSubAssets(props, $"{OutDir}/StaticProps.asset", meshes);

            var asset = ScriptableObject.CreateInstance<CanonAsset>();
            asset.version = 1;
            asset.manifestSha256 = manifestSha;
            asset.importerVersion = ImporterVersion;
            asset.importedAtUtc = DateTime.UtcNow.ToString("o");
            asset.oracleCommit = CanonJson.Str(CanonJson.Obj(CanonJson.Get(CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(SourceDir, "manifest.json")))), "source")), "commit");
            CanonAsset.Layer L(int i)
            {
                var (src, name, _) = LayerFiles[i];
                return new CanonAsset.Layer
                {
                    path = src, bytes = files[src].bytes, sha256 = files[src].sha256,
                    data = AssetDatabase.LoadAssetAtPath<TextAsset>($"{OutDir}/{name}.bytes"),
                };
            }
            asset.heights = L(0); asset.@base = L(1); asset.surface = L(2); asset.wear = L(3); asset.roadd = L(4); asset.mud = L(5); asset.tint = L(6);
            asset.world = world; asset.surfaces = surfaces; asset.placements = placements; asset.vegetation = vegetation; asset.traffic = traffic;
            asset.detail = detail; asset.waterNormal = waterN; asset.dirt = dirt; asset.map = map;
            asset.buildings = buildingSet; asset.trees = treeSet; asset.staticProps = props;
            foreach (var l in asset.Layers) if (l.data == null) throw new CanonImportException("layer did not import: " + l.path);
            SaveAsset(asset, CanonAssetPath);
            AssetDatabase.SaveAssets();
            CanonRuntime.Unload();
        }

        static Dictionary<string, (int vertices, string positionSha256)> ManifestStaticProps()
        {
            var m = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(SourceDir, "manifest.json"))));
            var d = new Dictionary<string, (int, string)>();
            foreach (var kv in CanonJson.Obj(CanonJson.Get(m, "staticProps"), "staticProps"))
            {
                var b = CanonJson.Obj(kv.Value);
                d[kv.Key] = ((int)CanonJson.Num(b, "vertices"), CanonJson.Str(b, "positionSha256"));
            }
            return d;
        }

        // ------------------------------------------------------------------ asset helpers

        public static string ToFull(string assetPath) => Path.Combine(RepoRoot, "Game", assetPath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Path-derived GUID (PLAN 1.2 [C15]): the same path always gets the same GUID on every machine.</summary>
        public static string PathGuid(string assetPath)
        {
            using var md5 = MD5.Create();
            return CanonRuntime.Hex(md5.ComputeHash(Encoding.UTF8.GetBytes("backroads-canon:" + assetPath)));
        }

        const string TextScriptMeta = "TextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
        const string NativeMeta = "NativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 11400000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";
        const string NativeTextureMeta = "NativeFormatImporter:\n  externalObjects: {}\n  mainObjectFileID: 2800000\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n";

        static void WriteMeta(string assetPath, string importerBody)
        {
            var meta = ToFull(assetPath) + ".meta";
            var text = $"fileFormatVersion: 2\nguid: {PathGuid(assetPath)}\n{importerBody}";
            if (File.Exists(meta) && File.ReadAllText(meta).Contains("guid: " + PathGuid(assetPath))) return;
            File.WriteAllText(meta, text, new UTF8Encoding(false));
        }

        static void CopyWithMeta(string src, string assetPath, string importerBody)
        {
            CopyFile(src, assetPath);
            WriteMeta(assetPath, importerBody);
        }

        static void CopyFile(string src, string assetPath)
        {
            var dst = ToFull(assetPath);
            if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(src).Length && Sha256File(dst) == Sha256File(src)) return;
            File.Copy(src, dst, true);
        }

        /// <summary>Creates the asset, or copies into the existing one so its GUID (and every reference to it) stays.</summary>
        /// <summary>
        /// Writes the asset with a path-derived GUID: AssetDatabase.CreateAsset always assigns a random GUID, so the
        /// object is created at a temporary path, its file is copied to the final path next to our own .meta, and the
        /// temporary asset is deleted. Re-imports overwrite the same file, so the GUID (and every reference) stays.
        /// </summary>
        static T SaveAsset<T>(T obj, string path, List<Mesh> subAssets = null) where T : Object
        {
            obj.name = Path.GetFileNameWithoutExtension(path);
            var tmp = path.Substring(0, path.LastIndexOf('/')) + "/_import_" + Path.GetFileName(path);
            if (AssetDatabase.LoadMainAssetAtPath(tmp) != null) AssetDatabase.DeleteAsset(tmp);
            AssetDatabase.CreateAsset(obj, tmp);
            if (subAssets != null) foreach (var m in subAssets) AssetDatabase.AddObjectToAsset(m, obj);
            EditorUtility.SetDirty(obj);
            AssetDatabase.SaveAssetIfDirty(obj);
            var existing = AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null && AssetDatabase.AssetPathToGUID(path) != PathGuid(path)) { AssetDatabase.DeleteAsset(path); existing = null; }
            File.Copy(ToFull(tmp), ToFull(path), true);
            WriteMeta(path, obj is Texture2D ? NativeTextureMeta : NativeMeta);
            AssetDatabase.DeleteAsset(tmp);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            var loaded = AssetDatabase.LoadAssetAtPath<T>(path);
            if (loaded == null) throw new CanonImportException("could not write " + path);
            return loaded;
        }

        static T SaveAssetWithSubAssets<T>(T obj, string path, List<Mesh> subAssets) where T : Object => SaveAsset(obj, path, subAssets);

        /// <summary>
        /// PNG to a Texture2D asset with rows as in the JS DataTexture (row 0 = v 0): LoadImage puts the PNG's first
        /// row at the top (v = 1), so the rows are flipped back.
        /// </summary>
        static Texture2D SaveTexture(string png, string assetPath, bool linear, TextureWrapMode wrap, int aniso)
        {
            var tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
            if (!tmp.LoadImage(File.ReadAllBytes(png), false)) throw new CanonImportException("cannot decode " + png);
            int w = tmp.width, h = tmp.height;
            var px = tmp.GetPixels32();
            Object.DestroyImmediate(tmp);
            var flipped = new Color32[px.Length];
            for (int y = 0; y < h; y++) Array.Copy(px, (h - 1 - y) * w, flipped, y * w, w);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true, linear)
            {
                name = Path.GetFileNameWithoutExtension(png), wrapMode = wrap, filterMode = FilterMode.Trilinear, anisoLevel = aniso,
            };
            tex.SetPixels32(flipped);
            tex.Apply(true, false);
            return SaveAsset(tex, assetPath);
        }
    }
}
