# Re-plan: the new world (U25-U29), M3 to M9

**Date:** 2026-10-19. **Why:** you decided that the Unity game's Millbrook is a real river town (U25), picked
Stockport, Ohio (U26) on a 2 km square (U27), and approved the roof-data download (U28). ADR-006 is the contract. This
note says what changes in PLAN §6.1 from M3 on; where it and the PLAN disagree, this note wins until the PLAN is
re-issued at the M3 tag.

## What stays

- Every vehicle, engine, sound, deformation and net design; the frozen ABI (ADR-002); the snapshot (ADR-005).
- CanonData v1 (ADR-001), now as the **test world** (`CanonTestWorld.unity`): the golden traces, the deformation and
  probe references, the M2 route test and every parity test keep running there, every nightly.
- Research 05's road design rules (corner radii by class, markings, stop-line placement, kerbs, ramps, roundabout
  geometry), now applied to whatever the map has instead of a node list.

## What changes, milestone by milestone

| Milestone | Before | After |
|---|---|---|
| **M3** | "Proper intersections" on canon v1.1 (node by node, plates into `CanonData\v1.1`, the square legacy until U8) | **"Real Millbrook: ground, streets and lots"** (`docs\cards\M3\`): the Stockport extract (T3.0), the 2 km world (T3.1-T3.3), the street program R0o-R4 (lane R), the site program S1-S5 (new lane S), the first Blender buildings (T3.4, lane A), BRV v2 core and the PhysX chassis (lane V, unchanged). R2 (kerbs, walks, ramps, driveway cuts) moves **in** from M4. Lane A's truck body (phase B) moves to M4 |
| **M4** | VS-1 (engine-sim drives the truck) + roads R2 + A phase C + W sky and fog | VS-1 unchanged. R: traffic groundwork on the new lane graph (from M6's R5-R6) and the R2 fixes from M3's F10. A: buildings phase 2 (every remaining type: church, school, shopfront variants, garages, sheds, the lock house), the truck body (phase B) and phase C shaders. W: sky, fog, night, the river's look. The "labour up the hill past the depot" drive becomes the hollow road to the quarry |
| **M5** | Pretty Millbrook on canon (lit panes from `lit` strings, the lake) | Pretty Millbrook on the new world: lit panes from A's pane names (`World.LitPanes` replaces `Canon.WindowsLit`), the river and the dam spill instead of the lake, APV and probes for 2 km. Perf budgets are set on the new world (the flythrough is redrawn on it); memory budgets include the ~450 MB ground layers |
| **M6** | Prerequisite U8 (the old roundabout); roundabout R4, lanes and connectors on canon nodes | No U8: the town has no roundabout in OSM, so R4's roundabout code only runs if a later town has one. Lanes, connectors, controls from OSM tags (stop signs, the signal if mapped); traffic AI on the real streets, the river bridge and the state road. Everything else unchanged |
| **M7** | Offroad in Blackwater Bog | Offroad in the **marsh at the creek mouth** (the bog, added) and on the dirt roads up the **hollows**; the dozer works the **quarry**. Tests renamed (`Offroad.BogStuckRecover` keeps its name) |
| **M8** | Online alpha | Unchanged; the net position field covers 2,048 m (ADR-006 §4) |
| **M9** | Jobs at the oracle's places | Jobs at W7's places: the garage, mill, fuel, landing (replaces the harbour), farms, quarry, depot, marsh. The payout formula and its oracle references stay; the minimap and big map come from the new bake |

## Effort

M3 grows from about 41 to about 70 agent-days, mostly in parallel: R 21, S 19 (new lane), W 14, A 8, V 13.5, I 5,
feedback reserve 6. With five lanes running it stays near two calendar weeks of lane time; M4 shrinks by R2's 5 days.

## Names

Nothing real ships: the town is Millbrook, streets get names from our own list, shops get fictional names with no
logos, and the OSM `name`, `brand`, `operator` and address tags are stripped at extraction (ADR-006 §6). Credits name
OpenStreetMap contributors (ODbL) and the USGS 3D Elevation Program.
