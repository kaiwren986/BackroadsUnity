using System;
using System.Collections.Generic;
using System.IO;
using Backroads.Core.WorldBake;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    public static partial class RoadGraphMap
    {
        static readonly Color32 WalkC = new Color32(196, 194, 186, 255), ShoulderC = new Color32(138, 118, 92, 255), KerbedC = new Color32(255, 255, 255, 255);
        static readonly Color32 OpenEdgeC = new Color32(120, 110, 100, 255), DepressedC = new Color32(255, 214, 0, 255), RampRunC = new Color32(255, 140, 0, 255);
        static readonly Color32 ForbiddenC = new Color32(235, 60, 60, 255), FrontageC = new Color32(60, 220, 120, 255), ClearanceC = new Color32(255, 0, 220, 255);

        /// <summary>
        /// The kerbs of a fresh R2 bake: graph-kerbs.png (the village) and graph-kerbs-downtown.png, in artifacts/roads/(town)/:
        /// asphalt, walks, shoulders, kerb lines (white kerbed, brown unkerbed), depressed corners (yellow) and their ramps
        /// (orange), and along each face where driveways may go (green) or not (red).
        /// </summary>
        public static List<string> WriteKerbs(KerbBaker.Bake b)
        {
            var paths = new List<string>();
            var src = b.roads.source ?? TownRoadSource.LoadTown(b.town);
            var bg = TownBackground(src, TownRoadSource.TownDir(b.town));
            var dir = RoadNetworkBaker.TownArtifactsDir(b.town);
            Directory.CreateDirectory(dir);
            Vector2 lo = new Vector2(float.MaxValue, float.MaxValue), hi = new Vector2(float.MinValue, float.MinValue);
            if (src.kerbZone != null) foreach (var p in src.kerbZone) { lo = Vector2.Min(lo, new Vector2((float)p.x, (float)p.z)); hi = Vector2.Max(hi, new Vector2((float)p.x, (float)p.z)); }
            else { lo = new Vector2(-300, -300); hi = new Vector2(300, 300); }
            var size = hi - lo + new Vector2(60, 60);
            paths.Add(DrawKerbs(b, bg, (lo + hi) * 0.5f, Mathf.Max(size.x, size.y) / 2000f, 2000, 2000, Path.Combine(dir, "graph-kerbs.png"),
                "ROADS R2: KERBS, WALKS, RAMPS, FRONTAGE, " + b.town.ToUpperInvariant()));
            if (src.downtownZone != null)
            {
                lo = new Vector2(float.MaxValue, float.MaxValue); hi = new Vector2(float.MinValue, float.MinValue);
                foreach (var p in src.downtownZone) { lo = Vector2.Min(lo, new Vector2((float)p.x, (float)p.z)); hi = Vector2.Max(hi, new Vector2((float)p.x, (float)p.z)); }
                size = hi - lo + new Vector2(20, 20);
                paths.Add(DrawKerbs(b, bg, (lo + hi) * 0.5f, Mathf.Max(size.x, size.y) / 2000f, 2000, 2000, Path.Combine(dir, "graph-kerbs-downtown.png"),
                    "ROADS R2: DOWNTOWN KERBS, " + b.town.ToUpperInvariant()));
                // one downtown junction up close, with the road plates' outlines: the main-street junction nearest the zone's centre
                var mid = (lo + hi) * 0.5f;
                GraphNode best = null;
                foreach (var n in b.roads.graph.nodes)
                {
                    if (n.Degree < 3 || b.roads.surfaces.Of(n.id)?.mode == SurfaceMode.Fallback) continue;
                    bool main = false;
                    foreach (var a in n.arms) main |= b.roads.graph.arms[a].cls == RoadClass.MainStreet;
                    if (!main) continue;
                    if (best == null || (new Vector2((float)n.p.x, (float)n.p.z) - mid).sqrMagnitude < (new Vector2((float)best.p.x, (float)best.p.z) - mid).sqrMagnitude) best = n;
                }
                if (best != null)
                {
                    var plates = RoadPlateBuilder.Build(b.roads.graph, b.roads.surfaces, b.kerbs, src.dem, b.settings);
                    paths.Add(DrawKerbs(b, bg, new Vector2((float)best.p.x, (float)best.p.z), 70f / 2000f, 2000, 2000, Path.Combine(dir, "graph-kerbs-junction.png"),
                        "ROADS R2: NODE " + best.id + " UP CLOSE, WITH THE ROAD PLATES", plates));
                }
            }
            return paths;
        }

        /// <summary>A close-up of the kerbs and plates around one point (debugging): graph-kerbs-at.png, `size` metres across.</summary>
        public static string WriteKerbsAt(KerbBaker.Bake b, Vector2 at, float size)
        {
            var src = b.roads.source ?? TownRoadSource.LoadTown(b.town);
            var bg = TownBackground(src, TownRoadSource.TownDir(b.town));
            var plates = RoadPlateBuilder.Build(b.roads.graph, b.roads.surfaces, b.kerbs, src.dem, b.settings);
            var path = Path.Combine(RoadNetworkBaker.TownArtifactsDir(b.town), "graph-kerbs-at.png");
            return DrawKerbs(b, bg, at, size / 2000f, 2000, 2000, path, string.Format(System.Globalization.CultureInfo.InvariantCulture, "ROADS R2 AT ({0:F1}, {1:F1})", at.x, at.y), plates);
        }

        static readonly Color32 PlateRoadC = new Color32(0, 200, 255, 160), PlateWalkC = new Color32(255, 255, 255, 160), PlateVergeC = new Color32(120, 255, 120, 140), PlateShoulderC = new Color32(255, 170, 60, 160);

        static string DrawKerbs(KerbBaker.Bake b, Background bg, Vector2 centre, float mpp, int w, int h, string path, string title, List<Plate> plates = null)
        {
            var cv = new Canvas(w, h, centre, mpp);
            Terrain(cv, bg);
            foreach (var poly in bg.water) cv.Fill(Array.ConvertAll(poly, p => cv.P(p)), Water);
            Vector2 V(D2 p) => cv.P(new Vector2((float)p.x, (float)p.z));
            Vector2 V3(D3 p) => cv.P(new Vector2((float)p.x, (float)p.z));
            var g = b.roads.graph;
            var s = b.roads.surfaces;
            var k = b.kerbs;
            var st = b.settings;
            // walks and shoulders under everything else
            foreach (var f in k.faces)
                for (int i = 0; i + 1 < f.Count; i++)
                {
                    var n = (f.kerb[i + 1] - f.kerb[i]).Normalized.Left;
                    double w0 = f.kerbed ? f.width[i] : st.shoulder, w1 = f.kerbed ? f.width[i + 1] : st.shoulder;
                    if (w0 <= 0 && w1 <= 0) continue;
                    cv.Fill(new[] { V(f.kerb[i]), V(f.kerb[i + 1]), V(f.kerb[i + 1] + n * w1), V(f.kerb[i] + n * w0) }, f.kerbed ? WalkC : ShoulderC);
                }
            foreach (var poly in bg.buildings) cv.Fill(Array.ConvertAll(poly, p => cv.P(p)), Building);
            foreach (var r in s.ribbons)
            {
                int P = r.profile.Length;
                for (int q = 0; q + 1 < r.stations.Count; q++)
                    cv.Fill(new[] { V3(r.stations[q][0]), V3(r.stations[q + 1][0]), V3(r.stations[q + 1][P - 1]), V3(r.stations[q][P - 1]) }, g.edges[r.edge].cls == RoadClass.Dirt ? Gravel : Asphalt);
            }
            foreach (var ns in s.nodes)
                if (ns.mode != SurfaceMode.Fallback && ns.polygon.Count >= 3) cv.Fill(ns.polygon.ConvertAll(V), JunctionC);

            float lw = Mathf.Max(1.5f, 0.2f / mpp);
            foreach (var f in k.faces)
            {
                var pts = f.kerb.ConvertAll(V);
                cv.Polyline(pts, lw, f.kerbed ? KerbedC : OpenEdgeC);
                // frontage: green where a driveway may start, red where not, drawn just inside the block
                double off = f.kerbed ? Math.Min(0.6, 0.5 * f.width[0]) + 0.1 : 0.5;
                for (int i = 0; i + 1 < f.Count; i++)
                {
                    double sm = 0.5 * (f.s[i] + f.s[i + 1]);
                    bool blocked = false;
                    foreach (var iv in f.forbidden) if (sm >= iv.s0 && sm <= iv.s1) { blocked = true; break; }
                    var n = (f.kerb[i + 1] - f.kerb[i]).Normalized.Left;
                    cv.Line(V(f.kerb[i] + n * off), V(f.kerb[i + 1] + n * off), Mathf.Max(1f, 0.25f / mpp), blocked ? ForbiddenC : FrontageC);
                }
            }
            // ramps: the lowered stretch and its runs along the kerb
            foreach (var f in k.faces)
            {
                if (!f.kerbed || f.corners.Count == 0) continue;
                for (int i = 0; i + 1 < f.Count; i++)
                {
                    double t = f.t0 + 0.5 * (f.s[i] + f.s[i + 1]);
                    double hgt = KerbBuilder.KerbHeight(k, f, t, st);
                    if (hgt >= st.barrier - 1e-6) continue;
                    cv.Line(V(f.kerb[i]), V(f.kerb[i + 1]), lw * 2.2f, hgt <= st.dropped + 1e-6 ? DepressedC : RampRunC);
                }
            }
            // where a kerb is closer to a building than the walk needs
            foreach (var f in k.faces)
                if (f.walk && !double.IsNaN(f.minClearance) && f.minClearance < KerbRules.WalkWidth((RoadClass)(byte)f.cls, st) - 1e-6)
                {
                    int i = 0;
                    for (int q = 0; q < f.Count; q++) if (Math.Abs(f.s[q] - f.minClearanceS) < Math.Abs(f.s[i] - f.minClearanceS)) i = q;
                    cv.Ring(V(f.kerb[i]), Mathf.Max(6f, 2.5f / mpp), 2f, ClearanceC);
                }

            if (plates != null)
            {
                float half = 0.5f * Mathf.Max(w, h) * mpp + 5f;
                foreach (var pl in plates)
                {
                    var p0 = pl.polygon[0];
                    if (Mathf.Abs((float)p0.x - centre.x) > half || Mathf.Abs((float)p0.y - centre.y) > half) continue;
                    var col = pl.kind == PlateKind.Walk ? (pl.surface == RoadPlateBuilder.Grass ? PlateVergeC : PlateWalkC) : pl.surface == RoadPlateBuilder.Gravel && pl.priority == RoadPlateBuilder.PriorityVerge ? PlateShoulderC : PlateRoadC;
                    var pts = new List<Vector2>(pl.polygon.Length);
                    foreach (var q in pl.polygon) pts.Add(cv.P(new Vector2((float)q.x, (float)q.y)));
                    cv.Polyline(pts, 1f, col, true);
                }
            }

            int sc = 2, line = 9 * sc + 6, x0 = 16, top = cv.h - 16;
            var items = new (Color32 c, string t)[]
            {
                (WalkC, "WALK"), (ShoulderC, "GRAVEL SHOULDER"), (KerbedC, "KERB (BARRIER 150 MM)"), (OpenEdgeC, "UNKERBED PAVEMENT EDGE"),
                (DepressedC, "DEPRESSED CORNER (DROPPED 25 MM)"), (RampRunC, "RAMP RUN"), (FrontageC, "FRONTAGE OPEN TO DRIVEWAYS"),
                (ForbiddenC, "NO DRIVEWAY (CORNER, CROSSWALK, RAMP, STOP, INLET)"), (ClearanceC, "KERB CLOSER TO A BUILDING THAN THE WALK"),
            };
            cv.Rect(x0 - 8, top - (items.Length + 2) * line - 12, x0 + 56 * 6 * sc, top + 8, new Color32(12, 14, 20, 200));
            cv.Text(x0, top, title, sc, new Color32(255, 255, 255, 255));
            int yy = top - line - 6;
            cv.Text(x0, yy, KerbBaker.Summary(b).Split(',')[0].ToUpperInvariant() + ", " + k.faces.Count + " FACES", sc, new Color32(200, 200, 200, 255));
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
    }
}
