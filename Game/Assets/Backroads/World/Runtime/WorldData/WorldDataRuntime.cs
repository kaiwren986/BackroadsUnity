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
    /// T3.3: the world the game is in now, the one place that code needing "the world" binds to (ground queries, cameras,
    /// lane V's VehicleSpawner and VehicleSimSystem from T3.6), the same in both worlds.
    /// <para>Who sets it: the scene's world root, once everything is built: <c>MillbrookWorld</c> (CanonTestWorld.unity,
    /// Name "canon", from CanonRuntime and the canon asset) and <c>TownWorld</c> (Millbrook.unity, Name "millbrook", from
    /// WorldDataRuntime and the imported WorldData). Any other scene with its own grid (a test track) may call Set too.</para>
    /// <para>Lifecycle: empty at start (statics reset on SubsystemRegistration); <c>Set</c> when the root is ready, then
    /// <c>Changed</c>; <c>Clear(grid)</c> when that world's grid is unloaded (CanonRuntime.Unload, WorldDataRuntime.Unload:
    /// the root's OnDestroy, a scene change, quitting), then <c>Changed</c>. Clear ignores a grid that is not the active
    /// one, so unloading the canon after the town is set leaves the town active. A consumer waits for <c>IsSet</c> (or
    /// <c>Changed</c>) and drops its references on the Changed that clears or replaces the world. The arrays and grids
    /// belong to the world root; consumers never dispose them.</para>
    /// <para>Frames: exactly as CanonRuntime's: Grid is queried through WorldGridQueries like CanonRuntime.Grid; Colliders,
    /// Vegetation, GarageBays and Pois are in JS space (x_js = -x_unity), in the canon's placements.json, vegetation.json
    /// and world.json forms.</para>
    /// <para>When nothing is set, TryGrid falls back to the canon if it is loaded (the M2 code paths keep working).</para>
    /// </summary>
    public static class ActiveWorld
    {
        /// <summary>"canon" (the canon test world) or the WorldData world id ("millbrook"); null when nothing is set.</summary>
        public static string Name { get; private set; }
        /// <summary>The world's grid (heights, surfaces, mud, deformation layers), on its WorldGridLayout.</summary>
        public static WorldGrid Grid { get; private set; }
        /// <summary>The static collider grid: a box per building, a circle per tree trunk (JS space, PlacementSet form).</summary>
        public static ColliderGrid Colliders { get; private set; }
        /// <summary>The trees, bushes, reeds and rocks (JS space; a collider's <c>tree</c> indexes <c>Vegetation.trees</c>).</summary>
        public static VegetationSet Vegetation { get; private set; }
        /// <summary>
        /// The garage's spawn bays in VEHICLE_ORDER (<c>ClassicVehicleDefs.Order</c>): bay i is vehicle i's, 0-3 the four M2
        /// vehicles and 4 the dozer's (empty until M7). Five in both worlds: the canon's placements.json parking, and the
        /// new Millbrook's places.json parking (5.6 m apart in front of the garage, facing it). JS space, PlacementSet
        /// form (x, z, yaw with yaw_js = -yaw_unity), so <c>SpawnAtJs(def, bay.x, bay.z, bay.yaw)</c> takes them as they are.
        /// </summary>
        public static PlacementSet.Parking[] GarageBays { get; private set; } = Array.Empty<PlacementSet.Parking>();
        /// <summary>
        /// The world's places (world.json pois form, JS space). The new Millbrook has garage, mill, fuel, landing, farm, town,
        /// quarry, depot and bog; the canon has town, depot, farm, quarry, mill, harbor and fuel (no "garage": its garage is
        /// where its bays are).
        /// </summary>
        public static WorldInfo.Poi[] Pois { get; private set; } = Array.Empty<WorldInfo.Poi>();

        /// <summary>Raised after Set and after a Clear that cleared.</summary>
        public static event Action Changed;

        public static bool IsSet => Grid != null && Grid.IsCreated;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Name = null; Grid = null; Colliders = null; Vegetation = null;
            GarageBays = Array.Empty<PlacementSet.Parking>(); Pois = Array.Empty<WorldInfo.Poi>();
            Changed = null;
        }

        public static void Set(string name, WorldGrid grid, ColliderGrid colliders, VegetationSet vegetation,
                               PlacementSet.Parking[] garageBays, WorldInfo.Poi[] pois)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            Name = name; Grid = grid; Colliders = colliders; Vegetation = vegetation;
            GarageBays = garageBays ?? Array.Empty<PlacementSet.Parking>();
            Pois = pois ?? Array.Empty<WorldInfo.Poi>();
            Changed?.Invoke();
        }

        /// <summary>Clears the active world if it is this grid (else does nothing).</summary>
        public static void Clear(WorldGrid grid)
        {
            if (grid == null || !ReferenceEquals(Grid, grid)) return;
            Name = null; Grid = null; Colliders = null; Vegetation = null;
            GarageBays = Array.Empty<PlacementSet.Parking>(); Pois = Array.Empty<WorldInfo.Poi>();
            Changed?.Invoke();
        }

        /// <summary>The place with this id (for example "fuel"), if the world has it.</summary>
        public static bool TryGetPoi(string id, out WorldInfo.Poi poi)
        {
            foreach (var p in Pois) if (p.id == id) { poi = p; return true; }
            poi = default;
            return false;
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
