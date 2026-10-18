#!/usr/bin/env node
// Zero-dependency receiver for the canon export (PLAN §1.8.3, T0.7c). Binds 127.0.0.1 only.
//
//   node tools/oracle/serve-export.mjs --root artifacts/oracle/552f780 --out <dir> [--port 8131] [--timeout-min 10]
//        [--open <browser.exe> --profile <dir>]   (Export-Canon.bat: opens a normal browser window on the export page)
//
//  GET  /<path>                 static files from --root (no caching)
//  POST /__export/<relpath>     body written to <out>/<relpath> (path-sanitised; each path once per run)
//  POST /__export/done          JSON body {seed, renderer, ...}: writes <out>/manifest.json with the SHA-256
//                               of every file, then exits 0
//  POST /__export/meta/<name>   JSON kept in memory and merged into the manifest at done: 'export' is the
//                               exporter's metadata, 'trace-<id>' one entry per golden trace
//  POST /__export/fail          JSON body {error}: logs it and exits 3
//  POST /__log                  one line of page log, echoed to stdout
// Exit codes: 0 done, 2 bad arguments, 3 the page reported a failure, 4 manifest failed, 5 overall timeout,
// 6 stalled (no request for --idle-timeout-s, default 120).
// --out must be empty or absent, so a manifest only ever describes one export run.
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';

function arg(name, def = null) {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : def;
}
const ROOT = path.resolve(arg('--root', 'artifacts/oracle/552f780'));
const OUT = path.resolve(arg('--out', 'artifacts/oracle/export'));
const PORT = Number(arg('--port', '8131'));
const TIMEOUT_MIN = Number(arg('--timeout-min', '10'));
const IDLE_S = Number(arg('--idle-timeout-s', '120')); // no request at all for this long = the page stalled
const HOST = '127.0.0.1';
const OPEN = arg('--open'), PROFILE = arg('--profile');

if (!fs.existsSync(path.join(ROOT, 'index.html'))) { console.error(`serve-export: no index.html under ${ROOT}; run make-snapshot.mjs first`); process.exit(2); }
if (fs.existsSync(OUT) && fs.readdirSync(OUT).length) { console.error(`serve-export: --out ${OUT} is not empty`); process.exit(2); }
fs.mkdirSync(OUT, { recursive: true });

const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8', '.png': 'image/png', '.jpg': 'image/jpeg',
  '.svg': 'image/svg+xml', '.ico': 'image/x-icon', '.wav': 'audio/wav', '.mp3': 'audio/mpeg', '.glb': 'model/gltf-binary',
};

const written = new Map(); // relpath -> {bytes, sha256}
const metaParts = {};     // name -> JSON from POST /__export/meta/<name>
const t0 = Date.now();
const log = (...a) => console.log(`[${((Date.now() - t0) / 1000).toFixed(1).padStart(6)}s]`, ...a);

// relpath rules: forward slashes, segments of [A-Za-z0-9._-], no dot-only segments, at most 8 deep
function safeRel(raw) {
  let rel;
  try { rel = decodeURIComponent(raw); } catch { return null; }
  if (!rel || rel.length > 200 || rel.includes('\\') || rel.includes('\0') || rel.startsWith('/')) return null;
  const segs = rel.split('/');
  if (segs.length > 8) return null;
  for (const s of segs) if (!/^[A-Za-z0-9._-]+$/.test(s) || /^\.+$/.test(s)) return null;
  if (rel === 'manifest.json') return null; // the server writes that one
  const abs = path.resolve(OUT, ...segs);
  if (!abs.startsWith(OUT + path.sep)) return null;
  return { rel, abs };
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const parts = [];
    req.on('data', (c) => parts.push(c));
    req.on('end', () => resolve(Buffer.concat(parts)));
    req.on('error', reject);
  });
}

function walk(dir, base = '') {
  const out = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const rel = base ? `${base}/${e.name}` : e.name;
    if (e.isDirectory()) out.push(...walk(path.join(dir, e.name), rel));
    else out.push(rel);
  }
  return out;
}

function writeManifest(meta) {
  const files = {};
  for (const rel of walk(OUT).sort()) {
    if (rel === 'manifest.json') continue;
    const buf = fs.readFileSync(path.join(OUT, ...rel.split('/')));
    files[rel] = { bytes: buf.length, sha256: createHash('sha256').update(buf).digest('hex') };
    const w = written.get(rel);
    if (!w || w.sha256 !== files[rel].sha256) throw new Error(`file on disk does not match what was received: ${rel}`);
  }
  const manifest = {
    format: 'backroads-canon',
    version: 1,
    ...meta,
    files,
  };
  fs.writeFileSync(path.join(OUT, 'manifest.json'), JSON.stringify(manifest, null, 2) + '\n');
  return Object.keys(files).length;
}

let finished = false;
function finish(code, why) {
  if (finished) return;
  finished = true;
  log(why);
  server.close();
  setTimeout(() => process.exit(code), 100);
}

