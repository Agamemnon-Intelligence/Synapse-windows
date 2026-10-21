// App shell: state from the host, sidebar, routing between views, approvals, shortcuts, command palette.
import { call, on, isWebView } from './bridge.js';
import { icon, logo } from './icons.js';
import { h, ic, clear, iconButton, menu, contextMenu, modal, topModal, promptDialog, confirmDialog, toast, errorToast, closeMenu } from './ui.js';
import * as ChatView from './views/chat.js';
import * as ProjectView from './views/project.js';
import * as ModelsView from './views/models.js';
import * as SitesView from './views/sites.js';
import * as UsageView from './views/usage.js';
import * as SettingsView from './views/settings.js';
import { openAccounts } from './views/accounts.js';

export const store = { app: null, chat: null };
const appListeners = new Set();
export function onApp(fn) { appListeners.add(fn); return () => appListeners.delete(fn); }

let current = null;          // { key, view, instance }
let sidebarQuery = '';
let approvalModal = null;
const root = document.getElementById('root');

// MARK: Host messages

on('app', (data) => {
  const prevSel = store.app?.selection;
  store.app = data;
  applyTheme();
  renderSidebar();
  route(prevSel !== data.selection);
  syncApprovals();
  for (const fn of appListeners) fn(data);
});

on('chat', (data) => {
  store.chat = data;
  if (current?.view === ChatView) current.instance.setChat(data);
  renderTopbar();
});

on('message', (data, msg) => {
  const c = store.chat;
  if (!c || c.id !== msg.chatId) return;
  const i = c.messages.findIndex((m) => m.id === data.id);
  if (i >= 0) c.messages[i] = data; else c.messages.push(data);
  if (current?.view === ChatView) current.instance.updateMessage(data);
});

on('host', (data) => {
  if (data?.action === 'focusComposer') current?.instance?.focus?.();
  if (data?.action === 'newChat') newChat();
  if (data?.action === 'palette') openPalette();
});

on('reconnect', () => start());

// MARK: Theme

let systemDark = window.matchMedia('(prefers-color-scheme: dark)');
systemDark.addEventListener?.('change', applyTheme);
function applyTheme() {
  const t = store.app?.settings?.theme || 'dark';
  const dark = t === 'system' ? systemDark.matches : t !== 'light';
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
}

// MARK: Shell

const shell = h('div.app');
const sidebar = h('aside.sidebar');
const main = h('section.main');
const topbar = h('header.topbar');
const viewHost = h('div.view');
main.append(topbar, viewHost);
shell.append(sidebar, main);
root.append(h('div.backdrop'), shell);

export function select(selection) {
  if (!store.app) return;
  store.app.selection = selection;
  if (selection?.startsWith('chat:') && store.chat?.id !== selection.slice(5)) store.chat = null;
  renderSidebar();
  route(true);
  call('select', { selection });
}

export async function newChat(projectId = null, model = null) {
  try {
    const id = await call('newChat', { projectId, model });
    if (store.app) store.app.selection = 'chat:' + id;
    route(true);
    setTimeout(() => current?.instance?.focus?.(), 30);
    return id;
  } catch (e) { errorToast(e); }
}

function routeKey(sel) {
  if (!sel) return 'none';
  return sel;
}

function viewFor(sel) {
  if (!sel) return null;
  if (sel.startsWith('chat:')) return ChatView;
  if (sel.startsWith('project:')) return ProjectView;
  return { models: ModelsView, sites: SitesView, usage: UsageView, settings: SettingsView }[sel] || null;
}

