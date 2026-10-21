// Models: curated picks checked against this PC, HuggingFace search, downloaded models, cloud models, and the llama.cpp engine.
import { call } from '../bridge.js';
import { h, ic, clear, iconButton, menu, modal, segmented, errorToast, toast, formatBytes, compact, debounce, copyText } from '../ui.js';
import { store, select, newChat, onApp } from '../app.js';
import { revealLabel } from './preview.js';
import { openAccounts } from './accounts.js';

let tab = 'recommended';
let rerender = null;
export function setTab(t) { tab = t; rerender?.(); }

const FIT = {
  great: ['green', 'Runs great', 'Fits comfortably. Plenty of memory left for other apps.'],
  good: ['mint', 'Runs well', 'Fits. Close heavy apps for best speed.'],
  tight: ['orange', 'Tight fit', 'May be slow or swap. Quit other apps first.'],
  tooLarge: ['red', 'Too large', 'Needs more memory than this PC has.'],
};
const FIT_ORDER = { great: 0, good: 1, tight: 2, tooLarge: 3 };
export function fitBadge(fit) {
  const [color, label, detail] = FIT[fit] || FIT.good;
  return h('span.fit', { title: detail }, h('span.dot', { class: color }), label);
}

// Variants are fetched once per session.
const variantCache = new Map();
let catalog = null;
async function loadVariants(repo) {
  if (variantCache.has(repo)) return variantCache.get(repo);
  const p = call('variants', { repo }).then((r) => ({ ...r }), (e) => ({ error: e.message }));
  variantCache.set(repo, p);
  const r = await p;
  variantCache.set(repo, r);
  return r;
}
function cached(repo) { const v = variantCache.get(repo); return v && !(v instanceof Promise) ? v : null; }

export function mount(container) {
  const page = h('div.page');
  const inner = h('div.page-inner');
  page.append(inner);
  container.append(page);
  const head = h('div');
  const tabsBar = h('div.tabs-bar');
  const body = h('div');
  inner.append(head, tabsBar, body);
  let tabInstance = null;

  function renderHead() {
    const hw = store.app.hardware;
    clear(head).append(h('div.page-head', h('div.grow',
      h('h2', 'Models'),
      h('p', hw.advice),
      h('div.hw-banner',
        h('span.chip', ic('cpu', 'sm'), hw.cpu),
        h('span.chip', ic('layers', 'sm'), `${Math.round(hw.memoryGB)} GB memory`),
        ...hw.gpus.map((g) => h('span.chip', ic('gpu', 'sm'), `${g.name}${g.vramGB ? ` · ${g.vramGB} GB` : ''}`)),
        h('span.chip', ic('drive', 'sm'), `${formatBytes(hw.freeDisk)} free`)))));
  }

  function renderTabs() {
    clear(tabsBar).append(segmented([
      ['recommended', 'Recommended'], ['search', 'Search HuggingFace'], ['mine', `My Models${store.app.localModels.length ? ` (${store.app.localModels.length})` : ''}`],
      ['cloud', 'Cloud Models'], ['engine', 'Engine'],
    ], tab, (t) => { tab = t; renderTabs(); showTab(); }));
  }

  function showTab() {
    tabInstance?.unmount?.();
    clear(body);
    tabInstance = ({ recommended: Recommended, search: Search, mine: Mine, cloud: Cloud, engine: Engine })[tab](body);
  }

  renderHead();
  renderTabs();
  showTab();
  rerender = () => { renderTabs(); showTab(); };

  return {
    update() { renderTabs(); tabInstance?.update?.(); },
    title: () => ({ title: 'Models', subtitle: store.app.engine.available ? '' : 'llama.cpp not installed' }),
    unmount() { tabInstance?.unmount?.(); rerender = null; },
  };
}

// MARK: Download button

