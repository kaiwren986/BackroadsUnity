#!/usr/bin/env node
// Clips a TownData town to a small square test fixture for the road bake (roads-R0o "done when"): the same layout as
// TownData/<town>/v1 (manifest.json with file hashes, roads.json, buildings.json, sites.json, dem/dem.json + a DEM crop,
// credit.txt), moved so the fixture's centre is (0, 0). The importer then treats it as a play area of half = --half
// metres (roads end 50 m inside its edge, EDGE_RULE) inside an extract of half + --skirt metres.
//
//   node tools/world/roads/make-fixture.mjs --town stockport-oh --name village --cx 380 --cz 420
//   node tools/world/roads/make-fixture.mjs --town stockport-oh --name hills --cx 300 --cz -450
//   node tools/world/roads/make-fixture.mjs ... --table      (also print the topology table the tests check)
//   node tools/world/roads/make-fixture.mjs ... --check      (rebuild into a temp folder and compare bytes)
//
// --table prints, from the raw OSM topology and independently of the C# importer, every node where three or more
// drivable road ends meet (a junction) and every dead end, inside the fixture's inner square less a 30 m margin.
// Zero dependencies (Node 20+). Output files use LF and are byte-stable.

import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const args = Object.fromEntries(process.argv.slice(2).reduce((acc, a, i, all) => {
  if (a.startsWith('--')) acc.push([a.slice(2), all[i + 1] && !all[i + 1].startsWith('--') ? all[i + 1] : true]);
  return acc;
}, []));
const town = args.town || 'stockport-oh';
const name = args.name;
const cx = Number(args.cx), cz = Number(args.cz);
const half = Number(args.half || 300), skirt = Number(args.skirt || 100);
if (!name || !Number.isInteger(cx) || !Number.isInteger(cz)) {
  console.error('usage: make-fixture.mjs --town <id> --name <fixture> --cx <int metres> --cz <int metres> [--half 300] [--skirt 100] [--table] [--check]');
  process.exit(2);
}
const SRC = path.join(REPO, 'TownData', town, 'v1');
const OUT = path.join(REPO, 'Game', 'Assets', 'Backroads', 'Tests', 'EditMode', 'Roads', 'Fixtures', name);
const ext = half + skirt;

const r3 = (v) => Math.round(v * 1000) / 1000;
const inside = (x, z, h) => Math.abs(x) <= h && Math.abs(z) <= h;
const readJson = (f) => JSON.parse(fs.readFileSync(path.join(SRC, f), 'utf8'));

function build(outDir) {
  fs.mkdirSync(path.join(outDir, 'dem'), { recursive: true });
  const manifestIn = readJson('manifest.json');
  const roads = readJson('roads.json');
  const nodeById = new Map(roads.nodes.map((n) => [n.id, n]));
  const local = (n) => ({ x: r3(n.x - cx), z: r3(n.z - cz) });

  // ways: from the first to the last node inside the extract square, plus one node beyond at each end
  const ways = [];
  const used = new Set();
  for (const w of roads.ways) {
    const idx = w.nodes.map((id, i) => [id, i]).filter(([id]) => {
      const n = nodeById.get(id);
      if (!n) return false;
      const p = local(n);
      return inside(p.x, p.z, ext);
    }).map(([, i]) => i);
    if (idx.length === 0) continue;
    const a = Math.max(0, idx[0] - 1), b = Math.min(w.nodes.length - 1, idx[idx.length - 1] + 1);
    const nodes = w.nodes.slice(a, b + 1);
    if (nodes.length < 2) continue;
    nodes.forEach((id) => used.add(id));
    ways.push({ nodes, osm: w.osm, tags: w.tags });
  }
  const nodes = [...used].sort((a, b) => a - b).map((id) => {
    const n = nodeById.get(id);
    const p = local(n);
    const o = { id, outside: !inside(p.x, p.z, ext), x: p.x, z: p.z };
    if (n.tags) o.tags = n.tags;
    return o;
  });
  const roadsOut = { format: 'backroads-towndata', kind: 'roads', nodes, note: `fixture: TownData/${town}/v1 roads.json clipped to a ${2 * ext} m square around (${cx}, ${cz}) and moved to (0, 0)`, version: 1, ways };

  const polyIn = (poly) => poly.some(([x, z]) => inside(x - cx, z - cz, ext));
  const move = (poly) => poly.map(([x, z]) => [r3(x - cx), r3(z - cz)]);
  const b = readJson('buildings.json');
  const buildings = b.buildings.filter((q) => q.poly && polyIn(q.poly)).map((q) => ({ osm: q.osm, poly: move(q.poly), type: q.type }));
  const s = readJson('sites.json');
  const parking = (s.parking || []).filter((p) => p.polys.some((pp) => polyIn(pp.outer)))
    .map((p) => ({ osm: p.osm, polys: p.polys.map((pp) => ({ outer: move(pp.outer) })), tags: p.tags }));

  // DEM crop: whole metres, rows south to north
  const demIn = readJson('dem/dem.json');
  const n = demIn.n, res = demIn.res;
  const buf = fs.readFileSync(path.join(SRC, 'dem', demIn.file));
  if (buf.length !== n * n * 4) throw new Error(`DEM is ${buf.length} bytes, expected ${n * n * 4} (an LFS pointer? run git lfs pull)`);
  const i0 = Math.round((cx - ext - demIn.x0) / res), j0 = Math.round((cz - ext - demIn.z0) / res);
  const m = Math.round(2 * ext / res) + 1;
  if (i0 < 0 || j0 < 0 || i0 + m > n || j0 + m > n) throw new Error('the fixture square leaves the DEM');
  const crop = Buffer.alloc(m * m * 4);
  for (let j = 0; j < m; j++) buf.copy(crop, j * m * 4, ((j0 + j) * n + i0) * 4, ((j0 + j) * n + i0 + m) * 4);
  const demOut = {
    Y0: demIn.Y0, encoding: demIn.encoding, file: 'dem_1m.f32', format: 'backroads-towndata-dem',
    layout: 'k = j*nx + i; x = x0 + i*res, z = z0 + j*res (rows south to north, columns west to east); value = y = elevation - Y0',
    nx: m, nz: m, res, source: `crop of TownData/${town}/v1 dem/${demIn.file} (${demIn.source})`, version: 1, x0: -ext, z0: -ext,
  };

  const write = (rel, data) => {
    const p = path.join(outDir, rel);
    fs.writeFileSync(p, data);
    return { bytes: data.length, sha256: crypto.createHash('sha256').update(data).digest('hex') };
  };
  const json = (o) => Buffer.from(JSON.stringify(o) + '\n', 'utf8');
  const files = {};
  files['roads.json'] = write('roads.json', json(roadsOut));
  files['buildings.json'] = write('buildings.json', json({ buildings, format: 'backroads-towndata', kind: 'buildings', version: 1 }));
  files['sites.json'] = write('sites.json', json({ format: 'backroads-towndata', kind: 'sites', parking, version: 1 }));
  files['dem/dem.json'] = write('dem/dem.json', json(demOut));
  files['dem/dem_1m.f32'] = write('dem/dem_1m.f32', crop);
  files['credit.txt'] = write('credit.txt', Buffer.from(
    `Test fixture clipped from TownData/${town}/v1 by tools/world/roads/make-fixture.mjs (centre ${cx}, ${cz}; play area +-${half} m).\n` +
    'Map data (c) OpenStreetMap contributors, ODbL 1.0 (https://www.openstreetmap.org/copyright).\n' +
    'Elevation: USGS 3D Elevation Program (3DEP), public domain.\n', 'utf8'));
  const zones = {};
  for (const k of ['kerbZone', 'downtownZone']) {
    const zIn = manifestIn.zones && manifestIn.zones[k];
    if (zIn && zIn.polygon) zones[k] = { polygon: move(zIn.polygon), rule: zIn.rule };
  }
  const manifest = {
    credit: manifestIn.credit, extract: { half: ext }, files, fixture: { centre: [cx, cz], from: `TownData/${town}/v1`, half, skirt, tool: 'tools/world/roads/make-fixture.mjs' },
    format: 'backroads-towndata', playArea: { half }, version: 1, zones,
  };
  fs.writeFileSync(path.join(outDir, 'manifest.json'), json(manifest));
  return { roads: roadsOut, nodeCount: nodes.length, wayCount: ways.length, buildings: buildings.length, parking, m };
}

