#!/usr/bin/env node
// Builds the frozen town source extract TownData/<town>/v1/ from the cached downloads of fetch-town.mjs (card T3.0,
// ADR-006 stage W0, ADR-007). Reads only artifacts/world-src/<town>/ (verified against its SHA-256 pins); downloads
// nothing. Re-running it on the same cache writes byte-identical files. Zero dependencies.
//
//   node tools/world/extract-town.mjs <town-id> [--out DIR] [--check]
//
// --check extracts into a temporary folder and compares every file with TownData/<town>/v1 (exit 1 on any difference).
//
// Output (local frame of town-lib.mjs: x east, z north, metres; y = DEM elevation - Y0; LF, sorted keys, mm):
//   roads.json      every highway way (and railway way) touching the extract square, with its OSM node chain; ways are
//                   cut to the nodes inside the square plus one beyond, so edge crossings and junctions just outside
//                   are kept; every node with x, z, (y inside the square) and its tags
//   buildings.json  footprints (CCW, holes CW), OSM id, tags, business type and form from the classifier
//                   (tools/osm/measure.mjs typeOf, buildTownFrom, lotAnalysis), the classifier inputs, the attached
//                   shop/amenity points, ground y at the centroid, inPlay
//   sites.json      mapped parking, parking spaces, aisles and service ways, fuel, yards, area:highway, barriers,
//                   entrances, points of interest (with their host building)
//   landuse.json    land use, natural and leisure areas (clipped to the square), tree rows, hedges, power lines, runways,
//                   points (trees, power towers, masts)
//   water.json      water areas (the river polygon), waterway lines (the river centreline, creeks), dams, weirs, locks,
//                   lock gates, and the DEM levels measured inside each river area
//   dem/dem_1m.f32  float32 LE, y = elevation - Y0, 2,649 x 2,649 samples at 1 m, south-first rows, + dem/dem.json
//   sources.json    the download pins (URLs, query, dates, SHA-256), credit.txt (ODbL + USGS), manifest.json
// Real names are stripped (town-lib.mjs STRIP); a leak scan against the raw names fails the run if any survives.
// Data (c) OpenStreetMap contributors, ODbL 1.0; elevation USGS 3D Elevation Program (public domain).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  REPO, townFrame, srcDir, townDataDir, PLAY_HALF, SKIRT, EXTRACT_HALF, DEM_N, DEM_RES, sha256, sha256File, mm, stableJson, writeFile,
  ringArea, ringCentroid, pointInRing, inBox, ringBox, clipRing, clipLine, lineLength, STRIP, STATE_REF, stripTags, nameValues,
} from './town-lib.mjs';
import { mosaicDem, sampler } from './dem-lib.mjs';
import { assembleRings } from '../osm/lib.mjs';
import { buildTownFrom, lotAnalysis } from '../osm/measure.mjs';

export const EXTRACT_VERSION = 1;
export const FORMAT = 'backroads-towndata';
export const TOWN_NAME = 'Millbrook'; // TOWN_NAME of the overview's parameters: the in-game name

// measure.mjs type -> Backroads.Core.WorldBake.BusinessType (ADR-007)
export const BUSINESS = {
  house: 'House', apartments: 'Apartments', accessory: 'Accessory', shop: 'Retail', office: 'Office', restaurant: 'Restaurant',
  fast_food: 'FastFood', supermarket: 'Supermarket', fuel_shop: 'Fuel', fuel_canopy: 'Fuel', car_repair: 'CarRepair', motel: 'Motel',
  civic: 'Civic', church: 'Church', school: 'School', warehouse: 'Warehouse', industrial: 'Industrial', farm: 'Farm', quarry: 'Quarry',
  mill: 'Mill', harbor: 'Harbor', roof: 'Roof', unknown: 'Unknown',
};

const RAIL = new Set(['rail', 'abandoned', 'disused', 'razed', 'dismantled', 'light_rail', 'narrow_gauge', 'preserved', 'construction', 'proposed', 'tram', 'subway', 'siding', 'spur']);
const WATER_LINES = new Set(['river', 'stream', 'canal', 'ditch', 'drain', 'brook', 'tidal_channel', 'rapids', 'canoe_pass', 'fish_pass']);
const WATER_STRUCT = new Set(['dam', 'weir', 'lock_gate', 'lock', 'waterfall', 'sluice_gate', 'fuel']);
const YARD_LANDUSE = new Set(['farmyard', 'industrial', 'commercial', 'retail', 'quarry', 'harbour', 'port', 'railway', 'construction', 'brownfield', 'depot', 'garages']);
const YARD_MANMADE = new Set(['works', 'pier', 'quay', 'silo', 'storage_tank', 'wastewater_plant', 'bunker_silo', 'water_works', 'breakwater']);
const POI_KEYS = ['shop', 'amenity', 'craft', 'office', 'tourism', 'leisure', 'man_made', 'historic', 'emergency', 'healthcare', 'club', 'sport'];
const NOT_AREA_NATURAL = new Set(['coastline', 'tree_row', 'cliff', 'ridge', 'arete', 'valley', 'gorge', 'earth_bank']);
const AREA_KEYS = ['building', 'landuse', 'natural', 'leisure', 'amenity', 'shop', 'man_made', 'historic', 'tourism', 'power', 'industrial', 'craft', 'office', 'place', 'aeroway', 'military', 'golf', 'area:highway', 'parking'];

function isAreaWay(t, closed) {
  if (!closed) return false;
  if (t.area === 'no') return false;
  if (t.area === 'yes') return true;
  if (t.highway || t.barrier || t.railway) return false;
  if (t.waterway) return ['riverbank', 'dock', 'boatyard', 'dam', 'weir', 'lock'].includes(t.waterway);
  if (t.natural && NOT_AREA_NATURAL.has(t.natural)) return false;
  return AREA_KEYS.some((k) => t[k] !== undefined) || t.water !== undefined;
}

const r2 = (v) => Math.round(v * 100) / 100;
const ccw = (r) => (ringArea(r) >= 0 ? r : r.slice().reverse());
const cw = (r) => (ringArea(r) <= 0 ? r : r.slice().reverse());