export function downloadButton(variant, displayName) {
  const app = store.app;
  const local = app.localModels.find((m) => m.id === variant.id);
  if (local) return h('button.btn.sm.primary', { onclick: () => newChat(null, local.ref) }, ic('chat', 'sm'), 'Chat');
  const d = app.downloads.find((x) => x.id === variant.id);
  if (d) {
    if (d.error) {
      return h('div.row', h('span.tiny.err.ellipsis', { title: d.error, style: { maxWidth: '150px' } }, d.error),
        h('button.btn.xs', { onclick: () => { call('cancelDownload', { id: d.id }); call('download', { variant, name: displayName }).catch(errorToast); } }, 'Retry'));
    }
    return h('div.dl-progress', { title: d.file },
      h('div.progress', h('i', { style: { width: `${Math.round(d.fraction * 100)}%` } })),
      `${Math.round(d.fraction * 100)}%`,
      d.speed ? h('span.faint', `${formatBytes(d.speed)}/s`) : null,
      iconButton('x', 'Cancel download', () => call('cancelDownload', { id: d.id }), 'sm'));
  }
  const tooBig = variant.sizeBytes * 1.02 > app.hardware.freeDisk && app.hardware.freeDisk > 0;
  return h('button.btn.sm', {
    disabled: tooBig, title: tooBig ? 'Not enough free disk space' : `Download ${formatBytes(variant.sizeBytes)}`,
    onclick: () => call('download', { variant, name: displayName }).then(() => toast(`Downloading ${displayName}…`, 'info', 2200), errorToast),
  }, ic('download', 'sm'), 'Download');
}

// MARK: Recommended

function Recommended(body) {
  const grid = h('div.grid');
  const note = h('div.row', { style: { marginBottom: '12px' } }, h('span.muted.small', 'Sorted by how well they run on this PC. Models download from HuggingFace and run privately with llama.cpp.'));
  body.append(note, grid);
  let alive = true;

  async function load() {
    if (!catalog) catalog = await call('catalog').catch(() => []);
    if (!alive) return;
    render();
    // A few at a time keeps HuggingFace happy.
    const queue = catalog.map((e) => e.repo).filter((r) => !cached(r));
    const worker = async () => { while (queue.length && alive) { await loadVariants(queue.shift()); if (alive) render(); } };
    await Promise.all([worker(), worker(), worker(), worker()]);
  }

  function render() {
    if (!catalog) return;
    const scored = catalog.map((e) => {
      const v = cached(e.repo);
      const rec = v?.recommended;
      return { e, v, rec, fit: rec?.fit || 'good' };
    });
    const fitting = scored.filter((s) => s.rec && FIT_ORDER[s.fit] <= 1).sort((a, b) => b.rec.sizeBytes - a.rec.sizeBytes);
    const best = fitting[0]?.e.repo;
    scored.sort((a, b) => {
      if (a.e.repo === best) return -1;
      if (b.e.repo === best) return 1;
      if (FIT_ORDER[a.fit] !== FIT_ORDER[b.fit]) return FIT_ORDER[a.fit] - FIT_ORDER[b.fit];
      return (b.rec?.sizeBytes || 0) - (a.rec?.sizeBytes || 0);
    });
    clear(grid);
    for (const { e, v, rec, fit } of scored) {
      const foot = h('div.foot');
      if (rec) foot.append(fitBadge(fit), h('span.tiny.faint', `${rec.label} · ${formatBytes(rec.sizeBytes)}`), h('span.spacer'), downloadButton(rec, e.title));
      else if (v?.error) foot.append(h('span.tiny.err.ellipsis.grow', { title: v.error }, v.error), h('button.btn.xs', { onclick: () => { variantCache.delete(e.repo); load(); } }, 'Retry'));
      else foot.append(h('span.spinner'), h('span.tiny.faint', 'Checking sizes…'), h('span.spacer'));
      foot.append(iconButton('more', 'Other sizes and quantizations', () => variantsModal(e.repo, e.title), 'sm'));
      grid.append(h('div.model-card', { class: [e.repo === best ? 'best' : '', fit === 'tooLarge' ? 'dim' : ''].join(' ') },
        h('div.row', h('h4.grow', e.title), e.repo === best ? h('span.chip.accent', 'Recommended') : null, h('span.chip.mono', 'GGUF')),
        h('div.blurb', e.blurb),
        h('div.row.wrap', e.tags.map((t) => h('span.chip', t))),
        foot));
    }
  }

  load();
  return { update: render, unmount() { alive = false; } };
}

