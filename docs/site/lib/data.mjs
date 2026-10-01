// What the page modules need to know that is not prose: the released version, the metric families,
// both config tables, the alert rules, the dashboard, and the project's SonarCloud numbers.
// Everything is extracted once, checked against the other documents, and handed over frozen: it
// holds values, never tokens, so no page can leak a hook into another.
import { parseDocument } from 'yaml';
import { CHANGELOG_H2, deepFreeze, flat, plain, slugBase } from './sources.mjs';

// ---------------------------------------------------------------- version (the site documents a release)
/**
 * The site always documents the newest release of the changelog, on every branch. While a
 * development line is open (modinfo.json ahead of the changelog, the normal state of dev) that is a
 * note, not an error. It is an error when the two mods disagree, when there is no dated release at
 * all, or when modinfo.json names a release that is not the newest.
 */
export function resolveVersion({ mod, otlp, headings }) {
  if (mod !== otlp) throw new Error(`Pulse/modinfo.json says ${mod} and Pulse.Otlp/modinfo.json says ${otlp}: the two mods ship together and must carry the same version`);
  const releases = headings.filter((h) => h.level === 2).map((h) => CHANGELOG_H2.exec(h.text)).filter((m) => m?.[2]).map((m) => ({ version: m[1], date: m[2] }));
  if (!releases.length) throw new Error('CHANGELOG.md has no heading of the form "[x.y.z] - yyyy-mm-dd": the site documents a release and there is none');
  const newest = releases[0];
  if (!releases.some((r) => r.version === mod)) {
    return { version: newest.version, released: newest.date, note: `modinfo.json says ${mod}, which has no dated changelog heading yet; building for ${newest.version}` };
  }
  if (mod !== newest.version) throw new Error(`modinfo.json says ${mod}, which is a release, but ${newest.version} is the newest one in CHANGELOG.md`);
  return { version: mod, released: newest.date, note: null };
}

