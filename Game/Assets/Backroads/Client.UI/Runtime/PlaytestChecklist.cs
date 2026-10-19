using System;
using System.Collections.Generic;
using System.IO;
using Backroads.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// F10 Playtest Checklist (PLAN 1.10 [S12]), in every client build. Items come from
    /// StreamingAssets/Playtest/(milestone).json; each has Yes / No and a comment. Answers are saved on every change to
    /// (data)/playtest/(build id).json, i.e. %USERPROFILE%\AppData\LocalLow\(company)\(product)\playtest\ for the user.
    /// </summary>
    public sealed class PlaytestChecklist
    {
        [Serializable] public sealed class Item { public string id; public string text; }

        [Serializable]
        public sealed class ItemsFile
        {
            public string milestone;
            public string title;
            public string summary;
            public Item[] items;
        }

        [Serializable] public sealed class Answer { public string id; public string text; public string answer = ""; public string comment = ""; }

        [Serializable]
        public sealed class AnswersFile
        {
            public string build;
            public string milestone;
            public string savedUtc;
            public Answer[] answers;
        }

        static readonly Color Panel = new Color(0.08f, 0.09f, 0.10f, 0.92f);
        static readonly Color Ink = new Color(0.93f, 0.93f, 0.90f);
        static readonly Color Dim = new Color(0.65f, 0.66f, 0.64f);
        static readonly Color YesOn = new Color(0.20f, 0.55f, 0.30f);
        static readonly Color NoOn = new Color(0.65f, 0.22f, 0.20f);
        static readonly Color Off = new Color(0.22f, 0.23f, 0.25f);

        VisualElement _panel;
        Label _status;
        ItemsFile _items;
        readonly List<Answer> _answers = new List<Answer>();
        readonly Dictionary<string, (Button yes, Button no)> _buttons = new Dictionary<string, (Button, Button)>();

        /// <summary>The checklist of this run (autotest m0.playtest.f10 drives it).</summary>
        public static PlaytestChecklist Current { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Current = null;

        public bool Visible => _panel != null && _panel.style.display == DisplayStyle.Flex;
        public IReadOnlyList<Item> Items => _items?.items ?? new Item[0];

        /// <summary>Presses an item's Yes or No button the way the UI does (a submit event on the button).</summary>
        public bool Press(string itemId, string which)
        {
            var b = _panel?.Q<Button>(which + "-" + itemId);
            if (b == null) return false;
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
            return true;
        }

        public string AnswerOf(string itemId)
        {
            foreach (var a in _answers) if (a.id == itemId) return a.answer;
            return "";
        }
        public string AnswersPath => Path.Combine(BrPaths.PlaytestDir, BuildInfo.Current.Id + ".json");

        public static ItemsFile LoadItems(string milestone)
        {
            var dir = Path.Combine(Application.streamingAssetsPath, "Playtest");
            var path = Path.Combine(dir, milestone + ".json");
            if (!File.Exists(path) && Directory.Exists(dir))
            {
                var all = Directory.GetFiles(dir, "M*.json");
                Array.Sort(all, StringComparer.Ordinal);
                if (all.Length > 0) path = all[all.Length - 1];
            }
            if (!File.Exists(path)) return new ItemsFile { milestone = milestone, title = "Playtest checklist", items = new Item[0] };
            return JsonUtility.FromJson<ItemsFile>(File.ReadAllText(path));
        }

        public VisualElement Build()
        {
            _items = LoadItems(BuildInfo.Current.milestone);
            LoadAnswers();

            Current = this;
            _panel = new VisualElement { name = "playtest-checklist" };
            var s = _panel.style;
            s.position = Position.Absolute; s.top = 40; s.right = 40; s.width = 560;
            s.backgroundColor = Panel;
            s.paddingLeft = 18; s.paddingRight = 18; s.paddingTop = 14; s.paddingBottom = 14;
            s.borderTopLeftRadius = 6; s.borderTopRightRadius = 6; s.borderBottomLeftRadius = 6; s.borderBottomRightRadius = 6;
            s.display = DisplayStyle.None;

            var title = new Label($"{(_items.title ?? "Playtest checklist")}  ({_items.milestone})");
            title.style.fontSize = 22; title.style.color = Ink; title.style.unityFontStyleAndWeight = FontStyle.Bold;
            _panel.Add(title);
            var sub = new Label($"Build {BuildInfo.Current.Id}. Press F10 to close. Answers save as you go.");
            sub.style.fontSize = 13; sub.style.color = Dim; sub.style.marginBottom = 10;
            _panel.Add(sub);

            foreach (var it in _items.items ?? new Item[0]) _panel.Add(BuildItem(it));

            _status = new Label("");
            _status.style.fontSize = 12; _status.style.color = Dim; _status.style.marginTop = 8;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _panel.Add(_status);

            var close = new Button(Toggle) { text = "Close (F10)" };
            close.style.marginTop = 8; close.style.alignSelf = Align.FlexEnd;
            _panel.Add(close);
            return _panel;
        }

        VisualElement BuildItem(Item it)
        {
            var a = AnswerFor(it);
            var box = new VisualElement { name = "item-" + it.id };
            box.style.marginTop = 6; box.style.marginBottom = 6;

            var q = new Label(it.text);
            q.style.fontSize = 17; q.style.color = Ink; q.style.whiteSpace = WhiteSpace.Normal;
            box.Add(q);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row; row.style.marginTop = 4;
            var yes = new Button(() => SetAnswer(it, "yes")) { text = "Yes", name = "yes-" + it.id };
            var no = new Button(() => SetAnswer(it, "no")) { text = "No", name = "no-" + it.id };
            foreach (var b in new[] { yes, no })
            {
                b.style.width = 80; b.style.height = 28; b.style.color = Ink; b.style.fontSize = 15;
                row.Add(b);
            }
            var comment = new TextField { name = "comment-" + it.id, value = a.comment ?? "" };
            comment.style.flexGrow = 1; comment.style.marginLeft = 8;
            comment.textEdition.placeholder = "comment (optional)";
            comment.RegisterValueChangedCallback(e => { a.comment = e.newValue; Save(); });
            row.Add(comment);
            box.Add(row);

            _buttons[it.id] = (yes, no);
            Paint(it.id, a.answer);
            return box;
        }

        Answer AnswerFor(Item it)
        {
            foreach (var x in _answers) if (x.id == it.id) { x.text = it.text; return x; }
            var a = new Answer { id = it.id, text = it.text };
            _answers.Add(a);
            return a;
        }

        void SetAnswer(Item it, string value)
        {
            var a = AnswerFor(it);
            a.answer = a.answer == value ? "" : value;
            Paint(it.id, a.answer);
            Save();
        }

        void Paint(string id, string answer)
        {
            if (!_buttons.TryGetValue(id, out var b)) return;
            b.yes.style.backgroundColor = answer == "yes" ? YesOn : Off;
            b.no.style.backgroundColor = answer == "no" ? NoOn : Off;
        }

        public void Toggle()
        {
            if (_panel == null) return;
            bool show = _panel.style.display != DisplayStyle.Flex;
            Ui.Show(_panel, show);
            if (!show) Save();
        }

        void LoadAnswers()
        {
            _answers.Clear();
            try
            {
                if (!File.Exists(AnswersPath)) return;
                var f = JsonUtility.FromJson<AnswersFile>(File.ReadAllText(AnswersPath));
                if (f?.answers != null) _answers.AddRange(f.answers);
            }
            catch (Exception e) { Debug.LogWarning(Log.Prefix + "playtest answers unreadable: " + e.Message); }
        }

        public void Save()
        {
            if (_items == null) return;
            try
            {
                BrPaths.EnsureDir(BrPaths.PlaytestDir);
                var f = new AnswersFile
                {
                    build = BuildInfo.Current.Id,
                    milestone = _items.milestone,
                    savedUtc = DateTime.UtcNow.ToString("o"),
                    answers = _answers.ToArray(),
                };
                File.WriteAllText(AnswersPath, JsonUtility.ToJson(f, true));
                if (_status != null) _status.text = "Saved to " + AnswersPath;
            }
            catch (Exception e)
            {
                if (_status != null) _status.text = "Could not save: " + e.Message;
                Debug.LogWarning(Log.Prefix + "playtest answers not saved: " + e.Message);
            }
        }
    }
}
