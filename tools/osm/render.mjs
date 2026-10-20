// Draws the real site templates (Game/Assets/Content/sites/siteTemplates.json) as a contact sheet:
//   artifacts/osm/templates.png                     (for a quick look; not committed)
//   docs/research/08-site-layouts/templates-<group>.svg  (committed with the research note)
//
//   node tools/osm/render.mjs
//
// Colours: street (dark grey, its assumed width), lot (light grey), building (brown), canopy (orange),
// other buildings (tan), aisles and service ways (blue), stalls (white outlines), entrances (red dots),
// docks (purple), fences (green), yards (pale green), driveways (blue). Zero dependencies (PNG via node:zlib).
// Map data (c) OpenStreetMap contributors, ODbL 1.0.

import fs from 'node:fs';
import path from 'node:path';
import { Canvas, png } from './raster.mjs';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(HERE, '..', '..');
const SRC = path.join(ROOT, 'Game', 'Assets', 'Content', 'sites', 'siteTemplates.json');
const PNG_OUT = path.join(ROOT, 'artifacts', 'osm', 'templates.png');
const SVG_DIR = path.join(ROOT, 'docs', 'research', '08-site-layouts');

const COL = { bg: [246, 244, 238], street: [92, 92, 96], lot: [196, 196, 192], yard: [214, 228, 196], bld: [150, 104, 72], canopy: [236, 150, 40], other: [214, 196, 160],
  aisle: [60, 110, 200], stall: [255, 255, 255], ent: [220, 40, 40], dock: [130, 60, 170], fence: [40, 140, 60], drive: [60, 110, 200], frame: [120, 120, 120] };

// ------------------------------------------------------------------ drawing one template

function layers(t) {
  const L = [];
  for (const s of t.streets || []) L.push({ k: 'street', line: s.line, w: s.hwAssumed * 2 });
  if (t.yard) L.push({ k: 'yard', poly: t.yard.poly });
  if (t.lot) L.push({ k: 'lot', poly: t.lot.poly });
  for (const l of t.lots || []) L.push({ k: 'lot', poly: l.poly });
  for (const o of t.others || []) L.push({ k: 'other', poly: o.poly });
  for (const b of t.buildings || []) L.push({ k: 'bld', poly: b.poly });
  if (t.building) L.push({ k: 'bld', poly: t.building.poly });
  if (t.garage) L.push({ k: 'other', poly: t.garage.poly });
  if (t.canopy) L.push({ k: 'canopy', poly: t.canopy.poly });
  for (const s of t.stalls || []) L.push({ k: 'stall', poly: s.poly });
  for (const a of t.aisles || []) L.push({ k: 'aisle', line: a.line });
  for (const s of t.service || []) L.push({ k: 'aisle', line: s.line, thin: true });
  for (const a of t.access || []) L.push({ k: 'aisle', line: a.line, thin: true });
  if (t.driveway) L.push({ k: 'drive', line: t.driveway.line });
  for (const f of t.fences || []) L.push({ k: 'fence', line: f.line });
  for (const e of t.entrances || []) L.push({ k: 'ent', p: [e.u, e.v] });
  for (const d of t.docks || []) L.push({ k: 'dock', p: [d.u, d.v] });
  return L;
}
function extent(t) {
  let u0 = Infinity, u1 = -Infinity, v0 = Infinity, v1 = -Infinity;
  const add = (P) => { for (const [u, v] of P) { u0 = Math.min(u0, u); u1 = Math.max(u1, u); v0 = Math.min(v0, v); v1 = Math.max(v1, v); } };
  for (const l of layers(t)) if (l.poly && l.k !== 'other') add(l.poly); else if (l.p) add([l.p]);
  if (!Number.isFinite(u0)) for (const l of layers(t)) if (l.poly) add(l.poly);
  const pad = 8;
  return { u0: u0 - pad, u1: u1 + pad, v0: Math.min(v0 - pad, -12), v1: v1 + pad };
}

function drawPng(cv, t, ox, oy, size) {
  const E = extent(t), s = Math.min(size / (E.u1 - E.u0), size / (E.v1 - E.v0));
  const X = ([u, v]) => [ox + (u - E.u0) * s, oy + size - (v - E.v0) * s]; // v up (street at the bottom)
  for (const l of layers(t)) {
    const c = COL[l.k];
    if (l.k === 'street') cv.pline(l.line.map(X), c, Math.max(1, l.w * s));
    else if (l.poly && l.k === 'stall') cv.pline(l.poly.map(X), c, 1, true);
    else if (l.poly) cv.poly(l.poly.map(X), c, l.k === 'lot' || l.k === 'yard' ? 0.9 : 1);
    else if (l.line) cv.pline(l.line.map(X), c, l.thin ? 1 : 2);
    else if (l.p) cv.dot(X(l.p), c, 3);
  }
  // 10 m scale bar
  cv.line([ox + 4, oy + size - 4], [ox + 4 + 10 * s, oy + size - 4], [0, 0, 0], 2);
}

