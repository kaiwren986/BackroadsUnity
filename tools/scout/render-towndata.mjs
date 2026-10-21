// The scout's map renderer pointed at a TownData extract (card T3.0 step 7): the same hillshade, contours, land use,
// water, roads and footprints as render-map.mjs, drawn from TownData/<town>/v1 alone, so the map shows exactly what
// the lanes get, and no real names (TownData has none; the map is titled with the in-game name).
//
//   node tools/scout/render-map.mjs --town-data TownData/<town>/v1 [--out docs/milestones/M3/town-source.png]
//
// Buildings are coloured by business type; the play area, the extract edge and the kerb and downtown zones of the
// manifest are outlined. Map data (c) OpenStreetMap contributors, ODbL; elevation USGS 3DEP (public domain).
import fs from 'node:fs';
import path from 'node:path';

const MAP = 2000, PAD = 24, PANEL = 640;
export const TD_W = PAD + MAP + PAD + PANEL + PAD, TD_H = PAD + MAP + PAD;

const BUSINESS_FILL = {
  House: '#b5654a', Apartments: '#9c4a3a', Accessory: '#8f8478', Retail: '#d6336c', Office: '#7b5cd6', Restaurant: '#e8590c', FastFood: '#f08c00',
  Supermarket: '#c2255c', Fuel: '#e03131', CarRepair: '#495057', Motel: '#1c7ed6', Civic: '#2b8a3e', Church: '#5f3dc4', School: '#0b7285',
  Warehouse: '#6741d9', Industrial: '#5c3d99', Farm: '#a87b1c', Quarry: '#6d6250', Mill: '#8b4513', Harbor: '#1a7fc1', Roof: '#adb5bd', Unknown: '#000000',
};
const ROAD = {
  secondary: [9, 7, '#f6cf6a'], primary: [9, 7, '#f0a35e'], trunk: [10, 8, '#e8894a'], tertiary: [7.5, 5.6, '#fff3b0'], unclassified: [6, 4.4, '#ffffff'],
  residential: [6, 4.4, '#ffffff'], living_street: [6, 4.4, '#ffffff'], service: [3.8, 2.6, '#ffffff'], track: [3.4, 2.2, '#c9a46a'],
};
const ROAD_ORDER = ['service', 'track', 'residential', 'living_street', 'unclassified', 'tertiary', 'secondary', 'primary', 'trunk'];

