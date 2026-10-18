# 04: Unity technology stack and online play (research, 2026-10-18)

Scope: which Unity version, render pipeline, netcode/online services, distribution and packages to use for the Unity port of Backroads. Version numbers were checked on 2026-10-18 against the Unity release API, the Unity package registry (`https://packages.unity.com/<package>`), GitHub release pages, and the two editors installed on this machine (`C:\Program Files\Unity\Hub\Editor\6000.3.20f1` and `6000.5.6f1`). Where a fact comes from a third-party site or my own estimate, it is labelled.

---

## TL;DR: decisions

| Topic | Recommendation | Why, in one line |
|---|---|---|
| Unity version | **Unity 6.3 LTS.** Update the installed 6000.3.20f1 to **6000.3.26f1** (2026-10-08) in Unity Hub. **Don't use 6000.5.** Plan to move to **6.7 LTS** after it ships and has had 2-3 patches. | 6.3 LTS is supported until Dec 2027. 6.5 stopped getting patches on 2026-09-02, when 6.6 replaced it. 6.7 LTS brings URP GTAO, SSR and Surface Cache GI. |
| Render pipeline | **URP 17.3** with the Forward+ path, GPU Resident Drawer, STP upscaling, SSAO, Decals, and Adaptive Probe Volumes with sky occlusion. | Unity has put HDRP into maintenance with no new features (Feb 2026) and deprecated Built-in in 6.5. URP runs on Steam Deck and is what Synty ships for. |
| Netcode | **Netcode for GameObjects 2.13.x**, moving to 3.x on 6.7. Vehicles are **owner-authoritative** (each client simulates its own car); the host or server validates and relays; remote cars use snapshot interpolation plus dead reckoning. **Mirror 96.x** is the fallback. | No prediction or rollback is needed for owner-authoritative cars. NGO is first-party, works with Multiplayer Play Mode and the network simulator, and has both Steam and Unity Relay transports. |
| "Free to play online" | **Steam (US$100 one-off fee) with player-hosted sessions over Steam Datagram Relay (SDR)**, plus a free **dedicated-server build** players can run or rent, as Motor Town does. Use the Unity Relay free tier only for non-Steam and itch.io test builds. | Recurring cost to you is **$0/month** at any player count. Every paid relay or hosting option costs roughly $200-500/month at 10k monthly players. |
| Session size | 16 by default for player-hosted sessions, 24 with interest management, 32+ on dedicated servers. | Host upload is about 1.25 Mbit/s at 16 players and about 5 Mbit/s at 32 without interest management (calculation in section 3.3). |
| Distribution | Use **itch.io** (free) for early playtests and **Steam** for the real launch. | Steam supplies the free relay, identity, Cloud saves, Workshop, Playtest and Deck Verified. |
| Anti-cheat | **None at launch.** Rely on server-side sanity checks plus per-server kick and ban lists keyed on SteamID. | It's a casual co-op game, and host-side checks are what actually stop speed and teleport cheats. |
| Accounts and progression | SteamID for identity and **Steam Cloud** for saves. If progression must be global, add **Unity Authentication + Cloud Save** (free tier: 5 GiB stored, 1M reads and 1M writes a month). | $0 at 10k monthly players. |

---

## 1. Unity version: 6000.3 LTS vs 6000.5

### 1.1 Release landscape on 2026-10-18

