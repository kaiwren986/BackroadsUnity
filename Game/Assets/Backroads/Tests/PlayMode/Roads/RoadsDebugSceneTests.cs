using System.Collections;
using System.IO;
using Backroads.Client;
using Backroads.Roads;
using Backroads.Roads.Client;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Backroads.Tests.PlayMode.Roads
{
    /// <summary>
    /// RoadsDebug.unity in play mode (lane R, T2.8 step 4): Millbrook loads, the road graph overlay and the R0 plain
    /// ribbons are built from the baked asset, the M1 ribbons are found for the side-by-side view, and four views are
    /// rendered to artifacts/roads/ (graph over the town, downtown oblique, side by side, whole network). Needs a graphics
    /// device (unity-tests.ps1 -Platform PlayMode without -NoGraphics).
    /// </summary>
    [TestFixture, Category("Roads")]
    public class RoadsDebugScene
    {
        const string ScenePath = "Assets/Backroads/Scenes/RoadsDebug.unity";

        static string OutDir => Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..")), "artifacts", "roads");

        [UnityTest]
        public IEnumerator LoadsAndRendersViews()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("no graphics device (-nographics)");
#if UNITY_EDITOR
            yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
#else
            Assert.Ignore("editor only");
#endif
            float t0 = Time.realtimeSinceStartup;
            while ((RoadsDebugView.Active == null || !RoadsDebugView.Active.Ready) && Time.realtimeSinceStartup - t0 < 180f) yield return null;
            var view = RoadsDebugView.Active;
            Assert.NotNull(view, "no RoadsDebugView in the scene");
            Assert.IsTrue(view.Ready, "RoadsDebug did not get ready in 180 s");
            var world = MillbrookWorld.Current;
            Assert.NotNull(world, "no MillbrookWorld");
            Assert.IsTrue(world.Ready, "Millbrook failed: " + world.Error);
            Assert.NotNull(view.M1Ribbons, "lane W's M1 ribbons were not found under the Millbrook object");
            Assert.NotNull(view.R0Ribbons);
            Assert.NotNull(view.Overlay);
            var fly = view.mainCamera.GetComponent<FreeFlyCamera>();
            if (fly != null) fly.ScriptedControl = true;

            Directory.CreateDirectory(OutDir);
            int w = 1920, h = 1080;
            var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "roads shots", antiAliasing = 1 };
            rt.Create();
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            try
            {
                yield return Shot(view, world, RoadsDebugView.Mode.Graph, view.ViewTown, rt, tex, "rd-graph-town.png");
                yield return Shot(view, world, RoadsDebugView.Mode.Graph, view.ViewDowntown, rt, tex, "rd-graph-downtown.png");
                yield return Shot(view, world, RoadsDebugView.Mode.SideBySide, view.ViewDowntown, rt, tex, "rd-ribbons-side-by-side.png");
                yield return Shot(view, world, RoadsDebugView.Mode.Ribbons, () => view.ViewNode(2), rt, tex, "rd-r0-ribbons-node2.png");
                yield return Shot(view, world, RoadsDebugView.Mode.Graph, view.ViewOverview, rt, tex, "rd-graph-network.png");
            }
            finally
            {
                view.mainCamera.targetTexture = null;
                if (view.RightCamera != null) view.RightCamera.targetTexture = null;
                rt.Release();
                Object.Destroy(rt);
                Object.Destroy(tex);
            }
            foreach (var f in new[] { "rd-graph-town.png", "rd-graph-downtown.png", "rd-ribbons-side-by-side.png", "rd-r0-ribbons-node2.png", "rd-graph-network.png" })
                Assert.IsTrue(File.Exists(Path.Combine(OutDir, f)), f);
        }

        static IEnumerator Shot(RoadsDebugView view, MillbrookWorld world, RoadsDebugView.Mode mode, System.Action pose, RenderTexture rt, Texture2D tex, string file)
        {
            view.SetMode(mode);
            pose();
            world.SettleTerrain();
            world.SetHour(11f);
            for (int f = 0; f < 10; f++) yield return null;
            var cams = mode == RoadsDebugView.Mode.SideBySide && view.RightCamera != null ? new[] { view.mainCamera, view.RightCamera } : new[] { view.mainCamera };
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = prev;
            // three renders: GPU occlusion culling uses the camera's previous depth, so the first render after a big camera
            // move can cull the whole world
            for (int pass = 0; pass < 3; pass++)
                foreach (var cam in cams)
                {
                    view.Overlay.FaceCamera(cam);
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = null;
                }
            prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = prev;
            var path = Path.Combine(OutDir, file);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            // a frame that is all one colour means nothing was drawn
            var px = tex.GetPixels32();
            int differ = 0;
            for (int i = 0; i < px.Length; i += 997) if (!px[i].Equals(px[0])) differ++;
            Assert.Greater(differ, 10, file + " looks empty");
            TestContext.WriteLine("wrote " + path);
            view.SetMode(RoadsDebugView.Mode.Graph);
        }
    }
}
