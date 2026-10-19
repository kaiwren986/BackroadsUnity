# ADR-001: CanonData v1 is a frozen contract

- **Status:** **accepted and frozen** at the `m1` tag (2026-10-18; M1 milestone gate 19/19, docs/gates/M1.json). Changes need a new ADR.
- **Date:** 2026-10-18
- **Owners:** lane W (world and rendering); changes need the user's approval (AGENTS.md rule 4) and the integrator's merge.
- **Plan:** PLAN §1.2, §1.8, §1.10 ("frozen contracts"), §2.2, §2.6, §6.1 M0/M1; 03 §2, §3.

## Context

Millbrook is canon: the town, terrain, roads and props of the browser game at oracle commit
`552f7807f4ad56547815c32a5e8407a7c324d556` must appear in Unity unchanged. Regenerating the world in C# would need a
bit-exact port of `world.js`, `townGen.js`, `props.js` RNG order and `trafficNet.js`, and still depend on the GLB
contents (03 §3.1). Instead, M0 exported the *outputs* once (T0.7) and M1 imports them (T1.1). This ADR fixes what that
export is, so every later lane (roads, vehicles, netcode, saves) can rely on it.

## Decision

`CanonData/v1/` is read-only from the M1 tag on. Nothing in it is edited, re-exported or re-encoded. Any change
(a deliberate deviation such as v1.1 road plates) goes into a *new* folder with its own manifest
(`CanonData/v1.1/`, `CanonData/refs/<name>/`) and is recorded in `CanonData/CHANGES.md` with the user's approval and
before/after hashes. Changing this contract needs a new ADR and updated tests in the same commit.

### Files (55, 117 MB; binaries through Git LFS, every path `-text`, no CR bytes)

| Group | Files | Content |
|---|---|---|
| Manifest | `manifest.json` | SHA-256 and byte size of the other 54 files; source commit and seeds (world 20251015, planner 20279734, props 4242); coordinate rule; grid layout; layer hashes; counts; static-prop bucket counts and position hashes; renderer; shot list; deform and trace summaries |
| Grid layers | `terrain/heights.f32`, `base.f32` (float32 LE, 2049 x 2049), `surface.u8`, `wear.u8`, `roadd.u8`, `mud.u8` (u8, 2049 x 2049), `tint.f32` (float32, 129 x 129 x 2) | Headerless, row-major `k = j*2049 + i`, `x = i*0.5 - 512`, `z = j*0.5 - 512` (JS space) |
| World data | `world.json`, `placements.json`, `vegetation.json`, `traffic.json` | Full-precision JSON (JS `JSON.stringify` doubles), JS space |
| Probes | `terrain/probes.json` | 1,000 `heightAt`/`normalAt`/`surfaceAt` probes; 12 x 4,096 camera rays with `rayTerrain` hits |
| Textures | `textures/detail.png`, `water_n.png`, `dirt.png`, `map.png` | RGBA8, rows as the JS DataTexture (row 0 = v 0) |
| References | `reference/static_props.glb`, `reference/cameras.json`, `reference/shots/*.png` (24), `reference/traces/*.json` (6), `reference/deform/{calls,final}.json` | Visual and physics parity references |
| Gameplay | `gameplay/vehicles.json`, `gameplay/jobs.json` | Oracle tables (reference only; shipped names follow U9) |
| Source | `source/canon.json`, `source/townProfile.json` | Verbatim canon inputs |

The frozen models it depends on live beside it, so v1 never changes: `CanonData/refs/models/` holds `buildings.glb`
and `trees.glb`, byte copies of the oracle blobs `e547a7938a64…` and `8b6a0e0b9d9c…`, with their own `manifest.json`
(SHA-256 `e966b6f6aee2…`, `21f2b4de5a4e…`). Pane names and order in `buildings.glb` index the lit flags of
`placements.json`; re-running `buildings.py` would break them (03 §3.3).

### Hashes that define v1

