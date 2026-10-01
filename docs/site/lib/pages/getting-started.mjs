// Getting started. The guide (docs/getting-started.md) stays the single source: this module cuts it by
// its headings and its lists into the parts the wizard folds, marks them through the token hooks of
// lib/md.mjs, and adds the few things only the site knows (the released version, the picture of the
// dashboard, "What next"). It never rewrites a sentence of the guide and the guide carries no marker.
// Without script the page is the whole guide in order. js/wizard.js reads what is written here and
// nothing else: test/getting-started.test.mjs checks that both sides agree on the attributes.
//
// This is the only place that knows the shape of the guide. When the guide stops matching, the build
// stops with a sentence that says what moved, and the rules below are what to update with it.
import { OVERVIEW } from '../../content/home.mjs';
import { REPO } from '../layout.mjs';
import { markCallout, textOf } from '../md.mjs';
import { flat, plain } from '../sources.mjs';

const FILE = 'docs/getting-started.md';
const SLUG = 'getting-started';

const H2 = ['Which path is yours', 'Step 1: install the mod on the server', 'Path A: run Prometheus and Grafana yourself', 'Path B: Grafana Cloud, if you would rather host nothing', 'Troubleshooting'];
const H3_PATH_A = ['Before you start', 'On a Linux server or PC', 'On Windows or macOS (Docker Desktop)'];
// the headings that become fold buttons carry no "#" link: a link inside a button is invalid and would be read as part of its name
const NO_ANCHOR = [H2[0], H2[1], H2[2], H2[3], H3_PATH_A[1], H3_PATH_A[2]];

const HOSTS = [['server', 'Linux server'], ['home', 'home PC'], ['panel', 'panel-only host'], ['container', 'inside a container']];
const WHY_NOT_A = { panel: 'Skip straight to path B', container: 'Path A cannot reach it:' };   // the reason shown on the blue pill runs from here to the end of the bullet
// the five numbered lists: [items, the name used in announcements]. Their item counts, joined, are the signature the stored progress is keyed on.
const STEP_LISTS = { install: [5, 'Install the mod'], linux: [3, 'Path A on Linux'], desktop: [3, 'Path A with Docker Desktop'], cloud: [6, 'Path B'], import: [4, 'Path B, dashboard import'] };
const LATER = ['linux-3', 'desktop-3'];                      // "When you are done": shown, never counted
// "Not working?": the troubleshooting entries offered under a step, by position. The guide does not link its steps to its entries: these nine rows are an editorial judgement.
// The steps of the dashboard (linux-2, desktop-2) also offer entry 1: when the page does not open at all, that is the one that matches.
const TROUBLE = { 'install-4': [2], 'install-5': [6, 2], 'linux-1': [1], 'linux-2': [3, 4, 1], 'desktop-1': [1], 'desktop-2': [3, 4, 1], 'cloud-5': [5], 'cloud-6': [5], 'import-4': [4] };
const BEFORE = ['linux-1', 'desktop-1'];                    // a command that fails outright also points at the guide's "Before you start" section
const TROUBLE_ENTRIES = ['`docker compose up -d` succeeds', "Pulse's own port, 9464", 'Grafana opens, but', 'Some panels are empty', 'Path B: numbers never show up', 'Nothing outside the server can reach'];
const PLACEHOLDERS = ['user@your-server', 'https://otlp-gateway-<region>.grafana.net/otlp', '<everything after Authorization= from step 1>'];
const CALLOUTS = ['Both Grafana and Prometheus are set to listen on `127.0.0.1` only', '`pulse-otlp.json` now holds a credential in plain text.'];
// where a command runs, by its first words (an sh fence): [pattern, label, how many fences must match]. Path A runs on one
// machine, the one that runs the game server: the clone says so, and the compose commands say "that same machine".
const WHERE = [[/^curl /, 'run on the game server', 1], [/^git clone /, 'run on the machine that runs the game server', 1], [/^ssh -L /, 'run on your own computer', 1],
  [/^docker compose /, 'run on that same machine, in the contrib/grafana folder', 4]];
