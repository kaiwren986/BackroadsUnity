#!/usr/bin/env node
// Downloads the source data of one town for the world bake (card T3.0, ADR-006 §2; approvals U23/U25):
//   1. ONE Overpass query: every tagged node, every way and every multipolygon (and building) relation in the extract
//      square (the 2,048 m play area + 300 m on every side), with full geometry. Data (c) OpenStreetMap contributors,
//      ODbL 1.0. Main server first; a busy server (429/502/503/504) is retried with backoff (20 s, doubling, 5 tries),
//      then the next server (tools/scout/fetch-osm.mjs, tools/osm/fetch.mjs).
//   2. The USGS 3D Elevation Program DEM (public domain) for the same square at 1 m, through the 3DEPElevation
//      ImageServer exportImage endpoint as raw F32 (tools/scout/fetch-dem.mjs), in the frame's own UTM zone, pixel
//      centres on whole metres of the local frame. The request is tiled when the server caps the image size (from its
//      service description) and the tiles overlap by OVERLAP px so the join can be checked. Plus the 3DEP catalog
//      query over the square (which lidar project covers it).
// Everything is cached under artifacts/world-src/<town>/ (gitignored) and pinned by SHA-256 in sources.json there;
// extract-town.mjs copies the pins into TownData/<town>/v1/sources.json. Cached files are never fetched again unless
// --force. Zero dependencies.
//
//   node tools/world/fetch-town.mjs <town-id> [--osm-only | --dem-only] [--force] [--endpoint URL] [--dry-run]
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { townFrame, srcDir, sha256, writeFile, UA, sleep, EXTRACT_HALF, DEM_N, DEM_RES } from './town-lib.mjs';
import { mosaicDem } from './dem-lib.mjs';

const args = process.argv.slice(2);
const flag = (f) => args.includes(f);
const opt = (f, d = null) => { const i = args.indexOf(f); return i >= 0 && i + 1 < args.length ? args[i + 1] : d; };
const id = args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--endpoint');
if (!id && process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) { console.error('usage: node tools/world/fetch-town.mjs <town-id> [--osm-only|--dem-only] [--force] [--dry-run]'); process.exit(1); }

const ENDPOINTS = opt('--endpoint') ? [opt('--endpoint')] : [
  'https://overpass-api.de/api/interpreter',
  'https://overpass.kumi.systems/api/interpreter',
  'https://maps.mail.ru/osm/tools/overpass/api/interpreter',
];
const SVC = 'https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer';
const OVERLAP = 16;      // px shared by neighbouring DEM tiles (the seam check compares them)
const TILE_MAX = 1024;   // px; never ask for more than this per request even if the server would allow it

const F = townFrame(id);
const dir = srcDir(id);
const dry = flag('--dry-run');

export function overpassQuery(f) {
  const q = f.query;
  const bb = [q.south, q.west, q.north, q.east].map((v) => v.toFixed(6)).join(',');
  const [plat, plon] = f.latlon(0, 0).map((v) => v.toFixed(6));
  // No global [bbox]: with one, Overpass clips "out geom" to it and relation members lose their outside parts.
  return `[out:json][timeout:300][maxsize:536870912];
(
  node(if:count_tags()>0)(${bb});
  way(${bb});
  rel[type=multipolygon](${bb});
  rel[building](${bb});
)->.box;
is_in(${plat},${plon})->.a;
(way(pivot.a); rel(pivot.a)[type=multipolygon];)->.around;
(.box; .around;);
out geom qt;`;
}