// The topology table, from the raw ways (independent of the C# importer): drivable classes as the importer keeps them.
function table(fix) {
  const keepClass = (t) => {
    const hw = t.highway || '', sv = t.service || '';
    if (t.area === 'yes') return false;
    if (/^(motorway|trunk|primary|secondary|tertiary)(_link)?$/.test(hw)) return true;
    if (['residential', 'unclassified', 'living_street', 'road', 'track'].includes(hw)) return true;
    if (hw === 'service') return !['driveway', 'parking_aisle', 'drive-through', 'shared_driveway'].includes(sv);
    return false;
  };
  const byId = new Map(fix.roads.nodes.map((n) => [n.id, n]));
  const deg = new Map();
  for (const w of fix.roads.ways) {
    if (!keepClass(w.tags)) continue;
    const ns = w.nodes.filter((id, i, a) => i === 0 || a[i - 1] !== id);
    ns.forEach((id, i) => deg.set(id, (deg.get(id) || 0) + (i === 0 || i === ns.length - 1 ? 1 : 2)));
  }
  const lim = half - 50 - 30;
  const rows = [];
  for (const [id, d] of [...deg].sort((a, b) => a[0] - b[0])) {
    if (d === 2) continue;
    const n = byId.get(id);
    if (!inside(n.x, n.z, lim)) continue;
    rows.push({ id, x: n.x, z: n.z, arms: d });
  }
  console.log(`// ${name}: ${rows.filter((r) => r.arms >= 3).length} junctions, ${rows.filter((r) => r.arms === 1).length} dead ends inside +-${lim} m (osm node, x, z, arm ends)`);
  for (const r of rows) console.log(`            (${r.id}, ${r.x.toFixed(1)}, ${r.z.toFixed(1)}, ${r.arms}),`);
}

if (args.check) {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'br-fixture-'));
  build(tmp);
  const diff = [];
  const walk = (rel) => {
    for (const e of fs.readdirSync(path.join(tmp, rel), { withFileTypes: true })) {
      const r = path.join(rel, e.name);
      if (e.isDirectory()) { walk(r); continue; }
      const a = fs.readFileSync(path.join(tmp, r));
      const bp = path.join(OUT, r);
      if (!fs.existsSync(bp) || !a.equals(fs.readFileSync(bp))) diff.push(r);
    }
  };
  walk('.');
  fs.rmSync(tmp, { recursive: true, force: true });
  if (diff.length) { console.error('fixture differs from a fresh build: ' + diff.join(', ')); process.exit(1); }
  console.log(`fixture ${name} matches a fresh build`);
} else {
  const fix = build(OUT);
  console.log(`wrote ${path.relative(REPO, OUT)}: ${fix.wayCount} ways, ${fix.nodeCount} nodes, ${fix.buildings} buildings, ${fix.parking.length} parking lots, DEM ${fix.m} x ${fix.m}`);
  if (args.table) table(fix);
}
