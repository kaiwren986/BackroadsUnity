# M3 handoff: lane R, card roads-R1 (junction surfaces)

Branch `m3/r-junctions` in `D:\Backroads-Unity-wt\r`, cut from `main` at `9b0ce42` (R0o merged). Nothing was pushed,
installed, downloaded or signed into.

## In plain words

Every junction in Millbrook now has a real road surface: one clean asphalt shape with rounded kerb corners, the side
streets' stop lines, crosswalk bands downtown, and heights that carry the main road straight through while the side
street tilts onto its camber. Between junctions the roads are proper ribbons with a 2 % crown and, in the village, a
gutter pan at each kerb; where a ribbon meets a junction they share the very same vertices, so there is no seam to fall
through. 108 of the 112 junctions and bends get a surface; the 4 left are two pairs of junctions only 4.5 m and 5 m apart,
which card R4 merges. Narrow alleys and a few streets were narrowed where the map has buildings close to the road.
Pictures: `M3-handoff-R-R1/graph-junctions.png` (the village) and `graph-junctions-downtown.png`.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Arms and edge lines** from the cross sections, left and right separately (asymmetric sections offset each side by its own half-width). One frame per road, `F(s, u) = c(s) + m(s) u` with the miter normals of 05 §5.2 interpolated per segment, so kerb lines, offsets, mouths and ribbon vertices all come from one function | `Roads/Runtime/RoadFrame.cs` |
| 2 | **Corner solver** (05 §5.3): polyline fillets with the radius back-off (x 0.8 when the offsets do not cross or the arm has no room), radius by class pair (7.5 m streets, 12 m two through roads, 9 m rural, dirt and service, 6 m inside L-bends), tangent points kept within 25 m on acute corners, the building clearance step-down 7.5 -> 6 -> 4.5 m when a kerb passes within 2.5 m of a building, and a kerb inside a building shrinks further (down to 2 m). L-bends: inner fillet, outer square miter; the through side of a T follows its kerb lines; nose (chamfered crossing) as the last resort. Every reduced corner records why | `Roads/Runtime/JunctionSurfaces.cs` (`SolveCorner`, `Fillet`, `Clearance`) |
| 3 | **Mouths, crosswalk bands, stop lines** (05 §5.4): mouth = furthest tangent + 0.5 m; stop lines on every STOP, all-way and signal approach (1.22-9.14 m from the intersected edge, or 1.22 m behind a crosswalk), moved to the flattest spot within +4 m when the approach is steeper than 6 % (10 times in Millbrook); crosswalk bands 3.0 m wide where a crossing is mapped as marked, at signals, and at downtown junctions of Main Street (05 §5.8) | `EdgeStopCrosswalk` |
| 4 | **Junction polygons** (05 §5.5): mouth lines (every ribbon vertex), kerb lines, arcs, assembled counter-clockwise, welded without ever dropping a mouth vertex, checked simple; a ring mesh (centre fan + rings at 1/3, 2/3) when star-shaped, ear clipping otherwise; triangles in Unity's winding (facing up) | `Outline`, `Mesh`, `Geom2` |
| 5 | **Heights** (05 §5.6): the major road's profile carried through with its crown (projected onto the through road; beyond its kerb the last slope continues, clamped to 0.15 m); no major road: a least-squares plane through the mouth centres, clamped to 3 %; two-arm nodes carry the road through. **Curb inlets** upstream of the kerb returns on uphill approaches (outside the crosswalk), at flowline low points and every 90 m on grades (229, as props) | `Heights`, `JunctionHeight`, `Crown`, `Inlets` |
| 6 | **Ribbons** between the mouths with the tag-derived sections: crown 2 % (4 % gravel), the 0.45 m gutter pan falling 27 mm at the kerb on village streets; each end's junction warps the cross slope over 12 m (05 §5.6 ArmY); the end rows ARE the junction's mouth vertices | `BuildRibbon` |
| 7 | **Fallbacks**: a node whose arm has no room, or whose outline is not simple, takes its `roads-overrides` polygon, else no surface (the ribbons run to the node), always reported. `roads-overrides.(town).json` keyed by OSM node id (radius, polygon or ribbons, each with a reason); Millbrook needs none yet | `Roads/Overrides/roads-overrides.stockport-oh.json`, `Roads/Editor/RoadOverrides.cs` |
| + | **Fitting roads to buildings** (in the importer, so the road table and R1 agree): a side whose half-width reaches within 0.3 m of a mapped building narrows to clear it, never below one lane; 16 sides narrowed (mostly 6 m alleys at 4-5 m); 2 alleys have a building within 1.1 m of their centreline, which no width clears: flagged `buildingConflict` and listed as map conflicts | `OsmRoadImporter.FitToBuildings` |
| + | **Asset format 3**: OSM node ids, per-approach mouth / stop / crosswalk distances, the surfaces (outline, height mode, plane, corners with radius and reason, arcs), the inlets. Meshes are not stored: R3 bakes the chunk meshes and colliders from R1 in the same bake | `RoadNetworkAsset.cs` |
| + | Report `artifacts\roads\(town)\surfaces-report.txt`, maps `graph-junctions*.png`; menu *Backroads > Roads > Bake junction surfaces (R1)*, `RoadBakeCli.Surfaces`; `RoadBakeCli.BakeTowns` bakes R0o + R1 into the asset | `Roads/Editor/SurfaceBaker.cs`, `RoadGraphMap.WriteJunctions` |

