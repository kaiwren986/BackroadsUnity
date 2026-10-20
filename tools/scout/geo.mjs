// UTM on WGS84 (Krueger series, good to a few mm inside a zone) and the scout's square frames.
// Zero dependencies. Used by fetch-osm.mjs, fetch-dem.mjs and render-map.mjs.
//
// A town's frame is a square of side S metres in UTM, centred on the town's centre rounded to 10 m.
// Local coordinates: x = easting - E0, y = northing - N0 (metres, y north), with (E0, N0) the square's
// south-west corner. The DEM is fetched in the same UTM zone, so DEM pixels and OSM vectors line up.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const HERE = path.dirname(fileURLToPath(import.meta.url));
export const REPO = path.resolve(HERE, '..', '..');

const A_WGS = 6378137, F_WGS = 1 / 298.257223563, K0 = 0.9996;
const N_ = F_WGS / (2 - F_WGS);
const n2 = N_ * N_, n3 = n2 * N_;
const AA = A_WGS / (1 + N_) * (1 + n2 / 4 + n2 * n2 / 64);
const ALPHA = [N_ / 2 - 2 * n2 / 3 + 5 * n3 / 16, 13 * n2 / 48 - 3 * n3 / 5, 61 * n3 / 240];
const BETA = [N_ / 2 - 2 * n2 / 3 + 37 * n3 / 96, n2 / 48 + n3 / 15, 17 * n3 / 480];
const DELTA = [2 * N_ - 2 * n2 / 3 - 2 * n3, 7 * n2 / 3 - 8 * n3 / 5, 56 * n3 / 15];
const E2N = 2 * Math.sqrt(N_) / (1 + N_);
const D2R = Math.PI / 180;

export const utmZone = (lon) => Math.floor((lon + 180) / 6) + 1;
export const utmEpsg = (zone, north = true) => (north ? 32600 : 32700) + zone;
const lon0 = (zone) => (zone - 1) * 6 - 180 + 3;

export function toUtm(lat, lon, zone) {
  const phi = lat * D2R, lam = (lon - lon0(zone)) * D2R;
  const s = Math.sin(phi);
  const t = Math.sinh(Math.atanh(s) - E2N * Math.atanh(E2N * s));
  const xi = Math.atan2(t, Math.cos(lam)), eta = Math.atanh(Math.sin(lam) / Math.sqrt(1 + t * t));
  let E = eta, N = xi;
  for (let j = 1; j <= 3; j++) {
    E += ALPHA[j - 1] * Math.cos(2 * j * xi) * Math.sinh(2 * j * eta);
    N += ALPHA[j - 1] * Math.sin(2 * j * xi) * Math.cosh(2 * j * eta);
  }
  return [500000 + K0 * AA * E, K0 * AA * N];
}

export function fromUtm(E, N, zone) {
  const xi = N / (K0 * AA), eta = (E - 500000) / (K0 * AA);
  let xp = xi, ep = eta;
  for (let j = 1; j <= 3; j++) {
    xp -= BETA[j - 1] * Math.sin(2 * j * xi) * Math.cosh(2 * j * eta);
    ep -= BETA[j - 1] * Math.cos(2 * j * xi) * Math.sinh(2 * j * eta);
  }
  const chi = Math.asin(Math.sin(xp) / Math.cosh(ep));
  let phi = chi;
  for (let j = 1; j <= 3; j++) phi += DELTA[j - 1] * Math.sin(2 * j * chi);
  const lam = Math.atan2(Math.sinh(ep), Math.cos(xp));
  return [phi / D2R, lon0(zone) + lam / D2R];
}

export function loadTowns() {
  return JSON.parse(fs.readFileSync(path.join(HERE, 'towns.json'), 'utf8'));
}

export function getTown(id) {
  const cfg = loadTowns();
  const t = cfg.towns.find((x) => x.id === id);
  if (!t) throw new Error(`unknown town "${id}"; known: ${cfg.towns.map((x) => x.id).join(', ')}`);
  return { ...t, frameSize: t.frameSize ?? cfg.frameSize };
}

// The town's square frame: UTM zone, EPSG, SW corner, side, and the lat/lon box that contains it.
export function townFrame(town, size = town.frameSize) {
  const zone = utmZone(town.lon), epsg = utmEpsg(zone, town.lat >= 0);
  const [ec, nc] = toUtm(town.lat, town.lon, zone);
  const E0 = Math.round(ec / 10) * 10 - size / 2, N0 = Math.round(nc / 10) * 10 - size / 2;
  const corners = [[E0, N0], [E0 + size, N0], [E0, N0 + size], [E0 + size, N0 + size]].map(([e, n]) => fromUtm(e, n, zone));
  const lats = corners.map((c) => c[0]), lons = corners.map((c) => c[1]);
  const pad = 0.0005; // ~50 m, so ways that touch the square's edge come along
  return {
    zone, epsg, E0, N0, size,
    south: Math.min(...lats) - pad, north: Math.max(...lats) + pad,
    west: Math.min(...lons) - pad, east: Math.max(...lons) + pad,
    local: (lat, lon) => { const [e, n] = toUtm(lat, lon, zone); return [e - E0, n - N0]; },
    latlon: (x, y) => fromUtm(E0 + x, N0 + y, zone),
  };
}

export function townDir(id) {
  const d = path.join(REPO, 'artifacts', 'scout', id);
  fs.mkdirSync(d, { recursive: true });
  return d;
}

export function arg(name, def = null) {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : def;
}
export const flag = (name) => process.argv.includes(name);
const VALUE_FLAGS = new Set(['--size', '--res', '--endpoint', '--out']);
export function positional() {
  const out = [], a = process.argv.slice(2);
  for (let i = 0; i < a.length; i++) {
    if (VALUE_FLAGS.has(a[i])) i++;
    else if (!a[i].startsWith('--')) out.push(a[i]);
  }
  return out;
}
// Town ids from the command line, or every town in towns.json with --all.
export const townIds = () => (flag('--all') ? loadTowns().towns.map((t) => t.id) : positional());

export const UA = 'BackroadsScout/0.1 (hobby game terrain research; one query per town, cached)';
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
