# tools\ci: build, test and gate scripts

PLAN 1.9 (CI and batchmode scripts), 1.10 (gate JSON, lanes, locks), 1.11 (operating model), 1.1 (backups), 1.2 (LFS budget).
Run everything from the repo root (or a lane worktree) in PowerShell. Every script works in Windows PowerShell 5.1.

## Common rules

- Every script dot-sources `env.ps1` (tool paths; the only place the Unity version is written) and `ci-lib.ps1` (shared helpers).
- Output: `artifacts\<yyyyMMdd-HHmm>\<gate>\` (logs, results, `gate.json`) and the latest result per gate in `artifacts\gates\<gate>.json`.
- `-DryRun` on every script prints the commands it would run and starts nothing.
- `-WaitForUser` (heavy scripts): wait while the user plays instead of refusing.

| Exit code | Gate status | Meaning |
|---|---|---|
| 0 | `pass` (also `skipped`, `dryrun`) | Green |
| 1 | `fail` | A hard check failed |
| 2 | `error` | Could not run: a tool, the Unity project, a build or the C# side is missing. The message says which. |
| 3 | `blocked` | A lock: the user is playing, or a lock did not free up in time |
| 4 | `invalid` | A perf run on battery, on the integrated GPU or on a busy machine. It is not a failure; re-run it later. |

## Locks (machine-wide, in the MAIN checkout's `artifacts\`, shared by every worktree)

| Lock | Taken by | Rule |
|---|---|---|
| `.user-playing` | `Play.bat` (written by BuildCli) | Heavy scripts refuse to start (exit 3), and `gate.ps1` pauses before each heavy step. The user also counts as playing while a `Backroads.exe` runs that no script started. Scripts mark their players with `-br-ci`; players run from a lane worktree (`D:\Backroads-Unity-wt\...`) count as agents. A lock older than 3 minutes with no game running is stale and is removed. |
| `machine.lock` | perf autotests (`-Perf`, `-MinSpec`, ids with `.perf.`) | The holder also takes every Unity slot and `.gpu.lock`, then waits for an idle machine (no other Unity, Blender, player or native build). Others wait. |
| `.gpu.lock` | every player autotest | One GPU or audio autotest at a time |
| `.unity-slots\slot-1..4.lock` | `unity-tests.ps1`, `build-player.ps1` | At most 4 Unity processes started by scripts |

These locks are held through an open file handle, so a crashed holder frees its lock. Leftover lock files are deleted the next time a script checks them, so scripts that only test whether the file exists (`build-native.ps1`, `project-unity.ps1`) see the truth. `Get-BrLockStatus` (from `ci-lib.ps1`) shows who holds what.

## Scripts

### unity-tests.ps1: Unity Test Framework in batchmode

`Unity.exe -batchmode [-nographics] -projectPath Game -runTests -testPlatform <P> -testCategory <cats> -testResults <xml> -logFile <log>`. It never adds `-quit`. It parses the NUnit 3 XML into the gate JSON: counts, failures, and counts per category.

| Switch | Default | |
|---|---|---|
| `-Platform EditMode\|PlayMode` | required | EditMode always runs with `-nographics` |
| `-Category` | `!Slow;!PlayerOnly` | Semicolon list. `!X` excludes. Plain names are matched exactly (`^X$`). `all` means no filter. |
| `-TestFilter` | none | `-testFilter` (test names, regex) |
| `-MinTests` | 1 | Fewer executed tests than this is a failure. Use 0 for optional sets such as `Slow`. |
| `-NoGraphics` | off | PlayMode without rendering |
| `-TimeoutMin` | 45 | |

Before Unity starts, it runs `build-native.ps1` if the plugin DLL is missing; otherwise Unity would delete the committed `.meta` files. A licence error in the log is reported as "please open Unity Hub and sign in".

```powershell
tools\ci\unity-tests.ps1 -Platform EditMode
tools\ci\unity-tests.ps1 -Platform PlayMode -Category "Slow" -MinTests 0
tools\ci\unity-tests.ps1 -Platform EditMode -Category "Repo;Architecture"
```

### build-player.ps1: player builds through BuildCli

`-executeMethod Backroads.Build.Editor.BuildCli.Build -profile <P> -milestone M<n> -out <dir>`. BuildCli writes the player, `Play.bat`, `PLAYTEST.md`, `Server.bat` for servers, `<Product>_Data\StreamingAssets\build.json` and `artifacts\build-<profile>-<milestone>.json`. The script then checks the result: the exe, `build.json`, the `Play.bat` lock line, and the testing assembly (present in Dev, absent in Client). It also replaces BuildCli's list-only notices with the output of `notices.ps1`.

| Profile | Folder | Use |
|---|---|---|
| `Client` | `builds\M<n>\` | The user's build (`Play.bat`), `-br-smoke` |
| `Dev` | `builds\M<n>-dev\` | Autotests (`Backroads.Testing`, Development player) |
| `ServerHeadless` | `builds\M<n>-server\` | `BackroadsServer.exe`, server canary and soaks |

Switches: `-Profile`, `-Milestone` (default: from the branch `m<n>/...`, else the tag after the newest `m<n>`, else M0), `-BuildDir` (override the folder), `-CompileServerScripts` (rule 7 check `BuildCli.CompileServerScripts`; builds nothing), `-SkipNotices`, `-TimeoutMin`. BuildCli exit 3 (the user started playing) is reported as `blocked`.

```powershell
tools\ci\build-player.ps1 -Profile Dev -Milestone M0
tools\ci\build-player.ps1 -Profile Client
tools\ci\build-player.ps1 -Profile ServerHeadless
tools\ci\build-player.ps1 -CompileServerScripts
```

### autotest.ps1: player autotests

`<build>\Backroads.exe -br-autotest <id>` (Dev builds; reads `StreamingAssets\Autotest\<id>.json`, writes `result.json`) or `-br-smoke` (every build; writes `smoke.json`). The exit code decides: 0 passes, 1 fails. A crash or a timeout fails. So does exit 0 without a result file, or with a result file that says `fail`. The scalar fields of the result file (`frames`, `p95FrameMs`, `exceptions`, ...) go into `metrics`.

| Switch | |
|---|---|
| `-Scenario <id>` or `-Smoke [-SmokeSeconds N]` | `m<n>.<area>.<case>`, or `smoke` |
| `-Profile Dev\|Client`, `-Milestone`, `-Exe` | Default: Dev for `-Scenario`, Client for `-Smoke`. `-Exe` picks a player directly. |
| `-Fps 30\|45\|60` | `-br-fps-cap N`; output folder `<id>-fpsN` |
| `-MinSpec` | Min-spec proxy: affinity = first 6 E-cores from `artifacts\env.json` (env-check.ps1), `-br-quality Low`, `-br-fps-cap 30`, 1920x1080. A perf run. |
| `-Perf`, `-Repeat N`, `-IdleTimeoutSec`, `-Force` | Perf: `machine.lock`, idle machine, mains power. `-Repeat`: pass by majority, `metrics.median` per numeric field. |
| `-AffinityMask 0x..`, `-Quality`, `-Width/-Height` (2560x1440), `-ExtraArgs`, `-TimeoutSec` (600), `-Loud` (no `-br-silent`) | |
| `-NoEgressCheck` | For `m0.net.egress` and every `-Smoke` run, the script polls the player's sockets (`Get-NetTCPConnection` / `Get-NetUDPEndpoint`). Anything other than loopback fails, except Unity's PlayerConnection (54997-55511) in Development players. |

Output: `artifacts\autotest\<id>[-fpsN|-minspec]\`. The previous run moves to `.prev`. Every gate JSON names the quality tier, the core class and the fps cap (PLAN 1.10).

```powershell
tools\ci\autotest.ps1 -Scenario smoke
tools\ci\autotest.ps1 -Smoke -Profile Client
tools\ci\autotest.ps1 -Scenario m0.crash.recover
tools\ci\autotest.ps1 -Scenario m1.bench.revcycle -Fps 30
tools\ci\autotest.ps1 -Scenario m1.perf.flythrough -MinSpec -Repeat 3
```

### soak.ps1: server canary and multiplayer soaks (everything on 127.0.0.1)

`-Clients 0` is the server canary: `BackroadsServer.exe -batchmode -nographics -br-bind 127.0.0.1 -br-quit-after <s>`. It passes on exit 0, a `SERVER BOOT OK` line in the log, and loopback-only sockets. `-Minutes N` keeps the server up N minutes; the default is 10 s. `-Clients N` with `-Net latencyMs,jitterMs,lossPercent` needs the bot client (N lane, M4). Until then the script refuses, and `-DryRun` shows the planned bot command line (proposed flags: `-br-bot -br-connect 127.0.0.1:<port> -br-net`). `-ServerLinux` runs the server in WSL2 and needs U4b. From M4, every participant's `hashes.log` must be identical.

```powershell
tools\ci\soak.ps1 -Clients 0
tools\ci\soak.ps1 -Clients 4 -Minutes 10 -Net 150,20,2 -DryRun
```

### gate.ps1: the three tiers

| Tier | Budget | Runs |
|---|---|---|
| `-Tier merge` | 20 min | oracle hash, vendor check, native ctest, EditMode, PlayMode `!Slow`, server compile check, Dev build, `smoke` autotest. It stops at the first failed hard step; `-KeepGoing` runs everything. |
| `-Tier nightly` | 3 h | the merge steps plus `Slow` tests, ASan native build, ServerHeadless build and canary, LFS budget, notices check, backup, and the milestone's extra autotests |
| `-Tier milestone -Gate M<n>` | none | last green nightly (an ancestor of HEAD), env check, oracle export, vendor and native, EditMode, Client, Dev and Server builds, `-br-smoke` on the Client build, server canary, LFS, notices, and the milestone's autotests. Only headline steps decide; failed lane gates are listed in `laneGateFailures`. It copies the JSON to `docs\gates\M<n>.json` and `docs\gates\M<n>\`. |

The steps live in `gates.json` (tier steps, `include`, and per-milestone additions under `milestones`). Step flags: `gateJson`, `heavy`, `perf`, `oracle`, `hard`, `headline`, `requiresParam`, `owner`, `fallbackIfFail`. Other switches:
- `-SkipOracle`
- `-Only a,b` and `-Skip a,b` (wildcards allowed)
- `-StrictBudget` (a green run over budget fails; otherwise it is only a warning)
- `-SkipNightlyCheck`
- `-OwnersBase main` (sets `BR_OWNERS_BASE` for `Repo.OwnersRespected`)
- `-Config`

Steps that belong to other lanes (`vendor-check.ps1`, `build-native.ps1`, `env-check.ps1`) are judged by their exit code, and gate.ps1 writes their gate JSON. A missing script is an `error` naming its lane.

```powershell
tools\ci\gate.ps1 -Tier merge
tools\ci\gate.ps1 -Tier merge -SkipOracle -DryRun
tools\ci\gate.ps1 -Tier nightly -WaitForUser
tools\ci\gate.ps1 -Tier milestone -Gate M0
```

### oracle.ps1: oracle check (wrapper around lane W's tools)

Runs `node tools\oracle\make-snapshot.mjs` (only when `artifacts\oracle\552f780\` is missing, or with `-Fresh`), then `node tools\oracle\gen-hash.mjs`. With `-Export` it also runs `tools\oracle\run-export.ps1`. It never touches `D:\Backroads`. If W's files are not on the branch yet, it fails and says so; use `gate.ps1 -SkipOracle` until then.

### worktree-new.ps1: a lane worktree

`-Lane <x> -Topic <t> [-Milestone M<n>]` creates `D:\Backroads-Unity-wt\<lane>` on branch `m<n>/<lane>-<topic>` from `main` (`-Base`, `-Root` to change). It copies `EngineSimPlugin.dll` and its version file from the main checkout when the new worktree has none. If there is nothing to copy, it runs the new worktree's `build-native.ps1` (`-NoNativeBuild` skips this). It warns below 40 GB free or above 6 worktrees, checks hashed trees for CRLF, and prints the next steps and the time taken.

```powershell
tools\ci\worktree-new.ps1 -Lane w -Topic terrain
tools\ci\worktree-new.ps1 -Lane r -Topic lanegraph -Milestone M3 -DryRun
```

### backup.ps1: nightly backup until a remote exists

`git bundle create --all` plus the LFS objects. Each object is copied once into `<Dest>\lfs-objects\`, and each snapshot lists its objects in `lfs-oids.txt`. The script keeps the newest 7 snapshots (`-Keep`) and drops LFS copies that no kept snapshot lists. `-Verify` re-hashes every LFS copy. `-Dest` defaults to `D:\Backroads-Unity-backups` until the user names a second drive (QUEUE U21). It refuses a destination inside the repository or a worktree. Each snapshot has a `RESTORE.txt`. Only committed work is saved.

```powershell
tools\ci\backup.ps1
tools\ci\backup.ps1 -Dest E:\BackroadsBackups -Verify
```

### lfs-budget.ps1: LFS size gate

It fails when the LFS objects reachable from any ref (`git lfs ls-files --all`, which is what GitHub would store) exceed 6 GiB (`-MaxGiB`). It warns at 80 %. It also reports the local store `.git\lfs\objects` (and suggests `git lfs prune` when the store is much larger), sizes per folder, the largest objects, and blobs over 5 MiB committed without LFS.

### notices.ps1: THIRD_PARTY_NOTICES.txt and licenses\

It builds the notices from the shipped entries of `third-party.json`. Each entry gets name, version, licence, copyright and source, plus the ODbL 4.3 produced-work notice and CC-BY attributions from `assets-provenance.json`. Full licence texts are included for MIT, BSD, Apache-2.0, OFL-1.1, ODbL-1.0 and BSL-1.0. Each text comes from the first of these that exists:
1. the entry's `licenceFile`;
2. the vendored upstream file (`native\EngineSimPlugin\vendor\LICENSES\<name>-*`, or `LICENSE*` / `COPYING` / `OFL.txt` at the root of the entry's `files`);
3. `licenses\<id>.txt`.

The copies in `licenses\` are agent-written from knowledge, are marked as such, and are reported as unverified until they are checked against the canonical sources (QUEUE). `-Check` validates only. Every shipped entry needs a licence and a source; MIT and BSD entries also need a copyright line or an upstream file.

```powershell
tools\ci\notices.ps1 -Check
tools\ci\notices.ps1 -OutDir builds\M0
```

### Other lanes' scripts called by the gates

- `env-check.ps1` (lane I-a): writes `artifacts\env.json`, including the E-core map that `-MinSpec` reads.
- `build-native.ps1` (V): DLL, ctest, `-Asan`.
- `vendor-check.ps1` (V).
- `project-unity.ps1` and `project-setup.ps1` (I-a's thin helpers, kept unchanged).

## Player command line (contract with BuildCli, AutotestRunner, SmokeRunner, ServerMain)

| Flag | Who reads it |
|---|---|
| `-br-autotest <id>`, `-br-out <dir>`, `-br-silent`, `-br-fps-cap N` | AutotestRunner (Dev builds) |
| `-br-smoke [-br-smoke-seconds N]`, `-br-out` | SmokeRunner (every build) |
| `-br-data <dir>` | CrashCollector, SessionMetrics (default `<out>\data` when `-br-out` is given) |
| `-br-crash-test` | CrashCollector |
| `-br-bind`, `-br-quit-after`, `-br-server-fps` | ServerMain |
| `-br-ci` | Marker only: "started by a script". The player ignores it; the lock logic reads it. |
| `-br-quality <Tier>` | Proposed for the min-spec proxy (QualityPreset, M1); not read by the player yet |
| `-br-bot`, `-br-connect`, `-br-net`, `-br-port` | Proposed for soaks (BotClient, M4); not implemented |

Result files: `result.json` / `smoke.json` with `status` (`pass` / `fail`), `failures[]`, `endpoints[]`, `developmentBuild` and the scalar metrics. Log lines: `[BR] SMOKE OK`, `[BR] AUTOTEST <id> PASS|FAIL`, `SERVER BOOT OK`.

Starting Unity and players: the scripts use ShellExecute, which inherits no handles, and wait for that process only. The Unity Licensing client outlives the editor; that is why `Start-Process -Wait` hangs, and why piping a child's output could hang a caller. `Invoke-BrChildScript` sends a child script's output to a file and echoes it while waiting for the child process itself.

## Gate JSON (PLAN 1.10)

`{schema, gate, tier, status, hard, headline, message, startedAt, finishedAt, build{git, branch, dirty, unity, native, abi}, machine{summary, power, batteryPercent, gpu{active, discretePresent, controllers[]}, powerMode{overlayAc, overlayDc, scheme}, cores{logical, p[], e[], minSpecMask}, cpuLoadPercent, otherProcs[]}, wallClock{seconds, budget, pausedSeconds, overBudget}, conditions{}, metrics{}, thresholds{}, evidence[], warnings[], fallbackIfFail, exitCode, out, run, script}`. Tier results add `metrics.steps[]` (id, status, hard, headline, seconds, message, json) and `metrics.counts`.

## Self-test

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ci\tests\ci-selftest.ps1          # about 6 min
powershell -NoProfile -ExecutionPolicy Bypass -File tools\ci\tests\ci-selftest.ps1 -Quick   # without the stub-player runs
```

The self-test checks 5.1 parsing, ASCII-only files, no 7-only operators, and the C# contract (BuildCli arguments, player switches, scenario files). It also covers the locks (with real holder processes), NUnit parsing, the egress rules, and every `-DryRun`. Further groups:
- the not-ready errors, lfs-budget, notices, and backup with pruning;
- gate composition, using `tests\fixtures\gates-test.json` and `tests\fake-step.ps1`;
- worktree-new, in a scratch clone;
- end-to-end autotests against `tests\StubPlayer.cs`, compiled at test time.

It uses scratch locks (`BR_LOCKS_DIR`), so the real ones are never touched, and it restores `artifacts\gates\merge.json` and `nightly.json`. Run it after every change in `tools\ci`.