// MARK: Variants

export async function variantsModal(repo, title) {
  const body = h('div.modal-body');
  const m = modal({ cls: 'wide', title, subtitle: repo, body });
  const link = h('a', { href: '#', 'data-href': `https://huggingface.co/${repo}` }, 'Open on HuggingFace');
  const content = h('div');
  body.append(h('div.hint', 'Smaller quantizations use less memory but lose some quality. Q4_K_M is the usual sweet spot. ', link), content);
  content.append(h('div.row', h('span.spinner'), h('span.muted', 'Loading files…')));
  const r = await loadVariants(repo);
  const render = () => {
    clear(content);
    if (r.error) { content.append(h('div.err', r.error)); return; }
    if (!r.variants.length) { content.append(h('div.muted', 'No GGUF files in this repo.')); return; }
    const table = h('table.variant-table');
    for (const v of r.variants) {
      table.append(h('tr', { class: r.recommended?.id === v.id ? 'rec' : '' },
        h('td.mono', { style: { width: '150px' } }, v.label, r.recommended?.id === v.id ? h('span.chip.accent', { style: { marginLeft: '6px' } }, 'Best') : null),
        h('td.muted', formatBytes(v.sizeBytes)),
        h('td', fitBadge(v.fit)),
        h('td.faint.tiny', v.mmproj ? h('span', { title: 'Includes the vision projector' }, ic('eye', 'sm')) : null, v.files.length > 1 ? ` ${v.files.length} parts` : ''),
        h('td', { style: { textAlign: 'right' } }, downloadButton(v, title))));
    }
    content.append(table);
  };
  render();
  const off = onApp(() => { if (document.body.contains(m.box)) render(); else off(); });
}

// MARK: Search

function Search(body) {
  const input = h('input', { placeholder: 'Search HuggingFace (e.g. qwen, llama, gemma, coder, mistral)', spellcheck: 'false' });
  const status = h('span');
  const bar = h('div.search', { style: { margin: '0 0 14px' } }, ic('search', 'sm'), input);
  const results = h('div.list');
  body.append(h('div.row', bar, status), results);
  let seq = 0;
  const run = async () => {
    const my = ++seq;
    clear(status).append(h('span.spinner'));
    try {
      const list = await call('hfSearch', { query: input.value });
      if (my !== seq) return;
      clear(results);
      if (!list.length) results.append(h('div.empty', h('p', 'No GGUF models found.')));
      for (const r of list) {
        results.append(h('div.list-row', { onclick: () => variantsModal(r.id, r.id.split('/').pop()), style: { cursor: 'default' } },
          ic('cpu'),
          h('div.grow', h('div.ellipsis', r.id.split('/').pop()), h('div.tiny.faint', r.id.split('/')[0])),
          r.pipelineTag ? h('span.chip', r.pipelineTag) : null,
          h('span.tiny.faint', ic('download', 'sm'), ' ', compact(r.downloads)),
          h('span.tiny.faint', '♥ ', compact(r.likes)),
          ic('chevronRight', 'sm')));
      }
    } catch (e) { if (my === seq) { clear(results).append(h('div.err', e.message)); } }
    if (my === seq) clear(status);
  };
  input.addEventListener('input', debounce(run, 350));
  input.addEventListener('keydown', (e) => { if (e.key === 'Enter') run(); });
  input.style.paddingLeft = '32px';
  setTimeout(() => input.focus(), 0);
  run();
  return {};
}

// MARK: My models

