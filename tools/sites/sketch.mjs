// "How it will look": plan sketches of the rule-based site layouts of docs/design/site-layouts.md, one per business
// type, drawn by a small prototype of the layout rules (parking modules, stalls, accessible stalls, islands, entrances,
// docks, canopies) with the numbers measured in Game/Assets/Content/sites/siteProfile.json.
//
//   node tools/sites/sketch.mjs        -> docs/design/site-layouts/<type>.svg and index.svg
//
// Frame: u along the street (to the right), v away from the street; the street centreline is v = 0 and runs along the
// bottom of each sketch. This is a sketch tool, not the generator: rectangles only, no polygon clipping.
// Zero dependencies.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Canvas, png } from '../osm/raster.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const OUT = path.join(ROOT, 'docs', 'design', 'site-layouts');
const PROFILE = path.join(ROOT, 'Game', 'Assets', 'Content', 'sites', 'siteProfile.json');

// ------------------------------------------------------------------ numbers (measured where the profile has them)

const prof = fs.existsSync(PROFILE) ? JSON.parse(fs.readFileSync(PROFILE, 'utf8')) : {};
const pick = (f, dflt, lo = -Infinity, hi = Infinity) => { try { const v = f(prof); return Number.isFinite(v) && v >= lo && v <= hi ? v : dflt; } catch { return dflt; } };
const K = {
  stallW: pick((p) => p.parking.stalls.width.p50, 2.7, 2.4, 3.0),
  stallL: pick((p) => (p.parking.stalls.length.n >= 20 ? p.parking.stalls.length.p50 : NaN), 5.5, 4.8, 6.1), // 17 mapped stalls: too few, use 18 ft
  aisle2: pick((p) => p.parking.aisles.widthBetweenStalls.p50, 7.3, 6.0, 8.0),
  adaW: 2.44, adaAisle: 1.52, vanAisle: 2.44,           // 2010 ADA Standards 502.2, 502.3
  throat: 7.3, throatRes: 3.6, throatTruck: 12.0,       // driveway throats (two-way commercial, house, truck)
  kerb: 0.15, walk: 1.5,
};
const ADA_TABLE = [[25, 1], [50, 2], [75, 3], [100, 4], [150, 5], [200, 6], [300, 7], [400, 8], [500, 9]]; // 2010 ADA 208.2
const adaCount = (n) => { if (n <= 0) return 0; for (const [m, k] of ADA_TABLE) if (n <= m) return k; return n <= 1000 ? Math.ceil(n * 0.02) : 20 + Math.ceil((n - 1000) / 100); };

// ------------------------------------------------------------------ geometry and the layout prototype

const R = (u0, v0, u1, v1) => ({ u0: Math.min(u0, u1), v0: Math.min(v0, v1), u1: Math.max(u0, u1), v1: Math.max(v0, v1) });
const poly = (r) => [[r.u0, r.v0], [r.u1, r.v0], [r.u1, r.v1], [r.u0, r.v1]];

