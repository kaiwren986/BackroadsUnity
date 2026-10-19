using Backroads.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>
    /// T2.7 minimal HUD (03 section 4.23, hud.js speedometer): vehicle name with LOW / WINCH / ENGINE OFF tags, km/h,
    /// gear (R, N, 1..), the rpm bar (VehicleHud.Rpm over RedlineRpm, hot above 90 %), the surface and "sunk N cm".
    /// Bottom right, shown while a vehicle is driven and the camera follows it. Tick allocates nothing: the numbers come
    /// from cached strings and an element changes only when what it shows changes.
    /// </summary>
    public sealed class HudView
    {
        const int MaxSpeed = 400, MaxSunk = 500, MaxGear = 12;
        static readonly string[] SpeedText = Numbers(MaxSpeed);
        static readonly string[] SunkText = Sunk();
        static readonly string[] GearText = Gears();

        VisualElement _root, _barFill;
        Label _name, _low, _winch, _off, _speed, _gear, _surface, _sunk;
        int _lastSpeed = -1, _lastGear = int.MinValue, _lastSunk = -1, _lastBar = -1;
        int _lastFlags = -1;
        bool _hot, _shown;
        string _lastSurface, _lastName;

        public bool Visible => _shown;

        /// <summary>Managed bytes allocated inside Tick (GC.GetAllocatedBytesForCurrentThread) and Tick calls, since the last reset.</summary>
        public static long AllocatedBytes { get; private set; }
        public static int Ticks { get; private set; }
        public static void ResetAllocation() { AllocatedBytes = 0; Ticks = 0; }
        /// <summary>What the HUD shows now (tests): speed, gear, surface, sunk and the rpm bar in 0.5 % steps.</summary>
        public string SpeedShown => _speed?.text;
        public string GearShown => _gear?.text;
        public string SurfaceShown => _surface?.text;
        public string SunkShown => _sunk?.text;
        public float RpmBarPercent => _lastBar * 0.5f;

        static string[] Numbers(int n)
        {
            var a = new string[n + 1];
            for (int i = 0; i <= n; i++) a[i] = i.ToString();
            return a;
        }

        static string[] Sunk()
        {
            var a = new string[MaxSunk + 1];
            a[0] = "";
            for (int i = 1; i <= MaxSunk; i++) a[i] = "sunk " + i + " cm";
            return a;
        }

        static string[] Gears()
        {
            var a = new string[MaxGear + 2];
            a[0] = "R"; a[1] = "N";
            for (int g = 1; g <= MaxGear; g++) a[g + 1] = g.ToString();
            return a;
        }

        public VisualElement Build()
        {
            _root = new VisualElement { name = "hud", pickingMode = PickingMode.Ignore };
            var s = _root.style;
            s.position = Position.Absolute; s.right = 24; s.bottom = 56; s.width = 280;
            s.paddingLeft = 14; s.paddingRight = 14; s.paddingTop = 10; s.paddingBottom = 10;
            s.backgroundColor = new Color(0.05f, 0.06f, 0.07f, 0.62f);
            s.borderTopLeftRadius = 8; s.borderTopRightRadius = 8; s.borderBottomLeftRadius = 8; s.borderBottomRightRadius = 8;
            s.display = DisplayStyle.None;

            var top = Row();
            _name = Ui.Text("", 14, Ui.Dim);
            _name.style.flexGrow = 1;
            top.Add(_name);
            _low = Tag("LOW", Ui.Accent); _winch = Tag("WINCH", Ui.Good); _off = Tag("ENGINE OFF", Ui.Hot);
            top.Add(_low); top.Add(_winch); top.Add(_off);
            _root.Add(top);

            var mid = Row();
            mid.style.alignItems = Align.FlexEnd;
            _speed = Ui.Text("0", 46, Ui.Ink, bold: true);
            _speed.name = "hud-speed";
            mid.Add(_speed);
            var kmh = Ui.Text("km/h", 15, Ui.Dim);
            kmh.style.marginBottom = 9; kmh.style.marginLeft = 4; kmh.style.flexGrow = 1;
            mid.Add(kmh);
            _gear = Ui.Text("N", 40, Ui.Accent, bold: true);
            _gear.name = "hud-gear";
            mid.Add(_gear);
            _root.Add(mid);

            var bar = new VisualElement { name = "hud-rpm", pickingMode = PickingMode.Ignore };
            bar.style.height = 8; bar.style.marginTop = 2; bar.style.marginBottom = 6;
            bar.style.backgroundColor = new Color(1f, 1f, 1f, 0.12f);
            _barFill = new VisualElement { name = "hud-rpm-fill", pickingMode = PickingMode.Ignore };
            _barFill.style.height = Length.Percent(100);
            _barFill.style.width = Length.Percent(0);
            _barFill.style.backgroundColor = Ui.Ink;
            bar.Add(_barFill);
            _root.Add(bar);

            var bottom = Row();
            _surface = Ui.Text("", 14, Ui.Ink);
            _surface.name = "hud-surface";
            _surface.style.flexGrow = 1;
            bottom.Add(_surface);
            _sunk = Ui.Text("", 14, Ui.Accent);
            _sunk.name = "hud-sunk";
            bottom.Add(_sunk);
            _root.Add(bottom);
            return _root;
        }

        static VisualElement Row()
        {
            var r = new VisualElement { pickingMode = PickingMode.Ignore };
            r.style.flexDirection = FlexDirection.Row;
            return r;
        }

        static Label Tag(string text, Color c)
        {
            var l = Ui.Text(text, 12, c, bold: true);
            l.style.marginLeft = 6;
            l.style.display = DisplayStyle.None;
            return l;
        }

        public void SetVisible(bool on)
        {
            if (on == _shown || _root == null) return;
            _shown = on;
            _root.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Updates the HUD from the vehicle's VehicleHud (call once per frame).</summary>
        public void Tick(IDrivenVehicle v)
        {
            if (_root == null || v == null) return;
            long a0 = System.GC.GetAllocatedBytesForCurrentThread();
            Update(v);
            AllocatedBytes += System.GC.GetAllocatedBytesForCurrentThread() - a0;
            Ticks++;
        }

        void Update(IDrivenVehicle v)
        {
            var h = v.Hud;
            int speed = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(h.SpeedKmh)), 0, MaxSpeed);
            if (speed != _lastSpeed) { _lastSpeed = speed; _speed.text = SpeedText[speed]; }
            int gear = Mathf.Clamp(h.Gear, -1, MaxGear);
            if (gear != _lastGear) { _lastGear = gear; _gear.text = GearText[gear + 1]; }
            float frac = h.RedlineRpm > 1f ? h.Rpm / h.RedlineRpm : 0f;
            int bar = Mathf.Clamp(Mathf.RoundToInt(frac * 200f), 0, 200);
            if (bar != _lastBar)
            {
                _lastBar = bar;
                _barFill.style.width = Length.Percent(bar * 0.5f);
                bool hot = frac > 0.9f;
                if (hot != _hot) { _hot = hot; _barFill.style.backgroundColor = hot ? Ui.Hot : Ui.Ink; }
            }
            if (!ReferenceEquals(h.Surface, _lastSurface)) { _lastSurface = h.Surface; _surface.text = h.Surface ?? ""; }
            int sunk = Mathf.Clamp(Mathf.RoundToInt(h.SunkCm), 0, MaxSunk);
            if (sunk != _lastSunk) { _lastSunk = sunk; _sunk.text = SunkText[sunk]; }
            var name = v.DisplayName;
            if (!ReferenceEquals(name, _lastName)) { _lastName = name; _name.text = name ?? ""; }
            int flags = (h.LowRange ? 1 : 0) | (h.WinchOn ? 2 : 0) | (h.Ignition ? 0 : 4);
            if (flags != _lastFlags)
            {
                _lastFlags = flags;
                _low.style.display = (flags & 1) != 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _winch.style.display = (flags & 2) != 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _off.style.display = (flags & 4) != 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }
    }
}
