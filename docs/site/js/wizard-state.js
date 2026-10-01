// Getting started wizard: the state rules, pure. No document, no storage, no address: the wizard
// hands in strings and gets plain values back, so the whole routing table is tested in Node.

export const DIMS = { where: ['server', 'home', 'panel', 'container'], os: ['windows', 'macos', 'linux'], pill: ['blue', 'red'], docker: ['engine', 'desktop'], view: ['all'] };

/**
 * Known keys with known values only; anything else in the query is ignored. A query that names a
 * hosting situation (`where`) is a whole route and wins alone. Any other query is partial (the
 * home page's ?pill=red, a link that only says ?view=all) and is laid over `base`, the stored
 * route, so a link that carries a pill never makes the reader answer the hosting question again.
 */
export function parseState(search, base = {}) {
  const p = new URLSearchParams(search), q = {};
  for (const k in DIMS) if (DIMS[k].includes(p.get(k))) q[k] = p.get(k);
  return normalise(q.where ? q : { ...base, ...q });
}

/** The one canonical form of a state: a key is kept only where it means something and differs from the guide's own routing. */
export function normalise(s) {
  const n = {};
  if (s.where) n.where = s.where;
  if (n.where === 'home' && s.os) n.os = s.os;
  const noBlue = n.where === 'panel' || n.where === 'container';
  if (s.pill === 'red' && !noBlue) n.pill = 'red';
  if (s.pill === 'blue' && !n.where) n.pill = 'blue';
  if (n.where === 'home' && n.os === 'linux' && n.pill !== 'red' && s.docker === 'desktop') n.docker = 'desktop';
  if (s.view === 'all') n.view = 'all';
  return n;
}

/** The guide's routing ("Which path is yours"), in one place. */
export function route(s) {
  const noBlue = s.where === 'panel' || s.where === 'container';
  const os = s.where === 'server' ? 'linux' : s.where === 'home' ? s.os : undefined;
  const pill = noBlue ? 'red' : s.pill ?? (s.where ? 'blue' : undefined);
  const path = pill === 'red' ? 'b' : pill === 'blue' ? 'a' : undefined;
  const docker = path !== 'a' || !os ? undefined : os === 'linux' ? s.docker ?? 'engine' : 'desktop';
  return {
    chosen: !!s.where && (s.where !== 'home' || !!s.os),     // the hosting question is answered
    noBlue, os, pill, path, docker,
    noShell: s.where === 'panel',                            // a panel-only host has no terminal: the guide's check of step 5 is optional there
    askOs: s.where === 'home',
    askDocker: path === 'a' && s.where === 'home' && os === 'linux',
    tunnel: s.where === 'server',                            // the SSH tunnel fold starts open
    all: s.view === 'all',
  };
}

/** Ids of the steps that count on a route, in order. lists: items per numbered list; later: ids never counted. */
export function countedSteps(r, lists, later) {
  if (!r.chosen) return [];
  const names = ['install', ...(r.path === 'b' ? ['cloud', 'import'] : [r.docker === 'engine' ? 'linux' : 'desktop'])];
  return names.flatMap((name) => Array.from({ length: lists[name] }, (_, i) => `${name}-${i + 1}`))
    .filter((id) => !later.includes(id) && !(id === 'install-5' && r.noShell));
}

/** The query string for a state: the wizard's own keys in a fixed order, every other parameter left alone. */
export function mergeQuery(search, s) {
  const p = new URLSearchParams(search);
  for (const k in DIMS) p.delete(k);
  for (const k in DIMS) if (s[k]) p.set(k, s[k]);
  const q = p.toString();
  return q ? '?' + q : '';
}
