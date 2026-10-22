using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Synapse;

// MARK: - JSON

public static class Json
{
    public static readonly JsonSerializerOptions Options = Make(indented: false);
    public static readonly JsonSerializerOptions Pretty = Make(indented: true);

    static JsonSerializerOptions Make(bool indented)
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        o.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return o;
    }

    public static string Serialize<T>(T value, bool pretty = false) => JsonSerializer.Serialize(value, pretty ? Pretty : Options);
    public static T? Deserialize<T>(string s) => JsonSerializer.Deserialize<T>(s, Options);

    /// Writes atomically: a crash mid-write never leaves a truncated state file. The previous
    /// version is kept as path.bak, so a file a newer or older build can't read is never the only copy.
    public static void WriteFile<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(value, pretty: true));
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true); return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// Reads path, falling back to path.bak when the file is missing or damaged.
    public static T? ReadFile<T>(string path) => ReadFile<T>(path, out _);

    public static T? ReadFile<T>(string path, out bool fromBackup)
    {
        fromBackup = false;
        if (TryRead<T>(path) is { } value) return value;
        fromBackup = true;
        return TryRead<T>(path + ".bak");
    }

    static T? TryRead<T>(string path)
    {
        try { return File.Exists(path) ? Deserialize<T>(File.ReadAllText(path)) : default; }
        catch { return default; }
    }
}

// MARK: - Paths

public static class Paths
{
    static string? _root;

    /// %LOCALAPPDATA%\Synapse (models are large, so nothing roams). SYNAPSE_HOME overrides it.
    public static string Root
    {
        get
        {
            if (_root != null) return _root;
            var env = Environment.GetEnvironmentVariable("SYNAPSE_HOME");
            var root = !string.IsNullOrWhiteSpace(env) ? env
                : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Synapse");
            System.IO.Directory.CreateDirectory(root);
            return _root = root;
        }
        set { _root = value; System.IO.Directory.CreateDirectory(value); }
    }

    public static string Dir(string name)
    {
        var p = System.IO.Path.Combine(Root, name);
        System.IO.Directory.CreateDirectory(p);
        return p;
    }

    public static string Models => Dir("Models");
    public static string Attachments => Dir("Attachments");
    public static string Images => Dir("Images");
    public static string Sites => Dir("Sites");
    public static string Engines => Dir("Engines");
    public static string WebView => Dir("WebView2");
    public static string Logs => Dir("Logs");
    public static string StateFile => System.IO.Path.Combine(Root, "state.json");
    public static string UsageFile => System.IO.Path.Combine(Root, "usage.json");
    public static string SettingsFile => System.IO.Path.Combine(Root, "settings.json");

    public static string ProjectsRoot
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) docs = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var p = System.IO.Path.Combine(docs, "Synapse Projects");
            System.IO.Directory.CreateDirectory(p);
            return p;
        }
    }

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

// MARK: - Settings

public enum ImageProvider { Pollinations, Gemini, Openai }

public sealed class AppSettings
{
    static AppSettings? _shared;
    public static AppSettings Shared
    {
        get => _shared ??= Json.ReadFile<AppSettings>(Paths.SettingsFile) ?? new AppSettings();
        set => _shared = value;
    }

    public string CustomBaseUrl { get; set; } = "http://localhost:1234/v1";
    public bool CustomEnabled { get; set; }
    public ImageProvider ImageProvider { get; set; } = ImageProvider.Pollinations;
    public string GeminiImageModel { get; set; } = "gemini-3.1-flash-image";
    /// For ImageProvider.Openai: uses the custom server's /images/generations.
    public string OpenaiImageModel { get; set; } = "gpt-image-1";
    public int ContextSize { get; set; } = 8192;
    /// "auto" lets llama.cpp fit layers to free VRAM, "all", "0" (CPU only) or a number.
    public string GpuLayers { get; set; } = "auto";
    /// llama.cpp build to install: auto, cpu, vulkan, cuda-12, cuda-13, rocm.
    public string LlamaBackend { get; set; } = "auto";
    /// Optional path to an existing llama-server(.exe).
    public string LlamaServerPath { get; set; } = "";
    public bool ApiServerEnabled { get; set; } = true;
    public int ApiServerPort { get; set; } = 11435;
    public string SystemPrompt { get; set; } = "";
    public ModelRef? DefaultModel { get; set; } = new(ProviderId.Google, "gemini-3.8-flash", "gemini-3.8-flash", true);
    public double ShellTimeout { get; set; } = 120;
    /// "auto" picks PowerShell 7 when installed, else Windows PowerShell.
    public string Shell { get; set; } = "auto";
    /// dark, light or system.
    public string Theme { get; set; } = "dark";
    public bool CloseToTray { get; set; } = true;
    public bool LaunchAtStartup { get; set; }
    public bool GlobalHotkey { get; set; } = true;
    public bool NotifyWhenDone { get; set; } = true;
    public bool SendWithEnter { get; set; } = true;

