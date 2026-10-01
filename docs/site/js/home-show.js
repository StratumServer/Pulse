// The Home show: the opening sequence, the glitch of the wordmark, the reveals, the pill wash, the pill
// artwork and the white rabbit (the scroll rail of Home, and the rabbit of the 404 page). Show pages only (assets/home.js). Progressive enhancement: the page is complete
// without any of this, and none of it runs under prefers-reduced-motion or with the rain switch off
// (motionOn(), which rain.js defines). Nothing here moves the focus, shifts the layout or touches what
// the reader typed.
import { make } from './dom.js';
import { motionOn } from './rain.js';

/** The three lines of the opening, and what `wake up` prints when the opening may not play. */
export const OPENING = ['Wake up, admin...', 'Your server has a pulse...', 'Follow the white rabbit.'];
const KEY = 'pulse.opening';                                // sessionStorage: the opening plays once per browser session
const T = { start: 250, type: [36, 24, 24], pause: 280, hold: 200, out: 400 };   // 3.2 seconds, 3.5 at the most
const WASH_MS = 300;
const GLYPH_GAP = [8000, 15000];                           // the wordmark glitches every 8 to 15 seconds

/** When each character of the opening appears, and when the dissolve starts and ends: milliseconds from the start. */
export function openingTimeline(lines = OPENING, t = T) {
  const chars = [];
  let at = t.start;
  lines.forEach((line, i) => {
    for (let k = 1; k <= line.length; k++) { at += t.type[i]; chars.push({ at, line: i, count: k }); }
    at += i < lines.length - 1 ? t.pause : t.hold;
  });
  return { chars, dissolve: at, end: at + t.out };
}

let running = null;                                          // the opening that is playing, if any
const seen = () => { try { return sessionStorage.getItem(KEY) === '1'; } catch (e) { return false; } };
const mark = () => { try { sessionStorage.setItem(KEY, '1'); } catch (e) { /* private mode: it may play again */ } };

/**
 * Types the opening over a black overlay, then dissolves it. Once per session unless forced; never
 * under reduced motion, with the switch off, or when the address carries a hash. Any key, click, tap
 * or scroll ends it at once. Returns whether it started.
 */
export function playOpening({ force = false } = {}) {
  if (running) return true;
  if (!motionOn() || (!force && (location.hash || seen()))) return false;
  mark();
  const root = document.documentElement;
  root.classList.add('is-opening');
  const layer = make('div', 'opening');
  layer.setAttribute('aria-hidden', 'true');                 // decoration: the page underneath is already there for everyone
  const skip = make('button', 'opening__skip', 'skip');
  skip.type = 'button';
  skip.tabIndex = -1;                                        // not a tab stop, and aria-hidden: any key ends the opening for the keyboard
  // Every character is on the screen from the start, transparent: typing only colours the next one, so nothing moves (no layout shift).
  const text = make('div', 'opening__text'), spans = [], first = [];
  for (const line of OPENING) {
    const row = make('div', 'opening__line');
    first.push(spans.length);
    for (const ch of [...line, ' ']) { const c = make('span', 'opening__ch', ch); row.append(c); spans.push(c); }   // the last one is where the caret rests
    text.append(row);
  }
  layer.append(text, skip);
  document.body.append(layer);

  let caret = 0;
  const timeline = openingTimeline();
  const timers = [];
  const draw = ({ line: i, count }) => {
    const at = first[i] + count - 1;
    spans[at].classList.add('is-on');
    spans[caret].classList.remove('is-cur');
    spans[at + 1].classList.add('is-cur');
    caret = at + 1;
  };
  spans[0].classList.add('is-cur');
  const events = ['keydown', 'pointerdown', 'wheel', 'touchstart'];
  const end = (soon) => {
    if (!running) return;
    running = null;
    timers.forEach(clearTimeout);
    events.forEach((e) => removeEventListener(e, stop, true));
    layer.remove();
    root.classList.remove('is-opening');
    if (soon) live();
  };
  const stop = () => end(true);
  events.forEach((e) => addEventListener(e, stop, { capture: true, passive: true }));
  running = { end };
  for (const c of timeline.chars) timers.push(setTimeout(() => draw(c), c.at));
  timers.push(setTimeout(() => { layer.classList.add('is-out'); live(); }, timeline.dissolve));
  timers.push(setTimeout(() => end(false), timeline.end));
  return true;
}

