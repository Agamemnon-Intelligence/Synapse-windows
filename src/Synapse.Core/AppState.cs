using System.Text;
using System.Text.Json.Nodes;

namespace Synapse;

public enum ChangeKind { App, Chat, Message }

/// What changed. Chat = messages were added/removed (or the chat itself changed); Message = one message's content moved.
public readonly record struct Change(ChangeKind Kind, Guid? ChatId = null, Guid? MessageId = null);

public sealed class ApprovalRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public Guid? ChatId { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

sealed class SavedState
{
    public List<Chat> Chats { get; set; } = [];
    public List<Project> Projects { get; set; } = [];
    public List<LocalModel> LocalModels { get; set; } = [];
}

/// All app data and actions. Like @MainActor on the Mac, every member runs on the main (UI) thread:
/// construct it there, and hop back with OnMain from other threads.
public sealed class AppState
{
    // Data
    public List<Chat> Chats { get; private set; } = [];
    public List<Project> Projects { get; private set; } = [];
    public List<LocalModel> LocalModels { get; private set; } = [];
    public Dictionary<ProviderId, List<ModelRef>> RemoteModels { get; } = [];
    public Dictionary<ProviderId, string> RemoteErrors { get; } = [];

    // UI
    /// "chat:<id>", "project:<id>", "models", "sites", "usage" or "settings".
    public string? Selection { get; set; }
    public bool ShowBrowserPanel { get; set; }
    public List<ApprovalRequest> Approvals { get; } = [];
    public HashSet<Guid> Generating { get; } = [];
    /// Set by the host so finished replies can notify when the window isn't in front.
    public bool WindowFocused { get; set; } = true;
    public int KeysVersion { get; private set; }

    // Services
    public IPlatform Platform { get; }
    public AppSettings Settings => AppSettings.Shared;
    public LocalEngine Engine { get; }
    public DownloadManager Downloads { get; }
    public SiteManager Sites { get; }
    public UsageTracker Usage { get; }
    public SynchronizationContext Main { get; }
    public int? ApiPort => _apiServer?.Port;
    public string? ApiError { get; private set; }

    public event Action<Change>? Changed;

    readonly Dictionary<Guid, CancellationTokenSource> _tasks = [];
    HttpServer? _apiServer;
    bool _saveScheduled;

    public AppState(IPlatform platform)
    {
        Main = SynchronizationContext.Current ?? throw new InvalidOperationException("Create AppState on the UI thread (it needs a SynchronizationContext).");
        Platform = platform;
        ChatClient.Keys = platform.Secrets;
        HuggingFace.Keys = platform.Secrets;
        Engine = new LocalEngine(platform, NotifyApp);
        Downloads = new DownloadManager(NotifyApp)
        {
            OnComplete = model =>
            {
                LocalModels.RemoveAll(m => m.Id == model.Id);
                LocalModels.Add(model);
                SaveSoon();
                NotifyApp();
                if (!WindowFocused) Platform.Notify("Download finished", $"{model.DisplayName} is ready to chat.");
            },
        };
        Sites = new SiteManager(NotifyApp);
        Usage = new UsageTracker(NotifyApp);
        Load();
        Sites.Refresh();
        var first = Chats.OrderByDescending(c => c.UpdatedAt).FirstOrDefault();
        if (first != null) Selection = "chat:" + first.Id; else NewChat();
        _ = RefreshRemoteModels();
        StartApiServer();
    }

    // MARK: Change notifications

    public void NotifyApp() => Changed?.Invoke(new Change(ChangeKind.App));
    public void NotifyChat(Guid chatId) { Changed?.Invoke(new Change(ChangeKind.Chat, chatId)); SaveSoon(); }
    public void NotifyMessage(Guid chatId, Guid messageId) => Changed?.Invoke(new Change(ChangeKind.Message, chatId, messageId));

    // MARK: Main thread

    public bool OnMainThread => SynchronizationContext.Current == Main;

