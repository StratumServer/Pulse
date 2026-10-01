// The pure parts of the rain (js/rain.js) and of the still frame of the calm pages (js/nav.js): the
// seeded generator, the backing scale, the brightness ramp, the column stepping. The canvas code is
// looked at in a browser; what decides how the rain behaves is here, with a seed, so it is repeatable.
import assert from 'node:assert/strict';
import test from 'node:test';
import { FALLBACK_GLYPHS, GLYPHS, KATAKANA, mulberry32 } from '../js/glyphs.js';
import { BAND_LAYERS, LAYERS, backingScale, levelFor, makeState, ramp, scaleLayers, step, visible } from '../js/rain.js';
import { stillGlyphs } from '../js/nav.js';

test('the generator is seeded: the same rain on every load, a different one for another seed', () => {
  const a = mulberry32(42), b = mulberry32(42), c = mulberry32(43);
  const run = (g) => Array.from({ length: 50 }, g);
  assert.deepEqual(run(a), run(b));
  assert.notDeepEqual(run(mulberry32(42)), run(c));
  assert.ok(run(mulberry32(7)).every((v) => v >= 0 && v < 1));
});

test('the glyphs are the ones of the ModDB art: half-width katakana and the digits, 66 of them', () => {
  assert.equal(KATAKANA.length, 56);
  assert.equal(KATAKANA[0], 'ｦ');
  assert.equal(KATAKANA.at(-1), 'ﾝ');
  assert.equal(GLYPHS.length, 66);
  assert.equal(GLYPHS.at(-1), '9');
  assert.ok(FALLBACK_GLYPHS.every((g) => g.charCodeAt(0) < 0x80), 'without a katakana font the rain falls in plain characters, never in empty boxes');
});

test('the backing store is crisp up to 2x, never below 1x, and capped near four megapixels', () => {
  assert.equal(backingScale(1, 1920, 1080), 1);
  assert.equal(backingScale(undefined, 1920, 1080), 1);
  assert.ok(Math.abs(backingScale(2, 1920, 1080) - Math.sqrt(4e6 / (1920 * 1080))) < 1e-9, 'a 4K backing store at 1080p, not more');
  assert.equal(backingScale(3, 360, 800), 2, 'a phone is capped at 2x');
  assert.equal(backingScale(2, 5120, 1440), 1, 'an ultrawide is never drawn below its own pixels');
  assert.equal(backingScale(2, 800, 600), 2);
});

test('the brightness ramp: a head, then bright down to nearly gone, always inside the atlas', () => {
  assert.equal(levelFor(0, 12), 0);
  const ramp = Array.from({ length: 12 }, (_, k) => levelFor(k, 12));
  assert.deepEqual(ramp, [...ramp].sort((x, y) => x - y), 'it only gets dimmer down the trail');
  assert.equal(ramp[1], 1);
  assert.equal(ramp.at(-1), 7);
  assert.equal(levelFor(0, 1), 0, 'a trail of one is only a head');
  for (const len of [2, 3, 10, 26]) for (let k = 0; k < len; k++) assert.ok(levelFor(k, len) >= 0 && levelFor(k, len) <= 7);
});

test('the glyph count stops growing at 2.5 megapixels: a 4K window gets bigger glyphs, not more', () => {
  assert.equal(scaleLayers(LAYERS, 1920, 1080), LAYERS, 'a 1080p window is left as it is');
  assert.equal(scaleLayers(LAYERS, 1280, 720), LAYERS);
  const count = (layers, w, h) => layers.reduce((n, l) => n + Math.ceil(w / l.cell) * Math.ceil(h / l.cell), 0);
  const big = scaleLayers(LAYERS, 3840, 2160);
  assert.ok(big[1].cell > LAYERS[1].cell * 1.5 && big[1].glyph > LAYERS[1].glyph * 1.5, 'the glyphs grow');
  const reference = count(LAYERS, 1936, 1291);                                  // 2.5 megapixels
  assert.ok(Math.abs(count(big, 3840, 2160) - reference) / reference < 0.1, 'and the count stays where it was at the limit');
  assert.equal(LAYERS[1].cell, 22, 'the layers themselves are not touched');
  assert.equal(scaleLayers(BAND_LAYERS, 960, 80), BAND_LAYERS);
});

test('the ramp of a trail is computed once and agrees with levelFor', () => {
  assert.equal(ramp(12), ramp(12));
  assert.deepEqual([...ramp(12)], Array.from({ length: 12 }, (_, k) => levelFor(k, 12)));
});

const layer = LAYERS[1];

