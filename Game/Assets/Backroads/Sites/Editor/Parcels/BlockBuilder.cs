using System;
using System.Collections.Generic;
using Backroads.Core.WorldBake;
using Backroads.Roads;
using Clipper2Lib;
using Unity.Mathematics;

namespace Backroads.Sites.Editor
{
    /// <summary>
    /// sites-S1 step 1: blocks from roads-R2's block loops (the faces of the planar street graph: play area minus every
    /// road and junction surface, each loop with its block on the left). A block is its loop with its holes, minus the
    /// sidewalks (cut back to the walk's back edge), minus water, and where no road closes it, bounded at RURAL_DEPTH from
    /// its frontage. A loop that falls apart (a river through it) gives one block per piece. Deterministic (Clipper at mm).
    /// </summary>
    public static class BlockBuilder
    {
        public sealed class Result
        {
            public readonly List<Block> blocks = new List<Block>();
            public readonly List<FrontLine> fronts = new List<FrontLine>();
            /// <summary>Block regions (Clipper, mm), indexed like blocks.</summary>
            public readonly List<Paths64> regions = new List<Paths64>();
            public readonly List<string> notes = new List<string>();
            public double ruralDepth;
        }

        public const double RuralDepth = 120;
        /// <summary>Blocks smaller than this are road slivers (a traffic island, a corner scrap): dropped.</summary>
        public const double MinBlockArea = 25;

        /// <summary>Land kept round a building that stands beyond RURAL_DEPTH (a farm down a long drive), metres.</summary>
        public const double KeepRound = 40;