function Mine(body) {
  function render() {
    const app = store.app;
    clear(body);
    if (!app.localModels.length && !app.downloads.length) {
      body.append(h('div.empty', ic('cpu'), h('h3', 'No local models yet'), h('p', 'Download one from Recommended. Models run privately on your PC.'),
        h('button.btn.primary', { onclick: () => setTab('recommended') }, 'Browse Recommended')));
      return;
    }
    if (app.downloads.length) {
      body.append(h('div.section-title', 'Downloading'));
      const list = h('div.list');
      for (const d of app.downloads) {
        list.append(h('div.list-row',
          ic('download'),
          h('div.grow', h('div', `${d.name} · ${d.label}`), h('div.tiny.ellipsis', { class: d.error ? 'err' : 'faint' }, d.error || d.file)),
          h('span.tiny.faint.mono', `${formatBytes(d.received)} / ${formatBytes(d.size)}`),
          d.speed && !d.error ? h('span.tiny.faint', `${formatBytes(d.speed)}/s`) : null,
          h('div.progress', { style: { width: '120px' } }, h('i', { style: { width: `${Math.round(d.fraction * 100)}%` } })),
          iconButton('x', 'Cancel', () => call('cancelDownload', { id: d.id }), 'sm')));
      }
      body.append(list);
    }
    if (app.localModels.length) {
      body.append(h('div.section-title', 'On this PC'));
      const list = h('div.list');
      for (const m of app.localModels) {
        const loaded = app.engine.loadedModelId === m.id;
        const loading = loaded && app.engine.state === 'loading';
        const more = iconButton('more', 'More', () => menu(more, [
          { label: revealLabel(), icon: 'folderOpen', onclick: () => call('revealPath', { path: m.directory }) },
          { label: 'Set as Default Model', icon: 'check', checked: app.settingsDefaultModel?.id === m.id, onclick: () => call('setDefaultModel', { model: m.ref }) },
          { label: 'Copy Terminal Command', icon: 'terminal', sub: `synapse chat "${m.ref.provider}/${m.id}"`, onclick: () => copyText(`synapse chat "local/${m.id}"`) },
          'sep',
          { label: 'Delete', icon: 'trash', danger: true, onclick: () => call('deleteModel', { id: m.id }) },
        ], { align: 'right' }));
        list.append(h('div.list-row',
          h('div', { style: { color: 'var(--text-2)' } }, ic('cpu', 'lg')),
          h('div.grow', h('div.ellipsis', m.displayName, m.vision ? h('span', { title: 'Understands images', style: { marginLeft: '6px', color: 'var(--text-3)' } }, ic('eye', 'sm')) : null),
            h('div.tiny.faint.ellipsis', `GGUF · llama.cpp · ${formatBytes(m.sizeBytes)} · ${m.repo}`)),
          fitBadge(m.fit),
          loading ? h('span.spinner') : loaded ? h('button.btn.sm', { onclick: () => call('unloadModel') }, 'Unload')
            : h('button.btn.sm', { onclick: () => call('loadModel', { id: m.id }).catch(errorToast) }, 'Load'),
          h('button.btn.sm.primary', { onclick: () => newChat(null, m.ref) }, ic('chat', 'sm'), 'Chat'),
          more));
      }
      body.append(list);
      if (!app.engine.available) {
        body.append(h('div.card', { style: { marginTop: '14px', display: 'flex', gap: '12px', alignItems: 'center' } }, ic('alert'),
          h('div.grow', 'llama.cpp isn’t installed yet. It runs your local models.'),
          h('button.btn.primary.sm', { onclick: () => setTab('engine') }, 'Set Up Engine')));
      }
    }
  }
  render();
  return { update: render };
}

// MARK: Cloud

