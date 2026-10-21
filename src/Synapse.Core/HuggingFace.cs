using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Synapse;

public sealed record HfModelSummary(string Id, int Downloads, int Likes, string? PipelineTag, List<string> Tags)
{
    public string Author => Id.Split('/')[0];
    public string Name => Id.Split('/').Last();
}

public sealed record HfFile(string Path, long Size);

/// One downloadable variant of a repo (a GGUF quantization, possibly split into parts).
public sealed record ModelVariant
{
    public string Repo { get; init; } = "";
    public ModelFormat Format { get; init; } = ModelFormat.Gguf;
    /// e.g. "Q4_K_M"
    public string Label { get; init; } = "";
    /// GGUF parts to download.
    public List<string> Files { get; init; } = [];
    public string? Mmproj { get; init; }
    public long SizeBytes { get; init; }

    public string Id => Repo + ":" + Label;
    public string? MainFile => Files.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
}

public static class HuggingFace
{
    public const string Base = "https://huggingface.co";
    public static ISecretStore? Keys { get; set; }

    static HttpRequestMessage Request(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, Base + path);
        if (Keys?.Get("huggingface") is { } token) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        return req;
    }

    public static HttpRequestMessage FileRequest(string repo, string file)
    {
        var encoded = string.Join("/", file.Split('/').Select(Uri.EscapeDataString));
        return Request($"/{repo}/resolve/main/{encoded}");
    }

    static readonly string[] NonChat = ["embed", "whisper", "tts", "asr", "parakeet", "rerank", "-image", "kokoro", "music", "audio"];

    public static async Task<List<HfModelSummary>> Search(string query, int limit = 40, CancellationToken ct = default)
    {
        var q = $"/api/models?search={Uri.EscapeDataString(query)}&filter=gguf&sort=downloads&limit={limit}";
        using var req = Request(q);
        using var resp = await ChatClient.Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, ChatClient.ErrorMessage(text));
        var list = new List<HfModelSummary>();
        foreach (var m in JsonNodeExt.ParseOrNull(text) as JsonArray ?? [])
        {
            var id = m.StrOrNull("id") ?? m.StrOrNull("modelId");
            if (id == null) continue;
            var l = id.ToLowerInvariant();
            // Hide things that aren't chat models.
            if (NonChat.Any(l.Contains)) continue;
            var tags = (m?["tags"] as JsonArray)?.Select(t => t?.GetValue<string>() ?? "").ToList() ?? [];
            list.Add(new HfModelSummary(id, m.Int("downloads", 0), m.Int("likes", 0), m.StrOrNull("pipeline_tag"), tags));
        }
        return list;
    }

    public static async Task<List<HfFile>> Files(string repo, CancellationToken ct = default)
    {
        using var req = Request($"/api/models/{repo}/tree/main?recursive=true");
        using var resp = await ChatClient.Http.SendAsync(req, ct);
        if ((int)resp.StatusCode is 401 or 403)
            throw new ApiException((int)resp.StatusCode, "This model is gated. Accept its license on huggingface.co and add a HuggingFace token in Accounts.");
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, ChatClient.ErrorMessage(text));
        var files = new List<HfFile>();
        foreach (var f in JsonNodeExt.ParseOrNull(text) as JsonArray ?? [])
        {
            if (f.Str("type") != "file") continue;
            // LFS files report their real size under lfs.size.
            var size = f?["lfs"]?["size"] is JsonValue ls && ls.TryGetValue<long>(out var l) ? l
                     : f?["size"] is JsonValue sv && sv.TryGetValue<long>(out var s) ? s : 0;
            files.Add(new HfFile(f.Str("path"), size));
        }
        return files;
    }

    /// Turns a repo's file list into downloadable variants.
    public static List<ModelVariant> Variants(string repo, List<HfFile> files)
    {
        var ggufs = files.Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (ggufs.Count == 0) return [];
        var mmproj = ggufs.Where(f => f.Path.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Path.Contains("F16") ? 0 : f.Path.Contains("BF16") ? 1 : 2).FirstOrDefault();
        var weights = ggufs.Where(f => !f.Path.Contains("mmproj", StringComparison.OrdinalIgnoreCase)
                                    && !f.Path.Contains("mtp-", StringComparison.OrdinalIgnoreCase)
                                    && !f.Path.Contains("imatrix", StringComparison.OrdinalIgnoreCase));
        // Group split files (…-00001-of-00003.gguf) into one variant.
        var groups = weights.GroupBy(f => Regex.Replace(f.Path, @"-\d{5}-of-\d{5}\.gguf$", ".gguf", RegexOptions.IgnoreCase));
        return groups.Select(g => new ModelVariant
        {
            Repo = repo,
            Label = QuantLabel(g.Key),
            Files = g.Select(f => f.Path).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Mmproj = mmproj?.Path,
            SizeBytes = g.Sum(f => f.Size) + (mmproj?.Size ?? 0),
        })
        .GroupBy(v => v.Label).Select(g => g.First())   // the same quant in two folders: keep one
        .OrderBy(v => v.SizeBytes).ToList();
    }

    public static string QuantLabel(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        var m = Regex.Match(name, @"(UD-)?(I?Q\d[\w]*|BF16|F16|F32|MXFP4\w*)$", RegexOptions.IgnoreCase);
        return m.Success ? m.Value : name;
    }

    static readonly string[] Preferred = ["Q4_K_M", "UD-Q4_K_XL", "Q4_K_S", "IQ4_XS", "Q5_K_M", "Q6_K", "Q8_0", "MXFP4", "F16"];

    /// Picks the best variant for this PC: the highest-quality common quant that still fits comfortably.
    public static ModelVariant? RecommendedVariant(List<ModelVariant> variants, HardwareInfo hw)
    {
        var fitting = variants.Where(v => hw.FitFor(v.SizeBytes) <= Fit.Good).ToList();
        foreach (var p in Preferred)
            if (fitting.FirstOrDefault(v => v.Label.Equals(p, StringComparison.OrdinalIgnoreCase)) is { } v) return v;
        if (fitting.Count > 0) return fitting[^1];
        foreach (var p in Preferred)
            if (variants.FirstOrDefault(v => v.Label.Equals(p, StringComparison.OrdinalIgnoreCase)) is { } v) return v;
        return variants.FirstOrDefault();
    }
}