export function extract(id, outDir) {
  const F = townFrame(id);
  const S = srcDir(id);
  const log = [];
  const say = (s) => { log.push(s); console.log(s); };

  // ------------------------------------------------------------ 1. sources, verified
  const sources = JSON.parse(fs.readFileSync(path.join(S, 'sources.json'), 'utf8'));
  if (sources.frame.E0 !== F.E0 || sources.frame.N0 !== F.N0 || sources.frame.epsg !== F.epsg || sources.frame.extractHalf !== EXTRACT_HALF)
    throw new Error('the cached download was made for another frame; re-run fetch-town.mjs');
  const pins = [['osm.json', sources.osm.sha256], ['dem/service.json', sources.dem.service?.sha256], ['dem/coverage.json', sources.dem.catalog?.sha256],
    ...sources.dem.tiles.map((t) => [t.file, t.sha256])];
  for (const [f, want] of pins) {
    const got = sha256File(path.join(S, f));
    if (got !== want) throw new Error(`${f}: SHA-256 ${got} does not match its pin ${want}`);
  }
  if (sources.dem.samples !== DEM_N || sources.dem.resolution !== DEM_RES) throw new Error('the cached DEM has another grid');

  // ------------------------------------------------------------ 2. OSM elements, projected
  const raw = JSON.parse(fs.readFileSync(path.join(S, 'osm.json'), 'utf8'));
  const order = { node: 0, way: 1, relation: 2 };
  const seen = new Set();
  const els = raw.elements.filter((e) => { const k = e.type + '/' + e.id; if (seen.has(k)) return false; seen.add(k); return true; })
    .sort((a, b) => order[a.type] - order[b.type] || a.id - b.id);
  const projMemo = new Map();
  const P = (lat, lon) => {
    const k = lat + ',' + lon;
    let p = projMemo.get(k);
    if (!p) { const [x, z] = F.local(lat, lon); p = [mm(x), mm(z)]; projMemo.set(k, p); }
    return p;
  };
  const nodeLL = new Map(), nodeTags = new Map();
  for (const e of els) if (e.type === 'node') { nodeLL.set(e.id, [e.lat, e.lon]); if (e.tags) nodeTags.set(e.id, e.tags); }
  let llConflicts = 0;
  for (const e of els) if (e.type === 'way' && e.nodes && e.geometry) e.nodes.forEach((nid, i) => {
    const g = e.geometry[i];
    if (!g) return;
    const prev = nodeLL.get(nid);
    if (prev && (prev[0] !== g.lat || prev[1] !== g.lon)) llConflicts++;
    else nodeLL.set(nid, [g.lat, g.lon]);
  });
  if (llConflicts) throw new Error(`${llConflicts} node positions disagree between ways`);
  const nodeXZ = (nid) => { const ll = nodeLL.get(nid); return P(ll[0], ll[1]); };
  const nameSet = new Set();
  for (const e of els) { nameValues(e.tags, nameSet); }

  const tally = {};
  const strip = (tags, kind) => stripTags(tags, kind, tally);

  // ------------------------------------------------------------ 3. the DEM and Y0
  const dem = mosaicDem(S, sources.dem);
  if (dem.nodata) say(`warning: ${dem.nodata} DEM samples have no data`);
  const at = sampler(dem.z, dem.n, DEM_RES, EXTRACT_HALF);

  // ------------------------------------------------------------ 4. areas (ways and multipolygons)
  const wayById = new Map(els.filter((e) => e.type === 'way').map((e) => [e.id, e]));
  const areas = [];   // { osm, tags, polys: [{ outer, holes }] } in [x, z]
  const lines = [];   // { osm, tags, pts, nodes }
  for (const e of els) {
    if (e.type !== 'way' || !e.geometry || e.geometry.length < 2) continue;
    const t = e.tags ?? {};
    const pts = e.geometry.map((g) => P(g.lat, g.lon));
    const closed = e.nodes && e.nodes.length >= 4 && e.nodes[0] === e.nodes[e.nodes.length - 1];
    if (isAreaWay(t, closed)) areas.push({ osm: `way/${e.id}`, tags: t, polys: [{ outer: ccw(pts.slice(0, -1)), holes: [] }], way: e });
    if (!isAreaWay(t, closed) || t.waterway || t.barrier) lines.push({ osm: `way/${e.id}`, tags: t, pts, nodes: e.nodes, closed, way: e });
  }
  for (const e of els) {
    if (e.type !== 'relation' || !e.members) continue;
    const t = e.tags ?? {};
    if (t.type !== 'multipolygon' && !t.building) continue;
    const loc = (g) => g.filter(Boolean).map((p) => { const q = P(p.lat, p.lon); return { x: q[0], y: q[1] }; });
    const outer = e.members.filter((m) => m.type === 'way' && m.geometry && (m.role === 'outer' || m.role === '')).map((m) => loc(m.geometry));
    const inner = e.members.filter((m) => m.type === 'way' && m.geometry && m.role === 'inner').map((m) => loc(m.geometry));
    const toA = (r) => r.map((p) => [p.x, p.y]);
    const outs = assembleRings(outer).map(toA), ins = assembleRings(inner).map(toA);
    if (!outs.length) continue;
    const polys = outs.map((o) => ({ outer: ccw(o), holes: ins.filter((h) => pointInRing(h[0][0], h[0][1], o)).map(cw) }));
    areas.push({ osm: `relation/${e.id}`, tags: t, polys, rel: e });
  }
  const clipArea = (a) => {
    const polys = [];
    let clipped = false;
    for (const p of a.polys) {
      const o = clipRing(p.outer, EXTRACT_HALF);
      if (!o) { clipped = true; continue; }
      if (o.length !== p.outer.length || o.some((q, i) => q[0] !== p.outer[i][0] || q[1] !== p.outer[i][1])) clipped = true;
      const holes = p.holes.map((h) => clipRing(h, EXTRACT_HALF)).filter(Boolean).map(cw);
      polys.push({ outer: ccw(o), ...(holes.length ? { holes } : {}) });
    }
    return polys.length ? { polys, clipped } : null;
  };
  const areaOut = (a, kind) => {
    const c = clipArea(a);
    if (!c) return null;
    const A = c.polys.reduce((s, p) => s + Math.abs(ringArea(p.outer)) - (p.holes ?? []).reduce((u, h) => u + Math.abs(ringArea(h)), 0), 0);
    return { osm: a.osm, tags: strip(a.tags, kind), polys: c.polys, area: r2(A), ...(c.clipped ? { clipped: true } : {}) };
  };
  const lineOut = (l, kind) => {
    const parts = clipLine(l.pts, EXTRACT_HALF);
    if (!parts.length) return null;
    const full = parts.length === 1 && parts[0].length === l.pts.length;
    return { osm: l.osm, tags: strip(l.tags, kind), parts, length: r2(parts.reduce((s, p) => s + lineLength(p), 0)), ...(full ? {} : { clipped: true }) };
  };

  // ------------------------------------------------------------ 5. water and Y0
  const isWaterArea = (t) => t.natural === 'water' || ['riverbank', 'dock', 'boatyard'].includes(t.waterway) || ['reservoir', 'basin'].includes(t.landuse);
  const isRiverArea = (t) => t.water === 'river' || t.waterway === 'riverbank' || t.water === 'canal' || t.water === 'lock';
  const riverAreas = areas.filter((a) => isWaterArea(a.tags) && isRiverArea(a.tags));
  const inPolys = (x, z, polys) => polys.some((p) => pointInRing(x, z, p.outer) && !(p.holes ?? []).some((h) => pointInRing(x, z, h)));
  const levels = [];
  let minArea = Infinity;
  for (const a of riverAreas) {
    const vals = [];
    for (const p of a.polys) {
      const [x0, z0, x1, z1] = ringBox(p.outer);
      for (let zz = Math.ceil(Math.max(z0, -EXTRACT_HALF)); zz <= Math.min(z1, EXTRACT_HALF); zz++)
        for (let xx = Math.ceil(Math.max(x0, -EXTRACT_HALF)); xx <= Math.min(x1, EXTRACT_HALF); xx++) {
          if (!inPolys(xx, zz, [p])) continue;
          const v = dem.z[(zz + EXTRACT_HALF) * dem.n + (xx + EXTRACT_HALF)];
          if (Number.isFinite(v)) vals.push(v);
        }
    }
    if (!vals.length) continue;
    vals.sort((u, v) => u - v);
    const q = (f) => vals[Math.min(vals.length - 1, Math.floor(f * vals.length))];
    levels.push({ osm: a.osm, samples: vals.length, min: vals[0], p05: q(0.05), p50: q(0.5), p95: q(0.95), max: vals[vals.length - 1] });
    minArea = Math.min(minArea, vals[0]);
  }
  let minLine = Infinity;
  for (const l of lines.filter((x) => x.tags.waterway === 'river')) {
    for (const part of clipLine(l.pts, EXTRACT_HALF)) for (let i = 1; i < part.length; i++) {
      const [ax, az] = part[i - 1], [bx, bz] = part[i], L = Math.hypot(bx - ax, bz - az);
      for (let d = 0; d <= L; d += 1) { const v = at(ax + (bx - ax) * d / (L || 1), az + (bz - az) * d / (L || 1)); if (v < minLine) minLine = v; }
    }
  }
  const lowest = Math.min(minArea, minLine);
  if (!Number.isFinite(lowest)) throw new Error('no river in the extract: Y0 needs the lowest river point');
  const Y0 = mm(lowest - 2);
  const yAt = (x, z) => mm(at(x, z) - Y0);

  // ------------------------------------------------------------ 6. buildings, classified by the study's rules
  const T = { name: id, nodes: [], ways: [], rels: [] };
  const tagsToNode = new Map();
  for (const e of els) if (e.type === 'node' && e.tags) {
    const p = P(e.lat, e.lon);
    T.nodes.push({ id: `node/${e.id}`, tags: e.tags, p: { x: p[0], y: p[1] } });
    tagsToNode.set(e.tags, `node/${e.id}`);
  }
  for (const e of els) if (e.type === 'way' && e.geometry) {
    const g = e.geometry.filter(Boolean).map((q) => { const p = P(q.lat, q.lon); return { x: p[0], y: p[1] }; });
    T.ways.push({ id: `way/${e.id}`, tags: e.tags || {}, g, closed: !!(e.nodes && e.nodes.length > 3 && e.nodes[0] === e.nodes[e.nodes.length - 1]), refs: e.nodes || [] });
  }
  for (const e of els) if (e.type === 'relation' && e.members) {
    const loc = (g) => g.filter(Boolean).map((q) => { const p = P(q.lat, q.lon); return { x: p[0], y: p[1] }; });
    const outer = e.members.filter((m) => m.type === 'way' && m.geometry && (m.role === 'outer' || m.role === '')).map((m) => loc(m.geometry));
    const inner = e.members.filter((m) => m.type === 'way' && m.geometry && m.role === 'inner').map((m) => loc(m.geometry));
    for (const ring of assembleRings(outer)) T.rels.push({ id: `relation/${e.id}`, tags: e.tags || {}, poly: ring, holes: assembleRings(inner) });
  }
  // the building candidates exactly as buildTownFrom collects them, keyed by the first point object of their ring
  const candidates = [];
  for (const w of T.ways) { const poly = w.closed ? w.g.slice(0, -1) : null; if (w.tags.building && poly && poly.length >= 3) candidates.push({ id: w.id, tags: w.tags, poly, holes: [], key: poly[0] }); }
  for (const r of T.rels) if (r.tags.building) candidates.push({ id: r.id, tags: r.tags, poly: r.poly, holes: r.holes, key: r.poly[0] });
  const town = buildTownFrom(T, id);
  lotAnalysis(town); // turns street-front buildings with their own front lot back into standalone ones
  const classified = new Map(town.buildings.map((b) => [b.poly[0], b]));
  const partCount = {};
  for (const c of candidates) partCount[c.id] = (partCount[c.id] ?? 0) + 1;
  const partSeen = {};
  const buildings = [];
  const hostOf = new Map(); // node id -> building id
  for (const c of candidates) {
    if (c.tags.building === 'no') continue;
    const k = (partSeen[c.id] = (partSeen[c.id] ?? -1) + 1);
    const outer = c.poly.map((p) => [p.x, p.y]);
    const b = classified.get(c.key);
    const cen = b ? [b.c.x, b.c.y] : ringCentroid(outer);
    if (!inBox(cen, EXTRACT_HALF)) continue;
    const rec = {
      osm: c.id, ...(partCount[c.id] > 1 ? { part: k } : {}), poly: ccw(outer), area: r2(Math.abs(ringArea(outer))),
      c: [mm(cen[0]), mm(cen[1])], y: yAt(cen[0], cen[1]), inPlay: inBox(cen, PLAY_HALF), tags: strip(c.tags, c.id.startsWith('way') ? 'way' : 'relation'),
    };
    const holes = (c.holes ?? []).map((h) => cw(h.map((p) => [p.x, p.y]))).filter((h) => pointInRing(h[0][0], h[0][1], outer));
    if (holes.length) rec.holes = holes;
    if (!b) {
      Object.assign(rec, { type: 'unknown', business: 'Unknown', reason: Math.abs(ringArea(outer)) < 6 ? 'footprint under 6 m2' : 'footprint of 80,000 m2 or more' });
    } else {
      rec.type = b.type;
      rec.business = BUSINESS[b.type] ?? 'Unknown';
      if (b.sub !== undefined) rec.sub = b.sub;
      if (b.inferred) rec.inferred = true;
      if (b.form) rec.form = b.form;
      rec.lu = Object.keys(b.lu).sort();
      const ctx = {};
      for (const f of ['inFuel', 'nearFuel', 'inMarina', 'inWorks', 'inSawmill']) if (b[f]) ctx[f] = true;
      rec.ctx = ctx;
      rec.pois = b.pois.map((t) => ({ osm: tagsToNode.get(t), tags: strip(t, 'node') })).sort((u, v) => (u.osm < v.osm ? -1 : u.osm > v.osm ? 1 : 0));
      for (const p of rec.pois) if (!hostOf.has(p.osm)) hostOf.set(p.osm, c.id);
      if (b.f) { rec.street = b.f.street.id; rec.setback = r2(b.f.setback); }
    }
    buildings.push(rec);
  }

  // ------------------------------------------------------------ 7. roads (topology kept)
  const insideN = (nid) => inBox(nodeXZ(nid), EXTRACT_HALF);
  const segHits = (a, b) => clipLine([a, b], EXTRACT_HALF).length > 0;
  const usedNodes = new Set();
  const pieces = (e) => {
    const ns = e.nodes ?? [];
    const keep = ns.map((nid, i) => insideN(nid) || (i > 0 && segHits(nodeXZ(ns[i - 1]), nodeXZ(nid))) || (i + 1 < ns.length && segHits(nodeXZ(nid), nodeXZ(ns[i + 1]))));
    const out = [];
    let cur = null;
    keep.forEach((k, i) => { if (k) { if (!cur) { cur = []; out.push(cur); } cur.push(ns[i]); } else cur = null; });
    return out.filter((p) => p.length >= 2);
  };
  const roadWays = [], railWays = [], serviceWays = [];
  for (const e of els) {
    if (e.type !== 'way' || !e.nodes) continue;
    const t = e.tags ?? {};
    const isHw = !!t.highway, isRail = !t.highway && RAIL.has(t.railway);
    if (!isHw && !isRail) continue;
    const ps = pieces(e);
    ps.forEach((nodes, k) => {
      for (const nid of nodes) usedNodes.add(nid);
      const rec = { osm: `way/${e.id}`, nodes, tags: strip(t, 'way'), ...(ps.length > 1 ? { part: k } : {}), ...(nodes.length < e.nodes.length ? { clipped: true } : {}) };
      (isHw ? roadWays : railWays).push(rec);
      if (t.highway === 'service') serviceWays.push({ ...rec, pts: nodes.map(nodeXZ) });
    });
  }
  const roadNodes = [...usedNodes].sort((a, b) => a - b).map((nid) => {
    const [x, z] = nodeXZ(nid), inside = inBox([x, z], EXTRACT_HALF);
    const tg = nodeTags.get(nid);
    return { id: nid, x, z, ...(inside ? { y: yAt(x, z) } : { outside: true }), ...(tg ? { tags: strip(tg, 'node') } : {}) };
  });
  // bridge spans: bridge ways of drivable roads and railways, merged per crossing (tools/scout/render-map.mjs bridges())
  const bridgeLines = lines.filter((l) => l.tags.bridge && l.tags.bridge !== 'no' && (l.tags.highway || RAIL.has(l.tags.railway)));
  const grp = bridgeLines.map((_, i) => i);
  const find = (i) => (grp[i] === i ? i : (grp[i] = find(grp[i])));
  for (let i = 0; i < bridgeLines.length; i++) for (let j = i + 1; j < bridgeLines.length; j++) {
    const a = bridgeLines[i], b = bridgeLines[j];
    const shareNode = a.nodes.some((n) => b.nodes.includes(n));
    if (shareNode && !!a.tags.railway === !!b.tags.railway) grp[find(i)] = find(j);
  }
  const riverLines = lines.filter((l) => ['river', 'canal'].includes(l.tags.waterway)), streamLines = lines.filter((l) => ['stream', 'brook', 'ditch', 'drain'].includes(l.tags.waterway));
  const waterAreas = areas.filter((a) => isWaterArea(a.tags));
  const segX = (p1, p2, p3, p4) => {
    const d = (p2[0] - p1[0]) * (p4[1] - p3[1]) - (p2[1] - p1[1]) * (p4[0] - p3[0]);
    if (d === 0) return false;
    const u = ((p3[0] - p1[0]) * (p4[1] - p3[1]) - (p3[1] - p1[1]) * (p4[0] - p3[0])) / d, v = ((p3[0] - p1[0]) * (p2[1] - p1[1]) - (p3[1] - p1[1]) * (p2[0] - p1[0])) / d;
    return u >= 0 && u <= 1 && v >= 0 && v <= 1;
  };
  const crosses = (segs, ls) => ls.some((l) => segs.some(([a, b]) => l.pts.some((q, k) => k > 0 && segX(a, b, l.pts[k - 1], q))));
  const spanMap = new Map();
  bridgeLines.forEach((l, i) => { const r = find(i); if (!spanMap.has(r)) spanMap.set(r, []); spanMap.get(r).push(l); });
  const bridgeSpans = [];
  for (const ls of spanMap.values()) {
    const segs = ls.flatMap((l) => l.pts.slice(1).map((q, k) => [l.pts[k], q]));
    const all = ls.flatMap((l) => l.pts), c = [all.reduce((s, p) => s + p[0], 0) / all.length, all.reduce((s, p) => s + p[1], 0) / all.length];
    if (!inBox(c, EXTRACT_HALF)) continue;
    let over = 'other';
    if (crosses(segs, riverLines) || waterAreas.some((w) => isRiverArea(w.tags) && inPolys(c[0], c[1], w.polys))) over = 'river';
    else if (crosses(segs, streamLines)) over = 'stream';
    bridgeSpans.push({ ways: ls.map((l) => l.osm).sort(), over, rail: !!ls[0].tags.railway, length: r2(segs.reduce((s, [a, b]) => s + Math.hypot(b[0] - a[0], b[1] - a[1]), 0)),
      c: [mm(c[0]), mm(c[1])], inPlay: inBox(c, PLAY_HALF), highway: ls[0].tags.highway ?? null });
  }
  bridgeSpans.sort((a, b) => (a.ways[0] < b.ways[0] ? -1 : 1));
  const bridgeAreas = areas.filter((a) => a.tags.man_made === 'bridge').map((a) => areaOut(a, 'way')).filter(Boolean);

  // ------------------------------------------------------------ 8. sites
  const byOsm = (a, b) => (a.osm < b.osm ? -1 : a.osm > b.osm ? 1 : (a.part ?? 0) - (b.part ?? 0));
  const kindOf = (o) => (o.startsWith('way') ? 'way' : o.startsWith('relation') ? 'relation' : 'node');
  const nodePoint = (e) => { const p = P(e.lat, e.lon); return { osm: `node/${e.id}`, p, tags: strip(e.tags, 'node'), ...(inBox(p, EXTRACT_HALF) ? { y: yAt(p[0], p[1]) } : {}) }; };
  const nodesIn = (pred) => els.filter((e) => e.type === 'node' && e.tags && pred(e.tags) && inBox(P(e.lat, e.lon), EXTRACT_HALF)).map(nodePoint);
  const areasWhere = (pred) => areas.filter((a) => pred(a.tags)).map((a) => areaOut(a, kindOf(a.osm))).filter(Boolean).sort(byOsm);
  const sites = {
    parking: areasWhere((t) => t.amenity === 'parking' && !t.building),
    parkingSpaces: [...areasWhere((t) => t.amenity === 'parking_space'), ...nodesIn((t) => t.amenity === 'parking_space')].sort(byOsm),
    fuel: [...areasWhere((t) => t.amenity === 'fuel' && !t.building), ...nodesIn((t) => t.amenity === 'fuel')].sort(byOsm),
    yards: areasWhere((t) => YARD_LANDUSE.has(t.landuse) || YARD_MANMADE.has(t.man_made) || t.leisure === 'marina' || t.tourism === 'caravan_site' || t.tourism === 'camp_site'),
    areaHighway: areasWhere((t) => t['area:highway'] !== undefined),
    service: serviceWays.map((s) => ({ osm: s.osm, nodes: s.nodes, pts: s.pts, tags: s.tags, ...(s.part !== undefined ? { part: s.part } : {}), ...(s.clipped ? { clipped: true } : {}) })).sort(byOsm),
    barriers: [...lines.filter((l) => l.tags.barrier).map((l) => lineOut(l, 'way')).filter(Boolean), ...areasWhere((t) => t.barrier !== undefined)].sort(byOsm),
    barrierNodes: nodesIn((t) => t.barrier !== undefined),
    entrances: nodesIn((t) => t.entrance !== undefined || t.door !== undefined),
    points: nodesIn((t) => POI_KEYS.some((k) => t[k] !== undefined)).map((p) => ({ ...p, ...(hostOf.has(p.osm) ? { host: hostOf.get(p.osm) } : {}) })),
  };

  // ------------------------------------------------------------ 9. land use and the rest of the map
  const isLanduse = (t) => !t.building && !isWaterArea(t) && (t.landuse !== undefined || (t.natural !== undefined && !['water', 'bay', 'strait'].includes(t.natural)) ||
    (t.leisure !== undefined && t.leisure !== 'marina') || t.amenity === 'grave_yard' || t.golf !== undefined || t.aeroway !== undefined || t.place === 'farm');
  const landuse = {
    areas: areasWhere(isLanduse),
    lines: lines.filter((l) => ['tree_row', 'cliff', 'ridge', 'earth_bank'].includes(l.tags.natural) || ['embankment', 'dyke', 'cutline'].includes(l.tags.man_made) ||
      ['line', 'minor_line', 'cable'].includes(l.tags.power) || l.tags.barrier === 'hedge' || l.tags.aeroway !== undefined).map((l) => lineOut(l, 'way')).filter(Boolean).sort(byOsm),
    points: nodesIn((t) => t.natural !== undefined || t.power !== undefined || ['mast', 'tower', 'water_tower', 'antenna', 'communications_tower'].includes(t.man_made) || t.aeroway !== undefined),
  };

  // ------------------------------------------------------------ 10. water
  const water = {
    areas: areasWhere(isWaterArea).map((a) => ({ ...a, ...(isRiverArea(areas.find((x) => x.osm === a.osm).tags) ? { river: true } : {}) })),
    lines: lines.filter((l) => WATER_LINES.has(l.tags.waterway)).map((l) => { const o = lineOut(l, 'way'); return o && { ...o, ...(l.tags.waterway === 'river' ? { river: true } : {}), ...(l.tags.lock === 'yes' ? { lock: true } : {}) }; })
      .filter(Boolean).sort(byOsm),
    structures: [...lines.filter((l) => WATER_STRUCT.has(l.tags.waterway) || l.tags.lock === 'yes').map((l) => lineOut(l, 'way')).filter(Boolean),
      ...areasWhere((t) => WATER_STRUCT.has(t.waterway)), ...nodesIn((t) => WATER_STRUCT.has(t.waterway) || t.lock === 'yes')].sort(byOsm),
    levels: levels.map((l) => ({ osm: l.osm, samples: l.samples, min: mm(l.min - Y0), p05: mm(l.p05 - Y0), p50: mm(l.p50 - Y0), p95: mm(l.p95 - Y0), max: mm(l.max - Y0) })),
  };

  // ------------------------------------------------------------ 11. the DEM file
  const yGrid = new Float32Array(dem.n * dem.n);
  let yMin = Infinity, yMax = -Infinity;
  for (let k = 0; k < yGrid.length; k++) { const v = Math.fround(dem.z[k] - Y0); yGrid[k] = v; if (v < yMin) yMin = v; if (v > yMax) yMax = v; }
  const demBytes = Buffer.from(yGrid.buffer);
  // play-area numbers
  let pMin = Infinity, pMax = -Infinity;
  for (let j = 0; j < dem.n; j++) { const z = -EXTRACT_HALF + j * DEM_RES; if (Math.abs(z) > PLAY_HALF) continue;
    for (let i = 0; i < dem.n; i++) { const x = -EXTRACT_HALF + i * DEM_RES; if (Math.abs(x) > PLAY_HALF) continue; const v = dem.z[j * dem.n + i]; if (v < pMin) pMin = v; if (v > pMax) pMax = v; } }
  const demJson = {
    format: 'backroads-towndata-dem', version: 1, file: 'dem_1m.f32', encoding: 'float32 little-endian, no header',
    layout: 'k = j*n + i; x = x0 + i*res, z = z0 + j*res (rows south to north, columns west to east); value = y = elevation - Y0',
    n: dem.n, res: DEM_RES, x0: -EXTRACT_HALF, z0: -EXTRACT_HALF, Y0, verticalDatum: 'NAVD88 (3DEP), metres',
    yMin: mm(yMin), yMax: mm(yMax), elevationMin: mm(dem.min), elevationMax: mm(dem.max), playArea: { elevationMin: mm(pMin), elevationMax: mm(pMax) },
    nodata: dem.nodata, seams: { tiles: dem.tiles.length, overlaps: dem.seams.pairs, compared: dem.seams.compared, differing: dem.seams.differing, maxAbs: dem.seams.maxAbs },
    lowestRiver: { inRiverAreas: Number.isFinite(minArea) ? mm(minArea) : null, alongRiverLines: Number.isFinite(minLine) ? mm(minLine) : null, Y0rule: 'Y0 = lowest river point - 2 m' },
    source: 'USGS 3D Elevation Program (3DEP) 1 m lidar DEM via the 3DEPElevation ImageServer, bilinear to whole metres of the local frame (public domain)',
  };

  // ------------------------------------------------------------ 12. zones (overview parameters KERB_ZONE, DOWNTOWN_ZONE)
  const zones = measureZones(buildings, roadWays, nodeXZ, F);

  // ------------------------------------------------------------ 13. write
  const frame = {
    crs: `EPSG:${F.epsg}`, projection: 'UTM (WGS84), Krueger series to n^3 (tools/scout/geo.mjs; Backroads.Core.WorldBake.TownFrame)', zone: F.zone, northern: true,
    originEasting: F.E0, originNorthing: F.N0, originLat: +F.originLat.toFixed(9), originLon: +F.originLon.toFixed(9), Y0,
    axes: 'x = easting - originEasting (east), z = northing - originNorthing (north), y = elevation - Y0 (up); metres; Unity position (x, y, z)',
    rule: 'origin = the scout 2 km square centre: the town place node rounded to 10 m in UTM, plus towns.json play2 (dx, dy)',
    placeNode: F.placeNode, placeLatLon: F.placeLatLon, play2: F.play2,
  };
  const checks = [[0, 0], [1000, 1000], [-1324, -1324], [1324, 1324], [-1024, 1024], [512.25, -777.75]].map(([x, z]) => {
    const [lat, lon] = F.latlon(x, z);
    return { lat: +lat.toFixed(10), lon: +lon.toFixed(10), x, z, ...(() => { const [xx, zz] = F.local(+lat.toFixed(10), +lon.toFixed(10)); return { xFromLatLon: +xx.toFixed(6), zFromLatLon: +zz.toFixed(6) }; })() };
  });
  frame.checks = checks;
  const files = {};
  const put = (rel, data) => { writeFile(path.join(outDir, rel), data); files[rel] = data; };
  const credit = 'Map data (c) OpenStreetMap contributors, available under the Open Database Licence 1.0 (ODbL): https://www.openstreetmap.org/copyright\n' +
    'Elevation: U.S. Geological Survey, 3D Elevation Program (3DEP), 1 m lidar DEM (public domain).\n' +
    'This folder is a derivative database of OpenStreetMap (ODbL 1.0): names, addresses and contact details removed, re-projected to a local frame.\n';
  fs.rmSync(outDir, { recursive: true, force: true });
  put('roads.json', stableJson({ format: FORMAT, kind: 'roads', version: EXTRACT_VERSION, note: 'ways: highway ways, cut to the nodes inside the extract square plus one beyond; nodes: x, z (y inside the square: bare-earth DEM, not the deck on bridges)',
    ways: roadWays.sort(byOsm), rail: railWays.sort(byOsm), nodes: roadNodes, bridgeSpans, bridgeAreas }));
  put('buildings.json', stableJson({ format: FORMAT, kind: 'buildings', version: EXTRACT_VERSION,
    note: 'type, sub, inferred, form: tools/osm/measure.mjs typeOf and buildTownFrom (lotAnalysis for form); business: Backroads.Core.WorldBake.BusinessType; lu, ctx, pois: the classifier inputs; area: outer ring, m2; y: DEM at the centroid',
    buildings: buildings.sort(byOsm) }));
  put('sites.json', stableJson({ format: FORMAT, kind: 'sites', version: EXTRACT_VERSION, ...sites }));
  put('landuse.json', stableJson({ format: FORMAT, kind: 'landuse', version: EXTRACT_VERSION, ...landuse }));
  put('water.json', stableJson({ format: FORMAT, kind: 'water', version: EXTRACT_VERSION, note: 'levels: DEM y inside each river area (the lidar is hydro-flattened: flat per reach)', ...water }));
  put('dem/dem_1m.f32', demBytes);
  put('dem/dem.json', stableJson(demJson));
  put('credit.txt', credit);
  const srcOut = {
    osm: { ...sources.osm, file: undefined, cache: 'osm.json' },
    dem: { ...sources.dem, tiles: sources.dem.tiles.map((t) => ({ ...t, file: undefined, cache: t.file })),
      service: sources.dem.service && { ...sources.dem.service, file: undefined, cache: 'dem/service.json' },
      catalog: sources.dem.catalog && { ...sources.dem.catalog, file: undefined, cache: 'dem/coverage.json' }, oneMetre: coverageSummary(S, sources) },
    frame: { ...sources.frame, placeNode: undefined }, cacheFolder: 'artifacts/world-src/<town id>/ (gitignored); "cache" paths are relative to it',
  };
  put('sources.json', stableJson(srcOut));

  // ------------------------------------------------------------ 14. the leak scan (real names)
  const names = [...nameSet].filter((s) => s.length >= 4).map((s) => s.toLowerCase());
  const leaks = [];
  for (const [rel, data] of Object.entries(files)) {
    if (!rel.endsWith('.json') && !rel.endsWith('.txt')) continue;
    const text = String(data).toLowerCase();
    for (const nmv of names) if (text.includes(nmv)) leaks.push(`${rel}: "${nmv}"`);
  }
  if (leaks.length) throw new Error(`real names survived the strip:\n${leaks.join('\n')}`);

  // ------------------------------------------------------------ 15. counts and the manifest
  const stats = statsOf({ buildings, roadWays, nodeXZ, bridgeSpans, water, sites, landuse, dem, demJson });
  const toolFiles = ['tools/world/extract-town.mjs', 'tools/world/town-lib.mjs', 'tools/world/dem-lib.mjs', 'tools/osm/measure.mjs', 'tools/osm/lib.mjs', 'tools/scout/geo.mjs'];
  const manifest = {
    format: FORMAT, version: EXTRACT_VERSION, townName: TOWN_NAME,
    frame, playArea: { half: PLAY_HALF, bbox: [-PLAY_HALF, -PLAY_HALF, PLAY_HALF, PLAY_HALF] }, extract: { half: EXTRACT_HALF, skirt: SKIRT, bbox: [-EXTRACT_HALF, -EXTRACT_HALF, EXTRACT_HALF, EXTRACT_HALF], overpassBbox: F.query },
    osm: { timestamp: sources.osm.osmBase, endpoint: sources.osm.endpoint, sha256: sources.osm.sha256, elements: sources.osm.counts },
    dem: { file: 'dem/dem_1m.f32', n: dem.n, res: DEM_RES, Y0, sha256: sha256(demBytes) },
    strip: { exact: STRIP.exact, prefixes: STRIP.prefixes, contains: STRIP.contains, refKeptOn: `highway ways, every part matching ${STATE_REF.source}`, dropped: tally, leakScan: { names: names.length, leaks: 0 } },
    tools: Object.fromEntries(toolFiles.map((f) => [f, sha256File(path.join(REPO, f))])), extractVersion: EXTRACT_VERSION,
    zones, counts: stats,
    credit: ['(c) OpenStreetMap contributors, ODbL 1.0', 'USGS 3D Elevation Program (public domain)'],
    files: Object.fromEntries(Object.entries(files).sort(([a], [b]) => (a < b ? -1 : 1)).map(([rel, data]) => [rel, { bytes: Buffer.byteLength(data), sha256: sha256(Buffer.isBuffer(data) ? data : Buffer.from(data)) }])),
  };
  writeFile(path.join(outDir, 'manifest.json'), stableJson(manifest));
  return { manifest, stats, Y0, log };
}

