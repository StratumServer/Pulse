// The Home copy: site-only text, the same sentences as the ModDB listing (docs/moddb/listing.html), with
// every number that can age computed by the build instead of written here. Where the copy departs from
// the listing it says why. The terminal's sample and replies live in js/terminal.js, so that the page
// and the live prompt cannot disagree; this file guards them (checkFacts, checkReplies).
import { readFileSync } from 'node:fs';
import { COMMANDS, DEFAULTS, README_FACTS, SAMPLE, SOURCE } from '../js/terminal.js';

/** The pill artwork (docs/assets/moddb/pulse-pill-choice.webp), shown above the two pill cards. */
export const PILL_ART = {
  alt: 'A figure in a dark coat holds out both open hands: a blue pill in the left hand, a red pill in the right.',
  still: 'assets/img/pulse-pill-choice-still.webp',
  animated: 'assets/img/pulse-pill-choice.webp',
  width: 900, height: 400,
};

export const TAGLINE = ['Wake up, admin.', 'Your server has a pulse.'];
export const LEDE = 'Tick health, players, network, and per-mod attribution, all on a Prometheus endpoint you already trust.';

/** The badge row. The version and the game version are computed; "zero dependencies" is true of the base mod only (README). */
export const BADGES = ['SERVER SIDE', '.NET 10', 'OPEN SOURCE', 'ZERO DEPENDENCIES'];

// "Sample", not "real": the values trace to nothing in the repository.
export const sampleCaption = (zip) => `Sample output. One zip, no agent, no database, no account. Drop <code>${zip}</code> into <code>Mods/</code>, start the server, scrape.`;

export const DOES = `Pulse serves your server's own health numbers on a Prometheus scrape endpoint: tick rate and tick time, players and entities, network and autosave pauses, log health, and the .NET runtime underneath it. Nothing calls home, nothing needs an account, and it does not talk to anything unless something comes and reads <code>/metrics</code>. An optional second mod pushes the same numbers over OTLP for Grafana Cloud, Datadog or any collector that speaks it.`;

export const GET = [
  ['Tick health.', `Rate, time as a histogram (p95 and p99 are yours), the budget to read saturation against, and the engine's own <code>busy time</code>, showing load climb long before TPS drops.`],
  ['Per-mod attribution.', 'Turn it on and see which mod is actually spending the tick, live, with a server command, no restart.'],
  ['Players and world.', 'Online count, ping, deaths; entities with a top-ten by type; chunks, worldgen queue, columns generated.'],
  ['Network and pauses.', 'TCP and UDP rates and totals; autosave suspends counted and timed, the freeze players actually feel.'],
  ['Log health and runtime.', 'Warning, error and fatal counters, four engine warnings worth alerting on, and the .NET runtime GC, heap, CPU and thread pool.'],
];

// The overview picture, once for every page that shows it (Home, Dashboard, Getting started): its file, its size and its alt text.
// The caption links contrib/grafana when it is given an address, and not on the Dashboard page, which is that kit's own.
export const OVERVIEW = {
  src: 'assets/img/dashboard-overview.png', width: 1880, height: 629,
  alt: 'Grafana dashboard: players, uptime, tick rate and tick time at a glance, plus tick health graphs, from a test server with three scripted players and about 120 chickens',
  caption: (href) => `The dashboard bundled in ${href ? `<a href="${href}"><code>contrib/grafana</code></a>` : '<code>contrib/grafana</code>'}, scraping a test server with three players and about 120 chickens. Provision it with two docker commands, or point your own Grafana at the same JSON.`,
};

// "upgrading from 0.1.0 fills in" became "upgrading fills in": the first would age with every release.
export const stepCard = (zip) => `Put <code>${zip}</code> in your server's <code>Mods/</code> folder and start it. Defaults land in <code>ModConfig/pulse.json</code>; upgrading fills in the new keys and keeps the ones you set.`;
// "get the numbers out", not "read /metrics": the push path never reads it.
export const LAST_CHANCE = 'This is your last chance. After this, there is no going back. Two ways to get the numbers out: choose one, or both.';

// A scraper on another machine needs a tunnel or a reverse proxy (the endpoint binds loopback), and the alert rules are not
// wired into the kit's Prometheus (contrib/alerts/README.md): they are loaded by hand.
export const bluePill = (grafana, alerts) => `Prometheus scrapes <code>/metrics</code> from the base mod alone: point one at <code>127.0.0.1:9464</code> next to the server, or reach it from elsewhere through a tunnel or a reverse proxy. Bring up the bundled Grafana dashboard (<a href="${grafana}"><code>contrib/grafana</code></a>) with two docker commands, load the alert rules (<a href="${alerts}"><code>contrib/alerts</code></a>) into its Prometheus, or point your own Grafana at the same JSON.`;
export const redPill = (otlpZip) => `Drop <code>${otlpZip}</code> in beside the base mod and it pushes every interval over OTLP: point <code>Endpoint</code> at a local OpenTelemetry collector, Grafana Cloud, Datadog, Honeycomb or New Relic. See how deep the rabbit hole goes.`;
export const WHITE_RABBIT = (href) => `New to Prometheus? Follow the white rabbit: the <a href="${href}">getting-started guide</a> walks both pills end to end.`;

