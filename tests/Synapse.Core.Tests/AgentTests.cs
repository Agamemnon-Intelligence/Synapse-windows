using System.Text;
using System.Text.Json.Nodes;

namespace Synapse.Tests;

[CollectionDefinition("app", DisableParallelization = true)]
public class AppCollection { }

/// Runs the real agent loop against a fake OpenAI-compatible server.
[Collection("app")]
public sealed class AgentTests : IDisposable
{
    readonly string _home = Path.Combine(Path.GetTempPath(), "synapse-tests-" + Guid.NewGuid().ToString("N")[..8]);
    readonly SingleThreadContext _main = new();
    readonly HttpServer _mock;
    readonly List<JsonObject> _requests = [];

    public AgentTests()
    {
        Paths.Root = _home;
        _mock = new HttpServer(0, Mock);
        _mock.Start();
        AppSettings.Shared = new AppSettings
        {
            CustomEnabled = true,
            CustomBaseUrl = $"http://127.0.0.1:{_mock.Port}/v1",
            DefaultModel = new ModelRef(ProviderId.Custom, "mock", "mock"),
            ApiServerPort = 0,
        };
    }

    public void Dispose()
    {
        _mock.Stop();
        _main.Dispose();
        try { Directory.Delete(_home, true); } catch { }
    }

