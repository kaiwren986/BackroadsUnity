# M3 handoff: lane R, card roads-R0o (the OSM road graph on the real ground)

Branch `m3/r-osm-graph` in `D:\Backroads-Unity-wt\r`, cut from `main` at `5d8ab2c` (the integrator's R3 street-name fix
and the ownership entry for this handoff). Nothing was pushed, installed or signed into; `TownData` and `CanonData` were
only read; `D:\Backroads` was read only through `git show` (makeRoad's smoothing recipe).

## In plain words

Millbrook's real streets are now one clean road map on the real ground. The bake reads the town's OpenStreetMap roads and
the 1 m lidar heights, joins the map's pieces into streets, finds every junction (95: 68 T and 27 X), lays every road on
the ground the way the real road sits (on average 8 cm from the lidar ground, the 95th percentile 28 cm), and puts every
mapped stop sign (all 55) on the approach it faces; 05's rules fill the 50 approaches the map leaves unsigned. The state
route becomes Main Street inside the village (12.6 m with parking downtown, 7.8 m elsewhere), streets are 9 m in the
village and 6.2 m outside it, and roads stop 50 m inside the play-area edge. Nothing you drive on changes yet: the junction
surfaces, kerbs and paint come with R1-R3. Pictures: `M3-handoff-R-R0o/graph-village.png` and `graph-overview.png`.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **`OsmRoadImporter`**: class from `highway` (trunk..tertiary and links -> Highway, residential/unclassified/living_street -> Street, `service` -> Yard, track or an unpaved `surface` -> Dirt); footways, paths, steps, site driveways, parking aisles, drive-throughs and service ways inside mapped parking lots skipped with a reason. Ways split at every shared OSM node, then chained through two-way nodes whose pieces carry the same class and tags (one-way and left/right tags follow the travel direction); sharp corners (>= 45 degrees over +-6 m, 12 m from the ends) become L-bend nodes; centrelines resampled to <= 2 m keeping every OSM vertex | `Roads/Runtime/OsmRoadImporter.cs`, `HeightField.cs` |
| 2 | **Cross sections from tags**: `width`, then `lanes` x lane width (+ 2.4 m per parking side from `parking:*`), then class defaults (Highway 7.2, MainStreet 6.6 + 2 x 0.6 gutters + parking: both sides downtown unless tagged, Street 9.0 in the kerb zone / 6.2 rural, Dirt 5.4, Yard 6.0), left and right separately; one-way, bridge, layer, tunnel, roundabout, surface, sidewalk and parking tags kept on the road; speeds from `maxspeed`, else 55 mph highways, 25 mph in town, 10 mph yards | `RoadGraphInput.CrossSection`, `Road` |
| 3 | **Heights from the DEM** along *strokes* (a road carried through its junctions by class, then straightness), highest rank first: box smoothing r 12 m x 4 passes (exact integral, ends pinned), grade limits 11 % paved / 15 % dirt with the real road's own grade + 1 point as the floor, bridge decks straight between the abutments, and every junction at the height of its highest-ranked road, the others warped onto it over 12 m (05 §5.6) | `OsmRoadImporter.Heights` |
| 4 | R0's cleanup reused unchanged (clustering, joins, L-bends, dead ends, complex pairs, major road by class first). Millbrook-specific parts stay behind `RoadGraphCanon` (legacy node numbers only when the input has them; the canon hash only for the canon). The town zone is the manifest's `KERB_ZONE` polygon, not a street hull | `RoadGraphBuilder.cs`, `RoadGeometry.cs` (`TownZone.FromPolygon`) |
| 5 | **Controls from OSM nodes**: `highway=stop` (with `direction`, or `stop=all`), `give_way`, `traffic_signals`, `crossing=*`. Each sign controls the approach it faces at the first junction within 60 m in its direction of travel; crossings join the approach of a junction within 30 m. Mapped signs pick the through road (reported when that disagrees with the class-first pick: 0 times in Millbrook); 05 §6.2 fills only what the map leaves open; every approach records its control and its source (`tag` or `rule`) | `RoadGraphBuilder.ApplyFeatures`, `ArmControlsFromRules` |
| 6 | **`RoadNetworkAsset` format 2**: the road table (OSM ways, tags, cross sections, zones, edge cuts), per-approach controls with source and sign, the mapped features, the downtown zone, the edge cuts, the mapped driveways for sites-S1, the steep stretches. Two committed assets: `BrRoadNetwork` (the canon test world; its graph is unchanged, the M2 tests pass) and `BrRoadNetwork_stockport-oh` (932 KB). Bake: menu *Backroads > Roads > Bake town road networks (R0o)* or `RoadBakeCli.BakeTowns [-verifyHash]`; report and maps in `artifacts\roads\stockport-oh\` | `RoadNetworkAsset.cs`, `Roads/Editor/RoadNetworkBaker.cs`, `TownRoadSource.cs`, `RoadGraphMap.cs` |
| 7 | **Play-area edge**: the graph is built on the extract, then every road is cut 50 m inside the play area (`EDGE_RULE`); the 11 cut ends carry `NodeFlags.PlayAreaEdge` and the report lists each with the distance to its next node beyond the cut. Groups of roads apart from the network stay when they reach the edge (3: they connect outside the play area) and are dropped when they are only service roads reached through a mapped driveway or path (2, 77 m: handed to the sites program) | `OsmRoadImporter.Clip`, `Groups` |
| + | **Provisional `IKerbFrontageSource`** (overview contract 2) so sites-S1 can start before R2: one face per side of every edge, the kerb the road's half-width from the centreline, walk band in the kerb zone, no driveways within the junction mouth + 10 m | `Roads/Runtime/ProvisionalKerbFrontage.cs` |

## Done when, expected vs measured

EditMode, category `Roads`, on this desktop, 2026-10-21. Fixtures: `Tests/EditMode/Roads/Fixtures/{village,west}` (600 x 600 m
clips of the town by `tools/world/roads/make-fixture.mjs`, ODbL and 3DEP credit in each), the whole town, and synthetic towns.

| Item | Measured |
|---|---|
| Every drivable way in the graph or skipped with a reason | **pass** (`EveryWayAccountedFor`, both fixtures and the town): town 160 ways = 68 in the graph + 31 site driveways/aisles + 52 footways and paths + 7 outside the play area + 2 dropped service fragments |
| Node kinds and arm counts match a hand-checked table | **pass** (`NodesMatchTheTable`): 49 junctions in the village fixture, 18 junctions and 2 dead ends in the west fixture, each by OSM node id, with no extra junction. The table comes from the raw OSM topology by the fixture tool (`--table`), independently of the C# importer, and was checked against the fixture maps |
| The graph is connected | **pass** (`Connected`): one network; every other group reaches the play-area edge. `MajorProblems` and `ControlProblems` empty |
| Every tagged control lands on the right approach | **pass** (`ControlsOnTheirApproach`): 55 of 55 town stops (32 village, 11 west) placed; each checked geometrically (junction ahead in the sign's direction within 60 m, arm pointing back at the sign). Synthetic: stop forward/backward, `stop=all` -> all-way stop, `give_way` -> yield beside a rule stop on a gravel track, a signal on the junction node, a crossing near a junction (`SyntheticControlsFromTags`) |
| Heights within 0.05 m of the smoothed DEM; no paved grade over 11 % unless the real road is steeper (reported) | **pass** (`HeightsFollowTheSmoothedDem`): an independent box smoothing of the DEM along each edge agrees to **0.0000 m** at all 2,468 town samples more than 50 m from a junction and away from the bridge; 0 stretches over the limit and steeper than the real road; 14 steep stretches reported, steepest 21.8 % (the real road is 21.8 %). Near junctions 903 samples are warped so arms meet flush, and the grade limit then moves 41 more by up to 0.39 m |
| Deterministic | **pass** (`BakeDeterministic`, `TownAssetUpToDate`): two bakes give the same SHA-256; the committed asset is current. `RoadBakeCli.BakeTowns -verifyHash` checks the same in batchmode |
| Extra | `SyntheticCrossSectionsFromTags` (width, lanes + parking, downtown and plain MainStreet, highway, rural and town streets, Dirt, class changes and the downtown join, the edge cuts, a skipped footway, a mapped driveway); `SyntheticBridgeDeckIsStraight` (deck straight between bank-top abutments, approaches flush); `EdgeRule`; `ProvisionalFrontage` (370 faces, 264 kerbed, kerb at the half-width 370/370, street on the right 370/370); `TownMapsWrite` |

Full EditMode: **158 passed, 1 skipped** (ServerContents: no server build here), 0 failed, with `BR_OWNERS_BASE=main`
(OwnersRespected passes: only lane R paths). PlayMode `Roads`: 1/1. `gate.ps1 -Tier merge`: see "Merge" below.

## The town (the bake report, `artifacts\roads\stockport-oh\graph-report.txt`)

- 185 roads, 15.8 km: Highway 3.73 km, MainStreet 1.77 km, Street 6.66 km, service and alleys 3.64 km. 136 nodes: 95
  junctions (68 T, 27 X), 4 L-bends, 24 dead ends, 6 class changes, 7 joins (bridge ends, width changes); 11 edge cuts.
- Controls: 55 approaches stop from mapped signs, 50 by rule; no signals or yields mapped; 21 unmarked crossings.
- **16 complex pairs** (junctions under 30 m apart), for R4 step 6: mostly side streets meeting Main Street from north and
  south a few metres apart (offset T's), two only 4.5 m and 5.0 m apart (nodes 12/22 at (413, 436), 66/67 at (291, 411)).
- Steepest junction approaches: service roads on the north-west hill at up to 17.5 %, a street at 10.4 %.
- 20 mapped driveways and aisles meet a road: in the asset (`mappedDriveways`) for sites-S1.

## Where this differs from the card

- **Fixtures are two clips of Millbrook, not two towns.** A second town needs an Overpass download that is not approved
  (U23 covered one). The village and west clips differ a lot (grid and downtown; the edge, a state route turning into Main
  Street, steep yards), and synthetic towns cover the tags Millbrook does not use (width, lanes, parking, signals,
  give-way, all-way stops, bridges). A second real town can be added with `make-fixture.mjs` once a download is approved.
- **Smoothing radius**: the card's "box r 12 m" (metres of road). makeRoad used 12 *samples* (24 m at its 2 m spacing),
  which flattens real crests and dips on lidar.
- **Grade floor**: the real road's own grade is the smoothed DEM profile's, plus 1 point so a junction warp settles in
  metres instead of creeping along steep roads.
- **DEM**: read straight from `TownData/stockport-oh/v1/dem/dem_1m.f32` (lane W's T3.2 heightfield is not on main yet; it is
  the same file).

## Next

- **R1** (junction surfaces) on this branch or a new one from it: arms and edge lines from `Road.hwLeft/hwRight`, corners
  by class pair with the building clearance (the footprints are already in the input), heights from the stroke profile.
- **R4 step 6**: the 16 complex pairs (the report lists them). The two pairs under 5 m look like mapping artefacts that a
  merged junction polygon resolves.
- **T3.3 (lane W)**: when the new `Millbrook.unity` exists, `RoadsDevOverlay` and `RoadsDebugView` should load
  `RoadNetworkAsset.LoadTown(townId)` there (they load the canon asset today, which is right for `CanonTestWorld`).
- **sites-S1 (lane S)**: `new ProvisionalKerbFrontage(RoadNetworkAsset.LoadTown("stockport-oh"))` and the asset's
  `mappedDriveways`.
- **REQUEST FOR I**: `docs/desktop-setup.md` §2 clones engine-sim without its submodules, so `vendor-check.ps1` fails on a
  fresh desktop ("not a tree object"); the clone needs `--recurse-submodules` (or `git submodule update --init` in it).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.BakeTowns
node tools/world/roads/make-fixture.mjs --name village --cx 380 --cz 420 --check
node tools/world/roads/make-fixture.mjs --name west --cx -250 --cz 600 --check
```

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'R'; tools\ci\gate.ps1 -Tier merge` (lane R paths only):
**pass, 8 of 8 steps** (2026-10-21 on the desktop, run `artifacts\20261021-1923\gate-merge`): oracle.hash; vendor.check
(584 files, 0 differences); native.ctest; unity.editmode 158 passed, 1 skipped, 0 failed; unity.playmode 9 of 9;
build.servercompile; build.dev (225 MiB); autotest.smoke. The vendor check first failed on this desktop because the
engine-sim reference clone had no submodules; with the user's approval they were fetched
(`git submodule update --init --recursive` in `D:\Backroads-ref\engine-sim`), see "REQUEST FOR I" above.
