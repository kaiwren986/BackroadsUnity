using System.Collections.Generic;
using System.IO;
using System.Linq;
using Backroads.Build.Editor;
using Backroads.Canon.Editor;
using Backroads.World;
using Backroads.World.Bake;
using NUnit.Framework;
using UnityEditor;

namespace Backroads.Tests.EditMode.World
{
    /// <summary>
    /// T3.3: the committed WorldData imports into the WorldDataAsset the town scene loads, and the asset carries what the
    /// files say: the layout, every layer, the water (a surface per reach polygon, the lock, the weir sheet), every
    /// building (lane A's model where its index has one, a grey box elsewhere), the trees, a collider per building and per
    /// tree trunk, the 12 camera views, the map.
    /// </summary>
    public partial class World
    {
        static Dictionary<string, object> WorldJson(string rel)
        {
            var p = Path.Combine(RepoPaths.Root, "WorldData", WorldDataImporter.DefaultWorld, "v1", rel);
            return CanonJson.Obj(CanonJson.Parse(File.ReadAllText(p)), rel);
        }

        static bool InsidePoly(UnityEngine.Vector2[] poly, UnityEngine.Vector2 p)
        {
            if (poly == null || poly.Length < 3) return false;
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            return inside;
        }

        /// <summary>
        /// T3.3 follow-ups (lane S's S4 requests 1-3): the committed WorldData was baked with lane S's site plates (source
        /// "sites"), lane R's apron plates for lane S's driveway requests (the driveways file's hash in the manifest), and no
        /// pad under an open roof (building=roof, canopy, carport), which buildings.json marks "open".
        /// </summary>
        [Test]
        public void SitesInWorldData()
        {
            var m = WorldJson("manifest.json");
            var bake = CanonJson.Obj(CanonJson.Get(m, "bake"), "bake");
            var plates = CanonJson.Obj(CanonJson.Get(bake, "plates"), "plates");
            var sources = CanonJson.Obj(CanonJson.Get(plates, "sources"), "sources");
            Assert.IsTrue(sources.ContainsKey("sites"), "lane S's site plates (source \"sites\") are in the bake");
            Assert.Greater(CanonJson.I(sources, "sites", 0), 0, "site plates");
            Assert.Greater(CanonJson.I(plates, "aprons", 0), 0, "apron plates for the driveways");
            var dw = CanonJson.Obj(CanonJson.Get(bake, "driveways"), "driveways");
            Assert.AreEqual(TownSource.Sha256File(DrivewaySource.PathFor(TownSource.DefaultTownId())), CanonJson.Str(dw, "sha256"), "the driveways file the bake read is the committed one");
            Assert.Greater(CanonJson.I(dw, "cuts", 0), 0, "driveway cuts");

            var t = TownSource.Load(TownSource.DefaultTownId());
            var inPlay = t.buildings.Where(b => b.inPlay && b.poly != null && b.poly.Length >= 3).ToList();
            int openRoofs = inPlay.Count(b => b.OpenRoof);
            Assert.Greater(openRoofs, 0, "the fuel canopy is an open roof");
            Assert.AreEqual(inPlay.Count - openRoofs, CanonJson.I(plates, "pads", 0), "a pad under every building but the open roofs");
            var bj = CanonJson.Arr(CanonJson.Get(WorldJson("buildings.json"), "buildings")).Select(o => CanonJson.Obj(o)).ToList();
            Assert.AreEqual(openRoofs, bj.Count(b => CanonJson.Bool(b, "open")), "buildings.json marks the open roofs");
        }