// ---- the hero goes live: its line draws, the dot travels, the wordmark glitches ----------------------

let lived = false;
const rand = ([lo, hi]) => lo + Math.random() * (hi - lo);

function glitch() {
  for (const e of document.querySelectorAll('[data-glitch]')) {
    e.classList.remove('is-glitch');
    void e.offsetWidth;                                      // restart the animation if one is still running
    e.classList.add('is-glitch');
  }
}

function live() {
  if (!motionOn() || lived) return;
  lived = true;
  document.dispatchEvent(new Event('pulse-live'));
  for (const hero of document.querySelectorAll('[data-hero]')) hero.classList.add('is-live');
  for (const rail of document.querySelectorAll('.rail')) { rail.classList.remove('is-away'); once(rail, 'is-in'); }   // the rabbit hops in and takes its place
  const again = () => { setTimeout(() => { if (motionOn() && !document.hidden) glitch(); again(); }, rand(GLYPH_GAP)); };
  setTimeout(glitch, 350);
  again();
}

// ---- reveals: a panel fades and rises in once, as it comes into view ----------------------------------

function initReveals() {
  if (!motionOn() || !('IntersectionObserver' in window)) return;
  const io = new IntersectionObserver((entries) => entries.forEach((e) => {
    if (e.isIntersecting) { e.target.classList.add('is-in'); io.unobserve(e.target); }
  }), { rootMargin: '0px 0px -6% 0px' });
  for (const e of document.querySelectorAll('[data-reveal]')) {
    if (e.getBoundingClientRect().top < innerHeight) continue;   // already on screen: leave it alone, so nothing flashes
    e.classList.add('rv');
    io.observe(e);
  }
}

// ---- the pills: a colour wash, then the link --------------------------------------------------------

/** Plays the wash of the pill (a card, or the terminal) and follows `href`; without motion it is only the link. */
function wash(which, href) {
  if (!motionOn()) { location.assign(href); return; }
  const w = make('div', `wash wash--${which === 'red' ? 'push' : 'pull'}`);
  w.setAttribute('aria-hidden', 'true');
  document.body.append(w);
  setTimeout(() => location.assign(href), WASH_MS);
}

/** `take the blue pill` in the terminal does what the card does. */
export function goPill(which) {
  const a = document.querySelector(`[data-go="${which === 'red' ? 'red' : 'blue'}"]`);
  wash(which, a ? a.href : `${document.documentElement.dataset.root || ''}getting-started/index.html?pill=${which}`);
}

function initPills() {
  document.addEventListener('click', (e) => {
    const a = e.target.closest ? e.target.closest('a[data-go]') : null;
    if (!a || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey || !motionOn()) return;
    e.preventDefault();                                      // the card is a plain link first: here it is a link with a wash
    wash(a.dataset.go, a.href);
  });
  addEventListener('pageshow', (e) => { if (e.persisted) document.querySelectorAll('.wash').forEach((w) => w.remove()); });   // Back must not show the wash
}

// ---- the pill artwork: its animated file is a <source> for a reader who allows motion; the rain switch drops it ---------

function syncPillArt() {
  const source = document.querySelector('source[data-anim]');
  if (source) source.media = motionOn() ? '(prefers-reduced-motion: no-preference)' : 'not all';
}

