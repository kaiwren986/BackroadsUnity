using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Build.Editor;
using Backroads.Canon.Editor;
using Backroads.Roads;
using Backroads.Sites;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Backroads.World.Bake
{
    /// <summary>
    /// T3.3: imports WorldData/(world)/v1 (T3.2's bake) for run time, as CanonImporter does for the canon: verifies every
    /// file against the manifest, copies the grid layers as TextAssets and writes a WorldDataAsset into
    /// Assets/Backroads/World/Imported/(world)/Resources (gitignored) with: the layout, the water (one mesh per reach, the
    /// lock chamber, the sheet over the weir), the bridge decks, the buildings (lane A's model from
    /// Assets/Art/Buildings/(world)/buildings.index.json where there is one, else a grey box: the footprint extruded to its
    /// storeys, mapped storeys or lane S's building profile by type, a gable on houses and garages, flat otherwise, walls
    /// and roof on swatches of lane A's palette atlas so one material draws them all), the static colliders (a box per
    /// building, a circle per tree), the vegetation, the places, camera bookmarks, the map, the canon's tree models and
    /// textures, and lane R's road chunks. Cached by the manifest's hash, lane A's index and the importer version.
    ///   menu: Backroads > World > Import WorldData
    ///   batchmode: -executeMethod Backroads.World.Bake.WorldDataImporter.Run [-world millbrook]
    /// </summary>
    public static class WorldDataImporter
    {
        /// <summary>1: T3.3; 2: open roofs (canopies on posts), lane S's site chunks and props; 3: canopy normals.</summary>
        public const int ImporterVersion = 3;
        public const string DefaultWorld = "millbrook";
        public const string ImportedRoot = "Assets/Backroads/World/Imported";

        public static string OutDir(string world) => ImportedRoot + "/" + world;
        public static string AssetPath(string world) => OutDir(world) + "/Resources/" + WorldDataAsset.ResourceName(world) + ".asset";
        public static string ModelsDir(string world) => "Assets/Art/Buildings/" + char.ToUpperInvariant(world[0]) + world.Substring(1);
        public const string AtlasMaterialPath = "Assets/Art/Materials/Buildings/M_Bld_Atlas.mat";

        static readonly (string src, string name)[] LayerFiles =
        {
            ("terrain/heights.f32", "heights"), ("terrain/base.f32", "base"), ("terrain/surface.u8", "surface"), ("terrain/wear.u8", "wear"),
            ("terrain/roadd.u8", "roadd"), ("terrain/mud.u8", "mud"), ("terrain/tint.f32", "tint"),
        };

        [MenuItem("Backroads/World/Import WorldData")]
        static void Menu()
        {
            var r = Import(DefaultWorld, true);
            Debug.Log("[BR] " + r);
        }

        public static void Run()
        {
            int code = 0;
            try
            {
                var a = Environment.GetCommandLineArgs();
                int i = Array.IndexOf(a, "-world");
                Debug.Log("[BR] " + Import(i >= 0 && i + 1 < a.Length ? a[i + 1] : DefaultWorld, a.Contains("-force")));
            }
            catch (Exception e) { Debug.LogException(e); code = 1; }
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }

        public static WorldDataAsset LoadImported(string world = DefaultWorld) => AssetDatabase.LoadAssetAtPath<WorldDataAsset>(AssetPath(world));

        /// <summary>Imports when missing or stale. False with a message when the WorldData is missing or broken.</summary>
        public static bool EnsureImported(out string message, string world = DefaultWorld)
        {
            try { message = Import(world, false); return true; }
            catch (Exception e) { message = "WorldData import failed: " + e.Message; return false; }
        }

        static string Key(string world, string manifestSha)
        {
            var index = Path.Combine(RepoPaths.Game, ModelsDir(world), "buildings.index.json");
            string idx = File.Exists(index) ? TownSource.Sha256File(index) : "none";
            return $"{manifestSha}:{idx}:{ImporterVersion}";
        }

        public static string Import(string world, bool force)
        {
            var src = WorldDataExport.WorldDir(world);
            var manifestPath = Path.Combine(src, "manifest.json");
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("no WorldData: " + manifestPath);
            var manifestSha = TownSource.Sha256File(manifestPath);
            var key = Key(world, manifestSha);
            var existing = LoadImported(world);
            if (!force && existing != null && existing.manifestSha256 == key && existing.Layers.All(l => l.data != null))
                return $"world {world} current (manifest {manifestSha.Substring(0, 12)})";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var problems = Verify(src, manifestPath);
            if (problems.Count > 0) throw new InvalidDataException("WorldData does not match its manifest: " + string.Join("; ", problems.Take(5)));
            if (!CanonImporter.EnsureImported(out var canonMsg)) throw new InvalidOperationException(canonMsg);
            DoImport(world, src, manifestPath, key);
            return $"world {world} imported in {sw.Elapsed.TotalSeconds:F1} s (manifest {manifestSha.Substring(0, 12)})";
        }

        static List<string> Verify(string dir, string manifestPath)
        {
            var bad = new List<string>();
            var m = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(manifestPath)), "manifest");
            foreach (var kv in CanonJson.Obj(CanonJson.Get(m, "files"), "files"))
            {
                var f = CanonJson.Obj(kv.Value);
                var p = Path.Combine(dir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(p)) { bad.Add("missing " + kv.Key); continue; }
                if (TownSource.Sha256File(p) != CanonJson.Str(f, "sha256")) bad.Add(kv.Key + " differs");
            }
            return bad;
        }

        static string Full(string assetPath) => Path.Combine(RepoPaths.Game, assetPath.Replace('/', Path.DirectorySeparatorChar));

        static void DoImport(string world, string src, string manifestPath, string key)
        {
            var outDir = OutDir(world);
            Directory.CreateDirectory(Full(outDir + "/Resources"));
            var ignore = Full(ImportedRoot + "/.gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, "*\n!.gitignore\n");
            foreach (var (s, name) in LayerFiles)
            {
                var dst = Full($"{outDir}/{name}.bytes");
                var from = Path.Combine(src, s.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dst) || TownSource.Sha256File(dst) != TownSource.Sha256File(from)) File.Copy(from, dst, true);
            }
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            foreach (var (_, name) in LayerFiles) AssetDatabase.ImportAsset($"{outDir}/{name}.bytes", ImportAssetOptions.ForceSynchronousImport);

            var m = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(manifestPath)), "manifest");
            var files = CanonJson.Obj(CanonJson.Get(m, "files"), "files");
            var canon = CanonImporter.LoadImported();
            var a = ScriptableObject.CreateInstance<WorldDataAsset>();
            a.worldId = world;
            a.townName = CanonJson.Str(CanonJson.Obj(CanonJson.Get(m, "town"), "town"), "name");
            a.manifestSha256 = key;
            a.bakeCommit = CanonJson.Str(CanonJson.Obj(CanonJson.Get(m, "bake"), "bake"), "commit");
            a.importerVersion = ImporterVersion;
            a.layout = WorldGridLayout.FromManifestFile(manifestPath);
            CanonAsset.Layer L(int i)
            {
                var (s, name) = LayerFiles[i];
                var f = CanonJson.Obj(CanonJson.Get(files, s), s);
                return new CanonAsset.Layer { path = s, bytes = (long)CanonJson.Num(f, "bytes"), sha256 = CanonJson.Str(f, "sha256"), data = AssetDatabase.LoadAssetAtPath<TextAsset>($"{outDir}/{name}.bytes") };
            }
            a.heights = L(0); a.@base = L(1); a.surface = L(2); a.wear = L(3); a.roadd = L(4); a.mud = L(5); a.tint = L(6);
            foreach (var l in a.Layers) if (l.data == null) throw new InvalidOperationException("layer did not import: " + l.path);

            Dictionary<string, object> J(string rel) => CanonJson.Obj(CanonJson.Parse(File.ReadAllText(Path.Combine(src, rel))), rel);
            var meshes = new List<Mesh>();
            Water(a, J("water.json"), meshes);
            Decks(a, J("structures.json"));
            var vegJson = J("vegetation.json");
            a.vegetation = CanonDataBuilders.BuildVegetation(vegJson);
            a.vegetation.name = "Vegetation";
            Places(a, J("places.json"));
            Buildings(world, a, J("buildings.json"), meshes);
            a.buildingMaterial = AssetDatabase.LoadAssetAtPath<Material>(AtlasMaterialPath);
            Colliders(a);
            a.trees = canon != null ? canon.trees : null;
            a.detail = canon != null ? canon.detail : null;
            a.waterNormal = canon != null ? canon.waterNormal : null;
            var map = new Texture2D(2, 2, TextureFormat.RGBA32, true, false) { name = "map", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            map.LoadImage(File.ReadAllBytes(Path.Combine(src, "map.png")), false);
            a.map = map;
            var townId = TownSource.Towns().Count == 1 ? TownSource.DefaultTownId() : null;
            a.roadChunks = townId != null ? RoadChunkSet.LoadTown(townId) : null;
            a.siteChunks = townId != null ? SiteChunks.LoadTown(townId) : null;
            a.siteProps = townId != null ? SitePropSet.LoadTown(townId) : null;
            a.importedAtUtc = DateTime.UtcNow.ToString("o");

            var path = AssetPath(world);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(a, path);
            AssetDatabase.AddObjectToAsset(a.vegetation, a);
            AssetDatabase.AddObjectToAsset(map, a);
            foreach (var mesh in meshes) AssetDatabase.AddObjectToAsset(mesh, a);
            EditorUtility.SetDirty(a);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            WorldDataRuntime.Unload();
        }

        // ------------------------------------------------------------------------------------------ water

        static Vector2[] RingUnity(object o)
        {
            var l = CanonJson.Arr(o);
            var r = new Vector2[l.Count];
            for (int i = 0; i < r.Length; i++) { var p = CanonJson.Doubles(l[i]); r[i] = new Vector2((float)-p[0], (float)p[1]); }
            return r;
        }

        static Mesh Flat(string name, Vector2[] ring, float y)
        {
            var poly = ring.Select(p => new double2(p.x, p.y)).ToArray();
            var tris = PlateSink.Triangulate(poly);
            double area = 0;
            for (int k = 0, l = poly.Length - 1; k < poly.Length; l = k++) area += poly[l].x * poly[k].y - poly[k].x * poly[l].y;
            var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.vertices = ring.Select(p => new Vector3(p.x, y, p.y)).ToArray();
            m.normals = ring.Select(_ => Vector3.up).ToArray();
            m.uv = ring.Select(p => new Vector2(p.x / 8f, p.y / 8f)).ToArray();
            var idx = new List<int>();
            // facing up (+y): clockwise seen from above in Unity's left-handed winding = the polygon's CCW order reversed
            foreach (var t in tris) { if (area >= 0) { idx.Add(t.x); idx.Add(t.z); idx.Add(t.y); } else { idx.Add(t.x); idx.Add(t.y); idx.Add(t.z); } }
            m.triangles = idx.ToArray();
            m.RecalculateBounds();
            return m;
        }

        static void Water(WorldDataAsset a, Dictionary<string, object> w, List<Mesh> meshes)
        {
            var reaches = new List<WorldDataAsset.Reach>();
            var water = new List<Mesh>();
            var reachRings = new List<(Vector2[] ring, float level)>();
            foreach (var ro in CanonJson.Arr(CanonJson.Get(w, "reaches")))
            {
                var r = CanonJson.Obj(ro);
                float level = (float)CanonJson.Num(r, "level");
                reaches.Add(new WorldDataAsset.Reach { id = CanonJson.Str(r, "id"), level = level, wetVertices = CanonJson.I(r, "wetVertices", 0) });
                int n = 0;
                foreach (var po in CanonJson.Arr(CanonJson.Get(r, "polys")))
                {
                    var ring = RingUnity(CanonJson.Get(CanonJson.Obj(po), "outer"));
                    water.Add(Flat($"water {CanonJson.Str(r, "id")} {n++}", ring, level));
                    reachRings.Add((ring, level));
                }
            }
            a.reaches = reaches.ToArray();
            var weir = CanonJson.Obj(CanonJson.Get(w, "weir"), "weir");
            a.weirCrest = (float)CanonJson.Num(weir, "crest", 0);
            var lk = CanonJson.Obj(CanonJson.Get(w, "lock"), "lock");
            a.lockLevel = (float)CanonJson.Num(lk, "level", 0);
            a.lockWallTop = (float)CanonJson.Num(lk, "wallTop", 0);
            int c = 0;
            foreach (var ch in CanonJson.Arr(CanonJson.Get(lk, "chambers"))) water.Add(Flat($"water lock {c++}", RingUnity(ch), a.lockLevel));
            // the sheet over the weir: from the upper water at the crest line down to the lower water 3 m downstream
            if (reaches.Count >= 2)
            {
                float up = reaches.Max(r => r.level), low = reaches.Min(r => r.level);
                int wi = 0;
                foreach (var lo in CanonJson.Arr(CanonJson.Get(weir, "lines")))
                {
                    var line = RingUnity(lo);
                    if (line.Length < 2) continue;
                    // downstream side: the side whose sample point lies in the lower reach
                    var mid = (line[0] + line[line.Length - 1]) * 0.5f;
                    var dir = (line[line.Length - 1] - line[0]).normalized;
                    var n = new Vector2(-dir.y, dir.x);
                    bool InLow(Vector2 p) => reachRings.Any(rr => rr.level == low && Raster.Inside(new double2(p.x, p.y), new[] { rr.ring.Select(q => new double2(q.x, q.y)).ToArray() }));
                    if (!InLow(mid + n * 4f)) n = -n;
                    var v = new List<Vector3>();
                    var t = new List<int>();
                    for (int k = 0; k < line.Length; k++)
                    {
                        v.Add(new Vector3(line[k].x, up, line[k].y));
                        var q = line[k] + n * 3f;
                        v.Add(new Vector3(q.x, low, q.y));
                    }
                    for (int k = 0; k + 1 < line.Length; k++)
                    {
                        int a0 = 2 * k, b0 = a0 + 1, a1 = a0 + 2, b1 = a0 + 3;
                        t.AddRange(new[] { a0, a1, b0, b0, a1, b1, a0, b0, a1, b0, b1, a1 });   // both sides
                    }
                    var m = new Mesh { name = $"water weir {wi++}" };
                    m.SetVertices(v);
                    m.SetUVs(0, v.Select(p => new Vector2(p.x / 8f, p.z / 8f)).ToList());
                    m.SetTriangles(t, 0);
                    m.RecalculateNormals();
                    m.RecalculateBounds();
                    water.Add(m);
                }
            }
            a.waterMeshes = water.ToArray();
            meshes.AddRange(water);
        }

        static void Decks(WorldDataAsset a, Dictionary<string, object> s)
        {
            var l = new List<WorldDataAsset.Deck>();
            foreach (var o in CanonJson.Arr(CanonJson.Get(s, "decks")))
            {
                var d = CanonJson.Obj(o);
                l.Add(new WorldDataAsset.Deck
                {
                    osmWay = CanonJson.Str(d, "osmWay"), over = CanonJson.Str(d, "over"), overRiver = CanonJson.Bool(d, "overRiver"),
                    outline = RingUnity(CanonJson.Get(d, "outline")), yMin = (float)CanonJson.Num(d, "yMin"), yMax = (float)CanonJson.Num(d, "yMax"),
                });
            }
            a.decks = l.ToArray();
        }

        // ------------------------------------------------------------------------------------------ places and bookmarks

        static void Places(WorldDataAsset a, Dictionary<string, object> p)
        {
            var pois = new List<WorldInfo.Poi>();
            foreach (var o in CanonJson.Arr(CanonJson.Get(p, "pois")))
            {
                var q = CanonJson.Obj(o);
                pois.Add(new WorldInfo.Poi
                {
                    id = CanonJson.Str(q, "id"), name = CanonJson.Str(q, "name"), kind = CanonJson.Str(q, "kind"),
                    x = CanonJson.F(q, "x"), z = CanonJson.F(q, "z"), r = CanonJson.F(q, "r"), fall = CanonJson.F(q, "fall"), y = CanonJson.F(q, "y"),
                });
            }
            a.pois = pois.ToArray();
            a.parking = CanonJson.Arr(CanonJson.Get(p, "parking")).Select(o =>
            {
                var b = CanonJson.Obj(o);
                return new PlacementSet.Parking { x = CanonJson.F(b, "x"), z = CanonJson.F(b, "z"), yaw = CanonJson.F(b, "yaw") };
            }).ToArray();
            var marks = new List<WorldDataAsset.Bookmark>();
            foreach (var o in CanonJson.Arr(CanonJson.Get(p, "cameras")))
            {
                var c = CanonJson.Obj(o);
                var pos = CanonJson.Doubles(CanonJson.Get(c, "pos"));
                var tgt = CanonJson.Doubles(CanonJson.Get(c, "target"));
                marks.Add(new WorldDataAsset.Bookmark
                {
                    id = CanonJson.Str(c, "id"), name = CanonJson.Str(c, "name"), fov = CanonJson.F(c, "fov", 55f),
                    position = new Vector3((float)-pos[0], (float)pos[1], (float)pos[2]), target = new Vector3((float)-tgt[0], (float)tgt[1], (float)tgt[2]),
                });
            }
            a.bookmarks = marks.ToArray();
        }

        // ------------------------------------------------------------------------------------------ buildings

        sealed class Profile
        {
            readonly Dictionary<string, object> _keys;
            public Profile()
            {
                var path = Path.Combine(RepoPaths.Game, "Assets/Content/sites/buildingProfile.json");
                _keys = File.Exists(path) ? CanonJson.Obj(CanonJson.Get(CanonJson.Obj(CanonJson.Parse(File.ReadAllText(path))), "keys")) : null;
            }

            public int Storeys(string type)
            {
                var e = _keys != null && type != null ? CanonJson.Get(_keys, "type:" + type) as Dictionary<string, object> : null;
                var st = e != null ? CanonJson.Get(e, "storeys") as Dictionary<string, object> : null;
                var d = st != null ? CanonJson.Get(st, "dist") as Dictionary<string, object> : null;
                double p50 = d != null ? CanonJson.Num(d, "p50", 1) : 1;
                return Math.Max(1, (int)Math.Round(p50));
            }

            public bool Flat(string type)
            {
                var e = _keys != null && type != null ? CanonJson.Get(_keys, "type:" + type) as Dictionary<string, object> : null;
                var rs = e != null ? CanonJson.Get(e, "roofShape") as Dictionary<string, object> : null;
                var sh = rs != null ? CanonJson.Get(rs, "shares") as Dictionary<string, object> : null;
                if (sh == null) return type != "house" && type != "accessory";
                double flat = CanonJson.Num(sh, "flat", 0), gabled = CanonJson.Num(sh, "gabled", 0);
                return flat >= gabled;
            }
        }

        /// <summary>Swatch UV centre of lane A's palette (art/buildings/gen/palette.py: 512 atlas, 32 px cells, v from the bottom).</summary>
        static Vector2 Swatch(int col, int row) => new Vector2((col + 0.5f) * 32f / 512f, 1f - (row + 0.5f) * 32f / 512f);

        static (Vector2 wall, Vector2 roof) Swatches(string type) => type switch
        {
            "house" => (Swatch(0, 0), Swatch(1, 2)),
            "accessory" => (Swatch(9, 5), Swatch(6, 2)),
            "fuel_canopy" => (Swatch(2, 4), Swatch(2, 4)),
            "shop" or "office" or "restaurant" or "motel" => (Swatch(0, 1), Swatch(14, 1)),
            "school" or "civic" or "church" => (Swatch(2, 1), Swatch(14, 1)),
            _ => (Swatch(13, 1), Swatch(14, 1)),
        };

        static void Buildings(string world, WorldDataAsset a, Dictionary<string, object> bj, List<Mesh> meshes)
        {
            var models = new Dictionary<string, (GameObject go, Vector3 origin, float height)>();
            var indexPath = Path.Combine(RepoPaths.Game, ModelsDir(world), "buildings.index.json");
            if (File.Exists(indexPath))
            {
                var idx = CanonJson.Obj(CanonJson.Parse(File.ReadAllText(indexPath)), "buildings.index.json");
                foreach (var o in CanonJson.Arr(CanonJson.Get(idx, "buildings")))
                {
                    var b = CanonJson.Obj(o);
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(ModelsDir(world) + "/" + CanonJson.Str(b, "fbx"));
                    if (go == null) continue;
                    var org = CanonJson.Doubles(CanonJson.Get(b, "origin"));
                    models[CanonJson.Str(b, "osm") + ":" + CanonJson.I(b, "part", 0)] = (go, new Vector3((float)org[0], (float)org[1], (float)org[2]), CanonJson.F(b, "height", 6f));
                }
            }
            var profile = new Profile();
            var list = new List<WorldDataAsset.Building>();
            foreach (var o in CanonJson.Arr(CanonJson.Get(bj, "buildings")))
            {
                var b = CanonJson.Obj(o);
                var fp = RingUnity(CanonJson.Get(b, "poly"));
                var rec = new WorldDataAsset.Building
                {
                    osm = CanonJson.Str(b, "osm"), part = CanonJson.I(b, "part", 0), type = CanonJson.Str(b, "type"), business = CanonJson.Str(b, "business"),
                    padY = CanonJson.F(b, "padY"), area = CanonJson.F(b, "area"), footprint = fp, open = CanonJson.Bool(b, "open"),
                };
                if (models.TryGetValue(rec.osm + ":" + rec.part, out var m))
                {
                    rec.model = m.go; rec.origin = m.origin; rec.height = m.height;
                }
                else if (rec.open)
                {
                    // a canopy: a 0.5 m roof slab over the footprint at 5 m (fuel) or 2.6 m (carports, roofs), on four posts
                    float under = rec.type == "fuel_canopy" ? 5.0f : 2.6f;
                    rec.height = under + 0.5f;
                    rec.origin = new Vector3(fp.Average(p => p.x), rec.padY, fp.Average(p => p.y));
                    rec.greyBox = Canopy($"canopy {rec.osm}:{rec.part}", fp, rec.padY, under, Swatches(rec.type));
                    meshes.Add(rec.greyBox);
                }
                else
                {
                    int storeys = int.TryParse(CanonJson.Str(b, "levels"), out var lv) && lv > 0 ? lv : profile.Storeys(rec.type);
                    bool flat = CanonJson.Str(b, "roof") is string rs ? rs == "flat" : profile.Flat(rec.type);
                    float wallH = rec.type == "fuel_canopy" ? 5.0f : storeys * 3.0f + 0.4f;
                    rec.height = wallH + (flat ? 0 : 2.5f);
                    rec.origin = new Vector3(fp.Average(p => p.x), rec.padY, fp.Average(p => p.y));
                    rec.greyBox = GreyBox($"box {rec.osm}:{rec.part}", fp, rec.padY, wallH, flat, Swatches(rec.type));
                    meshes.Add(rec.greyBox);
                }
                list.Add(rec);
            }
            a.buildings = list.ToArray();
        }

        /// <summary>The four posts of an open roof: 0.3 m square, 0.5 m inside the corners of its oriented rectangle.</summary>
        internal static List<(Vector2 c, Vector2 u)> Posts(Vector2[] fp)
        {
            Rect(fp, out var u, out var w, out float umin, out float umax, out float wmin, out float wmax);
            float du = Mathf.Min(0.5f, (umax - umin) * 0.25f), dw = Mathf.Min(0.5f, (wmax - wmin) * 0.25f);
            var l = new List<(Vector2, Vector2)>();
            foreach (var a in new[] { umin + du, umax - du })
                foreach (var c in new[] { wmin + dw, wmax - dw })
                    l.Add((u * a + w * c, u));
            return l;
        }

        /// <summary>The oriented bounding rectangle along the footprint's longest edge.</summary>
        static void Rect(Vector2[] fp, out Vector2 u, out Vector2 w, out float umin, out float umax, out float wmin, out float wmax)
        {
            int e = 0; float best = -1;
            for (int k = 0; k < fp.Length; k++) { float len = (fp[(k + 1) % fp.Length] - fp[k]).sqrMagnitude; if (len > best) { best = len; e = k; } }
            u = (fp[(e + 1) % fp.Length] - fp[e]).normalized;
            w = new Vector2(-u.y, u.x);
            umin = float.MaxValue; umax = float.MinValue; wmin = float.MaxValue; wmax = float.MinValue;
            foreach (var p in fp)
            {
                float a = Vector2.Dot(p, u), c = Vector2.Dot(p, w);
                umin = Mathf.Min(umin, a); umax = Mathf.Max(umax, a); wmin = Mathf.Min(wmin, c); wmax = Mathf.Max(wmax, c);
            }
        }

        /// <summary>An open roof: a closed slab over the footprint from under to under + 0.5 m (top, bottom, edges) and four
        /// closed posts from 0.6 m under the ground to the slab, every face single-sided and facing out (its own vertices,
        /// so the normals are flat).</summary>
        static Mesh Canopy(string name, Vector2[] fp, float padY, float under, (Vector2 wall, Vector2 roof) sw)
        {
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var t = new List<int>();
            double area = 0;
            for (int k = 0, l = fp.Length - 1; k < fp.Length; l = k++) area += fp[l].x * fp[k].y - fp[k].x * fp[l].y;
            bool ccw = area >= 0;
            float y0 = padY + under, y1 = y0 + 0.5f;
            var tris = PlateSink.Triangulate(fp.Select(p => new double2(p.x, p.y)).ToArray());
            // top (up) and bottom (down)
            foreach (var (y, up) in new[] { (y1, true), (y0, false) })
            {
                int b = v.Count;
                foreach (var p in fp) { v.Add(new Vector3(p.x, y, p.y)); uv.Add(sw.roof); }
                foreach (var tri in tris)
                    if (ccw == up) t.AddRange(new[] { b + tri.x, b + tri.z, b + tri.y });
                    else t.AddRange(new[] { b + tri.x, b + tri.y, b + tri.z });
            }
            void Walls(Vector2[] ring, bool ringCcw, float lo, float hi, Vector2 swatch)
            {
                for (int k = 0; k < ring.Length; k++)
                {
                    var p = ring[k]; var q = ring[(k + 1) % ring.Length];
                    int b = v.Count;
                    v.Add(new Vector3(p.x, lo, p.y)); v.Add(new Vector3(q.x, lo, q.y)); v.Add(new Vector3(q.x, hi, q.y)); v.Add(new Vector3(p.x, hi, p.y));
                    for (int n = 0; n < 4; n++) uv.Add(swatch);
                    if (ringCcw) t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
                    else t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }
            }
            Walls(fp, ccw, y0, y1, sw.roof);
            foreach (var (c, u) in Posts(fp))
            {
                var w = new Vector2(-u.y, u.x);
                // counter-clockwise in (u, w), which is right-handed like (x, z)
                Walls(new[] { c - u * 0.15f - w * 0.15f, c + u * 0.15f - w * 0.15f, c + u * 0.15f + w * 0.15f, c - u * 0.15f + w * 0.15f }, true, padY - 0.6f, y0, sw.wall);
            }
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// The footprint extruded from 0.6 m under the pad to wallH over it (walls facing out), with a flat roof over the
        /// footprint, or a gable over its oriented bounding rectangle (ridge along the long side, 2.5 m high).
        /// </summary>
        static Mesh GreyBox(string name, Vector2[] fp, float padY, float wallH, bool flat, (Vector2 wall, Vector2 roof) sw)
        {
            var v = new List<Vector3>();
            var uv = new List<Vector2>();
            var t = new List<int>();
            double area = 0;
            for (int k = 0, l = fp.Length - 1; k < fp.Length; l = k++) area += fp[l].x * fp[k].y - fp[k].x * fp[l].y;
            bool ccw = area >= 0;
            float y0 = padY - 0.6f, y1 = padY + wallH;
            for (int k = 0; k < fp.Length; k++)
            {
                var p = fp[k]; var q = fp[(k + 1) % fp.Length];
                int b = v.Count;
                v.Add(new Vector3(p.x, y0, p.y)); v.Add(new Vector3(q.x, y0, q.y)); v.Add(new Vector3(q.x, y1, q.y)); v.Add(new Vector3(p.x, y1, p.y));
                for (int n = 0; n < 4; n++) uv.Add(sw.wall);
                // outward faces: for a CCW ring (seen from above, x east, z north) the outside is to the right of p -> q
                if (ccw) t.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
                else t.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }
            if (flat)
            {
                var poly = fp.Select(p => new double2(p.x, p.y)).ToArray();
                int b = v.Count;
                foreach (var p in fp) { v.Add(new Vector3(p.x, y1, p.y)); uv.Add(sw.roof); }
                foreach (var tri in PlateSink.Triangulate(poly))
                    if (ccw) t.AddRange(new[] { b + tri.x, b + tri.z, b + tri.y });
                    else t.AddRange(new[] { b + tri.x, b + tri.y, b + tri.z });
            }
            else
            {
                // oriented rectangle along the longest edge
                int e = 0; float best = -1;
                for (int k = 0; k < fp.Length; k++) { float len = (fp[(k + 1) % fp.Length] - fp[k]).sqrMagnitude; if (len > best) { best = len; e = k; } }
                var u = (fp[(e + 1) % fp.Length] - fp[e]).normalized;
                var w = new Vector2(-u.y, u.x);
                float umin = float.MaxValue, umax = float.MinValue, wmin = float.MaxValue, wmax = float.MinValue;
                foreach (var p in fp)
                {
                    float a = Vector2.Dot(p, u), c = Vector2.Dot(p, w);
                    umin = Mathf.Min(umin, a); umax = Mathf.Max(umax, a); wmin = Mathf.Min(wmin, c); wmax = Mathf.Max(wmax, c);
                }
                Vector3 P(float a, float c, float y) { var q = u * a + w * c; return new Vector3(q.x, y, q.y); }
                float wm = (wmin + wmax) * 0.5f, ry = y1 + 2.5f;
                int b = v.Count;
                v.Add(P(umin, wmin, y1)); v.Add(P(umax, wmin, y1)); v.Add(P(umax, wm, ry)); v.Add(P(umin, wm, ry));
                v.Add(P(umax, wmax, y1)); v.Add(P(umin, wmax, y1));
                for (int n = 0; n < 6; n++) uv.Add(sw.roof);
                // two roof planes and two gable ends, both sides drawn (the rectangle's handedness depends on the footprint)
                int[] q4 = { b, b + 1, b + 2, b, b + 2, b + 3, b + 4, b + 5, b + 3, b + 4, b + 3, b + 2, b, b + 3, b + 5, b + 1, b + 4, b + 2 };
                t.AddRange(q4);
                for (int k = 0; k < q4.Length; k += 3) t.AddRange(new[] { q4[k], q4[k + 2], q4[k + 1] });
            }
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------------------------------ colliders

        /// <summary>A box per building (its footprint's rectangle along the longest edge), a circle per tree with a trunk; JS space.</summary>
        static void Colliders(WorldDataAsset a)
        {
            var l = new List<PlacementSet.Collider>();
            foreach (var b in a.buildings)
            {
                var fp = b.footprint;
                if (fp == null || fp.Length < 3) continue;
                if (b.open)
                {
                    // an open roof stops a vehicle only at its posts
                    foreach (var (pc, pu) in Posts(fp))
                        l.Add(new PlacementSet.Collider
                        {
                            type = PlacementSet.ColliderType.Box, kind = "post", x = -pc.x, z = pc.y, hw = 0.15f, hd = 0.15f,
                            rot = Mathf.Atan2(-pu.y, -pu.x), y0 = b.padY - 0.6f, y1 = b.padY + b.height, tree = -1,
                        });
                    continue;
                }
                int e = 0; float best = -1;
                for (int k = 0; k < fp.Length; k++) { float len = (fp[(k + 1) % fp.Length] - fp[k]).sqrMagnitude; if (len > best) { best = len; e = k; } }
                var u = (fp[(e + 1) % fp.Length] - fp[e]).normalized;
                var w = new Vector2(-u.y, u.x);
                float umin = float.MaxValue, umax = float.MinValue, wmin = float.MaxValue, wmax = float.MinValue;
                foreach (var p in fp)
                {
                    float x = Vector2.Dot(p, u), z = Vector2.Dot(p, w);
                    umin = Mathf.Min(umin, x); umax = Mathf.Max(umax, x); wmin = Mathf.Min(wmin, z); wmax = Mathf.Max(wmax, z);
                }
                var c = u * ((umin + umax) * 0.5f) + w * ((wmin + wmax) * 0.5f);
                // JS: x_js = -x; the box's local x axis is (cos rot, -sin rot) in JS (three.js rotation.y), here u_js = (-u.x, u.y)
                float rot = Mathf.Atan2(-u.y, -u.x);
                l.Add(new PlacementSet.Collider
                {
                    type = PlacementSet.ColliderType.Box, kind = "building", x = -c.x, z = c.y, hw = (umax - umin) * 0.5f, hd = (wmax - wmin) * 0.5f,
                    rot = rot, y0 = b.padY - 0.6f, y1 = b.padY + Mathf.Max(3f, b.height), tree = -1,
                });
            }
            var veg = a.vegetation;
            for (int i = 0; i < veg.trees.Length; i++)
            {
                var tr = veg.trees[i];
                var sp = veg.species[tr.sp];
                if (sp.trunk <= 0) continue;
                l.Add(new PlacementSet.Collider
                {
                    type = PlacementSet.ColliderType.Circle, kind = "tree", x = tr.x, z = tr.z, r = sp.trunk * tr.s, y0 = tr.y, y1 = tr.y + sp.h * tr.s, tree = i,
                });
            }
            a.colliders = l.ToArray();
        }
    }
}