// Packs a rectangular lot with 90-degree parking modules. dir 'v': aisles run away from the street; 'u': along it.
// door = {u, v} of the building's main door (accessible stalls go nearest it). Returns drawable parts.
function packLot(lot, { dir = 'v', door = null, frontLane = true, backLane = true, endIsland = 2.0, edge = 0.6, singleOk = true } = {}) {
  const out = { aisles: [], stalls: [], islands: [], lamps: [], arrows: [] };
  const L = K.stallL, W = K.stallW, A = K.aisle2, M = 2 * L + A;
  const across0 = dir === 'v' ? lot.u0 + edge : lot.v0 + edge, across1 = dir === 'v' ? lot.u1 - edge : lot.v1 - edge;
  const along0 = (dir === 'v' ? lot.v0 : lot.u0) + edge, along1 = (dir === 'v' ? lot.v1 : lot.u1) - edge;
  const span = across1 - across0;
  let n = Math.floor(span / M), rest = span - n * M, single = false;
  if (singleOk && rest >= L + A + 0.3) { single = true; rest -= L + A; }
  const pad = rest / 2;
  const mk = (a0, a1, b0, b1) => (dir === 'v' ? R(a0, b0, a1, b1) : R(b0, a0, b1, a1)); // a = across, b = along
  // drive lanes at the ends (connect the aisles); the front one meets the entrance
  const fl = frontLane ? A : 0, bl = backLane ? A : 0;
  if (frontLane) out.aisles.push(mk(across0 + pad, across1 - pad, along0, along0 + A));
  if (backLane) out.aisles.push(mk(across0 + pad, across1 - pad, along1 - A, along1));
  const rowsAt = [];
  let x = across0 + pad;
  for (let i = 0; i < n; i++) {
    rowsAt.push({ a0: x, a1: x + L, face: +1 });
    out.aisles.push(mk(x + L, x + L + A, along0 + fl, along1 - bl));
    rowsAt.push({ a0: x + L + A, a1: x + M, face: -1 });
    x += M;
  }
  if (single) { out.aisles.push(mk(x, x + A, along0 + fl, along1 - bl)); rowsAt.push({ a0: x + A, a1: x + A + L, face: -1 }); }
  const b0 = along0 + fl + endIsland, b1 = along1 - bl - endIsland;
  for (const r of rowsAt) {
    const k = Math.floor((b1 - b0) / W);
    const off = (b1 - b0 - k * W) / 2;
    out.islands.push(mk(r.a0, r.a1, along0 + fl, b0 + off), mk(r.a0, r.a1, b1 - off, along1 - bl));
    for (let j = 0; j < k; j++) out.stalls.push({ r: mk(r.a0, r.a1, b0 + off + j * W, b0 + off + (j + 1) * W), ada: false, van: false });
    out.lamps.push(dir === 'v' ? [(r.a0 + r.a1) / 2, (along0 + fl + b0 + off) / 2] : [(along0 + fl + b0 + off) / 2, (r.a0 + r.a1) / 2]);
  }
  // accessible stalls nearest the door; every 6th one is a van stall (2010 ADA 208.2.4); aisles alongside
  const need = adaCount(out.stalls.length);
  if (door && need) {
    const c = (s) => [(s.r.u0 + s.r.u1) / 2, (s.r.v0 + s.r.v1) / 2];
    const order = [...out.stalls].sort((a, b) => Math.hypot(c(a)[0] - door.u, c(a)[1] - door.v) - Math.hypot(c(b)[0] - door.u, c(b)[1] - door.v));
    const taken = new Set();
    let made = 0;
    for (const s of order) {
      if (made >= need) break;
      if (taken.has(s)) continue;
      s.ada = true; s.van = made % 6 === 0; taken.add(s);
      // the neighbour along the row becomes the hatched access aisle
      const nb = order.find((o) => !taken.has(o) && Math.abs((dir === 'v' ? o.r.u0 - s.r.u0 : o.r.v0 - s.r.v0)) < 0.01 &&
        Math.abs((dir === 'v' ? o.r.v0 - s.r.v0 : o.r.u0 - s.r.u0)) < W + 0.01);
      if (nb) { nb.access = true; taken.add(nb); }
      made++;
    }
  }
  out.count = out.stalls.filter((s) => !s.access).length;
  out.ada = need;
  return out;
}

// ------------------------------------------------------------------ SVG

const C = { grass: '#cfe0b4', asphalt: '#6b6b70', lotAsph: '#7d7d82', concrete: '#c9c6bf', gravel: '#b9ad97', dirt: '#a88b68', walk: '#d9d6cf', kerb: '#f2f1ec',
  bld: '#9c6b4c', bld2: '#b9a07e', roofline: '#5b3d2b', canopy: 'rgba(250,250,250,0.85)', island: '#8fb36c', line: '#ffffff', yellow: '#f2c230', ada: '#2f6fc0',
  fence: '#4f7d3a', water: '#8fb7d6', text: '#222', prop: '#d0402f', rail: '#555' };

