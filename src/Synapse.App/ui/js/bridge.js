// Talks to the Synapse host. In the Windows app that's WebView2's message channel; in the dev
// host (any browser) it's Server-Sent Events plus POST.

const listeners = new Map();
const pending = new Map();
let nextId = 1;
let send;

export const isWebView = !!(window.chrome && window.chrome.webview);

function dispatch(msg) {
  if (msg.type === 'reply') {
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    msg.ok ? p.resolve(msg.result) : p.reject(new Error(msg.error || 'Failed'));
    return;
  }
  for (const fn of listeners.get(msg.type) || []) {
    try { fn(msg.data, msg); } catch (e) { console.error(e); }
  }
}

if (isWebView) {
  window.chrome.webview.addEventListener('message', (e) => {
    try { dispatch(typeof e.data === 'string' ? JSON.parse(e.data) : e.data); } catch (err) { console.error(err); }
  });
  send = (m) => window.chrome.webview.postMessage(JSON.stringify(m));
} else {
  let source;
  // Replies arrive over the event stream, so nothing is sent until it is connected.
  let markOpen;
  const opened = new Promise((r) => { markOpen = r; });
  const connect = () => {
    source = new EventSource('/bridge/events');
    source.onmessage = (e) => { try { dispatch(JSON.parse(e.data)); } catch (err) { console.error(err); } };
    let lost = false;
    source.onerror = () => { lost = true; };
    // After the host restarts, the page asks for fresh state again.
    source.onopen = () => { markOpen(); if (lost) { lost = false; for (const fn of listeners.get('reconnect') || []) fn(); } };
  };
  connect();
  send = (m) => opened.then(() => fetch('/bridge/send', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(m) }))
    .catch(() => { const p = pending.get(m.id); if (p) { pending.delete(m.id); p.reject(new Error('Synapse is not reachable')); } });
}

/** Subscribes to pushes: "app", "chat", "message", "host". */
export function on(type, fn) {
  if (!listeners.has(type)) listeners.set(type, []);
  listeners.get(type).push(fn);
}

/** Calls a host command and resolves with its result. */
export function call(cmd, args = {}) {
  return new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    send({ id, cmd, args });
  });
}

/** Fire and forget. */
export function tell(cmd, args = {}) { send({ cmd, args }); }

/**
 * Sends dropped File objects. WebView2 hands the host their real paths (no copying through
 * JavaScript); browsers fall back to uploading the bytes.
 */
export async function sendFiles(files, cmd, args = {}) {
  const list = Array.from(files || []);
  if (!list.length) return [];
  if (isWebView && window.chrome.webview.postMessageWithAdditionalObjects) {
    return new Promise((resolve, reject) => {
      const id = nextId++;
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessageWithAdditionalObjects(JSON.stringify({ id, cmd, args }), list);
    });
  }
  return null;   // caller uploads data instead
}

export function readAsBase64(file) {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result).split(',')[1] || '');
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
}
