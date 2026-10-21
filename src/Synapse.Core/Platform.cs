using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Synapse;

/// Where API keys live. On Windows: Credential Manager (like the macOS Keychain).
public interface ISecretStore
{
    string? Get(string account);
    void Set(string account, string? value);
}

/// Everything that differs between operating systems. The Windows app implements this with
/// Win32/WPF; the dev host and tests use GenericPlatform.
public interface IPlatform
{
    string OsName { get; }
    /// Name of the shell run_shell uses, for prompts ("PowerShell").
    string ShellName { get; }
    bool IsWindows { get; }
    ISecretStore Secrets { get; }
    HardwareInfo Hardware { get; }
    /// The browser the model can drive, or null when this host has none.
    BrowserAgent? Browser { get; }

    Task<ProcessResult> RunShell(string command, string cwd, double timeout, CancellationToken ct);
    /// Captures every monitor into a PNG. Returns an error message, or null on success.
    Task<string?> TakeScreenshot(string path);
    Task<(bool Ok, string Message)> Open(string target);
    void RevealInFileManager(string path);
    void OpenExternal(string url);
    Task<string?> GetClipboardText();
    Task SetClipboardText(string text);
    /// Re-encodes a large image as JPEG no bigger than `maxSide`. Null when unsupported.
    byte[]? DownscaleToJpeg(byte[] data, int maxSide);
    Task<string[]?> PickFiles(bool multiple, bool folders, string? title = null);
    Task<string?> PickSaveFile(string suggestedName, string filter);
    /// Kills a child process with the app (a Job Object on Windows).
    void TrackChildProcess(Process p);
    void Notify(string title, string body);
    string InstallCli();
    /// Sends a file to the Recycle Bin when possible.
    void Trash(string path);
}

/// Minimal surface of a web view that BrowserAgent drives.
public interface IWebEngine
{
    /// Runs JavaScript and returns the JSON-encoded result.
    Task<string?> Eval(string script);
    void Navigate(string url);
    void GoBack();
    void GoForward();
    void Reload();
    void StopLoading();
    bool IsLoading { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    string Url { get; }
    string Title { get; }
    Task<byte[]?> CapturePng();
}

/// Plain-file key storage for the dev host. The Windows app never uses this.
public sealed class FileSecretStore(string path) : ISecretStore
{
    readonly object _gate = new();

    Dictionary<string, string> Load() => Json.ReadFile<Dictionary<string, string>>(path) ?? [];

    public string? Get(string account)
    {
        lock (_gate) return Load().TryGetValue(account, out var v) && v.Length > 0 ? v : null;
    }

    public void Set(string account, string? value)
    {
        lock (_gate)
        {
            var d = Load();
            if (string.IsNullOrWhiteSpace(value)) d.Remove(account); else d[account] = value.Trim();
            Json.WriteFile(path, d);
        }
    }
}

/// Cross-platform fallback used by the dev host and tests (bash/xdg-open, no browser panel).
public class GenericPlatform : IPlatform
{
    public GenericPlatform(ISecretStore? secrets = null)
    {
        Secrets = secrets ?? new FileSecretStore(System.IO.Path.Combine(Paths.Root, "dev-secrets.json"));
    }

    public virtual string OsName => System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public virtual string ShellName => OperatingSystem.IsWindows() ? "PowerShell" : "bash";
    public bool IsWindows => OperatingSystem.IsWindows();
    public ISecretStore Secrets { get; }
    public virtual HardwareInfo Hardware { get; } = HardwareInfo.Generic();
    public virtual BrowserAgent? Browser => null;
    public List<string> Notifications { get; } = [];

    public virtual Task<ProcessResult> RunShell(string command, string cwd, double timeout, CancellationToken ct) =>
        OperatingSystem.IsWindows()
            ? ProcessRunner.Run("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command], cwd, timeout, ct: ct)
            : ProcessRunner.Run("/bin/bash", ["-lc", command], cwd, timeout, ct: ct);

    public virtual Task<string?> TakeScreenshot(string path) => Task.FromResult<string?>("Screenshots need the Windows app.");

    public virtual async Task<(bool Ok, string Message)> Open(string target)
    {
        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        var r = await ProcessRunner.Run(opener, [target], timeout: 20);
        return (r.Status == 0, r.Status == 0 ? $"Opened {target}" : r.Output);
    }

    public virtual void RevealInFileManager(string path) => _ = Open(File.Exists(path) ? System.IO.Path.GetDirectoryName(path)! : path);
    public virtual void OpenExternal(string url) => _ = Open(url);

    string _clipboard = "";
    public virtual Task<string?> GetClipboardText() => Task.FromResult<string?>(_clipboard);
    public virtual Task SetClipboardText(string text) { _clipboard = text; return Task.CompletedTask; }
    public virtual byte[]? DownscaleToJpeg(byte[] data, int maxSide) => null;
    public virtual Task<string[]?> PickFiles(bool multiple, bool folders, string? title = null) => Task.FromResult<string[]?>(null);
    public virtual Task<string?> PickSaveFile(string suggestedName, string filter) => Task.FromResult<string?>(null);
    public virtual void TrackChildProcess(Process p) { }
    public virtual void Notify(string title, string body) => Notifications.Add(title + ": " + body);
    public virtual string InstallCli() => "Run `dotnet run --project src/Synapse.Cli -- chat` to use the command-line tool.";

    public virtual void Trash(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}

public static class JsonNodeExt
{
    public static string Str(this JsonNode? n, string key, string fallback = "")
    {
        var v = n?[key];
        if (v == null) return fallback;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<string>(out var s)) return s;
            return jv.ToJsonString();
        }
        return v.ToJsonString();
    }

    public static string? StrOrNull(this JsonNode? n, string key) => n?[key] == null ? null : n.Str(key);

    public static int Int(this JsonNode? n, string key, int fallback = -1)
    {
        var v = n?[key];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var i)) return i;
            if (jv.TryGetValue<double>(out var d)) return (int)d;
            if (jv.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) return p;
        }
        return fallback;
    }

    public static bool Bool(this JsonNode? n, string key, bool fallback = false)
    {
        var v = n?[key];
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out var b)) return b;
            if (jv.TryGetValue<string>(out var s)) return s.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return fallback;
    }

    public static Guid? Guid(this JsonNode? n, string key) =>
        System.Guid.TryParse(n.Str(key), out var g) ? g : null;

    public static JsonNode? ParseOrNull(string s)
    {
        try { return JsonNode.Parse(s); } catch { return null; }
    }
}
