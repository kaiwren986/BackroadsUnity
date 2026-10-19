using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Backroads.EngineSim.Editor
{
    /// <summary>
    /// Writes Assets/Backroads/Scenes/EngineBench.unity: a camera and a light. EngineBench creates itself when a scene named
    /// EngineBench loads (panel and engine built in code), so the scene holds no client script for server builds to miss.
    /// Batchmode: tools\ci\project-unity.ps1 -Method Backroads.EngineSim.Editor.BenchSceneBuilder.Build
    /// </summary>
    public static class BenchSceneBuilder
    {
        public const string ScenePath = "Assets/Backroads/Scenes/EngineBench.unity";

        [MenuItem("Backroads/Engine Bench/Rebuild EngineBench.unity")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var cam = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cam.tag = "MainCamera";
            var c = cam.GetComponent<Camera>();
            c.clearFlags = CameraClearFlags.SolidColor;
            c.backgroundColor = new Color(0.10f, 0.11f, 0.12f);
            cam.transform.position = new Vector3(0f, 1.2f, -4f);
            var light = new GameObject("Directional Light", typeof(Light));
            light.GetComponent<Light>().type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("could not save " + ScenePath);
            Debug.Log("[BR] wrote " + ScenePath);
        }
    }
}
