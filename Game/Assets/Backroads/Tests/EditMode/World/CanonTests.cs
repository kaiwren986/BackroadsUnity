using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Canon.Editor;
using Backroads.World;
using NUnit.Framework;
using UnityEditor;

namespace Backroads.Tests.EditMode.World
{
    /// <summary>
    /// Canon.ManifestHashes and Canon.Counts (PLAN 6.1 M1 headline gates, T1.1): the importer verified CanonData v1
    /// and carried every record into the ScriptableObjects.
    /// </summary>
    [TestFixture, Category("Canon")]
    public class Canon
    {
        /// <summary>
        /// Every SHA-256 of CanonData/v1/manifest.json and CanonData/refs/models/manifest.json matches its file; the seven
        /// layer hashes equal 03 section 3.2 (tools/oracle/expected.json); the imported .bytes copies hash the same.
        /// </summary>
        [Test]
        public void ManifestHashes()
        {
            var errors = CanonImporter.VerifySources(out var manifestSha);
            Assert.IsEmpty(errors, string.Join("\n", errors));

            var files = CanonImporter.ManifestFiles(CanonImporter.SourceDir);
            Assert.AreEqual(54, files.Count, "CanonData/v1/manifest.json lists 54 files");
            var layers = CanonJson.Obj(CanonJson.Get(WorldTestUtil.Expected(), "layers"));
            Assert.AreEqual(7, layers.Count);
            foreach (var kv in layers)
            {
                var want = CanonJson.Obj(kv.Value);
                Assert.AreEqual(CanonJson.Str(want, "sha256"), files[kv.Key].sha256, kv.Key + " (03 section 3.2)");
                Assert.AreEqual((long)CanonJson.Num(want, "bytes"), files[kv.Key].bytes, kv.Key + " bytes");
            }
            Assert.AreEqual("a8908d7f740ec10b723aeedd778f16b00a38ef7502ac03374bb61d45aaf154a0", files["terrain/heights.f32"].sha256);

            var a = WorldTestUtil.Canon();
            Assert.AreEqual(manifestSha, a.manifestSha256, "the import cache key is the manifest hash");
            Assert.AreEqual(CanonImporter.ImporterVersion, a.importerVersion);
            var imported = CanonImporter.VerifyImportedLayers(a);
            Assert.IsEmpty(imported, string.Join("\n", imported));
            foreach (var l in a.Layers) Assert.AreEqual(files[l.path].sha256, l.sha256, l.path);
        }

        /// <summary>The .bytes layers and the data assets get path-derived GUIDs (PLAN 1.2 [C15]).</summary>
        [Test]
        public void PathDerivedGuids()
        {
            var a = WorldTestUtil.Canon();
            var bad = new List<string>();
            foreach (var l in a.Layers)
            {
                var p = AssetDatabase.GetAssetPath(l.data);
                if (AssetDatabase.AssetPathToGUID(p) != CanonImporter.PathGuid(p)) bad.Add(p);
            }
            foreach (var o in new UnityEngine.Object[] { a, a.world, a.placements, a.vegetation, a.surfaces, a.traffic, a.staticProps, a.buildings, a.trees, a.detail, a.map })
            {
                var p = AssetDatabase.GetAssetPath(o);
                if (AssetDatabase.AssetPathToGUID(p) != CanonImporter.PathGuid(p)) bad.Add(p);
            }
            foreach (var p in new[] { a.buildings.source, a.trees.source })
                if (AssetDatabase.AssetPathToGUID(p) != CanonImporter.PathGuid(p)) bad.Add(p);
            Assert.IsEmpty(bad, "GUIDs not derived from the path:\n" + string.Join("\n", bad));
        }

        /// <summary>A single changed byte in a canon file aborts the import before anything is written.</summary>
        [Test]
        public void MismatchAbortsImport()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "br-canon-tamper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(tmp, "gameplay"));
            try
            {
                File.Copy(Path.Combine(CanonImporter.SourceDir, "manifest.json"), Path.Combine(tmp, "manifest.json"));
                var bytes = File.ReadAllBytes(Path.Combine(CanonImporter.SourceDir, "gameplay", "jobs.json"));
                bytes[bytes.Length / 2] ^= 0x01;
                File.WriteAllBytes(Path.Combine(tmp, "gameplay", "jobs.json"), bytes);
                var errors = CanonImporter.VerifyManifestDir(tmp);
                Assert.IsTrue(errors.Any(e => e.StartsWith("gameplay/jobs.json: sha256")), "tampered file not reported:\n" + string.Join("\n", errors.Take(5)));

                var before = File.GetLastWriteTimeUtc(CanonImporter.ToFull(CanonImporter.CanonAssetPath));
                CanonImporter.SourceDirOverride = tmp;
                var ex = Assert.Throws<CanonImportException>(() => CanonImporter.Import(force: true));
                StringAssert.Contains("hash mismatch", ex.Message);
                Assert.AreEqual(before, File.GetLastWriteTimeUtc(CanonImporter.ToFull(CanonImporter.CanonAssetPath)), "the import wrote before verifying");
            }
            finally
            {
                CanonImporter.SourceDirOverride = null;
                Directory.Delete(tmp, true);
            }
        }

