// Checks on the emitted pages: what the browser would find wrong, found before it does. The pages
// are our own output, so a small tag scanner is enough and no HTML parser is needed. The first
// failure throws one sentence that names the file and the rule.
import { posix } from 'node:path';
import { gzipSync } from 'node:zlib';
import { BOOT, decode } from './layout.mjs';

// Size budgets, in KB gzipped. Each sits about 15 percent above the size the asset had when it was last
// set, so a warning means something changed, not that a document grew a little: raise it deliberately.
// They print a warning and never stop the build. A request to another origin does stop it.
export const BUDGETS = { 'assets/site.js': 27.5, 'assets/home.js': 17.5, 'assets/site.css': 19, 'assets/search-index.js': 51.5, html: 17.5 };

const TAG = /<([a-zA-Z][\w:-]*)((?:\s+[^\s"'<>\/=]+(?:\s*=\s*(?:"[^"]*"|'[^']*'|[^\s"'=<>`]+))?)*)\s*\/?>/g;
const ATTR = /([^\s"'<>\/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>`]+)))?/g;
const SCHEME = /^(?:[a-z][a-z0-9+.-]*:|\/\/)/i;

function scan(html) {
  const out = [];
  for (const m of html.matchAll(TAG)) {
    const attrs = {};
    for (const a of m[2].matchAll(ATTR)) attrs[a[1].toLowerCase()] = decode(a[2] ?? a[3] ?? a[4] ?? '');
    out.push({ name: m[1].toLowerCase(), attrs });
  }
  return out;
}

/**
 * files: Map of output path -> string or Buffer; pages: [{ path, base, show }] for the HTML pages.
 * Returns { warnings, stats: { pages, links, anchors } } or throws.
 */
