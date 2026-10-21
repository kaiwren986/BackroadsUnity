#!/usr/bin/env node
// The business-type classifier fixture shared by tools/osm/measure.mjs typeOf (JS) and its C# port
// Backroads.Core.WorldBake.BusinessClassifier (EditMode test Town.ClassifierFixture). Card T3.0, overview contract 5.
//
//   node tools/world/classifier-fixture.mjs <town-id>   writes the fixture (200 buildings)
//   node tools/world/classifier-fixture.mjs --check      runs typeOf on every fixture input and compares (exit 1 on any difference)
//
// The fixture holds the classifier's inputs only (tags, the tags of attached points, land use keys at the centroid,
// footprint area, the fuel/marina/works/sawmill flags) and the expected answer {type, sub, inferred}. Inputs come from
// two sources: synthetic cases that walk every rule of typeOf in order (each alternative of each pattern, the size
// thresholds on both sides, precedence between rules, case and separator edge cases), and real buildings of the town's
// TownData extract (names already stripped), stratified by type, to fill up to 200. Real inputs: (c) OpenStreetMap
// contributors, ODbL 1.0.
import fs from 'node:fs';
import path from 'node:path';
import { typeOf } from '../osm/measure.mjs';
import { REPO, townDataDir } from './town-lib.mjs';

export const FIXTURE = path.join(REPO, 'Game', 'Assets', 'Backroads', 'Tests', 'EditMode', 'Town', 'Fixtures', 'classifier-fixture.json');
const SIZE = 200;
const FLAGS = ['inFuel', 'nearFuel', 'inMarina', 'inWorks', 'inSawmill'];

// One input as typeOf reads it.
function toB(rec) {
  const b = { tags: rec.tags, pois: rec.pois, lu: Object.fromEntries(rec.lu.map((k) => [k, { use: k }])), A: rec.A };
  for (const f of FLAGS) b[f] = !!rec.ctx[f];
  return b;
}
export function classify(rec) {
  const r = typeOf(toB(rec));
  const out = { type: r.type };
  if (r.sub !== undefined) out.sub = r.sub;
  if (r.inferred) out.inferred = true;
  return out;
}

