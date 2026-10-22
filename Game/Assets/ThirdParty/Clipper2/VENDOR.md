# Clipper2 (vendored)

Polygon clipping and offsetting by Angus Johnson, Boost Software License 1.0 (`LICENSE.txt`).

- Source: https://github.com/AngusJohnson/Clipper2, tag `Clipper2_2.0.1` (commit `21ebba05db8894f0c7217ad35ea518080f324946`,
  released 2025-12-19), archive `Clipper2_2.0.1.zip` SHA-256 `2fbfe548aa1427716510b572446bcec34c88990df7da510039cbdc2ee2406a23`.
- Copied unchanged: the nine `.cs` files of `CSharp/Clipper2Lib/` (the `.csproj` and the signing key are left out).
- Download approved as U3 b (PLAN §1.4, `docs/approvals/QUEUE.md`), fetched 2026-10-22.
- Used by the editor road bake only (kerb loops, sidewalks, swept paths: research 05 §5.7): `Clipper2Lib.asmdef` is
  editor-only and not auto-referenced, so nothing of it ships in a build. `csc.rsp` turns on the nullable context the
  library is written for (C# 8, as its `.csproj` sets); `USINGZ` stays undefined (2D only).
- To update: download a newer tag, replace the nine files and `LICENSE.txt`, update this file and `third-party.json`.
