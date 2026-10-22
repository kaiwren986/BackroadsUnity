using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Sites;
using Backroads.Sites.Editor;
using Unity.Mathematics;

namespace Backroads.Tests.EditMode.Sites
{
    /// <summary>
    /// One synthetic parcel per business type for the sites-S3 snapshot check ("the plan for a fixture parcel of each type
    /// matches a reviewed snapshot"): a straight street along x (z = 0 is the walk's back edge; kerbed with a 2 m walk, or
    /// unkerbed), the parcel north of it, its buildings, and for street-front sites an alley behind. Real profile and
    /// templates; lane R's DrivewayCutter on the test street.
    /// </summary>
    public static class PlanFixtures
    {
        /// <summary>The repository root (null: RepoPaths.Root inside Unity).</summary>
        public static string RepoRoot;

        public sealed class Fixture
        {
            public string name;
            public PlanContext ctx;
            public int parcel;
        }

        sealed class Street : IKerbFrontageSource
        {
            readonly List<KerbFrontage> m_F = new List<KerbFrontage>();
            public Street(IEnumerable<FrontLine> fronts)
            {
                foreach (var f in fronts)
                {
                    int n = f.s.Length;
                    m_F.Add(new KerbFrontage
                    {
                        faceId = f.faceId, osmWay = f.osmWay, kerb = f.kerb, s = f.s, topOfKerb = Enumerable.Repeat(f.kerbed ? 0.15f : 0f, n).ToArray(),
                        gutter = new float[n], walkBack = f.back, walkBackY = Enumerable.Repeat(f.kerbed ? 0.17f : 0f, n).ToArray(),
                        kerbKind = f.kerbed ? KerbKind.Barrier150 : KerbKind.None, streetClass = f.street, speedKmh = 40, forbidden = Array.Empty<ForbiddenInterval>(),
                    });
                }
            }
            public bool Provisional => false;
            public IReadOnlyList<KerbFrontage> Faces => m_F;
            public KerbFrontage Face(int id) => m_F.FirstOrDefault(f => f.faceId == id);
        }

        static FrontLine Face(int id, double x0, double x1, double z, bool kerbed, bool reverse = false, bool alley = false)
        {
            var kerb = new List<double2>();
            var back = new List<double2>();
            var s = new List<float>();
            int n = (int)Math.Ceiling((x1 - x0) / 5.0);
            for (int i = 0; i <= n; i++)
            {
                double x = x0 + (x1 - x0) * i / n;
                double xx = reverse ? x1 - (x - x0) : x;
                double off = kerbed ? 2.0 : 0.0;
                kerb.Add(new double2(xx, reverse ? z + off : z - off));
                back.Add(new double2(xx, z));
                s.Add((float)(x - x0));
            }
            return new FrontLine { faceId = id, osmWay = "way/" + (900 + id), street = alley ? StreetClass.Yard : StreetClass.Street, kerbed = kerbed, alley = alley, block = 0,
                kerb = kerb.ToArray(), back = back.ToArray(), s = s.ToArray() };
        }

        static double2[] Rect(double x0, double z0, double x1, double z1) =>
            new[] { new double2(x0, z0), new double2(x1, z0), new double2(x1, z1), new double2(x0, z1) };

        sealed class B
        {
            public double2[] poly;
            public BusinessType type;
            public BuildingForm form;
            public Dictionary<string, string> tags = new Dictionary<string, string> { { "building", "yes" } };
        }

