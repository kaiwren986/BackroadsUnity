using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Backroads.Build.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode
{
    /// <summary>Backroads.Tests.EditMode.Repo.*</summary>
    [TestFixture]
    public class Repo
    {
        /// <summary>Hashed trees (AGENTS.md rule 7, PLAN 1.2 [T5]).</summary>
        public static readonly string[] HashedTrees =
        {
            "CanonData", "native/EngineSimPlugin/vendor", "native/EngineSimPlugin/generated", "Game/Assets/StreamingAssets", "tools/oracle/traces",
        };

        /// <summary>Binary by extension: the LFS patterns of .gitattributes plus other formats git treats as binary.</summary>
        static readonly HashSet<string> BinaryExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".f32", ".u8", ".bytes", ".blend", ".fbx", ".glb", ".png", ".jpg", ".jpeg", ".exr", ".wav", ".ogg", ".dll", ".roadbake",
            ".bin", ".gz", ".zip", ".pdb", ".exe", ".ttf", ".otf", ".ico", ".dds", ".tga", ".mp3", ".flac",
        };

        /// <summary>git's own rule: a NUL byte in the first 8000 bytes means binary.</summary>
        static bool LooksBinary(byte[] b)
        {
            int n = Math.Min(b.Length, 8000);
            for (int i = 0; i < n; i++) if (b[i] == 0) return true;
            return false;
        }

        /// <summary>Text files under a hashed tree never contain a CR byte. Binary files (16 upstream PNG/WAV under vendor\) are skipped.</summary>
        [Test]
        public void NoCrInHashedFiles()
        {
            var bad = new List<string>();
            int text = 0, binary = 0;
            foreach (var tree in HashedTrees)
            {
                var dir = RepoPaths.InRepo(tree);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    if (BinaryExt.Contains(Path.GetExtension(f))) { binary++; continue; }
                    var bytes = File.ReadAllBytes(f);
                    if (LooksBinary(bytes)) { binary++; continue; }
                    text++;
                    if (Array.IndexOf(bytes, (byte)'\r') >= 0) bad.Add(RepoPaths.Relative(f));
                }
            }
            TestContext.WriteLine($"{text} text files checked, {binary} binary files skipped");
            Assert.IsEmpty(bad, "CR bytes in hashed trees:\n" + string.Join("\n", bad.Take(50)));
        }

        // --- OWNERS.json ---------------------------------------------------------------------------------------
        public sealed class Owners
        {
            public Dictionary<string, string> Lanes = new Dictionary<string, string>();
            public Dictionary<string, string> Paths = new Dictionary<string, string>();

            public static Owners Load(string file)
            {
                var j = BrJson.Obj(BrJson.Parse(File.ReadAllText(file)), "OWNERS.json");
                var o = new Owners();
                foreach (var kv in BrJson.Obj(j["lanes"], "lanes")) o.Lanes[kv.Key] = kv.Value as string;
                foreach (var kv in BrJson.Obj(j["paths"], "paths")) o.Paths[kv.Key] = kv.Value as string;
                return o;
            }

            /// <summary>Longest prefix wins. "X.meta" also matches the owner of folder "X/".</summary>
            public string OwnerOf(string path)
            {
                string best = null; int bestLen = -1;
                void Try(string p)
                {
                    foreach (var kv in Paths)
                        if (p.StartsWith(kv.Key, StringComparison.Ordinal) && kv.Key.Length > bestLen) { best = kv.Value; bestLen = kv.Key.Length; }
                }
                Try(path);
                if (path.EndsWith(".meta", StringComparison.Ordinal)) Try(path.Substring(0, path.Length - 5) + "/");
                return best;
            }
        }

        [Test]
        public void OwnersJsonValid()
        {
            var o = Owners.Load(RepoPaths.InRepo("OWNERS.json"));
            Assert.IsTrue(o.Paths.ContainsKey(""), "OWNERS.json needs a root entry \"\"");
            var unknown = o.Paths.Where(kv => kv.Value == null || !o.Lanes.ContainsKey(kv.Value)).Select(kv => $"{kv.Key} -> {kv.Value}").ToList();
            Assert.IsEmpty(unknown, "paths owned by unknown lanes:\n" + string.Join("\n", unknown));
        }

        /// <summary>
        /// A branch changes only paths its lane owns (PLAN 1.10 [S11]). Inputs (skipped with Ignore when neither is set):
        /// BR_OWNERS_DIFF = a file with one changed path per line (e.g. git diff --name-only main...HEAD), or
        /// BR_OWNERS_BASE = a git ref to diff against (e.g. main). The lane comes from BR_OWNERS_LANE (one or more,
        /// comma separated, e.g. "I" or "I,A") or else from the branch name m(n)/(lane)-(topic).
        /// </summary>
        [Test]
        public void OwnersRespected()
        {
            var diffFile = Environment.GetEnvironmentVariable("BR_OWNERS_DIFF");
            var baseRef = Environment.GetEnvironmentVariable("BR_OWNERS_BASE");
            List<string> changed;
            if (!string.IsNullOrEmpty(diffFile))
            {
                Assert.IsTrue(File.Exists(diffFile), "BR_OWNERS_DIFF file not found: " + diffFile);
                changed = File.ReadAllLines(diffFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            }
            else if (!string.IsNullOrEmpty(baseRef))
            {
                var d = RepoPaths.Git($"diff --name-only {baseRef}...HEAD");
                Assert.IsNotNull(d, $"git diff --name-only {baseRef}...HEAD failed");
                changed = d.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            }
            else
            {
                Assert.Ignore("set BR_OWNERS_BASE=main (or BR_OWNERS_DIFF=<file of changed paths>) to check lane ownership");
                return;
            }

            var lanesVar = Environment.GetEnvironmentVariable("BR_OWNERS_LANE");
            var lanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(lanesVar)) foreach (var l in lanesVar.Split(',')) lanes.Add(l.Trim());
            else
            {
                var branch = RepoPaths.Git("rev-parse --abbrev-ref HEAD") ?? "";
                var m = Regex.Match(branch, @"^m\d+/([A-Za-z])[A-Za-z]*-");
                Assert.IsTrue(m.Success, $"cannot read the lane from branch '{branch}'; set BR_OWNERS_LANE");
                lanes.Add(m.Groups[1].Value.ToUpperInvariant());
            }

            var owners = Owners.Load(RepoPaths.InRepo("OWNERS.json"));
            var bad = changed.Select(p => (p, owner: owners.OwnerOf(p))).Where(x => x.owner == null || !lanes.Contains(x.owner))
                .Select(x => $"{x.p} (owner {x.owner ?? "none"})").ToList();
            TestContext.WriteLine($"{changed.Count} changed paths checked for lane(s) {string.Join(",", lanes)}");
            Assert.IsEmpty(bad, $"lane(s) {string.Join(",", lanes)} changed paths they do not own:\n" + string.Join("\n", bad.Take(100)));
        }
    }
}
