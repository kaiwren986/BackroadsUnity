# M3 paused (2026-10-22)

The user paused the project during T3.7. Nothing is lost: every finished card is merged on `main`, which passes the
merge tier, and the unfinished drive autotest is committed on its own branch. Nothing has been pushed: `main` is
112 commits ahead of `origin/main`.

## Where M3 stands

**Merged on main** (each with its handoff in `docs/milestones/`):

| Lane | Cards |
|---|---|
| I | T3.0 town source; ownership and gate updates during M3 |
| R | R0o OSM graph, R1 junctions, R2 kerbs and walks, R3 markings and chunks, R4 bridges, roundabouts, aprons; `Roads.Validation`; `m3.shots.junctions`; dropped kerbs at the driveway cuts (`M3-handoff-R-*.md`) |
| W | T3.1 world size and `CanonTestWorld`, T3.2 terrain, river, plates, WorldData, T3.3 the `Millbrook.unity` scene and `ActiveWorld`, the T3.3 follow-ups (sites in the world, physics layers) (`M3-handoff-W-*.md`) |
| S | S1 parcels, S2 parking engine, S3 site plans, S4 surfaces and props, S5 validation (`M3-handoff-S-*.md`) |
| A | T3.4 phase 1 Blender buildings, then lane S's real doors (`M3-handoff-A-*.md`) |
| V | T3.5 BRV v2 core, T3.6 PhysX chassis in both worlds (`M3-handoff-V-*.md`) |

**Gates:** a preview of the milestone tier (`gate.ps1 -Tier milestone -Gate M3 -SkipNightlyCheck`) passed 32 of 33
steps at `8937775`. The three `m3.drive.millbrook` steps were still pending (no scenario yet), and the nightly
check was skipped. Only `autotest.m3.drive.millbrook*` stays `pending` in `tools/ci/gates.json`.

## Left for M3 (T3.7)

1. **`m3.drive.millbrook`**: work in progress on branch `m3/ib-drive-millbrook` in `D:\Backroads-Unity-wt\ib`
   (lane I-b), with its own handoff `docs/milestones/M3-handoff-I-drive.md` on that branch. Finish it, gate it,
   merge it, and drop `pending` on its three gate entries.
2. **Nightly**: `tools\ci\gate.ps1 -Tier nightly` on `main` (a run was started and stopped for the pause; none has
   finished).
3. **Milestone gate**: `tools\ci\gate.ps1 -Tier milestone -Gate M3`, which also builds `builds\M3` (Client); commit
   `docs\gates\M3.json` with the report.
4. **`docs\milestones\M3.md`**: the report, screenshots, and the F10 question for the user. The user's first look
   (2026-10-22): "everything is ugly, and painted green". The ground is one flat colour per surface type, with no
   grass, texture detail, wear or colour grading yet.
5. **M4 cards**: start with a look pass (terrain materials and grass, road and wall textures, sun, shadows and
   colour grading), pulled ahead of the planned M4/M5 look work. Then VS-1 and the rest of the re-plan's M4
   (`replan-new-world.md`).
6. Tag `m3`. Push only with the user's OK (`tools\ci\push-github.ps1`).

**Optional polish noted by the lanes:** R1/R2 grade follow-ups (`M3-handoff-R-validation.md`, Findings); shared
materials for site and road props (the scene holds about 1,000 inline materials); fences in lane S's props; BRV
water level for the river (lane V's T3.6 handoff, "For W"); lamps lighting the lots at night.

## Machine notes

- Lane worktrees `D:\Backroads-Unity-wt\{r,w,s,a,v,ib}` are kept, each with its Library (about 10 GB each).
  `tools\ci\worktree-new.ps1` makes fresh ones when needed.
- Drive C: filled up once during M3: the user's own data plus a 25 GB page file under memory pressure. Run
  `tools\ci` scripts with `$env:TEMP`/`$env:TMP` on D: (for example `artifacts\tmp`).

## Resume

Start one coordinator session: "Coordinate milestone M3 of D:\Backroads-Unity following AGENTS.md; resume from
docs\milestones\M3-paused.md".
