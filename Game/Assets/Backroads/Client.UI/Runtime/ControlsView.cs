using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// T2.6 controls list, generated from bindings.json (the table the game really uses, with settings.json changes):
    /// the F1 overlay and the Controls screen of the Esc menu. Actions that start working in a later milestone are
    /// marked with it.
    /// </summary>
    public sealed class ControlsView
    {
        readonly string _name;
        readonly bool _modal;
        readonly Action _onClose;
        VisualElement _panel;
        ScrollView _list;
        BindingTable _shown;

        public ControlsView(string name, bool modal, Action onClose)
        {
            _name = name; _modal = modal; _onClose = onClose;
        }

        public bool Visible => Ui.IsShown(_panel);

        public VisualElement Build()
        {
            _panel = Ui.PanelBox(_name);
            var s = _panel.style;
            if (_modal) { s.left = Length.Percent(14); s.right = Length.Percent(14); s.top = Length.Percent(6); s.bottom = Length.Percent(6); }
            else { s.right = 24; s.top = 24; s.bottom = 60; s.width = 640; s.backgroundColor = new Color(0.06f, 0.07f, 0.08f, 0.86f); }
            _panel.Add(Ui.Text("Controls", 24, Ui.Ink, bold: true));
            _panel.Add(Ui.Text(_modal ? "Keyboard and mouse | gamepad. Esc or B goes back." : "Keyboard and mouse | gamepad. F1 closes this list.", 13, Ui.Dim));
            _list = new ScrollView(ScrollViewMode.Vertical) { name = _name + "-list" };
            _list.style.flexGrow = 1; _list.style.marginTop = 8;
            _list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            _panel.Add(_list);
            if (_modal)
            {
                var close = Ui.Btn("Back", _name + "-back", () => _onClose?.Invoke());
                close.style.alignSelf = Align.FlexEnd;
                _panel.Add(close);
            }
            return _panel;
        }

        public void Show()
        {
            var t = ClientInput.Current?.Table ?? BindingTable.LoadDefault(out _);
            if (t != null && !ReferenceEquals(t, _shown)) Fill(t);
            Ui.Show(_panel, true);
            if (_modal) _panel.Q<Button>(_name + "-back")?.Focus();
        }

        public void Hide() => Ui.Show(_panel, false);

        public void Toggle() { if (Visible) Hide(); else Show(); }

        /// <summary>The rows shown: (map, help, keyboard, gamepad, since) per action, hold bindings folded in.</summary>
        public static List<(string map, string help, string keys, string pad, string since)> Rows(BindingTable t)
        {
            var rows = new List<(string, string, string, string, string)>();
            var all = t.maps.SelectMany(m => m.bindings).ToList();
            foreach (var m in t.maps)
            {
                foreach (var action in m.bindings.Select(b => b.action).Distinct())
                {
                    var own = m.bindings.Where(b => b.action == action).ToList();
                    var first = own[0];
                    var keys = own.SelectMany(b => KeyNames(b)).Distinct().ToList();
                    var pads = own.SelectMany(b => b.PadControls.Select(p => BindingTable.Pretty(p) + (string.IsNullOrEmpty(b.hold) ? "" : " (tap)"))).Distinct().ToList();
                    foreach (var h in all.Where(x => x.hold == action))
                    {
                        keys.AddRange(h.KeyboardControls.Select(p => "hold " + BindingTable.Pretty(p)));
                        pads.AddRange(h.PadControls.Select(p => "hold " + BindingTable.Pretty(p)));
                    }
                    if (own.Any(b => b.padVia == "menu") && pads.Count == 0) pads.Add("Start menu");
                    string help = first.help ?? action;
                    int cut = help.IndexOf(" (gamepad", StringComparison.Ordinal);
                    if (cut > 0) help = help.Substring(0, cut);
                    cut = help.IndexOf(" (hold ", StringComparison.Ordinal);
                    if (cut > 0) help = help.Substring(0, cut);
                    rows.Add((m.name, help, string.Join(", ", keys), string.Join(", ", pads), first.since));
                }
            }
            return rows;
        }

        static IEnumerable<string> KeyNames(BindingEntry b)
        {
            if (b.kind == "axis" && (b.negative?.Length ?? 0) > 0)
            {
                for (int i = 0; i < b.negative.Length; i++)
                    yield return BindingTable.Pretty(b.negative[i]) + "/" + (i < b.positive.Length ? BindingTable.Pretty(b.positive[i]) : "");
                yield break;
            }
            if (b.kind == "vector" && (b.up?.Length ?? 0) > 0)
            {
                yield return string.Join("", new[] { b.up[0], b.left[0], b.down[0], b.right[0] }.Select(BindingTable.Pretty));
                yield break;
            }
            foreach (var k in b.keys ?? new string[0]) yield return BindingTable.Pretty(k);
        }

        void Fill(BindingTable t)
        {
            _shown = t;
            _list.Clear();
            string map = null;
            foreach (var r in Rows(t))
            {
                if (r.map == "OnFoot") continue;   // on foot arrives in M7
                if (r.map != map)
                {
                    map = r.map;
                    var h = Ui.Text(map == "UI" ? "Screens" : map, 17, Ui.Accent, bold: true);
                    h.style.marginTop = 8; h.style.marginBottom = 2;
                    _list.Add(h);
                }
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.marginBottom = 2;
                bool later = r.since != null && string.CompareOrdinal(r.since, "M2") > 0 && r.since.Length == 2;
                var c0 = Ui.Text(r.help + (later ? $"  ({r.since})" : ""), 14, later ? Ui.Dim : Ui.Ink);
                c0.style.width = Length.Percent(42);
                var c1 = Ui.Text(r.keys, 14, Ui.Ink);
                c1.style.width = Length.Percent(30);
                var c2 = Ui.Text(r.pad, 14, Ui.Dim);
                c2.style.width = Length.Percent(28);
                row.Add(c0); row.Add(c1); row.Add(c2);
                _list.Add(row);
            }
        }
    }
}
