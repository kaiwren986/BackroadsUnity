using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Roads
{
    /// <summary>One road vertex as the chunk meshes store it (05 §5.9): 44 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RoadVertex
    {
        public float3 position, normal;
        public float2 uv0, uv1;
        public Color32 color;
        public const int Size = 44;
    }

    /// <summary>
    /// roads-R3 step 5 (05 §5.13): a town's road meshes in 64 m chunks on the terrain's grid, baked by
    /// Backroads.Roads.Editor.RoadChunkBaker. This small asset indexes the chunks; their data is one gzip-compressed binary
    /// (`BrRoadChunks_(town).roadbake.bytes`, in LFS). Per chunk: one vertex buffer (positions relative to the chunk centre),
    /// LOD0 and LOD1 index lists for the three submeshes (Asphalt, Concrete, Paint), and the collider (asphalt and concrete,
    /// kerb faces included). RoadChunkLoader builds the GameObjects.
    /// </summary>
    public sealed class RoadChunkSet : ScriptableObject
    {
        public const int FormatVersion = 1;
        public const string ResourceName = "BrRoadChunks";
        public const uint Magic = 0x42525242; // "BRRB"
        public const int Submeshes = 3;

        public static string TownResourceName(string townId) => ResourceName + "_" + townId;

        [Serializable]
        public struct Chunk
        {
            public int ix, iz;
            public Vector3 centre;
            public Bounds bounds;
            public int vertices, lod0Triangles, lod1Triangles, colliderTriangles;
            /// <summary>Byte range of this chunk in the decompressed data.</summary>
            public int offset, length;
        }

        public int formatVersion = FormatVersion;
        public int meshVersion;
        public string source = "", roadContentHash = "", frontageContentHash = "";
        /// <summary>SHA-256 of the driveways file (lane S's tools/world/sites/(town)/driveways.json) whose cuts the meshes carry; "" when none.</summary>
        public string drivewaysSha256 = "";
        /// <summary>SHA-256 of the decompressed data and the chunk table.</summary>
        public string contentHash = "";
        public float chunkSize = 64, originX, originZ;
        public int gridN;
        public TextAsset data;
        public Chunk[] chunks = Array.Empty<Chunk>();
        public int lod0Triangles, lod1Triangles, colliderTriangles;
        /// <summary>The marking totals, for the report: metres of centre line (and of it in no-passing zones), edge line, warning strips; counts.</summary>
        public float centreLineM, noPassingM, edgeLineM, warningM;
        public int stopLines, crosswalks, yieldLines;

        public static RoadChunkSet LoadTown(string townId) => Resources.Load<RoadChunkSet>(TownResourceName(townId));

        /// <summary>The decompressed chunk data.</summary>
        public byte[] Decompress()
        {
            if (data == null) return Array.Empty<byte>();
            using (var src = new MemoryStream(data.bytes))
            using (var gz = new GZipStream(src, CompressionMode.Decompress))
            using (var dst = new MemoryStream())
            {
                gz.CopyTo(dst);
                return dst.ToArray();
            }
        }

        /// <summary>The meshes of one chunk: LOD0 and LOD1 (three submeshes each, sharing the vertex data) and the collider.</summary>
        public static void ReadChunk(byte[] all, Chunk c, out Mesh lod0, out Mesh lod1, out Mesh collider, string name = "")
        {
            using (var ms = new MemoryStream(all, c.offset, c.length, false))
            using (var r = new BinaryReader(ms))
            {
                int nv = r.ReadInt32();
                var verts = new RoadVertex[nv];
                for (int i = 0; i < nv; i++)
                {
                    verts[i].position = new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    verts[i].normal = new float3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    verts[i].uv0 = new float2(r.ReadSingle(), r.ReadSingle());
                    verts[i].uv1 = new float2(r.ReadSingle(), r.ReadSingle());
                    verts[i].color = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());
                }
                bool wide = nv > 65535;
                var lods = new Mesh[2];
                for (int lod = 0; lod < 2; lod++)
                {
                    var subs = new int[Submeshes][];
                    int total = 0;
                    for (int s = 0; s < Submeshes; s++)
                    {
                        int n = r.ReadInt32();
                        var idx = new int[n];
                        for (int i = 0; i < n; i++) idx[i] = wide ? r.ReadInt32() : r.ReadUInt16();
                        subs[s] = idx;
                        total += n;
                    }
                    lods[lod] = Build(verts, subs, total, wide, c.bounds, c.centre, $"{name} LOD{lod}");
                }
                lod0 = lods[0];
                lod1 = lods[1];
                int cv = r.ReadInt32();
                var cp = new Vector3[cv];
                for (int i = 0; i < cv; i++) cp[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int ci = r.ReadInt32();
                var cidx = new int[ci];
                for (int i = 0; i < ci; i++) cidx[i] = r.ReadInt32();
                collider = new Mesh { name = name + " collider", indexFormat = cv > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                collider.SetVertices(cp);
                collider.SetTriangles(cidx, 0, false);
                collider.bounds = new Bounds(c.bounds.center - c.centre, c.bounds.size);
            }
        }

        /// <summary>A mesh through Unity's per-attribute API (it takes care of each platform's colour byte order).</summary>
        static Mesh Build(RoadVertex[] verts, int[][] subs, int total, bool wide, Bounds b, Vector3 centre, string name)
        {
            int n = verts.Length;
            var pos = new Vector3[n];
            var nrm = new Vector3[n];
            var uv0 = new Vector2[n];
            var uv1 = new Vector2[n];
            var col = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                pos[i] = verts[i].position;
                nrm[i] = verts[i].normal;
                uv0[i] = verts[i].uv0;
                uv1[i] = verts[i].uv1;
                col[i] = verts[i].color;
            }
            var m = new Mesh { name = name, indexFormat = wide ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            m.SetVertices(pos);
            m.SetNormals(nrm);
            m.SetUVs(0, uv0);
            m.SetUVs(1, uv1);
            m.SetColors(col);
            m.subMeshCount = subs.Length;
            for (int s = 0; s < subs.Length; s++) m.SetTriangles(subs[s], s, false);
            m.bounds = new Bounds(b.center - centre, b.size);
            return m;
        }
    }

    /// <summary>
    /// Builds a town's road chunks (05 §5.13): one GameObject per chunk at its centre with a LODGroup (LOD0 under about
    /// 250 m, LOD1 to about 600 m, then culled: the terrain's road colours carry it), one MeshRenderer per LOD with the
    /// three road materials, and a MeshCollider (asphalt and concrete, kerb faces included). Without materials (a server)
    /// only the colliders are made.
    /// </summary>
    public static class RoadChunkLoader
    {
        public static GameObject Spawn(RoadChunkSet set, Transform parent, Material[] materials, bool colliders = true, int layer = 0)
        {
            var root = new GameObject("Roads (R3 chunks)");
            root.transform.SetParent(parent, false);
            if (set == null || set.chunks.Length == 0) return root;
            var all = set.Decompress();
            foreach (var c in set.chunks)
            {
                string name = $"Road chunk {c.ix:D2}_{c.iz:D2}";
                RoadChunkSet.ReadChunk(all, c, out var lod0, out var lod1, out var col, name);
                var go = new GameObject(name) { layer = layer };
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = c.centre;
                if (materials != null && materials.Length >= RoadChunkSet.Submeshes)
                {
                    var r0 = Renderer(go, "LOD0", lod0, materials, layer);
                    var r1 = Renderer(go, "LOD1", lod1, materials, layer);
                    var g = go.AddComponent<LODGroup>();
                    g.fadeMode = LODFadeMode.None;
                    g.SetLODs(new[] { new LOD(0.22f, new Renderer[] { r0 }), new LOD(0.09f, new Renderer[] { r1 }) });
                    g.size = set.chunkSize;
                }
                else { Release(lod0); Release(lod1); }
                if (colliders && col.vertexCount > 0)
                {
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = col;
                }
            }
            return root;
        }

        static void Release(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        static Renderer Renderer(GameObject parent, string name, Mesh mesh, Material[] materials, int layer)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = materials;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = true;
            return r;
        }
    }
}
