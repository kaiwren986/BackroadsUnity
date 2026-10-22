using System;
using System.Collections.Generic;
using System.IO;
using Backroads.World;
using UnityEditor;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// A plan-view debug map of a baked road graph, drawn on the CPU (works in -nographics batchmode): hillshaded heights,
    /// water, building footprints, the plain ribbons by class, the town kerb zone (and a town's downtown zone), nodes by kind
    /// with their numbers, major arms (cyan), approaches that stop (red from a mapped sign, orange by rule), provisional
    /// mouths (yellow) and provisional kerb fillets (white). Seen from above with +x right and +z up (Unity space: for the
    /// canon the JS x axis is mirrored; a town's local frame has x east, z north, so north is up).
    ///   menu: Backroads > Roads > Write debug maps  ->  artifacts/roads/graph-town.png, graph-overview.png (canon) and
    ///         artifacts/roads/(town)/graph-overview.png, graph-village.png (TownData towns)
    /// </summary>
    public static class RoadGraphMap
    {
        /// <summary>What the map draws under the roads.</summary>
        public sealed class Background
        {
            public Func<float, float, float> height;
            /// <summary>Everything below this height is water (canon); towns give water polygons instead.</summary>
            public float waterY = float.NegativeInfinity;
            public readonly List<Vector2[]> water = new List<Vector2[]>();
            public readonly List<Vector2[]> buildings = new List<Vector2[]>();
            /// <summary>Heights over this range above the lowest ground shade from green to grey.</summary>
            public float shadeRange = 40f, baseY;
        }

        [MenuItem("Backroads/Roads/Write debug maps")]
        static void Menu()
        {
            var paths = WriteDefault(out var msg);
            foreach (var town in TownRoadSource.Towns()) paths.AddRange(WriteTown(town, out _));
            foreach (var p in paths) Debug.Log("[BR] wrote " + p);
            if (msg.Length > 0) Debug.LogWarning("[BR] " + msg);
        }

        /// <summary>Writes the town and overview maps for the committed canon asset; returns the paths written.</summary>
        public static List<string> WriteDefault(out string message)
        {
            message = "";
            var paths = new List<string>();
            var asset = RoadNetworkBaker.LoadBaked();
            if (asset == null) { message = "no baked road asset: run Backroads > Roads > Bake road network (R0)"; return paths; }
            if (!Backroads.Canon.Editor.CanonImporter.EnsureImported(out message)) return paths;
            message = "";
            var canon = Backroads.Canon.Editor.CanonImporter.LoadImported();
            var bg = CanonBackground(canon);
            Directory.CreateDirectory(RoadNetworkBaker.ArtifactsDir);
            // town: JS centre (-70, 30) -> Unity (70, 30)
            paths.Add(Write(asset, bg, new Vector2(80f, 90f), 0.25f, 1800, 1800, Path.Combine(RoadNetworkBaker.ArtifactsDir, "graph-town.png"),
                "ROADS R0: JUNCTION GRAPH OVER MILLBROOK (TOWN)", 2, legendTop: true));
            paths.Add(Write(asset, bg, new Vector2(-15f, -40f), 0.5f, 1440, 1240, Path.Combine(RoadNetworkBaker.ArtifactsDir, "graph-overview.png"),
                "ROADS R0: WHOLE NETWORK", 2, legendTop: false));
            return paths;
        }

        /// <summary>
        /// Writes a town's maps for its committed asset: the play area (graph-overview.png) and the village, the kerb zone
        /// plus 40 m (graph-village.png), in artifacts/roads/(town)/.
        /// </summary>
        public static List<string> WriteTown(string townId, out string message)
        {
            message = "";
            var paths = new List<string>();
            var asset = RoadNetworkBaker.LoadBakedTown(townId);
            if (asset == null) { message = "no baked road asset for " + townId + ": run Backroads > Roads > Bake town road networks (R0o)"; return paths; }
            var src = TownRoadSource.LoadTown(townId);
            var bg = TownBackground(src, TownRoadSource.TownDir(townId));
            var dir = RoadNetworkBaker.TownArtifactsDir(townId);
            Directory.CreateDirectory(dir);
            float half = (float)src.playHalf;
            paths.Add(Write(asset, bg, Vector2.zero, 2 * half / 1900f, 1900, 1900, Path.Combine(dir, "graph-overview.png"),
                "ROADS R0O: " + townId.ToUpperInvariant() + " PLAY AREA", 1, legendTop: true, labels: false));
            // the village: the kerb zone's bounding box plus 40 m
            Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = new Vector2(float.MinValue, float.MinValue);
            foreach (var p in asset.townZone) { lo = Vector2.Min(lo, p); hi = Vector2.Max(hi, p); }
            if (asset.townZone.Length == 0) { lo = new Vector2(-300, -300); hi = new Vector2(300, 300); }
            var size = hi - lo + new Vector2(80, 80);
            float mpp = Mathf.Max(size.x, size.y) / 2000f;
            paths.Add(Write(asset, bg, (lo + hi) * 0.5f, mpp, 2000, 2000, Path.Combine(dir, "graph-village.png"),
                "ROADS R0O: " + townId.ToUpperInvariant() + " VILLAGE (KERB ZONE)", 1, legendTop: true));
            return paths;
        }

        // ------------------------------------------------------------------ R1: junction surfaces

        static readonly Color32 Asphalt = new Color32(70, 72, 78, 255), JunctionC = new Color32(96, 98, 106, 255), KerbC = new Color32(235, 235, 235, 255);
        static readonly Color32 StopLineC = new Color32(255, 255, 255, 255), CrosswalkC = new Color32(255, 255, 255, 200), MouthLineC = new Color32(255, 214, 0, 255);
        static readonly Color32 InletC = new Color32(40, 160, 255, 255), FallbackC = new Color32(255, 0, 220, 255), Gravel = new Color32(150, 120, 90, 255);

        /// <summary>
        /// The junction surfaces of a fresh R1 bake: graph-junctions.png (the village) and graph-junctions-downtown.png
        /// (the downtown zone close up), in artifacts/roads/(town)/.
        /// </summary>
        public static System.Collections.Generic.List<string> WriteJunctions(SurfaceBaker.Bake b)
        {
            var paths = new System.Collections.Generic.List<string>();
            var src = TownRoadSource.LoadTown(b.town);
            var bg = TownBackground(src, TownRoadSource.TownDir(b.town));
            var dir = RoadNetworkBaker.TownArtifactsDir(b.town);
            Directory.CreateDirectory(dir);
            Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = new Vector2(float.MinValue, float.MinValue);
            if (src.kerbZone != null) foreach (var p in src.kerbZone) { lo = Vector2.Min(lo, new Vector2((float)p.x, (float)p.z)); hi = Vector2.Max(hi, new Vector2((float)p.x, (float)p.z)); }
            else { lo = new Vector2(-300, -300); hi = new Vector2(300, 300); }
            var size = hi - lo + new Vector2(60, 60);
            paths.Add(WriteSurfaces(b, bg, (lo + hi) * 0.5f, Mathf.Max(size.x, size.y) / 2000f, 2000, 2000, Path.Combine(dir, "graph-junctions.png"),
                "ROADS R1: JUNCTION SURFACES, " + b.town.ToUpperInvariant()));
            if (src.downtownZone != null)
            {
                lo = new Vector2(float.MaxValue, float.MaxValue); hi = new Vector2(float.MinValue, float.MinValue);
                foreach (var p in src.downtownZone) { lo = Vector2.Min(lo, new Vector2((float)p.x, (float)p.z)); hi = Vector2.Max(hi, new Vector2((float)p.x, (float)p.z)); }
                size = hi - lo + new Vector2(20, 20);
                paths.Add(WriteSurfaces(b, bg, (lo + hi) * 0.5f, Mathf.Max(size.x, size.y) / 2000f, 2000, 2000, Path.Combine(dir, "graph-junctions-downtown.png"),
                    "ROADS R1: DOWNTOWN JUNCTIONS, " + b.town.ToUpperInvariant()));
            }
            return paths;
        }

        static string WriteSurfaces(SurfaceBaker.Bake b, Background bg, Vector2 centre, float mpp, int w, int h, string path, string title)
        {
            var cv = new Canvas(w, h, centre, mpp);
            Terrain(cv, bg);
            foreach (var poly in bg.water) cv.Fill(Array.ConvertAll(poly, p => cv.P(p)), Water);
            foreach (var poly in bg.buildings) cv.Fill(Array.ConvertAll(poly, p => cv.P(p)), Building);
            Vector2 V(D2 p) => cv.P(new Vector2((float)p.x, (float)p.z));
            Vector2 V3(D3 p) => cv.P(new Vector2((float)p.x, (float)p.z));
            var g = b.graph;
            var s = b.surfaces;
            // ribbons
            foreach (var r in s.ribbons)
            {
                var col = g.edges[r.edge].cls == RoadClass.Dirt ? Gravel : Asphalt;
                int P = r.profile.Length;
                for (int q = 0; q + 1 < r.stations.Count; q++)
                    cv.Fill(new[] { V3(r.stations[q][0]), V3(r.stations[q + 1][0]), V3(r.stations[q + 1][P - 1]), V3(r.stations[q][P - 1]) }, col);
            }
            // junctions
            foreach (var ns in s.nodes)
            {
                if (ns.mode == SurfaceMode.Fallback || ns.polygon.Count < 3) continue;
                var poly = ns.polygon.ConvertAll(V);
                cv.Fill(poly, JunctionC);
            }
            float lw = Mathf.Max(1.5f, 0.25f / mpp);
            foreach (var ns in s.nodes)
            {
                var n = g.nodes[ns.node];
                if (ns.mode == SurfaceMode.Fallback)
                {
                    cv.Ring(cv.P(new Vector2((float)n.p.x, (float)n.p.z)), Mathf.Max(8f, 4f / mpp), 3f, FallbackC);
                    continue;
                }
                foreach (var c in ns.corners)
                    if (c.arc.Count > 1) cv.Polyline(c.arc.ConvertAll(V), lw, KerbC);
                foreach (var a in ns.arms)
                {
                    if (a.mouth != null) cv.Polyline(Array.ConvertAll(a.mouth, V3), Mathf.Max(1f, 0.15f / mpp), MouthLineC);
                    if (a.crosswalk)
                        for (double u = -a.hwR + 0.3; u < a.hwL - 0.3; u += 1.2)
                        {
                            var p0 = a.frame.P(a.sCwInner, u);
                            var p1 = a.frame.P(a.sCwOuter, u);
                            cv.Line(V(p0), V(p1), Mathf.Max(1.5f, 0.6f / mpp), CrosswalkC);
                        }
                    if (a.sStop >= 0)
                        cv.Line(V(a.frame.P(a.sStop, 0)), V(a.frame.P(a.sStop, -a.hwR + 0.3)), Mathf.Max(2f, 0.45f / mpp), StopLineC);
                }
            }
            // ribbon edges (kerb lines)
            foreach (var r in s.ribbons)
            {
                int P = r.profile.Length;
                for (int q = 0; q + 1 < r.stations.Count; q++)
                {
                    cv.Line(V3(r.stations[q][0]), V3(r.stations[q + 1][0]), lw * 0.7f, KerbC);
                    cv.Line(V3(r.stations[q][P - 1]), V3(r.stations[q + 1][P - 1]), lw * 0.7f, KerbC);
                }
            }
            foreach (var i in s.inlets) cv.Disc(V3(i.p), Mathf.Max(2.5f, 0.6f / mpp), InletC);

            int sc = 2, line = 9 * sc + 6, x0 = 16, top = cv.h - 16;
            var items = new (Color32 c, string t)[]
            {
                (JunctionC, "JUNCTION SURFACE"), (Asphalt, "ROAD RIBBON"), (KerbC, "KERB LINE AND RETURN"), (MouthLineC, "MOUTH (SHARED SEAM)"),
                (StopLineC, "STOP LINE"), (CrosswalkC, "CROSSWALK BAND"), (InletC, "CURB INLET"), (FallbackC, "NO SURFACE (FALLBACK)"),
            };
            cv.Rect(x0 - 8, top - (items.Length + 2) * line - 12, x0 + 44 * 6 * sc, top + 8, new Color32(12, 14, 20, 200));
            cv.Text(x0, top, title, sc, new Color32(255, 255, 255, 255));
            int yy = top - line - 6;
            cv.Text(x0, yy, SurfaceChecks.Summary(b.checks).Split(';')[0].ToUpperInvariant(), sc, new Color32(200, 200, 200, 255));
            yy -= line + 4;
            foreach (var (c, t) in items)
            {
                cv.Rect(x0, yy - 13, x0 + 16, yy - 2, c);
                cv.Text(x0 + 26, yy, t, sc, new Color32(235, 235, 235, 255));
                yy -= line;
            }
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(cv.px);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            return path;
        }

        static Background CanonBackground(CanonAsset canon)
        {
            var bg = new Background();
            if (canon?.placements?.footprints != null)
                foreach (var f in canon.placements.footprints)
                {
                    var tmp = new RoadGraphInput();
                    tmp.AddJsFootprint(f.x, f.z, f.rot, f.hw, f.hd, f.hasRect, f.r);
                    bg.buildings.Add(Array.ConvertAll(tmp.footprints[0].corners, p => new Vector2((float)p.x, (float)p.z)));
                }
            if (canon == null || canon.heights.data == null) return bg;
            var H = canon.heights.data.GetData<float>();
            bg.waterY = canon.world.waterY;
            bg.baseY = canon.world.waterY;
            const int VN = WorldGrid.VN;
            bg.height = (ux, uz) =>
            {
                float fx = (-ux + WorldGrid.Half) / WorldGrid.Cell, fz = (uz + WorldGrid.Half) / WorldGrid.Cell;
                fx = Mathf.Clamp(fx, 0, VN - 1.001f); fz = Mathf.Clamp(fz, 0, VN - 1.001f);
                int i = (int)fx, j = (int)fz;
                float tx = fx - i, tz = fz - j;
                float h00 = H[j * VN + i], h10 = H[j * VN + i + 1], h01 = H[(j + 1) * VN + i], h11 = H[(j + 1) * VN + i + 1];
                return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
            };
            return bg;
        }

        static Background TownBackground(OsmRoadSource src, string dir)
        {
            var bg = new Background { shadeRange = 110f, baseY = 2f };
            var dem = src.dem;
            bg.height = (x, z) => (float)dem.Sample(x, z);
            foreach (var f in src.footprints) bg.buildings.Add(Array.ConvertAll(f, p => new Vector2((float)p.x, (float)p.z)));
            var waterPath = Path.Combine(dir, "water.json");
            if (File.Exists(waterPath))
            {
                var w = Backroads.Build.Editor.BrJson.Obj(Backroads.Build.Editor.BrJson.Parse(File.ReadAllText(waterPath)), "water.json");
                if (w.TryGetValue("areas", out var ao) && ao != null)
                    foreach (var a in Backroads.Build.Editor.BrJson.Arr(ao))
                        foreach (var pp in Backroads.Build.Editor.BrJson.Arr(Backroads.Build.Editor.BrJson.Obj(a)["polys"]))
                        {
                            var outer = Backroads.Build.Editor.BrJson.Arr(Backroads.Build.Editor.BrJson.Obj(pp)["outer"]);
                            bg.water.Add(outer.ConvertAll(q =>
                            {
                                var v = Backroads.Build.Editor.BrJson.Arr(q);
                                return new Vector2(Convert.ToSingle(v[0]), Convert.ToSingle(v[1]));
                            }).ToArray());
                        }
            }
            return bg;
        }

        // ------------------------------------------------------------------ canvas

        sealed class Canvas
        {
            public readonly int w, h;
            public readonly Color32[] px;
            public readonly Vector2 centre;
            public readonly float mpp;

            public Canvas(int w, int h, Vector2 centre, float mpp)
            {
                this.w = w; this.h = h; this.centre = centre; this.mpp = mpp;
                px = new Color32[w * h];
            }

            /// <summary>World (x, z) to pixel coordinates (float), y up.</summary>
            public Vector2 P(Vector2 xz) => new Vector2((xz.x - centre.x) / mpp + w * 0.5f, (xz.y - centre.y) / mpp + h * 0.5f);
            public Vector2 P(Vector3 p) => P(new Vector2(p.x, p.z));
            public Vector2 World(int x, int y) => new Vector2(centre.x + (x + 0.5f - w * 0.5f) * mpp, centre.y + (y + 0.5f - h * 0.5f) * mpp);

            public void Blend(int x, int y, Color32 c)
            {
                if (x < 0 || y < 0 || x >= w || y >= h) return;
                int i = y * w + x;
                if (c.a == 255) { px[i] = c; return; }
                var d = px[i];
                float a = c.a / 255f;
                px[i] = new Color32((byte)(d.r + (c.r - d.r) * a), (byte)(d.g + (c.g - d.g) * a), (byte)(d.b + (c.b - d.b) * a), 255);
            }

            /// <summary>Scanline fill of a polygon in pixel coordinates (even-odd).</summary>
            public void Fill(IReadOnlyList<Vector2> poly, Color32 c)
            {
                if (poly.Count < 3) return;
                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var p in poly) { minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); }
                int y0 = Mathf.Max(0, Mathf.FloorToInt(minY)), y1 = Mathf.Min(h - 1, Mathf.CeilToInt(maxY));
                var xs = new List<float>();
                for (int y = y0; y <= y1; y++)
                {
                    float sy = y + 0.5f;
                    xs.Clear();
                    for (int i = 0; i < poly.Count; i++)
                    {
                        Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
                        if ((a.y <= sy && b.y > sy) || (b.y <= sy && a.y > sy))
                            xs.Add(a.x + (sy - a.y) / (b.y - a.y) * (b.x - a.x));
                    }
                    xs.Sort();
                    for (int k = 0; k + 1 < xs.Count; k += 2)
                    {
                        int xa = Mathf.Max(0, Mathf.CeilToInt(xs[k] - 0.5f)), xb = Mathf.Min(w - 1, Mathf.FloorToInt(xs[k + 1] - 0.5f));
                        for (int x = xa; x <= xb; x++) Blend(x, y, c);
                    }
                }
            }

            public void Line(Vector2 a, Vector2 b, float width, Color32 c)
            {
                var d = b - a;
                float l = d.magnitude;
                if (l < 1e-4f) { Disc(a, width * 0.5f, c); return; }
                var n = new Vector2(-d.y, d.x) / l * (width * 0.5f);
                Fill(new[] { a + n, b + n, b - n, a - n }, c);
            }

            public void Polyline(IReadOnlyList<Vector2> pts, float width, Color32 c, bool closed = false)
            {
                for (int i = 0; i + 1 < pts.Count; i++) Line(pts[i], pts[i + 1], width, c);
                if (closed && pts.Count > 2) Line(pts[pts.Count - 1], pts[0], width, c);
            }

            public void Disc(Vector2 p, float r, Color32 c)
            {
                int x0 = Mathf.FloorToInt(p.x - r), x1 = Mathf.CeilToInt(p.x + r), y0 = Mathf.FloorToInt(p.y - r), y1 = Mathf.CeilToInt(p.y + r);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        if ((x + 0.5f - p.x) * (x + 0.5f - p.x) + (y + 0.5f - p.y) * (y + 0.5f - p.y) <= r * r) Blend(x, y, c);
            }

            public void Ring(Vector2 p, float r, float width, Color32 c)
            {
                var pts = new List<Vector2>();
                for (int i = 0; i < 32; i++) pts.Add(p + new Vector2(Mathf.Cos(i * Mathf.PI / 16), Mathf.Sin(i * Mathf.PI / 16)) * r);
                Polyline(pts, width, c, true);
            }

            public void Rect(int x0, int y0, int x1, int y1, Color32 c)
            {
                for (int y = Mathf.Max(0, y0); y <= Mathf.Min(h - 1, y1); y++)
                    for (int x = Mathf.Max(0, x0); x <= Mathf.Min(w - 1, x1); x++) Blend(x, y, c);
            }

            /// <summary>Text with the top-left corner at (x, y) (y up: the text grows downwards), scale s, dark outline.</summary>
            public int Text(int x, int y, string s, int scale, Color32 c, bool outline = true)
            {
                if (outline)
                    for (int oy = -1; oy <= 1; oy++)
                        for (int ox = -1; ox <= 1; ox++)
                            if (ox != 0 || oy != 0) Glyphs(x + ox * Math.Max(1, scale / 2), y + oy * Math.Max(1, scale / 2), s, scale, new Color32(10, 12, 16, 255));
                return Glyphs(x, y, s, scale, c);
            }

            int Glyphs(int x, int y, string s, int scale, Color32 c)
            {
                int cx = x;
                foreach (char ch in s.ToUpperInvariant())
                {
                    var g = Font5x7.Get(ch);
                    for (int row = 0; row < 7; row++)
                        for (int col = 0; col < 5; col++)
                            if ((g[row] >> (4 - col) & 1) != 0)
                                Rect(cx + col * scale, y - (row + 1) * scale + 1, cx + col * scale + scale - 1, y - row * scale, c);
                    cx += 6 * scale;
                }
                return cx - x;
            }
        }

        // ------------------------------------------------------------------ the map

        static readonly Color32 Building = new Color32(58, 62, 72, 255), Water = new Color32(44, 84, 112, 255);
        static readonly Color32 Major = new Color32(0, 229, 255, 255), Stop = new Color32(255, 64, 64, 255), Plain = new Color32(240, 240, 240, 255);
        static readonly Color32 RuleStop = new Color32(255, 160, 40, 255), Downtown = new Color32(230, 120, 230, 220), EdgeCutC = new Color32(255, 255, 255, 255);
        static readonly Color32 MouthC = new Color32(255, 235, 59, 255), Fillet = new Color32(255, 255, 255, 200), Zone = new Color32(140, 230, 140, 220);

        public static Color32 KindColor(NodeKind k, int degree)
        {
            switch (k)
            {
                case NodeKind.Junction: return degree >= 4 ? new Color32(232, 52, 52, 255) : new Color32(255, 150, 30, 255);
                case NodeKind.DirtApron: return new Color32(205, 133, 63, 255);
                case NodeKind.Bend: return new Color32(79, 195, 247, 255);
                case NodeKind.DeadEnd: return new Color32(170, 170, 170, 255);
                case NodeKind.ClassChange: return new Color32(102, 187, 106, 255);
                case NodeKind.Join: return new Color32(120, 120, 220, 255);
                default: return new Color32(255, 0, 255, 255);
            }
        }

        public static string Write(RoadNetworkAsset a, Background bg, Vector2 centre, float mpp, int w, int h, string path, string title, int labelScale,
            bool legendTop = true, bool labels = true)
        {
            var cv = new Canvas(w, h, centre, mpp);
            Terrain(cv, bg);
            foreach (var poly in bg.water) cv.Fill(Array.ConvertAll(poly, p => cv.P(p)), Water);
            foreach (var poly in bg.buildings) cv.Fill(Array.ConvertAll(poly, p => cv.P(p)), Building);

            // town zone (and downtown)
            var zone = new List<Vector2>();
            foreach (var p in a.townZone) zone.Add(cv.P(p));
            cv.Polyline(zone, 2f, Zone, true);
            var dz = new List<Vector2>();
            foreach (var p in a.downtownZone) dz.Add(cv.P(p));
            if (dz.Count > 2) cv.Polyline(dz, 2f, Downtown, true);

            // ribbons
            foreach (var st in PlainRibbonMesh.Strips(a))
            {
                var c = PlainRibbonMesh.ClassColor(st.cls);
                for (int i = 0; i + 1 < st.left.Length; i++)
                    cv.Fill(new[]
                    {
                        cv.P(new Vector2((float)st.left[i].x, (float)st.left[i].z)), cv.P(new Vector2((float)st.left[i + 1].x, (float)st.left[i + 1].z)),
                        cv.P(new Vector2((float)st.right[i + 1].x, (float)st.right[i + 1].z)), cv.P(new Vector2((float)st.right[i].x, (float)st.right[i].z)),
                    }, c);
            }
            // centrelines
            foreach (var e in a.edges)
            {
                var pts = new List<Vector2>();
                foreach (var p in e.points) pts.Add(cv.P(p));
                cv.Polyline(pts, Mathf.Max(1f, 0.25f / mpp), new Color32(30, 30, 30, 160));
            }

            float lineW = Mathf.Max(2f, 0.6f / mpp);
            foreach (var n in a.nodes)
            {
                var np = new Vector2(n.position.x, n.position.z);
                // provisional fillets
                for (int k = 0; k < n.cornerCount; k++)
                {
                    var c = a.corners[n.firstCorner + k];
                    if (c.kind != CornerKind.Fillet) continue;
                    var I = a.arms[c.armI];
                    var J = a.arms[c.armJ];
                    Vector2 ti = np + I.dir * c.tI + new Vector2(-I.dir.y, I.dir.x) * I.halfWidth;
                    Vector2 tj = np + J.dir * c.tJ + new Vector2(J.dir.y, -J.dir.x) * J.halfWidth;
                    cv.Polyline(Arc(c.centre, c.radius, ti, tj, np).ConvertAll(v => cv.P(v)), Mathf.Max(1.5f, 0.3f / mpp), Fillet);
                }
                for (int k = 0; k < n.armCount; k++)
                {
                    var arm = a.arms[n.firstArm + k];
                    bool isMajor = k == n.majorA || k == n.majorB;
                    bool stop = arm.control == Control.MinorStop || arm.control == Control.AllWayStop || arm.control == Control.Yield || arm.control == Control.Signal;
                    var col = isMajor ? Major : stop ? (arm.controlSource == ControlSource.Tag || a.source.StartsWith("CanonData", StringComparison.Ordinal) ? Stop : RuleStop) : Plain;
                    float len = Mathf.Min(arm.length, Mathf.Max(10f, arm.mouth));
                    cv.Line(cv.P(np), cv.P(np + arm.dir * len), lineW, col);
                    if (arm.mouth > 0 && arm.mouth < arm.length)
                    {
                        var m = np + arm.dir * arm.mouth;
                        var nn = new Vector2(-arm.dir.y, arm.dir.x) * arm.halfWidth;
                        cv.Line(cv.P(m - nn), cv.P(m + nn), Mathf.Max(2f, 0.4f / mpp), arm.shortArm ? new Color32(255, 120, 0, 255) : MouthC);
                    }
                }
            }
            var labelled = new List<Vector2>();
            foreach (var n in a.nodes)
            {
                var p = cv.P(n.position);
                if (p.x < -20 || p.y < -20 || p.x > w + 20 || p.y > h + 20) continue;
                float r = Mathf.Max(labels ? 5f : 3f, 2.4f / mpp);
                cv.Disc(p, r + 1.5f, new Color32(10, 12, 16, 255));
                cv.Disc(p, r, KindColor(n.kind, n.armCount));
                if ((n.flags & NodeFlags.ComplexPair) != 0) cv.Ring(p, r + 5f, 3f, new Color32(255, 0, 220, 255));
                if ((n.flags & NodeFlags.PlayAreaEdge) != 0) cv.Ring(p, r + 5f, 2f, EdgeCutC);
                if (!labels || (n.armCount < 3 && n.kind != NodeKind.DirtApron)) continue;
                string label = n.id.ToString();
                // a node a few pixels from one already labelled (harbor 10/27, mill 11/29) gets its label below-left
                bool crowded = labelled.Exists(q => (q - p).sqrMagnitude < 30f * 30f);
                int lx = crowded ? Mathf.RoundToInt(p.x - r - 3 - label.Length * 6 * labelScale) : Mathf.RoundToInt(p.x + r + 3);
                int ly = crowded ? Mathf.RoundToInt(p.y - r - 2) : Mathf.RoundToInt(p.y + 7 * labelScale + 2);
                cv.Text(lx, ly, label, labelScale, new Color32(255, 255, 255, 255));
                labelled.Add(p);
            }
            Legend(cv, a, title, legendTop);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(cv.px);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            return path;
        }

        static List<Vector2> Arc(Vector2 c, float r, Vector2 from, Vector2 to, Vector2 node)
        {
            float a0 = Mathf.Atan2(from.y - c.y, from.x - c.x), a1 = Mathf.Atan2(to.y - c.y, to.x - c.x);
            float ccw = Mathf.Repeat(a1 - a0, 2 * Mathf.PI), cw = ccw - 2 * Mathf.PI;
            Vector2 mc = c + new Vector2(Mathf.Cos(a0 + ccw / 2), Mathf.Sin(a0 + ccw / 2)) * r;
            Vector2 mw = c + new Vector2(Mathf.Cos(a0 + cw / 2), Mathf.Sin(a0 + cw / 2)) * r;
            float sweep = (mc - node).sqrMagnitude <= (mw - node).sqrMagnitude ? ccw : cw;
            var pts = new List<Vector2>();
            int steps = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(sweep) / (5 * Mathf.Deg2Rad)));
            for (int k = 0; k <= steps; k++)
            {
                float t = a0 + sweep * k / steps;
                pts.Add(c + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * r);
            }
            return pts;
        }

        /// <summary>Hillshade of the heights (light from the north-west), green-grey by height, water below waterY.</summary>
        static void Terrain(Canvas cv, Background bg)
        {
            if (bg?.height == null)
            {
                for (int i = 0; i < cv.px.Length; i++) cv.px[i] = new Color32(70, 80, 70, 255);
                return;
            }
            var Hgt = bg.height;
            float water = bg.waterY;
            var light = new Vector3(-0.5f, 0.75f, 0.45f).normalized; // Unity space: from up-left (north-west in this view)
            for (int y = 0; y < cv.h; y++)
                for (int x = 0; x < cv.w; x++)
                {
                    var wp = cv.World(x, y);
                    float hc = Hgt(wp.x, wp.y);
                    if (hc < water)
                    {
                        cv.px[y * cv.w + x] = Water;
                        continue;
                    }
                    float e = Mathf.Max(1.5f, cv.mpp);
                    float dx = Hgt(wp.x + e, wp.y) - Hgt(wp.x - e, wp.y), dz = Hgt(wp.x, wp.y + e) - Hgt(wp.x, wp.y - e);
                    var nrm = new Vector3(-dx, 2 * e, -dz).normalized;
                    float shade = Mathf.Clamp01(0.35f + 0.85f * Vector3.Dot(nrm, light));
                    float t = Mathf.Clamp01((hc - bg.baseY) / bg.shadeRange);
                    var baseC = Color.Lerp(new Color(0.34f, 0.42f, 0.30f), new Color(0.50f, 0.50f, 0.42f), t);
                    var c = baseC * shade;
                    cv.px[y * cv.w + x] = new Color32((byte)(c.r * 255), (byte)(c.g * 255), (byte)(c.b * 255), 255);
                }
        }

        static void Legend(Canvas cv, RoadNetworkAsset a, string title, bool top0)
        {
            int s = 2, line = 9 * s + 6, x0 = 16;
            bool town = !a.source.StartsWith("CanonData", StringComparison.Ordinal);
            var items = new List<(Color32 c, string text, int kind)>
            {
                (KindColor(NodeKind.Junction, 4), "JUNCTION X (4 ARMS)", 0), (KindColor(NodeKind.Junction, 3), "JUNCTION T (3 ARMS)", 0),
                (KindColor(NodeKind.DirtApron, 3), "DIRT ROAD MEETS PAVEMENT", 0), (KindColor(NodeKind.Bend, 2), "L-BEND", 0),
                (KindColor(NodeKind.DeadEnd, 1), town ? "DEAD END" : "DEAD END (YARD)", 0), (KindColor(NodeKind.ClassChange, 2), "TOWN EDGE (CLASS CHANGE)", 0),
                (new Color32(255, 0, 220, 255), town ? "JUNCTIONS UNDER 30 M APART (R4)" : "TOO CLOSE: NEEDS DECISION U8", 3),
                (PlainRibbonMesh.ClassColor(RoadClass.MainStreet), "MAIN STREET (THROUGH ROAD IN TOWN)", 1),
                (PlainRibbonMesh.ClassColor(RoadClass.Highway), "HIGHWAY", 1), (PlainRibbonMesh.ClassColor(RoadClass.Street), "STREET", 1),
                (PlainRibbonMesh.ClassColor(RoadClass.Dirt), "DIRT ROAD", 1),
                (Major, "MAJOR ROAD ARM (DRIVES THROUGH)", 2), (Stop, town ? "ARM THAT STOPS (MAPPED SIGN)" : "ARM THAT STOPS", 2),
                (MouthC, "JUNCTION MOUTH (PROVISIONAL)", 2), (Fillet, "KERB CORNER (PROVISIONAL)", 2), (Zone, "TOWN KERB ZONE", 2),
            };
            if (town)
            {
                items.Insert(6, (KindColor(NodeKind.Join, 2), "JOIN (TAGS CHANGE: BRIDGE, WIDTH)", 0));
                items.Insert(7, (EdgeCutC, "ROAD ENDS AT THE PLAY-AREA EDGE", 3));
                items.Add((RuleStop, "ARM THAT STOPS (BY RULE)", 2));
                items.Add((Downtown, "DOWNTOWN ZONE", 2));
                items.Add((PlainRibbonMesh.ClassColor(RoadClass.Yard), "SERVICE ROAD, ALLEY", 1));
            }
            int boxH = (items.Count + 2) * line + 20, boxW = 46 * 6 * s;
            int top = top0 ? cv.h - 16 : boxH + 8;
            cv.Rect(x0 - 8, top - boxH, x0 + boxW, top + 8, new Color32(12, 14, 20, 200));
            cv.Text(x0, top, title, s, new Color32(255, 255, 255, 255));
            int y = top - line - 6;
            string counts = $"{a.nodes.Length} NODES, {a.edges.Length} EDGES, {a.arms.Length} ARM ENDS";
            cv.Text(x0, y, counts, s, new Color32(200, 200, 200, 255));
            y -= line + 4;
            foreach (var (c, text, kind) in items)
            {
                if (kind == 0) cv.Disc(new Vector2(x0 + 8, y - 7), 7, c);
                else if (kind == 3) cv.Ring(new Vector2(x0 + 8, y - 7), 7, 3, c);
                else if (kind == 1) cv.Rect(x0, y - 13, x0 + 16, y - 2, c);
                else cv.Rect(x0, y - 9, x0 + 16, y - 5, c);
                cv.Text(x0 + 26, y, text, s, new Color32(235, 235, 235, 255));
                y -= line;
            }
        }
    }

    /// <summary>A 5x7 bitmap font: A-Z, 0-9 and a little punctuation, one byte per row (bit 4 = left column).</summary>
    static class Font5x7
    {
        static readonly Dictionary<char, byte[]> s_Glyphs = new Dictionary<char, byte[]>
        {
            ['A'] = B("01110", "10001", "10001", "11111", "10001", "10001", "10001"),
            ['B'] = B("11110", "10001", "10001", "11110", "10001", "10001", "11110"),
            ['C'] = B("01110", "10001", "10000", "10000", "10000", "10001", "01110"),
            ['D'] = B("11110", "10001", "10001", "10001", "10001", "10001", "11110"),
            ['E'] = B("11111", "10000", "10000", "11110", "10000", "10000", "11111"),
            ['F'] = B("11111", "10000", "10000", "11110", "10000", "10000", "10000"),
            ['G'] = B("01110", "10001", "10000", "10111", "10001", "10001", "01111"),
            ['H'] = B("10001", "10001", "10001", "11111", "10001", "10001", "10001"),
            ['I'] = B("01110", "00100", "00100", "00100", "00100", "00100", "01110"),
            ['J'] = B("00111", "00010", "00010", "00010", "00010", "10010", "01100"),
            ['K'] = B("10001", "10010", "10100", "11000", "10100", "10010", "10001"),
            ['L'] = B("10000", "10000", "10000", "10000", "10000", "10000", "11111"),
            ['M'] = B("10001", "11011", "10101", "10101", "10001", "10001", "10001"),
            ['N'] = B("10001", "10001", "11001", "10101", "10011", "10001", "10001"),
            ['O'] = B("01110", "10001", "10001", "10001", "10001", "10001", "01110"),
            ['P'] = B("11110", "10001", "10001", "11110", "10000", "10000", "10000"),
            ['Q'] = B("01110", "10001", "10001", "10001", "10101", "10010", "01101"),
            ['R'] = B("11110", "10001", "10001", "11110", "10100", "10010", "10001"),
            ['S'] = B("01111", "10000", "10000", "01110", "00001", "00001", "11110"),
            ['T'] = B("11111", "00100", "00100", "00100", "00100", "00100", "00100"),
            ['U'] = B("10001", "10001", "10001", "10001", "10001", "10001", "01110"),
            ['V'] = B("10001", "10001", "10001", "10001", "10001", "01010", "00100"),
            ['W'] = B("10001", "10001", "10001", "10101", "10101", "10101", "01010"),
            ['X'] = B("10001", "10001", "01010", "00100", "01010", "10001", "10001"),
            ['Y'] = B("10001", "10001", "10001", "01010", "00100", "00100", "00100"),
            ['Z'] = B("11111", "00001", "00010", "00100", "01000", "10000", "11111"),
            ['0'] = B("01110", "10001", "10011", "10101", "11001", "10001", "01110"),
            ['1'] = B("00100", "01100", "00100", "00100", "00100", "00100", "01110"),
            ['2'] = B("01110", "10001", "00001", "00010", "00100", "01000", "11111"),
            ['3'] = B("11111", "00010", "00100", "00010", "00001", "10001", "01110"),
            ['4'] = B("00010", "00110", "01010", "10010", "11111", "00010", "00010"),
            ['5'] = B("11111", "10000", "11110", "00001", "00001", "10001", "01110"),
            ['6'] = B("00110", "01000", "10000", "11110", "10001", "10001", "01110"),
            ['7'] = B("11111", "00001", "00010", "00100", "01000", "01000", "01000"),
            ['8'] = B("01110", "10001", "10001", "01110", "10001", "10001", "01110"),
            ['9'] = B("01110", "10001", "10001", "01111", "00001", "00010", "01100"),
            ['-'] = B("00000", "00000", "00000", "11111", "00000", "00000", "00000"),
            ['.'] = B("00000", "00000", "00000", "00000", "00000", "01100", "01100"),
            [':'] = B("00000", "01100", "01100", "00000", "01100", "01100", "00000"),
            ['('] = B("00010", "00100", "01000", "01000", "01000", "00100", "00010"),
            [')'] = B("01000", "00100", "00010", "00010", "00010", "00100", "01000"),
            ['+'] = B("00000", "00100", "00100", "11111", "00100", "00100", "00000"),
            ['/'] = B("00000", "00001", "00010", "00100", "01000", "10000", "00000"),
            [','] = B("00000", "00000", "00000", "00000", "01100", "00100", "01000"),
            ['%'] = B("11000", "11001", "00010", "00100", "01000", "10011", "00011"),
            ['='] = B("00000", "00000", "11111", "00000", "11111", "00000", "00000"),
        };

        static readonly byte[] Blank = new byte[7];

        static byte[] B(params string[] rows)
        {
            var b = new byte[7];
            for (int i = 0; i < 7; i++) b[i] = Convert.ToByte(rows[i], 2);
            return b;
        }

        public static byte[] Get(char c) => s_Glyphs.TryGetValue(c, out var g) ? g : Blank;
    }
}