// ---------------------------------------------------------------- metric families (README.md)
const N = '`((?:pulse|dotnet)_[a-z0-9_]+)(?:\\{([a-z_]+)\\})?`';
const BULLET = new RegExp(`^${N}(?: and ${N})?\\s+\\((counter|gauge|histogram)s?\\):\\s+`);
const GROUPS = [['public', /^The metric families it serves:$/], ['engine', /^Six more come from the engine's own accounting/], ['attribution', /^Four families appear once it is on:$/]];

/** Walks the top-level tokens of the README: a paragraph that names a group says which group the list right after it is. */
function parseMetrics(tokens, readmeText) {
  const metrics = [], seenGroups = new Set();
  let bullets = 0, group = null;
  for (const t of tokens) {
    if (t.type === 'space') continue;
    if (t.type === 'paragraph') {
      const g = GROUPS.find(([, re]) => re.test(flat(t.text)));
      group = g ? g[0] : null;
      if (g) seenGroups.add(g[0]);
      continue;
    }
    if (t.type !== 'list' || !group) { group = null; continue; }
    t.items.forEach((item, position) => {
      const m = BULLET.exec(item.text);
      if (!m) throw new Error(`README.md: a bullet of the ${group} metric list does not match the metric rule (lib/data.mjs BULLET): ${item.text.slice(0, 70)}`);
      const names = [[m[1], m[2]], [m[3], m[4]]].filter(([n]) => n);
      bullets++;
      for (const [name, label] of names) {
        if (metrics.some((x) => x.name === name)) throw new Error(`README.md: the metric family ${name} is documented twice`);
        metrics.push({ name, type: m[5], labels: label ? [label] : [], group, item: position, siblings: names.map(([n]) => n) });
      }
    });
    group = null;
  }
  for (const [g] of GROUPS) if (!seenGroups.has(g)) throw new Error(`README.md: the paragraph that introduces the ${g} metric families is gone (GROUPS in lib/data.mjs)`);
  const lines = [...readmeText.matchAll(/^- `(?:pulse|dotnet)_/gm)].length;
  if (lines !== bullets) throw new Error(`README.md: ${lines} lines start with a metric name but ${bullets} sit in one of the three metric lists: a bullet is out of place`);
  return metrics;
}

// ---------------------------------------------------------------- config tables (README.md)
// Exactly these keys carry a constraint, so a reworded cell or a newly limited key stops the build
// instead of quietly losing its limit.
const CONSTRAINTS = [
  ['ModConfig/pulse.json', 'ChunksRefreshSeconds', { min: 1, max: 86400 }],
  ['ModConfig/pulse.json', 'Attribution.BurstTicks', { min: 1, max: 300 }],
  ['ModConfig/pulse.json', 'Attribution.IntervalSeconds', { min: 1 }],
  ['ModConfig/pulse-otlp.json', 'Protocol', { options: ['http/protobuf', 'grpc'] }],
  ['ModConfig/pulse-otlp.json', 'IntervalSeconds', { min: 5, max: 86400 }],
];
const flatten = (o, prefix = '') => Object.entries(o).flatMap(([k, v]) => (v && typeof v === 'object' && Object.keys(v).length ? flatten(v, `${prefix}${k}.`) : [[prefix + k, v]]));

/** `configuration`: tokens of the configuration slice; `printed`: the default files the README prints, as parsed JSON, by file name. */
function parseConfig(configuration, printed) {
  const files = [];
  let label = null;
  for (const t of configuration) {
    if (t.type === 'paragraph') { const m = /^\*\*`(ModConfig\/[a-z-]+\.json)`\*\*$/.exec(t.raw.trim()); if (m) label = m[1]; }
    if (t.type !== 'table' || !label) continue;
    if (t.header.map(plain).join('|') !== 'Key|Default|What it does|Live or restart') throw new Error(`README.md: the header of the ${label} table is no longer Key, Default, What it does, Live or restart`);
    const short = label === 'ModConfig/pulse.json' ? 'pulse' : 'otlp';
    const rows = t.rows.map((row) => {
      const what = row[2].text;
      let def;
      try { def = JSON.parse(row[1].text.replace(/^`|`$/g, '')); } catch { throw new Error(`README.md: the default of ${row[0].text} in the ${label} table is not a JSON literal: ${row[1].text}`); }
      const r = { key: row[0].text.replace(/`/g, ''), default: def, type: Array.isArray(def) ? 'array' : typeof def, live: /^Live/.test(row[3].text), liveText: plain(row[3]), what };
      let m;
      if ((m = /Clamped to (\d+) through (\d+)/.exec(what))) { r.min = +m[1]; r.max = +m[2]; }
      if ((m = /Floored at (\d+)/.exec(what))) r.min = +m[1];
      if ((m = /capped at (?:[a-z ]+\()?(\d+)/.exec(what))) r.max = +m[1];
      if (r.type === 'string' && (m = /^`([^`]+)` or `([^`]+)`\./.exec(what))) r.options = [m[1], m[2]];
      r.id = `cfg-${short}-${r.key.toLowerCase().replace(/\./g, '-')}`;
      return r;
    });
    const want = flatten(printed[label] ?? {}).map(([k, v]) => JSON.stringify([k, v]));
    const got = rows.map((r) => JSON.stringify([r.key, r.default]));
    if (want.join('\n') !== got.join('\n')) throw new Error(`README.md: the defaults of the ${label} table differ from the default file the README prints, key for key and in order`);
    files.push({ file: label, rows });
    label = null;
  }
  if (files.length !== 2) throw new Error(`README.md: expected two config tables labelled **\`ModConfig/<name>.json\`**, found ${files.length}`);
  const found = files.flatMap((f) => f.rows.filter((r) => 'min' in r || 'max' in r || r.options).map((r) => [f.file, r.key, { ...(r.min !== undefined && { min: r.min }), ...(r.max !== undefined && { max: r.max }), ...(r.options && { options: r.options }) }]));
  if (JSON.stringify(found) !== JSON.stringify(CONSTRAINTS)) throw new Error(`README.md: the constraints read from the config tables are ${JSON.stringify(found)}, the site knows ${JSON.stringify(CONSTRAINTS)} (CONSTRAINTS in lib/data.mjs)`);
  return files;
}

// ---------------------------------------------------------------- alert rules (pulse-alerts.yml)
/** Families named by a PromQL expression; a histogram's _bucket, _sum and _count give the family. */
export function metricsIn(expr, histograms) {
  const out = new Set();
  for (const [name] of expr.matchAll(/\b(?:pulse|dotnet)_[a-z0-9_]+\b/g)) {
    const base = name.replace(/_(bucket|sum|count)$/, '');
    out.add(histograms.has(base) ? base : name);
  }
  return [...out];
}

const RULES_FILE = 'contrib/alerts/pulse-alerts.yml';
const NAME = /^[\w.-]+$/;                  // an alert or a group name ends up in an id and in an address

