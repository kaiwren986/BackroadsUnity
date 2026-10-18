// Golden physics traces, run in the oracle snapshot page as index.html?trace=<id> (PLAN §1.8.5, T0.7e).
//
// Feeds a scripted input file (traces/<id>.json, copied from tools/oracle/traces/) through
// G.update(1/120) with G.noRender, the seeded Math.random of determinism.js and NO AI traffic
// (G.traffic.enabled = false before the first step, which ghosts every traffic car), and records
// pos/quat/vel/angVel/rpm/gear/mudSink every `sampleEvery` steps (0.1 s).
//
// Input files carry open-loop channels (throttle, brake, steer, handbrake; run-length [value, steps])
// plus key hits (KeyX low range, KeyG winch) at given steps. Unity's AutotestRunner replays them as
// they are. The harness drives the analog channels through the gamepad path of main.js
// (input.gp.active), which is how vehicleControls() reads analog throttle, brake and steer.
//
// Authoring: a file with a `teach` block and no `channels` runs in teach mode (&teach=1): a pure-pursuit
// steering controller along the named roads (and an optional brake point) produces the channels, and the
// baked input file is POSTed to trace-inputs/<id>.json to be copied into tools/oracle/traces/.
//
// Chain mode (&chain=1, used by the canon export): after posting reference/traces/<id>.json the page
// moves on to the next id in traces/index.json, and the last one POSTs /__export/done.
const params = new URLSearchParams(location.search);
const STEP_DT = 1 / 120;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function log(msg) {
  console.log('[trace] ' + msg);
  fetch('/__log', { method: 'POST', body: String(msg) }).catch(() => {});
}
async function fail(e) {
  const msg = (e && e.stack) || String(e);
  console.error('[trace] FAILED', msg);
  try { await fetch('/__export/fail', { method: 'POST', body: JSON.stringify({ error: msg }) }); } catch { /* ignore */ }
}
async function post(rel, body) {
  const r = await fetch('/__export/' + rel, { method: 'POST', body });
  if (!r.ok) throw new Error(`POST ${rel} -> ${r.status} ${await r.text()}`);
}
async function sha256hex(buf) {
  const d = await crypto.subtle.digest('SHA-256', buf);
  return [...new Uint8Array(d)].map((b) => b.toString(16).padStart(2, '0')).join('');
}
const wrap = (a) => { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; };

// run-length channel -> per-step lookup
function expand(rle, steps) {
  const out = new Float64Array(steps);
  let k = 0;
  for (const [v, n] of rle) for (let i = 0; i < n && k < steps; i++) out[k++] = v;
  if (k !== steps) throw new Error(`channel covers ${k} of ${steps} steps`);
  return out;
}
function rle(arr) {
  const out = [];
  for (const v of arr) { if (out.length && out[out.length - 1][0] === v) out[out.length - 1][1]++; else out.push([v, 1]); }
  return out;
}

// the right-hand lane of a sequence of roads, as a polyline
function lanePath(W, roads, lane) {
  const pts = [];
  for (const [id, dir] of roads) {
    const r = W.ROADS[id];
    const p = dir > 0 ? r.pts : r.pts.slice().reverse();
    for (let i = 0; i < p.length; i++) {
      const a = p[Math.max(0, i - 1)], b = p[Math.min(p.length - 1, i + 1)];
      const l = Math.hypot(b.x - a.x, b.z - a.z) || 1;
      const tx = (b.x - a.x) / l, tz = (b.z - a.z) / l;
      const q = { x: p[i].x - tz * lane, z: p[i].z + tx * lane }; // right of travel, as trafficNet.offsetRight
      if (!pts.length || Math.hypot(q.x - pts[pts.length - 1].x, q.z - pts[pts.length - 1].z) > 0.5) pts.push(q);
    }
  }
  return pts;
}

