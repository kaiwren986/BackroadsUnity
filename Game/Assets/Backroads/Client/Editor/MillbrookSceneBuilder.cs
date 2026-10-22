using System;
using System.IO;
using Backroads.Client.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Backroads.Client.Editor
{
    /// <summary>
    /// Writes lane W's committed M1 assets: the materials and MillbrookLook.asset under Client.Rendering/Materials, the
    /// post-processing profile, and Scenes/CanonTestWorld.unity, the canon test world (the old Millbrook.unity, renamed in
    /// T3.1, ADR-006 section 1: camera with FreeFlyCamera + CameraBookmarks, the sun, a global volume and the
    /// MillbrookWorld composition root; everything else is built at run time from CanonData v1).
    ///   menu: Backroads > Build canon test world scene
    ///   batchmode: -executeMethod Backroads.Client.Editor.MillbrookSceneBuilder.Build
    /// Re-running keeps every GUID (assets are updated in place).
    /// </summary>
    public static class MillbrookSceneBuilder
    {
        public const string ScenePath = "Assets/Backroads/Scenes/CanonTestWorld.unity";
        public const string MatDir = "Assets/Backroads/Client.Rendering/Materials";
        public const string LookPath = MatDir + "/MillbrookLook.asset";
        public const string VolumePath = MatDir + "/MillbrookVolume.asset";
        public const string ClientConfigPath = "Assets/Backroads/Client/Resources/BrClientConfig.asset";
        public const string BindingsPath = "Assets/Content/core/bindings.json";

        [MenuItem("Backroads/Build canon test world scene")]
        public static void BuildMenu() => BuildAll();

        public static void Build()
        {
            int code = 0;
            try { BuildAll(); }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static void BuildAll()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Backroads/Client.Rendering/Materials"));
            var look = CreateLook();
            CreateClientConfig();
            CreateScene(look);
            AssetDatabase.SaveAssets();
            Debug.Log("[BR] canon test world scene and look assets written");
        }

        // ------------------------------------------------------------------ materials

        static Material Mat(string name, string shader)
        {
            var path = $"{MatDir}/{name}.mat";
            var sh = Shader.Find(shader) ?? throw new InvalidOperationException("shader not found: " + shader);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(sh) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            else m.shader = sh;
            m.shaderKeywords = Array.Empty<string>();
            return m;
        }

        static void Toggle(Material m, string prop, string keyword, bool on)
        {
            m.SetFloat(prop, on ? 1 : 0);
            if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
        }

        /// <summary>A Backroads/Lit Color material (props.js MeshStandardMaterial parameters).</summary>
        static Material Lit(string name, int baseHex, float roughness, float metalness, bool vertexColor = false,
                            int emissiveHex = 0, float emissionDay = 0, float emissionNight = 0, bool tint = false, bool wind = false)
        {
            var m = Mat(name, "Backroads/Lit Color");
            m.SetColor("_BaseColor", BrColor.Hex(baseHex));
            m.SetFloat("_Smoothness", 1 - roughness);
            m.SetFloat("_Metallic", metalness);
            m.SetColor("_EmissionColor", emissiveHex != 0 ? BrColor.Hex(emissiveHex) : Color.black);
            m.SetFloat("_EmissionBase", emissionDay);
            m.SetFloat("_EmissionNight", emissionNight);
            m.SetFloat("_Cull", (float)CullMode.Back);
            Toggle(m, "_VertexColor", "_VERTEX_COLOR", vertexColor);
            Toggle(m, "_UserTint", "_USER_TINT", tint);
            Toggle(m, "_Wind", "_WIND", wind);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static MillbrookLook CreateLook()
        {
            var look = AssetDatabase.LoadAssetAtPath<MillbrookLook>(LookPath);
            if (look == null)
            {
                look = ScriptableObject.CreateInstance<MillbrookLook>();
                AssetDatabase.CreateAsset(look, LookPath);
            }
            look.terrain = Mat("M_Terrain", "Backroads/SG_Terrain_V0");
            look.terrain.SetFloat("_Roughness", 0.93f);
            look.road = Mat("M_Road", "Backroads/Road");
            look.road.SetFloat("_Roughness", 0.86f);
            look.water = Mat("M_Water", "Backroads/Water");
            var wc = BrColor.Hex(0x103f52);
            look.water.SetColor("_BaseColor", new Color(wc.r, wc.g, wc.b, 0.86f));
            look.water.SetFloat("_Smoothness", 0.94f);
            look.water.SetFloat("_Metallic", 0.1f);
            look.sky = Mat("M_Sky", "Backroads/Sky");
            look.lightPool = Mat("M_LightPool", "Backroads/Light Pool");
            look.lightPool.SetColor("_Color", BrColor.Hex(0xffc27a));
            look.lightPool.SetFloat("_Opacity", 0.2f);
            look.sunScale = 0.318f;
            look.ambientScale = 1f;
            look.dayExposureEv = 0.76f;
            look.lampIntensity = 20f;
            look.lampRange = 26f;
            look.emissionScale = 0.6f;

            // buildings.glb parts (props.js mats.building / metal / bulb / signRed / windowLit / windowDark)
            look.buildingPart = Lit("M_BuildingPart", 0xffffff, 0.82f, 0.04f);
            look.buildingMetal = Lit("M_BuildingMetal", 0xffffff, 0.45f, 0.65f);
            look.bulb = Lit("M_Bulb", 0xfff3d6, 0.4f, 0f, emissiveHex: 0xffd49a, emissionDay: 0.3f, emissionNight: 2.4f);
            look.sign = Lit("M_SignRed", 0x551111, 0.5f, 0f, emissiveHex: 0xff3b2f, emissionDay: 0.3f, emissionNight: 3.5f);
            look.glassLit = Lit("M_WindowLit", 0x1d2a36, 0.15f, 0.2f, emissiveHex: 0xffbf6b, emissionDay: 0.05f, emissionNight: 1.5f);
            look.glassDark = Lit("M_WindowDark", 0x1a2530, 0.12f, 0.3f);
            var neon = Lit("M_Neon", 0x222222, 0.5f, 0f, emissiveHex: 0x39d5ff, emissionDay: 0.4f, emissionNight: 5f);
            look.propBuckets = new[]
            {
                Lit("M_PropsBuilding", 0xffffff, 0.82f, 0.04f, vertexColor: true),
                Lit("M_PropsMetal", 0xffffff, 0.45f, 0.65f, vertexColor: true),
                look.glassLit, look.glassDark, look.bulb, neon, look.sign,
            };
            look.tree = Lit("M_Tree", 0xffffff, 0.86f, 0f, vertexColor: true, tint: true, wind: true);
            look.rock = Lit("M_Rock", 0x8a847b, 0.92f, 0f, tint: true);
            look.volume = CreateVolume();
            foreach (var m in new Object[] { look.terrain, look.road, look.water, look.sky, look.lightPool }) EditorUtility.SetDirty(m);
            EditorUtility.SetDirty(look);
            return look;
        }

        static VolumeProfile CreateVolume()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(VolumePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, VolumePath);
            }
            T Get<T>() where T : VolumeComponent
            {
                if (!profile.TryGet<T>(out var c))
                {
                    c = profile.Add<T>(true);
                    c.name = typeof(T).Name;
                    AssetDatabase.AddObjectToAsset(c, profile);
                }
                return c;
            }
            var tm = Get<Tonemapping>();
            tm.mode.Override(TonemappingMode.ACES);
            var bloom = Get<Bloom>();
            bloom.threshold.Override(0.85f);
            bloom.intensity.Override(0.16f);
            bloom.scatter.Override(0.6f);
            var ca = Get<ColorAdjustments>();
            ca.postExposure.Override(0.66f);
            EditorUtility.SetDirty(profile);
            return profile;
        }

        // ------------------------------------------------------------------ client config (T2.6)

        /// <summary>Resources/BrClientConfig.asset, which carries Content/core/bindings.json into every client build.</summary>
        public static void CreateClientConfig()
        {
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Backroads/Client/Resources"));
            var bindings = AssetDatabase.LoadAssetAtPath<TextAsset>(BindingsPath) ?? throw new InvalidOperationException("missing " + BindingsPath);
            var cfg = AssetDatabase.LoadAssetAtPath<ClientConfig>(ClientConfigPath);
            if (cfg == null)
            {
                cfg = ScriptableObject.CreateInstance<ClientConfig>();
                AssetDatabase.CreateAsset(cfg, ClientConfigPath);
            }
            cfg.bindings = bindings;
            EditorUtility.SetDirty(cfg);
        }

        // ------------------------------------------------------------------ scene

        static void CreateScene(MillbrookLook look)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 6000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            camGo.AddComponent<AudioListener>();
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.renderShadows = true;
            camGo.transform.SetPositionAndRotation(new Vector3(-18.4f, 40.8f, -58.4f), Quaternion.Euler(10f, -44f, 0f));
            var fly = camGo.AddComponent<FreeFlyCamera>();

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = 1f;
            sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            sunGo.AddComponent<UniversalAdditionalLightData>();

            var volGo = new GameObject("Global Volume");
            var vol = volGo.AddComponent<Volume>();
            vol.isGlobal = true;
            vol.sharedProfile = look.volume;

            var worldGo = new GameObject("Millbrook");
            var world = worldGo.AddComponent<MillbrookWorld>();
            world.look = look;
            world.cam = cam;
            world.sun = sun;
            world.volume = vol;
            var bm = camGo.AddComponent<CameraBookmarks>();
            bm.world = world;
            camGo.AddComponent<VehicleCameraRig>();
            camGo.AddComponent<CameraDirector>();
            _ = fly;

            RenderSettings.skybox = look.sky;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.00105f;
            RenderSettings.sun = sun;

            EditorSceneManager.SaveScene(scene, ScenePath);
        }
    }
}
