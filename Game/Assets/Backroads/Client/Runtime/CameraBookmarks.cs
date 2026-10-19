using Backroads.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Backroads.Client
{
    /// <summary>
    /// The 12 reference cameras of CanonData v1 (reference/cameras.json, the same views as the contact sheet) on keys
    /// 1-9, 0, -, = (PLAN 6.1 M1 "You see and play"); P / Shift+P jump to the places (POIs) seen from above; T switches
    /// between 10:00 and 21:00, [ and ] move the clock by an hour. A short label names the view.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class CameraBookmarks : MonoBehaviour
    {
        public MillbrookWorld world;
        public bool ScriptedControl;

        static readonly Key[] Keys = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9, Key.Digit0, Key.Minus, Key.Equals };

        string _label;
        float _labelUntil;
        int _poi = -1;
        bool _showHelp = true;
        GUIStyle _style, _small;

        public int Count => world != null && world.Canon != null ? world.Canon.world.cameras.Length : 0;

        void Update()
        {
            if (ScriptedControl || world == null || !world.Ready) return;
            var kb = Keyboard.current;
            if (kb == null) return;
            for (int i = 0; i < Keys.Length && i < Count; i++)
                if (kb[Keys[i]].wasPressedThisFrame) { Go(i); _showHelp = false; }
            if (kb.pKey.wasPressedThisFrame) { JumpToPoi(kb.leftShiftKey.isPressed ? -1 : 1); _showHelp = false; }
            if (kb.tKey.wasPressedThisFrame) { world.SetHour(world.Sky.Hour >= 6f && world.Sky.Hour < 18f ? 21f : 10f); Say($"{world.Sky.Hour:00}:00"); }
            if (ClientInput.Current == null)   // without the input driver [ and ] still move the clock (M1 behaviour)
            {
                if (kb.leftBracketKey.wasPressedThisFrame) { world.SetHour(world.Sky.Hour - 1f); Say($"{Mathf.FloorToInt(world.Sky.Hour):00}:00"); }
                if (kb.rightBracketKey.wasPressedThisFrame) { world.SetHour(world.Sky.Hour + 1f); Say($"{Mathf.FloorToInt(world.Sky.Hour):00}:00"); }
            }
            if (kb.hKey.wasPressedThisFrame) _showHelp = !_showHelp;
        }

        /// <summary>Puts the camera at reference camera i (0-11): position, rotation and the 60 deg field of view.</summary>
        public void Go(int i)
        {
            var w = world.Canon.world;
            var c = w.cameras[i];
            var pose = CanonTransforms.Camera(c);
            var cam = GetComponent<Camera>();
            cam.fieldOfView = w.cameraFov;
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            GetComponent<FreeFlyCamera>()?.SyncFromTransform();
            world.SettleTerrain();
            Say($"{KeyName(i)}  {c.name}");
        }

        /// <summary>Next (dir 1) or previous (dir -1) place, seen from 120 m up and 90 m south-east, looking at it.</summary>
        public void JumpToPoi(int dir)
        {
            var pois = world.Canon.world.pois;
            _poi = ((_poi + dir) % pois.Length + pois.Length) % pois.Length;
            var p = pois[_poi];
            var target = Backroads.Core.BrCoords.Pos(p.x, p.y, p.z);
            var pos = target + new Vector3(-70f, 120f, -70f);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos, Vector3.up));
            GetComponent<FreeFlyCamera>()?.SyncFromTransform();
            world.SettleTerrain();
            Say($"P  {p.name}");
        }

        static string KeyName(int i) => i < 9 ? (i + 1).ToString() : i == 9 ? "0" : i == 10 ? "-" : "=";

        void Say(string text)
        {
            _label = text;
            _labelUntil = Time.unscaledTime + 2.5f;
        }

        void OnGUI()
        {
            if (ScriptedControl || (ClientInput.Current != null && ClientInput.Current.Paused)) return;
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            _small ??= new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = new Color(1, 1, 1, 0.85f) } };
            if (_label != null && Time.unscaledTime < _labelUntil)
            {
                GUI.Label(new Rect(22, 22, 800, 40), _label, _style);
            }
            if (world != null && !world.Ready)
                GUI.Label(new Rect(22, 22, 900, 40), world.Error != null ? "Millbrook could not load: " + world.Error : "Loading Millbrook...", _style);
            if (_showHelp)
                GUI.Label(new Rect(22, Screen.height - 58, 1400, 30), // one line above the version stamp
                    "Fly camera   WASD fly   hold right mouse: look   Q/E down/up   Shift fast   wheel: speed   1-0 - =: the 12 views   P: places   T: day/night   F8: drive   H: hide", _small);
        }
    }
}
