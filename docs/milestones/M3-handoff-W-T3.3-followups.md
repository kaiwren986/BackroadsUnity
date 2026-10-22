# M3 handoff: lane W, card "T3.3 follow-ups" (lane S's sites in the world, lane V's physics layers)

Branch `m3/w-world-sites` in `D:\Backroads-Unity-wt\w`, cut from main at `54e5362` (T3.3 on main as `c6e2a8d`, with
sites-S4, lane A's doors and lane V's T3.6 test-world part). Commits: `e631d8f` (code and tests), `3bdbf0f` (WorldData
re-baked at `e631d8f`, bake v3), `8c3d30b` (`Millbrook.unity` rebuilt), `def9a8c` (the canopy drawn lit) and this
handoff. Lane W paths only. Nothing was pushed, installed, downloaded or signed into.

## In plain words

Lane S's lots are now part of the town. The ground under every parking lot, forecourt and driveway is graded to the
paving lane S designed, and lane R's driveway aprons cut through the verges where lane S asked for driveways. The
paving, kerbs and wheel stops themselves are drawn in the scene, and so are lane S's props (pumps, bins, signs: our own
stand-ins here, as the Synty packs are not imported in this worktree).

The fuel station's canopy is no longer a solid block: it is a roof on four posts, with lane S's forecourt and pumps
under it, and no building pad there pushing dirt over the paving. `t33f-fuel-canopy.jpg` in
`M3-handoff-W-T3.3-followups/` shows it; `t33f-shots-1200.jpg` is the 12 views at noon.

For the vehicles of lane V's new physics: every building, canopy post, fence, prop and tree is on the `Static` layer,
and the road and lot surfaces are on `Road`, so the chassis meets the town without changes on lane V's side.

## Done

