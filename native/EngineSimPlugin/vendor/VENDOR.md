# Vendored engine-sim (PLAN §1.6, §3.1)

Byte-identical copies of committed git blobs. Nothing in `vendor\engine-sim\` is ever edited: every fix lives in our own
sources under `native\EngineSimPlugin\src\` (research 01 §8). `tools\ci\vendor-check.ps1` proves the copy.

## Source and commits

Taken on 2026-10-18 from `D:\Backroads-ref\engine-sim` (read only; never the CRLF working tree, PLAN T5).

| Component | Repository | Commit | Date | Subject | Files |
|---|---|---|---|---|---|
| engine-sim | https://github.com/ange-yaghi/engine-sim | `85f7c3b959a908ed5232ede4f1a4ac7eafe6b630` | 2023-01-22 | Update README.md | 249 |
| simple-2d-constraint-solver | https://github.com/ange-yaghi/simple-2d-constraint-solver | `e009f4ff1c9c4c5874e865e893cdb62e208fb2b3` | 2022-09-07 | Create LICENSE | 74 |
| piranha | https://github.com/ange-yaghi/piranha | `432f0b122bb1663b686c553c7e7269300afac3bc` | 2022-08-11 | Fixed compilation issue with MSVC | 261 |

`git -C D:\Backroads-ref\engine-sim rev-parse HEAD`:

```
85f7c3b959a908ed5232ede4f1a4ac7eafe6b630
```

`git -C D:\Backroads-ref\engine-sim submodule status` (a leading `-` means not checked out):

```
 2112c55e1e831c8f7a1b91de23722b7926ad3d00 dependencies/submodules/csv-io (heads/master)
-b7d0a046733b924d12706baf1e5e59ba427aa7b1 dependencies/submodules/delta-studio
-19f939c88d740d9e42755d6191daff5719be198f dependencies/submodules/direct-to-video
 432f0b122bb1663b686c553c7e7269300afac3bc dependencies/submodules/piranha (432f0b1)
 e009f4ff1c9c4c5874e865e893cdb62e208fb2b3 dependencies/submodules/simple-2d-constraint-solver (e009f4f)
