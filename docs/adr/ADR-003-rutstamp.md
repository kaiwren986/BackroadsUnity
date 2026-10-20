# ADR-003: RutStamp and the integer ground kernels

- **Status:** **accepted and frozen** at the `m2` tag (2026-10-19; M2 milestone gate 22/22, `docs\gates\M2.json`). Lane W, M2 T2.5. Changes need a new ADR and updated tests in the same commit.
- **Date:** 2026-10-19
- **Owners:** lane W (world, deformation). Callers: lane V (vehicles, through `Backroads.Deform.IStampSink`); lane N (M5 stamp batches).
- **Plan:** PLAN §2.2 (WorldGrid, DirtyRectQueue), §2.4 step 6, §2.6 (DeltaUm, "why micrometres"), §6.1 M2; 03 §4.4; 06 §5.4, §5.6.

## Context

The browser game deforms its float32 heightfield in place (`world.js` `rut`, `brush`, `relax`, 552f780 lines 137-403).
Unity must look and behave the same (`Deform.MatchesJs`), and from M5 every machine must end up with the same ground
from the same calls (`Deform.Deterministic`). Float code is not reproducible across CPUs and compilers without care
(Burst `FloatMode.Deterministic` is "verify", 03 §4.4); integers are. PLAN §2.6 made the deformation state `DeltaUm`
(int32 micrometres relative to canon H0) for that reason, and 06 §5.6 proposed quantised stamps.

## Decision

1. **Quantise first, then apply the stamp.** `IStampSink` (the M2 contract, JS arguments in JS coordinates) turns each
   call into a stamp; the stamp, not the arguments, is applied, logged and (from M5) sent. Every machine therefore runs
   the same integer kernel on the same integers.
2. **`RutStamp`, 18 bytes, `Pack = 1`, little endian:**

   | Offset | Size | Field | Unit | Range |
   |---|---|---|---|---|
   | 0 | 4 | `X` (int32) | JS world x, Q20 m (2^-20 m = 0.95 µm) | ±2,048 m |
   | 4 | 4 | `Z` (int32) | JS world z, Q20 m | ±2,048 m |
   | 8 | 2 | `Radius` (u16) | Q16 m (15.3 µm) | up to 0.99998 m |
   | 10 | 2 | `Depth` (u16) | 16 µm | up to 1.04856 m |
   | 12 | 2 | `Rate` (u16) | 8 µm per call | up to 0.52428 m |
   | 14 | 2 | `Dir` (u16) | binary angle of `atan2(fz, fx)`, 2π/65536 | full circle |
   | 16 | 1 | `Berm` (u8) | bit 7: directional spoil (`Dir` valid); bits 0-6: berm share in 1/100 | 0 to 1.27 |
   | 17 | 1 | `Wear` (u8) | `wearAdd` in 1/4 | 0 to 63.75 |

   Rounding is half away from zero; out-of-range values clamp. The direction keeps only its angle: the kernel tests the
   spoil cone against the unit direction (JS passes the wheel's ground-plane forward vector, whose horizontal length
   is `cos(pitch)`; on a 20° slope JS's cone is about 1° narrower). The decode of `Dir` is an integer CORDIC (31
   iterations, error below 1e-8), so no trigonometry runs on the applying machine.
3. **Provisional stamps** (frozen with the blade in M7): `BrushStamp` 12 B (`X`, `Z` Q20; `Radius` Q15 m up to 2 m;
   `Amount` int16 in 4 µm, ±131 mm) and `RelaxStamp` 13 B (`X`, `Z` Q20; `Radius` Q12 m; `MaxDiff` 16 µm; `Iters` u8).
   `cutBlade` and the deposits stay JS-only until M7.
