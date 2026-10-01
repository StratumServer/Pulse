// The rain, film style, and the switch that stops it. Show pages only (it is part of assets/home.js).
//
// Independent columns, as in the film: each has its own speed and length, a near-white leading glyph,
// a trail that fades, and glyphs that change now and then. The glyph set is the one of the ModDB art
// (half-width katakana and the digits) in the colours of the tokens. Two depth layers: a far one,
// smaller and dimmer and slower, behind a near one. Nothing is a tile, so a wide screen never shows a
// repeat. The page canvas is fixed and created here, so it can never cause a layout shift; the two
// divider bands get a small field of their own. About 24 frames a second, paused while the tab is
// hidden, never started under prefers-reduced-motion or with the switch off (a still frame then).
//
// The pure parts (the generator, the backing scale, the layer scaling, the column stepping, the brightness
// ramp) are exported and tested in test/rain.test.mjs.
//
// Cost, measured and not guessed: a frame is tick, paint and a one pixel read back that forces the raster,
// 300 frames in headless Chrome 154 with software rasterisation on a Ryzen 9 9900X, a few thousand copies
// from a sprite sheet each. 1920 by 1080: 3.1 ms on average, 3.9 ms at the 95th percentile (the budget is
// 4 ms). 1280 by 720: 1.8 ms. 2560 by 1440: 4.0 ms. 3840 by 2160: 6.4 ms (the glyphs grow past 2.5
// megapixels, scaleLayers, so the count stays about 2700). 360 by 800 at 3x: 0.7 ms. 1920 by 1080 at 2x:
// 4.1 ms. A browser with a GPU rasterises canvases elsewhere, so this is a ceiling for this machine; a
// laptop or a phone may be three to six times slower, and the safety valve stops the rain, keeping the
// still frame, when the first 24 frames average more than 8 ms.
//
// The switch: one button in each [data-slot="rain"] (top bar, menu sheet, footer). Its label is its
// state (rain: on, rain: off, rain: still under reduced motion), so it carries no aria-pressed. The
// choice is remembered in localStorage (pulse.rain, behind try/catch). The one flag the stylesheet and
// the other modules read is body.is-motion: set only when motion is allowed AND this script runs, so
// that no animation ever exists without the switch that stops it.

import { CJK, FALLBACK_GLYPHS, GLYPHS, hasKatakana, mulberry32 } from './glyphs.js';

const FRAME_MS = 1000 / 24;                 // the cap: about 24 frames a second
const VALVE_MS = 8, VALVE_FRAMES = 24;      // a device that averages over 8 ms a frame at the start keeps the still frame
const MUTATE_PER_SECOND = 2.5;              // how often a column changes one glyph of its trail
const LEVELS = 8;                           // brightness levels of the atlas: 0 is the head, 7 the end of the trail
const LEVEL_RGBA = [[214, 255, 227, .95], [150, 255, 180, .9], [70, 235, 120, .8], [25, 205, 85, .7], [13, 170, 66, .58], [13, 140, 55, .45], [13, 125, 50, .32], [13, 115, 48, .18]];

/** The two depth layers of the page; speeds in rows a second, lengths in glyphs. */
export const LAYERS = [
  { cell: 15, glyph: 12, density: 0.55, speed: [4, 9], len: [6, 14], alpha: 0.5 },
  { cell: 22, glyph: 18, density: 0.75, speed: [8, 19], len: [10, 24], alpha: 0.9 },
];
/** One slow layer for the divider bands, which are four rows high. */
export const BAND_LAYERS = [{ cell: 20, glyph: 16, density: 0.9, speed: [2.5, 6], len: [2, 4], alpha: 1 }];

/** How many device pixels to a CSS pixel: crisp up to 2x, never more than about 4 megapixels of backing store, never below 1x. */
export function backingScale(ratio, width, height) {
  return Math.max(1, Math.min(ratio || 1, 2, Math.sqrt(4e6 / Math.max(1, width * height))));
}