function parseRules(yamlText, histograms) {
  const doc = parseDocument(yamlText);
  if (doc.errors.length) throw new Error(`${RULES_FILE} does not parse: ${doc.errors[0].message}`);
  const rules = [];
  for (const g of doc.get('groups').items) {
    const group = g.get('name');
    if (typeof group !== 'string' || !NAME.test(group)) throw new Error(`${RULES_FILE}: the group name ${JSON.stringify(group)} is not made of letters, digits, dots, dashes and underscores`);
    const seq = g.get('rules');
    seq.items.forEach((node, i) => {
      const r = node.toJSON();
      const keys = Object.keys(r).filter((k) => k !== 'for').join(',');
      if (keys !== 'alert,expr,labels,annotations') throw new Error(`${RULES_FILE}: the rule ${r.alert} has the keys ${Object.keys(r).join(', ')}, expected alert, expr, labels, annotations and maybe for`);
      if (typeof r.alert !== 'string' || !NAME.test(r.alert)) throw new Error(`${RULES_FILE}: the alert name ${JSON.stringify(r.alert)} is not made of letters, digits, dots, dashes and underscores`);
      if (!['warning', 'critical'].includes(r.labels?.severity)) throw new Error(`${RULES_FILE}: the rule ${r.alert} has the severity "${r.labels?.severity}", expected warning or critical`);
      for (const [what, value] of [['an expr', r.expr], ['a summary annotation', r.annotations?.summary], ['a description annotation', r.annotations?.description]]) {
        if (typeof value !== 'string') throw new Error(`${RULES_FILE}: the rule ${r.alert} has no text for ${what}`);
      }
      const id = `rule-${r.alert.toLowerCase()}-${r.labels.severity}`;
      if (rules.some((x) => x.id === id)) throw new Error(`${RULES_FILE}: two rules are called ${r.alert} with the severity ${r.labels.severity}`);
      // a rule with no comment of its own takes the one of the previous rule with the same name
      let comment = ((i === 0 ? seq.commentBefore : node.commentBefore) ?? '').split('\n').map((l) => l.replace(/^ /, '')).join('\n');
      if (!comment) comment = rules.findLast((x) => x.alert === r.alert)?.comment ?? '';
      rules.push({ id, group, alert: r.alert, severity: r.labels.severity, for: r.for ?? null, expr: r.expr,
        summary: r.annotations.summary, description: r.annotations.description, comment, metrics: metricsIn(r.expr, histograms) });
    });
  }
  return rules;
}

// ---------------------------------------------------------------- dashboard (pulse.json)
const DASH_FILE = 'contrib/grafana/.../pulse.json';

function parseDashboard(json, histograms) {
  const dash = JSON.parse(json);
  const rows = [], ids = new Set();
  for (const p of dash.panels) {
    if (p.type === 'row') {
      if (typeof p.title !== 'string') throw new Error(`${DASH_FILE}: a row has no title`);
      if (p.collapsed || p.panels?.length) throw new Error(`${DASH_FILE}: the row "${p.title}" is collapsed or holds panels: the site reads the flat list of panels that follow a row`);
      rows.push({ id: `row-${slugBase(p.title)}`, title: p.title, panels: [] });
      continue;
    }
    if (!Number.isInteger(p.id) || typeof p.title !== 'string' || typeof p.type !== 'string') throw new Error(`${DASH_FILE}: a panel needs an integer id, a title and a type (found id ${JSON.stringify(p.id)}, title ${JSON.stringify(p.title)}, type ${JSON.stringify(p.type)})`);
    if (!rows.length) throw new Error(`${DASH_FILE}: the panel "${p.title}" comes before the first row`);
    if (ids.has(p.id)) throw new Error(`${DASH_FILE}: the panel id ${p.id} is used twice`);
    ids.add(p.id);
    rows.at(-1).panels.push({ id: p.id, anchor: `panel-${p.id}`, row: rows.at(-1).title, title: p.title, type: p.type, unit: p.fieldConfig?.defaults?.unit ?? null, description: p.description ?? '',
      targets: (p.targets ?? []).map((t) => ({ refId: t.refId ?? '', expr: t.expr, legend: t.legendFormat ?? '', metrics: metricsIn(t.expr ?? '', histograms) })) });
  }
  return { title: dash.title, rows };
}

// ---------------------------------------------------------------- the project's own numbers (SonarCloud)
export const SONAR_API = 'https://sonarcloud.io/api/measures/component?component=StratumServer_Pulse&metricKeys=alert_status,coverage,bugs,vulnerabilities';
export const SONAR_PAGE = 'https://sonarcloud.io/summary/overall?id=StratumServer_Pulse';