async function runTrace(id) {
  const det = window.__det;
  if (!det) throw new Error('determinism.js is not active');
  const t0 = det.realNow();
  for (;;) {
    const G = window.__game;
    if (G && G.hud && G.composer && typeof G.last === 'number' && det.pending() > 0) break;
    if (det.realNow() - t0 > 5 * 60 * 1000) throw new Error('game did not boot');
    await sleep(100);
  }
  if (det.frames !== 0) throw new Error('the game loop ran before the trace started');
  log(`trace ${id}: booted in ${((det.realNow() - t0) / 1000).toFixed(1)} s`);
  const G = window.__game;
  const W = G.W;
  const { input } = await import('./input.js');
  const raw = await (await fetch(`/traces/${id}.json`)).arrayBuffer();
  const spec = JSON.parse(new TextDecoder().decode(raw));
  const inputSha = await sha256hex(raw);
  const teach = params.get('teach') === '1';
  if (!spec.channels && !teach) throw new Error(`traces/${id}.json has no channels (bake it with &teach=1)`);
  if (teach && !spec.teach) throw new Error(`traces/${id}.json has no teach block`);

  G.noRender = true;
  G.traffic.enabled = false;                 // no AI traffic: the first update ghosts every car
  input.pollGamepad = () => {};              // the harness owns the gamepad channel
  const gp = input.gp;
  gp.active = true; gp.throttle = 0; gp.brake = 0; gp.steer = 0; gp.a = false; gp.x = false; gp.lb = false; gp.rb = false;

  const v = G.vehicles.find((o) => o.id === spec.vehicle);
  if (!v) throw new Error('no vehicle ' + spec.vehicle);
  if (G.player.vehicle && G.player.vehicle !== v) { G.player.vehicle.park(); G.player.vehicle.engineOn = false; }
  let { x, z, yaw } = spec.start;
  v.place(x, z, yaw);
  v.settle();
  G.player.mode = 'vehicle';
  G.player.vehicle = v;
  v.wake(); v.driven = true; v.engineOn = true; v.lightsOn = false; v.fuel = 1;
  G.acc = 0;

  const steps = spec.steps, every = spec.sampleEvery || 12;
  const ch = {};
  if (!teach) for (const k of ['throttle', 'brake', 'steer', 'handbrake']) ch[k] = expand(spec.channels[k], steps);
  else for (const k of ['throttle', 'brake', 'steer', 'handbrake']) ch[k] = new Float64Array(steps);
  const hits = new Map();
  for (const h of spec.hits || []) { if (!hits.has(h.step)) hits.set(h.step, []); hits.get(h.step).push(h.key); }

  // teach mode: pure pursuit along the lane, throttle until brakeAtKmh, then brake
  const T = spec.teach;
  const path = teach && T.roads ? lanePath(W, T.roads, T.lane ?? 1.8) : null;
  let braking = false, lifted = false, stopped = false, hint = 0;
  const teachInputs = (k) => {
    let thr = T.throttle ?? 1, brk = 0, st = 0;
    const kmh = v.speed * 3.6;
    if (T.liftAtKmh && kmh >= T.liftAtKmh) lifted = true;
    if (T.brakeAtKmh && kmh >= T.brakeAtKmh) braking = true;
    if (braking && T.releaseBelowKmh && kmh < T.releaseBelowKmh) stopped = true;
    if (lifted) thr = 0;
    if (braking) { thr = 0; brk = stopped ? 0 : 1; }
    if (T.throttleFromStep !== undefined && k < T.throttleFromStep) thr = 0;
    if (T.coastAfterStep !== undefined && k >= T.coastAfterStep) thr = 0;
    if (path) {
      let bi = hint, bd = Infinity;
      for (let i = Math.max(0, hint - 5); i < Math.min(path.length, hint + 60); i++) { const d = (path[i].x - v.pos.x) ** 2 + (path[i].z - v.pos.z) ** 2; if (d < bd) { bd = d; bi = i; } }
      hint = bi;
      const look = (T.lookahead ?? 8) + v.speed * (T.lookaheadPerMs ?? 0.4);
      let j = bi, acc = 0;
      while (j < path.length - 1 && acc < look) { acc += Math.hypot(path[j + 1].x - path[j].x, path[j + 1].z - path[j].z); j++; }
      v.updateBasis();
      const head = Math.atan2(v.ez.x, v.ez.z), want = Math.atan2(path[j].x - v.pos.x, path[j].z - v.pos.z);
      const err = wrap(want - head); // > 0: target to the left (+X is left when facing +Z)
      st = Math.max(-1, Math.min(1, (-(T.gain ?? 1.6) * err * (1 + v.speed * 0.055)) / v.def.steer));
      st = Math.round(st * 1e4) / 1e4;
    }
    ch.throttle[k] = thr; ch.brake[k] = brk; ch.steer[k] = st; ch.handbrake[k] = 0;
  };

  const fields = ['step', 't', 'px', 'py', 'pz', 'qx', 'qy', 'qz', 'qw', 'vx', 'vy', 'vz', 'wx', 'wy', 'wz', 'rpm', 'gear', 'mudSink', 'speed', 'fwdSpeed', 'steerAngle', 'lowRange', 'stuckT', 'contacts'];
  const samples = [];
  const sample = (k) => samples.push([k, k * STEP_DT, v.pos.x, v.pos.y, v.pos.z, v.quat.x, v.quat.y, v.quat.z, v.quat.w, v.vel.x, v.vel.y, v.vel.z,
    v.angVel.x, v.angVel.y, v.angVel.z, v.rpm, v.gear, v.mudSink, v.speed, v.fwdSpeed, v.steerAngle, v.lowRange ? 1 : 0, v.stuckT, v.wheels.filter((w) => w.contact).length]);
  const events = [];
  sample(0);
  const randomAtStart = det.calls;
  for (let k = 0; k < steps; k++) {
    if (teach) teachInputs(k);
    gp.throttle = ch.throttle[k]; gp.brake = ch.brake[k]; gp.steer = ch.steer[k]; gp.a = ch.handbrake[k] > 0.5;
    for (const key of hits.get(k) || []) { input.pressed.add(key); events.push({ step: k, key }); }
    det.advance(STEP_DT * 1000);
    G.update(STEP_DT);
    if (hits.has(k) && v.winch && !events.some((e) => e.winch)) {
      const a = v.winch.anchor;
      events.push({ step: k, winch: { anchor: [a.x, a.y, a.z], len: v.winch.len, target: v.winch.target ? v.winch.target.id : null } });
    }
    if ((k + 1) % every === 0) sample(k + 1);
  }
  if (det.frames !== 0) throw new Error('the animation loop ran during the trace');
  const out = {
    id, vehicle: spec.vehicle, about: spec.about, inputFile: `tools/oracle/traces/${id}.json`, inputSha256: inputSha,
    dt: STEP_DT, steps, sampleEvery: every, start: { x, z, yaw }, settle: 240, traffic: 'none (G.traffic.enabled = false)',
    randomSeed: det.seed, randomCallsAtStart: randomAtStart, randomCallsAtEnd: det.calls,
    fields, samples, events, final: { winch: v.winch ? { len: v.winch.len, tension: v.winch.tension } : null, fuel: v.fuel, dirt: v.dirt },
  };
  if (teach) {
    const baked = { ...spec, channels: Object.fromEntries(Object.entries(ch).map(([k, a]) => [k, rle(Array.from(a))])) };
    await post(`trace-inputs/${id}.json`, JSON.stringify(baked, null, 1) + '\n');
  }
  await post(`reference/traces/${id}.json`, JSON.stringify(out) + '\n');
  const last = samples[samples.length - 1];
  log(`trace ${id}: ${steps} steps, final speed ${(last[18] * 3.6).toFixed(1)} km/h at (${last[2].toFixed(1)}, ${last[4].toFixed(1)}), ${((det.realNow() - t0) / 1000).toFixed(1)} s`);
  await fetch('/__export/meta/trace-' + id, { method: 'POST', body: JSON.stringify({ inputSha256: inputSha, steps, samples: samples.length, randomCallsAtEnd: det.calls }) });

  // main.js saves money, clock, quality and fuel to localStorage every 10 s of game time and loads it
  // at boot: clear it so the next page (and every trace) starts from the same fresh state
  try { localStorage.clear(); } catch { /* storage blocked */ }
  if (params.get('chain') === '1') {
    const list = await (await fetch('/traces/index.json')).json();
    const i = list.indexOf(id);
    const extra = teach ? '&teach=1' : '';
    if (i >= 0 && i + 1 < list.length) location.href = `/index.html?trace=${encodeURIComponent(list[i + 1])}&chain=1${extra}`;
    else await fetch('/__export/done', { method: 'POST', body: '{}' });
  }
}

const traceId = params.get('trace');
if (traceId) runTrace(traceId).catch(fail);
