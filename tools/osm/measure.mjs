// Measures how real small towns lay out their sites (parking lots, fuel stations, shops, yards, driveways)
// and their buildings (footprints, storeys, heights, roof shapes), from the OpenStreetMap data fetch.mjs
// cached under artifacts/osm/raw/.
//
//   node tools/osm/measure.mjs
//
// Writes (committed):
//   Game/Assets/Content/sites/siteProfile.json      site layout numbers per business type (n + quantiles)
//   Game/Assets/Content/sites/buildingProfile.json  building footprints, storeys, heights, roof shapes per type and OSM tag
//   Game/Assets/Content/sites/siteTemplates.json    real example layouts in the building's street frame, with OSM ids
// and (not committed) artifacts/osm/report.md, artifacts/osm/candidates.json for the research note and render.mjs.
//
// Two town sets: artifacts/osm/raw/ (the 17 site-study towns, U23) feeds everything; artifacts/osm/raw-tags/ (the towns
// with dense storey and roof tags, U28, fetched by fetch-tags.mjs) feeds only buildingProfile.json: its North American
// towns add to the footprint numbers, and all of them to the storey, height and roof numbers, which are split by region
// (na = North America, eu = Germany) and name their towns.
// Distributions are {n, p10, p25, p50, p75, p90, min, max}, pooled over all towns. Lengths in metres, areas in m2,
// angles in degrees. "Edge" distances assume a road half-width (centreline to kerb/edge) from the width or lanes
// tags, else 6.0 m on through roads and 4.5 m on local streets (the browser game's town study used the same).
// OpenStreetMap data is (c) OpenStreetMap contributors, ODbL 1.0.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  R2D, dist, counts, shares, round, mean, loadTown, area, absArea, centroid, hull, perimeter, dist2, lineLength, minRect, segDist,
  inPoly, polyDist, polyPolyDist, lineCrossesPoly, rayHit, simplify, reflexCount, sampleInside, angDiff, Grid, num,
} from './lib.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const RAW = path.join(ROOT, 'artifacts', 'osm', 'raw');
const RAW_TAGS = path.join(ROOT, 'artifacts', 'osm', 'raw-tags');
const OUT_ART = path.join(ROOT, 'artifacts', 'osm');
const OUT_CONTENT = path.join(ROOT, 'Game', 'Assets', 'Content', 'sites');
const CREDIT = '(c) OpenStreetMap contributors, ODbL 1.0';

// ------------------------------------------------------------------ classes and types

const THROUGH = new Set(['motorway', 'trunk', 'primary', 'secondary', 'tertiary', 'motorway_link', 'trunk_link', 'primary_link', 'secondary_link', 'tertiary_link']);
const LOCAL = new Set(['unclassified', 'residential', 'living_street']);
const HOUSE = /^(house|detached|residential|semidetached_house|semi|bungalow|cabin|duplex|terrace|farm|static_caravan|townhouse)$/;
const ACCESSORY = /^(garage|garages|shed|carport|outbuilding|hut|shelter)$/;
const FARMB = /^(barn|farm_auxiliary|cowshed|stable|greenhouse|silo|grain_silo|sty|chicken_coop)$/;

function streetHalfWidth(t, cls) {
  const w = num(t.width);
  if (Number.isFinite(w) && w > 3 && w < 40) return w / 2;
  const lanes = num(t.lanes);
  if (Number.isFinite(lanes) && lanes >= 1 && lanes <= 8) return Math.max(3.0, lanes * 3.3 / 2 + (cls === 'through' ? 1.5 : 1.0));
  return cls === 'through' ? 6.0 : 4.5;
}

// the business type of a building, from its own tags, the points mapped inside it and the land use around it.
// Exported for tools/world/extract-town.mjs; the C# port (Backroads.Core.WorldBake.BusinessClassifier) is kept in step
// with it by the fixture test Town.ClassifierFixture (tools/world/classifier-fixture.mjs --check runs this side).
export function typeOf(b) {
  const t = b.tags, all = [t, ...b.pois];
  const has = (k, re) => all.some((x) => x[k] !== undefined && re.test(x[k]));
  const lu = b.lu, bt = t.building || '';
  if (/^(roof|canopy)$/.test(bt) || t.man_made === 'canopy') return { type: b.nearFuel || t.amenity === 'fuel' ? 'fuel_canopy' : 'roof' };
  if (b.inFuel || has('amenity', /^fuel$/)) return { type: 'fuel_shop' };
  if (lu.quarry) return { type: 'quarry' };
  if (b.inSawmill || has('craft', /^sawmill$/) || has('industrial', /^sawmill$/)) return { type: 'mill', sub: 'sawmill' };
  if (/^(silo|grain_silo)$/.test(bt) || t.man_made === 'silo') return { type: lu.farmyard ? 'farm' : 'mill', sub: lu.farmyard ? 'silo' : 'grain_elevator' };
  if (lu.farmyard) return { type: 'farm', sub: HOUSE.test(bt) ? 'farmhouse' : FARMB.test(bt) ? bt : ACCESSORY.test(bt) ? 'accessory' : 'other' };
  if (lu.harbour || lu.port || b.inMarina || bt === 'boathouse') return { type: 'harbor' };
  if (has('amenity', /^car_wash$/) || has('shop', /^(car_repair|car|tyres|motorcycle|car_parts|truck)$/) || has('craft', /^mechanic$/) || has('service', /^vehicle/)) return { type: 'car_repair' };
  if (has('tourism', /^(motel|hotel|guest_house)$/) || /^(hotel|motel)$/.test(bt)) return { type: 'motel' };
  if (has('amenity', /^place_of_worship$/) || /^(church|chapel|cathedral|temple|mosque|synagogue)$/.test(bt)) return { type: 'church' };
  if (has('amenity', /^(school|college|kindergarten|university)$/) || /^(school|college|kindergarten|university)$/.test(bt)) return { type: 'school' };
  if (has('amenity', /^(townhall|post_office|fire_station|police|library|community_centre|courthouse|clinic|hospital|doctors|dentist|social_facility|nursing_home|arts_centre|theatre|cinema)$/) ||
      /^(civic|public|government|fire_station|hospital|townhall|train_station)$/.test(bt)) return { type: 'civic' };
  if (has('shop', /^supermarket$/) || bt === 'supermarket') return { type: 'supermarket' };
  if (has('amenity', /^fast_food$/)) return { type: 'fast_food' };
  if (has('amenity', /^(restaurant|cafe|bar|pub|ice_cream|biergarten)$/)) return { type: 'restaurant' };
  if (has('amenity', /^bank$/) || has('office', /./) || bt === 'office') return { type: 'office' };
  if (has('shop', /./) || /^(retail|commercial|kiosk)$/.test(bt)) return { type: 'shop' };
  if (/^(warehouse|storage|hangar|transportation)$/.test(bt)) return { type: 'warehouse' };
  if (/^(industrial|manufacture|factory|works)$/.test(bt) || b.inWorks) return { type: 'industrial' };
  if (bt === 'apartments' || bt === 'dormitory') return { type: 'apartments' };
  if (HOUSE.test(bt)) return { type: 'house' };
  if (ACCESSORY.test(bt)) return { type: 'accessory', sub: bt };
  if (FARMB.test(bt)) return { type: 'farm', sub: bt };
  if (/^(church|chapel)$/.test(bt)) return { type: 'church' };
  // building=yes (or something rare): land use first, then size, as the browser game's town study did
  const A = b.A;
  if (lu.industrial) return { type: A < 60 ? 'accessory' : A > 1200 ? 'warehouse' : 'industrial', inferred: true };
  if (lu.retail || lu.commercial) return { type: A < 40 ? 'accessory' : 'shop', inferred: true };
  if (A < 55) return { type: 'accessory', inferred: true };
  if (A < 320) return { type: 'house', inferred: true };
  return { type: A > 1500 ? 'warehouse' : 'shop', inferred: true };
}

// ------------------------------------------------------------------ one town

export function buildTown(file) {
  return buildTownFrom(loadTown(JSON.parse(fs.readFileSync(file, 'utf8'))), path.basename(file, '.json'));
}

