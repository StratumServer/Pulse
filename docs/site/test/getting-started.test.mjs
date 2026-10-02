// The build rules of the Getting started page: the guide stays the single source, the page cuts and
// marks it, and the build stops with a sentence that says what moved when the guide
// stops matching. Each failing case feeds an altered copy of a document to the build.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { buildSite } from '../build.mjs';
import { textOf } from '../lib/md.mjs';
import { flat, loadSources } from '../lib/sources.mjs';

const FILE = 'docs/getting-started.md';
const real = loadSources();
const altered = (path, edit) => loadSources({ text: (p) => (p === path ? edit(real.text(p)) : real.text(p)) });
const swap = (from, to) => (text) => { assert.ok(text.includes(from), `the document no longer holds the text this case changes: ${from}`); return text.replace(from, to); };
const guide = (edit) => altered(FILE, edit);
// every message of the guide's rules starts with the same sentence, then says what moved
const failsRule = (sources, re) => assert.rejects(() => buildSite({ sources, offline: true }), (e) => {
  assert.ok(e.message.startsWith('docs/getting-started.md no longer matches the wizard rules: '), e.message);
  assert.match(e.message, re);
  return true;
});
const built = await buildSite({ offline: true });
const html = built.files.get('getting-started/index.html');
const page = built.pages.find((p) => p.slug === 'getting-started');
const { version, released } = built.data;
const decode = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&');
const count = (re, text = html) => (text.match(re) ?? []).length;

// ------------------------------------------------------------------ what the build writes, as the wizard reads it
test('the page is the module of the registry, with the root the wizard starts from', () => {
  assert.equal(page.title, 'Getting started');
  assert.equal(page.layout, 'docs');
  assert.deepEqual(page.source, [FILE]);
  assert.deepEqual(page.attrs, { class: 'gs', 'data-gs': '', 'data-gs-sig': '5.3.3.6.4' });
  assert.match(html, /<article class="article prose gs" id="main" data-source="docs\/getting-started\.md" data-gs data-gs-sig="5\.3\.3\.6\.4">/);
  const first = flat(textOf(real.slice(FILE).find((t) => t.type === 'paragraph'))).split(/(?<=\.)\s/)[0];
  assert.equal(page.description, first, 'the first sentence of the guide, whole');
  assert.ok(page.description.endsWith('.') && page.description.length < 160);
  assert.match(html, new RegExp(`<meta name="description" content="${first.slice(0, 40)}`));
});

test('the acts: four hosting situations, Step 1, one act per path, What next, then Troubleshooting that is no act', () => {
  const acts = [...html.matchAll(/<section class="act" data-act="(\w+)"([^>]*)>\n<p class="act__kicker" aria-hidden="true">([^<]+)<\/p>\n<h2 id="([^"]+)">([^<]+)<\/h2>/g)];
  assert.deepEqual(acts.map((m) => [m[1], m[3], m[5]]), [
    ['choose', 'choose_your_pill', 'Which path is yours'],
    ['install', 'wake_the_server', 'Step 1: install the mod on the server'],
    ['path', 'follow_the_white_rabbit', 'Path A: run Prometheus and Grafana yourself'],
    ['path', 'follow_the_white_rabbit', 'Path B: Grafana Cloud, if you would rather host nothing'],
    ['next', 'the_oracle', 'What next'],
  ]);
  assert.match(acts[2][2], /^ data-path="a" data-lede="Use this path if you are happy running two small programs yourself, on your own computer or on the server\."$/);
  assert.match(acts[3][2], /^ data-path="b" data-lede="Use this path if you do not want to run Prometheus or Grafana yourself, or your host will not let you\."$/);
  assert.match(html, /<p class="kicker" aria-hidden="true">Wake up, admin\.<\/p>\n<h1 id="getting-started-from-install-to-your-first-dashboard">/);
  assert.match(html, /<section class="trouble">\n<h2 id="troubleshooting">Troubleshooting&nbsp;<a class="anchor" href="#troubleshooting"/);
  assert.equal(count(/data-docker="engine"/g), 1);
  assert.equal(count(/data-docker="desktop"/g), 1);
  assert.match(html, /<section data-docker="engine">\n<h3 id="on-a-linux-server-or-pc">On a Linux server or PC<\/h3>/);
  assert.match(html, /<section data-docker="desktop">\n<h3 id="on-windows-or-macos-docker-desktop">On Windows or macOS \(Docker Desktop\)<\/h3>/);
});