        /// <summary>A fixture: parcel x0..x1 by 0..depth north of the street, its buildings (the first is the main one).</summary>
        static Fixture Make(string name, BusinessType type, double x0, double x1, double depth, IEnumerable<B> bs, bool kerbed = true, bool alley = false,
            BuildingForm form = BuildingForm.None, Action<SiteTownData, ParcelSet> extra = null)
        {
            var data = new SiteTownData { townId = "fixture", playHalf = 5000 };
            foreach (var b in bs)
            {
                var sb = new SiteBuilding
                {
                    index = data.buildings.Count, osm = "way/" + (100 + data.buildings.Count), poly = Poly2.Ccw(b.poly), business = b.type, form = b.form, inPlay = true,
                    tags = b.tags, type = b.type.ToString(),
                };
                sb.centroid = Poly2.Centroid(sb.poly);
                sb.area = Poly2.Area(sb.poly);
                data.buildings.Add(sb);
            }
            var blocks = new BlockBuilder.Result { ruralDepth = 120 };
            var street = Face(1, -200, 200, 0, kerbed);
            blocks.fronts.Add(street);
            if (alley) blocks.fronts.Add(Face(2, x0 - 30, x1 + 30, depth, false, true, true));
            var outer = Rect(x0, 0, x1, depth);
            blocks.blocks.Add(new Block { id = 0, loop = 0, area = Poly2.Area(outer), outers = new List<double2[]> { outer }, faces = blocks.fronts.Select(f => f.faceId).ToArray() });
            blocks.regions.Add(Clip.Region(outer));
            var set = new ParcelSet { town = "fixture", data = data, blocks = blocks };
            var frontage = new List<FrontageRef> { new FrontageRef { faceId = 1, s0 = (float)(x0 + 200), s1 = (float)(x1 + 200), street = street.street, osmWay = street.osmWay } };
            if (alley) frontage.Add(new FrontageRef { faceId = 2, s0 = 30, s1 = (float)(x1 - x0 + 30), street = StreetClass.Yard, osmWay = "way/902" });
            var parcel = new Parcel
            {
                id = 0, block = 0, kind = data.buildings.Count > 0 ? ParcelKind.Building : ParcelKind.Vacant, outer = outer, area = Poly2.Area(outer),
                buildings = data.buildings.Select(b => b.index).ToArray(), type = type, form = form, frontage = frontage.ToArray(), alley = alley,
                depth = (float)depth,
            };
            set.parcels.Add(parcel);
            set.regions.Add(Clip.Region(outer));
            set.parcelOfBuilding = data.buildings.Select(_ => 0).ToArray();
            extra?.Invoke(data, set);
            set.hash = ParcelBuilder.Hash(set);
            var src = new Street(blocks.fronts);
            var repo = (RepoRoot ?? Backroads.Build.Editor.RepoPaths.Root);
            var ctx = new PlanContext("fixture", data, set) { kerbs = src, cutter = new DrivewayCutter(src), profile = SiteProfile.Load(repo), templates = SiteTemplates.Load(repo) };
            return new Fixture { name = name, ctx = ctx, parcel = 0 };
        }

        static B Box(double x0, double z0, double x1, double z1, BusinessType t, BuildingForm f = BuildingForm.None, string building = "yes", params (string k, string v)[] tags)
        {
            var b = new B { poly = Rect(x0, z0, x1, z1), type = t, form = f };
            b.tags["building"] = building;
            foreach (var (k, v) in tags) b.tags[k] = v;
            return b;
        }

