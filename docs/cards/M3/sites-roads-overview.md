# M3 draft cards: the street and site program (overview)

**Status: draft.** Written against decision **U25** (2026-10-19): the new Millbrook is a real small US river town in rolling hills, renamed, with elevation from USGS 3DEP and streets, lots and building footprints from OpenStreetMap; every building is new (lane A, Blender, Synty-matched). U8a-e, U15 and U22 are superseded. The town is **Stockport, Ohio** (chosen 2026-10-19), a Muskingum-valley village renamed Millbrook, on a 2 km × 2 km play area; town-specific numbers stay **parameters** (table at the end, Stockport values filled in where known) so the programs run on any OSM town. Nothing here depends on the old Millbrook's nodes, widths or roundabout.

Design: `docs/design/site-layouts.md` (sites) and `docs/research/05-roads-intersections.md` §3-§8 (the junction algorithms, which carry over). Measurements: `docs/research/08-site-layouts.md` (sites), `08b-storeys-and-roofs.md` (storeys and roofs for lane A), `Game/Assets/Content/sites/{siteProfile,buildingProfile,siteTemplates}.json`.

## In plain words

Two programs turn the real town's map data into something you can drive through. The **street program** (lane R) takes the town's roads from OpenStreetMap plus the real ground heights and builds proper junctions: one clean asphalt surface with rounded kerbs, sidewalks, ramps, painted lines, stop lines and signs. The **site program** (lane S) takes every building and the parking lots, yards and driveways mapped around it, and builds what belongs there for that kind of business: a fuel forecourt with its canopy, a shop's car park with painted stalls, a warehouse's truck yard and loading docks, a farm's gravel yard, a house's driveway. Where the map shows nothing, it borrows a real example from the 17 measured towns and fits it, and where nothing fits it lays out a sensible lot by rule. Both programs run in the editor, always give the same result, and replace the old round gravel pads and loose road overlaps completely.

## Pipeline and order

```
                 (I) T3.S0 town source ─────────────┐ (W) DEM import, terrain v2, plates API
                     OSM extract, typed buildings   │        (lane W card, not here)
                              │                     │
                 (R) R0o OSM road graph ◄───────────┤
                              │                     │
                 (R) R1 junction surfaces           │
                              │                     │
   (S) S2 parking engine  (R) R2 kerbs, walks, cuts ── KerbFrontage + DrivewayCut API ──┐
   (synthetic, starts day 1)  │                                                        │
              │           (R) R3 markings, controls, chunks                            │
              │           (R) R4 roundabouts, bridges, aprons                          │
              │                                                                        │
   (S) S1 blocks and parcels ◄── R0o graph (provisional kerb lines) then R2 frontage ──┘
              │
   (S) S3 site plans: mapped OSM, real templates, rules per business type
              │
   (S) S4 surfaces, grading plates, kerbs and islands, markings, props ◄── W plates API, R2 cuts
              │
   (S) S5 validation, determinism, screenshots  +  (R) Roads.Validation  →  M3 gate
```

| Card | Lane | Title | Needs | Est. |
|---|---|---|---|---|
| [sites-S0](sites-S0-town-source.md) | I (or W) | Town source: one OSM extract, typed buildings, hashed `TownData` | the user's town pick | 2 AD |
| [roads-R0o](roads-R0o-osm-graph.md) | R | OSM road graph on the DEM | S0, W's DEM | 3 AD |
| [roads-R1](roads-R1-junctions.md) | R | Junction surfaces: corners, mouths, polygons, heights | R0o | 5 AD |
| [roads-R2](roads-R2-kerbs-walks-cuts.md) | R | Kerbs, sidewalks, ramps, driveway cuts, road plates | R1, W plates API | 5 AD |
| [roads-R3](roads-R3-markings-controls.md) | R | Markings, controls from tags, signs, chunks, LOD, colliders | R1 (R2 for crosswalk ramps) | 4 AD |
| [roads-R4](roads-R4-roundabouts-bridges-aprons.md) | R | Roundabouts and mini-roundabouts from tags, bridges, unpaved aprons, class changes | R1 | 4 AD |
| [sites-S1](sites-S1-parcels.md) | S | Blocks and parcels from the street graph and OSM land use | R0o (R2 for final frontage) | 3 AD |
| [sites-S2](sites-S2-parking-engine.md) | S | Parking engine: stalls, aisles, accessible stalls, islands, entrances | none (synthetic) | 4 AD |
| [sites-S3](sites-S3-site-plans.md) | S | Site plans per business type: mapped OSM, real templates, rules | S1, S2 | 6 AD |
| [sites-S4](sites-S4-surfaces-props.md) | S | Surfaces, grading plates, kerbs, markings, props, colliders | S3, R2 cut API, W plates API | 4 AD |
| [sites-S5](sites-S5-validation.md) | S | Validation, determinism, screenshots, profile check | S4, R3 | 2 AD |

