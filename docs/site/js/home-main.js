// Entry of assets/home.js, loaded by the show pages only (Home and 404). The calm pages never
// download it. Progressive enhancement: the page is complete without it, so nothing here sets a
// flag that the page depends on. One concern throwing does not stop the others.
import { initRain } from './rain.js';
import { initHomeShow } from './home-show.js';
import { initTerminal } from './terminal.js';

for (const init of [initRain, initHomeShow, initTerminal]) {
  try { init(); } catch (e) { setTimeout(() => { throw e; }); }
}
