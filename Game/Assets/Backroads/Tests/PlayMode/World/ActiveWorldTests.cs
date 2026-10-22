using System.Collections;
using Backroads.Client;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Active = Backroads.World.ActiveWorld;

namespace Backroads.Tests.PlayMode.World
{
    /// <summary>
    /// ActiveWorldScenes.ServesBothWorlds (T3.3, the contract lane V's T3.6 binds the vehicles to): the canon test world
    /// and the new Millbrook each set ActiveWorld when ready, with a grid, the static colliders, the vegetation and five
    /// garage bays (0-3 the M2 vehicles, 4 the dozer's) on that world's ground; leaving a world clears it, and Changed is
    /// raised for both. Loads CanonTestWorld.unity, then Millbrook.unity.
    /// </summary>
    [TestFixture, Category("World")]
    public class ActiveWorldScenes
    {
        const string CanonScene = "Assets/Backroads/Scenes/CanonTestWorld.unity";
        const string TownScene = "Assets/Backroads/Scenes/Millbrook.unity";

        int _changes;
        void OnChanged() => _changes++;

        [UnityTest]
        public IEnumerator ServesBothWorlds()
        {
#if !UNITY_EDITOR
            Assert.Ignore("editor only");
            yield break;
#else
            Active.Changed += OnChanged;
            try
            {
                yield return Load(CanonScene);
                float t0 = Time.realtimeSinceStartup;
                while ((MillbrookWorld.Current == null || (!MillbrookWorld.Current.Ready && MillbrookWorld.Current.Error == null)) && Time.realtimeSinceStartup - t0 < 240f) yield return null;
                Assert.NotNull(MillbrookWorld.Current, "no MillbrookWorld");
                Assert.IsTrue(MillbrookWorld.Current.Ready, "canon test world not ready: " + MillbrookWorld.Current.Error);
                Check("canon");
                var canonGrid = Active.Grid;
                int changes = _changes;

                yield return Load(TownScene);
                t0 = Time.realtimeSinceStartup;
                while ((TownWorld.Current == null || (!TownWorld.Current.Ready && TownWorld.Current.Error == null)) && Time.realtimeSinceStartup - t0 < 240f) yield return null;
                Assert.NotNull(TownWorld.Current, "no TownWorld");
                Assert.IsTrue(TownWorld.Current.Ready, "Millbrook not ready: " + TownWorld.Current.Error);
                Check("millbrook");
                Assert.AreNotSame(canonGrid, Active.Grid, "the town's own grid");
                Assert.GreaterOrEqual(_changes - changes, 2, "Changed on leaving the canon and on setting the town");
                Assert.IsTrue(Active.TryGetPoi("garage", out _), "the town's garage");
            }
            finally
            {
                Active.Changed -= OnChanged;
            }
#endif
        }

        static IEnumerator Load(string path)
        {
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            yield break;
#endif
        }

        static void Check(string name)
        {
            Assert.AreEqual(name, Active.Name, "ActiveWorld.Name");
            Assert.IsTrue(Active.IsSet, name + ": no grid");
            Assert.NotNull(Active.Colliders, name + ": no colliders");
            Assert.NotNull(Active.Vegetation, name + ": no vegetation");
            Assert.Greater(Active.Vegetation.trees.Length, 0, name + ": no trees");
            Assert.AreEqual(5, Active.GarageBays.Length, name + ": garage bays");
            Assert.IsTrue(Active.TryGrid(out var g) && ReferenceEquals(g, Active.Grid), name + ": TryGrid");
            float half = Active.Grid.Layout.Half;
            foreach (var b in Active.GarageBays)
            {
                Assert.Less(Mathf.Abs(b.x), half, name + ": bay outside the world");
                Assert.Less(Mathf.Abs(b.z), half, name + ": bay outside the world");
                float y = Ground.HeightAt(new Vector3(-b.x, 0, b.z));
                Assert.Greater(y, -1f, name + $": bay at ({-b.x:F1}, {b.z:F1}) under water ({y:F2} m)");
            }
        }
    }
}
