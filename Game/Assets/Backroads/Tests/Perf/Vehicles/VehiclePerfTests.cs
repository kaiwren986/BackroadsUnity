using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Backroads.Vehicles;
using NUnit.Framework;
using UnityEngine;

namespace Backroads.Tests.Perf
{
    /// <summary>
    /// Perf.Vehicles.StepCost (card T3.5, 06 probe P1): BRV v2 on the test track, 1, 20 and 50 vehicles driving, the
    /// Burst step on one thread pinned to a P-core (and, for the record, to an E-core). Gate: at most 30 us per vehicle
    /// per fixed step on a P-core, and 50 vehicles at most 1.0 ms of wall time per step.
    /// Perf numbers count only on mains power with nothing else running (machine.lock, AGENTS.md); the test asserts the
    /// budget only when the runner says so (BR_PERF_GATE=1) and otherwise reports the numbers as provisional, failing only
    /// beyond three times the budget (a real regression, not a busy machine).
    /// </summary>
    [TestFixture, Category("Perf"), Category("Vehicle")]
    public class Vehicles
    {
        const double BudgetUs = 30, Budget50Ms = 1.0;
        static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        // ---- Windows: P-cores and E-cores (EfficiencyClass), thread affinity
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

        /// <summary>Affinity masks (group 0) of one core of the fastest and one of the slowest class; true on a hybrid CPU.</summary>
        static bool CoreMasks(out ulong pMask, out ulong eMask, out string what)
        {
            pMask = eMask = 0; what = "unknown";
            if (Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer) return false;
            uint len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len == 0) return false;
            var buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return false;
                var cores = new List<(byte cls, ulong mask)>();
                for (int off = 0; off < len;)
                {
                    var p = IntPtr.Add(buf, off);
                    int rel = Marshal.ReadInt32(p), size = Marshal.ReadInt32(p, 4);
                    if (rel == 0)
                    {
                        byte cls = Marshal.ReadByte(p, 9);
                        ulong mask = (ulong)Marshal.ReadInt64(p, 32);
                        ushort group = (ushort)Marshal.ReadInt16(p, 40);
                        if (group == 0) cores.Add((cls, mask));
                    }
                    if (size <= 0) break;
                    off += size;
                }
                if (cores.Count == 0) return false;
                byte hi = cores.Max(c => c.cls), lo = cores.Min(c => c.cls);
                // one core of each class (its first logical processor), so the step runs on a single physical core
                ulong First(ulong m) => m & (~m + 1);
                pMask = First(cores.First(c => c.cls == hi).mask);
                eMask = First(cores.Last(c => c.cls == lo).mask);
                what = hi == lo ? $"{cores.Count} cores, one class (not hybrid: no E-cores, every core counts as a P-core)" : $"{cores.Count(c => c.cls == hi)} P-cores (class {hi}), {cores.Count(c => c.cls == lo)} E-cores (class {lo})";
                return hi != lo;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        struct Result { public int n; public string core; public double usPerVehicle, msPerStepMedian, msPerStepP95; }

