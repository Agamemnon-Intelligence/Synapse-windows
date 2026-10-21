// Small DOM helpers: element builder, popover menus, modals, toasts, lightbox and formatters.
import { icon } from './icons.js';
import { escapeHtml } from './markdown.js';

// Views build rows with `cond ? node : null`; let append/prepend skip empty slots instead of printing "null".
for (const proto of [Element.prototype, DocumentFragment.prototype]) {
  for (const name of ['append', 'prepend']) {
    const native = proto[name];
    proto[name] = function (...nodes) { return native.apply(this, nodes.flat(Infinity).filter((n) => n !== null && n !== undefined && n !== false && n !== '')); };
  }
}

/** h('div.card#id', {onclick, class, style, html, ...attrs}, ...children) */
export function h(tag, props, ...children) {
  if (props == null || typeof props !== 'object' || props instanceof Node || Array.isArray(props)) {
    if (props !== undefined && props !== null) children.unshift(props);
    props = {};
  }
  const m = tag.match(/^([a-z0-9-]+)?((?:[.#][\w-]+)*)$/i);
  const el = document.createElement((m && m[1]) || 'div');
  if (m && m[2]) {
    for (const part of m[2].match(/[.#][\w-]+/g)) {
      if (part[0] === '.') el.classList.add(part.slice(1)); else el.id = part.slice(1);
    }
  }
  for (const [k, v] of Object.entries(props)) {
    if (v === undefined || v === null || v === false) continue;
    if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (k === 'class') el.className += (el.className ? ' ' : '') + v;
    else if (k === 'style' && typeof v === 'object') Object.assign(el.style, v);
    else if (k === 'html') el.innerHTML = v;
    else if (k === 'text') el.textContent = v;
    else if (k === 'value') el.value = v;
    else if (k === 'checked') el.checked = !!v;
    else if (k === 'dataset') Object.assign(el.dataset, v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, v);
  }
  append(el, children);
  return el;
}

function append(el, children) {
  for (const c of children.flat(Infinity)) {
    if (c === null || c === undefined || c === false) continue;
    el.appendChild(c instanceof Node ? c : document.createTextNode(String(c)));
  }
}

/** Inline SVG icon as an element. */
export function ic(name, cls = '') {
  const t = document.createElement('template');
  t.innerHTML = icon(name, cls);
  return t.content.firstChild;
}

export function html(s) {
  const t = document.createElement('template');
  t.innerHTML = s.trim();
  return t.content.childElementCount === 1 ? t.content.firstChild : t.content;
}

export function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); return el; }

export function iconButton(name, title, onclick, cls = '') {
  return h('button.icon-btn', { title, 'aria-label': title, onclick, class: cls }, ic(name));
}

export function toggleSwitch(checked, onchange) {
  const input = h('input', { type: 'checkbox', checked, onchange: (e) => onchange(e.target.checked) });
  return h('label.switch', input, h('span'));
}

export function segmented(options, value, onchange) {
  return h('div.segmented', options.map(([v, label]) =>
    h('button', { class: v === value ? 'on' : '', onclick: () => onchange(v) }, label)));
}

// MARK: Formatting

export function formatBytes(b) {
  if (!b && b !== 0) return '';
  const u = ['bytes', 'KB', 'MB', 'GB', 'TB'];
  let i = 0, v = b;
  while (v >= 1000 && i < u.length - 1) { v /= 1000; i++; }
  return i === 0 ? `${b} bytes` : `${v >= 100 ? v.toFixed(0) : v.toFixed(1).replace(/\.0$/, '')} ${u[i]}`;
}

export function compact(n) {
  if (n >= 1e9) return (n / 1e9).toFixed(1).replace(/\.0$/, '') + 'B';
  if (n >= 1e6) return (n / 1e6).toFixed(1).replace(/\.0$/, '') + 'M';
  if (n >= 1e3) return (n / 1e3).toFixed(1).replace(/\.0$/, '') + 'k';
  return String(n || 0);
}

export function relTime(date) {
  const d = typeof date === 'string' ? new Date(date) : date;
  const s = Math.round((Date.now() - d.getTime()) / 1000);
  if (s < 45) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  if (s < 86400 * 7) return `${Math.round(s / 86400)} d ago`;
  return d.toLocaleDateString();
}

export function basename(p) { return String(p || '').split(/[\\/]/).filter(Boolean).pop() || p; }
export function ext(p) { const b = basename(p); const i = b.lastIndexOf('.'); return i > 0 ? b.slice(i + 1).toLowerCase() : ''; }
export function host(url) { try { return new URL(url).host; } catch { return url; } }

// MARK: Popover menus

let openMenu = null;

export function closeMenu() {
  if (!openMenu) return;
  openMenu.el.remove();
  openMenu.anchor?.classList.remove('menu-open');
  document.removeEventListener('mousedown', openMenu.outside, true);
  document.removeEventListener('keydown', openMenu.keys, true);
  window.removeEventListener('blur', closeMenu);
  openMenu = null;
}

/**
 * Items: {label, sub, icon, onclick, checked, danger, disabled, kbd, keep} | {section} | 'sep'.
 * Options: {search: placeholder, align: 'left'|'right', above: bool, width}
 */
export function menu(anchor, items, opts = {}) {
  if (openMenu && openMenu.anchor === anchor) { closeMenu(); return; }
  closeMenu();
  const el = h('div.popover', { role: 'menu' });
  if (opts.width) el.style.width = opts.width + 'px';
  const list = h('div');
  let query = '';
  let hot = -1;
  const render = () => {
    clear(list);
    const q = query.toLowerCase();
    let shown = 0;
    let pendingSection = null;
    const rows = [];
    for (const it of items) {
      if (it === 'sep') { if (!q) list.append(h('div.menu-sep')); continue; }
      if (it.section) { pendingSection = it.section; if (!q) list.append(h('div.menu-section', it.section)); continue; }
      if (q && !(`${it.label} ${it.sub || ''} ${it.search || ''} ${pendingSection || ''}`.toLowerCase().includes(q))) continue;
      if (q && pendingSection) { list.append(h('div.menu-section', pendingSection)); pendingSection = null; }
      const row = h('div.menu-item', {
        class: [it.danger && 'danger', it.disabled && 'disabled'].filter(Boolean).join(' '),
        role: 'menuitem',
        onclick: (e) => { e.stopPropagation(); if (!it.keep) closeMenu(); it.onclick?.(e); if (it.keep) render(); },
      },
        it.checked !== undefined ? h('span.check', it.checked ? ic('check', 'sm') : '') : null,
        it.icon ? ic(it.icon) : null,
        h('span.label', it.label, it.sub ? h('span.sub', it.sub) : null),
        it.kbd ? h('span.kbd', it.kbd) : null,
        it.right || null);
      rows.push(row);
      list.append(row);
      shown++;
    }
    if (!shown) list.append(h('div.menu-empty', opts.empty || 'Nothing found'));
    hot = q && rows.length ? 0 : -1;
    rows.forEach((r, i) => r.classList.toggle('hot', i === hot));
    el._rows = rows;
  };
  if (opts.search) {
    const input = h('input', { placeholder: opts.search, oninput: (e) => { query = e.target.value; render(); } });
    el.append(h('div.menu-search', input));
    setTimeout(() => input.focus(), 0);
  }
  el.append(list);
  render();
  document.body.append(el);
  position(el, anchor, opts);
  anchor?.classList.add('menu-open');
  const outside = (e) => { if (!el.contains(e.target) && !anchor?.contains(e.target)) closeMenu(); };
  const keys = (e) => {
    const rows = el._rows || [];
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeMenu(); }
    else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      if (!rows.length) return;
      hot = (hot + (e.key === 'ArrowDown' ? 1 : -1) + rows.length) % rows.length;
      rows.forEach((r, i) => r.classList.toggle('hot', i === hot));
      rows[hot].scrollIntoView({ block: 'nearest' });
    } else if (e.key === 'Enter' && hot >= 0 && rows[hot]) { e.preventDefault(); rows[hot].click(); }
  };
  document.addEventListener('mousedown', outside, true);
  document.addEventListener('keydown', keys, true);
  window.addEventListener('blur', closeMenu);
  openMenu = { el, anchor, outside, keys };
  return el;
}

function position(el, anchor, opts) {
  const r = anchor ? anchor.getBoundingClientRect() : { left: opts.x || 0, right: opts.x || 0, top: opts.y || 0, bottom: opts.y || 0, width: 0 };
  const w = el.offsetWidth, hgt = el.offsetHeight;
  let left = opts.align === 'right' ? r.right - w : r.left;
  left = Math.max(8, Math.min(left, innerWidth - w - 8));
  let top = r.bottom + 6;
  if (opts.above || top + hgt > innerHeight - 8) top = Math.max(8, r.top - hgt - 6);
  el.style.left = left + 'px';
  el.style.top = top + 'px';
}

/** Context menu at the pointer. */
export function contextMenu(e, items) {
  e.preventDefault();
  closeMenu();
  menu(null, items, { x: e.clientX, y: e.clientY });
}

// MARK: Modals

const modalStack = [];

export function modal({ title, subtitle, body, foot, cls = '', onclose, dismissable = true, headExtra } = {}) {
  const layer = h('div.modal-layer');
  const box = h('div.modal', { class: cls, role: 'dialog', 'aria-modal': 'true' });
  const close = () => {
    layer.remove();
    const i = modalStack.indexOf(entry);
    if (i >= 0) modalStack.splice(i, 1);
    onclose?.();
  };
  if (title !== undefined) {
    box.append(h('div.modal-head', headExtra || null,
      h('div.grow', h('h3', title), subtitle ? h('p', subtitle) : null),
      dismissable ? iconButton('x', 'Close', close) : null));
  }
  if (body) box.append(body instanceof Node ? body : h('div.modal-body', body));
  if (foot) box.append(h('div.modal-foot', foot));
  layer.append(box);
  if (dismissable) layer.addEventListener('mousedown', (e) => { if (e.target === layer) close(); });
  document.body.append(layer);
  const entry = { layer, close, dismissable };
  modalStack.push(entry);
  setTimeout(() => box.querySelector('[autofocus], input, textarea, .btn.primary')?.focus(), 0);
  return { close, box, layer };
}

export function topModal() { return modalStack[modalStack.length - 1]; }

export function confirmDialog(title, text, okLabel = 'OK', danger = false) {
  return new Promise((resolve) => {
    let done = false;
    const finish = (v) => { if (done) return; done = true; m.close(); resolve(v); };
    const m = modal({
      title, subtitle: text,
      foot: [h('button.btn', { onclick: () => finish(false) }, 'Cancel'),
        h('button.btn', { class: danger ? 'danger' : 'primary', onclick: () => finish(true), autofocus: true }, okLabel)],
      onclose: () => finish(false),
    });
    m.box.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); finish(true); } });
  });
}

