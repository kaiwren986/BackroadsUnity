# roads-R1: junction surfaces (R, worktree `r`, ≈ 5 AD) — draft

**Inputs:** roads-R0o (`RoadNetworkAsset` v2 on the town), research 05 §5.2-§5.6 and §5.12 (algorithms carry over unchanged; only the inputs are new), `TownData` building footprints (for the corner clearance check), parameters `CORNER_R`, `DOWNTOWN_ZONE`.

**Steps**
1. Arms and edge lines (05 §5.2) from the tag-derived cross sections; asymmetric sections (parking on one side, one-way streets) offset each side by its own half-width.
2. Corner solver (05 §5.3, polyline fillets with the radius back-off). Radius by class pair from `CORNER_R`; **building clearance** decides downtown corners: a corner whose 7.5 m kerb would pass closer than 2.5 m to a mapped building wall drops to 6 m, then 4.5 m, and the bake reports any corner that still passes closer than 1.0 m (R0's rule, now on real footprints). Truck routes (`hgv=designated` or Highway-Highway) take 12 m. L-bends: inner fillet, outer miter.
3. Mouths, crosswalk bands and stop lines (05 §5.4), stop lines on the flattest spot within +4 m when the approach is steeper than 6 %.
4. Junction polygons and `RingMesh` / ear-clip fallback (05 §5.5); every mouth shares its vertices with the ribbon bit-exactly.
5. Heights (05 §5.6): major-road profile carried through with its crown, minor arms warped over 12 m; no-major junctions on a least-squares plane clamped to 3 %; drainage low points kept out of crosswalks (inlets placed, as props).
6. Ribbons between junctions with the tag-derived sections and crowns, replacing R0's plain ribbons.
7. Fallback per node when the solver fails: a hand-authored polygon in `roads-overrides.json` (town-specific, versioned), last resort overlapping ribbons, always reported.

**Done when:** EditMode `Roads.Synthetic` (05 §8: 30° Y, 5-arm star, two junctions 25 m apart, a T on a 15 m curve, an arm shorter than its tangent) all bake or fail with a clear message; on the town: every junction surface is simple and CCW, its area within ±5 % of the connector-corridor union, seam height error < 1 mm, no kerb line inside a building footprint; a report lists every corner radius that was reduced and why.

**Gate:** feeds the M3 headline `Roads.Validation` and `m3.shots.junctions`.
