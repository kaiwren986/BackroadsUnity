# sites-S4: surfaces, grading, kerbs, markings, props and colliders (S, worktree `s`, ≈ 4 AD) — draft

**Inputs:** sites-S3 plans, roads-R2 (`DrivewayCut`s and the walk back-edge heights), lane W's plates API (overview contract 4), the user's Synty prefabs (U24) and the prop table of design §7, research 05 §5.9 and §5.13 (materials, chunks, LOD, colliders; shared with the roads bake).

**Steps**
1. **Surfaces:** polygons per material (asphalt, concrete, gravel, dirt, pavers, grass strips) from the plan, unioned per parcel, triangulated, draped on the graded heights + 0.02 m (asphalt and concrete) or written only as terrain surface ids (gravel, dirt, which the terrain shader draws). Lot asphalt uses the road asphalt material with a lighter wear tint; concrete slabs get 3-4.5 m joint UVs.
2. **Grading plates** to W: one plate per paved area: a least-squares plane through the cut heights and the building's door sill, slopes clamped (lots ≤ 5 %, accessible stalls and aisles ≤ 2 % in any direction, fuel forecourts ≤ 2 %, drives ≤ 12 %, gravel yards ≤ 8 %), side slopes ≤ 1V:3H inside the parcel, blending to the DEM within the parcel; the plate meets every `DrivewayCut` apron edge exactly.
3. **Kerbs and islands** (the roads bake's kerb extruder, 150 mm barrier kerbs on paved lots in the kerb zone, none on gravel), **markings** (stall lines 0.1 m white, accessible stalls blue with the symbol, hatched access aisles, arrows, stop bars, fire-lane kerb paint) as one markings submesh per chunk like the roads.
4. **Props** placed as prefabs (never edited, never merged into baked meshes): the per-type table of design §7 (lamp poles in islands, parking and entrance signs, wheel stops, bollards in front of shop doors and pumps, dumpsters in enclosures behind, cones, benches, bins, pallets, barrels, tyre stacks, gates). Items Synty does not have (fuel pumps, price pylon, wheel stops, canopy, scale house, grain bins, hay bales) are lane A models or simple procedural meshes, listed in the plan so lane A can replace them.
5. **Colliders:** paved surfaces and kerbs into the roads' per-chunk MeshCollider; props keep their own prefab colliders; fences and gates as box colliders.
6. Chunking and LOD as the roads (64 m chunks, LOD0 < 250 m, LOD1 to 600 m with markings reduced, culled beyond).

**Done when:** every plan is drawn; terrain 0.25 m inside every paved site polygon is ≤ surface − 0.03 m; every driveway apron meets its lot within 5 mm; all grades within the limits above or reported; prop placements are within their parcel and clear of stalls, aisles and doors; total site triangles ≤ `SITE_TRI_BUDGET` (parameter, 150k for the town); deterministic.

**Gate:** feeds `Sites.Validation`, `m3.shots.sites`.
