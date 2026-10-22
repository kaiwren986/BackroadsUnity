# M3 handoff: lane A, card T3.4 door step (the buildings take the site plan's doors)

Branch `m3/a-doors` in `D:\Backroads-Unity-wt\a`, cut from `main` at `91b0690` (T3.4 phase 1 and sites-S3 merged).
Commits: `241a784` (generator, tests, regenerated models), `fb1ff55` (a door for every building the site plan
skips) and the handoff commit after them. Nothing was pushed,
installed, downloaded or signed into; no Synty file was touched.

## In plain words

The buildings now have their doors where lane S's site plans put them, instead of lane A's guesses: the front door
faces the walk or the drive, the back door the yard or lot, and garage doors the apron. Porches and front steps
moved with the front doors. Lane S gives no door to sheds and garages (they get an apron only) and plans nothing
outside the play area, so those 13 buildings keep lane A's own doors, marked as ours. The door test now checks the
site's real specs: every one of the 428 site doors on a modelled building has its opening within 3 mm of the spot,
at least as wide as asked. Pictures: `art/buildings/review/` (re-rendered with the new doors).

## Done

| What | Where |
|---|---|
| **Site doors by default.** `art\buildings\gen\run.ps1` reads `tools/world/sites/<town>/doors.json` when it exists (`-PlaceholderDoors` for lane A's, `-Doors <file>` for another file) and rebuilt all 247 town buildings: 428 site doors (Main 230, Service 194, Garage 4, of S3's 464; the other 36 are on grey-box buildings) on 234 buildings | `art/buildings/gen/run.ps1`, `plan.py`, `Game/Assets/Art/Buildings/Millbrook/` |
| **Buildings without a site door** keep lane A's rule doors (21 doors on 13 buildings: 10 garages and a machine shed that the site plan gives an apron only, 2 houses outside the play area; Garage 10, Bay 1, Service 8, Main 2). Their markers are `DOOR_<Kind>_A<NN>`, so they never pass for site specs; the index counts them (`fallbackDoors`). The rule now always finds a wall long enough for a garage door and gives any building without one a plain door (phase 1 left two garages doorless and three with only a side door) | `plan.py`, `build.py` |
| **Low walls**: a door is cut under the roof of the wall it is in (one site Service door in a lean-to wing now stands 1.68 m) | `build.py` |
| **Index**: `doors` = `sites-S3`, `doorsFile` = `tools/world/sites/<town>/doors.json` (the town folder written as `<town>`, so no real name ships), `fallbackDoors` = 21. The placeholder file for the town is gone (the fixtures keep theirs) | `Game/Assets/Art/Buildings/Millbrook/buildings.index.json` |
| **Tests**: `Art.DoorsMatchSites` reads the file the index names, fails when S3's file exists but the models were made without it, and checks only the site's specs; `Art.BuildingsDeterministic` regenerates its sample with the same door file | `Game/Assets/Backroads/Tests/EditMode/Art/BuildingTests.cs` |

## Done when, expected vs measured

| Test | Measured |
|---|---|
| `Art.DoorsMatchSites`: every `DoorSpec` from S3 has an opening within 0.1 m | **pass**: 428 site specs on modelled buildings, worst distance **3 mm**, every opening at least the spec's width, the LOD0 wall open across every doorway; 36 specs on grey-box buildings skipped (phase 2 types) |
| `Art.BuildingsDeterministic` | **pass** (sample of each model type with the site doors, and every fixture: generated twice, identical, equal to the committed files). By hand: all 247 town FBX regenerated with the site doors in a fresh process are byte-identical to the commit |
| `Art.BuildingsBudget` | **pass**: 262 models, LOD0 max house 5,907 / 6,000, block 3,928 / 15,000, mill 10,414 / 15,000 (fixture); 7,288 panes, 706 colliders, 0 triangles dropped on import |

Art tests 6 of 6. Merge gate: last section.

## For lane S

1. **Doors for garages and sheds.** S3 writes no door for 11 of the town's accessory buildings that lane A models (10 garages
   and a machine shed: `way/1024617019`, `way/1272028969`-`way/1272028978`); they keep lane A's rule doors (garage door
   toward the nearest driveway end or the street). If their aprons point elsewhere, a `Garage` (or `Bay`) spec on the
   apron's wall would line them up; lane A picks it up on the next regeneration.
2. **A front door on a touching wall.** `way/1272029067`'s Main door is on its edge 3, which runs within 0.8 m of
   `way/1272029066` for its whole 8.3 m. It is modelled as specified; you may want it on another wall.
3. Porch outlines in the index (`porch`, town frame) now follow your Main doors (112 porches).

## For lane I

`tools\ci\gates.json`: `unity.editmode.art-buildings` (M3 nightly and milestone) is still `soft` with `pending`; the
three tests are on main and pass, so T3.7 can drop `pending` and set it hard.

## Resume

```powershell
cd D:\Backroads-Unity-wt\a
art\buildings\gen\run.ps1                                     # site doors when tools\world\sites\<town>\doors.json exists
tools\ci\unity-tests.ps1 -Platform EditMode -TestFilter Backroads.Tests.EditMode.Art
art\pipeline\unity-art.ps1 -Steps building-capture -Graphics  # review renders
```

## Merge

`$env:BR_OWNERS_BASE='main'; $env:BR_OWNERS_LANE='A'; tools\ci\gate.ps1 -Tier merge` on `fb1ff55` (with `TEMP`/`TMP`
on D:): **pass**, 8 of 8 steps in 619 s (2026-10-22 14:05): oracle hash, vendor check, native ctest, EditMode **305
passed, 1 skipped, 0 failed** (with `Repo.OwnersRespected` on lane A's paths and the three building tests), PlayMode 20
of 20, server compile, Dev build (231.4 MiB), smoke autotest. A first run at 13:49 stopped at native ctest because lane
W's perf autotest held `machine.lock`; it was rerun after the lock cleared. The native plugin the gate rebuilt was
restored (lane V's files). Lane A paths only: `art/`, `Game/Assets/Art/`, `Game/Assets/Backroads/Tests/EditMode/Art/`,
this file.
