// Home and the 404 page, the two pages of the show regime (they may animate, the others never do).
// The blocks of the page, the project's own numbers among them; the words are
// in content/home.mjs, the terminal's sample and replies in js/terminal.js. Everything is complete
// HTML: the script only enhances it. No style attribute, no inline handler: the policy blocks both.
import * as C from '../../content/home.mjs';
import { COMMANDS, SAMPLE } from '../../js/terminal.js';
import { SONAR_PAGE } from '../data.mjs';
import { MODDB, REPO } from '../layout.mjs';
import { slugBase } from '../sources.mjs';

// The hero scene, in the geometry of docs/assets/moddb/generate_hero.py: the wire under the wordmark and the four beats.
const WIRE = 'M 450 195 C 456 195, 462 300, 469 300 L 496.4 300 L 504.9 285.3 L 515.2 314.7 L 525.4 262.2 L 537.4 323.1 L 546 300 L 576.8 300 L 604.1 300 L 612.7 285.3 L 622.9 314.7 L 633.2 262.2 L 645.2 323.1 L 653.7 300 L 684.5 300 L 711.9 300 L 720.4 285.3 L 730.7 314.7 L 740.9 262.2 L 752.9 323.1 L 761.5 300 L 792.2 300 L 819.6 300 L 828.2 285.3 L 838.4 314.7 L 848.7 262.2 L 860.7 323.1 L 869.2 300 L 900 300';
const LENSES = 'M 90 195 L 133.2 195 L 146.7 183.1 L 162.9 206.9 L 179 164.4 L 197.9 213.7 L 211.4 195 L 260 195 M 280 195 L 323.2 195 L 336.7 183.1 L 352.9 206.9 L 369 164.4 L 387.9 213.7 L 401.4 195 L 450 195';
const TRAIL = `${LENSES} ${WIRE.slice(WIRE.indexOf('C'))}`;
const STRIP = `M 469 300 ${WIRE.slice(WIRE.indexOf('L 496.4'))}`;
const TRACE = 'fill="none" stroke-linecap="round" stroke-linejoin="round" filter="url(#glow)"';
const hero = (tagline) => `<header class="hero" data-hero>
<svg class="hero__scene hero__scene--wide" viewBox="0 0 900 400" aria-hidden="true" focusable="false"><use href="#glasses"/><path class="hero__wire" pathLength="1" d="${WIRE}" stroke="#00c83c" stroke-opacity=".59" stroke-width="4" ${TRACE}/><path class="sweep-trail" pathLength="1633.3" d="${TRAIL}" stroke="#00ff41" stroke-opacity=".75" stroke-width="7" stroke-dasharray="193 1440.3" ${TRACE}/><circle class="sweep-head" r="7" fill="#d6ffe3" filter="url(#glow)"/></svg>
<svg class="hero__scene hero__scene--narrow" viewBox="62 111 416 168" aria-hidden="true" focusable="false"><use href="#glasses"/><path class="sweep-trail sweep-trail--lens" pathLength="539.7" d="${LENSES}" stroke="#00ff41" stroke-opacity=".75" stroke-width="7" stroke-dasharray="120 419.7" ${TRACE}/><circle class="sweep-head sweep-head--lens" r="7" fill="#d6ffe3" filter="url(#glow)"/></svg>
<div class="hero__text"><h1 class="hero__wordmark" data-glitch>PULSE</h1><p class="hero__tagline">${tagline.map((s) => `<span>${s}</span>`).join(' ')}</p></div>
<svg class="hero__scene hero__strip" viewBox="469 255 431 75" aria-hidden="true" focusable="false"><path class="hero__wire" pathLength="1" d="${STRIP}" stroke="#00c83c" stroke-opacity=".59" stroke-width="4" ${TRACE}/></svg>
</header>`;

/** A band between two bright rules, rained on by script: a black strip with no script, and still a separator. */
const BAND = '<div class="band" role="presentation"><canvas data-rain-band aria-hidden="true"></canvas></div>';

/**
 * The white rabbit as inline SVG: every frame of C.RABBIT, one path a colour, runs of cells merged, crisp edges. Frames
 * are told apart by a class, never an id (the page holds two rabbits at most and ids must be unique). Only the frame
 * that CSS shows is visible; the rest wait at opacity 0.
 */
export function rabbitSvg() {
  const { colours, frames } = C.RABBIT;
  const group = (name, rows) => {
    if (rows.length !== 14 || rows.some((r) => r.length !== 16 || /[^.Wsk]/.test(r))) throw new Error(`the rabbit frame "${name}" must be 14 rows of 16 cells made of . W s k (content/home.mjs, RABBIT)`);
    const paths = Object.entries(colours).map(([cell, fill]) => {
      let d = '';
      rows.forEach((row, y) => { for (const m of row.matchAll(new RegExp(`${cell}+`, 'g'))) d += `M${m.index} ${y}h${m[0].length}v1h-${m[0].length}z`; });
      return d ? `<path fill="${fill}" d="${d}"/>` : '';
    }).join('');
    return `<g class="rb rb--${name}">${paths}</g>`;
  };
  return `<svg class="rabbit__svg" viewBox="0 0 16 14" aria-hidden="true" focusable="false" shape-rendering="crispEdges">${Object.entries(frames).map(([name, rows]) => group(name, rows)).join('')}</svg>`;
}

