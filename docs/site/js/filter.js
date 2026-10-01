// List filter for the metric families, the alert rules and the dashboard panels ([data-filter]): a text
// field, one group of chips per facet, and a count in a live region. A region marks its items with
// [data-item] and names its facets in data-filter, by the data-* attribute that carries each facet's
// value on an item. Nothing typed is stored and nothing goes in the address: a link to one family, rule
// or panel is its anchor.
import { h } from './dom.js';

const TEXT = {
  label: 'Filter this list',
  placeholder: 'filter by name or word',
  count: (shown, total) => `${shown} of ${total} shown`,
  none: 'Nothing matches.',
  clear: 'clear',
};

/** item: { text (lower case), facets: { group: 'engine', type: 'gauge' } }
 *  state: { q: 'tick', pressed: { group: ['engine'], type: [] } }
 *  Inside one facet the pressed chips are alternatives; facets and the text combine. */
export function matches(item, state) {
  for (const word of state.q.toLowerCase().split(/\s+/).filter(Boolean)) if (!item.text.includes(word)) return false;
  for (const [facet, values] of Object.entries(state.pressed)) if (values.length && !values.includes(item.facets[facet])) return false;
  return true;
}

/** The values each facet takes on the items, in order of first appearance. */
export function facetValues(items, facets) {
  return Object.fromEntries(facets.map((f) => [f, [...new Set(items.map((i) => i.facets[f]).filter(Boolean))]]));
}

export const countText = (shown, total) => TEXT.count(shown, total);

/** What a card says: its text, minus the disclosure (which names panels and rules) and the copy buttons the script added. */
function textOf(el) {
  const copy = el.cloneNode(true);
  for (const n of copy.querySelectorAll('details, .copy')) n.remove();
  return copy.textContent.toLowerCase();
}

function mount(region) {
  const slot = region.querySelector('[data-slot="filter"]');
  const els = [...region.querySelectorAll('[data-item]')];
  if (!slot || !els.length) return;
  const facets = (region.dataset.filter || '').split(/\s+/).filter(Boolean);
  const items = els.map((el) => ({ el, text: textOf(el), facets: Object.fromEntries(facets.map((f) => [f, el.dataset[f]])) }));
  const state = { q: '', pressed: Object.fromEntries(facets.map((f) => [f, []])) };

  // what goes with a list of cards: the container that holds them, and the paragraph that introduces it
  const boxes = new Map();
  for (const item of items) {
    const box = item.el.parentElement;
    if (box !== region) boxes.set(box, [...(boxes.get(box) || []), item]);
  }
  const leads = new Map([...boxes.keys()].map((box) => [box, box.tagName === 'UL' && box.previousElementSibling && box.previousElementSibling.tagName === 'P' ? box.previousElementSibling : null]));

  // the bar
  const field = h('input', { type: 'search', id: `filter-${region.id}`, className: 'input', placeholder: TEXT.placeholder, autocomplete: 'off', spellcheck: false });
  const status = h('p', { className: 'filterbar__status' }, { role: 'status' });
  const clear = h('button', { type: 'button', className: 'btn btn--sm btn--quiet', textContent: TEXT.clear, hidden: true });
  const bar = h('div', { className: 'filterbar' }, { role: 'search', 'aria-label': TEXT.label });
  const wrap = h('div', { className: 'field' });
  wrap.append(h('label', { className: 'sr-only', htmlFor: field.id, textContent: TEXT.label }), field);
  bar.append(wrap);
  const chips = [];
  const values = facetValues(items, facets);
  for (const facet of facets) {
    if (!values[facet].length) continue;
    const group = h('div', { className: 'filterbar__chips' }, { role: 'group', 'aria-label': facet });
    for (const value of values[facet]) {
      const chip = h('button', { type: 'button', className: 'chip', textContent: value }, { 'aria-pressed': 'false' });
      chip.dataset.facet = facet;
      chip.dataset.value = value;
      chips.push(chip);
      group.append(chip);
    }
    bar.append(group);
  }
  bar.append(status, clear);
  slot.replaceWith(bar);

  function apply() {
    let shown = 0;
    for (const item of items) {
      const ok = matches(item, state);
      item.el.hidden = !ok;
      if (ok) shown++;
    }
    for (const [box, inside] of boxes) {
      const any = inside.some((item) => !item.el.hidden);
      box.hidden = !any;
      if (leads.get(box)) leads.get(box).hidden = !any;
    }
    status.textContent = shown ? countText(shown, items.length) : TEXT.none;
    clear.hidden = shown > 0;
  }
  function reset() {
    state.q = '';
    field.value = '';
    for (const f of facets) state.pressed[f] = [];
    for (const chip of chips) chip.setAttribute('aria-pressed', 'false');
    apply();
  }

  field.addEventListener('input', () => { state.q = field.value; apply(); });
  bar.addEventListener('click', (e) => {
    const chip = e.target.closest && e.target.closest('.chip');
    if (!chip) return;
    const on = chip.getAttribute('aria-pressed') !== 'true';
    chip.setAttribute('aria-pressed', String(on));
    const list = state.pressed[chip.dataset.facet];
    state.pressed[chip.dataset.facet] = on ? [...list, chip.dataset.value] : list.filter((v) => v !== chip.dataset.value);
    apply();
  });
  clear.addEventListener('click', () => { reset(); field.focus(); });     // the button hides itself, so the focus goes to the field
  // a link to a card the filter is hiding (a search result, an edited address) shows the card instead of nothing
  addEventListener('hashchange', (e) => {
    let id = '';
    try { id = decodeURIComponent(new URL(e.newURL).hash.slice(1)); } catch { return; }
    const target = id && document.getElementById(id);
    const hidden = target && region.contains(target) && items.some((item) => item.el.contains(target) && item.el.hidden);
    if (hidden) { reset(); target.scrollIntoView(); }
  });
  apply();
}

export function initFilter() {
  for (const region of document.querySelectorAll('[data-filter]')) mount(region);
}
