// A project: name, shared instructions, its chats, and the files every chat in it can read.
import { call, sendFiles, readAsBase64 } from '../bridge.js';
import { h, ic, clear, iconButton, contextMenu, confirmDialog, errorToast, debounce, relTime, basename, toast } from '../ui.js';
import { store, select, newChat } from '../app.js';
import { preview, revealLabel } from './preview.js';

export function mount(container, ctx) {
  const id = ctx.sel.slice(8);
  const project = () => store.app.projects.find((p) => p.id === id);
  let files = [];

  const root = h('div.project');
  const left = h('div.left');
  const right = h('div.right');
  root.append(left, right);
  container.append(root);

  const p0 = project();
  if (!p0) { clear(container).append(h('div.empty', h('h3', 'Project not found'))); return { title: () => ({}) }; }

  // Left: header, instructions, chats
  const name = h('input.project-name', { value: p0.name, spellcheck: 'false' });
  const saveName = debounce(() => call('updateProject', { id, name: name.value.trim() || 'Untitled Project' }), 400);
  name.addEventListener('input', saveName);
  const instructions = h('textarea.textarea', { value: p0.instructions, placeholder: 'e.g. “You are helping me write my thesis. Use British spelling and cite sources.”', style: { minHeight: '130px' } });
  instructions.addEventListener('input', debounce(() => call('updateProject', { id, instructions: instructions.value }), 500));
  const chatList = h('div.list');
  left.append(
    h('div.row', { style: { alignItems: 'flex-start', paddingTop: '6px' } },
      h('div', { style: { color: 'var(--text-2)', paddingTop: '6px' } }, ic('folder', 'lg')),
      h('div.grow', name, h('div.tiny.faint.ellipsis', { title: p0.folder }, p0.folder)),
      h('button.btn.primary', { onclick: () => newChat(id) }, ic('chatPlus', 'sm'), 'New Chat')),
    h('div.field', h('label', 'Instructions'), h('div.hint', 'Every chat in this project follows these (tone, goals, rules, context).'), instructions),
    h('div.field', h('label', 'Chats'), chatList));

  function renderChats() {
    clear(chatList);
    const chats = store.app.chats.filter((c) => c.projectId === id);
    if (!chats.length) chatList.append(h('div.hint', 'No chats yet.'));
    for (const c of chats) {
      chatList.append(h('div.chat-link', { onclick: () => select('chat:' + c.id) },
        ic('chat', 'sm'), h('span.grow.ellipsis', c.title), c.generating ? h('span.spinner') : null, h('span.tiny.faint', relTime(c.updatedAt))));
    }
  }

  // Right: files
  const list = h('div.file-list');
  const fileHead = h('div.row', { style: { padding: '12px 12px 10px 16px', borderBottom: '1px solid var(--border)' } },
    h('b.grow', 'Files'),
    iconButton('plus', 'Add files', addPicked, 'sm'),
    iconButton('refresh', 'Refresh', reload, 'sm'),
    iconButton('folderOpen', revealLabel(), () => call('revealPath', { path: project().folder }), 'sm'));
  right.append(fileHead, list);

  let depth = 0;
  const has = (e) => [...(e.dataTransfer?.types || [])].includes('Files');
  right.addEventListener('dragenter', (e) => { if (has(e)) { depth++; right.classList.add('dragging'); e.preventDefault(); } });
  right.addEventListener('dragover', (e) => { if (has(e)) e.preventDefault(); });
  right.addEventListener('dragleave', () => { if (--depth <= 0) { depth = 0; right.classList.remove('dragging'); } });
  right.addEventListener('drop', async (e) => {
    depth = 0;
    right.classList.remove('dragging');
    const dropped = [...(e.dataTransfer?.files || [])];
    if (!dropped.length) return;
    e.preventDefault();
    try {
      const r = await sendFiles(dropped, 'projectAddFiles', { id });
      if (r === null) for (const f of dropped) await call('projectAddData', { id, name: f.name, base64: await readAsBase64(f) });
      reload();
    } catch (err) { errorToast(err); }
  });

  async function addPicked() {
    try { const n = await call('projectAddFiles', { id }); if (n) { toast(`Added ${n} file${n === 1 ? '' : 's'}`, 'ok'); reload(); } } catch (e) { errorToast(e); }
  }

  async function reload() {
    try { files = await call('projectFiles', { id }); } catch { files = []; }
    renderFiles();
  }

  function renderFiles() {
    clear(list);
    const folder = project()?.folder || '';
    const sep = folder.includes('\\') ? '\\' : '/';
    if (!files.length) {
      list.append(h('div.empty', ic('fileText'), h('h3', 'No files'), h('p', 'Drop files here. Chats in this project can read them, and the AI can create new ones.')));
      return;
    }
    for (const f of files) {
      const full = folder + sep + f;
      const row = h('div.file-row', { title: f, onclick: () => preview(full), ondblclick: () => call('openPath', { path: full }) },
        ic(/\.(png|jpe?g|gif|webp|bmp)$/i.test(f) ? 'image' : 'file', 'sm'), h('span.name', f));
      row.oncontextmenu = (e) => contextMenu(e, [
        { label: 'Preview', icon: 'eye', onclick: () => preview(full) },
        { label: 'Open', icon: 'external', onclick: () => call('openPath', { path: full }).catch(errorToast) },
        { label: revealLabel(), icon: 'folderOpen', onclick: () => call('revealPath', { path: full }) },
        'sep',
        { label: 'Move to Recycle Bin', icon: 'trash', danger: true, onclick: async () => {
          if (await confirmDialog(`Delete ${basename(f)}?`, 'The file goes to the Recycle Bin.', 'Delete', true)) { await call('projectDeleteFile', { id, path: f }); reload(); }
        } },
      ]);
      list.append(row);
    }
  }

  renderChats();
  reload();
  const poll = setInterval(reload, 6000);   // files the AI writes show up without a refresh

  return {
    update() {
      const p = project();
      if (!p) return;
      if (document.activeElement !== name && name.value !== p.name) name.value = p.name;
      renderChats();
    },
    title: () => ({ title: project()?.name || 'Project', subtitle: 'Project' }),
    unmount() { clearInterval(poll); },
  };
}