/** The four numbers, or null when the answer is not what it should be: they end up in the HTML, so nothing else gets in. */
export function parseHealth(body) {
  const by = Object.fromEntries((body?.component?.measures ?? []).map((m) => [m.metric, m.value]));
  const num = (v, max = Infinity) => { const n = /^\d+(\.\d+)?$/.test(v ?? '') ? Number(v) : NaN; return Number.isFinite(n) && n <= max ? n : null; };
  const health = { gate: /^[A-Z_]{2,20}$/.test(by.alert_status ?? '') ? by.alert_status : null, coverage: num(by.coverage, 100), bugs: num(by.bugs), vulnerabilities: num(by.vulnerabilities) };
  return Object.values(health).every((v) => v !== null) ? health : null;
}

/** Asked at build time, no token, 5 seconds at most. Any failure, or offline, gives null: the build never fails on it. */
export async function fetchHealth({ offline = false, fetchFn = fetch, timeoutMs = 5000 } = {}) {
  if (offline) return null;
  try {
    const res = await fetchFn(SONAR_API, { signal: AbortSignal.timeout(timeoutMs) });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const health = parseHealth(await res.json());
    if (!health) throw new Error('an answer that does not hold the four measures');
    return health;
  } catch (e) {
    console.warn(`note: SonarCloud gave no numbers (${e.name === 'TimeoutError' ? `no answer in ${timeoutMs / 1000} s` : e.message}); the vital signs are built as links only`);
    return null;
  }
}

// ---------------------------------------------------------------- everything
/** opts.offline (the --offline flag, or PULSE_SITE_OFFLINE=1) skips the SonarCloud request; opts.fetchFn and opts.timeoutMs are for the tests. */
export async function loadData(sources, opts = {}) {
  const { docs, slice, text } = sources;
  const notes = [];
  const modinfo = JSON.parse(text('Pulse/modinfo.json'));
  const v = resolveVersion({ mod: modinfo.version, otlp: JSON.parse(text('Pulse.Otlp/modinfo.json')).version, headings: docs['CHANGELOG.md'].headings });
  if (v.note) notes.push(v.note);

  const notice = text('NOTICE').split(/\n\s*\n/).map(flat)[1] ?? '';
  if (!notice.startsWith('This product is an independent')) throw new Error('NOTICE: its second paragraph no longer starts with "This product is an independent", the sentence the footer shows');

  const metrics = parseMetrics(docs['README.md'].tokens, text('README.md'));
  const histograms = new Set(metrics.filter((m) => m.type === 'histogram').map((m) => m.name));
  const firstJson = (name) => { const c = slice(name).find((t) => t.type === 'code' && t.lang === 'json'); if (!c) throw new Error(`README.md: no json block in the ${name} section`); return JSON.parse(c.text); };
  const config = parseConfig(slice('configuration'), { 'ModConfig/pulse.json': firstJson('install'), 'ModConfig/pulse-otlp.json': firstJson('otlp') });
  const rules = parseRules(text('contrib/alerts/pulse-alerts.yml'), histograms);
  const dashboard = parseDashboard(text('contrib/grafana/provisioning/dashboards/json/pulse.json'), histograms);
  const panels = dashboard.rows.flatMap((r) => r.panels);

  // Every pulse_ name the kits read must be a documented family. The reverse is only a note.
  const documented = new Set(metrics.map((m) => m.name));
  const used = new Set([...panels.flatMap((p) => p.targets.flatMap((t) => t.metrics)), ...rules.flatMap((r) => r.metrics)].filter((n) => n.startsWith('pulse_')));
  const unknown = [...used].filter((n) => !documented.has(n));
  if (unknown.length) throw new Error(`the dashboard or the alert rules read ${unknown.join(', ')}, which README.md does not document as a metric family`);
  const unused = metrics.filter((m) => !used.has(m.name)).map((m) => m.name);
  if (unused.length) notes.push(`documented but read by no panel and no rule: ${unused.join(', ')}`);

  const data = {
    version: v.version, released: v.released, game: modinfo.dependencies.game,
    description: modinfo.description, notice,
    metrics, metric: (name) => metrics.find((m) => m.name === name),
    usedBy: (name) => {
      const queries = [];
      for (const p of panels) for (const t of p.targets) if (t.metrics.includes(name) && !queries.some((q) => q.expr === t.expr)) queries.push({ expr: t.expr, panel: p, legend: t.legend });
      return { panels: panels.filter((p) => p.targets.some((t) => t.metrics.includes(name))), rules: rules.filter((r) => r.metrics.includes(name)), queries };
    },
    config, rules, dashboard,
    // { gate, coverage, bugs, vulnerabilities } as SonarCloud reports them, or null (offline, no answer): then Home shows links only
    health: await fetchHealth({ offline: opts.offline, fetchFn: opts.fetchFn, timeoutMs: opts.timeoutMs }),
    notes,
  };
  return deepFreeze(data);
}
