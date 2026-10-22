# M3 handoff: lane R, card roads-R4 (roundabouts, bridges, aprons, class changes, alleys, close pairs)

Branch `m3/r-roundabouts` in `D:\Backroads-Unity-wt\r`, cut from `main` at `917d42f` (R3 and U33 merged). Two commits:
`0a7b674` (close pairs, alleys, aprons, kerb tapers) and `3901af2` (bridges, roundabouts, the R4 report). Nothing was
pushed, installed, downloaded or signed into.

## In plain words

The river bridge is now a real bridge: one 226 m concrete deck with parapets on both sides, four piers in the river
and an abutment on each bank, and the ground under it is left alone. Gravel roads that meet a paved road get 12 m of
asphalt at the end and a STOP sign with a painted stop bar. Alleys meet the street through a dropped kerb with tight
3 m corners, with no STOP sign. Where the town's kerbs end at a country road, the kerb slopes down to nothing over
3 m instead of stopping as a step. Two pairs of junctions only 5 m apart became one junction each, so the 4 places
where road surfaces overlapped are gone. Stockport has no roundabout, so none was added; the roundabout builder is
there for towns that have one and is shown on a test map. Pictures in `M3-handoff-R-R4/`: the bridge from the bank
(`r4-00`), along the deck (`r4-01`) and at the abutment (`r4-02`), an alley mouth (`r4-03`), a gravel road's apron
and STOP (`r4-04`), a kerb taper at the town edge (`r4-05`), and the test roundabout from above, from an approach
and at a splitter island (`roundabout-*.png`). The full check list is `M3-handoff-R-R4/r4-report.txt`.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Roundabouts from the map.** A closed ring of `junction=roundabout` (or `circular`) ways becomes one node at its centroid; every way that touched the ring is a leg and runs straight to the centre from its last point outside the ring radius + 8 m; the ring ways leave the import with a reason. A ring that does not close stays as roads (reported). `highway=mini_roundabout` nodes become mini-roundabouts (ICD from `diameter`, else 18 m, clamped to 14-27 m). `RoundaboutBuilder` (05 §5.11): ICD = 2 × (mapped ring radius + 2.75 m); circulatory roadway 5.5 m; truck apron 3 m (narrowed by a small ring); a ring under 26 m ICD is a mini (fully mountable 0.10 m dome, no raised splitters); legs flare by 1.25 m each side so a 2.5 m raised splitter fits without narrowing the lanes; splitters 15 m long with a rounded nose at the yield line and a 1.8 m cut-through at the crosswalk; entry kerb curves 15 m and exit 20 m tangent to the inscribed circle (cut down and reported when legs are close); every entry `RoundaboutYield` (no stop line); yield-line shark teeth (0.6 m base, 0.9 m high) across each entry; the ring's outer edge line solid beside the splitter, wide dotted across the entry, none across the exit; crosswalks one car length (6 m) behind the yield line on legs with walks, with perpendicular kerb ramps there; YIELD signs right of each entry and on its splitter; island plates (grass island at kerb height, concrete apron and splitters). The surface: the plane through the mouths, the ring falling 2 % outward. Legs under 70° apart: a reported exception. In the road network (format 5: `roundabouts`) for R5's lanes | `Roads/Runtime/OsmRoadImporter.cs` (`CollapseRoundabouts`, `MiniRoundabouts`), `RoadGraphBuilder.MarkRoundabouts`, `Roads/Runtime/RoundaboutBuilder.cs`, `JunctionSurfaces.cs` (`RoundaboutNode`), `Roads/Editor/RoundaboutMeshBuilder.cs`, `RoadPropBuilder.RoundaboutYields` |
| 2 | **Bridges.** Each chain of bridge edges is one deck (the river bridge was three graph edges: a zone change mid-span splits the road, not the bridge). R0o's deck profile (straight between the abutment heights) is kept. Abutments run from each end until the ground falls 0.5 m clear of the 1.0 m deep structure; wall piers split the free span into spans of at most 45 m; our own concrete edge strip, 0.85 m parapet, fascia and soffit follow the pavement edges (the width tapers where the road's section changes on the deck); abutment front walls and wing walls are concrete meshes. **Abutment plates** hold the fill at road level under each abutment zone (and its wing walls); under the free span the DEM is untouched (checked: no plate of the bridge's own under it). Deck footprints, abutment zones, piers and clearance are in the road network (format 5: `decks`, with `over` from `roads.json` `bridgeSpans`) for lane W | `Roads/Runtime/BridgeBuilder.cs`, `Roads/Editor/BridgeMeshBuilder.cs`, `RoadPlateBuilder.Abutments` |
| 3 | **Unpaved meets paved** (05 §5.12): every gravel or dirt approach (not an alley) at a junction with a paved road stops (`MinorStop`, even on the straight pair); its first 12 m from the mouth are paved (half the road where it is shorter), meshed as asphalt with the gravel end feathered, on asphalt plates; the stop line is painted there. R1's 9 m rural corner radius and its steep-approach rule (the stop line moves to the flattest spot within 4 m) apply as before | `RoadGraphBuilder.ArmControlsFromRules`, `SurfaceBuilder.Aprons` / `PavedAt`, `RoadMeshBuilder.Ribbons`, `RoadPlateBuilder.Ribbons` |
| 4 | **Class changes** at the kerb-zone edge: the kerb tapers to flush over 3 m wherever a kerbed face meets an unkerbed one on its block loop (town edge, rural corners, alley mouths), so the walk ramps down to the shoulder at 5 %; the gutter pan runs out (the through surface carries the unkerbed crown; the kerbed ribbon warps onto it over 12 m); edge lines start and the centre line follows R3's rural rules from the class change | `KerbBuilder.Tapers` / `KerbHeight`, `SurfaceBuilder.JunctionHeight` |
| 5 | **Alleys** (`service=alley`): 3 m kerb returns, the whole return a dropped kerb (25 mm, flaring back over 1.83 m), no detectable warning, no STOP or YIELD by rule (mapped signs stand), no marked crosswalk across the mouth. Service roads that meet only each other are uncontrolled (an alley crossing had become an all-way stop). Driveways and parking aisles were already handed to the sites program as mapped driveways by R0o | `RoadGraphBuilder`, `JunctionSurfaces.BaseRadius` / `MarkedCrosswalk`, `KerbBuilder.Ramp` |
| 6 | **Close junction pairs**: junctions joined by a way under 8 m merge into one at import (2 pairs: 4.5 m and 5.0 m); the other 14 pairs under 30 m stay two junctions with a surface each and the usual control rules (R0o's "no control until R4" is gone). Each pair's outcome is in `r4-report.txt` and the graph report. No `roads-overrides.json` entry was needed | `OsmRoadImporter.MergeCloseJunctions`, `RoadGraphBuilder.PickMajorsAndControls` |
| + | `RoadR4Report` (`artifacts/roads/(town)/r4-report.txt`, written by `BakeTowns` and `Meshes`); `RoadPreview.ShotsR4` renders the R4 views and the demo roundabout (`RoundaboutDemo`) | `Roads/Editor/RoadR4Report.cs`, `Roads/Client/Editor/RoadPreviewR4.cs`, `Roads/Editor/RoundaboutDemo.cs` |

Versions: importer 2, graph builder 3, surfaces 2, kerbs 2, plates 2, meshes 2, bridges 1; road network format 5.

## Done when, expected vs measured

| Item | Measured |
|---|---|
| Every mapped roundabout and mini-roundabout bakes, legs ≥ 70° apart or a reported exception | **pass**: Stockport maps none (no `junction=roundabout` way, no `mini_roundabout` node), and none is invented (`RoadsR4.TownHasNoInventedRoundabout`). On synthetic maps (`RoadsR4`): a closed ring of radius 13.25 m with four legs bakes to ICD 31.8 m, apron 3 m, four raised splitters in two pieces, four crosswalks, four yield lines, eight YIELD signs, no stop line, ramps at the crosswalks, island plates; a mini node bakes to an 18 m mini with a dome; a 21.5 m ring becomes a mini; legs 50° apart are reported (radii cut, outline still simple); a ring cut open stays roads |
| Every bridge has a deck, rails and abutments; the DEM under it is untouched outside the abutment plates | **pass** (`RoadsR4.RiverBridgeIsOneDeck`, `TownChecksPass`): one deck over the river, 225.9 m (edges 159, 158, 157), abutments 10.5 m and 8.5 m, free span 206.9 m, 4 piers (spans 41.4 m), soffit at least 0.53 m over the ground (at the west abutment; about 6-8 m over the water), parapets collide, no plate of its own under the free span (a riverside road passes under and keeps its plates). The 39 m stream bridge is outside the play area |
| Every unpaved-paved meeting has an apron and a STOP | **pass**: 7 meetings, 7 paved aprons (12 m; 10.1 m and 5.9 m on two short roads), 7 STOP signs, 7 stop lines on the aprons |
| The alley count of the town matches the map | **pass**: 9 alleys in the play area, 9 in the graph, 0 skipped; 54 dropped-kerb alley mouths; no STOP by rule (two alley approaches keep their mapped STOP signs) |
| Close pairs resolved one by one with the reason | **pass**: 2 merged, 14 kept as two junctions, every one with a surface; R1 fallbacks 4 → 0 |
| Extra | kerb tapers: 76, every one flush at its end (15 at the town edge); controls: 92 STOP approaches (37 by rule, 55 mapped), 13 fewer by rule than in R3, mostly alley arms; LOD0 140,369 triangles (33,467 per km², budget 60,000) |

Road tests: **86 passed** (8 new in `RoadsR4`). Full EditMode **205 passed, 1 skipped**, 0 failed, with
`BR_OWNERS_BASE=main` (lane R paths only). PlayMode 9 of 9.

## Waiting on you

Nothing new. The street-name list (U33) is still waiting for your approval.

## Where this differs from the card

- **Rails**: the parapet is our own concrete mesh; the Synty `SM_Env_Bridge_Edge_01` / `SM_Env_Bridge_Wall_01` were not
  used (their straight modules do not follow a sloping, curved deck whose width changes).
- **Abutment wing walls** are concrete meshes over terrain plates (the plates hold the fill, the walls hide the steep
  edge of the fill); no walk on the bridge (R2 builds none on structures).
- **Deck profile**: R0o's straight line between the abutments; no vertical curve.
- **Lane W** still has to drop the ground's collision under the deck where the river runs: the footprint is in the
  road network (`decks[].outline`, `over`).
- **Roundabouts**: no AI lanes yet (R5); the truck apron is plain concrete (no red brick: the road shader has no colour
  channel for it); a mini's painted splitters are not drawn; the town has none, so the builder is proven on synthetic
  maps only.
- **Double yellow → broken at the class change** comes from R3's per-edge rules (passing sight distance on the rural
  side), not from a rule at the node.
- **R3's one sign exception** (node 23: a STOP 20.7 m from the edge, 4 ft before a far crosswalk on a long corner) is
  not a close pair and stays as MUTCD has it.

## Next

- **Lane W**: the bridge deck footprints (drop the ground collision under the deck over the river); the abutment and
  island plates are in `RoadPlateBuilder.Build` like the others.
- **R5**: the lane graph, including the roundabout ring lane and entry/exit connectors (the plan is in
  `RoadNetworkAsset.roundabouts`).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.BakeTowns
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.Meshes
Unity.exe -batchmode -projectPath Game -executeMethod Backroads.Roads.Client.Editor.RoadPreview.ShotsR4   (no -nographics)
```

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'R'; tools\ci\gate.ps1 -Tier merge` (lane R paths only):
**pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1016\gate-merge`, on `3901af2`): oracle.hash; vendor.check
(584 files, 0 differences); native.ctest; unity.editmode 205 passed, 1 skipped (ServerContents: no server build), 0
failed; unity.playmode 9 of 9; build.servercompile; build.dev (231 MiB); autotest.smoke. The native step's
`EngineSimPlugin.dll` was restored afterwards (lane V's, committed only at milestone tags).
