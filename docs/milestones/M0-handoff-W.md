# M0 handoff: lane W, T0.7 canon export

Branch `m0/w-canon` in `D:\Backroads-Unity-wt\w`. Oracle: `D:\Backroads` at `552f7807f4ad56547815c32a5e8407a7c324d556`, read only through `git -c core.autocrlf=false archive`. Nothing was pushed, installed or downloaded. The oracle's working tree was not touched.

## Done

All five parts of the card, a) to e).

| Part | What | Where |
|---|---|---|
| a | Snapshot script. It reads the archive with its own tar reader and checks each of the 48 files against its git blob id. It then applies 11 exact-match hooks to the snapshot copy only. Each hook fails unless its anchor occurs exactly once. The hooks: `treeList`, `props.rocks`, `props.allOps` (plus `props.typed`), wrappers on `placeModel`, `lamp`, `silo`, `dock`, `fuelStation`, `crusher` and `sawShed`, and the module tags. `determinism.js` is the first module. Hook anchors and the hashes of the hooked files go into `_snapshot.json` and from there into the manifest. | `tools/oracle/make-snapshot.mjs`, `tools/oracle/determinism.js` |
| b | Exporter, run in the page. It waits for `window.__game`. The loop never runs: every `requestAnimationFrame` callback is queued and the clock is frozen at 0. In one synchronous task it copies the 7 layers with `slice()` and serialises every JSON file. It then checks `sha256(WEAR)` and POSTs all of §1.8.4. Binary layers go as ArrayBuffers, JSON at full precision. `static_props.glb` comes from three's `GLTFExporter`. The 24 shots use `canvas.toBlob` right after `composer.render(0)`. The page also hashes the three.js module and records the WebGL renderer. The deform references run last. | `tools/oracle/exportUnity.js` |
| c | Receiver: zero dependencies, `node:http`, binds 127.0.0.1 only. It serves the snapshot and accepts `POST /__export/<relpath>` with path sanitising (each path once, `--out` must start empty). `done` writes `manifest.json` with SHA-256 per file, the PRNG seed, the renderer and the rest of the metadata, then exits 0. `run-export.ps1` starts the receiver on 8131 (or the next free port) and launches headless Edge per §1.8.3 with a fresh temporary profile. It waits up to 10 min, then kills only the Edge processes that use that profile and deletes the profile. It then verifies. `-Twice` compares two exports and `-Install` copies the result into `CanonData\v1`. `Export-Canon.bat` (CRLF) is the manual fallback with a normal Edge window. | `tools/oracle/serve-export.mjs`, `tools/oracle/run-export.ps1`, `tools/oracle/Export-Canon.bat` |
| d | Node-only check of the world generator: 46/46 checks equal, `generate()` 0.62 s in Node 24.18.0. It covers the six layers, SURFACE before props, POI pads, roads, lots, canon ops and the headless traffic network (52/104/no roundabout). Gate check and double-export check are in place. CanonData v1 is committed. | `tools/oracle/gen-hash.mjs`, `verify-canon.mjs`, `compare-exports.mjs`, `pngread.mjs`, `expected.json` |
| e | Golden traces: 6 seeded runs with no AI traffic, input files committed. Deform references: 10,000 seeded `rut`/`brush` calls. | `tools/oracle/goldenTraces.js`, `tools/oracle/traces/*.json`, `CanonData/v1/reference/{traces,deform}/` |

## Gate results (PLAN §6.1 M0 oracle gate, 03 §3.2)

`node tools/oracle/verify-canon.mjs CanonData/v1`: **68/68 green**, recomputed from the files themselves. The same result holds on a fresh checkout of the commit (LFS smudge, no line-ending change).