function route(force) {
  const sel = store.app?.selection;
  const key = routeKey(sel);
  if (current && current.key === key && !force) {
    current.instance.update?.(store.app);
    renderTopbar();
    return;
  }
  if (current && current.key === key) {
    current.instance.update?.(store.app);
    renderTopbar();
    return;
  }
  current?.instance?.unmount?.();
  clear(viewHost);
  const view = viewFor(sel);
  if (!view) {
    current = { key, view: null, instance: { title: () => '' } };
    viewHost.append(h('div.empty', html(logo(44)), h('button.btn.primary', { onclick: () => newChat() }, ic('compose'), 'New Chat')));
  } else {
    const instance = view.mount(viewHost, { store, select, newChat, sel });
    current = { key, view, instance };
    if (view === ChatView && store.chat && store.chat.id === sel.slice(5)) instance.setChat(store.chat);
  }
  renderTopbar();
}

function html(s) { const t = document.createElement('template'); t.innerHTML = s; return t.content.firstChild; }

// MARK: Top bar

export function renderTopbar() {
  clear(topbar);
  const app = store.app;
  if (!app) return;
  const t = current?.instance?.title?.() || {};
  if (shell.classList.contains('sidebar-hidden')) topbar.append(iconButton('sidebar', 'Show sidebar (Ctrl+\\)', toggleSidebar));
  topbar.append(h('div.titles', h('h1', t.title || ''), t.subtitle ? h('span.subtitle', t.subtitle) : null));
  const actions = h('div.actions');
  const pill = enginePill(app);
  if (pill) actions.append(pill);
  if (t.actions) actions.append(...t.actions.filter(Boolean));
  if (app.platform.browser && app.selection?.startsWith('chat:')) {
    actions.append(iconButton('globe', app.showBrowserPanel ? 'Hide browser (Ctrl+Shift+B)' : 'Show browser (Ctrl+Shift+B)',
      () => call('toggleBrowser'), app.showBrowserPanel ? 'active' : ''));
  }
  actions.append(iconButton('key', 'Accounts & API keys (Ctrl+Shift+K)', () => openAccounts()));
  topbar.append(actions);
}

function enginePill(app) {
  const e = app.engine;
  if (e.state === 'stopped') return null;
  if (e.state === 'loading') return h('span.engine-pill', h('span.spinner'), h('span.label', `Loading ${e.text}`));
  if (e.state === 'failed') return h('span.engine-pill.failed', { title: e.text, onclick: () => select('models') }, ic('alert', 'sm'), h('span.label', e.text));
  const pill = h('span.engine-pill', { title: 'Loaded in memory' }, h('span.dot.green'), h('span.label', e.text));
  pill.onclick = () => menu(pill, [
    { label: `Eject ${e.text}`, icon: 'power', onclick: () => call('unloadModel') },
    { label: 'Engine log', icon: 'terminal', onclick: () => { ModelsView.setTab('engine'); select('models'); } },
  ], { align: 'right' });
  return pill;
}

// MARK: Sidebar

function toggleSidebar() {
  shell.classList.toggle('sidebar-hidden');
  try { localStorage.setItem('sidebarHidden', shell.classList.contains('sidebar-hidden') ? '1' : ''); } catch {}
  renderTopbar();
}
try { if (localStorage.getItem('sidebarHidden')) shell.classList.add('sidebar-hidden'); } catch {}

const searchInput = h('input', {
  placeholder: 'Search chats', spellcheck: 'false',
  oninput: (e) => { sidebarQuery = e.target.value; renderSidebarList(); },
  onkeydown: (e) => { if (e.key === 'Escape') { e.target.value = ''; sidebarQuery = ''; renderSidebarList(); e.target.blur(); } },
});
const sideList = h('div.side-scroll');
const sideFoot = h('div.side-foot');
sidebar.append(
  h('div.sidebar-head',
    h('div.brand', html(logo(22)), 'Synapse'),
    iconButton('sidebar', 'Hide sidebar (Ctrl+\\)', toggleSidebar),
    iconButton('compose', 'New chat (Ctrl+N)', () => newChat())),
  h('div.search', ic('search', 'sm'), searchInput),
  sideList, sideFoot);

