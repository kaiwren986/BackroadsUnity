using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Backroads.Canon.Editor;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.3's WorldData files, all in JS space (x_js = -x) like CanonData v1:
    /// - vegetation.json: CanonData v1's format (species; trees [x, y, z, sp, s, rot, tint, hue]; rocks [x, y, z, s, rot, v]).
    /// - places.json: "pois" in world.json's form (id, name, kind, x, z, r, fall, y) and "parking" (x, z, yaw) for the
    ///   garage's bays, as placements.json has them.
    /// - buildings.json: every footprint in the play area (osm, part, poly, holes, pad y, type, business, area, the
    ///   levels and roof tags when mapped), for the grey boxes, the static colliders and the map.
    /// - map.png: the minimap and big map, 1 m per pixel, north up (land by surface, 10 m contours with a bold one every
    ///   50 m, water, roads, walks, buildings).
    /// </summary>
    public static class WorldDataExtras
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string N(double v, int digits) => (Math.Round(v, digits) + 0.0).ToString("R", Inv);
        static string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        static string Json(object o)
        {
            switch (o)
            {
                case null: return "null";
                case string s: return Q(s);
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("R", Inv);
                case Dictionary<string, object> m:
                    return "{" + string.Join(", ", m.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Q(kv.Key) + ": " + Json(kv.Value))) + "}";
                case List<object> l: return "[" + string.Join(", ", l.Select(Json)) + "]";
                default: return Convert.ToString(o, Inv);
            }
        }

        public static string Vegetation(NatureResult n)
        {
            var sb = new StringBuilder("{\n  \"note\": \"T3.3 first nature (W6), CanonData v1's format, JS space (x_js = -x): tree mesh at y - 0.2, rock mesh at y - 0.25 s\",\n");
            sb.Append("  \"rockFields\": [\"x\", \"y\", \"z\", \"s\", \"rot\", \"v\"],\n  \"rocks\": [");
            for (int i = 0; i < n.rocks.Count; i++)
            {
                var r = n.rocks[i];
                sb.Append(i == 0 ? "\n    [" : ",\n    [").Append(N(-r.x, 3)).Append(", ").Append(N(r.y, 3)).Append(", ").Append(N(r.z, 3)).Append(", ")
                  .Append(N(r.s, 4)).Append(", ").Append(N(r.rot, 4)).Append(", ").Append(N(r.v, 4)).Append(']');
            }
            sb.Append("\n  ],\n  \"species\": [");
            for (int i = 0; i < n.species.Count; i++) sb.Append(i == 0 ? "\n    " : ",\n    ").Append(Json(n.species[i]));
            sb.Append("\n  ],\n  \"treeFields\": [\"x\", \"y\", \"z\", \"sp\", \"s\", \"rot\", \"tint\", \"hue\"],\n  \"trees\": [");
            for (int i = 0; i < n.trees.Count; i++)
            {
                var t = n.trees[i];
                sb.Append(i == 0 ? "\n    [" : ",\n    [").Append(N(-t.x, 3)).Append(", ").Append(N(t.y, 3)).Append(", ").Append(N(t.z, 3)).Append(", ")
                  .Append(t.sp).Append(", ").Append(N(t.s, 4)).Append(", ").Append(N(t.rot, 4)).Append(", ").Append(N(t.tint, 4)).Append(", ").Append(N(t.hue, 4)).Append(']');
            }
            sb.Append("\n  ],\n  \"version\": 1\n}\n");
            return sb.ToString();
        }

        public static string Places(PlacesResult p)
        {
            var sb = new StringBuilder("{\n  \"format\": \"backroads-world-places\",\n");
            sb.Append("  \"note\": \"T3.3 provisional game places (W7 is lane G's in M9): pois as CanonData v1's world.json has them, parking bays as placements.json; JS space (x_js = -x, yaw_js = -yaw_unity)");
            if (p.notes.Count > 0) sb.Append("; ").Append(string.Join("; ", p.notes).Replace("\"", "'"));
            sb.Append("\",\n  \"cameras\": [");
            for (int i = 0; i < p.cameras.Count; i++)
            {
                var c = p.cameras[i];
                sb.Append(i == 0 ? "\n    " : ",\n    ").Append("{\"fov\": ").Append(N(c.fov, 3)).Append(", \"id\": ").Append(Q(c.id)).Append(", \"name\": ").Append(Q(c.name))
                  .Append(", \"pos\": [").Append(N(-c.pos.x, 3)).Append(", ").Append(N(c.pos.y, 3)).Append(", ").Append(N(c.pos.z, 3)).Append(']')
                  .Append(", \"target\": [").Append(N(-c.target.x, 3)).Append(", ").Append(N(c.target.y, 3)).Append(", ").Append(N(c.target.z, 3)).Append("]}");
            }
            sb.Append("\n  ],\n  \"parking\": [");
            for (int i = 0; i < p.bays.Count; i++)
            {
                var b = p.bays[i];
                sb.Append(i == 0 ? "\n    " : ",\n    ").Append("{\"x\": ").Append(N(-b.x, 3)).Append(", \"y\": ").Append(N(b.y, 3)).Append(", \"yaw\": ").Append(N(b.yawJs, 6)).Append(", \"z\": ").Append(N(b.z, 3)).Append('}');
            }
            sb.Append("\n  ],\n  \"pois\": [");
            for (int i = 0; i < p.pois.Count; i++)
            {
                var q = p.pois[i];
                sb.Append(i == 0 ? "\n    " : ",\n    ").Append("{\"fall\": ").Append(N(q.fall, 3)).Append(", \"id\": ").Append(Q(q.id)).Append(", \"kind\": ").Append(Q(q.kind))
                  .Append(", \"name\": ").Append(Q(q.name)).Append(", \"r\": ").Append(N(q.r, 3)).Append(", \"x\": ").Append(N(-q.x, 3)).Append(", \"y\": ").Append(N(q.y, 3))
                  .Append(", \"z\": ").Append(N(q.z, 3)).Append('}');
            }
            sb.Append("\n  ],\n  \"version\": 1\n}\n");
            return sb.ToString();
        }

        static string Ring(double2[] ring)
        {
            var sb = new StringBuilder("[");
            for (int k = 0; k < ring.Length; k++) sb.Append(k == 0 ? "[" : ", [").Append(N(-ring[k].x, 3)).Append(", ").Append(N(ring[k].y, 3)).Append(']');
            return sb.Append(']').ToString();
        }

        public static string Buildings(TownSource t)
        {
            var sb = new StringBuilder("{\n  \"buildings\": [");
            bool first = true;
            foreach (var b in t.buildings)
            {
                if (!b.inPlay || b.poly == null || b.poly.Length < 3) continue;
                sb.Append(first ? "\n    " : ",\n    ");
                first = false;
                sb.Append("{\"area\": ").Append(N(b.area, 2)).Append(", \"business\": ").Append(Q(b.business));
                sb.Append(", \"holes\": [").Append(string.Join(", ", b.holes.Select(Ring))).Append(']');
                sb.Append(", \"levels\": ").Append(Q(b.Tag("building:levels"))).Append(", \"open\": ").Append(b.OpenRoof ? "true" : "false");
                sb.Append(", \"osm\": ").Append(Q(b.osm)).Append(", \"padY\": ").Append(N(b.y, 3));
                sb.Append(", \"part\": ").Append(b.part).Append(", \"poly\": ").Append(Ring(b.poly)).Append(", \"roof\": ").Append(Q(b.Tag("roof:shape")));
                sb.Append(", \"type\": ").Append(Q(b.type)).Append('}');
            }
            sb.Append("\n  ],\n  \"format\": \"backroads-world-buildings\",\n");
            sb.Append("  \"note\": \"T3.3: the footprints in the play area (TownData buildings.json, outer CCW in the local frame), JS space; padY = the flat pad under the footprint (W's PadStep), or for an open roof (open: building=roof|canopy|carport, no pad) the DEM at its centroid\",\n");
            sb.Append("  \"version\": 1\n}\n");
            return sb.ToString();
        }

        static readonly Color32 Water = new Color32(120, 170, 205, 255), Road = new Color32(70, 70, 76, 255), Walk = new Color32(175, 172, 165, 255),
            Building = new Color32(120, 82, 60, 255), Grass = new Color32(196, 214, 165, 255), Field = new Color32(222, 205, 150, 255),
            Rock = new Color32(190, 185, 176, 255), Mud = new Color32(160, 150, 120, 255),
            Gravel = new Color32(200, 195, 182, 255), Sand = new Color32(220, 205, 165, 255);

        /// <summary>map.png: size x size pixels over the whole square (north up, east right).</summary>
        public static byte[] Map(WorldBakeResult r, int size = 2048)
        {
            var g = r.grid;
            var bmask = new bool[g.Count];
            foreach (var b in r.town.buildings)
                if (b.inPlay && b.poly != null && b.poly.Length >= 3) Raster.Fill(g, new[] { b.poly }, k => bmask[k] = true);
            var px = new Color32[size * size];
            double mpp = 2 * g.Half / size;
            int K(double x, double z)
            {
                int i = (int)Math.Round(g.GridI(x)), j = (int)Math.Round(g.GridJ(z));
                i = Math.Max(0, Math.Min(g.N, i)); j = Math.Max(0, Math.Min(g.N, j));
                return j * g.VN + i;
            }
            int Band(double h, double step) => (int)Math.Floor(h / step);
            for (int py = 0; py < size; py++)
                for (int pxi = 0; pxi < size; pxi++)
                {
                    double x = -g.Half + (pxi + 0.5) * mpp, z = -g.Half + (py + 0.5) * mpp;
                    int k = K(x, z);
                    Color32 c;
                    byte s = g.surface[k];
                    if (bmask[k]) c = Building;
                    else if (r.river.wet[k] != 0) c = Water;
                    else if (r.applied.carriageway != null && r.applied.carriageway[k] != 0) c = Road;
                    else if (s == 4) c = Road;
                    else if (s == 6) c = Walk;
                    else
                    {
                        c = s == 1 ? Field : s == 2 ? Sand : s == 3 ? Rock : s == 5 ? Gravel : s == 8 ? Mud : Grass;
                        // contours every 10 m (bold every 50 m) where the band changes toward the next pixel east or north
                        double h = g.heights[k];
                        double hx = g.heights[K(x + mpp, z)], hz = g.heights[K(x, z + mpp)];
                        bool c10 = Band(h, 10) != Band(hx, 10) || Band(h, 10) != Band(hz, 10);
                        bool c50 = Band(h, 50) != Band(hx, 50) || Band(h, 50) != Band(hz, 50);
                        if (c50) c = new Color32((byte)(c.r * 0.55), (byte)(c.g * 0.5), (byte)(c.b * 0.45), 255);
                        else if (c10) c = new Color32((byte)(c.r * 0.8), (byte)(c.g * 0.78), (byte)(c.b * 0.74), 255);
                    }
                    px[py * size + pxi] = c;
                }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            try
            {
                tex.SetPixels32(px);
                tex.Apply(false);
                return tex.EncodeToPNG();
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }
    }
}
