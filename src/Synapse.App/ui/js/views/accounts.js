// Accounts & API keys: sign in to free providers, paste keys (stored in Windows Credential Manager).
import { call } from '../bridge.js';
import { h, ic, clear, modal, errorToast, toast, toggleSwitch } from '../ui.js';
import { store, onApp } from '../app.js';

const ICONS = { google: 'sparkle', groq: 'zap', openrouter: 'branch', custom: 'server' };
const PRESETS = [
  ['Ollama', 'http://localhost:11434/v1'],
  ['LM Studio', 'http://localhost:1234/v1'],
  ['OpenAI', 'https://api.openai.com/v1'],
  ['Mistral', 'https://api.mistral.ai/v1'],
  ['Together', 'https://api.together.xyz/v1'],
  ['DeepSeek', 'https://api.deepseek.com/v1'],
];

let open = null;

export function openAccounts() {
  if (open) return;
  const body = h('div.modal-body', { style: { gap: '12px' } });
  const state = new Map();   // provider -> {editing, result, ok, testing}
  const m = modal({
    cls: 'wide',
    title: 'Accounts & API Keys',
    subtitle: `Sign in to a provider, create a free key, and paste it here. Keys are stored in ${store.app.platform.isWindows ? 'Windows Credential Manager' : 'your user folder'}.`,
    body,
    foot: [h('button.btn.primary', { onclick: () => m.close() }, 'Done')],
    onclose: () => { off(); open = null; },
  });
  open = m;

  const st = (id) => { if (!state.has(id)) state.set(id, {}); return state.get(id); };

  function render() {
    const app = store.app;
    const keep = document.activeElement && body.contains(document.activeElement) ? document.activeElement.dataset.key : null;
    const values = Object.fromEntries([...body.querySelectorAll('input[data-key]')].map((i) => [i.dataset.key, i.value]));
    clear(body);
    for (const p of app.providers) body.append(p.id === 'custom' ? customRow(p, app) : providerRow(p));
    body.append(hfRow(app));
    for (const i of body.querySelectorAll('input[data-key]')) if (values[i.dataset.key]) i.value = values[i.dataset.key];
    if (keep) body.querySelector(`input[data-key="${keep}"]`)?.focus();
  }

  function providerRow(p) {
    const s = st(p.id);
    const row = h('div.provider');
    row.append(h('div.row',
      h('div.pico', ic(ICONS[p.id])),
      h('div.grow', h('div', { style: { fontWeight: 600 } }, p.name), h('div.tiny.faint', p.note)),
      p.hasKey ? [h('span.connected', ic('check', 'sm'), 'Connected'), h('span.tiny.faint', `${p.count} models`)] : null));
    if (!p.hasKey || s.editing) {
      const input = h('input.input', { type: 'password', placeholder: `Paste your ${p.name} API key`, 'data-key': p.id, autocomplete: 'off', spellcheck: 'false' });
      const save = async () => {
        const key = input.value.trim();
        if (!key) return;
        s.editing = false;
        await call('setKey', { provider: p.id, key });
        test(p.id);
      };
      input.addEventListener('keydown', (e) => { if (e.key === 'Enter') save(); });
      row.append(
        h('div.row', p.keyPage ? h('button.btn.primary.sm', { onclick: () => call('openExternal', { url: p.keyPage }) }, ic('user', 'sm'), 'Sign In & Get Key') : null,
          h('span.tiny.muted', p.steps)),
        h('div.row', input,
          h('button.btn', { onclick: async () => { try { input.value = (await navigator.clipboard.readText()).trim(); } catch { toast('Press Ctrl+V in the field to paste.', 'info'); } } }, 'Paste'),
          h('button.btn', { onclick: save }, 'Save'),
          s.editing ? h('button.btn.ghost', { onclick: () => { s.editing = false; render(); } }, 'Cancel') : null));
    } else {
      row.append(h('div.row',
        h('button.btn.sm', { onclick: () => test(p.id) }, 'Test'),
        h('button.btn.sm', { onclick: () => { s.editing = true; render(); } }, 'Replace Key'),
        p.usagePage ? h('a.small', { href: '#', 'data-href': p.usagePage }, 'Open dashboard') : null,
        h('span.spacer'),
        h('button.btn.sm.danger', { onclick: () => { s.result = null; call('setKey', { provider: p.id, key: null }); } }, 'Sign Out')));
    }
    if (s.testing) row.append(h('div.row', h('span.spinner'), h('span.tiny.muted', 'Testing…')));
    if (s.result) row.append(h('div.tiny.selectable', { class: s.ok ? 'ok' : 'err' }, s.result));
    return row;
  }

  async function test(id) {
    const s = st(id);
    s.testing = true;
    render();
    try {
      const r = await call('testKey', { provider: id });
      s.ok = r.ok;
      s.result = r.message;
    } catch (e) { s.ok = false; s.result = e.message; }
    s.testing = false;
    render();
  }

  function customRow(p, app) {
    const row = h('div.provider');
    const enabled = app.settings.customEnabled;
    row.append(h('div.row',
      h('div.pico', ic('server')),
      h('div.grow', h('div', { style: { fontWeight: 600 } }, 'Custom server'),
        h('div.tiny.faint', 'OpenAI, Mistral, Together, LM Studio, Ollama, or any OpenAI-compatible endpoint.')),
      toggleSwitch(enabled, (v) => call('setCustom', { enabled: v }).catch(errorToast))));
    if (enabled) {
      const url = h('input.input.mono', { value: app.settings.customBaseUrl, placeholder: 'Base URL (ends in /v1)', 'data-key': 'custom-url', spellcheck: 'false' });
      const key = h('input.input', { type: 'password', placeholder: p.hasKey ? 'Key saved (leave blank to keep)' : 'API key (optional)', 'data-key': 'custom-key', autocomplete: 'off' });
      const save = () => call('setCustom', { url: url.value.trim(), enabled: true, key: key.value.trim() }).then(() => toast('Saved', 'ok', 1500), errorToast);
      row.append(
        h('div.presets', PRESETS.map(([name, u]) => h('button.btn.xs', { onclick: () => { url.value = u; } }, name))),
        h('div.row', url, key, h('button.btn', { onclick: save }, 'Save'),
          p.hasKey ? h('button.btn.ghost.sm', { title: 'Forget the saved key', onclick: () => call('setCustom', { clearKey: true }) }, 'Clear Key') : null));
      const err = app.remoteErrors.custom;
      if (err) row.append(h('div.tiny.err.selectable', err));
      else if (app.remoteModels.custom) row.append(h('div.tiny.ok', `${app.remoteModels.custom.length} models`));
    }
    return row;
  }

  function hfRow(app) {
    const row = h('div.provider');
    const input = h('input.input', { type: 'password', placeholder: app.hfToken ? 'Token saved' : 'hf_…', 'data-key': 'hf', autocomplete: 'off' });
    row.append(
      h('div.row', h('div.pico', '🤗'),
        h('div.grow', h('div', { style: { fontWeight: 600 } }, 'HuggingFace (optional)'),
          h('div.tiny.faint', 'Only needed for gated models (e.g. some Llama and Gemma repos) and faster downloads.')),
        app.hfToken ? h('span.connected', ic('check', 'sm'), 'Connected') : null),
      h('div.row',
        h('button.btn.sm', { onclick: () => call('openExternal', { url: 'https://huggingface.co/settings/tokens' }) }, ic('user', 'sm'), 'Sign In & Get Token'),
        input,
        h('button.btn', { onclick: () => { if (input.value.trim()) call('setHfToken', { token: input.value.trim() }); } }, 'Save'),
        app.hfToken ? h('button.btn.danger', { onclick: () => call('setHfToken', { token: null }) }, 'Remove') : null));
    return row;
  }

  render();
  const off = onApp(render);
}
