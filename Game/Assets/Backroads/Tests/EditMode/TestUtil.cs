using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Backroads.Build.Editor;

namespace Backroads.Tests.EditMode
{
    /// <summary>Source and asmdef helpers shared by the Architecture, Privacy and Repo tests.</summary>
    static class TestUtil
    {
        public static string BackroadsRoot => Path.Combine(RepoPaths.Game, "Assets", "Backroads");

        public sealed class Asmdef
        {
            public string Name;
            public string Path;
            public string Folder;
            public List<string> References = new List<string>();
            public List<string> DefineConstraints = new List<string>();
            public List<string> IncludePlatforms = new List<string>();
            public bool AllowUnsafe;
        }

        static List<Asmdef> s_Asmdefs;

        public static List<Asmdef> Asmdefs()
        {
            if (s_Asmdefs != null) return s_Asmdefs;
            var list = new List<Asmdef>();
            foreach (var f in Directory.GetFiles(BackroadsRoot, "*.asmdef", SearchOption.AllDirectories))
            {
                var j = BrJson.Obj(BrJson.Parse(File.ReadAllText(f)), f);
                var a = new Asmdef { Name = BrJson.Str(j, "name"), Path = f, Folder = System.IO.Path.GetDirectoryName(f) };
                a.References = Strings(j, "references").Select(ResolveRef).ToList();
                a.DefineConstraints = Strings(j, "defineConstraints").ToList();
                a.IncludePlatforms = Strings(j, "includePlatforms").ToList();
                a.AllowUnsafe = j.TryGetValue("allowUnsafeCode", out var u) && u is bool b && b;
                list.Add(a);
            }
            return s_Asmdefs = list;
        }

        static IEnumerable<string> Strings(Dictionary<string, object> j, string key) =>
            j.TryGetValue(key, out var v) && v is List<object> l ? l.OfType<string>() : Enumerable.Empty<string>();

        /// <summary>"GUID:..." references resolve to the referenced asmdef's name.</summary>
        static string ResolveRef(string r)
        {
            if (!r.StartsWith("GUID:", StringComparison.Ordinal)) return r;
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(r.Substring(5));
            if (string.IsNullOrEmpty(path)) return r;
            var full = System.IO.Path.Combine(RepoPaths.Game, path);
            return File.Exists(full) ? BrJson.Str(BrJson.Obj(BrJson.Parse(File.ReadAllText(full))), "name") ?? r : r;
        }

        public static Asmdef Get(string name) => Asmdefs().FirstOrDefault(a => a.Name == name);

        public static readonly string[] Shared =
        {
            "Backroads.Core", "Backroads.World", "Backroads.Deform", "Backroads.Roads", "Backroads.Vehicles", "Backroads.Traffic",
            "Backroads.Gameplay", "Backroads.Persistence", "Backroads.Net", "Backroads.Net.Steam", "Backroads.Game", "Backroads.Server",
        };

        public static readonly string[] Client = { "Backroads.EngineSim", "Backroads.Client.Rendering", "Backroads.Client", "Backroads.Client.UI" };

        /// <summary>
        /// Client assemblies: the planned ones, plus any lane presentation assembly named "<Area>.Client" (for example
        /// Backroads.Roads.Client, Backroads.Vehicles.Client). Each must carry "!UNITY_SERVER"; no shared assembly may reference one.
        /// </summary>
        public static bool IsClientName(string n) =>
            n == "Backroads.EngineSim" || n.StartsWith("Backroads.Client", StringComparison.Ordinal) ||
            (n.StartsWith("Backroads.", StringComparison.Ordinal) && n.EndsWith(".Client", StringComparison.Ordinal));

        /// <summary>
        /// The planned client assemblies and every player asmdef whose name marks it as client. Editor-only assemblies
        /// (Backroads.Client.Editor, Backroads.Roads.Client.Editor) never reach a player and need no "!UNITY_SERVER".
        /// </summary>
        public static IEnumerable<string> AllClient => Client.Concat(Asmdefs()
            .Where(a => IsClientName(a.Name) && !a.IncludePlatforms.SequenceEqual(new[] { "Editor" }))
            .Select(a => a.Name)).Distinct();

        /// <summary>Every .cs file under Assets/Backroads with its text, comments removed (// and /* */), strings kept.</summary>
        public static IEnumerable<(string path, string code)> Sources(Func<string, bool> include = null)
        {
            foreach (var f in Directory.GetFiles(BackroadsRoot, "*.cs", SearchOption.AllDirectories))
            {
                var rel = RepoPaths.Relative(f);
                if (include != null && !include(rel)) continue;
                yield return (rel, StripComments(File.ReadAllText(f)));
            }
        }

        static readonly Regex Comments = new Regex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
        public static string StripComments(string code) => Comments.Replace(code, "");

        public static List<string> Find(string token, Func<string, bool> include = null)
        {
            var hits = new List<string>();
            foreach (var (path, code) in Sources(include))
            {
                var lines = code.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(token)) hits.Add($"{path}:{i + 1}: {lines[i].Trim()}");
            }
            return hits;
        }
    }
}
