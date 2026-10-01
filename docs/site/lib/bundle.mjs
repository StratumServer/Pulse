// Joins the client sources into the files the page loads. The sources stay real ES modules, so
// node --test imports them as they are. A page cannot use them as modules: Chrome and Firefox do
// not run a module script from a file:// URL, and the site has to open from a downloaded folder.
// So the modules reachable from one entry are joined into ONE classic script. If file:// is ever
// dropped as a requirement, delete bundle(), copy js/ to assets/js/ and ship type="module": the
// modules themselves do not change.
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import vm from 'node:vm';

const IMPORT = /^import \{ ([\w$]+(?:, [\w$]+)*) \} from '\.\/([\w.-]+\.js)';$/;
const EXPORT = /^export ((?:async )?function\*?|const|let|class) ([\w$]+)/;

/**
 * files: Map of file name -> source of every client module; entry: the file name to start from;
 * name: what the script is called in a stack trace. A module nothing imports is left out. One
 * source line is one output line, so a stack trace maps back by eye (no source map).
 */
export function bundle(files, entry, name = 'site.js') {
  const done = new Map();                                    // name -> wrapped source, in dependency order
  const visit = (file, trail) => {
    if (done.has(file)) return;
    if (trail.includes(file)) throw new Error(`js/${file}: import cycle ${[...trail, file].join(' -> ')}`);
    if (!files.has(file)) throw new Error(`js/${trail.at(-1)}: imports ./${file}, which does not exist`);
    const exported = [];
    const lines = files.get(file).split('\n').map((line, i) => {
      const at = `js/${file}:${i + 1}`;
      if (/^import\b/.test(line)) {
        const m = IMPORT.exec(line);
        if (!m) throw new Error(`${at}: only "import { a, b } from './file.js';" on one line is supported`);
        visit(m[2], [...trail, file]);
        return `const { ${m[1]} } = __m[${JSON.stringify(m[2])}];`;
      }
      if (/^export\b/.test(line)) {
        const m = EXPORT.exec(line);
        if (!m) throw new Error(`${at}: only "export function", "export const", "export let" and "export class" are supported`);
        exported.push(m[2]);
        return line.slice('export '.length);
      }
      if (/\bimport\s*\(|\bimport\.meta\b|^\s+(?:import|export)\s/.test(line)) throw new Error(`${at}: dynamic import, import.meta and indented import or export are not supported`);
      return line;
    });
    done.set(file, `// ---- js/${file}\n__m[${JSON.stringify(file)}] = (function () {\n${lines.join('\n')}\nreturn { ${exported.join(', ')} };\n})();`);
  };
  visit(entry, []);
  const out = `(function () {\n'use strict';\nconst __m = {};\n${[...done.values()].join('\n')}\n})();\n`;
  new vm.Script(out, { filename: name });                    // a syntax error stops the build here
  return out;
}

/** Every file of a directory, by name, in name order. */
export const readDir = (dir) => new Map(readdirSync(dir).sort().map((f) => [f, readFileSync(join(dir, f), 'utf8')]));

/** The stylesheets joined in file name order. No processing: a url() with a scheme stops the build (another origin, or data:, which the policy has no source for). */
export function joinCss(files) {
  const out = [...files].map(([name, css]) => {
    const bad = /url\(\s*["']?\s*(?:[a-z][a-z0-9+.-]*:|\/\/)/i.exec(css);
    if (bad) throw new Error(`css/${name}: a url() that names another origin or a data: address (${bad[0]}): the site makes no request outside itself and the policy has no data: source`);
    return `/* ---- css/${name} */\n${css.trimEnd()}\n`;
  });
  return out.join('\n');
}