// T: { name, nodes, ways, rels } as lib.mjs loadTown returns them (local metres, x east, y north); extract-town.mjs
// passes its own UTM-projected town.
export function buildTownFrom(T, slug) {
  const town = { name: T.name, slug, parts: [], streets: [], service: [], tracks: [], buildings: [], parking: [], spaces: [], spaceNodes: [],
    fuelAreas: [], fuelNodes: [], landuse: [], barriers: [], gates: [], doors: [], piers: [], marinas: [], works: [], sawmills: [], areaHw: [], pois: [], lamps: [], slipways: [], railways: [] };
  const publicNode = new Map(); // node id -> street

  const polyOf = (w) => (w.closed ? w.g.slice(0, -1) : null);
  for (const w of T.ways) {
    const t = w.tags, poly = polyOf(w);
    if (t.highway) {
      if (THROUGH.has(t.highway) || LOCAL.has(t.highway)) {
        const cls = THROUGH.has(t.highway) ? 'through' : 'local';
        const s = { id: w.id, cls, hw: streetHalfWidth(t, cls), g: w.g, tags: t };
        town.streets.push(s);
        for (const r of w.refs) publicNode.set(r, s);
      } else if (t.highway === 'service') town.service.push({ id: w.id, svc: t.service || '', g: w.g, refs: w.refs, tags: t });
      else if (t.highway === 'track') town.tracks.push({ id: w.id, g: w.g, refs: w.refs, tags: t });
    }
    if (t['area:highway'] && poly) town.areaHw.push({ id: w.id, kind: t['area:highway'], poly, tags: t });
    if (t.building && poly && poly.length >= 3) town.buildings.push({ id: w.id, tags: t, poly });
    else if (t['building:part'] && poly && poly.length >= 3) town.parts.push({ id: w.id, tags: t, poly });
    if (t.amenity === 'parking' && poly && !t.building && !/^(underground|multi-storey|rooftop)$/.test(t.parking || '')) town.parking.push({ id: w.id, poly, tags: t });
    if (t.amenity === 'parking_space' && poly) town.spaces.push({ id: w.id, poly, tags: t });
    if (t.amenity === 'fuel' && poly && !t.building) town.fuelAreas.push({ id: w.id, poly, tags: t });
    if (t.landuse && poly) town.landuse.push({ id: w.id, use: t.landuse, poly, tags: t });
    if (t.barrier && /^(fence|wall|hedge|retaining_wall|guard_rail|jersey_barrier)$/.test(t.barrier)) town.barriers.push({ id: w.id, kind: t.barrier, g: w.g, tags: t });
    if (t.barrier === 'gate') town.gates.push({ id: w.id, p: centroid(w.g) });
    if (t.man_made === 'pier' || t.man_made === 'quay' || t.man_made === 'breakwater') town.piers.push({ id: w.id, kind: t.man_made, g: w.g, poly, tags: t });
    if (t.leisure === 'marina' && poly) town.marinas.push({ id: w.id, poly, tags: t });
    if (t.leisure === 'slipway') town.slipways.push({ id: w.id, p: centroid(w.g) });
    if (t.man_made === 'works' && poly) town.works.push({ id: w.id, poly, tags: t });
    if ((t.craft === 'sawmill' || t.industrial === 'sawmill' || /lumber|timber|wood/.test(t.product || '')) && poly) town.sawmills.push({ id: w.id, poly, tags: t });
    if (t.railway === 'rail') town.railways.push({ id: w.id, g: w.g });
  }
  for (const r of T.rels) {
    const t = r.tags;
    if (t.building) town.buildings.push({ id: r.id, tags: t, poly: r.poly });
    else if (t['building:part']) town.parts.push({ id: r.id, tags: t, poly: r.poly });
    else if (t.amenity === 'parking' && !/^(underground|multi-storey|rooftop)$/.test(t.parking || '')) town.parking.push({ id: r.id, poly: r.poly, tags: t });
    else if (t.landuse) town.landuse.push({ id: r.id, use: t.landuse, poly: r.poly, tags: t });
  }
  for (const n of T.nodes) {
    const t = n.tags;
    if (t.amenity === 'fuel') town.fuelNodes.push({ id: n.id, p: n.p, tags: t });
    if (t.amenity === 'parking_space') town.spaceNodes.push({ id: n.id, p: n.p, tags: t });
    if (t.door || t.entrance) town.doors.push({ id: n.id, p: n.p, tags: t });
    if (t.highway === 'street_lamp') town.lamps.push({ id: n.id, p: n.p });
    if (t.leisure === 'slipway') town.slipways.push({ id: n.id, p: n.p });
    if (t.barrier === 'gate' || t.barrier === 'lift_gate' || t.barrier === 'swing_gate') town.gates.push({ id: n.id, p: n.p });
    if (t.shop || t.amenity || t.craft || t.office || t.tourism) town.pois.push({ id: n.id, p: n.p, tags: t });
  }

  // indexes
  const sg = new Grid(40);
  for (const s of town.streets) for (let i = 1; i < s.g.length; i++) { const a = s.g[i - 1], b = s.g[i]; sg.addBox({ a, b, s }, Math.min(a.x, b.x), Math.min(a.y, b.y), Math.max(a.x, b.x), Math.max(a.y, b.y)); }
  town.nearestStreet = (p, maxR = 80, filter = null) => {
    let best = null;
    for (const seg of sg.near(p, maxR)) {
      if (filter && !filter(seg.s)) continue;
      const h = segDist(p, seg.a, seg.b);
      if (h.d < maxR && (!best || h.d < best.h.d)) best = { s: seg.s, h, seg };
    }
    return best;
  };
  town.publicNode = publicNode;
  const lg = new Grid(60);
  for (const l of town.landuse) lg.addPoly(l, l.poly);
  const luAt = (p) => { const o = {}; for (const l of lg.near(p, 1)) if (inPoly(p, l.poly)) o[l.use] = l; return o; };
  const inAny = (p, list) => list.find((a) => inPoly(p, a.poly)) || null;

  // buildings
  const bg = new Grid(40);
  for (const b of town.buildings) {
    b.A = absArea(b.poly);
    b.c = centroid(b.poly);
    b.r = minRect(b.poly);
    b.pois = [];
    bg.addPoly(b, b.poly);
  }
  town.buildings = town.buildings.filter((b) => b.A >= 6 && b.A < 80000);
  for (const n of town.pois) {
    let host = null;
    for (const b of bg.near(n.p, 15)) if (inPoly(n.p, b.poly)) { host = b; break; }
    if (!host) { let bd = 12; for (const b of bg.near(n.p, 12)) { const d = polyDist(n.p, b.poly); if (d < bd && !/^(roof|canopy)$/.test(b.tags.building || '') && !HOUSE.test(b.tags.building || '')) { bd = d; host = b; } } }
    if (host) host.pois.push(n.tags);
  }
  town.bgrid = bg;
  for (const p of town.parts) {
    p.A = absArea(p.poly); p.c = centroid(p.poly); p.r = minRect(p.poly);
    let host = null;
    for (const b of bg.near(p.c, 5)) if (inPoly(p.c, b.poly) && (!host || b.A < host.A)) host = b;
    if (host) (host.parts = host.parts || []).push(p);
  }
  for (const b of town.buildings) {
    b.lu = luAt(b.c);
    b.inFuel = !!inAny(b.c, town.fuelAreas) && !/^(roof|canopy)$/.test(b.tags.building || '');
    b.nearFuel = !!inAny(b.c, town.fuelAreas) || town.fuelNodes.some((f) => dist2(f.p, b.c) < 45);
    b.inMarina = !!inAny(b.c, town.marinas);
    b.inWorks = !!inAny(b.c, town.works);
    b.inSawmill = !!inAny(b.c, town.sawmills);
    Object.assign(b, typeOf(b));
    b.dCentre = Math.hypot(b.c.x, b.c.y);
    frameFor(town, b);
  }
  // doors and entrances mapped on outlines: which side of the building they face (front = towards its street)
  for (const d of town.doors) {
    let host = null;
    for (const b of bg.near(d.p, 3)) if (polyDist(d.p, b.poly) < 0.6 || inPoly(d.p, b.poly)) { host = b; break; }
    if (!host || !host.f) continue;
    let be = null, bd = Infinity;
    for (let i = 0; i < host.poly.length; i++) { const h = segDist(d.p, host.poly[i], host.poly[(i + 1) % host.poly.length]); if (h.d < bd) { bd = h.d; be = h; } }
    let nx = -be.ty, ny = be.tx;
    if (inPoly({ x: d.p.x + nx * 0.4, y: d.p.y + ny * 0.4 }, host.poly)) { nx = -nx; ny = -ny; }
    const dot = nx * host.f.n.x + ny * host.f.n.y;
    (host.doorSides = host.doorSides || []).push({ kind: d.tags.entrance || d.tags.door, side: dot < -0.7 ? 'front' : dot > 0.7 ? 'rear' : 'side' });
  }
  // street-front commerce: a commercial building whose wall is at most 3 m behind the road edge (main street)
  const COM =new Set(['shop', 'restaurant', 'fast_food', 'office', 'supermarket', 'civic', 'motel', 'car_repair']);
  // (a wall within 4 m of the assumed road edge, or within 10 m and sharing a wall with a neighbour; a lot in front
  //  of it, found later, turns it back into a standalone building)
  for (const b of town.buildings) b.form = COM.has(b.type) ? 'standalone' : null;
  for (const b of town.buildings) {
    if (!b.form || !b.f || b.f.setback > 10 || b.dCentre > 1500) continue;
    b.touching = bg.near(b.c, 40).some((o) => o !== b && o.type !== 'accessory' && o.type !== 'roof' && polyPolyDist(b.poly, o.poly) < 1.0);
    if (b.f.setback <= 4 || b.touching) b.form = 'streetfront';
  }
  return town;
}

// the building's street frame: u along the street (to the right when facing the building), v away from the street
function frameFor(town, b) {
  const ns = town.nearestStreet(b.c, 90);
  if (!ns) { b.f = null; return; }
  // pick the street whose segment is closest to the outline (not the centroid), among those within 90 m
  let md = Infinity;
  for (const p of b.poly) md = Math.min(md, segDist(p, ns.seg.a, ns.seg.b).d);
  const o = { x: ns.h.x, y: ns.h.y };
  let nx = b.c.x - o.x, ny = b.c.y - o.y, nl = Math.hypot(nx, ny);
  if (nl < 1e-6) { nx = -ns.h.ty; ny = ns.h.tx; nl = 1; }
  nx /= nl; ny /= nl;
  const ux = ny, uy = -nx;
  const toL = (p) => ({ u: (p.x - o.x) * ux + (p.y - o.y) * uy, v: (p.x - o.x) * nx + (p.y - o.y) * ny });
  const loc = b.poly.map(toL);
  const v0 = Math.min(...loc.map((q) => q.v)), v1 = Math.max(...loc.map((q) => q.v));
  const u0 = Math.min(...loc.map((q) => q.u)), u1 = Math.max(...loc.map((q) => q.u));
  const streetAng = Math.atan2(ns.h.ty, ns.h.tx);
  b.f = { street: ns.s, o, n: { x: nx, y: ny }, u: { x: ux, y: uy }, toL, hw: ns.s.hw, v0, v1, u0, u1,
    setback: md - ns.s.hw, centreDist: md, streetAng,
    axis: angDiff(b.r.ang, streetAng) < 20 / R2D ? 'along' : angDiff(b.r.ang, streetAng) > 70 / R2D ? 'across' : 'oblique' };
}

// ------------------------------------------------------------------ analyses

export function lotAnalysis(town) {
  const out = [];
  const big = town.buildings.filter((b) => !['house', 'accessory', 'roof', 'fuel_canopy'].includes(b.type) || b.type === 'apartments');
  const g = new Grid(50);
  for (const b of big) g.addPoly(b, b.poly);
  const spaceGrid = new Grid(30);
  for (const s of town.spaces) { s.r = minRect(s.poly); s.c = centroid(s.poly); spaceGrid.addPoly(s, s.poly); }
  for (const L of town.parking) {
    const A = absArea(L.poly);
    if (A < 60 || A > 60000) continue;
    const c = centroid(L.poly), r = minRect(L.poly);
    const rec = { id: L.id, town: town.name, A, w: r.w, d: r.d, tags: L.tags, poly: L.poly, surface: L.tags.surface || null,
      access: L.tags.access || null, capacity: num(L.tags.capacity) };
    // the building it serves: nearest commercial/civic/industrial building outline within 30 m
    let best = null, bd = 30, second = Infinity;
    for (const b of g.near(c, Math.max(r.d, 40) + 30)) {
      const d = polyPolyDist(L.poly, b.poly);
      if (d < bd) { second = bd; bd = d; best = b; } else if (d < second) second = d;
    }
    if (best && best.f) {
      rec.building = best;
      rec.gap = bd;
      rec.shared = second < 6;
      const F = best.f, pts = sampleInside(L.poly, 2.0).map(F.toL);
      const sh = { front: 0, side: 0, rear: 0 };
      for (const q of pts) sh[q.v < F.v0 - 0.5 ? 'front' : q.v > F.v1 + 0.5 ? 'rear' : 'side']++;
      const np = pts.length || 1;
      rec.sameSide = pts.filter((q) => q.v > 0).length / np >= 0.85;
      rec.share = { front: sh.front / np, side: sh.side / np, rear: sh.rear / np };
      rec.pos = rec.sameSide ? Object.entries(rec.share).sort((a, b) => b[1] - a[1])[0][0] : 'across';
      rec.wrap = Object.values(rec.share).filter((v) => v > 0.2).length > 1;
      const loc = L.poly.map(F.toL);
      rec.lv0 = Math.min(...loc.map((q) => q.v)); rec.lv1 = Math.max(...loc.map((q) => q.v));
      rec.lu0 = Math.min(...loc.map((q) => q.u)); rec.lu1 = Math.max(...loc.map((q) => q.u));
      rec.ratio = A / best.A;
      if (!rec.sameSide) rec.wrap = false; // a lot across the street: only its size and ratio count
      else {
        rec.depth = rec.lv1 - Math.max(rec.lv0, F.hw);           // from the road edge (or the lot's front) to its back
        rec.frontGap = rec.lv0 - F.hw;                           // lot front line to the road edge
        const near = pts.filter((q) => q.v < F.hw + 4);
        rec.touchLen = near.length ? Math.max(...near.map((q) => q.u)) - Math.min(...near.map((q) => q.u)) + 2 : 0;
        rec.lotWidthAlong = rec.lu1 - rec.lu0;
        rec.bWidthAlong = F.u1 - F.u0;
      }
    }
    // entrances: service ways that run from a public street into the lot
    const ents = [];
    for (const s of town.service) {
      const inside = s.g.some((p) => inPoly(p, L.poly)) || lineCrossesPoly(s.g, L.poly) > 0;
      if (!inside) continue;
      for (let i = 0; i < s.g.length; i++) {
        const onStreet = town.publicNode.has(s.refs[i]) || (i === 0 || i === s.g.length - 1) && (() => { const ns = town.nearestStreet(s.g[i], 3); return !!ns; })();
        if (onStreet) {
          const p = s.g[i];
          if (polyDist(p, L.poly) < 40 && !ents.some((e) => dist2(e.p, p) < 10)) ents.push({ p, svc: s.svc, width: num(s.tags.width), id: s.id, oneway: s.tags.oneway || null });
        }
      }
    }
    rec.entrances = ents;
    // stalls and aisles inside
    const stalls = [];
    for (const s of spaceGrid.near(c, r.d)) if (inPoly(s.c, L.poly)) stalls.push(s);
    const aisles = town.service.filter((s) => s.svc === 'parking_aisle' && (s.g.some((p) => inPoly(p, L.poly)) || lineCrossesPoly(s.g, L.poly) > 0));
    rec.stalls = stalls; rec.aisles = aisles;
    rec.stallNodes = town.spaceNodes.filter((n) => inPoly(n.p, L.poly)).length;
    // aisle widths (stall edge to stall edge) and edge offsets
    rec.aisleW = []; rec.module = []; rec.edgeOff = []; rec.stallAng = [];
    const stallShapes = stalls.map((s) => ({ g: s.poly, closed: true }));
    for (const a of aisles) {
      for (let i = 1; i < a.g.length; i++) {
        const p = a.g[i - 1], q = a.g[i], L2 = dist2(p, q);
        if (L2 < 6) continue;
        const m = { x: (p.x + q.x) / 2, y: (p.y + q.y) / 2 }, tx = (q.x - p.x) / L2, ty = (q.y - p.y) / L2;
        if (!inPoly(m, L.poly)) continue;
        const nl = { x: -ty, y: tx }, nr = { x: ty, y: -tx };
        if (stallShapes.length) {
          const dl = rayHit(m, nl, stallShapes, 12), dr = rayHit(m, nr, stallShapes, 12);
          if (dl < 12 && dr < 12) rec.aisleW.push(dl + dr);
        }
        const el = rayHit(m, nl, [{ g: L.poly, closed: true }], 80), er = rayHit(m, nr, [{ g: L.poly, closed: true }], 80);
        for (const e of [el, er]) if (e < 80) rec.edgeOff.push(e);
        // parallel aisles in the same lot: perpendicular spacing = one parking module
        for (const b2 of aisles) for (let j = 1; j < b2.g.length; j++) {
          const p2 = b2.g[j - 1], q2 = b2.g[j];
          if (b2 === a && j === i) continue;
          if (angDiff(Math.atan2(ty, tx), Math.atan2(q2.y - p2.y, q2.x - p2.x)) > 10 / R2D) continue;
          const h = segDist(m, p2, q2);
          if (h.t > 0.02 && h.t < 0.98 && h.d > 8 && h.d < 30) rec.module.push(h.d);
        }
      }
    }
    for (const s of stalls) {
      let bestA = null, bdd = 12;
      for (const a of aisles) for (let i = 1; i < a.g.length; i++) { const h = segDist(s.c, a.g[i - 1], a.g[i]); if (h.d < bdd) { bdd = h.d; bestA = Math.atan2(h.ty, h.tx); } }
      if (bestA !== null) rec.stallAng.push(angDiff(s.r.ang, bestA) * R2D);
    }
    out.push(rec);
  }
  // a street-front building with its own lot in front is not street-front after all
  for (const l of out) if (l.building && l.sameSide && l.pos === 'front' && l.share.front > 0.6 && l.building.form === 'streetfront') l.building.form = 'standalone';
  return out;
}

