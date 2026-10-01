// Entry of assets/site.js, the script every page loads. Each concern starts on its own: one that
// throws does not stop the others. A new concern is a new init in its own module, listed here.
import { initNav } from './nav.js';
import { initCopy } from './copy.js';
import { initSearch } from './search.js';
import { initFilter } from './filter.js';
import { initConfig } from './config.js';
import { initWizard } from './wizard.js';

document.documentElement.dataset.site = '1';          // tells the inline boot script that this file ran
for (const init of [initNav, initCopy, initSearch, initFilter, initConfig, initWizard]) {
  try { init(); } catch (e) { setTimeout(() => { throw e; }); }
}
