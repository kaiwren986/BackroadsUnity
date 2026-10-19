using UnityEngine;
using UnityEngine.UIElements;

namespace Backroads.Client.UI
{
    /// <summary>Shared look of the M2 screens (dark panels, light text), built in code like the M1 overlay.</summary>
    static class Ui
    {
        public static readonly Color Panel = new Color(0.08f, 0.09f, 0.10f, 0.93f);
        public static readonly Color Ink = new Color(0.93f, 0.93f, 0.90f);
        public static readonly Color Dim = new Color(0.65f, 0.66f, 0.64f);
        public static readonly Color Accent = new Color(0.98f, 0.76f, 0.30f);
        public static readonly Color Hot = new Color(0.95f, 0.35f, 0.25f);
        public static readonly Color Good = new Color(0.45f, 0.85f, 0.45f);
        public static readonly Color Button = new Color(0.20f, 0.21f, 0.23f);

        public static VisualElement PanelBox(string name, float radius = 6)
        {
            var p = new VisualElement { name = name };
            var s = p.style;
            s.position = Position.Absolute;
            s.backgroundColor = Panel;
            s.paddingLeft = 18; s.paddingRight = 18; s.paddingTop = 14; s.paddingBottom = 14;
            s.borderTopLeftRadius = radius; s.borderTopRightRadius = radius; s.borderBottomLeftRadius = radius; s.borderBottomRightRadius = radius;
            s.display = DisplayStyle.None;
            return p;
        }

        public static Label Text(string text, int size, Color color, bool bold = false)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.style.fontSize = size;
            l.style.color = color;
            l.style.whiteSpace = WhiteSpace.Normal;
            if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
            return l;
        }

        public static Button Btn(string text, string name, System.Action onClick, int size = 17)
        {
            var b = new Button(onClick) { text = text, name = name };
            b.style.fontSize = size; b.style.height = 34; b.style.marginTop = 4;
            b.style.color = Ink;
            b.style.backgroundColor = Button;
            return b;
        }

        public static Label Outlined(string text, int size, Color color)
        {
            var l = Text(text, size, color);
            l.style.unityTextOutlineColor = new Color(0f, 0f, 0f, 0.65f);
            l.style.unityTextOutlineWidth = 0.15f;
            return l;
        }

        public static void Show(VisualElement e, bool on)
        {
            if (e == null) return;
            if (!on)
            {
                // a hidden button must not keep the focus: gamepad A (handbrake) is also UI "submit"
                var f = e.focusController?.focusedElement as VisualElement;
                if (f != null && (f == e || e.Contains(f))) f.Blur();
            }
            e.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public static bool IsShown(VisualElement e) => e != null && e.style.display == DisplayStyle.Flex;
    }
}
