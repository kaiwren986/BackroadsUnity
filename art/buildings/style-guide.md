# Building style guide (card T3.4, phase 1)

What makes our generated buildings sit beside the Synty City buildings and props as one family, written as numbers the
generator (`art/buildings/gen/`) follows. **Synty-matched means matched in style only**: every mesh and every colour
here is our own. The Synty numbers below were measured in Unity from the imported POLYGON City prefabs
(`SyntyStyleAudit`, numbers only; AGENTS.md rule 3); no Synty mesh, texture or render was traced, converted, committed
or uploaded, and no colour was copied from a Synty texture.

## 1. What Synty City buildings are made of (measured)

`art\pipeline\unity-art.ps1 -Steps synty-style` writes `artifacts/art/synty-style.json` (local, gitignored). POLYGON
City 1.12.5, folder `Prefabs/Buildings`, 76 prefabs, 153,661 triangles, measured 2026-10-22 on Unity 6000.3.20f1.

| What | Measured |
|---|---|
| Materials | one per building (`PolygonCity_01_A`, Synty's `Generic_Basic` shader graph); shop fronts add a second, `Glass_01` |
| Texture | one 1024 × 1024 colour atlas; faces take flat colour from it (no detail textures, no normal maps on walls) |
| Saturation (area-weighted, all faces) | p10 0.02, p25 0.12, **p50 0.18**, p75 0.40, p90 0.45 |
| Value (sRGB texture value) | p10 0.24, p25 0.29, **p50 0.53**, p75 0.60, p90 0.60 (brightest swatch about 0.68) |
| Hues of the saturated faces | two families: warm browns and tans (30-40°) and the window blue-grey (about 207-210°) |
| Glass | one blue-grey swatch, h 210°, s 0.44, v 0.58 |
| Windows (glass panes) | width p25 0.61, **p50 1.02**, p75 1.25 m; height p25 0.65, **p50 1.59**, p75 2.15 m |
| Doors (glass doors at the ground) | width p50 1.84 m (double doors), height p50 2.74 m |
| Storey pitch (window rows) | p25 2.50, **p50 2.76**, p75 2.95 m; modular pieces are 3.0 m (apartments) and 3.19 m (old offices) tall |
| Horizontal bands (cornices, string courses, sills seen from the front) | height p25 0.12, **p50 0.19**, p75 0.34 m |
| Ledges (top faces of sills, cornices, copings) | depth p25 0.11, **p50 0.16**, p75 0.28 m |
| Triangles | a 5 m shop module 160-720; a 5 × 3 m apartment module 450-1,030; whole buildings 4,000-18,000 (city hall 12,604, old office 11,748-17,936) |
| Shading | flat (hard edges everywhere), no bevels on walls; detail is geometry: frames, sills, cornices and ledges stand proud of the wall |

## 2. Our palette (`art/buildings/palette/palette.json`, `Game/Assets/Art/Buildings/Textures/T_Bld_Palette.png`)

- One 512 × 512 atlas of **98 flat swatches** (16 × 16 grid of 32 px cells), drawn by `palette.py` from our own list of
  Ohio river-village colours: painted clapboard (16), brick and painted brick (10), stone and concrete (6), roofs
  (asphalt shingle, standing seam and corrugated metal, tar and membrane: 16), trim (10), doors (8), metal wall panels
  (6), barn boards (4), shopfronts, signs and awnings (9) and details (porch floor and ceiling, lattice, steel, rubber,
  dock yellow: 13).
- **Limits taken from section 1**: saturation at most 0.62 and value at most 0.76 for every swatch (`palette.py`
  refuses to write the atlas otherwise). Ours, per swatch (Synty's figures are area-weighted): saturation median 0.24,
  highest 0.61; value median 0.48, highest 0.75; walls 0.43-0.73, brick 0.42-0.70, roofs 0.18-0.67, trim 0.17-0.75.
- **Glass** is its own material, `M_Bld_Glass` = #4b6074 (h 209°, s 0.35, v 0.45), a step darker than Synty's glass so
  lit windows (M5) read against it.
- **UVs**: every face sits inside one swatch's inner 24 × 24 px (4 px margin, so mips down to 4 px per swatch stay
  clean), with a tiny planar projection instead of a single point (no degenerate UVs). Import: sRGB, bilinear, clamp,
  mips, no compression (in the texture's `.meta`).
- **Variation** comes from choosing swatches per building (seeded), never from tinting: one material, one draw call per
  LOD for the opaque body.

## 3. Materials, shading and detail

- **At most three materials per model** (Art.BuildingsBudget); we use two: `M_Bld_Atlas` (URP Lit, the atlas,
  smoothness 0.12) and `M_Bld_Glass` (URP Lit, smoothness 0.85). The generator writes these names and
  `BackroadsModelPostprocessor` remaps them to `Assets/Art/Materials/Buildings/` on the first import.
- Flat shading, no bevels (Synty's walls have none either); detail stands proud of the wall as geometry, sized like
  Synty's bands and ledges:

| Detail | Size (our numbers) | Synty reference |
|---|---|---|
| Window frame (houses, mill, barns) | 0.09 m wide (mill 0.12), 0.035 m proud | bands p50 0.19 m |
| Window sill | frame width + 0.04 each side, 0.06 m tall, 0.08 m proud | ledges p50 0.16 m |
| Brick lintel / sill (blocks) | 0.22 m tall, 0.04 proud / 0.08 tall, 0.07 proud | bands 0.12-0.34 m |
| Corner boards (houses) | 0.14 m each face, 0.03 m proud | |
| Water table / frieze (houses) | 0.18 m at the floor line / 0.25 m under the eave, trim colour | |
| Cornice (blocks) | 0.35 m tall, 0.30 m proud, 0.5 m under the parapet top | ledges p75 0.28 m |
| Parapet coping | 0.12 m tall, 0.05 m overhang outside, 0.04 m inside | |
| Roof slab (fascia) | 0.18 m houses, 0.20 m mill, 0.14 garages, 0.12 metal roofs | |
| Window and door reveals | 0.12 m windows, 0.10 m shop windows, 0.16 m doors | |
| Muntins | 0.07 m bars (houses 2-over-2, mill 6-over-6 look) | |

## 4. Sizes per type (real scale; storeys and roofs from research 08b, `buildingProfile.json`)

Real footprints from the map; storeys and roof shapes from the building's own tags, then the profile's North American
numbers (n ≥ 20) drawn with the building's seed, else the [doc] values of 08b §5. Synty City's 3 m storeys are a
modular simplification; ours are real heights, which is why our blocks stand a little taller than Synty's shops.

| Type | Storeys | Storey height | Plinth | Roof | Windows (w × h, sill, bay) | Doors |
|---|---|---|---|---|---|---|
| **House** (frame) | profile `type:house` na (1: 32 %, 2: 60 %, 3: 6 %), 1 under 60 m² | 3.0-3.4 m | 0.5-0.9 m | profile shares read as gable 71 % (gabled, many, saltbox), hipped 27 % (pyramidal, quadruple saltbox, hipped, half-hipped, mansard), flat 2 %; pitch 34-45° (2+ storeys), 26-40° (1 storey), 28-38° hipped; eave overhang 0.35-0.50 m, rake 0.20-0.30 m; cross gables on L and T footprints; narrow wings (< 2.6 m) as lean-tos at 14°; half the small wings of 2-storey houses one storey | 0.80-0.95 × 1.45-1.70 m, sill 0.75-0.85 m, bay 2.5-3.2 m; small attic window in tall gables | main 0.95 × 2.1 m with a glass panel; back door 0.9 × 2.05 m; porch (70 % where the setback allows): 2.0-2.4 m deep, posts 0.14 m, rails, 14° roof, steps; else a stoop with steps |
| **Commercial block** (brick, street front) | profile `type:downtown_*` na (1: 25 %, 2: 44 %, 3: 25 %, 4: 5 %) | ground 4.0-4.6 m, upper 3.3-3.6 m | 0.15 m | flat behind a 0.8-1.1 m parapet with coping and a cornice; shared side walls stay blank (party walls found from the map within 0.8 m) | shopfront on the street wall: piers 0.5 m, display windows 0.55-2.75 m in bays ≤ 2.4 m, transoms 2.85-3.25 m, sign band, storefront cornice, awning (50 %); upper floors 0.95-1.05 × 1.9-2.1 m, sill 0.8 m, bay 2.3-2.6 m, stone lintels and sills | recessed door 1.1 m (1.8 m double on fronts ≥ 8 m) × 2.3 m with a transom; rear service door 1.0 m |
| **Barn and machine shed** (barn, machine shed, garage, shed) | 1 | eave: barn 4.5-5.5 m, machine shed 4.0-4.8 m, garage 2.6-3.0 m, shed 2.3-2.6 m | 0.1 m | barn: gambrel (60° / 25°) or gable 33-38°; machine shed gable 14-18° metal; garage gable 27-34°; shed gable or lean-to 18-30° | barn and machine shed 0.7 × 0.7 m, sill 1.3 m, bay 4 m; garage one side window | barn: sliding doors up to 4.2 × 4.0 m with X brace and track board, hay door in the gable; machine shed: 1-3 bays 3.6-4.2 × 4.2 m; garage: 2.6 m or 4.9 m × 2.15 m overhead door, side door |
| **Warehouse or shed** | profile `type:warehouse` na, cap 2 | eave 6-8 m | 0.15 m | flat with a 0.7-1.0 m parapet (60 %) or gable 5-10° (40 %) | clerestory band 1.2 × 0.9 m high on the long sides | man door 1.0 m; dock doors 2.7 × 3.0 m, 4 m apart on the side or rear away from the street, rubber bumpers and a dock seal |
| **Mill** (by the dam) | 4 (3 under 400 m²) [doc] | 3.6-4.0 m | 0.4 m, stone band to 0.5 m | gable 24-30° per rectangle, cupola on the main ridge | 1.0 × 1.7-1.9 m, sill 0.9 m, bay 2.5-2.9 m, 6-over-6 look | main 1.6 × 2.4 m; loading doors stacked on the service end with a hoist beam |

Colours per type are weighted picks from the palette (`plan.py`): houses mostly white clapboard (30 %) with white or
dark trim, shutters on 35 %; blocks red and dark brick with stone trim and dark shopfronts; barns barn red (55 %) with
white trim and metal roofs; machine sheds and warehouses metal panels; garages match the nearest house (70 %).

## 5. Model structure, LODs and budgets

| Node | Content |
|---|---|
| `Body_LOD0..2` | opaque body on `M_Bld_Atlas` |
| `Panes_LOD0..2` | every window and door glass on `M_Bld_Glass`; **vertex colour = (pane id, storey / 8, kind, 1)**: id a stable random 0.02-0.98 per pane (the same pane has the same id at every LOD), kind 0 window, 0.5 door glass, 1 shop window; UV 0-1 across each pane. M5's night shader lights a share of panes by id and time of day (07 §6.8): one mesh per building instead of one object per pane |
| `UCX_<file>_NN` | convex hulls: prisms over the footprint's convex pieces to the eave or parapet, one per roof rectangle, one for the porch |
| `DOOR_<Kind>_NN` | empties at each door opening's sill centre on the wall face (NN = the door spec's index for that building) |

- LOD0 walls have real openings (reveals, recessed glass and leaves, frames, sills, muntins); LOD1 keeps the massing
  and puts panes and frames flush on the walls (about 15 % of LOD0); LOD2 is the shell with flush panes (about 6 %).
  LOD Group heights 0.30 / 0.12 / culled at 0.01 (07 §8.5, `LodFromNames`).
- **Budgets (LOD0 triangles)**: house 6,000, block 15,000, mill 15,000 (the card's), barn family 6,000, warehouse
  15,000. A building over budget drops shutters, muntins and corner boards, then flattens its window frames
  (`build.py make`); one house of 223 needed that.
- Every wall face is cut on a grid at every opening and band, so there are no T-junctions; faces never share vertices
  (flat shading); coordinates are rounded to 0.01 mm and the FBX bytes are deterministic (`br_fbx.py`, no tangents).

## 6. Contact sheet (local only)

`art\pipeline\unity-art.ps1 -Steps contact-sheet -Graphics` renders Synty City buildings (shop modules, an old office,
a station, an apartment stack) beside our row of three blocks, two houses and the mill in the same light, to
`artifacts/art/contact-sheet.png`. It is never committed or uploaded. What it shows (2026-10-22): the same flat-colour,
hard-edged look and the same kind of proud frames and cornices; Synty's buildings read a little lighter in the same
light (their shader, and their lit-window texture), ours a little darker and browner. Revisit the wall values with
M5's building shader before tuning the palette.

## 7. Sign-off: our five types against this guide (generated 2026-10-22)

Measured from `Game/Assets/Art/Buildings/*/buildings.index.json` (the town's 247 buildings and 15 fixtures).

| Type | Built | LOD0 triangles (median / max, budget) | LOD1 / LOD2 median | Storeys | Eave / ridge (m) | Panes (median) | Colliders (median) |
|---|---|---|---|---|---|---|---|
| House | 223 + 4 fixtures | 2,468 / 5,893 (6,000) | 372 / 140 | 1-3 | 3.0-9.8 / 3.4-15.2 | 28 | 3 |
| Commercial block | 8 + 3 fixtures | 2,725 / 3,886 (15,000) | 267 / 157 | 1-3 | 4.1-11.4 / parapet 4.9-12.2 | 47 | 1 |
| Barn and machine shed | 13 garages, 2 machine sheds + 2 barns, a shed, a machine shed, a garage (fixtures) | barn 788, machine shed 540-920, garage 357, shed 94 (6,000) | 82-108 / 59-68 | 1 | 2.6-5.0 / 3.4-11.1 | 0-12 | 2 |
| Warehouse | 2 fixtures (none in the town) | 1,956 (15,000) | 157 / 96 | 1-2 | 6.0-6.3 / 7.2-7.3 | 19 | 1 |
| Mill | 1 (the mill by the dam) + 1 fixture | 6,877 / 10,414 (15,000) | 655 / 411 | 4 | 15.3-15.4 / 19.2-19.9 | 95-155 | 6-7 |

Review renders (Unity, URP): `art/buildings/review/types_houses_blocks_street.png`, `..._rear.png`,
`types_barns_warehouses_mill_street.png`, `..._rear.png`, `millbrook_downtown.png`, `millbrook_mill.png`,
`millbrook_houses.png` (on the town's DEM ground), `house_lod0_lod1_lod2.png`.
