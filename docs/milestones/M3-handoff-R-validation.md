# M3 handoff: lane R, Roads.Validation (05 §8 on the town, the M3 headline test)

Branch `m3/r-validation` in `D:\Backroads-Unity-wt\r`, cut from `main` at `c5c147f`. Commits: `b3acbd2` (the test, the
grade report, the roundabout-entry check) and this handoff. Nothing was pushed, installed, downloaded or signed into.
The second lane R gate item, the `m3.shots.junctions` screenshots, is not in this card: it needs lane W's new
`Millbrook.unity` (T3.3), which has not started (lane W is on T3.2).

## In plain words

The town's roads now have one test that checks everything research 05 §8 asks of them: `Roads.Validation`. It bakes
the whole road network once in memory (7 s), runs every check the road cards already had on that one bake, then bakes
it again to prove the result is identical, and compares it with the road files committed in the repo. It finds 0
errors. Every junction surface is a clean counter-clockwise outline, every road meets its junction on exactly the same
vertices, the ground sits at least 3 cm under the pavement everywhere, every stop approach has exactly one STOP sign
and stop line, the R4 checks (bridge, aprons, alleys, close pairs, tapers) are clean, and the committed files match a
fresh bake. Steep places are listed, not failed, as 05 §8.3 asks: 10 stop approaches on grades over 6 % and 31
junctions that lean with their main road over 3 %. The list also turned up three things worth fixing later in the
kerbs and walks (see "Findings"): kerb ramps that get too steep on sloping streets, small steps in wide walks around
sloping corners, and a few short streets whose gutter kinks where two junctions' height blends meet. The summary is
`M3-handoff-R-validation/validation-report.txt`, the full grade list `M3-handoff-R-validation/grades-report.txt`.

## Done

| What | Where |
|---|---|
| **`Roads.Validation`** (`Backroads.Tests.EditMode.Roads.Roads.Validation`, category Roads, the name `gates.json` already filters on): one fresh in-memory bake of the town (`RoadNetworkBaker.BakeTownWithKerbs` + `BakeMeshes` + `RoadPlateBuilder.Build`: graph, surfaces, kerbs, streets, props, bridges, meshes, chunks, plates; nothing saved), then the existing checks on it: `RoadGraphChecks.MajorProblems` / `ControlProblems` and the importer's grade conflicts; `SurfaceChecks` (§8.1 simple and counter-clockwise, areas against the corridor estimate, no fallback; §8.2 seams bit-shared, worst error exactly 0; kerbs in buildings); kerb faces on every road side (§8.4); `RoadMeshReport.Run` (§8.6: one STOP or signal per controlled approach on its right 0.6-2 m from the kerb within 15.2 m of the edge, stop lines only with them, no centre line on a junction, mapped controls honoured); `RoadPlates.Terrain` (§8.7, below); `RoadR4Report.Run` (all six problem lists empty, alleys in the map = graph + skipped); a second bake (§8.9: network, frontage, chunk and plate hashes equal) and the committed network, frontage and chunks current and self-consistent. Every failure goes into one error list; the test prints a one-screen summary (`TestContext.WriteLine`) and writes `artifacts/roads/(town)/validation-report.txt`. The finer tests are unchanged | `Tests/EditMode/Roads/ValidationTests.cs` |
| **§8.3 grade report** (new): approaches over 6 % within 10 m of a stop line (the window `SurfaceBuilder` uses to move a stop line), junction surfaces leaning over 3 % (the least-squares plane through each surface mesh; Plane-mode junctions must stay within 3 %, a failure otherwise), walk cross slopes over 2.1 %, kerb ramps over 8.33 % (depressed corners, alley mouths, tapers) and perpendicular-ramp flares over 10 %, and steps where two faces' walks meet more than 5 cm apart. Walks and ramps are read like an inspector's 2 ft (0.61 m) level: across each face's own walk behind the kerb top and at the back edge of every kerb segment, and along the kerb top in 0.1 m steps over each ramp run (a finer chord reads centimetre kinks as steep slopes). Listed, never failed; `artifacts/roads/(town)/grades-report.txt` | `Roads/Editor/RoadGradeReport.cs` |
| **Terrain check shared**: `RoadPlates.TerrainUnderAndBesideThePavement`'s sampling (0.25 m inside every paved footprint at least 0.03 m under the surface; 1 m outside within 0.1 m of the edge) moved into `RoadPlates.Terrain(Kerbs.Bake)`, which that test and `Roads.Validation` both call with the same assertions | `Tests/EditMode/Roads/KerbTests.cs` |
| **Roundabout entries in `RoadMeshReport`**: a `RoundaboutYield` approach now needs a YIELD on its right (a second one on a raised splitter is allowed) and a yield line; before, its YIELD signs were reported as "a sign on an uncontrolled approach", so the report would have failed on any town with a roundabout. The town has none; `Roads.Validation` checks R4's synthetic ring too | `Roads/Editor/RoadMeshReport.cs` |

