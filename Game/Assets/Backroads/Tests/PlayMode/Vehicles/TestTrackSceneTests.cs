using System.Collections;
using System.Linq;
using Backroads.Core;
using Backroads.Vehicles;
using Backroads.Vehicles.Client;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Backroads.Tests.PlayMode.Vehicles
{
    public partial class Vehicle
    {
        public const string TestTrackScenePath = "Assets/Backroads/Scenes/BrvTestTrack.unity";

        static void Shot(Camera cam, string name)
        {
            var rt = RenderTexture.GetTemporary(1600, 900, 24);
            var old = cam.targetTexture;
            var active = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1600, 900, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(ArtifactsDir(), name), tex.EncodeToPNG());
                Object.Destroy(tex);
            }
            finally
            {
                cam.targetTexture = old;
                RenderTexture.active = active;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        /// <summary>
        /// The BrvTestTrack scene (card T3.5 step 2) loads from its file and comes up: the track on its own
        /// WorldGridLayout, drawn by the terrain chunks (with a graphics device), the four M2 vehicles on BRV v2 with the
        /// player in the truck, and the truck drives off on driver input.
        /// </summary>
        [UnityTest, Category("BrvV2")]
        public IEnumerator TestTrackScene()
        {
#if UNITY_EDITOR
            var op = UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(TestTrackScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Assert.NotNull(op, "could not load " + TestTrackScenePath);
            while (!op.isDone) yield return null;
            var scene = SceneManager.GetSceneByPath(TestTrackScenePath);
            try
            {
                float t0 = Time.realtimeSinceStartup;
                while ((BrvTestTrackWorld.Current == null || (!BrvTestTrackWorld.Current.Ready && BrvTestTrackWorld.Current.Error == null))
                       && Time.realtimeSinceStartup - t0 < 60) yield return null;
                var w = BrvTestTrackWorld.Current;
                Assert.NotNull(w, "no BrvTestTrackWorld in the scene");
                Assert.IsNull(w.Error, w.Error);
                Assert.IsTrue(w.Ready, "the track did not come up within 60 s");
                Assert.AreEqual(BrvTestTrack.Layout, w.Grid.Layout, "the track's own layout");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    Assert.NotNull(w.Terrain, "the track is not drawn (look/terrain material)");
                    Assert.Greater(w.Terrain.ChunkCount, 0);
                }
                var sys = w.SimSystem;
                Assert.AreEqual(ClassicVehicleDefs.M2.Length, sys.Vehicles.Count);
                Assert.IsTrue(Enumerable.Range(0, sys.Vehicles.Count).All(i => sys.Fleet.IsV2(i)), "every vehicle on BRV v2");
                Assert.AreEqual(BrvTestTrackWorld.StartVehicle, sys.Player.Def.id);
                Assert.AreSame(w.ViewOf(sys.Player), PlayerVehicle.Current, "PlayerVehicle.Current follows the player");
                var truck = sys.Player;
                var p0 = truck.Position;
                t0 = Time.realtimeSinceStartup;
                long steps0 = sys.TotalSteps;
                while (Time.realtimeSinceStartup - t0 < 4)
                {
                    truck.SetInput(new DriveInput { Throttle = 0.6f });
                    yield return null;
                }
                float moved = Vector3.Distance(truck.Position, p0);
                TestContext.WriteLine($"BrvTestTrack: {sys.Vehicles.Count} vehicles on v2, terrain {(w.Terrain != null ? w.Terrain.ChunkCount + " chunks" : "not drawn")}; " +
                                      $"the truck drove {moved:F1} m in {sys.TotalSteps - steps0} fixed steps, {truck.State.speed * 3.6:F1} km/h, gear {truck.State.gear}");
                Assert.Greater(moved, 1f, "the truck did not drive off");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null && w.cam != null)
                {
                    // two pictures for the handoff: the chase camera behind the truck, and the track from above the ramps
                    Shot(w.cam, "brv-testtrack-chase.png");
                    var pose = (w.cam.transform.position, w.cam.transform.rotation);
                    foreach (var mb in w.cam.GetComponents<MonoBehaviour>()) mb.enabled = false;
                    // Unity x = -x (JS): the ramp lanes are at Unity x 35..145, the steps at -10..10
                    w.cam.transform.SetPositionAndRotation(new Vector3(10f, 40f, -50f), Quaternion.Euler(16f, 35f, 0f));
                    if (w.Terrain != null) w.Terrain.Update(w.cam.transform.position);
                    yield return null;
                    Shot(w.cam, "brv-testtrack-overview.png");
                    foreach (var mb in w.cam.GetComponents<MonoBehaviour>()) mb.enabled = true;
                    w.cam.transform.SetPositionAndRotation(pose.Item1, pose.Item2);
                }
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) SceneManager.UnloadSceneAsync(scene);
            }
            yield return null;
#else
            Assert.Ignore("needs the editor (EditorSceneManager)");
            yield break;
#endif
        }
    }
}
