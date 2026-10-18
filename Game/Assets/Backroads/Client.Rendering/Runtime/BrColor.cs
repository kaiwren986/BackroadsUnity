using UnityEngine;

namespace Backroads.Client.Rendering
{
    /// <summary>
    /// Colour conversions between three.js r170 (colour-managed: hex constants are sRGB, maths linear) and Unity
    /// materials (Material.SetColor takes sRGB colours and linearises them in a Linear project).
    /// </summary>
    public static class BrColor
    {
        /// <summary>0xRRGGBB as an sRGB colour for Material.SetColor.</summary>
        public static Color Hex(int hex) => new Color(((hex >> 16) & 255) / 255f, ((hex >> 8) & 255) / 255f, (hex & 255) / 255f, 1f);

        /// <summary>three.js Color.getHex() of a linear colour, as an sRGB colour: what the merged JS buckets used.</summary>
        public static Color LinearToHex(Color linear)
        {
            static float Q(float c)
            {
                float s = c < 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 0.41666f) - 0.055f;
                return Mathf.Round(Mathf.Clamp(s * 255f, 0f, 255f)) / 255f;
            }
            return new Color(Q(linear.r), Q(linear.g), Q(linear.b), 1f);
        }

        public static Color Of(Color32 c) => new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);

        /// <summary>sRGB hex to linear (three's SRGBToLinear), for vertex streams and shader globals.</summary>
        public static Color HexLinear(int hex) => Hex(hex).linear;
    }
}