```

The gitlinks recorded in engine-sim `85f7c3b` (`git ls-tree 85f7c3b dependencies/submodules/`) are the same commits;
vendor-check re-reads them on every run and fails if they ever disagree with the commits above.

## What was taken

| Vendored path | From |
|---|---|
| `engine-sim\{src, include, scripting, es, assets\engines, LICENSE}` | engine-sim `85f7c3b` |
| `engine-sim\dependencies\submodules\simple-2d-constraint-solver\**` | the whole tree at `e009f4f` |
| `engine-sim\dependencies\submodules\piranha\**` | the whole tree at `432f0b1` |

Omitted: `delta-studio` (UI/renderer; the core reaches it only through an unused include, satisfied by our empty shim
headers in `native\EngineSimPlugin\shim\`), `csv-io` (linked upstream but never called by the core, research 01 §1.1),
`direct-to-video`, and engine-sim's `art`, `configuration`, `design`, `docs`, `test`, `.github` and top-level CMake files.
Our own `native\EngineSimPlugin\CMakeLists.txt` lists sources from this tree; upstream CMake is never used (01 §1.4).

The layout mirrors upstream, so upstream's relative includes resolve unchanged (for example `include\scs.h` includes
`../dependencies/submodules/simple-2d-constraint-solver/include/scs.h`, and `scripting\include\piranha.h` includes
`<piranha/include/piranha.h>` with `dependencies\submodules` on the include path).

## Exact commands

Run from a PowerShell prompt. `git archive -o` writes the tar itself, so PowerShell 5.1 never pipes binary data.
`GIT_OPTIONAL_LOCKS=0` keeps git from refreshing the reference checkout's index.

```powershell
$env:GIT_OPTIONAL_LOCKS = '0'
$ES  = 'D:\Backroads-ref\engine-sim'
$V   = 'native\EngineSimPlugin\vendor\engine-sim'
$TMP = "$env:TEMP\esp-vendor"; New-Item -ItemType Directory -Force $TMP | Out-Null
git -c core.autocrlf=false -C $ES archive --format=tar -o "$TMP\engine-sim.tar" 85f7c3b src include scripting es assets/engines LICENSE
git -c core.autocrlf=false -C "$ES\dependencies\submodules\simple-2d-constraint-solver" archive --format=tar -o "$TMP\scs.tar" e009f4f
git -c core.autocrlf=false -C "$ES\dependencies\submodules\piranha" archive --format=tar -o "$TMP\piranha.tar" 432f0b1
New-Item -ItemType Directory -Force "$V\dependencies\submodules\simple-2d-constraint-solver", "$V\dependencies\submodules\piranha" | Out-Null
tar -x -f "$TMP\engine-sim.tar" -C $V
tar -x -f "$TMP\scs.tar" -C "$V\dependencies\submodules\simple-2d-constraint-solver"
tar -x -f "$TMP\piranha.tar" -C "$V\dependencies\submodules\piranha"
tools\ci\vendor-check.ps1
```

None of the three trees has a `.gitattributes` file, so with `core.autocrlf=false` `git archive` writes the blob bytes
unchanged. vendor-check compares `git hash-object --no-filters` of every vendored file against the blob ids from
`git ls-tree -r` at the commits above, so the result never depends on anyone's `autocrlf` setting.

## Line endings and Git LFS

- Our `.gitattributes` marks `native/EngineSimPlugin/vendor/**` as `-text`: git never converts these bytes.
- No vendored text file contains a CR byte. 16 binary files do, inside their binary data, exactly as their upstream
  blobs do: 6 PNG images under `piranha\docs\public\` and 10 impulse-response WAVs under `es\sound-library\`.
  `Repo.NoCrInHashedFiles` must therefore skip binary files (or accept a file whose bytes equal its upstream blob, which
  vendor-check proves).
- `*.wav` and `*.png` are stored through Git LFS like everywhere else in the repo. vendor-check reads the working-tree
  files, so it checks the real bytes after LFS smudge; a checkout without LFS content fails the check, as it should.

## Licences (`LICENSES\`)

| File | Content | Source blob |
|---|---|---|
| `engine-sim-LICENSE` | MIT, "Copyright 2022 AngeTheGreat (Ange Yaghi)" | engine-sim `85f7c3b:LICENSE` = `e245c4c63cb5c8bdf9cdb978b8bff7b16026479a` |
| `simple-2d-constraint-solver-LICENSE` | MIT, "Copyright (c) 2022 Ange Yaghi" | scs `e009f4f:LICENSE` = `231f3f1baf112ac7a71efd68d7be22107c64abb0` |
| `piranha-LICENSE` | MIT, "Copyright (c) 2022 Ange Yaghi" | piranha `ff2955d:LICENSE` = `231f3f1baf112ac7a71efd68d7be22107c64abb0` (see below) |
| `flex-NOTICE.txt` | flex's BSD notice (needed in binary distributions only if the generated scanner ships, U3c) | lines 5-28 of `piranha\dependencies\libraries\flex\include\FlexLexer.h`, verbatim |
| `bison-NOTICE.txt` | the Bison skeleton's copyright, GPL-3.0 notice and special exception (the generated parser ships from M1, U3c; notices.ps1 copies it for the bison entry) | lines 1-31 of `..\generated\piranha\parser.auto.cpp`, verbatim |

vendor-check verifies each of these five files against its source; `piranha-LICENSE` is the only file that has no
counterpart in the vendored commits (PLAN §1.6, C9).

### piranha licence note

piranha at `432f0b1` (2022-08-11, the commit engine-sim pins) has no LICENSE file, and its README names none. Its MIT
LICENSE was added upstream in `004fa15` (2022-08-16), whose only change against `432f0b1` is that file:
https://github.com/ange-yaghi/piranha/compare/432f0b1...004fa15 (research 01 §5.4, PLAN §3.2, C9).

The local clone is shallow and does not contain `004fa15`, but it does contain piranha's `origin/master`
(`ff2955daddb2a7e0bae93f8e444a9601bf5bf111`, 2024-09-15), whose `LICENSE` blob is `231f3f1b…`. That text was copied
with `git cat-file blob ff2955d:LICENSE`: no download. It is byte-identical to simple-2d-constraint-solver's own LICENSE
(same author, same year). What is still open (U3e): confirming that `004fa15:LICENSE` is that same blob, by looking at the
compare page above or by an approved fetch of `004fa15` followed by `git ls-tree 004fa15 LICENSE`. piranha is not
shipped unless U3c is approved (`third-party.json`).

## Generated piranha scanner and parser (U3c)

piranha needs flex and bison at build time and commits no generated parser (research 01 5.4). On 2026-10-18 lane V
ran win_flex_bison once and committed the output to `native\EngineSimPlugin\generated\piranha\` (outside `vendor\`, so
vendor-check is unchanged; see `generated\piranha\GENERATED.md` for the commands and file hashes):

| Tool | Release tag | Asset | Bytes | SHA-256 of the zip |
|---|---|---|---|---|
| win_flex_bison 2.5.25 (win_flex 2.6.4, win_bison 3.8.2) | `v2.5.25` (https://github.com/lexxmark/winflexbison/releases/tag/v2.5.25) | `win_flex_bison-2.5.25.zip` | 1,083,989 | `8d324b62be33604b2c45ad1dd34ab93d722534448f55a16ca7292de32b6ac135` |

The zip lives in `tools\winflexbison\` (gitignored) and is not needed again. `src\piranha_path_std.cpp` replaces
piranha's `src\path.cpp` (Boost.Filesystem) with std::filesystem, so piranha builds with MSVC alone (research 01 App. A.6).

## Rules

- Never edit a vendored file. Plugin fixes go in `native\EngineSimPlugin\src\`; future fork work (turbo, engine brake,
  diesel) goes into `native\EngineSimPlugin\patches\` and is applied to a build copy (PLAN §1.6).
- To move to another upstream commit: re-run the commands above at the new commits, update this file and the commit
  constants at the top of `tools\ci\vendor-check.ps1` in the same commit, and run `tools\ci\vendor-check.ps1`.
