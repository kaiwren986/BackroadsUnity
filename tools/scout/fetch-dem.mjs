#!/usr/bin/env node
// Downloads a coarse elevation raster of one candidate town from the USGS 3D Elevation Program
// (public domain) and records which 3DEP products cover it (1 m lidar DEM, 1/9", 1/3", 1").
// Zero dependencies.
//
//   node tools/scout/fetch-dem.mjs <town-id> [...] [--all] [--size 4000] [--res 5] [--force]
//
// Output under artifacts/scout/<town>/ (gitignored):
//   dem.f32       float32 little-endian, row-major, first row = north edge, NaN = no data
//   dem.json      grid: UTM EPSG, SW corner (E0, N0), resolution, width, height, min, max
//   coverage.json the 3DEP catalog items over the frame, and the share of the frame (and of the
//                 centre 1 km and 2 km squares) covered by each resolution class
//
// exportImage with format=bsq, pixelType=F32 returns width*height raw floats followed by a one-bit-
// per-pixel validity mask; the bbox is given in the frame's own UTM zone so pixels are square metres.
import fs from 'node:fs';
import path from 'node:path';
import { getTown, townFrame, townDir, townIds, arg, flag, UA, sleep } from './geo.mjs';

const SVC = 'https://elevation.nationalmap.gov/arcgis/rest/services/3DEPElevation/ImageServer';

async function get(url, binary) {
  for (let attempt = 1, wait = 10_000; ; attempt++, wait *= 2) {
    try {
      const res = await fetch(url, { headers: { 'User-Agent': UA }, signal: AbortSignal.timeout(180_000) });
      if (res.ok) return binary ? Buffer.from(await res.arrayBuffer()) : await res.json();
      if (attempt >= 4 || ![429, 500, 502, 503, 504].includes(res.status)) throw new Error(`HTTP ${res.status} for ${url}`);
    } catch (e) {
      if (attempt >= 4) throw e;
    }
    await sleep(wait);
  }
}

// Resolution class of a 3DEP catalog item from its title / pixel size.
function resClass(item) {
  const t = (item.title ?? '').toLowerCase();
  if (item.LowPS <= 1.5 || t.includes('1 meter') || t.includes('one meter')) return '1m';
  if (t.includes('1/9')) return '1/9"';
  if (t.includes('1/3')) return '1/3"';
  if (t.includes('1 arc')) return '1"';
  return 'other';
}

function pointInRings(x, y, rings) {
  let inside = false;
  for (const r of rings) {
    for (let i = 0, j = r.length - 1; i < r.length; j = i++) {
      const [xi, yi] = r[i], [xj, yj] = r[j];
      if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
    }
  }
  return inside;
}

async function coverage(f) {
  const env = { xmin: f.E0, ymin: f.N0, xmax: f.E0 + f.size, ymax: f.N0 + f.size, spatialReference: { wkid: f.epsg } };
  const q = new URLSearchParams({
    where: "ProductName='USGS_3DEP'", geometry: JSON.stringify(env), geometryType: 'esriGeometryEnvelope', inSR: String(f.epsg),
    spatialRel: 'esriSpatialRelIntersects', outFields: 'OBJECTID,Name,title,LowPS,AcquisitionDate', returnGeometry: 'true',
    outSR: String(f.epsg), f: 'json',
  });
  const j = await get(`${SVC}/query?${q}`, false);
  const items = (j.features ?? []).map((ft) => ({
    title: ft.attributes.title, name: ft.attributes.Name, lowPS: ft.attributes.LowPS, cls: resClass(ft.attributes),
    acquired: ft.attributes.AcquisitionDate ? new Date(ft.attributes.AcquisitionDate).toISOString().slice(0, 10) : null,
    rings: (ft.geometry?.rings ?? []).map((r) => r.map(([e, n]) => [e - f.E0, n - f.N0])),
  }));
  // Share of each square covered by each class, sampled on a 40 x 40 grid.
  const squares = { frame: f.size, '2km': 2000, '1km': 1000 };
  const share = {};
  for (const [name, side] of Object.entries(squares)) {
    share[name] = {};
    const o = (f.size - side) / 2;
    for (const cls of ['1m', '1/9"', '1/3"', '1"']) {
      const its = items.filter((it) => it.cls === cls);
      let hit = 0;
      for (let a = 0; a < 40; a++) for (let b = 0; b < 40; b++) {
        const x = o + (a + 0.5) * side / 40, y = o + (b + 0.5) * side / 40;
        if (its.some((it) => pointInRings(x, y, it.rings))) hit++;
      }
      share[name][cls] = Math.round((hit / 1600) * 1000) / 10;
    }
  }
  const projects = [...new Map(items.filter((i) => i.cls === '1m').map((i) => [i.name, { name: i.name, acquired: i.acquired }])).values()];
  return { share, oneMetreProjects: projects, items: items.map(({ rings, ...rest }) => rest) };
}