function fuelAnalysis(town) {
  const out = [];
  const roofs = town.buildings.filter((b) => /^(roof|canopy)$/.test(b.tags.building || '') || b.tags.man_made === 'canopy');
  const sites = town.fuelAreas.map((a) => ({ id: a.id, poly: a.poly, c: centroid(a.poly), tags: a.tags }));
  const near = (p, r) => sites.some((s) => (s.poly ? inPoly(p, s.poly) || polyDist(p, s.poly) < r : dist2(p, s.c) < r));
  // most stations are mapped on the canopy itself (building=roof + amenity=fuel), some on the shop building
  for (const b of roofs) if (b.tags.amenity === 'fuel' && !near(b.c, 25)) sites.push({ id: b.id, poly: null, c: b.c, tags: b.tags, canopyHint: b });
  for (const b of town.buildings) if (b.tags.amenity === 'fuel' && !roofs.includes(b) && !near(b.c, 40)) sites.push({ id: b.id, poly: null, c: b.c, tags: b.tags, shopHint: b });
  for (const n of town.fuelNodes) {
    if (sites.some((s) => (s.poly ? inPoly(n.p, s.poly) || polyDist(n.p, s.poly) < 30 : dist2(n.p, s.c) < 30))) continue;
    if (town.buildings.some((b) => b.type === 'fuel_canopy' && inPoly(n.p, b.poly) && sites.some((s) => s.poly && inPoly(b.c, s.poly)))) continue;
    sites.push({ id: n.id, poly: null, c: n.p, tags: n.tags });
  }
  for (const s of sites) {
    const cand = roofs.filter((r) => (s.poly ? inPoly(r.c, s.poly) || polyPolyDist(r.poly, s.poly) < 3 : dist2(r.c, s.c) < 45));
    const canopy = s.canopyHint || cand.sort((a, b) => b.A - a.A)[0] || null;
    const rec = { id: s.id, town: town.name, site: s.poly, tags: s.tags, canopy, brandless: !s.tags.brand };
    if (!canopy) { rec.noCanopy = true; out.push(rec); continue; }
    const ns = town.nearestStreet(canopy.c, 120);
    if (!ns) { out.push(rec); continue; }
    // frame from the road the canopy faces
    const o = { x: ns.h.x, y: ns.h.y };
    let nx = canopy.c.x - o.x, ny = canopy.c.y - o.y; const nl = Math.hypot(nx, ny) || 1; nx /= nl; ny /= nl;
    const F = { o, n: { x: nx, y: ny }, u: { x: ny, y: -nx }, hw: ns.s.hw, street: ns.s };
    F.toL = (p) => ({ u: (p.x - o.x) * F.u.x + (p.y - o.y) * F.u.y, v: (p.x - o.x) * nx + (p.y - o.y) * ny });
    rec.F = F;
    const cl = canopy.poly.map(F.toL);
    rec.canopyV0 = Math.min(...cl.map((q) => q.v)); rec.canopyV1 = Math.max(...cl.map((q) => q.v));
    rec.canopySetback = rec.canopyV0 - F.hw;
    rec.canopyW = canopy.r.w; rec.canopyD = canopy.r.d; rec.canopyA = canopy.A;
    rec.canopyAxis = angDiff(canopy.r.ang, Math.atan2(ns.h.ty, ns.h.tx)) * R2D;
    rec.roadClass = ns.s.cls; rec.roadTags = ns.s.tags;
    // the shop: a non-roof building in the site, or the fuel/convenience-tagged building nearest the canopy
    const shopCand = town.bgrid.near(canopy.c, 70).filter((b) => b !== canopy && !/^(roof|canopy)$/.test(b.tags.building || '') && b.type !== 'house' && b.type !== 'accessory' &&
      ((s.poly && inPoly(b.c, s.poly)) || polyPolyDist(b.poly, canopy.poly) < 35));
    shopCand.sort((a, b) => (b === s.shopHint) - (a === s.shopHint) || (b.type === 'fuel_shop') - (a.type === 'fuel_shop') || polyPolyDist(a.poly, canopy.poly) - polyPolyDist(b.poly, canopy.poly));
    const shop = shopCand[0] || null;
    if (shop && ['shop', 'house', 'restaurant', 'fast_food'].includes(shop.type) && (shop.inferred || shop.pois.some((p) => /^(convenience|kiosk)$/.test(p.shop || '')))) shop.type = 'fuel_shop';
    canopy.type = 'fuel_canopy';
    rec.shop = shop;
    if (shop) {
      const sl = shop.poly.map(F.toL);
      const sv0 = Math.min(...sl.map((q) => q.v)), sv1 = Math.max(...sl.map((q) => q.v));
      const su = mean(sl.map((q) => q.u)), cu = mean(cl.map((q) => q.u));
      rec.shopGap = polyPolyDist(shop.poly, canopy.poly);
      rec.shopPos = sv0 > rec.canopyV1 - 1 ? 'behind' : sv1 < rec.canopyV0 + 1 ? 'front' : 'beside';
      rec.shopW = shop.r.w; rec.shopD = shop.r.d; rec.shopA = shop.A; rec.shopSetback = sv0 - F.hw;
      rec.shopOffsetU = su - cu;
      rec.attached = rec.shopGap < 0.5;
    }
    if (s.poly) {
      const sl = s.poly.map(F.toL);
      rec.siteA = absArea(s.poly); rec.siteDepth = Math.max(...sl.map((q) => q.v)) - F.hw; rec.siteFront = Math.max(...sl.map((q) => q.u)) - Math.min(...sl.map((q) => q.u));
      const r = minRect(s.poly); rec.siteW = r.w; rec.siteD = r.d;
    }
    // entrances: service ways from a public street into the site (or within 25 m of the canopy)
    const ents = [];
    for (const w of town.service) {
      const touches = s.poly ? (w.g.some((p) => inPoly(p, s.poly)) || lineCrossesPoly(w.g, s.poly) > 0) : w.g.some((p) => dist2(p, canopy.c) < 30);
      if (!touches) continue;
      for (let i = 0; i < w.g.length; i++) if (town.publicNode.has(w.refs[i])) { const p = w.g[i]; if (dist2(p, canopy.c) < 60 && !ents.some((e) => dist2(e.p, p) < 8)) ents.push({ p, street: town.publicNode.get(w.refs[i]).id, oneway: w.tags.oneway || null, width: num(w.tags.width) }); }
    }
    rec.entrances = ents;
    rec.streets = new Set(ents.map((e) => e.street)).size;
    if (ents.length >= 2) { const us = ents.map((e) => F.toL(e.p).u).sort((a, b) => a - b); rec.entSpacing = us[us.length - 1] - us[0]; }
    // pumps or islands mapped inside the canopy
    rec.pumpNodes = town.pois.filter((p) => inPoly(p.p, canopy.poly) && (p.tags.amenity === 'fuel' || /pump/.test(JSON.stringify(p.tags)))).length;
    rec.mappedInside = [...town.pois, ...town.doors].filter((p) => inPoly(p.p, canopy.poly)).map((p) => p.tags);
    out.push(rec);
  }
  return out;
}

function downtownAnalysis(town, lots) {
  const front = town.buildings.filter((b) => b.form === 'streetfront');
  const streetsWithParking = new Set();
  const orient = [];
  for (const s of town.streets) {
    const t = s.tags, keys = Object.keys(t).filter((k) => /^parking:(lane|both|left|right)/.test(k));
    if (!keys.length) continue;
    const vals = keys.map((k) => t[k]).join(' ');
    if (/(parallel|diagonal|perpendicular|lane|street_side|half_on_kerb|on_kerb|marked|yes)/.test(vals) && !/^(no|no_parking|no_stopping|separate)(\s|$)/.test(vals)) streetsWithParking.add(s);
    for (const k of keys) if (/orientation$/.test(k) || /^parking:lane:(both|left|right)$/.test(k)) if (/^(parallel|diagonal|perpendicular)$/.test(t[k])) orient.push(t[k]);
  }
  const streetSideAreas = town.parking.filter((p) => /^(street_side|lane|on_kerb|half_on_kerb)$/.test(p.tags.parking || ''));
  const alleys = town.service.filter((s) => s.svc === 'alley');
  const recs = [];
  for (const b of front) {
    if (!b.f) continue;
    const onStreet = streetsWithParking.has(b.f.street) || streetSideAreas.some((a) => polyPolyDist(a.poly, b.poly) < 20);
    // an alley behind: an alley segment within 30 m of the back wall, on the far side from the street
    let alley = null;
    for (const a of alleys) for (const p of a.g) { const q = b.f.toL(p); if (q.v > b.f.v1 - 1 && q.v < b.f.v1 + 30 && q.u > b.f.u0 - 10 && q.u < b.f.u1 + 10) { if (!alley || q.v < alley) alley = q.v; } }
    const rearLot = lots.find((l) => l.building === b && l.pos === 'rear');
    recs.push({ b, onStreet, alleyGap: alley !== null ? alley - b.f.v1 : null, rearLot: !!rearLot, depth: b.f.v1 - b.f.v0, width: b.f.u1 - b.f.u0, touching: !!b.touching });
  }
  const alleyLen = alleys.reduce((a, s) => a + lineLength(s.g), 0);
  return { recs, orient, alleyLen, alleyCount: alleys.length, streetsTagged: streetsWithParking.size };
}