function renderSidebar() {
  renderSidebarList();
  clear(sideFoot);
  const sel = store.app?.selection;
  const nav = (id, label, iconName, extra) => h('div.side-item', { class: sel === id ? 'active' : '', onclick: () => select(id) }, ic(iconName), h('span.label', label), extra || null);
  const downloads = store.app?.downloads?.length || 0;
  sideFoot.append(
    nav('models', 'Models', 'cpu', downloads ? h('span.chip.accent', String(downloads)) : null),
    nav('sites', 'Websites', 'globe'),
    nav('usage', 'Usage', 'chart'),
    nav('settings', 'Settings', 'gear'));
}

function dayGroup(iso) {
  const d = new Date(iso);
  const now = new Date();
  const startOfDay = (x) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  const diff = Math.round((startOfDay(now) - startOfDay(d)) / 86400000);
  if (diff <= 0) return 'Today';
  if (diff === 1) return 'Yesterday';
  if (diff < 7) return 'Previous 7 days';
  if (diff < 30) return 'Previous 30 days';
  return 'Older';
}

function renderSidebarList() {
  const app = store.app;
  clear(sideList);
  if (!app) return;
  const sel = app.selection;
  const q = sidebarQuery.trim().toLowerCase();

  if (!q) {
    const section = h('div.side-section', h('div.side-title', 'Projects', iconButton('plus', 'New project', newProjectDialog, 'sm')));
    if (!app.projects.length) section.append(h('div.side-item.muted', { onclick: newProjectDialog }, ic('plus'), h('span.label', 'New Project')));
    for (const p of app.projects) {
      const row = h('div.side-item', { class: sel === 'project:' + p.id ? 'active' : '', onclick: () => select('project:' + p.id) },
        ic('folder'), h('span.label', p.name),
        iconButton('more', 'More', (e) => { e.stopPropagation(); projectMenu(e.currentTarget.parentElement, p); }, 'sm more'));
      row.oncontextmenu = (e) => contextMenu(e, projectItems(p));
      section.append(row);
    }
    sideList.append(section);
  }

  const chats = app.chats.filter((c) => !q || c.title.toLowerCase().includes(q));
  const projectName = Object.fromEntries(app.projects.map((p) => [p.id, p.name]));
  const groups = new Map();
  for (const c of chats) {
    const g = c.pinned ? 'Pinned' : dayGroup(c.updatedAt);
    if (!groups.has(g)) groups.set(g, []);
    groups.get(g).push(c);
  }
  const order = ['Pinned', 'Today', 'Yesterday', 'Previous 7 days', 'Previous 30 days', 'Older'];
  if (!chats.length) sideList.append(h('div.side-empty', q ? 'No chats match.' : 'No chats yet.'));
  for (const g of order) {
    if (!groups.has(g)) continue;
    const section = h('div.side-section', h('div.side-title', g));
    for (const c of groups.get(g)) {
      const row = h('div.side-item', {
        class: sel === 'chat:' + c.id ? 'active' : '',
        title: c.projectId ? `${c.title} · ${projectName[c.projectId] || ''}` : c.title,
        onclick: () => select('chat:' + c.id),
      },
        c.projectId ? ic('folder', 'sm') : null,
        h('span.label', c.title),
        c.needsApproval ? h('span.badge-dot', { title: 'Waiting for your approval' }) : null,
        c.generating ? h('span.spinner') : null,
        iconButton('more', 'More', (e) => { e.stopPropagation(); chatMenu(e.currentTarget.parentElement, c); }, 'sm more'));
      row.oncontextmenu = (e) => contextMenu(e, chatItems(c));
      row.ondblclick = () => renameChat(c);
      section.append(row);
    }
    sideList.append(section);
  }
}

