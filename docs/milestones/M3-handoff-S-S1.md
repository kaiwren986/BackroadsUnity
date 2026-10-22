# M3 handoff: lane S, card sites-S1 (blocks and parcels)

Branch `m3/s-parcels` in `D:\Backroads-Unity-wt\s`, cut from `m3/s-parking-engine` at `8fb9dcf` (S2 done, not yet on
main). One code commit, `a11f063`, then this handoff. Nothing was pushed, installed, downloaded or signed into; TownData
and lane R's code and assets were only read.

## In plain words

Millbrook's land is now divided into plots. Every block between the streets (cut back to the back of the sidewalk)
is shared out among the buildings on it: each house, shop, the school and the fuel station gets one plot, drawn the
way real lots look, with lot lines that meet the street square and run straight between neighbours. Garages and sheds
sit on their house's plot, the fuel canopy on its shop's. The three mapped parking lots and the campground keep their
exact mapped outlines, attached to the building they serve or as plots of their own. Parks, ball fields and the
cemetery stay public land; frontage nobody claims stays vacant. In the countryside a block only reaches 120 m back from
the road, except round a building that stands farther back, which keeps its land and reaches the road by an access
easement (like a farm down a long drive). Each plot knows which stretches of which street it fronts, whether it is a
corner or alley plot and how deep it is, and the 21 driveways the map shows meeting a street became driveway requests
that lane R's cutter answers. Pictures in `M3-handoff-S-S1/`: `parcels-village.png` (the village; the coloured
lines on the sidewalk's back edge are each plot's frontage, dashed lines are easements, red ticks mapped driveways,
hatching mapped lots), `parcels-downtown.png` (closer), `parcels.png` (the whole play area); the full list is
`parcels-report.txt`.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Blocks**: roads-R2's block loops (the faces of the planar street graph: play area minus every road and junction surface), each outer loop with the holes inside it, minus the walk pieces (so a block ends at the walk's back edge; on unkerbed roads at the pavement edge), minus water (`water.json` areas), bounded at `RURAL_DEPTH` 120 m from the loop's frontage, plus 40 m round any building that stands beyond it; a loop that falls apart gives one block per piece, pieces under 25 m² dropped. The faces are R2's (built in memory with `KerbBaker.BuildTown`, checked against the published `KerbFrontageSource`: same ids, ways, lengths, walk back edges). 57 blocks, 3 of them cut at 120 m | `Sites/Editor/Parcels/BlockBuilder.cs` |
| 2 | **Fixed areas**: mapped `amenity=parking`, `amenity=fuel`, `landuse=farmyard|industrial|retail|commercial|quarry|harbour|port`, `leisure=marina`, `man_made=works|pier`, and campgrounds (`tourism=caravan_site|camp_site`, the town's one mapped yard) are kept exactly. Each serves the nearest non-house building whose outline is within 30 m, shared with a second within 6 m (research 08 §1.5). On its building's block it joins that building's parcel; elsewhere (across a street, no building near) it is a parcel of its own that names the building it serves. A building standing inside another's mapped area keeps its footprint + 3 m. Town: 4 fixed areas (3 lots, the campground), 2 served | `ParcelBuilder.BuildFixedAreas`, step 3 of `Build` |
| 3 | **Parcels**: a Voronoi diagram (`Delaunay2`, Bowyer-Watson, our own) of sites every 1.5 m along each block's edges and along every footprint. A footprint's sites belong to its building; an edge site to the nearest building within its type's reach (houses 45 m, shops 50, fuel and supermarkets 60, civic 80, industry 90, farms 200), weighted a little towards commercial and civic buildings; each building's parcel is the union of its sites' cells, so a lot line meets the street on the perpendicular bisector of two edge sites (square to the street, "surveyed") and runs halfway between buildings behind. Lot lines two parcels share are then straightened between their ends wherever both parcels stay simple, the line stays 1 m from both buildings and inside the two parcels (238 lines). Fuel canopies join the nearest shop within 35 m (08 §1.6). Public land (`leisure=park|pitch|playground|garden|recreation_ground|sports_centre|dog_park`, `landuse=cemetery|recreation_ground|village_green`) is cut out of parcels whose building stands outside it. Leftover pieces under 50 m² join the neighbour they share most edge with, larger ones are vacant land. Each parcel records its buildings (main first, then canopies and accessory buildings), its business type and form, its frontage (`FrontageRef`: face, s0..s1 along R2's arc length, street class and way; non-alley first, longest first), `corner` (two different streets meeting at 35° or more), `alley` (an alley face or a mapped alley within 3 m), depth (area / street frontage) and, with no frontage, an easement (the mapped service way that leaves it for a street, else the straight line to the nearest face) | `ParcelBuilder.cs`, `Geometry/Delaunay2.cs`, `Geometry/Clip.cs` (Clipper2 at 1 mm) |
| 4 | **Mapped driveways**: every `service=driveway|shared_driveway|parking_aisle|drive-through` or untagged service way that crosses a face's kerb line (or ends within 6 m of it) becomes a `DrivewayRequest` (`mapped = true`, its way) on the parcel it leads into: throat 3.6 m residential (4.5 shared), 7.3 m commercial (aisles, shops, lots), 4.5 m drive-through, 5 m field, 12 m truck, or the way's `width`; surface from its tag, else asphalt on kerbed streets and gravel elsewhere. Ways that are roads in R's graph, branch off other service ways, or lie outside the play area make none (listed in the report). Town: 21 requests from 52 service ways; R's `DrivewayCutter` bakes 18, merges 2 (two driveways on one 6.5 m face), rejects 1 (a mapped way deep in a corner return) | `ParcelBuilder.MappedDriveways` |
| 5 | **Debug view**: SVG plan maps (parcels by business type, vacant pale, public green, mapped lots hatched, buildings dark, frontage coloured on the back edge, easements dashed, requests red) of the play area, the village and downtown, and the report (by-type table, blocks, fixed areas, requests, every parcel); menu *Backroads > Sites > Build parcels (S1)*, batch `SitesBakeCli.Parcels` (builds twice and checks the hashes) | `Parcels/ParcelMap.cs`, `Parcels/SiteParcels.cs` |

