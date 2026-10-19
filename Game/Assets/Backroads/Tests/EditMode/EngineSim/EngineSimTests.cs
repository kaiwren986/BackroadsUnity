using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Backroads.EngineSim;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace Backroads.Tests.EditMode.EngineSim
{
    /// <summary>Backroads.Tests.EditMode.EngineSim.* (lane V, M1): the C# side of ABI v1 and the engine catalogue.</summary>
    [TestFixture]
    public class EngineSim
    {
        static readonly string Plugins = Path.Combine(Application.dataPath, "Plugins", "x86_64");

        static void RequirePlugin()
        {
            if (!Native.Available(out var why)) Assert.Fail("EngineSimPlugin: " + why + " (run tools\\ci\\build-native.ps1)");
        }

        /// <summary>PLAN 1.10 [T13]: every C# mirror has the size of its packed C struct (ESP_SIZEOF_* in EngineSimPlugin.h).</summary>
        [Test]
        public void StructSizes()
        {
            var bad = new List<string>();
            void Check<T>(int size) where T : struct
            {
                int m = Marshal.SizeOf<T>(), u = UnsafeUtility.SizeOf<T>();
                if (m != size || u != size) bad.Add($"{typeof(T).Name}: Marshal {m}, unsafe {u}, header {size}");
            }
            Check<EspCreateDesc>(EspCreateDesc.Size);
            Check<EspEngineInfo>(EspEngineInfo.Size);
            Check<EspControls>(EspControls.Size);
            Check<EspDrivelineIn>(EspDrivelineIn.Size);
            Check<EspStepOut>(EspStepOut.Size);
            Check<EspAudioParams>(EspAudioParams.Size);
            Check<EspStats>(EspStats.Size);
            Check<EspWorkerOptions>(EspWorkerOptions.Size);
            Assert.IsEmpty(bad, string.Join("\n", bad));
        }

        /// <summary>The header's sizes, as the plugin reports them: a struct with the wrong structSize is refused.</summary>
        [Test]
        public void PluginRefusesWrongStructSize()
        {
            RequirePlugin();
            EngineSimLog.Drain();
            var desc = new EspCreateDesc { structSize = EspCreateDesc.Size - 4 };
            Assert.AreEqual(IntPtr.Zero, CreateRaw(desc), "esp_create accepted a short EspCreateDesc");
            StringAssert.Contains("struct_size", EngineSimLog.Drain(), "the refusal is logged");  // drained here, not as a Debug.LogError later
        }

        static unsafe IntPtr CreateRaw(EspCreateDesc desc)
        {
            var es = Native.Utf8(EngineCatalog.StreamingEsDir);
            fixed (byte* p = es) return Native.esp_create(desc, p, null);
        }

        /// <summary>The installed DLL speaks ABI v1 and is the one EngineSimPlugin.version.json describes (build-native.ps1).</summary>
        [Test]
        public void NativeSelftest()
        {
            RequirePlugin();
            Assert.AreEqual(Native.AbiVersion, Native.esp_version());
            var info = Native.BuildInfo();
            Assert.IsNotNull(info, "esp_build_info failed");
            StringAssert.Contains("\"abi\":1", info);
            StringAssert.Contains("\"crt\":\"static\"", info);
            StringAssert.Contains("\"asan\":false", info, "an ASan build is installed in Plugins (run build-native.ps1 without -Asan)");
            var versionFile = Path.Combine(Plugins, "EngineSimPlugin.version.json");
            Assert.IsTrue(File.Exists(versionFile), versionFile + " missing");
            var m = Regex.Match(File.ReadAllText(versionFile), "\"sourceHash\"\\s*:\\s*\"([0-9a-f]+)\"");
            Assert.IsTrue(m.Success, "no sourceHash in " + versionFile);
            StringAssert.Contains("\"sourceHash\":\"" + m.Groups[1].Value + "\"", info, "the DLL is not the build version.json describes");
            TestContext.WriteLine(info);
        }

        /// <summary>The impulse responses the builtins load are in StreamingAssets (shipped with every client build).</summary>
        [Test]
        public void ImpulseResponsesShipped()
        {
            foreach (var f in new[] { "sound-library/new/minimal_muffling_02.wav", "sound-library/smooth/smooth_39.wav" })
                Assert.IsTrue(File.Exists(Path.Combine(EngineCatalog.StreamingEsDir, f)), f + " missing from StreamingAssets/EngineSim/es");
        }

        /// <summary>Every roster engine loads, cranks on the starter and reports the cylinders its EngineDef names.</summary>
        [Test]
        public void BuiltinsCrank()
        {
            RequirePlugin();
            var notes = new List<string>();
            foreach (var def in EngineCatalog.Roster)
            {
                var e = EngineSimEngine.Create(def, new EngineSimEngine.Options { workerThread = false }, out var err);
                Assert.IsNotNull(e, def.id + ": " + err);
                try
                {
                    Assert.AreEqual(def.cylinders, e.Info.cylinders, def.id + " cylinders");
                    Assert.Greater(e.Info.simFrequency, 0, def.id + " sim frequency");
                    double peak = Crank(e, 1.5);
                    notes.Add($"{def.id}: '{e.Info.Name}', {e.Info.cylinders} cyl, {e.Info.displacementM3 * 1000:F2} L, peak {peak:F0} rpm in 1.5 s");
                    Assert.Greater(peak, 150, def.id + " does not turn on the starter");
                }
                finally { e.Dispose(); }
            }
            TestContext.WriteLine(string.Join("\n", notes));
        }

        static unsafe double Crank(EngineSimEngine e, double seconds)
        {
            var c = new EspControls { structSize = EspControls.Size, ignition = 1, starter = 1, simSpeed = 1, frameDtS = 1f / 60 };
            Native.esp_set_controls(e.Handle, c);
            var d = EspDrivelineIn.Of(EspMode.Free);
            var o = new EspStepOut { structSize = EspStepOut.Size };
            double peak = 0, dt = 1.0 / 120;
            for (long k = 0; k < (long)(seconds / dt); k++)
            {
                Assert.AreEqual(EspStatus.Ok, Native.esp_step(e.Handle, k, dt, d, null, 0, ref o), "esp_step");
                peak = Math.Max(peak, o.rpm);
            }
            return peak;
        }

        /// <summary>
        /// FixedStep on the native worker: every step is queued in order, results come back by step index and the
        /// collect never waits longer than its bound.
        /// </summary>
        [Test]
        public void WorkerCollectsInOrder()
        {
            RequirePlugin();
            var def = EngineCatalog.Find("i4_synth");
            var e = EngineSimEngine.Create(def, new EngineSimEngine.Options { workerThread = true }, out var err);
            Assert.IsNotNull(e, err);
            try
            {
                e.Ignition = true;
                e.Starter = true;
                var d = EspDrivelineIn.Of(EspMode.Free);
                long lastIndex = -1;
                int got = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 240; i++)
                {
                    var o = e.FixedStep(1.0 / 120, d);
                    if (e.HaveResult)
                    {
                        Assert.GreaterOrEqual(o.stepIndex, lastIndex, "results went backwards");
                        if (o.stepIndex != lastIndex) got++;
                        lastIndex = o.stepIndex;
                    }
                    if (i % 2 == 1) Thread.Sleep(16);  // two fixed steps per 60 fps frame
                }
                Assert.AreEqual(240, e.StepsKicked, "every step queued");
                Assert.Greater(got, 200, "results collected");
                var s = e.Stats();
                Assert.GreaterOrEqual(s.stepsRun, 200);
                Assert.Less(e.CostPercentileMs(99), 2.0f, "main-thread cost per FixedUpdate (p99)");
                TestContext.WriteLine($"{got} results, last index {lastIndex}, stale {s.staleSteps}, cost p50 {e.CostPercentileMs(50):F3} ms p99 {e.CostPercentileMs(99):F3} ms, {sw.ElapsedMilliseconds} ms");
            }
            finally { e.Dispose(); }
        }

        /// <summary>PLAN 3.4 [T1]: L = clamp(S, 2, 4) from the fixed steps Unity runs this frame.</summary>
        [Test]
        public void LagFromSteps()
        {
            Assert.AreEqual(new[] { 2, 2, 2, 3, 4, 4 }, Enumerable.Range(0, 6).Select(FixedStepFrame.LagFor).ToArray());
            double f = 1.0 / 120;
            Assert.AreEqual(0, FixedStepFrame.StepsFor(10.0, 10.0, f));
            Assert.AreEqual(2, FixedStepFrame.StepsFor(10.0 + 2 * f, 10.0, f));
            Assert.AreEqual(1, FixedStepFrame.StepsFor(10.0 + 1.9 * f, 10.0, f));
            Assert.AreEqual(4, FixedStepFrame.StepsFor(10.5, 10.0, f), "at most 4 (maximumDeltaTime 4/120)");
        }

        /// <summary>PLAN 1.9 pitch rule on synthetic signals: a pulse train at the firing frequency passes, a detuned one fails.</summary>
        [Test]
        public void PitchRuleSynthetic()
        {
            const int rate = 48000;
            double rpm = 3000, firing = rpm / 120 * 4;    // 4 cylinders: 100 Hz
            var good = Pulses(rate, firing, 1.5);
            var r = PitchAnalysis.Check(good, 0, good.Length, 1, rate, rpm, EngineDef.FiringOrders(4));
            Assert.IsTrue(r.Pass, "firing-frequency pulses: " + r);
            var bad = Pulses(rate, firing * 1.1, 1.5);
            var r2 = PitchAnalysis.Check(bad, 0, bad.Length, 1, rate, rpm, EngineDef.FiringOrders(4));
            Assert.IsFalse(r2.Pass, "pulses 10 % sharp: " + r2);
            Assert.Less(PitchAnalysis.ClipFraction(good, good.Length), 1e-9);
        }

        static float[] Pulses(int rate, double hz, double seconds)
        {
            var x = new float[(int)(rate * seconds)];
            double period = rate / hz;
            for (int i = 0; i < x.Length; i++)
            {
                double ph = i % period / period;
                x[i] = (float)(0.4 * Math.Exp(-ph * 12) * Math.Sin(2 * Math.PI * 600 * i / rate) + 0.2 * Math.Exp(-ph * 6));
            }
            return x;
        }

        // Real makes and models of the upstream catalogue (PLAN 3.10, Content.NoRealMarks): never in what a Client build shows.
        static readonly string[] RealMarks =
        {
            "subaru", "honda", "kohler", "harley", "davidson", "hayabusa", "suzuki", "audi", "ferrari", "lexus", "lfa", "merlin",
            "rolls", "royce", "chevrolet", "chevy", "toyota", "2jz", "bmw", "m52", "ej25", "ej20", "vtec", "trx", "shovelhead",
            "gm ", "ls1", "ls3", "f136", "lamborghini", "porsche", "ford", "dodge", "nissan", "mazda", "mitsubishi", "yamaha",
        };

        static IEnumerable<string> Marks(string s)
        {
            var t = (s ?? "").ToLowerInvariant();
            return RealMarks.Where(m => t.Contains(m));
        }

        /// <summary>Roster engines (what a Client build lists) carry fictional ids and names only, in C# and in the plugin.</summary>
        [Test]
        public void CatalogNoRealMarks()
        {
            var bad = new List<string>();
            foreach (var d in EngineCatalog.Roster)
            {
                Assert.IsTrue(d.roster && !d.devOnly && d.source == EngineSource.Builtin, d.id + " must be a builtin roster engine");
                foreach (var f in new[] { d.id, d.displayName, d.builtinName, d.notes })
                    foreach (var m in Marks(f)) bad.Add($"{d.id}: '{f}' contains '{m}'");
            }
            if (Native.Available(out _))
                foreach (var d in EngineCatalog.Roster)
                {
                    var e = EngineSimEngine.Create(d, new EngineSimEngine.Options { workerThread = false }, out var err);
                    Assert.IsNotNull(e, d.id + ": " + err);
                    try { foreach (var m in Marks(e.Info.Name)) bad.Add($"{d.id}: plugin name '{e.Info.Name}' contains '{m}'"); }
                    finally { e.Dispose(); }
                }
            Assert.IsEmpty(bad, string.Join("\n", bad));
        }

        /// <summary>The Dev-only upstream list: every script is marked devOnly and none is in the roster.</summary>
        [Test]
        public void UpstreamScriptsAreDevOnly()
        {
            var dev = EngineCatalog.DevScripts();
            if (dev.Count == 0) Assert.Ignore("the vendored engine-sim tree is not in this checkout");
            Assert.AreEqual(25, dev.Count, "upstream scripts found");
            Assert.IsTrue(dev.All(d => d.devOnly && !d.roster && d.id.StartsWith("upstream/", StringComparison.Ordinal)));
            Assert.IsFalse(EngineCatalog.Roster.Any(r => dev.Any(d => d.id == r.id)));
        }
    }
}
