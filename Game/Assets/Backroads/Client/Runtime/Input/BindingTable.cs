using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Backroads.Client
{
    /// <summary>One row of Content/core/bindings.json (ADR-004).</summary>
    [Serializable]
    public sealed class BindingEntry
    {
        public string action;
        /// <summary>button, hold, value, axis, vector, delta or scroll.</summary>
        public string kind;
        public string[] keys = new string[0];
        public string[] pad = new string[0];
        public string[] negative = new string[0], positive = new string[0];
        public string[] up = new string[0], down = new string[0], left = new string[0], right = new string[0];
        /// <summary>The action that holding this binding for holdSeconds triggers (a tap triggers this entry's action).</summary>
        public string hold;
        /// <summary>"menu": a gamepad reaches this action through the Start menu.</summary>
        public string padVia;
        public string help;
        public string since;

        public IEnumerable<string> KeyboardControls => (keys ?? new string[0]).Concat(negative ?? new string[0]).Concat(positive ?? new string[0])
            .Concat(up ?? new string[0]).Concat(down ?? new string[0]).Concat(left ?? new string[0]).Concat(right ?? new string[0]);

        public IEnumerable<string> PadControls => pad ?? new string[0];

        public IEnumerable<string> AllControls => KeyboardControls.Concat(PadControls);
    }

    [Serializable]
    public sealed class BindingMap
    {
        public string name;
        public BindingEntry[] bindings = new BindingEntry[0];
    }

    /// <summary>A user's change to one entry's keyboard or gamepad controls (settings.json).</summary>
    [Serializable]
    public sealed class BindingOverride
    {
        public string map;
        public string action;
        /// <summary>"keys" or "pad".</summary>
        public string slot;
        public string[] paths = new string[0];
    }

    /// <summary>
    /// T2.6: the control table Content/core/bindings.json, frozen with ADR-004: one table for keyboard, mouse and
    /// gamepad in three maps (Driving, OnFoot, UI). The Input System actions are generated from it (CreateActions);
    /// Problems() is what Input.NoDuplicateBindings checks.
    /// </summary>
    [Serializable]
    public sealed class BindingTable
    {
        public const int CurrentVersion = 1;
        public const string ResourceName = "BrClientConfig";
        public static readonly string[] Kinds = { "button", "hold", "value", "axis", "vector", "delta", "scroll" };
        public static readonly string[] MapNames = { "Driving", "OnFoot", "UI" };

        public int version;
        public string note;
        public float deadzone = 0.14f;
        public float holdSeconds = 0.3f;
        public BindingMap[] maps = new BindingMap[0];

        public static BindingTable Parse(string json)
        {
            var t = JsonUtility.FromJson<BindingTable>(json);
            if (t == null) throw new FormatException("bindings.json: not a JSON object");
            t.maps ??= new BindingMap[0];
            foreach (var m in t.maps)
            {
                m.bindings ??= new BindingEntry[0];
                foreach (var b in m.bindings)
                {
                    b.keys ??= new string[0]; b.pad ??= new string[0];
                    b.negative ??= new string[0]; b.positive ??= new string[0];
                    b.up ??= new string[0]; b.down ??= new string[0]; b.left ??= new string[0]; b.right ??= new string[0];
                    if (string.IsNullOrEmpty(b.hold)) b.hold = null;
                    if (string.IsNullOrEmpty(b.padVia)) b.padVia = null;
                }
            }
            return t;
        }

        /// <summary>The table the game uses: Resources/BrClientConfig's reference to Content/core/bindings.json.</summary>
        public static BindingTable LoadDefault(out string error)
        {
            error = null;
            try
            {
                var cfg = Resources.Load<ClientConfig>(ResourceName);
                if (cfg != null && cfg.bindings != null) return Parse(cfg.bindings.text);
#if UNITY_EDITOR
                var path = Path.Combine(Application.dataPath, "Content", "core", "bindings.json");
                if (File.Exists(path)) return Parse(File.ReadAllText(path));
#endif
                error = $"Resources/{ResourceName} (with Content/core/bindings.json) is missing: run Backroads > Build Millbrook scene";
            }
            catch (Exception e) { error = "bindings.json: " + e.Message; }
            return null;
        }

        public BindingMap Map(string name) => maps?.FirstOrDefault(m => m.name == name);

        public static string Normalize(string path) => (path ?? "").Trim().ToLowerInvariant();

        /// <summary>Applies settings.json overrides; entries are matched by map, action and slot (keys or pad).</summary>
        public BindingTable WithOverrides(IEnumerable<BindingOverride> overrides)
        {
            var copy = Parse(JsonUtility.ToJson(this));
            if (overrides == null) return copy;
            foreach (var o in overrides)
            {
                var m = copy.Map(o.map);
                var e = m?.bindings.FirstOrDefault(b => b.action == o.action && (o.slot == "pad" ? b.pad?.Length > 0 : b.keys?.Length > 0));
                if (e == null) continue;
                if (o.slot == "pad") e.pad = o.paths ?? new string[0];
                else e.keys = o.paths ?? new string[0];
            }
            return copy;
        }

        /// <summary>
        /// Everything Input.NoDuplicateBindings rejects: a control bound twice in one map; a control shared by the UI map
        /// and Driving or OnFoot (they are on together); unknown kinds or hold targets; a Driving or OnFoot action without
        /// a keyboard/mouse route or without a gamepad route (direct, or by holding another binding); a UI action with no
        /// gamepad route and no padVia.
        /// </summary>
        public List<string> Problems()
        {
            var bad = new List<string>();
            if (version != CurrentVersion) bad.Add($"version {version}, expected {CurrentVersion}");
            foreach (var name in MapNames) if (Map(name) == null) bad.Add("missing map " + name);
            var actionsByMap = new Dictionary<string, HashSet<string>>();
            foreach (var m in maps ?? new BindingMap[0])
            {
                var seen = new Dictionary<string, string>();
                actionsByMap[m.name] = new HashSet<string>((m.bindings ?? new BindingEntry[0]).Select(b => b.action));
                foreach (var b in m.bindings ?? new BindingEntry[0])
                {
                    if (string.IsNullOrEmpty(b.action)) bad.Add($"{m.name}: an entry without an action");
                    if (Array.IndexOf(Kinds, b.kind) < 0) bad.Add($"{m.name}.{b.action}: unknown kind '{b.kind}'");
                    foreach (var c in b.AllControls)
                    {
                        var n = Normalize(c);
                        if (!n.StartsWith("<keyboard>/") && !n.StartsWith("<mouse>/") && !n.StartsWith("<gamepad>/"))
                            bad.Add($"{m.name}.{b.action}: control '{c}' is not a keyboard, mouse or gamepad path");
                        if (seen.TryGetValue(n, out var other)) bad.Add($"{m.name}: '{c}' is bound twice ({other} and {b.action})");
                        else seen[n] = b.action;
                    }
                }
            }
            foreach (var m in maps ?? new BindingMap[0])
                foreach (var b in m.bindings ?? new BindingEntry[0])
                    if (!string.IsNullOrEmpty(b.hold) && !maps.Any(mm => mm.bindings.Any(x => x.action == b.hold)))
                        bad.Add($"{m.name}.{b.action}: hold target '{b.hold}' is no action");

            // UI is on together with Driving or OnFoot: no shared controls
            var ui = Map("UI");
            if (ui != null)
            {
                var uiControls = new HashSet<string>(ui.bindings.SelectMany(b => b.AllControls).Select(Normalize));
                foreach (var mode in new[] { "Driving", "OnFoot" })
                {
                    var m = Map(mode);
                    if (m == null) continue;
                    foreach (var b in m.bindings)
                        foreach (var c in b.AllControls)
                            if (uiControls.Contains(Normalize(c))) bad.Add($"'{c}' is in UI and in {mode}.{b.action}");
                }
            }

            // routes
            foreach (var m in maps ?? new BindingMap[0])
            {
                var all = maps.SelectMany(mm => mm.bindings).ToList();
                foreach (var action in m.bindings.Select(b => b.action).Distinct())
                {
                    var own = m.bindings.Where(b => b.action == action).ToList();
                    bool viaHold(Func<BindingEntry, bool> has) => all.Any(x => x.hold == action && has(x));
                    bool kb = own.Any(b => b.KeyboardControls.Any()) || viaHold(x => x.KeyboardControls.Any());
                    bool gp = own.Any(b => b.PadControls.Any()) || viaHold(x => x.PadControls.Any());
                    bool via = own.Any(b => b.padVia == "menu");
                    if (m.name == "UI")
                    {
                        if (!kb && !gp) bad.Add($"UI.{action}: no control");
                        if (!gp && !via) bad.Add($"UI.{action}: no gamepad binding and no padVia");
                    }
                    else
                    {
                        if (!kb) bad.Add($"{m.name}.{action}: no keyboard or mouse binding");
                        if (!gp) bad.Add($"{m.name}.{action}: no gamepad binding");
                    }
                }
            }
            return bad;
        }

        /// <summary>Input System action name for entry index i of a map and a device class ("k" keyboard/mouse, "p" gamepad).</summary>
        public static string ActionName(string action, int index, char device) => $"{action}.{index}.{device}";

        /// <summary>
        /// Generates the Input System actions: one map per table map, one action per entry and device class (so taps and
        /// holds are tracked per binding). Keyboard axes and vectors become 1D/2D composites.
        /// </summary>
        public InputActionAsset CreateActions()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            asset.name = "BrControls";
            foreach (var m in maps ?? new BindingMap[0])
            {
                var map = asset.AddActionMap(m.name);
                for (int i = 0; i < m.bindings.Length; i++)
                {
                    var b = m.bindings[i];
                    if (b.KeyboardControls.Any()) AddAction(map, b, i, 'k');
                    if (b.PadControls.Any()) AddAction(map, b, i, 'p');
                }
            }
            return asset;
        }

        static void AddAction(InputActionMap map, BindingEntry b, int index, char dev)
        {
            string name = ActionName(b.action, index, dev);
            switch (b.kind)
            {
                case "value":
                    {
                        var a = map.AddAction(name, InputActionType.Value, expectedControlLayout: "Axis");
                        foreach (var p in dev == 'k' ? b.keys : b.pad) a.AddBinding(p);
                        break;
                    }
                case "axis":
                    {
                        var a = map.AddAction(name, InputActionType.Value, expectedControlLayout: "Axis");
                        if (dev == 'k')
                        {
                            foreach (var p in b.keys ?? new string[0]) a.AddBinding(p);
                            int n = Math.Max(b.negative?.Length ?? 0, b.positive?.Length ?? 0);
                            for (int k = 0; k < n; k++)
                            {
                                var c = a.AddCompositeBinding("1DAxis");
                                if (k < b.negative.Length) c.With("Negative", b.negative[k]);
                                if (k < b.positive.Length) c.With("Positive", b.positive[k]);
                            }
                        }
                        else foreach (var p in b.pad) a.AddBinding(p);
                        break;
                    }
                case "vector":
                    {
                        var a = map.AddAction(name, InputActionType.Value, expectedControlLayout: "Vector2");
                        if (dev == 'k')
                        {
                            int n = new[] { b.up?.Length ?? 0, b.down?.Length ?? 0, b.left?.Length ?? 0, b.right?.Length ?? 0 }.Max();
                            for (int k = 0; k < n; k++)
                            {
                                var c = a.AddCompositeBinding("2DVector");
                                if (k < b.up.Length) c.With("Up", b.up[k]);
                                if (k < b.down.Length) c.With("Down", b.down[k]);
                                if (k < b.left.Length) c.With("Left", b.left[k]);
                                if (k < b.right.Length) c.With("Right", b.right[k]);
                            }
                        }
                        else foreach (var p in b.pad) a.AddBinding(p);
                        break;
                    }
                case "delta":
                    {
                        var a = map.AddAction(name, dev == 'k' ? InputActionType.PassThrough : InputActionType.Value, expectedControlLayout: "Vector2");
                        foreach (var p in dev == 'k' ? b.keys : b.pad) a.AddBinding(p);
                        break;
                    }
                case "scroll":
                    {
                        var a = dev == 'k'
                            ? map.AddAction(name, InputActionType.PassThrough, expectedControlLayout: "Axis")
                            : map.AddAction(name, InputActionType.Button);
                        foreach (var p in dev == 'k' ? b.keys : b.pad) a.AddBinding(p);
                        break;
                    }
                default: // button, hold
                    {
                        var a = map.AddAction(name, InputActionType.Button);
                        foreach (var p in dev == 'k' ? b.keys : b.pad) a.AddBinding(p);
                        break;
                    }
            }
        }

        /// <summary>A short readable name for a control path ("Left Shift", "RT", "D-pad left").</summary>
        public static string Pretty(string path)
        {
            var n = Normalize(path);
            int slash = n.IndexOf('/');
            string dev = slash > 0 ? n.Substring(0, slash) : "", ctl = slash > 0 ? n.Substring(slash + 1) : n;
            if (dev == "<gamepad>")
            {
                switch (ctl)
                {
                    case "buttonsouth": return "A";
                    case "buttoneast": return "B";
                    case "buttonwest": return "X";
                    case "buttonnorth": return "Y";
                    case "lefttrigger": return "LT";
                    case "righttrigger": return "RT";
                    case "leftshoulder": return "LB";
                    case "rightshoulder": return "RB";
                    case "select": return "Back";
                    case "start": return "Start";
                    case "leftstick": case "leftstick/x": return "Left stick";
                    case "rightstick": return "Right stick";
                    case "leftstickpress": return "L3";
                    case "rightstickpress": return "R3";
                    case "dpad/up": return "D-pad up";
                    case "dpad/down": return "D-pad down";
                    case "dpad/left": return "D-pad left";
                    case "dpad/right": return "D-pad right";
                }
                return ctl;
            }
            if (dev == "<mouse>")
            {
                switch (ctl)
                {
                    case "delta": return "Mouse";
                    case "scroll/y": return "Mouse wheel";
                    case "leftbutton": return "Left mouse";
                    case "rightbutton": return "Right mouse";
                }
                return "Mouse " + ctl;
            }
            switch (ctl)
            {
                case "space": return "Space";
                case "escape": return "Esc";
                case "leftshift": return "Left Shift";
                case "leftctrl": return "Left Ctrl";
                case "uparrow": return "Up";
                case "downarrow": return "Down";
                case "leftarrow": return "Left";
                case "rightarrow": return "Right";
                case "leftbracket": return "[";
                case "rightbracket": return "]";
            }
            return ctl.Length <= 3 ? ctl.ToUpperInvariant() : char.ToUpperInvariant(ctl[0]) + ctl.Substring(1);
        }
    }
}
