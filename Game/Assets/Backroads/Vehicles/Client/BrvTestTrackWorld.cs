using System.Collections;
using System.Collections.Generic;
using Backroads.Client;
using Backroads.Client.Rendering;
using Backroads.Core;
using Backroads.Deform;
using Backroads.World;
using UnityEngine;

namespace Backroads.Vehicles.Client
{
    /// <summary>
    /// The BrvTestTrack scene's composition root (card T3.5 step 2; the scene is a camera, a sun and this component):
    /// builds the analytic test track (<see cref="BrvTestTrack"/>, its own WorldGridLayout), renders it with lane W's
    /// terrain chunks, keeps ruts in the mud with lane W's deformation, and puts the four M2 vehicles on the pad on
    /// BRV v2 (-br-brv-v0 keeps them on V0 for a comparison), the player in the truck. Drive as in Millbrook: the
    /// camera rig and the input follow PlayerVehicle.Current; E switches vehicles at standstill.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public sealed class BrvTestTrackWorld : MonoBehaviour
    {
        public const string SceneName = "BrvTestTrack";
        public const string V0Flag = "-br-brv-v0";
        public const string StartVehicle = "truck";

        [Tooltip("Lane W's look (the terrain material).")]
        public MillbrookLook look;
        public Camera cam;
        public Light sun;

        public static BrvTestTrackWorld Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        public bool Ready { get; private set; }
        public string Error { get; private set; }
        public WorldGrid Grid { get; private set; }
        public TerrainChunkSystem Terrain { get; private set; }
        public DeformSystem Deform { get; private set; }
        public VehicleSimSystem SimSystem { get; private set; }
        public bool OnV2 { get; private set; }

        readonly Dictionary<SimVehicle, VehicleView> _views = new Dictionary<SimVehicle, VehicleView>();

        public VehicleView ViewOf(SimVehicle v) => v != null && _views.TryGetValue(v, out var view) ? view : null;

        void Awake() => Current = this;

        IEnumerator Start()
        {
            yield return null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (cam == null) cam = Camera.main;
                if (cam != null)
                {
                    if (cam.GetComponent<VehicleCameraRig>() == null) cam.gameObject.AddComponent<VehicleCameraRig>();
                    if (cam.GetComponent<CameraDirector>() == null) cam.gameObject.AddComponent<CameraDirector>();
                }
                Grid = BrvTestTrack.Build();
                if (look != null && look.terrain != null)
                {
                    Terrain = new TerrainChunkSystem(Grid, look.terrain, transform);
                    Terrain.BuildAll(cam != null ? cam.transform.position : Vector3.zero);
                }
                else Log.Warn("BrvTestTrack: no look/terrain material assigned; the track is not drawn");
                Deform = new DeformSystem(Grid);
                StampSinks.Current = Deform;

                OnV2 = !CommandLine.Has(V0Flag);
                bool was = BrvFeatures.V2Driveline;
                BrvFeatures.V2Driveline = OnV2;
                SimSystem = gameObject.AddComponent<VehicleSimSystem>();
                SimSystem.Bind(Grid, null, null);
                var models = VehicleModelSet.Load();
                if (models == null) Log.Warn($"BrvTestTrack: {VehicleModelSet.ResourceName} missing (run Backroads > Vehicles > Build models)");
                try
                {
                    // a row on the south end of the pad, heading north (+z, JS)
                    var ids = ClassicVehicleDefs.M2;
                    for (int k = 0; k < ids.Length; k++)
                    {
                        var sim = SimSystem.SpawnAtJs(ClassicVehicleDefs.Get(ids[k]), BrvTestTrack.PadStart.x - 15 + 10 * k, BrvTestTrack.PadZ0 + 20, 0);
                        _views[sim] = VehicleView.Create(sim, models, transform);
                    }
                }
                finally { BrvFeatures.V2Driveline = was; }
                SimSystem.PlayerChanged += (_, now) => PlayerVehicle.Current = ViewOf(now);
                var start = SimSystem.Find(StartVehicle) ?? (SimSystem.Vehicles.Count > 0 ? SimSystem.Vehicles[0] : null);
                if (start != null)
                {
                    SimSystem.SetPlayer(start);
                    PlayerVehicle.Current = ViewOf(start);
                }
                Ready = true;
                Log.Info($"BrvTestTrack ready in {sw.Elapsed.TotalMilliseconds:F0} ms: track {Grid.Layout}, {(Terrain != null ? Terrain.ChunkCount + " terrain chunks" : "no terrain view")}, " +
                         $"{SimSystem.Vehicles.Count} vehicles on {(OnV2 ? "BRV v2" : "V0")}, driving the {start?.DisplayName}");
            }
            catch (System.Exception e)
            {
                Error = e.Message;
                Debug.LogException(e);
            }
        }

        void LateUpdate()
        {
            if (!Ready) return;
            if (Deform != null && Terrain != null)
            {
                while (Deform.Dirty.TryDequeue(out var r)) Terrain.MarkDirty(r.x, r.y, r.z, r.w);
                Terrain.FlushDirty();
            }
            if (Terrain != null && cam != null) Terrain.Update(cam.transform.position);
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            foreach (var v in _views.Values)
                if (v != null && ReferenceEquals(PlayerVehicle.Current, v)) PlayerVehicle.Current = null;
            if (ReferenceEquals(StampSinks.Current, Deform)) StampSinks.Current = null;
            Terrain?.Dispose();
            Deform?.Dispose();
            // the fleet (VehicleSimSystem, on this object) never disposes the grid it was bound to: the grid is ours
            Grid?.Dispose();
        }
    }
}
