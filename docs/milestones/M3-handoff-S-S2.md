# M3 handoff: lane S, card sites-S2 (the parking engine)

Branch `m3/s-parking-engine` in `D:\Backroads-Unity-wt\s`, cut from `main` at `bbe542f`. One code commit, `c9634f2`,
then this handoff. Nothing was pushed, installed, downloaded or signed into; no Synty file was touched.

## In plain words

There is now one engine that lays out any parking lot: give it the lot's outline, where its driveways meet it and the
building it serves, and it draws the stalls, aisles and drive lanes, puts the accessible stalls next to the door with
their striped access aisles, signs and kerb ramps, adds landscaped islands at the row ends and every 15 stalls, lamp
posts, kerbs, wheel stops, white stall lines, one-way arrows, stop bars at exits onto a main street, a fire lane in
front of a big store and a drive-through lane round a fast-food restaurant. It tries the lot every sensible way round
(aisles along the lot, across the street, along the building; straight or angled stalls; a few hundred offsets) and
keeps the best, always the same one for the same lot. It does not need the town yet: it was built and tested on 21
made-up lots, from a 12 x 20 m corner lot to a 120 x 90 m one, and lays each out in under 5 ms. Plan maps of ten of
them are in `M3-handoff-S-S2/` (street at the bottom; grey lot, white stall lines, blue accessible stalls, green
islands, yellow lamps, orange wheel stops, red entrances): `rect_60x40.svg` (a shop lot), `bigbox.svg` (a supermarket
with its fire lane), `fastfood.svg` (the drive-through), `angled_one_way.svg` (a narrow lot with 45-degree stalls on a
one-way aisle), `rect_120x90.svg` (the largest), `l_shape.svg`, `corner.svg`, `trailer_yard.svg`, `rect_40x30.svg`,
`rect_12x20.svg`.

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **`ParkingPacker.Pack(lot, entrances, building, targets, seed) → LotLayout`**, pure C# in double precision (`Backroads.Sites`, a new runtime assembly with no engine types in the core). Candidate aisle directions: the lot's long and short axes (minimum-area rectangle), across and along the frontage, along and across the building, deduplicated within 1°. Stall angles: 90° on two-way aisles; 60° and 45° on one-way aisles when the lot cannot take a double 90° module. Module phase 0 to one module in 0.25 m steps. Per candidate: double-loaded modules up the lot, a single-loaded module below or above where room is left (an aisle may use the 0.5 m edge margin), then drive lanes ("spines") in three modes: from the entrances only; plus a lane along the side of a side entrance; plus lanes at both ends of the aisles. Each strip of the lot is scanned edge by edge (exact for any polygon, convex or not), obstacles by their projection; a breadth-first graph from the entrances finds which aisles are reached; stalls are sliced only along reached aisles (one-way aisles must be open at both ends), 0.5 m inside the lot, 1 m from the building, clear of drive lanes and of the first 6 m behind every entrance. Score: capacity against the target (stalls past the target count a fifth), + 2 × alignment with the building, − 0.5 per dead-end aisle end, − 0.05 per hop from the entrance, small penalties for squeezed and angled stalls. The first best candidate in the fixed order wins | `Sites/Runtime/Parking/ParkingPacker.cs` |
| 2 | **Sizes from the profile**: `ParkingDims.FromProfile(siteProfile.json)`: stall width = p50 2.75 m, the squeeze band = p25 (2.72 x 5.07 m), the measured module band (17.3 / 18.4 / 19.6 m). The stall length stays 5.5 m [doc] since the measured module (n 351) = two 5.5 m stalls + a 7.3 m aisle (the 17 measured stall polygons are thin and bimodal); the 18.3 m module is checked against the band (`report.moduleInBand`). Squeezed stalls are tried only where a small lot holds at most one full double module and the p25 stall fits another row | `ParkingDims.cs` |
| 3 | **Accessible stalls**: the 2010 ADA table 208.2 (`Ada.Required`, 1-1000+) and one van space in six (208.2.4), the smallest count that meets the table for the lot's total *after* the conversion; placed in the window of standard stalls nearest the main door (the mapped door, else the footprint edge nearest the lot); 2.44 m stalls in pairs sharing one access aisle (1.52 m, or 2.44 m when a van is in the pair; spare width widens the aisle); blue hatching, a blue ISA symbol per stall, an accessible / van sign at the head, a kerb ramp spot at the head of each access aisle | `ParkingDressing.cs` (`Accessible`, `EmitRuns`) |
| 4 | **Islands, lamps, kerbs, wheel stops**: 1.8 m end islands where a row meets a drive lane or an entrance's first 6 m, an intermediate island every 15 stalls (`ISLAND_EVERY`), kerbed (`Barrier150`) on paved kerb-zone lots, hatched otherwise; lamp spots in islands at least 25 m apart, plus poles on the stall-head line at stall-line points wherever a row is more than 17.5 m from a pole; perimeter `Barrier150` round paved kerb-zone lots, broken at the throats and at edges marked `Building` or `Open`; wheel stops (1.8 m, 0.75 m inside the head) where a stall faces a walk, a building, or a property line without a kerb | `ParkingDressing.cs` |
| 5 | **Circulation and markings**: one-way aisles run from an entry-only entrance or towards an exit-only one (else they alternate), angled stalls lean with the flow, arrows every 15 m; 0.1 m stall lines (white, blue beside accessible stalls); a 0.6 m stop bar across the outbound half of every exit onto MainStreet or Highway; a 6.1 m fire lane along the big-box face (no stalls, red kerb paint, FIRE LANE lettering); a 3.7 m one-way drive-through lane round the building with the building on the driver's left, stacking 6-8 cars (7 aimed), window and order board spots | `ParkingDressing.cs` |
| 6 | **`LotLayout`**: stalls (polygon, kind standard / accessible / van / trailer, front and back, angle, aisle, run), lanes (aisle, spine, fire lane, drive-through; centreline, width, one-way, footprint), islands, kerbs, wheel stops, lines, symbols, hatches, ramps, lamps, props (sign keys with the rule that placed them), the requested entrances, and a report (chosen direction, angle, phase, lane mode, capacity, ADA counts, dead ends, module check, notes). `LotLayoutHash` (mm and 0.01°, SHA-256) and `LotLayoutSvg` (the plan maps) | `LotTypes.cs`, `LotLayoutOut.cs` |

