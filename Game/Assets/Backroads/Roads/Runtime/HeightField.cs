using System;

namespace Backroads.Roads
{
    /// <summary>Ground heights for the road bake (Unity space: y at plan x, z).</summary>
    public interface IHeightField
    {
        double Sample(double x, double z);
        bool Contains(double x, double z);
    }

    /// <summary>
    /// A regular grid of heights (TownData dem/dem_1m.f32 or a test fixture crop): value k = j * nx + i at
    /// x = x0 + i * res, z = z0 + j * res (rows south to north, columns west to east), bilinear between samples,
    /// clamped at the borders. Double precision arithmetic, so the same file gives the same heights everywhere.
    /// </summary>
    public sealed class GridHeightField : IHeightField
    {
        public readonly float[] data;
        public readonly int nx, nz;
        public readonly double x0, z0, res;

        public GridHeightField(float[] data, int nx, int nz, double x0, double z0, double res)
        {
            if (data == null || data.Length != (long)nx * nz) throw new ArgumentException($"height grid: {data?.Length ?? 0} values for {nx} x {nz}");
            if (!(res > 0)) throw new ArgumentException("height grid: resolution must be positive");
            this.data = data; this.nx = nx; this.nz = nz; this.x0 = x0; this.z0 = z0; this.res = res;
        }

        /// <summary>Reads little-endian float32 values with no header (the TownData DEM encoding).</summary>
        public static float[] ReadF32(byte[] bytes)
        {
            if (bytes.Length % 4 != 0) throw new ArgumentException("float32 file length is not a multiple of 4");
            var v = new float[bytes.Length / 4];
            if (BitConverter.IsLittleEndian) Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
            else
                for (int i = 0; i < v.Length; i++)
                {
                    var b = new[] { bytes[4 * i + 3], bytes[4 * i + 2], bytes[4 * i + 1], bytes[4 * i] };
                    v[i] = BitConverter.ToSingle(b, 0);
                }
            return v;
        }

        public bool Contains(double x, double z) =>
            x >= x0 && z >= z0 && x <= x0 + (nx - 1) * res && z <= z0 + (nz - 1) * res;

        public double Sample(double x, double z)
        {
            double fx = (x - x0) / res, fz = (z - z0) / res;
            if (fx < 0) fx = 0; else if (fx > nx - 1) fx = nx - 1;
            if (fz < 0) fz = 0; else if (fz > nz - 1) fz = nz - 1;
            int i = Math.Min((int)fx, nx - 2), j = Math.Min((int)fz, nz - 2);
            double tx = fx - i, tz = fz - j;
            int k = j * nx + i;
            double h00 = data[k], h10 = data[k + 1], h01 = data[k + nx], h11 = data[k + nx + 1];
            return (h00 * (1 - tx) + h10 * tx) * (1 - tz) + (h01 * (1 - tx) + h11 * tx) * tz;
        }
    }
}
