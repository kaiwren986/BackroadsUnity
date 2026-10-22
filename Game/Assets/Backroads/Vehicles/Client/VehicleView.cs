using System.Collections.Generic;
using Backroads.Core;
using UnityEngine;

namespace Backroads.Vehicles.Client
{
    /// <summary>
    /// The presentation of one BRV vehicle (vehicleModels.js buildFromAsset + Vehicle.sync): the body interpolated
    /// between the last two fixed steps (03 section 4.14 phase 1), wheels on their suspension, steering and spinning,
    /// lights (L: head, tail, brake, reverse, indicators; headlight spots on the player's vehicle) and the horn (H).
    /// Implements IDrivenVehicle for input, cameras and HUD (lane W). Model space as in the JS, x flipped by glTFast.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public sealed class VehicleView : MonoBehaviour, IDrivenVehicle
    {
        public SimVehicle Sim { get; private set; }
        public Transform BodyGroup { get; private set; }
        public LODGroup Lod { get; private set; }
        Transform _hood, _exhaust;
        readonly List<Transform> _pivots = new List<Transform>();
        readonly List<Transform> _spins = new List<Transform>();
        readonly Dictionary<string, Material> _mats = new Dictionary<string, Material>();
        Material _head, _tail, _rev, _amber;
        Light[] _headlights;
        AudioSource _horn;
        int _lightState = -1;

        static readonly Color HeadC = Hex(0xfff4dc), TailC = Hex(0xff2a1a), RevC = Hex(0xffffff), AmberC = Hex(0xff9a1f);

        public string DisplayName => Sim.DisplayName;
        public Transform Body => transform;
        public Transform HoodAnchor => _hood;
        public Transform Exhaust => _exhaust;
        public Vector3 Velocity => Sim.Velocity;
        public VehicleHud Hud => Sim.Hud;
        public void SetInput(in DriveInput input) => Sim.SetInput(input);

        static Color Hex(int rgb) => new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f);

        public static VehicleView Create(SimVehicle sim, VehicleModelSet models, Transform parent)
        {
            var go = new GameObject("Vehicle " + sim.Def.id);
            go.transform.SetParent(parent, false);
            var v = go.AddComponent<VehicleView>();
            v.Build(sim, models);
            return v;
        }

