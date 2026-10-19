using System.Collections;
using System.IO;
using Backroads.Client;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>Shared helpers of lane W's player autotests (Millbrook).</summary>
    static class WorldScenarioUtil
    {
        /// <summary>Waits until MillbrookWorld has built everything (or failed, or the timeout ran out).</summary>
        public static IEnumerator WaitForMillbrook(AutotestContext ctx, float timeoutSeconds)
        {
            float t0 = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - t0 < timeoutSeconds)
            {
                var w = MillbrookWorld.Current;
                if (w != null && (w.Ready || w.Error != null)) break;
                yield return null;
            }
            var world = MillbrookWorld.Current;
            if (world == null) ctx.Fail("no MillbrookWorld in the first scene (is Millbrook.unity the first scene of the build?)");
            else if (world.Error != null) ctx.Fail("Millbrook failed to load: " + world.Error);
            else if (!world.Ready) ctx.Fail($"Millbrook not ready after {timeoutSeconds:F0} s");
        }

        /// <summary>Takes the camera away from the user input components.</summary>
        public static void Script(MillbrookWorld w, bool scripted)
        {
            var fly = w.cam.GetComponent<FreeFlyCamera>();
            if (fly != null) fly.ScriptedControl = scripted;
            var bm = w.cam.GetComponent<CameraBookmarks>();
            if (bm != null) bm.ScriptedControl = scripted;
        }

        /// <summary>The repository root (the folder with CanonData/v1/manifest.json) above the player, or null.</summary>
        public static string FindRepoRoot()
        {
            var d = new DirectoryInfo(Application.dataPath);
            for (int i = 0; i < 8 && d != null; i++, d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "CanonData", "v1", "manifest.json"))) return d.FullName;
            return null;
        }
    }
}
