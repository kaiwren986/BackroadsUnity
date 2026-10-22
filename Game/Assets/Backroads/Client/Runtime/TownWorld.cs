using System;
using System.Collections;
using System.Diagnostics;
using Backroads.Client.Rendering;
using Backroads.Core;
using Backroads.Deform;
using Backroads.Roads;
using Backroads.World;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace Backroads.Client
{
    /// <summary>
    /// T3.3: the composition root of a baked world's scene (Millbrook.unity; the scene is a thin bootstrap, PLAN 1.2). It
    /// loads the imported WorldData (WorldDataRuntime), then builds the terrain chunks (1,024 of 64 m on the 4,097² grid),
    /// the river (a flat surface per reach, the lock, the sheet over the weir), lane R's road chunks, the buildings (lane
    /// A's models where they exist, grey boxes elsewhere, with colliders), the trees, hedgerows and reeds, the deformation
    /// and the day/night lighting, and makes the world ActiveWorld (ground queries, cameras, lane V's vehicles). The
    /// canon test world keeps its own root (MillbrookWorld).
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class TownWorld : MonoBehaviour
    {
        /// <summary>Lane R's road materials in Roads/Client/Resources (RoadChunkView's names).</summary>
        public static readonly string[] RoadMaterials = { "BrRoadAsphalt", "BrRoadConcrete", "BrRoadPaint" };

        [Tooltip("The WorldData world to load (WorldData/(world)/v1, imported by Backroads > World > Import WorldData).")]
        public string world = "millbrook";
        public MillbrookLook look;
        public Camera cam;
        public Light sun;
        public Volume volume;
        public float startHour = 12f;
        [Tooltip("Bookmark to start at (places.json cameras); empty keeps the scene camera.")]
        public string startBookmark = "overview";

        public static TownWorld Current { get; private set; }

        public bool Ready { get; private set; }
        public string Error { get; private set; }
        public WorldDataAsset Data { get; private set; }
        public WorldGrid Grid { get; private set; }
        public TerrainChunkSystem Terrain { get; private set; }
        public VegetationRenderer Vegetation { get; private set; }
        public DayNightPresenter Sky { get; private set; }
        public DeformSystem Deform { get; private set; }
        public Transform WaterRoot { get; private set; }
        public Transform RoadsRoot { get; private set; }
        public Transform BuildingsRoot { get; private set; }
        public int Models { get; private set; }
        public int GreyBoxes { get; private set; }

        /// <summary>Load timings in milliseconds (reported by the perf autotest).</summary>
        public double DataLoadMs, TerrainBuildAllMs, BuildingsMs, VegetationMs, RoadsMs, TotalLoadMs;
        public bool FreezeClock;
        float _shadowDistanceBefore = -1;
        PlacementSet _noLamps;
        readonly System.Collections.Generic.List<Object> _created = new System.Collections.Generic.List<Object>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        void Awake() => Current = this;

        IEnumerator Start()
        {
            var total = Stopwatch.StartNew();
            Data = WorldDataRuntime.Asset(world);
            if (Data == null) { Fail(WorldDataRuntime.NotImportedMessage(world)); yield break; }
            if (cam == null) cam = Camera.main;
            if (cam.GetComponent<VehicleCameraRig>() == null) cam.gameObject.AddComponent<VehicleCameraRig>();
            if (cam.GetComponent<CameraDirector>() == null) cam.gameObject.AddComponent<CameraDirector>();
            var fly = cam.GetComponent<FreeFlyCamera>();
            if (fly != null) fly.extent = Data.layout.Half + 150f;
            if (!string.IsNullOrEmpty(startBookmark)) GoTo(startBookmark);
            try { DataLoadMs = WorldDataRuntime.Load(Data); }
            catch (Exception e) { Fail("WorldData load failed: " + e.Message); yield break; }
            Grid = WorldDataRuntime.Grid;
            if (Data.detail != null) Shader.SetGlobalTexture("_BrDetail", Data.detail);
            var q = QualityPreset.Current;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp && !QualityPreset.Requested)
            {
                _shadowDistanceBefore = urp.shadowDistance;
                urp.shadowDistance = Mathf.Max(urp.shadowDistance, q.ShadowDistance);
            }

            var sw = Stopwatch.StartNew();
            Terrain = new TerrainChunkSystem(Grid, look.terrain, transform) { LodScale = q.TerrainLodScale, BuildsPerFrame = q.ChunkBuildsPerFrame };
            TerrainBuildAllMs = Terrain.BuildAll(cam.transform.position);
            WaterRoot = SpawnWater();
            yield return null;

            sw.Restart();
            RoadsRoot = SpawnRoads();
            RoadsMs = sw.Elapsed.TotalMilliseconds;
            yield return null;

            sw.Restart();
            BuildingsRoot = SpawnBuildings();
            BuildingsMs = sw.Elapsed.TotalMilliseconds;
            yield return null;

            sw.Restart();
            Vegetation = new VegetationRenderer(Data.vegetation, Data.trees, Data.layout, look, transform, q.TreeNearRadius);
            VegetationMs = sw.Elapsed.TotalMilliseconds;
            yield return null;

            Deform = new DeformSystem(Grid);
            StampSinks.Current = Deform;
            _noLamps = ScriptableObject.CreateInstance<PlacementSet>();
            _noLamps.lamps = Array.Empty<PlacementSet.Lamp>();
            _created.Add(_noLamps);
            Sky = new DayNightPresenter(sun, look, _noLamps, transform, volume);
            Sky.SetHour(startHour, cam.transform.position);
            ActiveWorld.Set(world, Grid, WorldDataRuntime.Colliders, Data.vegetation, Data.parking, Data.pois);
            TotalLoadMs = total.Elapsed.TotalMilliseconds;
            Log.Info($"{Data.townName ?? world} ready in {TotalLoadMs:F0} ms: data {DataLoadMs:F0} ms, terrain {Terrain.ChunkCount} chunks {TerrainBuildAllMs:F0} ms, " +
                     $"roads {RoadsMs:F0} ms, {Models} models + {GreyBoxes} grey boxes {BuildingsMs:F0} ms, {Vegetation.Trees} trees + {Vegetation.Rocks} rocks {VegetationMs:F0} ms; quality {q.Tier}");
            Ready = true;
        }

        void Fail(string why)
        {
            Error = why;
            Log.Error("world: " + why);
        }

        Transform SpawnWater()
        {
            var root = new GameObject("Water").transform;
            root.SetParent(transform, false);
            var mat = new Material(look.water) { name = "water" };
            if (Data.waterNormal != null) mat.SetTexture("_NormalMap", Data.waterNormal);
            _created.Add(mat);
            foreach (var m in Data.waterMeshes)
            {
                if (m == null) continue;
                var go = new GameObject(m.name);
                go.transform.SetParent(root, false);
                go.AddComponent<MeshFilter>().sharedMesh = m;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = mat;
                r.shadowCastingMode = ShadowCastingMode.Off;
            }
            return root;
        }

        Transform SpawnRoads()
        {
            if (!(Data.roadChunks is RoadChunkSet set)) { Log.Warn("world: no road chunks (lane R's RoadBakeCli.BakeTowns)"); return null; }
            var mats = new Material[RoadMaterials.Length];
            for (int i = 0; i < mats.Length; i++)
            {
                mats[i] = Resources.Load<Material>(RoadMaterials[i]);
                if (mats[i] == null) { Log.Warn("world: road material missing: " + RoadMaterials[i]); mats = null; break; }
            }
            var go = RoadChunkLoader.Spawn(set, transform, mats, true);
            return go != null ? go.transform : null;
        }

        Transform SpawnBuildings()
        {
            var root = new GameObject("Buildings").transform;
            root.SetParent(transform, false);
            foreach (var b in Data.buildings)
            {
                if (b.model != null)
                {
                    var go = Instantiate(b.model, b.origin, Quaternion.identity, root);
                    go.name = b.osm + ":" + b.part;
                    Models++;
                }
                else if (b.greyBox != null)
                {
                    var go = new GameObject(b.osm + ":" + b.part + " (grey box)");
                    go.transform.SetParent(root, false);
                    go.AddComponent<MeshFilter>().sharedMesh = b.greyBox;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = Data.buildingMaterial;
                    go.AddComponent<MeshCollider>().sharedMesh = b.greyBox;
                    GreyBoxes++;
                }
            }
            return root;
        }

        void Update()
        {
            if (!Ready) return;
            var input = ClientInput.Current;
            if (input == null) return;
            if (input.Pressed("timeForward")) { SetHour((Sky.Hour + 1f) % 24f); Toasts.Show($"{Mathf.FloorToInt(Sky.Hour):00}:00"); }
            if (input.Pressed("timeBack")) { SetHour((Sky.Hour + 23f) % 24f); Toasts.Show($"{Mathf.FloorToInt(Sky.Hour):00}:00"); }
        }

        void LateUpdate()
        {
            if (!Ready || cam == null) return;
            var p = cam.transform.position;
            FlushDeformation();
            Terrain.Update(p);
            Sky.Tick(Time.deltaTime, p, !FreezeClock);
        }

        /// <summary>This frame's changed ground rectangles to the terrain (partial updates) and the trees (re-snap).</summary>
        public void FlushDeformation()
        {
            if (Deform == null) return;
            ColliderGrid colliders = null;
            while (Deform.Dirty.TryDequeue(out var r))
            {
                Terrain.MarkDirty(r.x, r.y, r.z, r.w);
                colliders ??= WorldDataRuntime.Colliders;
                Vegetation.Resnap(r.x, r.y, r.z, r.w, Grid.View, colliders);
            }
            Terrain.FlushDirty();
        }

        public void SetHour(float hour)
        {
            if (Sky != null) Sky.SetHour(hour, cam != null ? cam.transform.position : Vector3.zero);
        }

        /// <summary>The bookmark (places.json cameras) with this id, if any.</summary>
        public bool TryBookmark(string id, out WorldDataAsset.Bookmark b)
        {
            b = default;
            if (Data == null) return false;
            foreach (var m in Data.bookmarks) if (m.id == id) { b = m; return true; }
            return false;
        }

        /// <summary>Puts the camera at a bookmark (looking at its target).</summary>
        public bool GoTo(string id)
        {
            if (cam == null || !TryBookmark(id, out var b)) return false;
            cam.transform.SetPositionAndRotation(b.position, Quaternion.LookRotation(b.target - b.position, Vector3.up));
            if (b.fov > 1) cam.fieldOfView = b.fov;
            var fly = cam.GetComponent<FreeFlyCamera>();
            if (fly != null) fly.SyncFromTransform();
            return true;
        }

        /// <summary>Builds every terrain chunk the camera needs right now (after a teleport); returns milliseconds.</summary>
        public double SettleTerrain() => Terrain != null && cam != null ? Terrain.BuildAll(cam.transform.position) : 0;

        void OnDestroy()
        {
            if (Current == this) Current = null;
            if (_shadowDistanceBefore >= 0 && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp) urp.shadowDistance = _shadowDistanceBefore;
            Terrain?.Dispose();
            Vegetation?.Destroy();
            if (Deform != null) { Deform.Dispose(); Deform = null; }
            foreach (var o in _created) if (o != null) Destroy(o);
            WorldDataRuntime.Unload();
        }
    }
}
