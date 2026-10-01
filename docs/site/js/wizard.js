// Getting started wizard. The built page is the whole guide in order; this module folds it around the
// reader's route and adds the controls. Nothing of the guide is ever hidden: what is not on the route
// is folded and says so. Choices live in the address, progress in one localStorage key,
// and nothing typed or pasted is ever kept. If anything throws while it starts, the plain guide comes
// back: the page is complete without this file.
//
// Motion: none at rest. A tick draws one heartbeat on the trace, once, for 600 ms at most, and not at
// all under prefers-reduced-motion. A choice, a tick or a paste never moves the focus; "Continue" does.
import { DIMS, parseState, normalise, route, countedSteps, mergeQuery } from './wizard-state.js';
import { fromHtml } from './dom.js';
import { mountHelper } from './helper.js';
import { setCopyText } from './copy.js';

// Every string the wizard adds, in one place, so it can be read and corrected in one sitting.
// (The build writes the others: the act labels, the run labels, the tags of "What next", the download lines.)
const TEXT = {
  progress: 'Progress', prompt: '> ',
  noRoute: 'no route yet', notStarted: 'not started', choose: 'choose where your server runs', nextStep: 'go to your next step', whatNext: 'what next',
  path: (letter) => ' Path ' + letter,
  count: (done, total) => done + ' of ' + total + ' steps done',
  sentence: (done, total) => done + ' of ' + total + ' steps done.',
  actCount: (done, total) => done + ' of ' + total + ' done',
  wholeGuide: 'whole guide', wholeGuideLabel: 'Show the whole guide', startOver: 'start over',
  osLegend: 'What does that PC run?', os: { windows: 'Windows', macos: 'macOS', linux: 'Linux' },
  pillLegend: 'Choose your pill', pill: { blue: '[blue pill] pull', red: '[red pill] push' },
  dockerLegend: 'Which Docker did you install?', docker: { engine: 'Docker Engine (the usual choice)', desktop: 'Docker Desktop for Linux' },
  offRoute: 'not on your route', notOnRoute: 'Not on your route. ', done: 'done',
  later: 'for later, not counted', optional: 'optional on your route',
  notWorking: 'Not working? ', or: ' Or: ', beforeYouStart: 'Before you start', back: 'Back to ', tunnel: 'SSH tunnel command, for a remote server',
  continueTo: 'Continue to ', markDone: 'Mark done: ', stepOf: (label, n) => label + ', step ' + n,
  said: {
    route: (titles, n) => 'Your route: ' + titles.join(', then ') + '. ' + n + ' steps.',
    oneMore: 'One more answer needed.',
    blueOff: 'Path A is not available for this hosting situation: ',
    ticked: (what, summary) => (what + ' done. ' + summary).trim(),
    unticked: (what, summary) => (what + ' is not done any more. ' + summary).trim(),
    pulse: 'Your server has a pulse.', pulsePlain: 'Pulse confirmed it is up. ',
    spoon: 'There is no spoon. Only metrics.', allDone: (n) => 'All ' + n + ' steps done: your first dashboard is up.',
    startedOver: 'Started over: choices and checkmarks cleared.',
  },
};
const KEY = 'pulse.gs';
const SVG = 'http://www.w3.org/2000/svg';
// the trace: one slot per counted step, flat while it is open, one heartbeat once it is ticked
const SLOT = 107.8, FLAT = 'l107.8 0', BEAT = 'l27.4 0l8.5 -14.7l10.3 29.4l10.2 -52.5l12 60.9l8.6 -23.1l30.8 0';

export function initWizard() {
  const gs = document.querySelector('[data-gs]');
  if (!gs) return;
  try { start(gs); } catch (e) {
    document.documentElement.classList.remove('js');
    for (const n of gs.querySelectorAll('[hidden]')) if (n.tagName !== 'TEMPLATE') n.hidden = false;
    throw e;
  }
}

