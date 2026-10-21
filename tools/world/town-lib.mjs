// Shared helpers of the town source tools (card T3.0, ADR-006 stage W0): the town's local frame, the extract and play
// squares, stable JSON, hashing, clipping and the name-stripping rules. Zero dependencies.
//
// The local frame (ADR-006 §3, ADR-007): UTM on WGS84 in the town's zone (the scout's Krueger series, geo.mjs),
// x = easting - E0 (east), z = northing - N0 (north), metres; y = DEM elevation - Y0. The origin (E0, N0) is the centre
// of the play area: the scout's 2 km square centre (tools/scout/towns.json "play2", an offset from the scout frame
// centre, which is the town's place node rounded to 10 m in UTM). Data (c) OpenStreetMap contributors, ODbL 1.0;
// elevation USGS 3DEP (public domain).
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { toUtm, fromUtm, utmZone, utmEpsg, getTown, REPO } from '../scout/geo.mjs';

export { REPO };
export const PLAY_HALF = 1024;              // ADR-006 §4: 2,048 m square
export const SKIRT = 300;                   // ADR-006 §2: the extract is the play area + 300 m on every side
export const EXTRACT_HALF = PLAY_HALF + SKIRT;
export const DEM_RES = 1;                   // metres; samples at whole metres of the local frame
export const DEM_N = 2 * EXTRACT_HALF / DEM_RES + 1; // 2,649 samples per side, x = -1324 .. 1324
export const UA = 'BackroadsWorld/1.0 (hobby game terrain; one extract per town, cached; OSM credited)';
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export const srcDir = (id) => path.join(REPO, 'artifacts', 'world-src', id);
export const townDataDir = (id, version = 'v1') => path.join(REPO, 'TownData', id, version);

// The town's frame. Everything downstream (the extract, the C# TownFrame) derives from these numbers.
export function townFrame(id) {
  const town = getTown(id);
  if (!town.play2) throw new Error(`${id}: towns.json has no play2 square`);
  const zone = utmZone(town.lon), epsg = utmEpsg(zone, town.lat >= 0);
  const [ec, nc] = toUtm(town.lat, town.lon, zone);
  const E0 = Math.round(ec / 10) * 10 + town.play2.dx, N0 = Math.round(nc / 10) * 10 + town.play2.dy;
  const local = (lat, lon) => { const [e, n] = toUtm(lat, lon, zone); return [e - E0, n - N0]; };
  const latlon = (x, z) => fromUtm(E0 + x, N0 + z, zone);
  const [originLat, originLon] = latlon(0, 0);
  const H = EXTRACT_HALF;
  const corners = [[-H, -H], [H, -H], [-H, H], [H, H]].map(([x, z]) => latlon(x, z));
  const pad = 0.0005; // ~50 m, so ways that touch the extract edge come along (as the scout did)
  const lats = corners.map((c) => c[0]), lons = corners.map((c) => c[1]);
  return {
    id, town, zone, epsg, E0, N0, originLat, originLon, local, latlon,
    placeNode: town.osm, placeLatLon: [town.lat, town.lon], play2: town.play2,
    query: { south: Math.min(...lats) - pad, west: Math.min(...lons) - pad, north: Math.max(...lats) + pad, east: Math.max(...lons) + pad },
  };
}

// ---------------------------------------------------------------- hashing and stable JSON
export const sha256 = (buf) => crypto.createHash('sha256').update(buf).digest('hex');
export const sha256File = (f) => sha256(fs.readFileSync(f));

export const mm = (v) => { const r = Math.round(v * 1000) / 1000; return Object.is(r, -0) ? 0 : r; };

function sortKeys(v) {
  if (Array.isArray(v)) return v.map(sortKeys);
  if (v && typeof v === 'object') {
    const o = {};
    for (const k of Object.keys(v).sort()) if (v[k] !== undefined) o[k] = sortKeys(v[k]);
    return o;
  }
  if (typeof v === 'number' && !Number.isFinite(v)) throw new Error('non-finite number in JSON output');
  return v;
}
// Sorted keys everywhere; the top-level object one key per line, and every element of a top-level array on its own
// line (diff-friendly, still compact). LF only.
export function stableJson(obj) {
  const o = sortKeys(obj);
  const lines = ['{'];
  const keys = Object.keys(o);
  keys.forEach((k, i) => {
    const v = o[k], comma = i < keys.length - 1 ? ',' : '';
    if (Array.isArray(v) && v.length && v.every((e) => e && typeof e === 'object')) {
      lines.push(`  ${JSON.stringify(k)}: [`);
      v.forEach((e, j) => lines.push('    ' + JSON.stringify(e) + (j < v.length - 1 ? ',' : '')));
      lines.push('  ]' + comma);
    } else lines.push(`  ${JSON.stringify(k)}: ${JSON.stringify(v)}${comma}`);
  });
  lines.push('}');
  const s = lines.join('\n') + '\n';
  if (s.includes('\r')) throw new Error('CR in JSON output');
  return s;
}
export function writeFile(f, data) {
  fs.mkdirSync(path.dirname(f), { recursive: true });
  fs.writeFileSync(f, data);
  return f;
}

