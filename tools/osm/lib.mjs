// Shared helpers for the OSM street and site study (zero dependencies).
// Plane geometry in local metres: x east, y north. Polygons are arrays of {x, y}, not closed (first != last).
// OpenStreetMap data is (c) OpenStreetMap contributors, ODbL 1.0.

export const R2D = 180 / Math.PI;

// ------------------------------------------------------------------ statistics

export function quant(arr, ps = [0.1, 0.25, 0.5, 0.75, 0.9]) {
  const a = arr.filter((v) => Number.isFinite(v));
  if (!a.length) return null;
  const s = [...a].sort((p, q) => p - q);
  return ps.map((p) => round(s[Math.min(s.length - 1, Math.max(0, Math.floor(p * (s.length - 1) + 0.5)))], 2));
}
// a distribution record: n, p10..p90, min, max
export function dist(arr, digits = 2) {
  const a = arr.filter((v) => Number.isFinite(v));
  if (!a.length) return { n: 0 };
  const q = quant(a);
  return { n: a.length, p10: round(q[0], digits), p25: round(q[1], digits), p50: round(q[2], digits), p75: round(q[3], digits), p90: round(q[4], digits), min: round(Math.min(...a), digits), max: round(Math.max(...a), digits) };
}
export function counts(arr) {
  const o = {};
  for (const v of arr) o[v] = (o[v] || 0) + 1;
  return Object.fromEntries(Object.entries(o).sort((a, b) => b[1] - a[1]));
}
export function shares(arr) {
  const c = counts(arr), n = arr.length, o = { n };
  for (const [k, v] of Object.entries(c)) o[k] = round(v / n, 3);
  return o;
}
export const round = (v, d = 2) => (Number.isFinite(v) ? Math.round(v * 10 ** d) / 10 ** d : v);
export const mean = (a) => (a.length ? a.reduce((x, y) => x + y, 0) / a.length : NaN);

// ------------------------------------------------------------------ projection and parsing

export function projector(c) {
  const kx = Math.cos((c.lat * Math.PI) / 180) * 111320, ky = 110574;
  return (p) => ({ x: (p.lon - c.lon) * kx, y: (p.lat - c.lat) * ky });
}

// Joins way pieces (arrays of points) into closed rings; returns the rings with >= 3 distinct points.
export function assembleRings(pieces) {
  const key = (p) => p.x.toFixed(2) + ',' + p.y.toFixed(2);
  const rings = [];
  const open = pieces.map((g) => [...g]).filter((g) => g.length >= 2);
  while (open.length) {
    let cur = open.shift();
    let guard = 0;
    while (key(cur[0]) !== key(cur[cur.length - 1]) && guard++ < 1000) {
      const end = key(cur[cur.length - 1]);
      let found = -1, rev = false;
      for (let i = 0; i < open.length; i++) {
        if (key(open[i][0]) === end) { found = i; break; }
        if (key(open[i][open[i].length - 1]) === end) { found = i; rev = true; break; }
      }
      if (found < 0) break;
      const nxt = open.splice(found, 1)[0];
      cur = cur.concat((rev ? nxt.reverse() : nxt).slice(1));
    }
    if (key(cur[0]) === key(cur[cur.length - 1])) cur = cur.slice(0, -1);
    if (cur.length >= 3) rings.push(cur);
  }
  return rings;
}