    async Task Mock(HttpRequest req, HttpResponder res)
    {
        if (req.Path.EndsWith("/models")) { await res.SendJson(new { data = new[] { new { id = "mock" } } }); return; }
        var body = (JsonObject)JsonNode.Parse(req.BodyText)!;
        lock (_requests) _requests.Add(body);
        var messages = (JsonArray)body["messages"]!;
        var last = messages[^1]!;
        var text = last["content"]?.ToJsonString() ?? "";
        await res.BeginStream(200, "text/event-stream");
        async Task Chunk(object delta) => await res.Write($"data: {new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = JsonNode.Parse(Json.Serialize(delta)) }) }.ToJsonString()}\n\n");
        if (last["role"]!.GetValue<string>() == "user" && text.Contains("list"))
        {
            await Chunk(new { tool_calls = new[] { new { index = 0, id = "c1", function = new { name = "list_files", arguments = "{\"path\":" } } } });
            await Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "\".\"}" } } } });
        }
        else if (last["role"]!.GetValue<string>() == "user" && text.Contains("write"))
        {
            var target = Path.Combine(_home, "outside.txt").Replace("\\", "\\\\");
            await Chunk(new { tool_calls = new[] { new { index = 0, id = "w1", function = new { name = "write_file", arguments = $"{{\"path\":\"{target}\",\"content\":\"hi\"}}" } } } });
        }
        else if (last["role"]!.GetValue<string>() == "tool")
        {
            await Chunk(new { content = "Done: " + last["content"]!.GetValue<string>().Split('\n').Length + " lines" });
        }
        else
        {
            await Chunk(new { reasoning_content = "hmm" });
            await Chunk(new { content = "<think>more</think>Hello " });
            await Chunk(new { content = "world" });
        }
        await res.Write("data: {\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":3}}\n\n");
        await res.Write("data: [DONE]\n\n");
        res.End();
    }

    async Task<AppState> MakeApp()
    {
        var app = await _main.Run(() => Task.FromResult(new AppState(new GenericPlatform(new FileSecretStore(Path.Combine(_home, "keys.json"))))));
        await _main.Run(() => app.RefreshRemoteModels().ContinueWith(_ => true));
        return app;
    }

    static async Task WaitFor(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task StreamsAReplyAndHidesThinking()
    {
        var app = await MakeApp();
        Guid id = default;
        await _main.Run(() => { id = app.NewChat(); app.Send(id, "hello", []); });
        await WaitFor(() => _main.Run(() => Task.FromResult(!app.Generating.Contains(id))).Result);
        var msgs = await _main.Run(() => Task.FromResult(app.FindChat(id)!.Messages.ToList()));
        var reply = msgs.Last();
        Assert.Equal(Role.Assistant, reply.Role);
        Assert.Equal("Hello world", reply.Content);
        Assert.Contains("hmm", reply.Reasoning);
        Assert.Contains("more", reply.Reasoning);
        Assert.Equal(3, reply.CompletionTokens);
        Assert.Equal("hello", msgs[0].Content);
        Assert.Equal(1, await _main.Run(() => Task.FromResult(app.Usage.Today(ProviderId.Custom).Requests)));
    }

    [Fact]
    public async Task RunsToolsInsideAProject()
    {
        var app = await MakeApp();
        Guid id = default;
        await _main.Run(() =>
        {
            var p = app.NewProject("Test Project", Path.Combine(_home, "proj"));
            File.WriteAllText(Path.Combine(_home, "proj", "a.txt"), "x");
            File.WriteAllText(Path.Combine(_home, "proj", "b.txt"), "y");
            id = app.NewChat(p);
            app.Send(id, "list my files", []);
        });
        await WaitFor(() => _main.Run(() => Task.FromResult(!app.Generating.Contains(id))).Result);
        var msgs = await _main.Run(() => Task.FromResult(app.FindChat(id)!.Messages.ToList()));
        Assert.Equal([Role.User, Role.Assistant, Role.Tool, Role.Assistant], msgs.Select(m => m.Role));
        Assert.Equal("list_files", msgs[1].ToolCalls.Single().Name);
        Assert.Contains("a.txt", msgs[2].Content);
        Assert.Equal("Done: 3 lines", msgs[3].Content);
        // The second request carries the tool call and its result in order.
        var second = _requests.Last();
        var roles = ((JsonArray)second["messages"]!).Select(m => m!["role"]!.GetValue<string>()).ToList();
        Assert.Equal(["system", "user", "assistant", "tool"], roles);
        Assert.Contains("Project: Test Project", ((JsonArray)second["messages"]!)[0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task AsksBeforeWritingOutsideTheProject()
    {
        var app = await MakeApp();
        Guid id = default;
        await _main.Run(() =>
        {
            id = app.NewChat();
            app.FindChat(id)!.Capabilities.ComputerAccess = true;
            app.Send(id, "write a file", []);
        });
        await WaitFor(() => _main.Run(() => Task.FromResult(app.Approvals.Count == 1)).Result);
        var approval = await _main.Run(() => Task.FromResult(app.Approvals[0]));
        Assert.Equal("Write file", approval.Title);
        Assert.Equal(id, approval.ChatId);
        Assert.False(File.Exists(Path.Combine(_home, "outside.txt")));
        await _main.Run(() => app.ResolveApproval(approval.Id, true));
        await WaitFor(() => _main.Run(() => Task.FromResult(!app.Generating.Contains(id))).Result);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_home, "outside.txt")));
    }

    [Fact]
    public async Task StoppingReleasesApprovals()
    {
        var app = await MakeApp();
        Guid id = default;
        await _main.Run(() =>
        {
            id = app.NewChat();
            app.FindChat(id)!.Capabilities.ComputerAccess = true;
            app.Send(id, "write a file", []);
        });
        await WaitFor(() => _main.Run(() => Task.FromResult(app.Approvals.Count == 1)).Result);
        await _main.Run(() => app.Stop(id));
        await WaitFor(() => _main.Run(() => Task.FromResult(app.Approvals.Count == 0 && !app.Generating.Contains(id))).Result);
        Assert.False(File.Exists(Path.Combine(_home, "outside.txt")));
    }

    [Fact]
    public async Task ServesTheLocalApi()
    {
        AppSettings.Shared.ApiServerPort = 0;
        var app = await MakeApp();
        var port = await _main.Run(() => Task.FromResult(app.ApiPort!.Value));
        using var http = new HttpClient();
        var models = await http.GetStringAsync($"http://127.0.0.1:{port}/v1/models");
        Assert.Contains("custom/mock", models);
        var body = new StringContent("""{"model":"custom/mock","stream":true,"messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"http://127.0.0.1:{port}/v1/chat/completions", body);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("world", text);
        Assert.Contains("[DONE]", text);
        Assert.Equal("mock", _requests.Last()["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task PersistsChatsAcrossRestarts()
    {
        var app = await MakeApp();
        Guid id = default;
        await _main.Run(() => { id = app.NewChat(); app.FindChat(id)!.Title = "Keep me"; app.FindChat(id)!.Messages.Add(new ChatMessage(Role.User, "x")); app.Save(); app.Shutdown(); });
        var again = await MakeApp();
        var title = await _main.Run(() => Task.FromResult(again.FindChat(id)?.Title));
        Assert.Equal("Keep me", title);
        await _main.Run(() => again.Shutdown());
    }

    [Fact]
    public async Task BridgeAnswersCommands()
    {
        var app = await MakeApp();
        var posted = new List<string>();
        var bridge = await _main.Run(() => Task.FromResult(new BridgeHost(app, s => { lock (posted) posted.Add(s); }, "/file?path=")));
        await _main.Run(() => bridge.Receive("""{"id":1,"cmd":"ready","args":{}}"""));
        await _main.Run(() => bridge.Receive("""{"id":2,"cmd":"newChat","args":{}}"""));
        await WaitFor(() => { lock (posted) return posted.Count(p => p.Contains("\"type\":\"reply\"")) >= 2; });
        string ready;
        lock (posted) ready = posted.First(p => p.Contains("\"id\":1"));
        var node = JsonNode.Parse(ready)!;
        Assert.True(node["ok"]!.GetValue<bool>());
        Assert.NotNull(node["result"]!["app"]!["chats"]);
        Assert.Equal("custom", node["result"]!["app"]!["providers"]![3]!["id"]!.GetValue<string>());
    }
}
