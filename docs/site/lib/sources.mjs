// Reads the documents the site renders and refuses to go on when they stop having the shape the
// site was written for: a renamed section, a new image, a link into nowhere. Nothing is rendered
// here; page modules get the lexed documents and their slices.
import { existsSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Marked } from 'marked';
import { REPO } from './layout.mjs';

/** The repository, from the working tree: docs/site/lib is three levels down. */
export const ROOT = fileURLToPath(new URL('../../../', import.meta.url));

const DOC_FILES = ['README.md', 'docs/getting-started.md', 'contrib/grafana/README.md', 'contrib/alerts/README.md', 'CHANGELOG.md'];
// Everything the build reads from the repository (the site's own static/ folder apart). One that is missing stops the
// build before anything is written.
const INPUTS = [...DOC_FILES, 'contrib/alerts/pulse-alerts.yml', 'contrib/grafana/provisioning/dashboards/json/pulse.json',
  'contrib/grafana/pulse-overview-shared.json', 'Pulse/modinfo.json', 'Pulse.Otlp/modinfo.json', 'Pulse/PulseCommands.cs', 'NOTICE',
  'docs/assets/moddb/dashboard-overview.png', 'docs/assets/moddb/dashboard-attribution.png', 'docs/assets/moddb/pulse-pill-choice.webp'];

export const flat = (s) => s.replace(/\s+/g, ' ').trim();
export const plain = (t) => (t.tokens ? t.tokens.map(plain).join('') : t.text ?? '');
/** GitHub's own heading slug. */
export const slugBase = (text) => text.toLowerCase().replace(/[^\p{L}\p{N}\p{M}_\- ]/gu, '').replace(/ /g, '-');
/** One per source document: GitHub numbers duplicate headings by document, not by page. */
export function makeSlugger() {
  const seen = new Map();
  return (text) => {
    const base = slugBase(text), n = seen.get(base) ?? 0;
    seen.set(base, n + 1);
    return n ? `${base}-${n}` : base;
  };
}

