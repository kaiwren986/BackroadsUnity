# 05 — Roads and "proper street intersections" for Backroads in Unity

Research date 2026-10-18. Inputs: `D:\Backroads` at commit `552f780` (read only), the world generated in Node exactly as the game does (`world.generate(20251015)`, 574 ms), `assets/towns/canon.json`, the 10 cached OpenStreetMap towns in `tools/osm/cache`, and the sibling reports `03-port-map.md` and `04-unity-stack-online.md`. Probe scripts live in the session scratchpad (`roadprobe/probe*.mjs`), not in the repo. Labels: **[measured]** = I computed it from the game data or OSM; **[doc]** = from a standard or vendor page (linked in Sources); **[unverified]** = needs a check, with how to check it.

This report refines §4.5 (roads) and §4.20 (traffic network) of `03-port-map.md` and uses its conventions: Unity position = (−x, y, z) of the JS position, Unity yaw = −JS yaw, and the export goes into `CanonData/v1`. It assumes the `04` stack: Unity 6.3 LTS (6000.3.26f1+), URP 17.3 with Forward+ and GPU Resident Drawer. All coordinates quoted below as "(x, z)" are **JS coordinates**, so they can be checked against the JS game.

---

## 0. Summary

**Recommendation: write our own C# road and intersection generator.** Call it `Backroads.Roads`. It is driven by the frozen road graph that the JS build exports. It runs in the editor as a deterministic bake and outputs meshes, colliders, a terrain patch, the lane graph and sign/signal placements. All of these come from **one** geometric model, so the asphalt, kerbs, markings, AI lanes and stop lines can never disagree.

