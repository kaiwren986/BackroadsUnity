# Generated piranha scanner and parser (PLAN 3.2, U3c)

piranha (vendored at `432f0b1`) needs flex and bison at build time and commits no generated parser. These four files
were generated once, on 2026-10-18, by lane V and are committed so that no build ever needs flex or bison. The plugin
builds them into the `piranha` target with `-DESP_WITH_PIRANHA=ON` (the default in `CMakePresets.json`); nothing
regenerates them.

| File | Bytes | SHA-256 |
|---|---|---|
| `parser.auto.cpp` | 123,956 | `09bb9f92bb06caa0855ca65085ff6b1d05ad643a782ff9ae1a44601ab4949f7f` |
| `parser.auto.h` | 67,498 | `0e942e14afeef21f49fbd647696d08da96264235ad13efcc5040689f04fba449` |
| `scanner.auto.cpp` | 62,653 | `ea6488e436d670c56499c92fec7edd771b3700137edadd3f3c63c3acc4b88b89` |
| `scanner.auto.h` | 11,385 | `2ecdd42c2a4246979f86911ea250c25cbcdf764af2c34a86cd5f92ace71b3dba` |

## Tool

win_flex_bison 2.5.25 (portable zip: `win_flex` 2.6.4, `win_bison` 3.8.2), release `v2.5.25` of
https://github.com/lexxmark/winflexbison, asset `win_flex_bison-2.5.25.zip`, 1,083,989 bytes,
SHA-256 `8d324b62be33604b2c45ad1dd34ab93d722534448f55a16ca7292de32b6ac135`. Downloaded with the allowlisted command
(the user approved it under U3c) into `tools\winflexbison\` (gitignored; nothing installed system-wide):

```
curl -L -o tools/winflexbison/win_flex_bison.zip https://github.com/lexxmark/winflexbison/releases/download/v2.5.25/win_flex_bison-2.5.25.zip
```

## Commands

Run from this folder, so the `#line` directives hold relative paths only (no machine paths in the files):

```
W=../../../../tools/winflexbison/2.5.25
P=../../vendor/engine-sim/dependencies/submodules/piranha/flex-bison
$W/win_bison.exe --defines=parser.auto.h -o parser.auto.cpp $P/specification.y   # 1 shift/reduce conflict, as upstream
$W/win_flex.exe --header-file=scanner.auto.h -o scanner.auto.cpp $P/scanner.l
```

These are the outputs piranha's own CMake (`flex_target` / `bison_target` with `DEFINES_FILE`) would produce. win_bison
writes CRLF line endings; they were converted to LF (`generated/**` is a hashed tree, `Repo.NoCrInHashedFiles`), the
only change made to the tool output.

## Licences

- `parser.auto.cpp` and `parser.auto.h` keep Bison's header: GPL-3.0-or-later with the Bison special exception, which
  lets the generated parser be distributed under terms of our choice.
- `scanner.auto.cpp` and `scanner.auto.h` come from flex's skeleton. The scanner includes `FlexLexer.h`, whose BSD-style
  flex notice (`vendor\LICENSES\flex-NOTICE.txt`) is reproduced in `THIRD_PARTY_NOTICES.txt` for binary distributions
  (PLAN 3.2 [C9]); flex therefore becomes a shipped entry in `third-party.json`.
- `Licence.GeneratedHeadersIntact` (lane I) checks that these headers stay intact.
