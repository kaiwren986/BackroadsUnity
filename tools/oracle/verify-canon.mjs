#!/usr/bin/env node
// Verifies a canon export folder (CanonData/v1 or an export under artifacts/) against expected.json:
// the PLAN section 6.1 M0 oracle gate (layer hashes and counts), recomputed from the files themselves.
//
//   node tools/oracle/verify-canon.mjs <dir> [--report out.json]
//
// Checks: every manifest entry exists with its byte count and SHA-256 and nothing else is in the folder;
// no CR byte in any JSON file; the seven layer hashes; trees by species, colliders by kind, lamps, lots,
// model placements (y, sx, sz, matrix), lamp posts (faceRot, h), footprints, rings, parking, boats, roads,
// traffic, rngCalls; static_props.glb bucket vertex counts (parsed from the GLB) and that its position
// buffers hash as the page recorded; pane counts; shots, probes and rays present; renderer, seed and
// three.js hash recorded. Exit 0 = green.
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { pngInfo } from './pngread.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const DIR = path.resolve(args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--report') || 'CanonData/v1');
const REPORT = (() => { const i = args.indexOf('--report'); return i >= 0 ? args[i + 1] : null; })();
const EXP = JSON.parse(fs.readFileSync(path.join(HERE, 'expected.json'), 'utf8'));

const rows = [];
const check = (gate, name, want, got, ok = JSON.stringify(want) === JSON.stringify(got)) => rows.push({ gate, name, want, got, ok: !!ok });
const sha = (buf) => createHash('sha256').update(buf).digest('hex');
const rd = (rel) => fs.readFileSync(path.join(DIR, ...rel.split('/')));
const js = (rel) => JSON.parse(rd(rel).toString('utf8'));

const M = js('manifest.json');

// ---------------------------------------------------------------- files
function walk(dir, base = '') {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const rel = base ? `${base}/${e.name}` : e.name;
    if (e.isDirectory()) out.push(...walk(path.join(dir, e.name), rel)); else out.push(rel);
  }
  return out;
}
const onDisk = walk(DIR).filter((f) => f !== 'manifest.json').sort();
const listed = Object.keys(M.files).sort();
check('files', 'files on disk = manifest entries', listed, onDisk);
let badFiles = [], crFiles = [];
for (const rel of listed) {
  const p = path.join(DIR, ...rel.split('/'));
  if (!fs.existsSync(p)) { badFiles.push(rel + ' (missing)'); continue; }
  const buf = fs.readFileSync(p);
  if (buf.length !== M.files[rel].bytes || sha(buf) !== M.files[rel].sha256) badFiles.push(rel);
  if (rel.endsWith('.json') && buf.includes(13)) crFiles.push(rel);
}
if (rd('manifest.json').includes(13)) crFiles.push('manifest.json');
check('files', 'every file matches its manifest SHA-256', [], badFiles);
check('files', 'no CR byte in JSON files', [], crFiles);

// ---------------------------------------------------------------- layers
for (const [rel, e] of Object.entries(EXP.layers)) {
  const f = M.files[rel];
  check('M0', `${rel} SHA-256`, e.sha256, f && f.sha256);
  check('M0', `${rel} bytes`, e.bytes, f && f.bytes);
}

// ---------------------------------------------------------------- vegetation
const veg = js('vegetation.json');
check('M0', 'trees', EXP.trees, veg.trees.length);
const bySp = {};
for (const s of veg.species) bySp[s.name] = 0;
for (const t of veg.trees) bySp[veg.species[t[3]].name]++;
check('M0', 'trees by species', EXP.treesBySpecies, bySp);
check('data', 'tree rows have 8 finite numbers', true, veg.trees.every((t) => t.length === 8 && t.every(Number.isFinite)));

