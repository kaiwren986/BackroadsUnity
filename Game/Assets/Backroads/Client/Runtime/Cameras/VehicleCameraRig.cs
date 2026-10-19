using Backroads.Core;
using Backroads.World;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Client
{
    /// <summary>Optional: a vehicle that knows its camera.js camDist and camHeight (otherwise they come from its size).</summary>
    public interface IVehicleCameraHints
    {
        float CameraDistance { get; }
        float CameraHeight { get; }
    }

    /// <summary>
    /// T2.7 chase and hood cameras, a straight port of camera.js CameraRig.vehicle (03 section 4.19) in Unity space:
    /// yaw follows the heading at 4/s (1.5/s when slow) and looks back when reversing faster than 4 m/s; mouse and right
    /// stick orbit, and after 1.5 s without look input above 2 m/s the orbit recentres; pitch 0.2 + orbit; distance
    /// camDist x zoom x (1 + 0.006 min(v, 40)); a terrain ray pulls the camera in (at least 1.5 m), which stays 0.6 m
    /// above the ground and 0.3 m above water; FOV 60 rising to 76 with speed. The hood camera sits at the vehicle's
    /// HoodAnchor. Driven by CameraDirector.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class VehicleCameraRig : MonoBehaviour
    {
        public enum Mode { Chase, Hood }

        public Mode mode = Mode.Chase;
        public float zoom = 1f;
        public float CamDistance { get; private set; } = 8f;
        public float CamHeight { get; private set; } = 2.6f;

        static readonly float[] ZoomSteps = { 1f, 1.4f, 1.8f, 0.7f };

        Camera _cam;
        IDrivenVehicle _v;
        float _yaw, _orbitYaw, _orbitPitch, _idle = 10f, _ty;
        bool _tyValid;

        void Awake() => _cam = GetComponent<Camera>();

        public IDrivenVehicle Vehicle => _v;

        /// <summary>Follows a vehicle from now on (null: none); estimates camDist and camHeight once.</summary>
        public void Attach(IDrivenVehicle v)
        {
            _v = v;
            _orbitYaw = _orbitPitch = 0f;
            _idle = 10f;
            _tyValid = false;
            if (v?.Body == null) return;
            var f = v.Body.forward;
            _yaw = Mathf.Atan2(f.x, f.z);
            EstimateHints(v, out float dist, out float height);
            CamDistance = dist;
            CamHeight = height;
        }

        /// <summary>
        /// camera.js values per vehicle when the vehicle says them (IVehicleCameraHints); otherwise from the size of the
        /// renderers under its body: distance 1.6 x length, height top + 0.65 m (within 5 % of the JS values for the
        /// hatch, rally, pickup and truck).
        /// </summary>
        public static void EstimateHints(IDrivenVehicle v, out float distance, out float height)
        {
            distance = 8f; height = 2.6f;
            if (v is IVehicleCameraHints h) { distance = h.CameraDistance; height = h.CameraHeight; return; }
            var body = v?.Body;
            if (body == null) return;
            var rs = body.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            var inv = body.worldToLocalMatrix;
            Vector3 mn = Vector3.positiveInfinity, mx = Vector3.negativeInfinity;
            foreach (var r in rs)
            {
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var l = inv.MultiplyPoint3x4(c);
                    mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l);
                }
            }
            float length = Mathf.Max(mx.z - mn.z, mx.x - mn.x);
            distance = Mathf.Clamp(1.6f * length, 5f, 16f);
            height = Mathf.Clamp(mx.y + 0.65f, 1.6f, 5.5f);
        }

        /// <summary>One camera update (LateUpdate): look in radians (+ right, + up), wheel steps (+ out), zoom-cycle presses.</summary>
        public void Tick(float dt, float lookYaw, float lookPitch, int wheel, int zoomCycle)
        {
            if (_v == null || _v.Body == null) return;
            var body = _v.Body;
            var vel = _v.Velocity;
            float speed = vel.magnitude;
            var fwd = body.forward;
            float fwdSpeed = Vector3.Dot(vel, fwd);

            if (wheel != 0) zoom = Mathf.Clamp(zoom + wheel * 0.08f, 0.55f, 1.8f);
            for (int i = 0; i < zoomCycle; i++)
            {
                int k = 0;
                for (int s = 0; s < ZoomSteps.Length; s++) if (Mathf.Abs(ZoomSteps[s] - zoom) < 0.05f) k = s + 1;
                zoom = ZoomSteps[k % ZoomSteps.Length];
            }
            if (lookYaw != 0f || lookPitch != 0f) { _orbitYaw += lookYaw; _orbitPitch += lookPitch; _idle = 0f; }
            else _idle += dt;
            if (_idle > 1.5f && speed > 2f)
            {
                float k = Mathf.Exp(-dt * 2.2f);
                _orbitYaw *= k; _orbitPitch *= k;
            }
            _orbitYaw = Mathf.Atan2(Mathf.Sin(_orbitYaw), Mathf.Cos(_orbitYaw));
            _orbitPitch = Mathf.Clamp(_orbitPitch, -0.45f, 1.0f);
            float heading = Mathf.Atan2(fwd.x, fwd.z);
            if (fwdSpeed < -4f) heading = Mathf.Atan2(-fwd.x, -fwd.z);
            float follow = 1f - Mathf.Exp(-dt * (speed > 1f ? 4f : 1.5f));
            _yaw = LerpAngle(_yaw, heading, follow);

            if (mode == Mode.Hood)
            {
                var anchor = _v.HoodAnchor != null ? _v.HoodAnchor : body;
                var pos = anchor.position + (_v.HoodAnchor == null ? body.up * (CamHeight * 0.5f) : Vector3.zero);
                // camera.js looks 20 m ahead and 0.15 m lower, then turns by the orbit yaw
                var rot = anchor.rotation * Quaternion.Euler(0.43f, _orbitYaw * Mathf.Rad2Deg, 0f);
                transform.SetPositionAndRotation(pos, rot);
                Fov(speed, dt);
                return;
            }

            var tgt = body.position;
            tgt.y += CamHeight * 0.42f;
            if (!_tyValid) { _ty = tgt.y; _tyValid = true; }
            _ty += (tgt.y - _ty) * Mathf.Min(1f, dt * 9f);
            tgt.y = _ty;
            float yaw = _yaw + _orbitYaw;
            float pitch = Mathf.Clamp(0.2f + _orbitPitch, -0.25f, 1.25f);
            float dist = CamDistance * zoom * (1f + Mathf.Min(speed, 40f) * 0.006f);
            float cx = Mathf.Sin(yaw) * Mathf.Cos(pitch), cy = Mathf.Sin(pitch), cz = Mathf.Cos(yaw) * Mathf.Cos(pitch);
            float d = dist;
            bool haveGround = CanonRuntime.IsLoaded;
            if (haveGround)
            {
                var g = CanonRuntime.Grid.View;
                // JS space: x flipped; the Unity direction (-cx, cy, -cz) is (cx, cy, -cz) there
                float hit = WorldGridQueries.RayTerrainJs(g, new float3(-tgt.x, tgt.y, tgt.z), new float3(cx, cy, -cz), dist, 0.6f);
                if (hit > 0f) d = Mathf.Max(1.5f, hit - 0.5f);
            }
            var p = new Vector3(tgt.x - cx * d, tgt.y + cy * d + CamHeight * 0.35f, tgt.z - cz * d);
            if (haveGround)
            {
                var g = CanonRuntime.Grid.View;
                float ground = WorldGridQueries.HeightAt(g, p);
                if (p.y < ground + 0.6f) p.y = ground + 0.6f;
                if (p.y < 0.3f && ground < 0f) p.y = 0.3f;
            }
            var look = new Vector3(tgt.x + Mathf.Sin(yaw) * 2f, tgt.y + 0.4f, tgt.z + Mathf.Cos(yaw) * 2f);
            transform.SetPositionAndRotation(p, Quaternion.LookRotation(look - p, Vector3.up));
            Fov(speed, dt);
        }

        void Fov(float speed, float dt)
        {
            if (_cam == null) return;
            float target = 60f + Mathf.Min(16f, Mathf.Max(0f, speed - 8f) * 0.4f);
            float f = _cam.fieldOfView + (target - _cam.fieldOfView) * Mathf.Min(1f, dt * 3f);
            if (Mathf.Abs(f - _cam.fieldOfView) > 0.01f) _cam.fieldOfView = f;
        }

        static float LerpAngle(float a, float b, float t)
        {
            float d = b - a;
            d = Mathf.Atan2(Mathf.Sin(d), Mathf.Cos(d));
            return a + d * t;
        }
    }
}
