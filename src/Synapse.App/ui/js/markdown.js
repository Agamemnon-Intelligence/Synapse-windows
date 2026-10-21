// Markdown → HTML for model replies: headings, nested lists, task lists, quotes, tables, fenced
// code with highlighting, inline styles and simple math. Everything is escaped; only http(s)
// and mailto links survive, and they open in the default browser.
import { icon } from './icons.js';

export function escapeHtml(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

// MARK: Math

const SYMBOLS = [
  ['\\times', '×'], ['\\cdot', '·'], ['\\div', '÷'], ['\\pm', '±'], ['\\mp', '∓'], ['\\leq', '≤'], ['\\geq', '≥'], ['\\le', '≤'],
  ['\\ge', '≥'], ['\\neq', '≠'], ['\\ne', '≠'], ['\\approx', '≈'], ['\\equiv', '≡'], ['\\infty', '∞'], ['\\sqrt', '√'], ['\\pi', 'π'],
  ['\\rightarrow', '→'], ['\\Rightarrow', '⇒'], ['\\leftarrow', '←'], ['\\to', '→'], ['\\sum', 'Σ'], ['\\prod', 'Π'], ['\\int', '∫'],
  ['\\partial', '∂'], ['\\nabla', '∇'], ['\\alpha', 'α'], ['\\beta', 'β'], ['\\gamma', 'γ'], ['\\Gamma', 'Γ'], ['\\delta', 'δ'],
  ['\\Delta', 'Δ'], ['\\epsilon', 'ε'], ['\\varepsilon', 'ε'], ['\\theta', 'θ'], ['\\lambda', 'λ'], ['\\mu', 'μ'], ['\\sigma', 'σ'],
  ['\\Sigma', 'Σ'], ['\\phi', 'φ'], ['\\varphi', 'φ'], ['\\omega', 'ω'], ['\\Omega', 'Ω'], ['\\rho', 'ρ'], ['\\tau', 'τ'], ['\\eta', 'η'],
  ['\\in', '∈'], ['\\notin', '∉'], ['\\subset', '⊂'], ['\\cup', '∪'], ['\\cap', '∩'], ['\\forall', '∀'], ['\\exists', '∃'],
  ['\\degree', '°'], ['\\circ', '°'], ['\\ldots', '…'], ['\\cdots', '⋯'], ['\\quad', '  '], ['\\,', ' '], ['\\;', ' '], ['\\!', ''],
  ['\\left', ''], ['\\right', ''],
];
const SUP = { 0: '⁰', 1: '¹', 2: '²', 3: '³', 4: '⁴', 5: '⁵', 6: '⁶', 7: '⁷', 8: '⁸', 9: '⁹', '+': '⁺', '-': '⁻', n: 'ⁿ', i: 'ⁱ', '(': '⁽', ')': '⁾' };
const SUB = { 0: '₀', 1: '₁', 2: '₂', 3: '₃', 4: '₄', 5: '₅', 6: '₆', 7: '₇', 8: '₈', 9: '₉', '+': '₊', '-': '₋', i: 'ᵢ', n: 'ₙ', x: 'ₓ' };

/** Turns simple LaTeX ($17 \times 23$, \frac{a}{b}, x^2) into plain symbols. */
export function simplifyMath(s) {
  let out = s;
  out = out.replace(/\\(?:text|mathrm|mathbf|mathit|operatorname|boxed)\{([^}]*)\}/g, '$1');
  out = out.replace(/\\[dt]?frac\{([^{}]*)\}\{([^{}]*)\}/g, (_, a, b) => `${wrap(a)}⁄${wrap(b)}`);
  out = out.replace(/\\sqrt\{([^{}]*)\}/g, '√($1)');
  for (const [k, v] of [...SYMBOLS].sort((a, b) => b[0].length - a[0].length)) out = out.split(k).join(v);
  out = out.replace(/\^\{([^{}]+)\}/g, (_, x) => toScript(x, SUP, '^'));
  out = out.replace(/\^([0-9n+-])/g, (_, x) => SUP[x] || '^' + x);
  out = out.replace(/_\{([^{}]+)\}/g, (_, x) => toScript(x, SUB, '_'));
  out = out.replace(/_([0-9])/g, (_, x) => SUB[x] || '_' + x);
  return out.replace(/[{}]/g, '');
}
const wrap = (x) => (/^[\w.]+$/.test(x) ? x : `(${x})`);
function toScript(x, map, mark) {
  return [...x].every((c) => map[c]) ? [...x].map((c) => map[c]).join('') : `${mark}(${x})`;
}

