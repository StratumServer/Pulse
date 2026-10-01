// Copy buttons on code blocks. Buttons are inserted by script, so a page without script shows
// no dead control. One delegated listener; a block may register a function that supplies its text.
const providers = new WeakMap();
const timers = new WeakMap();
let status = null;

/** box: a .code element; getText: () => string, called at click time. */
export function setCopyText(box, getText) { providers.set(box, getText); }

export function addCopyButtons(root) {
  for (const box of root.querySelectorAll('.code')) {
    const bar = box.querySelector('.code__bar');
    if (!bar || bar.querySelector('.copy')) continue;
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'btn btn--sm btn--quiet copy';
    b.textContent = 'copy';
    bar.appendChild(b);
  }
}

function legacyCopy(text) {                       // no clipboard API (an http page that is not localhost)
  const t = document.createElement('textarea');
  t.value = text;
  t.readOnly = true;
  t.className = 'sr-only';
  document.body.appendChild(t);
  t.select();
  let ok = false;
  try { ok = document.execCommand('copy'); } catch (e) { ok = false; }
  t.remove();
  return ok;
}

export function initCopy() {
  status = document.createElement('p');
  status.className = 'sr-only';
  status.setAttribute('role', 'status');
  document.body.appendChild(status);
  addCopyButtons(document);
  document.addEventListener('click', (e) => {
    const b = e.target.closest ? e.target.closest('.copy') : null;
    if (!b) return;
    const box = b.closest('.code');
    const text = providers.has(box) ? providers.get(box)() : box.querySelector('pre').textContent;
    const done = (ok) => {
      b.textContent = ok ? 'copied' : 'copy failed';
      b.dataset.state = ok ? 'copied' : 'failed';
      status.textContent = ok ? 'Copied.' : 'Copy failed: select the text by hand.';
      clearTimeout(timers.get(b));                // a second click inside the two seconds restarts them
      timers.set(b, setTimeout(() => { b.textContent = 'copy'; delete b.dataset.state; }, 2000));
    };
    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).then(() => done(true), () => done(legacyCopy(text)));
    else done(legacyCopy(text));
  });
}
