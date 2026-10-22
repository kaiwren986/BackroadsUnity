using System;
using System.Collections.Generic;
using Backroads.Core;
using UnityEngine;

namespace Backroads.World
{
    /// <summary>
    /// T3.3: loads an imported baked world at run time (like CanonRuntime for the canon test world): the WorldDataAsset
    /// from Resources, then its WorldGrid (TextAsset.GetData views copied into persistent NativeArrays) and its static
    /// ColliderGrid. One world at a time; statics reset on SubsystemRegistration.
    /// </summary>
    public static class WorldDataRuntime
    {
        static readonly Dictionary<string, WorldDataAsset> s_Assets = new Dictionary<string, WorldDataAsset>();
        static WorldDataAsset s_Loaded;
        static WorldGrid s_Grid;
        static ColliderGrid s_Colliders;
        static bool s_QuitHooked;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Unload();

        public static string NotImportedMessage(string worldId) =>
            $"world '{worldId}' is not imported: run Backroads > World > Import WorldData (or -executeMethod Backroads.World.Bake.WorldDataImporter.Run)";

        /// <summary>The imported world, or null when the importer has not run.</summary>
        public static WorldDataAsset Asset(string worldId)
        {
            if (!s_Assets.TryGetValue(worldId, out var a) || a == null)
                s_Assets[worldId] = a = Resources.Load<WorldDataAsset>(WorldDataAsset.ResourceName(worldId));
            return a;
        }

        public static WorldDataAsset Loaded => s_Loaded;
        public static bool IsLoaded => s_Grid != null && s_Grid.IsCreated;
        public static WorldGrid Grid => s_Grid;

        public static ColliderGrid Colliders
        {
            get
            {
                if (s_Colliders == null && s_Loaded != null) s_Colliders = ColliderGrid.Build(s_Loaded.colliders, s_Loaded.layout);
                return s_Colliders;
            }
        }

        /// <summary>Loads the world's grid (idempotent for the same world). Returns the load time in milliseconds.</summary>
        public static double Load(WorldDataAsset a)
        {
            if (a == null) throw new ArgumentNullException(nameof(a));
            if (IsLoaded && s_Loaded == a) return 0;
            Unload();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var l in a.Layers) if (l.data == null) throw new InvalidOperationException("WorldData layer missing: " + l.path);
            s_Grid = WorldGrid.FromLayers(a.layout, a.heights.data.GetData<float>(), a.@base.data.GetData<float>(), a.surface.data.GetData<byte>(),
                                          a.wear.data.GetData<byte>(), a.roadd.data.GetData<byte>(), a.mud.data.GetData<byte>(), a.tint.data.GetData<float>());
            s_Loaded = a;
            if (!s_QuitHooked)
            {
                Application.quitting += Unload;
#if UNITY_EDITOR
                UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Unload;
                UnityEditor.EditorApplication.quitting += Unload;
#endif
                s_QuitHooked = true;
            }
            double ms = sw.Elapsed.TotalMilliseconds;
            Log.Info($"world {a.worldId} loaded: WorldGrid {a.layout.VN}x{a.layout.VN} in {ms:F0} ms (manifest {Short(a.manifestSha256)})");
            return ms;
        }

        static string Short(string s) => string.IsNullOrEmpty(s) ? "?" : s.Substring(0, Math.Min(12, s.Length));

        public static void Unload()
        {
            if (s_Grid != null) ActiveWorld.Clear(s_Grid);
            s_Grid?.Dispose();
            s_Grid = null;
            s_Colliders?.Dispose();
            s_Colliders = null;
            s_Loaded = null;
            s_Assets.Clear();
        }
    }

    /// <summary>
    /// T3.3: the world the game is in now (the canon test world or a baked world), for the code that only needs "the
    /// ground": ground queries, cameras, vehicles (lane V: VehicleSimSystem and VehicleSpawner can bind here instead of
    /// CanonRuntime). The scene's world root sets it when it has loaded and clears it on destroy. When nothing is set,
    /// TryGrid falls back to the canon if it is loaded.
    /// </summary>
    public static class ActiveWorld
    {
        public static string Name { get; private set; }
        public static WorldGrid Grid { get; private set; }
        public static ColliderGrid Colliders { get; private set; }
        public static VegetationSet Vegetation { get; private set; }
        /// <summary>The garage's parking bays (JS space, PlacementSet form) and the world's places (JS space, WorldInfo form).</summary>
        public static PlacementSet.Parking[] Parking { get; private set; } = Array.Empty<PlacementSet.Parking>();
        public static WorldInfo.Poi[] Pois { get; private set; } = Array.Empty<WorldInfo.Poi>();

        /// <summary>Raised after Set and Clear.</summary>
        public static event Action Changed;

        public static bool IsSet => Grid != null && Grid.IsCreated;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Name = null; Grid = null; Colliders = null; Vegetation = null;
            Parking = Array.Empty<PlacementSet.Parking>(); Pois = Array.Empty<WorldInfo.Poi>();
            Changed = null;
        }

        public static void Set(string name, WorldGrid grid, ColliderGrid colliders, VegetationSet vegetation,
                               PlacementSet.Parking[] parking, WorldInfo.Poi[] pois)
        {
            Name = name; Grid = grid; Colliders = colliders; Vegetation = vegetation;
            Parking = parking ?? Array.Empty<PlacementSet.Parking>();
            Pois = pois ?? Array.Empty<WorldInfo.Poi>();
            Changed?.Invoke();
        }

        /// <summary>Clears the active world if it is this grid.</summary>
        public static void Clear(WorldGrid grid)
        {
            if (grid == null || !ReferenceEquals(Grid, grid)) return;
            Name = null; Grid = null; Colliders = null; Vegetation = null;
            Parking = Array.Empty<PlacementSet.Parking>(); Pois = Array.Empty<WorldInfo.Poi>();
            Changed?.Invoke();
        }

        /// <summary>The ground to query: the active world, else the canon when it is loaded.</summary>
        public static bool TryGrid(out WorldGrid grid)
        {
            if (IsSet) { grid = Grid; return true; }
            if (CanonRuntime.IsLoaded) { grid = CanonRuntime.Grid; return true; }
            grid = null;
            return false;
        }
    }
}