function industrialAnalysis(town, lots) {
  const out = [];
  const shapes = [...town.buildings.map((b) => ({ g: b.poly, closed: true, b })), ...town.barriers.map((f) => ({ g: f.g, closed: false }))];
  for (const b of town.buildings) {
    if (!['warehouse', 'industrial'].includes(b.type) || b.A < 300) continue;
    const rec = { b, town: town.name, docks: [], doors: [] };
    for (const d of town.doors) {
      if (polyDist(d.p, b.poly) > 1.0) continue;
      const kind = d.tags.door || d.tags.entrance;
      // outward normal of the nearest edge
      let be = null, bd = Infinity;
      for (let i = 0; i < b.poly.length; i++) { const h = segDist(d.p, b.poly[i], b.poly[(i + 1) % b.poly.length]); if (h.d < bd) { bd = h.d; be = h; } }
      let nx = -be.ty, ny = be.tx;
      const test = { x: d.p.x + nx * 0.5, y: d.p.y + ny * 0.5 };
      if (inPoly(test, b.poly)) { nx = -nx; ny = -ny; }
      const start = { x: d.p.x + nx * 0.3, y: d.p.y + ny * 0.3 };
      const hit = rayHit(start, { x: nx, y: ny }, shapes.filter((s) => s.b !== b), 120);
      let side = null;
      if (b.f) { const dot = nx * b.f.n.x + ny * b.f.n.y; side = dot < -0.7 ? 'front' : dot > 0.7 ? 'rear' : 'side'; }
      const e = { kind, apron: hit < 120 ? hit : null, side, p: d.p };
      if (/loading_dock/.test(kind) || d.tags.door === 'overhead' || d.tags.entrance === 'service') rec.docks.push(e); else rec.doors.push(e);
    }
    // fences within 40 m and the yard (industrial land use containing it)
    let fenceLen = 0;
    for (const f of town.barriers) if (f.kind === 'fence' || f.kind === 'wall') for (let i = 1; i < f.g.length; i++) { const m = { x: (f.g[i - 1].x + f.g[i].x) / 2, y: (f.g[i - 1].y + f.g[i].y) / 2 }; if (polyDist(m, b.poly) < 40) fenceLen += dist2(f.g[i - 1], f.g[i]); }
    rec.fenceRatio = fenceLen / perimeter(b.poly);
    rec.gates = town.gates.filter((g) => polyDist(g.p, b.poly) < 60).length;
    const yard = b.lu.industrial;
    if (yard) { rec.yardA = absArea(yard.poly); rec.coverage = b.A / rec.yardA; }
    rec.lots = lots.filter((l) => l.building === b);
    out.push(rec);
  }
  return out;
}

function areaSites(town) {
  const farms = [], quarries = [], mills = [], harbors = [];
  const shapesOfAccess = [...town.service, ...town.tracks];
  const access = (poly) => { // ways that cross the area outline and reach a public road: lengths outside the area
    const res = [];
    for (const w of shapesOfAccess) {
      if (!(lineCrossesPoly(w.g, poly) > 0 || w.g.some((p) => inPoly(p, poly)))) continue;
      const iPub = w.refs.findIndex((r) => town.publicNode.has(r));
      if (iPub < 0) continue;
      let L = 0;
      // walk from the public road towards the area until the first vertex inside
      const dir = iPub === 0 ? 1 : -1;
      for (let i = iPub; i + dir >= 0 && i + dir < w.g.length; i += dir) { if (inPoly(w.g[i], poly)) break; L += dist2(w.g[i], w.g[i + dir]); }
      res.push({ len: L, surface: w.tags.surface || null, kind: w.tags.highway === 'track' ? 'track' : w.svc || 'service', width: num(w.tags.width) });
    }
    return res;
  };
  const roadGap = (poly) => { let d = Infinity; for (const p of poly) { const ns = town.nearestStreet(p, 300); if (ns) d = Math.min(d, ns.h.d - ns.s.hw); } return Number.isFinite(d) ? Math.max(0, d) : null; };
  const inside = (poly) => town.buildings.filter((b) => inPoly(b.c, poly));
  for (const l of town.landuse) {
    if (l.use === 'farmyard') {
      const r = minRect(l.poly), bs = inside(l.poly);
      farms.push({ id: l.id, town: town.name, poly: l.poly, A: absArea(l.poly), w: r.w, d: r.d, buildings: bs, bTags: counts(bs.map((b) => b.tags.building)), cover: bs.reduce((a, b) => a + b.A, 0) / absArea(l.poly), gap: roadGap(l.poly), access: access(l.poly) });
    } else if (l.use === 'quarry') {
      const r = minRect(l.poly), bs = inside(l.poly);
      quarries.push({ id: l.id, town: town.name, poly: l.poly, A: absArea(l.poly), w: r.w, d: r.d, buildings: bs, gap: roadGap(l.poly), access: access(l.poly), tags: l.tags });
    } else if (l.use === 'harbour' || l.use === 'port') {
      const bs = inside(l.poly);
      harbors.push({ id: l.id, town: town.name, kind: l.use, poly: l.poly, A: absArea(l.poly), buildings: bs, gap: roadGap(l.poly) });
    }
  }
  for (const m of town.marinas) {
    const bs = inside(m.poly);
    harbors.push({ id: m.id, town: town.name, kind: 'marina', poly: m.poly, A: absArea(m.poly), buildings: bs, gap: roadGap(m.poly) });
  }
  // piers and quays outside any mapped harbour or marina: group them (within 60 m) into waterfront sites
  const loose = town.piers.filter((p) => !harbors.some((h) => p.g.some((q) => polyDist(q, h.poly) < 30)));
  const groups = [];
  for (const p of loose) {
    const g = groups.find((G) => G.some((o) => o.g.some((a) => p.g.some((b) => dist2(a, b) < 60))));
    if (g) g.push(p); else groups.push([p]);
  }
  for (const G of groups) {
    const pts = G.flatMap((p) => p.g), H = hull(pts);
    if (H.length < 3) continue;
    const bs = town.buildings.filter((b) => polyDist(b.c, H) < 40 && b.type !== 'roof');
    harbors.push({ id: G[0].id, town: town.name, kind: 'piers', poly: H, A: absArea(H), buildings: bs, gap: roadGap(H) });
  }
  for (const h of harbors) {
    h.piers = town.piers.filter((p) => p.g.some((q) => polyDist(q, h.poly) < 30)).map((p) => ({ id: p.id, kind: p.kind, len: p.poly ? minRect(p.poly).d : lineLength(p.g), width: p.poly ? minRect(p.poly).w : num(p.tags.width) }));
    h.slipways = town.slipways.filter((s) => polyDist(s.p, h.poly) < 50).length;
    h.lots = town.parking.filter((p) => polyPolyDist(p.poly, h.poly) < 80).map((p) => absArea(p.poly));
  }
  for (const s of town.sawmills) {
    const r = minRect(s.poly), bs = inside(s.poly);
    mills.push({ id: s.id, town: town.name, kind: 'sawmill', poly: s.poly, A: absArea(s.poly), w: r.w, d: r.d, buildings: bs, gap: roadGap(s.poly), access: access(s.poly) });
  }
  for (const b of town.buildings) if (b.type === 'mill' && b.sub === 'grain_elevator') mills.push({ id: b.id, town: town.name, kind: 'grain_elevator', b, height: num(b.tags.height), A: b.A, nearRail: town.railways.some((r) => r.g.some((p) => polyDist(p, b.poly) < 60)) });
  return { farms, quarries, mills, harbors };
}

function houseAnalysis(town) {
  const houses = town.buildings.filter((b) => b.type === 'house' && b.f && b.f.setback !== null && b.f.setback < 60);
  const hg = new Grid(40);
  for (const h of houses) hg.addPoly(h, h.poly);
  const drives = [];
  for (const s of town.service) {
    if (s.svc !== 'driveway') continue;
    const i0 = s.refs.findIndex((r) => town.publicNode.has(r));
    const st = i0 >= 0 ? i0 : (() => { const a = town.nearestStreet(s.g[0], 3), b = town.nearestStreet(s.g[s.g.length - 1], 3); return a ? 0 : b ? s.g.length - 1 : -1; })();
    if (st < 0) continue;
    const end = st === 0 ? s.g[s.g.length - 1] : s.g[0];
    let host = null, hd = 15;
    for (const h of hg.near(end, 20)) { const d = polyDist(end, h.poly); if (d < hd) { hd = d; host = h; } }
    const startP = s.g[st];
    const rec = { id: s.id, town: town.name, len: lineLength(s.g), width: num(s.tags.width), surface: s.tags.surface || null, house: host, g: s.g, startP };
    if (host) {
      const F = host.f, q = F.toL(startP), cu = (F.u0 + F.u1) / 2, hw = (F.u1 - F.u0) / 2;
      rec.side = Math.abs(q.u - cu) > hw * 0.6 ? 'side' : 'centre';
      rec.offset = q.u - cu;
    }
    drives.push(rec);
  }
  // garages relative to their house
  const garages = [];
  for (const g of town.buildings) {
    if (!/^(garage|garages|carport)$/.test(g.tags.building || '')) continue;
    let host = null, hd = 30;
    for (const h of hg.near(g.c, 30)) { const d = polyPolyDist(g.poly, h.poly); if (d < hd) { hd = d; host = h; } }
    if (!host) continue;
    const F = host.f, q = F.toL(g.c);
    garages.push({ g, house: host, gap: hd, attached: hd < 0.5, pos: q.v < F.v0 ? 'front' : q.v > F.v1 ? 'rear' : 'side', setback: Math.min(...g.poly.map((p) => F.toL(p).v)) - F.hw });
  }
  // house frontage (spacing along the street), as in the browser game's town study
  const groups = new Map();
  for (const h of houses) { const k = h.f.street.id + ':' + Math.sign((h.c.x - h.f.o.x) * -Math.sin(h.f.streetAng) + (h.c.y - h.f.o.y) * Math.cos(h.f.streetAng)); if (!groups.has(k)) groups.set(k, []); groups.get(k).push(h); }
  const frontage = [];
  for (const list of groups.values()) {
    const st = list[0].f.street;
    const along = (b) => { let acc = 0, best = 0, bd = Infinity; for (let i = 1; i < st.g.length; i++) { const h = segDist(b.c, st.g[i - 1], st.g[i]); const L = dist2(st.g[i - 1], st.g[i]); if (h.d < bd) { bd = h.d; best = acc + h.t * L; } acc += L; } return best; };
    const ss = list.map(along).sort((a, b) => a - b);
    for (let i = 1; i < ss.length; i++) { const gap = ss[i] - ss[i - 1]; if (gap > 7 && gap < 70) frontage.push(gap); }
  }
  return { houses, drives, garages, frontage };
}

// ------------------------------------------------------------------ templates