4. **Integer kernels** (`StampKernels`, Burst job run synchronously, or the same code managed):
   - Horizontal fixed point Q20 m from the grid origin (one 0.5 m cell = 2^19), heights in µm (`DeltaUm`; canon
     H0 and BASE rounded once to µm at load, half away from zero).
   - `rut`: core test `d² < r²` and ring test `400 d² ≤ (20 r + 11·2^20)²` exactly in int64; `f = 1 − d²/r²` in Q20;
     target, step (`rate·f·(0.5 + 0.5 soft)`) and wear in fixed point with one rounding each; ring weight
     `1 − (d − r)/0.55 m` in Q16 from an exact integer square root; spoil `round(removed × berm)` split over the ring by
     largest remainder (ties by ring order), so no micrometre is made or lost; the JS berm cap `BASE + 0.6 depth` and
     the forward cone `dx·fx + dz·fz > 0.35 d` as in JS.
   - `brush`: smoothstep falloff from a 4,097-entry Q12-indexed table (indexed by `(d/r)²`, values Q20, built with
     integer arithmetic, linear interpolation), DIG_RATE in Q16, the JS clamps `[max(BASE − 12, −14), BASE + 12]`.
   - `relax`: angle of repose on absolute µm heights; `(d − maxDiff)/4` rounded.
   - Locked surfaces (ROAD, CONCRETE) never change. The derived float `H = H0 + (float)(DeltaUm · 1e-6)` is rewritten
     for each changed vertex with the expression `WorldGrid.RebuildH` uses.
5. **Dirty rectangles and tiles:** each stamp that changes something adds its world.js `markDirty` rectangle to
   `DirtyRectQueue`; the client drains it once per frame into partial terrain-chunk updates and tree re-snaps. Each
   stamp bumps the version of the 16 m tiles (32 × 32 cells) it touches; `TileTable.Hash` is xxHash3 over the tile's
   DeltaUm, WEAR and SURFACE (the M5 resync hash, 06 §5.6).

## Evidence (M2, lane W)

| Test | Expected | Measured (2026-10-19, Unity 6000.3.20f1, Burst 1.8.29) |
|---|---|---|
| `Deform.MatchesJs` (10,000 reference calls through `IStampSink`, max height error curve) | ≤ 0.5 mm over the first 1,000 calls, ≤ 2 mm over all; volume ±2 % | 0.0191 mm and 0.0191 mm (flat from call 500); displaced volume -0.0006 %; WEAR within 2 units, SURFACE identical |
| `Deform.SubMmConverges` (0.3 mm per call) | JS target depth within 1 % | dirt 75.602 mm for a 75.600 mm target, grass 37.800 / 37.800 mm; slow pass within 0.34 mm of a 28.7 mm rut |
| `Deform.Deterministic` (same stamp log, Burst on and off) | identical tile hashes | 10,200 stamps, 113 tiles changed, 0 tile hashes and 0 DeltaUm values differ (Burst/managed and Burst/Burst) |

Before measuring the integer path, `Deform.MatchesJs` proves its reference: `JsDeformReference`, a double-precision
mirror of the world.js functions on float32 arrays, reproduces all 10,000 per-call fnv1a hashes and
`reference/deform/final.json` bit for bit.

## Alternatives considered

- **06 §5.6's 16-byte layout with millimetre positions and a 6-bit direction.** Rejected: a millimetre in position
  or radius moves vertices across the core/ring boundary (`d = r`, where JS's spoil weight jumps from 0 to 1) about once
  per 300 calls, and a 6-bit direction moves ring vertices in and out of the spoil cone in about 8 % of directional
  calls. Each such flip is a millimetre-scale berm difference at one vertex. Measured on the 10,000 reference calls
  (the same kernels, only the quantisation changed): millimetre positions and radius give a max height error of
  13.5 mm; 06's layout (also millimetre depth and a 6-bit direction) 16.2 mm; the layout above 0.019 mm (0.015 mm when
  the arguments arrive as doubles). No vertex of the reference calls lies within this layout's quantisation error of
  either boundary (closest: 12 µm to the core boundary, 5.3e-5 to the cone edge), so its error is the kernel's own
  rounding.
- **Float kernels with Burst `FloatMode.Deterministic`.** Rejected: "verify" in 03 §4.4, and Mono (Burst off) would
  still differ.
- **Millimetre DeltaUm.** Rejected in PLAN §2.6: per-call steps of 0.1-1 mm round to nothing.

## Consequences

- `IStampSink` takes floats (the M2 contract): positions arrive as float32, about 15-30 µm apart at 256-512 m from the
  centre, before quantisation. Measured effect on `Deform.MatchesJs`: 0.019 mm instead of 0.015 mm. A double overload
  would remove it (For I, if the contract is revised).
- Bandwidth for M5 (06 §5.6 [calc]): 4 wheels × 20 merged stamps/s × 18 B ≈ 1.4 KB/s per vehicle in mud.
- Memory: two int32 layers (H0 and BASE in µm, 33.6 MB) beside WorldGrid's 113 MB.