export function promptDialog(title, value = '', { placeholder = '', okLabel = 'Save', subtitle } = {}) {
  return new Promise((resolve) => {
    let done = false;
    const input = h('input.input', { value, placeholder, autofocus: true });
    const finish = (v) => { if (done) return; done = true; m.close(); resolve(v); };
    const m = modal({
      title, subtitle,
      body: [input],
      foot: [h('button.btn', { onclick: () => finish(null) }, 'Cancel'), h('button.btn.primary', { onclick: () => finish(input.value) }, okLabel)],
      onclose: () => finish(null),
    });
    input.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); finish(input.value); } });
    setTimeout(() => { input.focus(); input.select(); }, 0);
  });
}

// MARK: Toasts

export function toast(text, kind = 'info', ms = 4200) {
  let layer = document.querySelector('.toasts');
  if (!layer) { layer = h('div.toasts'); document.body.append(layer); }
  const t = h('div.toast', { class: kind }, ic(kind === 'error' ? 'alertCircle' : kind === 'ok' ? 'check' : 'info'), h('div', text));
  layer.append(t);
  setTimeout(() => { t.style.transition = 'opacity .2s'; t.style.opacity = '0'; setTimeout(() => t.remove(), 220); }, ms);
}

export function errorToast(e) { toast(e?.message || String(e), 'error', 6000); }

// MARK: Lightbox

export function lightbox(src, actions = []) {
  const box = h('div.lightbox', { onclick: (e) => { if (e.target === box) box.remove(); } },
    h('img', { src }),
    h('div.bar', actions, h('button.btn.sm', { onclick: () => box.remove() }, ic('x', 'sm'), 'Close')));
  const esc = (e) => { if (e.key === 'Escape') { box.remove(); document.removeEventListener('keydown', esc, true); } };
  document.addEventListener('keydown', esc, true);
  document.body.append(box);
}

export { escapeHtml };

/** Debounce helper. */
export function debounce(fn, ms) {
  let t;
  return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); };
}

export async function copyText(text) {
  try { await navigator.clipboard.writeText(text); }
  catch {
    const { call } = await import('./bridge.js');
    await call('copyText', { text });
  }
  toast('Copied', 'ok', 1400);
}
