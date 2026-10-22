# M3 handoff: lane S, card sites-S5 (validation, determinism, screenshots, the profile check)

Branch `m3/s-validation` in `D:\Backroads-Unity-wt\s`, cut from `m3/s-surfaces` at `88d0880` (S4, since merged), main
(`54e5362`: S4, T3.3, T3.6's test world, A's doors) merged in at `c3f5ff9`. One code commit, `ae65bf4`, then this
handoff. Nothing was pushed, installed, downloaded or signed into; no Synty file was touched; lane I's, W's and R's files
were only read (the shots scenario sits in lane S's folder of the Testing assembly).

## In plain words

The site program now checks itself. One test, **Sites.Validation**, bakes the whole town twice and checks everything
the design asks: every building has its plan, nothing paved lies on a building, a street or a neighbour's land, every
parking stall is inside its lot and can be driven to from an entrance, there are as many accessible stalls as the ADA
table asks, every lot entrance is a driveway lane R really cut, the lots meet the driveway aprons exactly, the ground
stays under the paving in lane W's terrain bake, no prop stands on a stall, an aisle or a doorstep, and the two bakes
and the committed files agree to the byte. A second test, **Sites.MatchesProfile**, measures the generated town the way
the study measured seventeen real small towns (where lots sit round their buildings, how big they are, how far from the
road, how many entrances, the fuel station's layout, house driveways, setbacks and frontage) and checks it falls
inside the real towns' range: **22 of 24 measures do (92 %; it needs 70 %)**. The bake now writes a manifest with the
hash of every input and output, `tools\sites\sites-bake.ps1` runs it (or checks it is current), and a new autotest,
**m3.shots.sites**, photographs twelve sites at noon and dusk in the real Millbrook scene: `M3-handoff-S-S5/
shots-sites-contact-sheet.png`. In those pictures the lots, stall lines and kerbs are there, but the ground is not yet
graded under them (lane W's terrain does not take the site plates until W re-exports it), so parts of the lots sink
into the grass; and the site props are not in the scene yet (lane W's scene build places them).

## Done (card steps)

| Step | What | Where |
|---|---|---|
| 1 | **Sites.Validation** (`Backroads.Tests.EditMode.Sites.Sites.Validation`, the name lane I's gate already selects): two fresh bakes and the checks of design §13 through `SiteValidation`: plans complete (249 buildings, 255 plans), no overlaps (568 surfaces on their own parcels and blocks, off the buildings), stalls inside their lots and reachable (209 stalls in 17 lots, each on a lane an entrance reaches through the lot's lanes), ADA counts (19 accessible where 19 are required), entrances are baked cuts (15 street entrances), aprons within 5 mm (215 at 0.00 mm) with the grades listed, the terrain under the paving (lane W's `WorldBake.Run` with the site plates and lane R's apron plates for our cuts: 6,469 points, 1 over by 39 mm, allowed and listed), props clear (367), template credits (on the town and the 2 template fixtures), determinism and the committed bake and manifest current. Writes `artifacts\sites\(town)\validation-report.txt` | `Sites/Editor/Validation/SiteValidation.cs`, `Tests/EditMode/Sites/SitesHeadlineTests.cs` |
| 2 | **Sites.MatchesProfile** (`...Sites.Sites.MatchesProfile`): the study's measures on the generated town, against `siteProfile.json` (research 08): per served type each lot's position round its building (front, side, rear in the street frame), area, lot-to-building area, gap to the building and to the road edge, width along the street, entrances and surface; the fuel canopy's setback and orientation, the shop's position and gap; house driveway length and side, garage position, setback, frontage. A distribution is inside when the generated median lies in the study's p10-p90 band, a share when within 0.15 (or two binomial standard errors on few sites); counted with at least 3 generated and 5 study values. Town: **22 of 24 inside**; the two outside are house driveways (median 10.7 m, the study's band 13-71 m: S3 draws drives to a pad or garage near the house) and shops' lots beside the building (80 %, the study's 42 %). Writes `artifacts\sites\(town)\profile-check.md` | `Sites/Editor/Validation/SiteProfileCheck.cs` |
| 3 | **Determinism and hashing**: `Sites/Baked/manifest_(town).json` (format, generator versions, the hashes of the inputs: the TownData manifest, lane R's frontage, the profile, the templates, the prop table; and of every output: parcels, plans, chunks, props, plates, door and driveway files); `SitesBakeCli.Bake -verifyHash` bakes twice and fails on a difference or a stale bake, file or manifest. `tools\sites\sites-bake.ps1 [-VerifyHash]` runs it as a gate script (gate JSON, a Unity slot) | `Sites/Editor/Bake/SiteBake.cs`, `tools/sites/sites-bake.ps1` |
| 4 | **m3.shots.sites**: `ShotsSitesScenario` (kind `shots-sites`, lane S's folder of `Backroads.Testing`) loads Millbrook.unity, spawns the site chunks when the scene has none yet (lane R's loader and road materials), and photographs each view at 12:00 and 20:00, 1600x900; the views come from the bake (`SiteShots`: the card's list first, the kinds the town lacks named in the file's notes, other sites of the town in their places; each framed from the street side), written to `StreamingAssets/Autotest/m3.shots.sites.json`. Run with `tools\ci\autotest.ps1 -Scenario m3.shots.sites -Profile Dev -Milestone M3`: **pass, 24 shots**, contact sheet in `M3-handoff-S-S5/` | `Testing/Runtime/Scenarios/Sites/ShotsSitesScenario.cs`, `Sites/Editor/Validation/SiteShots.cs` |

## Done when, expected vs measured

| Item | Measured |
|---|---|
| Sites.Validation passes on the town | **pass** (107 Sites tests in all pass) |
| Sites.MatchesProfile: at least 70 % inside | **pass**: 22 of 24 (92 %) |
| Bake twice and in a fresh worktree, identical hashes; the manifest | **pass** in this worktree (two bakes agree, the committed manifest current); a fresh worktree is the integrator's CI (`tools\sites\sites-bake.ps1 -VerifyHash` there) |
| m3.shots.sites: 12 sites, noon and night, contact sheet | **pass**: 24 shots, contact sheet; see "where this differs" for what the pictures still lack |

**For the M3 report** (lane I's `docs/milestones/M3.md`): plan sources per type: houses 221 (mapped 9, rules 212),
downtown 10 (rules), retail 15 (mapped 1, rules 14), the school and the fuel station 1 each (rules), mapped lots and
yards 7 (mapped); no template plan in the town (S3: no template fits Millbrook's buildings within design §6's
scale range). The profile check's table is `M3-handoff-S-S5/profile-check.md`; the screenshots are
`M3-handoff-S-S5/shots-sites-contact-sheet.png`.

## Where this differs from the card

- **The study's own code**: `tools/osm/measure.mjs` measures OSM extracts; the generated town is not an OSM extract, so
  `SiteProfileCheck` takes the same measures in C# on the plans (same definitions as far as the profile's notes give
  them; "across the street" lots are left out of the position shares, which are renormalised over front, side and
  rear). Docks and aprons have no study data (0 measured) and are not checked; the town has no fast food, supermarket,
  warehouse, farm, quarry, mill or harbour, so their measures have no generated values.
- **tools\ci\sites-bake.ps1** is lane I's path: the script is `tools\sites\sites-bake.ps1` (lane S's) and writes a gate
  JSON like tools\ci's scripts; a one-line wrapper or a gate step pointing at it is the integrator's (suggested: a step
  `sites.bake` with `-VerifyHash`, merge tier).
- **m3.shots.sites** shows only 6 of the card's 12 kinds (two houses, downtown front and rear, a retail lot, fuel); the
  other six views are the town's other sites (two more retail sites, the school, a mapped lot, the campground yard,
  another house). The pictures are not final until lane W (1) re-exports WorldData with the site plates (the ground is
  ungraded under the lots, which sink into it in places), (2) spawns the site chunks in TownWorld (the scenario does it
  meanwhile) and places the props in the scene build; dusk shows no lamp light (TownWorld's sky has no lamps yet).
- **Grades** stay listed, not failures (design §9): 181 notes (S4's handoff).

## For other lanes

- **Lane I**: `gates.json` already has `unity.editmode.sites` (Sites.Validation, Sites.MatchesProfile) and
  `autotest.m3.shots.sites` as pending; both pass now. A bake determinism step can call `tools\sites\sites-bake.ps1
  -VerifyHash` (gate JSON, `-Out`, `-GateName`, `-DryRun` as tools\ci's scripts). The folder meta
  `Game/Assets/Backroads/Testing/Runtime/Scenarios/Sites.meta` sits beside lane S's folder (like S2's `Sites.meta`).
- **Lane W**: as in S4's handoff: the site plates on with a WorldData re-export, the site chunks in TownWorld (the
  scenario then uses them), the props in the scene build, no pads under open roofs; lamps at night would light the lots.

## Next

- The card is done; M3's site work waits on lane W's wiring (above) for final pictures. Follow-ups worth a later S
  card: S3 placing lots with the ground's slope in mind (parcel 40), longer house drives and fewer side lots (the two
  profile misses), and the 9 aprons that lead onto nothing (S3's driveway geometry).

## Resume

```
tools\ci\unity-tests.ps1 -Platform EditMode -Category Sites
tools\sites\sites-bake.ps1 -VerifyHash
tools\ci\build-player.ps1 -Profile Dev -Milestone M3
tools\ci\autotest.ps1 -Scenario m3.shots.sites -Profile Dev -Milestone M3
```

## Merge

`$env:BR_OWNERS_BASE = 'main'; $env:BR_OWNERS_LANE = 'S'; tools\ci\gate.ps1 -Tier merge` (lane S paths only; the
diff against main holds S5): **pass, 8 of 8 steps** (2026-10-22, run `artifacts\20261022-1537\gate-merge`, on
`ae65bf4`): oracle.hash; vendor.check (584 files, 0 differences); native.ctest; unity.editmode 344 passed, 1 skipped
(ServerContents: no server build), 0 failed; unity.playmode 27 of 27; build.servercompile; build.dev (485 MiB);
autotest.smoke. Also run: `tools\sites\sites-bake.ps1` pass (twice the same hash), `autotest.ps1 -Scenario
m3.shots.sites` pass (24 shots). The native step's `EngineSimPlugin.dll` was restored afterwards (lane V's).