        void Build(SimVehicle sim, VehicleModelSet set)
        {
            Sim = sim;
            var def = sim.Def;
            var brv = sim.Brv;
            BodyGroup = new GameObject("Body").transform;
            BodyGroup.SetParent(transform, false);
            BodyGroup.localPosition = new Vector3(0, (float)-def.comY, (float)-def.comZ);

            var model = set != null ? set.Find(def.id) : null;
            var lod0 = new List<Renderer>();
            var lod1 = new List<Renderer>();
            if (model == null) Log.Warn($"vehicle {def.id}: no model in {VehicleModelSet.ResourceName}; drawing a box");
            if (model != null)
            {
                lod0.Add(AddPart(BodyGroup, "Body", model.body, model, set, false));
                if (model.bodyLod1?.mesh != null) lod1.Add(AddPart(BodyGroup, "Body_LOD1", model.bodyLod1, model, set, false));
            }
            else lod0.Add(AddBox(BodyGroup, def));

            // wheels: pivot at (x, r, z) in model space (JS +x left = Unity -x), spin inside, mirrored on the right
            for (int i = 0; i < brv.wheels.Length; i++)
            {
                var l = brv.wheels[i];
                var pivot = new GameObject("Wheel" + i).transform;
                pivot.SetParent(BodyGroup, false);
                pivot.localPosition = new Vector3((float)-l.x, (float)def.wheelR, (float)l.z);
                var spin = new GameObject("Spin").transform;
                spin.SetParent(pivot, false);
                int side = l.x >= 0 ? 1 : -1;
                if (model?.wheel?.mesh != null)
                {
                    lod0.Add(AddWheel(spin, "Wheel", model.wheel, model, set, side, 0));
                    if (model.wheelLod1?.mesh != null) lod1.Add(AddWheel(spin, "Wheel_LOD1", model.wheelLod1, model, set, side, 0));
                    if (def.wheelDual && !l.front)
                    {
                        float off = side * (float)(def.wheelW + 0.04);
                        lod0.Add(AddWheel(spin, "Wheel2", model.wheel, model, set, side, off));
                        if (model.wheelLod1?.mesh != null) lod1.Add(AddWheel(spin, "Wheel2_LOD1", model.wheelLod1, model, set, side, off));
                    }
                }
                if (model?.caliper?.mesh != null)
                {
                    var c = AddPart(pivot, "Caliper", model.caliper, model, set, false);
                    c.transform.localScale = new Vector3(side, 1, 1);
                    lod0.Add(c);
                }
                _pivots.Add(pivot);
                _spins.Add(spin);
            }

            if (lod1.Count > 0)
            {
                Lod = gameObject.AddComponent<LODGroup>();
                // vehicleModels.js switches at 40 m: about 10 % of the screen height for a car at 60 degrees fov
                Lod.SetLODs(new[] { new LOD(0.10f, lod0.ToArray()), new LOD(0.004f, lod1.ToArray()) });
                Lod.RecalculateBounds();
            }

            // hood camera (camera.js: seat height + 0.25, 0.2 m ahead of the seat, looking 20 m ahead)
            _hood = new GameObject("HoodAnchor").transform;
            _hood.SetParent(transform, false);
            var hp = new Vector3(0, (float)(def.seatY - def.comY + 0.25), (float)(def.seatZ - def.comZ + 0.2));
            _hood.localPosition = hp;
            _hood.localRotation = Quaternion.LookRotation(new Vector3(0, (float)(def.seatY - def.comY + 0.1), 20) - hp, Vector3.up);

            _exhaust = new GameObject("Exhaust").transform;
            _exhaust.SetParent(BodyGroup, false);
            _exhaust.localPosition = new Vector3((float)-def.exhaustX, (float)def.exhaustY, (float)def.exhaustZ);
            if (def.exhaustUp) _exhaust.localRotation = Quaternion.LookRotation(Vector3.up, Vector3.back);
            else _exhaust.localRotation = Quaternion.LookRotation(Vector3.back);
            sim.ExhaustAnchor = _exhaust;
            if (sim.Engine != null) sim.Engine.AttachTo(_exhaust);

            // headlights (main.js: two spots 0.32 w apart at the front, aimed 25 m ahead and 2.5 m down)
            double fz = def.boxZ + def.boxL / 2 - def.comZ;
            _headlights = new Light[2];
            for (int i = 0; i < 2; i++)
            {
                float sx = (i == 0 ? 1 : -1);
                var lg = new GameObject("Headlight" + i);
                lg.transform.SetParent(transform, false);
                var p = new Vector3(sx * (float)(def.boxW * 0.32), (float)(def.boxY - def.comY - def.boxH * 0.2), (float)(fz - 0.1));
                var t = new Vector3(sx * (float)(def.boxW * 0.3), (float)(-def.comY - 2.5), (float)(fz + 25));
                lg.transform.localPosition = p;
                lg.transform.localRotation = Quaternion.LookRotation(t - p, Vector3.up);
                var light = lg.AddComponent<Light>();
                light.type = LightType.Spot;
                light.spotAngle = 2 * 0.48f * Mathf.Rad2Deg;
                light.innerSpotAngle = light.spotAngle * 0.45f;
                light.range = 90f;
                light.intensity = 40f;
                light.color = new Color(1f, 0.945f, 0.847f);
                light.shadows = LightShadows.None;
                light.enabled = false;
                _headlights[i] = light;
            }

            _horn = gameObject.AddComponent<AudioSource>();
            _horn.clip = HornClip.Get();
            _horn.loop = true;
            _horn.playOnAwake = false;
            _horn.spatialBlend = 1f;
            _horn.minDistance = 4f;
            _horn.maxDistance = 120f;
            _horn.volume = 0.5f;

            SyncPose(1f);
            UpdateLights(true);
        }

