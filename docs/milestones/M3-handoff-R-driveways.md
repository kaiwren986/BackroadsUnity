# M3 handoff: lane R, driveway cuts in the road meshes (lane S's sites-S4 request)

Branch `m3/r-driveways` in `D:\Backroads-Unity-wt\r`, cut from `main` at `b4ad241`. Commits: `c97c7ac` (the cuts in the
meshes, the apron-plate fix, the tests, the re-baked road chunks), `321a2e6` (optional fixed views in the shots
autotest kind), **`f171e2c` (for W: WorldData re-exported after R's apron-plate fix, reviewed by the integrator at
merge; approved by the coordinator, lane W agreed)** and this handoff. Nothing was pushed, installed, downloaded or
signed into. Unity's temp files went to `artifacts\tmp` on D:.

## In plain words

Driveways now meet the street the way they do in a real town. Where one of lane S's driveways reaches a kerbed street,
the kerb drops to a 25 mm lip across the driveway mouth and rises back to full height over a short flare each side,
and the sidewalk dips with it, so the car crosses a gentle apron instead of a 15 cm step and the walk meets the
driveway at the height lane S built it to. On country roads without kerbs, a paved driveway gets a slab of its own
asphalt or concrete across the gravel shoulder; gravel and dirt drives stay the ground's own. That is 186 driveway
mouths on kerbed streets (710 m of dropped kerb), 11 slabs on country roads, 17 unpaved drives.

Doing it turned up a bug in lane R's own ground plates for these aprons (the ones lane W bakes into the terrain): they
followed a straight line from one kerb point to the next, so near the ends of a driveway mouth the ground sat higher than
the lowered walk, and once the walk came down the grass would have poked through it in places, by up to 4.7 cm. The
plates now follow the same shape as the walk, the ground stays at least 3 cm under it everywhere, and lane W's terrain
was re-baked to match (approved, one labelled commit). Pictures in `M3-handoff-R-driveways/`: a driveway mouth seen low
along the kerb (`driveway-mouth.jpg`; the kerb face thins out to the lip across the mouth) and a paved slab across a
country road's shoulder (`driveway-slab.jpg`), both shot in the Dev player before the terrain re-bake.

## Done

