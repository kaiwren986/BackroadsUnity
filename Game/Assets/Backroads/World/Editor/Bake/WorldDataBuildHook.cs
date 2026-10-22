using System.IO;
using Backroads.Build.Editor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.3: once Millbrook.unity exists (BuildCli.Scenes lists it then), a player build needs the imported WorldData
    /// (gitignored, made per worktree): imports it before the build when it is missing or stale, and fails the build when
    /// it cannot. The canon gets the same from BuildCli (CanonImporter.EnsureImported).
    /// </summary>
    sealed class WorldDataBuildHook : IPreprocessBuildWithReport
    {
        public int callbackOrder => -100;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!File.Exists(Path.Combine(RepoPaths.Game, MillbrookTownSceneBuilder.ScenePath))) return;
            if (!WorldDataImporter.EnsureImported(out var msg)) throw new BuildFailedException("WorldData: " + msg);
            UnityEngine.Debug.Log("[BR] " + msg);
        }
    }
}
