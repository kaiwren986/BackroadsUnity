using System;
using Backroads.Client;
using Backroads.Client.Editor;
using Backroads.Client.Rendering;
using Backroads.Roads.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Backroads.Roads.Client.Editor
{
    /// <summary>
    /// Writes lane R's RoadsDebug.unity and its two materials (Roads/Client/Resources, so Dev builds can load them): a
    /// camera with lane W's fly camera, the sun, the global volume, lane W's MillbrookWorld (unchanged; it builds Millbrook
    /// and its M1 ribbons at run time) and the RoadsDebugView + RoadNetworkView of the baked R0 road network.
    ///   menu: Backroads > Roads > Build RoadsDebug scene
    ///   batchmode: -executeMethod Backroads.Roads.Client.Editor.RoadsDebugSceneBuilder.Build
    /// Re-running keeps every GUID.
    /// </summary>
    public static class RoadsDebugSceneBuilder
    {
        public const string ScenePath = "Assets/Backroads/Scenes/RoadsDebug.unity";
        public const string ResDir = "Assets/Backroads/Roads/Client/Resources";
        public const string OverlayMatPath = ResDir + "/" + RoadGraphOverlay.MaterialResource + ".mat";
        public const string RibbonMatPath = ResDir + "/BrRoadsRibbon.mat";

        [MenuItem("Backroads/Roads/Build RoadsDebug scene")]
        static void Menu() => BuildAll();

        public static void Build()
        {
            int code = 0;
            try { BuildAll(); }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static void BuildAll()
        {
            RoadNetworkBaker.EnsureFolder(ResDir);
            var overlay = Mat(OverlayMatPath, "Backroads/Roads/Debug Overlay", 0.55f);
            var ribbon = Mat(RibbonMatPath, "Backroads/Roads/Debug Ribbon", 0.42f);
            AssetDatabase.SaveAssets();
            var network = RoadNetworkBaker.LoadBaked() ?? throw new InvalidOperationException("bake the road network first (" + RoadNetworkBaker.AssetPath + ")");
            var look = AssetDatabase.LoadAssetAtPath<MillbrookLook>(MillbrookSceneBuilder.LookPath)
                       ?? throw new InvalidOperationException("lane W's look asset is missing: " + MillbrookSceneBuilder.LookPath);
            CreateScene(look, network, overlay, ribbon);
            Debug.Log("[BR] RoadsDebug scene written: " + ScenePath);
        }

        /// <summary>tint &lt; 1 keeps the debug colours saturated through the scene's exposure and ACES tonemapping.</summary>
        static Material Mat(string path, string shader, float tint)
        {
            var sh = Shader.Find(shader) ?? throw new InvalidOperationException("shader not found: " + shader);
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = new Material(sh) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
                AssetDatabase.CreateAsset(m, path);
            }
            else m.shader = sh;
            m.SetColor("_Color", new Color(tint, tint, tint, 1f));
            EditorUtility.SetDirty(m);
            return m;
        }

        static void CreateScene(MillbrookLook look, RoadNetworkAsset network, Material overlay, Material ribbon)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 60f;
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 6000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            camGo.AddComponent<AudioListener>();
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.renderShadows = true;
            camGo.transform.SetPositionAndRotation(new Vector3(85f, 330f, 85f), Quaternion.Euler(90f, 0f, 0f));
            camGo.AddComponent<FreeFlyCamera>();

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
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
            world.startBookmark = -1;
            world.startHour = 11f;

            var roadsGo = new GameObject("Roads debug (R0)");
            var view = roadsGo.AddComponent<RoadsDebugView>();
            view.network = network;
            view.overlayMaterial = overlay;
            view.ribbonMaterial = ribbon;
            view.mainCamera = cam;
            var gizmos = roadsGo.AddComponent<RoadNetworkView>();
            gizmos.network = network;

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
