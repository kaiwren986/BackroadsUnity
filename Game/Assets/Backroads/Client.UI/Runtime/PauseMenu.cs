using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// The M1 Esc menu: jump between the scenes in the build (Millbrook, the Engine Bench, the test yard), open the
    /// Credits, or quit. M2's PauseMenu (Resume / Controls / Settings / Credits / Quit) replaces it.
    /// </summary>
    public sealed class QuickMenu
    {
        public static readonly (string label, string scene)[] Scenes =
        {
            ("Fly over Millbrook", "Millbrook"),
            ("Engine Bench", "EngineBench"),
            ("Test yard", "TestYard"),
        };

        static readonly Color Panel = new Color(0.08f, 0.09f, 0.10f, 0.92f);
        static readonly Color Ink = new Color(0.93f, 0.93f, 0.90f);
        static readonly Color Dim = new Color(0.65f, 0.66f, 0.64f);

        readonly Action _openCredits;
        VisualElement _panel;
        CursorLockMode _cursorBefore;
        bool _cursorVisibleBefore;

        public QuickMenu(Action openCredits) { _openCredits = openCredits; }

        public static QuickMenu Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        public bool Visible => _panel != null && _panel.style.display == DisplayStyle.Flex;

        public VisualElement Build()
        {
            Current = this;
            _panel = new VisualElement { name = "quick-menu" };
            var s = _panel.style;
            s.position = Position.Absolute; s.left = 40; s.top = Length.Percent(25); s.width = 340;
            s.backgroundColor = Panel;
            s.paddingLeft = 18; s.paddingRight = 18; s.paddingTop = 14; s.paddingBottom = 14;
            s.borderTopLeftRadius = 6; s.borderTopRightRadius = 6; s.borderBottomLeftRadius = 6; s.borderBottomRightRadius = 6;
            s.display = DisplayStyle.None;

            var title = new Label("Backroads");
            title.style.fontSize = 24; title.style.color = Ink; title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _panel.Add(title);
            var sub = new Label("Esc closes this menu.");
            sub.style.fontSize = 13; sub.style.color = Dim; sub.style.marginBottom = 10;
            _panel.Add(sub);

            foreach (var (label, scene) in Scenes)
            {
                if (!Application.CanStreamedLevelBeLoaded(scene)) continue;
                var b = new Button(() => Load(scene)) { text = label, name = "go-" + scene };
                Style(b);
                _panel.Add(b);
            }
            var credits = new Button(() => { Hide(); _openCredits?.Invoke(); }) { text = "Credits", name = "open-credits" };
            Style(credits);
            _panel.Add(credits);
            var quit = new Button(Application.Quit) { text = "Quit", name = "quit" };
            Style(quit);
            _panel.Add(quit);
            return _panel;
        }

        static void Style(Button b)
        {
            b.style.fontSize = 17; b.style.height = 36; b.style.marginTop = 4;
        }

        public void Toggle() { if (Visible) Hide(); else Show(); }

        public void Show()
        {
            if (_panel == null) return;
            _cursorBefore = UnityEngine.Cursor.lockState;
            _cursorVisibleBefore = UnityEngine.Cursor.visible;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            _panel.style.display = DisplayStyle.Flex;
        }

        public void Hide()
        {
            if (_panel == null || !Visible) return;
            _panel.style.display = DisplayStyle.None;
            UnityEngine.Cursor.lockState = _cursorBefore;
            UnityEngine.Cursor.visible = _cursorVisibleBefore;
        }

        void Load(string scene)
        {
            _panel.style.display = DisplayStyle.None;
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            Core.Log.Info("menu: load scene " + scene);
            SceneManager.LoadScene(scene);
        }
    }
}
