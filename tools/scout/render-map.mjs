#!/usr/bin/env node
// Renders the scout overview map of a candidate town (decision U25) and measures it.
// Zero dependencies (the optional PNG uses the Edge or Chrome already on this PC, headless).
//
//   node tools/scout/render-map.mjs <town-id> [...] [--all] [--png] [--summary]
//
// Reads artifacts/scout/<town>/{osm.json, dem.f32, dem.json, coverage.json} (fetch-osm.mjs, fetch-dem.mjs).
// Writes docs/research/new-world/<town>.svg (and .png with --png) and artifacts/scout/<town>/stats.json.
// --summary prints a markdown table of every town's stats.json.
//
// Map: hillshade and hypsometric tint from the DEM, contours every 10 m (bold every 50 m), land use,
// water, roads by class, railways, building footprints, bridges, the 1 km and 2 km play-area squares,
// and labelled candidate sites for the game's places (mill, quarry, fuel, depot, landing, farms,
// wetland, woods tracks, bridges). Sites found in OSM are solid; sites the game would add (towns.json
// "sites" with "add": true) are dashed.
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawnSync } from 'node:child_process';
import { REPO, getTown, townFrame, townDir, townIds, flag, loadTowns } from './geo.mjs';
import { loadOsm, roadClass, isUnpaved, ringArea, polyLenIn, segIntersect, pointInRings, inBox, rasterize } from './osm.mjs';
import { encodePNG } from './png.mjs';

const OUT_DIR = path.join(REPO, 'docs', 'research', 'new-world');
const MAP = 2000, PAD = 24, PANEL = 660;
const SVG_W = PAD + MAP + PAD + PANEL + PAD, SVG_H = PAD + MAP + PAD;

const CREDIT_OSM = '(c) OpenStreetMap contributors, ODbL';
const CREDIT_DEM = 'USGS 3D Elevation Program (public domain)';

// ---------------------------------------------------------------- DEM
function loadDem(dir) {
  const meta = JSON.parse(fs.readFileSync(path.join(dir, 'dem.json'), 'utf8'));
  const b = fs.readFileSync(path.join(dir, 'dem.f32'));
  const z = new Float32Array(b.buffer.slice(b.byteOffset, b.byteOffset + b.length));
  const { width: W, height: H, res } = meta;
  let fill = meta.min;
  for (let i = 0; i < z.length; i++) if (Number.isNaN(z[i])) z[i] = fill; else fill = z[i];
  const at = (x, y) => { // bilinear, local metres
    const c = Math.min(W - 1.001, Math.max(0, x / res - 0.5)), r = Math.min(H - 1.001, Math.max(0, (meta.size - y) / res - 0.5));
    const i = Math.floor(c), j = Math.floor(r), u = c - i, v = r - j;
    const k = j * W + i;
    return (z[k] * (1 - u) + z[k + 1] * u) * (1 - v) + (z[k + W] * (1 - u) + z[k + W + 1] * u) * v;
  };
  return { meta, z, W, H, res, at };
}

function boxStatsDem(dem, box) {
  const { z, W, res, meta } = dem, vals = [];
  let slopeSum = 0, steep = 0, n = 0;
  for (let j = 1; j < W - 1; j++) {
    const y = meta.size - (j + 0.5) * res;
    if (y < box[1] || y > box[3]) continue;
    for (let i = 1; i < W - 1; i++) {
      const x = (i + 0.5) * res;
      if (x < box[0] || x > box[2]) continue;
      const k = j * W + i;
      vals.push(z[k]);
      const gx = (z[k + 1] - z[k - 1]) / (2 * res), gy = (z[k - W] - z[k + W]) / (2 * res);
      const s = Math.hypot(gx, gy);
      slopeSum += s; if (s > 0.25) steep++; n++;
    }
  }
  vals.sort((a, b) => a - b);
  const q = (p) => vals[Math.min(vals.length - 1, Math.floor(p * vals.length))];
  return { min: +q(0).toFixed(1), max: +q(1).toFixed(1), p2: +q(0.02).toFixed(1), p98: +q(0.98).toFixed(1), relief: +(q(1) - q(0)).toFixed(1),
    meanSlopePct: +(100 * slopeSum / n).toFixed(1), steepPct: +(100 * steep / n).toFixed(1) };
}

// Hillshade (NW light) over a hypsometric tint, as RGB pixels.
function hillshadeImage(dem) {
  const { z, W, H, res } = dem;
  let lo = Infinity, hi = -Infinity;
  for (const v of z) { if (v < lo) lo = v; if (v > hi) hi = v; }
  const zf = 1.8, az = 315 * Math.PI / 180, alt = 42 * Math.PI / 180;
  const L = [Math.sin(az) * Math.cos(alt), Math.cos(az) * Math.cos(alt), Math.sin(alt)];
  const stops = [[0, [168, 196, 146]], [0.3, [208, 214, 160]], [0.6, [214, 190, 146]], [0.85, [206, 176, 150]], [1, [238, 230, 220]]];
  const tint = (t) => {
    for (let s = 1; s < stops.length; s++) if (t <= stops[s][0]) {
      const [t0, c0] = stops[s - 1], [t1, c1] = stops[s], u = (t - t0) / (t1 - t0);
      return c0.map((c, i) => c + (c1[i] - c) * u);
    }
    return stops[stops.length - 1][1];
  };
  const px = new Uint8Array(W * H * 3);
  const g = (i, j) => z[Math.min(H - 1, Math.max(0, j)) * W + Math.min(W - 1, Math.max(0, i))];
  for (let j = 0; j < H; j++) for (let i = 0; i < W; i++) {
    const gx = ((g(i + 1, j - 1) + 2 * g(i + 1, j) + g(i + 1, j + 1)) - (g(i - 1, j - 1) + 2 * g(i - 1, j) + g(i - 1, j + 1))) / (8 * res);
    const gy = ((g(i - 1, j - 1) + 2 * g(i, j - 1) + g(i + 1, j - 1)) - (g(i - 1, j + 1) + 2 * g(i, j + 1) + g(i + 1, j + 1))) / (8 * res);
    const nx = -gx * zf, ny = -gy * zf, nn = Math.hypot(nx, ny, 1);
    const hs = Math.max(0, (nx * L[0] + ny * L[1] + L[2]) / nn);
    const c = tint((g(i, j) - lo) / Math.max(1, hi - lo));
    const k = (j * W + i) * 3, m = 0.32 + 0.78 * hs;
    for (let ch = 0; ch < 3; ch++) px[k + ch] = Math.max(0, Math.min(255, Math.round(c[ch] * m)));
  }
  return encodePNG(W, H, 3, px);
}

// Marching squares on a lightly smoothed DEM; returns [{ level, lines: [[x,y]...] }] in local metres.
function contours(dem, step) {
  const { W, H, res, meta } = dem;
  const s = new Float32Array(W * H);
  for (let j = 0; j < H; j++) for (let i = 0; i < W; i++) {
    let sum = 0, n = 0;
    for (let b = -1; b <= 1; b++) for (let a = -1; a <= 1; a++) {
      const ii = i + a, jj = j + b;
      if (ii >= 0 && jj >= 0 && ii < W && jj < H) { sum += dem.z[jj * W + ii]; n++; }
    }
    s[j * W + i] = sum / n;
  }
  let lo = Infinity, hi = -Infinity;
  for (const v of s) { if (v < lo) lo = v; if (v > hi) hi = v; }
  const out = [];
  const TABLE = { 1: [[3, 2]], 2: [[2, 1]], 3: [[3, 1]], 4: [[0, 1]], 5: [[3, 0], [2, 1]], 6: [[0, 2]], 7: [[3, 0]], 8: [[3, 0]], 9: [[0, 2]], 10: [[0, 1], [3, 2]], 11: [[0, 1]], 12: [[3, 1]], 13: [[2, 1]], 14: [[3, 2]] };
  for (let level = Math.ceil(lo / step) * step; level <= hi; level += step) {
    const segs = [], byEdge = new Map();
    const edgeId = (i, j, e) => (e === 0 ? 2 * (j * W + i) : e === 2 ? 2 * ((j + 1) * W + i) : e === 3 ? 2 * (j * W + i) + 1 : 2 * (j * W + i + 1) + 1);
    for (let j = 0; j < H - 1; j++) for (let i = 0; i < W - 1; i++) {
      const tl = s[j * W + i] >= level, tr = s[j * W + i + 1] >= level, br = s[(j + 1) * W + i + 1] >= level, bl = s[(j + 1) * W + i] >= level;
      const c = (tl << 3) | (tr << 2) | (br << 1) | bl;
      if (c === 0 || c === 15) continue;
      for (const [ea, eb] of TABLE[c]) {
        const a = edgeId(i, j, ea), b = edgeId(i, j, eb), k = segs.length;
        segs.push([a, b]);
        for (const e of [a, b]) { const l = byEdge.get(e); if (l) l.push(k); else byEdge.set(e, [k]); }
      }
    }
    const used = new Uint8Array(segs.length);
    const pt = (e) => {
      const v = e & 1, idx = e >> 1, i = idx % W, j = (idx - i) / W;
      const z0 = s[idx], z1 = v ? s[idx + W] : s[idx + 1];
      const t = (level - z0) / (z1 - z0);
      const ci = v ? i : i + t, cj = v ? j + t : j;
      return [(ci + 0.5) * res, meta.size - (cj + 0.5) * res];
    };
    const lines = [];
    for (let k0 = 0; k0 < segs.length; k0++) {
      if (used[k0]) continue;
      used[k0] = 1;
      const chain = [segs[k0][0], segs[k0][1]];
      for (const dir of [1, -1]) {
        for (;;) {
          const end = dir === 1 ? chain[chain.length - 1] : chain[0];
          const nxt = (byEdge.get(end) ?? []).find((k) => !used[k]);
          if (nxt === undefined) break;
          used[nxt] = 1;
          const other = segs[nxt][0] === end ? segs[nxt][1] : segs[nxt][0];
          if (dir === 1) chain.push(other); else chain.unshift(other);
        }
      }
      if (chain.length >= 4) lines.push(simplify(chain.map(pt), 1.5));
    }
    out.push({ level, lines });
  }
  return out;
}

