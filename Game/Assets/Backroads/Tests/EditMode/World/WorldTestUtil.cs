using System.Collections.Generic;
using System.IO;
using Backroads.Canon.Editor;
using Backroads.World;
using NUnit.Framework;

namespace Backroads.Tests.EditMode.World
{
    /// <summary>Shared set-up for the lane W tests: the imported canon (imported once if missing) and the JSON references.</summary>
    static class WorldTestUtil
    {
        static CanonAsset s_Asset;
        static readonly Dictionary<string, Dictionary<string, object>> s_Json = new Dictionary<string, Dictionary<string, object>>();

        public static CanonAsset Canon()
        {
            if (s_Asset != null) return s_Asset;
            if (!CanonImporter.EnsureImported(out var msg)) Assert.Fail(msg);
            TestContext.WriteLine(msg);
            s_Asset = CanonImporter.LoadImported();
            Assert.NotNull(s_Asset, "no CanonAsset after import");
            return s_Asset;
        }

        public static WorldGrid Grid()
        {
            Canon();
            return CanonRuntime.Grid;
        }

        public static Dictionary<string, object> Json(string rel)
        {
            if (s_Json.TryGetValue(rel, out var o)) return o;
            var full = rel.StartsWith("tools/") ? Path.Combine(CanonImporter.RepoRoot, rel) : Path.Combine(CanonImporter.RepoRoot, "CanonData", "v1", rel);
            o = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(full)), rel);
            s_Json[rel] = o;
            return o;
        }

        public static Dictionary<string, object> Expected() => Json("tools/oracle/expected.json");
    }
}