| Check | Expected | Measured |
|---|---|---|
| H `terrain/heights.f32` | `a8908d7f740ec10b723aeedd778f16b00a38ef7502ac03374bb61d45aaf154a0` | same (16,793,604 B) |
| BASE `terrain/base.f32` | `2eda40821238d55f73999ebee506d7ca8736df6a6c64e7f5e31f6d370926072a` | same |
| SURFACE after props `terrain/surface.u8` | `8788d405d310617ef951a652c24c4939dadda777c51a0ec4ca8445419739c22f` | same (before props `16d3b0e4a3e018e4…` in Node) |
| ROADD `terrain/roadd.u8` | `8d31f10915e1a8c5d06622f10df7815e41f9465bbed953ea108ff089123fbf32` | same |
| MUD `terrain/mud.u8` | `2192f4c8fdf91e0c2566a3c176774e7f46a4950e9107752c27a539daae46d015` | same |
| WEAR `terrain/wear.u8` | `85473f6f37e608431a5dadc307f14af862bade6a8729f44ec0f5c93f614080fb` | same (2,168 non-zero) |
| TINT `terrain/tint.f32` | `08452b464b4608b3a3deb8be497b9747c0a19d55064bc6e906f3a576ea825dad` | same (133,128 B) |
| Trees | 5,265: Pine 933, Spruce 447, Oak 1,005, Birch 798, Poplar 182, Maple 140, Bush 1,491, Snag 18, Reeds 251 | identical |
| Colliders | 4,013 (circle tree 3523, rock 263, lamp 77, post 15, pump 4, boat 2, building 3; box building 80, fence 39, logs 3, bale 2, dock 2) | identical, kind by kind |
| Lamps | 78 | 78 |
| Lots | 59 | 59 |
| Model placements with y, sx, sz, matrix | 59 | 59 lot placements, which replay the canon ops in order (plus 13 site placements, 72 in all) |
| Lamp-post records with faceRot, h | 77 | 77 |
| Footprints / rings / parking bays / boats | 87 / 7 / 5 / 2 | 87 / 7 / 5 / 2 |
| Roads, points | 24, 1,722 | 24, 1,722 |
| Traffic | 21 nodes / 53 lanes / 108 connectors / 5 loops (1 car-only), roundabout at (−70, 30) R 12.5 | identical |
| rngCalls | 1220 | 1220 (canon.json; the burn is proved by the unchanged tree counts and SURFACE hash) |
| static_props.glb vertices | bld 261,066 · metal 27,276 · winLit 14,436 · winDark 11,952 · bulb 6,228 · neon 216 · signRed 720 | identical, parsed from the GLB; the GLB position buffers hash as the page recorded |
| Pane counts | House_A 9, House_B 22, House_C 9, Shop_A 10, Shop_B 17, Warehouse_A 15, Warehouse_B 2, Warehouse_C 0, Barn 6, Shed 0 | identical (Garage is not placed in canon) |
| Shots, probes, rays | 12 cameras × 10:00/21:00 at 1600×900; 1,000 probes; 12 × 4,096 rays | 24, 1,000, 49,152 |
| Manifest names the renderer | yes | `ANGLE (NVIDIA, NVIDIA GeForce RTX 5080 Laptop GPU (0x00002C59) Direct3D11 vs_5_0 ps_5_0, D3D11)`, headless Edge 155 |

Bucket position hashes, which the M1 test `Render.StaticPropsBuckets` can use: bld `ec309c2c44fc8e54…`, metal `f05adbe60b01ff6b…`, winLit `4a81ee0e73737765…`, winDark `5d04a8bbc1dfd713…`, bulb `116d330188c23308…`, neon `ba1b9986c922b082…`, signRed `2839cfb29bf0e372…`. The full hashes are in `manifest.json` → `staticProps`.

**No number in the plan disagreed with the oracle.** Every value of 03 §3.2 and of the M0 gate was reproduced exactly.

## Double export

`run-export.ps1 -Twice` compared two exports with `compare-exports.mjs`. The manifests were **identical** apart from `exportedAt`, `userAgent` and `timings`. Shots: **23 of 24 byte-identical**; the other had min SSIM 1.00000 (threshold 0.99).

Across all runs, 0 to 2 of the 24 shots differed by GPU noise; every non-shot file was identical in every run. A SwiftShader export also matched the GPU export in every data file. Its shots reached SSIM ≥ 0.990 against the GPU shots.

