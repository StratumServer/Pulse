// node --test: the wizard's routing table. Pure functions, no browser.
import assert from 'node:assert/strict';
import test from 'node:test';
import { DIMS, normalise, parseState, route, countedSteps, mergeQuery } from '../js/wizard-state.js';

const LISTS = { install: 5, linux: 3, desktop: 3, cloud: 6, import: 4 }, LATER = ['linux-3', 'desktop-3'];
const probe = (query, stored) => {
  const s = parseState(query, stored), r = route(s), steps = countedSteps(r, LISTS, LATER);
  return { canonical: mergeQuery('', s), path: r.path ?? '-', docker: r.docker ?? '-', steps: steps.length, chosen: r.chosen, tunnel: r.tunnel, askOs: r.askOs, askDocker: r.askDocker, noBlue: r.noBlue };
};

test('the routing table: the address opened, the address shown, the route, the steps, what is asked', () => {
  let cases = 0;
  const row = (query, want) => { cases++; assert.deepEqual(probe(query), { askOs: false, askDocker: false, noBlue: false, tunnel: false, ...want }, query); };
  row('', { canonical: '', path: '-', docker: '-', steps: 0, chosen: false });
  row('?where=server', { canonical: '?where=server', path: 'a', docker: 'engine', steps: 7, chosen: true, tunnel: true });
  row('?where=home', { canonical: '?where=home', path: 'a', docker: '-', steps: 0, chosen: false, askOs: true });
  row('?where=home&os=windows', { canonical: '?where=home&os=windows', path: 'a', docker: 'desktop', steps: 7, chosen: true, askOs: true });
  row('?where=home&os=macos', { canonical: '?where=home&os=macos', path: 'a', docker: 'desktop', steps: 7, chosen: true, askOs: true });
  row('?where=home&os=linux', { canonical: '?where=home&os=linux', path: 'a', docker: 'engine', steps: 7, chosen: true, askOs: true, askDocker: true });
  row('?where=home&os=linux&docker=desktop', { canonical: '?where=home&os=linux&docker=desktop', path: 'a', docker: 'desktop', steps: 7, chosen: true, askOs: true, askDocker: true });
  row('?where=panel', { canonical: '?where=panel', path: 'b', docker: '-', steps: 14, chosen: true, noBlue: true });
  row('?where=container', { canonical: '?where=container', path: 'b', docker: '-', steps: 15, chosen: true, noBlue: true });
  row('?where=server&pill=red', { canonical: '?where=server&pill=red', path: 'b', docker: '-', steps: 15, chosen: true, tunnel: true });
  row('?where=home&os=linux&pill=red', { canonical: '?where=home&os=linux&pill=red', path: 'b', docker: '-', steps: 15, chosen: true, askOs: true });
  row('?pill=red', { canonical: '?pill=red', path: 'b', docker: '-', steps: 0, chosen: false });
  row('?pill=blue', { canonical: '?pill=blue', path: 'a', docker: '-', steps: 0, chosen: false });
  row('?where=server&view=all', { canonical: '?where=server&view=all', path: 'a', docker: 'engine', steps: 7, chosen: true, tunnel: true });
  // what the guide rules out, or what means nothing, is dropped
  row('?where=container&pill=blue', { canonical: '?where=container', path: 'b', docker: '-', steps: 15, chosen: true, noBlue: true });
  row('?where=panel&pill=red&docker=desktop&os=linux', { canonical: '?where=panel', path: 'b', docker: '-', steps: 14, chosen: true, noBlue: true });
  row('?where=server&pill=blue&os=windows&docker=desktop', { canonical: '?where=server', path: 'a', docker: 'engine', steps: 7, chosen: true, tunnel: true });
  row('?where=home&os=windows&docker=engine', { canonical: '?where=home&os=windows', path: 'a', docker: 'desktop', steps: 7, chosen: true, askOs: true });
  row('?where=home&os=linux&docker=engine', { canonical: '?where=home&os=linux', path: 'a', docker: 'engine', steps: 7, chosen: true, askOs: true, askDocker: true });
  row('?where=home&os=linux&pill=red&docker=desktop', { canonical: '?where=home&os=linux&pill=red', path: 'b', docker: '-', steps: 15, chosen: true, askOs: true });
  row('?where=mars&os=%3Cscript%3E&pill=green&view=1', { canonical: '', path: '-', docker: '-', steps: 0, chosen: false });
  row('?os=linux&docker=desktop', { canonical: '', path: '-', docker: '-', steps: 0, chosen: false });
  // foreign parameters are not ours: the state ignores them
  row('?token=abc&where=server', { canonical: '?where=server', path: 'a', docker: 'engine', steps: 7, chosen: true, tunnel: true });
  assert.equal(cases, 23);
});

