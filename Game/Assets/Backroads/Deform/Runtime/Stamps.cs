using System;
using System.Runtime.InteropServices;

namespace Backroads.Deform
{
    /// <summary>
    /// One tyre-compaction call of world.js rut(), quantised (06 section 5.6, ADR-003). 18 bytes, Pack = 1, little
    /// endian; frozen as a contract with ADR-003. The owner quantises the JS arguments to a stamp and applies the stamp,
    /// never the arguments, so every machine runs the same integer kernel on the same integers.
    /// <code>
    /// offset size field   unit                                   range
    ///  0     4    X       JS world x, Q20 m (2^-20 m, 0.95 um)    +-2048 m
    ///  4     4    Z       JS world z, Q20 m                       +-2048 m
    ///  8     2    Radius  Q16 m (2^-16 m, 15.3 um)                up to 0.99998 m
    /// 10     2    Depth   16 um                                   up to 1.04856 m
    /// 12     2    Rate    8 um                                    up to 0.52428 m per call
    /// 14     2    Dir     binary angle of atan2(fz, fx), 2pi/65536  full circle (used when Berm bit 7 is set)
    /// 16     1    Berm    bit 7: directional spoil; bits 0-6: berm share in 1/100   0 .. 1.27
    /// 17     1    Wear    wearAdd in 1/4                          0 .. 63.75
    /// </code>
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RutStamp : IEquatable<RutStamp>
    {
        public const int Size = 18;
        public const byte DirectionalBit = 0x80;

        public int X, Z;
        public ushort Radius, Depth, Rate, Dir;
        public byte Berm, Wear;

        public bool Directional => (Berm & DirectionalBit) != 0;

        /// <summary>
        /// Quantises world.js rut(x, z, radius, depth, rate, wearAdd, berm, fx, fz) (JS coordinates). Values beyond a
        /// field's range are clamped; a direction is kept when (fx, fz) is not zero, as its angle (its length is dropped:
        /// the kernel tests the spoil cone against the unit direction).
        /// </summary>
        public static RutStamp FromJs(double x, double z, double radius, double depth, double rate, double wearAdd,
                                      double berm = 0.45, double fx = 0, double fz = 0)
        {
            var s = new RutStamp
            {
                X = Q.ToQ20(x), Z = Q.ToQ20(z),
                Radius = Q.U16(radius * 65536.0),
                Depth = Q.U16(depth / 16e-6),
                Rate = Q.U16(rate / 8e-6),
                Wear = Q.U8(wearAdd * 4.0),
            };
            int b = (int)Q.Round(Math.Max(0, Math.Min(1.27, berm)) * 100.0);
            bool dir = (fx != 0 && !double.IsNaN(fx)) || (fz != 0 && !double.IsNaN(fz));
            if (dir)
            {
                double a = Math.Atan2(fz, fx);                                  // -pi .. pi
                long q = (long)Q.Round(a / (2 * Math.PI) * 65536.0);
                s.Dir = (ushort)(q & 0xFFFF);
                b |= DirectionalBit;
            }
            s.Berm = (byte)b;
            return s;
        }

        /// <summary>The kernel arguments (the exact integer decode every machine uses).</summary>
        public KernelCall Decode()
        {
            long fx = 0, fz = 0;
            if (Directional) Q.CosSinQ30(Dir, out fx, out fz);
            return new KernelCall
            {
                Kind = StampKind.Rut, X = X, Z = Z, Radius = (long)Radius * 16, A = (long)Depth * 16, B = (long)Rate * 8,
                Berm200 = (Berm & 0x7F) * 2, Wear4 = Wear, Fx = fx, Fz = fz,
            };
        }

        /// <summary>Applies the stamp with the integer kernel (managed path).</summary>
        public StampResult Apply(in DeformLayers layers) => StampKernels.Apply(layers, Decode());

        public bool Equals(RutStamp o) => X == o.X && Z == o.Z && Radius == o.Radius && Depth == o.Depth && Rate == o.Rate && Dir == o.Dir && Berm == o.Berm && Wear == o.Wear;
        public override bool Equals(object obj) => obj is RutStamp o && Equals(o);
        public override int GetHashCode() => X ^ (Z * 397) ^ (Radius << 3) ^ (Depth << 7) ^ (Rate << 11) ^ (Dir << 5) ^ (Berm << 13) ^ (Wear << 17);
        public override string ToString() =>
            $"rut x={X / 1048576.0:F6} z={Z / 1048576.0:F6} r={Radius / 65536.0:F5} depth={Depth * 16e-6:F5} rate={Rate * 8e-6:F6} " +
            $"berm={(Berm & 0x7F) / 100.0:F2}{(Directional ? $" dir={Dir}" : "")} wear={Wear / 4.0:F2}";
    }

