// The chat: message list with streaming updates, tool activity, and the composer.
import { call, sendFiles, readAsBase64, isWebView } from '../bridge.js';
import { h, ic, clear, menu, toast, errorToast, iconButton, copyText, basename, ext, host, html } from '../ui.js';
import { renderMarkdown } from '../markdown.js';
import { logo } from '../icons.js';
import { store, select, renameChat, exportChat, deleteChat } from '../app.js';
import { fileUrl, preview, openImage, revealLabel } from './preview.js';

const drafts = new Map();        // chatId -> text
const pendingAtts = new Map();   // chatId -> attachments
try { for (const [k, v] of Object.entries(JSON.parse(localStorage.getItem('drafts') || '{}'))) drafts.set(k, v); } catch {}
const saveDrafts = () => { try { localStorage.setItem('drafts', JSON.stringify(Object.fromEntries([...drafts].filter(([, v]) => v)))); } catch {} };

const CAPS = [
  ['webSearch', 'Web Search', 'search', 'Search the web and read pages'],
  ['imageGeneration', 'Image Generation', 'image', 'Create pictures from a description'],
  ['computerAccess', 'Computer Access', 'monitor', 'PowerShell, files, apps, clipboard, screenshots'],
  ['browser', 'Browser', 'globe', 'Drive the built-in browser'],
  ['websites', 'Build Websites', 'code', 'Write sites and host them on localhost'],
];
const THINK = [
  ['off', 'Fast', 'zap', 'No extra reasoning. Quickest answers.'],
  ['standard', 'Normal', 'brain', 'The model decides how much to think.'],
  ['high', 'Think hard', 'brain', 'Maximum reasoning budget and step-by-step instructions.'],
  ['max', 'Think harder', 'sparkle', 'Think hard, then a second pass reviews and corrects the answer.'],
];