    public void Save()
    {
        try { Json.WriteFile(Paths.SettingsFile, this); } catch { }
    }
}

// MARK: - Hardware

public sealed record GpuInfo(string Name, long VramBytes, string Vendor)
{
    public double VramGB => VramBytes / 1_073_741_824.0;
}

public enum Fit { Great = 0, Good, Tight, TooLarge }

public sealed record HardwareInfo(string Cpu, long MemoryBytes, int Cores, long FreeDiskBytes, IReadOnlyList<GpuInfo> Gpus, string Arch)
{
    public double MemoryGB => MemoryBytes / 1_073_741_824.0;

    /// The GPU llama.cpp will offload to: the one with the most dedicated memory.
    public GpuInfo? BestGpu => Gpus.Where(g => g.VramBytes >= 2L * 1024 * 1024 * 1024).OrderByDescending(g => g.VramBytes).FirstOrDefault();

    public static HardwareInfo Generic()
    {
        long free = 0;
        try { free = new DriveInfo(System.IO.Path.GetPathRoot(Paths.Root) ?? "/").AvailableFreeSpace; } catch { }
        var mem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new HardwareInfo(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString() + " CPU",
            mem, Environment.ProcessorCount, free, [], System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }

    /// Estimates how a model file of `bytes` runs: fully on the GPU, split with system RAM, or not at all.
    public Fit FitFor(long bytes)
    {
        var needGB = bytes / 1_073_741_824.0 * 1.1 + 0.9;   // weights + KV cache/context + runtime
        if (BestGpu is { } g && needGB <= g.VramGB * 0.92) return Fit.Great;
        // Without enough VRAM, layers that don't fit run from system memory (slower, but it works).
        var total = MemoryGB + (BestGpu?.VramGB ?? 0) * 0.9;
        var ratio = needGB / Math.Max(total, 1);
        if (ratio <= 0.5) return BestGpu == null ? Fit.Great : Fit.Good;
        if (ratio <= 0.72) return Fit.Good;
        if (ratio <= 0.9) return Fit.Tight;
        return Fit.TooLarge;
    }

    public string Advice => (BestGpu?.VramGB ?? 0) switch
    {
        >= 20 => "Large models run well on your GPU.",
        >= 11 => "Models up to ~14B run fully on your GPU.",
        >= 7 => "Models up to ~8B run fully on your GPU.",
        >= 3.5 => "Small models (up to ~4B) run on your GPU; larger ones share system memory.",
        _ => MemoryGB switch
        {
            < 10 => "Small models (up to ~4B) run best.",
            < 20 => "Models up to ~8B run well on the CPU.",
            < 40 => "Models up to ~14B run on the CPU (slower than a GPU).",
            _ => "Large models fit in memory; a GPU makes them much faster.",
        },
    };
}

public static class FitInfo
{
    public static string Label(this Fit f) => f switch
    {
        Fit.Great => "Runs great",
        Fit.Good => "Runs well",
        Fit.Tight => "Tight fit",
        _ => "Too large",
    };

    public static string Detail(this Fit f) => f switch
    {
        Fit.Great => "Fits comfortably. Plenty of memory left for other apps.",
        Fit.Good => "Fits. Close heavy apps for best speed.",
        Fit.Tight => "May be slow or swap. Quit other apps first.",
        _ => "Needs more memory than this PC has.",
    };
}

// MARK: - Helpers

public static class Util
{
    public static string FormatBytes(long b)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = b;
        int i = 0;
        while (v >= 1000 && i < units.Length - 1) { v /= 1000; i++; }
        return i == 0 ? $"{b} bytes" : v >= 100 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    public static string Truncated(this string s, int n) =>
        s.Length > n ? s[..n] + $"\n…[truncated {s.Length - n} characters]" : s;

