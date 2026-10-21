using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// synapse — talk to any Synapse model (local or cloud) from a terminal.
// The Synapse app serves an OpenAI-compatible API on 127.0.0.1:11435; this tool is a thin client.

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;
Ansi.Enable();

var baseUrl = (Environment.GetEnvironmentVariable("SYNAPSE_URL") ?? "http://127.0.0.1:11435").TrimEnd('/');
var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
const string Dim = "\u001b[2m", Bold = "\u001b[1m", Violet = "\u001b[38;5;141m", Cyan = "\u001b[38;5;45m", Red = "\u001b[31m", Reset = "\u001b[0m";

var argv = args.ToList();
var think = ParseThink(argv);
var command = argv.FirstOrDefault() ?? "chat";

switch (command)
{
    case "help" or "-h" or "--help" or "/?":
        Usage();
        break;
    case "status":
        Console.WriteLine(await ServerUp() ? $"Synapse is running at {baseUrl}" : "Synapse isn't running.");
        break;
    case "models" or "list":
        await EnsureServer();
        foreach (var (id, name) in await ListModels())
            Console.WriteLine(id + (name != id && !id.EndsWith(name) ? $"  {Dim}{name}{Reset}" : ""));
        break;
    case "run":
    {
        await EnsureServer();
        if (argv.Count < 2) { Usage(); return 1; }
        var prompt = string.Join(" ", argv.Skip(2));
        if (prompt.Length == 0 || Console.IsInputRedirected)
        {
            var piped = Console.IsInputRedirected ? await Console.In.ReadToEndAsync() : "";
            prompt = string.Join("\n\n", new[] { prompt, piped }.Where(s => s.Length > 0));
        }
        var reply = await Stream(argv[1], [new JsonObject { ["role"] = "user", ["content"] = prompt }], think);
        return reply == null ? 1 : 0;
    }
    case "chat":
        await EnsureServer();
        await Chat(argv.Count > 1 ? argv[1] : null, think);
        break;
    default:
        // `synapse <model>` shortcut
        await EnsureServer();
        await Chat(command, think);
        break;
}
return 0;

void Usage()
{
    Console.WriteLine($"""
    {Violet}{Bold}◉ Synapse{Reset} — run AI models from a terminal

    {Bold}Usage{Reset}
      synapse chat [model] [--think off|normal|high|max]   Interactive chat
      synapse run <model> "prompt" [--think …]            One-shot answer (prompt can also come from a pipe)
      synapse models                                      List available models
      synapse status                                      Check the Synapse app is running

    Models look like {Cyan}local/<id>{Reset}, {Cyan}google/gemini-3.8-flash{Reset}, {Cyan}groq/llama-3.3-70b-versatile{Reset}.
    Leave the model out to use your default. In chat: /model <id>, /think <level>, /clear, /exit.
    Example: Get-Content notes.txt | synapse run default "Summarize this"
    """);
}

async Task<bool> ServerUp()
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        using var r = await http.GetAsync(baseUrl + "/health", cts.Token);
        return r.IsSuccessStatusCode;
    }
    catch { return false; }
}

async Task EnsureServer()
{
    if (await ServerUp()) return;
    Console.Error.WriteLine($"{Dim}Starting Synapse…{Reset}");
    // The CLI ships in <Synapse folder>\cli, next to the app.
    var dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
    var exe = new[] { Path.Combine(Path.GetDirectoryName(dir) ?? dir, "Synapse.exe"), Path.Combine(dir, "Synapse.exe") }.FirstOrDefault(File.Exists);
    try
    {
        if (exe != null) Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = true });
    }
    catch { }
    for (var i = 0; i < 60; i++)
    {
        if (await ServerUp()) return;
        await Task.Delay(500);
    }
    Console.Error.WriteLine($"{Red}Couldn't reach Synapse at {baseUrl}. Open the app and make sure the local API server is on (Settings → Terminal & API).{Reset}");
    Environment.Exit(1);
}

async Task<List<(string Id, string Name)>> ListModels()
{
    try
    {
        var obj = JsonNode.Parse(await http.GetStringAsync(baseUrl + "/v1/models"));
        return (obj?["data"] as JsonArray ?? []).Select(m => (m?["id"]?.GetValue<string>() ?? "", m?["name"]?.GetValue<string>() ?? "")).Where(m => m.Item1.Length > 0).ToList();
    }
    catch { return []; }
}