function chatItems(c) {
  const app = store.app;
  return [
    { label: 'Rename…', icon: 'edit', onclick: () => renameChat(c) },
    { label: c.pinned ? 'Unpin' : 'Pin', icon: 'pin', onclick: () => call('pinChat', { chatId: c.id, pinned: !c.pinned }) },
    { label: 'Duplicate', icon: 'duplicate', onclick: () => call('duplicateChat', { chatId: c.id }) },
    { label: 'Export as Markdown…', icon: 'share', onclick: () => exportChat(c.id) },
    ...(app.projects.length ? ['sep', { section: 'Move to project' },
      ...app.projects.map((p) => ({ label: p.name, icon: 'folder', checked: c.projectId === p.id, onclick: () => call('moveChat', { chatId: c.id, projectId: c.projectId === p.id ? null : p.id }) }))] : []),
    'sep',
    { label: 'Delete', icon: 'trash', danger: true, onclick: () => deleteChat(c) },
  ];
}
function chatMenu(anchor, c) { menu(anchor, chatItems(c)); }

function projectItems(p) {
  return [
    { label: 'New Chat in Project', icon: 'chatPlus', onclick: () => newChat(p.id) },
    { label: store.app.platform.isWindows ? 'Show in Explorer' : 'Open Folder', icon: 'folderOpen', onclick: () => call('revealPath', { path: p.folder }) },
    'sep',
    { label: 'Remove Project', sub: 'Files stay on disk', icon: 'trash', danger: true, onclick: async () => {
      if (await confirmDialog(`Remove “${p.name}”?`, 'Its chats move to your chat list. The folder and its files stay on disk.', 'Remove', true)) call('deleteProject', { id: p.id });
    } },
  ];
}
function projectMenu(anchor, p) { menu(anchor, projectItems(p)); }

export async function renameChat(c) {
  const title = await promptDialog('Rename Chat', c.title, { okLabel: 'Rename' });
  if (title !== null && title.trim()) call('renameChat', { chatId: c.id, title });
}

export async function deleteChat(c) {
  if (c.empty || await confirmDialog(`Delete “${c.title}”?`, 'This chat and its messages will be removed.', 'Delete', true)) call('deleteChat', { chatId: c.id });
}

export async function exportChat(id) {
  try {
    const md = await call('exportChat', { chatId: id });
    if (typeof md === 'string' && md) {
      await navigator.clipboard.writeText(md).catch(() => call('copyText', { text: md }));
      toast('Chat copied to the clipboard as Markdown.', 'ok');
    }
  } catch (e) { errorToast(e); }
}

export function newProjectDialog() {
  let folder = null;
  const name = h('input.input', { placeholder: 'Name', autofocus: true });
  const where = h('div.hint.ellipsis');
  const update = () => {
    const slug = (name.value || 'project').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'project';
    where.textContent = folder || `${store.app.platform.projectsRoot}${store.app.platform.isWindows ? '\\' : '/'}${slug}`;
  };
  name.oninput = update;
  update();
  const create = async () => {
    m.close();
    try { await call('newProject', { name: name.value, folder }); } catch (e) { errorToast(e); }
  };
  const m = modal({
    title: 'New Project',
    subtitle: 'Chats in a project share its files and instructions.',
    body: [name, h('div.row', where, h('span.spacer'), h('button.btn.sm', {
      onclick: async () => {
        const f = await call('pickFolder', { title: 'Choose a project folder' });
        if (f) { folder = f; if (!name.value) name.value = f.split(/[\\/]/).pop(); update(); }
      },
    }, ic('folder', 'sm'), 'Choose Folder…'))],
    foot: [h('button.btn', { onclick: () => m.close() }, 'Cancel'), h('button.btn.primary', { onclick: create }, 'Create')],
  });
  name.addEventListener('keydown', (e) => { if (e.key === 'Enter') create(); });
}

// MARK: Approvals