function svgOf(t, size) {
  const E = extent(t), s = Math.min(size / (E.u1 - E.u0), size / (E.v1 - E.v0));
  const X = ([u, v]) => `${((u - E.u0) * s).toFixed(1)},${(size - (v - E.v0) * s).toFixed(1)}`;
  const rgb = (c) => `rgb(${c.join(',')})`;
  const parts = [];
  for (const l of layers(t)) {
    const c = rgb(COL[l.k]);
    if (l.k === 'street') parts.push(`<polyline points="${l.line.map(X).join(' ')}" fill="none" stroke="${c}" stroke-width="${Math.max(1, l.w * s).toFixed(1)}" stroke-linejoin="round"/>`);
    else if (l.poly && l.k === 'stall') parts.push(`<polygon points="${l.poly.map(X).join(' ')}" fill="none" stroke="${c}" stroke-width="0.8"/>`);
    else if (l.poly) parts.push(`<polygon points="${l.poly.map(X).join(' ')}" fill="${c}"${l.k === 'lot' || l.k === 'yard' ? ' fill-opacity="0.9"' : ''}/>`);
    else if (l.line) parts.push(`<polyline points="${l.line.map(X).join(' ')}" fill="none" stroke="${c}" stroke-width="${l.thin ? 1 : 2}"/>`);
    else if (l.p) { const [x, y] = X(l.p).split(','); parts.push(`<circle cx="${x}" cy="${y}" r="3" fill="${c}"/>`); }
  }
  parts.push(`<line x1="4" y1="${size - 4}" x2="${(4 + 10 * s).toFixed(1)}" y2="${size - 4}" stroke="black" stroke-width="2"/>`);
  return parts.join('');
}

function main() {
  const J = JSON.parse(fs.readFileSync(SRC, 'utf8'));
  const T = J.templates;
  const size = 260, gap = 12, cols = 6, rows = Math.ceil(T.length / cols);
  const cv = new Canvas(cols * (size + gap) + gap, rows * (size + gap) + gap);
  T.forEach((t, i) => drawPng(cv, t, gap + (i % cols) * (size + gap), gap + Math.floor(i / cols) * (size + gap), size));
  fs.mkdirSync(path.dirname(PNG_OUT), { recursive: true });
  fs.writeFileSync(PNG_OUT, png(cv.w, cv.h, cv.d));
  console.log('wrote', PNG_OUT, `(${T.length} templates, ${cols} per row)`);
  T.forEach((t, i) => console.log(String(i).padStart(3), t.id.padEnd(18), t.town.padEnd(26), JSON.stringify(t.dims)));
  // SVG sheets per group, committed with the research note
  const groups = {};
  for (const t of T) { const g = t.type.split('_')[0]; (groups[g] = groups[g] || []).push(t); }
  fs.mkdirSync(SVG_DIR, { recursive: true });
  for (const [g, list] of Object.entries(groups)) {
    const tile = 240, lab = 34, c2 = Math.min(3, list.length), r2 = Math.ceil(list.length / c2);
    const W = c2 * (tile + gap) + gap, H = r2 * (tile + lab + gap) + gap + 22;
    const body = list.map((t, i) => {
      const x = gap + (i % c2) * (tile + gap), y = 22 + gap + Math.floor(i / c2) * (tile + lab + gap);
      return `<g transform="translate(${x},${y})"><rect width="${tile}" height="${tile}" fill="rgb(${COL.bg.join(',')})" stroke="#bbb"/>${svgOf(t, tile)}` +
        `<text x="0" y="${tile + 13}" font-size="11" font-family="sans-serif">${t.id}: ${esc(t.town)}</text>` +
        `<text x="0" y="${tile + 27}" font-size="9" font-family="sans-serif" fill="#555">${esc(Object.values(t.osm).filter(Boolean).join(' '))}</text></g>`;
    }).join('\n');
    const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">\n<rect width="${W}" height="${H}" fill="white"/>\n` +
      `<text x="${gap}" y="16" font-size="12" font-family="sans-serif">Real ${g} templates (street at the bottom; 10 m bar). Map data (c) OpenStreetMap contributors, ODbL.</text>\n${body}\n</svg>\n`;
    fs.writeFileSync(path.join(SVG_DIR, `templates-${g}.svg`), svg);
  }
  console.log('wrote', Object.keys(groups).length, 'SVG sheets in', SVG_DIR);
}
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');

main();