/// Streams a reply and prints the answer (thinking is hidden). Returns the content.
async Task<string?> Stream(string model, List<JsonObject> messages, string? level)
{
    var body = new JsonObject { ["model"] = model == "default" ? "" : model, ["stream"] = true };
    var arr = new JsonArray();
    foreach (var m in messages) arr.Add(m.DeepClone());
    body["messages"] = arr;
    if (level != null) body["synapse_think"] = level;
    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions")
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };
    using var cancel = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancel.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try
    {
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"{Red}Error {(int)resp.StatusCode}: {(text.Length > 800 ? text[..800] : text)}{Reset}");
            return null;
        }
        var content = new StringBuilder();
        var inReasoning = false;
        var inTag = false;
        // Thoughts stay private: show a placeholder while the model thinks, then clear it.
        void Emit(string s, bool reasoning)
        {
            if (reasoning)
            {
                if (!inReasoning) { Console.Write($"{Dim}Thinking…{Reset}"); inReasoning = true; }
                return;
            }
            if (inReasoning) { Console.Write("\r\u001b[K"); inReasoning = false; }
            Console.Write(s);
        }
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cancel.Token));
        string? line;
        while ((line = await reader.ReadLineAsync(cancel.Token)) != null)
        {
            if (!line.StartsWith("data:")) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") break;
            JsonNode? obj;
            try { obj = JsonNode.Parse(payload); } catch { continue; }
            if (obj?["error"] is { } err) { Console.WriteLine($"{Red}{err.ToJsonString()}{Reset}"); return null; }
            if (obj?["choices"]?[0]?["delta"] is not JsonObject delta) continue;
            foreach (var k in new[] { "reasoning_content", "reasoning" })
                if (delta[k] is JsonValue rv && rv.TryGetValue<string>(out var r) && r.Length > 0) Emit(r, true);
            if (delta["content"] is JsonValue cv && cv.TryGetValue<string>(out var c) && c.Length > 0)
            {
                foreach (var open in new[] { "<think>", "<thought>" })
                    if (c.Contains(open)) { c = c.Replace(open, ""); inTag = true; }
                if (inTag)
                {
                    foreach (var close in new[] { "</think>", "</thought>" })
                    {
                        var i = c.IndexOf(close, StringComparison.Ordinal);
                        if (i < 0) continue;
                        Emit(c[..i], true);
                        c = c[(i + close.Length)..];
                        inTag = false;
                    }
                    if (inTag) { Emit(c, true); continue; }
                }
                if (c.Length > 0) { Emit(c, false); content.Append(c); }
            }
        }
        Console.WriteLine(Reset);
        return content.ToString();
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine($"{Reset}\n{Dim}(stopped){Reset}");
        return null;
    }
    catch (Exception e)
    {
        Console.WriteLine($"{Red}{e.Message}{Reset}");
        return null;
    }
    finally { Console.CancelKeyPress -= onCancel; }
}

static string? ParseThink(List<string> a)
{
    var i = a.IndexOf("--think");
    if (i < 0 || i + 1 >= a.Count) return null;
    var v = a[i + 1];
    a.RemoveRange(i, 2);
    return v == "normal" ? "standard" : v;
}

async Task Chat(string? initial, string? level)
{
    var models = await ListModels();
    var model = initial ?? "";
    if (model.Length > 0 && !models.Any(m => m.Id == model) && models.FirstOrDefault(m => m.Id.EndsWith("/" + model) || m.Name == model) is { Id.Length: > 0 } found)
        model = found.Id;
    Console.WriteLine($"{Violet}{Bold}◉ Synapse{Reset}  {Dim}model:{Reset} {Cyan}{(model.Length == 0 ? "default" : model)}{Reset}  {Dim}think:{Reset} {level ?? "normal"}");
    Console.WriteLine($"{Dim}/model <id> · /models · /think off|normal|high|max · /clear · /exit   (Ctrl+C stops a reply){Reset}\n");
    var messages = new List<JsonObject>();
    while (true)
    {
        Console.Write($"{Violet}›{Reset} ");
        var input = Console.ReadLine();
        if (input == null) break;
        input = input.Trim();
        if (input.Length == 0) continue;
        if (input.StartsWith('/'))
        {
            var parts = input.Split(' ', 2);
            switch (parts[0])
            {
                case "/exit" or "/quit": return;
                case "/clear": messages.Clear(); Console.WriteLine($"{Dim}Cleared.{Reset}"); break;
                case "/models": foreach (var m in await ListModels()) Console.WriteLine("  " + m.Id); break;
                case "/model" when parts.Length > 1: model = parts[1]; Console.WriteLine($"{Dim}Model: {model}{Reset}"); break;
                case "/think" when parts.Length > 1: level = parts[1] == "normal" ? "standard" : parts[1]; Console.WriteLine($"{Dim}Think: {parts[1]}{Reset}"); break;
                default: Console.WriteLine($"{Dim}Unknown command{Reset}"); break;
            }
            continue;
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = input });
        Console.WriteLine();
        var reply = await Stream(model, messages, level);
        if (reply != null) messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = reply });
        else messages.RemoveAt(messages.Count - 1);
        Console.WriteLine();
    }
}

static class Ansi
{
    [DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] static extern bool SetConsoleMode(IntPtr h, uint mode);

    /// Colors work in Windows Terminal; classic consoles need virtual terminal processing switched on.
    public static void Enable()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var h = GetStdHandle(-11);
            if (GetConsoleMode(h, out var mode)) SetConsoleMode(h, mode | 0x0004);
        }
        catch { }
    }
}
