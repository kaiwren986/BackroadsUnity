# AGENTS.md: rules for every agent working in this repo

Read this before doing anything. The full design is `docs/PLAN.md`; cite its sections by number.

## Hard rules

1. **Never edit `D:\Backroads`** (the browser game, the canon oracle) **or `D:\Backroads-ref\engine-sim`**. Read the oracle only through `git -c core.autocrlf=false archive` (see `oracle.lock.json`).
2. **Never push**, never sign in, never install software, never change system or power settings, never type or ask for passwords. Downloads are listed in `docs/approvals/QUEUE.md` and happen only once the user has approved them.
3. **Synty: no AI-made or AI-edited models, nothing uploaded, nothing committed.** The user set this line on 2026-10-18, with model training turned off; it is what Synty's licence actually bans (PLAN §4.7, 07 §10.4). Agents may import the user's Synty `.unitypackage` files with Unity's importer, look at and audit them (names, counts, triangles, materials, screenshots), set up and convert their materials in Unity, and place, scale and arrange their prefabs in scripts and scenes. Agents never make or edit 3D models from Synty assets (no reshaped, cut, merged or regenerated meshes, and no Synty geometry in baked meshes), never upload Synty files or renders to AI 3D or image generation tools, and never commit Synty files: the pack folders are gitignored and listed in `synty-manifest.json`. Files inside the pack folders are changed only by Unity itself (import, material conversion); our own materials for Synty props go in `Game/Assets/Art/SyntyOverrides/`.
4. **Millbrook is canon.** `CanonData/v1` is frozen. Any deviation is recorded in `CanonData/CHANGES.md` with the user's approval and before/after hashes.
5. **No real brand names or logos** in shipped content: vehicle names, engine names and ids, badges, logos (PLAN §3.10, test `Content.NoRealMarks`). Shapes are not policed: grilles, lamps and silhouettes are fine, and Synty models are used as they are (the user, 2026-10-18).
6. **Frozen contracts** change only with an ADR in `docs/adr/` and updated tests in the same commit (PLAN §1.10).
7. Hashed trees (`CanonData/**`, `native/EngineSimPlugin/vendor/**`, `native/EngineSimPlugin/generated/**`, `Game/Assets/StreamingAssets/**`, `tools/oracle/traces/**`, `TownData/**`, `WorldData/**`) must never contain CR bytes (test `Repo.NoCrInHashedFiles`).

## Lanes and ownership

Lanes: **I** integrator, **W** world and rendering, **V** vehicles and native plugin (V′ joins in M3), **R** roads and traffic, **A** art (Blender), **N** netcode (from M4), **G** gameplay and UI (from M7). `OWNERS.json` says which lane owns each path. Change only what your lane owns; `Repo.OwnersRespected` enforces it.

Each lane works in its own worktree: `tools\ci\worktree-new.ps1 -Lane <lane> -Topic <topic>` creates `D:\Backroads-Unity-wt\<lane>` on branch `<milestone>/<lane>-<topic>`. Only the integrator merges into `main`, after `tools\ci\gate.ps1 -Tier merge` passes.

## Locks (files under `artifacts\`)

| Lock | Meaning |
|---|---|
| `.user-playing` | The user is playing a build (created by `Play.bat`). Run nothing heavy. |
| `machine.lock` | A perf gate is running. Nothing else runs. |
| `.gpu.lock` | A GPU or audio autotest is running. One at a time. |
| `.unity-slots` | At most 4 Unity processes at once. |

Perf gates count only on mains power.

## Canonical commands

All from the repo root in PowerShell. `tools\ci\env.ps1` holds every tool path.

| What | Command |
|---|---|
| Environment check | `tools\ci\env-check.ps1` |
| Native plugin | `tools\ci\build-native.ps1` |
| Vendor integrity | `tools\ci\vendor-check.ps1` |
| Canon export | `tools\oracle\run-export.ps1` |
| Unity tests | `tools\ci\unity-tests.ps1 -Platform EditMode` (or `PlayMode`) |
| Player build | `tools\ci\build-player.ps1 -Profile Dev -Milestone M<n>` |
| Gates | `tools\ci\gate.ps1 -Tier merge` (also `nightly`, `milestone -Gate M<n>`) |
| Backup | `tools\ci\backup.ps1` |

## Task cards and resuming

- Task cards: `docs/cards/M<n>/*.md` (M0 and M1 cards are in PLAN "First two weeks").
- Every card ends with a committed `HANDOFF.md` in the lane's working folder: what is done, what is next, the exact command to resume. A fresh session resumes from it.
- Anything that needs the user goes into `docs/approvals/QUEUE.md` with the decision id and its default.

## Starting a milestone

Start one coordinator session with: "Coordinate milestone M<n> of D:\Backroads-Unity following AGENTS.md and docs/PLAN.md §6". The coordinator starts the lanes, merges, builds and writes `docs/milestones/M<n>.md`.
