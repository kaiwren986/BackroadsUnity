using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Backroads.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Build.Editor
{
    /// <summary>
    /// Renders the complete Synty vehicles (no loose parts) from a three-quarter view into numbered contact sheets,
    /// artifacts/synty_preview_(pack).png, plus synty_preview.txt with the number -> prefab name list. Looking only:
    /// nothing in the packs is changed (PLAN §4.7). Needs a GPU, so run it without -nographics:
    ///   Unity -batchmode -quit -projectPath Game -executeMethod Backroads.Build.Editor.SyntyPreview.Vehicles
    /// </summary>
    public static class SyntyPreview
    {
        const int TileW = 480, TileH = 300, Cols = 4;

        static readonly string[] PartWords =
        {
            "Bumper", "Spoiler", "Wheel_", "Skirt", "Bonnet", "Boot_", "Exhaust", "Engine_", "Seat", "Roof_Scoop", "Undercarriage",
            "Halo", "Air_Box", "Fairing", "Netting", "HitchCover", "RollCage", "ScreenBanner", "Attach", "_Body_", "TrailerAxle",
            "Steering_Wheel", "Fender",
        };

        public static void Vehicles()
        {
            int code = 0;
            try
            {
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) throw new Exception("no GPU: run without -nographics");
                var index = new StringBuilder();
                foreach (var folder in SyntyTools.PackFolders().Distinct())
                {
                    var paths = AssetDatabase.FindAssets("t:Prefab SM_Veh", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath)
                        .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith("SM_Veh", StringComparison.Ordinal))
                        .Where(p => !PartWords.Any(w => Path.GetFileNameWithoutExtension(p).Contains(w)))
                        .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.Ordinal).ToList();
                    if (paths.Count == 0) continue;
                    var pack = folder.Substring(folder.LastIndexOf('/') + 1);
                    var outPath = Path.Combine(RepoPaths.Artifacts, $"synty_preview_{pack}.png");
                    Sheet(paths, outPath);
                    index.Append(pack).Append(" -> ").Append(Path.GetFileName(outPath)).Append('\n');
                    for (int i = 0; i < paths.Count; i++) index.Append($"  {i + 1,3}  {Path.GetFileNameWithoutExtension(paths[i])}\n");
                    Log.Info($"SyntyPreview: {paths.Count} vehicles -> {outPath}");
                }
                File.WriteAllText(Path.Combine(RepoPaths.Artifacts, "synty_preview.txt"), index.ToString().Replace("\r", ""), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Error("SyntyPreview failed: " + e);
                code = 1;
            }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        static void Sheet(List<string> paths, string outPath)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.68f, 0.78f);
            RenderSettings.ambientEquatorColor = new Color(0.50f, 0.50f, 0.50f);
            RenderSettings.ambientGroundColor = new Color(0.28f, 0.26f, 0.24f);

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.6f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

            var cam = new GameObject("Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.80f, 0.82f, 0.84f);
            cam.fieldOfView = 30f;
            var rt = new RenderTexture(TileW, TileH, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;

            int rows = (paths.Count + Cols - 1) / Cols;
            var sheet = new Texture2D(Cols * TileW, rows * TileH, TextureFormat.RGB24, false);
            var bg = Enumerable.Repeat(new Color32(40, 42, 46, 255), sheet.width * sheet.height).ToArray();
            sheet.SetPixels32(bg);
            var tile = new Texture2D(TileW, TileH, TextureFormat.RGB24, false);

            for (int i = 0; i < paths.Count; i++)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(paths[i]);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var rs = go.GetComponentsInChildren<Renderer>().Where(r => r.enabled && !(r is ParticleSystemRenderer)).ToList();
                if (rs.Count > 0)
                {
                    var b = rs[0].bounds;
                    foreach (var r in rs) b.Encapsulate(r.bounds);
                    float radius = b.extents.magnitude;
                    float dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 0.85f;
                    var dir = Quaternion.Euler(18f, 145f, 0f) * Vector3.back; // from the centre towards the camera
                    cam.transform.position = b.center + dir * dist;
                    cam.transform.LookAt(b.center);
                    cam.nearClipPlane = Mathf.Max(0.01f, dist - radius * 2f);
                    cam.farClipPlane = dist + radius * 2f;
                    cam.Render();
                    RenderTexture.active = rt;
                    tile.ReadPixels(new Rect(0, 0, TileW, TileH), 0, 0);
                    tile.Apply();
                    RenderTexture.active = null;
                }
                UnityEngine.Object.DestroyImmediate(go);

                int col = i % Cols, row = rows - 1 - i / Cols; // number 1 at the top left
                sheet.SetPixels(col * TileW, row * TileH, TileW, TileH, tile.GetPixels());
                DrawNumber(sheet, col * TileW + 10, row * TileH + TileH - 10, i + 1);
            }
            sheet.Apply();
            File.WriteAllBytes(outPath, sheet.EncodeToPNG());
            cam.targetTexture = null;
            rt.Release();
        }

        // 3x5 digits, drawn 5x scaled with a dark backing, top-left corner at (x, yTop).
        static readonly string[] Digits =
        {
            "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
            "111100111001111", "111100111101111", "111001001001001", "111101111101111", "111101111001111",
        };

        static void DrawNumber(Texture2D t, int x, int yTop, int n)
        {
            const int S = 5;
            var s = n.ToString();
            int w = s.Length * 4 * S + S, h = 7 * S;
            for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++) t.SetPixel(x + xx, yTop - yy, new Color(0f, 0f, 0f, 1f) * 0.75f);
            for (int c = 0; c < s.Length; c++)
            {
                var g = Digits[s[c] - '0'];
                for (int gy = 0; gy < 5; gy++)
                    for (int gx = 0; gx < 3; gx++)
                        if (g[gy * 3 + gx] == '1')
                            for (int py = 0; py < S; py++)
                                for (int px = 0; px < S; px++)
                                    t.SetPixel(x + S + c * 4 * S + gx * S + px, yTop - S - gy * S - py, Color.white);
            }
        }
    }
}