function coverageSummary(S, sources) {
  try {
    const c = JSON.parse(fs.readFileSync(path.join(S, 'dem', 'coverage.json'), 'utf8'));
    const items = (c.features ?? []).map((f) => f.attributes).filter((a) => a.LowPS <= 1.5 || /1 ?meter/i.test(a.title ?? ''));
    return [...new Map(items.map((a) => [a.Name, { project: a.Name, title: a.title, lowPS: a.LowPS, acquired: a.AcquisitionDate ? new Date(a.AcquisitionDate).toISOString().slice(0, 10) : null }])).values()]
      .sort((a, b) => (a.project < b.project ? -1 : 1));
  } catch { return null; }
}

// KERB_ZONE: the village's built-up hull + 20 m (no street has sidewalk=* tags); DOWNTOWN_ZONE: street-front hull + 30 m.
function measureZones(buildings, roadWays, nodeXZ, F) {
  const [px, pz] = F.local(F.placeLatLon[0], F.placeLatLon[1]);
  const cands = buildings.filter((b) => b.type !== 'accessory' && b.type !== 'roof' && b.area >= 40);
  // single-linkage clusters of building centroids, 60 m
  const parent = cands.map((_, i) => i);
  const find = (i) => (parent[i] === i ? i : (parent[i] = find(parent[i])));
  for (let i = 0; i < cands.length; i++) for (let j = i + 1; j < cands.length; j++)
    if (Math.hypot(cands[i].c[0] - cands[j].c[0], cands[i].c[1] - cands[j].c[1]) <= 60) parent[find(i)] = find(j);
  let near = 0;
  cands.forEach((b, i) => { if (Math.hypot(b.c[0] - px, b.c[1] - pz) < Math.hypot(cands[near].c[0] - px, cands[near].c[1] - pz)) near = i; });
  const root = find(near);
  const village = cands.filter((_, i) => find(i) === root);
  const vpts = village.flatMap((b) => b.poly);
  const kerb = bufferHull(vpts, 20);
  const sidewalkTagged = roadWays.filter((w) => Object.keys(w.tags).some((k) => k === 'sidewalk' || k.startsWith('sidewalk:'))).length;
  const sidewalkWays = roadWays.filter((w) => w.tags.footway === 'sidewalk');
  const inside = (p, r) => { let s = false; for (let i = 0, j = r.length - 1; i < r.length; j = i++) { const [xi, zi] = r[i], [xj, zj] = r[j]; if ((zi > p[1]) !== (zj > p[1]) && p[0] < ((xj - xi) * (p[1] - zi)) / (zj - zi) + xi) s = !s; } return s; };
  const swIn = sidewalkWays.filter((w) => w.nodes.every((n) => inside(nodeXZ(n), kerb))).length;
  const front = buildings.filter((b) => b.form === 'streetfront');
  const down = front.length ? bufferHull(front.flatMap((b) => b.poly), 30) : null;
  const box = (r) => { const xs = r.map((p) => p[0]), zs = r.map((p) => p[1]); return [mm(Math.min(...xs)), mm(Math.min(...zs)), mm(Math.max(...xs)), mm(Math.max(...zs))]; };
  return {
    kerbZone: {
      rule: 'no street carries sidewalk=* tags, so: the convex hull of the village footprints (buildings of 40 m2 or more, not accessory, in the 60 m single-linkage cluster nearest the place node) + 20 m',
      polygon: kerb, bbox: box(kerb), areaHa: r2(Math.abs(ringArea(kerb)) / 1e4), villageBuildings: village.length,
      sidewalkTaggedWays: sidewalkTagged, sidewalkFootways: sidewalkWays.length, sidewalkFootwaysInside: swIn,
    },
    downtownZone: down ? {
      rule: 'the convex hull of the form = streetfront footprints + 30 m', polygon: down, bbox: box(down), areaHa: r2(Math.abs(ringArea(down)) / 1e4),
      buildings: front.map((b) => b.osm),
    } : { rule: 'the convex hull of the form = streetfront footprints + 30 m', polygon: null, buildings: [] },
  };
}
function convexHull(pts) {
  const p = [...pts].sort((a, b) => a[0] - b[0] || a[1] - b[1]);
  if (p.length < 3) return p;
  const cr = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
  const lo = [], up = [];
  for (const v of p) { while (lo.length >= 2 && cr(lo[lo.length - 2], lo[lo.length - 1], v) <= 0) lo.pop(); lo.push(v); }
  for (const v of p.reverse()) { while (up.length >= 2 && cr(up[up.length - 2], up[up.length - 1], v) <= 0) up.pop(); up.push(v); }
  return lo.slice(0, -1).concat(up.slice(0, -1));
}
// The convex hull grown by d metres (each hull vertex replaced by a 16-gon of radius d, then the hull again).
function bufferHull(pts, d) {
  const h = convexHull(pts);
  const grown = [];
  for (const [x, z] of h) for (let k = 0; k < 16; k++) { const a = (k / 16) * 2 * Math.PI; grown.push([x + d * Math.cos(a), z + d * Math.sin(a)]); }
  return convexHull(grown).map((p) => [mm(p[0]), mm(p[1])]);
}