const r1 = (v) => Math.round(v * 10) / 10;
function localPoly(F, poly) { return poly.map((p) => { const q = F.toL(p); return [r1(q.u), r1(q.v)]; }); }
function localLine(F, g) { return g.map((p) => { const q = F.toL(p); return [r1(q.u), r1(q.v)]; }); }
function clipLine(F, g, box) { // keep the part of a line inside a local box
  const loc = g.map((p) => F.toL(p));
  const out = [];
  for (const q of loc) if (q.u > box.u0 && q.u < box.u1 && q.v > box.v0 && q.v < box.v1) out.push([r1(q.u), r1(q.v)]);
  return out.length >= 2 ? out : null;
}
function streetTagsOf(s) { const t = s.tags, o = { highway: t.highway }; for (const k of ['lanes', 'width', 'oneway', 'surface', 'maxspeed', 'parking:both', 'parking:lane:both']) if (t[k] !== undefined) o[k] = t[k]; return o; }

function templateBase(type, town, F, mainB, extra) {
  return { type, town: town.name, credit: CREDIT, road: { cls: F.street.cls, hwAssumed: round(F.hw, 2), tags: streetTagsOf(F.street), osm: F.street.id },
    frame: 'u along the street (to the right when facing the site from the street), v away from the street; metres; v = 0 on the street centreline',
    building: mainB ? { osm: mainB.id, tags: pickTags(mainB.tags), poly: localPoly(F, mainB.poly) } : null, ...extra };
}
function pickTags(t) { const o = {}; for (const k of ['building', 'shop', 'amenity', 'building:levels', 'height', 'roof:shape', 'roof:levels', 'craft', 'landuse']) if (t[k] !== undefined) o[k] = t[k]; return o; }
function contextOf(town, F, box, skip) {
  const others = [];
  for (const b of town.bgrid.near({ x: F.o.x + F.n.x * (box.v0 + box.v1) / 2, y: F.o.y + F.n.y * (box.v0 + box.v1) / 2 }, 120)) {
    if (skip.has(b)) continue;
    const loc = b.poly.map(F.toL);
    if (loc.every((q) => q.u > box.u0 && q.u < box.u1 && q.v > box.v0 && q.v < box.v1)) others.push({ osm: b.id, role: b.type, poly: loc.map((q) => [r1(q.u), r1(q.v)]) });
  }
  return others;
}
function serviceIn(town, F, box) {
  const out = [];
  for (const s of town.service) { const l = clipLine(F, s.g, box); if (l) out.push({ osm: s.id, svc: s.svc, line: l }); }
  return out;
}
function fencesIn(town, F, box) {
  const out = [];
  for (const f of town.barriers) { const l = clipLine(F, f.g, box); if (l) out.push({ osm: f.id, kind: f.kind, line: l }); }
  return out;
}
function streetsIn(town, F, box) {
  const out = [];
  for (const s of town.streets) { const l = clipLine(F, s.g, { u0: box.u0 - 30, u1: box.u1 + 30, v0: box.v0 - 30, v1: box.v1 + 30 }); if (l) out.push({ osm: s.id, cls: s.cls, hwAssumed: round(s.hw, 2), line: l }); }
  return out;
}
const boxOf = (polys, pad = 6) => {
  let u0 = Infinity, u1 = -Infinity, v0 = Infinity, v1 = -Infinity;
  for (const P of polys) for (const [u, v] of P) { u0 = Math.min(u0, u); u1 = Math.max(u1, u); v0 = Math.min(v0, v); v1 = Math.max(v1, v); }
  return { u0: u0 - pad, u1: u1 + pad, v0: Math.min(v0 - pad, -2), v1: v1 + pad };
};

// ------------------------------------------------------------------ main

function main() {
  const files = fs.readdirSync(RAW).filter((f) => f.endsWith('.json')).sort();
  const towns = [], allLots = [], allFuel = [], allDown = [], allInd = [], allSites = { farms: [], quarries: [], mills: [], harbors: [] }, allHouse = { drives: [], garages: [], frontage: [], houses: [] };
  const templates = [];
  for (const f of files) {
    const town = buildTown(path.join(RAW, f));
    const fuel = fuelAnalysis(town), lots = lotAnalysis(town), down = downtownAnalysis(town, lots), ind = industrialAnalysis(town, lots), sites = areaSites(town), house = houseAnalysis(town);
    towns.push({ town, lots, fuel, down, ind, sites, house });
    allLots.push(...lots); allFuel.push(...fuel); allDown.push(down); allInd.push(...ind);
    for (const k of Object.keys(allSites)) allSites[k].push(...sites[k]);
    for (const k of Object.keys(allHouse)) allHouse[k].push(...house[k]);
    console.log(`${town.name}: ${town.buildings.length} buildings, ${town.streets.length} streets, ${lots.length} lots, ${fuel.length} fuel, ${ind.length} industrial, ${sites.farms.length} farmyards, ${house.drives.length} driveways`);
  }
  const tagTowns = [];
  if (fs.existsSync(RAW_TAGS)) for (const f of fs.readdirSync(RAW_TAGS).filter((x) => x.endsWith('.json')).sort()) {
    const town = buildTown(path.join(RAW_TAGS, f));
    tagTowns.push(town);
    console.log(`${town.name} (storeys and roofs): ${town.buildings.length} buildings, ${town.parts.length} building parts`);
  }
  const P = profile(towns, allLots, allFuel, allDown, allInd, allSites, allHouse);
  const B = buildingProfile(towns.map((t) => t.town), tagTowns);
  const Tm = pickTemplates(towns);
  fs.mkdirSync(OUT_CONTENT, { recursive: true });
  fs.writeFileSync(path.join(OUT_CONTENT, 'siteProfile.json'), JSON.stringify(P, null, 1) + '\n');
  // one line per key keeps the file small and its diffs readable
  const keyLines = Object.entries(B.keys).map(([k, v]) => '  ' + JSON.stringify(k) + ': ' + JSON.stringify(v)).join(',\n');
  const bHead = JSON.stringify({ ...B, keys: '__KEYS__' }, null, 1).replace('"__KEYS__"', '{\n' + keyLines + '\n }');
  fs.writeFileSync(path.join(OUT_CONTENT, 'buildingProfile.json'), bHead + '\n');
  fs.writeFileSync(path.join(OUT_CONTENT, 'siteTemplates.json'), JSON.stringify(Tm.out, null, 1) + '\n');
  fs.writeFileSync(path.join(OUT_ART, 'candidates.json'), JSON.stringify(Tm.cands, null, 1));
  console.log('wrote siteProfile.json, buildingProfile.json, siteTemplates.json (' + Tm.out.templates.length + ' templates)');
}

// ------------------------------------------------------------------ the site profile

function profile(towns, lots, fuel, down, ind, sites, house) {
  const servedType = (l) => (l.building ? (l.building.form === 'streetfront' ? 'downtown' : l.building.type) : 'unassigned');
  const byType = {};
  for (const l of lots) { const k = servedType(l); (byType[k] = byType[k] || []).push(l); }
  const lotStats = (L) => ({
    n: L.length,
    position: shares(L.filter((l) => l.pos).map((l) => l.pos)),
    wrapsMoreThanOneSide: round(L.filter((l) => l.wrap).length / Math.max(1, L.filter((l) => l.pos).length), 3),
    area: dist(L.map((l) => l.A), 0), short: dist(L.map((l) => l.w), 1), long: dist(L.map((l) => l.d), 1),
    lotToBuildingArea: dist(L.filter((l) => l.ratio).map((l) => l.ratio), 2),
    gapToBuilding: dist(L.filter((l) => l.building).map((l) => l.gap), 1),
    frontGapToRoadEdge: dist(L.filter((l) => l.frontGap !== undefined).map((l) => l.frontGap), 1),
    depthFromRoadEdge: dist(L.filter((l) => l.depth !== undefined && l.pos === 'front').map((l) => l.depth), 1),
    widthAlongStreet: dist(L.filter((l) => l.lotWidthAlong).map((l) => l.lotWidthAlong), 1),
    widthAlongStreetOverBuildingWidth: dist(L.filter((l) => l.lotWidthAlong && l.bWidthAlong).map((l) => l.lotWidthAlong / l.bWidthAlong), 2),
    frontageTouchingRoad: dist(L.filter((l) => l.touchLen > 0).map((l) => l.touchLen), 1),
    entrances: { lotsWithMappedEntrances: L.filter((l) => l.entrances.length).length, count: dist(L.filter((l) => l.entrances.length).map((l) => l.entrances.length), 0), widthTagged: dist(L.flatMap((l) => l.entrances.map((e) => e.width)), 1) },
    surface: shares(L.filter((l) => l.surface).map((l) => l.surface)),
    capacityTagged: dist(L.filter((l) => l.capacity > 0).map((l) => l.capacity), 0),
    areaPerTaggedSpace: dist(L.filter((l) => l.capacity > 2).map((l) => l.A / l.capacity), 1),
  });
  const stallsAll = lots.flatMap((l) => l.stalls.map((s) => ({ w: s.r.w, d: s.r.d }))).filter((s) => s.w > 1.8 && s.w < 4.6 && s.d > 3.5 && s.d < 8);
  const P = {
    note: 'Site layout numbers measured from real North American small towns. Distributions: n, p10, p25, p50, p75, p90, min, max (pooled). ' +
      'Lengths m, areas m2, angles degrees. "Road edge" = street centreline + an assumed half-width (width or lanes tags, else 6.0 m through / 4.5 m local).',
    credit: CREDIT, generatedBy: 'tools/osm/measure.mjs', towns: towns.map((t) => t.town.name),
    perTown: towns.map((t) => ({ name: t.town.name, buildings: t.town.buildings.length, streetsKm: round(t.town.streets.reduce((a, s) => a + lineLength(s.g), 0) / 1000, 1),
      parkingLots: t.lots.length, fuelStations: t.fuel.length, farmyards: t.sites.farms.length, quarries: t.sites.quarries.length, mills: t.sites.mills.length, harbors: t.sites.harbors.length,
      driveways: t.house.drives.length, parkingSpacesMapped: t.town.spaces.length + t.town.spaceNodes.length, aislesMapped: t.town.service.filter((s) => s.svc === 'parking_aisle').length })),
    parking: {
      all: lotStats(lots),
      byServedType: Object.fromEntries(Object.entries(byType).sort((a, b) => b[1].length - a[1].length).map(([k, L]) => [k, lotStats(L)])),
      stalls: { mappedPolygons: stallsAll.length, width: dist(stallsAll.map((s) => s.w), 2), length: dist(stallsAll.map((s) => s.d), 2),
        angleToAisle: dist(lots.flatMap((l) => l.stallAng), 0), angleClass: shares(lots.flatMap((l) => l.stallAng).map((a) => (a > 75 ? '90' : a > 52 ? '60' : a > 30 ? '45' : 'parallel'))) },
      aisles: { mapped: lots.reduce((a, l) => a + l.aisles.length, 0), widthBetweenStalls: dist(lots.flatMap((l) => l.aisleW).filter((w) => w > 3 && w < 12), 2),
        moduleAisleToAisle: dist(lots.flatMap((l) => l.module), 1), aisleCentreToLotEdge: dist(lots.flatMap((l) => l.edgeOff).filter((e) => e < 25), 1),
        perLot: dist(lots.filter((l) => l.aisles.length).map((l) => l.aisles.length), 0) },
    },
    fuel: fuelStats(fuel),
    downtown: downtownStats(down),
    industrial: industrialStats(ind),
    farm: { n: sites.farms.length, area: dist(sites.farms.map((f) => f.A), 0), short: dist(sites.farms.map((f) => f.w), 1), long: dist(sites.farms.map((f) => f.d), 1),
      buildingsPerYard: dist(sites.farms.map((f) => f.buildings.length), 0), buildingCover: dist(sites.farms.filter((f) => f.buildings.length).map((f) => f.cover), 3),
      buildingTags: counts(sites.farms.flatMap((f) => f.buildings.map((b) => b.tags.building))),
      yardGapToRoadEdge: dist(sites.farms.map((f) => f.gap), 1),
      accessLength: dist(sites.farms.flatMap((f) => f.access.map((a) => a.len)), 1), accessSurface: shares(sites.farms.flatMap((f) => f.access.filter((a) => a.surface).map((a) => a.surface))),
      accessKind: shares(sites.farms.flatMap((f) => f.access.map((a) => a.kind))) },
    quarry: { n: sites.quarries.length, area: dist(sites.quarries.map((f) => f.A), 0), short: dist(sites.quarries.map((f) => f.w), 1), long: dist(sites.quarries.map((f) => f.d), 1),
      buildingsInside: dist(sites.quarries.map((f) => f.buildings.length), 0), buildingArea: dist(sites.quarries.flatMap((f) => f.buildings.map((b) => b.A)), 0),
      gapToRoadEdge: dist(sites.quarries.map((f) => f.gap), 1), accessLength: dist(sites.quarries.flatMap((f) => f.access.map((a) => a.len)), 1),
      accessSurface: shares(sites.quarries.flatMap((f) => f.access.filter((a) => a.surface).map((a) => a.surface))) },
    mill: { sawmills: sites.mills.filter((m) => m.kind === 'sawmill').length, sawmillArea: dist(sites.mills.filter((m) => m.kind === 'sawmill').map((m) => m.A), 0),
      sawmillBuildings: dist(sites.mills.filter((m) => m.kind === 'sawmill').map((m) => m.buildings.length), 0),
      grainElevators: sites.mills.filter((m) => m.kind === 'grain_elevator').length, grainElevatorFootprint: dist(sites.mills.filter((m) => m.kind === 'grain_elevator').map((m) => m.A), 0),
      grainElevatorHeight: dist(sites.mills.filter((m) => m.kind === 'grain_elevator').map((m) => m.height), 1),
      grainElevatorNearRail: round(sites.mills.filter((m) => m.kind === 'grain_elevator' && m.nearRail).length / Math.max(1, sites.mills.filter((m) => m.kind === 'grain_elevator').length), 2) },
    harbor: { n: sites.harbors.length, kinds: counts(sites.harbors.map((h) => h.kind)), area: dist(sites.harbors.map((h) => h.A), 0),
      pierLength: dist(sites.harbors.flatMap((h) => h.piers.map((p) => p.len)), 1), pierWidth: dist(sites.harbors.flatMap((h) => h.piers.map((p) => p.width)), 1),
      piersPer: dist(sites.harbors.map((h) => h.piers.length), 0), slipways: sites.harbors.reduce((a, h) => a + h.slipways, 0),
      parkingLotsWithin80m: dist(sites.harbors.map((h) => h.lots.length), 0), parkingArea: dist(sites.harbors.flatMap((h) => h.lots), 0),
      buildingsInside: dist(sites.harbors.map((h) => h.buildings.length), 0) },
    house: houseStats(house),
    surfaces: surfaceStats(towns),
  };
  return P;
}

