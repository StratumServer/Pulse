// node --test: the Grafana Cloud helper. Its rules against the guide's own block, and the source-level
// promise that nothing the reader pastes can leave a trace: the browser proof is the driver of
// tools/drive-getting-started.mjs, this is what stops a later edit from adding a log line or a stored
// value without anyone noticing. The same reading, with its own list, keeps the wizard script to the
// rules of the client scripts (the last test).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { Marked } from 'marked';
import { readPaste, fillBlock, maskSecret, PLACEHOLDERS } from '../js/helper.js';

const ROOT = new URL('../../../', import.meta.url);
const guide = readFileSync(new URL('docs/getting-started.md', ROOT), 'utf8');
const jsonBlock = () => {
  const found = [];
  const walk = (tokens) => { for (const t of tokens) { if (t.type === 'code' && t.lang === 'json') found.push(t.text); walk(t.items ?? []); walk(t.tokens ?? []); } };
  walk(new Marked({ gfm: true }).lexer(guide));
  assert.equal(found.length, 1, 'the guide holds one json block');
  return found[0];
};
const BLOCK = jsonBlock();

const EP = 'https://otlp-gateway-prod-eu-west-2.grafana.net/otlp';
const AUTH = 'Basic MTIzNDU2OmdsY19leGFtcGxldG9rZW4=';              // the README's own example value, not a credential
const ENC = 'Authorization=Basic%20MTIzNDU2OmdsY19leGFtcGxldG9rZW4=';
const both = { endpoint: EP, authorization: AUTH, notes: [] };
const only = (o) => ({ endpoint: null, authorization: null, notes: [], ...o });

test('the guide block holds both placeholders, which is what the helper fills', () => {
  assert.ok(BLOCK.includes(JSON.stringify(PLACEHOLDERS.endpoint)) && BLOCK.includes(JSON.stringify(PLACEHOLDERS.authorization)));
  assert.deepEqual(Object.keys(JSON.parse(BLOCK)), ['Endpoint', 'Protocol', 'Headers']);
});

test('what one paste holds: the whole printed block, in any of its shapes', () => {
  const is = (text, want) => assert.deepEqual(readPaste(text), want, JSON.stringify(text));
  // with and without export, either quote style, Windows line ends, PowerShell
  is(`export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"\nexport OTEL_EXPORTER_OTLP_ENDPOINT="${EP}"\nexport OTEL_EXPORTER_OTLP_HEADERS="${ENC}"\n`, both);
  is(`OTEL_EXPORTER_OTLP_ENDPOINT=${EP}\r\nOTEL_EXPORTER_OTLP_HEADERS=${ENC}\r\n`, both);
  is(`export OTEL_EXPORTER_OTLP_HEADERS='${ENC}'\nexport OTEL_EXPORTER_OTLP_ENDPOINT='${EP}'`, both);
  is(`$env:OTEL_EXPORTER_OTLP_ENDPOINT = "${EP}"\n$env:OTEL_EXPORTER_OTLP_HEADERS = "${ENC}"`, both);
  // two bare values in one paste
  is(`${EP}\n${ENC}`, both);
  is(`  "${EP}"  \n  "${ENC}"  `, both);
});

test('one value at a time: the other stays null and is not an error', () => {
  const is = (text, want) => assert.deepEqual(readPaste(text), want, JSON.stringify(text));
  is(EP, only({ endpoint: EP }));
  is(`OTEL_EXPORTER_OTLP_ENDPOINT="${EP}"`, only({ endpoint: EP }));
  is(ENC, only({ authorization: AUTH }));
  is(`export OTEL_EXPORTER_OTLP_HEADERS="${ENC}"`, only({ authorization: AUTH }));
  is('Authorization=' + AUTH, only({ authorization: AUTH }));                       // already a real space
  is('Authorization=Basic%20a%20b', only({ authorization: 'Basic a b' }));          // every %20
});

test('accepted, and said: the value is taken and a note says what to look at', () => {
  const is = (text, want) => assert.deepEqual(readPaste(text), want, JSON.stringify(text));
  is('Basic%20MTIzNDU2OmdsY19leGFtcGxldG9rZW4=', only({ authorization: AUTH, notes: ['no-authorization-prefix'] }));
  is(`OTEL_EXPORTER_OTLP_HEADERS="${AUTH}"`, only({ authorization: AUTH, notes: ['no-authorization-prefix'] }));
  is('Authorization=Basic%20abc%3D', only({ authorization: 'Basic abc%3D', notes: ['other-percent-sequence'] }));   // left as pasted, never guessed
  is(EP + '/v1/metrics', only({ endpoint: EP + '/v1/metrics', notes: ['endpoint-has-signal-path'] }));
});

