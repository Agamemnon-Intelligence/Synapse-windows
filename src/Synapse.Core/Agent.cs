using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Synapse;

/// Runs one turn of a chat: streams the reply, executes tool calls, and loops until done.
public sealed class Agent(AppState app, Chat chat, CancellationToken ct)
{
    public const int MaxSteps = 30;
    public const string GoogleFallback = "gemini-flash-lite-latest";
    /// After Google reports a model as busy, skip it for a while instead of waiting on it every turn.
    static readonly Dictionary<string, DateTime> GoogleBusy = [];

    bool Alive => !ct.IsCancellationRequested && app.Chats.Contains(chat);

    public async Task Run()
    {
        var model = chat.Model ?? app.DefaultModel;
        if (model == null)
        {
            Append(new ChatMessage(Role.Assistant, "Pick a model first: add an API key (Accounts, top right) or download a local model in Models.") { IsError = true });
            return;
        }
        chat.Model ??= model;

        // Local models need the engine running first.
        var requestModel = model.Id;
        if (model.Provider == ProviderId.Local)
        {
            var local = app.FindLocal(model.Id);
            if (local == null)
            {
                Append(new ChatMessage(Role.Assistant, "That local model was deleted. Pick another model.") { IsError = true });
                return;
            }
            var placeholder = Append(new ChatMessage(Role.Assistant) { ModelName = model.Name, Status = $"Loading {local.DisplayName}…" });
            try { await app.LoadLocal(local, ct); }
            catch (OperationCanceledException) { Remove(placeholder); return; }
            catch (Exception e)
            {
                Update(placeholder, m => { m.Status = null; m.Content = e is ApiException a ? a.Message : e.Message; m.IsError = true; });
                return;
            }
            Remove(placeholder);
            requestModel = app.Engine.RequestModelName(local);
        }

        var ctx = new ToolContext(app, chat, chat.Capabilities.Clone(), app.ProjectFor(chat), chat.AutoApprove, ct);
        var tools = Tools.Definitions(ctx);

        for (var step = 0; step < MaxSteps; step++)
        {
            if (!Alive) return;
            var messages = BuildMessages(model, ctx.Project, tools.Count > 0);
            var msg = Append(new ChatMessage(Role.Assistant) { ModelName = model.Name });
            List<ToolCall> calls;
            try
            {
                calls = await StreamReply(msg, model, requestModel, messages, tools, chat.Think);
            }
            catch (OperationCanceledException)
            {
                if (msg.Content.Length == 0 && msg.ToolCalls.Count == 0) Remove(msg);
                return;
            }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested) return;
                Update(msg, m => { m.Content += (m.Content.Length == 0 ? "" : "\n\n") + FriendlyError(e, model); m.IsError = true; });
                return;
            }
            if (calls.Count == 0)
            {
                if (chat.Think == ThinkLevel.Max && Alive) await Refine(msg, model, requestModel, messages);
                return;
            }
            Update(msg, m => m.ToolCalls = calls);
            foreach (var call in calls)
            {
                if (!Alive) return;
                ToolResult result;
                try { result = await Tools.Run(call, ctx); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { result = new ToolResult("Tool failed: " + e.Message); }
                Append(new ChatMessage(Role.Tool, result.Text)
                {
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Images = result.Images,
                    PresentedFiles = result.Presented,
                    Links = result.Links,
                });
            }
        }
        Append(new ChatMessage(Role.Assistant, $"Stopped after {MaxSteps} tool steps. Say “continue” to keep going.") { IsError = true });
    }

    // MARK: Streaming

    async Task<List<ToolCall>> StreamReply(ChatMessage msg, ModelRef model, string requestModel, JsonArray messages, JsonArray tools, ThinkLevel think)
    {
        var body = new JsonObject { ["model"] = requestModel, ["messages"] = messages.DeepClone() };
        if (tools.Count > 0) body["tools"] = tools.DeepClone();
        var thinkParams = ThinkingParams(model.Provider, model.Id, think);
        var attempt = Merge(body, thinkParams);
        if (model.Provider == ProviderId.Google && GoogleBusy.TryGetValue(model.Id, out var until) && until > DateTime.UtcNow)
        {
            attempt["model"] = GoogleFallback;
            Update(msg, m => m.ModelName = $"{GoogleFallback} ({model.Name} is busy)");
        }

        for (var tryNumber = 0; tryNumber < 4; tryNumber++)
        {
            try
            {
                return await StreamOnce(msg, model.Provider, model.Id, attempt);
            }
            catch (ApiException e) when (model.Provider == ProviderId.Google && (e.IsTimeout || e.Status == 503)
                                         && attempt.Str("model") != GoogleFallback && msg.Content.Length == 0)
            {
                // Google is overloaded: answer with the lighter model instead of failing.
                attempt["model"] = GoogleFallback;
                GoogleBusy[model.Id] = DateTime.UtcNow.AddMinutes(10);
                Update(msg, m => m.ModelName = $"{GoogleFallback} ({model.Name} {(e.IsTimeout ? "timed out" : "is busy")})");
            }
            catch (ApiException e) when (e.Status is 400 or 422)
            {
                // Some models reject reasoning or tool parameters: retry with fewer extras.
                if (msg.Content.Length > 0 || tryNumber == 3) throw;
                if (tryNumber == 0 && thinkParams.Count > 0) { attempt = (JsonObject)body.DeepClone(); continue; }
                if (attempt["tools"] != null && e.Message.Contains("tool", StringComparison.OrdinalIgnoreCase))
                {
                    attempt.Remove("tools");
                    continue;
                }
                throw;
            }
        }
        return [];
    }

    async Task<List<ToolCall>> StreamOnce(ChatMessage msg, ProviderId provider, string modelId, JsonObject body)
    {
        app.Usage.RecordRequest(provider, modelId);
        var calls = new List<ToolCall>();
        var pendingContent = new StringBuilder();
        var pendingReasoning = new StringBuilder();
        var lastFlush = DateTime.UtcNow;
        DateTime? thinkStart = null;
        void EndThinking()
        {
            if (thinkStart is not { } start) return;
            var elapsed = (DateTime.UtcNow - start).TotalSeconds;
            Update(msg, m => m.ThinkingSeconds = (m.ThinkingSeconds ?? 0) + elapsed);
            thinkStart = null;
        }
        void Flush()
        {
            if (pendingContent.Length == 0 && pendingReasoning.Length == 0) return;
            var c = pendingContent.ToString();
            var r = pendingReasoning.ToString();
            Update(msg, m => { m.Content += c; m.Reasoning += r; });
            pendingContent.Clear();
            pendingReasoning.Clear();
            lastFlush = DateTime.UtcNow;
        }
        try
        {
            await foreach (var ev in ChatClient.Stream(provider, body, ct))
            {
                ct.ThrowIfCancellationRequested();
                switch (ev)
                {
                    case StreamEvent.Content c:
                        EndThinking();
                        pendingContent.Append(c.Text);
                        if (msg.Content.Length == 0) Flush();   // show the first words right away
                        break;
                    case StreamEvent.Reasoning r:
                        pendingReasoning.Append(r.Text);
                        if (thinkStart == null) { thinkStart = DateTime.UtcNow; Flush(); }
                        break;
                    case StreamEvent.ToolCalls t: calls = t.Calls; break;
                    case StreamEvent.Usage u:
                        app.Usage.RecordTokens(provider, u.Tokens);
                        Update(msg, m => { m.PromptTokens = u.Tokens.Prompt; m.CompletionTokens = (m.CompletionTokens ?? 0) + u.Tokens.Completion; });
                        break;
                    case StreamEvent.RateLimit rl: app.Usage.RecordRateLimit(provider, rl.Info); break;
                }
                if ((DateTime.UtcNow - lastFlush).TotalMilliseconds > 50) Flush();
            }
        }
        catch (Exception e)
        {
            Flush();
            EndThinking();
            if (e is ApiException { Status: 429 } a) app.Usage.RecordLimitError(provider, a.Message);
            throw;
        }
        Flush();
        EndThinking();
        // Strip leading whitespace some models emit after thinking.
        Update(msg, m => m.Content = m.Content.Trim());
        return calls;
    }

    /// "Think harder": a second pass that critiques and rewrites the draft.
    async Task Refine(ChatMessage msg, ModelRef model, string requestModel, JsonArray baseMessages)
    {
        var draft = msg.Content;
        if (draft.Length == 0) return;
        var messages = (JsonArray)baseMessages.DeepClone();
        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = draft });
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "Review your previous answer like a strict expert. Check every fact, calculation, step and piece of code for errors, " +
                          "gaps and anything you misread in my request. Then write the corrected, complete final answer. " +
                          "Reply with ONLY the final answer, no commentary about the review.",
        });
        Update(msg, m =>
        {
            m.Reasoning += (m.Reasoning.Length == 0 ? "" : "\n\n") + "── First draft ──\n" + draft + "\n\n── Reviewing and refining ──\n";
            m.Content = "";
            m.Status = "Double-checking";
        });
        var body = Merge(new JsonObject { ["model"] = requestModel, ["messages"] = messages }, ThinkingParams(model.Provider, model.Id, ThinkLevel.High));
        try { await StreamOnce(msg, model.Provider, model.Id, body); }
        catch
        {
            if (msg.Content.Length == 0) Update(msg, m => m.Content = draft);
        }
        Update(msg, m => m.Status = null);
    }

    static JsonObject Merge(JsonObject a, JsonObject b)
    {
        var o = (JsonObject)a.DeepClone();
        foreach (var (k, v) in b) o[k] = v?.DeepClone();
        return o;
    }

    /// Provider-specific knobs that make a model reason more (or less).
    public static JsonObject ThinkingParams(ProviderId provider, string model, ThinkLevel level)
    {
        var m = model.ToLowerInvariant();
        switch (provider)
        {
            case ProviderId.Google:
            {
                var cfg = new JsonObject { ["include_thoughts"] = level != ThinkLevel.Off };
                if (m.Contains("2.5"))
                    cfg["thinking_budget"] = level == ThinkLevel.Off ? (m.Contains("pro") ? 128 : 0) : level == ThinkLevel.Standard ? -1 : 24_576;
                else if (level != ThinkLevel.Standard)
                    cfg["thinking_level"] = level == ThinkLevel.Off ? "low" : "high";
                return new JsonObject { ["extra_body"] = new JsonObject { ["google"] = new JsonObject { ["thinking_config"] = cfg } } };
            }
            case ProviderId.Groq:
                if (m.Contains("gpt-oss"))
                    return new JsonObject
                    {
                        ["reasoning_effort"] = level == ThinkLevel.Off ? "low" : level == ThinkLevel.Standard ? "medium" : "high",
                        ["include_reasoning"] = true,
                    };
                if (m.Contains("qwen3") || m.Contains("qwen-3"))
                    return new JsonObject { ["reasoning_effort"] = level == ThinkLevel.Off ? "none" : "default", ["reasoning_format"] = "parsed" };
                return [];
            case ProviderId.Openrouter:
                if (level == ThinkLevel.Standard) return [];
                return new JsonObject { ["reasoning"] = new JsonObject { ["effort"] = level == ThinkLevel.Off ? "low" : "high" } };
            case ProviderId.Local:
                return new JsonObject
                {
                    ["chat_template_kwargs"] = new JsonObject
                    {
                        ["enable_thinking"] = level != ThinkLevel.Off,
                        ["reasoning_effort"] = level == ThinkLevel.Off ? "low" : level == ThinkLevel.Standard ? "medium" : "high",
                    },
                };
            default:
                if (level == ThinkLevel.Standard) return [];
                return new JsonObject { ["reasoning_effort"] = level == ThinkLevel.Off ? "low" : "high" };
        }
    }

    // MARK: Prompt building

    string SystemPrompt(Project? project, bool tools)
    {
        var now = DateTime.Now;
        var hw = app.Platform.Hardware;
        var s = new StringBuilder();
        s.Append($"You are a helpful AI assistant running inside Synapse, a Windows app. Today is {now.ToString("dddd, d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)} ({TimeZoneInfo.Local.DisplayName}). ");
        s.Append($"The user is on {app.Platform.OsName} ({hw.Cpu}{(hw.BestGpu is { } g ? ", " + g.Name : "")}). Use Markdown. Use fenced code blocks with a language tag for code.");
        var caps = chat.Capabilities;
        if (tools)
        {
            var lines = new List<string>();
            if (caps.WebSearch) lines.Add("- Search the web and read pages. Do this for anything recent or uncertain, and cite source URLs.");
            if (caps.ImageGeneration) lines.Add("- Generate images with generate_image when the user asks for a picture.");
            if (caps.ComputerAccess)
                lines.Add($"- Use the user's PC: {app.Platform.ShellName} commands (including COM automation of Office and other apps), files, opening apps and URLs, the clipboard and screenshots. " +
                          "The user approves risky actions. Prefer safe, reversible commands; never delete user data unless explicitly asked. Windows paths use backslashes.");
            if (caps.Browser) lines.Add("- Drive the built-in browser: browser_navigate, then browser_click / browser_type using element numbers from the latest page read. Never enter passwords or payment details; ask the user to do that.");
            if (caps.Websites) lines.Add("- Build websites with create_website. Write complete, polished, responsive HTML/CSS/JS; the site is hosted on localhost and opened for the user.");
            if (project != null || caps.ComputerAccess) lines.Add("- Read, search, write and edit files. Use present_file to show finished documents to the user.");
            if (lines.Count > 0) s.Append("\n\nYou can:\n").Append(string.Join("\n", lines)).Append("\nUse tools when they help; don't narrate tool calls at length.");
        }
        switch (chat.Think)
        {
            case ThinkLevel.Off: s.Append("\n\nAnswer directly and concisely."); break;
            case ThinkLevel.High:
            case ThinkLevel.Max:
                s.Append("\n\nThis task deserves careful thought. Before answering, reason step by step: restate the goal, consider several approaches, work through details, check edge cases, and verify your result. Prefer correctness over speed.");
                break;
        }
        if (project != null)
        {
            s.Append($"\n\n# Project: {project.Name}\nProject folder: {project.Folder}");
            if (project.Instructions.Length > 0) s.Append($"\n\nProject instructions:\n{project.Instructions}");
            var files = ProjectFiles.List(project).Take(200).ToList();
            if (files.Count > 0)
            {
                s.Append("\n\nFiles in the project (relative paths):\n").Append(string.Join("\n", files.Select(f => "- " + f)));
                s.Append("\n\nUse read_file to open them.");
            }
        }
        if (app.Settings.SystemPrompt.Length > 0) s.Append("\n\n").Append(app.Settings.SystemPrompt);
        return s.ToString();
    }

    JsonArray BuildMessages(ModelRef model, Project? project, bool tools)
    {
        var output = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(project, tools) } };
        var vision = model.Vision || model.Provider is ProviderId.Google or ProviderId.Custom;
        var pendingImages = new List<string>();

        JsonObject? ImagePart(string path)
        {
            byte[] data;
            try { data = File.ReadAllBytes(path); } catch { return null; }
            var mime = Util.MimeType(path);
            if (!mime.StartsWith("image/")) mime = "image/png";
            if (data.Length > 3_500_000 && app.Platform.DownscaleToJpeg(data, 1600) is { } jpeg) { data = jpeg; mime = "image/jpeg"; }
            return new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{Convert.ToBase64String(data)}" } };
        }
        void FlushImages()
        {
            if (pendingImages.Count == 0) return;
            if (vision)
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = "(Images returned by the tools above.)" } };
                foreach (var p in pendingImages) if (ImagePart(p) is { } part) parts.Add(part);
                output.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
            }
            pendingImages.Clear();
        }

        // Tool results must follow the assistant message that asked for them, and every call needs a result
        // (a stopped run can leave calls unanswered).
        var answered = chat.Messages.Where(x => x.Role == Role.Tool && x.ToolCallId != null).Select(x => x.ToolCallId!).ToHashSet();
        var knownCalls = new HashSet<string>();
        foreach (var m in chat.Messages.Where(x => !x.IsError))
        {
            switch (m.Role)
            {
                case Role.System: continue;
                case Role.User:
                {
                    FlushImages();
                    var text = new StringBuilder(m.Content);
                    foreach (var a in m.Attachments.Where(a => a.Kind != AttachmentKind.Image))
                        text.Append($"\n\n<file name=\"{a.Filename}\">\n{a.ExtractedText ?? ""}\n</file>");
                    var images = m.Attachments.Where(a => a.Kind == AttachmentKind.Image).ToList();
                    if (images.Count == 0 || !vision)
                    {
                        if (images.Count > 0) text.Append($"\n\n(The user attached {images.Count} image(s), but this model can't see images.)");
                        output.Add(new JsonObject { ["role"] = "user", ["content"] = text.ToString() });
                    }
                    else
                    {
                        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text.Length == 0 ? "See the attached image." : text.ToString() } };
                        foreach (var a in images) if (ImagePart(a.Path) is { } part) parts.Add(part);
                        output.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
                    }
                    break;
                }
                case Role.Assistant:
                {
                    FlushImages();
                    var a = new JsonObject { ["role"] = "assistant", ["content"] = m.Content };
                    var calls = m.ToolCalls.Where(c => answered.Contains(c.Id)).ToList();
                    if (calls.Count > 0)
                    {
                        var arr = new JsonArray();
                        foreach (var c in calls)
                        {
                            var d = new JsonObject
                            {
                                ["id"] = c.Id,
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.IfEmpty("{}") },
                            };
                            if (c.Extra != null) d["extra_content"] = c.Extra.DeepClone();
                            arr.Add(d);
                            knownCalls.Add(c.Id);
                        }
                        a["tool_calls"] = arr;
                        if (m.Content.Length == 0) a["content"] = null;
                    }
                    else if (m.Content.Length == 0) continue;
                    output.Add(a);
                    break;
                }
                case Role.Tool:
                    // Drop tool messages whose assistant call was removed (keeps APIs happy).
                    if (m.ToolCallId == null || !knownCalls.Contains(m.ToolCallId)) continue;
                    output.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = m.Content });
                    // Only screenshots go back to the model; generated images are just shown to the user.
                    if (m.ToolName is "take_screenshot" or "browser_screenshot" or "present_file") pendingImages.AddRange(m.Images);
                    break;
            }
        }
        FlushImages();
        return output;
    }

    static string FriendlyError(Exception error, ModelRef model)
    {
        if (error is not ApiException e) return "⚠️ " + error.Message;
        return e.Status switch
        {
            401 or 403 => $"⚠️ {model.Provider.DisplayName()} rejected the API key. Open Accounts (top right) to fix it.\n\n{e.Message}",
            429 => $"⚠️ Rate limit reached for {model.Name}. Wait a bit, or switch models. See Usage for details.\n\n{e.Message}",
            404 => $"⚠️ Model not available: {model.Name}.\n\n{e.Message}",
            503 => $"⚠️ {model.Provider.DisplayName()} is overloaded right now. Try again or switch models.\n\n{e.Message}",
            _ => "⚠️ " + e.Describe(),
        };
    }

    // MARK: Message mutation

    ChatMessage Append(ChatMessage m)
    {
        if (!app.Chats.Contains(chat)) return m;
        chat.Messages.Add(m);
        chat.UpdatedAt = DateTime.UtcNow;
        app.NotifyChat(chat.Id);
        return m;
    }

    void Update(ChatMessage m, Action<ChatMessage> f)
    {
        f(m);
        if (chat.Messages.Contains(m)) app.NotifyMessage(chat.Id, m.Id);
    }

    void Remove(ChatMessage m)
    {
        if (chat.Messages.Remove(m)) app.NotifyChat(chat.Id);
    }
}

public static class ProjectFiles
{
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "build", "bin", "obj", ".vs", ".venv", "venv", "__pycache__", "dist", ".next", "target" };

    public static List<string> List(Project p, int limit = 500)
    {
        var root = p.Folder;
        var output = new List<string>();
        if (!Directory.Exists(root)) return output;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0 && output.Count < limit)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { continue; }
            foreach (var e in entries)
            {
                var name = Path.GetFileName(e);
                if (name.StartsWith('.')) continue;
                FileAttributes attrs;
                try { attrs = File.GetAttributes(e); } catch { continue; }
                if ((attrs & FileAttributes.Hidden) != 0) continue;
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (!Skipped.Contains(name) && (attrs & FileAttributes.ReparsePoint) == 0) stack.Push(e);
                    continue;
                }
                output.Add(Path.GetRelativePath(root, e));
                if (output.Count >= limit) break;
            }
        }
        output.Sort(StringComparer.OrdinalIgnoreCase);
        return output;
    }
}
