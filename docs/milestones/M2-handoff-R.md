# M2 handoff: lane R, T2.8 (Roads R0: graph import, cleanup, debug view, plain ribbons)

Branch `m2/r-graph` in `D:\Backroads-Unity-wt\r`, cut from `main` at `c42a473`; `main` (with lane A, `4fb372e`) merged in
at `85d0e98`. Nothing was pushed, installed, downloaded or signed into. `D:\Backroads` was only read (`git show
552f7807:js/trafficNet.js` and friends); `CanonData\v1` is untouched and read through lane W's `CanonImporter`.
`Millbrook.unity` and lane W's M1 ribbons are untouched.

## In plain words

Millbrook's 24 roads are now one clean map of where roads meet: 31 points (11 real junctions, 6 places where a dirt road
meets a paved one, 5 street corners, 5 dead ends at the yards, 4 town edges) joined by 40 road pieces. It matches the
roads research note (05) exactly, it fixes the old traffic code's mistake of letting side streets "win" over the highway at
two junctions, and it shows which junction needs your decision (the town square). Nothing you drive on changes yet: the
real junction surfaces come in M3. Pictures: see "Screenshots" at the end.

## Done

| Step | What | Where |
|---|---|---|
| 1 | **`RoadNetworkAsset`** baked in the editor from the imported canon (world.json roads, lots, building footprints; traffic.json node numbers). `RoadNetworkBaker` (menu *Backroads > Roads > Bake road network (R0)*), `RoadBakeCli.Bake [-verifyHash]` for batchmode (the PLAN 1.9 `roads-bake` entry point), a text report in `artifacts\roads\graph-report.txt`. The asset is committed in a Resources folder (ships in every build, server included), with a SHA-256 content hash. | `Roads/Runtime/RoadNetworkAsset.cs`, `Roads/Editor/RoadNetworkBaker.cs`, `Roads/Baked/Resources/BrRoadNetwork.asset` |
| 2 | **Graph cleanup (05 §5.1)** in plain C#, double precision, deterministic: T-splits with 1 m tolerance and 2 m end clearance (catches the 6 dirt ends), 1 m clustering (splits within 1 cm snap onto the canon point, so every canon point is kept), town-zone split (street hull + 20 m) into MainStreet / Highway, joins and L-bends, dead ends, dirt aprons, complex-pair check (< 30 m), arms sorted counter-clockwise, **major road by class first** (straightness only breaks ties), a provisional control from 05 §6.2 (two-way stop, all-way stop at the street-by-street X, stop on dirt). Nodes keep traffic.json's numbers (so they match 05's tables); new nodes are 21-30. | `Roads/Runtime/RoadGraphBuilder.cs`, `RoadGraph.cs`, `RoadGeometry.cs`, `RoadGraphInput.cs`, `RoadGraphCanon.cs`, `RoadTypes.cs` |
| 2+ | **Provisional corners for the debug view** (05 §5.3, straight-line formula): per-corner kerb radius by context (6 m downtown, 7.5 m streets, 12 m fuel yard and square, 9 m dirt, 6 m inside L-bends; acute corners shrink so tangents stay within 25 m), tangent points, mouths (tangent + 0.5 m), short-arm, steep-approach and acute-Y flags. "Downtown" uses 05 §1.3's own measure, the building clearance of a 7.5 m kerb; it reproduces §1.3's numbers to the centimetre. R1 replaces all of this with the polyline corner solver. | same |
| 3 | **Debug views.** Editor gizmos (`RoadNetworkView` + `RoadNetworkGizmos`: nodes, arms, tangent points, mouths, kerb arcs, labels). Dev-build overlay (`RoadGraphOverlay`, registered through `PresentationRegistry` in Development builds and the editor, so it is there in Millbrook without touching the scene; hidden until **F7**). A CPU-drawn plan map over the hillshaded canon terrain and building footprints (works with `-nographics`). | `Roads/Client/`, `Roads/Client/Editor/`, `Roads/Editor/RoadGraphMap.cs` |
| 4 | **Plain ribbons** from the cleaned graph (one strip per edge, 05 §5.2 miter offset, seams shared between pieces of one road), coloured by class, and **`RoadsDebug.unity`**: lane W's `MillbrookWorld` (unchanged) + the overlay + the R0 ribbons; keys 1 graph + M1 ribbons, 2 R0 ribbons, 3 side by side (left half M1, right half R0, same view), F7, L, T, O, J, N. | `Roads/Runtime/PlainRibbons.cs`, `PlainRibbonMesh.cs`, `Roads/Client/RoadsDebugView.cs`, `Scenes/RoadsDebug.unity`, `Roads/Client/Editor/RoadsDebugSceneBuilder.cs` |
| Tests | EditMode `Backroads.Tests.EditMode.Roads` (category `Roads`) and PlayMode `Backroads.Tests.PlayMode.Roads` (renders the debug views). | `Tests/EditMode/Roads/`, `Tests/PlayMode/Roads/` |

