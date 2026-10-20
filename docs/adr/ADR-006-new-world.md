# ADR-006: The new world: a real river town (TownData and WorldData)

- **Status:** **accepted** 2026-10-19 (U25 new world, U26 the town: **Stockport, Ohio**, U27 a **2 km square**;
  shortlist and maps in `docs\research\09-new-world-candidates.md`). Freezes at the M3 tag together with the first
  `WorldData\millbrook\v1` export.
- **Date:** 2026-10-19
- **Owners:** lane W (terrain, world data, the scene), lane R (road graph, junctions, lots), lane A (buildings),
  lane G (game places, jobs). Changes after the freeze need a new ADR and the integrator's merge.
- **Supersedes:** U15 (canon v1.1 road plates), U22 (canon v1.2), U8a-e (the old square and node questions), and the
  "Millbrook is canon" rule for the shipped Unity world. **Does not change** ADR-001: CanonData v1 stays frozen.
- **Plan:** PLAN §1.2, §1.8, §2.2, §6.1 M3-M9; research 05 (roads), 07 (art), 08 (sites, lane R research), 09 (towns).

## Context

Until now the Unity game rebuilt the browser game's generated Millbrook exactly (ADR-001). On 2026-10-19 you decided
(U25) that the shipped world should instead be a real small river town in rolling hills, renamed Millbrook: real
elevation from USGS 3DEP and real streets, lots and building footprints from OpenStreetMap, with every building new,
made in Blender as a real-life equivalent and styled to match your Synty City buildings.

Two things must not break on the way:

1. **The physics checks.** The golden traces, the deformation references and the probes in CanonData v1 measure the
   Unity vehicles and ground against the browser game. They only mean something on the browser game's own world.
2. **The engine room.** `WorldGrid`, `ColliderGrid`, the stamp kernels, `BrvState` and the drive tests all assume the
   v1 grid today (2,049 x 2,049 samples at 0.5 m, 1,024 m square, JS space).

## Decision

### 1. Two worlds