        public static IEnumerable<Fixture> All()
        {
            yield return Make("house", BusinessType.House, -10, 10, 40, new[] { Box(-4.5, 7, 4.5, 18, BusinessType.House, building: "house") });
            yield return Make("house_garage", BusinessType.House, -11, 11, 45, new[]
            {
                Box(-6, 6, 4, 17, BusinessType.House, building: "house"), Box(3, 24, 9.5, 31, BusinessType.Accessory, building: "garage"),
            });
            yield return Make("house_rural", BusinessType.House, -25, 25, 60, new[] { Box(-5, 20, 5, 32, BusinessType.House, building: "house") }, kerbed: false);
            yield return Make("downtown", BusinessType.Retail, -8, 8, 40, new[] { Box(-8, 0, 8, 22, BusinessType.Retail, BuildingForm.Streetfront, "commercial", ("shop", "hardware")) },
                alley: true, form: BuildingForm.Streetfront);
            yield return Make("retail", BusinessType.Retail, -30, 30, 55, new[] { Box(-10, 30, 10, 50, BusinessType.Retail, building: "retail", tags: ("shop", "variety_store")) });
            yield return Make("office", BusinessType.Office, -20, 20, 50, new[] { Box(-14, 12, 0, 30, BusinessType.Office, tags: ("office", "insurance")) });
            yield return Make("restaurant", BusinessType.Restaurant, -22, 23, 50, new[] { Box(-7, 18, 8, 36, BusinessType.Restaurant, tags: ("amenity", "restaurant")) });
            yield return Make("fast_food", BusinessType.FastFood, -28, 28, 52, new[] { Box(2, 22, 14, 42, BusinessType.FastFood, tags: ("amenity", "fast_food")) });
            yield return Make("supermarket", BusinessType.Supermarket, -55, 55, 120, new[] { Box(-30, 55, 30, 100, BusinessType.Supermarket, tags: ("shop", "supermarket")) });
            yield return Make("fuel", BusinessType.Fuel, -30, 30, 50, new[]
            {
                Box(-7, 26, 7, 46, BusinessType.Retail, tags: ("shop", "convenience")), Box(-8, 9, 8, 17, BusinessType.Fuel, building: "roof", tags: ("amenity", "fuel")),
            });
            yield return Make("fuel_no_canopy", BusinessType.Fuel, -30, 30, 50, new[] { Box(-7, 26, 7, 46, BusinessType.Fuel, BuildingForm.None, "yes", ("amenity", "fuel"), ("shop", "convenience")) });
            yield return Make("car_repair", BusinessType.CarRepair, -20, 20, 45, new[] { Box(-9, 10, 9, 22, BusinessType.CarRepair, tags: ("shop", "car_repair")) });
            yield return Make("motel", BusinessType.Motel, -35, 35, 45, new[] { Box(-22, 26, 22, 38, BusinessType.Motel, tags: ("tourism", "motel")) });
            yield return Make("apartments", BusinessType.Apartments, -25, 25, 60, new[] { Box(-20, 8, 10, 23, BusinessType.Apartments, building: "apartments") });
            yield return Make("civic", BusinessType.Civic, -25, 25, 50, new[] { Box(-18, 10, 2, 35, BusinessType.Civic, tags: ("amenity", "post_office")) });
            yield return Make("church", BusinessType.Church, -25, 25, 70, new[] { Box(-20, 12, 0, 47, BusinessType.Church, building: "church", tags: ("amenity", "place_of_worship")) });
            yield return Make("school", BusinessType.School, -60, 60, 90, new[] { Box(-50, 20, 10, 50, BusinessType.School, building: "school", tags: ("amenity", "school")) });
            yield return Make("warehouse", BusinessType.Warehouse, -45, 45, 110, new[] { Box(-30, 15, 0, 65, BusinessType.Warehouse, building: "warehouse") }, kerbed: false);
            yield return Make("industrial", BusinessType.Industrial, -40, 40, 80, new[] { Box(-25, 20, 15, 45, BusinessType.Industrial, building: "industrial") }, kerbed: false);
            yield return Make("farm", BusinessType.Farm, -75, 75, 200, new[]
            {
                Box(-30, 40, -20, 52, BusinessType.Farm, building: "farmhouse"), Box(0, 60, 20, 90, BusinessType.Farm, building: "barn"), Box(-20, 75, -10, 83, BusinessType.Accessory, building: "shed"),
            }, kerbed: false);
            yield return Make("quarry", BusinessType.Quarry, -80, 80, 220, new[] { Box(-8, 90, 7, 110, BusinessType.Quarry, building: "industrial", tags: ("landuse", "quarry")) }, kerbed: false);
            yield return Make("mill", BusinessType.Mill, -35, 35, 80, new[]
            {
                Box(-5, 30, 5, 40, BusinessType.Mill, building: "silo"), Box(10, 25, 25, 45, BusinessType.Accessory, building: "shed"),
            }, kerbed: false);
            yield return Make("harbor", BusinessType.Harbor, -30, 30, 80, new[] { Box(-20, 10, -8, 20, BusinessType.Harbor, building: "boathouse") }, kerbed: false);
            yield return Make("mapped_lot", BusinessType.Unknown, -20, 20, 35, Array.Empty<B>(), extra: (data, set) =>
            {
                var lot = Rect(-19.5, 0.5, 19.5, 34.5);
                set.fixedAreas.Add(new FixedArea { id = 0, osm = "way/700", kind = FixedKind.Parking, outer = lot, area = Poly2.Area(lot), parcel = 0 });
                set.parcels[0].fixedAreas = new[] { 0 };
                var a = new SiteTownData.MappedArea { osm = "way/700" };
                a.tags["amenity"] = "parking";
                a.tags["surface"] = "asphalt";
                a.polys.Add((lot, Array.Empty<double2[]>()));
                data.parking.Add(a);
            });
            yield return Make("retail_mapped_lot", BusinessType.Retail, -35, 35, 50, new[] { Box(-30, 12, -5, 35, BusinessType.Retail, tags: ("shop", "general")) }, extra: (data, set) =>
            {
                var lot = Rect(-2, 2, 33, 45);
                set.fixedAreas.Add(new FixedArea { id = 0, osm = "way/701", kind = FixedKind.Parking, outer = lot, area = Poly2.Area(lot), servedBy = new[] { 0 }, parcel = 0 });
                set.parcels[0].fixedAreas = new[] { 0 };
                var a = new SiteTownData.MappedArea { osm = "way/701" };
                a.tags["amenity"] = "parking";
                a.polys.Add((lot, Array.Empty<double2[]>()));
                data.parking.Add(a);
            });
            var tpl = TemplateFixture();
            if (tpl != null) yield return tpl;
        }

        /// <summary>A retail building with exactly the shape of a real template's building, so that template fits (scale 1).</summary>
        static Fixture TemplateFixture()
        {
            var t = SiteTemplates.Load((RepoRoot ?? Backroads.Build.Editor.RepoPaths.Root)).all.FirstOrDefault(x => x.id == "retail_front-01");
            if (t == null || t.building == null || t.lot == null) return null;
            double minV = t.building.Min(p => p.y), lotMinV = t.lot.Min(p => p.y);
            double shift = minV - lotMinV + 1.5 - minV;   // the lot starts 1.5 m behind the walk
            var poly = t.building.Select(p => new double2(p.x, p.y + shift)).ToArray();
            double x0 = Math.Min(t.lot.Min(p => p.x), t.building.Min(p => p.x)) - 6, x1 = Math.Max(t.lot.Max(p => p.x), t.building.Max(p => p.x)) + 6;
            double depth = poly.Max(p => p.y) + 15;
            var b = new B { poly = poly, type = BusinessType.Restaurant };
            b.tags["amenity"] = "restaurant";
            return Make("retail_template", BusinessType.Restaurant, x0, x1, depth, new[] { b });
        }
    }
}
