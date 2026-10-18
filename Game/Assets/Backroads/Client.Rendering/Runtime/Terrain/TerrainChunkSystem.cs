using System;
using System.Collections.Generic;
using System.Diagnostics;
using Backroads.World;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Backroads.Client.Rendering
{
    /// <summary>
    /// T1.3: the terrain as 256 chunk meshes of 64 m (03 section 4.3, terrain.js Terrain): LOD steps 1/2/4/8 chosen at
    /// 110/250/470 m x lodScale with +-10 m hysteresis, at most buildsPerFrame builds per frame, nearest first, the
    /// current LOD +-1 kept cached. Builds run as one Burst job per batch (TerrainChunkBuildJob, Mesh.MeshDataArray).
    /// Chunk logic stays in JS space (chunk (cx, cz) covers JS x in [cx*64 - 512, +64]).
    /// </summary>
    public sealed class TerrainChunkSystem : IDisposable
    {
        public const int ChunksPerSide = TerrainChunkBuildJob.ChunksPerSide;
        public const int ChunkCount = ChunksPerSide * ChunksPerSide;
        public const float ChunkSize = 64f;
        public static readonly float[] Thresholds = { 110f, 250f, 470f };

        sealed class Chunk
        {
            public int cx, cz;
            public float x0, z0, minY, maxY, dist;
            public int lod = -1, want = -1;
            public readonly Mesh[] meshes = new Mesh[4];
            public GameObject go;
            public MeshFilter filter;
            public MeshRenderer renderer;
        }

        readonly WorldGrid _grid;
        readonly Chunk[] _chunks = new Chunk[ChunkCount];
        readonly TerrainPalette _pal = TerrainPalette.Create();
        NativeArray<VertexAttributeDescriptor> _layout;
        readonly List<Chunk> _queue = new List<Chunk>();

        public float LodScale = 1f;
        public int BuildsPerFrame = 2;
        public Transform Root { get; }
        /// <summary>Wall time of the last BuildAll (all 256 chunks at their LOD for the camera), milliseconds.</summary>
        public double LastBuildAllMs { get; private set; }
        public int BuildsLastFrame { get; private set; }
        public int TotalBuilds { get; private set; }

        public TerrainChunkSystem(WorldGrid grid, Material material, Transform parent)
        {
            _grid = grid;
            _layout = TerrainChunkBuildJob.CreateLayout(Allocator.Persistent);
            Root = new GameObject("Terrain").transform;
            Root.SetParent(parent, false);
            var h = grid.H;
            for (int cz = 0; cz < ChunksPerSide; cz++)
                for (int cx = 0; cx < ChunksPerSide; cx++)
                {
                    float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
                    for (int j = cz * 128; j <= cz * 128 + 128; j += 4)
                        for (int i = cx * 128; i <= cx * 128 + 128; i += 4)
                        {
                            float v = h[j * WorldGrid.VN + i];
                            if (v < mn) mn = v;
                            if (v > mx) mx = v;
                        }
                    var c = new Chunk { cx = cx, cz = cz, x0 = cx * ChunkSize - 512f, z0 = cz * ChunkSize - 512f, minY = mn - 16, maxY = mx + 16 };
                    c.go = new GameObject($"chunk_{cx:00}_{cz:00}");
                    c.go.transform.SetParent(Root, false);
                    c.filter = c.go.AddComponent<MeshFilter>();
                    c.renderer = c.go.AddComponent<MeshRenderer>();
                    c.renderer.sharedMaterial = material;
                    c.renderer.shadowCastingMode = ShadowCastingMode.On;
                    c.renderer.receiveShadows = true;
                    c.renderer.enabled = false;
                    _chunks[cz * ChunksPerSide + cx] = c;
                }
        }

        public int LodOf(int cx, int cz) => _chunks[cz * ChunksPerSide + cx].lod;

        /// <summary>Unity-space bounds of chunk (cx, cz) (JS x range flipped).</summary>
        public Bounds BoundsOf(int cx, int cz)
        {
            var c = _chunks[cz * ChunksPerSide + cx];
            var min = new Vector3(-(c.x0 + ChunkSize), c.minY, c.z0);
            var max = new Vector3(-c.x0, c.maxY, c.z0 + ChunkSize);
            var b = new Bounds();
            b.SetMinMax(min, max);
            return b;
        }

        int LodFor(float d, int cur)
        {
            for (int k = 0; k < 3; k++)
            {
                float t = Thresholds[k] * LodScale + (cur <= k ? 10 : -10);
                if (d < t) return k;
            }
            return 3;
        }

        void Plan(Vector3 cameraUnity)
        {
            float camX = -cameraUnity.x, camZ = cameraUnity.z;
            _queue.Clear();
            foreach (var c in _chunks)
            {
                float dx = Mathf.Max(c.x0 - camX, 0, camX - (c.x0 + ChunkSize));
                float dz = Mathf.Max(c.z0 - camZ, 0, camZ - (c.z0 + ChunkSize));
                c.dist = Mathf.Sqrt(dx * dx + dz * dz);
                c.want = LodFor(c.dist, c.lod < 0 ? 3 : c.lod);
                if (c.want != c.lod) _queue.Add(c);
            }
            _queue.Sort((a, b) => a.dist.CompareTo(b.dist));
        }

        /// <summary>Builds every chunk at its LOD for this camera in one parallel job (load time); returns milliseconds.</summary>
        public double BuildAll(Vector3 cameraUnity)
        {
            var sw = Stopwatch.StartNew();
            Plan(cameraUnity);
            var todo = new List<Chunk>();
            foreach (var c in _queue) if (c.meshes[c.want] == null) todo.Add(c);
            Build(todo);
            foreach (var c in _queue) SetLod(c, c.want);
            LastBuildAllMs = sw.Elapsed.TotalMilliseconds;
            return LastBuildAllMs;
        }

        /// <summary>Per frame: LOD changes for the camera, at most BuildsPerFrame new meshes, nearest first.</summary>
        public void Update(Vector3 cameraUnity)
        {
            Plan(cameraUnity);
            var todo = new List<Chunk>();
            foreach (var c in _queue)
                if (c.meshes[c.want] == null && todo.Count < BuildsPerFrame) todo.Add(c);
            Build(todo);
            BuildsLastFrame = todo.Count;
            foreach (var c in _queue)
                if (c.meshes[c.want] != null) SetLod(c, c.want);
        }

        void Build(List<Chunk> todo)
        {
            if (todo.Count == 0) return;
            var lods = new int[todo.Count];
            for (int i = 0; i < todo.Count; i++) lods[i] = todo[i].want;
            var meshes = BuildMeshes(_grid.View, todo.ConvertAll(c => new int2(c.cx, c.cz)).ToArray(), lods, _pal, _layout);
            for (int i = 0; i < todo.Count; i++)
            {
                var c = todo[i];
                var m = meshes[i];
                m.bounds = new Bounds(new Vector3(-(c.x0 + ChunkSize * 0.5f), (c.minY + c.maxY) * 0.5f, c.z0 + ChunkSize * 0.5f),
                                      new Vector3(ChunkSize, c.maxY - c.minY, ChunkSize));
                c.meshes[c.want] = m;
            }
            TotalBuilds += todo.Count;
        }

        /// <summary>Builds the given chunks at the given LODs (one Burst job, one chunk per index) and returns new meshes.</summary>
        public static Mesh[] BuildMeshes(WorldGridView grid, int2[] chunks, int[] lods, TerrainPalette pal, NativeArray<VertexAttributeDescriptor> layout)
        {
            int n = chunks.Length;
            var data = Mesh.AllocateWritableMeshData(n);
            using var cs = new NativeArray<int2>(chunks, Allocator.TempJob);
            using var ls = new NativeArray<int>(lods, Allocator.TempJob);
            new TerrainChunkBuildJob { Grid = grid, Chunks = cs, Lods = ls, Layout = layout, Pal = pal, Out = data }.Schedule(n, 1).Complete();
            var meshes = new Mesh[n];
            for (int i = 0; i < n; i++) meshes[i] = new Mesh { name = $"terrain_{chunks[i].x:00}_{chunks[i].y:00}_lod{lods[i]}" };
            Mesh.ApplyAndDisposeWritableMeshData(data, meshes, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            return meshes;
        }

        /// <summary>Convenience for tests and tools: builds with a temporary layout.</summary>
        public static Mesh[] BuildMeshes(WorldGridView grid, int2[] chunks, int[] lods)
        {
            using var layout = TerrainChunkBuildJob.CreateLayout(Allocator.TempJob);
            return BuildMeshes(grid, chunks, lods, TerrainPalette.Create(), layout);
        }

        void SetLod(Chunk c, int lod)
        {
            c.lod = lod;
            c.filter.sharedMesh = c.meshes[lod];
            c.renderer.enabled = true;
            for (int l = 0; l < 4; l++)
                if (Math.Abs(l - lod) > 1 && c.meshes[l] != null) { Kill(c.meshes[l]); c.meshes[l] = null; }
        }

        public int CountAtLod(int lod)
        {
            int n = 0;
            foreach (var c in _chunks) if (c.lod == lod) n++;
            return n;
        }

        public void Dispose()
        {
            foreach (var c in _chunks)
            {
                if (c == null) continue;
                for (int l = 0; l < 4; l++) if (c.meshes[l] != null) { Kill(c.meshes[l]); c.meshes[l] = null; }
            }
            if (_layout.IsCreated) _layout.Dispose();
            if (Root != null) Kill(Root.gameObject);
        }

        /// <summary>Destroy in play mode and players, DestroyImmediate in edit mode (tests, tools).</summary>
        public static void Kill(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Object.Destroy(o);
            else Object.DestroyImmediate(o);
        }
    }
}