function fuelStats(F) {
  const ok = F.filter((f) => f.canopy && f.F);
  return {
    stations: F.length, withCanopy: ok.length, withSitePolygon: F.filter((f) => f.site).length,
    canopy: { short: dist(ok.map((f) => f.canopyW), 1), long: dist(ok.map((f) => f.canopyD), 1), area: dist(ok.map((f) => f.canopyA), 0),
      longAxisToRoad: dist(ok.map((f) => f.canopyAxis), 0), orientation: shares(ok.map((f) => (f.canopyAxis < 25 ? 'parallel' : f.canopyAxis > 65 ? 'perpendicular' : 'oblique'))),
      setbackFromRoadEdge: dist(ok.map((f) => f.canopySetback), 1) },
    shop: { found: ok.filter((f) => f.shop).length, position: shares(ok.filter((f) => f.shop).map((f) => f.shopPos)), gapToCanopy: dist(ok.filter((f) => f.shop).map((f) => f.shopGap), 1),
      attachedToCanopy: round(ok.filter((f) => f.attached).length / Math.max(1, ok.filter((f) => f.shop).length), 2),
      short: dist(ok.filter((f) => f.shop).map((f) => f.shopW), 1), long: dist(ok.filter((f) => f.shop).map((f) => f.shopD), 1), area: dist(ok.filter((f) => f.shop).map((f) => f.shopA), 0),
      setbackFromRoadEdge: dist(ok.filter((f) => f.shop).map((f) => f.shopSetback), 1), offsetAlongRoadFromCanopy: dist(ok.filter((f) => f.shop).map((f) => Math.abs(f.shopOffsetU)), 1) },
    site: { area: dist(ok.filter((f) => f.siteA).map((f) => f.siteA), 0), frontage: dist(ok.filter((f) => f.siteFront).map((f) => f.siteFront), 1), depth: dist(ok.filter((f) => f.siteDepth).map((f) => f.siteDepth), 1),
      canopyShareOfSite: dist(ok.filter((f) => f.siteA).map((f) => f.canopyA / f.siteA), 2) },
    entrances: { mapped: ok.filter((f) => f.entrances.length).length, count: dist(ok.filter((f) => f.entrances.length).map((f) => f.entrances.length), 0),
      onTwoStreets: round(ok.filter((f) => f.streets >= 2).length / Math.max(1, ok.filter((f) => f.entrances.length).length), 2), spacingAlongRoad: dist(ok.map((f) => f.entSpacing), 1),
      widthTagged: dist(ok.flatMap((f) => f.entrances.map((e) => e.width)), 1) },
    pumpsMappedInCanopy: dist(ok.map((f) => f.pumpNodes).filter((n) => n > 0), 0),
    roadClass: shares(ok.map((f) => f.roadClass)),
  };
}
function downtownStats(D) {
  const R = D.flatMap((d) => d.recs);
  return {
    streetfrontBuildings: R.length, sharesAWall: round(R.filter((r) => r.touching).length / Math.max(1, R.length), 2),
    frontWidth: dist(R.map((r) => r.width), 1), depth: dist(R.map((r) => r.depth), 1),
    onStreetParkingTagged: round(R.filter((r) => r.onStreet).length / Math.max(1, R.length), 2), onStreetOrientation: shares(D.flatMap((d) => d.orient)),
    alleyBehind: round(R.filter((r) => r.alleyGap !== null).length / Math.max(1, R.length), 2), alleyGapFromBackWall: dist(R.filter((r) => r.alleyGap !== null).map((r) => r.alleyGap), 1),
    rearLot: round(R.filter((r) => r.rearLot).length / Math.max(1, R.length), 2),
    alleysPerTown: dist(D.map((d) => d.alleyCount), 0), alleyKmPerTown: dist(D.map((d) => d.alleyLen / 1000), 2),
  };
}
function industrialStats(I) {
  const docks = I.flatMap((r) => r.docks);
  return {
    buildings: I.length, withDocksMapped: I.filter((r) => r.docks.length).length,
    docksPerBuilding: dist(I.filter((r) => r.docks.length).map((r) => r.docks.length), 0), dockSide: shares(docks.filter((d) => d.side).map((d) => d.side)),
    apronDepth: dist(docks.map((d) => d.apron).filter((a) => a !== null), 1),
    footprint: dist(I.map((r) => r.b.A), 0), short: dist(I.map((r) => r.b.r.w), 1), long: dist(I.map((r) => r.b.r.d), 1),
    setbackFromRoadEdge: dist(I.filter((r) => r.b.f).map((r) => r.b.f.setback), 1),
    fenced: round(I.filter((r) => r.fenceRatio > 0.5).length / Math.max(1, I.length), 2), gatesNear: dist(I.map((r) => r.gates), 0),
    yardArea: dist(I.filter((r) => r.yardA).map((r) => r.yardA), 0), buildingCoverOfYard: dist(I.filter((r) => r.coverage).map((r) => r.coverage), 2),
    lotsPerBuilding: dist(I.map((r) => r.lots.length), 0), lotPosition: shares(I.flatMap((r) => r.lots.map((l) => l.pos)).filter(Boolean)),
  };
}
function houseStats(H) {
  return {
    houses: H.houses.length, frontageSpacing: dist(H.frontage, 1), setbackFromRoadEdge: dist(H.houses.map((h) => h.f.setback), 1),
    axis: shares(H.houses.map((h) => h.f.axis)),
    driveways: { mapped: H.drives.length, perHouse: round(H.drives.length / Math.max(1, H.houses.length), 3), length: dist(H.drives.map((d) => d.len), 1),
      lengthWithHouse: dist(H.drives.filter((d) => d.house).map((d) => d.len), 1), widthTagged: dist(H.drives.map((d) => d.width), 1),
      side: shares(H.drives.filter((d) => d.side).map((d) => d.side)), surface: shares(H.drives.filter((d) => d.surface).map((d) => d.surface)) },
    garages: { n: H.garages.length, attached: round(H.garages.filter((g) => g.attached).length / Math.max(1, H.garages.length), 2), position: shares(H.garages.map((g) => g.pos)),
      gapToHouse: dist(H.garages.filter((g) => !g.attached).map((g) => g.gap), 1), setbackFromRoadEdge: dist(H.garages.map((g) => g.setback), 1),
      short: dist(H.garages.map((g) => g.g.r.w), 1), long: dist(H.garages.map((g) => g.g.r.d), 1) },
  };
}
function surfaceStats(towns) {
  const o = { parkingLots: [], parkingAisles: [], driveways: [], alleys: [], otherService: [], tracks: [], fuelSites: [], residentialStreets: [], throughRoads: [] };
  for (const { town } of towns) {
    for (const p of town.parking) if (p.tags.surface) o.parkingLots.push(p.tags.surface);
    for (const s of town.service) if (s.tags.surface) (s.svc === 'parking_aisle' ? o.parkingAisles : s.svc === 'driveway' ? o.driveways : s.svc === 'alley' ? o.alleys : o.otherService).push(s.tags.surface);
    for (const t of town.tracks) if (t.tags.surface) o.tracks.push(t.tags.surface);
    for (const f of town.fuelAreas) if (f.tags.surface) o.fuelSites.push(f.tags.surface);
    for (const s of town.streets) if (s.tags.surface) (s.cls === 'through' ? o.throughRoads : o.residentialStreets).push(s.tags.surface);
  }
  return Object.fromEntries(Object.entries(o).map(([k, v]) => [k, shares(v)]));
}

// ------------------------------------------------------------------ the building profile (for lane A's Blender generator)

const regionOf = (townName) => (/Germany$/.test(townName) ? 'eu' : 'na');
const PITCHED = new Set(['gabled', 'hipped', 'half-hipped', 'pyramidal', 'skillion', 'saltbox', 'side_hipped', 'gabled_row']);

