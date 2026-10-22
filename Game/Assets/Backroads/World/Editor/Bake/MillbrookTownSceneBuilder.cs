using System;
using Backroads.Client;
using Backroads.Client.Editor;
using Backroads.Client.Rendering;
using Backroads.Roads;
using Backroads.Roads.Client.Editor;
using Backroads.Sites.Client.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.3: writes Scenes/Millbrook.unity, the new real town's scene: a thin bootstrap (PLAN 1.2) with the camera
    /// (FreeFlyCamera), the sun, the global volume and the TownWorld root, which builds everything else at run time from
    /// the imported WorldData. Lane R's road props (RoadPropPlacer: STOP and YIELD signs, signals, street blades) are put
    /// under the root at build time, as lane R designed them, and so are lane S's site props (SitePropPlacer: fences, bins,
    /// signs, pumps; T3.3 follow-ups); where the Synty packs are not imported they are the lanes' own stand-ins. Both go on
    /// the Static physics layer (lane V's T3.6 request; nothing in them moves). Imports WorldData first when needed.
    ///   menu: Backroads > World > Build Millbrook scene (T3.3)
    ///   batchmode: -executeMethod Backroads.World.Bake.MillbrookTownSceneBuilder.Build
    /// </summary>
    public static class MillbrookTownSceneBuilder
    {
        public const string ScenePath = "Assets/Backroads/Scenes/Millbrook.unity";

        [MenuItem("Backroads/World/Build Millbrook scene (T3.3)")]
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
            if (!WorldDataImporter.EnsureImported(out var msg)) throw new InvalidOperationException(msg);
            Debug.Log("[BR] " + msg);
            var look = AssetDatabase.LoadAssetAtPath<MillbrookLook>(MillbrookSceneBuilder.LookPath)
                       ?? throw new InvalidOperationException("the look asset is missing (Backroads > Build canon test world scene): " + MillbrookSceneBuilder.LookPath);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 55f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 6000f;
            cam.clearFlags = CameraClearFlags.Skybox;
            camGo.AddComponent<AudioListener>();
            var camData = camGo.AddComponent<UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;
            camData.renderShadows = true;
            camGo.transform.SetPositionAndRotation(new Vector3(0f, 250f, -600f), Quaternion.Euler(20f, 0f, 0f));
            camGo.AddComponent<FreeFlyCamera>().extent = 1174f;
            camGo.AddComponent<VehicleCameraRig>();
            camGo.AddComponent<CameraDirector>();

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

            var worldGo = new GameObject("World");
            var world = worldGo.AddComponent<TownWorld>();
            world.world = WorldDataImporter.DefaultWorld;
            world.look = look;
            world.cam = cam;
            world.sun = sun;
            world.volume = vol;

            int staticLayer = TownWorld.LayerOf("Static");
            var towns = TownSource.Towns();
            var network = towns.Count == 1 ? RoadNetworkAsset.LoadTown(towns[0]) : null;
            var roadProps = network != null ? RoadPropPlacer.Place(network, worldGo.transform) : null;
            if (roadProps != null) TownWorld.SetLayer(roadProps, staticLayer);
            else Debug.LogWarning("[BR] no road network baked for the town: no road props");
            var siteProps = towns.Count == 1 ? SitePropPlacer.Place(towns[0], worldGo.transform) : null;
            if (siteProps != null) TownWorld.SetLayer(siteProps, staticLayer);
            else Debug.LogWarning("[BR] no site props baked for the town (lane S's SitesBakeCli.Bake)");

            RenderSettings.skybox = look.sky;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogDensity = 0.0007f;
            RenderSettings.sun = sun;

            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("could not save " + ScenePath);
            Debug.Log("[BR] wrote " + ScenePath);
        }
    }
}