export const oracle = (attribution) => `Tick busy time tells you the server is working hard. <a href="${attribution}">Attribution</a> is the Oracle: ask, and it tells you which mod is actually eating the tick, a per-mod share of the main thread on a continuous series you can graph and alert on. It is off by default, because it costs tick time to measure. You play operator: <code>/pulse attribution on</code> and <code>off</code> jack it in and out of the running server live, no restart, so the moment you actually need it is not the moment you have to plan for it.`;
// Thread-safe behaviours are attributed, and read low (README, "What it cannot see").
export const COST = 'The cost is measured, not guessed: about 26% of the tick budget while a burst runs, blending down to about 0.9% amortised at the shipped default of ten ticks every ten seconds. It is a main-thread sample, not a full profiler: broadcast event handlers are not attributed to a mod by name, and thread-safe behaviours read low. Good for spotting which mod to look at next; for a real call tree, reach for a sampling profiler.';
// The code span holds the name of the rule. "Agent Smith replicating" is the pun beside it: no rule or annotation of the file says it.
const HOGGING_RULE = 'PulseModHoggingTick';
export const smith = (alerts) => `The bundled <a href="${alerts}">alert rules</a> cover the obvious case too: a single mod that will not stop multiplying its share of the tick fires <code>${HOGGING_RULE}</code>, Agent Smith replicating, and only while the server is genuinely under load, so a mod that is merely heavy on an idle server never pages anyone. Every other alert here is your deja vu: the glitch that means something in the Matrix actually changed.`;

export const ATTRIBUTION_SHOT = {
  src: 'assets/img/dashboard-attribution.png', width: 1880, height: 751,
  alt: 'Grafana attribution row: tick share by mod over time, current share as a bar gauge, attributed tick time, and profiling health, from a plain dedicated server running only the vanilla modules, Pulse and Pulse OTLP, with attribution turned on and about 8,000 entities loaded',
  caption: "A plain dedicated server running only the vanilla modules, Pulse and Pulse OTLP, attribution switched on with <code>/pulse attribution on</code> at the console and about 8,000 entities loaded. Pulse's own share stays near zero, 0.12% here; the engine, the game and survival take most of the rest.",
};

// The listing says "Vintage Story 1.22.x"; here it is "1.22 or newer", which is what modinfo.json states.
export const COMPATIBILITY = 'Runs on your Nebuchadnezzar: Vintage Story 1.22 or newer, dedicated server only, on .NET 10. Linux and Windows alike, no administrator rights and no <code>netsh</code> reservation needed on Windows. Runs on vanilla, <code>Stratum</code> and <code>Lithos</code> servers. The base mod is one dll with nothing bundled, so nothing to collide with another mod; <code>pulseotlp</code> carries its own dependencies in its own zip. A failed bind, an unreachable backend, or a game update touching the deep-metrics probe all degrade cleanly: nothing here can take a server down. MIT licensed.';

// ---- the white rabbit ------------------------------------------------------------------------------------
// Pixel art of our own, side view, facing right, 16 by 14 cells: 48 px wide is three pixels a cell and 32 px
// (a phone) is two, both whole, so the edges stay crisp at any whole device ratio. W white, s pale mint (the
// inner ear, the haunch, the nose), k black (the eye), . nothing. Four frames: sitting, an ear flicked back
// (the idle twitch), stretched in the air, and the squash of the landing; a hop is crouch, stretch, land, sit.
export const RABBIT = {
  colours: { W: '#ffffff', s: '#ccffdb', k: '#000000' },
  frames: {
    sit: [
      '.....WW...WW....',
      '.....WsW..WsW...',
      '......WsW.WsW...',
      '......WsW.WWW...',
      '.......WWWWWW...',
      '.......WWWkWWW..',
      '.......WWWWWWWs.',
      '.......WWWWWWW..',
      '...WWW.WWWWWW...',
      '..WWWWWWWWWWWW..',
      '.WWWWWsssWWWWW..',
      'WWWWWsssssWWWW..',
      'WWWWWsssssWWWW..',
      '.WWWWWWWWWWWWWW.',
    ],
    twitch: [
      '................',
      '...WW.....WW....',
      '...WsW....WsW...',
      '....WsW...WsW...',
      '.....WsW..WWW...',
      '......WsWWWWW...',
      '.......WWWWWW...',
      '.......WWWkWWW..',
      '.......WWWWWWWs.',
      '...WWW.WWWWWW...',
      '..WWWWWWWWWWWW..',
      '.WWWWWsssWWWWW..',
      'WWWWWsssssWWWW..',
      '.WWWWWWWWWWWWWW.',
    ],
    stretch: [
      '................',
      '..WW............',
      '..WsWW..........',
      '...WsWWW........',
      '....WWWWWWWWW...',
      '..WWWWWWWWWWWW..',
      '.WWWWWWWWWWkWWWs',
      'WWsssWWWWWWWWW..',
      '.WWssssWWWWWWW..',
      '..WWWWWWWW.WWW..',
      '...WW...WW..WW..',
      '..WW....WW...WW.',
      '.WW...........W.',
      '................',
    ],
    land: [
      '................',
      '.....WW...WW....',
      '.....WsW..WsW...',
      '......WsW.WsW...',
      '......WsW.WWW...',
      '.......WWWWWW...',
      '.......WWWkWWW..',
      '.......WWWWWWWs.',
      '...WWW.WWWWWW...',
      '..WWWWWWWWWWWW..',
      '.WWWWWWWWWWWWWW.',
      'WWWWWsssWWWWWWW.',
      'WWWWsssssWWWWWWW',
      '.WWWWWWWWWWWWWW.',
    ],
  },
};
/** The text link that is the keyboard and no-script way into the rabbit rail (the rabbit itself is decorative). */
export const HOLE = 'down the rabbit hole: get started';
/** The rabbit of the 404 page is a link, so it has a name. */
export const rabbitName = (where) => `Follow the white rabbit: ${where}`;