// MARK: - Curated catalog

public sealed record CatalogEntry(string Repo, string Title, string Blurb, string[] Tags);

public static class Catalog
{
    public static readonly CatalogEntry[] Entries =
    [
        new("unsloth/Qwen3.5-0.8B-GGUF", "Qwen3.5 0.8B", "Tiny and instant. Good for quick questions on any PC.", ["fast"]),
        new("unsloth/Qwen3.5-2B-GGUF", "Qwen3.5 2B", "Small, quick all-rounder with thinking mode.", ["fast", "reasoning"]),
        new("unsloth/gemma-4-E2B-it-GGUF", "Gemma 4 E2B", "Google's efficient small model. Sees images.", ["vision", "fast"]),
        new("unsloth/Llama-3.2-3B-Instruct-GGUF", "Llama 3.2 3B", "Meta's compact chat model. Reliable tool use.", ["tools"]),
        new("unsloth/Phi-4-mini-instruct-GGUF", "Phi-4 mini", "Microsoft's small model, strong at math and logic.", ["reasoning"]),
        new("unsloth/Qwen3.5-4B-GGUF", "Qwen3.5 4B", "Best overall pick for 8 GB GPUs. Thinks, codes, sees images.", ["reasoning", "coding", "vision"]),
        new("unsloth/gemma-4-E4B-it-GGUF", "Gemma 4 E4B", "Great writing quality and vision in a small package.", ["vision", "writing"]),
        new("unsloth/Qwen3.5-9B-GGUF", "Qwen3.5 9B", "Much smarter. Wants a 12 GB GPU or 16 GB of RAM.", ["reasoning", "coding", "vision"]),
        new("unsloth/gemma-4-12b-it-GGUF", "Gemma 4 12B", "Excellent writer and image understanding.", ["vision", "writing"]),
        new("unsloth/gpt-oss-20b-GGUF", "gpt-oss 20B", "OpenAI's open-weight reasoning model.", ["reasoning", "tools"]),
        new("unsloth/gemma-4-26B-A4B-it-GGUF", "Gemma 4 26B A4B", "Mixture-of-experts: big-model quality, small-model speed.", ["vision", "writing"]),
        new("unsloth/Qwen3.8-27B-GGUF", "Qwen3.8 27B", "Top-tier open model for coding and reasoning.", ["reasoning", "coding"]),
        new("unsloth/Qwen3.6-35B-A3B-GGUF", "Qwen3.6 35B A3B", "Fast mixture-of-experts with strong tool use. Runs well split across GPU and RAM.", ["reasoning", "coding", "tools"]),
        new("unsloth/gemma-4-31B-it-GGUF", "Gemma 4 31B", "Google's largest open Gemma.", ["vision", "writing"]),
    ];
}

// MARK: - Downloads

