# Working on Backroads from your desktop

The project lives in your private GitHub repo `kaiwren986/Backroads-Unity` (U1). Your laptop pushes to it; your desktop
clones it. Everything you need comes down with the clone, apart from the things listed under "Not in GitHub".

## 1. Install (once)

The scripts expect the same tools and the same folders as on the laptop. On the desktop, install:

1. **Git for Windows** (it includes Git LFS). Leave the installer's defaults.
2. **Unity Hub**, then in Hub install the editor **6000.3.20f1** exactly (Installs → Install Editor → Archive, if it
   isn't listed). The default Windows build support is enough; no extra modules are needed (PLAN §1.1). Open Hub once
   and sign in yourself: the agents never sign in.
3. **Node.js** 20 or newer (the laptop has 24).
4. For the engine plugin and the native tests (optional: the committed plugin DLL works without them):
   **CMake** 3.21 or newer and **Visual Studio 2022 Build Tools** with the "Desktop development with C++" workload.
5. For art work (lane A): **Blender 4.5** in its default folder.
6. The **Claude** desktop app, if you want the agents there too.

## 2. Get the code (once)

The scripts use fixed folders on a **D:** drive. If your desktop has no D: drive, say so and the agents will make
the folders configurable first.

```bash
git clone https://github.com/kaiwren986/Backroads-Unity.git D:/Backroads-Unity
```

```bash
git clone https://github.com/kaiwren986/Backroads.git D:/Backroads
```

```bash
git clone https://github.com/ange-yaghi/engine-sim.git D:/Backroads-ref/engine-sim
```

The first is this project. The second is the browser game, the physics reference; it is only ever read, never
changed from the Unity project. The third is the upstream engine-sim, which only the vendor check reads (it compares
our vendored copy against commit `85f7c3b`); skip it if you won't run the full gates.

If Windows shows a GitHub sign-in window during a clone, sign in there yourself.

## 3. Not in GitHub

| What | Why | On the desktop |
|---|---|---|
| Your Synty packs (City, Explorer Kit, Pro Racer) | The licence doesn't allow them in git (AGENTS.md rule 3) | Download them from your Synty account and import them as on the laptop; `synty-manifest.json` lists the packs and where they go (`Game/Assets/Synty/`) |
| `artifacts\` (test results, download caches) | Re-made by every run | Nothing to do; downloads such as the OSM and elevation caches re-fetch when a script needs them |
| `builds\` | Re-made by the build scripts | Run a build, or copy `builds\M2` over by hand if you just want to play |
| `Game\Library\` | Unity's import cache | The first open takes 10-20 minutes while Unity imports |
| The backups in `D:\Backroads-Unity-backups` | Local to the laptop | GitHub is now the off-PC copy |

## 4. First run on the desktop

1. In Unity Hub: **Add** → `D:\Backroads-Unity\Game`. Let it import once, then close it (the scripts run Unity
   themselves; never keep the same project open twice).
2. Check the tools: `tools\ci\env-check.ps1` lists anything missing.
3. Play the latest build: `tools\ci\build-player.ps1 -Profile Client -Milestone M2`, then `builds\M2\Play.bat`.

## 5. Two PCs, one project

- **Work on one PC at a time.** Before you start on one, get the other's latest work; when you stop, send yours.
- On the laptop the agents push with `tools\ci\push-github.ps1` (U1). On the desktop, before you start:

```bash
git -C D:/Backroads-Unity pull
```

- If both PCs changed the same branch, the push stops with a message instead of overwriting anything
  (`push-github.ps1` never forces). Ask the agents to merge.
- Lane worktrees (`D:\Backroads-Unity-wt\...`) belong to the PC that made them. Their branches are in GitHub, so a
  lane can carry on from the other PC with a fresh worktree on the same branch.