No bake changed: the committed network, frontage and chunks are the R4 ones (content hashes `17799d4eebc6…`,
`f7700ce35c52…`, `eff860536796…`).

## Done when, expected vs measured

| Item (the brief) | Measured |
|---|---|
| The bake has 0 errors | **pass**: 0 errors over 183 roads, 134 nodes (93 junctions), 183 edges; 0 major-road or control problems; 0 grade conflicts in the import |
| Every junction surface simple and counter-clockwise | **pass**: 110 surfaces (93 junctions, 17 two-arm nodes), 0 not simple, 0 fallbacks; areas within 5 % of the corridor estimate (worst −4.2 %), 7 irregular junctions within 15 % (listed by R1) |
| Ribbon/mouth seams bit-shared | **pass**: 342 seams, 0 problems, worst difference 0 m |
| Terrain under every paved footprint at surface − 0.03 m or lower | **pass**: 32,225 points 0.25 m inside ribbons, junctions and walks, 0 failures; 13,646 points 1 m outside within 0.1 m of the edge (7 between two edges at different heights, 81 at alley mouths, under the 1-in-200 and 1-in-20 bounds); 88,024 plates |
| One STOP or signal per controlled approach; yield signs and lines at roundabout entries | **pass**: 92 controlled approaches, 92 STOP signs, 92 stop lines, 0 problems, 1 sign set back 4 ft before a far crosswalk (node 23, as in R3). The town has no roundabout; R4's synthetic ring: 4 entries, 8 YIELD signs, 4 yield lines, 0 problems |
| The 05 §8.3 grade report as listed exceptions | **listed**: 10 of 92 stop approaches over 6 % within 10 m (worst 16.4 %, a gravel apron at node 33; 4 stop lines already moved to the flattest spot); 31 of 93 junctions leaning over 3 % (worst 13.2 %), all Major junctions carrying their main road's grade (the town has no Plane junction); 83 faces with a walk cross slope over 2.1 %; 135 of 458 ramp runs over 8.33 %; 7 of 12 flares over 10 % (worst 13.0 %); 3 faces whose walk meets another face's 5-12 cm apart. See "Findings" |
| R4's checks with empty problem lists | **pass**: 16 close pairs, 9 alleys (9 in the graph, 0 skipped), 54 alley mouths, 7 unpaved meetings, 76 kerb tapers, 1 bridge, 0 roundabouts: 0 problems |
| Bake twice: identical hashes for network, frontage, chunks; committed assets current | **pass**: network `17799d4eebc6`, frontage `f7700ce35c52`, chunks `eff860536796`, plates `6c641635544e` from both bakes; the committed three are current, their stored hashes match their content, and the chunks name this network and frontage |
| Extra | the test takes 16.8 s (two full bakes); Roads tests **87 passed** (1 new) |

## Findings (listed by the grade report; follow-ups, nothing fails)

1. **Kerb ramps on sloping streets** (R2): a depressed corner or alley mouth drops the kerb 0.125 m over a fixed 1.83 m
   (6.8 %); on a street falling the same way the walk at the kerb falls 6.8 % plus the street's grade. 135 of 458 ramp
   runs read over 8.33 % (102 depressed corners, 24 alley mouths, 9 tapers); every depressed corner and alley mouth on
   the list reads 6.8-6.9 % more than the gutter under it (the 9 tapers are finding 3's kinks or the 5 % taper on a
   slope). Fix: lengthen the run with the grade (PROWAG lets the run stop at 15 ft, 4.57 m).