// ---------------------------------------------------------------- geometry (plane, [x, z] pairs)
export function ringArea(r) { // signed, > 0 counter-clockwise (x east, z north)
  let a = 0;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) a += (r[j][0] * r[i][1] - r[i][0] * r[j][1]);
  return a / 2;
}
export function ringCentroid(r) {
  let a = 0, cx = 0, cz = 0;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) {
    const c = r[j][0] * r[i][1] - r[i][0] * r[j][1];
    a += c; cx += (r[j][0] + r[i][0]) * c; cz += (r[j][1] + r[i][1]) * c;
  }
  if (Math.abs(a) < 1e-9) { let x = 0, z = 0; for (const p of r) { x += p[0]; z += p[1]; } return [x / r.length, z / r.length]; }
  return [cx / (3 * a), cz / (3 * a)];
}
export function pointInRing(x, z, r) {
  let inside = false;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) {
    const [xi, zi] = r[i], [xj, zj] = r[j];
    if ((zi > z) !== (zj > z) && x < ((xj - xi) * (z - zi)) / (zj - zi) + xi) inside = !inside;
  }
  return inside;
}
export const inBox = (p, h) => p[0] >= -h && p[0] <= h && p[1] >= -h && p[1] <= h;
export function ringBox(r) {
  let x0 = Infinity, z0 = Infinity, x1 = -Infinity, z1 = -Infinity;
  for (const [x, z] of r) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (z < z0) z0 = z; if (z > z1) z1 = z; }
  return [x0, z0, x1, z1];
}
// Sutherland-Hodgman against the square |x|, |z| <= h. Returns null when nothing is left.
export function clipRing(ring, h) {
  let pts = ring;
  const edges = [[0, -1], [0, 1], [1, -1], [1, 1]]; // axis, side: keep side*coord <= h
  for (const [ax, sd] of edges) {
    if (!pts.length) break;
    const out = [];
    const inside = (p) => sd * p[ax] <= h;
    for (let i = 0; i < pts.length; i++) {
      const a = pts[(i + pts.length - 1) % pts.length], b = pts[i];
      const ia = inside(a), ib = inside(b);
      if (ib) {
        if (!ia) out.push(cut(a, b, ax, sd * h));
        out.push(b);
      } else if (ia) out.push(cut(a, b, ax, sd * h));
    }
    pts = out;
  }
  const clean = [];
  for (const p of pts.map((q) => [mm(q[0]), mm(q[1])])) {
    const l = clean[clean.length - 1];
    if (!l || l[0] !== p[0] || l[1] !== p[1]) clean.push(p);
  }
  while (clean.length > 1 && clean[0][0] === clean[clean.length - 1][0] && clean[0][1] === clean[clean.length - 1][1]) clean.pop();
  // Sutherland-Hodgman leaves zero-width runs along the square's edge where the ring went outside past a corner:
  // drop every vertex on the edge that is collinear with its two neighbours on the same edge (spikes and midpoints).
  const onEdge = (p) => Math.abs(p[0]) === h || Math.abs(p[1]) === h;
  for (let changed = true; changed && clean.length >= 3;) {
    changed = false;
    for (let i = 0; i < clean.length && clean.length >= 3; i++) {
      const a = clean[(i + clean.length - 1) % clean.length], b = clean[i], c = clean[(i + 1) % clean.length];
      const sameX = a[0] === b[0] && b[0] === c[0] && Math.abs(b[0]) === h, sameZ = a[1] === b[1] && b[1] === c[1] && Math.abs(b[1]) === h;
      if (onEdge(b) && (sameX || sameZ)) { clean.splice(i, 1); changed = true; i--; }
    }
  }
  return clean.length >= 3 && Math.abs(ringArea(clean)) > 1e-6 ? clean : null;
}
function cut(a, b, ax, v) {
  const t = (v - a[ax]) / (b[ax] - a[ax]);
  const p = [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t];
  p[ax] = v;
  return p;
}
// Liang-Barsky per segment: the parts of a polyline inside the square, as separate polylines.
export function clipLine(pts, h) {
  const out = [];
  let cur = null;
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i];
    let t0 = 0, t1 = 1;
    const dx = b[0] - a[0], dz = b[1] - a[1];
    const ps = [-dx, dx, -dz, dz], qs = [a[0] + h, h - a[0], a[1] + h, h - a[1]];
    let ok = true;
    for (let k = 0; k < 4 && ok; k++) {
      if (ps[k] === 0) { if (qs[k] < 0) ok = false; continue; }
      const r = qs[k] / ps[k];
      if (ps[k] < 0) { if (r > t1) ok = false; else if (r > t0) t0 = r; } else { if (r < t0) ok = false; else if (r < t1) t1 = r; }
    }
    if (!ok) { cur = null; continue; }
    const p0 = [mm(a[0] + dx * t0), mm(a[1] + dz * t0)], p1 = [mm(a[0] + dx * t1), mm(a[1] + dz * t1)];
    if (!cur || cur[cur.length - 1][0] !== p0[0] || cur[cur.length - 1][1] !== p0[1] || t0 > 0) { cur = [p0]; out.push(cur); }
    cur.push(p1);
    if (t1 < 1) cur = null;
  }
  return out.filter((l) => l.length >= 2);
}
export function lineLength(pts) { let s = 0; for (let i = 1; i < pts.length; i++) s += Math.hypot(pts[i][0] - pts[i - 1][0], pts[i][1] - pts[i - 1][1]); return s; }