## Gates (T2.8 "done when"), expected vs measured

No M2 gate (the card's gate is "none in M2"; this is the head start for M3's roads bake). The card's "done when"
items are EditMode tests in `Backroads.Tests.EditMode.Roads`; all on this laptop, 2026-10-19.

| Done-when item | Expected (05 §1.2, §5.1) | Measured |
|---|---|---|
| Node counts | 11 paved junctions: 5 T (0, 1, 3, 17, 19; the square counted as a T) + 6 X (2, 4, 5, 7, 9, 16); 6 dirt-road T junctions at JS (-204, -95), (-256, 194), (326, -91), (141, -250), (90, 153), (186, -46); 5 L-bends (13, 14, 15, 18, 20); 5 dead ends (6, 8, 10, 11, 12) | **pass, exactly**: same ids and kinds, the 6 dirt aprons within 1 m of 05's points (nodes 21-30 are new). Plus 4 town-edge class-change nodes from the zone split (§5.1 step 3, not in §1.2's table). Total 31 nodes, 40 edges. |
| Arm counts | 3 per T, 4 per X, 3 per dirt apron (1 dirt + the highway twice), 2 per bend, 1 per dead end; arm roads as in 05 §1.2 | **pass**: 80 arm ends (2 per edge); the road set of every paved junction matches §1.2 (e.g. node 2 = R0, R0, R22, R23; node 16 = R11, R12, R19, R20). Arm gaps at node 2: 112/73/99/76 degrees (05: 113/72/97/78); smallest gap at a paved junction 73.2 degrees (05: 71.9). |
| Class-correct major road | Class first, straightness as tie-break: Route 9 (R0) through node 2 and R1 through node 5 (trafficNet picked the streets there); R0+R5 at node 3; no major at the all-way stop (16) | **pass**: `MajorProblems` = 0. Majors: 1 R0, 2 R0, 3 R0+R5, 4 R1, 5 R1, 7 R2, 9 R3, 17 R16+R17, 19 R14+R15, every dirt apron its highway; none at 16 (all-way stop) and at 0 (best pair bends 39 degrees; waits for U8). The test also confirms traffic.json made the streets major at 2 and 5. |
| Connected | One connected graph | **pass**: 31/31 nodes reachable; the paved network alone is connected too. No road crosses another except at a node. |
| Ribbons cover the canon centrelines within 0.1 m | Every canon centreline point and the full road width covered, at the canon height | **pass** (baked float asset): 1,722/1,722 canon points are ribbon centre vertices (max 1.6e-5 m); 40,884 samples every 0.25 m at the centre and at +-(hw - 0.1 m): 0 outside; kerb-line offset error max 0.056 m; surface height error max 0.060 m; each road's edges tile it with 0 m gaps. Lane W's M1 ribbons lie within 0.041 m of the R0 kerb lines. |
| Extra: deterministic, current | Same input, same bytes | **pass**: two bakes give the same SHA-256 (`442832e8...`), the committed asset survives Unity's YAML round trip bit-exactly (`RoadBakeCli.Bake -verifyHash`), and `Roads.BakedAssetUpToDate` fails if the asset goes stale. Bake 1.7 s in the editor (the graph itself ~30 ms); asset 168 KB. |
| Extra: 05's measurements | §1.2 grades, §1.3 kerb clearances | **pass**: steep approaches (> 6 % within 20 m) only at nodes 3 (+6.2 %; 05: +6.5), 5 (-9.1 % on R18) and 17 (-9.0 %); kerb clearances at 7.5 m 1.22 / 2.09 / 3.34 / 3.95 / 3.28 m at nodes 9 / 7 / 2 / 4 / 16 (05 §1.3: identical). |

