using System;
using Backroads.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// T2.6 Esc menu (PLAN 6.1 M2 [S13]), replacing the M1 stand-in: Resume / Controls / Settings / Credits / Quit, plus
    /// the playtest checklist, the debug overlay and the scenes in the build (Millbrook, the Engine Bench, the test yard).
    /// Opening it pauses the game (time scale 0, audio paused, driving input off, mouse free); Controls, Settings and
    /// Credits open over it and come back to it. Gamepad: Start opens it, the D-pad or stick moves, A presses.
    /// </summary>
    public sealed class PauseMenu
    {
        public static readonly (string label, string scene)[] Scenes =
        {
            ("Drive in Millbrook", "Millbrook"),
            ("Engine Bench", "EngineBench"),
            ("Test yard", "TestYard"),
        };

        readonly Action _openControls, _openSettings, _openCredits, _toggleChecklist, _toggleDebug;
        VisualElement _panel;
        Button _resume;
        float _timeScaleBefore = 1f;

        public PauseMenu(Action openControls, Action openSettings, Action openCredits, Action toggleChecklist, Action toggleDebug)
        {
            _openControls = openControls; _openSettings = openSettings; _openCredits = openCredits;
            _toggleChecklist = toggleChecklist; _toggleDebug = toggleDebug;
        }

        public static PauseMenu Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        /// <summary>The menu panel itself is on screen.</summary>
        public bool Visible => Ui.IsShown(_panel);
        /// <summary>The game is paused by this menu (also while Controls, Settings or Credits are open from it).</summary>
        public bool Paused { get; private set; }

        public VisualElement Build()
        {
            Current = this;
            _panel = Ui.PanelBox("quick-menu");   // the M1 name: autotest m1.ui.credits looks for it
            var s = _panel.style;
            s.left = 40; s.top = Length.Percent(18); s.width = 360;

            _panel.Add(Ui.Text("Backroads", 26, Ui.Ink, bold: true));
            var sub = Ui.Text("Paused. Esc or Start to resume.", 13, Ui.Dim);
            sub.style.marginBottom = 10;
            _panel.Add(sub);

            _resume = Ui.Btn("Resume", "resume", Resume);
            _panel.Add(_resume);
            _panel.Add(Ui.Btn("Controls", "open-controls", () => Open(_openControls)));
            _panel.Add(Ui.Btn("Settings", "open-settings", () => Open(_openSettings)));
            _panel.Add(Ui.Btn("Credits", "open-credits", () => Open(_openCredits)));
            _panel.Add(Ui.Btn("Playtest checklist (F10)", "open-checklist", () => { Resume(); _toggleChecklist?.Invoke(); }));
            _panel.Add(Ui.Btn("Debug overlay (F3)", "toggle-debug", () => { _toggleDebug?.Invoke(); }));
            _panel.Add(Ui.Btn("Fly camera for screenshots (F8)", "toggle-fly", () =>
            {
                var d = CameraDirector.Main;
                if (d == null) return;
                d.UserFly = !d.UserFly;
                Resume();
                Toasts.Show(d.UserFly ? "Fly camera (F8 to drive again)" : "Driving camera");
            }));

            var scenesTitle = Ui.Text("Places", 13, Ui.Dim);
            scenesTitle.style.marginTop = 10;
            _panel.Add(scenesTitle);
            foreach (var (label, scene) in Scenes)
            {
                if (!Application.CanStreamedLevelBeLoaded(scene)) continue;
                _panel.Add(Ui.Btn(label, "go-" + scene, () => Load(scene), 15));
            }
            var quit = Ui.Btn("Quit", "quit", Application.Quit);
            quit.style.marginTop = 10;
            _panel.Add(quit);
            return _panel;
        }

        public void Toggle() { if (Visible) Resume(); else Show(); }

        /// <summary>Shows the menu and pauses the game.</summary>
        public void Show()
        {
            if (_panel == null) return;
            Pause(true);
            Ui.Show(_panel, true);
            _resume?.Focus();
        }

        /// <summary>Back to the menu from a screen opened from it.</summary>
        public void Return()
        {
            if (Paused) Ui.Show(_panel, true);
        }

        /// <summary>Closes the menu and everything paused with it.</summary>
        public void Resume()
        {
            Ui.Show(_panel, false);
            Pause(false);
        }

        /// <summary>The M1 name of Resume.</summary>
        public void Hide() => Resume();

        void Open(Action screen)
        {
            Ui.Show(_panel, false);
            screen?.Invoke();
        }

        void Pause(bool on)
        {
            if (on == Paused) return;
            Paused = on;
            if (on)
            {
                _timeScaleBefore = Time.timeScale > 0f ? Time.timeScale : 1f;
                Time.timeScale = 0f;
                AudioListener.pause = true;
            }
            else
            {
                Time.timeScale = _timeScaleBefore;
                AudioListener.pause = false;
            }
            var ci = ClientInput.Current;
            if (ci != null) ci.Paused = on;
            if (on) { UnityEngine.Cursor.lockState = CursorLockMode.None; UnityEngine.Cursor.visible = true; }
        }

        void Load(string scene)
        {
            Resume();
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            Log.Info("menu: load scene " + scene);
            SceneManager.LoadScene(scene);
        }
    }

    /// <summary>The M1 Esc menu's name, kept for the autotests that open it (m1.ui.credits).</summary>
    public static class QuickMenu
    {
        public static PauseMenu Current => PauseMenu.Current;
    }
}