## CanonData v1

- **117 MB, 55 files** (54 plus `manifest.json`).
- **36 binaries through LFS: 108 MiB.** These are the 7 layers, 4 textures, 24 shots (47 MiB) and `static_props.glb` (11.6 MB).
- **19 JSON files in git: 8.2 MiB.** The largest is `terrain/probes.json` at 3.8 MB.
- No CR byte in any JSON file. Line endings and LFS behave as `.gitattributes` intends.

Layout follows 03 §3.3 plus:
- `reference/cameras.json`;
- `reference/deform/{calls,final}.json`;
- `reference/traces/*.json`;
- `gameplay/{vehicles,jobs}.json`;
- `source/{canon,townProfile}.json`;
- `terrain/probes.json`.

Golden traces (no traffic, seeded, 1/120 s steps, sampled every 0.1 s):

| Trace | Result |
|---|---|
| `pickup_0_100_route9` | 100 km/h in about 7 s, then coasts; 95.7 km/h at Route 9 Fuel after 9 s |
| `pickup_brake_80` | Brakes at 80 km/h and stops at about 7 s |
| `truck_0_80_route9` | Starts on the depot road and drives through Millbrook; 80 km/h, then coasts |
| `truck_quarry_low` | Low range: 9 km/h, held at about 2,710 rpm, up the paved quarry road for 30 s |
| `hatch_bog_10s` | Stuck in Blackwater Bog after 3.5 s, mudSink 0.29 m |
| `winch_15m_tree` | Hatch stuck in 0.90 m bog mud hooks a snag (tree 5005) at 14.93 m; it is winched about 10 m out of the mud and stops 4.8 m short of the snag |

Every route trace stays within 3 m of a road centreline. Deform: 14,565 heights, 13,853 wear and 82 surface vertices change; the final layer hashes are in the manifest.

## Deviations and decisions (all within the card; none changes canon)

1. **`determinism.js` is active only with `?export=`, `?trace=` or `?det=1`.** Without these, the snapshot plays like the original. Seed 20261018, recorded in the manifest. `Math.random` calls: 57,310 at the freeze. They come from stars, traffic car set-up, job offers and boats, and are identical in every run.
2. **Wrapper records carry more than the bare arguments**, but still only pure reads and no RNG:
   - `placeModel` also records `w, d, colors, lit` and `replay`. `lit` is the string `rec('model')` just pushed.
   - `fuelStation` adds the derived canopy pose `x, z, rot, y` after the call. It reads the ring the call set and `W.nearestRoad`.
   - `lamp` records `y` before the call; `crusher` and `sawShed` record `groundY` before the call.
3. **Private constants are read from the served module source.** `SPECIES` and `MODEL_CORE` (props.js), `SUPPLY`, `DEMAND`, `TANK` and `RING_R` (jobs.js) and `META` (vehicleModels.js) are not exported. The exporter parses their object literals from the snapshot's own module text rather than adding more hooks.
4. **Textures:**
   - `dirt.png` repeats the calls of the private `getDirtTex()`.
   - `map.png` is hud.js `buildMapImage()` run from its own source on a CPU-backed canvas (`willReadFrequently`). The game's accelerated canvas draws the anti-aliased roads differently on GPU and SwiftShader; the CPU canvas gives the same bytes with both.
   - All PNGs are RGBA8, rows not flipped, encoded in the page with `CompressionStream`.
5. **Rock `v` (brightness)** is read back from the instance colour as float32. props.js does not keep the double.
6. **No garage building.** `reserveParking` does not place its garage behind the 5 spawn bays in this canon (`rectClear` fails), so no building has `src: "parking"`.
7. **`placements.json.typed`** holds the wrapper records only (model, lamp, silo, dock, fuelStation, crusher, sawShed).
   - Fences, mailboxes and site parking lots are in `ops` (the `rec()` stream); canon claims and concrete are in `source/canon.json`.
   - Containers, hay bales, log piles, the crane and the fountain render from `static_props.glb`, as §1.8.2 says. 03 §3.3 listed more typed kinds than §1.8.2 asks for.
