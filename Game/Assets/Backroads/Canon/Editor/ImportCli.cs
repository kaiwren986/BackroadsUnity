using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Backroads.Canon.Editor
{
    /// <summary>
    /// Batchmode entry (PLAN 1.9):
    ///   Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Canon.Editor.ImportCli.Run [-force]
    /// Exit code 0 when the canon is imported (or the cached import is current and verified), 1 on any failure
    /// (a hash mismatch included).
    /// </summary>
    public static class ImportCli
    {
        public static void Run()
        {
            int code = 0;
            try
            {
                bool force = Environment.GetCommandLineArgs().Contains("-force");
                var r = CanonImporter.Import(force);
                Debug.Log($"[BR] ImportCli: canon v1 {(r.imported ? "imported" : "current")} in {r.seconds:F1} s, manifest {r.manifestSha256}");
                foreach (var n in r.notes) Debug.Log("[BR] ImportCli: " + n);
            }
            catch (Exception e)
            {
                Debug.LogError("[BR] ImportCli FAILED: " + e.Message);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }

    /// <summary>
    /// Every player build carries canon: before a build, the cached import is verified (and redone when missing or
    /// stale); a hash mismatch fails the build. BuildCli should still call CanonImporter.EnsureImported() itself before
    /// BuildPipeline.BuildPlayer, so the import never runs inside the build callback (see the M1 handoff, "For I").
    /// </summary>
    sealed class CanonBuildPreprocess : IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (!CanonImporter.EnsureImported(out var message)) throw new BuildFailedException(message);
            Debug.Log("[BR] build: " + message);
        }
    }
}
