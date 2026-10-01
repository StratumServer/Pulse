// The one renderer every page module uses: tokens in, HTML out. A page module changes the output
// by setting properties on its own copy of the tokens (the hooks below), never by editing the HTML
// afterwards. Rendering writes nothing into the tokens it is given.
//
//   attrs      heading, paragraph, list, list item, code, table: an object of attributes written on the
//              element (the <table> itself, div.code for a code block). '' writes the bare attribute;
//              a class is added to the element's own class. A table that carries data-config is stacked
//              at every width (css/60-tools.css), so its wrapper never scrolls and is not a Tab stop.
//   rowAttrs   table: an array, one attrs object per body row
//   prepend    list item: HTML right after <li ...>
//   append     list item: HTML just before </li>
//   after      any block: HTML right after the element
//   label      code: the text of code__label instead of the language; table: the name of its region
//   where      code: <span class="code__where">TEXT</span>, the next sibling of code__label
//   ph         code: strings inside the code, each wrapped in <span class="ph"> (placeholders)
//   wrapped    code without a language: the lines are one hard-wrapped log line, joined with a space (set by lib/sources.mjs, never by a page module)
//   anchor     heading: false for no trailing "#" link (a heading that a script turns into a button)
//   callout    paragraph: true wraps it in the security callout
import { Marked } from 'marked';
import { decode, esc, pageUrl, squash } from './layout.mjs';
import { flat, plain } from './sources.mjs';

const attrs = (o) => Object.entries(o ?? {}).map(([k, v]) => (v === '' || v === true ? ` ${k}` : ` ${k}="${esc(String(v))}"`)).join('');
const merge = (base, extra = {}) => ({ ...base, ...extra, ...(base.class && extra.class ? { class: `${base.class} ${extra.class}` } : {}) });
const INLINE = new Set(['text', 'strong', 'em', 'codespan', 'link', 'del', 'escape', 'image']);
const isExternal = (url) => {
  try { const u = new URL(url); return /^https?:$/.test(u.protocol) && !['localhost', '127.0.0.1'].includes(u.hostname); } catch { return false; }
};

/** Sets the callout hook on the one paragraph that starts with `words`, white space collapsed. It must exist exactly once. */
export function markCallout(tokens, words, where) {
  const hits = [];
  new Marked().walkTokens(tokens, (t) => { if (t.type === 'paragraph' && flat(t.raw).startsWith(words)) hits.push(t); });
  if (hits.length !== 1) throw new Error(`${where}: the paragraph that starts "${words.slice(0, 40)}" should exist exactly once, found ${hits.length}`);
  hits[0].callout = true;
}

/** The text of a block, with white space left as it is. */
export function textOf(t) {
  if (t.type === 'br') return ' ';
  if (t.type === 'list') return t.items.map(textOf).join(' ');
  if (t.type === 'table') return [...t.header, ...t.rows.flat()].map(textOf).join(' ');
  if (t.type === 'code') return t.text;
  if (t.tokens) return t.tokens.map(textOf).join(t.tokens.every((x) => INLINE.has(x.type)) ? '' : ' ');
  return t.text ?? '';
}

/** A block's text as one line of plain text. */
export const plainOf = (t) => squash(textOf(t));

/** The "#" link that ends a heading: what a heading rendered here carries, and what a page module writes for a heading of its own. */
export const anchorLink = (id) => `&nbsp;<a class="anchor" href="#${esc(id)}" aria-label="Link to this section">#</a>`;

/** The contents list (rendered headings of level 2 and 3) and the search entries of a run of tokens. */
function scan(tokens, shift) {
  const toc = [], search = [];
  let cur = { id: '', title: '', parts: [] };
  const flush = () => {
    const text = squash(cur.parts.join(' '));
    if (cur.id || text) search.push({ id: cur.id, title: cur.title, text });
  };
  for (const t of tokens) {
    if (t.type === 'space') continue;
    if (t.type === 'heading') {
      flush();
      cur = { id: t.id, title: decode(plain(t)), parts: [] };
      const level = t.depth - shift;
      if (level === 2 || level === 3) toc.push({ id: t.id, text: cur.title, level });
    } else cur.parts.push(textOf(t));
  }
  flush();
  return { toc, search };
}