- **Building blocks:**
  - **Unity Splines 2.9.1** (the version released for 6000.3): optional, for editor gizmos, `SplineInstantiate` prop rows and nearest-point utilities only. It has no intersection, cross-section or lane features, and converting our 2 m polylines to Bézier knots would move canon geometry.
  - **Clipper2** (C#, Boost Software License 1.0): offsets and polygon unions for kerb loops and sidewalks.
  - **LibTessDotNet** 1.1.15 (SGI Free Software License B 2.0), or a 150-line ear clipper: triangulation fallback.
  - **Burst/Jobs + `Mesh.AllocateWritableMeshData`**: meshing. **`Physics.BakeMesh`**: off-thread collider cooking.
- **Don't buy a road tool to do this.** None of the Asset Store tools covers all of these at once:
  - generate from *our* frozen polylines and heights,
  - shape *our* custom heightfield (03 recommends a custom terrain mesh, not Unity Terrain),
  - hand back a lane and turn graph for the AI.

  **EasyRoads3D Pro v3 ($45)** comes closest. It has a real runtime API (`ERRoadNetwork.CreateRoad`, `ConnectRoads`, `InstantiateConnection`, `ERRoad.ConnectToStart/End`, `BuildRoadNetwork`), T/X crossings and roundabouts. It is worth $45 only as a quick visual prototype or a fallback. CiDy 2, MicroVerse Roads, R.A.M and Polaris are not road-graph tools for this job (§2).
- **What Millbrook needs** [measured]:
  - The paved network has 11 nodes with 3 or more arms: 5 T and 6 X (counting the 3-arm square as a T).
  - There are also 5 L-shaped street corners, 6 dirt-road-to-highway T junctions that the traffic network ignores today, 5 work-site dead ends, and up to 51 driveway or apron curb cuts.
  - **Four problems must be fixed while porting:**
    1. The harbor road branches off **13.8 m** from the town-centre roundabout node. It has to become a proper 4th leg (or move), and its first ~40 m must be realigned, because the fuel and harbor legs are only 31–50° apart at 20–30 m from the centre (§7.1). This is a user decision.
    2. `trafficNet.js` picks the "major road" as the *straightest* arm pair. At nodes 2 and 5 that makes the canon **streets** major and the **highway** yield, which is the wrong way round under MUTCD §2B.07. Rank by road class first.
    3. Downtown corners only have room for about a **6 m (20 ft) kerb radius**. At nodes 7 and 9, a 7.5 m radius leaves only 1.2–2.1 m between kerb and shopfront, and 12 m hits a building.
    4. Junction approaches reach −9% grade at nodes 5 and 17. Keep them, because the heights are canon, but stop lines must sit on the flattest available spot.
- **Traffic control for a North American small town:**
  - 1 roundabout (yield on entry).
  - At most **one** signal. In the 10 measured towns, OSM shows a median of 1 signal within 1.6 km of the centre (counts 0,0,0,1,1,1,2,2,3,14), and no `junction=roundabout` ways at all.
  - Two-way stops wherever a street meets a highway.
  - Stop on the stem of street T junctions, an all-way stop at the one street-by-street X (node 16), and stop signs on dirt roads where they join pavement.
  - Placement follows MUTCD 11th edition §2B.18 (near side, right-hand side, no more than 50 ft from the intersected pavement edge) and §3B.19 (stop line 12–24 in wide, at least 4 ft before the crosswalk). Details in §6.

---

## 1. What we start from [measured]

### 1.1 Road inventory (from `W.ROADS` after `generate(20251015)`)

| Group | Count | Half-width `hw` | Length | Where it comes from | In `canon.json`? |
|---|---|---|---|---|---|
| Paved through roads ("highways") R0–R5 | 6 | 3.6 m (7.2 m wide) | 1.52 km | A* routing between POIs (`world.js buildRoads`) | No. Deterministic from the seed, and exported by 03's `CanonData/v1/world.json` |
| Dirt roads R6–R8 | 3 | 2.7 m | 0.85 km | Same | No |
| Town streets R9–R23 (`kind` cross/parallel) | 15 | 3.1 m (6.2 m wide) | 1.03 km | `townGen.js` grid, frozen | **Yes** (`streets[]`, y rounded to mm) |

- Points: 1,722 in total, spaced 2.00 m on average (2.84 m max).
- Tightest centreline radius (5-point estimate): 11.1 m on paved roads (R5 at (117, −233)) and 5.5 m on dirt (R7 at (172, −143)). Offsetting by `hw` is therefore safe everywhere: the inner edge radius stays at least 2.8 m.
- 1,777 m of road lies within 165 m of the town centre (−70, 30), at ground height y ≈ 13.33.
- Roads touch 45 of the 256 terrain chunks (64 m each); 37 of those contain paved road.

### 1.2 Node inventory (built the way `trafficNet.buildNetwork` does)

| Node | (x, z) | Arms (road@angle°) | Type | Notes |
|---|---|---|---|---|
| 0 | (−70.0, 30.0) | R0@−18 (to fuel), R1@127 (depot), R2@−127 (farm) | 3-way, roundabout today (lane radius 11.5) | Town square; 26 m gravel disc; the delivery ring sits in the middle |
| 1 | (−56.8, 25.7) | R0@162, R0@−32, R3@48 (harbor) | T | **13.8 m from node 0**, inside where a roundabout's outer edge goes |
| 2 | (−5.7, −15.7) | R0 ×2 (hwy), R22/R23 (streets) | X | Arm gaps 113/72/97/78°. **Code picks the streets as major** |
| 3 | (85, −85) | R0, R4, R5 (all hwy) | T | Inside the Route 9 Fuel yard; major = R0+R5 |
| 4 | (−120.2, 84.2) | R1 ×2, R20/R21 | X | |
| 5 | (−170.0, 143.9) | R1 ×2, R17/R18 | X | Approach −9.1% over 20 m; **code picks the streets as major** |
| 7 | (−104.6, −8.6) | R2 ×2, R13/R14 | X | Downtown shops |
| 9 | (−25.1, 58.9) | R3 ×2, R10/R11 | X | Downtown shops |
| 16 | (−79.8, 120.6) | R11, R12, R19, R20 (all streets) | X | Only street-by-street X |
| 17 | (−128.9, 177.0) | R12 (stem), R16/R17 | T | One arm −9.0% |
| 19 | (−158.3, 52.3) | R14/R15, R21 (stem) | T | |
| 13, 14, 15, 18, 20 | grid corners | 2 streets at ~90° | L-bend | Today two separate edges plus a connector |
| 6, 8, 10, 11, 12 | depot, farm, harbor, mill, quarry | 1 | dead end | Balloon turnaround loops (`fitLoop`) |
| (not in net) | (−204,−95), (−256,194), (326,−91), (141,−250), (90,153), (186,−46) | dirt R6/R7/R8 ends | dirt-on-highway T | Distance to the paved centreline 0.0 m |

- **Angles:** the smallest angle between two arms at any junction is 71.9°. Junctions are near-orthogonal but skewed by up to 18°.
- **Grades** within 20 m of a junction are below 1.6% everywhere except:
  - node 3: +6.5%;
  - node 5: −9.1% on R18;
  - node 17: −9.0%.

### 1.3 Space the canon leaves for kerbs and sidewalks (distance from the asphalt edge, i.e. from `hw`)

| Canon item | Min | Median | What it means |
|---|---|---|---|
| Lot front line | 1.20 | 1.20 | `frontLot` uses `hw + 1.2` |
| Shop (Shop_A/B) wall, 17 shops | **2.25** | 3.08 | Downtown sidewalk = kerb to shopfront, about 2.1–2.25 m |
| House wall, 34 houses | 3.34 | 12.3 | A residential walk of 1.5 m fits |
| Mailbox, 34 | 1.72 | 1.80 | A kerb-tight 1.5 m walk (0.15 kerb + 1.5) ends at 1.65 m, just in front of the mailboxes |
| Driveway / shop apron (`concrete`), 51 | 0.00 | 0.00 | Up to 51 curb cuts with dropped kerbs (most pads touch the asphalt edge; the farthest is 1.86 m away) |
| Yard trees | 3.70 | 15.8 | No conflict |

**Kerb-radius fit at the corners**: the closest building corner to the filleted kerb line, by radius r.

| Node | r = 6 m | 7.5 m | 9 m | 12 m |
|---|---|---|---|---|
| 9 | 1.72 | 1.22 | 0.70 | **−0.35** (hits a building) |
| 7 | 2.62 | 2.09 | 1.55 | 0.45 |
| 2 | 3.34 | 3.34 | 3.34 | 3.34 |
| 4 | 4.18 | 3.95 | 3.52 | 2.52 |
| 16 | 3.28 | 3.28 | 3.28 | 3.28 |
| 19 | 6.89 | 6.89 | 6.89 | 5.96 |

### 1.4 How the JS game draws roads today

- `roads.js` lays one flat ribbon per paved road at `y + 0.045`, double-sided, with `polygonOffset`.
- Junctions are simply overlapping ribbons. The dashed centre line fades out 6–16 m before a junction end (`roadFade`).
- Dirt roads are terrain colour only.
- `carveRoad` flattens the terrain within `hw + 0.9` and blends it out to `hw + 8`. The result depends on order: `SURF.ROAD` is never re-carved.

This is what "proper street intersections" replaces.

---

## 2. Options survey

Prices are as listed on 2026-10-18, in USD, per seat and before tax.

| Tool | Price | Version / date | Unity 6 | Can it be driven from our data? | Lanes for AI? | Shapes our terrain? | Licence | Verdict |
|---|---|---|---|---|---|---|---|---|
| **Custom generator + Unity Splines 2.9.1** | free | Splines 2.9.1 (2026-09-03) is "released" for 6000.3 [doc] | yes | yes, by design | yes, same geometry | yes, our own patch | Unity Companion Licence | **Recommended** |
| **EasyRoads3D Pro v3** (AndaSoft) | $45 | Store: v3.2.5f1; publisher site also lists v3.3 beta 23 | Listing: 6000.0.3f1, Built-in/URP/HDRP; a user reports 6.3 LTS URP works [doc] | Yes (Pro scripting API, editor or runtime): `new ERRoadNetwork()`, `CreateRoad(name, ERRoadType, Vector3[] markers)`, `ConnectRoads(r1, i1, r2, i2)`, `InstantiateConnection(src, name, pos, euler)`, `ERRoad.ConnectToStart/End(conn, index)`, `AttachToStart/End`, `SetMarkerControlType(i, ERMarkerControlType.StraightXZY)`, `BuildRoadNetwork(splat, trees, detail, roads[])`, `RestoreRoadNetwork()`, `FinalizeObjects()`, `GetSplinePointsCenter/Left/Right()` | No (you would read the spline points back) | **Unity Terrain only.** The release notes warn that terrain changes from scripted networks "may not always be restored" | Asset Store EULA | Fallback or prototype only. Crossings are prefab-based; roundabouts exist |
| **CiDy 2** (Reckless Games) | $88.70 | 2.7.4, 2025-10-21 | Not listed (original 2018.4.36) [unverified] | No public API found; editor scene-view city designer | Unknown | Unity Terrain | Asset Store EULA | Reject: overlaps the frozen canon town |
| **Road Constructor** (Pampel Games) | $69.95 list ($25.18 on sale today) | 1.9.6, 2026-10-16 | Listing shows 2022.3.31f1 and 2022.3.62f3 only [unverified for 6.3] | API "with full source code" and runtime placement, according to an 80.lv article; DOTS Traffic City docs: "Roundabouts are not currently supported for runtime scene" | Via DOTS Traffic City (paid, separate) | Terrain fitting | Asset Store EULA | Reject: 2022.3 target, no runtime roundabouts |
| **MicroVerse Roads** (Jason Booth) | $55 list ($27.50 sale) + MicroVerse $77 + MicroVerse Splines $55 | 1.7.45, 2025-11-11 | Original 2021.3 [unverified for 6.3] | Editor stamping on Unity Splines; intersections are prefabs (`MVR_UserPrefab` per the DOTS Traffic City docs) | No | Unity Terrain stamping | Asset Store EULA | Reject: editor-only, needs Unity Terrain, ~$187 |
| **R.A.M 2019 → R.A.M 3** (NatureManufacture) | not confirmed | R.A.M 3 lists "Unity 2022.3, 2023.2, Unity 6" | yes per listing | River and road splines carving terrain; no intersection tooling found | No | Unity Terrain | Asset Store EULA | Reject: rivers first |
| **Polaris 3** (Pinwheel) | in the Terrain Tools Bundle, $199 (v1.5, 2026-08-17; Built-in/URP, not HDRP); Polaris 2021→3 upgrade $19 | — | 6000.0.25f1 listed | Low-poly *mesh terrain* with a spline "ramp" tool; no intersections | No | Its own mesh terrain | Asset Store EULA | Not a road tool; only matters if the terrain report picks it. No EasyRoads3D integration was confirmed [unverified] |
| **Road Architect** (FritzsHero fork of MicroGSD) | free | Last commit 2026-10-08; tag 2.0.0 | README says it was developed on 2020.3 [unverified on 6.3] | Editor tool; intersections "procedurally generated based upon lanes, road width and angle of attack", with turn lanes and signals | Partial | Unity Terrain | **MIT** | A good algorithm donor (MIT allows copying with notice). Not a dependency |
| **Road Creator** (MCrafterzz) | free (MIT, archived 2021-02-10); Pro on itch is name-your-price (2.3.1, 2022-12-18; source may not be redistributed) | — | 2021.3+ | Editor-only; the author says runtime use "would require substantial rework" | No | Unity Terrain | MIT / custom | Reference only |
| **osm2streets** (A/B Street) | free | active | n/a (Rust) | `osm2streets/src/geometry/general_case.rs` `trim_to_corners`: intersect adjacent road-edge polylines, trim each road at the perpendicular through the hit, build the polygon from the corners | yes (lanes) | n/a | **Apache-2.0** | Algorithm reference for §5.3–5.5 |

**Why custom wins here.** Each requirement below rules out the commercial tools on its own.

- **(a) Canon:** the street polylines and heights are frozen. Any tool that re-splines markers moves them. EasyRoads3D can be pinned by passing every 2 m point as a `StraightXZY` marker, but that is unverified and clumsy.
- **(b) Terrain:** our terrain is a custom deformable heightfield (03 §4.3). Every tool above shapes Unity Terrain only.
- **(c) Traffic:** the AI needs stop lines, turn paths and conflict data that line up with the drawn kerbs. No tool exports those.
- **(d) Multiplayer:** a dedicated server must build identical colliders headless. A baked-asset pipeline we own does that trivially.

The geometry itself is well understood: osm2streets, Road Architect (MIT) and the Wilkie–Sewall–Lin paper (UNC) all describe it. The volume is modest: 3.4 km of road and about 30 junction-like nodes.

**How to verify any [unverified] Unity 6 claim in 30 minutes:**

1. Create `D:\Backroads-ref\probe\roads-<tool>` with Unity 6000.3.x URP.
2. Import the package.
3. Check the Console for compile errors after the domain reload.
4. Open the demo scene, press Play, and look for pink materials (shader errors).
5. For EasyRoads3D, also run a 20-line script that calls `new ERRoadNetwork().CreateRoad("t", rt, pts)` followed by `BuildRoadNetwork()`.

---

## 3. Architecture

```
JS build (oracle, unchanged)                Unity editor bake (Backroads.Roads.Editor)                    Runtime
--------------------------------           -----------------------------------------------------         ---------------------------
CanonData/v1/world.json  ─┐                1 RoadGraphImporter   : roads → edges, nodes, classes         RoadChunk prefabs (LODGroup)
  roads[] (pts x,y,z,d,hw)│                2 GraphCleanup        : split, snap, collapse, merge 0+1       MeshColliders (baked)
  lots[], POIs            ├─ ScriptedImporter 3 Arms + Corners     : offsets, fillets (per-class r)       LaneGraph.asset  ─► Traffic AI
CanonData/v1/traffic.json │  (*.roadgraph)  4 Mouths/Stops/XWalks : trims, stop lines, crosswalks         Controls.asset   ─► signals, signs
CanonData/v1/terrain/*    ┘                5 Surfaces            : ribbons, junction polygons, roundabout  RoadPlates.f32   ─► WorldGrid patch
roads-overrides.json (new, hand-edited:    6 Heights             : plateau, crown warp, drainage           Props list       ─► signs, lamps,
  per-node control, radius, roundabout      7 Kerbs/Sidewalks     : Clipper2 block loops, ramps, cuts                            inlets, poles
  spec, R3 realignment polyline)           8 Markings            : lines, stop bars, crosswalks, yields
                                           9 Terrain plates      : delta heightfield + surface ids
                                           10 Chunk + LOD + colliders
                                           11 Lanes + connectors + conflicts + controls
                                           12 Validation (fail the bake on errors)
```

- **Bake, don't generate at load.** The outputs are committed (Git LFS for `.asset` meshes, or plain YAML because they are small).
  - Clients and the dedicated server then load *identical* colliders (`04`: owner-authoritative cars, server validation).
  - A CI check re-bakes and compares hashes. Burst float results are not guaranteed bit-identical across CPUs, so the bake must never run at runtime.
  - Budget [estimate]: about 25k vertices of asphalt, kerb and sidewalk plus about 10k of markings for the whole map. The bake takes under 2 s in the editor.
- **Inputs are frozen, overrides are explicit.** Everything this report changes relative to canon goes into a small hand-edited `roads-overrides.json`, versioned as canon v1.1 the way 03 suggests "junction plates":
  - the R3 realignment polyline,
  - the per-node control type,
  - per-corner kerb radii,
  - roundabout dimensions.

  The canon files themselves are never edited.

---

## 4. Data model (C#, `Backroads.Roads` runtime assembly; all Unity coordinates)

```csharp
public enum RoadClass : byte { Highway, MainStreet, Street, Dirt, Yard, Driveway }
public enum NodeKind  : byte { Junction, Bend, Join, DeadEnd, Roundabout, DirtApron, ClassChange }
public enum Control   : byte { None, Yield, MinorStop, AllWayStop, Signal, RoundaboutYield }
public enum KerbType  : byte { None, Barrier150, Mountable100, Apron75, Dropped25 }
public enum LineStyle : byte { None, SolidWhite, DashedYellow, DoubleYellow, SolidYellowDashed, DottedWhite }

[System.Serializable] public struct CrossSection {
    public float halfWidth;     // centreline → kerb face / pavement edge. Canon: 3.6, 3.1, 2.7
    public float laneOffset;    // centreline → lane centre (halfWidth/2 for 2 lanes; 1.8 today)
    public float crown;         // cross slope each side: 0.02 paved, 0.04 gravel
    public KerbType kerb;       // Barrier150 in town, None rural
    public float gutter;        // 0.45 m concrete pan inside the kerb face (part of halfWidth)
    public float sidewalk;      // 0 none; 1.5 residential; <0 = "fill to frontage" (storefront)
    public float shoulder;      // rural gravel shoulder, 1.2
    public LineStyle centre;    // DoubleYellow on Main/Highway in town; none on residential streets
    public bool edgeLines;      // rural highway only
    public float speed;         // m/s, for lanes: 11 town, 16 open, 8 yard
}

public sealed class RoadEdge {
    public int id, nodeA, nodeB, sourceRoad;   // sourceRoad = index into world.json roads[]
    public RoadClass cls; public CrossSection xs;
    public float3[] c; public float[] s;       // centreline (canon heights) + arc length
    public float sMouthA, sMouthB;             // trims measured from each end
}

public sealed class Arm {                      // one edge end, seen from its node, oriented OUTWARD
    public int edge; public bool atA;
    public float2 dir; public float angle;     // unit dir at ~10 m out; angle = atan2(dir.y, dir.x), CCW from above
    public Polyline2 centre, left, right;      // left = kerb-face line on the CCW side (normal (-dz, dx))
    public float sTangentL, sTangentR;         // where the corner fillets touch this arm
    public float sMouth, sStop, sCwInner, sCwOuter; // geometry mouth, stop line, crosswalk band
    public bool approachControlled;            // has STOP/YIELD/signal
}

public sealed class Corner {                   // between arms[i] and arms[i+1] (CCW)
    public enum Kind : byte { Fillet, Straight, Miter, Nose }
    public Kind kind; public float radius; public float2 centre;
    public float2 tOnI, tOnJ; public Polyline2 arc;
    public RampKind ramp;                      // DepressedCorner (default), Perpendicular, None
}

public sealed class RoadNode {
    public int id; public float3 p; public NodeKind kind; public Control control;
    public List<Arm> arms;                     // sorted by angle (CCW from above)
    public List<Corner> corners;               // corners[i] joins arms[i] → arms[(i+1)%n]
    public int majorA = -1, majorB = -1;       // arm indices of the through road, if any
    public float4 plane;                       // plateau plane when there is no major road
    public RoundaboutSpec rb;                  // when kind == Roundabout
    public Polygon2 surface;                   // asphalt polygon (CCW), filled in §5.5
}

// AI (replaces trafficNet's lanes/conns/loops, same semantics)
public sealed class Lane      { public int id, edge, fromNode, toNode; public sbyte dir; public float3[] pts; public float stopS, speedCap; public bool noTruck; }
public sealed class Connector { public int id, node, fromLane, toLane; public TurnKind turn; public float3[] pts; public float[] k, vcap;
                                public byte phaseMask; public bool encroaches; public List<Conflict> conflicts; }
public struct Conflict        { public int other; public float sSelf, sOther; public ConflictKind kind; public Rule rule; } // Cross/Merge/Diverge/Ped; SelfYields/OtherYields/FIFO/Phase/Compatible
```

---

## 5. The intersection algorithm

### 5.1 Build and clean the graph

```
RoadGraph Build(WorldJson w, Overrides o):
  for r in w.roads:
     pts = r.pts.Select(p => new float3(-p.x, p.y, p.z))      // 03 §2 coordinate rule
     cls = r.street ? Street : r.paved ? Highway : Dirt
     add edge (pts, cls, xs = CrossSectionFor(cls, zone))
  // 1. T-split: every road end flagged startJ/endJ is projected onto the nearest OTHER road
  //    (segment projection, tolerance 1.0 m). If it lands > 2 m from that road's ends, split there.
  //    This now also catches the 6 dirt ends (trafficNet skips them because it is paved-only).
  // 2. Cluster all ends and split points within 1.0 m into nodes (trafficNet uses 6 m; too coarse once
  //    kerbs are drawn).
  // 3. Zone split: split Highway edges where they cross the town kerb zone
  //    (the convex hull of all Street edges, buffered 20 m; it reaches about 160 m from the centre).
  //    Inside → MainStreet (kerbed, sidewalks, double yellow). "MainStreet" here means ANY through road
  //    inside the zone (R0–R3 near the town), not only townGen's main pair R0+R1.
  //    Outside → Highway (shoulders, edge lines).
  //    The split node is ClassChange: kerb taper 3 m, gutter run-out, sidewalk ends at a ramp.
  // 4. Degree-2 nodes:
  //      deflection < 25° and same class → Join (merge polylines, remove node)
  //      deflection ≥ 25°               → Bend (the L-corners 13, 14, 15, 18, 20)
  // 5. Complex junctions: any two junction nodes closer than 30 m (centre to centre) → ERROR unless
  //    Overrides gives a resolution. Today: nodes 0 and 1 (13.8 m) → o.roundabout(0) with R3 realigned (§7.1).
  // 6. Classify: degree 1 → DeadEnd (yard pad, keep fitLoop); Dirt × paved → DirtApron;
  //    degree ≥ 3 → Junction (or Roundabout if overridden).
  // 7. Major road at each Junction: highest class pair first, straightness only as the tie-break:
  //      score(i,j) = 10*min(rank(i),rank(j)) + 2*(rank(i)==rank(j)) - |π - |Δangle||
  //    (fixes nodes 2 and 5). No major if the best pair bends > 35° (0.6 rad, as now) or the classes are equal and
  //    the control is AllWayStop/Signal.
```

### 5.2 Arms and edge lines

```
for node in junction-like nodes:
  for each incident edge end:
     centre = edge polyline oriented away from the node
     dir    = normalize(PointAtDistance(centre, max(10, 1.5*hw)).xz - node.p.xz)
     angle  = atan2(dir.y, dir.x)                      // CCW seen from +Y
     left   = Offset(centre, +xs.halfWidth)            // left normal n = (-d.y, d.x) in (x,z)
     right  = Offset(centre, -xs.halfWidth)
  sort arms by angle
Offset(poly, w): per-vertex miter normal (average of adjacent segment normals), miter length capped at 2|w|.
                 Valid because the tightest paved radius is 11.1 m (> 3.6) and the tightest dirt radius is 5.5 m (> 2.7).
```

### 5.3 Corner fillets

Kerb-face radius (the pavement-edge radius where there is no kerb), by context:

| Corner between | Context | r | Why |
|---|---|---|---|
| Street–Street | residential | **7.5 m (25 ft)** | Typical local-street standard (Kittitas Co. WA: 25 ft local/local; a township code: 25 ft residential) |
| Street–MainStreet | downtown (lots `zone: com` within 30 m of the corner) | **6.0 m (20 ft)** | Measured fit at nodes 7 and 9 (§1.3). Old downtowns have tight corners, and trucks swing wide |
| Street–MainStreet | residential | 7.5 m | |
| Highway–Highway (node 3, fuel yard) | truck route, unkerbed | **12 m (40 ft)** | Flagstaff AZ uses 30 ft on truck routes; 40 ft suits the box truck at speed |
| Dirt–Highway | rural, unkerbed | **9 m** edge radius + 10 m paved apron | Keeps gravel off the highway |
| Street L-bend (deg 2) | inner corner | 6.0 m; outer corner = Miter | Keeps the canon centreline; see §5.12 |
| Yard entrance | site | 12–15 m | Trucks |
| Roundabout entry / exit | | 15 m / 20 m | §5.11 |

```
Corner SolveCorner(Arm I, Arm J /* next CCW */, float r):
  θ = WrapPositive(J.angle - I.angle)                 // 0..2π, the corner's opening angle
  if θ >= 200°: return Miter(I.left ∩ J.right)        // reflex: outer side of an L-bend
  if θ >= 160°: return Straight(I.left → J.right)     // the through side of a T, or a gentle Y
  r = min(r, RadiusForMaxTangent(θ, I, J, 25 m))      // acute corners: keep tangent points within 25 m
  for attempt in 0..5:
     A = Offset(I.left,  +r)                          // further CCW = into the block
     B = Offset(J.right, -r)
     C = FirstIntersection(A, B, walkingOutwardFrom: node)   // polyline × polyline, nearest the node
     if C == null: r *= 0.8; continue
     (tI, sI) = ClosestPoint(I.left, C);  (tJ, sJ) = ClosestPoint(J.right, C)
     if sI > I.maxMouth || sJ > J.maxMouth: r *= 0.8; continue   // short arm or another junction too close
     arc = SampleArc(C, r, tI → tJ, side facing node, maxStepDeg: 7.5)
     return Fillet(C, r, tI, tJ, sI, sJ, arc)
  return Nose(I.left ∩ J.right, chamfer: 0.6 m)       // degenerate fallback, logged
```

- This is the polyline form of the classic two-line fillet. Because it offsets the actual edge polylines, it works on curved arms and skewed corners, such as node 2's 72° corner. With r = 6 there, the tangent points land 13 m out. For a straight-line check:

  `tangent distance = (hw_J + r)/sin θ + (hw_I + r)/tan θ`

- `maxMouth` for an arm is `min(0.45 · edge length, distance to the next node − 10 m)`.
- The same routine handles a roundabout entry. There the "edge" B is the outer circle, sampled as a polyline, and its offset is simply a circle of radius `Ro + r`.

### 5.4 Trims, crosswalks, stop lines

```
for arm a (approach side = right half for right-hand traffic):
  sT = max(a.sTangentL, a.sTangentR)                  // furthest fillet touch point on this arm
  sEdge = distance along a where its centreline meets the nearest crossing road's kerb line
  if a.markedCrosswalk:                               // rules in §5.8
      a.sCwOuter = max(sT, sEdge + 0.6 + wCw)
      a.sCwInner = a.sCwOuter - wCw                   // the band sits inside the kerb return, ending at the tangent
      a.sStop    = a.sCwOuter + 1.22 + wStop/2        // MUTCD 3B.19 ¶13: ≥ 4 ft before the crosswalk
  else if a.approachControlled:
      a.sStop = clamp(sT + 0.3, sEdge + 1.22, sEdge + 9.14)   // 3B.19 ¶13: 4–30 ft from the intersected edge
  a.sStop  = max(a.sStop, SweptPathSetback(a))        // §5.14: clear the design truck's turning sweep
  a.sStop  = FlattestWithin(a, a.sStop, a.sStop + 4)  // nodes 5/17: choose the least-grade spot
  a.sMouth = sT + 0.5                                 // asphalt polygon boundary (a short perpendicular stub)
```

Worked example, 90° street corner, r = 7.5, hw = 3.1, residential (unmarked):

- sT = 3.1 + 7.5 = 10.6 m
- sStop = 10.9 m
- The mouth is at 11.1 m.

The same corner downtown (r = 6, hw 3.6/3.1, 3.0 m continental crosswalk):

- sT ≈ 9.6 m
- crosswalk 6.6–9.6 m
- stop line centre at about 11.0 m

### 5.5 Junction surface polygon and triangulation

```
Polygon2 BuildSurface(node):
  P = new List<float2>()
  for i in 0..n-1:
     a = arms[i]; k = corners[i]; b = arms[(i+1)%n]
     P.AddRange(MouthLine(a))           // right→left across a at a.sMouth, INCLUDING every ribbon
                                        // cross-section vertex (gutter, lane edges, crown) so the seam is watertight
     switch k.kind:
       Fillet:   P += a.left.Slice(a.sMouth → k.sI); P += k.arc; P += b.right.Slice(k.sJ → b.sMouth)
       Straight: P += ThroughEdge(a.left @ a.sMouth → b.right @ b.sMouth)
       Miter:    P += [a.left @ a.sMouth, k.miterPoint, b.right @ b.sMouth]
       Nose:     P += [a.left @ a.sMouth, k.nose0, k.nose1, b.right @ b.sMouth]
  Weld(P, 0.02); EnsureCCW(P); Assert(IsSimple(P))   // orientation: Clipper2 Clipper.IsPositive; simplicity: O(n²) segment-pair test
  return P

Mesh TriangulateSurface(node):
  if IsStarShaped(P, node.p.xz):        // the angle around the node increases monotonically along P
      return RingMesh(P, centre: node.p, rings: {0.33, 0.66})   // a centre fan plus 2 rings → interior vertices for the crown warp
  else:
      return EarClip(P)  // or LibTessDotNet (WindingRule.EvenOdd); heights from §5.6 at vertices only
```

- Fillet junctions are star-shaped about the node in practice. Each kerb arc is the near side of a circle that does not contain the node.
- `RingMesh` gives about 3n triangles. n is 60–140 at our junctions, so a junction costs at most a few hundred triangles.

### 5.6 Heights: plateau, crown warp, drainage

The rule ("major road profile carried through, minor roads warped") follows standard practice. Its value: a car on the main road feels nothing at a side-street junction, and the side street ramps onto the main road's cross slope.

```
float JunctionHeight(node, float2 q):
  if node.majorA >= 0:
     (s, u) = ProjectOnto(majorCentrelinePolyline, q)   // u = signed lateral offset
     return MajorCentreY(s) + CrownOffset(major.xs, u)
     // CrownOffset(u) = -crown*|u| for |u| <= hw - gutter;
     //                  gutter pan -0.027 at the face; beyond hw (kerb returns) continue the last slope,
     //                  clamped to ±0.15 m so a wide corner doesn't dive
  else:
     return PlaneY(node.plane, q)
     // least-squares plane through the centre points at every mouth; |∇| clamped to ≤ 3%;
     // each arm's crown runs out linearly to 0 over the last 10 m before its mouth

float ArmY(Arm a, float s, float u):                     // used by the arm ribbon near its mouth
  own = CentreY(s) + CrownOffset(a.xs, u)
  if s >= a.sMouth + Lw: return own                      // Lw = 12 m warp length
  t = SmoothStep01((s - a.sMouth) / Lw)
  atMouth = JunctionHeight(node, PointAt(a, a.sMouth, u))
  carried = atMouth + (CentreY(s) - CentreY(a.sMouth))   // keep the arm's own climb, warp only the cross slope
  return lerp(carried, own, t)
```

- **C0 at the mouth by construction.** Every warp vertex uses the same function the junction uses. Ribbon and junction must share the mouth vertices: the same `float3`, written once.
- **Grades.** Canon heights are kept (03 "never re-carve"). The bake reports any arm whose grade within 10 m of `sStop` exceeds 6%: today nodes 5 and 17, and node 3 at 6.5%. It moves `sStop` to the flattest spot within +4 m. A real re-grade at node 5 would change street heights by about 0.6 m, so it stays an opt-in override.
- **Drainage camber:**
  - Crown: 2% on asphalt, 4% on gravel.
  - Gutter pan: 0.45 m, falling 0.027 m (6%) to the flowline at the kerb face.
  - Sidewalk: 1.5–2.0% cross slope towards the street (the PROWAG limit is 1:48, about 2.1%).
  - Roundabout circulatory roadway: 2% outward (away from the island).
  - **Low points never in crosswalks or ramps.** The bake samples the gutter flowline along every kerb loop and places a **curb inlet** (catch basin, 0.9 m grate):
    - 1.5 m upstream of each kerb return tangent on the uphill approach, outside the ramp;
    - at each local minimum of the flowline;
    - every 90 m on continuous grades.

    These are props with a small dark decal; the puddle mask feeds the wet shader (`TEXCOORD3.x` in 03).

### 5.7 Kerbs, sidewalks, corner ramps, driveway cuts

**Kerb loops from booleans, so asphalt and kerb cannot disagree:**

```
footprint = Clipper.Union( ribbonCorridors(±hw, kerbed edges only) ∪ junction surfaces ∪ roundabout outer discs )
zone      = TownKerbZone polygon (the street-grid hull + 20 m)
blocks    = Clipper.Difference(zone, footprint)            // ClipperD, precision 3 (mm)
kerbLoops = blocks outlines (closed, CCW for outer, CW for holes)  // already contain every fillet arc
walkBand  = Clipper.Difference(blocks, Clipper.InflatePaths(blocks, -wWalk, JoinType.Round, EndType.Polygon))
             // wWalk = 1.65 (0.15 kerb top + 1.5 walk) residential; storefront: fill to lot/building line
```

**Kerb extrusion** along each loop. Profile in (u = into block, v = up), with u = 0 at the kerb face:

- gutter lip (−0.45, 0), level with the asphalt edge → flowline (0, −0.027)
- face top (0.012, 0.15) (10 mm batter) → kerb top back (0.15, 0.15)
- then the sidewalk surface at +2% cross fall towards the street

That is 6 vertices per profile, at 1 m spacing, and 0.25 m on arcs below 8 m radius.

`KerbType` modifies the profile by loop parameter t:

| Type | Use | Face height |
|---|---|---|
| `Barrier150` | default in town | 150 mm (6 in) |
| `Mountable100` | newer residential, optional | 100 mm |
| `Apron75` | roundabout truck apron | 75 mm |
| `Dropped25` | driveways and depressed corners | 25 mm |

**Driveway cuts (up to 51):** for each canon `concrete` op whose rectangle touches the road edge:

- Find its interval on the kerb loop.
- Apply `Dropped25` over the driveway width, plus a 1.0 m linear flare each side (0.15 → 0.025 m).
- The sidewalk crosses at its normal cross slope (≤ 2%), and the apron slab continues to the lot pad at `padY`.

**Corner ramps.** Our walks are narrow (1.5 m residential, about 2.1 m storefront). A perpendicular ramp needs about 2.0 m of run plus a 1.2 m landing, which is 3.2 m deep and doesn't fit. So the default is the **depressed corner / blended transition**, the standard answer for kerb-tight narrow sidewalks:

- Between the two crosswalk landing points on a corner arc, the kerb is `Dropped25` and the walk is at gutter height plus 13 mm.
- Either side of that, the walk ramps down along its length at **≤ 7.5% design** (PROWAG maximum 8.33%, 1:12), which is 1.83 m for a 137 mm drop.
- The landing at the bottom is ≥ 1.2 × 1.2 m at ≤ 2%.
- **Detectable warning surface:** 0.61 m (24 in) deep along the kerb line across the full lowered width. Yellow truncated domes, done as a texture and normal map, not geometry.
- Perpendicular ramps (1.5 m wide, 2.0 m run at 7.5%, flares ≤ 10%) are used only where `walkBand` depth is ≥ 3.2 m, i.e. the roundabout ring and plazas.
- Residential corners without marked crosswalks still get ramps. That is the real-world norm, and the PROWAG 2023 rule requires them.

**Sidewalk UVs:** UV0 = (distance along the kerb loop, distance from the kerb) in metres. Scoring joints every 1.5 m then run square to the kerb, as real panels do.

### 5.8 Markings (MUTCD 11th edition, Part 3; geometry, not shader math)

| Marking | Where in Backroads | Spec |
|---|---|---|
| Centre line | `MainStreet` and in-town `Highway`: **double yellow** (two 0.10 m lines, 0.10 m gap; §3A.04 ¶2–3). Rural highway: **broken yellow 3.05 m dash, 9.15 m gap** (10/30 ft, §3A.04 ¶6), switching to no-passing where sight distance < §3B.03 Table 3B-1 (e.g. 900 ft / 274 m at 55 mph; 450 ft at 25 mph). **Residential streets: none**, as in most small towns. | Ends at the stop line on approaches and at `sMouth` on departures. **Not continued through the junction** (§3B.11 ¶5: an extension must be dotted. We omit it except at the skewed node 2, where a 0.6 m-dash / 1.2 m-gap dotted guide is optional) |
| Edge line | Rural `Highway` only, 0.15 m white, 0.15 m in from the pavement edge | Stops at fillet tangent points. "Solid lines shall not be used to extend edge lines into or through intersections" (§3B.11 ¶7) |
| Stop line | Every STOP-sign or signal approach | Solid white, **0.45 m** (18 in; §3B.19 ¶5 says 12–24 in), from the centre line to the kerb face − 0.3 m, at `sStop` |
| Crosswalk | Marked only: on MainStreet within 120 m of the centre, at the signal, across roundabout legs, at the school/park if added. Otherwise unmarked (ramps only). | Downtown: **high-visibility longitudinal ("continental")** bars 0.6 m wide, 0.6 m gaps (gap ≤ 2.5 × bar, Fig. 3C-1), bar length = crosswalk width **3.0 m**; bars centred on lane centres and lane lines, so gaps fall on wheel paths. Elsewhere: transverse, two 0.3 m lines with 2.4 m clear. Minimum width 6 ft / 1.83 m (§3C.03 ¶5) |
| Yield line | Roundabout entries | White isosceles triangles, base 0.6 m, height 0.9 m (1.5 × base), gap 0.15 m (§3B.19 ¶12), at the inscribed circle |
| Roundabout | Circulatory roadway | No centre line. Outer white edge line: solid next to splitter islands, wide dotted across entries, **none across exits** (§3D.03). Optional yellow inner edge line. No lane arrows on single-lane approaches (§3D.06) |
| Seams | Every mouth line and the apron edge | 0.08 m dark "tar seal" strip (cosmetic realism) |

**Generation:**

- Each marking is a quad strip split every 1 m (and at every underlying surface vertex), draped with `JunctionHeight`/`ArmY` plus 0.004 m.
- Dashes are phase-locked to `s = 0` of the edge, so they never move between bakes.
- One `Markings` submesh per chunk:
  - URP Lit, alpha-clipped wear mask, `Offset -1, -1` in the shader to avoid z-fighting;
  - vertex colour holds the paint: white (0.85, 0.85, 0.82), yellow (0.85, 0.62, 0.10);
  - UV0 = (u across 0..1, v along in metres) for wear.
- URP Decal Projectors (DBuffer) are only for one-off dirt: oil, skid marks, patch repairs, manhole rings. Long lines as decals cost fill-rate and blur at grazing angles.

### 5.9 UVs and vertex data for asphalt

- **UV0 = world planar** `(x/4, z/4)` on ribbons *and* junctions (4 m tile). This is seamless across every mouth, and asphalt is isotropic. Mirror x as 03 §2 says (`-worldPos.x`) if exact parity with the JS noise pattern matters.
- **UV1 = lane space** on ribbons: (lateral metres, s metres), used for directional details such as wheel-track darkening and longitudinal cracks. On junction surfaces UV1 is unused.
- **Vertex colour:**
  - R = traffic wear, baked from the lane graph: Σ over connectors and lanes of `exp(−(d_wheelpath/0.3)²)` × relative volume. Junctions get a realistic polished "turn fan" with no hand painting.
  - G = wetness (gutters and inlet sags).
  - B = per-patch variation id.
  - A = edge blend to the shoulder or terrain.
- **Materials per chunk:**
  - `Asphalt` (ribbons, junctions, aprons, roundabout circulatory)
  - `Concrete` (gutters, kerbs, sidewalks, islands, truck apron with a red-brick variant)
  - `Markings`
  - `Gravel` (dirt roads and shoulders, if not left to the terrain shader)

### 5.10 Terrain conforming ("road plates")

03 requires keeping the exported heightfield (road beds are already carved). So this step produces a **sparse delta**, `RoadPlates`, applied on load to `WorldGrid.H` and `BASE`, and versioned as canon v1.1. It only changes ground near *new* geometry: fillets, sidewalks, the roundabout, the R3 realignment and dirt aprons.

```
RoadStampJob : IJobParallelFor over grid vertices inside (union AABB of new geometry + 15 m):
  // primitives: ribbon quads, junction triangles, kerb/sidewalk strips, aprons, roundabout parts
  // in an 8 m uniform hash grid
  (d, ySurf, ySide, kind) = NearestPrimitive(v)        // d < 0 inside the footprint (signed polygon distance)
  if d < 0:
      H'  = ySurf - Sink(kind)       // asphalt 0.06 m, sidewalk/kerb 0.05 m below top: terrain never pokes through
      SURFACE' = kind == Asphalt ? ROAD : CONCRETE;   NoDeform' = 1   // ruts/dozer skip it (as SURF.ROAD today)
  else:
      flat     = kind is kerbed ? 0.3 : 1.2           // behind the walk / rural shoulder
      slopeLen = clamp(|H - ySide| * 3, 2, 12)        // ≤ 1V:3H side slopes
      w        = d <= flat ? 1 : 1 - Smoother((d - flat) / slopeLen)
      H'       = lerp(H, ySide, w)
  write delta = H' - H only where |delta| > 1 mm
```

- **Order-independent** (nearest-primitive), unlike `carveRoad`.
- Lot pads (`padY`) stay authoritative. Run the stamp, then re-apply the 59 lot pads, then assert |sidewalk back edge − lot pad edge| < 0.05 m.
- *Optional gameplay:* V-ditches 0.4 m deep at shoulder + 1.5 m on rural cut sections (good for getting stuck), behind an override flag.
- **If the terrain report instead chooses Unity Terrain:** read a region with `TerrainData.GetHeights(x, y, w, h)`, apply the same delta (normalised by `size.y`), `SetHeightsDelayLOD(x, y, heights)` per region, then a single `SyncHeightmap()`.
  - Forum reports put `SyncHeightmap` at a 0.5–15 ms CPU stall waiting on the GPU. It is fine at bake or load time, never per frame.
  - Paint an asphalt underlay into the alphamap so gaps never show green.

### 5.11 Roundabout generator

Dimensions for Millbrook, a compact single-lane roundabout:

- **ICD 32 m (105 ft).** NCHRP Report 672: single-lane 90–180 ft; mini 45–90 ft. Wisconsin DOT's "compact" class is 80–120 ft.
- **Circulatory width 5.5 m (18 ft).** TxDOT: 18–20 ft typical.
- **Lane centre radius 13.25 m** (today 11.5).
- **Truck apron 3.0 m** (7.5 → 10.5 m radius), mountable `Apron75` kerb, red brick.
- **Central island** r 7.5 m with a 150 mm kerb. The fountain or monument moves here from the square (`props.js buildTown`).
- **Splitter islands** raised, 15 m long, 2.5 m wide at the yield line, with a 1.8 m pedestrian cut-through.
- **Entries and exits:** entry width 4.6 m (TxDOT 16–20 ft), entry kerb radius 15 m, exit radius 20 m.
- **Crosswalk** one car length (6 m) behind the yield line.
- **Legs** must be ≥ 70° apart (bake error otherwise).

```
BuildRoundabout(node, spec):
  Ro = spec.ICD/2; Ri = Ro - spec.circW; Ra = Ri - spec.apronW; Rl = Ro - spec.circW/2
  require MinGap(legAngles) >= 70°
  for leg L:
     splitter = Teardrop(L.axis, widthAtYield 2.5, length 15, nose offset 0.6 from lane edges)
     entryLane = L.right half, from the splitter's right side to the circle; exitLane = L.left half
     corner(prev leg exit side, L entry side) = SolveCorner(J = circle(Ro) as a polyline, r = 15 entry / 20 exit)
  surfaces: annulus(Ri, Ro) asphalt; annulus(Ra, Ri) apron concrete +0.075; disc(Ra) island +0.15; entries per leg
  heights: plane through the legs (the square is flat to ±1 cm) + 2% outward on the annulus
  lanes:   ring lane at Rl, counter-clockwise seen from above (right-hand traffic);
           for each entry × exit: entry curve (tangent merge) + ring arc + exit curve, as trafficNet.roundaboutConns
           (search the dl offsets {0.5, 0.4, 0.6, 0.3, 0.75, 0.2} rad and keep the min-curvature one)
  controls: RoundaboutYield on every entry; yield line at r = Ro on the entry; YIELD sign right + on the splitter (left)
  markings: §5.8 roundabout row
```

### 5.12 Bends, dead ends, dirt aprons, class changes

- **L-bends (nodes 13, 14, 15, 18, 20).** Keep the canon centreline corner. The node becomes a 2-arm junction:
  - inner corner: `Fillet` r = 6 m;
  - outer corner: `Miter` (a square outer kerb, as on real town street corners);
  - lanes: two connectors (right turn and left turn), no control.

  Filleting the centreline itself would move the street up to 4.4 m into the inner corner lot.
- **Dead ends (yards).** The ribbon runs 0.5 m onto the site pad polygon (gravel or concrete, already in the heightfield). Port `fitLoop` unchanged; its loops are lanes with `kind = Loop`.
- **Dirt × Highway (6).** A T junction with 9 m unkerbed edge radii and an asphalt polygon, plus a 10 m asphalt apron into the dirt road with a ragged alpha edge. Dirt crown 4%. STOP sign on the dirt approach. The AI never takes dirt roads (no lanes generated), as today.
- **ClassChange (town edge).** Kerb taper 3 m to flush, gutter pan ends, sidewalk ends with a perpendicular ramp to the shoulder, edge lines begin, double yellow → broken.

### 5.13 Chunking, LOD, colliders

- **Chunks:** the same 64 m grid as the terrain (03 §4.3); 45 chunks contain road.
  - Each primitive goes to the chunk holding its centroid: ribbon pieces ≤ 32 m, a whole junction, a kerb-loop piece ≤ 32 m.
  - Triangles are not split at chunk borders. Overlap is harmless, and a deformation or override re-bake touches one chunk.
- **Per chunk:** one `MeshRenderer` per material (Asphalt, Concrete, Markings, Gravel). That is under 180 renderers in total: trivial for GPU Resident Drawer. No `MaterialPropertyBlock` (04 §2.2); use vertex colours.
- **LOD** (vertex count is not the issue; shimmer and draw distance are):
  - **LOD0** < 250 m: everything.
  - **LOD1** 250–600 m: kerb profile 6 → 3 vertices, no detectable-warning strips or seams, markings limited to centre lines and stop bars, generated with 2× wider lines to avoid sub-pixel shimmer.
  - **Culled** > 600 m: the terrain shader's ROAD/CONCRETE colours carry the road.
  - Use `LODGroup` with *Cross Fade* (dither). Check that the dither cross-fade works under GPU Resident Drawer on 6000.3 [unverified]; fall back to *None* if it shimmers.
- **Meshing:** `Mesh.AllocateWritableMeshData(chunkCount)` → a Burst `IJobParallelFor` per chunk → `Mesh.ApplyAndDisposeWritableMeshData`. Use UInt32 indices only if one chunk exceeds 65k vertices, which shouldn't happen.
- **Colliders:**
  - Per chunk, one `MeshCollider` for Asphalt+Concrete, kerb faces included, so a 150 mm kerb is a real bump for the raycast suspension.
  - Markings and decals get no collider.
  - Cook in the bake with `Physics.BakeMesh(meshId, false)` inside an `IJobParallelFor`.
  - Surface type for tyre grip: either split Asphalt and Concrete into two colliders with named `PhysicsMaterial`s, or keep the 03 approach where wheels query `WorldGrid` surface IDs (the plates write ROAD/CONCRETE there). **Keep both consistent:** the bake asserts that every collider triangle's surface matches the grid surface under it.

### 5.14 AI lanes and turn paths from the same geometry

```
Lanes: for each edge and direction:
   pts = OffsetPolyline(edge.c, +laneOffset to the right of travel)   // right = (dz, -dx) in Unity (x,z)
   approach end: trimmed at that arm's sStop (vehicles stop with the front bumper on the line)
   departure end: trimmed at the far arm's sMouth (or the crosswalk outer edge)
   resample 1.5 m; speedCap = min(xs.speed, sqrt(2.6 / k))            // A_LAT 2.6 as today

Connectors at a node, for each approach lane L and departure lane O (not the same edge; U-turns only at roundabouts/loops):
   p0 = L.end, d0 = L.endDir;  p3 = O.start, d3 = O.startDir;  turn = SignedAngle(d0, d3)
   if |turn| < 15°:  Through: straight, or an S-Bézier with handles chord/3
   elif right turn:  RightTurn: arc concentric with that corner's fillet, radius
                     rp = corner.r + (hw - laneOffset)        // follow the kerb at the lane-centre distance
                     straight p0 → arc start, arc, arc end → p3 (tangent-continuous)
   else:             LeftTurn: circular arc tangent to both lane lines (extended);
                     X = intersection of the two lane lines; tangent length T = min(|X - p0|, |X - p3|);
                     R = T / tan(|turn|/2), clamped to [8, 25] m;
                     Bézier with h = (4/3)·tan(|turn|/4)·R handles (trafficNet's formula), plus straight fills
   k[], vcap[] from curvature

Design-vehicle check (Hauler 7.5t: wheelbase 2.45+2.15 = 4.60 m, width 2.45, front overhang 0.9, max steer 0.6 rad):
   R_rear,min = 4.6 / tan(0.6) = 6.7 m;  outer front corner sweeps √((R+1.225)² + 5.5²)  → 9.7 m at full lock
   simulate the kinematic bicycle along each connector; envelope = union of body rectangles (Clipper2)
   if envelope ∩ (kerb loops ∪ islands) beyond 0.05 m:   try the truck variant (start the turn 1.0 m left,
        allow encroachment of the opposing departure lane), set c.encroaches = true
   if envelope ∩ WaitingBox(other arm, its sStop) :        push that arm's sStop back (up to +6 m)
   else if still failing:                                  c.noTruck = true (as fitLoop does)
   // r = 7.5, 3.1 m lanes: truck right turn reaches 11.4 m from the kerb centre = 0.8 m over the centre line. Realistic.

Conflicts: for every pair (a, b) of connectors at a node:
   same fromLane → Diverge (compatible); same toLane → Merge(sMergeA, sMergeB)
   else polyline intersection test (sweep over 1 m pieces) with clearance 2.6 m (width 2.45 + margin;
        trafficNet uses 3.2) → Cross(sA, sB)
   crosswalk polygons ∩ connector → Ped(sEnter, sExit)   // for future pedestrians; also "yield to peds" on right-on-red
Rules from node.control:
   MinorStop:       minor approaches stop at sStop, then yield to every major movement;
                    on the major road, a left turn yields to the opposing through/right
   AllWayStop:      everyone stops; FIFO by arrival time at sStop; ties → yield to the vehicle on the right
   Signal:          phaseMask per connector (2-phase: main / side); permissive left yields to opposing;
                    right-on-red after a full stop (US default) unless signed
   RoundaboutYield: entry yields to circulating at the merge conflict; no stop
   None (L-bends):  no conflicts
```

The importer keeps `traffic.json` from `CanonData/v1` as a **golden reference**. For junctions whose geometry did not change, the new lanes must stay within 0.3 m of the old lane polylines, which catches handedness and offset mistakes. Where geometry changed (roundabout, stop setbacks), the diff is reported, not failed.

---

## 6. Stop signs, yields and signals: rules typical of North American small towns

### 6.1 What real small towns do [measured, OSM 2026-10-18, 1.6 km radius around each centre]

| Town | Signals | `highway=stop` nodes | of which `stop=all` |
|---|---|---|---|
| Bellefontaine OH (pop. ~14k, US-33/US-68) | 14 | 0* | 0 |
| Mount Forest ON | 3 | 51 | 10 |
| Cle Elum WA | 2 | 16 | 3 |
| Viroqua WI | 2 | 0* | 0 |
| Hillsboro KS | 1 | 0* | 0 |
| Nevada City CA | 1 | 19 | 5 |
| Wallace ID | 1 | 0* | 0 |
| Decorah IA | 0 | 4 | 0 |
| Marfa TX | 0 | 9 | 0 |
| Woodstock VT | 0 | 24 | 1 |

\* Stop signs are badly under-mapped in OSM, so a zero means "not tagged". Signal counts are reliable.

- Junction mix from the same cache: **1,261 three-way vs 642 four-way (66% / 34%)**.
- Median smallest angle between arms: 89°; 9.1% of junctions have an angle < 60° and 17.8% one < 75°.
- **Zero** `junction=roundabout` ways in all 10 towns.

So Millbrook's roundabout is a deliberate game feature (modern US towns do add them), and one signal at most is typical.

### 6.2 Assignment rules (MUTCD 11th edition, December 2023, Revision 1 December 2025)

1. **Roundabout** → YIELD (R1-2) on every entry; yield line; no stop lines (§2B.10 ¶3F, §3D.04).
2. **Signal** → only where it is chosen deliberately (override). Our traffic of a dozen AI cars can never meet Warrant 1, even with the small-community column: the 70% column for an isolated community under 10,000 people needs **350 veh/h** on the major street and **105 veh/h** on the minor for 8 hours (§4C.02 Table 4C-1). It is a set-dressing and gameplay choice.
3. **Minor-road stop (two-way stop)** → wherever classes differ. The minor road is the one that meets a through or numbered highway, has the lower functional class, lower volume or lower speed (§2B.07 ¶1). In Backroads: every Street arm meeting a Highway or MainStreet; the stem of street T junctions; dirt roads at pavement; work-site exits onto a highway.
4. **All-way stop** → a street-by-street X in town with similar arms. §2B.17 ¶1B: "two residential neighborhood collector (through) streets of similar design and operating characteristics". Add the ALL-WAY plaque (R1-3P) under every STOP. The volume warrant (§2B.16) is 300 units/h major and 200 minor for 8 hours, not met in-game, so this too is a judgement call.
5. **Yield or nothing** at quiet local-by-local T or L junctions. §2B.10 ¶2 allows yield control below 1,800 units/day where the angle is 75–90° and both are local streets. L-bends get nothing.

### 6.3 Placement geometry (props generated from the node model)

**STOP R1-1 / YIELD R1-2 signs**

| Item | Rule | Source |
|---|---|---|
| Side | Near side, right-hand side of its approach | §2B.18 ¶1 |
| Distance | As close as practicable; ≤ 50 ft (15.2 m) from the edge of the intersected pavement | §2B.18 ¶2, ¶5 |
| Longitudinal position | Our rule: at `sStop`, or up to 1 m upstream if a ramp or inlet is there | — |
| Lateral offset | Urban: kerb face + 0.6 m (2 ft); rural: edge of travelled way + 3.6 m | Typical MUTCD Ch. 2A practice [verify exact 11th-edition section] |
| Mounting height | 2.1 m (7 ft) to the bottom where there is parking or pedestrian activity; 1.5 m (5 ft) rural | Ch. 2A [verify] |
| Size | STOP 30 × 30 in single-lane, 36 × 36 multi-lane; YIELD 36 in single-lane, 48 multi-lane | Table 2B-1 |
| Post | Never share a post between STOP and YIELD (§2B.18 ¶3). Street-name blades (D3-1) may go on top of the STOP post | |
| Orientation | Face the approach, turned about 5° away from the road to avoid headlight glare | Common practice |
| Advance warning | Stop Ahead (W3-1) 50–75 m upstream if the sign is not visible from 75 m. The bake ray-casts from 1.1 m eye height along the lane | Our rule |

**Signals (if node 2 is chosen)**

| Item | Rule | Source |
|---|---|---|
| Faces per approach | ≥ 2 primary faces for the through movement | §4D.05 ¶1A |
| Longitudinal position | Faces 40–180 ft (12.2–54.9 m) beyond the stop line; prefer overhead on the far side | §4D.08, §4D.05 ¶6 |
| Mounting height | Overhead housing bottom ≥ 15 ft (4.6 m) and top ≤ 25.6 ft (7.8 m); side-mounted ≥ 8 ft (2.4 m) above the sidewalk | §4D.09 |
| Hardware | Mast arm on the far-right corner, plus one near-side pole face per approach. Pedestrian signals and push buttons at each crosswalk end on the landing | Small-town practice |
| Timing (fixed time) | Yellow 3–6 s (§4F.17 ¶13): use 3.5 s at 25–35 mph. Red clearance 1.0 s. Main green 30 s, side 18 s, giving a cycle of about 60 s | |
| Pedestrian timing | WALK 7 s; clearance = crossing length / 1.07 m/s (3.5 ft/s, §4I.06 ¶7). For our 7.2 m road that is 7 s | |
| Night mode | Optional after 22:00: flashing yellow on the main road, flashing red on the side street (a common small-town practice) | |

**Other furniture placed from the same model**

- Street-name blades at every junction on 2 diagonal corners.
- Speed-limit signs: 25 mph (40 km/h, matches `TOWN_V` 11 m/s) on streets, 35 mph on MainStreet, 45–55 mph rural. Note that `OPEN_V` today is 16 m/s, about 36 mph.
- Roundabout signs: W2-6 "roundabout ahead" 60 m before each entry, and an R6-5P plaque.
- Lamp poles: the corner with the best coverage, plus every 30–45 m mid-block. This replaces the `placeRoadLamps` logic, at the exported transforms where they already exist.

These are good uses for Synty street-furniture props (§10).

---

## 7. Millbrook plan, node by node

| Node | Kind after cleanup | Control | Kerb r | Special |
|---|---|---|---|---|
| 0 + 1 | 4-leg roundabout: R0 (fuel), R3 (harbor), R1 (depot), R2 (farm) | Yield on all entries | entry 15 / exit 20 | §7.1 realignment; the delivery ring moves (§9 decision) |
| 2 | X, Highway R0 × streets R22/R23 | **Signal (optional, the only one)**, else minor stop | 7.5 m (buildings ≥ 3.3 m away at any radius; 72° corners → tangents ~15 m) | Busiest route (town ↔ fuel hub ↔ mill/quarry); furthest downtown X from the roundabout (79 m) |
| 3 | T, fuel yard | Minor stop on R4 (mill arm); major R0+R5 | 12 m unkerbed | Inside the site pad; +6.5% arm |
| 4 | X, R1 × R20/R21 | Minor stop | 7.5 m | |
| 5 | X, R1 × R17/R18 | Minor stop (**streets** stop; fixes current code) | 7.5 m | −9.1% approach: flattest-spot stop line |
| 7 | X, R2 × R13/R14 | Minor stop | **6 m** | Shopfronts 2.1–2.6 m from the kerb |
| 9 | X, R3 × R10/R11 | Minor stop | **6 m** | Building 1.2 m from the kerb at 7.5 m, hit at 12 m |
| 16 | X, 4 streets | **All-way stop** | 7.5 m | Only street-by-street X; 91 m from the centre |
| 17 | T, stem R12 | Stop on R12 | 7.5 m | −9.0% arm |
| 19 | T, stem R21 | Stop on R21 | 7.5 m | |
| 13, 14, 15, 18, 20 | L-bend | None | inner 6 m, outer miter | |
| 6 dirt aprons | T Dirt × Highway | Stop on dirt | 9 m edges | 10 m asphalt apron |
| 6, 8, 10, 11, 12 | Dead-end yards | — (exit stop optional) | — | Port `fitLoop` |

### 7.1 The town-centre roundabout needs the harbor road realigned [measured]

Leg bearings measured from the centre (−70, 30) at radius 15 / 20 / 30 / 40 m:

| Leg | 15 m | 20 m | 30 m | 40 m |
|---|---|---|---|---|
| R0 (fuel) | −19° | −22° | −27° | −30° |
| R3 (harbor) | −5° | +9° | +23° | +29° |
| R1 (depot) | | | | 127–131° |
| R2 (farm) | | | | −127° to −131° |

- The R0–R3 gap is only 31° at 20 m and 50° at 30 m. Splitter islands and entry/exit widths need about 70° at a 32 m ICD.
- **Proposal:** replace R3 from the circle to just before node 9 (−25.1, 58.9) with a new ~42 m alignment. It leaves the circle radially at **+55°** (point (−60.8, 43.1) at r = 16 m) and eases into R3's existing heading of 45° at node 9.
  - The new alignment is a gentle S: two arcs of about 22–27 m radius. That is fine at roundabout exit speeds of about 25 km/h.
- **Resulting gaps** (bearings at 20 m): 77° (R0–R3), 73° (R3–R1), 104° (R1–R2), 106° (R2–R0).
- **Clearance:** the nearest canon claims to the corridor are at 40.1 m @ +13° and 46.9 m @ +83° from the centre. The first is about 15.8 m from the new centreline [estimate].
- **Frontage:** the bake must also report any lot whose front line ends up more than 3 m from the new kerb. Lots that fronted the old R3 alignment can shift by up to about 6–8 m at 30–40 m from the centre.
- R3 is a generated through road (not in `canon.json`), so this is an **override, not a canon edit**. It still changes the look of the square, so it needs the user's OK.
- **Alternative:** keep a 3-leg roundabout and move the harbor junction out along R0 to ≥ 45 m from the centre. That needs a longer re-route of R3 and leaves a T junction 30 m from node 2.

---

## 8. Validation (the bake fails on errors)

1. **Polygons:** every junction surface is simple and CCW. Area within ±5% of a Clipper2 union of its connector corridors (a sanity cross-check with 03's method).
2. **Seams:** every mouth vertex is shared bit-exactly between ribbon and junction. A height mismatch anywhere on a mouth line or kerb loop seam is < 1 mm.
3. **Grades:** report every approach > 6% within 10 m of a stop line, every junction plane > 3%, and every sidewalk cross slope > 2.1% or ramp > 8.33%.
4. **Kerbs vs canon:** kerb loop to building footprint ≥ the walk width (1.5 m residential, 1.0 m minimum at downtown corners). Every mailbox, tree and lamp outside the walk band, or moved by an explicit override.
5. **Lanes:** every approach lane has ≥ 1 connector (except dead ends); every departure lane is reachable; there are no connectors with k > 1/6 m⁻¹ (car) or 1/9 m⁻¹ (truck) without `noTruck`/`encroaches`; conflicts are symmetric.
6. **Controls:** exactly one STOP per controlled approach, on its right side, 0.6–2 m from the kerb, ≤ 15.2 m from the intersected edge. Yield lines only with YIELD signs, stop lines only with STOP or signal (§3B.19 ¶3, ¶8–9).
7. **Terrain:** sample 0.25 m inside every footprint: terrain height ≤ surface − 0.03 m. Sample 1 m outside: terrain within 0.1 m of the edge height.
8. **Golden:** lane parity with `traffic.json` for unchanged junctions (§5.14). Screenshots from `reference/shots` (03 §3.6) at the same cameras.
9. **Determinism:** bake twice; the asset hashes must be equal.

Use the Unity Test Framework (EditMode) for 1–7 on the Millbrook data plus synthetic cases:

- a 30° Y junction,
- a 5-arm star,
- two junctions 25 m apart,
- a T on a curve with a 15 m radius,
- an arm shorter than its tangent distance.

---

## 9. Work plan, effort, risks, decisions

**Effort** [estimate]: about 6–7k lines of C#, comparable to 03's "M3 Living roads 4–6 weeks", in this order:

| Step | Content | Est. |
|---|---|---|
| R0 | Import `world.json`, graph cleanup, gizmo debug view (nodes, arms, tangents, mouths), plain ribbons | 3 d |
| R1 | Corners, mouths, junction polygons, ring mesh, heights and crown warp, seams | 5 d |
| R2 | Clipper2 kerb loops, kerb extrusion, sidewalks, depressed corners, 51 driveway cuts, road plates | 5 d |
| R3 | Markings, materials, UVs, vertex wear, chunks/LOD/colliders | 4 d |
| R4 | Roundabout (after the §7.1 decision), dirt aprons, class changes | 4 d |
| R5 | Lanes, connectors, swept path, conflicts, controls, signal controller, sign/lamp/inlet props; hook up the ported traffic AI | 7 d |
| R6 | Validation suite, editor overrides UI, polish | 3 d |

**Risks:**

- Skewed or very short arms make fillet tangents collide. Mitigation: the radius back-off loop plus the bake errors in §5.1 and §5.3.
- The GPU Resident Drawer LOD cross-fade is unverified.
- The PROWAG and MUTCD Chapter 2A mounting values above should be checked against the official 11th edition text (Part 2 PDF, Chapter 2A) before relying on them for anything beyond look and feel.

**Decisions only the user can make:**

1. Realign the harbor road (R3) for a 4-leg roundabout (§7.1, recommended), or use a 3-leg roundabout with a relocated T.
2. The square's delivery ring becomes the roundabout island. Where should the town delivery point go: a kerbside loading bay on MainStreet, or a small car park?
3. One signal at node 2, or none?
4. Keep canon street widths: 6.2 m streets, so sidewalks are 1.5 m kerb-tight with no parking lanes. Widening for parking would move every lot front.
5. Keep the canon −9% approaches at nodes 5 and 17 (recommended), or allow a re-grade of up to about 0.6 m.

---

## 10. Synty packs and roads (re the user's question)

- **Don't give the Synty login to an agent.** Claude can't sign in with your password or create accounts.
  - Sign in to syntystore.com yourself, or the Unity Asset Store / Fab if you bought there.
  - Download the `.unitypackage` files to a folder such as `D:\SyntyPacks\`, then tell the agent the path. The agent can import them (Assets → Import Package → Custom Package) and work with the models, as 03 §7 describes.
  - Keep them under `Assets/ThirdParty/Synty/` and git-ignore that folder. The EULA forbids redistributing the raw assets; shipping a built game is fine.
- **For roads specifically:** POLYGON City's "Modular Road Set" (plus 8 traffic-light props) and similar pieces are **grid tiles**. They can't follow Millbrook's frozen, 45°-rotated, 72–90° junctions, so the road *surfaces* should come from the generator above.
  - Use Synty for the furniture this generator places: stop and yield signs (re-skin to MUTCD shapes if needed), signal poles and heads, lamp posts, hydrants, benches, bins, inlet grates, bollards.
  - Use Synty textures as the style reference for asphalt and concrete.
  - Synty ships URP materials, which matches 04's URP choice.

---

## Sources

- Unity Splines: [manual for 6000.3, 2.9.1 released](https://docs.unity3d.com/6000.3/Documentation/Manual/com.unity.splines.html) · [2.9 changelog](https://docs.unity3d.com/Packages/com.unity.splines@2.9/changelog/CHANGELOG.html) · [2.8 changelog](https://docs.unity3d.com/Packages/com.unity.splines@2.8/changelog/CHANGELOG.html) · [2.9 manual (samples incl. road)](https://docs.unity3d.com/Packages/com.unity.splines@2.9/manual/index.html) · [extrude component 2.8](https://docs.unity3d.com/Packages/com.unity.splines@2.8/manual/extrude-component.html) · [SplinePath / knot links](https://docs.unity3d.com/Packages/com.unity.splines@2.8/api/UnityEngine.Splines.SplinePath-1.html) · [link/unlink knots](https://docs.unity3d.com/Packages/com.unity.splines@2.1/manual/link-unlink-knots.html)
- Unity terrain API: [TerrainData.SetHeightsDelayLOD (6.3)](https://docs.unity3d.com/ScriptReference/TerrainData.SetHeightsDelayLOD.html) · [SyncHeightmap performance thread](https://discussions.unity.com/t/syncheightmaps-and-applydelayedheightmapmodification-performance/738857) · [Why is SetHeights slow](https://support.unity.com/hc/en-us/articles/205486596-Why-is-the-TerrainData-SetHeight-so-slow)
- EasyRoads3D: [Asset Store](https://assetstore.unity.com/packages/tools/terrain/easyroads3d-pro-v3-469) · [publisher manual index](https://www.unityterraintools.com/manual/scriptingAPI.php) · [ERRoadNetwork API (v3 manual mirror)](https://nlp.fi.muni.cz/aiproject/ui/hutta_peter2019/pa026-project/Assets/EasyRoads3D/html/ERRoadNetwork.html) · [ERRoad API (mirror)](https://nlp.fi.muni.cz/aiproject/ui/hutta_peter2019/pa026-project/Assets/EasyRoads3D/html/ERRoad.html) · [scripting overview (mirror)](https://nlp.fi.muni.cz/aiproject/ui/hutta_peter2019/pa026-project/Assets/EasyRoads3D/html/scriptingAPI.html) · [release notes (mirror)](https://nlp.fi.muni.cz/aiproject/ui/hutta_peter2019/pa026-project/Assets/EasyRoads3D/html/release_notes.html) · [intersections (mirror)](https://nlp.fi.muni.cz/aiproject/ui/hutta_peter2019/pa026-project/Assets/EasyRoads3D/html/intersections.html) · [forum thread](https://discussions.unity.com/t/easyroads3d-v3-the-upcoming-new-road-system-part-2/1487279/399)
- [CiDy 2 Asset Store](https://assetstore.unity.com/packages/tools/level-design/cidy-2-55298) · [CiDy forum](https://discussions.unity.com/t/cidy-2-7-procedural-city-designer/616319)
- Road Constructor: [Asset Store](https://assetstore.unity.com/packages/tools/level-design/road-constructor-287445) · [80.lv](https://80.lv/articles/get-this-procedural-road-builder-for-unity) · [DOTS Traffic City integration](https://dotstrafficcity.readthedocs.io/en/latest/roadConstructor.html)
- MicroVerse: [Roads](https://assetstore.unity.com/packages/tools/terrain/microverse-roads-208590) · [Splines](https://assetstore.unity.com/packages/tools/terrain/microverse-splines-232974) · [DOTS Traffic City MicroVerse notes](https://dotstrafficcity.readthedocs.io/en/lite/_sources/microverseRoads.rst.txt)
- R.A.M: [R.A.M 2019 listing](https://assetstore.unity.com/packages/tools/terrain/r-a-m-2019-river-auto-material-2019-145937/reviews) · [R.A.M 3 (80.lv)](https://80.lv/articles/naturemanufacture-s-river-auto-material-3-for-unity-is-out-now/)
- Polaris: [Pinwheel Terrain Tools Bundle](https://assetstore.unity.com/packages/slug/301779) · [Polaris 2021](https://assetstore.unity.com/packages/tools/terrain/polaris-2021-low-poly-mesh-terrain-editor-196648) · [Polaris 3 on Fab](https://fab.com/listings/0675c3e1-c723-4bc8-90f2-5ed188431774)
- Road Architect: [FritzsHero fork (MIT)](https://github.com/FritzsHero/RoadArchitect) · [commits](https://github.com/FritzsHero/RoadArchitect/commits/master) · [embeddedt fork note](https://github.com/embeddedt/RoadArchitect) · [ecosyste.ms entry](https://awesome.ecosyste.ms/projects/1135583)
- Road Creator: [GitHub (MIT, archived)](https://github.com/MCrafterzz/roadcreator) · [Road Creator Pro](https://mcrafterzz.itch.io/road-creator-pro)
- osm2streets: [repo (Apache-2.0)](https://github.com/a-b-street/osm2streets) · [general_case.rs](https://raw.githubusercontent.com/a-b-street/osm2streets/main/osm2streets/src/geometry/general_case.rs) · [SotM 2022 talk](https://2022.stateofthemap.org/sessions/9NHQQM)
- Libraries: [Clipper2 (BSL-1.0)](https://github.com/AngusJohnson/Clipper2) · [LibTessDotNet 1.1.15](https://nuget.org/packages/LibTessDotNet) · [Unity SpriteShape third-party notices (SGI-B)](https://docs.unity3d.com/Packages/com.unity.2d.spriteshape@14.0/license/Third%20Party%20Notices.html)
- Research: [Wilkie, Sewall, Lin: GIS data to functional road models (UNC)](https://gamma.cs.unc.edu/RoadNetwork) · [junction finding in spline roads (thesis)](https://www.diva-portal.org/smash/get/diva2:1675311/FULLTEXT02) · [Unity forum: road networks and intersections](https://discussions.unity.com/t/procedurally-generated-road-networks-and-intersections/532867)
- MUTCD 11th edition (FHWA): [edition page (Rev. 1, Dec 2025)](https://mutcd.fhwa.dot.gov/kno_11th_Edition.htm) · [table of contents](https://mutcd.fhwa.dot.gov/pdfs/11th_Edition/11thcovtoc.pdf) · [Chapter 2B](https://mutcd.fhwa.dot.gov/pdfs/11th_Edition/Chapter2b.pdf) · [Part 3](https://mutcd.fhwa.dot.gov/pdfs/11th_Edition/part3.pdf) · [Part 4](https://mutcd.fhwa.dot.gov/pdfs/11th_Edition/part4.pdf)
- Kerb radii and roundabouts: [Flagstaff 13-10-002](https://codepublishing.com/AZ/Flagstaff/html/Flagstaff13/Flagstaff1310002.html) · [Kittitas County](https://records.co.kittitas.wa.us/WebLink/0/doc/4998796/Page73.aspx) · [ecode360 township code](https://ecode360.com/8213545) · [Toronto curb radii guideline](https://www.toronto.ca/wp-content/uploads/2018/05/90c8-Curb-Radii-Guideline-Version-1.1.1-May-2018.pdf) · [Beaverton application](https://apps2.beavertonoregon.gov/DevelopmentProjects/ApplicantMaterials/DE%20Application.pdf) · [TxDOT roundabouts](https://onlinemanuals.txdot.gov/TxDOTOnlineManuals/TxDOTManuals/rdw/roundabouts.htm) · [Maine DOT roundabout design](https://www.maine.gov/dot/sites/maine.gov.dot/files/inline-files/DG%20-%20Roundabout%20Design.pdf) · [WisDOT FDM 11-26](https://wisconsindot.gov/rdwy/fdm/fd-11-26.pdf) · [SCDOT roundabout criteria](https://www.scdot.org/business/pdf/I85/RoadwayDesign/Roundabout_Criteria.pdf)
- PROWAG 2023 summaries: [Oregon APWA](https://oregon.apwa.org/wp-content/uploads/sites/47/2024/03/November-2023-Presentation-The-New-PROWAG.pdf) · [INDOT](https://www.in.gov/dot/div/contracts/standards/GIFE/AWPSECTION%2022.pdf) · [MaineDOT](https://www.maine.gov/mdot/lpa/docs/certification/2024/ADAPresentation_Oct2024.pdf) · [Fresno COG](https://agendas.fresnocog.org/./itemAttachments/804/1.4-Public-Rights-of-Way-2-slides-per-page_(1)_.pdf) · [CP Tech Center](https://www.cptechcenter.org/wp-content/uploads/sites/7/2025/04/CP-Tech-Center-2023-PROWAG-Final-Rule-ADA-Presentation-combined.pdf)
- Synty: [POLYGON City Pack](https://syntystore.com/products/polygon-city-pack) · [POLYGON Town Pack](https://syntystore.com/products/polygon-town-pack) · [licences overview](https://syntystore.com/pages/licences-overview) · [Unity forum on paid assets in public repos](https://discussions.unity.com/t/what-kind-of-license-can-be-used-to-protect-assets-purchased-from-the-asset-store-in-a-public-repo/860104)
- OpenStreetMap data (© OpenStreetMap contributors, ODbL): the existing `tools/osm/cache`, plus one Overpass count query (`node(around:1600,…)[highway=traffic_signals|stop]; out count;`) on 2026-10-18.
