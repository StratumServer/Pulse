// What the client modules share to build elements. Each script of the site (lib/bundle.mjs) joins its own copy.

/** An element with properties and attributes: h('button', { type: 'button', textContent: 'x' }, { 'aria-pressed': 'false' }). */
export function h(tag, props = {}, attrs = {}) {
  const el = Object.assign(document.createElement(tag), props);
  for (const [k, v] of Object.entries(attrs)) el.setAttribute(k, v);
  return el;
}

/** An element with a class and a text. The text goes in as text, never as markup. */
export function make(tag, className, text) {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text !== undefined) e.textContent = text;
  return e;
}

/** The first element of a piece of HTML. Only a literal written in a module goes in here: what a reader types or pastes never does. */
export function fromHtml(html) {
  const t = document.createElement('template');
  t.innerHTML = html.trim();
  return t.content.firstElementChild;
}