export function renderTownData(dir, { hillshadeImage, contours, esc }) {
  const read = (f) => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8'));
  const man = read('manifest.json'), dj = read('dem/dem.json');
  const roads = read('roads.json'), bld = read('buildings.json').buildings, lu = read('landuse.json'), water = read('water.json'), sites = read('sites.json');
  const H = man.extract.half, S = 2 * H, half = man.playArea.half, Y0 = dj.Y0;
  // DEM: 2 x 2 block means of the 1 m grid, rows north first (the scout's layout), in elevation metres
  const raw = fs.readFileSync(path.join(dir, 'dem', 'dem_1m.f32'));
  const src = new Float32Array(raw.buffer.slice(raw.byteOffset, raw.byteOffset + raw.length));
  const n = dj.n, W = Math.floor((n - 1) / 2), res = S / W;
  const z = new Float32Array(W * W);
  for (let r = 0; r < W; r++) for (let c = 0; c < W; c++) {
    const j = n - 2 - 2 * r, i = 2 * c; // south-first source rows
    z[r * W + c] = (src[j * n + i] + src[j * n + i + 1] + src[(j + 1) * n + i] + src[(j + 1) * n + i + 1]) / 4 + Y0;
  }
  const dem = { meta: { size: S }, z, W, H: W, res };

  const K = MAP / S;
  const P = ([x, zz]) => `${(PAD + (x + H) * K).toFixed(1)},${(PAD + (H - zz) * K).toFixed(1)}`;
  const d = (pts, close) => (pts.length ? 'M' + pts.map(P).join('L') + (close ? 'Z' : '') : '');
  const polysD = (f) => f.polys.map((p) => d(p.outer, true) + (p.holes ?? []).map((h) => d(h, true)).join('')).join('');
  const nodeXZ = new Map(roads.nodes.map((q) => [q.id, [q.x, q.z]]));
  const wayPts = (w) => w.nodes.map((id) => nodeXZ.get(id));
  const o = [];
  o.push(`<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="${TD_W}" height="${TD_H}" viewBox="0 0 ${TD_W} ${TD_H}">`);
  o.push(`<title>${esc(`${man.townName}: town source extract (TownData v${man.version})`)}</title>`);
  o.push(`<defs><clipPath id="map"><rect x="${PAD}" y="${PAD}" width="${MAP}" height="${MAP}"/></clipPath></defs>`);
  o.push(`<style>text{font-family:'Segoe UI',Arial,Helvetica,sans-serif;fill:#1d1d1d}.h{paint-order:stroke;stroke:#fff;stroke-width:5px;stroke-linejoin:round}.sm{font-size:15px}.pn{font-size:17px}.hd{font-size:20px;font-weight:700}</style>`);
  o.push(`<rect width="${TD_W}" height="${TD_H}" fill="#f5f2ea"/>`);
  o.push(`<g clip-path="url(#map)">`);
  o.push(`<image x="${PAD}" y="${PAD}" width="${MAP}" height="${MAP}" preserveAspectRatio="none" xlink:href="data:image/png;base64,${hillshadeImage(dem).toString('base64')}"/>`);
  // land use
  const LU = [
    [(t) => t.landuse === 'forest' || t.natural === 'wood', 'fill="#5f8f4e" fill-opacity="0.35"'],
    [(t) => ['farmland', 'animal_keeping'].includes(t.landuse), 'fill="#f0e29a" fill-opacity="0.6" stroke="#b8a55a" stroke-width="0.8"'],
    [(t) => ['meadow', 'grass'].includes(t.landuse) || ['grassland', 'scrub', 'heath'].includes(t.natural), 'fill="#cfe5a3" fill-opacity="0.5"'],
    [(t) => t.landuse === 'cemetery', 'fill="#b9d1ab" fill-opacity="0.85"'],
    [(t) => t.leisure !== undefined, 'fill="#bfe3a0" fill-opacity="0.7" stroke="#6f9c4a" stroke-width="0.8"'],
    [(t) => t.natural === 'wetland', 'fill="#a9d3c4" fill-opacity="0.7"'],
  ];
  for (const [test, style] of LU) {
    const ds = lu.areas.filter((a) => test(a.tags)).map(polysD).join('');
    if (ds) o.push(`<path fill-rule="evenodd" ${style} d="${ds}"/>`);
  }
  const yards = sites.yards.map(polysD).join('') + sites.parking.map(polysD).join('');
  if (yards) o.push(`<path fill-rule="evenodd" fill="#d9d4cc" stroke="#7a736b" stroke-width="0.8" d="${yards}"/>`);
  // contours (elevation, 10 m, bold 50 m)
  const cl = contours(dem, 10);
  const shift = (l) => l.map(([x, y]) => [x - H, y - H]);
  o.push(`<path fill="none" stroke="#6b4f33" stroke-opacity="0.35" stroke-width="0.7" d="${cl.filter((c) => c.level % 50 !== 0).flatMap((c) => c.lines.map((l) => d(shift(l)))).join('')}"/>`);
  o.push(`<path fill="none" stroke="#6b4f33" stroke-opacity="0.65" stroke-width="1.4" d="${cl.filter((c) => c.level % 50 === 0).flatMap((c) => c.lines.map((l) => d(shift(l)))).join('')}"/>`);
  // water
  const wl = (pred, w, c) => { const ds = water.lines.filter((l) => pred(l)).flatMap((l) => l.parts.map((p) => d(p))).join(''); if (ds) o.push(`<path fill="none" stroke="${c}" stroke-width="${w}" stroke-linecap="round" stroke-linejoin="round" d="${ds}"/>`); };
  wl((l) => !l.river && l.tags.waterway !== 'canal', 2, '#4a90c8');
  wl((l) => l.river, 9, '#9cc9ea');
  const wa = water.areas.map(polysD).join('');
  if (wa) o.push(`<path fill-rule="evenodd" fill="#8ec3e6" stroke="#3f86bd" stroke-width="1.2" d="${wa}"/>`);
  wl((l) => l.river, 1.4, '#3f86bd');
  wl((l) => l.tags.waterway === 'canal', 5, '#5aa0d6');
  const st = water.structures.filter((s) => s.parts).flatMap((s) => s.parts.map((p) => d(p))).join('');
  if (st) o.push(`<path fill="none" stroke="#24425c" stroke-width="5" d="${st}"/>`);
  for (const s of water.structures.filter((x) => x.p)) { const [px, py] = P(s.p).split(',').map(Number); o.push(`<rect x="${px - 4}" y="${py - 4}" width="8" height="8" fill="#24425c"/>`); }
  // railways (an old grade)
  const rd = roads.rail.map((w) => d(wayPts(w))).join('');
  if (rd) o.push(`<path fill="none" stroke="#7d6a8a" stroke-width="2.4" stroke-dasharray="8 5" d="${rd}"/>`);
  // power lines, runway
  const pl = lu.lines.filter((l) => l.tags.power).flatMap((l) => l.parts.map((p) => d(p))).join('');
  if (pl) o.push(`<path fill="none" stroke="#555" stroke-width="1" stroke-dasharray="1 4" d="${pl}"/>`);
  const rw = lu.lines.filter((l) => l.tags.aeroway).flatMap((l) => l.parts.map((p) => d(p))).join('');
  if (rw) o.push(`<path fill="none" stroke="#8fbf6a" stroke-width="12" d="${rw}"/>`);
  // roads
  const by = {};
  for (const w of roads.ways) (by[w.tags.highway] ??= []).push(w);
  const paths = (by.footway ?? []).concat(by.path ?? [], by.steps ?? [], by.cycleway ?? []);
  if (paths.length) o.push(`<path fill="none" stroke="#b5544b" stroke-width="1.2" stroke-dasharray="2 3" d="${paths.map((w) => d(wayPts(w))).join('')}"/>`);
  const classOf = (h) => (h?.endsWith('_link') ? h.replace('_link', '') : h);
  const groups = {};
  for (const w of roads.ways) { const c = classOf(w.tags.highway); if (ROAD[c]) (groups[c] ??= []).push(w); }
  for (const c of ROAD_ORDER) if (groups[c]) o.push(`<path fill="none" stroke="#5a5550" stroke-width="${ROAD[c][0]}" stroke-linecap="round" stroke-linejoin="round" d="${groups[c].map((w) => d(wayPts(w))).join('')}"/>`);
  for (const c of ROAD_ORDER) if (groups[c]) o.push(`<path fill="none" stroke="${ROAD[c][2]}" stroke-width="${ROAD[c][1]}" stroke-linecap="round" stroke-linejoin="round" d="${groups[c].map((w) => d(wayPts(w))).join('')}"/>`);
  const brWays = new Set(roads.bridgeSpans.flatMap((b) => b.ways));
  const bl = roads.ways.filter((w) => brWays.has(w.osm)).map((w) => d(wayPts(w))).join('');
  if (bl) o.push(`<path fill="none" stroke="#111" stroke-width="12" stroke-linecap="butt" d="${bl}"/><path fill="none" stroke="#fffbe8" stroke-width="8" stroke-linecap="butt" d="${bl}"/>`);
  // stop signs from tags
  for (const q of roads.nodes.filter((x) => x.tags?.highway === 'stop')) { const [px, py] = P([q.x, q.z]).split(',').map(Number); o.push(`<circle cx="${px}" cy="${py}" r="2.6" fill="#c92a2a"/>`); }
  // buildings by business type
  const byB = {};
  for (const b of bld) (byB[b.business] ??= []).push(b);
  for (const [k, list] of Object.entries(byB)) {
    const ds = list.map((b) => d(b.poly, true) + (b.holes ?? []).map((h) => d(h, true)).join('')).join('');
    o.push(`<path fill-rule="evenodd" fill="${BUSINESS_FILL[k] ?? '#000'}" stroke="#2b1a14" stroke-width="0.5" d="${ds}"/>`);
  }
  const sf = bld.filter((b) => b.form === 'streetfront').map((b) => d(b.poly, true)).join('');
  if (sf) o.push(`<path fill="none" stroke="#111" stroke-width="1.6" d="${sf}"/>`);
  // zones and squares
  const z1 = man.zones.kerbZone?.polygon, z2 = man.zones.downtownZone?.polygon;
  if (z1) o.push(`<path fill="none" stroke="#2f9e44" stroke-width="3" stroke-dasharray="10 6" d="${d(z1, true)}"/>`);
  if (z2) o.push(`<path fill="none" stroke="#d6336c" stroke-width="3" stroke-dasharray="4 4" d="${d(z2, true)}"/>`);
  const outer = `M${P([-H, H])}L${P([H, H])}L${P([H, -H])}L${P([-H, -H])}Z`, inner = `M${P([-half, half])}L${P([-half, -half])}L${P([half, -half])}L${P([half, half])}Z`;
  o.push(`<path fill-rule="evenodd" fill="#ffffff" fill-opacity="0.38" d="${outer}${inner}"/>`);
  o.push(`<path fill="none" stroke="#e07b00" stroke-width="4" d="${inner}"/>`);
  const [lx, ly] = P([half, half]).split(',').map(Number);
  o.push(`<text class="h" x="${lx - 10}" y="${ly + 30}" text-anchor="end" style="font-size:22px;font-weight:700;fill:#e07b00">play area 2,048 m</text>`);
  const [ox, oy] = P([0, 0]).split(',').map(Number);
  o.push(`<path d="M${ox - 9} ${oy}h18M${ox} ${oy - 9}v18" stroke="#111" stroke-width="2.5"/><text class="h sm" x="${ox + 12}" y="${oy - 8}">origin (0, 0)</text>`);
  o.push('</g>');
  // scale bar, north arrow, frame
  const sbx = PAD + 30, sby = PAD + MAP - 40;
  o.push(`<rect x="${sbx - 12}" y="${sby - 34}" width="${1000 * K + 70}" height="58" fill="#fff" fill-opacity="0.85" rx="6"/>`);
  o.push(`<rect x="${sbx}" y="${sby}" width="${500 * K}" height="9" fill="#222"/><rect x="${sbx + 500 * K}" y="${sby}" width="${500 * K}" height="9" fill="#fff" stroke="#222" stroke-width="1.5"/>`);
  o.push(`<text class="sm" x="${sbx}" y="${sby - 8}">0</text><text class="sm" x="${sbx + 500 * K}" y="${sby - 8}" text-anchor="middle">500 m</text><text class="sm" x="${sbx + 1000 * K}" y="${sby - 8}" text-anchor="middle">1 km</text>`);
  const nax = PAD + MAP - 50, nay = PAD + 60;
  o.push(`<circle cx="${nax}" cy="${nay}" r="34" fill="#fff" fill-opacity="0.85"/><path d="M${nax} ${nay - 26}l12 34l-12 -8l-12 8z" fill="#222"/><text x="${nax}" y="${nay + 26}" text-anchor="middle" style="font-size:16px;font-weight:700">N</text>`);
  o.push(`<rect x="${PAD}" y="${PAD}" width="${MAP}" height="${MAP}" fill="none" stroke="#333" stroke-width="2"/>`);
  // panel
  const X = PAD + MAP + PAD + 6;
  let y = PAD + 34;
  const line = (txt, cls = 'pn') => { o.push(`<text class="${cls}" x="${X}" y="${y}">${esc(txt)}</text>`); };
  o.push(`<text x="${X}" y="${y}" style="font-size:34px;font-weight:800">${esc(man.townName)}</text>`); y += 30;
  line(`Town source extract, TownData v${man.version} (card T3.0)`, 'pn'); y += 22;
  line(`OSM data of ${man.osm.timestamp.slice(0, 10)}; local frame ${man.frame.crs}, x east, z north`, 'sm'); y += 20;
  line(`map: the extract square, ${S.toLocaleString('en-US')} m (play area + ${man.extract.skirt} m)`, 'sm'); y += 30;
  const c = man.counts;
  o.push(`<text class="hd" x="${X}" y="${y}">In the play area</text>`); y += 8; o.push(`<path d="M${X} ${y}H${X + 560}" stroke="#999"/>`); y += 24;
  const row = (a, b) => { o.push(`<text class="pn" x="${X}" y="${y}">${esc(a)}</text><text class="pn" x="${X + 560}" y="${y}" text-anchor="end">${esc(b)}</text>`); y += 23; };
  row('Buildings (footprints)', `${c.buildings.playArea} (${c.buildings.playAreaInferred} typed by size)`);
  row('  street-front', String(c.buildings.playAreaStreetfront));
  const km = c.roadsKm.playArea, sum = (ks) => ks.reduce((s, k) => s + (km[k] ?? 0), 0).toFixed(1);
  row('Roads, km: state routes / local / service', `${sum(['secondary', 'secondary_link', 'primary', 'trunk'])} / ${sum(['tertiary', 'residential', 'unclassified', 'living_street'])} / ${sum(['service', 'track'])}`);
  row('Footways (mapped sidewalks, paths), km', sum(['footway', 'path', 'steps']));
  row('Road bridges: river / stream', `${c.bridges.filter((b) => b.inPlay && b.over === 'river').length} / ${c.bridges.filter((b) => b.inPlay && b.over === 'stream').length}`);
  row('Ground, m above Y0', `${(c.dem.playArea.elevationMin - Y0).toFixed(1)} - ${(c.dem.playArea.elevationMax - Y0).toFixed(1)}`);
  row('Y0 (lowest river point - 2 m), NAVD88', `${Y0.toFixed(2)} m`);
  row('River levels above and below the dam (y)', water.levels.map((l) => l.p50.toFixed(2)).sort((a, b) => b - a).join(' / '));
  row('DEM', `${dj.n} x ${dj.n} at ${dj.res} m, ${dj.nodata} voids`);
  y += 12;
  o.push(`<text class="hd" x="${X}" y="${y}">Buildings by business type</text>`); y += 8; o.push(`<path d="M${X} ${y}H${X + 560}" stroke="#999"/>`); y += 22;
  const types = Object.entries(c.buildings.playAreaByBusiness);
  types.forEach(([k, v], i) => {
    const bx = X + (i % 2) * 290, by2 = y + Math.floor(i / 2) * 24;
    o.push(`<rect x="${bx}" y="${by2 - 13}" width="24" height="15" fill="${BUSINESS_FILL[k] ?? '#000'}" stroke="#2b1a14" stroke-width="0.5"/><text class="sm" x="${bx + 34}" y="${by2}">${esc(`${k} ${v}`)}</text>`);
  });
  y += Math.ceil(types.length / 2) * 24 + 4;
  line('Thick outline: street-front form. Untagged buildings are typed by size', 'sm'); y += 18;
  line('(the study rule: under 55 m2 accessory, under 320 m2 house, else retail).', 'sm'); y += 30;
  o.push(`<text class="hd" x="${X}" y="${y}">Legend</text>`); y += 8; o.push(`<path d="M${X} ${y}H${X + 560}" stroke="#999"/>`); y += 22;
  const leg = [
    ['<path d="M0 7h26" stroke="#5a5550" stroke-width="9"/><path d="M0 7h26" stroke="#f6cf6a" stroke-width="7"/>', 'State route'],
    ['<path d="M0 7h26" stroke="#5a5550" stroke-width="6"/><path d="M0 7h26" stroke="#fff" stroke-width="4.4"/>', 'Local road'],
    ['<path d="M0 7h26" stroke="#5a5550" stroke-width="3.8"/><path d="M0 7h26" stroke="#fff" stroke-width="2.6"/>', 'Service road, alley, drive'],
    ['<path d="M0 7h26" stroke="#b5544b" stroke-width="1.2" stroke-dasharray="2 3"/>', 'Footway / sidewalk'],
    ['<path d="M0 7h26" stroke="#111" stroke-width="12"/><path d="M0 7h26" stroke="#fffbe8" stroke-width="8"/>', 'Bridge'],
    ['<circle cx="13" cy="7" r="3" fill="#c92a2a"/>', 'Stop sign (tagged)'],
    ['<path d="M0 7h26" stroke="#7d6a8a" stroke-width="2.4" stroke-dasharray="8 5"/>', 'Old railway grade'],
    ['<rect width="26" height="14" fill="#8ec3e6" stroke="#3f86bd"/>', 'River area (mapped)'],
    ['<path d="M0 7h26" stroke="#9cc9ea" stroke-width="9"/><path d="M0 7h26" stroke="#3f86bd" stroke-width="1.4"/>', 'River centreline only'],
    ['<path d="M0 7h26" stroke="#24425c" stroke-width="5"/>', 'Dam (weir), lock, gates'],
    ['<rect width="26" height="14" fill="#f0e29a" stroke="#b8a55a"/>', 'Farmland'],
    ['<rect width="26" height="14" fill="#5f8f4e" fill-opacity="0.45"/>', 'Wood'],
    ['<rect width="26" height="14" fill="#d9d4cc" stroke="#7a736b"/>', 'Parking, yard'],
    ['<path d="M0 7h26" stroke="#6b4f33" stroke-width="1.4"/>', 'Contour (10 m, bold 50 m)'],
    ['<path d="M0 7h26" stroke="#2f9e44" stroke-width="3" stroke-dasharray="10 6"/>', 'Kerb zone (village + 20 m)'],
    ['<path d="M0 7h26" stroke="#d6336c" stroke-width="3" stroke-dasharray="4 4"/>', 'Downtown zone (+ 30 m)'],
    ['<rect width="26" height="14" fill="#fff" fill-opacity="0.6" stroke="#e07b00" stroke-width="2"/>', 'Play area; pale: 300 m skirt'],
    ['<path d="M0 7h26" stroke="#8fbf6a" stroke-width="10"/>', 'Grass airstrip'],
  ];
  leg.forEach(([sym, label], i) => {
    const lx2 = X + (i % 2) * 290, ly2 = y + Math.floor(i / 2) * 24;
    o.push(`<g transform="translate(${lx2},${ly2 - 11})">${sym}</g><text class="sm" x="${lx2 + 36}" y="${ly2 + 1}">${esc(label)}</text>`);
  });
  y = Math.max(y + Math.ceil(leg.length / 2) * 24 + 10, TD_H - PAD - 46);
  line('Map data (c) OpenStreetMap contributors, ODbL.', 'sm'); y += 18;
  line('Elevation: USGS 3D Elevation Program, 1 m lidar DEM (public domain).', 'sm');
  o.push('</svg>');
  return o.join('\n');
}