// storeys, height and roof of one building: its own tags first, else those of its largest building:part that has them
function threeDOf(b) {
  const els = [{ t: b.tags, poly: b.poly, r: b.r, A: b.A }].concat((b.parts || []).map((p) => ({ t: p.tags, poly: p.poly, r: p.r, A: p.A })).sort((x, y) => y.A - x.A));
  const pick = (k) => els.find((e) => e.t[k] !== undefined && e.t[k] !== '') || null;
  const lvE = pick('building:levels'), shE = pick('roof:shape'), rlE = pick('roof:levels'), orE = pick('roof:orientation'), anE = pick('roof:angle');
  const hE = els.find((e) => num(e.t.height) > 2) || null;
  const both = els.find((e) => num(e.t.height) > 2 && num(e.t['roof:height']) >= 0 && num(e.t['roof:height']) < num(e.t.height)) || null;
  const o = {};
  if (lvE) { const v = num(lvE.t['building:levels']); if (v >= 1 && v < 30) o.levels = Math.round(v); }
  if (hE) { const v = num(hE.t.height); if (v < 150) o.height = v; }
  if (shE) o.roofShape = String(shE.t['roof:shape']).trim();
  if (rlE) { const v = num(rlE.t['roof:levels']); if (v >= 0 && v < 5) o.roofLevels = v; }
  if (orE) o.roofOrientation = orE.t['roof:orientation'];
  // eave height: height minus roof height on the same element (a flat roof: the eave is the height)
  const shapeOf = (e) => e.t['roof:shape'] || (e === both ? o.roofShape : undefined);
  const eaveE = both || (hE && shapeOf(hE) === 'flat' ? hE : null);
  if (both) o.eave = num(both.t.height) - num(both.t['roof:height']);
  else if (eaveE) o.eave = num(hE.t.height);
  if (o.eave !== undefined && !(o.eave > 1.8 && o.eave < 60)) delete o.eave;
  if (o.eave !== undefined && o.levels) { const sh = (o.eave - (num(eaveE.t.min_height) || 0)) / o.levels; if (sh > 2.2 && sh < 5.5) o.storeyHeight = sh; }
  // roof pitch: roof:angle where tagged, else roof height over the half span (the full span for a skillion)
  if (anE) { const v = num(anE.t['roof:angle']); if (v >= 3 && v <= 75) { o.pitch = v; o.pitchSource = 'angle'; } }
  if (o.pitch === undefined && both) {
    const shape = shapeOf(both), rh = num(both.t['roof:height']), r = both.r || minRect(both.poly);
    if (PITCHED.has(shape) && rh > 0.3 && r.w > 2) {
      const across = (both.t['roof:orientation'] || o.roofOrientation) === 'across';
      const half = shape === 'skillion' ? (across ? r.d : r.w) : (across ? r.d / 2 : r.w / 2);
      const v = (Math.atan(rh / half) * 180) / Math.PI;
      if (v >= 3 && v <= 75) { o.pitch = v; o.pitchSource = 'roof:height'; o.pitchShape = shape; }
    }
  }
  if (o.pitch !== undefined && !o.pitchShape) o.pitchShape = o.roofShape || 'unknown';
  return Object.keys(o).length ? o : null;
}

function buildingProfile(siteTowns, tagTowns) {
  const tagOf = (town) => (b) => { b.townName = town.name; b.region = regionOf(town.name); b.set = 'site'; return b; };
  const site = siteTowns.flatMap((t) => t.buildings.filter((b) => b.type !== 'roof').map(tagOf(t)));
  const tags = tagTowns.flatMap((t) => t.buildings.filter((b) => b.type !== 'roof').map((b) => { tagOf(t)(b); b.set = 'tags'; return b; }));
  const all = site.concat(tags);
  for (const b of all) b.d3 = threeDOf(b);
  const keyed = {};
  const add = (k, b) => (keyed[k] = keyed[k] || []).push(b);
  for (const b of all) {
    add('type:' + (b.form === 'streetfront' ? 'downtown_' + b.type : b.type), b);
    add('building:' + (b.tags.building || 'yes'), b);
    for (const p of [b.tags, ...b.pois]) { if (p.shop) add('shop:' + p.shop, b); if (p.amenity && p.amenity !== 'parking') add('amenity:' + p.amenity, b); if (p.craft) add('craft:' + p.craft, b); }
  }
  const byTown = (L) => counts(L.map((b) => b.townName));
  const hist = (vals) => counts(vals.map((v) => String(v)));
  const split = (L, f) => { const o = { n: L.length, ...f(L), towns: byTown(L) }; for (const r of ['na', 'eu']) { const R = L.filter((b) => b.region === r); if (R.length) o[r] = { n: R.length, ...f(R) }; } return o; };
  const footprintStats = (L) => {
    const simp = L.map((b) => simplify(b.poly, 0.4));
    return {
      n: L.length, tagged: L.filter((b) => !b.inferred).length,
      footprint: { area: dist(L.map((b) => b.A), 0), short: dist(L.map((b) => b.r.w), 1), long: dist(L.map((b) => b.r.d), 1), aspect: dist(L.map((b) => b.r.d / Math.max(0.1, b.r.w)), 2),
        rectangularity: dist(L.map((b) => b.A / Math.max(1, b.r.A)), 2), corners: dist(simp.map((p) => p.length), 0),
        shape: shares(simp.map((p) => { const rc = reflexCount(p); return p.length <= 4 ? 'rect' : rc === 1 ? 'L' : rc === 2 ? 'T_or_U' : rc === 0 ? 'convex_other' : 'complex'; })) },
      roofMaterial: counts(L.filter((b) => b.tags['roof:material']).map((b) => b.tags['roof:material'])),
      roofColour: counts(L.filter((b) => b.tags['roof:colour']).map((b) => b.tags['roof:colour'])),
      wallMaterial: counts(L.filter((b) => b.tags['building:material']).map((b) => b.tags['building:material'])),
      wallColour: counts(L.filter((b) => b.tags['building:colour']).map((b) => b.tags['building:colour'])),
      longAxisToStreet: shares(L.filter((b) => b.f && b.f.centreDist < 50).map((b) => b.f.axis)),
      setbackFromRoadEdge: dist(L.filter((b) => b.f && b.f.setback < 80).map((b) => b.f.setback), 1),
      sharesAWall: L.some((b) => b.form === 'streetfront') ? round(L.filter((b) => b.touching).length / L.length, 2) : undefined,
      mainEntranceSide: shares(L.flatMap((b) => (b.doorSides || []).filter((d) => /^(main|yes)$/.test(d.kind || '')).map((d) => d.side))),
      serviceDoorSide: shares(L.flatMap((b) => (b.doorSides || []).filter((d) => /^(service|loading_dock|overhead|garage)$/.test(d.kind || '')).map((d) => d.side))),
    };
  };
  const threeDStats = (L) => {
    const W = (k) => L.filter((b) => b.d3 && b.d3[k] !== undefined);
    const st = W('levels'), rs = W('roofShape'), rl = W('roofLevels'), ro = W('roofOrientation'), ht = W('height'), ev = W('eave'), sh = W('storeyHeight'), pt = W('pitch');
    const pitchByShape = {};
    for (const b of pt) (pitchByShape[b.d3.pitchShape] = pitchByShape[b.d3.pitchShape] || []).push(b.d3.pitch);
    return {
      storeys: split(st, (X) => ({ hist: hist(X.map((b) => b.d3.levels)), dist: dist(X.map((b) => b.d3.levels), 1) })),
      roofShape: split(rs, (X) => ({ shares: shares(X.map((b) => b.d3.roofShape)) })),
      roofLevels: split(rl, (X) => ({ hist: hist(X.map((b) => b.d3.roofLevels)) })),
      roofOrientation: split(ro, (X) => ({ shares: shares(X.map((b) => b.d3.roofOrientation)) })),
      height: split(ht, (X) => ({ dist: dist(X.map((b) => b.d3.height), 1) })),
      eaveHeight: split(ev, (X) => ({ dist: dist(X.map((b) => b.d3.eave), 1) })),
      storeyHeight: split(sh, (X) => ({ dist: dist(X.map((b) => b.d3.storeyHeight), 2) })),
      roofPitchDeg: { ...split(pt, (X) => ({ dist: dist(X.map((b) => b.d3.pitch), 0) })),
        byShape: Object.fromEntries(Object.entries(pitchByShape).filter(([, v]) => v.length >= 3).map(([k, v]) => [k, dist(v, 0)])),
        source: counts(pt.map((b) => b.d3.pitchSource)) },
    };
  };
  const out = {
    note: 'Per building type and per OSM tag, for the procedural Blender buildings (lane A). Footprint, shape, materials, orientation, setback and doors: North American towns only ' +
      '(the 17 site-study towns and the 12 North American storey-and-roof towns). storeys, roofShape, roofLevels, roofOrientation, height, eaveHeight, storeyHeight, roofPitchDeg: every town, ' +
      'each with n, the towns behind it (towns: {town: n}) and a split by region (na = North America, eu = the two German towns, used only for roof shapes per type and for pitch and eave where ' +
      'North America has no heights). A building takes its own tags, else those of its largest building:part. Eave = height - roof:height (flat roofs: the height). Pitch = roof:angle, else ' +
      'atan(roof:height / half span), the span being the footprint\'s short side (the long side with roof:orientation=across; the full span for skillions). ' +
      '"type:" keys are this study\'s business types (downtown_* = street-front commerce); "building:", "shop:", "amenity:", "craft:" keys are OSM tag values. Keys with fewer than 5 buildings are left out.',
    credit: CREDIT, generatedBy: 'tools/osm/measure.mjs',
    towns: { site: siteTowns.map((t) => t.name), storeysAndRoofs: tagTowns.map((t) => t.name) },
    coverage: { buildings: all.length, withStoreys: all.filter((b) => b.d3 && b.d3.levels).length, withRoofShape: all.filter((b) => b.d3 && b.d3.roofShape).length,
      withHeight: all.filter((b) => b.d3 && b.d3.height).length, withEave: all.filter((b) => b.d3 && b.d3.eave).length, withPitch: all.filter((b) => b.d3 && b.d3.pitch).length,
      byTown: Object.fromEntries([...siteTowns, ...tagTowns].map((t) => { const L = all.filter((b) => b.townName === t.name); return [t.name, { buildings: L.length, storeys: L.filter((b) => b.d3 && b.d3.levels).length, roofShape: L.filter((b) => b.d3 && b.d3.roofShape).length, height: L.filter((b) => b.d3 && b.d3.height).length }]; })) },
    keys: {},
  };
  for (const [k, L] of Object.entries(keyed).sort((a, b) => b[1].length - a[1].length)) {
    if (L.length < 5) continue;
    const NA = L.filter((b) => b.region === 'na');
    out.keys[k] = { ...(NA.length >= 5 ? footprintStats(NA) : { n: NA.length }), ...threeDStats(L) };
  }
  return out;
}

// ------------------------------------------------------------------ choosing real templates

