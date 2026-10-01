// The build rules, run against the real documents of the working tree, and the proof that the build
// fails loudly: each case feeds an altered copy of a document to the code that checks it and expects
// an error that names the problem. Counts that grow with the documents (families, panels, rules) are
// asserted as invariants, not as numbers: a docs-only pull request must not turn red because a metric
// was added. What is exact is what the build itself treats as a contract.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { Marked } from 'marked';
import { parseDocument } from 'yaml';
import { buildSite, writeOutput } from '../build.mjs';
import { ATTRIBUTION_SHOT, OVERVIEW } from '../content/home.mjs';
import { BUDGETS, checkPages } from '../lib/checks.mjs';
import { fetchHealth, loadData, metricsIn, parseHealth, resolveVersion, SONAR_API } from '../lib/data.mjs';
import { assetUrl, BOOT, pageUrl, plainText } from '../lib/layout.mjs';
import { createMd, markCallout } from '../lib/md.mjs';
import { linkify, loadSources, makeSlugger, plain } from '../lib/sources.mjs';

const real = loadSources();
const altered = (path, edit) => loadSources({ text: (p) => (p === path ? edit(real.text(p)) : real.text(p)) });
const failsWith = (sources, re) => assert.rejects(() => buildSite({ sources, offline: true }), re);
const built = await buildSite({ offline: true });
const page = (path) => built.files.get(path);

// ------------------------------------------------------------------ slugs and headings
test('slugs are the ones GitHub gives, duplicates numbered per document', () => {
  const slug = makeSlugger();
  assert.equal(slug('A word on the bind address'), 'a-word-on-the-bind-address');
  assert.equal(slug('Path B: Grafana Cloud, if you would rather host nothing'), 'path-b-grafana-cloud-if-you-would-rather-host-nothing');
  assert.equal(slug('[0.2.0] - 2026-09-29'), '020---2026-09-29');
  assert.equal(slug('Added'), 'added');
  assert.equal(slug('Added'), 'added-1');
  const ids = real.docs['CHANGELOG.md'].headings.map((h) => h.id);
  assert.ok(ids.includes('020---2026-09-29') && ids.includes('unreleased'), 'the ids the documents link to exist');
  assert.equal(new Set(ids).size, ids.length, 'ids are unique inside a document');
  assert.deepEqual(real.docs['README.md'].headings.filter((h) => h.level === 2).map((h) => h.id).slice(0, 4), ['table-of-contents', 'install', 'configuration', 'scraping-it']);
});

test('the heading contract: README is exact for H1 and H2 and free for H3, the guide is exact at every level', () => {
  assert.throws(() => altered('README.md', (t) => t.replace('## Degraded mode', '## Degraded modes')), /heading contract/);
  assert.throws(() => altered('README.md', (t) => t.replace('## License', '## Licence')), /heading contract/);
  assert.throws(() => altered('docs/getting-started.md', (t) => t.replace('### Before you start', '### Before you begin')), /heading contract/);
  assert.throws(() => altered('docs/getting-started.md', (t) => t.replace('## Troubleshooting', '## Troubleshooting\n\n### A new H3')), /heading contract/);
  assert.throws(() => altered('contrib/alerts/README.md', (t) => t + '\n## One more\n'), /heading contract/);
  assert.throws(() => altered('CHANGELOG.md', (t) => t.replace('## [Unreleased]', '## Unreleased')), /CHANGELOG\.md/);
  assert.throws(() => altered('CHANGELOG.md', (t) => t.replace('### Added', '### Invented')), /not one of Added/);
  const withH3 = altered('README.md', (t) => t.replace('## Building and testing', '### A new H3 inside OTLP export\n\nText.\n\n## Building and testing'));
  const r = createMd(withH3).render(withH3.slice('otlp'), { file: 'README.md', page: 'otlp', shift: 1 });
  assert.match(r.html, /A new H3 inside OTLP export/, 'a README H3 renders wherever its section does');
});

test('no raw HTML, no image inside a rendered part, no fourth log excerpt', () => {
  assert.throws(() => altered('docs/getting-started.md', (t) => t + '\n<!-- note -->\n'), /raw HTML/);
  assert.throws(() => altered('README.md', (t) => t.replace('## Where this is going\n', '## Where this is going\n\n<b>x</b>\n')), /raw HTML/);
  assert.throws(() => altered('README.md', (t) => t.replace('## License', '## License\n\n![x](a.png)')), /an image/);
  assert.throws(() => altered('CHANGELOG.md', (t) => t + '\n![x](a.png)\n'), /an image/);
  assert.throws(() => altered('contrib/alerts/README.md', (t) => t + '\n```\nanother log line\n```\n'), /a fence without a language \("another log line"\) is not one of the log excerpts/);
});