export function checkPages(files, pages) {
  const text = (path) => files.get(path).toString();
  const fail = (path, what) => { throw new Error(`${path}: ${what}`); };
  const parsed = new Map(pages.map((p) => [p.path, scan(text(p.path))]));
  const idsOf = new Map();
  let links = 0, anchors = 0;

  for (const page of pages) {
    const html = text(page.path), tags = parsed.get(page.path);
    const ids = new Set();
    let h1 = 0, prev = 0;
    if (!html.includes('http-equiv="Content-Security-Policy"')) fail(page.path, 'no Content-Security-Policy: the promise that nothing leaves the page is the policy');
    if (html.includes('<!--')) fail(page.path, 'an HTML comment, a leftover marker, reached the page');
    for (const { name, attrs } of tags) {
      if ('id' in attrs) { if (ids.has(attrs.id)) fail(page.path, `the id "${attrs.id}" is used twice`); ids.add(attrs.id); }
      if (name === 'style') fail(page.path, 'a <style> element: the policy allows stylesheets from the site only');
      for (const a of Object.keys(attrs)) {
        if (a === 'style') fail(page.path, `a style attribute on <${name}>: the policy blocks it silently, move it to the stylesheet`);
        if (/^on/.test(a)) fail(page.path, `an inline event handler (${a}) on <${name}>: the policy blocks it silently`);
      }
      if (name === 'base' && !page.base) fail(page.path, 'a <base>: only 404.html needs one');
      if (/^h[1-6]$/.test(name)) {
        const level = +name[1];
        if (level === 1) h1++;
        if (level > prev + 1) fail(page.path, `a heading level that skips: <${name}> after ${prev ? `<h${prev}>` : 'nothing'}`);
        prev = level;
      }
      if (name === 'img') for (const a of ['alt', 'width', 'height']) if (!(a in attrs)) fail(page.path, `an <img> without ${a}`);
      if ('tabindex' in attrs && Number(attrs.tabindex) > 0) fail(page.path, `a positive tabindex (${attrs.tabindex})`);
    }
    if (h1 !== 1) fail(page.path, `${h1} <h1> elements, expected exactly one`);
    // the only scripts: the boot script, byte for byte, and the site's own bundles. A script element without a
    // src is inline whatever its other attributes say (type, nonce, a different case).
    const inline = tags.filter(({ name, attrs }) => name === 'script' && !('src' in attrs)).length;
    if (inline !== 1 || !html.includes(`<script>${BOOT}</script>`)) fail(page.path, 'an inline <script> other than the boot script: the policy only allows that one, by its hash');
    for (const { name, attrs } of tags) {
      if (name !== 'script' || !attrs.src) continue;
      const allowed = ['assets/site.js', ...(page.show ? ['assets/home.js'] : [])];
      if (!allowed.includes(posix.join(page.base ? '' : posix.dirname(page.path), attrs.src.split('?')[0]))) fail(page.path, `a <script src="${attrs.src}"> that is not one of ${allowed.join(', ')}`);
    }
    idsOf.set(page.path, ids);
  }

  for (const page of pages) {
    for (const { name, attrs } of parsed.get(page.path)) {
      if (name === 'base') continue;               // allowed on 404.html only, checked above
      const refs = [['href', attrs.href], ['src', attrs.src], ['srcset', attrs.srcset]].filter(([, v]) => v !== undefined);
      for (const [attr, raw] of refs) {
        if (name === 'link' && attrs.rel === 'canonical') continue;
        // a request must stay on the site, and the policy has no data: source; a link (<a href>) is a navigation, not a request.
        const addresses = attr === 'srcset' ? raw.split(',').map((s) => s.trim().split(/\s+/)[0]) : [raw];
        for (const value of addresses) {
          if (SCHEME.test(value)) {
            if (name !== 'a') fail(page.path, `an address with a scheme in ${attr} (${value}): the site makes no request to another origin`);
            continue;
          }
          if (attr === 'srcset') continue;
          const [, pathPart, fragment] = /^([^#?]*)(?:\?[^#]*)?(?:#(.*))?$/.exec(value);
          let target = pathPart ? posix.join(page.base ? '' : posix.dirname(page.path), pathPart) : page.path;
          if (!pathPart && !fragment) fail(page.path, `an empty ${attr} on <${name}>`);
          if (target === '.' || target.endsWith('/')) target = posix.join(target === '.' ? '' : target, 'index.html');
          if (!files.has(target)) fail(page.path, `${attr}="${value}" does not resolve to a file of the site (${target})`);
          links++;
          if (fragment) {
            if (!idsOf.has(target)) fail(page.path, `${attr}="${value}" names a fragment of ${target}, which is not a page`);
            if (!idsOf.get(target).has(decodeURIComponent(fragment))) fail(page.path, `${attr}="${value}" names #${fragment}, which is no id of ${target}`);
            anchors++;
          }
        }
      }
    }
  }

  // The search index points at ids of the pages. A result that opens nowhere is found by the reader first.
  let entries = 0;
  if (files.has('assets/search-index.js')) {
    const m = /^window\.PULSE_SEARCH = (.*);\n?$/s.exec(text('assets/search-index.js'));
    if (!m) fail('assets/search-index.js', 'is not "window.PULSE_SEARCH = [...];"');
    for (const e of JSON.parse(m[1])) {
      const [path, fragment] = e.u.split('#');
      if (!idsOf.has(path)) fail('assets/search-index.js', `the entry "${e.t}" points at ${e.u}, which is not a page`);
      if (fragment && !idsOf.get(path).has(fragment)) fail('assets/search-index.js', `the entry "${e.t}" points at #${fragment}, which is no id of ${path}`);
      entries++;
    }
  }

  const warnings = [];
  const kb = (content) => gzipSync(content).length / 1024;
  const over = (path, limit) => { if (files.has(path) && kb(files.get(path)) > limit) warnings.push(`warning: ${path} is ${kb(files.get(path)).toFixed(1)} KB gzipped, over its budget of ${limit} KB`); };
  for (const [path, limit] of Object.entries(BUDGETS)) if (path !== 'html') over(path, limit);
  for (const page of pages) over(page.path, BUDGETS.html);
  return { warnings, stats: { pages: pages.length, links, anchors, entries } };
}
