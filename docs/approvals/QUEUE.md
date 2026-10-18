# Approvals queue

Everything waiting for you, newest at the bottom. Each item has a decision id (PLAN §7.2), what is needed, and what happens if you don't answer by the due date. Anything that costs money, signs you up or uses your logins never defaults to "yes".

| Id | Needed from you | Due | Default if unanswered | Status |
|---|---|---|---|---|
| U19 | Accept the agents' allowlist (`docs\approvals\claude-settings.proposed.json`) | accepted 2026-10-18 | n/a | **accepted**: now in `.claude\settings.json`, with the same never-allowed list also applied to PowerShell commands (no push, `gh`, `wget`, `winget`, `npm install`, `pip install`) and `.\tools\...` spellings of the script rules |
| U21 | Name a backup folder on a second drive or USB stick for `tools\ci\backup.ps1` | end of M0 | Backups go to `D:\Backroads-Unity-backups` on the same drive (no protection against drive failure) | waiting |
| U1 | Create the private GitHub repo `kaiwren986/Backroads-Unity` (or say "agents may create it with gh") | end of M0 | Work stays on this PC only, plus local backups | waiting |
| Unity Hub | Open Unity Hub once and check you are signed in (agents never sign in) | M0, day 2 | The Unity project can't be created in batchmode | waiting |
| U10 | Synty packs (City, Explorer Kit, Pro Racer): you turned off model training on 2026-10-18 and asked the agents to import them into the client with Unity's importer. Sending Synty the email below is optional extra reassurance | after the M0 Unity project lands | n/a | **decided: agents import** |
| U3 | Free downloads (decision 5): Unity add-on packages (glTFast and the 6.3 patch versions), Clipper2 (kerbs and sidewalks), win_flex_bison (unlocks the 25 ready-made engines), piranha's licence file, later Facepunch.Steamworks | approved 2026-10-18 | n/a | **approved** |
| Licence texts | `licenses\` holds the standard texts of MIT, BSD-2/3-Clause, Apache-2.0, OFL-1.1, ODbL-1.0 and BSL-1.0, written by an agent from memory and marked as such (no download was allowed). Either let an agent fetch the 7 canonical text files from opensource.org, apache.org, openfontlicense.org, opendatacommons.org and boost.org to replace them, or compare them yourself. ODbL is the long one most likely to differ in wording | before any public build (friends-only builds are fine meanwhile) | The marked copies stay; `notices.ps1` reports them as unverified in every gate | waiting |

## Draft email to Synty (U10)

> Subject: Licence question: AI coding assistant and POLYGON packs
>
> Hi, I bought POLYGON City, POLYGON Explorer Kit and POLYGON Pro Racer (one-time licence) for my free hobby driving game made in Unity. I use an AI coding assistant to write the game's code. I would like it to write Unity editor and gameplay scripts that place your prefabs in my scenes by name or path (for example spawning SM_Veh_* prefabs as traffic, or placing street props along roads).
>
> The assistant never opens, reads, converts, edits or generates 3D models, textures or materials from your assets, and none of your files are uploaded anywhere or used for training. I import the packages myself in the Unity editor.
>
> Is that use allowed under your licence's generative-AI terms? Thank you.