/**
 * The cost of a frame follows the number of glyphs, so on a window of more than 2.5 megapixels of CSS pixels the
 * glyphs grow instead of multiplying: a 4K screen costs what a 1080p one does. The same layers come back at 2.5
 * megapixels and below.
 */
export function scaleLayers(layers, width, height, budget = 2.5e6) {
  const g = Math.max(1, Math.sqrt((width * height) / budget));
  return g === 1 ? layers : layers.map((l) => ({ ...l, cell: Math.round(l.cell * g), glyph: Math.round(l.glyph * g) }));
}

/** Brightness level of the glyph k places behind the head of a trail of `len` glyphs: 0 the head, then 1 (bright) to LEVELS - 1 (nearly gone). */
export function levelFor(k, len) {
  if (k === 0) return 0;
  return Math.min(LEVELS - 1, 1 + Math.floor(((k - 1) / Math.max(1, len - 1)) * (LEVELS - 1)));
}

const RAMPS = [];
/** The levels of a whole trail of `len` glyphs, computed once: a frame is nothing but copies. */
export const ramp = (len) => RAMPS[len] ?? (RAMPS[len] = Uint8Array.from({ length: len }, (_, k) => levelFor(k, len)));
const between = ([lo, hi], rnd) => lo + (hi - lo) * rnd();

/** The state of one layer: a struct of arrays, a slot per column. Heads are in rows and may be above the screen (negative). */
export function makeState(layer, width, height, rnd, glyphCount = GLYPHS.length) {
  const n = Math.ceil(width / layer.cell), rows = Math.ceil(height / layer.cell) + 1;
  const st = { n, rows, glyphCount, head: new Float32Array(n), speed: new Float32Array(n), len: new Uint8Array(n), wait: new Float32Array(n), cells: new Uint8Array(n * rows) };
  for (let c = 0; c < n; c++) {
    respawn(layer, st, c, rnd);
    st.head[c] = Math.floor(rnd() * (rows + st.len[c])) - st.len[c];          // spread over the screen, so the first frame is already full
    st.wait[c] = rnd() < layer.density ? 0 : rnd() * 4;
  }
  for (let i = 0; i < st.cells.length; i++) st.cells[i] = Math.floor(rnd() * glyphCount);
  return st;
}

function respawn(layer, st, c, rnd) {
  st.speed[c] = between(layer.speed, rnd);
  st.len[c] = Math.round(between(layer.len, rnd));
  st.head[c] = -1;
  st.wait[c] = rnd() < layer.density ? rnd() * 0.8 : 0.5 + rnd() * 4;        // some columns rest for a while, which is what keeps the rain uneven
}

/** Advances every column by dt seconds: the head falls, a new row gets a new glyph, one glyph of the trail may change, a column whose trail left the screen starts again. */
export function step(layer, st, dt, rnd) {
  const { n, rows, glyphCount } = st;
  for (let c = 0; c < n; c++) {
    if (st.wait[c] > 0) { st.wait[c] -= dt; continue; }
    const from = Math.floor(st.head[c]);
    st.head[c] += st.speed[c] * dt;
    const to = Math.floor(st.head[c]);
    for (let r = from + 1; r <= to; r++) if (r >= 0 && r < rows) st.cells[c * rows + r] = Math.floor(rnd() * glyphCount);
    if (rnd() < MUTATE_PER_SECOND * dt) {
      const r = to - Math.floor(rnd() * st.len[c]);
      if (r >= 0 && r < rows) st.cells[c * rows + r] = Math.floor(rnd() * glyphCount);
    }
    if (st.head[c] - st.len[c] > rows) respawn(layer, st, c, rnd);
  }
}

