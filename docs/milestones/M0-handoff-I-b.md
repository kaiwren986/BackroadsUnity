# M0 handoff, lane I-b (integrator, CI scripts): T0.6, the PowerShell side

Branch `m0/i-scripts` in worktree `D:\Backroads-Unity-wt\ib`, cut from `main` at `dd412fa`, with `main` merged in three times: `d684b73` (JSON fixes), `3023fc6` (lanes I-a and V, which brought `Game\`), and `0bab6ec` (lane W's canon export, the Synty rule). Not merged into main, not pushed.

## Done

All in `tools\ci\`, Windows PowerShell 5.1 syntax, ASCII only. Each script dot-sources `env.ps1` and `ci-lib.ps1`. `tools\ci\README.md` lists every switch with examples.

| File | What it does |
|---|---|
| `env.ps1` | New variables (existing names unchanged): `MAIN_REPO` (the main checkout even from a worktree), `GIT_COMMON`, `LOCKS` (the main checkout's `artifacts\`, shared by all worktrees; `BR_LOCKS_DIR` overrides it for the self-test), `BUILDS`, `BACKUP_DEST` (`D:\Backroads-Unity-backups`, U21), `LFS_BUDGET_GIB` (6), `UNITY_SLOTS` (4), `WSL_DISTRO`, `POWERSHELL` |
| `ci-lib.ps1` | Shared helpers: gate JSON (PLAN 1.10 fields), run folders, locks, user-playing detection, the process runner, Unity log checks, the NUnit parser, machine facts, endpoint rules, build lookup |
| `unity-tests.ps1` | `-runTests` (never `-quit`), NUnit XML into the gate JSON, exact-match categories, licence and compile-error reasons, `-MinTests`; runs `build-native.ps1` first when the plugin DLL is missing |
| `build-player.ps1` | `BuildCli.Build -profile -milestone -out` (Client to `builds\M<n>\`, Dev to `builds\M<n>-dev\`, Server to `builds\M<n>-server\`); checks BuildCli's report, `build.json`, `Play.bat` and the testing assembly; writes the full notices; `-CompileServerScripts` for rule 7 |
| `autotest.ps1` | `-br-autotest <id>` / `-br-smoke`; `.gpu.lock`; perf runs take `machine.lock` plus all slots, need an idle machine and mains power (otherwise `invalid`); `-MinSpec` (E-core affinity from `env.json`), `-Fps`, `-Repeat` (majority, medians); egress cross-check for `m0.net.egress` and every smoke |
| `soak.ps1` | Server canary (`-Clients 0`: exit 0, `SERVER BOOT OK`, loopback-only sockets); bot soak plumbing, gated until the M4 bot CLI; `-ServerLinux` gated on U4b |
| `gate.ps1` + `gates.json` | Tiers `merge` (1200 s), `nightly` (10800 s) and `milestone -Gate M<n>` with the wall clock against the budget. Pauses heavy steps while the user plays; fail-fast on merge; at the milestone tier, headline steps decide and lane failures are listed; copies to `docs\gates\`; sets `BR_OWNERS_BASE=main` for `Repo.OwnersRespected` |
| `oracle.ps1` | Thin wrapper around lane W's tools: `gen-hash.mjs` (CI check), `verify-canon.mjs CanonData\v1`, and with `-Export` `run-export.ps1 -Twice` under `.gpu.lock` |
| `worktree-new.ps1` | Worktree and branch from main; copies the DLL from the main checkout or runs `build-native.ps1`; disk, worktree-count and CRLF checks; next steps |
| `backup.ps1` | `git bundle create --all` plus a shared, deduplicated LFS object store; keeps 7; prunes unreferenced LFS copies; `-Verify`; `RESTORE.txt`; refuses destinations inside the repo or a worktree |
| `lfs-budget.ps1` | LFS history (`git lfs ls-files --all --json`) against 6 GiB, warning at 80 %; the local store; large non-LFS blobs |
| `notices.ps1` | `THIRD_PARTY_NOTICES.txt` and `licenses\` from the shipped entries; upstream licence files are used verbatim (V's `vendor\LICENSES\`); ODbL 4.3 notice; `-Check` |
| `tests\` | `ci-selftest.ps1`, `StubPlayer.cs` (compiled at test time), `fake-step.ps1`, fixtures |

Also:
- `licenses\`: MIT, BSD-2-Clause, BSD-3-Clause, Apache-2.0, OFL-1.1, ODbL-1.0 and BSL-1.0, written from knowledge, each marked "AGENT-WRITTEN COPY".
- `docs\approvals\QUEUE.md`: a "Licence texts" row (verify or fetch the canonical texts before any public build).
- `project-unity.ps1` and `project-setup.ps1` (lane I-a's helpers, arguments unchanged): they now also see the machine-wide `machine.lock` in `$LOCKS`.

## Contract as built (differs from the T0.6 brief, follows main)

1. **BuildCli flags** are `-profile`, `-milestone` and `-out` (PLAN 1.9 and lane I-a), not `-br-profile` and `-br-milestone`. Exit 3 means `.user-playing`, which the script reports as `blocked`.
2. **Folders:** `build-player.ps1` always passes `-out`. Client goes to `builds\M<n>\` (the user's build, Play.bat), Dev to `builds\M<n>-dev\` (autotests) and ServerHeadless to `builds\M<n>-server\`. That way the milestone tier has the Client and the Dev build at the same time. I-a's local default put Dev in `builds\M<n>\`.
3. **Play.bat comes from BuildCli.** I dropped my own template so there is a single source. The script checks that the file exists and has the lock line.
4. **Notices:** BuildCli writes a list-only `THIRD_PARTY_NOTICES.txt`; `build-player.ps1` replaces it with `notices.ps1` output (full licence texts plus `licenses\`).
5. **m0.crash.recover** runs inside the player, which relaunches itself, so `autotest.ps1` treats it like any other scenario. **Smoke** results are in `smoke.json`. Metrics are the flat scalar fields of `result.json`.
6. **Waiting for Unity and players:** ShellExecute (no inherited handles) and a wait on that process only. Child scripts write their output to a file instead of a pipe. The Unity Licensing client outlives the editor (I-a's deviation 16); the self-test reproduces this with a lingering grandchild.
7. **Who is "the user playing":**
   - a fresh `artifacts\.user-playing` (in the MAIN checkout; stale after 3 min with no game running);
   - any `Backroads.exe` that no script started. Scripts add the marker `-br-ci`. Players run from `D:\Backroads-Unity-wt\...` count as agents.

## Tested, and how

- **Self-test** `tools\ci\tests\ci-selftest.ps1` under Windows PowerShell 5.1.26100: **77 checks, 0 failed** (about 6 min; `-Quick` skips the stub-player group). It covers:
  - parsing, ASCII-only files and no 7-only operators;
  - the contract with the C# on main;
  - locks with real holder processes (held, refused, freed on exit and on kill, 4 slots, slots waiting during a perf gate, stale `.user-playing`);
  - NUnit parsing and the egress rules;
  - every `-DryRun` and the not-ready errors (exit 2);
  - real lfs-budget and notices runs;
  - backup into a temp folder (7 kept, LFS pruning, bundle verify, refused destinations);
  - gate composition with fixtures (fail-fast, `-KeepGoing`, `-Skip`, `-SkipOracle`, `requiresParam`, foreign steps, budget and `-StrictBudget`, nightly include and override, milestone headline vs lane, the `docs\gates` copy, refusal while playing);
  - worktree-new in a scratch clone;
  - end-to-end autotests against a stub player: pass, fail, liar, crash, hang/timeout, missing result, missing scenario, `-Fps`, affinity, `-Repeat` medians, `-MinSpec` (marked invalid when the machine is busy, by design), `-Smoke`, egress polling, refusal while playing, waiting for `.gpu.lock`, and a lingering grandchild.
- **Real runs on lane I-a's project** (this worktree, 2026-10-18; other lanes active):

| Run | Result | Wall clock |
|---|---|---|
| `gate.ps1 -Tier merge -SkipOracle` | **pass**, 7 steps passed, oracle skipped. EditMode 59/60 (1 skipped), PlayMode 1/1, server compile, Dev build 158 MiB, `smoke` autotest | **84 s** (budget 1200 s) |
| `gate.ps1 -Tier merge` (after merging main with lane W, 16:53) | **pass, all 8 steps**. Oracle `gen-hash` + `verify-canon` 68/68 in 1.9 s, EditMode **60/60**, PlayMode 1/1, server compile, Dev build, smoke | **100 s** |
| `gate.ps1 -Tier nightly -SkipOracle -Skip backup,native.asan` | **pass**, 16 steps passed, 3 skipped. Slow sets empty (0 tests, allowed), ServerHeadless 97.6 MiB, canary `SERVER BOOT OK` loopback only, LFS 112.6 MiB (1.8 %), notices, `m0.crash.recover`, `m0.net.egress` (60 s; only Dev PlayerConnection TCP 55000 and UDP, tolerated), `m0.playtest.f10` | 256 s |
| `gate.ps1 -Tier milestone -Gate M0 -SkipOracle` | **pass** (all headline steps). Nightly check, env-check, EditMode 60/60, Client 97.7 MiB without the testing assembly, `-br-smoke` on Client with **0 endpoints**, Dev, Server, canary, LFS, notices. Lane failure listed: `m0.playtest.f10` (see below). The `docs\gates\M0*` it wrote were deleted again: T0.9 makes them | 233 s |

## Needs other lanes, or not verified yet

- **`m0.playtest.f10` depends on window focus (lane I-a).** The test passed in the nightly and then failed four times in a row: "F10 did not open the checklist". `AutotestRunner.PressKey` queues `KeyboardState` events, and with the Input System's default background behaviour the keyboard is disabled while the player has no focus. A window started by a background script often cannot take the foreground while the user works. The fix belongs in C#: `InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus` (or a test-only device) in autotests. The scripts should not steal focus.
- **`ProjectSettings\MultiplayerManager.asset`: a build flipped `m_EnableMultiplayerRoles` from 0 to 1 (lane I-a).** I reverted it. `ProjectSetup.Apply` should write the value the builds want; otherwise every build dirties the tree, and the gate JSON then says `dirty: true`.
- **`m0.crash.recover` child (lane I-a):** the relaunched child does not carry `-br-ci`. Started from the main checkout's `builds\`, it counts as "the user playing" for a few seconds. Please forward `-br-ci` (or all `-br-*` flags) to the child.
- **BuildCli's `.user-playing` check (lane I-a)** looks in its own checkout's `artifacts\`. From a worktree it does not see the user's lock in the main checkout. The scripts check the main one before starting Unity, so builds through `build-player.ps1` are safe. For direct calls, BuildCli could check `git rev-parse --git-common-dir`\..\artifacts too.
- **`-br-quality <Tier>`** for `-MinSpec` is passed but not read by the player yet (QualityPreset, M1). The gate JSON carries a warning. `-MinSpec` against the real player is untested: only the stub is, because perf runs need an idle machine.
- **GPU check for perf validity:** `result.json` has no GPU name, so a run on the integrated GPU cannot be marked invalid yet. Suggest adding `SystemInfo.graphicsDeviceName` to AutotestRunner's result (M1).
- **Oracle export:** `oracle.ps1` (gen-hash + verify-canon) ran for real. `oracle.ps1 -Export` (`run-export.ps1 -Twice`) was not run from this lane: headless Edge loads three.js from jsDelivr, so the first live run belongs to T0.9, under the U2 approval W worked under. Only its command line and exit-code handling are tested (dry run and stub).
- **`native.asan`** (V's `build-native.ps1 -Asan`) is wired into the nightly but was not run (cold ASan build; skipped while other lanes work).
- **`backup.ps1` to the default `D:\Backroads-Unity-backups`** has not run; it was tested only in a temp folder, so no folder was created on the user's drive. U21 is still open.
- **`soak.ps1 -Clients N`** waits for BotClient (N lane, M4). `-ServerLinux` waits for U4b. The proposed bot flags are in the README.
- **Perf on battery and the "user plays mid-gate" pause** are tested with synthetic locks, not live.
- **Licence texts:** agent-written; verify before any public build (QUEUE row).

## Next

- Coordinator: merge `m0/i-scripts` (all paths are lane I's, except the two I-a helper lines above). Then `tools\ci\gate.ps1 -Tier merge` replaces ad-hoc checks for every lane merge (the oracle step is live now that W is on main).
- T0.9: `gate.ps1 -Tier nightly` (on main, with backup once U21 is answered), then `gate.ps1 -Tier milestone -Gate M0`, then commit `docs\gates\M0.json` and `docs\gates\M0\` with `docs\milestones\M0.md`.
- M1: add the M1 autotests to `gates.json` (`milestones.M1` is pre-filled: `m1.shots.canon`, `m1.bench.revcycle` at 60 and 30 fps, `m1.perf.flythrough` plain and `-MinSpec`), `-br-quality`, the GPU name in `result.json`, and the notices copy for the Credits panel.

## Resume

```powershell
cd D:\Backroads-Unity-wt\ib                       # branch m0/i-scripts
git log --oneline -3
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ci\tests\ci-selftest.ps1 -Quick   # expect 0 failed
tools\ci\gate.ps1 -Tier merge                     # expect pass, 8 steps, about 1.5-2 min
tools\ci\gate.ps1 -Tier nightly -Skip backup,native.asan
tools\ci\gate.ps1 -Tier milestone -Gate M0 -SkipOracle   # -SkipOracle skips only the live export; writes docs\gates\M0*: do not commit them from this lane
```

If the worktree is gone: `git -C D:\Backroads-Unity worktree add D:\Backroads-Unity-wt\ib m0/i-scripts`, then `tools\ci\build-native.ps1` before anything opens Unity.
