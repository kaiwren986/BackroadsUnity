using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// Batch entry point for lane A's Unity steps (run through art\pipeline\unity-art.ps1, which takes a Unity slot):
    ///   -executeMethod Backroads.Art.Editor.ArtBatch.Run -brArtSteps materials,probe,compare,capture [-brArtOut dir]
    /// materials: create the vehicle slot materials (ArtMaterials). probe: RemapProbe on the truck FBX. compare:
    /// ModelCompare of the truck's GLB and FBX imports. capture: side-by-side renders (needs a GPU: no -nographics).
    /// JSON and PNG go to -brArtOut (default art/vehicles/truck/review). Exits 0 when every step ran.
    /// </summary>
    public static class ArtBatch
    {
        public const string TruckDir = "Assets/Art/Vehicles/Truck";
        public const string TruckFbx = TruckDir + "/VH_Truck.fbx";
        public const string TruckGlb = TruckDir + "/truck.glb";

        static string RepoRoot => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));

        static string Arg(string name, string fallback)
        {
            var a = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback;
        }

        static void Write(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text.Replace("\r", ""), new UTF8Encoding(false));
            Log.Info("art: wrote " + path);
        }

        public static void Run()
        {
            int code = 0;
            var steps = Arg("-brArtSteps", "materials,probe,compare").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var outDir = Path.GetFullPath(Path.Combine(RepoRoot, Arg("-brArtOut", "art/vehicles/truck/review")));
            BackroadsModelPostprocessor.Verbose = true;
            foreach (var step in steps)
            {
                try
                {
                    switch (step)
                    {
                        case "materials": ArtMaterials.CreateVehicleSlots(); break;
                        case "reimport":
                            foreach (var p in new[] { TruckFbx, TruckGlb })
                                AssetDatabase.ImportAsset(p, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
                            break;
                        case "probe": Write(Path.Combine(outDir, "remap-probe.json"), RemapProbe.ToJson(RemapProbe.Run(TruckFbx))); break;
                        case "compare":
                            var r = ModelCompare.Compare(Load(TruckGlb), Load(TruckFbx));
                            Write(Path.Combine(outDir, "glb-vs-fbx.json"), JsonUtility.ToJson(r, true) + "\n");
                            Log.Info($"art compare: names {r.namesMatch}, slots {r.slotsMatch}, tris {r.trisMatch}, max position {r.maxPositionDelta:G3} m, " +
                                     $"rotation {r.maxRotationDeltaDeg:G3} deg, bounds {r.maxBoundsDelta * 1000f:G3} mm");
                            break;
                        case "capture": ArtCapture.Truck(outDir); break;
                        default: throw new ArgumentException("unknown step " + step);
                    }
                    Log.Info("art step " + step + " ok");
                }
                catch (Exception e)
                {
                    Log.Error("art step " + step + " failed: " + e);
                    code = 1;
                }
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static GameObject Load(string path)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) throw new FileNotFoundException("not imported: " + path);
            return go;
        }
    }

    /// <summary>Renders the truck's two imports side by side, and the FBX truck's two LOD levels, for the review.</summary>
    public static class ArtCapture
    {
        const int W = 1600, H = 900;

        public static void Truck(string outDir)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new Exception("no GPU: run without -nographics");
            Directory.CreateDirectory(outDir);
            var glb = ArtBatch.Load(ArtBatch.TruckGlb);
            var fbx = ArtBatch.Load(ArtBatch.TruckFbx);
            // Both at LOD0: at this framing the FBX's LOD Group would already pick LOD1, and glTFast shows every node
            // (Body and Body_LOD1 on top of each other), so the shot gives the GLB instance the same LOD rule first.
            Shot(Path.Combine(outDir, "truck_glb_vs_fbx.png"), new[] { (glb, 0, "GLB via glTFast"), (fbx, 0, "FBX via Blender") });
            Shot(Path.Combine(outDir, "truck_fbx_lod0_lod1.png"), new[] { (fbx, 0, "FBX LOD0"), (fbx, 1, "FBX LOD1") });
        }

        static void Shot(string path, (GameObject prefab, int forceLod, string label)[] items)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.68f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.50f, 0.50f, 0.50f);
            RenderSettings.ambientGroundColor = new Color(0.28f, 0.26f, 0.24f);
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.5f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(45f, -140f, 0f);

            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.localScale = new Vector3(6f, 1f, 6f);
            var gm = new Material(lit);
            gm.SetColor("_BaseColor", new Color(0.42f, 0.44f, 0.40f));
            gm.SetFloat("_Smoothness", 0.1f);
            ground.GetComponent<Renderer>().sharedMaterial = gm;

            const float spacing = 4.2f;
            var all = new Bounds();
            bool any = false;
            var placed = new List<(GameObject go, string label)>();
            for (int i = 0; i < items.Length; i++)
            {
                var (prefab, lod, label) = items[i];
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab) ?? UnityEngine.Object.Instantiate(prefab);
                go.transform.position = new Vector3(((items.Length - 1) * 0.5f - i) * spacing, 0f, 0f);   // item 0 on the left of the image
                if (lod >= 0 && go.GetComponentsInChildren<LODGroup>(true).Length == 0) LodFromNames.Build(go);   // scene instance only
                if (lod >= 0) foreach (var g in go.GetComponentsInChildren<LODGroup>(true)) g.ForceLOD(lod);
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (!any) { all = r.bounds; any = true; } else all.Encapsulate(r.bounds);
                }
                placed.Add((go, label));
            }

            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.78f, 0.82f, 0.86f);
            cam.fieldOfView = 30f;
            float radius = all.extents.magnitude;
            float dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.78f;
            var dir = Quaternion.Euler(16f, 148f, 0f) * Vector3.back;   // front three-quarter view, from the vehicle's left
            cam.transform.position = all.center + dir * dist;
            cam.transform.LookAt(all.center + Vector3.down * 0.3f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = dist + radius * 3f;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            foreach (var (go, label) in placed)
            {
                if (font == null) break;
                var t = new GameObject("Label").AddComponent<TextMesh>();
                t.font = font;
                t.text = label;
                t.fontSize = 64;
                t.characterSize = 0.06f;
                t.anchor = TextAnchor.LowerCenter;
                t.color = new Color(0.08f, 0.09f, 0.10f);
                t.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                t.transform.position = go.transform.position + new Vector3(0f, 0.05f, all.max.z + 1.2f);   // on the ground in front
                t.transform.rotation = Quaternion.LookRotation(t.transform.position - cam.transform.position);
            }

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            cam.targetTexture = null;
            rt.Release();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Log.Info("art: wrote " + path);
        }
    }
}