async function fetchTown(id) {
  const town = getTown(id);
  const size = Number(arg('--size', town.frameSize)), res = Number(arg('--res', 5));
  const f = townFrame(town, size);
  const dir = townDir(id);
  if (fs.existsSync(path.join(dir, 'dem.f32')) && !flag('--force')) { console.log(`${id}: cached`); return false; }
  const W = Math.round(size / res), H = W;
  const q = new URLSearchParams({
    bbox: [f.E0, f.N0, f.E0 + size, f.N0 + size].join(','), bboxSR: String(f.epsg), imageSR: String(f.epsg),
    size: `${W},${H}`, format: 'bsq', pixelType: 'F32', interpolation: 'RSP_BilinearInterpolation', f: 'image',
  });
  const buf = await get(`${SVC}/exportImage?${q}`, true);
  const want = W * H * 4, maskLen = Math.ceil((W * H) / 8);
  if (buf.length !== want + maskLen && buf.length !== want) throw new Error(`${id}: DEM is ${buf.length} bytes, expected ${want} (+${maskLen} mask)`);
  const dem = new Float32Array(W * H);
  let lo = Infinity, hi = -Infinity, nodata = 0;
  for (let i = 0; i < W * H; i++) {
    const valid = buf.length === want ? true : (buf[want + (i >> 3)] >> (7 - (i & 7))) & 1;
    let v = buf.readFloatLE(i * 4);
    if (!valid || !Number.isFinite(v) || v < -500 || v > 9000) { v = NaN; nodata++; } else { if (v < lo) lo = v; if (v > hi) hi = v; }
    dem[i] = v;
  }
  fs.writeFileSync(path.join(dir, 'dem.f32'), Buffer.from(dem.buffer));
  const cov = await coverage(f);
  const meta = {
    town: id, source: 'USGS 3D Elevation Program (3DEP), 3DEPElevation ImageServer, best available mosaic (public domain)',
    fetchedAt: new Date().toISOString(), epsg: f.epsg, E0: f.E0, N0: f.N0, size, res, width: W, height: H,
    rowOrder: 'north to south', min: lo, max: hi, nodata, url: `${SVC}/exportImage?${q}`,
  };
  fs.writeFileSync(path.join(dir, 'dem.json'), JSON.stringify(meta, null, 2) + '\n');
  fs.writeFileSync(path.join(dir, 'coverage.json'), JSON.stringify(cov, null, 2) + '\n');
  const s = cov.share;
  console.log(`${id}: ${W}x${H} at ${res} m, ${lo.toFixed(1)}..${hi.toFixed(1)} m, nodata ${nodata}; 1 m lidar DEM covers frame ${s.frame['1m']} %, 1 km ${s['1km']['1m']} %; 1/3" ${s.frame['1/3"']} %`);
  return true;
}

const ids = townIds();
if (!ids.length) { console.error('usage: node tools/scout/fetch-dem.mjs <town-id> ... | --all'); process.exit(1); }
let failed = 0;
for (const id of ids) {
  try { if (await fetchTown(id)) await sleep(2_000); } catch (e) { failed++; console.error(String(e.message ?? e)); }
}
process.exit(failed ? 1 : 0);