| Layer | SHA-256 |
|---|---|
| H `terrain/heights.f32` | `a8908d7f740ec10b723aeedd778f16b00a38ef7502ac03374bb61d45aaf154a0` |
| BASE `terrain/base.f32` | `2eda40821238d55f73999ebee506d7ca8736df6a6c64e7f5e31f6d370926072a` |
| SURFACE `terrain/surface.u8` (after props) | `8788d405d310617ef951a652c24c4939dadda777c51a0ec4ca8445419739c22f` |
| ROADD `terrain/roadd.u8` | `8d31f10915e1a8c5d06622f10df7815e41f9465bbed953ea108ff089123fbf32` |
| MUD `terrain/mud.u8` | `2192f4c8fdf91e0c2566a3c176774e7f46a4950e9107752c27a539daae46d015` |
| WEAR `terrain/wear.u8` | `85473f6f37e608431a5dadc307f14af862bade6a8729f44ec0f5c93f614080fb` |
| TINT `terrain/tint.f32` | `08452b464b4608b3a3deb8be497b9747c0a19d55064bc6e906f3a576ea825dad` |
| `manifest.json` itself (import cache key) | `a112c66d5c51…` (full value in the imported `CanonAsset`) |

Every other file's SHA-256 is in `manifest.json` → `files`. Counts that must hold (M0 gate, `Canon.Counts`): 5,265 trees
(Pine 933, Spruce 447, Oak 1,005, Birch 798, Poplar 182, Maple 140, Bush 1,491, Snag 18, Reeds 251), 4,013 colliders,
78 lamps (77 posts), 59 lots, 72 model placements (59 lots + 13 sites), 87 footprints, 7 rings, 5 parking bays,
2 boats, 24 roads with 1,722 points, traffic 21 nodes / 53 lanes / 108 connectors / 5 loops, `rngCalls` 1220, and
static-prop buckets bld 261,066, metal 27,276, winLit 14,436, winDark 11,952, bulb 6,228, neon 216, signRed 720 vertices.

### Coordinate rule (03 §2)

All of CanonData is in three.js space: right-handed, +Y up, metres, +X = west/left. Unity is left-handed. The
conversion happens at one boundary and nowhere else:

- position `(x, y, z)` → `(-x, y, z)`; direction and normal `(x, y, z)` → `(-x, y, z)`;
- yaw (three `rotation.y`, radians) → Unity euler y = `-yaw * 180 / pi`;
- quaternion `(x, y, z, w)` → `(x, -y, -z, w)`; three cameras look down -Z, so a camera's Unity rotation is the
  converted quaternion times a half turn about Y;
- `Matrix4.elements` (column-major) M → `S * M * S`, `S = diag(-1, 1, 1, 1)`;
- meshes generated from JS data negate x and swap two indices per triangle; world-space texture lookups sample
  `float2(-x, z)`;
- the grid core (`WorldGridQueriesRef`, the deform kernels) works in JS space on the JS-layout arrays and flips only at
  its public API (`HeightAt(p) = HeightAtJs(-p.x, p.z)`), so its arrays compare byte for byte with JS.

`Backroads.Core.BrCoords` and `Backroads.World.CanonTransforms` implement it; `Core.CoordsCanary` and
`World.PlacementsRoundTrip` test it.

### How Unity consumes it

`Backroads.Canon.Editor.CanonImporter` (menu *Backroads > Import Canon v1*, `ImportCli.Run`, `EnsureImported()` before
every build) verifies every SHA-256 of both manifests and aborts on any mismatch before writing anything, then writes
`Assets/Backroads/World/Canon/v1/` (gitignored, path-derived GUIDs, cached by the manifest hash and the importer
version): the 7 layers as `.bytes`, the ScriptableObjects `WorldInfo`, `PlacementSet`, `VegetationSet`, `SurfaceTable`,
`LegacyTrafficNetwork`, `CanonModelSet` (x2) and `StaticPropsSet`, and `Resources/BrCanonV1.asset` (`CanonAsset`), so
every player, the server included, carries canon. `CanonRuntime` loads it; `LoadAndVerify` re-hashes H at run time
(server canary `CANON v1 OK H=a8908d7f…`).

## Consequences

- Parity is testable forever: `Canon.ManifestHashes`, `Canon.Counts`, `World.HeightProbes` (bit-exact),
  `World.RayHits` (49,152 rays bit-identical), `World.PlacementsRoundTrip`, `Render.TerrainLod0BitExact`,
  `Render.PaneNamesResolve`, `Render.StaticPropsBuckets` and `tools/oracle/verify-canon.mjs` all read v1.
- Fixes to the canon world (junction plates, M3) are v1.1 overlays with their own hashes; v1's hashes never move.
- The oracle commit stays the source of truth: a second export (`tools/oracle/run-export.ps1 -Twice`) must reproduce
  every non-shot file byte for byte (M0: identical).
- LFS cost about 108 MiB once; no second copy is committed (the importer output is gitignored).
- Engines, vehicles and jobs are not frozen by this ADR; `gameplay/*.json` are reference tables only.
