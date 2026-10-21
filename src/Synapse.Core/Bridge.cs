using System.Text.Json;
using System.Text.Json.Nodes;

namespace Synapse;

/// Connects the HTML interface to AppState. The UI sends {id, cmd, args}; the host answers
/// {type:"reply", id, ok, result|error} and pushes state: "app" snapshots, the open "chat", and
/// single "message" updates while a reply streams. Pushes are coalesced to ~30 per second.
public sealed class BridgeHost
{
    readonly AppState _app;
    readonly Action<string> _post;
    readonly string _fileBase;
    bool _appDirty, _scheduled, _ready;
    readonly HashSet<Guid> _chatDirty = [];
    readonly HashSet<(Guid Chat, Guid Msg)> _msgDirty = [];
    Guid? _sentChat;

    static readonly bool Debug = Environment.GetEnvironmentVariable("SYNAPSE_DEBUG") == "1";

    /// Commands only the window host can do (quit, dev tools…).
    public event Action<string, JsonObject>? HostCommand;

    public BridgeHost(AppState app, Action<string> post, string fileBase)
    {
        _app = app;
        _post = post;
        _fileBase = fileBase;
        app.Changed += OnChanged;
        if (app.Platform.Browser is { } b) b.Changed += () => app.OnMain(() => OnChanged(new Change(ChangeKind.App)));
    }

    Guid? ViewChat => _app.Selection is { } s && s.StartsWith("chat:") && Guid.TryParse(s[5..], out var g) ? g : null;

    void OnChanged(Change c)
    {
        switch (c.Kind)
        {
            case ChangeKind.App: _appDirty = true; break;
            case ChangeKind.Chat when c.ChatId is { } id: _chatDirty.Add(id); break;
            case ChangeKind.Message when c.ChatId is { } id && c.MessageId is { } m: _msgDirty.Add((id, m)); break;
        }
        Schedule();
    }

    void Schedule()
    {
        if (_scheduled || !_ready) return;
        _scheduled = true;
        _app.Main.Post(async _ =>
        {
            await Task.Delay(33);
            _scheduled = false;
            Flush();
        }, null);
    }

    void Flush()
    {
        if (_appDirty)
        {
            _appDirty = false;
            Push("app", Snapshot());
        }
        var view = ViewChat;
        if (view != _sentChat || (view is { } v && _chatDirty.Contains(v)))
        {
            _sentChat = view;
            _msgDirty.RemoveWhere(x => x.Chat == view);
            Push("chat", view is { } id && _app.FindChat(id) is { } chat ? ChatJson(chat) : null);
        }
        foreach (var (chatId, msgId) in _msgDirty)
        {
            if (chatId != view || _app.FindChat(chatId)?.Messages.FirstOrDefault(m => m.Id == msgId) is not { } msg) continue;
            Post(new JsonObject { ["type"] = "message", ["chatId"] = chatId.ToString(), ["data"] = MessageJson(msg) });
        }
        _chatDirty.Clear();
        _msgDirty.Clear();
    }

    void Push(string type, JsonNode? data) => Post(new JsonObject { ["type"] = type, ["data"] = data });

    void Post(JsonObject o)
    {
        try
        {
            var json = o.ToJsonString(Json.Options);
            if (Debug) Console.Error.WriteLine($"[bridge] → {o["type"]} {json.Length}b");
            _post(json);
        }
        catch (Exception e) { Console.Error.WriteLine($"[bridge] couldn't send {o["type"]}: {e.Message}"); }
    }

