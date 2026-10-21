using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Synapse;

public enum EngineState { Stopped, Loading, Running, Failed }

/// Runs local models by managing a llama.cpp `llama-server` process, which exposes an
/// OpenAI-compatible API on 127.0.0.1.
public sealed class LocalEngine(IPlatform platform, Action changed)
{
    public const int Port = 18080;

    public EngineState State { get; private set; } = EngineState.Stopped;
    public string StatusText { get; private set; } = "";
    public string? LoadedModelId { get; private set; }
    public bool Installing { get; private set; }
    public double InstallProgress { get; private set; }
    public string InstallLog { get; private set; } = "";

    readonly StringBuilder _log = new();
    readonly object _logGate = new();
    Process? _process;
    Task? _loading;
    string? _loadingId;

    public string Log { get { lock (_logGate) return _log.ToString(); } }

    void AppendLog(string s)
    {
        lock (_logGate)
        {
            _log.Append(s);
            if (_log.Length > 40_000) _log.Remove(0, _log.Length - 30_000);
        }
    }

    // MARK: Engine discovery

    static string ExeName => OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    public static string InstallRoot => Path.Combine(Paths.Engines, "llama.cpp");
    static string InfoFile => Path.Combine(InstallRoot, "engine.json");

    public sealed record InstalledEngine(string Tag, string Backend, string Folder);

    public static InstalledEngine? Installed => Json.ReadFile<InstalledEngine>(InfoFile) is { } e && File.Exists(Path.Combine(e.Folder, ExeName)) ? e : null;

    /// The llama-server Synapse will run: a custom path, the copy Synapse installed, or one on PATH.
    public string? ServerPath
    {
        get
        {
            var custom = AppSettings.Shared.LlamaServerPath.Trim().Trim('"');
            if (custom.Length > 0)
            {
                if (File.Exists(custom)) return custom;
                if (Directory.Exists(custom) && File.Exists(Path.Combine(custom, ExeName))) return Path.Combine(custom, ExeName);
            }
            if (Installed is { } e) return Path.Combine(e.Folder, ExeName);
            return Util.Which("llama-server");
        }
    }

    public bool IsAvailable => ServerPath != null;

    // MARK: Load / stop

    public Task EnsureLoaded(LocalModel model, int contextSize, CancellationToken ct = default)
    {
        if (LoadedModelId == model.Id && State == EngineState.Running && _process is { HasExited: false }) return Task.CompletedTask;
        // Several chats may ask for the same model at once: share one load.
        if (_loading != null && _loadingId == model.Id && !_loading.IsCompleted) return _loading.WaitAsync(ct);
        _loadingId = model.Id;
        _loading = Load(model, contextSize);
        return _loading.WaitAsync(ct);
    }

