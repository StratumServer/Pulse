// The one build script: node build.mjs [--out <dir>] [--offline]
// Reads the repository (the working tree, two levels above this file), renders the pages, joins the
// client sources, checks the result and only then writes it. A failed check leaves the output
// directory as it was. --offline (or PULSE_SITE_OFFLINE=1) skips the SonarCloud request, which keeps
// the output the same on every run: tests and pull request builds use it, the deploy build does not.
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { parseArgs } from 'node:util';
import { gzipSync } from 'node:zlib';
import { bundle, joinCss, readDir } from './lib/bundle.mjs';
import { BUDGETS, checkPages } from './lib/checks.mjs';
import { loadData } from './lib/data.mjs';
import { assetUrl, esc, fingerprint, NOT_FOUND, outPath, PAGES, pageInfo, pageUrl, renderPage } from './lib/layout.mjs';
import { createMd } from './lib/md.mjs';
import { loadSources } from './lib/sources.mjs';
import changelog from './lib/pages/changelog.mjs';
import configuration from './lib/pages/configuration.mjs';
import gettingStarted from './lib/pages/getting-started.mjs';
import home from './lib/pages/home.mjs';
import kits from './lib/pages/kits.mjs';
import metrics from './lib/pages/metrics.mjs';
import reference from './lib/pages/reference.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const MODULES = [home, gettingStarted, configuration, reference, metrics, kits, changelog];

/** Builds the whole site in memory: a Map of output path -> string or Buffer. Touches no disk. */
export async function buildSite({ sources = loadSources(), offline = false, fetchFn, timeoutMs } = {}) {
  const data = await loadData(sources, { offline, fetchFn, timeoutMs });
  const md = createMd(sources);
  const ctx = { docs: sources.docs, slice: sources.slice, render: md.render, data, pageUrl, assetUrl, inline: md.inline, esc };

  const pages = MODULES.flatMap((module) => module(ctx));
  const expected = [...PAGES.map((p) => p.slug), NOT_FOUND.slug];
  for (const slug of expected) {
    const n = pages.filter((p) => p.slug === slug).length;
    if (n !== 1) throw new Error(`the page "${slug}" must come from exactly one page module, ${n} did`);
  }
  for (const p of pages) {
    const reg = PAGES.find((r) => r.slug === p.slug);
    if (!expected.includes(p.slug)) throw new Error(`a page module returned the page "${p.slug}", which is not in the registry of lib/layout.mjs`);
    if (!p.html || !['docs', 'home'].includes(p.layout)) throw new Error(`the page "${p.slug}" needs an html string and a layout of docs or home`);
    if (reg && p.title !== reg.label) throw new Error(`the page "${p.slug}" is titled "${p.title}", the registry says "${reg.label}": one name for the sidebar, the pager and the title`);
  }

  // client side: two classic scripts (every page, and the show pages) and one stylesheet
  const js = readDir(join(HERE, 'js'));
  const siteJs = bundle(js, 'main.js', 'site.js');
  const homeJs = bundle(js, 'home-main.js', 'home.js');
  const css = joinCss(readDir(join(HERE, 'css')));
  const site = { version: data.version, notice: data.notice, v: { css: fingerprint(css), site: fingerprint(siteJs), home: fingerprint(homeJs) } };

  const files = new Map();
  const order = [...PAGES.map((p) => p.slug), NOT_FOUND.slug];
  for (const slug of order) {
    const page = pages.find((p) => p.slug === slug);
    files.set(outPath(slug), renderPage(page, site));
  }
  // search: one entry per heading, page by page, in document order. Text before a page's first heading
  // (id '') belongs to the page itself. The index is a script, not JSON: fetch is refused from file://.
  const index = pages.filter((p) => p.slug && p.slug !== NOT_FOUND.slug).sort((a, b) => order.indexOf(a.slug) - order.indexOf(b.slug))
    .flatMap((p) => (p.search ?? []).map((s) => ({ u: outPath(p.slug) + (s.id ? `#${s.id}` : ''), t: s.title || p.title, c: p.title, x: s.text })));
  files.set('assets/site.css', css);
  files.set('assets/site.js', siteJs);
  files.set('assets/home.js', homeJs);
  files.set('assets/search-index.js', `window.PULSE_SEARCH = ${JSON.stringify(index)};\n`);
  files.set('assets/favicon.svg', readFileSync(join(HERE, 'static', 'favicon.svg')));
  for (const f of ['dashboard-overview.png', 'dashboard-attribution.png']) files.set(`assets/img/${f}`, sources.bytes(`docs/assets/moddb/${f}`));
  // the pill artwork of Home: the animated WebP of the ModDB page, and its first frame for reduced motion and a rain switch that is off
  files.set('assets/img/pulse-pill-choice.webp', sources.bytes('docs/assets/moddb/pulse-pill-choice.webp'));
  files.set('assets/img/pulse-pill-choice-still.webp', readFileSync(join(HERE, 'static', 'img', 'pulse-pill-choice-still.webp')));
  files.set('files/pulse-alerts.yml', sources.bytes('contrib/alerts/pulse-alerts.yml'));
  files.set('files/pulse-overview-shared.json', sources.bytes('contrib/grafana/pulse-overview-shared.json'));

  const checked = checkPages(files, order.map((slug) => ({ path: outPath(slug), base: !!pageInfo(slug).base, show: pageInfo(slug).regime === 'show' })));
  return { files, pages, data, notes: data.notes, warnings: checked.warnings, stats: checked.stats };
}

