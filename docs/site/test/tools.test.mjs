// The reference tools: the pure parts of filter.js, config.js and search.js, the source-level privacy test
// (nothing typed into the builder, the filter or the search is stored, logged or sent), and what the page
// modules of Metrics, Install and configuration, Dashboard and Alerts write. Counts that grow with the
// documents (families, panels, rules) are asserted as invariants against ctx.data, never as numbers: a
// docs-only pull request must not turn red because a metric was added.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { buildSite } from '../build.mjs';
import { metricRef } from '../lib/pages/metrics.mjs';
import { loadSources } from '../lib/sources.mjs';
import { buildConfig, changedKeys, changeLine, checkValue } from '../js/config.js';
import { countText, facetValues, matches } from '../js/filter.js';
import { search, snippet, statusText } from '../js/search.js';

const real = loadSources();
const altered = (path, edit) => loadSources({ text: (p) => (p === path ? edit(real.text(p)) : real.text(p)) });
const built = await buildSite({ sources: real, offline: true });
const { data } = built;
const html = (path) => built.files.get(path);
const index = JSON.parse(/^window\.PULSE_SEARCH = (.*);\n$/s.exec(html('assets/search-index.js'))[1]);
const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
/** The element that starts at the tag carrying `id`, up to its closing tag (cards do not nest their own tag). */
const element = (page, tag, id) => {
  const from = html(page).indexOf(`<${tag} class="`, html(page).indexOf(`id="${id}"`) - 60);
  return html(page).slice(from, html(page).indexOf(`</${tag}>`, from) + tag.length + 3);
};

// ------------------------------------------------------------------ search
test('search ranks a title above any amount of text, and a config key whose name is the query comes first', () => {
  const first = (q) => search(index, q)[0];
  assert.equal(first('bind').t, 'Bind');
  assert.equal(first('bind').c, 'Install and configuration');
  assert.equal(first('bind address').t, 'A word on the bind address');
  assert.equal(first('degraded').t, 'Degraded mode');
  assert.equal(first('otlp export').t, 'OTLP export');
  const family = data.metrics[0].name, rule = data.rules[0], row = data.dashboard.rows[0], panel = `${row.title}: ${row.panels[0].title}`;
  assert.equal(first(family).t, family, 'a family by its name');
  assert.equal(first(rule.alert).t, `${rule.alert} (${rule.severity})`, 'a rule by its name: the first of the pair');
  assert.ok(search(index, panel).some((e) => e.t === panel), 'a panel by row and title');
  assert.deepEqual(search(index, 'zzzzqq'), []);
  assert.deepEqual(search(index, '   '), []);
  assert.equal(search(index, 'the').length, 8, 'never more than the limit');
  assert.equal(search(index, 'tick').every((e) => (e.t + e.x).toLowerCase().includes('tick')), true, 'every entry holds the term');
  assert.equal(search(index, 'bind zzzzqq').length, 0, 'every term must be somewhere in the entry');
});

test('a result shows the words around the first match, and the status never repeats the query', () => {
  const text = 'The default binds loopback, which means only something running on the same host can scrape it. That default is deliberate.';
  assert.deepEqual(snippet(text, 'loopback'), ['The default binds ', 'loopback', ', which means only something running on the same host can sc ...']);
  assert.deepEqual(snippet('short text', 'nothing'), ['short text', '', '']);
  assert.deepEqual(snippet('one two three', 'TWO', 4), ['one ', 'two', ' thr ...']);
  assert.deepEqual(snippet('one two three four five', 'four', 4), ['... ree ', 'four', ' fiv ...'], 'a match far in is cut on both sides');
  assert.equal(statusText(0), 'No match. Follow the white rabbit: try bind, attribution or otlp.');
  assert.equal(statusText(1), '1 result');
  assert.equal(statusText(3), '3 results');
  assert.equal(statusText(9), 'The first 8 results.', 'one more than the limit was found');
});

