// Downloads OpenStreetMap data for real North American small towns, for measure.mjs (street and site study).
//
//   node tools/osm/fetch.mjs            (downloads only towns that are not cached yet)
//   node tools/osm/fetch.mjs --list     (prints the towns and the query, downloads nothing)
//   node tools/osm/fetch.mjs --light    (retry towns the server refused: FAR 3,500 m and no roads around far features)
//
// One Overpass API request per town (approval U23: a single download, credited). The raw answers are
// cached under artifacts/osm/raw/ (gitignored) and never fetched again; measure.mjs reads only the cache.
// No geocoding service is called: the town centres are fixed below (the first ten are the centres the
// browser game's town study used, D:\Backroads\tools\osm\fetch.mjs).
//
// What one request asks for, around each centre:
//   within NEAR m: every highway way (streets, service roads, driveways, parking aisles, alleys), every
//     building (ways and multipolygons, with all their tags: levels, height, roof shape...), parking,
//     fuel and other amenity areas, land use, barriers (fences, walls, gates), man_made features,
//     area:highway, marinas, railways; and the nodes that matter on sites: shops and amenities mapped as
//     points, entrances and doors (loading docks), parking entrances and spaces, lamps, barriers, pumps.
//   within FAR m: farmyards, quarries, industrial land, works, sawmills, harbours, marinas, piers, fuel
//     stations; and everything of interest inside those areas (buildings, tracks, fences, docks).
//   around every fuel station within FAR m (also the ones mapped only as a point): nearby buildings
//     (canopies, shops), service ways and amenities.
//
// OpenStreetMap data is (c) OpenStreetMap contributors, ODbL 1.0 (https://www.openstreetmap.org/copyright).
// Overpass is a free public service: one request at a time, pauses between towns, a descriptive User-Agent.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const CACHE = path.join(ROOT, 'artifacts', 'osm', 'raw');
const NEAR = 1600; // metres: the town itself (same radius as the browser game's study)
const LIGHT = process.argv.includes('--light');
const FAR = LIGHT ? 3500 : 6000; // metres: farms, quarries, mills, harbours and highway fuel stations around it
const UA = 'Backroads-site-study/1.0 (hobby game; one-off fetch; contact via the OSM credit in the game)';
const HOSTS = ['https://overpass-api.de/api/interpreter', 'https://overpass.kumi.systems/api/interpreter'];

// Small towns (roughly 1,000 to 15,000 people) across the US and Canada. lat/lon = town centre.
export const TOWNS = [
  // the browser game's ten (centres as its Nominatim lookup returned them, 2026-10-18)
  { name: 'Viroqua, Wisconsin', lat: 43.5569174, lon: -90.8887413 },
  { name: 'Hillsboro, Kansas', lat: 38.3533075, lon: -97.2047316 },
  { name: 'Woodstock, Vermont', lat: 43.6247646, lon: -72.5189791 },
  { name: 'Marfa, Texas', lat: 30.3117621, lon: -104.023146 },
  { name: 'Wallace, Idaho', lat: 47.4716008, lon: -115.923925 },
  { name: 'Cle Elum, Washington', lat: 47.1952417, lon: -120.940063 },
  { name: 'Mount Forest, Ontario', lat: 43.9806979, lon: -80.7338237 },
  { name: 'Nevada City, California', lat: 39.2625993, lon: -121.0187304 },
  { name: 'Decorah, Iowa', lat: 43.3041609, lon: -91.7859098 },
  { name: 'Bellefontaine, Ohio', lat: 40.3611643, lon: -83.7596557 },
  // added for this study: harbours, quarries, mills and more highway-town fuel stations
  { name: 'Galena, Illinois', lat: 42.4167, lon: -90.4290 },
  { name: 'Ely, Minnesota', lat: 47.9032, lon: -91.8671 },
  { name: 'Lander, Wyoming', lat: 42.8330, lon: -108.7307 },
  { name: 'Salida, Colorado', lat: 38.5347, lon: -105.9989 },
  { name: 'Barre, Vermont', lat: 44.1970, lon: -72.5020 },
  { name: 'Bar Harbor, Maine', lat: 44.3876, lon: -68.2039 },
  { name: 'Lunenburg, Nova Scotia', lat: 44.3770, lon: -64.3090 },
];

