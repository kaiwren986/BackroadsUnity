# Backroads (Unity)

Backroads is an offroad and trucking driving game: haul freight around the canon town of Millbrook, dig through mud, and later drive with friends online. This repository is the Unity version. The original browser game in `D:\Backroads` is the reference ("oracle") and is never edited.

## Playing a build

Every milestone produces a folder under `builds\` (for example `builds\M0\`):

1. Double-click `Play.bat` in that folder.
2. Press **F10** in the game for the playtest checklist. Answer each item; the agents read your answers.
3. `PLAYTEST.md` next to it says what is new and what to try.

Builds are not stored in git. They are made on this PC by `tools\ci\build-player.ps1`.

## Where things are

| Folder | What it holds |
|---|---|
| `docs\PLAN.md` | The plan: milestones, architecture and the decisions for you |
| `docs\research\`, `docs\design\` | The research reports and design proposals behind the plan |
| `docs\milestones\` | One report per milestone: what passed, screenshots, numbers, known issues |
| `docs\approvals\QUEUE.md` | Everything waiting for you: downloads, sign-ins, decisions |
| `CanonData\` | Millbrook exactly as the browser game built it, fingerprinted file by file |
| `Game\` | The Unity project (Unity Hub: Add > `D:\Backroads-Unity\Game`) |
| `native\EngineSimPlugin\` | engine-sim (MIT) built as a plugin, so Unity can simulate real engines |
| `art\` | Blender sources and the scripts that build models |
| `tools\` | Export, build, test and backup scripts |

## Rules that keep Millbrook identical

- Unity never re-creates Millbrook. The browser game exports its finished world once into `CanonData\v1`, every file has a SHA-256 in `manifest.json`, and tests check the fingerprints on every run.
- Hashed folders are stored byte-for-byte (see `.gitattributes`), so Windows line-ending conversion can never change them.

## For agents

Read `AGENTS.md` first.

## Credits

engine-sim is (c) 2022 AngeTheGreat (Ange Yaghi), MIT licence. Town measurements use OpenStreetMap data (c) OpenStreetMap contributors, ODbL. The full list is in `third-party.json` and in every build's `THIRD_PARTY_NOTICES.txt`.