test('every entry of the index that a tool page adds is an item of that page: a family, a key, a rule, a panel', () => {
  const on = (page) => index.filter((e) => e.u.startsWith(page + '/'));
  for (const m of data.metrics) assert.ok(on('metrics').some((e) => e.u === `metrics/index.html#${m.siblings[0]}` && e.t.includes(m.name) && e.x.includes(m.name)), m.name);
  for (const { rows } of data.config) for (const r of rows) assert.ok(on('configuration').some((e) => e.u === `configuration/index.html#${r.id}` && e.t === r.key), r.id);
  for (const r of data.rules) assert.ok(on('alerts').some((e) => e.u === `alerts/index.html#${r.id}` && e.t === `${r.alert} (${r.severity})` && e.x.includes(r.expr)), r.id);
  for (const row of data.dashboard.rows) for (const p of row.panels) assert.ok(on('dashboard').some((e) => e.u === `dashboard/index.html#${p.anchor}` && e.t === `${row.title}: ${p.title}`), p.anchor);
  assert.ok(!on('metrics').concat(on('configuration'), on('alerts'), on('dashboard')).some((e) => /\$\{|undefined|\[object/.test(e.t + e.x)), 'no template left over in the index');
});

// ------------------------------------------------------------------ filter
test('the filter shows an item that holds every word typed and, in each facet with a chip pressed, one of the pressed values', () => {
  const item = { text: 'pulse_server_tick_busy_seconds (gauge): the average time one tick spent working', facets: { group: 'engine', type: 'gauge' } };
  const st = (q, group = [], type = []) => ({ q, pressed: { group, type } });
  assert.equal(matches(item, st('')), true);
  assert.equal(matches(item, st('Tick BUSY')), true);
  assert.equal(matches(item, st('players')), false);
  assert.equal(matches(item, st('tick players')), false, 'every word');
  assert.equal(matches(item, st('', ['engine', 'attribution'])), true, 'chips of one facet are alternatives');
  assert.equal(matches(item, st('', ['public'])), false);
  assert.equal(matches(item, st('', ['engine'], ['counter'])), false, 'facets combine');
  assert.equal(matches(item, st('tick', ['engine'], ['gauge'])), true);
  assert.equal(matches({ text: 'x', facets: {} }, st('', ['engine'])), false, 'an item with no value for a pressed facet is not shown');
});

test('chips come from the values the items carry, in order of first appearance, and the count reads "n of m shown"', () => {
  const items = [{ facets: { group: 'public', type: 'counter' } }, { facets: { group: 'engine', type: 'gauge' } }, { facets: { group: 'public', type: 'gauge' } }, { facets: { group: 'attribution' } }];
  assert.deepEqual(facetValues(items, ['group', 'type']), { group: ['public', 'engine', 'attribution'], type: ['counter', 'gauge'] });
  assert.deepEqual(facetValues(items, []), {});
  assert.equal(countText(12, 24), '12 of 24 shown');
});

// ------------------------------------------------------------------ config builder
const printed = (name) => real.slice(name).find((t) => t.type === 'code' && t.lang === 'json').text;
const rowsOf = (file) => data.config.find((f) => f.file === file).rows.map((r) => ({ ...r }));

test('with no value typed the builder gives the default file the README prints, byte for byte', () => {
  assert.equal(buildConfig(rowsOf('ModConfig/pulse.json'), {}), printed('install'));
  assert.equal(buildConfig(rowsOf('ModConfig/pulse-otlp.json'), {}), printed('otlp'));
  for (const { rows } of data.config) assert.equal(changeLine(rows, {}), 'This is the default file.');
});

test('every limit the README states for a key is the limit the builder reports, and nothing else is checked', () => {
  const limited = data.config.flatMap(({ rows }) => rows).filter((r) => r.min !== undefined || r.max !== undefined);
  assert.ok(limited.length > 0, 'keys with a limit exist, and the build pins which ones');
  for (const r of limited) {
    if (r.min !== undefined) {
      assert.equal(checkValue(r, String(r.min)).note, null, `${r.key} at its floor`);
      assert.equal(checkValue(r, String(r.min - 1)).note, 'out-of-range', `${r.key} under its floor`);
    }
    if (r.max !== undefined) {
      assert.equal(checkValue(r, String(r.max)).note, null, `${r.key} at its cap`);
      assert.equal(checkValue(r, String(r.max + 1)).note, 'out-of-range', `${r.key} over its cap`);
    }
  }
  // a key with no limit stated is not checked, and a closed list is a select that cannot hold anything else, so it is never reported
  for (const r of data.config.flatMap(({ rows }) => rows).filter((x) => x.type === 'number' && x.min === undefined && x.max === undefined)) {
    assert.deepEqual(checkValue(r, '-5'), { value: -5, note: null }, `${r.key}: the README states no range`);
    assert.deepEqual(checkValue(r, '999999'), { value: 999999, note: null }, r.key);
  }
  for (const r of data.config.flatMap(({ rows }) => rows).filter((x) => x.options)) {
    for (const o of r.options) assert.equal(checkValue(r, o).note, null, `${r.key}: ${o}`);
  }
});

test('a changed value lands in the file where the table puts the key, dotted keys nested, and the line says how each takes effect', () => {
  const rows = rowsOf('ModConfig/pulse.json');
  const values = { Bind: '0.0.0.0', Port: 9500, 'Attribution.Enabled': true };
  const file = JSON.parse(buildConfig(rows, values));
  assert.deepEqual(file, { Enabled: true, Bind: '0.0.0.0', Port: 9500, RuntimeMetrics: true, ChunksRefreshSeconds: 30, Attribution: { Enabled: true, BurstTicks: 10, IntervalSeconds: 10 } });
  assert.deepEqual(Object.keys(file), ['Enabled', 'Bind', 'Port', 'RuntimeMetrics', 'ChunksRefreshSeconds', 'Attribution'], 'table order');
  assert.equal(changeLine(rows, values), 'Changed from the defaults: Bind (Restart), Port (Restart), Attribution.Enabled (Live, via /pulse reload).');
  assert.equal(changeLine(rows, { Port: 9464 }), 'This is the default file.', 'a value equal to the default is not a change');
});

test('the builder: types, notes and nothing corrected', () => {
  const rows = [
    { key: 'Enabled', default: true, type: 'boolean' }, { key: 'Bind', default: '127.0.0.1', type: 'string' }, { key: 'Port', default: 9464, type: 'number' },
    { key: 'Attribution.Enabled', default: false, type: 'boolean', live: true }, { key: 'Attribution.BurstTicks', default: 10, type: 'number', min: 1, max: 300, live: true },
    { key: 'Protocol', default: 'http/protobuf', type: 'string', options: ['http/protobuf', 'grpc'] }, { key: 'Endpoint', default: 'http://localhost:4318', type: 'string' },
    { key: 'Headers', default: {}, type: 'object' },
  ];
  const row = (k) => rows.find((r) => r.key === k);
  assert.deepEqual(JSON.parse(buildConfig(rows, { Port: 9500, 'Attribution.Enabled': true })).Attribution, { Enabled: true, BurstTicks: 10 });
  assert.equal(buildConfig(rows.slice(0, 2), {}), '{\n  "Enabled": true,\n  "Bind": "127.0.0.1"\n}');
  assert.deepEqual(checkValue(row('Port'), ' 9500 '), { value: 9500, note: null });
  assert.deepEqual(checkValue(row('Port'), '95.5'), { value: 9464, note: 'not-a-whole-number' });
  assert.deepEqual(checkValue(row('Port'), ''), { value: 9464, note: 'not-a-whole-number' });
  assert.deepEqual(checkValue(row('Port'), '1e3'), { value: 9464, note: 'not-a-whole-number' });
  assert.deepEqual(checkValue(row('Attribution.BurstTicks'), '301'), { value: 301, note: 'out-of-range' });
  assert.deepEqual(checkValue(row('Attribution.BurstTicks'), '0'), { value: 0, note: 'out-of-range' });
  assert.deepEqual(checkValue(row('Attribution.BurstTicks'), '300'), { value: 300, note: null });
  assert.deepEqual(checkValue(row('Bind'), '0.0.0.0'), { value: '0.0.0.0', note: 'bind-not-loopback' });
  for (const loopback of ['127.0.0.1', 'localhost', '::1', '[::1]', ' localhost ']) assert.equal(checkValue(row('Bind'), loopback).note, null, loopback);
  assert.deepEqual(checkValue(row('Protocol'), 'grpc'), { value: 'grpc', note: null });
  assert.deepEqual(checkValue(row('Endpoint'), 'otlp.example'), { value: 'otlp.example', note: 'endpoint-not-a-url' });
  assert.deepEqual(checkValue(row('Endpoint'), 'ftp://x.example'), { value: 'ftp://x.example', note: 'endpoint-not-a-url' });
  assert.deepEqual(checkValue(row('Endpoint'), 'https://x/otlp/v1/metrics'), { value: 'https://x/otlp/v1/metrics', note: 'endpoint-has-signal-path' });
  assert.equal(checkValue(row('Endpoint'), 'https://x.example/otlp').note, null);
  assert.deepEqual(checkValue(row('Headers'), [[' Authorization ', 'Basic abc='], ['', 'ignored']]), { value: { Authorization: 'Basic abc=' }, note: null });
  assert.equal(checkValue(row('Headers'), [['a', 'x,y']]).note, 'comma-in-header-value');
  assert.equal(checkValue(row('Headers'), [['X-A', '1'], [' X-A ', '2']]).note, 'duplicate-header-name');
  assert.equal(checkValue(row('Headers'), [['X-A', '1'], ['x-a', '2']]).note, null, 'the README only states the collision after trimming');
  assert.deepEqual(changedKeys(rows, { Port: 9464, Bind: '0.0.0.0', Headers: { a: 'b' }, 'Attribution.Enabled': true }), ['Bind', 'Attribution.Enabled', 'Headers']);
  assert.deepEqual(changedKeys(rows, { Headers: {} }), []);
});

// ------------------------------------------------------------------ privacy: nothing typed is stored, logged or sent
// The words that would put a typed value somewhere it can be found. Comments are taken out first: they may name them.
const FORBIDDEN = /\b(localStorage|sessionStorage|indexedDB|cookie|history|location|console|fetch|XMLHttpRequest|sendBeacon|WebSocket|postMessage)\b/g;
const strip = (src) => src.replace(/\/\*[\s\S]*?\*\//g, '').replace(/^\s*\/\/.*$/gm, '').replace(/([;{}\s])\/\/.*$/gm, '$1');
const source = (file) => readFileSync(new URL(`../js/${file}`, import.meta.url), 'utf8');

test('config.js, filter.js and search.js never store, log, send or put in the address what the reader types', () => {
  for (const file of ['config.js', 'filter.js', 'search.js']) assert.deepEqual(strip(source(file)).match(FORBIDDEN) ?? [], [], file);
  assert.notDeepEqual(strip('const x = 1; console.log(secret); // fine\n').match(FORBIDDEN) ?? [], [], 'the assertion catches a planted console.log');
  assert.notDeepEqual(strip('localStorage.setItem("k", v)').match(FORBIDDEN) ?? [], []);
  assert.deepEqual(strip('// the cookie in a comment\n/* history */ const a = 1;').match(FORBIDDEN) ?? [], [], 'comments may name them');
});

test('the three modules write nothing from the page, the address or a field with innerHTML, and set no style attribute', () => {
  for (const file of ['config.js', 'filter.js', 'search.js']) {
    const code = strip(source(file));
    assert.doesNotMatch(code, /innerHTML|outerHTML|insertAdjacentHTML|document\.write|\beval\(|new Function/, file);
    assert.doesNotMatch(code, /setAttribute\(\s*['"]style['"]|\.cssText|\.style\s*=/, `${file}: the policy blocks a style attribute silently`);
    assert.doesNotMatch(code, /\bon[a-z]+\s*=\s*['"]/, `${file}: no inline handler`);
  }
});

test('the stylesheet of the tools has no animation and no transition: they move nothing at rest', () => {
  const css = readFileSync(new URL('../css/60-tools.css', import.meta.url), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  assert.doesNotMatch(css, /animation|@keyframes|transition/);
  assert.doesNotMatch(css, /https?:|url\(/);
});

// ------------------------------------------------------------------ the pages
test('the explorer: one card per bullet of the three lists, with the ids a link can name', () => {
  const page = html('metrics/index.html');
  const bullets = new Set(data.metrics.map((m) => `${m.group}:${m.item}`));
  assert.equal([...page.matchAll(/<li class="metric" /g)].length, bullets.size, 'one card per bullet, one id per family');
  for (const m of data.metrics) {
    assert.equal([...page.matchAll(new RegExp(`id="${m.name}"`, 'g'))].length, 1, `${m.name} has exactly one anchor`);
    const card = element('metrics/index.html', 'li', m.siblings[0]);
    assert.match(card, new RegExp(`data-group="${m.group}" data-type="${m.type}"`), m.name);
    assert.ok(card.includes(`<code>${m.name}`), `${m.name}: the bullet is there as written`);
  }
  assert.match(page, /<section class="explorer" id="families" data-filter="group type">/);
  assert.equal([...page.matchAll(/<ul class="metrics">/g)].length, 3, 'public, engine and attribution');
  assert.match(page, /<div data-slot="filter"><\/div>/);
  // the three lead-in paragraphs still come before their lists, and the two paragraphs after the lists are outside the explorer
  const explorer = page.slice(page.indexOf('<section class="explorer"'), page.indexOf('</section>', page.indexOf('<section class="explorer"')));
  assert.match(explorer, /The metric families it serves:<\/p>\n<ul class="metrics">[\s\S]*Six more come from[\s\S]*<ul class="metrics">[\s\S]*Four more answer[\s\S]*<ul class="metrics">/);
  assert.doesNotMatch(explorer, /The tick period is measured/);
  assert.match(page.slice(page.indexOf('</section>', page.indexOf('<section class="explorer"'))), /^<\/section>\n<p>The tick period is measured/);
});

test('"Used by" says what the data says: the panels, the alert rules and the distinct queries, never anything written by hand', () => {
  for (const m of data.metrics) {
    const card = element('metrics/index.html', 'li', m.siblings[0]);
    const panels = new Set(), rules = new Set(), queries = new Set();
    for (const name of m.siblings) {
      const u = data.usedBy(name);
      u.panels.forEach((p) => panels.add(p.anchor));
      u.rules.forEach((r) => rules.add(r.id));
      u.queries.forEach((q) => queries.add(q.expr));
    }
    const listed = (re) => [...card.matchAll(re)].map((x) => x[1]);
    assert.deepEqual(new Set(listed(/href="\.\.\/dashboard\/index\.html#(panel-\d+)"/g)), panels, `${m.name}: panels`);
    assert.deepEqual(new Set(listed(/href="\.\.\/alerts\/index\.html#(rule-[a-z0-9-]+)"/g)), rules, `${m.name}: rules`);
    assert.deepEqual(new Set(listed(/<pre><code>([\s\S]*?)<\/code><\/pre>/g)), new Set([...queries].map(esc)), `${m.name}: queries`);
    assert.equal(card.includes('<details'), panels.size + rules.size > 0, `${m.name}: a disclosure when something reads it`);
    const summary = /<summary><span>(.*?)<\/span><\/summary>/.exec(card)?.[1];
    if (summary) assert.equal(summary, `Used by ${[panels.size && `${panels.size} panel${panels.size === 1 ? '' : 's'}`, rules.size && `${rules.size} alert rule${rules.size === 1 ? '' : 's'}`].filter(Boolean).join(' and ')}`);
  }
});

test('the attribution cards live on the Metrics page only: the Attribution page is the README as written', () => {
  assert.doesNotMatch(html('attribution/index.html'), /class="metric"|data-item|data-group/);
  const family = data.metrics.find((m) => m.group === 'attribution');
  assert.ok(html('attribution/index.html').includes(`<li><code>${family.name}`), 'the bullet is plain on the Attribution page');
  assert.ok(html('metrics/index.html').includes(`<li class="metric" id="${family.siblings[0]}" data-item data-group="attribution" data-type="${family.type}">`));
});

test('a metric reference links to the family, with a badge when it is not a public one, and a dotnet_ name to the runtime section', () => {
  const ctx = { data, esc, pageUrl: (from, page, hash) => `${from}>${page}#${hash}` };
  const of = (group) => data.metrics.find((m) => m.group === group).name;
  assert.equal(metricRef(ctx, 'alerts', of('public')), `<a class="metric-ref" href="alerts&gt;metrics#${of('public')}"><code>${of('public')}</code></a>`);
  assert.match(metricRef(ctx, 'alerts', of('engine')), /<\/a> <span class="badge badge--muted">engine<\/span>$/);
  assert.match(metricRef(ctx, 'alerts', of('attribution')), /<\/a> <span class="badge badge--muted">attribution<\/span>$/);
  assert.equal(metricRef(ctx, 'dashboard', 'dotnet_gc_collections_total'), '<a class="metric-ref" href="dashboard&gt;metrics#runtime-metrics"><code>dotnet_gc_collections_total</code></a>');
});

test('the config tables carry what the builder reads, from the README\'s own cells', () => {
  const page = html('configuration/index.html');
  assert.equal([...page.matchAll(/<table class="table--stack" data-config="ModConfig\/[a-z-]+\.json">/g)].length, 2);
  assert.match(page, /<div class="table-wrap" role="region" aria-label="ModConfig\/pulse\.json">/);
  assert.match(page, /<div class="table-wrap" role="region" aria-label="ModConfig\/pulse-otlp\.json">/);
  assert.doesNotMatch(page, /class="table-wrap" tabindex/, 'the rows are cards at every width: nothing to scroll, so no Tab stop');
  assert.match(html('changelog/index.html'), /<div class="table-wrap" tabindex="0" role="region" aria-label="Old name, New name">/, 'any other table can be wider than its wrapper, which a keyboard scrolls');
  for (const { rows } of data.config) {
    for (const r of rows) {
      const tr = new RegExp(`<tr id="${r.id}" data-key="${r.key.replace('.', '\\.')}" data-default="([^"]*)" data-type="([a-z]+)"([^>]*)>`).exec(page);
      assert.ok(tr, `${r.id}: a row the builder can read`);
      assert.equal(tr[1], esc(JSON.stringify(r.default)), `${r.id}: default as a JSON literal`);
      assert.equal(tr[2], r.type, r.id);
      const rest = tr[3];
      assert.equal(rest.includes(`data-min="${r.min}"`), r.min !== undefined, `${r.id}: floor`);
      assert.equal(rest.includes(`data-max="${r.max}"`), r.max !== undefined, `${r.id}: cap`);
      assert.equal(rest.includes('data-options='), !!r.options, `${r.id}: closed list`);
      assert.equal(/ data-live$/.test(rest), r.live, `${r.id}: live or restart`);
    }
  }
  for (const { rows } of data.config) for (const r of rows) assert.equal([...page.matchAll(new RegExp(`<tr id="${r.id}" `, 'g'))].length, 1, r.id);
  assert.doesNotMatch(page, /Your value|<button|<input|<select|<textarea/, 'the builder is script: without it the tables are the README\'s');
});

test('Install and configuration: the zips are offered before the paragraph that says where they go, with the version they are built for', () => {
  const page = html('configuration/index.html');
  const v = data.version;
  const at = page.indexOf('Drop <code>pulse_x.x.x.zip</code>');
  assert.ok(at > 0);
  const after = page.slice(page.lastIndexOf('step by step.</p>', at), at);
  assert.match(page.slice(at), /^[^]*?with its defaults:<\/p>\n<div class="code" data-lang="json">/, 'the sentence is followed by its block, nothing comes between them');
  assert.match(after, new RegExp(`<p class="dl"><a class="btn btn--sm" href="https://github\\.com/StratumServer/Pulse/releases/download/v${v}/pulse_${v}\\.zip">download pulse_${v}\\.zip</a> <span>version ${v}, released ${data.released}</span></p>`));
  assert.match(after, new RegExp(`href="https://github\\.com/StratumServer/Pulse/releases/download/v${v}/pulseotlp_${v}\\.zip">download pulseotlp_${v}\\.zip</a>`));
});

test('the panel browser: a card for every panel by id, every target in a block, every family read as a reference', () => {
  const page = html('dashboard/index.html');
  const panels = data.dashboard.rows.flatMap((r) => r.panels);
  assert.equal([...page.matchAll(/<article class="dash-panel" /g)].length, panels.length);
  assert.equal([...page.matchAll(/<section class="dash-row" /g)].length, data.dashboard.rows.length);
  assert.match(page, new RegExp(`<p>${panels.length} panels in ${data.dashboard.rows.length} rows, read from the dashboard file\\.</p>`));
  for (const row of data.dashboard.rows) assert.match(page, new RegExp(`<section class="dash-row" id="${row.id}"><h3>${esc(row.title)}`));
  for (const p of panels) {
    const card = element('dashboard/index.html', 'article', p.anchor);
    assert.match(card, new RegExp(`<article class="dash-panel" id="${p.anchor}" data-item>`), p.anchor);
    for (const t of p.targets) {
      assert.ok(card.includes(`<span class="code__label">${esc(t.legend ? `${t.refId}: ${t.legend}` : t.refId)}</span>`), `${p.anchor} ${t.refId}`);
      assert.ok(card.includes(`<pre><code>${esc(t.expr)}</code></pre>`), `${p.anchor} ${t.refId}`);
    }
    const names = [...new Set(p.targets.flatMap((t) => t.metrics))];
    assert.equal([...card.matchAll(/class="metric-ref"/g)].length, names.length, `${p.anchor}: Reads`);
    assert.equal(card.includes('<p class="dash-panel__desc">'), !!p.description, `${p.anchor}: a description when the file has one`);
  }
  assert.ok(new Set(panels.map((p) => p.title)).size < panels.length, 'titles repeat, so the id is the key');
  assert.equal(new Set(panels.map((p) => p.anchor)).size, panels.length);
});

test('the Dashboard page: the picture is sized and linked to the full file, the shared dashboard is a download', () => {
  const page = html('dashboard/index.html');
  assert.match(page, /<figure class="shot dash-shot"><a href="\.\.\/assets\/img\/dashboard-overview\.png"><img src="\.\.\/assets\/img\/dashboard-overview\.png" alt="Grafana dashboard: players, uptime[^"]+" width="1880" height="629" loading="lazy" decoding="async"><\/a><figcaption>The dashboard bundled in <code>contrib\/grafana<\/code>/);
  assert.ok(page.indexOf('<figure') < page.indexOf('<h2 id="importing-it-into-a-grafana-you-already-run">'), 'after the first paragraph, before the first section');
  const importing = page.indexOf('<h2 id="importing-it-into-a-grafana-you-already-run">');
  assert.match(page.slice(importing, page.indexOf('<h2 id="the-files">')), /<p class="dl"><a class="btn btn--sm" href="\.\.\/files\/pulse-overview-shared\.json" download>download pulse-overview-shared\.json<\/a><\/p>/);
  assert.match(page, /Source: <a class="ext" href="[^"]+contrib\/grafana\/README\.md">[^<]+<\/a>, <a class="ext" href="[^"]+provisioning\/dashboards\/json\/pulse\.json">/);
});

test('the rule browser: a card for every rule by (alert, severity), what it reads, why it exists', () => {
  const page = html('alerts/index.html');
  assert.equal([...page.matchAll(/<article class="rule" /g)].length, data.rules.length);
  assert.equal([...page.matchAll(/<section class="rule-group">/g)].length, new Set(data.rules.map((r) => r.group)).size);
  assert.match(page, /<section class="rules" id="rules" data-filter="severity">/);
  assert.match(page, /<p class="dl"><a class="btn btn--sm" href="\.\.\/files\/pulse-alerts\.yml" download>download pulse-alerts\.yml<\/a><\/p>/);
  for (const r of data.rules) {
    const card = element('alerts/index.html', 'article', r.id);
    assert.match(card, new RegExp(`<article class="rule" id="${r.id}" data-item data-severity="${r.severity}">`), r.id);
    assert.ok(card.includes(`<h4 class="rule__title"><code>${r.alert}</code> ${r.severity === 'critical' ? '<span class="badge badge--crit">CRITICAL</span>' : '<span class="badge badge--warn">warning</span>'}`), r.id);
    assert.equal(card.includes(`<span class="badge badge--muted">for ${r.for}</span>`), !!r.for, `${r.id}: for`);
    assert.ok(card.includes(`<pre><code>${esc(r.expr)}</code></pre>`) && card.includes(esc(r.summary)) && card.includes(esc(r.description)), r.id);
    assert.equal(card.includes('<p class="rule__reads">'), r.metrics.length > 0, `${r.id}: Reads only when a family is read`);
    assert.equal(card.includes('<pre class="rule__why">'), !!r.comment, `${r.id}: the comment of the file`);
  }
  assert.ok(!data.rules.every((r, i, all) => all.findIndex((x) => x.alert === r.alert) === i), 'names repeat, so the pair is the key');
});

test('without script the four pages hold no control, and the filter slots are empty', () => {
  for (const path of ['metrics', 'configuration', 'dashboard', 'alerts']) {
    const page = html(`${path}/index.html`);
    assert.doesNotMatch(page, /<button|<input|<select|<textarea|<form/, path);
    assert.doesNotMatch(page, /<div data-slot="filter">[^<]/, path);
  }
  for (const path of ['metrics', 'dashboard', 'alerts']) assert.match(html(`${path}/index.html`), /<div data-slot="filter"><\/div>/, path);
});

// ------------------------------------------------------------------ the build says so when a document no longer has the shape the tools were written for
test('a README that loses the paragraph that introduces the attribution cards, or the one that offers the zips, stops the build with that sentence', async () => {
  const fails = (sources, re) => assert.rejects(() => buildSite({ sources, offline: true }), re);
  await fails(altered('README.md', (t) => t.replace('Four more answer', 'Another four answer')), /Four more answer/);
  await fails(altered('README.md', (t) => t.replace("Drop `pulse_x.x.x.zip` into your server's `Mods/` folder", 'Put the zip in your Mods folder')), /Drop `pulse_x\.x\.x\.zip`/);
});

test('a config key the builder cannot edit stops the build instead of being edited wrongly', async () => {
  // a number with a fraction: the table and the printed file agree, so lib/data.mjs is content, and the builder only edits whole numbers.
  // Not Port nor the attribution numbers: Home guards those facts itself and would stop the build first.
  const port = data.config[0].rows.find((r) => r.key === 'ChunksRefreshSeconds');
  const text = real.text('README.md');
  const cell = `| \`${port.key}\` | \`${port.default}\` |`, line = `"${port.key}": ${port.default},`;
  assert.ok(text.includes(cell) && text.includes(line), 'the edit of this test still applies to the README');
  const fraction = altered('README.md', (t) => t.replace(cell, cell.replace(`${port.default}`, `${port.default}.5`)).replace(line, line.replace(`${port.default}`, `${port.default}.5`)));
  await assert.rejects(() => buildSite({ sources: fraction, offline: true }), new RegExp(`${port.key} in the ModConfig/pulse\\.json table has a default of a shape the config builder does not know \\(${port.default}\\.5\\)`));
});