function simplify(pts, tol) {
  if (pts.length < 3) return pts;
  const keep = new Uint8Array(pts.length); keep[0] = keep[pts.length - 1] = 1;
  const stack = [[0, pts.length - 1]];
  while (stack.length) {
    const [a, b] = stack.pop();
    let best = -1, bd = tol;
    const [ax, ay] = pts[a], [bx, by] = pts[b], dx = bx - ax, dy = by - ay, L = Math.hypot(dx, dy) || 1e-9;
    for (let i = a + 1; i < b; i++) {
      const d = Math.abs(dy * pts[i][0] - dx * pts[i][1] + bx * ay - by * ax) / L;
      if (d > bd) { bd = d; best = i; }
    }
    if (best > 0) { keep[best] = 1; stack.push([a, best], [best, b]); }
  }
  return pts.filter((_, i) => keep[i]);
}

// ---------------------------------------------------------------- features
const nm = (t) => t.name ?? '';
const RX = {
  mill: /\b(mill|mills|grist|woolen|woollen|feed mill|sawmill|saw mill)\b/i,
  quarry: /\b(quarry|quarries|gravel|sand pit|aggregate|stone co|crushed|asphalt|pit)\b/i,
  landing: /\b(landing|boat launch|boat ramp|launch|access area|fishing access|river access|canoe|kayak|put[- ]?in|take[- ]?out|marina|ferry|boat access)\b/i,
  depot: /\b(depot|yard|warehouse|freight|elevator|feed|co-?op|lumber|supply|works|plant|industr)/i,
};

function depotLabel(t) {
  const n = t.name ?? '';
  if (/lumber|building supply|builders/i.test(n) || t.shop === 'trade') return 'lumber / builders yard';
  if (/elevator|grain/i.test(n)) return 'grain elevator';
  if (/feed|co-?op|agri|farm supply/i.test(n) || ['agrarian', 'farm'].includes(t.shop)) return 'feed / farm supply';
  if (/creamery|dairy|cheese/i.test(n)) return 'creamery';
  if (/concrete|asphalt|gravel|aggregate|stone/i.test(n)) return 'concrete / aggregate yard';
  if (t.landuse === 'railway') return 'rail yard';
  if (t.landuse === 'industrial') return 'industrial yard';
  if (t.man_made === 'works') return 'works';
  if (t.building === 'warehouse') return 'warehouse';
  return 'industrial building';
}