// ---------------------------------------------------------------- placements
const pl = js('placements.json');
check('M0', 'colliders', EXP.colliders, pl.colliders.length);
const byKind = {};
for (const c of pl.colliders) byKind[`${c.type}:${c.kind}`] = (byKind[`${c.type}:${c.kind}`] || 0) + 1;
const sortObj = (o) => Object.fromEntries(Object.entries(o).sort());
check('M0', 'colliders by kind', sortObj(EXP.collidersByKind), sortObj(byKind));
check('data', 'tree colliders point at trees', true, pl.colliders.filter((c) => c.kind === 'tree').every((c) => Number.isInteger(c.tree) && c.tree >= 0 && c.tree < veg.trees.length));
check('M0', 'lamps (77 posts + fountain)', EXP.lamps, pl.lamps.length);
const posts = pl.typed.filter((t) => t.type === 'lamp');
check('M0', 'lamp-post records with faceRot and h', EXP.lampPosts, posts.filter((t) => Number.isFinite(t.faceRot) && Number.isFinite(t.h) && Number.isFinite(t.y)).length);
const lotModels = pl.buildings.filter((b) => b.src === 'lot');
const full = (b) => Number.isFinite(b.y) && Number.isFinite(b.sx) && Number.isFinite(b.sz) && Array.isArray(b.M) && b.M.length === 16 && b.M.every(Number.isFinite);
check('M0', 'lot model placements with y, sx, sz and matrix', EXP.lotModelPlacements, lotModels.filter(full).length);
check('data', 'every model placement (lots and sites) complete', pl.buildings.length, pl.buildings.filter(full).length);
check('data', 'lit flags match pane counts', true, pl.buildings.every((b) => typeof b.lit === 'string' && b.lit.length === b.panes.length));
const canonModelOps = js('source/canon.json').ops.filter((o) => o[0] === 'model');
check('data', 'lot placements replay the canon model ops in order', true,
  lotModels.length === canonModelOps.length && lotModels.every((b, i) => b.model === canonModelOps[i][1] && b.x === canonModelOps[i][2] && b.z === canonModelOps[i][3] && b.rot === canonModelOps[i][4] && b.lit === canonModelOps[i][8]));
const panes = {};
for (const b of pl.buildings) panes[b.model] = b.panes.length;
check('data', 'pane counts of the placed models', Object.fromEntries(Object.keys(panes).sort().map((k) => [k, EXP.panes[k]])), sortObj(panes));
check('M0', 'footprints', EXP.footprints, pl.footprints.length);
check('M0', 'rings', EXP.rings, Object.keys(pl.rings).length);
check('M0', 'parking bays', EXP.parking, pl.parking.length);
check('M0', 'boats', EXP.boats, pl.boats.length);
check('M0', 'rngCalls', EXP.rngCalls, pl.rngCalls);
check('data', 'canon.json rngCalls', EXP.rngCalls, js('source/canon.json').rngCalls);
const typedKinds = {};
for (const t of pl.typed) typedKinds[t.type] = (typedKinds[t.type] || 0) + 1;
check('data', 'typed wrappers recorded silo/dock/fuelStation/crusher/sawShed', true, ['silo', 'dock', 'fuelStation', 'crusher', 'sawShed'].every((k) => typedKinds[k] > 0));

// ---------------------------------------------------------------- world
const world = js('world.json');
check('M0', 'lots', EXP.lots, world.lots.length);
check('M0', 'roads', EXP.roads.count, world.roads.length);
check('M0', 'road points', EXP.roads.points, world.roads.reduce((a, r) => a + r.pts.length, 0));
check('data', 'POI pad heights (mm)', EXP.poiY, Object.fromEntries(world.pois.map((p) => [p.id, Math.round(p.y * 1000) / 1000])));

// ---------------------------------------------------------------- traffic
const tr = js('traffic.json');
const segById = new Map(tr.segs.map((s) => [s.id, s]));
check('M0', 'traffic nodes', EXP.traffic.nodes, tr.nodes.length);
check('M0', 'traffic lanes', EXP.traffic.lanes, tr.lanes.length);
check('M0', 'traffic connectors', EXP.traffic.conns, tr.conns.length);
check('M0', 'traffic loops', EXP.traffic.loops, tr.loops.length);
check('data', 'car-only loops', EXP.traffic.carOnlyLoops, tr.loops.filter((id) => segById.get(id).noTruck).length);
const rb = tr.nodes.filter((n) => n.rb);
check('data', 'roundabout centre and radius', [EXP.traffic.roundabout], rb.map((n) => ({ x: n.rb.C.x, z: n.rb.C.z, R: n.rb.R })));

