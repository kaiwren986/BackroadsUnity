# roads-R4: roundabouts, bridges, unpaved aprons and class changes (R, worktree `r`, ≈ 4 AD) — draft

**Inputs:** roads-R1, research 05 §5.11-§5.12; OSM tags `junction=roundabout`, `highway=mini_roundabout`, `bridge=*`, `layer=*`, `surface=*`, `tunnel=*`. A river town will have bridges; whether it has a roundabout depends on the town (none in the 10 towns of 05 §6.1).

**Steps**
1. **Roundabouts from the map:** a closed `junction=roundabout` way (or a ring of ways) becomes one roundabout node. The mapped ring is the circulatory centreline; ICD = 2 × (mean ring radius + half the circulatory width); legs are the ways that meet the ring. Splitter islands, truck apron, central island, yield lines and crosswalks per 05 §5.11, with every dimension clamped to the mapped ring (a ring smaller than a 26 m ICD becomes a mini-roundabout: painted or domed island, fully mountable). `highway=mini_roundabout` nodes become mini-roundabouts (ICD 14-27 m). No roundabout is invented where the map has none.
2. **Bridges:** `bridge=yes` ways become decks: the DEM is ignored under the deck, the profile is a straight or vertical-curve line between abutment heights, kerbs or barrier rails per class (`PolygonCity/Prefabs/Environments/SM_Env_Bridge_Edge_01` and `SM_Env_Bridge_Wall_01` as rail dressing where they fit; otherwise our own rail mesh), abutment wing walls as terrain plates. Lane W excludes deck footprints from the ground mesh collision where the river runs under.
3. **Unpaved meets paved** (05 §5.12): T junction with 9 m edge radii and a 10-15 m paved apron into the unpaved road; STOP on the unpaved approach. Steep aprons use the flattest-spot stop line.
4. **Class changes** at the kerb-zone edge: kerb taper 3 m, gutter run-out, walk ends at a ramp, edge lines start, double yellow becomes broken.
5. **Service roads and alleys** (`service=alley|driveway|parking_aisle`) that meet a street: alleys become low-class junctions with a dropped-kerb mouth (no stop line); driveways and aisles are handed to sites-S1 as mapped driveway requests (they never become road junctions).
6. Complex junction pairs (< 30 m apart) that R0o flagged: resolve by a merged junction polygon or a `roads-overrides.json` entry, one by one, with the reason recorded.

**Done when:** every mapped roundabout and mini-roundabout bakes with legs ≥ 70° apart or a reported exception; every bridge has a deck, rails and abutments, and the DEM under it is untouched outside the abutment plates; every unpaved-paved meeting has an apron and a STOP; the alley count of the town matches the map.

**Gate:** feeds `Roads.Validation` and `m3.shots.junctions`.