function synthetic() {
  const S = [];
  const add = (note, tags, o = {}) => S.push({ note, tags, pois: o.pois ?? [], lu: o.lu ?? [], A: o.A ?? 150, ctx: o.ctx ?? {} });
  // 1. roofs and canopies
  add('roof', { building: 'roof' });
  add('canopy', { building: 'canopy' });
  add('roof near fuel', { building: 'roof' }, { ctx: { nearFuel: true } });
  add('canopy tagged fuel', { building: 'canopy', amenity: 'fuel' });
  add('man_made canopy, no building', { man_made: 'canopy' });
  add('man_made canopy in a fuel area', { building: 'yes', man_made: 'canopy' }, { ctx: { inFuel: true, nearFuel: true } });
  add('roof inside a fuel area but not near (flags are independent)', { building: 'roof' }, { ctx: { inFuel: true } });
  // 2. fuel shops
  add('in a fuel area', { building: 'yes' }, { ctx: { inFuel: true } });
  add('fuel point inside', { building: 'retail' }, { pois: [{ amenity: 'fuel' }] });
  add('fuel tag on the building', { building: 'yes', amenity: 'fuel' });
  add('fuel beats quarry', { building: 'yes', amenity: 'fuel' }, { lu: ['quarry'] });
  // 3. quarry, mill, silo, farm
  add('quarry land use', { building: 'house' }, { lu: ['quarry'] });
  add('in a sawmill area', { building: 'yes' }, { ctx: { inSawmill: true } });
  add('craft sawmill point', { building: 'industrial' }, { pois: [{ craft: 'sawmill' }] });
  add('industrial=sawmill tag', { building: 'yes', industrial: 'sawmill' });
  add('silo in a farmyard', { building: 'silo' }, { lu: ['farmyard'] });
  add('grain silo outside a farmyard', { building: 'grain_silo' });
  add('man_made silo', { building: 'yes', man_made: 'silo' });
  add('farmhouse', { building: 'house' }, { lu: ['farmyard'] });
  add('farm barn', { building: 'barn' }, { lu: ['farmyard'] });
  add('farm garage', { building: 'garage' }, { lu: ['farmyard'] });
  add('farm other', { building: 'yes' }, { lu: ['farmyard'] });
  add('farmyard beats harbour', { building: 'shed' }, { lu: ['farmyard', 'harbour'] });
  // 4. harbour
  add('harbour land use', { building: 'yes' }, { lu: ['harbour'] });
  add('port land use', { building: 'warehouse' }, { lu: ['port'] });
  add('in a marina', { building: 'yes' }, { ctx: { inMarina: true } });
  add('boathouse', { building: 'boathouse' });
  // 5. car repair
  for (const [k, v] of [['amenity', 'car_wash'], ['shop', 'car_repair'], ['shop', 'car'], ['shop', 'tyres'], ['shop', 'motorcycle'], ['shop', 'car_parts'], ['shop', 'truck'], ['craft', 'mechanic'], ['service', 'vehicle_repair'], ['service', 'vehicles']])
    add(`car repair ${k}=${v}`, { building: 'yes' }, { pois: [{ [k]: v }] });
  add('car repair tag on the building', { building: 'commercial', shop: 'car_repair' });
  add('service=tyres is not vehicle', { building: 'yes', service: 'tyres' });
  // 6. motel, church, school, civic
  for (const v of ['motel', 'hotel', 'guest_house']) add(`tourism=${v}`, { building: 'yes', tourism: v });
  add('building=hotel', { building: 'hotel' });
  add('building=motel', { building: 'motel' });
  add('hotel beats restaurant', { building: 'yes', tourism: 'hotel', amenity: 'restaurant' });
  add('place of worship', { building: 'yes', amenity: 'place_of_worship' });
  for (const v of ['church', 'chapel', 'cathedral', 'temple', 'mosque', 'synagogue']) add(`building=${v}`, { building: v });
  add('church beats school', { building: 'school', amenity: 'place_of_worship' });
  for (const v of ['school', 'college', 'kindergarten', 'university']) add(`amenity=${v}`, { building: 'yes' }, { pois: [{ amenity: v }] });
  add('building=school', { building: 'school' });
  add('building=kindergarten', { building: 'kindergarten' });
  for (const v of ['townhall', 'post_office', 'fire_station', 'police', 'library', 'community_centre', 'courthouse', 'clinic', 'hospital', 'doctors', 'dentist',
    'social_facility', 'nursing_home', 'arts_centre', 'theatre', 'cinema']) add(`amenity=${v}`, { building: 'yes', amenity: v });
  for (const v of ['civic', 'public', 'government', 'fire_station', 'hospital', 'townhall', 'train_station']) add(`building=${v}`, { building: v });
  // 7. commerce
  add('supermarket point', { building: 'yes' }, { pois: [{ shop: 'supermarket' }] });
  add('building=supermarket', { building: 'supermarket' });
  add('supermarket;bakery is a shop', { building: 'yes', shop: 'supermarket;bakery' });
  add('fast food', { building: 'yes' }, { pois: [{ amenity: 'fast_food' }] });
  for (const v of ['restaurant', 'cafe', 'bar', 'pub', 'ice_cream', 'biergarten']) add(`amenity=${v}`, { building: 'yes', amenity: v });
  add('restaurant;bar is not a restaurant', { building: 'yes', amenity: 'restaurant;bar' });
  add('fast food beats restaurant', { building: 'yes' }, { pois: [{ amenity: 'restaurant' }, { amenity: 'fast_food' }] });
  add('bank', { building: 'yes', amenity: 'bank' });
  add('office=any', { building: 'yes', office: 'insurance' });
  add('building=office', { building: 'office' });
  add('empty office value is ignored', { building: 'yes', office: '' }, { A: 100 });
  add('office value of a line break only is ignored', { building: 'yes', office: '\n' }, { A: 100 });
  add('office value with a line break and text', { building: 'yes', office: '\nlawyer' }, { A: 100 });
  add('shop=any', { building: 'yes', shop: 'hardware' });
  add('shop point', { building: 'house' }, { pois: [{ shop: 'bakery' }] });
  for (const v of ['retail', 'commercial', 'kiosk']) add(`building=${v}`, { building: v });
  add('empty shop value is ignored', { building: 'yes', shop: '' }, { A: 400 });
  // 8. buildings by their own tag
  for (const v of ['warehouse', 'storage', 'hangar', 'transportation']) add(`building=${v}`, { building: v });
  for (const v of ['industrial', 'manufacture', 'factory', 'works']) add(`building=${v}`, { building: v });
  add('in a works area', { building: 'yes' }, { ctx: { inWorks: true } });
  add('apartments', { building: 'apartments' });
  add('dormitory', { building: 'dormitory' });
  for (const v of ['house', 'detached', 'residential', 'semidetached_house', 'semi', 'bungalow', 'cabin', 'duplex', 'terrace', 'farm', 'static_caravan', 'townhouse'])
    add(`building=${v}`, { building: v });
  for (const v of ['garage', 'garages', 'shed', 'carport', 'outbuilding', 'hut', 'shelter']) add(`building=${v}`, { building: v });
  for (const v of ['barn', 'farm_auxiliary', 'cowshed', 'stable', 'greenhouse', 'sty', 'chicken_coop']) add(`building=${v}`, { building: v });
  add('case matters: House', { building: 'House' }, { A: 150 });
  add('case matters: Garage', { building: 'Garage' }, { A: 30 });
  add('no building tag', {}, { A: 200 });
  // 9. untagged buildings: land use, then size (the thresholds on both sides)
  for (const A of [59.99, 60, 1200, 1200.01]) add(`industrial land, ${A} m2`, { building: 'yes' }, { lu: ['industrial'], A });
  for (const A of [39.99, 40]) add(`retail land, ${A} m2`, { building: 'yes' }, { lu: ['retail'], A });
  add('commercial land, 500 m2', { building: 'yes' }, { lu: ['commercial'], A: 500 });
  add('industrial beats retail land', { building: 'yes' }, { lu: ['retail', 'industrial'], A: 300 });
  add('residential land is not used', { building: 'yes' }, { lu: ['residential'], A: 30 });
  for (const A of [54.99, 55, 319.99, 320, 1500, 1500.01, 80000]) add(`size ${A} m2`, { building: 'yes' }, { A });
  return S;
}

