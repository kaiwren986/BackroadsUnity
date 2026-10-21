using System;
using System.Collections.Generic;

namespace Backroads.Core.WorldBake
{
    /// <summary>The classifier's inputs for one building, as tools/osm/measure.mjs typeOf reads them.</summary>
    public struct ClassifierInput
    {
        /// <summary>The building's own OSM tags.</summary>
        public IReadOnlyDictionary<string, string> Tags;
        /// <summary>Tags of the shop/amenity/craft/office/tourism points mapped inside it or within 12 m.</summary>
        public IReadOnlyList<IReadOnlyDictionary<string, string>> Pois;
        /// <summary>The landuse=* values of the land-use areas that contain its centroid (farmyard, quarry, industrial ...).</summary>
        public ICollection<string> Landuse;
        /// <summary>Footprint area of the outer ring, m2.</summary>
        public double Area;
        public bool InFuel, NearFuel, InMarina, InWorks, InSawmill;
    }

    /// <summary>The classifier's answer: measure.mjs's type string, sub-type and inferred flag, and the BusinessType.</summary>
    public readonly struct BuildingClass
    {
        public readonly string Type;
        public readonly string Sub;
        public readonly bool Inferred;

        public BuildingClass(string type, string sub = null, bool inferred = false) { Type = type; Sub = sub; Inferred = inferred; }

        public BusinessType Business => BusinessClassifier.BusinessOf(Type);
        public override string ToString() => Type + (Sub != null ? "/" + Sub : "") + (Inferred ? " (inferred)" : "");
    }

    /// <summary>
    /// The business type of a building (overview contract 5): a line-by-line C# port of tools/osm/measure.mjs typeOf,
    /// first match wins. Kept in step with the JS by the fixture test Town.ClassifierFixture (200 shared cases in
    /// Tests/EditMode/Town/Fixtures/classifier-fixture.json, which tools/world/classifier-fixture.mjs --check runs on the
    /// JS side). The JS patterns are anchored, case-sensitive regular expressions; here they are exact string sets, and
    /// "/./" (any value) means "has a character that is not a line terminator", as in JavaScript.
    /// </summary>
    public static class BusinessClassifier
    {
        static HashSet<string> Set(params string[] s) => new HashSet<string>(s, StringComparer.Ordinal);

        static readonly HashSet<string> House = Set("house", "detached", "residential", "semidetached_house", "semi", "bungalow", "cabin", "duplex", "terrace", "farm", "static_caravan", "townhouse");
        static readonly HashSet<string> Accessory = Set("garage", "garages", "shed", "carport", "outbuilding", "hut", "shelter");
        static readonly HashSet<string> FarmBuilding = Set("barn", "farm_auxiliary", "cowshed", "stable", "greenhouse", "silo", "grain_silo", "sty", "chicken_coop");
        static readonly HashSet<string> RoofCanopy = Set("roof", "canopy");
        static readonly HashSet<string> Silo = Set("silo", "grain_silo");
        static readonly HashSet<string> CarShop = Set("car_repair", "car", "tyres", "motorcycle", "car_parts", "truck");
        static readonly HashSet<string> MotelTourism = Set("motel", "hotel", "guest_house");
        static readonly HashSet<string> MotelBuilding = Set("hotel", "motel");
        static readonly HashSet<string> ChurchBuilding = Set("church", "chapel", "cathedral", "temple", "mosque", "synagogue");
        static readonly HashSet<string> Schools = Set("school", "college", "kindergarten", "university");
        static readonly HashSet<string> CivicAmenity = Set("townhall", "post_office", "fire_station", "police", "library", "community_centre", "courthouse", "clinic",
            "hospital", "doctors", "dentist", "social_facility", "nursing_home", "arts_centre", "theatre", "cinema");
        static readonly HashSet<string> CivicBuilding = Set("civic", "public", "government", "fire_station", "hospital", "townhall", "train_station");
        static readonly HashSet<string> RestaurantAmenity = Set("restaurant", "cafe", "bar", "pub", "ice_cream", "biergarten");
        static readonly HashSet<string> RetailBuilding = Set("retail", "commercial", "kiosk");
        static readonly HashSet<string> WarehouseBuilding = Set("warehouse", "storage", "hangar", "transportation");
        static readonly HashSet<string> IndustrialBuilding = Set("industrial", "manufacture", "factory", "works");
        static readonly HashSet<string> ApartmentBuilding = Set("apartments", "dormitory");
        static readonly HashSet<string> ChurchChapel = Set("church", "chapel");

        static readonly Func<string, bool> Fuel = v => v == "fuel";
        static readonly Func<string, bool> Sawmill = v => v == "sawmill";
        static readonly Func<string, bool> CarWash = v => v == "car_wash";
        static readonly Func<string, bool> Mechanic = v => v == "mechanic";
        static readonly Func<string, bool> Vehicle = v => v.StartsWith("vehicle", StringComparison.Ordinal);
        static readonly Func<string, bool> Worship = v => v == "place_of_worship";
        static readonly Func<string, bool> Supermarket = v => v == "supermarket";
        static readonly Func<string, bool> FastFood = v => v == "fast_food";
        static readonly Func<string, bool> Bank = v => v == "bank";

        /// <summary>JavaScript's /./ : at least one character that is not a line terminator (\n, \r, U+2028, U+2029).</summary>
        static bool AnyValue(string v)
        {
            foreach (var c in v) if (c != '\n' && c != '\r' && c != (char)0x2028 && c != (char)0x2029) return true;
            return false;
        }

        static Func<string, bool> In(HashSet<string> s) => v => s.Contains(v);

        static string Get(IReadOnlyDictionary<string, string> t, string k) => t != null && t.TryGetValue(k, out var v) ? v : null;

