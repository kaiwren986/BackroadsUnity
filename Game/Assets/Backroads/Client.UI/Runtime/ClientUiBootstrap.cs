using Backroads.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// Plugs the client overlay into the game through PresentationRegistry (PLAN 1.5): one UI Toolkit document with the
    /// version stamp (bottom left), the crash notice and the F10 playtest checklist. Client-only ("!UNITY_SERVER").
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

    /// <summary>Builds the overlay once the UIDocument has its root element.</summary>
    public sealed class OverlayController : MonoBehaviour
    {
        PlaytestChecklist _checklist;
        Label _crash;
        float _crashUntil;

        void Start()
        {
            var root = GetComponent<UIDocument>().rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.style.flexGrow = 1;

            var stamp = new Label(BuildInfo.Current.Stamp) { name = "version-stamp", pickingMode = PickingMode.Ignore };
            stamp.style.position = Position.Absolute;
            stamp.style.left = 10; stamp.style.bottom = 6;
            stamp.style.fontSize = 13;
            stamp.style.color = new Color(1f, 1f, 1f, 0.75f);
            stamp.style.unityTextOutlineColor = new Color(0f, 0f, 0f, 0.6f);
            stamp.style.unityTextOutlineWidth = 0.15f;
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

            _checklist = new PlaytestChecklist();
            root.Add(_checklist.Build());
        }

        void Update()
        {
            if (_crash != null && Time.realtimeSinceStartup > _crashUntil) { _crash.RemoveFromHierarchy(); _crash = null; }
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f10Key.wasPressedThisFrame) _checklist?.Toggle();
        }

        void OnApplicationQuit() => _checklist?.Save();
    }
}
