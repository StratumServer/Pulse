// The config builder (table[data-config]). It is the README's table itself, not a second form: each table gets a
// fifth column, "Your value", with one control per key, and under it the file those values give, to copy.
// The build wrote what each row says (key, default, type, limits, closed list) on the <tr>, read from the
// README's own cells. Nothing here is stored or sent; a Headers value may be a credential, so it lives in
// its input and in the file shown under the table, in no form, and both are emptied when the page is hidden.
import { addCopyButtons } from './copy.js';
import { h } from './dom.js';

const TEXT = {
  yourValue: 'Your value',
  isDefault: 'This is the default file.',
  changed: 'Changed from the defaults: ',
  reset: 'reset to defaults',
  addHeader: 'add a header',
  remove: 'remove',
  headerName: 'Header name',
  headerValue: 'Header value',
  nameOf: (n) => `Header name ${n}`,
  valueOf: (n) => `Header value ${n}`,
  removeOf: (n) => `remove header ${n}`,
  controlOf: (key, file) => `${key} in ${file}`,
  item: (key, live) => `${key} (${live})`,
  secret: 'Stays in this browser tab. Never stored, never sent.',
  cloud: 'Coming from Grafana Cloud? The helper in Getting started builds this file from what it printed.',
  cloudPath: 'getting-started/index.html?pill=red#cloud-4',
  bindSee: ' See ',
  bindLink: 'A word on the bind address',
  notes: {
    'not-a-whole-number': 'Not a whole number: the default is used.',
    'out-of-range': 'Outside the range the table gives for this key.',
    'bind-not-loopback': 'Changing Bind is a choice you should make on purpose, not a default you inherit.',
    'endpoint-not-a-url': 'Not an http or https address.',
    'endpoint-has-signal-path': 'Base address of the collector, without a signal path.',
    'comma-in-header-value': 'A comma in a header value: Pulse refuses to start exporting.',
    'duplicate-header-name': 'Two rows have the same header name once trimmed: the file keeps only the last one.',
  },
};

/** The file a set of values gives: every key, in table order, nested by the dots of the keys. */
export function buildConfig(rows, values) {
  const out = {};
  for (const r of rows) {
    const path = r.key.split('.');
    let o = out;
    for (const p of path.slice(0, -1)) o = o[p] ??= {};
    o[path.at(-1)] = r.key in values ? values[r.key] : r.default;
  }
  return JSON.stringify(out, null, 2);
}

/** What a control holds, as { value, note }. value is what goes in the file; note is a code or null.
 *  Nothing is corrected: an out of range number is kept and reported, as the mod itself clamps it. */
export function checkValue(row, raw) {
  if (row.type === 'boolean') return { value: !!raw, note: null };
  if (row.type === 'number') {
    const text = String(raw).trim();
    if (!/^-?\d+$/.test(text)) return { value: row.default, note: 'not-a-whole-number' };
    const n = Number(text);
    return { value: n, note: (row.min !== undefined && n < row.min) || (row.max !== undefined && n > row.max) ? 'out-of-range' : null };
  }
  if (row.type === 'object') {                         // Headers: raw is a list of [name, value] pairs
    const pairs = raw.map(([k, v]) => [k.trim(), v]).filter(([k]) => k);
    const names = pairs.map(([k]) => k);
    const note = pairs.some(([, v]) => v.includes(',')) ? 'comma-in-header-value' : new Set(names).size !== names.length ? 'duplicate-header-name' : null;
    return { value: Object.fromEntries(pairs), note };
  }
  const value = String(raw);
  if (row.key === 'Bind') return { value, note: ['127.0.0.1', 'localhost', '::1', '[::1]'].includes(value.trim()) ? null : 'bind-not-loopback' };
  if (row.key === 'Endpoint') {
    let url = null;
    try { url = new URL(value); } catch (e) { url = null; }
    if (!url || !/^https?:$/.test(url.protocol)) return { value, note: 'endpoint-not-a-url' };
    return { value, note: /\/v1\/metrics\/?$/.test(url.pathname) ? 'endpoint-has-signal-path' : null };
  }
  return { value, note: null };
}

