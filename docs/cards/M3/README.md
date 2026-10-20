# M3 task cards: Real Millbrook (ground, streets and lots)

PLAN §6.1 M3, re-planned for the new world (`docs\milestones\replan-new-world.md`). You drive the truck through a real
river town: Stockport, Ohio, renamed Millbrook, on a 2 km square of real lidar ground. The streets meet in clean
junctions with kerbs, sidewalks, stop lines and signs; every shop, fuel station, yard, farm and house has the lot,
driveway and parking that its kind of business really has. The first Blender buildings (Synty-matched) stand on their
real footprints, with grey boxes for the types not modelled yet.

Decisions in force (`docs\approvals\QUEUE.md`): **U25** new world; **U26** Stockport, Ohio; **U27** 2 km square;
**U28** roof-data download; **U29** site defaults (keep mapped lots and fill the gaps, generate missing driveways,
paint accessible stalls, arrows and fire lanes); **U24** Synty props in lots; **U4a** Unity 6000.3.20f1. Superseded:
U8a-e, U15, U22. ADR-006 (the new world, accepted) is the contract for everything world-shaped.

| Card | Lane (worktree) | Title | Feeds |
|---|---|---|---|
| [T3.0](T3.0-town-source-and-contracts.md) | I (main) | Town source (the old S0): the Stockport extract and DEM as `TownData`; bake contracts on main; ownership | everyone |
| [T3.1](T3.1-world-size-and-test-world.md) | W (`w`) | World size from the manifest; the old Millbrook becomes `CanonTestWorld` | every parity test |
| [T3.2](T3.2-terrain-and-plates.md) | W (`w`) | Terrain from the DEM, the river, land surfaces, the plates API, the `WorldData` export | R2, S4, T3.3 |
| [T3.3](T3.3-millbrook-scene.md) | W (`w`) | The new Millbrook scene: 2 km chunks, water, grey-box buildings, first nature, places, minimap | `m3.drive.millbrook` |
| [roads-R0o](roads-R0o-osm-graph.md) | R (`r`) | OSM road graph on the DEM | R1-R4, S1 |
| [roads-R1](roads-R1-junctions.md) | R (`r`) | Junction surfaces: corners, mouths, polygons, heights | `Roads.Validation` |
| [roads-R2](roads-R2-kerbs-walks-cuts.md) | R (`r`) | Kerbs, sidewalks, ramps, driveway cuts, road plates | S1, S4 |
| [roads-R3](roads-R3-markings-controls.md) | R (`r`) | Markings, controls from tags, signs, chunks, LOD, colliders | `m3.shots.junctions` |
| [roads-R4](roads-R4-roundabouts-bridges-aprons.md) | R (`r`) | Bridges (the river bridge), unpaved aprons, class changes; roundabouts only where tagged | `m3.drive.millbrook` |
| [sites-S1](sites-S1-parcels.md) | S (`s`) | Blocks and parcels from the street graph and land use | S3 |
| [sites-S2](sites-S2-parking-engine.md) | S (`s`) | Parking engine: stalls, aisles, accessible stalls, islands, entrances | S3 |
| [sites-S3](sites-S3-site-plans.md) | S (`s`) | Site plans per business type: mapped, templates, rules | S4, T3.4 doors |
| [sites-S4](sites-S4-surfaces-props.md) | S (`s`) | Surfaces, grading plates, kerbs, markings, Synty props, colliders | `m3.shots.sites` |
| [sites-S5](sites-S5-validation.md) | S (`s`) | Validation, determinism, screenshots, profile check | `Sites.Validation` |
| [T3.4](T3.4-blender-buildings.md) | A (`a`) | Blender building generator, phase 1: the style guide and the first five types | `Art.BuildingsBudget`, `m3.shots.sites` |
| [T3.5](T3.5-brv-v2-core.md) | V (`v`) | BRV v2 core behind a flag (unchanged from PLAN M3) | M4 coupling |
| [T3.6](T3.6-physx-chassis.md) | V (`v`) | PhysX chassis; statics from both worlds | `Vehicle.BuildingImpact` |
| [T3.7](T3.7-integration-and-gate.md) | I (main) | Integration, `m3.drive.millbrook`, the M3 gate, build, report, M4 cards | M3 gate |

The road and site cards were drafted by the site research (`docs\cards\M3\sites-roads-overview.md`, which also holds
the parameters table and the five lane contracts) and are filled in for Stockport by T3.0.

## Order

```
T3.0 (I, day 1-2) ──► TownData\stockport-oh\v1 + bake contracts on main
   │
   ├─► W: T3.1 (size, test world) ─► T3.2 (terrain, plates) ─► T3.3 (scene)
   ├─► R: R0o ─► R1 ─► R2 ─► R3 / R4
   ├─► S: S2 (synthetic, day 1) ; S1 (after R0o) ─► S3 ─► S4 (after R2, T3.2 plates) ─► S5
   ├─► A: T3.4 (style guide first, then types; door specs from S3 when it lands)
   └─► V: T3.5, T3.6 (BrvTestTrack and the test world; Millbrook statics after T3.3)
T3.7 (I) integration as lanes land; the gate when all are in
```

## Shared contracts (on main before the lanes start, T3.0)

- `TownData\stockport-oh\v1\` (frozen source extract, ADR-006 §2 and the overview's contract 1): `manifest.json`,
  `roads.json`, `buildings.json`, `sites.json`, `landuse.json`, `water.json`, `dem\dem_1m.f32` + `dem.json`, `credit.txt`.
  Real names are stripped at extraction (ADR-006 §6): the town, streets, businesses, `brand`, `operator`, addresses.
- `Backroads.Core.WorldBake` (`Game\Assets\Backroads\Core\Runtime\WorldBakeContracts.cs`, I-owned): `BusinessType`,
  `TownFrame` (local metres, x east, z north; the flip to the grid's JS layout happens once, in W's export),
  `Plate` + `IPlateSink` (W implements; R and S submit), `KerbFrontage` + `IKerbFrontageSource` (R implements; S
  reads), `DrivewayRequest` / `DrivewayCut` + `IDrivewayCutter` (R implements; S asks), `DoorSpec` (S writes, A reads).
  The overview's contracts 2-5 are the specification; ADR-007 (draft, T3.0) records the field lists.

A lane that needs one of these changed asks the integrator; nobody edits them on a lane branch.

## Ownership in M3

New in `OWNERS.json` (T3.0): lane **S** (sites) owns `Game\Assets\Backroads\Sites\`, `Tests\{EditMode,PlayMode}\Sites\`,
`Testing\Runtime\Scenarios\Sites\` and `Game\Assets\Content\sites\`; lane W owns `WorldData\`, `TownData\` (after T3.0
writes it) and `tools\world\`; lane R owns `tools\world\roads\`; lane S owns `tools\world\sites\` and `tools\sites\`;
`tools\osm\` and `tools\scout\` stay I. `Scenes\CanonTestWorld.unity` and the new `Scenes\Millbrook.unity` are W's.
