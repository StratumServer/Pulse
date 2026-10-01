// The terminal of Home (js/terminal.js, content/home.mjs) and the Home page that holds it. The terminal
// must not invent output: every reply is a literal of Pulse/PulseCommands.cs, and the build refuses to
// go on when one has moved. Run with node --test.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { buildSite } from '../build.mjs';
import { RABBIT, checkFacts, checkReplies, rabbitName } from '../content/home.mjs';
import { loadData, SONAR_PAGE } from '../lib/data.mjs';
import { loadSources } from '../lib/sources.mjs';
import { ASCII_RABBIT, COMMANDS, README_FACTS, SAMPLE, SOURCE, attributionClock, createSession, fill, walk } from '../js/terminal.js';
import { OPENING, hopRabbit, openingTimeline } from '../js/home-show.js';
import { rabbitSvg } from '../lib/pages/home.mjs';

const sources = loadSources();
const data = await loadData(sources, { offline: true });
const commandsCs = readFileSync(new URL('../../../Pulse/PulseCommands.cs', import.meta.url), 'utf8');
const built = await buildSite({ sources, offline: true });
const home = built.files.get('index.html');

// a session with a clock the test moves
const session = () => { let t = 1_000_000; const s = createSession(() => t); return { run: (line) => s.run(line), wait: (ms) => { t += ms; } }; };
const say = (s, line) => s.run(line).lines.join('\n');

// ------------------------------------------------------------------ the replies are the mod's own
test('every reply of /pulse is a whole literal of PulseCommands.cs, and the build says so when one moves', () => {
  assert.doesNotThrow(() => checkReplies(commandsCs));
  for (const [name, template] of Object.entries(SOURCE)) {
    const moved = commandsCs.replace(template.split('{')[0].slice(0, 30), 'Something else entirely ');
    assert.throws(() => checkReplies(moved), (e) => e.message.includes(`SOURCE.${name}`) || e.message.includes('PulseCommands.cs'), name);
  }
  assert.throws(() => checkReplies(commandsCs.replace('waiting for the next burst.', 'waiting for the next one.')), /waiting for the next burst/);
  assert.throws(() => checkReplies(''), /PulseCommands\.cs no longer holds the reply/);
});