test('a layer starts full, with columns that have a speed and a length of their own', () => {
  const st = makeState(layer, 1280, 720, mulberry32(1));
  assert.equal(st.n, Math.ceil(1280 / layer.cell));
  assert.equal(st.rows, Math.ceil(720 / layer.cell) + 1);
  assert.equal(st.cells.length, st.n * st.rows);
  for (let c = 0; c < st.n; c++) {
    assert.ok(st.speed[c] >= layer.speed[0] && st.speed[c] <= layer.speed[1], `speed of column ${c}`);
    assert.ok(st.len[c] >= layer.len[0] && st.len[c] <= layer.len[1], `length of column ${c}`);
  }
  assert.ok(new Set(st.len).size > 4 && new Set(st.speed).size > st.n / 2, 'no two columns are a copy of one another');
  assert.ok(visible(st).length > 100, 'the first frame already has rain in it');
  assert.deepEqual(makeState(layer, 1280, 720, mulberry32(1)).head, st.head, 'seeded');
});

test('a column falls at its own speed, gives each new row a new glyph, and starts again when its trail has left', () => {
  const rnd = mulberry32(5);
  const st = makeState(layer, 220, 220, rnd);
  const c = 3;
  st.wait[c] = 0; st.head[c] = 2; st.speed[c] = 10; st.len[c] = 5;
  st.cells.fill(255, c * st.rows, (c + 1) * st.rows);                      // nothing is a glyph yet
  step(layer, st, 0.25, rnd);
  assert.equal(st.head[c], 4.5, 'two and a half rows in a quarter of a second at ten rows a second');
  assert.ok(st.cells[c * st.rows + 3] < GLYPHS.length && st.cells[c * st.rows + 4] < GLYPHS.length, 'the head entered rows 3 and 4: they hold glyphs now');
  assert.equal(st.cells[c * st.rows + 9], 255, 'rows the head has not reached are untouched');
  st.head[c] = st.rows + st.len[c];                                          // the whole trail is about to leave the screen
  step(layer, st, 0.1, rnd);
  assert.equal(st.head[c], -1, 'back above the screen');
  assert.ok(st.speed[c] >= layer.speed[0] && st.len[c] >= layer.len[0], 'with a fresh speed and length');
});

test('a column that is resting does not move', () => {
  const rnd = mulberry32(9);
  const st = makeState(layer, 220, 220, rnd);
  st.wait[0] = 2; st.head[0] = 4;
  step(layer, st, 0.5, rnd);
  assert.equal(st.head[0], 4);
  assert.equal(st.wait[0], 1.5);
  assert.ok(!visible(st).some(([col]) => col === 0));
});

test('two minutes of rain at 24 frames a second stay inside the arrays and never run dry', () => {
  for (const [name, l, w, h] of [['page', LAYERS[0], 1920, 1080], ['page', LAYERS[1], 1920, 1080], ['band', BAND_LAYERS[0], 960, 80]]) {
    const rnd = mulberry32(2024), st = makeState(l, w, h, rnd);
    let least = Infinity;
    for (let i = 0; i < 24 * 120; i++) {
      step(l, st, 1 / 24, rnd);
      const seen = visible(st);
      least = Math.min(least, seen.length);
      for (const [c, r, g, level] of seen) {
        assert.ok(c >= 0 && c < st.n && r >= 0 && r < st.rows && g >= 0 && g < GLYPHS.length && level >= 0 && level <= 7, `${name}: ${[c, r, g, level]}`);
      }
    }
    assert.ok(least > 0, `${name}: the rain never empties`);
  }
});

test('the divider band is a slow, short rain: four rows, trails of two to four', () => {
  const [b] = BAND_LAYERS;
  assert.equal(Math.ceil(80 / b.cell), 4);
  assert.ok(b.len[1] <= 4 && b.speed[1] < LAYERS[1].speed[0]);
});

test('the still of the calm pages leaves the slab alone and looks the same in a window of any width', () => {
  const frame = stillGlyphs(1920, 1080, 336, 1584, 42);
  assert.ok(frame.length > 50, 'there is rain in the gutters');
  assert.ok(frame.every((g) => g.x + 22 <= 336 || g.x >= 1584), 'nothing is drawn under the slab');
  assert.ok(frame.every((g) => g.alpha > 0 && g.alpha <= 0.6), 'dim: it is a texture, not a show');
  assert.deepEqual(stillGlyphs(1920, 1080, 336, 1584, 42), frame, 'seeded');
  const column = (list) => JSON.stringify(list.filter((g) => g.x === 22));
  assert.ok(column(frame).length > 10, 'the second column has a streak');
  assert.equal(column(stillGlyphs(2560, 1080, 700, 1860, 42)), column(frame), 'a column is the same column whatever the width of the window');
  const sides = stillGlyphs(1920, 1080, 600, 1320, 42);
  assert.ok(sides.some((g) => g.x < 600) && sides.some((g) => g.x >= 1320), 'both gutters get rain');
});