// Reads a raw Overpass answer into nodes, ways (with projected geometry) and relation polygons.
export function loadTown(T) {
  const P = projector(T.centre);
  const seen = new Set();
  const nodes = [], ways = [], rels = [];
  for (const e of T.elements) {
    const id = e.type + '/' + e.id;
    if (seen.has(id)) continue;
    seen.add(id);
    if (e.type === 'node') nodes.push({ id, tags: e.tags || {}, p: P(e) });
    else if (e.type === 'way' && e.geometry) {
      const g = e.geometry.filter(Boolean).map(P);
      const closed = e.nodes && e.nodes.length > 3 && e.nodes[0] === e.nodes[e.nodes.length - 1];
      ways.push({ id, tags: e.tags || {}, g, closed, refs: e.nodes || [] });
    } else if (e.type === 'relation' && e.members) {
      const outer = e.members.filter((m) => m.type === 'way' && m.geometry && (m.role === 'outer' || m.role === '')).map((m) => m.geometry.filter(Boolean).map(P));
      const inner = e.members.filter((m) => m.type === 'way' && m.geometry && m.role === 'inner').map((m) => m.geometry.filter(Boolean).map(P));
      for (const ring of assembleRings(outer)) rels.push({ id, tags: e.tags || {}, poly: ring, holes: assembleRings(inner) });
    }
  }
  return { name: T.name, centre: T.centre, nodes, ways, rels };
}

// ------------------------------------------------------------------ plane geometry

export function area(poly) {
  let a = 0;
  for (let i = 0; i < poly.length; i++) { const p = poly[i], q = poly[(i + 1) % poly.length]; a += p.x * q.y - q.x * p.y; }
  return a / 2; // signed: > 0 counter-clockwise
}
export const absArea = (poly) => Math.abs(area(poly));
export function centroid(poly) {
  let a = 0, cx = 0, cy = 0;
  for (let i = 0; i < poly.length; i++) {
    const p = poly[i], q = poly[(i + 1) % poly.length], c = p.x * q.y - q.x * p.y;
    a += c; cx += (p.x + q.x) * c; cy += (p.y + q.y) * c;
  }
  if (Math.abs(a) < 1e-9) { let x = 0, y = 0; for (const p of poly) { x += p.x; y += p.y; } return { x: x / poly.length, y: y / poly.length }; }
  return { x: cx / (3 * a), y: cy / (3 * a) };
}
export function perimeter(poly) {
  let L = 0;
  for (let i = 0; i < poly.length; i++) L += dist2(poly[i], poly[(i + 1) % poly.length]);
  return L;
}
export const dist2 = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
export function lineLength(g) { let L = 0; for (let i = 1; i < g.length; i++) L += dist2(g[i - 1], g[i]); return L; }

