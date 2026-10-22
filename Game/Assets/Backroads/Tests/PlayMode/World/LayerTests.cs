using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Backroads.Client;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Backroads.Tests.PlayMode.World
{
    /// <summary>
    /// MillbrookLayers.StaticsAndRoads (T3.3 follow-ups, lane V's T3.6 request): in the new Millbrook every collider is on
    /// the Road layer (lane R's road chunks, lane S's site chunks: paved surfaces, kerbs, wheel stops) or on Static
    /// (buildings, open-roof posts, lane A's UCX_ colliders, site fences, lane R's and lane S's props), and none is left on
    /// Default; lane S's site chunks and fences are in the scene.
    /// </summary>
    [TestFixture, Category("World")]
    public class MillbrookLayers
    {
        const string ScenePath = "Assets/Backroads/Scenes/Millbrook.unity";

        [UnityTest]
        public IEnumerator StaticsAndRoads()
        {
#if !UNITY_EDITOR
            Assert.Ignore("editor only");
            yield break;
#else
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            float t0 = Time.realtimeSinceStartup;
            while ((TownWorld.Current == null || (!TownWorld.Current.Ready && TownWorld.Current.Error == null)) && Time.realtimeSinceStartup - t0 < 240f) yield return null;
            var w = TownWorld.Current;
            Assert.NotNull(w, "no TownWorld");
            Assert.IsTrue(w.Ready, "Millbrook not ready: " + w.Error);
            int road = LayerMask.NameToLayer("Road"), stat = LayerMask.NameToLayer("Static");
            Assert.GreaterOrEqual(road, 0, "no Road layer");
            Assert.GreaterOrEqual(stat, 0, "no Static layer");
            Assert.NotNull(w.SitesRoot, "lane S's site chunks are spawned");

            var all = Object.FindObjectsByType<Collider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var bad = new List<string>();
            int onRoad = 0, onStatic = 0, roadUnder = 0, sitesUnder = 0, buildingsUnder = 0, fences = 0;
            foreach (var c in all)
            {
                int l = c.gameObject.layer;
                if (l == road) onRoad++;
                else if (l == stat) onStatic++;
                else bad.Add($"{Path(c.transform)} ({c.GetType().Name}) on {LayerMask.LayerToName(l)}");
                if (w.RoadsRoot != null && c.transform.IsChildOf(w.RoadsRoot)) { roadUnder++; if (l != road) bad.Add($"{Path(c.transform)}: road chunk not on Road"); }
                if (c.transform.IsChildOf(w.SitesRoot))
                {
                    bool fence = c.transform.parent != null && c.transform.parent.name == Backroads.Sites.SiteChunks.FenceRoot;
                    if (fence) { fences++; if (l != stat) bad.Add($"{Path(c.transform)}: fence not on Static"); }
                    else { sitesUnder++; if (l != road) bad.Add($"{Path(c.transform)}: site chunk not on Road"); }
                }
                if (w.BuildingsRoot != null && c.transform.IsChildOf(w.BuildingsRoot)) { buildingsUnder++; if (l != stat) bad.Add($"{Path(c.transform)}: building not on Static"); }
            }
            Debug.Log($"[BR] MillbrookLayers: {all.Length} colliders, {onRoad} on Road, {onStatic} on Static; road chunks {roadUnder}, " +
                      $"site chunks {sitesUnder}, fences {fences}, buildings {buildingsUnder}");
            Assert.Greater(roadUnder, 0, "road chunk colliders");
            Assert.Greater(sitesUnder, 0, "site chunk colliders");
            Assert.Greater(buildingsUnder, 0, "building colliders");
            Assert.IsEmpty(bad, string.Join("\n", bad.Take(40)));
#endif
        }

        static string Path(Transform t)
        {
            var s = t.name;
            for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
            return s;
        }
    }
}
