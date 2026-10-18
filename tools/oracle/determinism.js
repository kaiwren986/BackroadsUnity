// Determinism shim for the oracle snapshot (PLAN §1.8.2) [T4]. Injected by make-snapshot.mjs as the
// FIRST module of index.html, so it runs before any game module is evaluated. It imports nothing.
//
// Active only when the page URL has ?export=… or ?trace=… or ?det=1, so the snapshot still plays like
// the original without them. When active:
//  - Math.random is a seeded mulberry32 (seed: ?seed=<uint32>, default below; recorded in the manifest).
//    The world uses its own mulberry32 streams, so world hashes do not depend on it (the export proves it).
//    ?rlog=1 logs every draw, so references that consume random numbers can be replayed in C#.
//  - performance.now() and Date.now() read a scripted clock that only moves when __det.advance(ms) or
//    __det.step(ms) is called.
//  - requestAnimationFrame is wrapped: callbacks are queued and run only by __det.step(ms), so the game
//    loop never runs on its own and the exporter can freeze it or step it explicitly.
const params = new URLSearchParams(location.search);
const ACTIVE = params.has('export') || params.has('trace') || params.get('det') === '1';

export const DEFAULT_SEED = 20261018;

if (ACTIVE) {
  const seed = (Number(params.get('seed') ?? DEFAULT_SEED) >>> 0);
  let a = seed;
  const log = params.get('rlog') === '1' ? [] : null;
  const det = {
    seed,
    calls: 0,
    log,
    clockMs: 0,
    epochMs: Date.UTC(2026, 9, 18, 0, 0, 0), // Date.now() at clock 0
    raf: new Map(),
    rafId: 0,
    frames: 0,
    // advance the scripted clock without running frames
    advance(ms) { this.clockMs += ms; return this.clockMs; },
    // advance the clock by ms and run every queued animation frame callback once
    step(ms = 1000 / 60) {
      this.clockMs += ms;
      const cbs = [...this.raf.values()];
      this.raf.clear();
      this.frames++;
      const t = this.clockMs;
      for (const cb of cbs) cb(t);
      return cbs.length;
    },
    pending() { return this.raf.size; },
  };
  const realRandom = Math.random;
  Math.random = function random() {
    a = (a + 0x6D2B79F5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    const v = ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    det.calls++;
    if (log) log.push(v);
    return v;
  };
  det.realRandom = realRandom;
  const realNow = performance.now.bind(performance);
  det.realNow = realNow; // wall clock, only for the harness's own timeouts and timings
  performance.now = () => det.clockMs;
  const realDateNow = Date.now;
  det.realDateNow = realDateNow;
  Date.now = () => det.epochMs + det.clockMs;
  const realRaf = window.requestAnimationFrame.bind(window);
  det.realRaf = realRaf;
  window.requestAnimationFrame = (cb) => { const id = ++det.rafId; det.raf.set(id, cb); return id; };
  window.cancelAnimationFrame = (id) => { det.raf.delete(id); };
  window.__det = det;
}
