# 07 - Art pipeline: Blender to Unity 6 (URP), vehicles, Synty

Status: research report, 2026-10-18. Written for the Backroads port to Unity.
Scope: reference material on disk, Blender to Unity transport, naming, automated export, baking, shaders (paint, glass, lights, mud, livery), procedural versus hand modelling, poly budgets, the legal side of vehicles "inspired by" real ones, and Synty POLYGON packs.
Nothing here is legal advice. Prices and licence terms were read from the vendors' pages on 2026-10-18 and can change.

---

## 0. Decisions in one screen

| Topic | Recommendation |
|---|---|
| Render pipeline | **URP** (Forward+). Unity's 2026 render pipeline plan puts new work into URP. HDRP gets maintenance only, and Built-in is deprecated from Unity 6.5. Synty packs ship for URP or Built-in, not HDRP. |
| Unity version for art work | 6000.3.x (6.3 LTS) is installed and fine. Everything below also works on 6000.5. Plan to move to 6.7 LTS when it ships (due end of 2026). |
| Transport format | **FBX** for everything that ends up in a Unity scene. Keep writing **GLB** from the same scripts for the three.js build and for a possible later runtime or modding path (glTFast). |
| Source of truth | One `.blend` per hero vehicle, generated first by Python and then tuned by hand, plus the generator scripts. Every step after that (UVs, bake, LODs, export) is a script, so you can re-run it any time. |
| Materials | 5 to 9 materials per vehicle at LOD0 (Paint, Trim sheet, Glass, Lights, Tyre, Rim, Interior, Chassis, Decal), down from the 19 to 22 today. Unity remaps them by name to project Shader Graph materials. |
| Textures | Shared 2048 vehicle trim sheet with a palette strip. A per-vehicle 2048 normal map and a packed MASK (AO, edge, cavity, paint zone). Tiling triplanar mud textures. No photo textures on vehicles. |
| Mud | Port the existing `dirtify()` shader logic (`js/vehicleModels.js`) to a Shader Graph subgraph `SG_MudLayer`. Add wetness, cavity mask and an optional splat render texture. |
| LODs | Authored LOD0 to LOD3 (`_LOD0` to `_LOD3` suffixes, so Unity builds the LOD Group itself), plus a separate wheel asset with its own LODs. Unity's automatic "Mesh LOD" only for static props. |
| Modelling | Procedural cage from blueprint profiles, then a hand-tuning pass in Blender by the user, then automated subdivision, bevels, bake and LODs. Start with boxy classes (trucks, pickups, construction). |
| Legal | Fictional brands only. No badges, real model names, signature grilles or iconic silhouettes. Mix three or more inspirations per design. Rename "Ranger 4x4" and "Muncher **D3**" now (section 9). |
| Synty | Good for town, farm, construction and nature dressing, and as placeholder traffic or race cars. Buy **one-time** licences for the packs you keep (a SyntyPass subscription stops covering new development when it ends). Keep Synty files **out of git**. Agents work only with prefab names and paths, not the model files themselves (generative AI clause, section 10.4). **No agent should use your Synty login** (section 10.1). |

---

## 1. What is already on disk

### 1.1 `D:\RaceMockups` (listed read-only)

**This folder has no vehicle reference material.** Here "race" means a playable fantasy race (a tiger-folk character), not motor racing. The folder's own `README.md` describes it as procedural Blender mockups for a separate EverQuest-style RPG project.

| Folder | Files | Size | What is in it |
|---|---|---|---|
| `blend/` | 6 `.blend` + 3 `.blend1` backups | 377 MB (29 to 52 MB each) | Character variants (`vahshir_male_tiger`, `_tawny`, `vahshir_female_white`, plus `_idle` poses) |
| `renders/` | 22 PNG | 46 MB | Turnaround sheets at 1200x1600, 1200x1200, 2400x800 and 1800x1400; head close-ups; a lineup |
| `sprites/` | 58 PNG in 3 sub-folders + 1 preview | 14 MB | Pre-rendered 8-direction isometric sprites at 512x512 and 128x128, plus row sheets |
| `scripts/` | 5 `.py` (`vahshir_build.py` is 72 KB) | 89 KB | Headless Blender build, turntable render and sprite render scripts |
| root | `README.md` | 3 KB | Build instructions |

Totals: 80 PNG, 9 Blender files, 5 Python scripts, 1 Markdown file, 437 MB.

What Backroads can reuse is **technique, not content**:
- The pattern of a headless build that writes a `.blend` and renders a turntable (`blender -b --factory-startup -P script.py -- --render`) is the same review loop proposed in section 5.
- The body there is SDF primitives meshed with a voxel remesh. That suits organic shapes and **does not** suit crisp car panels (section 8).
- Its own "not game-ready yet" list (retopology, UVs, bakes, LODs) is the same list our vehicles need.

So any real-vehicle photos still have to be collected (section 8.3).

### 1.2 Existing Blender generators in `D:\Backroads\tools\blender\`

| Script | Output | What it does now | Gaps for Unity |
|---|---|---|---|
| `vehicles.py` (1,174 lines) | `assets/vehicles/{hatch,rally,pickup,truck,dozer}.glb` | Geometry is authored in **game space** (x = left, y = up, z = forward, ground at y = 0) and converted with `TO_BLENDER` before `export_scene.gltf(export_yup=True)`. Bodies are lofted from station tables (`loft()`, 9 to 12 stations, subsurf level 2). Wheel arches are cut with EXACT booleans (`cut_arches`). Detail is `box`/`cyl`/`lathe`/`tube` primitives. LOD1 is `lod_copy()`: Decimate COLLAPSE at 0.14 for the body and 0.3 for the blade, with minor materials folded into `Trim`. Smoothing is `smooth_by_angle(38 deg)`. | **No UVs** except the dozer tracks. Colours come only from flat materials (19 to 22 per vehicle). One LOD level only. No sockets: wheel positions are hard-coded twice, in `place_wheels_for_preview()` and in `js/vehicle.js` `DEFS`. Lamps are materials, not light positions. No collision meshes. No damage parts. |
| `buildings.py` (641 lines) | `assets/buildings/buildings.glb` (15,018 tris, 16 materials) | 11 models (House_A to C, Shop_A/B, Warehouse_A to C, Barn, Garage, Shed). Each has a `Body` with named recolourable materials and **one `Glass` object per window pane** for night lighting. | Hundreds of pane objects become hundreds of GameObjects in Unity. Replace them with one glass mesh per building that carries a per-pane ID in vertex colour (section 6.8). No UVs. |
| `trees.py` (502 lines) | `assets/trees/trees.glb` (8,476 tris, 1 material) | 9 species plus `_LOD1` copies. Vertex-colour shading in `COLOR_0`. Canopies are voxel-remeshed sphere clusters. | Already close to Unity-ready: the names map straight onto LOD Groups. Needs a URP vertex-colour plus wind Shader Graph. |

Measured from the current GLBs (triangles per node):

| Vehicle | Body | Body_LOD1 | Wheel (one) | Wheel_LOD1 | Body submeshes |
|---|---|---|---|---|---|
| hatch | 20,638 | 2,881 | 3,924 | 252 | 14 |
| rally | 20,377 | 2,848 | 3,524 | 252 | 16 |
| pickup | 22,274 | 3,110 | 3,632 | 252 | 16 |
| truck | 14,638 | 2,040 | 3,580 | 252 | 20 |
| dozer | 9,916 (+ 2 x 768 tracks, 1,708 blade) | 1,359 | n/a | n/a | 11 |

Each submesh is a draw call in Unity. A hatch today costs about 14 + 4 x 5 = 34 draws at LOD0. The consolidation in section 4 brings that to 10 or fewer.

The mud look is already worked out in `js/vehicleModels.js` `dirtify()`. It climbs from the sills with object-space height, breaks up with a 128 px tileable noise, darkens to linear (0.085, 0.062, 0.042), pushes roughness to 0.88 and removes metallic and clear coat. Section 6.5 ports it one-for-one.

---

## 2. Pipeline, look and lighting targets

