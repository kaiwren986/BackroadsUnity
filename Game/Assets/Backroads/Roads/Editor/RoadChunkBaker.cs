using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R3 step 5 (05 §5.13): the road meshes into 64 m chunks on the terrain's grid. Each part goes whole to the
    /// chunk holding its centroid; per chunk one vertex buffer (positions relative to the chunk centre), the LOD0 and LOD1
    /// index lists of the three submeshes, and the collider (asphalt and concrete LOD0 triangles, kerb faces included).
    /// Written as one gzip-compressed `BrRoadChunks_(town).roadbake.bytes` (LFS) beside the `RoadChunkSet` that indexes it.
    /// </summary>
    public static class RoadChunkBaker
    {
        public const double ChunkSize = 64.0;

        public static string TownAssetPath(string townId) => RoadNetworkBaker.BakedDir + "/" + RoadChunkSet.TownResourceName(townId) + ".asset";
        public static string TownDataPath(string townId) => RoadNetworkBaker.BakedDir + "/" + RoadChunkSet.TownResourceName(townId) + ".roadbake.bytes";

        /// <summary>The chunk set (not saved; data not yet attached) and the decompressed and compressed payloads.</summary>
        public static RoadChunkSet Build(RoadMeshes m, double playHalf, string source, string roadHash, string frontageHash, out byte[] raw, out byte[] packed) =>
            Build(m, playHalf, source, roadHash, frontageHash, "", out raw, out packed);

        /// <summary>The same, naming the driveways file whose cuts the meshes carry (its hash joins the content hash when not empty).</summary>
        public static RoadChunkSet Build(RoadMeshes m, double playHalf, string source, string roadHash, string frontageHash, string drivewaysSha256, out byte[] raw, out byte[] packed)
        {
            int gridN = (int)Math.Ceiling(2 * playHalf / ChunkSize);
            var byChunk = new SortedDictionary<int, List<RoadMeshPart>>();
            foreach (var p in m.parts)
            {
                var c = p.Centroid;
                int ix = Math.Max(0, Math.Min(gridN - 1, (int)Math.Floor((c.x + playHalf) / ChunkSize)));
                int iz = Math.Max(0, Math.Min(gridN - 1, (int)Math.Floor((c.z + playHalf) / ChunkSize)));
                int key = iz * gridN + ix;
                if (!byChunk.TryGetValue(key, out var l)) byChunk[key] = l = new List<RoadMeshPart>();
                l.Add(p);
            }
            var set = ScriptableObject.CreateInstance<RoadChunkSet>();
            set.meshVersion = RoadMeshBuilder.Version;
            set.source = source ?? "";
            set.roadContentHash = roadHash ?? "";
            set.frontageContentHash = frontageHash ?? "";
            set.drivewaysSha256 = drivewaysSha256 ?? "";
            set.chunkSize = (float)ChunkSize;
            set.originX = set.originZ = (float)-playHalf;
            set.gridN = gridN;
            var chunks = new List<RoadChunkSet.Chunk>();
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                foreach (var kv in byChunk)
                {
                    int ix = kv.Key % gridN, iz = kv.Key / gridN;
                    var centre = new Vector3((float)(-playHalf + (ix + 0.5) * ChunkSize), 0, (float)(-playHalf + (iz + 0.5) * ChunkSize));
                    int start = (int)ms.Position;
                    var rec = WriteChunk(w, kv.Value, centre);
                    rec.ix = ix; rec.iz = iz; rec.centre = centre;
                    rec.offset = start;
                    rec.length = (int)ms.Position - start;
                    chunks.Add(rec);
                    set.lod0Triangles += rec.lod0Triangles;
                    set.lod1Triangles += rec.lod1Triangles;
                    set.colliderTriangles += rec.colliderTriangles;
                }
                w.Flush();
                raw = ms.ToArray();
            }
            set.chunks = chunks.ToArray();
            set.centreLineM = (float)m.centreLineM; set.noPassingM = (float)m.noPassingM; set.edgeLineM = (float)m.edgeLineM; set.warningM = (float)m.warningM;
            set.stopLines = m.stopLines; set.crosswalks = m.crosswalks; set.yieldLines = m.yieldLines;
            set.contentHash = Hash(set, raw);
            using (var o = new MemoryStream())
            {
                using (var gz = new GZipStream(o, System.IO.Compression.CompressionLevel.Optimal, true)) gz.Write(raw, 0, raw.Length);
                packed = o.ToArray();
            }
            return set;
        }

        static RoadChunkSet.Chunk WriteChunk(BinaryWriter w, List<RoadMeshPart> parts, Vector3 centre)
        {
            var rec = new RoadChunkSet.Chunk();
            var verts = new List<(D3 p, Vector3 n, Vector2 a, Vector2 b, Color32 c)>();
            var idx = new List<int>[2, RoadChunkSet.Submeshes];
            for (int l = 0; l < 2; l++) for (int s = 0; s < RoadChunkSet.Submeshes; s++) idx[l, s] = new List<int>();
            var colV = new List<Vector3>();
            var colI = new List<int>();
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (var p in parts)
            {
                int b = verts.Count;
                for (int i = 0; i < p.pos.Count; i++)
                {
                    var q = p.pos[i];
                    verts.Add((q, p.nrm[i], p.uv0[i], p.uv1[i], p.col[i]));
                    x0 = Math.Min(x0, q.x); y0 = Math.Min(y0, q.y); z0 = Math.Min(z0, q.z); x1 = Math.Max(x1, q.x); y1 = Math.Max(y1, q.y); z1 = Math.Max(z1, q.z);
                }
                for (int l = 0; l < 2; l++)
                    if ((p.lods & (1 << l)) != 0)
                        foreach (int t in p.tri) idx[l, (int)p.material].Add(b + t);
                if (p.collider)
                {
                    int cb = colV.Count;
                    foreach (var q in p.pos) colV.Add(new Vector3((float)(q.x - centre.x), (float)q.y, (float)(q.z - centre.z)));
                    foreach (int t in p.tri) colI.Add(cb + t);
                }
            }
            bool wide = verts.Count > 65535;
            w.Write(verts.Count);
            foreach (var v in verts)
            {
                w.Write((float)(v.p.x - centre.x)); w.Write((float)v.p.y); w.Write((float)(v.p.z - centre.z));
                w.Write(v.n.x); w.Write(v.n.y); w.Write(v.n.z);
                w.Write(v.a.x); w.Write(v.a.y); w.Write(v.b.x); w.Write(v.b.y);
                w.Write(v.c.r); w.Write(v.c.g); w.Write(v.c.b); w.Write(v.c.a);
            }
            for (int l = 0; l < 2; l++)
                for (int s = 0; s < RoadChunkSet.Submeshes; s++)
                {
                    var list = idx[l, s];
                    w.Write(list.Count);
                    foreach (int i in list) { if (wide) w.Write(i); else w.Write((ushort)i); }
                    if (l == 0) rec.lod0Triangles += list.Count / 3; else rec.lod1Triangles += list.Count / 3;
                }
            w.Write(colV.Count);
            foreach (var v in colV) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
            w.Write(colI.Count);
            foreach (int i in colI) w.Write(i);
            rec.vertices = verts.Count;
            rec.colliderTriangles = colI.Count / 3;
            rec.bounds = new Bounds(new Vector3((float)(0.5 * (x0 + x1)), (float)(0.5 * (y0 + y1)), (float)(0.5 * (z0 + z1))), new Vector3((float)(x1 - x0), (float)(y1 - y0), (float)(z1 - z0)));
            return rec;
        }

        static string Hash(RoadChunkSet s, byte[] raw)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(s.formatVersion); w.Write(s.meshVersion); w.Write(s.source ?? ""); w.Write(s.roadContentHash ?? ""); w.Write(s.frontageContentHash ?? "");
                // only when the meshes carry driveway cuts (lane S's site chunks and older sets hash as before)
                if (!string.IsNullOrEmpty(s.drivewaysSha256)) w.Write(s.drivewaysSha256);
                w.Write(s.chunkSize); w.Write(s.originX); w.Write(s.originZ); w.Write(s.gridN);
                w.Write(s.chunks.Length);
                foreach (var c in s.chunks) { w.Write(c.ix); w.Write(c.iz); w.Write(c.offset); w.Write(c.length); w.Write(c.vertices); w.Write(c.lod0Triangles); w.Write(c.lod1Triangles); w.Write(c.colliderTriangles); }
                w.Write(raw.Length);
                w.Write(raw);
                w.Flush();
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(ms.ToArray())).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Saves the data file and the chunk set in place (GUIDs kept across re-bakes).</summary>
        public static string Save(string townId, RoadChunkSet fresh, byte[] packed)
        {
            RoadNetworkBaker.EnsureFolder(RoadNetworkBaker.BakedDir);
            string dataPath = TownDataPath(townId), setPath = TownAssetPath(townId);
            var full = Path.Combine(Path.GetDirectoryName(Application.dataPath), dataPath);
            var old = File.Exists(full) ? File.ReadAllBytes(full) : null;
            if (old == null || !Same(old, packed))
            {
                File.WriteAllBytes(full, packed);
                AssetDatabase.ImportAsset(dataPath, ImportAssetOptions.ForceSynchronousImport);
            }
            fresh.data = AssetDatabase.LoadAssetAtPath<TextAsset>(dataPath);
            fresh.name = RoadChunkSet.TownResourceName(townId);
            var asset = AssetDatabase.LoadAssetAtPath<RoadChunkSet>(setPath);
            if (asset == null) AssetDatabase.CreateAsset(fresh, setPath);
            else
            {
                EditorUtility.CopySerialized(fresh, asset);
                asset.name = RoadChunkSet.TownResourceName(townId);
                EditorUtility.SetDirty(asset);
                UnityEngine.Object.DestroyImmediate(fresh);
            }
            AssetDatabase.SaveAssets();
            return setPath;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        public static RoadChunkSet LoadBakedTown(string townId) => AssetDatabase.LoadAssetAtPath<RoadChunkSet>(TownAssetPath(townId));
    }
}