test('which steps count, exactly', () => {
  const ids = (query) => countedSteps(route(parseState(query)), LISTS, LATER);
  assert.deepEqual(ids('?where=server'), ['install-1', 'install-2', 'install-3', 'install-4', 'install-5', 'linux-1', 'linux-2']);
  assert.deepEqual(ids('?where=home&os=macos').slice(5), ['desktop-1', 'desktop-2']);
  assert.deepEqual(ids('?where=panel'), ['install-1', 'install-2', 'install-3', 'install-4', 'cloud-1', 'cloud-2', 'cloud-3', 'cloud-4', 'cloud-5', 'cloud-6', 'import-1', 'import-2', 'import-3', 'import-4'],
    'install-5 is not counted on a panel-only host, which has no shell');
  assert.ok(ids('?where=container').includes('install-5'), 'a container may have one: the guide only names the panel-only host');
  assert.deepEqual([route(parseState('?where=panel')).noShell, route(parseState('?where=container')).noShell, route(parseState('?where=server')).noShell], [true, false, false]);
  assert.equal(ids('?where=server&pill=red').length, 15, 'by choice, step 5 counts on path B');
  assert.ok(!ids('?where=server').includes('linux-3') && !ids('?where=home&os=windows').includes('desktop-3'), 'the teardown step is shown, never counted');
});

test('changing an answer is "set one key, normalise": the rules fall out of it', () => {
  const set = (s, k, v) => normalise({ ...s, [k]: v });
  assert.deepEqual(set(parseState('?where=home&os=linux&docker=desktop'), 'where', 'server'), { where: 'server' });
  assert.deepEqual(set(parseState('?where=server&pill=red'), 'where', 'home'), { where: 'home', pill: 'red' }, 'an explicit red pill survives a change of hosting situation');
  assert.deepEqual(set(parseState('?where=server&pill=red'), 'where', 'container'), { where: 'container' });
  assert.deepEqual(set(parseState('?where=server&pill=red'), 'pill', 'blue'), { where: 'server' }, 'blue is the default there, so it is not kept');
  assert.deepEqual(set(parseState('?where=home&os=linux&docker=desktop'), 'os', 'windows'), { where: 'home', os: 'windows' }, 'a change of system drops the Docker answer');
  assert.deepEqual(set(parseState('?pill=blue'), 'where', 'container'), { where: 'container' }, 'an explicit blue pill does not survive');
  assert.deepEqual(set(parseState('?where=server'), 'view', 'all'), { where: 'server', view: 'all' });
  assert.deepEqual(set(parseState('?where=server&view=all'), 'view', undefined), { where: 'server' });
});

test('foreign parameters survive, ours come last in a fixed order', () => {
  assert.equal(mergeQuery('?token=abc&utm=x&where=home', parseState('?pill=red&where=server')), '?token=abc&utm=x&where=server&pill=red');
  assert.equal(mergeQuery('?view=all&where=home&os=linux', {}), '');
  assert.equal(mergeQuery('', parseState('?view=all&docker=desktop&os=linux&where=home')), '?where=home&os=linux&docker=desktop&view=all');
});

test('a link that only carries part of a route is laid over the stored one, a link that names the hosting situation wins alone', () => {
  const stored = (query) => parseState(query);
  const lay = (query, base) => parseState(query, stored(base));
  // the home page's pills and the builder's ?pill=red#cloud-4 keep the answer the reader already gave
  assert.deepEqual(lay('?pill=red', '?where=server'), { where: 'server', pill: 'red' });
  assert.deepEqual(lay('?pill=red', '?where=home&os=windows'), { where: 'home', os: 'windows', pill: 'red' });
  assert.deepEqual(lay('?pill=blue', '?where=server&pill=red'), { where: 'server' }, 'an explicit blue link goes back to the default of the stored route');
  assert.deepEqual(lay('?pill=blue', '?where=panel'), { where: 'panel' }, 'a pill the guide rules out is dropped');
  assert.deepEqual(lay('?pill=red', ''), { pill: 'red' }, 'with nothing stored it is the first visit with a pill');
  assert.deepEqual(lay('?os=linux', '?where=home'), { where: 'home', os: 'linux' });
  assert.deepEqual(lay('?docker=desktop', '?where=home&os=linux'), { where: 'home', os: 'linux', docker: 'desktop' });
  assert.deepEqual(lay('?view=all', '?where=server&pill=red'), { where: 'server', pill: 'red', view: 'all' });
  // nothing known in the query: the stored route comes back as it was
  assert.deepEqual(lay('', '?where=server&pill=red&view=all'), { where: 'server', pill: 'red', view: 'all' });
  assert.deepEqual(lay('?token=abc&utm=x', '?where=home&os=linux&docker=desktop'), { where: 'home', os: 'linux', docker: 'desktop' });
  assert.deepEqual(lay('?where=mars', '?where=server'), { where: 'server' }, 'a value that is not on the list is no answer at all');
  // a query with a hosting situation is a whole route: nothing of the stored one is carried over
  assert.deepEqual(lay('?where=home&os=windows', '?where=server&pill=red'), { where: 'home', os: 'windows' });
  assert.deepEqual(lay('?where=panel', '?where=home&os=linux&docker=desktop&view=all'), { where: 'panel' });
  assert.deepEqual(lay('?where=server', '?where=server&pill=red'), { where: 'server' });
});

test('nothing but the closed lists of DIMS ever reaches the state', () => {
  assert.deepEqual(Object.keys(DIMS), ['where', 'os', 'pill', 'docker', 'view']);
  for (const hostile of ['?where=__proto__', '?where[]=server', '?where=server%00', '?where=SERVER', '?pill=%3Cimg%20src%3Dx%3E', '?docker=constructor', '?view=ALL']) {
    assert.deepEqual(parseState(hostile), {}, hostile);
  }
  assert.deepEqual(Object.keys(parseState('?where=server&__proto__=x&constructor=y')), ['where']);
  assert.equal(Object.getPrototypeOf(parseState('?where=server')), Object.prototype);
});
