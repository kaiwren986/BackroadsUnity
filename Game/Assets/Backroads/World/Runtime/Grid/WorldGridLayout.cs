using System;
using System.Globalization;

namespace Backroads.World
{
    /// <summary>
    /// The size of one world's grid (T3.1, ADR-006 section 4), read from the world's manifest ("grid": VN, cell, half,
    /// tintN, tintCell). The canon test world (CanonData v1) is 2,049 x 2,049 vertices at 0.5 m over 1,024 m with a
    /// 129 x 129 tint grid; the new Millbrook is 4,097 x 4,097 at 0.5 m over 2,048 m with 257 x 257. Blittable, so Burst
    /// jobs carry it as a field (WorldGridView.Layout); the derived values are filled once by Create so no job divides.
    /// Layout of the layers (JS space, as CanonData v1): k = j * VN + i, x = i * Cell - Half, z = j * Cell - Half.
    /// </summary>
    [Serializable]
    public struct WorldGridLayout : IEquatable<WorldGridLayout>
    {
        /// <summary>Terrain chunks are 128 cells (64 m at 0.5 m), so N must be a multiple of this.</summary>
        public const int ChunkCells = 128;

        /// <summary>Vertices per side (2,049 or 4,097).</summary>
        public int VN;
        /// <summary>Cells per side (VN - 1).</summary>
        public int N;
        /// <summary>VN * VN.</summary>
        public int Count;
        /// <summary>Cell size in metres, a power of two (0.5), so multiplying by InvCell is exact.</summary>
        public float Cell;
        public float InvCell;
        /// <summary>Half the side in metres (512 or 1,024): the grid spans [-Half, Half] in x and z.</summary>
        public float Half;
        /// <summary>Tint vertices per side (129 or 257) and their spacing in metres (8).</summary>
        public int TintN;
        public float TintCell;
        /// <summary>Grid vertices per tint cell (TintCell / Cell = 16).</summary>
        public float TintStep;
        /// <summary>Upper clamp of the float height lookup, N - 1e-3 (world.js heightAt).</summary>
        public float MaxF;
        /// <summary>Deformation fixed point: Q20 units per cell are 2^CellShift; OriginQ20 = Half in Q20.</summary>
        public int CellShift;
        public long OriginQ20;

        /// <summary>The canon test world's layout (CanonData v1, ADR-001): 2,049 x 2,049 at 0.5 m, 129 x 129 tint at 8 m.</summary>
        public static WorldGridLayout CanonV1 => Create(2049, 0.5f, 512f, 129, 8f);

        /// <summary>The new Millbrook's layout (ADR-006 section 4): 4,097 x 4,097 at 0.5 m over 2,048 m, 257 x 257 tint at 8 m.</summary>
        public static WorldGridLayout Millbrook => Create(4097, 0.5f, 1024f, 257, 8f);

        public bool IsValid => VN > 1 && Count == VN * VN && Cell > 0;
        /// <summary>Side length in metres (2 * Half).</summary>
        public float Size => 2f * Half;
        /// <summary>Terrain chunks per side (16 or 32).</summary>
        public int ChunksPerSide => N / ChunkCells;
        /// <summary>16 m cells of the static collider grid per side (64 or 128).</summary>
        public int ColliderCellsPerSide => (int)Math.Round(Size / 16.0);

        /// <summary>
        /// A checked layout: VN - 1 cells of a power-of-two size spanning exactly 2 * half, a whole number of 64 m terrain
        /// chunks, and a tint grid of tintN vertices spaced tintCell spanning the same square.
        /// </summary>
        public static WorldGridLayout Create(int vn, float cell, float half, int tintN, float tintCell)
        {
            if (vn < 3) throw new ArgumentException($"grid VN {vn} is too small");
            if (!(cell > 0) || !IsPowerOfTwo(cell)) throw new ArgumentException($"grid cell {cell} m is not a power of two");
            int n = vn - 1;
            if ((double)n * cell != 2.0 * half) throw new ArgumentException($"grid {n} cells x {cell} m != 2 x half ({half} m)");
            if (n % ChunkCells != 0) throw new ArgumentException($"grid N {n} is not a multiple of {ChunkCells} (64 m terrain chunks)");
            if (tintN < 2 || !(tintCell > 0) || (double)(tintN - 1) * tintCell != 2.0 * half)
                throw new ArgumentException($"tint grid {tintN} x {tintCell} m does not span 2 x half ({half} m)");
            int shift = 20 + (int)Math.Round(Math.Log(cell, 2));
            if (shift < 1 || shift > 30) throw new ArgumentException($"grid cell {cell} m is outside the deformation fixed point");
            if ((long)vn * vn > int.MaxValue) throw new ArgumentException($"grid VN {vn} is too large");
            return new WorldGridLayout
            {
                VN = vn, N = n, Count = vn * vn, Cell = cell, InvCell = 1f / cell, Half = half,
                TintN = tintN, TintCell = tintCell, TintStep = tintCell / cell, MaxF = n - 1e-3f,
                CellShift = shift, OriginQ20 = (long)Math.Round(half * 1048576.0),
            };
        }