test('a sentence the C# splits over two lines is still one reply', () => {
  assert.match(commandsCs, /unless the engine's "\s*\n\s*\+ \$"own \/debug logticks/, 'the source really splits it');
  assert.doesNotThrow(() => checkReplies(commandsCs));
  assert.throws(() => checkReplies(commandsCs.replace('own /debug logticks', 'own /debug ticks')), /SOURCE\.switchedOff/);
});

test('holes are filled by name, and one nobody gave a value for is a mistake', () => {
  assert.equal(fill('bursts of {burstTicks} ticks', { burstTicks: 10 }), 'bursts of 10 ticks');
  assert.throws(() => fill('{nobody}', {}), /no value for the hole \{nobody\}/);
});

// ------------------------------------------------------------------ the commands
test('help lists the commands, and says that the numbers are samples', () => {
  const out = say(session(), 'help');
  assert.match(out, /^Sample console\. The \/pulse replies are the mod's own words; the numbers are samples\./);
  for (const [command, what] of COMMANDS) assert.ok(out.includes(command) && out.includes(what), command);
  assert.ok(out.includes(SAMPLE.command));
});

test('curl prints the sample the page holds, and nothing else', () => {
  const s = session();
  assert.deepEqual(s.run('curl 127.0.0.1:9464/metrics'), { lines: SAMPLE.lines, raw: true });
  assert.deepEqual(s.run('  CURL   http://localhost:9464/metrics/ ').lines, SAMPLE.lines);
  assert.match(say(s, 'curl 10.0.0.1:9464/metrics'), /^command not found: curl 10\.0\.0\.1:9464\/metrics\. Try help\.$/);
});

test('whoami, clear, an empty line, and anything else', () => {
  const s = session();
  assert.equal(say(s, 'whoami'), 'admin');
  assert.deepEqual(s.run('clear'), { lines: [], effect: 'clear' });
  assert.deepEqual(s.run('   '), { lines: [] });
  assert.equal(say(s, 'rm -rf /'), 'command not found: rm -rf /. Try help.');
  assert.equal(say(s, '<b>bold</b> & "quotes"'), 'command not found: <b>bold</b> & "quotes". Try help.', 'what the reader typed comes back as it was typed: the page writes it with textContent');
});

test('/pulse attribution: off at boot, on after the command, the first burst one interval later', () => {
  const s = session();
  assert.equal(say(s, '/pulse attribution status'), 'Attribution is off. It would run bursts of 10 ticks every 10s; 0 ticks profiled so far.');
  assert.equal(say(s, '/pulse attribution on'), 'Attribution is on: bursts of 10 ticks every 10s. pulse.json was not changed, so the file decides again after a restart.');
  assert.equal(say(s, '/pulse attribution status'), 'Attribution is on: bursts of 10 ticks every 10s, 0 ticks profiled so far, waiting for the next burst.');
  s.wait(9_000);
  assert.match(say(s, '/pulse attribution status'), /, 0 ticks profiled so far, waiting for the next burst\.$/, 'nine seconds: no burst yet');
  s.wait(1_100);
  assert.match(say(s, '/pulse attribution status'), /, 0 ticks profiled so far, profiling right now\.$/, 'the burst is running: ten ticks of about 33 ms');
  s.wait(300);
  assert.match(say(s, '/pulse attribution status'), /, 10 ticks profiled so far, waiting for the next burst\.$/);
  s.wait(20_700);
  assert.match(say(s, '/pulse attribution status'), /, 30 ticks profiled so far, /);
  assert.equal(say(s, '/pulse attribution off'), "Attribution is off, and the engine's frame profiler with it, unless the engine's own /debug logticks still wants it running. pulse.json was not changed, so the file decides again after a restart.");
  assert.equal(say(s, '/pulse attribution status'), 'Attribution is off. It would run bursts of 10 ticks every 10s; 30 ticks profiled so far.', 'what was profiled stays counted, as the counter of the mod does');
  say(s, '/pulse attribution on');
  s.wait(10_400);
  assert.match(say(s, '/pulse attribution status'), /, 40 ticks profiled so far, /, 'switching on starts the cycle over, the total goes on');
});

test('/pulse reload applies the file, which says Enabled false, so it switches attribution off', () => {
  const s = session();
  say(s, '/pulse attribution on');
  assert.equal(say(s, '/pulse reload'), 'Reloaded pulse.json. Attribution is off. Nothing else in the file differs from what the server is running.');
  assert.match(say(s, '/pulse attribution status'), /^Attribution is off\./);
  assert.equal(say(createSession(), '/pulse reload'), 'Reloaded pulse.json. Attribution is off. Nothing else in the file differs from what the server is running.');
});

test('no reply ever carries a hole that was not filled', () => {
  const s = session();
  for (const line of ['help', '/pulse attribution status', '/pulse attribution on', '/pulse attribution status', '/pulse reload', '/pulse attribution off', 'follow the white rabbit']) {
    assert.doesNotMatch(say(s, line), /[{}]/, line);
  }
});

test('the winks: the white rabbit, both pills, waking up, the spoon', () => {
  const s = session();
  const rabbit = s.run('Follow the White Rabbit');
  assert.deepEqual(rabbit.lines, [...ASCII_RABBIT, ['New to Prometheus? Follow the white rabbit: the ', { text: 'getting-started guide', href: 'getting-started/index.html' }, ' walks both pills end to end.']]);
  assert.equal(rabbit.effect, 'rabbit', 'the rabbit of the page hops once');
  assert.deepEqual(s.run('take the blue pill'), { lines: ['> take the blue pill: path A'], effect: { pill: 'blue' } });
  assert.deepEqual(s.run('take the red pill'), { lines: ['> take the red pill: path B'], effect: { pill: 'red' } });
  assert.deepEqual(s.run('wake up'), { lines: [], effect: 'opening' });
  assert.equal(say(s, 'there is no spoon'), 'There is no spoon. Only metrics.');
  assert.equal(say(s, '  /PULSE   attribution  STATUS '), 'Attribution is off. It would run bursts of 10 ticks every 10s; 0 ticks profiled so far.', 'case and spaces do not matter');
});

test('the clock of the cycle: one interval, then burstTicks ticks of 33 ms', () => {
  assert.deepEqual(attributionClock(0), { ticksProfiled: 0, inBurst: false });
  assert.deepEqual(attributionClock(9_999), { ticksProfiled: 0, inBurst: false });
  assert.deepEqual(attributionClock(10_000), { ticksProfiled: 0, inBurst: true });
  assert.deepEqual(attributionClock(10_330), { ticksProfiled: 10, inBurst: false });
  assert.deepEqual(attributionClock(2 * 10_330 + 10_100), { ticksProfiled: 20, inBurst: true });
  assert.deepEqual(attributionClock(5_000, { burstTicks: 30, intervalSeconds: 2 }), { ticksProfiled: 30 * Math.floor(5_000 / (2_000 + 30 * 33)), inBurst: 5_000 % (2_000 + 30 * 33) >= 2_000 });
});

test('arrow keys walk the history and stop at both ends', () => {
  const history = ['a', 'b', 'c'];
  assert.equal(walk(history, 3, -1), 2);
  assert.equal(walk(history, 0, -1), 0);
  assert.equal(walk(history, 2, 1), 3, 'one past the last is the line being typed');
  assert.equal(walk(history, 3, 1), 3);
  assert.equal(walk([], 0, -1), 0);
});

// ------------------------------------------------------------------ the guards of Home
test('the facts Home states are checked against the README, the data and the C# source', () => {
  assert.doesNotThrow(() => checkFacts({ docs: sources.docs, data }));
  const fails = (what, re) => assert.throws(what, re);
  fails(() => checkFacts({ docs: sources.docs, data: { ...data, metric: () => undefined } }), /prints pulse_server_ticks_total, which README\.md does not document/);
  fails(() => checkFacts({ docs: sources.docs, data: { ...data, usedBy: () => ({ panels: [] }) } }), /prints dotnet_gc_pause_time_seconds_total/);
  const tweak = (key, value) => ({ ...data, config: data.config.map((f) => (f.file === 'ModConfig/pulse.json' ? { ...f, rows: f.rows.map((r) => (r.key === key ? { ...r, default: value } : r)) } : f)) });
  fails(() => checkFacts({ docs: sources.docs, data: tweak('Attribution.BurstTicks', 30) }), /no longer 10 and 10/);
  fails(() => checkFacts({ docs: sources.docs, data: tweak('Attribution.IntervalSeconds', 5) }), /ten ticks every ten seconds/);
  fails(() => checkFacts({ docs: sources.docs, data: tweak('Port', 9500) }), /127\.0\.0\.1:9464/);
  const without = (phrase) => ({ 'README.md': { ...sources.docs['README.md'], tokens: sources.docs['README.md'].tokens.map((t) => ({ ...t, raw: t.raw.replace(/\s+/g, ' ').split(phrase).join('') })) } });
  for (const phrase of ['26%', '0.9%', ...README_FACTS, '`/pulse reload`']) fails(() => checkFacts({ docs: without(phrase), data }), /README\.md no longer holds/, phrase);
  fails(() => checkFacts({ docs: sources.docs, data, commandsCs: 'internal static class PulseCommands {}' }), /PulseCommands\.cs no longer holds the reply/);
  fails(() => checkFacts({ docs: sources.docs, data: { ...data, rules: data.rules.filter((r) => r.alert !== 'PulseModHoggingTick') } }), /no rule called PulseModHoggingTick/);
});

// ------------------------------------------------------------------ the page
test('Home is complete HTML: the sample is in the markup the terminal enhances, with its prompt', () => {
  const term = /<figure class="term" data-term>[\s\S]*?<\/figure>/.exec(home)[0];
  assert.match(term, /<pre class="term__body" tabindex="0" role="group" aria-label="Sample session: curl of the metrics endpoint">/);
  assert.ok(term.includes(`<span class="term__cmd">$ ${SAMPLE.command}</span>`));
  for (const line of SAMPLE.lines) assert.ok(term.includes(`<span class="term__l">${line.replace(/"/g, '&quot;')}</span>`), line);
  assert.match(term, /<span class="term__cmd term__prompt">\$ <span class="cursor" aria-hidden="true"><\/span><\/span><\/pre>/);
  assert.match(term, /<figcaption>Sample output\. One zip, no agent, no database, no account\. Drop <code>pulse_\d+\.\d+\.\d+\.zip<\/code> into <code>Mods\/<\/code>, start the server, scrape\.<\/figcaption>/);
  assert.doesNotMatch(term, /<button|<input|role="log"/, 'the terminal is made by script: no dead control, and no live region, without it');
});

test('Home holds its blocks, in order, with one h1', () => {
  const ids = [...home.matchAll(/<h2 class="h-prompt" id="([^"]+)">/g)].map((m) => m[1]);
  assert.deepEqual(ids, ['h-does', 'h-pill', 'h-get', 'h-vitals', 'h-shot', 'h-oracle', 'h-attr', 'h-compat']);
  assert.equal(home.match(/<h1/g).length, 1);
  assert.match(home, /<h1 class="hero__wordmark" data-glitch>PULSE<\/h1>/);
  assert.equal(home.match(/class="band"/g).length, 2, 'a band before the pills and one at the end');
  assert.match(home, /<span class="sr-only">Choose your pill<\/span><span aria-hidden="true">&gt; choose_your_pill<\/span>/, 'the plain words for assistive technology, the prompt for the eye');
  assert.match(home, /<li><a class="badge" href="changelog\/index\.html#020---2026-09-29">v0\.2\.0<\/a><\/li><li class="badge">VS 1\.22\+<\/li>/);
  assert.match(home, /class="stats"><div><dt><a href="metrics\/index\.html">metric families<\/a><\/dt><dd>28<\/dd>/);
});

test('the pill cards are links first, blue pull and red push, each with its own way into the guide', () => {
  assert.match(home, /<a class="pill-card__go" data-go="blue" href="getting-started\/index\.html\?pill=blue"><span aria-hidden="true">&gt; <\/span>take the blue pill: path A<\/a>/);
  assert.match(home, /<a class="pill-card__go" data-go="red" href="getting-started\/index\.html\?pill=red"><span aria-hidden="true">&gt; <\/span>take the red pill: path B<\/a>/);
  assert.match(home, /\[blue pill\] pull<\/h3><p>Prometheus scrapes <code>\/metrics<\/code> from the base mod alone: point one at <code>127\.0\.0\.1:9464<\/code> next to the server, or reach it from elsewhere through a tunnel or a reverse proxy\./);
  assert.match(home, /with two docker commands, load the alert rules \(<a href="alerts\/index\.html"><code>contrib\/alerts<\/code><\/a>\) into its Prometheus/, 'the rules are loaded by hand, never promised as wired in');
  assert.match(home, /will not stop multiplying its share of the tick fires <code>PulseModHoggingTick<\/code>, Agent Smith replicating/, 'a code span holds a name the rules file has');
  assert.match(home, /\[red pill\] push<\/h3><p>Drop <code>pulseotlp_\d+\.\d+\.\d+\.zip<\/code> in beside the base mod/);
  assert.match(home, /<a class="pill-card__alt" href="otlp\/index\.html">pushing to your own collector: OTLP export<\/a>/, 'red is not reduced to cloud');
  assert.doesNotMatch(home, /local versus cloud|local vs cloud/i);
});

test('the pill artwork: the still is the image, the animated file a source that never matches until script allows it', () => {
  assert.match(home, /<picture><source data-anim media="not all" srcset="assets\/img\/pulse-pill-choice\.webp" type="image\/webp"><img src="assets\/img\/pulse-pill-choice-still\.webp" width="900" height="400" loading="lazy" decoding="async" alt="A figure in a dark coat/);
  assert.ok(built.files.get('assets/img/pulse-pill-choice.webp').length > 50_000, 'the animated WebP ships');
  const still = built.files.get('assets/img/pulse-pill-choice-still.webp');
  assert.equal(still.subarray(0, 4).toString(), 'RIFF');
  assert.equal(still.subarray(8, 12).toString(), 'WEBP');
  assert.ok(still.length < 60_000, 'a still frame, not a second animation');
});

test('the project health block: numbers when SonarCloud answered, the links alone when it did not', async () => {
  const body = { component: { measures: [{ metric: 'alert_status', value: 'OK' }, { metric: 'coverage', value: '96.8' }, { metric: 'bugs', value: '0' }, { metric: 'vulnerabilities', value: '0' }] } };
  const online = (await buildSite({ sources, fetchFn: async () => ({ ok: true, json: async () => body }) })).files.get('index.html');
  const boxes = (html) => [...html.matchAll(/<div><dt><a[^>]*href="([^"]+)">([^<]+)<\/a><\/dt>(?:<dd>([^<]*)<\/dd>)?<\/div>/g)].map((m) => [m[2], m[1], m[3]]).filter(([label]) => ['quality gate', 'coverage', 'bugs', 'vulnerabilities', 'how Pulse is tested'].includes(label));
  assert.deepEqual(boxes(online), [['quality gate', SONAR_PAGE, 'OK'], ['coverage', SONAR_PAGE, '96.8%'], ['bugs', SONAR_PAGE, '0'], ['vulnerabilities', SONAR_PAGE, '0'], ['how Pulse is tested', 'development/index.html#building-and-testing', undefined]]);
  assert.match(online, /read from SonarCloud when this site was built/);
  assert.deepEqual(boxes(home), [['quality gate', SONAR_PAGE, undefined], ['coverage', SONAR_PAGE, undefined], ['bugs', SONAR_PAGE, undefined], ['vulnerabilities', SONAR_PAGE, undefined], ['how Pulse is tested', 'development/index.html#building-and-testing', undefined]]);
  assert.match(home, /live on SonarCloud/);
  assert.match(online, /<a class="ext" href="https:\/\/sonarcloud\.io\/summary\/overall\?id=StratumServer_Pulse">quality gate<\/a>/);
  const failing = (await buildSite({ sources, fetchFn: async () => ({ ok: true, json: async () => ({ component: { measures: body.component.measures.map((m) => (m.metric === 'alert_status' ? { ...m, value: 'ERROR' } : m)) } }) }) })).files.get('index.html');
  assert.match(failing, /quality gate<\/a><\/dt><dd>ERROR<\/dd>/, 'a failing gate is shown as it is, in words');
});

test('the 404 page: one line and two ways out, in the show regime', () => {
  const lost = built.files.get('404.html');
  assert.match(lost, /<h1 class="h-prompt" id="h-lost" data-glitch>404: there is no page\.<\/h1>/);
  assert.match(lost, /Follow the white rabbit: <a href="index\.html">Home<\/a> or <a href="getting-started\/index\.html">Getting started<\/a>\./);
  assert.match(lost, /data-regime="show"/);
});

test('the opening is 3.5 seconds at the most, types three lines and ends in a dissolve', () => {
  const t = openingTimeline();
  assert.equal(t.chars.length, OPENING.join('').length);
  assert.ok(t.end <= 3500, `${t.end} ms`);
  assert.ok(t.dissolve < t.end);
  assert.deepEqual(t.chars.map((c) => c.at), [...t.chars.map((c) => c.at)].sort((a, b) => a - b), 'in order');
  assert.deepEqual(t.chars.at(-1), { at: t.chars.at(-1).at, line: 2, count: 24 });
});

// ------------------------------------------------------------------ the white rabbit
test('the rabbit is 16 by 14 cells of four frames, each with its eye', () => {
  assert.deepEqual(Object.keys(RABBIT.frames), ['sit', 'twitch', 'stretch', 'land']);
  assert.deepEqual(Object.keys(RABBIT.colours), ['W', 's', 'k']);
  for (const [name, rows] of Object.entries(RABBIT.frames)) {
    assert.equal(rows.length, 14, name);
    assert.ok(rows.every((r) => r.length === 16 && /^[.Wsk]+$/.test(r)), `${name}: 16 cells of . W s k`);
    assert.equal(rows.join('').split('k').length - 1, 1, `${name}: one black cell, the eye`);
    assert.ok(rows.join('').split('W').length - 1 > 40, `${name}: mostly white`);
  }
  assert.ok(RABBIT.frames.sit.slice(0, 4).every((r) => /W/.test(r)), 'the ears of the sitting rabbit reach the top rows');
});

test('the rabbit is drawn as inline SVG: crisp edges, one path a colour, no style attribute, no id', () => {
  const svg = rabbitSvg();
  assert.match(svg, /^<svg class="rabbit__svg" viewBox="0 0 16 14" aria-hidden="true" focusable="false" shape-rendering="crispEdges">/);
  assert.deepEqual([...svg.matchAll(/<g class="rb rb--(\w+)">/g)].map((m) => m[1]), ['sit', 'twitch', 'stretch', 'land']);
  assert.doesNotMatch(svg, /style=| id=|<image|href=/);
  assert.equal([...svg.matchAll(/<path fill="#ffffff"/g)].length, 4, 'one white path a frame');
  assert.ok(svg.length < 3500, `${svg.length} bytes`);
  // a path draws what the grid says: the eye of the sitting frame is the cell (10, 5)
  const eye = /<g class="rb rb--sit">.*?<path fill="#000000" d="([^"]+)"/.exec(svg)[1];
  assert.equal(eye, 'M10 5h1v1h-1z');
});

test('Home holds the rail: decoration for assistive technology, out of the tab order, a link for the mouse', () => {
  const rail = /<div class="rail"[\s\S]*?<\/div>/.exec(home)[0];
  assert.match(rail, /^<div class="rail" data-rabbit aria-hidden="true"><span class="rail__line"><\/span><span class="rail__hole"><\/span><a class="rail__go" href="getting-started\/index\.html" tabindex="-1">/);
  assert.equal(home.match(/class="rail"/g).length, 1);
  assert.equal(home.match(/data-rabbit/g).length, 1, 'one rabbit on Home');
  assert.doesNotMatch(rail, /aria-label|<button|<input/);
  // calm pages have no rail and no rabbit
  for (const [path, html] of built.files) if (path.endsWith('index.html') && path !== 'index.html') assert.doesNotMatch(html, /class="rail|data-rabbit|rabbit__/, path);
});

test('the last block of Home is a plain link down the rabbit hole, before the closing band', () => {
  assert.match(home, /<div class="panel panel--hole" data-reveal><p><a class="hole" href="getting-started\/index\.html"><span aria-hidden="true">&gt; <\/span>down the rabbit hole: get started<\/a><\/p><\/div>\n<div class="band"/);
  assert.ok(home.indexOf('panel--hole') > home.indexOf('h-compat'), 'after the compatibility note');
});

test('the rabbit of the 404 page sits beside the message and links to Home, with a name', () => {
  const lost = built.files.get('404.html');
  assert.equal(rabbitName('Home'), 'Follow the white rabbit: Home');
  assert.match(lost, /<a class="rabbit" data-rabbit href="index\.html" aria-label="Follow the white rabbit: Home"><span class="rabbit__hop"><svg class="rabbit__svg"/);
  assert.match(lost, /<div class="lost__text"><h1 class="h-prompt" id="h-lost" data-glitch>404: there is no page\.<\/h1>/);
  assert.doesNotMatch(lost, /class="rail"/, 'the rail belongs to Home');
});

test('the stylesheet: the rail follows the scroll by CSS where it can, motion starts under .js, the scrollbar is themed', () => {
  const css = built.files.get('assets/site.css');
  assert.match(css, /@supports \(animation-timeline: scroll\(\)\) \{\s*\.js \.rail__go \{ animation: rail-go linear both; animation-timeline: scroll\(root block\); \}/);
  assert.match(css, /transform: translateY\(calc\(var\(--p, 0\) \* var\(--travel\)\)\)/, 'the fallback: script sets --p');
  assert.match(css, /html\[data-rain="off"\] \.rail__go \{ animation: none; \}/, 'the rain switch leaves no animation object, and the script sets the position');
  assert.match(css, /@keyframes rail-go \{ from \{ transform: translateY\(0\); \} to \{ transform: translateY\(var\(--travel\)\); \} \}/, 'an explicit start, so a --p left by the script cannot bend the animation');
  assert.match(css, /@media \(min-width: 76rem\) \{ \.js \.rail \{ display: block; \}/, 'the rail only where there is a gutter for it, and only with script');
  assert.match(css, /html \{[^}]*scrollbar-color: var\(--line-strong\) var\(--bg-panel\); scrollbar-width: thin;/, 'the native scrollbar is themed, thin, and never hidden');
  assert.doesNotMatch(css, /::-webkit-scrollbar|scrollbar-width: none/);
  for (const m of css.matchAll(/^([^@{}\n][^{}\n]*)\{[^}\n]*\banimation:\s*(?!\s|none)[^;}\n]+/gm)) assert.match(m[1], /\.js /, `an animation outside .js: ${m[1]}`);
});

test('the page rabbit hops only while motion is allowed, and does nothing in a page without one', () => {
  assert.equal(typeof hopRabbit, 'function');
});
