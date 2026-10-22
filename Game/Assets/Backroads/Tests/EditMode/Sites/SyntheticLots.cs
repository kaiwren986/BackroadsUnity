using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using Backroads.Sites;
using Unity.Mathematics;

namespace Backroads.Tests.EditMode.Sites
{
    /// <summary>
    /// The synthetic lots of card sites-S2 (no town needed): rectangles from 12 x 20 m to 120 x 90 m, L-shaped and
    /// trapezoid lots, one and two entrances, a corner lot, a rotated lot, and lots that exercise the fire lane, the
    /// drive-through, gravel, kerbs, one-way angled stalls and trailer stalls. Street at the bottom (z = 0) unless named.
    /// </summary>
    static class SyntheticLots
    {
        public sealed class Case
        {
            public string name;
            public Func<LotInput> make;
            /// <summary>Hand-computed capacity (stalls before the accessible conversion), or -1.</summary>
            public int handOptimum = -1;
            public string handNote;
        }

        public static double2[] Rect(double w, double h, double x0 = 0, double z0 = 0) =>
            new[] { new double2(x0, z0), new double2(x0 + w, z0), new double2(x0 + w, z0 + h), new double2(x0, z0 + h) };

        public static LotEntrance Ent(double x0, double z0, double x1, double z1, EntranceFlow flow = EntranceFlow.TwoWay, StreetClass street = StreetClass.Street) =>
            new LotEntrance { a = new double2(x0, z0), b = new double2(x1, z1), flow = flow, street = street };

        /// <summary>A 7.3 m two-way entrance centred on the bottom edge of a w-wide lot.</summary>
        public static LotEntrance Bottom(double w, double at = double.NaN, double width = 7.3, StreetClass street = StreetClass.Street)
        {
            double c = double.IsNaN(at) ? w * 0.5 : at;
            return Ent(c - width * 0.5, 0, c + width * 0.5, 0, EntranceFlow.TwoWay, street);
        }

        static LotInput Simple(double w, double h, params LotEntrance[] ents)
        {
            var i = new LotInput { lot = Rect(w, h), frontageDir = new double2(1, 0), toStreet = new double2(0, -1), seed = 7 };
            i.entrances.AddRange(ents);
            return i;
        }

        static double2 Rot(double2 p, double deg)
        {
            double a = deg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            return new double2(p.x * c - p.y * s, p.x * s + p.y * c);
        }

        static double2[] Rot(double2[] ps, double deg)
        {
            var r = new double2[ps.Length];
            for (int i = 0; i < ps.Length; i++) r[i] = Rot(ps[i], deg);
            return r;
        }

        // Hand-computed capacities (sites-S2 "capacity within 10 % of a hand-computed optimum"). Rules, as a designer would
        // lay the lot by hand with this program's sizes: 90-degree stalls 2.75 x 5.5 m on 7.3 m two-way aisles (module
        // 18.3 m), 0.5 m from the lot edge (an aisle may use the margin), the best of aisles along or across the street;
        // either one aisle straight in from the entrance, or a 7.3 m cross lane from the entrance that links the aisles;
        // no stall in the first 6 m behind the throat; 1.8 m end islands where rows meet the cross lane or the entrance's
        // first 6 m; an island every 15 stalls.
        // W = width along the street, H = depth; insets W' = W - 1, H' = H - 1; "P" = aisles parallel to the street with a
        // cross lane up from a bottom-centre entrance, "Q" = aisles perpendicular to the street.
        public static readonly List<Case> References = new List<Case>
        {
            new Case { name = "rect_12x20", make = () => Simple(12, 20, Bottom(12)), handOptimum = 4,
                handNote = "P: one module across H' 19; the cross lane takes the bottom row (2 x 1.35 m left); top row 11 / 2.75 = 4" },
            new Case { name = "rect_20x30", make = () => Simple(20, 30, Bottom(20)), handOptimum = 14,
                handNote = "Q: one module across W' 19, its aisle straight in from the entrance; rows from 6 m behind the throat + a 1.8 m island to 29.5 m = 21.7 / 2.75 = 7 each" },
            new Case { name = "rect_30x40", make = () => Simple(30, 40, Bottom(30)), handOptimum = 28,
                handNote = "P: two modules across H' 39 = 4 rows; cross lane cuts 3: 2 x 3 + 2 x (2 x 3) [runs (29 - 7.3) / 2 - 1.8 = 9.05]; top row 29 / 2.75 = 10" },
            new Case { name = "rect_40x30", make = () => Simple(40, 30, Bottom(40)), handOptimum = 34,
                handNote = "Q: two modules across W' 39 = 4 rows along H; a cross lane along the street edge links both aisles: 2 inner rows (29.5 - 9.1) / 2.75 = 7, 2 outer rows beside the lane 29 / 2.75 = 10" },
            new Case { name = "rect_60x40", make = () => Simple(60, 40, Bottom(60)), handOptimum = 72,
                handNote = "Q: three modules across W' 59 = 6 rows; middle aisle in from the entrance, cross lane along the street edge: 4 inner rows (39.5 - 9.1) / 2.75 = 11, 2 outer rows 39 / 2.75 = 14" },
            new Case { name = "rect_80x50", make = () => Simple(80, 50, Bottom(80)), handOptimum = 120,
                handNote = "P: two modules + one single-loaded across H' 49 = 5 rows, cross lane cuts all: runs (79 - 7.3) / 2 - 1.8 = 34.05 -> 12, bottom row 33.55 -> 12: 10 runs x 12" },
            new Case { name = "rect_120x90", make = () => Simple(120, 90, Bottom(120)), handOptimum = 340,
                handNote = "P: four modules + one single across H' 89 = 9 rows, cross lane cuts all: runs 54.05 = 15 + island + 4 = 19, bottom row 53.55 -> 18: 8 x 38 + 36" },
            new Case { name = "rect_25x60", make = () => Simple(25, 60, Bottom(25)), handOptimum = 38,
                handNote = "Q: one module across W' 24, aisle straight in: one row clear of the throat 59 m = 15 + island + 5 = 20, the other 53.5 m = 18" },
            new Case { name = "rect_50x50", make = () => Simple(50, 50, Bottom(50)), handOptimum = 73,
                handNote = "Q: two modules + one single across W' 49 = 5 rows; middle aisle in, cross lane along the street edge over the aisles: 4 rows (49.5 - 9.1) / 2.75 = 14, the outer row 49 m = 15 + island + 2 = 17" },
            new Case { name = "rect_100x60", make = () => Simple(100, 60, Bottom(100)), handOptimum = 184,
                handNote = "P: three modules across H' 59 = 6 rows; cross lane cuts 5: runs 44.05 = 15 (5 rows x 30, bottom row 43.55 also 15), top row 99 m = 2 x (15 + island) + 4 = 34" },
        };

