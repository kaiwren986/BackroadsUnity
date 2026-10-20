// Second OSM download (approval U28): towns where storeys and roof shapes are densely tagged, for the building
// profile lane A's Blender generator reads (building:levels, height, roof:shape, roof:height, roof:levels,
// roof:orientation, roof:angle, building:part).
//
//   node tools/osm/fetch-tags.mjs --screen   one count-only request for all CANDIDATES (no map data): how many
//                                            buildings, and how many carry levels, roof shape, height, parts;
//                                            writes artifacts/osm/screen-tags.json
//   node tools/osm/fetch-tags.mjs            one request per CHOSEN town (skips cached ones) into artifacts/osm/raw-tags/
//   node tools/osm/fetch-tags.mjs --list     prints the chosen towns, why, and the query
//
// OpenStreetMap data is (c) OpenStreetMap contributors, ODbL 1.0 (https://www.openstreetmap.org/copyright).
// Overpass is a free public service: one request at a time, pauses between towns, a descriptive User-Agent.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const CACHE = path.join(ROOT, 'artifacts', 'osm', 'raw-tags');
const SCREEN = path.join(ROOT, 'artifacts', 'osm', 'screen-tags.json');
const R = 1600;          // metres around the centre, as the site study
const R_SCREEN = 1500;   // metres for the count-only screening
const UA = 'Backroads-building-study/1.0 (hobby game; one-off fetch; contact via the OSM credit in the game)';
const HOSTS = ['https://overpass-api.de/api/interpreter', 'https://overpass.kumi.systems/api/interpreter'];

// Candidates: small towns like the new Millbrook (Stockport, Ohio: a Muskingum river village) first, Ohio and
// Ohio-river towns, then older small towns elsewhere in the US known for careful mapping, then a few Canadian and
// German small towns in case the US ones are too thin. lat/lon = town centre.
export const CANDIDATES = [
  ['Marietta, Ohio', 39.4154, -81.4548], ['McConnelsville, Ohio', 39.6487, -81.8532], ['Zanesville, Ohio', 39.9403, -82.0132],
  ['Athens, Ohio', 39.3292, -82.1013], ['Granville, Ohio', 40.0681, -82.5196], ['Pomeroy, Ohio', 39.0273, -82.0335],
  ['Gallipolis, Ohio', 38.8098, -82.2024], ['Coshocton, Ohio', 40.2720, -81.8596], ['Madison, Indiana', 38.7359, -85.3800],
  ['Point Pleasant, West Virginia', 38.8445, -82.1371], ['Harpers Ferry, West Virginia', 39.3254, -77.7389],
  ['Lexington, Virginia', 37.7840, -79.4428], ['Staunton, Virginia', 38.1496, -79.0717], ['Lititz, Pennsylvania', 40.1573, -76.3069],
  ['New Hope, Pennsylvania', 40.3643, -74.9513], ['Jim Thorpe, Pennsylvania', 40.8759, -75.7324], ['Cape May, New Jersey', 38.9351, -74.9060],
  ['Nantucket, Massachusetts', 41.2835, -70.0995], ['Provincetown, Massachusetts', 42.0584, -70.1786], ['Stowe, Vermont', 44.4654, -72.6874],
  ['Hanover, New Hampshire', 43.7022, -72.2896], ['Stillwater, Minnesota', 45.0564, -92.8060], ['Red Wing, Minnesota', 44.5625, -92.5338],
  ['Fredericksburg, Texas', 30.2752, -98.8720], ['Telluride, Colorado', 37.9375, -107.8123], ['Park City, Utah', 40.6461, -111.4980],
  ['Jackson, Wyoming', 43.4799, -110.7624], ['Hood River, Oregon', 45.7054, -121.5215], ['Port Townsend, Washington', 48.1170, -122.7604],
  ['Niagara-on-the-Lake, Ontario', 43.2550, -79.0773], ['Perth, Ontario', 44.8998, -76.2489], ['Elora, Ontario', 43.6845, -80.4296],
  ['Wolfville, Nova Scotia', 45.0917, -64.3600], ['Rothenburg ob der Tauber, Germany', 49.3772, 10.1866],
  ['Dinkelsbuehl, Germany', 49.0697, 10.3197], ['Monschau, Germany', 50.5547, 6.2398],
].map(([name, lat, lon]) => ({ name, lat, lon }));

