using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// T2.6 Settings screen (from the Esc menu): graphics quality, volumes, screen size and mode, look sensitivity and
    /// inversion, time of day (the gamepad route to [ and ]). Every change applies at once and is saved to settings.json.
    /// </summary>
    public sealed class SettingsPanel
    {
        readonly Action _onClose;
        VisualElement _panel;
        Label _status;
        readonly Dictionary<string, Button> _quality = new Dictionary<string, Button>();
        Slider _master, _engine, _effects, _look;
        Toggle _invert;
        DropdownField _resolution, _mode;
        bool _filling;

        public SettingsPanel(Action onClose) { _onClose = onClose; }

        public bool Visible => Ui.IsShown(_panel);

        public VisualElement Build()
        {
            _panel = Ui.PanelBox("settings");
            var s = _panel.style;
            s.left = Length.Percent(22); s.right = Length.Percent(22); s.top = Length.Percent(8);
            _panel.Add(Ui.Text("Settings", 24, Ui.Ink, bold: true));
            _panel.Add(Ui.Text("Changes apply at once and are saved for next time.", 13, Ui.Dim));

            _panel.Add(Section("Graphics"));
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            foreach (var q in new[] { "Low", "Mid", "High" })
            {
                var b = Ui.Btn(q, "quality-" + q.ToLowerInvariant(), () => SetQuality(q), 15);
                b.style.width = 110; b.style.marginRight = 6;
                _quality[q] = b;
                row.Add(b);
            }
            _panel.Add(row);

            _resolution = new DropdownField("Screen size") { name = "resolution" };
            _resolution.RegisterValueChangedCallback(e => { if (!_filling) SetResolution(e.newValue, _mode.value); });
            _mode = new DropdownField("Window", new List<string> { "Full screen", "Exclusive full screen", "Window" }, 0) { name = "window-mode" };
            _mode.RegisterValueChangedCallback(e => { if (!_filling) SetResolution(_resolution.value, e.newValue); });
            Style(_resolution); Style(_mode);
            _panel.Add(_resolution);
            _panel.Add(_mode);

            _panel.Add(Section("Sound"));
            _master = Volume("Master", "volume-master", v => Change(st => st.masterVolume = v));
            _engine = Volume("Engine", "volume-engine", v => Change(st => st.engineVolume = v));
            _effects = Volume("Effects", "volume-effects", v => Change(st => st.effectsVolume = v));

            _panel.Add(Section("Camera"));
            _look = new Slider("Look speed", 0.3f, 3f) { name = "look-speed" };
            _look.RegisterValueChangedCallback(e => { if (!_filling) Change(st => st.lookSensitivity = e.newValue); });
            Style(_look);
            _panel.Add(_look);
            _invert = new Toggle("Invert look up / down") { name = "invert-look" };
            _invert.RegisterValueChangedCallback(e => { if (!_filling) Change(st => st.invertLookY = e.newValue); });
            Style(_invert);
            _panel.Add(_invert);

            _panel.Add(Section("Time of day"));
            var trow = new VisualElement();
            trow.style.flexDirection = FlexDirection.Row;
            var back = Ui.Btn("- 1 hour", "time-back", () => Hour(-1), 15);
            var fwd = Ui.Btn("+ 1 hour", "time-forward", () => Hour(1), 15);
            back.style.width = 110; fwd.style.width = 110; back.style.marginRight = 6;
            trow.Add(back); trow.Add(fwd);
            _panel.Add(trow);

            _status = Ui.Text("", 12, Ui.Dim);
            _status.style.marginTop = 8;
            _panel.Add(_status);

            var buttons = new VisualElement();
            buttons.style.flexDirection = FlexDirection.Row; buttons.style.justifyContent = Justify.FlexEnd; buttons.style.marginTop = 6;
            var defaults = Ui.Btn("Defaults", "settings-defaults", Defaults, 15);
            defaults.style.marginRight = 6;
            buttons.Add(defaults);
            buttons.Add(Ui.Btn("Back", "settings-back", () => _onClose?.Invoke(), 15));
            _panel.Add(buttons);
            return _panel;
        }

        static Label Section(string text)
        {
            var l = Ui.Text(text, 16, Ui.Accent, bold: true);
            l.style.marginTop = 10; l.style.marginBottom = 2;
            return l;
        }

        static void Style(VisualElement e)
        {
            e.style.marginTop = 4;
            // only the field's own label: the drop-down keeps the theme's dark text on its light box
            foreach (var l in e.Query<Label>(className: "unity-base-field__label").ToList()) l.style.color = Ui.Ink;
        }

        Slider Volume(string label, string name, Action<float> set)
        {
            var sl = new Slider(label, 0f, 1f) { name = name };
            sl.RegisterValueChangedCallback(e => { if (!_filling) set(e.newValue); });
            Style(sl);
            _panel.Add(sl);
            return sl;
        }

        public void Show()
        {
            Fill();
            Ui.Show(_panel, true);
            _panel.Q<Button>("settings-back")?.Focus();
        }

        public void Hide() => Ui.Show(_panel, false);

        void Fill()
        {
            _filling = true;
            var st = SettingsStore.Current;
            string tier = QualityPreset.Tier.ToString();
            foreach (var kv in _quality) kv.Value.style.backgroundColor = kv.Key == tier ? new Color(0.30f, 0.45f, 0.30f) : Ui.Button;
            var sizes = Screen.resolutions.Select(r => $"{r.width} x {r.height}").Distinct().ToList();
            string now = $"{Screen.width} x {Screen.height}";
            if (!sizes.Contains(now)) sizes.Add(now);
            _resolution.choices = sizes;
            _resolution.SetValueWithoutNotify(now);
            _mode.SetValueWithoutNotify(ModeName(Screen.fullScreenMode));
            _master.SetValueWithoutNotify(st.masterVolume);
            _engine.SetValueWithoutNotify(st.engineVolume);
            _effects.SetValueWithoutNotify(st.effectsVolume);
            _look.SetValueWithoutNotify(st.lookSensitivity);
            _invert.SetValueWithoutNotify(st.invertLookY);
            _status.text = "Saved in " + SettingsStore.DefaultPath;
            _filling = false;
        }

        static string ModeName(FullScreenMode m) => m == FullScreenMode.ExclusiveFullScreen ? "Exclusive full screen" : m == FullScreenMode.Windowed ? "Window" : "Full screen";

        static FullScreenMode ModeOf(string name) => name == "Exclusive full screen" ? FullScreenMode.ExclusiveFullScreen : name == "Window" ? FullScreenMode.Windowed : FullScreenMode.FullScreenWindow;

        void Change(Action<GameSettings> edit)
        {
            var st = SettingsStore.Current;
            edit(st);
            SettingsStore.Apply(st, atBoot: false);
            if (SettingsStore.Save(st, SettingsStore.DefaultPath, out var err)) _status.text = "Saved in " + SettingsStore.DefaultPath;
            else _status.text = "Could not save: " + err;
        }

        /// <summary>Sets the quality tier (also F2), applies it to the world and saves it.</summary>
        public static void ApplyQuality(string tier)
        {
            var st = SettingsStore.Current;
            st.quality = tier;
            SettingsStore.Apply(st, atBoot: false);
            MillbrookWorld.Current?.ApplyQuality(QualityPreset.Current);
            SettingsStore.SaveCurrent();
        }

        void SetQuality(string tier)
        {
            ApplyQuality(tier);
            Fill();
            Toasts.Show("Graphics: " + tier);
        }

        void SetResolution(string size, string mode)
        {
            var parts = (size ?? "").Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out int w) || !int.TryParse(parts[1].Trim(), out int h)) return;
            Change(st => { st.width = w; st.height = h; st.fullscreen = ModeOf(mode).ToString(); });
        }

        static void Hour(int d)
        {
            var w = MillbrookWorld.Current;
            if (w == null || w.Sky == null) return;
            w.SetHour((w.Sky.Hour + 24f + d) % 24f);
            Toasts.Show($"{Mathf.FloorToInt(w.Sky.Hour):00}:00");
        }

        void Defaults()
        {
            var keep = SettingsStore.Current;
            var d = new GameSettings { bindings = keep.bindings };
            Change(st => { st.quality = d.quality; st.masterVolume = d.masterVolume; st.engineVolume = d.engineVolume; st.effectsVolume = d.effectsVolume; st.lookSensitivity = d.lookSensitivity; st.invertLookY = d.invertLookY; st.width = 0; st.height = 0; st.fullscreen = ""; });
            Fill();
        }
    }
}
