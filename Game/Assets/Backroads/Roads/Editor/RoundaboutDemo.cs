using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;

namespace Backroads.Roads.Editor
{
    /// <summary>
    /// roads-R4: a synthetic map with one mapped roundabout (the town has none, and none is invented), for the previews:
    /// a closed junction=roundabout ring of 16 nodes and straight residential legs, on a flat DEM inside a kerb zone.
    /// </summary>
    public static class RoundaboutDemo
    {
        public sealed class Bake
        {
            public OsmRoadSource src;
            public RoadGraphInput input;
            public OsmImportReport report;
            public RoadGraph g;
            public RoadSurfaces s;
            public KerbNetwork kn;
            public KerbSettings st;
            public List<StreetInfo> streets;
            public List<RoadNetworkAsset.Prop> props;
            public RoadMeshes meshes;
            public List<Plate> plates;
        }

        public static OsmRoadSource Ring(double radius, double[] legDeg, string legHighway = "residential", double y = 10)
        {
            var src = new OsmRoadSource { source = "synthetic/roundabout", playHalf = 200 };
            const int n = 601;
            var h = new float[n * n];
            for (int k = 0; k < h.Length; k++) h[k] = (float)y;
            src.dem = new GridHeightField(h, n, n, -300, -300, 1);
            src.kerbZone = new[] { new D2(-160, -160), new D2(160, -160), new D2(160, 160), new D2(-160, 160) };
            var ring = new List<long>();
            for (int k = 0; k < 16; k++)
            {
                double a = 2 * Math.PI * k / 16;
                src.nodes[100 + k] = new OsmRoadSource.Node { id = 100 + k, x = radius * Math.Cos(a), z = radius * Math.Sin(a), tags = new Dictionary<string, string>() };
                ring.Add(100 + k);
            }
            ring.Add(100);
            src.ways.Add(new OsmRoadSource.Way { id = 1, osm = "way/1", nodes = ring.ToArray(), tags = new Dictionary<string, string> { ["highway"] = "tertiary", ["junction"] = "roundabout" } });
            long id = 1000, way = 2;
            foreach (var deg in legDeg)
            {
                double a = deg * Geo.Deg;
                int k = (int)Math.Round(a / (2 * Math.PI) * 16) % 16;
                long n1 = id++, n2 = id++;
                src.nodes[n1] = new OsmRoadSource.Node { id = n1, x = 60 * Math.Cos(a), z = 60 * Math.Sin(a), tags = new Dictionary<string, string>() };
                src.nodes[n2] = new OsmRoadSource.Node { id = n2, x = 140 * Math.Cos(a), z = 140 * Math.Sin(a), tags = new Dictionary<string, string>() };
                src.ways.Add(new OsmRoadSource.Way { id = way, osm = "way/" + way, nodes = new[] { 100 + k, n1, n2 }, tags = new Dictionary<string, string> { ["highway"] = legHighway } });
                way++;
            }
            return src;
        }

        public static Bake Run(OsmRoadSource src)
        {
            var b = new Bake { src = src, st = new KerbSettings() };
            b.input = OsmRoadImporter.Import(src, null, out b.report);
            b.g = RoadGraphBuilder.Build(b.input);
            b.s = SurfaceBuilder.Build(b.g, b.input);
            b.kn = KerbBuilder.Build(b.g, b.input, b.s, src.playHalf, b.st);
            b.streets = StreetNamer.Assign(b.g, new List<string>(), false);
            b.props = RoadPropBuilder.Build(b.g, b.s, b.kn, b.streets, b.st);
            b.meshes = RoadMeshBuilder.Build(b.g, b.s, b.kn, b.streets, b.st);
            b.plates = RoadPlateBuilder.Build(b.g, b.s, b.kn, src.dem, b.st);
            return b;
        }
    }
}