export const NOT_FOUND = { heading: '404: there is no page.', lead: 'Follow the white rabbit:' };

// ---- the guards ----------------------------------------------------------------------------------------------
// Each is a one-line check that stops the build with a sentence naming what moved. The terminal must not
// invent output, and the numbers on Home must not age without somebody noticing.

/** The C# source with its string concatenations joined, so that a sentence split over two lines is one literal again. */
const joinLiterals = (cs) => cs.replace(/"\s*\+\s*\$?"/g, '');

/** Every reply the terminal prints is a whole literal of Pulse/PulseCommands.cs. */
export function checkReplies(commandsCs) {
  const source = joinLiterals(commandsCs);
  for (const [name, template] of Object.entries(SOURCE)) {
    if (!source.includes(`"${template}"`)) throw new Error(`Pulse/PulseCommands.cs no longer holds the reply "${template}" (SOURCE.${name} in js/terminal.js): the terminal of Home must not invent output, so update it from the file`);
  }
}

const COMMANDS_CS = new URL('../../../Pulse/PulseCommands.cs', import.meta.url);

/**
 * docs: the lexed documents; data: lib/data.mjs; commandsCs: Pulse/PulseCommands.cs (read from the
 * repository unless a test hands it over).
 */
export function checkFacts({ docs, data, commandsCs }) {
  const fail = (what) => { throw new Error(`Home: ${what}`); };
  const readme = docs['README.md'].tokens.map((t) => t.raw).join('').replace(/\s+/g, ' ');       // a phrase may wrap onto the next line
  // the sample: every pulse_ family is documented, every dotnet_ family is one the dashboard reads
  for (const [name] of SAMPLE.lines.join('\n').matchAll(/\b(?:pulse|dotnet)_[a-z0-9_]+/g)) {
    if (name.startsWith('pulse_') ? !data.metric(name) : !data.usedBy(name).panels.length) fail(`the terminal sample prints ${name}, which README.md does not document as a metric family (js/terminal.js, SAMPLE)`);
  }
  // the cost paragraph and the replies rest on the README
  for (const phrase of ['26%', '0.9%', ...README_FACTS, ...COMMANDS.map(([c]) => `\`${c}\``)]) {
    if (!readme.includes(phrase)) fail(`README.md no longer holds "${phrase}", which the Home copy or the terminal relies on`);
  }
  const defaults = Object.fromEntries(data.config.find((f) => f.file === 'ModConfig/pulse.json').rows.map((r) => [r.key, r.default]));
  if (defaults['Attribution.BurstTicks'] !== DEFAULTS.burstTicks || defaults['Attribution.IntervalSeconds'] !== DEFAULTS.intervalSeconds) {
    fail(`the defaults of Attribution.BurstTicks and Attribution.IntervalSeconds are no longer ${DEFAULTS.burstTicks} and ${DEFAULTS.intervalSeconds}: the cost paragraph says "ten ticks every ten seconds" and the terminal runs that cycle`);
  }
  if (defaults.Bind !== '127.0.0.1' || defaults.Port !== 9464) fail('the default endpoint is no longer 127.0.0.1:9464, which the pill card and the terminal sample name');
  if (!data.rules.some((r) => r.alert === HOGGING_RULE)) fail(`contrib/alerts/pulse-alerts.yml has no rule called ${HOGGING_RULE}, which the Oracle block names (smith in content/home.mjs)`);
  checkReplies(commandsCs ?? readFileSync(COMMANDS_CS, 'utf8'));
}
