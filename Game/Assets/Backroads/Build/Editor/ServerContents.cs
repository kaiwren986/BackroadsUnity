using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backroads.Build.Editor
{
    /// <summary>
    /// Architecture rule 8 [T15]: the ServerHeadless player's Managed\ folder holds no Backroads.Client* or
    /// Backroads.EngineSim (or Backroads.Testing) assembly, and its plugins folder has no EngineSimPlugin.dll.
    /// Used by BuildCli after every server build and by the EditMode test Build.ServerContents.
    /// </summary>
    public static class ServerContents
    {
        public static readonly string[] Required = { "Backroads.Core.dll", "Backroads.Game.dll", "Backroads.Server.dll" };

        public static bool IsForbiddenAssembly(string fileName) =>
            fileName.StartsWith("Backroads.Client") || fileName == "Backroads.EngineSim.dll" || fileName == "Backroads.Testing.dll";

        public static List<string> Check(string buildDir)
        {
            var problems = new List<string>();
            if (!Directory.Exists(buildDir)) { problems.Add("no build folder " + buildDir); return problems; }
            var data = Directory.GetDirectories(buildDir, "*_Data").FirstOrDefault(d => Directory.Exists(Path.Combine(d, "Managed")));
            if (data == null) { problems.Add("no *_Data/Managed folder in " + buildDir); return problems; }
            var managed = Path.Combine(data, "Managed");
            var dlls = Directory.GetFiles(managed, "*.dll").Select(Path.GetFileName).ToList();
            foreach (var r in Required) if (!dlls.Contains(r)) problems.Add("missing " + r);
            foreach (var d in dlls) if (IsForbiddenAssembly(d)) problems.Add("client or test assembly in server build: " + d);
            var plugins = Path.Combine(data, "Plugins");
            if (Directory.Exists(plugins))
                foreach (var f in Directory.GetFiles(plugins, "*", SearchOption.AllDirectories))
                    if (Path.GetFileName(f).Equals("EngineSimPlugin.dll", System.StringComparison.OrdinalIgnoreCase))
                        problems.Add("EngineSimPlugin.dll in server build: " + f);
            return problems;
        }

        /// <summary>The newest builds\M(n)-server folder, or null.</summary>
        public static string FindLatestServerBuild()
        {
            var env = System.Environment.GetEnvironmentVariable("BR_SERVER_BUILD");
            if (!string.IsNullOrEmpty(env)) return env;
            if (!Directory.Exists(RepoPaths.Builds)) return null;
            return Directory.GetDirectories(RepoPaths.Builds, "*-server")
                .Where(d => File.Exists(Path.Combine(d, BuildCli.ServerExeName)))
                .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, BuildCli.ServerExeName)))
                .FirstOrDefault();
        }
    }
}
