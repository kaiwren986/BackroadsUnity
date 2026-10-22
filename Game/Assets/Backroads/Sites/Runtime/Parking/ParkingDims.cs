using System;
using System.Collections.Generic;
using System.Globalization;

namespace Backroads.Sites
{
    /// <summary>
    /// Stall, aisle and lane sizes for the parking engine (card sites-S2 step 2, design §5, research 08 §3.2).
    /// Measured numbers come from siteProfile.json ("parking.stalls", "parking.aisles"); [doc] values are design
    /// standards where OSM is silent (08 §11). Metres.
    /// </summary>
    [Serializable]
    public sealed class ParkingDims
    {
        /// <summary>Stall width: the profile's p50 (2.75 m, n 17).</summary>
        public double stallWidth = 2.75;
        /// <summary>Stall length 5.5 m [doc]: the measured stall polygons are thin and bimodal (p50 5.08, p75 6.89); the
        /// measured module (18.4 m, n 351) = two 5.5 m stalls + a 7.3 m aisle, so the module decides.</summary>
        public double stallLength = 5.5;
        /// <summary>Squeezed stall (the profile's p25 width and length), tried only where it fits one more row.</summary>
        public double squeezeWidth = 2.72, squeezeLength = 5.07;
        /// <summary>Two-way aisle at 90 degrees [doc, 24 ft]; one-way aisles at 60 and 45 degrees [doc, 18 and 13 ft].</summary>
        public double aisleTwoWay = 7.3, aisle60 = 5.5, aisle45 = 4.0;
        /// <summary>Drive lanes that connect aisles to the entrances (two-way and one-way).</summary>
        public double laneTwoWay = 7.3, laneOneWay = 4.5;
        /// <summary>Stalls keep this far inside the lot edge (card step 1).</summary>
        public double edgeMargin = 0.5;
        /// <summary>Stalls keep this far from the building; drive lanes keep laneBuildingClear.</summary>
        public double buildingClear = 1.0, laneBuildingClear = 0.5;
        /// <summary>End islands (at least 1.8 m) and intermediate islands, one every islandEvery stalls (ISLAND_EVERY).</summary>
        public double endIsland = 1.8, islandWidth = 1.8;
        public int islandEvery = 15;
        /// <summary>Lamp pole spacing along the rows.</summary>
        public double lampMin = 25, lampMax = 35;
        /// <summary>Module phase search step (card step 1).</summary>
        public double phaseStep = 0.25;
        /// <summary>ADA 502.2-502.3: accessible stall 96 in, access aisle 60 in; van stall 132 in, or 96 in with a 96 in aisle.</summary>
        public double accWidth = 2.44, accAisle = 1.52, vanWidth = 3.35, vanAisle = 2.44;
        /// <summary>Fire lane 20 ft [doc]; drive-through lane 3.7 m, one car per 6 m, 6-8 cars of stacking (design §4.2).</summary>
        public double fireLaneWidth = 6.1, driveThroughWidth = 3.7, carSpacing = 6.0;
        public int stackMin = 6, stackMax = 8;
        /// <summary>Paint: stall lines 0.1 m, stop bars 0.6 m; wheel stops 1.8 m long, set 0.75 m inside the stall head.</summary>
        public double lineWidth = 0.1, stopBarWidth = 0.6, wheelStopLength = 1.8, wheelStopInset = 0.75;
        /// <summary>The measured aisle-to-aisle module (p25, p50, p75): the 90-degree module is checked against it.</summary>
        public double moduleP25 = 17.3, moduleP50 = 18.4, moduleP75 = 19.6;
        /// <summary>Trailer stalls (warehouse yards, design §4.2): 3.7 x 16 m off a 15 m truck aisle [doc].</summary>
        public double trailerWidth = 3.7, trailerLength = 16, trailerAisle = 15;
        /// <summary>Where the numbers came from ("defaults" or "siteProfile.json").</summary>
        public string source = "defaults";

        public static ParkingDims Default => new ParkingDims();