const SEE_DASHBOARD = ['linux-2', 'desktop-2', 'import-4'];   // the steps that end on the dashboard get its picture
// "What next": four links, each with a sentence quoted from a document. The build checks that the words are still there
// (without the final full stop, white space collapsed) and the page closes them with one.
const NEXT = [
  { label: 'Dashboard', slug: 'dashboard', file: 'contrib/grafana/README.md', quote: 'The dashboard covers every metric family the mod serves, grouped into rows' },
  { label: 'Attribution', slug: 'attribution', file: 'README.md', quote: 'Tick busy time tells you the server is working hard. Attribution tells you what it is working on' },
  { label: 'Alerts', slug: 'alerts', file: 'contrib/alerts/README.md', tag: 'deja_vu', quote: 'Eleven rules across tick health, engine warnings, log errors, endpoint availability, the worldgen queue and per-mod attribution' },
  { label: 'Install and configuration', slug: 'configuration', file: 'README.md', quote: 'Pulse and the OTLP mod each keep their settings in their own file under `ModConfig/`, written with their defaults on first boot' },
];

// Words the site writes on this page, apart from the wizard's own (js/wizard.js, js/helper.js). The film
// references follow the ModDB listing: the white rabbit is the guide walk, the Oracle is what comes after.
const WORDS = {
  kicker: 'Wake up, admin.', choose: 'choose_your_pill', install: 'wake_the_server', path: 'follow_the_white_rabbit', next: 'the_oracle',
  whatNext: 'What next',
  download: (file) => `download ${file}`, released: (version, date) => `version ${version}, released ${date}`,
  sameFile: 'same file as the GitHub link above',
  caption: 'The "Pulse server overview" dashboard, here on a test server with three players and about 120 chickens. Yours shows your own numbers.',
};

const fail = (why) => { throw new Error(`${FILE} no longer matches the wizard rules: ${why}`); };
const must = (ok, why) => { if (!ok) fail(why); };
const only = (list, what) => { must(list.length === 1, `expected exactly one ${what}, found ${list.length}`); return list[0]; };
const blocksOf = (item) => item.tokens.filter((t) => t.type !== 'space');
const isText = (t) => t.type === 'paragraph' || t.type === 'text';
const listsIn = (tokens, ordered) => tokens.filter((t) => t.type === 'list' && t.ordered === ordered);
const walk = (tokens, fn) => { for (const t of tokens) { fn(t); walk(t.items ?? [], fn); walk(t.tokens ?? [], fn); } };
/** Runs of top-level tokens, each starting at a heading of `depth` (the run before the first heading is under ''). */
const cut = (tokens, depth) => {
  const out = { '': [] };
  let key = '';
  for (const t of tokens) {
    if (t.type === 'heading' && t.depth === depth) { key = t.text; out[key] = []; }
    out[key].push(t);
  }
  return out;
};

