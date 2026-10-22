using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Backroads.Client;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Backroads.Tests.PlayMode.World
{
    /// <summary>
    /// Look.NoErrorShaders (T3.3 done-when, PLAN M5 headline gate): Millbrook.unity loads in play mode with no error in the
    /// log, and every renderer of the built world (terrain chunks, water, road chunks, lane R's props, buildings, trees,
    /// rocks) and the sky draw with a material whose shader URP can draw: no missing material, no error shader, no
    /// shader without a URP pass (the built-in Standard shader draws magenta under URP), no reference to a missing
    /// texture. Needs a graphics device for shader support (unity-tests.ps1 -Platform PlayMode without -NoGraphics).
    /// </summary>
    [TestFixture, Category("World")]
    public class Look
    {
        const string ScenePath = "Assets/Backroads/Scenes/Millbrook.unity";

        static readonly HashSet<string> UrpLightModes = new HashSet<string>
        {
            "", "SRPDefaultUnlit", "UniversalForward", "UniversalForwardOnly", "UniversalGBuffer", "Universal2D",
        };

        [UnityTest]
        public IEnumerator NoErrorShaders()
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("editor only");
#endif
            float t0 = Time.realtimeSinceStartup;
            while ((TownWorld.Current == null || (!TownWorld.Current.Ready && TownWorld.Current.Error == null)) && Time.realtimeSinceStartup - t0 < 240f) yield return null;
            var world = TownWorld.Current;
            Assert.NotNull(world, "no TownWorld in " + ScenePath);
            Assert.IsNull(world.Error, "Millbrook failed");
            Assert.IsTrue(world.Ready, "Millbrook did not get ready in 240 s");
            Assert.Greater(world.Terrain.ChunkCount, 0, "terrain");
            Assert.NotNull(world.WaterRoot, "water");
            Assert.NotNull(world.RoadsRoot, "road chunks");
            Assert.Greater(world.Models + world.GreyBoxes, 0, "buildings");
            Assert.Greater(world.Vegetation.Trees, 0, "trees");
            yield return null;
            yield return null;

            bool device = SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;
            var problems = new List<string>();
            var seen = new HashSet<Material>();
            var renderers = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            int checkedRenderers = 0;
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                checkedRenderers++;
                var mats = r.sharedMaterials;
                if (mats.Length == 0) problems.Add($"{Path(r.transform)}: no material");
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) { problems.Add($"{Path(r.transform)}: material {i} missing"); continue; }
                    if (!seen.Add(m)) continue;
                    var why = Check(m, device);
                    if (why != null) problems.Add($"{Path(r.transform)}: material {m.name}: {why}");
                }
            }
            if (RenderSettings.skybox != null && seen.Add(RenderSettings.skybox))
            {
                var why = Check(RenderSettings.skybox, device);
                if (why != null) problems.Add("sky: " + why);
            }
            Debug.Log($"[BR] Look.NoErrorShaders: {checkedRenderers} renderers, {seen.Count} materials, {problems.Count} problems; " +
                      $"{world.Models} models, {world.GreyBoxes} grey boxes, {world.Vegetation.Trees} trees");
            Assert.Greater(checkedRenderers, 1000, "too few renderers: did the world build?");
            Assert.IsEmpty(problems, string.Join("\n", problems.Take(40)));
        }

        static string Check(Material m, bool device)
        {
            var s = m.shader;
            if (s == null) return "no shader";
            if (s.name == "Hidden/InternalErrorShader") return "error shader (the shader failed to compile or is missing)";
            if (device && !s.isSupported) return $"shader {s.name} not supported on {SystemInfo.graphicsDeviceType}";
            bool urp = false;
            for (int p = 0; p < s.passCount && !urp; p++) urp = UrpLightModes.Contains(s.FindPassTagValue(p, new ShaderTagId("LightMode")).name ?? "");
            if (!urp) return $"shader {s.name} has no URP pass (draws magenta)";
#if UNITY_EDITOR
            // a texture slot that names an asset which no longer exists: the reference is kept, the object is gone
            var so = new UnityEditor.SerializedObject(m);
            var envs = so.FindProperty("m_SavedProperties.m_TexEnvs");
            for (int i = 0; envs != null && i < envs.arraySize; i++)
            {
                var e = envs.GetArrayElementAtIndex(i);
                var tex = e.FindPropertyRelative("second.m_Texture");
                if (tex != null && tex.objectReferenceValue == null && tex.objectReferenceInstanceIDValue != 0)
                    return $"missing texture in {e.FindPropertyRelative("first").stringValue}";
            }
#endif
            return null;
        }

        static string Path(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }
}
