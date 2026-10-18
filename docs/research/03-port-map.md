# 03 — Port map: Backroads (three.js) → Unity

Research date 2026-10-18. Source inventoried: `D:\Backroads` at commit `552f780` ("Town generator from real small towns; Millbrook made canon"), 24 JS modules (9,814 lines), 3 Blender generators, 7 GLB files, `assets/towns/canon.json` (frozen 2026-10-18), `tools/osm`.

Numbers marked **[measured]** came from running the real code during this research: the world generator headless in Node 24, and the full game in the desktop app's Chromium 152 browser pane at `http://localhost:8130`. Probe scripts are in the session scratchpad (`...\scratchpad\probe\gen.mjs`, `net.mjs`, `size.mjs`, `glb.mjs`, `gears.mjs`). No repo source file was changed.

---

## 0. Summary and key decisions

1. **Freeze the world by exporting it. Do not regenerate it in C#.** The canon town is not self-contained. `canon.json` holds streets, lots and placements, but it is replayed *on top of* the heightfield that `world.generate(20251015)` makes. Building heights are recomputed from that ground at load time (`groundY()`). Tree, rock and site placements come later from the same `mulberry32(4242)` stream, and that stream is consumed conditionally (for example `if (rng() < 0.18)` runs only after a height or slope test passes). A C# re-implementation would have last-bit float differences (`Math.Sin`/`Pow` in .NET vs V8). Those flip threshold tests, shift the RNG stream and change *every later tree*. **[measured]** The JS generator is bit-identical in Node 24 and in Chromium 152 (same SHA-256 for every layer). So the JS build is a reliable oracle to export *from*.
2. **Export the final post-boot state from the JS build** (heightfield layers, roads, lots, every placement, colliders, trees, rocks, lamps, the traffic network and the procedural textures) into a versioned, hashed folder (`CanonData/v1`, §3). Unity imports that folder and never runs JS-derived generation. The JS repo stays as the frozen generator and reference oracle.
3. **One coordinate rule.** JS/glTF space is right-handed (+Y up, model front +Z, +X = model's left). Unity is left-handed. Use the same flip glTFast applies to meshes: **Unity = (−x, y, z)**, Unity yaw° = −yaw_rad·180/π, matrices `S·M·S` with `S = diag(−1,1,1,1)`. The grid core is ported verbatim in JS coordinates and flipped only at its public API (§2).
4. **Port the gameplay simulation; replace the presentation.** Vehicle physics, mud, deformation, winch, blade, traffic AI and jobs are custom, self-contained math. Port them to C# (Burst where hot) and keep golden-trace parity tests against the JS build. Sky, post-processing, particles, water, audio, HUD and input map onto Unity features and packages.
5. **"Proper street intersections" needs new code, not a port.** Today every road is an independent asphalt ribbon. Junctions are overlapping ribbons with a polygon offset, and the centre line fades out 6–16 m from a junction end. The exported traffic-network nodes (position, arms, radius `R`, major road, roundabout circle) are the right input for a new C# junction mesher (§4.5).

Classification legend: **(a)** port the data as-is · **(b)** port the algorithm to C# · **(c)** replace with a Unity feature or package · **(d)** drop.
Effort legend (one developer with AI help, including tuning to parity): **S** ≤ 3 days · **M** 1–2 weeks · **L** 3–6 weeks.

---

## 1. Inventory at a glance

| File | Lines | Responsibility | Class | Effort |
|---|---|---|---|---|
| `js/world.js` | 1029 | Heightfield arrays, sampling, ray march, deformation, full world generation, road routing and carving | (a) layers + (b) sampling/deformation; generation (d) at runtime | M |
| `js/terrain.js` | 455 | 16×16 chunk mesh, 4 LODs, skirts, partial updates, two-surface terrain shader | (b) | L |
| `js/roads.js` | 90 | Asphalt ribbons plus marking shader | (b) + new junction mesher | M (+L for junctions) |
| `js/townGen.js` | 509 | Town planner; canon rebuild (`rebuildCanon`, `gradeLot`); `canonSnapshot` | (a) output only; planner stays in JS | S |
| `assets/towns/canon.json` | 57 KB | Canon streets, hamlets, lots, placement ops | (a) verbatim | S |
| `js/townFreeze.js` | 20 | Writes canon.json from the browser | (d) (keep in JS) | — |
| `js/townProfile.js` | 511 | OSM-measured town statistics | (a) as JSON; only needed if the planner is ever ported | S |
| `js/props.js` | 1522 | Buildings (GLB and box fallbacks), site dressing, lamps, parking, collider grid, trees, rocks, boats, light pools, night emissives | (a) placements + (b) small runtime parts | M |
| `js/grass.js` | 259 | Streamed instanced grass tufts | (b) | M |
| `js/sky.js` | 208 | Day/night clock, Preetham sky, sun/moon light, shadow-box snapping, IBL regen, fog, stars, moon | clock (b), rendering (c) | M |
| `js/main.js` | 680 | Boot, fixed-step loop, enter/exit vehicle, tow, winch anchor search, effects emission, lights, quality presets, save, grade shader | (b) glue + (c) post | M |
| `js/vehicle.js` | 1008 | Rigid body, raycast suspension, PGS tyres, engine/auto gearbox, low range/AWD, mud, hull contacts, statics, water, winch, dozer blade, vehicle–vehicle SAT | (b) | L |
| `js/vehicleModels.js` | 528 | GLB vehicle loading, LOD, material swaps, mud-on-bodywork shader, lights, cargo crates, procedural fallbacks | (c) import + (b) dirt shader | M |
| `js/character.js` | 195 | On-foot controller, swim, collisions, box-man rig, shovel | (b) logic, (c) model/animation | M |
| `js/camera.js` | 155 | Chase, hood and on-foot cameras | (b) (Cinemachine optional) | S |
| `js/trafficNet.js` | 505 | Lane graph, junction connectors, roundabout, dead-end loops | (a) export network; (b) later if roads change | S (import) / M (port) |
| `js/traffic.js` | 873 | AI drivers: ghosts, pure pursuit, IDM-style speed, junction reservation, roundabout gap acceptance, bypass, rescue | (b) | L |
| `js/jobs.js` | 273 | Freight boards, cargo, payouts, refuel, wash | (a) tables + (b) logic | S |
| `js/hud.js` | 386 | DOM HUD, minimap, big map, labels, map image | (c) UI Toolkit + (b) map bake | M |
| `js/particles.js` | 133 | Pooled point sprites: dust, clod, smoke, mud, splash | (c) VFX Graph / Shuriken | S |
| `js/audio.js` | 148 | WebAudio synth: engine, skid, roll, wind, horn, thud, crunch, chime | (c) engine-sim audio + AudioSources | M |
| `js/input.js` | 75 | Keys, pointer lock, gamepad | (c) Input System | S |
| `js/water.js` | 25 | One plane at y = 0 with scrolling normals | (c) | S |
| `js/textures.js` | 109 | Seeded procedural textures | (a) export PNGs (or (b), trivial) | S |
| `js/noise.js` | 118 | mulberry32, Simplex2, fbm, ridged, tileable noise | (b) only for runtime users (grass tiles) | S |
| `tools/blender/*.py` | 3 files | Vehicle, tree and building generators → GLB | (a) keep; add Unity export notes | S |
| `tools/osm/*.mjs` | 2 files | OSM fetch and measure → `townProfile.js` | (d) stays a Node tool | — |
| `server.mjs`, `Play.bat`, `index.html`, `style.css` | — | Local web hosting, DOM | (d) (style values reused for UI) | — |

---

## 2. Coordinate, unit and colour conventions (read before porting anything)

**World:** metres, +Y up, `WATER_Y = 0`. Island `SIZE = 1024`, `HALF = 512`, grid `CELL = 0.5`, `N = 2048` cells, `VN = 2049` vertices per side. Grid index `k = j*VN + i`, `x = i*0.5 − 512`, `z = j*0.5 − 512` (JS coordinates).

**Conversion (JS → Unity):**

```csharp
public static class BrCoords {
    public const int VN = 2049; public const float CELL = 0.5f, HALF = 512f;
    public static Vector3 Pos(float x, float y, float z) => new(-x, y, z);
    public static float YawDeg(float yawJsRad) => -yawJsRad * Mathf.Rad2Deg;          // three rotation.y -> Unity euler y
    public static Quaternion Rot(float qx, float qy, float qz, float qw) => new(qx, -qy, -qz, qw);
    static readonly Matrix4x4 S = Matrix4x4.Scale(new Vector3(-1, 1, 1));
    public static Matrix4x4 Mat(float[] e) {                                          // three Matrix4.elements, column-major
        var m = new Matrix4x4(new(e[0], e[1], e[2], e[3]), new(e[4], e[5], e[6], e[7]),
                              new(e[8], e[9], e[10], e[11]), new(e[12], e[13], e[14], e[15]));
        return S * m * S;
    }
    // Unity Terrain column for JS grid column i (terrain placed at x = -512): col = 2048 - i, row = j
}
```

glTFast (`com.unity.cloud.gltfast`) converts glTF by negating X (UnityGLTF is believed to use the same convention; verify with one asset if you switch importers), so GLB assets and exported world data end up consistent under this rule. A model's front stays +Z, and its left (glTF +X) becomes Unity −X, which is correct for Unity.

**Grid core stays in JS coordinates.** Port `heightAt`, `normalAt`, `rayTerrain`, `rut`, `brush`, `cutBlade`, `deposit*` and `relax` verbatim on the JS-layout arrays. The public C# API takes Unity positions and flips at the boundary: `HeightAt(Vector3 p) => HeightAtJs(-p.x, p.z)`. Normals come back as `(-nx, ny, nz)`. Direction arguments (`rut` fx/fz, blade axis ax/az, plane normals nx/nz) are passed with x negated. Plane offsets `c = p·f` are unchanged because both operands flip. Golden tests can then compare the C# arrays byte-for-byte with JS.

**Handedness-sensitive code**, which must be rewritten rather than copied when it runs in Unity space:

- `vehicle.js`
  - `steerAngle = -ctrl.steer * def.steer / (1 + 0.055|v|)`: the sign flips in Unity, where positive yaw turns right.
  - Wheel layout `sx = +1` means *left*. This is labelling only, since the layout is symmetric.
  - `DEFS.*.seat.x` (+ = left = driver side) and `vehicleModels.js META.*.exhaust[0]` both flip sign.
- `main.js` `exitVehicle`: the driver steps out on local +x, which is the left in JS and −x in Unity. The headlight offsets (`±0.32w`) are symmetric.
- `trafficNet.js`
  - `offsetRight` (`x − tz·off, z + tx·off`): right of the heading in JS is `(-tz, tx)`; in Unity it is `(tz, -tx)`.
  - `fitLoop` uses `nr = (-t.z, t.x)` ("right of arrival").
  - `roundaboutConns`: tangent `T(ph) = (sin ph, −cos ph)`, `span0 = (aIn − aOut) mod 2π`, and arcs stepping `phIn − sp·k/steps`.
  - Simplest fix: import the network from the export (§3) instead of porting it.
- `traffic.js`
  - `lx = fz, lz = −fx // left`.
  - Bypass offsets `x += tz·off; z −= tx·off` (+ = left).
  - `lat = (px−qx)·tz − (pz−qz)·tx`.
  - `clearBehind` uses `l = dx·fz − dz·fx`.
  - `roundaboutClear` does phase arithmetic.
  - In Unity: left of forward `(fx, fz)` is `(-fz, fx)`, right is `(fz, -fx)`.
- `camera.js` shoulder offset `rx = −cos(yaw), rz = sin(yaw)` and `character.js` strafe vector: same left/right swap. Mouse-yaw sign: `orbitYaw −= dx`.
- `hud.js`: the map image is in JS grid space (`px = x + 512`). Convert a Unity position with `px = −x_u + 512`. The minimap `theta` formula needs re-deriving.
- `sky.js`: `sunDir` is a world vector, so negate x and the sun keeps rising on the same side of the island.
- **Meshes generated from JS data** (terrain chunks, road ribbons): negating x reverses triangle winding. Swap two indices per triangle and negate `normal.x`.
- **World-space texture lookups** in the terrain, road and grass shaders (`vWPos.xz * k`): sample `float2(-worldPos.x, worldPos.z)` to reproduce the exact pattern.

**Colour:** three.js r170 with colour management converts every `0xRRGGBB` palette constant from sRGB to linear. Vertex colours and instance colours are linear. In Unity (Linear colour space), material colour properties set from the inspector are converted for you. Values written to `Mesh.colors`/vertex streams are **not** converted, so write `color.linear`. Tree GLBs carry `COLOR_0`; three.js treated it as linear, so do the same.

**Lighting units:** the light intensities (sun 3.4, hemisphere 0.22–0.32, headlight 420, lamp 32·night, exposure 0.95 + 0.45·night with ACES) are artistic, not physical. Re-light in Unity rather than transferring the values, especially with HDRP physical units.

---

## 3. Guaranteeing the canon world: what it depends on, export format, import, verification

### 3.1 Dependency chain (why "just port canon.json" is not enough)

Boot order (`main.js boot()` → `world.generate(20251015)` → `buildProps(scene)` → `Terrain`, `Grass`, `buildRoadMeshes`, …, `new Traffic`):

1. `generate(seed)`:
   - Setup: `rng = mulberry32(20251015)`; `sA, sB, sC = new Simplex2(rng)` three times. Each `Simplex2` constructor makes 255 rng calls (Fisher–Yates on a 256-entry permutation).
   - `genMacro`: coarse 2 m grid, Catmull-Rom upsample, fine detail.
   - `flattenPads`: POI pads.
   - `shapeSites`: harbour quay and farm fields.
   - `buildRoads`: A* on a 4 m grid, run splitting, `makeRoad`, `carveRoad`.
   - `planTowns(mulberry32(20251015 ^ 0x7031 = 20279734))`. With CANON present it calls `rebuildCanon`: it carves the 15 canon streets from their stored `[x,y,z]` points (y rounded to mm), then re-grades all 59 lots, last to first, to their stored `padY`. Grading only touches ground where `ROADD ≥ 9.4` (that is, `(3.1+1.6)·2` in 0.5 m units) and not ROAD surface, so the result depends on the through-road carve.
   - `classifySurfaces(sB)`.
   - `makeMud(sB, rng)`: it continues the *same* `rng` (two phase draws per dirt road, then 6 bog satellites × 4 draws).
   - `buildTint(sC)`; `BASE = H.slice()`; `quarryPiles()`. The quarry piles are added after BASE, so they count as loose soil.
2. `buildProps(scene, seed = 4242)`, with `rng = mulberry32(4242)`:
   - `reserveParking()`: Millbrook garage lot, 5 spawn bays, garage warehouse (its own `mulberry32(5)`), 2 lamps.
   - `buildSite()` × 7: consumes `rng`.
   - `buildTown` → `buildLots`: canon replay. `replayLots` places the ops, then burns `rngCalls = 1220` draws so later placements stay identical. Then the fountain.
   - `placeRoadLamps`, `buildBoats`, `buildTrees(rng)`, `buildRocks(rng)`, `buildLightPools`.
   - Every placement stamps CONCRETE into `SURFACE` (`stampConcrete`, `y = null`, so heights are untouched) and adds colliders.
   - Building y is recomputed from the ground: `groundY()` takes the minimum `heightAt` over 4 corners plus the centre, then subtracts 0.2 for GLB models (0.25 for boxes).
3. `new Traffic` → `buildNetwork({clearance: staticClearance, soft: ringClearance})` depends on the props colliders and delivery rings. **[measured]** Without them (headless Node, default clearances) the network has 52 lanes, 104 connectors and no roundabout. In the browser it has 53 lanes, 108 connectors and the Millbrook roundabout.

So the canon *as seen in game* is a function of: all of `world.js` generation, `townGen.js` grading, `props.js` placement code and RNG order, `buildings.glb`/`trees.glb` content (pane order, model cores), and `trafficNet.js`. Only an export of the outputs pins all of that down.

### 3.2 Reference values the export must reproduce [measured]

Same results in Node 24 (headless `world.generate`) and in the browser right after boot.

| Layer | SHA-256 (full) |
|---|---|
| `H` (float32 LE, 2049², 16,793,604 B) | `a8908d7f740ec10b723aeedd778f16b00a38ef7502ac03374bb61d45aaf154a0` |
| `BASE` | `2eda40821238d55f73999ebee506d7ca8736df6a6c64e7f5e31f6d370926072a` (differs from H at 2,144 vertices: the quarry piles) |
| `SURFACE` after `buildProps` (concrete stamps) | `8788d405d310617ef951a652c24c4939dadda777c51a0ec4ca8445419739c22f` (straight out of `generate()`: `16d3b0e4a3e018e4…`) |
| `ROADD` | `8d31f10915e1a8c5d06622f10df7815e41f9465bbed953ea108ff089123fbf32` |
| `MUD` | `2192f4c8fdf91e0c2566a3c176774e7f46a4950e9107752c27a539daae46d015` |
| `WEAR` | `85473f6f37e608431a5dadc307f14af862bade6a8729f44ec0f5c93f614080fb` (2,168 non-zero vertices = quarry piles at 100; any other value means gameplay already deformed the ground) |
| `TINT` (129²×2 float32) | `08452b464b4608b3a3deb8be497b9747c0a19d55064bc6e906f3a576ea825dad` |

| Count [measured] | Value |
|---|---|
| Height range | H min −8.810 m (lake), max 175.202 m |
| POI pad heights `p.y` | town 13.335, depot 11.098, farm 29.450, quarry 44.381, mill 20.912, harbor 2.008, fuel 9.386 |
| Roads | 24: 6 paved through roads (hw 3.6), 3 dirt (hw 2.7), 15 canon streets (hw 3.1); 1,722 points; 3.40 km |
| Lots / hamlets / bogs / fields | 59 / 2 (Cedar Flats, Oxbow) / 1 (Blackwater Bog at (−215.85, 38.73), r 40) / 2 |
| Canon ops | 59 model placements (34 houses, 17 shops, 2 warehouses, 6 sheds), 59 claims, 51 concrete, 39 fences, 34 mailboxes, 22 yard trees, 1 box; 0 parking; `rngCalls` 1220 |
| Trees | **5,265** (README says "~6,000"): Pine 933, Spruce 447, Oak 1005, Birch 798, Poplar 182, Maple 140, Bush 1491, Snag 18, Reeds 251 |
| Colliders | 4,013: circle tree 3523, rock 263, lamp 77, post 15, pump 4, boat 2, building 3; box building 80, fence 39, logs 3, bale 2, dock 2 |
| Lamps / parking bays / footprints / rings / boats | 78 (77 posts + fountain) / 5 / 87 / 7 / 2 |
| Traffic network | 21 nodes, 53 lanes, 108 connectors, 5 dead-end loops (1 car-only), 1 roundabout at (−70, 30), R 12.5; build time 109 ms |
| Merged static prop meshes (vertices) | bld 261,066 · metal 27,276 · winLit 14,436 · winDark 11,952 · bulb 6,228 · neon 216 · signRed 720 |
| Generation time | 0.6 s (Node), so regenerating is cheap. Exactness, not speed, is the constraint |

### 3.3 Export format — `CanonData/v1/`

Headerless little-endian binaries plus JSON. Sizes and hashes live in the manifest. Commit the folder to the Unity repo with **Git LFS** for `*.f32`/`*.u8`. Gzip estimates **[measured]**: heights 14.0 MB, base 14.0 MB, surface 0.15 MB, roadd 0.05 MB, mud 0.07 MB, wear 0.004 MB.

```
CanonData/v1/
  manifest.json
  terrain/heights.f32     float32[2049*2049]  W.H at boot (row-major k = j*2049 + i)
  terrain/base.f32        float32[2049*2049]  W.BASE (undeformed reference; ruts = BASE - H)
  terrain/surface.u8      uint8               W.SURFACE after buildProps (0..8, see surfaceTable)
  terrain/wear.u8         uint8               W.WEAR
  terrain/roadd.u8        uint8               W.ROADD (0.5 m units, 255 = far; 254 cap)
  terrain/mud.u8          uint8               W.MUD (0..255 -> 0..1.0 m soft-mud depth)
  terrain/tint.f32        float32[129*129*2]  W.TINT interleaved (tb, td), 8 m spacing
  world.json              grid, surface tables, POIs, lake, bogs, fields, hamlets, town, roads, lots
  placements.json         buildings, typed props, lamps, parking, rings, footprints, boats, colliders
  vegetation.json         trees (+ species table), rocks
  traffic.json            lane network
  textures/detail.png     makeDetailTexture(256) RGBA8 (mulberry32(99))
  textures/water_n.png    makeNormalTexture(256, 3) RGBA8 (mulberry32(7))
  textures/dirt.png       vehicle dirt noise 128² (mulberry32(31))
  textures/map.png        hud.js buildMapImage() 1024² (1 px = 1 m)
  reference/static_props.glb   three GLTFExporter of props.meshes (7 merged buckets, vertex colours)
  reference/shots/*.png        fixed-camera screenshots for visual parity (see 3.6)
  source/canon.json            verbatim copy
  source/townProfile.json      TOWN_PROFILE as JSON
```

**`manifest.json`**

```json
{
  "format": "backroads-canon", "version": 1,
  "source": { "repo": "kaiwren986/Backroads", "commit": "552f780", "worldSeed": 20251015,
              "plannerSeed": 20279734, "propsSeed": 4242, "canonFrozen": "2026-10-18",
              "three": "0.170.0", "exportedAt": "2026-10-18T00:00:00Z", "userAgent": "…" },
  "coords": { "space": "three.js right-handed, +Y up, metres", "toUnity": "(-x, y, z); yawDeg = -yaw*180/PI; M' = S*M*S, S = diag(-1,1,1,1)" },
  "grid": { "size": 1024, "half": 512, "cell": 0.5, "N": 2048, "VN": 2049, "waterY": 0,
            "layout": "k = j*VN + i; x = i*cell - half; z = j*cell - half" },
  "files": { "terrain/heights.f32": { "bytes": 16793604, "sha256": "a8908d7f…" }, "…": {} },
  "counts": { "trees": 5265, "colliders": 4013, "lamps": 78, "lots": 59, "roads": 24, "lanes": 53, "conns": 108, "loops": 5 }
}
```

**`world.json`**

- `surfaceTable`:
  - `names`: GRASS 0, DIRT 1, SAND 2, ROCK 3, ROAD 4, GRAVEL 5, CONCRETE 6, TRACK 7, MUD 8.
  - Per-surface arrays: `softness`, `grip`, `digRate`, `roughness`, `dust`, verbatim from `world.js` lines 16–23 (see §4.1).
  - `mudMax` 1.0.
- `pois`: `[{id,name,x,z,r,fall,kind,y}]`; harbor adds `quay {x,z,ux,uz,len:30,hw:7}`.
- `lake`: `{x:215,z:245,r:125}`.
- `bogs`, `fields` (`{x,z,w,d,rot}`), `hamlets`, `town` (`{radius:165, blockLen:100.24, blockDepth:74.8}`).
- `roads`: `[{id, kind: "paved"|"dirt"|"street-cross"|"street-parallel", hw, paved, startJ, endJ, len, pts:[[x,y,z,d],…]}]`. The full-precision points are what the bed was carved from. Keep the order of `W.ROADS`, because traffic edges reference road indices.
- `lots`: canon lot objects verbatim (`x,z,nx,nz,tx,tz,w,depth,zone,roadY,contiguous,padY,hamlet?`).

**`placements.json`**

- `buildings`: `[{model:"House_A", src:"lot|site|parking", x, y, z, rot, sx, sz, w, d, colors:{Wall:hex,Roof:hex,…}, lit:"1101001100", panes:["Glass.040",…]}]`.
  - `y` is the value actually used (`groundY − 0.2`).
  - `sx`/`sz` are clamped to [0.75, 1.25].
  - `panes` lists the glTF node names of the model's Glass children *in three.js traverse order*, which is the order `lit` indexes. Pane counts in the current `buildings.glb` **[measured]**: House_A 9, House_B 22, House_C 9, Shop_A 10, Shop_B 17, Warehouse_A 15, Warehouse_B 2, Warehouse_C 0, Barn 6, Garage 1, Shed 0.
  - **Re-running `buildings.py` renames and renumbers panes (`Glass.009` …), which silently breaks canon lit flags.** That is true in JS today as well. Freeze `buildings.glb` alongside the canon, or always map by explicit pane names.
- `boxes`: the Warehouse_A office annex collider op.
- `typed`: primitives that are not GLB models. Each record is `{type, x, y, z, rot, …params, color}`:
  - `lamp {h:6.5|6, faceRot}`
  - `fence {x0,z0,x1,z1,y,color}`
  - `mailbox {rot,color}`
  - `parkingBays {cx,cz,rot,w,d,y,withLamp}`
  - `silo {r,h}`
  - `containerStack {levels, colors[]}`
  - `hayBales`, `logPile {radii[], colors[]}`, `crusher`, `sawShed`, `dock`, `crane`, `fuelStation {canopy, pumps[], sign}`, `fountain`.
  - The geometry of each is a few boxes or cylinders (see `props.js` lines 474–747, 966–970). Port those builders or swap in prefabs. `reference/static_props.glb` is the exact visual fallback.
- `lamps`: `props.lamps` `[{x,y,z,gy}]` (bulb position plus ground y, for light pools).
- `parking`: `props.parking` `[{x,z,yaw}]` (the 5 player spawn and tow bays).
- `rings`: `props.rings` `{poiId:{x,z,y}}` (delivery zones, radius 7.5).
- `footprints`: `props.footprints` (big-map building rectangles, `{x,z,r,rot?,hw?,hd?}`).
- `boats`: `{x,z,ang}`.
- `colliders`: `P.colliders` as `{type:"circle",x,z,r,y0,y1,kind,tree?:index}` or `{type:"box",x,z,hw,hd,rot,y0,y1,kind}`.

**`vegetation.json`**

- `species`: the `SPECIES` table verbatim (§4.8).
- `trees`: `[[x,y,z,sp,s,rot,tint,hue], …]` (5,265 rows; y is ground height; the mesh sits at y − 0.2).
- `rocks`: `[[x,y,z,s,rot,v], …]`.

**`traffic.json`**

- `nodes`: `[{id,x,z,R,dead,arms:[{edge,out,ang}],major:[edgeId,edgeId]|null, rb:{C:{x,z},R}|null}]`.
- `segs`: `[{id, kind:"lane|conn|loop", x[], z[], s[], k[], cap[], next:[segId], edge?, from?, to?, node?, dir?, turn?, major?, minor?, noTruck?, yard?:nodeId, compat:[segId], rb?, phIn?, phOut?, R?, clear?}]`.
- `skipped`: the steps the network left out.

### 3.4 How to produce the export (JS side, a new file; the existing modules stay as they are)

- **Add `js/exportUnity.js`.** Run it via `index.html?export=unity`, or from the console *immediately after boot*. It must refuse to run if `sha256(W.WEAR) ≠ 85473f6f…`, because that means the player has already rutted the ground.
- **Delivery:** the File System Access API `showDirectoryPicker()` (works in Chromium on `127.0.0.1`), or a single store-only `.zip` download. For a repeatable command-line run, drive the installed Microsoft Edge with Playwright (`channel: 'msedge'`, `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`, project-local `npm i -D playwright`). The script `page.evaluate`s the exporter and writes the returned ArrayBuffers.
- **What is readable today without touching other modules:**
  - `window.__game` (`G`), `G.W` (the world module), `await import('/js/props.js')` → `props`, `colliders`.
  - `props.meshes` (for `GLTFExporter` → `static_props.glb`), `props.lamps/parking/rings/footprints/boatSpots`.
  - `G.traffic.net`, `G.jobs.rings`, `canon.json`.
  - Tree records hang off tree colliders (`colliders[i].tree`), but bushes and reeds have no collider.
- **Three one-line additions to `props.js` make the export exact.** None of them changes RNG consumption.
  - `export const treeList = trees`.
  - `props.rocks = spots`.
  - Make `rec()` always append to `props.allOps` (currently it records only during `buildLots`). This captures site buildings and typed props with their final `y`.
  - After adding them, re-verify every hash and count in §3.2 to prove nothing moved.
- **Write the full float precision.** JSON numbers from `JSON.stringify` round-trip doubles exactly. Do not round placements as `canonSnapshot` does (mm rounding is fine for street points only because carving already used the rounded values).

### 3.5 Unity import (editor-time)

`Assets/Backroads/Editor/CanonImporter.cs` (menu *Backroads → Import Canon v1*):

1. Verify every `sha256` in the manifest (`System.Security.Cryptography.SHA256`). Abort on mismatch.
2. Copy the binaries to `Assets/Backroads/World/*.bytes`. At runtime, read them with `TextAsset.GetData<float>()` / `GetData<byte>()` (NativeArray views, no parse) into the `WorldGrid` NativeArrays, which become the authoritative runtime copies.
3. Build ScriptableObjects for `WorldInfo` (POIs, roads, lots, rings, lamps, parking), `Vegetation`, `TrafficNetwork` and `SurfaceTable`.
4. Spawn the static scene: building prefabs (from `buildings.glb` via glTFast), typed props, colliders. Optionally build a `TerrainData` (see §4.3).
5. Self-test: recompute `heightAt` at 1,000 seeded points on the C# side and compare with a JS-exported probe list (`terrain/probes.json`: `[x,z,heightAt,normalAt,surfaceAt]`). Tolerance 0 for surface, 1e-5 m for height.

### 3.6 Parity verification beyond hashes

- **Visual:** 12 fixed cameras (each POI, the bog, a junction, the roundabout) at 10:00 and 21:00. `G.noRender` and `G.update(dt)` already exist for scripted stepping. Capture `reference/shots/` from JS and the same poses in Unity; compare side by side.
- **Physics golden traces:** `G.update` is exposed, so a JS harness can feed scripted inputs at a fixed dt of 1/120 and record `pos/quat/vel/rpm/gear/mudSink` every 0.1 s. Suggested scenarios:
  - Pickup 0–100 km/h on the Route 9 asphalt.
  - Braking from 80 km/h.
  - Hatch driving into Blackwater Bog (sink depth and stuck time).
  - Loaded truck in low range up the quarry road.
  - Dozer 20 s blade cut (`cutBlade` volume).
  - Winch 15 m to a tree.
- Run the same inputs in C# from the same exported start state. Expect divergence after a few seconds (chaotic contact dynamics), so compare per-second statistics against tolerances, not exact positions.

---

## 4. Per-system port map

### 4.1 World data and sampling (`world.js` lines 1–128)

**What it does**

- Arrays:
  - `H` float32: live heights.
  - `BASE` float32: as generated.
  - `SURFACE` u8.
  - `WEAR` u8: ruts and disturbed soil; ≥170 turns GRASS into DIRT.
  - `ROADD` u8: distance to the road centreline in 0.5 m units.
  - `MUD` u8: soft-mud depth, 0..255 → 0..`MUD_MAX` = 1 m.
  - `TINT`: 129² × 2 floats, 8 m colour-variation grid.
- Surface tables (index = SURF id):
  - `SOFTNESS` [0.5, 1, 0.85, 0, 0, 0.16, 0, 0.3, 1]
  - `GRIP` [0.8, 0.74, 0.64, 0.9, 1.08, 0.86, 1.02, 0.9, 0.44]
  - `DIG_RATE` [1, 1, 1.2, 0.3, 0, 0.8, 0, 0.9, 1.4]
  - `ROUGHNESS` [0.5, 0.6, 0.4, 0.8, 0.08, 0.7, 0.12, 0.55, 0.3]
  - `DUST` RGB triples
  - `SURF_NAMES` for the HUD
- Sampling:
  - `heightAt`: bilinear, clamped to the grid.
  - `normalAt`: central differences at ±CELL.
  - `surfaceAt`/`wearAt`/`roadDistAt`/`mudAt`/`sinkAt`: nearest vertex.
  - `inBounds`: ±(512 − 2).
  - `rayTerrain`: march, then 7 bisection steps.
- `onDirty(fn)` listeners receive `(i0, j0, i1, j1)` vertex rectangles.

**Port: (a) + (b), S.**
- Implementation: `WorldGrid` (C#, `NativeArray<float>`/`<byte>`, Burst-compatible static functions), JS layout, flip at the API (§2).
- Dirty events become a `NativeQueue<int4>` drained once per frame by the terrain, grass, trees and collider systems.

### 4.2 World generation (`world.js` lines 405–1012) — keep in JS, never run in Unity

**Reference summary, in case someone must extend the world later:**

- **`macroHeight`**
  - Domain warp: 45 m at 1/310.
  - Base: `9 + 21·fbm(1/430, 3 oct) + 6.5·fbm(1/140 + offset, 3)`.
  - Northern ridges: `ridged(1/250, 4)·42·north + 6·north`, with `north = smoothstep(−60, 260, −z)`.
  - Edge mountains: superellipse `r = (x⁴+z⁴)^¼ / 512`, `rr = smoothstep(0.8, 0.995, r)`, `+rr²·(70 + 50·ridge)`.
  - Lake bowl: centre (215, 245), r 125·(1 + 0.22·noise), floor `max(−8.5, (ld−1)·11 − 0.5)`, blend `smoothstep(1.45, 0.95, ld)`.
  - Computed on a 2 m grid, Catmull-Rom upsampled separably, plus `0.38·noise(x/15) + 0.07·noise(x/4.7+31)` at full resolution.
- **`flattenPads`:** pad y = mean of 72 samples, clamped ≥ 1.7. Quintic blend over `fall`. Surface GRASS (town) or GRAVEL inside r−2. The town square r 26 is GRAVEL.
- **`shapeSites`:**
  - Concrete quay: |side| < 7, along −16…15, at the pad height, with a 2.5 m embankment.
  - Two furrowed fields (0.13 m amplitude, 1.7 m period, DIRT).
- **`buildRoads`:**
  - LINKS: town–fuel/depot/farm/harbor and fuel–mill/quarry are paved; farm–depot, mill–quarry and harbor–mill are dirt.
  - A* on a 256² grid at 4 m, 8-neighbour.
  - Cost: `len·(1 + sk·(22s² + 500(s−0.16)² if s > 0.16))`, plus `80·len` below y 1.2, plus `40·len·(rr−0.78)` past the 0.78 Chebyshev radius. `sk` is 1 for paved and 0.35 for dirt. Reuse of existing road cells costs ×0.3 (paved) or ×0.95 (dirt). The heuristic is 0.3·Euclidean.
  - Paths are split into runs at existing road cells, which become junctions.
- **`makeRoad`:**
  - Shape: 3× Chaikin, resample 2 m, x/z box smoothing (r 6, 3 passes), resample.
  - Heights: `y = max(ground, 0.8)`, smoothed (r 12, 4 passes).
  - Grade limit `g = max(0.11 paved / 0.15 dirt, 1.1·|Δy|/len)` with 6 forward/back passes, then smooth r 4 × 2.
  - `meetFlush` at junctions.
- **`carveRoad`:**
  - Per vertex: nearest-segment distance and interpolated height.
  - `ROADD = min(254, round(2d))`.
  - Weight 1 within hw + 0.9, quintic falloff to hw + 8.
  - Never re-carves existing ROAD.
  - ROAD/TRACK within hw + 0.2; GRAVEL shoulder to hw + 1.4 (paved).
- **`classifySurfaces`:** patch noise on a 4 m grid. Rules, in order:
  - SAND if h < 1 + 0.6·pn.
  - ROCK if slope > 1.05 + 0.3·pn, or (h > 95 and slope > 0.6).
  - DIRT if slope > 0.58 or pn > 0.42.
  - Else GRASS.
  - Map borders are ROCK.
- **`makeMud`:**
  - Dirt-road holes: `m = ½ + ½·sin(d/29+ph)·sin(d/67+ph2)`, stamped where `t = smoothstep(0.5, 0.78, m) > 0.02`, radius hw + 2.4, depth 0.18 + 0.32t.
  - Blackwater Bog at 45 % along the longest dirt road: R 30, depth 0.9, plus 6 satellites R 12–24.
  - Wet hollows on a 4 m grid: `fbm(x/85+33, z/85−17, 3) ≥ 0.33`, 0.4 < h ≤ 15, ROADD ≥ 24, gradient ≤ 0.16, R 3.6.
  - Finally: no mud on ROAD/CONCRETE/ROCK; `H −= 0.28·depth`; `SURFACE = MUD` where depth > 0.12.
- **`quarryPiles`** (after BASE): 4 gravel heaps.

**Port: (d) at runtime.** Keep it runnable in Node (it already is) as the oracle.
- If the world is ever extended, do it as a **versioned patch** on the exported layers (for example `CanonData/v2` = v1 plus a Unity-editor stamp). Never by changing generation code.
- A full C# port would be **L** and could never be bit-exact (§0.1). Not recommended.

### 4.3 Terrain chunks, LOD and shading (`terrain.js`)

**What it does**

- Chunks: 128 cells = 64 m per side, 16×16 = 256 meshes.
- LOD steps [1, 2, 4, 8]; distance thresholds [110, 250, 470] m × `lodScale` (0.7/1/1.3 by quality), ±10 m hysteresis.
- At most `buildsPerFrame` (1/2/3) builds per frame, nearest first. Keeps at most current ±1 LODs cached.
- Skirts on all 4 edges, depth 1.2 + 0.7·step (they hide LOD cracks; no stitching).
- Deformation updates rewrite only the dirty row band (`addUpdateRange`). Other LODs of a dirty chunk are discarded.
- Per-vertex attributes:
  - `position`; `normal` from central differences.
  - `color` and `color2`: colours of the vertex's own surface and of the dominant neighbouring surface, found with 4 taps at ±`st`, where `st = clamp(step, 1, 4)`.
  - `blendw`: tent-weighted 5×5 share of the second surface; 0.5 for a lone surface.
  - `wear` (0 on ROAD/CONCRETE).
  - `wet` (mud plus rut puddles).
  - `kindA`/`kindB` = (grass, rock, concrete, gravel) pattern weights; soil is the remainder.
- Colour palette `PAL`:
  - lush `0x46702f`, dry `0x8a8447`, alpine `0x5b6b45`
  - dirt `0x7a5a3b`, sand `0xcfba8f`, rock `0x8a7e70`, rockDark `0x4f4b47`, rockRed `0x8a6a52`
  - road `0x47474a`, gravel `0x857e72`, concrete `0x8c8a85`, track `0x917455`
  - mud `0x3b2a1a`, bogLight `0x55402b`, bogDeep `0x3b2b1c`, puddle `0x221b13`
  - dug `0x5c4231`, fill `0x82623f`, under `0x35503f`, snow `0xeef2f6`
- Colour rules:
  - Grass: lush→dry by TINT.td, alpine above 45 m.
  - Steep grass and dirt go rock-coloured (`smooth(0.62, 1, slope)·0.85`).
  - Snow above 118 m (rock above 120 m).
  - Dug and filled ground tints from `BASE − H`.
  - Wear darkens towards mud.
  - Underwater: `exp(−0.18·depth)`.
- Fragment shader:
  - Samples a 256² RGB tileable noise "detail" texture at many scales.
  - Crisp anti-aliased steps (`cel`, `fwidth`) and a ragged boundary between the two surfaces.
  - Per-kind tones:
    - Grass patches.
    - Soil pebbles.
    - Gravel stones.
    - Concrete 4 m slabs with joints.
    - Rock strata.
  - Mud swirls; standing puddles only on flat mud (`|n_face.y| > 0.95`).
  - Tyre streaks in worn soil.
  - Roughness: worn → 0.62, wet → 0.55/0.12, puddle → 0.04.

**Port: (b), L.** Recommended architecture:

- **Rendering: custom chunk mesh, ported** (Mesh API with `Mesh.MeshDataArray` built in Burst jobs; partial updates through `Mesh.SetVertexBufferData(data, start, meshStart, count, stream, MeshUpdateFlags.DontRecalculateBounds | DontValidateIndices)`).
  - Attribute packing:
    - `Color` = color.rgb + blendw
    - `TEXCOORD0` = color2.rgb + wear
    - `TEXCOORD1` = kindA
    - `TEXCOORD2` = kindB
    - `TEXCOORD3.x` = wet
  - Shader: Shader Graph (URP or HDRP) or HLSL with a custom function node.
  - Remember the winding and normal-x flip (§2).
- **Collision: none needed for vehicles.** Vehicle wheels, hulls, the character and the camera all query `WorldGrid` directly, as in JS. PhysX-driven extras (debris, knocked trees, a future ragdoll) need a collider; two options:
  - An invisible `TerrainData` 2049² with `TerrainCollider`, updated with `SetHeightsDelayLOD(col0, row0, float[,])` on dirty rects.
  - Or MeshColliders on near LOD0 chunks, re-cooked off-thread with `Physics.BakeMesh`.
- **Alternative: Unity Terrain for rendering.** It fits nicely: heightmap resolution 2049 is legal (2ⁿ+1, max 4097) and 1024 m / 2048 = 0.5 m exactly.
  - Settings: size (1024, 208, 1024), position (−512, −16, −512).
  - Height range: the playable range is −14 (dig floor `max(BASE−12, WATER_Y−14)`) to 189.2 (`BASE+14` pile cap). Use −16 … +192.
  - Precision: 16-bit storage, quantisation ≈ 3 mm (measured max error 1.6 mm at 65,535 levels; Unity may use about 15 bits, giving ~3.2 mm; check `TerrainData` docs).
  - Downsides: the per-vertex two-surface look would have to be rebuilt from extra data textures (SURFACE R8 2049², WEAR, MUD, BASE−H) updated with `Texture2D.SetPixelData` plus `Apply()` or a compute shader. Runtime `SetHeights` calls cost CPU and GPU time.
  - Benefit: free editor sculpting, tree, detail and hole systems.
  - **Recommendation:** custom mesh for parity, with the invisible-TerrainData collider only if PhysX needs ground. Prototype both in week 1 and measure `SetHeightsDelayLOD` cost with 4 wheels rutting at 60 Hz.

### 4.4 Ground deformation (`world.js` lines 130–403, callers in `vehicle.js`/`main.js`)

**Functions**

- **`rut(x, z, radius, depth, rate, wearAdd, berm = 0.45, fx, fz)`: tyre compaction**
  - Inside the radius, `f = 1 − (d/r)²`; target = `BASE − depth·soft·f`; moves at most `rate·f·(0.5 + 0.5·soft)` per call.
  - Wear `+ wearAdd·f·(0.4 + soft)`.
  - Spoil goes into a 0.55 m ring (berm share 0.45) and never in front of the rolling direction (`dx·fx + dz·fz > 0.35d` is skipped). The berm is capped at `BASE + 0.6·depth`.
- **`brush(x, z, radius, amount)`: shovel**
  - Smoothstep falloff; digging scales by `DIG_RATE`.
  - Clamped to [`max(BASE−12, −14)`, `BASE+12`]. Skips ROAD/CONCRETE.
  - GRASS/TRACK→DIRT; wear ≥ 150·f.
- **`cutBlade(cx, cz, ax, az, hw, hd, yCut, maxDh)`**
  - Loose soil above BASE goes at once; native ground is shaved by `maxDh·DIG_RATE` per step.
  - Side walls slope at `REPOSE = 0.8` over `SIDE = 4` m.
  - Wear 210.
- **Deposits**
  - `deposit`: oriented rectangle, weight `(1−|v|/hd)(1−0.4|u|/hw)`.
  - `depositMound`: `(1−d²)²`, cap BASE + 14.
  - `depositCapped`: blade spill. A cone `yTop − slope·d` is filled outward through rings R = 2.5, 4.5, 7.5, 10 m. Soil may ride 0.3·(1−d/R) over an existing heap. It returns the volume that found no room.
- **`relax(x, z, radius, iters = 2, maxDiff = 0.36, plane)`**
  - Angle of repose: only vertices with wear ≥ 100 give soil.
  - Per neighbour pair, moves `(d − maxDiff)·0.25`.
  - An optional half-plane freeze keeps soil out of the blade path.
- Locked surfaces (ROAD, CONCRETE) are never deformed.

**Port: (b), M.**
- Port verbatim as Burst jobs on `WorldGrid`. Calls are tiny rectangles (≤ ~10×10 m), so run them synchronously in `FixedUpdate`.
- Accumulate dirty rects. Each frame, push them to the terrain mesh, grass tiles, tree heights (`onTerrainChanged`: trees in touched 16 m cells re-snap their y and collider) and the optional collider.
- Golden test: apply a scripted list of calls in JS and C#; compare arrays byte-for-byte.
- **Online:** deformation is shared world state. A server-authoritative design must replicate the *calls* (deterministic inputs), not the arrays. Every client must run identical float math (Burst with `FloatMode.Deterministic` is the relevant option; verify).

### 4.5 Roads: carve data, ribbon mesh, markings, intersections (`world.js` roads, `roads.js`)

**What exists**

- Ribbon mesh:
  - One quad strip per paved road (dirt roads are only terrain colour), at `p.y + 0.045`, double-sided.
  - Half-width hw: 3.6 through roads, 3.1 streets.
  - Attributes: `roadUv = (u across 0..1, d along)` and `roadFade` (centre line fades out between 6 and 16 m from a junction end).
- Shader:
  - Asphalt `0.052` grey modulated by 3 detail octaves.
  - Edge lines at u 0.04–0.062 and 0.938–0.96, colour (0.62, 0.62, 0.6).
  - Dashed yellow centre (0.75, 0.52, 0.08) at u 0.488–0.512, 9 m period, about 0.04–0.52 duty.
  - Darker wheel tracks at u 0.17–0.33 and 0.67–0.83.
  - Marking roughness 0.62.
- Junctions are overlapping ribbons, hidden only by polygon offset.

**Port**

- **Road beds: (a).** They are already in the exported heightfield; never re-carve.
- **Ribbons: (b), S.** Same strip from `world.json` points with the winding flip.
- **New: junction mesher, L.** This is the user's "proper street intersections" goal. For each traffic node with ≥ 3 arms (11 such nodes in canon, one of which, the Millbrook square at (−70, 30), is the roundabout):
  1. Trim each arm ribbon back to the node radius `R` (exported).
  2. Build the junction surface as the polygon union of:
     - every connector polyline (`traffic.json` `kind:"conn"` at that node) buffered by 2.2 m (lane half-width 1.8 plus margin), and
     - the arm stubs.
  3. Fillet the corners (6–9 m kerb radius), triangulate, drape on `WorldGrid.HeightAt + 0.045`.
  4. Markings come from the same data:
     - stop or give-way lines on the minor arms (`seg.minor`, `node.major`),
     - zebra crossings in Millbrook (inside `TOWN_R` 105),
     - roundabout ring markings from `rb.C`, `rb.R`.
- Libraries:
  - Clipper2 C# for offset and union (Boost Software License 1.0).
  - LibTessDotNet for triangulation (SGI Free Software License B).
  - Alternatively `com.unity.splines` 2.x for ribbon extrusion only (it has no intersections).
  - Commercial EasyRoads3D Pro does crossings, but would fight the frozen centrelines and heights. Prefer the custom mesher.
- **Ground under fillets** is not guaranteed flat: the carve is flat only within hw + 0.9 of each centreline. If draping looks wrong, apply a deliberate, versioned "junction plates" terrain patch (canon v1.1) in the Unity editor.

### 4.6 Town planner, canon file, freezer, profile (`townGen.js`, `canon.json`, `townFreeze.js`, `townProfile.js`, `tools/osm`)

**What it does (planner, for reference)**

- **Main street:** the straightest pair of paved arms within 15 m of the town centre.
- **Grid:**
  - Block length `L = clamp(sampleQ(blockLength, 0.45), 85, 115)`; canon value 100.24.
  - Lot depth `clamp(setback.res p50 + 13 + 10, 28, 40)`; block depth `S = 2·lotDepth + 2·3.1`; canon value 74.8.
  - Cross streets at u = ±52, ±(52+L)… within radius 165; parallel streets every S.
  - Streets end at through roads (T-junctions, dropped if sin < 0.7).
  - Edges are rejected if short (< 25), steep (> 0.13), wet, on a site, within 11 m of another road, bumpy (> 4.5 m off grade) or crossing a road.
  - Dead ends are peeled off.
- **`makeStreet`:** 2 m points, ground-following with ±5-point smoothing, grade ≤ max(0.09, 1.05·Δy/L), level 7–15 m junction plates.
- **Lots:** along the main arms (com < 95 m from the centre), then the streets (corner 9 m), then the other arms.
  - Frontage from `TP.lotFrontage`; com lots `footprint.com.w + 2`.
  - SAT overlap test with 1 m shrink.
  - Industry: 2 lots 34×46 out at R−10…R+80.
  - Hamlets: 2, at least 140 m apart, on through roads ≥ 200 m long, 70 m from the ends and beyond town radius + 70. Store 26×36, workshop 22×36, houses either side.
- **`gradeLot`:** pad target `max(1, 0.6·mean + 0.4·roadY)`. Rejected if relief > 8 (res), 10 (store/shed) or 7 m. 5 m smooth blend. Skips road and kerb vertices.
- **Lot builder** (`props.js buildLots`):
  - res: house set back `max(5.5, min(depth−d−7, setback.res sample))`, driveway on one side, 35 % garage / 25 % shed, 35 % fence, mailbox, 55 % yard tree.
  - com: shop 1.4–3 m back, sidewalk.
  - ind/shed: warehouse plus a front yard.
  - store: shop behind a car park.

**canon.json v1 format (verbatim carry-over, (a), S)**

```
{ version:1, frozen:"2026-10-18", rngCalls:1220,
  town:{radius,blockLen,blockDepth},
  streets:[{hw:3.1, kind:"cross"|"parallel", pts:[[x,y,z] mm-rounded]}],       // 15
  hamlets:[{name,x,z}],                                                           // 2
  lots:[{x,z,nx,nz,tx,tz,w,depth,zone:"res|com|ind|store|shed",roadY,contiguous?,padY,hamlet?}], // 59
  ops:[ ["claim",x,z,rot,hw,hd] | ["model",name,x,z,rot,w,d,{Mat:hex},"litBits"] |
        ["concrete",x,z,rot,hw,hd] | ["mailbox",x,z,rot,hex] | ["tree",x,z] |
        ["fence",x0,z0,x1,z1,hex] | ["box",x,z,hw,hd,rot,y0,y1] | ["parking",cx,cz,rot,w,d,withLamp] ] }  // 265
```

Op `rot` is the three.js yaw (radians). `model` ops do **not** store y (it is recomputed from the ground), which is why §3 exports resolved `y`. Unity should treat `canon.json` as provenance plus lot semantics (zone, hamlet). It should build from `placements.json`, not by replaying ops.

**Classification**

- `townGen.js` planner: **(d)** in Unity. It stays in the JS repo as the tool that made canon. Porting it to a C# editor tool is only worth doing (**M**) if new towns are wanted, and then the output must also go through the export/patch route.
- `townFreeze.js`: **(d)**.
- `townProfile.js`: **(a)** → `source/townProfile.json` (needed only by a future planner).
- `tools/osm/fetch.mjs`, `measure.mjs`: **(d)**. They stay Node tools; the data is ODbL, so keep the attribution "Map data © OpenStreetMap contributors, ODbL" in the credits.

### 4.7 Props, buildings, site dressing, lamps, parking, collider grid (`props.js` lines 1–1038)

**What it does**

- **Collider grid:** 16 m cells (64×64). Records: `circle {x,z,r,y0,y1,kind}` and `box {x,z,hw,hd,rot,cos,sin,y0,y1,kind}`. Kinds: tree, rock, lamp, post, pump, boat, building, fence, logs, bale, dock. `queryColliders` dedupes with a stamp.
- **Geometry buckets:** everything static is merged into 7 meshes by material:
  - building: vertex colours, roughness 0.82
  - metal: 0.45 / metalness 0.65
  - windowLit: emissive `0xffbf6b`, intensity 0.05 + 1.5·night
  - windowDark
  - bulb: emissive `0xffd49a`, 0.3 + 2.4·night
  - neon: `0x39d5ff`, 0.4 + 5·night
  - signRed: `0xff3b2f`, 0.3 + 3.5·night
  - Light-pool decals: instanced 15 m quads at the lamp ground, additive `0xffc27a`, opacity 0.2·night.
- **GLB buildings** (`buildings.glb`, one empty per model):
  - `MODEL_CORE` [w, d, roofH]:
    - House_A 7.6×8.4×6.1, House_B 7.8×9.0×9.6, House_C 10.4×8.0×5.3
    - Shop_A 11×9×5.9, Shop_B 10×9×8.1
    - Warehouse_A 22×30×9.8, Warehouse_B 12×18×6.2, Warehouse_C 26×10×6.5
    - Barn 12×18×9.9, Garage 4.2×6.4×4.2, Shed 2.6×3.2×3.0
  - Scaled to the plot within ±25 %.
  - Recolourable materials: Wall, Roof, Door, Awning, Accent, Cladding (per building). Fixed: Trim, Frame, Stone, Wood, Concrete, Dark, Metal, Sign, Bulb.
  - Each window pane is a separate `Glass` object so it can be lit or dark individually.
- **Sites:** `tryPlace` searches a ring of angles/radii with `rectClear` (5×5 sample grid: on the island, `roadDist ≥ margin`, above water + 0.6, clear of footprints and rings).
  - depot: 2× Warehouse_A, house, Warehouse_C, containers
  - farm: barn, house, 2 silos, Warehouse_B, hay
  - quarry: crusher, Warehouse_B
  - mill: saw shed, house, Warehouse_B, logs
  - harbor: dock with 2 piers, crane, Warehouse_B, shop
  - fuel: canopy with 4 pumps, shop, sign
- **Lamps:** 6.5 m, every ≥ 30 m on paved roads near the town (within radius + 10) or hamlets (60 m) and near sites, alternating sides at hw + 1.1. Plus 3 per site ring at 13 m and car-park lamps.
- **Parking:** the Millbrook garage lot (7×15 m, 5 bays at 5.6 m, facing the road) supplies the player spawn and tow bays.

**Port**

- Placements **(a)** via `placements.json`.
- Buildings **(c)**: glTFast prefab per model. Per-instance colours via `MaterialPropertyBlock` (SRP-Batcher-friendly alternative: per-material colour arrays indexed by an instance id in Shader Graph). Per-pane lit flags pick a lit or dark Glass material, using the exported pane names.
- Batching: Unity 6 **GPU Resident Drawer** (URP Forward+ / HDRP) for the static instances, or static batching.
- Night emissive curves **(b)**.
- Collider grid **(b)**, S: keep it for the custom vehicle statics and traffic clearance, and/or generate PhysX Box/Capsule colliders from the same records.
- Typed primitives **(b)**, M: port the small builders, or replace them with prefabs, Synty included.
- Phase-0 shortcut: import `reference/static_props.glb` for instant visual parity (split it into 64 m chunks for culling; the bld bucket alone is 261k vertices).
- Effort M overall.

### 4.8 Trees (`props.js` lines 1040–1424, `trees.glb`, `tools/blender/trees.py`)

**What it does**

- Nine species (name: height at scale 1 / trunk radius / knock strength / scale range / sway):

  | Species | h (m) | trunk (m) | strength | scale | sway |
  |---|---|---|---|---|---|
  | Pine | 12 | 0.30 | 1.0 | 0.7–1.15 | 0.7 |
  | Spruce | 9.5 | 0.26 | 1.0 | 0.7–1.2 | 0.6 |
  | Oak | 9 | 0.45 | 2.6 | 0.75–1.15 | 0.8 |
  | Birch | 10 | 0.18 | 0.6 | 0.7–1.1 | 1.3 |
  | Poplar | 13 | 0.26 | 1.2 | 0.75–1.1 | 0.9 |
  | Maple | 7.5 | 0.34 | 1.4 | 0.75–1.15 | 0.9 |
  | Bush | 1.4 | 0 | 0 | 0.7–1.4 | 0.25 |
  | Snag | 6.4 | 0.28 | 0.5 | 0.8–1.3 | 0 |
  | Reeds | 1.9 | 0 | 0 | 0.75–1.25 | 0 |

- Placement (exported; reference only):
  - 7 m jittered grid; `forestDensity` from layered sines → 0.85/0.35/0.035.
  - Fringe bushes; surface, road-distance, slope (n.y ≥ 0.72), POI, field and footprint exclusions.
  - `pickSpecies`: conifers above 38/55 m, birch groves, an autumn corner.
  - 22 bog snags/birch, reeds round the bog and the lake shore, yard trees, poplar windbreaks.
- Rendering:
  - Per species, a near and a far InstancedMesh. Re-split when the camera moves more than 6 m; near radius 90/140/190 by quality. Only the near mesh casts shadows.
  - Mesh y = ground − 0.2.
  - Instance tint `(t, t·(0.96 + 0.08·hue), t·(0.86 + 0.1·hue))`.
- Wind (vertex):

  ```
  hb = max(y − 1.5, 0)
  gust = 0.6 + 0.4·sin(0.37t + 0.013x + 0.011z)
  bend = hb²·0.0016·sway·gust
  dx = sin(ph)·bend + 0.25·sin(2.7ph + y)·bend    with ph = 1.25t + 0.17x + 0.13z
  dz = 0.6·cos(0.83ph)·bend
  ```

- Knock-over: in vehicle statics, when `vn > 4` and `vn·mass > 9000·s²·strength`. The tree falls with `fall += dt·(0.6 + 2.5·fall)`, angle `fall²·1.45` about the impact yaw, and stays down; its collider radius drops to 0. Terrain changes re-snap tree y.

**Port**

- Instances **(a)** (`vegetation.json`).
- Rendering **(c)**: GameObjects with LODGroup from the glTFast prefabs (`Pine` / `Pine_LOD1` …), driven by GPU Resident Drawer. Fall back to `Graphics.RenderMeshIndirect` with compute culling if profiling demands it.
- Wind shader **(b)**, S: Shader Graph vertex offset using the object origin for the per-instance phase.
- Knock-over **(b)**, S: swap to a falling animation or a Rigidbody.
- Effort M.
- Mesh budgets **[measured]** (triangles, LOD0/LOD1): Oak 1628/224, Birch 1424/200, Maple 1348/212, Poplar 1148/160, Bush 680/80, Spruce 368/156, Pine 290/162, Reeds 176/74, Snag 92/54. All are vertex-coloured (`COLOR_0`), one material `Tree`.

### 4.9 Rocks, boats, water (`props.js`, `water.js`)

- **Rocks**
  - Up to 340 accepted from 6,000 random tries (ROCK surface 50 %, grass/dirt 5 %), plus 18 quarry rim boulders.
  - Mesh: `DodecahedronGeometry(1, 1)`, each vertex scaled by `0.82 + frac(sin(41.3x + 17.1y + 29.7z)·9871.13)·0.32`, then (×1.15, ×0.7, ×1); flat-shaded `0x8a847b`.
  - Placement: y offset −0.25·s; colliders where s > 0.7 (r = 0.95·s).
  - **(a)** instances; **(b)** the mesh (or a Blender rock). S.
- **Boats:** 2 at the dock. Bob `y = −0.3 + 0.12·sin(0.9t + ph)`, roll `0.04·sin(0.7t)`, pitch `0.03·sin(0.55t + 2ph)`; random phase. **(b)**, S.
- **Water**
  - A single plane 1424 m square at y 0, colour `0x103f52`, roughness 0.06, opacity 0.86, no depth write.
  - Normal map 256² tiled 90×, strength 0.32, scrolling (0.0045, 0.0031)/s.
  - The terrain hides it except over the lake and ponds.
  - **(c)**: HDRP Water System (lake surface type) or a URP water shader with depth fade. The underwater ground tint already lives in the terrain colours. Swimming and buoyancy logic stays custom (y = 0). S.

### 4.10 Grass (`grass.js`)

**What it does**

- Tiles: 16 m, built at most 3 per frame nearest first, within radius 55/75 m (Low disables), density 4.5/6 tufts per m². Each tile is seeded `mulberry32(tileKey·7919 + 13)`, so tiles are deterministic.
- A tuft is accepted only on:
  - GRASS surface, MUD ≤ 8, WEAR ≤ 40, |BASE − H| ≤ 0.06, h > 0.15, slope ≤ 0.75;
  - after edge and slope thinning `rng < 0.11·edges + 0.6·slope`;
  - outside building boxes (+0.2) and non-tree circles.
- Tuft geometry (`mulberry32(42)`): 6 blades × 3 segments, h 0.32–0.54, w 0.045–0.065, lean.
- Shader:
  - Scale fade `1 − smoothstep(0.65R, R)`; tall/short patches from the detail map.
  - Two-frequency wind sway, stronger at the tips (∝ y²).
  - Normals forced up, so tufts are lit like the ground; colour from `terrain.colorAt` plus the ground-shader tones; dark roots.
  - Fake lamp pools from the 12 nearest lamps (radius 7.5, colour (1, 0.54, 0.2)).
- Tiles rebuild when the ground under them changes.

**Port: (b), M.** C# tile builder (Burst), `Graphics.RenderMeshInstanced`/`RenderMeshIndirect` with a per-tile instance buffer (position xyz + rotation, colour rgb + scale), Shader Graph. Unity Terrain detail layers are a (c) alternative only if Unity Terrain is chosen for rendering; their density maps would then need re-baking on deformation.

### 4.11 Sky, day/night, sun/moon, IBL, fog (`sky.js`)

**What it does (the clock and curves are the part to keep)**

- Clock:
  - Start time 16.2 h.
  - Rate 1/80 game-hours per real second by day; 1/36 when night factor > 0.6.
  - `[` and `]` skip ±1 h.
- Sun:
  - Sunrise 5.5 h, sunset 20 h. Day maps to angle 0…π, night to π…2π.
  - `sunDir = (cos t, sin t·sin 0.98, sin t·cos 0.98)`.
  - `nightFactor = 1 − smooth(−7, 3, elevation)`.
- Preetham Sky (three `Sky`):
  - mie 0.005 / g 0.82; turbidity 2.2 + 6·low; rayleigh 1.5 + 1.8·low + 0.5·night.
  - Gain 0.5 + 0.2·low + 0.3·night.
  - Horizon glow (1, 0.36–0.52, 0.14–0.24) peaking at sunrise and sunset.
  - Night colour blend.
- One shadow-casting directional light:
  - Sun when elevation > −2.5°: warm curve, intensity `3.4·smooth(−2.5, 7, elev)`.
  - Otherwise moon: (0.55, 0.66, 1), `0.7·smooth(−2.5, −9, elev)`, direction offset +0.25 x.
  - Light y clamped ≥ 0.06 (sun) / 0.15 (moon).
  - Shadow box ±55/75/95 m, texel-snapped to the focus point; map 1024/2048/4096; bias −0.0004, normalBias 0.04.
- Hemisphere ambient 0.22 + 0.1·night.
- `FogExp2`: colour from the elevation table (−14°: `0x070c1a` … 45°: `0xadc2d6`), density 0.00105 + 0.0006·night + 0.0002·low.
- 2,600 stars (fade with night), moon sphere 4 km away.
- PMREM environment regenerated from the sky every 1.2 s when the time changed by more than 0.05 h.
- Exposure 0.95 + 0.45·night.

**Port: clock and curves (b), S; rendering (c), M.**
- HDRP:
  - Physically Based Sky + Volumetric Clouds.
  - Exponential/volumetric fog driven by the same elevation table.
  - Directional light with a moon swap (or HDRP's moon via a second directional light), Cascaded Shadow Maps.
  - Real-time reflection probe or `HDRenderPipeline` sky ambient (automatic in HDRP).
- URP:
  - Procedural skybox or a time-of-day asset (examples, licences unchecked: Enviro 3, COZY: Stylized Weather 3, Azure[Sky] Dynamic Skybox).
  - Reflection probe refresh via script (`RenderProbe()` time-sliced), Main Light shadows with cascades.
- Shadow texel snapping is built into Unity's CSM; drop that code.

### 4.12 Post-processing and quality presets (`main.js`)

**What exists**

- `EffectComposer` with 4× MSAA HalfFloat target.
- `GTAOPass`: radius 1.6, distanceExponent 1.4, thickness 2, scale 1.25, falloff 0.6, 16 samples; denoise lumaPhi 10, depthPhi 2, normalPhi 3, radius 6; blend 0.85. Excluded from AO: additive or transparent (opacity < 0.5) materials, points, lines and `userData.noAO` objects (grass).
- `UnrealBloomPass`: half resolution, strength 0.16 + 0.3·night, radius 0.55, threshold 0.85.
- `OutputPass`: ACES Filmic, sRGB.
- `GradeShader`:
  - Desaturate yellow-greens (saturation 1.08 → 0.86 by greenness) and nudge them (0.97, 1.0, 0.93).
  - Split tone: shadows (−0.004, 0, +0.018), highlights (+0.018, +0.008, −0.012) over luma 0.15–0.75.
  - Contrast ×1.05 about 0.5; lift ×0.985 + 0.012.
  - Vignette `smoothstep(0.9, 0.3, |uv−0.5|·(1, 0.85))` to 0.8.
- F2 quality presets:
  - Pixel ratio 0.75/1/min(dpr, 1.5).
  - Shadow 1024/2048/4096, range 55/75/95.
  - Bloom and AO off on Low; grass off on Low (radius 55/75).
  - Tree near radius 90/140/190.
  - Terrain lodScale 0.7/1/1.3, builds 1/2/3.

**Port: (c), S.**
- Volume profile:
  - AO: URP SSAO renderer feature / HDRP Ambient Occlusion.
  - Bloom (threshold ~0.85 to start), Tonemapping ACES, Exposure.
  - Split Toning; Color Adjustments plus Hue-vs-Sat / Channel Mixer for the green taming; Vignette.
- Quality: Unity Quality Settings levels plus a `GraphicsPreset` ScriptableObject carrying the gameplay-side values (grass radius and density, tree LOD bias, terrain LOD scale).
- Re-tune the look by eye in Unity against `reference/shots`.

### 4.13 Particles and wheel effects (`particles.js`, `main.js effects()`)

**What it does**

- One pool of 3,000 point sprites with a radial texture.
- Each particle has drag and gravity; alpha `a0·min(1, 8t)·(1 − t)`; size grows.
- Types, as (size, grow, life, alpha, drag, gravity):

  | Type | size | grow | life (s) | alpha | drag | gravity |
  |---|---|---|---|---|---|---|
  | dust | 0.7 | 2.6·amount | 1.3–2.4 | 0.3·amount | 1.6 | −0.25 |
  | clod | 0.12–0.24 | 0 | 0.7–1.1 | 1 | 0.3 | 14 |
  | smoke | 0.35 | 1.8 | 1.4–2.2 | 0.22·amount | 1.2 | −0.6 (dark 0.12 or light 0.62 grey) |
  | mud | 0.14–0.34 | 0.15 | 0.7–1.2 | 1 | 0.25 | 13 |
  | splash | 0.25 | 0.9 | 0.8 | 0.7 | 0.6 | 9 |

- Particle light: sun colour × min(1, I/3.2)·0.8 + 0.12 + 0.2·day.
- Emission rules per wheel per frame (within 150 m, or 60 m for AI):
  - In water: splashes.
  - On mud: muck flung backwards at rate `spin·26 + 1.6·max(0, v − 1.5)`.
  - Unpaved, non-rock: dust `(0.35·max(0, v−3) + 6·spin + 4·skid)`, plus clods when spin > 0.25.
  - Paved: tyre smoke when skid > 0.3.
  - Dust colour = `DUST[surface]` darkened by wetness (`WEAR/255`).
- Exhaust puffs (diesel dark under load). Shovel clods and dust.

**Port: (c) VFX Graph** (GPU events per type) or Shuriken with `ParticleSystem.Emit(EmitParams)`. Emission rules **(b)**. S.

### 4.14 Vehicle physics core (`vehicle.js`) — the heart of the port

**Model and data**

- Fixed step `STEP = 1/120`; at most 8 substeps per frame (the accumulator resets if exceeded). Render interpolation: lerp/slerp of the previous and current pose.
- Body: box inertia `I = m·inertia·(h²+l², w²+l², w²+h²)/12`. Payload adds mass (`setPayload`).
- Hull: 14 points (8 box corners at 96 %, front/rear bottom, both side bottoms, bottom and top centre).
- `DEFS` per vehicle (pickup, hatch, rally, truck, dozer):
  - mass, inertia factor, box {w,h,l,y,z}, com {y,z}
  - wheel {r, w, x, front, rear, dual?, rows?}
  - susp {rest, k, c}
  - drive fwd/rwd/awd/tracked, steer, steerRate
  - engine {torque, idle, redline, vmax, gears, first}
  - brake, handbrake, grip, drag, roll, antiRoll, rollInfluence
  - cap, camDist/camHeight, fuelUse, sound {cyl, pitch, rough}, mudGrip, awdOnLow, seat, blade, simple
  - → **(a)** as `VehicleDefinition` ScriptableObjects (this is also where "varying trucks and race cars" get added).
- Derived overall ratios **[measured]**: top ratio `ω_red·r/vmax`, first ratio = top/`first`, geometric spacing in between; reverse = 0.95 × first.
  - hatch 19.94/14.37/10.36/7.47/5.38, rev 18.94
  - rally 20.2/14.29/10.1/7.14/5.05
  - pickup 27.65/19.35/13.55/9.48/6.64, rev 26.27
  - truck 30.8/21.61/15.16/10.64/7.46/5.24
  - Top speeds 148/187/137/97 km/h. Low range divides speed by 2.6: pickup 12.6 km/h in 1st, truck 6.4 km/h.

**Step order:** gravity, water, suspension, tyres, (track steer), hull contacts, statics, winch, then aero drag `v *= max(0, 1 − drag·|v|·dt/m)` (quadratic), angular damping 0.25/s, |ω| ≤ 14 rad/s, integrate, clamp to the island ±506 m, deform at 60 Hz, dirt, stuck timer, fuel, sleep.

**Suspension**
- 3 downward rays per wheel at ±0.55r along forward (round-tyre approximation); fast fixed-point solve when upright, ray march otherwise.
- Spring `F = k·comp + c·compVel`, clamped to [0, 5mg], scaled by `clamp(n·up, 0.3, 1)`.
- Bump-stop impulse at full compression (records impacts).
- Anti-roll `(compL − compR)·antiRoll·dt` per axle pair.

**Tyres**
- Grip budget `μ·load·dt` with `μ = grip·GRIP[surf]·(1 − 0.18·wear·soft)·(1 − 0.5·inWater)`; mud multiplies by `mudGrip` (hatch 0.75 … dozer 1.6).
- Mud resistance: `mudRes = load·(0.04 + 0.12·mudDepth + 0.3s² + 0.12s + 0.1·mudDepth·min(|v|, 6))`, where s = sink/r (×0.35 tracked).
- Drive impulse beyond grip becomes wheelspin (`spin ≤ 3`, impulse capped at 0.92 of grip). **Low range** caps it at 0.97 of grip with no spin: it claws instead of digging in.
- Brake limit `brake·pedal + roll·load + engineBrake`.
- Handbrake: long 0.9 / lat 0.42 of grip.
- Lateral limit: the friction-circle remainder, minimum √0.12 of grip; ×0.84 while sliding.
- Solver: 4 PGS iterations over longitudinal (`fd`) and lateral (`sd`) impulses. The lateral impulse is applied at the roll-influence-adjusted arm `rS` (`arm − up·(arm·up)·(1 − rollInfluence)`).
- Wheel ω relaxes towards `vl/r + 18·spin` (20/s), clamped to the rev limit.

**Engine and gearbox**
- Torque curve: 0.62 below 12 % rpm, rising to 1.0 at 62 %, falling to 0.68 at 100 %.
- Clutch rpm in 1st/reverse: `idle + thr·(0.55·redline − idle)`; rpm smoothing 14/s.
- Rev limiter on *ground* speed. Shifts on ground speed (not spinning wheels): up at 0.9·redline with throttle > 0.1 (0.25 s); down when the lower gear < 0.8/0.62·redline and current < 0.5·redline (0.18 s); torque ×0.1 while shifting.
- Low range ×2.6 on every ratio, top gear limited to 2. Truck `awdOnLow` drives all wheels in low.
- Driveline efficiency 0.86. Engine braking `0.22·T·ratio·(rpm/redline)/r` when throttle < 0.05.
- Engine cut when submerged > 0.65.
- Driver aids: reverse after holding brake 0.22 s below 0.7 m/s; forward after 0.15 s; auto-hold brake 0.7 when |throttle| < 0.05 and |v| < 0.5.
- Steering: rate-limited (`steerRate`, ×1.7 returning), angle `steer/(1 + 0.055|v|)`.

**Hull contacts**
- Terrain penetration push 35 % of min(depth, 0.4); 3 PGS iterations; restitution only when v₀ < −2 (−0.15v₀).
- Scraping friction 0.55 (0.35 in mud).
- Hull points in mud carve ruts every 4th tick (`rut(r 0.7, depth 0.9·mud)`).

**Statics**
- Body footprint as a 2D OBB (at COM-relative box) against circle and box colliders (SAT).
- Restitution 0.15 trees / 0.25 circles / 0.2 boxes; wall friction 0.3; positional correction 80 % of min(pen, 0.5).
- Vertical overlap check `y0..y1`. Tree knock-over as in §4.8.

**Water**
- Buoyancy 0.95·g·submerged fraction, linear damping 1.6·sub, angular 2.0·sub.
- Washes off dirt at 0.25·inWater per second.

**Ruts** (`deform`, every 2nd step)
- On mud: `depth = mud·0.55·clamp(0.5 + load/30000, 0.55, 1)` (×0.4 tracked); `rate = 0.006 + 0.35|v|dt + 0.05·spin (+0.01 sliding)`; wear 12 + 20·spin; berm 0.6 (0.2 tracked); directional spoil.
- Elsewhere: `depth = clamp(0.03 + load/36000, 0, 0.34)` (tracked 0.07); `rate = 0.002 + 0.11|v|dt + 0.014·spin`.
- Radius `max(0.42, 0.5w + 0.24)`.

**Other**
- Mud on the body (`dirt` 0..1) accumulates from churn on mud.
- `stuckT`: throttle > 0.5 in mud below 0.6 m/s; the HUD hints after 2.5 s.
- Fuel `fuelUse·(0.08 + |thr|)·dt`.
- Sleep after 1.2 s still with all wheels in contact.
- Vehicle–vehicle: 2D OBB SAT, e = 0.25, inverse-mass positional split.

**Port: (b), L.**
1. **Phase 1: verbatim port** (own integrator, no PhysX for vehicles) as plain C# with Burst-friendly math. `FixedUpdate` at `Time.fixedDeltaTime = 1/120`, transform interpolation in `Update`. Parity-test against the golden traces (§3.6).
2. **Phase 2 (optional): move body integration and body collisions to a PhysX `Rigidbody`** (convex-hull collider; Unity 6 API `linearVelocity`/`angularVelocity`, `linearDamping`) for true 3D collisions with rocks, ramps and buildings. Keep the wheel rays against `WorldGrid` and keep the tyre PGS. Run it on a *local copy* of linear/angular velocity inside `FixedUpdate`, then write the result back (PhysX applies `AddForce` lazily, which would break the iterative solver).
3. **engine-sim seam:**
   - `engineForce(dt)` and the rpm logic are replaced by an engine-sim crank coupled through clutch, gearbox and differential, using the ratio tables above as the starting drivetrain.
   - `torqueCurve`/`torque`/`idle`/`redline` become engine-sim engine scripts.
   - Wheel load torque feeds back as the crank load. See the engine-sim research report for the coupling details.
- Do **not** use `WheelCollider`. It cannot represent sinkage, rut-dependent grip or mud resistance, and it would discard the tuned feel.

### 4.15 Dozer blade (`vehicle.js bladeWork`, `trackSteer`)

**What it does**
- Blade height `h` ∈ [−0.4, 1.3], Q/Z at 0.7 m/s; > 0.55 means raised (no work).
- Cuts only going forward. Max native shave per step `0.006 + 0.05·max(0, v)·dt`.
- **Grade-following:**
  - Least-squares line through the rear-half wheel contacts (slope clamped ±0.6), extended to the blade.
  - `yCut = intercept + 0.5·slope·tb + h`.
  - When digging (h < 0), at most 0.08 m below the ground 0.45 m ahead.
- Per step: `cutBlade` (half-width 1.75, depth 0.4), `depositCapped` (centre 0.8 m ahead, cone top yCut + 1.6, slope 0.75), `relax` (2.2 m ahead, r 3.2, 2 iterations, 0.42, frozen behind the blade plane).
- Resistance impulse `min(6500·vol, 0.5·m·v)`. Blade load is smoothed for the HUD.
- Tracks:
  - Skid steer to target yaw rate `−0.85·steer` (reversed when backing), torque gain 0.35; the yaw-rate change is limited to ±18·dt rad/s per step.
  - Simple drive force 72 kN × (1 − v/3.8) with no gearbox; rpm 700 + 1400|t|.
  - Tread texture scroll from track speeds `v ∓ 1.2·yawRate`.

**Port: (b), M.** Port verbatim. The tread scroll becomes a material UV offset.

### 4.16 Winch (`main.js findAnchor`/`toggleWinch`, `vehicle.js winchStep`)

**What it does**
- Anchors: trees (standing only), rocks, lamps, posts within 30 m of the hook, or another player vehicle.
  - Score `d − 6·ahead` (prefers in front); minimum 1 m.
  - The hook goes on the near side of the trunk at height min(y1, y0 + 1.6).
- Hook point: `(0, cy − 0.45·hh, cz + hl + 0.05)` local.
- Rope (pull-only):
  - Slack is taken in automatically; holding W reels in at 0.9 m/s; minimum length 2.5.
  - Baumgarte target `min(1.2, 3·(dist − len))`.
  - Max pull `1.3·m·g`.
  - Impulse on both bodies for vehicle anchors; tension shown in kN.
- Cable mesh: a cylinder r 0.018 stretched hook-to-anchor.

**Port: (b), S.** The cable can become a `LineRenderer` or a stretched mesh.

### 4.17 Vehicle models (`vehicleModels.js`, `assets/vehicles/*.glb`, `tools/blender/vehicles.py`)

**Assets [measured]**

| Vehicle | Body / Body_LOD1 (tris) | Wheel / Wheel_LOD1 (tris) |
|---|---|---|
| hatch | 20,638 / 2,881 | 3,924 / 252 |
| rally | 20,377 / 2,848 | 3,524 / 252 |
| pickup | 22,274 / 3,110 | 3,632 / 252 |
| truck | 14,638 / 2,040 | 3,580 / 252 |
| dozer | 9,916 / 1,359 | — (TrackL/TrackR 768 each, Blade 1,708 / 512) |

- Each file also has a Caliper object.
- No textures, except the dozer `Tread` UVs. Materials are named: Paint, Glass, Headlight, Taillight, Reverse, Indicator, Trim, Chrome, ….
- Model space: ground y = 0, +Z front, +X left. The body group is offset by −com.

**Runtime behaviour**
- LOD switch at 40 m.
- Paint: MeshPhysical with clearcoat 1, roughness 0.32, metalness 0.35.
- Glass: opacity 0.36 (truck 0.88).
- Light emissives: head 2.6/0.12; tail brake 3.5, on 1.0, off 0.2; reverse 2.5; amber 2/0.3.
- Truck defaults to a green paint.
- Pickup bed shows up to 8 cargo crates.
- **Dirt shader** (`dirtify`):
  - Mud climbs from the sills: `reach = 1 − smoothstep(0.25 + 0.9d, 0.55 + 1.5d, y_object)` (wheels get it everywhere).
  - Noise 128² (mulberry32(31)); colour (0.085, 0.062, 0.042); roughness → 0.88; metalness and clearcoat off.
  - Only the `DIRTABLE` materials get it.
- Headlights: two spots, intensity 420, range 90 m, angle 0.48 rad, penumbra 0.55, decay 1.6, aimed 25 m ahead and 2.5 m down.
- Exhaust points per vehicle (`META`).
- Traffic merges meshes per material (90 → 20 draw calls).

**Port**
- GLBs **(a)** via glTFast (or re-export FBX from the same scripts).
- Materials **(c)**: remap by name at import (glTFast custom material generator or an `AssetPostprocessor`). Paint → HDRP Lit with coat mask, or URP Complex Lit with clear coat.
- Dirt **(b)**: Shader Graph sub-graph, a `_Dirt` property per vehicle via MaterialPropertyBlock.
- LOD: `LODGroup`.
- Lights: Unity spot lights; HDRP light layers keep headlights off the body.
- Effort M.
- **Wheel mirroring:** JS sets `scale.x = side` for right-side wheels. In Unity a negative scale works but flips winding (Unity handles it and inverts the culling). Keep it.

### 4.18 Character on foot and shovel (`character.js`, `main.js footControls`)

**What it does**
- Movement relative to the camera: walk 4.0 m/s, run 7.2 m/s (Shift / gamepad B).
- Acceleration 18/s grounded or swimming, 4/s airborne; gravity 22; jump 7.2 m/s.
- Swimming when ground < −1.3 and body below −1.0: speed ×0.45, float towards y −1.15.
- Slope slide on n.y < 0.64 (push 14·n).
- Capsule radius 0.35, height 1.8. Pushes out of circle and box colliders and out of vehicle OBBs; vehicles faster than 4 m/s throw you.
- Get in when within 2.2 m of a vehicle's box (E / gamepad Y). Get out only below 3 m/s; driver side, then passenger side, then behind; never into deep water.
- Shovel: aim ray through the crosshair (needs pointer lock), reach ≤ 6.5 m, every 0.13 s `brush(r 1.15, ±0.09)`. Locked on ROAD/CONCRETE (red marker). Wakes vehicles within 7 m. Clods and dust.
- Rig: procedural box-man with leg/arm swing, `phase += 2.4·speed·dt`.

**Port**
- Controller **(b)**, S: keep the custom ground-follow on `WorldGrid` (Unity `CharacterController` needs a ground collider; see §4.3).
- Model and animation **(c)**, M: rigged humanoid (a Synty character, or Mixamo/Unity Starter Assets clips) with an Animator blend tree; shovel dig as an animation event.

### 4.19 Camera (`camera.js`)

**What it does**
- Mouse sensitivity 0.0024 rad/px; gamepad look 2.6/2.0 rad/s; wheel zoom 0.55–1.8.
- Chase:
  - Yaw follows the heading at rate 4/s (1.5/s when slow); looks backward when reversing faster than 4 m/s.
  - After 1.5 s without input above 2 m/s the orbit recentres with `exp(−2.2dt)`.
  - Pitch 0.2 + orbit, clamp −0.25…1.25. Distance `camDist·zoom·(1 + 0.006·min(v, 40))`.
  - Target height lerp 9/s; `0.42·camHeight` + offset `0.35·camHeight`.
  - Terrain ray pull-in (≥ 1.5 m), stays above ground + 0.6 and water + 0.3.
- Hood cam from `seat`.
- Foot cam: over the right shoulder 0.55 m, 4.2 m behind, pitch −0.6…1.2, aims through the crosshair.
- FOV 60 → up to 76 with speed above 8 m/s.
- Impact shake `0.35·s²` decaying 2.5/s.

**Port: (b), S.** Custom rig (port), or Cinemachine 3 with a custom extension for the heightfield occlusion ray. A straight port is simpler and faithful.

### 4.20 Traffic network (`trafficNet.js`)

**What it does**
- Builds from the paved roads only.
- Roads are split where side roads join (more than 8 m from ends). Nodes cluster within 6 m.
- Edges with a step grade > 0.4 are skipped.
- Junction radius R: the smallest radius (9–24 m, default 12.5) at which every pair of arms is `2·LANE + 7` apart.
- Major road: the straightest arm pair with deviation < 0.6 rad.
- Delivery-ring junctions get a roundabout (R from 11.5/10.5/12.5, centre offsets ±1.5/±3, clearance ≥ 1.9).
- Lanes: `LANE = 1.8` m right of the centreline, trimmed to R, resampled 1.5 m.
- Connectors: cubic Bézier with arc-approximation handles (minimum 0.3·chord), no U-turns. `compat` = movements whose paths stay more than 3.2 m apart.
- Dead-end balloon loops: search over R ∈ {12, 10.5, 13.5, 9.5, 15, 16.5, 18}, offsets c and b, attachment distance D ∈ 18…54. Constraints: k ≤ 0.105, ground steps ≤ 0.2 m, clearance ≥ 2.7 (truck) or 2.1 (car-only, `noTruck`).
- Speed cap per point: min(16 open / 11 within 105 m of town / 8 in site yards, √(2.6/k)), with curvature smoothed over ±2 samples.

**Port: (a) import `traffic.json`, S.** Port the builder **(b)**, M, only if roads ever change. In either case, `kind/next/compat/major/minor/noTruck/yard` and the roundabout `phIn/phOut` must be preserved.

### 4.21 Traffic AI (`traffic.js`)

**What it does**
- Fleet: 12 cars (`MIX`: 6 hatch, 4 pickup, 2 truck), paint palettes per type. Cruise speeds 15/14/11.5 m/s × (0.9–1.05); acceleration 1.8/1.5/0.9 m/s².
- Comfort braking 2.4, max 7 m/s².
- Ghosts beyond 225 m only advance along their route. They materialise inside 175 m when out of view (or beyond 110 m), with room.
- Rebalance every 1 s: wanted nearby cars = clamp(lane points within 175 m × 6/110, 2, 6). Far ghosts are teleported to spots 110–170 m away, out of view.
- Route: extended up to 160 m or 10 segments ahead; random choice weighted 1.6 for straight on. Trucks avoid `noTruck`; nobody enters a closed yard.
- Steering: pure pursuit from the rear axle, look-ahead `clamp(3.2 + 0.5v, 4.5, 14)`, `steer = −δ·(1 + 0.055v)/maxSteer`.
- Speed:
  - Road and corner profile `√(v² + 2·B_COMF·d)` over the samples within `clamp(12 + 2.2v + v²/2B, 20, 85)` m.
  - Steering-limited corner speed.
  - IDM interaction term `(s*/gap)²` with `s* = S0 + v·1.3 + v·Δv/(2√(a·b))`, S0 3.2.
  - Obstacle scan: vehicles as rows of circles, swept-width margin `0.5·wb²·k` for long vehicles.
- Junctions:
  - Major-road movements request early (`max(30, v²/2B + 18)` m); minor ones stop at the line and request when within 5 m and below 2.5 m/s.
  - Granted when compatible with every holder; major road first, otherwise first come, first served; and only if the exit has room.
  - Released once the tail is 2 m past.
- Roundabout: give way if a circulating car is within 22 m upstream of the merge point.
- Exit blocked by a parked or stalled obstacle: wait, then reroute after 4 s.
- Bypass: plan a sideways offset round a parked obstacle (shoulder first, otherwise the oncoming lane when clear for 60 m). The path must avoid statics and steep ground. Reverse 2.5 s if too close.
- Lights on at night factor > 0.28–0.53 (per car).
- A knock makes the driver stop 1.5–3 s.
- Rescue:
  - Flipped > 3 s, lost (> 6 m off route) > 10 s or idle > 40 s while unseen → respawn.
  - Flipped > 10 s → reset upright.
  - Idle > 25 s with no blocker → put back on the lane.
- Detail LOD: shadows off beyond 50 m, wheels hidden beyond 90 m.

**Port: (b), L.** It drives the same `Vehicle` class through `drive()`, so it ports after §4.14. Replace `Math.random` with a seeded `Unity.Mathematics.Random`, which makes AI deterministic and network-friendly. Watch the handedness sites listed in §2. Visibility uses `GeometryUtility.TestPlanesAABB` with camera frustum planes.

### 4.22 Jobs and economy (`jobs.js`)

**Data (a)**

- `CARGO` (size units / value per unit / kg per unit / fragility):

  | Cargo | size | value | kg | fragile |
  |---|---|---|---|---|
  | parcels | 1 | 5.5 | 35 | 0.7 |
  | produce | 2 | 7 | 110 | 1.0 |
  | lumber | 3 | 8 | 280 | 0.25 |
  | stone | 4 | 11 | 650 | 0.15 |
  | fish | 2 | 9.5 | 100 | 0.8, timed |
  | goods | 3 | 7.5 | 200 | 0.5 |
  | drums | 3 | 9 | 380 | 0.4 |

- `CARRIES`:
  - hatch: parcels, fish
  - rally: parcels
  - pickup: parcels, produce, lumber, fish, goods, drums
  - truck: everything
  - dozer: nothing
- `SUPPLY` and `DEMAND` per site: verbatim tables.
- `TANK`: hatch 0.6, rally 0.7, pickup 1, truck 2, dozer 1.6.
- Ring radius 7.5. Starting money $500.

**Rules (b)**
- Each board has 3 offers. Destinations are drawn from DEMAND. `perUnit = round(value·(1 + d/160)·U(0.85, 1.15))`, d = 1.25 × straight-line distance. `maxUnits = max(2, round(U(8, 28)/size))`.
- Every 3 game hours each board refreshes with 50 % chance.
- Stop below 1.3 m/s; keys 1–3 load (1.6 s); unloading 1.6 s.
- Units = min(maxUnits, ⌊cap/size⌋). The payload changes mass.
- Damage: when the impulse Δv = j/m ≥ 1.6, condition −(Δv − 1.6)·7·fragile.
- Pay = perUnit·units·(0.35 + 0.65·condition/100). Fish ×1.3 if elapsed ≤ dist/11 + 45 s.
- Fuel zone:
  - Refuel 0.14 tank/s at `100·0.8·TANK` $/tank.
  - A free quarter tank when broke and below 25 %.
  - Jet wash removes dirt at 0.3/s.
- Tow (T): $50, to a free garage bay, fuel ≥ 0.2.

**Port: (a) + (b), S.** ScriptableObject tables plus a C# `JobSystem`. **Online:** money, offers and payouts must become server-authoritative (cheat-proofing), so design the API as requests.

### 4.23 HUD, minimap, big map, labels (`hud.js`, `index.html`, `style.css`)

**What it does**
- Top bar: money, clock with a sun/moon icon, key hints.
- Speedometer: name, LOW/AWD/WINCH tags, km/h, gear P/R/1–6/D, rpm bar (hot above 90 %), fuel bar (low below 18 %), surface name plus "sunk N cm".
- Blade state text. Job tracker (cargo, destination and distance, condition, estimated pay, fish timer). Freight board panel. Prompt and progress bar. Toasts (3.6 s). Crosshair. Pointer-lock hint. F1 help.
- POI labels projected to screen between 12 and 520 m, fading beyond 380.
- Minimap:
  - 210 px circle, rotating with the camera.
  - Zoom `1.15 − min(0.55, v/60)` px/m.
  - Pins: T/D/F/Q/M/H/⛽ (blue, green for fuel, gold diamond for the destination, clamped to the rim). Traffic dots, player arrow, north marker.
- Big map (M): 86 % of the viewport. Pins and names, hamlet names in italics, traffic and fleet dots.
- **Map image** (1024², 1 px = 1 m):
  - Surface palette, hillshade with light (−0.5, 0.75, −0.45), 10 m contours, snow above 118 m, water depth tint.
  - Fields, roads (paved: dark 9 px under light 6 px; dirt: dashed), building footprints.
- Fonts: Barlow Condensed 500/600/700, Inter 400/500/600 (both SIL OFL, bundle them).

**Port: (c) UI Toolkit (UXML/USS), M.**
- Use the exported `textures/map.png` (or port `buildMapImage` into an editor bake, S).
- Minimap: a VisualElement with the map as background, rotated and scaled through `style.rotate`/`scale`, or a RenderTexture. Pins and arrow via `generateVisualContent` + `Painter2D`.
- Labels: `Camera.WorldToScreenPoint` + `RuntimePanelUtils.CameraTransformWorldToPanel`.

### 4.24 Audio (`audio.js`, `main.js updateAudio`)

**What it does**
- Engine:
  - Firing frequency `f = rpm/60·cyl/2·pitch`.
  - Two saws (f, 1.007f) + a sub square (f/2) → tanh shaper (drive 2.2) → lowpass `220 + 1600·load + 0.18·rpm·pitch` (Q 2).
  - Diesel clatter: noise band-passed at 1800 Hz, amplitude-modulated by a square at f with depth `0.25·rough`.
  - Gain `0.05 + 0.09·load + 0.03·rpm/6000`.
- Tyre squeal: band 1500 Hz Q 6, ≤ 0.16.
- Rolling rumble: lowpass `300 + 25v`, gain `v·rough·0.008` ≤ 0.16.
- Wind: band 500, `(v/40)²·0.08`.
- Horn: 392 + 494 Hz squares, lowpass 1800.
- One-shots: thud (lowpass 180 + 600s, impact above 1.4·mass), shovel crunch, chime (659/880/1175 Hz, or 440/330 for bad).
- Master 0.55 → compressor (−14 dB, 4:1).

**Port: (c), M.**
- Engine audio comes from engine-sim (its synthesizer renders from simulated exhaust flow). Feed it through `OnAudioFilterRead` or `AudioClip.Create(..., stream: true, PCMReaderCallback)`; see the engine-sim report.
- Tyres, gravel, wind and horn: looping AudioSources with pitch and volume curves, driven by the same state values (recorded CC0 samples), or port the noise synths into `OnAudioFilterRead`, which is S.
- Spatialise traffic engines (JS plays only the player's vehicle).

### 4.25 Save (`main.js save/loadSave`)

**What it does**
- `localStorage['backroads-save-v1']`, written every 10 s:

  ```
  {money:int, time:hours, quality:0|1|2, stats:{deliveries, earned}, fuel:{pickup:…, hatch:…}}
  ```

- `backroads-help-seen` flag.
- Not saved: positions, ground deformation, dirt, knocked trees, active job.

**Port: (b), S.** JSON in `Application.persistentDataPath/save.json`, same fields plus `version`. Migrating browser saves is not worth it (they are local to the browser). Optional new feature: persist ground deformation as sparse per-chunk deltas against `base.f32`.

### 4.26 Input and gamepad (`input.js`, `main.js`)

**Bindings**

| Action | Keyboard / mouse | Gamepad (standard mapping) |
|---|---|---|
| Throttle / brake & reverse / steer | W/↑, S/↓, A/←, D/→ | RT (7) analog, LT (6) analog, left stick X (axis 0) |
| Handbrake | Space | A (0) |
| Get in / out | E | Y (3) |
| Lights / horn | L / H | — / X (2) |
| Camera chase/hood | C | Back (8) |
| Reset upright (< 4 m/s) | R | Start (9) |
| Tow ($50) | T | — |
| Blade up / down | Q / Z | RB (5) / LB (4) |
| Low range | X | — |
| Winch toggle / reel | G / hold W | — / RT |
| Cancel job | Backspace | — |
| Load job | 1–3 (also Numpad) | — |
| Map / help / graphics | M / F1 / F2 | — |
| Time ±1 h | ] / [ | — |
| Look / zoom | mouse (pointer lock), wheel | right stick (axes 2/3) |
| On foot: move, run, jump | WASD, Shift, Space | left stick, B (1), A (0) |
| Dig / pile | LMB / RMB | RB / LB |

Stick deadzone 0.14.

**Port: (c) Input System package** (`com.unity.inputsystem`, Unity 6 default): one `.inputactions` asset with Driving, OnFoot and UI maps, plus rebinding UI later. S. Note: JS has no steering-wheel support; Input System HID or Logitech SDK would be new work.

### 4.27 Bootstrap, loop and glue (`main.js`)

**What it does**
- Boot order (§3.1). Five player vehicles on the garage bays (order pickup, hatch, rally, truck, dozer); each is `place`d, then 240 settle steps with brakes on, then parked and synced. The player starts in the pickup.
- Loop per frame (dt ≤ 0.05), in order:
  1. Poll the gamepad; global keys; vehicle or foot controls.
  2. Traffic update.
  3. Fixed-step physics (bodies = player fleet + materialised traffic; then all-pairs vehicle collisions).
  4. Interpolation; winch cables; impacts → thud, shake, cargo damage.
  5. Effects; terrain dirty flush; falling trees.
  6. Camera; sky; night emissives.
  7. Terrain LOD; grass; tree split; lights.
  8. Particles; water; boats; jobs; HUD; audio.
  9. Bloom strength; render; save every 10 s.
- Lamp lights: a pool of 6 point lights (intensity 32·night, range 26, decay 1.7) assigned to the nearest lamps within 140 m every 0.3 s.
- Debug: `window.__game`, `G.update`, `G.noRender`, `PLAN_DEBUG`.

**Port: (b), M.**
- A `GameBootstrap` MonoBehaviour plus systems ordered via Script Execution Order or a single `GameLoop` driving plain C# systems, with fixed-step physics in `FixedUpdate`.
- In HDRP/URP Forward+, give each of the 78 lamps a real light (no shadows, distance-culled) instead of the pool.
- Keep a test hook equivalent to `G.update(dt)` for golden traces and CI.

### 4.28 Tools: Blender generators, build scripts, hosting

- `tools/blender/vehicles.py`, `trees.py`, `buildings.py` and `tools/build-*.bat` are **(a) kept unchanged**. They are the source of truth for the GLBs.
  - Blender 4.5 path: `%LOCALAPPDATA%\Programs\Blender Foundation\Blender 4.5\blender.exe`, `-b --factory-startup --python … -- --out … --previews …`.
  - The scripts export glTF with `export_yup=True`. Vehicles are authored in game space and converted with `TO_BLENDER` before export.
  - LOD1 via a Decimate (COLLAPSE) modifier: Body 0.14, Blade 0.3. Trees are voxel-remeshed and vertex-painted; buildings use per-pane Glass objects.
- Unity work items:
  1. Point the Unity project's glTFast import at `Assets/Backroads/Art/*.glb`, either copied by the bat files or via a symlinked folder.
  2. Optionally add a `--fbx` flag. Not required with glTFast.
  3. **Freeze the canon `buildings.glb`.** Changing window counts shifts pane indices (§3.3). If the buildings are remodelled, re-resolve the lit flags by pane position, not by index.
- `server.mjs`, `Play.bat`, `.claude/launch.json`: **(d)** for Unity. Keep them so the JS oracle and the exporter can run.

---

## 5. Summary table

| # | System | Class | Unity target | Effort | Depends on |
|---|---|---|---|---|---|
| 1 | Export tool (JS) + Canon importer (C#) + hash checks | new | `js/exportUnity.js`, `CanonImporter.cs` | M | — |
| 2 | World grid and sampling | a+b | `WorldGrid` (NativeArray, Burst) | S | 1 |
| 3 | World generation | d | stays in JS (oracle) | — | — |
| 4 | Deformation (rut/brush/blade/deposit/relax) | b | Burst jobs on `WorldGrid` | M | 2 |
| 5 | Terrain mesh, LOD, shader | b | custom chunk meshes + Shader Graph (or Unity Terrain, see §4.3) | L | 2, 4 |
| 6 | Road ribbons + markings | b | generated meshes | S | 1 |
| 7 | **Junction meshes (new)** | new | Clipper2 + LibTessDotNet mesher | L | 1, 13 |
| 8 | Town planner / canon / profile / OSM | a / d | data only | S | 1 |
| 9 | Buildings and static props | a+c | glTFast prefabs, MPB colours, GPU Resident Drawer | M | 1 |
| 10 | Collider grid | b | C# grid (+ optional PhysX colliders) | S | 1 |
| 11 | Trees | a+c | LODGroup prefabs + wind Shader Graph | M | 1 |
| 12 | Rocks, boats, water | a / b / c | instances; HDRP Water or URP shader | S | 1 |
| 13 | Traffic network | a | `TrafficNetwork` SO from `traffic.json` | S | 1 |
| 14 | Traffic AI | b | C# `TrafficSystem` | L | 13, 16 |
| 15 | Grass | b | instanced tiles, Shader Graph | M | 2, 5 |
| 16 | Vehicle physics (incl. mud, low range, AWD-on-low) | b | custom integrator at 1/120 s (phase 2: Rigidbody hybrid) | L | 2, 4, 10 |
| 17 | Dozer blade + tracks | b | part of 16 | M | 16 |
| 18 | Winch | b | part of 16 + LineRenderer | S | 16 |
| 19 | Vehicle models, materials, dirt shader, lights | a+c | glTFast + material remap + Shader Graph | M | 16 |
| 20 | engine-sim drivetrain + audio (new) | new | native plugin bridge | L | 16 (see engine-sim report) |
| 21 | Character + shovel | b+c | custom controller + rigged model | M | 2, 4 |
| 22 | Camera | b | ported rig | S | 2 |
| 23 | Sky, day/night, fog | b+c | HDRP PBSky/clouds or URP ToD asset | M | — |
| 24 | Post-processing + quality | c | Volume profile + Quality levels | S | 23 |
| 25 | Particles | c | VFX Graph / Shuriken | S | 16 |
| 26 | Jobs/economy | a+b | SO tables + `JobSystem` | S | 16, 9 |
| 27 | HUD, maps, labels | c | UI Toolkit | M | 26 |
| 28 | Audio (non-engine) | c | AudioSources / OnAudioFilterRead | M | 16 |
| 29 | Save | b | JSON in persistentDataPath | S | 26 |
| 30 | Input | c | Input System actions | S | — |
| 31 | Main loop / glue | b | bootstrap + system order | M | all |

Rough total for parity (rows 1–31, excluding engine-sim and online): **about 5–7 months** for one part-time developer with AI help. The L items (terrain, vehicle physics, traffic AI, junctions) are about 60 % of that.

---

## 6. Suggested order (each milestone ends in something playable or comparable)

1. **M0 — Canon in Unity (2–3 weeks):** exporter + importer + hash checks; `WorldGrid`; terrain mesh (flat-shaded first); road ribbons; `static_props.glb`; tree instances; free-fly camera. Compare against `reference/shots`.
2. **M1 — Drive the pickup (3–4 weeks):** vehicle core port, deformation, camera, input. Golden traces for asphalt, braking and bog.
3. **M2 — Whole fleet and offroad (3 weeks):** remaining DEFS, dozer, winch, low range/AWD, dirt shader, particles, character and shovel.
4. **M3 — Living roads (4–6 weeks):** import the traffic network, port the AI, **junction meshes and markings**.
5. **M4 — Game loop (2–3 weeks):** jobs, HUD/maps, save, sky/post, audio.
6. **M5 — New goals:** engine-sim, new trucks and race cars, Synty dressing, online (server-authoritative jobs and money; deformation replicated as calls).

---

## 7. Synty assets (relates to the user's request about a Synty login)

- **Do not give the Synty password to the agent.** Claude must not enter account passwords. The user signs in to syntystore.com (or the Unity Asset Store, if the packs were bought there; they then appear in Package Manager → My Assets) and downloads the `.unitypackage` files themselves. Save them, for example, to `D:\SyntyPacks\` and tell the agent the path; the agent can then import them (Assets → Import Package → Custom Package) and work with the models.
- **Licence hygiene:** the Synty licence forbids redistributing raw assets. Put them under `Assets/ThirdParty/Synty/` and **git-ignore that folder**, even in the private repo, so the repository can be shared or made public later. Built game binaries are fine. Check the EULA for per-seat terms if collaborators join.
- **Fit with canon:** canon is *positions, footprints and colliders*, not meshes.
  - A Synty building can replace a Blender model if it is scaled into the same `MODEL_CORE` footprint and sits at the exported `y`.
  - Synty POLYGON assets colour through a shared atlas texture, not named material slots. Canon per-building colours (Wall/Roof/Door…) and per-pane night lighting would therefore need a palette-remap shader or a nearest atlas variant.
  - Recommendation: keep the canon Blender buildings for Millbrook. Use Synty for vehicles' interiors and props, street furniture (replacing typed lamps, fences and mailboxes 1:1 at the exported transforms), characters and site dressing.
  - Synty packs typically ship Built-in/URP materials. HDRP needs conversion; verify per pack.

---

## 8. Open questions and how to settle them

1. **Render pipeline (URP vs HDRP)** changes the (c) targets in §4.9, §4.11, §4.12 and §4.17. Decide before M0 shading work (covered in the pipeline research report).
2. **Unity Terrain vs custom mesh (§4.3).** Prototype in M0: a 2049² `TerrainData` with 4 rut stamps per wheel at 60 Hz via `SetHeightsDelayLOD` + `SyncHeightmap` every 0.25 s, measured in the Profiler on the RTX 5080 laptop and on a mid-range PC.
3. **Unity heightmap precision** (15 vs 16 bits): read back `TerrainData.GetHeights` after `SetHeights` on a ramp and measure the step size.
4. **glTFast child order for Glass panes:** after import, compare `transform.GetChild(i).name` with the exported `panes` list for all 11 models. If anything differs, always map by name.
5. **Burst float determinism for online deformation:** test `FloatMode.Deterministic` across two machines with a 10,000-call `rut` script and compare hashes.
6. **Exact package versions** (glTFast 6.x, Input System 1.x, Splines 2.x, Cinemachine 3.x on Unity 6000.3 LTS): pin whatever Package Manager offers on 6000.3.20f1. These were not checked online for this report.
7. **The README says "~6,000 trees"; the canon build has 5,265.** Update the README or accept it. The export pins 5,265.