// Chosen after the screening of 2026-10-19 (artifacts/osm/screen-tags.json, counts within 1.5 km); the reasons are
// also in docs/research/08b-storeys-and-roofs.md. Counts: buildings / with building:levels / with roof:shape / with height.
export const CHOSEN = [
  ['Marietta, Ohio', 39.4154, -81.4548, 'mouth of the Muskingum, 60 km downstream of Stockport; the densest US sample: 4,175 / 668 / 265 / 0'],
  ['Zanesville, Ohio', 39.9403, -82.0132, 'Muskingum river town upstream of Stockport: 563 / 317 / 0 / 0'],
  ['Coshocton, Ohio', 40.2720, -81.8596, 'head of the Muskingum: 349 / 68 / 0 / 0'],
  ['Athens, Ohio', 39.3292, -82.1013, 'southeast Ohio hill town 40 km from Stockport: 3,355 / 83 / 0 / 7'],
  ['Staunton, Virginia', 38.1496, -79.0717, 'Appalachian valley town with a 19th-century downtown: 2,551 / 196 / 0 / 1'],
  ['Jim Thorpe, Pennsylvania', 40.8759, -75.7324, 'river town in steep hills, levels and roof shapes both mapped: 957 / 27 / 31 / 0'],
  ['New Hope, Pennsylvania', 40.3643, -74.9513, 'Delaware river town: 1,735 / 35 / 0 / 0 (and 12 building parts)'],
  ['Hanover, New Hampshire', 43.7022, -72.2896, 'New England village: 1,178 / 33 / 3 / 1'],
  ['Park City, Utah', 40.6461, -111.4980, 'mountain small town, the second-best US roof sample: 1,672 / 194 / 82 / 0'],
  ['Port Townsend, Washington', 48.1170, -122.7604, 'Victorian port town: 2,929 / 39 / 1 / 1'],
  ['Wolfville, Nova Scotia', 45.0917, -64.3600, 'Canadian small town with dense storeys: 931 / 321 / 2 / 0'],
  ['Elora, Ontario', 43.6845, -80.4296, 'Ontario river village: 1,288 / 22 / 1 / 0'],
  ['Rothenburg ob der Tauber, Germany', 49.3772, 10.1866, 'the densest 3D tagging found (roof shapes and storeys on ~60 % of buildings): 4,977 / 2,970 / 3,022 / 63; for roof shapes per building type, not for North American proportions'],
  ['Dinkelsbuehl, Germany', 49.0697, 10.3197, 'heights, roof shapes and 127 building parts: 4,349 / 307 / 344 / 137; the main source of roof pitch and eave height'],
].map(([name, lat, lon, why]) => ({ name, lat, lon, why }));

export const slug = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function screenQuery(list) {
  const parts = list.map((t) => {
    const a = `(around:${R_SCREEN},${t.lat},${t.lon})`;
    return `way["building"]${a}->.b;\n.b out count;\nway.b["building:levels"];\nout count;\nway.b["roof:shape"];\nout count;\nway.b["height"];\nout count;\nway["building:part"]${a};\nout count;`;
  });
  return `[out:json][timeout:240];\n${parts.join('\n')}`;
}

export function townQuery(t) {
  const a = `(around:${R},${t.lat},${t.lon})`;
  return `[out:json][timeout:240];
(
  way["building"]${a};
  relation["building"]${a};
  way["building:part"]${a};
  relation["building:part"]${a};
  way["highway"~"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service)(_link)?$"]${a};
  way["landuse"]${a};
  way["amenity"]${a};
  node["shop"]${a};
  node["amenity"]${a};
  node["craft"]${a};
  node["office"]${a};
  node["tourism"]${a};
  node["entrance"]${a};
);
out body geom;`;
}

async function overpass(q) {
  for (let attempt = 0; attempt < 2; attempt++) {
    for (const host of HOSTS) {
      try {
        const r = await fetch(host, { method: 'POST', headers: { 'User-Agent': UA, 'Content-Type': 'application/x-www-form-urlencoded' }, body: 'data=' + encodeURIComponent(q) });
        if (r.ok) return await r.json();
        console.warn(`  ${host} -> HTTP ${r.status}`);
      } catch (e) { console.warn(`  ${host} -> ${e.message}`); }
      await sleep(15000);
    }
    await sleep(60000);
  }
  throw new Error('overpass failed');
}

async function screen() {
  const data = await overpass(screenQuery(CANDIDATES));
  const counts = data.elements.filter((e) => e.type === 'count').map((e) => +e.tags.ways || 0);
  const rows = CANDIDATES.map((t, i) => {
    const [b, lv, roof, h, parts] = counts.slice(i * 5, i * 5 + 5);
    return { ...t, buildings: b, levels: lv, roofShape: roof, height: h, parts, levelsShare: b ? +(lv / b).toFixed(3) : 0, roofShare: b ? +(roof / b).toFixed(3) : 0 };
  });
  fs.mkdirSync(path.dirname(SCREEN), { recursive: true });
  fs.writeFileSync(SCREEN, JSON.stringify({ fetched: new Date().toISOString(), radius: R_SCREEN, credit: '(c) OpenStreetMap contributors, ODbL', rows }, null, 1));
  for (const r of [...rows].sort((a, b) => b.roofShape + b.levels - (a.roofShape + a.levels))) console.log(`${r.name.padEnd(36)} bld ${String(r.buildings).padStart(5)}  levels ${String(r.levels).padStart(5)}  roof ${String(r.roofShape).padStart(5)}  height ${String(r.height).padStart(5)}  parts ${String(r.parts).padStart(4)}`);
}

async function main() {
  if (process.argv.includes('--screen')) return screen();
  if (process.argv.includes('--list')) { for (const t of CHOSEN) console.log(`${t.name}  ${t.lat}, ${t.lon}  ${t.why}`); if (CHOSEN[0]) console.log(townQuery(CHOSEN[0])); return; }
  fs.mkdirSync(CACHE, { recursive: true });
  for (const t of CHOSEN) {
    const file = path.join(CACHE, slug(t.name) + '.json');
    if (fs.existsSync(file)) { console.log(`${t.name}: cached`); continue; }
    try {
      const t0 = Date.now();
      console.log(`${t.name}: downloading`);
      const data = await overpass(townQuery(t));
      fs.writeFileSync(file, JSON.stringify({ name: t.name, centre: { lat: t.lat, lon: t.lon }, near: R, far: 0, set: 'tags', why: t.why,
        fetched: new Date().toISOString(), osm3s: data.osm3s || null, credit: '(c) OpenStreetMap contributors, ODbL', elements: data.elements }));
      console.log(`${t.name}: ${data.elements.length} elements, ${(fs.statSync(file).size / 1e6).toFixed(1)} MB, ${((Date.now() - t0) / 1000).toFixed(0)} s`);
    } catch (e) { console.warn(`${t.name}: skipped (${e.message})`); }
    await sleep(10000);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
