using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Backroads.Art.Editor;
using Backroads.Build.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Backroads.Tests.EditMode.Art
{
    /// <summary>
    /// Card T3.4 (Blender buildings, phase 1): the generator's models imported from Assets/Art/Buildings/Millbrook (the
    /// town's buildings of the five types) and Assets/Art/Buildings/_Fixtures (synthetic barns, warehouses, a shared-wall
    /// row, L and T houses, a mill). Each folder's buildings.index.json is the generator's own list (art/buildings/gen).
    /// </summary>
    public partial class Art
    {
        static readonly string[] BuildingSets = { "Assets/Art/Buildings/Millbrook", "Assets/Art/Buildings/_Fixtures" };

        /// <summary>LOD0 triangle budgets per family: the card's (a house 6k, a block or the mill 15k), barns and sheds as
        /// houses, warehouses as blocks (art/buildings/gen/build.py BUDGET_LOD0).</summary>
        static readonly Dictionary<string, int> LodBudget = new Dictionary<string, int>
        {
            ["house"] = 6000, ["block"] = 15000, ["mill"] = 15000, ["warehouse"] = 15000, ["barn"] = 6000,
        };

        const int MaxMaterials = 3;

        static Dictionary<string, object> ReadJson(string path) => BrJson.Obj(BrJson.Parse(File.ReadAllText(path, Encoding.UTF8)), path);

        static List<Dictionary<string, object>> IndexOf(string set)
        {
            var path = Path.Combine(RepoPaths.Game, set, "buildings.index.json");
            Assert.IsTrue(File.Exists(path), path + " missing (run art\\buildings\\gen\\run.ps1)");
            return BrJson.Arr(ReadJson(path)["buildings"]).Select(o => BrJson.Obj(o)).ToList();
        }

        static double Num(object o) => Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture);

        static int Tris(Mesh m)
        {
            long n = 0;
            for (int s = 0; s < m.subMeshCount; s++) n += m.GetIndexCount(s) / 3;
            return (int)n;
        }

        static Transform Child(GameObject root, string name) => root.transform.Cast<Transform>().FirstOrDefault(t => t.name == name);

        /// <summary>
        /// Every building model: one LOD Group on the root with three levels (Body_LOD0..2 and Panes_LOD0..2), LOD0 within
        /// its family's triangle budget, at most three materials and only the two project building materials (remapped on
        /// import), convex UCX_ colliders without renderers, glass panes named Panes_LOD* on M_Bld_Glass with a pane id in
        /// every vertex colour, and one DOOR_ marker per door.
        /// </summary>
        [Test]
        public void BuildingsBudget()
        {
            var problems = new List<string>();
            var maxByFamily = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var countByModel = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int models = 0, panesTotal = 0, colliders = 0, dropped = 0;
            var atlas = AssetDatabase.LoadAssetAtPath<Material>(BuildingMaterials.PathOf(BuildingMaterials.Atlas));
            var glass = AssetDatabase.LoadAssetAtPath<Material>(BuildingMaterials.PathOf(BuildingMaterials.Glass));
            Assert.NotNull(atlas, "M_Bld_Atlas missing (art\\pipeline\\unity-art.ps1 -Steps building-materials)");
            Assert.NotNull(glass, "M_Bld_Glass missing");
            foreach (var set in BuildingSets)
            {
                var entries = IndexOf(set);
                Assert.IsNotEmpty(entries, set + ": no buildings in the index");
                foreach (var e in entries)
                {
                    var path = set + "/" + (string)e["fbx"];
                    var id = $"{path} ({e["osm"]}, {e["model"]})";
                    var family = (string)e["family"];
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (go == null) { problems.Add(id + ": not imported"); continue; }
                    models++;
                    countByModel[(string)e["model"]] = countByModel.TryGetValue((string)e["model"], out var c) ? c + 1 : 1;
                    var mi = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (mi == null || !BuildingMaterials.IsBuilding(path) || mi.bakeAxisConversion || Math.Abs(mi.globalScale - 1f) > 1e-6f)
                        problems.Add(id + ": not imported by the building rules (scale 1, no axis bake)");
                    if (go.transform.localRotation != Quaternion.identity) problems.Add(id + ": root rotation is not identity");

                    var g = go.GetComponent<LODGroup>();
                    if (g == null) { problems.Add(id + ": no LODGroup on the root"); continue; }
                    var lods = g.GetLODs();
                    if (lods.Length != 3) { problems.Add(id + $": {lods.Length} LOD levels, expected 3"); continue; }
                    for (int i = 0; i < 3; i++)
                    {
                        var names = lods[i].renderers.Where(r => r != null).Select(r => r.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                        if (!names.Contains("Body_LOD" + i)) problems.Add(id + $": LOD{i} lacks Body_LOD{i} ({string.Join(",", names)})");
                        if (names.Any(n => !n.EndsWith("_LOD" + i, StringComparison.Ordinal))) problems.Add(id + $": LOD{i} holds {string.Join(",", names)}");
                    }
                    int lod0 = lods[0].renderers.Where(r => r != null).Sum(r => Tris(r.GetComponent<MeshFilter>().sharedMesh));
                    if (!LodBudget.TryGetValue(family, out var budget)) { problems.Add(id + ": unknown family " + family); continue; }
                    if (lod0 > budget) problems.Add(id + $": LOD0 {lod0} triangles > {budget} ({family})");
                    // Unity drops degenerate (millimetre sliver) triangles on import: allow up to 1 %, never more than written
                    int written = (int)Num(e["lod0Tris"]);
                    dropped += written - lod0;
                    if (lod0 > written || written - lod0 > Math.Max(2, written / 100)) problems.Add(id + $": LOD0 {lod0} triangles, the generator wrote {written}");
                    maxByFamily[family] = Math.Max(maxByFamily.TryGetValue(family, out var mx) ? mx : 0, lod0);
                    int lod1 = lods[1].renderers.Where(r => r != null).Sum(r => Tris(r.GetComponent<MeshFilter>().sharedMesh));
                    int lod2 = lods[2].renderers.Where(r => r != null).Sum(r => Tris(r.GetComponent<MeshFilter>().sharedMesh));
                    if (!(lod1 < lod0 && lod2 < lod1)) problems.Add(id + $": LOD triangles do not fall ({lod0}, {lod1}, {lod2})");

                    var renderers = go.GetComponentsInChildren<MeshRenderer>(true);
                    var mats = renderers.SelectMany(r => r.sharedMaterials).Distinct().ToList();
                    if (mats.Count > MaxMaterials) problems.Add(id + $": {mats.Count} materials > {MaxMaterials}");
                    foreach (var m in mats)
                        if (m != atlas && m != glass) problems.Add(id + ": material " + (m == null ? "null" : AssetDatabase.GetAssetPath(m) + " " + m.name) + " is not a building slot");

                    var cols = go.GetComponentsInChildren<MeshCollider>(true);
                    colliders += cols.Length;
                    if (cols.Length == 0) problems.Add(id + ": no colliders");
                    if (cols.Length != (int)Num(e["colliders"])) problems.Add(id + $": {cols.Length} colliders, the generator wrote {e["colliders"]}");
                    foreach (var mc in cols)
                    {
                        if (!mc.convex || mc.sharedMesh == null) problems.Add(id + ": " + mc.name + " is not a convex mesh collider");
                        if (!mc.name.StartsWith("UCX_", StringComparison.Ordinal) || mc.GetComponent<Renderer>() != null) problems.Add(id + ": collider " + mc.name);
                    }

                    int panes = (int)Num(e["panes"]);
                    panesTotal += panes;
                    for (int i = 0; i < 3 && panes > 0; i++)
                    {
                        var t = Child(go, "Panes_LOD" + i);
                        if (t == null) { problems.Add(id + ": Panes_LOD" + i + " missing"); continue; }
                        var r = t.GetComponent<MeshRenderer>();
                        if (r == null || r.sharedMaterial != glass) problems.Add(id + ": Panes_LOD" + i + " is not on M_Bld_Glass");
                        var mesh = t.GetComponent<MeshFilter>().sharedMesh;
                        if (i == 0 && Tris(mesh) != 2 * panes) problems.Add(id + $": Panes_LOD0 has {Tris(mesh)} triangles for {panes} panes");
                        using (var arr = Mesh.AcquireReadOnlyMeshData(mesh))
                        {
                            var d = arr[0];
                            if (!d.HasVertexAttribute(VertexAttribute.Color)) { problems.Add(id + ": Panes_LOD" + i + " has no vertex colours (pane ids)"); continue; }
                            var col = new NativeArray<Color>(d.vertexCount, Allocator.Temp);
                            d.GetColors(col);
                            for (int k = 0; k < col.Length; k++)
                                if (col[k].r < 0.019f || col[k].r > 0.981f) { problems.Add(id + $": pane id {col[k].r} out of 0.02..0.98"); break; }
                            col.Dispose();
                        }
                    }
                    int doors = go.transform.Cast<Transform>().Count(t => t.name.StartsWith("DOOR_", StringComparison.Ordinal));
                    if (doors != BrJson.Arr(e["doors"]).Count) problems.Add(id + $": {doors} DOOR_ markers, the generator wrote {BrJson.Arr(e["doors"]).Count}");
                }
            }
            TestContext.WriteLine($"{models} building models ({string.Join(", ", countByModel.Select(kv => kv.Key + " " + kv.Value))}); " +
                                  $"max LOD0 triangles {string.Join(", ", maxByFamily.Select(kv => $"{kv.Key} {kv.Value}/{LodBudget[kv.Key]}"))}; " +
                                  $"{panesTotal} panes, {colliders} colliders; {dropped} degenerate LOD0 triangles dropped by the importer");
            Assert.IsEmpty(problems, string.Join("\n", problems.Take(60)));
        }

        // ------------------------------------------------------------------------------------------ determinism

        /// <summary>Blender as tools\ci\env.ps1 finds it ($BLENDER), or BR_BLENDER.</summary>
        static string BlenderPath()
        {
            var env = Environment.GetEnvironmentVariable("BR_BLENDER");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Blender Foundation", "Blender 4.5", "blender.exe");
            return File.Exists(p) ? p : null;
        }

        static string TownV1()
        {
            var root = RepoPaths.InRepo("TownData");
            var dirs = Directory.GetDirectories(root).Select(d => Path.Combine(d, "v1")).Where(d => File.Exists(Path.Combine(d, "manifest.json"))).OrderBy(d => d, StringComparer.Ordinal).ToList();
            Assert.AreEqual(1, dirs.Count, "expected one TownData/<town>/v1");
            return dirs[0];
        }

        static string Sha(string file)
        {
            using (var sha = SHA256.Create())
            using (var s = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(s).Select(b => b.ToString("x2")));
        }

        static void RunBlender(string blender, string args, string what)
        {
            var psi = new ProcessStartInfo(blender, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                WorkingDirectory = RepoPaths.Root,
            };
            using (var p = Process.Start(psi))
            {
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } Assert.Fail(what + ": Blender timed out"); }
                var o = outTask.Result + errTask.Result;
                Assert.AreEqual(0, p.ExitCode, what + ": Blender failed:\n" + string.Join("\n", o.Split('\n').Reverse().Take(30).Reverse()));
                StringAssert.Contains("br_buildings: wrote", o, what + ": no output line");
            }
        }

        /// <summary>
        /// The generator twice on a sample (the first building of every model type of the town, and all fixtures) into
        /// two empty folders: both runs write byte-identical FBX files, and they equal the committed ones (so the
        /// committed models are the generator's current output).
        /// </summary>
        [Test]
        public void BuildingsDeterministic()
        {
            var blender = BlenderPath();
            Assert.NotNull(blender, "Blender 4.5 not found (%LOCALAPPDATA%\\Programs\\Blender Foundation\\Blender 4.5, or set BR_BLENDER)");
            var town = IndexOf(BuildingSets[0]);
            var sample = town.GroupBy(e => (string)e["model"]).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.OrderBy(e => (string)e["osm"], StringComparer.Ordinal).First()).ToList();
            var tmp = Path.Combine(Path.GetTempPath(), "br-bld-determinism-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var script = RepoPaths.InRepo("art/buildings/gen/br_buildings.py");
            var profile = RepoPaths.InRepo("Game/Assets/Content/sites/buildingProfile.json");
            var townDir = TownV1();
            var only = string.Join(",", sample.Select(e => (string)e["osm"]));
            var problems = new List<string>();
            int compared = 0;
            try
            {
                for (int run = 1; run <= 2; run++)
                {
                    RunBlender(blender, $"-b --factory-startup --python \"{script}\" -- --town \"{townDir}\" --profile \"{profile}\" --out-dir \"{Path.Combine(tmp, "town" + run)}\" --only {only}", "town run " + run);
                    RunBlender(blender, $"-b --factory-startup --python \"{script}\" -- --town fixtures --profile \"{profile}\" --out-dir \"{Path.Combine(tmp, "fixtures" + run)}\"", "fixtures run " + run);
                }
                foreach (var (set, sub, entries) in new[] { (BuildingSets[0], "town", sample), (BuildingSets[1], "fixtures", IndexOf(BuildingSets[1])) })
                {
                    foreach (var e in entries)
                    {
                        var f = (string)e["fbx"];
                        string a = Path.Combine(tmp, sub + "1", f), b = Path.Combine(tmp, sub + "2", f), committed = Path.Combine(RepoPaths.Game, set, f);
                        if (!File.Exists(a) || !File.Exists(b)) { problems.Add(f + ": not written"); continue; }
                        string ha = Sha(a), hb = Sha(b), hc = File.Exists(committed) ? Sha(committed) : "missing";
                        compared++;
                        if (ha != hb) problems.Add($"{f}: two runs differ ({ha.Substring(0, 12)} vs {hb.Substring(0, 12)})");
                        if (ha != hc) problems.Add($"{f}: differs from the committed file ({ha.Substring(0, 12)} vs {(hc.Length > 12 ? hc.Substring(0, 12) : hc)}): regenerate with art\\buildings\\gen\\run.ps1");
                        if (ha != (string)e["sha256"]) problems.Add($"{f}: the index's sha256 is stale");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
            TestContext.WriteLine($"{compared} FBX files generated twice and compared with the committed ones (sample: {only}; and every fixture)");
            Assert.Greater(compared, 5, "too few files compared");
            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        // ------------------------------------------------------------------------------------------ doors

        /// <summary>
        /// Every DoorSpec (lane S's sites-S3 file when the index names one, else lane A's placeholder specs) on a modelled
        /// building has its opening: a DOOR_&lt;Kind&gt;_&lt;NN&gt; marker within 0.1 m of the spec's point on the
        /// buildings.json wall (NN = the spec's index among its building's specs, file order), at least the spec's width,
        /// and no LOD0 wall triangle across the doorway 1 m above the sill.
        /// </summary>
        [Test]
        public void DoorsMatchSites()
        {
            const double Tol = 0.1;
            var set = BuildingSets[0];
            var indexPath = Path.Combine(RepoPaths.Game, set, "buildings.index.json");
            var index = ReadJson(indexPath);
            var src = (string)index["doors"];
            string specPath = src.StartsWith("placeholder", StringComparison.Ordinal)
                ? RepoPaths.InRepo("art/buildings/millbrook/doors.placeholder.json")
                : RepoPaths.InRepo(src);
            Assert.IsTrue(File.Exists(specPath), "door specs missing: " + specPath);
            var specs = BrJson.Arr(ReadJson(specPath)["doors"]).Select(o => BrJson.Obj(o)).ToList();
            var polys = new Dictionary<string, List<Vector2d>>(StringComparer.Ordinal);
            foreach (var o in BrJson.Arr(ReadJson(Path.Combine(TownV1(), "buildings.json"))["buildings"]))
            {
                var b = BrJson.Obj(o);
                var part = b.TryGetValue("part", out var pv) ? (int)Num(pv) : 0;
                polys[(string)b["osm"] + "#" + part] = BrJson.Arr(b["poly"]).Select(p => { var a = BrJson.Arr(p); return new Vector2d(Num(a[0]), Num(a[1])); }).ToList();
            }
            var entries = BrJson.Arr(index["buildings"]).Select(o => BrJson.Obj(o)).ToDictionary(e => (string)e["osm"] + "#" + (int)Num(e["part"]), e => e, StringComparer.Ordinal);
            var problems = new List<string>();
            var perBuilding = new Dictionary<string, int>(StringComparer.Ordinal);
            int checkedDoors = 0, skipped = 0;
            double worst = 0;
            var meshCache = new Dictionary<string, (Vector3[] v, int[] t)>();
            foreach (var s in specs)
            {
                var key = (string)s["building"] + "#" + (s.TryGetValue("part", out var pv) ? (int)Num(pv) : 0);
                int k = perBuilding.TryGetValue(key, out var kk) ? kk : 0;
                perBuilding[key] = k + 1;
                if (!entries.TryGetValue(key, out var e)) { skipped++; continue; }   // a grey-box type (T3.3) until phase 2
                var kind = s["kind"] is string ks ? ks : ((Backroads.Core.WorldBake.DoorKind)(int)Num(s["kind"])).ToString();
                var poly = polys[key];
                int edge = (int)Num(s["wallEdge"]);
                var a = poly[edge];
                var bb = poly[(edge + 1) % poly.Count];
                var dir = (bb - a).Normalized;
                var p = a + dir * Num(s["offset"]);
                var path = set + "/" + (string)e["fbx"];
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (go == null) { problems.Add(path + " not imported"); continue; }
                var name = $"DOOR_{kind}_{k:00}";
                var marker = Child(go, name);
                if (marker == null) { problems.Add($"{key}: no {name} in {path}"); continue; }
                var origin = BrJson.Arr(e["origin"]).Select(Num).ToArray();
                var mp = new Vector2d(origin[0] + marker.localPosition.x, origin[2] + marker.localPosition.z);
                double dist = (mp - p).Length;
                worst = Math.Max(worst, dist);
                if (dist > Tol) problems.Add($"{key} {name}: opening {dist:F3} m from the spec (limit {Tol})");
                var info = BrJson.Arr(e["doors"]).Select(o => BrJson.Obj(o)).FirstOrDefault(d => (string)d["name"] == name);
                if (info == null || Num(info["width"]) < Num(s["width"]) - Tol) problems.Add($"{key} {name}: opening narrower than the spec's {Num(s["width"]):F2} m");
                // the doorway is open in the LOD0 wall: no Body_LOD0 triangle through the point 1 m above the sill
                if (!meshCache.TryGetValue(path, out var mesh))
                {
                    var body = Child(go, "Body_LOD0");
                    var mf = body.GetComponent<MeshFilter>().sharedMesh;
                    using (var arr = Mesh.AcquireReadOnlyMeshData(mf))
                    {
                        var d = arr[0];
                        var v = new NativeArray<Vector3>(d.vertexCount, Allocator.Temp);
                        d.GetVertices(v);
                        var tris = new List<int>();
                        for (int sm = 0; sm < d.subMeshCount; sm++)
                        {
                            var idx = new NativeArray<int>(d.GetSubMesh(sm).indexCount, Allocator.Temp);
                            d.GetIndices(idx, sm);
                            tris.AddRange(idx);
                            idx.Dispose();
                        }
                        var m = go.transform.worldToLocalMatrix * body.localToWorldMatrix;
                        mesh = (v.Select(x => m.MultiplyPoint3x4(x)).ToArray(), tris.ToArray());
                        v.Dispose();
                    }
                    meshCache[path] = mesh;
                }
                var q = marker.localPosition + Vector3.up * 1.0f;
                if (Blocks(mesh.v, mesh.t, q)) problems.Add($"{key} {name}: the LOD0 wall is closed across the doorway");
                checkedDoors++;
            }
            TestContext.WriteLine($"{checkedDoors} door specs checked from {RepoPaths.Relative(specPath)} ({(src.StartsWith("placeholder", StringComparison.Ordinal) ? "lane A placeholders until sites-S3" : "sites-S3")}); " +
                                  $"{skipped} on grey-box buildings skipped; worst distance {worst:F4} m");
            Assert.Greater(checkedDoors, 0, "no door specs checked");
            Assert.IsEmpty(problems, string.Join("\n", problems.Take(60)));
        }

        /// <summary>True when a triangle passes within 1 cm of q and covers it.</summary>
        static bool Blocks(Vector3[] v, int[] t, Vector3 q)
        {
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                var n = Vector3.Cross(b - a, c - a);
                float len = n.magnitude;
                if (len < 1e-9f) continue;
                n /= len;
                float dist = Vector3.Dot(q - a, n);
                if (Mathf.Abs(dist) > 0.01f) continue;
                var p = q - n * dist;
                if (Vector3.Dot(Vector3.Cross(b - a, p - a), n) >= -1e-6f && Vector3.Dot(Vector3.Cross(c - b, p - b), n) >= -1e-6f &&
                    Vector3.Dot(Vector3.Cross(a - c, p - c), n) >= -1e-6f) return true;
            }
            return false;
        }

        struct Vector2d
        {
            public double x, y;
            public Vector2d(double x, double y) { this.x = x; this.y = y; }
            public static Vector2d operator -(Vector2d a, Vector2d b) => new Vector2d(a.x - b.x, a.y - b.y);
            public static Vector2d operator +(Vector2d a, Vector2d b) => new Vector2d(a.x + b.x, a.y + b.y);
            public static Vector2d operator *(Vector2d a, double s) => new Vector2d(a.x * s, a.y * s);
            public double Length => Math.Sqrt(x * x + y * y);
            public Vector2d Normalized => this * (1.0 / Length);
        }
    }
}
