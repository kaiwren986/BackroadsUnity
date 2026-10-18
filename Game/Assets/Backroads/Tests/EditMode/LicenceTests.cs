using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Backroads.Build.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode
{
    /// <summary>Backroads.Tests.EditMode.Licence.*  (PLAN 1.4, 1.9 [C1])</summary>
    [TestFixture]
    public class Licence
    {
        sealed class Entry
        {
            public string Name, Licence, Source, Files;
            public bool Shipped;
            public List<string> Packages = new List<string>();
            public List<Regex> Globs = new List<Regex>();
        }

        static Regex Glob(string pattern)
        {
            pattern = pattern.Trim().Replace('\\', '/').TrimEnd('/');
            bool wild = pattern.IndexOfAny(new[] { '*', '?' }) >= 0;
            var rx = Regex.Escape(pattern).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]").Replace("\u0001", ".*");
            return new Regex(wild ? "^" + rx + "$" : "^" + rx + "(/.*)?$", RegexOptions.IgnoreCase);
        }

        static List<Entry> Load(List<string> problems)
        {
            var path = RepoPaths.InRepo("third-party.json");
            var root = BrJson.Obj(BrJson.Parse(File.ReadAllText(path)), "third-party.json");
            var list = new List<Entry>();
            int i = 0;
            foreach (var o in BrJson.Arr(root["entries"], "entries"))
            {
                i++;
                if (!(o is Dictionary<string, object> e)) { problems.Add($"entry {i} is not an object"); continue; }
                var x = new Entry
                {
                    Name = BrJson.Str(e, "name"), Licence = BrJson.Str(e, "licence"), Source = BrJson.Str(e, "source"), Files = BrJson.Str(e, "files"),
                };
                if (string.IsNullOrWhiteSpace(x.Name)) problems.Add($"entry {i} has no name");
                if (!e.TryGetValue("shipped", out var s) || !(s is bool)) problems.Add($"{x.Name ?? "entry " + i}: 'shipped' must be true or false");
                else x.Shipped = (bool)s;
                if (x.Shipped && string.IsNullOrWhiteSpace(x.Licence)) problems.Add($"{x.Name}: shipped without a licence");
                if (x.Shipped && string.IsNullOrWhiteSpace(x.Source)) problems.Add($"{x.Name}: shipped without a source");
                if (e.TryGetValue("packages", out var p) && p is List<object> pl) x.Packages.AddRange(pl.OfType<string>());
                if (!string.IsNullOrWhiteSpace(x.Files)) x.Globs.AddRange(x.Files.Split(',').Where(g => g.Trim().Length > 0).Select(Glob));
                list.Add(x);
            }
            return list;
        }

        /// <summary>
        /// third-party.json is well-formed; every shipped entry has a licence and a source; every package in
        /// packages-lock.json and every file under Plugins/, StreamingAssets/EngineSim/, Fonts/ and Audio/ has an entry.
        /// Packages named com.unity.* are covered by the Unity Companion License entry unless an entry names them.
        /// </summary>
        [Test]
        public void NoticesComplete()
        {
            var problems = new List<string>();
            var entries = Load(problems);

            var lockFile = Path.Combine(RepoPaths.Game, "Packages", "packages-lock.json");
            var deps = BrJson.Obj(BrJson.Obj(BrJson.Parse(File.ReadAllText(lockFile)))["dependencies"]);
            var unityUmbrella = entries.FirstOrDefault(e => (e.Licence ?? "").IndexOf("Unity Companion License", StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var id in deps.Keys)
            {
                if (id.StartsWith("com.unity.modules.", StringComparison.Ordinal)) continue; // engine modules, part of the Unity runtime
                bool named = entries.Any(e => e.Packages.Contains(id) || (e.Name ?? "").IndexOf(id, StringComparison.OrdinalIgnoreCase) >= 0);
                if (named) continue;
                if (id.StartsWith("com.unity.", StringComparison.Ordinal) && unityUmbrella != null) continue;
                problems.Add($"package {id} has no third-party.json entry");
            }

            var assets = Path.Combine(RepoPaths.Game, "Assets");
            var roots = new List<string> { Path.Combine(assets, "Plugins"), Path.Combine(assets, "StreamingAssets", "EngineSim") };
            if (Directory.Exists(assets))
                roots.AddRange(Directory.GetDirectories(assets, "*", SearchOption.AllDirectories)
                    .Where(d => (Path.GetFileName(d) == "Fonts" || Path.GetFileName(d) == "Audio") && !d.Replace('\\', '/').Contains("/ThirdParty/Synty")));
            int files = 0;
            foreach (var r in roots.Distinct().Where(Directory.Exists))
                foreach (var f in Directory.GetFiles(r, "*", SearchOption.AllDirectories))
                {
                    if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    files++;
                    var rel = RepoPaths.Relative(f);
                    if (!entries.Any(e => e.Globs.Any(g => g.IsMatch(rel)))) problems.Add($"{rel} has no third-party.json entry");
                }
            TestContext.WriteLine($"{entries.Count} entries, {deps.Count} locked packages, {files} files under Plugins/EngineSim/Fonts/Audio");

            // The notices text BuildCli writes must list every shipped entry.
            var notices = BuildCli.NoticesText("test");
            foreach (var e in entries.Where(e => e.Shipped && e.Name != null))
                if (!notices.Contains(e.Name)) problems.Add($"{e.Name} missing from THIRD_PARTY_NOTICES.txt");

            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        /// <summary>
        /// ODbL attribution [C8]: third-party.json ships the OpenStreetMap entry, and the in-game Credits screen always
        /// shows the OSM credit with the licence and the copyright link, whether or not the notices file is present.
        /// </summary>
        [Test]
        public void OsmAttribution()
        {
            var text = File.ReadAllText(RepoPaths.InRepo("third-party.json"));
            Assert.IsTrue(Regex.IsMatch(text, "\"name\":\\s*\"OpenStreetMap[^\"]*\"[^}]*\"licence\":\\s*\"ODbL 1\\.0\"[^}]*\"shipped\":\\s*true"),
                "third-party.json has no shipped OpenStreetMap entry under ODbL 1.0");
            var credit = Backroads.Client.UI.CreditsPanel.OsmCredit;
            StringAssert.Contains("OpenStreetMap contributors", credit);
            StringAssert.Contains("Open Database License", credit);
            StringAssert.Contains("https://www.openstreetmap.org/copyright", credit);
        }
    }
}
