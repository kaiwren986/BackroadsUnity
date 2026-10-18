# M0 handoff, lane V (vehicles + native plugin): T0.3 and T0.4

Branch `m0/v-native` in worktree `D:\Backroads-Unity-wt\v`, cut from `main` at `dd412fa`. Not merged, not pushed.
`main` has since gained `d684b73`, which touches none of the files below.

## Done

**T0.3 Preserve probe evidence** (commit `3d9fa42`)
- `native\EngineSimPlugin\probes\p02\` from `D:\Backroads-ref\probe`: `CMakeLists.txt`, `build.ps1`, `src\{ej25.cpp, ej25.h, probe.cpp, wav.cpp, wav.h}`, `patched\synthesizer.cpp`.
- `native\EngineSimPlugin\probes\p01\` from `D:\Backroads-ref\probe01` (U20 was done): `CMakeLists.txt`, `bench.cpp`, `piranha_path_std.cpp`, `shim\delta-studio\...` (2 headers).
- Left out: WAVs, `run_*.txt` logs, `build*\` folders, `*.log`, `out-*\`.

**T0.4 Vendor engine-sim and the CMake skeleton** (commits `7f66d5f`, `9d11726`)
- `vendor\engine-sim\`: 584 files taken with `git -c core.autocrlf=false archive -o <tar>`. That is engine-sim `85f7c3b` (`src include scripting es assets/engines LICENSE`, 249 files), simple-2d-constraint-solver `e009f4f` (74) and piranha `432f0b1` (261). They sit in the upstream layout (`vendor\engine-sim\dependencies\submodules\<name>\`).
- `vendor\VENDOR.md`: commits, `rev-parse HEAD`, `submodule status`, the exact commands, the line-ending and LFS notes, the licence table and the piranha licence note.
- `vendor\LICENSES\`: `engine-sim-LICENSE`, `simple-2d-constraint-solver-LICENSE`, `piranha-LICENSE`, `flex-NOTICE.txt`.
- `tools\ci\vendor-check.ps1`: compares `git ls-tree -r` blob ids at the pinned commits with `git hash-object --no-filters` of every vendored file. It also checks the gitlinks in `85f7c3b`, verifies each LICENSES file against its source, rejects any other file under `vendor\`, and applies the CR rule.
- `shim\delta-studio\include\yds_core.h`, `shim\delta-studio\engines\basic\include\delta_basic_engine.h`: no declarations, one comment line each.
- `src\EngineSimPlugin.h`, `src\esp_api.cpp`: `esp_version()` returns 1. `esp_build_info()` returns one-line JSON (abi, git, sourceHash, piranha, asan, crt, fp, ndebug, msvc, vendor commits). There is also `esp_debug_crash_test(magic)`.
- `src\crash_guard.h/.cpp`: `esp::run_guarded`, `esp::start_guarded_thread`. The SEH filter writes one minidump from a helper thread to `%TEMP%\BRDev\Backroads\Crashes\esp-<time>-<pid>-<tid>.dmp` (`BR_CRASH_DIR` overrides the folder), then returns `EXCEPTION_CONTINUE_SEARCH` so Unity's own crash handling runs.
- `CMakeLists.txt`: targets `scs` (29, count asserted), `enginesim_core` (41, count asserted, shim first), `EngineSimPlugin` (shared) and tests `esp_smoke` and `esp_crash_guard`. Flags are `/MT`, `NDEBUG` always, `/O2 /fp:fast /MP /Zi`, C++17, `/W1` on vendor code and `/W4 /permissive-` on ours.
- `CMakePresets.json`: configure `vs2022-x64` → `native\build` and `vs2022-x64-asan` → `native\build-asan`; build and test presets `release` and `release-asan`.
- `tools\ci\build-native.ps1`:
  - Steps: dot-sources `env.ps1`, configures, builds Release with `--parallel 16`, runs `ctest --output-on-failure`, then installs `EngineSimPlugin.dll` and `EngineSimPlugin.version.json` `{abi, git, sourceHash, built, piranha}` into `Game\Assets\Plugins\x86_64\` (it creates the folder).
  - Version file: abi, git and sourceHash are read back from the DLL's own `esp_build_info()`.
  - Cache: `<main checkout>\artifacts\native-cache\<sourceHash>\`. The source hash covers every tracked or untracked-not-ignored file under `native\EngineSimPlugin` except `probes\`, plus the script itself. On a cache hit it skips the build and re-checks the installed copy with the cached `esp_smoke.exe`.
  - `-Asan` builds and tests only. `-Force` rebuilds.
  - Exit 3 when `machine.lock` exists; at most `-Parallel 4` while `.user-playing` exists; exit 4 (with a message) when the installed DLL is locked.
  - Writes `artifacts\<stamp>\build-native.log` and `artifacts\gates\build-native.json`.
- `.gitignore`: `Game/Assets/Plugins/x86_64/*.dll`, `*.pdb`, `EngineSimPlugin.version.json`. Neither the DLL nor the version file is committed.

## Verification (all on 2026-10-18)

| Check | Command | Result |
|---|---|---|
| Vendor integrity | `tools\ci\vendor-check.ps1` | `584 vendored files vs 584 upstream blobs (engine-sim 249, scs 74, piranha 261), 4 licence files; 0 differences (PASS)`, exit 0 |
| Negative test | edited, added, renamed files; CR in `VENDOR.md`; extra licence file | 7 differences reported, exit 1; back to 0 after restoring |
| Fresh checkout with `autocrlf=true` | `git -c core.autocrlf=true worktree add --detach <tmp> HEAD`, then both scripts there | vendor-check 0 differences; same source hash `5adbd0db…`, so build-native was a cache hit, esp_smoke OK (temporary worktree removed) |
| Native build | `tools\ci\build-native.ps1` (clean `native\build`) | PASS in 6.2 s, 0 compiler warnings, 74 `.cpp` compiled (29 + 41 + 2 + 2) |
| ctest | (inside build-native) | `esp_smoke Passed 0.07 s`, `esp_crash_guard Passed 0.12 s`, `100% tests passed, 0 tests failed out of 2` |
| Installed | `Game\Assets\Plugins\x86_64\` | `EngineSimPlugin.dll` (244,736 bytes) + version file `{abi 1, git 9d11726…, sourceHash 5adbd0db…, piranha false}` |
| DLL imports | esp_smoke | `dbghelp.dll, KERNEL32.dll` only (static CRT) |
| Exports | `dumpbin /exports` | `esp_build_info`, `esp_debug_crash_test`, `esp_version` |
| Cache hit | second run, unchanged sources | `cache hit … (unchanged sources; no rebuild)`, PASS in 0.6 s |
| Locked DLL | run while a handle holds the installed DLL | "is locked, most likely by an open Unity editor", exit 4 |
| ASan preset | `tools\ci\build-native.ps1 -Asan` | PASS, esp_smoke passed, 0 warnings (esp_crash_guard is not registered under ASan) |
| CR bytes | vendor-check `filesWithCrList` | no vendored text file has a CR. 16 binary files do (6 PNG in `piranha\docs\public\`, 10 IR WAVs in `es\sound-library\`), identical to their upstream blobs. `VENDOR.md` and `LICENSES\*`: none |

## Deviations from the plan, and why

1. **piranha LICENSE without a download (U3e mostly done).** The local piranha clone is shallow and lacks `004fa15`, but it holds `origin/master` `ff2955d`, whose `LICENSE` is blob `231f3f1b…`. That is MIT, "Copyright (c) 2022 Ange Yaghi", byte-identical to scs's LICENSE. `vendor\LICENSES\piranha-LICENSE` was copied from it with `git cat-file`. The only part of U3e still open is confirming that `004fa15:LICENSE` is the same blob (the compare URL is in `VENDOR.md`). **For I:** update the U3e row in `docs\approvals\QUEUE.md`.
2. **`LICENSES\` holds four verified copies.** vendor-check checks each one: engine-sim and scs LICENSE against their blobs, piranha against `231f3f1b`, and the flex notice against lines 5-28 of the vendored `FlexLexer.h`. `piranha-LICENSE` is the only file with no counterpart at the vendored commits, which is how I read "the only allowed extra" in §1.6.
3. **`third-party.json` paths are wrong for this layout.** The submodules sit under `vendor/engine-sim/dependencies/submodules/` so upstream's relative includes resolve. `third-party.json` (lane I) still says `vendor/simple-2d-constraint-solver/**` and `vendor/piranha/**`. **For I:** change these to `native/EngineSimPlugin/vendor/engine-sim/dependencies/submodules/{simple-2d-constraint-solver,piranha}/**`.
4. **Extra export `esp_debug_crash_test(int32_t magic)` and extra ctest `esp_crash_guard`.** `m0.crash.recover` needs `-br-crash-test` to "force a native fault on an engine worker". With `magic != 0x43524153` the call does nothing. The ABI is frozen only at M1, so ADR-002 should list it.
5. **`/WHOLEARCHIVE` on the core (`ESP_LINK_WHOLE_CORE=ON`).** Nothing calls engine-sim yet, so the linker would otherwise drop it. With the option, every core and scs object must resolve inside the DLL. `/OPT:REF` still trims the DLL to 244 KB. T1.6 should turn the option off.
6. **Crash folder is compiled in.** It defaults to `%TEMP%\BRDev\Backroads\Crashes`, the GameInfo placeholders, with an env override `BR_CRASH_DIR`. §3.3 has no call for passing company and product. **For T1.6/T1.7:** add one (for example `esp_set_crash_dir`) before ADR-002 if U18 names change.
7. **Cache location.** The cache is in the **main checkout's** `artifacts\native-cache`, not the worktree's, so `worktree-new.ps1` can restore from it. So build-native writes (gitignored) files outside the worktree that runs it.
8. **Dirty builds are not reused after commit.** A cache entry built from uncommitted sources is reused only while the sources stay uncommitted. After a commit the next run rebuilds (one file) so the DLL carries the commit id. A clean entry keeps the first commit its sources were built at. `-Force` always rebuilds; the milestone tag build should use it.
9. **Probe text files normalised to LF.** `* text=auto eol=lf` converted `bench.cpp` and `patched\synthesizer.cpp` (CRLF in the source folders) on commit. `probes\` is not a hashed tree.
10. **The version file has an extra `piranha` field**, per §1.6 ("piranha on/off").

## Notes for other lanes

- **Repo.NoCrInHashedFiles (I, T0.6):** must skip binary files under `vendor\` (the 16 PNG and WAV above), or accept files that vendor-check proves equal to upstream. Otherwise it fails on upstream bytes.
- **LFS:** the 16 vendored `.wav` and `.png` files (1.2 MB) go through LFS per `.gitattributes`. vendor-check reads working-tree bytes, so a checkout without LFS smudge fails it, as it should.
- **Unity import (I, T0.5/T0.6):** `Game\` does not exist on this branch. The future `EngineSimPlugin.dll.meta` (Windows x86_64 + Editor) must be committed. The DLL itself is gitignored until a milestone tag (`git add -f`), so a clone without a build has an orphan `.meta`: run `build-native.ps1` before opening the project (`worktree-new.ps1` does this).
- **Cosmetic:** `dumpbin` shows `esp_version = DllMain` because `/OPT:ICF` folded two identical `return 1` bodies. Harmless.

## Next (lane V)

- T1.6 (PLAN "First two weeks"): `UnitySim`, worker + step-indexed rings, renderSync, FIR, WAV reader + resampler, builtins (`f4_boxer_a` from `probes\p02\src\ej25.cpp`; `v8_cross_a`, `i4_synth` from `probes\p01\bench.cpp`), `esp_cli`, the M1 ctest suite, ASan soak. Turn off `ESP_LINK_WHOLE_CORE` once the core is called. Start every plugin thread with `esp::start_guarded_thread`.
- If U3c is approved: `generated\piranha\`, `-DESP_WITH_PIRANHA=ON` (today it stops with a clear message).

## Resume

```powershell
cd D:\Backroads-Unity-wt\v                     # worktree of branch m0/v-native
git log --oneline -5
tools\ci\vendor-check.ps1                       # expect: 0 differences, exit 0
tools\ci\build-native.ps1                       # expect: PASS, 2/2 ctest, DLL + version file in Game\Assets\Plugins\x86_64
tools\ci\build-native.ps1 -Asan                 # ASan preset (native\build-asan), tests only
```

If the worktree is gone: `git -C D:\Backroads-Unity worktree add D:\Backroads-Unity-wt\v m0/v-native`, then the same commands.