Test runs: EditMode `-Category Roads` **16/16 pass**; full default EditMode **107 passed, 2 skipped** (ServerContents: no server
build; OwnersRespected: needs a base), **0 failed** (this includes the UNITY_SERVER compile check); OwnersRespected with
`BR_OWNERS_BASE=main` flags only `OWNERS.json` (the REQUEST FOR I commit); PlayMode `-Category Roads` **1/1 pass** (RoadsDebug
loads, Millbrook ready in 0.8 s, five views rendered). Dev player (`build-player.ps1 -Profile Dev -Milestone M2`, 221 MiB) builds
and passes the `smoke` autotest; its log shows `presentation 'roads-graph-overlay' started`. The full `gate.ps1 -Tier merge` was not run.

## The cleaned graph (from the bake)

JS coordinates and angles, as 05 quotes them. Classes: M MainStreet (a through road inside the town kerb zone), H Highway,
S Street, D Dirt. Kerb radii are the provisional per-corner radii; the bracket is 05 §1.3's building clearance of a 7.5 m
kerb at the node's tightest corner.

| node | legacy | JS (x, z) | kind | arms (road/class @ JS angle, grade) | major | control | kerb radii (building clearance at 7.5 m) | flags |
|---|---|---|---|---|---|---|---|---|
| 0 | 0 | (-70.0, 30.0) | Junction T | R0/M@-15 R2/M@-125 R1/M@125 | none (best pair bends 39.2) | None | 12/12/12 | ComplexPair, ShortArm |
| 1 | 1 | (-56.8, 25.7) | Junction T | R0/M@-30 R0/M@157 R3/M@48 | R0+R0 (bend 7.7) | None | straight/12/12 | ComplexPair, ShortArm |
| 2 | 2 | (-5.7, -15.7) | Junction X | R0/M@-29 R23/S@-141 R0/M@143 R22/S@44 | R0+R0 (bend 8.0) | MinorStop | 7.5/7.5/7.5/7.5 (3.34 m) | ShortArm |
| 3 | 3 | (85.0, -85.0) | Junction T | R5/H@-65 (+6.2%) R0/H@135 R4/H@31 | R5+R0 (bend 20.2) | MinorStop | 12/12/12 | SteepApproach |
| 4 | 4 | (-120.2, 84.2) | Junction X | R1/M@-45 R21/S@-140 R1/M@135 R20/S@42 | R1+R1 (bend 0.0) | MinorStop | 7.5/7.5/7.5/7.5 (3.95 m) |  |
| 5 | 5 | (-170.0, 143.9) | Junction X | R1/M@-53 R18/S@-137 (-9.1%) R1/M@135 R17/S@39 | R1+R1 (bend 8.4) | MinorStop | 7.5/7.5/7.5/7.5 (8.83 m) | SteepApproach |
| 6 | 6 | (-300.0, 225.0) | DeadEnd | R1/H@-37 | - | None | - |  |
| 7 | 7 | (-104.6, -8.6) | Junction X | R13/S@-49 R2/M@-135 R14/S@131 R2/M@45 | R2+R2 (bend 0.0) | MinorStop | 6/6/6/6 (2.09 m) | Downtown |
| 8 | 8 | (-255.0, -175.0) | DeadEnd | R2/H@46 | - | None | - |  |
| 9 | 9 | (-25.1, 58.9) | Junction X | R10/S@-50 R3/M@-135 R11/S@132 R3/M@45 | R3+R3 (bend 0.1) | MinorStop | 6/6/6/6 (1.22 m) | Downtown |
| 10 | 10 | (92.0, 152.0) | DeadEnd | R3/H@162 | - | None | - |  |
| 11 | 11 | (330.0, -95.0) | DeadEnd | R4/H@133 | - | None | - |  |
| 12 | 12 | (150.0, -315.0) | DeadEnd | R5/H@90 | - | None | - |  |
| 13 | 13 | (-4.2, 186.3) | Bend | R19/S@-139 R9/S@131 | - | None | 6/miter |  |
| 14 | 14 | (-53.2, 242.8) | Bend | R9/S@-49 R16/S@-139 | - | None | 6/miter |  |
| 15 | 15 | (18.3, 7.7) | Bend | R22/S@-136 R10/S@130 | - | None | 6/miter |  |
| 16 | 16 | (-79.8, 120.6) | Junction X | R11/S@-48 R20/S@-138 R12/S@131 R19/S@41 | none (best pair bends 0.6) | AllWayStop | 7.5/7.5/7.5/7.5 (3.28 m) |  |
| 17 | 17 | (-128.9, 177.0) | Junction T | R12/S@-49 R17/S@-141 R16/S@41 (-9.0%) | R17+R16 (bend 2.1) | MinorStop | 7.5/straight/7.5 (17.24 m) | SteepApproach |
| 18 | 18 | (-60.2, -60.6) | Bend | R13/S@131 R23/S@39 | - | None | 6/miter |  |
| 19 | 19 | (-158.3, 52.3) | Junction T | R14/S@-49 R15/S@131 R21/S@40 | R14+R15 (bend 0.4) | MinorStop | straight/7.5/7.5 (6.54 m) |  |
| 20 | 20 | (-207.4, 108.8) | Bend | R15/S@-49 R18/S@43 | - | None | miter/6 |  |
| 21 | - | (13.0, -25.4) | ClassChange | R0/H@-31 R0/M@154 | - | None | - |  |
| 22 | - | (-185.5, 156.7) | ClassChange | R1/M@-35 R1/H@151 | - | None | - |  |
| 23 | - | (-255.7, 194.5) | DirtApron | R1/H@-31 R6/D@-64 R1/H@151 | R1+R1 (bend 2.0) | MinorStop | 4.2/9/straight | AcuteArms |
| 24 | - | (-119.2, -23.2) | ClassChange | R2/H@-135 R2/M@45 | - | None | - |  |
| 25 | - | (-203.7, -95.3) | DirtApron | R2/H@-130 (+11.0%) R6/D@105 R2/H@36 (-11.0%) | R2+R2 (bend 13.2) | MinorStop | 9/9/straight | SteepApproach |
| 26 | - | (25.4, 111.4) | ClassChange | R3/M@-130 R3/H@54 | - | None | - |  |
| 27 | - | (89.6, 152.8) | DirtApron | R3/H@-18 R8/D@-127 R3/H@175 | R3+R3 (bend 13.5) | MinorStop | 9/9/straight | ShortArm, AcuteArms |
| 28 | - | (185.7, -45.7) | DirtApron | R4/H@-9 R4/H@169 R8/D@127 (-10.0%) | R4+R4 (bend 1.4) | MinorStop | straight/6.5/9 | SteepApproach, AcuteArms |
| 29 | - | (325.9, -90.6) | DirtApron | R4/H@-47 R7/D@-168 R4/H@143 | R4+R4 (bend 9.6) | MinorStop | 9/8.1/straight | ShortArm, AcuteArms |
| 30 | - | (141.2, -250.1) | DirtApron | R5/H@-61 (+15.1%) R5/H@143 (-15.1%) R7/D@52 (+9.5%) | R5+R5 (bend 23.2) | MinorStop | 12/9/9 | SteepApproach |