| World | Data | Scene | Used for |
|---|---|---|---|
| **Test world** (the old Millbrook) | `CanonData\v1`, frozen, unchanged (ADR-001) | `CanonTestWorld.unity` (today's `Millbrook.unity`, renamed) | Golden traces, deformation and probe references, `Vehicle.*` and `Deform.*` parity tests, the M2 route test. Never shipped in Client builds after M3 |
| **Millbrook** (the new, real town) | `TownData\stockport-oh\v1\` (the source extract) → `WorldData\millbrook\v1\` (the bake; both new, this ADR) | `Millbrook.unity` (new) | Everything you play, and every look, roads, traffic, jobs and net test from M3 on |

The test world keeps its name in code (`CanonData`), so nothing that reads it changes. The shipped name "Millbrook"
moves to the new world.

### 2. Sources, downloaded once and pinned

- **Elevation:** USGS 3D Elevation Program, the 1 m lidar DEM where the town has it (otherwise 1/3 arc-second, about
  10 m), public domain. Fetched through the 3DEP ImageServer, cached in `artifacts\world-src\stockport-oh\` (not in git),
  pinned by SHA-256 in `TownData\stockport-oh\v1\sources.json` with the request, date and resolution.
- **Map data:** an OpenStreetMap extract for the play area plus 300 m (one Overpass query), ODbL, pinned the same way
  with its timestamp. Re-fetching is a deliberate act: a new extract is a new `WorldData` version.
- **Not used:** Google Maps and any other source whose terms forbid copying (you asked; their terms say no), and
  aerial or street photos as textures. Our textures and buildings are our own.

### 3. Coordinates

- The town is projected from its UTM zone to a **local flat frame** with the origin at a chosen town-centre point:
  east and north in metres, elevation in metres above the lowest river point in the area, minus 2 m (so the river is a
  little above zero).
- `TownData` and the bake tools (R, S, A) work in Unity's frame: x east, z north, y up, metres.
- The grid layers in `WorldData` keep **CanonData v1's JS-space layout** (`x_js = -east`, `z = north`; Unity
  `= (-x, y, z)`), so `WorldGrid`, the stamp kernels and `BrvState` work unchanged on both worlds. W8 does the flip
  once, at export.

### 4. World size: 2 km square (U27)

`WorldGrid` gets its size from the world's manifest instead of compile-time constants (`N`, `VN`, `Half`, `TintN`;
`ColliderGrid.Cells` follows). The test world keeps 2,049² samples at 0.5 m over 1,024 m. The new Millbrook is
**4,097² samples at 0.5 m over 2,048 m**, all resident, with the village in the middle (the scout's 2 km square,
`tools\scout\towns.json`, centre offset 300 m west and 400 m south of the village's OSM node so the quarry, depot,
marsh and hollows fit).

| | Test world (v1) | Millbrook (v2) |
|---|---|---|
| Grid | 2,049², 1,024 m | 4,097², 2,048 m |
| Runtime ground layers (client and server) | ~113 MB | ~450 MB; the deform deltas become sparse per 64 m tile if the memory budget (M5) needs it |
| Terrain chunks (64 m) | 256 | 1,024 |
| Collider grid (16 m cells) | 64² | 128² |
| Net position field (draft `VehicleStateCodec`, not yet frozen) | 21 bits over 1,024 m | 22 bits over 2,048 m, the same ~0.5 mm step |

The rejected options: 1 km (hauls of a few hundred metres) and 4 km (mostly empty ridges, and it would need tile
streaming).

### 5. The bake pipeline (offline, deterministic, `tools\world\` + Unity editor)

Each stage reads the previous stage's files and writes its own, so a stage can be re-run alone. All of it is code;
nothing is hand-edited except the override files named below.

| Stage | Lane | In → out |
|---|---|---|
| W0 fetch + extract | I (T3.0) | sources → `artifacts\world-src\stockport-oh\` → `TownData\stockport-oh\v1\` (roads, buildings, sites, land use, water, the DEM; real names stripped), pinned in `sources.json` and `manifest.json` |
| W1 terrain | W | DEM → `terrain\dem.f32` resampled to the grid (bilinear from 1 m, bicubic from 10 m); river surface levelled per reach; banks smoothed; `surface`, `mud` and `tint` from OSM landuse (`natural=wetland` → bog mud, farmland, meadow, forest floor, gravel) |
| W2 graph | R | OSM ways → `roads\graph.json`: centrelines in the local frame, class, width, lanes, surface, one-way, bridge and ford, junction type from tags (`junction=roundabout`, `highway=stop`, `traffic_signals`), otherwise rules by class (05 §6) |
| W3 roads | R | graph + terrain → junction surfaces, corners, kerbs, sidewalks, markings, stop lines, bridges' decks; the road footprint flattens `heights` and writes `roadd` and `surface` (05 §5, now generic) |
| W4 sites | R | OSM landuse, `amenity=parking`, `fuel`, shops and yards → lots, driveways, parking rows by business type (research 08 templates); where OSM maps no lot, a plausible one from the business type and frontage |
| W5 buildings | A | OSM footprints with `building`, `building:levels`, `roof:shape`, `shop`/`amenity`/`craft` → `buildings\instances.json` (footprint, storeys, roof, type, style seed) → Blender procedural models (07, Synty-matched style guide) → `art\buildings\` GLB/FBX |
| W6 nature | W | OSM `landuse=forest`, `natural=wood`, `scrub`, hedgerows and the DEM's slope → tree, bush and grass scatter with our eight species and the grass tufts |
| W7 places | G | the game places on real features: garage, mill, quarry, fuel, depot, river landing (replaces the harbour), farms, bog → `world.json` `pois` with delivery rings |
| W8 export | W | everything above → `WorldData\millbrook\v1\` with `manifest.json` (SHA-256 per file, source pins, bake commit), layer files in the CanonData v1 formats (`heights`, `base`, `surface`, `wear`, `roadd`, `mud`, `tint`) |

Overrides, the only hand-made inputs: `WorldData\millbrook\overrides\` holds `roads.json` (a junction the solver
can't do; a road to add or drop), `sites.json` (a lot to change), `places.json` (move a game place). Every override
has a one-line reason.

### 6. What we take from OSM, and what we don't

- **Taken:** geometry and the descriptive tags (road class, width, surface, building type, levels, roof shape, land
  use, waterway, bridge).
- **Never shipped:** the town's name, street names, business names, `brand`, `operator`, phone numbers, addresses and
  any `name:*` tag. The bake drops them at W2 and W5; streets get fictional names from our own list; shops get
  fictional names and no logos (AGENTS.md rule 5).
- **Credit:** the Credits panel already shows "© OpenStreetMap contributors (ODbL)"; it adds "Elevation: U.S.
  Geological Survey, 3D Elevation Program". `THIRD_PARTY_NOTICES.txt` gets both.
- **ODbL share-alike:** the game is a *produced work* (attribution only). Our cleaned graph and footprint files are a
  *derivative database*; before any public build we offer them under ODbL (a zip beside the game or on request),
  which the notice says. Friends-only builds are fine meanwhile.

### 7. What changes for existing work

| Item | Before | After |
|---|---|---|
| CanonData v1, ADR-001 | the shipped world | the test world; unchanged |
| `Canon.PlacementsUnchanged`, `Canon.PlatesMask`, `Canon.BaseMatchesH`, `Canon.WindowsLit` (M3, M5 gates) | on v1.1 | dropped; replaced by `World.ManifestHashes`, `World.BakeDeterministic` (bake twice → identical hashes, also in a fresh worktree) and `World.LitPanes` on v2 |
| Roads research 05 | node-by-node for v1 | the design rules stay (corner radii by class, MUTCD markings, stop-line placement, kerbs, roundabouts); the node list, U8 and the square go |
| Lane R's M2 `RoadNetworkAsset` | the legacy `traffic.json` graph | stays for the test world; the new world gets its graph from W2 |
| Golden traces, deform refs, `m2.drive.route9` | Millbrook (v1) | the test world, unchanged |
| Jobs (M9) | the oracle's places | the payout formula and its oracle references stay; the places are W7's |
| Minimap | v1 `map.png` | rendered from the v2 bake |
| Buildings, trees | `buildings.glb`, `trees.glb` from the oracle | v2 buildings from Blender (W5); the eight tree species we already have |

### 8. Ownership

New paths: `WorldData\` (W), `tools\world\` (W; `tools\world\roads\` and `sites\` R), `art\buildings\` (A, already
under `art\`), `Game\Assets\Backroads\Scenes\CanonTestWorld.unity` (W). `OWNERS.json` gets them when this ADR is
accepted.

## Consequences

- M3 becomes **"Real Millbrook: ground, roads and junctions"** (the re-plan, `docs\milestones\replan-new-world.md`):
  W0-W3 and W8 with grey-box buildings from footprints, so you can drive the real streets early; W4-W6 and the
  Synty-matched buildings in M4-M5; W7 with the jobs in M9.
- Every parity test keeps running on the test world, so the vehicles stay checked against the browser game while the
  shipped world changes.
- The world grows (option B is four times the area): the M5 perf gates are re-measured on the new world, and the
  flythrough route is redrawn on it.
- The bake is code from public data, so another town (a second map, a sequel) is a new `TownData` extract and `WorldData` name, not new art.

## Open questions

1. ~~The town and the size~~: decided (U26 Stockport, U27 2 km).
2. Seasons and era: OSM is today's town. Do we keep modern details (a dollar store's box shape, a cell tower) or
   lean older (1990s small town)? Default: today's shapes, no brands.
3. Rail: if the town has a rail line, a static line with a siding at the depot in M3 (driving over level crossings);
   trains later or never. Default: static.
