#!/usr/bin/env node
// Downloads the OpenStreetMap data of one candidate town (decision U25): ONE Overpass query per town,
// cached raw under artifacts/scout/<town>/osm.json (artifacts/ is gitignored). Zero dependencies.
//
//   node tools/scout/fetch-osm.mjs <town-id> [<town-id> ...] [--all] [--size 4000] [--force] [--endpoint URL]
//
// The query takes every tagged node, every way and every multipolygon relation in the town's square
// frame (geo.mjs), with geometry. Data (c) OpenStreetMap contributors, ODbL 1.0.
// A busy public server answers 429/502/503/504: we back off (20 s, 40 s, ... up to 5 tries per server)
// and then try the next server. Already cached towns are skipped unless --force.
import fs from 'node:fs';
import path from 'node:path';
import { getTown, townFrame, townDir, townIds, arg, flag, UA, sleep } from './geo.mjs';

const ENDPOINTS = arg('--endpoint') ? [arg('--endpoint')] : [
  'https://overpass-api.de/api/interpreter',
  'https://overpass.kumi.systems/api/interpreter',
  'https://maps.mail.ru/osm/tools/overpass/api/interpreter',
];

export function overpassQuery(f) {
  const bb = [f.south, f.west, f.north, f.east].map((v) => v.toFixed(6)).join(',');
  return `[out:json][timeout:300][maxsize:536870912][bbox:${bb}];
(
  node(if:count_tags()>0);
  way;
  rel[type=multipolygon];
);
out geom qt;`;
}

async function post(endpoint, query) {
  const res = await fetch(endpoint, {
    method: 'POST',
    headers: { 'User-Agent': UA, 'Content-Type': 'application/x-www-form-urlencoded' },
    body: 'data=' + encodeURIComponent(query),
    signal: AbortSignal.timeout(420_000),
  });
  const text = await res.text();
  return { status: res.status, text };
}

async function fetchTown(id) {
  const town = getTown(id);
  const size = Number(arg('--size', town.frameSize));
  const f = townFrame(town, size);
  const dir = townDir(id);
  const out = path.join(dir, 'osm.json');
  if (fs.existsSync(out) && !flag('--force')) { console.log(`${id}: cached (${out})`); return false; }
  const query = overpassQuery(f);
  for (const ep of ENDPOINTS) {
    let wait = 20_000;
    for (let attempt = 1; attempt <= 5; attempt++) {
      const t0 = Date.now();
      let r;
      try { r = await post(ep, query); } catch (e) { r = { status: 0, text: String(e) }; }
      const secs = ((Date.now() - t0) / 1000).toFixed(1);
      const ok = r.status === 200 && r.text.trimStart().startsWith('{');
      let json = null;
      if (ok) { try { json = JSON.parse(r.text); } catch { json = null; } }
      if (json && !json.remark?.includes('runtime error')) {
        fs.writeFileSync(out, r.text);
        const counts = { node: 0, way: 0, relation: 0 };
        for (const e of json.elements) counts[e.type]++;
        const meta = {
          town: id, endpoint: ep, fetchedAt: new Date().toISOString(), seconds: Number(secs), bytes: r.text.length,
          osmBase: json.osm3s?.timestamp_osm_base, frame: { epsg: f.epsg, E0: f.E0, N0: f.N0, size: f.size },
          bbox: { south: f.south, west: f.west, north: f.north, east: f.east }, counts, query,
          licence: '(c) OpenStreetMap contributors, ODbL 1.0',
        };
        fs.writeFileSync(path.join(dir, 'osm.meta.json'), JSON.stringify(meta, null, 2) + '\n');
        console.log(`${id}: ${(r.text.length / 1e6).toFixed(1)} MB in ${secs} s from ${new URL(ep).host}: ${counts.node} nodes, ${counts.way} ways, ${counts.relation} relations`);
        return true;
      }
      const why = json?.remark ?? r.text.slice(0, 120).replace(/\s+/g, ' ');
      console.log(`${id}: ${new URL(ep).host} try ${attempt}: HTTP ${r.status} after ${secs} s (${why})`);
      if (![0, 429, 502, 503, 504].includes(r.status)) break; // not a busy-server answer: next server
      await sleep(wait);
      wait *= 2;
    }
  }
  throw new Error(`${id}: every Overpass server failed`);
}

const ids = townIds();
if (!ids.length) { console.error('usage: node tools/scout/fetch-osm.mjs <town-id> ... | --all'); process.exit(1); }
let failed = 0;
for (const id of ids) {
  try { if (await fetchTown(id)) await sleep(10_000); } catch (e) { failed++; console.error(String(e.message ?? e)); } // be polite between towns
}
process.exit(failed ? 1 : 0);