function pickTemplates(towns) {
  const cands = [];
  for (const T of towns) {
    const town = T.town;
    // fuel forecourts: canopy + shop (+ site polygon, entrances)
    for (const f of T.fuel) {
      if (!f.canopy || !f.F || !f.shop || f.canopyW < 5 || f.canopyD > 50 || f.canopyA < 60) continue;
      const F = { ...f.F };
      const polys = [f.canopy.poly, f.shop.poly, ...(f.site ? [f.site] : [])].map((p) => localPoly(F, p));
      const box = boxOf(polys);
      const score = 2 + (f.site ? 2 : 0) + Math.min(2, f.entrances.length) + (f.pumpNodes ? 1 : 0) + (f.canopyA > 120 && f.canopyA < 900 ? 1 : 0) + (f.shopA > 60 && f.shopA < 700 ? 1 : 0);
      cands.push({ score, t: templateBase('fuel', town, F, f.shop, {
        osm: { site: f.id, canopy: f.canopy.id, shop: f.shop.id }, canopy: { osm: f.canopy.id, poly: localPoly(F, f.canopy.poly) }, lot: f.site ? { osm: f.id, poly: localPoly(F, f.site) } : null,
        entrances: f.entrances.map((e) => { const q = F.toL(e.p); return { u: r1(q.u), v: r1(q.v), oneway: e.oneway }; }), service: serviceIn(town, F, box), streets: streetsIn(town, F, box), others: contextOf(town, F, box, new Set([f.canopy, f.shop])),
        dims: { canopy: [r1(f.canopyW), r1(f.canopyD)], canopyAxisToRoadDeg: Math.round(f.canopyAxis), canopySetback: r1(f.canopySetback), shopPos: f.shopPos, shopGap: r1(f.shopGap), siteArea: f.siteA ? Math.round(f.siteA) : null } }) });
    }
    // retail and other lots: building + its lot, by position
    for (const l of T.lots) {
      const b = l.building;
      if (!b || !b.f || l.shared || !l.sameSide || b.A < 150) continue;
      const kind = b.form === 'streetfront' ? 'downtown' : b.type;
      if (!['shop', 'supermarket', 'restaurant', 'fast_food', 'office', 'downtown', 'motel', 'car_repair', 'church', 'civic', 'school', 'warehouse', 'industrial'].includes(kind)) continue;
      const F = b.f, polys = [localPoly(F, b.poly), localPoly(F, l.poly)], box = boxOf(polys);
      const aisles = l.aisles.map((a) => ({ osm: a.id, line: localLine(F, a.g) }));
      const stalls = l.stalls.map((s) => ({ osm: s.id, poly: localPoly(F, s.poly) }));
      const tType = kind === 'downtown' ? 'downtown_' + l.pos : ['warehouse', 'industrial'].includes(kind) ? 'warehouse' : kind === 'fast_food' ? 'fast_food' : ['church', 'civic', 'school'].includes(kind) ? 'civic' : 'retail_' + l.pos;
      const score = 1 + Math.min(2, aisles.length) + Math.min(2, stalls.length / 6) + Math.min(2, l.entrances.length) + (l.ratio > 0.5 && l.ratio < 6 ? 1 : 0) + (l.wrap ? 0 : 1) + (l.gap < 8 ? 1 : 0);
      const ind = T.ind.find((r) => r.b === b);
      cands.push({ score: score + (ind && ind.docks.length ? 3 : 0), t: templateBase(tType, town, F, b, {
        osm: { building: b.id, lot: l.id }, lot: { osm: l.id, poly: localPoly(F, l.poly), surface: l.surface }, aisles, stalls,
        entrances: l.entrances.map((e) => { const q = F.toL(e.p); return { u: r1(q.u), v: r1(q.v), svc: e.svc, oneway: e.oneway }; }),
        docks: ind ? ind.docks.map((d) => { const q = F.toL(d.p); return { u: r1(q.u), v: r1(q.v), side: d.side, apron: d.apron !== null ? r1(d.apron) : null }; }) : undefined,
        fences: ['warehouse', 'industrial'].includes(kind) ? fencesIn(town, F, box) : undefined,
        service: serviceIn(town, F, box), streets: streetsIn(town, F, box), others: contextOf(town, F, box, new Set([b])),
        dims: { buildingType: b.type, buildingArea: Math.round(b.A), lotArea: Math.round(l.A), position: l.pos, share: Object.fromEntries(Object.entries(l.share).map(([k, v]) => [k, round(v, 2)])), lotDepth: r1(l.depth), frontGap: r1(l.frontGap) } }) });
    }
    // warehouses with docks but no mapped lot
    for (const r of T.ind) {
      if (!r.docks.length || r.lots.length || !r.b.f) continue;
      const F = r.b.f, box = boxOf([localPoly(F, r.b.poly)], 30);
      cands.push({ score: 2 + Math.min(3, r.docks.length) + (r.fenceRatio > 0.3 ? 1 : 0), t: templateBase('warehouse', town, F, r.b, {
        osm: { building: r.b.id }, docks: r.docks.map((d) => { const q = F.toL(d.p); return { u: r1(q.u), v: r1(q.v), side: d.side, apron: d.apron !== null ? r1(d.apron) : null }; }),
        fences: fencesIn(town, F, box), service: serviceIn(town, F, box), streets: streetsIn(town, F, box), others: contextOf(town, F, box, new Set([r.b])),
        dims: { buildingArea: Math.round(r.b.A), docks: r.docks.length } }) });
    }
    // farms, quarries, sawmills, harbours: the yard polygon in the frame of the road it is reached from
    const yardTemplate = (type, y, extra = {}) => {
      const c = centroid(y.poly), ns = town.nearestStreet(c, 600);
      if (!ns) return;
      const o = { x: ns.h.x, y: ns.h.y };
      let nx = c.x - o.x, ny = c.y - o.y; const nl = Math.hypot(nx, ny) || 1; nx /= nl; ny /= nl;
      const F = { o, n: { x: nx, y: ny }, u: { x: ny, y: -nx }, hw: ns.s.hw, street: ns.s };
      F.toL = (p) => ({ u: (p.x - o.x) * F.u.x + (p.y - o.y) * F.u.y, v: (p.x - o.x) * nx + (p.y - o.y) * ny });
      const yp = localPoly(F, y.poly), box = boxOf([yp], 15);
      const bs = (y.buildings || []).map((b) => ({ osm: b.id, role: b.tags.building, tags: pickTags(b.tags), poly: localPoly(F, b.poly) }));
      const acc = [...town.service, ...town.tracks].map((w) => ({ w, l: clipLine(F, w.g, box) })).filter((x) => x.l).map((x) => ({ osm: x.w.id, kind: x.w.tags.highway === 'track' ? 'track' : x.w.svc || 'service', surface: x.w.tags.surface || null, line: x.l }));
      const score = 1 + Math.min(4, bs.length) + Math.min(2, acc.length) + (extra.bonus || 0);
      const piers = (y.piers || []).map((p) => town.piers.find((q) => q.id === p.id)).filter(Boolean).map((p) => ({ osm: p.id, kind: p.kind, line: localLine(F, p.poly ? p.poly.concat([p.poly[0]]) : p.g) }));
      const lots = type === 'harbor' ? town.parking.filter((p) => polyPolyDist(p.poly, y.poly) < 80).map((p) => ({ osm: p.id, poly: localPoly(F, p.poly) })) : undefined;
      cands.push({ score, t: templateBase(type, town, F, null, { osm: { yard: y.id }, yard: { osm: y.id, kind: y.kind, poly: yp, area: Math.round(y.A) }, buildings: bs, access: acc, piers, lots, fences: fencesIn(town, F, box), streets: streetsIn(town, F, box), dims: { area: Math.round(y.A), buildings: bs.length, piers: piers.length } }) });
    };
    for (const y of T.sites.farms) if (y.A > 800 && y.A < 60000 && y.buildings.length >= 2) yardTemplate('farm', y);
    for (const y of T.sites.quarries) if (y.A > 2000 && y.A < 400000) yardTemplate('quarry', y, { bonus: y.buildings.length ? 1 : 0 });
    for (const y of T.sites.mills) if (y.kind === 'sawmill') yardTemplate('mill', y);
    for (const y of T.sites.harbors) if (y.A < 200000) yardTemplate('harbor', y, { bonus: y.piers.length ? 2 : 0 });
    // grain elevators: the building group around the elevator
    const elevs = [];
    for (const m of T.sites.mills) if (m.kind === 'grain_elevator' && m.b.f && m.A >= 50 && !elevs.some((e) => dist2(e.c, m.b.c) < 80)) {
      elevs.push(m.b);
      const F = m.b.f, box = boxOf([localPoly(F, m.b.poly)], 40);
      cands.push({ score: 2 + (m.height ? 1 : 0) + (m.nearRail ? 1 : 0), t: templateBase('mill', town, F, m.b, { osm: { building: m.b.id }, sub: 'grain_elevator', service: serviceIn(town, F, box), streets: streetsIn(town, F, box), others: contextOf(town, F, box, new Set([m.b])), dims: { footprint: Math.round(m.A), height: m.height || null } }) });
    }
    // house driveways: house + driveway (+ garage)
    for (const d of T.house.drives) {
      const h = d.house;
      if (!h || !h.f) continue;
      const F = h.f, gar = T.house.garages.find((g) => g.house === h);
      const polys = [localPoly(F, h.poly), ...(gar ? [localPoly(F, gar.g.poly)] : [])];
      const box = boxOf(polys, 8);
      cands.push({ score: 1 + (gar ? 2 : 0) + (d.width ? 1 : 0) + (d.surface ? 1 : 0) + (d.len > 8 && d.len < 60 ? 1 : 0), t: templateBase('house', town, F, h, {
        osm: { building: h.id, driveway: d.id, garage: gar ? gar.g.id : null }, driveway: { osm: d.id, line: localLine(F, d.g), width: d.width || null, surface: d.surface },
        garage: gar ? { osm: gar.g.id, poly: localPoly(F, gar.g.poly), attached: gar.attached } : null, others: contextOf(town, F, box, new Set([h, ...(gar ? [gar.g] : [])])), streets: streetsIn(town, F, box),
        dims: { houseArea: Math.round(h.A), drivewayLength: r1(d.len), side: d.side, setback: r1(h.f.setback) } }) });
    }
    // downtown blocks with an alley behind
    for (const d of T.down.recs) {
      if (d.alleyGap === null || !d.b.f) continue;
      const F = d.b.f, box = boxOf([localPoly(F, d.b.poly)], 25);
      cands.push({ score: 2 + (d.touching ? 1 : 0) + (d.rearLot ? 2 : 0) + (d.onStreet ? 1 : 0), t: templateBase('downtown_alley', town, F, d.b, {
        osm: { building: d.b.id }, service: serviceIn(town, F, box), streets: streetsIn(town, F, box), others: contextOf(town, F, box, new Set([d.b])),
        lots: T.lots.filter((l) => polyPolyDist(l.poly, d.b.poly) < 30).map((l) => ({ osm: l.id, poly: localPoly(F, l.poly) })),
        dims: { alleyGap: r1(d.alleyGap), frontWidth: r1(d.width), depth: r1(d.depth), sharesWall: d.touching, onStreetTagged: d.onStreet } }) });
    }
  }
  // choose up to N per type, best first, at most 2 per town per type, no near-duplicates
  const N = { fuel: 6, retail_front: 6, retail_side: 6, retail_rear: 6, downtown_rear: 4, downtown_side: 3, downtown_front: 2, downtown_alley: 4, fast_food: 4, civic: 4, motel: 3, car_repair: 3, supermarket: 3,
    retail_wrap: 0, warehouse: 6, farm: 6, quarry: 5, mill: 5, harbor: 5, house: 6, office: 3, restaurant: 3 };
  cands.sort((a, b) => b.score - a.score || (a.t.town < b.t.town ? -1 : 1));
  const chosen = [], perTown = {}, seen = new Set();
  for (const c of cands) {
    const t = c.t, lim = N[t.type] ?? 3;
    const have = chosen.filter((x) => x.type === t.type).length;
    if (have >= lim) continue;
    const k = t.type + '|' + t.town;
    if ((perTown[k] || 0) >= 2) continue;
    const sig = t.osm.building || t.osm.yard || t.osm.site || JSON.stringify(t.osm);
    if (seen.has(sig)) continue;
    seen.add(sig); perTown[k] = (perTown[k] || 0) + 1;
    chosen.push({ id: `${t.type}-${String(have + 1).padStart(2, '0')}`, score: round(c.score, 1), ...t });
  }
  const out = { note: 'Real site layouts from OpenStreetMap, normalised into the street frame of the site (u along the street, to the right when facing the site; v away from the street, v = 0 on the street centreline; metres). ' +
      'The generator fits one to a target building: it may mirror u, scale within the measured ranges of siteProfile.json, and shift v to the target road\'s real kerb line (road.hwAssumed is the half-width this study assumed). ' +
      'Every template lists its source town and OSM element ids for the ODbL credit.', credit: CREDIT, generatedBy: 'tools/osm/measure.mjs', templates: chosen };
  return { out, cands: cands.map((c) => ({ score: c.score, type: c.t.type, town: c.t.town, osm: c.t.osm, dims: c.t.dims })) };
}

// Run only as a script (extract-town.mjs imports typeOf, buildTownFrom and lotAnalysis).
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
