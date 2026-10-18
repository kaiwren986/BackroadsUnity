using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace Backroads.Build.Editor
{
    /// <summary>Repository locations seen from the Unity project (Game\ sits at the repo root).</summary>
    public static class RepoPaths
    {
        /// <summary>D:\Backroads-Unity (or a worktree such as D:\Backroads-Unity-wt\i).</summary>
        public static string Root => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        public static string Game => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        public static string Artifacts => Path.Combine(Root, "artifacts");
        public static string Builds => Path.Combine(Root, "builds");
        public static string UserPlayingLock => Path.Combine(Artifacts, ".user-playing");

        public static string InRepo(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>Repo-relative path with forward slashes.</summary>
        public static string Relative(string full)
        {
            var root = Root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var f = Path.GetFullPath(full);
            return (f.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? f.Substring(root.Length) : f).Replace('\\', '/');
        }

        /// <summary>Runs git in the repo and returns trimmed stdout, or null when git fails.</summary>
        public static string Git(string args, int timeoutMs = 20000)
        {
            try
            {
                var psi = new ProcessStartInfo("git", $"-C \"{Root}\" {args}")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    return p.ExitCode == 0 ? o.Trim() : null;
                }
            }
            catch { return null; }
        }
    }
}
