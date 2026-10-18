using System;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// reference/static_props.glb (the oracle's 7 merged prop buckets) cut into 64 m chunks by the importer (03 section
    /// 4.7 "phase-0 shortcut"), minus the triangles of the 72 placed GLB buildings, which BuildingSpawner draws from
    /// buildings.glb instead. Unity space (x negated, winding flipped). One mesh per chunk, one submesh per bucket.
    /// </summary>
    public sealed class StaticPropsSet : ScriptableObject
    {
        public static readonly string[] BucketNames = { "bld", "metal", "winLit", "winDark", "bulb", "neon", "signRed" };
        public const float ChunkSize = 64f;
        public const int ChunksPerSide = 16;

        [Serializable]
        public sealed class Chunk
        {
            public int cx, cz;
            public Mesh mesh;
            /// <summary>Bucket index (into <see cref="BucketNames"/>) of each submesh.</summary>
            public int[] buckets;
        }

        [Serializable]
        public struct BucketStats
        {
            public string name;
            /// <summary>Vertices in the GLB accessor (the M0 counts).</summary>
            public int vertices;
            /// <summary>SHA-256 of the POSITION accessor bytes (float32 LE, JS space), as in the manifest.</summary>
            public string positionSha256;
            /// <summary>Vertices removed because they belong to a placed GLB building.</summary>
            public int buildingVertices;
            /// <summary>Vertices kept in the chunks.</summary>
            public int keptVertices;
            /// <summary>Sum of all GLB positions (JS space, x y z as doubles) and of the kept + removed ones, to prove nothing was lost.</summary>
            public double[] sourceSum, splitSum;
        }

        public string glbSha256;
        public BucketStats[] buckets;
        public Chunk[] chunks;
    }
}