// MARK: Inline

function safeUrl(url) {
  const u = url.trim().replace(/^<|>$/g, '');
  return /^(https?:|mailto:)/i.test(u) ? u : null;
}

export function inline(src) {
  const slots = [];
  const keep = (html) => `\u0000${slots.push(html) - 1}\u0000`;
  let s = src;
  // Code spans first so their contents stay literal.
  s = s.replace(/(`+)([\s\S]*?[^`])\1(?!`)/g, (_, __, code) => keep(`<code>${escapeHtml(code.trim() === '' ? code : code.replace(/^ (.*) $/, '$1'))}</code>`));
  // Math: $…$ and \( … \)
  s = s.replace(/\\\((.+?)\\\)/g, (_, m) => keep(`<span class="math">${escapeHtml(simplifyMath(m))}</span>`));
  s = s.replace(/(^|[^\\\w$])\$(?!\s)([^$\n]+?)(?<!\s)\$(?![\w$])/g, (_, pre, m) =>
    /^\d+([.,]\d+)?$/.test(m) ? _ : pre + keep(`<span class="math">${escapeHtml(simplifyMath(m))}</span>`));
  // Images become links (model output rarely has real images worth loading).
  s = s.replace(/!\[([^\]]*)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g, (all, alt, url) => {
    const u = safeUrl(url);
    return u ? keep(`<a href="#" data-href="${escapeHtml(u)}">${escapeHtml(alt || u)}</a>`) : all;
  });
  // Links [text](url)
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g, (all, text, url) => {
    const u = safeUrl(url);
    return u ? keep(`<a href="#" data-href="${escapeHtml(u)}" title="${escapeHtml(u)}">${inlineNoLinks(text)}</a>`) : all;
  });
  // Bare URLs
  s = s.replace(/(^|[\s(])(https?:\/\/[^\s<>()]+[^\s<>().,;:!?'"*_])/g, (_, pre, url) =>
    pre + keep(`<a href="#" data-href="${escapeHtml(url)}">${escapeHtml(url)}</a>`));
  s = escapeHtml(s);
  s = emphasis(s);
  return s.replace(/\u0000(\d+)\u0000/g, (_, i) => slots[+i]);
}

function inlineNoLinks(text) { return emphasis(escapeHtml(text)); }

function emphasis(s) {
  return s
    .replace(/\*\*\*(?=\S)([\s\S]*?\S)\*\*\*/g, '<strong><em>$1</em></strong>')
    .replace(/\*\*(?=\S)([\s\S]*?\S)\*\*/g, '<strong>$1</strong>')
    .replace(/(^|[^\w])__(?=\S)([\s\S]*?\S)__(?!\w)/g, '$1<strong>$2</strong>')
    .replace(/(^|[^*\w])\*(?=[^\s*])([^*\n]*?[^\s*])\*(?!\*)/g, '$1<em>$2</em>')
    .replace(/(^|[^\w])_(?=[^\s_])([^_\n]*?[^\s_])_(?!\w)/g, '$1<em>$2</em>')
    .replace(/~~(?=\S)([\s\S]*?\S)~~/g, '<del>$1</del>')
    .replace(/==(?=\S)([^=\n]*?\S)==/g, '<mark>$1</mark>');
}

// MARK: Blocks

const FENCE = /^(\s{0,3})(`{3,}|~{3,})\s*([\w#+.-]*)[^`]*$/;
const HEADING = /^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$/;
const HR = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
const LIST = /^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/;
const QUOTE = /^\s{0,3}>\s?(.*)$/;
const TABLE_SEP = /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;

export function renderMarkdown(text) {
  return renderBlocks(String(text || '').replace(/\r\n?/g, '\n').split('\n'));
}

function renderBlocks(lines) {
  const out = [];
  let para = [];
  const flushPara = () => {
    if (!para.length) return;
    out.push(`<p>${para.map((l) => inline(l.trim())).join('<br>')}</p>`);
    para = [];
  };
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const fence = line.match(FENCE);
    if (fence) {
      flushPara();
      const marker = fence[2];
      const lang = fence[3] || '';
      const body = [];
      let j = i + 1;
      while (j < lines.length && !new RegExp(`^\\s{0,3}${marker[0] === '`' ? '`' : '~'}{${marker.length},}\\s*$`).test(lines[j])) {
        body.push(fence[1] && lines[j].startsWith(fence[1]) ? lines[j].slice(fence[1].length) : lines[j]);
        j++;
      }
      out.push(codeBlock(body.join('\n'), lang));
      i = j;
      continue;
    }
    if (/^\s*\$\$/.test(line)) {
      flushPara();
      const body = [line.replace(/^\s*\$\$/, '')];
      let j = i;
      if (!/\$\$\s*$/.test(body[0]) || body[0].trim() === '') {
        j = i + 1;
        while (j < lines.length && !/\$\$\s*$/.test(lines[j])) body.push(lines[j++]);
        if (j < lines.length) body.push(lines[j]);
      }
      const math = body.join('\n').replace(/\$\$\s*$/, '').trim();
      out.push(`<p class="math" style="text-align:center">${escapeHtml(simplifyMath(math))}</p>`);
      i = j;
      continue;
    }
    if (!line.trim()) { flushPara(); continue; }
    const h = line.match(HEADING);
    if (h) {
      flushPara();
      out.push(`<h${h[1].length}>${inline(h[2])}</h${h[1].length}>`);
      continue;
    }
    if (HR.test(line) && !(para.length && /^\s*-+\s*$/.test(line))) {
      flushPara();
      out.push('<hr>');
      continue;
    }
    if (para.length && /^\s*(=+|-+)\s*$/.test(line)) {
      // Setext heading
      const level = line.trim()[0] === '=' ? 1 : 2;
      const textLine = para.pop();
      flushPara();
      out.push(`<h${level}>${inline(textLine)}</h${level}>`);
      continue;
    }
    if (QUOTE.test(line)) {
      flushPara();
      const body = [];
      let j = i;
      while (j < lines.length && (QUOTE.test(lines[j]) || (lines[j].trim() && body.length && !LIST.test(lines[j]) && !FENCE.test(lines[j]) && QUOTE.test(lines[j - 1])))) {
        const m = lines[j].match(QUOTE);
        body.push(m ? m[1] : lines[j]);
        j++;
      }
      out.push(`<blockquote>${renderBlocks(body)}</blockquote>`);
      i = j - 1;
      continue;
    }
    if (line.includes('|') && i + 1 < lines.length && TABLE_SEP.test(lines[i + 1]) && lines[i + 1].includes('-')) {
      flushPara();
      const rows = [line];
      const align = splitRow(lines[i + 1]).map((c) => (/^:-+:$/.test(c) ? 'center' : /-+:$/.test(c) ? 'right' : ''));
      let j = i + 2;
      while (j < lines.length && lines[j].includes('|') && lines[j].trim()) rows.push(lines[j++]);
      out.push(table(rows, align));
      i = j - 1;
      continue;
    }
    if (LIST.test(line) && (!para.length || !/^\s/.test(line))) {
      flushPara();
      const block = [];
      let j = i;
      while (j < lines.length) {
        const l = lines[j];
        if (!l.trim()) {
          // A blank line continues the list only if the list (or an indented continuation) resumes.
          const next = lines[j + 1];
          if (next !== undefined && (LIST.test(next) || /^\s{2,}\S/.test(next))) { block.push(l); j++; continue; }
          break;
        }
        if (LIST.test(l) || /^\s+\S/.test(l)) { block.push(l); j++; continue; }
        // Lazy continuation: an unindented line right after an item line.
        if (lines[j - 1].trim() && !HEADING.test(l) && !FENCE.test(l) && !QUOTE.test(l) && !HR.test(l) && !l.includes('|')) {
          block.push(l);
          j++;
          continue;
        }
        break;
      }
      out.push(list(block));
      i = j - 1;
      continue;
    }
    para.push(line);
  }
  flushPara();
  return out.join('');
}

function splitRow(row) {
  let s = row.trim();
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|') && !s.endsWith('\\|')) s = s.slice(0, -1);
  const cells = [];
  let cur = '';
  let inCode = false;
  for (let i = 0; i < s.length; i++) {
    const c = s[i];
    if (c === '`') inCode = !inCode;
    if (c === '\\' && s[i + 1] === '|') { cur += '|'; i++; continue; }
    if (c === '|' && !inCode) { cells.push(cur.trim()); cur = ''; continue; }
    cur += c;
  }
  cells.push(cur.trim());
  return cells;
}

function table(rows, align) {
  const head = splitRow(rows[0]);
  const style = (i) => (align[i] ? ` style="text-align:${align[i]}"` : '');
  let html = '<table><thead><tr>' + head.map((c, i) => `<th${style(i)}>${inline(c)}</th>`).join('') + '</tr></thead><tbody>';
  for (const r of rows.slice(1)) {
    const cells = splitRow(r);
    html += '<tr>' + head.map((_, i) => `<td${style(i)}>${inline(cells[i] ?? '')}</td>`).join('') + '</tr>';
  }
  return html + '</tbody></table>';
}

/** Builds nested lists from indentation. */
function list(lines) {
  const items = [];   // {indent, ordered, start, text: [lines]}
  for (const l of lines) {
    const m = l.match(LIST);
    if (m) items.push({ indent: m[1].replace(/\t/g, '    ').length, ordered: /\d/.test(m[2]), start: parseInt(m[2], 10), text: [m[3]] });
    else if (items.length) items[items.length - 1].text.push(l);
  }
  let i = 0;
  const build = (minIndent) => {
    if (i >= items.length) return '';
    const first = items[i];
    const tag = first.ordered ? 'ol' : 'ul';
    let html = `<${tag}${first.ordered && first.start !== 1 ? ` start="${first.start}"` : ''}>`;
    const base = first.indent;
    while (i < items.length && items[i].indent >= minIndent) {
      const it = items[i];
      if (it.indent > base + 1 && html.endsWith('</li>')) {
        html = html.slice(0, -5) + build(it.indent) + '</li>';
        continue;
      }
      if (it.indent < base || (it.indent <= base + 1 && it.ordered !== first.ordered)) break;
      i++;
      let body = it.text.map((t) => t.replace(/^\s{1,8}/, '')).join('\n').replace(/\n+$/, '');
      let cls = '';
      const task = body.match(/^\[([ xX])\]\s+([\s\S]*)$/);
      if (task) {
        cls = ' class="task"';
        body = task[2];
      }
      const inner = body.includes('\n\n') || /\n\s*(```|~~~)/.test(body) ? renderBlocks(body.split('\n')) : body.split('\n').map((x) => inline(x.trim())).join('<br>');
      html += `<li${cls}>${task ? `<input type="checkbox" disabled${task[1] !== ' ' ? ' checked' : ''}>` : ''}${inner}`;
      while (i < items.length && items[i].indent > base) html += build(items[i].indent);
      html += '</li>';
    }
    return html + `</${tag}>`;
  };
  let html = '';
  while (i < items.length) html += build(0);
  return html;
}

function codeBlock(code, lang) {
  const label = lang || 'text';
  return `<div class="code-block"><div class="code-head"><span class="lang">${escapeHtml(label)}</span>` +
    `<button class="btn xs ghost" data-copy>${icon('copy', 'sm')}<span>Copy</span></button></div>` +
    `<pre><code>${highlight(code, lang)}</code></pre></div>`;
}

// MARK: Syntax highlighting

const KW = {
  js: 'await break case catch class const continue debugger default delete do else export extends finally for from function get if import in instanceof let new of return set static super switch this throw try typeof var void while with yield async true false null undefined interface type enum implements private public protected readonly as declare namespace abstract keyof',
  py: 'and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return try while with yield True False None self match case',
  cs: 'abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly record ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while get set init yield partial where when with nameof',
  java: 'abstract assert boolean break byte case catch char class const continue default do double else enum extends final finally float for if implements import instanceof int interface long native new null package private protected public return short static super switch synchronized this throw throws transient true false try void volatile while var record fun val object companion data sealed when',
  c: 'auto break case char const continue default do double else enum extern float for goto if inline int long register return short signed sizeof static struct switch typedef union unsigned void volatile while class namespace template typename public private protected virtual override new delete nullptr true false bool using std include define ifdef ifndef endif',
  go: 'break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var true false nil',
  rust: 'as async await break const continue crate else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while dyn Some None Ok Err',
  sh: 'if then else elif fi for while do done case esac in function return export local readonly echo exit set unset source cd',
  ps: 'begin break catch class continue data do dynamicparam else elseif end exit filter finally for foreach from function if in param process return switch throw trap try until using var while workflow $true $false $null',
  sql: 'select from where and or not insert into values update set delete create table index view drop alter add primary key foreign references join left right inner outer full on group by order having limit offset as distinct union all case when then else end null is in between like exists count sum avg min max with returning default constraint unique',
  css: 'important',
};
const ALIASES = {
  javascript: 'js', jsx: 'js', ts: 'js', typescript: 'js', tsx: 'js', mjs: 'js', json: 'json', jsonc: 'json',
  python: 'py', py: 'py', python3: 'py', csharp: 'cs', 'c#': 'cs', cs: 'cs', fsharp: 'cs', java: 'java', kotlin: 'java', kt: 'java', scala: 'java',
  c: 'c', cpp: 'c', 'c++': 'c', h: 'c', hpp: 'c', cc: 'c', objc: 'c', swift: 'rust', go: 'go', golang: 'go', rust: 'rust', rs: 'rust',
  sh: 'sh', bash: 'sh', zsh: 'sh', shell: 'sh', console: 'sh', powershell: 'ps', ps1: 'ps', pwsh: 'ps', ps: 'ps', bat: 'sh', cmd: 'sh',
  sql: 'sql', html: 'html', xml: 'html', svg: 'html', xaml: 'html', vue: 'html', css: 'css', scss: 'css', less: 'css', yaml: 'yaml', yml: 'yaml', toml: 'yaml', ini: 'yaml',
  php: 'js', ruby: 'py', rb: 'py', lua: 'py', r: 'py', dart: 'java', dockerfile: 'sh', makefile: 'sh',
};

export function highlight(code, lang) {
  const l = ALIASES[(lang || '').toLowerCase()] || ((lang || '').toLowerCase() in KW ? lang.toLowerCase() : null);
  if (!l || code.length > 120_000) return escapeHtml(code);
  if (l === 'html') return highlightMarkup(code);
  if (l === 'json') return highlightJson(code);
  const hash = ['py', 'sh', 'ps', 'yaml'].includes(l);
  const keywords = new Set((KW[l] || '').split(' '));
  const ci = l === 'sql' || l === 'ps';
  const rules = [];
  if (l === 'sql') rules.push(['c', /--[^\n]*/y]);
  if (l === 'ps') rules.push(['c', /<#[\s\S]*?#>/y]);
  if (hash) rules.push(['c', /#[^\n]*/y]);
  if (!hash || l === 'ps') rules.push(['c', /\/\/[^\n]*/y], ['c', /\/\*[\s\S]*?\*\//y]);
  if (l === 'py') rules.push(['s', /("""|''')[\s\S]*?\1/y]);
  rules.push(['s', /"(?:\\[\s\S]|[^"\\\n])*"/y], ['s', /'(?:\\[\s\S]|[^'\\\n])*'/y]);
  if (l === 'js' || l === 'go' || l === 'sh' || l === 'ps') rules.push(['s', /`(?:\\[\s\S]|[^`\\])*`/y]);
  if (l === 'sh' || l === 'ps') rules.push(['v', /\$[\w:]+|\$\{[^}]*\}/y]);
  if (l === 'ps') rules.push(['a', /(?<![\w-])-[A-Za-z]\w*/y]);
  if (l === 'css') rules.push(['a', /[\w-]+(?=\s*:)/y], ['t', /[.#][\w-]+/y]);
  if (l === 'yaml') rules.push(['a', /^[\t ]*[\w.-]+(?=\s*[:=])/my]);
  rules.push(['n', /\b(?:0x[\da-f]+|\d[\d_]*(?:\.\d+)?(?:e[+-]?\d+)?)[a-z]*\b/iy]);
  rules.push(['w', /[A-Za-z_$@][\w$-]*/y]);
  let out = '';
  let i = 0;
  let plain = '';
  const flush = () => { if (plain) { out += escapeHtml(plain); plain = ''; } };
  outer: while (i < code.length) {
    for (const [kind, re] of rules) {
      re.lastIndex = i;
      const m = re.exec(code);
      if (!m || m.index !== i || !m[0]) continue;
      const t = m[0];
      if (kind === 'w') {
        const key = ci ? t.toLowerCase() : t;
        let cls = null;
        if (keywords.has(key) || (l === 'ps' && /^[A-Z][a-z]+-[A-Z]\w+$/.test(t))) cls = keywords.has(key) ? 'k' : 'f';
        else if (code[i + t.length] === '(') cls = 'f';
        else if (/^[A-Z][a-z0-9]+[A-Za-z0-9]*$/.test(t) && l !== 'sql') cls = 't';
        if (cls) { flush(); out += `<span class="tok-${cls}">${escapeHtml(t)}</span>`; } else plain += t;
      } else {
        flush();
        out += `<span class="tok-${kind}">${escapeHtml(t)}</span>`;
      }
      i += t.length;
      continue outer;
    }
    plain += code[i++];
  }
  flush();
  return out;
}

function highlightJson(code) {
  return escapeHtml(code).replace(/(&quot;(?:\\.|[^&\\]|&(?!quot;))*?&quot;)(\s*:)?|\b(true|false|null)\b|-?\b\d+(?:\.\d+)?(?:e[+-]?\d+)?\b/gi,
    (m, str, colon, lit) => str ? `<span class="tok-${colon ? 'a' : 's'}">${str}</span>${colon || ''}` : lit ? `<span class="tok-k">${m}</span>` : `<span class="tok-n">${m}</span>`);
}

function highlightMarkup(code) {
  return escapeHtml(code)
    .replace(/&lt;!--[\s\S]*?--&gt;/g, (m) => `<span class="tok-c">${m}</span>`)
    .replace(/(&lt;\/?)([\w:-]+)([\s\S]*?)(\/?&gt;)/g, (_, open, tag, attrs, close) =>
      `${open}<span class="tok-tag">${tag}</span>${attrs.replace(/([\w:-]+)(=)(&quot;[\s\S]*?&quot;|&#39;[\s\S]*?&#39;|[^\s&]+)?/g,
        (__, a, eq, v) => `<span class="tok-a">${a}</span>${eq}${v ? `<span class="tok-s">${v}</span>` : ''}`)}${close}`);
}