function classify(osm, town) {
  const own = new RegExp(`\\b${town.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'i');
  const named = (t, rx) => t.name && rx.test(t.name) && !(town.name.match(RX.mill) && own.test(t.name) && !rx.test(t.name.replace(own, '')));
  const notRoad = (t) => !t.highway && !t.place && !t.boundary && !t.route && !t.railway;
  const all = [...osm.points.map((e) => ({ ...e, kind: 'point', c: e.p })), ...osm.areas.map((e) => ({ ...e, kind: 'area' })),
    ...osm.lines.filter((e) => !e.fromArea).map((e) => ({ ...e, kind: 'line', c: e.pts[Math.floor(e.pts.length / 2)] }))];
  const F = { mill: [], dam: [], hydro: [], quarry: [], fuel: [], depot: [], rail: [], landing: [], farm: [], wetland: [], track: [] };
  for (const e of all) {
    const t = e.tags;
    if (!t || !Object.keys(t).length) continue;
    const lab = nm(t);
    if ((['mill', 'watermill', 'windmill'].includes(t.historic) || ['watermill', 'windmill'].includes(t.man_made) || t.building === 'mill' || t.industrial === 'mill' ||
      (notRoad(t) && named(t, RX.mill) && (t.building || t.historic || t.man_made || t.industrial || t.landuse || t.craft || t.ruins || t.tourism) && !t.waterway) ||
      (['dam', 'weir'].includes(t.waterway) && named(t, RX.mill)))) F.mill.push({ e, label: t.waterway ? `${lab} (mill dam)` : lab || 'mill' });
    if (['dam', 'weir'].includes(t.waterway)) F.dam.push({ e, label: lab || t.waterway });
    if (t.power && (t['plant:source'] === 'hydro' || t['generator:source'] === 'hydro')) F.hydro.push({ e, label: lab || 'hydro station' });
    if (t.landuse === 'quarry' || t['disused:landuse'] === 'quarry' || t['abandoned:landuse'] === 'quarry' || ['quarry', 'mine', 'mining'].includes(t.industrial) ||
      ['mineshaft', 'adit'].includes(t.man_made) || ((t.landuse === 'industrial' || t.man_made === 'works' || t.industrial) && named(t, RX.quarry))) {
      F.quarry.push({ e, label: lab || (t.landuse === 'quarry' ? 'quarry / gravel pit' : 'old quarry') });
    }
    if (t.amenity === 'fuel') F.fuel.push({ e, label: 'fuel station' }); // generic: station names are often brands
    if ((t.landuse === 'industrial' && e.kind === 'area' && e.area >= 1500) || t.man_made === 'works' || t.landuse === 'railway' ||
      (['warehouse', 'industrial'].includes(t.building) && e.kind === 'area' && e.area >= 600) || (t.shop && ['agrarian', 'farm', 'trade'].includes(t.shop))) {
      F.depot.push({ e, label: depotLabel(t) }); // generic: yard names are often company names
    }
    if (['station', 'halt', 'yard', 'goods'].includes(t.railway) || t.building === 'train_station' || t.historic === 'railway_station' || t.landuse === 'railway' ||
      (t.public_transport === 'station' && t.train === 'yes')) F.rail.push({ e, label: t.historic === 'railway_station' ? 'old railway station' : ['yard', 'goods'].includes(t.railway) || t.landuse === 'railway' ? 'rail yard' : 'railway station' });
    const putIn = /put_in|egress/.test(`${t.canoe ?? ''};${t.whitewater ?? ''}`);
    if (['slipway', 'marina'].includes(t.leisure) || ['boat_rental', 'ferry_terminal'].includes(t.amenity) || t.waterway === 'access_point' || (putIn && e.kind !== 'line') ||
      (t.leisure === 'fishing' && e.kind !== 'line') || (notRoad(t) && named(t, RX.landing) && !t.waterway)) {
      F.landing.push({ e, label: lab || (t.leisure === 'slipway' ? 'boat launch' : t.waterway === 'access_point' || putIn ? 'canoe access' : t.leisure ?? t.amenity ?? 'landing') });
    }
    if (['farmyard', 'orchard', 'vineyard'].includes(t.landuse) || ['barn', 'farm', 'farm_auxiliary', 'silo', 'cowshed', 'stable', 'greenhouse'].includes(t.building) ||
      t.place === 'farm' || t.man_made === 'silo' || (t.landuse === 'farmland' && e.kind === 'area' && e.area >= 20000)) F.farm.push({ e, label: lab || (t.landuse ?? t.building ?? t.place ?? 'farm') });
    if (t.natural === 'wetland' && e.kind === 'area') F.wetland.push({ e, label: lab || (t.wetland ? `wetland (${t.wetland})` : 'wetland') });
    if (e.kind === 'line' && (t.highway === 'track' || (roadClass(t) && roadClass(t) !== 'path' && isUnpaved(t)))) F.track.push({ e, label: lab || (t.highway === 'track' ? 'track' : `unpaved ${t.highway}`) });
  }
  return F;
}

function bridges(osm) {
  const parts = osm.lines.filter((l) => l.tags.bridge && l.tags.bridge !== 'no' && (roadClass(l.tags) && roadClass(l.tags) !== 'path' || ['rail', 'abandoned', 'disused', 'light_rail', 'narrow_gauge'].includes(l.tags.railway)));
  const mids = parts.map((l) => l.pts[Math.floor(l.pts.length / 2)]);
  const grp = parts.map((_, i) => i);
  const find = (i) => (grp[i] === i ? i : (grp[i] = find(grp[i])));
  for (let i = 0; i < parts.length; i++) for (let j = i + 1; j < parts.length; j++) {
    const near = Math.hypot(mids[i][0] - mids[j][0], mids[i][1] - mids[j][1]) < 45 ||
      [parts[i].pts[0], parts[i].pts.at(-1)].some((p) => [parts[j].pts[0], parts[j].pts.at(-1)].some((q) => Math.hypot(p[0] - q[0], p[1] - q[1]) < 1));
    if (near && !!parts[i].tags.railway === !!parts[j].tags.railway) grp[find(i)] = find(j);
  }
  const rivers = osm.lines.filter((l) => ['river', 'canal'].includes(l.tags.waterway));
  const streams = osm.lines.filter((l) => ['stream', 'brook', 'ditch', 'drain', 'tidal_channel'].includes(l.tags.waterway));
  const water = osm.areas.filter((a) => a.tags.natural === 'water' || a.tags.waterway === 'riverbank' || a.tags.water || ['reservoir', 'basin'].includes(a.tags.landuse));
  const crosses = (segs, lines) => lines.some((l) => segs.some(([a, b]) => { for (let k = 1; k < l.pts.length; k++) if (segIntersect(a, b, l.pts[k - 1], l.pts[k])) return true; return false; }));
  const clusters = new Map();
  parts.forEach((p, i) => { const r = find(i); if (!clusters.has(r)) clusters.set(r, []); clusters.get(r).push(p); });
  const out = [];
  for (const ps of clusters.values()) {
    const segs = ps.flatMap((p) => p.pts.slice(1).map((q, k) => [p.pts[k], q]));
    const len = segs.reduce((s, [a, b]) => s + Math.hypot(b[0] - a[0], b[1] - a[1]), 0);
    const all = ps.flatMap((p) => p.pts);
    const c = [all.reduce((s, p) => s + p[0], 0) / all.length, all.reduce((s, p) => s + p[1], 0) / all.length];
    let over = 'other';
    if (crosses(segs, rivers) || water.some((w) => pointInRings(c[0], c[1], w.rings))) over = 'river';
    else if (crosses(segs, streams)) over = 'stream';
    const t = ps[0].tags;
    const old = ['abandoned', 'disused', 'razed', 'dismantled'].includes(t.railway) || (!!t.railway && ['cycleway', 'path', 'footway'].includes(t.highway));
    out.push({ c, over, len: Math.round(len), rail: !!t.railway, old, name: t.name ?? t.ref ?? '', road: t.highway ?? `railway=${t.railway}`, parts: ps.map((p) => p.id) });
  }
  return out;
}

// ---------------------------------------------------------------- measurements
function measure(osm, dem, F, brs, box, extra = {}) {
  const b = box;
  const buildings = osm.areas.filter((a) => a.tags.building && a.tags.building !== 'no' && inBox(a.c, b));
  const roads = { major: 0, local: 0, service: 0, unpaved: 0, path: 0 };
  for (const l of osm.lines) {
    const rc = roadClass(l.tags);
    if (!rc) continue;
    const len = polyLenIn(l.pts, b) / 1000;
    if (!len) continue;
    if (rc === 'path') roads.path += len;
    else {
      if (['motorway', 'trunk', 'primary', 'secondary'].includes(rc)) roads.major += len;
      else if (rc === 'service') roads.service += len;
      else if (rc !== 'track') roads.local += len;
      if (isUnpaved(l.tags)) roads.unpaved += len;
    }
  }
  let railActive = 0, railOld = 0, river = 0, stream = 0;
  for (const l of osm.lines) {
    const t = l.tags;
    if (['rail', 'light_rail', 'narrow_gauge', 'preserved'].includes(t.railway) && !t.highway) railActive += polyLenIn(l.pts, b) / 1000;
    else if (['abandoned', 'disused', 'razed', 'dismantled'].includes(t.railway) || /rail[- ]?trail/i.test(nm(t))) railOld += polyLenIn(l.pts, b) / 1000;
    if (['river', 'canal'].includes(t.waterway)) river += polyLenIn(l.pts, b) / 1000;
    if (['stream', 'brook'].includes(t.waterway)) stream += polyLenIn(l.pts, b) / 1000;
  }
  // Land cover areas on a 10 m grid inside the box.
  const cs = 10, n = Math.round((b[2] - b[0]) / cs);
  const cover = {};
  const cls = (t) => (['farmland', 'meadow', 'orchard', 'vineyard', 'farmyard', 'grass'].includes(t.landuse) || t.landuse === 'animal_keeping' ? 'farm'
    : t.landuse === 'forest' || t.natural === 'wood' ? 'forest' : t.natural === 'wetland' ? 'wetland'
      : t.natural === 'water' || t.waterway === 'riverbank' || ['reservoir', 'basin'].includes(t.landuse) ? 'water'
        : ['industrial', 'railway'].includes(t.landuse) ? 'industrial' : t.landuse === 'quarry' ? 'quarry' : null);
  for (const a of osm.areas) {
    const k = cls(a.tags);
    if (!k) continue;
    if (!cover[k]) cover[k] = new Uint8Array(n * n);
    rasterize(cover[k], n, cs, a.rings.map((r) => r.map(([x, y]) => [x - b[0], y - b[1]])));
  }
  const ha = {};
  for (const k of ['farm', 'forest', 'wetland', 'water', 'industrial', 'quarry']) ha[k] = cover[k] ? Math.round(cover[k].reduce((s, v) => s + v, 0) * cs * cs / 1e4) : 0;
  // Footprint coverage proxy: share of town-street length with a mapped building within 50 m.
  const grid = new Map(), G = 50;
  for (const a of osm.areas) if (a.tags.building && a.tags.building !== 'no') {
    const k = `${Math.floor(a.c[0] / G)},${Math.floor(a.c[1] / G)}`;
    (grid.get(k) ?? grid.set(k, []).get(k)).push(a.c);
  }
  let samples = 0, hits = 0;
  for (const l of osm.lines) {
    if (!['residential', 'unclassified', 'tertiary', 'secondary', 'primary', 'trunk'].includes(roadClass(l.tags))) continue;
    for (let k = 1; k < l.pts.length; k++) {
      const [a, c] = [l.pts[k - 1], l.pts[k]], L = Math.hypot(c[0] - a[0], c[1] - a[1]);
      for (let d = 5; d < L; d += 10) {
        const p = [a[0] + (c[0] - a[0]) * d / L, a[1] + (c[1] - a[1]) * d / L];
        if (!inBox(p, b)) continue;
        samples++;
        const gi = Math.floor(p[0] / G), gj = Math.floor(p[1] / G);
        let hit = false;
        for (let u = -1; u <= 1 && !hit; u++) for (let v = -1; v <= 1 && !hit; v++) hit = (grid.get(`${gi + u},${gj + v}`) ?? []).some((q) => Math.hypot(q[0] - p[0], q[1] - p[1]) < 50);
        if (hit) hits++;
      }
    }
  }
  const inB = (list) => list.filter((x) => inBox(x.e.c, b));
  const brIn = brs.filter((x) => inBox(x.c, b));
  return {
    buildings: buildings.length,
    buildingsOver70m2: buildings.filter((a) => a.area >= 70).length,
    buildingsOver400m2: buildings.filter((a) => a.area >= 400).length,
    buildingTypedPct: buildings.length ? Math.round(100 * buildings.filter((a) => a.tags.building !== 'yes').length / buildings.length) : 0,
    roadsKm: { drivable: +(roads.major + roads.local + roads.service).toFixed(1), major: +roads.major.toFixed(1), local: +roads.local.toFixed(1), service: +roads.service.toFixed(1),
      unpavedOrTrack: +(roads.unpaved + F.track.filter((x) => x.e.tags.highway === 'track').reduce((s, x) => s + polyLenIn(x.e.pts, b) / 1000, 0)).toFixed(1), path: +roads.path.toFixed(1) },
    railKm: { active: +railActive.toFixed(2), old: +railOld.toFixed(2) },
    riverKm: +river.toFixed(2), streamKm: +stream.toFixed(2),
    bridges: { river: brIn.filter((x) => x.over === 'river' && !x.rail).length, riverRail: brIn.filter((x) => x.over === 'river' && x.rail).length,
      stream: brIn.filter((x) => x.over === 'stream').length, other: brIn.filter((x) => x.over === 'other').length },
    elevation: boxStatsDem(dem, b), ha,
    frontagePct: samples ? Math.round(100 * hits / samples) : null,
    sites: Object.fromEntries(Object.entries(F).map(([k, v]) => [k, inB(v).length])),
    ...extra,
  };
}

function reliefQuick(dem, b) {
  const { z, W, res, meta } = dem;
  let lo = Infinity, hi = -Infinity;
  const i0 = Math.max(0, Math.ceil(b[0] / res)), i1 = Math.min(W - 1, Math.floor(b[2] / res));
  const j0 = Math.max(0, Math.ceil((meta.size - b[3]) / res)), j1 = Math.min(W - 1, Math.floor((meta.size - b[1]) / res));
  for (let j = j0; j <= j1; j += 3) for (let i = i0; i <= i1; i += 3) { const v = z[j * W + i]; if (v < lo) lo = v; if (v > hi) hi = v; }
  return hi - lo;
}

// Picks the play-area centre (offset from frame centre, 100 m steps) that best covers the game's places.
function autoSquare(osm, dem, F, brs, S, side, core, inner = null) {
  const blds = osm.areas.filter((a) => a.tags.building && a.tags.building !== 'no').map((a) => a.c);
  const rivers = osm.lines.filter((l) => ['river', 'canal'].includes(l.tags.waterway));
  let best = null;
  const lim = (S - side) / 2;
  for (let dx = -lim; dx <= lim; dx += 100) for (let dy = -lim; dy <= lim; dy += 100) {
    const cx = S / 2 + dx, cy = S / 2 + dy, b = [cx - side / 2, cy - side / 2, cx + side / 2, cy + side / 2];
    if (inner && (inner[0] < b[0] || inner[1] < b[1] || inner[2] > b[2] || inner[3] > b[3])) continue; // the 2 km square holds the 1 km one
    const nb = blds.filter((c) => inBox(c, b)).length;
    const riverLen = rivers.reduce((s, l) => s + polyLenIn(l.pts, b), 0);
    const has = (k, min = 1) => F[k].filter((x) => inBox(x.e.c, b)).length >= min;
    const rb = brs.filter((x) => x.over === 'river' && inBox(x.c, b)).length;
    let score = 0;
    score += nb >= 40 ? (side <= 1000 ? Math.min(4, 4 - Math.max(0, nb - 300) / 150) : 4) : 4 * nb / 40;
    score += riverLen > side * 0.3 ? 3 : 3 * riverLen / (side * 0.3);
    score += Math.min(3, rb) * 1.2;
    score += (has('mill') || has('dam') || has('hydro') ? 2 : 0) + (has('fuel') ? 1 : 0) + (has('quarry') ? 1 : 0) + (has('depot') ? 1 : 0) +
      (has('rail') ? 0.5 : 0) + (has('landing') ? 1 : 0) + (has('wetland') ? 1 : 0) + (has('farm', 2) ? 1 : 0) + (has('track', 2) ? 1 : 0);
    const rel = reliefQuick(dem, b);
    score += rel <= 200 ? Math.min(2.5, rel / 24) : 2.5 - (rel - 200) / 40; // real hills: 60-200 m
    score -= Math.max(0, Math.hypot(cx - core[0], cy - core[1]) - side * 0.25) / (side * 0.2); // keep the town core in the square
    if (!best || score > best.score) best = { dx, dy, score: +score.toFixed(2) };
  }
  return best;
}

// ---------------------------------------------------------------- SVG
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
function wrap(text, max) {
  const words = String(text).split(/\s+/), lines = [];
  let cur = '';
  for (const w of words) { if ((cur + ' ' + w).trim().length > max && cur) { lines.push(cur); cur = w; } else cur = (cur + ' ' + w).trim(); }
  if (cur) lines.push(cur);
  return lines;
}

const SITE_STYLE = {
  mill: { letter: 'M', fill: '#8b4513', name: 'Mill / mill site' },
  dam: { letter: 'Dm', fill: '#1f5f8b', name: 'Dam or weir' },
  hydro: { letter: 'H', fill: '#1f5f8b', name: 'Hydro station' },
  quarry: { letter: 'Q', fill: '#6d6250', name: 'Quarry / gravel pit' },
  fuel: { letter: 'F', fill: '#d0342c', name: 'Fuel station' },
  depot: { letter: 'I', fill: '#7a4e9a', name: 'Industrial yard / depot' },
  rail: { letter: 'R', fill: '#222222', name: 'Railway station / yard' },
  landing: { letter: 'L', fill: '#1a7fc1', name: 'River landing / boat launch' },
  farm: { letter: 'Fa', fill: '#9a7b1c', name: 'Farm' },
  wetland: { letter: 'W', fill: '#2f8a73', name: 'Wetland / marsh' },
  track: { letter: 'T', fill: '#8a5a2b', name: 'Dirt road into woods' },
  bridge: { letter: 'B', fill: '#111111', name: 'Bridge over the river' },
};

function render(town, f, dem, osm, F, brs, sq1, sq2, stats, cov, picks) {
  const S = f.size, K = MAP / S;
  const P = ([x, y]) => `${(PAD + x * K).toFixed(1)},${(PAD + (S - y) * K).toFixed(1)}`;
  const d = (pts, close) => pts.length ? 'M' + pts.map(P).join('L') + (close ? 'Z' : '') : '';
  const areaD = (a) => a.rings.map((r) => d(r, true)).join('');
  const o = [];
  o.push(`<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="${SVG_W}" height="${SVG_H}" viewBox="0 0 ${SVG_W} ${SVG_H}">`);
  o.push(`<title>${esc(`${town.name}, ${town.state}: scouting map for the new Millbrook`)}</title>`);
  o.push(`<defs><clipPath id="map"><rect x="${PAD}" y="${PAD}" width="${MAP}" height="${MAP}"/></clipPath>
<pattern id="wet" width="14" height="14" patternUnits="userSpaceOnUse"><rect width="14" height="14" fill="#a9d3c4" fill-opacity="0.55"/><path d="M2 9h4M8 4h4M3 13h3" stroke="#2f8a73" stroke-width="1.3"/></pattern>
<pattern id="qry" width="10" height="10" patternUnits="userSpaceOnUse" patternTransform="rotate(45)"><rect width="10" height="10" fill="#c9bca6"/><path d="M0 0v10" stroke="#7d705c" stroke-width="2"/></pattern>
<pattern id="orch" width="16" height="16" patternUnits="userSpaceOnUse"><rect width="16" height="16" fill="#cfe0a0" fill-opacity="0.6"/><circle cx="8" cy="8" r="2.2" fill="#6f9c4a"/></pattern>
</defs>`);
  o.push(`<style>text{font-family:'Segoe UI',Arial,Helvetica,sans-serif;fill:#1d1d1d}.h{paint-order:stroke;stroke:#fff;stroke-width:5px;stroke-linejoin:round}.sm{font-size:15px}.pn{font-size:17px}.hd{font-size:20px;font-weight:700}</style>`);
  o.push(`<rect width="${SVG_W}" height="${SVG_H}" fill="#f5f2ea"/>`);
  o.push(`<g clip-path="url(#map)">`);
  o.push(`<image x="${PAD}" y="${PAD}" width="${MAP}" height="${MAP}" preserveAspectRatio="none" xlink:href="data:image/png;base64,${hillshadeImage(dem).toString('base64')}"/>`);
  // land use
  const LU = [
    [(t) => t.landuse === 'forest' || t.natural === 'wood', 'fill="#5f8f4e" fill-opacity="0.33"'],
    [(t) => ['farmland', 'animal_keeping'].includes(t.landuse), 'fill="#f0e29a" fill-opacity="0.55" stroke="#b8a55a" stroke-width="0.6"'],
    [(t) => ['meadow', 'grass'].includes(t.landuse) || ['grassland', 'scrub', 'heath'].includes(t.natural), 'fill="#cfe5a3" fill-opacity="0.45"'],
    [(t) => ['orchard', 'vineyard'].includes(t.landuse), 'fill="url(#orch)"'],
    [(t) => t.landuse === 'farmyard', 'fill="#e2c38c" fill-opacity="0.7" stroke="#a88a50" stroke-width="0.6"'],
    [(t) => ['residential'].includes(t.landuse), 'fill="#e2dad3" fill-opacity="0.45"'],
    [(t) => ['commercial', 'retail'].includes(t.landuse), 'fill="#efc9c4" fill-opacity="0.6"'],
    [(t) => ['cemetery'].includes(t.landuse) || t.amenity === 'grave_yard', 'fill="#b9d1ab" fill-opacity="0.8"'],
    [(t) => ['park', 'pitch', 'recreation_ground', 'golf_course', 'playground'].includes(t.leisure) || t.landuse === 'recreation_ground', 'fill="#bfe3a0" fill-opacity="0.6"'],
    [(t) => ['industrial', 'railway'].includes(t.landuse), 'fill="#d8c4e4" fill-opacity="0.75" stroke="#7a4e9a" stroke-width="1"'],
    [(t) => t.landuse === 'quarry' || t['disused:landuse'] === 'quarry' || t['abandoned:landuse'] === 'quarry', 'fill="url(#qry)" stroke="#5b503f" stroke-width="1.2"'],
    [(t) => ['landfill', 'brownfield', 'construction'].includes(t.landuse), 'fill="#d8cfb8" fill-opacity="0.7"'],
    [(t) => t.natural === 'wetland', 'fill="url(#wet)" stroke="#2f8a73" stroke-width="0.8"'],
  ];
  const big = [...osm.areas].sort((a, b) => b.area - a.area);
  for (const [test, style] of LU) {
    const ds = big.filter((a) => !a.tags.building && test(a.tags)).map(areaD).join('');
    if (ds) o.push(`<path fill-rule="evenodd" ${style} d="${ds}"/>`);
  }
  // contours
  const cl = contours(dem, 10);
  const minorD = cl.filter((c) => c.level % 50 !== 0).flatMap((c) => c.lines.map((l) => d(l))).join('');
  const majorD = cl.filter((c) => c.level % 50 === 0).flatMap((c) => c.lines.map((l) => d(l))).join('');
  o.push(`<path fill="none" stroke="#6b4f33" stroke-opacity="0.35" stroke-width="0.7" d="${minorD}"/>`);
  o.push(`<path fill="none" stroke="#6b4f33" stroke-opacity="0.65" stroke-width="1.4" d="${majorD}"/>`);
  // water
  const isWaterArea = (t) => t.natural === 'water' || t.waterway === 'riverbank' || t.water || ['reservoir', 'basin'].includes(t.landuse);
  const wl = osm.lines;
  const W = (pred, w, c) => { const ds = wl.filter((l) => pred(l.tags)).map((l) => d(l.pts)).join(''); if (ds) o.push(`<path fill="none" stroke="${c}" stroke-width="${w}" stroke-linecap="round" stroke-linejoin="round" d="${ds}"/>`); };
  W((t) => ['stream', 'brook', 'ditch', 'drain'].includes(t.waterway), 1.6, '#4a90c8');
  W((t) => ['river', 'canal'].includes(t.waterway), 7, '#7fb8e0');
  const wa = osm.areas.filter((a) => isWaterArea(a.tags)).map(areaD).join('');
  if (wa) o.push(`<path fill-rule="evenodd" fill="#8ec3e6" stroke="#3f86bd" stroke-width="1" d="${wa}"/>`);
  W((t) => ['river', 'canal'].includes(t.waterway), 1.2, '#3f86bd');
  W((t) => ['dam', 'weir', 'lock_gate'].includes(t.waterway), 4, '#24425c');
  // railways
  const rails = wl.filter((l) => ['rail', 'light_rail', 'narrow_gauge', 'preserved'].includes(l.tags.railway) && !l.tags.highway);
  const oldRail = wl.filter((l) => ['abandoned', 'disused', 'razed', 'dismantled'].includes(l.tags.railway) || /rail[- ]?trail/i.test(nm(l.tags)));
  if (oldRail.length) o.push(`<path fill="none" stroke="#7d6a8a" stroke-width="2.2" stroke-dasharray="7 5" d="${oldRail.map((l) => d(l.pts)).join('')}"/>`);
  if (rails.length) {
    const rd = rails.map((l) => d(l.pts)).join('');
    o.push(`<path fill="none" stroke="#222" stroke-width="4" d="${rd}"/><path fill="none" stroke="#fff" stroke-width="2" stroke-dasharray="10 10" d="${rd}"/>`);
  }
  // roads
  const RS = {
    trunk: [9, 7, '#e8894a'], primary: [9, 7, '#f0a35e'], secondary: [8, 6, '#f6cf6a'], tertiary: [7, 5.2, '#fff3b0'],
    unclassified: [6, 4.4, '#ffffff'], residential: [6, 4.4, '#ffffff'], service: [3.6, 2.4, '#ffffff'], motorway: [10, 8, '#d9644a'],
  };
  const order = ['service', 'residential', 'unclassified', 'tertiary', 'secondary', 'primary', 'trunk', 'motorway'];
  const roadsBy = {};
  for (const l of wl) { const rc = roadClass(l.tags); if (rc) (roadsBy[rc] ??= []).push(l); }
  const paved = (rc) => (roadsBy[rc] ?? []).filter((l) => !isUnpaved(l.tags));
  const unp = (roadsBy.track ?? []).concat(...order.map((rc) => (roadsBy[rc] ?? []).filter((l) => isUnpaved(l.tags))));
  if (roadsBy.path) o.push(`<path fill="none" stroke="#b5544b" stroke-width="1.1" stroke-dasharray="2 3" d="${roadsBy.path.map((l) => d(l.pts)).join('')}"/>`);
  if (unp.length) o.push(`<path fill="none" stroke="#8a5a2b" stroke-width="2.4" stroke-dasharray="8 4" d="${unp.map((l) => d(l.pts)).join('')}"/>`);
  for (const rc of order) { const ls = paved(rc); if (ls.length) o.push(`<path fill="none" stroke="#5a5550" stroke-width="${RS[rc][0]}" stroke-linecap="round" stroke-linejoin="round" d="${ls.map((l) => d(l.pts)).join('')}"/>`); }
  for (const rc of order) { const ls = paved(rc); if (ls.length) o.push(`<path fill="none" stroke="${RS[rc][2]}" stroke-width="${RS[rc][1]}" stroke-linecap="round" stroke-linejoin="round" d="${ls.map((l) => d(l.pts)).join('')}"/>`); }
  // bridges
  const brParts = new Set(brs.flatMap((b) => b.parts));
  const bl = wl.filter((l) => brParts.has(l.id));
  if (bl.length) {
    const bd = bl.map((l) => d(l.pts)).join('');
    o.push(`<path fill="none" stroke="#111" stroke-width="11" stroke-linecap="butt" d="${bd}"/><path fill="none" stroke="#fffbe8" stroke-width="7" stroke-linecap="butt" d="${bd}"/>`);
  }
  // buildings
  const bds = osm.areas.filter((a) => a.tags.building && a.tags.building !== 'no').map(areaD).join('');
  if (bds) o.push(`<path fill-rule="evenodd" fill="#6e3f33" stroke="#3b211a" stroke-width="0.5" d="${bds}"/>`);
  // play-area squares
  const placed = [];
  const free = (r) => !placed.some((q) => !(r[2] < q[0] || r[0] > q[2] || r[3] < q[1] || r[1] > q[3]));
  const sqr = (sq, side, style, label) => {
    const x0 = sq.cx - side / 2, y1 = sq.cy + side / 2;
    const [px, py] = P([x0, y1]).split(',').map(Number);
    o.push(`<rect x="${px}" y="${py}" width="${side * K}" height="${side * K}" fill="none" ${style}/>`);
    const lx = side > 1000 ? px + side * K - 10 : px + side * K - 10, ly = side > 1000 ? py + side * K - 12 : py + 28;
    placed.push([lx - label.length * 12, ly - 22, lx, ly + 4]);
    o.push(`<text class="h" x="${lx}" y="${ly}" text-anchor="end" style="font-size:22px;font-weight:700;fill:${style.match(/stroke="([^"]+)"/)[1]}">${esc(label)}</text>`);
  };
  sqr(sq2, 2000, 'stroke="#e07b00" stroke-width="4" stroke-dasharray="18 10"', '2 km play area');
  sqr(sq1, 1000, 'stroke="#d0021b" stroke-width="5"', '1 km play area');
  // river name
  const rivs = wl.filter((l) => ['river', 'canal'].includes(l.tags.waterway) && l.tags.name);
  if (rivs.length) {
    const byName = {};
    for (const l of rivs) byName[l.tags.name] = (byName[l.tags.name] ?? 0) + polyLenIn(l.pts, [0, 0, S, S]);
    const rn = Object.entries(byName).sort((a, b) => b[1] - a[1])[0][0];
    const pts = rivs.filter((l) => l.tags.name === rn).flatMap((l) => l.pts).filter((p) => inBox(p, [100, 100, S - 100, S - 100]));
    const target = [sq1.cx + 600, sq1.cy - 600];
    pts.sort((a, b) => Math.hypot(a[0] - target[0], a[1] - target[1]) - Math.hypot(b[0] - target[0], b[1] - target[1]));
    for (const p of pts) {
      const [x, y] = P(p).split(',').map(Number), r = [x, y + 18, x + rn.length * 12.5, y + 46];
      if (r[2] > PAD + MAP - 10 || !free(r)) continue;
      placed.push(r);
      o.push(`<text class="h" x="${x}" y="${y + 40}" style="font-size:24px;font-style:italic;fill:#1f5f8b">${esc(rn)}</text>`);
      break;
    }
  }
  // town name
  const [tx, ty] = P(f.local(town.lat, town.lon)).split(',').map(Number);
  o.push(`<text class="h" x="${tx}" y="${ty - 30}" text-anchor="middle" style="font-size:34px;font-weight:800;letter-spacing:2px;stroke-width:7px">${esc(town.name.toUpperCase())}</text>`);
  // spot heights
  let zmin = Infinity, zmax = -Infinity, imin = 0, imax = 0;
  dem.z.forEach((v, i) => { if (v < zmin) { zmin = v; imin = i; } if (v > zmax) { zmax = v; imax = i; } });
  for (const [i, v] of [[imax, zmax], [imin, zmin]]) {
    const x = ((i % dem.W) + 0.5) * dem.res, y = S - (Math.floor(i / dem.W) + 0.5) * dem.res;
    const [px, py] = P([Math.min(S - 150, Math.max(150, x)), Math.min(S - 60, Math.max(60, y))]).split(',').map(Number);
    o.push(`<path d="M${px} ${py - 9}l8 14h-16z" fill="#3b2a1a"/><text class="h sm" x="${px + 11}" y="${py + 5}">${v.toFixed(0)} m</text>`);
  }
  // sites
  placed.push([tx - town.name.length * 13, ty - 60, tx + town.name.length * 13, ty - 22]);
  const marks = [];
  for (const s of picks) {
    const st = SITE_STYLE[s.cat];
    let [px, py] = P(s.p).split(',').map(Number);
    px = Math.min(PAD + MAP - 30, Math.max(PAD + 30, px)); py = Math.min(PAD + MAP - 30, Math.max(PAD + 30, py));
    const r = 17, dash = s.add ? ' stroke-dasharray="5 3"' : '';
    const ox = px, oy = py;
    for (let k = 0; k < 24 && (marks.some(([mx, my]) => Math.hypot(mx - px, my - py) < 38) || placed.some((q) => px > q[0] - r && px < q[2] + r && py > q[1] - r && py < q[3] + r)); k++) {
      const ang = k * 1.05, rad = 40 + 14 * Math.floor(k / 6);
      px = ox + Math.cos(ang) * rad; py = oy + Math.sin(ang) * rad;
    }
    marks.push([px, py]);
    if (px !== ox || py !== oy) o.push(`<path d="M${ox} ${oy}L${px} ${py}" stroke="#222" stroke-width="1.5"/><circle cx="${ox}" cy="${oy}" r="3.5" fill="#222"/>`);
    o.push(`<circle cx="${px}" cy="${py}" r="${r}" fill="${s.add ? '#ffffff' : st.fill}" stroke="${s.add ? st.fill : '#fff'}" stroke-width="3.5"${dash}/>`);
    o.push(`<text x="${px}" y="${py + 6}" text-anchor="middle" style="font-size:${st.letter.length > 1 || s.n ? 14 : 17}px;font-weight:800;fill:${s.add ? st.fill : '#fff'}">${st.letter}${s.n ?? ''}</text>`);
    // label: try right, then left, then below, avoid overlaps with earlier labels
    const txt = s.mapLabel ?? s.label;
    const wpx = txt.length * 9.2 + 8;
    const cands = [[px + r + 6, py + 6, 'start'], [px - r - 6, py + 6, 'end'], [px, py + r + 22, 'middle'], [px, py - r - 10, 'middle']];
    let chosen = cands[0];
    for (const c of cands) {
      const x0 = c[2] === 'start' ? c[0] : c[2] === 'end' ? c[0] - wpx : c[0] - wpx / 2;
      const rect = [x0, c[1] - 16, x0 + wpx, c[1] + 4];
      if (rect[0] > PAD && rect[2] < PAD + MAP && !placed.some((q) => !(rect[2] < q[0] || rect[0] > q[2] || rect[3] < q[1] || rect[1] > q[3]))) { chosen = c; placed.push(rect); break; }
    }
    o.push(`<text class="h" x="${chosen[0]}" y="${chosen[1]}" text-anchor="${chosen[2]}" style="font-size:16px;font-weight:600${s.add ? ';font-style:italic' : ''}">${esc(txt)}</text>`);
  }
  o.push('</g>');
  // scale bar and north arrow
  const sbx = PAD + 30, sby = PAD + MAP - 40;
  o.push(`<rect x="${sbx - 12}" y="${sby - 34}" width="${1000 * K + 70}" height="58" fill="#fff" fill-opacity="0.85" rx="6"/>`);
  o.push(`<rect x="${sbx}" y="${sby}" width="${500 * K}" height="9" fill="#222"/><rect x="${sbx + 500 * K}" y="${sby}" width="${500 * K}" height="9" fill="#fff" stroke="#222" stroke-width="1.5"/>`);
  o.push(`<text class="sm" x="${sbx}" y="${sby - 8}">0</text><text class="sm" x="${sbx + 500 * K}" y="${sby - 8}" text-anchor="middle">500 m</text><text class="sm" x="${sbx + 1000 * K}" y="${sby - 8}" text-anchor="middle">1 km</text>`);
  const nax = PAD + MAP - 50, nay = PAD + 60;
  o.push(`<circle cx="${nax}" cy="${nay}" r="34" fill="#fff" fill-opacity="0.85"/><path d="M${nax} ${nay - 26}l12 34l-12 -8l-12 8z" fill="#222"/><text x="${nax}" y="${nay + 26}" text-anchor="middle" style="font-size:16px;font-weight:700">N</text>`);
  o.push(`<rect x="${PAD}" y="${PAD}" width="${MAP}" height="${MAP}" fill="none" stroke="#333" stroke-width="2"/>`);
  // ---------- panel
  const X = PAD + MAP + PAD + 6;
  let y = PAD + 30;
  const line = (txt, cls = 'pn', extra = '') => { o.push(`<text class="${cls}" x="${X}" y="${y}"${extra}>${esc(txt)}</text>`); };
  o.push(`<text x="${X}" y="${y}" style="font-size:34px;font-weight:800">${esc(`${town.name}, ${town.state}`)}</text>`); y += 30;
  line(town.region, 'pn', ' style="font-size:19px;fill:#555"'); y += 24;
  line(`Candidate for the new Millbrook (decision U25)${town.population ? `; population ${town.population}` : ''}`, 'sm'); y += 20;
  line(`Map: 4 km square, UTM zone ${f.zone}N (EPSG:${f.epsg}); north up`, 'sm'); y += 30;
  // stats table
  const cols = [X + 300, X + 420, X + 545];
  o.push(`<text class="hd" x="${X}" y="${y}">Measured</text>`);
  ['1 km', '2 km', '4 km'].forEach((h, i) => o.push(`<text class="hd" x="${cols[i]}" y="${y}" text-anchor="end">${h}</text>`));
  y += 8; o.push(`<path d="M${X} ${y}H${X + 560}" stroke="#999"/>`); y += 22;
  const r3 = (label, fn) => {
    o.push(`<text class="pn" x="${X}" y="${y}">${esc(label)}</text>`);
    [stats.sq1, stats.sq2, stats.frame].forEach((s, i) => o.push(`<text class="pn" x="${cols[i]}" y="${y}" text-anchor="end">${esc(fn(s))}</text>`));
    y += 24;
  };
  r3('Buildings (footprints)', (s) => s.buildings);
  r3('  of which 70 m2 or more', (s) => s.buildingsOver70m2);
  r3('Roads, km (drivable)', (s) => s.roadsKm.drivable);
  r3('  of which unpaved / track', (s) => s.roadsKm.unpavedOrTrack);
  r3('Railway km (active / old)', (s) => `${s.railKm.active.toFixed(1)} / ${s.railKm.old.toFixed(1)}`);
  r3('River km', (s) => s.riverKm.toFixed(1));
  r3('Road bridges: river / stream', (s) => `${s.bridges.river} / ${s.bridges.stream}`);
  r3('Elevation, m', (s) => `${s.elevation.min.toFixed(0)}-${s.elevation.max.toFixed(0)}`);
  r3('Relief, m', (s) => s.elevation.relief.toFixed(0));
  r3('Mean slope, %', (s) => s.elevation.meanSlopePct);
  r3('Farmland + meadow, ha', (s) => s.ha.farm);
  r3('Forest, ha', (s) => s.ha.forest);
  r3('Wetland, ha', (s) => s.ha.wetland);
  r3('Street frontage with buildings, %', (s) => s.frontagePct ?? '-');
  y += 10;
  // game places
  o.push(`<text class="hd" x="${X}" y="${y}">Game places</text>`); y += 8;
  o.push(`<path d="M${X} ${y}H${X + 560}" stroke="#999"/>`); y += 24;
  for (const g of stats.places) {
    const st = SITE_STYLE[g.cat];
    o.push(`<circle cx="${X + 12}" cy="${y - 6}" r="11" fill="${g.status === 'add' ? '#fff' : g.status === 'missing' ? '#ddd' : st.fill}" stroke="${g.status === 'native' ? '#fff' : st.fill}" stroke-width="2"${g.status === 'add' ? ' stroke-dasharray="4 2"' : ''}/>`);
    o.push(`<text x="${X + 12}" y="${y - 1}" text-anchor="middle" style="font-size:11px;font-weight:800;fill:${g.status === 'native' ? '#fff' : st.fill}">${st.letter}</text>`);
    const lines = wrap(`${g.title}: ${g.text}`, 62);
    lines.forEach((l, i) => { o.push(`<text class="sm" x="${X + 30}" y="${y + i * 18}"${i === 0 ? '' : ''}>${esc(l)}</text>`); });
    y += lines.length * 18 + 6;
  }
  y += 8;
  o.push(`<text class="hd" x="${X}" y="${y}">Data</text>`); y += 8;
  o.push(`<path d="M${X} ${y}H${X + 560}" stroke="#999"/>`); y += 22;
  const dataLines = [
    `Elevation: 1 m lidar DEM covers ${cov.share['1km']['1m']} % of the 1 km square and ${cov.share.frame['1m']} % of this map (${cov.oneMetreProjects.map((p) => `${p.name} ${p.acquired?.slice(0, 4) ?? ''}`).join(', ') || 'none'}); 1/3 arc-second everywhere. Shown at 5 m.`,
    `OSM: ${stats.frame.buildings} building footprints on this map; ${stats.osmBase ? `data of ${stats.osmBase.slice(0, 10)}` : ''}.`,
  ];
  for (const t of dataLines) for (const l of wrap(t, 70)) { line(l, 'sm'); y += 18; }
  y += 10;
  // legend
  o.push(`<text class="hd" x="${X}" y="${y}">Legend</text>`); y += 8;
  o.push(`<path d="M${X} ${y}H${X + 560}" stroke="#999"/>`); y += 20;
  const leg = [
    ['<rect width="26" height="14" fill="#6e3f33"/>', 'Building'], ['<path d="M0 7h26" stroke="#5a5550" stroke-width="9"/><path d="M0 7h26" stroke="#f0a35e" stroke-width="7"/>', 'Main road'],
    ['<path d="M0 7h26" stroke="#5a5550" stroke-width="6"/><path d="M0 7h26" stroke="#fff" stroke-width="4.4"/>', 'Local road'], ['<path d="M0 7h26" stroke="#8a5a2b" stroke-width="2.4" stroke-dasharray="8 4"/>', 'Unpaved road / track'],
    ['<path d="M0 7h26" stroke="#222" stroke-width="4"/><path d="M0 7h26" stroke="#fff" stroke-width="2" stroke-dasharray="6 6"/>', 'Railway'], ['<path d="M0 7h26" stroke="#7d6a8a" stroke-width="2.2" stroke-dasharray="7 5"/>', 'Old railway / rail trail'],
    ['<rect width="26" height="14" fill="#8ec3e6" stroke="#3f86bd"/>', 'River, water'], ['<path d="M0 7h26" stroke="#24425c" stroke-width="4"/>', 'Dam / weir'],
    ['<rect width="26" height="14" fill="url(#wet)"/>', 'Wetland'], ['<rect width="26" height="14" fill="#5f8f4e" fill-opacity="0.45"/>', 'Forest'],
    ['<rect width="26" height="14" fill="#f0e29a"/>', 'Farmland'], ['<rect width="26" height="14" fill="#d8c4e4" stroke="#7a4e9a"/>', 'Industrial'],
    ['<rect width="26" height="14" fill="url(#qry)"/>', 'Quarry'], ['<path d="M0 7h26" stroke="#6b4f33" stroke-width="1.4"/>', 'Contour (10 m, bold 50 m)'],
    ['<path d="M0 7h26" stroke="#111" stroke-width="11"/><path d="M0 7h26" stroke="#fffbe8" stroke-width="7"/>', 'Bridge'], ['<circle cx="13" cy="7" r="8" fill="#fff" stroke="#8b4513" stroke-width="2.5" stroke-dasharray="4 2"/>', 'Site the game would add'],
  ];
  leg.forEach(([sym, label], i) => {
    const lx = X + (i % 2) * 290, ly = y + Math.floor(i / 2) * 24;
    o.push(`<g transform="translate(${lx},${ly - 11})">${sym}</g><text class="sm" x="${lx + 36}" y="${ly + 1}">${esc(label)}</text>`);
  });
  y += Math.ceil(leg.length / 2) * 24 + 10;
  y = Math.max(y, SVG_H - PAD - 46);
  line(`Map data ${CREDIT_OSM}.`, 'sm'); y += 18;
  line(`Elevation: ${CREDIT_DEM}.`, 'sm');
  o.push('</svg>');
  return o.join('\n');
}

// ---------------------------------------------------------------- per town
function pickSites(town, f, F, brs, sq1, sq2) {
  const b2 = [sq2.cx - 1000, sq2.cy - 1000, sq2.cx + 1000, sq2.cy + 1000];
  const c1 = [sq1.cx, sq1.cy];
  const dist = (p) => Math.hypot(p[0] - c1[0], p[1] - c1[1]);
  const inFrame = (p) => inBox(p, [0, 0, f.size, f.size]);
  const picks = [];
  const nearest = (list) => { // nearest inside the 2 km square, else nearest on the map
    const l = list.filter((x) => inFrame(x.e.c)).sort((a, b) => dist(a.e.c) - dist(b.e.c));
    return l.find((x) => inBox(x.e.c, b2)) ?? l[0];
  };
  const largestIn = (list) => list.filter((x) => inBox(x.e.c, b2)).sort((a, b) => (b.e.area ?? 0) - (a.e.area ?? 0))[0] ?? nearest(list);
  const longestIn = (list) => list.filter((x) => inBox(x.e.c, b2) && x.e.tags.highway === 'track').sort((a, b) => polyLenIn(b.e.pts, b2) - polyLenIn(a.e.pts, b2))[0] ?? nearest(list);
  const choose = { mill: nearest, dam: nearest, hydro: nearest, quarry: nearest, fuel: nearest, depot: (l) => largestIn(l.filter((x) => x.e.tags.landuse === 'industrial' || x.e.tags.landuse === 'railway')) ?? nearest(l),
    rail: nearest, landing: nearest, farm: (l) => nearest(l.filter((x) => ['farmyard', 'orchard', 'vineyard'].includes(x.e.tags.landuse) || x.e.tags.building === 'barn' || x.e.tags.place === 'farm')) ?? largestIn(l),
    wetland: largestIn, track: longestIn };
  const overrides = town.sites ?? [];
  for (const cat of Object.keys(choose)) {
    const ov = overrides.filter((s) => s.cat === cat);
    if (ov.length) {
      for (const s of ov) {
        const p = s.lat !== undefined ? f.local(s.lat, s.lon) : [f.size / 2 + (s.dx ?? 0), f.size / 2 + (s.dy ?? 0)];
        picks.push({ cat, p, label: s.label, add: !!s.add, note: s.note, d: dist(p) });
      }
      continue;
    }
    const x = choose[cat](F[cat]);
    if (!x) continue;
    if (['dam', 'hydro'].includes(cat) && picks.some((q) => q.cat === 'mill' && Math.hypot(q.p[0] - x.e.c[0], q.p[1] - x.e.c[1]) < 80)) continue;
    const label = x.label.length > 34 ? x.label.slice(0, 32) + '...' : x.label;
    picks.push({ cat, p: x.e.c, label, ref: x.e.id, d: dist(x.e.c) });
  }
  // river bridges, nearest first, numbered
  brs.filter((x) => x.over === 'river' && inBox(x.c, [0, 0, f.size, f.size])).sort((a, b) => dist(a.c) - dist(b.c)).slice(0, 6).forEach((x, i) =>
    picks.push({ cat: 'bridge', n: i + 1, p: x.c, label: x.rail ? (x.old ? 'old rail bridge (trail)' : 'rail bridge') : x.name || 'bridge', d: dist(x.c), rail: x.rail }));
  return picks;
}

function placesSummary(picks, stats, sq1, sq2) {
  const at = (p) => (inBox(p.p, [sq1.cx - 500, sq1.cy - 500, sq1.cx + 500, sq1.cy + 500]) ? 'in the 1 km square'
    : inBox(p.p, [sq2.cx - 1000, sq2.cy - 1000, sq2.cx + 1000, sq2.cy + 1000]) ? `in the 2 km square, ${(p.d / 1000).toFixed(1)} km from the centre`
      : `outside the 2 km square, ${(p.d / 1000).toFixed(1)} km from the centre`);
  const one = (cat, title) => {
    const ps = picks.filter((p) => p.cat === cat);
    if (!ps.length) return { cat, title, status: 'missing', text: 'none mapped in the 4 km square; would be added' };
    const p = ps[0];
    return { cat, title, status: p.add ? 'add' : 'native', text: p.add ? `add: ${p.label}${p.note ? ` (${p.note})` : ''}` : `${p.label}, ${at(p)}` };
  };
  const out = [];
  const mill = one('mill', 'Mill');
  if (mill.status === 'missing') {
    const dam = picks.find((p) => (p.cat === 'dam' || p.cat === 'hydro') && !p.add);
    if (dam) Object.assign(mill, { status: 'native', text: `no mill tagged; ${dam.label} ${at(dam)} is the mill site` });
  }
  out.push(mill);
  const brR = picks.filter((p) => p.cat === 'bridge' && !p.rail);
  out.push({ cat: 'bridge', title: 'Bridges', status: brR.length ? 'native' : 'missing',
    text: brR.length ? `${stats.sq1.bridges.river} road bridge(s) over the river in the 1 km square, ${stats.sq2.bridges.river} in 2 km, ${stats.frame.bridges.river} on the map; ${stats.frame.bridges.stream} over streams` : 'no road bridge over the river on the map' });
  out.push(one('quarry', 'Quarry'));
  out.push(one('fuel', 'Fuel'));
  const dep = one('depot', 'Depot / yard');
  const rail = picks.find((p) => p.cat === 'rail');
  dep.text += stats.frame.railKm.active > 0 ? `; active railway ${stats.frame.railKm.active.toFixed(1)} km on the map${rail ? ` (${rail.label})` : ''}` : stats.frame.railKm.old > 0 ? `; old railway / rail trail ${stats.frame.railKm.old.toFixed(1)} km` : '; no railway';
  out.push(dep);
  out.push(one('landing', 'Landing'));
  out.push(one('farm', 'Farms'));
  out.push(one('wetland', 'Wetland'));
  out.push(one('track', 'Woods roads'));
  return out;
}

function runTown(id) {
  const town = getTown(id);
  const f = townFrame(town);
  const dir = townDir(id);
  for (const fn of ['osm.json', 'dem.f32']) if (!fs.existsSync(path.join(dir, fn))) throw new Error(`${id}: missing ${fn}; run fetch-osm.mjs / fetch-dem.mjs first`);
  const dem = loadDem(dir);
  const osm = loadOsm(dir, f);
  const cov = JSON.parse(fs.readFileSync(path.join(dir, 'coverage.json'), 'utf8'));
  const place = osm.points.find((p) => p.id === town.osm);
  town.population = place?.tags.population;
  const F = classify(osm, town);
  const brs = bridges(osm);
  const S = f.size;
  const core = f.local(town.lat, town.lon);
  const a1 = town.play1 ?? autoSquare(osm, dem, F, brs, S, 1000, core);
  const c1 = [S / 2 + a1.dx, S / 2 + a1.dy];
  const a2 = town.play2 ?? autoSquare(osm, dem, F, brs, S, 2000, core, [c1[0] - 500, c1[1] - 500, c1[0] + 500, c1[1] + 500]);
  const sq1 = { cx: S / 2 + a1.dx, cy: S / 2 + a1.dy, auto: !town.play1, score: a1.score };
  const sq2 = { cx: S / 2 + a2.dx, cy: S / 2 + a2.dy, auto: !town.play2, score: a2.score };
  const box = (sq, side) => [sq.cx - side / 2, sq.cy - side / 2, sq.cx + side / 2, sq.cy + side / 2];
  const stats = {
    town: id, name: town.name, state: town.state, region: town.region, population: town.population ?? null,
    osmBase: osm.meta.osmBase, frameSize: S, epsg: f.epsg,
    play1: { dx: a1.dx, dy: a1.dy, auto: sq1.auto, centre: f.latlon(sq1.cx, sq1.cy).map((v) => +v.toFixed(5)) },
    play2: { dx: a2.dx, dy: a2.dy, auto: sq2.auto, centre: f.latlon(sq2.cx, sq2.cy).map((v) => +v.toFixed(5)) },
    sq1: measure(osm, dem, F, brs, box(sq1, 1000)), sq2: measure(osm, dem, F, brs, box(sq2, 2000)), frame: measure(osm, dem, F, brs, [0, 0, S, S]),
    demCoverage: cov.share, oneMetreProjects: cov.oneMetreProjects,
  };
  const blds = osm.areas.filter((a) => a.tags.building && a.tags.building !== 'no');
  const src = {};
  for (const a of blds) { const s = a.tags.source ?? a.tags['source:geometry'] ?? '(none)'; src[s] = (src[s] ?? 0) + 1; }
  stats.buildingSources = Object.fromEntries(Object.entries(src).sort((a, b) => b[1] - a[1]).slice(0, 5));
  stats.addressNodes = osm.points.filter((p) => p.tags['addr:housenumber'] && !p.tags.building).length;
  const picks = pickSites(town, f, F, brs, sq1, sq2);
  stats.places = placesSummary(picks, stats, sq1, sq2);
  stats.picks = picks.map((p) => ({ cat: p.cat, n: p.n, label: p.label, add: !!p.add, ref: p.ref, distM: Math.round(p.d), latlon: f.latlon(p.p[0], p.p[1]).map((v) => +v.toFixed(5)) }));
  stats.bridgeList = brs.map((b) => ({ over: b.over, rail: b.rail, name: b.name, road: b.road, len: b.len, distM: Math.round(Math.hypot(b.c[0] - sq1.cx, b.c[1] - sq1.cy)) }));
  stats.found = Object.fromEntries(Object.entries(F).map(([k, v]) => [k, v.filter((x) => inBox(x.e.c, [0, 0, S, S])).slice(0, 40).map((x) => ({ label: x.label, ref: x.e.id, distM: Math.round(Math.hypot(x.e.c[0] - sq1.cx, x.e.c[1] - sq1.cy)), ha: x.e.area ? +(x.e.area / 1e4).toFixed(2) : undefined }))]));
  fs.writeFileSync(path.join(dir, 'stats.json'), JSON.stringify(stats, null, 2) + '\n');
  fs.mkdirSync(OUT_DIR, { recursive: true });
  const svg = render(town, f, dem, osm, F, brs, sq1, sq2, stats, cov, picks);
  const svgPath = path.join(OUT_DIR, `${id}.svg`);
  fs.writeFileSync(svgPath, svg);
  console.log(`${id}: ${svgPath} (${(svg.length / 1e6).toFixed(2)} MB); 1 km: ${stats.sq1.buildings} buildings, relief ${stats.sq1.elevation.relief} m, river bridges ${stats.sq1.bridges.river}; play1 ${sq1.auto ? 'auto' : 'set'} (${a1.dx}, ${a1.dy})`);
  if (flag('--png')) png(svgPath, path.join(OUT_DIR, `${id}.png`));
}

function png(svgPath, pngPath) {
  const cands = ['C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', 'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe'];
  const exe = cands.find((c) => fs.existsSync(c));
  if (!exe) { console.log('  no Edge or Chrome found: PNG skipped'); return; }
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'scout-edge-')); // fresh each time: a stale profile makes Edge exit without a shot
  fs.rmSync(pngPath, { force: true });
  const r = spawnSync(exe, ['--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--no-default-browser-check', '--disable-extensions',
    `--user-data-dir=${profile}`, `--window-size=${SVG_W},${SVG_H}`, '--force-device-scale-factor=1', `--screenshot=${pngPath}`, 'file:///' + svgPath.replace(/\\/g, '/')], { timeout: 120_000, encoding: 'utf8' });
  // The msedge.exe launcher can return before the browser has written the shot: wait for a stable file.
  const nap = (ms) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
  let last = -1;
  for (let t = 0; t < 180; t++) {
    const size = fs.existsSync(pngPath) ? fs.statSync(pngPath).size : -1;
    if (size > 0 && size === last) break;
    last = size; nap(500);
  }
  nap(1000);
  try { fs.rmSync(profile, { recursive: true, force: true }); } catch { /* Edge may still hold a file for a moment */ }
  if (fs.existsSync(pngPath)) console.log(`  ${pngPath} (${(fs.statSync(pngPath).size / 1e6).toFixed(2)} MB)`);
  else console.log(`  PNG failed: status ${r?.status} ${r?.error ?? ""} ${(r?.stderr ?? "").slice(0, 300)}`);
}

