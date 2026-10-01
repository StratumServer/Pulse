// Drives the BUILT Getting started page in headless Chrome over the DevTools protocol, from a file:// URL
// (the hardest case the site promises), at 360 and 1280 px. No dependency: Node 22 has WebSocket built in.
// It is a tool, not part of `npm test` (that needs no browser): run it by hand or from a script.
//
//   node tools/drive-getting-started.mjs              every scenario, against a fresh offline build
//   node tools/drive-getting-started.mjs helper nojs  only these
//
// Environment: CHROME (default google-chrome-stable), PULSE_SITE_DIST (a built site to use instead of
// building one), PULSE_SITE_URL (open the page from a server instead of from files).
// Exit code: non-zero when a scenario fails. One line per scenario.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { setMaxListeners } from 'node:events';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const TMP = mkdtempSync(join(tmpdir(), 'pulse-drive-'));
let DIST = process.env.PULSE_SITE_DIST;
if (!DIST) {
  const { buildSite, writeOutput } = await import('../build.mjs');
  DIST = join(TMP, 'dist');
  writeOutput(DIST, (await buildSite({ offline: true })).files);
}
// from the built files (file://) unless PULSE_SITE_URL names a server that serves them, for example http://127.0.0.1:4173/
const PAGE = process.env.PULSE_SITE_URL ? new URL('getting-started/', process.env.PULSE_SITE_URL).href : pathToFileURL(join(DIST, 'getting-started', 'index.html')).href;

const profile = join(TMP, 'profile');
const chrome = spawn(process.env.CHROME ?? 'google-chrome-stable', ['--headless=new', '--disable-gpu', '--no-sandbox', '--hide-scrollbars', '--remote-debugging-port=0', `--user-data-dir=${profile}`, 'about:blank'], { stdio: ['ignore', 'ignore', 'pipe'] });
const wsUrl = await new Promise((resolve, reject) => {
  let buf = '';
  chrome.stderr.on('data', (d) => { buf += d; const m = buf.match(/DevTools listening on (ws:\/\/\S+)/); if (m) resolve(m[1]); });
  chrome.on('error', reject);
  setTimeout(() => reject(new Error('Chrome did not start')), 20000);
});
const ws = new WebSocket(wsUrl);
setMaxListeners(200, ws);
await new Promise((r) => ws.addEventListener('open', r));
let seq = 0;
const pending = new Map(), waiters = [];
ws.addEventListener('message', (ev) => {
  const msg = JSON.parse(ev.data);
  if (msg.id && pending.has(msg.id)) { const { resolve, reject } = pending.get(msg.id); pending.delete(msg.id); msg.error ? reject(new Error(JSON.stringify(msg.error))) : resolve(msg.result); }
  else if (msg.method) for (const w of waiters.splice(0)) (w.method === msg.method && w.sessionId === msg.sessionId) ? w.resolve(msg.params) : waiters.push(w);
});
const send = (method, params = {}, sessionId) => new Promise((resolve, reject) => { const id = ++seq; pending.set(id, { resolve, reject }); ws.send(JSON.stringify({ id, method, params, sessionId })); });
const once = (method, sessionId) => new Promise((resolve) => waiters.push({ method, sessionId, resolve }));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** A page in its own browser context (fresh storage) unless opts.context says otherwise. */
async function open(url, width = 1280, height = 900, opts = {}) {
  const { browserContextId } = opts.context ? { browserContextId: opts.context } : await send('Target.createBrowserContext');
  const { targetId } = await send('Target.createTarget', { url: 'about:blank', browserContextId });
  const { sessionId } = await send('Target.attachToTarget', { targetId, flatten: true });
  const S = (m, p) => send(m, p, sessionId);
  await S('Page.enable'); await S('Runtime.enable'); await S('Network.enable');
  await S('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  if (opts.noScript) await S('Emulation.setScriptExecutionDisabled', { value: true });
  if (opts.reducedMotion) await S('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-reduced-motion', value: 'reduce' }] });
  if (opts.block) await S('Network.setBlockedURLs', { urls: opts.block });
  // runs before any script of the page: records policy violations, and an optional planted fault
  await S('Page.addScriptToEvaluateOnNewDocument', { source: `window.__csp = []; addEventListener('securitypolicyviolation', (e) => __csp.push(e.violatedDirective + ' ' + e.blockedURI)); ${opts.inject || ''}` });
  const errors = [], console_ = [], requests = [];
  ws.addEventListener('message', (ev) => {
    const m = JSON.parse(ev.data);
    if (m.sessionId !== sessionId) return;
    if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description?.split('\n')[0] || m.params.exceptionDetails.text);
    if (m.method === 'Runtime.consoleAPICalled') console_.push(m.params.args.map((a) => a.value ?? a.description ?? '').join(' '));
    if (m.method === 'Network.requestWillBeSent') requests.push(m.params.request.url + ' ' + (m.params.request.postData ?? ''));
  });
  const loaded = once('Page.loadEventFired', sessionId);
  await S('Page.navigate', { url });
  await loaded;
  await sleep(200);
  const js = async (expression) => {
    const r = await S('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description || r.exceptionDetails.text);
    return r.result.value;
  };
  const key = async (k) => {
    const map = { Tab: [9, 'Tab'], Enter: [13, 'Enter', '\r'], ' ': [32, 'Space', ' '], ArrowDown: [40, 'ArrowDown'], ArrowRight: [39, 'ArrowRight'] };
    const [code, name, text] = map[k];
    await S('Input.dispatchKeyEvent', { type: text ? 'keyDown' : 'rawKeyDown', key: k, code: name, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code, text });
    await S('Input.dispatchKeyEvent', { type: 'keyUp', key: k, code: name, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code });
    await sleep(40);
  };
  const type = async (text) => { await S('Input.insertText', { text }); await sleep(40); };
  const close = async () => { await send('Target.closeTarget', { targetId }); if (!opts.context) await send('Target.disposeBrowserContext', { browserContextId }); };
  return { width, js, key, type, close, errors, console: console_, requests, S, browserContextId };
}