function start(gs) {
  const $ = (sel, root) => (root || gs).querySelector(sel);
  const $$ = (sel, root) => Array.from((root || gs).querySelectorAll(sel));
  const reduced = matchMedia('(prefers-reduced-motion: reduce)');

  // ---------------------------------------------------------------- state: clicks only, never keystrokes
  const SIG = gs.dataset.gsSig;
  const LISTS = {}, LATER = $$('[data-later]').map((li) => li.id), STEPS = [];
  for (const ol of $$('ol[data-list]')) { LISTS[ol.dataset.list] = ol.children.length; for (const li of ol.children) STEPS.push(li.id); }
  let stored = {};
  try { stored = JSON.parse(localStorage.getItem(KEY)) || {}; } catch (e) { /* no storage: progress lasts as long as the page */ }
  if (typeof stored !== 'object' || stored.sig !== SIG) stored = {};      // step ids are positions: a record means something only for the same shape of lists
  const done = new Set((Array.isArray(stored.done) ? stored.done : []).filter((id) => STEPS.includes(id)));
  // a link that names the hosting situation is a whole route and wins; any other query is laid over the stored route
  const named = DIMS.where.includes(new URLSearchParams(location.search).get('where'));
  let s = parseState(location.search, parseState('?' + (typeof stored.route === 'string' ? stored.route : '')));
  function save() {
    const routeText = mergeQuery('', { ...s, view: undefined }).slice(1);      // the whole-guide view says what is open, and what is open is never stored
    try {
      if (!routeText && !done.size) localStorage.removeItem(KEY);       // nothing to remember: no record at all
      else localStorage.setItem(KEY, JSON.stringify({ sig: SIG, route: routeText, done: Array.from(done) }));
    } catch (e) { /* see above */ }
  }
  function replace(url) { try { history.replaceState(null, '', url); } catch (e) { /* a sandboxed frame: the page still works */ } }

  // ---------------------------------------------------------------- the strip under the title
  const vitals = fromHtml('<div class="vitals" role="group">' +
    '<div class="vitals__row"><span class="vitals__path"></span><span class="vitals__count"></span></div>' +
    '<div class="vitals__trace" aria-hidden="true"><svg preserveAspectRatio="none"><path class="vitals__line"/></svg><span class="vitals__dot"></span></div>' +
    '<a class="btn btn--sm btn--primary" data-next></a><p class="vitals__status" role="status"></p></div>');
  vitals.setAttribute('aria-label', TEXT.progress);
  const tools = fromHtml('<div class="vitals-tools"><button type="button" class="chip" data-view aria-pressed="false"></button>' +
    '<button type="button" class="btn btn--sm btn--quiet" data-reset></button></div>');
  $('[data-view]', tools).textContent = TEXT.wholeGuide;
  $('[data-view]', tools).setAttribute('aria-label', TEXT.wholeGuideLabel);
  $('[data-reset]', tools).textContent = TEXT.startOver;
  $('h1').after(vitals, tools);
  const say = (plain, flavour) => {               // the flavour is decoration; the plain sentence is what is announced
    const p = $('.vitals__status');
    p.textContent = '';
    if (flavour) { const b = document.createElement('b'); b.setAttribute('aria-hidden', 'true'); b.textContent = TEXT.prompt + flavour + ' '; p.appendChild(b); }
    p.appendChild(document.createTextNode(plain));
  };

  // ---------------------------------------------------------------- folds: acts, and the two Docker variants
  function makeFold(sec) {                         // heading > button, the rest of the section in a body the button controls
    const h = sec.querySelector('h2, h3'), body = fromHtml('<div class="fold__body"></div>');
    body.id = 'body-' + h.id;
    while (h.nextSibling) body.appendChild(h.nextSibling);
    sec.appendChild(body);
    const btn = fromHtml('<button type="button" class="fold__toggle"><span class="fold__title"></span><span class="sr-only" hidden>, </span><span class="fold__state"></span></button>');
    btn.setAttribute('aria-controls', body.id);
    while (h.firstChild) btn.firstChild.appendChild(h.firstChild);
    h.classList.add('fold__h');
    h.appendChild(btn);
    sec.classList.add('fold');
    btn.addEventListener('click', () => open(sec, !sec.classList.contains('is-open')));
    return sec;
  }
  function open(sec, yes) {
    sec.classList.toggle('is-open', yes);
    $('.fold__toggle', sec).setAttribute('aria-expanded', String(yes));
    sec.querySelector(':scope > .fold__body').hidden = !yes;
  }
  const title = (sec) => $('.fold__title', sec).textContent;
  const state = (sec, text) => {                   // the state is part of the button's name, after a spoken comma that is only there when there is a state
    const btn = sec.querySelector(':scope > .fold__h .fold__toggle');
    btn.querySelector('.fold__state').textContent = text;
    btn.querySelector('.sr-only').hidden = !text;
  };
  const acts = $$('.act').map(makeFold);
  const variants = $$('[data-docker]').map(makeFold);
  const pathAct = { a: $('.act[data-path="a"]'), b: $('.act[data-path="b"]') };
  for (const sec of acts) {
    const next = fromHtml('<p class="act__next"><button type="button" class="btn btn--primary"></button></p>');
    next.firstChild.addEventListener('click', () => { const to = after(sec); open(sec, false); go(to); });
    sec.querySelector(':scope > .fold__body').appendChild(next);
  }
  const onRoute = () => acts.filter((a) => !a.hasAttribute('data-off'));
  const after = (sec) => { const v = onRoute(); return v[v.indexOf(sec) + 1]; };
  function go(sec) {                               // the one control that moves the focus
    open(sec, true);
    replace(location.pathname + location.search + '#' + sec.querySelector('h2').id);
    sec.scrollIntoView();
    $('.fold__toggle', sec).focus({ preventScroll: true });
  }

  // ---------------------------------------------------------------- act 1: the guide's four bullets become four radio cards
  const hostList = $('[data-list="hosts"]'), chooser = hostList.closest('.act');
  const hostBox = fromHtml('<fieldset class="choice"><legend class="sr-only"></legend></fieldset>');
  hostBox.firstChild.textContent = title(chooser);
  hostList.before(hostBox);
  hostBox.appendChild(hostList);
  hostList.classList.add('cards');
  $$('li', hostList).forEach((li, i) => {
    const card = fromHtml('<label class="card"><input class="sr-only" type="radio" name="where"><span class="card__title"></span><span class="card__body"></span></label>');
    const [input, t, b] = card.children;
    input.value = li.dataset.where;
    t.id = 'where-t' + i; b.id = 'where-b' + i;
    input.setAttribute('aria-labelledby', t.id);
    input.setAttribute('aria-describedby', b.id);
    t.appendChild($('strong', li));
    while (li.firstChild) b.appendChild(li.firstChild);
    li.appendChild(card);
  });
  const radios = (name, legend, options) => {
    const box = fromHtml('<fieldset class="choice choice--ask"><legend></legend><div class="radios"></div></fieldset>');
    box.firstChild.textContent = legend;
    for (const [value, text] of Object.entries(options)) {
      const lab = fromHtml('<label class="radio"><input class="sr-only" type="radio"></label>');
      lab.firstChild.name = name; lab.firstChild.value = value;
      lab.appendChild(document.createTextNode(text));
      box.lastChild.appendChild(lab);
    }
    return box;
  };
  const osBox = radios('os', TEXT.osLegend, TEXT.os);
  const pillBox = fromHtml('<fieldset class="choice choice--ask"><legend></legend><div class="pills"></div></fieldset>');
  pillBox.firstChild.textContent = TEXT.pillLegend;
  for (const [colour, p] of [['blue', 'a'], ['red', 'b']]) {
    const card = fromHtml('<label class="pill-card"><input class="sr-only" type="radio" name="pill"><span class="pill-card__title">' +
      '<span class="pill-card__glyph" aria-hidden="true"></span><span class="pill-card__state"></span></span>' +
      '<span class="pill-card__desc"><span class="pill-card__sub"></span><span class="pill-card__lede"></span><span class="pill-card__why" hidden></span></span></label>');
    if (colour === 'red') card.classList.add('pill-card--push');
    card.firstChild.value = colour;
    $('.pill-card__state', card).id = 'pill-' + colour + '-t';
    $('.pill-card__desc', card).id = 'pill-' + colour;
    card.firstChild.setAttribute('aria-labelledby', 'pill-' + colour + '-t');
    card.firstChild.setAttribute('aria-describedby', 'pill-' + colour);
    $('.pill-card__state', card).textContent = TEXT.pill[colour];
    $('.pill-card__sub', card).textContent = title(pathAct[p]);
    $('.pill-card__lede', card).textContent = pathAct[p].dataset.lede;
    pillBox.lastChild.appendChild(card);
  }
  hostBox.after(osBox, pillBox);
  const blue = $('input[value="blue"]', pillBox), why = $('.pill-card__why', blue.parentNode);
  const dockerBox = radios('docker', TEXT.dockerLegend, TEXT.docker);
  variants[0].before(dockerBox);

  // ---------------------------------------------------------------- troubleshooting: each entry a disclosure under its own bold symptom
  const troubleItems = $$('[data-list="trouble"] > li');
  troubleItems[0].parentNode.classList.add('trouble__list');
  for (const li of troubleItems) {
    const d = fromHtml('<details class="acc"><summary><span></span></summary><div class="acc__body"></div></details>');
    d.firstChild.firstChild.appendChild($('strong', li));
    while (li.firstChild) d.lastChild.appendChild(li.firstChild);
    li.appendChild(d);
  }

  // ---------------------------------------------------------------- numbered lists become checkable steps
  for (const ol of $$('ol[data-list]')) {
    ol.classList.add('steps');
    Array.from(ol.children).forEach((li, i) => {
      li.classList.add('step');
      const label = TEXT.stepOf(ol.dataset.label, i + 1);
      const box = fromHtml('<label class="step__check"><input type="checkbox"><span class="sr-only"></span></label>');
      box.lastChild.textContent = TEXT.markDone + label;
      box.firstChild.checked = done.has(li.id);
      box.firstChild.addEventListener('change', (e) => {
        if (e.target.checked) done.add(li.id); else done.delete(li.id);
        const p = render();
        save();
        if (!e.target.checked) say(TEXT.said.unticked(label, p.summary));
        else if (p.total && p.done === p.total) say(TEXT.said.allDone(p.total), TEXT.said.spoon);
        else if (li.id === 'install-4') say(TEXT.said.pulsePlain + p.summary, TEXT.said.pulse);
        else say(TEXT.said.ticked(label, p.summary));
        if (e.target.checked) beat(p.req.indexOf(li.id));
      });
      const tag = fromHtml('<span class="step__tag"></span>');       // `done`, or what this step is on this route: real text, so it can be found, read and copied
      li.prepend(box, tag);
      const entries = (li.dataset.trouble || '').split(' ').filter(Boolean);
      if (!entries.length) return;
      const help = fromHtml('<p class="step__help"></p>');
      help.textContent = TEXT.notWorking;
      entries.forEach((n, k) => {
        const a = document.createElement('a');
        a.href = '#trouble-' + n;
        a.textContent = $('#trouble-' + n + ' summary').textContent.trim();
        a.addEventListener('click', () => backLink(n, li.id, label));
        if (k) help.appendChild(document.createTextNode(TEXT.or));
        help.appendChild(a);
      });
      if (li.dataset.before) {                                  // a command that fails outright: the guide's own list of what it needs comes first
        const a = document.createElement('a');
        a.href = '#' + li.dataset.before;
        a.textContent = TEXT.beforeYouStart;
        help.append(TEXT.or, a);
      }
      li.appendChild(help);
    });
  }
  function backLink(n, id, label) {
    const body = $('#trouble-' + n + ' .acc__body'), old = $('.trouble__back', body);
    if (old) old.remove();
    const p = fromHtml('<p class="trouble__back"><a></a></p>');
    p.firstChild.href = '#' + id;
    p.firstChild.textContent = TEXT.back + label;
    body.appendChild(p);
  }

  // ---------------------------------------------------------------- the SSH tunnel: the command and the paragraph after it, one disclosure
  const tunnelCode = $('[data-tunnel]');
  const tunnelBox = fromHtml('<details class="acc"><summary><span></span></summary><div class="acc__body"></div></details>');
  tunnelBox.firstChild.firstChild.textContent = TEXT.tunnel;
  const tunnelParts = [tunnelCode, tunnelCode.nextElementSibling];
  tunnelCode.before(tunnelBox);
  for (const n of tunnelParts) tunnelBox.lastChild.appendChild(n);

  mountHelper(gs, (text) => say(text), setCopyText);

  // ---------------------------------------------------------------- the beat: one heartbeat on the trace when a step is ticked, once, 600 ms
  function beat(index) {
    if (index < 0 || reduced.matches) return;
    const svg = $('.vitals__trace svg');
    for (const old of $$('.vitals__beat', svg)) old.remove();
    const p = document.createElementNS(SVG, 'path');
    p.setAttribute('class', 'vitals__beat');
    p.setAttribute('d', 'M' + (index * SLOT).toFixed(1) + ' 45' + BEAT);
    p.addEventListener('animationend', () => p.remove());
    svg.appendChild(p);
  }

  // ---------------------------------------------------------------- render: one function, called after every change
  const stepEl = (id) => document.getElementById(id);
  function render() {
    const r = route(s), req = countedSteps(r, LISTS, LATER), nDone = req.filter((id) => done.has(id)).length;
    const summary = req.length ? TEXT.sentence(nDone, req.length) : '';

    for (const i of $$('input[type="radio"]')) i.checked = (i.name === 'pill' ? r.pill : i.name === 'docker' ? r.docker : s[i.name]) === i.value;
    osBox.hidden = !r.askOs;
    dockerBox.hidden = !r.askDocker;
    blue.setAttribute('aria-disabled', String(r.noBlue));
    blue.parentNode.classList.toggle('is-off', r.noBlue);
    why.hidden = !r.noBlue;
    if (r.noBlue && why.dataset.for !== s.where) {
      why.textContent = '';
      why.appendChild(document.createElement('b')).textContent = TEXT.notOnRoute;
      why.appendChild($('template[data-why="' + s.where + '"]').content.cloneNode(true));
      why.dataset.for = s.where;
    }

    for (const p of ['a', 'b']) pathAct[p].toggleAttribute('data-off', !!r.path && r.path !== p);
    for (const v of variants) {
      const off = r.path === 'a' && !!r.docker && v.dataset.docker !== r.docker;
      v.toggleAttribute('data-off', off);
      state(v, off ? TEXT.offRoute : '');
    }

    const current = req.find((id) => !done.has(id));
    for (const li of $$('.step')) {
      li.classList.toggle('is-done', done.has(li.id));
      if (li.id === current) li.setAttribute('aria-current', 'step'); else li.removeAttribute('aria-current');
      $(':scope > .step__tag', li).textContent = done.has(li.id) ? TEXT.done : li.hasAttribute('data-later') ? TEXT.later : li.id === 'install-5' && r.noShell ? TEXT.optional : '';
    }

    const count = (sec) => { const mine = req.filter((id) => sec.contains(stepEl(id))); return mine.length ? TEXT.actCount(mine.filter((id) => done.has(id)).length, mine.length) : ''; };
    for (const sec of acts) {
      const kind = sec.dataset.act, off = sec.hasAttribute('data-off'), to = after(sec), next = $('.act__next', sec);
      state(sec, off ? TEXT.offRoute : kind === 'choose' ? (r.chosen ? TEXT.done : TEXT.choose) : count(sec));
      next.hidden = off || !to || r.all || !r.chosen;
      if (to) next.firstChild.textContent = TEXT.continueTo + title(to);
    }

    const badge = $('.vitals__path');
    badge.textContent = '';
    if (r.path) {
      const b = badge.appendChild(fromHtml('<span class="badge"></span>'));
      b.classList.add(r.path === 'a' ? 'badge--pull' : 'badge--push');
      b.textContent = TEXT.pill[r.pill];
      badge.appendChild(document.createTextNode(TEXT.path(r.path.toUpperCase())));
    } else badge.textContent = TEXT.noRoute;
    $('.vitals__count').textContent = req.length ? TEXT.count(nDone, req.length) : r.chosen ? '' : TEXT.notStarted;
    const flags = req.length ? req.map((id) => done.has(id)) : [false];
    $('.vitals__trace svg').setAttribute('viewBox', '0 0 ' + (flags.length * SLOT).toFixed(1) + ' 76');
    $('.vitals__line').setAttribute('d', 'M0 45' + flags.map((on) => (on ? BEAT : FLAT)).join(''));
    $('.vitals__dot').style.left = (100 * (current ? req.indexOf(current) : req.length ? flags.length : 0) / flags.length) + '%';
    $('[data-view]', tools).setAttribute('aria-pressed', String(r.all));
    gs.toggleAttribute('data-all', r.all);

    const nextBtn = $('[data-next]', vitals);      // one plain link: where to go now
    let target = 'what-next';
    if (!r.chosen) target = $('h2', acts[0]).id;
    else if (current) {
      const act = stepEl(current).closest('.act');  // a fresh act is entered by its heading, so its introduction is read first
      target = req.some((id) => done.has(id) && act.contains(stepEl(id))) ? current : $('h2', act).id;
    }
    nextBtn.textContent = !r.chosen ? TEXT.choose : current ? TEXT.nextStep : TEXT.whatNext;
    nextBtn.href = '#' + target;
    $('[data-reset]', tools).hidden = !Object.keys(s).some((k) => k !== 'view') && !done.size;       // the whole-guide view is a way of reading, there is nothing to start over

    replace(location.pathname + mergeQuery(location.search, s) + location.hash);
    return { done: nDone, total: req.length, summary, req };
  }

  // what is open: on load, and whenever the route or the view changes (never on a tick)
  function unfold() {
    const r = route(s), req = countedSteps(r, LISTS, LATER), todo = req.find((id) => !done.has(id));
    const first = !r.chosen ? acts[0] : todo ? stepEl(todo).closest('.act') : acts.find((a) => a.dataset.act === 'next');
    for (const a of acts) open(a, r.all || a === first);
    unfoldVariants();
    for (const d of $$('.trouble__list details')) d.open = r.all;
    if (r.all) for (const d of $$('.act details, .trouble details')) d.open = true;       // not the contents list at the top
  }
  function unfoldVariants() {
    const r = route(s);
    for (const v of variants) open(v, r.all || !v.hasAttribute('data-off'));
    tunnelBox.open = r.all || r.tunnel;
  }
  function reveal(id, always) {                    // a link into the page always lands on visible text
    const target = id && document.getElementById(id);
    if (!target || !gs.contains(target)) return;
    let moved = false;
    for (let n = target; n && n !== gs; n = n.parentElement) {
      if (n.classList.contains('fold') && !n.classList.contains('is-open')) { open(n, true); moved = true; }
      if (n.tagName === 'DETAILS' && !n.open) { n.open = true; moved = true; }
    }
    const own = target.matches('[data-list="trouble"] > li') ? $('details', target) : null;
    if (own && !own.open) { own.open = true; moved = true; }
    if (always || moved) target.scrollIntoView();
  }

  // ---------------------------------------------------------------- wiring
  gs.addEventListener('change', (e) => {
    const t = e.target;
    if (t.type !== 'radio' || !DIMS[t.name]) return;
    if (t === blue && route(s).noBlue) { render(); say(TEXT.said.blueOff + why.textContent); return; }
    s = normalise({ ...s, [t.name]: t.value });
    render(); save(); unfoldVariants();
    const r = route(s);
    if (r.askOs && !s.os) osBox.scrollIntoView({ block: 'nearest', behavior: 'auto' });         // the question a home PC raises can be below the fold of a phone
    say(r.chosen ? TEXT.said.route(onRoute().slice(1, 3).map(title), countedSteps(r, LISTS, LATER).length) : TEXT.said.oneMore);
  });
  $('[data-view]', tools).addEventListener('click', () => { s = normalise({ ...s, view: s.view === 'all' ? undefined : 'all' }); render(); save(); unfold(); });
  $('[data-reset]', tools).addEventListener('click', () => {
    s = {}; done.clear();
    for (const i of $$('.step__check input')) i.checked = false;
    render(); save(); unfold(); say(TEXT.said.startedOver);
  });
  $('[data-next]', vitals).addEventListener('click', (e) => reveal(e.currentTarget.getAttribute('href').slice(1), false));
  addEventListener('hashchange', () => reveal(location.hash.slice(1), false));
  document.addEventListener('click', (e) => {      // the same link twice is no hashchange: it still has to open what the reader closed in between
    const a = e.target.closest ? e.target.closest('a[href^="#"]') : null;
    if (a && a.hash === location.hash) reveal(a.hash.slice(1), false);
  });

  render();
  unfold();
  gs.classList.add('is-ready');
  if (named) save();                                // a link that names the hosting situation is remembered; a partial link never writes
  reveal(location.hash.slice(1), true);
}