function statsOf({ buildings, roadWays, nodeXZ, bridgeSpans, water, sites, landuse, dem, demJson }) {
  const play = buildings.filter((b) => b.inPlay);
  const by = (list, f) => { const o = {}; for (const x of list) { const k = f(x); o[k] = (o[k] ?? 0) + 1; } return Object.fromEntries(Object.entries(o).sort((a, b) => b[1] - a[1] || (a[0] < b[0] ? -1 : 1))); };
  const km = (half) => {
    const o = {};
    for (const w of roadWays) {
      const parts = clipLine(w.nodes.map(nodeXZ), half);
      const L = parts.reduce((s, p) => s + lineLength(p), 0);
      if (L > 0) o[w.tags.highway] = (o[w.tags.highway] ?? 0) + L / 1000;
    }
    return Object.fromEntries(Object.entries(o).sort((a, b) => b[1] - a[1]).map(([k, v]) => [k, Math.round(v * 100) / 100]));
  };
  return {
    buildings: { extract: buildings.length, playArea: play.length, playAreaByBusiness: by(play, (b) => b.business), playAreaByType: by(play, (b) => b.type),
      playAreaInferred: play.filter((b) => b.inferred).length, playAreaStreetfront: play.filter((b) => b.form === 'streetfront').length, playAreaTaggedBuilding: play.filter((b) => b.tags.building !== 'yes').length },
    roadsKm: { playArea: km(PLAY_HALF), extract: km(EXTRACT_HALF) },
    bridges: bridgeSpans.map((b) => ({ over: b.over, rail: b.rail, length: b.length, inPlay: b.inPlay, highway: b.highway })),
    water: { areas: water.areas.length, riverAreas: water.areas.filter((a) => a.river).length, lines: by(water.lines, (l) => l.tags.waterway), structures: by(water.structures, (s) => s.tags.waterway ?? (s.tags.lock ? 'lock' : 'other')) },
    sites: Object.fromEntries(Object.entries(sites).map(([k, v]) => [k, v.length])),
    landuse: { areas: by(landuse.areas, (a) => Object.entries(a.tags).filter(([k]) => ['landuse', 'natural', 'leisure', 'amenity', 'aeroway'].includes(k)).map(([k, v]) => `${k}=${v}`).join(',') || 'other'), lines: landuse.lines.length, points: landuse.points.length },
    dem: { n: dem.n, res: DEM_RES, elevationMin: demJson.elevationMin, elevationMax: demJson.elevationMax, playArea: demJson.playArea, yMin: demJson.yMin, yMax: demJson.yMax, nodata: dem.nodata },
  };
}