| What | Where |
|---|---|
| **The cuts in the bake**: `RoadDriveways.Load` reads `tools/world/sites/(town)/driveways.json` (format `backroads-driveways` v1, its SHA-256); `RoadDriveways.ForBake` resolves every request at once with `DrivewayCutter` against the bake's own frontage (as lane S graded and lane W bakes), into `KerbBaker.Bake.cuts`, `.cutter`, `.drivewaysSha256`; `RoadNetworkBaker.BakeMeshes` passes them to the meshes and records the file's hash on the chunk set | `Roads/Editor/RoadDriveways.cs`, `RoadNetworkBaker.cs`, `KerbBaker.cs` |
| **Kerbed cuts** (`RoadMeshBuilder`): over each cut's throat the kerb face is at Dropped25 and over each flare it rises linearly to the barrier (`RoadPlateBuilder.Dropped`); where the kerb there is already lower (a corner ramp, a taper) it stays. The walk comes down by as much as the kerb top, so its back edge is the cut's lot-edge height (`DrivewayCutter.LotY`: gutter + 25 mm + 1.5 % across the walk). The walk pieces and the kerb segments are split square to the kerb wherever the profile bends (throat and flare ends, and where the kerb already there crosses it), so the shape is exact along the kerb; the apron's walk pieces get their own patch shade. A cut whose throat or flare reaches past its face's end carries on into the next kerbed face of the block loop (9 cuts) | `Roads/Editor/RoadMeshBuilder.cs` (`CutZones`, `Lower`, `AtCuts`, `Concrete`) |
| **Unkerbed cuts**: a paved request (asphalt, concrete, pavers) gets a slab of its material over the cut's apron outline across the shoulder, flat at the pavement edge's height, with a collider; gravel and dirt stay the plates' ground surface | `RoadMeshBuilder.Slabs` |
| **The apron plates follow the same profile**: `RoadPlateBuilder.CutSpans` (the cuts by face, carried ones included), `CutKerbHeight` and `CutBreaks` are shared by the plates and the meshes; a kerbed apron plate is now one piece per bend of the profile instead of one per kerb segment | `Roads/Editor/RoadPlateBuilder.cs` (`Aprons`) |
| **The chunk set** records `drivewaysSha256` (in the content hash when set, so lane S's site chunks and older sets hash as before); `RoadMeshBuilder.Version` stays 2 because lane S's site chunks carry it in their hash. Re-baked `BrRoadChunks_stockport-oh` (the network and frontage are byte-identical) | `Roads/Runtime/RoadChunkSet.cs`, `RoadChunkBaker.cs` |
| **Tests**: `RoadCuts.CutsMatchLaneSAnswers` (every answer in `driveways.json`: outcome, face, throat, merge), `RoadCuts.KerbAndWalkDropAtEveryKerbedCut` (the kerb face at the throat ends and past the flares; the walk every 0.25 m along every throat and flare, behind the kerb, mid-walk and at the back, against the profile; the cut-aware plates' ground at least 0.03 m under it), `RoadCuts.SlabsOnUnkerbedPavedCuts`; `RoadsR3` and `Roads.Validation` bake with the cuts (Validation's plates too) and check that the committed chunks name the current driveways file | `Tests/EditMode/Roads/DrivewayCutTests.cs`, `RoadR3Tests.cs`, `ValidationTests.cs` |
| **For W**, `f171e2c`: `WorldData/millbrook/v1` re-exported with W's exporter, unchanged (`WorldBakeCli.Export`, then `Verify`: matches), from the clean commit `321a2e6` (`dirty: false`) | `WorldData/millbrook/v1/` |
| Extra: the `shots-junctions` autotest kind takes optional fixed views and can leave the eight places out (`places: false`), used here from a local scenario file for the driveway pictures | `Testing/Runtime/Scenarios/Roads/ShotsJunctionsScenario.cs` |

## Measured

| Item | Measured |
|---|---|
| The cuts | 218 requests: 214 baked, 3 merged, 1 rejected, **the same as lane S's answers** in `driveways.json`, request by request (outcome, face, throat to 1 mm, merge) |
| In the meshes | 186 kerbed cuts (710 m of throat), 9 of them carried into the next face; 11 paved slabs across a shoulder; 17 unpaved (the ground) |
| Kerb and walk at the cuts | 13,407 walk points over the throats and flares within 3 mm of the profile (away from face ends) and the kerb face at the throat ends at 25 mm: 0 failures |
| Ground under the lowered walks (32,796 probes along every throat and flare, at 0.1 m steps, three across the walk) | **before the plate fix 2,525 probes** with the ground less than 0.03 m under the walk, the worst 7.7 cm over that line, 4.7 cm through the walk (at a throat end); **after: 0** |
| Road chunks | content `eff860536796…` → `6a0d061a440b…`; LOD0 140,369 → 144,887 triangles (+3.2 %; 34,580 per km², budget 60,000), LOD1 107,591 → 110,643, collider 100,725 → 103,777; data 4.68 → 4.85 MB |
| Tests | Roads 91 passed (3 new), Sites all pass |

**WorldData files changed by the re-export** (`f171e2c`; every other file byte-identical):

| File | Old SHA-256 | New SHA-256 |
|---|---|---|
| `terrain/heights.f32` | `b4e16dad3a0d5b2a42d26314e1930fcee5856e1d78cbacb4c78c2cec869f4197` | `94d5b423fa973f0eaac4b1bce98f5abb35d86150650e8af417a262fb32f4ee7a` |
| `map.png` | `73d25b96af4530a08c12314f04d0ae4605fe3d26bdde7e7ce95d439a394738e0` | `98ad51099dc0276b8658dc487b7daf20bedf00050754f95035e888b9158e0242` |
| `manifest.json` | `c8295779da9cc2cce2d3c4ff4e89558576d45842b8e873796906676d4ecfe16b` | `3d099fdbad6af40aa298b19486f9cfa7793bfa1f17b072274d1ca5e41b5c552c` |

In the manifest: the bake commit `e631d8f` → `321a2e6` (dirty false), apron plates 862 → 1,240, plates 172,124 →
172,502, the plate list hash `95210965…` → `fa09a239…`; the driveways file (`7b518fa8…`, 218 requests, 218 cuts) is
the same.

## Where this differs from the request

- **The plates changed too** (lane R's own `RoadPlateBuilder`), which is why WorldData was re-exported: lowering the walk
  without them would have let the ground through it at the driveway mouths.
- **Flares are the cut's own** (`DrivewayCutter`'s flare: 1 m residential, 1.5 m commercial, 3 m truck), so a residential
  mouth's kerb rises at 12.5 % over its flare; no detectable warnings (driveways are not curb ramps).
- **The verge** behind a cut is unchanged: lane S's site surface (the driveway) covers the lot side.
- **Unkerbed slabs** are flat across the shoulder at the pavement edge's height, like their plates; a radius return's
  curve is the cut's outline.

## Next

- The R1/R2 follow-ups from `M3-handoff-R-validation.md` (ramps on grades, continuous walk heights around wide corners,
  short-edge warps), a separate card; say if you want them now.
- **Lane W**: nothing to do; the next WorldData export from main reproduces `f171e2c`. **Lane S**: nothing; the cuts are
  yours, unchanged.

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads -TestFilter Backroads.Tests.EditMode.Roads.RoadCuts
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.BakeTowns           (re-bake the road chunks)
tools\ci\project-unity.ps1 -Method Backroads.World.Bake.WorldBakeCli.Export               (lane W's, after a plate change)
```

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'R,W'; tools\ci\gate.ps1 -Tier merge` (lane W in the list only for
the labelled commit `f171e2c`):
**pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1622\gate-merge`, on `f171e2c`, 11 min): oracle.hash;
vendor.check (584 files, 0 differences); native.ctest; unity.editmode 349 passed, 1 skipped (ServerContents: no server
build), 0 failed, among them the 91 Roads tests (`RoadCuts` x3, `Roads.Validation` 15.5 s, `RoadsR3.CommittedChunksCurrentAndLoadable`),
lane W's `World.BakeDeterministic` (77.5 s), `World.ManifestHashes`, `World.SitesInWorldData` and `World.ImportMatchesData`,
`Repo.OwnersRespected` (lanes R,W) and `Repo.NoCrInHashedFiles`; unity.playmode 28 of 28; build.servercompile;
build.dev (486 MiB); autotest.smoke. Then `m3.shots.junctions` on that build: pass, 16 of 16. The native step's
`EngineSimPlugin.dll` and `.version.json` were restored afterwards (lane V's).
