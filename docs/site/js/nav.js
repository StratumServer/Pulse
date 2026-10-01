// The menu (Escape, a click outside, closing on a link), the contents spy, the print hook and, on the
// calm pages, one still frame of rain in the gutters beside the documentation slab. Nothing in here
// moves by itself: the calm pages have no ambient animation, so the still is drawn once and never
// ticks (the live rain is rain.js, which only the show pages download).

import { CJK, FALLBACK_GLYPHS, GLYPHS, hasKatakana, mulberry32 } from './glyphs.js';

const CELL = 22, GLYPH_PX = 17, SEED = 42;

/**
 * The glyphs of one frame: columns of CELL pixels, the ones that overlap the slab (the pixels from `left`
 * to `right`) left out, each a streak with a head and a fading trail. A column has a generator of its
 * own, so the same column looks the same whatever the width of the window. Pure: [{ x, y, glyph, alpha }].
 */
export function stillGlyphs(width, height, left, right, seed) {
  const out = [], rows = Math.ceil(height / CELL);
  for (let c = 0; c * CELL < width; c++) {
    const x = c * CELL;
    if (x + CELL > left && x < right) continue;
    const rnd = mulberry32(seed * 1000 + c);
    if (rnd() > 0.72) continue;
    const head = Math.floor(rnd() * (rows + 6)), len = 5 + Math.floor(rnd() * 17), depth = 0.45 + 0.55 * rnd();
    for (let k = 0; k < len; k++) {
      const row = head - k;
      if (row < 0 || row >= rows) continue;
      const fade = (1 - k / len) ** 1.6;
      out.push({ x, y: row * CELL, glyph: GLYPHS[Math.floor(rnd() * GLYPHS.length)], alpha: +(0.5 * depth * fade + 0.04).toFixed(3) });
    }
  }
  return out;
}

function initStill() {
  const root = document.documentElement, shell = document.querySelector('.shell');
  if (root.dataset.regime !== 'calm' || !shell) return;
  let canvas = null, timer = 0, katakana = null;
  const paint = () => {
    const w = innerWidth, h = innerHeight, gutter = shell.getBoundingClientRect().left;
    if (gutter < 40) { canvas?.remove(); canvas = null; return; }     // less than one column on a side: nothing to show
    if (!canvas) {
      canvas = document.createElement('canvas');
      canvas.className = 'rain';
      canvas.setAttribute('aria-hidden', 'true');
      document.body.insertBefore(canvas, document.body.firstChild);
    }
    const k = Math.max(1, Math.min(devicePixelRatio || 1, 2, Math.sqrt(4e6 / (w * h))));
    canvas.width = Math.round(w * k); canvas.height = Math.round(h * k);
    const x = canvas.getContext('2d');
    if (!x) return;
    if (katakana === null) katakana = hasKatakana();
    x.scale(k, k); x.font = `${GLYPH_PX}px ${CJK}`; x.textAlign = 'center'; x.textBaseline = 'middle';
    for (const g of stillGlyphs(w, h, gutter, w - gutter, SEED)) {
      x.fillStyle = `rgba(13,130,52,${g.alpha})`;
      x.fillText(katakana ? g.glyph : FALLBACK_GLYPHS[GLYPHS.indexOf(g.glyph) % FALLBACK_GLYPHS.length], g.x + CELL / 2, g.y + CELL / 2);
    }
  };
  paint();
  addEventListener('resize', () => { clearTimeout(timer); timer = setTimeout(paint, 200); });
}

function initMenu() {
  const menu = document.querySelector('.nav__menu');
  if (!menu) return;
  const close = (focus) => { menu.open = false; if (focus) menu.querySelector('summary').focus(); };
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && menu.open) close(true); });
  document.addEventListener('click', (e) => { if (menu.open && !menu.contains(e.target)) close(false); });
  menu.addEventListener('click', (e) => { if (e.target.closest('.nav__sheet a')) close(false); });
}

/**
 * The contents entry of the section being read: the last heading above a line a third of the way down the screen.
 * Recomputed once a frame while the page scrolls (an observer would miss a jump: End, a dragged scrollbar, a
 * link to a far section can carry a heading across the line between two frames).
 */
function initSpy() {
  const items = [...document.querySelectorAll('.toc a[href^="#"]')]
    .map((a) => ({ a, h: document.getElementById(decodeURIComponent(a.hash.slice(1))) })).filter((x) => x.h);
  if (!items.length) return;
  let current = null, queued = false;
  const update = () => {
    queued = false;
    let hit = null;
    for (const it of items) {
      if (!it.h.getClientRects().length) continue;                   // folded away: not on screen, not current
      if (!hit || it.h.getBoundingClientRect().top <= innerHeight * 0.3) hit = it; else break;
    }
    if (!hit || hit === current) return;
    current?.a.removeAttribute('aria-current');
    hit.a.setAttribute('aria-current', 'location');
    current = hit;
  };
  const soon = () => { if (!queued) { queued = true; requestAnimationFrame(update); } };
  addEventListener('scroll', soon, { passive: true });
  addEventListener('resize', soon);
  update();
}

/** Folded content is printed, then folded again. */
function initPrint() {
  let opened = [];
  addEventListener('beforeprint', () => { opened = [...document.querySelectorAll('details:not([open])')]; opened.forEach((d) => { d.open = true; }); });
  addEventListener('afterprint', () => { opened.forEach((d) => { d.open = false; }); opened = []; });
}

export function initNav() {
  initMenu();
  initSpy();
  initPrint();
  initStill();
}
