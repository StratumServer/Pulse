// The interactive terminal of Home (figure[data-term]). Show pages only.
//
// Progressive enhancement of the static sample the page already holds: on first view it types the
// `curl` session once (a click or focus completes it at once), then a live prompt takes commands.
// Nothing here invents output. The replies of /pulse are the mod's own sentences, quoted from
// Pulse/PulseCommands.cs as C# interpolated strings (SOURCE below); the build refuses to go on when
// one of them is no longer in that file (content/home.mjs, checkReplies). What the replies state
// about the cycle comes from the README: off by default, the first burst one interval after
// switching on, ten ticks every ten seconds, a 33 ms tick.
//
// Rules: what the reader types reaches the page through text nodes only, never as HTML. The log
// keeps 200 lines. The log is a role="log" region, the input has an accessible name, Tab leaves the
// terminal, and nothing here ever takes the focus by itself.
import { make } from './dom.js';
import { OPENING, goPill, hopRabbit, playOpening } from './home-show.js';

export const SCROLLBACK = 200;
export const MAX_INPUT = 120;
const PROMPT = '$ ';

/** The sample of the page, and what `curl` prints. Every pulse_ name is a documented family (guarded by the build). */
export const SAMPLE = {
  command: 'curl 127.0.0.1:9464/metrics',
  lines: [
    'pulse_server_ticks_total 92183',
    'pulse_server_tick_busy_seconds 0.004',
    'pulse_players_online 14',
    'pulse_mod_tick_share{modid="survival"} 0.14',
    'pulse_entities_by_code{code="chicken-rooster"} 213',
    'pulse_server_suspend_seconds_total 8.42',
    'pulse_engine_warnings_total{kind="overload"} 0',
    'dotnet_gc_pause_time_seconds_total 41.2',
  ],
};

/** What `help` lists, and what the oracle block of the page prints: the four commands of the ModDB listing. */
export const COMMANDS = [
  ['/pulse attribution on', 'start profiling, next burst one interval away'],
  ['/pulse attribution off', 'stop, drop a burst in progress rather than half-publish it'],
  ['/pulse attribution status', 'is it running, which cycle, how many ticks profiled'],
  ['/pulse reload', 're-read pulse.json and apply Attribution live'],
];

/** The sentences of Pulse/PulseCommands.cs, each one whole C# literal, holes as written there. */
export const SOURCE = {
  cycle: 'bursts of {burstTicks} ticks every {intervalSeconds}s',
  notWritten: 'pulse.json was not changed, so the file decides again after a restart.',
  switchedOn: 'Attribution is on: {Cycle(burstTicks, intervalSeconds)}. {NotWritten}',
  switchedOff: "Attribution is off, and the engine's frame profiler with it, unless the engine's own /debug logticks still wants it running. {NotWritten}",
  statusOn: 'Attribution is on: {Cycle(burstTicks, intervalSeconds)}, {ticksProfiled} ticks profiled so far, ',
  inBurst: 'profiling right now.',
  waiting: 'waiting for the next burst.',
  statusOff: 'Attribution is off. It would run {Cycle(burstTicks, intervalSeconds)}; {ticksProfiled} ticks profiled so far.',
  reloaded: 'Reloaded pulse.json. {attribution} {rest}',
  reloadedOn: 'Attribution is on: {Cycle(burstTicks, intervalSeconds)}.',
  reloadedOff: 'Attribution is off.',
  nothingElse: 'Nothing else in the file differs from what the server is running.',
};

/** What the replies rest on, each a phrase of the README's Attribution section (guarded too). */
export const README_FACTS = ['It is off by default', 'The first burst lands one interval later', 'roughly 26% of the 33 ms budget'];
/** The defaults of Attribution.BurstTicks and Attribution.IntervalSeconds in the README's config table (guarded). */
export const DEFAULTS = { burstTicks: 10, intervalSeconds: 10 };
export const TICK_MS = 33;

/** Fills the {holes} of a template; a hole nobody gave a value for is a mistake, not a blank. */
export function fill(template, values) {
  return template.replace(/\{([^{}]+)\}/g, (m, key) => {
    if (!Object.hasOwn(values, key)) throw new Error(`terminal: no value for the hole ${m}`);
    return values[key];
  });
}

/** Ticks profiled and whether a burst is running, `ms` after attribution was switched on: the first burst comes one interval later and takes burstTicks ticks. */
export function attributionClock(ms, cfg = DEFAULTS) {
  const interval = cfg.intervalSeconds * 1000, cycle = interval + cfg.burstTicks * TICK_MS;
  return { ticksProfiled: Math.floor(ms / cycle) * cfg.burstTicks, inBurst: ms % cycle >= interval };
}

const GO = 'getting-started/index.html';
/** The rabbit of `follow the white rabbit`, in plain characters (the page's own rabbit hops once at the same time). */
export const ASCII_RABBIT = ['  (\\_/)', "  (='.'=)", '  (")_(")'];
const norm = (s) => s.trim().replace(/\s+/g, ' ').toLowerCase();