function Cloud(body) {
  function render() {
    const app = store.app;
    clear(body);
    for (const p of app.providers) {
      const section = h('div', { style: { marginBottom: '18px' } });
      section.append(h('div.row', { style: { margin: '8px 0 8px' } }, h('b.grow', p.name),
        p.enabled ? h('button.btn.xs.ghost', { onclick: () => call('refreshModels', { provider: p.id }) }, ic('refresh', 'sm'), 'Refresh') : null,
        p.usagePage ? h('a.small', { href: '#', 'data-href': p.usagePage }, 'Dashboard') : null));
      const err = app.remoteErrors[p.id];
      const list = app.remoteModels[p.id];
      if (!p.enabled) {
        section.append(h('div.list-row', h('span.grow.muted.small', p.note),
          h('button.btn.sm', { onclick: () => openAccounts() }, p.id === 'custom' ? 'Set Up…' : 'Sign In & Add Key…')));
      } else if (err) {
        section.append(h('div.list-row', h('span.grow.err.small', err), h('button.btn.sm', { onclick: () => call('refreshModels', { provider: p.id }) }, 'Retry')));
      } else if (!list) {
        section.append(h('div.row', h('span.spinner'), h('span.muted.small', 'Loading models…')));
      } else {
        const rows = h('div.list');
        for (const m of list) {
          const isDefault = app.settingsDefaultModel?.provider === m.provider && app.settingsDefaultModel?.id === m.id;
          rows.append(h('div.list-row', { style: { padding: '7px 12px' } },
            h('span.grow.ellipsis', m.name, m.name !== m.id ? h('span.tiny.faint', { style: { marginLeft: '8px' } }, m.id) : null),
            m.vision ? h('span', { title: 'Understands images', style: { color: 'var(--text-3)' } }, ic('eye', 'sm')) : null,
            iconButton('terminal', 'Copy terminal command', () => copyText(`synapse chat "${m.provider}/${m.id}"`), 'sm'),
            h('button.btn.xs', { class: isDefault ? '' : 'ghost', style: isDefault ? { color: 'var(--accent-strong)' } : {}, onclick: () => call('setDefaultModel', { model: m }) }, isDefault ? 'Default' : 'Make Default'),
            h('button.btn.xs', { onclick: () => newChat(null, m) }, 'Chat')));
        }
        if (!list.length) rows.append(h('div.hint', 'No chat models found.'));
        section.append(rows);
      }
      body.append(section);
    }
  }
  render();
  return { update: render };
}

// MARK: Engine

const BACKENDS = [
  ['auto', 'Automatic', 'Best build for your GPU'],
  ['cuda-12', 'NVIDIA CUDA 12', 'GeForce/RTX with recent drivers'],
  ['cuda-13', 'NVIDIA CUDA 13', 'Newest NVIDIA drivers (580+)'],
  ['vulkan', 'Vulkan', 'AMD, Intel and NVIDIA GPUs'],
  ['rocm', 'AMD ROCm', 'Supported Radeon GPUs'],
  ['cpu', 'CPU only', 'No GPU acceleration'],
];