class Sheet {
  constructor(title, box, scale = 4) { this.t = title; this.b = box; this.s = scale; this.prims = []; this.legend = []; this.note = ''; }
  X(u) { return (u - this.b.u0) * this.s; }
  Y(v) { return (this.b.v1 - v) * this.s; }
  rect(r, fill, o = {}) { return this.pg(poly(r), fill, o); }
  pg(P, fill, o = {}) { this.prims.push({ k: 'pg', P, fill, ...o }); return this; }
  pl(P, stroke, w = 1, o = {}) { this.prims.push({ k: 'pl', P, stroke, w, ...o }); return this; }
  dot(u, v, r, fill) { this.prims.push({ k: 'dot', P: [[u, v]], r, fill }); return this; }
  text(u, v, s, size = 10, o = {}) { this.prims.push({ k: 'text', P: [[u, v]], s, size, ...o }); return this; }
  prop(u, v, label, prefab) { this.dot(u, v, 3.2, C.prop); this.text(u + 0.8, v + 0.6, label, 8); if (prefab && !this.legend.some((l) => l[0] === label)) this.legend.push([label, prefab]); return this; }
  street(hw, walk = K.walk, kind = 'kerbed', u0 = this.b.u0, u1 = this.b.u1) {
    this.rect(R(u0, -hw, u1, hw), C.asphalt);
    this.pl([[u0, 0.1], [u1, 0.1]], C.yellow, 1.2).pl([[u0, -0.1], [u1, -0.1]], C.yellow, 1.2);
    if (kind === 'kerbed') { this.rect(R(u0, hw, u1, hw + K.kerb), C.kerb); this.rect(R(u0, hw + K.kerb, u1, hw + K.kerb + walk), C.walk); }
    else this.rect(R(u0, hw, u1, hw + 1.2), C.gravel);
    return this;
  }
  cut(u, w, hw, walk = K.walk, surface = C.lotAsph) { // driveway cut: dropped kerb + apron across the walk, with flares
    return this.pg([[u - w / 2 - 1, hw], [u + w / 2 + 1, hw], [u + w / 2, hw + K.kerb + walk], [u - w / 2, hw + K.kerb + walk]], surface);
  }
  lot(p) {
    for (const i of p.islands) this.rect(i, C.island, { stroke: C.kerb, sw: 1 });
    for (const s of p.stalls) {
      if (s.ada) this.rect(s.r, C.ada, { op: 0.85 });
      if (s.access) this.rect(s.r, 'hatch');
      this.rect(s.r, 'none', { stroke: C.line, sw: 1 });
    }
    for (const l of p.lamps) this.prop(l[0], l[1], 'L', 'PolygonCity SM_Prop_LightPole_Base_01 + SM_Prop_LightPole_Lights_01');
    return this;
  }
  bld(r, label, fill = C.bld) { this.rect(r, fill, { stroke: C.roofline, sw: 1 }); if (label) this.text((r.u0 + r.u1) / 2 - (label.length * 2.6) / this.s, (r.v0 + r.v1) / 2, label, 10, { fill: '#fff' }); return this; }
  dim(a, b, label, off = 0) { this.pl([a, b], '#333', 0.8, { ticks: true }); this.text((a[0] + b[0]) / 2 + off, (a[1] + b[1]) / 2 + 0.6, label, 8); return this; }
  svg() {
    const W = (this.b.u1 - this.b.u0) * this.s, H = (this.b.v1 - this.b.v0) * this.s, LH = 14 * (this.legend.length + 2) + 10;
    const pts = (P) => P.map(([u, v]) => `${this.X(u).toFixed(1)},${this.Y(v).toFixed(1)}`).join(' ');
    const body = this.prims.map((p) => {
      if (p.k === 'pg') return `<polygon points="${pts(p.P)}" fill="${p.fill === 'hatch' ? 'url(#hatch)' : p.fill}"${p.op ? ` fill-opacity="${p.op}"` : ''}${p.stroke ? ` stroke="${p.stroke}" stroke-width="${p.sw || 1}"` : ''}${p.dash ? ` stroke-dasharray="${p.dash}"` : ''}/>`;
      if (p.k === 'pl') return `<polyline points="${pts(p.P)}" fill="none" stroke="${p.stroke}" stroke-width="${p.w}"${p.dash ? ` stroke-dasharray="${p.dash}"` : ''}${p.ticks ? ' marker-start="url(#tick)" marker-end="url(#tick)"' : ''} stroke-linejoin="round"/>`;
      if (p.k === 'dot') return `<circle cx="${this.X(p.P[0][0]).toFixed(1)}" cy="${this.Y(p.P[0][1]).toFixed(1)}" r="${p.r}" fill="${p.fill}"/>`;
      return `<text x="${this.X(p.P[0][0]).toFixed(1)}" y="${this.Y(p.P[0][1]).toFixed(1)}" font-size="${p.size}" font-family="sans-serif" fill="${p.fill || C.text}">${esc(p.s)}</text>`;
    }).join('\n');
    const leg = this.legend.map(([l, p], i) => `<circle cx="10" cy="${H + 22 + i * 14}" r="3.2" fill="${C.prop}"/><text x="18" y="${H + 25 + i * 14}" font-size="10" font-family="sans-serif">${esc(l)}: ${esc(p)}</text>`).join('');
    return `<svg xmlns="http://www.w3.org/2000/svg" width="${W.toFixed(0)}" height="${(H + LH + 18).toFixed(0)}" viewBox="0 0 ${W.toFixed(0)} ${(H + LH + 18).toFixed(0)}">
<defs><pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="6" height="6" fill="${C.lotAsph}"/><line x1="0" y1="0" x2="0" y2="6" stroke="${C.ada}" stroke-width="2"/></pattern>
<marker id="tick" viewBox="0 0 4 4" refX="2" refY="2" markerWidth="4" markerHeight="4"><rect width="4" height="4" fill="#333"/></marker></defs>
<rect width="100%" height="100%" fill="white"/><rect width="${W.toFixed(0)}" height="${H.toFixed(0)}" fill="${C.grass}"/>
${body}
<line x1="8" y1="${(H - 8).toFixed(0)}" x2="${(8 + 10 * this.s).toFixed(0)}" y2="${(H - 8).toFixed(0)}" stroke="#000" stroke-width="3"/><text x="8" y="${(H - 12).toFixed(0)}" font-size="9" font-family="sans-serif" fill="#fff">10 m</text>
<text x="8" y="${(H + 13).toFixed(0)}" font-size="12" font-family="sans-serif" font-weight="bold">${esc(this.t)}</text>
${leg}
<text x="8" y="${(H + LH + 12).toFixed(0)}" font-size="9" font-family="sans-serif" fill="#555">${esc(this.note)}</text>
</svg>
`;
  }
  raster(cv, ox, oy, scale) { // quick-look PNG (no text)
    const k = scale / this.s, X = ([u, v]) => [ox + this.X(u) * k, oy + this.Y(v) * k];
    cv.poly(poly(R(this.b.u0, this.b.v0, this.b.u1, this.b.v1)).map(X), rgb(C.grass));
    for (const p of this.prims) {
      if (p.k === 'pg') { if (p.fill !== 'none') cv.poly(p.P.map(X), p.fill === 'hatch' ? [120, 150, 210] : rgb(p.fill), p.op || alpha(p.fill)); if (p.stroke) cv.pline(p.P.map(X), rgb(p.stroke), 1, true); }
      else if (p.k === 'pl') cv.pline(p.P.map(X), rgb(p.stroke), Math.max(1, p.w * k));
      else if (p.k === 'dot') cv.dot(X(p.P[0]), rgb(p.fill), Math.max(1, Math.round(p.r * k)));
    }
  }
}
const rgb = (c) => { if (c.startsWith('#')) return [1, 3, 5].map((i) => parseInt(c.slice(i, i + 2), 16)); return c.match(/[\d.]+/g).map(Number).slice(0, 3); };
const alpha = (c) => { const m = c.startsWith('rgba') ? c.match(/[\d.]+/g).map(Number) : null; return m ? m[3] : 1; };
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const m1 = (v) => v.toFixed(1);

