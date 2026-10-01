// Dashboard and Alerts: each kit's README as written, then a browser built from the kit's own file.
// The panel browser reads pulse.json, the rule browser reads pulse-alerts.yml, both through ctx.data:
// nothing a card says is written by hand, and a card is keyed by panel id or by (alert, severity),
// never by a title, since four panel titles and two alert names repeat.
import { OVERVIEW } from '../../content/home.mjs';
import { anchorLink } from '../md.mjs';
import { plain } from '../sources.mjs';
import { codeBlock, metricRef, plural } from './metrics.mjs';

const IMPORTING = 'Importing it into a Grafana you already run';

export default function kits(ctx) {
  const { slice, render, data, assetUrl, esc } = ctx;

  const reads = (cls, from, names) => (names.length ? `<p class="${cls}">Reads: ${names.map((n) => metricRef(ctx, from, n)).join(', ')}</p>` : '');
  const download = (from, file) => `<p class="dl"><a class="btn btn--sm" href="${esc(assetUrl(from, `files/${file}`))}" download>download ${esc(file)}</a></p>`;

  // ---------------------------------------------------------------- Dashboard
  function dashboard() {
    const FILE = 'contrib/grafana/README.md';
    const tokens = slice(FILE);
    const intro = tokens.find((t) => t.type === 'paragraph');
    const importing = tokens.findIndex((t) => t.type === 'heading' && plain(t) === IMPORTING);
    const importIntro = tokens.slice(importing + 1).find((t) => t.type === 'paragraph');
    if (!intro || importing < 0 || !importIntro) throw new Error(`${FILE}: the first paragraph or the section "${IMPORTING}" is gone: the Dashboard page puts a picture and a download line there`);
    const src = esc(assetUrl('dashboard', OVERVIEW.src));
    intro.after = `<figure class="shot dash-shot"><a href="${src}"><img src="${src}" alt="${esc(OVERVIEW.alt)}" width="${OVERVIEW.width}" height="${OVERVIEW.height}" loading="lazy" decoding="async"></a><figcaption>${OVERVIEW.caption()}</figcaption></figure>\n`;
    importIntro.after = download('dashboard', 'pulse-overview-shared.json');
    const r = render(tokens, { file: FILE, page: 'dashboard' });

    const { rows } = data.dashboard;
    const panels = rows.flatMap((row) => row.panels);
    const card = (p) => {
      const names = [...new Set(p.targets.flatMap((t) => t.metrics))];
      return `<article class="dash-panel" id="${esc(p.anchor)}" data-item>
<h4 class="dash-panel__title">${esc(p.title)} <span class="badge badge--muted">${esc(p.type)}</span></h4>
${p.description ? `<p class="dash-panel__desc">${esc(p.description)}</p>\n` : ''}${p.targets.filter((t) => t.expr).map((t) => codeBlock(ctx, 'promql', t.legend ? `${t.refId}: ${t.legend}` : t.refId, t.expr)).join('\n')}
${reads('dash-panel__reads', 'dashboard', names)}</article>`;
    };
    const section = `<section class="dash" id="panels" data-filter="">
<h2>The panels${anchorLink('panels')}</h2>
<p>${plural(panels.length, 'panel', 'panels')} in ${plural(rows.length, 'row', 'rows')}, read from the dashboard file.</p>
<div data-slot="filter"></div>
${rows.map((row) => `<section class="dash-row" id="${row.id}"><h3>${esc(row.title)}${anchorLink(row.id)}</h3>\n${row.panels.map(card).join('\n')}\n</section>`).join('\n')}
</section>
`;
    const text = (p) => [p.description, ...p.targets.map((t) => t.expr)].filter(Boolean).join(' ');
    return { slug: 'dashboard', title: 'Dashboard', layout: 'docs', source: [FILE, 'contrib/grafana/provisioning/dashboards/json/pulse.json'],
      html: r.html + section,
      toc: [...r.toc, { id: 'panels', text: 'The panels', level: 2 }, ...rows.map((row) => ({ id: row.id, text: row.title, level: 3 }))],
      search: [...r.search, { id: 'panels', title: 'The panels', text: `${plural(panels.length, 'panel', 'panels')} in ${plural(rows.length, 'row', 'rows')}, read from the dashboard file.` },
        ...rows.flatMap((row) => [{ id: row.id, title: row.title, text: '' }, ...row.panels.map((p) => ({ id: p.anchor, title: `${row.title}: ${p.title}`, text: text(p) }))])] };
  }

  // ---------------------------------------------------------------- Alerts
  function alerts() {
    const FILE = 'contrib/alerts/README.md';
    const r = render(slice(FILE), { file: FILE, page: 'alerts' });
    const groups = [...new Set(data.rules.map((rule) => rule.group))];
    const card = (rule) => `<article class="rule" id="${esc(rule.id)}" data-item data-severity="${esc(rule.severity)}">
<h4 class="rule__title"><code>${esc(rule.alert)}</code> ${rule.severity === 'critical' ? '<span class="badge badge--crit">CRITICAL</span>' : '<span class="badge badge--warn">warning</span>'}${rule.for ? ` <span class="badge badge--muted">for ${esc(rule.for)}</span>` : ''}</h4>
<p class="rule__summary">${esc(rule.summary)}</p>
${codeBlock(ctx, 'promql', 'expr', rule.expr)}
<p class="rule__desc">${esc(rule.description)}</p>
${reads('rule__reads', 'alerts', rule.metrics)}${rule.comment ? `<details class="acc"><summary><span>Why this rule, from the comments of the file</span></summary><div class="acc__body"><pre class="rule__why">${esc(rule.comment)}</pre></div></details>\n` : ''}</article>`;
    const section = `<section class="rules" id="rules" data-filter="severity">
<h2>The rules${anchorLink('rules')}</h2>
${download('alerts', 'pulse-alerts.yml')}
<div data-slot="filter"></div>
${groups.map((g) => `<section class="rule-group"><h3 id="group-${esc(g)}">${esc(g)}${anchorLink(`group-${g}`)}</h3>\n${data.rules.filter((rule) => rule.group === g).map(card).join('\n')}\n</section>`).join('\n')}
</section>
`;
    return { slug: 'alerts', title: 'Alerts', layout: 'docs', source: [FILE, 'contrib/alerts/pulse-alerts.yml'],
      html: r.html + section,
      toc: [...r.toc, { id: 'rules', text: 'The rules', level: 2 }, ...groups.map((g) => ({ id: `group-${g}`, text: g, level: 3 }))],
      search: [...r.search, { id: 'rules', title: 'The rules', text: '' },
        ...groups.flatMap((g) => [{ id: `group-${g}`, title: g, text: '' }, ...data.rules.filter((rule) => rule.group === g).map((rule) => ({ id: rule.id, title: `${rule.alert} (${rule.severity})`, text: `${rule.summary} ${rule.description} ${rule.expr}` }))])] };
  }

  return [dashboard(), alerts()];
}