/** `resolve` is the link resolver of lib/sources.mjs. */
export function createMd({ resolve }) {
  function make({ file, page = '', shift = 0 }) {
    const rewrite = (href) => {
      if (!file) return href;
      const r = resolve(file, href);
      return r.external ?? pageUrl(page, r.page, r.hash);
    };
    const md = new Marked({ gfm: true });
    md.use({ renderer: {
      heading(t) {
        if (!t.id) throw new Error(`heading "${plain(t)}" has no id: only headings of a lexed document can be rendered`);
        const level = Math.min(6, Math.max(1, t.depth - shift));
        const anchor = t.anchor !== false && (level === 2 || level === 3) ? anchorLink(t.id) : '';
        return `<h${level}${attrs(merge({ id: t.id }, t.attrs))}>${this.parser.parseInline(t.tokens)}${anchor}</h${level}>\n${t.after ?? ''}`;
      },
      paragraph(t) {
        const p = `<p${attrs(t.attrs)}>${this.parser.parseInline(t.tokens)}</p>`;
        if (!t.callout) return `${p}\n${t.after ?? ''}`;
        return `<aside class="callout callout--security" aria-label="Security"><span class="callout__label">SECURITY</span>${p}</aside>\n${t.after ?? ''}`;
      },
      code(t) {
        let body = esc(t.text);
        for (const ph of t.ph ?? []) body = body.split(esc(ph)).join(`<span class="ph">${esc(ph)}</span>`);
        const lang = (t.lang ?? '').trim().split(/\s+/)[0];
        if (!lang) {
          return `<figure${attrs(merge({ class: 'term term--log' }, t.attrs))}><div class="term__bar" aria-hidden="true"><span class="term__dot"></span><span class="term__dot"></span><span class="term__dot"></span><span class="term__title">Logs/server-main.log</span></div><pre class="term__body" tabindex="0">${t.wrapped ? body.replace(/\n/g, ' ') : body}</pre></figure>\n${t.after ?? ''}`;
        }
        return `<div${attrs(merge({ class: 'code', 'data-lang': lang }, t.attrs))}><div class="code__bar"><span class="code__label">${esc(t.label ?? lang)}</span>${t.where ? `<span class="code__where">${esc(t.where)}</span>` : ''}</div><pre><code>${body}</code></pre></div>\n${t.after ?? ''}`;
      },
      table(t) {
        const labels = t.header.map(plain);
        const head = t.header.map((h) => `<th scope="col">${this.parser.parseInline(h.tokens)}</th>`).join('');
        const body = t.rows.map((r, i) => `<tr${attrs(t.rowAttrs?.[i])}>${r.map((c, j) => `<td data-label="${esc(labels[j])}">${this.parser.parseInline(c.tokens)}</td>`).join('')}</tr>`).join('\n');
        const stop = 'data-config' in (t.attrs ?? {}) ? '' : ' tabindex="0"';      // a keyboard scrolls a wide table through its wrapper; a config table is stacked at every width
        return `<div class="table-wrap"${stop} role="region" aria-label="${esc(t.label ?? labels.join(', '))}"><table${attrs(merge(labels.length >= 2 ? { class: 'table--stack' } : {}, t.attrs))}><thead><tr>${head}</tr></thead><tbody>\n${body}\n</tbody></table></div>\n${t.after ?? ''}`;
      },
      list(t) {
        const tag = t.ordered ? 'ol' : 'ul';
        return `<${tag}${attrs(merge(t.ordered && t.start !== 1 ? { start: t.start } : {}, t.attrs))}>\n${t.items.map((i) => this.listitem(i)).join('')}</${tag}>\n${t.after ?? ''}`;
      },
      listitem(i) {
        return `<li${attrs(i.attrs)}>${i.prepend ?? ''}${this.parser.parse(i.tokens)}${i.append ?? ''}</li>\n`;
      },
      link(t) {
        const url = rewrite(t.href);
        return `<a${isExternal(url) ? ' class="ext"' : ''} href="${esc(url)}"${t.title ? ` title="${esc(t.title)}"` : ''}>${t.autolink ? esc(t.text) : this.parser.parseInline(t.tokens)}</a>`;
      },
      image(t) { throw new Error(`an image (${t.href}) reached the renderer: the site shows no image from a document`); },
      html(t) { throw new Error(`raw HTML (${t.raw.trim().slice(0, 40)}) reached the renderer: the site uses no marker`); },
    } });
    return md;
  }

  /**
   * tokens: a run of top-level tokens (a copy, from slice()); file: the document they come from, for
   * links; page: the slug the HTML will sit on, for relative addresses; shift: how many levels the
   * headings are promoted (a page made of one README section promotes H2 to H1).
   */
  function render(tokens, { file, page = '', shift = 0 } = {}) {
    const html = make({ file, page, shift }).parser(tokens);
    return { html, ...scan(tokens, shift) };
  }

  /** Markdown written by the site itself (a sentence with a code span): inline HTML, links rewritten when `file` is given. */
  function inline(text, { file, page = '' } = {}) {
    return make({ file, page }).parseInline(text);
  }

  return { render, inline };
}
