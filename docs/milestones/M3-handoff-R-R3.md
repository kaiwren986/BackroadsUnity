# M3 handoff: lane R, card roads-R3 (markings, controls, signs, chunks, LOD, colliders)

Branch `m3/r-markings` in `D:\Backroads-Unity-wt\r`, cut from `main` at `e1c06a7` (R2 merged). Nothing was pushed,
installed, downloaded or signed into. The user's POLYGON City pack was imported into this worktree with Unity's importer
(gitignored, as in the main checkout) to place and check the real sign prefabs.

## In plain words

Millbrook's roads are now real meshes: dark asphalt with yellow centre lines on the main road, white stop bars and
crosswalks, kerbs and concrete sidewalks with yellow warning strips at every lowered corner, and on the rural highway a
broken yellow line that turns solid where a hill or bend hides oncoming cars. Every stop has its STOP sign on the right
at the stop bar (your Synty sign), and street corners carry green name blades. The meshes come in 64 m pieces with a
simpler far version and a collider that makes the kerbs a real bump. Streets have names, but only placeholders
("Street 01" ...) until you approve the proposed list. Pictures: `M3-handoff-R-R3/r3-00-downtown-junction.png`,
`r3-01-downtown-above.png`, `r3-02-residential-corner.png`, `r3-03-rural-highway.png` (rendered in Unity with the
road plates on the bare DEM as ground; lane W's terrain and buildings come later).

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Markings** (05 §5.8, MUTCD 11th edition): double yellow on MainStreet; on rural highways a broken yellow line (3.05 m dashes every 12.2 m, phase-locked to s = 0) with **no-passing zones from passing sight distance** (Table 3B-1 by posted speed; eye and object 1.07 m; sight along the whole street through junctions, within the pavement + 2 m; zones at least 152 m, gaps under 122 m closed; one solid line on the side of each direction that may not pass); none on residential streets, one-way or one-lane roads. Centre lines end at the stop line, the crosswalk or the mouth, never on a junction surface. White edge lines 0.15 m in on rural highways. Stop lines 0.45 m from the centre line to 0.3 m short of the kerb on every paved STOP and signal approach. Crosswalks: continental 0.6 m bars downtown, two 0.3 m transverse lines elsewhere. Yellow detectable-warning strips 0.61 m deep along every lowered kerb (R2's ramps). Paint draped 4 mm over its surface; LOD1 keeps centre lines (one 0.4 m line) and stop bars (doubled) | `Roads/Editor/RoadMeshBuilder.cs` |
| 2 | **Controls** with their source: R0o already gives every approach its control, mapped signs first (55) and 05 §6.2's rules for the rest (50); R3 audits them: every mapped sign honoured or listed with its reason (none listed) | `RoadGraphBuilder` (R0o), `RoadMeshReport.Run` |
| 3 | **Signs and blades** (05 §6.3): one STOP, YIELD or signal head per controlled approach, near side right, at the stop line (moved up to 1 m off a ramp or inlet), 0.6 m behind the kerb (town) or 1.8 m from the pavement edge (rural), facing the traffic turned 5°; all-way plaques on the STOP post; street-name blades on two corners across from each other (on a STOP post within 3 m). Recorded in the road network (format 4: `props`). `RoadPropPlacer` puts them in a scene: STOP and YIELD are the Synty prefabs as they are (checked: they face +z), a signal is the Synty head on our own post; the street blades are ours (the Synty blade has "SYNTY AVE" printed on it) with the street's name as text | `Roads/Runtime/RoadPropBuilder.cs`, `Roads/Client/Editor/RoadPropPlacer.cs` |
| 3 | **Street names**: 32 streets (edges chained through bends and joins and straight across junctions, the through road first), numbered by their smallest OSM way id, named from our own list `Roads/Data/street-names.json` (80 invented names) **once approved**; until then "Street NN" placeholders. In the road network (format 4: `streets`, each edge's `street`) for the minimap and jobs. No route numbers shown | `Roads/Runtime/StreetNames.cs`, `Roads/Editor/StreetNameList.cs` |
| 4 | **Vertex data** (05 §5.9): UV0 world planar (x/4, z/4), UV1 lane space (lateral, s) on ribbons; colour R wear (flat until R5's lane graph), G wet (the gutter flowline, curb inlets), B patch id, A edge blend (0 at unkerbed pavement edges). Asphalt normals from the surface's own slope (smooth across junction fans, mouths and chunk borders). One shader `Backroads/Roads/Surface` (lit through lane W's `BrCommon`), three materials | `Roads/Client/Shaders/BrRoadSurface.shader`, `Roads/Client/Resources/BrRoad*.mat` |
| 5 | **Chunks, LOD, colliders** (05 §5.13): 189 chunks on the 64 m terrain grid, each primitive whole in the chunk of its centroid (ribbon pieces ≤ 32 m, whole junctions, kerb pieces ≤ 32 m); per chunk one vertex buffer, LOD0 and LOD1 index lists of three submeshes (Asphalt, Concrete, Paint) and the collider (asphalt and concrete, kerb faces included). Gravel and dirt roads are not meshed: the terrain's own surface (R2's plates) carries them. One gzip binary `BrRoadChunks_stockport-oh.roadbake.bytes` (4.5 MB, LFS) indexed by `BrRoadChunks_stockport-oh.asset`; `RoadChunkLoader.Spawn` builds the GameObjects (LODGroup at about 250 m / 600 m; colliders only on a server), `RoadChunkView.SpawnTown` adds the materials | `Roads/Editor/RoadChunkBaker.cs`, `Roads/Runtime/RoadChunkSet.cs`, `Roads/Client/RoadChunkView.cs` |
| + | `RoadBakeCli.BakeTowns` bakes R0o + R1 + R2 + R3 (network, frontage, chunks; `-verifyHash` checks all three); `RoadBakeCli.Meshes` in memory with `markings-report.txt`; `RoadPreview.Shots` / `Props` render the previews and audit the Synty props (batchmode with a graphics device) | `Roads/Editor/RoadNetworkBaker.cs`, `RoadMeshReport.cs`, `Roads/Client/Editor/RoadPreview.cs` |

## Done when, expected vs measured

| Item | Measured |
|---|---|
| Exactly one STOP per controlled approach, on its right, 0.6-2 m from the kerb face, ≤ 15.2 m from the intersected edge | **pass** (`RoadsR3.SignsAtEveryControlledApproach`, town and village): 105 approaches, 105 STOP signs, all on the approach's right, 0.6-1.8 m from the kerb face or pavement edge, at most 13.1 m from the edge **except 1** (node 24, Main Street downtown): R1 put that arm's crosswalk 16-19 m out on a sharp corner, and MUTCD's own rule (the sign 4 ft before a marked crosswalk) wins; listed, R4's complex-junction work revisits it. The all-way case (R1's five-arm star): 5 STOPs, 5 plaques on their posts, 5 stop lines |
| Stop lines only with STOP or signals; yield lines only with YIELD | **pass** (`RoadsR3.LinesWhereTheirSignsAre`): 78 stop lines, one on every paved STOP approach (the other 27 approaches are on gravel service roads, which take a sign but no paint, or at R1's fallback junctions); 0 yield lines (no roundabouts until R4) |
| Every tagged control honoured or listed with a reason | **pass**: 55 of 55 mapped STOP signs honoured, none listed |
| Centre lines never cross a junction surface | **pass**: 0 |
| ≤ 60k triangles per km² of town, deterministic | **pass** (`RoadsR3.ChunksWithinBudgetAndReadable`): LOD0 135,682 triangles (LOD1 102,876, colliders 96,954) = 32,349 per km² over the 4.19 km² play area; two bakes agree; `RoadBakeCli.BakeTowns -verifyHash` |
| Extra | `RoadsR3.PaintLiesOnItsSurface` (19,346 paint vertices 3-30 mm over the asphalt; 121 dashes phase-locked); `RoadsR3.OneNamePerStreet`; `RoadsR3.CommittedChunksCurrentAndLoadable` (every chunk decodes; triangle counts match); `RoadsR3.CollidersOnPavedPlates` (25,350 of 25,358 collider triangles over R2's paved plates, the surface ids agree; a ray from above hits the road at the junction surface's height with colliders loaded headless) |

Road tests: **78 passed** (11 new). Full EditMode **197 passed, 1 skipped**, 0 failed, with `BR_OWNERS_BASE=main` (lane R paths only). PlayMode 9 of 9.

## Waiting on you

- **Street names (a new approvals item):** the proposed list is `Game/Assets/Backroads/Roads/Data/street-names.json`
  (80 invented names, for example Millrace Street, Lockkeeper Street, Towpath Road, Bellwether Street, Copperbeck
  Lane). The real town's street names are not in the extract (stripped) and the raw download is not on this PC, so
  nothing checked them against Stockport's own names: please look before approving. Approving = set `"approved": true`
  with the decision id, re-bake; streets take the names in order. Route numbers stay hidden until decided.

## Where this differs from the card

- **Controls (step 2)** were already built in R0o; R3 verifies and reports them.
- **Signals**: Stockport has none; the code places a Synty head on our own post per approach (05 §6.3's near-side
  face; no mast arms yet). **YIELD and yield lines**: none in Stockport; yield lines come with R4's roundabouts.
- **Parking T-marks**: no `parking:*` tag in Stockport marks a lane, so none are drawn; **seams** (0.08 m tar strips)
  and lamp poles and speed-limit signs (05 §6.3's other furniture) are left for later.
- **Mounting heights**: the Synty STOP and YIELD come with their own posts (sign bottom about 1.5 m); they are placed
  as they are, not scaled to MUTCD's 2.1 m.
- **Gravel roads** (untagged service roads) are not meshed: the terrain draws them from R2's gravel plates and takes
  ruts there.
- **The 4 R1 fallback nodes** still have overlapping ribbons (z-fighting asphalt there) until R4.

## Next

- **Lane W (T3.3)**: `RoadChunkView.SpawnTown(town, parent)` in the Millbrook scene, `RoadPropPlacer.Place(network)`
  in its scene build (the Synty packs must be imported there, as in the main checkout).
- **R4**: roundabouts (yield lines, apron kerbs), the close junction pairs, node 24's far crosswalk.
- **R5**: the lane graph; its traffic volumes fill the wear channel (vertex colour R).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.BakeTowns
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.Meshes
Unity.exe -batchmode -projectPath Game -executeMethod Backroads.Roads.Client.Editor.RoadPreview.Shots   (no -nographics)
```

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'R'; tools\ci\gate.ps1 -Tier merge` (lane R paths only):
**pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-0910\gate-merge`): oracle.hash; vendor.check (584 files, 0
differences); native.ctest; unity.editmode 197 passed, 1 skipped (ServerContents: no server build), 0 failed;
unity.playmode 9 of 9; build.servercompile; build.dev (231 MiB); autotest.smoke. The first run failed
`Town.NoRealNames` (a tooltip of mine named the real town as an example); fixed. The native step's rebuilt
`EngineSimPlugin.dll` was restored (lane V's, committed only at milestone tags).
