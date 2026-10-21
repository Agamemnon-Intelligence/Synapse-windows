// Smoke tests for the chat Markdown renderer: node tests/ui/markdown.test.mjs
import assert from 'node:assert/strict';
import { renderMarkdown, simplifyMath, highlight } from '../../src/Synapse.App/ui/js/markdown.js';

const html = (s) => renderMarkdown(s);

assert.match(html('# Hi'), /<h1>Hi<\/h1>/);
assert.match(html('**b** and *i*'), /<strong>b<\/strong> and <em>i<\/em>/);
assert.ok(!html('<script>alert(1)</script>').includes('<script>'), 'escapes HTML');
assert.ok(!html('[x](javascript:alert(1))').includes('data-href'), 'drops unsafe links');
assert.match(html('[x](https://a.b)'), /data-href="https:\/\/a.b"/);
assert.match(html('- a\n- b\n  - c'), /<ul><li>a<\/li><li>b<ul><li>c<\/li><\/ul><\/li><\/ul>/);
assert.match(html('- a\n\n1. b'), /<\/ul><ol><li>b<\/li><\/ol>/);
assert.match(html('- [x] done'), /checked/);
assert.match(html('| a | b |\n|:-|-:|\n| 1 | 2 |'), /<table>.*text-align:right.*<td style="text-align:right">2<\/td>/s);
assert.match(html('```js\nconst a = 1\n```'), /<span class="tok-k">const<\/span>/);
assert.match(html('```\nunclosed'), /<pre><code>unclosed<\/code><\/pre>/);
assert.match(html('> quote'), /<blockquote><p>quote<\/p><\/blockquote>/);
assert.equal(simplifyMath('17 \\times 23 \\le x^2'), '17 × 23 ≤ x²');
assert.equal(simplifyMath('\\frac{a}{b}'), 'a⁄b');
assert.match(html('costs $5 and $10'), /costs \$5 and \$10/);
assert.match(highlight('Get-Item -Path $HOME', 'powershell'), /tok-f">Get-Item.*tok-a">-Path.*tok-v">\$HOME/);
console.log('markdown: all tests passed');