// ---------------------------------------------------------------- static_props.glb
const glb = rd('reference/static_props.glb');
if (glb.readUInt32LE(0) !== 0x46546c67) throw new Error('static_props.glb is not a GLB');
const jlen = glb.readUInt32LE(12);
const gj = JSON.parse(glb.toString('utf8', 20, 20 + jlen));
const bin = glb.subarray(20 + jlen + 8);
const verts = {}, posSha = {};
for (const node of gj.nodes) {
  if (node.mesh === undefined) continue;
  const prim = gj.meshes[node.mesh].primitives[0];
  const acc = gj.accessors[prim.attributes.POSITION];
  const bv = gj.bufferViews[acc.bufferView];
  if (bv.byteStride && bv.byteStride !== 12) throw new Error('interleaved positions');
  verts[node.name] = acc.count;
  posSha[node.name] = sha(bin.subarray((bv.byteOffset || 0) + (acc.byteOffset || 0), (bv.byteOffset || 0) + (acc.byteOffset || 0) + acc.count * 12));
}
check('M0', 'static_props.glb bucket vertex counts', EXP.staticPropsVertices, Object.fromEntries(Object.keys(EXP.staticPropsVertices).map((k) => [k, verts[k]])));
check('data', 'static_props.glb positions hash as recorded in the page', true,
  !!M.staticProps && Object.keys(EXP.staticPropsVertices).every((k) => M.staticProps[k] && M.staticProps[k].positionSha256 === posSha[k] && M.staticProps[k].vertices === verts[k]));

// ---------------------------------------------------------------- references
const cams = js('reference/cameras.json');
check('M0', 'cameras', EXP.shots.cameras, cams.cameras.length);
const shotFiles = listed.filter((f) => f.startsWith('reference/shots/'));
check('M0', 'shots (cameras x times)', EXP.shots.cameras * EXP.shots.times.length, shotFiles.length);
check('data', 'shots are 1600x900 PNGs', true, shotFiles.every((f) => { const i = pngInfo(rd(f)); return i.width === EXP.shots.width && i.height === EXP.shots.height; }));
const pr = js('terrain/probes.json');
check('data', 'height probes', EXP.probes, pr.probes.length);
check('data', 'camera rays', EXP.rays.cameras * EXP.rays.perCamera, pr.rays.reduce((a, r) => a + r.hits.length, 0));
check('data', 'probe and ray values finite', true, pr.probes.every((p) => p.every(Number.isFinite)) && pr.rays.every((r) => r.hits.every((h) => h.every(Number.isFinite))));
if (M.files['reference/deform/calls.json']) check('data', 'deform calls', EXP.deformCalls, js('reference/deform/calls.json').calls.length);
const traceFiles = listed.filter((f) => f.startsWith('reference/traces/')).map((f) => f.slice('reference/traces/'.length, -5)).sort();
check('data', 'golden traces', EXP.traces, traceFiles);
for (const id of traceFiles) {
  const t = js(`reference/traces/${id}.json`);
  const src = path.join(HERE, 'traces', id + '.json');
  const inSha = fs.existsSync(src) ? sha(fs.readFileSync(src)) : null;
  check('data', `trace ${id}: samples, finite values, input file unchanged`, true,
    t.samples.length === Math.floor(t.steps / t.sampleEvery) + 1 && t.samples.every((s) => s.every(Number.isFinite)) && t.inputSha256 === inSha && M.traces && M.traces[id] && M.traces[id].inputSha256 === inSha);
}

// ---------------------------------------------------------------- manifest metadata
check('M0', 'renderer recorded', true, !!(M.renderer && (M.renderer.unmaskedRenderer || M.renderer.renderer)));
check('data', 'PRNG seed recorded', true, Number.isInteger(M.prng && M.prng.seed));
check('data', 'three.js module hash recorded', true, /^[0-9a-f]{64}$/.test(M.threeModule && M.threeModule.sha256));
check('data', 'oracle commit', EXP.oracleCommit, M.source && M.source.commit);
check('data', 'game loop never ran before the copies', 0, M.clock && M.clock.framesRun);

// ---------------------------------------------------------------- report
const bad = rows.filter((r) => !r.ok);
const show = (v) => { const s = typeof v === 'string' ? v : JSON.stringify(v); return s.length > 70 ? s.slice(0, 67) + '...' : s; };
for (const r of rows) console.log(`${r.ok ? 'ok  ' : 'FAIL'}  [${r.gate.padEnd(5)}] ${r.name.padEnd(56)} ${r.ok ? show(r.got) : `want ${show(r.want)} got ${show(r.got)}`}`);
console.log(`verify-canon: ${rows.length - bad.length}/${rows.length} checks green in ${DIR} (renderer: ${M.renderer && (M.renderer.unmaskedRenderer || M.renderer.renderer)})`);
if (REPORT) fs.writeFileSync(REPORT, JSON.stringify({ ok: !bad.length, dir: DIR, checks: rows }, null, 2) + '\n');
process.exit(bad.length ? 1 : 0);