export const slug = (s) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function query(lat, lon) {
  const n = `(around:${NEAR},${lat},${lon})`, f = `(around:${FAR},${lat},${lon})`;
  return `[out:json][timeout:240];
(
  way["highway"]${n};
  way["building"]${n};
  relation["building"]${n};
  way["amenity"]${n};
  relation["amenity"="parking"]${n};
  way["landuse"]${n};
  way["barrier"]${n};
  way["man_made"]${n};
  way["area:highway"]${n};
  way["leisure"~"^(marina|slipway|park)$"]${n};
  way["railway"~"^(rail|platform)$"]${n};
  way["waterway"="dock"]${n};
  node["shop"]${n};
  node["amenity"]${n};
  node["craft"]${n};
  node["office"]${n};
  node["tourism"]${n};
  node["entrance"]${n};
  node["door"]${n};
  node["barrier"]${n};
  node["man_made"]${n};
  node["highway"~"^(street_lamp|stop|traffic_signals)$"]${n};
)->.near;
(
  way["landuse"~"^(farmyard|quarry|industrial|harbour|port)$"]${f};
  relation["landuse"~"^(farmyard|quarry|industrial|harbour|port)$"]${f};
  way["man_made"~"^(works|pier|quay|silo|storage_tank|bunker_silo|wastewater_plant)$"]${f};
  way["craft"="sawmill"]${f};
  way["industrial"]${f};
  way["leisure"="marina"]${f};
  way["amenity"="fuel"]${f};
)->.far;
.far map_to_area->.fa;
(
  way(area.fa)["building"];
  way(area.fa)["highway"];
  way(area.fa)["barrier"];
  way(area.fa)["man_made"];
  way(area.fa)["amenity"];
  way(area.fa)["landuse"];
  way(area.fa)["area:highway"];
  node(area.fa)["man_made"];
  node(area.fa)["door"];
  node(area.fa)["entrance"];
  node(area.fa)["amenity"];
)->.inner;
node["amenity"="fuel"]${f}->.fuelnodes;
(
  way(around.fuelnodes:70)["building"];
  way(around.fuelnodes:70)["highway"];
  way(around.fuelnodes:70)["amenity"];
  way(around.far:40)["building"];
${LIGHT ? '' : '  way(around.far:60)["highway"];\n'})->.fuelnear;
(.near; .far; .inner; .fuelnodes; .fuelnear;);
out body geom;`;
}

async function overpass(q) {
  for (let attempt = 0; attempt < 2; attempt++) {
    for (const host of HOSTS) {
      try {
        const r = await fetch(host, {
          method: 'POST',
          headers: { 'User-Agent': UA, 'Content-Type': 'application/x-www-form-urlencoded' },
          body: 'data=' + encodeURIComponent(q),
        });
        if (r.ok) return await r.json();
        console.warn(`  ${host} -> HTTP ${r.status}`);
      } catch (e) { console.warn(`  ${host} -> ${e.message}`); }
      await sleep(15000);
    }
    await sleep(60000);
  }
  throw new Error('overpass failed');
}

async function main() {
  if (process.argv.includes('--list')) {
    for (const t of TOWNS) console.log(`${t.name}  ${t.lat}, ${t.lon}`);
    console.log(query(TOWNS[0].lat, TOWNS[0].lon));
    return;
  }
  fs.mkdirSync(CACHE, { recursive: true });
  for (const t of TOWNS) {
    const file = path.join(CACHE, slug(t.name) + '.json');
    if (fs.existsSync(file)) { console.log(`${t.name}: cached`); continue; }
    try {
      console.log(`${t.name}: downloading around ${t.lat.toFixed(4)}, ${t.lon.toFixed(4)}`);
      const t0 = Date.now();
      const data = await overpass(query(t.lat, t.lon));
      const out = { name: t.name, centre: { lat: t.lat, lon: t.lon }, near: NEAR, far: FAR, light: LIGHT,
        fetched: new Date().toISOString(), osm3s: data.osm3s || null,
        credit: '(c) OpenStreetMap contributors, ODbL', elements: data.elements };
      fs.writeFileSync(file, JSON.stringify(out));
      console.log(`${t.name}: ${data.elements.length} elements, ${(fs.statSync(file).size / 1e6).toFixed(1)} MB, ${((Date.now() - t0) / 1000).toFixed(0)} s`);
    } catch (e) {
      console.warn(`${t.name}: skipped (${e.message})`);
    }
    await sleep(10000);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
