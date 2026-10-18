#!/usr/bin/env node
// Compares two canon exports (PLAN section 6.1 M0: "a second export run gives an identical manifest").
//
//   node tools/oracle/compare-exports.mjs <dirA> <dirB> [--report out.json] [--files-only]
//
// The manifests must be equal apart from source.exportedAt, source.userAgent, timings and the
// reference/shots/* entries; every shot pair must reach SSIM >= 0.99 (8x8 windows on luma).
// --files-only compares only the per-file hashes (except the shots), as run-export.ps1 -Install does.
import fs from 'node:fs';
import path from 'node:path';
import { decodePNG, luma, ssim } from './pngread.mjs';

const args = process.argv.slice(2);
const flag = (n) => args.includes(n);
const opt = (n) => { const i = args.indexOf(n); return i >= 0 ? args[i + 1] : null; };
const [A, B] = args.filter((a, i) => !a.startsWith('--') && args[i - 1] !== '--report');
if (!A || !B) { console.error('usage: compare-exports.mjs <dirA> <dirB> [--report out.json] [--files-only]'); process.exit(2); }
const load = (d) => JSON.parse(fs.readFileSync(path.join(d, 'manifest.json'), 'utf8'));
const ma = load(A), mb = load(B);
const SSIM_MIN = 0.99;
const isShot = (k) => k.startsWith('reference/shots/');

function strip(m) {
  const c = JSON.parse(JSON.stringify(m));
  if (c.source) { delete c.source.exportedAt; delete c.source.userAgent; }
  delete c.timings;
  for (const k of Object.keys(c.files || {})) if (isShot(k)) delete c.files[k];
  return c;
}
const diffs = [];
function walk(a, b, p) {
  if (typeof a !== typeof b || Array.isArray(a) !== Array.isArray(b) || (a === null) !== (b === null)) { diffs.push({ path: p, a, b }); return; }
  if (a && typeof a === 'object') {
    for (const k of new Set([...Object.keys(a), ...Object.keys(b)])) walk(a[k], b[k], p ? `${p}.${k}` : k);
    return;
  }
  if (a !== b) diffs.push({ path: p, a, b });
}
const sa = strip(ma), sb = strip(mb);
if (flag('--files-only')) walk(sa.files, sb.files, 'files');
else walk(sa, sb, '');

const shots = [];
if (!flag('--files-only')) {
  const keys = [...new Set([...Object.keys(ma.files), ...Object.keys(mb.files)])].filter(isShot).sort();
  for (const k of keys) {
    if (!ma.files[k] || !mb.files[k]) { shots.push({ file: k, ok: false, why: 'missing in one export' }); continue; }
    if (ma.files[k].sha256 === mb.files[k].sha256) { shots.push({ file: k, ok: true, identical: true, ssim: 1 }); continue; }
    const ia = decodePNG(fs.readFileSync(path.join(A, k))), ib = decodePNG(fs.readFileSync(path.join(B, k)));
    if (ia.width !== ib.width || ia.height !== ib.height) { shots.push({ file: k, ok: false, why: 'size differs' }); continue; }
    const s = ssim(luma(ia), luma(ib), ia.width, ia.height);
    shots.push({ file: k, ok: s >= SSIM_MIN, identical: false, ssim: s });
  }
}
const badShots = shots.filter((s) => !s.ok);
for (const d of diffs.slice(0, 40)) console.log(`DIFF  ${d.path}: ${JSON.stringify(d.a)} vs ${JSON.stringify(d.b)}`.slice(0, 300));
if (shots.length) {
  const ident = shots.filter((s) => s.identical).length;
  const minS = Math.min(...shots.map((s) => s.ssim ?? 0));
  console.log(`shots: ${shots.length} pairs, ${ident} byte-identical, min SSIM ${minS.toFixed(5)} (need >= ${SSIM_MIN})`);
  for (const s of badShots) console.log(`FAIL  ${s.file}: ${s.why || 'SSIM ' + s.ssim}`);
}
const ok = !diffs.length && !badShots.length;
console.log(`compare-exports: ${ok ? 'MATCH' : 'DIFFERENT'} (${diffs.length} manifest differences outside the ignored keys)`);
const rep = opt('--report');
if (rep) fs.writeFileSync(rep, JSON.stringify({ ok, a: path.resolve(A), b: path.resolve(B), ignored: ['source.exportedAt', 'source.userAgent', 'timings', 'files[reference/shots/*]'], diffs, shots }, null, 2) + '\n');
process.exit(ok ? 0 : 1);
