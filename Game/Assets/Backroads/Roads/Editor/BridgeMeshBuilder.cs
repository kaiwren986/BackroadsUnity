using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R4 step 2: the bridge structure as our own concrete meshes (the deck's pavement is R1's ribbon, meshed with
    /// the other asphalt). Per side a 0.1 m edge strip at the pavement edge, a 0.35 m solid parapet 0.85 m high (its
    /// inner face, top and the outer fascia down to the soffit), the soffit 1.0 m under the pavement edges, wall piers
    /// across the free span (soffit to 2 m below the ground) and at each end the abutment's front wall and wing walls,
    /// which retain the abutment plate's fill. The parapets, strips and wing walls collide; the soffit and piers are
    /// out of reach and do not. The Synty bridge edge and wall pieces are not used: their modules do not follow a
    /// sloping, curved deck, so the parapet is ours.
    /// </summary>
    public static class BridgeMeshBuilder
    {
        const double Step = 4.0, WingThickness = 0.3;
        static readonly Vector3 Up = new Vector3(0, 1, 0), Down = new Vector3(0, -1, 0);

        public static void Build(IReadOnlyList<BridgeDeck> decks, IHeightField dem, BridgeSettings st, RoadMeshes m)
        {
            if (decks == null) return;
            st = st ?? new BridgeSettings();
            foreach (var d in decks)
            {
                var ss = BridgeBuilder.Stations(d, Step, d.sA, d.length - d.sB);
                Deck(d, ss, st, m);
                foreach (var sp in d.piers) Pier(d, sp, dem, st, m);
                if (d.FreeSpan > 0)
                {
                    Abutment(d, ss, true, dem, st, m);
                    Abutment(d, ss, false, dem, st, m);
                }
            }
        }

        static Color32 Col(int id, int salt) => new Color32(0, 0, (byte)((id * 37 + salt * 101 + 17) & 255), 255);

        static D3 At(BridgeDeck d, double s, double u, double y)
        {
            var p = d.frame.P(s, u);
            return new D3(p.x, y, p.z);
        }

        /// <summary>A quad a-b-c-d (a and b along the run, d above a), wound to face `front`.</summary>
        static void Quad(RoadMeshPart part, D3 a, D3 b, D3 c, D3 dd, Vector3 front, Color32 col, float u0, float u1, float v0, float v1)
        {
            int i0 = part.Add(a, new Vector2(u0, v0), Vector2.zero, col, front);
            int i1 = part.Add(b, new Vector2(u1, v0), Vector2.zero, col, front);
            int i2 = part.Add(c, new Vector2(u1, v1), Vector2.zero, col, front);
            int i3 = part.Add(dd, new Vector2(u0, v1), Vector2.zero, col, front);
            part.Tri(i0, i1, i2, front);
            part.Tri(i0, i2, i3, front);
        }

        static Vector3 Lateral(BridgeDeck d, double s, int side)
        {
            var dir = d.frame.Dir(s);
            var left = dir.Left;
            return new Vector3((float)(left.x * side), 0, (float)(left.z * side));
        }

        static void Deck(BridgeDeck d, List<double> ss, BridgeSettings st, RoadMeshes m)
        {
            RoadMeshPart rail = null, under = null;
            double start = 0;
            for (int i = 0; i + 1 < ss.Count; i++)
            {
                double s0 = ss[i], s1 = ss[i + 1];
                if (rail == null || s0 - start >= RoadMeshBuilder.PieceLength)
                {
                    rail = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, edge = d.edge };
                    under = new RoadMeshPart { material = RoadMaterial.Concrete, edge = d.edge };
                    m.parts.Add(rail);
                    m.parts.Add(under);
                    start = s0;
                }
                foreach (int side in new[] { 1, -1 })
                {
                    // the pavement edge, the strip's outer edge (the parapet's inner face) and the outer face, at both stations
                    double uE0 = side * d.Hw(s0, side), uE1 = side * d.Hw(s1, side);
                    double uS0 = uE0 + side * st.edgeStrip, uS1 = uE1 + side * st.edgeStrip;
                    double uO0 = side * d.Outer(s0, side), uO1 = side * d.Outer(s1, side);
                    double y0 = d.EdgeY(s0, side), y1 = d.EdgeY(s1, side);
                    var c = Col(d.id * 2 + (side > 0 ? 0 : 1), 7);
                    var inward = -Lateral(d, 0.5 * (s0 + s1), side);
                    var outward = -inward;
                    // edge strip at the pavement edge's height
                    Quad(rail, At(d, s0, uE0, y0), At(d, s1, uE1, y1), At(d, s1, uS1, y1), At(d, s0, uS0, y0), Up, c, (float)s0, (float)s1, 0, (float)st.edgeStrip);
                    // parapet: inner face, top, outer fascia down to the soffit
                    double H = st.parapetHeight;
                    Quad(rail, At(d, s0, uS0, y0), At(d, s1, uS1, y1), At(d, s1, uS1, y1 + H), At(d, s0, uS0, y0 + H), inward, c, (float)s0, (float)s1, 0, (float)H);
                    Quad(rail, At(d, s0, uS0, y0 + H), At(d, s1, uS1, y1 + H), At(d, s1, uO1, y1 + H), At(d, s0, uO0, y0 + H), Up, c, (float)s0, (float)s1, 0, (float)st.parapetWidth);
                    Quad(under, At(d, s0, uO0, y0 - st.depth), At(d, s1, uO1, y1 - st.depth), At(d, s1, uO1, y1 + H), At(d, s0, uO0, y0 + H), outward, c, (float)s0, (float)s1, 0, (float)(H + st.depth));
                }
                // the soffit, flat across between the two sides' undersides
                double yl0 = d.EdgeY(s0, 1) - st.depth, yl1 = d.EdgeY(s1, 1) - st.depth, yr0 = d.EdgeY(s0, -1) - st.depth, yr1 = d.EdgeY(s1, -1) - st.depth;
                Quad(under, At(d, s0, -d.Outer(s0, -1), yr0), At(d, s1, -d.Outer(s1, -1), yr1), At(d, s1, d.Outer(s1, 1), yl1), At(d, s0, d.Outer(s0, 1), yl0), Down, Col(d.id, 8), (float)s0, (float)s1, 0, (float)(d.outerL + d.outerR));
            }
            // parapet end blocks at both ends of the deck
            foreach (var (s, sign) in new[] { (0.0, -1), (d.length, 1) })
            {
                var along = d.frame.Dir(s);
                var front = new Vector3((float)(along.x * sign), 0, (float)(along.z * sign));
                foreach (int side in new[] { 1, -1 })
                {
                    double hw = d.Hw(s, side), outer = d.Outer(s, side);
                    double y = d.EdgeY(s, side);
                    Quad(rail, At(d, s, side * (hw + st.edgeStrip), y), At(d, s, side * outer, y), At(d, s, side * outer, y + st.parapetHeight), At(d, s, side * (hw + st.edgeStrip), y + st.parapetHeight),
                        front, Col(d.id, 9), 0, (float)st.parapetWidth, 0, (float)st.parapetHeight);
                }
            }
        }

        static void Pier(BridgeDeck d, double sp, IHeightField dem, BridgeSettings st, RoadMeshes m)
        {
            var part = new RoadMeshPart { material = RoadMaterial.Concrete, edge = d.edge };
            m.parts.Add(part);
            double s0 = sp - 0.5 * st.pierThickness, s1 = sp + 0.5 * st.pierThickness;
            double uL = d.Outer(sp, 1) - st.pierInset, uR = -(d.Outer(sp, -1) - st.pierInset);
            double top = BridgeBuilder.Soffit(d, sp, st);
            double ground = dem != null ? BridgeBuilder.Ground(d, dem, sp) : top - 10;
            double bot = Math.Min(ground, top - 1) - st.footing;
            var c = Col(d.id * 16 + d.piers.IndexOf(sp), 10);
            var along = d.frame.Dir(sp);
            var fwd = new Vector3((float)along.x, 0, (float)along.z);
            var left = Lateral(d, sp, 1);
            float h = (float)(top - bot);
            Quad(part, At(d, s1, uR, bot), At(d, s1, uL, bot), At(d, s1, uL, top), At(d, s1, uR, top), fwd, c, 0, (float)(uL - uR), 0, h);
            Quad(part, At(d, s0, uL, bot), At(d, s0, uR, bot), At(d, s0, uR, top), At(d, s0, uL, top), -fwd, c, 0, (float)(uL - uR), 0, h);
            Quad(part, At(d, s0, uL, bot), At(d, s1, uL, bot), At(d, s1, uL, top), At(d, s0, uL, top), left, c, 0, (float)st.pierThickness, 0, h);
            Quad(part, At(d, s1, uR, bot), At(d, s0, uR, bot), At(d, s0, uR, top), At(d, s1, uR, top), -left, c, 0, (float)st.pierThickness, 0, h);
        }

        /// <summary>The front wall where the free span starts (soffit to below the ground) and the wing walls along the fill.</summary>
        static void Abutment(BridgeDeck d, List<double> ss, bool atStart, IHeightField dem, BridgeSettings st, RoadMeshes m)
        {
            var part = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, edge = d.edge };
            m.parts.Add(part);
            double sw = atStart ? d.sA : d.length - d.sB;
            double uL = d.Outer(sw, 1) + st.wing, uR = -(d.Outer(sw, -1) + st.wing);
            double Ground(double s, double u)
            {
                if (dem == null) return BridgeBuilder.Soffit(d, s, st) - 3;
                var p = d.frame.P(s, u);
                return dem.Sample(p.x, p.z);
            }
            var along = d.frame.Dir(sw);
            var toSpan = new Vector3((float)along.x, 0, (float)along.z) * (atStart ? 1 : -1);
            var c = Col(d.id * 2 + (atStart ? 0 : 1), 11);
            // front wall, in strips across so its foot follows the ground
            const int N = 6;
            double top = BridgeBuilder.Soffit(d, sw, st);
            for (int k = 0; k < N; k++)
            {
                double ua = uR + (uL - uR) * k / N, ub = uR + (uL - uR) * (k + 1) / N;
                double ga = Math.Min(Ground(sw, ua), top - 0.5) - st.footing, gb = Math.Min(Ground(sw, ub), top - 0.5) - st.footing;
                Quad(part, At(d, sw, ua, ga), At(d, sw, ub, gb), At(d, sw, ub, top), At(d, sw, ua, top), toSpan, c, (float)(ua - uR), (float)(ub - uR), 0, (float)(top - Math.Min(ga, gb)));
            }
            // wing walls: outer face from the road edge's height down to below the ground, and a top cap
            foreach (int side in new[] { 1, -1 })
            {
                double uo = side > 0 ? uL : uR, ui = uo - side * WingThickness;
                var outward = Lateral(d, sw, side);
                for (int i = 0; i + 1 < ss.Count; i++)
                {
                    double s0 = ss[i], s1 = ss[i + 1];
                    bool inZone = atStart ? s1 <= d.sA + 1e-6 : s0 >= d.length - d.sB - 1e-6;
                    if (!inZone) continue;
                    double y0 = d.EdgeY(s0, side), y1 = d.EdgeY(s1, side);
                    double g0 = Math.Min(Ground(s0, uo), y0 - 0.2) - st.footing, g1 = Math.Min(Ground(s1, uo), y1 - 0.2) - st.footing;
                    Quad(part, At(d, s0, uo, g0), At(d, s1, uo, g1), At(d, s1, uo, y1), At(d, s0, uo, y0), outward, c, (float)s0, (float)s1, 0, (float)(y0 - g0));
                    Quad(part, At(d, s0, ui, y0), At(d, s1, ui, y1), At(d, s1, uo, y1), At(d, s0, uo, y0), Up, c, (float)s0, (float)s1, 0, (float)WingThickness);
                }
            }
        }
    }
}
