# ADR-007: World bake contracts (TownData v1 and Backroads.Core.WorldBake)

- **Status:** **draft** (card T3.0, 2026-10-20). Freezes with ADR-006 at the M3 tag, after lanes W, R, S and A have
  built against it. Until then a lane that needs a change asks the integrator; nobody edits these types on a lane branch.
- **Date:** 2026-10-20
- **Owners:** lane I (the contracts, `TownData` v1 and its tools until T3.0 merges; then `TownData\` and `tools\world\`
  go to lane W as `OWNERS.json` says). Changes need an update of this ADR and of the tests in the same commit
  (AGENTS.md rule 6).
- **Plan:** ADR-006 §2-§6 (sources, frame, stages W0-W8, names); the overview's contracts 1-5 and parameters
  (`docs\cards\M3\sites-roads-overview.md`); the site design §2.2, §4.1, §7a (`docs\design\site-layouts.md`); research
  05 §5 (kerbs, cuts, road plates), 08 §1 (the classifier).

## Context

M3 splits the new Millbrook's bake over five lanes that work in parallel: lane W grades the terrain and exports the
grid, lane R builds the street graph, junctions, kerbs and driveway cuts, lane S plans the lots, lane A models the
buildings. They meet at a handful of data shapes. This ADR fixes those shapes before the lanes start, so every lane
reads the same frozen source and codes against the same types.

## Decision

### 1. TownData v1 (stage W0, the overview's contract 1)

`TownData\<town id>\v1\` is written only by `tools\world\extract-town.mjs` from the cached downloads of
`tools\world\fetch-town.mjs` (`artifacts\world-src\<town id>\`, gitignored, pinned in `sources.json`). It is never
edited by hand; a re-extract with a new download is `v2`. Re-running the extract on the same cache gives the same bytes
(`extract-town.mjs --check`). The folder is a hashed tree (AGENTS.md rule 7): LF only, `-text`, the DEM through LFS.

| File | Content |
|---|---|
| `manifest.json` | format `backroads-towndata`, version; `townName` (the in-game name); `frame` (below, with six lat/lon check points from the JS projection); `playArea` and `extract` boxes; the OSM timestamp, endpoint and hash; the DEM pin; `strip` (the name rules, the dropped-key tally, the leak scan); the SHA-256 of every tool that wrote it; `zones` (kerb and downtown zones, section 5); `counts`; `files` (bytes and SHA-256 of every other file) |
| `roads.json` | `ways`: every `highway=*` way touching the extract square, `{osm, nodes, tags, part?, clipped?}`, cut to the nodes inside the square plus one beyond (no synthetic nodes, so OSM topology is kept); `rail` (railway ways, same form); `nodes`: `{id, x, z, y?, tags?, outside?}` (y only inside the square: the bare-earth DEM, not a bridge deck); `bridgeSpans` (bridge ways merged per crossing, over river / stream / other, length); `bridgeAreas` |
| `buildings.json` | `{osm, part?, poly (CCW), holes? (CW), area (outer ring, m²), c (centroid), y (DEM at c), inPlay, tags, type, business, sub?, inferred?, form?, lu, ctx, pois, street?, setback?}`: `type`/`sub`/`inferred`/`form` from the classifier (section 4); `lu`, `ctx`, `pois` are its inputs, so C# can re-run it; `street` and `setback` are the study's street frame (nearest street within 90 m, wall to the assumed road edge) |
| `sites.json` | `parking`, `parkingSpaces`, `fuel`, `yards` (farmyard, industrial, retail, commercial, quarry, harbour, railway land use; works, piers, silos; marinas; caravan sites), `areaHighway`, `service` (service ways with their node chains), `barriers`, `barrierNodes`, `entrances`, `points` (shops and amenities as points, with the `host` building they were attached to) |
| `landuse.json` | `areas` (land use, natural, leisure, cemetery, aeroway areas), `lines` (tree rows, hedges, cliffs, power lines, runways), `points` (trees, power towers, masts) |
| `water.json` | `areas` (water polygons; `river: true` on the river), `lines` (waterways; `river`, `lock` flags), `structures` (dams, weirs, locks, lock gates), `levels` (the DEM's y inside each river area: min, p05, p50, p95, max; the lidar is hydro-flattened, flat per reach) |
| `dem\dem_1m.f32`, `dem\dem.json` | float32 little-endian, no header, **y = elevation − Y0**, `n` x `n` samples at 1 m, `k = j*n + i`, `x = x0 + i`, `z = z0 + j` (rows **south to north**, columns west to east), `x0 = z0 = −extract half`; `dem.json` holds the layout, Y0, ranges, voids and the tile-seam check |
| `sources.json` | the download pins: the Overpass query, endpoint, date, bytes and SHA-256; every 3DEP request URL with its date, bytes and SHA-256; the 1 m lidar projects of the catalog |
| `credit.txt` | ODbL attribution and the USGS 3DEP credit |

Areas and lines are clipped to the extract square (Sutherland-Hodgman, Liang-Barsky; `clipped: true`). Buildings,
points and nodes are kept whole when their centroid or position lies in the square. Coordinates are rounded to the
millimetre, areas and lengths to the centimetre; keys are sorted; ids are typed OSM ids (`way/123`) except the node
references of `roads.json` (numbers).

### 2. The frame (`TownFrame`)

UTM on WGS84 in the town's zone (Krueger's series to third order in n, `tools\scout\geo.mjs` and `Utm` in C#; well under
a millimetre inside the zone), then **x = easting − originEasting** (east), **z = northing − originNorthing** (north),
**y = elevation − Y0** (up), metres. Unity position = (x, y, z). The plan is UTM grid metres (scale factor about
0.9997 here), not ground metres, so the OSM vectors and the DEM line up exactly.

- **Origin** = the centre of the play area: the scout's 2 km square centre, i.e. the town's OSM place node rounded to
  10 m in UTM plus `towns.json` `play2` (dx, dy).
- **Y0** = the lowest river point in the extract (the DEM inside the river polygons and along the river centreline)
  − 2 m, rounded to the millimetre.
- **Play area** ±1,024 m; **extract** = play area + 300 m on every side, ±1,324 m.
- The grid layers of `WorldData` keep CanonData v1's JS layout (x_js = −x); lane W flips once, at export (W8). No
  contract type here is in JS space.

### 3. Names (ADR-006 §6, the card's step 3)

Dropped at extraction from every tag set: `name`, `name:*`, `alt_name`, `old_name`, `brand*`, `operator`, `addr:*`,
`phone`, `website`, `ref` (kept only on highway ways when every part is a state route, `^(OH|SR)[ -]?\d+[A-Z]?$`, as a
class hint that is never shown), and every other key that carries a name or free text: any key containing `name`,
`brand`, `operator`, `owner`, `addr`, `phone`, `website`, `email`, `contact`, `wikidata` or `wikipedia`; `description`,
`note*`, `fixme`, `inscription`, `destination*`, `wiki*`, `image*`, `url`, `fax`, `source*`, `tiger:*`, `gnis:*`,
`nhd:*`, `is_in*`, `ref:*`, `old_ref`, `official_ref`, `artist`, `architect`, `subject*`. The extractor then scans
every file it wrote for every full name of the raw data and fails on a hit. `Town.NoRealNames` checks the same rules
on the committed files (and repeats the name scan when the raw download is cached).

### 4. Business type and form (the overview's contract 5)

One classifier: the rules of `tools\osm\measure.mjs typeOf` (first match wins, research 08 §1 step 4), applied by
`extract-town.mjs` through the study's own `buildTownFrom` (points attached within 12 m, land use at the centroid, the
fuel, marina, works and sawmill areas) and `lotAnalysis` (a street-front building with its own lot in front is
standalone). `Backroads.Core.WorldBake.BusinessClassifier` is its C# port; `Town.ClassifierFixture` keeps the two in step
on 200 shared cases (`Tests\EditMode\Town\Fixtures\classifier-fixture.json`; `tools\world\classifier-fixture.mjs --check`
runs the JS side) and on every building of the extract.

`BusinessType` (byte, append only): Unknown 0, House 1, Apartments 2, Accessory 3, Downtown 4, Retail 5, Office 6,
Restaurant 7, FastFood 8, Supermarket 9, Fuel 10, CarRepair 11, Motel 12, Civic 13, Church 14, School 15, Warehouse 16,
Industrial 17, Farm 18, Quarry 19, Mill 20, Harbor 21, Roof 22. The classifier's types map as: house, apartments,
accessory, office, restaurant, supermarket, motel, civic, church, school, warehouse, industrial, farm, quarry, mill,
harbor to their names; shop → Retail; fast_food → FastFood; car_repair → CarRepair; fuel_shop and fuel_canopy → Fuel;
roof (a free-standing roof or canopy away from fuel) → Roof. **Downtown** is not a classifier answer: it is the site plan
type for buildings whose `form` is `streetfront` (`BuildingForm`: None, Standalone, Streetfront).

### 5. Zones (the overview's `KERB_ZONE`, `DOWNTOWN_ZONE`)

Measured by the extractor and stored in `manifest.json` `zones` as polygons: the kerb zone is the village's built-up
hull + 20 m (no street carries `sidewalk=*`), the downtown zone is the hull of the street-front buildings + 30 m. Lane R
(R2) and lane S (S1) read them from the manifest instead of recomputing them.

### 6. The C# contracts (`Game\Assets\Backroads\Core\Runtime\WorldBakeContracts.cs`)

Namespace `Backroads.Core.WorldBake`. All plan positions are `double2` with `.x` = x (east) and `.y` = z (north);
polygons are CCW from above and not closed; heights are local y (metres above Y0); lengths and arc lengths in metres;
slopes as rise over run.

| Type | Fields | Who |
|---|---|---|
| `TownFrame` | `zone`, `northern`, `originEasting`, `originNorthing`, `y0`, `playHalf`, `extractHalf`; `ToLocal(lat, lon)`, `ToLatLon(local)`, `YOf(elevation)`, `ElevationOf(y)`, `ToUnity(plan, y)`, `InPlayArea`, `InExtract`; `Utm.Forward`/`Inverse` | everyone |
| `Plate` | `id`, `kind` (Road, Walk, Lot, Yard, Pad), `polygon`, `surface` (the CanonData v1 surface ids: Grass 0, Dirt 1, Sand 2, Rock 3, Road 4, Gravel 5, Concrete 6, Track 7, Mud 8), `heights` (Plane: `plane`, y = a·x + b·z + c; LeastSquares: `fitPoints`; PerVertex: `vertexY`), `maxSlope`, `blend`, `priority`, `noDeform` | R2, S4, A/W submit |
| `IPlateSink` | `Submit(Plate)` | W implements |
| `KerbFrontage` | `faceId`, `osmWay`, `kerb`, `s`, `topOfKerb`, `gutter`, `walkBack`, `walkBackY` (all at the same s), `kerbKind` (None, Barrier150, Mountable100, Dropped25), `streetClass` (Highway, MainStreet, Street, Dirt, Yard, Driveway = R's `RoadClass`), `speedKmh`, `oneway`, `forbidden` (`ForbiddenInterval {s0, s1, reason}`: CornerReturn, Crosswalk, Ramp, StopLine, Inlet, Pole, Other), `provisional` | R implements `IKerbFrontageSource` (`Provisional`, `Faces`, `Face(id)`); S reads |
| `DrivewayRequest` | `id`, `faceId`, `s0`, `s1`, `kind` (Residential, Commercial, Truck, FuelIn, FuelOut, Field), `flareLength` or `returnRadius`, `surface` (`SiteSurface`: Asphalt, Concrete, Gravel, Dirt, Pavers, Grass), `mapped`, `osmWay` | S asks |
| `DrivewayCut` | `requestId`, `outcome` (Baked, Moved, Merged, Rejected), `faceId`, `s0`, `s1`, `shift` (at most `CUT_SHIFT_MAX` = 3 m), `mergedWith`, `kerb` (Dropped25), `apron`, `crossSlope` (at most 0.02), `lotEdgeA/B`, `lotEdgeYA/YB` (where the lot plate must meet the cut), `rejectedBy`, `reason` | R implements `IDrivewayCutter.Resolve(requests)`: one cut per request, in request order, handled sorted by (faceId, s0, id) |
| `DoorSpec` | `building` (buildings.json `osm`), `part`, `kind` (Main, Service, Garage, Dock, Bay, DriveThroughWindow), `wallEdge` (edge from vertex i to i + 1 of the CCW footprint), `offset` (vertex to door centre), `width`, `sillY` (NaN when ungraded) | S writes, A reads |

A `KerbFrontage` face runs with its block on the left, so the street is on the right of the direction of increasing s.

### 7. The plate order

Lane W applies plates after every plate is in, sorted by `PlateOrder.Compare`: **stage** first (Roads: Road and Walk
plates of R2; then Sites: Lot and Yard plates of S4; then Buildings: Pad plates), then **priority** ascending (later
wins where plates overlap), then **id** (ordinal). Then the blend bands. Submission order never matters; the same
plates give the same bytes. A lot plate meets the sidewalk back edge height exactly at every driveway cut (the cut's
`lotEdgeY*`), and grades only inside its own polygon plus its blend band.

## Consequences

- Every M3 lane starts from the same committed extract; nothing downstream downloads anything.
- Lane W owns `TownData\` and `tools\world\` after T3.0 merges; a new extract is a new version folder and a new ADR-007
  revision only if the file shapes change.
- The classifier lives twice (JS for the extract, C# for the bake); the fixture test is what keeps them one.
- Gates: `Town.ManifestHashes`, `Town.NoRealNames` (M3 headline), `Town.ClassifierFixture`, `Town.FrameRoundTrip`.

## Open questions

1. Lane A may want a building pad height per footprint in `buildings.json` (today only `y` at the centroid); W8 could
   add it from the DEM instead. Default: W computes pads from the DEM.
2. `ref` on county and township roads is dropped; if lane R wants a road number as a class hint there too, it becomes
   a numeric-only tag in v2. Default: class from `highway` only.
