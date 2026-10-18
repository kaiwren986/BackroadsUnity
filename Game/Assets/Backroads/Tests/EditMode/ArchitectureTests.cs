using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Backroads.Build.Editor;
using NUnit.Framework;

namespace Backroads.Tests.EditMode
{
    /// <summary>
    /// Backroads.Tests.EditMode.Architecture.* : PLAN 1.5 rules 1-7, by reflection over the compiled assemblies plus a
    /// scan of the asmdef files and the C# sources under Assets/Backroads. Rule 8 is Build.ServerContents.
    /// </summary>
    [TestFixture]
    public class Architecture
    {
        const string Netcode = "Unity.Netcode.Runtime";

        static IEnumerable<System.Reflection.Assembly> Compiled() =>
            AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name.StartsWith("Backroads.", StringComparison.Ordinal));

        static IEnumerable<string> RefsOf(System.Reflection.Assembly a) => a.GetReferencedAssemblies().Select(n => n.Name);

        static string Fmt(IEnumerable<string> l) => string.Join("\n", l);

        [Test]
        public void AllPlannedAssembliesExist()
        {
            var expected = TestUtil.Shared.Concat(TestUtil.Client).Concat(new[]
            {
                "Backroads.Testing", "Backroads.Canon.Editor", "Backroads.Roads.Editor", "Backroads.Content.Editor",
                "Backroads.Art.Editor", "Backroads.Build.Editor", "Backroads.Tests.EditMode", "Backroads.Tests.PlayMode", "Backroads.Tests.Perf",
            });
            var missing = expected.Where(n => TestUtil.Get(n) == null).ToList();
            Assert.IsEmpty(missing, "asmdefs missing: " + string.Join(", ", missing));
        }

        [Test]
        public void DefineConstraintsAndUnsafeFlags()
        {
            var bad = new List<string>();
            foreach (var n in TestUtil.Client)
                if (!(TestUtil.Get(n)?.DefineConstraints.Contains("!UNITY_SERVER") ?? false)) bad.Add(n + " lacks !UNITY_SERVER");
            if (!(TestUtil.Get("Backroads.Server")?.DefineConstraints.Contains("UNITY_SERVER") ?? false)) bad.Add("Backroads.Server lacks UNITY_SERVER");
            if (!(TestUtil.Get("Backroads.Testing")?.DefineConstraints.Any(d => d.Contains("BACKROADS_TESTING")) ?? false)) bad.Add("Backroads.Testing lacks BACKROADS_TESTING");
            if (!(TestUtil.Get("Backroads.Net.Steam")?.DefineConstraints.Contains("BACKROADS_STEAM") ?? false)) bad.Add("Backroads.Net.Steam lacks BACKROADS_STEAM");
            foreach (var n in new[] { "Backroads.EngineSim", "Backroads.Vehicles" })
                if (!(TestUtil.Get(n)?.AllowUnsafe ?? false)) bad.Add(n + " lacks allowUnsafeCode");
            foreach (var a in TestUtil.Asmdefs().Where(a => a.Name.EndsWith(".Editor") || a.Name.StartsWith("Backroads.Tests.EditMode")))
                if (!a.IncludePlatforms.SequenceEqual(new[] { "Editor" })) bad.Add(a.Name + " is not editor-only");
            Assert.IsEmpty(bad, Fmt(bad));
        }

        /// <summary>Rule 1: only Backroads.Net and Backroads.Net.Steam reference Unity.Netcode.Runtime.</summary>
        [Test]
        public void OnlyNetReferencesNetcode()
        {
            var allowed = new[] { "Backroads.Net", "Backroads.Net.Steam" };
            var bad = TestUtil.Asmdefs().Where(a => !allowed.Contains(a.Name) && a.References.Any(r => r.StartsWith("Unity.Netcode")))
                .Select(a => a.Name + " (asmdef)").ToList();
            bad.AddRange(Compiled().Where(a => !allowed.Contains(a.GetName().Name) && RefsOf(a).Any(r => r.StartsWith("Unity.Netcode")))
                .Select(a => a.GetName().Name + " (compiled)"));
            Assert.IsEmpty(bad, "reference " + Netcode + ":\n" + Fmt(bad));
        }