/**
 * One terminal: `run(line)` gives { lines, effect } for a line the reader typed. A line is a string
 * or an array of strings and { text, href }; an effect is 'clear', 'opening', 'rabbit' or { pill }; `raw` marks
 * metric lines, which never wrap.
 * `now` is a clock in milliseconds, so a test can let time pass.
 */
export function createSession(now = Date.now) {
  let on = false, since = 0, banked = 0;                       // banked: ticks of the bursts finished before the cycle was last restarted
  const clock = () => attributionClock(now() - since);
  const profiled = () => banked + (on ? clock().ticksProfiled : 0);
  const stop = () => { if (on) banked += clock().ticksProfiled; on = false; };   // a burst in progress is dropped, as in the mod
  const holes = () => {
    const cycle = fill(SOURCE.cycle, { burstTicks: DEFAULTS.burstTicks, intervalSeconds: DEFAULTS.intervalSeconds });
    return { 'Cycle(burstTicks, intervalSeconds)': cycle, NotWritten: SOURCE.notWritten, ticksProfiled: profiled() };
  };
  const say = (template, extra = {}) => fill(template, { ...holes(), ...extra });
  const pad = Math.max(...COMMANDS.map(([c]) => c.length), SAMPLE.command.length) + 2;
  const help = () => [
    "Sample console. The /pulse replies are the mod's own words; the numbers are samples.",
    `  ${SAMPLE.command.padEnd(pad)}read the metrics endpoint`,
    ...COMMANDS.map(([c, d]) => `  ${c.padEnd(pad)}${d}`),
    '  help, clear, whoami. Not everything is listed.',
  ];

  return {
    run(raw) {
      const line = norm(raw);
      if (!line) return { lines: [] };
      switch (line) {
        case 'help': return { lines: help() };
        case 'clear': return { lines: [], effect: 'clear' };
        case 'whoami': return { lines: ['admin'] };
        case '/pulse attribution on':
          stop(); on = true; since = now();                    // switching on again starts the cycle over
          return { lines: [say(SOURCE.switchedOn)] };
        case '/pulse attribution off':
          stop();
          return { lines: [say(SOURCE.switchedOff)] };
        case '/pulse attribution status': {
          if (!on) return { lines: [say(SOURCE.statusOff)] };
          const { inBurst } = clock();
          return { lines: [say(SOURCE.statusOn) + (inBurst ? SOURCE.inBurst : SOURCE.waiting)] };
        }
        case '/pulse reload':                                  // the file is applied: it says Enabled false, so the cycle stops
          stop();
          return { lines: [say(SOURCE.reloaded, { attribution: SOURCE.reloadedOff, rest: SOURCE.nothingElse })] };
        case 'follow the white rabbit':
          return { lines: [...ASCII_RABBIT, ['New to Prometheus? Follow the white rabbit: the ', { text: 'getting-started guide', href: GO }, ' walks both pills end to end.']], effect: 'rabbit' };
        case 'take the blue pill': return { lines: ['> take the blue pill: path A'], effect: { pill: 'blue' } };
        case 'take the red pill': return { lines: ['> take the red pill: path B'], effect: { pill: 'red' } };
        case 'wake up': return { lines: [], effect: 'opening' };
        case 'there is no spoon': return { lines: ['There is no spoon. Only metrics.'] };
        default:
          if (/^curl (?:http:\/\/)?(?:127\.0\.0\.1|localhost):9464\/metrics\/?$/.test(line)) return { lines: [...SAMPLE.lines], raw: true };
          return { lines: [`command not found: ${raw.trim()}. Try help.`] };
      }
    },
  };
}

/** Arrow up and down through what was typed: `at` is the place in the history (its length means the line being typed), dir is -1 or 1. */
export function walk(history, at, dir) {
  return Math.max(0, Math.min(history.length, at + dir));
}

// ---- the page --------------------------------------------------------------------------------------

const TYPE_MS = 38, GAP_MS = 320, LINE_MS = 90, END_MS = 200;     // 29 characters, a pause, eight lines: about 2.3 seconds