test('the headings that become fold buttons carry no "#" link, the two others keep theirs', () => {
  for (const id of ['which-path-is-yours', 'step-1-install-the-mod-on-the-server', 'path-a-run-prometheus-and-grafana-yourself', 'path-b-grafana-cloud-if-you-would-rather-host-nothing', 'on-a-linux-server-or-pc', 'on-windows-or-macos-docker-desktop', 'what-next']) {
    assert.match(html, new RegExp(`<h[23] id="${id}">[^<]+</h[23]>`), `${id} is a bare heading`);
    assert.doesNotMatch(html, new RegExp(`href="#${id}"[^>]*class="anchor"|class="anchor" href="#${id}"`), `no link to ${id} inside its heading`);
  }
  for (const id of ['before-you-start', 'troubleshooting']) assert.match(html, new RegExp(`<h[23] id="${id}">[^<]+&nbsp;<a class="anchor" href="#${id}"`), id);
});

test('five numbered lists, 21 steps, the ids and the names the wizard announces', () => {
  const lists = [...html.matchAll(/<ol data-list="(\w+)" data-label="([^"]+)">/g)].map((m) => [m[1], m[2]]);
  assert.deepEqual(lists, [['install', 'Install the mod'], ['linux', 'Path A on Linux'], ['desktop', 'Path A with Docker Desktop'], ['cloud', 'Path B'], ['import', 'Path B, dashboard import']]);
  const ids = [...html.matchAll(/<li id="((?:install|linux|desktop|cloud|import)-\d+)"/g)].map((m) => m[1]);
  assert.deepEqual(ids, ['install-1', 'install-2', 'install-3', 'install-4', 'install-5', 'linux-1', 'linux-2', 'linux-3', 'desktop-1', 'desktop-2', 'desktop-3', 'cloud-1', 'cloud-2', 'cloud-3', 'cloud-4', 'cloud-5', 'cloud-6', 'import-1', 'import-2', 'import-3', 'import-4']);
  assert.deepEqual([...html.matchAll(/<li id="([a-z]+-\d)"[^>]* data-later[ >]/g)].map((m) => m[1]), ['linux-3', 'desktop-3'], 'the two teardown steps are shown and never counted');
  assert.equal(count(/<ul data-list="hosts">/g), 1);
  assert.deepEqual([...html.matchAll(/<li data-where="(\w+)">/g)].map((m) => m[1]), ['server', 'home', 'panel', 'container']);
  assert.deepEqual([...html.matchAll(/<li id="(trouble-\d)">/g)].map((m) => m[1]), ['trouble-1', 'trouble-2', 'trouble-3', 'trouble-4', 'trouble-5', 'trouble-6']);
  assert.match(html, /<ul data-list="trouble">/);
});

test('"Not working?": the nine rows, step to troubleshooting entries, in this order', () => {
  const rows = Object.fromEntries([...html.matchAll(/<li id="([a-z]+-\d)"[^>]* data-trouble="([\d ]+)"/g)].map((m) => [m[1], m[2]]));
  assert.deepEqual(rows, { 'install-4': '2', 'install-5': '6 2', 'linux-1': '1', 'linux-2': '3 4 1', 'desktop-1': '1', 'desktop-2': '3 4 1', 'cloud-5': '5', 'cloud-6': '5', 'import-4': '4' });
  // when the dashboard does not open at all, entry 1 is the one that matches: it comes last under the two steps that end there
  const before = [...html.matchAll(/<li id="([a-z]+-\d)"[^>]* data-before="([^"]+)"/g)].map((m) => [m[1], m[2]]);
  assert.deepEqual(before, [['linux-1', 'before-you-start'], ['desktop-1', 'before-you-start']], 'the two commands that fail outright also offer the guide\'s own section');
  assert.match(html, /<h3 id="before-you-start">/, 'and the id they point at exists');
  assert.deepEqual([...new Set(Object.values(rows).flatMap((v) => v.split(' ')))].sort(), ['1', '2', '3', '4', '5', '6'], 'every entry is reachable from at least one step');
});