async function fetchOsm() {
  const out = path.join(dir, 'osm.json'), metaFile = path.join(dir, 'osm.meta.json');
  if (fs.existsSync(out) && fs.existsSync(metaFile) && !flag('--force')) { console.log(`osm: cached (${out})`); return JSON.parse(fs.readFileSync(metaFile, 'utf8')); }
  const query = overpassQuery(F);
  if (dry) { console.log('osm: would POST to ' + ENDPOINTS[0] + '\n' + query); return null; }
  for (const ep of ENDPOINTS) {
    let wait = 20_000;
    for (let attempt = 1; attempt <= 5; attempt++) {
      const t0 = Date.now();
      let status = 0, buf = Buffer.alloc(0), err = '';
      try {
        const res = await fetch(ep, {
          method: 'POST', headers: { 'User-Agent': UA, 'Content-Type': 'application/x-www-form-urlencoded' },
          body: 'data=' + encodeURIComponent(query), signal: AbortSignal.timeout(420_000),
        });
        status = res.status;
        buf = Buffer.from(await res.arrayBuffer());
      } catch (e) { err = String(e); }
      const secs = ((Date.now() - t0) / 1000).toFixed(1);
      let json = null;
      if (status === 200) { try { json = JSON.parse(buf.toString('utf8')); } catch { json = null; } }
      if (json && Array.isArray(json.elements) && !String(json.remark ?? '').includes('runtime error')) {
        writeFile(out, buf);
        const counts = { node: 0, way: 0, relation: 0 };
        for (const e of json.elements) counts[e.type]++;
        const meta = {
          endpoint: ep, method: 'POST', fetchedAt: new Date().toISOString(), seconds: Number(secs), bytes: buf.length, sha256: sha256(buf),
          osmBase: json.osm3s?.timestamp_osm_base ?? null, generator: json.generator ?? null, counts, query,
          bbox: F.query, licence: '(c) OpenStreetMap contributors, ODbL 1.0', remark: json.remark ?? null,
        };
        writeFile(metaFile, JSON.stringify(meta, null, 2) + '\n');
        console.log(`osm: ${(buf.length / 1e6).toFixed(2)} MB in ${secs} s from ${new URL(ep).host}: ${counts.node} nodes, ${counts.way} ways, ${counts.relation} relations (OSM base ${meta.osmBase})`);
        return meta;
      }
      const why = json?.remark ?? (err || buf.toString('utf8').slice(0, 160).replace(/\s+/g, ' '));
      console.log(`osm: ${new URL(ep).host} try ${attempt}: HTTP ${status} after ${secs} s (${why})`);
      if (![0, 429, 502, 503, 504].includes(status)) break; // not a busy-server answer: next server
      await sleep(wait);
      wait *= 2;
    }
  }
  throw new Error('osm: every Overpass server failed');
}

async function get(url, what) {
  for (let attempt = 1, wait = 10_000; ; attempt++, wait *= 2) {
    const t0 = Date.now();
    try {
      const res = await fetch(url, { headers: { 'User-Agent': UA }, signal: AbortSignal.timeout(240_000) });
      const buf = Buffer.from(await res.arrayBuffer());
      if (res.ok) return { buf, seconds: +((Date.now() - t0) / 1000).toFixed(1), contentType: res.headers.get('content-type') };
      console.log(`${what}: HTTP ${res.status} (try ${attempt})`);
      if (attempt >= 4 || ![429, 500, 502, 503, 504].includes(res.status)) throw new Error(`HTTP ${res.status} for ${url}`);
    } catch (e) {
      console.log(`${what}: ${e.message ?? e} (try ${attempt})`);
      if (attempt >= 4) throw e;
    }
    await sleep(wait);
  }
}

async function cachedGet(file, url, what) {
  const metaFile = file + '.meta.json';
  if (fs.existsSync(file) && fs.existsSync(metaFile) && !flag('--force')) return { ...JSON.parse(fs.readFileSync(metaFile, 'utf8')), cached: true };
  if (dry) { console.log(`${what}: would GET ${url}`); return null; }
  const r = await get(url, what);
  writeFile(file, r.buf);
  const meta = { url, fetchedAt: new Date().toISOString(), seconds: r.seconds, bytes: r.buf.length, sha256: sha256(r.buf), contentType: r.contentType };
  writeFile(metaFile, JSON.stringify(meta, null, 2) + '\n');
  console.log(`${what}: ${r.buf.length} bytes in ${r.seconds} s`);
  return meta;
}

// The tile plan: pixel (c, r) of the whole grid has its centre at local x = -EXTRACT_HALF + c, z = EXTRACT_HALF - r
// (rows from the north, as the server returns them).
export function tilePlan(maxW, maxH) {
  const T = Math.min(TILE_MAX, maxW, maxH);
  const starts = (n) => { const s = [0]; while (s[s.length - 1] + T < n) s.push(s[s.length - 1] + T - OVERLAP); return s; };
  const cs = starts(DEM_N), rs = starts(DEM_N);
  const tiles = [];
  rs.forEach((r0, ri) => cs.forEach((c0, ci) => {
    const w = Math.min(T, DEM_N - c0), h = Math.min(T, DEM_N - r0);
    tiles.push({ name: `r${ri}c${ci}`, r0, c0, w, h });
  }));
  return { tileSize: T, overlap: OVERLAP, tiles };
}

function exportUrl(t) {
  const half = EXTRACT_HALF + DEM_RES / 2;
  const xmin = F.E0 - half + t.c0 * DEM_RES, ymax = F.N0 + half - t.r0 * DEM_RES;
  const q = new URLSearchParams({
    bbox: [xmin, ymax - t.h * DEM_RES, xmin + t.w * DEM_RES, ymax].join(','), bboxSR: String(F.epsg), imageSR: String(F.epsg),
    size: `${t.w},${t.h}`, format: 'bsq', pixelType: 'F32', interpolation: 'RSP_BilinearInterpolation', f: 'image',
  });
  return `${SVC}/exportImage?${q}`;
}

