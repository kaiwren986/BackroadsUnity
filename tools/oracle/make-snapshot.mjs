#!/usr/bin/env node
// Makes the oracle snapshot that every canon export runs against (PLAN §1.8.2, T0.7a).
//
//   node tools/oracle/make-snapshot.mjs [--out artifacts/oracle/552f780] [--keep]
//
// 1. `git -c core.autocrlf=false -C <oracle> archive --format=tar <commit>` (oracle.lock.json), unpacked
//    by the small tar reader below, so the snapshot holds the committed LF bytes. Every extracted file is
//    checked against its git blob id; the oracle working tree is never touched.
// 2. The export hooks are applied to the SNAPSHOT copy only. Each is an exact-match replacement that
//    fails loudly unless its anchor occurs exactly once.
// 3. determinism.js, exportUnity.js and goldenTraces.js are copied into the snapshot's js/ and their
//    module tags are injected into index.html (determinism first, before the game's module).
// 4. _snapshot.json records the commit, the hooks and the SHA-256 of every hooked or added file.
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..');

function arg(name, def = null) {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : def;
}
const fail = (msg) => { console.error('make-snapshot: ' + msg); process.exit(1); };
const sha256 = (buf) => createHash('sha256').update(buf).digest('hex');

const lock = JSON.parse(fs.readFileSync(path.join(REPO, 'oracle.lock.json'), 'utf8'));
const ORACLE = lock.repo;
const COMMIT = lock.commit;
if (!/^[0-9a-f]{40}$/.test(COMMIT)) fail('oracle.lock.json: commit must be a full 40-hex id');
const OUT = path.resolve(REPO, arg('--out', path.join('artifacts', 'oracle', COMMIT.slice(0, 7))));
if (!OUT.startsWith(path.join(REPO, 'artifacts') + path.sep)) fail('--out must be inside artifacts\\ (got ' + OUT + ')');

function git(args, opts = {}) {
  const r = spawnSync('git', ['-c', 'core.autocrlf=false', '-C', ORACLE, ...args], { maxBuffer: 1 << 30, ...opts });
  if (r.status !== 0) fail(`git ${args.join(' ')} failed: ${r.stderr ? r.stderr.toString() : r.error}`);
  return r.stdout;
}

// ------------------------------------------------------------------ 1. archive + extract

const full = git(['rev-parse', '--verify', COMMIT + '^{commit}']).toString().trim();
if (full !== COMMIT) fail(`oracle commit mismatch: ${full} != ${COMMIT}`);

const tar = git(['archive', '--format=tar', COMMIT]);
// blob ids of the tree, to prove the bytes on disk are the committed bytes
const blobs = new Map();
for (const rec of git(['ls-tree', '-r', '-z', '--full-tree', COMMIT]).toString().split('\0')) {
  if (!rec) continue;
  const m = /^(\d+) (\w+) ([0-9a-f]{40})\t(.*)$/s.exec(rec);
  if (!m) fail('cannot parse ls-tree record: ' + rec);
  if (m[2] !== 'blob') fail(`unsupported tree entry ${m[2]} ${m[4]}`);
  if (m[1] === '120000') fail('symlink in oracle tree: ' + m[4]);
  blobs.set(m[4], m[3]);
}

function* tarEntries(buf) {
  let off = 0, pax = {}, globalPax = {};
  let h = null;
  const str = (o, n) => { const s = h.subarray(o, o + n); const z = s.indexOf(0); return (z >= 0 ? s.subarray(0, z) : s).toString('utf8'); };
  const parsePax = (data) => {
    const out = {};
    let p = 0;
    while (p < data.length) {
      const sp = data.indexOf(0x20, p);
      const len = parseInt(data.subarray(p, sp).toString(), 10);
      const rec = data.subarray(sp + 1, p + len - 1).toString('utf8');
      const eq = rec.indexOf('=');
      out[rec.slice(0, eq)] = rec.slice(eq + 1);
      p += len;
    }
    return out;
  };
  while (off + 512 <= buf.length) {
    h = buf.subarray(off, off + 512);
    if (h.every((b) => b === 0)) break;
    let sum = 0;
    for (let i = 0; i < 512; i++) sum += i >= 148 && i < 156 ? 32 : h[i];
    const chk = parseInt(str(148, 8).trim(), 8);
    if (sum !== chk) fail(`tar header checksum mismatch at ${off}`);
    const size = parseInt(str(124, 12).trim() || '0', 8);
    const type = String.fromCharCode(h[156] || 0x30);
    const prefix = str(345, 155);
    let name = (prefix ? prefix + '/' : '') + str(0, 100);
    const data = buf.subarray(off + 512, off + 512 + size);
    off += 512 + Math.ceil(size / 512) * 512;
    if (type === 'g') { globalPax = { ...globalPax, ...parsePax(data) }; continue; }
    if (type === 'x') { pax = parsePax(data); continue; }
    if (pax.path) name = pax.path;
    pax = {};
    yield { name, type, data, globalPax };
  }
}

