// Grafana Cloud helper (path B, step 4). The pure rules come first, the DOM part last.
// The guide documents two transformations: take everything after "Authorization=", and turn every
// %20 back into a space. Reading a value out of a pasted line is parsing, not a third
// transformation. Everything else is reported with a code and left as pasted, never guessed.
//
// Privacy rules. The two values live in the closure of mountHelper and nowhere else.
// They are never written to storage, the address, an attribute, a title, an aria-label, the live
// region, an error or the developer console. A paste or a drop is read from the event and
// prevented, so it never becomes the content of the box. The credential is in the page text only
// while "show the credential" is pressed. test/helper.test.mjs reads this file without its
// comments and fails on any word that would reach out of the page or leave a trace in it.
import { fromHtml } from './dom.js';

const unquote = (s) => {
  s = s.trim();
  const q = s[0];
  return s.length > 1 && (q === '"' || q === "'") && s.at(-1) === q ? s.slice(1, -1).trim() : s;
};
const valueOf = (text, name) => { const m = new RegExp(name + '\\s*=\\s*(.*)').exec(text); return m ? unquote(m[1]) : null; };
const linesOf = (text) => text.split(/\r?\n/).map((l) => unquote(l.replace(/^\s*export\s+/, ''))).filter(Boolean);

/** What one paste holds: { endpoint, authorization, notes }. A value that was not found, or that is unusable, is null. */
export function readPaste(text) {
  const notes = [], lines = linesOf(text);
  let endpoint = valueOf(text, 'OTEL_EXPORTER_OTLP_ENDPOINT') ?? lines.find((l) => /^https?:\/\//i.test(l)) ?? null;
  if (endpoint !== null) {
    let url = null;
    try { url = new URL(endpoint); } catch { /* reported below */ }
    if (!url || !/^https?:$/.test(url.protocol) || /\s/.test(endpoint)) { notes.push('endpoint-not-a-url'); endpoint = null; }
    else if (/\/v1\/metrics\/?$/.test(url.pathname)) notes.push('endpoint-has-signal-path');
  }
  const header = valueOf(text, 'OTEL_EXPORTER_OTLP_HEADERS') ?? lines.find((l) => /^Authorization=/i.test(l)) ?? lines.find((l) => /^Basic(%20| )/i.test(l)) ?? null;
  let authorization = null;
  if (header !== null) {
    const at = header.search(/Authorization=/i);
    if (at < 0) notes.push('no-authorization-prefix');
    const value = (at < 0 ? header : header.slice(at + 'Authorization='.length)).replaceAll('%20', ' ').trim();
    if (!value) notes.push('authorization-empty');
    else if (/[\u0000-\u001f\u007f]/.test(value)) notes.push('authorization-has-control-character');
    else if (value.includes(',')) notes.push('comma-in-value');
    else {
      authorization = value;
      if (/%[0-9a-f]{2}/i.test(value)) notes.push('other-percent-sequence');
    }
  }
  if (endpoint === null && header === null && !notes.length) notes.push('nothing-recognised');
  return { endpoint, authorization, notes };
}

/** The guide's own json block with the values that are known put in place of its placeholders. */
export const PLACEHOLDERS = { endpoint: 'https://otlp-gateway-<region>.grafana.net/otlp', authorization: '<everything after Authorization= from step 1>' };
export function fillBlock(block, values) {
  let out = block;
  for (const key of ['endpoint', 'authorization']) {
    if (values[key] != null) out = out.replace(JSON.stringify(PLACEHOLDERS[key]), () => JSON.stringify(values[key]));
  }
  return out;
}

/** What the screen shows in place of a credential: the scheme word when it is Basic or Bearer, then twelve stars. Any other first word could be the secret itself. */
export const maskSecret = (value) => {
  const word = value.slice(0, value.indexOf(' ') + 1);        // no regular expression: it would leave the value in RegExp.input
  return (['basic ', 'bearer '].includes(word.toLowerCase()) ? word : '') + '*'.repeat(12);
};

// ------------------------------------------------------------------ DOM part
// Every string the helper shows, in one place. A note is a code that the rules above return; its
// sentence is fixed and never holds a value.
const TEXT = {
  title: 'Paste what Grafana Cloud printed',
  labelStart: 'Both ', labelCode: 'OTEL_EXPORTER_OTLP', labelEnd: ' lines, or one value at a time',
  hint: 'Read, then removed from this box. Kept in memory until you leave this page: never stored, never sent.',
  endpointRead: (host) => '[x] Endpoint read: ' + host, secretRead: '[x] Authorization value read', hidden: ' (hidden)', warn: '[!] ',
  show: 'show the credential', forget: 'forget both values',
  pointer: '[i] The helper at step 4 does this for you.',
  said: { both: 'Both values read. The block under step 4 now holds them.', endpoint: 'Endpoint read.', secret: 'Authorization value read.', none: 'Nothing was read from that paste.', forgotten: 'Both values forgotten.' },
  notes: {
    'nothing-recognised': 'Neither value was found in that paste. Paste the lines that start with OTEL_EXPORTER_OTLP_ENDPOINT and OTEL_EXPORTER_OTLP_HEADERS.',
    'endpoint-not-a-url': 'The endpoint is not an http or https address. Use the endpoint as printed.',
    'endpoint-has-signal-path': 'Endpoint is the base address only, Pulse adds the rest of the path itself.',
    'no-authorization-prefix': 'No Authorization= in front of the value. It was taken as the value itself: check it against what Grafana Cloud printed.',
    'authorization-empty': 'Nothing after Authorization=. Paste the whole OTEL_EXPORTER_OTLP_HEADERS value.',
    'authorization-has-control-character': 'The value holds a tab and was not used. Paste it again as one line.',
    'other-percent-sequence': 'The value still holds a % sequence other than %20. The guide only covers %20, so it is left as pasted.',
    'comma-in-value': 'The value holds a comma and was not used: Pulse refuses to start exporting with a comma in a header value. Paste the Authorization header alone, up to the comma.',
  },
};
const ABOUT_ENDPOINT = ['endpoint-not-a-url', 'endpoint-has-signal-path'];

/** root: the guide's article; say: (text) => void, the wizard's live region; setCopyText: from copy.js. */
export function mountHelper(root, say, setCopyText) {
  const box = root.querySelector('[data-helper]');
  if (!box) return;
  const code = box.querySelector('code'), template = code.textContent;
  const slots = Array.from(code.querySelectorAll('.ph'));
  if (slots.length !== 2 || slots[0].textContent !== PLACEHOLDERS.endpoint || slots[1].textContent !== PLACEHOLDERS.authorization) return;   // the guide's block changed shape: leave it alone
  const ui = fromHtml('<div class="helper"><p class="helper__title"></p>' +
    '<div class="field field--secret"><label for="gs-paste"></label>' +
    '<textarea class="input" id="gs-paste" rows="3" autocomplete="off" autocapitalize="off" autocorrect="off" spellcheck="false" aria-describedby="gs-paste-hint gs-paste-notes"></textarea>' +
    '<span class="hint" id="gs-paste-hint"></span></div>' +
    '<ul class="helper__read" hidden></ul><ul class="helper__notes" id="gs-paste-notes" hidden></ul>' +
    '<div class="helper__tools" hidden><button type="button" class="chip" data-show aria-pressed="false"></button>' +
    '<button type="button" class="btn btn--sm btn--quiet" data-forget></button></div></div>');
  const field = ui.querySelector('textarea'), read = ui.querySelector('.helper__read'), list = ui.querySelector('.helper__notes');
  const tools = ui.querySelector('.helper__tools'), showBtn = ui.querySelector('[data-show]'), forgetBtn = ui.querySelector('[data-forget]');
  ui.querySelector('.helper__title').textContent = TEXT.title;
  const label = ui.querySelector('label'), tag = document.createElement('code');
  tag.textContent = TEXT.labelCode;
  label.append(TEXT.labelStart, tag, TEXT.labelEnd);
  ui.querySelector('.hint').textContent = TEXT.hint;
  showBtn.textContent = TEXT.show;
  forgetBtn.textContent = TEXT.forget;
  box.before(ui);
  let endpoint = null, secret = null, show = false;
  let endpointNotes = [], secretNotes = [], pasteNotes = [];   // about the value in memory; about the last paste only

  const inJson = (v) => JSON.stringify(v).slice(1, -1);
  function draw() {
    const notes = endpointNotes.concat(secretNotes, pasteNotes);
    slots[0].textContent = endpoint === null ? PLACEHOLDERS.endpoint : inJson(endpoint);
    slots[0].className = endpoint === null ? 'ph' : 'val';
    slots[1].textContent = secret === null ? PLACEHOLDERS.authorization : inJson(show ? secret : maskSecret(secret));
    slots[1].className = secret === null ? 'ph' : 'val';
    read.textContent = '';
    if (endpoint !== null) read.appendChild(fromHtml('<li></li>')).textContent = TEXT.endpointRead(new URL(endpoint).host);
    if (secret !== null) read.appendChild(fromHtml('<li></li>')).textContent = TEXT.secretRead + (show ? '' : TEXT.hidden);
    read.hidden = endpoint === null && secret === null;
    list.textContent = '';
    for (const n of notes) list.appendChild(fromHtml('<li class="error"></li>')).textContent = TEXT.warn + TEXT.notes[n];
    list.hidden = !notes.length;
    field.setAttribute('aria-invalid', String(notes.length > 0));
    tools.hidden = read.hidden;
    showBtn.hidden = secret === null;
    showBtn.setAttribute('aria-pressed', String(show));
  }
  function take(text) {
    const r = readPaste(text);
    const aboutEndpoint = r.notes.filter((n) => ABOUT_ENDPOINT.includes(n));
    const aboutSecret = r.notes.filter((n) => !ABOUT_ENDPOINT.includes(n) && n !== 'nothing-recognised');
    pasteNotes = r.notes.filter((n) => n === 'nothing-recognised');
    // a note stays with the value it is about; a paste that brought no usable value only reports
    if (r.endpoint !== null) { endpoint = r.endpoint; endpointNotes = aboutEndpoint; } else pasteNotes = pasteNotes.concat(aboutEndpoint);
    if (r.authorization !== null) { secret = r.authorization; secretNotes = aboutSecret; } else pasteNotes = pasteNotes.concat(aboutSecret);
    field.value = '';
    draw();
    say(r.endpoint !== null && r.authorization !== null ? TEXT.said.both : r.endpoint !== null ? TEXT.said.endpoint : r.authorization !== null ? TEXT.said.secret : TEXT.said.none);
  }
  function forget() {
    endpoint = secret = null; show = false; endpointNotes = []; secretNotes = []; pasteNotes = []; field.value = ''; draw();
    /^/.test('');                       // a successful empty match replaces RegExp.input, which still holds the last pasted line
  }

  field.addEventListener('paste', (e) => { e.preventDefault(); take(e.clipboardData.getData('text')); });
  field.addEventListener('drop', (e) => { e.preventDefault(); take(e.dataTransfer.getData('text')); });
  field.addEventListener('change', () => { if (field.value) take(field.value); });      // typed by hand: taken when the box loses focus
  showBtn.addEventListener('click', () => { show = !show; draw(); });
  forgetBtn.addEventListener('click', () => { forget(); say(TEXT.said.forgotten); });
  addEventListener('pagehide', forget);
  setCopyText(box, () => fillBlock(template, { endpoint, authorization: secret }));
  const pointer = root.querySelector('[data-helper-note]');
  if (pointer) pointer.appendChild(fromHtml('<p class="helper__pointer"></p>')).textContent = TEXT.pointer;
  draw();
}
