// The page registry, the address helpers and the shell around every page. Nothing here reads a
// document: page modules hand over finished HTML and this wraps it.
import { createHash } from 'node:crypto';

export const REPO = 'https://github.com/StratumServer/Pulse';
export const MODDB = 'https://mods.vintagestory.at/pulse';
const DISCORD = 'https://discord.gg/pd24fawhsD';
const SUPPORT = 'https://opencollective.com/stratum';
// Absolute only where a relative address cannot do: canonical, og:image and og:url. The <base> of
// 404.html, which GitHub Pages serves at any depth, is the path of this address and nothing more,
// so the policy's base-uri 'self' accepts it on any host. Every other address stays relative, so
// the site works under /Pulse/, at a domain root, in a preview host and from file://.
const SITE_URL = 'https://stratumserver.github.io/Pulse/';

/** HTML escaping for text and for double-quoted attribute values. */
export const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
/** The reverse of esc, for the characters it escapes, the apostrophe the Markdown parser escapes, and the &nbsp; before a heading's anchor link. */
export const decode = (s) => s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&nbsp;/g, ' ').replace(/&amp;/g, '&');
/** Entities decoded, every run of white space one space, the ends trimmed. */
export const squash = (s) => decode(s).replace(/\s+/g, ' ').trim();
/** The text of a piece of HTML. */
export const plainText = (html) => squash(html.replace(/<[^>]+>/g, ''));

// Navigation order, which is also the previous and next order. `top` is the top bar entry marked
// as current on that page: four Reference pages have no entry of their own and sit under Docs.
// `regime` is the motion regime: 'show' pages (Home, 404) may animate and print the rain switch
// and load assets/home.js, 'calm' pages never do (the documentation never animates).
export const PAGES = [
  { slug: '', group: 'Start', label: 'Home', top: null, regime: 'show', title: 'Pulse | Server metrics for Vintage Story' },
  { slug: 'getting-started', group: 'Start', label: 'Getting started', top: 'getting-started', regime: 'calm' },
  { slug: 'configuration', group: 'Reference', label: 'Install and configuration', top: 'configuration', regime: 'calm' },
  { slug: 'scraping', group: 'Reference', label: 'Scraping', top: 'configuration', regime: 'calm' },
  { slug: 'metrics', group: 'Reference', label: 'Metrics', top: 'metrics', regime: 'calm' },
  { slug: 'attribution', group: 'Reference', label: 'Attribution', top: 'configuration', regime: 'calm' },
  { slug: 'otlp', group: 'Reference', label: 'OTLP export', top: 'configuration', regime: 'calm' },
  { slug: 'dashboard', group: 'Kits', label: 'Dashboard', top: 'dashboard', regime: 'calm' },
  { slug: 'alerts', group: 'Kits', label: 'Alerts', top: 'alerts', regime: 'calm' },
  { slug: 'changelog', group: 'Project', label: 'Changelog', top: 'changelog', regime: 'calm' },
  { slug: 'development', group: 'Project', label: 'Development', top: null, regime: 'calm' },
];
// The one page that is not in the navigation. It sits at the site root whatever address was asked
// for, so its relative addresses are given a <base> (and a policy that allows one).
export const NOT_FOUND = { slug: '404', out: '404.html', regime: 'show', base: true };
const TOP = [['getting-started', 'Getting started'], ['configuration', 'Docs'], ['metrics', 'Metrics'], ['dashboard', 'Dashboard'], ['alerts', 'Alerts'], ['changelog', 'Changelog']];

export const pageInfo = (slug) => (slug === NOT_FOUND.slug ? NOT_FOUND : PAGES.find((p) => p.slug === slug));
export const outPath = (slug) => pageInfo(slug)?.out ?? (slug ? `${slug}/index.html` : 'index.html');

/** URL of `page` (a slug, '' for home) seen from `fromPage`, always naming index.html so it works from file://. */
export const pageUrl = (fromPage, page, hash = '') => {
  if (fromPage === page) return hash ? `#${hash}` : 'index.html';
  return `${fromPage ? '../' : ''}${page ? page + '/' : ''}index.html${hash ? '#' + hash : ''}`;
};
/** An asset seen from a page: assetUrl('metrics', 'assets/site.css') is '../assets/site.css'. */
export const assetUrl = (fromPage, path) => (fromPage ? '../' : '') + path;

// The only inline script. It marks that scripting is on (the stylesheet has a rule or two for that),
// restores the rain switch before the first paint, and arms the fail-open: deferred scripts run
// before DOMContentLoaded and main.js sets data-site first, so a script that did not load or did
// not parse leaves the plain document behind.
export const BOOT = "var d=document.documentElement;d.classList.add('js');try{if(localStorage.getItem('pulse.rain')==='off')d.dataset.rain='off'}catch(e){}addEventListener('DOMContentLoaded',function(){if(!d.dataset.site)d.classList.remove('js')});";
const bootHash = () => createHash('sha256').update(BOOT).digest('base64');
/** Eight characters that change with the content, so a deploy never pairs new HTML with an old script. */
export const fingerprint = (content) => createHash('sha256').update(content).digest('hex').slice(0, 8);

