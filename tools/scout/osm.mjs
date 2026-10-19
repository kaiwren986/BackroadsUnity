// Reads a cached Overpass download (fetch-osm.mjs) into local metres and sorts it into the layers the
// scout map and its statistics need. Zero dependencies. Data (c) OpenStreetMap contributors, ODbL 1.0.
import fs from 'node:fs';
import path from 'node:path';

const AREA_KEYS = ['building', 'landuse', 'natural', 'leisure', 'amenity', 'shop', 'man_made', 'historic', 'tourism', 'power', 'industrial', 'craft', 'office', 'place', 'aeroway', 'military'];
const NOT_AREA_NATURAL = new Set(['coastline', 'tree_row', 'cliff', 'ridge', 'arete', 'valley', 'gorge']);

export const DRIVE = ['motorway', 'trunk', 'primary', 'secondary', 'tertiary', 'unclassified', 'residential', 'service', 'track',
  'motorway_link', 'trunk_link', 'primary_link', 'secondary_link', 'tertiary_link', 'living_street', 'road'];
export const UNPAVED = new Set(['unpaved', 'gravel', 'dirt', 'compacted', 'ground', 'fine_gravel', 'grass', 'earth', 'mud', 'sand', 'pebblestone', 'rock', 'woodchips', 'grass_paver']);

export function roadClass(t) {
  const h = t.highway;
  if (!h) return null;
  if (h.endsWith('_link')) return h.replace('_link', '');
  if (h === 'living_street' || h === 'road') return 'residential';
  if (['motorway', 'trunk', 'primary', 'secondary', 'tertiary', 'unclassified', 'residential', 'service', 'track'].includes(h)) return h;
  if (['path', 'footway', 'cycleway', 'bridleway', 'steps', 'pedestrian'].includes(h)) return 'path';
  return null;
}
export const isUnpaved = (t) => t.highway === 'track' || UNPAVED.has(t.surface) || ['grade2', 'grade3', 'grade4', 'grade5'].includes(t.tracktype);

function isAreaWay(t, closed) {
  if (!closed) return false;
  if (t.area === 'no') return false;
  if (t.area === 'yes') return true;
  if (t.highway || t.barrier || t.railway) return false;
  if (t.waterway) return ['riverbank', 'dock', 'boatyard', 'dam'].includes(t.waterway);
  if (t.natural && NOT_AREA_NATURAL.has(t.natural)) return false;
  return AREA_KEYS.some((k) => t[k] !== undefined) || t.water !== undefined;
}

// Joins open member ways into closed rings by matching end points.
function assembleRings(lines) {
  const key = (p) => `${p[0].toFixed(3)},${p[1].toFixed(3)}`;
  const rings = [], pool = lines.filter((l) => l.length >= 2).map((l) => l.slice());
  while (pool.length) {
    let ring = pool.shift();
    let guard = 0;
    while (key(ring[0]) !== key(ring[ring.length - 1]) && guard++ < 10000) {
      const end = key(ring[ring.length - 1]);
      let idx = pool.findIndex((l) => key(l[0]) === end), rev = false;
      if (idx < 0) { idx = pool.findIndex((l) => key(l[l.length - 1]) === end); rev = true; }
      if (idx < 0) break;
      const next = pool.splice(idx, 1)[0];
      if (rev) next.reverse();
      ring = ring.concat(next.slice(1));
    }
    if (ring.length >= 3) rings.push(ring);
  }
  return rings;
}

export function ringArea(r) {
  let a = 0;
  for (let i = 0, j = r.length - 1; i < r.length; j = i++) a += (r[j][0] + r[i][0]) * (r[j][1] - r[i][1]);
  return Math.abs(a / 2);
}
export function ringCentroid(r) {
  let x = 0, y = 0;
  const n = r.length > 1 && r[0][0] === r[r.length - 1][0] && r[0][1] === r[r.length - 1][1] ? r.length - 1 : r.length;
  for (let i = 0; i < n; i++) { x += r[i][0]; y += r[i][1]; }
  return [x / n, y / n];
}

