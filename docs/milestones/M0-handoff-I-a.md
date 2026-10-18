# M0 handoff, lane I-a (integrator, Unity side): T0.2, T0.5, T0.6 (Unity/C#), T0.8

Branch `m0/i-project` in worktree `D:\Backroads-Unity-wt\i`. It was cut from `main` at `dd412fa` and fast-forwarded to `8626384` (lane V merged) before the first commit. Not merged, not pushed. The CI scripts of T0.6 (`unity-tests.ps1`, `build-player.ps1`, `autotest.ps1`, `gate.ps1`, ...) belong to lane I-b and are not on this branch.

## Done

**T0.2 Environment check.** `tools\ci\env-check.ps1` writes `artifacts\env.json` and exits 0. It records:
- Unity 6000.3.20f1 and its `WindowsStandaloneSupport\Variations`: Mono only, x86/x64/arm64, development and non-development. No IL2CPP, no dedicated-server variation.
- Blender 4.5.12 LTS, Node v24.18.0, CMake 4.3.4, git 2.55.0 with git-lfs 3.7.1.
- VS 2022 Community 17.14 with the VC x64 tools (found with `vswhere -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64`), Edge 155.
- The WSL2 distro (Ubuntu-24.04), the power source and power plan (read only).
- The E-core map from `GetLogicalProcessorInformationEx` through a small Add-Type helper: E = logical 2-9 and 14-21 (16), P = 0, 1, 10-13, 22, 23 (8). `minSpecAffinityMask` = the first 6 E-cores.
- The licence scan of `artifacts\create.log`. On a real licence error it stops with "please open Unity Hub and sign in". The line "Access token is unavailable; failed to update" appears in every batchmode log on this machine while the entitlement still resolves, so it is recorded but only fatal when no entitlement resolved.

