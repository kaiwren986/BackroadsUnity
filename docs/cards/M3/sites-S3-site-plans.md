# sites-S3: site plans per business type — mapped OSM, real templates, rules (S, worktree `s`, ≈ 6 AD) — draft

**Inputs:** sites-S1 (parcels, fixed areas, mapped driveway requests), sites-S2 (parking engine), `siteTemplates.json` and `siteProfile.json` (blended with the town's own `profile.json` by `PROFILE_BLEND`), design `docs/design/site-layouts.md` §4 (business types) and §6 (template fitting).

**Decided (2026-10-19, defaults applied; design §14):** the town's own mapped lots are kept and only the gaps are filled; houses the map leaves without a driveway get one by the measured rules; accessible stalls, arrows and fire lanes are painted.

**Steps**
1. `SitePlanner.Plan(parcel) → SitePlan`, three sources tried in order and recorded on the plan:
   - **Mapped:** the parcel has mapped lots, aisles, spaces, fuel areas or yards. Keep their outlines exactly; fill what the map leaves out (stall rows inside a mapped lot without spaces; aisles inside a lot without aisles; a canopy's islands; a yard's surfaces), never move mapped geometry.
   - **Template:** choose the best real template of the business type (design §6: same type, building size within the template's scaling range, frontage and depth that fit, mirror allowed, stalls re-sliced at true size rather than scaled), clip to the parcel, reject if more than 5 % of it falls outside or it overlaps a building.
   - **Rules:** the type's rule layout (design §4) from the profile's numbers.
2. One module per business type (design §4): house (driveway, garage apron, walk, mailbox), apartments, downtown (no front lot; walk to the face; rear lot off an alley or side drive; loading bay), standalone retail and office (front/side/rear by the measured shares and the space available), fast food (drive-through), supermarket (big front lot, cart corrals, truck dock at side/rear), fuel (canopy parallel to the road at the measured setback, islands under it, shop behind, two wide aprons), car repair, motel, church/school/civic (side lot, drop-off loop), warehouse and depot (docks side/rear, apron, fenced yard, trailer stalls, staff lot), industrial works, farm (long drive, gravel yard, outbuildings, fences), quarry (gate, scale, haul road, stockpiles), mill (sawmill log yard; grain elevator with scale and rail siding), harbour/marina (ramp, trailer stalls, piers), accessory buildings (aprons only).
3. Choices that need randomness (which side a house's driveway takes, which of two equal templates) use `SITE_SEED` = hash(`TOWN_ID`, OSM type, OSM id) only, so editing one site never changes another.
4. Every plan emits its `DrivewayRequest`s, then re-plans once against R2's answers (moved or rejected cuts).
5. Every plan also emits `DoorSpec`s (main, service, garage, dock, bay, drive-through window: wall edge, offset, width) and `ModelRequest`s (canopy, dispensers, pylon, grain bins, scale house) for lane A (design §7a), so openings and aprons line up.

**Done when:** every non-accessory building has a plan with its source (mapped / template id / rules) and business type; a report shows the shares of each source per type and every rejected template with the reason; no plan overlaps a building, a road footprint, another parcel or a mapped area it does not own; the plan for a fixture parcel of each type matches a reviewed snapshot.

**Gate:** feeds `Sites.Validation`, `Sites.MatchesProfile`, `m3.shots.sites`.
