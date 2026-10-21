// Reads the cached 3DEP exportImage tiles of a town (fetch-town.mjs) into one grid and checks that they join.
// Zero dependencies. Elevation: USGS 3D Elevation Program (public domain).
//
// A tile answer (format=bsq, pixelType=F32) is w*h little-endian floats, rows from the north, optionally followed by a
// one-bit-per-pixel validity mask (most significant bit first). The mosaic is returned SOUTH-first: sample (i, j) at
// k = j*n + i has its centre at local x = -half + i*res, z = -half + j*res (the TownData dem layout).
import fs from 'node:fs';
import path from 'node:path';
import { sha256 } from './town-lib.mjs';

export function decodeTile(buf, w, h) {
  const want = w * h * 4, maskLen = Math.ceil((w * h) / 8);
  if (buf.length !== want && buf.length !== want + maskLen) throw new Error(`tile is ${buf.length} bytes, expected ${want} (+${maskLen} mask)`);
  const hasMask = buf.length === want + maskLen;
  const z = new Float32Array(w * h);
  let nodata = 0;
  for (let i = 0; i < w * h; i++) {
    const valid = hasMask ? (buf[want + (i >> 3)] >> (7 - (i & 7))) & 1 : 1;
    let v = buf.readFloatLE(i * 4);
    if (!valid || !Number.isFinite(v) || v < -500 || v > 9000) { v = NaN; nodata++; }
    z[i] = v;
  }
  return { z, nodata, hasMask };
}

// dem: the "dem" record of sources.json (samples, tiles with r0, c0, w, h, file, sha256).
export function mosaicDem(srcDir, dem, { verify = true } = {}) {
  const n = dem.samples;
  const z = new Float32Array(n * n).fill(NaN);
  const owner = new Int16Array(n * n).fill(-1);
  const seams = { pairs: 0, compared: 0, differing: 0, maxAbs: 0, bitIdentical: true };
  const pairSeen = new Set();
  const tileInfo = [];
  dem.tiles.forEach((t, ti) => {
    const buf = fs.readFileSync(path.join(srcDir, t.file));
    if (verify && t.sha256 && sha256(buf) !== t.sha256) throw new Error(`${t.file}: SHA-256 does not match sources.json`);
    const d = decodeTile(buf, t.w, t.h);
    tileInfo.push({ name: t.name, nodata: d.nodata, mask: d.hasMask });
    for (let r = 0; r < t.h; r++) {
      const gr = t.r0 + r, j = n - 1 - gr;
      for (let c = 0; c < t.w; c++) {
        const k = j * n + t.c0 + c, v = d.z[r * t.w + c];
        if (owner[k] < 0) { z[k] = v; owner[k] = ti; continue; }
        pairSeen.add(`${owner[k]}-${ti}`);
        seams.compared++;
        const a = z[k];
        const same = (Number.isNaN(a) && Number.isNaN(v)) || Object.is(a, v);
        if (!same) {
          seams.differing++;
          seams.bitIdentical = false;
          const dv = Number.isNaN(a) || Number.isNaN(v) ? Infinity : Math.abs(a - v);
          if (dv > seams.maxAbs) seams.maxAbs = dv;
        }
      }
    }
  });
  seams.pairs = pairSeen.size;
  let nodata = 0, min = Infinity, max = -Infinity, uncovered = 0;
  for (let k = 0; k < z.length; k++) {
    if (owner[k] < 0) uncovered++;
    const v = z[k];
    if (Number.isNaN(v)) nodata++;
    else { if (v < min) min = v; if (v > max) max = v; }
  }
  if (uncovered) throw new Error(`${uncovered} DEM samples are not covered by any tile`);
  return { n, z, nodata, min, max, seams, tiles: tileInfo };
}

// Bilinear sample of a south-first grid (n x n, spacing res, first sample at -half), clamped to the grid.
export function sampler(z, n, res, half) {
  return (x, zz) => {
    const fi = Math.min(n - 1.000001, Math.max(0, (x + half) / res)), fj = Math.min(n - 1.000001, Math.max(0, (zz + half) / res));
    const i = Math.floor(fi), j = Math.floor(fj), u = fi - i, v = fj - j, k = j * n + i;
    return (z[k] * (1 - u) + z[k + 1] * u) * (1 - v) + (z[k + n] * (1 - u) + z[k + n + 1] * u) * v;
  };
}
