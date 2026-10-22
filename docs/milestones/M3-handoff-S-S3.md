# M3 handoff: lane S, card sites-S3 (site plans per business type)

Branch `m3/s-site-plans` in `D:\Backroads-Unity-wt\s`, cut from `m3/s-parcels` at `89cbe68` (S1 done, since merged
into main). One code commit, `39ed04a`, then main merged in (`55c2aad`, main at `22a87a6` with T3.2 and T3.5), then
this handoff. Nothing was pushed, installed, downloaded or signed into; no Synty file was touched; TownData, lane R's
code and lane A's files were only read.

## In plain words

Every plot in Millbrook with a building on it now has a site plan: what is paved and with what, where the driveway
meets the street, where people park, where the doors are, and the small things that make a place look used (mailbox,
bins, dumpsters, pallets, benches). Plans come from three places, tried in order and written on the plan: the town's
own map where it shows a parking lot, a yard or a driveway (kept exactly, only the stalls and drives filled in), a
real site template of the same kind when one fits the plot without stretching it, and otherwise the kind's rules
(houses: a side driveway to the garage or a parking pad, 79 % of the time on the side, from the measured profile; a
shop: a lot in front, beside or behind as real shops have them; downtown: no front lot, a rear lot off the alley when
there is room; the fuel station: canopy, pump islands, the shop behind and two wide entrances). Every plan asks lane
R's driveway cutter for its driveways; a driveway the street refuses (too close to a corner) is asked for once more at
another spot, and if that fails too the plan says so and the house parks on the street. Every building gets its door
specs for lane A in lane A's own format (`tools/world/sites/stockport-oh/doors.json`, 464 doors on 252 buildings), so
the modelled doors face the driveway and the lot. The town has no supermarket, motel, quarry or farm, so each
business type (plus mapped lots and two template fits) is also planned on a made-up plot and checked against a
reviewed snapshot. Pictures in `M3-handoff-S-S3/`: `plans-village.png` and `plans-downtown.png` (the town: buildings
dark, lots dark grey with white stalls and blue accessible stalls, drives and walks pale, gravel tan, green islands,
red dots doors, yellow dots props), `fixtures.png` (one made-up plot per type); the full list is `plans-report.txt`.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **`SitePlanner.Plan`**, three sources in order, recorded on the plan (`PlanSource`, `templateId`, `mirrored`, `scale`): **Mapped** when the parcel holds a mapped lot, yard or S1 driveway (outlines kept exactly; stall rows and aisles packed inside a lot with S2's engine, preferring the mapped aisle's direction; drives added after packing so they never eat the lot); **Template** (design §6: same kind, building scale 0.75 to 1.33 each way, mirror allowed, no more than 5 % of the lot off the parcel, no building overlap, entrances moved to the frontage, stalls re-packed at true size, not scaled); **Rules** from the profile (`siteProfile.json` blended with the town's). Town: 255 plans, Mapped 17, Template 0, Rules 238 | `Sites/Editor/Plans/SitePlanner.cs`, `TemplateFitter.cs`, `PlanBuilder.cs`, `Modules/LotHelper.cs` |
| 2 | **One module per business type**: house (side or centred drive to the mapped garage or a pad, walk to the door, mailbox, bins; rural gravel; no drive where the frontage is too short), downtown (no front lot; rear lot off the alley or a side drive when there is room, else street parking), retail and office (front, side or rear lot by the measured shares, falling back to whatever fits), restaurant, fast food (the drive-through lane round the building, from S2, placed first), supermarket (lot in front, truck dock at the rear), fuel (canopy parallel to the road, islands, dispensers, shop stalls, FuelIn/FuelOut aprons, pylon), car repair (bays, apron, yard, tyre stacks), motel (row of rooms facing the lot), apartments, civic, church, school (lot, bus bay), warehouse and industrial (docks, apron, fenced yard, truck gate, trailer stalls, staff lot), farm (long drive, gravel yard, grain bins, hay bales), quarry (boom gate, scale, crusher), mill (log yard, kiln, elevator leg, truck scale), harbour (ramp, piers, trailer stalls); accessory buildings get an apron only | `Plans/Modules/HouseModule.cs`, `CommercialModule.cs`, `OtherModules.cs` |
| 3 | **`SITE_SEED`** = FNV-1a of (`TOWN_ID`, OSM type and id) into splitmix64, one stream per named choice (`drive-side`, `lot-position`, ...): editing one site never changes another | `Plans/PlanSupport.cs` (`SiteSeed`, `SiteRng`) |
| 4 | **Driveways against R2's answers**: pass 1 every plan places its throats clear of R2's forbidden intervals (corner returns, other cuts) and asks; R's `DrivewayCutter` answers all requests together; pass 2 asks again once for each rejected request at another spot; pass 3 builds the geometry from the final answers (merged cuts share one throat; R's aprons stay clear). Town: 218 requests (21 mapped), Baked 214, Merged 3, Rejected 1 (S1's mapped `way/1271520106` deep in a corner return; that house parks on the street, noted on its plan) | `SitePlanner.Plan`, `PlanBuilder.Entrance` |
| 5 | **DoorSpecs and ModelRequests**: main, service, garage, dock, bay and drive-through window doors (wall edge, offset, width) on the wall facing the walk, lot or yard; written in lane A's `backroads-doorspecs` format, sorted, to `tools/world/sites/(town)/doors.json` (Main 248, Service 212, Garage 4). ModelRequests for the fuel canopy (when the map has none), islands, dispensers and pylon (4 in town: an island, two dispensers, the pylon), dock levellers, grain bins, hay bales, the quarry scale and crusher, the mill's kiln, elevator leg and truck scale, the harbour's ramp and piers on the fixtures | `SitePlans.DoorsJson`, `PlanBuilder.Door`, `PlanBuilder.Model` |

Also: fences and gates (`Fence`, `Gate`), props by key with the rule that placed them (`PropSpot`, 302 in town),
surfaces with use and wear (`SurfacePoly`: lot, drive, apron, forecourt, walk, yard, pad), OSM credits and notes on
every plan (`Sites/Runtime/Plans/SitePlanTypes.cs`); SVG plan maps of the town and of each fixture; the report (sources
per type, every rejected template with its reason, R's answers, every plan); menu *Backroads > Sites > Build site plans
(S3)*, batch `SitesBakeCli.Plans` (builds twice, fails on a hash difference or any problem, writes the door file). In
S2: an optional preferred aisle direction (`LotInput.preferredAisle`, for mapped aisles) and the drive-through lane as
a public builder (`ParkingPacker.DriveThrough`); S2's own outputs and snapshots are unchanged.

## Done when, expected vs measured

| Item | Measured (EditMode `Sites.SitePlans`, 6 town tests + 26 fixture snapshots; 70 Sites tests in all) |
|---|---|
| Every non-accessory building has a plan with its source and business type | **pass**: 249 main buildings, every one on a parcel with a plan of the parcel's type; plus 7 plans for vacant parcels that hold a mapped lot, yard or driveway; every fixed area's parcel has a plan |
| The report shows the shares of each source per type and every rejected template with the reason | **pass**: `plans-report.txt` table "Sources per type" and 350 rejections, each with its reason (`scale 0.12 x 0.42 outside 0.75-1.33`, `13 % of the lot falls off the parcel`, `the lot overlaps a building by 2 m²`) |
| No plan overlaps a building, a road footprint, another parcel or a mapped area it does not own | **pass**: 0 problems from the planner's own check; independently every surface lies on its own parcel (so off the roads and the other parcels) within 0.05 m² and off every building |
| The plan for a fixture parcel of each type matches a reviewed snapshot | **pass**: 26 fixtures (every type, a garage house, a rural house, fuel without a canopy, a mapped lot, a shop with a mapped lot, two template fits), snapshots `Tests/EditMode/Sites/Fixtures/plans/*.json` (source, template, surface areas by use, stalls, drives, doors, props, models and the plan's hash to the mm), reviewed on `fixtures.png` |
| Extra | R answers every request (one answer each, every rejection noted on its plan, every S1 mapped driveway still asked for); door specs on a real wall edge within its length, every main building has a main door; two builds give the same hash (`419d01e1bc15`) |

Town numbers (from the report): 17 lots with 209 stalls; the biggest are the 46-stall mapped lot and a shop's
39-stall lot; 22 houses with no frontage (reached by an easement) get a pad and no driveway of their own; 13
houses have no room for a driveway and park on the street; 5 shops have no room for a lot (their customers park on
the street); the school has no room for a bus bay in front.

## Where this differs from the card

- **No template fits the town.** All 350 tries were rejected, almost all for scale: Millbrook's shops are much
  smaller or longer than the template sources, and design §6's 0.75 to 1.33 range is strict. Two fixtures prove the
  template path works (an office fits `retail_side-06`, a restaurant `retail_front-01`, its stalls re-packed). Widening the range would give some template plans in town; I left design §6 as written. **A decision
  for the user if they want templates in Millbrook.**
- **Houses use rules, not templates**: the house templates come with detached garages and sheds we do not build;
  the rules take the measured side share, drive widths and surfaces from the profile instead.
- **The re-plan is a second request pass, not a full re-plan**: rejected requests are asked once more at another
  spot; moved and merged answers are taken as R gives them.
- **Downtown loading bays and on-street parking are not in the plans**: they are on the street, which is lane R's.
- **Parcels with no frontage** (flag lots reached by an easement) get a pad but no driveway of their own: a drive
  along the easement would cross other parcels. Their plans say so.
- **One mapped lot has no way in**: `way/1271520105` (376 m²) sits on a parcel with no frontage, so it stays a paved
  surface without stalls (noted on parcel 284's plan).
- **The fixtures are made-up plots**, not town parcels: the town has no supermarket, motel, warehouse, farm, quarry,
  mill or harbour.
- **No baked asset yet** besides the door file: plans are built in memory for S4; S5's
  bake writes `Sites/manifest.json`.

## For other lanes

- **Lane A**: the real door specs are in `tools/world/sites/stockport-oh/doors.json` (your format, 464 doors on 252
  buildings). `art\buildings\gen\run.ps1 -Doors tools/world/sites/stockport-oh/doors.json` regenerates the buildings
  against them; `Art.DoorsMatchSites` still checks the placeholders until the index names this file. The fuel
  station's 4 ModelRequests (an island, two dispensers, the price pylon; its canopy is a mapped building) are on
  parcel 143's plan.
- **Lane R**: no change needed; the cutter's answers were used as they are.

## Next

- **sites-S4** (this lane, next): surfaces and props; lane W's T3.2 (plates) is now on main, so S4 can start. The
  Synty props need the user's prefabs in place (S4 will report if they are missing).
- Lane A's models add porches outside the mapped footprints (112 in its index, placed at the placeholder doors). Once
  lane A regenerates against this door file, S4/S5 should keep props and drives off the porch outlines in
  `buildings.index.json`; the plans here only know the mapped footprints.

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Sites
Unity.exe -batchmode -nographics -quit -projectPath Game -executeMethod Backroads.Sites.Editor.SitesBakeCli.Plans
```

Report and maps: `artifacts\sites\(town)\plans-report.txt`, `plans-*.svg`; fixtures `artifacts\sites\s3\fixtures\`.
To rewrite the fixture snapshots after reviewing the maps: `$env:BR_SITES_SNAPSHOT = 'write'` for one test run.

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'S'; tools\ci\gate.ps1 -Tier merge` on `55c2aad` (S3 with main
merged in): **not passed yet, blocked by the machine, not by S3.** Two runs (2026-10-22): oracle.hash, vendor.check
and native.ctest pass both times; in unity.editmode every Sites test and every other test passed except one of lane
W's each time:

- run `artifacts\20261022-1302\gate-merge-2`: 303 passed, 1 skipped (ServerContents), 0 failed, but
  `World.GridLayout.StepCostMatchesM2` was *Inconclusive* ("the machine was too busy to time the step cost") and Unity
  exits with 2 on that;
- run `artifacts\20261022-1309\gate-merge`: `World.Canon.MismatchAbortsImport` failed with "There is not enough space
  on the disk" writing to `%TEMP%`: drive C: has 0 bytes free.

The later steps (playmode, server compile, dev build, smoke) did not run. To finish: free space on C:, rerun the
gate, update this section. The native step's `EngineSimPlugin.dll` was restored after each run (lane V's).