if (fs.existsSync(OUT)) {
  if (process.argv.includes('--keep')) fail(OUT + ' exists (--keep)');
  fs.rmSync(OUT, { recursive: true, force: true });
}
fs.mkdirSync(OUT, { recursive: true });
let nFiles = 0, archiveComment = null;
for (const e of tarEntries(tar)) {
  if (e.globalPax.comment) archiveComment = e.globalPax.comment;
  const rel = e.name.replace(/\/$/, '');
  if (!rel || rel.split('/').some((s) => s === '..' || s === '') || path.isAbsolute(rel) || /[:\\]/.test(rel)) fail('unsafe path in archive: ' + e.name);
  const dest = path.join(OUT, ...rel.split('/'));
  if (e.type === '5') { fs.mkdirSync(dest, { recursive: true }); continue; }
  if (e.type !== '0') fail(`unsupported tar entry type '${e.type}' for ${e.name}`);
  const want = blobs.get(rel);
  if (!want) fail('archive file not in tree: ' + rel);
  const got = createHash('sha1').update(`blob ${e.data.length}\0`).update(e.data).digest('hex');
  if (got !== want) fail(`blob mismatch for ${rel}: ${got} != ${want} (attributes rewrote the bytes?)`);
  fs.mkdirSync(path.dirname(dest), { recursive: true });
  fs.writeFileSync(dest, e.data);
  blobs.delete(rel);
  nFiles++;
}
if (blobs.size) fail('files missing from archive: ' + [...blobs.keys()].join(', '));
if (archiveComment && archiveComment !== COMMIT) fail(`archive comment ${archiveComment} != ${COMMIT}`);

// ------------------------------------------------------------------ 2. hooks (snapshot copy only)

const hooks = [];
function hook(rel, label, anchor, replacement) {
  const file = path.join(OUT, ...rel.split('/'));
  const src = fs.readFileSync(file, 'utf8');
  let n = 0;
  for (let i = src.indexOf(anchor); i >= 0; i = src.indexOf(anchor, i + anchor.length)) n++;
  if (n !== 1) fail(`hook '${label}': anchor occurs ${n} times in ${rel} (must be exactly once):\n  ${anchor}`);
  const out = src.replace(anchor, () => replacement);
  fs.writeFileSync(file, out);
  hooks.push({ file: rel, label, anchor, anchorSha256: sha256(anchor) });
}

const P = 'js/props.js';
// --- the three one-liners (03 §3.4)
hook(P, 'treeList', 'const trees = [];', 'const trees = [];\nexport const treeList = trees; // [export hook]');
hook(P, 'rocks', 'const spots = [];', 'const spots = []; props.rocks = spots; // [export hook]');
hook(P, 'allOps', 'function rec(...op) { if (REC) REC.push(op); }',
  'function rec(...op) { props.allOps.push(op); if (REC) REC.push(op); } // [export hook]\n' +
  'props.allOps = []; // [export hook] every rec() op, fresh or replayed\n' +
  'props.typed = [];  // [export hook] typed-prop records from the wrappers below');