        /// <summary>has(k, re): the building's own tags or any attached point has key k with a value matching.</summary>
        static bool Has(in ClassifierInput b, string key, Func<string, bool> match)
        {
            var v = Get(b.Tags, key);
            if (v != null && match(v)) return true;
            if (b.Pois != null)
                foreach (var p in b.Pois)
                {
                    var pv = Get(p, key);
                    if (pv != null && match(pv)) return true;
                }
            return false;
        }

        static bool Lu(in ClassifierInput b, string use) => b.Landuse != null && b.Landuse.Contains(use);

        public static BuildingClass Classify(in ClassifierInput b)
        {
            var t = b.Tags;
            var bt = Get(t, "building");
            if (string.IsNullOrEmpty(bt)) bt = ""; // t.building || ''
            if (RoofCanopy.Contains(bt) || Get(t, "man_made") == "canopy")
                return new BuildingClass(b.NearFuel || Get(t, "amenity") == "fuel" ? "fuel_canopy" : "roof");
            if (b.InFuel || Has(b, "amenity", Fuel)) return new BuildingClass("fuel_shop");
            if (Lu(b, "quarry")) return new BuildingClass("quarry");
            if (b.InSawmill || Has(b, "craft", Sawmill) || Has(b, "industrial", Sawmill)) return new BuildingClass("mill", "sawmill");
            if (Silo.Contains(bt) || Get(t, "man_made") == "silo")
                return Lu(b, "farmyard") ? new BuildingClass("farm", "silo") : new BuildingClass("mill", "grain_elevator");
            if (Lu(b, "farmyard"))
                return new BuildingClass("farm", House.Contains(bt) ? "farmhouse" : FarmBuilding.Contains(bt) ? bt : Accessory.Contains(bt) ? "accessory" : "other");
            if (Lu(b, "harbour") || Lu(b, "port") || b.InMarina || bt == "boathouse") return new BuildingClass("harbor");
            if (Has(b, "amenity", CarWash) || Has(b, "shop", In(CarShop)) || Has(b, "craft", Mechanic) || Has(b, "service", Vehicle)) return new BuildingClass("car_repair");
            if (Has(b, "tourism", In(MotelTourism)) || MotelBuilding.Contains(bt)) return new BuildingClass("motel");
            if (Has(b, "amenity", Worship) || ChurchBuilding.Contains(bt)) return new BuildingClass("church");
            if (Has(b, "amenity", In(Schools)) || Schools.Contains(bt)) return new BuildingClass("school");
            if (Has(b, "amenity", In(CivicAmenity)) || CivicBuilding.Contains(bt)) return new BuildingClass("civic");
            if (Has(b, "shop", Supermarket) || bt == "supermarket") return new BuildingClass("supermarket");
            if (Has(b, "amenity", FastFood)) return new BuildingClass("fast_food");
            if (Has(b, "amenity", In(RestaurantAmenity))) return new BuildingClass("restaurant");
            if (Has(b, "amenity", Bank) || Has(b, "office", AnyValue) || bt == "office") return new BuildingClass("office");
            if (Has(b, "shop", AnyValue) || RetailBuilding.Contains(bt)) return new BuildingClass("shop");
            if (WarehouseBuilding.Contains(bt)) return new BuildingClass("warehouse");
            if (IndustrialBuilding.Contains(bt) || b.InWorks) return new BuildingClass("industrial");
            if (ApartmentBuilding.Contains(bt)) return new BuildingClass("apartments");
            if (House.Contains(bt)) return new BuildingClass("house");
            if (Accessory.Contains(bt)) return new BuildingClass("accessory", bt);
            if (FarmBuilding.Contains(bt)) return new BuildingClass("farm", bt);
            if (ChurchChapel.Contains(bt)) return new BuildingClass("church");
            // building=yes (or something rare): land use first, then size, as the browser game's town study did
            double a = b.Area;
            if (Lu(b, "industrial")) return new BuildingClass(a < 60 ? "accessory" : a > 1200 ? "warehouse" : "industrial", null, true);
            if (Lu(b, "retail") || Lu(b, "commercial")) return new BuildingClass(a < 40 ? "accessory" : "shop", null, true);
            if (a < 55) return new BuildingClass("accessory", null, true);
            if (a < 320) return new BuildingClass("house", null, true);
            return new BuildingClass(a > 1500 ? "warehouse" : "shop", null, true);
        }

        /// <summary>measure.mjs type string to BusinessType (ADR-007).</summary>
        public static BusinessType BusinessOf(string type)
        {
            switch (type)
            {
                case "house": return BusinessType.House;
                case "apartments": return BusinessType.Apartments;
                case "accessory": return BusinessType.Accessory;
                case "shop": return BusinessType.Retail;
                case "office": return BusinessType.Office;
                case "restaurant": return BusinessType.Restaurant;
                case "fast_food": return BusinessType.FastFood;
                case "supermarket": return BusinessType.Supermarket;
                case "fuel_shop":
                case "fuel_canopy": return BusinessType.Fuel;
                case "car_repair": return BusinessType.CarRepair;
                case "motel": return BusinessType.Motel;
                case "civic": return BusinessType.Civic;
                case "church": return BusinessType.Church;
                case "school": return BusinessType.School;
                case "warehouse": return BusinessType.Warehouse;
                case "industrial": return BusinessType.Industrial;
                case "farm": return BusinessType.Farm;
                case "quarry": return BusinessType.Quarry;
                case "mill": return BusinessType.Mill;
                case "harbor": return BusinessType.Harbor;
                case "roof": return BusinessType.Roof;
                default: return BusinessType.Unknown;
            }
        }
    }
}