    static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Json.Options);

    // MARK: Snapshots

    public static JsonObject MessageJson(ChatMessage m)
    {
        var o = (JsonObject)ToNode(m)!;
        // Thinking is private: the UI only learns that there was some.
        o.Remove("reasoning");
        o["hasReasoning"] = m.Reasoning.Length > 0;
        if (o["attachments"] is JsonArray atts) foreach (var a in atts) (a as JsonObject)?.Remove("extractedText");
        return o;
    }

    public JsonObject ChatJson(Chat c)
    {
        var o = new JsonObject
        {
            ["id"] = c.Id.ToString(),
            ["title"] = c.Title,
            ["projectId"] = c.ProjectId?.ToString(),
            ["projectName"] = _app.ProjectFor(c)?.Name,
            ["model"] = ToNode(c.Model),
            ["capabilities"] = ToNode(c.Capabilities),
            ["think"] = ToNode(c.Think),
            ["autoApprove"] = c.AutoApprove,
            ["pinned"] = c.Pinned,
            ["generating"] = _app.Generating.Contains(c.Id),
        };
        var msgs = new JsonArray();
        foreach (var m in c.Messages) msgs.Add(MessageJson(m));
        o["messages"] = msgs;
        return o;
    }

    public JsonObject Snapshot()
    {
        var app = _app;
        var hw = app.Platform.Hardware;
        var engine = app.Engine;
        var browser = app.Platform.Browser;
        var installed = LocalEngine.Installed;
        var log = engine.Log;

        var usage = new JsonObject();
        foreach (var p in Enum.GetValues<ProviderId>())
        {
            var u = app.Usage.Usage(p);
            var today = app.Usage.Today(p);
            var history = new JsonArray();
            foreach (var (day, d) in u.Days.OrderBy(x => x.Key).TakeLast(14))
                history.Add(new JsonObject { ["day"] = day, ["requests"] = d.Requests, ["tokens"] = d.PromptTokens + d.CompletionTokens });
            usage[p.Key()] = new JsonObject
            {
                ["requestsToday"] = today.Requests,
                ["promptTokensToday"] = today.PromptTokens,
                ["completionTokensToday"] = today.CompletionTokens,
                ["lastMinute"] = app.Usage.RequestsLastMinute(p),
                ["rateLimit"] = ToNode(u.RateLimit),
                ["lastLimitError"] = u.LastLimitError,
                ["lastLimitErrorAt"] = u.LastLimitErrorAt,
                ["perModelToday"] = u.PerModelDay == UsageTracker.DayKey() ? ToNode(u.PerModelToday) : new JsonObject(),
                ["history"] = history,
                ["hasData"] = u.Days.Count > 0,
            };
        }

        var remote = new JsonObject();
        foreach (var (p, list) in app.RemoteModels) remote[p.Key()] = ToNode(list);
        var errors = new JsonObject();
        foreach (var (p, e) in app.RemoteErrors) errors[p.Key()] = e;

        return new JsonObject
        {
            ["version"] = typeof(AppState).Assembly.GetName().Version?.ToString(3),
            ["fileBase"] = _fileBase,
            ["platform"] = new JsonObject
            {
                ["os"] = app.Platform.OsName,
                ["shell"] = app.Platform.ShellName,
                ["isWindows"] = app.Platform.IsWindows,
                ["browser"] = browser != null,
                ["projectsRoot"] = Paths.ProjectsRoot,
                ["dataRoot"] = Paths.Root,
                ["sitesRoot"] = Paths.Sites,
            },
            ["selection"] = app.Selection,
            ["showBrowserPanel"] = app.ShowBrowserPanel,
            ["chats"] = new JsonArray(app.Chats.OrderByDescending(c => c.UpdatedAt).Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id.ToString(),
                ["title"] = c.Title,
                ["projectId"] = c.ProjectId?.ToString(),
                ["updatedAt"] = c.UpdatedAt,
                ["pinned"] = c.Pinned,
                ["generating"] = app.Generating.Contains(c.Id),
                ["empty"] = c.Messages.Count == 0,
                ["needsApproval"] = app.Approvals.Any(a => a.ChatId == c.Id),
            }).ToArray()),
            ["projects"] = ToNode(app.Projects),
            ["localModels"] = new JsonArray(app.LocalModels.Select(m =>
            {
                var o = (JsonObject)ToNode(m)!;
                o["vision"] = m.Vision;
                o["fit"] = ToNode(hw.FitFor(m.SizeBytes));
                o["ref"] = ToNode(m.Ref);
                return (JsonNode)o;
            }).ToArray()),
            ["remoteModels"] = remote,
            ["remoteErrors"] = errors,
            ["defaultModel"] = ToNode(app.DefaultModel),
            ["settingsDefaultModel"] = ToNode(app.Settings.DefaultModel),
            ["providers"] = new JsonArray(Providers.Cloud.Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Key(),
                ["name"] = p.DisplayName(),
                ["note"] = p.FreeTierNote(),
                ["steps"] = p.KeySteps(),
                ["keyPage"] = p.KeyPageUrl(),
                ["usagePage"] = p.UsagePageUrl(),
                ["hasKey"] = p != ProviderId.Custom ? app.HasKey(p) : app.Platform.Secrets.Get("custom") != null,
                ["enabled"] = app.ProviderEnabled(p),
                ["count"] = app.RemoteModels.GetValueOrDefault(p)?.Count ?? 0,
            }).ToArray()),
            ["hfToken"] = app.Platform.Secrets.Get("huggingface") != null,
            ["engine"] = new JsonObject
            {
                ["state"] = ToNode(engine.State),
                ["text"] = engine.StatusText,
                ["loadedModelId"] = engine.LoadedModelId,
                ["available"] = engine.IsAvailable,
                ["path"] = engine.ServerPath,
                ["installed"] = installed == null ? null : new JsonObject { ["tag"] = installed.Tag, ["backend"] = installed.Backend },
                ["installing"] = engine.Installing,
                ["installProgress"] = engine.InstallProgress,
                ["installLog"] = engine.InstallLog,
                ["log"] = log.Length > 6000 ? log[^6000..] : log,
                ["autoBackend"] = LocalEngine.AutoBackend(hw),
                ["port"] = LocalEngine.Port,
            },
            ["downloads"] = new JsonArray(app.Downloads.Items.Values.Where(d => !d.Done).Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id,
                ["name"] = d.DisplayName,
                ["label"] = d.Variant.Label,
                ["repo"] = d.Variant.Repo,
                ["received"] = d.Received,
                ["size"] = d.Variant.SizeBytes,
                ["fraction"] = d.Fraction,
                ["speed"] = d.Speed,
                ["file"] = d.CurrentFile,
                ["error"] = d.Error,
            }).ToArray()),
            ["approvals"] = new JsonArray(app.Approvals.Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id.ToString(),
                ["title"] = a.Title,
                ["detail"] = a.Detail,
                ["chatId"] = a.ChatId?.ToString(),
            }).ToArray()),
            ["browser"] = browser == null ? null : new JsonObject
            {
                ["url"] = browser.Url,
                ["title"] = browser.Title,
                ["agentActive"] = browser.AgentActive,
                ["lastAction"] = browser.LastAgentAction,
            },
            ["sites"] = new JsonArray(app.Sites.Sites.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["folder"] = s.Folder,
                ["url"] = s.Url,
            }).ToArray()),
            ["usage"] = usage,
            ["hardware"] = new JsonObject
            {
                ["cpu"] = hw.Cpu,
                ["memoryGB"] = Math.Round(hw.MemoryGB, 1),
                ["cores"] = hw.Cores,
                ["freeDisk"] = hw.FreeDiskBytes,
                ["arch"] = hw.Arch,
                ["gpus"] = ToNode(hw.Gpus.Select(g => new { g.Name, VramGB = Math.Round(g.VramGB, 1), g.Vendor })),
                ["bestGpu"] = hw.BestGpu?.Name,
                ["advice"] = hw.Advice,
            },
            ["settings"] = ToNode(app.Settings),
            ["api"] = new JsonObject
            {
                ["port"] = app.ApiPort,
                ["error"] = app.ApiError,
                ["url"] = app.ApiPort is { } port ? $"http://127.0.0.1:{port}/v1" : null,
            },
        };
    }

    // MARK: Commands

    /// Handles one message from the UI. Call on the main thread.
    public async void Receive(string json)
    {
        if (JsonNodeExt.ParseOrNull(json) is not JsonObject msg) return;
        var id = msg["id"]?.DeepClone();
        var cmd = msg.Str("cmd");
        var args = msg["args"] as JsonObject ?? [];
        if (Debug) Console.Error.WriteLine($"[bridge] ← {cmd} #{id}");
        try
        {
            var result = await Handle(cmd, args);
            if (id != null) Post(new JsonObject { ["type"] = "reply", ["id"] = id, ["ok"] = true, ["result"] = result });
        }
        catch (Exception e)
        {
            if (id != null) Post(new JsonObject { ["type"] = "reply", ["id"] = id, ["ok"] = false, ["error"] = e is ApiException a ? a.Describe() : e.Message });
        }
    }

    /// Lets a host add attachments it received outside the JSON channel (WebView2 drag and drop gives real file paths).
    public JsonNode? AttachPaths(IEnumerable<string> paths) =>
        ToNode(paths.Select(p => _app.MakeAttachment(p)).Where(a => a != null).ToList());

    Chat ChatArg(JsonObject a) => a.Guid("chatId") is { } id && _app.FindChat(id) is { } c ? c : throw new ApiException(0, "Chat not found");
    Project ProjectArg(JsonObject a) => a.Guid("id") is { } id && _app.FindProject(id) is { } p ? p : throw new ApiException(0, "Project not found");

    static T? Arg<T>(JsonObject a, string key) => a[key] is { } n ? n.Deserialize<T>(Json.Options) : default;

    async Task<JsonNode?> Handle(string cmd, JsonObject a)
    {
        var app = _app;
        switch (cmd)
        {
            case "ready":
                _ready = true;
                _sentChat = ViewChat;
                return new JsonObject
                {
                    ["app"] = Snapshot(),
                    ["chat"] = ViewChat is { } vc && app.FindChat(vc) is { } chat ? ChatJson(chat) : null,
                };

            case "select":
                app.Selection = a.StrOrNull("selection");
                app.NotifyApp();
                return null;

            // Chats
            case "newChat":
            {
                var id = app.NewChat(a.Guid("projectId"), Arg<ModelRef>(a, "model"));
                return id.ToString();
            }
            case "deleteChat": app.DeleteChat(ChatArg(a).Id); return null;
            case "renameChat":
            {
                var c = ChatArg(a);
                c.Title = a.Str("title").Trim().IfEmpty(c.Title);
                app.NotifyChat(c.Id); app.NotifyApp();
                return null;
            }
            case "pinChat":
            {
                var c = ChatArg(a);
                c.Pinned = a.Bool("pinned");
                app.NotifyChat(c.Id); app.NotifyApp();
                return null;
            }
            case "duplicateChat": return app.DuplicateChat(ChatArg(a).Id).ToString();
            case "moveChat":
            {
                var c = ChatArg(a);
                c.ProjectId = a.Guid("projectId");
                app.NotifyChat(c.Id); app.NotifyApp();
                return null;
            }
            case "send":
            {
                var c = ChatArg(a);
                var atts = Arg<List<Attachment>>(a, "attachments") ?? [];
                // Text extracted when the file was attached stays on the host side.
                foreach (var att in atts.Where(x => x.Kind != AttachmentKind.Image && x.ExtractedText == null))
                    att.ExtractedText = TextExtract.Read(att.Path).Truncated(120_000);
                app.Send(c.Id, a.Str("text"), atts);
                return null;
            }
            case "stop": app.Stop(ChatArg(a).Id); return null;
            case "regenerate": app.Regenerate(ChatArg(a).Id); return null;
            case "editResend":
                app.EditAndResend(ChatArg(a).Id, a.Guid("messageId") ?? Guid.Empty, a.Str("text"));
                return null;
            case "deleteMessage":
            {
                var c = ChatArg(a);
                if (app.Generating.Contains(c.Id)) return null;
                c.Messages.RemoveAll(m => m.Id == a.Guid("messageId"));
                app.NotifyChat(c.Id);
                return null;
            }
            case "setChat":
            {
                var c = ChatArg(a);
                if (a.ContainsKey("model")) c.Model = Arg<ModelRef>(a, "model");
                if (a.ContainsKey("think")) c.Think = Arg<ThinkLevel>(a, "think");
                if (a.ContainsKey("capabilities")) c.Capabilities = Arg<Capabilities>(a, "capabilities") ?? c.Capabilities;
                if (a.ContainsKey("autoApprove")) c.AutoApprove = a.Bool("autoApprove");
                app.NotifyChat(c.Id);
                return null;
            }
            case "exportChat":
            {
                var c = ChatArg(a);
                var md = app.ExportMarkdown(c.Id);
                var path = await app.Platform.PickSaveFile(Util.Slug(c.Title) + ".md", "Markdown|*.md|Text|*.txt");
                if (path == null) return md;
                await File.WriteAllTextAsync(path, md);
                app.Platform.RevealInFileManager(path);
                return null;
            }

            // Projects
            case "newProject":
                return app.NewProject(a.Str("name").Trim().IfEmpty("Untitled Project"), a.StrOrNull("folder").IfEmpty(null!)).ToString();
            case "deleteProject": app.DeleteProject(ProjectArg(a).Id); return null;
            case "updateProject":
            {
                var p = ProjectArg(a);
                if (a.ContainsKey("name")) p.Name = a.Str("name");
                if (a.ContainsKey("instructions")) p.Instructions = a.Str("instructions");
                app.SaveSoon();
                app.NotifyApp();
                return null;
            }
            case "projectFiles":
            {
                var p = ProjectArg(a);
                return await Task.Run(() => ToNode(ProjectFiles.List(p)));
            }
            case "projectAddFiles":
            {
                var p = ProjectArg(a);
                var paths = Arg<string[]>(a, "paths") ?? await app.Platform.PickFiles(true, false, "Add files to " + p.Name) ?? [];
                foreach (var src in paths)
                {
                    try
                    {
                        if (Directory.Exists(src)) CopyDirectory(src, Path.Combine(p.Folder, Path.GetFileName(src)));
                        else File.Copy(src, UniquePath(Path.Combine(p.Folder, Path.GetFileName(src))));
                    }
                    catch { }
                }
                return paths.Length;
            }
            case "projectAddData":
            {
                var p = ProjectArg(a);
                var name = Path.GetFileName(a.Str("name")).IfEmpty("file");
                await File.WriteAllBytesAsync(UniquePath(Path.Combine(p.Folder, name)), Convert.FromBase64String(a.Str("base64")));
                return null;
            }
            case "projectDeleteFile":
            {
                var p = ProjectArg(a);
                var full = Path.GetFullPath(Path.Combine(p.Folder, a.Str("path")));
                if (Util.IsInside(full, p.Folder) && !full.Equals(Path.GetFullPath(p.Folder), Util.PathComparison)) app.Platform.Trash(full);
                return null;
            }
            case "pickFolder":
                return (await app.Platform.PickFiles(false, true, a.StrOrNull("title")))?.FirstOrDefault();

            // Attachments & files
            case "attachPaths": return AttachPaths(Arg<string[]>(a, "paths") ?? []);
            case "attachPick":
                return AttachPaths(await app.Platform.PickFiles(true, false, "Attach files") ?? []);
            case "attachData":
                return ToNode(app.AttachData(a.Str("name"), Convert.FromBase64String(a.Str("base64"))));
            case "openPath":
            {
                var (ok, message) = await app.Platform.Open(a.Str("path"));
                if (!ok) throw new ApiException(0, message);
                return null;
            }
            case "revealPath": app.Platform.RevealInFileManager(a.Str("path")); return null;
            case "openExternal":
            {
                var url = a.Str("url");
                if (Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "mailto")
                    app.Platform.OpenExternal(u.AbsoluteUri);
                return null;
            }
            case "preview": return await Task.Run(() => Preview(a.Str("path")));
            case "copyText": await app.Platform.SetClipboardText(a.Str("text")); return null;

            // Accounts
            case "setKey":
            {
                var p = Providers.Parse(a.Str("provider")) ?? throw new ApiException(0, "Unknown provider");
                app.SetKey(p, a.StrOrNull("key"));
                return null;
            }
            case "testKey":
            {
                var p = Providers.Parse(a.Str("provider")) ?? throw new ApiException(0, "Unknown provider");
                try
                {
                    var models = await ChatClient.ListModels(p);
                    await app.RefreshRemoteModels(p);
                    return new JsonObject { ["ok"] = true, ["message"] = $"Key works · {models.Count} chat models available" };
                }
                catch (Exception e)
                {
                    return new JsonObject { ["ok"] = false, ["message"] = e is ApiException x ? x.Describe() : e.Message };
                }
            }
            case "setCustom":
            {
                if (a.ContainsKey("url")) app.Settings.CustomBaseUrl = a.Str("url").Trim();
                if (a.ContainsKey("enabled")) app.Settings.CustomEnabled = a.Bool("enabled");
                app.Settings.Save();
                if (a.StrOrNull("key") is { Length: > 0 } key) app.Platform.Secrets.Set("custom", key);
                if (a.Bool("clearKey")) app.Platform.Secrets.Set("custom", null);
                await app.RefreshRemoteModels(ProviderId.Custom);
                return null;
            }
            case "setHfToken":
                app.Platform.Secrets.Set("huggingface", a.StrOrNull("token"));
                app.NotifyApp();
                return null;
            case "refreshModels":
                await app.RefreshRemoteModels(Providers.Parse(a.StrOrNull("provider")));
                return null;

            // Local models
            case "catalog":
                return ToNode(Catalog.Entries);
            case "variants":
            {
                var repo = a.Str("repo");
                var files = await HuggingFace.Files(repo);
                var variants = HuggingFace.Variants(repo, files);
                var hw = app.Platform.Hardware;
                var rec = HuggingFace.RecommendedVariant(variants, hw);
                return new JsonObject
                {
                    ["variants"] = new JsonArray(variants.Select(v => (JsonNode)VariantJson(v, hw)).ToArray()),
                    ["recommended"] = rec == null ? null : VariantJson(rec, hw),
                };
            }
            case "hfSearch":
                return ToNode(await HuggingFace.Search(a.Str("query")));
            case "download":
            {
                var v = Arg<ModelVariant>(a, "variant") ?? throw new ApiException(0, "No variant");
                if (Util.IsInside(DownloadManager.FolderFor(v), Paths.Models) is false) throw new ApiException(0, "Bad model name");
                if (v.SizeBytes > 0 && v.SizeBytes * 1.02 > app.Platform.Hardware.FreeDiskBytes && app.Platform.Hardware.FreeDiskBytes > 0)
                    throw new ApiException(0, $"Not enough disk space: needs {Util.FormatBytes(v.SizeBytes)}.");
                app.Downloads.Start(v, a.Str("name").IfEmpty(v.Repo.Split('/').Last()));
                return null;
            }
            case "cancelDownload": app.Downloads.Cancel(a.Str("id")); return null;
            case "deleteModel": app.DeleteLocalModel(a.Str("id")); return null;
            case "loadModel":
            {
                var m = app.FindLocal(a.Str("id")) ?? throw new ApiException(0, "Model not found");
                await app.LoadLocal(m);
                return null;
            }
            case "unloadModel": app.Engine.Stop(); return null;
            case "setDefaultModel":
                app.Settings.DefaultModel = Arg<ModelRef>(a, "model");
                app.Settings.Save();
                app.NotifyApp();
                return null;
            case "engineInstall":
                _ = app.Engine.Install(a.Str("backend").IfEmpty(app.Settings.LlamaBackend));
                return null;
            case "engineVersion": return await app.Engine.Version();

            // Settings
            case "setSettings":
            {
                var before = app.Settings;
                var merged = (JsonObject)ToNode(before)!;
                foreach (var (k, v) in a) merged[k] = v?.DeepClone();
                var updated = merged.Deserialize<AppSettings>(Json.Options) ?? before;
                AppSettings.Shared = updated;
                updated.Save();
                if (before.ContextSize != updated.ContextSize || before.GpuLayers != updated.GpuLayers || before.LlamaServerPath != updated.LlamaServerPath)
                    app.Engine.Stop();
                if (before.ApiServerEnabled != updated.ApiServerEnabled || before.ApiServerPort != updated.ApiServerPort)
                    app.StartApiServer();
                if (before.CustomBaseUrl != updated.CustomBaseUrl || before.CustomEnabled != updated.CustomEnabled)
                    _ = app.RefreshRemoteModels(ProviderId.Custom);
                HostCommand?.Invoke("settingsChanged", a);
                app.NotifyApp();
                return null;
            }

            // Websites
            case "sitesRefresh": app.Sites.Refresh(); return null;
            case "siteStart": app.Sites.Start(a.Str("folder")); return null;
            case "siteStop": app.Sites.Stop(a.Str("folder")); return null;
            case "siteDelete": app.Sites.Delete(a.Str("folder"), app.Platform); return null;
            case "newSiteChat":
            {
                var id = app.NewChat();
                if (app.FindChat(id) is { } c) { c.Capabilities.Websites = true; app.NotifyChat(id); }
                return id.ToString();
            }

            // Approvals, browser, misc
            case "approve":
                app.ResolveApproval(a.Guid("id") ?? Guid.Empty, a.Bool("ok"));
                return null;
            case "toggleBrowser":
                app.ShowBrowserPanel = a.ContainsKey("show") ? a.Bool("show") : !app.ShowBrowserPanel;
                app.NotifyApp();
                return null;
            case "browserNavigate":
                app.Platform.Browser?.Load(a.Str("url"));
                if (app.Platform.Browser != null && !app.ShowBrowserPanel) { app.ShowBrowserPanel = true; app.NotifyApp(); }
                return null;
            case "installCli": return app.Platform.InstallCli();
            case "usageReset":
                if (Providers.Parse(a.Str("provider")) is { } up) app.Usage.Reset(up);
                return null;
            case "log":
                Console.Error.WriteLine("[ui] " + a.Str("message"));
                return null;
            default:
                HostCommand?.Invoke(cmd, a);
                return null;
        }
    }

    static JsonObject VariantJson(ModelVariant v, HardwareInfo hw)
    {
        var o = (JsonObject)ToNode(v)!;
        o["id"] = v.Id;
        o["fit"] = ToNode(hw.FitFor(v.SizeBytes));
        return o;
    }

    static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(src)) File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true);
        foreach (var d in Directory.EnumerateDirectories(src)) CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    /// What the preview window shows for a file (the Windows stand-in for Quick Look).
    static JsonObject Preview(string path)
    {
        if (!File.Exists(path)) return new JsonObject { ["kind"] = "missing" };
        var ext = Util.Ext(path);
        var size = new FileInfo(path).Length;
        var o = new JsonObject { ["name"] = Path.GetFileName(path), ["size"] = size, ["ext"] = ext, ["path"] = path };
        if (Util.IsImage(path)) { o["kind"] = "image"; return o; }
        if (ext == "pdf") { o["kind"] = "pdf"; return o; }
        if (ext is "html" or "htm" or "svg") o["html"] = true;
        if (ext is "mp4" or "webm" or "mp3" or "wav" or "ogg") { o["kind"] = "media"; return o; }
        if (ext is "docx" or "pptx" or "xlsx" or "odt" or "rtf" || size < 2_000_000)
        {
            var text = TextExtract.Read(path);
            if (!text.StartsWith("Binary file"))
            {
                o["kind"] = ext is "md" or "markdown" ? "markdown" : "text";
                o["text"] = text.Truncated(300_000);
                return o;
            }
        }
        o["kind"] = "other";
        return o;
    }
}