test('log excerpts are named, and only the ones that are one wrapped line are joined', () => {
  const html = (name, file, page, shift = 0) => createMd(real).render(real.slice(name), { file, page, shift }).html;
  const guide = html('docs/getting-started.md', 'docs/getting-started.md', 'getting-started');
  assert.match(guide, /<pre class="term__body" tabindex="0">Pulse OTLP export to https:\/\/otlp-gateway-&lt;region&gt;\.grafana\.net\/otlp\/v1\/metrics failed: Response status code does not indicate success: 401 \(Unauthorized\)\. The backend answered: \{/, 'one log line, not five');
  assert.match(guide, /<pre class="term__body" tabindex="0">Pulse serving metrics on http:\/\/127\.0\.0\.1:9464\/metrics<\/pre>/);
  assert.doesNotMatch(guide, /term__body"[^>]*>[^<]*\n[^<]*<\/pre>/, 'no log excerpt keeps a line break of the document');
  assert.match(html('otlp', 'README.md', 'otlp', 1), /<pre class="term__body" tabindex="0">Pulse OTLP export to [^\n<]+ minutes\.<\/pre>/);
  assert.match(page('getting-started/index.html'), /failed: Response status code does not indicate success/);
  // a fence with a language is code: its lines stay
  assert.match(guide, /<code>\{\n  &quot;Endpoint&quot;/);
  // an excerpt that is declared one line and grows, or that disappears, stops the build
  assert.throws(() => altered('docs/getting-started.md', (t) => { const at = 'Pulse serving metrics on http://127.0.0.1:9464/metrics\n'; assert.ok(t.includes(at)); return t.replace(at, `${at}   and a second line\n`); }), /declared as one line and now spans several/);
  assert.throws(() => altered('docs/getting-started.md', (t) => t.replace('Pulse serving metrics on', 'Pulse is serving metrics on')), /is not one of the log excerpts/);
});

test('a missing input stops the build before anything is written', () => {
  assert.throws(() => loadSources({ exists: (p) => (p === 'NOTICE' ? null : 'file') }), /NOTICE: input file is missing/);
});

// ------------------------------------------------------------------ phrases that become links
test('every phrase of LINKIFY is found exactly once and no word changes', () => {
  const visible = (text) => new Marked({ gfm: true }).parse(text).replace(/<[^>]+>/g, '').replace(/\s+/g, ' ');
  for (const file of ['README.md', 'docs/getting-started.md', 'contrib/grafana/README.md', 'contrib/alerts/README.md']) {
    const text = real.text(file);
    assert.equal(visible(linkify(file, text)), visible(text), `linkify changes no word of ${file}`);
  }
  assert.throws(() => linkify('README.md', real.text('README.md').replace('lives in `contrib/grafana`', 'lives elsewhere')), /should occur exactly once/);
  assert.throws(() => linkify('README.md', real.text('README.md') + '\nlives in `contrib/grafana`\n'), /should occur exactly once/);
});

// ------------------------------------------------------------------ links
test('links are rewritten to pages and anchors, and a dead link stops the build', async () => {
  const r = real.resolve;
  assert.deepEqual(r('README.md', 'docs/getting-started.md'), { page: 'getting-started', hash: '' });
  assert.deepEqual(r('README.md', '#install'), { page: 'configuration', hash: 'install' });
  assert.deepEqual(r('README.md', '#a-word-on-the-bind-address'), { page: 'scraping', hash: 'a-word-on-the-bind-address' });
  assert.deepEqual(r('README.md', '#runtime-metrics'), { page: 'metrics', hash: 'runtime-metrics' });
  assert.deepEqual(r('README.md', '#attribution'), { page: 'attribution', hash: 'attribution' });
  assert.deepEqual(r('README.md', 'CHANGELOG.md#020---2026-09-29'), { page: 'changelog', hash: '020---2026-09-29' });
  assert.deepEqual(r('docs/getting-started.md', '../README.md#degraded-mode'), { page: 'metrics', hash: 'degraded-mode' });
  assert.deepEqual(r('contrib/alerts/README.md', '../grafana/README.md'), { page: 'dashboard', hash: '' });
  assert.deepEqual(r('contrib/grafana/README.md', '../../docs/getting-started.md'), { page: 'getting-started', hash: '' });
  assert.deepEqual(r('README.md', 'LICENSE'), { external: 'https://github.com/StratumServer/Pulse/blob/main/LICENSE' });
  assert.deepEqual(r('README.md', 'contrib/grafana'), { external: 'https://github.com/StratumServer/Pulse/tree/main/contrib/grafana' });
  assert.deepEqual(r('README.md', 'https://example.org/x'), { external: 'https://example.org/x' });
  assert.deepEqual(r('README.md', 'mailto:someone@example.org'), { external: 'mailto:someone@example.org' });
  assert.throws(() => r('README.md', 'javascript:alert(1)'), /scheme the site does not link to/);
  assert.throws(() => r('README.md', '#nope'), /not a heading/);
  assert.throws(() => r('README.md', 'docs/missing.md'), /not in the repository/);
  assert.throws(() => r('README.md', '#table-of-contents'), /does not render/);
  await failsWith(altered('README.md', (t) => t.replaceAll('[Install](#install)', '[Install](#nope)')), /not a heading/);
  await failsWith(altered('README.md', (t) => t.replaceAll('(docs/getting-started.md)', '(docs/missing.md)')), /not in the repository/);
  await failsWith(altered('README.md', (t) => t.replaceAll('[Install](#install)', '[Install](#table-of-contents)')), /does not render/);
});

test('page addresses are relative and name index.html', () => {
  assert.equal(pageUrl('', 'metrics', 'degraded-mode'), 'metrics/index.html#degraded-mode');
  assert.equal(pageUrl('scraping', 'metrics', 'degraded-mode'), '../metrics/index.html#degraded-mode');
  assert.equal(pageUrl('metrics', 'metrics', 'degraded-mode'), '#degraded-mode');
  assert.equal(pageUrl('metrics', ''), '../index.html');
  assert.equal(pageUrl('metrics', 'metrics'), 'index.html');
  assert.equal(pageUrl('', ''), 'index.html');
  assert.equal(assetUrl('metrics', 'assets/site.css'), '../assets/site.css');
  assert.equal(assetUrl('', 'assets/site.css'), 'assets/site.css');
});

// ------------------------------------------------------------------ slices
test('a page module works on its own copy of the tokens', () => {
  const md = createMd(real);
  const opts = { file: 'README.md', page: 'attribution', shift: 1 };
  const baseline = md.render(real.slice('attribution'), opts).html;
  const copy = real.slice('attribution');
  copy[0].attrs = { class: 'x' };
  copy.find((t) => t.type === 'paragraph').callout = 'security';
  copy.find((t) => t.type === 'list').items[0].append = '<b>cards</b>';
  copy.find((t) => t.type === 'code').after = '<hr class="after">';
  assert.notEqual(md.render(copy, opts).html, baseline, 'the hooks change the copy');
  assert.equal(md.render(real.slice('attribution'), opts).html, baseline, 'a slice taken after is as plain as before');
  assert.throws(() => { real.docs['README.md'].tokens[0].attrs = {}; }, TypeError, 'the shared documents are frozen');
  assert.throws(() => real.slice('nope'), /not a README slice/);
  assert.throws(() => real.slice('constructor'), /not a README slice/, 'only the names of the slices and the paths of the documents');
  assert.ok(real.slice('docs/getting-started.md').length > 10, 'a whole document is a slice too');
});

test('the README is cut into its slices', () => {
  const first = (name) => plain(real.slice(name)[0]);
  assert.equal(first('install'), 'Install');
  assert.equal(first('scraping'), 'Scraping it');
  assert.equal(first('degraded'), 'Degraded mode');
  assert.equal(first('license'), 'License');
  const families = real.slice('families');
  assert.equal(families[0].type, 'paragraph');
  assert.match(families[0].text, /^The metric families it serves:$/);
  assert.ok(!real.slice('scraping').some((t) => t.type === 'paragraph' && t.text === 'The metric families it serves:'), 'the split paragraph starts the next slice');
  assert.ok(real.slice('scraping').some((t) => t.type === 'heading' && t.text === 'A word on the bind address'));
});

test('the metric split paragraph must exist exactly once', () => {
  assert.throws(() => altered('README.md', (t) => t.replace('The metric families it serves:', 'The families it serves:')), /should occur exactly once at the top level, found 0/);
  assert.throws(() => altered('README.md', (t) => t.replace('## Degraded mode', 'The metric families it serves:\n\n## Degraded mode')), /found 2/);
});

// ------------------------------------------------------------------ data
test('metric families come from the three lists of the README', () => {
  const { data } = built;
  assert.ok(data.metrics.length >= 28);
  assert.equal(new Set(data.metrics.map((m) => m.name)).size, data.metrics.length, 'no duplicate');
  assert.deepEqual([...new Set(data.metrics.map((m) => m.group))].sort(), ['attribution', 'engine', 'public']);
  assert.ok(data.metrics.every((m) => m.name.startsWith('pulse_') && ['counter', 'gauge', 'histogram'].includes(m.type)));
  assert.equal(data.metric('pulse_server_tick_seconds').type, 'histogram');
  assert.deepEqual(data.metric('pulse_server_suspends_total').siblings, ['pulse_server_suspends_total', 'pulse_server_suspend_seconds_total']);
  assert.deepEqual(data.metric('pulse_log_entries_total').labels, ['level']);
  assert.equal(data.metric('pulse_mod_tick_share').group, 'attribution');
  assert.equal(data.metric('nope'), undefined);
  assert.equal(typeof data.metrics[0].item, 'number', 'item is a position, never a token');
  assert.throws(() => { data.metrics[0].name = 'x'; }, TypeError, 'the data is frozen');
});

test('a bullet that does not match the metric rule stops the build', async () => {
  await failsWith(altered('README.md', (t) => t.replace('- `pulse_player_deaths_total` (counter)', '- `pulse_new_thing` (summary): a new one.\n- `pulse_player_deaths_total` (counter)')), /does not match the metric rule/);
  await failsWith(altered('README.md', (t) => t.replace('## Degraded mode', '- `pulse_stray_total` (counter): out of place.\n\n## Degraded mode')), /out of place/);
});

test('both config tables read into rows, constraints on exactly the known keys', () => {
  const { config } = built.data;
  assert.deepEqual(config.map((c) => c.file), ['ModConfig/pulse.json', 'ModConfig/pulse-otlp.json']);
  const row = (file, key) => config.find((c) => c.file === file).rows.find((r) => r.key === key);
  const pulse = (key) => row('ModConfig/pulse.json', key), otlp = (key) => row('ModConfig/pulse-otlp.json', key);
  assert.deepEqual([pulse('ChunksRefreshSeconds').min, pulse('ChunksRefreshSeconds').max], [1, 86400]);
  assert.deepEqual([pulse('Attribution.BurstTicks').min, pulse('Attribution.BurstTicks').max], [1, 300]);
  assert.equal(pulse('Attribution.IntervalSeconds').min, 1);
  assert.deepEqual(otlp('Protocol').options, ['http/protobuf', 'grpc']);
  assert.deepEqual([otlp('IntervalSeconds').min, otlp('IntervalSeconds').max], [5, 86400]);
  assert.equal(pulse('Port').type, 'number');
  assert.equal(otlp('Headers').type, 'object');
  assert.deepEqual(config[0].rows.filter((r) => r.live).map((r) => r.key), ['Attribution.Enabled', 'Attribution.BurstTicks', 'Attribution.IntervalSeconds']);
  assert.equal(pulse('Attribution.Enabled').liveText, 'Live, via /pulse reload', 'the cell as text, without its back-ticks');
  assert.equal(pulse('Attribution.BurstTicks').id, 'cfg-pulse-attribution-burstticks');
});

test('a default that differs from the printed file, or a newly limited key, stops the build', async () => {
  await failsWith(altered('README.md', (t) => t.replace('| `Port` | `9464` |', '| `Port` | `"9464"` |')), /defaults of the ModConfig\/pulse\.json table differ/);
  await failsWith(altered('README.md', (t) => t.replace('Clamped to 1 through 300', 'Clamped to 1 through 600')), /constraints read from the config tables/);
  await failsWith(altered('README.md', (t) => t.replace('Port the metrics endpoint listens on.', 'Clamped to 1 through 65535. Port the metrics endpoint listens on.')), /constraints read from the config tables/);
  await failsWith(altered('README.md', (t) => t.replace('| Key | Default | What it does | Live or restart |', '| Key | Default | What | Live or restart |')), /header of the/);
  await failsWith(altered('README.md', (t) => t.replace('`"127.0.0.1"`', '`127.0.0.1`')), /not a JSON literal/);
});

test('alert rules: unique ids, comments, the metrics each reads', () => {
  const { rules } = built.data;
  assert.ok(rules.length >= 11);
  assert.equal(new Set(rules.map((r) => r.id)).size, rules.length);
  assert.ok(rules.every((r) => ['warning', 'critical'].includes(r.severity) && r.id === `rule-${r.alert.toLowerCase()}-${r.severity}`));
  const critical = rules.find((r) => r.id === 'rule-pulsetickratelow-critical');
  assert.equal(critical.comment, rules.find((r) => r.id === 'rule-pulsetickratelow-warning').comment, 'a rule with no comment of its own takes the one of the rule with the same name');
  assert.ok(rules.every((r) => r.comment), 'every rule ends up with a comment');
  const needs = (id) => [...new Set(rules.find((r) => r.id === id).metrics.map((m) => built.data.metric(m)?.group).filter((g) => g && g !== 'public'))].sort();
  assert.deepEqual(needs('rule-pulseticksaturationhigh-warning'), ['engine'], 'the engine probe caveat of the alerts README');
  assert.deepEqual(needs('rule-pulsemodhoggingtick-warning'), ['attribution', 'engine'], 'the attribution caveat of the alerts README');
});

test('metricsIn gives the family of a histogram series', () => {
  const histograms = new Set(['pulse_server_tick_seconds']);
  assert.deepEqual(metricsIn('histogram_quantile(0.95, sum by (le) (rate(pulse_server_tick_seconds_bucket[5m])))', histograms), ['pulse_server_tick_seconds']);
  assert.deepEqual(metricsIn('rate(pulse_server_ticks_total[2m]) < 27 and up{job="x"}', histograms), ['pulse_server_ticks_total']);
  assert.deepEqual(metricsIn('dotnet_gc_collections_total + pulse_x_total', histograms), ['dotnet_gc_collections_total', 'pulse_x_total']);
});

test('the dashboard: rows, panels with a row title, queries per family', () => {
  const { dashboard } = built.data;
  const panels = dashboard.rows.flatMap((r) => r.panels);
  assert.ok(dashboard.rows.length >= 1 && panels.length >= dashboard.rows.length);
  assert.equal(new Set(panels.map((p) => p.id)).size, panels.length, 'panel ids are unique');
  assert.ok(panels.every((p) => p.anchor === `panel-${p.id}` && p.row));
  assert.ok(dashboard.rows.every((r) => r.id.startsWith('row-')));
  const used = built.data.usedBy('pulse_server_tick_seconds');
  assert.ok(used.panels.length >= 1 && used.rules.length >= 1 && used.queries.length >= 1);
  assert.equal(new Set(used.queries.map((q) => q.expr)).size, used.queries.length, 'distinct expressions');
  assert.deepEqual(built.data.usedBy('pulse_nothing_total'), { panels: [], rules: [], queries: [] });
});

test('a panel that reads a family nobody documents, or a kit file that is off, stops the build', async () => {
  const dash = 'contrib/grafana/provisioning/dashboards/json/pulse.json';
  const edit = (f) => loadSources({ text: (p) => { if (p !== dash) return real.text(p); const d = JSON.parse(real.text(p)); f(d); return JSON.stringify(d); } });
  await failsWith(edit((d) => { d.panels.find((p) => p.targets?.length).targets[0].expr = 'rate(pulse_unknown_total[1m])'; }), /pulse_unknown_total/);
  await failsWith(edit((d) => { d.panels.find((p) => p.type === 'row').collapsed = true; }), /collapsed/);
  await failsWith(edit((d) => { d.panels.unshift({ id: 999, type: 'stat', title: 'orphan', targets: [] }); }), /before the first row/);
  await failsWith(edit((d) => { const ps = d.panels.filter((p) => p.type !== 'row'); ps[1].id = ps[0].id; }), /used twice/);
  const rules = (edit2) => loadSources({ text: (p) => (p === 'contrib/alerts/pulse-alerts.yml' ? edit2(real.text(p)) : real.text(p)) });
  await failsWith(rules((t) => t.replace('severity: warning', 'severity: info')), /severity "info"/);
  await failsWith(rules(() => 'groups: [\n'), /does not parse/);
});

test('a missing field of a rule or a panel, or a name that cannot be an id, stops the build with a sentence that names the file', async () => {
  const dash = 'contrib/grafana/provisioning/dashboards/json/pulse.json', alerts = 'contrib/alerts/pulse-alerts.yml';
  const panels = (f) => loadSources({ text: (p) => { if (p !== dash) return real.text(p); const d = JSON.parse(real.text(p)); f(d); return JSON.stringify(d); } });
  const rules = (f) => loadSources({ text: (p) => { if (p !== alerts) return real.text(p); const d = parseDocument(real.text(p)); f(d); return String(d); } });
  const first = (...path) => ['groups', 0, 'rules', 0, ...path];
  await failsWith(rules((d) => d.deleteIn(first('annotations', 'summary'))), /pulse-alerts\.yml: the rule \w+ has no text for a summary annotation/);
  await failsWith(rules((d) => d.deleteIn(first('annotations', 'description'))), /has no text for a description annotation/);
  await failsWith(rules((d) => d.setIn(first('alert'), 'Pulse Tick Rate')), /the alert name "Pulse Tick Rate" is not made of letters, digits/);
  await failsWith(rules((d) => d.setIn(['groups', 0, 'name'], 'tick 100%')), /the group name "tick 100%" is not made of letters, digits/);
  await failsWith(panels((d) => { delete d.panels.find((p) => p.type !== 'row').id; }), /contrib\/grafana\/\.\.\.\/pulse\.json: a panel needs an integer id, a title and a type/);
  await failsWith(panels((d) => { delete d.panels.find((p) => p.type !== 'row').title; }), /a panel needs an integer id, a title and a type/);
  const noRef = await buildSite({ sources: panels((d) => { delete d.panels.find((p) => p.targets?.length).targets[0].refId; }), offline: true });
  assert.doesNotMatch(noRef.files.get('dashboard/index.html'), /undefined/, 'a target with no refId is labelled with what it has');
  assert.match(noRef.files.get('dashboard/index.html'), /<span class="code__label">players<\/span>/, 'its legend alone, with no colon in front of it');
});

test('the version: the newest release of the changelog, whatever modinfo.json says', () => {
  const heads = (...texts) => texts.map((text) => ({ level: 2, text }));
  const h = heads('[Unreleased]', '[0.2.0] - 2026-09-29', '[0.1.0] - 2026-09-01');
  assert.deepEqual(resolveVersion({ mod: '0.2.0', otlp: '0.2.0', headings: h }), { version: '0.2.0', released: '2026-09-29', note: null });
  const open = resolveVersion({ mod: '0.3.0', otlp: '0.3.0', headings: h });
  assert.equal(open.version, '0.2.0');
  assert.equal(open.note, 'modinfo.json says 0.3.0, which has no dated changelog heading yet; building for 0.2.0');
  assert.throws(() => resolveVersion({ mod: '0.1.0', otlp: '0.1.0', headings: h }), /newest one/);
  assert.throws(() => resolveVersion({ mod: '0.2.0', otlp: '0.1.0', headings: h }), /must carry the same version/);
  assert.throws(() => resolveVersion({ mod: '0.2.0', otlp: '0.2.0', headings: heads('[Unreleased]') }), /no heading of the form/);
});

test('both modinfo.json at 9.9.9 build for the newest release and say so', async () => {
  const text = (p) => (p.endsWith('modinfo.json') ? real.text(p).replace(/"version": "[^"]+"/, '"version": "9.9.9"') : real.text(p));
  const data = await loadData(loadSources({ text }), { offline: true });
  assert.equal(data.version, built.data.version);
  assert.match(data.notes[0], /^modinfo\.json says 9\.9\.9, which has no dated changelog heading yet; building for /);
  assert.match(data.released, /^\d{4}-\d{2}-\d{2}$/);
  assert.match(data.notice, /^This product is an independent/);
  await assert.rejects(() => loadData(loadSources({ text: (p) => (p === 'NOTICE' ? 'Pulse\n\nSomething else.\n' : real.text(p)) }), { offline: true }), /NOTICE/);
});

// ------------------------------------------------------------------ SonarCloud
test('project health: fetched with a timeout, offline by flag, never fatal', async () => {
  const body = { component: { measures: [{ metric: 'coverage', value: '96.8' }, { metric: 'alert_status', value: 'OK' }, { metric: 'bugs', value: '0' }, { metric: 'vulnerabilities', value: '0' }] } };
  assert.deepEqual(parseHealth(body), { gate: 'OK', coverage: 96.8, bugs: 0, vulnerabilities: 0 });
  const withValue = (metric, value) => ({ component: { measures: body.component.measures.map((m) => (m.metric === metric ? { ...m, value } : m)) } });
  for (const bad of [null, {}, { component: { measures: [] } }, withValue('alert_status', '<script>'), withValue('bugs', '1; drop'), withValue('coverage', 'NaN'),
    withValue('coverage', '100.5'), withValue('bugs', '9'.repeat(400))]) assert.equal(parseHealth(bad), null);
  assert.equal(parseHealth(withValue('coverage', '100')).coverage, 100, 'a whole project is covered at most 100 percent, and 100 is allowed');
  const quiet = console.warn; console.warn = () => {};
  try {
    let asked = 0;
    const answer = (res) => async (url, init) => { asked++; assert.equal(url, SONAR_API); assert.ok(init.signal, 'a timeout signal is passed'); return res; };
    assert.deepEqual(await fetchHealth({ fetchFn: answer({ ok: true, json: async () => body }) }), { gate: 'OK', coverage: 96.8, bugs: 0, vulnerabilities: 0 });
    assert.equal(await fetchHealth({ fetchFn: answer({ ok: false, status: 503 }) }), null);
    assert.equal(await fetchHealth({ fetchFn: answer({ ok: true, json: async () => ({}) }) }), null);
    assert.equal(await fetchHealth({ fetchFn: async () => { throw new TypeError('fetch failed'); } }), null);
    assert.equal(await fetchHealth({ timeoutMs: 20, fetchFn: (url, init) => new Promise((_, reject) => { const keep = setTimeout(() => {}, 5000); init.signal.addEventListener('abort', () => { clearTimeout(keep); reject(init.signal.reason); }); }) }), null, 'no answer in time');
    const before = asked;
    assert.equal(await fetchHealth({ offline: true, fetchFn: answer({ ok: true, json: async () => body }) }), null);
    assert.equal(asked, before, 'offline makes no request');
    const online = await buildSite({ fetchFn: answer({ ok: true, json: async () => body }) });
    assert.match(online.files.get('index.html'), /quality gate<\/a><\/dt><dd>OK<\/dd>/);
    assert.match(online.files.get('index.html'), /coverage<\/a><\/dt><dd>96\.8%<\/dd>/);
    const failed = await buildSite({ fetchFn: async () => { throw new TypeError('fetch failed'); } });
    assert.doesNotMatch(failed.files.get('index.html'), /<dd>OK<\/dd>/, 'no numbers when SonarCloud is silent');
    assert.match(failed.files.get('index.html'), /quality gate<\/a>/, 'the links stay');
  } finally { console.warn = quiet; }
  assert.equal(built.data.health, null, 'the build of these tests is offline');
  assert.doesNotMatch(page('index.html'), /<dd>(OK|\d+(\.\d+)?%)<\/dd>/);
});

// ------------------------------------------------------------------ rendering (the hooks of lib/md.mjs)
const lex = (text) => {
  const tokens = new Marked({ gfm: true }).lexer(text), slug = makeSlugger();
  new Marked().walkTokens(tokens, (t) => { if (t.type === 'heading') t.id = slug(plain(t)); });
  return tokens;
};
const md = createMd({ resolve: real.resolve });

test('headings: ids, promotion, anchor links, and no link when asked', () => {
  const doc = lex('# One\n\n## Two\n\n### Three `x`\n\n#### Four\n\n## Two\n');
  const headings = (tokens) => md.render(tokens, {}).html.split('\n').filter((l) => l.startsWith('<h'));
  assert.deepEqual(headings(doc), [
    '<h1 id="one">One</h1>',
    '<h2 id="two">Two&nbsp;<a class="anchor" href="#two" aria-label="Link to this section">#</a></h2>',
    '<h3 id="three-x">Three <code>x</code>&nbsp;<a class="anchor" href="#three-x" aria-label="Link to this section">#</a></h3>',
    '<h4 id="four">Four</h4>',
    '<h2 id="two-1">Two&nbsp;<a class="anchor" href="#two-1" aria-label="Link to this section">#</a></h2>']);
  const attr = md.render(real.slice('attribution'), { file: 'README.md', page: 'attribution', shift: 1 });
  assert.ok(attr.html.startsWith('<h1 id="attribution">Attribution</h1>'), 'a promoted H2 is an h1 that keeps its id and has no anchor link');
  assert.match(attr.html, /<h2 id="turning-it-on-without-a-restart">Turning it on without a restart&nbsp;<a class="anchor" href="#turning-it-on-without-a-restart"/);
  assert.deepEqual(attr.toc.map((e) => [e.level, e.id]), [[2, 'turning-it-on-without-a-restart'], [2, 'how-it-works-and-what-it-costs'], [2, 'what-it-cannot-see']]);
  const two = doc.filter((t) => t.type === 'heading')[1];
  two.anchor = false;
  assert.doesNotMatch(md.render(doc, {}).html, /id="two">Two&nbsp;<a/);
  two.attrs = { class: 'fold__h', 'data-x': '' };
  assert.match(md.render(doc, {}).html, /<h2 id="two" class="fold__h" data-x>/);
});

test('the hooks: attrs, rowAttrs, prepend, append, after, label, where, ph, callout', () => {
  const doc = lex('Para.\n\nSecret.\n\n- one\n- two\n\n```sh\ncurl https://x/<region>\n```\n\n```\nlog line\n```\n\n| a | b | c |\n| - | - | - |\n| 1 | 2 | 3 |\n\n| a | b |\n| - | - |\n| 1 | 2 |\n');
  const [p, secret, list, sh, , wide] = doc.filter((t) => t.type !== 'space');
  p.attrs = { id: 'lead', class: 'big' }; p.after = '<hr class="after">';
  secret.callout = true;
  list.attrs = { 'data-list': 'x' }; list.items[0].attrs = { id: 'one' }; list.items[0].prepend = '<i>pre</i>'; list.items[1].append = '<i>post</i>';
  sh.attrs = { 'data-tunnel': '' }; sh.label = 'ModConfig/x.json'; sh.where = 'run on the game server'; sh.ph = ['<region>'];
  wide.label = 'ModConfig/pulse.json'; wide.attrs = { 'data-config': 'x', class: 'mine' }; wide.rowAttrs = [{ id: 'row-1' }];
  const { html } = md.render(doc, {});
  assert.match(html, /<p id="lead" class="big">Para\.<\/p>\n<hr class="after">/);
  assert.match(html, /<aside class="callout callout--security" aria-label="Security"><span class="callout__label">SECURITY<\/span><p>Secret\.<\/p><\/aside>/);
  assert.match(html, /<ul data-list="x">\n<li id="one"><i>pre<\/i>one<\/li>\n<li>two<i>post<\/i><\/li>\n<\/ul>/);
  assert.match(html, /<div class="code" data-lang="sh" data-tunnel><div class="code__bar"><span class="code__label">ModConfig\/x\.json<\/span><span class="code__where">run on the game server<\/span><\/div><pre><code>curl https:\/\/x\/<span class="ph">&lt;region&gt;<\/span>\n?<\/code><\/pre><\/div>/);
  assert.match(html, /<figure class="term term--log"><div class="term__bar" aria-hidden="true"><span class="term__dot"><\/span><span class="term__dot"><\/span><span class="term__dot"><\/span><span class="term__title">Logs\/server-main\.log<\/span><\/div><pre class="term__body" tabindex="0">log line\n?<\/pre><\/figure>/);
  assert.match(html, /<div class="table-wrap" role="region" aria-label="ModConfig\/pulse\.json"><table class="table--stack mine" data-config="x">/, 'a config table is stacked at every width: its wrapper never scrolls and is no Tab stop');
  assert.match(html, /<tr id="row-1"><td data-label="a">1<\/td><td data-label="b">2<\/td><td data-label="c">3<\/td><\/tr>/);
  assert.match(html, /<div class="table-wrap" tabindex="0" role="region" aria-label="a, b"><table class="table--stack"><thead>/, 'a table is named by its headers unless a label is given, two columns stack too, and its wrapper is a Tab stop for a keyboard to scroll it');
  assert.throws(() => md.render(new Marked({ gfm: true }).lexer('# No id\n'), {}), /has no id/, 'only headings of a lexed document can be rendered');
});

test('a name in a table cell may break after each underscore, and the text of the cell stays the name', () => {
  const { html } = md.render(lex('| Old | New |\n| - | - |\n| `a_b_c` | some_text `x_y` |\n\nA `d_e` outside.\n'), {});
  assert.match(html, /<td data-label="Old"><code>a_<wbr>b_<wbr>c<\/code><\/td>/);
  assert.match(html, /<td data-label="New">some_text <code>x_<wbr>y<\/code><\/td>/, 'the code of a cell, not the words around it');
  assert.match(html, /<p>A <code>d_e<\/code> outside\.<\/p>/, 'a table only: code elsewhere is left alone');
  assert.equal(plainText(/<td data-label="Old">.*?<\/td>/.exec(html)[0]), 'a_b_c', 'the break opportunities add no character to what a reader copies');
});

test('the documents render as Markdown, with their tables, logs and links', () => {
  const guide = md.render(real.slice('docs/getting-started.md'), { file: 'docs/getting-started.md', page: 'getting-started' });
  assert.match(guide.html, /&quot;Authorization&quot;: &quot;&lt;everything after Authorization= from step 1&gt;&quot;/, 'angle brackets and quotes are escaped in code');
  assert.ok((guide.html.match(/class="term term--log"/g) ?? []).length >= 2, 'the guide has its log excerpts');
  assert.match(guide.html, /<ol>\n<li><p>Download/, 'a numbered list renders with its items');
  assert.match(guide.html, /<ul>\n<li>Linux, the official server install script/, 'a bullet list nested in a numbered item');
  const config = md.render(real.slice('configuration'), { file: 'README.md', page: 'configuration' });
  assert.match(config.html, /<a href="\.\.\/scraping\/index\.html#a-word-on-the-bind-address">A word on the bind address<\/a>/, 'a link in a table cell goes through the rewriter');
  assert.ok((config.html.match(/data-label="What it does"/g) ?? []).length >= 15 && config.html.includes('<td data-label="Key"><code>Attribution.BurstTicks</code></td>'), 'every cell carries its column label');
  assert.match(config.html, /<table class="table--stack">/, 'a four column table stacks');
  const log = md.render(real.slice('CHANGELOG.md'), { file: 'CHANGELOG.md', page: 'changelog' });
  assert.match(log.html, /<h2 id="020---2026-09-29">\[0\.2\.0\] - 2026-09-29/);
  assert.match(log.html, /<h3 id="added-1">/, 'duplicate headings are numbered');
  assert.match(log.html, /<td data-label="Old name"><code>dotnet_<wbr>process_<wbr>memory_<wbr>working_<wbr>set<\/code><\/td>/, 'a two column table nested in a list item renders, its names breaking after each underscore');
  assert.match(log.html, /<table class="table--stack"><thead><tr><th scope="col">Old name/, 'and stacks on a phone, so the new name is never out of sight');
  const grafana = md.render(real.slice('contrib/grafana/README.md'), { file: 'contrib/grafana/README.md', page: 'dashboard' });
  assert.match(grafana.html, /<a href="http:\/\/localhost:3000\/d\/pulse-overview">http:\/\/localhost:3000\/d\/pulse-overview<\/a>\./, 'a bare localhost address is a live link, the period stays outside, no arrow');
  for (const html of [guide.html, config.html, log.html, grafana.html]) assert.doesNotMatch(html, /<(script|style|iframe)\b| style="|<!--/, 'nothing active comes out of a document');
});

test('links: external ones get an arrow, localhost ones do not, a link without a file is left alone', () => {
  assert.equal(md.inline('[a](https://example.org/x) [b](http://localhost:3000) [c](http://127.0.0.1:9464/metrics) [d](#here)'),
    '<a class="ext" href="https://example.org/x">a</a> <a href="http://localhost:3000">b</a> <a href="http://127.0.0.1:9464/metrics">c</a> <a href="#here">d</a>');
  assert.equal(md.inline('see [the guide](docs/getting-started.md) and `code`', { file: 'README.md', page: '' }), 'see <a href="getting-started/index.html">the guide</a> and <code>code</code>');
  assert.throws(() => md.render(lex('![x](a.png)\n'), {}), /an image/);
});

test('callouts are named by their first words and must be found exactly once', () => {
  assert.equal(page('otlp/index.html').match(/callout--security/g).length, 1);
  assert.equal(page('getting-started/index.html').match(/callout--security/g).length, 2);
  assert.deepEqual([...page('getting-started/index.html').matchAll(/<span class="ph">([^<]+)<\/span>/g)].map((m) => m[1]).slice(0, 3),
    ['user@your-server', 'https://otlp-gateway-&lt;region&gt;.grafana.net/otlp', '&lt;everything after Authorization= from step 1&gt;'], 'the three placeholders of the guide are marked');
  const tokens = real.slice('otlp');
  assert.throws(() => markCallout(tokens, 'No such paragraph', 'README.md'), /exactly once, found 0/);
  assert.throws(() => markCallout(tokens, 'A ', 'README.md'), /exactly once, found \d+/);
});

// ------------------------------------------------------------------ the pages
test('eleven pages and the 404 page, in the order of the registry', () => {
  const paths = [...built.files.keys()].filter((p) => p.endsWith('.html'));
  assert.deepEqual(paths, ['index.html', 'getting-started/index.html', 'configuration/index.html', 'scraping/index.html', 'metrics/index.html', 'attribution/index.html',
    'otlp/index.html', 'dashboard/index.html', 'alerts/index.html', 'changelog/index.html', 'development/index.html', '404.html']);
  assert.deepEqual([...built.files.keys()].filter((p) => !p.endsWith('.html')),
    ['assets/site.css', 'assets/site.js', 'assets/home.js', 'assets/search-index.js', 'assets/favicon.svg', 'assets/img/dashboard-overview.png', 'assets/img/dashboard-attribution.png', 'assets/img/pulse-pill-choice.webp', 'assets/img/pulse-pill-choice-still.webp', 'files/pulse-alerts.yml', 'files/pulse-overview-shared.json']);
  for (const p of ['getting-started', 'configuration', 'scraping', 'metrics', 'attribution', 'otlp', 'dashboard', 'alerts', 'changelog', 'development']) {
    assert.match(page(`${p}/index.html`), /<h1 id="/);
  }
});

test('every page has the policy, no marker, no inline style, no handler, no request to another origin', () => {
  for (const [path, html] of built.files) {
    if (!path.endsWith('.html')) continue;
    assert.match(html, /Content-Security-Policy/, path);
    assert.doesNotMatch(html, /<!--| style="| on[a-z]+="|<style|(src|srcset)="(https?:)?\/\//, path);
    assert.match(html, /img-src 'self'; connect-src 'none'/, `${path}: no data: source in the policy, since no page has a data: address`);
    assert.doesNotMatch(html, /\bundefined\b|\[object Object\]/, `${path}: a missing field must stop the build, not print its name`);
  }
  assert.doesNotMatch(page('assets/site.css'), /https?:\/\//);
  assert.doesNotMatch(page('assets/site.js') + page('assets/home.js'), /\bfetch\(|XMLHttpRequest|sendBeacon|WebSocket/);
});

test('the shell: top bar, sidebar, contents, breadcrumb, pager, source line, footer', () => {
  const metrics = page('metrics/index.html');
  assert.match(metrics, /<html lang="en" data-root="\.\.\/" data-page="metrics" data-regime="calm">/);
  assert.match(metrics, /<a class="skip" href="#main">Skip to content<\/a>/);
  assert.match(metrics, /<article class="article prose" id="main" data-source="README\.md">/, 'the skip link lands on the article, past the sidebar');
  assert.doesNotMatch(metrics, /<main id=/);
  assert.match(metrics, /<a href="index\.html" aria-current="page">Metrics<\/a>/);
  assert.match(metrics, /<nav class="side" aria-label="Documentation"><p class="side__h" id="side-start">Start<\/p>/, 'group labels are paragraphs, not headings');
  assert.doesNotMatch(metrics, /<nav class="side"[^>]*><h2/);
  assert.match(metrics, /<nav class="pager" aria-label="Previous and next page"><a class="pager__prev" href="\.\.\/scraping\/index\.html">Scraping<\/a><a class="pager__next" href="\.\.\/attribution\/index\.html">Attribution<\/a><\/nav>/);
  assert.match(metrics, /<p class="source">Source: <a class="ext" href="https:\/\/github\.com\/StratumServer\/Pulse\/blob\/main\/README\.md">README\.md<\/a><\/p>/);
  assert.match(metrics, /<nav class="crumb" aria-label="Breadcrumb"><ol><li><a href="\.\.\/index\.html">pulse<\/a><\/li><li aria-current="page">metrics<\/li><\/ol><\/nav>/);
  const attribution = page('attribution/index.html');
  assert.match(attribution, /<details class="acc toc-inline"><summary><span>On this page<\/span><\/summary><div class="acc__body"><ul><li><a href="#turning-it-on-without-a-restart">/);
  assert.match(attribution, /<nav class="toc" aria-label="On this page"><h2>On this page<\/h2><ul><li><a href="#turning-it-on-without-a-restart">/);
  assert.match(metrics, /<footer class="footer">[\s\S]*source_on_github[\s\S]*This product is an independent[\s\S]*There is no spoon\. Only metrics\./);
  // the top bar marks the entry of the page, Docs for the four pages that have none of their own, nothing for Home and Development
  const current = (path) => /<nav class="nav__links"[\s\S]*?<\/nav>/.exec(page(path))[0].match(/aria-current="page">([^<]+)</)?.[1] ?? null;
  assert.equal(current('getting-started/index.html'), 'Getting started');
  for (const p of ['configuration', 'scraping', 'attribution', 'otlp']) assert.equal(current(`${p}/index.html`), 'Docs', p);
  for (const [p, label] of [['metrics', 'Metrics'], ['dashboard', 'Dashboard'], ['alerts', 'Alerts'], ['changelog', 'Changelog']]) assert.equal(current(`${p}/index.html`), label, p);
  assert.equal(current('development/index.html'), null);
  assert.equal(current('index.html'), null);
  // Home comes before Getting started, nothing comes after Development, and Home has no pager at all
  assert.match(page('getting-started/index.html'), /pager__prev" href="\.\.\/index\.html">Home</);
  assert.doesNotMatch(page('development/index.html'), /pager__next/);
  assert.doesNotMatch(page('index.html'), /class="pager"|class="shell"|class="source"/);
  assert.match(page('index.html'), /<main id="main"><div class="home">/);
  // the contents are left out under three entries
  assert.doesNotMatch(page('scraping/index.html'), /class="toc"/);
  assert.match(page('scraping/index.html'), /<h2 id="a-word-on-the-bind-address">/);
});

test('two regimes: the rain switch and assets/home.js belong to the show pages only', () => {
  for (const path of ['index.html', '404.html']) {
    assert.match(page(path), /data-regime="show"/, path);
    assert.match(page(path), /<script defer src="assets\/home\.js\?v=[0-9a-f]{8}"><\/script>/, path);
    assert.equal(page(path).match(/data-slot="rain"/g).length, 3, `${path}: top bar, menu sheet, footer`);
  }
  for (const path of built.files.keys()) {
    if (!path.endsWith('.html') || ['index.html', '404.html'].includes(path)) continue;
    assert.match(page(path), /data-regime="calm"/, path);
    assert.doesNotMatch(page(path), /home\.js|data-slot="rain"/, path);
    assert.match(page(path), /<script defer src="\.\.\/assets\/site\.js\?v=[0-9a-f]{8}"><\/script>/, path);
  }
  assert.match(page('index.html'), /<link rel="stylesheet" href="assets\/site\.css\?v=[0-9a-f]{8}">/);
  assert.match(page('index.html'), /<title>Pulse \| Server metrics for Vintage Story<\/title>/);
  assert.match(page('metrics/index.html'), /<title>Metrics \| Pulse<\/title>/);
  assert.match(page('metrics/index.html'), /<link rel="canonical" href="https:\/\/stratumserver\.github\.io\/Pulse\/metrics\/">/);
});

test('404.html sits at the root whatever address was asked for: a base, a policy that allows it, no skip link', () => {
  const html = page('404.html');
  assert.match(html, /<base href="\/Pulse\/">/, 'the path of the site address, which base-uri \'self\' accepts on any host');
  assert.match(html, /base-uri 'self'/);
  assert.doesNotMatch(html, /class="skip"/, 'a fragment link would navigate to the base');
  assert.match(html, /<link rel="stylesheet" href="assets\/site\.css/, 'addresses are written from the root, the base makes them work at any depth');
  assert.match(html, /<a href="index\.html">Home<\/a>/);
  assert.match(html, /<a href="getting-started\/index\.html">Getting started<\/a>/);
  assert.match(html, /404: there is no page\./);
  for (const path of built.files.keys()) if (path.endsWith('index.html')) assert.match(page(path), /base-uri 'none'/, path);
  assert.doesNotMatch(page('index.html'), /<base /);
});

test('the boot script is the only inline script, allowed by its hash', () => {
  const hash = /script-src 'self' 'sha256-([^']+)'/.exec(page('index.html'))[1];
  assert.equal(hash, createHash('sha256').update(BOOT).digest('base64'));
  for (const path of built.files.keys()) if (path.endsWith('.html')) assert.deepEqual([...page(path).matchAll(/<script>([\s\S]*?)<\/script>/g)].map((m) => m[1]), [BOOT], path);
});

test('the typography of the calm pages is a token, the whole of Home stays monospace', () => {
  const css = page('assets/site.css');
  assert.match(css, /--font-mono: ui-monospace/);
  assert.match(css, /--font-prose: system-ui, -apple-system, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;/);
  assert.match(css, /html\[data-regime="calm"\][^{]*\{[^}]*var\(--font-prose\)/);
  assert.match(css, /^body \{[^}]*var\(--font-mono\)/m, 'the body is monospace, so Home is monospace throughout');
});

test('animations start under .js only, so motion never exists without the switch that stops it', () => {
  const css = page('assets/site.css');
  let seen = 0;
  for (const m of css.matchAll(/^([^@{}\n][^{}\n]*)\{[^}\n]*\banimation:\s*(?!\s|none)[^;}\n]+/gm)) { seen++; assert.match(m[1], /\.js /, `animation outside .js: ${m[1]}`); }
  assert.ok(seen >= 5, 'the hero sweep and the cursor are the animations there are');
});

test('the search index holds one entry per heading, and the text under a site-written heading is indexed with it', () => {
  const index = JSON.parse(/^window\.PULSE_SEARCH = (.*);\n$/s.exec(page('assets/search-index.js'))[1]);
  assert.ok(index.length > 30);
  assert.ok(index.every((e) => e.u && e.t && e.c && typeof e.x === 'string'));
  const bind = index.find((e) => e.u === 'scraping/index.html#a-word-on-the-bind-address');
  assert.equal(bind.t, 'A word on the bind address');
  assert.equal(bind.c, 'Scraping');
  assert.match(bind.x, /^The default binds loopback/);
  assert.ok(!index.some((e) => e.u === 'index.html' || e.u.startsWith('404')), 'Home and 404 are not indexed');
  const families = index.find((e) => e.u === 'metrics/index.html#families');
  assert.equal(families.t, 'Metric families');
  assert.match(families.x, /The metric families it serves:/);
  assert.ok(index.findIndex((e) => e.u.startsWith('getting-started/')) < index.findIndex((e) => e.u.startsWith('metrics/')), 'page order');
});

test('the build is deterministic: same inputs, same bytes', async () => {
  const again = await buildSite({ offline: true });
  assert.deepEqual([...again.files.keys()], [...built.files.keys()]);
  for (const [path, content] of built.files) assert.ok(Buffer.from(content).equals(Buffer.from(again.files.get(path))), path);
});

test('the screenshots keep the size the markup will say, and the shared dashboard keeps its placeholders', () => {
  const size = (path) => { const b = real.bytes(path); return [b.readUInt32BE(16), b.readUInt32BE(20)]; };
  assert.deepEqual(size('docs/assets/moddb/dashboard-overview.png'), [OVERVIEW.width, OVERVIEW.height]);
  assert.deepEqual(size('docs/assets/moddb/dashboard-attribution.png'), [ATTRIBUTION_SHOT.width, ATTRIBUTION_SHOT.height]);
  assert.ok(Buffer.from(page('assets/img/dashboard-overview.png')).equals(real.bytes('docs/assets/moddb/dashboard-overview.png')), 'copied byte for byte');
  assert.ok(page('files/pulse-overview-shared.json').toString().includes('${DS_PROMETHEUS}'), 'copied, never through a template');
});

test('the output directory is only emptied when it holds a previous build', () => {
  const dir = mkdtempSync(join(tmpdir(), 'pulse-site-'));
  try {
    const mine = join(dir, 'sources');
    mkdirSync(mine);
    writeFileSync(join(mine, 'build.mjs'), 'precious');
    assert.throws(() => writeOutput(mine, built.files), /does not hold a previous build/);
    assert.equal(readFileSync(join(mine, 'build.mjs'), 'utf8'), 'precious', 'nothing was deleted');
    assert.throws(() => writeOutput(join(mine, 'build.mjs'), built.files), /is a file/);
    const out = join(dir, 'dist');
    writeOutput(out, built.files);
    writeFileSync(join(out, 'stale.txt'), 'old');
    writeOutput(out, built.files);
    assert.equal(readFileSync(join(out, 'index.html'), 'utf8'), page('index.html'));
    assert.throws(() => readFileSync(join(out, 'stale.txt')), /ENOENT/, 'a previous build is emptied first');
  } finally { rmSync(dir, { recursive: true, force: true }); }
});

// ------------------------------------------------------------------ checks on the emitted pages
const doc = (body, head = '', up = '') => `<!doctype html><html lang="en"><head><meta http-equiv="Content-Security-Policy" content="x">${head}<script>${BOOT}</script><script defer src="${up}assets/site.js"></script></head><body>${body}</body></html>`;
const files = (html, extra = {}) => new Map([['index.html', html], ['assets/site.js', ''], ['other/index.html', doc('<h1 id="top">o</h1><h2 id="sec">s</h2>', '', '../')], ...Object.entries(extra)]);
const run = (html, extra) => checkPages(files(html, extra), [{ path: 'index.html' }, { path: 'other/index.html' }]);

test('checks on the emitted pages: what passes', () => {
  const ok = run(doc('<h1 id="a">t</h1><h2 id="b">u</h2><h3 id="c">v</h3><a href="other/index.html#sec">x</a><a href="#b">y</a><a href="https://example.org/">z</a><a href="other/index.html?pill=blue">q</a><img src="assets/x.png" alt="" width="1" height="1">',
    '<link rel="canonical" href="https://stratumserver.github.io/Pulse/">'), { 'assets/x.png': 'png' });
  assert.deepEqual(ok.stats, { pages: 2, links: 6, anchors: 2, entries: 0 });
  assert.deepEqual(ok.warnings, []);
});

test('checks on the emitted pages: what stops the build', () => {
  const bad = (body, re, extra, head) => assert.throws(() => run(doc(body, head), extra), re, body);
  bad('<h1 id="a">t</h1><a href="nope/index.html">x</a>', /does not resolve to a file/);
  bad('<h1 id="a">t</h1><a href="other/index.html#nope">x</a>', /no id of other\/index\.html/);
  bad('<h1 id="a">t</h1><a href="#nope">x</a>', /no id of index\.html/);
  bad('<h1 id="a">t</h1><a href="assets/site.js#x">x</a>', /not a page/);
  bad('<h1 id="a">t</h1><h2 id="a">u</h2>', /used twice/);
  bad('<h1 id="a">t</h1><h1 id="b">u</h1>', /2 <h1>/);
  bad('<h2 id="a">t</h2>', /skips/);
  bad('<h1 id="a">t</h1><h3 id="b">u</h3>', /skips: <h3> after <h1>/);
  bad('<h1 id="a">t</h1><img src="assets/x.png" width="1" height="1">', /without alt/, { 'assets/x.png': '' });
  bad('<h1 id="a">t</h1><img src="assets/x.png" alt="x" height="1">', /without width/, { 'assets/x.png': '' });
  bad('<h1 id="a">t</h1><div tabindex="2">x</div>', /positive tabindex/);
  bad('<h1 id="a">t</h1><!-- note -->', /HTML comment/);
  bad('<h1 id="a">t</h1><p style="color:red">x</p>', /style attribute/);
  bad('<h1 id="a">t</h1><button onclick="x()">x</button>', /inline event handler/);
  bad('<h1 id="a">t</h1><style>p{}</style>', /<style> element/);
  bad('<h1 id="a">t</h1><script>alert(1)</script>', /inline <script>/);
  bad('<h1 id="a">t</h1><script type="module">alert(1)</script>', /inline <script>/);
  bad('<h1 id="a">t</h1><SCRIPT>alert(1)</SCRIPT>', /inline <script>/);
  bad('<h1 id="a">t</h1><script data-src="x">alert(1)</script>', /inline <script>/);
  bad('<h1 id="a">t</h1><img src="data:image/png;base64,AAAA" alt="" width="1" height="1">', /scheme in src/);
  bad('<h1 id="a">t</h1><script src="assets/other.js"></script>', /not one of assets\/site\.js/);
  bad('<h1 id="a">t</h1><img src="https://example.org/x.png" alt="" width="1" height="1">', /scheme in src/);
  bad('<h1 id="a">t</h1><img srcset="https://example.org/x.png 2x" alt="" width="1" height="1">', /scheme in srcset/);
  bad('<h1 id="a">t</h1>', /scheme in href/, undefined, '<link rel="stylesheet" href="https://example.org/x.css">');
  bad('<h1 id="a">t</h1>', /<base>/, undefined, '<base href="https://example.org/">');
  assert.throws(() => run(doc('<h1 id="a">t</h1>').replace('http-equiv="Content-Security-Policy"', 'http-equiv="x"')), /no Content-Security-Policy/);
  const withHome = (html) => html.replace('<script defer src="assets/site.js"></script>', '<script defer src="assets/site.js"></script><script defer src="assets/home.js"></script>');
  const extra = [['assets/home.js', ''], ['assets/site.js', '']];
  assert.throws(() => checkPages(new Map([['index.html', withHome(doc('<h1 id="a">t</h1>'))], ...extra]), [{ path: 'index.html' }]), /not one of assets\/site\.js/, 'home.js belongs to the show pages');
  assert.doesNotThrow(() => checkPages(new Map([['index.html', withHome(doc('<h1 id="a">t</h1>'))], ...extra]), [{ path: 'index.html', show: true }]));
  assert.doesNotThrow(() => checkPages(new Map([['404.html', withHome(doc('<h1 id="a">t</h1><a href="other/index.html">x</a>', '<base href="/Pulse/">'))], ['other/index.html', doc('<h1 id="o">o</h1>', '', '../')], ...extra]),
    [{ path: '404.html', base: true, show: true }, { path: 'other/index.html' }]), 'a page with a base is written from the root');
});

test('the search index may only point at pages and ids that exist', () => {
  const index = (...entries) => ({ 'assets/search-index.js': `window.PULSE_SEARCH = ${JSON.stringify(entries)};\n` });
  const good = { u: 'other/index.html#sec', t: 'Section', c: 'Other', x: 'text' };
  assert.equal(run(doc('<h1 id="a">t</h1>'), index(good, { u: 'other/index.html', t: 'Other', c: 'Other', x: '' })).stats.entries, 2);
  assert.throws(() => run(doc('<h1 id="a">t</h1>'), index({ ...good, u: 'other/index.html#nope' })), /"Section" points at #nope, which is no id of other\/index\.html/);
  assert.throws(() => run(doc('<h1 id="a">t</h1>'), index({ ...good, u: 'missing/index.html#x' })), /which is not a page/);
  assert.throws(() => run(doc('<h1 id="a">t</h1>'), { 'assets/search-index.js': 'var x = [];' }), /is not "window\.PULSE_SEARCH/);
  assert.ok(built.stats.entries > 30, 'the real index is checked too');
});

test('size budgets print a warning and never stop the build', () => {
  const noise = Array.from({ length: 200000 }, (_, i) => Math.imul(i, 2654435761).toString(36)).join(' ');
  const out = run(doc('<h1 id="a">t</h1>'), { 'assets/site.js': noise });
  assert.equal(out.warnings.length, 1);
  assert.match(out.warnings[0], new RegExp(`assets/site\\.js is [\\d.]+ KB gzipped, over its budget of ${BUDGETS['assets/site.js']} KB`));
});
