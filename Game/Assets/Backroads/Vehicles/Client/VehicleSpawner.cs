using System.Collections;
using System.Collections.Generic;
using Backroads.Core;
using Backroads.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Backroads.Vehicles.Client
{
    /// <summary>
    /// Puts the four M2 vehicles on the garage bays of the world the game is in (T3.6: <see cref="ActiveWorld"/>, set by
    /// the scene's world root once it is built: the canon test world, CanonTestWorld.unity, and the new Millbrook,
    /// Millbrook.unity): pickup, hatch, rally and truck on <see cref="ActiveWorld.GarageBays"/> 0-3 in VEHICLE_ORDER (the
    /// dozer's bay 4 stays empty until M7), each settled for 240 steps before it is shown, and the player in the truck
    /// (PlayerVehicle.Current). E (get in/out) at standstill moves the player into the nearest vehicle within 12 m.
    /// Built in code (the scene files are lane W's). The spawner goes when its world is cleared or replaced. A world
    /// without a garage, or a scene that runs its own vehicles (the BRV test track), gets none. -br-no-vehicles leaves
    /// them out.
    /// </summary>
    public sealed class VehicleSpawner : MonoBehaviour
    {
        public const string NoVehiclesFlag = "-br-no-vehicles";
        public const string StartVehicle = "truck";

        public static VehicleSpawner Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Current = null;
            ActiveWorld.Changed -= OnWorldChanged;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            ActiveWorld.Changed -= OnWorldChanged;
            ActiveWorld.Changed += OnWorldChanged;
            OnWorldChanged();
        }

        static void OnWorldChanged()
        {
            // the world went away or another one came: this spawner's vehicles belong to the old one
            GameObject old = null;
            if (Current != null && (!ActiveWorld.IsSet || !ReferenceEquals(Current.World, ActiveWorld.Grid)))
            {
                old = Current.gameObject;
                old.SetActive(false);
                Destroy(old);
                Current = null;
            }
            if (!ActiveWorld.IsSet || Current != null) return;
            if (CommandLine.Has(NoVehiclesFlag)) return;
            if (ActiveWorld.GarageBays.Length < ClassicVehicleDefs.M2.Length) return;   // no garage in this world
            var running = VehicleSimSystem.Current;
            if (running != null && running.gameObject != old) return;                    // the scene runs its own vehicles
            Create(SceneManager.GetActiveScene());
        }

        /// <summary>Creates the spawner for the active world in a scene (once per world).</summary>
        public static VehicleSpawner Create(Scene scene)
        {
            if (CommandLine.Has(NoVehiclesFlag) || !ActiveWorld.IsSet) return null;
            if (Current != null && ReferenceEquals(Current.World, ActiveWorld.Grid)) return Current;
            var go = new GameObject("Vehicles");
            if (scene.IsValid()) SceneManager.MoveGameObjectToScene(go, scene);
            var sp = go.AddComponent<VehicleSpawner>();
            sp.World = ActiveWorld.Grid;
            return sp;
        }

        readonly Dictionary<SimVehicle, VehicleView> _views = new Dictionary<SimVehicle, VehicleView>();

        /// <summary>The grid of the world these vehicles were spawned in.</summary>
        public WorldGrid World { get; private set; }
        public string WorldName { get; private set; }
        public VehicleSimSystem SimSystem { get; private set; }
        public bool Ready { get; private set; }
        public string Error { get; private set; }
        public IReadOnlyDictionary<SimVehicle, VehicleView> Views => _views;

        public VehicleView ViewOf(SimVehicle v) => v != null && _views.TryGetValue(v, out var view) ? view : null;

        void Awake() => Current = this;

        IEnumerator Start()
        {
            yield return null;
            if (!ActiveWorld.IsSet || !ReferenceEquals(World, ActiveWorld.Grid)) { Error = "the world changed before the vehicles were spawned"; yield break; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                WorldName = ActiveWorld.Name;
                SimSystem = VehicleSimSystem.Create(gameObject);
                var models = VehicleModelSet.Load();
                if (models == null) Log.Warn($"vehicles: {VehicleModelSet.ResourceName} missing (run Backroads > Vehicles > Build models)");
                var bays = ActiveWorld.GarageBays;
                foreach (var id in ClassicVehicleDefs.M2)
                {
                    int bay = System.Array.IndexOf(ClassicVehicleDefs.Order, id);
                    if (bays == null || bay >= bays.Length) { Log.Error($"vehicles: no garage bay {bay} for {id} in {WorldName}"); continue; }
                    var spot = bays[bay];
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
                Log.Info($"vehicles: {SimSystem.Vehicles.Count} on the {WorldName} garage bays, settled in {sw.Elapsed.TotalMilliseconds:F0} ms; " +
                         $"{(SimSystem.Statics != null ? SimSystem.Statics.Count + " PhysX statics" : "no PhysX statics")}; driving the {start?.DisplayName}");
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