export default function gettingStarted({ docs, slice, render, data, pageUrl, assetUrl, inline, esc }) {
  const tokens = slice(FILE);
  const h1 = tokens[0];
  must(h1.type === 'heading' && h1.depth === 1, 'the document no longer opens with its H1');
  const byH2 = cut(tokens, 2);
  for (const h of H2) must(byH2[h], `the section "${h}" is gone`);
  const byH3 = cut(byH2[H2[2]], 3);
  for (const h of H3_PATH_A) must(byH3[h], `the section "${h}" is gone from Path A`);
  const numbered = (name, tokensIn, what) => {
    const list = only(listsIn(tokensIn, true), `numbered list under ${what}`);
    const [count, label] = STEP_LISTS[name];
    must(list.items.length === count, `the list "${name}" (${what}) has ${list.items.length} items, the rules say ${count}`);
    list.attrs = { 'data-list': name, 'data-label': label };
    list.items.forEach((item, i) => {
      const id = `${name}-${i + 1}`;
      item.attrs = { id };
      if (LATER.includes(id)) { must(item.text.startsWith('When you are done'), `${id} no longer starts with "When you are done"`); item.attrs['data-later'] = ''; }
      if (TROUBLE[id]) item.attrs['data-trouble'] = TROUBLE[id].join(' ');
      if (BEFORE.includes(id)) item.attrs['data-before'] = byH3[H3_PATH_A[0]][0].id;
    });
    return list;
  };
  const addTo = (item, html) => { item.append = (item.append ?? '') + html; };

  // ---------------------------------------------------------------- the version, the picture, the download lines
  const { version, released } = data;
  const zip = (mod) => `${REPO}/releases/download/v${version}/${mod}_${version}.zip`;
  const downloadLine = (href, file, note, extra = '') => `<p class="dl"><a class="btn btn--sm" href="${href}"${extra}>${esc(WORDS.download(file))}</a> <span>${esc(note)}</span></p>\n`;
  const zipLine = (mod) => downloadLine(zip(mod), `${mod}_${version}.zip`, WORDS.released(version, released));
  const figure = `<figure class="shot"><img src="${assetUrl(SLUG, OVERVIEW.src)}" width="${OVERVIEW.width}" height="${OVERVIEW.height}" loading="lazy" decoding="async" alt="${esc(OVERVIEW.alt)}"><figcaption>${esc(WORDS.caption)}</figcaption></figure>\n`;

  // ---------------------------------------------------------------- act 1: "Which path is yours"
  const hosts = only(listsIn(byH2[H2[0]], false), `bullet list under "${H2[0]}"`);
  must(hosts.items.length === HOSTS.length, `"${H2[0]}" has ${hosts.items.length} bullets, the rules say ${HOSTS.length}`);
  hosts.attrs = { 'data-list': 'hosts' };
  const why = {};
  hosts.items.forEach((item, i) => {
    const [id, keyword] = HOSTS[i], lead = blocksOf(item)[0]?.tokens?.[0];
    must(lead?.type === 'strong' && plain(lead).includes(keyword), `bullet ${i + 1} of "${H2[0]}" should open with bold text naming "${keyword}"`);
    item.attrs = { 'data-where': id };
    if (WHY_NOT_A[id]) {
      const text = flat(item.text), at = text.indexOf(WHY_NOT_A[id]);
      must(at > 0, `bullet ${i + 1} of "${H2[0]}" no longer contains "${WHY_NOT_A[id]}"`);
      why[id] = inline(text.slice(at), { file: FILE, page: SLUG });
    }
  });

  // ---------------------------------------------------------------- act 2: Step 1
  const install = numbered('install', byH2[H2[1]], `"${H2[1]}"`);
  must(install.items[0].text.includes('`pulse_x.x.x.zip`'), 'install-1 no longer names pulse_x.x.x.zip');
  must(blocksOf(install.items[3]).some((t) => t.type === 'code' && !t.lang), 'install-4 no longer holds the log line in a fence without a language');
  addTo(install.items[0], zipLine('pulse'));

  // ---------------------------------------------------------------- act 3, blue: Path A
  must(only(listsIn(byH3[H3_PATH_A[0]], false), `bullet list under "${H3_PATH_A[0]}"`).items.length === 5, `"${H3_PATH_A[0]}" should have 5 bullets`);
  const linux = numbered('linux', byH3[H3_PATH_A[1]], `"${H3_PATH_A[1]}"`);
  const desktop = numbered('desktop', byH3[H3_PATH_A[2]], `"${H3_PATH_A[2]}"`);
  const tunnel = blocksOf(linux.items[1]);
  must(tunnel.length === 3 && isText(tunnel[0]) && tunnel[1].type === 'code' && tunnel[1].lang === 'sh' && tunnel[1].text.startsWith('ssh -L') && isText(tunnel[2]),
    'linux-2 should be: a paragraph, the ssh -L command, a paragraph');
  tunnel[1].attrs = { 'data-tunnel': '' };

  // ---------------------------------------------------------------- act 3, red: Path B
  const cloudLists = listsIn(byH2[H2[3]], true);
  must(cloudLists.length === 2, `"${H2[3]}" should hold two numbered lists, found ${cloudLists.length}`);
  const cloud = numbered('cloud', [cloudLists[0]], `"${H2[3]}"`);
  const imports = numbered('import', [cloudLists[1]], `"${H2[3]}", after the first`);
  must(cloud.items[0].text.includes('`Authorization=`'), 'cloud-1 no longer explains Authorization=');
  must(cloud.items[1].text.includes('`pulseotlp_x.x.x.zip`'), 'cloud-2 no longer names pulseotlp_x.x.x.zip');
  cloud.items[0].attrs['data-helper-note'] = '';
  addTo(cloud.items[1], zipLine('pulseotlp'));
  const json = only(blocksOf(cloud.items[3]).filter((t) => t.type === 'code' && t.lang === 'json'), 'json block in cloud-4');
  let shape;
  try { shape = JSON.parse(json.text); } catch { fail('the json block of cloud-4 is no longer valid JSON'); }
  must(shape.Endpoint === PLACEHOLDERS[1] && shape.Headers?.Authorization === PLACEHOLDERS[2] && shape.Protocol === 'http/protobuf',
    'the json block of cloud-4 no longer holds the two placeholders the helper fills, with Protocol http/protobuf');
  json.attrs = { 'data-helper': '' };
  json.label = 'ModConfig/pulse-otlp.json';
  // the dashboard file is one the site already ships (files/): a first timer needs no detour through GitHub's page
  addTo(imports.items[1], downloadLine(assetUrl(SLUG, 'files/pulse-overview-shared.json'), 'pulse-overview-shared.json', WORDS.sameFile, ' download'));
  for (const [list, ids] of [[linux, 'linux'], [desktop, 'desktop'], [imports, 'import']]) {
    list.items.forEach((item, i) => { if (SEE_DASHBOARD.includes(`${ids}-${i + 1}`)) addTo(item, figure); });
  }
  const lede = (name) => {
    const para = byH2[name].find(isText);
    const m = /^Use this path if [^.]+\./.exec(flat(para ? textOf(para) : ''));
    must(m, `"${name}" no longer opens with a sentence that starts "Use this path if "`);
    return m[0];
  };
  const ledes = { a: lede(H2[2]), b: lede(H2[3]) };

  // ---------------------------------------------------------------- Troubleshooting
  const trouble = only(listsIn(byH2[H2[4]], false), `bullet list under "${H2[4]}"`);
  must(trouble.items.length === TROUBLE_ENTRIES.length, `"${H2[4]}" has ${trouble.items.length} entries, the rules say ${TROUBLE_ENTRIES.length}`);
  trouble.attrs = { 'data-list': 'trouble' };
  trouble.items.forEach((item, i) => {
    const lead = blocksOf(item)[0]?.tokens?.[0];
    must(lead?.type === 'strong' && lead.raw.slice(2).startsWith(TROUBLE_ENTRIES[i]), `troubleshooting entry ${i + 1} should open with bold text that starts "${TROUBLE_ENTRIES[i]}"`);
    item.attrs = { id: `trouble-${i + 1}` };
  });
  for (const rows of Object.values(TROUBLE)) for (const n of rows) must(n >= 1 && n <= trouble.items.length, `a step points at the troubleshooting entry ${n}, which is not there`);

  // ---------------------------------------------------------------- code blocks, callouts, headings
  const codes = [];
  walk(tokens, (t) => { if (t.type === 'code') codes.push(t); });
  for (const [re, label, n] of WHERE) {
    const hit = codes.filter((c) => c.lang === 'sh' && re.test(c.text));
    must(hit.length === n, `${n} sh fence(s) should start with ${re}, found ${hit.length}`);
    for (const c of hit) c.where = label;
  }
  must(codes.filter((c) => !c.lang).length === 2, 'a fence without a language is rendered as a log excerpt: exactly two are expected');
  for (const ph of PLACEHOLDERS) must(codes.some((c) => c.lang && c.text.includes(ph)), `the placeholder ${ph} is no longer in a code block`);
  for (const c of codes) if (c.lang) c.ph = PLACEHOLDERS;
  for (const words of CALLOUTS) {
    try { markCallout(tokens, words, FILE); } catch (e) { fail(e.message.replace(`${FILE}: `, '')); }      // one prefix for every message of this module
  }
  for (const t of tokens) if (t.type === 'heading' && NO_ANCHOR.includes(t.text)) t.anchor = false;

  // ---------------------------------------------------------------- "What next": every sentence is quoted from a document and checked
  const rawOf = (file) => flat(docs[file].tokens.map((t) => t.raw).join(''));
  const next = NEXT.map((n) => {
    const hits = rawOf(n.file).split(n.quote).length - 1;
    must(hits === 1, `the sentence quoted for "${n.label}" ("${n.quote.slice(0, 40)}...") should be in ${n.file} exactly once, found ${hits}`);
    return `<li><a href="${pageUrl(SLUG, n.slug)}"><b>${esc(n.label)}</b></a>${n.tag ? ` <span class="next__tag" aria-hidden="true">${n.tag}</span>` : ''}<br>${inline(n.quote)}.</li>`;
  });

  // ---------------------------------------------------------------- render, part by part, in page order
  const part = (list) => render(list, { file: FILE, page: SLUG });
  const intro = part(byH2['']), choose = part(byH2[H2[0]]), step1 = part(byH2[H2[1]]);
  const a0 = part(byH3['']), before = part(byH3[H3_PATH_A[0]]), onLinux = part(byH3[H3_PATH_A[1]]), onDesktop = part(byH3[H3_PATH_A[2]]);
  const b = part(byH2[H2[3]]), tr = part(byH2[H2[4]]);
  const act = (name, kicker, extra, html) => `<section class="act" data-act="${name}"${extra}>\n<p class="act__kicker" aria-hidden="true">${kicker}</p>\n${html}</section>\n`;
  const pathA = `${a0.html}${before.html}<section data-docker="engine">\n${onLinux.html}</section>\n<section data-docker="desktop">\n${onDesktop.html}</section>\n`;
  const whatNext = `<h2 id="what-next">${WORDS.whatNext}</h2>\n<ul class="next">\n${next.join('\n')}\n</ul>\n`;
  const html = [
    `<p class="kicker" aria-hidden="true">${WORDS.kicker}</p>`,
    intro.html,
    act('choose', WORDS.choose, '', choose.html),
    act('install', WORDS.install, '', step1.html),
    act('path', WORDS.path, ` data-path="a" data-lede="${esc(ledes.a)}"`, pathA),
    act('path', WORDS.path, ` data-path="b" data-lede="${esc(ledes.b)}"`, b.html),
    act('next', WORDS.next, '', whatNext),
    `<section class="trouble">\n${tr.html}</section>`,
    // the guide's own reason why path A is not available, shown on the blue pill for those two hosting situations
    `<template data-why="panel">${why.panel}</template>`,
    `<template data-why="container">${why.container}</template>`,
  ].join('\n');

  // the search finds the page by its headings, and each step and each troubleshooting entry by itself
  const items = (list, name) => list.items.map((item, i) => ({ id: `${name}-${i + 1}`, title: `${STEP_LISTS[name][1]}, step ${i + 1}`, text: flat(textOf(item)) }));
  const search = [...intro.search, ...choose.search, ...step1.search, ...items(install, 'install'), ...a0.search, ...before.search,
    ...onLinux.search, ...items(linux, 'linux'), ...onDesktop.search, ...items(desktop, 'desktop'), ...b.search, ...items(cloud, 'cloud'), ...items(imports, 'import'),
    { id: 'what-next', title: WORDS.whatNext, text: NEXT.map((n) => n.label).join(' ') },
    ...tr.search, ...trouble.items.map((item, i) => ({ id: `trouble-${i + 1}`, title: plain(blocksOf(item)[0].tokens[0]), text: flat(textOf(item)) }))];
  const toc = [...choose.toc, ...step1.toc, ...a0.toc, ...before.toc, ...onLinux.toc, ...onDesktop.toc, ...b.toc, { id: 'what-next', text: WORDS.whatNext, level: 2 }, ...tr.toc];
  const sig = [install, linux, desktop, cloud, imports].map((l) => l.items.length).join('.');
  const description = flat(textOf(byH2[''].find((t) => t.type === 'paragraph'))).split(/(?<=\.)\s/)[0];

  return [{ slug: SLUG, title: 'Getting started', description, layout: 'docs', source: [FILE], html, toc, search, attrs: { class: 'gs', 'data-gs': '', 'data-gs-sig': sig } }];
}
