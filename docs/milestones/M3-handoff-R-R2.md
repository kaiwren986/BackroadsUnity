# M3 handoff: lane R, card roads-R2 (kerbs, walks, ramps, driveway cuts, road plates)

Branch `m3/r-kerbs` in `D:\Backroads-Unity-wt\r`, cut from `main` at `92797f8` (R1 merged, Clipper2 vendored). Nothing
was pushed, installed, downloaded or signed into.

## In plain words

Every street in Millbrook's village now has kerbs and sidewalks, and every road outside it has a plain edge with a
gravel shoulder. The kerbs come from one boolean of the asphalt (Clipper2), so kerb and road can never disagree. Every
kerbed street corner has a lowered kerb with short ramps for wheelchairs and prams (four downtown corners with very wide
walks get proper perpendicular ramps). Downtown the sidewalk runs right up to the shop fronts. Each side of each road
is a "face" that lane S can put driveways on: it knows where driveways are not allowed (corners, crosswalks, ramps,
stop lines, drains) and a resolver cuts the driveways in, moving one up to 3 m or refusing it with a reason. Finally
R2 hands lane W the "plates" that shape the terrain under roads, walks and verges, so the ground never pokes through
the asphalt and meets each walk's back edge. Pictures: `M3-handoff-R-R2/graph-kerbs.png` (the village),
`graph-kerbs-downtown.png` and `graph-kerbs-junction.png` (one downtown junction up close, with the plate outlines).

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Kerb zone and walks**: one rule shared with R1's gutter pans (`KerbRules`): Streets and MainStreet inside `KERB_ZONE`, or anywhere with a mapped sidewalk, are kerbed; sidewalk tags decide each side (none are mapped in Stockport, so the rule gives both). Walks 1.5 m (Street), 1.8 m (MainStreet), plus the 0.15 m kerb top; downtown the walk fills to a building line within 6 m (a ray along the kerb normal), else 2.4 m. Unkerbed edges get a 1.2 m gravel shoulder | `Roads/Runtime/KerbRules.cs`, `KerbBuilder.Widths` |
| 2 | **Kerb loops by Clipper2** (05 §5.7): the footprint is every R1 ribbon corridor (pavement edge to pavement edge, end rows sharing every mouth vertex) and junction surface, plus a corridor between junctions whose mouths touch; blocks = play area − footprint; each block outline is a loop with the block on its left. Integer Clipper at 0.1 mm with collinear points kept: every loop vertex that is an R1 vertex keeps its exact double position and height. Each loop segment is matched to the R1 piece it lies on (a road side, a corner arc, a dead-end cap, the play-area edge) and the loop splits into faces, one per road side between corners; a corner arc splits at its middle, or goes wholly to the street when the other road is an unkerbed alley. Kerb types: `Barrier150` (every kerbed face), `Dropped25` at corners and cuts | `Roads/Editor/KerbBuilder.cs` |
| 3 | **Corner ramps** (05 §5.7): every corner with a walk on either side gets a depressed corner: half the arc (2.4-6 m), widened over a marked crosswalk that lands on a walk, at `Dropped25`, ramps of 1.83 m either side (6.8 %, under 7.5 % design and PROWAG's 8.33 %), landing 1.2 x 1.2 m at the walk's 1.5 % cross fall, detectable warning 0.61 m along the lowered stretch (texture). Where the walk is 3.2 m deep or more over the whole arc, two perpendicular ramps (1.5 m openings, 10 % flares) instead | `KerbBuilder.Corners`, `Ramp`, `KerbHeight` |
| 4 | **`KerbFrontage` export** (overview contract 2): per face the kerb line, s, gutter (the asphalt edge height, the flowline on kerbed streets), top of kerb, walk back edge and its height, kerb type, class, speed, one-way and the forbidden intervals: corner return + 3 m (local) / + 9 m (MainStreet, Highway), ramps, marked crosswalks + 0.5 m, stop-line zones ± 2 m on the stopping side, curb inlets ± 0.75 m, the whole face on bridges, and 15 m + the return around R1's four fallback nodes. In its own asset next to the road network, linked by the network's content hash, 8 packed floats per station; `KerbFrontageSource` (runtime, `IKerbFrontageSource`) reads it. R0o's `ProvisionalKerbFrontage` stays for comparison | `Roads/Runtime/KerbFrontageAsset.cs`, `KerbNetwork.cs`; `Baked/Resources/BrKerbFrontage_stockport-oh.asset` |
| 5 | **Driveway cuts** (overview contract 3): `DrivewayCutter.Resolve(requests)`, one cut per request in request order, handled sorted by (face, s0, id). A throat on a forbidden interval moves by the smallest shift up to 3 m (forwards on a tie) or is rejected with that interval's reason; mapped driveways move only off corner returns and override the rest; requests whose throats or flares meet merge (a shared driveway; `mergedWith` names the first). Kerbed: `Dropped25` over the throat, linear flares (1.0 m residential, 1.5 m commercial and fuel, 3 m truck, or the request's own), the walk crossing at 1.5 % from the dropped kerb, the apron across the walk, `lotEdgeA/B` and their heights at the walk's back edge. Unkerbed: the apron spans the shoulder, widened by flares or radius returns (9 m trucks, 6 m fields by default), level with the pavement edge | `Roads/Runtime/DrivewayCutter.cs` |
| 6 | **Road plates** (05 §5.10, overview contract 4): small convex pieces with per-vertex heights so W's triangulation reproduces them: ribbon quads (station to station, gutter line to gutter line) and junction triangles (R1's mesh) sunk 0.06 m; then the kerbside partition (`KerbPieceBuilder`): walks, then a flat 1.2 m grass verge behind each walk at the back edge's height, then the gravel shoulders, each piece keeping only what no earlier piece or the asphalt holds, so nothing overlaps. Under a walk the ground runs from the asphalt's sunk level at the kerb face to the back edge less 0.05 m, so a coarse terrain grid never pokes up through the gutter. Paved plates (asphalt, concrete) are `noDeform`; gravel and dirt are the ground itself (no sink, ruts allowed). Side slopes: blend = 3 x the step to the DEM, 2-12 m. Priorities: verge and shoulder 0, walk 10, ribbon 20, junction 30, driveway apron 40. No plates on bridges or in tunnels. After `Resolve`, the cuts' aprons lower the walk plate under each driveway | `Roads/Editor/RoadPlateBuilder.cs`, `KerbPieceBuilder.cs` |
| + | Report `artifacts\roads\(town)\kerbs-report.txt` (summary, clearance list, every kerb return and face with its intervals), maps `graph-kerbs*.png`; menu *Backroads > Roads > Bake kerbs and frontage (R2)*; `RoadBakeCli.BakeTowns` now bakes R0o + R1 + R2 and `-verifyHash` checks both assets; `RoadBakeCli.Kerbs` (in memory, `-at x,z -size m` draws a close-up) | `Roads/Editor/KerbBaker.cs`, `RoadGraphMap.Kerbs.cs`, `RoadNetworkBaker.cs` |