        /// <summary>The aisle width that goes with a stall angle (90 two-way, 60 and 45 one-way).</summary>
        public double Aisle(double angleDeg, StallKind kind = StallKind.Standard) =>
            kind == StallKind.Trailer ? trailerAisle : angleDeg >= 89.9 ? aisleTwoWay : angleDeg >= 59.9 ? aisle60 : aisle45;

        /// <summary>A stall row's depth across the aisle: length sin a + width cos a (the parallelogram's height).</summary>
        public static double Depth(double width, double length, double angleDeg)
        {
            double a = angleDeg * Math.PI / 180;
            return angleDeg >= 89.9 ? length : length * Math.Sin(a) + width * Math.Cos(a);
        }

        /// <summary>The 90-degree double-loaded module: two stalls and a two-way aisle.</summary>
        public double Module90 => 2 * stallLength + aisleTwoWay;

        public bool ModuleInBand => Module90 >= moduleP25 - 1e-9 && Module90 <= moduleP75 + 1e-9;

        /// <summary>
        /// Reads siteProfile.json as parsed by any JSON reader that gives Dictionary&lt;string, object&gt; for objects and
        /// double for numbers (Backroads.Build.Editor.BrJson): the stall width p50 and the squeeze band (p25), the module
        /// band. The stall length stays 5.5 m [doc] unless the measured module disagrees with it by more than 0.5 m.
        /// </summary>
        public static ParkingDims FromProfile(object siteProfileRoot)
        {
            var d = new ParkingDims { source = "siteProfile.json" };
            var parking = Obj(Get(siteProfileRoot, "parking"));
            var stalls = Obj(Get(parking, "stalls"));
            var aisles = Obj(Get(parking, "aisles"));
            var width = Obj(Get(stalls, "width"));
            var length = Obj(Get(stalls, "length"));
            var module = Obj(Get(aisles, "moduleAisleToAisle"));
            d.stallWidth = Num(width, "p50");
            d.squeezeWidth = Math.Min(d.stallWidth, Num(width, "p25"));
            d.squeezeLength = Math.Min(d.stallLength, Num(length, "p25"));
            d.moduleP25 = Num(module, "p25");
            d.moduleP50 = Num(module, "p50");
            d.moduleP75 = Num(module, "p75");
            double fromModule = (d.moduleP50 - d.aisleTwoWay) / 2;
            if (Math.Abs(fromModule - d.stallLength) > 0.5) d.stallLength = Math.Round(fromModule, 2);
            return d;
        }

        static object Get(object o, string key)
        {
            if (o is Dictionary<string, object> m && m.TryGetValue(key, out var v)) return v;
            throw new FormatException("siteProfile.json: missing \"" + key + "\"");
        }

        static Dictionary<string, object> Obj(object o) => o as Dictionary<string, object> ?? throw new FormatException("siteProfile.json: not an object");

        static double Num(Dictionary<string, object> o, string key)
        {
            var v = Get(o, key);
            if (v is double x) return x;
            if (v is string s) return double.Parse(s, CultureInfo.InvariantCulture);
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The 2010 ADA Standards §208.2 (minimum accessible parking spaces) and §208.2.4 (van spaces).</summary>
    public static class Ada
    {
        /// <summary>Minimum accessible spaces for a lot of `total` spaces (table 208.2).</summary>
        public static int Required(int total)
        {
            if (total <= 0) return 0;
            if (total <= 25) return 1;
            if (total <= 50) return 2;
            if (total <= 75) return 3;
            if (total <= 100) return 4;
            if (total <= 150) return 5;
            if (total <= 200) return 6;
            if (total <= 300) return 7;
            if (total <= 400) return 8;
            if (total <= 500) return 9;
            if (total <= 1000) return (total * 2 + 99) / 100;     // 2 percent, rounded up
            return 20 + (total - 1000 + 99) / 100;                // 20 plus 1 for each 100, or fraction, over 1000
        }

        /// <summary>One in six accessible spaces, or fraction of six, is van accessible (§208.2.4).</summary>
        public static int RequiredVan(int accessible) => accessible <= 0 ? 0 : (accessible + 5) / 6;
    }
}
