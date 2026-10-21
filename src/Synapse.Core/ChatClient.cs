using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Synapse;

public sealed class ApiException(int status, string message, bool isTimeout = false) : Exception(message)
{
    public int Status { get; } = status;
    public bool IsTimeout { get; } = isTimeout;
    public string Describe() => Status > 0 ? $"HTTP {Status}: {Message}" : Message;
}

public sealed class RateLimitInfo
{
    public int? LimitRequests { get; set; }
    public int? RemainingRequests { get; set; }
    public string? ResetRequests { get; set; }
    public int? LimitTokens { get; set; }
    public int? RemainingTokens { get; set; }
    public string? ResetTokens { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static RateLimitInfo? From(HttpResponseMessage resp)
    {
        string? H(string k) => resp.Headers.TryGetValues(k, out var v) ? v.FirstOrDefault() : null;
        int? I(string k) => int.TryParse(H(k), out var i) ? i : null;
        var r = new RateLimitInfo
        {
            LimitRequests = I("x-ratelimit-limit-requests"),
            RemainingRequests = I("x-ratelimit-remaining-requests"),
            ResetRequests = H("x-ratelimit-reset-requests"),
            LimitTokens = I("x-ratelimit-limit-tokens"),
            RemainingTokens = I("x-ratelimit-remaining-tokens"),
            ResetTokens = H("x-ratelimit-reset-tokens"),
        };
        return r.LimitRequests == null && r.LimitTokens == null && r.RemainingRequests == null ? null : r;
    }
}

public readonly record struct TokenUsage(int Prompt, int Completion);

public abstract record StreamEvent
{
    public sealed record Content(string Text) : StreamEvent;
    public sealed record Reasoning(string Text) : StreamEvent;
    /// Final, assembled tool calls.
    public sealed record ToolCalls(List<ToolCall> Calls) : StreamEvent;
    public sealed record Usage(TokenUsage Tokens) : StreamEvent;
    public sealed record RateLimit(RateLimitInfo Info) : StreamEvent;
}

/// Splits `<think>…</think>` / `<thought>…</thought>` out of streamed content.
public sealed class ThinkTagSplitter
{
    string _buffer = "";
    bool _inside;
    static readonly string[] Opens = ["<think>", "<thought>"];
    static readonly string[] Closes = ["</think>", "</thought>"];

    public (string Content, string Reasoning) Feed(string s)
    {
        _buffer += s;
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        while (_buffer.Length > 0)
        {
            var tags = _inside ? Closes : Opens;
            int best = -1, bestLen = 0;
            foreach (var t in tags)
            {
                var i = _buffer.IndexOf(t, StringComparison.Ordinal);
                if (i >= 0 && (best < 0 || i < best)) { best = i; bestLen = t.Length; }
            }
            if (best >= 0)
            {
                (_inside ? reasoning : content).Append(_buffer[..best]);
                _buffer = _buffer[(best + bestLen)..];
                _inside = !_inside;
                continue;
            }
            // Keep a possible partial tag at the end of the buffer.
            var keep = 0;
            foreach (var tag in tags)
                for (var n = Math.Min(tag.Length - 1, _buffer.Length); n >= 1; n--)
                    if (_buffer.EndsWith(tag[..n], StringComparison.Ordinal)) { keep = Math.Max(keep, n); break; }
            (_inside ? reasoning : content).Append(_buffer[..^keep]);
            _buffer = _buffer[^keep..];
            break;
        }
        return (content.ToString(), reasoning.ToString());
    }

    public (string Content, string Reasoning) Flush()
    {
        var b = _buffer;
        _buffer = "";
        return _inside ? ("", b) : (b, "");
    }
}

public static class ChatClient
{
    public static ISecretStore? Keys { get; set; }

    public static readonly HttpClient Http = MakeClient();

