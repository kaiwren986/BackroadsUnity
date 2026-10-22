// A top-down render of the site bake for the report (card sites-S4), drawn on the CPU (no graphics device needed).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// card sites-S4: the baked site meshes seen from straight above, with lane R's road chunks and the ground: a CPU raster
    /// (z-buffered triangles, Lambert light from the north-west, the DEM hillshaded), gravel, dirt and grass yards from the
    /// plans, island beds green, buildings dark, props as dots and fences as lines. Written as PNGs to artifacts/sites/(town):
    /// surfaces-village.png, surfaces-downtown.png and close-ups of the busiest lots.
    /// </summary>
    public static class SiteBakeMap
    {
        struct Tri { public float3 a, b, c; public Color32 col; public byte mat; }

        static readonly float3 Light = math.normalize(new float3(-1, 2.2f, 1));

        public static string WriteArtifacts(SiteBakeResult r)
        {
            var dir = SiteParcels.ArtifactsDir(r.town);
            Directory.CreateDirectory(dir);
            var tris = new List<Tri>();
            AddParts(tris, r.meshes.meshes.parts, site: true);
            AddRoadChunks(tris, r.town);
            var written = new List<string>();
            var data = r.plans.ctx.data;
            if (data.kerbZone != null && data.kerbZone.Length > 2)
            {
                var (mn, mx) = Bounds(data.kerbZone);
                written.Add(Render(r, tris, mn, mx, 0.35, Path.Combine(dir, "surfaces-village.png")));
            }
            if (data.downtownZone != null && data.downtownZone.Length > 2)
            {
                var (mn, mx) = Bounds(data.downtownZone);
                written.Add(Render(r, tris, mn - 40, mx + 40, 0.15, Path.Combine(dir, "surfaces-downtown.png")));
            }
            // close-ups: the four plans with the most stalls, and the fuel station
            var close = r.plans.plans.Where(p => p.lots.Count > 0).OrderByDescending(p => p.lots.Sum(l => l.stalls.Count)).ThenBy(p => p.parcel).Take(4).ToList();
            close.AddRange(r.plans.plans.Where(p => p.type == BusinessType.Fuel && !close.Contains(p)).Take(1));
            foreach (var p in close)
            {
                var (mn, mx) = Bounds(r.plans.ctx.parcels.parcels[p.parcel].outer);
                var c = (mn + mx) * 0.5;
                double half = Math.Max(30, Math.Max(mx.x - mn.x, mx.y - mn.y) * 0.5 + 8);
                written.Add(Render(r, tris, c - half, c + half, Math.Max(0.04, half * 2 / 1400), Path.Combine(dir, $"surfaces-parcel-{p.parcel:D4}.png")));
            }
            return string.Join(", ", written);
        }

        static (double2, double2) Bounds(IEnumerable<double2> pts)
        {
            var mn = new double2(double.MaxValue, double.MaxValue);
            var mx = new double2(double.MinValue, double.MinValue);
            foreach (var q in pts) { mn = math.min(mn, q); mx = math.max(mx, q); }
            return (mn, mx);
        }

        static void AddParts(List<Tri> tris, List<RoadMeshPart> parts, bool site)
        {
            foreach (var p in parts)
                for (int k = 0; k + 2 < p.tri.Count; k += 3)
                {
                    D3 a = p.pos[p.tri[k]], b = p.pos[p.tri[k + 1]], c = p.pos[p.tri[k + 2]];
                    tris.Add(new Tri
                    {
                        a = new float3((float)a.x, (float)a.y, (float)a.z), b = new float3((float)b.x, (float)b.y, (float)b.z), c = new float3((float)c.x, (float)c.y, (float)c.z),
                        col = p.col[p.tri[k]], mat = (byte)((int)p.material + (site ? 0 : 3)),
                    });
                }
        }

        /// <summary>Lane R's committed road chunks (LOD0), if baked.</summary>
        static void AddRoadChunks(List<Tri> tris, string town)
        {
            var set = AssetDatabase.LoadAssetAtPath<RoadChunkSet>(RoadChunkBaker.TownAssetPath(town));
            if (set == null) return;
            var all = set.Decompress();
            foreach (var c in set.chunks)
            {
                RoadChunkSet.ReadChunk(all, c, out var lod0, out var lod1, out var col);
                var v = lod0.vertices;
                var cols = lod0.colors32;
                for (int s = 0; s < lod0.subMeshCount; s++)
                {
                    var idx = lod0.GetTriangles(s);
                    for (int k = 0; k + 2 < idx.Length; k += 3)
                        tris.Add(new Tri
                        {
                            a = (float3)(v[idx[k]] + c.centre), b = (float3)(v[idx[k + 1]] + c.centre), c = (float3)(v[idx[k + 2]] + c.centre),
                            col = cols.Length > 0 ? cols[idx[k]] : new Color32(255, 255, 255, 255), mat = (byte)(s + 3),
                        });
                }
                UnityEngine.Object.DestroyImmediate(lod0);
                UnityEngine.Object.DestroyImmediate(lod1);
                UnityEngine.Object.DestroyImmediate(col);
            }
        }

        static Color32 Base(Tri t)
        {
            switch (t.mat)
            {
                case 0: return new Color32((byte)(84 + t.col.b / 24), (byte)(87 + t.col.b / 24), (byte)(92 + t.col.b / 24), 255); // site asphalt (lighter)
                case 1: return new Color32(178, 175, 168, 255);   // site concrete
                case 3: return new Color32(62, 64, 68, 255);      // road asphalt
                case 4: return new Color32(170, 168, 160, 255);   // road concrete
                default: return t.col;                            // paint: its vertex colour
            }
        }

        /// <summary>A render of the view (min to max, metres per pixel) with the site meshes only (no road chunks): for tests and tools.</summary>
        public static string Render(SiteBakeResult r, double2 mn, double2 mx, double mpp, string path)
        {
            var tris = new List<Tri>();
            AddParts(tris, r.meshes.meshes.parts, site: true);
            return Render(r, tris, mn, mx, mpp, path);
        }

        static string Render(SiteBakeResult r, List<Tri> tris, double2 mn, double2 mx, double mpp, string path)
        {
            int W = Math.Max(16, (int)Math.Ceiling((mx.x - mn.x) / mpp)), H = Math.Max(16, (int)Math.Ceiling((mx.y - mn.y) / mpp));
            var px = new Color32[W * H];
            var zb = new float[W * H];
            for (int i = 0; i < zb.Length; i++) zb[i] = float.NegativeInfinity;
            double2 P(int i, int j) => new double2(mn.x + (i + 0.5) * mpp, mx.y - (j + 0.5) * mpp);
            // the ground: grass, hillshaded from the DEM
            var g = r.ground;
            for (int j = 0; j < H; j++)
                for (int i = 0; i < W; i++)
                {
                    var q = P(i, j);
                    double gx = (g.At(q + new double2(1, 0)) - g.At(q - new double2(1, 0))) * 0.5, gz = (g.At(q + new double2(0, 1)) - g.At(q - new double2(0, 1))) * 0.5;
                    float sh = Shade(math.normalize(new float3((float)-gx, 1, (float)-gz)));
                    px[j * W + i] = Mul(new Color32(112, 138, 88, 255), sh);
                }
            // yards (gravel, dirt, grass) and island beds from the plans
            foreach (var plan in r.plans.plans)
            {
                r.meshes.grades.TryGetValue(plan.parcel, out var pg);
                foreach (var s in plan.surfaces)
                {
                    if (SiteMesher.Paved(s.surface)) continue;
                    var col = s.surface == SiteSurface.Gravel ? new Color32(176, 160, 128, 255) : s.surface == SiteSurface.Dirt ? new Color32(140, 112, 82, 255) : new Color32(104, 140, 80, 255);
                    FillPoly(px, zb, W, H, mn, mx, mpp, s.outer, s.holes, col, pg);
                }
                foreach (var lot in plan.lots)
                    foreach (var isl in lot.islands)
                        if (isl.kerbed && isl.poly != null && isl.poly.Length >= 3) FillPoly(px, zb, W, H, mn, mx, mpp, isl.poly, Array.Empty<double2[]>(), new Color32(92, 132, 70, 255), pg, 0.13f);
            }
            // buildings
            var roofs = new List<double2[]>();
            foreach (var b in r.plans.ctx.data.buildings)
            {
                if (!b.inPlay) continue;
                if (!b.OpenRoof) { FillPoly(px, zb, W, H, mn, mx, mpp, b.poly, Array.Empty<double2[]>(), new Color32(78, 66, 60, 255), null, 0, 1e6f); continue; }
                roofs.Add(b.poly);   // canopies and carports: an outline over what runs under them
            }
            // meshes, z-buffered
            foreach (var t in tris)
            {
                float minx = Math.Min(t.a.x, Math.Min(t.b.x, t.c.x)), maxx = Math.Max(t.a.x, Math.Max(t.b.x, t.c.x));
                float minz = Math.Min(t.a.z, Math.Min(t.b.z, t.c.z)), maxz = Math.Max(t.a.z, Math.Max(t.b.z, t.c.z));
                if (maxx < mn.x || minx > mx.x || maxz < mn.y || minz > mx.y) continue;
                int i0 = Math.Max(0, (int)Math.Floor((minx - mn.x) / mpp)), i1 = Math.Min(W - 1, (int)Math.Ceiling((maxx - mn.x) / mpp));
                int j0 = Math.Max(0, (int)Math.Floor((mx.y - maxz) / mpp)), j1 = Math.Min(H - 1, (int)Math.Ceiling((mx.y - minz) / mpp));
                var n = math.cross(t.b - t.a, t.c - t.a);
                if (math.lengthsq(n) < 1e-12f) continue;
                n = math.normalize(n);
                if (n.y < 0) n = -n;
                var col = Mul(Base(t), t.mat == 2 || t.mat == 5 ? 1f : Shade(n));
                float den = (t.b.z - t.c.z) * (t.a.x - t.c.x) + (t.c.x - t.b.x) * (t.a.z - t.c.z);
                if (Math.Abs(den) < 1e-12f) continue;
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                    {
                        var q = P(i, j);
                        float x = (float)q.x, z = (float)q.y;
                        float w0 = ((t.b.z - t.c.z) * (x - t.c.x) + (t.c.x - t.b.x) * (z - t.c.z)) / den;
                        float w1 = ((t.c.z - t.a.z) * (x - t.c.x) + (t.a.x - t.c.x) * (z - t.c.z)) / den;
                        float w2 = 1 - w0 - w1;
                        if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                        float y = w0 * t.a.y + w1 * t.b.y + w2 * t.c.y;
                        int k = j * W + i;
                        if (y <= zb[k]) continue;
                        zb[k] = y;
                        px[k] = col;
                    }
            }
            foreach (var roof in roofs)
                for (int k = 0; k < roof.Length; k++)
                {
                    var a = roof[k]; var b = roof[(k + 1) % roof.Length];
                    Line(px, W, H, mn, mx, mpp, new Vector3((float)a.x, 0, (float)a.y), new Vector3((float)b.x, 0, (float)b.y), new Color32(78, 66, 60, 255));
                }
            // fences and props on top
            foreach (var f in r.fences)
                for (int k = 0; k + 1 < f.line.Length; k++) Line(px, W, H, mn, mx, mpp, f.line[k], f.line[k + 1], new Color32(120, 84, 50, 255));
            foreach (var p in r.placements)
                Dot(px, W, H, mn, mx, mpp, p.position, p.key == "lamp" ? new Color32(250, 230, 120, 255) : p.prefabs.Length == 0 ? new Color32(70, 120, 220, 255) : new Color32(230, 90, 40, 255), Math.Max(1, (int)Math.Round(0.35 / mpp)));
            foreach (var m in r.models)
            {
                var poly = m.footprint.Select(q => new double2(q.x, q.y)).ToArray();
                FillPoly(px, zb, W, H, mn, mx, mpp, poly, Array.Empty<double2[]>(), new Color32(150, 150, 160, 255), null, 0, 2e6f);
            }
            File.WriteAllBytes(path, Png(px, W, H));
            return path;
        }

        static float Shade(float3 n) => 0.55f + 0.5f * Math.Max(0, math.dot(n, Light));

        static Color32 Mul(Color32 c, float k) =>
            new Color32((byte)Math.Min(255, c.r * k), (byte)Math.Min(255, c.g * k), (byte)Math.Min(255, c.b * k), 255);

        static void FillPoly(Color32[] px, float[] zb, int W, int H, double2 mn, double2 mx, double mpp, double2[] outer, double2[][] holes, Color32 col, PlanGrade g, float lift = 0, float z = float.NaN)
        {
            var (a, b) = Bounds(outer);
            int i0 = Math.Max(0, (int)Math.Floor((a.x - mn.x) / mpp)), i1 = Math.Min(W - 1, (int)Math.Ceiling((b.x - mn.x) / mpp));
            int j0 = Math.Max(0, (int)Math.Floor((mx.y - b.y) / mpp)), j1 = Math.Min(H - 1, (int)Math.Ceiling((mx.y - a.y) / mpp));
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    var q = new double2(mn.x + (i + 0.5) * mpp, mx.y - (j + 0.5) * mpp);
                    if (!Poly2.Contains(outer, q)) continue;
                    bool hole = false;
                    foreach (var h in holes) if (Poly2.Contains(h, q)) { hole = true; break; }
                    if (hole) continue;
                    int k = j * W + i;
                    float y = !float.IsNaN(z) ? z : g != null ? (float)g.H(q) + lift : -1e6f;
                    if (y < zb[k]) continue;
                    zb[k] = y;
                    px[k] = col;
                }
        }

        static void Line(Color32[] px, int W, int H, double2 mn, double2 mx, double mpp, Vector3 a, Vector3 b, Color32 col)
        {
            double L = Vector3.Distance(a, b);
            int n = Math.Max(1, (int)Math.Ceiling(L / (mpp * 0.5)));
            for (int s = 0; s <= n; s++)
            {
                var p = Vector3.Lerp(a, b, (float)s / n);
                int i = (int)Math.Floor((p.x - mn.x) / mpp), j = (int)Math.Floor((mx.y - p.z) / mpp);
                if (i >= 0 && j >= 0 && i < W && j < H) px[j * W + i] = col;
            }
        }

        static void Dot(Color32[] px, int W, int H, double2 mn, double2 mx, double mpp, Vector3 p, Color32 col, int r)
        {
            int ci = (int)Math.Floor((p.x - mn.x) / mpp), cj = (int)Math.Floor((mx.y - p.z) / mpp);
            for (int j = cj - r; j <= cj + r; j++)
                for (int i = ci - r; i <= ci + r; i++)
                    if (i >= 0 && j >= 0 && i < W && j < H && (i - ci) * (i - ci) + (j - cj) * (j - cj) <= r * r) px[j * W + i] = col;
        }

        // ------------------------------------------------------------------------------------------ PNG (RGB8, zlib)

        public static byte[] Png(Color32[] px, int W, int H)
        {
            var raw = new byte[(W * 3 + 1) * H];
            int o = 0;
            for (int j = 0; j < H; j++)
            {
                raw[o++] = 0;
                for (int i = 0; i < W; i++) { var c = px[j * W + i]; raw[o++] = c.r; raw[o++] = c.g; raw[o++] = c.b; }
            }
            byte[] z;
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var d = new DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true)) d.Write(raw, 0, raw.Length);
                uint a = 1, b = 0;
                foreach (var x in raw) { a = (a + x) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                z = ms.ToArray();
            }
            using (var png = new MemoryStream())
            {
                png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var ihdr = new byte[13];
                Be(ihdr, 0, (uint)W); Be(ihdr, 4, (uint)H);
                ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
                Chunk(png, "IHDR", ihdr);
                Chunk(png, "IDAT", z);
                Chunk(png, "IEND", new byte[0]);
                return png.ToArray();
            }
        }

        static void Be(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4];
            Be(len, 0, (uint)data.Length);
            s.Write(len, 0, 4);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            s.Write(t, 0, 4);
            s.Write(data, 0, data.Length);
            uint crc = 0xFFFFFFFF;
            foreach (var x in t) crc = Crc(crc, x);
            foreach (var x in data) crc = Crc(crc, x);
            crc ^= 0xFFFFFFFF;
            var cb = new byte[4];
            Be(cb, 0, crc);
            s.Write(cb, 0, 4);
        }

        static uint[] s_Crc;

        static uint Crc(uint crc, byte b)
        {
            if (s_Crc == null)
            {
                s_Crc = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    s_Crc[n] = c;
                }
            }
            return s_Crc[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
    }
}
