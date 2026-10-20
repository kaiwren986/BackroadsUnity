# sites-S1: blocks and parcels (S, worktree `s`, ≈ 3 AD) — draft

**Inputs:** `TownData` (sites-S0), roads-R0o's graph and provisional `KerbFrontage`, later roads-R2's real one, design `docs/design/site-layouts.md` §3, Clipper2.

**Steps**
1. **Blocks:** the faces of the planar street graph, cut back to the walk back edge (`KerbFrontage`); rural blocks are bounded at `RURAL_DEPTH` (parameter, 120 m) from the road where no other road closes them.
2. **Fixed areas from the map:** mapped `amenity=parking` lots, `amenity=fuel` areas, `landuse=farmyard|industrial|retail|commercial|quarry|harbour`, `leisure=marina`, `man_made=works|pier` are kept exactly and assigned to the building(s) they serve (the study's served-building rule: nearest non-house outline within 30 m, shared when two are within 6 m).
3. **Parcels:** split each block among its buildings: a weighted Voronoi of the footprints (weight by business type: houses get their measured frontage, commercial their lot-to-building area ratio), with the parcel lines snapped perpendicular to the street at the frontage and merged with the fixed areas. Each parcel records its frontage faces (face id, s0..s1), whether it is a corner parcel, its alley access (a mapped `service=alley` along its back) and its depth.
4. **Mapped driveways and aisles:** every `service=driveway|parking_aisle|drive-through` and untagged service way that meets a street becomes a `DrivewayRequest` with `mapped = true` on its parcel.
5. Debug view: parcels coloured by business type, frontage faces, fixed areas hatched, plus a CPU plan map like R0's.

**Done when:** every building except accessory buildings has exactly one parcel; parcels tile each block without gaps or overlaps above 0.1 m²; every parcel has ≥ 1 frontage face or an access easement to one (flag-shaped lots and farms down long drives are legal); every mapped lot and yard is inside its parcel; deterministic (two runs, same hash).

**Gate:** feeds `Sites.Validation`.
