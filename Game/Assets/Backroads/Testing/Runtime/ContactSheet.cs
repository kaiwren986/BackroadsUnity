using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Backroads.Testing
{
    /// <summary>
    /// Composes screenshots into one PNG grid (PLAN 1.9; from M1 the Unity shots sit next to the JS shots).
    /// CPU only (bilinear sampling of readable textures), so it also works in batchmode.
    /// </summary>
    public static class ContactSheet
    {
        public static string Build(IList<string> pngPaths, string outPath, int columns = 4, int thumbWidth = 480)
        {
            var thumbs = new List<Texture2D>();
            int thumbHeight = 0;
            foreach (var p in pngPaths)
            {
                if (!File.Exists(p)) continue;
                var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!src.LoadImage(File.ReadAllBytes(p))) { Object.Destroy(src); continue; }
                int h = Mathf.Max(1, Mathf.RoundToInt(thumbWidth * (float)src.height / src.width));
                thumbHeight = Mathf.Max(thumbHeight, h);
                thumbs.Add(Scale(src, thumbWidth, h));
                Object.Destroy(src);
            }
            if (thumbs.Count == 0) return null;

            const int gap = 8;
            int cols = Mathf.Min(columns, thumbs.Count);
            int rows = (thumbs.Count + cols - 1) / cols;
            int w = cols * thumbWidth + (cols + 1) * gap, hgt = rows * thumbHeight + (rows + 1) * gap;
            var sheet = new Texture2D(w, hgt, TextureFormat.RGBA32, false);
            var bg = new Color32(24, 25, 27, 255);
            var fill = new Color32[w * hgt];
            for (int i = 0; i < fill.Length; i++) fill[i] = bg;
            sheet.SetPixels32(fill);

            for (int i = 0; i < thumbs.Count; i++)
            {
                int c = i % cols, r = i / cols;
                int x = gap + c * (thumbWidth + gap);
                int y = hgt - (gap + (r + 1) * thumbHeight + r * gap); // row 0 at the top
                sheet.SetPixels32(x, y, thumbs[i].width, thumbs[i].height, thumbs[i].GetPixels32());
                Object.Destroy(thumbs[i]);
            }
            sheet.Apply(false);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            File.WriteAllBytes(outPath, sheet.EncodeToPNG());
            Object.Destroy(sheet);
            return outPath;
        }

        static Texture2D Scale(Texture2D src, int w, int h)
        {
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = src.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
            dst.SetPixels(px);
            dst.Apply(false);
            return dst;
        }
    }
}
