using System;
using System.Collections.Generic;

namespace Backroads.Roads
{
    /// <summary>
    /// What the road graph is built from: the canon road polylines (CanonData/v1 world.json roads[]), the lots (for the
    /// downtown corner context) and the legacy traffic nodes (traffic.json, only to number the nodes as 05 does). Everything
    /// is in Unity space (x = -JS x). Plain C# so the builder runs and is tested without a scene.
    /// </summary>
    public sealed class RoadGraphInput
    {
        public sealed class Road
        {
            public int id;
            public string kind;
            public bool paved, startJ, endJ;
            public double hw;
            /// <summary>Centreline in Unity space; d = canon distance along the road (2D arc length, exactly as JS wrote it).</summary>
            public D3[] pts;
            public double[] d;
            public RoadClass cls;
            public double Length => d[d.Length - 1];
        }

        public struct Lot
        {
            public D2 p;
            public string zone;
        }

        public struct LegacyNode
        {
            public int id;
            public D2 p;
        }

        /// <summary>A building footprint outline (Unity xz): the 4 corners of a rectangle, or a polygon around a circle.</summary>
        public struct Footprint
        {
            public D2[] corners;
        }

        public readonly List<Road> roads = new List<Road>();
        public readonly List<Lot> lots = new List<Lot>();
        public readonly List<LegacyNode> legacyNodes = new List<LegacyNode>();
        public readonly List<Footprint> footprints = new List<Footprint>();

        /// <summary>
        /// Adds a footprint given in JS space: a rectangle (centre, half extents, three.js yaw) like world.js stampConcrete, whose
        /// local (lx, lz) sits at (cx + c lx + s lz, cz - s lx + c lz); or, without a rectangle, a circle of radius r (8 points).
        /// </summary>
        public void AddJsFootprint(double cx, double cz, double rot, double hw, double hd, bool hasRect, double r)
        {
            D2[] pts;
            if (hasRect)
            {
                double c = Math.Cos(rot), s = Math.Sin(rot);
                pts = new D2[4];
                int k = 0;
                foreach (var (lx, lz) in new[] { (hw, hd), (-hw, hd), (-hw, -hd), (hw, -hd) })
                    pts[k++] = new D2(-(cx + c * lx + s * lz), cz - s * lx + c * lz);
            }
            else
            {
                pts = new D2[8];
                for (int k = 0; k < 8; k++) pts[k] = new D2(-(cx + r * Math.Cos(k * Math.PI / 4)), cz + r * Math.Sin(k * Math.PI / 4));
            }
            footprints.Add(new Footprint { corners = pts });
        }
        /// <summary>SHA-256 of CanonData/v1/manifest.json the input came from (empty for synthetic input).</summary>
        public string canonManifestSha256 = "";

        public int PointCount
        {
            get
            {
                int c = 0;
                foreach (var r in roads) c += r.pts.Length;
                return c;
            }
        }

        /// <summary>Adds a road given in JS coordinates (x, y, z, d per point), converting to Unity space.</summary>
        public Road AddJsRoad(int id, string kind, bool paved, double hw, bool startJ, bool endJ, double[] x, double[] y, double[] z, double[] d)
        {
            if (x.Length != y.Length || x.Length != z.Length || x.Length != d.Length || x.Length < 2)
                throw new ArgumentException($"road {id}: point arrays differ in length or have fewer than 2 points");
            var r = new Road
            {
                id = id, kind = kind, paved = paved, hw = hw, startJ = startJ, endJ = endJ,
                pts = new D3[x.Length], d = (double[])d.Clone(), cls = RoadClasses.FromCanon(kind, paved),
            };
            for (int i = 0; i < x.Length; i++) r.pts[i] = new D3(-x[i], y[i], z[i]);
            roads.Add(r);
            return r;
        }

        /// <summary>Adds a road given directly in Unity space; d is recomputed as the 2D arc length.</summary>
        public Road AddUnityRoad(int id, RoadClass cls, double hw, bool startJ, bool endJ, IList<D3> pts)
        {
            var arr = new D3[pts.Count];
            pts.CopyTo(arr, 0);
            var r = new Road
            {
                id = id, kind = cls == RoadClass.Street ? "street" : cls == RoadClass.Dirt ? "dirt" : "paved",
                paved = cls != RoadClass.Dirt, hw = hw, startJ = startJ, endJ = endJ, pts = arr, d = Polyline.ArcLength(arr), cls = cls,
            };
            roads.Add(r);
            return r;
        }
    }
}
