# 08b — Storeys and roofs per building type (for lane A's Blender buildings)

Research date 2026-10-19 (lane R research, branch `m3/rs-sites-research`), decision **U28** (a second OSM download of about 15 towns with dense storey and roof tags). Map data **(c) OpenStreetMap contributors, ODbL 1.0**. Companion of `08-site-layouts.md` (same tools, same business types). Results: `Game/Assets/Content/sites/buildingProfile.json`, fields `storeys`, `roofShape`, `roofLevels`, `roofOrientation`, `height`, `eaveHeight`, `storeyHeight`, `roofPitchDeg` of every key, each with its sample size, the towns behind it (`towns: {town: n}`) and a split by region (`na` North America, `eu` Germany).

## 0. Summary

**In plain words.** To build Millbrook's houses, shops and barns at the right height and with the right roofs, lane A needs to know how many storeys each kind of building has and what its roof looks like. The first 17 towns had this on fewer than 1 % of buildings. We checked 36 candidate towns with one small count-only request, then downloaded the 14 with the most such data, led by Marietta, Ohio (where the Muskingum meets the Ohio, 60 km from Stockport), Zanesville and Coshocton (upstream on the Muskingum) and two German towns whose mappers record roof heights. The result: storey counts and roof shapes per building type from North America, and eave heights and roof pitches from the German towns, which serve only as a range check because their roofs are steeper than American ones.

Key numbers (North America unless marked; n = buildings with the tag):