## Done when, expected vs measured

| Item | Measured |
|---|---|
| Every block face has a kerb loop or an unkerbed edge | **pass** (`Kerbs.LoopsAndFaces`, town and both fixtures): 59 block loops (4 holes), 374 faces (264 kerbed, all with walks; 110 unkerbed), **0 road sides without a face**; every loop segment runs along a road side, a dead-end cap or the play-area edge; every kerb point lies on R1's asphalt outline (within 2 mm) |
| Kerb-to-building clearance ≥ the walk width (1.0 m downtown) or reported | **pass, 18 faces reported** (`Kerbs.ClearanceReported`): mapped buildings 0.01-1.02 m from the kerb (14 faces reaching downtown, 4 residential). The walk stops at the building there |
| Ramps on every kerbed corner | **pass** (`Kerbs.RampsOnEveryKerbedCorner`): 211 of 211 kerbed corners (207 depressed, 4 perpendicular), each with a 1.2 m landing at `Dropped25`; walk cross fall ≤ 2 % everywhere |
| Synthetic test of `Resolve` | **pass** (`Driveways.Resolve`): a clear request bakes; an overlapping one merges (shared throat 30-35 m); a throat on an inlet moves 2 m back (the smaller of 2 m back and 2.5 m on); one deep in a corner return is rejected `CornerReturn`; a mapped one on an inlet stays, one 2 m into a corner return moves 2 m, one deep in it is rejected; an unknown face is rejected `Other`; an unkerbed truck entrance gets 9 m returns; the same requests reversed give the same cuts. On the town (`Driveways.OnTheTown`): one request in the middle of every face's longest open stretch, 312 cuts, all baked as asked, and the ground stays under each dropped walk |
| Terrain 0.25 m inside every footprint ≤ surface − 0.03 m; within 0.1 m of the edge 1 m outside | **pass** with a reference plate evaluation (`RoadPlates.TerrainUnderAndBesideThePavement`; lane W's applier does not exist yet): **32,224 points** under the town's paved ribbons, junctions and walks, all at least 0.03 m under the surface; **13,429 points** 1 m beside every paved edge, all within 0.1 m except 2 that sit between two walk edges at different heights about 2 m apart (no ground can meet both; listed by the test), with 1,057 within 1 m of a building (the building pad comes later) and 125 on another road's paving not counted |
| Two bakes give identical hashes | **pass**: `RoadBakeCli.BakeTowns -verifyHash` (network `e8df69a1`, unchanged by R2; frontage `0f337dbc`), `Kerbs.Deterministic` (frontage and the 86,187 plates), `Kerbs.FrontageAssetUpToDate` (the committed frontage is current and reads back exactly) |

Road tests: **67 passed** (17 new). Full EditMode **186 passed, 1 skipped**, 0 failed, with `BR_OWNERS_BASE=main` (lane R paths only). PlayMode 9 of 9.

## The town (kerbs-report.txt)

- Kerb 13.5 km (all with walks), unkerbed edges 15.2 km. Walk depth median 1.95 m, downtown up to 6.15 m.
- Frontage open to driveways: 23.2 of 28.3 km (82 %). Forbidden: 524 corner returns, 371 ramps, 99 stop-line zones,
  82 crosswalks, 229 inlets, 8 bridge faces.
- Plates: 86,187 (44,740 asphalt quads and triangles, 14,866 walk, 17,228 verge, 9,353 shoulder pieces).
- The frontage asset is 1.4 MB; the road network asset is byte-identical to R1's (the shared kerb rule changes nothing
  in Stockport).