        static bool IsPowerOfTwo(float v)
        {
            double l = Math.Log(v, 2);
            return Math.Abs(l - Math.Round(l)) < 1e-9 && Math.Pow(2, Math.Round(l)) == v;
        }

        /// <summary>From an imported WorldInfo (its grid block came from the same manifest grid).</summary>
        public static WorldGridLayout FromWorldInfo(WorldInfo w)
        {
            if (w == null) throw new ArgumentNullException(nameof(w));
            var l = Create(w.vn, w.cell, w.half, w.tintN, w.tintCell);
            if (w.n != 0 && w.n != l.N) throw new ArgumentException($"world info N {w.n} != VN - 1 ({l.N})");
            return l;
        }

        /// <summary>
        /// From a world manifest's JSON text (CanonData v1 manifest.json or a WorldData manifest): its "grid" object with
        /// VN, cell, half, tintN and tintCell (N and size are checked when present).
        /// </summary>
        public static WorldGridLayout FromManifestJson(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new ArgumentException("empty manifest");
            ManifestGrid m;
            try { m = UnityEngine.JsonUtility.FromJson<ManifestGrid>(json); }
            catch (Exception e) { throw new ArgumentException("the manifest is not JSON: " + e.Message); }
            var g = m?.grid;
            if (g == null || g.VN == 0) throw new ArgumentException("the manifest has no \"grid\" object with VN");
            if (g.cell == 0 || g.half == 0 || g.tintN == 0 || g.tintCell == 0) throw new ArgumentException("manifest grid needs VN, cell, half, tintN and tintCell");
            var l = Create(g.VN, g.cell, g.half, g.tintN, g.tintCell);
            if (g.N != 0 && g.N != l.N) throw new ArgumentException($"manifest grid N {g.N} != VN - 1 ({l.N})");
            if (g.size != 0 && g.size != l.Size) throw new ArgumentException($"manifest grid size {g.size} != 2 x half ({l.Size})");
            return l;
        }

        // JsonUtility shapes of a manifest's "grid" block (other fields are ignored; absent numbers read as 0)
        [Serializable] class ManifestGrid { public GridBlock grid; }
        [Serializable] class GridBlock { public int VN, N, tintN; public float cell, half, size, tintCell; }

        public static WorldGridLayout FromManifestFile(string path) => FromManifestJson(System.IO.File.ReadAllText(path));

        public int Index(int i, int j) => j * VN + i;

        /// <summary>JS-space x (or z) of vertex column i (row j).</summary>
        public float X(int i) => i * Cell - Half;

        /// <summary>world.js inBounds: 2 m inside the edge.</summary>
        public bool InBoundsJs(float x, float z) => x > -Half + 2 && x < Half - 2 && z > -Half + 2 && z < Half - 2;

        public bool Equals(WorldGridLayout o) => VN == o.VN && Cell == o.Cell && Half == o.Half && TintN == o.TintN && TintCell == o.TintCell;
        public override bool Equals(object obj) => obj is WorldGridLayout o && Equals(o);
        public override int GetHashCode() => VN * 397 ^ TintN ^ Half.GetHashCode();
        public static bool operator ==(WorldGridLayout a, WorldGridLayout b) => a.Equals(b);
        public static bool operator !=(WorldGridLayout a, WorldGridLayout b) => !a.Equals(b);

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0}x{0} at {1} m over {2} m, tint {3}x{3} at {4} m", VN, Cell, Size, TintN, TintCell);
    }
}
