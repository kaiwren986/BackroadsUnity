using Backroads.Core;
using UnityEngine;

namespace Backroads.Client
{
    /// <summary>
    /// T2.7: who moves the main camera. Fly (M1 FreeFlyCamera + CameraBookmarks) when nobody drives or when the fly key
    /// (F8) is on; otherwise the VehicleCameraRig in chase or hood mode (C / Back toggles) following
    /// PlayerVehicle.Current. Autotests that script the camera (FreeFlyCamera.ScriptedControl or Scripted) take it over
    /// completely. Locks the mouse while driving, frees it for menus and the fly camera.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [RequireComponent(typeof(Camera))]
    public sealed class CameraDirector : MonoBehaviour
    {
        public enum Mode { Fly, Chase, Hood }

        public Mode Current { get; private set; } = Mode.Fly;
        /// <summary>The fly camera was asked for (F8), even with a vehicle.</summary>
        public bool UserFly;
        /// <summary>An autotest moves the camera itself.</summary>
        public bool Scripted;
        /// <summary>Lock the mouse while driving (off in tests and when the window has no focus).</summary>
        public bool LockCursor = true;

        public static CameraDirector Main { get; private set; }

        FreeFlyCamera _fly;
        CameraBookmarks _bookmarks;
        VehicleCameraRig _rig;
        IDrivenVehicle _attached;
        bool _cursorLocked;

        public VehicleCameraRig Rig => _rig;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Main = null;

        void Awake()
        {
            Main = this;
            _fly = GetComponent<FreeFlyCamera>();
            _bookmarks = GetComponent<CameraBookmarks>();
            _rig = GetComponent<VehicleCameraRig>();
            if (_rig == null) _rig = gameObject.AddComponent<VehicleCameraRig>();
        }

        void OnDestroy()
        {
            if (Main == this) Main = null;
            SetCursorLock(false);
        }

        bool IsScripted => Scripted || (_fly != null && _fly.ScriptedControl);

        void LateUpdate()
        {
            var input = ClientInput.Current;
            var v = PlayerVehicle.Current;
            if (input != null && input.Pressed("flyCamera"))
            {
                UserFly = !UserFly;
                Toasts.Show(UserFly ? "Fly camera (F8 to drive again)" : "Driving camera");
            }
            if (!ReferenceEquals(v, _attached))
            {
                _attached = v;
                _rig.Attach(v);
            }
            if (v != null && !UserFly && input != null && input.DrivingActive && input.Pressed("camera"))
                _rig.mode = _rig.mode == VehicleCameraRig.Mode.Chase ? VehicleCameraRig.Mode.Hood : VehicleCameraRig.Mode.Chase;

            var want = v == null || UserFly ? Mode.Fly : _rig.mode == VehicleCameraRig.Mode.Hood ? Mode.Hood : Mode.Chase;
            if (want != Current)
            {
                Current = want;
                if (want == Mode.Fly) _fly?.SyncFromTransform();
            }
            bool scripted = IsScripted;
            if (!scripted)
            {
                if (_fly != null) _fly.enabled = want == Mode.Fly;
                if (_bookmarks != null) _bookmarks.enabled = want == Mode.Fly;
            }
            if (input != null) input.FlyMode = want == Mode.Fly;
            if (scripted) { SetCursorLock(false); return; }

            if (want != Mode.Fly)
            {
                bool look = input != null && input.DrivingActive;
                _rig.Tick(Time.deltaTime, look ? input.LookYaw : 0f, look ? input.LookPitch : 0f,
                          look ? input.ZoomWheel : 0, look ? input.Presses("zoom") : 0);
            }
            SetCursorLock(LockCursor && want != Mode.Fly && input != null && !input.Paused && Application.isFocused);
        }

        void SetCursorLock(bool on)
        {
            if (on == _cursorLocked) return;
            _cursorLocked = on;
            Cursor.lockState = on ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !on;
        }
    }
}
