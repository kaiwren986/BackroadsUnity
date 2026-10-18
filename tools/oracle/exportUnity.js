// Canon export, run in the oracle snapshot page as index.html?export=unity (PLAN §1.8.4, T0.7b).
//
// Order [T4]:
//  1. wait for window.__game to finish booting; the loop never ran (determinism.js queues every
//     requestAnimationFrame callback), the clock is frozen, G.noRender is set;
//  2. in ONE synchronous task: copy every exported array with slice() and serialise every JSON file from
//     the live modules, so nothing can change between the WEAR check and the POSTs;
//  3. check sha256(WEAR) against 03 §3.2 and POST the files from the copies;
//  4. render reference/shots (12 cameras x 10:00 / 21:00) with the clock frozen, canvas.toBlob right
//     after each render;
//  5. LAST, because it mutates the live module arrays: reference/deform (10,000 seeded rut/brush calls);
//  6. POST /__export/done with the metadata for manifest.json (or hand it to the trace chain, 7);
//  7. then the golden traces (goldenTraces.js) run one page each and the last one POSTs done.
// Debug switches: &shots=0 skips 4, &deform=0 skips 5, &traces=0 skips 7.
const params = new URLSearchParams(location.search);

const WEAR_SHA256 = '85473f6f37e608431a5dadc307f14af862bade6a8729f44ec0f5c93f614080fb';
const SHOT_W = 1600, SHOT_H = 900;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function post(rel, body) {
  const r = await fetch('/__export/' + rel, { method: 'POST', body });
  if (!r.ok) throw new Error(`POST ${rel} -> ${r.status} ${await r.text()}`);
  return r.json();
}
function log(msg) {
  console.log('[export] ' + msg);
  fetch('/__log', { method: 'POST', body: String(msg) }).catch(() => {});
}
async function fail(e) {
  const msg = (e && e.stack) || String(e);
  console.error('[export] FAILED', msg);
  try { await fetch('/__export/fail', { method: 'POST', body: JSON.stringify({ error: msg }) }); } catch { /* server gone */ }
}
window.addEventListener('error', (e) => log('window error: ' + e.message));
window.addEventListener('unhandledrejection', (e) => log('unhandled rejection: ' + ((e.reason && e.reason.stack) || e.reason)));