        Renderer AddPart(Transform parent, string name, VehicleModelSet.Part part, VehicleModelSet.Model model, VehicleModelSet set, bool wheel)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = part.mesh;
            var mr = go.AddComponent<MeshRenderer>();
            var mats = new Material[part.materials.Length];
            for (int i = 0; i < mats.Length; i++) mats[i] = MaterialFor(part.materials[i], model, set);
            mr.sharedMaterials = mats;
            bool glassOnly = true;
            foreach (var n in part.materials) if (n != "Glass") glassOnly = false;
            mr.shadowCastingMode = glassOnly ? UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
            mr.receiveShadows = true;
            return mr;
        }

        Renderer AddWheel(Transform spin, string name, VehicleModelSet.Part part, VehicleModelSet.Model model, VehicleModelSet set, int side, float offsetX)
        {
            var r = AddPart(spin, name, part, model, set, true);
            r.transform.localScale = new Vector3(side, 1, 1);
            r.transform.localPosition = new Vector3(offsetX, 0, 0);
            return r;
        }

        Renderer AddBox(Transform parent, ClassicVehicleDef def)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var col = go.GetComponent<Collider>();
            col.enabled = false;            // at once: the PhysX chassis must never meet the view (Destroy waits a frame)
            Destroy(col);
            go.name = "BoxBody";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3(0, (float)def.boxY, (float)def.boxZ);
            go.transform.localScale = new Vector3((float)def.boxW, (float)def.boxH, (float)def.boxL);
            return go.GetComponent<Renderer>();
        }

        /// <summary>vehicleModels.js swaps: Paint (the vehicle's colour), Glass, Headlight, Taillight, Reverse, Indicator.</summary>
        Material MaterialFor(string name, VehicleModelSet.Model model, VehicleModelSet set)
        {
            if (_mats.TryGetValue(name, out var m)) return m;
            var def = Sim.Def;
            var info = model.Material(name);
            switch (name)
            {
                case "Paint":
                {
                    int color = def.color;
                    if (def.defaultPaint != null && color == def.defaultPaint[0]) color = def.defaultPaint[1];
                    m = Opaque(set, Hex(color), 0.35f, 0.32f);
                    break;
                }
                case "Glass":
                {
                    m = new Material(set.glassTemplate) { name = "Glass" };
                    var c = Hex(0x0b1116);
                    c.a = (float)def.glassOpacity;
                    m.SetColor("_BaseColor", c);
                    m.SetFloat("_Metallic", 0.1f);
                    m.SetFloat("_Smoothness", 0.97f);
                    break;
                }
                case "Headlight": m = _head = Opaque(set, Hex(0x111111), 0f, 0.3f); break;
                case "Taillight": m = _tail = Opaque(set, Hex(0x111111), 0f, 0.3f); break;
                case "Reverse": m = _rev = Opaque(set, Hex(0x111111), 0f, 0.3f); break;
                case "Indicator": m = _amber = Opaque(set, Hex(0x111111), 0f, 0.3f); break;
                default:
                {
                    if (info != null && info.blend)
                    {
                        m = new Material(set.glassTemplate);
                        var c = info.baseColor.gamma;
                        c.a = info.baseColor.a;
                        m.SetColor("_BaseColor", c);
                        m.SetFloat("_Metallic", info.metallic);
                        m.SetFloat("_Smoothness", 1f - info.roughness);
                    }
                    else if (info != null)
                    {
                        m = Opaque(set, info.baseColor.gamma, info.metallic, info.roughness);
                        if (info.emissive.maxColorComponent > 0) m.SetColor("_EmissionColor", info.emissive);
                    }
                    else m = Opaque(set, Color.grey, 0f, 0.6f);
                    break;
                }
            }
            m.name = def.id + "_" + name;
            _mats[name] = m;
            return m;
        }

        static Material Opaque(VehicleModelSet set, Color srgb, float metallic, float roughness)
        {
            var m = new Material(set.opaqueTemplate);
            m.SetColor("_BaseColor", srgb);
            m.SetFloat("_Metallic", metallic);
            m.SetFloat("_Smoothness", 1f - roughness);
            m.SetColor("_EmissionColor", Color.black);
            return m;
        }

        void Update()
        {
            if (Sim == null) return;
            SyncPose(VehicleSimSystem.Alpha);
            UpdateLights(false);
            ref var s = ref Sim.State;
            bool horn = s.horn && Sim.IsPlayer;
            if (horn && !_horn.isPlaying) _horn.Play();
            else if (!horn && _horn.isPlaying) _horn.Stop();
            if (Lod != null && Sim.IsPlayer) Lod.ForceLOD(0);
            else if (Lod != null) Lod.ForceLOD(-1);
        }

        void SyncPose(float alpha)
        {
            Sim.InterpolatedPose(alpha, out var p, out var q);
            transform.SetPositionAndRotation(p, q);
            ref var s = ref Sim.State;
            var brv = Sim.Brv;
            for (int i = 0; i < _pivots.Count && i < s.wheels.Length; i++)
            {
                var wh = s.wheels[i];
                var l = brv.wheels[i];
                var lp = _pivots[i].localPosition;
                lp.y = (float)(brv.mountModelY - wh.len);
                _pivots[i].localPosition = lp;
                if (l.steer) _pivots[i].localRotation = Quaternion.AngleAxis((float)(-s.steerAngle * Mathf.Rad2Deg), Vector3.up);
                _spins[i].localRotation = Quaternion.AngleAxis((float)(wh.angle * Mathf.Rad2Deg % 360.0), Vector3.right);
            }
        }

        /// <summary>vehicleModels.js setLights(on, braking, reversing).</summary>
        void UpdateLights(bool force)
        {
            ref var s = ref Sim.State;
            bool on = s.lightsOn;
            bool braking = s.ctrl.brake > 0.1 && System.Math.Abs(s.fwdSpeed) > 0.3 && s.driven;
            bool reversing = s.gear < 0 && s.driven;
            int state = (on ? 1 : 0) | (braking ? 2 : 0) | (reversing ? 4 : 0) | (Sim.IsPlayer ? 8 : 0);
            if (!force && state == _lightState) return;
            _lightState = state;
            SetEmission(_head, HeadC, on ? 2.6f : 0.12f);
            SetEmission(_tail, TailC, braking ? 3.5f : on ? 1.0f : 0.2f);
            SetEmission(_rev, RevC, reversing ? 2.5f : 0f);
            SetEmission(_amber, AmberC, on ? 2f : 0.3f);
            if (_headlights != null) foreach (var h in _headlights) h.enabled = on && Sim.IsPlayer;
        }

        static void SetEmission(Material m, Color c, float intensity)
        {
            if (m == null) return;
            m.SetColor("_EmissionColor", c.linear * intensity);
        }

        void OnDestroy()
        {
            foreach (var m in _mats.Values) if (m != null) Destroy(m);
            _mats.Clear();
        }
    }
}