function syncApprovals() {
  const req = store.app?.approvals?.[0];
  if (approvalModal && (!req || approvalModal.id !== req.id)) { approvalModal.m.close(); approvalModal = null; }
  if (!req || approvalModal) return;
  const answer = (ok, always) => {
    if (always && req.chatId) call('setChat', { chatId: req.chatId, autoApprove: true });
    call('approve', { id: req.id, ok });
    approvalModal?.m.close();
    approvalModal = null;
  };
  const always = h('input', { type: 'checkbox' });
  const m = modal({
    cls: 'approval',
    title: req.title,
    subtitle: 'The model wants to do this. Allow only if you expect it.',
    headExtra: h('div.badge', ic('shield')),
    dismissable: false,
    body: [h('pre.detail-box', req.detail),
      h('label.row.small.muted', always, 'Don’t ask again in this chat (browser submits still ask)')],
    foot: [h('button.btn', { onclick: () => answer(false) }, 'Deny'), h('button.btn.primary', { onclick: () => answer(true, always.checked), autofocus: true }, 'Allow')],
  });
  m.box.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { e.preventDefault(); answer(false); }
    if (e.key === 'Enter' && e.target.tagName !== 'INPUT') { e.preventDefault(); answer(true, always.checked); }
  });
  approvalModal = { id: req.id, m };
}

// MARK: Command palette

function openPalette() {
  closeMenu();
  if (document.querySelector('.palette')) return;
  const app = store.app;
  const actions = [
    { label: 'New Chat', icon: 'compose', kbd: 'Ctrl+N', run: () => newChat() },
    { label: 'New Project…', icon: 'folder', run: newProjectDialog },
    { label: 'Models', icon: 'cpu', kbd: 'Ctrl+Shift+M', run: () => select('models') },
    { label: 'Websites', icon: 'globe', run: () => select('sites') },
    { label: 'Usage & Limits', icon: 'chart', kbd: 'Ctrl+Shift+U', run: () => select('usage') },
    { label: 'Settings', icon: 'gear', kbd: 'Ctrl+,', run: () => select('settings') },
    { label: 'Accounts & API Keys', icon: 'key', kbd: 'Ctrl+Shift+K', run: () => openAccounts() },
    app.platform.browser ? { label: 'Toggle Browser', icon: 'globe', kbd: 'Ctrl+Shift+B', run: () => call('toggleBrowser') } : null,
    app.engine.state === 'running' ? { label: 'Unload Local Model', icon: 'power', run: () => call('unloadModel') } : null,
    { label: 'Toggle Sidebar', icon: 'sidebar', kbd: 'Ctrl+\\', run: toggleSidebar },
  ].filter(Boolean);
  const chats = app.chats.map((c) => ({ label: c.title, icon: c.projectId ? 'folder' : 'chat', sub: 'Chat', run: () => select('chat:' + c.id) }));
  const projects = app.projects.map((p) => ({ label: p.name, icon: 'folder', sub: 'Project', run: () => select('project:' + p.id) }));
  const all = [...actions, ...projects, ...chats];
  const input = h('input', { placeholder: 'Search chats, projects and commands…' });
  const results = h('div.results');
  let hot = 0;
  let shown = [];
  const close = () => { pal.remove(); layer.remove(); };
  const render = () => {
    const q = input.value.toLowerCase().trim();
    shown = (q ? all.filter((x) => x.label.toLowerCase().includes(q)) : [...actions, ...chats.slice(0, 8)]).slice(0, 60);
    hot = Math.min(hot, Math.max(0, shown.length - 1));
    clear(results);
    shown.forEach((x, i) => results.append(h('div.menu-item', { class: i === hot ? 'hot' : '', onclick: () => { close(); x.run(); } },
      ic(x.icon), h('span.label', x.label), x.kbd ? h('span.kbd', x.kbd) : x.sub ? h('span.kbd', x.sub) : null)));
    if (!shown.length) results.append(h('div.menu-empty', 'Nothing found'));
  };
  input.oninput = () => { hot = 0; render(); };
  input.onkeydown = (e) => {
    if (e.key === 'Escape') { e.preventDefault(); close(); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      hot = (hot + (e.key === 'ArrowDown' ? 1 : -1) + shown.length) % Math.max(1, shown.length);
      render();
      results.children[hot]?.scrollIntoView({ block: 'nearest' });
    } else if (e.key === 'Enter' && shown[hot]) { e.preventDefault(); close(); shown[hot].run(); }
  };
  const layer = h('div.modal-layer', { style: { background: 'rgba(0,0,0,.3)' }, onmousedown: close });
  const pal = h('div.palette', input, results);
  document.body.append(layer, pal);
  render();
  input.focus();
}