// --- typed-prop wrappers [T3]: record the arguments and pure reads, then call the original. No RNG.
function wrap(label, header, body) {
  const m = /^function (\w+)\(/.exec(header);
  if (!m || m[1] !== label) fail('bad wrapper header ' + header);
  hook(P, label + '__o', header,
    `// [export hook] typed-prop wrapper; the original is ${label}__o\n${header}\n${body}\n}\n` +
    header.replace(`function ${label}(`, `function ${label}__o(`));
}
wrap('placeModel', 'function placeModel(name, x, z, rot, w, d, colors, rng, litChance, lits = null) {', [
  '  const r = placeModel__o(name, x, z, rot, w, d, colors, rng, litChance, lits);',
  '  const op = props.allOps[props.allOps.length - 1];',
  "  const lit = op && op[0] === 'model' && op[1] === name ? op[8] : null;",
  "  props.typed.push({ type: 'model', name, x, z, rot, w, d, y: r.y, sx: r.sx, sz: r.sz, M: Array.from(r.M.elements), colors: { ...colors }, lit, replay: lits !== null });",
  '  return r;',
].join('\n'));
wrap('lamp', 'function lamp(x, z, faceRot, h = 6.5) {', [
  "  props.typed.push({ type: 'lamp', x, z, faceRot, h, y: W.heightAt(x, z) - 0.1 });",
  '  return lamp__o(x, z, faceRot, h);',
].join('\n'));
wrap('silo', 'function silo(x, z, r = 3, h = 12) {', [
  "  props.typed.push({ type: 'silo', x, z, r, h, y: W.heightAt(x, z) - 0.3 });",
  '  return silo__o(x, z, r, h);',
].join('\n'));
wrap('dock', 'function dock(q) {', [
  "  props.typed.push({ type: 'dock', q: { x: q.x, z: q.z, ux: q.ux, uz: q.uz, len: q.len, hw: q.hw } });",
  '  return dock__o(q);',
].join('\n'));
wrap('fuelStation', 'function fuelStation(p, rng) {', [
  "  const rec0 = { type: 'fuelStation', poi: p.id, px: p.x, pz: p.z };",
  '  const res = fuelStation__o(p, rng);',
  '  // pure reads after the call: the canopy centre is where the ring was set, rot follows the nearest paved road',
  '  const road = W.nearestRoad(p.x, p.z, true), rg = props.rings[p.id];',
  '  rec0.rot = Math.atan2(road ? road.tx : 1, road ? road.tz : 0);',
  '  rec0.x = rg.x; rec0.z = rg.z; rec0.y = W.heightAt(rg.x, rg.z) - 0.05;',
  '  props.typed.push(rec0);',
  '  return res;',
].join('\n'));
wrap('crusher', 'function crusher(x, z, rot) {', [
  "  props.typed.push({ type: 'crusher', x, z, rot, y: groundY(x, z, rot, 5, 6) - 0.2 });",
  '  return crusher__o(x, z, rot);',
].join('\n'));
wrap('sawShed', 'function sawShed(x, z, rot) {', [
  "  props.typed.push({ type: 'sawShed', x, z, rot, y: groundY(x, z, rot, 9, 7) - 0.2 });",
  '  return sawShed__o(x, z, rot);',
].join('\n'));

// --- module tags: determinism first, the game, then the export/trace harnesses
hook('index.html', 'modules', '<script type="module" src="js/main.js"></script>', [
  '<script type="module" src="js/determinism.js"></script><!-- [export hook] first module: seeded Math.random, scripted clock -->',
  '<script type="module" src="js/main.js"></script>',
  '<script type="module" src="js/exportUnity.js"></script><!-- [export hook] ?export=unity -->',
  '<script type="module" src="js/goldenTraces.js"></script><!-- [export hook] ?trace=<id> -->',
].join('\n'));

// ------------------------------------------------------------------ 3. harness files

const added = [];
for (const f of ['determinism.js', 'exportUnity.js', 'goldenTraces.js']) {
  const src = path.join(HERE, f);
  const dst = path.join(OUT, 'js', f);
  if (fs.existsSync(path.join(OUT, 'js', f)) && !added.includes(f)) fail(`js/${f} already exists in the oracle`);
  if (fs.existsSync(src)) fs.copyFileSync(src, dst);
  else fs.writeFileSync(dst, `// ${f} is not written yet (tools/oracle/${f})\n`);
  added.push(f);
}
// trace input files travel with the snapshot so the page can fetch them
const traceDir = path.join(HERE, 'traces');
if (fs.existsSync(traceDir)) {
  fs.mkdirSync(path.join(OUT, 'traces'), { recursive: true });
  const ids = [];
  for (const f of fs.readdirSync(traceDir).sort()) if (f.endsWith('.json')) { fs.copyFileSync(path.join(traceDir, f), path.join(OUT, 'traces', f)); ids.push(f.slice(0, -5)); }
  if (ids.includes('index')) fail('tools/oracle/traces/index.json is reserved');
  fs.writeFileSync(path.join(OUT, 'traces', 'index.json'), JSON.stringify(ids) + '\n');
}

// ------------------------------------------------------------------ 4. record

const fileHash = (rel) => sha256(fs.readFileSync(path.join(OUT, ...rel.split('/'))));
const info = {
  oracle: ORACLE, commit: COMMIT, files: nFiles, madeBy: 'tools/oracle/make-snapshot.mjs',
  hooks,
  hookedFiles: Object.fromEntries(['js/props.js', 'index.html'].map((f) => [f, fileHash(f)])),
  addedFiles: Object.fromEntries(added.map((f) => [`js/${f}`, fileHash(`js/${f}`)])),
};
fs.writeFileSync(path.join(OUT, '_snapshot.json'), JSON.stringify(info, null, 2) + '\n');
console.log(`snapshot ${COMMIT.slice(0, 7)}: ${nFiles} files verified against their blobs, ${hooks.length} hooks applied -> ${path.relative(REPO, OUT)}`);