public sealed class DownloadItem
{
    public string Id { get; init; } = "";
    public ModelVariant Variant { get; init; } = new();
    public string DisplayName { get; init; } = "";
    public long Received { get; set; }
    public string CurrentFile { get; set; } = "";
    public string? Error { get; set; }
    public bool Done { get; set; }
    /// Bytes per second, smoothed.
    public double Speed { get; set; }
    public double Fraction => Variant.SizeBytes > 0 ? Math.Min(1, (double)Received / Variant.SizeBytes) : 0;
}

/// Downloads GGUF files from HuggingFace with resume support (.part files survive restarts).
public sealed class DownloadManager(Action changed)
{
    public Dictionary<string, DownloadItem> Items { get; } = [];
    readonly Dictionary<string, CancellationTokenSource> _tasks = [];
    public Action<LocalModel>? OnComplete { get; set; }

    public bool IsDownloading(string variantId) => Items.TryGetValue(variantId, out var i) && !i.Done && i.Error == null;

    public void Start(ModelVariant variant, string displayName)
    {
        if (IsDownloading(variant.Id)) return;
        Items[variant.Id] = new DownloadItem { Id = variant.Id, Variant = variant, DisplayName = displayName };
        var cts = new CancellationTokenSource();
        _tasks[variant.Id] = cts;
        changed();
        _ = Run(variant, displayName, cts.Token);
    }

    public void Cancel(string id)
    {
        if (_tasks.Remove(id, out var cts)) cts.Cancel();
        Items.Remove(id);
        changed();
    }

    public static string FolderFor(ModelVariant v) => Path.Combine(Paths.Models, v.Repo.Replace("/", "__") + "__" + v.Label);

    async Task Run(ModelVariant variant, string displayName, CancellationToken ct)
    {
        var dir = FolderFor(variant);
        var all = variant.Files.ToList();
        if (variant.Mmproj != null) all.Add(variant.Mmproj);
        long completed = 0;
        var item = Items[variant.Id];
        try
        {
            foreach (var file in all)
            {
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                item.CurrentFile = file;
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                {
                    completed += new FileInfo(dest).Length;
                    item.Received = completed;
                    changed();
                    continue;
                }
                var baseBytes = completed;
                await DownloadFile(HuggingFace.FileRequest(variant.Repo, file), dest, got =>
                {
                    item.Received = baseBytes + got;
                }, speed => item.Speed = speed, changed, ct);
                completed += new FileInfo(dest).Length;
                item.Received = completed;
                changed();
            }
            item.Done = true;
            var model = new LocalModel
            {
                Id = variant.Id,
                Repo = variant.Repo,
                DisplayName = displayName + " · " + variant.Label,
                Directory = dir,
                MainFile = variant.MainFile,
                Mmproj = variant.Mmproj,
                SizeBytes = Math.Max(completed, variant.SizeBytes),
            };
            OnComplete?.Invoke(model);
        }
        catch (OperationCanceledException)
        {
            Items.Remove(variant.Id);
        }
        catch (Exception e)
        {
            item.Error = e is ApiException a ? a.Describe() : e.Message;
        }
        _tasks.Remove(variant.Id);
        changed();
    }

    /// Streams a file to `dest`, resuming from `dest.part` when the server supports ranges.
    public static async Task DownloadFile(HttpRequestMessage req, string dest, Action<long> progress, Action<double>? speed,
        Action? tick, CancellationToken ct)
    {
        // The copy loop runs off the UI thread; `tick` is posted back to it.
        var ui = SynchronizationContext.Current;
        void Tick() { if (tick == null) return; if (ui != null) ui.Post(_ => tick(), null); else tick(); }
        var part = dest + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var resp = await ChatClient.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // The .part is already complete.
            File.Move(part, dest, overwrite: true);
            progress(existing);
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var msg = (int)resp.StatusCode is 401 or 403
                ? "Access denied. This model may be gated: accept its license on huggingface.co and add a token in Accounts."
                : $"Download failed (HTTP {(int)resp.StatusCode})";
            throw new ApiException((int)resp.StatusCode, msg);
        }
        var append = existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!append) existing = 0;
        await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        await using (var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var buffer = new byte[1 << 20];
            long got = existing;
            var lastTick = DateTime.UtcNow;
            long lastBytes = got;
            double smoothed = 0;
            int n;
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                try { n = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ApiException(0, "The download stalled. Click Retry to resume."); }
                if (n == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                got += n;
                progress(got);
                var now = DateTime.UtcNow;
                var dt = (now - lastTick).TotalSeconds;
                if (dt >= 0.5)
                {
                    var inst = (got - lastBytes) / dt;
                    smoothed = smoothed == 0 ? inst : smoothed * 0.7 + inst * 0.3;
                    speed?.Invoke(smoothed);
                    lastTick = now;
                    lastBytes = got;
                    Tick();
                }
            }
        }
        File.Move(part, dest, overwrite: true);
    }
}
