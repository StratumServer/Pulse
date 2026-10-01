// Site search: the button in [data-slot="search"], the panel under the top bar, and the lazy load of
// assets/search-index.js on first use. The index is a classic script that sets window.PULSE_SEARCH: a fetch
// of a sibling file is refused from file:// and by the page's policy, a script element is not. Nothing typed
// is stored or put in the address, and results are built with DOM nodes and textContent, never innerHTML.
// The index is an array of { u, t, c, x }: address from the site root, title, crumb (the page it is on), text.
import { h } from './dom.js';

const TEXT = {
  button: 'Search the documentation',
  word: 'search',
  label: 'Search the documentation',
  prompt: 'grep>',
  key: '/',
  placeholder: 'search the docs',
  results: 'Search results',
  loading: 'Loading the index.',
  failed: 'The index did not load.',
  hint: 'Try bind, attribution or otlp.',
  none: 'No match. Follow the white rabbit: try bind, attribution or otlp.',
  found: (n) => `${n} ${n === 1 ? 'result' : 'results'}`,
  capped: (limit) => `The first ${limit} results.`,
};
const LIMIT = 8;

/** Entries that hold every term, best first. A term in a title outweighs any number of terms in the text. */
export function search(index, query, limit = LIMIT) {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean).slice(0, 6);
  if (!terms.length) return [];
  const hits = [];
  index.forEach((entry, i) => {
    const title = entry.t.toLowerCase(), text = entry.x.toLowerCase();
    let score = 0;
    for (const term of terms) {
      const at = title.indexOf(term);
      if (at < 0 && !text.includes(term)) return;          // every term must be somewhere in the entry
      score += at === 0 ? 30 : at > 0 ? 20 : 1;
    }
    hits.push({ entry, score, i });
  });
  return hits.sort((a, b) => b.score - a.score || a.i - b.i).slice(0, limit).map((h) => h.entry);
}

/** The words around the first match, as [before, match, after]; the caller builds the <mark> with DOM nodes. */
export function snippet(text, query, radius = 60) {
  const terms = query.toLowerCase().split(/\s+/).filter(Boolean), low = text.toLowerCase();
  let at = -1, len = 0;
  for (const term of terms) { const i = low.indexOf(term); if (i >= 0 && (at < 0 || i < at)) { at = i; len = term.length; } }
  if (at < 0) return [text.slice(0, 2 * radius) + (text.length > 2 * radius ? ' ...' : ''), '', ''];
  const from = Math.max(0, at - radius), to = Math.min(text.length, at + len + radius);
  return [(from ? '... ' : '') + text.slice(from, at), text.slice(at, at + len), text.slice(at + len, to) + (to < text.length ? ' ...' : '')];
}

/** The status line: how many results, never the query (a live region must not repeat what was typed). */
export function statusText(found, limit = LIMIT) {
  if (!found) return TEXT.none;
  return found > limit ? TEXT.capped(limit) : TEXT.found(found);
}