function realRecords(id) {
  const file = path.join(townDataDir(id), 'buildings.json');
  const bs = JSON.parse(fs.readFileSync(file, 'utf8')).buildings.filter((b) => b.type !== 'unknown');
  return bs.map((b) => ({ note: `real ${b.osm}${b.part !== undefined ? '#' + b.part : ''}`, tags: b.tags, pois: b.pois.map((p) => p.tags), lu: b.lu, A: b.area, ctx: b.ctx, type: b.type }));
}

function build(id) {
  const syn = synthetic();
  const real = realRecords(id);
  // stratified: round-robin over types (rarest first), each type in OSM id order, until SIZE
  const byType = new Map();
  for (const r of real) { if (!byType.has(r.type)) byType.set(r.type, []); byType.get(r.type).push(r); }
  const groups = [...byType.entries()].sort((a, b) => a[1].length - b[1].length || (a[0] < b[0] ? -1 : 1)).map(([, v]) => v);
  const picked = [];
  for (let round = 0; picked.length < SIZE - syn.length && groups.some((g) => g.length > round); round++)
    for (const g of groups) if (g.length > round && picked.length < SIZE - syn.length) {
      // spread the picks over the list instead of taking its head
      const k = Math.floor((round * 7919) % g.length);
      const cand = g.filter((x) => !picked.includes(x));
      picked.push(cand[k % cand.length]);
    }
  const cases = [...syn.map((r, i) => ({ id: `syn-${String(i + 1).padStart(3, '0')}`, ...r })),
    ...picked.map((r, i) => ({ id: `real-${String(i + 1).padStart(3, '0')}`, note: r.note, tags: r.tags, pois: r.pois, lu: r.lu, A: r.A, ctx: r.ctx }))];
  if (cases.length !== SIZE) throw new Error(`fixture has ${cases.length} cases, wanted ${SIZE}`);
  for (const c of cases) c.expect = classify(c);
  return cases;
}

function write(cases) {
  const head = {
    format: 'backroads-classifier-fixture', version: 1,
    note: 'Inputs and expected answers of tools/osm/measure.mjs typeOf; Backroads.Core.WorldBake.BusinessClassifier must give the same {type, sub, inferred} (Town.ClassifierFixture). Regenerate with node tools/world/classifier-fixture.mjs <town-id>; check the JS side with --check.',
    credit: 'real-* inputs: (c) OpenStreetMap contributors, ODbL 1.0 (names removed)', inputs: 'tags, pois (tags of attached points), lu (land use keys at the centroid), A (m2), ctx (inFuel, nearFuel, inMarina, inWorks, inSawmill)',
  };
  const lines = ['{'];
  for (const [k, v] of Object.entries(head)) lines.push(`  ${JSON.stringify(k)}: ${JSON.stringify(v)},`);
  lines.push('  "cases": [');
  cases.forEach((c, i) => {
    const o = { id: c.id, note: c.note, tags: c.tags, pois: c.pois, lu: c.lu, A: c.A, ctx: c.ctx, expect: c.expect };
    lines.push('    ' + JSON.stringify(o) + (i < cases.length - 1 ? ',' : ''));
  });
  lines.push('  ]', '}');
  fs.mkdirSync(path.dirname(FIXTURE), { recursive: true });
  fs.writeFileSync(FIXTURE, lines.join('\n') + '\n');
}

function check() {
  const f = JSON.parse(fs.readFileSync(FIXTURE, 'utf8'));
  const bad = [];
  for (const c of f.cases) {
    const got = classify(c);
    if (JSON.stringify(got) !== JSON.stringify(c.expect)) bad.push(`${c.id} (${c.note}): expected ${JSON.stringify(c.expect)}, typeOf gives ${JSON.stringify(got)}`);
  }
  const types = new Set(f.cases.map((c) => c.expect.type));
  console.log(`classifier fixture: ${f.cases.length} cases, ${types.size} types (${[...types].sort().join(', ')}); ${bad.length} differ from typeOf`);
  if (bad.length) { console.error(bad.join('\n')); process.exit(1); }
}

const args = process.argv.slice(2);
if (args.includes('--check')) check();
else {
  const id = args.find((a) => !a.startsWith('--'));
  if (!id) { console.error('usage: node tools/world/classifier-fixture.mjs <town-id> | --check'); process.exit(1); }
  const cases = build(id);
  write(cases);
  console.log(`wrote ${FIXTURE}: ${cases.length} cases (${cases.filter((c) => c.id.startsWith('syn')).length} synthetic, ${cases.filter((c) => c.id.startsWith('real')).length} real)`);
  check();
}
