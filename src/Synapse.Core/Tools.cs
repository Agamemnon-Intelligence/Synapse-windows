using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Synapse;

public sealed class ToolResult(string text)
{
    public string Text { get; set; } = text;
    public List<string> Images { get; set; } = [];
    public List<string> Presented { get; set; } = [];
    public List<string> Links { get; set; } = [];
}

public sealed record ToolContext(AppState App, Chat Chat, Capabilities Capabilities, Project? Project, bool AutoApprove, CancellationToken Ct)
{
    public string WorkDir => Project?.Folder ?? Paths.Home;
    public bool FilesEnabled => Project != null || Capabilities.ComputerAccess;
}

public static class Tools
{
    // MARK: Definitions

    public static JsonArray Definitions(ToolContext ctx)
    {
        var defs = new JsonArray();
        void Add(string name, string desc, JsonObject? props = null, params string[] required)
        {
            var req = new JsonArray();
            foreach (var r in required) req.Add(r);
            defs.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = desc,
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props ?? [], ["required"] = req },
                },
            });
        }
        static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
        static JsonObject Int(string d) => new() { ["type"] = "integer", ["description"] = d };
        static JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };
        static JsonObject Enum(string d, params string[] values)
        {
            var arr = new JsonArray();
            foreach (var v in values) arr.Add(v);
            return new JsonObject { ["type"] = "string", ["enum"] = arr, ["description"] = d };
        }

        var caps = ctx.Capabilities;
        var shell = ctx.App.Platform.ShellName;
        if (caps.WebSearch)
        {
            Add("web_search", "Search the web. Returns titles, URLs and snippets. Use for current events or facts you aren't sure about.",
                new JsonObject { ["query"] = Str("Search query") }, "query");
            Add("fetch_url", "Download a web page (or PDF) and return its readable text.", new JsonObject { ["url"] = Str("Full URL") }, "url");
        }
        if (caps.ImageGeneration)
            Add("generate_image", "Create an image from a text description. The image is shown to the user automatically.",
                new JsonObject { ["prompt"] = Str("Detailed description of the image"), ["aspect"] = Enum("Aspect ratio", "square", "landscape", "portrait") },
                "prompt");
        if (ctx.FilesEnabled)
        {
            var scope = caps.ComputerAccess ? "Paths may be absolute (C:\\…) or relative to the working folder." : "Paths are relative to the project folder.";
            Add("list_files", "List files and folders. " + scope, new JsonObject { ["path"] = Str("Folder path, default is the working folder") });
            Add("read_file", "Read a text, code, PDF, Word, PowerPoint or Excel file. " + scope, new JsonObject { ["path"] = Str("File path") }, "path");
            Add("write_file", "Create or overwrite a text file. " + scope,
                new JsonObject { ["path"] = Str("File path"), ["content"] = Str("Full file contents") }, "path", "content");
            Add("edit_file", "Replace an exact piece of text in a file. old_text must match exactly once unless replace_all is true. " + scope,
                new JsonObject
                {
                    ["path"] = Str("File path"),
                    ["old_text"] = Str("Exact text to replace, including whitespace"),
                    ["new_text"] = Str("Replacement text"),
                    ["replace_all"] = Bool("Replace every occurrence"),
                }, "path", "old_text", "new_text");
            Add("search_files", "Search file contents under a folder (like grep). Returns matching lines with file names and line numbers. " + scope,
                new JsonObject
                {
                    ["pattern"] = Str("Text or regular expression to find"),
                    ["path"] = Str("Folder to search, default is the working folder"),
                    ["glob"] = Str("Only files matching this name pattern, e.g. *.cs or *.md"),
                    ["regex"] = Bool("Treat pattern as a regular expression"),
                }, "pattern");
            Add("present_file", "Show a file to the user as a preview card (documents, images, code, etc).", new JsonObject { ["path"] = Str("File path") }, "path");
        }
        if (caps.ComputerAccess)
        {
            Add("run_shell", $"Run a {shell} command on the user's Windows PC and return its output. The user approves each command. " +
                             "Use PowerShell syntax (Get-ChildItem, Start-Process, COM objects via New-Object -ComObject, etc).",
                new JsonObject { ["command"] = Str($"{shell} command"), ["cwd"] = Str("Working directory (optional)") }, "command");
            Add("open", "Open an app, file, folder or URL with its default handler (like double-clicking it).",
                new JsonObject { ["target"] = Str("App name (e.g. 'notepad', 'excel'), path or URL") }, "target");
            Add("take_screenshot", "Take a screenshot of the user's screen(s) so you can see it.");
            Add("get_clipboard", "Read the text on the user's clipboard.");
            Add("set_clipboard", "Put text on the user's clipboard.", new JsonObject { ["text"] = Str("Text to copy") }, "text");
        }
        if (caps.Browser)
        {
            Add("browser_navigate", "Open a URL (or search terms) in Synapse's built-in browser. Returns the page text and numbered interactive elements.",
                new JsonObject { ["url"] = Str("URL or search terms") }, "url");
            Add("browser_read", "Re-read the current browser page, with numbered interactive elements.");
            Add("browser_click", "Click a numbered element from the last browser_read.", new JsonObject { ["element"] = Int("Element number") }, "element");
            Add("browser_type", "Type text into a numbered input element. Set submit=true to press Enter.",
                new JsonObject { ["element"] = Int("Element number"), ["text"] = Str("Text to type"), ["submit"] = Bool("Press Enter afterwards") },
                "element", "text");
            Add("browser_scroll", "Scroll the page.", new JsonObject { ["direction"] = Enum("Direction", "up", "down") }, "direction");
            Add("browser_back", "Go back to the previous page.");
            Add("browser_screenshot", "Capture the browser so you can see the page visually.");
        }
        if (caps.Websites)
        {
            Add("create_website", "Build a website and host it on localhost. Provide every file (HTML, CSS, JS). Returns the local URL, which opens in the browser.",
                new JsonObject
                {
                    ["name"] = Str("Short site name"),
                    ["files"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["description"] = "Files to write; include index.html",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["path"] = Str("Relative path, e.g. index.html"), ["content"] = Str("File contents") },
                            ["required"] = new JsonArray("path", "content"),
                        },
                    },
                }, "name", "files");
        }
        return defs;
    }

    // MARK: Execution

    public static async Task<ToolResult> Run(ToolCall call, ToolContext ctx)
    {
        var args = JsonNodeExt.ParseOrNull(call.Arguments.IfEmpty("{}")) as JsonObject ?? [];
        string S(string k) => args.Str(k);
        var app = ctx.App;
        var browser = app.Platform.Browser;

        switch (call.Name)
        {
            case "web_search": return await WebSearch(S("query"), ctx.Ct);
            case "fetch_url": return await FetchUrl(S("url"), ctx.Ct);
            case "generate_image": return await ImageGen.Generate(app, S("prompt"), S("aspect"), ctx.Ct);

            case "list_files":
            {
                if (Resolve(S("path").IfEmpty("."), ctx) is not { } dir) return Denied();
                if (!Directory.Exists(dir)) return new ToolResult($"Folder not found: {dir}");
                var lines = new List<string>();
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(300))
                        lines.Add("📁 " + Path.GetFileName(d) + "\\");
                    foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(Math.Max(0, 300 - lines.Count)))
                    {
                        long size = 0;
                        try { size = new FileInfo(f).Length; } catch { }
                        lines.Add($"📄 {Path.GetFileName(f)}  ({Util.FormatBytes(size)})");
                    }
                }
                catch (Exception e) { return new ToolResult("Error: " + e.Message); }
                return new ToolResult(dir + "\n" + (lines.Count == 0 ? "(empty)" : string.Join("\n", lines)));
            }

            case "read_file":
            {
                if (Resolve(S("path"), ctx) is not { } file) return Denied();
                return new ToolResult(TextExtract.Read(file).Truncated(60_000));
            }

            case "write_file":
            {
                if (Resolve(S("path"), ctx) is not { } file) return Denied();
                if (!InsideProject(file, ctx) && !await Approve(ctx, "Write file", $"Synapse wants to write {file}")) return Declined();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllTextAsync(file, S("content"), new UTF8Encoding(false), ctx.Ct);
                    return new ToolResult($"Wrote {file}") { Presented = [file] };
                }
                catch (Exception e) { return new ToolResult("Error: " + e.Message); }
            }

            case "edit_file":
            {
                if (Resolve(S("path"), ctx) is not { } file) return Denied();
                if (!File.Exists(file)) return new ToolResult($"File not found: {file}");
                var oldText = S("old_text");
                if (oldText.Length == 0) return new ToolResult("old_text is empty.");
                string text;
                try { text = await File.ReadAllTextAsync(file, ctx.Ct); } catch (Exception e) { return new ToolResult("Error: " + e.Message); }
                // Models often send \n while the file uses \r\n.
                if (!text.Contains(oldText) && text.Contains("\r\n") && text.Contains(oldText.Replace("\n", "\r\n")))
                    oldText = oldText.Replace("\n", "\r\n");
                var count = CountOccurrences(text, oldText);
                if (count == 0) return new ToolResult("old_text wasn't found. Read the file again and copy the text exactly.");
                var all = args.Bool("replace_all");
                if (count > 1 && !all) return new ToolResult($"old_text appears {count} times. Add more surrounding text so it's unique, or set replace_all.");
                var newText = S("new_text");
                if (text.Contains("\r\n") && !newText.Contains("\r\n")) newText = newText.Replace("\n", "\r\n");
                var updated = all ? text.Replace(oldText, newText) : ReplaceFirst(text, oldText, newText);
                if (!InsideProject(file, ctx) && !await Approve(ctx, "Edit file", $"Synapse wants to edit {file}:\n\n- {oldText.Truncated(800)}\n+ {newText.Truncated(800)}"))
                    return Declined();
                try { await File.WriteAllTextAsync(file, updated, new UTF8Encoding(false), ctx.Ct); }
                catch (Exception e) { return new ToolResult("Error: " + e.Message); }
                return new ToolResult($"Edited {file} ({(all ? count : 1)} replacement{(all && count > 1 ? "s" : "")}).");
            }

            case "search_files":
            {
                if (Resolve(S("path").IfEmpty("."), ctx) is not { } dir) return Denied();
                return await Task.Run(() => SearchFiles(dir, S("pattern"), S("glob"), args.Bool("regex"), ctx.Ct), ctx.Ct);
            }

            case "present_file":
            {
                if (Resolve(S("path"), ctx) is not { } file) return Denied();
                if (!File.Exists(file)) return new ToolResult($"File not found: {file}");
                var isImage = Util.IsImage(file);
                return new ToolResult("Shown to the user.") { Images = isImage ? [file] : [], Presented = isImage ? [] : [file] };
            }

            case "run_shell":
            {
                var cmd = S("command");
                var cwd = Resolve(S("cwd").IfEmpty("."), ctx) ?? ctx.WorkDir;
                if (!Directory.Exists(cwd)) cwd = ctx.WorkDir;
                if (!await Approve(ctx, "Run command", $"In {cwd}:\n\n{cmd}")) return Declined();
                var r = await app.Platform.RunShell(cmd, cwd, app.Settings.ShellTimeout, ctx.Ct);
                return new ToolResult($"exit {r.Status}\n" + r.Output.Truncated(30_000));
            }

            case "open":
            {
                var target = S("target");
                if (!await Approve(ctx, "Open", target)) return Declined();
                var (ok, message) = await app.Platform.Open(target);
                return new ToolResult(ok ? $"Opened {target}" : "Failed: " + message);
            }

            case "take_screenshot":
            {
                if (!await Approve(ctx, "Take a screenshot", "Synapse wants to capture your screen and send it to the model.")) return Declined();
                var path = Path.Combine(Paths.Images, $"screen-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.png");
                var error = await app.Platform.TakeScreenshot(path);
                if (error != null || !File.Exists(path)) return new ToolResult("Screenshot failed. " + error);
                return new ToolResult("Screenshot captured (attached as an image).") { Images = [path] };
            }

            case "get_clipboard":
            {
                if (!await Approve(ctx, "Read clipboard", "Synapse wants to read the text on your clipboard.")) return Declined();
                var text = await app.Platform.GetClipboardText();
                return new ToolResult(string.IsNullOrEmpty(text) ? "(The clipboard has no text.)" : text.Truncated(30_000));
            }

            case "set_clipboard":
                await app.Platform.SetClipboardText(S("text"));
                return new ToolResult("Copied to the clipboard.");

            case "browser_navigate":
            case "browser_read":
            case "browser_click":
            case "browser_type":
            case "browser_scroll":
            case "browser_back":
            case "browser_screenshot":
                if (browser == null) return new ToolResult("The built-in browser isn't available here. Use fetch_url instead.");
                return await RunBrowser(call.Name, args, ctx, browser);

            case "create_website":
            {
                var name = S("name").IfEmpty("site");
                var files = new List<(string Path, string Content)>();
                if (args["files"] is JsonArray arr)
                    foreach (var f in arr)
                        if (f.StrOrNull("path") is { } p && f.StrOrNull("content") is { } c) files.Add((p, c));
                if (files.Count == 0) return new ToolResult("No files given.");
                try
                {
                    var parent = ctx.Project != null ? Path.Combine(ctx.Project.Folder, "sites") : null;
                    var site = app.Sites.Create(name, files, parent);
                    var url = site.Url!;
                    if (browser != null)
                    {
                        app.ShowBrowserPanel = true;
                        app.NotifyApp();
                        browser.Load(url);
                    }
                    return new ToolResult($"Website is live at {url} (folder: {site.Folder}).{(browser != null ? " It's open in the browser." : "")}") { Links = [url] };
                }
                catch (Exception e) { return new ToolResult("Error: " + e.Message); }
            }

            default:
                return new ToolResult($"Unknown tool {call.Name}");
        }
    }

    static async Task<ToolResult> RunBrowser(string name, JsonObject args, ToolContext ctx, BrowserAgent b)
    {
        var app = ctx.App;
        var id = args.Int("element");
        switch (name)
        {
            case "browser_navigate":
                if (!app.ShowBrowserPanel) { app.ShowBrowserPanel = true; app.NotifyApp(); }
                return new ToolResult((await b.Navigate(args.Str("url"))).Truncated(14_000));
            case "browser_read":
                return new ToolResult((await b.ReadPage()).Truncated(14_000));
            case "browser_click":
            {
                if (await b.ElementInfo(id) is { } info && (info.IsSubmit || info.InPasswordForm))
                    if (!await Approve(ctx, "Submit in browser", $"Click “{info.Label}” on {b.Url}? This may submit a form.", force: true))
                        return Declined();
                return new ToolResult((await b.Click(id)).Truncated(14_000));
            }
            case "browser_type":
            {
                if (await b.ElementInfo(id) is { Type: "password" })
                    return new ToolResult("Typing passwords is blocked. Ask the user to type it themselves in the browser panel.");
                var submit = args.Bool("submit");
                if (submit && !await Approve(ctx, "Submit in browser", $"Type “{args.Str("text")}” and press Enter on {b.Url}?", force: true))
                    return Declined();
                return new ToolResult((await b.Type(id, args.Str("text"), submit)).Truncated(14_000));
            }
            case "browser_scroll":
                return new ToolResult((await b.Scroll(args.Str("direction"))).Truncated(14_000));
            case "browser_back":
                return new ToolResult((await b.Back()).Truncated(14_000));
            default:
            {
                var path = await b.Screenshot();
                return path == null ? new ToolResult("Couldn't capture the browser.") : new ToolResult("Browser screenshot attached.") { Images = [path] };
            }
        }
    }

    // MARK: Helpers

    static ToolResult Denied() => new("Access denied: that path is outside the project folder. Turn on Computer Access to reach other files.");
    static ToolResult Declined() => new("The user declined this action.");

    static Task<bool> Approve(ToolContext ctx, string title, string detail, bool force = false) =>
        ctx.AutoApprove && !force ? Task.FromResult(true) : ctx.App.RequestApproval(title, detail, ctx.Ct, ctx.Chat.Id);

    static bool InsideProject(string file, ToolContext ctx) => ctx.Project != null && Util.IsInside(file, ctx.Project.Folder);

    public static string? Resolve(string path, ToolContext ctx)
    {
        var expanded = Util.ExpandHome(path.Trim().Trim('"'));
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(ctx.WorkDir, expanded)); }
        catch { return null; }
        if (ctx.Capabilities.ComputerAccess) return full;
        if (ctx.Project != null && Util.IsInside(full, ctx.Project.Folder)) return full;
        return null;
    }

    static int CountOccurrences(string text, string sub)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }

    static string ReplaceFirst(string text, string a, string b)
    {
        var i = text.IndexOf(a, StringComparison.Ordinal);
        return i < 0 ? text : text[..i] + b + text[(i + a.Length)..];
    }

    static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", ".vs", ".venv", "venv", "__pycache__", "dist", "build", ".next", "target" };

    static ToolResult SearchFiles(string root, string pattern, string glob, bool regex, CancellationToken ct)
    {
        if (pattern.Length == 0) return new ToolResult("pattern is empty.");
        if (!Directory.Exists(root)) return new ToolResult($"Folder not found: {root}");
        Regex re;
        try { re = new Regex(regex ? pattern : Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
        catch (Exception e) { return new ToolResult("Bad pattern: " + e.Message); }
        Regex? globRe = glob.Length == 0 ? null
            : new Regex("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);
        var hits = new List<string>();
        int scanned = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && hits.Count < 200 && scanned < 5000)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                    if (!SkipDirs.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.')) stack.Push(d);
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    if (globRe != null && !globRe.IsMatch(Path.GetFileName(f))) continue;
                    var info = new FileInfo(f);
                    if (info.Length > 2_000_000) continue;
                    scanned++;
                    string[] lines;
                    try
                    {
                        var bytes = File.ReadAllBytes(f);
                        if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) continue;   // binary
                        lines = Encoding.UTF8.GetString(bytes).Split('\n');
                    }
                    catch { continue; }
                    for (var i = 0; i < lines.Length && hits.Count < 200; i++)
                    {
                        try
                        {
                            if (re.IsMatch(lines[i]))
                                hits.Add($"{Path.GetRelativePath(root, f)}:{i + 1}: {lines[i].Trim().Truncated(200)}");
                        }
                        catch (RegexMatchTimeoutException) { }
                    }
                }
            }
            catch { }
        }
        return new ToolResult(hits.Count == 0 ? $"No matches for “{pattern}” in {root}." : string.Join("\n", hits) + (hits.Count >= 200 ? "\n…(more matches not shown)" : ""));
    }

    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0";

    public static async Task<ToolResult> WebSearch(string query, CancellationToken ct)
    {
        string html;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/")
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query)]),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            using var resp = await ChatClient.Http.SendAsync(req, cts.Token);
            html = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new ToolResult("Search failed (offline?)."); }
        var results = ParseSearchResults(html);
        return new ToolResult(results.Count == 0 ? $"No results for “{query}”."
            : $"Results for “{query}”:\n\n" + string.Join("\n\n", results.Select((r, i) => $"{i + 1}. {r.Title}\n   {r.Url}\n   {r.Snippet}")));
    }

    internal static List<(string Title, string Url, string Snippet)> ParseSearchResults(string html)
    {
        var output = new List<(string, string, string)>();
        foreach (var b in html.Split("class=\"result__body").Skip(1).Take(10))
        {
            var href = FirstMatch("class=\"result__a\"[^>]*href=\"([^\"]+)\"", b) ?? FirstMatch("href=\"([^\"]+)\"[^>]*class=\"result__a\"", b);
            if (href == null) continue;
            var link = href.Replace("&amp;", "&");
            var m = Regex.Match(link, "uddg=([^&]+)");
            if (m.Success) link = Uri.UnescapeDataString(m.Groups[1].Value);
            if (link.StartsWith("//")) link = "https:" + link;
            if (link.Contains("duckduckgo.com/y.js")) continue;   // ads
            var title = StripHtml(FirstMatch("class=\"result__a\"[^>]*>(.*?)</a>", b) ?? "");
            var snippet = StripHtml(FirstMatch("class=\"result__snippet\"[^>]*>(.*?)</a>", b) ?? "");
            output.Add((title, link, snippet));
            if (output.Count >= 8) break;
        }
        return output;
    }

    public static async Task<ToolResult> FetchUrl(string s, CancellationToken ct)
    {
        if (!Uri.TryCreate(s.Contains("://") ? s : "https://" + s, UriKind.Absolute, out var url)) return new ToolResult("Bad URL");
        byte[] data;
        string type;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/pdf;q=0.9,*/*;q=0.8");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var resp = await ChatClient.Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.NotFound)
                return new ToolResult($"Couldn't load {s} (HTTP {(int)resp.StatusCode}).");
            data = await resp.Content.ReadAsByteArrayAsync(cts.Token);
            type = resp.Content.Headers.ContentType?.MediaType ?? "";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new ToolResult($"Couldn't load {s}"); }
        if (type.Contains("pdf") || url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return new ToolResult(TextExtract.PdfText(data).Truncated(40_000));
        var html = Encoding.UTF8.GetString(data);
        if (!type.Contains("html") && !html.Contains("<html", StringComparison.OrdinalIgnoreCase)) return new ToolResult(html.Truncated(40_000));
        var title = StripHtml(FirstMatch("<title[^>]*>(.*?)</title>", html) ?? "");
        return new ToolResult($"Title: {title}\nURL: {url}\n\n" + HtmlToText(html).Truncated(40_000));
    }

    static string? FirstMatch(string pattern, string s)
    {
        try
        {
            var m = Regex.Match(s, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
            return m.Success && m.Groups.Count > 1 ? m.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }
    }

    public static string StripHtml(string s) => WebUtility.HtmlDecode(Regex.Replace(s, "<[^>]+>", "")).Trim();

    public static string HtmlToText(string html)
    {
        var s = html;
        foreach (var tag in new[] { "script", "style", "noscript", "svg", "head", "nav", "footer", "iframe", "template" })
            s = Regex.Replace(s, $"<{tag}[\\s\\S]*?</{tag}>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<!--[\\s\\S]*?-->", " ");
        s = Regex.Replace(s, "<(br|/p|/div|/h[1-6]|/li|/tr|/section|/article)[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        s = StripHtml(s);
        s = Regex.Replace(s, "[ \\t\\u00a0]+", " ");
        s = Regex.Replace(s, "\\n\\s*\\n+", "\n\n");
        return s.Trim();
    }
}