export function hull(pts) {
  const p = [...pts].sort((a, b) => a.x - b.x || a.y - b.y);
  if (p.length < 3) return p;
  const cr = (o, a, b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
  const lo = [], up = [];
  for (const v of p) { while (lo.length >= 2 && cr(lo[lo.length - 2], lo[lo.length - 1], v) <= 0) lo.pop(); lo.push(v); }
  for (const v of p.reverse()) { while (up.length >= 2 && cr(up[up.length - 2], up[up.length - 1], v) <= 0) up.pop(); up.push(v); }
  return lo.slice(0, -1).concat(up.slice(0, -1));
}
// minimum-area rectangle: w = short side, d = long side, ang = direction of the long side (radians), c = centre
export function minRect(poly) {
  const h = hull(poly);
  let best = null;
  for (let i = 0; i < h.length; i++) {
    const a = h[i], b = h[(i + 1) % h.length];
    const ang = Math.atan2(b.y - a.y, b.x - a.x), c = Math.cos(ang), s = Math.sin(ang);
    let mnu = Infinity, mxu = -Infinity, mnv = Infinity, mxv = -Infinity;
    for (const p of h) { const u = p.x * c + p.y * s, v = -p.x * s + p.y * c; mnu = Math.min(mnu, u); mxu = Math.max(mxu, u); mnv = Math.min(mnv, v); mxv = Math.max(mxv, v); }
    const A = (mxu - mnu) * (mxv - mnv);
    if (!best || A < best.A) {
      const L1 = mxu - mnu, L2 = mxv - mnv, cu = (mnu + mxu) / 2, cv = (mnv + mxv) / 2;
      best = { A, w: Math.min(L1, L2), d: Math.max(L1, L2), ang: L1 >= L2 ? ang : ang + Math.PI / 2, c: { x: cu * c - cv * s, y: cu * s + cv * c } };
    }
  }
  return best || { A: 0, w: 0, d: 0, ang: 0, c: poly[0] };
}
export function segDist(p, a, b) {
  const ex = b.x - a.x, ey = b.y - a.y, l2 = ex * ex + ey * ey || 1e-12;
  let t = ((p.x - a.x) * ex + (p.y - a.y) * ey) / l2;
  t = Math.max(0, Math.min(1, t));
  const x = a.x + ex * t, y = a.y + ey * t, L = Math.sqrt(l2);
  return { d: Math.hypot(p.x - x, p.y - y), t, x, y, tx: ex / L, ty: ey / L };
}
export function inPoly(p, poly) {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const a = poly[i], b = poly[j];
    if ((a.y > p.y) !== (b.y > p.y) && p.x < ((b.x - a.x) * (p.y - a.y)) / (b.y - a.y) + a.x) inside = !inside;
  }
  return inside;
}
export function polyDist(p, poly) { // distance from a point to a polygon outline (0 inside)
  if (inPoly(p, poly)) return 0;
  let d = Infinity;
  for (let i = 0; i < poly.length; i++) d = Math.min(d, segDist(p, poly[i], poly[(i + 1) % poly.length]).d);
  return d;
}
function segSeg(a, b, c, d) {
  const r = { x: b.x - a.x, y: b.y - a.y }, s = { x: d.x - c.x, y: d.y - c.y };
  const den = r.x * s.y - r.y * s.x;
  if (Math.abs(den) < 1e-12) return null;
  const t = ((c.x - a.x) * s.y - (c.y - a.y) * s.x) / den, u = ((c.x - a.x) * r.y - (c.y - a.y) * r.x) / den;
  return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? { t, u, x: a.x + r.x * t, y: a.y + r.y * t } : null;
}
export function polyPolyDist(A, B) { // edge-to-edge distance, 0 if they overlap
  for (const p of A) if (inPoly(p, B)) return 0;
  for (const p of B) if (inPoly(p, A)) return 0;
  let d = Infinity;
  for (let i = 0; i < A.length; i++) {
    const a = A[i], b = A[(i + 1) % A.length];
    for (let j = 0; j < B.length; j++) {
      const c = B[j], e = B[(j + 1) % B.length];
      if (segSeg(a, b, c, e)) return 0;
      d = Math.min(d, segDist(a, c, e).d, segDist(b, c, e).d, segDist(c, a, b).d, segDist(e, a, b).d);
    }
  }
  return d;
}
export function lineCrossesPoly(g, poly) { // number of crossings of a polyline through a polygon outline
  let n = 0;
  for (let i = 1; i < g.length; i++) for (let j = 0; j < poly.length; j++) if (segSeg(g[i - 1], g[i], poly[j], poly[(j + 1) % poly.length])) n++;
  return n;
}
// ray from p along unit dir; nearest hit distance against a list of polylines/polygons (closed=true)
export function rayHit(p, dir, shapes, maxD = 200) {
  const q = { x: p.x + dir.x * maxD, y: p.y + dir.y * maxD };
  let best = Infinity;
  for (const s of shapes) {
    const g = s.g, n = s.closed ? g.length : g.length - 1;
    for (let i = 0; i < n; i++) {
      const h = segSeg(p, q, g[i], g[(i + 1) % g.length]);
      if (h && h.t * maxD > 0.05) best = Math.min(best, h.t * maxD);
    }
  }
  return best;
}
// polygon simplification (Douglas-Peucker on the closed ring), used for shape complexity
export function simplify(poly, tol = 0.5) {
  if (poly.length < 4) return poly;
  const dp = (pts) => {
    if (pts.length < 3) return pts;
    let md = 0, mi = 0;
    for (let i = 1; i < pts.length - 1; i++) { const d = segDist(pts[i], pts[0], pts[pts.length - 1]).d; if (d > md) { md = d; mi = i; } }
    if (md <= tol) return [pts[0], pts[pts.length - 1]];
    return dp(pts.slice(0, mi + 1)).slice(0, -1).concat(dp(pts.slice(mi)));
  };
  // split at the farthest point from the first vertex so the ring closes cleanly
  let fi = 0, fd = 0;
  for (let i = 1; i < poly.length; i++) { const d = dist2(poly[0], poly[i]); if (d > fd) { fd = d; fi = i; } }
  const a = dp(poly.slice(0, fi + 1)), b = dp(poly.slice(fi).concat([poly[0]]));
  return a.slice(0, -1).concat(b.slice(0, -1));
}
export function reflexCount(poly) { // concave corners (L, T, U shapes have 1, 2, 2+)
  const s = area(poly) > 0 ? 1 : -1;
  let n = 0;
  for (let i = 0; i < poly.length; i++) {
    const a = poly[(i + poly.length - 1) % poly.length], b = poly[i], c = poly[(i + 1) % poly.length];
    const cr = (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x);
    if (cr * s < -1e-6) n++;
  }
  return n;
}
export function sampleInside(poly, step = 1.5) { // grid points inside a polygon
  let x0 = Infinity, x1 = -Infinity, y0 = Infinity, y1 = -Infinity;
  for (const p of poly) { x0 = Math.min(x0, p.x); x1 = Math.max(x1, p.x); y0 = Math.min(y0, p.y); y1 = Math.max(y1, p.y); }
  const out = [];
  for (let y = y0 + step / 2; y < y1; y += step) for (let x = x0 + step / 2; x < x1; x += step) { const p = { x, y }; if (inPoly(p, poly)) out.push(p); }
  return out;
}
export const angDiff = (a, b) => { let d = Math.abs(a - b) % Math.PI; return Math.min(d, Math.PI - d); }; // undirected lines, 0..pi/2

