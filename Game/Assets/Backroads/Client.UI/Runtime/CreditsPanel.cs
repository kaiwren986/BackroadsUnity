using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// The Credits screen (PLAN §1.5 CreditsPanel, [C1, C8]): the OpenStreetMap credit, always, then the build's
    /// THIRD_PARTY_NOTICES.txt (written next to Backroads.exe by notices.ps1), one label per paragraph because a
    /// single UI Toolkit label cannot hold tens of kilobytes of text.
    /// </summary>
    public sealed class CreditsPanel
    {
        public const string OsmCredit =
            "Millbrook's layout was measured from real small towns in OpenStreetMap. Map data (c) OpenStreetMap contributors, " +
            "available under the Open Database License (ODbL 1.0): https://www.openstreetmap.org/copyright";

        public const string NoticesFile = "THIRD_PARTY_NOTICES.txt";

        static readonly Color Panel = new Color(0.08f, 0.09f, 0.10f, 0.95f);
        static readonly Color Ink = new Color(0.93f, 0.93f, 0.90f);
        static readonly Color Dim = new Color(0.65f, 0.66f, 0.64f);

        VisualElement _panel;
        ScrollView _scroll;
        bool _filled;

        public bool Visible => _panel != null && _panel.style.display == DisplayStyle.Flex;

        /// <summary>Next to the player's exe; null in the editor (the notices are generated per build folder).</summary>
        public static string NoticesPath => Application.isEditor ? null : Path.GetFullPath(Path.Combine(Application.dataPath, "..", NoticesFile));

        public VisualElement Build()
        {
            _panel = new VisualElement { name = "credits" };
            var s = _panel.style;
            s.position = Position.Absolute; s.left = Length.Percent(12); s.right = Length.Percent(12); s.top = Length.Percent(6); s.bottom = Length.Percent(6);
            s.backgroundColor = Panel;
            s.paddingLeft = 22; s.paddingRight = 22; s.paddingTop = 16; s.paddingBottom = 14;
            s.borderTopLeftRadius = 6; s.borderTopRightRadius = 6; s.borderBottomLeftRadius = 6; s.borderBottomRightRadius = 6;
            s.display = DisplayStyle.None;

            var title = new Label("Credits");
            title.style.fontSize = 24; title.style.color = Ink; title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _panel.Add(title);

            _scroll = new ScrollView(ScrollViewMode.Vertical) { name = "credits-scroll" };
            _scroll.style.flexGrow = 1; _scroll.style.marginTop = 8; _scroll.style.marginBottom = 8;
            _panel.Add(_scroll);

            var close = new Button(Hide) { text = "Close (Esc)" };
            close.style.alignSelf = Align.FlexEnd;
            _panel.Add(close);
            return _panel;
        }

        public void Show()
        {
            if (_panel == null) return;
            if (!_filled) Fill();
            _panel.style.display = DisplayStyle.Flex;
        }

        public void Hide()
        {
            if (_panel != null) _panel.style.display = DisplayStyle.None;
        }

        void Fill()
        {
            _filled = true;
            Add("Backroads: a free hobby driving game.", 17, Ink, bold: true);
            Add(OsmCredit, 15, Ink);
            Add("Engine sound and behaviour come from engine-sim by Ange Yaghi (MIT licence).", 15, Ink);

            var path = NoticesPath;
            string text = null;
            try { if (path != null && File.Exists(path)) text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception e) { Backroads.Core.Log.Warn("credits: cannot read " + path + ": " + e.Message); }
            if (text == null)
            {
                Add(path == null ? "In the editor: the full notices are written into each build folder (THIRD_PARTY_NOTICES.txt)."
                                 : "THIRD_PARTY_NOTICES.txt is missing next to the game; it lists every licence.", 13, Dim);
                return;
            }
            foreach (var para in Paragraphs(text)) Add(para, 13, Dim);
        }

        /// <summary>Blank-line separated paragraphs, long ones cut at line ends (about 4,000 characters per label).</summary>
        public static IEnumerable<string> Paragraphs(string text)
        {
            const int Max = 4000;
            foreach (var raw in text.Replace("\r", "").Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = raw.Trim('\n');
                if (p.Length == 0) continue;
                if (p.Length <= Max) { yield return p; continue; }
                var sb = new StringBuilder();
                foreach (var line in p.Split('\n'))
                {
                    if (sb.Length + line.Length + 1 > Max && sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line);
                }
                if (sb.Length > 0) yield return sb.ToString();
            }
        }

        void Add(string text, int size, Color color, bool bold = false)
        {
            var l = new Label(text);
            l.style.fontSize = size; l.style.color = color; l.style.whiteSpace = WhiteSpace.Normal; l.style.marginBottom = 8;
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            _scroll.Add(l);
        }
    }
}
