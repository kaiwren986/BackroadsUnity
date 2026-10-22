# M3 handoff: lane S, card sites-S4 (surfaces, grading, kerbs, markings, props, colliders)

Branch `m3/s-surfaces` in `D:\Backroads-Unity-wt\s`, cut from `m3/s-site-plans` at `867dc38` (S3, since merged), main
(`91b0690`: S3 and lane R's validation) merged in at `5f64314`. One code commit, `d7b6805`, then this handoff. Nothing
was pushed, installed, downloaded or signed into; no Synty file was touched or committed (the packs are not imported in
this worktree: props are placed by prefab path, with stand-ins of our own here); lane R's and lane W's code and data were
only read.

## In plain words

Millbrook's site plans are now things you can drive on. Every lot, drive, walk, forecourt and pad is a real surface:
dark asphalt (lot asphalt a shade lighter than the road's), concrete with its joints, gravel and dirt yards drawn by the
terrain itself, white stall lines, blue accessible stalls with their symbol and hatched aisle, arrows, kerbs round the
lots and their planted islands (with lamp posts in them), concrete wheel stops, and props from the user's Synty packs
(lamps, parking signs, bins, benches, dumpsters, mailboxes, pumps' bollards and the like; our own simple posts and boards
where the packs have nothing, such as accessible-parking signs and drive-through menu boards). The ground is graded
under them: each site gets a level platform for its lot, its drives climb from the street no steeper than 10 % where
the ground allows, every surface meets lane R's driveway apron exactly (0.00 mm on all 215) and the sidewalk flush, and
the terrain is pushed just under every paved surface so it never shows through. The meshes come in the roads' 64 m
pieces with a near and a far version and a collider. Pictures in `M3-handoff-S-S4/`: `surfaces-village.png` and
`surfaces-downtown.png` (seen from straight above, with lane R's roads; dots are props: orange Synty, blue ours, yellow
lamps), `surfaces-parcel-0188.png` (a kerbed shop lot close up), `surfaces-parcel-0143.png` (the fuel station: the
forecourt now runs under the canopy), `surfaces-parcel-0040.png` (a lot S3 put on a steep bank, see below),
`fixtures.png` (one made-up site per business type over a ground rising 5 % from the street); the full list is
`surfaces-report.txt`.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Surfaces**: every plan surface (kerbed islands cut out of the paving) cut into the cells of a 4 m world grid, each cell split until its triangles lie within 1 cm of the grade (down to 0.5 m), triangulated (fan or ear clipping, slivers dropped). Asphalt, concrete and pavers become mesh parts in lane R's format (asphalt and concrete submeshes, UV0 world planar x/4, z/4, lot asphalt lighter through the vertex colour's patch and edge channels, normals from the grade); gravel, dirt and grass are the terrain's own surface (plates only). Concrete joints every 3.6 m (forecourts, aprons, pads) and walk scores every 1.5 m are thin dark paint lines (lane R's shader samples no UVs). Town: 22,446 m² paved, 11,217 m² gravel, dirt and grass | `Sites/Editor/Bake/SiteMesher.cs` |
| 2 | **Grading plates** to lane W: one PerVertex plate per mesh triangle (so W's terrain reproduces the mesh exactly), paving 0.06 m (asphalt) / 0.05 m (concrete) under it as lane R's plates are, unpaved 4 cm under the grade, a 1 m grass verge round the paving 4 cm under its edge (on a neighbour's land it yields to everything there), planting beds in kerbed islands; ids `site:`, priorities verge < yard < paving < island. **The grade** (PlanGrade): a platform plane on each site's lots, forecourts, yards, pads and site aprons, its tilt fitted to the ground and clamped to design §9 (2 % with accessible stalls or on forecourts, 5 % lots and pads, 8 % yards; on hillsides relaxed up to 12 % to keep cut and fill within 1.5 m), its height from the entrances, the door sills and the ground; drives get a ramp from the street that follows the smoothed ground but never climbs faster than 10 % from the street or from the lot or door at their far end, level across beyond the first 6 m; exact anchors at lane R's apron lot edges (`lotEdgeYA/YB`), at door sills (the building's floor, buildings.json `y`, where lane W's pad and lane A's model stand; doors more than 0.6 m off get steps instead) and where a surface meets the walk's back edge (`walkBackY`); the anchors' corrections blend by group so a ramp starts level. Town: 82,973 plates | `Sites/Editor/Bake/SiteGrading.cs`, `SiteMesher.cs` |
| 3 | **Kerbs and islands** (our own extruder; lane R's is private): S2's kerb lines, 150 mm barrier or 100 mm mountable, face, 0.15 m top and back skirt, broken at kerb ramps; 1,389 m in town, 14 island beds. **Markings** as one paint submesh per chunk (LOD0 only), draped 5 mm over the paving: stall lines, stop bars, fire-lane kerb paint, hatching, accessible symbols (blue square, white wheelchair), arrows, fire-lane stencils (a red frame); 4,835 m. **Wheel stops**: 1.8 x 0.15 x 0.12 m concrete bars of our own, 33 | `SiteMesher.cs` |
| 4 | **Props** as prefabs (never edited or merged): design §7's table by key (`SitePropTable`: Synty paths, variants by SITE_SEED; our own stand-ins for `sign.accessible`, `sign.vanAccessible`, `menu.board`), each on the graded ground (on a bed, a surface or the DEM), kept inside its parcel and 0.3 m clear of stalls, aisles, drive-through lanes, buildings and the 1.2 m before every door (moved up to 2 m, else dropped and listed); fences and gates; lane A's ModelRequests as stand-in boxes. `SitePropPlacer.Place(town)` puts them in a scene (Synty prefabs where the packs are imported, our stand-ins elsewhere; fence pieces repeated along each run). Town: 367 props (348 Synty, 19 ours; 65 moved, 9 dropped), 4 lane A stand-ins | `Sites/Editor/Bake/SiteProps.cs`, `Sites/Runtime/Bake/SitePropSet.cs`, `Sites/Client/Editor/SitePropPlacer.cs` |
| 5 | **Colliders**: paved surfaces, kerbs and wheel stops in each chunk's MeshCollider (lane R's chunk format); props keep their prefab colliders; fences get box colliders at run time (`SiteChunks.SpawnFenceColliders`); our stand-ins and lane A's boxes get box colliders | `Sites/Runtime/Bake/SiteChunks.cs` |
| 6 | **Chunks and LOD** with lane R's `RoadChunkBaker` on the same 64 m grid: LOD0 46,207 triangles (budget 150,000), LOD1 33,213, collider 33,543, 124 chunks; `BrSiteChunks_(town)` (+ gzip data, LFS) and `BrSiteProps_(town)` in `Sites/Baked/Resources`; `SiteChunks.Spawn` (server: colliders only) and `SiteChunkView.SpawnTown` (client, lane R's three materials) build them with lane R's loader | `Sites/Editor/Bake/SiteBake.cs`, `Sites/Client/SiteChunkView.cs` |
| + | `SitesBakeCli.Bake` (bakes twice, fails on a hash difference or a problem, saves the assets; `-verifyHash`: fails on a stale bake); menu *Backroads > Sites > Bake surfaces and props (S4)*; the report and top-down renders (`SiteBakeMap`, a CPU raster that also draws lane R's committed road chunks); `tools/world/sites/(town)/driveways.json`, every driveway request in the contract's terms with R's answers, for lanes R and W | `SiteBake.cs`, `SiteBakeMap.cs` |

## Done when, expected vs measured

| Item | Measured (EditMode `Sites.Surfaces`, 9 town tests + 26 fixtures; 105 Sites tests in all) |
|---|---|
| Every plan is drawn | **pass**: 255 plans graded; every surface of 1 m² or more has its plates, the paved meshes cover the paved area (22,655 m² of surface and kerb top for 22,446 m² of paving) |
| Terrain 0.25 m inside every paved site polygon ≤ surface - 0.03 m | **pass** with lane W's real bake (`WorldBake.Run` with the site plates and, as lanes R and W will add them, lane R's apron plates for our cuts): 6,469 points (not within 0.75 m of a wall), **1 over**, by 39 mm, beside a downtown apron where no plate covers the ground (allowed: at most 0.1 %, each listed, as lane R's own check allows a few) |
| Every driveway apron meets its lot within 5 mm | **pass**: 0.00 mm on all 215 baked cuts. Where S3 drew no drive to a baked cut (13), S4 added a drive stub to 5; 9 aprons still lead onto nothing of their plan (listed with their reasons; the test caps them at 5 %), see below |
| All grades within the limits or reported | **reported**: 181 notes (drives over 12 % on 88 plans, walks 51, lots 11, accessible stalls 9, cut or fill over 1.5 m on 10), each with its place; the plates 5 m round the steepest are in the report too |
| Prop placements within their parcel and clear of stalls, aisles and doors | **pass**: 367 placements, none off its parcel, on a stall or aisle, in a building or within 0.8 m of a door |
| Total site triangles ≤ SITE_TRI_BUDGET (150k) | **pass**: LOD0 46,207 |
| Deterministic | **pass**: two bakes, same hash (`SiteSurfacesTests.Deterministic`, `SitesBakeCli.Bake`) |
| Extra | the committed bake equals a fresh one and loads from Resources; a ray from above hits each of five paved lots' colliders at the mesh's height; the placer puts one object per placement in a scene at its place and yaw; every fixture type stages without problems, its plates CCW triangles |

## Where this differs from the card

- **Draping**: the surfaces sit on our own grade and the plates put the terrain under them at lane R's depths, instead
  of draping 0.02 m over a finished terrain: the terrain bake follows the plates, so mesh and ground agree by
  construction.
- **Plates are triangles, not one least-squares plane per area**: one plane cannot meet two apron edges and a door at
  once. The platform plane is still least-squares (on the ground), the rest are exact anchors and ramps.
- **Joint UVs**: lane R's shader samples no UVs, so concrete joints and walk scores are thin paint lines, and lot
  asphalt is lighter through the vertex colour, not a tint property.
- **Kerbs** come from our own extruder; **colliders** are in our own chunk set (lane R's format and loader) rather than
  inside lane R's per-chunk collider, which has no hook for another lane.
- **Grades over the limits are many on this hilly town**: 88 sites have a drive steeper than 12 % somewhere (median of
  those 18 %), mostly where the ground itself is that steep within a few metres (streets in cuts, river banks). The
  worst is **parcel 40**, a 39-stall lot S3 put across a bank that falls about 50 %: 11 m of cut and fill even at a
  12 % plane. That is S3's lot placement (it does not look at the slope): a follow-up for S5 or a later S3 pass.
- **Doors**: buildings stand at the DEM height at their centroid (lane W's pads, lane A's models), so on slopes a door
  can sit well above or below the lot; doors more than 0.6 m off get steps rather than pulling the lot to them.
- **Drive stubs and unreached aprons** (S3 defects): 13 baked cuts led onto nothing of their plan; S4 adds a stub
  where there is room (5). 9 remain (8 without room, one whose stub stops 0.8 m short), mostly cuts lane R moved or
  merged off the parcel's own frontage or whose lot edge lies outside the parcel; listed in the report. A follow-up for
  S3's driveway geometry.
- **S3 fix**: surfaces now run under open roofs (fuel canopies, carports); the two fuel fixture snapshots were
  rewritten after review.
- **Fences**: the town's plans have none (S3 makes them for yards and farms, which Millbrook lacks); fence runs come
  out on the fixtures (car repair, warehouse, industrial, farm).

## For other lanes

- **Lane W** (all small, all in your files):
  1. *Site plates*: `SitePlateSource` registers source "sites" with `WorldBakePlateSources` but is **off**
     (`SitePlateSource.Enabled`, lane S's file): turning it on changes WorldData, so do it in the same merge as your
     WorldData re-export (World.BakeDeterministic compares with the committed hashes). Either flip the flag (tell me) or
     call `SitePlateSource.Register()` before `WorldBakeCli.Export`.
  2. *Apron plates*: read `tools/world/sites/(town)/driveways.json`, `new DrivewayCutter(KerbFrontageSource.LoadTown(town)).Resolve(requests)`
     and pass the cuts and that cutter to `RoadPlateBuilder.Build` (that is how `Sites.Surfaces` checks the terrain).
  3. *Pads*: skip `building=roof|canopy|carport` (open roofs): the forecourt runs under the fuel canopy, and a dirt pad
     there at the DEM's height would cover it.
  4. *Scene (T3.3)*: `SiteChunkView.SpawnTown(town, parent)` next to `RoadChunkView` on a client,
     `SiteChunks.Spawn(town, parent, null)` on a server; `SitePropPlacer.Place(town, parent)` in the scene build (the
     Synty packs must be imported there).
  5. *Proposal*: building floors from the site grade (`DoorSpec.sillY`, which the contract has) instead of the DEM at
     the centroid, so doors meet their lots; needs lane A to follow. Not needed for M3.
- **Lane R**: road meshes: dropped kerbs and apron slabs at the cuts in `tools/world/sites/(town)/driveways.json`
  (until then the kerb runs across every driveway mouth and the walk stays at full height there).
- **Lane A**: nothing new; the 4 fuel ModelRequests stand in as grey boxes until modelled.

## Next

- **sites-S5** (this lane): validation, the profile check, `tools\ci\sites-bake.ps1`, screenshots `m3.shots.sites`
  (needs the Synty packs and lane W's scene).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Sites
Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Sites.Editor.SitesBakeCli.Bake [-verifyHash]
```

Report and renders: `artifacts\sites\(town)\surfaces-report.txt`, `surfaces-*.png`; fixtures `artifacts\sites\s4\fixtures\`.

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'S'; tools\ci\gate.ps1 -Tier merge` (lane S paths only; the
diff against main holds S4): **pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1456\gate-merge`, on
`d7b6805`): oracle.hash; vendor.check (584 files, 0 differences); native.ctest; unity.editmode 340 passed, 1 skipped
(ServerContents: no server build), 0 failed; unity.playmode 20 of 20; build.servercompile (the new runtime code compiles
with UNITY_SERVER; the client assembly is left out there); build.dev (233 MiB); autotest.smoke. The native step's
`EngineSimPlugin.dll` was restored afterwards (lane V's). Temp files were pointed at D: for the Unity runs (lane V's
tip while C: was full).