export function mount(container, ctx) {
  const chatId = ctx.sel.slice(5);
  let chat = null;
  let stick = true;
  let editing = null;
  const rows = new Map();   // message id -> { el, sig }
  let timer = null;

  const root = h('div.chat');
  const scroller = h('div.messages');
  const inner = h('div.messages-inner');
  scroller.append(inner);
  const jump = h('button.jump', { onclick: () => { stick = true; scrollToBottom(true); } }, ic('arrowDown', 'sm'), 'Jump to latest');
  jump.style.display = 'none';
  const greeting = h('div.greeting');
  const composerWrap = h('div.composer-wrap');
  const composer = buildComposer();
  root.append(greeting, scroller, jump, composerWrap);
  container.append(root);

  scroller.addEventListener('scroll', () => {
    stick = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight < 80;
    jump.style.display = stick ? 'none' : '';
  });

  // Drag and drop files anywhere on the chat.
  let dragDepth = 0;
  root.addEventListener('dragenter', (e) => { if (hasFiles(e)) { dragDepth++; root.classList.add('dragging'); e.preventDefault(); } });
  root.addEventListener('dragover', (e) => { if (hasFiles(e)) e.preventDefault(); });
  root.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; root.classList.remove('dragging'); } });
  root.addEventListener('drop', (e) => {
    dragDepth = 0;
    root.classList.remove('dragging');
    if (!e.dataTransfer?.files?.length) return;
    e.preventDefault();
    addFiles(e.dataTransfer.files);
  });

  function hasFiles(e) { return [...(e.dataTransfer?.types || [])].includes('Files'); }

  // MARK: Rendering

  function layout() {
    const empty = !chat || chat.messages.length === 0;
    greeting.style.display = empty ? '' : 'none';
    scroller.style.display = empty ? 'none' : '';
    if (empty) {
      if (!greeting.firstChild || greeting.dataset.project !== String(chat?.projectName || '')) {
        clear(greeting);
        greeting.dataset.project = String(chat?.projectName || '');
        greeting.append(html(logo(38)), h('h2', chat?.projectName ? `New chat in ${chat.projectName}` : 'How can I help?'));
        const wrap = h('div.composer-wrap');
        greeting.append(wrap, suggestions());
      }
      greeting.querySelector('.composer-wrap').append(composer.el);
    } else {
      composerWrap.append(composer.el);
    }
    composerWrap.style.display = empty ? 'none' : '';
  }

  function suggestions() {
    const isWin = store.app?.platform?.isWindows;
    const items = [
      ['search', 'What happened in tech this week?', { webSearch: true }],
      ['code', 'Build me a landing page for a coffee shop', { websites: true }],
      ['terminal', isWin ? 'Which apps use the most disk space on my PC?' : 'What is using the most disk space?', { computerAccess: true }],
      ['brain', 'Explain how transformers work, simply', null],
    ];
    return h('div.suggestions', items.map(([i, text, caps]) => h('button', {
      onclick: async () => {
        if (caps && chat) await call('setChat', { chatId, capabilities: { ...chat.capabilities, ...caps } });
        composer.setText(text);
        composer.focus();
      },
    }, ic(i, 'sm'), text)));
  }

  function setChat(c) {
    if (!c || c.id !== chatId) return;
    chat = c;
    layout();
    renderAll();
    composer.update();
  }

  function renderAll() {
    const ids = new Set(chat.messages.map((m) => m.id));
    for (const [id, r] of rows) if (!ids.has(id)) { r.el.remove(); rows.delete(id); }
    let prev = null;
    chat.messages.forEach((m, i) => {
      const el = renderRow(m, i);
      const expected = prev ? prev.nextSibling : inner.firstChild;
      if (el !== expected) inner.insertBefore(el, expected);
      prev = el;
    });
    scrollToBottom();
    tick();
  }

  function updateMessage(m) {
    if (!chat) return;
    const i = chat.messages.findIndex((x) => x.id === m.id);
    if (i < 0) return;
    const r = rows.get(m.id);
    if (!r) { renderAll(); return; }
    renderRow(m, i);
    scrollToBottom();
    tick();
  }

  function isLive(i) { return chat.generating && i === chat.messages.length - 1; }

  function renderRow(m, i) {
    const live = isLive(i);
    const last = i === chat.messages.length - 1;
    const nextIsTool = chat.messages[i + 1]?.role === 'tool';
    // Tool calls are "running" until their results arrive.
    const answered = new Set(chat.messages.slice(i + 1).filter((x) => x.role === 'tool').map((x) => x.toolCallId));
    const sig = JSON.stringify([m, live, last, nextIsTool, chat.generating && m.toolCalls?.length ? [...answered] : 0, editing === m.id]);
    const existing = rows.get(m.id);
    if (existing && existing.sig === sig) return existing.el;
    const el = m.role === 'user' ? userRow(m, last) : m.role === 'assistant' ? assistantRow(m, live, last, answered) : m.role === 'tool' ? toolRow(m) : h('div');
    if (existing) {
      // Update the row in place: a fresh element would replay the fade-in on every streamed
      // token, so the reply looked faded until it finished.
      existing.el.className = el.className;
      existing.el.replaceChildren(...el.childNodes);
      existing.sig = sig;
      return existing.el;
    }
    rows.set(m.id, { el, sig });
    return el;
  }

  function userRow(m, last) {
    const el = h('div.msg.user');
    const imgs = m.attachments.filter((a) => a.kind === 'image');
    const files = m.attachments.filter((a) => a.kind !== 'image');
    if (imgs.length) el.append(h('div.attachment-images', imgs.map((a) => h('img', { src: fileUrl(a.path), alt: a.filename, onclick: () => openImage(a.path) }))));
    if (files.length) el.append(h('div.row.wrap', { style: { justifyContent: 'flex-end' } }, files.map((a) => attChip(a))));
    if (editing === m.id) {
      const ta = h('textarea.textarea', { value: m.content });
      const save = () => { editing = null; call('editResend', { chatId, messageId: m.id, text: ta.value }).catch(errorToast); };
      ta.addEventListener('keydown', (e) => {
        if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); save(); }
        if (e.key === 'Escape') { e.preventDefault(); editing = null; renderAll(); }
      });
      el.append(h('div.edit-box', ta, h('div.row', { style: { justifyContent: 'flex-end' } },
        h('button.btn.sm', { onclick: () => { editing = null; renderAll(); } }, 'Cancel'),
        h('button.btn.sm.primary', { onclick: save }, 'Send'))));
      setTimeout(() => { ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length); autosize(ta); }, 0);
      ta.addEventListener('input', () => autosize(ta));
      return el;
    }
    if (m.content) el.append(h('div.bubble.selectable', m.content));
    el.append(h('div.msg-actions',
      !chat.generating ? iconButton('edit', 'Edit and resend', () => { editing = m.id; renderAll(); }, 'sm') : null,
      iconButton('copy', 'Copy', () => copyText(m.content), 'sm')));
    return el;
  }

  function assistantRow(m, live, last, answered) {
    const el = h('div.msg.assistant');
    const hasCalls = m.toolCalls?.length > 0;
    if (live && !m.content && !hasCalls) {
      el.append(!m.hasReasoning && !m.status ? h('div.typing', h('i'), h('i'), h('i')) : thinking(m));
    } else if (m.content && m.thinkingSeconds >= 1) {
      el.append(h('div.thought', `Thought for ${Math.round(m.thinkingSeconds)}s`));
    }
    if (m.content) {
      if (m.isError) el.append(h('div.error-box.selectable', ic('alertCircle'), h('div', m.content)));
      else el.append(h('div.md', { html: renderMarkdown(m.content) }));
    }
    if (live && m.content && m.status) el.append(thinking(m));
    for (const c of m.toolCalls || []) {
      const [iconName, text] = describe(c);
      el.append(h('div.tool-call', { class: chat.generating && !answered.has(c.id) ? 'running' : '' }, ic(iconName), h('span.text', { title: text }, text)));
    }
    if (m.content && !live && !hasCalls && !m.isError) {
      const tokens = m.completionTokens ? `${m.completionTokens.toLocaleString()} tokens` : '';
      el.append(h('div.msg-actions',
        iconButton('copy', 'Copy', () => copyText(m.content), 'sm'),
        last && !chat.generating ? iconButton('refresh', 'Retry', () => call('regenerate', { chatId }), 'sm') : null,
        'speechSynthesis' in window ? iconButton('play', 'Read aloud', () => speak(m.content), 'sm') : null,
        m.modelName ? h('span.meta', m.modelName) : null,
        tokens ? h('span.meta', { title: m.promptTokens ? `${m.promptTokens.toLocaleString()} prompt tokens` : '' }, '· ' + tokens) : null));
    } else if (m.isError && last && !chat.generating) {
      el.append(h('div.msg-actions', h('button.btn.xs', { onclick: () => call('regenerate', { chatId }) }, ic('refresh', 'sm'), 'Try again'),
        h('button.btn.xs.ghost', { onclick: () => select('models') }, 'Switch model')));
    }
    return el;
  }

  function thinking(m) {
    const secs = h('span.secs', { 'data-since': m.createdAt });
    secs.textContent = elapsed(m.createdAt);
    return h('div.thinking', h('span.dot.pulse'), h('span', m.status || 'Thinking'), secs);
  }

  function toolRow(m) {
    const el = h('div.tool-out');
    const pre = h('pre.selectable', m.content);
    pre.style.display = 'none';
    const toggle = h('button.toggle', ic('chevronRight'), h('span', 'Output'));
    toggle.onclick = () => {
      const open = pre.style.display === 'none';
      pre.style.display = open ? '' : 'none';
      toggle.classList.toggle('open', open);
      toggle.lastChild.textContent = open ? 'Hide output' : 'Output';
    };
    el.append(toggle, pre);
    for (const p of m.images || []) {
      const img = h('img.gen', { src: fileUrl(p), onclick: () => openImage(p) });
      img.oncontextmenu = (e) => {
        e.preventDefault();
        menu(null, [
          { label: 'Open', icon: 'external', onclick: () => call('openPath', { path: p }) },
          { label: revealLabel(), icon: 'folderOpen', onclick: () => call('revealPath', { path: p }) },
          { label: 'Copy Path', icon: 'copy', onclick: () => copyText(p) },
        ], { x: e.clientX, y: e.clientY });
      };
      el.append(img);
    }
    for (const p of m.presentedFiles || []) el.append(fileCard(p));
    for (const link of m.links || []) {
      el.append(h('div.link-row', ic('external', 'sm'), h('a', { href: '#', 'data-href': link }, link),
        store.app?.platform?.browser ? h('button.btn.xs', { onclick: () => call('browserNavigate', { url: link }) }, 'Open in panel') : null));
    }
    return el;
  }

  function fileCard(p) {
    return h('div.file-card',
      h('div.ficon', (ext(p) || 'file').slice(0, 4).toUpperCase()),
      h('div.grow', h('div.ellipsis', { style: { fontWeight: 500 } }, basename(p)), h('div.tiny.faint.ellipsis', { title: p }, p)),
      h('button.btn.sm', { onclick: () => preview(p) }, 'Preview'),
      h('button.btn.sm', { onclick: () => call('openPath', { path: p }).catch(errorToast) }, 'Open'),
      iconButton('folderOpen', revealLabel(), () => call('revealPath', { path: p }), 'sm'));
  }

  function scrollToBottom(force) {
    if (!stick && !force) return;
    requestAnimationFrame(() => { scroller.scrollTop = scroller.scrollHeight; });
  }

  function tick() {
    const live = chat?.generating;
    if (live && !timer) {
      timer = setInterval(() => {
        for (const s of inner.querySelectorAll('.secs[data-since]')) s.textContent = elapsed(s.dataset.since);
      }, 1000);
    } else if (!live && timer) { clearInterval(timer); timer = null; }
  }

  // MARK: Composer

  function buildComposer() {
    const el = h('div.composer');
    const attRow = h('div.att-row');
    const ta = h('textarea', { rows: 1, placeholder: 'Ask Synapse anything', spellcheck: 'true' });
    const bar = h('div.composer-bar');
    el.append(attRow, ta, bar);
    const fileInput = h('input', { type: 'file', multiple: true, style: { display: 'none' }, onchange: (e) => { addFiles(e.target.files); e.target.value = ''; } });
    el.append(fileInput);

    ta.value = drafts.get(chatId) || '';
    ta.addEventListener('input', () => { autosize(ta); drafts.set(chatId, ta.value); saveDrafts(); updateSend(); });
    ta.addEventListener('keydown', (e) => {
      if (e.key !== 'Enter' || e.isComposing) return;
      const enterSends = store.app?.settings?.sendWithEnter !== false;
      if ((enterSends && !e.shiftKey && !e.altKey) || (!enterSends && (e.ctrlKey || e.metaKey))) {
        e.preventDefault();
        if (!chat?.generating) send();
      }
    });
    ta.addEventListener('paste', (e) => {
      const files = [...(e.clipboardData?.files || [])];
      if (files.length) { e.preventDefault(); addFiles(files); }
    });

    const sendBtn = h('button.round-btn.send', { title: 'Send (Enter)' });
    sendBtn.onclick = () => (chat?.generating ? call('stop', { chatId }) : send());

    function atts() { return pendingAtts.get(chatId) || []; }
    function canSend() { return ta.value.trim().length > 0 || atts().some((a) => !a.loading); }
    function updateSend() {
      const gen = chat?.generating;
      sendBtn.className = 'round-btn send' + (gen ? ' stop' : canSend() ? ' ready' : '');
      sendBtn.innerHTML = '';
      sendBtn.append(ic(gen ? 'stop' : 'arrowUp'));
      sendBtn.title = gen ? 'Stop (Ctrl+.)' : 'Send (Enter)';
    }

    function renderAtts() {
      clear(attRow);
      const list = atts();
      attRow.style.display = list.length ? '' : 'none';
      for (const a of list) {
        attRow.append(a.loading ? h('div.att-chip.loading', h('span.spinner'), h('span.name', a.filename))
          : attChip(a, () => { pendingAtts.set(chatId, atts().filter((x) => x !== a)); renderAtts(); updateSend(); }));
      }
    }

    function renderBar() {
      clear(bar);
      if (!chat) return;
      const plus = h('button.round-btn', { title: 'Attach files and choose what the model can do' }, ic('plus'));
      plus.onclick = () => toolsMenu(plus);
      const think = THINK.find((t) => t[0] === chat.think) || THINK[1];
      const thinkBtn = h('button.pill', { class: chat.think === 'high' || chat.think === 'max' ? 'accent' : '', title: 'How hard the model thinks' }, ic(think[2], 'sm'), h('span.label', think[1]));
      thinkBtn.onclick = () => menu(thinkBtn, THINK.map(([v, label, i, help]) => ({
        label, sub: help, icon: i, checked: chat.think === v, onclick: () => call('setChat', { chatId, think: v }),
      })), { above: true });
      const caps = h('div.caps');
      for (const [key, label, i] of CAPS) {
        if (!chat.capabilities[key]) continue;
        caps.append(h('button.cap-on', {
          title: `${label} is on. Click to turn off.`,
          onclick: () => call('setChat', { chatId, capabilities: { ...chat.capabilities, [key]: false } }),
        }, ic(i)));
      }
      if (chat.autoApprove && (chat.capabilities.computerAccess || chat.capabilities.browser)) {
        caps.append(h('button.cap-on', { title: 'Actions run without asking. Click to ask again.', style: { color: 'var(--orange)', background: 'rgba(245,165,36,.12)' },
          onclick: () => call('setChat', { chatId, autoApprove: false }) }, ic('zap')));
      }
      const model = chat.model || store.app?.defaultModel;
      const modelBtn = h('button.pill', { title: model ? model.id : 'Choose a model' },
        ic(model?.provider === 'local' ? 'cpu' : 'cloud', 'sm'), h('span.label', model?.name || 'Choose model'), ic('chevronDown', 'sm'));
      modelBtn.onclick = () => modelMenu(modelBtn);
      bar.append(plus, thinkBtn, caps, h('span.spacer'), modelBtn, sendBtn);
      updateSend();
    }

    function toolsMenu(anchor) {
      const app = store.app;
      const items = [
        { label: 'Add Files or Photos…', icon: 'paperclip', onclick: pickFiles },
        { section: 'Capabilities' },
        ...CAPS.map(([key, label, i, sub]) => {
          const unavailable = key === 'browser' && !app.platform.browser;
          return {
            label, icon: i, sub: unavailable ? 'Available in the Windows app' : sub, keep: true, disabled: unavailable,
            checked: !!chat.capabilities[key],
            onclick: () => { chat.capabilities[key] = !chat.capabilities[key]; call('setChat', { chatId, capabilities: chat.capabilities }); renderBar(); },
          };
        }),
      ];
      if (chat.capabilities.computerAccess || chat.capabilities.browser) {
        items.push('sep', {
          label: 'Run Actions Without Asking', icon: 'zap', keep: true, checked: chat.autoApprove,
          sub: 'Commands, file writes and screenshots run immediately', onclick: () => { chat.autoApprove = !chat.autoApprove; call('setChat', { chatId, autoApprove: chat.autoApprove }); renderBar(); },
        });
      }
      menu(anchor, items, { above: true, width: 300 });
    }

    function modelMenu(anchor) {
      const app = store.app;
      const cur = chat.model || app.defaultModel;
      const isCur = (m) => cur && cur.provider === m.provider && cur.id === m.id;
      const items = [];
      if (app.localModels.length) {
        items.push({ section: 'On this PC' });
        for (const m of app.localModels) items.push({ label: m.displayName, icon: 'cpu', checked: isCur(m.ref), search: m.repo, onclick: () => setModel(m.ref) });
      }
      for (const p of app.providers) {
        const list = app.remoteModels[p.id] || [];
        if (!list.length) continue;
        items.push({ section: p.name });
        for (const m of list.slice(0, 80)) items.push({ label: m.name, icon: m.vision ? 'eye' : 'cloud', checked: isCur(m), search: m.id, onclick: () => setModel(m) });
      }
      if (!items.length) items.push({ label: 'No models yet', sub: 'Add a free API key or download a local model', disabled: true });
      items.push('sep',
        { label: 'Get More Models…', icon: 'download', onclick: () => select('models') },
        { label: 'Accounts & API Keys…', icon: 'key', onclick: () => import('./accounts.js').then((a) => a.openAccounts()) });
      menu(anchor, items, { search: 'Search models', above: true, align: 'right', width: 340 });
    }

    function setModel(m) {
      chat.model = m;
      call('setChat', { chatId, model: m });
      renderBar();
    }

    async function pickFiles() {
      if (isWebView) {
        try { addAttachments(await call('attachPick')); } catch (e) { errorToast(e); }
      } else fileInput.click();
    }

    return {
      el,
      update() { renderBar(); renderAtts(); autosize(ta); },
      focus() { ta.focus(); },
      setText(t) { ta.value = t; autosize(ta); drafts.set(chatId, t); updateSend(); },
      text() { return ta.value; },
      clear() { ta.value = ''; autosize(ta); drafts.delete(chatId); saveDrafts(); pendingAtts.delete(chatId); renderAtts(); updateSend(); },
      renderAtts, updateSend,
    };
  }

  function addAttachments(list) {
    if (!list?.length) return;
    pendingAtts.set(chatId, [...(pendingAtts.get(chatId) || []).filter((a) => !a.loading), ...list]);
    composer.renderAtts();
    composer.updateSend();
  }

  async function addFiles(fileList) {
    const files = [...fileList];
    if (!files.length) return;
    // WebView2 gives the host real paths for dropped files; pasted clipboard data has none.
    const withPaths = await sendFiles(files, 'attachPaths').catch(() => null);
    if (Array.isArray(withPaths) && withPaths.length) { addAttachments(withPaths); return; }
    const placeholders = files.map((f) => ({ filename: f.name || 'Pasted image', loading: true }));
    pendingAtts.set(chatId, [...(pendingAtts.get(chatId) || []), ...placeholders]);
    composer.renderAtts();
    const done = [];
    for (const f of files) {
      if (f.size > 60_000_000) { toast(`${f.name} is too large to attach.`, 'error'); continue; }
      try {
        const name = f.name && f.name !== 'image.png' ? f.name : `pasted-${Date.now()}.${(f.type.split('/')[1] || 'png').replace('jpeg', 'jpg')}`;
        const a = await call('attachData', { name, base64: await readAsBase64(f) });
        if (a) done.push(a);
      } catch (e) { errorToast(e); }
    }
    pendingAtts.set(chatId, (pendingAtts.get(chatId) || []).filter((a) => !placeholders.includes(a)));
    addAttachments(done);
  }

  function send() {
    const text = composer.text();
    const atts = (pendingAtts.get(chatId) || []).filter((a) => !a.loading);
    if (!text.trim() && !atts.length) return;
    if (!chat?.model && !store.app?.defaultModel) {
      toast('Pick a model first: add a free API key (key icon, top right) or download a local model in Models.', 'error', 7000);
      return;
    }
    stick = true;
    call('send', { chatId, text, attachments: atts }).catch(errorToast);
    composer.clear();
  }

  // MARK: Instance

  const instance = {
    setChat,
    updateMessage,
    update() { composer.update(); },
    focus() { composer.focus(); },
    title() {
      const c = chat || store.app?.chats.find((x) => x.id === chatId);
      const title = c?.title || 'New Chat';
      const more = iconButton('more', 'Chat options', () => menu(more, [
        { label: 'Rename…', icon: 'edit', onclick: () => renameChat({ id: chatId, title }) },
        { label: c?.pinned ? 'Unpin' : 'Pin', icon: 'pin', onclick: () => call('pinChat', { chatId, pinned: !c?.pinned }) },
        { label: 'Duplicate', icon: 'duplicate', onclick: () => call('duplicateChat', { chatId }) },
        { label: 'Export as Markdown…', icon: 'share', onclick: () => exportChat(chatId) },
        'sep',
        { label: 'Delete Chat', icon: 'trash', danger: true, onclick: () => deleteChat({ id: chatId, title, empty: !chat?.messages.length }) },
      ], { align: 'right' }));
      return { title, subtitle: chat?.projectName || '', actions: chat?.messages.length ? [more] : [] };
    },
    unmount() { if (timer) clearInterval(timer); speechSynthesis?.cancel?.(); },
  };
  layout();
  composer.update();
  setTimeout(() => composer.focus(), 0);
  return instance;
}