    public static string IfEmpty(this string? s, string fallback) => string.IsNullOrEmpty(s) ? fallback : s;

    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '-');
        var parts = sb.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries);
        var s = string.Join("-", parts);
        if (s.Length > 48) s = s[..48].TrimEnd('-');
        return s.IfEmpty("site");
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    public static StringComparison PathComparison => IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// True when `path` is `root` or inside it. Compares whole path segments, so C:\proj never matches C:\project2.
    public static bool IsInside(string path, string root)
    {
        var p = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        var r = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
        if (p.Equals(r, PathComparison)) return true;
        return p.StartsWith(r + System.IO.Path.DirectorySeparatorChar, PathComparison)
            || p.StartsWith(r + '/', PathComparison);
    }

    static readonly string[] ImageExts = ["png", "jpg", "jpeg", "gif", "webp", "bmp", "tif", "tiff", "heic", "ico"];
    public static bool IsImage(string path) => ImageExts.Contains(System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant());

    public static string Ext(string path) => System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static string MimeType(string path) => Ext(path) switch
    {
        "html" or "htm" => "text/html",
        "css" => "text/css",
        "js" or "mjs" => "text/javascript",
        "json" or "map" => "application/json",
        "svg" => "image/svg+xml",
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "ico" => "image/x-icon",
        "tif" or "tiff" => "image/tiff",
        "avif" => "image/avif",
        "pdf" => "application/pdf",
        "txt" or "md" or "log" or "csv" => "text/plain",
        "xml" => "application/xml",
        "wasm" => "application/wasm",
        "woff" => "font/woff",
        "woff2" => "font/woff2",
        "ttf" => "font/ttf",
        "otf" => "font/otf",
        "mp3" => "audio/mpeg",
        "wav" => "audio/wav",
        "ogg" => "audio/ogg",
        "mp4" => "video/mp4",
        "webm" => "video/webm",
        _ => "application/octet-stream",
    };

    public static string ExpandHome(string path) =>
        path == "~" ? Paths.Home
        : path.StartsWith("~/") || path.StartsWith("~\\") ? System.IO.Path.Combine(Paths.Home, path[2..])
        : Environment.ExpandEnvironmentVariables(path);

    /// Finds an executable on PATH (adds .exe on Windows).
    public static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var names = IsWindows && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? new[] { name + ".exe", name } : [name];
        foreach (var dir in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var n in names)
            {
                try
                {
                    var f = System.IO.Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(f)) return f;
                }
                catch { }
            }
        return null;
    }
}

public sealed record ProcessResult(int Status, string Output);

public static class ProcessRunner
{
    /// Runs a program and captures stdout+stderr. Kills the whole tree on timeout or cancellation.
    public static async Task<ProcessResult> Run(string file, IEnumerable<string> args, string? cwd = null, double timeout = 120,
        IDictionary<string, string>? env = null, string? stdin = null, CancellationToken ct = default, Action<string>? onOutput = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd != null && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        var output = new StringBuilder();
        var gate = new object();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnData(object _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            lock (gate)
            {
                if (output.Length < 4_000_000) output.Append(e.Data).Append('\n');
            }
            onOutput?.Invoke(e.Data + "\n");
        }
        p.OutputDataReceived += OnData;
        p.ErrorDataReceived += OnData;
        try { p.Start(); }
        catch (Exception e) { return new ProcessResult(-1, $"Failed to start {file}: {e.Message}"); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin != null)
        {
            try { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); } catch { }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeout)));
        var timedOut = false;
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch { }
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        // Let the async readers drain.
        try { p.WaitForExit(2000); } catch { }
        string text;
        lock (gate) text = output.ToString();
        if (timedOut) text += $"\n[timed out after {(int)timeout}s]";
        if (ct.IsCancellationRequested) text += "\n[cancelled]";
        int code;
        try { code = p.HasExited ? p.ExitCode : -1; } catch { code = -1; }
        return new ProcessResult(code, text);
    }
}

// MARK: - Main-thread dispatch

/// Everything in AppState runs on one thread (the UI thread in the app), like @MainActor on the Mac.
/// This context gives console hosts (dev host, tests) the same single-threaded behavior.
public sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    readonly Thread _thread;

    public SingleThreadContext(string name = "Synapse main")
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    void Loop()
    {
        SetSynchronizationContext(this);
        foreach (var (cb, state) in _queue.GetConsumingEnumerable())
        {
            try { cb(state); }
            catch (Exception e) { Console.Error.WriteLine("Unhandled: " + e); }
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); } catch (InvalidOperationException) { }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId) { d(state); return; }
        using var done = new ManualResetEventSlim();
        Exception? error = null;
        Post(_ => { try { d(state); } catch (Exception e) { error = e; } finally { done.Set(); } }, null);
        done.Wait();
        if (error != null) throw error;
    }

    public Task<T> Run<T>(Func<Task<T>> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ =>
        {
            try { tcs.SetResult(await f()); } catch (Exception e) { tcs.SetException(e); }
        }, null);
        return tcs.Task;
    }

    public Task Run(Action f) => Run<bool>(() => { f(); return Task.FromResult(true); });

    public void Dispose() => _queue.CompleteAdding();
}