function summary() {
  const rows = [];
  for (const t of loadTowns().towns) {
    const p = path.join(REPO, 'artifacts', 'scout', t.id, 'stats.json');
    if (!fs.existsSync(p)) continue;
    const s = JSON.parse(fs.readFileSync(p, 'utf8'));
    rows.push(`| ${s.name}, ${s.state} | ${s.population ?? ''} | ${s.sq1.buildings} (${s.sq1.buildingsOver70m2}) / ${s.sq2.buildings} / ${s.frame.buildings} | ${s.sq1.frontagePct}% | ${s.sq1.roadsKm.drivable} / ${s.sq2.roadsKm.drivable} | ${s.sq2.roadsKm.unpavedOrTrack} | ${s.sq1.bridges.river} / ${s.sq2.bridges.river} | ${s.sq1.elevation.relief} / ${s.sq2.elevation.relief} | ${s.frame.railKm.active} / ${s.frame.railKm.old} | ${s.places.map((g) => `${g.cat}:${g.status[0]}`).join(' ')} |`);
  }
  console.log('| Town | Pop. | Buildings 1 (>=70 m2) / 2 / 4 km | Frontage 1 km | Road km 1/2 | Unpaved km 2 | River bridges 1/2 | Relief m 1/2 | Rail km act/old (4) | Places |');
  console.log('|---|---|---|---|---|---|---|---|---|---|');
  console.log(rows.join('\n'));
}

if (flag('--summary')) { summary(); process.exit(0); }
const ids = townIds();
if (!ids.length) { console.error('usage: node tools/scout/render-map.mjs <town-id> ... | --all [--png] | --summary'); process.exit(1); }
let failed = 0;
for (const id of ids) {
  try { runTown(id); } catch (e) { failed++; console.error(e.stack ?? String(e)); }
}
process.exit(failed ? 1 : 0);
