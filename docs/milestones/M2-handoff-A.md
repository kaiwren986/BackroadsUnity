# M2 handoff: lane A, T2.9 (Art A1: FBX postprocessor, material remap, LOD groups)

Branch `m2/a-import` in `D:\Backroads-Unity-wt\a`, from `main` at `c42a473`. Nothing was pushed, installed or
downloaded. The oracle was read only with `git -C D:\Backroads show 552f7807:assets/vehicles/truck.glb` (sha256
`ca3c549a…1523`, 532,528 bytes); `D:\Backroads` is untouched. No Synty files were touched.

## Done

| Step | What | Where |
|---|---|---|
| 1 | **`glb_to_fbx.py`**: headless Blender 4.5.12 reads a browser-game GLB with Blender's glTF importer and writes FBX with the 07 §3.2 flags. Nothing is renamed, merged or re-triangulated; the script fails if a node name or a material slot changed on the way. It writes `<out>.report.json` (nodes, triangles, materials, Unity-space bounds, both sha256) and with `--unity-dir` copies the FBX (and the untouched GLB) into Unity. **`br_fbx.py`** is the one shared exporter; `br_canary.py` now uses it, so the canary proves exactly the flags every export uses. It also makes the FBX bytes deterministic (pinned header timestamp, FBX object ids from a stable hash instead of Python's per-process `hash()`): re-running on the same input writes the same file and no new LFS object. **`br_lod_fixture.py`** writes the LOD/UCX test fixture. | `art/pipeline/`, `art/vehicles/truck/VH_Truck.fbx` (+ `.report.json`), `art/fixtures/VH_LodFixture.fbx` |
| 2 | **`BackroadsModelPostprocessor` A1** (version 2). Every FBX under `Assets/Art/`: scale 1, `bakeAxisConversion` off, normals imported, Mikk tangents (as M0); **LOD Groups from names** (`LodFromNames`): one group per parent whose children carry `_LOD<n>`, same-suffix siblings share a level, a child with LOD children of its own (`PART_Door`) gets its own group, today's unsuffixed `Body` next to `Body_LOD1` is level 0; heights 0.30 / 0.12 / 0.04, last level culled below 0.01 (07 §8.5), cross-fade on. Unity's own name-based LOD groups are replaced, so one rule decides. **`UCX_` nodes** become convex MeshColliders with the MeshRenderer and MeshFilter removed (`CollisionHulls`). Vehicles and wheels (`Assets/Art/Vehicles/`, `Assets/Art/Wheels/`): no animation, `VH_` files readable, and **every material remapped by name** to the 10 project slots (`VehicleMaterialSlots`: identity for `M_*` names, the 07 §4.3 SLOT_MAP fold for today's 28 legacy names). Each vehicle import depends on the slot `.mat` files, so a model imported before them is imported again. | `Game/Assets/Backroads/Art/Editor/` |
| 2 | **Slot materials** `M_Paint`, `M_Trim`, `M_Glass`, `M_Lights`, `M_Tyre`, `M_Rim`, `M_Interior`, `M_Chassis`, `M_Decal`, `M_VehicleFar`: URP/Lit placeholders (colours from vehicles.py MATDEF; glass transparent) until phase C's Shader Graphs. `ArtMaterials.CreateVehicleSlots` never overwrites an existing one. | `Game/Assets/Art/Materials/Vehicle/` |
| 3 | **Today's truck both ways, side by side**: `truck.glb` (byte copy of the oracle file, glTFast 6.20.0 importer, default settings) and `VH_Truck.fbx` (ModelImporter + postprocessor). `ModelCompare` compares names, transforms, bounds, submeshes, triangles and slots; `RemapProbe` answers 07 §12. Evidence: `glb-vs-fbx.json`, `remap-probe.json` and two renders. | `Game/Assets/Art/Vehicles/Truck/`, `art/vehicles/truck/review/` |
| Tests | `Backroads.Tests.EditMode.Art` (category `Art`, editor-only asmdef): `Art.TruckFbxMatchesGlb`, `Art.LodGroupFromNames`. | `Game/Assets/Backroads/Tests/EditMode/Art/` |
| Tools | `art\pipeline\unity-art.ps1 -Steps <materials,reimport,probe,compare,capture> [-Graphics]` runs `Backroads.Art.Editor.ArtBatch.Run` through `tools\ci\ci-lib.ps1` (refuses while the user plays, takes a Unity slot, waits on `machine.lock`; `-Graphics` also takes `.gpu.lock` and drops `-nographics` for renders). | `art/pipeline/unity-art.ps1`, `ArtBatch.cs` |

## Gates (card T2.9; no M2 gate, feeds M3 phase B), expected vs measured

| Test | Expected | Measured |
|---|---|---|
| `Art.TruckFbxMatchesGlb` | same node names, transforms within 1e-4 m, bounds within 1 mm, material slots remapped on first import | **pass**. Names: the same 5 (`Body`, `Body_LOD1`, `Caliper`, `Wheel`, `Wheel_LOD1`), no duplicates. Position, rotation and scale deltas **0** (all nodes at the origin in both). Bounds: max delta **0.000477 mm** per node and for the whole truck, (-1.5, -0.527, -4.3)..(1.5, 3.76, 4.1334) m in both. Submeshes and triangles per submesh identical (39 submeshes, 20,618 triangles). Material slots: **39/39** are the project material the GLB name maps to, on the first import of a fresh copy (new GUID, no `.meta`) and after a full Library rebuild. Importer: scale 1, `bakeAxisConversion` off, readable, root rotation identity. |
| `Art.LodGroupFromNames` | LOD Group from `_LOD0/_LOD1/...` names | **pass**. Fixture: 2 groups. Root: 3 levels `Crate_LOD0,Lid_LOD0` / `Crate_LOD1,Lid_LOD1` / `Crate_LOD2`, heights 0.30 / 0.12 / 0.01, cross-fade. `PART_Door`: its own 2 levels `Door_LOD0` / `Door_LOD1`. `UCX_Crate_00`: convex MeshCollider, no MeshRenderer or MeshFilter, in no LOD level. Identity remap `M_Paint`, `M_Glass`. Truck: 1 group on the root, `Body,Wheel` / `Body_LOD1,Wheel_LOD1`, heights 0.30 / 0.01; `Caliper` (no LOD1) stays outside. Name parser and height table unit-checked. |
| `Art.Canary` (M0) | unchanged | **pass** (MARK_Front (0,0,2), MARK_Left (-1,0,0), MARK_Up (0,1,0)) |
| Full EditMode (`!Slow;!PlayerOnly`) | no regressions | **91 passed, 0 failed, 2 skipped** (`Build.ServerContents`, and `Repo.OwnersRespected` without `BR_OWNERS_BASE`; with `BR_OWNERS_BASE=main` see below) |

**07 §12, `AddRemap` vs `OnAssignMaterialModel` (Unity 6000.3.20f1):** `RemapProbe` imports a fresh copy of the truck
FBX once per hook. All three variants remap **39/39 slots on the first import** and again on a reimport, with no
material sub-assets and no files left beside the model: `AddRemap` in `OnPreprocessModel`, `OnAssignMaterialModel`
with materials in the prefab, and `OnAssignMaterialModel` with the legacy External location. The difference:
`AddRemap` writes 38 `externalObjects` entries into the `.meta`, one for every name the map knows, most of them absent
from the file. **We keep `OnAssignMaterialModel` (in prefab)**: the `.meta` stays clean and the C# map is the only
place the mapping lives. A full Library rebuild imports the 10 `.mat` files before the FBX and the remap holds; the
`DependsOnSourceAsset` dependency covers the other order.

## Screenshots

- `art/vehicles/truck/review/truck_glb_vs_fbx.png`: the GLB through glTFast (left) and the FBX through Blender
  (right), both at LOD0. Same shape. The colours differ on purpose: the FBX uses the project's placeholder slot
  materials, with paint blue and the green/yellow livery folded into `M_Paint`, until the M4 shaders.
- `art/vehicles/truck/review/truck_fbx_lod0_lod1.png`: the FBX truck's two LOD levels (14,638 vs 2,040 body triangles).

## For I

1. **`Game/ProjectSettings/MultiplayerManager.asset`**: Unity 6000.3.20f1 (with `com.unity.dedicated-server`) flips
   `m_EnableMultiplayerRoles` from 0 to 1 every time it opens a fresh worktree. I reverted it before every commit.
   Commit the value Unity wants, or set it in `ProjectSetup.Apply`, so lanes stop seeing the diff.
2. **A tools\ci entry for editor batch steps.** PLAN §1.9 names `tools\ci\art.ps1 -Id <id>`. Until it exists,
   `art\pipeline\unity-art.ps1` uses `ci-lib.ps1`'s `Invoke-BrUnity` (Unity slot, `machine.lock`), `.gpu.lock` and the
   user-playing check. Adopt it, wrap it or replace it as you prefer.
3. **`tools\ci\gates.json`** (optional; the card has no M2 gate): `Art.TruckFbxMatchesGlb` and
   `Art.LodGroupFromNames` run in the normal EditMode step (category `Art`). Add them as M2 lane gates if you want them
   listed.
4. **Card text:** T2.9, T2.2 and the lane prompt say `assets/models/*.glb`. The oracle path at 552f7807 is
   **`assets/vehicles/*.glb`**. Please fix it in the M3 cards.
5. **Lane V:** `Game/Assets/Art/Vehicles/Truck/truck.glb` is a byte copy of the oracle truck (glTFast import, default
   settings). If V imports the same GLB elsewhere for M2, the two copies share one LFS object. V can also use this
   one. For the FBX truck, the LOD Group sits on the root and covers `Body` and the single `Wheel` template.
   Phase B brings sockets and separate wheel assets.
6. **Expected import warning:** Unity logs `File 'VH_Truck' has inconsistent LOD naming (_LOD1 found but no _LOD0)`
   for today's legacy names, because "names preserved" keeps `Body` and does not rename it to `Body_LOD0`. Our postprocessor builds the right group anyway (test
   above). Phase B files use `_LOD0`.
