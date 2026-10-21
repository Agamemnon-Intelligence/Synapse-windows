// Websites the models build, hosted on localhost while Synapse runs.
import { call } from '../bridge.js';
import { h, ic, clear, iconButton, menu, confirmDialog, errorToast } from '../ui.js';
import { store, select } from '../app.js';
import { revealLabel } from './preview.js';

export function mount(container) {
  const page = h('div.page');
  const inner = h('div.page-inner');
  page.append(inner);
  container.append(page);
  const list = h('div.list');
  inner.append(h('div.page-head',
    h('div.grow', h('h2', 'Websites'), h('p', 'Sites your models build are hosted on localhost while Synapse is open.')),
    iconButton('refresh', 'Refresh', () => call('sitesRefresh')),
    h('button.btn', { onclick: () => call('revealPath', { path: store.app.platform.sitesRoot }) }, ic('folderOpen', 'sm'), 'Open Folder'),
    h('button.btn.primary', { onclick: async () => { const id = await call('newSiteChat'); select('chat:' + id); } }, ic('plus', 'sm'), 'New Site')), list);

  function render() {
    clear(list);
    const sites = store.app.sites;
    if (!sites.length) {
      list.append(h('div.empty', ic('globe'), h('h3', 'No websites yet'), h('p', 'Turn on “Build Websites” in a chat and ask for a site.')));
      return;
    }
    for (const s of sites) {
      const more = iconButton('more', 'More', () => menu(more, [
        { label: revealLabel(), icon: 'folderOpen', onclick: () => call('revealPath', { path: s.folder }) },
        ...(s.url ? [{ label: 'Copy Address', icon: 'link', onclick: () => navigator.clipboard.writeText(s.url) }] : []),
        'sep',
        { label: 'Move to Recycle Bin', icon: 'trash', danger: true, onclick: async () => {
          if (await confirmDialog(`Delete “${s.name}”?`, 'The site folder goes to the Recycle Bin.', 'Delete', true)) call('siteDelete', { folder: s.folder });
        } },
      ], { align: 'right' }));
      list.append(h('div.list-row',
        h('span.dot', { class: s.url ? 'green' : '' }),
        h('div.grow', h('div', s.name), h('div.tiny.faint.mono', s.url || 'Stopped')),
        s.url ? [
          store.app.platform.browser ? h('button.btn.sm', { onclick: () => call('browserNavigate', { url: s.url }) }, 'Open in Panel') : null,
          h('button.btn.sm', { onclick: () => call('openExternal', { url: s.url }) }, ic('external', 'sm'), 'Open'),
          h('button.btn.sm', { onclick: () => call('siteStop', { folder: s.folder }) }, 'Stop'),
        ] : h('button.btn.sm', { onclick: () => call('siteStart', { folder: s.folder }).catch(errorToast) }, ic('play', 'sm'), 'Start'),
        more));
    }
  }
  render();
  return { update: render, title: () => ({ title: 'Websites' }) };
}