// ------------------------------------------------------------------ spatial index

export class Grid {
  constructor(cell = 40) { this.cell = cell; this.m = new Map(); }
  key(i, j) { return i * 1000003 + j; }
  addBox(item, x0, y0, x1, y1) {
    const c = this.cell;
    for (let i = Math.floor(x0 / c); i <= Math.floor(x1 / c); i++) for (let j = Math.floor(y0 / c); j <= Math.floor(y1 / c); j++) {
      const k = this.key(i, j);
      if (!this.m.has(k)) this.m.set(k, []);
      this.m.get(k).push(item);
    }
  }
  addPoly(item, poly) {
    let x0 = Infinity, x1 = -Infinity, y0 = Infinity, y1 = -Infinity;
    for (const p of poly) { x0 = Math.min(x0, p.x); x1 = Math.max(x1, p.x); y0 = Math.min(y0, p.y); y1 = Math.max(y1, p.y); }
    this.addBox(item, x0, y0, x1, y1);
  }
  near(p, r) {
    const c = this.cell, out = new Set();
    for (let i = Math.floor((p.x - r) / c); i <= Math.floor((p.x + r) / c); i++) for (let j = Math.floor((p.y - r) / c); j <= Math.floor((p.y + r) / c); j++) {
      for (const it of this.m.get(this.key(i, j)) || []) out.add(it);
    }
    return [...out];
  }
}

// ------------------------------------------------------------------ tags

export function num(v) { // "12", "12 m", "12.5m", "40'" , "40 ft", "3;4" -> metres or count
  if (v === undefined || v === null) return NaN;
  const s = String(v).trim().split(';')[0];
  const ft = s.match(/^([\d.]+)\s*(ft|feet|')$/i);
  if (ft) return +ft[1] * 0.3048;
  const fi = s.match(/^(\d+)'\s*(\d+)"?$/);
  if (fi) return +fi[1] * 0.3048 + +fi[2] * 0.0254;
  const m = s.match(/^([\d.]+)\s*(m|metres|meters)?$/i);
  return m ? +m[1] : NaN;
}
