// Free-tier usage per provider: live limits from Groq-style headers, plus Synapse's own counts.
import { call } from '../bridge.js';
import { h, ic, clear, compact, relTime } from '../ui.js';
import { store } from '../app.js';

export function mount(container) {
  const page = h('div.page');
  const inner = h('div.page-inner');
  page.append(inner);
  container.append(page);
  const body = h('div', { style: { display: 'grid', gap: '16px' } });
  inner.append(h('div.page-head', h('div.grow', h('h2', 'Usage'),
    h('p', 'Free-tier usage per provider. Groq reports limits live; for others Synapse counts requests.'))), body);

  function render() {
    const app = store.app;
    clear(body);
    for (const p of app.providers) {
      const u = app.usage[p.id];
      if (!p.enabled && !u?.hasData) continue;
      body.append(card(p, u));
    }
    const local = app.usage.local;
    body.append(h('div.card.row', h('b.grow', 'On this PC'), h('span.muted', `${local?.requestsToday || 0} requests today · no limits`)));
  }

  function card(p, u) {
    const c = h('div.card', { style: { display: 'grid', gap: '14px' } });
    c.append(h('div.row', h('b.grow', { style: { fontSize: '15px' } }, p.name),
      p.usagePage ? h('a.small', { href: '#', 'data-href': p.usagePage }, ic('external', 'sm'), ' Official dashboard') : null));
    const rl = u.rateLimit;
    if (rl && (rl.limitRequests || rl.limitTokens)) {
      const box = h('div', { style: { display: 'grid', gap: '10px' } }, h('div.tiny.faint', `Live limits (from the last reply, ${relTime(rl.updatedAt)})`));
      if (rl.limitRequests != null && rl.remainingRequests != null) box.append(limitBar('Requests', rl.remainingRequests, rl.limitRequests, rl.resetRequests));
      if (rl.limitTokens != null && rl.remainingTokens != null) box.append(limitBar('Tokens', rl.remainingTokens, rl.limitTokens, rl.resetTokens));
      c.append(box);
    } else if (p.id === 'google') {
      c.append(h('div.small.muted', 'Google doesn’t report remaining quota through the API. Free-tier limits differ per model (requests per minute and per day, resetting at midnight Pacific). The counts below are tracked by Synapse; the dashboard link shows Google’s exact numbers.'));
    }
    c.append(h('div.stats',
      stat('Requests today', u.requestsToday),
      stat('Last minute', u.lastMinute),
      stat('Tokens in today', compact(u.promptTokensToday)),
      stat('Tokens out today', compact(u.completionTokensToday))));
    if (u.lastLimitError && u.lastLimitErrorAt) {
      c.append(h('div.row', { style: { alignItems: 'flex-start' } }, h('span.warn', ic('alert')),
        h('div', h('div', { style: { fontWeight: 500 } }, `Limit hit ${relTime(u.lastLimitErrorAt)}`), h('div.tiny.muted', u.lastLimitError))));
    }
    const per = Object.entries(u.perModelToday || {}).sort((a, b) => b[1] - a[1]);
    if (per.length) {
      c.append(h('div', { style: { display: 'grid', gap: '4px' } }, h('div.tiny.faint', 'Today by model'),
        per.map(([k, v]) => h('div.row.small', h('span.grow.ellipsis', k), h('span.muted.mono', `${v} requests`)))));
    }
    if (u.history.length > 1) {
      const max = Math.max(1, ...u.history.map((d) => d.requests));
      c.append(h('div.bars', u.history.map((d) => h('div.bar', { title: `${d.day}: ${d.requests} requests, ${compact(d.tokens)} tokens` },
        h('i', { style: { height: `${Math.max(2, (d.requests / max) * 100)}%` } }), h('span', d.day.slice(5))))));
    }
    c.append(h('div.row', h('span.spacer'), h('button.btn.xs.ghost', { onclick: () => call('usageReset', { provider: p.id }) }, 'Reset counts')));
    return c;
  }

  function limitBar(title, remaining, limit, reset) {
    const used = Math.max(0, limit - remaining);
    const frac = limit > 0 ? used / limit : 0;
    return h('div.limit',
      h('div.row', h('b.grow', { style: { fontWeight: 500 } }, title), h('span.mono', `${remaining.toLocaleString()} of ${limit.toLocaleString()} left`),
        reset ? h('span.faint', `· resets in ${reset}`) : null),
      h('div.progress', { class: frac > 0.85 ? 'red' : frac > 0.6 ? 'orange' : '' }, h('i', { style: { width: `${Math.round(frac * 100)}%` } })));
  }

  function stat(title, value) { return h('div.stat', h('b', String(value ?? 0)), h('span', title)); }

  render();
  return { update: render, title: () => ({ title: 'Usage' }) };
}
