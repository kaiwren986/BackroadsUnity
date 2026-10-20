# sites-S2: the parking engine (S, worktree `s`, ≈ 4 AD) — draft

**Inputs:** design `docs/design/site-layouts.md` §5 (the packer), `siteProfile.json` (`parking.stalls`, `parking.aisles`, `parking.byServedType`), the 2010 ADA Standards §208.2 and §502 (accessible stall count, widths, access aisles, van stalls), Clipper2. No dependency on the town: it runs on synthetic lots from day 1.

**Steps**
1. `ParkingPacker.Pack(lotPolygon, entrances, building, targets, seed) → LotLayout` (pure C#, double precision, no Unity types in the core): candidate aisle directions (lot long axis, perpendicular to the frontage, parallel to the building face), candidate stall angles (90° two-way; 60° and 45° one-way where the lot is too narrow for a 90° module), module phase offsets in 0.25 m steps; pack double-loaded modules, then single-loaded rows, then drive lanes that connect every aisle to an entrance; slice stalls; clip against the lot with a 0.5 m edge margin; keep the best by (capacity ≥ target, circulation valid, alignment with the building, fewest dead ends), ties by a fixed order.
2. Stall and aisle sizes from the profile (p50, clamped to the p25-p75 band when a lot needs squeezing), the module checked against the measured aisle-to-aisle spacing.
3. **Accessible stalls** (ADA 208.2 table; one in six van-accessible, 208.2.4): nearest the building's main door (door from `entrance=main` nodes, else the footprint edge facing the lot nearest the street), 2.44 m stall + 1.52 m hatched access aisle (van: 3.35 + 1.52 or 2.44 + 2.44), a kerb ramp from the access aisle to the walk, the ISA symbol and sign.
4. **Islands and kerbs:** end islands at every row end (≥ 1.8 m), an intermediate island every `ISLAND_EVERY` stalls (parameter, 15), lamp poles in islands at 25-35 m spacing, perimeter kerb on paved lots in the kerb zone, wheel stops where stalls face a walk, a building or a property line without a kerb.
5. **Circulation and markings:** one-way aisles get arrows and the angled stalls lean with the flow; stop bars at exits onto MainStreet/Highway; fire lanes along big-box fronts (painted kerb); drive-through lanes as one-way 3.7 m lanes with 6-8 cars of stacking.
6. Output `LotLayout`: stalls (polygons with kind: standard, accessible, van, compact, trailer, motorcycle), aisles, drive lanes, islands, kerbs, wheel stops, markings (lines, symbols, arrows, hatches), lamp spots, and the requested entrances.

**Done when:** EditMode `Sites.ParkingSynthetic`: rectangles from 12 × 20 m to 120 × 90 m, L-shaped and trapezoid lots, lots with one and two entrances, corner lots; every stall inside the lot; every stall reachable from an entrance through aisles (graph search); accessible counts match the ADA table for 1-500 stalls; capacity within 10 % of a hand-computed optimum on 10 reference rectangles; the packer is deterministic and runs < 20 ms per lot.

**Gate:** feeds `Sites.Validation`, `Sites.MatchesProfile`.
