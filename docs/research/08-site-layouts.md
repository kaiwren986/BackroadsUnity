# 08 — Site layouts: parking lots, fuel stations, yards, driveways and buildings in real small towns

Research date 2026-10-19 (lane R research, branch `m3/rs-sites-research`). Map data **(c) OpenStreetMap contributors, ODbL 1.0**. Labels as in 05: **[measured]** = computed from the OSM download; **[doc]** = a design standard used where OSM is silent (sources in §11); **[thin]** = fewer than 20 samples.

Outputs (committed): `Game/Assets/Content/sites/siteProfile.json` (site numbers per business type), `buildingProfile.json` (footprints, storeys, heights, roofs per type and OSM tag, for lane A), `siteTemplates.json` (real layouts with OSM ids). Tools: `tools/osm/fetch.mjs`, `measure.mjs`, `render.mjs`. Design that uses them: `docs/design/site-layouts.md`.

---

## 0. Summary

**In plain words.** We downloaded the map data of 17 real North American small towns (once, from the free OpenStreetMap service, as the user approved) and measured how their parking lots, fuel stations, shops, warehouses, farms and houses are laid out, and how big and how tall their buildings are. The site generator uses these numbers, and 72 real layouts copied from these towns, so that every lot in the game is shaped and placed like a real one.

Key numbers (medians, the middle half in brackets):

| Business type | What the towns do [measured] |
|---|---|
| Parking, all (566 lots) | beside the building 43 %, behind 32 %, in front 15 %, across the street 10 %; 1,040 m² (490-2,030); lot area ≈ 1.2 × the building's footprint (0.5-2.6); 1 entrance (1-2); asphalt 76 %; aisle-to-aisle module 18.4 m (17.3-19.6, n 351) = two 5.5 m stalls + a 7.3 m aisle; stalls 2.75 × 5.1 m [thin, n 17] |
| Shops (standalone) | lot beside 37 %, behind 31 %, in front 21 %, across 11 % (n 126); lot ≈ 1.5 × footprint (0.7-3.4) |
| Downtown (street-front) | 999 buildings, 63 % share a side wall, 18 m wide (11-28) × 22 m deep (16-31), wall 3.4 m behind the assumed kerb; never a front lot: beside 43 %, behind 39 %, across 18 % (n 93); an alley behind 10 % (16 m from the back wall) |
| Fuel (103 stations, 48 with a mapped canopy) | canopy 7.8 × 16.4 m (6.8-10.4 × 12-26), long side parallel to the road 56 % (perpendicular 35 %), 9.0 m (5.8-13) from the road edge; shop behind the canopy 66 % (beside 30 %), 6 m away (2.7-15), 14 × 20 m; 2 entrances (1-2), 52 m apart; corner sites use both streets 30 % |
| Warehouses and industry (408 buildings) | 23 × 46 m (16-38 × 32-67), 17 m from the road edge; **loading docks are not mapped** in any town (0 of 408), yard fences almost never; lots beside 46 % (n 22) |
| Farms (63 farmyards) | yard 16,300 m² (6,200-25,300), 117 × 190 m; drive from the road 40 m (29-80); gravel or dirt; few mapped buildings per yard (0-3) |
| Quarries (64) | most are small pits (~900 m²); the working ones 30,000-400,000 m²; set back 40 m (7-57) behind an 83 m (62-136) unpaved access road |
| Mills | no sawmill mapped; 17 grain-elevator bins (silos) [thin] |
| Harbours (29 waterfront groups) | no harbour or marina areas are mapped; piers are: 43 m long (22-60, n 68), 14 m wide where mapped as areas (12-20, n 10), 2 per waterfront (1-3) |
| Houses (18,614) | 21 m apart along the street (15-31), 10.7 m from the road edge (7-15); driveway beside the house 79 %, 27 m long (17-50) to the house, mapped for only 4 % of houses; garage behind 64 %, beside 28 %, in front 8 %, 4.4 m from the house, 6.8 × 8.2 m |