async function sha256hex(buf) {
  const d = await crypto.subtle.digest('SHA-256', buf);
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
const bytesOf = (ta) => new Uint8Array(ta.buffer, ta.byteOffset, ta.byteLength);
const json = (o, pretty = false) => JSON.stringify(o, null, pretty ? 2 : undefined) + '\n';
const hex6 = (n) => '#' + (n >>> 0).toString(16).padStart(6, '0');

// --- constants that the oracle modules keep private: read from the served (snapshot) module source
function extractConst(src, name) {
  const m = new RegExp(`(^|\\n)(export )?const ${name} = `).exec(src);
  if (!m) throw new Error('const not found: ' + name);
  let i = m.index + m[0].length;
  const open = src[i];
  if (open !== '{' && open !== '[') throw new Error(`const ${name} is not an object/array literal`);
  const close = open === '{' ? '}' : ']';
  let depth = 0, q = null;
  for (let k = i; k < src.length; k++) {
    const c = src[k];
    if (q) { if (c === '\\') { k++; continue; } if (c === q) q = null; continue; }
    if (c === "'" || c === '"' || c === '`') { q = c; continue; }
    if (c === '/' && src[k + 1] === '/') { k = src.indexOf('\n', k); continue; }
    if (c === '{' || c === '[') depth++;
    else if (c === '}' || c === ']') { depth--; if (depth === 0) { if (c !== close) throw new Error('unbalanced ' + name); return Function(`"use strict"; return (${src.slice(i, k + 1)});`)(); } }
  }
  throw new Error('unterminated const ' + name);
}

// --- a top-level function declaration of an oracle module, as source text
function extractFunction(src, name) {
  const m = new RegExp(`(^|\\n)function ${name}\\(`).exec(src);
  if (!m) throw new Error('function not found: ' + name);
  const start = m.index + m[1].length;
  let depth = 0, q = null;
  for (let k = src.indexOf('{', start); k < src.length; k++) {
    const c = src[k];
    if (q) { if (c === '\\') { k++; continue; } if (c === q) q = null; continue; }
    if (c === "'" || c === '"' || c === '`') { q = c; continue; }
    if (c === '/' && src[k + 1] === '/') { k = src.indexOf('\n', k); continue; }
    if (c === '{') depth++;
    else if (c === '}' && --depth === 0) return src.slice(start, k + 1);
  }
  throw new Error('unterminated function ' + name);
}

// --- PNG (RGBA8, zlib via CompressionStream; rows exactly as given, no flip)
const CRC = (() => { const t = new Uint32Array(256); for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; t[n] = c >>> 0; } return t; })();
function crc32(bytes) { let c = 0xffffffff; for (let i = 0; i < bytes.length; i++) c = CRC[(c ^ bytes[i]) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; }
async function encodePNG(rgba, w, h) {
  const stride = w * 4 + 1, raw = new Uint8Array(stride * h);
  for (let y = 0; y < h; y++) raw.set(rgba.subarray(y * w * 4, (y + 1) * w * 4), y * stride + 1);
  const z = new Uint8Array(await new Response(new Blob([raw]).stream().pipeThrough(new CompressionStream('deflate'))).arrayBuffer());
  const chunk = (type, data) => {
    const out = new Uint8Array(12 + data.length), dv = new DataView(out.buffer);
    dv.setUint32(0, data.length);
    for (let i = 0; i < 4; i++) out[4 + i] = type.charCodeAt(i);
    out.set(data, 8);
    dv.setUint32(8 + data.length, crc32(out.subarray(4, 8 + data.length)));
    return out;
  };
  const ihdr = new Uint8Array(13), dv = new DataView(ihdr.buffer);
  dv.setUint32(0, w); dv.setUint32(4, h); ihdr[8] = 8; ihdr[9] = 6;
  return new Blob([new Uint8Array([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', z), chunk('IEND', new Uint8Array(0))]);
}

// --- FNV-1a 32 over bytes pushed one at a time
function fnv() {
  let h = 0x811c9dc5;
  return { u8(v) { h = Math.imul(h ^ (v & 0xff), 0x01000193); }, u32(v) { for (let s = 0; s < 32; s += 8) this.u8(v >>> s); }, hex() { return (h >>> 0).toString(16).padStart(8, '0'); } };
}

async function run() {
  const det = window.__det;
  if (!det) throw new Error('determinism.js is not active (expected as the first module)');
  const tStart = det.realNow();
  log(`exporter started, seed ${det.seed}`);

  // ---------------------------------------------------------------- 1. wait for boot
  for (;;) {
    const G = window.__game;
    if (G && G.hud && G.composer && typeof G.last === 'number' && det.pending() > 0) break;
    const lt = document.getElementById('loadText');
    if (lt && /Something went wrong/.test(lt.textContent)) throw new Error('game boot failed: ' + lt.textContent);
    if (det.realNow() - tStart > 5 * 60 * 1000) throw new Error('game did not boot within 5 min: ' + (lt && lt.textContent));
    await sleep(100);
  }
  const G = window.__game;
  G.noRender = true;
  const tBoot = det.realNow();
  log(`game booted in ${((tBoot - tStart) / 1000).toFixed(1)} s; loop frozen (${det.pending()} rAF callback queued, ${det.frames} frames run), clock ${det.clockMs} ms`);
  if (det.frames !== 0) throw new Error('the game loop ran before the export froze it');

  const THREE = await import('three');
  const { GLTFExporter } = await import('three/addons/exporters/GLTFExporter.js');
  const { GLTFLoader } = await import('three/addons/loaders/GLTFLoader.js');
  const W = await import('./world.js');
  const P = await import('./props.js');
  const { TOWN, CANON } = await import('./townGen.js');
  const { TOWN_PROFILE } = await import('./townProfile.js');
  const { CARGO, CARRIES } = await import('./jobs.js');
  const { DEFS, VEHICLE_ORDER, STEP } = await import('./vehicle.js');
  const { makeDetailTexture, makeNormalTexture } = await import('./textures.js');
  const { mulberry32, tileableNoise } = await import('./noise.js');
  const { props, colliders, treeList } = P;
  if (!treeList || !props.rocks || !props.allOps || !props.typed) throw new Error('export hooks missing: run tools/oracle/make-snapshot.mjs');

  // ---------------------------------------------------------------- async inputs that do not touch game state
  const text = async (u) => { const r = await fetch(u); if (!r.ok) throw new Error('GET ' + u + ' ' + r.status); return r.text(); };
  const bin = async (u) => { const r = await fetch(u); if (!r.ok) throw new Error('GET ' + u + ' ' + r.status); return r.arrayBuffer(); };
  const [propsSrc, jobsSrc, vmSrc, hudSrc, canonBytes, snapshotInfo] = await Promise.all([
    text('/js/props.js'), text('/js/jobs.js'), text('/js/vehicleModels.js'), text('/js/hud.js'), bin('/assets/towns/canon.json'), text('/_snapshot.json').then(JSON.parse),
  ]);
  const SPECIES = extractConst(propsSrc, 'SPECIES');
  const MODEL_CORE = extractConst(propsSrc, 'MODEL_CORE');
  const SUPPLY = extractConst(jobsSrc, 'SUPPLY'), DEMAND = extractConst(jobsSrc, 'DEMAND'), TANK = extractConst(jobsSrc, 'TANK');
  const META = extractConst(vmSrc, 'META');
  const RING_R = Number(/const RING_R = ([0-9.]+);/.exec(jobsSrc)[1]);
  // pane names in three.js traverse order, exactly as props.js collects them (lit flags index this list)
  const gltf = await new GLTFLoader().loadAsync('/assets/buildings/buildings.glb');
  gltf.scene.updateMatrixWorld(true);
  const PANES = {};
  for (const name of Object.keys(MODEL_CORE)) {
    const root = gltf.scene.getObjectByName(name);
    if (!root) throw new Error('buildings.glb: missing ' + name);
    const panes = [];
    root.traverse((o) => { if (o.isMesh && (o.material.name || '').replace(/\.\d+$/, '') === 'Glass') panes.push(o.name); });
    PANES[name] = panes;
  }
  // three.js as the page loaded it (the Node server never sees CDN requests) [T4]
  const importMap = JSON.parse(document.querySelector('script[type="importmap"]').textContent);
  const threeUrl = importMap.imports.three;
  const threeSha = await sha256hex(await bin(threeUrl));
  const gl = G.renderer.getContext();
  const dbg = gl.getExtension('WEBGL_debug_renderer_info');
  const renderer = {
    vendor: gl.getParameter(gl.VENDOR), renderer: gl.getParameter(gl.RENDERER), version: gl.getParameter(gl.VERSION),
    unmaskedVendor: dbg ? gl.getParameter(dbg.UNMASKED_VENDOR_WEBGL) : null, unmaskedRenderer: dbg ? gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL) : null,
  };
  renderer.software = /swiftshader|llvmpipe|software|basic render/i.test(`${renderer.unmaskedRenderer} ${renderer.renderer}`);
  log(`renderer: ${renderer.unmaskedRenderer || renderer.renderer}`);

  // ---------------------------------------------------------------- 2. ONE synchronous task: copies + serialisation
  if (det.frames !== 0) throw new Error('the game loop ran during the export');
  const randomAtFreeze = det.calls;
  const L = {
    'terrain/heights.f32': W.H.slice(), 'terrain/base.f32': W.BASE.slice(), 'terrain/surface.u8': W.SURFACE.slice(),
    'terrain/wear.u8': W.WEAR.slice(), 'terrain/roadd.u8': W.ROADD.slice(), 'terrain/mud.u8': W.MUD.slice(), 'terrain/tint.f32': W.TINT.slice(),
  };
  const files = {}; // rel -> string | Blob | Uint8Array
  const notes = [];

  // world.json
  const SURF_ORDER = Object.entries(W.SURF).sort((a, b) => a[1] - b[1]).map(([k]) => k);
  const roadKind = (r) => (r.street ? 'street-' + r.kind : r.paved ? 'paved' : 'dirt');
  const world = {
    grid: { size: W.SIZE, half: W.HALF, cell: W.CELL, N: W.N, VN: W.VN, waterY: W.WATER_Y, tintN: W.TINT_N, tintCell: W.SIZE / (W.TINT_N - 1),
      layout: 'k = j*VN + i; x = i*cell - half; z = j*cell - half; tint: ((j*tintN + i)*2 + {0: tb, 1: td}), x = i*tintCell - half' },
    surfaceTable: {
      names: SURF_ORDER, displayNames: [...W.SURF_NAMES],
      softness: Array.from(W.SOFTNESS), grip: Array.from(W.GRIP), digRate: Array.from(W.DIG_RATE), roughness: Array.from(W.ROUGHNESS),
      dust: W.DUST.map((c) => [...c]), mudMax: W.MUD_MAX,
      note: 'softness/grip/digRate/roughness are Float32Array in world.js: these are the exact float32 values',
    },
    pois: W.POIS.map((p) => JSON.parse(JSON.stringify(p))),
    lake: { ...W.LAKE }, bogs: W.BOGS.map((b) => ({ ...b })), fields: W.FIELDS.map((f) => ({ ...f })), hamlets: W.HAMLETS.map((h) => ({ ...h })),
    town: { ...TOWN },
    roads: W.ROADS.map((r, id) => ({ id, kind: roadKind(r), hw: r.hw, paved: r.paved, startJ: r.startJ, endJ: r.endJ, len: r.len, pts: r.pts.map((p) => [p.x, p.y, p.z, p.d]) })),
    lots: W.LOTS.map((l) => { const o = { ...l }; delete o.road; return o; }),
  };
  files['world.json'] = json(world);

  // placements.json
  const treeIndex = new Map(treeList.map((t, i) => [t, i]));
  const models = props.typed.filter((t) => t.type === 'model');
  const parkC = props.parking.length ? props.parking.reduce((a, p) => ({ x: a.x + p.x / props.parking.length, z: a.z + p.z / props.parking.length }), { x: 0, z: 0 }) : null;
  const buildings = models.map((t, i) => {
    let src = t.replay ? 'lot' : 'site';
    if (!t.replay && i === 0 && t.name === 'Warehouse_C' && parkC && Math.hypot(t.x - parkC.x, t.z - parkC.z) < 40) src = 'parking';
    if (!PANES[t.name]) throw new Error('no panes for ' + t.name);
    if (t.lit !== null && t.lit.length !== PANES[t.name].length) throw new Error(`lit/pane count mismatch for ${t.name}: ${t.lit.length} vs ${PANES[t.name].length}`);
    return { model: t.name, src, x: t.x, y: t.y, z: t.z, rot: t.rot, sx: t.sx, sz: t.sz, w: t.w, d: t.d,
      colors: Object.fromEntries(Object.entries(t.colors).map(([k, v]) => [k, hex6(v)])), lit: t.lit, panes: PANES[t.name], M: t.M };
  });
  const canonOps = (CANON && CANON.ops) || [];
  const boxOps = [...props.allOps.filter((o) => o[0] === 'box'), ...canonOps.filter((o) => o[0] === 'box')];
  const boatMeshes = props.boats.map((b) => ({ x: b.g.position.x, z: b.g.position.z, ang: b.g.rotation.y, phase: b.phase }));
  const placements = {
    note: 'three.js space (right-handed, +Y up, metres). buildings[].M is the column-major Matrix4 of the placed model; colliders[].tree indexes vegetation.json trees.',
    buildings,
    boxes: boxOps.map(([, x, z, hw, hd, rot, y0, y1]) => ({ x, z, hw, hd, rot, y0, y1 })),
    typed: props.typed.map((t) => ({ ...t })),
    ops: props.allOps,
    lamps: props.lamps.map((l) => ({ ...l })),
    parking: props.parking.map((p) => ({ ...p })),
    rings: JSON.parse(JSON.stringify(props.rings)),
    footprints: props.footprints.map((f) => ({ ...f })),
    boatSpots: (props.boatSpots || []).map((b) => ({ ...b })),
    boats: boatMeshes,
    colliders: colliders.map((c) => (c.type === 'circle'
      ? { type: 'circle', x: c.x, z: c.z, r: c.r, y0: c.y0, y1: c.y1, kind: c.kind, ...(c.tree ? { tree: treeIndex.get(c.tree) } : {}) }
      : { type: 'box', x: c.x, z: c.z, hw: c.hw, hd: c.hd, rot: c.rot, y0: c.y0, y1: c.y1, kind: c.kind })),
    lotStats: { ...props.lotStats },
    rngCalls: CANON ? CANON.rngCalls : null,
  };
  files['placements.json'] = json(placements);

  // vegetation.json
  const rockMesh = (() => {
    let hit = null;
    G.scene.traverse((o) => { if (o.isInstancedMesh && o.geometry.type === 'DodecahedronGeometry' && o.count === props.rocks.length) hit = o; });
    if (!hit) throw new Error('rock InstancedMesh not found');
    return hit;
  })();
  const rv = rockMesh.instanceColor.array;
  const vegetation = {
    species: SPECIES,
    treeFields: ['x', 'y', 'z', 'sp', 's', 'rot', 'tint', 'hue'],
    trees: treeList.map((t) => [t.x, t.y, t.z, t.sp, t.s, t.rot, t.tint, t.hue]),
    rockFields: ['x', 'y', 'z', 's', 'rot', 'v'],
    rocks: props.rocks.map((r, i) => [r.x, r.y, r.z, r.s, r.r, rv[i * 3]]),
    note: 'tree mesh sits at y - 0.2; rock mesh at y - 0.25*s. Rock v is read back from the instance colour (float32).',
  };
  notes.push('vegetation.json rocks[].v is the float32 instance colour red channel (the double is not kept by props.js)');
  files['vegetation.json'] = json(vegetation);

  // traffic.json
  const net = G.traffic.net;
  const ids = (a) => a.map((o) => o.id);
  const segOut = (s) => {
    const o = { id: s.id, kind: s.kind, n: s.n, len: s.len, kMax: s.kMax, x: Array.from(s.x), z: Array.from(s.z), s: Array.from(s.s), k: Array.from(s.k), cap: Array.from(s.cap), next: ids(s.next) };
    const known = new Set(['id', 'kind', 'n', 'len', 'kMax', 'x', 'z', 's', 'k', 'cap', 'next']);
    const put = (k, v) => { o[k] = v; known.add(k); };
    if (s.kind === 'lane') { put('edge', s.edge.id); put('from', s.from.id); put('to', s.to.id); put('dir', s.dir); put('minor', s.minor); }
    else { put('node', s.node.id); put('from', s.from.id); put('to', s.to.id); }
    for (const k of ['turn', 'major', 'rb', 'phIn', 'phOut', 'clear', 'R', 'noTruck']) if (k in s) put(k, s[k]);
    if ('compat' in s) put('compat', [...s.compat].sort((a, b) => a - b));
    if ('yard' in s) put('yard', s.yard ? s.yard.id : null);
    for (const k of Object.keys(s)) if (!known.has(k)) throw new Error(`traffic seg key not exported: ${k}`);
    return o;
  };
  const traffic = {
    note: 'lane from/to are node ids; conn and loop from/to are seg ids; next/compat are seg ids; yard is a node id',
    nodes: net.nodes.map((n) => ({ id: n.id, x: n.x, z: n.z, R: n.R, dead: n.dead, failed: !!n.failed,
      arms: n.arms.map((a) => ({ edge: a.edge.id, out: a.out, ang: a.ang })), major: n.major ? ids(n.major) : null,
      rb: n.rb ? { C: { ...n.rb.C }, R: n.rb.R, clear: n.rb.clear, ring: n.rb.ring, score: n.rb.score } : null,
      inLanes: ids(n.inLanes), outLanes: ids(n.outLanes), conns: ids(n.conns) })),
    edges: net.edges.map((e) => ({ id: e.id, a: e.a.id, b: e.b.id, road: W.ROADS.indexOf(e.road), pts: e.pts.map((p) => [p.x, p.z]) })),
    segs: net.segs.map(segOut),
    lanes: ids(net.lanes), conns: ids(net.conns), loops: ids(net.loops),
    skipped: net.skipped.map((s) => ({ ...s })),
    buildMs: G.traffic.buildMs,
  };
  files['traffic.json'] = json(traffic);

  // cameras (also the ray-probe origins)
  const cams = [];
  const addCam = (id, name, tx, tz, dist, height, yawDeg, tyOff = 1.5) => {
    const ty = W.heightAt(tx, tz) + tyOff;
    const a = (yawDeg * Math.PI) / 180;
    const px = tx + Math.sin(a) * dist, pz = tz + Math.cos(a) * dist;
    const py = Math.max(W.heightAt(px, pz), ty) + height;
    cams.push({ id, name, pos: [px, py, pz], target: [tx, ty, tz] });
  };
  for (const p of W.POIS) addCam('poi_' + p.id, p.name, p.x, p.z, p.r + 45, 26, 135);
  const bog = W.BOGS[0];
  addCam('bog', bog.name, bog.x, bog.z, bog.r + 30, 22, 200);
  const rbNode = net.nodes.find((n) => n.rb);
  if (!rbNode) throw new Error('no roundabout in the traffic network');
  addCam('roundabout', 'Millbrook roundabout', rbNode.rb.C.x, rbNode.rb.C.z, 42, 20, 300);
  const jn = net.nodes.find((n) => n.arms.length >= 3 && !n.rb && Math.hypot(n.x - rbNode.x, n.z - rbNode.z) > 60);
  addCam('junction', `Junction node ${jn.id}`, jn.x, jn.z, 38, 16, 60);
  addCam('lake', 'Lake', W.LAKE.x, W.LAKE.z, W.LAKE.r + 70, 34, 315, 0);
  cams.push({ id: 'overview', name: 'Overview', pos: [0, 620, 430], target: [0, 0, 0] });
  if (cams.length !== 12) throw new Error('expected 12 cameras, got ' + cams.length);
  const camObj = (c) => {
    const cam = new THREE.PerspectiveCamera(60, SHOT_W / SHOT_H, 0.3, 6000);
    cam.position.set(...c.pos); cam.up.set(0, 1, 0); cam.lookAt(new THREE.Vector3(...c.target));
    cam.updateProjectionMatrix(); cam.updateMatrixWorld(true);
    return cam;
  };
  const cameras = {
    width: SHOT_W, height: SHOT_H, fov: 60, near: 0.3, far: 6000, times: [10, 21],
    note: 'three.js PerspectiveCamera, vertical fov in degrees, camera.lookAt(target) with up +Y',
    cameras: cams.map((c) => {
      const q = camObj(c).quaternion;
      return { ...c, quat: [q.x, q.y, q.z, q.w], shots: { 10: `reference/shots/${c.id}_1000.png`, 21: `reference/shots/${c.id}_2100.png` } };
    }),
  };
  files['reference/cameras.json'] = json(cameras, true);

  // terrain/probes.json: 1,000 seeded probes + 12 x 4,096 camera rays
  const prng = mulberry32(1000);
  const nrm = { x: 0, y: 0, z: 0 };
  const probes = [];
  for (let n = 0; n < 1000; n++) {
    const x = (prng() * 2 - 1) * W.HALF, z = (prng() * 2 - 1) * W.HALF;
    W.normalAt(x, z, nrm);
    probes.push([x, z, W.heightAt(x, z), nrm.x, nrm.y, nrm.z, W.surfaceAt(x, z)]);
  }
  const RAY_N = 64, RAY_MAX = 1500, RAY_STEP = 0.5;
  const v3 = new THREE.Vector3();
  const rays = cams.map((c) => {
    const cam = camObj(c);
    const [ox, oy, oz] = c.pos;
    if (oy - W.heightAt(ox, oz) <= 0) throw new Error('camera underground: ' + c.id);
    const hits = [];
    for (let j = 0; j < RAY_N; j++) for (let i = 0; i < RAY_N; i++) {
      v3.set(((i + 0.5) / RAY_N) * 2 - 1, ((j + 0.5) / RAY_N) * 2 - 1, 0.5).unproject(cam).sub(cam.position).normalize();
      hits.push([v3.x, v3.y, v3.z, W.rayTerrain(ox, oy, oz, v3.x, v3.y, v3.z, RAY_MAX, RAY_STEP)]);
    }
    return { camera: c.id, origin: [ox, oy, oz], hits };
  });
  files['terrain/probes.json'] = json({
    probeFields: ['x', 'z', 'heightAt', 'nx', 'ny', 'nz', 'surfaceAt'], probeSeed: 'mulberry32(1000): x = (r*2-1)*half, z = (r*2-1)*half',
    probes,
    rayFields: ['dx', 'dy', 'dz', 't'], rayGrid: `${RAY_N}x${RAY_N} NDC cell centres, row-major from bottom-left, unprojected at z=0.5`,
    rayMaxT: RAY_MAX, rayStep: RAY_STEP, rayNote: 'W.rayTerrain(origin, dir, maxT, step): -1 = miss, 0 = origin underground',
    rays,
  });

  // gameplay
  files['gameplay/jobs.json'] = json({ CARGO, CARRIES, SUPPLY, DEMAND, TANK, RING_R }, true);
  const derived = {};
  for (const v of G.vehicles) {
    derived[v.id] = {
      mass: v.mass, ratios: v.ratios || null, revRatio: v.revRatio ?? null, mountModelY: v.mountModelY, box: { ...v.box }, nDrive: v.nDrive,
      iI: [v.iI.x, v.iI.y, v.iI.z], hull: v.hull.map((p) => [p.x, p.y, p.z]),
      wheels: v.wheels.map((w) => ({ x: w.x, z: w.z, mx: w.mx, my: w.my, mz: w.mz, steer: w.steer, drive: w.drive, hand: w.hand, side: w.side, front: w.front ?? null, r: w.r, w: w.w, rest: w.rest, k: w.k, c: w.c, len: w.len, comp: w.comp })),
    };
  }
  files['gameplay/vehicles.json'] = json({ STEP, VEHICLE_ORDER, DEFS, META, derived }, true);
  files['source/townProfile.json'] = json(TOWN_PROFILE, true);
  files['source/canon.json'] = new Uint8Array(canonBytes);

  // counts (verify-canon.mjs recomputes them from the files)
  const by = (arr, f) => arr.reduce((m, x) => { const k = f(x); m[k] = (m[k] || 0) + 1; return m; }, {});
  const counts = {
    trees: treeList.length, treesBySpecies: Object.fromEntries(SPECIES.map((s, i) => [s.name, treeList.filter((t) => t.sp === i).length])),
    colliders: colliders.length, collidersByKind: by(colliders, (c) => `${c.type}:${c.kind}`),
    lamps: props.lamps.length, lampPosts: props.typed.filter((t) => t.type === 'lamp').length, lots: W.LOTS.length,
    modelPlacements: models.length, lotModelPlacements: models.filter((t) => t.replay).length,
    footprints: props.footprints.length, rings: Object.keys(props.rings).length, parking: props.parking.length, boats: props.boats.length,
    roads: W.ROADS.length, roadPoints: W.ROADS.reduce((a, r) => a + r.pts.length, 0),
    nodes: net.nodes.length, lanes: net.lanes.length, conns: net.conns.length, loops: net.loops.length, carOnlyLoops: net.loops.filter((l) => l.noTruck).length,
    rocks: props.rocks.length, rngCalls: CANON ? CANON.rngCalls : null,
  };
  const randomAfterSerialise = det.calls;
  const tSer = det.realNow();
  log(`copies and serialisation in one task: ${((tSer - tBoot) / 1000).toFixed(1)} s since boot; trees ${counts.trees}, colliders ${counts.colliders}`);

  // ---------------------------------------------------------------- 3. check and POST
  const wearSha = await sha256hex(L['terrain/wear.u8']);
  if (wearSha !== WEAR_SHA256) throw new Error(`sha256(WEAR) = ${wearSha}, expected ${WEAR_SHA256}: the ground was already deformed`);
  if (new Uint8Array(new Uint16Array([1]).buffer)[0] !== 1) throw new Error('big-endian host');
  const layerHashes = {};
  for (const [rel, ta] of Object.entries(L)) {
    layerHashes[rel] = await sha256hex(bytesOf(ta));
    await post(rel, bytesOf(ta));
  }
  for (const [rel, body] of Object.entries(files)) await post(rel, body);

  // textures
  const det256 = makeDetailTexture(256).image;
  await post('textures/detail.png', await encodePNG(det256.data, det256.width, det256.height));
  const wn = makeNormalTexture(256, 3).image;
  await post('textures/water_n.png', await encodePNG(wn.data, wn.width, wn.height));
  { // vehicleModels.js getDirtTex(), which is not exported: same calls
    const size = 128, n = tileableNoise(size, mulberry32(31), [4, 8, 16, 32]);
    const data = new Uint8Array(size * size * 4);
    for (let i = 0; i < size * size; i++) { data[i * 4] = data[i * 4 + 1] = data[i * 4 + 2] = n[i] * 255; data[i * 4 + 3] = 255; }
    await post('textures/dirt.png', await encodePNG(data, size, size));
  }
  {
    // hud.js buildMapImage(), evaluated from its own source on a CPU-backed canvas (willReadFrequently), so the
    // anti-aliased road strokes do not depend on the GPU: G.hud.mapImg is drawn by the accelerated canvas and
    // differs between the GPU and the SwiftShader renderer
    const fnSrc = extractFunction(hudSrc, 'buildMapImage');
    const cpuDoc = { createElement: (tag) => { const c = document.createElement(tag); const g = c.getContext.bind(c); c.getContext = (t, o) => g(t, { ...(o || {}), willReadFrequently: true }); return c; } };
    const c = Function('W', 'props', 'document', `"use strict"; ${fnSrc}; return buildMapImage();`)(W, props, cpuDoc);
    const img = c.getContext('2d').getImageData(0, 0, c.width, c.height);
    await post('textures/map.png', await encodePNG(new Uint8Array(img.data.buffer), c.width, c.height));
  }
  notes.push('textures/*.png: RGBA8 rows exactly as in the DataTexture data (row 0 = v 0, not flipped); map.png px = x + 512, py = z + 512');
  notes.push('textures/map.png is hud.js buildMapImage() run from its source on a CPU-backed 2D canvas, so it is the same with every WebGL renderer');

  // static_props.glb: the 7 merged buckets
  const BUCKETS = ['bld', 'metal', 'winLit', 'winDark', 'bulb', 'neon', 'signRed'];
  const exportScene = new THREE.Scene();
  const staticProps = {};
  for (let i = 0; i < BUCKETS.length; i++) {
    const src = props.meshes[i];
    if (!src) throw new Error('missing bucket ' + BUCKETS[i]);
    const m = new THREE.Mesh(src.geometry, src.material);
    m.name = BUCKETS[i];
    exportScene.add(m);
    const g = src.geometry;
    staticProps[BUCKETS[i]] = { vertices: g.attributes.position.count, positionSha256: await sha256hex(bytesOf(g.attributes.position.array)), colorSha256: await sha256hex(bytesOf(g.attributes.color.array)) };
  }
  const glb = await new GLTFExporter().parseAsync(exportScene, { binary: true });
  await post('reference/static_props.glb', glb);
  log('data files posted');

  // ---------------------------------------------------------------- 4. shots, clock frozen
  let shotInfo = null;
  if (params.get('shots') !== '0') shotInfo = await shots(G, THREE, P, W, cameras);

  // ---------------------------------------------------------------- 5. deform references LAST
  let deformInfo = null;
  if (params.get('deform') !== '0') deformInfo = await deformRefs(W, L, mulberry32);

  // ---------------------------------------------------------------- 6. done
  const meta = {
    source: { repo: 'kaiwren986/Backroads', commit: snapshotInfo.commit, worldSeed: 20251015, plannerSeed: 20251015 ^ 0x7031, propsSeed: 4242,
      canonFrozen: CANON ? CANON.frozen ?? null : null, three: THREE.REVISION, exportedAt: new Date().toISOString(), userAgent: navigator.userAgent },
    coords: { space: 'three.js right-handed, +Y up, metres', toUnity: '(-x, y, z); yawDeg = -yaw*180/PI; M\' = S*M*S, S = diag(-1,1,1,1)' },
    grid: world.grid,
    prng: { kind: 'mulberry32 (determinism.js)', seed: det.seed, callsAtFreeze: randomAtFreeze, callsAfterSerialise: randomAfterSerialise, callsAtEnd: det.calls },
    clock: { frozenAtMs: det.clockMs, framesRun: det.frames },
    renderer,
    threeModule: { url: threeUrl, sha256: threeSha },
    layers: layerHashes,
    counts,
    staticProps,
    shots: shotInfo,
    deform: deformInfo,
    snapshot: { commit: snapshotInfo.commit, hooks: snapshotInfo.hooks.map((h) => h.label), hookedFiles: snapshotInfo.hookedFiles, addedFiles: snapshotInfo.addedFiles },
    notes,
    timings: { bootS: (tBoot - tStart) / 1000, totalS: (det.realNow() - tStart) / 1000 },
  };
  // golden traces run on fresh pages (each one drives and ruts the ground), chained after this one
  let traceIds = [];
  if (params.get('traces') !== '0') { const r = await fetch('/traces/index.json'); if (r.ok) traceIds = await r.json(); }
  if (traceIds.length) {
    await fetch('/__export/meta/export', { method: 'POST', body: JSON.stringify(meta) });
    log(`export page done; chaining ${traceIds.length} golden traces`);
    location.href = `/index.html?trace=${encodeURIComponent(traceIds[0])}&chain=1`;
    return;
  }
  const r = await fetch('/__export/done', { method: 'POST', body: JSON.stringify(meta) });
  log('done: ' + (await r.text()));
}

// ------------------------------------------------------------------ shots
async function shots(G, THREE, P, W, cameras) {
  const r = G.renderer, comp = G.composer;
  r.setPixelRatio(1); r.setSize(SHOT_W, SHOT_H);
  comp.setPixelRatio(1); comp.setSize(SHOT_W, SHOT_H);
  const cam = G.camera;
  cam.aspect = SHOT_W / SHOT_H; cam.fov = 60; cam.near = 0.3; cam.far = 6000; cam.updateProjectionMatrix();
  G.marker.visible = false;
  for (const h of G.headlights) h.intensity = 0;
  const out = { width: r.domElement.width, height: r.domElement.height, files: [] };
  if (out.width !== SHOT_W || out.height !== SHOT_H) throw new Error(`canvas is ${out.width}x${out.height}`);
  for (const hour of cameras.times) {
    for (const c of cameras.cameras) {
      const focus = new THREE.Vector3(...c.target);
      cam.position.set(...c.pos); cam.up.set(0, 1, 0); cam.lookAt(focus); cam.updateMatrixWorld(true);
      G.sky.time = hour; G.sky.envTimer = 0;
      G.sky.update(0, focus, cam.position, true);
      const nf = G.sky.nightFactor;
      P.setNight(nf);
      G.terrain.update(cam.position.x, cam.position.z, true);
      G.grass.lampT = 0;
      G.grass.fill(cam.position);
      P.updateTrees(0, cam.position, true);
      // main.js updateLights(): the six nearest street lamps get a real light at night
      const sorted = P.props.lamps.map((l) => ({ l, d: (l.x - cam.position.x) ** 2 + (l.z - cam.position.z) ** 2 })).filter((o) => o.d < 140 * 140).sort((a, b) => a.d - b.d);
      G.lampLights.forEach((pl, i) => { const o = sorted[i]; if (!o || nf < 0.05) { pl.intensity = 0; return; } pl.position.set(o.l.x, o.l.y - 0.3, o.l.z); pl.intensity = 32 * nf; });
      G.water.update(0);
      P.animateProps(0);
      G.bloom.strength = 0.16 + nf * 0.3;
      comp.render(0);
      const blob = await new Promise((res) => r.domElement.toBlob(res, 'image/png')); // toBlob called in the same task as the render
      const rel = c.shots[hour];
      await post(rel, blob);
      out.files.push(rel);
    }
    log(`shots at ${hour}:00 done`);
  }
  return out;
}

// ------------------------------------------------------------------ deform references (mutates the live arrays)
async function deformRefs(W, L, mulberry32) {
  const H = W.H, WEAR = W.WEAR, SURF = W.SURFACE, VN = W.VN, N = W.N, HALF = W.HALF, CELL = W.CELL;
  for (const [rel, arr] of [['terrain/heights.f32', H], ['terrain/wear.u8', WEAR], ['terrain/surface.u8', SURF]]) {
    const c = L[rel];
    for (let k = 0; k < c.length; k++) if (c[k] !== arr[k] && !(Number.isNaN(c[k]) && Number.isNaN(arr[k]))) throw new Error(`live ${rel} changed before the deform references (k=${k})`);
  }
  const rng = mulberry32(424242);
  const f32 = new Float32Array(1), u32 = new Uint32Array(f32.buffer);
  const calls = [];
  const region = (x, z, rad) => {
    const gi = (x + HALF) / CELL, gj = (z + HALF) / CELL, rc = rad / CELL + 2;
    return [Math.max(0, Math.floor(gi - rc)), Math.max(0, Math.floor(gj - rc)), Math.min(N, Math.ceil(gi + rc)), Math.min(N, Math.ceil(gj + rc))];
  };
  const track = (fn, args, rad) => {
    const [i0, j0, i1, j1] = region(args[0], args[1], rad);
    const w = i1 - i0 + 1, hgt = j1 - j0 + 1;
    const bh = new Float32Array(w * hgt), bw = new Uint8Array(w * hgt), bs = new Uint8Array(w * hgt);
    for (let j = j0; j <= j1; j++) for (let i = i0; i <= i1; i++) { const k = j * VN + i, l = (j - j0) * w + (i - i0); bh[l] = H[k]; bw[l] = WEAR[k]; bs[l] = SURF[k]; }
    const ret = fn === 'rut' ? W.rut(...args) : W.brush(...args);
    const h = fnv();
    let n = 0;
    for (let j = j0; j <= j1; j++) for (let i = i0; i <= i1; i++) {
      const k = j * VN + i, l = (j - j0) * w + (i - i0);
      if (bh[l] === H[k] && bw[l] === WEAR[k] && bs[l] === SURF[k]) continue;
      n++;
      f32[0] = H[k];
      h.u32(k); h.u32(u32[0]); h.u8(WEAR[k]); h.u8(SURF[k]);
    }
    calls.push([fn, ...args, ret, n, h.hex()]);
  };
  // 20 tyre tracks x 400 rut calls (two wheels 1.6 m apart, 0.25 m steps), over soft ground near the sites
  const softSpots = [W.BOGS[0], ...W.FIELDS, ...W.POIS.filter((p) => p.kind !== 'town')];
  for (let t = 0; t < 20; t++) {
    const s = softSpots[t % softSpots.length];
    let x = s.x + (rng() - 0.5) * 30, z = s.z + (rng() - 0.5) * 30;
    let hd = rng() * Math.PI * 2;
    const directional = t % 2 === 1;
    const radius = 0.42 + rng() * 0.13, berm = directional ? 0.6 : 0.45;
    for (let k = 0; k < 400; k++) {
      hd += (rng() - 0.5) * 0.06;
      const fx = Math.sin(hd), fz = Math.cos(hd);
      if (k % 2 === 0) { x += fx * 0.25; z += fz * 0.25; }
      const side = k % 2 ? 0.8 : -0.8;
      const wx = x + fz * side, wz = z - fx * side;
      const mud = W.mudAt(wx, wz);
      const depth = mud > 0 ? mud * 0.55 * (0.6 + rng() * 0.4) : 0.03 + rng() * 0.31;
      const rate = 0.002 + rng() * 0.058;
      const wearAdd = 9 + rng() * 21;
      const args = directional ? [wx, wz, radius, depth, rate, wearAdd, berm, fx, fz] : [wx, wz, radius, depth, rate, wearAdd, berm];
      track('rut', args, radius + 0.55);
    }
  }
  // 40 shovel clusters x 50 brush calls (dig or pile, 1.15 m, like main.js)
  for (let c = 0; c < 40; c++) {
    const s = softSpots[c % softSpots.length];
    const cx = s.x + (rng() - 0.5) * 40, cz = s.z + (rng() - 0.5) * 40;
    for (let k = 0; k < 50; k++) {
      const x = cx + (rng() - 0.5) * 3, z = cz + (rng() - 0.5) * 3;
      const amount = rng() < 0.6 ? -0.09 : 0.09;
      track('brush', [x, z, 1.15, amount], 1.15);
    }
  }
  if (calls.length !== 10000) throw new Error('deform call count ' + calls.length);
  // final sparse state vs the exported layers
  const dH = [], dW = [], dS = [];
  const c0 = L['terrain/heights.f32'], w0 = L['terrain/wear.u8'], s0 = L['terrain/surface.u8'];
  for (let k = 0; k < H.length; k++) {
    if (H[k] !== c0[k]) dH.push([k, H[k]]);
    if (WEAR[k] !== w0[k]) dW.push([k, WEAR[k]]);
    if (SURF[k] !== s0[k]) dS.push([k, SURF[k]]);
  }
  const finalHashes = { heights: await sha256hex(bytesOf(H)), wear: await sha256hex(bytesOf(WEAR)), surface: await sha256hex(bytesOf(SURF)) };
  await post('reference/deform/calls.json', json({
    note: 'Replayed in order on the v1 layers (heights, base, surface, wear). rut(x,z,radius,depth,rate,wearAdd,berm[,fx,fz]) or brush(x,z,radius,amount); ' +
      'each row ends with [return value, changed vertex count, fnv1a32 over (k u32le, H float32 bits u32le, WEAR u8, SURFACE u8) of the changed vertices in ascending k].',
    seed: 'mulberry32(424242)', calls,
  }));
  await post('reference/deform/final.json', json({ note: 'sparse [k, value] differences from the v1 layers after all 10,000 calls', heights: dH, wear: dW, surface: dS, sha256: finalHashes }));
  log(`deform references: ${calls.length} calls, ${dH.length} heights changed`);
  return { calls: calls.length, changedHeights: dH.length, changedWear: dW.length, changedSurface: dS.length, sha256: finalHashes };
}

// started last, so every const above is initialised
if (params.get('export') === 'unity') run().catch(fail);
