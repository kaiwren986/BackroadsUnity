using System;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// Root of the imported canon (Assets/Backroads/World/Canon/v1/Resources/BrCanonV1.asset, written by
    /// Backroads.Canon.Editor.CanonImporter, gitignored). It sits in a Resources folder so every build, the server
    /// included, carries the canon without a scene reference; CanonRuntime loads it by name.
    /// </summary>
    public sealed class CanonAsset : ScriptableObject
    {
        public const string ResourceName = "BrCanonV1";

        [Serializable]
        public struct Layer
        {
            /// <summary>Path inside CanonData/v1, e.g. "terrain/heights.f32".</summary>
            public string path;
            public string sha256;
            public long bytes;
            public TextAsset data;
        }

        [Header("Identity")]
        public int version = 1;
        /// <summary>SHA-256 of CanonData/v1/manifest.json: the import cache key.</summary>
        public string manifestSha256;
        public string oracleCommit;
        public int importerVersion;
        public string importedAtUtc;

        [Header("Grid layers (headerless little-endian, k = j*2049 + i)")]
        public Layer heights;
        public Layer @base;
        public Layer surface;
        public Layer wear;
        public Layer roadd;
        public Layer mud;
        public Layer tint;

        [Header("Data")]
        public WorldInfo world;
        public PlacementSet placements;
        public VegetationSet vegetation;
        public SurfaceTable surfaces;
        public LegacyTrafficNetwork traffic;

        [Header("Textures (rows as in the JS DataTexture: row 0 = v 0)")]
        public Texture2D detail;
        public Texture2D waterNormal;
        public Texture2D dirt;
        public Texture2D map;

        [Header("Frozen models and the merged props")]
        public CanonModelSet buildings;
        public CanonModelSet trees;
        public StaticPropsSet staticProps;

        public Layer[] Layers => new[] { heights, @base, surface, wear, roadd, mud, tint };

        /// <summary>The first 8 hex digits of the heights layer hash, for log lines ("CANON v1 OK H=a8908d7f...").</summary>
        public string HeightsShort => string.IsNullOrEmpty(heights.sha256) ? "?" : heights.sha256.Substring(0, 8);
    }
}
