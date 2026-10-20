# 09 - New world candidates: a real river town for the new Millbrook (U25)

Status: research report, 2026-10-20. Town scout for decision U25 (`docs/approvals/QUEUE.md`), branch `m3/ws-town-scout`.
Scope: shortlist real small US river towns in rolling hills that the Unity game could rebuild as the new Millbrook, from USGS 3DEP elevation and OpenStreetMap streets, lots and footprints. The game renames the town; every building is new (Blender, Synty-matched). Real town names appear only in this research note, never in shipped content.

Map data (c) OpenStreetMap contributors, ODbL. Elevation: USGS 3D Elevation Program (public domain). See section 7.

---

## 0. Recommendation in one screen

| Rank | Town | Pitch | Play area | Footprints in OSM | Map |
|---|---|---|---|---|---|
| **1** | **Stockport, Ohio** (Muskingum valley) | An old mill on the river at a lock and dam, the river bridge and a boat launch beside it, a compact grid village (191 footprints in the 1 km square) under steep wooded hills (90 m of relief inside the 1 km, 113 m in 2 km). | 1 km holds the whole village, mill, bridge, launch and bottom-land farms; **2 km recommended** for the quarry, depot and woods roads we add | very good (92 % of street length) | [svg](new-world/stockport-oh.svg) / [png](new-world/stockport-oh.png) |
| 2 | Hardwick, Vermont (Lamoille valley) | The most complete town: three road bridges in the 1 km, five in 2 km, a brick Main Street, a mill dam, canoe access, marsh, a rail trail through town and 15 km of dirt roads in the 2 km square. | needs **2 km** (the dam, industrial park and canoe access are 1.3-1.7 km north) | partial (60 %), but 529 address points mark the missing houses | [svg](new-world/hardwick-vt.svg) / [png](new-world/hardwick-vt.png) |
| 3 | Lanesboro, Minnesota (Driftless, Root River) | A brick main street in a river bend under 100 m bluffs, two road bridges and an old mill dam in the 1 km square, a quarry 1.6 km out. | core fits 1 km (the south half of town spills 300 m); 2 km for farms and quarry | good (76 %) | [svg](new-world/lanesboro-mn.svg) / [png](new-world/lanesboro-mn.png) |
| 4 | Gays Mills, Wisconsin (Driftless, Kickapoo) | Exactly the wanted size (108 footprints), a mill dam, a marsh at the edge of town, orchards on the ridges, a quarry and deep coulees (126 m relief in 2 km). | old village fits 1 km; 2 km for fuel and orchards; quarry 2.1 km out | partial (55 %) | [svg](new-world/gays-mills-wi.svg) / [png](new-world/gays-mills-wi.png) |
| 5 | Greenwich, New York (upper Hudson, Battenkill) | The best data and the only **active rail yard** (a short line) in town, an old mill, three named dams and two small hydro plants, a kayak launch; but a big village and the gentlest hills. | 1 km takes only the south half of the village (414 footprints); 2 km | excellent (90 %) | [svg](new-world/greenwich-ny.svg) / [png](new-world/greenwich-ny.png) |
| 6 | Newport, Pennsylvania (Juniata valley) | A busy main-line railway along a wide river, a large quarry 700 m from Main Street, steep ridges; mill, landing and marsh would be ours. | core fits 1 km; 2 km for the quarry | poor (36 %) | [svg](new-world/newport-pa.svg) / [png](new-world/newport-pa.png) |

**Recommendation: Stockport, Ohio, in a 2 km x 2 km play area** (the 1 km square at the centre holds the town, at the current 0.5 m detail). Why it wins and what it costs is in section 4; the play-area choice in section 5.

---

## 1. What we looked for, and how we measured it

**Wanted** (U25 brief): a small town core of roughly 40-120 buildings with a brick main street and houses on side streets; a river through or beside town with 1-3 bridges; a mill or mill site by the river; farms on rolling slopes; dirt or gravel roads into woods; a quarry or gravel pit; a fuel station; an industrial yard or depot (rail a bonus); a river landing or boat launch (replaces the old harbour); a wetland or marsh (the bog); real hills of about 60-200 m relief.

