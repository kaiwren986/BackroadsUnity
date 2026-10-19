using Backroads.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// Plugs the client overlay into the game through PresentationRegistry (PLAN 1.5): one UI Toolkit document with the
    /// version stamp (bottom left), the key hint, the crash notice, toasts, the HUD, the F3 debug overlay, the F1 controls
    /// list, the Esc menu with Controls, Settings and Credits, and the F10 playtest checklist. Client-only ("!UNITY_SERVER").
    /// </summary>
    public static class ClientUiBootstrap
    {
        public const string PanelSettingsResource = "BrPanelSettings";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register() => PresentationRegistry.Register("client-ui-overlay", Start);

        static void Start(GameObject root)
        {
            if (Application.isBatchMode) return; // nothing to show without a window

            if (Object.FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                Object.DontDestroyOnLoad(es);
            }

            var panel = Resources.Load<PanelSettings>(PanelSettingsResource);
            if (panel == null) { Log.Error($"Resources/{PanelSettingsResource} missing: run ProjectSetup.Apply"); return; }

            var go = new GameObject("BrOverlay");
            go.SetActive(false);
            go.transform.SetParent(root.transform, false);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = panel;
            doc.sortingOrder = 100;
            go.AddComponent<OverlayController>();
            go.SetActive(true);
        }
    }

    /// <summary>Builds the overlay once the UIDocument has its root element and routes the UI map's actions to it.</summary>
    public sealed class OverlayController : MonoBehaviour
    {
        PlaytestChecklist _checklist;
        PauseMenu _menu;
        CreditsPanel _credits;
        ControlsView _help, _controls;
        SettingsPanel _settings;
        HudView _hud;
        DebugOverlay _debug;
        Label _crash, _toast;
        float _crashUntil, _toastUntil;
        VisualElement _root;

        /// <summary>The menu or a screen opened from it is up (the game is paused), or the F1 list is shown.</summary>
        public bool AnyScreenOpen => (_menu != null && _menu.Paused) || (_help != null && _help.Visible) || (_credits != null && _credits.Visible);

        public static OverlayController Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        public HudView Hud => _hud;
        public DebugOverlay Debug => _debug;
        public ControlsView Help => _help;
        public ControlsView Controls => _controls;
        public SettingsPanel Settings => _settings;
        public PauseMenu Menu => _menu;
        public string LastToast { get; private set; }

        void Awake() => Current = this;

        void OnDestroy()
        {
            if (Current == this) Current = null;
            Toasts.Shown -= OnToast;
        }

        void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            _root = root;
            root.pickingMode = PickingMode.Ignore;
            root.style.flexGrow = 1;

            var stamp = Ui.Outlined(BuildInfo.Current.Stamp, 13, new Color(1f, 1f, 1f, 0.75f));
            stamp.name = "version-stamp";
            stamp.style.position = Position.Absolute;
            stamp.style.left = 10; stamp.style.bottom = 6;
            root.Add(stamp);

            if (CrashNotice.LastSessionCrashed)
            {
                _crash = new Label(CrashNotice.Message) { name = "crash-notice", pickingMode = PickingMode.Ignore };
                _crash.style.position = Position.Absolute;
                _crash.style.top = 16; _crash.style.left = 0; _crash.style.right = 0;
                _crash.style.unityTextAlign = TextAnchor.MiddleCenter;
                _crash.style.fontSize = 20;
                _crash.style.color = new Color(1f, 0.85f, 0.4f);
                _crash.style.backgroundColor = new Color(0f, 0f, 0f, 0.55f);
                _crash.style.paddingTop = 6; _crash.style.paddingBottom = 6;
                root.Add(_crash);
                _crashUntil = Time.realtimeSinceStartup + 12f;
            }

            var hint = Ui.Outlined("Esc menu   F1 controls   F10 checklist", 13, new Color(1f, 1f, 1f, 0.6f));
            hint.name = "key-hint";
            hint.style.position = Position.Absolute;
            hint.style.right = 10; hint.style.bottom = 28; // clear of the "Development Build" watermark
            root.Add(hint);

            _hud = new HudView();
            root.Add(_hud.Build());
            _debug = new DebugOverlay();
            root.Add(_debug.Build());

            _toast = Ui.Outlined("", 20, Ui.Ink);
            _toast.name = "toast";
            _toast.style.position = Position.Absolute;
            _toast.style.top = 60; _toast.style.left = 0; _toast.style.right = 0;
            _toast.style.unityTextAlign = TextAnchor.MiddleCenter;
            _toast.style.display = DisplayStyle.None;
            root.Add(_toast);
            Toasts.Shown += OnToast;

            _help = new ControlsView("controls-overlay", modal: false, onClose: null);
            root.Add(_help.Build());
            _credits = new CreditsPanel(BackToMenu);
            _controls = new ControlsView("controls", modal: true, onClose: () => { _controls.Hide(); BackToMenu(); });
            _settings = new SettingsPanel(() => { _settings.Hide(); BackToMenu(); });
            _menu = new PauseMenu(_controls.Show, _settings.Show, _credits.Show, () => _checklist?.Toggle(), () => _debug.Toggle());
            root.Add(_menu.Build());
            root.Add(_controls.Build());
            root.Add(_settings.Build());
            root.Add(_credits.Build());

            _checklist = new PlaytestChecklist();
            root.Add(_checklist.Build());


            if (SettingsStore.LastMessage != null) OnToast(SettingsStore.LastMessage, true);
            var input = ClientInput.Current;
            if (input != null && input.Error != null) OnToast("Controls: " + input.Error, true);
        }

        void OnToast(string text, bool bad)
        {
            if (_toast == null) return;
            LastToast = text;
            _toast.text = text;
            _toast.style.color = bad ? Ui.Hot : Ui.Ink;
            _toast.style.display = DisplayStyle.Flex;
            _toastUntil = Time.realtimeSinceStartup + 3.6f;
        }

        void BackToMenu()
        {
            if (_menu != null && _menu.Paused) _menu.Return();
        }

        /// <summary>Esc / Start / B: close the top screen, else open or close the menu.</summary>
        public void Back()
        {
            if (_credits != null && _credits.Visible) { _credits.Hide(); BackToMenu(); }
            else if (_controls != null && _controls.Visible) { _controls.Hide(); BackToMenu(); }
            else if (_settings != null && _settings.Visible) { _settings.Hide(); BackToMenu(); }
            else if (_help != null && _help.Visible) _help.Hide();
            else _menu?.Toggle();
        }

        void Update()
        {
            if (_crash != null && Time.realtimeSinceStartup > _crashUntil) { _crash.RemoveFromHierarchy(); _crash = null; }
            if (_toast != null && _toastUntil > 0 && Time.realtimeSinceStartup > _toastUntil) { _toast.style.display = DisplayStyle.None; _toastUntil = 0; }
            var input = ClientInput.Current;
            if (input == null)
            {
                // no bindings (missing config): the M1 keys still work
                var kb = UnityEngine.InputSystem.Keyboard.current;
                if (kb == null) return;
                if (kb.f10Key.wasPressedThisFrame) _checklist?.Toggle();
                if (kb.escapeKey.wasPressedThisFrame) Back();
                return;
            }
            var focused = _root?.focusController?.focusedElement as VisualElement;
            input.Typing = focused != null && (focused is TextField || focused.GetFirstAncestorOfType<TextField>() != null);
            if (input.Pressed("checklist")) _checklist?.Toggle();
            // gamepad B goes back on the menu screens (the Driving map, where B is the clutch, is off while paused)
            var pad = UnityEngine.InputSystem.Gamepad.current;
            bool padBack = pad != null && pad.buttonEast.wasPressedThisFrame && AnyScreenOpen;
            if (input.Pressed("menu") || padBack) Back();
            if (input.Pressed("help")) _help?.Toggle();
            if (input.Pressed("debug")) _debug?.Toggle();
            if (input.Pressed("map")) Toasts.Show("The map arrives in M9");
            if (input.Pressed("graphics"))
            {
                var next = QualityPreset.Tier == QualityTier.Low ? "Mid" : QualityPreset.Tier == QualityTier.Mid ? "High" : "Low";
                SettingsPanel.ApplyQuality(next);
                Toasts.Show("Graphics: " + next);
            }
        }

        void LateUpdate()
        {
            var v = PlayerVehicle.Current;
            var dir = CameraDirector.Main;
            bool show = v != null && (dir == null || dir.Current != CameraDirector.Mode.Fly);
            _hud?.SetVisible(show);
            if (show) _hud.Tick(v);
            _debug?.Tick();
        }

        void OnApplicationQuit() => _checklist?.Save();
    }
}