        /// <param name="keep">Footprints of the buildings that need a parcel: one beyond RURAL_DEPTH keeps the land within
        /// KeepRound of it (its block piece then reaches the street by an easement).</param>
        public static Result Build(KerbNetwork kn, IEnumerable<SiteTownData.MappedArea> water, ISet<string> alleyWays, double playHalf,
            IEnumerable<double2[]> keep = null, double ruralDepth = RuralDepth)
        {
            var keepRegion = new Paths64();
            if (keep != null)
                foreach (var k in keep) if (k != null && k.Length >= 3) keepRegion.AddRange(Clip.Region(k));
            if (keepRegion.Count > 0) keepRegion = Clip.Inflate(Clip.Union(keepRegion), KeepRound);
            var res = new Result { ruralDepth = ruralDepth };
            // Face lines (KerbFrontage geometry: kerb and back edge at the same s).
            var frontsByLoop = new Dictionary<int, List<FrontLine>>();
            foreach (var f in kn.faces)
            {
                int n = f.Count;
                var fl = new FrontLine
                {
                    faceId = f.id, osmWay = f.osmWay ?? "", street = f.cls, kerbed = f.kerbed, bridge = f.bridge,
                    alley = alleyWays != null && f.osmWay != null && alleyWays.Contains(f.osmWay),
                    kerb = new double2[n], back = new double2[n], s = new float[n],
                };
                for (int i = 0; i < n; i++)
                {
                    fl.kerb[i] = new double2(f.kerb[i].x, f.kerb[i].z);
                    fl.back[i] = new double2(f.back[i].x, f.back[i].z);
                    fl.s[i] = (float)f.s[i];
                }
                res.fronts.Add(fl);
                if (!frontsByLoop.TryGetValue(f.loop, out var l)) frontsByLoop[f.loop] = l = new List<FrontLine>();
                l.Add(fl);
            }

            // Holes join the smallest outer loop that contains them.
            var outers = new List<KerbLoop>();
            var holes = new List<KerbLoop>();
            foreach (var l in kn.loops) (l.hole ? holes : outers).Add(l);
            var ringOf = new Dictionary<int, double2[]>();
            foreach (var l in kn.loops)
            {
                var r = new double2[l.Count];
                for (int i = 0; i < l.Count; i++) r[i] = new double2(l.pts[i].x, l.pts[i].z);
                ringOf[l.id] = r;
            }
            var holesOf = new Dictionary<int, List<KerbLoop>>();
            foreach (var h in holes)
            {
                var p = ringOf[h.id][0];
                KerbLoop best = null;
                double bestArea = double.MaxValue;
                foreach (var o in outers)
                {
                    double a = Math.Abs(Poly2.SignedArea(ringOf[o.id]));
                    if (a < bestArea && Poly2.Contains(ringOf[o.id], p)) { best = o; bestArea = a; }
                }
                if (best == null) { res.notes.Add($"hole loop {h.id} lies in no outer loop"); continue; }
                if (!holesOf.TryGetValue(best.id, out var hl)) holesOf[best.id] = hl = new List<KerbLoop>();
                hl.Add(h);
            }

            // Walks per loop (KerbPiece Walk, by its face's loop).
            var walksOf = new Dictionary<int, Paths64>();
            foreach (var pc in kn.pieces)
            {
                if (pc.kind != KerbPieceKind.Walk || pc.poly == null || pc.poly.Length < 3) continue;
                var f = kn.Face(pc.face);
                if (f == null) continue;
                var ring = new double2[pc.poly.Length];
                for (int i = 0; i < ring.Length; i++) ring[i] = new double2(pc.poly[i].x, pc.poly[i].z);
                if (!walksOf.TryGetValue(f.loop, out var w)) walksOf[f.loop] = w = new Paths64();
                var path = Clip.Path(ring);
                if (Clipper.Area(path) < 0) path.Reverse();
                w.Add(path);
            }

            var waterRegion = new Paths64();
            if (water != null)
                foreach (var a in water)
                    foreach (var (o, hs) in a.polys) waterRegion.AddRange(Clip.Region(o, hs));
            waterRegion = Clip.Union(waterRegion);
            var play = Clip.Region(new[] { new double2(-playHalf, -playHalf), new double2(playHalf, -playHalf), new double2(playHalf, playHalf), new double2(-playHalf, playHalf) });

            outers.Sort((x, y) => x.id.CompareTo(y.id));
            foreach (var o in outers)
            {
                var loopHoles = holesOf.TryGetValue(o.id, out var hl) ? hl : new List<KerbLoop>();
                var holeRings = new List<double2[]>();
                foreach (var h in loopHoles) holeRings.Add(ringOf[h.id]);
                var region = Clip.Region(ringOf[o.id], holeRings);
                var walks = new Paths64();
                if (walksOf.TryGetValue(o.id, out var w0)) walks.AddRange(w0);
                foreach (var h in loopHoles) if (walksOf.TryGetValue(h.id, out var wh)) walks.AddRange(wh);
                if (walks.Count > 0) region = Clip.Difference(region, Clip.Union(walks));
                if (waterRegion.Count > 0) region = Clip.Difference(region, waterRegion);
                region = Clip.Intersect(region, play);

                // RURAL_DEPTH: keep what lies within ruralDepth of the loop's frontage (the walk back edges).
                var lines = new List<double2[]>();
                var faceIds = new List<int>();
                void AddFronts(int loop)
                {
                    if (!frontsByLoop.TryGetValue(loop, out var fl)) return;
                    foreach (var f in fl) { lines.Add(f.back); faceIds.Add(f.faceId); }
                }
                AddFronts(o.id);
                foreach (var h in loopHoles) AddFronts(h.id);
                double before = Clip.Area(region);
                bool rural = false;
                if (lines.Count > 0)
                {
                    var band = Clip.Band(lines, ruralDepth);
                    if (keepRegion.Count > 0) band = Clip.Union(band, keepRegion);
                    var cut = Clip.Intersect(region, band);
                    if (before - Clip.Area(cut) > 1.0) rural = true;
                    region = cut;
                }
                else { res.notes.Add($"loop {o.id} has no frontage: skipped"); continue; }

                foreach (var piece in Clip.Pieces(region))
                {
                    double area = Clip.Area(piece);
                    if (area < MinBlockArea) continue;
                    var b = new Block { id = res.blocks.Count, loop = o.id, area = area, rural = rural };
                    foreach (var path in piece)
                    {
                        var pts = Clip.Pts(path);
                        if (Clipper.Area(path) > 0) b.outers.Add(pts); else b.holes.Add(pts);
                    }
                    // Faces of this piece: those whose back edge runs along it.
                    var mine = new List<int>();
                    for (int k = 0; k < lines.Count; k++)
                    {
                        var l = lines[k];
                        int on = 0, n = 0;
                        for (int i = 0; i < l.Length; i += Math.Max(1, l.Length / 8), n++)
                            if (NearBoundary(piece, l[i], 0.25)) on++;
                        if (n > 0 && on * 2 >= n) mine.Add(faceIds[k]);
                    }
                    mine.Sort();
                    b.faces = mine.ToArray();
                    foreach (var fid in mine)
                        foreach (var f in res.fronts)
                            if (f.faceId == fid) f.block = b.id;
                    res.blocks.Add(b);
                    res.regions.Add(piece);
                }
            }
            res.fronts.Sort((x, y) => x.faceId.CompareTo(y.faceId));
            return res;
        }

        static bool NearBoundary(Paths64 region, double2 p, double tol)
        {
            foreach (var path in region)
            {
                var ring = Clip.Pts(path);
                if (Poly2.BoundaryDistance(ring, p, out _) <= tol) return true;
            }
            return false;
        }
    }
}