## Decisions for you (05 §9), needed before R1 and R4

These are decision **U8** in PLAN §7.2 (its defaults are in brackets). What the bake measured is in the "Why" column.

| # | Question | Needed by | My recommendation | Why |
|---|---|---|---|---|
| 1 | **The town square.** Make it a 4-way roundabout and re-route the first ~40 m of the harbor road so it joins as the fourth exit, or keep a 3-way roundabout and move the harbor turn-off ≥ 45 m out along the fuel road? [4-way] | R4 (M4); until then the square stays as today ("legacy"), and R1 skips nodes 0 and 1 | **4-way roundabout with the harbor road re-routed** (05 §7.1). It is an override in canon v1.1 (the canon files stay as they are), but it changes the look of the square, so it needs your OK. | The harbor road leaves 13.9 m from the square's centre (nodes 0 and 1, the only pair closer than 30 m); the 14 m stretch between them has room for neither junction (each needs ~11.5 m). The 3-way option needs a longer re-route and puts a T 30 m from node 2. |
| 2 | **Where the town delivery point goes** once the square's delivery ring becomes the roundabout island: a kerbside loading bay on Main Street, or a small car park? [loading bay] | R4 (M4) | **Kerbside loading bay on Main Street**, near the shops (for example on R2 between the square and node 7). | No new land needed; the square's fountain moves onto the roundabout island (05 §5.11). |
| 3 | **One traffic light at node 2** (Route 9 x the downtown streets), or none? [none at first] | M3 controls (signs and stop lines); the signal itself in R5 | **None for now**: node 2 becomes a two-way stop (the side streets stop, Route 9 drives through). A light can be added later as set dressing. | Our dozen AI cars never come near a real signal warrant (05 §6.2); the 10 real towns have a median of one signal. The bake already makes Route 9 the through road at node 2 (the old code had it backwards). |
| 4 | **Keep the canon street widths** (6.2 m streets, 1.5 m sidewalks right at the kerb, no parking lanes), or widen streets for parking (moves every lot front)? [keep] | R1/R2 (M3) | **Keep.** | Downtown corners already only fit a 6 m kerb radius: at nodes 7 and 9 a 7.5 m kerb would pass 2.1 m and 1.2 m from shop walls (measured). Wider streets would push into the buildings. |
| 5 | **Keep the steep (-9 %) approaches** at nodes 5 and 17, or allow a re-grade of up to ~0.6 m? [keep] | R1 (M3) | **Keep**, and put each stop line on the flattest spot within 4 m. | Measured: -9.1 % (node 5, R18), -9.0 % (node 17, R16), +6.2 % (node 3, R5); everything else at paved junctions is under 5 %. Re-grading changes canon heights near houses. |

