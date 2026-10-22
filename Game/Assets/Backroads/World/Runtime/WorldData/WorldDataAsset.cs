using System;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// T3.3: a baked world (WorldData/(world)/v1, T3.2) imported for run time, as CanonAsset is for the canon test world:
    /// written by Backroads.World.Bake.WorldDataImporter into Assets/Backroads/World/Imported/(world)/Resources (gitignored)
    /// so every build carries it; WorldDataRuntime loads it by name (BrWorld_(world)). Positions here are Unity space
    /// (the town's local frame); the grid layers keep CanonData v1's JS layout like CanonAsset's.
    /// </summary>
    public sealed class WorldDataAsset : ScriptableObject
    {
        public const string ResourcePrefix = "BrWorld_";
        public static string ResourceName(string worldId) => ResourcePrefix + worldId;

        [Serializable]
        public struct Reach
        {
            public string id;
            /// <summary>The flat water level (local y).</summary>
            public float level;
            public int wetVertices;
        }

        [Serializable]
        public struct Deck
        {
            public string osmWay, over;
            public bool overRiver;
            /// <summary>The deck's footprint (x, z), Unity space.</summary>
            public Vector2[] outline;
            public float yMin, yMax;
        }

        [Serializable]
        public struct Building
        {
            public string osm, type, business;
            public int part;
            /// <summary>The pad's height (the terrain under the footprint) and the footprint (x, z), Unity space.</summary>
            public float padY, area;
            public Vector2[] footprint;
            /// <summary>Lane A's model (T3.4) placed at origin without rotation, or null: then the grey box mesh.</summary>
            public GameObject model;
            public Vector3 origin;
            public Mesh greyBox;
            public Color32 colour;
            public float height;
        }

        [Serializable]
        public struct Bookmark
        {
            public string id, name;
            public Vector3 position, target;
            public float fov;
        }

        [Header("Identity")]
        public string worldId;
        public string townName;
        /// <summary>SHA-256 of WorldData/(world)/v1/manifest.json: the import cache key.</summary>
        public string manifestSha256;
        public string bakeCommit;
        public int importerVersion;
        public string importedAtUtc;
        public WorldGridLayout layout;

        [Header("Grid layers (headerless little-endian, k = j*VN + i, JS space)")]
        public CanonAsset.Layer heights;
        public CanonAsset.Layer @base;
        public CanonAsset.Layer surface;
        public CanonAsset.Layer wear;
        public CanonAsset.Layer roadd;
        public CanonAsset.Layer mud;
        public CanonAsset.Layer tint;

        [Header("Water (water.json)")]
        public Reach[] reaches = Array.Empty<Reach>();
        public float weirCrest, lockLevel, lockWallTop;
        /// <summary>The water surfaces: one mesh per reach, the lock chamber and the sheet over the weir (sub-assets).</summary>
        public Mesh[] waterMeshes = Array.Empty<Mesh>();

        [Header("Structures (structures.json)")]
        public Deck[] decks = Array.Empty<Deck>();

        [Header("Buildings (buildings.json + lane A's index)")]
        public Building[] buildings = Array.Empty<Building>();
        /// <summary>Lane A's palette atlas material (M_Bld_Atlas): the grey boxes' walls and roofs sit on its swatches.</summary>
        public Material buildingMaterial;

        [Header("Nature and places")]
        public VegetationSet vegetation;
        /// <summary>The canon's tree models (the same eight species and the reeds).</summary>
        public CanonModelSet trees;
        public WorldInfo.Poi[] pois = Array.Empty<WorldInfo.Poi>();
        /// <summary>The garage's bays (JS space, as PlacementSet has them).</summary>
        public PlacementSet.Parking[] parking = Array.Empty<PlacementSet.Parking>();
        /// <summary>The static colliders (JS space, PlacementSet's form): a box per building, a circle per tree.</summary>
        public PlacementSet.Collider[] colliders = Array.Empty<PlacementSet.Collider>();
        public Bookmark[] bookmarks = Array.Empty<Bookmark>();

        [Header("Look")]
        public Texture2D map;
        public Texture2D detail, waterNormal;

        [Header("Roads (lane R)")]
        /// <summary>Lane R's RoadChunkSet of this town (typed where the Roads assembly is referenced).</summary>
        public ScriptableObject roadChunks;

        public CanonAsset.Layer[] Layers => new[] { heights, @base, surface, wear, roadd, mud, tint };

        public bool TryGetPoi(string id, out WorldInfo.Poi poi)
        {
            foreach (var p in pois) if (p.id == id) { poi = p; return true; }
            poi = default;
            return false;
        }
    }
}