        [Test]
        public void ImportMatchesData()
        {
            Assert.IsTrue(WorldDataImporter.EnsureImported(out var msg), msg);
            var a = WorldDataImporter.LoadImported();
            Assert.NotNull(a, "no " + WorldDataImporter.AssetPath(WorldDataImporter.DefaultWorld));
            Assert.AreEqual(WorldDataImporter.DefaultWorld, a.worldId);
            Assert.AreEqual(WorldGridLayout.Millbrook.VN, a.layout.VN, "layout VN");
            Assert.AreEqual(WorldGridLayout.Millbrook.Cell, a.layout.Cell, "layout cell");
            Assert.AreEqual(WorldGridLayout.Millbrook.TintN, a.layout.TintN, "layout tint");

            foreach (var l in a.Layers)
            {
                Assert.NotNull(l.data, "layer " + l.path);
                Assert.AreEqual(l.bytes, l.data.bytes.LongLength, "layer bytes " + l.path);
            }

            var water = WorldJson("water.json");
            int polys = CanonJson.Arr(CanonJson.Get(water, "reaches")).Sum(r => CanonJson.Arr(CanonJson.Get(CanonJson.Obj(r), "polys")).Count);
            Assert.AreEqual(CanonJson.Arr(CanonJson.Get(water, "reaches")).Count, a.reaches.Length, "reaches");
            Assert.GreaterOrEqual(a.waterMeshes.Length, polys + 2, "water meshes: a surface per reach polygon, the lock, the weir sheet");
            Assert.IsTrue(a.waterMeshes.All(m => m != null && m.vertexCount >= 3), "empty water mesh");
            Assert.Greater(a.weirCrest, a.reaches.Min(r => r.level), "weir crest above the lower reach");

            var bj = CanonJson.Arr(CanonJson.Get(WorldJson("buildings.json"), "buildings"));
            Assert.AreEqual(bj.Count, a.buildings.Length, "buildings");
            Assert.IsTrue(a.buildings.All(b => (b.model != null) != (b.greyBox != null)), "every building has a model or a grey box, not both");
            Assert.NotNull(a.buildingMaterial, "lane A's atlas material " + WorldDataImporter.AtlasMaterialPath);
            int models = a.buildings.Count(b => b.model != null);
            var index = Path.Combine(RepoPaths.Game, WorldDataImporter.ModelsDir(a.worldId), "buildings.index.json");
            if (File.Exists(index))
            {
                var keys = new HashSet<string>(bj.Select(o => CanonJson.Str(CanonJson.Obj(o), "osm") + ":" + CanonJson.I(CanonJson.Obj(o), "part", 0)));
                int indexed = CanonJson.Arr(CanonJson.Get(CanonJson.Obj(CanonJson.Parse(File.ReadAllText(index)), "index"), "buildings"))
                    .Count(o => keys.Contains(CanonJson.Str(CanonJson.Obj(o), "osm") + ":" + CanonJson.I(CanonJson.Obj(o), "part", 0)));
                Assert.AreEqual(indexed, models, "every building of lane A's index uses its model");
            }

            var veg = WorldJson("vegetation.json");
            Assert.AreEqual(CanonJson.Arr(CanonJson.Get(veg, "trees")).Count, a.vegetation.trees.Length, "trees");
            int trunks = a.vegetation.trees.Count(t => a.vegetation.species[t.sp].trunk > 0);
            int walled = a.buildings.Count(b => !b.open && b.footprint != null && b.footprint.Length >= 3), open = a.buildings.Count(b => b.open);
            Assert.AreEqual(walled + 4 * open + trunks, a.colliders.Length, "a box per building, four posts per open roof, a circle per trunk");
            Assert.AreEqual(4 * open, a.colliders.Count(c => c.kind == "post"), "the open roofs' posts");
            foreach (var b in a.buildings.Where(b => b.open))
                Assert.IsTrue(b.model == null && b.greyBox != null && b.greyBox.name.StartsWith("canopy", System.StringComparison.Ordinal), $"open roof {b.osm} is a canopy");

            var ids = new HashSet<string>(a.bookmarks.Select(b => b.id));
            foreach (var id in new[] { "street", "overview", "dam", "bridge", "landing", "fuel", "quarry", "marsh", "garage", "farm", "depot", "river" })
                Assert.IsTrue(ids.Contains(id), "camera view " + id);
            Assert.IsTrue(a.TryGetPoi("garage", out _), "garage poi");
            // the garage bays as the canon gives them (ActiveWorld.GarageBays): five, 5.6 m apart, none inside a building
            Assert.AreEqual(5, a.parking.Length, "garage bays: the four M2 vehicles and the dozer's, as the canon's");
            for (int i = 0; i < a.parking.Length; i++)
            {
                var p = new UnityEngine.Vector2(-a.parking[i].x, a.parking[i].z);
                if (i > 0) Assert.AreEqual(5.6f, UnityEngine.Vector2.Distance(p, new UnityEngine.Vector2(-a.parking[i - 1].x, a.parking[i - 1].z)), 0.01f, "bay spacing");
                foreach (var b in a.buildings)
                    Assert.IsFalse(InsidePoly(b.footprint, p), $"bay {i} inside building {b.osm}");
            }
            Assert.NotNull(a.map, "map");
            Assert.AreEqual(2048, a.map.width, "map size");
            Assert.NotNull(a.roadChunks, "lane R's road chunks");
            Assert.NotNull(a.siteChunks, "lane S's site chunks");
            Assert.NotNull(a.siteProps, "lane S's site props");
        }
    }
}
