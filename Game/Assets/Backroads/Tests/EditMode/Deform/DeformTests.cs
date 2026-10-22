using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Canon.Editor;
using Backroads.Client.Rendering;
using Backroads.Deform;
using Backroads.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Backroads.Tests.EditMode.Deform
{
    /// <summary>
    /// T2.5 gates (PLAN 6.1 M2 lane): Deform.MatchesJs, Deform.SubMmConverges, Deform.Deterministic, plus the partial
    /// terrain update and the tree re-snap. The JS reference is JsDeformReference, which each run first proves bit-exact
    /// against the browser game's per-call hashes in CanonData/v1/reference/deform/calls.json.
    /// </summary>
    [TestFixture, Category("Deform")]
    public unsafe class Deform
    {
        // ------------------------------------------------------------------ helpers

        // the canon test world's grid (CanonData v1): the reference calls were recorded on it
        static readonly WorldGridLayout V1 = WorldGridLayout.CanonV1;
        static readonly int N = V1.N, VN = V1.VN;

        static CanonAsset s_Canon;
        static List<Call> s_Calls;

        sealed class Call
        {
            public string Fn;
            public double[] A;
            public double Ret;
            public int N;
            public string Hash;
        }

        static CanonAsset Canon()
        {
            if (s_Canon != null) return s_Canon;
            if (!CanonImporter.EnsureImported(out var msg)) Assert.Fail(msg);
            s_Canon = CanonImporter.LoadImported();
            Assert.NotNull(s_Canon, "no CanonAsset after import");
            return s_Canon;
        }

        static WorldGrid FreshGrid() => WorldGrid.FromCanon(Canon());

        static JsDeformReference Mirror(WorldGrid g) =>
            new JsDeformReference(g.H0.ToArray(), g.Base.ToArray(), g.Surface.ToArray(), g.Wear.ToArray());

        /// <summary>
        /// calls.json rows, numbers parsed with exact (correctly rounded) decimal-to-double conversion: Mono's
        /// double.Parse is not always correctly rounded, and one ulp in an argument shows in JS's return values.
        /// </summary>
        static List<Call> Calls()
        {
            if (s_Calls != null) return s_Calls;
            var path = Path.Combine(CanonImporter.SourceDir, "reference", "deform", "calls.json");
            var text = File.ReadAllText(path);
            var list = new List<Call>();
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\[""(rut|brush)"",([^\]]*)\]"))
            {
                var parts = m.Groups[2].Value.Split(',');
                int nargs = parts.Length - 3;
                var a = new double[nargs];
                for (int i = 0; i < nargs; i++) a[i] = ExactDouble(parts[i]);
                list.Add(new Call { Fn = m.Groups[1].Value, A = a, Ret = ExactDouble(parts[nargs]), N = int.Parse(parts[nargs + 1], CultureInfo.InvariantCulture), Hash = parts[nargs + 2].Trim('"') });
            }
            s_Calls = list;
            return list;
        }

        /// <summary>A JSON number as the nearest double (round half to even), with BigInteger arithmetic.</summary>
        public static double ExactDouble(string s)
        {
            s = s.Trim();
            bool neg = s.StartsWith("-");
            if (neg || s.StartsWith("+")) s = s.Substring(1);
            int e10 = 0;
            int ei = s.IndexOfAny(new[] { 'e', 'E' });
            if (ei >= 0) { e10 = int.Parse(s.Substring(ei + 1), CultureInfo.InvariantCulture); s = s.Substring(0, ei); }
            int dot = s.IndexOf('.');
            if (dot >= 0) { e10 -= s.Length - dot - 1; s = s.Remove(dot, 1); }
            var m = System.Numerics.BigInteger.Parse(s, CultureInfo.InvariantCulture);
            if (m.IsZero) return neg ? -0.0 : 0.0;
            System.Numerics.BigInteger num = m, den = System.Numerics.BigInteger.One;
            if (e10 >= 0) num *= System.Numerics.BigInteger.Pow(10, e10); else den = System.Numerics.BigInteger.Pow(10, -e10);
            // scale so the quotient has at least 55 bits
            int shift = 55 - (BitLen(num) - BitLen(den));
            if (shift > 0) num <<= shift; else den <<= -shift;
            var q = System.Numerics.BigInteger.DivRem(num, den, out var rem);
            int extra = BitLen(q) - 53;
            var mant = q >> extra;
            var low = q - (mant << extra);
            var half = System.Numerics.BigInteger.One << (extra - 1);
            int cmp = low.CompareTo(half);
            if (cmp > 0 || (cmp == 0 && !rem.IsZero) || (cmp == 0 && rem.IsZero && !mant.IsEven)) mant += 1;
            int e2 = extra - shift;
            if (BitLen(mant) > 53) { mant >>= 1; e2++; }
            double v = (double)(long)mant * Math.Pow(2, e2);   // exact: 53-bit mantissa times a power of two
            return neg ? -v : v;
        }

        static int BitLen(System.Numerics.BigInteger v)
        {
            int n = 0;
            var b = v.ToByteArray();
            int top = b.Length - 1;
            while (top > 0 && b[top] == 0) top--;
            n = top * 8;
            for (int x = b[top]; x != 0; x >>= 1) n++;
            return n;
        }

        static double JsCall(JsDeformReference js, Call c) => c.Fn == "rut"
            ? js.Rut(c.A[0], c.A[1], c.A[2], c.A[3], c.A[4], c.A[5], c.A[6], c.A.Length > 7 ? c.A[7] : 0, c.A.Length > 8 ? c.A[8] : 0)
            : js.Brush(c.A[0], c.A[1], c.A[2], c.A[3]);

        /// <summary>The call through the IStampSink contract (float arguments, as the vehicles call it).</summary>
        static void SinkCall(IStampSink s, Call c)
        {
            var a = c.A;
            if (c.Fn == "rut") s.Rut((float)a[0], (float)a[1], (float)a[2], (float)a[3], (float)a[4], (float)a[5], (float)a[6], a.Length > 7 ? (float)a[7] : 0f, a.Length > 8 ? (float)a[8] : 0f);
            else s.Brush((float)a[0], (float)a[1], (float)a[2], (float)a[3]);
        }

        static StampRecord StampOf(Call c)
        {
            var a = c.A;
            return c.Fn == "rut"
                ? StampRecord.Of(RutStamp.FromJs((float)a[0], (float)a[1], (float)a[2], (float)a[3], (float)a[4], (float)a[5], (float)a[6], a.Length > 7 ? (float)a[7] : 0f, a.Length > 8 ? (float)a[8] : 0f))
                : StampRecord.Of(BrushStamp.FromJs((float)a[0], (float)a[1], (float)a[2], (float)a[3]));
        }

        /// <summary>exportUnity.js track(): the vertex box the export compared (radius + 2 cells).</summary>
        static void Region(Call c, out int i0, out int j0, out int i1, out int j1)
        {
            double rad = c.Fn == "rut" ? c.A[2] + 0.55 : c.A[2];
            double gi = (c.A[0] + 512) / 0.5, gj = (c.A[1] + 512) / 0.5, rc = rad / 0.5 + 2;
            i0 = Math.Max(0, (int)Math.Floor(gi - rc)); j0 = Math.Max(0, (int)Math.Floor(gj - rc));
            i1 = Math.Min(N, (int)Math.Ceiling(gi + rc)); j1 = Math.Min(N, (int)Math.Ceiling(gj + rc));
        }

        static uint Fnv(uint h, byte v) => (h ^ v) * 0x01000193u;
        static uint FnvU32(uint h, uint v) { for (int s = 0; s < 32; s += 8) h = Fnv(h, (byte)(v >> s)); return h; }

        static string ArtifactsDir()
        {
            var d = Path.Combine(CanonImporter.RepoRoot, "artifacts", "deform");
            Directory.CreateDirectory(d);
            return d;
        }

        // ------------------------------------------------------------------ Deform.MatchesJs

        /// <summary>
        /// The 10,000 reference calls replayed in order through IStampSink (quantised stamps, integer kernels): max height
        /// error against JS (as a curve over the call index) at most 0.5 mm over the first 1,000 calls and 2 mm over all,
        /// displaced volume within 2 %. The JS mirror must first reproduce every per-call hash of the browser game.
        /// </summary>
        [Test]
        public void MatchesJs()
        {
            var calls = Calls();
            Assert.AreEqual(10000, calls.Count, "calls.json");
            using var grid = FreshGrid();
            using var sink = new DeformSystem(grid);
            var js = Mirror(grid);
            float* h = (float*)grid.H.GetUnsafeReadOnlyPtr();
            var h0 = grid.H0.ToArray();
            var touched = new List<int>();
            var seen = new HashSet<int>();
            var curve = new double[calls.Count];
            int mirrorBad = 0;
            string firstBad = null;
            double sumJs = 0;
            for (int c = 0; c < calls.Count; c++)
            {
                var call = calls[c];
                Region(call, out int i0, out int j0, out int i1, out int j1);
                int w = i1 - i0 + 1, hh = j1 - j0 + 1;
                var bh = new float[w * hh]; var bw = new byte[w * hh]; var bs = new byte[w * hh];
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        int k = j * VN + i, l = (j - j0) * w + (i - i0);
                        bh[l] = js.H[k]; bw[l] = js.Wear[k]; bs[l] = js.Surface[k];
                        if (seen.Add(k)) touched.Add(k);
                    }
                double ret = JsCall(js, call);
                sumJs += ret;
                uint fh = 0x811c9dc5; int n = 0;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        int k = j * VN + i, l = (j - j0) * w + (i - i0);
                        if (bh[l] == js.H[k] && bw[l] == js.Wear[k] && bs[l] == js.Surface[k]) continue;
                        n++;
                        fh = FnvU32(fh, (uint)k); fh = FnvU32(fh, math.asuint(js.H[k])); fh = Fnv(fh, js.Wear[k]); fh = Fnv(fh, js.Surface[k]);
                    }
                if (ret != call.Ret || n != call.N || fh.ToString("x8") != call.Hash)
                {
                    mirrorBad++;
                    firstBad ??= $"call {c} {call.Fn}: return {ret:R} vs {call.Ret:R}, changed {n} vs {call.N}, hash {fh:x8} vs {call.Hash}";
                }

                SinkCall(sink, call);

                double mx = 0;
                foreach (int k in touched)
                {
                    double e = Math.Abs((double)h[k] - js.H[k]);
                    if (e > mx) mx = e;
                }
                curve[c] = mx;
            }
            Assert.AreEqual(0, mirrorBad, "the JS mirror does not reproduce the browser game: " + firstBad);

            double vJs = 0, vInt = 0, netJs = 0, netInt = 0;
            int wearDiff = 0, wearMax = 0, surfDiff = 0;
            foreach (int k in touched)
            {
                vJs += Math.Abs((double)js.H[k] - h0[k]); vInt += Math.Abs((double)h[k] - h0[k]);
                netJs += (double)js.H[k] - h0[k]; netInt += (double)h[k] - h0[k];
                int dw = Math.Abs(grid.Wear[k] - js.Wear[k]);
                if (dw != 0) wearDiff++;
                wearMax = Math.Max(wearMax, dw);
                if (grid.Surface[k] != js.Surface[k]) surfDiff++;
            }
            vJs *= 0.25; vInt *= 0.25; netJs *= 0.25; netInt *= 0.25;
            double max1000 = curve.Take(1000).Max(), maxAll = curve.Max();
            double volErr = (vInt - vJs) / vJs;

            var csv = new StringBuilder("call,max_abs_height_error_mm\n");
            for (int c = 0; c < curve.Length; c++) csv.Append(c + 1).Append(',').Append((curve[c] * 1000).ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
            var csvPath = Path.Combine(ArtifactsDir(), "MatchesJs-curve.csv");
            File.WriteAllText(csvPath, csv.ToString());
            var pts = string.Join("  ", new[] { 1, 10, 100, 500, 1000, 2000, 4000, 6000, 8000, 10000 }.Select(i => $"{i}:{curve[i - 1] * 1000:F4}"));
            TestContext.WriteLine($"Deform.MatchesJs: mirror bit-exact on {calls.Count} calls; max height error {max1000 * 1000:F4} mm (first 1,000), {maxAll * 1000:F4} mm (all 10,000)");
            TestContext.WriteLine($"  curve (call: mm): {pts}  -> {csvPath}");
            TestContext.WriteLine($"  displaced volume JS {vJs:F4} m3, integer {vInt:F4} m3 ({volErr * 100:+0.000;-0.000} %); net JS {netJs:F4} m3, integer {netInt:F4} m3");
            TestContext.WriteLine($"  {touched.Count} vertices touched; WEAR differs at {wearDiff} (max {wearMax}), SURFACE at {surfDiff}; {sink.StampsApplied} stamps in {sink.TotalStampMs:F0} ms (Burst {sink.LastRanBurst})");
            Assert.LessOrEqual(max1000, 0.0005, "max height error over the first 1,000 calls");
            Assert.LessOrEqual(maxAll, 0.002, "max height error over all 10,000 calls");
            Assert.LessOrEqual(Math.Abs(volErr), 0.02, "displaced volume");
        }

        // ------------------------------------------------------------------ Deform.SubMmConverges

        /// <summary>
        /// Ruts that lower the ground by at most 0.3 mm per call (rate 0.0003 m) reach JS's target depth within 1 %: at a
        /// fixed spot on dirt and on grass, and along a slow pass. At 1 mm resolution these steps would round to nothing.
        /// </summary>
        [Test]
        public void SubMmConverges()
        {
            using var grid = FreshGrid();
            using var sink = new DeformSystem(grid);
            var js = Mirror(grid);
            float* h = (float*)grid.H.GetUnsafeReadOnlyPtr();
            const double depth = 0.10, rate = 0.0003, radius = 0.5;
            var report = new List<string>();
            foreach (byte surf in new byte[] { 1, 0 })   // DIRT (soft 1), GRASS (soft 0.5)
            {
                Assert.IsTrue(FindPatch(grid, surf, 6, out int ci, out int cj), $"no 13 x 13 patch of surface {surf}");
                double x = ci * 0.5 - 512 + 0.13, z = cj * 0.5 - 512 + 0.21;
                double soft = JsDeformReference.Softness[surf];
                int reachJs = -1, reachInt = -1;
                int kc = cj * VN + ci;
                double fc = 1 - (0.13 * 0.13 + 0.21 * 0.21) / (radius * radius);
                double targetC = depth * soft * fc;
                for (int c = 0; c < 900; c++)
                {
                    js.Rut(x, z, radius, depth, rate, 0, 0.45);
                    sink.Rut((float)x, (float)z, (float)radius, (float)depth, (float)rate, 0f, 0.45f);
                    if (reachJs < 0 && js.Base[kc] - js.H[kc] >= 0.99 * targetC) reachJs = c + 1;
                    if (reachInt < 0 && grid.Base[kc] - h[kc] >= 0.99 * targetC) reachInt = c + 1;
                }
                double worst = 0, worstRing = 0, sinkInt = grid.Base[kc] - h[kc], sinkJs = js.Base[kc] - js.H[kc];
                int core = 0;
                for (int j = cj - 3; j <= cj + 3; j++)
                    for (int i = ci - 3; i <= ci + 3; i++)
                    {
                        int k = j * VN + i;
                        double dx = i * 0.5 - 512 - x, dz = j * 0.5 - 512 - z;
                        double diff = Math.Abs(((double)grid.Base[k] - h[k]) - ((double)js.Base[k] - js.H[k]));
                        if (Math.Sqrt(dx * dx + dz * dz) < radius) { worst = Math.Max(worst, diff); core++; }
                        else worstRing = Math.Max(worstRing, diff);
                    }
                report.Add($"surface {surf}: centre target {targetC * 1000:F3} mm, JS {sinkJs * 1000:F3} mm (99 % after {reachJs} calls), integer {sinkInt * 1000:F3} mm (after {reachInt}); " +
                           $"worst difference {worst * 1000:F4} mm over the {core} rut vertices, {worstRing * 1000:F4} mm on the berm");
                Assert.Greater(reachInt, 0, $"surface {surf}: the integer rut never reached 99 % of the target");
                Assert.LessOrEqual(Math.Abs(sinkInt - targetC), 0.01 * targetC, $"surface {surf}: centre depth vs target");
                Assert.LessOrEqual(Math.Abs(sinkInt - sinkJs), 0.01 * sinkJs, $"surface {surf}: centre depth vs JS");
                Assert.LessOrEqual(worst, 0.01 * depth * soft, $"surface {surf}: core vertices vs JS");
            }
            // a slow pass: 0.02 m per call over 6 m, three times, same 0.3 mm rate
            {
                Assert.IsTrue(FindPatch(grid, 1, 10, out int ci, out int cj), "no untouched 21 x 21 dirt patch");
                double x0 = ci * 0.5 - 512 - 3, z = cj * 0.5 - 512 + 0.07;
                for (int pass = 0; pass < 3; pass++)
                    for (int c = 0; c <= 300; c++)
                    {
                        double x = x0 + c * 0.02;
                        js.Rut(x, z, radius, depth, rate, 9, 0.45, 1, 0);
                        sink.Rut((float)x, (float)z, (float)radius, (float)depth, (float)rate, 9f, 0.45f, 1f, 0f);
                    }
                double worst = 0, deepest = 0;
                for (int j = cj - 3; j <= cj + 3; j++)
                    for (int i = ci - 8; i <= ci + 8; i++)
                    {
                        int k = j * VN + i;
                        double sJs = (double)js.Base[k] - js.H[k];
                        deepest = Math.Max(deepest, sJs);
                        worst = Math.Max(worst, Math.Abs(((double)grid.Base[k] - h[k]) - sJs));
                    }
                report.Add($"slow pass: deepest JS rut {deepest * 1000:F3} mm, worst difference {worst * 1000:F4} mm");
                Assert.LessOrEqual(worst, 0.01 * depth, "slow pass vs JS");
            }
            foreach (var r in report) TestContext.WriteLine("Deform.SubMmConverges: " + r);
        }

        /// <summary>A vertex whose (2r+1)^2 neighbourhood is all one surface with no mud, far from the map edge.</summary>
        static bool FindPatch(WorldGrid g, byte surf, int r, out int ci, out int cj)
        {
            for (int j = 300; j < N - 300; j += 7)
                for (int i = 300; i < N - 300; i += 7)
                {
                    bool ok = true;
                    for (int dj = -r; dj <= r && ok; dj++)
                        for (int di = -r; di <= r && ok; di++)
                        {
                            int k = (j + dj) * VN + i + di;
                            ok = g.Surface[k] == surf && g.Mud[k] == 0 && g.Wear[k] == 0 && g.DeltaUm[k] == 0;
                        }
                    if (!ok) continue;
                    ci = i; cj = j;
                    return true;
                }
            ci = cj = 0;
            return false;
        }

        // ------------------------------------------------------------------ Deform.Deterministic

        /// <summary>The same stamp log gives identical tile hashes (and identical DeltaUm) with Burst on and off, and again.</summary>
        [Test]
        public void Deterministic()
        {
            var log = new List<StampRecord>();
            var calls = Calls();
            for (int c = 0; c < calls.Count; c++)
            {
                log.Add(StampOf(calls[c]));
                if (c % 50 == 49)   // angle of repose behind the shovel and in the ruts
                    log.Add(StampRecord.Of(RelaxStamp.FromJs((float)calls[c].A[0], (float)calls[c].A[1], 2.0f, 2, 0.36f)));
            }
            ulong[] baseline;
            using (var g = FreshGrid()) using (var d = new DeformSystem(g)) baseline = d.Tiles.HashAll(g);

            ulong[] Run(bool burst, out int[] delta, out bool ranBurst, out double ms)
            {
                using var g = FreshGrid();
                using var d = new DeformSystem(g) { UseBurst = burst };
                bool anyManaged = false;
                for (int i = 0; i < log.Count; i++) { d.Apply(log[i]); if (burst && !d.LastRanBurst) anyManaged = true; }
                ranBurst = burst && !anyManaged;
                ms = d.TotalStampMs;
                delta = g.DeltaUm.ToArray();
                return d.Tiles.HashAll(g);
            }

            var a = Run(true, out var da, out bool burstA, out double msA);
            var b = Run(false, out var db, out _, out double msB);
            var c2 = Run(true, out var dc, out _, out _);
            int changedTiles = 0;
            for (int t = 0; t < a.Length; t++) if (a[t] != baseline[t]) changedTiles++;
            int diffAB = 0, diffAC = 0;
            for (int t = 0; t < a.Length; t++) { if (a[t] != b[t]) diffAB++; if (a[t] != c2[t]) diffAC++; }
            int deltaDiff = 0;
            for (int k = 0; k < da.Length; k++) if (da[k] != db[k] || da[k] != dc[k]) deltaDiff++;
            TestContext.WriteLine($"Deform.Deterministic: {log.Count} stamps ({log.Count(s => s.Kind == StampKind.Relax)} relax); {changedTiles} of {baseline.Length} tiles changed; " +
                                  $"Burst {msA:F0} ms (really Burst: {burstA}), managed {msB:F0} ms; tiles differing Burst/managed {diffAB}, Burst/Burst {diffAC}; DeltaUm differing at {deltaDiff} vertices");
            Assert.IsTrue(burstA, "the Burst path ran managed code (is Burst compilation enabled?)");
            Assert.Greater(changedTiles, 0, "the log changed no tile");
            Assert.AreEqual(0, diffAB, "tile hashes differ between Burst and managed");
            Assert.AreEqual(0, diffAC, "tile hashes differ between two Burst runs");
            Assert.AreEqual(0, deltaDiff, "DeltaUm differs");
        }

        // ------------------------------------------------------------------ lane checks: stamp layout, partial terrain updates, trees

        [Test]
        public void StampLayout()
        {
            Assert.AreEqual(RutStamp.Size, UnsafeUtility.SizeOf<RutStamp>(), "RutStamp size");
            Assert.AreEqual(18, RutStamp.Size);
            Assert.AreEqual(BrushStamp.Size, UnsafeUtility.SizeOf<BrushStamp>(), "BrushStamp size");
            Assert.AreEqual(RelaxStamp.Size, UnsafeUtility.SizeOf<RelaxStamp>(), "RelaxStamp size");
            Assert.AreEqual(0, UnsafeUtility.GetFieldOffset(typeof(RutStamp).GetField("X")));
            Assert.AreEqual(8, UnsafeUtility.GetFieldOffset(typeof(RutStamp).GetField("Radius")));
            Assert.AreEqual(14, UnsafeUtility.GetFieldOffset(typeof(RutStamp).GetField("Dir")));
            Assert.AreEqual(16, UnsafeUtility.GetFieldOffset(typeof(RutStamp).GetField("Berm")));
            Assert.AreEqual(17, UnsafeUtility.GetFieldOffset(typeof(RutStamp).GetField("Wear")));
            // the integer CORDIC that decodes the direction
            double worst = 0;
            for (int d = 0; d < 65536; d += 7)
            {
                Q.CosSinQ30((ushort)d, out long cs, out long sn);
                double a = d * 2 * Math.PI / 65536;
                worst = Math.Max(worst, Math.Max(Math.Abs(cs / 1073741824.0 - Math.Cos(a)), Math.Abs(sn / 1073741824.0 - Math.Sin(a))));
            }
            TestContext.WriteLine($"CORDIC direction decode: worst error {worst:E2}");
            Assert.Less(worst, 1e-7);
            var s = RutStamp.FromJs(-226.45, 30.18, 0.5037, 0.49, 0.0373, 23.96, 0.6, 0.6, 0.8);
            Assert.IsTrue(s.Directional);
            Assert.AreEqual(60, s.Berm & 0x7F);
            TestContext.WriteLine("example: " + s);
        }

        /// <summary>After stamps, a chunk updated in place is bit-identical to the same chunk built from scratch, at LOD 0 and 3.</summary>
        [Test]
        public void TerrainPartialUpdate()
        {
            var calls = Calls();
            using var grid = FreshGrid();
            using var sink = new DeformSystem(grid);
            var root = new GameObject("deform-test");
            try
            {
                foreach (int lod in new[] { 0, 3 })
                {
                    var first = calls[lod == 0 ? 0 : 8000];
                    var focus = new Vector3((float)-first.A[0], 0, (float)first.A[1]);
                    var terrain = new TerrainChunkSystem(grid, null, root.transform);
                    terrain.BuildAll(lod == 0 ? focus + Vector3.up * 30 : new Vector3(-2000, 300, -2000));
                    int builds = terrain.TotalBuilds;
                    var touchedChunks = new HashSet<int2>();
                    int from = lod == 0 ? 0 : 8000;
                    for (int c = from; c < from + 400; c++)
                    {
                        SinkCall(sink, calls[c]);
                        int ci = (int)((calls[c].A[0] + 512) / 0.5), cj = (int)((calls[c].A[1] + 512) / 0.5);
                        touchedChunks.Add(new int2(Math.Min(15, ci / 128), Math.Min(15, cj / 128)));
                    }
                    int rects = 0;
                    while (sink.Dirty.TryDequeue(out var r)) { terrain.MarkDirty(r.x, r.y, r.z, r.w); rects++; }
                    terrain.FlushDirty();
                    Assert.AreEqual(builds, terrain.TotalBuilds, "a chunk was rebuilt");
                    Assert.Greater(terrain.PartialUpdates, 0, "no partial update");
                    int compared = 0, differ = 0;
                    foreach (var ch in touchedChunks)
                    {
                        var mesh = terrain.CurrentMesh(ch.x, ch.y);
                        Assert.NotNull(mesh);
                        int chLod = terrain.LodOf(ch.x, ch.y);
                        if (lod == 3) Assert.AreEqual(3, chLod, "test set-up: far camera should give LOD 3");
                        var fresh = TerrainChunkSystem.BuildMeshes(grid.View, new[] { ch }, new[] { chLod })[0];
                        differ += CompareStreams(mesh, fresh);
                        compared++;
                        Object.DestroyImmediate(fresh);
                    }
                    TestContext.WriteLine($"TerrainPartialUpdate camera for LOD {lod}: 400 calls, {rects} dirty rectangles, {terrain.PartialUpdates} partial updates, {terrain.PartialVerticesLastFlush} vertices written; {compared} chunks compared, {differ} bytes differ");
                    Assert.AreEqual(0, differ, $"LOD {lod}: the partial update differs from a rebuild");
                    terrain.Dispose();
                }
            }
            finally { Object.DestroyImmediate(root); }
        }

        static int CompareStreams(Mesh a, Mesh b)
        {
            int differ = 0;
            using (var da = Mesh.AcquireReadOnlyMeshData(a))
            using (var db = Mesh.AcquireReadOnlyMeshData(b))
            {
                for (int s = 0; s < 2; s++)
                {
                    var x = da[0].GetVertexData<byte>(s);
                    var y = db[0].GetVertexData<byte>(s);
                    if (x.Length != y.Length) return int.MaxValue;
                    for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) differ++;
                }
            }
            return differ;
        }

        /// <summary>Digging under trees moves them down with the ground, and their collider y range with them.</summary>
        [Test]
        public void TreesResnap()
        {
            var canon = Canon();
            var look = AssetDatabase.LoadAssetAtPath<MillbrookLook>("Assets/Backroads/Client.Rendering/Materials/MillbrookLook.asset");
            Assert.NotNull(look, "MillbrookLook.asset");
            using var grid = FreshGrid();
            using var sink = new DeformSystem(grid);
            using var colliders = ColliderGrid.Build(canon.placements.colliders, grid.Layout);
            var root = new GameObject("deform-trees");
            try
            {
                var veg = new VegetationRenderer(canon, look, root.transform, 140f);
                // the first three trees on soft, unlocked ground
                var picked = new List<int>();
                for (int i = 0; i < canon.vegetation.trees.Length && picked.Count < 3; i++)
                {
                    var t = canon.vegetation.trees[i];
                    int k = (int)Math.Round((t.z + 512) / 0.5) * VN + (int)Math.Round((t.x + 512) / 0.5);
                    if (grid.Surface[k] == 0 || grid.Surface[k] == 1) picked.Add(i);
                }
                Assert.AreEqual(3, picked.Count);
                foreach (int i in picked)
                {
                    var t = canon.vegetation.trees[i];
                    for (int n = 0; n < 12; n++) sink.Brush(t.x, t.z, 1.15f, -0.09f);
                }
                int moved = 0;
                while (sink.Dirty.TryDequeue(out var r)) moved += veg.Resnap(r.x, r.y, r.z, r.w, grid.View, colliders);
                var map = new Dictionary<int, int>();
                for (int ci = 0; ci < colliders.Records.Length; ci++) if (colliders.Records[ci].tree >= 0) map[colliders.Records[ci].tree] = ci;
                foreach (int i in picked)
                {
                    var t = canon.vegetation.trees[i];
                    float ground = WorldGridQueries.HeightAtJs(grid.View, t.x, t.z);
                    var tf = root.transform.Find("Vegetation/" + canon.vegetation.species[t.sp].name + "/t" + i);
                    Assert.NotNull(tf, "tree " + i);
                    TestContext.WriteLine($"tree {i}: canon ground {t.y:F3} m, now {ground:F3} m, mesh y {tf.localPosition.y:F3} m");
                    Assert.Less(ground, t.y - 0.05f, "the dig did not lower the ground under tree " + i);
                    Assert.AreEqual(ground - 0.2f, tf.localPosition.y, 1e-4f, "tree " + i + " was not re-snapped");
                    if (map.TryGetValue(i, out int cIdx)) Assert.AreEqual(ground - 1f, colliders.Records[cIdx].y0, 1e-4f, "collider y0 of tree " + i);
                }
                Assert.GreaterOrEqual(moved, 3);
                veg.Destroy();
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