// ------------------------------------------------------------------ one sketch per business type

const sketches = {};

sketches.house = () => {
  const S = new Sheet('House: driveway to a rear garage (residential street)', R(-20, -6, 20, 46));
  const hw = 4.5; S.street(hw);
  S.rect(R(-11, hw + 1.65, 11, 44), C.grass, { stroke: '#9fb98a', dash: '4 3' });
  S.cut(6.5, K.throatRes, hw).rect(R(4.7, hw + 1.65, 8.3, 36), C.concrete).rect(R(3.8, 30, 9.6, 36.5), C.concrete);
  S.bld(R(-8, 12, 3.5, 22), 'house').bld(R(3.5, 30.2, 9.6, 37), 'garage', C.bld2);
  S.rect(R(-3.6, hw + 1.65, -2.4, 12), C.walk);
  S.prop(9.6, hw + 2.2, 'M', 'PolygonCity SM_Prop_Mailbox_01').prop(-9, 39, 'T', 'PolygonCity SM_Prop_Trashbin_01');
  S.pl([[-11, 26], [-11, 44], [11, 44], [11, 38]], C.fence, 2, { dash: '3 2' });
  S.dim([4.7, 24], [8.3, 24], `${m1(K.throatRes)} m`, -2).dim([-12.5, hw + 1.65], [-12.5, 12], 'setback', 0);
  S.note = 'Driveway 3.0-3.6 m on the house\'s side, to a garage behind or beside the house; dropped kerb and flared apron across the walk (lane R cut); fence behind.'; return S;
};

sketches.downtown = () => {
  const S = new Sheet('Downtown: street-front shops, on-street parking, alley and rear lot', R(-40, -9, 40, 62), 3.6);
  const hw = 7.5; S.street(hw, 3.0);
  for (let u = -38; u < 38; u += 6.1) S.rect(R(u, hw - 2.4, u + 6.1, hw), 'none', { stroke: C.line, sw: 1 });
  const fronts = [[-38, -24], [-24, -14], [-14, 0], [0, 9], [9, 22], [22, 38]];
  fronts.forEach(([a, b], i) => S.bld(R(a, hw + 3.15, b, hw + 3.15 + 24 - (i % 2) * 4), i === 2 ? 'shop' : '', i % 2 ? C.bld : '#8a5c42'));
  S.rect(R(-40, 44, 40, 50), C.asphalt).text(-38, 46, 'alley 6 m', 9, { fill: '#fff' });
  const lot = R(-14, 50.5, 22, 61.5);
  S.rect(lot, C.lotAsph); S.lot(packLot(lot, { dir: 'u', frontLane: false, backLane: false, edge: 0.3, singleOk: false, door: { u: -7, v: 40 } }));
  S.prop(26, 41, 'D', 'PolygonProRacer SM_Prop_Dumpster_01').prop(-30, hw + 1.2, 'B', 'PolygonCity SM_Prop_ParkBench_01').prop(-20, hw + 1.2, 'P', 'PolygonCity SM_Prop_Planter_01')
    .prop(-6, hw + 0.5, 'L', 'PolygonCity SM_Prop_LightPole_Base_02').prop(12, hw + 1.2, 'R', 'PolygonCity SM_Prop_Trashbin_02').prop(30, hw + 0.4, 'H', 'PolygonCity SM_Prop_Hydrant_01');
  S.dim([-39, hw], [-39, hw + 3.15], 'walk 3 m', 1);
  S.note = 'Walls on the walk line, shared side walls; parallel stalls 6.1 m in the parking lane; deliveries and staff from the alley; a rear lot off the alley where a gap allows.'; return S;
};