export function initTerminal() {
  const fig = document.querySelector('[data-term]');
  const body = fig && fig.querySelector('.term__body');
  if (!body) return;
  const session = createSession();
  const history = [];
  let at = 0, draft = '', typing = null, started = false;

  // The prompt line of the static page becomes a real input, one line high, so nothing moves.
  const lastLine = body.querySelector('.term__prompt');
  if (lastLine) lastLine.remove();                                // the newline that ends the last output line stays: what is written next starts on a line of its own
  const row = make('div', 'term__in');
  const ps1 = make('span', 'term__ps1', PROMPT);
  ps1.setAttribute('aria-hidden', 'true');
  const caret = make('span', 'cursor');
  caret.setAttribute('aria-hidden', 'true');
  const input = make('input', 'term__field');
  input.type = 'text';
  input.maxLength = MAX_INPUT;
  input.placeholder = 'type help';                              // shown (dim) beside the block cursor while the field is empty and unfocused
  input.spellcheck = false;
  input.autocomplete = 'off';
  input.setAttribute('autocapitalize', 'off');
  input.setAttribute('autocorrect', 'off');
  input.setAttribute('enterkeyhint', 'send');
  input.setAttribute('aria-label', 'Terminal input: type help for the commands');
  row.append(ps1, caret, input);
  fig.insertBefore(row, body.nextSibling);
  row.addEventListener('click', () => input.focus());            // the whole line is the field, as in a terminal
  // a click or a tap in the output too, so typing goes to the prompt (and a phone opens its keyboard); not on a link, not when text is being selected
  body.addEventListener('click', (e) => { if (!e.target.closest('a') && !String(getSelection())) input.focus(); });
  fig.classList.add('term--live');

  const trim = () => {                                           // the scrollback keeps SCROLLBACK lines
    while (body.children.length > SCROLLBACK) {
      const first = body.firstElementChild;
      if (first.nextSibling && first.nextSibling.nodeType === 3) first.nextSibling.remove();
      first.remove();
    }
  };
  const write = (node, className) => {
    const line = make('span', className);
    if (typeof node === 'string') line.textContent = node;       // text, never HTML
    else for (const part of node) {
      if (typeof part === 'string') line.append(document.createTextNode(part));
      else { const a = make('a', null, part.text); a.href = (document.documentElement.dataset.root || '') + part.href; line.append(a); }
    }
    body.append(line, document.createTextNode('\n'));
    trim();
  };
  const submit = (text) => {
    write(PROMPT + text, 'term__cmd term__o');
    if (text.trim() && history.at(-1) !== text) history.push(text);
    at = history.length; draft = '';
    const { lines, effect, raw } = session.run(text);
    for (const line of lines) write(line, raw ? 'term__l' : 'term__o');
    if (effect === 'clear') body.textContent = '';
    else if (effect === 'opening') { if (!playOpening({ force: true })) for (const line of OPENING) write(line, 'term__o'); }
    else if (effect === 'rabbit') hopRabbit();
    else if (effect) goPill(effect.pill);
    body.scrollTop = body.scrollHeight;
  };

  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); const text = input.value; input.value = ''; submit(text); }
    else if (e.key === 'ArrowUp' || e.key === 'ArrowDown') {
      e.preventDefault();
      if (at === history.length) draft = input.value;
      at = walk(history, at, e.key === 'ArrowUp' ? -1 : 1);
      input.value = at === history.length ? draft : history[at];
    }
  });

  // ---- the first view: type the sample once; a click or focus completes it
  const cmd = body.querySelector('.term__cmd');
  const outputs = [...body.querySelectorAll('.term__l')];
  const full = cmd ? cmd.textContent : '';
  const finish = () => {
    if (typing) { clearTimeout(typing); typing = null; }
    cmd && cmd.style.removeProperty('clip-path');
    fig.classList.remove('is-typing');
    for (const l of outputs) l.classList.remove('is-shown');
    body.setAttribute('role', 'log');                            // from here on a new line is announced, the sample is not
  };
  const type = () => {                                           // the text is already there: it is uncovered, so nothing in the layout moves
    if (started || !cmd) return;
    started = true;
    body.setAttribute('role', 'group');                          // a live region would read out every character
    fig.classList.add('is-typing');
    const n = [...full].length;
    const steps = Array.from({ length: n + 1 }, (_, i) => [TYPE_MS, () => { cmd.style.clipPath = `inset(0 ${100 - (100 * i) / n}% 0 0)`; }]);
    steps.push([GAP_MS, () => {}], ...outputs.map((l) => [LINE_MS, () => l.classList.add('is-shown')]), [END_MS, finish]);
    const run = () => { const s = steps.shift(); if (!s) return; typing = setTimeout(() => { s[1](); run(); }, s[0]); };
    cmd.style.clipPath = 'inset(0 100% 0 0)';
    run();
  };
  // A click or focus completes the typing at once, and a reader who has already been in the terminal never sees it typed.
  for (const ev of ['pointerdown', 'focusin']) fig.addEventListener(ev, () => { started = true; finish(); });
  finish();                                                      // the sample is complete until it is told to type
  const ready = () => {
    if (!document.body.classList.contains('is-motion') || !('IntersectionObserver' in window)) return;
    const io = new IntersectionObserver((entries) => { if (entries.some((e) => e.isIntersecting)) { io.disconnect(); type(); } }, { threshold: 0.6 });
    io.observe(fig);
  };
  if (document.documentElement.classList.contains('is-opening')) document.addEventListener('pulse-live', ready, { once: true }); else ready();
}