// Phrases that point at text which now lives on another page ("further down", "the main README").
// The words stay as they are; only a link is put around them, in memory, before lexing. Each
// pattern must match exactly once in its file. Targets are written the way an author would write
// them in that file, so they go through the ordinary link rewriting.
// [file, pattern, replacement]
export const LINKIFY = [
  ['README.md', /described further\s+down, off because/, (m) => m.replace(/described further\s+down/, (w) => `[${w}](#attribution)`)],
  ['README.md', /a\s+section of their own further down/, (m) => `[${m}](#attribution)`],
  ['README.md', /see the\s+changelog for the full old to new list/, (m) => m.replace('changelog', '[changelog](CHANGELOG.md#020---2026-09-29)')],
  ['README.md', /lives in `contrib\/grafana`/, () => 'lives in [`contrib/grafana`](contrib/grafana/README.md)'],
  ['README.md', /live in `contrib\/alerts`/, () => 'live in [`contrib/alerts`](contrib/alerts/README.md)'],
  ['README.md', /The coverage badge\s+at the top of this page/, (m) => `[${m}](https://github.com/StratumServer/Pulse#readme)`],
  ['docs/getting-started.md', /the main README's Attribution\s+section/, (m) => `[${m}](../README.md#attribution)`],
  ['docs/getting-started.md', /also covered in the main\s+README/, (m) => m.replace(/the main\s+README/, (w) => `[${w}](../README.md#degraded-mode)`)],
  ['docs/getting-started.md', /section and the changelog if a/, (m) => m.replace('the changelog', '[the changelog](../CHANGELOG.md#020---2026-09-29)')],
  ['contrib/grafana/README.md', /the main README's Attribution section/, (m) => `[${m}](../../README.md#attribution)`],
  ['contrib/grafana/README.md', /`docs\/getting-started\.md`/, (m) => `[${m}](../../docs/getting-started.md)`],
  ['contrib/alerts/README.md', /the main README's "Degraded mode"\s+section/, (m) => `[${m}](../../README.md#degraded-mode)`],
  ['contrib/alerts/README.md', /the main README's "Attribution"\s+section/, (m) => `[${m}](../../README.md#attribution)`],
  ['contrib/alerts/README.md', /`contrib\/grafana\/README\.md`/, (m) => `[${m}](../grafana/README.md)`],
];
export function linkify(file, text) {
  for (const [f, re, rep] of LINKIFY) {
    if (f !== file) continue;
    const found = text.match(new RegExp(re.source, 'g'))?.length ?? 0;
    if (found !== 1) throw new Error(`${file}: the phrase ${re} should occur exactly once (LINKIFY in lib/sources.mjs), found ${found}`);
    text = text.replace(re, rep);
  }
  return text;
}

// The headings of each document, as [level, text]. A new, missing, renamed or moved heading stops
// the build with both lists side by side: a section nobody assigned to a page must not appear by
// accident. README.md is exact for H1 and H2 only (its H3 are free: they render wherever their
// section does); the guide is exact at every level, its H3 are the wizard's Docker variants.
const CONTRACT = {
  'README.md': { levels: [1, 2], headings: [[1, 'Pulse'], [2, 'Table of contents'], [2, 'Install'], [2, 'Configuration'], [2, 'Scraping it'], [2, 'Degraded mode'],
    [2, 'Runtime metrics'], [2, 'Attribution'], [2, 'OTLP export'], [2, 'Building and testing'], [2, 'Where this is going'], [2, 'License']] },
  'docs/getting-started.md': { headings: [[1, 'Getting started: from install to your first dashboard'], [2, 'Which path is yours'], [2, 'Step 1: install the mod on the server'],
    [2, 'Path A: run Prometheus and Grafana yourself'], [3, 'Before you start'], [3, 'On a Linux server or PC'], [3, 'On Windows or macOS (Docker Desktop)'],
    [3, 'Optional: load the alert rules'], [2, 'Path B: Grafana Cloud, if you would rather host nothing'], [2, 'Troubleshooting']] },
  'contrib/grafana/README.md': { headings: [[1, 'Grafana kit'], [2, 'Importing it into a Grafana you already run'], [2, 'The files']] },
  'contrib/alerts/README.md': { headings: [[1, 'Alerting rules']] },
};
export const CHANGELOG_H2 = /^\[(Unreleased|\d+\.\d+\.\d+)\](?: - (\d{4}-\d{2}-\d{2}))?$/;
const CHANGELOG_H3 = ['Added', 'Changed', 'Deprecated', 'Removed', 'Fixed', 'Security'];

function checkHeadings(file, headings) {
  const rule = CONTRACT[file];
  if (rule) {
    const found = headings.filter((h) => !rule.levels || rule.levels.includes(h.level)).map((h) => `${h.level} ${h.text}`);
    const expected = rule.headings.map(([l, t]) => `${l} ${t}`);
    if (found.join('\n') !== expected.join('\n')) {
      throw new Error(`${file} no longer matches the heading contract (CONTRACT in lib/sources.mjs): a section was added, renamed, moved or removed.\nexpected:\n  ${expected.join('\n  ')}\nfound:\n  ${found.join('\n  ')}`);
    }
    return;
  }
  // CHANGELOG.md
  if (headings[0]?.level !== 1 || headings[0].text !== 'Changelog') throw new Error('CHANGELOG.md no longer opens with its H1 "Changelog"');
  for (const h of headings) {
    if (h.level === 2 && !CHANGELOG_H2.test(h.text)) throw new Error(`CHANGELOG.md: the heading "${h.text}" is not "[Unreleased]" or "[x.y.z] - yyyy-mm-dd" (CHANGELOG_H2 in lib/sources.mjs)`);
    if (h.level === 3 && !CHANGELOG_H3.includes(h.text)) throw new Error(`CHANGELOG.md: the heading "${h.text}" is not one of ${CHANGELOG_H3.join(', ')}`);
    if (h.level > 3 || (h.level === 1 && h !== headings[0])) throw new Error(`CHANGELOG.md: unexpected heading "${h.text}" (level ${h.level})`);
  }
}

// Which page renders which part of the README. Sections that are not here (the title and the
// introduction, the table of contents) are not rendered at all: the site's own navigation replaces them.
const SLICE_PAGE = { install: 'configuration', configuration: 'configuration', scraping: 'scraping', families: 'metrics', degraded: 'metrics',
  runtime: 'metrics', attribution: 'attribution', otlp: 'otlp', building: 'development', going: 'development', license: 'development' };
const README_H2 = { install: 'Install', configuration: 'Configuration', scraping: 'Scraping it', degraded: 'Degraded mode', runtime: 'Runtime metrics',
  attribution: 'Attribution', otlp: 'OTLP export', building: 'Building and testing', going: 'Where this is going', license: 'License' };
const PAGE_OF_DOC = { 'docs/getting-started.md': 'getting-started', 'contrib/grafana/README.md': 'dashboard', 'contrib/alerts/README.md': 'alerts', 'CHANGELOG.md': 'changelog' };
// The one slice that does not start at a heading: this paragraph, inside "Scraping it", starts the metric reference.
const SPLIT = 'The metric families it serves:';
// A fence without a language is an excerpt of the server's log, shown in a terminal bar. The documents wrap their lines at
// 100 columns, so a log line that is one line for the server spans several lines of the file: the site joins those back and
// the page wraps the line to the reader's width. Each excerpt is named here by its first words: 'wrapped' is one log line
// wrapped over several, 'line' is a single line already. A new one stops the build until somebody says which, because the
// build cannot tell one wrapped line from several short ones; a fence with a language is code and is never touched.
const LOG_EXCERPTS = [['README.md', '[pulseotlp] Pulse OTLP export to ', 'wrapped'], ['docs/getting-started.md', '[pulse] Pulse serving metrics on ', 'line'],
  ['docs/getting-started.md', '[pulseotlp] Pulse OTLP export to ', 'wrapped']];

export const deepFreeze = (o) => { if (o && typeof o === 'object' && !Object.isFrozen(o)) { Object.freeze(o); Object.values(o).forEach(deepFreeze); } return o; };
const posixJoin = (dir, rel) => {
  const out = dir ? dir.split('/') : [];
  for (const part of rel.split('/')) { if (part === '..') out.pop(); else if (part !== '.' && part !== '') out.push(part); }
  return out.join('/');
};

/**
 * opts.text(path) reads a document and opts.exists(path) says 'file', 'dir' or null: the tests swap them to
 * feed an altered copy of a document to the checks.
 */
export function loadSources(opts = {}) {
  const abs = (path) => join(ROOT, path);
  const exists = opts.exists ?? ((path) => (existsSync(abs(path)) ? (statSync(abs(path)).isDirectory() ? 'dir' : 'file') : null));
  const bytes = (path) => readFileSync(abs(path));
  const text = opts.text ?? ((path) => bytes(path).toString('utf8'));
  for (const path of INPUTS) if (exists(path) !== 'file') throw new Error(`${path}: input file is missing`);

  const marked = new Marked({ gfm: true });
  const docs = {};
  const excerpts = new Set();
  for (const file of DOC_FILES) {
    const tokens = marked.lexer(linkify(file, text(file)));
    let seenH2 = false;
    for (const top of tokens) {
      if (top.type === 'heading' && top.depth === 2) seenH2 = true;
      marked.walkTokens([top], (t) => {
        if (t.type === 'html') throw new Error(`${file}: raw HTML or an HTML comment (${t.raw.trim().slice(0, 40)}): the site uses no marker, and its policy assumes no markup comes out of the documents`);
        // the badges above the README's first H2 are the only images, and that part is not rendered
        if (t.type === 'image' && (file !== 'README.md' || seenH2)) throw new Error(`${file}: an image (${t.href}) inside a rendered part: the site shows no image from a document`);
        if (t.type === 'code' && !t.lang) {
          const known = LOG_EXCERPTS.find(([f, start]) => f === file && t.text.startsWith(start));
          if (!known) throw new Error(`${file}: a fence without a language ("${t.text.slice(0, 40)}") is not one of the log excerpts the build knows (LOG_EXCERPTS in lib/sources.mjs): say whether it is one wrapped line or already one line, or give the fence a language`);
          if (known[2] === 'line' && t.text.includes('\n')) throw new Error(`${file}: the log excerpt "${known[1]}" is declared as one line and now spans several (LOG_EXCERPTS in lib/sources.mjs)`);
          if (known[2] === 'wrapped') t.wrapped = true;
          excerpts.add(known);
        }
      });
    }
    const slug = makeSlugger();
    const headings = [];
    marked.walkTokens(tokens, (t) => { if (t.type === 'heading') { t.id = slug(plain(t)); headings.push({ level: t.depth, text: plain(t), id: t.id }); } });
    checkHeadings(file, headings);
    docs[file] = { file, tokens, headings };
  }
  for (const [file, start] of LOG_EXCERPTS.filter((e) => !excerpts.has(e))) throw new Error(`${file}: the log excerpt that starts "${start}" is gone (LOG_EXCERPTS in lib/sources.mjs)`);

  // README slices: runs of top-level tokens from one H2 to the next, and the split paragraph.
  const readme = docs['README.md'].tokens;
  const h2At = (name) => {
    const i = readme.findIndex((t) => t.type === 'heading' && t.depth === 2 && t.text === name);
    if (i < 0) throw new Error(`README.md: no H2 "${name}"`);
    return i;
  };
  const splits = readme.map((t, i) => [t, i]).filter(([t]) => t.type === 'paragraph' && flat(t.text) === SPLIT);
  if (splits.length !== 1) throw new Error(`README.md: the paragraph "${SPLIT}" should occur exactly once at the top level, found ${splits.length}`);
  const split = splits[0][1];
  if (split < h2At('Scraping it') || split > h2At('Degraded mode')) throw new Error(`README.md: the paragraph "${SPLIT}" is no longer inside "Scraping it"`);
  const slices = {};
  const h2s = readme.map((t, i) => (t.type === 'heading' && t.depth === 2 ? i : -1)).filter((i) => i >= 0);
  const endOf = (start) => h2s.find((i) => i > start) ?? readme.length;
  for (const [name, h2] of Object.entries(README_H2)) {
    const start = h2At(h2);
    slices[name] = readme.slice(start, name === 'scraping' ? split : endOf(start));
  }
  slices.families = readme.slice(split, h2At('Degraded mode'));

  // Which page each heading ends up on, for the link rewriting. Not rendered: null.
  const anchorPage = {};
  for (const h of docs['README.md'].headings) anchorPage[`README.md#${h.id}`] = null;
  for (const [name, tokens] of Object.entries(slices)) {
    marked.walkTokens(tokens, (t) => { if (t.type === 'heading') anchorPage[`README.md#${t.id}`] = SLICE_PAGE[name]; });
  }
  for (const [file, page] of Object.entries(PAGE_OF_DOC)) for (const h of docs[file].headings) anchorPage[`${file}#${h.id}`] = page;

  /** href as written in `file` -> { page, hash } for a page of the site, or { external } for anything else. Throws on a dead link. */
  function resolve(file, href) {
    if (/^(https?|mailto):/i.test(href)) return { external: href };
    if (/^[a-z][a-z0-9+.-]*:/i.test(href)) throw new Error(`${file}: the link ${href} has a scheme the site does not link to (only http, https and mailto)`);
    const [pathPart, hash = ''] = href.split('#');
    const target = pathPart ? posixJoin(file.split('/').slice(0, -1).join('/'), pathPart) : file;
    if (Object.hasOwn(docs, target)) {
      if (!hash) return { page: target === 'README.md' ? '' : PAGE_OF_DOC[target], hash: '' };
      const page = anchorPage[`${target}#${hash}`];
      if (page === undefined) throw new Error(`${file}: the link ${href} points at "${hash}", which is not a heading of ${target}`);
      if (page === null) throw new Error(`${file}: the link ${href} points into a part of ${target} that the site does not render`);
      return { page, hash };
    }
    const kind = exists(target);
    if (kind === 'file') return { external: `${REPO}/blob/main/${target}${hash ? '#' + hash : ''}` };
    if (kind === 'dir') return { external: `${REPO}/tree/main/${target}` };
    throw new Error(`${file}: the link ${href} points at ${target}, which is not in the repository`);
  }

  /** A fresh deep copy of a README slice (see SLICE_PAGE) or of a whole document, by its path. Hooks go on copies only. */
  function slice(name) {
    const tokens = Object.hasOwn(slices, name) ? slices[name] : Object.hasOwn(docs, name) ? docs[name].tokens : null;
    if (!tokens) throw new Error(`slice("${name}"): not a README slice (${Object.keys(slices).join(', ')}) and not one of the documents (${DOC_FILES.join(', ')})`);
    return structuredClone(tokens);
  }

  for (const doc of Object.values(docs)) deepFreeze(doc.tokens);
  return { text, bytes, docs, slice, resolve };
}