/** A heading in the prompt style of the ModDB page: the plain words for assistive technology, the prompt for the eye. */
const prompt = (id, words, snake) => `<h2 class="h-prompt" id="${id}"><span class="sr-only">${words}</span><span aria-hidden="true">&gt; ${snake}</span></h2>`;
const panel = (id, words, snake, body, { reveal = true } = {}) => `<section class="panel"${reveal ? ' data-reveal' : ''} aria-labelledby="${id}">${prompt(id, words, snake)}\n${body}\n</section>`;

/** One stat box: the value above, the label below, the whole box a link. A box without a value is a link and nothing else. */
const stat = (href, label, value, ext = false) => `<div><dt><a${ext ? ' class="ext"' : ''} href="${href}">${label}</a></dt>${value === undefined ? '' : `<dd>${value}</dd>`}</div>`;

const shot = ({ src, width, height, alt }, caption, open) => `<figure class="shot"><a class="shot__open" href="${src}" aria-label="${open}"><img src="${src}" width="${width}" height="${height}" loading="lazy" decoding="async" alt="${alt}"></a><figcaption>${caption}</figcaption></figure>`;

export default function home({ docs, data, pageUrl, esc }) {
  C.checkFacts({ docs, data });
  const to = (slug, hash) => pageUrl('', slug, hash);
  const v = data.version, zip = `pulse_${v}.zip`, otlpZip = `pulseotlp_${v}.zip`;
  const release = (file) => `${REPO}/releases/download/v${v}/${file}`;
  const panels = data.dashboard.rows.flatMap((r) => r.panels).length;

  const badges = [`<li><a class="badge" href="${to('changelog', slugBase(`[${v}] - ${data.released}`))}">v${esc(v)}</a></li>`, `<li class="badge">VS ${esc(data.game.split('.').slice(0, 2).join('.'))}+</li>`, ...C.BADGES.map((b) => `<li class="badge">${b}</li>`)];

  const term = `<figure class="term" data-term>
<div class="term__bar" aria-hidden="true"><span class="term__dot"></span><span class="term__dot"></span><span class="term__dot"></span><span class="term__title">admin@vs-server: ~</span></div>
<pre class="term__body" tabindex="0" role="group" aria-label="Sample session: curl of the metrics endpoint"><span class="term__cmd">$ ${esc(SAMPLE.command)}</span>
${SAMPLE.lines.map((l) => `<span class="term__l">${esc(l)}</span>\n`).join('')}<span class="term__cmd term__prompt">$ <span class="cursor" aria-hidden="true"></span></span></pre>
<figcaption>${C.sampleCaption(zip)}</figcaption>
</figure>`;

  const covers = [stat(to('metrics'), 'metric families', data.metrics.length), stat(to('dashboard'), 'dashboard panels', panels), stat(to('alerts'), 'alert rules', data.rules.length)];
  const features = `<ul class="features">${C.GET.map(([lead, text]) => `<li><b>${lead}</b> ${text}</li>`).join('')}</ul>`;

  // The project's own numbers, baked in at build time. No numbers (offline, SonarCloud silent): the links alone.
  const h = data.health;
  const vitals = [stat(SONAR_PAGE, 'quality gate', h ? esc(h.gate) : undefined, true), stat(SONAR_PAGE, 'coverage', h ? `${h.coverage}%` : undefined, true),
    stat(SONAR_PAGE, 'bugs', h?.bugs, true), stat(SONAR_PAGE, 'vulnerabilities', h?.vulnerabilities, true), stat(to('development', 'building-and-testing'), 'how Pulse is tested')];

  const pills = `<div class="pills">
<article class="pill-card"><h3 class="pill-card__title"><span class="pill-card__glyph" aria-hidden="true"></span>[blue pill] pull</h3><p>${C.bluePill(to('dashboard'), to('alerts'))}</p><a class="pill-card__go" data-go="blue" href="${to('getting-started')}?pill=blue"><span aria-hidden="true">&gt; </span>take the blue pill: path A</a></article>
<article class="pill-card pill-card--push"><h3 class="pill-card__title"><span class="pill-card__glyph" aria-hidden="true"></span>[red pill] push</h3><p>${C.redPill(otlpZip)}</p><p><a class="pill-card__alt" href="${to('otlp')}">pushing to your own collector: OTLP export</a></p><a class="pill-card__go" data-go="red" href="${to('getting-started')}?pill=red"><span aria-hidden="true">&gt; </span>take the red pill: path B</a></article>
</div>`;
  // The artwork is an animated WebP that CSS cannot pause. The still is the image; the animated file is a <source> that
  // never matches until script has checked that the reader has not asked for less motion and the rain switch is on
  // (syncPillArt in js/home-show.js), so a page with no script shows the still.
  const art = `<p class="pillart"><picture><source data-anim media="not all" srcset="${C.PILL_ART.animated}" type="image/webp"><img src="${C.PILL_ART.still}" width="${C.PILL_ART.width}" height="${C.PILL_ART.height}" loading="lazy" decoding="async" alt="${C.PILL_ART.alt}"></picture></p>\n`;
  const choose = `<div class="zipcard"><h3 class="zipcard__title">[1] drop the zip</h3><p>${C.stepCard(zip)}</p><p class="dl"><a class="btn btn--sm ext" href="${release(zip)}">download ${zip}</a> <span>version ${esc(v)}, released ${esc(data.released)}</span></p></div>
<p class="pillnote">${C.LAST_CHANCE}</p>\n${art}${pills}\n<p class="pillnote">${C.WHITE_RABBIT(to('getting-started'))}</p>`;

  const oracle = `<p>${C.oracle(to('attribution'))}</p>
<dl class="cmds">${COMMANDS.map(([c, d]) => `<div><dt><code>${esc(c)}</code></dt><dd>${esc(d)}</dd></div>`).join('')}</dl>
<p class="note">${C.COST}</p>
<p class="note">${C.smith(to('alerts'))}</p>`;

  const html = [
    hero(C.TAGLINE.map(esc)),
    `<p class="lede">${esc(C.LEDE)}</p>`,
    `<div class="cta"><a class="btn btn--primary" href="${to('getting-started')}">Get started</a><a class="btn ext" href="${MODDB}">Download on ModDB</a><a class="btn btn--quiet ext" href="${REPO}">GitHub</a></div>`,
    `<ul class="badges" aria-label="At a glance">${badges.join('')}</ul>`,
    term,
    panel('h-does', 'What Pulse does', 'what_pulse_does', `<p>${C.DOES}</p>`),
    BAND,
    panel('h-pill', 'Choose your pill', 'choose_your_pill', choose),
    `<div class="split" data-reveal>${panel('h-get', 'What you get', 'what_you_get', features, { reveal: false })}<dl class="stats">${covers.join('')}</dl></div>`,
    panel('h-vitals', 'Vital signs', 'vital_signs', `<p class="note">${h ? 'The project\'s own numbers: those of the development branch, read from SonarCloud when this site was built.' : 'The project\'s own numbers live on SonarCloud.'}</p>\n<dl class="stats stats--health">${vitals.join('')}</dl>`),
    panel('h-shot', 'Server overview', 'server_overview', shot(C.OVERVIEW, C.OVERVIEW.caption(to('dashboard')), 'Open the dashboard overview at full size')),
    panel('h-oracle', 'The Oracle', 'the_oracle', oracle),
    panel('h-attr', 'Attribution row', 'attribution_row', shot(C.ATTRIBUTION_SHOT, C.ATTRIBUTION_SHOT.caption, 'Open the attribution row at full size')),
    `<aside class="callout" data-reveal aria-labelledby="h-compat"><h2 class="h-prompt" id="h-compat"><span class="sr-only">Compatibility</span><span aria-hidden="true">&gt; compatibility</span></h2><p>${C.COMPATIBILITY}</p></aside>`,
    // the last block: the way down the rabbit hole is a plain link, the keyboard and no-script way into what the rail's rabbit only shows
    `<div class="panel panel--hole" data-reveal><p><a class="hole" href="${to('getting-started')}"><span aria-hidden="true">&gt; </span>${C.HOLE}</a></p></div>`,
    BAND,
    // The scroll rail: a dim trail down the right gutter, the rabbit on it by scroll progress, a hole at the end. Decoration
    // for assistive technology (the link above is the way in) and out of the tab order; where there is no gutter, hidden.
    `<div class="rail" data-rabbit aria-hidden="true"><span class="rail__line"></span><span class="rail__hole"></span><a class="rail__go" href="${to('getting-started')}" tabindex="-1"><span class="rail__in"><span class="rabbit__hop">${rabbitSvg()}</span></span></a></div>`,
  ].join('\n');

  const notFound = `<section class="panel panel--lost" aria-labelledby="h-lost"><a class="rabbit" data-rabbit href="${to('')}" aria-label="${C.rabbitName('Home')}"><span class="rabbit__hop">${rabbitSvg()}</span></a><div class="lost__text"><h1 class="h-prompt" id="h-lost" data-glitch>${C.NOT_FOUND.heading}</h1><p>${C.NOT_FOUND.lead} <a href="${to('')}">Home</a> or <a href="${to('getting-started')}">Getting started</a>.</p></div></section>`;
  return [
    { slug: '', title: 'Home', description: data.description, layout: 'home', html },
    { slug: '404', title: 'Page not found', description: 'There is no page at this address.', layout: 'home', html: notFound },
  ];
}