test('where a command runs, in the bar of the code block, whole', () => {
  const blocks = [...html.matchAll(/<span class="code__label">sh<\/span>(?:<span class="code__where">([^<]+)<\/span>)?<\/div><pre><code>([^\n<]*)/g)].map((m) => [m[2], m[1]]);
  const expected = (first) => (/^curl /.test(first) ? 'run on the game server' : /^git clone /.test(first) ? "run on the game server's machine" : /^ssh -L /.test(first) ? 'run on your own computer'
    : /^docker compose /.test(first) ? "run on the game server's machine, in contrib/grafana" : undefined);
  assert.equal(blocks.length, 7, 'seven sh fences');
  for (const [first, where] of blocks) assert.equal(where, expected(first), first);
  assert.equal(count(/class="code__where"/g), 7, 'every one of them says where it runs');
});

test('the tunnel command, the helper block, their notes and the reasons path A is off are marked once each', () => {
  assert.equal(count(/<div class="code" data-lang="sh" data-tunnel>/g), 1);
  assert.equal(count(/<div class="code" data-lang="json" data-helper>/g), 1);
  assert.match(html, /<span class="code__label">ModConfig\/pulse-otlp\.json<\/span>/, 'the helper output is labelled with the file it is for');
  assert.equal(count(/<li id="cloud-1" data-helper-note>/g), 1);
  assert.match(html, /<template data-why="panel">Skip straight to path B[^<]*<\/template>/);
  assert.match(html, /<template data-why="container">Path A cannot reach it: <code>127\.0\.0\.1<\/code>[^<]*<\/template>/);
  const why = (name) => decode(new RegExp(`<template data-why="${name}">([\\s\\S]*?)</template>`).exec(html)[1].replace(/<\/?code>/g, '`'));
  const bullets = real.slice(FILE).find((t) => t.type === 'list' && !t.ordered).items.map((i) => flat(i.text));
  assert.ok(bullets[2].endsWith(why('panel')) && bullets[3].endsWith(why('container')), 'each reason runs from its phrase to the end of its bullet, word for word');
  assert.equal(count(/<template /g), 2);
  // the three placeholders, and only inside code blocks with a language (the log excerpt keeps its text as written)
  const ph = [...html.matchAll(/<span class="ph">([^<]+)<\/span>/g)].map((m) => decode(m[1]));
  assert.deepEqual(ph, ['user@your-server', 'https://otlp-gateway-<region>.grafana.net/otlp', '<everything after Authorization= from step 1>']);
  assert.equal(count(/callout--security/g), 2);
});

