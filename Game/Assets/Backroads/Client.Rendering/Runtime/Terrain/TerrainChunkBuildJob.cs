using System.Runtime.InteropServices;
using Backroads.World;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Client.Rendering
{
    /// <summary>terrain.js PAL, converted from sRGB hex to linear exactly as three.js r170 does (03 section 2: vertex colours are linear).</summary>
    public struct TerrainPalette
    {
        public float3 lush, dry, alpine, dirt, sand, rock, rockDark, rockRed, road, gravel, concrete, track,
                      mud, bogLight, bogDeep, puddle, dug, fill, under, snow;

        public static float3 Linear(int hex)
        {
            static double C(double c) => c < 0.04045 ? c * 0.0773993808 : System.Math.Pow(c * 0.9478672986 + 0.0521327014, 2.4);
            return new float3((float)C(((hex >> 16) & 255) / 255.0), (float)C(((hex >> 8) & 255) / 255.0), (float)C((hex & 255) / 255.0));
        }

        public static TerrainPalette Create() => new TerrainPalette
        {
            lush = Linear(0x46702f), dry = Linear(0x8a8447), alpine = Linear(0x5b6b45),
            dirt = Linear(0x7a5a3b), sand = Linear(0xcfba8f), rock = Linear(0x8a7e70), rockDark = Linear(0x4f4b47), rockRed = Linear(0x8a6a52),
            road = Linear(0x47474a), gravel = Linear(0x857e72), concrete = Linear(0x8c8a85), track = Linear(0x917455),
            mud = Linear(0x3b2a1a), bogLight = Linear(0x55402b), bogDeep = Linear(0x3b2b1c), puddle = Linear(0x221b13),
            dug = Linear(0x5c4231), fill = Linear(0x82623f), under = Linear(0x35503f), snow = Linear(0xeef2f6),
        };
    }

    /// <summary>Vertex stream 0: position and normal (Unity space).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainVertexPN
    {
        public float3 position;
        public float3 normal;
    }

    /// <summary>Vertex stream 1: colour + blendw, colour2 + wear, kindA, kindB, wet (03 section 4.3 packing).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TerrainVertexAttr
    {
        public float4 color;
        public float4 color2;
        public float4 kindA;
        public float4 kindB;
        public float2 wet;
    }

    /// <summary>
    /// T1.3: builds terrain chunks (03 section 4.3, terrain.js build/writeVertex/colorAt) into Mesh.MeshData in parallel,
    /// one chunk per index. JS layout inside; written in Unity space: x negated, normal x negated, winding swapped.
    /// Grid positions are BrCoords.Pos(i*0.5 - 512, H[k], j*0.5 - 512) bit for bit; skirts sit 1.2 + 0.7*step lower.
    /// </summary>
    [BurstCompile]
    public struct TerrainChunkBuildJob : IJobParallelFor
    {
        public const int CellsPerChunk = 128;   // 64 m
        public const int ChunksPerSide = 16;

        public WorldGridView Grid;
        [ReadOnly] public NativeArray<int2> Chunks;     // (cx, cz) in JS grid order
        [ReadOnly] public NativeArray<int> Lods;        // 0..3 -> step 1, 2, 4, 8
        [ReadOnly] public NativeArray<VertexAttributeDescriptor> Layout;
        public TerrainPalette Pal;
        public Mesh.MeshDataArray Out;

        const int N = WorldGrid.N, VN = WorldGrid.VN, TintN = WorldGrid.TintN;
        const byte GRASS = 0, DIRT = 1, SAND = 2, ROCK = 3, ROAD = 4, GRAVEL = 5, CONCRETE = 6, TRACK = 7, MUD = 8;

        public static int Step(int lod) => 1 << lod;
        public static int VertexCount(int lod) { int vn = CellsPerChunk / Step(lod) + 1; return vn * vn + 4 * vn; }
        public static int IndexCount(int lod) { int n = CellsPerChunk / Step(lod); return n * n * 6 + 4 * n * 12; }

        public static NativeArray<VertexAttributeDescriptor> CreateLayout(Allocator a)
        {
            var l = new NativeArray<VertexAttributeDescriptor>(7, a);
            l[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
            l[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0);
            l[2] = new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4, 1);
            l[3] = new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4, 1);
            l[4] = new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.Float32, 4, 1);
            l[5] = new VertexAttributeDescriptor(VertexAttribute.TexCoord2, VertexAttributeFormat.Float32, 4, 1);
            l[6] = new VertexAttributeDescriptor(VertexAttribute.TexCoord3, VertexAttributeFormat.Float32, 2, 1);
            return l;
        }

        public void Execute(int index)
        {
            var md = Out[index];
            int2 ch = Chunks[index];
            int lod = Lods[index];
            int step = Step(lod), n = CellsPerChunk / step, vn = n + 1;
            int gridCount = vn * vn, total = gridCount + 4 * vn;
            md.SetVertexBufferParams(total, Layout);
            md.SetIndexBufferParams(IndexCount(lod), IndexFormat.UInt16);
            var pn = md.GetVertexData<TerrainVertexPN>(0);
            var at = md.GetVertexData<TerrainVertexAttr>(1);
            int iB = ch.x * CellsPerChunk, jB = ch.y * CellsPerChunk;
            for (int r = 0; r <= n; r++)
                for (int c = 0; c <= n; c++)
                    WriteVertex(pn, at, r * vn + c, iB + c * step, jB + r * step, step, 0.0);
            double depth = 1.2 + step * 0.7;
            for (int e = 0; e < 4; e++)
                for (int t = 0; t <= n; t++)
                {
                    EdgeRC(e, t, n, out int r, out int c);
                    WriteVertex(pn, at, gridCount + e * vn + t, iB + c * step, jB + r * step, step, depth);
                }

            var idx = md.GetIndexData<ushort>();
            int p = 0;
            // JS: (a, cc, b), (b, cc, d); x is negated, so the winding is swapped: (a, b, cc), (b, d, cc)
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    int a = r * vn + c, b = a + 1, cc = a + vn, d = cc + 1;
                    idx[p++] = (ushort)a; idx[p++] = (ushort)b; idx[p++] = (ushort)cc;
                    idx[p++] = (ushort)b; idx[p++] = (ushort)d; idx[p++] = (ushort)cc;
                }
            // skirts face both ways in JS (four triangles per edge segment), so only the order changes
            for (int e = 0; e < 4; e++)
                for (int t = 0; t < n; t++)
                {
                    EdgeRC(e, t, n, out int r0, out int c0);
                    EdgeRC(e, t + 1, n, out int r1, out int c1);
                    int g0 = r0 * vn + c0, g1 = r1 * vn + c1;
                    int s0 = gridCount + e * vn + t, s1 = s0 + 1;
                    idx[p++] = (ushort)g0; idx[p++] = (ushort)g1; idx[p++] = (ushort)s0;
                    idx[p++] = (ushort)g1; idx[p++] = (ushort)s1; idx[p++] = (ushort)s0;
                    idx[p++] = (ushort)g0; idx[p++] = (ushort)s0; idx[p++] = (ushort)g1;
                    idx[p++] = (ushort)g1; idx[p++] = (ushort)s0; idx[p++] = (ushort)s1;
                }
            md.subMeshCount = 1;
            md.SetSubMesh(0, new SubMeshDescriptor(0, p), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
        }

        static void EdgeRC(int e, int t, int n, out int r, out int c)
        {
            switch (e)
            {
                case 0: r = 0; c = t; break;
                case 1: r = n; c = t; break;
                case 2: r = t; c = 0; break;
                default: r = t; c = n; break;
            }
        }

        static float Smooth(float a, float b, float x)
        {
            float t = math.saturate((x - a) / (b - a));
            return t * t * (3 - 2 * t);
        }

        void WriteVertex(NativeArray<TerrainVertexPN> pn, NativeArray<TerrainVertexAttr> at, int v, int i, int j, int step, double skirt)
        {
            var H = Grid.H;
            var S = Grid.Surface;
            int k = j * VN + i;
            float h = H[k];
            float x = i * 0.5f - 512f, z = j * 0.5f - 512f;
            // BrCoords.Pos(x, y, z) = (-x, y, z); skirts in double like JS (h - skirt stored as float32)
            float y = skirt == 0.0 ? h : (float)(h - skirt);
            float hl = H[i > 0 ? k - 1 : k], hr = H[i < N ? k + 1 : k];
            float hu = H[j > 0 ? k - VN : k], hd = H[j < N ? k + VN : k];
            float nx = hl - hr, nz = hu - hd, ny = 2f * WorldGrid.Cell;
            float len = math.sqrt(nx * nx + ny * ny + nz * nz);
            pn[v] = new TerrainVertexPN { position = new float3(-x, y, z), normal = new float3(-nx / len, ny / len, nz / len) };
            float slope = math.sqrt(nx * nx + nz * nz) / (2f * WorldGrid.Cell);

            byte s = S[k];
            int st = math.max(1, math.min(step, 4));
            int t2 = i >= st ? k - st : k, t3 = i + st <= N ? k + st : k, t4 = j >= st ? k - VN * st : k, t5 = j + st <= N ? k + VN * st : k;
            byte s2 = S[t2], s3 = S[t3], s4 = S[t4], s5 = S[t5];
            byte other = s;
            int best = 0;
            for (int q = 0; q < 4; q++)
            {
                byte t = q == 0 ? s2 : q == 1 ? s3 : q == 2 ? s4 : s5;
                if (t == s || t == other) continue;
                int cnt = (s2 == t ? 1 : 0) + (s3 == t ? 1 : 0) + (s4 == t ? 1 : 0) + (s5 == t ? 1 : 0);
                if (cnt > best) { best = cnt; other = t; }
            }
            byte sa = (byte)math.min((int)s, other), sb = (byte)math.max((int)s, other);
            float blend = 0.5f;
            if (sa != sb)
            {
                float wb = 0, wt = 0;
                for (int dj = -2; dj <= 2; dj++)
                {
                    int jj = math.clamp(j + dj * st, 0, N);
                    for (int di = -2; di <= 2; di++)
                    {
                        int ii = math.clamp(i + di * st, 0, N);
                        float wgt = (3 - math.abs(di)) * (3 - math.abs(dj));
                        wt += wgt;
                        if (S[jj * VN + ii] == sb) wb += wgt;
                    }
                }
                blend = wb / wt;
            }
            float md = Grid.Mud[k] * (1f / 255f);
            float3 ca = ColorAt(i, j, k, h, slope, sa);
            float3 cb = sa == sb ? ca : ColorAt(i, j, k, h, slope, sb);
            float wear = (s == ROAD || s == CONCRETE) ? 0f : Grid.Wear[k] / 255f;
            float wet = 0f;
            if (md > 0.05f) wet = math.min(1f, 0.2f + md * 0.15f + Smooth(0.08f, 0.35f, Grid.Base[k] - h) * 0.75f);
            at[v] = new TerrainVertexAttr
            {
                color = new float4(ca, blend), color2 = new float4(cb, wear),
                kindA = Kind(sa, slope, md), kindB = Kind(sb, slope, md), wet = new float2(wet, 0),
            };
        }

        static float4 Kind(byte s, float slope, float md)
        {
            float g = 0, r = 0, c = 0, gv = 0;
            if (s == GRASS) g = 1;
            else if (s == ROCK) r = 1;
            else if (s == CONCRETE) c = 1;
            else if (s == GRAVEL || s == ROAD) gv = 1;
            if (s == GRASS || s == DIRT)
            {
                float st = Smooth(0.62f, 1f, slope) * 0.85f;
                r = st; g *= 1 - st;
            }
            if (md > 0.05f) g *= 0.3f;
            return new float4(g, r, c, gv);
        }

        float3 SurfColor(byte s, float h, float tb, float td)
        {
            switch (s)
            {
                case GRASS:
                {
                    var o = math.lerp(Pal.lush, Pal.dry, Smooth(-0.35f, 0.65f, td) * 0.85f);
                    if (h > 45) o = math.lerp(o, Pal.alpine, Smooth(45, 90, h) * 0.6f);
                    return o;
                }
                case DIRT: return math.lerp(Pal.dirt, Pal.dry, 0.15f + tb * 0.1f);
                case SAND: return Pal.sand;
                case ROCK: return math.lerp(math.lerp(Pal.rock, Pal.rockDark, Smooth(-0.5f, 0.7f, tb) * 0.8f), Pal.rockRed, Smooth(0f, 0.8f, td) * 0.45f);
                case ROAD: return Pal.gravel; // the asphalt is its own mesh; what peeks out beside it is the gravel shoulder
                case GRAVEL: return Pal.gravel;
                case CONCRETE: return Pal.concrete;
                case TRACK: return Pal.track;
                case MUD: return math.lerp(Pal.bogLight, Pal.bogDeep, 0.35f + tb * 0.3f);
                default: return Pal.lush;
            }
        }

        float3 ColorAt(int i, int j, int k, float h, float slope, byte s)
        {
            // tint grid (8 m)
            float tu = i / 16f, tv = j / 16f;
            int ti = math.min(TintN - 2, (int)tu), tj = math.min(TintN - 2, (int)tv);
            float fu = tu - ti, fv = tv - tj;
            var T = Grid.Tint;
            int a = (tj * TintN + ti) * 2, b = a + 2, c = a + TintN * 2, d = c + 2;
            float tb = (T[a] * (1 - fu) + T[b] * fu) * (1 - fv) + (T[c] * (1 - fu) + T[d] * fu) * fv;
            float td = (T[a + 1] * (1 - fu) + T[b + 1] * fu) * (1 - fv) + (T[c + 1] * (1 - fu) + T[d + 1] * fu) * fv;
            float3 o = SurfColor(s, h, tb, td);
            if (s == GRASS || s == DIRT)
            {
                o = math.lerp(o, Pal.rock, Smooth(0.62f, 1f, slope) * 0.85f);
                if (h > 118) o = math.lerp(o, Pal.snow, Smooth(118, 135, h + tb * 8) * (1 - Smooth(0.7f, 1.1f, slope)));
            }
            if (s == ROCK && h > 120) o = math.lerp(o, Pal.snow, Smooth(122, 150, h + tb * 10) * (1 - Smooth(0.9f, 1.4f, slope)));
            o *= 1 + tb * 0.1f;
            float md = Grid.Mud[k] * (1f / 255f);
            if (md > 0.05f)
            {
                o = math.lerp(o, Pal.bogDeep, math.min(0.5f, md * 0.6f));
                o = math.lerp(o, Pal.puddle, Smooth(0.1f, 0.4f, Grid.Base[k] - h) * 0.7f);
            }
            if (s != ROAD && s != CONCRETE && s != MUD)
            {
                float dd = Grid.Base[k] - h;
                if (dd > 0.04f) o = math.lerp(o, Pal.dug, Smooth(0.04f, 0.7f, dd) * 0.75f);
                else if (dd < -0.06f && s == DIRT) o = math.lerp(o, Pal.fill, Smooth(0.06f, 0.5f, -dd) * 0.7f);
                byte w = Grid.Wear[k];
                if (w > 0) o = math.lerp(o, Pal.mud, (w / 255f) * (s == ROCK ? 0.3f : 0.75f));
            }
            if (h < 0)
            {
                float depth = -h;
                o = math.lerp(o, Pal.under, math.min(1f, depth * 0.25f));
                o *= math.exp(-depth * 0.18f);
            }
            return o;
        }
    }
}
