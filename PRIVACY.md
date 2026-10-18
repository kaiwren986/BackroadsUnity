# Privacy

Draft (PLAN §5.9). Completed before any public build (M9).

- The game collects no analytics. Unity's hardware statistics and Cloud Diagnostics are switched off (`ProjectSetup.Apply`, checked by `Build.ProjectSettings` and autotest `m0.net.egress`).
- Crash reports and session metrics stay on the player's PC (`%USERPROFILE%\AppData\LocalLow\<company>\<product>\`). Nothing is uploaded.
- Online play (from M8) exchanges player names and network addresses with the other players in a session, through Unity Relay or Steam. Saved server data and ban lists will be documented here when they exist.
