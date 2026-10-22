# M3 handoff: lane R, m3.shots.junctions (the road screenshots of the M3 gate)

Branch `m3/r-shots` in `D:\Backroads-Unity-wt\r`, cut from `main` at `54e5362` (T3.3's `Millbrook.unity` and S4 merged).
Commits: `6dbafbe` (the picker, the autotest kind, its scenario file and test) and this handoff. Nothing was pushed,
installed, downloaded or signed into. Unity's temp files went to `artifacts\tmp` on D:.

## In plain words

The M3 gate's road screenshots now exist: `m3.shots.junctions` flies the camera to eight places of the new Millbrook's
roads and takes each at noon and at 22:00. The places are picked from the road network by rule, so they follow any
re-bake: a downtown junction on the main street with its crosswalks and the side street's STOP, a four-way of local
streets, an alley's dropped-kerb mouth seen from across the street, a gravel road's paved apron and STOP where it meets
the main road, the river bridge from its west end looking along the deck, the town edge where the kerbs end and the
country road's double yellow begins, the closest pair of junctions kept as two, and a T on the country highway outside
the town. Every view shows what it names. The night shots are dark: the town has no street lights in M3, so only the
markings and the moonlit buildings show. Contact sheets in `M3-handoff-R-shots/`:
- `junctions-1200.jpg`: the eight places at noon.
- `junctions-2200.jpg`: the same at 22:00.
- `junctions.txt`: each camera, the node it looks at and why that place was chosen.

The full-size shots (1600x900) are in `artifacts\autotest\m3.shots.junctions\` on this worktree.

## Done

| What | Where |
|---|---|
| **The picker** `JunctionShots.Pick(RoadNetworkAsset)`: eight places by rule, each a different node, deterministic (scores, then the lowest id). *main-street*: a downtown junction with two main-street arms and a paved side street that stops (most crosswalks first). *residential-x*: four kerbed, paved local-street arms outside downtown, not a close pair, from a stopping approach (the longest shortest arm). *alley-mouth*: a T where one `service=alley` meets two kerbed paved streets, camera on the far half of the street on the alley's line, looking into the mouth. *gravel-apron*: an unpaved approach that stops at a paved junction (square-on meetings and long, flat gravel roads first). *bridge*: the longest deck, from 24 m before its west end, looking 60 m along it. *town-edge*: a class change from a kerbed to an unkerbed paved arm, from the country side (flattest first). *complex-pair*: the closest pair of junctions kept as two, from 8 m over an approach of the first, looking between them. *rural-t*: a three-arm junction outside the kerb zone on a highway through road with a paved side road that stops. A camera on an approach stands over its right lane (the side its sign is on), at least 9 m behind the stop line, 3-8 m over the road, looking at the node | `Roads/Runtime/JunctionShots.cs` |
| **The autotest kind** `shots-junctions`: loads `Millbrook.unity` with lane W's `TownScenarioUtil.Load`, finds the road network the scene's chunks were baked from (its content hash; the asset is in the build's Resources), picks the places, writes `junctions.txt`, freezes the clock and the wind, and renders each place at each hour into a 1600x900 target (`(id)_(hhmm).png`, the contact sheet). The camera is lifted to stay 1.5 m over the ground (none needed). Fails on no road chunks, no matching network, a missing place, or fewer shots than `minShots` | `Testing/Runtime/Scenarios/Roads/ShotsJunctionsScenario.cs` |
| **The scenario file**: hours 12 and 22, 1600x900, 8 settle frames, `minShots` 16 | `StreamingAssets/Autotest/m3.shots.junctions.json` |
| **`RoadShots.PicksEveryPlace`** (EditMode, category Roads): on the committed network all eight places are found, on distinct nodes, inside the play square, 10-60 m from what they look at (the bridge view 100 m), the same twice, and no building footprint of the map stands between a camera and its junction | `Tests/EditMode/Roads/RoadShotsTests.cs` |

## Done when, expected vs measured

| Item | Measured |
|---|---|
| Fixed cameras on 8 representative junctions, noon and night | **pass**: 16 shots of 16 (8 places at 12:00 and 22:00), 0 failures, in the Dev player the merge gate built from `6dbafbe` (`M3-6dbafbe+-Dev`, the + being the gate's rebuilt native plugin; RTX 4090, High, 2560x1440 window, shots 1600x900 off screen); the town ready in 1.3 s; 0 cameras lifted over the ground. Checked by eye: every view shows what it names |
| The places (nodes of network `17799d4eebc6`) | main-street node 23 (4 arms, 4 crosswalks, side street R15 stops); residential-x node 92 (4 kerbed street arms, the shortest 53 m); alley-mouth node 20 (alley R12 meets street R99); gravel-apron node 31 (unpaved R150, 90 m, +2.8 %); bridge node 121 (the 226 m deck over the river); town-edge node 34 (highway R26 becomes kerbed main street R25); complex-pair nodes 21 and 85 (11.9 m apart); rural-t node 89 (highway R91/R90, side road R95 stops) |
| Extra | Roads tests 88 passed (1 new) |

## Where this differs from the brief

- **Night is 22:00.** The town has no street lights yet (lane W's `TownWorld` lights no lamps), so the night set is dark;
  20:00 (lane W's dusk) reads better if you would rather have dusk.
- **The bridge is one view**, from its west end looking along the whole deck (the far end is in it), so eight places
  stay eight.
- **The cameras come from rules, not hand-placed poses**, so a re-bake that changes node numbers keeps the shots.
- **05 §8.8's golden comparison** (the same cameras in reference shots) has nothing to compare with for the new town; the
  contact sheets are for your eyes.

## Seen in the shots, not fixed here

- **Lane W**: in the scene the gravel road beyond its 12 m paved apron renders almost as dark as the asphalt, so the
  apron's end does not read (R4's preview with the plain terrain shader showed it sandy). The asphalt is near black at
  noon (lane W's note too).
- **Lane R**: the walk fans around some corners (beside the alley mouth, right of it) show the walk-piece steps found by
  `Roads.Validation`'s grade report (its finding 2); the driveway mouths still carry a full kerb (job 2, next).

## For I

- `gates.json`: `autotest.m3.shots.junctions` can drop `pending` (it passes on this branch: 16 of 16 shots, about 17 s
  in the player). It is a screenshot gate for you to look at; `hard` is yours to decide.

## Next

- Job 2 of this round: dropped kerbs and apron slabs at lane S's driveway cuts in the road meshes.
- The R2 and R1 follow-ups from `M3-handoff-R-validation.md` (ramps on grades, continuous walk heights, short-edge warps).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads -TestFilter Backroads.Tests.EditMode.Roads.RoadShots
tools\ci\build-player.ps1 -Profile Dev -Milestone M3
tools\ci\autotest.ps1 -Scenario m3.shots.junctions -Profile Dev -Milestone M3
```

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'R'; tools\ci\gate.ps1 -Tier merge` (lane R paths only):
**pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1530\gate-merge`, on `6dbafbe`, 9 min): oracle.hash;
vendor.check (584 files, 0 differences); native.ctest; unity.editmode 343 passed, 1 skipped (ServerContents: no server
build), 0 failed, among them the 88 Roads tests (`RoadShots.PicksEveryPlace`, `Roads.Validation` 17.5 s); unity.playmode
27 of 27; build.servercompile; build.dev (485 MiB); autotest.smoke. Then `m3.shots.junctions` on that build: pass, 16
of 16. The native step's `EngineSimPlugin.dll` and `.version.json` were restored afterwards (lane V's).