    async Task Load(LocalModel model, int contextSize)
    {
        Stop();
        State = EngineState.Loading;
        StatusText = model.DisplayName;
        lock (_logGate) _log.Clear();
        changed();

        var bin = ServerPath;
        if (bin == null)
        {
            Fail("llama.cpp isn't installed");
            throw new ApiException(0, "llama.cpp isn't installed. Open Models → Engine and click Install.");
        }
        if (model.MainFile == null) { Fail("Model file missing"); throw new ApiException(0, "Model file missing"); }
        var modelPath = Path.Combine(model.Directory, model.MainFile.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(modelPath)) { Fail("Model file missing"); throw new ApiException(0, $"Model file missing: {modelPath}"); }

        var args = new List<string>
        {
            "-m", modelPath, "--host", "127.0.0.1", "--port", Port.ToString(),
            "-c", contextSize.ToString(), "--jinja", "--alias", model.DisplayName,
        };
        var layers = AppSettings.Shared.GpuLayers.Trim().ToLowerInvariant();
        if (layers is not ("" or "auto")) args.AddRange(["-ngl", layers]);
        if (model.Mmproj != null) args.AddRange(["--mmproj", Path.Combine(model.Directory, model.Mmproj.Replace('/', Path.DirectorySeparatorChar))]);

        var psi = new ProcessStartInfo(bin)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(bin)!,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data + "\n"); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data + "\n"); };
        AppendLog($"> {Path.GetFileName(bin)} {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}\n");
        try
        {
            p.Start();
        }
        catch (Exception e)
        {
            Fail("The engine didn't start");
            throw new ApiException(0, "Couldn't start llama-server: " + e.Message);
        }
        platform.TrackChildProcess(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        _process = p;
        LoadedModelId = model.Id;

        // Wait until the server answers.
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var url = $"http://127.0.0.1:{Port}/health";
        var lastPush = DateTime.UtcNow;
        while (DateTime.UtcNow < deadline)
        {
            if (_process != p) throw new OperationCanceledException("Another model was loaded.");
            if (p.HasExited)
            {
                Fail("The engine exited");
                LoadedModelId = null;
                throw new ApiException(0, "The local engine stopped while loading.\n" + ExplainCrash(Log));
            }
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var resp = await ChatClient.Http.GetAsync(url, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    State = EngineState.Running;
                    StatusText = model.DisplayName;
                    changed();
                    return;
                }
            }
            catch { }
            if ((DateTime.UtcNow - lastPush).TotalSeconds > 1) { changed(); lastPush = DateTime.UtcNow; }   // stream the log
            await Task.Delay(400);
        }
        Stop();
        throw new ApiException(0, "Timed out loading the model.");
    }

    /// Turns the usual llama.cpp failures into advice.
    static string ExplainCrash(string log)
    {
        var tail = log.Length > 1500 ? log[^1500..] : log;
        if (Regex.IsMatch(log, "out of memory|failed to allocate|cudaMalloc failed|ErrorOutOfDeviceMemory", RegexOptions.IgnoreCase))
            return "It ran out of memory. Try a smaller quantization, a shorter context (Settings → Models), or fewer GPU layers.\n\n" + tail;
        if (Regex.IsMatch(log, "unknown model architecture|unknown architecture", RegexOptions.IgnoreCase))
            return "This llama.cpp build doesn't know this model yet. Update the engine in Models → Engine.\n\n" + tail;
        if (Regex.IsMatch(log, "couldn't bind|address already in use|bind failed", RegexOptions.IgnoreCase))
            return $"Port {Port} is in use by another program.\n\n" + tail;
        return tail;
    }

    void Fail(string message)
    {
        State = EngineState.Failed;
        StatusText = message;
        changed();
    }

    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
        LoadedModelId = null;
        if (State != EngineState.Stopped) { State = EngineState.Stopped; StatusText = ""; changed(); }
    }

    /// The `model` value to send to the running server.
    public string RequestModelName(LocalModel model) => model.DisplayName;

    // MARK: Install (prebuilt llama.cpp from GitHub releases)

    public sealed record ReleaseAsset(string Name, string Url, long Size);

    /// Which llama.cpp build fits this PC best.
    public static string AutoBackend(HardwareInfo hw)
    {
        var arm = hw.Arch.Contains("arm");
        var gpu = hw.BestGpu;
        if (gpu == null) return "cpu";
        return gpu.Vendor switch
        {
            "nvidia" => arm ? "cuda-13" : "cuda-12",
            "amd" or "intel" or "qualcomm" => "vulkan",
            _ => "vulkan",
        };
    }

    /// Picks the release zips for a backend: llama-<tag>-bin-win-<backend>-<arch>.zip (+ the CUDA runtime zip).
    public static List<ReleaseAsset> PickAssets(IEnumerable<ReleaseAsset> assets, string backend, string arch)
    {
        var a = arch.Contains("arm") ? "arm64" : "x64";
        var list = assets.ToList();
        ReleaseAsset? Find(string pattern) => list.Where(x => Regex.IsMatch(x.Name, pattern, RegexOptions.IgnoreCase)).OrderByDescending(x => x.Name).FirstOrDefault();
        switch (backend)
        {
            case "cuda-12":
            case "cuda-13":
            {
                var major = backend[^2..];
                var main = Find($@"^llama-.*-bin-win-cuda-{major}\.[\d.]+-{a}\.zip$");
                if (main == null) break;
                var ver = Regex.Match(main.Name, @"cuda-([\d.]+)-").Groups[1].Value;
                var rt = Find($@"^cudart-llama-bin-win-cuda-{Regex.Escape(ver)}-{a}\.zip$");
                return rt == null ? [main] : [main, rt];
            }
            case "rocm":
            {
                if (Find($@"^llama-.*-bin-win-(rocm|hip)[\w.-]*-{a}\.zip$") is { } r) return [r];
                break;
            }
            default:
                if (Find($@"^llama-.*-bin-win-{Regex.Escape(backend)}-{a}\.zip$") is { } m) return [m];
                break;
        }
        // Fall back to the CPU build, which always exists.
        return Find($@"^llama-.*-bin-win-cpu-{a}\.zip$") is { } cpu ? [cpu] : [];
    }

    public static async Task<(string Tag, List<ReleaseAsset> Assets)> LatestRelease(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/llama.cpp/releases/latest");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        using var resp = await ChatClient.Http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, "Couldn't check llama.cpp releases: " + ChatClient.ErrorMessage(text));
        var obj = JsonNodeExt.ParseOrNull(text);
        var tag = obj.Str("tag_name");
        var assets = (obj?["assets"] as JsonArray ?? []).Select(a => new ReleaseAsset(a.Str("name"), a.Str("browser_download_url"),
            a?["size"] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0)).ToList();
        return (tag, assets);
    }

    public async Task Install(string backend, CancellationToken ct = default)
    {
        if (Installing) return;
        Installing = true;
        InstallProgress = 0;
        InstallLog = "";
        void Log(string s) { InstallLog += s + "\n"; changed(); }
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Log("Automatic install downloads the Windows build of llama.cpp. On this system, install llama.cpp yourself and set its path in Settings → Models.");
                return;
            }
            var hw = platform.Hardware;
            if (backend == "auto") backend = AutoBackend(hw);
            Log($"Checking the latest llama.cpp release… (backend: {backend}, {hw.Arch})");
            var (tag, assets) = await LatestRelease(ct);
            var pick = PickAssets(assets, backend, hw.Arch);
            if (pick.Count == 0) throw new ApiException(0, $"No Windows build found in llama.cpp {tag}.");
            var actualBackend = Regex.Match(pick[0].Name, @"bin-win-(.+)-(x64|arm64)\.zip").Groups[1].Value;
            if (Installed is { } current && current.Tag == tag && current.Backend == actualBackend)
            {
                Log($"✓ llama.cpp {tag} ({actualBackend}) is already installed and up to date.");
                return;
            }
            var folder = Path.Combine(InstallRoot, $"{tag}-{actualBackend}");
            var staging = folder + ".staging";
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            long total = pick.Sum(x => x.Size), done = 0;
            foreach (var asset in pick)
            {
                Log($"Downloading {asset.Name} ({Util.FormatBytes(asset.Size)})…");
                var zip = Path.Combine(Paths.Engines, asset.Name);
                var before = done;
                await DownloadManager.DownloadFile(new HttpRequestMessage(HttpMethod.Get, asset.Url), zip,
                    got => InstallProgress = total > 0 ? (double)(before + got) / total : 0, null, changed, ct);
                done += asset.Size;
                Log($"Extracting {asset.Name}…");
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging, overwriteFiles: true), ct);
                try { File.Delete(zip); } catch { }
            }
            // Some zips nest everything in a folder; flatten so llama-server.exe sits at the top.
            var exe = Directory.EnumerateFiles(staging, ExeName, SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new ApiException(0, "The download didn't contain llama-server.exe.");
            var exeDir = Path.GetDirectoryName(exe)!;
            if (!exeDir.Equals(staging, StringComparison.OrdinalIgnoreCase))
                foreach (var f in Directory.EnumerateFiles(staging, "*.dll", SearchOption.AllDirectories))
                    if (!Path.GetDirectoryName(f)!.Equals(exeDir, StringComparison.OrdinalIgnoreCase))
                        File.Copy(f, Path.Combine(exeDir, Path.GetFileName(f)), overwrite: true);
            Stop();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.Move(staging, folder);
            var finalDir = Path.Combine(folder, Path.GetRelativePath(staging, exeDir));
            Json.WriteFile(InfoFile, new InstalledEngine(tag, actualBackend, Path.GetFullPath(finalDir)));
            // Remove older builds.
            foreach (var d in Directory.EnumerateDirectories(InstallRoot))
                if (!Util.IsInside(finalDir, d)) try { Directory.Delete(d, true); } catch { }
            InstallProgress = 1;
            Log($"✓ llama.cpp {tag} ({actualBackend}) installed.");
        }
        catch (OperationCanceledException) { Log("Cancelled."); }
        catch (Exception e) { Log("✗ Install failed: " + (e is ApiException a ? a.Describe() : e.Message)); }
        finally
        {
            Installing = false;
            changed();
        }
    }

    /// Asks the installed llama-server for its version (e.g. "version: 6123 (abc1234)").
    public async Task<string?> Version()
    {
        if (ServerPath is not { } bin) return null;
        var r = await ProcessRunner.Run(bin, ["--version"], timeout: 15);
        var m = Regex.Match(r.Output, @"version:\s*(\S+)(\s*\(\w+\))?");
        return m.Success ? m.Value.Replace("version: ", "b") : null;
    }
}
