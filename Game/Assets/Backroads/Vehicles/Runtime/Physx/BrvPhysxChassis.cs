using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Vehicles
{
    /// <summary>
    /// The PhysX chassis of a <see cref="BrvFleet"/> (card T3.6, research 06 V1, PLAN 6.1 M3): every vehicle, V0 and v2
    /// alike, gets a dynamic Rigidbody without gravity (BRV applies gravity in its own step), a hull box on layer
    /// VehicleHull (the JS box, generated until the FBX bodies bring their UCX_ hulls) and a sphere per wheel on layer
    /// Wheel (06 section 3.5 item 7). PhysX resolves the vehicles against the static colliders and against each other;
    /// the ground stays BRV's (the hull and the wheels never touch Road or TerrainProxy).
    /// <para>
    /// Per fixed step: BRV steps the fleet and integrates in doubles, as always; <see cref="BeforeSimulate"/> puts each
    /// body at its pose from before BRV's integration, with BRV's new velocities; PhysX simulates; then
    /// <see cref="AfterSimulate"/> compares the velocities. Without a contact PhysX leaves the linear velocity bit for bit
    /// as it was (no gravity, no damping; the angular velocity only picks up float noise) and BRV's own result stands, so
    /// a vehicle that touches nothing moves exactly as before.
    /// After a contact BRV takes PhysX's pose and velocities (and reports the impulse as the vehicle's impact).
    /// </para>
    /// </summary>
    public sealed class BrvPhysxChassis : IDisposable
    {
        public const string LayerVehicle = "Vehicle", LayerHull = "VehicleHull", LayerWheel = "Wheel", LayerStatic = "Static";

        sealed class Body
        {
            public GameObject go;
            public Rigidbody rb;
            public BoxCollider hull;
            public SphereCollider[] wheels;
            public Vector3 vSet, wSet;
        }

        readonly BrvFleet _fleet;
        readonly Transform _root;
        readonly List<Body> _bodies = new List<Body>();
        readonly PhysicsMaterial _hullMaterial;
        bool _written;

        /// <summary>Steps in which some vehicle took a PhysX contact, and the vehicles that did in the last step.</summary>
        public long ContactSteps { get; private set; }
        public int ContactsLastStep { get; private set; }
        public long Adoptions { get; private set; }
        public Transform Root => _root;

        public BrvPhysxChassis(BrvFleet fleet, Transform parent)
        {
            _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            _fleet.PhysxCollisions = true;
            _root = new GameObject("VehicleChassis").transform;
            if (parent != null) _root.SetParent(parent, false);
            // the browser game's wall response: restitution 0.2, sliding friction 0.3
            _hullMaterial = new PhysicsMaterial("BrvHull")
            {
                bounciness = 0.2f, dynamicFriction = 0.3f, staticFriction = 0.3f,
                bounceCombine = PhysicsMaterialCombine.Maximum, frictionCombine = PhysicsMaterialCombine.Average,
            };
            Sync();
        }

        public static int Layer(string name)
        {
            int l = LayerMask.NameToLayer(name);
            return l >= 0 ? l : 0;
        }

        public int Count => _bodies.Count;
        public Rigidbody RigidbodyOf(int i) => _bodies[i].rb;
        public BoxCollider HullOf(int i) => _bodies[i].hull;
        public SphereCollider WheelOf(int i, int w) => _bodies[i].wheels[w];

        /// <summary>Creates the bodies of vehicles added to the fleet since the last call.</summary>
        public void Sync()
        {
            while (_bodies.Count < _fleet.Count) _bodies.Add(Create(_bodies.Count));
        }

        Body Create(int i)
        {
            ref readonly var d = ref _fleet.Def(i);
            ref var s = ref _fleet.State(i);
            var go = new GameObject($"Chassis {i} {_fleet.Classic(i).id}") { layer = Layer(LayerVehicle) };
            go.transform.SetParent(_root, false);
            go.AddComponent<BrvChassisBody>().Index = i;
            var rb = go.AddComponent<Rigidbody>();
            rb.useGravity = false;                       // BRV applies gravity inside its step (06 section 3.4, risk 8)
            rb.linearDamping = 0;
            rb.angularDamping = 0;
            rb.maxAngularVelocity = 50;
            rb.maxDepenetrationVelocity = 3;
            rb.sleepThreshold = 0;
            rb.interpolation = RigidbodyInterpolation.None;      // VehicleView interpolates BRV's poses
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.excludeLayers = (1 << Layer("Road")) | (1 << Layer("TerrainProxy")) | (1 << Layer("Trigger")) | (1 << Layer("Water"));
            SetMass(rb, s);
            rb.position = BrvFleet.UnityPos(s.pos);
            rb.rotation = BrvFleet.UnityRot(s.quat);

            var hullGo = new GameObject("UCX_Hull") { layer = Layer(LayerHull) };
            hullGo.transform.SetParent(go.transform, false);
            var hull = hullGo.AddComponent<BoxCollider>();
            hull.center = new Vector3((float)-d.bcx, (float)d.bcy, (float)d.bcz);
            hull.size = new Vector3((float)(2 * d.bhw), (float)(2 * d.bhh), (float)(2 * d.bhl));
            hull.sharedMaterial = _hullMaterial;

            var wheels = new SphereCollider[d.wheels.Length];
            for (int w = 0; w < wheels.Length; w++)
            {
                var wg = new GameObject($"Wheel {w}") { layer = Layer(LayerWheel) };
                wg.transform.SetParent(go.transform, false);
                var sc = wg.AddComponent<SphereCollider>();
                sc.radius = (float)d.wheels[w].r;
                sc.sharedMaterial = _hullMaterial;
                wheels[w] = sc;
            }
            var b = new Body { go = go, rb = rb, hull = hull, wheels = wheels };
            PlaceWheels(b, d, s);
            return b;
        }

        static void SetMass(Rigidbody rb, in BrvState s)
        {
            rb.mass = (float)s.mass;
            rb.centerOfMass = Vector3.zero;            // the body's origin is the COM, as in BRV
            rb.inertiaTensor = new Vector3((float)(1 / s.iI.x), (float)(1 / s.iI.y), (float)(1 / s.iI.z));
            rb.inertiaTensorRotation = Quaternion.identity;
        }

        /// <summary>Wheel spheres at the hubs: the suspension mount minus the current suspension length along the body's up.</summary>
        static void PlaceWheels(Body b, in BrvDef d, in BrvState s)
        {
            for (int w = 0; w < b.wheels.Length; w++)
            {
                var l = d.wheels[w];
                double len = w < s.wheels.Length ? s.wheels[w].len : l.rest;
                b.wheels[w].center = new Vector3((float)-l.mx, (float)(l.my - len), (float)l.mz);
            }
        }

        /// <summary>After the fleet's step, before PhysX simulates: each body at its pre-integration pose with BRV's velocities.</summary>
        public void BeforeSimulate()
        {
            Sync();
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                ref var s = ref _fleet.State(i);
                ref readonly var d = ref _fleet.Def(i);
                if (b.rb.mass != (float)s.mass) SetMass(b.rb, s);
                bool still = s.asleep || s.steps == 0;
                b.rb.position = BrvFleet.UnityPos(still ? s.pos : s.prePos);
                b.rb.rotation = BrvFleet.UnityRot(still ? s.quat : s.preQuat);
                b.vSet = still ? Vector3.zero : BrvFleet.UnityDir(s.vel);
                b.wSet = still ? Vector3.zero : UnityAngVel(s.angVel);
                b.rb.linearVelocity = b.vSet;
                b.rb.angularVelocity = b.wSet;
                PlaceWheels(b, d, s);
            }
            _written = true;
        }

        /// <summary>After PhysX simulated: vehicles whose velocities PhysX changed (a contact) take PhysX's pose and velocities.</summary>
        public void AfterSimulate()
        {
            if (!_written) return;
            _written = false;
            int n = 0;
            for (int i = 0; i < _bodies.Count; i++)
            {
                var b = _bodies[i];
                Vector3 v = b.rb.linearVelocity, w = b.rb.angularVelocity;
                // between steps the bodies idle (a paused simulation must not drift them)
                b.rb.linearVelocity = Vector3.zero;
                b.rb.angularVelocity = Vector3.zero;
                // a contact impulse always changes the linear velocity, which PhysX otherwise returns bit for bit; the
                // angular velocity comes back with float noise (about 1e-7 relative) even when nothing touched
                if (Same(v, b.vSet) && (w - b.wSet).sqrMagnitude <= 1e-8f * Mathf.Max(1f, b.wSet.sqrMagnitude)) continue;
                ref var s = ref _fleet.State(i);
                double3 vel = JsDir(v);
                double3 dv = vel - s.vel;
                s.pos = JsPos(b.rb.position);
                s.quat = JsRot(b.rb.rotation);
                s.vel = vel;
                s.angVel = JsAngVel(w);
                s.speed = math.length(s.vel);
                BrvSim.UpdateBasis(ref s);
                double j = s.mass * math.length(dv);
                if (j > s.impact)
                {
                    s.impact = j;
                    double l = math.length(dv);
                    if (l > 0) s.impactDir = dv / l;
                }
                if (s.asleep) BrvSim.Wake(ref s);
                n++;
            }
            ContactsLastStep = n;
            if (n > 0) { ContactSteps++; Adoptions += n; }
        }

        /// <summary>One fixed step of the fleet with PhysX in a given physics scene (tests and scripted runs).</summary>
        public void Step(PhysicsScene scene)
        {
            _fleet.Step();
            BeforeSimulate();
            scene.Simulate((float)BrvSim.STEP);
            AfterSimulate();
        }

        // exact component comparison (Vector3 == is approximate)
        static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

        /// <summary>Angular velocity is an axial vector: the x mirror between JS and Unity flips its y and z.</summary>
        public static Vector3 UnityAngVel(double3 w) => new Vector3((float)w.x, (float)-w.y, (float)-w.z);
        public static double3 JsAngVel(Vector3 w) => new double3(w.x, -w.y, -w.z);
        public static double3 JsPos(Vector3 p) => new double3(-p.x, p.y, p.z);
        public static double3 JsDir(Vector3 v) => new double3(-v.x, v.y, v.z);
        public static double4 JsRot(Quaternion q) => new double4(q.x, -q.y, -q.z, q.w);

        /// <summary>
        /// Editor validation (card T3.6): every vehicle chassis Rigidbody under <paramref name="root"/> (marked
        /// <see cref="BrvChassisBody"/>, or on the Vehicle or VehicleHull layer) must have gravity off. Returns the problems.
        /// </summary>
        public static List<string> Validate(GameObject root)
        {
            var bad = new List<string>();
            if (root == null) return bad;
            int vehicle = LayerMask.NameToLayer(LayerVehicle), hull = LayerMask.NameToLayer(LayerHull);
            foreach (var rb in root.GetComponentsInChildren<Rigidbody>(true))
            {
                bool isVehicle = rb.GetComponent<BrvChassisBody>() != null || (vehicle >= 0 && rb.gameObject.layer == vehicle) || (hull >= 0 && rb.gameObject.layer == hull);
                if (isVehicle && rb.useGravity) bad.Add($"{Path(rb.transform)}: vehicle Rigidbody with gravity on (BRV applies gravity itself)");
            }
            return bad;
        }

        static string Path(Transform t) => t.parent == null ? t.name : Path(t.parent) + "/" + t.name;

        public void Dispose()
        {
            _fleet.PhysxCollisions = false;
            if (_root != null) DestroyNow(_root.gameObject);
            if (_hullMaterial != null) DestroyNow(_hullMaterial);
            _bodies.Clear();
        }

        internal static void DestroyNow(UnityEngine.Object o)
        {
            if (o == null) return;
            // Destroy waits for the end of the frame: deactivate first so the bodies leave the PhysX scene now
            if (o is GameObject go) go.SetActive(false);
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }
    }
}