export function initSearch() {
  const slot = document.querySelector('[data-slot="search"]');
  if (!slot) return;
  const root = document.documentElement.dataset.root || '';

  const button = h('button', { type: 'button', className: 'btn btn--sm btn--quiet' }, { 'data-search-open': '', 'aria-keyshortcuts': TEXT.key, 'aria-label': TEXT.button, 'aria-expanded': 'false', 'aria-controls': 'search-panel' });
  button.append(TEXT.key, h('span', { className: 'nav__word', textContent: TEXT.word }));
  slot.append(button);

  const input = h('input', { id: 'q', type: 'search', placeholder: TEXT.placeholder, autocomplete: 'off', spellcheck: false }, { role: 'combobox', 'aria-expanded': 'false', 'aria-controls': 'hits', 'aria-autocomplete': 'list' });
  const box = h('div', { className: 'search__box' });
  box.append(h('span', { textContent: TEXT.prompt }, { 'aria-hidden': 'true' }), input, h('kbd', { textContent: TEXT.key }, { 'aria-hidden': 'true' }));
  const status = h('p', { className: 'search__status' }, { role: 'status' });
  // tabindex -1 on the two scrolling boxes: a browser makes a scroller a Tab stop, and the field is where the keys are
  const list = h('ul', { id: 'hits', className: 'search__results', hidden: true }, { role: 'listbox', 'aria-label': TEXT.results, tabindex: '-1' });
  const panel = h('div', { id: 'search-panel', className: 'search', hidden: true }, { role: 'search', 'aria-label': TEXT.label, tabindex: '-1' });
  panel.append(h('label', { className: 'sr-only', htmlFor: 'q', textContent: TEXT.label }), box, status, list);
  const header = document.querySelector('header.nav');
  if (header) header.after(panel); else document.body.append(panel);       // right after the top bar, so Tab leaves it into the page

  let index = Array.isArray(window.PULSE_SEARCH) ? window.PULSE_SEARCH : null;
  let loading = false;
  let links = [];
  let active = 0;

  function select(i) {
    if (!links.length) { input.removeAttribute('aria-activedescendant'); return; }
    active = (i + links.length) % links.length;
    links.forEach((a, n) => a.parentNode.setAttribute('aria-selected', String(n === active)));
    input.setAttribute('aria-activedescendant', links[active].parentNode.id);
    links[active].parentNode.scrollIntoView({ block: 'nearest' });
  }

  function render() {
    const q = input.value.trim();
    links = [];
    list.replaceChildren();
    if (!index) { status.textContent = loading ? TEXT.loading : TEXT.failed; }
    else if (!q) { status.textContent = TEXT.hint; }
    else {
      const found = search(index, q, LIMIT + 1);
      status.textContent = statusText(found.length);
      found.slice(0, LIMIT).forEach((entry, i) => {
        const [before, hit, after] = snippet(entry.x, q);
        const snip = h('span', { className: 'search__snip' });
        snip.append(before);
        if (hit) snip.append(h('mark', { textContent: hit }));
        snip.append(after);
        const a = h('a', { href: root + entry.u, tabIndex: -1 });
        a.append(h('span', { className: 'search__path', textContent: entry.c === entry.t ? entry.t : `${entry.c} > ${entry.t}` }), snip);
        const li = h('li', { id: `hit-${i}` }, { role: 'option', 'aria-selected': 'false' });
        li.append(a);
        list.append(li);
        links.push(a);
      });
    }
    list.hidden = !links.length;
    input.setAttribute('aria-expanded', String(links.length > 0));
    select(0);
  }

  function load() {
    if (loading) return;
    loading = true;
    const script = h('script', { src: `${root}assets/search-index.js` });
    script.addEventListener('load', () => { loading = false; index = Array.isArray(window.PULSE_SEARCH) ? window.PULSE_SEARCH : null; render(); });
    script.addEventListener('error', () => { loading = false; script.remove(); render(); });     // the next opening tries again
    document.head.append(script);
  }

  function open() {
    panel.hidden = false;
    button.setAttribute('aria-expanded', 'true');
    input.focus();
    if (!index) load();
    render();
  }
  function close(giveFocusBack) {
    panel.hidden = true;
    button.setAttribute('aria-expanded', 'false');
    if (giveFocusBack) button.focus();
  }

  button.addEventListener('click', () => { if (panel.hidden) open(); else close(true); });
  input.addEventListener('input', render);
  panel.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      if (links.length) { e.preventDefault(); select(active + (e.key === 'ArrowDown' ? 1 : -1)); }
    } else if (e.key === 'Enter') {
      if (links.length) { e.preventDefault(); links[active].click(); }
    } else if (e.key === 'Escape') {
      e.preventDefault();
      if (input.value) { input.value = ''; render(); } else close(true);
    }
  });
  list.addEventListener('click', (e) => { if (e.target.closest && e.target.closest('a')) close(false); });
  // the panel closes when the focus or the pointer goes elsewhere
  panel.addEventListener('focusout', (e) => { if (e.relatedTarget && !panel.contains(e.relatedTarget) && e.relatedTarget !== button) close(false); });
  document.addEventListener('pointerdown', (e) => { if (!panel.hidden && !panel.contains(e.target) && !button.contains(e.target)) close(false); });
  // "/" opens it from anywhere that is not a field
  document.addEventListener('keydown', (e) => {
    if (e.key !== '/' || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
    if (e.target.closest && e.target.closest('input, textarea, select, [contenteditable]')) return;
    e.preventDefault();
    open();
  });
}