async function fetchDem() {
  const dd = path.join(dir, 'dem');
  const service = await cachedGet(path.join(dd, 'service.json'), `${SVC}?f=json`, 'dem service');
  let maxW = 4000, maxH = 4000;
  if (service) {
    const s = JSON.parse(fs.readFileSync(path.join(dd, 'service.json'), 'utf8'));
    maxW = s.maxImageWidth ?? maxW; maxH = s.maxImageHeight ?? maxH;
    console.log(`dem service: maxImageWidth ${s.maxImageWidth}, maxImageHeight ${s.maxImageHeight}, pixel ${s.pixelSizeX} (${s.spatialReference?.latestWkid ?? s.spatialReference?.wkid})`);
  }
  const env = { xmin: F.E0 - EXTRACT_HALF, ymin: F.N0 - EXTRACT_HALF, xmax: F.E0 + EXTRACT_HALF, ymax: F.N0 + EXTRACT_HALF, spatialReference: { wkid: F.epsg } };
  const cq = new URLSearchParams({
    where: "ProductName='USGS_3DEP'", geometry: JSON.stringify(env), geometryType: 'esriGeometryEnvelope', inSR: String(F.epsg),
    spatialRel: 'esriSpatialRelIntersects', outFields: 'OBJECTID,Name,title,LowPS,AcquisitionDate', returnGeometry: 'true', outSR: String(F.epsg), f: 'json',
  });
  const coverage = await cachedGet(path.join(dd, 'coverage.json'), `${SVC}/query?${cq}`, 'dem catalog');
  const plan = tilePlan(maxW, maxH);
  console.log(`dem: ${DEM_N} x ${DEM_N} samples at ${DEM_RES} m in ${plan.tiles.length} tiles of up to ${plan.tileSize} px (overlap ${plan.overlap})`);
  const tiles = [];
  for (const t of plan.tiles) {
    const meta = await cachedGet(path.join(dd, `tile-${t.name}.bin`), exportUrl(t), `dem tile ${t.name}`);
    tiles.push({ ...t, file: `dem/tile-${t.name}.bin`, ...(meta ?? {}) });
    if (meta && !meta.cached) await sleep(1000); // be polite between requests
  }
  return { service, coverage, plan, tiles };
}

const pin = ({ cached, ...rest }) => rest;

async function main() {
  fs.mkdirSync(dir, { recursive: true });
  console.log(`${id}: UTM zone ${F.zone} (EPSG:${F.epsg}), origin E ${F.E0} N ${F.N0} (${F.originLat.toFixed(6)}, ${F.originLon.toFixed(6)}); extract +-${EXTRACT_HALF} m`);
  const osm = flag('--dem-only') ? null : await fetchOsm();
  const dem = flag('--osm-only') ? null : await fetchDem();
  if (dry) return;
  const prev = fs.existsSync(path.join(dir, 'sources.json')) ? JSON.parse(fs.readFileSync(path.join(dir, 'sources.json'), 'utf8')) : {};
  const sources = {
    town: id, frame: { epsg: F.epsg, zone: F.zone, E0: F.E0, N0: F.N0, extractHalf: EXTRACT_HALF, placeNode: F.placeNode },
    osm: osm ? { file: 'osm.json', ...osm } : prev.osm,
    dem: dem ? {
      source: 'USGS 3D Elevation Program (3DEP), 3DEPElevation ImageServer, best-available mosaic (public domain)',
      resolution: DEM_RES, samples: DEM_N, interpolation: 'RSP_BilinearInterpolation', pixelType: 'F32', format: 'bsq',
      service: dem.service && { file: 'dem/service.json', ...pin(dem.service) }, catalog: dem.coverage && { file: 'dem/coverage.json', ...pin(dem.coverage) },
      tileSize: dem.plan.tileSize, overlap: dem.plan.overlap,
      tiles: dem.tiles.map((t) => ({ name: t.name, file: t.file, r0: t.r0, c0: t.c0, w: t.w, h: t.h, url: t.url, fetchedAt: t.fetchedAt, bytes: t.bytes, sha256: t.sha256 })),
    } : prev.dem,
  };
  writeFile(path.join(dir, 'sources.json'), JSON.stringify(sources, null, 2) + '\n');
  if (sources.dem?.tiles?.length) {
    const m = mosaicDem(dir, sources.dem);
    console.log(`dem: mosaic ${m.n} x ${m.n}; seams: ${m.seams.pairs} overlaps, ${m.seams.compared} px compared, max |diff| ${m.seams.maxAbs} m, ${m.seams.differing} px differ; ` +
      `nodata ${m.nodata}; elevation ${m.min.toFixed(2)} .. ${m.max.toFixed(2)} m`);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main().catch((e) => { console.error(e.stack ?? String(e)); process.exit(1); });