/** Keys whose value differs from the default, for the line under the output. */
export function changedKeys(rows, values) {
  return rows.filter((r) => r.key in values && JSON.stringify(values[r.key]) !== JSON.stringify(r.default)).map((r) => r.key);
}

/** The line under the output: what differs, each key with its own "Live or restart" cell. rows: { key, liveText }. */
export function changeLine(rows, values) {
  const changed = new Set(changedKeys(rows, values));
  const list = rows.filter((r) => changed.has(r.key)).map((r) => TEXT.item(r.key, r.liveText));
  return list.length ? `${TEXT.changed}${list.join(', ')}.` : TEXT.isDefault;
}

// ---------------------------------------------------------------- the page

/** What the build wrote on a <tr>, as the row the pure functions read. */
function readRow(tr, file) {
  const d = tr.dataset;
  return {
    key: d.key, id: tr.id, default: JSON.parse(d.default), type: d.type, file,
    ...(d.min !== undefined && { min: Number(d.min) }), ...(d.max !== undefined && { max: Number(d.max) }),
    ...(d.options && { options: JSON.parse(d.options) }),
    liveText: tr.querySelector('[data-label="Live or restart"]').textContent.trim(),
    bindHref: (tr.querySelector('td a') || {}).href,
  };
}

/** The control of one row: { cell, read(), reset(), show(code), clearSecrets() }. */
function control(row) {
  const name = TEXT.controlOf(row.key, row.file.split('/').pop());
  const note = h('span', { className: 'error', id: `${row.id}-note` });
  const field = h('div', { className: 'field' });
  const cell = h('td', {}, { 'data-label': TEXT.yourValue });
  cell.append(field);
  const describe = { 'aria-label': name, 'aria-describedby': note.id, autocomplete: 'off' };
  let main, read, reset;

  if (row.type === 'boolean') {
    main = h('input', { type: 'checkbox', checked: row.default }, describe);
    const word = h('span', { textContent: String(row.default) });
    const label = h('label', { className: 'builder__check' });
    label.append(main, word);
    field.append(label);
    main.addEventListener('change', () => { word.textContent = String(main.checked); });
    read = () => main.checked;
    reset = () => { main.checked = row.default; word.textContent = String(row.default); };
  } else if (row.type === 'number') {
    main = h('input', { type: 'number', step: '1', className: 'input', value: String(row.default), spellcheck: false }, describe);
    field.append(main);
    read = () => main.value;
    reset = () => { main.value = String(row.default); };
  } else if (row.type === 'object') {
    return headers(row, name, note, field, cell);
  } else if (row.options) {
    main = h('select', { className: 'input' }, describe);
    for (const o of row.options) main.append(h('option', { value: o, textContent: o }));
    main.value = row.default;
    field.append(main);
    read = () => main.value;
    reset = () => { main.value = row.default; };
  } else {
    main = h('input', { type: 'text', className: 'input', value: row.default, spellcheck: false }, describe);
    field.append(main);
    read = () => main.value;
    reset = () => { main.value = row.default; };
  }
  field.append(note);
  const show = (code) => {
    say(note, code, row);
    if (code && TEXT.notes[code]) main.setAttribute('aria-invalid', 'true'); else main.removeAttribute('aria-invalid');
  };
  return { cell, read, reset, show, clearSecrets() {} };
}

/** The note under a control: "[!] " and the sentence for the code. The one for Bind ends with the README's own link to its section. */
function say(note, code, row) {
  note.replaceChildren();
  if (!code || !TEXT.notes[code]) return;
  note.append(`[!] ${TEXT.notes[code]}`);
  if (code === 'bind-not-loopback' && row.bindHref) {
    note.append(TEXT.bindSee, h('a', { href: row.bindHref, textContent: TEXT.bindLink }), '.');
  }
}