- **URP Forward+.** It has no 8-lights-per-object limit, which matters for headlights and indicators on traffic at night. It supports the GPU Resident Drawer, which needs Forward+ and works only with MeshRenderers.
- **The look: "Synty-plus".** A clean, low-frequency stylised world with slightly more detailed, smoothly shaded hero vehicles. Mud and wetness are the main surface detail.
- **What makes "pretty good graphics" in URP** (all built in to Unity 6 URP):
  - The SSAO Renderer Feature.
  - Main light shadows: 4 cascades, about 150 to 250 m distance, soft shadows.
  - A **realtime reflection probe that follows the player vehicle** (128 px, time-sliced, "Via scripting" refresh), the biggest single win for car paint.
  - Box-projected baked probes in the town.
  - Adaptive Probe Volumes for the town's GI.
  - Post-processing: Bloom (threshold 1.0, so HDR lamp emission blooms), Neutral or ACES tonemapping, a colour-grading LUT, and a light vignette.
  - TAA or STP upscaling for performance.
  - Unity's 2026 URP plan adds physical light units, auto-exposure, a physical sky and realtime GI. Design the lighting so it can take these later.
- **Headlights:** two URP Spot Lights per vehicle with a light cookie for the beam pattern. Shadows only on the player's lights. Traffic further away gets emissive lenses plus an additive fake beam cone mesh (Shader Graph with a Scene Depth fade), and no real light.

---

## 3. Blender to Unity transport: FBX vs glTF (glTFast)

### 3.1 Comparison for our needs

| Need | FBX through Unity's ModelImporter | glTF/GLB through glTFast 6.x (`com.unity.cloud.gltfast`, 6.19.0 released 2026-05-19, needs Unity 6.0 LTS or later) |
|---|---|---|
| LOD Group from `_LOD0`, `_LOD1`... names | **Automatic** (long-standing ModelImporter behaviour) | **No.** `MSFT_lod` is listed as "not investigated". You would build LOD Groups yourself in a post-import script. |
| Remap to our project materials | **Yes.** Materials tab "Search and Remap", `AssetImporter.AddRemap`, `OnAssignMaterialModel` / `OnPreprocessMaterialDescription` | Generates its own glTF Shader Graph materials. Overriding them needs a custom material generator or a prefab post-process. |
| Custom metadata (wheel radius, hinge axis, light type) | Blender custom properties, exported with `use_custom_props=True`, arrive in `AssetPostprocessor.OnPostprocessGameObjectWithUserProperties` | Blender writes glTF `extras`. In glTFast you read them through the add-on API (6.18 added a post-deserialization hook), which means more code. |
| Blend shapes, animation (doors, dozer blade), vertex colours | Yes | Yes. 6.19 added add-on support for custom animation import. |
| Clear coat in the file's material | Not carried (FBX materials are basic) | `KHR_materials_clearcoat`: URP support is partial (coat mask and smoothness only). Irrelevant once we remap to our own materials. |
| Unity mesh tooling (Mesh LOD generation, lightmap UV generation, mesh compression, Read/Write, Mesh Optimization) | All ModelImporter options | Fewer options |
| Runtime loading (mods, user-made vehicles later) | No (editor only) | **Yes.** This is glTF's real advantage. |
| Works today | Exporter call not yet written | Exporters already written |

**Decision:** export FBX for the Unity project and GLB as a secondary output from the same build step. Do **not** use Unity's direct `.blend` import. It shells out to a local Blender install on every machine and every reimport, it is slow, and the results depend on that Blender's version.

### 3.2 Blender 4.5 FBX export (drop-in for `vehicles.py` `export()`)

```python
def export_fbx(path, objs):
    bpy.ops.object.select_all(action='DESELECT')
    for ob in objs:
        ob.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={'EMPTY', 'MESH'},
        apply_unit_scale=True, apply_scale_options='FBX_SCALE_ALL',   # 1 Blender m = 1 Unity unit, scale 1
        axis_forward='-Z', axis_up='Y', bake_space_transform=True,   # "Apply Transform": no -90 deg root rotation
        use_mesh_modifiers=True, mesh_smooth_type='OFF',             # write normals only; Unity imports them
        use_triangles=True, use_tspace=True,                         # same triangulation and tangents as the bake
        colors_type='LINEAR',                                         # masks in FLOAT_COLOR attributes stay numeric
        use_custom_props=True, add_leaf_bones=False, bake_anim=False,
        path_mode='STRIP', embed_textures=False)                      # textures are written separately as PNG
```

Two things to watch:
- **Triangulate before baking** with a Triangulate modifier and Keep Normals on. Then the normal map, the exported mesh and Unity's tangents (set Tangents to *Calculate Mikktspace*) all agree. Blender bakes OpenGL-style (+Y) normal maps, which is what Unity expects, so no green-channel flip is needed.
- **Axis "canary".** The axis options above are the usual recipe, but get them confirmed by a test, not by memory.
  - Export a tiny `CANARY` asset each build: a 10 cm cube `MARK_Front` at game (0, 0, 2), `MARK_Left` at game (1, 0, 0) and `MARK_Up` at (0, 1, 0).
  - A Unity EditMode test asserts that after import `MARK_Front.z > 0`, `MARK_Left.x < 0` (Unity +X is right) and `MARK_Up.y > 0`, with root rotation identity and scale 1.
  - If you use Blender's `bake_space_transform`, set Unity's `bakeAxisConversion = false` (or the other way round). Use one, never both.
- **Coordinates:** game space (three.js, right-handed, x = left) maps to Unity (left-handed, x = right) as Unity = (-gx, gy, gz). From Blender space that is Unity = (-bx, bz, -by). The importers do this for meshes and nodes. You only need it when you **hand-write** numbers, for example porting `DEFS.wheel.x` or writing the JSON sidecar (section 4.4).

### 3.3 Unity import side (sketch; compile and test it in a probe project first)

`Assets/Editor/ArtPipeline/BackroadsModelPostprocessor.cs`:

```csharp
using UnityEditor; using UnityEngine;

public class BackroadsModelPostprocessor : AssetPostprocessor
{
    const string VehicleRoot = "Assets/Art/Vehicles/";
    const string MatRoot = "Assets/Art/Materials/Vehicle/";
    static readonly string[] Slots = { "M_Paint", "M_Trim", "M_Glass", "M_Lights", "M_Tyre", "M_Rim",
                                       "M_Interior", "M_Chassis", "M_Decal", "M_VehicleFar" };
    public override uint GetVersion() => 1;            // bump whenever this file changes

    void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(VehicleRoot)) return;
        var mi = (ModelImporter)assetImporter;
        mi.useFileScale = true; mi.globalScale = 1f;
        mi.bakeAxisConversion = false;                   // Blender already applied the transform (see the canary test)
        mi.importCameras = false; mi.importLights = false;
        mi.importBlendShapes = true; mi.animationType = ModelImporterAnimationType.None;
        mi.importNormals = ModelImporterNormals.Import;
        mi.importTangents = ModelImporterTangents.CalculateMikk;
        mi.meshCompression = ModelImporterMeshCompression.Off;
        mi.isReadable = assetPath.Contains("/VH_");      // runtime dent deformation needs CPU-side vertices
        foreach (var s in Slots) {
            var m = AssetDatabase.LoadAssetAtPath<Material>(MatRoot + s + ".mat");
            if (m) mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), s), m);
        }
    }

    void OnPostprocessGameObjectWithUserProperties(GameObject go, string[] names, object[] values)
    {
        if (!assetPath.StartsWith(VehicleRoot)) return;
        var p = go.AddComponent<BrNodeProps>();          // small runtime component (runtime assembly, not Editor)
        p.Set(names, values);
    }

    void OnPostprocessModel(GameObject root)
    {
        if (!assetPath.StartsWith(VehicleRoot)) return;
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) {
            if (t.name.StartsWith("UCX_")) {               // convex collision hulls
                var mf = t.GetComponent<MeshFilter>();
                var mc = t.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh; mc.convex = true;
                Object.DestroyImmediate(t.GetComponent<MeshRenderer>()); Object.DestroyImmediate(mf);
            }
        }
    }
}
```

