# Approvals queue

Everything waiting for you, newest at the bottom. Each item has a decision id (PLAN §7.2), what is needed, and what happens if you don't answer by the due date. Anything that costs money, signs you up or uses your logins never defaults to "yes".

| Id | Needed from you | Due | Default if unanswered | Status |
|---|---|---|---|---|
| U19 | Accept the agents' allowlist: copy `docs\approvals\claude-settings.proposed.json` into `Game`'s project settings, or tell an agent "accept the allowlist" | M0 | Agents keep asking before each command (slower) | waiting |
| U21 | Name a backup folder on a second drive or USB stick for `tools\ci\backup.ps1` | end of M0 | Backups go to `D:\Backroads-Unity-backups` on the same drive (no protection against drive failure) | waiting |
| U1 | Create the private GitHub repo `kaiwren986/Backroads-Unity` (or say "agents may create it with gh") | end of M0 | Work stays on this PC only, plus local backups | waiting |
| Unity Hub | Open Unity Hub once and check you are signed in (agents never sign in) | M0, day 2 | The Unity project can't be created in batchmode | waiting |
| U10 | Synty packs (City, Explorer Kit, Pro Racer are downloaded): 1) turn off "use my chats to improve the model" in your Claude privacy settings, 2) email Synty for written confirmation (draft below), 3) run `tools\Synty-Import.bat` yourself when it exists | before any Synty import | Synty stays out of the project | waiting |

## Draft email to Synty (U10)

> Subject: Licence question: AI coding assistant and POLYGON packs
>
> Hi, I bought POLYGON City, POLYGON Explorer Kit and POLYGON Pro Racer (one-time licence) for my free hobby driving game made in Unity. I use an AI coding assistant to write the game's code. I would like it to write Unity editor and gameplay scripts that place your prefabs in my scenes by name or path (for example spawning SM_Veh_* prefabs as traffic, or placing street props along roads).
>
> The assistant never opens, reads, converts, edits or generates 3D models, textures or materials from your assets, and none of your files are uploaded anywhere or used for training. I import the packages myself in the Unity editor.
>
> Is that use allowed under your licence's generative-AI terms? Thank you.
