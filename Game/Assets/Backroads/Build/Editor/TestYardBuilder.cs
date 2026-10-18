using System.IO;
using Backroads.Game;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace Backroads.Build.Editor
{
    /// <summary>
    /// Writes the M0 boot scene TestYard.unity (PLAN T0.9): a grey plane and the axis canary markers on a slow turntable,
    /// seen from behind like a chase camera (Unity +Z = the model's front, -X = its left). Built only from shared and
    /// engine components, so the ServerHeadless player loads it without missing scripts.
    /// </summary>
    public static class TestYardBuilder
    {
        public const string ScenePath = "Assets/Backroads/Scenes/TestYard.unity";
        public const string MaterialFolder = "Assets/Backroads/Scenes/TestYard";
        public const string CanaryPath = "Assets/Art/Canary/CANARY.fbx";

        [MenuItem("Backroads/Project Setup/Rebuild TestYard scene")]
        public static void Rebuild() => EnsureScene(true);

        public static void EnsureScene(bool force)
        {
            if (!force && File.Exists(Path.Combine(RepoPaths.Game, ScenePath))) return;
            Directory.CreateDirectory(Path.Combine(RepoPaths.Game, MaterialFolder));

            var grey = Mat("M_TestYard_Grey", new Color(0.46f, 0.47f, 0.46f));
            var front = Mat("M_Canary_Front", new Color(0.85f, 0.18f, 0.15f));
            var left = Mat("M_Canary_Left", new Color(0.15f, 0.35f, 0.90f));
            var up = Mat("M_Canary_Up", new Color(0.20f, 0.75f, 0.25f));
            var axis = Mat("M_Canary_Axis", new Color(0.12f, 0.12f, 0.12f));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.62f, 0.72f);
            RenderSettings.ambientEquatorColor = new Color(0.42f, 0.44f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.21f, 0.20f);
            RenderSettings.skybox = null;

            var sun = new GameObject("Sun");
            var l = sun.AddComponent<Light>();
            l.type = LightType.Directional; l.intensity = 1.3f; l.shadows = LightShadows.Soft;
            l.color = new Color(1f, 0.96f, 0.9f);
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.70f, 0.78f);
            cam.fieldOfView = 50f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 500f;
            camGo.transform.position = new Vector3(1.2f, 1.6f, -3.6f);
            camGo.transform.LookAt(new Vector3(0f, 0.45f, 0.6f));
            cam.GetUniversalAdditionalCameraData();

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.layer = ProjectSetup.Layer("Static");
            ground.transform.localScale = new Vector3(6f, 1f, 6f);
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            ground.GetComponent<MeshRenderer>().sharedMaterial = grey;

            var table = new GameObject("Turntable");
            table.transform.position = new Vector3(0f, 0.15f, 0f); // badges (0.3 m) sit on the ground
            table.AddComponent<Turntable>().degreesPerSecond = 12f;

            var canaryAsset = AssetDatabase.LoadAssetAtPath<GameObject>(CanaryPath);
            if (canaryAsset != null)
            {
                var canary = (GameObject)PrefabUtility.InstantiatePrefab(canaryAsset, scene);
                canary.transform.SetParent(table.transform, false);
                foreach (var t in canary.GetComponentsInChildren<Transform>(true))
                {
                    Material m = t.name == "MARK_Front" ? front : t.name == "MARK_Left" ? left : t.name == "MARK_Up" ? up : null;
                    if (m == null) continue;
                    var r = t.GetComponent<MeshRenderer>();
                    if (r != null) r.sharedMaterial = m;
                    // A larger marker and an axis bar from the origin, so the 10 cm canary cubes read at a glance.
                    var big = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    big.name = t.name + "_Badge";
                    Object.DestroyImmediate(big.GetComponent<Collider>());
                    big.GetComponent<MeshRenderer>().sharedMaterial = m;
                    big.transform.SetParent(table.transform, false);
                    big.transform.position = t.position;
                    big.transform.localScale = Vector3.one * 0.3f;
                    var bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    bar.name = t.name + "_Axis";
                    Object.DestroyImmediate(bar.GetComponent<Collider>());
                    bar.GetComponent<MeshRenderer>().sharedMaterial = axis;
                    bar.transform.SetParent(table.transform, false);
                    var p = t.localPosition;
                    bar.transform.localPosition = p * 0.5f;
                    bar.transform.localScale = new Vector3(Mathf.Max(0.04f, Mathf.Abs(p.x)), Mathf.Max(0.04f, Mathf.Abs(p.y)), Mathf.Max(0.04f, Mathf.Abs(p.z)));
                }
            }
            else Debug.LogWarning("[BR] TestYard: " + CanaryPath + " not imported yet; the scene has no canary markers");

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
        }

        static Material Mat(string name, Color c)
        {
            var path = $"{MaterialFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                m = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(m, path);
            }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.2f);
            EditorUtility.SetDirty(m);
            return m;
        }
    }
}