        public static readonly List<Case> Others = new List<Case>
        {
            new Case { name = "rect_60x40_two_entrances", make = () => Simple(60, 40, Bottom(60, 12), Bottom(60, 48)) },
            new Case { name = "l_shape", make = () =>
            {
                var i = new LotInput { lot = new[] { new double2(0, 0), new double2(60, 0), new double2(60, 20), new double2(25, 20), new double2(25, 50), new double2(0, 50) }, frontageDir = new double2(1, 0), seed = 11 };
                i.entrances.Add(Bottom(60, 42));
                return i;
            } },
            new Case { name = "trapezoid", make = () =>
            {
                var i = new LotInput { lot = new[] { new double2(0, 0), new double2(60, 0), new double2(48, 35), new double2(12, 35) }, frontageDir = new double2(1, 0), seed = 12 };
                i.entrances.Add(Bottom(60));
                return i;
            } },
            new Case { name = "corner", make = () =>
            {
                var i = Simple(50, 40, Bottom(50, 12), Ent(50, 22, 50, 29.3));
                i.seed = 13;
                return i;
            } },
            new Case { name = "rotated_30", make = () =>
            {
                var i = new LotInput { lot = Rot(Rect(60, 40), 30), frontageDir = Rot(new double2(1, 0), 30), seed = 14 };
                var e = Bottom(60);
                i.entrances.Add(new LotEntrance { a = Rot(e.a, 30), b = Rot(e.b, 30) });
                return i;
            } },
            new Case { name = "bigbox", make = () =>
            {
                // A supermarket lot in front of a 70 x 45 m store sharing the lot's top edge; two entrances onto a main street.
                var i = new LotInput
                {
                    lot = Rect(100, 60),
                    edgeKinds = new[] { LotEdgeKind.Walk, LotEdgeKind.Property, LotEdgeKind.Building, LotEdgeKind.Property },
                    building = new LotBuilding { footprint = Rect(70, 45, 15, 60), hasDoor = true, door = new double2(50, 60) },
                    frontageDir = new double2(1, 0), toStreet = new double2(0, -1), seed = 21,
                    targets = new LotTargets { stalls = 150, kerbed = true, fireLane = true },
                };
                i.entrances.Add(Bottom(100, 20, 7.3, StreetClass.MainStreet));
                i.entrances.Add(Bottom(100, 80, 7.3, StreetClass.MainStreet));
                return i;
            } },
            new Case { name = "fastfood", make = () =>
            {
                // A fast-food lot wrapping a 12 x 20 m restaurant; the drive-through runs round its back and left side.
                var i = new LotInput
                {
                    lot = Rect(56, 52),
                    building = new LotBuilding { footprint = Rect(12, 20, 30, 22) },
                    frontageDir = new double2(1, 0), toStreet = new double2(0, -1), seed = 22,
                    targets = new LotTargets { kerbed = true, driveThrough = true },
                };
                i.entrances.Add(Bottom(56, 8));
                i.entrances.Add(Bottom(56, 48));
                return i;
            } },
            new Case { name = "gravel", make = () =>
            {
                var i = Simple(40, 30, Bottom(40));
                i.targets = new LotTargets { paved = false };
                return i;
            } },
            new Case { name = "kerbed_walk", make = () =>
            {
                var i = Simple(60, 40, Bottom(60));
                i.edgeKinds = new[] { LotEdgeKind.Walk, LotEdgeKind.Property, LotEdgeKind.Property, LotEdgeKind.Property };
                i.building = new LotBuilding { footprint = Rect(30, 20, 15, 45), hasDoor = true, door = new double2(30, 45) };
                i.targets = new LotTargets { kerbed = true };
                return i;
            } },
            new Case { name = "angled_one_way", make = () =>
            {
                // 17 m wide: no double 90-degree module; entry at the west end, exit at the east end.
                var i = new LotInput { lot = Rect(70, 17), frontageDir = new double2(1, 0), seed = 31 };
                i.entrances.Add(Ent(0, 13, 0, 4, EntranceFlow.EntryOnly));
                i.entrances.Add(Ent(70, 4, 70, 13, EntranceFlow.ExitOnly, StreetClass.MainStreet));
                return i;
            } },
            new Case { name = "trailer_yard", make = () =>
            {
                var i = Simple(60, 55, Bottom(60, 30, 12));
                i.targets = new LotTargets { stallKind = StallKind.Trailer, accessible = false, paved = false };
                return i;
            } },
        };

        public static IEnumerable<Case> All
        {
            get
            {
                foreach (var c in References) yield return c;
                foreach (var c in Others) yield return c;
            }
        }
    }
}