One more question R4 will raise (not in 05 §9; the default needs no answer now): four dirt roads meet the highway at sharp
angles (node 23: 33 degrees, 28: 43, 29: 49, 27: 57). R4's default keeps the canon angles with a longer paved apron; the
alternative is to straighten the last 20-30 m of those dirt roads so they meet at 60 degrees or more.

## Findings for R1-R4 (no decision needed)

- **Short stubs at the yards.** The harbor dead end (node 10) is only 2.5 m past where dirt road R8 meets R3 (node 27), and
  the mill dead end (node 11) 6.0 m past where R7 meets R4 (node 29). R4 should make each a single yard entrance (apron and
  yard pad as one surface).
- **Dirt aprons on steep highway.** Nodes 25 (R2, +-11 %), 28 (R8, -10 %) and 30 (R5, +-15 %, R7 +9.5 %) sit on grades far
  steeper than any town junction; R4's 10 m apron and the dirt stop line need the flattest-spot rule too.
- **Node 2's R22 arm is tight**: its 72-degree corners need 15.3 m of mouth on a 33.5 m arm (room by 05's 0.45 x length
  rule: 15.1 m). R1's radius back-off will take 7.5 m down slightly there.
- **Town zone.** The street hull + 20 m reaches 233 m from the centre (05 said "about 160 m": the north-west street corner R9
  is 213 m out). Class changes sit on R0 at 100 m, R1 at 171 m, R2 at 72 m (only 21 m past node 7) and R3 at 126 m from the
  centre.
- **Corners as 05 §7 plans them**: 6 m at nodes 7 and 9 (all corners), 7.5 m at nodes 2, 4, 5, 16, 17, 19, 12 m at node 3,
  9 m at dirt aprons (less where the angle is sharp), 6 m inside every L-bend with a square outer corner.

## Deviations

1. **Downtown radius rule.** 05 §5.3 says "6 m where a commercial lot is within 30 m of the corner"; that marks node 4 as
   downtown, which contradicts 05 §7 (7.5 m at node 4). I used 05 §1.3's actual measure instead: a node is downtown when a
   7.5 m kerb would pass closer than 2.5 m to a building, and then all its street/main-road corners take 6 m. This gives
   exactly §7's plan (6 m at nodes 7 and 9 only) and reproduces §1.3's clearances (1.22, 2.09, 3.34, 3.95, 3.28 m).
2. **Highway and MainStreet rank equally** for the major-road pick (MainStreet is the same through road inside town), so a
   class change at a junction can never decide it. Street ranks below, dirt below that.