// The theme's glasses, drawn once and used by the brand in the top bar (and by the hero).
const SPRITE = `<svg class="sprite" width="0" height="0" aria-hidden="true" focusable="false"><defs><filter id="glow" x="-10%" y="-60%" width="120%" height="220%"><feGaussianBlur stdDeviation="4" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter><g id="glasses"><ellipse cx="175" cy="195" rx="89" ry="68" fill="#000" opacity=".75"/><ellipse cx="365" cy="195" rx="89" ry="68" fill="#000" opacity=".75"/><ellipse cx="175" cy="195" rx="85" ry="64" fill="#020c05"/><ellipse cx="365" cy="195" rx="85" ry="64" fill="#020c05"/><ellipse cx="147" cy="169" rx="34" ry="16" fill="#9dffb0" opacity=".086" transform="rotate(18 147 169)"/><ellipse cx="337" cy="169" rx="34" ry="16" fill="#9dffb0" opacity=".086" transform="rotate(18 337 169)"/><g fill="none" stroke="#00be44" stroke-opacity=".92" stroke-width="6" stroke-linecap="round" stroke-linejoin="round" filter="url(#glow)"><path d="M 90 195 L 133.2 195 L 146.7 183.1 L 162.9 206.9 L 179 164.4 L 197.9 213.7 L 211.4 195 L 260 195"/><path d="M 280 195 L 323.2 195 L 336.7 183.1 L 352.9 206.9 L 369 164.4 L 387.9 213.7 L 401.4 195 L 450 195"/></g><g fill="none" stroke="#2a7a44" stroke-linecap="round"><ellipse cx="175" cy="195" rx="85" ry="64" stroke-width="5"/><ellipse cx="365" cy="195" rx="85" ry="64" stroke-width="5"/><path d="M 238.8 152.7 Q 270 132.7 301.2 152.7" stroke-width="6"/></g></g></defs></svg>`;

/** The first paragraph of a page as plain text, cut at a word under 160 characters. */
function describe(html) {
  const text = plainText(/<p>([\s\S]*?)<\/p>/.exec(html)?.[1] ?? '');
  return text.length <= 160 ? text : text.slice(0, 157).replace(/\s+\S*$/, '') + '...';
}

/** The contents list: level 2 entries, level 3 entries nested under the one before them. */
function tocList(toc) {
  const items = [];
  for (const e of toc) {
    if (e.level === 3 && items.length) items.at(-1).kids.push(e);
    else items.push({ ...e, kids: [] });
  }
  const li = (e) => `<li><a href="#${esc(e.id)}">${esc(e.text)}</a>${e.kids?.length ? `<ul>${e.kids.map(li).join('')}</ul>` : ''}</li>`;
  return `<ul>${items.map(li).join('')}</ul>`;
}

/**
 * The whole document for one page.
 * page: what a page module returned; site: { version, notice, v: { css, site, home } }, the facts
 * every page shares and the fingerprints of the three assets.
 */