Also new: `SiteTownData` (lane S's reader of TownData: buildings, mapped lots, yards, service ways, barriers, points,
land use, water, the manifest's zones) and the runtime types `Parcel`, `Block`, `FrontLine`, `FrontageRef`, `FixedArea`,
`SiteBuilding` (`Sites/Runtime/Parcels/ParcelTypes.cs`).

## Done when, expected vs measured

| Item | Measured (EditMode `Sites.Parcels`, 7 tests; 38 Sites tests in all) |
|---|---|
| Every building except accessory buildings has exactly one parcel | **pass**: 249 main buildings (248 parcels with a building; the fuel canopy shares its shop's), 17 accessory buildings on their house's parcel, no building on two parcels, every main building's centre on its own parcel |
| Parcels tile each block without gaps or overlaps above 0.1 m² | **pass**: largest gap 0.047 m², largest overlap 0.027 m² (Clipper rounds new crossings to 1 mm); every parcel one piece |
| Every parcel has ≥ 1 frontage face or an access easement | **pass**: 264 of 300 front a street, 36 reach one by an easement (flag lots, interior vacant land, the farmhouse beyond 120 m, the park's inner pieces) |
| Every mapped lot and yard is inside its parcel | **pass**: the 4 fixed areas, 0.000 m² outside their parcels (the part on blocks: 302 m² of one lot's mapped outline lies on the street and its walk, 636 m² of the campground's outside the play area; the campground keeps a house's footprint + 3 m) |
| Deterministic (two runs, same hash) | **pass**: `Parcels.Deterministic` and `SitesBakeCli.Parcels` build twice: hash `d65873a9…` both times |
| Extra | faces equal R2's published frontage; R's cutter answers every request; 123 corner and 48 alley parcels (the village grid's blocks are small, cut by alleys) |

By type (from the report): houses 221 parcels, median 1,125 m², 18 m deep; shops 19, median 2,767 m²; the school
7,950 m²; the fuel station 10,340 m² on a rural corner.

## Where this differs from the card

- **The split** is a Voronoi diagram of edge and footprint sites with type-based reach, not a weighted Voronoi of
  footprints with a separate snap step: edge sites give the perpendicular lines at the frontage by construction, and the
  straightening pass gives straight lot lines. "Houses get their measured frontage" is not imposed: the village's own
  spacing decides (the profile check is S5's `Sites.MatchesProfile`).
- **Public land** (parks, pitches, cemetery) and **campgrounds** are handled though the card does not list them.
- **Debug view**: SVG maps (vector, any zoom) and their PNG renders, not a CPU raster like R0's.
- **Blocks come from R2's kerb network baked in memory** (`KerbBaker.BuildTown`, the same code that writes the
  committed frontage asset): the loops and walk pieces are not in the `IKerbFrontageSource` contract. The faces are
  checked against the published asset in `Parcels.FacesAreTheR2Frontage`.
- **No baked asset yet**: parcels are built in memory (about 3 s) for S3; S5's bake writes `Sites/manifest.json`.
- **One driveway rejected by R** (`way/1271520106`, a mapped aisle deep in a corner return): S3 re-plans that lot.

## Next

- **sites-S3** (this lane, next): site plans per parcel: mapped lots first (`Parcel.fixedAreas`, the S2 packer inside
  them), then templates, then rules; S3 also sends its own driveway requests and re-plans against R's cuts.

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Sites
Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Sites.Editor.SitesBakeCli.Parcels
```

Report and maps: `artifacts\sites\(town)\parcels-report.txt`, `parcels*.svg`.

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'S'; tools\ci\gate.ps1 -Tier merge` (lane S paths only; the
diff against main holds S2 and S1): **pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1217\gate-merge`, on
`a11f063`): oracle.hash; vendor.check (584 files, 0 differences); native.ctest; unity.editmode 243 passed, 1 skipped
(ServerContents: no server build), 0 failed; unity.playmode 9 of 9; build.servercompile; build.dev (231 MiB);
autotest.smoke. The native step's `EngineSimPlugin.dll` was restored afterwards (lane V's).