    /// <summary>
    /// world.js brush() (the shovel), quantised: 12 bytes. Provisional (ADR-003 freezes it with cutBlade in M7).
    /// X, Z in Q20 m; Radius in Q15 m (up to 2 m); Amount in 4 um (+-131 mm; &gt; 0 raises, &lt; 0 digs).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BrushStamp
    {
        public const int Size = 12;
        public int X, Z;
        public ushort Radius;
        public short Amount;

        public static BrushStamp FromJs(double x, double z, double radius, double amount) => new BrushStamp
        {
            X = Q.ToQ20(x), Z = Q.ToQ20(z),
            Radius = Q.U16(Math.Min(radius, 1.99997) * 32768.0),
            Amount = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, Q.Round(amount / 4e-6))),
        };

        public KernelCall Decode() => new KernelCall { Kind = StampKind.Brush, X = X, Z = Z, Radius = (long)Radius * 32, A = (long)Amount * 4 };

        public StampResult Apply(in DeformLayers layers) => StampKernels.Apply(layers, Decode());
    }

    /// <summary>
    /// world.js relax() (angle of repose), quantised: 13 bytes. Provisional (frozen with the blade in M7). X, Z in Q20 m;
    /// Radius in Q12 m (up to 16 m); MaxDiff in 16 um (up to 1.05 m); Iters as is.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RelaxStamp
    {
        public const int Size = 13;
        public int X, Z;
        public ushort Radius, MaxDiff;
        public byte Iters;

        public static RelaxStamp FromJs(double x, double z, double radius, int iters, double maxDiff) => new RelaxStamp
        {
            X = Q.ToQ20(x), Z = Q.ToQ20(z),
            Radius = Q.U16(radius * 4096.0),
            MaxDiff = Q.U16(maxDiff / 16e-6),
            Iters = (byte)Math.Max(0, Math.Min(255, iters)),
        };

        public KernelCall Decode() => new KernelCall { Kind = StampKind.Relax, X = X, Z = Z, Radius = (long)Radius * 256, A = (long)MaxDiff * 16, Iters = Iters };

        public StampResult Apply(in DeformLayers layers) => StampKernels.Apply(layers, Decode());
    }

    public enum StampKind : byte { Rut = 1, Brush = 2, Relax = 3 }

    /// <summary>One entry of a stamp log (tests, replays; the network batches of M5 carry the same structs).</summary>
    public struct StampRecord
    {
        public StampKind Kind;
        public RutStamp Rut;
        public BrushStamp Brush;
        public RelaxStamp Relax;

        public static StampRecord Of(in RutStamp s) => new StampRecord { Kind = StampKind.Rut, Rut = s };
        public static StampRecord Of(in BrushStamp s) => new StampRecord { Kind = StampKind.Brush, Brush = s };
        public static StampRecord Of(in RelaxStamp s) => new StampRecord { Kind = StampKind.Relax, Relax = s };

        public KernelCall Decode()
        {
            switch (Kind)
            {
                case StampKind.Rut: return Rut.Decode();
                case StampKind.Brush: return Brush.Decode();
                case StampKind.Relax: return Relax.Decode();
                default: return default;
            }
        }
    }

    /// <summary>Quantisation helpers: round half away from zero, clamped; the integer CORDIC that decodes Dir.</summary>
    public static class Q
    {
        public static double Round(double v) => Math.Round(v, MidpointRounding.AwayFromZero);

        public static int ToQ20(double metres)
        {
            double v = Round(metres * 1048576.0);
            return v >= int.MaxValue ? int.MaxValue : v <= int.MinValue ? int.MinValue : (int)v;
        }

        public static ushort U16(double v)
        {
            v = Round(v);
            return v <= 0 || double.IsNaN(v) ? (ushort)0 : v >= 65535 ? (ushort)65535 : (ushort)v;
        }

        public static byte U8(double v)
        {
            v = Round(v);
            return v <= 0 || double.IsNaN(v) ? (byte)0 : v >= 255 ? (byte)255 : (byte)v;
        }

        // atan(2^-i) in binary angle units (2^32 = full circle), i = 0..30
        static readonly int[] AtanBam =
        {
            536870912, 316933406, 167458907, 85004756, 42667331, 21354465, 10679838, 5340245, 2670163, 1335087, 667544,
            333772, 166886, 83443, 41722, 20861, 10430, 5215, 2608, 1304, 652, 326, 163, 81, 41, 20, 10, 5, 3, 1, 1,
        };

        /// <summary>
        /// cos and sin of the binary angle dir * 2pi / 65536 in Q30, by integer CORDIC (no floating point, so the decode
        /// is identical on every CPU and compiler). Error below 1e-8.
        /// </summary>
        public static void CosSinQ30(ushort dir, out long cos, out long sin)
        {
            uint a = (uint)dir << 16;                     // full circle = 2^32
            bool flip = false;
            // fold into [-pi/2, pi/2]
            if (a > 0x40000000u && a < 0xC0000000u) { a += 0x80000000u; flip = true; }
            long z = (int)a;                              // signed binary angle
            long x = 652032874, y = 0;                    // K * 2^30
            for (int i = 0; i < 31; i++)
            {
                long xs = x >> i, ys = y >> i;
                if (z >= 0) { x -= ys; y += xs; z -= AtanBam[i]; }
                else { x += ys; y -= xs; z += AtanBam[i]; }
            }
            if (flip) { x = -x; y = -y; }
            cos = x; sin = y;
        }
    }
}