function Engine(body) {
  const app0 = store.app;
  const status = h('div.card.engine-card');
  const progress = h('div');
  const installLog = h('pre.log-box', { style: { height: '160px' } });
  const engineLog = h('pre.log-box');
  const backend = h('select.select', { style: { width: '230px' } }, BACKENDS.map(([v, label]) => h('option', { value: v }, label)));
  backend.value = app0.settings.llamaBackend || 'auto';
  backend.onchange = () => call('setSettings', { llamaBackend: backend.value });
  const layers = h('select.select', { style: { width: '230px' } },
    [['auto', 'Automatic (fit to free VRAM)'], ['all', 'All layers on GPU'], ['0', 'CPU only'], ['16', '16 layers'], ['24', '24 layers'], ['32', '32 layers']].map(([v, l]) => h('option', { value: v }, l)));
  layers.value = app0.settings.gpuLayers || 'auto';
  if (![...layers.options].some((o) => o.value === layers.value)) layers.append(h('option', { value: layers.value }, `${layers.value} layers`)), (layers.value = app0.settings.gpuLayers);
  layers.onchange = () => call('setSettings', { gpuLayers: layers.value });
  const ctxSize = h('select.select', { style: { width: '230px' } }, [4096, 8192, 16384, 32768, 65536, 131072].map((n) => h('option', { value: n }, `${n.toLocaleString()} tokens`)));
  ctxSize.value = String(app0.settings.contextSize);
  ctxSize.onchange = () => call('setSettings', { contextSize: +ctxSize.value });
  const customPath = h('input.input.mono', { value: app0.settings.llamaServerPath || '', placeholder: 'C:\\tools\\llama.cpp\\llama-server.exe', spellcheck: 'false' });
  customPath.addEventListener('change', () => call('setSettings', { llamaServerPath: customPath.value.trim() }));

  body.append(status, progress,
    h('div.set-group', { style: { marginTop: '16px' } },
      h('div.set-row', h('div.text', h('div', 'Build'), h('div.hint', 'Which llama.cpp build to install. Automatic picks CUDA for NVIDIA and Vulkan for AMD/Intel GPUs.')), backend),
      h('div.set-row', h('div.text', h('div', 'GPU layers'), h('div.hint', 'How much of the model goes to the GPU. Automatic fits as many layers as free VRAM allows; the rest runs on the CPU.')), layers),
      h('div.set-row', h('div.text', h('div', 'Context length'), h('div.hint', 'Longer context uses more memory. 8K is a good default on 8 GB GPUs.')), ctxSize),
      h('div.set-row.stack', h('div.text', h('div', 'Use my own llama-server'), h('div.hint', 'Optional. Path to llama-server.exe (or its folder) from a build you manage.')), customPath)),
    h('div.section-title', 'Install log'), installLog,
    h('div.section-title', 'Engine log'), engineLog,
    h('p.hint', { style: { marginTop: '12px' } }, ''));
  const foot = body.lastChild;

  let lastVersion = null;
  call('engineVersion').then((v) => { lastVersion = v; render(); }).catch(() => {});

  function render() {
    const app = store.app;
    const e = app.engine;
    const installed = e.installed;
    clear(status).append(
      h('div.state', { class: e.available ? 'ok' : '' }, ic(e.available ? 'check' : 'download', 'lg')),
      h('div.grow',
        h('div', { style: { fontWeight: 600, fontSize: '15px' } }, 'llama.cpp'),
        h('div.small.muted', e.available
          ? `Runs GGUF models on ${installed ? installed.backend.toUpperCase().replace('CUDA', 'CUDA ') : 'your'} build${installed ? ` · ${installed.tag}` : lastVersion ? ` · ${lastVersion}` : ''}`
          : app.platform.isWindows ? 'Not installed. Synapse downloads the official build for your hardware (about 30–600 MB).' : 'Install llama.cpp and set the path below.'),
        e.path ? h('div.tiny.faint.mono.ellipsis', { title: e.path }, e.path) : null,
        h('div.tiny.faint', `Suggested build for this PC: ${e.autoBackend}`)),
      e.state === 'running' ? h('span.chip', h('span.dot.green'), e.text) : null,
      h('button.btn', {
        class: e.available ? '' : 'primary', disabled: e.installing,
        onclick: () => call('engineInstall', { backend: backend.value }),
      }, e.installing ? h('span.spinner') : ic(e.available ? 'refresh' : 'download', 'sm'), e.installing ? 'Installing…' : e.available ? 'Update' : 'Install'));
    clear(progress);
    if (e.installing) progress.append(h('div.progress', { style: { marginTop: '10px' } }, h('i', { style: { width: `${Math.round(e.installProgress * 100)}%` } })));
    setLog(installLog, e.installLog || 'Nothing installed in this session.');
    setLog(engineLog, e.log || 'No model loaded.');
    foot.textContent = `Local models are served on http://127.0.0.1:${e.port}/v1. Synapse also serves every model (local and cloud) on ${app.api.url || 'the local API'} for the synapse command and other apps.`;
  }
  function setLog(el, text) {
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 30;
    if (el.textContent !== text) { el.textContent = text; if (atBottom) el.scrollTop = el.scrollHeight; }
  }
  render();
  return { update: render };
}