**T0.5 Unity project.** `Game\` was created with `-createProject -cloneFromTemplate ...\com.unity.template.3d-cross-platform-17.0.14.tgz`. The template was accepted (no fallback), took 82 s, and the log has no licence error. `Packages\manifest.json` is pinned to bundled versions only (deviations below). `Backroads.Build.Editor.ProjectSetup.Apply` writes every §1.3 setting:
- Linear colour; Forward+ on every Universal Renderer (Mobile was Forward); SRP Batcher; GPU Resident Drawer = Instanced Drawing; BRG variants Keep All; no dynamic batching.
- Enter Play Mode with no domain and no scene reload.
- Fixed Timestep exactly 1/120 (Unity 6 stores it as the rational 1176000/141120000); maximumDeltaTime 4/120; TGS solver.
- Input System only (`com.unity.inputsystem` 1.19.0); 48 kHz with Best latency (256 samples).
- Windows x64, Mono, Burst AOT on, Force Text, visible meta files.
- `submitAnalytics` off, Cloud Diagnostics off, Unity Connect, Analytics, Insights, Engine Diagnostics and Performance Reporting off, insecure HTTP not allowed.
- `GameInfo.asset` with the placeholders BRDev / Backroads, copied into the company and product names.
- The 9 physics layers (8-16) and the 06 §5.2 collision matrix.
- The `EngineSimPlugin.dll` importer: Editor + Win64 only, define constraint `!UNITY_SERVER`.
- The runtime UI Toolkit `PanelSettings` and theme, the TestYard scene, and the build scene list.

`Build.ProjectSettings` asserts each of these as its own test case (43). Run it with `tools\ci\project-setup.ps1`.

**T0.6 Unity parts.**
- **Assemblies.** All 25 asmdefs of §1.5 sit under `Game\Assets\Backroads\<Area>\{Runtime,Editor}\` (tests under `Tests\EditMode|PlayMode|Perf`). Each has the table's references, `!UNITY_SERVER` on client assemblies, `UNITY_SERVER` on Server, `BACKROADS_STEAM` on Net.Steam and `BACKROADS_TESTING` on Testing, plus `allowUnsafeCode` on EngineSim and Vehicles. Assemblies without code yet hold an `AssemblyMarker` class.
- **Core.** `BrCoords` (03 §2), `CommandLine`, `Log`, `GameInfo`, `PresentationRegistry`, `BrPaths`, `BuildInfo` (version stamp), `CrashNotice`.
- **Game (every build).**
  - `GameBootstrap`.
  - `CrashCollector`. It writes `session.lock` and, at the next boot, collects the previous log, the Unity `Crashes` folder and the plugin log into `playtest\crash-<time>\` with `crash-info.json`. The `-br-crash-test` switch throws a managed exception and then ends the process without a clean exit.
  - `SessionMetrics` (frame-time histogram plus counters), `SmokeRunner` (`-br-smoke`), `NetEgressProbe`, `Turntable`.
- **Server.** `ServerMain` logs `SERVER BOOT OK`, reads `-br-bind` (default 127.0.0.1) and quits with exit 0 after `-br-quit-after N`.
- **Client.UI.**
  - Plugs in through `PresentationRegistry`.
  - The version stamp sits at the bottom left: milestone, git hash, profile, canon, esp.
  - The crash notice.
  - The **F10 PlaytestChecklist** in UI Toolkit: Yes / No / comment for each item of `StreamingAssets\Playtest\M0.json` ("Did the game open?", "Did F10 show this list?"). Answers save on every change to `<data>\playtest\<build id>.json`.
- **Testing (Dev builds).**
  - `AutotestRunner` reads `-br-autotest <id>` and `StreamingAssets\Autotest\<id>.json`, writes `result.json`, `frametimes.csv`, screenshots and a contact sheet, then exits 0 or 1. It has four scenarios:
    - `smoke`: boot, 3 s, screenshot.
    - `m0.crash.recover`.
    - `m0.net.egress`.
    - `m0.playtest.f10`, an extra one.
  - `MetricsRecorder`, `ContactSheet`.
- **Build.Editor.**
  - `BuildCli.Build` takes `-profile Dev|Client|ServerHeadless`, `-milestone M<n>` and an optional `-out`.
    - Dev and Client go to `builds\M<n>\`; ServerHeadless goes to `builds\M<n>-server\BackroadsServer.exe`.
    - It writes `Play.bat` (creates and removes `artifacts\.user-playing`), `PLAYTEST.md` (from the Playtest JSON), `THIRD_PARTY_NOTICES.txt` (from `third-party.json`) and `<Product>_Data\StreamingAssets\build.json`. ServerHeadless also gets `Server.bat`.
    - It refuses with exit 3 while `.user-playing` exists, and writes `artifacts\build-<profile>-<milestone>.json`.
  - `BuildCli.CompileServerScripts` (rule 7), `ServerContents` (rule 8), `TestYardBuilder`, `BrJson`, `RepoPaths`.
- **TestYard.unity**, the M0 boot scene: a grey 60 m plane, sun, chase-style camera, and the canary markers on a 12°/s turntable with colour badges and axis bars. It uses only shared and engine components, so the server loads it cleanly.
- **Tests.**
  - EditMode:
    - `Build.ProjectSettings` (43 cases), `Build.ServerContents`.
    - `Core.CoordsCanary`.
    - Architecture: `AllPlannedAssembliesExist`, `DefineConstraintsAndUnsafeFlags`, rules 1-6 (`OnlyNetReferencesNetcode`, `SharedNeverReferencesClient`, `VehiclesSeesEnginesOnlyThroughInterface`, `NoPropertyBlocks`, `NoStoredInstanceId`, `SharedStateThroughAuthorities`) and rule 7 (`ServerScriptsCompile`).
    - Repo: `NoCrInHashedFiles`, `OwnersJsonValid`, `OwnersRespected`.
    - `Licence.NoticesComplete`, `Art.Canary`, `Privacy.NoNetworkApiUse`.
  - PlayMode: `Audio.BatchmodeDspRuns`.

**T0.8 Axis canary.**
- `art\pipeline\br_canary.py` exports `art\canary\CANARY.fbx` with Blender 4.5 headless (`--background --factory-startup`, 4 s). It uses the 07 §3.2 flags unchanged: `axis_forward='-Z'`, `axis_up='Y'`, `bake_space_transform=True`, `FBX_SCALE_ALL`, and so on.
- The FBX holds 10 cm cubes MARK_Front at game (0,0,2), MARK_Left at (1,0,0) and MARK_Up at (0,1,0), placed in Blender at (gx, -gz, gy). It is copied to `Game\Assets\Art\Canary\`.
- `BackroadsModelPostprocessor` (`Assets/Art/**.fbx`) sets `bakeAxisConversion = false`, file scale and global scale 1.
- `Art.Canary` passes with exact positions: Front (0,0,2), Left (-1,0,0), Up (0,1,0); root rotation identity, scale 1. **The 07 §3.2 recipe is confirmed.**

## Test results (batchmode, 2026-10-18)

| Suite | Command | Result | Wall clock |
|---|---|---|---|
| EditMode | `-runTests -testPlatform EditMode -nographics` | 60 total: **59 passed, 0 failed, 1 skipped** (`Repo.OwnersRespected`, no diff input) | 16-28 s |
| PlayMode | `-runTests -testPlatform PlayMode -nographics` | 1/1 passed (`Audio.BatchmodeDspRuns`) | 14-16 s |
| PlayMode with graphics | same without `-nographics` | 1/1 passed | 16 s |
| `Repo.OwnersRespected` | `BR_OWNERS_BASE=main` (lane I from the branch name) | **fails as expected**: this bootstrap branch creates stubs in every lane's folder and A's canary | 15 s |
| `Repo.OwnersRespected` | `BR_OWNERS_BASE=main BR_OWNERS_LANE=I,A,W,V,R,N,G` | passed (295 paths) | 15 s |

Every EditMode test passes, `Build.ServerContents` on `builds\M0-server` included. `Repo.NoCrInHashedFiles` checked 583 text files and skipped 16 binaries (the vendor PNG and WAV files). `Licence.NoticesComplete` covers 32 locked packages and the plugin DLL plus its version file.

**Audio.BatchmodeDspRuns:** DSP **runs** in batchmode: 187.5 callbacks/s = 48000/256 on both the source and the listener, with and without `-nographics` (`artifacts\audio-batchmode.json`). So audio assertions are **not** tagged `PlayerOnly` by default. The decision is recorded in `Tests\PlayMode\AudioPolicy.cs` (`BatchmodeDspRuns = true`); re-run the test after every editor update.

## Builds and player runs

| Step | Result | Wall clock |
|---|---|---|
| `env-check.ps1` | pass, exit 0 | 3 s |
| `-createProject` from the URP template | exit 0, no licence error | 82 s |
| `build-native.ps1` (lane V's script, cache hit) | DLL + version file in `Game\Assets\Plugins\x86_64\` | 2 s |
| `project-setup.ps1` (`ProjectSetup.Apply`) | 43 checks, 0 failed | 22 s first, 8 s after |
| Dev build (`BuildCli.Build -profile Dev -milestone M0`) | Succeeded, 158 MB | **80 s cold** (73 s inside Unity), 16-22 s incremental |
| `builds\M0\Backroads.exe -br-smoke -br-out ...` | **exit 0**, `SMOKE OK`, 1,247-1,504 frames in 3 s, D3D12, screenshot written | 11-12 s |
| `Play.bat -br-smoke ...` | `.user-playing` created while running and removed after; exit 0 | 12 s |
| autotest `smoke` | pass, exit 0 | 11 s |
| autotest `m0.playtest.f10` | pass: F10 opens the list (2 items), Yes on both, answers file written, F10 closes it | 6 s |
| autotest `m0.crash.recover` | pass: phase 1 exit -1 (killed), `session.lock` left behind; phase 2 exit 0, `crash-*\crash-info.json` with the managed exception, `Player-prev.log` copied, "The last session crashed..." in the log | 9 s |
| autotest `m0.net.egress` (Dev, 60 s) | pass: 60 samples. Only Unity's development PlayerConnection was seen: TCP LISTEN 0.0.0.0:55000, plus a UDP socket on 0.0.0.0 that is allowed in development builds only | 64 s |
| Client build (`-out builds\M0-client`) | Succeeded, 97 MB, no `Backroads.Testing.dll`, Burst "DoNotShip" folder removed | 26 s |
| Client `-br-smoke` | exit 0, **0 network endpoints** | 11 s |
| ServerHeadless build | Succeeded, 97 MB; Managed has no `Backroads.Client*`, `EngineSim` or `Testing`; no `EngineSimPlugin.dll` | 37 s first, 11-16 s after |
| `BackroadsServer.exe -batchmode -nographics -br-bind 127.0.0.1 -br-quit-after 10` | logs `SERVER BOOT OK`, **exit 0** | 14-16 s |
| `BuildCli.CompileServerScripts` (rule 7) | OK: 11 shared assemblies, no client ones | 17 s (5 s compile) |

A warm merge tier, Unity part only, comes to about 1.5 min: EditMode, PlayMode, server compile, an incremental Dev build and smoke.

## How m0.net.egress is checked (method)

1. **Settings** (`Build.ProjectSettings` `privacy.*`): `submitAnalytics` off; Cloud Diagnostics off; Unity Connect, Analytics, Insights, Engine Diagnostics and Performance Reporting off; no `com.unity.analytics`, `services.analytics`, `services.cloud-diagnostics`, `modules.unityanalytics`, `collab-proxy` or `serialization` in `packages-lock.json`; insecure HTTP not allowed. The `unitywebrequest*` modules are not in the manifest at all.
2. **Source** (`Privacy.NoNetworkApiUse`): no `UnityWebRequest`, `System.Net.Sockets`, `TcpClient`/`UdpClient`/`TcpListener`, `HttpClient`, `WebClient`, `WebRequest.Create`, `new Socket(`, `Dns.Get*`, `Ping` or `Application.OpenURL` under `Assets/Backroads` outside `Net/` and `Net.Steam/`.
3. **Runtime** (`NetEgressProbe`, used by `-br-smoke` in every build and by autotest `m0.net.egress` for 60 s): the process reads its own rows of the Windows IP Helper tables (`GetExtendedTcpTable` / `GetExtendedUdpTable` with owner PID, the same data as `Get-NetTCPConnection` / `Get-NetUDPEndpoint`); it opens no socket. Outbound means a TCP connection to a non-loopback address, or a UDP endpoint bound to a non-loopback address.
   - Development players always run Unity's PlayerConnection (profiler discovery). `NetEgressProbe.Classify` allows exactly that, and only in development builds.
   - The Client build had none.
   - Gate scripts can also cross-check with `Get-NetTCPConnection -OwningProcess`.

## Deviations and things to know

1. **Packages (M0 = bundled only).**
   - Pinned, all bundled with 6000.3.20f1: URP, Shader Graph and VFX Graph 17.3.0; Burst 1.8.29; Collections 2.6.6; Mathematics 1.3.3; Input System 1.19.0; Test Framework 1.6.0; Performance 3.5.0; Dedicated Server 2.0.2.
   - Also pinned: `com.unity.ide.visualstudio` 2.0.26 (editor-only, bundled; added for the user's VS 2022).
   - Every resolved version is a bundled tarball or a built-in package. `packages-lock.json` labels them `"source": "registry"`, but nothing was downloaded.
   - **Not added:** glTFast 6.20.0 is **not bundled** (M1, after U3a). FBX Exporter, Splines, NGO, Transport, the Multiplayer packages, Memory Profiler and Services Multiplayer are bundled but start in later milestones.
   - **Removed from the template:** ai.navigation, collab-proxy (Unity Version Control: sign-in and network), multiplayer.center, timeline, visualscripting, ide.rider. ugui stays as a dependency of URP core.
   - **Engine modules trimmed** to animation, audio, imageconversion, imgui, jsonserialize, particlesystem, physics, screencapture, terrain, terrainphysics, ui, uielements, umbra and wind. Gone, among others: unityanalytics, unitywebrequest*, video, vr/xr.
2. **Testing's define constraint is `UNITY_EDITOR || BACKROADS_TESTING`.** Players still need `BACKROADS_TESTING`, which only the Dev profile defines. The editor also compiles the assembly, so it is checked on every import.
3. **References beyond the §1.5 table.**
   - Client.UI: + `Unity.InputSystem`, `UnityEngine.UI` (F10 key, EventSystem with `InputSystemUIInputModule`).
   - Testing: + `Unity.InputSystem` (F10 simulation).
   - Server: + `Core` (asmdef references are not transitive).
   - Build.Editor: + URP and core runtime and editor, and `Unity.RenderPipelines.GPUDriven.Runtime`.
   - `Backroads.Net` lists `Unity.Netcode.Runtime` and `Unity.Networking.Transport`, which are not installed until M4; Unity ignores them without a warning.
4. **ProjectSettings that the 6000.3 API cannot set** are written as serialized fields: `submitAnalytics` (no public API), `m_BrgStripping` (`EditorGraphicsSettings.batchRendererGroupShaderStrippingMode` is read-only), Fixed Timestep (rational), the TimeManager, Audio, Tag, Dynamics and UnityConnect assets.
5. **Settings beyond §1.3:**
   - `runInBackground` on, so autotests keep running without focus.
   - `enableFrameTimingStats` on, for MetricsRecorder main-thread time.
   - A windowed 1600×900 resizable player.
   - Unity Ads and Purchasing flags off.
   - Burst writes `ProjectSettings\BurstAotSettings_StandaloneWindows.json`; Burst shares one file for x86 and x64.
6. **Rule 8 needed a second guard.** The plugin importer's `!UNITY_SERVER` constraint is **not** applied to `BuildPlayerOptions.extraScriptingDefines`. The first ServerHeadless build contained `EngineSimPlugin.dll`, and `ServerContents` caught it and failed the build. `BuildCli` now also leaves every native plugin with `!UNITY_SERVER` out of server builds (`PluginImporter.SetIncludeInBuildDelegate`).
   - `ProjectSetup` writes `EngineSimPlugin.dll.meta` (Editor + Win64, `!UNITY_SERVER`). That meta, and `EngineSimPlugin.version.json.meta`, are committed in lane V's `Game/Assets/Plugins/`.
   - The DLL is gitignored. **Run `tools\ci\build-native.ps1` before opening the project**, or Unity deletes the orphan `.meta` files.
7. **ServerHeadless** is a normal Windows Mono player with `UNITY_SERVER` defined (no dedicated-server variation is installed), started with `-batchmode -nographics`.
8. **Output folders.** Dev and Client both default to `builds\M<n>\`; the last build wins, as the §1.9 examples imply. Use `-out` to keep both. I built the Client into `builds\M0-client`; `builds\M0` holds the Dev build.
9. **`-br-crash-test` is a managed exception followed by `Process.Kill`.** The native fault on an engine worker comes with the plugin calls (lane V added `esp_debug_crash_test` for that).
   - **`m0.crash.recover` runs inside the player.** It starts itself twice on a private `-br-data` folder, so `autotest.ps1` can run it like any other scenario.
10. **Data isolation.** `-br-out` without `-br-data` puts crash, playtest and session files in `<out>\data`, so automated runs never touch `LocalLow\BRDev\Backroads`. SessionMetrics is off in the editor and in batchmode (servers, CI) unless a data folder is named.
    - Unity Test Framework 1.6 still writes a copy of every result file to `LocalLow\BRDev\Backroads\TestResults.xml`. That is its own behaviour.
11. **`Repo.OwnersRespected` inputs.** It reads `BR_OWNERS_DIFF` (a file of changed paths) or `BR_OWNERS_BASE` (a git ref), plus `BR_OWNERS_LANE` (a comma-separated lane list, defaulting to the lane in the branch name). It skips with Ignore when no diff is given.
    - A `.meta` file inherits the owner of its folder.
    - For this M0 bootstrap branch the coordinator must merge with all lanes allowed, since T0.6 and T0.8 are cross-lane by design.
12. **`third-party.json`.** The engine-sim entry now also covers `Game/Assets/Plugins/x86_64/EngineSimPlugin.*`, which is built from engine-sim + scs and is MIT. The Unity umbrella entry names all pinned packages. `Licence.NoticesComplete` treats `com.unity.*` packages as covered by the Unity Companion License entry unless an entry names them.
13. **`Repo.NoCrInHashedFiles` treats a file as binary** when its extension is one of the LFS patterns (plus pdb, exe, fonts and so on) or it has a NUL byte in its first 8,000 bytes (git's rule). Only text files must be CR-free.
14. **Extra autotest `m0.playtest.f10`.** It presses F10 through the Input System and clicks Yes with UI Toolkit submit events. Not in the card; it automates "Did F10 show this list?".
15. **Kept from the template:** `InputSystem_Actions.inputactions` (project-wide actions until `bindings.json`, M2) and the quality levels Mobile and PC, both URP and Forward+.
16. **Starting Unity and the players from PowerShell (for I-b).** `Start-Process -Wait` hangs because the Unity Licensing client outlives the editor. Use `$p = Start-Process ... -PassThru; $null = $p.Handle; $p.WaitForExit(); $p.ExitCode`; the `$p.Handle` line keeps `ExitCode` readable. Unity.exe and Backroads.exe are GUI programs, so a bare `& $UNITY ...` does not wait either.

## Next

- **Coordinator:** merge `m0/i-project` (with `BR_OWNERS_LANE=I,A,W,V,R,N,G` for this branch). V's handoff also asks I to update the U3e row in `docs\approvals\QUEUE.md`.
- **I-b scripts can call:**
  - `Backroads.Build.Editor.BuildCli.Build -profile <P> -milestone M<n> [-out <dir>] [-force]`. Exit 0 built, 1 failed, 2 bad arguments, 3 `.user-playing`.
  - `Backroads.Build.Editor.BuildCli.CompileServerScripts` (exit 0/1), `Backroads.Build.Editor.ProjectSetup.Apply` (exit 0/1/2).
  - Player switches: `-br-smoke [-br-smoke-seconds N]`, `-br-autotest <id>`, `-br-out`, `-br-data`, `-br-silent`, `-br-fps-cap`, `-br-crash-test`. The server takes `-br-bind`, `-br-quit-after`, `-br-server-fps`.
  - Results: `smoke.json` and `result.json` (`status`, `failures`, `evidence`, `endpoints`). Log lines: `[BR] SMOKE OK`, `[BR] AUTOTEST <id> PASS|FAIL`, `SERVER BOOT OK`.
- **T0.9:** Client build into `builds\M0\` (the default), `gate.ps1 -Tier milestone -Gate M0`, `docs\milestones\M0.md`.
- **Lanes:** replace the `AssemblyMarker` stubs in their own folders. A extends `BackroadsModelPostprocessor` (remaps, UCX_ hulls) at A1. W replaces the template input actions with `bindings.json` (M2).

## Resume

```powershell
cd D:\Backroads-Unity-wt\i                      # worktree of branch m0/i-project
git log --oneline -3
tools\ci\env-check.ps1                           # expect: wrote artifacts\env.json (pass), exit 0
tools\ci\build-native.ps1                        # plugin DLL into Game\Assets\Plugins\x86_64 BEFORE Unity opens the project
tools\ci\project-setup.ps1                       # Blender canary + ProjectSetup.Apply; expect "43 checks, 0 failed", exit 0
. tools\ci\env.ps1
$p = Start-Process $UNITY -ArgumentList '-batchmode','-nographics','-projectPath',"`"$PROJ`"",'-runTests','-testPlatform','EditMode','-testResults',"`"$ART\editmode.xml`"",'-logFile',"`"$ART\editmode.log`"" -PassThru; $null = $p.Handle; $p.WaitForExit(); $p.ExitCode   # 0
$p = Start-Process $UNITY -ArgumentList '-batchmode','-nographics','-projectPath',"`"$PROJ`"",'-runTests','-testPlatform','PlayMode','-testResults',"`"$ART\playmode.xml`"",'-logFile',"`"$ART\playmode.log`"" -PassThru; $null = $p.Handle; $p.WaitForExit(); $p.ExitCode   # 0
tools\ci\project-unity.ps1 -Method Backroads.Build.Editor.BuildCli.Build -Extra '-profile','Dev','-milestone','M0'
tools\ci\project-unity.ps1 -Method Backroads.Build.Editor.BuildCli.Build -Extra '-profile','ServerHeadless','-milestone','M0'
# players are GUI programs: read exit codes with Start-Process -PassThru / WaitForExit (deviation 16)
builds\M0\Backroads.exe -br-smoke -br-out artifacts\smoke -screen-fullscreen 0 -logFile artifacts\smoke.log          # exit 0, artifacts\smoke\smoke.json
builds\M0\Backroads.exe -br-autotest m0.crash.recover -br-out artifacts\autotest\m0.crash.recover -br-silent -screen-fullscreen 0
builds\M0-server\BackroadsServer.exe -batchmode -nographics -br-bind 127.0.0.1 -br-quit-after 10 -logFile artifacts\server.log   # SERVER BOOT OK, exit 0
```

If the worktree is gone: `git -C D:\Backroads-Unity worktree add D:\Backroads-Unity-wt\i m0/i-project`, then the commands above. The first Unity start in a fresh worktree also imports `Library`, about 1.5 min on this machine.
