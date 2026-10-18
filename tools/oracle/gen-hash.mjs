#!/usr/bin/env node
// Quick check of the world generator without a browser (PLAN §1.8.6, T0.7d).
//
//   node tools/oracle/gen-hash.mjs [--snapshot artifacts/oracle/552f780] [--json out.json]
//
// Imports the snapshot's js/world.js in Node, runs generate(20251015) and checks the six layers that do
// not depend on props.js (H, BASE, ROADD, MUD, WEAR, TINT) against expected.json, plus SURFACE before the
// props stamps, the road/lot/POI numbers and the headless traffic network (no colliders, so 52 lanes,
// 104 connectors and no roundabout, 03 §3.1). Exit code 0 = all equal. Runs in every CI pass.
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { spawnSync } from 'node:child_process';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..');
const arg = (n, d = null) => { const i = process.argv.indexOf(n); return i >= 0 ? process.argv[i + 1] : d; };
const EXP = JSON.parse(fs.readFileSync(path.join(HERE, 'expected.json'), 'utf8'));
const SNAP = path.resolve(REPO, arg('--snapshot', path.join('artifacts', 'oracle', EXP.oracleCommit.slice(0, 7))));

if (!fs.existsSync(path.join(SNAP, 'js', 'world.js'))) {
  console.log('gen-hash: no snapshot yet, making one');
  const r = spawnSync(process.execPath, [path.join(HERE, 'make-snapshot.mjs')], { stdio: 'inherit' });
  if (r.status !== 0) process.exit(r.status || 1);
}

const sha = (ta) => createHash('sha256').update(new Uint8Array(ta.buffer, ta.byteOffset, ta.byteLength)).digest('hex');
const results = [];
const check = (name, got, want, ok = got === want) => { results.push({ name, ok, got, want }); };

const t0 = performance.now();
const W = await import(pathToFileURL(path.join(SNAP, 'js', 'world.js')).href);
await W.generate(EXP.worldSeed);
const genMs = performance.now() - t0;

const layer = { 'terrain/heights.f32': W.H, 'terrain/base.f32': W.BASE, 'terrain/roadd.u8': W.ROADD, 'terrain/mud.u8': W.MUD, 'terrain/wear.u8': W.WEAR, 'terrain/tint.f32': W.TINT };
for (const [rel, ta] of Object.entries(layer)) {
  check(`${rel} sha256`, sha(ta), EXP.layers[rel].sha256);
  check(`${rel} bytes`, ta.byteLength, EXP.layers[rel].bytes);
}
check('SURFACE before props (prefix)', sha(W.SURFACE).slice(0, 16), EXP.surfaceBeforePropsPrefix);

let diff = 0, wear = 0, mn = Infinity, mx = -Infinity;
for (let k = 0; k < W.H.length; k++) {
  if (W.H[k] !== W.BASE[k]) diff++;
  if (W.WEAR[k]) wear++;
  if (W.H[k] < mn) mn = W.H[k];
  if (W.H[k] > mx) mx = W.H[k];
}
check('BASE != H vertices (quarry piles)', diff, EXP.baseDiffersFromH);
check('WEAR non-zero vertices', wear, EXP.wearNonZero);
check('H min (mm)', Math.round(mn * 1000) / 1000, EXP.heightRange[0]);
check('H max (mm)', Math.round(mx * 1000) / 1000, EXP.heightRange[1]);
for (const p of W.POIS) check(`POI ${p.id} pad y (mm)`, Math.round(p.y * 1000) / 1000, EXP.poiY[p.id]);

const R = W.ROADS;
check('roads', R.length, EXP.roads.count);
check('road points', R.reduce((a, r) => a + r.pts.length, 0), EXP.roads.points);
check('roads paved (through)', R.filter((r) => r.paved && !r.street).length, EXP.roads.paved);
check('roads dirt', R.filter((r) => !r.paved).length, EXP.roads.dirt);
check('roads canon streets', R.filter((r) => r.street).length, EXP.roads.streets);
check('road length km (10 m)', Math.round(R.reduce((a, r) => a + r.len, 0) / 10) / 100, EXP.roads.km);
check('lots', W.LOTS.length, EXP.lots);
check('hamlets', W.HAMLETS.length, EXP.hamlets);
check('bogs', W.BOGS.length, EXP.bogs);
check('fields', W.FIELDS.length, EXP.fields);

// canon ops, straight from the canon file the planner replayed
const { CANON } = await import(pathToFileURL(path.join(SNAP, 'js', 'townGen.js')).href);
const ops = {};
for (const o of CANON.ops) ops[o[0]] = (ops[o[0]] || 0) + 1;
for (const [k, v] of Object.entries(EXP.canonOps)) check(`canon ops ${k}`, ops[k] || 0, v);
check('canon rngCalls', CANON.rngCalls, EXP.rngCalls);

// headless traffic network: default clearances (03 §3.1 [measured])
const T = await import(pathToFileURL(path.join(SNAP, 'js', 'trafficNet.js')).href);
const net = T.buildNetwork();
check('headless traffic lanes', net.lanes.length, EXP.trafficHeadless.lanes);
check('headless traffic conns', net.conns.length, EXP.trafficHeadless.conns);
check('headless traffic roundabouts', net.nodes.filter((n) => n.rb).length, EXP.trafficHeadless.roundabouts);

const bad = results.filter((r) => !r.ok);
for (const r of results) console.log(`${r.ok ? 'ok  ' : 'FAIL'}  ${r.name.padEnd(40)} ${r.ok ? String(r.got).slice(0, 20) : `got ${r.got}, want ${r.want}`}`);
console.log(`gen-hash: ${results.length - bad.length}/${results.length} equal; generate() ${(genMs / 1000).toFixed(2)} s in Node ${process.version}`);
const out = arg('--json');
if (out) fs.writeFileSync(out, JSON.stringify({ ok: !bad.length, genMs, node: process.version, results }, null, 2) + '\n');
process.exit(bad.length ? 1 : 0);
