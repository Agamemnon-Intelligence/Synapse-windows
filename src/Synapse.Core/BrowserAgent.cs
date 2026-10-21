using System.Text.Json;
using System.Text.Json.Nodes;

namespace Synapse;

/// The in-app browser the AI can drive. The Windows app gives it a WebView2; the agent logic and
/// page scripts live here so every host behaves the same.
public sealed class BrowserAgent(IWebEngine web)
{
    public IWebEngine Web { get; } = web;
    public string? LastAgentAction { get; private set; }
    public bool AgentActive { get; set; }
    public string Url => Web.Url;
    public string Title => Web.Title;
    /// Raised when the agent state changes, so the UI can show "Synapse is using the browser".
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();

    void Act(string action)
    {
        AgentActive = true;
        LastAgentAction = action;
        Changed?.Invoke();
    }

    public void Load(string text)
    {
        var url = Normalize(text);
        if (url != null) Web.Navigate(url);
    }

    public static string? Normalize(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return null;
        if (!s.Contains("://"))
        {
            var isLocal = s.StartsWith("localhost") || s.StartsWith("127.0.0.1");
            if ((s.Contains('.') && !s.Contains(' ')) || isLocal) s = (isLocal ? "http://" : "https://") + s;
            else s = "https://duckduckgo.com/?q=" + Uri.EscapeDataString(s);
        }
        return Uri.TryCreate(s, UriKind.Absolute, out var u) ? u.AbsoluteUri : null;
    }