A separate `VehiclePrefabBuilder` (an editor menu item, run after import):
- Creates or updates a **generated base prefab** `VH_Pickup_A.prefab`. It adds the `VehicleVisual` component (wheel sockets, light sockets, paint renderers, moving parts), wheel instances and light components.
- The user's hand tweaks live in a **Prefab Variant** `VH_Pickup_A_Game.prefab`, so regenerating never overwrites them.

---

## 4. Naming conventions

### 4.1 Files and folders

```
D:\Backroads\art\vehicles\<id>\<id>.blend      source after hand tuning (Git LFS)
D:\Backroads\art\vehicles\<id>\brief.md        design brief: fictional brand, inspirations, deliberate deviations, dimensions
D:\Backroads\art\trims\TS_Vehicle_A.blend      trim sheet source; textures are baked from it by script
D:\Backroads-ref\vehicle-refs\<id>\            reference photos: OUTSIDE the repo, never committed
<Unity>\Assets\Art\Vehicles\<Id>\VH_<Class>_<Variant>.fbx (+ .vehicle.json, Textures\T_*.png)
<Unity>\Assets\Art\Wheels\WH_<Style>_<Size>.fbx
<Unity>\Assets\Art\Materials\Vehicle\M_*.mat   <Unity>\Assets\Art\Shaders\SG_*.shadergraph
<Unity>\Assets\Synty\<Pack>\                   gitignored (section 10.5; where the packs import themselves)
```

Asset ids are **fictional and generic**: `VH_Pickup_MidA`, `VH_Truck_Box75`, `VH_Rally_CoupeA`. Never use real model names, even inside the project: dataminers read file names.

### 4.2 Objects inside a vehicle FBX