sketches.retail = () => {
  const S = new Sheet('Standalone retail: front lot, two entrances (rule layout)', R(-36, -8, 36, 64), 3.6);
  const hw = 6.0; S.street(hw);
  const lot = R(-32, hw + 1.65 + 1.5, 32, 38.5);
  S.rect(lot, C.lotAsph);
  const p = packLot(lot, { dir: 'v', door: { u: 0, v: 40 }, backLane: true, frontLane: true });
  S.lot(p);
  S.cut(-23, K.throat, hw).cut(23, K.throat, hw);
  S.rect(R(-32, 38.5, 32, 41), C.walk).bld(R(-24, 41, 24, 60), 'store');
  S.prop(26, 61, 'D', 'PolygonProRacer SM_Prop_Dumpster_01').prop(30, hw + 3.5, 'S', 'PolygonCity SM_Prop_Sign_Parking_01').prop(-3, 39.6, 'C', 'PolygonProRacer SM_Prop_Bollard_01');
  S.dim([-32, 30], [-32 + K.stallL, 30], 'stall', 0).dim([-32 + K.stallL + 0.6, 30], [-32 + K.stallL + 0.6 + K.aisle2, 30], `aisle ${m1(K.aisle2)}`, 0);
  S.note = `${p.count} stalls ${m1(K.stallW)} x ${m1(K.stallL)} m, ${p.ada} accessible (blue, van every 6th, hatched aisle), 90-degree two-way aisles, end islands with lamps, ${m1(K.throat)} m throats.`; return S;
};

sketches['retail-side'] = () => {
  const S = new Sheet('Retail with a side lot (the most common measured position)', R(-34, -8, 34, 52), 3.8);
  const hw = 6.0; S.street(hw);
  S.bld(R(-30, hw + 1.65 + 3, -4, 40), 'shop');
  const lot = R(-2, hw + 1.65 + 1.5, 32, 44);
  S.rect(lot, C.lotAsph);
  const p = packLot(lot, { dir: 'v', door: { u: -4, v: 14 } }); S.lot(p);
  S.cut(10.7, K.throat, hw);
  S.prop(31, 46, 'D', 'PolygonProRacer SM_Prop_Dumpster_01').prop(-3, 12, 'C', 'PolygonProRacer SM_Prop_Bollard_01');
  S.note = `${p.count} stalls; the aisle runs away from the street so the entrance leads straight in; accessible stalls by the side door.`; return S;
};

sketches['fast-food'] = () => {
  const S = new Sheet('Fast food: drive-through lane wrapping the building', R(-34, -8, 34, 56), 3.8);
  const hw = 6.0; S.street(hw);
  S.rect(R(-30, hw + 3.2, 30, 52), C.lotAsph);
  S.bld(R(-8, 20, 10, 36), 'restaurant');
  const lane = [[18, hw + 4], [18, 44], [-14, 44], [-14, 14], [10, 14]];
  S.pl(lane, C.yellow, 2, { dash: '6 3' }).text(-13, 46, 'one-way 3.7 m drive-through: order board, then window; 8 cars of stacking', 8);
  const p = packLot(R(-31, hw + 3.2, -15, 50), { dir: "v", frontLane: false, backLane: false, singleOk: true, door: { u: -8, v: 28 } }); S.lot(p);
  S.cut(18, 4.5, hw).cut(-22, K.throat, hw);
  S.prop(-2, 44.8, 'M', 'menu board (own model)').prop(10.5, 30, 'W', 'pickup window (building model)').prop(26, 50, 'D', 'PolygonProRacer SM_Prop_Dumpster_01');
  S.note = 'Entrance-only throat for the drive-through, two-way throat for the lot; stacking lane kerbed off from the stalls.'; return S;
};