8. **The winch trace changed before handoff.**
   - The first committed v1 had a pickup winching to a lone oak on dry grass. It simply drove into the tree and ran it over, so it tested nothing.
   - The trace now winches a hatch stuck in the bog, which is what the winch is for (03 §3.6).
   - The re-export (third commit) changed only `reference/traces/winch_15m_tree.json`, two night shots (GPU noise) and `manifest.json`. No layer, placement or count moved.
   - v1 has not been merged or used yet; it freezes at the M1 gate (T1.9).
9. **Traces drive analog inputs through the gamepad path** of `vehicleControls()`: `input.gp.active`, with the harness owning `pollGamepad`.
   - The input files hold open-loop run-length channels. These were produced once by a pure-pursuit "teach" mode (`&teach=1`) and committed. Replay is open-loop, so Unity's `AutotestRunner` can replay them unchanged.
   - `localStorage` is cleared after each trace so every page boots from the same state.
10. **More tools than the §1.2 tree lists:** `expected.json`, `verify-canon.mjs`, `compare-exports.mjs`, `pngread.mjs` and `traces/`.
11. **Robustness additions:**
    - The receiver exits 6 when no request arrives for 120 s; `run-export.ps1` then retries once with a fresh profile.
    - One early manual-fallback test stalled after the 4th trace. It used a fresh Edge profile without the quiet flags. The rerun passed, and the quiet flags (`--disable-background-networking`, `--disable-component-update`, timer and backgrounding flags) are now used on both paths.
    - The SwiftShader fallback works: 113 s without traces, renderer recorded.

## For other lanes

- **I:** the M0 gate text says `oracle.ps1 -Export`. The command is `tools\oracle\run-export.ps1`; `node tools\oracle\verify-canon.mjs CanonData\v1` is the quick re-check and `node tools\oracle\gen-hash.mjs` the CI one.
- **I:** `Repo.NoCrInHashedFiles` must skip LFS binaries in the working tree. `.f32`, `.u8`, `.png` and `.glb` contain 0x0D bytes by nature. Their git blobs are LFS pointers without CR. No JSON in `CanonData` or `tools/oracle/traces` contains a CR.
- **V / U9:** `gameplay/vehicles.json` copies `DEFS` verbatim, so it contains the oracle's vehicle names, including "Ranger 4x4". This is reference data, not shipped content; the shipped names still need the U9 renames and `Content.NoRealMarks`.

## Next (M1, lane W)

- **T1.1 `CanonImporter`.** Read `manifest.json` and verify every SHA-256, then copy the layers to `.bytes`. Build `WorldInfo`, `PlacementSet` (with `typed`), `VegetationSet`, `SurfaceTable` and `LegacyTrafficNetwork`. `Canon.ManifestHashes` and `Canon.Counts` can take their expected values from `tools/oracle/expected.json`.
- **T1.2 `WorldGrid`** against `terrain/probes.json`. Probe fields `[x, z, heightAt, nx, ny, nz, surfaceAt]`; rays `[dx, dy, dz, t]` from `rays[i].origin` with `maxT` 1500 and step 0.5.
- **Deform parity test** from `reference/deform/calls.json`. Each row carries a per-call FNV-1a fingerprint, and the final sparse differences and layer hashes are in `final.json`.

## Resume

```powershell
git -C D:\Backroads-Unity-wt\w log --oneline -4
node D:\Backroads-Unity-wt\w\tools\oracle\gen-hash.mjs                              # 46/46, 0.6 s
node D:\Backroads-Unity-wt\w\tools\oracle\verify-canon.mjs D:\Backroads-Unity-wt\w\CanonData\v1   # 68/68
# full re-export into artifacts (does not touch CanonData\v1):
powershell -File D:\Backroads-Unity-wt\w\tools\oracle\run-export.ps1 -Twice
# re-bake a trace input after editing its teach block (then copy trace-inputs\<id>.json into tools\oracle\traces\):
#   start serve-export.mjs on an empty --out, open index.html?trace=<id>&chain=1&teach=1 in headless Edge
```
