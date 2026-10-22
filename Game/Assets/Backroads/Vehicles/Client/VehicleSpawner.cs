using System.Collections;
using System.Collections.Generic;
using Backroads.Core;
using Backroads.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Backroads.Vehicles.Client
{
    /// <summary>
    /// Puts the four M2 vehicles on the Millbrook garage bays when the canon test world (CanonTestWorld.unity, the old
    /// Millbrook scene, renamed in T3.1) loads (built in code; the scene file is lane W's): pickup, hatch, rally and truck
    /// on parking bays 0-3 in VEHICLE_ORDER (the dozer's bay 4 stays
    /// empty until M7), each settled for 240 steps before it is shown, and the player in the truck
    /// (PlayerVehicle.Current). E (get in/out) at standstill moves the player into the nearest vehicle within 12 m.
    /// -br-no-vehicles leaves them out.
    /// </summary>
    public sealed class VehicleSpawner : MonoBehaviour
    {
        public const string SceneName = "CanonTestWorld";
        public const string NoVehiclesFlag = "-br-no-vehicles";
        public const string StartVehicle = "truck";

        public static VehicleSpawner Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            var s = SceneManager.GetActiveScene();
            if (s.name == SceneName) Create(s);
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == SceneName) Create(scene);
        }

        /// <summary>Creates the spawner in a scene (once).</summary>
        public static VehicleSpawner Create(Scene scene)
        {
            if (CommandLine.Has(NoVehiclesFlag)) return null;
            if (Current != null && Current.gameObject.scene == scene) return Current;
            var go = new GameObject("Vehicles");
            SceneManager.MoveGameObjectToScene(go, scene);
            return go.AddComponent<VehicleSpawner>();
        }

        readonly Dictionary<SimVehicle, VehicleView> _views = new Dictionary<SimVehicle, VehicleView>();

        public VehicleSimSystem SimSystem { get; private set; }
        public bool Ready { get; private set; }
        public string Error { get; private set; }
        public IReadOnlyDictionary<SimVehicle, VehicleView> Views => _views;

        public VehicleView ViewOf(SimVehicle v) => v != null && _views.TryGetValue(v, out var view) ? view : null;

        void Awake() => Current = this;

        IEnumerator Start()
        {
            // MillbrookWorld loads the canon in its Start; CanonRuntime.Grid loads it here otherwise
            yield return null;
            if (!CanonRuntime.IsAvailable) { Error = CanonRuntime.NotImportedMessage; Log.Error("vehicles: " + Error); yield break; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                SimSystem = VehicleSimSystem.Create(gameObject);
                var models = VehicleModelSet.Load();
                if (models == null) Log.Warn($"vehicles: {VehicleModelSet.ResourceName} missing (run Backroads > Vehicles > Build models)");
                var parking = CanonRuntime.Asset.placements.parking;
                foreach (var id in ClassicVehicleDefs.M2)
                {
                    int bay = System.Array.IndexOf(ClassicVehicleDefs.Order, id);
                    if (parking == null || bay >= parking.Length) { Log.Error($"vehicles: no parking bay {bay} for {id}"); continue; }
                    var spot = parking[bay];
                    var sim = SimSystem.SpawnAtJs(ClassicVehicleDefs.Get(id), spot.x, spot.z, spot.yaw);
                    _views[sim] = VehicleView.Create(sim, models, transform);
                }
                SimSystem.PlayerChanged += (_, now) => PlayerVehicle.Current = ViewOf(now);
                var falling = gameObject.AddComponent<FallingTrees>();
                SimSystem.TreeKnocked += falling.Knock;
                var start = SimSystem.Find(StartVehicle) ?? (SimSystem.Vehicles.Count > 0 ? SimSystem.Vehicles[0] : null);
                if (start != null)
                {
                    SimSystem.SetPlayer(start);
                    PlayerVehicle.Current = ViewOf(start);
                }
                Ready = true;
                Log.Info($"vehicles: {SimSystem.Vehicles.Count} on the Millbrook garage bays, settled in {sw.Elapsed.TotalMilliseconds:F0} ms; driving the {start?.DisplayName}");
            }
            catch (System.Exception e)
            {
                Error = e.Message;
                Debug.LogException(e);
            }
        }

        void OnDestroy()
        {
            if (Current == this) Current = null;
            foreach (var v in _views.Values)
                if (v != null && ReferenceEquals(PlayerVehicle.Current, v)) PlayerVehicle.Current = null;
        }
    }
}
