using System;
using Backroads.Client.Rendering;
using Backroads.Vehicles.Client;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Backroads.Vehicles.Editor
{
    /// <summary>
    /// Writes Assets/Backroads/Scenes/BrvTestTrack.unity (card T3.5 step 2): a camera at the south end of the pad, a sun,
    /// the default sky and the <see cref="BrvTestTrackWorld"/> composition root with lane W's look (terrain material).
    /// Everything else is built in code when the scene loads. Menu Backroads > Vehicles > Build BrvTestTrack scene, or
    /// -executeMethod Backroads.Vehicles.Editor.BrvTestTrackSceneBuilder.BuildCli.
    /// </summary>
    public static class BrvTestTrackSceneBuilder
    {
        public const string ScenePath = "Assets/Backroads/Scenes/BrvTestTrack.unity";
        public const string LookPath = "Assets/Backroads/Client.Rendering/Materials/MillbrookLook.asset";

        [MenuItem("Backroads/Vehicles/Build BrvTestTrack scene")]
        public static string Build()
        {
            var look = AssetDatabase.LoadAssetAtPath<MillbrookLook>(LookPath);
            if (look == null) throw new InvalidOperationException("no MillbrookLook at " + LookPath);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 2000f;
            cam.fieldOfView = 60f;
            camGo.AddComponent<AudioListener>();
            // Unity space: x = -x (JS); behind the vehicles' row at the south end of the pad, looking north
            camGo.transform.SetPositionAndRotation(new Vector3((float)-BrvTestTrack.PadStart.x, (float)BrvTestTrack.Ground + 6f, (float)BrvTestTrack.PadZ0 + 4f),
                                                   Quaternion.Euler(12f, 0f, 0f));

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.6f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            RenderSettings.sun = sun;
            RenderSettings.skybox = AssetDatabase.GetBuiltinExtraResource<Material>("Default-Skybox.mat");
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;

            var root = new GameObject("BrvTestTrack");
            var world = root.AddComponent<BrvTestTrackWorld>();
            world.look = look;
            world.cam = cam;
            world.sun = sun;

            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("could not save " + ScenePath);
            AssetDatabase.SaveAssets();
            return $"wrote {ScenePath} (camera, sun, BrvTestTrackWorld with {LookPath})";
        }

        public static void BuildCli()
        {
            try
            {
                Debug.Log("[BR] " + Build());
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