export function renderPage(page, site) {
  const info = pageInfo(page.slug);
  if (!info) throw new Error(`page "${page.slug}" is not in the registry of lib/layout.mjs`);
  const show = info.regime === 'show';
  const from = info.base ? '' : page.slug;       // where the page sits, for relative addresses
  const url = (slug, hash) => pageUrl(from, slug, hash);
  const asset = (path) => assetUrl(from, path);
  const title = info.title ?? `${page.title} | Pulse`;
  const description = page.description || describe(page.html);
  const canonical = SITE_URL + (page.slug ? page.slug + '/' : '');
  const rainSlot = show ? '<span data-slot="rain"></span>' : '';

  const head = `<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'self' 'sha256-${bootHash()}'; style-src 'self'; img-src 'self'; connect-src 'none'; form-action 'none'; base-uri ${info.base ? "'self'" : "'none'"}">
${info.base ? `<base href="${new URL(SITE_URL).pathname}">\n` : ''}<title>${esc(title)}</title>
<meta name="description" content="${esc(description)}">
${info.base ? '' : `<link rel="canonical" href="${canonical}">\n`}<meta property="og:type" content="website">
<meta property="og:title" content="${esc(title)}">
<meta property="og:description" content="${esc(description)}">
${info.base ? '' : `<meta property="og:url" content="${canonical}">\n`}<meta property="og:image" content="${SITE_URL}assets/img/dashboard-overview.png">
<meta name="twitter:card" content="summary_large_image">
<link rel="icon" href="${asset('assets/favicon.svg')}" type="image/svg+xml">
<link rel="stylesheet" href="${asset('assets/site.css')}?v=${site.v.css}">
<script>${BOOT}</script>
<script defer src="${asset('assets/site.js')}?v=${site.v.site}"></script>${show ? `\n<script defer src="${asset('assets/home.js')}?v=${site.v.home}"></script>` : ''}`;

  // Group labels are paragraphs, not headings: they would come before the page's h1.
  const groups = (prefix) => [...new Set(PAGES.map((p) => p.group))].map((g) => `<p class="side__h" id="${prefix}-${g.toLowerCase()}">${g}</p><ul aria-labelledby="${prefix}-${g.toLowerCase()}">${PAGES.filter((p) => p.group === g).map((p) => `<li><a href="${url(p.slug)}"${p.slug === from && !info.base ? ' aria-current="page"' : ''}>${esc(p.label)}</a></li>`).join('')}</ul>`).join('');
  const topLinks = TOP.map(([slug, label]) => `<a href="${url(slug)}"${info.top === slug ? ' aria-current="page"' : ''}>${label}</a>`).join('\n    ');
  const nav = `<header class="nav"><div class="nav__in">
  <a class="nav__brand" href="${url('')}" aria-label="Pulse home"><svg viewBox="82 123 376 144" aria-hidden="true" focusable="false"><use href="#glasses"/></svg>PULSE</a>
  <nav class="nav__links" aria-label="Primary">
    ${topLinks}
  </nav>
  <div class="nav__tools">
    ${['<span data-slot="search"></span>', rainSlot, `<a class="only-wide ext" href="${MODDB}">ModDB</a>`, `<a class="only-wide ext" href="${REPO}">GitHub</a>`].filter(Boolean).join('\n    ')}
    <details class="nav__menu"><summary>menu</summary>
      <nav class="nav__sheet" aria-label="All pages">${rainSlot}${groups('sheet')}<a class="ext" href="${MODDB}">Download on ModDB</a><a class="ext" href="${REPO}">GitHub</a></nav>
    </details>
  </div>
</div></header>`;

  let main;
  if (page.layout === 'home') {
    main = `<main id="main"><div class="home">\n${page.html}\n</div></main>`;
  } else {
    const at = PAGES.findIndex((p) => p.slug === page.slug);
    const prev = PAGES[at - 1], next = PAGES[at + 1];
    const toc = page.toc?.length >= 3 ? tocList(page.toc) : '';
    const classes = ['article', 'prose', page.attrs?.class].filter(Boolean).join(' ');
    const own = { ...(page.source?.length ? { 'data-source': page.source.join(' ') } : {}), ...page.attrs };
    const attrs = Object.entries(own).filter(([k]) => k !== 'class').map(([k, v]) => (v === '' ? ` ${k}` : ` ${k}="${esc(v)}"`)).join('');
    main = `<main>
<div class="shell">
  <nav class="side" aria-label="Documentation">${groups('side')}</nav>
  <article class="${classes}" id="main"${attrs}>
    <nav class="crumb" aria-label="Breadcrumb"><ol><li><a href="${url('')}">pulse</a></li><li aria-current="page">${esc(page.slug)}</li></ol></nav>
${toc ? `    <details class="acc toc-inline"><summary><span>On this page</span></summary><div class="acc__body">${toc}</div></details>\n` : ''}${page.html}
    <nav class="pager" aria-label="Previous and next page">${prev ? `<a class="pager__prev" href="${url(prev.slug)}">${esc(prev.label)}</a>` : ''}${next ? `<a class="pager__next" href="${url(next.slug)}">${esc(next.label)}</a>` : ''}</nav>
${page.source?.length ? `    <p class="source">Source: ${page.source.map((f) => `<a class="ext" href="${REPO}/blob/main/${f}">${esc(f)}</a>`).join(', ')}</p>\n` : ''}  </article>
${toc ? `  <nav class="toc" aria-label="On this page"><h2>On this page</h2>${toc}</nav>\n` : ''}</div>
</main>`;
  }

  const footer = `<footer class="footer">
  <nav class="footer__links" aria-label="Project links"><ul>
    <li><a href="${REPO}" aria-label="source on GitHub">source_on_github</a></li>
    <li><a href="${url('getting-started')}" aria-label="getting started">getting_started</a></li>
    <li><a href="${url('changelog')}" aria-label="changelog">changelog</a></li>
    <li><a href="${REPO}/issues" aria-label="issues and ideas">issues_and_ideas</a></li>
    <li><a href="${DISCORD}" aria-label="Stratum Discord">stratum_discord</a></li>
    <li><a href="${SUPPORT}" aria-label="support Stratum">support_stratum</a></li>
  </ul></nav>
  <p class="footer__meta">Pulse v${esc(site.version)} &middot; MIT licensed &middot; ${esc(site.notice)}${rainSlot ? ' ' + rainSlot : ''}</p>
  <p class="footer__spoon">There is no spoon. Only metrics.</p>
</footer>`;

  // A <base> turns every "#fragment" into a navigation to the site root, so 404.html has no skip link.
  return `<!doctype html>
<html lang="en" data-root="${from ? '../' : ''}" data-page="${page.slug || 'home'}" data-regime="${info.regime}">
<head>
${head}
</head>
<body>
${info.base ? '' : '<a class="skip" href="#main">Skip to content</a>\n'}${SPRITE}
${nav}
${main}
${footer}
</body>
</html>
`;
}
