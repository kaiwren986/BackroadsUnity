using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Backroads.Vehicles.Editor
{
    /// <summary>
    /// Editor validation (card T3.6, research 06 risk 8): no vehicle Rigidbody may have gravity on, because BRV applies
    /// gravity inside its own step and PhysX would add it a second time. Checks every prefab under the game's asset
    /// folders and every scene under Assets/Backroads/Scenes (vehicle bodies: a BrvChassisBody, or the Vehicle or
    /// VehicleHull layer). Menu Backroads > Vehicles > Validate chassis; EditMode test Vehicle.ChassisGravityValidation.
    /// </summary>
    public static class BrvChassisValidator
    {
        static readonly string[] PrefabRoots = { "Assets/Backroads", "Assets/Content", "Assets/Art/Vehicles" };
        const string SceneRoot = "Assets/Backroads/Scenes";

        [MenuItem("Backroads/Vehicles/Validate chassis (gravity off)")]
        public static void Menu()
        {
            var bad = ValidateProject(out int prefabs, out int scenes);
            foreach (var b in bad) Debug.LogError("[BR] " + b);
            Debug.Log($"[BR] chassis validation: {prefabs} prefabs, {scenes} scenes, {bad.Count} problems");
        }

        public static List<string> ValidateProject(out int prefabs, out int scenes)
        {
            var bad = new List<string>();
            var roots = PrefabRoots.Where(AssetDatabase.IsValidFolder).ToArray();
            var guids = roots.Length > 0 ? AssetDatabase.FindAssets("t:Prefab", roots) : new string[0];
            prefabs = 0;
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                prefabs++;
                bad.AddRange(BrvPhysxChassis.Validate(go).Select(p => path + ": " + p));
            }
            scenes = 0;
            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                foreach (var path in Directory.GetFiles(SceneRoot, "*.unity", SearchOption.AllDirectories).Select(p => p.Replace('\\', '/')))
                {
                    var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    scenes++;
                    foreach (var root in scene.GetRootGameObjects())
                        bad.AddRange(BrvPhysxChassis.Validate(root).Select(p => path + ": " + p));
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            finally
            {
                if (setup != null && setup.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            return bad;
        }
    }
}
