using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Backroads.Roads.Editor;

namespace Backroads.World.Bake
{
    public sealed class WorldBakeOptions
    {
        /// <summary>The TownData extract (its folder name); null = the only one (TownSource.DefaultTownId). Real names never
        /// appear in code (Town.NoRealNames), so the id comes from the data.</summary>
        public string townId;
        public string worldId = "millbrook";
        public bool useBurst = true;
        /// <summary>Lane R's road plates (RoadPlateBuilder) and decks; off for fast tests of the terrain alone.</summary>
        public bool roadPlates = true;
        /// <summary>W's building pads (PadStep).</summary>
        public bool pads = true;
        /// <summary>More plates to submit (tests).</summary>
        public List<Plate> extraPlates;
        /// <summary>Plates from the registered sources (WorldBakePlateSources: lane S's lots when S4 lands).</summary>
        public bool registeredSources = true;
        public uint seed = 20261022u;
        public Action<string> log;
    }

    public sealed class WorldBakeResult
    {
        public WorldBakeOptions options;
        public TownSource town;
        public BakeGrid grid;
        public RiverResult river;
        public SurfaceStep.Result surfaces;
        public PlateSink plates;
        public PlateSink.Result applied;
        public int roadPlates, padPlates, extraPlates, carriageway;
        /// <summary>Plates per registered source, by name.</summary>
        public readonly SortedDictionary<string, int> sourcePlates = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public List<BridgeDeck> decks = new List<BridgeDeck>();
        /// <summary>T3.3: first nature (W6) and the provisional places (W7).</summary>
        public NatureResult nature;
        public PlacesResult places;
        /// <summary>The ground before the river step and the plates (DEM + micro-noise), for reports and tests.</summary>
        public float[] natural;
        public readonly Dictionary<string, double> ms = new Dictionary<string, double>();
        public string platesHash;
    }

    /// <summary>
    /// T3.2: lane W's terrain bake of a town (ADR-006 stages W1, W6 surfaces, W8 inputs): the DEM on the world's grid,
    /// the river, the land surfaces, then every plate (lane R's roads, W's building pads, any extra plates such as lane S's
    /// lots), then the road distance. Deterministic: the same TownData and plates give the same bytes, with Burst on or
    /// off (World.BakeDeterministic). WorldDataExport writes the result.
    /// </summary>
    public static class WorldBake
    {
        /// <summary>1: T3.2 (terrain, river, surfaces, plates); 2: T3.3 (nature, places, buildings, the map).</summary>
        public const int Version = 2;

        public static WorldGridLayout LayoutFor(TownSource t)
        {
            const double cell = 0.5, tintCell = 8;
            double half = t.playHalf;
            int vn = (int)Math.Round(2 * half / cell) + 1, tintN = (int)Math.Round(2 * half / tintCell) + 1;
            return WorldGridLayout.Create(vn, (float)cell, (float)half, tintN, (float)tintCell);
        }

        public static WorldBakeResult Run(WorldBakeOptions o = null)
        {
            o = o ?? new WorldBakeOptions();
            var res = new WorldBakeResult { options = o };
            var sw = Stopwatch.StartNew();
            void Lap(string what)
            {
                res.ms[what] = sw.Elapsed.TotalMilliseconds;
                o.log?.Invoke(string.Format(CultureInfo.InvariantCulture, "world bake {0}: {1}: {2:F0} ms", o.worldId, what, sw.Elapsed.TotalMilliseconds));
                sw.Restart();
            }

            o.townId = o.townId ?? TownSource.DefaultTownId();
            res.town = TownSource.Load(o.townId);
            Lap("town source");
            var g = res.grid = new BakeGrid(LayoutFor(res.town));
            DemKernel.Resample(g, res.town.dem, o.seed, o.useBurst);
            res.natural = (float[])g.@base.Clone();
            Lap("dem");
            res.river = RiverStep.Run(g, res.town);
            Lap("river");
            res.surfaces = SurfaceStep.Run(g, res.town, res.river, res.natural, o.seed);
            Lap("surfaces");

            Array.Copy(g.@base, g.heights, g.Count);
            var sink = res.plates = new PlateSink();
            if (o.roadPlates)
            {
                var kb = KerbBaker.BuildTown(o.townId);
                var plates = RoadPlateBuilder.Build(kb.roads.graph, kb.roads.surfaces, kb.kerbs, kb.roads.source?.dem, kb.settings);
                foreach (var p in plates) sink.Submit(p);
                res.roadPlates = plates.Count;
                res.decks = BridgeBuilder.Build(kb.roads.graph, kb.roads.source?.dem);
                Lap("road plates (lane R)");
            }
            if (o.pads) res.padPlates = PadStep.Submit(res.town, sink);
            if (o.registeredSources)
                foreach (var kv in WorldBakePlateSources.All)
                {
                    int n = 0;
                    foreach (var p in kv.Value(o.townId) ?? Array.Empty<Plate>()) { sink.Submit(p); n++; }
                    res.sourcePlates[kv.Key] = n;
                }
            if (o.extraPlates != null) foreach (var p in o.extraPlates) { sink.Submit(p); res.extraPlates++; }
            res.platesHash = sink.Hash();
            res.applied = sink.Apply(g, o.useBurst);
            Lap("plates");
            res.carriageway = RoadDistanceStep.Run(g, res.applied.carriageway);
            Lap("road distance");
            res.nature = NatureStep.Run(g, res.town, res.river, res.surfaces, res.applied, res.natural, o.seed);
            Lap("nature");
            res.places = PlacesStep.Run(g, res.town, res.river, res.applied);
            PlacesStep.Cameras(g, res.town, res.river, res.applied, res.decks, res.places);
            Lap("places");
            return res;
        }
    }

    /// <summary>
    /// Plate sources of other lanes, so lane W's bake takes their plates without referencing their assemblies: a lane
    /// registers once per domain load (for example from an [InitializeOnLoadMethod] in its editor assembly), with a name
    /// and a function from the town id to its plates. WorldBake.Run submits every source's plates (sources in name
    /// order; the plate order is ADR-007's anyway) and the manifest lists each source with its plate count.
    /// </summary>
    public static class WorldBakePlateSources
    {
        static readonly SortedDictionary<string, Func<string, IEnumerable<Plate>>> s_Sources =
            new SortedDictionary<string, Func<string, IEnumerable<Plate>>>(StringComparer.Ordinal);

        public static void Register(string name, Func<string, IEnumerable<Plate>> source)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("a plate source needs a name");
            lock (s_Sources) s_Sources[name] = source ?? throw new ArgumentNullException(nameof(source));
        }

        public static bool Unregister(string name)
        {
            lock (s_Sources) return s_Sources.Remove(name);
        }

        public static IReadOnlyList<KeyValuePair<string, Func<string, IEnumerable<Plate>>>> All
        {
            get { lock (s_Sources) return new List<KeyValuePair<string, Func<string, IEnumerable<Plate>>>>(s_Sources); }
        }
    }
}
