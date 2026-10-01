// Metrics, with the explorer. The README's own text (the two metric lists, the paragraphs around them,
// degraded mode, the runtime metrics) under a site-written H1. Every bullet of the three metric lists
// becomes a card in place: the bullet as written, and a closed disclosure that says which dashboard
// panels and alert rules read the family, with the panels' own queries as samples. The attribution list
// lives in the Attribution section of the README; it is rendered here from this module's own copy of
// that section, so the Attribution page stays as the README has it. All of it comes from ctx.data.
import { plainText } from '../layout.mjs';
import { anchorLink, plainOf } from '../md.mjs';
import { flat } from '../sources.mjs';

export const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const BULLET_NAME = /^`((?:pulse|dotnet)_[a-z0-9_]+)/;

/** The link to a metric family wherever a rule or a panel names one, followed by its group badge when it is not a public one.
 *  A dotnet_ name is not explored one by one: it links to the runtime metrics section. */
export function metricRef({ data, pageUrl, esc }, from, name) {
  const m = data.metric(name);
  const href = pageUrl(from, 'metrics', m ? name : 'runtime-metrics');
  return `<a class="metric-ref" href="${esc(href)}"><code>${esc(name)}</code></a>${m && m.group !== 'public' ? ` <span class="badge badge--muted">${esc(m.group)}</span>` : ''}`;
}

/** A PromQL (or YAML, or JSON) block in the shape of every other code block, with its label. */
export const codeBlock = ({ esc }, lang, label, text) => `<div class="code" data-lang="${lang}"><div class="code__bar"><span class="code__label">${esc(label)}</span></div><pre><code>${esc(text)}</code></pre></div>`;

export default function metrics(ctx) {
  const { slice, render, data, pageUrl, esc } = ctx;
  const FILE = 'README.md';
  const opts = { file: FILE, page: 'metrics' };

  // ---- the three metric lists, found in this module's own copies of the README's sections
  const isMetricList = (t) => t.type === 'list' && t.items.every((i) => data.metric(BULLET_NAME.exec(i.text)?.[1]));
  const families = slice('families');
  const lists = families.filter(isMetricList);
  if (lists.length !== 2) throw new Error(`README.md: ${lists.length} metric lists between "The metric families it serves:" and "Degraded mode", expected the public and the engine lists`);
  const engineAt = families.indexOf(lists[1]);
  const lead = families.slice(engineAt + 1).find((t) => t.type !== 'space');
  if (lead?.type !== 'paragraph' || !flat(lead.text).startsWith('Four more answer')) {
    throw new Error('README.md: the paragraph that introduces the attribution families ("Four more answer ...") no longer follows the engine list: the explorer does not know where its third list goes');
  }
  const attribution = slice('attribution');
  const attributionLists = attribution.filter(isMetricList);
  if (attributionLists.length !== 1) throw new Error(`README.md: ${attributionLists.length} metric lists in Attribution, expected the one that follows "Four families appear once it is on:"`);
  const all = [...lists, attributionLists[0]];

  // ---- who uses a family: the union over the names a bullet documents, in the order of the dashboard and the rules
  const panelOrder = new Map(data.dashboard.rows.flatMap((r) => r.panels).map((p, i) => [p.id, i]));
  const ruleOrder = new Map(data.rules.map((r, i) => [r.id, i]));
  const usedBy = (names) => {
    const panels = new Map(), rules = new Map(), queries = [];
    for (const name of names) {
      const u = data.usedBy(name);
      u.panels.forEach((p) => panels.set(p.id, p));
      u.rules.forEach((r) => rules.set(r.id, r));
      u.queries.forEach((q) => { if (!queries.some((x) => x.expr === q.expr)) queries.push(q); });
    }
    return {
      panels: [...panels.values()].sort((a, b) => panelOrder.get(a.id) - panelOrder.get(b.id)),
      rules: [...rules.values()].sort((a, b) => ruleOrder.get(a.id) - ruleOrder.get(b.id)),
      queries: queries.sort((a, b) => panelOrder.get(a.panel.id) - panelOrder.get(b.panel.id)),
    };
  };
  const disclosure = (names) => {
    const { panels, rules, queries } = usedBy(names);
    if (!panels.length && !rules.length) return '';
    const summary = `Used by ${[panels.length && plural(panels.length, 'panel', 'panels'), rules.length && plural(rules.length, 'alert rule', 'alert rules')].filter(Boolean).join(' and ')}`;
    const where = (label, links) => (links.length ? `<p class="metric__where">${label}: ${links.join(', ')}</p>` : '');
    return `<details class="acc metric__use"><summary><span>${summary}</span></summary><div class="acc__body">`
      + where('Panels', panels.map((p) => `<a href="${esc(pageUrl('metrics', 'dashboard', p.anchor))}">${esc(`${p.row}: ${p.title}`)}</a>`))
      + where('Alert rules', rules.map((r) => `<a href="${esc(pageUrl('metrics', 'alerts', r.id))}">${esc(r.alert)} (${r.severity})</a>`))
      + queries.map((q) => codeBlock(ctx, 'promql', q.legend ? `${q.panel.title}: ${q.legend}` : q.panel.title, q.expr)).join('')
      + '</div></details>';
  };

  // ---- one card per bullet: hooks on the copy of the list, search entry from the bullet's own text
  const entries = [];
  for (const list of all) {
    list.attrs = { class: 'metrics' };
    for (const item of list.items) {
      const first = data.metric(BULLET_NAME.exec(item.text)[1]);
      const names = first.siblings;
      item.attrs = { class: 'metric', id: names[0], 'data-item': '', 'data-group': first.group, 'data-type': first.type };
      item.prepend = names.slice(1).map((n) => `<span id="${esc(n)}"></span>`).join('');
      item.append = disclosure(names);
      entries.push({ id: names[0], title: names.join(' and '), text: plainOf(item) });
    }
  }

  // ---- the page: the explorer holds the three lists and the paragraphs that introduce them
  const rowHref = data.dashboard.rows.find((r) => /^row-runtime/.test(r.id))?.id;
  if (!rowHref) throw new Error('the dashboard has no Runtime row: the note of the Metrics page points at it');
  // the filter counts entries (one per bullet), the page and Home count families: a bullet may document two
  const sizes = all.map((list) => list.items.map((item) => data.metric(BULLET_NAME.exec(item.text)[1]).siblings.length)).flat();
  if (sizes.some((n) => n > 2)) throw new Error('README.md: a bullet documents more than two metric families, and the sentence about entries and families in the note of the Metrics page says two (lib/pages/metrics.mjs)');
  const double = sizes.filter((n) => n === 2).length;
  const word = (n) => ['no', 'one', 'two', 'three', 'four', 'five', 'six', 'seven', 'eight', 'nine', 'ten'][n] ?? n;
  const counts = `${sizes.length} entries for ${data.metrics.length} families${double ? `: ${word(double)} ${double === 1 ? 'entry documents' : 'entries document'} two families each` : ''}.`;
  const note = `Queries are the bundled dashboard's own. <code>$__rate_interval</code> is a Grafana variable: in Prometheus itself, write a window of at least four scrape intervals, such as <code>[1m]</code> at 15 seconds. The <code>dotnet_</code> runtime families are described under <a href="${pageUrl('metrics', 'metrics', 'runtime-metrics')}">Runtime metrics</a> below and charted in the dashboard's <a href="${esc(pageUrl('metrics', 'dashboard', rowHref))}">Runtime row</a>. ${counts}`;
  lead.after = render([attributionLists[0]], opts).html;
  const cut = families.indexOf(lead) + 1;
  const inside = render(families.slice(0, cut), opts);
  const rest = render([...families.slice(cut), ...slice('degraded'), ...slice('runtime')], opts);
  const explorer = `<section class="explorer" id="families" data-filter="group type">
<h2>Metric families${anchorLink('families')}</h2>
<p class="explorer__note">${note}</p>
<div data-slot="filter"></div>
${inside.html}</section>
`;
  // the paragraphs of the README that sit under "Metric families" (before and after the section) are its search entry; the cards have their own
  const prose = families.filter((t) => t.type === 'paragraph').map(plainOf).join(' ');
  return [{ slug: 'metrics', title: 'Metrics', description: 'The metric families Pulse serves on its Prometheus endpoint, what degraded mode drops, and the .NET runtime metrics.', layout: 'docs', source: ['README.md'],
    html: `<h1 id="metrics">Metrics</h1>\n${explorer}${rest.html}`,
    toc: [{ id: 'families', text: 'Metric families', level: 2 }, ...rest.toc],
    search: [{ id: 'metrics', title: 'Metrics', text: '' }, { id: 'families', title: 'Metric families', text: `${plainText(note)} ${prose}` }, ...entries, ...rest.search.filter((s) => s.id !== '')] }];
}
