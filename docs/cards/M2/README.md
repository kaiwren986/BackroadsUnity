# M2 task cards: First drive

PLAN §6.1 M2. You start in the truck at the Millbrook garage and drive anywhere; the bog ruts and grabs like the browser game; a real big-block V8 follows your foot.

Decisions in force (docs\approvals\QUEUE.md, 2026-10-19): **U6** gas V8 for the truck; **U9** the pickup is the **Kestrel 4x4** and the dozer the **Badger D3**; **U15** canon v1.1 accepted (used from M3); **U4a** stay on Unity 6000.3.20f1.

| Card | Lane (worktree) | Title | Feeds |
|---|---|---|---|
| [T2.1](T2.1-brv-v0.md) | V (`v`) | BRV V0: the browser game's vehicle model at 1/120 s | Vehicle.GoldenTraces, SnapshotRoundTrip |
| [T2.2](T2.2-vehicles-in-millbrook.md) | V (`v`) | Four vehicles in Millbrook: garage bays, switching, player vehicle | Vehicle.SettleParked, "you play" |
| [T2.3](T2.3-brv-v2-head-start.md) | V (`v`) | BRV v2 head start behind a flag | M3 |
| [T2.4](T2.4-engine-sound-in-vehicles.md) | V (`vs`) | Engine sound in vehicles: FOLLOW_RPM, the gas V8, the soak underrun | EngineSim.FollowInVehicle, m2.drive.route9 |
| [T2.5](T2.5-deformation.md) | W (`w`) | Deformation: integer stamps, ruts, partial chunk updates | Deform.MatchesJs, SubMmConverges, Deterministic |
| [T2.6](T2.6-controls-and-screens.md) | W (`w`) | Controls and screens: bindings.json, Esc menu, F1, settings.json | Input.NoDuplicateBindings, Settings.RoundTrip |
| [T2.7](T2.7-cameras-and-hud.md) | W (`w`) | Chase and hood cameras, HUD, F3 overlay | m2.drive.route9 |
| [T2.8](T2.8-roads-r0.md) | R (`r`) | Roads R0: graph import, cleanup, debug view, plain ribbons | M3 roads bake |
| [T2.9](T2.9-art-a1.md) | A (`a`) | Art A1: FBX postprocessor, material remap, LOD groups; the truck via glTFast and FBX | M3 phase B |
| [T2.10](T2.10-integration-and-gate.md) | I (main) | Integration, m2.drive.route9, the M2 gate, build, report, M3 cards | M2 gate |

## Shared contracts (on main before the lanes start)

- `Backroads.Core.VehicleContracts`: `EngineState`, `IVehicleEngine` + `VehicleEngines` (factory registry), `DriveInput` (levels + press counters), `VehicleHud`, `IDrivenVehicle` + `PlayerVehicle`. Vehicles (V) produce and consume them; EngineSim (V) implements the engine; input, cameras and HUD (W) drive and read the player's vehicle.
- `Backroads.Deform.IStampSink` + `StampSinks`: the browser game's `rut`/`brush`/`relax` in JS coordinates. Vehicles (V) call it; deformation (W) implements it.

A lane that needs one of these changed asks the integrator; nobody edits them on a lane branch.

## Ownership in M2

`OWNERS.json`: V owns Vehicles, EngineSim, native, Content; W owns World, Canon, Deform, Client, Client.Rendering, and for M2 also Client.UI (lane G takes it from M7) and `Content/core/bindings.json`; R owns Roads; A owns `art/`, `Game/Assets/Art/` and `Game/Assets/Backroads/Art/`. The two V worktrees split by folder: `v` works in Vehicles (+ its tests and scenarios), `vs` in EngineSim and native (+ its tests and scenarios).