test('refused: nothing is taken and the note says why', () => {
  const is = (text, want) => assert.deepEqual(readPaste(text), want, JSON.stringify(text));
  is('OTEL_EXPORTER_OTLP_ENDPOINT=otlp-gateway-prod-eu-west-2.grafana.net/otlp', only({ notes: ['endpoint-not-a-url'] }));
  is('OTEL_EXPORTER_OTLP_ENDPOINT=javascript:alert(1)', only({ notes: ['endpoint-not-a-url'] }));
  is('OTEL_EXPORTER_OTLP_ENDPOINT=https://a b', only({ notes: ['endpoint-not-a-url'] }));
  is('Authorization=', only({ notes: ['authorization-empty'] }));
  is('OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic%20abc\tdef"', only({ notes: ['authorization-has-control-character'] }));
  is(ENC + ',X-Scope-OrgID=1', only({ notes: ['comma-in-value'] }));                  // a value Pulse would refuse is never handed over
  is('', only({ notes: ['nothing-recognised'] }));
  is('hello', only({ notes: ['nothing-recognised'] }));
  is(BLOCK, only({ notes: ['nothing-recognised'] }));                    // the guide's own block is not a value
});

test('no note ever carries a value', () => {
  let seen = 0;
  for (const t of [ENC + ',x', 'Authorization=Basic%20abc%3D', EP + '/v1/metrics', 'OTEL_EXPORTER_OTLP_ENDPOINT=nope', 'Basic%20abc']) {
    for (const n of readPaste(t).notes) { seen++; assert.match(n, /^[a-z-]+$/); }
  }
  assert.equal(seen, 5);
});

test('fillBlock: only the placeholders change, one or both; the result is JSON; odd characters survive', () => {
  assert.deepEqual(JSON.parse(fillBlock(BLOCK, both)), { Endpoint: EP, Protocol: 'http/protobuf', Headers: { Authorization: AUTH } });
  assert.equal(fillBlock(BLOCK, {}), BLOCK);
  assert.equal(JSON.parse(fillBlock(BLOCK, { endpoint: EP })).Headers.Authorization, PLACEHOLDERS.authorization);
  const nasty = 'Basic a"b\\c$&$1';
  assert.equal(JSON.parse(fillBlock(BLOCK, { endpoint: EP, authorization: nasty })).Headers.Authorization, nasty, 'a replacement function, so $& is not read as a pattern');
  assert.equal(fillBlock(BLOCK, both).replace(JSON.stringify(EP), JSON.stringify(PLACEHOLDERS.endpoint)).replace(JSON.stringify(AUTH), JSON.stringify(PLACEHOLDERS.authorization)), BLOCK, 'the output is the guide block, nothing else moved');
  assert.equal(readPaste(ENC).authorization, 'Basic MTIzNDU2OmdsY19leGFtcGxldG9rZW4=', 'the README rule gives the README value');
});

test('the mask shows the scheme and nothing about the length', () => {
  assert.equal(maskSecret(AUTH), 'Basic ************');
  assert.equal(maskSecret('x'.repeat(200)), '************');
  assert.equal(maskSecret('Bearer abc def'), 'Bearer ************');
  assert.equal(maskSecret('glc_abc def'), '************', 'a first word that is no scheme could be the secret itself');
  assert.equal(maskSecret(''), '************');
});

// ------------------------------------------------------------------ the source-level promise
const FORBIDDEN = /localStorage|sessionStorage|indexedDB|cookie|history|location|console|fetch|XMLHttpRequest|sendBeacon|WebSocket|postMessage/i;

/**
 * The code of a script without its comments. A comment starts where a // or a slash-star follows the
 * start, white space, ; { or }, outside a string, so the // of an address inside quotes, or of a
 * regular expression, never hides the code that follows. When in doubt the text is kept: a false
 * alarm is visible, a hidden word would not be.
 */
export function withoutComments(src) {
  let out = '', quote = '';
  for (let i = 0; i < src.length; i++) {
    const c = src[i], before = src[i - 1] ?? '\n';
    if (quote) {
      out += c;
      if (c === '\\') out += src[++i] ?? '';
      else if (c === quote || (c === '\n' && quote !== '`')) quote = '';
    } else if (c === '"' || c === "'" || c === '`') { quote = c; out += c; }
    else if (c === '/' && src[i + 1] === '/' && /[\s;{}]/.test(before)) { while (i < src.length && src[i] !== '\n') i++; out += '\n'; }
    else if (c === '/' && src[i + 1] === '*' && /[\s;{}]/.test(before)) { const end = src.indexOf('*/', i + 2); i = end < 0 ? src.length : end + 1; out += ' '; }
    else out += c;
  }
  return out;
}
const leaks = (src) => withoutComments(src).match(new RegExp(FORBIDDEN.source, 'gi')) ?? [];

