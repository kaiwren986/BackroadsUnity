using Backroads.World;

namespace Backroads.Roads
{
    /// <summary>The builder input from the imported canon (CanonAsset: world.json roads and lots, traffic.json nodes).</summary>
    public static class RoadGraphCanon
    {
        public static RoadGraphInput FromCanon(CanonAsset canon)
        {
            var input = new RoadGraphInput { sourceManifestSha256 = canon.manifestSha256 ?? "", source = "CanonData/v1" };
            foreach (var r in canon.world.roads)
                input.AddJsRoad(r.id, r.kind, r.paved, r.hw, r.startJ, r.endJ, r.x, r.y, r.z, r.d);
            if (canon.world.lots != null)
                foreach (var l in canon.world.lots)
                    input.lots.Add(new RoadGraphInput.Lot { p = new D2(-l.x, l.z), zone = l.zone });
            if (canon.traffic != null && canon.traffic.nodes != null)
                foreach (var n in canon.traffic.nodes)
                    input.legacyNodes.Add(new RoadGraphInput.LegacyNode { id = n.id, p = new D2(-n.x, n.z) });
            if (canon.placements != null && canon.placements.footprints != null)
                foreach (var f in canon.placements.footprints)
                    input.AddJsFootprint(f.x, f.z, f.rot, f.hw, f.hd, f.hasRect, f.r);
            return input;
        }

        public static RoadGraph Build(CanonAsset canon) => RoadGraphBuilder.Build(FromCanon(canon));
    }
}