// ---------------------------------------------------------------- real names (ADR-006 §6, card T3.0 step 3)
// Dropped everywhere: the card's list (name, name:*, alt_name, old_name, brand*, operator, addr:*, phone, website, ref on
// anything but a state road) and every other key that carries a name or free text: any key containing "name"
// (official_name, loc_name, lock_name, bridge:name, tiger:name_base, gnis:name ...), operator:*, owner, contact:*,
// email, fax, url, description, note, fixme, inscription, destination*, wiki*, image, tiger:*, gnis:*, nhd:*, is_in*,
// source*, ref:* and old_ref/official_ref, and any key containing operator, owner, addr, phone, website, email or contact
// (old_railway_operator). The same rules are in Backroads.Tests.EditMode.Town (Town.NoRealNames) and
// listed in manifest.json "strip".
export const STRIP = {
  exact: ['name', 'alt_name', 'old_name', 'brand', 'operator', 'phone', 'website', 'owner', 'email', 'fax', 'url', 'description',
    'note', 'fixme', 'FIXME', 'inscription', 'image', 'is_in', 'source', 'old_ref', 'official_ref', 'artist', 'architect', 'subject', 'flag:name'],
  prefixes: ['name:', 'alt_name:', 'old_name:', 'brand:', 'operator:', 'addr:', 'phone:', 'website:', 'contact:', 'destination', 'wiki',
    'tiger:', 'gnis:', 'nhd:', 'is_in:', 'source:', 'ref:', 'note:', 'description:', 'fixme:', 'subject:', 'image:', 'old_addr', 'url:', 'email:'],
  contains: ['name', 'brand', 'operator', 'owner', 'addr', 'phone', 'website', 'email', 'contact', 'wikidata', 'wikipedia'],
};
// ref survives only on highway ways, and only when every part is a state route (a class hint, never shown).
export const STATE_REF = /^(OH|SR)[ -]?\d+[A-Z]?$/;
export function keyDropped(k) {
  if (STRIP.exact.includes(k)) return true;
  if (STRIP.prefixes.some((p) => k.startsWith(p))) return true;
  if (STRIP.contains.some((c) => k.toLowerCase().includes(c))) return true;
  return false;
}
export function refKept(tags, kind) {
  if (kind !== 'way' || !tags.highway || tags.ref === undefined) return false;
  return String(tags.ref).split(';').map((s) => s.trim()).every((s) => STATE_REF.test(s));
}
// Returns the stripped tags (sorted) and adds every dropped key to the tally.
export function stripTags(tags, kind, tally) {
  const out = {};
  for (const k of Object.keys(tags ?? {}).sort()) {
    let drop = keyDropped(k);
    if (k === 'ref') drop = !refKept(tags, kind);
    if (drop) { if (tally) tally[k] = (tally[k] ?? 0) + 1; continue; }
    out[k] = String(tags[k]);
  }
  return out;
}
// The full names in the raw data (name, alt_name, old_name, official_name ..., name_1, ref_name, brand, operator and
// any *operator key, addr:street, addr:city, addr:place, addr:housename), for the leak scan after stripping. Name
// fragments (tiger:name_base "Water", "Main") are left out: they are ordinary words that tag values may hold.
export const FULL_NAME_KEY = /^((alt_|old_|official_|loc_|short_|reg_|nat_|int_|ref_)?name(_\d+)?(:.*)?|brand|operator|[a-z_:]*operator|addr:(street|city|place|housename))$/;
export function nameValues(tags, sink) {
  for (const [k, v] of Object.entries(tags ?? {})) {
    if (!FULL_NAME_KEY.test(k)) continue;
    for (const part of String(v).split(';')) { const s = part.trim(); if (s) sink.add(s); }
  }
}