| Request | What | Where |
|---|---|---|
| S 1, site plates | Lane S's `SitePlateSource` (source "sites") is registered in lane W's bake by a small W assembly, `Backroads.World.Editor.Sites` (`[InitializeOnLoadMethod]`; `Backroads.World.Editor` cannot reference lane S's editor assembly, which references it). Lane S's `Enabled` flag is untouched; flipping it later is harmless (the registry is by name). WorldData re-exported in this branch: **82,973 site plates** in ADR-007's Sites stage | `World/Editor/Sites/SitePlatesOn.cs` |
| S 2, apron plates | `DrivewaySource` reads `tools/world/sites/(town)/driveways.json`; `WorldBake` resolves all requests at once with `new DrivewayCutter(KerbFrontageSource.LoadTown(town)).Resolve(requests)` and hands cuts and cutter to `RoadPlateBuilder.Build`: **218 requests, 218 cuts, 862 apron plates**. They come with the registered sources, so a caller that brings its own site plates and aprons (lane S's `TerrainUnderPavedSurfaces`, `registeredSources = false`) gets neither twice. The manifest records the cuts, the requests and the driveways file's hash | `World/Editor/Bake/DrivewaySource.cs`, `WorldBake.cs`, `WorldDataExport.cs` |
| S 3, no pads under open roofs | `building=roof`, `canopy` or `carport` (`BuildingFootprint.OpenRoof`; in this town the fuel canopy) gets no pad: **265 pads** (266 before). `buildings.json` marks it `"open": true`; the importer builds it as a canopy (a 0.5 m slab over the footprint at 5 m for fuel, 2.6 m otherwise, on four 0.3 m posts 0.5 m inside the corners) with a collider per post (kind `post`, as the canon's) instead of one box | `TownSource.cs`, `TerrainSteps.cs` (`PadStep`), `WorldDataExtras.cs`, `WorldDataImporter.cs` |
| S 4, scene | The import links lane S's `BrSiteChunks` and `BrSiteProps` assets (`WorldDataAsset.siteChunks`, `siteProps`); `TownWorld` spawns the chunks with lane R's loader and road materials (what `SiteChunkView.SpawnTown` does, without the town id in W's runtime) and the fence colliders (`SiteChunks.SpawnFenceColliders`). The scene build calls `SitePropPlacer.Place`: **367 props** (0 Synty, 367 stand-ins, 4 of them lane A's stand-ins) | `Client/Runtime/TownWorld.cs`, `MillbrookTownSceneBuilder.cs` |
| V, layers | `Road`: lane R's road chunks and lane S's site chunks (paved surfaces, kerbs, wheel stops). `Static`: grey boxes and canopies, lane A's models with their `UCX_` colliders, site fences, the trees, lane R's 213 and lane S's 367 props (set in the scene; none of them moves, so none is on `Prop`). In play: **1,005 colliders, 307 on Road, 698 on Static, none on Default** | `TownWorld.cs` (`LayerOf`, `SetLayer`), `MillbrookTownSceneBuilder.cs` |

Bake v3 (`WorldBake.Version`), importer v3. Item 5 of lane S's list (floors from `DoorSpec.sillY`) is not done (not
needed for M3).

## Measured

| Item | Result |
|---|---|
| `World.SitesInWorldData` (new, EditMode) | **pass**: the committed manifest has source "sites" with plates, apron plates, driveway cuts and the committed driveways file's hash; pads = buildings in play − open roofs; `buildings.json` marks the open roofs |
| `World.ImportMatchesData` (extended) | **pass**: a box per walled building, four `post` colliders per open roof, a circle per trunk; the open roof is a canopy mesh; lane S's chunks and props are linked |
| `MillbrookLayers.StaticsAndRoads` (new, PlayMode) | **pass**: every collider on Road or Static (numbers above): road chunks 183 and site chunks 124 on Road; buildings 675 on Static |
| `World.BakeDeterministic`, `World.ManifestHashes` | **pass** against the re-exported WorldData (the bake with site plates and aprons is deterministic too) |
| Lane S's `Sites.TerrainUnderPavedSurfaces` | **pass** (its own plates and aprons, no doubles) |
| `Look.NoErrorShaders`, `ActiveWorldScenes.ServesBothWorlds` | **pass**: 46,337 renderers, 1,042 materials, 0 problems |
| `m3.shots.millbrook` | **pass**, 24 of 24, 0 errors; the fuel view shows the canopy on its posts over the paved forecourt |
| `m3.perf.flythrough` (High, 3 runs, report only) | p50 **3.85-3.88 ms**, p95 **6.68-6.78 ms**, p99 7.53-7.58 ms, worst 9.7-11.0 ms; ready in 1.44-1.45 s (T3.3: p95 6.99-7.17 ms) |

WorldData's terrain changes where the sites and aprons grade it (`heights`, `surface`, `roadd`); `base`, `wear`, `mud`
and `tint` are unchanged. The trees drop from 20,886 to 20,766 (none on the lots now). The bake takes about 80 s, 28 s
of it lane S's site build inside the plate stage.

## For S

- Your site plates and the driveway aprons are in WorldData now. When `SitePlateSource.Enabled` flips to true, W's
  registration becomes redundant and can go (or stay: same name, same function).
- The props set has **no fence runs** (`SitePropPlacer`: "0 fence runs (0 pieces), 0 gates"), so no fence colliders
  spawn yet.
- `SitePropPlacer` saves a material per prop into the scene (the scene now has 1,022 inline materials, 7.8 MB with lane
  R's); a shared material per prop kind would keep it small (lane R has the same note).

## For V

- Layers as asked (table above). Trees carry no Unity colliders: they are in `ActiveWorld.Colliders` (a circle per
  trunk, `tree` indexing `Vegetation.trees`), as the canon's are, so they can snap the same way. The fuel canopy's posts
  are in `ActiveWorld.Colliders` (kind `post`) and in its MeshCollider (`Static`).

## For R

- The bake now hands lane S's cuts to `RoadPlateBuilder.Build`: 862 apron plates. The dropped kerbs and apron slabs in
  the road meshes are still yours (S4's note).

## Next

Nothing open on lane W's side for these requests. Open elsewhere: lane V's switch to `ActiveWorld` (T3.6), fences in
lane S's props, the look items in the T3.3 handoff.

## Resume

```
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldBakeCli.Export              (re-bake WorldData)
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.MillbrookTownSceneBuilder.Build   (import, rewrite Millbrook.unity)
tools\ci\unity-tests.ps1 -Platform EditMode -Category World
tools\ci\unity-tests.ps1 -Platform PlayMode -Category World
```

Set `$env:TEMP` and `$env:TMP` to a folder on D: first (`artifacts\tmp`), so Unity's temp files stay off C:.

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'W'; tools\ci\gate.ps1 -Tier merge -KeepGoing` on `def9a8c`:
**pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1528\gate-merge`): oracle.hash; vendor.check (584 files);
native.ctest; unity.editmode 343 passed, 1 skipped (ServerContents: no server build), 0 failed, `Repo.OwnersRespected`
31 changed paths, all lane W, `Town.NoRealNames` pass; unity.playmode 28 of 28; build.servercompile; build.dev
(485.9 MiB); autotest.smoke. Main has not moved since `54e5362`. The native step's `EngineSimPlugin.dll` was restored
afterwards (lane V's).