**Method.** Fourteen towns in eight states were screened (section 3). For each, `tools/scout/` downloaded one Overpass query (every tagged node, way and multipolygon in a 4 km UTM square around the town's OSM place node) and a 5 m elevation raster from the 3DEP ImageServer, then rendered the overview map and measured three squares: the 1 km play square, the 2 km play square (always containing the 1 km one) and the whole 4 km map. The squares were placed by a scoring search (town core, river, bridges, places, relief) and then checked by eye; their centres are in `tools/scout/towns.json`.

**What the numbers mean.**

- *Buildings*: OSM footprints whose centroid is in the square; "70 m2 or more" leaves out sheds and most garages, so it is closer to the number of buildings we would model.
- *Footprint coverage*: the share of town-street length (residential to primary) with a mapped footprint within 50 m. A fully mapped town scores 85-95 % (fields and woods along some streets keep it below 100); 30-60 % means many houses are missing.
- *Relief*: highest minus lowest ground in the square (bare-earth DEM, 5 m cells); *mean slope* is the average ground slope.
- *Road bridges over the river*: OSM road ways tagged `bridge` that cross the river line or water area, merged per crossing. Rail and rail-trail bridges are counted separately.
- *Game places*: found from OSM tags (mill, dam, quarry, fuel, industrial land use, rail yards, slipways and canoe access points, farmland and barns, wetlands, tracks and unpaved roads). On the maps, places found in OSM are solid circles; places we would add are dashed and say "add".

**Known limits.** US land-use mapping in OSM is sparse: farmland, forest and wetland areas are missing in many towns even where the ground is plainly fields (Lanesboro has none at all), so the hectare rows undercount. Gravel county roads are often not tagged unpaved. Business names were not used as labels (fuel stations and yards are labelled generically). The DEM is bare earth: it shows ground, not trees.

---

## 2. The shortlist

All six have **1 m lidar DEM coverage on 100 % of the 4 km map** (and 1/3 arc-second everywhere). Numbers are 1 km / 2 km / 4 km square.

### 2.1 Stockport, Ohio (Muskingum valley, Morgan County) - rank 1

A village of about 540 on the west bank of the Muskingum, in the unglaciated hill country of south-east Ohio. The village is a tight grid of a dozen streets on a river terrace; at its east end the state road crosses the river on the one bridge, right beside the old **Stockport Mill**, which stands at Muskingum River **Dam No. 6** with its lock. A boat launch is on the same bank just downstream, a fuel station on the east bank. Bottom-land fields lie across the river and north of the village; behind it the land climbs 100 m and more into steep, wooded, deeply cut hollows with county roads. An old railway grade runs south along the river. It reads like Millbrook already: mill, dam, bridge and landing in one view from the village street.

| Measure | 1 km | 2 km | 4 km |
|---|---|---|---|
| Buildings (70 m2 or more) | 191 (186) | 265 | 295 |
| Drivable road km / unpaved | 9.0 / 0 | 18.3 / 0 | 38.5 / 2.9 |
| River km / road bridges over it | 1.6 / 1 | 2.4 / 1 | 6.0 / 1 |
| Elevation (relief) | 192-282 m (90) | 192-304 m (113) | 191-311 m (120) |
| Mean slope | 7.7 % | 18.5 % | 19.6 % |
| Footprint coverage | 92 % | 65 % | 31 % |

- **Native:** mill (the real one, at the dam), river bridge (SR 266, about 230 m long), boat launch, fuel station (0.5 km, just over the river), farmland in the 1 km square, steep hills.
- **We would add:** quarry on a hillside off the county road south-west of the village; a depot on the old rail grade south of town; a marsh at the creek mouth north of the village; dirt roads up the hollows (the county roads exist but are not tagged unpaved). No active railway.
- **Play area:** the village, mill, bridge, launch and fields fit in **1 km**; the **2 km** square adds the hollows and ridges where the quarry and woods roads go.
- **OSM:** 295 footprints, no import tag; the village core is fully drawn, outlying farms partly.
- **DEM:** OH_Statewide_Phase2_2020 lidar, 1 m.
- **Concerns:** only one river bridge (the brief's minimum); a short main street with few commercial buildings, so the brick blocks are mostly ours; the core is about 190 footprints with outbuildings, above the 120 target (drop sheds and garages and it is near 150). Not a famous town.

### 2.2 Hardwick, Vermont (Lamoille valley, Caledonia County) - rank 2

A former granite-finishing town of about 2,900 (the whole town; the village is smaller) on the Lamoille River in Vermont's hill farm country. The river winds through the village and is crossed by **three road bridges inside the 1 km square** (Main Street, West Church Street, Elm Street) and five in the 2 km; a weir and the dam of Hardwick Lake, 1.7 km north, are the mill site. The **Lamoille Valley Rail Trail** follows the old railway through town. North of the centre: an industrial park, canoe access points on the river, a marsh. Around it: barns, rolling fields, woods and a dense net of dirt roads (15 km unpaved in the 2 km square). Relief is the biggest on the list (177 m in 2 km, 240 m on the map).

| Measure | 1 km | 2 km | 4 km |
|---|---|---|---|
| Buildings (70 m2 or more) | 223 (181) | 347 | 503 |
| Drivable road km / unpaved | 14.1 / 2.3 | 28.8 / 15.4 | 52 / 39.8 |
| River km / road bridges over it | 2.3 / 3 | 4.4 / 5 | 9.2 / 7 |
| Elevation (relief) | 244-327 m (83) | 238-415 m (177) | 237-477 m (240) |
| Mean slope | 14.5 % | 18.3 % | 17.9 % |
| Footprint coverage | 60 % | 49 % | 36 % |

- **Native:** bridges, mill dam and weir, fuel, industrial park, canoe access, marsh, barns and fields, dirt roads, rail trail (old railway).
- **We would add:** a gravel pit (the historic granite quarries are in Woodbury, outside the map). No active railway.
- **Play area:** the centre and its bridges fit 1 km, but the mill dam, industrial park, canoe access and marsh are 1.3-1.7 km north: **needs 2 km**.
- **OSM:** 503 footprints (14 from the Microsoft import); many houses missing, but **529 address points** sit where the missing houses are, so we can place buildings on them.
- **DEM:** VT_Statewide_A23 lidar (2023), 1 m, plus two older projects.
- **Concerns:** footprints only partly drawn; the village is long and strung out along the valley (bigger than the 120 target). Not famous.

### 2.3 Lanesboro, Minnesota (Driftless Area, Root River, Fillmore County) - rank 3

A village of about 750 in a tight bend of the South Branch Root River, ringed by 100 m limestone bluffs. The main street is a row of 19th-century brick commercial blocks; two road bridges and the **Lanesboro Dam** (an old mill dam with a small mill pond) are inside the 1 km square, and the Root River State Trail crosses the river three times on old railway bridges. An industrial yard is in town and a quarry 1.6 km south. Residential streets fill the flat south of town.

| Measure | 1 km | 2 km | 4 km |
|---|---|---|---|
| Buildings (70 m2 or more) | 290 (236) | 477 | 487 |
| Drivable road km / unpaved | 12.0 / 0.5 | 22.9 / 2.4 | 45.1 / 7.2 |
| River km / road bridges over it | 1.6 / 2 | 2.3 / 2 | 8.8 / 2 |
| Elevation (relief) | 247-341 m (94) | 247-348 m (101) | 239-369 m (130) |
| Mean slope | 19.1 % | 16.8 % | 17.8 % |
| Footprint coverage | 76 % | 70 % | 36 % |

- **Native:** dam (mill site), two road bridges, industrial yard, quarry (just outside 2 km), rail trail (old railway), a few unpaved roads, bluffs.
- **We would add:** fuel station (none tagged), canoe landing below the dam, a farm on the bluff top, a backwater marsh. No land use is mapped at all, so fields and woods come from our own placement.
- **Play area:** main street, bridges and dam fit 1 km; the south half of town is 300 m outside it; 2 km for the farms, the quarry needs a small move inward.
- **OSM:** 487 footprints, no import tag, town well drawn.
- **DEM:** MN_SE_Driftless_2021 lidar (2022), 1 m.
- **Concerns:** a known tourist and bike-trail town (moderately well known); about 290 footprints in the 1 km, above target; the bluffs are steep (19 % mean slope in the 1 km) and leave little rolling farmland inside 2 km.

### 2.4 Gays Mills, Wisconsin (Driftless Area, Kickapoo River, Crawford County) - rank 4

A village of about 600 named after its mills, on the Kickapoo River between steep coulee ridges. The old village is a small grid (108 footprints, **inside the 40-120 target**) beside the river, with the old mill dam site at its north end by the one road bridge, an industrial yard and a marsh on its edge. After the floods of 2007-2008 part of the village was rebuilt on higher ground to the north (the fuel station is there); the ridges east of town carry apple orchards, and a large quarry is 2.1 km north. An old railway grade runs along the valley.

| Measure | 1 km | 2 km | 4 km |
|---|---|---|---|
| Buildings (70 m2 or more) | 108 (93) | 127 | 150 |
| Drivable road km / unpaved | 9.4 / 0.3 | 18.7 / 0.6 | 40.1 / 3.4 |
| River km / road bridges over it | 1.4 / 1 | 2.9 / 1 | 5.8 / 1 |
| Elevation (relief) | 211-297 m (86) | 211-337 m (126) | 210-354 m (144) |
| Mean slope | 9.8 % | 19.3 % | 22.9 % |
| Footprint coverage | 55 % | 35 % | 17 % |

- **Native:** mill dam site, bridge, industrial yard, marsh, fuel (1.1 km), orchards and fields, quarry (2.1 km), old railway grade.
- **We would add:** a canoe landing at the bridge (none mapped); dirt roads up the coulees (few tagged).
- **Play area:** the old village fits **1 km** easily; fuel and orchards need **2 km**; the quarry is just outside even that.
- **OSM:** 150 footprints, two thirds from the Microsoft import; the old core has gaps, partly real (houses removed after the floods), partly unmapped.
- **DEM:** WI_Statewide_2021 lidar, 1 m.
- **Concerns:** one bridge; little main street (few commercial buildings); thin data outside the core.

### 2.5 Greenwich, New York (upper Hudson valley, Battenkill, Washington County) - rank 5

A dairy-country village on the Battenkill, a few miles east of the Hudson. Along the river at the south of the village are the old **Pleasant Vale Mill**, three named dams and two small hydro plants, the **Mill Hollow Kayak Launch**, the Bridge Street bridge and the yard of a working **short-line railroad**, the only active rail yard in a town on this list. The village above is large and dense (414 footprints in the 1 km, 784 in 2 km). Hills are gentle: 61 m relief in the 1 km, 76 m in 2 km.

| Measure | 1 km | 2 km | 4 km |
|---|---|---|---|
| Buildings (70 m2 or more) | 414 (332) | 784 | 948 |
| Drivable road km / unpaved | 8.1 / 0.4 | 29.0 / 1.9 | 59.3 / 6.7 |
| River km / road bridges over it | 2.0 / 1 | 3.1 / 1 | 6.6 / 2 |
| Elevation (relief) | 91-152 m (61) | 91-167 m (76) | 90-255 m (165) |
| Mean slope | 10.3 % | 8.3 % | 10.6 % |
| Footprint coverage | 90 % | 59 % | 42 % |

- **Native:** mill, dams and hydro plants, rail yard and active railway (4 km on the map), kayak launch, one road bridge (plus two rail bridges), fuel (1.1 km), barns, marsh (1.3 km), tracks.
- **We would add:** a gravel pit in the south hills.
- **Play area:** the 1 km square takes the river front and the south half of the village; the whole village needs 2 km.
- **OSM:** 948 footprints (130 from the Microsoft import) and 695 address points: the best-mapped town here.
- **DEM:** NY_Hudson_D22 lidar (2023), 1 m.
- **Concerns:** too big for the 40-120 core (we would model 300-400 buildings or thin the side streets); the gentlest terrain on the list, at the bottom of the 60-200 m range.

### 2.6 Newport, Pennsylvania (Juniata valley, Perry County) - rank 6

A borough of about 1,500 on the Juniata River, which is about 200 m wide here, with a **busy main-line railway** (OSM: "NS Pittsburgh Line") along the bank (12.7 km of active railway on the map) and a **large quarry 700 m from the centre**. The grid town climbs from the river; one long road bridge (Market Street, about 200 m) crosses to the far bank; steep wooded ridges rise 90-170 m on both sides.

| Measure | 1 km | 2 km | 4 km |
|---|---|---|---|
| Buildings (70 m2 or more) | 115 (109) | 152 | 215 |
| Drivable road km / unpaved | 14.4 / 0 | 32.8 / 2.1 | 90.7 / 5.9 |
| River km / road bridges over it | 0.6 / 1 | 1.9 / 1 | 7.2 / 2 |
| Elevation (relief) | 112-203 m (92) | 111-208 m (97) | 111-285 m (175) |
| Mean slope | 14.3 % | 16.3 % | 15.7 % |
| Footprint coverage | 36 % | 22 % | 13 % |

- **Native:** quarry, fuel, the main-line railway, river bridge, tracks, an industrial yard (1.8 km).
- **We would add:** a mill on Little Buffalo Creek (it runs through the south of the borough), a boat launch by the bridge, a marsh on the river bottom.
- **Play area:** the borough fits 1 km; the 2 km square takes in the quarry.
- **OSM:** only 215 footprints for a town of 1,500 (83 from the Microsoft import): most houses missing, few address points (15).
- **DEM:** PA_South_Central_B1_2017 lidar, 1 m.
- **Concerns:** footprints poor; the river is very wide for one bridge; a divided highway at the north-east corner of the map.

---

## 3. Screened and not shortlisted

| Town | Pop. | Buildings 1 km (70 m2+) / 2 km | Coverage 1 km | River bridges 1 / 2 km | Relief 1 / 2 km | Why not |
|---|---|---|---|---|---|---|
| Enosburg Falls, VT | 1,356 | 414 (342) / 681 | 97 % | 1 / 1 | 28 / 75 m | Too flat in the 1 km; big village |
| Bethel, VT | 670 | 56 (52) / 66 | 37 % | 3 / 3 | 173 / 199 m | Footprints barely mapped (71 on the whole map); narrow steep valley. Has an active railway and the Bethel Mills dam |
| Schuylerville, NY | 1,370 | 382 (355) / 785 | 92 % | 2 / 2 | 39 / 74 m | Too flat in the 1 km; big village |
| Rosendale, NY | n/a | 90 (86) / 184 | 40 % | 1 / 2 | 137 / 139 m | Partial footprints, few farms, a motorway on the map's edge. Strong on drama (cement mines, rail trestle, ridges) |
| McConnelsville, OH | 1,667 | 344 (281) / 641 | 77 % | 1 / 2 | 112 / 112 m | Good, but a bigger town than Stockport in the same valley and without its mill; 1 m DEM covers 72 % of the map |
| Elkader, IA | 1,209 | 65 (51) / 68 | 15 % | 1 / 2 | 82 / 89 m | Footprints barely mapped. Has a mill dam, a whitewater access and a famous stone arch bridge |
| Hermann, MO | 2,751 | 136 (129) / 150 | 58 % | 1 / 2 | 48 / 69 m | Footprints sparse for its size, gentle hills in the 1 km, known wine-tourism town. Has an active main line and station |
| Van Buren, MO | 823 | 16 (13) / 16 | 17 % | 2 / 2 | 64 / 69 m | Almost no footprints mapped |

Their maps can be regenerated in seconds from the cached downloads (`node tools/scout/render-map.mjs <town> --png`, written to `docs/research/new-world/`); the screening renders are in `artifacts\scout\maps\` (not committed).

---

## 4. Ranking, and why Stockport

Scored on fit for the game first, data second:

1. **Stockport** has the brief's central picture natively and in one place: river, dam, the mill on it, the bridge and the landing, a small village, steep wooded hills behind. Everything it lacks (quarry, depot, marsh, woods roads) is an out-of-town place that are easy and natural to add, and the 2 km square has room and terrain for them. Its core data is the best of the small towns (92 % coverage), and it is the only candidate that also works if the world stays at 1 km. Weak points: one bridge, a short main street, no railway.
2. **Hardwick** has the most places natively (all but the quarry), the most bridges and the most dirt roads, and the strongest hills. It needs the 2 km world, and about a third of its houses would be placed by us from address points.
3. **Lanesboro** has the best main street and two bridges plus the dam in a compact centre, but no land use mapped and four places to add, and its bluffs leave little rolling farmland.
4. **Gays Mills** is the right size and has most places, but the data is thin and it has one bridge and little main street.
5. **Greenwich** has the best data and the rail yard, but is two to four times the wanted size with the gentlest hills.
6. **Newport** has the best rail and quarry, but the footprints would mostly be ours.

If the user prefers more bridges and a bigger, busier town, Hardwick is the alternative; if a brick main street matters most, Lanesboro.

---

## 5. Play-area size

**Recommended: 2 km x 2 km (2,048 m).** In every shortlisted town the 1 km square holds the town but not its outskirts: the quarries, industrial yards, farms and woods roads that give haul jobs their distance sit 0.7-2 km out. A 1 km world makes most hauls a few hundred metres long; 2 km gives 1-3 km trips over real hills with the town in the middle. A 4 km square would add mostly empty ridges and is not needed for any of the six.

What a 2 km world means for the current design (PLAN §2.6 and §5.3): the WorldGrid is 2049² vertices at 0.5 m over 1,024 m (about 113 MB of layers). At 0.5 m over 2,048 m it becomes 4097² (about 450 MB, client and server) with 1,024 terrain chunks instead of 256; the net position field (21 bits over 1,024 m) would cover 2,048 m at about 1 mm. These are frozen contracts, so the change needs an ADR (AGENTS rule 6). A cheaper variant keeps 0.5 m in the central 1 km and 1 m outside. If the world must stay at 1 km for now, Stockport still works: the 1 km square holds the village, mill, bridge, launch and fields, and the added places would go inside it.

---

## 6. Next steps once the user picks

1. Fetch the 1 m lidar DEM tiles for the chosen 2 km square (3DEP, approved under U25) and re-fetch OSM for exactly that square.
2. Decide footprint gaps: place buildings on address points and empty lots ourselves, or approve one more download, the Microsoft US Building Footprints (ODbL), to fill them. Stockport needs neither in the core.
3. Move fixed places (the proposed quarry, depot, marsh, landing) into `towns.json` as the starting layout; the world lane turns them into lots.

---

## 7. Credits, licences and reproduction

- Map data **(c) OpenStreetMap contributors, ODbL** (Open Database Licence 1.0, `licenses\`). Downloaded through the public Overpass API (overpass-api.de), one query per town, 2026-10-19/20.
- Elevation: **USGS 3D Elevation Program (public domain)**, 3DEPElevation ImageServer (elevation.nationalmap.gov), best-available mosaic sampled at 5 m.
- Raw data is cached under `artifacts\scout\<town>\` (gitignored): `osm.json` and `osm.meta.json` (query, server, timestamp), `dem.f32` and `dem.json`, `coverage.json` (3DEP catalog), `stats.json` (all numbers in this note).

Reproduce (Node only, no installs; the PNG uses the Edge already on the PC, headless):

```
node tools/scout/fetch-osm.mjs <town-id> | --all       # cached; --force to refetch
node tools/scout/fetch-dem.mjs <town-id> | --all
node tools/scout/render-map.mjs <town-id> | --all [--png]
node tools/scout/render-map.mjs --summary              # the screening table
```

Download notes: the main Overpass server was often at capacity (HTTP 502); the script's backoff (20 s, doubling) got every town from it within a few tries, so the mirror fallbacks were never needed. The 3DEP service answered every request at once.