    public Task<T> OnMain<T>(Func<T> f)
    {
        if (OnMainThread) return Task.FromResult(f());
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Main.Post(_ => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } }, null);
        return tcs.Task;
    }

    public Task OnMain(Action f) => OnMain(() => { f(); return true; });

    public Task<T> OnMainAsync<T>(Func<Task<T>> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Main.Post(async _ => { try { tcs.SetResult(await f()); } catch (Exception e) { tcs.SetException(e); } }, null);
        return tcs.Task;
    }

    // MARK: Persistence

    void Load()
    {
        var s = Json.ReadFile<SavedState>(Paths.StateFile);
        if (s == null)
        {
            // A damaged state file is kept aside instead of being overwritten.
            if (File.Exists(Paths.StateFile))
                try { File.Copy(Paths.StateFile, Paths.StateFile + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            return;
        }
        Chats = s.Chats;
        Projects = s.Projects;
        LocalModels = s.LocalModels.Where(m => Directory.Exists(m.Directory)).ToList();
        // Clean up anything a crash left mid-flight.
        foreach (var c in Chats)
            foreach (var m in c.Messages.Where(m => m.Status != null)) m.Status = null;
    }

    /// Saves within a second, coalescing bursts (streaming replies change state constantly).
    public void SaveSoon()
    {
        if (_saveScheduled) return;
        _saveScheduled = true;
        Main.Post(async _ =>
        {
            await Task.Delay(1000);
            _saveScheduled = false;
            Save();
        }, null);
    }

    public void Save()
    {
        try
        {
            var s = new SavedState { Chats = Chats, Projects = Projects, LocalModels = LocalModels };
            Json.WriteFile(Paths.StateFile, s);
        }
        catch { }
        Usage.Flush();
    }

    // MARK: Models

    public IEnumerable<ModelRef> AllModels =>
        LocalModels.Select(m => m.Ref).Concat(Providers.Cloud.SelectMany(p => RemoteModels.GetValueOrDefault(p) ?? []));

    public bool HasKey(ProviderId p) => p == ProviderId.Local || Platform.Secrets.Get(p.Key()) != null;

    public void SetKey(ProviderId p, string? key)
    {
        Platform.Secrets.Set(p.Key(), string.IsNullOrWhiteSpace(key) ? null : key.Trim());
        KeysVersion++;
        NotifyApp();
        _ = RefreshRemoteModels(p);
    }

    public bool ProviderEnabled(ProviderId p) => p == ProviderId.Custom ? Settings.CustomEnabled : HasKey(p);

    public async Task RefreshRemoteModels(ProviderId? only = null)
    {
        var providers = only is { } o ? [o] : Providers.Cloud;
        var jobs = providers.Select(async p =>
        {
            if (!ProviderEnabled(p)) return (p, (List<ModelRef>?)null, (string?)null);
            try { return (p, await ChatClient.ListModels(p), null); }
            catch (Exception e) { return (p, null, e is ApiException a ? a.Describe() : e.Message); }
        }).ToList();
        foreach (var (p, list, error) in await Task.WhenAll(jobs))
        {
            if (list != null) { RemoteModels[p] = list; RemoteErrors.Remove(p); }
            else if (error != null) RemoteErrors[p] = error;
            else { RemoteModels.Remove(p); RemoteErrors.Remove(p); }
        }
        NotifyApp();
    }

    public LocalModel? FindLocal(string id) => LocalModels.FirstOrDefault(m => m.Id == id);

    public Task LoadLocal(LocalModel m, CancellationToken ct = default) => Engine.EnsureLoaded(m, Settings.ContextSize, ct);

    public void DeleteLocalModel(string id)
    {
        if (FindLocal(id) is not { } m) return;
        if (Engine.LoadedModelId == m.Id) Engine.Stop();
        Platform.Trash(m.Directory);
        LocalModels.Remove(m);
        SaveSoon();
        NotifyApp();
    }

    /// Picks a sensible model for new chats.
    public ModelRef? DefaultModel
    {
        get
        {
            if (Settings.DefaultModel is { } d)
            {
                if (d.Provider == ProviderId.Local) { if (FindLocal(d.Id) != null) return d; }
                else if (ProviderEnabled(d.Provider)) return d;
            }
            return RemoteModels.GetValueOrDefault(ProviderId.Google)?.FirstOrDefault()
                ?? RemoteModels.GetValueOrDefault(ProviderId.Groq)?.FirstOrDefault()
                ?? LocalModels.FirstOrDefault()?.Ref
                ?? RemoteModels.GetValueOrDefault(ProviderId.Openrouter)?.FirstOrDefault()
                ?? RemoteModels.GetValueOrDefault(ProviderId.Custom)?.FirstOrDefault();
        }
    }

    // MARK: Chats & projects

    public Chat? FindChat(Guid id) => Chats.FirstOrDefault(c => c.Id == id);
    public Project? FindProject(Guid id) => Projects.FirstOrDefault(p => p.Id == id);
    public Project? ProjectFor(Chat chat) => chat.ProjectId is { } id ? FindProject(id) : null;

    public Guid NewChat(Guid? project = null, ModelRef? model = null)
    {
        // Reuse an untouched empty chat instead of piling up blank ones.
        if (Chats.FirstOrDefault(c => c.Messages.Count == 0 && c.ProjectId == project && !Generating.Contains(c.Id)) is { } empty)
        {
            if (model != null) empty.Model = model;
            Selection = "chat:" + empty.Id;
            NotifyApp();
            return empty.Id;
        }
        var c = new Chat { ProjectId = project, Model = model ?? DefaultModel };
        if (Chats.MaxBy(x => x.UpdatedAt) is { } last)
        {
            c.Capabilities = last.Capabilities.Clone();
            c.Think = last.Think;
        }
        Chats.Add(c);
        Selection = "chat:" + c.Id;
        SaveSoon();
        NotifyApp();
        return c.Id;
    }

    public void DeleteChat(Guid id)
    {
        Stop(id);
        Chats.RemoveAll(c => c.Id == id);
        if (Selection == "chat:" + id)
            Selection = Chats.MaxBy(c => c.UpdatedAt) is { } next ? "chat:" + next.Id : null;
        SaveSoon();
        NotifyApp();
    }

    public Guid DuplicateChat(Guid id)
    {
        if (FindChat(id) is not { } c) return id;
        var copy = Json.Deserialize<Chat>(Json.Serialize(c))!;
        copy.Id = Guid.NewGuid();
        copy.Title = c.Title + " (copy)";
        copy.Pinned = false;
        copy.UpdatedAt = DateTime.UtcNow;
        foreach (var m in copy.Messages) m.Id = Guid.NewGuid();
        Chats.Add(copy);
        Selection = "chat:" + copy.Id;
        SaveSoon();
        NotifyApp();
        return copy.Id;
    }

    public Guid NewProject(string name, string? folder = null)
    {
        var dir = folder ?? Path.Combine(Paths.ProjectsRoot, Util.Slug(name).IfEmpty("project"));
        if (folder == null && Directory.Exists(dir) && Projects.Any(p => p.Folder.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            dir += "-" + Guid.NewGuid().ToString("N")[..4];
        Directory.CreateDirectory(dir);
        var p = new Project { Name = name, Folder = dir };
        Projects.Add(p);
        Selection = "project:" + p.Id;
        SaveSoon();
        NotifyApp();
        return p.Id;
    }

    public void DeleteProject(Guid id)
    {
        Projects.RemoveAll(p => p.Id == id);
        foreach (var c in Chats.Where(c => c.ProjectId == id)) c.ProjectId = null;
        if (Selection == "project:" + id) Selection = null;
        SaveSoon();
        NotifyApp();
    }

    // MARK: Generation

    public void Send(Guid chatId, string text, List<Attachment> attachments)
    {
        if (FindChat(chatId) is not { } chat || Generating.Contains(chatId)) return;
        var trimmed = text.Trim();
        if (trimmed.Length == 0 && attachments.Count == 0) return;
        chat.Messages.Add(new ChatMessage(Role.User, trimmed) { Attachments = attachments });
        if (chat.Title is "New Chat" or "New chat")
        {
            var words = string.Join(" ", trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(7));
            chat.Title = words.Length == 0 ? attachments.FirstOrDefault()?.Filename ?? "New Chat" : words.Length > 60 ? words[..60] : words;
        }
        chat.UpdatedAt = DateTime.UtcNow;
        NotifyChat(chatId);
        NotifyApp();
        Run(chat);
    }

    public void Regenerate(Guid chatId)
    {
        if (FindChat(chatId) is not { } chat || Generating.Contains(chatId)) return;
        while (chat.Messages.Count > 0 && chat.Messages[^1].Role != Role.User) chat.Messages.RemoveAt(chat.Messages.Count - 1);
        if (chat.Messages.Count == 0) return;
        NotifyChat(chatId);
        Run(chat);
    }

    /// Replaces a user message (and everything after it) and asks again.
    public void EditAndResend(Guid chatId, Guid messageId, string text)
    {
        if (FindChat(chatId) is not { } chat || Generating.Contains(chatId)) return;
        var i = chat.Messages.FindIndex(m => m.Id == messageId);
        if (i < 0 || chat.Messages[i].Role != Role.User) return;
        var attachments = chat.Messages[i].Attachments;
        chat.Messages.RemoveRange(i, chat.Messages.Count - i);
        NotifyChat(chatId);
        Send(chatId, text, attachments);
    }

    void Run(Chat chat)
    {
        var cts = new CancellationTokenSource();
        _tasks[chat.Id] = cts;
        Generating.Add(chat.Id);
        NotifyApp();
        Main.Post(async _ =>
        {
            try { await new Agent(this, chat, cts.Token).Run(); }
            catch (Exception e)
            {
                chat.Messages.Add(new ChatMessage(Role.Assistant, "⚠️ " + e.Message) { IsError = true });
            }
            if (Platform.Browser is { } b && b.AgentActive) { b.AgentActive = false; b.NotifyChanged(); }
            if (_tasks.GetValueOrDefault(chat.Id) == cts) _tasks.Remove(chat.Id);
            Generating.Remove(chat.Id);
            chat.UpdatedAt = DateTime.UtcNow;
            NotifyChat(chat.Id);
            NotifyApp();
            if (!WindowFocused && Settings.NotifyWhenDone && !cts.IsCancellationRequested && Chats.Contains(chat))
            {
                var last = chat.Messages.LastOrDefault(m => m.Role == Role.Assistant);
                var body = last?.Content ?? "";
                Platform.Notify(chat.Title, body.Length > 140 ? body[..140] + "…" : body.IfEmpty("Reply ready."));
            }
        }, null);
    }

    public void Stop(Guid chatId)
    {
        if (_tasks.Remove(chatId, out var cts)) cts.Cancel();
        Generating.Remove(chatId);
        // Release the approval prompts that belong to the cancelled run.
        foreach (var a in Approvals.Where(a => a.ChatId == chatId).ToList())
        {
            Approvals.Remove(a);
            a.Completion.TrySetResult(false);
        }
        NotifyApp();
    }

    public void StopAll()
    {
        foreach (var id in _tasks.Keys.ToList()) Stop(id);
    }

    public async Task<bool> RequestApproval(string title, string detail, CancellationToken ct, Guid? chatId = null)
    {
        var req = new ApprovalRequest { Title = title, Detail = detail, ChatId = chatId ?? Generating.FirstOrDefault() };
        Approvals.Add(req);
        NotifyApp();
        Platform.Notify("Synapse needs your approval", title);
        await using var reg = ct.Register(() => req.Completion.TrySetResult(false));
        var ok = await req.Completion.Task;
        if (Approvals.Remove(req)) NotifyApp();
        return ok && !ct.IsCancellationRequested;
    }

    public void ResolveApproval(Guid id, bool ok)
    {
        if (Approvals.FirstOrDefault(a => a.Id == id) is not { } a) return;
        Approvals.Remove(a);
        a.Completion.TrySetResult(ok);
        NotifyApp();
    }

    // MARK: Attachments

    public Attachment? MakeAttachment(string path)
    {
        if (!File.Exists(path)) return null;
        var name = Path.GetFileName(path);
        var dest = Path.Combine(Paths.Attachments, $"{Guid.NewGuid().ToString("N")[..8]}-{name}");
        try { File.Copy(path, dest); } catch { return null; }
        if (Util.IsImage(path)) return new Attachment { Kind = AttachmentKind.Image, Filename = name, Path = dest };
        var text = TextExtract.Read(dest);
        return new Attachment { Kind = Util.Ext(path) == "pdf" ? AttachmentKind.Pdf : AttachmentKind.Text, Filename = name, Path = dest, ExtractedText = text.Truncated(120_000) };
    }

    /// Saves pasted or dropped data (from the UI) as an attachment.
    public Attachment? AttachData(string filename, byte[] data)
    {
        var name = Path.GetFileName(filename).IfEmpty("pasted.png");
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var dest = Path.Combine(Paths.Attachments, $"{Guid.NewGuid().ToString("N")[..8]}-{name}");
        File.WriteAllBytes(dest, data);
        if (Util.IsImage(dest)) return new Attachment { Kind = AttachmentKind.Image, Filename = filename.StartsWith("image.") ? "Pasted image" : name, Path = dest };
        return new Attachment { Kind = Util.Ext(dest) == "pdf" ? AttachmentKind.Pdf : AttachmentKind.Text, Filename = name, Path = dest, ExtractedText = TextExtract.Read(dest).Truncated(120_000) };
    }

    // MARK: Export

    public string ExportMarkdown(Guid chatId)
    {
        if (FindChat(chatId) is not { } c) return "";
        var sb = new StringBuilder($"# {c.Title}\n\n");
        foreach (var m in c.Messages)
        {
            switch (m.Role)
            {
                case Role.User:
                    sb.Append("**You:**\n\n").Append(m.Content).Append("\n\n");
                    foreach (var a in m.Attachments) sb.Append($"_Attached: {a.Filename}_\n\n");
                    break;
                case Role.Assistant when m.Content.Length > 0:
                    sb.Append($"**{m.ModelName ?? "Assistant"}:**\n\n").Append(m.Content).Append("\n\n");
                    break;
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    // MARK: Local OpenAI-compatible API (used by the `synapse` CLI and other apps)

    public void StartApiServer()
    {
        _apiServer?.Stop();
        _apiServer = null;
        ApiError = null;
        if (!Settings.ApiServerEnabled) { NotifyApp(); return; }
        var server = new HttpServer(Settings.ApiServerPort, HandleApi);
        try
        {
            server.Start();
            _apiServer = server;
        }
        catch (Exception e)
        {
            ApiError = $"Port {Settings.ApiServerPort} is busy: {e.Message}";
        }
        NotifyApp();
    }

    public void StopApiServer() { _apiServer?.Stop(); _apiServer = null; }

    /// Runs on a server thread.
    async Task HandleApi(HttpRequest req, HttpResponder res)
    {
        if (req.Method == "OPTIONS")
        {
            await res.Send(204, new() { ["Access-Control-Allow-Headers"] = "*", ["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS" }, []);
            return;
        }
        switch (req.Method, req.Path.TrimEnd('/'))
        {
            case ("GET", "/health") or ("GET", ""):
                await res.SendJson(new { status = "ok", app = "Synapse", platform = "windows" });
                break;
            case ("GET", "/v1/models"):
            {
                var models = await OnMain(() => AllModels.ToList());
                var data = models.Select(m => new { id = m.QualifiedId, @object = "model", owned_by = m.Provider.Key(), name = m.Name });
                await res.SendJson(new { @object = "list", data });
                break;
            }
            case ("POST", "/v1/chat/completions"):
                await ProxyChat(req, res);
                break;
            default:
                await res.SendJson(new { error = new { message = $"Not found: {req.Method} {req.Path}" } }, 404);
                break;
        }
    }

    async Task ProxyChat(HttpRequest req, HttpResponder res)
    {
        if (JsonNodeExt.ParseOrNull(req.BodyText) is not JsonObject body)
        {
            await res.SendJson(new { error = new { message = "Invalid JSON" } }, 400);
            return;
        }
        var requested = body.Str("model");
        var resolved = await OnMain(() =>
        {
            var all = AllModels.ToList();
            return all.FirstOrDefault(m => m.QualifiedId == requested)
                   ?? all.FirstOrDefault(m => m.Id == requested || m.Name == requested)
                   ?? all.FirstOrDefault(m => requested.Length > 0 && m.QualifiedId.EndsWith("/" + requested))
                   ?? DefaultModel;
        });
        if (resolved == null)
        {
            await res.SendJson(new { error = new { message = $"Unknown model {requested}. GET /v1/models lists available models." } }, 400);
            return;
        }
        try
        {
            if (resolved.Provider == ProviderId.Local)
            {
                var name = await OnMainAsync(async () =>
                {
                    var m = FindLocal(resolved.Id) ?? throw new ApiException(0, "Model not found");
                    await LoadLocal(m);
                    return Engine.RequestModelName(m);
                });
                body["model"] = name;
            }
            else body["model"] = resolved.Id;

            if (body["synapse_think"] is JsonValue tv && tv.TryGetValue<string>(out var think))
            {
                body.Remove("synapse_think");
                if (Enum.TryParse<ThinkLevel>(think == "normal" ? "standard" : think, true, out var level))
                    foreach (var (k, v) in Agent.ThinkingParams(resolved.Provider, resolved.Id, level)) body[k] = v?.DeepClone();
            }
            await OnMain(() => Usage.RecordRequest(resolved.Provider, resolved.Id));
            using var upstream = ChatClient.MakeRequest(resolved.Provider, "/chat/completions", body);
            using var resp = await ChatClient.Http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, res.Aborted.Token);
            if (RateLimitInfo.From(resp) is { } rl) await OnMain(() => Usage.RecordRateLimit(resolved.Provider, rl));
            await res.BeginStream((int)resp.StatusCode, resp.Content.Headers.ContentType?.ToString() ?? "application/json");
            await using var stream = await resp.Content.ReadAsStreamAsync(res.Aborted.Token);
            var buffer = new byte[8192];
            int n;
            while ((n = await stream.ReadAsync(buffer, res.Aborted.Token)) > 0)
                await res.Write(buffer[..n]);
            res.End();
        }
        catch (OperationCanceledException) { res.End(); }
        catch (Exception e)
        {
            if (!res.Closed) await res.SendJson(new { error = new { message = e is ApiException a ? a.Describe() : e.Message } }, 502);
        }
    }

    // MARK: Shutdown

    public void Shutdown()
    {
        StopAll();
        Engine.Stop();
        Sites.StopAll();
        StopApiServer();
        Save();
    }
}