/** Every glyph of a layer for one frame, as [column, row, glyph, level]: what draw() paints, and what the tests read. */
export function visible(st) {
  const out = [];
  for (let c = 0; c < st.n; c++) {
    if (st.wait[c] > 0) continue;
    const h = Math.floor(st.head[c]);
    for (let k = 0; k < st.len[c]; k++) {
      const r = h - k;
      if (r < 0) break;
      if (r < st.rows) out.push([c, r, st.cells[c * st.rows + r], levelFor(k, st.len[c])]);
    }
  }
  return out;
}

/** Every glyph at every level, once: a sprite sheet, so a frame is nothing but copies. */
function buildAtlas(layer, K, glyphs) {
  const size = Math.round(layer.cell * K);
  const canvas = document.createElement('canvas');
  canvas.width = glyphs.length * size; canvas.height = LEVELS * size;
  const x = canvas.getContext('2d');
  x.font = `${Math.round(layer.glyph * K)}px ${CJK}`; x.textAlign = 'center'; x.textBaseline = 'middle';
  for (let l = 0; l < LEVELS; l++) {
    const [r, g, b, a] = LEVEL_RGBA[l];
    x.fillStyle = `rgba(${r},${g},${b},${a * layer.alpha})`;
    for (let i = 0; i < glyphs.length; i++) x.fillText(glyphs[i], i * size + size / 2, l * size + size / 2);
  }
  return { canvas, size };
}

/** A canvas and the layers that rain on it. Everything it draws is in device pixels, snapped to whole ones. `big`: the layers grow with the window (scaleLayers). */
class Field {
  constructor(canvas, layers, rnd, glyphs, big = false) {
    this.cv = canvas; this.ctx = canvas.getContext('2d'); this.layers = layers; this.rnd = rnd; this.glyphs = glyphs; this.big = big; this.K = 0; this.on = true;
  }
  /** Sizes the canvas to what the page gives it and starts every layer full. */
  resize(K) {
    const r = this.cv.getBoundingClientRect();
    this.K = K;
    this.cv.width = Math.max(1, Math.round(r.width * K)); this.cv.height = Math.max(1, Math.round(r.height * K));
    this.shown = this.big ? scaleLayers(this.layers, r.width, r.height) : this.layers;
    this.states = this.shown.map((l) => makeState(l, r.width, r.height, this.rnd, this.glyphs.length));
    this.atlases = this.shown.map((l) => buildAtlas(l, K, this.glyphs));
  }
  tick(dt) { this.shown.forEach((l, i) => step(l, this.states[i], dt, this.rnd)); }
  paint() {
    const x = this.ctx;
    x.clearRect(0, 0, this.cv.width, this.cv.height);
    for (let i = 0; i < this.shown.length; i++) {
      const { cell } = this.shown[i], st = this.states[i], { canvas, size } = this.atlases[i], pitch = cell * this.K;
      for (let c = 0; c < st.n; c++) {
        if (st.wait[c] > 0) continue;
        const h = Math.floor(st.head[c]), len = st.len[c], dx = Math.round(c * pitch), levels = ramp(len);
        for (let k = 0; k < len; k++) {
          const r = h - k;
          if (r < 0) break;
          if (r >= st.rows) continue;
          x.drawImage(canvas, st.cells[c * st.rows + r] * size, levels[k] * size, size, size, dx, Math.round(r * pitch), size, size);
        }
      }
    }
  }
}

// ---- the page: canvas, bands, switch --------------------------------------------------------------

/** Whether anything may move: the reader has not asked for less, and the switch is not off. */
export function motionOn() {
  return !matchMedia('(prefers-reduced-motion: reduce)').matches && document.documentElement.dataset.rain !== 'off';
}

const TEXT = { on: 'rain: on', off: 'rain: off', still: 'rain: still' };

