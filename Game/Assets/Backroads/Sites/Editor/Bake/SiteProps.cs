// Site props by key (card sites-S4 step 4, design §7).
using System;
using System.Collections.Generic;
using System.Linq;
using Backroads.Core.WorldBake;
using Clipper2Lib;
using Unity.Mathematics;
using UnityEngine;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// Design §7's prop table: each key the site plans and lots use, the Synty prefabs that stand for it (paths in the
    /// user's packs, bottom to top; variants picked by SITE_SEED), or none where the packs have nothing (our own stand-in).
    /// </summary>
    public static class SitePropTable
    {
        const string City = "Assets/Synty/PolygonCity/Prefabs/Props/", Racer = "Assets/Synty/PolygonProRacer/Prefabs/Props/",
            Gen = "Assets/Synty/PolygonGeneric/Prefabs/Props/", GenEnv = "Assets/Synty/PolygonGeneric/Prefabs/Environment/";

        static string[] V(string dir, string stem, int n) => Enumerable.Range(1, n).Select(i => $"{dir}{stem}_{i:D2}.prefab").ToArray();
        static string[] One(string path) => new[] { path };

        /// <summary>Per key: the variants (each a stack of prefab paths). An empty list: our own stand-in.</summary>
        public static readonly SortedDictionary<string, string[][]> Keys = new SortedDictionary<string, string[][]>(StringComparer.Ordinal)
        {
            { "lamp", new[] { new[] { City + "SM_Prop_LightPole_Base_01.prefab", City + "SM_Prop_LightPole_Lights_01.prefab" } } },
            { "sign.parking", new[] { One(City + "SM_Prop_Sign_Parking_01.prefab") } },
            { "sign.entrance", new[] { One(City + "SM_Prop_Sign_Entrance_01.prefab") } },
            { "sign.exit", new[] { One(Racer + "SM_Prop_Sign_Exit_01.prefab") } },
            { "sign.warning", new[] { One(City + "SM_Prop_Sign_Warning_01.prefab") } },
            { "sign.accessible", new string[0][] },
            { "sign.vanAccessible", new string[0][] },
            { "menu.board", new string[0][] },
            { "trash.bin", new[] { One(City + "SM_Prop_Trashbin_01.prefab"), One(City + "SM_Prop_Trashbin_02.prefab") } },
            { "trash.island", new[] { One(Racer + "SM_Prop_Rubbish_Bin_01.prefab") } },
            { "bench", new[] { One(City + "SM_Prop_ParkBench_01.prefab") } },
            { "dumpster", new[] { One(Racer + "SM_Prop_Dumpster_01.prefab"), One(City + "SM_Prop_Skip_01.prefab") } },
            { "skip", new[] { One(City + "SM_Prop_Skip_01.prefab"), One(City + "SM_Prop_Skip_02.prefab") } },
            { "pallets", new[] { One(City + "SM_Prop_Pallet_01.prefab"), One(City + "SM_Prop_CardboardBox_01.prefab"), One(City + "SM_Prop_CardboardBox_03.prefab") } },
            { "mailbox", new[] { One(City + "SM_Prop_Mailbox_01.prefab") } },
            { "washing.line", V(City, "SM_Prop_Washingline", 4).Select(One).ToArray() },
            { "tyre.stack", V(Racer, "SM_Prop_Tyre_Stack", 5).Select(One).ToArray() },
            { "jack.stand", new[] { One(Racer + "SM_Prop_Jack_Stand_01.prefab"), One(Racer + "SM_Prop_Front_Jack_01.prefab") } },
            { "fuel.air", new[] { One(Racer + "SM_Prop_Air_Gun_01.prefab") } },
            { "fuel.cans", V(Racer, "SM_Prop_Fuel_Can", 3).Select(One).ToArray() },
            { "water.barrel", new[] { One(Racer + "SM_Prop_Water_Barrel_01.prefab") } },
            { "bollard", new[] { One(Racer + "SM_Prop_Bollard_01.prefab") } },
            { "barrier.short", new[] { One(Racer + "SM_Prop_Barrier_Short_01.prefab") } },
            { "barrier", V(Racer, "SM_Prop_Barrier", 3).Select(One).ToArray() },
            { "boom.gate", new[] { One(Racer + "SM_Prop_Boom_Gate_01.prefab") } },
            { "cone", V(Racer, "SM_Prop_Cone", 3).Select(One).ToArray() },
            { "portaloo", new[] { One(Racer + "SM_Prop_Portaloo_01.prefab") } },
            { "barrel.wood", V(Gen, "SM_Gen_Prop_Barrel_Wood", 3).Select(One).ToArray() },
            { "barrel.metal", V(Gen, "SM_Gen_Prop_Barrel_Metal", 3).Select(One).ToArray() },
            { "sack.stack", V(Gen, "SM_Gen_Prop_Sack_Stack", 2).Select(One).ToArray() },
            { "log", V(GenEnv, "SM_Gen_Env_Log", 2).Select(One).ToArray() },
            { "plank.stack", V(Gen, "SM_Gen_Prop_Plank", 2).Select(One).ToArray() },
        };

        /// <summary>The prefab stack for a key at a seed (variant by seed); empty for our own stand-in or an unknown key.</summary>
        public static string[] Pick(string key, ulong seed)
        {
            if (!Keys.TryGetValue(key, out var vs) || vs.Length == 0) return Array.Empty<string>();
            return vs[(int)(seed % (ulong)vs.Length)];
        }
    }

    /// <summary>What the prop placement came to (for the report and the tests).</summary>
    public sealed class SitePropStats
    {
        public readonly SortedDictionary<string, int> placed = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> moved = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> unknownKeys = new List<string>();
        public readonly List<string> notes = new List<string>();
        public int synty, own;
    }

    /// <summary>
    /// card sites-S4 step 4: the props of every plan (PropSpot) and lot (LotProp, lamps) as placements on the graded ground,
    /// each kept inside its parcel and clear of stalls, aisles, drive-through lanes, buildings and the 1.2 m in front of
    /// every door (moved up to 2 m to the nearest clear spot, else dropped and reported); fences and gates with their
    /// heights; lane A's ModelRequests as stand-in boxes.
    /// </summary>
    public static class SiteProps
    {
        public const double DoorClear = 1.2, Nudge = 2.0;

        public static void Build(SitePlan plan, PlanContext ctx, PlanGrade g, List<SitePropSet.Placement> props, List<SitePropSet.FenceRun> fences,
            List<SitePropSet.GateSpan> gates, List<SitePropSet.ModelBox> models, SitePropStats stats)
        {
            var parcel = ctx.parcels.parcels[plan.parcel];
            var region = ctx.parcels.regions[plan.parcel];
            var obstacles = Obstacles(plan, ctx, parcel);
            ulong seed = SiteSeed.Of(ctx.town, parcel.buildings.Length > 0 ? ctx.data.buildings[parcel.buildings[0]].osm : "parcel/" + plan.parcel);
            var spots = new List<(string key, double2 pos, float yaw, string reason, bool island)>();
            foreach (var p in plan.props) spots.Add((p.key, p.pos, p.yaw, p.reason, false));
            foreach (var lot in plan.lots)
            {
                foreach (var p in lot.props) spots.Add((p.key, p.pos, p.yaw, p.reason, false));
                foreach (var l in lot.lamps) spots.Add(("lamp", l, 0f, "design §7: lamp poles in islands (S2)", true));
            }
            for (int i = 0; i < spots.Count; i++)
            {
                var (key, pos, yaw, reason, island) = spots[i];
                if (!SitePropTable.Keys.ContainsKey(key)) { stats.unknownKeys.Add($"parcel {plan.parcel}: {key}"); continue; }
                var at = pos;
                bool ok = Clear(region, obstacles, at);
                if (!ok)
                {
                    ok = Find(region, obstacles, pos, out at);
                    if (ok) Bump(stats.moved, key);
                }
                if (!ok) { Bump(stats.dropped, key); stats.notes.Add($"parcel {plan.parcel}: {key} at ({pos.x:F1}, {pos.y:F1}) dropped: no clear spot within {Nudge} m"); continue; }
                double y = Height(plan, ctx, g, at);
                var stack = SitePropTable.Pick(key, SiteSeed.Mix(seed ^ (ulong)(i + 1)));
                if (stack.Length > 0) stats.synty++; else stats.own++;
                props.Add(new SitePropSet.Placement
                {
                    key = key, prefabs = stack, position = new Vector3((float)at.x, (float)y, (float)at.y), yaw = yaw, parcel = plan.parcel, reason = reason,
                });
                Bump(stats.placed, key);
            }
            foreach (var f in plan.fences)
            {
                if (f.line == null || f.line.Length < 2) continue;
                fences.Add(new SitePropSet.FenceRun
                {
                    line = f.line.Select(q => new Vector3((float)q.x, (float)Height(plan, ctx, g, q), (float)q.y)).ToArray(),
                    kind = f.kind, height = f.height > 0 ? f.height : 1.2f, parcel = plan.parcel,
                });
            }
            foreach (var gt in plan.gates)
                gates.Add(new SitePropSet.GateSpan
                {
                    a = new Vector3((float)gt.a.x, (float)Height(plan, ctx, g, gt.a), (float)gt.a.y),
                    b = new Vector3((float)gt.b.x, (float)Height(plan, ctx, g, gt.b), (float)gt.b.y),
                    kind = gt.kind, sliding = gt.sliding, parcel = plan.parcel,
                });
            foreach (var m in plan.models)
            {
                if (m.footprint == null || m.footprint.Length < 3) continue;
                var c = Poly2.Centroid(m.footprint);
                models.Add(new SitePropSet.ModelBox
                {
                    key = m.key, footprint = m.footprint.Select(q => new Vector2((float)q.x, (float)q.y)).ToArray(),
                    y = (float)Height(plan, ctx, g, c), height = m.height, parcel = plan.parcel, reason = m.reason,
                });
            }
        }

        static void Bump(SortedDictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out var n) ? n + 1 : 1;

        /// <summary>Stalls, aisles and lanes, drive-through pieces, buildings, and a 1.2 m apron in front of every door.</summary>
        static Paths64 Obstacles(SitePlan plan, PlanContext ctx, Parcel parcel)
        {
            var o = new Paths64();
            foreach (var lot in plan.lots)
            {
                foreach (var s in lot.stalls) if (s.poly != null && s.poly.Length >= 3) o.AddRange(Clip.Region(s.poly));
                foreach (var l in lot.lanes) if (l.poly != null && l.poly.Length >= 3) o.AddRange(Clip.Region(l.poly));
                if (lot.driveThrough != null) foreach (var pc in lot.driveThrough.pieces) if (pc != null && pc.Length >= 3) o.AddRange(Clip.Region(pc));
            }
            if (plan.driveThrough != null) foreach (var pc in plan.driveThrough.pieces) if (pc != null && pc.Length >= 3) o.AddRange(Clip.Region(pc));
            foreach (var bi in parcel.buildings) if (!ctx.data.buildings[bi].OpenRoof) o.AddRange(Clip.Region(ctx.data.buildings[bi].poly));
            foreach (var (p, _, d) in SiteGrading.DoorPoints(plan, ctx))
            {
                var b = ctx.data.buildings.First(x => x.osm == d.building);
                var (dp, outward) = PlanBuilder.DoorAt(b, d);
                var along = new double2(-outward.y, outward.x);
                double hw = d.width * 0.5 + 0.3;
                o.AddRange(Clip.Region(new[] { dp - along * hw, dp + along * hw, dp + along * hw + outward * DoorClear, dp - along * hw + outward * DoorClear }));
            }
            return Clip.Inflate(Clip.Union(o), PropClear, JoinType.Miter);
        }

        /// <summary>Props keep this far from stalls, aisles, buildings and door aprons.</summary>
        public const double PropClear = 0.3;

        static bool Clear(Paths64 region, Paths64 obstacles, double2 p) => Clip.Contains(region, p) && !Clip.Contains(obstacles, p);

        /// <summary>The nearest clear spot within 2 m (rings of 0.25 m, 16 directions, in a fixed order).</summary>
        static bool Find(Paths64 region, Paths64 obstacles, double2 p, out double2 at)
        {
            for (double r = 0.25; r <= Nudge + 1e-9; r += 0.25)
                for (int k = 0; k < 16; k++)
                {
                    double a = k * Math.PI / 8;
                    var q = p + new double2(Math.Cos(a), Math.Sin(a)) * r;
                    if (Clear(region, obstacles, q)) { at = q; return true; }
                }
            at = p;
            return false;
        }

        /// <summary>On a kerbed island: its bed (just under the kerb top); on a graded surface: the grade; else the DEM.</summary>
        public static double Height(SitePlan plan, PlanContext ctx, PlanGrade g, double2 p)
        {
            foreach (var lot in plan.lots)
                foreach (var isl in lot.islands)
                    if (isl.kerbed && isl.poly != null && isl.poly.Length >= 3 && Poly2.Contains(isl.poly, p)) return g.H(p) + 0.13;
            foreach (var s in plan.surfaces)
                if (Poly2.Contains(s.outer, p) && !s.holes.Any(h => Poly2.Contains(h, p))) return SiteMesher.Paved(s.surface) ? g.H(p) : g.H(p) - 0.04;
            return g.Ground.At(p);
        }
    }
}