        /// <summary>The M0 counts (PLAN 6.1 M0 headline gate, 03 section 3.2), read back from the imported assets.</summary>
        [Test]
        public void Counts()
        {
            var a = WorldTestUtil.Canon();
            var e = WorldTestUtil.Expected();
            var fails = new List<string>();
            void Eq(string what, long want, long got) { if (want != got) fails.Add($"{what}: expected {want}, got {got}"); }
            long X(string key) => (long)CanonJson.Num(e, key);

            // trees by species
            var veg = a.vegetation;
            Eq("trees", 5265, veg.trees.Length);
            Eq("trees (expected.json)", X("trees"), veg.trees.Length);
            foreach (var kv in CanonJson.Obj(CanonJson.Get(e, "treesBySpecies")))
                Eq("trees " + kv.Key, (long)CanonJson.Num(kv.Value), veg.CountSpecies(veg.SpeciesIndex(kv.Key)));
            Eq("species", 9, veg.species.Length);
            Eq("rocks", 358, veg.rocks.Length);

            // colliders by kind
            var pl = a.placements;
            Eq("colliders", 4013, pl.colliders.Length);
            foreach (var kv in CanonJson.Obj(CanonJson.Get(e, "collidersByKind")))
            {
                var parts = kv.Key.Split(':');
                var type = parts[0] == "box" ? PlacementSet.ColliderType.Box : PlacementSet.ColliderType.Circle;
                Eq("colliders " + kv.Key, (long)CanonJson.Num(kv.Value), pl.CountColliders(type, parts[1]));
            }
            Eq("tree colliders indexing trees", 3523, pl.colliders.Count(c => c.kind == "tree" && c.tree >= 0 && c.tree < veg.trees.Length));

            Eq("lamps", 78, pl.lamps.Length);
            Eq("lamp-post records", 77, pl.typed.Count(t => t.type == "lamp" && t.TryGet("faceRot", out _) && t.TryGet("h", out _)));
            Eq("lots", 59, a.world.lots.Length);
            Eq("lot model placements", 59, pl.buildings.Count(b => b.src == "lot"));
            Eq("model placements with y, sx, sz and M", 72, pl.buildings.Count(b => !double.IsNaN(b.y) && b.sx > 0 && b.sz > 0 && b.m != null && b.m.Length == 16));
            Eq("typed model records", 72, pl.CountTyped("model"));
            Eq("typed silo/dock/fuelStation/crusher/sawShed", 6, pl.CountTyped("silo") + pl.CountTyped("dock") + pl.CountTyped("fuelStation") + pl.CountTyped("crusher") + pl.CountTyped("sawShed"));
            Eq("footprints", X("footprints"), pl.footprints.Length);
            Eq("rings", 7, pl.rings.Length);
            Eq("parking bays", 5, pl.parking.Length);
            Eq("boats", 2, pl.boats.Length);
            Eq("rngCalls", 1220, pl.rngCalls);
            Eq("canon fence ops", 39, pl.opTypes.Count(o => o == "fence"));
            Eq("canon mailbox ops", 34, pl.opTypes.Count(o => o == "mailbox"));

            // roads
            var w = a.world;
            Eq("roads", 24, w.roads.Length);
            Eq("road points", 1722, w.RoadPointCount);
            Eq("paved roads", 6, w.roads.Count(r => r.kind == "paved"));
            Eq("dirt roads", 3, w.roads.Count(r => r.kind == "dirt"));
            Eq("canon streets", 15, w.roads.Count(r => r.kind.StartsWith("street")));
            Eq("POIs", 7, w.pois.Length);
            Eq("hamlets", 2, w.hamlets.Length);
            Eq("bogs", 1, w.bogs.Length);
            Eq("fields", 2, w.fields.Length);
            Eq("cameras", 12, w.cameras.Length);

            // traffic
            var t = a.traffic;
            Eq("traffic nodes", 21, t.nodes.Length);
            Eq("traffic lanes", 53, t.lanes.Length);
            Eq("traffic connectors", 108, t.conns.Length);
            Eq("traffic loops", 5, t.loops.Length);
            Eq("car-only loops", 1, t.CountCarOnlyLoops());
            var rb = t.nodes.Where(n => n.hasRoundabout).ToList();
            Eq("roundabouts", 1, rb.Count);
            if (rb.Count == 1 && (rb[0].rbCx != -70 || rb[0].rbCz != 30 || rb[0].rbR != 12.5f)) fails.Add($"roundabout at ({rb[0].rbCx}, {rb[0].rbCz}) R {rb[0].rbR}");

            // static props buckets (also Render.StaticPropsBuckets)
            foreach (var kv in CanonJson.Obj(CanonJson.Get(e, "staticPropsVertices")))
                Eq("static props " + kv.Key, (long)CanonJson.Num(kv.Value), a.staticProps.buckets.First(b => b.name == kv.Key).vertices);

            // pane counts per model
            foreach (var kv in CanonJson.Obj(CanonJson.Get(e, "panes")))
                Eq("panes " + kv.Key, (long)CanonJson.Num(kv.Value), a.buildings.Find(kv.Key)?.PaneCount ?? -1);

            // surface table
            Eq("surfaces", 9, a.surfaces.names.Length);
            Assert.IsEmpty(fails, string.Join("\n", fails));
        }
    }
}