| Name | Meaning | Unity result |
|---|---|---|
| `VH_<...>` root empty | Origin on the ground under the vehicle centre, +Z forward (as in `js/vehicle.js`) | Prefab root |
| `Body_LOD0` to `Body_LOD3`, `Glass_LOD0..2`, `Interior_LOD0..1` | Renderers per LOD. Same-suffix siblings join one LOD level. | LOD Group, created automatically |
| `SOCKET_Wheel_FL`, `_FR`, `_RL`, `_RR`, `_RL2`, `_RR2` (dual or extra axles) | Hub centres. Custom props `radius`, `width`, `steer`, `drive`. | Empty transforms that `VehicleVisual` reads |
| `SOCKET_Light_Head_L/R`, `_Brake_L/R`, `_Rev_L/R`, `_Ind_FL/FR/RL/RR`, `_Beacon` | Where real URP lights and flares go. -Z of the socket points along the beam. | Lights added by the prefab builder |
| `SOCKET_Exhaust_0..n`, `SOCKET_Seat_Driver`, `SOCKET_Cam_Hood`, `SOCKET_Cam_Cockpit`, `SOCKET_Winch`, `SOCKET_Hitch`, `SOCKET_BodyMount` (trucks) | Particles, engine-sim audio source, cameras, winch, trailers, swappable truck bodies | Transforms |
| `PART_Door_FL` (and so on), `PART_Hood`, `PART_Tailgate`, `PART_Blade`, `PART_SteeringWheel`, `PART_Needle_RPM`, `PART_Needle_Speed`, `PART_Caliper_FL` | Moving parts, with the pivot at the hinge. Custom props `axis` (for example `x`), `min`, `max` in degrees. | Animated by script. The RPM needle is driven by engine-sim. |
| `DMG_Bumper_F`, `DMG_Bumper_R`, `DMG_Mirror_L`... | Parts that can be knocked off. Optional `..._Broken` variant. | Swapped or detached on impact |
| `UCX_Body_00..n` | Convex collision hulls (Unreal's convention, kept because tools understand it) | MeshCollider (convex), renderer removed |
| `TrackL`, `TrackR` | Dozer belts. UV.y runs along the belt (already true in `track_belt()`). | Shader scrolls `_TreadOffset` |

Wheels are separate assets (`WH_Offroad_A_33in.fbx` containing `Tyre_LOD0..2` and `Rim_LOD0..2`):
- Pivot at the hub, axle along X, outer face towards +X.
- Left-side wheels are **mirrored** (scale X -1), not rotated, so directional tread keeps its direction.
- Separate tyre and rim meshes allow Motor Town-style swaps and a painted rim.

### 4.3 Materials and textures

- **Material slots (Blender names = Unity material names):** `M_Paint`, `M_Trim` (the trim sheet with its palette strip), `M_Glass`, `M_Lights`, `M_Tyre`, `M_Rim`, `M_Interior`, `M_Chassis`, `M_Decal` (plates, cut-out stickers), `M_VehicleFar` (the single material for LOD2 and LOD3).
- **Porting the current names:** `vehicles.py` keeps its old names during the port and a `SLOT_MAP` dict folds them:
  - `Paint`/`Stripe`/`Livery*` go to `M_Paint` zones.
  - `Trim`/`Grille`/`Chrome`/`Steel`/`Alloy`/`Plate`/`Under`/`Bedliner`/`Mirror` go to `M_Trim` rectangles.
  - `Headlight`/`Taillight`/`Reverse`/`Indicator` go to `M_Lights` vertex-colour channels.
  - `Rubber`/`Tread` go to `M_Tyre`.
  - `Seat`/`Interior` go to `M_Interior`.
- **Texture names:** `T_<Asset>_<Map>`:
  - `_N` normal (BC5, linear)
  - `_MASK` (BC7, **sRGB off**): body MASK channels are R AO, G edge/curvature, B cavity ("dirt catch"), A paint zone
  - `_ORMA` for trim-sheet textures: R AO, G smoothness, B metallic, A emission mask
  - `_C` colour (BC7, sRGB)
  - `_Livery` colour with alpha (in the UV1 space described below)
- **UV channels:** UV0 for bake and textures. UV1 is the livery layout (planar side projection, section 6.6). UV2 is optional, for mud splats and damage.

### 4.4 Sidecar `<id>.vehicle.json`

Blender writes facts that physics and gameplay need: wheel sockets in **Unity space**, tyre radius and width, bounding box, socket list, tri counts per LOD and texture sizes. A small ScriptedImporter turns it into a `VehicleDefinition` ScriptableObject, so the Unity `DEFS` equivalent and the mesh can never drift apart. Today they live in two places: `js/vehicle.js` and `place_wheels_for_preview()`.

---

## 5. Automated pipeline

```
generator (vehicles.py builders)  ->  <id>.blend (cage + parts; the user may hand-tune it)
      -> br_build.py <id>: validate -> hi-poly -> LOD0..3 -> UVs -> bake -> FBX + GLB + JSON + PNG
      -> copy into Unity Assets/Art/Vehicles/<Id>/  -> Unity reimport -> post-processor -> prefab builder
      -> turntable capture (Blender EEVEE and Unity batchmode) -> review sheet PNG
```

### 5.1 Scripts to add (under `tools/blender/pipeline/`)

| File | What it does |
|---|---|
| `br_common.py` | Naming rules, `SLOT_MAP`, budgets per class, the game-to-Blender-to-Unity transforms, argument parsing (move the shared `arg()` and `lin()` here) |
| `br_generate.py` | Calls the existing `build_*()` functions but **saves a `.blend`** instead of exporting, and records sockets. Wrap `box(d, 'Headlight', ...)` and similar calls so that every lamp also drops a `SOCKET_Light_*` empty at its position. Take wheel sockets from the numbers that are now in `place_wheels_for_preview()`. |
| `br_uv.py` | UV0: seams from sharp edges plus edges tagged `panel`. Then `bpy.ops.uv.unwrap(method='ANGLE_BASED', margin=0.002)` and `bpy.ops.uv.pack_islands(margin=0.004, rotate=True)`. Trim-sheet parts get UVs **assigned** to rectangles from `trim_sheet.json`, which a generator can do exactly. UV1 is the livery projection. |
| `br_bake.py` | The hi-to-low bakes in section 7 |
| `br_lod.py` | Builds LOD0 to LOD3 (section 8.5) and merges materials for LOD2 and LOD3 |
| `br_validate.py` | Fails the build on: a missing or unknown name; tris over budget per LOD; more than the allowed materials; missing UV0; n-gons after triangulation; non-unit scale or unapplied rotation; wheel radius more than 2 percent away from `vehicle.json`; texel density below target. Writes `build_report.json`. |
| `br_export.py` | FBX (section 3.2), GLB (the current call with `export_extras=True`), JSON, PNG |
| `tools/build-vehicle.bat <id>` | Same style as the existing `build-vehicles.bat`: `"%LOCALAPPDATA%\Programs\Blender Foundation\Blender 4.5\blender.exe" -b <id>.blend --factory-startup --python tools\blender\pipeline\br_build.py -- --id <id> --unity <UnityProject>` |

On the Unity side, a menu item **Backroads > Art > Rebuild Vehicle...** runs the same Blender command through `System.Diagnostics.Process` and then calls `AssetDatabase.Refresh()`.
- Bakes use Cycles on the RTX 5080. Blender 4.4 and later support RTX 50 (Blackwell). Use OptiX and check `bpy.context.preferences.addons['cycles'].preferences.get_devices()`.
- Bakes run headless, about 10 to 60 s per 2048 map.

### 5.2 Review loop for a non-artist and an AI agent

The agent cannot see the Blender viewport. So each build also:
1. Renders a 6-view EEVEE sheet (front, rear, side, 3/4 front, 3/4 rear, top). Reuse `render_preview()` and the turntable idea from `D:\RaceMockups\scripts\`.
2. Renders an **ortho side view at 50 percent opacity over the reference photo**, so proportions can be checked against the brief.
3. Captures a Unity batchmode screenshot of the prefab in a lighting test scene (noon, dusk, night, with mud at 0, 0.5 and 1). Use `-executeMethod ArtPipeline.CaptureTurntable`.

The user comments on the sheets ("hood higher", "wheels bigger"), and the agent edits the profile JSON or the generator and rebuilds.

---

## 6. Materials and shaders (URP Shader Graph)

### 6.1 `SG_CarPaint` (URP Lit target, Metallic workflow, **Clear Coat** on in Graph Settings)

- **Inputs:**
  - `_Color1`, `_Color2`, `_Color3`, picked by the MASK.A zone value (0, 0.5, 1)
  - `_Metallic` (0 to 0.6)
  - `_Smoothness` (about 0.55)
  - `_FlakeNormal`: tiling noise normal in object space, about 40 tiles per m. Strength 0 to 0.15, masked by the coat.
  - `_LiveryTex` (UV1, alpha over paint)
  - the `SG_MudLayer` outputs (section 6.5)
  - `_DamageMask`
- **Outputs:**
  - Base Color, Normal (body `_N` blended with flakes)
  - Metallic times (1 - mud)
  - Smoothness, lerped to mud smoothness
  - Occlusion from MASK.R
  - **Coat Mask** = (1 - mud) times (1 - damage), **Coat Smoothness** 0.95
- **Per-vehicle colours:** use material instances (`renderer.material`). Avoid MaterialPropertyBlocks here: they break SRP Batcher compatibility and are not supported by the GPU Resident Drawer.

### 6.2 `SG_VehicleTrim`

Samples the shared `TS_Vehicle_A` trim sheet (`_C`, `_N`, `_ORMA`) and adds `SG_MudLayer`. Proposed 2048x2048 layout, with rows 256 px tall:

1. Rubber seals and weatherstrips
2. Grille meshes (hex, slats, wire)
3. Tread blocks and sidewall lettering (a **fictional** tyre brand)
4. Vents and louvres
5. Lamp reflector and lens patterns
6. Bolts, rivets, chequer plate
7. Chrome and brushed strips
8. **Palette strip**: 128 flat swatches of 64x64 px

Every trim texture is generated in Blender by script (geometry or procedural shader, baked onto a plane). That keeps them 100 percent ours and reproducible. Extra tiling textures can come from CC0 libraries such as Poly Haven and ambientCG.

### 6.3 `SG_VehicleGlass`

- URP Lit, Surface **Transparent**, Blend Alpha, **Preserve Specular Lighting** on.
- Smoothness 0.95, base alpha 0.3 (today's game uses 0.36 to 0.38), tint about #0B1116.
- Mud and dust raise alpha and roughness through the same subgraph.
- Windscreen only: a wiper-arc mask in UV0 cleans mud when wipers run.
- The interior is opaque and dark (the existing `inner_shell()` trick). LOD2 and LOD3 make the glass opaque dark tint inside `M_VehicleFar`.

### 6.4 `SG_VehicleLights` (one material for all lamps)

The generator already knows which faces are which lamp. It writes a FLOAT_COLOR attribute: R headlight/DRL, G tail/brake, B indicator, A reverse.
- Emission = sum of lamp colour times `_Intensity[k]` times channel. Use HDR values, for example DRL 4, head 20, tail 2, brake 8, indicator blinking 0 to 10, so Bloom picks them up.
- A reflector normal from trim row 5 adds lens detail.
- **Thin LED light signatures** (DRL strips, full-width light bars) are cheap emissive strips. They are the single strongest "modern vehicle" cue a generator can make.

### 6.5 `SG_MudLayer` subgraph (a port of `dirtify()`)

Custom Function node (File mode), `MudMask_float`:

```hlsl
// Mirrors js/vehicleModels.js dirtify(): mud climbs from the sills as Dirt rises.
void MudMask_float(float3 ObjPos, float Dirt, float Full, float Noise, float Cavity, float Splat, out float Mask)
{
    float reach = Full > 0.5 ? 1.0 : 1.0 - smoothstep(0.25 + Dirt * 0.9, 0.55 + Dirt * 1.5, ObjPos.y);
    float m = Dirt * 1.6 * reach * (0.4 + Noise * 0.95) - (1.0 - Dirt) * 0.2;
    Mask = saturate(max(m + Cavity * Dirt * 0.5, Splat));
}
```

- **Noise:** sample a tileable noise texture at `ObjPos.xz * 0.9 + ObjPos.y * 0.6`, the same mapping as today. This works because the vehicle pivot stays on the ground (y = 0 at the tyre contact), as in `js/vehicle.js`.
- **Outputs:**
  - Albedo lerps to dry mud (linear 0.085, 0.062, 0.042, or a triplanar `T_Mud_Dry_C`) and wet mud (darker).
  - Smoothness goes to 0.12 when dry and up to 0.55 when wet.
  - Metallic times (1 - m), coat times (1 - m).
  - Normal is blended with a triplanar mud normal using Normal Blend.
- **`Full = 1`** for tyres and wheels, as today.
- **`_Wet`** decays over time after leaving water or bog. Shared global `_RainWetness` and `_Snow` parameters are set from script.
- **Phase 2:** **`Splat`** reads a per-vehicle 512x512 R8 RenderTexture in UV2. A small script stamps mud blobs into it where wheel spray hits (behind each wheel, lower rear surfaces), so mud builds up where it should. `jobs.js`-style washing fades it. Use Unity's "Generate Lightmap UVs" or a Blender lightmap pack for UV2.
- Use the same subgraph in a `SG_BR_SyntyLit` variant for environment props near work sites (section 10.6).

### 6.6 Livery and decals

- **UV1 "livery space"** is generated by `br_uv.py` as planar projections:
  - faces with normal.x > 0.5 go to a left-side island, projected on (z, y)
  - faces with normal.x < -0.5 go to a right-side island, projected on (z, y) with u flipped so text reads front-to-back on both sides
  - everything else goes to a top/front/rear island
- **Templates:** Blender renders `<id>_livery_template.png` showing panel outlines in UV1. The user paints liveries in Krita or GIMP as simple side views.
- **Runtime:** a `LiveryCompositor` blits layers (base pattern, stripes, number, sponsor stickers, each with position, rotation, scale and colour in UV1) into a 2048 RenderTexture once on spawn or edit. AI traffic variants can be baked to PNG.
- **Race numbers and sponsor stickers** must be fictional. That applies equally to tyre, fuel and oil brands.
- **Number plates:** a fictional plate format, with the text rendered to a small RT.
- **URP Decal Projectors** (Decal Renderer Feature, Technique Automatic, Rendering Layers on) are for the **world**: road markings at the new intersections (stop lines, crosswalks, arrows), mud splats and skid marks on asphalt. Exclude vehicles with decal layers.

### 6.7 Damage

- **Recommended:** runtime vertex deformation of the readable `Body_LOD0` mesh in a Burst job on impact. This keeps a MeshRenderer, so the SRP Batcher and the GPU Resident Drawer still work.
- **Avoid blend-shape dents:** they force SkinnedMeshRenderer.
- **Scratches:** the `_DamageMask` (world hit positions or UV2 RT) reveals primer and bare metal from the palette, and kills the clear coat.
- Detachable `DMG_*` parts get a rigidbody when they break off.

### 6.8 Environment shaders from the existing generators

- **Buildings:** recolour through material instances, as now. Replace per-pane `Glass` objects with one glass mesh per building. A per-pane random ID goes in vertex colour, and the shader lights a subset of windows by a time-of-day threshold, so you get the same look with no GameObject explosion.
- **Trees:** URP Lit Shader Graph reading `COLOR_0`, plus vertex wind sway weighted by height. Use LOD cross-fade, enabled in the URP asset and the Shader Graph.

---

## 7. Texture baking

| Map | How, in Blender 4.5 (scripted, Cycles) | Settings |
|---|---|---|
| Normal (hi to low) | `scene.render.bake.use_selected_to_active = True`, `cage_extrusion = 0.02`, `max_ray_distance = 0.05`, `bpy.ops.object.bake(type='NORMAL', normal_space='TANGENT')` | Hi-poly = cage at subdiv 3, with panel grooves and bolted details. Margin 16 px, `margin_type='EXTEND'`. |
| AO | `bpy.ops.object.bake(type='AO')` | AO distance (`world.light_settings.distance`) 0.4 to 0.6 m, 64 to 128 samples. Bake with the **wheels in place** (as preview instances) so arches darken correctly, then hide them. |
| Edge / curvature | Temporary emission material: `1 - dot(Bevel node normal, Geometry normal)`, scaled. Bake `type='EMIT'`. | Bevel radius 0.01 to 0.02 m. Drives edge wear and paint chips. |
| Cavity ("dirt catch") | Same trick inverted, multiplied by AO | Feeds MASK.B. Mud collects in creases first. |
| Vertex-colour masks (no UVs needed) | `bake_type` with `target='VERTEX_COLORS'` onto a FLOAT_COLOR attribute | Good for LOD2 and LOD3 and for small parts |

- **Panel lines:**
  - In the cage, edges carry a boolean attribute `panel`.
  - For the hi-poly only, `br_bake.py` runs bevel plus inset and pushes those loops inward 4 to 6 mm to make grooves.
  - That bakes into the LOD0 normal map.
  - The low-poly stays clean. This is how modern-looking shut lines get in cheaply.
- **Crease and bevel data:** Blender 4.x stores crease and bevel weight as generic attributes (`crease_edge`, `bevel_weight_edge`). The generator can write them directly, and Subdivision and Bevel (Limit Method: Weight) use them. Finish the low-poly with a **Weighted Normal** modifier (Keep Sharp) for hard-surface shading.
- **Texel density:**
  - Player vehicles: at least 400 px/m (a 2048 map over about 18 to 25 m² of hatch surface gives roughly 380 to 460 px/m after packing).
  - Traffic: 200 px/m (1024 maps).
- **Unity import:** `_N` as Normal map (BC5). `_MASK` and `_ORMA` linear BC7. Colour sRGB BC7. Mip streaming on.

---

## 8. Procedural Python vs hand modelling, and a workflow that works

### 8.1 How far procedural gets

| Vehicle family | Procedural only (station lofts and primitives, as now) | Plus a user hand-tuning pass on the cage | Notes |
|---|---|---|---|
| Box trucks, cab-overs, semis, tippers, tankers | **High.** Mostly planar, with modular bodies. | Very high | Best first targets. A modular chassis, cab and body kit gives "varying trucks" cheaply. |
| Construction (dozer, loader, excavator), tractors | High | Very high | Hydraulics, tracks and lights are all parametric |
| Pickups, SUVs, vans, 4x4 / 6x6 offroad | Medium | High | Boxy, but modern ones have flared arches and character lines that need cage tuning |
| Hatchbacks and sedans | Low to medium (looks "2005 generic") | Medium to high | Compound curvature and greenhouse transitions are hard |
| Race and sports cars, open-wheelers | Low | Medium | Aero surfaces are parametric (wings, splitters, diffusers). The body is not. Liveries do much of the work. |
| Wheels, tyres, rims, calipers, lamps, grilles, light bars, roof racks, snorkels, winches, interiors | **Very high** | n/a | Make a reusable parts library. Today's `wheel()`, `mirror_pair()` and `interior()` are the seeds. |

**Why the current bodies look dated:** too few stations and section points (9 to 12 stations by 11 points), one generic section shape (`section()`), no character lines, no panel gaps, small wheels relative to the body, and blocky lamp boxes.

**Modern cues a generator *can* make:**
- large wheels with short overhangs
- thin LED DRL and full-width tail strips
- flush, tinted glass with gloss-black pillars and roof
- big patterned grilles (from the trim sheet)
- crisp shoulder lines (one creased edge loop)
- black wheel-arch cladding on offroaders
- visible coloured brake calipers and multi-spoke rims

### 8.2 Practical workflow per vehicle (user plus AI agent)

1. **Brief (agent, 30 min).** `brief.md` records:
   - class
   - **fictional** brand and model name
   - 2 to 4 real inspirations of the same class, chosen by the user
   - dimensions from published spec sheets (wheelbase, length, width, height, track, ground clearance, tyre size; for example 265/70R17 gives r = 0.381 m). Dimensions are facts and free to use.
   - cues to borrow from each inspiration
   - a "do not copy" list: badge, grille signature, lamp signature, any iconic silhouette
2. **Reference setup (script).** `ref_setup.py` creates ortho image empties (`empty_display_type='IMAGE'`) for side, front, rear and top. They are scaled so the photo's wheelbase equals the spec wheelbase, plus a proportion rig: ground line, wheel circles at the socket positions, height box.
3. **Cage (script).** `cage_from_profiles.py` lofts a **quad cage** from a profile JSON. The profile holds the side silhouette (roof line, belt line, sill line), the plan view (half-width per station) and 3 to 5 section shapes (tumblehome), with 16 to 24 stations by 16 to 24 points. This is today's `loft()`, with more resolution and separate panels (hood, doors, fenders, bumpers). Crease and bevel weights and `panel` flags are written per edge.
4. **Hand tuning (user, 1 to 3 h).**
   - In Blender, move cage vertices with Proportional Editing (O) against the references.
   - Add or remove creases (Shift+E) and bevel weights.
   - Optionally move part empties (mirrors, lamps).
   - Rules: only **move** vertices, never delete or retopologise, so the scripts keep working.
5. **Build (script).** `build-vehicle.bat <id>` runs the steps in section 5 to give FBX and review sheets.
6. **Review and iterate.** Use the overlay render (5.2). Aim for "same class and attitude", **not** "same car" (section 9).

Optional tools, if you ever want more control:
- **Plasticity** (NURBS modelling for artists; paid indie licence) exports clean hard-surface panels to Blender.
- Commission a freelancer for a few hero bodies while the generator handles parts, LODs and bakes.
- Image-to-3D AI tools give poor topology for hard-surface cars today. At most, use one as a blockout reference, and check its licence and territorial restrictions first.

### 8.3 Reference photos ("real pictures of modern vehicles")

- **Collect** 6 to 12 images per vehicle (side, front, rear, 3/4 views, interior, wheels) into `D:\Backroads-ref\vehicle-refs\<id>\` with `sources.txt` listing the URLs. Good places to look:
  - manufacturer press and media sites (editorial terms)
  - Wikimedia Commons (CC licences, and handy for side profiles)
  - your own photos at car parks and dealers, which are the safest of all
- **Private reference viewing is normal practice.** Do not ship the photos, do not commit them to any repo, and never project a photo onto the model as a texture.
- Downloading images is a step the user approves case by case. Agents should not bulk-scrape.
- AI-generated concept images for moodboards are fine, but they often reproduce real grilles and badges. Run them through the same "do not copy" check.

### 8.4 Poly and texture budgets per class (LOD0 includes wheels and interior)

| Class | LOD0 total | Wheel each (LOD0, LOD1) | LOD1 | LOD2 | LOD3 | Materials at LOD0 | Body textures |
|---|---|---|---|---|---|---|---|
| Hatch or sedan (player) | 50 to 70k | 3 to 5k, 0.8k | 15 to 20k | 4 to 6k | 0.8 to 1.2k | 8 or fewer | 2048 `_N` + `_MASK` |
| Pickup or SUV (offroad, underbody visible) | 70 to 100k | 5 to 9k (lug tread), 1k | 20 to 30k | 5 to 8k | 1 to 1.5k | 9 or fewer | 2048 |
| Rally, touring, race car | 60 to 90k | 3 to 5k, 0.8k | 20 to 25k | 5 to 7k | 1k | 9 or fewer | 2048 + 2048 livery |
| Box truck or semi tractor | 90 to 150k | 4 to 6k (x6 to x10), 1k | 30 to 40k | 8 to 12k | 1.5 to 2k | 9 or fewer | 2048 (+1024 per body module) |
| Heavy 6x6 or 8x8 offroad truck | 130 to 180k | 8 to 12k, 1.5k | 35 to 50k | 10 to 15k | 2k | 9 or fewer | 2048 |
| Construction (dozer, loader) | 50 to 80k (tracks 4 to 8k) | n/a | 15 to 25k | 5k | 1k | 8 or fewer | 2048 |
| AI-traffic-only car | 15 to 25k | 1 to 2k, 0.3k | 6 to 8k | 2 to 3k | 0.5k | 6 or fewer | 1024 |

- These are targets for a mid-range PC. Our own current bodies are about 20k at LOD0 and about 3k at LOD1.
- Synty POLYGON vehicles are much lighter (typically low thousands of triangles). Check them in the Statistics or Rendering Debugger after import.
- Draw calls: LOD0 10 or fewer submeshes, LOD1 6 or fewer, LOD2 and LOD3 1 or 2.

### 8.5 LOD generation

- **LOD0:** cage at subdiv 2 with bevels and all details.
- **LOD1:** **the same cage at subdiv 1.** It shares UV0, so it uses the **same textures**. Parts tagged with custom prop `lod_max = 0` are dropped (badges, wipers, small bolts), and the interior is simplified.
- **LOD2:** cage at subdiv 0 plus Decimate (COLLAPSE). No interior. One material, `M_VehicleFar`: paint colour from an instance parameter, trim from palette UVs, glass opaque dark, lamps as vertex colour.
- **LOD3:** about 1k triangles (decimated LOD2, or a scripted "shell").
- **LOD Group transitions** (screen-relative height): LOD0 0.30 or more, LOD1 0.12, LOD2 0.04, LOD3 0.01, culled below that (traffic only). The player vehicle is held at LOD0 with `LODGroup.ForceLOD(0)`. Turn on LOD cross-fade.
- Subdivision-level LODs keep silhouettes far better than today's 0.14 collapse decimation.
- **Unity Mesh LOD** ("Generate mesh LODs", Unity 6.2 and later, stored in the mesh's index buffer) is for static props and buildings only:
  - It does not combine well with LOD Group.
  - It keeps materials the same.
  - It ignores skin weights.
  - It needs the GPU Resident Drawer for cross-fade.

---

## 9. Legal: vehicles "inspired by" real models in a free online game

Not legal advice. A free game is still distributed publicly and worldwide. Trademark and design law does not require you to make money to be liable, although damages tend to be lower.

**What can bite:**
1. **Trademarks.** Brand names, **model names**, logos, badges and hood ornaments. Several distinctive design features are themselves registered trademarks, for example Jeep's seven-slot grille.
2. **Trade dress.** The overall look of an iconic vehicle.
   - GM v. Lanard Toys (6th Cir. 2006): Hummer-like toy vehicles infringed the Hummer trade dress, grille included.
   - The ITC's 2020 Mahindra Roxor decision: the original Roxor infringed Jeep trade dress, but not the grille trademark. A redesigned grille and body later cleared it.
3. **Design rights.**
   - EU and UK registered designs last up to 25 years, and EU unregistered designs 3 years.
   - The **EU design reform in force from 1 May 2025** (Regulation 2024/2822) widened "product" to non-physical products, including items in video games and virtual worlds. Virtual copies of protected car designs are now clearly in scope in the EU.
   - US design patents protect body designs for 15 years.
4. **Copyright in applied art** in some EU countries, plus manufacturers' rights in their own **photos and blueprints**.

**Defences exist but are not a plan.**
- AM General v. Activision (S.D.N.Y. 2020) held that Humvees in *Call of Duty* were protected expressive use under the Rogers test.
- The US Supreme Court's *Jack Daniel's v. VIP* (2023) says Rogers does not apply when a mark is used as a source identifier. So naming or selling an in-game vehicle under a real mark, or selling it as a cosmetic, is much weaker ground.
- A hobby developer should not plan on litigating.

**How games handle it.** BeamNG.drive uses fictional brands (Gavril, ETK, Ibishu, Hirochi...) because licensing is expensive. Its cars blend several real vehicles each. GTA does the same with parody brands. Licensed games (Forza and others) pay for and follow manufacturer approval rules.

**Safe practices for Backroads:**
1. **Fictional brands and model names everywhere:** in-game UI, file and asset names, code identifiers, store pages, tags and trailers. Check names against USPTO Trademark Search, EUIPO eSearch / TMview and the WIPO Global Brand Database in classes 12 (vehicles), 9 and 41 (games).
2. **Rename the current vehicles:**
   - "Ranger 4x4" (`js/vehicle.js`, pickup): Ranger is Ford's pickup name.
   - "Muncher **D3**": D3 is a Caterpillar dozer model designation.
   - "Sprint RS" and "Hauler 7.5t" look low-risk, but run them through the same check.
3. **No badges, logos or emblems** from real makers, even blurred or "nearly". Design our own badges.
4. **Avoid signature elements:** seven-slot grille with round lamps, kidney grilles, Rolls-Royce-style temple grilles, Porsche 911 / Beetle / Hummer H1 / Defender / G-Class / classic Mini silhouettes, and distinctive lamp signatures. The more iconic the car, the higher the risk.
5. **Mix three or more inspirations per vehicle,** change key proportions, and design our own lamp signature and grille. Generic class forms (double-cab pickup, cab-over box truck, 6x6 offroad truck) are functional and free to use.
6. **Gut check:** if a player would name the real model at a glance with no badge, change it. Record inspirations and deliberate deviations in `brief.md`, which helps show good faith.
7. **Never ship or commit reference photos or blueprints,** and never use them as textures.
8. **Same rules for sponsors, tyres, fuel, oil and tools** on liveries and props.
9. **Online and user content:** the terms of service forbid real logos in user liveries, and there is a report-and-remove path. If the game ever gets livery sharing, add moderation.
10. **Keep vehicles data-driven** so any single model can be swapped quickly if you ever get a complaint.
11. If the game ever charges for anything (cosmetics, premium), get a short consultation with an IP lawyer before launch.

---

## 10. Synty POLYGON packs

### 10.1 Your Synty login

**Please don't send your Synty login to an agent, and no agent should use it.** Signing in on your behalf is not something we do, and Synty's terms also tie each seat to a person and treat shared credentials as grounds for suspension. Instead:
1. You sign in at syntystore.com yourself and download the packs you choose. Get the **Unity package** and, optionally, the "FBX Source Files" zip.
2. Put them in `D:\SyntyPacks\<PackName>\`, outside the repo.
3. Say "the Town and Construction packs are in D:\SyntyPacks". From there an agent can import them into a probe Unity project:
   - `Unity.exe -batchmode -projectPath <probe> -importPackage D:\SyntyPacks\...\<pack>.unitypackage -quit`
   - then run the URP extract or converter (10.3)
   - then build the editor tooling and scenes that place the prefabs, within the limits in 10.4.

### 10.2 Packs that suit Backroads

Prices are from Synty Store product pages on 2026-10-18. Several pages showed "Sold out" text; check before buying. Each one-time licence covers 5 seats.

| Pack | Price | What we'd use | Vehicles included | Engine and pipeline (from page) |
|---|---|---|---|---|
| **POLYGON Town** | $49.99 | Millbrook dressing: modular house kit, shops, 412 props, road and pavement tiles | 8 vehicles x 4 colours (school bus, bus, firetruck, pickup, truck, delivery truck, rubbish truck...) | Unity 2022.3+, URP and Built-in. Unity pkg 1.9.2, source v7, FBX source included. |
| **POLYGON City** | $19.99 | Traffic placeholders, modular road set, street props | 8 vehicles x 12 variants (sedan, taxi, police, ambulance, small car, 4x4, van, muscle car) | 2022.3+, URP and Built-in. Unity 1.12.5, FBX included. |
| **POLYGON Farm** | $49.99 | Farm work sites | 11 vehicles (harvester, pickup, modern and old tractors, trailers) | 2022.3+, URP and Built-in. Unity 1.7.3, FBX included. |
| **POLYGON Construction** | $49.99 | Quarry and construction work sites (550+ prefabs) | Bulldozer, cherry picker, crane, 2 excavators, 3 loaders, pickup, roller, telehandler, trailers, truck with mixer and 3 dump trays | 2022.3+. Page doesn't state the pipeline; changelog says shaders were converted to Shader Graph. **Test it.** |
| **POLYGON Meadow Forest** (Nature Biome) | $54.99 | Fields, forests, flowers | n/a | 2022.3+, URP and Built-in. "Unity 6 URP extract" added in 1.9.0. |
| **POLYGON Swamp Marshland** | $54.99 (on sale $27.50 when read) | Wet bog, reed beds, mud terrain materials | n/a | URP and Built-in. Unity 6 URP extract in 1.9.0. One user report of URP trouble in Unity 6, so **test it**. |
| **POLYGON Alpine Mountain** | (price not captured) | Background ranges, rocks, pines | n/a | 2022.3+, URP and Built-in |
| **POLYGON Pro Racer** | $199.99 | Race cars and a race venue (grandstands, tunnels, ramps, sandpits) | Open wheeler, stock car, touring, rally, dirt bike, kart, vintage, prototype, pace car, truck, trailer...; 9 with customisable parts, 16 liveries | 2022.3+, URP and Built-in. Unity 1.0.8. |
| SyntyPass (all 130+ packs) | $30/mo billed annually, or $40/mo monthly (3-month minimum); includes $10/month store credit per licence for one-time purchases | Trying packs before you settle | All | See the licence caveat below |

**Suggested first buy:** Town + Construction, plus Swamp Marshland if the bog matters. That is about $155 one-time. Add Farm and Pro Racer once the work sites and race events exist.

### 10.3 Unity 6 URP compatibility in practice

- Packs state "Unity 2022.3+" with URP and Built-in. Unity 6 projects are URP by default, so a straight import can show **pink materials**.
- The fix:
  1. Import the pack's bundled URP or "Unity 6 URP extract" package where there is one (it sits inside the pack folder).
  2. Otherwise use **Window > Rendering > Render Pipeline Converter** (Built-in to URP, Material Upgrade).
  3. Packs with custom shaders (vegetation wind and "frosting", water) need their own URP versions.
- Do this in a **throwaway probe project first.** An audit editor script records pink materials, console errors, tris per prefab, texture sizes and LOD presence for each pack, and writes `synty_audit.json`. Only the audit numbers are shared, not the files.
- Assets use Synty's own naming (for example `SM_Bld_*`, `SM_Prop_*`, `SM_Veh_*`; check in the import). Most share one or a few small **colour-palette atlas** textures: the UVs sit on colour swatches.
- Check whether pack vehicles have separate wheel and door meshes before using them as drivable placeholders.

### 10.4 Licence points that matter here (One Time Purchase Licence, version dated 9 July 2026)

Paraphrased; read the full text on syntystore.com.
- **Free and online games are fine.** A videogame is always a covered "Product". There is no engine restriction. Assets may be distributed **built into** the game. Modifying assets for your game is allowed, but you don't own the modified asset.
- **Source files stay inside your Team.** Contractors may use them while working for you and must delete them afterwards. No standalone redistribution, no stock or asset-store reuse.
  - So a modding SDK or a public repo must not contain Synty files.
  - Building "game creation software" or metaverse content needs a custom licence. Ask Synty before adding any level or UGC editor that exposes Synty assets.
- **Generative AI clause.** The licence forbids:
  - putting assets into datasets used by generative AI
  - **generating 3D models with generative AI programs** using the assets
  - uploading source files or models to third-party services for 3D model generation
  - using the assets in promotion related to generative AI
  - Its definition of generative AI is broad.
  - **Practical rule for this project** (set by the user on 2026-10-18, with model training off; PLAN §4.7 supersedes the stricter version first written here):
    - Agents may import, audit, look at, set up materials for and arrange Synty prefabs in Unity.
    - Agents never make or edit 3D models from Synty assets, and never merge Synty geometry into baked meshes.
    - No Synty file or render goes into image-to-3D or any AI generation tool, and no Synty file is committed.
    - Optional: ask Synty support in writing to confirm that an AI coding assistant working this way is fine. Suggested wording: "the assistant never generates or edits 3D models from your assets."
- **Subscription (SyntyPass, Standard Subscription Licence, also 9 July 2026).** When the subscription ends you may not use the assets in new work. Only minor bug fixes and maintenance are allowed, and substantial development needs an active subscription. For a free online game that keeps being updated, that is a trap, so **buy one-time licences for the packs you keep**. The subscription's monthly store credit can go toward them.
- **Unity Asset Store purchases** of Synty packs are governed by the Unity Asset Store EULA, not Synty's licence. Pick one storefront and keep the receipts.
- **Seats:** 5 per licence purchase, so a solo hobby project is covered.

### 10.5 Synty and the private git repo

- **A private repo that only you (and future team members within your seats) can access fits the "inside your Team" rule.**
- Recommended anyway:
  - Gitignore `Assets/Synty/` (where the packs import themselves) and commit `synty-manifest.json` (pack, version, storefront, local zip path).
  - Add an editor check that warns when a listed pack is missing.
  - Re-importing a `.unitypackage` restores the same GUIDs, so scene and prefab references survive. Any converted or overridden materials go in our own `Assets/Art/SyntyOverrides/`, which is committed.
- This keeps the repo small: packs are hundreds of MB to several GB. GitHub Free includes 10 GiB of LFS storage and 10 GiB of bandwidth per month. It also keeps the repo safe to open-source later.

### 10.6 Blending Synty with our own vehicles

1. **One lighting and post stack for everything** (section 2). That alone unifies most of the look.
2. **Matching palette and value range.** Build our own `BR_Palette` swatch strip (trim row 8) by sampling Synty's colours by eye in Unity. Our props then look Synty-compatible but are 100 percent ours. Don't UV our own assets onto Synty's atlas: that would make our files depend on Synty textures.
3. **Same edge language.** Chunky 1 to 3 cm bevels and weighted normals. No razor-sharp CAD edges and no photo textures.
4. **Shared weathering.** The same `SG_MudLayer` and wetness on our vehicles, on terrain, and (through `SG_BR_SyntyLit` override materials in our own folder) on Synty props at work sites. Swapping shaders on materials is modifying an asset, which the licence allows. Use user-run tools, and keep agents to scripts (10.4).
5. **Hero detail where the camera is.** Our drivable vehicles carry more polygons than Synty props (section 8.4). That reads as "hero object", not as a mismatch, as long as their shading stays low-frequency.
6. **Scale check:** measure Synty doors, kerbs and road widths against our real-scale vehicles in the probe before committing to a pack for Millbrook. Canon pad heights and footprints in `assets/towns/canon.json` stay authoritative, and Synty pieces must fit the lots, not the other way round.
7. **Use Synty vehicles as placeholders** for traffic and race grids, and replace them with our own as the pipeline matures. Synty cars cannot be put through AI tooling anyway.

---

## 11. Phased plan with "done when"

| Phase | Work | Done when |
|---|---|---|
| A. Probe (1 to 2 days) | Unity 6.3 URP probe project. Import current `hatch.glb` (glTFast) and a quick FBX of it. Write `BackroadsModelPostprocessor`, the axis canary and the test. | Canary test passes; LOD Group auto-built from `_LOD` names; materials remapped by name; scale 1, rotation 0 |
| B. Generator upgrade | Add `br_common`, `br_generate`, `br_uv`, `br_lod`, `br_validate`, `br_export` around `vehicles.py`. Add sockets, material consolidation, LOD0 to LOD3, JSON sidecar. Rename vehicles (section 9). | `build-vehicle.bat pickup` gives an FBX within budget, with 9 or fewer materials and a valid `build_report.json`, and Unity builds the prefab with no hand steps |
| C. Shaders | `SG_CarPaint`, `SG_VehicleTrim`, `SG_VehicleGlass`, `SG_VehicleLights`, `SG_Tyre`, `SG_MudLayer` (port of `dirtify`) | Lighting test scene (noon, dusk, night, mud 0 / 0.5 / 1) matches or beats the three.js look side by side |
| D. Bakes and trim sheet | `TS_Vehicle_A` generated in Blender; AO, normal, edge and cavity bakes for the pickup | Panel lines and AO visible; texel density at least 400 px/m; bake runs headless in under 5 min |
| E. First photo-inspired vehicle | Brief, references, cage, hand tune, build, for a modern mid-size double-cab pickup or a 6x6 truck | User sign-off on the review sheet; brief records 3+ inspirations and deviations |
| F. Synty probe | User downloads Town + Construction. Import, URP fix-up, audit, scale check. | `synty_audit.json` has no pink materials; decision recorded on what dresses Millbrook |
| G. Livery and race cars | UV1 livery projection, `LiveryCompositor`, fictional sponsor set, race car class | A rally car with 3 liveries and readable numbers on both sides |

---

## 12. Open questions and how to settle them

- **Exact FBX axis flags:** the canary test (3.2) settles them in minutes. Don't argue it from memory.
- **`AddRemap` inside `OnPreprocessModel` vs `OnAssignMaterialModel`:** both are documented hooks. Check in the probe which one remaps reliably on the first import in Unity 6.3, and keep that one.
- **Whether Synty vehicles have separate wheels, doors and pivots,** and their triangle counts: answered by the audit script in phase F.
- **Synty's view of AI coding assistants:** email Synty support. Keep the written answer next to `synty-manifest.json`.
- **Minimum-spec target:** budgets in 8.4 assume a mid-range PC. If the free online target includes laptops with integrated GPUs, cut the LOD0 totals about 40 percent and use 1024 body maps.
- **Whether Motor Town-style vehicle customisation** (parts and wheels) is a goal: it decides how far to push the modular socket system (wheels, bumpers, light bars, truck bodies).

---

## Sources

- glTFast 6.10 feature tables (extension and pipeline support): https://docs.unity3d.com/Packages/com.unity.cloud.gltfast@6.10/manual/features.html
- glTFast changelog (6.19.0, 2026-05-19, Unity 6.0 LTS minimum): https://github.com/Unity-Technologies/com.unity.cloud.gltfast/blob/main/Packages/com.unity.cloud.gltfast/CHANGELOG.md
- Unity Mesh LOD (6.2+) introduction and generation: https://docs.unity3d.com/6000.3/Documentation/Manual/lod/mesh-lod-introduction.html , https://docs.unity3d.com/6000.2/Documentation/Manual/lod/mesh-lod-generate-lods.html
- Unity LOD naming convention (`_LOD0`...): https://docs.unity3d.com/ru/2019.3/Manual/LevelOfDetail.html
- Unity AssetPostprocessor (6.3 LTS): https://docs.unity3d.com/ScriptReference/AssetPostprocessor
- Unity URP SSAO and Decal Shader Graph (6.3 LTS): https://docs.unity3d.com/Manual/urp/post-processing-ssao.html , https://docs.unity3d.com/Manual/urp/prebuilt-shader-graphs-urp-decal.html
- Unity render pipelines strategy for 2026: https://unity.com/topics/render-pipelines-strategy-for-2026 ; coverage: https://80.lv/articles/unity-unveils-2026-render-pipelines-strategy/
- Unity 6.3 LTS features (CG Channel, Dec 2025): https://www.cgchannel.com/2025/12/unity-6-3-lts-is-out-see-5-key-features-for-cg-artists/
- Blender 4.4 Cycles release notes (RTX 50 support): https://developer.blender.org/docs/release_notes/4.4/cycles/
- Synty licences: https://syntystore.com/pages/licences-overview , https://syntystore.com/pages/one-time-purchase-licence , https://syntystore.com/pages/standard-subscription-licence , SyntyPass: https://syntystore.com/products/syntypass , FAQ: https://syntystore.com/community/faq
- Synty packs: https://syntystore.com/products/polygon-town-pack , https://syntystore.com/products/polygon-city-pack , https://syntystore.com/products/polygon-farm-pack , https://syntystore.com/products/polygon-construction-pack , https://syntystore.com/products/polygon-meadow-forest-nature-biome , https://syntystore.com/products/polygon-swamp-marshland-nature-biome , https://syntystore.com/products/polygon-alpine-mountain-nature-biomes , https://syntystore.com/products/polygon-pro-racer , Unity 6 note on the starter pack page: https://syntystore.com/products/polygon-starter-pack
- Palette-atlas recolouring (Synty-style atlases): https://discussions.unity.com/t/released-palette-modifier-change-the-colors-of-low-poly-models-directly-inside-unity/760556
- GitHub LFS quotas: https://docs.github.com/en/billing/concepts/product-billing/git-lfs
- AM General v. Activision (2020): https://www.finnegan.com/en/insights/blogs/incontestable/in-legal-warfare-over-humvee-trademarks-the-first-amendment-goes-beyond-the-call-of-duty-in-dismissing-am-generals-claims.html , https://www.thedrive.com/tech/32872/you-have-a-first-amendment-right-to-humvees-in-call-of-duty-games-judge-rules
- Jack Daniel's v. VIP Products (2023): https://www.law.cornell.edu/supremecourt/text/22-148 , https://dwt.com/insights/2023/06/scotus-jack-daniels-trademark-rogers-test
- Jeep vs Mahindra Roxor (ITC 2020): https://www.thedrive.com/news/34061/jeep-wins-in-legal-fight-over-mahindra-roxor , https://en.wikipedia.org/wiki/Mahindra_Roxor
- GM v. Lanard Toys (6th Cir. 2006): https://caselaw.findlaw.com/court/us-6th-circuit/1073414.html
- EU design reform (Regulation 2024/2822, from 1 May 2025): https://www.hsfkramer.com/notes/ip/2025-05/eu-design-reform-begins-key-provisions-of-eu-regulation-2024-2822-effective-from-1-may , https://www.dreyfus.fr/en/2026/01/05/eu-design-protection-complete-guide-to-the-2025-reform/
- BeamNG fictional brands: https://www.wikipedia.org/wiki/BeamNG
- Local files read: `D:\Backroads\tools\blender\vehicles.py`, `buildings.py`, `trees.py`, `tools\build-*.bat`, `js\vehicleModels.js`, `js\vehicle.js`, `assets\*\*.glb` (headers only), `D:\RaceMockups\README.md` and the folder listing.