// what every scenario reads back
const PROBE = `(() => {
  const vis = (n) => !!n && n.getClientRects().length > 0;
  const q = (sel) => document.querySelector(sel);
  const acts = [...document.querySelectorAll('.act')].map((a) => ({ act: a.dataset.act + (a.dataset.path || ''), open: a.classList.contains('is-open'), off: a.hasAttribute('data-off'), state: a.querySelector('.fold__state')?.textContent ?? null, kicker: vis(a.querySelector('.act__kicker')), headingVisible: vis(a.querySelector('h2')) }));
  const variants = [...document.querySelectorAll('[data-docker]')].map((v) => ({ docker: v.dataset.docker, open: v.classList.contains('is-open'), off: v.hasAttribute('data-off'), state: v.querySelector('.fold__state')?.textContent ?? null, headingVisible: vis(v.querySelector('h3')) }));
  return { w: innerWidth, url: location.search + location.hash, html: document.documentElement.className, ready: q('[data-gs]').classList.contains('is-ready'),
    count: q('.vitals__count')?.textContent ?? null, path: q('.vitals__path')?.textContent ?? null, status: q('.vitals__status')?.textContent ?? null,
    next: q('[data-next]') ? q('[data-next]').textContent + ' -> ' + q('[data-next]').getAttribute('href') : null,
    acts, variants, osAsked: !q('input[name="os"]')?.closest('fieldset').hidden, dockerAsked: !q('input[name="docker"]')?.closest('fieldset').hidden,
    tunnelOpen: q('[data-tunnel]')?.closest('details')?.open ?? null, current: q('[aria-current="step"]')?.id ?? null,
    stepsVisible: [...document.querySelectorAll('ol[data-list] > li')].filter(vis).length, troubleVisible: [...document.querySelectorAll('[data-list="trouble"] > li')].filter(vis).length,
    headingsVisible: [...document.querySelectorAll('[data-gs] h1, [data-gs] h2, [data-gs] h3')].filter(vis).length + ' of ' + document.querySelectorAll('[data-gs] h1, [data-gs] h2, [data-gs] h3').length,
    overflowX: document.documentElement.scrollWidth - innerWidth, page: document.documentElement.scrollHeight, csp: window.__csp,
    stored: (() => { try { return localStorage.getItem('pulse.gs'); } catch (e) { return 'unavailable'; } })(),
    focus: (() => { const a = document.activeElement; return a === document.body ? 'body' : a.tagName.toLowerCase() + (a.name ? '[' + a.name + '=' + a.value + ']' : '') + ' "' + (a.getAttribute('aria-labelledby') ? document.getElementById(a.getAttribute('aria-labelledby')).textContent : a.textContent || a.closest('label')?.textContent || '').trim().slice(0, 44) + '"'; })() };
})()`;
const tick = (ids) => `${JSON.stringify(ids)}.forEach((id) => document.querySelector('#' + id + ' .step__check input').click())`;
const click = (sel) => `document.querySelector(${JSON.stringify(sel)}).click()`;
const INSTALL = ['install-1', 'install-2', 'install-3', 'install-4', 'install-5'];
const BLOCK = 'export OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"\nexport OTEL_EXPORTER_OTLP_ENDPOINT="https://otlp-gateway-prod-eu-west-2.grafana.net/otlp"\nexport OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic%20MTIzNDU2OmdsY19leGFtcGxldG9rZW4="';
const paste = (text) => `(() => { const dt = new DataTransfer(); dt.setData('text', ${JSON.stringify(text)}); const f = document.getElementById('gs-paste'); f.focus(); f.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true })); })()`;
// the regular expression engine keeps its last match, and the text before and after it: none of it may be a pasted line
const unremembered = async (p, why) => assert.deepEqual(await p.js(`[RegExp.input, RegExp.leftContext, RegExp.rightContext]`), ['', '', ''], why);
const act = (p, name) => p.acts.find((a) => a.act === name);
const clean = (p, r) => { assert.deepEqual(p.errors, [], 'no exception'); assert.deepEqual(r.csp, [], 'no policy violation'); assert.equal(r.overflowX, 0, 'no sideways scroll'); };
// the nine rows as the page must build them: step -> troubleshooting entries, by position
const NOT_WORKING = { 'install-4': [2], 'install-5': [6, 2], 'linux-1': [1], 'linux-2': [3, 4, 1], 'desktop-1': [1], 'desktop-2': [3, 4, 1], 'cloud-5': [5], 'cloud-6': [5], 'import-4': [4] };
const BEFORE = ['linux-1', 'desktop-1'];                   // these two also link to the guide's "Before you start", last

