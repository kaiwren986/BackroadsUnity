using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Build.Editor;
using Backroads.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// Review renders of the generated buildings (card T3.4) under the project's URP: the five types side by side, the
    /// town's buildings on the real DEM ground around the downtown and the mill, and one house at its three LODs
    /// (art/buildings/review/, committed). <see cref="ContactSheet"/> puts Synty City buildings beside ours for the style
    /// check: that image stays in artifacts/ (gitignored) and is never committed or uploaded (AGENTS.md rule 3).
    /// Needs a GPU: art\pipeline\unity-art.ps1 -Steps building-capture -Graphics.
    /// </summary>
    public static class BuildingCapture
    {
        const int W = 1920, H = 1080;
        const string Town = "Assets/Art/Buildings/Millbrook";
        const string Fixtures = "Assets/Art/Buildings/_Fixtures";

        sealed class Entry
        {
            public string osm, model, family, fbx;
            public Vector3 origin;
        }

        static List<Entry> Index(string set)
        {
            var path = Path.Combine(RepoPaths.Game, set, "buildings.index.json");
            var root = BrJson.Obj(BrJson.Parse(File.ReadAllText(path, Encoding.UTF8)));
            var list = new List<Entry>();
            foreach (var o in BrJson.Arr(root["buildings"]))
            {
                var e = BrJson.Obj(o);
                var or = BrJson.Arr(e["origin"]).Select(x => Convert.ToSingle(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                list.Add(new Entry { osm = (string)e["osm"], model = (string)e["model"], family = (string)e["family"], fbx = set + "/" + (string)e["fbx"], origin = new Vector3(or[0], or[1], or[2]) });
            }
            return list;
        }

        static void Stage(Color sky)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.60f, 0.66f, 0.76f) * 1.05f;
            RenderSettings.ambientEquatorColor = new Color(0.52f, 0.52f, 0.50f);
            RenderSettings.ambientGroundColor = new Color(0.30f, 0.28f, 0.25f);
            RenderSettings.fog = false;
            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.55f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(42f, -38f, 0f);
            Sky = sky;
        }

        static Color Sky = new Color(0.72f, 0.79f, 0.86f);

        static Material Flat(Color c, float smooth = 0.05f)
        {
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", smooth);
            return m;
        }

        static GameObject Place(string path, Vector3 at, int forceLod = -1)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) throw new FileNotFoundException("not imported: " + path);
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.transform.position = at;
            if (forceLod >= 0) foreach (var g in go.GetComponentsInChildren<LODGroup>(true)) g.ForceLOD(forceLod);
            return go;
        }

        static Bounds BoundsOf(IEnumerable<GameObject> gos)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var go in gos)
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                {
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
            return b;
        }

        static void Label(string text, Vector3 at, Camera cam, float size = 0.09f)
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) return;
            var t = new GameObject("Label").AddComponent<TextMesh>();
            t.font = font;
            t.text = text;
            t.fontSize = 64;
            t.characterSize = size;
            t.anchor = TextAnchor.LowerCenter;
            t.color = new Color(0.08f, 0.09f, 0.10f);
            t.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            t.transform.position = at;
            t.transform.rotation = Quaternion.LookRotation(at - cam.transform.position);
        }

        static Camera Cam(Bounds b, float yaw, float pitch, float fov = 32f, float fill = 0.62f)
        {
            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Sky;
            cam.fieldOfView = fov;
            float radius = b.extents.magnitude;
            float dist = radius / Mathf.Sin(fov * 0.5f * Mathf.Deg2Rad) * fill;
            var dir = Quaternion.Euler(pitch, yaw, 0f) * Vector3.back;
            cam.transform.position = b.center + dir * dist;
            cam.transform.LookAt(b.center);
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = dist + radius * 4f;
            return cam;
        }

        static void Shoot(Camera cam, string path)
        {
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
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            Log.Info("art: wrote " + path);
        }

        static GameObject Ground(Vector3 centre, float size, float y)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Plane);
            g.transform.position = new Vector3(centre.x, y, centre.z);
            g.transform.localScale = new Vector3(size / 10f, 1f, size / 10f);
            g.GetComponent<Renderer>().sharedMaterial = Flat(new Color(0.36f, 0.40f, 0.30f));
            UnityEngine.Object.DestroyImmediate(g.GetComponent<Collider>());
            return g;
        }

        /// <summary>Terrain from the town's DEM (TownData/(town)/v1/dem) around a point, 2 m cells.</summary>
        static GameObject DemGround(Vector2 centre, float half)
        {
            var root = RepoPaths.InRepo("TownData");
            var v1 = Directory.GetDirectories(root).Select(d => Path.Combine(d, "v1")).First(d => File.Exists(Path.Combine(d, "manifest.json")));
            var meta = BrJson.Obj(BrJson.Parse(File.ReadAllText(Path.Combine(v1, "dem", "dem.json"))));
            int n = (int)Convert.ToDouble(meta["n"]);
            float x0 = (float)Convert.ToDouble(meta["x0"]), z0 = (float)Convert.ToDouble(meta["z0"]), res = (float)Convert.ToDouble(meta["res"]);
            var bytes = File.ReadAllBytes(Path.Combine(v1, "dem", (string)meta["file"]));
            float H(float x, float z)
            {
                int i = Mathf.Clamp(Mathf.RoundToInt((x - x0) / res), 0, n - 1), j = Mathf.Clamp(Mathf.RoundToInt((z - z0) / res), 0, n - 1);
                return BitConverter.ToSingle(bytes, (j * n + i) * 4);
            }
            const float step = 2f;
            int cells = Mathf.CeilToInt(2 * half / step);
            var verts = new List<Vector3>();
            for (int j = 0; j <= cells; j++)
                for (int i = 0; i <= cells; i++)
                {
                    float x = centre.x - half + i * step, z = centre.y - half + j * step;
                    verts.Add(new Vector3(x, H(x, z) - 0.05f, z));
                }
            var tris = new List<int>();
            for (int j = 0; j < cells; j++)
                for (int i = 0; i < cells; i++)
                {
                    int a = j * (cells + 1) + i, b = a + 1, c = a + cells + 1, d = c + 1;
                    tris.AddRange(new[] { a, c, b, b, c, d });
                }
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            var go = new GameObject("DEM");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = Flat(new Color(0.37f, 0.41f, 0.30f));
            return go;
        }

        public static void Review(string outDir)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new Exception("no GPU: run without -nographics");
            var fx = Index(Fixtures);
            var town = Index(Town);

            // 1. the five types, from the fixtures, on a flat lot (they stand on their fixture positions)
            // the fixtures' street is to the south (-z): yaw 20 looks at the street side, 200 at the rear
            var homes = new[] { "fixture/house_t", "fixture/house_l", "fixture/house_hip", "fixture/cottage", "fixture/garage", "fixture/row_shop", "fixture/row_restaurant", "fixture/row_office" };
            var work = new[] { "fixture/barn_gambrel", "fixture/barn_gable", "fixture/machineshed", "fixture/shed", "fixture/warehouse_gable", "fixture/warehouse_flat", "fixture/mill" };
            List<Entry> Pick(string[] ids) => ids.Select(id => fx.First(e => e.osm == id)).ToList();
            Stage(Sky);
            ShootSet(Pick(homes), Path.Combine(outDir, "types_houses_blocks_street.png"), 20f, 16f, true);
            Stage(Sky);
            ShootSet(Pick(homes), Path.Combine(outDir, "types_houses_blocks_rear.png"), 200f, 18f, true);
            Stage(Sky);
            ShootSet(Pick(work), Path.Combine(outDir, "types_barns_warehouses_mill_street.png"), 8f, 16f, true);
            Stage(Sky);
            ShootSet(Pick(work), Path.Combine(outDir, "types_barns_warehouses_mill_rear.png"), 188f, 18f, true);

            // 2. the town's buildings on the DEM: downtown, the mill by the dam, a residential street
            foreach (var (name, c, half, yaw) in new[] { ("millbrook_downtown.png", new Vector2(285f, 390f), 150f, 205f), ("millbrook_mill.png", new Vector2(560f, 380f), 110f, 160f),
                                                       ("millbrook_houses.png", new Vector2(110f, 560f), 130f, 215f) })
            {
                Stage(Sky);
                var near = town.Where(e => Mathf.Abs(e.origin.x - c.x) < half && Mathf.Abs(e.origin.z - c.y) < half).ToList();
                var gos = near.Select(e => Place(e.fbx, e.origin)).ToList();
                DemGround(c, half + 40f);
                var b = BoundsOf(gos);
                var cam = Cam(b, yaw, 38f, 34f, 0.55f);
                Shoot(cam, Path.Combine(outDir, name));
            }

            // 3. one house at LOD0, LOD1 and LOD2
            Stage(Sky);
            var house = fx.First(e => e.osm == "fixture/house_t");
            var lodGos = new List<GameObject>();
            for (int l = 0; l < 3; l++) lodGos.Add(Place(house.fbx, new Vector3(l * 18f, 0f, 0f), l));
            Ground(new Vector3(18f, 0f, 0f), 120f, -0.02f);
            var cb = BoundsOf(lodGos);
            var lc = Cam(cb, 20f, 16f, 30f, 0.6f);
            for (int l = 0; l < 3; l++) Label("LOD" + l, lodGos[l].transform.position + new Vector3(0f, 0.05f, -9f), lc, 0.12f);
            Shoot(lc, Path.Combine(outDir, "house_lod0_lod1_lod2.png"));
        }

        static void ShootSet(List<Entry> set, string path, float yaw, float pitch, bool labels)
        {
            // side by side in the given order, 5 m apart, each on the ground (fixture floors stand on y = 0 ground)
            var gos = new List<GameObject>();
            float x = 0f;
            foreach (var e in set)
            {
                var go = Place(e.fbx, Vector3.zero);
                var bb = BoundsOf(new[] { go });
                go.transform.position = new Vector3(x - bb.min.x, e.origin.y, -bb.center.z);
                x += bb.size.x + 5f;
                gos.Add(go);
            }
            var b = BoundsOf(gos);
            Ground(b.center, Mathf.Max(b.size.x, b.size.z) * 2.5f, -0.02f);
            var cam = Cam(b, yaw, pitch, 24f, 0.5f);
            if (labels)
                for (int i = 0; i < gos.Count; i++)
                {
                    var rb = BoundsOf(new[] { gos[i] });
                    var side = Mathf.Cos(yaw * Mathf.Deg2Rad) > 0 ? rb.min.z - 3f : rb.max.z + 3f;
                    Label(set[i].model, new Vector3(rb.center.x, 0.05f, side), cam, 0.16f);
                }
            Shoot(cam, path);
        }

        /// <summary>Synty City buildings (left) beside ours (right), same light and scale: local style check only.</summary>
        public static void ContactSheet(string outPath)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new Exception("no GPU: run without -nographics");
            var synty = new[] { "SM_Bld_Shop_01", "SM_Bld_Shop_03", "SM_Bld_Shop_04", "SM_Bld_OfficeOld_Small_02", "SM_Bld_Station_02", "SM_Bld_Apartment_Stack_01" };
            var fx = Index(Fixtures);
            var ours = new[] { "fixture/row_shop", "fixture/row_restaurant", "fixture/row_office", "fixture/house_t", "fixture/cottage", "fixture/mill" };
            Stage(Sky);
            var gos = new List<GameObject>();
            var names = new List<string>();
            float x = 0f;
            foreach (var s in synty)
            {
                var guid = AssetDatabase.FindAssets(s + " t:Prefab", new[] { SyntyStyleAudit.CityBuildings }).FirstOrDefault(g => Path.GetFileNameWithoutExtension(AssetDatabase.GUIDToAssetPath(g)) == s);
                if (guid == null) continue;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)));
                var b = BoundsOf(new[] { go });
                go.transform.position += new Vector3(x - b.min.x, -b.min.y, -b.center.z);
                x += b.size.x + 4f;
                gos.Add(go);
                names.Add("Synty " + s.Replace("SM_Bld_", ""));
            }
            x += 10f;
            foreach (var id in ours)
            {
                var e = fx.First(f => f.osm == id);
                var go = Place(e.fbx, Vector3.zero);
                var b = BoundsOf(new[] { go });
                go.transform.position = new Vector3(x - b.min.x, 0f, -b.center.z);
                x += b.size.x + 4f;
                gos.Add(go);
                names.Add("ours " + e.model);
            }
            var all = BoundsOf(gos);
            Ground(all.center, all.size.x * 2f, -0.02f);
            var cam = Cam(all, 180f, 14f, 22f, 0.5f);
            for (int i = 0; i < gos.Count; i++)
            {
                var b = BoundsOf(new[] { gos[i] });
                Label(names[i], new Vector3(b.center.x, 0.05f, b.min.z - 3f), cam, 0.13f);
            }
            Shoot(cam, outPath);
        }
    }
}
