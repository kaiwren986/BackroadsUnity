using Backroads.Client.Rendering;
using Backroads.World;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Backroads.Client
{
    /// <summary>
    /// M1 fly camera (PLAN 6.1 "You see and play"): WASD to move, hold the right mouse button to look, Q/E down/up
    /// (also Ctrl/Space), Shift faster, Alt slower, mouse wheel changes speed. It never goes below the ground
    /// (WorldGridQueries) and stays over the island. Input is ignored while ScriptedControl is set (autotests).
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class FreeFlyCamera : MonoBehaviour
    {
        public float speed = 30f;
        public float lookSensitivity = 0.12f;
        public float minClearance = 1.2f;
        public bool ScriptedControl;

        float _yaw, _pitch;

        void OnEnable() => SyncFromTransform();

        /// <summary>Re-reads yaw and pitch after something else moved the camera (bookmarks, autotests).</summary>
        public void SyncFromTransform()
        {
            var e = transform.rotation.eulerAngles;
            _yaw = e.y;
            _pitch = e.x > 180 ? e.x - 360 : e.x;
        }

        void Update()
        {
            if (ScriptedControl) return;
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            if (mouse != null)
            {
                if (mouse.rightButton.isPressed)
                {
                    var d = mouse.delta.ReadValue();
                    _yaw += d.x * lookSensitivity;
                    _pitch = Mathf.Clamp(_pitch - d.y * lookSensitivity, -89f, 89f);
                    transform.rotation = Quaternion.Euler(_pitch, _yaw, 0);
                }
                float wheel = mouse.scroll.ReadValue().y;
                if (wheel != 0) speed = Mathf.Clamp(speed * (wheel > 0 ? 1.25f : 0.8f), 2f, 400f);
            }
            if (kb == null) return;
            var move = Vector3.zero;
            if (kb.wKey.isPressed) move += Vector3.forward;
            if (kb.sKey.isPressed) move += Vector3.back;
            if (kb.dKey.isPressed) move += Vector3.right;
            if (kb.aKey.isPressed) move += Vector3.left;
            if (kb.eKey.isPressed || kb.spaceKey.isPressed) move += Vector3.up;
            if (kb.qKey.isPressed || kb.leftCtrlKey.isPressed) move += Vector3.down;
            if (move == Vector3.zero) return;
            float s = speed * (kb.leftShiftKey.isPressed ? 4f : 1f) * (kb.leftAltKey.isPressed ? 0.25f : 1f);
            var delta = transform.rotation * new Vector3(move.x, 0, move.z) + Vector3.up * move.y;
            MoveTo(transform.position + delta.normalized * s * dt);
        }

        /// <summary>Moves to a position, kept above the ground and over the island.</summary>
        public void MoveTo(Vector3 p)
        {
            p.x = Mathf.Clamp(p.x, -700f, 700f);
            p.z = Mathf.Clamp(p.z, -700f, 700f);
            p.y = Mathf.Clamp(p.y, -20f, 2000f);
            if (CanonRuntime.IsLoaded)
            {
                float ground = WorldGridQueries.HeightAt(CanonRuntime.Grid.View, new float3(p.x, 0, p.z));
                p.y = Mathf.Max(p.y, Mathf.Max(ground, 0f) + minClearance);
            }
            transform.position = p;
        }
    }
}
