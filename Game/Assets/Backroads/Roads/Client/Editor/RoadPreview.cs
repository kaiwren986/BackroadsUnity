using System;
using System.Collections.Generic;
using System.IO;
using Backroads.Roads.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Roads.Client.Editor
{
    /// <summary>
    /// roads-R3 in pictures: the three road materials (Roads/Client/Resources) and a preview renderer. Preview builds an
    /// unsaved scene with the town's road chunks, its road props (RoadPropPlacer), a sun, and the DEM as ground with R2's
    /// road plates applied (the reference evaluation; lane W's terrain does this for real), then renders a few fixed
    /// views to artifacts/roads/(town)/r3-*.png. Needs a graphics device (batchmode without -nographics):
    ///   Unity.exe -batchmode -projectPath Game -executeMethod Backroads.Roads.Client.Editor.RoadPreview.Shots -quit
    /// </summary>
    public static class RoadPreview
    {
        public const string ResDir = "Assets/Backroads/Roads/Client/Resources";

        [MenuItem("Backroads/Roads/Create road materials (R3)")]
        public static void EnsureMaterials()
        {
            var shader = Shader.Find("Backroads/Roads/Surface");
            if (shader == null) throw new InvalidOperationException("shader Backroads/Roads/Surface not found");
            Make(RoadChunkView.AsphaltMaterial, shader, new Color(0.055f, 0.055f, 0.06f), 0.93f, 0.6f, false);
            Make(RoadChunkView.ConcreteMaterial, shader, new Color(0.40f, 0.39f, 0.37f), 0.82f, 0.35f, false);
            Make(RoadChunkView.PaintMaterial, shader, Color.white, 0.62f, 0.3f, true);
            AssetDatabase.SaveAssets();
        }

        static void Make(string name, Shader shader, Color c, float rough, float detail, bool paint)
        {
            string path = ResDir + "/" + name + ".mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            bool fresh = m == null;
            if (fresh) m = new Material(shader);
            m.shader = shader;
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Roughness", rough);
            m.SetFloat("_DetailStrength", detail);
            m.SetFloat("_Paint", paint ? 1 : 0);
            m.SetFloat("_OffsetFactor", paint ? -1 : 0);
            m.SetFloat("_OffsetUnits", paint ? -2 : 0);
            m.enableInstancing = true;
            if (fresh) AssetDatabase.CreateAsset(m, path);
            else EditorUtility.SetDirty(m);
        }

        public static void Shots()
        {
            int code = 0;
            try
            {
                foreach (var town in TownRoadSource.Towns())
                    foreach (var p in Render(town)) Debug.Log("[BR] wrote " + p);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        /// <summary>
        /// The Synty sign prefabs RoadPropPlacer uses, each rendered from +z and from -z with its bounds logged (looking at
        /// the packs is allowed; they are never changed): artifacts/roads/props-*.png. Batchmode with graphics.
        /// </summary>
        public static void Props()
        {
            int code = 0;
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var sunGo = new GameObject("Sun");
                var sun = sunGo.AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 1.6f;
                sunGo.transform.rotation = Quaternion.Euler(40, 30, 0);
                RenderSettings.ambientMode = AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f);
                var dir = RoadNetworkBaker.ArtifactsDir;
                var audit = new List<(string key, string path)>();
                foreach (var kv in RoadPropPlacer.Prefabs) audit.Add((kv.Key.ToString(), kv.Value.path));
                const string props = "Assets/Synty/PolygonCity/Prefabs/Props/";
                foreach (var extra in new[] { "SM_Prop_Sign_Street_02", "SM_Prop_TrafficLight_02", "SM_Prop_TrafficLight_03", "SM_Prop_LightPole_Base_01", "SM_Prop_Sign_Attachment_01" })
                    audit.Add((extra, props + extra + ".prefab"));
                foreach (var kv in audit)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(kv.path);
                    if (prefab == null) { Debug.Log($"[BR] {kv.key}: no prefab at {kv.path}"); continue; }
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    var b = new Bounds(go.transform.position, Vector3.zero);
                    foreach (var r in go.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                    Debug.Log($"[BR] {kv.key}: {prefab.name} bounds centre {b.center} size {b.size}");
                    float d = Mathf.Max(b.size.y, Mathf.Max(b.size.x, b.size.z)) * 1.6f + 1f;
                    var target = b.center;
                    Shot(dir, $"props-{kv.key}-from-plus-z.png", target, target + new Vector3(0, 0, d));
                    Shot(dir, $"props-{kv.key}-from-above.png", target, target + new Vector3(0, d, 0.01f));
                    Shot(dir, $"props-{kv.key}-from-minus-z.png", target, target + new Vector3(0, 0, -d));
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static List<string> Render(string town)
        {
            EnsureMaterials();
            var paths = new List<string>();
            var net = RoadNetworkBaker.LoadBakedTown(town);
            if (net == null) throw new InvalidOperationException("no baked road network for " + town);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // a bake in memory for the plates and the junction to look at
            var a = RoadNetworkBaker.BakeTownWithKerbs(town, out var bake, out var kerbs, out var frontage);
            UnityEngine.Object.DestroyImmediate(a);
            UnityEngine.Object.DestroyImmediate(frontage);
            var plates = RoadPlateBuilder.Build(bake.graph, bake.surfaces, kerbs.kerbs, bake.source.dem, kerbs.settings);
            var field = new PlateField(plates);

            Globals();
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 2.2f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(48, 210, 0);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.47f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.25f, 0.24f, 0.2f);
            RenderSettings.fog = false;

            RoadChunkView.SpawnTown(town, null);
            RoadPropPlacer.Place(net);

            // views: the downtown main-street junction close up, the same from above, a residential corner, a rural stretch
            var views = Views(bake, kerbs);
            int k = 0;
            foreach (var (name, target, eye) in views)
            {
                var ground = Ground(bake.source.dem, field, target, 140, 1.0f);
                ground.name = "Ground (DEM + plates)";
                paths.Add(Shot(RoadNetworkBaker.TownArtifactsDir(town), $"r3-{k++:D2}-{name}.png", target, eye));
                UnityEngine.Object.DestroyImmediate(ground);
            }
            return paths;
        }

        static List<(string, Vector3, Vector3)> Views(SurfaceBaker.Bake b, KerbBaker.Bake k)
        {
            var g = b.graph;
            var list = new List<(string, Vector3, Vector3)>();
            Vector3 P(D3 p) => new Vector3((float)p.x, (float)p.y, (float)p.z);
            // the main-street junction nearest the downtown zone's middle
            GraphNode down = null;
            if (g.downtown != null && g.downtown.Length >= 3)
            {
                double cx = 0, cz = 0;
                foreach (var q in g.downtown) { cx += q.x; cz += q.z; }
                var mid = new D2(cx / g.downtown.Length, cz / g.downtown.Length);
                foreach (var n in g.nodes)
                {
                    if (n.Degree < 3 || b.surfaces.Of(n.id)?.mode == SurfaceMode.Fallback) continue;
                    bool main = false;
                    foreach (var a in n.arms) main |= g.arms[a].cls == RoadClass.MainStreet;
                    if (main && (down == null || D2.Distance(n.p.XZ, mid) < D2.Distance(down.p.XZ, mid))) down = n;
                }
            }
            if (down != null)
            {
                var t = P(down.p);
                list.Add(("downtown-junction", t, t + new Vector3(-22, 9, -26)));
                list.Add(("downtown-above", t, t + new Vector3(-3, 55, -12)));
            }
            // a residential street corner with a STOP
            foreach (var n in g.nodes)
            {
                if (n.Degree != 4 || n == down) continue;
                bool streets = true;
                foreach (var a in n.arms) streets &= g.arms[a].cls == RoadClass.Street;
                if (!streets) continue;
                var t = P(n.p);
                list.Add(("residential-corner", t, t + new Vector3(14, 6, -18)));
                break;
            }
            // a rural highway with its no-passing lines: the longest Highway edge, from above its middle
            GraphEdge best = null;
            foreach (var e in g.edges) if (e.cls == RoadClass.Highway && (best == null || e.Length > best.Length)) best = e;
            if (best != null)
            {
                var mid = Polyline.At(best.pts, best.s, best.Length * 0.5);
                var t = P(mid);
                list.Add(("rural-highway", t, t + new Vector3(30, 22, -40)));
            }
            return list;
        }

        /// <summary>The DEM around a point as a mesh, with the road plates applied where they reach (1 m grid), plain green.</summary>
        static GameObject Ground(IHeightField dem, PlateField field, Vector3 c, float size, float step)
        {
            int n = (int)(size / step) + 1;
            var v = new Vector3[n * n];
            var col = new Color32[n * n];
            for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    double x = c.x - size * 0.5 + i * step, z = c.z - size * 0.5 + j * step;
                    double y = dem.Sample(x, z);
                    var plated = field.Sample(new D2(x, z), out double py, out var win);
                    if (plated) y = py;
                    v[j * n + i] = new Vector3((float)x, (float)y, (float)z);
                    bool road = plated && (win.surface == 4 || win.surface == 6);
                    col[j * n + i] = road ? new Color32(70, 66, 60, 255) : plated && win.surface == 5 ? new Color32(120, 104, 82, 255) : new Color32(74, 104, 52, 255);
                }
            var tris = new int[(n - 1) * (n - 1) * 6];
            int t = 0;
            for (int j = 0; j + 1 < n; j++)
                for (int i = 0; i + 1 < n; i++)
                {
                    int a = j * n + i, b = a + 1, cc = a + n, d = cc + 1;
                    tris[t++] = a; tris[t++] = cc; tris[t++] = b;
                    tris[t++] = b; tris[t++] = cc; tris[t++] = d;
                }
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.vertices = v;
            mesh.colors32 = col;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            var go = new GameObject("Ground");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Simple Lit") ?? Shader.Find("Universal Render Pipeline/Lit"));
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Lane W's shader globals the road shader reads: the detail noise and a daytime sky.</summary>
        static void Globals()
        {
            var detail = Path.Combine(Backroads.Build.Editor.RepoPaths.Root, "CanonData", "v1", "textures", "detail.png");
            if (File.Exists(detail))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Repeat };
                tex.LoadImage(File.ReadAllBytes(detail));
                Shader.SetGlobalTexture("_BrDetail", tex);
            }
            Shader.SetGlobalVector("_BrSkyZenith", new Vector4(0.25f, 0.45f, 0.8f, 1));
            Shader.SetGlobalVector("_BrSkyHorizon", new Vector4(0.7f, 0.78f, 0.86f, 1));
            Shader.SetGlobalVector("_BrSkyGlow", new Vector4(1f, 0.85f, 0.6f, 0.2f));
            Shader.SetGlobalVector("_BrSunDir", new Vector4(0.4f, 0.75f, 0.5f, 0));
            Shader.SetGlobalVector("_BrSky", new Vector4(0, 13, 0, 1));
        }

        static string Shot(string dir, string file, Vector3 target, Vector3 eye)
        {
            Directory.CreateDirectory(dir);
            var go = new GameObject("Preview camera");
            var cam = go.AddComponent<Camera>();
            cam.transform.position = eye;
            cam.transform.LookAt(target);
            cam.fieldOfView = 50;
            cam.nearClipPlane = 0.2f;
            cam.farClipPlane = 2000;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.72f, 0.85f);
            var rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            var path = Path.Combine(dir, file);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(go);
            return path;
        }
    }
}