**Ownership (for `OWNERS.json`, integrator):** lane S would own `Game/Assets/Backroads/Sites/`, `Game/Assets/Backroads/Tests/EditMode/Sites/`, `Game/Assets/Backroads/Tests/PlayMode/Sites/`, `Game/Assets/Backroads/Testing/Runtime/Scenarios/Sites/` and `Game/Assets/Content/sites/` (today under lane V's `Game/Assets/Content/`; the three study files were written there on this research branch); `TownData/` goes to W (like `CanonData/`) or I; `tools/osm/` and `tools/sites/` stay I.

Totals: R 21 AD, S 19 AD, I 2 AD. Lane S is a proposal: a new worktree `s` (branch `m3/s-sites`), or lane R takes S1-S5 after R4 if the integrator prefers fewer lanes (then M3 grows by ~3 weeks for R).

## Contracts between the lanes (frozen at the start of M3, ADR in `docs/adr/`)

1. **`TownData` (S0 → everyone).** `TownData/<townId>/v1/`: `manifest.json` (sha256 of every file, OSM timestamp, extract bbox, origin, generator versions), `roads.json` (ways with the tags R reads), `buildings.json` (footprint polygons in local metres, OSM id, tags, business type, form), `sites.json` (mapped parking lots, aisles, spaces, fuel areas, land use, barriers, docks, entrances), `credit.txt` (ODbL). Local frame: metres, x east, z north, origin `ORIGIN_LATLON` (parameter); Unity position = (x, y, z) with y from the DEM. Like `CanonData`, never edited by hand; a re-extract is a new version.
2. **`KerbFrontage` (R2 → S1, S4).** For every block face: the kerb-face polyline (or pavement edge on unkerbed roads), the sidewalk back edge, arc length s, top-of-kerb and gutter heights per s, the road class, speed, oneway, and **forbidden intervals** (corner returns and their tangents + clearance, crosswalk landings and ramps, stop-line zones, inlets, signal and sign poles). Until R2 lands, R0o provides a provisional version (centreline offset by half-width + kerb + walk, no forbidden intervals except a 10 m corner clearance) behind the same interface.
3. **`DrivewayRequest` → `DrivewayCut` (S4 ↔ R2).** S asks: face id, s0..s1 (throat), kind (`residential`, `commercial`, `truck`, `fuelIn`, `fuelOut`, `field`), flare (length or radius), surface. R answers with the cut it baked (dropped kerb `Dropped25`, apron across the walk at ≤ 2 % cross slope, flares) or moves it by up to `CUT_SHIFT_MAX` (parameter, 3 m) out of a forbidden interval, or rejects it with a reason; S re-plans the site against the answer. Mapped OSM driveways and service roads come in as requests with `mapped = true` and are moved only when they hit a corner return.
4. **Grading and surface plates (S4 and R2 → W).** `Plate {polygon, kind (road, walk, lot, yard, pad), surface id, target heights (a plane, a least-squares surface or per-vertex), max slope, blend distance, priority}`. W applies them in a fixed order: road plates (R2), then site plates (S4), then building pads (A/W), then the blend; every write is deterministic and the result is hashed. Lots grade only inside their own polygon plus the blend band; a lot plate meets the sidewalk back edge height exactly at every driveway cut.
5. **Business type and doors (S0 → S3 → A).** One classifier, written once (the rules of `tools/osm/measure.mjs typeOf`, ported to C# and kept in step with a fixture test), keyed on OSM `building`, `shop`, `amenity`, `craft`, `office`, `tourism` and land use. Lane A's Blender generator reads the same type and `buildingProfile.json` (keyed by type and by OSM `building`/`shop`/`amenity`/`craft` tags), and builds door openings where the site plan's `DoorSpec`s put them (design §7a).

## Gates (proposed for the M3 milestone)

- EditMode `Roads.Validation` (05 §8, re-pointed at `TownData`): bake with 0 errors; every junction surface simple and CCW; seams bit-shared; terrain under every road footprint at surface - 0.03 m; one STOP per controlled approach; **bake twice → identical hashes**, also in a fresh worktree.
- EditMode `Sites.Validation` (sites-S5): every non-accessory building has a site plan; no plan overlaps a building, a road footprint or another plan; every stall lies wholly inside its lot; accessible stall counts follow the 2010 ADA table; every entrance is a baked `DrivewayCut`; lot grades within limits; **bake twice → identical hashes**.
- EditMode `Sites.MatchesProfile`: the generated town's lot positions, stall and aisle sizes, fuel canopy setbacks, driveway lengths etc. fall inside the measured p10-p90 bands of `siteProfile.json` (report, with a hard floor of 70 % inside).
- Autotests `m3.shots.junctions` and `m3.shots.sites` (fixed cameras: 8 junctions, 12 sites across the business types, noon and night).
- F10: "Do these look like real small-town streets, parking lots and yards?"

## Parameters for Stockport, Ohio (decided 2026-10-19: Stockport, a Muskingum-valley village, renamed Millbrook, on a 2 km x 2 km play area)

| Parameter | Meaning | Stockport value | Who fills in the rest |
|---|---|---|---|
| `TOWN_ID`, `TOWN_NAME` | the real town and its in-game name | `stockport-oh`, "Millbrook" | set |
| `ORIGIN_LATLON` | local frame origin, the centre of the play area | the scout's 2 km square centre: 300 m west and 400 m south of Stockport's OSM `place` node 154045239 (node about 39.548 N, 81.793 W; `tools/scout/towns.json`), so the quarry hollow, the depot on the old rail grade and the marsh fit | T3.0 computes it and writes it into the `TownData` manifest |
| `PLAY_AREA` | the drivable world | 2,048 × 2,048 m square centred on `ORIGIN_LATLON` (the 4,097² grid at 0.5 m, ADR-006 §4), axis-aligned in the local frame (x east, z north), ±1,024 m | set |
| `EXTRACT_BBOX` | OSM extract | `PLAY_AREA` + 300 m on every side (ADR-006 §2), so roads that cross the edge keep their junctions just outside; everything is clipped back to `PLAY_AREA` after the graph is built | set |
| `DEM_EXTENT`, `DEM_RES` | elevation | `PLAY_AREA` + the skirt lane W chooses for distant terrain; USGS 3DEP 1 m DEM (Ohio has statewide 1 m lidar coverage in 3DEP) [verify: lane W checks the tile index] | lane W |
| `EDGE_RULE` | what a road does at the play-area edge | it ends 50 m inside the edge with the terrain closing it off (a bend into trees or a rise), no visible stub; lane R reports every road that crosses the edge | lane R, R0o |
| `KERB_ZONE` | where streets get kerbs and walks | streets with `sidewalk=*` tags, else the village's built-up hull + 20 m (a village of a few hundred people: expect kerbs only in the village core) | R2 from the extract |
| `DOWNTOWN_ZONE` | street-front commerce | hull of `form = streetfront` buildings + 30 m (expected: a short riverside block) | S1 from the extract |
| `CORNER_R` | kerb radii by class pair | 05 §5.3 (6 / 7.5 / 9 / 12 m); state routes 12 m | set |
| `CUT_SHIFT_MAX` | how far R may move a driveway cut | 3 m | set |
| `RURAL_DEPTH` | rural block depth where no road closes it | 120 m | set |
| `SITE_SEED` | per-site seed | hash(`TOWN_ID`, OSM type, OSM id) | set |
| `PROFILE_BLEND` | weight of Stockport's own measurements against the 17-town profile | per measure, w = n_stockport / (n_stockport + 30): a village this small has few lots of each kind, so the 17-town profile carries most numbers | set |
| Expected special sites | to check in the extract | the Muskingum River crossing (bridge, roads-R4), the river lock and dam and the historic mill (mill and harbour types: a river landing), farms on the valley slopes | S0 lists what the map really has |