// Loads artifacts/scout/<id>/osm.json. Returns { areas, lines, points, meta } in local metres:
//   areas:  { id, tags, rings: [outer..., inner...] with outer first, area (m2), c: centroid }
//   lines:  { id, tags, pts }
//   points: { id, tags, p }
export function loadOsm(dir, frame) {
  const raw = JSON.parse(fs.readFileSync(path.join(dir, 'osm.json'), 'utf8'));
  const meta = JSON.parse(fs.readFileSync(path.join(dir, 'osm.meta.json'), 'utf8'));
  const loc = (g) => g.filter(Boolean).map((p) => frame.local(p.lat, p.lon));
  const areas = [], lines = [], points = [];
  for (const e of raw.elements) {
    const t = e.tags ?? {};
    if (e.type === 'node') { points.push({ id: `node/${e.id}`, tags: t, p: frame.local(e.lat, e.lon) }); continue; }
    if (e.type === 'way') {
      if (!e.geometry || e.geometry.length < 2) continue;
      const pts = loc(e.geometry);
      const closed = e.nodes && e.nodes[0] === e.nodes[e.nodes.length - 1] && pts.length >= 4;
      if (isAreaWay(t, closed)) {
        const area = ringArea(pts);
        areas.push({ id: `way/${e.id}`, tags: t, rings: [pts], outerCount: 1, area, c: ringCentroid(pts) });
        if (t.waterway === 'dam' || t.natural === 'water' && t.water === 'river') lines.push({ id: `way/${e.id}`, tags: t, pts, fromArea: true });
      } else {
        lines.push({ id: `way/${e.id}`, tags: t, pts, closed });
      }
      continue;
    }
    if (e.type === 'relation' && t.type === 'multipolygon') {
      const outer = [], inner = [];
      for (const m of e.members ?? []) {
        if (m.type !== 'way' || !m.geometry) continue;
        (m.role === 'inner' ? inner : outer).push(loc(m.geometry));
      }
      const o = assembleRings(outer), i = assembleRings(inner);
      if (!o.length) continue;
      const area = o.reduce((s, r) => s + ringArea(r), 0) - i.reduce((s, r) => s + ringArea(r), 0);
      const big = o.reduce((a, b) => (ringArea(a) >= ringArea(b) ? a : b));
      areas.push({ id: `relation/${e.id}`, tags: t, rings: [...o, ...i], outerCount: o.length, area, c: ringCentroid(big) });
    }
  }
  return { areas, lines, points, meta };
}

// ---- small geometry helpers ----
export function clipLen(a, b, x0, y0, x1, y1) {
  // Liang-Barsky: length of segment a-b inside the box.
  let t0 = 0, t1 = 1;
  const dx = b[0] - a[0], dy = b[1] - a[1];
  const ps = [-dx, dx, -dy, dy], qs = [a[0] - x0, x1 - a[0], a[1] - y0, y1 - a[1]];
  for (let i = 0; i < 4; i++) {
    if (ps[i] === 0) { if (qs[i] < 0) return 0; continue; }
    const r = qs[i] / ps[i];
    if (ps[i] < 0) { if (r > t1) return 0; if (r > t0) t0 = r; } else { if (r < t0) return 0; if (r < t1) t1 = r; }
  }
  return Math.hypot(dx, dy) * (t1 - t0);
}
export function polyLenIn(pts, box) {
  let s = 0;
  for (let i = 1; i < pts.length; i++) s += clipLen(pts[i - 1], pts[i], box[0], box[1], box[2], box[3]);
  return s;
}
export function segIntersect(p1, p2, p3, p4) {
  const d = (p2[0] - p1[0]) * (p4[1] - p3[1]) - (p2[1] - p1[1]) * (p4[0] - p3[0]);
  if (d === 0) return false;
  const u = ((p3[0] - p1[0]) * (p4[1] - p3[1]) - (p3[1] - p1[1]) * (p4[0] - p3[0])) / d;
  const v = ((p3[0] - p1[0]) * (p2[1] - p1[1]) - (p3[1] - p1[1]) * (p2[0] - p1[0])) / d;
  return u >= 0 && u <= 1 && v >= 0 && v <= 1;
}
export function pointInRings(x, y, rings) {
  let inside = false;
  for (const r of rings) {
    for (let i = 0, j = r.length - 1; i < r.length; j = i++) {
      const [xi, yi] = r[i], [xj, yj] = r[j];
      if ((yi > y) !== (yj > y) && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
    }
  }
  return inside;
}
export const inBox = (p, b) => p[0] >= b[0] && p[0] <= b[2] && p[1] >= b[1] && p[1] <= b[3];

// Even-odd scanline fill of rings into a coarse grid (cell = cs metres, n x n, row 0 = south).
export function rasterize(grid, n, cs, rings, value = 1) {
  let ymin = Infinity, ymax = -Infinity;
  for (const r of rings) for (const p of r) { if (p[1] < ymin) ymin = p[1]; if (p[1] > ymax) ymax = p[1]; }
  const j0 = Math.max(0, Math.floor(ymin / cs)), j1 = Math.min(n - 1, Math.ceil(ymax / cs));
  const xs = [];
  for (let j = j0; j <= j1; j++) {
    const y = (j + 0.5) * cs;
    xs.length = 0;
    for (const r of rings) for (let i = 0, k = r.length - 1; i < r.length; k = i++) {
      const [xi, yi] = r[i], [xk, yk] = r[k];
      if ((yi > y) !== (yk > y)) xs.push(xi + ((y - yi) / (yk - yi)) * (xk - xi));
    }
    xs.sort((a, b) => a - b);
    for (let q = 0; q + 1 < xs.length; q += 2) {
      const i0 = Math.max(0, Math.ceil(xs[q] / cs - 0.5)), i1 = Math.min(n - 1, Math.floor(xs[q + 1] / cs - 0.5));
      for (let i = i0; i <= i1; i++) grid[j * n + i] = value;
    }
  }
}
