// Tiny raster helpers (PNG writer, polygon fill, thick lines) for quick-look images. Zero dependencies.
import zlib from 'node:zlib';

const BG = [246, 244, 238];
// ------------------------------------------------------------------ PNG

function crc32(buf) {
  let c, crc = 0xffffffff;
  for (let n = 0; n < buf.length; n++) { c = (crc ^ buf[n]) & 0xff; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; crc = (crc >>> 8) ^ c; }
  return (crc ^ 0xffffffff) >>> 0;
}
export function png(w, h, rgba) {
  const chunk = (type, data) => { const len = Buffer.alloc(4); len.writeUInt32BE(data.length); const td = Buffer.concat([Buffer.from(type), data]); const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td)); return Buffer.concat([len, td, crc]); };
  const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  const raw = Buffer.alloc((w * 4 + 1) * h);
  for (let y = 0; y < h; y++) { raw[y * (w * 4 + 1)] = 0; rgba.copy(raw, y * (w * 4 + 1) + 1, y * w * 4, (y + 1) * w * 4); }
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]);
}
export class Canvas {
  constructor(w, h) { this.w = w; this.h = h; this.d = Buffer.alloc(w * h * 4); for (let i = 0; i < w * h; i++) { this.d[i * 4] = BG[0]; this.d[i * 4 + 1] = BG[1]; this.d[i * 4 + 2] = BG[2]; this.d[i * 4 + 3] = 255; } }
  px(x, y, c, a = 1) { x |= 0; y |= 0; if (x < 0 || y < 0 || x >= this.w || y >= this.h) return; const k = this.clip; if (k && (x < k[0] || y < k[1] || x >= k[2] || y >= k[3])) return; const i = (y * this.w + x) * 4; for (let k = 0; k < 3; k++) this.d[i + k] = this.d[i + k] * (1 - a) + c[k] * a; }
  poly(P, c, a = 1) { // even-odd scanline fill
    let y0 = Infinity, y1 = -Infinity; for (const p of P) { y0 = Math.min(y0, p[1]); y1 = Math.max(y1, p[1]); }
    for (let y = Math.max(0, Math.floor(y0)); y <= Math.min(this.h - 1, Math.ceil(y1)); y++) {
      const xs = [];
      for (let i = 0, j = P.length - 1; i < P.length; j = i++) { const a1 = P[i], b1 = P[j]; if ((a1[1] > y + 0.5) !== (b1[1] > y + 0.5)) xs.push(a1[0] + ((y + 0.5 - a1[1]) * (b1[0] - a1[0])) / (b1[1] - a1[1])); }
      xs.sort((p, q) => p - q);
      for (let k = 0; k + 1 < xs.length; k += 2) for (let x = Math.ceil(xs[k] - 0.5); x <= Math.floor(xs[k + 1] - 0.5); x++) this.px(x, y, c, a);
    }
  }
  line(a, b, c, wpx = 1) {
    const L = Math.hypot(b[0] - a[0], b[1] - a[1]), n = Math.max(1, Math.ceil(L * 2)), r = wpx / 2;
    for (let i = 0; i <= n; i++) { const x = a[0] + ((b[0] - a[0]) * i) / n, y = a[1] + ((b[1] - a[1]) * i) / n; for (let dy = -r; dy <= r; dy += 0.5) for (let dx = -r; dx <= r; dx += 0.5) if (dx * dx + dy * dy <= r * r + 0.01) this.px(x + dx, y + dy, c); }
  }
  pline(P, c, wpx = 1, closed = false) { for (let i = 1; i < P.length; i++) this.line(P[i - 1], P[i], c, wpx); if (closed && P.length > 2) this.line(P[P.length - 1], P[0], c, wpx); }
  dot(p, c, r) { for (let dy = -r; dy <= r; dy++) for (let dx = -r; dx <= r; dx++) if (dx * dx + dy * dy <= r * r) this.px(p[0] + dx, p[1] + dy, c); }
}