sketches.fuel = () => {
  const S = new Sheet('Fuel station: canopy parallel to the road, shop behind (measured medians)', R(-34, -8, 34, 52), 3.8);
  const hw = 6.0; S.street(hw);
  S.rect(R(-30, hw + 1.65, 30, 48), C.concrete);
  const cv0 = hw + 8.6, cv1 = cv0 + 10; // canopy 24 x 10 m (measured p75 of both sides), 8.6 m from the road edge (p50)
  for (let i = 0; i < 3; i++) { const u = -7.5 + i * 7.5; S.rect(R(u - 0.6, cv0 + 2, u + 0.6, cv1 - 2), '#e8e4d8', { stroke: '#555' }); S.rect(R(u - 0.4, cv0 + 3, u + 0.4, cv0 + 4), C.prop).rect(R(u - 0.4, cv1 - 4, u + 0.4, cv1 - 3), C.prop); }
  S.rect(R(-12, cv0, 12, cv1), C.canopy, { stroke: '#888', dash: '4 2' }).text(-11.5, cv1 + 1, 'canopy 24 x 10 m (dashed), 3 islands 7.5 m apart', 8);
  S.bld(R(-10, cv1 + 5.6, 10, cv1 + 19.6), 'shop 20 x 14');
  const p = packLot(R(-29.5, cv1 + 2, -12, 47.5), { dir: 'v', frontLane: false, backLane: false, door: { u: -10, v: cv1 + 12 } }); S.lot(p);
  S.cut(-21, 10, hw, K.walk, C.concrete).cut(21, 10, hw, K.walk, C.concrete);
  S.prop(26, hw + 3, 'S', 'price pylon (own model)').prop(25, cv1 + 10, 'A', 'air and water (own model)').prop(16, cv1 + 18, 'D', 'PolygonProRacer SM_Prop_Dumpster_01')
    .prop(10.5, cv1 + 8, 'I', 'ice chest / propane cage (own model)').prop(12.5, cv0 + 1, 'B', 'PolygonProRacer SM_Prop_Bollard_01').prop(-13, cv0 + 5, 'T', 'PolygonCity SM_Prop_Trashbin_01');
  S.dim([-12, cv0 - 1.5], [12, cv0 - 1.5], '24 m', -2).dim([31, hw], [31, cv0], '8.6 m', 0.5);
  S.note = 'Concrete forecourt; two wide (10 m) entrance aprons about 42 m apart; shop 5.6 m behind the canopy; pumps are islands under the canopy (lane A models them: Synty has no fuel pump).'; return S;
};

sketches.warehouse = () => {
  const S = new Sheet('Warehouse or depot: docks on the side, truck apron, fenced yard, staff lot in front', R(-44, -8, 64, 92), 2.8);
  const hw = 6.0; S.street(hw, K.walk, 'rural');
  S.rect(R(-40, hw + 1.2, 60, 88), C.concrete);
  S.bld(R(-36, 30, 4, 80), 'warehouse').bld(R(-36, 18, -16, 30), 'office', C.bld2);
  for (let v = 34; v < 78; v += 4) S.rect(R(4, v, 5.2, v + 3), '#333').text(5.6, v + 1.2, '', 6);
  S.text(6, 79.5, 'docks 4 m apart', 8).dim([5, 56], [45, 56], 'apron 40 m', -4);
  for (let v = 28; v < 84; v += 3.7) S.rect(R(46, v, 59, v + 3.6), 'none', { stroke: C.line, sw: 1 });
  S.text(46, 85, 'trailer stalls 3.7 x 16 m', 8);
  S.pl([[-40, 16], [-40, 88], [60, 88], [60, 16], [12, 16]], C.fence, 2, { dash: '4 2' }).prop(6, 16, 'G', 'PolygonProRacer SM_Prop_Boom_Gate_01');
  const p = packLot(R(-15, hw + 3, 2, 29), { dir: 'u', door: { u: -16, v: 24 }, frontLane: false, backLane: false, singleOk: false }); S.lot(p);
  S.cut(-7, K.throat, hw, 1.2, C.concrete).cut(9, K.throatTruck, hw, 1.2, C.concrete);
  S.prop(-38, 84, 'D', 'PolygonCity SM_Prop_Skip_01').prop(40, 84, 'P', 'PolygonCity SM_Prop_Pallet_01').prop(-2, 82, 'X', 'PolygonGeneric SM_Gen_Prop_Barrel_Metal_01');
  S.note = 'Truck entrance 12 m with large flares; dock face on the side away from the street (measured: docks mostly side/rear); staff stalls by the office.'; return S;
};

sketches.farm = () => {
  const S = new Sheet('Farm: long gravel drive, house near the road, barnyard behind', R(-60, -8, 80, 150), 2.2);
  const hw = 3.5; S.street(hw, 0, 'rural');
  S.pl([[20, hw], [20, 60], [10, 95]], C.gravel, 4 * 2.2);
  S.pg([[-20, 85], [55, 80], [60, 140], [-25, 145]], C.gravel, { op: 0.8 });
  S.bld(R(26, 40, 38, 52), 'house').bld(R(-10, 110, 14, 140), 'barn', '#8f3b2e').bld(R(25, 120, 55, 136), 'machine shed', '#8a8f94');
  S.dot(-6, 92, 14, '#c9c6bf').dot(8, 92, 10, '#c9c6bf').text(-12, 92, 'grain bins', 8);
  S.pl([[-55, 60], [-55, 140], [-25, 145]], C.fence, 2, { dash: '4 2' }).rect(R(-55, 140, -25, 148), 'none');
  S.prop(23, hw + 2, 'M', 'PolygonCity SM_Prop_Mailbox_01').prop(40, 100, 'F', 'fuel tank (own model)').prop(-15, 100, 'H', 'hay bales (own model)').prop(45, 90, 'X', 'PolygonGeneric SM_Gen_Prop_Barrel_Metal_01');
  S.note = 'Gravel drive and yard; the drive length, yard size and building mix come from the measured farmyards; fences around paddocks; no kerbs.'; return S;
};