/** The Headers key: a list of name and value inputs. Values may be credentials: no form, no autofill, no mask, emptied on pagehide. */
function headers(row, name, note, field, cell) {
  field.classList.add('field--secret');
  const list = h('div', { className: 'headers' });
  const add = h('button', { type: 'button', className: 'btn btn--sm btn--quiet', textContent: TEXT.addHeader });
  const hint = h('span', { className: 'hint', textContent: TEXT.secret });
  field.append(list, add, hint, note);
  const quiet = { autocomplete: 'off', autocapitalize: 'off', autocorrect: 'off', spellcheck: 'false', 'aria-describedby': note.id };
  const rows = () => [...list.children];
  const label = () => rows().forEach((r, i) => {
    r.firstChild.setAttribute('aria-label', TEXT.nameOf(i + 1));
    r.children[1].setAttribute('aria-label', TEXT.valueOf(i + 1));
    r.lastChild.setAttribute('aria-label', TEXT.removeOf(i + 1));
  });
  const addRow = () => {
    const r = h('div', { className: 'headers__row' });
    r.append(h('input', { type: 'text', className: 'input', placeholder: TEXT.headerName }, quiet), h('input', { type: 'text', className: 'input', placeholder: TEXT.headerValue }, quiet), h('button', { type: 'button', className: 'btn btn--sm btn--quiet', textContent: TEXT.remove }));
    list.append(r);
    label();
    return r;
  };
  add.addEventListener('click', () => { addRow().firstChild.focus(); });          // the new field is where the reader types next
  list.addEventListener('click', (e) => {
    const b = e.target.closest && e.target.closest('button');
    if (!b) return;
    b.closest('.headers__row').remove();
    label();
    add.focus();                                                                   // the focused button is gone: the focus goes to the one that stays
  });
  const pairs = () => rows().map((r) => [r.firstChild.value, r.children[1].value]);
  const show = (code) => {
    say(note, code, row);
    const names = pairs().map(([k]) => k.trim());
    rows().forEach((r, i) => {
      const dup = code === 'duplicate-header-name' && names[i] && names.indexOf(names[i]) !== names.lastIndexOf(names[i]);
      const comma = code === 'comma-in-header-value' && r.children[1].value.includes(',');
      for (const [input, bad] of [[r.firstChild, dup], [r.children[1], comma]]) { if (bad) input.setAttribute('aria-invalid', 'true'); else input.removeAttribute('aria-invalid'); }
    });
  };
  return { cell, read: pairs, reset: () => { list.replaceChildren(); }, show, clearSecrets: () => { list.replaceChildren(); } };
}

function mount(table) {
  const file = table.dataset.config;
  const rows = [...table.tBodies[0].rows].map((tr) => ({ ...readRow(tr, file), tr }));
  const controls = rows.map((r) => control(r));
  table.tHead.rows[0].append(h('th', { scope: 'col', textContent: TEXT.yourValue }));
  rows.forEach((r, i) => r.tr.append(controls[i].cell));

  const code = h('code');
  const box = h('div', { className: 'code' }, { 'data-lang': 'json' });
  box.append(h('div', { className: 'code__bar' }), h('pre'));
  box.firstChild.append(h('span', { className: 'code__label', textContent: file }));
  box.lastChild.append(code);
  const status = h('p', { className: 'builder__changed' }, { role: 'status' });
  const resetButton = h('button', { type: 'button', className: 'btn btn--sm btn--quiet', textContent: TEXT.reset }, { 'data-reset': '' });
  const tools = h('div', { className: 'builder__tools' });
  tools.append(resetButton);
  const builder = h('div', { className: 'builder' }, { 'data-for': file });
  builder.append(box, status, tools);
  if (file.endsWith('pulse-otlp.json')) {
    const link = h('p', { className: 'builder__link' });
    link.append(h('a', { href: (document.documentElement.dataset.root || '') + TEXT.cloudPath, textContent: TEXT.cloud }));
    builder.append(link);
  }
  const wrap = table.closest('.table-wrap');
  (wrap || table).after(builder);
  addCopyButtons(builder);

  function update() {
    const values = {};
    rows.forEach((r, i) => {
      const { value, note } = checkValue(r, controls[i].read());
      values[r.key] = value;
      controls[i].show(note);
    });
    code.textContent = buildConfig(rows, values);
    status.textContent = changeLine(rows, values);
  }
  for (const type of ['input', 'change', 'click']) table.addEventListener(type, update);     // click: a header row was added or removed
  resetButton.addEventListener('click', () => { controls.forEach((c) => c.reset()); update(); });
  addEventListener('pagehide', () => { controls.forEach((c) => c.clearSecrets()); update(); });
  update();
}

export function initConfig() {
  for (const table of document.querySelectorAll('table[data-config]')) mount(table);
}