let lastRequest = Date.now();
const server = http.createServer(async (req, res) => {
  lastRequest = Date.now();
  const url = new URL(req.url, `http://${HOST}:${PORT}`);
  const p = url.pathname;
  const reply = (code, body, type = 'application/json; charset=utf-8') => {
    res.writeHead(code, { 'Content-Type': type, 'Cache-Control': 'no-store' });
    res.end(typeof body === 'string' ? body : JSON.stringify(body));
  };
  try {
    if (req.method === 'POST' && p === '/__log') {
      const line = (await readBody(req)).toString('utf8');
      log('page:', line.slice(0, 2000));
      return reply(200, { ok: true });
    }
    if (req.method === 'POST' && p.startsWith('/__export/meta/')) {
      const name = p.slice('/__export/meta/'.length);
      if (!/^[A-Za-z0-9._-]{1,80}$/.test(name)) return reply(400, { error: 'bad meta name' });
      metaParts[name] = JSON.parse((await readBody(req)).toString('utf8'));
      log('meta part ' + name);
      return reply(200, { ok: true });
    }
    if (req.method === 'POST' && p === '/__export/done') {
      const body = JSON.parse((await readBody(req)).toString('utf8') || '{}');
      const meta = { ...(metaParts.export || {}), ...body };
      const traces = Object.entries(metaParts).filter(([k]) => k.startsWith('trace-'));
      if (traces.length) meta.traces = Object.fromEntries(traces.sort().map(([k, v]) => [k.slice(6), v]));
      const n = writeManifest(meta);
      reply(200, { ok: true, files: n });
      return finish(0, `done: manifest.json written with ${n} files -> ${OUT}`);
    }
    if (req.method === 'POST' && p === '/__export/fail') {
      const body = (await readBody(req)).toString('utf8');
      reply(200, { ok: true });
      console.error('export failed in the page: ' + body);
      return finish(3, 'page reported failure');
    }
    if (req.method === 'POST' && p.startsWith('/__export/')) {
      const s = safeRel(p.slice('/__export/'.length));
      if (!s) return reply(400, { error: 'bad path' });
      if (written.has(s.rel)) return reply(409, { error: 'already written: ' + s.rel });
      const body = await readBody(req);
      fs.mkdirSync(path.dirname(s.abs), { recursive: true });
      fs.writeFileSync(s.abs, body);
      const sha = createHash('sha256').update(body).digest('hex');
      written.set(s.rel, { bytes: body.length, sha256: sha });
      log(`wrote ${s.rel} (${body.length} B, ${sha.slice(0, 12)})`);
      return reply(200, { ok: true, bytes: body.length, sha256: sha });
    }
    if (req.method !== 'GET' && req.method !== 'HEAD') return reply(405, { error: 'method' });
    let rel;
    try { rel = decodeURIComponent(p); } catch { return reply(400, 'Bad request', 'text/plain'); }
    if (rel.endsWith('/')) rel += 'index.html';
    const file = path.resolve(ROOT, '.' + rel);
    if (!file.startsWith(ROOT + path.sep)) return reply(403, 'Forbidden', 'text/plain');
    fs.readFile(file, (err, data) => {
      if (err) return reply(404, 'Not found', 'text/plain');
      res.writeHead(200, { 'Content-Type': TYPES[path.extname(file).toLowerCase()] || 'application/octet-stream', 'Cache-Control': 'no-store' });
      res.end(req.method === 'HEAD' ? undefined : data);
    });
  } catch (e) {
    console.error(e);
    reply(500, { error: String(e && e.message) });
    if (p === '/__export/done') finish(4, 'manifest failed: ' + e.message);
  }
});

server.on('error', (e) => { console.error(`serve-export: ${e.message}`); process.exit(2); });
server.listen(PORT, HOST, () => {
  log(`serving ${ROOT} at http://${HOST}:${PORT}/ ; export -> ${OUT}`);
  if (OPEN) {
    const url = `http://${HOST}:${PORT}/index.html?export=unity`;
    // EXPORT_HEADLESS=1 is only for testing Export-Canon.bat without opening a window
    const quiet = ['--disable-extensions', '--disable-sync', '--disable-background-networking', '--disable-component-update', '--no-pings', '--disable-default-apps',
      '--disable-background-timer-throttling', '--disable-renderer-backgrounding', '--disable-backgrounding-occluded-windows'];
    const a = [...(process.env.EXPORT_HEADLESS === '1' ? ['--headless=new'] : []), '--no-first-run', '--no-default-browser-check', ...quiet, '--window-size=1600,900', ...(PROFILE ? [`--user-data-dir=${PROFILE}`] : []), url];
    spawn(OPEN, a, { detached: true, stdio: 'ignore' }).unref();
    log('opened ' + url);
  }
});
setInterval(() => { if (Date.now() - lastRequest > IDLE_S * 1000) finish(6, `stalled: no request for ${IDLE_S} s`); }, 5000).unref();
setTimeout(() => finish(5, `timeout after ${TIMEOUT_MIN} min without /__export/done`), TIMEOUT_MIN * 60 * 1000).unref();