sketches.quarry = () => {
  const S = new Sheet('Quarry: gate, scale house, haul road, stockpiles, pit', R(-60, -8, 110, 170), 2.0);
  const hw = 4.0; S.street(hw, 0, 'rural');
  S.pl([[0, hw], [0, 40], [30, 80], [40, 120]], C.dirt, 9 * 2.0);
  S.pg([[-40, 100], [100, 90], [105, 165], [-45, 165]], C.dirt, { op: 0.85 });
  S.pg([[10, 120], [90, 110], [95, 160], [5, 162]], '#8c7a63', { stroke: '#5a4a3a' }).text(30, 140, 'pit (benches)', 10, { fill: '#fff' });
  S.bld(R(8, 28, 16, 34), 'scale', C.bld2).rect(R(-6, 25, 6, 43), '#555').text(-5, 44, 'truck scale 3.5 x 18 m', 8);
  for (const [u, v, r] of [[-25, 110, 9], [-5, 104, 7], [60, 100, 8]]) S.dot(u, v, r * 2.0, '#9d917f');
  S.bld(R(70, 80, 85, 90), 'crusher', '#8a8f94');
  S.pl([[-50, 20], [-8, 20]], C.fence, 2).pl([[8, 20], [100, 20]], C.fence, 2).prop(0, 20, 'G', 'PolygonProRacer SM_Prop_Boom_Gate_01').prop(-12, 60, 'C', 'PolygonCity SM_Prop_Cone_01');
  S.note = 'Dirt and gravel throughout, a 9 m haul road with a 15 m paved apron at the public road (mud tracking), scale by the gate.'; return S;
};

sketches.mill = () => {
  const S = new Sheet('Sawmill: log yard, saw shed, lumber stacks, loading', R(-50, -8, 90, 120), 2.4);
  const hw = 4.5; S.street(hw, 0, 'rural');
  S.rect(R(-45, hw + 1.2, 85, 115), C.gravel, { op: 0.9 });
  S.bld(R(0, 50, 50, 70), 'saw shed', '#7d6a55').bld(R(-35, 20, -15, 32), 'office', C.bld2).bld(R(55, 60, 80, 100), 'kiln / shed', '#8a8f94');
  for (let i = 0; i < 6; i++) S.rect(R(-40 + i * 6, 75, -36 + i * 6, 105), '#a07850');
  S.text(-40, 107, 'log decks', 8);
  for (let i = 0; i < 5; i++) S.rect(R(5 + i * 9, 80, 12 + i * 9, 92), '#d8c39a', { stroke: '#8a6a3a' });
  S.text(5, 94, 'lumber stacks', 8);
  S.cut(10, K.throatTruck, hw, 1.2, C.gravel).prop(-20, 40, 'X', 'PolygonGeneric SM_Gen_Prop_Barrel_Metal_01').prop(52, 45, 'P', 'PolygonCity SM_Prop_Pallet_01');
  S.note = 'Gravel yard; truck entrance; grain elevators use the same frame with a rail siding, a truck scale and a concrete apron at the pit shed.'; return S;
};

sketches.harbor = () => {
  const S = new Sheet('Marina or harbour: ramp, trailer parking, piers', R(-50, -8, 70, 110), 2.6);
  const hw = 4.5; S.street(hw);
  S.rect(R(-50, 70, 70, 110), C.water);
  S.rect(R(-45, hw + 1.65, 65, 70), C.gravel, { op: 0.9 });
  for (let i = 0; i < 6; i++) S.rect(R(-40, 14 + i * 3.6, -28, 17.5 + i * 3.6), 'none', { stroke: C.line, sw: 1 });
  S.text(-40, 37, 'trailer stalls 3.6 x 12 m', 8);
  const p = packLot(R(-22, hw + 3, 20, 40), { dir: 'u', frontLane: false, backLane: false, door: { u: 30, v: 30 } }); S.lot(p);
  S.rect(R(30, 55, 38, 90), C.concrete).text(39, 85, 'ramp', 8).rect(R(-10, 66, -7, 105), '#a07850').rect(R(10, 66, 13, 100), '#a07850');
  for (let v = 72; v < 100; v += 6) S.rect(R(-16, v, -10, v + 1.2), '#a07850').rect(R(13, v, 19, v + 1.2), '#a07850');
  S.bld(R(40, 20, 56, 32), 'bait shop').prop(28, 52, 'S', 'PolygonCity SM_Prop_Sign_Warning_01').prop(-30, 60, 'B', 'PolygonCity SM_Prop_ParkBench_01');
  S.note = 'Gravel lot with long trailer stalls; concrete ramp; timber piers; a shop or harbour office by the entrance.'; return S;
};