Source: the Unity release API, `https://services.api.unity.com/unity/editor/release/v1/releases?version=<stream>&order=RELEASE_DATE_DESC`, queried today. Support terms come from [Unity release support](https://unity.com/releases/unity-6/support) and [endoflife.ai](https://endoflife.ai/score/unity/6.6) (third-party).

| Stream | Type | First release | Latest patch | Support status |
|---|---|---|---|---|
| 6000.0 (6.0) | LTS | 2024-04-29 | – | Standard support ends **Oct 2026**, i.e. now. |
| **6000.3 (6.3)** | **LTS** | 2025-12-03 (6000.3.0f1) | **6000.3.26f1, 2026-10-08** | Supported **until Dec 2027** (Enterprise/Industry +1 year). Patches come roughly every two weeks. The installed 6000.3.20f1 dates from 2026-07-16, six patches behind. |
| 6000.4 (6.4) | Supported (Update) | 2026-03-18 | – | End of life (superseded by 6.5). |
| 6000.5 (6.5) | Supported (Update) | 2026-06-15 | **6000.5.11f1, 2026-09-02 (final)** | **End of life**: no patches since 6.6 shipped on 2026-08-31. The installed 6000.5.6f1 is five patches behind even that final patch. |
| 6000.6 (6.6) | Supported (Update) | 2026-08-31 (6000.6.0f1) | 6000.6.6f1, 2026-10-15 (Hub "recommended") | Supported only until 6.7 ships. |
| 6000.7 (6.7) | Beta, will become **LTS** | 6000.7.0b1 on 2026-09-17 | 6000.7.0b5, 2026-10-15 | Unity calls it "the final release built upon Mono" ([CoreCLR update, June 2026](https://discussions.unity.com/t/coreclr-scripting-and-serialization-update-june-2026/1723299)). There is no official release date. 6.3 LTS shipped on 3 Dec 2025, so I would guess around Dec 2026 to Q1 2027 (my estimate). |
| 7000.0 (Unity 7) | Alpha | 7000.0.0a7 on 2026-10-06 | 7000.0.0a10, 2026-10-15 | Announced at Unite Seoul (2026-07-21). Preview due December, full release targeted for Q1 2027. Brings CoreCLR, .NET 10 and C# 14 ([Unity blog](https://unity.com/blog/unite-seoul-keynote-2026-recap)). |

### 1.2 Recommendation

**Build on 6000.3 LTS (6000.3.26f1 or newer).** Leave 6000.5.6f1 installed if you like, but don't create the project with it.

- **Stability and support.** 6.3 LTS gets fortnightly fixes until Dec 2027. 6.5 gets nothing further.
- **Package compatibility.** Every package this project needs has a 6.3-compatible version, and most are bundled with the editor (section 5). The 6.3-only `com.unity.platformtoolkit.steam` 1.2.0 needs 6000.3 or newer. NGO 3.x and Netcode for Entities 7.x need 6000.7, which is one reason to plan the 6.7 move.
- **Why not 6.6?** It has nice extras: Fast Enter Play Mode on by default, Burst and Cinemachine as core packages, the Compute Light Baker, and production WebGPU ([What's new in 6.6](https://docs.unity3d.com/6000.6/Documentation/Manual/WhatsNewUnity66.html)). But it falls out of support the day 6.7 ships, and it isn't installed here. Choose it only if you're happy to upgrade the moment 6.7 LTS lands.
- **Why plan for 6.7 LTS?** Unity says Surface Cache GI (fully dynamic GI for URP, no baking), Screen Space Reflections and GTAO are "previewable in the 6.7 alpha today and will reach broad availability with Unity 6.7 LTS" ([Unite Seoul recap](https://unity.com/blog/unite-seoul-keynote-2026-recap); [URP SSR preview thread](https://discussions.unity.com/t/preview-of-screen-space-reflections-for-urp/1721494)). Those are the biggest "pretty graphics" improvements available to a URP game with a day/night cycle.

**Forward-compatibility rules for the 6.3 project.** These make the later 6.7 and Unity 7 upgrades cheap:

1. Turn on *Project Settings > Editor > Enter Play Mode Settings: "Do not reload Domain or Scene"* from day one. This matches the 6.6+ default and the CoreCLR direction. Reset `static` state manually, for example with `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`.
2. Don't store `GetInstanceID()` results as `int`. 6.4+ replaces InstanceID with `EntityId` ([What's new in 6.4](https://docs.unity3d.com/6000.4/Documentation/Manual/WhatsNewUnity64.html)).
3. Enable **Read/Write** on every mesh used by a `MeshCollider` or as a Terrain detail mesh. From 6.6 the build *fails* otherwise.
4. Never embed or copy the Burst package into `Packages/`. It becomes a built-in module in 6.6 and an embedded copy "will break upon upgrading".
5. Avoid `com.unity.serialization`, which is deprecated in 6.6. Use `System.Text.Json`, JsonUtility, or MemoryPack.
6. Don't use dynamic batching, which is deprecated in 6.5 and obsolete in 6.6.

---

## 2. Render pipeline: URP, not HDRP

### 2.1 Why URP

- **HDRP is in maintenance.** Unity's [Render Pipelines strategy for 2026](https://discussions.unity.com/t/render-pipelines-strategy-for-2026/1710004) (23 Feb 2026) says "no new features are planned for HDRP". URP gets all the new work: physical light units, auto exposure, physical sky and a dynamic sky manager, real-time GI, SSR. Built-in is deprecated from 6.5, available at least through 6.7 LTS, and supported to the end of 2028 ([80.lv summary](https://80.lv/articles/unity-unveils-2026-render-pipelines-strategy/)).
- **Hardware.** Free-to-play players on mid-range PCs and Steam Deck (an RDNA2 APU at 1280x800) can't carry HDRP's high fixed cost. Motor Town's recommended GPU is a GTX 1070 and its minimum a GTX 750 4 GB ([Steam page](https://store.steampowered.com/app/1369670/)). URP is the realistic way to match that.
- **Art style and assets.** A stylised look (Blender GLBs, Synty packs) doesn't need HDRP's physically based camera and sky. Synty ships Standard and URP support ([Synty LTS policy](https://syntystore.com/pages/long-term-support-policy)).

### 2.2 URP 17.3 (Unity 6.3) feature plan

| Need | Use in 6.3 LTS | Notes and limits |
|---|---|---|
| Many lights, decals, large world | **Forward+** rendering path on the Universal Renderer | Forward+ is required by GPU Resident Drawer. |
| Draw-call cost of trees, buildings and props | **GPU Resident Drawer** (uses the BatchRendererGroup API) plus **GPU occlusion culling** | Requirements ([docs](https://docs.unity3d.com/6000.3/Documentation/Manual/urp/gpu-resident-drawer.html)): set Graphics > *BatchRendererGroup Variants* to **Keep All**, enable the SRP Batcher, set *GPU Resident Drawer* to **Instanced Drawing**, use Forward+, and draw objects with `MeshRenderer`. **Incompatible** ([docs](https://docs.unity3d.com/6000.3/Documentation/Manual/urp/make-object-compatible-gpu-rendering.html)): `MaterialPropertyBlock`, real-time GI, `OnRenderObject` callbacks, and Light Probe Proxy Volumes. Don't use MaterialPropertyBlock for per-car paint colours; use separate material instances, or the 6.3 *Renderer Shader User Value* (a per-renderer 32-bit value). It also increases build time. |
| Grass at scale (port of the three.js instanced grass) | **`Graphics.RenderMeshIndirect`** with a compute-shader cull, written as Burst/Jobs or HLSL compute. Terrain Detail is the fallback. | Whether the Resident Drawer handles Terrain *trees* and *details* isn't documented, so test it. Safest path: trees as GameObjects with `LODGroup`, which the Resident Drawer handles; grass as a custom indirect draw. |
| Ambient occlusion | URP **Screen Space Ambient Occlusion** renderer feature | GTAO arrives with 6.7 LTS. |
| Tyre marks, mud splats, road paint | URP **Decal** renderer feature (DBuffer or screen-space) with `DecalProjector` | |
| Upscaling | **STP** (Spatial-Temporal Post-processing). Set *Quality > Upscaling Filter* to STP; it needs TAA. | DLSS and FSR2 are **HDRP-only** in 6.3 ([upscaler table](https://docs.unity3d.com/Manual/resolution-scale-introduction.html)). On Steam Deck, use about 0.67-0.77 render scale with STP. |
| Indirect lighting | **Adaptive Probe Volumes (APV)** with **Sky Occlusion**, plus lighting scenario blending | Sky occlusion lets the sky colour change at runtime, which is exactly what day/night needs. Requirements ([docs](https://docs.unity3d.com/6000.3/Documentation/Manual/urp/probevolumes-skyocclusion.html)): Progressive **GPU** lightmapper, ambient source **Gradient or Color** (with a skybox, every change needs `DynamicGI.UpdateEnvironment`, which is "very large" in cost), and a rebake. This works because Millbrook is **canon and frozen**, so APV can be baked once for the town and the 1 km map. Re-bake whenever canon.json changes. |
| Day/night | Rotate the directional sun and moon light, drive `RenderSettings.ambientSkyColor/EquatorColor/GroundColor` from a gradient, and use time-sliced real-time reflection probes. Optionally blend baked APV scenarios (day, dusk, night). | Surface Cache GI in 6.7 removes most of the baking. |
| Sky | Procedural sky shader (Shader Graph, or Unity's *Skybox/Procedural*) | A URP physical sky is on Unity's 2026 list but not shipped in 6.3. |
| Volumetric fog and god rays | Third-party, MIT-licensed [CristianQiu/Unity-URP-Volumetric-Light](https://github.com/CristianQiu/Unity-URP-Volumetric-Light) (OpenUPM `com.cqf.urpvolumetricfog`). Render Graph support for Unity 6; Forward, Deferred, Forward+ and Deferred+ paths. | URP has no built-in volumetric fog. The package README says it supports up to 6000.5, so re-test it on 6.7. A cheaper option is height fog in the terrain and object shaders. |
| Terrain shading | URP **Terrain Lit**, or a **Shader Graph terrain material** (new in 6.3, with the *Terrain Texture* node). Enable *Draw Instanced* on the Terrain. | 1 km at a 1025 heightmap gives about 1 m cells; 2049 gives about 0.5 m. For mud ruts, `TerrainData.SetHeightsDelayLOD` on small rectangles, then `SyncHeightmap()` when driving stops. Collider updates cost CPU, so keep fine rut detail visual-only (decals or displacement) and the physical deformation coarser. |
| Post-processing | URP Volume stack: Tonemapping (Neutral or ACES), Bloom (6.3 adds the faster **Kawase/Dual** filters), Color Adjustments, White Balance, Vignette, Depth of Field (photo mode), Motion Blur | Check that motion blur doesn't smear the HUD; render the UI after post. |
| Shader authoring | Shader Graph 17.3 (core). Stylised lighting via the 6.3 *custom lighting in URP Unlit* Shader Graph. | Use the 6.3 *Shader Build Settings* to keep variant counts down; the Resident Drawer's "Keep All" inflates them. |

### 2.3 Quality tiers to build from the start

| Setting | Steam Deck / low | Mid (GTX 1070 / RX 580 class) | High (RTX 3070+) |
|---|---|---|---|
| Render scale and upscaler | 0.7 with STP, 30-45 fps cap | 1.0, or 0.85 with STP | 1.0 |
| Main light shadows | 2 cascades, 1024, 120 m | 4 cascades, 2048, 250 m | 4 cascades, 4096, 400 m |
| SSAO | off, or half-res low | half-res | full-res |
| Grass density and distance | 25%, 40 m | 60%, 80 m | 100%, 150 m |
| Volumetrics | off | off or low | on |
| Additional lights per object | 2 | 4 | 8 |

**Steam Deck.** Valve tests the native Linux build first and falls back to the Windows build under Proton ([steamdeckhq summary](https://steamdeckhq.com/news/unity-engine-announces-enhancements-for-linux/)). Unity promised better Linux and Deck support at GDC 2026, but published no dates ([Digital Production](https://digitalproduction.com/2026/03/17/official-steam-support-lands-on-unity-roadmap/)). **Recommendation: ship Windows only and rely on Proton on Deck.** That also avoids building engine-sim's C++ native plugin as a Linux `.so`. Add a native Linux build only if profiling on a Deck shows it's faster.

---

## 3. Online play

### 3.1 What Motor Town does

- Motor Town: Behind The Wheel is by **P3 Games**, in Early Access since 2021-12-17, at $19.99 ([Steam](https://store.steampowered.com/app/1369670/)). It is built in Unreal Engine; a forum comment says so, but the engine version isn't confirmed.
- It offers **both** player hosting from the game client and a separate Steam tool app, **"Motor Town: Behind The Wheel - Dedicated Server" (app 2223650)**. The server tool is Windows-only, and the downloading account must own the game; anonymous SteamCMD doesn't work. Default ports are 7777 TCP/UDP plus 27015 for Steam queries. One config file holds server name, password, **max players**, per-player vehicle limits, housing rental, and **NPC traffic density**. Hosting guides say the engine allows up to **100 players**, with about **20 recommended** on an average machine. These are vendor guides, not developer documentation: [Survival Servers](https://www.survivalservers.com/services/game_servers/motor_town/), [Game Host Bros](https://www.gamehostbros.com/guides/games/motor-town-behind-the-wheel/max-players).
- Players rent servers from commercial hosts (GPortal, Shockbyte, BisectHosting), so the developer pays nothing for those. After each game patch, the server must update before patched players can join.
- **Unknown:** how Motor Town replicates vehicle physics (server- or owner-authoritative). No public developer write-up exists. The only way to find out is to play on a high-ping server and watch how your own car behaves when it collides with others.

**Takeaway for Backroads:** copy this model. Offer an in-game "Host session" button, plus a headless dedicated server build that is free for players to run or rent.

### 3.2 Authority model for vehicles (recommended)

**Owner-authoritative vehicles, with the host or server validating and relaying.**

Server-authoritative vehicles with client prediction would mean re-simulating the raycast suspension, mud sinking, ruts and winch for several ticks every time a correction arrives. PhysX rollback in Unity is possible (`Physics.simulationMode = SimulationMode.Script` with `Physics.Simulate`, or `PhysicsScene.Simulate`), but it is CPU-heavy and fragile with deformable terrain. Netcode for Entities does prediction properly, but requires rewriting the vehicles in ECS on Unity Physics. Even Unity's own ECS vehicle package is only `com.unity.vehicles 0.1.1-exp.1`. That's not sensible for this project.

Concrete design:

| Item | Value |
|---|---|
| Local physics | `Time.fixedDeltaTime` of 1/60 s, or 1/100 if the raycast suspension needs it. The owner's car is a normal dynamic `Rigidbody`. |
| Network tick | NGO `NetworkConfig.TickRate` of 30 (the default). Send vehicle state at **20-30 Hz**, unreliable-sequenced. |
| State packet (~32 bytes per vehicle) | uint16 tick (2 B); position quantised to 3 x 21 bits over 1,024 m, about 0.5 mm steps (8 B); rotation, smallest-three 3 x 10 bits (4 B); linear velocity as 3 half-floats (6 B); angular velocity as 3 half-floats (6 B); inputs: steer int8, throttle and brake uint8, flags byte for gear, low range, handbrake, lights and winch (4 B); engine RPM uint16 (2 B). |
| Remote cars | Kinematic proxies. Interpolate snapshots with about **100 ms** of buffer (2-3 snapshots) using Hermite curves on position and velocity. For cars **near the local player**, render them **extrapolated to the present** with projective-velocity-blending dead reckoning, so collisions line up. Far cars stay interpolated. Extrapolate at most 250 ms, then freeze or blend. |
| Car-to-car collisions | The local car collides with the remote kinematic proxy and reacts. Send a small "contact impulse" RPC (point, normal, impulse) to the other car's owner, who applies it, so both sides feel the hit. |
| Validation (host or server) | Reject or kick on: speed above the vehicle's spec max plus a margin; position jumps greater than v·dt + ε; out-of-world positions; impossible acceleration. **Job payouts and economy are host-authoritative**: the server decides that a delivery completed, from trigger volumes it runs itself. |
| AI traffic | The host simulates the AI. Replicate compact lane state (`laneId`, distance along the spline, speed) at 5-10 Hz. Clients move AI cars along the same spline network. A car knocked off its lane switches to full-state replication until it recovers. |
| Mud ruts and terrain deformation | Wheels emit "rut stamps" (x, z, radius, depth), batched every 0.25 s and sent reliable to the host. The host merges them and rebroadcasts. Late joiners receive the modified heightfield tiles as deflate-compressed int16 height deltas. |
| Engine audio (engine-sim) | Network only RPM, throttle, load and gear, never audio. Each client runs full engine-sim for its own car and the 2-3 nearest remote cars, and a cheaper sample or granular model for the rest. Engine-sim is CPU-heavy. |
| Host leaves | NGO client-hosted sessions have **no host migration**. Unity staff point to Distributed Authority as the equivalent ([forum](https://discussions.unity.com/t/host-migration-in-multiplayer-game-which-uses-lobby-relay-and-netcode-for-gameobjects/923073)). For a casual driving game: save progress continuously (Steam Cloud), end the session cleanly, and offer "host a new session with the same players". Dedicated servers avoid the problem for persistent worlds. |

### 3.3 Session size and bandwidth (my calculation)

With 32 B per vehicle, 20 Hz and about 40 B of UDP, IP and transport headers per packet:

| Players per session | Per client down | Per client up | Host or server upload (sum) |
|---|---|---|---|
| 8 | 7 x 32 x 20 + 800 = 5.3 KB/s | 1.4 KB/s | 37 KB/s (0.3 Mbit/s) |
| 16 | 10.4 KB/s (~83 kbit/s) | 1.4 KB/s | 156 KB/s (**1.25 Mbit/s**) |
| 32, no interest management | 20.6 KB/s | 1.4 KB/s | 639 KB/s (**5.1 Mbit/s**) |
| 32, with interest management (full rate within 250 m, 4 Hz beyond; about 6 cars near) | about 7.8 KB/s | 1.4 KB/s | 242 KB/s (1.9 Mbit/s) |

That is about **42 MB per player-hour** at the client (down plus up), or about **45 MB** with AI traffic, ruts and chat added. Recommended limits: **player-hosted: 16 by default, 24 maximum** (with interest management); **dedicated: 32-48**. Motor Town's 20 is a good reference point.

### 3.4 Netcode libraries compared (versions checked 2026-10-18)

| Library | Version and licence | Topologies | Prediction | Host migration | Steam/EOS transports | Verdict |
|---|---|---|---|---|---|---|
| **Netcode for GameObjects** | **2.13.3** (2026-09-21) for 6000.0+; 2.13.0 is bundled in the 6000.3.20f1 editor. **3.1.0** needs 6000.7. Unity Companion Licence, free. | Client-host, dedicated server, Distributed Authority | None built in. Owner-authoritative `NetworkTransform` and `NetworkRigidbody` with buffered interpolation (Legacy lerp, Lerp, Smooth dampening), quaternion compression to 4 B, half-float positions ([docs](https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.13/manual/components/helper/networktransform.html)) | Distributed Authority only | Unity Transport 2.7.x with Unity Relay; community [Facepunch transport 2.0.0](https://github.com/Unity-Technologies/multiplayer-community-contributions) (updated 2026-09-08) and a SteamNetworkingSockets transport | **Recommended.** First-party, documented, works with Multiplayer Play Mode 2.0.2 and Multiplayer Tools 2.2.x. For vehicles, write a custom compact state message (custom messages or unreliable RPCs) rather than stock NetworkTransform. |
| Netcode for Entities | 1.14.x on 6.3 (1.14.0 bundled); a core package (6.5.0) from 6.5; 7.1.0 for 6000.7. Free. | Server-authoritative, client-hosted | Full prediction and rollback (Unity Physics) | Yes, with Unity Cloud ([docs](https://docs.unity3d.com/Packages/com.unity.netcode@1.5/manual/host-migration.html)) | Community | **Not recommended.** It needs an ECS rewrite of the vehicles, mud and winch. |
| Mirror | **v96.11.3** (2026-09-17), MIT, about 6.4k stars | Host, dedicated | `PredictedRigidbody`, labelled "experimental" in 2024 ([changelog](https://mirror-networking.gitbook.io/docs/manual/general/changelog/2024-change-log)) | No | FizzySteamworks / FizzyFacepunch (community), Edgegap relay | **Fallback.** Mature, many tutorials and many Steam games. |
| FishNet | **4.7.4** (2026-10-16). Custom licence: free for commercial use, no CCU limits, no revenue share ([legal page](https://fish-networking.gitbook.io/docs/overview/readme/legal-restrictions)). Pro tier is paid. | Host, dedicated | Built-in client-side prediction (v2, `PredictionRigidbody`) | No | FishySteamworks, FishyFacepunch | Strong technically. Pick it only if you later decide to go server-authoritative. Single main maintainer. |
| PurrNet | **v1.25.0-beta** (2026-10-17), MIT; PurrDiction 1.4.0 (MIT) adds prediction | Host, dedicated | Via PurrDiction | Not documented | Built-in Steam transport | Promising but young (about 690 stars). |
| Photon Fusion 2 | Commercial (2.1.x) | Shared, Host, Server | Yes (tick-based, rigidbody prediction) | **Yes** (`HostMigrationConfig`, [sample](https://doc.photonengine.com/fusion/v2/technical-samples/fusion-host-migration)) | Photon Cloud relay included | Good tech, but costs scale with CCU and you're locked in (see 3.6). |
| Steamworks networking (transport, not a netcode) | [Facepunch.Steamworks 2.5.2](https://github.com/Facepunch/Facepunch.Steamworks) (MIT) or [Steamworks.NET 2025.164.1](https://github.com/rlabrecque/Steamworks.NET) (MIT) | P2P with `ISteamNetworkingSockets::CreateListenSocketP2P` / `ConnectP2P`, or `ISteamNetworkingMessages` | – | – | "All P2P connections are automatically relayed over the Valve backbone when appropriate" ([Steamworks networking](https://partner.steamgames.com/doc/features/multiplayer/networking)). This hides IPs and handles NAT. | **Use this as the player-hosted transport.** |
| Epic Online Services | Free | P2P (punch-through, then relay fallback), lobbies | – | – | Relays are free according to third parties ([Coherence docs](https://docs.coherence.io/hosting/client-hosting/implementing-client-hosting/epic-online-services-eos-relay), [Edgegap](https://edgegap.com/comparison/edgegap-vs-epic-online-services-eos)). Dedicated servers can't use EOS P2P. Players need an EOS login (Epic, or Connect via Steam, Discord or others). | Only if you want cross-store P2P without Steam. Confirm the terms on the EOS developer portal. |

### 3.5 Hosted services and their prices

| Service | Free tier | Paid rate | Source |
|---|---|---|---|
| **Unity Relay** (via Multiplayer Services "Sessions", `com.unity.services.multiplayer` 2.4.1, `SessionOptions.WithRelayNetwork()`) | First **50 average monthly CCU**. **3 GiB per CCU, capped at 150 GiB/month** (the wording is ambiguous: either 150 GiB flat or 3 GiB x your average CCU). | $0.16 per extra average CCU; $0.09/GiB US+EU, $0.16/GiB Asia+Australia | [UGS pricing](https://unity.com/products/gaming-services/pricing) |
| Unity Relay limits | Up to **150 players** per session. No region locking. All traffic goes through the host's chosen region. | – | [Relay limitations](https://docs.unity.com/ugs/en-us/manual/relay/manual/limitations) |
| Unity Lobby | 10 GiB/month per region group | $0.09 or $0.16 per GiB after that | UGS pricing |
| Unity Distributed Authority | 6,000 connectivity hours/month; 100 GiB egress | $0.001 per hour; $0.09 or $0.16 per GiB | UGS pricing |
| Unity Authentication | Free | – | UGS pricing |
| Unity Cloud Save | 5 GiB stored; 1M writes; 1M reads per month | $0.50/GiB; $0.025 per 10k writes; $0.009 per 10k reads | UGS pricing. Per-player caps: 5 MB of key-value data, 1 GB of files ([docs](https://docs.unity.com/ugs/cloud-save/manual/concepts/files)) |
| Unity Multiplay hosting | **Gone.** Deprecated on 2026-04-01; migrated customers moved to Rocket Science | – | [Unity status notice](https://status.unity.com/info_notices/362941) |
| Photon Fusion | 20 CCU (development only). **100 CCU free for one Fusion or Quantum app, commercial use allowed, about 0.3 TB/month included, hard cap** | 500 CCU $125/mo (1.5 TB); 1,000 CCU $250/mo (3 TB); 2,000 CCU $500/mo; extra traffic $0.05-0.10/GB. Billed on the sum of regional peaks. | [Photon blog (100 CCU)](https://blog.photonengine.com/?p=5466); paid tiers per [Crux, verified 2026-09-12](https://crux.supercraft.host/blog/photon-fusion-pricing-2026/) (third party; photonengine.com blocked automated fetches) |
| Edgegap | Free trial: 1 app, 1.5 vCPU, 1 deployment, 60-minute cap | $0.00115 per vCPU-minute (≈ $0.069 per vCPU-hour); egress $0.10/GB. Relays: 100 CCU $8/mo, 500 CCU $115/mo. | [edgegap.com/pricing](https://edgegap.com/pricing) |
| Hetzner Cloud VPS (self-managed dedicated server) | – | CX23 (2 vCPU / 4 GB) about €4-5.5/mo after the 2026 price rises, with **20 TB traffic** in EU locations (US: about 1 TB) | Third party: [whtop](https://www.whtop.com/amp/plans/hetzner.com/144086), [privatedevops](https://privatedevops.com/news/hetzner-june-2026-cloud-price-increase-what-to-do). Check hetzner.com. |
| Steam Datagram Relay | No published price. It comes with Steamworks P2P. | – | [SDR docs](https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay): dedicated-server use of SDR needs a talk with Valve ("contact us ... fine print"). |

### 3.6 Monthly cost at 0, 1k and 10k monthly players (my estimate)

**Assumptions** (all mine): each monthly player averages 6 hours a month, so 1k MAU is about 6,000 player-hours (about 8 average CCU, about 25 peak) and 10k MAU is about 60,000 player-hours (about 82 average, about 250 peak). Traffic is about 45 MB per player-hour, so about **300 GB a month at 1k** and **3 TB at 10k**. If a relay bills both ingress and egress, double the bandwidth lines. Photon's own rule of thumb (MAU ≈ 400 x CCU) gives about 10x less traffic, so treat these figures as a pessimistic case.

| Option | 0 players | 1k MAU | 10k MAU |
|---|---|---|---|
| **Steam + SDR, player-hosted** (recommended) | $100 once (Steam Direct) | **$0** | **$0** |
| + community dedicated servers (Motor Town model) | $0 | $0 (players pay hosts) | $0 |
| + your own "official" EU dedicated servers on Hetzner | – | 1 x CX23 ≈ €5 | about 5-6 x CX23 ≈ €25-35 (US servers have only about 1 TB traffic each) |
| Unity Relay + Lobby (Sessions) | $0 | CCU $0; bandwidth (279 − 150 GiB) x $0.09 ≈ **$12** (about $37 if billed both ways) | CCU (82 − 50) x $0.16 ≈ $5; bandwidth (2,766 − 150) x $0.09 ≈ $235. **≈ $240** (about $490 if billed both ways) |
| Unity Distributed Authority | $0 | Hours free; egress (279 − 100) x $0.09 ≈ **$16** | Hours 54k x $0.001 = $54; egress ≈ $240. **≈ $295** |
| Photon Fusion 2 | $0 | Free 100 CCU plan; 300 GB is about the 0.3 TB included. **≈ $0-5** | 500 CCU plan $125 + about 1.5 TB extra x $0.05-0.10. **≈ $200-275** |
| Edgegap on-demand servers | $0 | 1 always-on 1 vCPU server ≈ $50 + 222 GB x $0.10 ≈ $22. **≈ $72** | About 5 average servers x 730 h x $0.069 ≈ $252 + 2.2 TB x $0.10 ≈ $222. **≈ $475** |
| EOS P2P relay | $0 | $0 | $0 (verify the terms) |

### 3.7 Concrete recommendation

1. **Netcode:** NGO 2.13.x on 6000.3 (3.x after the 6.7 upgrade). Use the topology the host picks, client-host or dedicated server. Vehicles are owner-authoritative with a custom 32-byte state, sent unreliable at 20 Hz (section 3.2).
2. **Transports:** use `Unity.Netcode.Transports.UTP.UnityTransport` for dedicated servers (direct UDP) and for Unity Relay in non-Steam builds. Use the community **Facepunch transport 2.0.0** with **Facepunch.Steamworks 2.5.2** for Steam P2P. To switch, swap the `NetworkManager.NetworkConfig.NetworkTransport` component per build profile.
3. **Matchmaking:** use the Steam lobby (`SteamMatchmaking`) with friends invites and a public list. For non-Steam builds, use Unity Multiplayer Services Sessions (`MultiplayerService.Instance.CreateSessionAsync(...)` with `WithRelayNetwork()`), which costs $0 while under 50 average CCU.
4. **Dedicated server:** a `com.unity.dedicated-server` 2.0.2 build profile (Linux or Windows headless) with no rendering, audio or engine-sim. Ship it later as a free Steam "Dedicated Server" tool app; Valve can add a released server app to the anonymous SteamCMD package 17906 ([Steamworks docs](https://partner.steamgames.com/doc/sdk/uploading/distributing_gs)). Whether that app costs another $100 fee isn't stated anywhere I found, so ask in Steamworks support.
5. **Testing:** Multiplayer Play Mode 2.0.2 (up to 4 virtual players in one editor) plus Multiplayer Tools 2.2.12 (*Network Simulator* with a 100-200 ms latency and 2% loss preset, *Runtime Network Stats Monitor*).

---

## 4. Distribution, anti-cheat and accounts

| | itch.io | Steam |
|---|---|---|
| Upfront fee | $0 | **$100 per app**, non-refundable, recouped after $1,000 of adjusted gross revenue ([Steam Direct](https://partner.steamgames.com/steamdirect), [fee doc](https://partner.steamgames.com/doc/gettingstarted/appfee)). A free game with no purchases never recoups it. |
| Revenue share | Developer chooses 0-100%; default 10% ([itch open revenue sharing](https://itch.io/updates/open-revenue-sharing)) | 30% standard |
| Online infrastructure included | None (use Unity Relay or EOS) | **SDR relay, lobbies, friends invites, Steam Cloud, Workshop, Playtest, Deck verification** |
| Unity integration | `butler` CLI uploads | Facepunch.Steamworks or Steamworks.NET; Unity's **Platform Toolkit for Steam** (`com.unity.platformtoolkit.steam` 1.2.0, needs 6000.3+) covers accounts, achievements and Steam Cloud saves (100 MiB per save), but **not networking** ([docs](https://docs.unity3d.com/Packages/com.unity.platformtoolkit.steam@1.1/manual/platform-details.html)) |
| Who must act | Free account | **The user** must sign up as a Steamworks partner (tax and bank forms) and pay the fee. An agent can't and shouldn't do this. |

**Plan:** itch.io for the first public alpha, using the Unity Relay free tier for multiplayer. Then Steam, with a "Coming Soon" page ahead of release, Steam Playtest for open tests, and launch as free-to-play.

**Unity licence:** Unity Personal stays free up to **$200,000** in revenue and funding, and the **splash screen is optional with Unity 6** ([Unity pricing updates](https://unity.com/pricing-updates)). There is no runtime fee.

**Anti-cheat:** none. Easy Anti-Cheat is free through EOS ([Epic announcement](https://www.epicgames.com/site/news/epic-online-services-launches-free-in-game-voice-and-easy-anti-cheat)), but it doesn't stop a cheating *host* in a player-hosted game. Its kernel driver also adds Linux and Deck opt-in friction. Use instead:
- Host/server validation (section 3.2).
- Steam auth tickets (`SteamUser.GetAuthSessionTicket` / `BeginAuthSession`) so clients can't spoof SteamIDs.
- Per-server ban lists, plus an optional global ban list on a small backend later.

**Accounts and progression (free options):**
1. **SteamID with Steam Cloud** (Auto-Cloud or Remote Storage; you set per-user quotas in Steamworks). This costs $0 and is enough when progression is local or per-server, which is what player-hosted games usually do.
2. If progression must be **global and harder to cheat**: Unity Authentication 3.8.0 (Steam sign-in from a session ticket) plus Cloud Save 3.4.2. At 10k MAU, saving every 5 minutes is about 720k writes a month, which is inside the free 1M. Only the host or dedicated server should write economy values.
3. Self-hosted alternative: [Nakama v3.41.0](https://github.com/heroiclabs/nakama) (Apache-2.0) on the same €5 VPS.
4. **Avoid PlayFab.** Several 2026 reports say its free tier dropped from 100k to 1,000 lifetime players on 2026-03-11 ([AccelByte](https://accelbyte.io/blog/playfab-just-cut-its-free-tier-by-99.-heres-what-that-means-for-your-game), [Crux](https://crux.supercraft.host/blog/playfab-cut-free-tier-99-percent-foundation-mode-fix-2026/); both are competitors).

---

## 5. Packages and versions

"Bundled" means the tarball ships with the installed **6000.3.20f1** editor (`Editor\Data\Resources\PackageManager\Editor\*.tgz`, or built-in core packages). "Latest" is from the Unity registry on 2026-10-18; the registry also lists each version's minimum Unity version.

| Package | Bundled with 6000.3.20f1 | Latest compatible with 6.3 | Use |
|---|---|---|---|
| `com.unity.render-pipelines.universal` | 17.3.0 (core) | ships with editor | URP |
| `com.unity.shadergraph` / `com.unity.visualeffectgraph` | 17.3.0 (core) | ships with editor | shaders, dust and mud VFX |
| `com.unity.inputsystem` | 1.19.0 | **1.20.1** (min 6000.0) | wheels, pads, keyboard; `Gamepad.SetMotorSpeeds` for rumble |
| `com.unity.splines` | 2.9.0 | **2.9.1** | road and lane network, `SplineExtrude`, knot links for junctions |
| `com.unity.cinemachine` | 2.10.7 (the old CM2 line, don't use) | **3.1.7** (install by name) | chase, bonnet and orbit cameras (`CinemachineCamera`, `CinemachineFollow`) |
| `com.unity.addressables` | 2.9.1 | 2.11.2 (2.x line); 4.1.1 exists (new Scriptable Build Pipeline 4) | Optional. Adopt once content passes a few GB or for DLC and mods. Stay on 2.x for 6.3. |
| `com.unity.cloud.gltfast` | – | **6.20.0** (min 6000.0). Avoid 7.0.0-exp.1. | Imports Blender `.glb` in the editor. FBX import is better where you need `_LOD0/_LOD1` LOD groups and material remapping. |
| `com.unity.formats.fbx` | 5.1.6 | 5.1.6 | FBX Exporter for round-trips |
| `com.unity.probuilder` | 6.1.2 | **6.1.3** | blockouts, loading docks, ramps |
| `com.unity.terrain-tools` | 5.3.2 | **5.3.3** | terrain sculpting and stamping |
| `com.unity.burst` | 1.8.29 | **1.8.31** (min 6000.0) | grass culling, AI traffic, rut merging (built-in from 6.6; never embed) |
| `com.unity.collections` / `com.unity.mathematics` | 2.6.6 / 1.3.3 | 2.7.1 / 1.3.3 | NativeArray containers for Jobs |
| `com.unity.entities` | 1.4.6 | 1.5.0 | **Don't use** for gameplay |
| `com.unity.netcode.gameobjects` | 2.13.0 | **2.13.3** (3.1.0 needs 6000.7) | netcode |
| `com.unity.transport` | 2.7.3 | **2.7.4** | UDP, Relay, WebSocket |
| `com.unity.services.multiplayer` | 2.2.4 | **2.4.1** | Sessions (Lobby, Relay, Distributed Authority) for non-Steam builds |
| `com.unity.multiplayer.playmode` | 2.0.2 | 2.0.2 | virtual players in the editor |
| `com.unity.multiplayer.tools` | 2.2.9 | **2.2.12** | network simulator and stats |
| `com.unity.dedicated-server` | 2.0.2 | 2.0.2 | headless server build profile, Multiplayer Roles |
| `com.unity.services.authentication` / `.cloudsave` | 3.7.1 / 3.4.0 | 3.8.0 / 3.4.2 | optional global progression |
| `com.unity.platformtoolkit` + `.steam` | – | **1.2.0** (min 6000.3) | achievements, Steam Cloud saves |
| Facepunch.Steamworks (GitHub) | – | **2.5.2** | Steam API and SDR P2P |
| NGO Facepunch transport (git URL, `Transports/com.community.netcode.transport.facepunch`) | – | 2.0.0 | NGO over Steam |
| CristianQiu URP Volumetric Light (OpenUPM `com.cqf.urpvolumetricfog`) | – | latest | volumetric fog |
| `com.unity.memoryprofiler` | 1.1.12 | – | memory checks before release |
| Jobs (`Unity.Jobs`, built in) | – | – | yes |
| `com.unity.vehicles` | – | 0.1.1-exp.1 (ECS, experimental) | **Don't use.** Port the existing raycast suspension. The built-in `com.unity.modules.vehicles` (`WheelCollider`) is there as a reference. |

---

## 6. Synty packs in this stack

This section also answers the user's question: "If I give my Synty login can we get our packs we need off that and work with those models".

- **Don't share the Synty login with the agents.** They can't sign in with someone else's credentials. Instead, **download the packs yourself** from Synty Store (My Account > Downloads), or from Unity's *Package Manager > My Assets* if you bought them on the Asset Store. Put the `.unitypackage` / `.zip` files in a folder outside the repo (for example `D:\Backroads-assets\synty\`) and tell the agents the path. They can then import the packs into the Unity project, convert the materials to URP, and set up the prefabs.
- **URP:** Synty supports Standard and URP and has a URP conversion tutorial ([LTS policy](https://syntystore.com/pages/long-term-support-policy)). Older packs may show pink materials until converted with *Window > Rendering > Render Pipeline Converter*.
- **Licence points that affect this project.** I read the EULA pages; this is not legal advice.
  - **One-time purchase EULA** ([link](https://syntystore.com/pages/one-time-purchase-licence)): perpetual, **5 seats**, "You must not share the source files of any Assets outside your team". So Synty files may only go in a **private** repo with team-only access, never a public fork, and must be excluded from any open-sourced code. Shipping them inside built games is fine on any platform, free or paid.
  - **Subscription EULA** ([link](https://syntystore.com/pages/standard-subscription-licence)): use is tied to an *active* subscription. If the product is "substantially developed, modified or improved" after the subscription ends, an active subscription is required again (clause 12.3). For a long hobby project that is an ongoing cost. Clause 1.6 also forbids using the assets in "Content Creation Systems", which include mods and UGC editors you build. So **no player-facing building or modding tools that expose Synty assets** under that licence.
  - **Both EULAs prohibit "Generation of 3D models utilising Generative AI Programs"**, and the one-time EULA bans uploading source files to third-party 3D-generation services. Importing, placing, recolouring and building prefabs from Synty models with ordinary tools is presumably normal use. **Having an AI agent programmatically reshape or kitbash Synty meshes in Blender could arguably fall under that clause.** Ask Synty support before doing that, and keep the agents' Blender generators working on the project's own models.

---

## 7. Open questions and how to settle them

| Question | How to find out |
|---|---|
| Does Unity Relay's free 150 GiB apply flat, or as 3 GiB x average CCU, and does it count both directions? | Create a UGS project, run a 2-player Relay session for 10 minutes, and read *Dashboard > Multiplayer > Relay > Usage*. |
| Exact 6.7 LTS release date | Watch https://unity.com/releases and the Unity Hub "Installs" list. Upgrade at patch f2 or f3 or later. |
| Does the GPU Resident Drawer draw Terrain trees and details? | In a 6.3 URP test scene, enable the Resident Drawer, then check *Window > Analysis > Rendering Statistics* (6.4+) or the Frame Debugger for BatchRendererGroup draws. |
| Is SDR free for P2P at any scale? | No public price list exists, and it's widely used for free in P2P Steam games. Confirm in the Steamworks partner site once the app exists. Using SDR for *dedicated* servers needs Valve's approval. |
| Does a dedicated-server Steam tool app need another $100? | Ask Steamworks support, or check the base app's Steamworks admin pages once it exists (tool apps are created under the game). |
| How does Motor Town replicate cars? | Not public. Test on a far-away server with 150+ ms ping: watch whether your own car stutters or rubber-bands when you collide (server-authoritative) or only remote cars look wrong (owner-authoritative). |
| Current Photon Fusion prices | photonengine.com blocked automated fetches. Check https://www.photonengine.com/fusion/pricing in a browser. |
| Is the URP volumetric fog package compatible with 6.7? | Re-test after the upgrade. The README lists support up to 6000.5. |

---

## Sources

- Unity release API (queried 2026-10-18): `https://services.api.unity.com/unity/editor/release/v1/releases?version=6000.3&order=RELEASE_DATE_DESC` (also 6000.5, 6000.6, 6000.7, 7000)
- Unity support windows: https://unity.com/releases/unity-6/support ; https://unity.com/releases ; third-party EOL table: https://endoflife.ai/score/unity/6.6
- Unity product-update forum (6.6 / 6.7 beta posts): https://discussions.unity.com/c/unity-editor/product-update/205 ; Unity 7 alpha thread: https://discussions.unity.com/t/unity-7-is-here/1738415
- Unite Seoul 2026 recap (Unity 7; URP SSR / GTAO / Surface Cache GI in 6.7 LTS): https://unity.com/blog/unite-seoul-keynote-2026-recap
- CoreCLR, Scripting and Serialization update (June 2026): https://discussions.unity.com/t/coreclr-scripting-and-serialization-update-june-2026/1723299
- Unity 2026 roadmap summary: https://digitalproduction.com/2025/11/26/unitys-2026-roadmap-coreclr-verified-packages-fewer-surprises/
- What's new: 6.3 https://docs.unity3d.com/6000.3/Documentation/Manual/WhatsNewUnity63.html ; 6.4 https://docs.unity3d.com/6000.4/Documentation/Manual/WhatsNewUnity64.html ; 6.5 https://docs.unity3d.com/6000.5/Documentation/Manual/WhatsNewUnity65.html ; 6.6 https://docs.unity3d.com/6000.6/Documentation/Manual/WhatsNewUnity66.html ; URP 17: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/whats-new/urp-whats-new.html
- Render pipelines strategy 2026: https://discussions.unity.com/t/render-pipelines-strategy-for-2026/1710004 ; https://80.lv/articles/unity-unveils-2026-render-pipelines-strategy/
- GPU Resident Drawer: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/gpu-resident-drawer.html ; compatibility: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/make-object-compatible-gpu-rendering.html
- APV sky occlusion: https://docs.unity3d.com/6000.3/Documentation/Manual/urp/probevolumes-skyocclusion.html
- Upscaler comparison (DLSS/FSR2 HDRP-only): https://docs.unity3d.com/Manual/resolution-scale-introduction.html
- URP SSR preview: https://discussions.unity.com/t/preview-of-screen-space-reflections-for-urp/1721494
- URP volumetric light (MIT): https://github.com/CristianQiu/Unity-URP-Volumetric-Light
- Steam Deck / Linux: https://steamdeckhq.com/news/unity-engine-announces-enhancements-for-linux/ ; https://digitalproduction.com/2026/03/17/official-steam-support-lands-on-unity-roadmap/
- Unity Gaming Services pricing: https://unity.com/products/gaming-services/pricing ; Relay limits: https://docs.unity.com/ugs/en-us/manual/relay/manual/limitations ; Multiplay deprecation: https://status.unity.com/info_notices/362941 ; Cloud Save limits: https://docs.unity.com/ugs/cloud-save/manual/concepts/files
- NGO: registry https://packages.unity.com/com.unity.netcode.gameobjects ; NetworkTransform: https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.13/manual/components/helper/networktransform.html ; changelog: https://docs.unity3d.com/Packages/com.unity.netcode.gameobjects@2.13/changelog/CHANGELOG.html ; host-migration forum: https://discussions.unity.com/t/host-migration-in-multiplayer-game-which-uses-lobby-relay-and-netcode-for-gameobjects/923073 ; community transports: https://github.com/Unity-Technologies/multiplayer-community-contributions
- Netcode for Entities host migration: https://docs.unity3d.com/Packages/com.unity.netcode@1.5/manual/host-migration.html
- Mirror: https://github.com/MirrorNetworking/Mirror ; prediction changelog: https://mirror-networking.gitbook.io/docs/manual/general/changelog/2024-change-log
- FishNet: https://github.com/FirstGearGames/FishNet ; licence: https://fish-networking.gitbook.io/docs/overview/readme/legal-restrictions
- PurrNet: https://github.com/PurrNet/PurrNet ; PurrDiction: https://github.com/PurrNet/PurrDiction
- Photon: https://blog.photonengine.com/?p=5466 ; host migration: https://doc.photonengine.com/fusion/v2/technical-samples/fusion-host-migration ; pricing (third party): https://crux.supercraft.host/blog/photon-fusion-pricing-2026/
- Steamworks: networking https://partner.steamgames.com/doc/features/multiplayer/networking ; SDR https://partner.steamgames.com/doc/features/multiplayer/steamdatagramrelay ; Steam Direct https://partner.steamgames.com/steamdirect ; fee https://partner.steamgames.com/doc/gettingstarted/appfee ; dedicated server distribution https://partner.steamgames.com/doc/sdk/uploading/distributing_gs
- Facepunch.Steamworks: https://github.com/Facepunch/Facepunch.Steamworks ; Steamworks.NET: https://github.com/rlabrecque/Steamworks.NET
- Unity Platform Toolkit (Steam): https://docs.unity3d.com/Packages/com.unity.platformtoolkit.steam@1.1/manual/platform-details.html
- EOS: https://www.epicgames.com/site/news/epic-online-services-launches-free-in-game-voice-and-easy-anti-cheat ; relay behaviour: https://docs.coherence.io/hosting/client-hosting/implementing-client-hosting/epic-online-services-eos-relay ; https://edgegap.com/comparison/edgegap-vs-epic-online-services-eos
- Edgegap pricing: https://edgegap.com/pricing
- Hetzner (third party): https://www.whtop.com/amp/plans/hetzner.com/144086 ; https://privatedevops.com/news/hetzner-june-2026-cloud-price-increase-what-to-do
- Motor Town: https://store.steampowered.com/app/1369670/ ; https://www.survivalservers.com/services/game_servers/motor_town/ ; https://www.gamehostbros.com/guides/games/motor-town-behind-the-wheel/max-players
- itch.io revenue share: https://itch.io/updates/open-revenue-sharing
- Unity Personal / pricing: https://unity.com/pricing-updates
- PlayFab free-tier change (third party): https://accelbyte.io/blog/playfab-just-cut-its-free-tier-by-99.-heres-what-that-means-for-your-game ; https://crux.supercraft.host/blog/playfab-cut-free-tier-99-percent-foundation-mode-fix-2026/
- Nakama: https://github.com/heroiclabs/nakama
- Synty licences: https://syntystore.com/pages/licences-overview ; https://syntystore.com/pages/one-time-purchase-licence ; https://syntystore.com/pages/standard-subscription-licence ; https://syntystore.com/pages/long-term-support-policy