**For lane A's buildings:** footprints are well measured for every type (n in the hundreds to tens of thousands over 29 North American towns; houses 54 % rectangles, 18 % L, 15 % T/U, 13 % more complex; downtown shops 59 % rectangles, 16 % L). Storeys and roof shapes were tagged on only 1 % of these towns' buildings, so a second download of 14 towns chosen for dense storey and roof tags (U28) measures them: `08b-storeys-and-roofs.md`. §9 and `buildingProfile.json`.

**What is thin:** stall polygons (17), aisle widths measured between stalls (1), docks (0), sawmills (0), building tags. Where OSM is silent the design uses standards (§11), marked [doc].

## 1. Method

1. **Towns.** The browser game's 10 study towns (05 §6.1, `D:\Backroads\tools\osm\report.md`) plus 7 more chosen for harbours, quarries, mills and highway fuel stations: Galena IL, Ely MN, Lander WY, Salida CO, Barre VT (granite quarries), Bar Harbor ME and Lunenburg NS (harbours). Population roughly 1,000-15,000. Centres are fixed in `fetch.mjs` (the first 10 are the centres the browser game's study geocoded); no geocoding service was called.
2. **Download** (U23): one successful Overpass API request per town (`fetch.mjs`; requests the server refused were retried), cached under `artifacts/osm/raw/` (gitignored, 0.4-4.4 MB per town). Within 1.6 km of the centre: every highway way, building (ways and multipolygons, all tags), amenity area, land use, barrier, man_made feature, `area:highway`, marina, railway, and the nodes for shops, amenities, doors, entrances, gates, lamps and stops. Within 6 km: farmyards, quarries, industrial land, works, sawmills, harbours, marinas, fuel stations, and everything inside them. Busy servers refused Cle Elum, Decorah and Bar Harbor at first (HTTP 502/504); a second run the same day fetched them and Lunenburg with the same query, so all 17 towns have the full download (0.4-4.4 MB each; `fetch.mjs --light`, a smaller fallback query, was not needed).
3. **Frame.** Every building gets the street frame of its nearest street: u along the street (right when facing the building), v away from it. The road edge is the centreline + an assumed half-width: from `width`, else `lanes` (3.3 m per lane + parking margin), else 6.0 m on through roads and 4.5 m on local streets (the browser game's study used the same two numbers). "Setbacks" are walls to that edge, so downtown values include the sidewalk.
4. **Business type** (`typeOf` in `measure.mjs`, the same rules as design §4.1): from `building`, `shop`, `amenity`, `craft`, `office`, `tourism` tags of the building and of shops and amenities mapped as points inside or within 12 m, then land use; untagged buildings by land use and size as the browser game's study did (marked `inferred`). **Downtown** = commercial or civic, wall within 4 m of the road edge (or within 10 m and sharing a wall), and no lot in front.
5. **Lots.** Every `amenity=parking` polygon (not underground, multi-storey or rooftop; not a parking building) is assigned to the commercial, civic or industrial building whose outline is nearest within 30 m; lots more than 15 % across the building's street are "across". Lot area is split by sampling (2 m grid) into front, side and rear of the building. Entrances = service ways that run from a public street into the lot (within 40 m). Stalls = `amenity=parking_space` polygons; aisles = `service=parking_aisle` ways; aisle width = ray distance between stall rows; module = perpendicular spacing of parallel aisles in one lot.
6. **Fuel.** Stations from `amenity=fuel` areas, canopies (`building=roof` + `amenity=fuel`, the most common mapping), fuel-tagged buildings and points; canopy = the largest roof there; shop = the nearest non-roof building within 35 m.
7. **Repeatability.** `node tools/osm/measure.mjs` re-creates the three JSON files from the cache byte for byte.

## 2. Towns

| Town | Buildings | Street km downloaded | Parking lots | Fuel | Farmyards | Quarries | Driveways | Aisles mapped |
|---|---|---|---|---|---|---|---|---|
| Bar Harbor, Maine | 1,273 | 40.6 | 19 | 4 | 0 | 1 | 45 | 35 |
| Barre, Vermont | 3,669 | 138.7 | 94 | 16 | 8 | 5 | 204 | 74 |
| Bellefontaine, Ohio | 1,111 | 103.4 | 10 | 12 | 0 | 0 | 34 | 34 |
| Cle Elum, Washington | 1,995 | 106.3 | 24 | 9 | 0 | 35 | 26 | 30 |
| Decorah, Iowa | 311 | 192.1 | 12 | 5 | 10 | 6 | 44 | 36 |
| Ely, Minnesota | 2,567 | 87.6 | 34 | 4 | 0 | 4 | 24 | 34 |
| Galena, Illinois | 750 | 94.6 | 29 | 3 | 1 | 4 | 32 | 55 |
| Hillsboro, Kansas | 424 | 98.2 | 47 | 4 | 21 | 0 | 53 | 59 |
| Lander, Wyoming | 3,703 | 97.1 | 61 | 4 | 0 | 0 | 16 | 50 |
| Lunenburg, Nova Scotia | 1,121 | 49 | 29 | 3 | 0 | 3 | 26 | 11 |
| Marfa, Texas | 847 | 115.7 | 5 | 4 | 0 | 0 | 9 | 0 |
| Mount Forest, Ontario | 204 | 78.1 | 39 | 7 | 0 | 2 | 3 | 76 |
| Nevada City, California | 557 | 131.1 | 57 | 13 | 1 | 1 | 106 | 77 |
| Salida, Colorado | 5,687 | 110.8 | 60 | 6 | 22 | 1 | 95 | 53 |
| Viroqua, Wisconsin | 2,819 | 116.4 | 2 | 4 | 0 | 2 | 22 | 27 |
| Wallace, Idaho | 614 | 57.3 | 29 | 2 | 0 | 0 | 6 | 17 |
| Woodstock, Vermont | 628 | 42 | 15 | 3 | 0 | 0 | 74 | 11 |

## 3. Parking lots [measured]

### 3.1 Where the lot goes, by the business it serves

| Served type | Lots | Beside | Behind | In front | Across | Lot area m² | Lot / footprint | Entrances |
|---|---|---|---|---|---|---|---|---|
| shop | 126 | 37 % | 31 % | 21 % | 11 % | 908 (488-1,881) | 1.47 (0.71-3.39) | 1 |
| downtown | 93 | 43 % | 39 % | - | 18 % | 785 (372-1,232) | 1.20 (0.54-2.33) | 2 |
| civic | 41 | 46 % | 32 % | 20 % | 2 % | 913 (505-1,512) | 0.76 (0.41-1.29) | 1 |
| school | 32 | 66 % | 22 % | 13 % | - | 1,147 (488-1,648) | 0.40 (0.19-0.82) | 1 |
| church | 29 | 45 % | 45 % | 7 % | 3 % | 1,229 (502-1,826) | 1.55 (0.62-2.71) | 2 |
| office | 26 | 42 % | 39 % | 8 % | 12 % | 967 (428-1,745) | 1.74 (1.20-3.72) | 1 |
| restaurant | 24 | 38 % | 33 % | 25 % | 4 % | 869 (334-1,172) | 2.11 (0.76-3.85) | 2 |
| apartments | 20 | 30 % | 25 % | 20 % | 25 % | 573 (325-1,193) | 0.87 (0.50-1.36) | 1 |
| warehouse | 17 [thin] | 47 % | 18 % | 12 % | 24 % | 2,212 (1,622-4,012) | 0.62 (0.21-0.86) | 2 |
| motel | 14 [thin] | 50 % | 7 % | 36 % | 7 % | 1,553 (379-1,638) | 1.01 (0.53-2.39) | 2 |
| supermarket | 11 [thin] | 55 % | 36 % | 9 % | - | 2,843 (1,659-6,178) | 1.30 (0.79-1.85) | 2 |
| fast food | 7 [thin] | 57 % | 14 % | 29 % | - | 1,041 (580-1,744) | 2.87 (2.41-3.14) | 2 |
| industrial | 7 [thin] | 29 % | 43 % | 29 % | - | 2,390 (949-2,455) | 4.67 (2.07-20.36) | 1 |
| car repair | 5 [thin] | 40 % | 20 % | 40 % | - | 574 (248-913) | 2.10 (0.64-2.35) | 2 |
| (no building within 30 m) | 112 | | | | | 1,572 (635-2,906) | | |

- 34 % of lots wrap more than one side of the building (an L around a corner shop is common).
- Lots sit 9.5 m (4.4-30) behind the road edge (planted strip or sidewalk); lots in front run 28 m deep (15-46) [n 67].
- A lot spans 1.1 × the building's width along the street (0.6-1.8).
- Where the lot touches the road edge directly, it does so over 28 m (16-37), i.e. an open frontage instead of a single cut.

### 3.2 Inside the lot

| Measure | Value | n |
|---|---|---|
| Aisle-to-aisle spacing (one double-loaded module) | 18.4 m (17.3-19.6) | 351 |
| Aisle centre to lot edge (half aisle + one stall row) | 8.9 m (6.1-11.2) | 1,119 |
| Aisles per lot | 1 (1-2), up to 9 | 241 |
| Stall width × length (mapped polygons) | 2.75 m (2.72-2.84) × 5.1 m (5.1-6.9) | 17 [thin] |
| Stall angle to the aisle | 90° 50 %, 60° 25 %, 45° 13 %, parallel 13 % | 16 [thin] |
| Area per tagged space (`capacity`) | 44 m² (32-67) | 25 [thin] |
| Surface (tagged lots) | asphalt 76 %, paved 9 %, unpaved/ground/gravel/dirt 13 %, concrete 3 % | 119 |
| Aisle surface | asphalt 92 % | 163 |

The module (18.4 m) matches the standard 90° module of two 5.5 m stalls and a 7.3 m (24 ft) two-way aisle [doc]: the generator uses 2.75 × 5.5 m stalls and 7.3 m aisles, and checks the module against the measured band.

## 4. Fuel stations [measured]

| Measure | Value | n |
|---|---|---|
| Stations found / with a canopy / with a site polygon | 103 / 48 / 11 | |
| Canopy short × long side | 7.8 m (6.8-10.4) × 16.4 m (12.4-25.7) | 48 |
| Canopy area | 141 m² (97-216) | 48 |
| Canopy long axis to the road | parallel 56 %, perpendicular 35 %, oblique 8 % | 48 |
| Canopy to the road edge | 9.0 m (5.8-13.0) | 48 |
| Shop position | behind the canopy 66 %, beside 30 %, in front 4 % | 47 |
| Shop to canopy gap | 6.0 m (2.7-15.2); attached 9 % | 47 |
| Shop size | 14 × 20 m (10-17 × 18-28), 249 m² (191-415) | 47 |
| Shop to the road edge | 22 m (16-31) | 47 |
| Entrances | 2 (1-2), 52 m apart (32-65); both streets on corners 30 % | 23 |
| On a through road | 83 % | 48 |

- Pump islands and dispensers are **not mapped** (the only mapped point inside a canopy is the fuel station node itself), so island layout comes from the canopy: a 7-8 m deep canopy covers one row of islands along its long axis with cars on both sides; canopies ≥ 11 m deep cover islands across them, 7.3-7.9 m apart [doc].
- Small-town canopies are small: the median covers two islands (four fuelling positions).

## 5. Downtown and retail [measured]

| Measure | Value | n |
|---|---|---|
| Street-front buildings | 999 | |
| Share a side wall | 63 % | 999 |
| Width along the street × depth | 18 m (11-28) × 22 m (16-31) | 999 |
| Wall to the assumed road edge | 3.4 m (1.5-4.7) (the sidewalk) | 713 shops |
| Lots of downtown buildings | beside 43 %, behind 39 %, across 18 %, never in front | 93 |
| Alley behind (within 30 m of the back wall) | 10 %, 16 m (4-21) from the back wall | 100 |
| Alleys per town | 8 (2-40), 1.5 km (0.2-12.5) | 17 towns |
| On-street parking tagged on the frontage street | 5 % (parallel 71 %, diagonal 21 %, perpendicular 7 % of 14 tagged sides) [thin: under-mapped] | 999 |

- On-street parking is clearly under-mapped (real downtowns almost all have it); the design puts parallel stalls on every downtown street wide enough (≥ 12 m kerb to kerb) unless tagged otherwise.
- Standalone shops sit 13 m (8.4-24) from the road edge; their lots are beside 37 %, behind 31 %, in front 21 %.

## 6. Warehouses and industry [measured where mapped]

| Measure | Value | n |
|---|---|---|
| Buildings (`warehouse`, `industrial`, or big buildings on industrial land, ≥ 300 m²) | 408 | |
| Footprint | 867 m² (509-1,893), 23 × 46 m | 408 |
| Wall to the road edge | 17.4 m (6.8-29) | 324 |
| Lots | beside 46 %, behind 23 %, across 18 %, in front 14 % | 22 [thin] |
| Loading docks mapped (`door=loading_dock|overhead`, `entrance=service`) | 0 | |
| Fenced (fence length ≥ half the perimeter within 40 m) | < 1 % | 408 |
| Industrial land use polygons | 60,000 m² median (23,000-430,000): whole industrial zones, not parcels | 289 |

Docks, aprons and yard fences are therefore design standards [doc]: docks on the side or rear away from the street, doors 3.7-4.3 m on centre, 35-40 m apron for semi-trailers, trailer stalls 3.7 × 16 m, chain-link fence with a gate.

## 7. Farms, quarries, mills, harbours [measured]

| Site | Measure | Value | n |
|---|---|---|---|
| Farm (`landuse=farmyard`) | yard area; sides | 16,300 m² (6,200-25,300); 117 × 190 m | 63 |
| | mapped buildings per yard; cover | 0 (0-3); 4 % where mapped | 63 |
| | yard edge to the road edge | 3.3 m (0.5-48): yards usually reach the road | 60 |
| | drive from the public road to the yard | 40 m (29-80); `service` 61 %, `driveway` 39 % | 33 |
| Quarry (`landuse=quarry`) | area | 934 m² median (small pits); the working quarries 30,000-400,000 m² (Barre's granite quarries up to 1.4 km²) | 64 |
| | set back from the road edge; access road | 40 m (7-57); 83 m (62-136), unpaved 89 % | 30; 19 |
| | buildings inside | 0 (the plant sits outside the mapped pit) | 64 |
| Mill | sawmills (`craft=sawmill`, works with lumber) | 0 in 17 towns | |
| | grain elevators (`building=silo|grain_silo`, `man_made=silo` outside farmyards) | 17 bins, 28 m² (10-196) [thin] | 17 |
| Harbour | waterfront groups (piers within 60 m of each other; no `landuse=harbour` or `leisure=marina` is mapped in any town) | 29 (Bar Harbor, Lunenburg, lake towns) | |
| | pier length; width (pier areas) | 43 m (22-60); 14 m (12-20) | 68; 10 [thin] |
| | piers per waterfront; parking lots within 80 m | 2 (1-3); 9 lots, 1,970 m² (1,490-2,290) | 29 |

## 8. Houses [measured]

| Measure | Value | n |
|---|---|---|
| Houses (tagged and inferred) | 18,614 | |
| Spacing along the street (frontage) | 21 m (15-31) | 12,301 |
| Wall to the road edge | 10.7 m (7.0-15.2) | 18,614 |
| Long axis along / across the street | along 46 %, across 49 % | 18,371 |
| Mapped driveways per house | 0.04 (819 driveways) | |
| Driveway length (to a house) | 27 m (17-50) | 319 |
| Driveway start beside the house / in front of it | 79 % / 21 % | 319 |
| Driveway surface (all tagged driveways) | unpaved 34 %, asphalt 32 %, paved 19 %, gravel 10 %, concrete 3 % | 130 |
| Garages (`building=garage|garages|carport`) | 391 | |
| Garage position | behind 64 %, beside 28 %, in front 8 %; attached 4 % | 391 |
| Garage gap to the house; to the road edge | 4.4 m (2.2-8.2); 21.6 m (13.1-28.5) | 375; 391 |
| Garage size | 6.8 × 8.2 m (5.5-7.9 × 7.1-9.8) | 391 |

## 9. Buildings for Blender (lane A) [measured]

`buildingProfile.json` keys every number by this study's business type (`type:house`, `type:downtown_shop`, ...) and by OSM tag value (`building:*`, `shop:*`, `amenity:*`, `craft:*`; a building counts under each of its tags and under those of the shops mapped inside it). Per key: footprint area, short and long side, aspect, rectangularity, corner count, shape class (rectangle, L, T/U, complex), materials and colours (North American towns), storeys, roof shape, roof levels, ridge orientation, height, eave height, storey height and roof pitch (every town, each with n, the towns behind it and a North America / Germany split: 08b), long axis to the street, setback, shared walls, and the side of mapped main and service doors.

| Type | n (North America) | Footprint m² | Short × long m | Shape | Storeys tagged (na): counts | Roof shapes tagged (na) |
|---|---|---|---|---|---|---|
| house | 34,213 | 131 (97-181) | 9.6 (8.1-11.7) × 14.9 (12.1-18.1) | rect 54, L 18, T/U 15 | 1330: 2×804 1×427 3×83 | 358: gabled 59 %, pyramidal 12 %, many 10 % |
| accessory | 8,278 | 32 (18-45) | 4.6 (3.5-6.0) × 6.7 (5.1-7.8) | rect 96, L 3, T/U 1 | 52: 1×47 2×4 3×1 | 7: gabled 29 %, skillion 29 %, hipped 29 % |
| apartments | 537 | 351 (176-621) | 13.9 (10.3-19.3) × 27.6 (17.9-39.8) | rect 39, complex 32, T/U 16 | 103: 2×41 3×35 4×13 | 7: gabled 57 %, flat 14 %, quadruple saltbox 14 % |
| downtown shop | 1,325 | 324 (166-516) | 12.8 (8.2-18.6) × 26.5 (19.4-35.1) | rect 59, L 16, complex 14 | 244: 2×108 1×62 3×60 | 16: flat 69 %, gabled 13 %, quadruple saltbox 6 % |
| downtown restaurant | 302 | 263 (175-460) | 11.8 (8.6-18.1) × 24.4 (19.4-30.7) | rect 60, L 16, T/U 11 | 53: 2×23 1×14 3×12 | 6: flat 67 %, hipped 17 %, double saltbox 17 % |
| downtown office | 150 | 300 (178-552) | 14.0 (10.2-19.3) × 25.3 (19.2-34.3) | rect 53, L 19, complex 17 | 59: 2×33 3×15 1×5 | 7: flat 57 %, gabled 43 % |
| downtown civic | 103 | 582 (348-902) | 19.4 (14.9-27.2) × 33.2 (24.9-46.1) | rect 42, complex 30, T/U 16 | 20: 2×9 3×6 1×3 | 1: flat 100 % |
| shop | 3,401 | 403 (317-601) | 16.7 (12.2-21.2) × 29.0 (22.0-37.3) | rect 46, complex 26, T/U 14 | 177: 1×88 2×54 3×26 | 17: gabled 59 %, flat 18 %, hipped 12 % |
| restaurant | 218 | 337 (163-619) | 14.4 (9.7-20.9) × 25.8 (17.0-36.4) | rect 53, complex 18, L 17 | 35: 1×20 2×10 3×5 | 9: flat 33 %, gabled 22 %, many 22 % |
| fast food | 76 | 312 (232-451) | 13.8 (11.3-16.3) × 24.8 (18.9-31.1) | rect 54, complex 20, L 14 | 7: 1×5 3×1 5×1 | 0 |
| office | 250 | 308 (179-555) | 14.5 (11.1-20.5) × 23.6 (17.3-33.8) | rect 40, complex 27, T/U 17 | 65: 2×29 1×24 3×12 | 9: flat 33 %, many 33 %, pyramidal 11 % |
| supermarket | 28 | 2,300 (1,229-3,375) | 38.9 (32.7-54.3) × 65.1 (41.6-81.4) | complex 43, T/U 21, rect 21 | 1: 1×1 | 0 |
| fuel shop | 61 | 175 (135-334) | 11.4 (8.9-14.3) × 17.7 (14.8-23.2) | rect 79, T/U 8, L 8 | 1: 1×1 | 0 |
| fuel canopy | 76 | 158 (105-248) | 8.9 (7.0-11.3) × 16.2 (13.0-25.8) | rect 93, T/U 3, convex_other 3 | 1: 1×1 | 0 |
| car repair | 130 | 390 (195-740) | 16.8 (10.4-21.4) × 27.8 (18.8-38.5) | rect 58, L 19, complex 15 | 29: 1×26 2×2 3×1 | 1: flat 100 % |
| motel | 161 | 533 (262-1,214) | 20.0 (14.4-28.0) × 35.2 (21.9-57.6) | complex 42, rect 30, T/U 17 | 28: 3×12 2×11 4×4 | 2: gabled 100 % |
| church | 234 | 581 (308-961) | 20.9 (14.4-29.5) × 33.3 (24.7-44.0) | complex 45, rect 25, T/U 18 | 18: 1×8 2×7 3×3 | 1: gabled 100 % |
| school | 363 | 893 (342-2,061) | 26.2 (16.6-43.7) × 42.1 (25.8-69.5) | complex 57, rect 21, T/U 15 | 66: 2×21 3×15 5×14 | 4: flat 50 %, gabled 25 %, many 25 % |
| civic | 276 | 711 (386-1,412) | 23.1 (17.2-35.5) × 36.5 (26.7-51.8) | complex 45, rect 28, T/U 16 | 38: 1×17 2×13 3×7 | 7: gabled 43 %, flat 29 %, hipped 14 % |
| warehouse | 298 | 1,938 (1,526-3,252) | 38.9 (29.1-53.3) × 68.2 (52.0-88.3) | complex 45, rect 29, L 13 | 29: 1×11 2×8 4×6 | 5: gabled 60 %, round 40 % |
| industrial | 487 | 384 (199-668) | 13.9 (10.1-18.8) × 27.4 (19.0-38.3) | rect 78, complex 8, L 8 | 17: 1×10 2×6 4×1 | 5: gabled 60 %, flat 40 % |
| farm | 204 | 81 (23-224) | 6.2 (3.8-12.1) × 12.3 (5.4-20.6) | rect 80, L 6, T/U 6 | 1: 2×1 | 0 |
| quarry | 50 | 113 (59-387) | 8.3 (6.7-15.2) × 15.0 (8.8-27.0) | rect 82, T/U 8, complex 6 | 0 | 0 |
| mill | 28 | 99 (28-196) | 9.9 (6.0-13.1) × 11.3 (6.0-16.2) | convex_other 89, rect 7, T/U 4 | 0 | 0 |

- **Storeys and roofs come mostly from the second download** (U28, `08b-storeys-and-roofs.md`): over both studies, 5,809 of 60,939 buildings have storeys, 3,972 a roof shape, 247 a height and 163 a measurable pitch; the counts above are North American, the German towns of 08b add roof shapes, eaves and pitches.
- Lane A takes **sizes and shapes** from these keys and **storeys, roofs, eaves and pitch** as 08b §6 says: the building's own Stockport tags first, then the `na` numbers of its key, then the standards of 08b §5.
- Houses face the street with the gable end slightly more often than with the long side (across 53 %, along 42 %, n 33,007): both front-gabled and side-gabled houses are needed.

## 10. Real templates (`siteTemplates.json`)

72 layouts, chosen automatically by completeness (a building, its lot, mapped aisles and stalls, entrances, a canopy, docks, fences, a driveway, a garage), at most 2 per town per type, one per building, then checked by eye on the contact sheet (`node tools/osm/render.mjs` → `artifacts/osm/templates.png`; committed SVG sheets in `08-site-layouts/templates-*.svg`).

| Template type | Count | Towns | Why these |
|---|---|---|---|
| harbor | 5 | Lunenburg, Bar Harbor, Ely | waterfront pier groups with the lots and buildings beside them (no harbour or marina areas are mapped) |
| civic | 4 | Barre, Hillsboro, Ely, Lander | church, school and civic lots with their entrances |
| downtown side | 3 | Barre, Nevada City, Wallace | a street-front building with a lot in a gap beside it |
| downtown rear | 4 | Barre, Bar Harbor, Wallace | a street-front building with its lot behind (alley or side drive) |
| fast food | 4 | Cle Elum, Lander, Salida | fast-food buildings with their lots and mapped drive-through or service lanes |
| retail rear | 6 | Lander, Salida, Barre, Decorah, Cle Elum | a lot behind the building reached by a side drive or a second street |
| warehouse | 6 | Woodstock, Decorah, Lander, Nevada City, Salida | warehouses and works with their yards and lots (no docks are mapped anywhere) |
| farm | 6 | Barre, Hillsboro, Salida | farmyards with 3-13 mapped buildings and their access drives |
| fuel | 6 | Decorah, Nevada City, Barre, Bellefontaine, Cle Elum | canopy and shop both mapped, with entrances where mapped; the range covers a one-row canopy (7-9 m deep), a two-row canopy (13-14 m) and a long highway canopy |
| retail side | 6 | Mount Forest, Nevada City, Salida, Bar Harbor, Barre | the most common position; shops and supermarkets with aisles and two entrances |
| retail front | 6 | Nevada City, Woodstock, Ely, Galena, Hillsboro, Lunenburg | a lot wholly in front of the building with mapped aisles; small shop, restaurant, supermarket, motel and car repair |
| quarry | 5 | Barre, Cle Elum, Ely | mid-sized quarries (under 400,000 m²) with their access roads |
| house | 6 | Barre, Galena, Hillsboro | a house with its mapped driveway and garage |
| downtown alley | 4 | Lander, Wallace | street-front blocks with a mapped alley behind them |
| mill | 1 | Hillsboro | grain elevator groups (no sawmill is mapped) |

Each template is in the street frame (u along the street, v away from it, v = 0 on the centreline), with the road's assumed half-width, the main building, lot, aisles, stalls, entrances, canopy, docks, fences, yard, access ways, nearby buildings for context, and the OSM ids of everything used (ODbL credit).

## 11. Standards used where OSM is silent [doc]

| Item | Value | Source |
|---|---|---|
| Accessible stalls | 1 per 25 up to 100 total, 5 for 101-150, 6 for 151-200, 7 for 201-300 ...; one in six van-accessible | 2010 ADA Standards §208.2 (table checked on access-board.gov, 2026-10-19), §208.2.4 |
| Accessible stall and access aisle | car 96 in (2.44 m) wide with a 60 in (1.52 m) aisle; van 132 in (3.35 m) with a 60 in aisle, or 96 in with a 96 in (2.44 m) aisle; ≤ 1:48 slope | 2010 ADA Standards §502.2-502.4 (from the standard; not re-fetched) |
| 90° stall, two-way aisle | 9 × 18 ft (2.74 × 5.49 m), 24 ft (7.3 m) aisle, 60 ft (18.3 m) module | common North American design practice (ITE / ULI parking design); the measured module 18.4 m agrees |
| Parallel stall | 8 × 20-22 ft (2.4 × 6.1-6.7 m) | common practice [verify against the chosen state's DOT manual] |
| Driveway throats | residential 10-12 ft (3.0-3.6 m); commercial two-way 24-30 ft (7.3-9.1 m), one-way 15 ft (4.5 m); truck 40 ft (12 m) | typical municipal driveway standards [verify per town] |
| Fuel islands | island centres 24-26 ft (7.3-7.9 m) apart; canopy clearance 14.5-16 ft | fuel station design practice [verify] |
| Loading docks | doors 12-14 ft (3.7-4.3 m) on centre; apron depth for a 53 ft trailer ≈ 120-135 ft (37-41 m) | dock design practice [verify] |
| Drive-through stacking | 6-8 cars (≈ 6 m per car) | typical municipal codes [verify] |

## 12. Limitations and next steps

- **Mapping density varies a lot:** Viroqua has 2,819 buildings but 2 mapped parking lots; Barre maps 94 lots and 50 spaces. Pooled numbers lean towards the well-mapped towns (Barre, Salida, Lander, Nevada City).
- **Under-mapped features** (stalls, aisle widths, docks, pumps, on-street parking, building storeys and roofs) are filled by standards and rules, and the generator's `Sites.MatchesProfile` check only tests what was measured.
- **The chosen town comes first.** sites-S0 runs the same measurer on the chosen town's own extract; its numbers are blended with this profile (`PROFILE_BLEND`).
- **Second batch done** (U28): 14 towns chosen for dense storey and roof tags, for lane A; see `08b-storeys-and-roofs.md`. Stalls, canopies and docks stay as thin as above.
- Google Maps or Street View data is not used (its terms forbid it); OSM only.

## Credit

Map data (c) OpenStreetMap contributors, available under the Open Database License (ODbL) 1.0, https://www.openstreetmap.org/copyright. The derived profile and templates are a Produced Work / derivative database of OSM data and carry the same credit; `siteTemplates.json` lists the OSM element ids it was made from.
