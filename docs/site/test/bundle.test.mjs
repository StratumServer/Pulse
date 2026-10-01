// The joiner of lib/bundle.mjs: client modules in, one classic script out. Run with node --test.
import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { join } from 'node:path';
import test from 'node:test';
import { fileURLToPath, pathToFileURL } from 'node:url';
import vm from 'node:vm';
import { bundle, joinCss, readDir } from '../lib/bundle.mjs';

const JS = fileURLToPath(new URL('../js/', import.meta.url));
const files = new Map(Object.entries({
  'math.js': "export function add(a, b) { return a + b; }\nexport const PI = 3;\nconst hidden = 1;\nexport class Box { constructor(v) { this.v = v + hidden; } }\n",
  'ui.js': "import { add, PI } from './math.js';\nimport { Box } from './math.js';\nconst hidden = 40;\nexport function init(out) { out.push(add(PI, hidden), new Box(1).v); }\nexport async function later() { return add(1, 1); }\n",
  'unused.js': "export const never = true;\nthrow new Error('must not be included');\n",
  'main.js': "import { init } from './ui.js';\ninit(globalThis.PROBE);\n",
}));

test('the joined script behaves like the modules', () => {
  const out = bundle(files, 'main.js');
  const probe = [];
  vm.runInNewContext(out, { PROBE: probe, globalThis: { PROBE: probe } });
  assert.deepEqual(probe, [43, 2], 'each module keeps its own private names');
  assert.ok(!out.includes('never'), 'a module nothing imports is left out');
  assert.ok(out.indexOf('js/math.js') < out.indexOf('js/ui.js') && out.indexOf('js/ui.js') < out.indexOf('js/main.js'), 'dependencies come first');
  assert.equal(out.split('\n').filter((l) => /^(import|export)\b/.test(l)).length, 0, 'no import or export statement is left');
});

test('the conventions fail loudly, naming the file and the line', () => {
  const bad = (src, re) => assert.throws(() => bundle(new Map([['main.js', src], ['a.js', 'export const a = 1;\n']]), 'main.js'), re);
  bad("import a from './a.js';\n", /main\.js:1: only "import \{ a, b \}/);
  bad("import { a as b } from './a.js';\n", /main\.js:1/);
  bad("import {\n  a,\n} from './a.js';\n", /main\.js:1/);
  bad('import { a } from "./a.js";\n', /main\.js:1/);
  bad("import { a } from './nope.js';\n", /imports \.\/nope\.js, which does not exist/);
  bad('export default 1;\n', /main\.js:1: only "export function"/);
  bad('export { x };\n', /main\.js:1/);
  bad("const m = await import('./a.js');\n", /main\.js:1: dynamic import/);
  bad('const u = import.meta.url;\n', /main\.js:1/);
  bad("if (x) {\n  import { a } from './a.js';\n}\n", /main\.js:2/);
  bad("import { a } from './a.js';\nconst a = 2;\n", /already been declared/);     // caught by the syntax check
  assert.throws(() => bundle(new Map([['main.js', "import { b } from './b.js';\n"], ['b.js', "import { m } from './main.js';\nexport const b = 1;\n"]]), 'main.js'), /import cycle main\.js -> b\.js -> main\.js/);
});

test('the real sources join into the two scripts of the site', () => {
  const sources = readDir(JS);
  const site = bundle(sources, 'main.js', 'site.js');
  const home = bundle(sources, 'home-main.js', 'home.js');
  for (const name of ['nav.js', 'copy.js', 'search.js', 'filter.js', 'config.js', 'wizard.js', 'main.js']) assert.ok(site.includes(`// ---- js/${name}`), `site.js holds ${name}`);
  for (const name of ['rain.js', 'home-show.js', 'terminal.js', 'home-main.js']) assert.ok(home.includes(`// ---- js/${name}`), `home.js holds ${name}`);
  for (const name of ['dom.js', 'glyphs.js']) for (const script of [site, home]) assert.equal(script.split(`// ---- js/${name}\n`).length - 1, 1, `${name} is joined once into each script that imports it`);
  for (const name of ['rain.js', 'home-show.js', 'terminal.js']) assert.ok(!site.includes(`js/${name}`), `the calm pages never download ${name}`);
  for (const name of ['nav.js', 'copy.js', 'search.js', 'filter.js', 'config.js', 'wizard.js']) assert.ok(!home.includes(`js/${name}`), `home.js leaves out ${name}`);
  assert.ok(site.includes("dataset.site = '1'"), 'main.js tells the boot script that the script ran');
});

test('no module touches the document when it is loaded: Node can import every one but the two entries', async () => {
  const entries = ['main.js', 'home-main.js'];
  for (const name of readdirSync(JS).filter((f) => f.endsWith('.js') && !entries.includes(f))) {
    const module = await import(pathToFileURL(join(JS, name)).href);
    assert.ok(Object.keys(module).length, `${name} exports something`);
  }
});

test('stylesheets are joined in file name order and may not name another origin', () => {
  const css = joinCss(new Map([['00-a.css', 'a { color: red; }\n'], ['10-b.css', 'b { background: url(b.png); }\n']]));
  assert.ok(css.indexOf('/* ---- css/00-a.css */') < css.indexOf('/* ---- css/10-b.css */'));
  assert.throws(() => joinCss(new Map([['a.css', 'a { background: url("https://example.org/x.png"); }']])), /css\/a\.css: a url\(\) that names another origin/);
  assert.throws(() => joinCss(new Map([['a.css', 'a { background: url(//example.org/x.png); }']])), /another origin/);
  assert.throws(() => joinCss(new Map([['a.css', 'a { background: url(data:image/svg+xml,x); }']])), /a data: address/, 'the policy has no data: source, so it would be blocked silently');
  assert.doesNotThrow(() => joinCss(new Map([['a.css', 'a { filter: url(#glow); background: url(x.png); }']])));
  const real = joinCss(readDir(fileURLToPath(new URL('../css/', import.meta.url))));
  assert.ok(real.indexOf('css/00-tokens.css') < real.indexOf('css/10-base.css') && real.indexOf('css/50-wizard.css') < real.indexOf('css/90-print.css'));
});