        /// <summary>Rule 2: no shared assembly references Backroads.EngineSim or any Backroads.Client*.</summary>
        [Test]
        public void SharedNeverReferencesClient()
        {
            var bad = new List<string>();
            foreach (var n in TestUtil.Shared)
            {
                var a = TestUtil.Get(n);
                if (a == null) continue;
                bad.AddRange(a.References.Where(TestUtil.IsClientName).Select(r => $"{n} -> {r} (asmdef)"));
            }
            foreach (var a in Compiled().Where(a => TestUtil.Shared.Contains(a.GetName().Name)))
                bad.AddRange(RefsOf(a).Where(TestUtil.IsClientName).Select(r => $"{a.GetName().Name} -> {r} (compiled)"));
            Assert.IsEmpty(bad, Fmt(bad));
        }

        /// <summary>Rule 3: Backroads.Vehicles sees engines only through IVehicleEngine; BRV runs without the DLL.</summary>
        [Test]
        public void VehiclesSeesEnginesOnlyThroughInterface()
        {
            var bad = new List<string>();
            var v = TestUtil.Get("Backroads.Vehicles");
            Assert.NotNull(v, "Backroads.Vehicles asmdef missing");
            bad.AddRange(v.References.Where(r => r == "Backroads.EngineSim" || TestUtil.IsClientName(r)).Select(r => "asmdef references " + r));
            foreach (var a in Compiled().Where(a => a.GetName().Name == "Backroads.Vehicles"))
                bad.AddRange(RefsOf(a).Where(TestUtil.IsClientName).Select(r => "compiled reference " + r));
            bad.AddRange(TestUtil.Find("EngineSimPlugin", p => p.Contains("/Vehicles/")).Select(h => "native call: " + h));
            bad.AddRange(TestUtil.Find("DllImport", p => p.Contains("/Vehicles/")).Select(h => "native call: " + h));
            Assert.IsEmpty(bad, Fmt(bad));
        }

        /// <summary>Rule 4: no material property blocks anywhere under Assets/Backroads (GPU Resident Drawer, 04 section 2.2).</summary>
        [Test]
        public void NoPropertyBlocks()
        {
            var hits = TestUtil.Find("Material" + "PropertyBlock");
            Assert.IsEmpty(hits, Fmt(hits));
        }

        /// <summary>Rule 5: no stored GetInstanceID() (EntityId from 6.4). Any call is flagged.</summary>
        [Test]
        public void NoStoredInstanceId()
        {
            var hits = TestUtil.Find("GetInstance" + "ID(");
            Assert.IsEmpty(hits, Fmt(hits));
        }

        /// <summary>Rule 6: shared state changes only through authority interfaces; Wallet.money is written only inside *Authority classes.</summary>
        [Test]
        public void SharedStateThroughAuthorities()
        {
            var write = new Regex(@"\.\s*money\s*(\+\+|--|[-+*/]?=(?!=))");
            var bad = new List<string>();
            foreach (var (path, code) in TestUtil.Sources(p => !p.Contains("/Tests/")))
            {
                if (Path.GetFileNameWithoutExtension(path).EndsWith("Authority", StringComparison.Ordinal)) continue;
                var lines = code.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    if (write.IsMatch(lines[i])) bad.Add($"{path}:{i + 1}: {lines[i].Trim()}");
            }
            Assert.IsEmpty(bad, "money written outside an IEconomyAuthority implementation:\n" + Fmt(bad));
        }

        /// <summary>Rule 7: the player scripts compile with UNITY_SERVER (BuildCli.CompileServerScripts).</summary>
        [Test, Category("ServerCompile")]
        public void ServerScriptsCompile()
        {
            var dir = Path.Combine(RepoPaths.Artifacts, "server-compile-test");
            var r = BuildCli.CompileServerScriptsTo(dir);
            Assert.IsTrue(r.success, Fmt(r.problems) + "\nassemblies: " + string.Join(", ", r.assemblies));
            StringAssert.Contains("Backroads.Server.dll", string.Join(",", r.assemblies));
        }
    }
}