    public async Task WaitForLoad(double timeout = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeout);
        await Task.Delay(300);
        while (Web.IsLoading && DateTime.UtcNow < deadline) await Task.Delay(200);
        await Task.Delay(300);
        Changed?.Invoke();
    }

    async Task<string?> EvalString(string js)
    {
        var raw = await Web.Eval(js);
        if (raw == null) return null;
        try { return JsonSerializer.Deserialize<string>(raw); } catch { return null; }
    }

    public async Task<string> Navigate(string target)
    {
        Act("Opening page");
        Load(target);
        await WaitForLoad();
        return await ReadPage();
    }

    const string ReadScript = """
        (() => {
          const out = [];
          let n = 0;
          document.querySelectorAll('[data-synapse-id]').forEach(e => e.removeAttribute('data-synapse-id'));
          const els = document.querySelectorAll('a[href], button, input, textarea, select, summary, [role=button], [role=link], [role=tab], [role=menuitem], [role=checkbox], [role=option], [contenteditable=true], [onclick]');
          for (const el of els) {
            const r = el.getBoundingClientRect();
            const st = getComputedStyle(el);
            if (r.width < 2 || r.height < 2 || st.visibility === 'hidden' || st.display === 'none') continue;
            if (el.type === 'hidden') continue;
            n++;
            el.setAttribute('data-synapse-id', String(n));
            const tag = el.tagName.toLowerCase();
            let label = (el.innerText || el.value || el.getAttribute('aria-label') || el.placeholder || el.title || el.name || '').trim().replace(/\s+/g, ' ').slice(0, 80);
            let extra = '';
            if (tag === 'input') extra = ' type=' + (el.type || 'text') + (el.checked ? ' checked' : '');
            if (tag === 'a') extra = ' -> ' + (el.getAttribute('href') || '').slice(0, 80);
            if (tag === 'select') label = Array.from(el.options).map(o => (o.selected ? '*' : '') + o.text.trim()).join(' | ').slice(0, 120);
            out.push('[' + n + '] <' + tag + extra + '> ' + label);
            if (n >= 150) break;
          }
          const text = (document.body ? document.body.innerText : '').replace(/\n{3,}/g, '\n\n').slice(0, 7000);
          return 'URL: ' + location.href + '\nTitle: ' + document.title + '\n\n--- Page text ---\n' + text + '\n\n--- Interactive elements (use their numbers) ---\n' + out.join('\n');
        })()
        """;

    /// Returns page text plus a numbered list of interactive elements.
    public async Task<string> ReadPage() => await EvalString(ReadScript) ?? "Couldn't read the page.";

    public sealed record ElementDetails(string Tag, string Type, bool IsSubmit, bool InPasswordForm, string Label);

    public async Task<ElementDetails?> ElementInfo(int id)
    {
        var js = $$"""
            (() => {
              const el = document.querySelector('[data-synapse-id="{{id}}"]');
              if (!el) return null;
              const form = el.form || el.closest('form');
              const isSubmit = (el.type === 'submit') || (el.tagName === 'BUTTON' && form && (!el.type || el.type === 'submit'));
              const pw = form ? !!form.querySelector('input[type=password]') : false;
              return [el.tagName.toLowerCase(), (el.type || ''), isSubmit ? '1' : '0', pw ? '1' : '0', (el.innerText || el.value || el.getAttribute('aria-label') || '').trim().slice(0, 60)];
            })()
            """;
        var raw = await Web.Eval(js);
        if (raw == null || JsonNodeExt.ParseOrNull(raw) is not JsonArray { Count: 5 } a) return null;
        string S(int i) => a[i]?.GetValue<string>() ?? "";
        return new ElementDetails(S(0), S(1).ToLowerInvariant(), S(2) == "1", S(3) == "1", S(4));
    }

    /// Moves a visible cursor to the element so you can follow what the model does.
    async Task ShowCursor(int id, bool ripple)
    {
        var js = $$"""
            (() => {
              const el = document.querySelector('[data-synapse-id="{{id}}"]'); if (!el) return;
              el.scrollIntoView({block: 'center', behavior: 'instant'});
              const r = el.getBoundingClientRect();
              let c = document.getElementById('__synapse_cursor');
              if (!c) {
                c = document.createElement('div'); c.id = '__synapse_cursor';
                c.style.cssText = 'position:fixed;z-index:2147483647;width:14px;height:14px;margin:-7px 0 0 -7px;border-radius:50%;background:#3d7bff;box-shadow:0 0 0 4px rgba(61,123,255,.25),0 2px 6px rgba(0,0,0,.3);pointer-events:none;transition:left .35s ease,top .35s ease;left:50vw;top:50vh';
                document.documentElement.appendChild(c);
              }
              requestAnimationFrame(() => { c.style.left = (r.left + r.width/2) + 'px'; c.style.top = (r.top + r.height/2) + 'px'; });
              if ({{(ripple ? "true" : "false")}}) setTimeout(() => {
                const w = document.createElement('div');
                w.style.cssText = 'position:fixed;z-index:2147483646;width:10px;height:10px;margin:-5px 0 0 -5px;border-radius:50%;border:2px solid #3d7bff;pointer-events:none;transition:all .45s ease-out;opacity:1;left:' + (r.left + r.width/2) + 'px;top:' + (r.top + r.height/2) + 'px';
                document.documentElement.appendChild(w);
                requestAnimationFrame(() => { w.style.width = w.style.height = '44px'; w.style.margin = '-22px 0 0 -22px'; w.style.opacity = '0'; });
                setTimeout(() => w.remove(), 500);
              }, 380);
            })()
            """;
        await Web.Eval(js);
        await Task.Delay(450);
    }

    public async Task<string> Click(int id)
    {
        Act("Clicking");
        await ShowCursor(id, ripple: true);
        var js = $$"""
            (() => { const el = document.querySelector('[data-synapse-id="{{id}}"]'); if (!el) return 'not found';
              el.scrollIntoView({block: 'center'}); el.focus(); el.click(); return 'ok'; })()
            """;
        if (await EvalString(js) != "ok") return $"Element {id} wasn't found. Call browser_read to refresh the element numbers.";
        await WaitForLoad();
        return "Clicked. " + await ReadPage();
    }

    public async Task<string> Type(int id, string text, bool submit)
    {
        Act("Typing");
        await ShowCursor(id, ripple: false);
        var payload = JsonSerializer.Serialize(text);
        var js = $$"""
            (() => { const el = document.querySelector('[data-synapse-id="{{id}}"]'); if (!el) return 'not found';
              const v = {{payload}}; el.scrollIntoView({block: 'center'}); el.focus();
              if (el.isContentEditable) { el.innerText = v; }
              else if (el.tagName === 'SELECT') { const o = Array.from(el.options).find(o => o.text.trim().toLowerCase() === v.toLowerCase() || o.value === v); if (o) el.value = o.value; }
              else { const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                     const setter = Object.getOwnPropertyDescriptor(proto, 'value'); setter ? setter.set.call(el, v) : (el.value = v); }
              el.dispatchEvent(new Event('input', {bubbles: true})); el.dispatchEvent(new Event('change', {bubbles: true}));
              if ({{(submit ? "true" : "false")}}) {
                el.dispatchEvent(new KeyboardEvent('keydown', {key: 'Enter', code: 'Enter', keyCode: 13, bubbles: true}));
                if (el.form) { el.form.requestSubmit ? el.form.requestSubmit() : el.form.submit(); }
              }
              return 'ok'; })()
            """;
        if (await EvalString(js) != "ok") return $"Element {id} wasn't found. Call browser_read to refresh the element numbers.";
        if (submit) await WaitForLoad();
        return "Typed. " + (submit ? await ReadPage() : "");
    }

    public async Task<string> Scroll(string direction)
    {
        Act("Scrolling");
        var dy = direction == "up" ? "-window.innerHeight*0.8" : "window.innerHeight*0.8";
        await Web.Eval($"window.scrollBy(0, {dy}); 'ok'");
        await Task.Delay(400);
        return await ReadPage();
    }

    public async Task<string> Back()
    {
        Act("Going back");
        Web.GoBack();
        await WaitForLoad();
        return await ReadPage();
    }

    public async Task<string?> Screenshot()
    {
        Act("Looking at the page");
        var png = await Web.CapturePng();
        if (png == null) return null;
        var path = Path.Combine(Paths.Images, $"browser-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png");
        await File.WriteAllBytesAsync(path, png);
        return path;
    }
}