const SCENARIOS = {
  async first() {
    const out = {};
    for (const w of [360, 1280]) {
      const p = await open(PAGE, w, 800), r = await p.js(PROBE);
      clean(p, r);
      assert.equal(r.html, 'js'); assert(r.ready); assert.equal(r.url, '');
      assert.equal(r.path, 'no route yet'); assert.equal(r.count, 'not started'); assert.equal(r.next, 'choose where your server runs -> #which-path-is-yours');
      assert.deepEqual(r.acts.map((a) => a.open), [true, false, false, false, false], 'only act 1 is open');
      assert(r.acts.every((a) => !a.off && a.headingVisible && a.kicker), 'every act shows its label and heading');
      assert(!r.osAsked && !r.dockerAsked);
      assert.equal(r.troubleVisible, 6, 'the six entries are always there, closed');
      assert.equal(await p.js(`[...document.querySelectorAll('[data-list="trouble"] details')].some((d) => d.open)`), false);
      assert.equal(r.stored, null, 'a first visit writes nothing');
      assert.equal(await p.js(`document.querySelector('input[name="where"]:checked')`), null, 'nothing is selected: a wrong default would send someone down the wrong path without a click');
      // the whole text of every card and of both pills, at every width (a rented panel must read its own hint before choosing)
      const text = await p.js(`[...document.querySelectorAll('.card__body, .pill-card__lede')].map((n) => n.getClientRects().length > 0 && getComputedStyle(n).display !== 'none')`);
      assert(text.every(Boolean) && text.length === 6, 'cards and pills show their text');
      assert((await p.js(`document.querySelector('[data-where="panel"] .card__body').innerText.replace(/\\s+/g, ' ')`)).includes('many rented game panels are like this'));
      assert.equal(await p.js(`document.getAnimations().length`), 0, 'nothing animates at rest');
      out[w] = { page: r.page };
      await p.close();
    }
    return out;
  },
  async server() {
    const p = await open(PAGE + '?where=server', 1280, 900), r = await p.js(PROBE);
    clean(p, r);
    assert.equal(r.url, '?where=server'); assert.equal(r.path, '[blue pill] pull Path A'); assert.equal(r.count, '0 of 7 steps done');
    assert.equal(r.next, 'go to your next step -> #step-1-install-the-mod-on-the-server', 'a fresh act is entered by its heading');
    assert.deepEqual(r.acts.map((a) => a.open), [false, true, false, false, false]);
    assert.equal(act(r, 'choose').state, 'done');
    assert.deepEqual([act(r, 'pathb').off, act(r, 'pathb').state, act(r, 'pathb').headingVisible, act(r, 'pathb').kicker], [true, 'not on your route', true, false], 'path B is folded and says so, never hidden');
    assert.deepEqual(r.variants, [{ docker: 'engine', open: true, off: false, state: '', headingVisible: false }, { docker: 'desktop', open: false, off: true, state: 'not on your route', headingVisible: false }]);
    assert.equal(r.tunnelOpen, true); assert(!r.osAsked && !r.dockerAsked); assert.equal(r.current, 'install-1');
    assert.equal(r.stored, '{"sig":"5.3.3.6.4","route":"where=server","done":[]}', 'a link that names the hosting situation is remembered');
    // through Step 1
    await p.js(tick(INSTALL.slice(0, 4)));
    const r4 = await p.js(PROBE);
    assert.equal(r4.status, '> Your server has a pulse. Pulse confirmed it is up. 4 of 7 steps done.');
    assert.equal(r4.next, 'go to your next step -> #install-5', 'inside an act the link goes to the step');
    assert.equal(await p.js(`document.querySelector('#install-4 .step__tag').textContent`), 'done', 'the word is real text in the step');
    await p.js(tick(['install-5']));
    const r5 = await p.js(PROBE);
    assert.equal(r5.next, 'go to your next step -> #path-a-run-prometheus-and-grafana-yourself');
    assert.equal(r5.focus, 'body', 'ticking never moves the focus');
    await p.js(click('[data-act="install"] .act__next button'));
    const r6 = await p.js(PROBE);
    assert.deepEqual(r6.acts.map((a) => a.open), [false, false, true, false, false]);
    assert.equal(r6.url, '?where=server#path-a-run-prometheus-and-grafana-yourself');
    assert.match(r6.focus, /^button "Path A: run Prometheus and Grafana yourself/);
    assert.equal(r6.variants[0].headingVisible, true); assert.equal(r6.variants[1].headingVisible, true, 'the other Docker variant is a visible, folded heading');
    await p.js(tick(['linux-1']));
    assert.equal((await p.js(PROBE)).next, 'go to your next step -> #linux-2');
    await p.js(tick(['linux-2']));
    const r7 = await p.js(PROBE);
    assert.equal(r7.status, '> There is no spoon. Only metrics. All 7 steps done: your first dashboard is up.');
    assert.equal(r7.count, '7 of 7 steps done'); assert.equal(r7.next, 'what next -> #what-next'); assert.equal(r7.current, null);
    assert.deepEqual(JSON.parse(r7.stored), { sig: '5.3.3.6.4', route: 'where=server', done: [...INSTALL, 'linux-1', 'linux-2'] });
    assert.equal(await p.js(`document.querySelector('#linux-3 .step__tag').textContent`), 'for later, not counted', 'the teardown step is shown, tagged, never counted');
    assert.equal(await p.js(`document.body.innerText.includes('for later, not counted')`), true, 'and the tag is text the browser finds');
    assert.deepEqual(p.errors, []);
    await p.close();
    return { status: r7.status };
  },
  async home() {
    const p = await open(PAGE + '?where=home', 360, 800), r = await p.js(PROBE);
    clean(p, r);
    assert(r.osAsked); assert(!r.dockerAsked); assert.equal(r.count, 'not started', 'no count until the hosting question is answered');
    assert.equal(r.next, 'choose where your server runs -> #which-path-is-yours'); assert.equal(act(r, 'choose').open, true);
    await p.js(click('input[name="os"][value="linux"]'));
    const r2 = await p.js(PROBE);
    assert.equal(r2.url, '?where=home&os=linux'); assert(r2.dockerAsked); assert.equal(r2.count, '0 of 7 steps done'); assert.equal(r2.tunnelOpen, false);
    assert.equal(r2.status, 'Your route: Step 1: install the mod on the server, then Path A: run Prometheus and Grafana yourself. 7 steps.');
    await p.js(click('input[name="docker"][value="desktop"]'));
    const r3 = await p.js(PROBE);
    assert.equal(r3.url, '?where=home&os=linux&docker=desktop');
    assert.deepEqual(r3.variants.map((v) => [v.docker, v.open, v.off]), [['engine', false, true], ['desktop', true, false]]);
    await p.js(click('input[name="os"][value="windows"]'));
    const r4 = await p.js(PROBE);
    assert.equal(r4.url, '?where=home&os=windows'); assert(!r4.dockerAsked);
    await p.js(click('input[name="pill"][value="red"]'));
    const r5 = await p.js(PROBE);
    assert.equal(r5.url, '?where=home&os=windows&pill=red'); assert.equal(r5.count, '0 of 15 steps done'); assert.equal(act(r5, 'patha').off, true);
    await p.js(click('input[name="where"][value="container"]'));
    assert.equal((await p.js(PROBE)).url, '?where=container');
    assert.deepEqual(p.errors, []);
    await p.close();
    return { labels: r2.status };
  },
  async osQuestion() {
    // 390 px wide: the question "What does that PC run?" is on screen right after "A home PC", not below the fold
    const p = await open(PAGE, 390, 844);
    await p.js(click('input[name="where"][value="home"]'));
    await sleep(100);
    const r = await p.js(`(() => { const f = document.querySelector('input[name="os"]').closest('fieldset'), b = f.getBoundingClientRect(); return { hidden: f.hidden, top: Math.round(b.top), bottom: Math.round(b.bottom), h: innerHeight, focus: document.activeElement === document.body || document.activeElement.name === 'where' }; })()`);
    assert.equal(r.hidden, false);
    assert(r.top >= 0 && r.bottom <= r.h + 1, `the fieldset is in view (a pixel of rounding at the edge): ${JSON.stringify(r)}`);
    assert(r.focus, 'the focus did not move');
    await p.close();
    return r;
  },
  async container() {
    const p = await open(PAGE + '?where=container&pill=blue&os=linux&docker=desktop&junk=1&token=abc', 1280, 900), r = await p.js(PROBE);
    clean(p, r);
    assert.equal(r.url, '?junk=1&token=abc&where=container', 'what the guide rules out is dropped, foreign parameters stay');
    assert.equal(r.count, '0 of 15 steps done', 'a container may have a shell: step 5 counts'); assert.equal(r.path, '[red pill] push Path B'); assert.equal(act(r, 'patha').off, true);
    const blue = await p.js(`(() => { const b = document.querySelector('input[name="pill"][value="blue"]'); b.click(); return { disabled: b.getAttribute('aria-disabled'), checked: b.checked, red: document.querySelector('input[name="pill"][value="red"]').checked, why: document.querySelector('.pill-card__why').textContent, status: document.querySelector('.vitals__status').textContent, tag: document.querySelector('#install-5 .step__tag').textContent, name: b.getAttribute('aria-labelledby') }; })()`);
    assert.deepEqual([blue.disabled, blue.checked, blue.red, blue.tag, blue.name], ['true', false, true, '', 'pill-blue-t']);
    assert.equal(blue.why, 'Not on your route. Path A cannot reach it: 127.0.0.1 inside that container is the container, not the machine underneath it. Use path B instead.');
    assert.match(blue.status, /^Path A is not available for this hosting situation: /);
    const panel = await open(PAGE + '?where=panel', 1280, 900);
    assert.equal(await panel.js(`document.querySelector('.pill-card__why').textContent`), 'Not on your route. Skip straight to path B, Grafana Cloud: it needs nothing installed besides the mod itself.');
    assert.deepEqual(await panel.js(`[document.querySelector('#install-5 .step__tag').textContent, document.querySelector('.vitals__count').textContent]`), ['optional on your route', '0 of 14 steps done'], 'only a panel-only host has no shell');
    await panel.close();
    await p.close();
    return { why: blue.why };
  },
  async deeplinks() {
    const p = await open(PAGE + '?where=server#cloud-4', 1280, 900), r = await p.js(PROBE);
    clean(p, r);
    assert.equal(r.url, '?where=server#cloud-4', 'the state is not changed by a link into the other path');
    assert.deepEqual([act(r, 'pathb').open, act(r, 'pathb').off], [true, true]);
    assert.equal(await p.js(`(() => { const r = document.getElementById('cloud-4').getBoundingClientRect(); return r.top >= 0 && r.top < innerHeight; })()`), true, 'the target is on screen');
    await p.close();
    const p2 = await open(PAGE + '?where=home&os=windows#trouble-3', 360, 800), r2 = await p2.js(PROBE);
    clean(p2, r2);
    assert.equal(await p2.js(`document.querySelector('#trouble-3 details').open`), true);
    assert.equal(r2.troubleVisible, 6, 'the six entries are always there');
    // a "Not working?" link, then the way back
    await p2.js(click('[data-path="a"] .fold__toggle'));
    await p2.js(click('#desktop-2 .step__help a'));
    await sleep(100);
    const back = await p2.js(`({ url: location.hash, open: document.querySelector('#trouble-3 details').open, back: document.querySelector('#trouble-3 .trouble__back a').textContent + ' -> ' + document.querySelector('#trouble-3 .trouble__back a').getAttribute('href') })`);
    assert.deepEqual(back, { url: '#trouble-3', open: true, back: 'Back to Path A with Docker Desktop, step 2 -> #desktop-2' });
    // the same link twice, with the entry closed in between, opens it again
    await p2.js(`document.querySelector('#trouble-3 details').open = false`);
    await p2.js(click('#desktop-2 .step__help a'));
    assert.equal(await p2.js(`document.querySelector('#trouble-3 details').open`), true, 'a link to the place the address already names still opens what was closed');
    await p2.close();
    // the link back to the guide's own "Before you start" lands on its heading
    const p5 = await open(PAGE + '?where=server#path-a-run-prometheus-and-grafana-yourself', 1280, 900);
    await p5.js(click('#linux-1 .step__help a[href="#before-you-start"]'));
    await sleep(150);
    assert.deepEqual(await p5.js(`(() => { const top = document.getElementById('before-you-start').getBoundingClientRect().top; return [location.hash, top >= 0 && top < innerHeight]; })()`), ['#before-you-start', true], 'the heading is on screen');
    await p5.close();
    // the contents column links to headings inside folds: the click opens the fold and leaves the state alone
    const p4 = await open(PAGE + '?where=server', 1280, 900);
    assert.equal(await p4.js(`document.querySelector('[data-path="b"]').classList.contains('is-open')`), false);
    await p4.js(click('.toc a[href="#path-b-grafana-cloud-if-you-would-rather-host-nothing"]'));
    await sleep(150);
    const toc = await p4.js(`({ open: document.querySelector('[data-path="b"]').classList.contains('is-open'), url: location.search + location.hash, top: Math.round(document.getElementById('path-b-grafana-cloud-if-you-would-rather-host-nothing').getBoundingClientRect().top), focus: document.activeElement.tagName })`);
    assert.deepEqual([toc.open, toc.url], [true, '?where=server#path-b-grafana-cloud-if-you-would-rather-host-nothing'], 'a link into a folded act opens it, and no choice changes');
    assert.notEqual(toc.focus, 'BUTTON', 'a link never moves the focus into the fold');
    assert(toc.top >= 0 && toc.top < 400, 'and the heading is on screen');
    await p4.close();
    const p3 = await open(PAGE + '?where=panel#on-a-linux-server-or-pc', 1280, 900), r3 = await p3.js(PROBE);
    clean(p3, r3);
    assert.deepEqual([act(r3, 'patha').open, r3.variants[0].open, r3.variants[0].headingVisible], [true, true, true], 'a heading link into a folded branch opens it');
    await p3.close();
    return { ok: true };
  },
  async notworking() {
    // the nine rows as built: each step offers the bold symptom of its entries, in order, and every entry is reachable
    const p = await open(PAGE + '?view=all', 1280, 900);
    const rows = await p.js(`Object.fromEntries([...document.querySelectorAll('.step__help')].map((h) => [h.closest('.step').id, [...h.querySelectorAll('a')].map((a) => a.getAttribute('href').slice(1) + ' ' + a.textContent)]))`);
    const symptom = await p.js(`Object.fromEntries([...document.querySelectorAll('[data-list="trouble"] > li')].map((li) => [li.id, li.querySelector('summary').textContent.trim()]))`);
    assert.deepEqual(Object.keys(rows), Object.keys(NOT_WORKING), 'only the nine steps offer help');
    for (const [step, entries] of Object.entries(NOT_WORKING)) assert.deepEqual(rows[step], [...entries.map((n) => `trouble-${n} ${symptom['trouble-' + n]}`), ...(BEFORE.includes(step) ? ['before-you-start Before you start'] : [])], step);
    assert.equal(await p.js(`document.querySelector('#linux-1 .step__help').textContent.endsWith(' Or: Before you start')`), true, 'worded "Or: Before you start"');
    assert.deepEqual([...new Set(Object.values(NOT_WORKING).flat())].sort(), [1, 2, 3, 4, 5, 6], 'every entry is reachable from at least one step');
    assert.equal(Object.keys(symptom).length, 6);
    await p.close();
    return rows;
  },
  async whole() {
    const out = {};
    for (const w of [1280, 360]) {
      const p = await open(PAGE + '?where=server&view=all', w, 900), r = await p.js(PROBE);
      clean(p, r);
      assert(r.acts.every((a) => a.open) && r.variants.every((v) => v.open));
      assert.equal(r.stepsVisible, 21); assert.equal(r.troubleVisible, 6); assert.equal(r.headingsVisible, '10 of 10');
      assert.equal(await p.js(`[...document.querySelectorAll('.act details, .trouble details')].every((d) => d.open)`), true);
      assert.equal(await p.js(`document.querySelector('.toc-inline').open`), false, 'the contents list is not part of the guide');
      assert.equal(await p.js(`[...document.querySelectorAll('.act__next')].every((n) => n.getClientRects().length === 0)`), true, 'no Continue button in the whole-guide view');
      await p.js(click('[data-view]'));
      const r2 = await p.js(PROBE);
      assert.equal(r2.url, '?where=server'); assert.deepEqual(r2.acts.map((a) => a.open), [false, true, false, false, false]);
      assert.equal(await p.js(`[...document.querySelectorAll('[data-list="trouble"] details')].some((d) => d.open)`), false, 'pressing it again restores the default folding');
      out[w] = r.page;
      await p.close();
    }
    const only = await open(PAGE + '?view=all', 1280, 900);
    assert.equal(await only.js(`document.querySelector('[data-reset]').hidden`), true, 'the whole-guide view alone leaves nothing to start over');
    assert.equal(await only.js(`document.querySelector('[data-view]').getAttribute('aria-pressed')`), 'true');
    await only.close();
    return out;
  },
  async resume() {
    const p = await open(PAGE + '?where=server', 1280, 900);
    await p.js(tick(INSTALL.slice(0, 2)));
    const ctx = p.browserContextId;
    const p2 = await open(PAGE, 1280, 900, { context: ctx }), r2 = await p2.js(PROBE);
    assert.equal(r2.url, '?where=server', 'a bare address resumes the stored route and shows it'); assert.equal(r2.count, '2 of 7 steps done'); assert.equal(r2.current, 'install-3');
    // a partial link (the home page's pill) is laid over the stored route and writes nothing by itself
    const before = r2.stored;
    const p3 = await open(PAGE + '?pill=red', 1280, 900, { context: ctx }), r3 = await p3.js(PROBE);
    assert.equal(r3.url, '?where=server&pill=red', 'the stored hosting situation is kept, the pill is the link\'s');
    assert.equal(r3.count, '2 of 15 steps done'); assert.equal(r3.path, '[red pill] push Path B');
    assert.equal(r3.stored, before, 'a partial link never overwrites the stored route');
    await p3.js(tick(['install-3']));
    assert.equal(JSON.parse((await p3.js(PROBE)).stored).route, 'where=server&pill=red', 'the first click after it is remembered');
    const p3b = await open(PAGE + '?pill=blue', 1280, 900, { context: ctx });
    assert.equal((await p3b.js(PROBE)).url, '?where=server', 'a blue pill is the default of the stored route');
    // a link that names the hosting situation wins alone, and is remembered
    const p4 = await open(PAGE + '?where=panel', 1280, 900, { context: ctx }), r4 = await p4.js(PROBE);
    assert.equal(r4.url, '?where=panel'); assert.equal(JSON.parse(r4.stored).route, 'where=panel'); assert.deepEqual(JSON.parse(r4.stored).done, ['install-1', 'install-2', 'install-3'], 'the ticks survive a change of route');
    // the whole-guide view is a way of reading, not a route: it is never stored
    await p4.js(click('[data-view]'));
    assert.equal(JSON.parse((await p4.js(PROBE)).stored).route, 'where=panel', 'the view is in the address, not in the record');
    await p4.js(click('[data-view]'));
    // start over clears everything, the record included
    await p4.js(click('[data-reset]'));
    const r5 = await p4.js(PROBE);
    assert.equal(r5.url, ''); assert.equal(r5.count, 'not started'); assert.equal(r5.stored, null, 'start over leaves no record behind'); assert.equal(r5.status, 'Started over: choices and checkmarks cleared.');
    assert.equal(await p4.js(`document.querySelector('[data-reset]').hidden`), true, 'and is shown only when there is something to clear');
    // a record written for another shape of the guide is dropped
    await p4.js(`localStorage.setItem('pulse.gs', JSON.stringify({ sig: '5.3.3.7.4', route: 'where=home', done: ['install-1'] }))`);
    const p5 = await open(PAGE, 1280, 900, { context: ctx }), r6 = await p5.js(PROBE);
    assert.equal(r6.url, ''); assert.equal(r6.count, 'not started');
    assert.equal(r6.stored, JSON.stringify({ sig: '5.3.3.7.4', route: 'where=home', done: ['install-1'] }), 'it is ignored, not rewritten, until the reader clicks');
    // storage that throws: the wizard works and forgets on reload
    const p6 = await open(PAGE + '?where=server', 1280, 900, { inject: `Object.defineProperty(window, 'localStorage', { get() { throw new Error('denied'); } });` });
    await p6.js(tick(['install-1']));
    assert.equal((await p6.js(`document.querySelector('.vitals__count').textContent`)), '1 of 7 steps done'); assert.deepEqual(p6.errors, []);
    for (const q of [p6, p5, p4, p3b, p3, p2]) await q.close();
    await p.close();                       // p owns the browser context
    return { ok: true };
  },
  async helper() {
    const out = {};
    for (const w of [360, 1280]) {
      const p = await open(PAGE + '?where=panel#cloud-4', w, 900);
      await p.js(`navigator.clipboard.writeText = (t) => { window.__copied = t; return Promise.resolve(); }`);
      const before = await p.js(`({ block: document.querySelector('[data-helper] code').textContent.includes('<region>'), tools: document.querySelector('.helper__tools').hidden, pointer: document.querySelector('#cloud-1 .helper__pointer')?.textContent, spell: document.getElementById('gs-paste').spellcheck, auto: document.getElementById('gs-paste').autocomplete, form: !!document.getElementById('gs-paste').closest('form'), name: document.getElementById('gs-paste').getAttribute('name') })`);
      assert.deepEqual(before, { block: true, tools: true, pointer: '[i] The helper at step 4 does this for you.', spell: false, auto: 'off', form: false, name: null });
      await p.js(paste(BLOCK));
      await unremembered(p, 'an accepted paste leaves nothing of itself in the regular expression engine, which would hold the line while the value is on screen');
      const r = await p.js(`({ shown: document.querySelector('[data-helper] code').textContent, field: document.getElementById('gs-paste').value,
        read: [...document.querySelectorAll('.helper__read li')].map((li) => li.textContent), notes: [...document.querySelectorAll('.helper__notes li')].map((li) => li.textContent), status: document.querySelector('.vitals__status').textContent,
        inUrl: location.href.includes('MTIz'), inStorage: JSON.stringify(Object.entries(localStorage)).includes('MTIz') || JSON.stringify(Object.entries(sessionStorage)).includes('MTIz'), inDom: document.documentElement.outerHTML.includes('MTIz'),
        inFields: [...document.querySelectorAll('input, textarea')].some((i) => i.value.includes('MTIz')), inHistory: JSON.stringify(history.state || '').includes('MTIz'), inTitle: document.title.includes('MTIz'), inCookie: document.cookie.includes('MTIz'), csp: window.__csp })`);
      await p.js(click('[data-helper] .copy')); await sleep(50);
      assert.equal(r.shown, '{\n  "Endpoint": "https://otlp-gateway-prod-eu-west-2.grafana.net/otlp",\n  "Protocol": "http/protobuf",\n  "Headers": {\n    "Authorization": "Basic ************"\n  }\n}');
      assert.deepEqual(JSON.parse(await p.js('window.__copied')), { Endpoint: 'https://otlp-gateway-prod-eu-west-2.grafana.net/otlp', Protocol: 'http/protobuf', Headers: { Authorization: 'Basic MTIzNDU2OmdsY19leGFtcGxldG9rZW4=' } }, 'copy gives the real value');
      assert.deepEqual([r.field, r.inUrl, r.inStorage, r.inDom, r.inFields, r.inHistory, r.inTitle, r.inCookie], ['', false, false, false, false, false, false, false], 'the credential is in memory only');
      assert.deepEqual(r.read, ['[x] Endpoint read: otlp-gateway-prod-eu-west-2.grafana.net', '[x] Authorization value read (hidden)']); assert.deepEqual(r.notes, []); assert.deepEqual(r.csp, []);
      assert.equal(r.status, 'Both values read. The block under step 4 now holds them.');
      assert.equal(await p.js(`document.querySelector('[data-helper] .copy').textContent`), 'copied');
      assert.equal(await p.js(`document.getElementById('gs-paste').getAttribute('aria-invalid')`), 'false');
      await p.js(click('[data-show]'));
      assert.equal(await p.js(`document.querySelector('[data-helper] code').textContent.includes('"Basic MTIzNDU2OmdsY19leGFtcGxldG9rZW4="')`), true, 'show reveals it');
      assert.equal(await p.js(`document.querySelector('.vitals__status').textContent.includes('MTIz')`), false, 'never the live region');
      await p.js(`dispatchEvent(new Event('pagehide'))`);
      assert.equal(await p.js(`document.documentElement.outerHTML.includes('MTIz') || document.querySelector('[data-helper] code').textContent.includes('prod-eu-west')`), false, 'leaving the page forgets both values');
      await unremembered(p, 'and the regular expression engine no longer holds the last pasted line, which a page kept for Back would otherwise carry');
      // one value at a time, then a bad paste, then typed by hand
      await p.js(paste('https://otlp-gateway-prod-us-east-0.grafana.net/otlp/v1/metrics'));
      const one = await p.js(`({ read: [...document.querySelectorAll('.helper__read li')].map((li) => li.textContent), notes: [...document.querySelectorAll('.helper__notes li')].map((li) => li.textContent), auth: document.querySelectorAll('[data-helper] code .ph').length, invalid: document.getElementById('gs-paste').getAttribute('aria-invalid'), showHidden: document.querySelector('[data-show]').hidden })`);
      assert.deepEqual(one, { read: ['[x] Endpoint read: otlp-gateway-prod-us-east-0.grafana.net'], notes: ['[!] Endpoint is the base address only, Pulse adds the rest of the path itself.'], auth: 1, invalid: 'true', showHidden: true });
      await p.js(paste('hello'));
      assert.deepEqual(await p.js(`[...document.querySelectorAll('.helper__notes li')].map((li) => li.textContent.slice(0, 40))`), ['[!] Endpoint is the base address only, P', '[!] Neither value was found in that past'], 'a note stays with its value; a useless paste only reports');
      await p.js(`document.getElementById('gs-paste').focus()`); await p.type('Authorization=Basic%20dHlwZWQ6dG9rZW4=');
      assert.equal(await p.js(`document.getElementById('gs-paste').value`), 'Authorization=Basic%20dHlwZWQ6dG9rZW4=');
      await p.key('Tab');
      await p.js(click('[data-helper] .copy')); await sleep(50);
      const typed = await p.js(`({ field: document.getElementById('gs-paste').value, copied: JSON.parse(window.__copied).Headers.Authorization, inDom: document.documentElement.outerHTML.includes('dHlwZWQ6') })`);
      assert.deepEqual(typed, { field: '', copied: 'Basic dHlwZWQ6dG9rZW4=', inDom: false }, 'typed text is taken when the field loses focus');
      // a value with a comma is refused, and the value already read stays
      await p.js(paste('Authorization=Basic%20abc,X-Scope-OrgID=42'));
      await unremembered(p, 'a refused paste leaves nothing of itself in the regular expression engine, though no value was taken from it');
      await p.js(click('[data-helper] .copy')); await sleep(50);
      const comma = await p.js(`({ notes: [...document.querySelectorAll('.helper__notes li')].map((li) => li.textContent), copied: JSON.parse(window.__copied).Headers.Authorization, inDom: document.documentElement.outerHTML.includes('X-Scope-OrgID') })`);
      assert.deepEqual(comma, { notes: ['[!] Endpoint is the base address only, Pulse adds the rest of the path itself.', '[!] The value holds a comma and was not used: Pulse refuses to start exporting with a comma in a header value. Paste the Authorization header alone, up to the comma.'],
        copied: 'Basic dHlwZWQ6dG9rZW4=', inDom: false }, 'a value Pulse would refuse is not handed over');
      await p.js(click('[data-forget]'));
      assert.equal(await p.js(`document.querySelector('[data-helper] code').textContent.includes('<region>') && document.querySelector('.helper__tools').hidden`), true);
      await unremembered(p, 'forget both values leaves nothing of the pasted text in the regular expression engine');
      // nothing was logged, and nothing left the page
      assert.deepEqual(p.console.filter((l) => /MTIz|dHlwZWQ|prod-eu-west|prod-us-east|Basic/.test(l)), [], 'never the console');
      const own = PAGE.startsWith('http') ? new URL(PAGE).origin : 'file:';           // the page's own files are no leak; a request anywhere else, or one that carries a value, is
      assert.deepEqual(p.requests.filter((u) => /MTIz|dHlwZWQ|prod-eu-west|prod-us-east/.test(u) || !u.startsWith(own)), [], 'never a request');
      assert.deepEqual(p.errors, []);
      out[w] = { overflowX: (await p.js(PROBE)).overflowX };
      assert.equal(out[w].overflowX, 0);
      await p.close();
    }
    return out;
  },
  async nojs() {
    const out = {};
    for (const w of [360, 1280]) {
      const p = await open(PAGE + '?where=server', w, 800, { noScript: true });
      const r = await p.js(`(() => { const vis = (n) => n.getClientRects().length > 0; return { html: document.documentElement.className, controls: document.querySelectorAll('input, button, select, textarea').length, steps: [...document.querySelectorAll('ol[data-list] > li')].filter(vis).length,
        trouble: [...document.querySelectorAll('[data-list="trouble"] > li')].filter(vis).length, headings: [...document.querySelectorAll('[data-gs] h1, [data-gs] h2, [data-gs] h3')].filter(vis).length, downloads: [...document.querySelectorAll('.dl a')].map((a) => a.textContent), figures: document.querySelectorAll('.shot img').length,
        callouts: document.querySelectorAll('.callout--security').length, where: [...document.querySelectorAll('.code__where')].map((n) => n.textContent), page: document.documentElement.scrollHeight, overflowX: document.documentElement.scrollWidth - innerWidth,
        deadLinks: [...document.querySelectorAll('a[href="#"], a:not([href])')].length, hiddenTemplates: [...document.querySelectorAll('template')].filter(vis).length, animations: document.getAnimations().length }; })()`);
      assert.deepEqual([r.html, r.controls, r.steps, r.trouble, r.headings, r.figures, r.callouts, r.overflowX, r.deadLinks, r.hiddenTemplates, r.animations], ['', 0, 21, 6, 10, 3, 2, 0, 0, 0, 0], 'without script: the whole guide, no dead control');
      assert.equal(r.downloads.length, 3);
      assert.match(r.downloads[0], /^download pulse_\d+\.\d+\.\d+\.zip$/); assert.match(r.downloads[1], /^download pulseotlp_\d+\.\d+\.\d+\.zip$/); assert.equal(r.downloads[2], 'download pulse-overview-shared.json');
      const compose = "run on the game server's machine, in contrib/grafana";
      assert.deepEqual(r.where, ['run on the game server', "run on the game server's machine", compose, 'run on your own computer', compose, compose, compose]);
      out[w] = r.page;
      await p.close();
    }
    return out;
  },
  async failOpen() {
    // 1. the script file never arrives
    const p = await open(PAGE + '?where=server', 1280, 900, { block: ['*site.js*'] });
    const r = await p.js(`({ html: document.documentElement.className, steps: [...document.querySelectorAll('ol[data-list] > li')].filter((n) => n.getClientRects().length > 0).length, controls: document.querySelectorAll('input, button').length })`);
    assert.deepEqual(r, { html: '', steps: 21, controls: 0 }, 'a script that does not load leaves the plain guide');
    await p.close();
    // 2. the wizard throws half way through its start-up
    const p2 = await open(PAGE + '?where=server', 1280, 900, { inject: `document.addEventListener('readystatechange', () => { if (document.readyState === 'interactive') document.querySelector('[data-tunnel]').removeAttribute('data-tunnel'); });` });
    const r2 = await p2.js(`({ html: document.documentElement.className, steps: [...document.querySelectorAll('ol[data-list] > li')].filter((n) => n.getClientRects().length > 0).length, trouble: [...document.querySelectorAll('[data-list="trouble"] > li')].filter((n) => n.getClientRects().length > 0).length, hidden: document.querySelectorAll('[data-gs] [hidden]:not(template)').length, copy: document.querySelectorAll('.copy').length })`);
    assert.equal(p2.errors.length, 1); assert.match(p2.errors[0], /TypeError/);
    assert.deepEqual([r2.html, r2.steps, r2.trouble, r2.hidden], ['', 21, 6, 0], 'a wizard that throws puts the whole guide back');
    assert(r2.copy > 0, 'and the other modules still ran');
    await p2.close();
    return { thrown: p2.errors[0] };
  },
  async keyboard() {
    const p = await open(PAGE, 1280, 900), log = [];
    const step = async (k) => { await p.key(k); log.push(k + ' -> ' + (await p.js(PROBE)).focus); };
    await p.js(`document.querySelector('[data-next]').focus()`);
    await step('Tab'); await step('Tab'); await step('Tab'); await step(' '); await step('ArrowDown'); await step('Tab'); await step('ArrowRight'); await step('Tab'); await step('Tab');
    const r = await p.js(PROBE);
    assert.equal(r.url, '?where=home&os=macos');
    await p.key('Enter');
    const r2 = await p.js(PROBE);
    assert.equal(r2.url, '?where=home&os=macos#step-1-install-the-mod-on-the-server'); assert.match(r2.focus, /^button "Step 1: install the mod on the server/);
    assert.deepEqual(p.errors, []);
    await p.close();
    // from the top: the skip link first, and past the sidebar the next stop is inside the article
    const q = await open(PAGE, 1280, 900);
    await q.key('Tab');
    assert.match((await q.js(PROBE)).focus, /^a "Skip to content"/);
    await q.key('Enter'); await q.key('Tab');
    assert.equal(await q.js(`document.activeElement.closest('article') !== null`), true, 'the next stop after the skip link is in the article, not the sidebar');
    await q.close();
    return log;
  },
  async motion() {
    // at rest nothing animates, for every visitor; a tick draws one beat, once, and it is gone within 600 ms
    const p = await open(PAGE + '?where=server', 1280, 900);
    assert.equal(await p.js(`document.getAnimations().length`), 0, 'at rest');
    await p.js(tick(['install-1']));
    assert.equal(await p.js(`document.querySelectorAll('.vitals__beat').length`), 1);
    const running = await p.js(`document.getAnimations().map((a) => ({ name: a.animationName, ms: a.effect.getTiming().duration }))`);
    assert.deepEqual(running, [{ name: 'beat', ms: 600 }], 'one animation, 600 ms');
    await sleep(750);
    assert.equal(await p.js(`document.getAnimations().length + document.querySelectorAll('.vitals__beat').length`), 0, 'and nothing is left running');
    await p.js(tick(['install-1']));
    assert.equal(await p.js(`document.querySelectorAll('.vitals__beat').length`), 0, 'unticking draws nothing');
    const transitions = await p.js(`[...document.querySelectorAll('.btn, .fold__toggle, .chip')].map((n) => getComputedStyle(n).transitionDuration).every((d) => d.split(',').every((x) => parseFloat(x) <= 0.15))`);
    assert.equal(transitions, true, 'hover and focus transitions are 150 ms at most');
    await p.close();
    const q = await open(PAGE + '?where=server', 1280, 900, { reducedMotion: true });
    await q.js(tick(['install-1', 'install-2']));
    const r = await q.js(`({ animations: document.getAnimations().length, beats: document.querySelectorAll('.vitals__beat').length, transition: getComputedStyle(document.querySelector('.btn')).transitionDuration })`);
    assert.deepEqual([r.animations, r.beats], [0, 0], 'under reduced motion there is no beat at all');
    await q.close();
    return r;
  },
  async figure() {
    const p = await open(PAGE + '?where=server#linux-2', 1280, 900);
    await p.js(`document.querySelector('#linux-2 .shot img').scrollIntoView()`); await sleep(400);
    const r = await p.js(`(() => { const i = document.querySelector('#linux-2 .shot img'); return { complete: i.complete, natural: i.naturalWidth + 'x' + i.naturalHeight, shown: Math.round(i.getBoundingClientRect().width), csp: window.__csp }; })()`);
    assert.deepEqual([r.complete, r.natural, r.csp.length], [true, '1880x629', 0], 'the picture of the dashboard loads under the policy, from a file URL');
    await p.close();
    return r;
  },
  async solid() {
    // text never sits on a background that is not opaque: the browser reads it, element by element
    const SNIPPET = `[...document.querySelectorAll('body *')].filter((el) => [...el.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim()) && el.getClientRects().length).filter((el) => {
      for (let n = el; n && n !== document.body; n = n.parentElement) {
        const c = getComputedStyle(n).backgroundColor.match(/[\\d.]+/g);
        if (c && (c.length === 3 || Number(c[3]) === 1)) return false;
        if (c && Number(c[3]) > 0) return true;
      }
      return true;
    }).map((el) => el.tagName + '.' + el.className)`;
    const out = {};
    for (const [name, url, w] of [['guided 1280', '?where=server', 1280], ['whole 360', '?where=panel&view=all', 360]]) {
      const p = await open(PAGE + url, w, 900);
      assert.deepEqual(await p.js(SNIPPET), [], 'every text node has an opaque background behind it');
      out[name] = 'clean';
      await p.close();
    }
    return out;
  },
  async print() {
    const p = await open(PAGE + '?where=server', 1280, 900);
    await p.S('Emulation.setEmulatedMedia', { media: 'print' });
    const r = await p.js(`(() => { const shown = (sel) => [...document.querySelectorAll(sel)].filter((n) => n.getClientRects().length > 0).length; const all = (sel) => document.querySelectorAll(sel).length;
      return { bodies: [shown('.fold__body'), all('.fold__body')], steps: [shown('.steps > .step'), all('.steps > .step')], hosts: shown('.card__title'), controls: shown('.vitals, .vitals-tools, .step__check, .act__next, .helper, .choice--ask'), kickers: shown('.kicker, .act__kicker'), color: getComputedStyle(document.querySelector('.step')).color }; })()`);
    assert.deepEqual([r.bodies[0] === r.bodies[1], r.steps[0] === r.steps[1], r.hosts, r.controls, r.kickers], [true, true, 4, 0, 0], 'on paper: every fold open, the four hosting bullets, no control');
    assert.equal(r.color, 'rgb(0, 0, 0)');
    await p.close();
    return r;
  },
  async sizes() {
    const out = {};
    for (const w of [360, 1280]) {
      const h = {};
      for (const [name, url, sel] of [['path A, Linux', '?where=server', '[data-path="a"]'], ['path A, Docker Desktop', '?where=home&os=windows', '[data-path="a"]'], ['path B', '?where=panel', '[data-path="b"]'], ['Step 1', '?where=server', '[data-act="install"]']]) {
        const p = await open(PAGE + url, w, 800);
        h[name] = await p.js(`(() => { const a = document.querySelector(${JSON.stringify(sel)}); if (!a.classList.contains('is-open')) a.querySelector('.fold__toggle').click(); return Math.round(a.getBoundingClientRect().height); })()`);
        assert.equal(await p.js(`document.documentElement.scrollWidth - innerWidth`), 0, `${name} at ${w}: no sideways scroll`);
        await p.close();
      }
      out[w] = h;
    }
    return out;
  },
  async sweep() {
    // 360 px, every state: no sideways scroll, no exception, no policy violation, every act opened in turn
    const states = ['', '?where=server', '?where=home', '?where=home&os=linux', '?where=home&os=linux&docker=desktop', '?where=home&os=windows', '?where=panel', '?where=container', '?where=server&pill=red', '?pill=red', '?pill=blue', '?where=server&view=all', '?where=panel&view=all'];
    for (const q of states) {
      const p = await open(PAGE + q, 360, 800);
      for (let i = 0; i < 5; i++) {
        await p.js(`(() => { const acts = [...document.querySelectorAll('.act')]; const a = acts[${i}]; if (!a.classList.contains('is-open')) a.querySelector('.fold__toggle').click(); [...document.querySelectorAll('[data-gs] details')].forEach((d) => { d.open = true; }); })()`);
        const r = await p.js(PROBE);
        clean(p, r);
      }
      await p.close();
    }
    return { states: states.length };
  },
};

const wanted = process.argv.slice(2);
const run = wanted.length ? wanted : Object.keys(SCENARIOS);
let failed = 0;
try {
  for (const name of run) {
    assert(SCENARIOS[name], `no scenario called ${name}`);
    try { console.log(`ok   ${name}: ${JSON.stringify(await SCENARIOS[name]())}`); } catch (e) { failed++; console.log(`FAIL ${name}: ${e.message.split('\n').slice(0, 14).join('\n     ')}`); }
  }
} finally {
  ws.close();
  const gone = new Promise((r) => { chrome.once('exit', r); setTimeout(r, 5000); });
  chrome.kill();
  await gone;                                                   // Chrome writes to its profile until it has gone: remove the folder after
  rmSync(TMP, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });   // Chrome's children may still be writing
}
console.log(failed ? `${failed} scenario(s) failed` : `all ${run.length} scenarios passed`);
process.exit(failed ? 1 : 0);