test('the version: the prose keeps its x.x.x, the download lines name the released zips and the dashboard file', () => {
  assert.match(html, /Download <code>pulse_x\.x\.x\.zip<\/code> \(replace <code>x\.x\.x<\/code> with the version you downloaded\)/, 'the sentence is the guide\'s own');
  const dl = [...html.matchAll(/<p class="dl"><a class="btn btn--sm" href="([^"]+)"( download)?>([^<]+)<\/a> <span>([^<]+)<\/span><\/p>/g)].map((m) => [m[1], !!m[2], m[3], m[4]]);
  const base = `https://github.com/StratumServer/Pulse/releases/download/v${version}`;
  assert.match(version, /^\d+\.\d+\.\d+$/);
  assert.deepEqual(dl, [
    [`${base}/pulse_${version}.zip`, false, `download pulse_${version}.zip`, `version ${version}, released ${released}`],
    [`${base}/pulseotlp_${version}.zip`, false, `download pulseotlp_${version}.zip`, `version ${version}, released ${released}`],
    ['../files/pulse-overview-shared.json', true, 'download pulse-overview-shared.json', 'same file as the GitHub link above'],
  ]);
  assert.ok(built.files.has('files/pulse-overview-shared.json'), 'the file the line points at is shipped');
  assert.match(html, /<li id="install-1"><p>Download[\s\S]*?<p class="dl">[\s\S]*?<\/li>\n<li id="install-2">/, 'the line is the last thing in its step');
  assert.match(html, /<li id="cloud-2">[\s\S]*?<p class="dl">[\s\S]*?<\/li>\n<li id="cloud-3">/);
  assert.match(html, /<li id="import-2">[\s\S]*?use that page&#39;s download button[\s\S]*?<p class="dl">[\s\S]*?<\/li>\n<li id="import-3">/, 'under the guide\'s own link to the same file');
});

test('the picture of the dashboard sits in the three steps that end on it', () => {
  const pics = [...html.matchAll(/<li id="([a-z]+-\d)"[^>]*>(?:(?!<li id=)[\s\S])*?<figure class="shot"><img src="([^"]+)" width="(\d+)" height="(\d+)" loading="lazy" decoding="async" alt="([^"]+)"><figcaption>([^<]+)<\/figcaption><\/figure>/g)];
  assert.deepEqual(pics.map((m) => m[1]), ['linux-2', 'desktop-2', 'import-4']);
  for (const m of pics) {
    assert.deepEqual([m[2], m[3], m[4]], ['../assets/img/dashboard-overview.png', '1880', '629']);
    assert.equal(m[5], 'Grafana dashboard: players, uptime, tick rate and tick time at a glance, plus tick health graphs, from a test server with three scripted players and about 120 chickens');
    assert.equal(decode(m[6]), 'The "Pulse server overview" dashboard, here on a test server with three players and about 120 chickens. Yours shows your own numbers.');
  }
  assert.ok(built.files.has('assets/img/dashboard-overview.png'));
});

test('"What next": four links, each with a sentence that is in its document, closed with a full stop', () => {
  const items = [...html.matchAll(/<li><a href="([^"]+)"><b>([^<]+)<\/b><\/a>(?: <span class="next__tag" aria-hidden="true">(\w+)<\/span>)?<br>([^\n]*)<\/li>/g)].map((m) => [m[1], m[2], m[3], decode(m[4]).replace(/<\/?code>/g, '`')]);
  assert.deepEqual(items, [
    ['../dashboard/index.html', 'Dashboard', undefined, 'The dashboard covers every metric family the mod serves, grouped into rows.'],
    ['../attribution/index.html', 'Attribution', undefined, 'Tick busy time tells you the server is working hard. Attribution tells you what it is working on.'],
    ['../alerts/index.html', 'Alerts', 'deja_vu', 'Eleven rules across tick health, engine warnings, log errors, endpoint availability, the worldgen queue and per-mod attribution.'],
    ['../configuration/index.html', 'Install and configuration', undefined, 'Pulse and the OTLP mod each keep their settings in their own file under `ModConfig/`, written with their defaults on first boot.'],
  ]);
  const raw = (file) => flat(real.docs[file].tokens.map((t) => t.raw).join(''));
  const sources = ['contrib/grafana/README.md', 'README.md', 'contrib/alerts/README.md', 'README.md'];
  items.forEach(([, label, , sentence], i) => assert.equal(raw(sources[i]).split(sentence.slice(0, -1)).length - 1, 1, `${label}: the words are in ${sources[i]}, once`));
});

test('without script there is no control and nothing to hide: the page is the whole guide, in order', () => {
  assert.doesNotMatch(html, /<(button|input|select|textarea|form)\b/, 'a control that needs script is inserted by script');
  assert.doesNotMatch(html, /<[^>]+ (style|on[a-z]+)=|<!--/);
  // every block of the guide, in the order of the guide, appears in the page: nothing reworded, moved or split
  const squeeze = (s) => s.replace(/\s+/g, '');
  const body = squeeze(decode(html.slice(html.indexOf('<h1 ')).replace(/<(script|template)[\s\S]*?<\/\1>/g, '').replace(/<[^>]+>/g, '')));
  let at = 0, blocks = 0;
  const walk = (tokens) => {
    for (const t of tokens) {
      if (['heading', 'paragraph', 'text', 'code'].includes(t.type)) {
        const want = squeeze(t.type === 'code' ? t.text : textOf(t));
        if (want) { const hit = body.indexOf(want, at); assert.ok(hit >= 0, `the guide's ${t.type} is not in the page, in order: ${want.slice(0, 60)}`); at = hit + want.length; blocks++; }
      }
      walk(t.items ?? []);
      if (t.type !== 'paragraph' && t.type !== 'text' && t.type !== 'heading') walk(t.tokens ?? []);
    }
  };
  walk(real.slice(FILE));
  assert.ok(blocks > 80, `${blocks} blocks of the guide were found in the page, in order`);
});

test('the search finds each step and each troubleshooting entry by itself, after the heading it sits under', () => {
  const find = (id) => page.search.find((s) => s.id === id);
  assert.deepEqual([find('install-4').title, find('linux-2').title, find('desktop-3').title, find('cloud-6').title, find('import-1').title],
    ['Install the mod, step 4', 'Path A on Linux, step 2', 'Path A with Docker Desktop, step 3', 'Path B, step 6', 'Path B, dashboard import, step 1']);
  assert.ok(find('install-4').text.length > 60 && !/<|&lt;p/.test(find('install-4').text), 'the text of the step, as plain text');
  assert.match(find('trouble-2').title, /^Pulse's own port, 9464/);
  assert.match(find('trouble-1').title, /^docker compose up -d succeeds, but/, 'the symptom, as plain text: no back-ticks, no markup');
  assert.equal(page.search.filter((s) => /^(install|linux|desktop|cloud|import|trouble)-\d$/.test(s.id)).length, 27);
  const order = page.search.map((s) => s.id);
  assert.ok(order.indexOf('step-1-install-the-mod-on-the-server') < order.indexOf('install-1') && order.indexOf('install-5') < order.indexOf('path-a-run-prometheus-and-grafana-yourself'), 'document order');
  assert.ok(order.indexOf('what-next') < order.indexOf('troubleshooting') && order.indexOf('troubleshooting') < order.indexOf('trouble-1'));
  assert.deepEqual(page.toc.map((e) => e.id), ['which-path-is-yours', 'step-1-install-the-mod-on-the-server', 'path-a-run-prometheus-and-grafana-yourself', 'before-you-start', 'on-a-linux-server-or-pc',
    'on-windows-or-macos-docker-desktop', 'path-b-grafana-cloud-if-you-would-rather-host-nothing', 'what-next', 'troubleshooting']);
  const index = JSON.parse(/^window\.PULSE_SEARCH = (.*);\n$/s.exec(built.files.get('assets/search-index.js').toString())[1]);
  assert.ok(index.some((e) => e.u === 'getting-started/index.html#install-4' && e.t === 'Install the mod, step 4' && e.c === 'Getting started'));
});

test('the attributes the wizard reads are the ones the page writes, both ways', () => {
  const src = ['wizard.js', 'helper.js'].map((f) => readFileSync(new URL(`../js/${f}`, import.meta.url), 'utf8')).join('\n');
  const camel = (name) => name.slice(5).replace(/-(\w)/g, (m, c) => c.toUpperCase());
  const contract = ['data-gs', 'data-gs-sig', 'data-act', 'data-path', 'data-lede', 'data-docker', 'data-list', 'data-label', 'data-where', 'data-trouble', 'data-before', 'data-later', 'data-tunnel', 'data-helper', 'data-helper-note', 'data-why'];
  for (const attr of contract) {
    assert.match(html, new RegExp(`[ <]${attr}[ =>]`), `${attr} is written by the page`);
    assert.ok(src.includes(`[${attr}`) || src.includes(`dataset.${camel(attr)}`), `${attr} is read by the wizard`);
  }
  // and the ids it builds on: the step ids, the entries, the headings it makes buttons of
  for (const id of ['trouble-1', 'what-next', 'which-path-is-yours']) assert.match(html, new RegExp(`id="${id}"`));
});

test('the script the page loads holds the wizard, its state rules and the helper', () => {
  const js = built.files.get('assets/site.js');
  for (const m of ['wizard.js', 'wizard-state.js', 'helper.js']) assert.match(js, new RegExp(`// ---- js/${m.replace('.', '\\.')}\\n`), `${m} is joined into assets/site.js`);
});

// ------------------------------------------------------------------ the build stops when the guide stops matching, and says what moved
test('the guide still builds when only its words change: the page follows the guide', async () => {
  const edited = await buildSite({ sources: guide(swap('Pulse needs Vintage Story 1.22 or newer.', 'Pulse needs a recent Vintage Story.')), offline: true });
  assert.match(edited.files.get('getting-started/index.html'), /Pulse needs a recent Vintage Story\./);
  const wrapped = await buildSite({ sources: altered('README.md', swap('Tick busy time tells you the server is working hard. Attribution', 'Tick busy time tells you the server is working\nhard. Attribution')), offline: true });
  assert.match(wrapped.files.get('getting-started/index.html'), /Tick busy time tells you the server is working hard\. Attribution tells you what it is working on\./, 'a quoted sentence wrapped over two lines is still found');
});

test('"Which path is yours": four bullets, each opening with bold text that names its situation', async () => {
  await failsRule(guide(swap('Use path B instead.\n', 'Use path B instead.\n- **A fifth thing.** More.\n')), /"Which path is yours" has 5 bullets, the rules say 4/);
  await failsRule(guide(swap('**A home PC running the game server, on Windows, macOS or Linux.**', 'A home PC running the game server, on Windows, macOS or Linux.')), /bullet 2 of "Which path is yours" should open with bold text naming "home PC"/);
  await failsRule(guide(swap('Skip straight to path B', 'Go to path B')), /bullet 3 of "Which path is yours" no longer contains "Skip straight to path B"/);
  await failsRule(guide(swap('cannot reach it:', 'cannot get to it:')), /bullet 4 of "Which path is yours" no longer contains "Path A cannot reach it:"/);
});

test('a numbered list that gains or loses an item stops the build, with the list and the counts', async () => {
  await failsRule(guide(swap('\nThis assumes Pulse is running on the same machine', '4. One more step.\n\nThis assumes Pulse is running on the same machine')), /the list "linux" \("On a Linux server or PC"\) has 4 items, the rules say 3/);
  await failsRule(guide(swap('\nPulse also wrote its own config file', '6. Extra.\n\nPulse also wrote its own config file')), /the list "install" \("Step 1: install the mod on the server"\) has 6 items, the rules say 5/);
  await failsRule(guide(swap('Once the numbers are flowing, bring in the dashboard:\n\n1. ', 'Once the numbers are flowing, bring in the dashboard:\n\n- ')), /the list "import" \("Path B: Grafana Cloud, if you would rather host nothing", after the first\) has 3 items, the rules say 4/);
  await failsRule(guide((t) => t.replace(/(bring in the dashboard:\n\n)([\s\S]*?)(\n\nEvery panel on this dashboard)/, (m, head, items, tail) => head + items.replace(/^\d\. /gm, '- ') + tail)), /"Path B: Grafana Cloud, if you would rather host nothing" should hold two numbered lists, found 1/);
  await failsRule(guide(swap('- You need Docker installed.', '- Something first.\n- You need Docker installed.')), /"Before you start" should have 5 bullets/);
});

test('the steps the wizard builds on: the zips, the log line, the tunnel, the teardown, the helper block', async () => {
  await failsRule(guide(swap('Download `pulse_x.x.x.zip` (replace `x.x.x` with the version you downloaded)', 'Download the Pulse zip')), /install-1 no longer names pulse_x\.x\.x\.zip/);
  await failsRule(guide(swap('Download `pulseotlp_x.x.x.zip` next to `pulse_x.x.x.zip`', 'Download the OTLP zip next to the first')), /cloud-2 no longer names pulseotlp_x\.x\.x\.zip/);
  assert.throws(() => guide(swap('   ```\n   [pulse] Pulse serving metrics', '   ```sh\n   [pulse] Pulse serving metrics')), /docs\/getting-started\.md: the log excerpt that starts "\[pulse\] Pulse serving metrics on " is gone/, 'the loader knows the log excerpts by name, before this page looks for the one of install-4');
  await failsRule(guide(swap('ssh -L 3000', 'ssh -N -L 3000')), /linux-2 should be: a paragraph, the ssh -L command, a paragraph/);
  await failsRule(guide(swap('3. When you are done, stop both programs', '3. Stop both programs')), /linux-3 no longer starts with "When you are done"/);
  await failsRule(guide(swap('`Authorization=`, but change any', 'the prefix, but change any')), /cloud-1 no longer explains Authorization=/);
  await failsRule(guide(swap('"Endpoint": "https://otlp-gateway-<region>.grafana.net/otlp",', '"Endpoint": "https://example.org/otlp",')), /the json block of cloud-4 no longer holds the two placeholders the helper fills/);
  await failsRule(guide(swap('Leave every other key', '```json\n   {}\n   ```\n\n   Leave every other key')), /expected exactly one json block in cloud-4, found 2/);
});

test('the path sentences, the troubleshooting entries, the labels that say where a command runs', async () => {
  await failsRule(guide(swap('Use this path if you are happy running', 'Pick this path if you are happy running')), /"Path A: run Prometheus and Grafana yourself" no longer opens with a sentence that starts "Use this path if "/);
  await failsRule(guide(swap("- **Pulse's own port, 9464, will not bind.**", "- **Pulse's metrics port will not bind.**")), /troubleshooting entry 2 should open with bold text that starts "Pulse's own port, 9464"/);
  await failsRule(guide((t) => t.slice(0, t.indexOf('- **Nothing outside the server can reach'))), /"Troubleshooting" has 5 entries, the rules say 6/);
  await failsRule(guide(swap('3. Start (or restart) the server.\n', '3. Start (or restart) the server.\n\n   ```sh\n   curl http://example.org\n   ```\n')), /1 sh fence\(s\) should start with \/\^curl \/, found 2/);
  await failsRule(guide(swap('docker compose down\n', 'docker  compose down\n')), /4 sh fence\(s\) should start with \/\^docker compose \/, found 3/);
  await failsRule(guide(swap('user@your-server', 'user@host')), /the placeholder user@your-server is no longer in a code block/);
  await failsRule(guide(swap('Both Grafana and Prometheus are set to listen on `127.0.0.1` only', 'Grafana and Prometheus listen on `127.0.0.1` only')), /the paragraph that starts "Both Grafana and Prometheus.*exactly once, found 0/);
});

test('"What next": a quoted sentence that left its document, or that appears twice, stops the build', async () => {
  await failsRule(altered('contrib/alerts/README.md', swap('Eleven rules across tick health', 'Twelve rules across tick health')), /the sentence quoted for "Alerts" \("Eleven rules across tick health.*should be in contrib\/alerts\/README\.md exactly once, found 0/);
  await failsRule(altered('README.md', swap('Tick busy time tells you the server is working hard.', 'Tick busy time tells you the server is busy.')), /the sentence quoted for "Attribution".*README\.md exactly once, found 0/);
  await failsRule(altered('contrib/grafana/README.md', (t) => `${t}\nThe dashboard covers every metric family the mod serves, grouped into rows, again.\n`), /the sentence quoted for "Dashboard".*exactly once, found 2/);
});