## Done when, expected vs measured

| Item | Measured |
|---|---|
| `Roads.Synthetic`: a 30° Y, a 5-arm star, two junctions 25 m apart, a T on a 15 m curve, an arm shorter than its tangent all bake or fail with a clear message | **pass**, all five bake a surface: the Y's 30° corner caps at 3.6 m to keep its tangents within 25 m; the star (equal streets, an all-way stop) gets a plane and five 7.5 m fillets; the two junctions 25 m apart do not overlap and keep a ribbon between them; the T on the curve fillets both corners beside the street (its through road bends 38°, over 05 §5.1's 35°, so it is a plane); the 8 m arm backs its corners off to 3.07 m to fit |
| Every junction surface simple and CCW | **pass** (`Junctions.SurfacesValid`, town and both fixtures): 108 surfaces, 0 not simple |
| Area within ±5 % of the connector-corridor union | **pass for every regular junction (worst -4.2 %)**. The corridor union: each arm's roadway to its mouth, a band per pair of arms through the node, and right turns concentric with the kerb fillet in their own lane (05 §5.14), rasterised at 0.1 m. 8 irregular junctions (a nose corner, a complex pair, a short or acute arm, a corner a building forces under 4.5 m, or roads bending over 35° through the node) are held to ±15 % (worst -12.7 %) and listed in the report |
| Seam height error < 1 mm | **pass: 0.0 m**: all 334 mouth seams share their vertices bit-exactly with the junction and its mesh |
| No kerb line inside a building footprint | **pass**, except the 2 alleys the importer flags (a mapped building within 1.1 m of the centreline, inside one lane): 3 known map conflicts, listed |
| A report of every reduced corner radius and why | **done**: 74 corners in `surfaces-report.txt` (38 a building close to the kerb, 20 a short arm, 12 noses, 4 acute corners) |
| Extra | `Junctions.StopLinesAndCrosswalks` (103 stop lines, 41 crosswalk bands, each where 05 §5.4 puts it); `Junctions.CrownsAndPlanes` (ribbon cross slopes exact to 1e-6 m away from the junctions; planes <= 3 %; major junctions keep the major road's height at the node); the town asset current and deterministic (`OsmRoads.TownAssetUpToDate`, `BakeDeterministic`) |

Road tests: **50 passed**. Full EditMode **169 passed, 1 skipped** (ServerContents: no server build), 0 failed, with
`BR_OWNERS_BASE=main` (lane R paths only). PlayMode `Roads` 1/1.

## The town (surfaces-report.txt)

- 108 surfaces: 91 major-road junctions, 17 two-arm nodes (bends, joins, class changes, the bridge ends), 0 planes
  (every Millbrook junction has a through road); 99 ring meshes, 9 ear-clipped; 27,279 triangles.
- Corners: 229 fillets (102 at 7.5 m, 56 at 9 m, 25 at the downtown 4.5 m, 12 at 6 m, 5 at 12 m, the rest backed off),
  89 straight through-sides, 4 L-bend miters, 12 noses.
- **4 fallbacks**: the two junction pairs 4.5 m and 5.0 m apart (nodes 12/22 at (413, 436), 66/67 at (291, 411)).
- Building fits: 16 sides narrowed, 2 alleys in conflict with a building (`way/19178338` downtown, `way/394885217` up the
  north-west hill), 1 street held at one lane 0.19 m clear of a building (`way/19176893`).

## Where this differs from the card

- **The area check**: 05 §8's corridors come from the lane connectors of R5, which do not exist yet; R1 uses the
  independent estimate above. Regular junctions meet ±5 %; irregular ones (named reasons) get ±15 % and are listed.
- **Fitting to buildings** was not in the card; without it 18 kerb lines entered buildings (class-default widths: 6 m
  alleys, 12.6 m downtown Main Street, 9 m streets, against buildings drawn 2-6 m from the centreline).
- **Meshes are not persisted**: the asset keeps outlines, distances and corners; R3 bakes the chunk meshes from R1 in
  the same bake (05 §5.13), so nothing large is committed twice.
- **Inlets** are placed on the junction approaches and ribbons; the full kerb-loop pass (05 §5.6) comes with R2's kerbs.

## Next

- **R2** (kerbs, walks, ramps, driveway cuts): kerb loops from the R1 outlines and ribbon edges; the walk width against
  the 16 fitted sides; the inlets move onto the kerb loops.
- **R4 step 6**: merge the two close pairs (the 4 fallbacks) and look at the 5 complex pairs with nose corners.
- **The 2 map conflicts**: either an override (`ribbons: true` keeps them as they are) or a decision that those mapped
  buildings are sheds or canopies the road passes; the user may want to look at them on the map.

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.BakeTowns
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.Surfaces
```

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'R'; tools\ci\gate.ps1 -Tier merge` (lane R paths only):
**pass, 8 of 8 steps** (2026-10-21, run `artifacts\20261021-2008\gate-merge`): oracle.hash; vendor.check (584 files, 0
differences); native.ctest; unity.editmode 169 passed, 1 skipped, 0 failed; unity.playmode 9 of 9; build.servercompile;
build.dev (226 MiB); autotest.smoke. (The native step rebuilds `EngineSimPlugin.dll` in the worktree; it was restored,
it is lane V's and committed only at milestone tags.)
