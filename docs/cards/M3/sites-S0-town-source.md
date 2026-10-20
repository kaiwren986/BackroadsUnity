# sites-S0: town source — one OSM extract, typed buildings, hashed `TownData` (I, or W, main, ≈ 2 AD) — draft

**Inputs:** the town: Stockport, Ohio (`TOWN_ID` `stockport-oh`, `TOWN_NAME` Millbrook; the 2 km × 2 km play area and the extract box are in the overview's parameters), `tools/osm/` (fetch, lib, measure: the Overpass query, the building classifier `typeOf`, the street frame), the ODbL credit rule (PLAN §3.10, `Licence.OsmAttribution`). The single Stockport extract is approved; the integrator runs this card.

**Steps**
1. `tools/osm/town-extract.mjs`: one Overpass request for `EXTRACT_BBOX`, the play area + 250 m (the study query of `fetch.mjs` as a bounding box instead of radii, plus building parts as in `fetch-tags.mjs`, plus every `highway` way with all tags, `barrier` nodes, `crossing` nodes and `highway=stop|give_way|traffic_signals|mini_roundabout` nodes), cached under `artifacts/osm/town/` (never committed raw).
2. Convert to the local frame (`ORIGIN_LATLON`, metres, x east, z north; a transverse-Mercator projection centred on the origin, documented with its formula so C# can reproduce it).
3. Classify every building with the study's rules (business type, `form` = streetfront/standalone, sub-type), attach the shops and amenities mapped as points inside or within 12 m, and record the land use around it. Unknowns stay `unknown` with the reason; nothing is guessed silently.
4. Write `TownData/<TOWN_ID>/v1/` (`manifest.json`, `roads.json`, `buildings.json`, `sites.json`, `credit.txt`; see the overview's contract 1). LF only, sorted keys, numbers rounded to mm, so the hashes are stable. `TownData/**` joins the hashed trees of AGENTS.md rule 7 (`-text`, no CR) and `Repo.NoCrInHashedFiles`.
5. Run `measure.mjs` on the extract too and write `TownData/<TOWN_ID>/v1/profile.json` (the town's own numbers), which sites-S3 blends with the 17-town profile (`PROFILE_BLEND`).
6. C# `TownDataImporter` (editor) reads the folder, verifies the hashes, and exposes typed records to R0o, S1 and lane A; `BusinessType` enum and the classifier fixture test (the same 200 buildings classified by Node and by C# must agree).

**Done when:** `TownData` for the picked town is committed with its manifest; a second extract run on the same cached answer gives identical hashes; the classifier fixture test passes; the credit line appears in the game's Credits and in `THIRD_PARTY_NOTICES`.

**Gate:** canon-identity style check `TownData.ManifestHashes` (replaces `Canon.ManifestHashes` for the new world; CanonData v1 keeps its own check as the physics test world).
