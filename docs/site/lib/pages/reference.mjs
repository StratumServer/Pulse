// Scraping, Attribution, OTLP export and Development: README sections rendered as written. Nothing
// is added to these pages; the work is the slice, the link rewriting and the one security callout.
import { markCallout } from '../md.mjs';

export default function reference({ slice, render }) {
  // a page made of one README section promotes it (H2 becomes H1, H3 becomes H2) and keeps the ids
  const section = (slug, title, name, prepare) => {
    const tokens = slice(name);
    prepare?.(tokens);
    const r = render(tokens, { file: 'README.md', page: slug, shift: 1 });
    return { slug, title, layout: 'docs', source: ['README.md'], html: r.html, toc: r.toc, search: r.search };
  };
  // a page made of several sections gets a site-written H1 and keeps the README's H2 as H2
  const development = () => {
    const r = render([...slice('building'), ...slice('going'), ...slice('license')], { file: 'README.md', page: 'development' });
    return { slug: 'development', title: 'Development', layout: 'docs', source: ['README.md'], html: `<h1 id="development">Development</h1>\n${r.html}`,
      toc: r.toc, search: [{ id: 'development', title: 'Development', text: '' }, ...r.search] };
  };
  return [
    section('scraping', 'Scraping', 'scraping'),
    section('attribution', 'Attribution', 'attribution'),
    section('otlp', 'OTLP export', 'otlp', (tokens) => markCallout(tokens, '**`pulse-otlp.json` holds a credential.**', 'README.md')),
    development(),
  ];
}