| Type | Storeys | Roof shape |
|---|---|---|
| House (n 1,330 storeys / 358 roofs; Marietta 590, Wolfville 306, Park City 104) | 1: 32 %, 2: 60 %, 3: 6 %, 4+: 1 % | gabled 59 %, pyramidal 12 %, complex 10 %, saltbox 8 %, hipped 6 % |
| Downtown shop (244 / 16) | 1: 25 %, 2: 44 %, 3: 25 %, 4+: 6 % | flat 69 % [thin] |
| Standalone shop (177 / 17) | 1: 50 %, 2: 31 %, 3: 15 % | gabled 59 %, flat 18 % [thin] |
| Apartments (103) | 2: 40 %, 3: 34 %, 4+: 16 % | (Germany: gabled 79 %) |
| Office (65), civic (38) | mostly 1-2 | flat or gabled [thin] |
| Car repair (29) | 1: 90 % | |
| School (66) | 2: 32 %, 4+: 35 % (Athens' campus buildings) | |
| Measured eave and pitch (Germany only): house | eave 6.0 m (6-8), storey 3.0 m (2.7-3.0), n 42-64 | gabled pitch 48° (32-54), n 49 |

Coverage over both studies: 60,939 buildings, 5,809 with storeys, 3,972 with a roof shape, 247 with a height, 181 with a measurable eave and 163 with a measurable pitch. **North America has almost no heights** (25 buildings, all sheds), so for American eaves and pitches lane A uses the standards of §5, bounded above by the German measurements; real Stockport heights could come from the 3DEP lidar (design §14 #5).

## 1. Method

1. **Screening** (one count-only Overpass request, no map data; `fetch-tags.mjs --screen`, cached as `artifacts/osm/screen-tags.json`): for 36 candidate towns, how many buildings lie within 1.5 km of the centre and how many carry `building:levels`, `roof:shape`, `height`, and how many `building:part` ways exist. Candidates: Ohio and Ohio-river towns first (the region of Stockport), then older small towns across the US known for careful mapping, then Canadian and German small towns in case the US was too thin.
2. **Choice:** the 14 towns with the most storey and roof tags, keeping every regional match that had any (table in §2). Two German towns are in because North America has almost no heights: they are the only source of measured eave heights and pitches.
3. **Download** (`fetch-tags.mjs`): one Overpass request per town within 1.6 km: every building and `building:part` (ways and multipolygons, all tags), streets (for the street frame), land use, amenity areas and shop, amenity, craft, office, tourism and entrance points (for the business type). Cached under `artifacts/osm/raw-tags/` (gitignored, 0.8-5.7 MB per town). The servers refused Elora once (HTTP 502); the retry fetched it.
4. **Per building** (`threeDOf` in `measure.mjs`): its own tags, else those of its largest `building:part` that has them.
   - storeys = `building:levels` (1-29);
   - roof shape = `roof:shape`;
   - eave height = `height` − `roof:height` on the same element; for a flat roof, the height;
   - storey height = (eave − `min_height`) / storeys;
   - pitch = `roof:angle`, else atan(`roof:height` / half span). The span is the footprint's short side, or the long side with `roof:orientation=across`; a skillion uses the full span. Gabled, hipped, half-hipped, pyramidal, saltbox and skillion roofs only.
5. **Keys and regions:** the same business types and OSM tag keys as `08` §9. Footprint, shape, setback and door numbers stay North American only (the 17 site towns + the 12 North American towns here). Storeys, roofs, heights, eaves and pitches pool every town and are also given per region (`na`, `eu`), each with its towns.
6. **Repeatability:** `node tools/osm/measure.mjs` re-creates `buildingProfile.json` (one line per key) byte for byte from the two caches.

## 2. Towns

Screening (counts within 1.5 km; buildings / with `building:levels` / with `roof:shape` / with `height` / `building:part` ways), sorted by storey and roof tags:

| Candidate | Buildings | Levels | Roof shape | Height | Parts |
|---|---|---|---|---|---|
| Rothenburg ob der Tauber, Germany | 4,977 | 2,970 | 3,022 | 63 | 77 |
| Marietta, Ohio | 4,175 | 668 | 265 | 0 | 0 |
| Dinkelsbuehl, Germany | 4,349 | 307 | 344 | 137 | 127 |
| Wolfville, Nova Scotia | 931 | 321 | 2 | 0 | 1 |
| Zanesville, Ohio | 563 | 317 | 0 | 0 | 0 |
| Park City, Utah | 1,672 | 194 | 82 | 0 | 0 |
| Staunton, Virginia | 2,551 | 196 | 0 | 1 | 13 |
| Athens, Ohio | 3,355 | 83 | 0 | 7 | 0 |
| Coshocton, Ohio | 349 | 68 | 0 | 0 | 0 |
| Jim Thorpe, Pennsylvania | 957 | 27 | 31 | 0 | 2 |
| Port Townsend, Washington | 2,929 | 39 | 1 | 1 | 2 |
| Hanover, New Hampshire | 1,178 | 33 | 3 | 1 | 7 |
| New Hope, Pennsylvania | 1,735 | 35 | 0 | 0 | 12 |
| Elora, Ontario | 1,288 | 22 | 1 | 0 | 0 |
| Monschau, Germany | 1,539 | 9 | 9 | 1 | 0 |
| Provincetown, Massachusetts | 1,954 | 16 | 1 | 2 | 2 |
| Stillwater, Minnesota | 764 | 6 | 10 | 0 | 0 |
| Hood River, Oregon | 3,279 | 15 | 0 | 0 | 1 |
| Harpers Ferry, West Virginia | 674 | 14 | 0 | 0 | 0 |
| Niagara-on-the-Lake, Ontario | 2,122 | 14 | 0 | 0 | 15 |
| Red Wing, Minnesota | 610 | 9 | 0 | 0 | 0 |
| Granville, Ohio | 264 | 6 | 0 | 0 | 1 |
| Lexington, Virginia | 2,103 | 4 | 1 | 0 | 0 |
| Jackson, Wyoming | 2,336 | 5 | 0 | 0 | 0 |
| Madison, Indiana | 1,218 | 3 | 0 | 0 | 0 |
| Lititz, Pennsylvania | 2,807 | 3 | 0 | 1 | 0 |
| Perth, Ontario | 2,278 | 2 | 1 | 0 | 0 |
| Fredericksburg, Texas | 674 | 2 | 0 | 0 | 0 |
| Telluride, Colorado | 1,218 | 2 | 0 | 0 | 0 |
| McConnelsville, Ohio | 909 | 0 | 1 | 0 | 0 |
| Pomeroy, Ohio | 1,215 | 1 | 0 | 0 | 0 |
| Point Pleasant, West Virginia | 605 | 1 | 0 | 0 | 0 |
| Gallipolis, Ohio | 1,423 | 0 | 0 | 0 | 0 |
| Cape May, New Jersey | 3,340 | 0 | 0 | 0 | 0 |
| Nantucket, Massachusetts | 4,019 | 0 | 0 | 1 | 4 |
| Stowe, Vermont | 545 | 0 | 0 | 0 | 4 |

Chosen, and why (from `CHOSEN` in `tools/osm/fetch-tags.mjs`):

| Town | Why |
|---|---|
| Marietta, Ohio | mouth of the Muskingum, 60 km downstream of Stockport; the densest US sample |
| Zanesville, Ohio | Muskingum river town upstream of Stockport |
| Coshocton, Ohio | head of the Muskingum |
| Athens, Ohio | southeast Ohio hill town 40 km from Stockport |
| Staunton, Virginia | Appalachian valley town with a 19th-century downtown |
| Jim Thorpe, Pennsylvania | river town in steep hills, storeys and roof shapes both mapped |
| New Hope, Pennsylvania | Delaware river town, with building parts |
| Hanover, New Hampshire | New England village |
| Park City, Utah | mountain small town, the second-best US roof sample |
| Port Townsend, Washington | Victorian port town |
| Wolfville, Nova Scotia | Canadian small town with dense storeys |
| Elora, Ontario | Ontario river village |
| Rothenburg ob der Tauber, Germany | the densest 3D tagging found (storeys and roof shapes on ~60 % of buildings); roof shapes per type, not North American proportions |
| Dinkelsbuehl, Germany | heights, roof shapes and building parts; the main source of measured roof pitch and eave height |

Not chosen: McConnelsville, Pomeroy, Gallipolis (Ohio river and Muskingum towns, the closest to Stockport) and Point Pleasant had almost no storey or roof tags; Cape May, Nantucket, Stowe, Madison IN, Lititz, Fredericksburg TX, Telluride, Jackson, Lexington VA and Perth ON had fewer than 10 of each; Monschau had 9.

Coverage after the download (every classified building of both studies; storeys / roof shape / height):

| Town | Buildings | With storeys | With roof shape | With height |
|---|---|---|---|---|
| Athens, Ohio | 3,505 | 87 | 0 | 0 |
| Coshocton, Ohio | 389 | 68 | 0 | 0 |
| Dinkelsbuehl, Germany | 4,531 | 320 | 367 | 158 |
| Elora, Ontario | 1,331 | 22 | 1 | 0 |
| Hanover, New Hampshire | 1,243 | 35 | 3 | 1 |
| Jim Thorpe, Pennsylvania | 1,013 | 49 | 49 | 0 |
| Marietta, Ohio | 4,616 | 699 | 269 | 0 |
| New Hope, Pennsylvania | 1,800 | 36 | 0 | 0 |
| Park City, Utah | 1,763 | 195 | 82 | 0 |
| Port Townsend, Washington | 3,184 | 40 | 2 | 1 |
| Rothenburg ob der Tauber, Germany | 5,040 | 3,086 | 3,139 | 64 |
| Staunton, Virginia | 2,710 | 197 | 0 | 0 |
| Wolfville, Nova Scotia | 976 | 356 | 2 | 0 |
| Zanesville, Ohio | 592 | 335 | 0 | 0 |
| Bar Harbor, Maine (site study) | 1,271 | 45 | 11 | 0 |
| Barre, Vermont (site study) | 3,665 | 7 | 0 | 0 |
| Bellefontaine, Ohio (site study) | 1,110 | 0 | 0 | 0 |
| Cle Elum, Washington (site study) | 1,991 | 5 | 0 | 0 |
| Decorah, Iowa (site study) | 307 | 8 | 0 | 0 |
| Ely, Minnesota (site study) | 2,566 | 4 | 0 | 0 |
| Galena, Illinois (site study) | 749 | 5 | 0 | 0 |
| Hillsboro, Kansas (site study) | 423 | 0 | 0 | 0 |
| Lander, Wyoming (site study) | 3,702 | 73 | 0 | 0 |
| Lunenburg, Nova Scotia (site study) | 1,120 | 9 | 1 | 0 |
| Marfa, Texas (site study) | 847 | 3 | 17 | 23 |
| Mount Forest, Ontario (site study) | 203 | 0 | 0 | 0 |
| Nevada City, California (site study) | 556 | 24 | 4 | 0 |
| Salida, Colorado (site study) | 5,677 | 5 | 0 | 0 |
| Viroqua, Wisconsin (site study) | 2,819 | 4 | 3 | 0 |
| Wallace, Idaho (site study) | 613 | 86 | 22 | 0 |
| Woodstock, Vermont (site study) | 627 | 6 | 0 | 0 |
| **All** | **60,939** | **5,809** | **3,972** | **247** |

## 3. Storeys per type (North America)

Share of buildings by storey count (`building:levels`), North American towns only; n = buildings with the tag; the main towns behind the number; the last column is the German sample size for the same type (not used for storeys).

| Type | n | 1 | 2 | 3 | 4+ | Main towns | n (eu) |
|---|---|---|---|---|---|---|---|
| house | 1330 | 32 % | 60 % | 6 % | 1 % | Marietta 590, Wolfville 306, Park City 104 | 1454 |
| accessory | 52 | 90 % | 8 % | 2 % | 0 % | Wolfville 22, Staunton 11, Port Townsend 5 | 232 |
| apartments | 103 | 11 % | 40 % | 34 % | 16 % | Wolfville 25, Park City 15, Athens 12 | 1118 |
| downtown shop | 244 | 25 % | 44 % | 25 % | 6 % | Zanesville 64, Staunton 35, Park City 34 | 170 |
| downtown restaurant | 53 | 26 % | 43 % | 23 % | 8 % | Park City 19, Staunton 7, Bar Harbor 5 | 43 |
| downtown office | 59 | 8 % | 56 % | 25 % | 10 % | Marietta 16, Zanesville 16, Staunton 6 | 34 |
| downtown civic | 20 | 15 % | 45 % | 30 % | 10 % | Staunton 7, Zanesville 5, Marietta 3 | 24 |
| downtown motel | 19 [thin] | 0 % | 32 % | 37 % | 32 % | Park City 5, Staunton 3, Wallace 2 | 118 |
| shop | 177 | 50 % | 31 % | 15 % | 5 % | Zanesville 84, Coshocton 32, Lander 11 | 48 |
| restaurant | 35 | 57 % | 29 % | 14 % | 0 % | Zanesville 11, Port Townsend 5, Marietta 4 | 4 |
| fast food | 7 [thin] | 71 % | 0 % | 14 % | 14 % | Athens 2, Zanesville 2, Decorah 1 | 2 |
| office | 65 | 37 % | 45 % | 18 % | 0 % | Zanesville 34, Marietta 16, Staunton 6 | 28 |
| supermarket | 1 [thin] | 100 % | 0 % | 0 % | 0 % | Zanesville 1 | 3 |
| fuel shop | 1 [thin] | 100 % | 0 % | 0 % | 0 % | Nevada City 1 | 4 |
| car repair | 29 | 90 % | 7 % | 3 % | 0 % | Zanesville 21, Coshocton 3, Marietta 3 | 3 |
| motel | 28 | 4 % | 39 % | 43 % | 14 % | Bar Harbor 7, Park City 6, Zanesville 6 | 24 |
| church | 18 [thin] | 44 % | 39 % | 17 % | 0 % | Zanesville 15, Staunton 3 | 2 |
| school | 66 | 11 % | 32 % | 23 % | 35 % | Athens 31, Zanesville 8, Hanover 7 | 28 |
| civic | 38 | 45 % | 34 % | 18 % | 3 % | Zanesville 15, Marietta 6, Lander 3 | 19 |
| warehouse | 29 | 38 % | 28 % | 14 % | 21 % | Staunton 16, Zanesville 6, Elora 4 | 21 |
| industrial | 17 [thin] | 59 % | 35 % | 0 % | 6 % | Marietta 7, Zanesville 6, Elora 2 | 6 |
| farm | 1 [thin] | 0 % | 100 % | 0 % | 0 % | Staunton 1 | 11 |
| mill | 0 | - | - | - | - | - | 0 |

## 4. Roof shapes per type

Shares in % of buildings with `roof:shape` (top four), North America and Germany separately. `many` = a complex roof; `quadruple_saltbox` and `pyramidal` are common Marietta and Rothenburg mapping habits for hip-like roofs on square houses.

| Type | n (na) | North America | n (eu) | Germany |
|---|---|---|---|---|
| house | 358 | gabled 59, pyramidal 12, many 10, quadruple saltbox 8 | 1465 | gabled 84, hipped 6, half-hipped 5 |
| accessory | 7 [thin] | gabled 29, skillion 29, hipped 29, flat 14 | 271 | gabled 47, flat 37, pyramidal 6 |
| apartments | 7 [thin] | gabled 57, flat 14, quadruple saltbox 14, many 14 | 1152 | gabled 79, half-hipped 9, hipped 6 |
| downtown shop | 16 [thin] | flat 69, gabled 13, quadruple saltbox 6, many 6 | 174 | gabled 74, flat 8, half-hipped 8 |
| downtown restaurant | 6 [thin] | flat 67, hipped 17, double saltbox 17 | 43 | gabled 79, half-hipped 14, flat 2 |
| downtown office | 7 [thin] | flat 57, gabled 43 | 35 | gabled 71, flat 17, hipped 6 |
| downtown civic | 1 [thin] | flat 100 | 25 | gabled 48, hipped 28, half-hipped 12 |
| downtown motel | 3 [thin] | hipped 33, flat 33, mansard 33 | 118 | gabled 64, half-hipped 14, gambrel 7 |
| shop | 17 [thin] | gabled 59, flat 18, hipped 12, many 6 | 48 | gabled 60, flat 23, half-hipped 8 |
| restaurant | 9 [thin] | flat 33, gabled 22, many 22, quadruple saltbox 11 | 4 | gabled 50, hipped 25, flat 25 |
| fast food | 0 | - | 2 | flat 50, half-hipped 50 |
| office | 9 [thin] | flat 33, many 33, pyramidal 11, hipped 11 | 26 | gabled 50, flat 31, hipped 12 |
| supermarket | 0 | - | 4 | gabled 75, flat 25 |
| fuel shop | 0 | - | 4 | flat 50, many 25, gabled 25 |
| car repair | 1 [thin] | flat 100 | 3 | flat 100 |
| motel | 2 [thin] | gabled 100 | 27 | gabled 56, hipped 30, flat 15 |
| church | 1 [thin] | gabled 100 | 8 | gabled 88, hipped 13 |
| school | 4 [thin] | flat 50, gabled 25, many 25 | 28 | flat 32, skillion 25, gabled 25 |
| civic | 7 [thin] | gabled 43, flat 29, hipped 14, quadruple saltbox 14 | 20 | gabled 55, flat 25, hipped 10 |
| warehouse | 5 [thin] | gabled 60, round 40 | 20 | gabled 60, flat 25, half-hipped 5 |
| industrial | 5 [thin] | gabled 60, flat 40 | 6 | flat 50, gabled 33, many 17 |
| farm | 0 | - | 14 | gabled 79, hipped 14, half-hipped 7 |
| mill | 0 | - | 0 | - |

Roof levels (`roof:levels`, storeys inside the roof; all regions, mostly Rothenburg): house: 0×389 1×1240 2×537 3×13 4×1 1.5×1 (n 2181); accessory: 0×97 1×82 2×16 3×12 4×2 (n 209); apartments: 0×41 1×357 2×677 3×64 4×1 (n 1140); downtown shop: 0×79 1×26 2×111 3×20 4×1 (n 237); downtown restaurant: 0×24 1×9 2×29 3×8 (n 70); downtown office: 0×26 1×6 2×20 3×3 (n 55); downtown civic: 0×6 1×2 2×13 3×4 4×2 (n 27); downtown motel: 0×12 1×26 2×70 3×17 4×1 (n 126); shop: 0×37 1×18 2×8 3×4 (n 67); restaurant: 0×6 1×5 2×1 3×1 (n 13); office: 0×20 1×11 2×10 (n 41); car repair: 0×6 (n 6); motel: 0×10 1×15 2×10 3×1 (n 36); school: 0×14 1×8 2×9 3×1 (n 32); civic: 0×8 1×4 2×7 3×2 (n 21); warehouse: 0×1 1×4 2×6 3×3 (n 14); industrial: 0×11 1×1 (n 12); farm: 0×3 1×2 2×5 3×1 (n 11)

Ridge orientation (`roof:orientation`): house: across 71, along 29 (n 7) (almost never tagged; houses take the ridge along the long side unless the footprint is nearly square).

## 5. Heights, eaves and pitch

Measured where the tags allow (n = buildings): building height, eave height, storey height, pitch (median and middle half; by roof shape where n ≥ 3), and the main towns. Almost all of it comes from the German towns.

| Type | Height n | Height m | Eave n | Eave m | Storey n | Storey height m | Pitch n | Pitch ° | Pitch by shape | Towns |
|---|---|---|---|---|---|---|---|---|---|---|
| house | 89 (na 1) | 10.0 (7.0-13.0) | 64 | 6.0 (6.0-8.0) | 42 | 3.00 (2.67-3.00) | 62 | 48 (33-53) | gabled 48 (n 49); hipped 49 (n 3); pyramidal 51 (n 3); skillion 48 (n 6) | Dinkelsbuehl 48, Rothenburg ob der Tauber 16 |
| accessory | 74 (na 24) | 6.0 (2.5-15.0) | 38 | 5.0 (3.3-14.0) | 5 | 3.00 (3.00-3.00) | 22 | 55 (49-61) | pyramidal 59 (n 16); gabled 45 (n 3) | Rothenburg ob der Tauber 25, Dinkelsbuehl 13 |
| apartments | 22  | 14.0 (11.0-15.0) | 21 | 7.0 (6.0-8.0) | 20 | 3.00 (2.67-3.33) | 21 | 53 (50-58) | gabled 53 (n 21) | Dinkelsbuehl 21 |
| downtown shop | 26  | 15.0 (13.0-16.0) | 26 | 9.0 (7.0-10.0) | 24 | 3.25 (3.00-3.33) | 26 | 46 (40-54) | gabled 46 (n 26) | Dinkelsbuehl 24, Rothenburg ob der Tauber 2 |
| downtown restaurant | 9  | 14.0 (13.0-15.0) | 9 | 8.0 (6.0-9.0) | 9 | 2.75 (2.67-3.00) | 9 | 50 (46-55) | gabled 50 (n 9) | Dinkelsbuehl 9 |
| downtown office | 2  | 15.0 (14.0-15.0) | 2 | 9.0 (8.0-9.0) | 2 | 3.00 (2.67-3.00) | 2 | 46 (39-46) | - | Dinkelsbuehl 2 |
| downtown civic | 4  | 18.0 (14.0-18.0) | 4 | 9.0 (8.0-9.0) | 2 | 4.00 (3.00-4.00) | 4 | 45 (34-45) | gabled 45 (n 3) | Dinkelsbuehl 3, Rothenburg ob der Tauber 1 |
| downtown motel | 4  | 16.0 (15.0-16.0) | 4 | 7.0 (6.0-7.0) | 4 | 3.00 (3.00-3.00) | 4 | 55 (53-55) | gabled 55 (n 4) | Dinkelsbuehl 4 |
| shop | 6  | 12.5 (6.0-12.5) | 4 | 7.0 (7.0-7.0) | 4 | 3.25 (2.50-3.25) | 4 | 55 (51-55) | gabled 55 (n 4) | Dinkelsbuehl 4 |
| church | 6  | 16.0 (10.0-38.0) | 6 | 10.0 (5.0-20.0) | 1 | 3.00 (3.00-3.00) | 6 | 40 (24-51) | gabled 51 (n 4) | Dinkelsbuehl 4, Rothenburg ob der Tauber 2 |
| civic | 3  | 10.0 (10.0-28.0) | 1 | 3.0 (3.0-3.0) | 1 | 3.00 (3.00-3.00) | 1 | 36 (36-36) | - | Dinkelsbuehl 1 |
| farm | 2  | 4.0 (4.0-4.0) | 2 | 3.0 (3.0-3.0) | 0 | - | 2 | 21 (21-21) | - | Dinkelsbuehl 2 |

**What lane A uses for North America** (where OSM has no heights) [doc: typical North American construction; to be checked against Stockport's lidar if the user approves it, design §14 #5]:

| Building | Storey height (floor to floor) | Plinth above grade | Roof pitch |
|---|---|---|---|
| House, pre-1940 (most of an Ohio river village) | 3.0-3.4 m (9-10 ft ceilings) | 0.5-0.9 m | gabled 8/12-12/12 (34-45°); cross gables up to 14/12 (49°) |
| House, post-1945 (ranch, split level) | 2.7-3.0 m | 0.3-0.5 m | 4/12-6/12 (18-27°) |
| Downtown commercial block (19th-early 20th c.) | ground floor 3.7-4.9 m, upper floors 3.0-3.7 m | at grade | flat or low slope behind a parapet 0.6-1.2 m high |
| Standalone shop, fast food, fuel shop | 3.7-4.6 m (one storey) | at grade | flat with parapet, or 3/12-5/12 (14-23°) |
| Church | nave 6-9 m to the eave | 0.5-1.0 m | 10/12-14/12 (40-49°) |
| Barn, machine shed | 4-6 m to the eave | at grade | gambrel (lower 60°, upper 25°) or gabled 4/12-6/12 |
| Warehouse, industrial | 5-9 m to the eave | dock height 1.2 m where docks | 1/12-2/12 (5-10°) or flat |

The German measurements bound these from above: their houses have the same 3.0 m storeys but steeper roofs (gabled 48°, middle half 32-54°), so an American house roof above ~50° should be rare.

## 6. How lane A should use this

1. The building's own Stockport tags first (S0's extract), then its key's `na` numbers when n ≥ 20, then the type key, then `type:house` / `type:shop`.
2. Draw storeys and roof shape from the `na` histogram and shares with the site seed, so each building is stable across bakes. Read `many` as "complex" (an L or T house with crossing gables) and `quadruple_saltbox` / `pyramidal` on small square houses as hipped.
3. Eave height = storeys × storey height (table above) + plinth; ridge height from the pitch and the span. Check against the `eu` measured ranges, which should bound American roofs from above.
4. Prefer `type:` keys for sizes and `building:`, `shop:` or `amenity:` keys when a building carries a specific tag (for example `shop:hardware`, `amenity:place_of_worship`).

## 7. Limitations

- **North America has almost no heights:** see the coverage table. Pitch and eave numbers are German and only bound American ones; the recommended values are standards, not measurements.
- **One mapper per town** often adds most storey tags (Marietta's 699, Wolfville's 356), so a town's style weighs heavily. Marietta is the nearest large sample to Stockport, which is the right bias for this game.
- Roof shapes on downtown and commercial buildings are thin in North America (16-17 per type); the flat-roof majority downtown matches the regional norm (parapet commercial blocks).

## Credit

Map data (c) OpenStreetMap contributors, Open Database License (ODbL) 1.0, https://www.openstreetmap.org/copyright. `buildingProfile.json` is a derived database of OSM data and carries the same credit.
