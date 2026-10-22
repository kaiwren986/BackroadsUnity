using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Art.Editor
{
    /// <summary>
    /// Card T3.4 step 1: measures, in Unity and numbers only, what makes the Synty City buildings read as one family
    /// (sizes, triangles, materials, the palette's saturation and value ranges, window, door and trim sizes, storey
    /// pitch), so the style guide (art/buildings/style-guide.md) can write the numbers down and our own palette can be
    /// drawn to match. AGENTS.md rule 3: this only reads the imported prefabs, meshes and textures; it writes no mesh,
    /// changes nothing in the pack folders, and its output stays local (artifacts/art/synty-style.json, gitignored).
    ///   art\pipeline\unity-art.ps1 -Steps synty-style
    /// </summary>
    public static class SyntyStyleAudit
    {
        public const string CityBuildings = "Assets/Synty/PolygonCity/Prefabs/Buildings";

        sealed class Patch
        {
            public Color colour; public Vector3 normal; public float area;
            public Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            public float width, height;   // in-plane extents: horizontal along the wall and vertical (vertical patches)
            public float bottom;          // lowest point above the prefab's floor (m)
        }

        sealed class Stat
        {
            readonly List<(float v, float w)> xs = new List<(float, float)>();
            public void Add(float v, float w = 1f) { if (!float.IsNaN(v) && w > 0f) xs.Add((v, w)); }
            public int Count => xs.Count;
            public float P(float q)
            {
                if (xs.Count == 0) return float.NaN;
                var s = xs.OrderBy(x => x.v).ToList();
                float total = s.Sum(x => x.w), acc = 0f;
                foreach (var x in s) { acc += x.w; if (acc >= q * total) return x.v; }
                return s[s.Count - 1].v;
            }
            public string Json(string unit = "") =>
                Count == 0 ? "{\"n\": 0}" :
                string.Format(CultureInfo.InvariantCulture, "{{\"n\": {0}, \"p10\": {1:0.###}, \"p25\": {2:0.###}, \"p50\": {3:0.###}, \"p75\": {4:0.###}, \"p90\": {5:0.###}{6}}}",
                    Count, P(0.1f), P(0.25f), P(0.5f), P(0.75f), P(0.9f), unit.Length > 0 ? ", \"unit\": \"" + unit + "\"" : "");
        }

        static readonly Dictionary<string, Texture2D> TexCache = new Dictionary<string, Texture2D>();

        /// <summary>CPU copy of a texture, decoded from the file (Synty's import settings stay as they are).</summary>
        static Texture2D Cpu(Texture t)
        {
            if (t == null) return null;
            var path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return null;
            if (TexCache.TryGetValue(path, out var c)) return c;
            Texture2D tex = null;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(File.ReadAllBytes(path))) tex = null;
            }
            TexCache[path] = tex;
            return tex;
        }

        static Texture MainTex(Material m)
        {
            if (m == null) return null;
            // Synty's Generic_Basic shader graph names its colour map _Albedo_Map; URP Lit uses _BaseMap
            foreach (var p in new[] { "_Albedo_Map", "_BaseMap", "_MainTex" })
                if (m.HasProperty(p) && m.GetTexture(p) != null) return m.GetTexture(p);
            return null;
        }

        static Color Sample(Texture2D t, Vector2 uv, Color tint)
        {
            if (t == null) return tint;
            float u = uv.x - Mathf.Floor(uv.x), v = uv.y - Mathf.Floor(uv.y);
            int x = Mathf.Clamp((int)(u * t.width), 0, t.width - 1), y = Mathf.Clamp((int)(v * t.height), 0, t.height - 1);
            return t.GetPixel(x, y) * tint;
        }

        static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c).ToLowerInvariant();

        static int Find(int[] p, int i) { while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; } return i; }

        public static string Run(string outPath)
        {
            var guids = AssetDatabase.FindAssets("t:Prefab", new[] { CityBuildings });
            if (guids.Length == 0) throw new Exception("no prefabs under " + CityBuildings + " (import POLYGON City into this worktree first)");
            var sat = new Stat(); var val = new Stat(); var hueSat = new Stat();
            var winW = new Stat(); var winH = new Stat(); var winBottom = new Stat();
            var doorW = new Stat(); var doorH = new Stat();
            var bandH = new Stat(); var ledgeDepth = new Stat(); var storeyPitch = new Stat();
            var colourArea = new Dictionary<int, float>();
            var windowColours = new Dictionary<int, (float area, int n)>();
            var prefabs = new StringBuilder();
            int totalTris = 0, totalPrefabs = 0;
            var textures = new SortedDictionary<string, string>(StringComparer.Ordinal);

            foreach (var g in guids.OrderBy(x => AssetDatabase.GUIDToAssetPath(x), StringComparer.Ordinal))
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) continue;
                var toRoot = go.transform.worldToLocalMatrix;
                var patches = new List<Patch>();
                int tris = 0;
                var mats = new SortedSet<string>(StringComparer.Ordinal);
                var bounds = new Bounds(); bool any = false;
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                {
                    var mesh = mf.sharedMesh; var mr = mf.GetComponent<MeshRenderer>();
                    if (mesh == null || mr == null) continue;
                    var m = toRoot * mf.transform.localToWorldMatrix;
                    using (var arr = Mesh.AcquireReadOnlyMeshData(mesh))
                    {
                        var data = arr[0];
                        var pos = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
                        data.GetVertices(pos);
                        var uvs = new NativeArray<Vector2>(data.vertexCount, Allocator.Temp);
                        bool hasUv = data.HasVertexAttribute(VertexAttribute.TexCoord0);
                        if (hasUv) data.GetUVs(0, uvs);
                        var w = new Vector3[pos.Length];
                        for (int i = 0; i < pos.Length; i++) { w[i] = m.MultiplyPoint3x4(pos[i]); if (!any) { bounds = new Bounds(w[i], Vector3.zero); any = true; } else bounds.Encapsulate(w[i]); }
                        for (int sm = 0; sm < data.subMeshCount && sm < mr.sharedMaterials.Length; sm++)
                        {
                            var mat = mr.sharedMaterials[sm];
                            if (mat != null) mats.Add(mat.name);
                            var tex = Cpu(MainTex(mat));
                            if (tex != null) textures[AssetDatabase.GetAssetPath(MainTex(mat))] = tex.width + "x" + tex.height;
                            var tint = mat != null && mat.HasProperty("_BaseColor") ? mat.GetColor("_BaseColor") : Color.white;
                            var idx = new NativeArray<int>(data.GetSubMesh(sm).indexCount, Allocator.Temp);
                            data.GetIndices(idx, sm);
                            int nt = idx.Length / 3;
                            tris += nt;
                            // planar patches: triangles sharing a vertex index and a normal (Synty splits vertices at hard edges)
                            var parent = Enumerable.Range(0, nt).ToArray();
                            var nrm = new Vector3[nt]; var area = new float[nt]; var col = new Color[nt];
                            var byVert = new Dictionary<int, int>();
                            for (int t = 0; t < nt; t++)
                            {
                                Vector3 a = w[idx[3 * t]], b = w[idx[3 * t + 1]], c = w[idx[3 * t + 2]];
                                var cr = Vector3.Cross(b - a, c - a);
                                area[t] = cr.magnitude * 0.5f; nrm[t] = cr.sqrMagnitude > 0 ? cr.normalized : Vector3.up;
                                var uv = hasUv ? (uvs[idx[3 * t]] + uvs[idx[3 * t + 1]] + uvs[idx[3 * t + 2]]) / 3f : Vector2.zero;
                                col[t] = Sample(tex, uv, tint);
                                for (int k = 0; k < 3; k++)
                                {
                                    int v = idx[3 * t + k];
                                    if (byVert.TryGetValue(v, out var o) && Vector3.Dot(nrm[o], nrm[t]) > 0.99f) parent[Find(parent, t)] = Find(parent, o);
                                    else byVert[v] = t;
                                }
                            }
                            var map = new Dictionary<int, Patch>();
                            for (int t = 0; t < nt; t++)
                            {
                                int r = Find(parent, t);
                                if (!map.TryGetValue(r, out var p)) { map[r] = p = new Patch { colour = col[t], normal = nrm[t] }; patches.Add(p); }
                                p.area += area[t];
                                for (int k = 0; k < 3; k++) { var q = w[idx[3 * t + k]]; p.min = Vector3.Min(p.min, q); p.max = Vector3.Max(p.max, q); }
                                Color.RGBToHSV(col[t], out var hh, out var ss, out var vv);
                                sat.Add(ss, area[t]); val.Add(vv, area[t]);
                                if (ss > 0.12f) hueSat.Add(hh * 360f, area[t]);
                                int key = ((int)(col[t].r * 31) << 10) | ((int)(col[t].g * 31) << 5) | (int)(col[t].b * 31);
                                colourArea.TryGetValue(key, out var ca); colourArea[key] = ca + area[t];
                            }
                            idx.Dispose();
                        }
                        pos.Dispose(); uvs.Dispose();
                    }
                }
                if (!any) continue;
                totalPrefabs++; totalTris += tris;
                float floor = bounds.min.y;
                foreach (var p in patches)
                {
                    var size = p.max - p.min;
                    p.bottom = p.min.y - floor;
                    if (Mathf.Abs(p.normal.y) < 0.1f)
                    {
                        p.height = size.y;
                        p.width = Mathf.Sqrt(size.x * size.x + size.z * size.z);
                        int key = ((int)(p.colour.r * 31) << 10) | ((int)(p.colour.g * 31) << 5) | (int)(p.colour.b * 31);
                        bool windowSized = p.width >= 0.4f && p.width <= 3.5f && p.height >= 0.6f && p.height <= 3.5f && p.area >= 0.8f * p.width * p.height;
                        if (windowSized)
                        {
                            windowColours.TryGetValue(key, out var wc);
                            windowColours[key] = (wc.area + p.area, wc.n + 1);
                        }
                        if (p.width >= 1f && p.height >= 0.04f && p.height <= 0.6f) bandH.Add(p.height);
                    }
                    else if (Mathf.Abs(p.normal.y) > 0.9f)
                    {
                        float d = Mathf.Min(size.x, size.z), l = Mathf.Max(size.x, size.z);
                        if (l >= 1f && d >= 0.03f && d <= 0.6f && p.bottom > 0.3f) ledgeDepth.Add(d);
                    }
                }
                // window panes: the glass swatch is picked below from the window-sized colour clusters, so the panes of
                // each prefab are resolved after the loop
                prefabs.Append(prefabs.Length == 0 ? "\n    " : ",\n    ");
                prefabs.AppendFormat(CultureInfo.InvariantCulture,
                    "{{\"name\": \"{0}\", \"tris\": {1}, \"materials\": [{2}], \"size\": [{3:0.###}, {4:0.###}, {5:0.###}], \"patches\": {6}}}",
                    go.name, tris, string.Join(", ", mats.Select(x => "\"" + x + "\"")), bounds.size.x, bounds.size.y, bounds.size.z, patches.Count);
                Pending.Add((go.name, patches, floor));
            }

            // the glass colour: the window-sized vertical colour cluster with the most patches whose colour is bluish or dark
            int glass = windowColours.OrderByDescending(kv => kv.Value.n).Select(kv => kv.Key)
                .FirstOrDefault(k => { var c = Unpack(k); Color.RGBToHSV(c, out var h, out var s, out var v); return (h > 0.5f && h < 0.7f && s > 0.15f) || v < 0.3f; });
            foreach (var (name, patches, floor) in Pending)
            {
                var bottoms = new List<float>();
                foreach (var p in patches)
                {
                    if (Mathf.Abs(p.normal.y) >= 0.1f) continue;
                    int key = ((int)(p.colour.r * 31) << 10) | ((int)(p.colour.g * 31) << 5) | (int)(p.colour.b * 31);
                    if (key != glass || p.width < 0.3f || p.height < 0.5f) continue;
                    if (p.bottom < 0.35f && p.height >= 1.9f && p.height <= 3.2f) { doorW.Add(p.width); doorH.Add(p.height); continue; }
                    winW.Add(p.width); winH.Add(p.height); winBottom.Add(p.bottom); bottoms.Add(p.bottom);
                }
                bottoms.Sort();
                var rows = new List<float>();
                foreach (var b in bottoms) if (rows.Count == 0 || b - rows[rows.Count - 1] > 0.5f) rows.Add(b);
                for (int i = 1; i < rows.Count; i++) { float d = rows[i] - rows[i - 1]; if (d >= 2.4f && d <= 6f) storeyPitch.Add(d); }
            }
            Pending.Clear();

            var top = colourArea.OrderByDescending(kv => kv.Value).Take(32).ToList();
            float allArea = colourArea.Values.Sum();
            var sb = new StringBuilder();
            sb.Append("{\n  \"note\": \"Numbers measured from POLYGON City building prefabs in Unity (SyntyStyleAudit, card T3.4 step 1). Local only: never committed or uploaded (AGENTS.md rule 3).\",\n");
            sb.AppendFormat(CultureInfo.InvariantCulture, "  \"unity\": \"{0}\",\n  \"folder\": \"{1}\",\n  \"prefabCount\": {2},\n  \"trianglesTotal\": {3},\n", Application.unityVersion, CityBuildings, totalPrefabs, totalTris);
            sb.Append("  \"textures\": {").Append(string.Join(", ", textures.Select(kv => "\"" + Path.GetFileName(kv.Key) + "\": \"" + kv.Value + "\""))).Append("},\n");
            sb.Append("  \"saturation\": ").Append(sat.Json()).Append(",\n");
            sb.Append("  \"value\": ").Append(val.Json()).Append(",\n");
            sb.Append("  \"hueOfSaturated\": ").Append(hueSat.Json("deg")).Append(",\n");
            sb.Append("  \"glassColour\": \"").Append(Hex(Unpack(glass))).Append("\",\n");
            sb.Append("  \"windowWidth\": ").Append(winW.Json("m")).Append(",\n");
            sb.Append("  \"windowHeight\": ").Append(winH.Json("m")).Append(",\n");
            sb.Append("  \"windowSillAboveFloor\": ").Append(winBottom.Json("m")).Append(",\n");
            sb.Append("  \"doorWidth\": ").Append(doorW.Json("m")).Append(",\n");
            sb.Append("  \"doorHeight\": ").Append(doorH.Json("m")).Append(",\n");
            sb.Append("  \"storeyPitchFromWindowRows\": ").Append(storeyPitch.Json("m")).Append(",\n");
            sb.Append("  \"horizontalBandHeight\": ").Append(bandH.Json("m")).Append(",\n");
            sb.Append("  \"ledgeDepth\": ").Append(ledgeDepth.Json("m")).Append(",\n");
            sb.Append("  \"windowSizedColourClusters\": [").Append(string.Join(", ", windowColours.OrderByDescending(kv => kv.Value.n).Take(10)
                .Select(kv => string.Format(CultureInfo.InvariantCulture, "{{\"colour\": \"{0}\", \"patches\": {1}, \"area\": {2:0.#}}}", Hex(Unpack(kv.Key)), kv.Value.n, kv.Value.area)))).Append("],\n");
            sb.Append("  \"topColoursByArea\": [").Append(string.Join(", ", top.Select(kv =>
            {
                var c = Unpack(kv.Key); Color.RGBToHSV(c, out var h, out var s, out var v);
                return string.Format(CultureInfo.InvariantCulture, "{{\"colour\": \"{0}\", \"share\": {1:0.###}, \"h\": {2:0}, \"s\": {3:0.##}, \"v\": {4:0.##}}}", Hex(c), kv.Value / allArea, h * 360f, s, v);
            }))).Append("],\n");
            sb.Append("  \"prefabs\": [").Append(prefabs).Append("\n  ]\n}\n");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            File.WriteAllText(outPath, sb.ToString().Replace("\r", ""), new UTF8Encoding(false));
            foreach (var t in TexCache.Values) if (t != null) UnityEngine.Object.DestroyImmediate(t);
            TexCache.Clear();
            return outPath;
        }

        static readonly List<(string name, List<Patch> patches, float floor)> Pending = new List<(string, List<Patch>, float)>();

        static Color Unpack(int k) => new Color(((k >> 10) & 31) / 31f, ((k >> 5) & 31) / 31f, (k & 31) / 31f);
    }
}