function attChip(a, onRemove) {
  return h('div.att-chip', { title: a.path, onclick: () => (a.kind === 'image' ? openImage(a.path) : preview(a.path)) },
    a.kind === 'image' ? h('img', { src: fileUrl(a.path) }) : ic(a.kind === 'pdf' ? 'fileText' : 'file', 'sm'),
    h('span.name', a.filename),
    onRemove ? h('button.x', { title: 'Remove', onclick: (e) => { e.stopPropagation(); onRemove(); } }, ic('x')) : null);
}

export function autosize(ta) {
  ta.style.height = 'auto';
  ta.style.height = Math.min(ta.scrollHeight, 300) + 'px';
}

function elapsed(since) {
  const s = Math.max(0, Math.floor((Date.now() - new Date(since).getTime()) / 1000));
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`;
}

function speak(text) {
  const synth = window.speechSynthesis;
  if (synth.speaking) { synth.cancel(); return; }
  const plain = text.replace(/```[\s\S]*?```/g, ' (code) ').replace(/[#*_`>|]/g, '').replace(/\[([^\]]+)\]\([^)]+\)/g, '$1');
  synth.speak(new SpeechSynthesisUtterance(plain));
}

/** Short description of a tool call, like "Searched “weather in Ankara”". */
export function describe(c) {
  let a = {};
  try { a = JSON.parse(c.arguments || '{}'); } catch {}
  const v = (k) => (a[k] == null ? '' : String(a[k]));
  switch (c.name) {
    case 'web_search': return ['search', `Searched “${v('query')}”`];
    case 'fetch_url': return ['fileText', `Read ${host(v('url'))}`];
    case 'generate_image': return ['image', 'Generated an image'];
    case 'run_shell': return ['terminal', v('command')];
    case 'open': return ['external', `Opened ${v('target')}`];
    case 'take_screenshot': return ['camera', 'Took a screenshot'];
    case 'get_clipboard': return ['clipboard', 'Read the clipboard'];
    case 'set_clipboard': return ['clipboard', 'Copied to the clipboard'];
    case 'list_files': return ['folder', `Listed ${v('path') || 'files'}`];
    case 'read_file': return ['file', `Read ${basename(v('path'))}`];
    case 'write_file': return ['compose', `Wrote ${basename(v('path'))}`];
    case 'edit_file': return ['edit', `Edited ${basename(v('path'))}`];
    case 'search_files': return ['search', `Searched files for “${v('pattern')}”`];
    case 'present_file': return ['eye', `Shared ${basename(v('path'))}`];
    case 'browser_navigate': return ['globe', `Opened ${host(v('url'))}`];
    case 'browser_read': return ['fileText', 'Read the page'];
    case 'browser_click': return ['pointer', 'Clicked'];
    case 'browser_type': return ['keyboard', `Typed “${v('text')}”`];
    case 'browser_scroll': return ['scroll', `Scrolled ${v('direction')}`];
    case 'browser_back': return ['back', 'Went back'];
    case 'browser_screenshot': return ['camera', 'Looked at the page'];
    case 'create_website': return ['code', `Built “${v('name')}”`];
    default: return ['wrench', c.name];
  }
}