7. **07 §12 answered** (above). If you keep 07 and PLAN current, record it: both hooks work on Unity 6.3, and we use
   `OnAssignMaterialModel`.
8. `.gitignore` (optional): add `__pycache__/`. The art scripts set `sys.dont_write_bytecode`, so nothing is written
   today.

## Deviations and decisions

1. **Oracle path** `assets/vehicles/truck.glb`, not `assets/models/truck.glb` (For I 4).
2. **Legacy LOD names.** "Names preserved" keeps `Body` + `Body_LOD1` (and `Wheel` + `Wheel_LOD1`). The postprocessor
   treats an unsuffixed node as level 0 when a sibling `<name>_LOD1` exists. It always builds the groups itself
   rather than relying on Unity's name-based LOD import.
3. **Materials are folded into slots.** Today's 22 truck materials map onto 6 slots (`M_Paint`, `M_Trim`, `M_Glass`,
   `M_Lights`, `M_Tyre`, `M_Interior`), as 07 §4.3 plans. 07 does not list `BoxPanel` (→ `M_Paint`), `Rib`, `Brake`,
   `DarkAlloy` or `Caliper` (→ `M_Trim`), or `Yellow` (→ `M_Paint`). The test checks the mapping per submesh, not
   the look. The FBX truck therefore looks different from the GLB until phase C.