// MARK: Keyboard

document.addEventListener('keydown', (e) => {
  const ctrl = e.ctrlKey || e.metaKey;
  const k = e.key.toLowerCase();
  if (topModal() && !(ctrl && k === 'k')) {
    if (e.key === 'Escape' && topModal().dismissable) { e.preventDefault(); topModal().close(); }
    return;
  }
  if (!ctrl) return;
  if (k === 'n' && !e.shiftKey) { e.preventDefault(); newChat(); }
  else if (k === 'n' && e.shiftKey) {
    e.preventDefault();
    const sel = store.app?.selection || '';
    const pid = sel.startsWith('project:') ? sel.slice(8) : store.chat?.projectId;
    if (pid) newChat(pid); else newProjectDialog();
  }
  else if (k === 'k' && e.shiftKey) { e.preventDefault(); openAccounts(); }
  else if (k === 'k') { e.preventDefault(); openPalette(); }
  else if (k === 'm' && e.shiftKey) { e.preventDefault(); select('models'); }
  else if (k === 'u' && e.shiftKey) { e.preventDefault(); select('usage'); }
  else if (k === 'b' && e.shiftKey) { e.preventDefault(); if (store.app?.platform.browser) call('toggleBrowser'); }
  else if (k === ',') { e.preventDefault(); select('settings'); }
  else if (k === '\\') { e.preventDefault(); toggleSidebar(); }
  else if (k === '.' && store.chat?.generating) { e.preventDefault(); call('stop', { chatId: store.chat.id }); }
  else if ((k === 'r' || k === 'f5') && !isDev()) { e.preventDefault(); }
});

// Links rendered in markdown and elsewhere open in the default browser.
document.addEventListener('click', (e) => {
  const a = e.target.closest('a[data-href]');
  if (a) { e.preventDefault(); call('openExternal', { url: a.dataset.href }); return; }
  const copy = e.target.closest('[data-copy]');
  if (copy) {
    const code = copy.closest('.code-block')?.querySelector('code')?.textContent ?? '';
    navigator.clipboard.writeText(code).catch(() => call('copyText', { text: code }));
    const label = copy.querySelector('span');
    if (label) { label.textContent = 'Copied'; setTimeout(() => { label.textContent = 'Copy'; }, 1400); }
  }
});

// The window's own context menu is replaced by ours; keep the native one for text fields and selections.
document.addEventListener('contextmenu', (e) => {
  if (e.defaultPrevented) return;
  const editable = e.target.closest('input, textarea, [contenteditable]');
  if (!editable && !String(window.getSelection())) e.preventDefault();
});

function isDev() { return !isWebView; }

// Unhandled errors go to the host log, so they show up in diagnostics.
window.addEventListener('error', (e) => call('log', { message: `${e.message} @ ${e.filename}:${e.lineno}` }).catch(() => {}));
window.addEventListener('unhandledrejection', (e) => call('log', { message: 'Unhandled: ' + (e.reason?.message || e.reason) }).catch(() => {}));

// MARK: Start

async function start() {
  try {
    const { app, chat } = await call('ready');
    store.chat = chat;
    store.app = app;
    applyTheme();
    renderSidebar();
    route(true);
    syncApprovals();
    for (const fn of appListeners) fn(app);
  } catch (e) {
    clear(viewHost).append(h('div.empty', ic('alertCircle'), h('h3', 'Synapse isn’t responding'), h('p', String(e.message || e))));
    setTimeout(start, 1500);
  }
}
start();