2. **Steps in wide walks around sloping corners** (R2, `KerbPieceBuilder`): where the walk is 4-6 m deep around a
   curved kerb, the overlapping segment pieces are clipped and each keeps its own segment's kerb-top height, so
   neighbouring patches differ by up to about 0.2 m (face 328 at (479, 394): the kerb top rises 0.25 m along the arc).
   83 faces read a cross slope over 2.1 % (52 over 5 %, 13 over 10 %, worst 66 % across such a step). Fix: give walk
   vertices the height of the nearest kerb point, as verges already take the nearest edge's, so the walk is continuous.
   This changes the frontage and chunk hashes (re-bake) and the walks lane S reads.
3. **Short edges between two height blends** (R1): on edge 167 (15 m between a junction and a class change; the
   centreline falls 2-4 %) the gutter drops 0.15 m in 0.56 m where the two 12 m warps meet; similar kinks show in the
   taper lines of edges 134 and 17. Fix: limit the warp's gradient on edges shorter than two warps.
4. **Flares** over 10 % (7, worst 13.0 %): designed at exactly 10 %, plus the street's grade.

## Waiting on you

Nothing. The street-name list (U33) is still waiting for your approval.

## Where this differs from the brief and 05 §8

- **§8.5 lanes** wait for R5 (no lane graph yet); **§8.8 golden** is the `m3.shots.junctions` autotest (below); the
  test prints both as "not here".
- **§8.1 area** is R1's corridor estimate, not a Clipper2 union of connector corridors (no connectors before R5).
- **§8.4** mailboxes, trees and lamps are not road props (sites and world); the signs' 0.6-2 m from the kerb is checked
  under §8.6.
- **"Junction planes over 3 %"** lists every junction surface whose best-fit plane leans over 3 %, not only Plane-mode
  ones: on a hill a Major junction carries its main road's grade, which is the point of 05 §5.6.
- **Determinism "also in a fresh worktree"** (sites-roads overview): this run compares two bakes in the lane R worktree
  with the committed files; the merge gate in the main checkout runs the same test in a second checkout.
- **`gates.json`** is the integrator's: `unity.editmode.roads-validation` can drop `pending` and become `hard` (one test,
  about 17 s, passes on this branch); `autotest.m3.shots.junctions` stays `pending` until the screenshots land.

## Next

- **`m3.shots.junctions`** once T3.3's `Millbrook.unity` is on main: an autotest kind in
  `Testing/Runtime/Scenarios/Roads/` and `StreamingAssets/Autotest/m3.shots.junctions.json` (lane R paths), fixed
  cameras picked from the road network on 8 junctions (the downtown main-street junction, a residential X, an alley
  mouth, a gravel apron with its STOP, the bridge ends, the town-edge taper, a complex-pair junction, a rural T) at noon
  and night. It needs the scene's world (time of day, the camera) and the road chunks and props in it.
- R2 and R1 follow-ups from "Findings" (ramps on grades, continuous walk heights, short-edge warps).
- R5: the lane graph (05 §8.5).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category "!PlayerOnly" -TestFilter Backroads.Tests.EditMode.Roads.Roads.Validation -MinTests 1
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads
```

The reports land in `artifacts\roads\(town)\validation-report.txt` and `grades-report.txt`.

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'R'; tools\ci\gate.ps1 -Tier merge` (lane R paths only):
**pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1247\gate-merge`, on `b3acbd2`, 345 s): oracle.hash;
vendor.check (584 files, 0 differences); native.ctest; unity.editmode 248 passed, 1 skipped (ServerContents: no server
build), 0 failed, `Roads.Validation` among them (15.2 s); unity.playmode 9 of 9; build.servercompile; build.dev
(231 MiB); autotest.smoke. The `gates.json` step itself (`unity-tests.ps1 -Platform EditMode -Category "!PlayerOnly"
-TestFilter Backroads.Tests.EditMode.Roads.Roads.Validation -MinTests 1`): pass, 1 test. The native step's
`EngineSimPlugin.dll` and `.version.json` were restored afterwards (lane V's, committed only at milestone tags).