export function initRain() {
  const root = document.documentElement;
  const reduce = matchMedia('(prefers-reduced-motion: reduce)');
  const rnd = mulberry32(42);
  let fields = null, glyphs = GLYPHS, raf = 0, next = 0, last = 0, spent = 0, counted = 0, slow = false, lastW = innerWidth, lastH = innerHeight, timer = 0;

  const ensure = () => {                                                    // the canvases exist from the first time the rain is on
    if (fields) return;
    if (!hasKatakana()) glyphs = FALLBACK_GLYPHS;
    const page = document.createElement('canvas');
    page.className = 'rain';
    page.setAttribute('aria-hidden', 'true');
    document.body.insertBefore(page, document.body.firstChild);
    fields = [new Field(page, LAYERS, rnd, glyphs, true)];
    for (const band of document.querySelectorAll('canvas[data-rain-band]')) fields.push(new Field(band, BAND_LAYERS, rnd, glyphs));
    rebuild();
    if ('IntersectionObserver' in window) {                                 // a band that is off screen costs nothing
      const io = new IntersectionObserver((entries) => entries.forEach((e) => { fields.find((f) => f.cv === e.target).on = e.isIntersecting; }));
      for (const f of fields.slice(1)) io.observe(f.cv);
    }
  };
  const rebuild = () => {
    const K = backingScale(devicePixelRatio, innerWidth, innerHeight);
    for (const f of fields) { f.resize(f === fields[0] ? K : Math.max(1, Math.min(devicePixelRatio || 1, 2))); f.paint(); }
  };
  const frame = (t) => {
    raf = requestAnimationFrame(frame);
    if (!next) next = t;
    if (t < next - 1) return;
    next += FRAME_MS;                                                       // on a 60 Hz screen frames come 33 and 50 ms apart: 24 a second
    if (t > next) next = t;                                                 // behind by more than a frame: no catching up in a burst
    const dt = Math.min((t - (last || t - FRAME_MS)) / 1000, 0.1);
    last = t;
    const t0 = performance.now();
    for (const f of fields) if (f.on) { f.tick(dt); f.paint(); }
    if (counted < VALVE_FRAMES) {                                           // the safety valve: a slow device keeps the still frame
      spent += performance.now() - t0;
      if (++counted === VALVE_FRAMES && spent / VALVE_FRAMES > VALVE_MS) { slow = true; apply(); }
    }
  };
  const start = () => { if (!raf && !slow && fields && !document.hidden && motionOn()) { last = 0; next = 0; raf = requestAnimationFrame(frame); } };
  const stop = () => { cancelAnimationFrame(raf); raf = 0; };

  const buttons = [];
  const label = () => (root.dataset.rain === 'off' ? TEXT.off : reduce.matches || slow ? TEXT.still : TEXT.on);
  const apply = () => {
    document.body.classList.toggle('is-motion', motionOn());
    if (root.dataset.rain !== 'off') ensure();
    stop(); start();
    for (const b of buttons) b.textContent = label();
    document.dispatchEvent(new Event('pulse-motion'));
  };
  for (const slot of document.querySelectorAll('[data-slot="rain"]')) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'btn btn--sm btn--quiet rain-toggle';
    b.title = 'The falling code behind the page: click to switch it on or off';
    b.addEventListener('click', () => {
      const off = root.dataset.rain !== 'off';
      if (off) root.dataset.rain = 'off'; else delete root.dataset.rain;
      try { localStorage.setItem('pulse.rain', off ? 'off' : 'on'); } catch (e) { /* private mode: the choice lasts as long as the page */ }
      apply();
    });
    slot.appendChild(b);
    buttons.push(b);
  }

  apply();
  document.addEventListener('visibilitychange', () => { if (document.hidden) stop(); else start(); });
  if (reduce.addEventListener) reduce.addEventListener('change', apply);
  addEventListener('resize', () => {
    clearTimeout(timer);
    timer = setTimeout(() => {
      if (!fields || (innerWidth === lastW && Math.abs(innerHeight - lastH) < 120)) return;   // a phone's address bar is not a resize
      lastW = innerWidth; lastH = innerHeight;
      rebuild();
    }, 150);
  });
}
