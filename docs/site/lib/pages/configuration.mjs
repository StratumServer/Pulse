// Install and configuration: the README's Install and Configuration sections under a site-written H1,
// with the data the builder needs written on the rows of the two config tables (the builder itself is
// js/config.js: a fifth column of controls and the file they give). The two tables are named by their
// file, not by their headers (both would be announced as "Key, Default, What it does, Live or restart").
// Everything about a key (default, type, limits, closed list, live or restart) was read from the README's
// own table by lib/data.mjs; nothing here restates it.
import { REPO } from '../layout.mjs';
import { plainOf } from '../md.mjs';
import { flat } from '../sources.mjs';

const DROP = "Drop `pulse_x.x.x.zip` into your server's `Mods/` folder";

export default function configuration({ slice, render, data, esc }) {
  const install = slice('install');
  const tokens = [...install, ...slice('configuration')];
  const tables = tokens.filter((t) => t.type === 'table');
  if (tables.length !== data.config.length) throw new Error(`README.md: ${tables.length} tables in Install and Configuration, but ${data.config.length} config tables were read`);

  // both downloads come before the one paragraph that tells the reader to drop the zips in Mods/: that paragraph ends on
  // "with its defaults:" and its block, which the downloads would otherwise come between
  const drops = install.filter((t) => t.type === 'paragraph' && flat(t.text).startsWith(DROP));
  if (drops.length !== 1) throw new Error(`README.md: the paragraph that starts "${DROP}" should exist exactly once in Install, found ${drops.length}`);
  const zip = (name) => `<p class="dl"><a class="btn btn--sm" href="${esc(`${REPO}/releases/download/v${data.version}/${name}_${data.version}.zip`)}">download ${name}_${data.version}.zip</a> <span>version ${esc(data.version)}, released ${esc(data.released)}</span></p>`;
  const lead = install.slice(0, install.indexOf(drops[0])).findLast((t) => t.type !== 'space');
  lead.after = `${zip('pulse')}\n${zip('pulseotlp')}\n`;

  // the builder edits whole numbers, booleans, strings and the one list of header names and values: a key of another
  // shape would be edited wrongly and silently, so it stops the build until the builder knows it
  for (const { file, rows } of data.config) {
    for (const row of rows) {
      const ok = row.type === 'boolean' || row.type === 'string' || (row.type === 'number' && Number.isInteger(row.default)) || (row.type === 'object' && row.key === 'Headers' && !Object.keys(row.default).length);
      if (!ok) throw new Error(`README.md: ${row.key} in the ${file} table has a default of a shape the config builder does not know (${JSON.stringify(row.default)}): js/config.js edits booleans, strings, whole numbers and the Headers key`);
    }
  }

  // the rows carry what the builder reads: key, default (a JSON literal), type, limits, closed list, live or restart
  const search = [];
  tables.forEach((table, i) => {
    const { file, rows } = data.config[i];
    if (table.rows.length !== rows.length || rows.some((row, j) => plainOf(table.rows[j][0]) !== row.key)) throw new Error(`README.md: the rows of the ${file} table are not the ones lib/data.mjs read`);
    table.label = file;
    table.attrs = { 'data-config': file };
    table.rowAttrs = rows.map((row) => ({
      id: row.id, 'data-key': row.key, 'data-default': JSON.stringify(row.default), 'data-type': row.type,
      ...(row.min !== undefined && { 'data-min': row.min }), ...(row.max !== undefined && { 'data-max': row.max }),
      ...(row.options && { 'data-options': JSON.stringify(row.options) }), ...(row.live && { 'data-live': '' }),
    }));
    rows.forEach((row, j) => search.push({ id: row.id, title: row.key, text: `${plainOf(table.rows[j][2])} ${row.liveText} ${file}` }));
  });

  const r = render(tokens, { file: 'README.md', page: 'configuration' });
  return [{ slug: 'configuration', title: 'Install and configuration', layout: 'docs', source: ['README.md'],
    html: `<h1 id="install-and-configuration">Install and configuration</h1>\n${r.html}`, toc: r.toc,
    search: [{ id: 'install-and-configuration', title: 'Install and configuration', text: '' }, ...r.search, ...search] }];
}