// ---------------------------------------------------------------- CLI
function filesEqual(a, b) {
  const list = (d) => { const out = []; const walk = (p) => { for (const f of fs.readdirSync(p, { withFileTypes: true })) { const q = path.join(p, f.name); if (f.isDirectory()) walk(q); else out.push(path.relative(d, q).replace(/\\/g, '/')); } }; walk(d); return out.sort(); };
  const la = list(a), lb = list(b), diffs = [];
  for (const f of new Set([...la, ...lb])) {
    if (!la.includes(f) || !lb.includes(f)) { diffs.push(`${f}: only in ${la.includes(f) ? a : b}`); continue; }
    if (!fs.readFileSync(path.join(a, f)).equals(fs.readFileSync(path.join(b, f)))) diffs.push(`${f}: differs`);
  }
  return diffs;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const id = args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--out');
  if (!id) { console.error('usage: node tools/world/extract-town.mjs <town-id> [--out DIR] [--check]'); process.exit(1); }
  const check = args.includes('--check');
  const target = townDataDir(id);
  const out = check ? fs.mkdtempSync(path.join(os.tmpdir(), 'towndata-')) : (args.includes('--out') ? path.resolve(args[args.indexOf('--out') + 1]) : target);
  try {
    const t0 = Date.now();
    const r = extract(id, out);
    const s = r.stats;
    console.log(`${id}: TownData written to ${out} in ${((Date.now() - t0) / 1000).toFixed(1)} s; Y0 ${r.Y0} m`);
    console.log(`  buildings: ${s.buildings.extract} in the extract, ${s.buildings.playArea} in the play area: ${JSON.stringify(s.buildings.playAreaByBusiness)}`);
    console.log(`  roads km (play area): ${JSON.stringify(s.roadsKm.playArea)}`);
    console.log(`  bridges: ${JSON.stringify(s.bridges)}`);
    console.log(`  water: ${JSON.stringify(s.water)}`);
    console.log(`  dem: ${JSON.stringify(s.dem)}`);
    if (check) {
      const d = filesEqual(out, target);
      if (d.length) { console.error('check FAILED:\n' + d.join('\n')); process.exitCode = 1; } else console.log(`check: identical to ${target} (${Object.keys(r.manifest.files).length + 1} files)`);
    }
  } catch (e) { console.error(e.stack ?? String(e)); process.exitCode = 1; } finally { if (check) fs.rmSync(out, { recursive: true, force: true }); }
}