## Done when, expected vs measured

| Item | Measured (EditMode `Sites.ParkingSynthetic`, 31 tests) |
|---|---|
| Rectangles 12 x 20 to 120 x 90 m, L-shaped and trapezoid lots, one and two entrances, corner lots | **21 lots**: 10 reference rectangles, two entrances, L, trapezoid, corner (bottom + side), rotated 30°, big box, fast food, gravel, kerbed with a walk edge, 45° one-way, trailer yard |
| Every stall inside the lot | **pass**: every stall 0.5 m inside its lot; none overlaps another stall, a lane, an island, the building + 1 m or an entrance's first 6 m |
| Every stall reachable from an entrance through aisles (graph search) | **pass**, on a graph rebuilt from the output (lane footprints that touch, entrances on lanes): every stall opens onto a reached aisle; one-way aisles have a lane or an entrance at both ends |
| Accessible counts match the ADA table for 1-500 stalls | **pass**: the table function against the hand-written table for every total 1-500 (and 501, 1000, 1001; vans 1-13); lots of 1-481 stalls meet it after the conversion, one van in six |
| Capacity within 10 % of a hand-computed optimum on 10 reference rectangles | **pass**: 9 of 10 equal to the hand count, 120 x 90 m 345 against 340 (+1.5 %); the hand rules and the arithmetic are in `SyntheticLots.References` |
| Deterministic | **pass**: every lot packed twice gives the same `LotLayoutHash`; the profile's sizes give the defaults' layout |
| Under 20 ms per lot | **pass**: 0.4-4.3 ms in the editor (370-940 candidates per lot) |

Also checked: islands never more than 15 stalls apart, kerbs only on paved kerb-zone lots, no paint on gravel, lamp
coverage, the fire lane and drive-through kept clear, the drive-through's building on the driver's left, stop bars at
the main-street exits, 45° stalls leaning with the flow, trailer stalls 3.7 x 16 m.

## Where this differs from the card

- **End islands** go where a row meets a drive lane or an entrance's first 6 m, not where it runs into the lot's own
  edge (the perimeter strip and the 0.5 m margin serve there); the card says "every row end".
- **Lamp spacing**: poles in islands keep 25 m apart; where islands are farther apart than 35 m (15 stalls + island =
  43 m), extra poles stand on the stall-head line at a stall-line point; two poles can then be 10-25 m apart.
- **No stall within 6 m behind an entrance throat** (our rule, so a car entering never meets a car backing out); the
  card does not name one.
- **Squeezed stalls** (p25) only on small lots (at most one full double module) where they fit another row; the card's
  "when a lot needs squeezing" left the threshold open.
- **Stall kinds**: compact and motorcycle are in the enum but never generated; parallel stalls (13 % of the thin
  sample) are left to S3's on-street parking.
- **The seed** is recorded in the report; the search is deterministic by its fixed order and needs no randomness.
- **The drive-through** is the engine's minimal version (a lane round the building's oriented box, its start pulled
  back along the third side for 7 cars); S3's fast-food module places it against the real entrances and window.

## Next

- **sites-S1** (this lane, next): blocks and parcels from R2's `KerbFrontageSource` and the mapped lots.
- **sites-S3** calls `ParkingPacker.Pack` for every mapped, template and rule lot, with `LotEdgeKind` from the parcel
  (walk, building, property) and the entrances from R2's answered `DrivewayCut`s.
- **sites-S4** turns the layout into meshes, kerbs, markings and prop placements (the sign keys and lamp spots).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Sites
```

Plan maps: `artifacts\sites\s2\*.svg` (written by the tests).

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'S'; tools\ci\gate.ps1 -Tier merge` (lane S paths only):
**pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1109\gate-merge`, on `c9634f2`): oracle.hash; vendor.check
(584 files, 0 differences); native.ctest; unity.editmode 236 passed, 1 skipped (ServerContents: no server build), 0
failed; unity.playmode 9 of 9; build.servercompile; build.dev (231 MiB); autotest.smoke. The native step's
`EngineSimPlugin.dll` was restored afterwards (lane V's, committed only at milestone tags).
