// Quick Look for Windows: previews images, PDFs, documents, code and Markdown in a window.
import { call } from '../bridge.js';
import { h, ic, modal, lightbox, errorToast, formatBytes } from '../ui.js';
import { renderMarkdown, highlight } from '../markdown.js';
import { store } from '../app.js';

export function fileUrl(path) {
  return (store.app?.fileBase || '/file?path=') + encodeURIComponent(path);
}

const LANG = { cs: 'csharp', js: 'js', ts: 'ts', py: 'python', ps1: 'powershell', json: 'json', html: 'html', htm: 'html', css: 'css', xml: 'xml', sql: 'sql', sh: 'bash', yml: 'yaml', yaml: 'yaml', java: 'java', go: 'go', rs: 'rust', cpp: 'cpp', c: 'c', h: 'c', xaml: 'xml', svg: 'xml', kt: 'kotlin', toml: 'toml' };

export function revealLabel() { return store.app?.platform?.isWindows ? 'Show in Explorer' : 'Show in Folder'; }

export function openImage(path) {
  lightbox(fileUrl(path), [
    h('button.btn.sm', { onclick: () => call('openPath', { path }).catch(errorToast) }, ic('external', 'sm'), 'Open'),
    h('button.btn.sm', { onclick: () => call('revealPath', { path }) }, ic('folderOpen', 'sm'), revealLabel()),
  ]);
}

export async function preview(path) {
  let info;
  try { info = await call('preview', { path }); } catch (e) { errorToast(e); return; }
  if (info.kind === 'missing') { errorToast(new Error('File not found: ' + path)); return; }
  if (info.kind === 'image') { openImage(path); return; }
  const body = h('div.preview-body');
  if (info.kind === 'pdf') body.append(h('iframe', { src: fileUrl(path), title: info.name }));
  else if (info.kind === 'media') body.append(h(info.ext.match(/mp3|wav|ogg/) ? 'audio' : 'video', { src: fileUrl(path), controls: true, style: { margin: 'auto', maxWidth: '100%' } }));
  else if (info.kind === 'markdown') body.append(h('div.md', { html: renderMarkdown(info.text) }));
  else if (info.kind === 'text') {
    if (info.html) {
      const frame = h('iframe', { sandbox: '', srcdoc: info.text, title: info.name });
      const code = h('pre', { html: highlight(info.text, 'html'), style: { display: 'none' } });
      body.append(frame, code);
      body.dataset.toggle = '1';
      body._toggle = () => { const showCode = code.style.display === 'none'; code.style.display = showCode ? '' : 'none'; frame.style.display = showCode ? 'none' : ''; };
    } else {
      body.append(h('pre', { html: highlight(info.text, LANG[info.ext] || '') }));
    }
  } else {
    body.append(h('div.empty', ic('file'), h('h3', info.name), h('p', `${formatBytes(info.size)} · No preview for this kind of file.`),
      h('button.btn.primary', { onclick: () => call('openPath', { path }).catch(errorToast) }, 'Open')));
  }
  const m = modal({
    cls: 'xwide',
    title: info.name,
    subtitle: `${formatBytes(info.size)} · ${path}`,
    body,
    headExtra: null,
  });
  const head = m.box.querySelector('.modal-head');
  const bar = h('div.row',
    body._toggle ? h('button.btn.sm', { onclick: () => body._toggle() }, ic('code', 'sm'), 'Source') : null,
    h('button.btn.sm', { onclick: () => call('openPath', { path }).catch(errorToast) }, ic('external', 'sm'), 'Open'),
    h('button.btn.sm', { onclick: () => call('revealPath', { path }) }, ic('folderOpen', 'sm'), revealLabel()));
  head.insertBefore(bar, head.lastChild);
}