sketches.civic = () => {
  const S = new Sheet('Church, school or civic building: side lot and drop-off', R(-40, -8, 50, 64), 3.4);
  const hw = 4.5; S.street(hw);
  S.bld(R(-34, 14, -4, 50), 'church');
  S.rect(R(-4, hw + 1.65, 0, 14), C.walk);
  const lot = R(2, hw + 3, 46, 58); S.rect(lot, C.lotAsph);
  const p = packLot(lot, { dir: 'v', door: { u: -4, v: 20 } }); S.lot(p);
  S.cut(14.5, K.throat, hw).cut(33, K.throat, hw);
  S.prop(-36, 10, 'F', 'flagpole (own model)').prop(-6, 12, 'B', 'PolygonCity SM_Prop_ParkBench_01');
  S.note = 'Schools add a one-way bus and parent loop along the front with a 3 m kerbside drop-off lane.'; return S;
};

sketches['car-repair'] = () => {
  const S = new Sheet('Car repair garage: concrete apron to the bays, fenced storage behind', R(-30, -8, 40, 56), 3.8);
  const hw = 6.0; S.street(hw);
  S.rect(R(-26, hw + 1.65, 36, 22), C.concrete);
  S.bld(R(-20, 22, 20, 38), 'garage');
  for (const u of [-14, -4, 6]) S.rect(R(u, 21.6, u + 6, 22.4), '#333');
  S.rect(R(-26, 38, 36, 54), C.gravel).pl([[-26, 38], [-26, 54], [36, 54], [36, 38], [22, 38]], C.fence, 2, { dash: '3 2' });
  for (let u = 22; u < 35; u += 2.7) S.rect(R(u, 26, u + 2.7, 31.5), 'none', { stroke: C.line, sw: 1 });
  S.cut(-12, 9, hw, K.walk, C.concrete).cut(20, 9, hw, K.walk, C.concrete);
  S.prop(24, 44, 'T', 'PolygonProRacer SM_Prop_Tyre_Stack_01').prop(30, 46, 'X', 'PolygonGeneric SM_Gen_Prop_Barrel_Metal_01').prop(-24, 30, 'J', 'PolygonProRacer SM_Prop_Jack_Stand_01')
    .prop(34, hw + 3, 'S', 'PolygonCity SM_Prop_Sign_Entrance_01');
  S.note = 'Bay doors face the street (or the side), a full-width concrete apron, customer stalls beside, cars waiting for repair in the fenced gravel yard.'; return S;
};

sketches.motel = () => {
  const S = new Sheet('Motel: L-shaped building around its front lot', R(-40, -8, 44, 60), 3.4);
  const hw = 6.0; S.street(hw);
  S.bld(R(-36, 40, 40, 52), 'rooms').bld(R(24, 10, 40, 40), '', C.bld).bld(R(-36, 12, -26, 22), 'office', C.bld2);
  const lot = R(-24, hw + 3, 22, 38.5); S.rect(lot, C.lotAsph);
  const p = packLot(lot, { dir: 'u', door: { u: -26, v: 18 }, frontLane: true, backLane: false }); S.lot(p);
  S.cut(-30, K.throat, hw);
  S.prop(-34, hw + 3, 'S', 'motel sign (own model)').prop(20, 39.4, 'I', 'ice machine (own model)');
  S.note = 'About one stall per room, rows along the room doors, entrance by the office.'; return S;
};

function main() {
  fs.mkdirSync(OUT, { recursive: true });
  const names = [], sheets = [];
  for (const [k, f] of Object.entries(sketches)) { const S = f(); fs.writeFileSync(path.join(OUT, k + '.svg'), S.svg()); names.push(k); sheets.push(S); }
  const tile = 300, cols = 4, gap = 10, rows = Math.ceil(sheets.length / cols);
  const cv = new Canvas(cols * (tile + gap) + gap, rows * (tile + gap) + gap);
  sheets.forEach((S, i) => { const sc = Math.min(tile / (S.b.u1 - S.b.u0), tile / (S.b.v1 - S.b.v0)); S.raster(cv, gap + (i % cols) * (tile + gap), gap + Math.floor(i / cols) * (tile + gap), sc); });
  fs.mkdirSync(path.join(ROOT, 'artifacts', 'osm'), { recursive: true });
  fs.writeFileSync(path.join(ROOT, 'artifacts', 'osm', 'sketches.png'), png(cv.w, cv.h, cv.d));
  console.log('wrote', names.length, 'sketches to', OUT, 'with', JSON.stringify(K));
}
main();
