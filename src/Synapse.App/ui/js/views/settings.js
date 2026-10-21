// Settings, grouped like the Mac app plus Windows-specific options.
import { call } from '../bridge.js';
import { h, ic, clear, toggleSwitch, debounce, errorToast, toast, html } from '../ui.js';
import { logo } from '../icons.js';
import { store } from '../app.js';
import { setTab } from './models.js';

let section = 'general';
const SECTIONS = [
  ['general', 'General', 'gear'],
  ['appearance', 'Appearance & Windows', 'palette'],
  ['models', 'Models', 'cpu'],
  ['computer', 'Computer Access', 'monitor'],
  ['images', 'Image Generation', 'image'],
  ['terminal', 'Terminal & API', 'terminal'],
  ['about', 'About', 'info'],
];

export function mount(container) {
  const page = h('div.page');
  const inner = h('div.page-inner');
  page.append(inner);
  container.append(page);
  const nav = h('nav');
  const body = h('div');
  inner.append(h('div.page-head', h('div.grow', h('h2', 'Settings'))), h('div.settings', nav, body));
  const set = (patch) => call('setSettings', patch).catch(errorToast);

  function renderNav() {
    clear(nav);
    for (const [id, label, i] of SECTIONS) nav.append(h('button', { class: section === id ? 'on' : '', onclick: () => { section = id; renderNav(); renderBody(); } }, ic(i), label));
  }

  const row = (title, hint, control) => h('div.set-row', h('div.text', h('div', title), hint ? h('div.hint', hint) : null), control);
  const group = (title, ...rows) => h('div.set-group', title ? h('h4', title) : null, ...rows);
  const sw = (key) => toggleSwitch(!!store.app.settings[key], (v) => set({ [key]: v }));
  const select = (key, options, width = 240) => {
    const s = h('select.select', { style: { width: width + 'px' }, onchange: (e) => set({ [key]: isNaN(+options[0][0]) || typeof options[0][0] === 'string' ? e.target.value : +e.target.value }) },
      options.map(([v, l]) => h('option', { value: v }, l)));
    s.value = String(store.app.settings[key]);
    return s;
  };

  function renderBody() {
    const s = store.app.settings;
    const p = store.app.platform;
    clear(body);
    switch (section) {
      case 'general': {
        const ta = h('textarea.textarea', { value: s.systemPrompt, placeholder: 'e.g. “Answer in Turkish unless I write in English. Keep answers short.”', style: { minHeight: '120px' } });
        ta.addEventListener('input', debounce(() => set({ systemPrompt: ta.value }), 500));
        body.append(
          group('Custom instructions (all chats)', h('div.set-row.stack', h('div.hint', 'Added to every chat, after project instructions.'), ta)),
          group('Chat',
            row('Send with Enter', 'Off: Enter adds a new line and Ctrl+Enter sends.', sw('sendWithEnter')),
            row('Notify when a reply finishes', 'Shows a Windows notification when Synapse is in the background.', sw('notifyWhenDone'))));
        break;
      }
      case 'appearance':
        body.append(
          group('Appearance', row('Theme', null, select('theme', [['dark', 'Dark'], ['light', 'Light'], ['system', 'Match Windows']]))),
          group('Windows',
            row('Keep running in the tray when closed', 'The local API and hosted websites stay available. Quit from the tray icon.', sw('closeToTray')),
            row('Start with Windows', 'Starts minimized to the tray when you sign in.', sw('launchAtStartup')),
            row('Global shortcut', 'Ctrl+Alt+Space brings Synapse to the front from anywhere.', sw('globalHotkey'))));
        if (!p.isWindows) body.append(h('p.hint', 'Windows options apply in the Windows app.'));
        break;
      case 'models':
        body.append(group('Local models',
          row('Context length', 'Longer context uses more memory. 8K is a good default on 8 GB GPUs.',
            select('contextSize', [[4096, '4,096 tokens'], [8192, '8,192 tokens'], [16384, '16,384 tokens'], [32768, '32,768 tokens'], [65536, '65,536 tokens'], [131072, '131,072 tokens']])),
          row('GPU layers', 'Automatic fits as many layers as free VRAM allows.',
            select('gpuLayers', [['auto', 'Automatic'], ['all', 'All on GPU'], ['0', 'CPU only']])),
          row('Engine', 'Install or update llama.cpp, pick the GPU build, and see logs.',
            h('button.btn', { onclick: () => { setTab('engine'); import('../app.js').then((a) => a.select('models')); } }, 'Open Engine'))));
        break;
      case 'computer':
        body.append(group('Computer access',
          row('Shell', 'Used by run_shell. PowerShell 7 (pwsh) is used when installed and set to Automatic.',
            select('shell', p.isWindows ? [['auto', 'Automatic'], ['pwsh', 'PowerShell 7'], ['powershell', 'Windows PowerShell 5.1'], ['cmd', 'Command Prompt']] : [['auto', 'Automatic (bash)']])),
          row('Command timeout', 'Commands still running after this are stopped.',
            select('shellTimeout', [[30, '30 seconds'], [60, '1 minute'], [120, '2 minutes'], [300, '5 minutes'], [600, '10 minutes'], [1800, '30 minutes']]))),
          h('p.hint', 'Turn on Computer Access per chat from the + menu. Every command, file write outside the project, and screenshot asks first unless you allow it for that chat.'));
        break;
      case 'images': {
        const gem = h('input.input', { value: s.geminiImageModel, style: { width: '240px' } });
        gem.addEventListener('change', () => set({ geminiImageModel: gem.value.trim() }));
        const oai = h('input.input', { value: s.openaiImageModel, style: { width: '240px' } });
        oai.addEventListener('change', () => set({ openaiImageModel: oai.value.trim() }));
        body.append(group('Image generation',
          row('Provider', null, select('imageProvider', [['pollinations', 'Pollinations (free, no key)'], ['gemini', 'Google Gemini (AI Studio key)'], ['openai', 'Custom server (OpenAI images API)']], 300)),
          s.imageProvider === 'gemini' ? row('Gemini image model', 'Image models may not be included in the free tier.', gem) : null,
          s.imageProvider === 'openai' ? row('Image model', 'Sent to the custom server’s /images/generations (set its address in Accounts).', oai) : null));
        break;
      }
      case 'terminal': {
        const port = h('input.input.mono', { value: s.apiServerPort, style: { width: '110px' }, type: 'number', min: 1024, max: 65535 });
        port.addEventListener('change', () => { const v = +port.value; if (v >= 1024 && v <= 65535) set({ apiServerPort: v }); });
        const out = h('pre.detail-box', { style: { display: 'none' } });
        body.append(
          group('Terminal',
            row('synapse command', 'Use any model from a terminal: synapse chat, synapse models, synapse run <model> "prompt".',
              h('button.btn', { onclick: async () => { try { out.textContent = await call('installCli'); out.style.display = ''; } catch (e) { errorToast(e); } } }, ic('terminal', 'sm'), 'Add to PATH')),
            h('div.set-row.stack', out)),
          group('Local API server',
            row(`Serve all models on ${store.app.api.url || `http://127.0.0.1:${s.apiServerPort}/v1`}`, 'OpenAI-compatible. Point editors, scripts and other apps here to use your local and cloud models. Only reachable from this PC.', sw('apiServerEnabled')),
            row('Port', store.app.api.error || null, port)));
        break;
      }
      case 'about':
        body.append(h('div.about', html(logo(84)), h('h2', 'Synapse'), h('div.muted', `Version ${store.app.version} for Windows`),
          h('div.small.muted', 'Run AI models locally or through free APIs.'),
          h('div.small.faint', 'Copyright © 2026 mertyesileducation · MIT License'),
          h('div.row', { style: { marginTop: '10px' } },
            h('button.btn.sm', { onclick: () => call('openExternal', { url: 'https://github.com/Agamemnon-Intelligence/Synapse-windows' }) }, ic('external', 'sm'), 'GitHub'),
            h('button.btn.sm', { onclick: () => call('revealPath', { path: p.dataRoot }) }, ic('folderOpen', 'sm'), 'Data Folder')),
          h('div.tiny.faint.mono', { style: { marginTop: '6px' } }, p.os)));
        break;
    }
  }

  renderNav();
  renderBody();
  let last = JSON.stringify(store.app.settings);
  return {
    update() {
      const now = JSON.stringify(store.app.settings);
      if (now === last) return;
      last = now;
      // Don't rebuild under the user's cursor.
      if (!body.contains(document.activeElement) || document.activeElement.tagName === 'SELECT' || document.activeElement.type === 'checkbox') renderBody();
    },
    title: () => ({ title: 'Settings' }),
  };
}