4. **Remap hook:** `OnAssignMaterialModel` instead of the `AddRemap` in the 07 §3.3 sketch (measured, above).
   `BackroadsModelPostprocessor.Hook` keeps all three variants for `RemapProbe` to re-check after a Unity upgrade (U4a).
5. **LOD heights** follow 07 §8.5 (made for four-level phase-B LODs). For today's two-level truck, LOD1 starts below
   30 % screen height: about 24 m with a 60° camera, against the browser game's 40 m (`LOD_DIST`). The player's
   vehicle should use `LODGroup.ForceLOD(0)` (07 §8.5).
6. **Not in A1:** custom properties to `BrNodeProps`, `VehicleJsonImporter` and `VehiclePrefabBuilder` (PLAN §1.7)
   come in phase B with the first files that need them. The glTFast GLB keeps glTFast's own materials (the card asks
   for the remap on the FBX only).
7. `CANARY.fbx` was **not** regenerated. Its script now imports the shared exporter; a scratch run still exports the
   same marker positions.

## Next (M3, phase B: `VH_Truck_Box75.fbx`)

`br_build.py` around `art/legacy/vehicles.py`, with sockets, `SLOT_MAP` on the Blender side (names `M_*`, identity
remap here), LOD0-3 from subdivision levels (`_LOD0..3`), UCX_ hulls, the sidecar `.vehicle.json` with a
`VehicleJsonImporter`, the prefab builder, and `Art.TruckBudget`. The postprocessor, slot materials and tests above
carry over unchanged.

## Resume

```powershell
cd D:\Backroads-Unity-wt\a
# 1. GLB -> FBX (and copy into Unity); the GLB comes from the oracle through git:
cmd /c "git -C D:\Backroads show 552f7807:assets/vehicles/truck.glb > %TEMP%\truck.glb"   # cmd's > is binary-safe
. .\tools\ci\env.ps1
& $BLENDER -b --factory-startup --python art\pipeline\glb_to_fbx.py -- --in $env:TEMP\truck.glb --out art\vehicles\truck\VH_Truck.fbx --unity-dir Game\Assets\Art\Vehicles\Truck --glb-name truck.glb --source "D:\Backroads@552f7807f4ad56547815c32a5e8407a7c324d556:assets/vehicles/truck.glb"
& $BLENDER -b --factory-startup --python art\pipeline\br_lod_fixture.py -- --out art\fixtures\VH_LodFixture.fbx --unity-dir Game\Assets\Art\Vehicles\_Fixtures
# 2. Unity steps (slot materials, probe, compare, renders):
art\pipeline\unity-art.ps1 -Steps materials,probe,compare
art\pipeline\unity-art.ps1 -Steps capture -Graphics
# 3. Tests:
tools\ci\unity-tests.ps1 -Platform EditMode -TestFilter Backroads.Tests.EditMode.Art
git checkout -- Game/ProjectSettings/MultiplayerManager.asset   # Unity flips it on open (For I 1)
```

Do not write the GLB with PowerShell 5.1's own `>`: it re-encodes binary output. `cmd /c` or Git Bash is fine; the
sha256 must be `ca3c549a1aecbab10166b74efede6e45fe1ed2ae0223b5b4f2babd71934b1523`.
