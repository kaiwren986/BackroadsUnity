# ADR-005: BRV V0 snapshot format v1 (draft)

- **Status:** **draft** (lane V, M2, card T2.1). Proposed to freeze at the `m2` tag; until then it may change with this
  ADR updated and `Vehicle.SnapshotRoundTrip` kept green.
- **Date:** 2026-10-19
- **Owners:** lane V (vehicles). Changes after the freeze need a new ADR, updated tests in the same commit (PLAN §1.10)
  and the integrator's merge.
- **Plan:** PLAN §1.5 (`BrvState`, `VehicleStepJob`), §2.4 (one fixed step), §5.3 and §5.4 (vehicle replication, one
  owner while attached), §6.1 M2 (`Vehicle.SnapshotRoundTrip` < 1 mm); research 03 §4.14 (phase 1), 06 §4.6.

## Context

BRV V0 (`Backroads.Vehicles`) is a line-by-line port of the browser game's `js/vehicle.js`. Its whole per-vehicle state
is one blittable struct, `BrvState` (`Game/Assets/Backroads/Vehicles/Runtime/BrvState.cs`): body pose and velocities,
the controls, the gearbox and driver-aid timers, flags, the winch rope, and per wheel the suspension, contact, spin and
the tyre solver's scratch values. Everything is in the browser game's JS world space (+X left; Unity flips at the API)
and in IEEE doubles, like the JS.

A copy of the struct is a snapshot in memory (`BrvFleet.GetSnapshot` / `SetSnapshot`). Saves (M8-M9), ownership
hand-over (M4-M5, PLAN §5.4) and replays also need a **byte form** that a later build can read or refuse cleanly.
In-memory struct bytes are not that form: `FixedList` layout and padding belong to Unity.Collections and the compiler.

## Decision

`BrvSnapshot` (`Game/Assets/Backroads/Vehicles/Runtime/BrvSnapshot.cs`) writes and reads **version 1**:

- Little-endian (`BinaryWriter`), explicit field order, no padding. Doubles are IEEE 754 binary64, written whole: a
  restored vehicle continues exactly as if it had never stopped (measured: 0 m drift over 680 and 1,200 steps).
- Header: `u32 magic` = `0x53565242` ("BRVS"), `u16 version` = 1, `u16 wheelCount` (≤ 10).
- Body: `pos`, `vel`, `angVel`, `prevPos` (3 × f64 each), `quat`, `prevQuat` (x, y, z, w), the basis `ex`, `ey`, `ez`
  as of the last `updateBasis()`, `iI` (inverse principal inertia), `mass`, `invMass`, `payload`.
- Controls: `throttle`, `brake`, `steer`, `handbrake`; controller: `steerIn`, `steerAngle`, `i32 gear`, `rpm`,
  `shiftT`, `revHold`, `fwdHold`, `fuel`; `u8 flags` (bit 0 lightsOn, 1 horn, 2 engineOn, 3 asleep, 4 driven,
  5 lowRange); `sleepT`, `impact`, `impactDir`, `inWater`, `speed`, `fwdSpeed`, `i32 deformTick`, `dirt`, `mudSink`,
  `stuckT`, `omegaMax`, `engineBrake`, `i32 nContact`, `trackSpeed` (2 × f64).
- Winch: `bool active`, `anchor` (3 × f64, JS world), `i32 target` (fleet index, -1 = static anchor), `len`,
  `bool reel`, `tension`; then `i64 steps`.
- Per wheel, in the vehicle.js layout order (front left, front right, rear left, rear right; track rows front to back):
  `u8 flags` (bit 0 contact, 1 sliding), `u8 surf`, `len`, `comp`, `load`, `cp`, `n`, `omega`, `angle`, `spin`,
  `skid`, `vl`, `mudRes`, `fd`, `sd`, `arm`, `rS`, `kF`, `kS`, `accL`, `accS`, `longLim`, `latLim`, `vsPre`.
- 1,659 bytes for a four-wheel vehicle.

Readers refuse another magic, an unknown version, or a wheel count that differs from the vehicle definition
(`InvalidDataException`). The vehicle definition (`ClassicVehicleDef`) is **not** in the snapshot: the reader names
the vehicle, and the definition comes from the build's content (PLAN §5.3 content handshake).

Not in the snapshot, by design: the world's own state (deformation lives in `WorldGrid.DeltaUm` and the stamp log,
lane W; knocked-over trees in `BrvWorldData.TreeDown`), the engine-sim instance (rebuilt from `EngineState`), and the
presentation. A winch `target` is a fleet index, valid only inside the same fleet; network or save code maps it to a
stable vehicle id (open question below).

## Consequences

- `Vehicle.SnapshotRoundTrip` (PlayMode) snapshots mid-drive on asphalt and in the bog with the winch hooked,
  restores from bytes and requires the restored run to stay within 1 mm of the uninterrupted one (measured 0 m) and
  to end in a byte-identical state.
- The format is deliberately complete and large. The **network** form (PLAN §5.3, 20 Hz) is a separate, quantised
  `VehicleStateCodec` (lane N, M4) that sends pose, velocities, controls and a few flags, not the solver scratch.
- BRV v2 (M3-M4) adds shafts and rows; it gets **version 2** with the shaft block appended and a v1 reader kept for
  saves made with V0 (`Save.LoadsPreviousMilestones` from `m9`).

## Open questions (for the freeze)

1. Map `winch.target` to a stable vehicle id (needs lane N's ids) or drop vehicle-to-vehicle winching from snapshots.
2. Keep the tyre scratch fields (`sd` … `vsPre`, 16 doubles per wheel) or recompute them on load. They do not
   influence the next step today (each step rewrites them before use), so dropping them saves 512 bytes per car at
   no behaviour change; kept in v1 so "complete state" stays literally true.