// ---- the white rabbit ---------------------------------------------------------------------------------------
// The rail of Home reflects the scroll position and nothing else: a rabbit on a trail, a hole at its end. Where
// CSS can drive an animation from the scroll position (animation-timeline) it does, off the main thread; elsewhere,
// and under reduced motion (which switches every CSS animation off), this sets --p, the progress, on the rail.
// Hop frames, the ear twitch, the flip towards the direction of travel and the entrance are motion: they only run
// while motionOn(). The rabbit of the 404 page twitches and hops when touched, and is a link with a name.

const SCROLL_IDLE_MS = 150;
const once = (el, className) => { el.classList.remove(className); void el.offsetWidth; el.classList.add(className); };   // restart a one-shot

/** The page rabbit hops once (`follow the white rabbit` in the terminal). */
export function hopRabbit() {
  if (!motionOn()) return;
  for (const r of document.querySelectorAll('[data-rabbit]')) if (!r.matches('.is-in, .is-moving')) once(r, 'is-hop');
}

function flick(r) {                                           // two quick flicks of the ear
  const on = (ms, then) => { r.classList.add('is-twitch'); setTimeout(() => { r.classList.remove('is-twitch'); then && setTimeout(then, 130); }, ms); };
  on(110, () => on(110));
}

function trackScroll(rail) {
  const driven = () => CSS.supports('animation-timeline: scroll()') && motionOn();   // reduced motion and the rain switch turn the CSS animation off
  let last = scrollY, queued = false, idle = 0;
  const place = () => {
    queued = false;
    const y = scrollY, max = document.documentElement.scrollHeight - innerHeight;
    if (!driven()) rail.style.setProperty('--p', max > 0 ? Math.min(1, Math.max(0, y / max)) : 0);
    const dy = y - last;
    last = y;
    if (dy && motionOn() && !rail.classList.contains('is-in')) {
      rail.classList.add('is-moving');
      rail.classList.toggle('is-up', dy < 0);
      clearTimeout(idle);
      idle = setTimeout(() => rail.classList.remove('is-moving'), SCROLL_IDLE_MS);
    }
  };
  const soon = () => { if (!queued) { queued = true; requestAnimationFrame(place); } };
  addEventListener('scroll', soon, { passive: true });
  addEventListener('resize', soon);
  place();
  return place;                                                // run again when the motion state changes: the position must not wait for the next scroll
}

function initRabbit() {
  const roots = [...document.querySelectorAll('[data-rabbit]')];
  if (!roots.length) return;
  for (const r of roots) {
    r.addEventListener('animationend', (e) => {
      if (e.animationName === 'rabbit-arc') r.classList.remove('is-hop');
      if (e.animationName === 'rail-in') r.classList.remove('is-in');
    });
    (r.querySelector('.rail__go') || r).addEventListener('pointerenter', (e) => { if (e.pointerType === 'mouse' && motionOn() && !r.matches('.is-in, .is-moving, .is-hop')) once(r, 'is-hop'); });
    const idle = () => setTimeout(() => {                    // an ear twitches every few seconds while the rabbit sits
      if (motionOn() && !document.hidden && !r.matches('.is-in, .is-moving, .is-hop')) flick(r);
      idle();
    }, 3000 + Math.random() * 4000);
    idle();
  }
  const rail = document.querySelector('.rail');
  let follow = () => {};
  if (rail) {
    if (motionOn()) rail.classList.add('is-away');            // out of sight until the show begins (live)
    follow = trackScroll(rail);
  }
  document.addEventListener('pulse-motion', () => {         // motion off: the rabbit sits still and only the position follows
    if (!motionOn()) for (const r of roots) r.classList.remove('is-away', 'is-in', 'is-moving', 'is-hop', 'is-twitch');
    follow();
  });
}

export function initHomeShow() {
  syncPillArt();
  initPills();
  initReveals();
  initRabbit();
  document.addEventListener('pulse-motion', () => { syncPillArt(); live(); });
  const home = document.documentElement.dataset.page === 'home';
  if (!(home && playOpening())) live();
}