3. **Corners and mouths are provisional** (05 §5.3's straight-line formula) and exist only for the debug view and the
   R1 hand-over; nothing renders kerbs yet.
4. **Controls are provisional** (05 §6.2 rules); nodes 0 and 1 have none until U8. No signal anywhere.
5. **Four new ClassChange nodes** (05 §5.1 step 3) are not in 05 §1.2's table, which predates the zone split; they are
   counted separately in every check.
6. **Screenshots**: besides the in-engine RoadsDebug views (a PlayMode test), the bake can draw a plan map on the CPU so a
   picture exists even without a graphics device.

## For I

1. **`OWNERS.json`** (commit `d4a0457 REQUEST FOR I`): lane R owns `Game/Assets/Backroads/Tests/PlayMode/Roads/`. The
   RoadsDebug test renders, so it is a PlayMode test (EditMode always runs `-nographics`). `Repo.OwnersRespected` flags
   exactly this file.
2. **Architecture test lists** (`Tests/EditMode/TestUtil.cs`): add `Backroads.Roads.Client` to `TestUtil.Client` so its
   `!UNITY_SERVER` constraint is checked, and let `IsClientName` treat names ending in `.Client` as client (lane V's
   `Backroads.Vehicles.Client` is the same pattern). Today a shared assembly could reference `Backroads.Roads.Client` and
   rule 2 would not notice.
3. **F7** opens the road graph overlay in Development builds and the editor (any scene, Millbrook included, through
   `PresentationRegistry`; not compiled into Client release builds). It is a debug key, so it is not in `bindings.json`;
   please keep F7 free there (lane W), or tell lane R which key to use.
4. **`tools\ci\roads-bake.ps1`** (PLAN 1.9): the entry point exists, `-executeMethod Backroads.Roads.Editor.RoadBakeCli.Bake
   -verifyHash` (exit 0 current, 1 stale or not deterministic, 2 error). Add the script and an M3 gate step when you want
   them; meanwhile EditMode `Roads.BakedAssetUpToDate` catches a stale asset.
5. **Build contents**: `Roads/Baked/Resources/BrRoadNetwork.asset` (168 KB) ships in every build, server included (the
   server needs the road data from M3 anyway); the two debug materials in `Roads/Client/Resources` ship in client builds
   (a few KB). `RoadsDebug.unity` is an editor test scene and does not need to be in the build scene list.
6. **U8 timing**: the M2 card asks for the 05 §9 decisions before R1/R4; PLAN §7.2 has U8 due at week 12. M3 (R1, R3,
   controls) needs only questions 3-5 and their defaults work, so nothing blocks M3; R4 (M4) needs questions 1-2.
7. **Screenshots** live in `artifacts\roads\` of this worktree (gitignored); copy the ones below into the M2 report if you
   want them kept.

## Next (M3, lane R)

R1 (corners, mouths, junction polygons, ring mesh, heights and crown warp, seams) on top of this asset, then R3 (markings,
materials, chunks, LOD, colliders), plates and controls, per PLAN §6.1 M3. The square stays legacy until U8. The provisional
corner data (radius, tangents, clearance) is the starting point for R1's solver and its back-off.

## Resume

```powershell
cd D:\Backroads-Unity-wt\r
git log --oneline -8
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.Bake                       # re-bake (writes the asset + artifacts\roads\graph-report.txt)
tools\ci\project-unity.ps1 -Method Backroads.Roads.Editor.RoadBakeCli.Bake -Extra '-verifyHash'  # check the committed asset is current
tools\ci\project-unity.ps1 -Method Backroads.Roads.Client.Editor.RoadsDebugSceneBuilder.Build     # re-write RoadsDebug.unity + its materials
tools\ci\unity-tests.ps1 -Platform EditMode -Category Roads                                       # lane R EditMode tests (also writes the plan maps)
tools\ci\unity-tests.ps1 -Platform PlayMode -Category Roads -MinTests 1                           # RoadsDebug views (needs graphics)
```

## Screenshots

All in `D:\Backroads-Unity-wt\r\artifacts\roads\` (re-made by the Roads tests):

- `rd-graph-downtown.png`: **the junction graph over Millbrook**, in the engine (RoadsDebug, mode 1): node numbers, cyan
  through roads, red arms that stop, yellow junction mouths, white kerb corners, magenta rings on the square (decision U8).
- `graph-town.png`: the same graph as a labelled plan map with a legend (CPU-drawn over the canon terrain and building
  footprints); `graph-overview.png` shows the whole network.
- `rd-ribbons-side-by-side.png`: lane W's M1 ribbons (left) next to R0's plain ribbons coloured by class (right), same
  view. Also `rd-graph-town.png` (town from above), `rd-r0-ribbons-node2.png` (node 2 close-up), `rd-graph-network.png`.