/** Writes a built site. A directory that is not empty is emptied only when it holds a previous build, so --out . cannot remove sources. */
export function writeOutput(out, files) {
  if (existsSync(out)) {
    if (!statSync(out).isDirectory()) throw new Error(`${out} is a file`);
    if (readdirSync(out).length && !existsSync(join(out, 'assets', 'site.js'))) throw new Error(`${out} is not empty and does not hold a previous build (no assets/site.js): nothing was deleted`);
  }
  rmSync(out, { recursive: true, force: true });
  for (const [path, content] of files) {
    const to = join(out, path);
    mkdirSync(dirname(to), { recursive: true });
    writeFileSync(to, content);
  }
}

const kb = (content) => `${(Buffer.byteLength(content) / 1024).toFixed(1)} KB (${(gzipSync(content).length / 1024).toFixed(1)} gzip)`;

async function main() {
  let args;
  try { args = parseArgs({ options: { out: { type: 'string' }, offline: { type: 'boolean' } } }).values; } catch (e) { throw new Error(`${e.message}\nusage: node build.mjs [--out <dir>] [--offline]`); }
  const out = resolve(args.out ?? join(HERE, 'dist'));
  const started = performance.now();
  const built = await buildSite({ offline: args.offline || process.env.PULSE_SITE_OFFLINE === '1' });
  writeOutput(out, built.files);
  for (const note of built.notes) console.log(`note: ${note}`);
  for (const [path, content] of built.files) if (path.endsWith('.html')) console.log(`${path.padEnd(32)} ${kb(content)}`);
  for (const path of ['assets/site.js', 'assets/home.js', 'assets/site.css', 'assets/search-index.js']) console.log(`${path.padEnd(32)} ${kb(built.files.get(path))}, budget ${BUDGETS[path]} KB`);
  for (const w of built.warnings) console.warn(w);
  console.log(`${PAGES.length} pages and 404.html built into ${out} in ${((performance.now() - started) / 1000).toFixed(1)} s; ${built.stats.links} internal addresses (${built.stats.anchors} with an anchor) and ${built.stats.entries} search entries checked`);
}

if (process.argv[1] && pathToFileURL(resolve(process.argv[1])).href === import.meta.url) {
  main().catch((e) => { console.error(e.constructor === Error ? `error: ${e.message}` : e.stack); process.exit(1); });
}
