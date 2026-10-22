using System;
using System.Collections.Generic;
using System.Globalization;
using Backroads.Core.WorldBake;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.2 step 1 (W1): the 1 m DEM resampled to the grid by bicubic convolution (Keys, a = -0.5: it passes through every
    /// lidar sample and invents nothing between them), plus a seeded 2-4 cm micro-noise (two octaves of hashed value
    /// noise at 1.6 m and 0.7 m, tied to the local frame), kept under the lidar's own vertical noise. One static path
    /// for the Burst job and the managed run.
    /// </summary>
    public static class DemKernel
    {
        public const double NoiseCell1 = 1.6, NoiseAmp1 = 0.022, NoiseCell2 = 0.7, NoiseAmp2 = 0.012;

        public struct Ctx
        {
            public int VN;
            public double Cell, Half;
            [ReadOnly] public NativeArray<float> dem;
            public int n;
            public double x0, z0, res;
            public uint seed;
            [NativeDisableParallelForRestriction] public NativeArray<float> outBase;
        }

        public static void Row(ref Ctx c, int j)
        {
            double z = j * c.Cell - c.Half;
            for (int i = 0; i < c.VN; i++)
            {
                double x = c.Half - i * c.Cell;
                double h = Bicubic(ref c, x, z) + Noise(c.seed, x, z);
                c.outBase[j * c.VN + i] = (float)h;
            }
        }

        static double At(ref Ctx c, int i, int j)
        {
            i = i < 0 ? 0 : i >= c.n ? c.n - 1 : i;
            j = j < 0 ? 0 : j >= c.n ? c.n - 1 : j;
            return c.dem[j * c.n + i];
        }

        static void Weights(double t, out double w0, out double w1, out double w2, out double w3)
        {
            double t2 = t * t, t3 = t2 * t;
            w0 = -0.5 * t3 + t2 - 0.5 * t;
            w1 = 1.5 * t3 - 2.5 * t2 + 1.0;
            w2 = -1.5 * t3 + 2.0 * t2 + 0.5 * t;
            w3 = 0.5 * t3 - 0.5 * t2;
        }

        public static double Bicubic(ref Ctx c, double x, double z)
        {
            double fx = (x - c.x0) / c.res, fz = (z - c.z0) / c.res;
            int i = (int)Math.Floor(fx), j = (int)Math.Floor(fz);
            Weights(fx - i, out double a0, out double a1, out double a2, out double a3);
            Weights(fz - j, out double b0, out double b1, out double b2, out double b3);
            double r0 = a0 * At(ref c, i - 1, j - 1) + a1 * At(ref c, i, j - 1) + a2 * At(ref c, i + 1, j - 1) + a3 * At(ref c, i + 2, j - 1);
            double r1 = a0 * At(ref c, i - 1, j) + a1 * At(ref c, i, j) + a2 * At(ref c, i + 1, j) + a3 * At(ref c, i + 2, j);
            double r2 = a0 * At(ref c, i - 1, j + 1) + a1 * At(ref c, i, j + 1) + a2 * At(ref c, i + 1, j + 1) + a3 * At(ref c, i + 2, j + 1);
            double r3 = a0 * At(ref c, i - 1, j + 2) + a1 * At(ref c, i, j + 2) + a2 * At(ref c, i + 1, j + 2) + a3 * At(ref c, i + 2, j + 2);
            return b0 * r0 + b1 * r1 + b2 * r2 + b3 * r3;
        }

        /// <summary>A 32-bit integer hash of a lattice point (deterministic everywhere), mapped to [-1, 1].</summary>
        public static double Hash(uint seed, int ix, int iz)
        {
            uint h = seed ^ (uint)ix * 0x8DA6B343u ^ (uint)iz * 0xD8163841u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h & 0xFFFFFF) / (double)0x7FFFFF - 1.0;
        }

        static double Value(uint seed, double x, double z, double cell)
        {
            double fx = x / cell, fz = z / cell;
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            double tx = fx - ix, tz = fz - iz;
            tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
            double a = Hash(seed, ix, iz), b = Hash(seed, ix + 1, iz), cc = Hash(seed, ix, iz + 1), d = Hash(seed, ix + 1, iz + 1);
            return (a + (b - a) * tx) * (1 - tz) + (cc + (d - cc) * tx) * tz;
        }

        public static double Noise(uint seed, double x, double z) =>
            NoiseAmp1 * Value(seed, x, z, NoiseCell1) + NoiseAmp2 * Value(seed * 747796405u + 2891336453u, x, z, NoiseCell2);

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
        public struct RowJob : IJobParallelFor
        {
            public Ctx c;
            public void Execute(int j) => Row(ref c, j);
        }

        /// <summary>Fills g.base (and g.heights) from the DEM.</summary>
        public static void Resample(BakeGrid g, TownDem dem, uint seed, bool useBurst)
        {
            var c = new Ctx
            {
                VN = g.VN, Cell = g.Cell, Half = g.Half, n = dem.n, x0 = dem.x0, z0 = dem.z0, res = dem.res, seed = seed,
                dem = new NativeArray<float>(dem.y, Allocator.Persistent),
                outBase = new NativeArray<float>(g.Count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory),
            };
            try
            {
                if (useBurst) new RowJob { c = c }.Schedule(g.VN, 8).Complete();
                else for (int j = 0; j < g.VN; j++) Row(ref c, j);
                c.outBase.CopyTo(g.@base);
            }
            finally { c.dem.Dispose(); c.outBase.Dispose(); }
        }
    }

    /// <summary>
    /// T3.2 step 2: the river. Each reach (a river area of water.json) gets one flat water level (the DEM's median inside
    /// it: the lidar is hydro-flattened per tile, so its own water varies by a few centimetres). Inside the reach, every
    /// vertex the lidar shows as water (at most 0.3 m above the level) is carved: the bed falls from the shore over 3 m to
    /// 1.5 m below the surface and on to 3 m in mid-channel (over the next 17 m); higher ground inside the polygon stays
    /// (banks the polygon overlaps, islands). The weir is a concrete crest 0.1 m under the upper level along its mapped
    /// line, the lock a walled chamber (10.6 m inside, 1 m walls 1 m over the upper level) emptied to the lower level.
    /// Ground outside the river within 6 m of the water and up to 0.3 m below its level is lifted 5 cm over it (no dry land
    /// just below the water beside the river; ground far below it, such as the lower channel beside the weir, is another
    /// water body and stays).
    /// </summary>
    public sealed class RiverResult
    {
        public sealed class Reach
        {
            public string id;
            public double level;
            public List<Poly> polys;
            public int wetVertices;
        }

        public readonly List<Reach> reaches = new List<Reach>();
        /// <summary>Per vertex: reach index + 1 of carved water (lock chamber = its own reach), else 0.</summary>
        public byte[] wet;
        /// <summary>Per vertex: 1 weir crest, 2 lock wall, 3 lock chamber.</summary>
        public byte[] structure;
        /// <summary>Per vertex: 1 inside any river polygon.</summary>
        public bool[] inPolygon;
        /// <summary>Per vertex: the distance to the nearest water vertex (metres; float.MaxValue far from the river) and that water's reach + 1.</summary>
        public float[] toWater;
        public int[] nearestReach;
        /// <summary>Vertices lifted beside the river, and the weir crest height.</summary>
        public int lifted;
        public double weirCrest = double.NaN;
        public List<double2[]> weirLines = new List<double2[]>();
        public List<double2[]> lockChambers = new List<double2[]>();
        public List<double2[]> lockWalls = new List<double2[]>();
        public double lockLevel = double.NaN, lockWallTop = double.NaN;
    }

    public static class RiverStep
    {
        public const double LiftBelow = 0.3;
        public const double WetAbove = 0.3, BankRamp = 3.0, ShallowDepth = 1.5, DeepDepth = 3.0, DeepAfter = 17.0, LiftRange = 6.0, Lift = 0.05;
        public const double LockInner = 10.6, LockWall = 1.0, LockWallOver = 1.0, LockFloor = 3.0, WeirHalfWidth = 1.5, WeirUnder = 0.1;

        static double Smooth(double t) { t = t < 0 ? 0 : t > 1 ? 1 : t; return t * t * (3 - 2 * t); }

        public static double Depth(double d) =>
            d < BankRamp ? ShallowDepth * Smooth(d / BankRamp) : ShallowDepth + (DeepDepth - ShallowDepth) * Smooth((d - BankRamp) / DeepAfter);

        public static RiverResult Run(BakeGrid g, TownSource t)
        {
            var r = new RiverResult { wet = new byte[g.Count], structure = new byte[g.Count], inPolygon = new bool[g.Count], toWater = new float[g.Count] };
            for (int k = 0; k < g.Count; k++) r.toWater[k] = float.MaxValue;
            var reachMask = new byte[g.Count];
            double xmin = double.MaxValue, xmax = double.MinValue, zmin = double.MaxValue, zmax = double.MinValue;
            for (int ri = 0; ri < t.reaches.Count; ri++)
            {
                var src = t.reaches[ri];
                var reach = new RiverResult.Reach { id = src.osm, level = src.p50, polys = src.polys };
                r.reaches.Add(reach);
                byte label = (byte)(ri + 1);
                foreach (var p in src.polys)
                {
                    Raster.Fill(g, p, k => { reachMask[k] = label; r.inPolygon[k] = true; });
                    foreach (var q in p.outer) { xmin = Math.Min(xmin, q.x); xmax = Math.Max(xmax, q.x); zmin = Math.Min(zmin, q.y); zmax = Math.Max(zmax, q.y); }
                }
            }
            if (r.reaches.Count == 0) return r;
            int upper = 0, lower = 0;
            for (int ri = 1; ri < r.reaches.Count; ri++)
            {
                if (r.reaches[ri].level > r.reaches[upper].level) upper = ri;
                if (r.reaches[ri].level < r.reaches[lower].level) lower = ri;
            }
            double upLevel = r.reaches[upper].level, lowLevel = r.reaches[lower].level;

            // structures first: the weir crest and the lock chamber with its walls
            foreach (var line in t.weirs)
            {
                r.weirLines.Add(line);
                r.weirCrest = upLevel - WeirUnder;
                StrokeLine(g, line, WeirHalfWidth, k => r.structure[k] = 1);
            }
            if (t.locks.Count > 0)
            {
                r.lockLevel = lowLevel;
                r.lockWallTop = upLevel + LockWallOver;
                var lockReach = new RiverResult.Reach { id = "lock", level = lowLevel, polys = new List<Poly>() };
                foreach (var line in t.locks)
                {
                    if (line.Length < 2) continue;
                    var a = line[0]; var b = line[line.Length - 1];
                    var chamber = Rect(a, b, LockInner / 2, -LockInner / 2);
                    var wallL = Rect(a, b, LockInner / 2 + LockWall, LockInner / 2, 1.0);
                    var wallR = Rect(a, b, -LockInner / 2, -LockInner / 2 - LockWall, 1.0);
                    r.lockChambers.Add(chamber); r.lockWalls.Add(wallL); r.lockWalls.Add(wallR);
                    lockReach.polys.Add(new Poly { outer = chamber });
                    Raster.Fill(g, new[] { wallL }, k => r.structure[k] = 2);
                    Raster.Fill(g, new[] { wallR }, k => r.structure[k] = 2);
                    Raster.Fill(g, new[] { chamber }, k => { if (r.structure[k] == 0) r.structure[k] = 3; });
                }
                r.reaches.Add(lockReach);
            }

            // wet vertices: in a reach, not a structure, and at most WetAbove over the level
            for (int k = 0; k < g.Count; k++)
            {
                if (r.structure[k] == 3) { r.wet[k] = (byte)r.reaches.Count; continue; }   // the lock is the last reach
                int lab = reachMask[k];
                if (lab == 0 || r.structure[k] != 0) continue;
                if (g.@base[k] <= r.reaches[lab - 1].level + WetAbove) r.wet[k] = (byte)lab;
            }

            // distance to the shore inside the water, and the nearest water outside it (one chamfer over the river's box)
            g.Box(xmin, zmin, xmax, zmax, LiftRange + 2, out int i0, out int j0, out int i1, out int j1);
            var shore = Raster.Chamfer(g, k => r.wet[k] == 0, i0, j0, i1, j1);
            var labels = new int[g.Count];
            var seedLab = new int[g.Count];
            for (int k = 0; k < g.Count; k++) seedLab[k] = r.wet[k];
            var toWater = Raster.Chamfer(g, k => r.wet[k] != 0, i0, j0, i1, j1, seedLab, labels);
            r.nearestReach = labels;
            int w = i1 - i0 + 1;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    int k = j * g.VN + i, q = (j - j0) * w + (i - i0);
                    r.toWater[k] = toWater[q];
                    if (r.structure[k] == 1) { g.@base[k] = (float)r.weirCrest; continue; }
                    if (r.structure[k] == 2) { g.@base[k] = (float)r.lockWallTop; continue; }
                    if (r.structure[k] == 3) { g.@base[k] = (float)(r.lockLevel - LockFloor); continue; }
                    int lab = r.wet[k];
                    if (lab != 0)
                    {
                        var reach = r.reaches[lab - 1];
                        g.@base[k] = (float)(reach.level - Depth(shore[q]));
                        reach.wetVertices++;
                        continue;
                    }
                    // beside the water: no dry ground below the nearest reach's surface
                    if (r.inPolygon[k] || toWater[q] > LiftRange) continue;
                    int near = labels[k];
                    if (near <= 0) continue;
                    double floorY = r.reaches[near - 1].level + Lift;
                    if (g.@base[k] < floorY && g.@base[k] >= r.reaches[near - 1].level - LiftBelow) { g.@base[k] = (float)floorY; r.lifted++; }
                }
            return r;
        }

        /// <summary>The rectangle along a -> b between the side offsets u0 and u1 (left = +u), extended by ext at both ends, CCW.</summary>
        static double2[] Rect(double2 a, double2 b, double u0, double u1, double ext = 0)
        {
            var d = math.normalize(b - a);
            var n = new double2(-d.y, d.x);
            var a2 = a - d * ext; var b2 = b + d * ext;
            var poly = new[] { a2 + n * u1, b2 + n * u1, b2 + n * u0, a2 + n * u0 };
            double area = 0;
            for (int k = 0, l = 3; k < 4; l = k++) area += poly[l].x * poly[k].y - poly[k].x * poly[l].y;
            if (area < 0) Array.Reverse(poly);
            return poly;
        }

        /// <summary>Every vertex within halfWidth of the polyline.</summary>
        static void StrokeLine(BakeGrid g, double2[] line, double halfWidth, Action<int> fill)
        {
            double xmin = double.MaxValue, xmax = double.MinValue, zmin = double.MaxValue, zmax = double.MinValue;
            foreach (var q in line) { xmin = Math.Min(xmin, q.x); xmax = Math.Max(xmax, q.x); zmin = Math.Min(zmin, q.y); zmax = Math.Max(zmax, q.y); }
            g.Box(xmin, zmin, xmax, zmax, halfWidth, out int i0, out int j0, out int i1, out int j1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    if (Raster.DistanceToPolyline(g.Local(i, j), line) <= halfWidth) fill(j * g.VN + i);
        }
    }

    /// <summary>
    /// T3.2 step 3: surfaces, mud and tint from land use, the river and the slope (CanonData v1 surface ids: Grass 0,
    /// Dirt 1, Sand 2, Rock 3, Road 4, Gravel 5, Concrete 6, Track 7, Mud 8). Meadow (the default, parks, pitches, the
    /// cemetery, grass) is grass; farmland is dirt in 2 m crop rows (1.5 m dirt, 0.5 m grass) along the field's longest
    /// side; woods are forest floor (dirt, darker and damper tint); the scout's quarry is gravel; the scout's marsh at the
    /// creek mouth is bog (mud, MUD 120-230) on the low, flat river bottom within 150 m; the riverbed is sand, the river's
    /// banks within 3 m of the water and 1 m above it mud; slopes over 60 % rock. Plates paint over all of it.
    /// </summary>
    public static class SurfaceStep
    {
        public const byte Grass = 0, Dirt = 1, Sand = 2, Rock = 3, Gravel = 5, Concrete = 6, Mud = 8;
        public const double RockSlope = 0.6, RowPeriod = 2.0, RowDirt = 1.5, MarshRadius = 150, MarshAbove = 2.5, MarshSlope = 0.15, QuarryRadius = 45;

        public sealed class Result
        {
            public int farmland, wood, marsh, quarry, rock, bank, riverbed;
            public bool[] marshMask;
        }

        /// <param name="natural">The ground before the river step (DEM + micro-noise), for slopes.</param>
        public static Result Run(BakeGrid g, TownSource t, RiverResult river, float[] natural, uint seed)
        {
            var res = new Result { marshMask = new bool[g.Count] };
            // land use, in a fixed order (the areas are sorted by id)
            var kind = new byte[g.Count];   // 0 meadow, 1 farmland, 2 wood
            foreach (var a in t.landuse)
            {
                string lu = a.Tag("landuse"), nat = a.Tag("natural");
                if (lu == "farmland")
                {
                    foreach (var p in a.polys)
                    {
                        var dir = LongestEdgeDirection(p.outer);
                        var nrm = new double2(-dir.y, dir.x);
                        Raster.Fill(g, p, k =>
                        {
                            int i = k % g.VN, j = k / g.VN;
                            double s = math.dot(g.Local(i, j), nrm);
                            double m = s / RowPeriod - Math.Floor(s / RowPeriod);
                            kind[k] = 1;
                            g.surface[k] = m * RowPeriod < RowDirt ? Dirt : Grass;
                            res.farmland++;
                        });
                    }
                }
                else if (nat == "wood" || lu == "forest")
                {
                    foreach (var p in a.polys) Raster.Fill(g, p, k => { kind[k] = 2; g.surface[k] = Dirt; res.wood++; });
                }
            }
            // the scout's quarry: a gravel floor
            var quarry = t.Site("quarry");
            if (quarry != null)
                Disc(g, quarry.local, QuarryRadius, seed ^ 0x51u, (k, w) => { if (w > 0) { g.surface[k] = Gravel; res.quarry++; } });
            // the marsh at the creek mouth: low (within MarshAbove of the nearest river level), flat river bottom
            var marsh = t.Site("wetland");
            double riverLevel = double.NaN;
            foreach (var rr in river.reaches) if (rr.id != "lock") riverLevel = double.IsNaN(riverLevel) ? rr.level : Math.Max(riverLevel, rr.level);
            if (marsh != null && !double.IsNaN(riverLevel))
            {
                Disc(g, marsh.local, MarshRadius, seed ^ 0xB06u, (k, w) =>
                {
                    if (w <= -0.3 || river.wet[k] != 0 || river.structure[k] != 0) return;
                    double above = g.@base[k] - riverLevel;
                    if (above > MarshAbove || Slope(g, natural, k) > MarshSlope) return;
                    // a ragged edge (patches of bog among the grass where the disc fades) and pools of deeper mud inside
                    var p = g.Local(k % g.VN, k / g.VN);
                    double edge = w + 0.35 * Value(seed ^ 0xB07u, p, 14.0) + 0.15 * Value(seed ^ 0xB08u, p, 5.0);
                    if (edge < 0.12) return;
                    double depth = Math.Min(Math.Min(1.0, edge), 1.0 - above / MarshAbove) + 0.25 * Value(seed ^ 0xB09u, p, 7.0);
                    depth = depth < 0 ? 0 : depth > 1 ? 1 : depth;
                    res.marshMask[k] = true;
                    g.surface[k] = Mud;
                    g.mud[k] = (byte)Math.Max(g.mud[k], 120 + (int)(110 * depth));
                    res.marsh++;
                });
            }
            // the river: bed sand, banks mud; slopes rock
            for (int k = 0; k < g.Count; k++)
            {
                if (river.structure[k] == 1 || river.structure[k] == 2) { g.surface[k] = Concrete; continue; }
                if (river.wet[k] != 0) { g.surface[k] = Sand; g.mud[k] = (byte)Math.Max((int)g.mud[k], 60); res.riverbed++; continue; }
                if (res.marshMask[k]) continue;
                // banks: within 3 m of the water and at most 1 m above its level
                if (river.toWater[k] <= 3.0 && river.structure[k] == 0 && river.nearestReach != null && river.nearestReach[k] > 0 &&
                    g.@base[k] - river.reaches[river.nearestReach[k] - 1].level <= 1.0)
                {
                    g.surface[k] = Mud;
                    g.mud[k] = (byte)Math.Max((int)g.mud[k], 110);
                    res.bank++;
                    continue;
                }
                if (Slope(g, natural, k) > RockSlope) { g.surface[k] = Rock; res.rock++; }
            }
            Tint(g, t, kind, river, res, seed);
            return res;
        }

        /// <summary>The slope (rise over run) of layer h at vertex k, by central differences over 2 cells (1 m) each side.</summary>
        public static double Slope(BakeGrid g, float[] h, int k)
        {
            int i = k % g.VN, j = k / g.VN;
            int il = Math.Max(0, i - 2), ir = Math.Min(g.N, i + 2), jd = Math.Max(0, j - 2), ju = Math.Min(g.N, j + 2);
            double gx = (h[j * g.VN + ir] - h[j * g.VN + il]) / ((ir - il) * g.Cell);
            double gz = (h[ju * g.VN + i] - h[jd * g.VN + i]) / ((ju - jd) * g.Cell);
            return Math.Sqrt(gx * gx + gz * gz);
        }

        static double2 LongestEdgeDirection(double2[] p)
        {
            double best = -1;
            var dir = new double2(1, 0);
            for (int k = 0; k < p.Length; k++)
            {
                var d = p[(k + 1) % p.Length] - p[k];
                double l = math.length(d);
                if (l > best + 1e-9) { best = l; dir = d / l; }
            }
            return dir;
        }

        /// <summary>A ragged disc: weight 1 - d / (radius * (0.8 + 0.4 noise(angle))) over every vertex within 1.2 radius.</summary>
        static void Disc(BakeGrid g, double2 c, double radius, uint seed, Action<int, double> f)
        {
            g.Box(c.x, c.y, c.x, c.y, radius * 1.25, out int i0, out int j0, out int i1, out int j1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var q = g.Local(i, j) - c;
                    double d = math.length(q);
                    if (d > radius * 1.25) continue;
                    // the radius wobbles with the direction: value noise on the unit circle (no trigonometry, so every
                    // machine gets the same outline)
                    double wob = d > 1e-9 ? Value(seed, q / d * 2.0, 1.0) : 0.0;
                    double rr = radius * (1.0 + 0.2 * wob);
                    f(j * g.VN + i, 1.0 - d / rr);
                }
        }

        /// <summary>
        /// The 8 m tint grid (tb brightness, td dryness, about -0.7..0.7 like v1): two octaves of value noise (64 m and
        /// 24 m), plus farmland drier (+0.25 td), woods darker and damper (-0.25 tb, -0.2 td), the marsh and the river banks
        /// damp (-0.4 td).
        /// </summary>
        static void Tint(BakeGrid g, TownSource t, byte[] kind, RiverResult river, Result res, uint seed)
        {
            int tn = g.L.TintN;
            double tc = g.L.TintCell;
            for (int tj = 0; tj < tn; tj++)
                for (int ti = 0; ti < tn; ti++)
                {
                    // tint vertex (ti, tj) sits on grid vertex (ti * step, tj * step)
                    int step = (int)Math.Round(g.L.TintStep);
                    int i = Math.Min(g.N, ti * step), j = Math.Min(g.N, tj * step);
                    int k = j * g.VN + i;
                    var p = g.Local(i, j);
                    double tb = 0.45 * Value(seed ^ 0x7B1u, p, 64) + 0.2 * Value(seed ^ 0x7B2u, p, 24);
                    double td = 0.45 * Value(seed ^ 0x7D1u, p, 64) + 0.2 * Value(seed ^ 0x7D2u, p, 24);
                    if (kind[k] == 1) td += 0.25;
                    else if (kind[k] == 2) { tb -= 0.25; td -= 0.2; }
                    if (res.marshMask[k] || g.surface[k] == Mud) td -= 0.4;
                    tb = Math.Max(-0.72, Math.Min(0.72, tb));
                    td = Math.Max(-0.72, Math.Min(0.72, td));
                    int o = (tj * tn + ti) * 2;
                    g.tint[o] = (float)tb;
                    g.tint[o + 1] = (float)td;
                }
        }

        static double Value(uint seed, double2 p, double cell)
        {
            double fx = p.x / cell, fz = p.y / cell;
            int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
            double tx = fx - ix, tz = fz - iz;
            tx = tx * tx * (3 - 2 * tx); tz = tz * tz * (3 - 2 * tz);
            double a = DemKernel.Hash(seed, ix, iz), b = DemKernel.Hash(seed, ix + 1, iz), c = DemKernel.Hash(seed, ix, iz + 1), d = DemKernel.Hash(seed, ix + 1, iz + 1);
            return (a + (b - a) * tx) * (1 - tz) + (c + (d - c) * tx) * tz;
        }
    }

    /// <summary>
    /// Building pads (stage Buildings, ADR-007 open question 1: W computes them from the DEM): every footprint in the play
    /// area becomes a flat Pad plate at the DEM's height at its centroid (buildings.json y), dirt underneath, blend 3 x
    /// the largest step at its corners (1.5-12 m).
    /// </summary>
    public static class PadStep
    {
        public static int Submit(TownSource t, IPlateSink sink)
        {
            int n = 0;
            foreach (var b in t.buildings)
            {
                if (!b.inPlay || b.poly == null || b.poly.Length < 3) continue;
                double step = 0;
                foreach (var q in b.poly) step = Math.Max(step, Math.Abs(t.dem.Bilinear(q.x, q.y) - b.y));
                sink.Submit(new Plate
                {
                    id = string.Format(CultureInfo.InvariantCulture, "pad:{0}:{1}", b.osm, b.part), kind = PlateKind.Pad,
                    polygon = b.poly, surface = SurfaceStep.Dirt, heights = PlateHeights.Plane, plane = new double3(0, 0, b.y),
                    maxSlope = 0, blend = (float)Math.Max(1.5, Math.Min(12, 3 * step)), priority = 0, noDeform = false,
                });
                n++;
            }
            return n;
        }
    }

    /// <summary>ROADD: the distance to the nearest carriageway vertex in 0.5 m steps (CanonData v1: metres = value * 0.5), 255 at most.</summary>
    public static class RoadDistanceStep
    {
        public static int Run(BakeGrid g, byte[] carriageway)
        {
            int n = 0;
            for (int k = 0; k < g.Count; k++) if (carriageway[k] != 0) n++;
            if (n == 0) { for (int k = 0; k < g.Count; k++) g.roadd[k] = 255; return 0; }
            var d = Raster.Chamfer(g, k => carriageway[k] != 0, 0, 0, g.N, g.N);
            for (int k = 0; k < g.Count; k++)
            {
                double steps = d[k] / 0.5;
                g.roadd[k] = (byte)(steps >= 255 ? 255 : (int)Math.Round(steps, MidpointRounding.AwayFromZero));
            }
            return n;
        }
    }
}
