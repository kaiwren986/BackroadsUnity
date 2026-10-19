using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Backroads.Tests.EditMode.EngineSim
{
    /// <summary>
    /// Backroads.Tests.EditMode.EngineSim.GeneratedSources.* (PLAN 6.1 M1 "Licence.GeneratedHeadersIntact", with piranha):
    /// the generated piranha parser and scanner match GENERATED.md byte for byte, keep the Bison exception, and the
    /// notices the build ships (vendor/LICENSES/bison-NOTICE.txt, flex-NOTICE.txt) are there.
    /// </summary>
    [TestFixture]
    public class GeneratedSources
    {
        static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
        static string Generated => Path.Combine(Repo, "native", "EngineSimPlugin", "generated", "piranha");
        static string Licenses => Path.Combine(Repo, "native", "EngineSimPlugin", "vendor", "LICENSES");

        static string Sha256(byte[] b)
        {
            using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(b).Select(x => x.ToString("x2")));
        }

        [Test]
        public void GeneratedHeadersIntact()
        {
            var md = Path.Combine(Generated, "GENERATED.md");
            if (!File.Exists(md)) Assert.Ignore("no generated/piranha in this checkout");
            var rows = Regex.Matches(File.ReadAllText(md), @"^\| `([^`]+)` \| ([0-9,]+) \| `([0-9a-f]{64})` \|", RegexOptions.Multiline);
            Assert.AreEqual(4, rows.Count, "GENERATED.md lists the four generated files");
            var bad = new List<string>();
            foreach (Match m in rows)
            {
                var path = Path.Combine(Generated, m.Groups[1].Value);
                if (!File.Exists(path)) { bad.Add(m.Groups[1].Value + " missing"); continue; }
                var bytes = File.ReadAllBytes(path);
                long want = long.Parse(m.Groups[2].Value.Replace(",", ""));
                if (bytes.LongLength != want) bad.Add($"{m.Groups[1].Value}: {bytes.LongLength} bytes, GENERATED.md says {want}");
                if (Sha256(bytes) != m.Groups[3].Value) bad.Add($"{m.Groups[1].Value}: SHA-256 differs from GENERATED.md");
                if (Array.IndexOf(bytes, (byte)'\r') >= 0) bad.Add($"{m.Groups[1].Value}: CR bytes");
            }
            foreach (var f in new[] { "parser.auto.cpp", "parser.auto.h" })
            {
                var text = File.ReadAllText(Path.Combine(Generated, f));
                if (!text.Contains("As a special exception, you may create a larger work that contains")) bad.Add(f + ": Bison special exception removed");
                if (!text.Contains("Free Software Foundation, Inc.")) bad.Add(f + ": Bison copyright removed");
            }
            var notice = Path.Combine(Licenses, "bison-NOTICE.txt");
            if (!File.Exists(notice)) bad.Add("vendor/LICENSES/bison-NOTICE.txt missing");
            else
            {
                var head = string.Join("\n", File.ReadAllText(Path.Combine(Generated, "parser.auto.cpp")).Split('\n').Take(31)) + "\n";
                if (File.ReadAllText(notice) != head) bad.Add("bison-NOTICE.txt is not lines 1-31 of parser.auto.cpp");
            }
            if (!File.Exists(Path.Combine(Licenses, "flex-NOTICE.txt"))) bad.Add("vendor/LICENSES/flex-NOTICE.txt missing");
            Assert.IsEmpty(bad, string.Join("\n", bad));
        }
    }
}