    static HttpClient MakeClient()
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
        };
        var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Synapse-Windows/" + (typeof(ChatClient).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
        return c;
    }

    public static string? ApiKey(ProviderId p) => p == ProviderId.Local ? null : Keys?.Get(p.Key());

    public static HttpRequestMessage MakeRequest(ProviderId provider, string path, JsonNode? body)
    {
        var url = provider.BaseUrl() + path;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new ApiException(0, "Bad base URL: " + url);
        var req = new HttpRequestMessage(body == null ? HttpMethod.Get : HttpMethod.Post, uri);
        if (ApiKey(provider) is { } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (provider == ProviderId.Openrouter)
        {
            req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/Agamemnon-Intelligence/Synapse-windows");
            req.Headers.TryAddWithoutValidation("X-Title", "Synapse");
        }
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    public static string ErrorMessage(string data)
    {
        var obj = JsonNodeExt.ParseOrNull(data);
        if (obj is JsonArray arr && arr.Count > 0) obj = arr[0];
        if (obj is JsonObject d)
        {
            if (d["error"] is JsonObject e && e["message"] is JsonValue m && m.TryGetValue<string>(out var ms)) return ms;
            if (d["error"] is JsonValue ev && ev.TryGetValue<string>(out var es)) return es;
            if (d["message"] is JsonValue mv && mv.TryGetValue<string>(out var mm)) return mm;
        }
        return data.Length > 600 ? data[..600] : data;
    }

    /// Idle timeout: cloud servers that stall get cut off; local engines may need longer for big prompts.
    public static TimeSpan IdleTimeout(ProviderId p) => TimeSpan.FromSeconds(p == ProviderId.Local ? 600 : 60);

    /// Streams an OpenAI-compatible chat completion.
    public static async IAsyncEnumerable<StreamEvent> Stream(ProviderId provider, JsonObject body, [EnumeratorCancellation] CancellationToken ct = default)
    {
        body = (JsonObject)body.DeepClone();
        body["stream"] = true;
        if (provider != ProviderId.Local) body["stream_options"] = new JsonObject { ["include_usage"] = true };

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var idleSpan = IdleTimeout(provider);
        idle.CancelAfter(idleSpan);
        using var req = MakeRequest(provider, "/chat/completions", body);
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, idle.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(0, "The request timed out.", isTimeout: true); }
        catch (HttpRequestException e) { throw new ApiException(0, e.Message); }

        using (resp)
        {
            if (RateLimitInfo.From(resp) is { } rl) yield return new StreamEvent.RateLimit(rl);
            if (!resp.IsSuccessStatusCode)
            {
                var text = await ReadLimited(resp, 64_000, ct);
                throw new ApiException((int)resp.StatusCode, ErrorMessage(text));
            }

            var splitter = new ThinkTagSplitter();
            var calls = new List<ToolCall>();
            var nonSse = new StringBuilder();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (true)
            {
                string? line;
                idle.CancelAfter(idleSpan);
                try { line = await reader.ReadLineAsync(idle.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(0, "The server stopped responding.", isTimeout: true); }
                catch (IOException e) { throw new ApiException(0, "Connection lost: " + e.Message); }
                if (line == null) break;
                ct.ThrowIfCancellationRequested();
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (line.Length > 0 && !line.StartsWith(':') && !line.StartsWith("event:")) nonSse.Append(line).Append('\n');
                    continue;
                }
                var payload = line[5..].Trim();
                if (payload == "[DONE]") break;
                if (JsonNodeExt.ParseOrNull(payload) is not JsonObject obj) continue;
                if (obj["error"] is { } err)
                    throw new ApiException(0, ErrorMessage(new JsonObject { ["error"] = err.DeepClone() }.ToJsonString()));
                if (obj["usage"] is JsonObject u)
                    yield return new StreamEvent.Usage(new TokenUsage(u.Int("prompt_tokens", 0), u.Int("completion_tokens", 0)));
                if (obj["choices"] is not JsonArray { Count: > 0 } choices || choices[0]?["delta"] is not JsonObject delta) continue;
                foreach (var key in new[] { "reasoning_content", "reasoning", "thinking" })
                    if (delta[key] is JsonValue rv && rv.TryGetValue<string>(out var r) && r.Length > 0) yield return new StreamEvent.Reasoning(r);
                if (delta["content"] is JsonValue cv && cv.TryGetValue<string>(out var c) && c.Length > 0)
                {
                    var (content, reasoning) = splitter.Feed(c);
                    if (reasoning.Length > 0) yield return new StreamEvent.Reasoning(reasoning);
                    if (content.Length > 0) yield return new StreamEvent.Content(content);
                }
                if (delta["tool_calls"] is JsonArray tcs) MergeToolCalls(calls, tcs);
            }

            if (nonSse.Length > 0 && calls.Count == 0)
            {
                // Some servers ignore stream=true; handle a plain JSON reply.
                var obj = JsonNodeExt.ParseOrNull(nonSse.ToString());
                if (obj?["choices"]?[0]?["message"] is JsonObject msg)
                {
                    if (msg["reasoning_content"] is JsonValue rv && rv.TryGetValue<string>(out var r) && r.Length > 0) yield return new StreamEvent.Reasoning(r);
                    if (msg["content"] is JsonValue cv && cv.TryGetValue<string>(out var c))
                    {
                        var (content, reasoning) = splitter.Feed(c);
                        if (reasoning.Length > 0) yield return new StreamEvent.Reasoning(reasoning);
                        if (content.Length > 0) yield return new StreamEvent.Content(content);
                    }
                    if (msg["tool_calls"] is JsonArray tcs)
                    {
                        for (var i = 0; i < tcs.Count; i++) if (tcs[i] is JsonObject t) t["index"] = i;
                        MergeToolCalls(calls, tcs);
                    }
                    if (obj["usage"] is JsonObject u)
                        yield return new StreamEvent.Usage(new TokenUsage(u.Int("prompt_tokens", 0), u.Int("completion_tokens", 0)));
                }
                else
                {
                    throw new ApiException(0, ErrorMessage(nonSse.ToString()));
                }
            }
            var rest = splitter.Flush();
            if (rest.Reasoning.Length > 0) yield return new StreamEvent.Reasoning(rest.Reasoning);
            if (rest.Content.Length > 0) yield return new StreamEvent.Content(rest.Content);
            var finished = calls.Where(x => x.Name.Length > 0).ToList();
            if (finished.Count > 0) yield return new StreamEvent.ToolCalls(finished);
        }
    }

    /// Assembles streamed tool-call deltas. Providers differ: most send `index`, some only `id`.
    internal static void MergeToolCalls(List<ToolCall> calls, JsonArray deltas)
    {
        foreach (var tcNode in deltas)
        {
            if (tcNode is not JsonObject tc) continue;
            var fn = tc["function"] as JsonObject;
            var id = tc.StrOrNull("id");
            int slot;
            var idx = tc.Int("index", -1);
            if (idx >= 0) slot = idx;
            else if (!string.IsNullOrEmpty(id) && calls.FindIndex(x => x.Id == id) is var existing && existing >= 0) slot = existing;
            else if (!string.IsNullOrEmpty(id)) slot = calls.Count;
            else slot = Math.Max(0, calls.Count - 1);
            while (calls.Count <= slot) calls.Add(new ToolCall { Id = "call_" + Guid.NewGuid().ToString("N")[..8] });
            if (!string.IsNullOrEmpty(id)) calls[slot].Id = id;
            if (fn.StrOrNull("name") is { Length: > 0 } n) calls[slot].Name = n;
            if (fn?["arguments"] is JsonValue av)
                calls[slot].Arguments += av.TryGetValue<string>(out var a) ? a : av.ToJsonString();
            else if (fn?["arguments"] is JsonObject ao)
                calls[slot].Arguments += ao.ToJsonString();
            if (tc["extra_content"] is { } extra) calls[slot].Extra = extra.DeepClone();
        }
    }

    static async Task<string> ReadLimited(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        try
        {
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[max];
            int total = 0, n;
            while (total < max && (n = await s.ReadAsync(buf.AsMemory(total, max - total), ct)) > 0) total += n;
            return Encoding.UTF8.GetString(buf, 0, total);
        }
        catch { return ""; }
    }

    /// Lists model IDs from a provider's /models endpoint.
    public static async Task<List<ModelRef>> ListModels(ProviderId provider, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var req = MakeRequest(provider, "/models", null);
        HttpResponseMessage resp;
        try { resp = await Http.SendAsync(req, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(0, "Timed out listing models."); }
        catch (HttpRequestException e) { throw new ApiException(0, e.Message); }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, ErrorMessage(text));
            var list = JsonNodeExt.ParseOrNull(text)?["data"] as JsonArray;
            if (list == null) return [];
            var refs = new List<ModelRef>();
            foreach (var m in list)
            {
                var id = m.StrOrNull("id");
                if (string.IsNullOrEmpty(id)) continue;
                if (id.StartsWith("models/")) id = id[7..];
                if (!IsChatModel(provider, id)) continue;
                var name = m.StrOrNull("name").IfEmpty(id);
                if (provider == ProviderId.Google) name = id;   // Google's display names drop the version
                refs.Add(new ModelRef(provider, id, name, GuessVision(provider, id, m)));
            }
            return refs.DistinctBy(r => r.Id).OrderBy(r => Rank(provider, r.Id), StringComparer.Ordinal).ToList();
        }
    }

    static readonly string[] Excluded = ["tts", "embedding", "embed", "veo", "lyria", "imagen", "-image", "image-", "nano-banana", "live",
        "audio", "aqa", "robotics", "transcribe", "whisper", "deep-research", "antigravity", "computer-use", "guard", "moderation",
        "orpheus", "playai", "distil-whisper", "translate", "omni"];

    internal static bool IsChatModel(ProviderId provider, string id)
    {
        var l = id.ToLowerInvariant();
        if (Excluded.Any(l.Contains)) return false;
        if (provider == ProviderId.Openrouter) return l.EndsWith(":free");
        if (provider == ProviderId.Google) return l.StartsWith("gemini") || l.StartsWith("gemma");
        return true;
    }

    static bool GuessVision(ProviderId provider, string id, JsonNode? raw)
    {
        var l = id.ToLowerInvariant();
        if (provider == ProviderId.Google) return true;
        if (raw?["architecture"]?["input_modalities"] is JsonArray mods)
            return mods.Any(x => x?.GetValue<string>() == "image");
        return l.Contains("vision") || l.Contains("llama-4") || l.Contains("-vl") || l.Contains("gemma-3") || l.Contains("gemma-4");
    }

    internal static string Rank(ProviderId provider, string id)
    {
        var l = id.ToLowerInvariant();
        string[] preferred = provider switch
        {
            ProviderId.Google => ["gemini-3.8-flash", "gemini-flash-latest", "gemini-3.7-flash", "gemini-pro-latest", "gemini-3.1-pro", "gemini-flash-lite-latest"],
            ProviderId.Groq => ["openai/gpt-oss-120b", "llama-3.3-70b", "moonshotai/kimi", "qwen/qwen3", "openai/gpt-oss-20b", "meta-llama/llama-4"],
            _ => [],
        };
        var i = Array.FindIndex(preferred, l.StartsWith);
        return i >= 0 ? $"0{i:00}{l}" : "1" + l;
    }
}