## Where this differs from the card

- **Loops over the whole play area**, not only `KERB_ZONE`: every road side gets a face, so lane S has frontage for the
  rural lots, farms and the quarry too (unkerbed, with shoulders).
- **Kerb types**: `Mountable100` is never chosen (nothing tells newer subdivisions apart); `Apron75` is the roundabout's
  (R4) and is not in ADR-007's `KerbKind`.
- **Walk cross fall 1.5 %** (05 §5.7 says 2 %, PROWAG's maximum; 1.5 % is the usual design value and keeps the
  driveway crossings under the contract's 2 %).
- **No `Pole` intervals yet**: signs and lamp posts are R3's props; the stop-line zone covers each STOP sign.
- **Verges and shoulders as plates**: not in 05 §5.10's list, but without them the 1 m rule would depend on W's blend
  curve; with them it holds by construction.
- **Driveway aprons are R's plates** (the cutter knows the flares): W's bake runs R's road plates after S has asked for
  its cuts (`RoadPlateBuilder.Build(..., cuts, cutter)`).
- **Untagged service roads** (`Yard`) are gravel: no sink, deformable. A `surface=asphalt` tag makes one paved.

## Next

- **Lane S (sites-S1)**: switch from `ProvisionalKerbFrontage` to `KerbFrontageSource.LoadTown(town)`. Face ids are new
  (sorted by edge, side and position), and faces are real block faces (a road side between two corners).
- **Lane W (T3.2)**: `RoadPlateBuilder.Build(graph, surfaces, kerbs, dem, settings[, cuts, cutter])` then `Submit` to
  the plates API; all R plates are stage `Roads`. A grid check at W's 0.5 m spacing belongs to W's applier tests.
- **R3** (meshes): kerb extrusion along the loops, walks from the `KerbPiece` partition (its `surfaceY`), the warning
  strips along the lowered stretches; sign and lamp props add `Pole` intervals.
- **R4**: the two close junction pairs (R1's 4 fallbacks) still have overlapping ribbons and a 15 m no-driveway zone.
- **The 18 clearance faces**: mapped buildings closer to the kerb than the walk; nothing to decide unless the map is
  wrong there (the report lists each).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.BakeTowns
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.Kerbs
```

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'R'; tools\ci\gate.ps1 -Tier merge` (lane R paths only):
**pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-0828\gate-merge`): oracle.hash; vendor.check (584 files, 0
differences); native.ctest; unity.editmode 186 passed, 1 skipped (ServerContents: no server build), 0 failed;
unity.playmode 9 of 9; build.servercompile; build.dev (226 MiB); autotest.smoke. (The native step rebuilds
`EngineSimPlugin.dll` in the worktree; it was restored, it is lane V's and committed only at milestone tags.)
