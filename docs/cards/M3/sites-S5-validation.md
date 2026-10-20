# sites-S5: validation, determinism, screenshots and the profile check (S, worktree `s`, ≈ 2 AD) — draft

**Inputs:** sites-S1..S4, roads-R3 (chunks and colliders), `siteProfile.json`, PLAN §1.9 bake entry points (add `tools\ci\sites-bake.ps1` next to `roads-bake.ps1`).

**Steps**
1. EditMode `Sites.Validation`: plans complete; no overlaps (plan vs building, road footprint, other plans); stalls inside lots and reachable; ADA counts; entrances are baked cuts; grades; terrain under surfaces; props clear of stalls, aisles and doors; every template-sourced plan lists its OSM ids for the credit.
2. EditMode `Sites.MatchesProfile`: compute the study's measures on the generated town (lot position shares by type, lot area per building area, stall and aisle sizes, entrance counts, fuel canopy setback and orientation, dock sides and apron depths, driveway lengths and sides, garage positions) and report each against the p10-p90 band of the blended profile; fail below 70 % inside.
3. **Determinism and hashing** (like CanonData): `SitesBakeCli.Bake -verifyHash` bakes twice and in a fresh worktree; `Sites/manifest.json` holds sha256 of every output plus the hashes of its inputs (`TownData` manifest, `KerbFrontage`, profile, templates) and the generator version; a stale or different hash fails.
4. Autotest `m3.shots.sites`: fixed cameras over 12 sites (two houses, downtown front and alley, a retail lot, fast food, supermarket, fuel, warehouse, farm, quarry or mill, harbour), noon and night, contact sheet next to the plan maps.

**Done when:** all of the above pass on the town; the M3 report lists the plan sources per type, the profile check table and the screenshots.

**Gate:** M3 headline (proposed): `Sites.Validation`, bake determinism; lane: `Sites.MatchesProfile`, `m3.shots.sites`.
