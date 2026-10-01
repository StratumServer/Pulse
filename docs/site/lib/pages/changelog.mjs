// The changelog, whole and as written. [Unreleased] is shown as it is, including "Nothing yet."
export default function changelog({ slice, render }) {
  const r = render(slice('CHANGELOG.md'), { file: 'CHANGELOG.md', page: 'changelog' });
  return [{ slug: 'changelog', title: 'Changelog', layout: 'docs', source: ['CHANGELOG.md'], html: r.html, toc: r.toc, search: r.search }];
}
