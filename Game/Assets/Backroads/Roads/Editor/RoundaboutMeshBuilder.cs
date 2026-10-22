using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using UnityEngine;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R4 step 1's meshes (05 §5.11, §5.8 roundabout row): the truck apron (concrete, a sloped Apron75 mountable
    /// kerb at the circulatory roadway's inner edge), the central island's 150 mm kerb and its concrete top band (the
    /// island itself is the terrain, raised by the island plate), a mini-roundabout's low mountable dome, and the raised
    /// splitter islands (150 mm kerbs, concrete top). Paint: yield-line shark teeth across each entry, the ring's outer
    /// edge line (solid beside the splitter, wide dotted across the entry, none across the exit); crosswalks are R3's.
    /// Everything but the paint collides.
    /// </summary>
    public static class RoundaboutMeshBuilder
    {
        static readonly Vector3 Up = new Vector3(0, 1, 0);
        const double Band = 0.3, Mountable = 0.15, TeethBase = 0.6, TeethHeight = 0.9, TeethGap = 0.15, EdgeWidth = 0.2;

        public static void Build(RoadSurfaces surf, RoadMeshes m, SurfaceIndex idx)
        {
            var sst = new SurfaceSettings();
            foreach (var ns in surf.nodes)
            {
                if (ns.mode != SurfaceMode.Roundabout || ns.roundabout == null) continue;
                var G = ns.roundabout;
                double Y(D2 q) => SurfaceBuilder.JunctionHeight(ns, q, sst);
                if (G.mini) Dome(ns, G, Y, sst.roundabout, m);
                else Island(ns, G, Y, sst.roundabout, m);
                foreach (var L in G.legs)
                    foreach (var sp in L.splitters) Raised(ns, sp, Y, sst.roundabout.islandKerb, m, ns.arms[L.arm].edge);
                Paint(ns, G, idx, m);
            }
        }

        static int Segments(double r) => Math.Max(24, (int)Math.Ceiling(2 * Math.PI * r / 2.0));
        static D2 On(D2 c, double r, double a) => c + new D2(Math.Cos(a), Math.Sin(a)) * r;
        static Color32 Col(int salt) => new Color32(0, 0, (byte)((salt * 37 + 17) & 255), 255);

        /// <summary>The apron (Ri to Ra, +0.075 with a sloped mountable face) and the island kerb and top band at Ra (+0.15).</summary>
        static void Island(NodeSurface ns, RoundaboutGeometry G, Func<D2, double> Y, RoundaboutSettings st, RoadMeshes m)
        {
            var part = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, node = ns.node };
            m.parts.Add(part);
            int N = Segments(G.Ri);
            var col = Col(ns.node);
            for (int k = 0; k < N; k++)
            {
                double a0 = 2 * Math.PI * k / N, a1 = 2 * Math.PI * (k + 1) / N;
                // rings outward to inward: apron foot, apron top edge, island kerb foot, island kerb top, band inner edge
                var rings = new List<(double r, double dy)>
                {
                    (G.Ri, 0), (G.Ri - Mountable, st.apronKerb), (G.Ra, st.apronKerb), (G.Ra, st.islandKerb), (Math.Max(0.2, G.Ra - Band), st.islandKerb),
                };
                if (G.Ra >= G.Ri - Mountable - 1e-6) rings = new List<(double, double)> { (G.Ri, 0), (G.Ri, st.islandKerb), (Math.Max(0.2, G.Ri - Band), st.islandKerb) };
                for (int q = 0; q + 1 < rings.Count; q++)
                {
                    var (r0, h0) = rings[q];
                    var (r1, h1) = rings[q + 1];
                    D2 p00 = On(G.c, r0, a0), p01 = On(G.c, r0, a1), p10 = On(G.c, r1, a0), p11 = On(G.c, r1, a1);
                    var A = new D3(p00.x, Y(p00) + h0, p00.z);
                    var B = new D3(p01.x, Y(p01) + h0, p01.z);
                    var C = new D3(p11.x, Y(p11) + h1, p11.z);
                    var D = new D3(p10.x, Y(p10) + h1, p10.z);
                    // faces up, or outward for the vertical and sloped kerbs (radial normals, so the ring shades round)
                    bool vertical = Math.Abs(r0 - r1) < 1e-6, sloped = !vertical && h1 > h0;
                    Vector3 N0 = Up, N1 = Up;
                    if (vertical || sloped)
                    {
                        float lift = sloped ? 2f : 0f;
                        N0 = new Vector3((float)Math.Cos(a0), lift, (float)Math.Sin(a0)).normalized;
                        N1 = new Vector3((float)Math.Cos(a1), lift, (float)Math.Sin(a1)).normalized;
                    }
                    var front = (N0 + N1).normalized;
                    int i0 = part.Add(A, new Vector2((float)(A.x / 2), (float)(A.z / 2)), Vector2.zero, col, N0);
                    int i1 = part.Add(B, new Vector2((float)(B.x / 2), (float)(B.z / 2)), Vector2.zero, col, N1);
                    int i2 = part.Add(C, new Vector2((float)(C.x / 2), (float)(C.z / 2)), Vector2.zero, col, N1);
                    int i3 = part.Add(D, new Vector2((float)(D.x / 2), (float)(D.z / 2)), Vector2.zero, col, N0);
                    part.Tri(i0, i1, i2, front);
                    part.Tri(i0, i2, i3, front);
                }
            }
        }

        /// <summary>A mini-roundabout's island: a mountable dome, the surface plus miniDome·(1 - (r/Ri)²).</summary>
        static void Dome(NodeSurface ns, RoundaboutGeometry G, Func<D2, double> Y, RoundaboutSettings st, RoadMeshes m)
        {
            var part = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, node = ns.node, smooth = 3 };
            m.parts.Add(part);
            int N = Segments(G.Ri), R = 4;
            var col = Col(ns.node + 1);
            D3 V(double r, double a)
            {
                var p = On(G.c, r, a);
                double t = r / G.Ri;
                return new D3(p.x, Y(p) + st.miniDome * (1 - t * t), p.z);
            }
            int centre = part.Add(V(0, 0), Vector2.zero, Vector2.zero, col, Up);
            var ring = new int[R + 1, N];
            for (int q = 1; q <= R; q++)
                for (int k = 0; k < N; k++)
                {
                    var v = V(G.Ri * q / R, 2 * Math.PI * k / N);
                    ring[q, k] = part.Add(v, new Vector2((float)(v.x / 2), (float)(v.z / 2)), Vector2.zero, col, Up);
                }
            for (int k = 0; k < N; k++)
            {
                int k1 = (k + 1) % N;
                part.Tri(centre, ring[1, k], ring[1, k1], Up);
                for (int q = 1; q < R; q++)
                {
                    part.Tri(ring[q, k], ring[q + 1, k], ring[q + 1, k1], Up);
                    part.Tri(ring[q, k], ring[q + 1, k1], ring[q, k1], Up);
                }
            }
        }

        /// <summary>A raised island: its top at the surface + h (ear-clipped), and the kerb faces around it.</summary>
        static void Raised(NodeSurface ns, D2[] poly, Func<D2, double> Y, double h, RoadMeshes m, int edge)
        {
            var part = new RoadMeshPart { material = RoadMaterial.Concrete, collider = true, node = ns.node, edge = edge };
            m.parts.Add(part);
            var col = Col(ns.node + 2);
            int b0 = part.pos.Count;
            foreach (var q in poly) part.Add(new D3(q.x, Y(q) + h, q.z), new Vector2((float)(q.x / 2), (float)(q.z / 2)), Vector2.zero, col, Up);
            var tris = Geom2.EarClip(poly);
            for (int k = 0; k + 2 < tris.Count; k += 3) part.Tri(b0 + tris[k], b0 + tris[k + 1], b0 + tris[k + 2], Up);
            for (int k = 0; k < poly.Length; k++)
            {
                D2 a = poly[k], b = poly[(k + 1) % poly.Length];
                var d = b - a;
                if (d.Length < 1e-4) continue;
                var outward = d.Normalized.Right;
                var front = new Vector3((float)outward.x, 0, (float)outward.z);
                int i0 = part.Add(new D3(a.x, Y(a), a.z), Vector2.zero, Vector2.zero, col, front);
                int i1 = part.Add(new D3(b.x, Y(b), b.z), Vector2.zero, Vector2.zero, col, front);
                int i2 = part.Add(new D3(b.x, Y(b) + h, b.z), Vector2.zero, Vector2.zero, col, front);
                int i3 = part.Add(new D3(a.x, Y(a) + h, a.z), Vector2.zero, Vector2.zero, col, front);
                part.Tri(i0, i1, i2, front);
                part.Tri(i0, i2, i3, front);
            }
        }

        static D3 Drape(SurfaceIndex idx, NodeSurface ns, D2 p)
        {
            double y = idx.Height(p, out double h) ? h : SurfaceBuilder.JunctionHeight(ns, p, new SurfaceSettings());
            return new D3(p.x, y + RoadMeshBuilder.PaintLift, p.z);
        }

        static void Tri(RoadMeshPart part, SurfaceIndex idx, NodeSurface ns, D2 a, D2 b, D2 c)
        {
            int i0 = part.Add(Drape(idx, ns, a), Vector2.zero, Vector2.zero, RoadMeshBuilder.White, Up);
            int i1 = part.Add(Drape(idx, ns, b), Vector2.zero, Vector2.zero, RoadMeshBuilder.White, Up);
            int i2 = part.Add(Drape(idx, ns, c), Vector2.zero, Vector2.zero, RoadMeshBuilder.White, Up);
            part.Tri(i0, i1, i2, Up);
        }

        /// <summary>A paint band along the circle radius r between angles a0 and a1 (counter-clockwise), width w.</summary>
        static void Arc(RoadMeshPart part, SurfaceIndex idx, NodeSurface ns, D2 c, double r, double a0, double a1, double w)
        {
            int n = Math.Max(1, (int)Math.Ceiling((a1 - a0) * r / 1.0));
            for (int k = 0; k < n; k++)
            {
                double t0 = a0 + (a1 - a0) * k / n, t1 = a0 + (a1 - a0) * (k + 1) / n;
                D2 p0 = On(c, r + 0.5 * w, t0), p1 = On(c, r + 0.5 * w, t1), q1 = On(c, r - 0.5 * w, t1), q0 = On(c, r - 0.5 * w, t0);
                Tri(part, idx, ns, p0, p1, q1);
                Tri(part, idx, ns, p0, q1, q0);
            }
        }

        static void Paint(NodeSurface ns, RoundaboutGeometry G, SurfaceIndex idx, RoadMeshes m)
        {
            for (int k = 0; k < G.legs.Count; k++)
            {
                var L = G.legs[k];
                var a = ns.arms[k];
                // the yield line: white triangles, base 0.6 m on a line just outside the inscribed circle, apex 0.9 m toward the approaching traffic
                var teeth = new RoadMeshPart { material = RoadMaterial.Paint, marking = MarkingKind.YieldLine, node = ns.node, arm = k, edge = a.edge };
                double sBase = G.Ro + 0.3, u0 = L.splitters.Count > 0 ? Math.Min(L.flare, L.FlareAt(sBase)) + 0.15 : 0.15, u1 = L.EdgeL(sBase) - 0.15;
                int count = 0;
                for (double u = u0; u + TeethBase <= u1 + 1e-9; u += TeethBase + TeethGap)
                {
                    Tri(teeth, idx, ns, a.frame.P(sBase, u), a.frame.P(sBase, u + TeethBase), a.frame.P(sBase + TeethHeight, u + 0.5 * TeethBase));
                    count++;
                }
                if (count > 0) { m.parts.Add(teeth); m.yieldLines++; }

                // the ring's outer edge line across this leg: solid beside the splitter, wide dotted across the entry, none across the exit
                double angle = Math.Atan2(a.frame.Dir(0).z, a.frame.Dir(0).x), r = G.Ro - 0.15;
                double half = L.splitters.Count > 0 ? L.flare : 0.0;
                double aSplit = Math.Asin(Math.Min(1, half / r)), aEntry = Math.Asin(Math.Min(1, L.EdgeL(G.Ro) / r));
                var edge = new RoadMeshPart { material = RoadMaterial.Paint, marking = MarkingKind.EdgeLine, node = ns.node, arm = k, edge = a.edge };
                if (half > 0) Arc(edge, idx, ns, G.c, r, angle - aSplit, angle + aSplit, EdgeWidth);
                const double dash = 0.6, gap = 0.9;
                for (double t = aSplit; t < aEntry; t += (dash + gap) / r)
                    Arc(edge, idx, ns, G.c, r, angle + t, angle + Math.Min(aEntry, t + dash / r), EdgeWidth);
                if (edge.tri.Count > 0) { m.parts.Add(edge); m.edgeLineM += r * (aEntry - aSplit) * dash / (dash + gap) + 2 * r * aSplit; }
            }
        }
    }
}