        static Result Measure(BrvWorldData world, int n, string coreName, ulong mask)
        {
            bool was = BrvFeatures.V2Driveline;
            BrvFeatures.V2Driveline = true;
            using var fleet = new BrvFleet(world, false, Math.Max(n, 1)) { NoDeformation = true };
            try
            {
                var ids = ClassicVehicleDefs.M2;
                for (int k = 0; k < n; k++)
                {
                    int i = fleet.Add(ClassicVehicleDefs.Get(ids[k % ids.Length]));
                    // a grid on the pad, 14 m apart, all heading +z
                    double x = -126 + 14 * (k % 10), z = -480 + 30 * (k / 10);
                    var w = world.View;
                    BrvSim.PlaceAligned(ref fleet.State(i), fleet.Def(i), w, x, z, 0);
                    ref var s = ref fleet.State(i);
                    s.engineOn = true; s.fuel = 1;
                    fleet.Settle(i, 120);
                }
            }
            finally { BrvFeatures.V2Driveline = was; }
            void Inputs(int step)
            {
                for (int k = 0; k < n; k++)
                {
                    double t = step / 120.0 + k * 0.37;
                    fleet.SetInput(k, new BrvInput { active = true, ignition = true, fwd = 0.6 + 0.3 * Math.Sin(t * 0.7), steer = 0.4 * Math.Sin(t * 0.9) });
                }
            }
            for (int s = 0; s < 240; s++) { Inputs(s); fleet.Step(); }   // warm up (Burst compiled, everyone moving)
            var old = mask != 0 ? SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask) : UIntPtr.Zero;
            const int Steps = 1200;
            var times = new double[Steps];
            try
            {
                var sw = new Stopwatch();
                for (int s = 0; s < Steps; s++)
                {
                    Inputs(240 + s);
                    sw.Restart();
                    fleet.Step();
                    times[s] = sw.Elapsed.TotalMilliseconds;
                }
            }
            finally { if (old != UIntPtr.Zero) SetThreadAffinityMask(GetCurrentThread(), old); }
            Array.Sort(times);
            return new Result
            {
                n = n, core = coreName,
                usPerVehicle = times.Average() * 1000 / n,
                msPerStepMedian = times[Steps / 2],
                msPerStepP95 = times[(int)(Steps * 0.95)],
            };
        }

        [Test]
        public void StepCost()
        {
            bool gate = Environment.GetEnvironmentVariable("BR_PERF_GATE") == "1";
            bool hybrid = CoreMasks(out ulong p, out ulong e, out string cpu);
            var world = BrvTestTrack.BuildWorld();
            var res = new List<Result>();
            try
            {
                foreach (int n in new[] { 1, 20, 50 })
                {
                    res.Add(Measure(world, n, "P-core", p));
                    if (hybrid && e != 0 && e != p) res.Add(Measure(world, n, "E-core", e));
                }
            }
            finally { var g = world.Grid; world.Dispose(); g.Dispose(); }
            var sb = new StringBuilder();
            sb.AppendLine("# Perf.Vehicles.StepCost (BRV v2)").AppendLine();
            sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm}, {SystemInfo.processorType} ({cpu}), Burst {(Unity.Burst.BurstCompiler.IsEnabled ? "on" : "OFF")}, " +
                          $"{(gate ? "perf gate run (BR_PERF_GATE=1: mains power, machine.lock)" : "PROVISIONAL (not run under machine.lock; other jobs may have been running)")}.").AppendLine();
            sb.AppendLine("1,200 fixed steps after a 2 s warm-up; every vehicle driving (part throttle, weaving) on the test-track pad; the whole fleet step (VehicleStepJob, Burst, one thread) timed.").AppendLine();
            sb.AppendLine("| Vehicles | Core | us per vehicle per step | ms per step (median) | ms per step (p95) |").AppendLine("|---:|---|---:|---:|---:|");
            foreach (var r in res) sb.AppendLine(string.Format(Ci, "| {0} | {1} | {2:F2} | {3:F3} | {4:F3} |", r.n, r.core, r.usPerVehicle, r.msPerStepMedian, r.msPerStepP95));
            var dir = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..")), "artifacts", "vehicles");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "perf-stepcost.md"), sb.ToString());
            TestContext.WriteLine(sb.ToString());
            var pc = res.Where(r => r.core == "P-core").ToList();
            double worstUs = pc.Max(r => r.usPerVehicle);
            double ms50 = pc.First(r => r.n == 50).msPerStepMedian;
            double k = gate ? 1 : 3;
            Assert.LessOrEqual(worstUs, BudgetUs * k, $"us per vehicle per step on a P-core (budget {BudgetUs}{(gate ? "" : ", x3 outside a perf gate run")})");
            Assert.LessOrEqual(ms50, Budget50Ms * k, $"50 vehicles, ms per step (budget {Budget50Ms}{(gate ? "" : ", x3 outside a perf gate run")})");
        }
    }
}