test('helper.js holds no word that could store, send or log what was pasted', () => {
  const src = readFileSync(new URL('../js/helper.js', import.meta.url), 'utf8');
  assert.deepEqual(leaks(src), [], 'in its code, comments left out');
  assert.match(src, /localStorage|console/, 'the comments do name them: the test is reading past comments, not an empty file');
  // markup only ever receives literals written in the module: every call of fromHtml() starts with a quote, and the one place that turns it into elements is js/dom.js
  const calls = [...withoutComments(src).matchAll(/\bfromHtml\(\s*(.)/g)].map((m) => m[1]);
  assert.ok(calls.length >= 5 && calls.every((c) => c === "'"), `fromHtml() is called with a string literal only, found ${JSON.stringify(calls)}`);
  assert.equal([...withoutComments(src).matchAll(/innerHTML/g)].length, 0, 'the module builds no markup itself');
  assert.doesNotMatch(withoutComments(src), /\.style\b|setAttribute\(\s*['"]style/, 'no style attribute: the policy blocks it');
});

test('the check itself: it reads past comments, and it catches a planted leak in code', () => {
  assert.deepEqual(leaks('// never in localStorage or console\nconst a = 1; /* history */ const b = 2;'), []);
  assert.deepEqual(leaks('const a = 1;\n/* a block\n   about sendBeacon\n*/ const b = 2;'), []);
  assert.deepEqual(leaks('const x = 1; console.log(secret); // fine'), ['console']);
  assert.deepEqual(leaks("const u = 'http://example.org/'; console.log(u)"), ['console']);
  assert.deepEqual(leaks('const r = /^https?:\\/\\//i; fetch(r)'), ['fetch']);
  assert.deepEqual(leaks('const s = "// not a comment"; localStorage.setItem(k, v)'), ['localStorage']);
  assert.deepEqual(leaks('x = `a ${b} // c`; location.hash = x'), ['location']);
  assert.deepEqual(leaks('a(); // one\nb(); // two\ndocument.cookie = x'), ['cookie']);
  assert.deepEqual(leaks('const s = "it\\"s"; history.pushState(s)'), ['history']);
  assert.deepEqual(leaks('new WebSocket(u); new XMLHttpRequest(); navigator.sendBeacon(u); postMessage(m); indexedDB.open(n); sessionStorage.x'),
    ['WebSocket', 'XMLHttpRequest', 'sendBeacon', 'postMessage', 'indexedDB', 'sessionStorage']);
});

test('wizard.js keeps the rules of the client scripts: one storage key, literals only into innerHTML, replaceState only, nothing that reaches out', () => {
  const code = withoutComments(readFileSync(new URL('../js/wizard.js', import.meta.url), 'utf8'));
  assert.deepEqual(code.match(/fetch|XMLHttpRequest|sendBeacon|WebSocket|postMessage|cookie|sessionStorage|indexedDB|console|eval\(|document\.write/gi) ?? [], [], 'no request, no other storage, no log');
  const storage = [...code.matchAll(/localStorage\.(\w+)\((\w+)/g)].map((m) => m[1] + ' ' + m[2]);
  assert.deepEqual(storage.sort(), ['getItem KEY', 'removeItem KEY', 'setItem KEY'], 'the one key pulse.gs, read, written, removed');
  assert.match(code, /const KEY = 'pulse\.gs';/);
  const calls = [...code.matchAll(/\bfromHtml\(\s*(.)/g)].map((m) => m[1]);
  assert.ok(calls.length >= 12 && calls.every((c) => c === "'"), `fromHtml() is called with a string literal only, found ${JSON.stringify(calls)}`);
  assert.equal([...code.matchAll(/innerHTML/g)].length, 0, 'the module builds no markup itself');
  assert.equal([...withoutComments(readFileSync(new URL('../js/dom.js', import.meta.url), 'utf8')).matchAll(/innerHTML/g)].length, 1, 'one place turns a literal into elements');
  assert.deepEqual(code.match(/history\.\w+/g), ['history.replaceState'], 'the address is rewritten, never pushed: fifteen ticks must not become fifteen Back presses');
  assert.doesNotMatch(code, /location\.(?:href|assign|replace)\s*[=(]/, 'the wizard never navigates by script');
  assert.deepEqual(code.match(/\.style\b[.\w]*/g), ['.style.left'], 'the one position it computes goes through the object model; no style attribute');
  assert.doesNotMatch(code, /setAttribute\(\s*['"](?:style|on\w+)/, 'no style attribute and no inline handler: the policy blocks them');
  const listened = [...new Set([...code.matchAll(/addEventListener\(\s*'(\w+)'/g)].map((m) => m[1]))].sort();
  assert.deepEqual(listened, ['animationend', 'change', 'click', 'hashchange'], 'clicks, the address and the end of the one beat: a click may be remembered, a keystroke never is');
});
