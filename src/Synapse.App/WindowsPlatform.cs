using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Synapse;

namespace Synapse.App;

/// API keys in Windows Credential Manager (Control Panel → Credential Manager → Windows Credentials, "Synapse:…").
sealed class CredentialStore : ISecretStore
{
    readonly Dictionary<string, string?> _cache = [];
    readonly object _gate = new();

    static string Target(string account) => "Synapse:" + account;

    public string? Get(string account)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(account, out var v)) return v;
            string? value = null;
            try { value = Native.ReadCredential(Target(account)); } catch { }
            _cache[account] = string.IsNullOrEmpty(value) ? null : value;
            return _cache[account];
        }
    }

    public void Set(string account, string? value)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(value)) Native.DeleteCredential(Target(account));
            else Native.WriteCredential(Target(account), account, value.Trim());
            _cache[account] = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}

sealed class WindowsPlatform : IPlatform
{
    readonly Func<Window?> _owner;
    readonly Action<string, string> _notify;
    readonly Lazy<HardwareInfo> _hardware = new(DetectHardware);

    public WindowsPlatform(Func<Window?> owner, Action<string, string> notify)
    {
        _owner = owner;
        _notify = notify;
    }

    public string OsName
    {
        get
        {
            var v = Environment.OSVersion.Version;
            var name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
            var display = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
            return $"{name}{(display != null ? " " + display : "")} (build {v.Build}, {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";
        }
    }

    public string ShellName => ResolveShell().Name;
    public bool IsWindows => true;
    public ISecretStore Secrets { get; } = new CredentialStore();
    public HardwareInfo Hardware => _hardware.Value;
    public BrowserAgent? Browser { get; set; }

    // MARK: Shell

    static (string Name, string Path, bool IsPowerShell) ResolveShell()
    {
        var pref = AppSettings.Shared.Shell;
        var pwsh = Util.Which("pwsh");
        var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        return pref switch
        {
            "cmd" => ("Command Prompt", Path.Combine(Environment.SystemDirectory, "cmd.exe"), false),
            "powershell" => ("Windows PowerShell", ps, true),
            "pwsh" when pwsh != null => ("PowerShell 7", pwsh, true),
            _ when pwsh != null && pref != "powershell" => ("PowerShell 7", pwsh, true),
            _ => ("Windows PowerShell", ps, true),
        };
    }

    public Task<ProcessResult> RunShell(string command, string cwd, double timeout, CancellationToken ct)
    {
        var (_, path, isPs) = ResolveShell();
        if (!isPs) return ProcessRunner.Run(path, ["/d", "/s", "/c", command], cwd, timeout, ct: ct);
        // UTF-8 output, no progress bars, wide tables, and the real exit code.
        var script = "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $OutputEncoding=[System.Text.Encoding]::UTF8\n"
                   + "& {\n" + command + "\n} *>&1 | Out-String -Width 220 -Stream\n"
                   + "if ($LASTEXITCODE) { exit $LASTEXITCODE }";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return ProcessRunner.Run(path, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-OutputFormat", "Text", "-EncodedCommand", encoded],
            cwd, timeout, ct: ct);
    }

    // MARK: Screen & files

    public Task<string?> TakeScreenshot(string path) => Task.Run<string?>(() =>
    {
        try
        {
            var r = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bmp = new System.Drawing.Bitmap(r.Width, r.Height);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, System.Drawing.CopyPixelOperation.SourceCopy);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return null;
        }
        catch (Exception e) { return e.Message; }
    });

    public Task<(bool Ok, string Message)> Open(string target) => Task.Run(() =>
    {
        var t = Util.ExpandHome(target.Trim().Trim('"'));
        try
        {
            // App names ("notepad", "excel") resolve through App Paths; paths and URLs open with their handler.
            Process.Start(new ProcessStartInfo(t) { UseShellExecute = true, WorkingDirectory = Paths.Home })?.Dispose();
            return (true, $"Opened {target}");
        }
        catch (Exception e)
        {
            if (!t.Contains('\\') && !t.Contains('/') && !t.Contains('.'))
                return (false, $"Couldn't find an app called “{target}”. Try its .exe name, full path, or a URI like ms-settings: or spotify:. ({e.Message})");
            return (false, e.Message);
        }
    });

    public void RevealInFileManager(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
            else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch { }
    }

    public void OpenExternal(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    public Task<string?> GetClipboardText() => OnUi(() =>
    {
        for (var i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { Thread.Sleep(60); }   // another app may hold the clipboard
        }
        return null;
    });

    public Task SetClipboardText(string text) => OnUi(() =>
    {
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetDataObject(text, true); return true; }
            catch { Thread.Sleep(60); }
        }
        return false;
    });

    public byte[]? DownscaleToJpeg(byte[] data, int maxSide)
    {
        try
        {
            var probe = BitmapFrame.Create(new MemoryStream(data), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var landscape = probe.PixelWidth >= probe.PixelHeight;
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new MemoryStream(data);
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (landscape) img.DecodePixelWidth = Math.Min(maxSide, probe.PixelWidth);
            else img.DecodePixelHeight = Math.Min(maxSide, probe.PixelHeight);
            img.EndInit();
            img.Freeze();
            var enc = new JpegBitmapEncoder { QualityLevel = 82 };
            enc.Frames.Add(BitmapFrame.Create(img));
            using var output = new MemoryStream();
            enc.Save(output);
            return output.ToArray();
        }
        catch { return null; }
    }

    public Task<string[]?> PickFiles(bool multiple, bool folders, string? title = null) => OnUi(() =>
    {
        var owner = _owner();
        if (folders)
        {
            var d = new OpenFolderDialog { Title = title ?? "Choose a folder", Multiselect = multiple };
            return d.ShowDialog(owner) == true ? d.FolderNames : null;
        }
        var f = new OpenFileDialog { Title = title ?? "Choose files", Multiselect = multiple, Filter = "All files|*.*" };
        return f.ShowDialog(owner) == true ? f.FileNames : null;
    });

    public Task<string?> PickSaveFile(string suggestedName, string filter) => OnUi(() =>
    {
        var d = new SaveFileDialog { FileName = suggestedName, Filter = filter, AddExtension = true };
        return d.ShowDialog(_owner()) == true ? d.FileName : null;
    });

    public void TrackChildProcess(Process p) => Native.KillWithApp(p);

    public void Notify(string title, string body) => _notify(title, body);

    public void Trash(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else if (File.Exists(path))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); } catch { }
        }
    }

    /// Puts the folder holding synapse.exe on the user's PATH.
    public string InstallCli()
    {
        var dir = Path.Combine(AppContext.BaseDirectory.TrimEnd('\\'), "cli");
        var cli = Path.Combine(dir, "synapse.exe");
        if (!File.Exists(cli)) return $"The command-line tool wasn't found ({cli}). Reinstall Synapse to get it.";
        using var env = Registry.CurrentUser.OpenSubKey("Environment", writable: true) ?? Registry.CurrentUser.CreateSubKey("Environment");
        var current = env.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        var parts = current.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => Environment.ExpandEnvironmentVariables(p).TrimEnd('\\').Equals(dir, StringComparison.OrdinalIgnoreCase)))
            return $"Already installed: {cli}\nOpen a new terminal and try: synapse chat";
        env.SetValue("Path", string.Join(";", parts.Append(dir)), RegistryValueKind.ExpandString);
        Native.BroadcastEnvironmentChange();
        return $"Added {dir} to your PATH.\nOpen a new terminal (Windows Terminal, PowerShell or cmd) and try: synapse chat";
    }

    static Task<T> OnUi<T>(Func<T> f)
    {
        var d = Application.Current.Dispatcher;
        return d.CheckAccess() ? Task.FromResult(f()) : d.InvokeAsync(f).Task;
    }

    // MARK: Hardware

    static HardwareInfo DetectHardware()
    {
        var cpu = (Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string)?.Trim()
                  ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
        long free = 0;
        try { free = new DriveInfo(Path.GetPathRoot(Paths.Models)!).AvailableFreeSpace; } catch { }
        return new HardwareInfo(cpu, Native.TotalMemory(), Environment.ProcessorCount, free, DetectGpus(),
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
    }

    /// Reads display adapters from the registry. HardwareInformation.qwMemorySize holds the true dedicated
    /// VRAM (WMI's AdapterRAM caps at 4 GB).
    static List<GpuInfo> DetectGpus()
    {
        var list = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string name) continue;
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                long vram = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    _ => k.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                        _ => 0,
                    },
                };
                var l2 = name.ToLowerInvariant();
                var vendor = l2.Contains("nvidia") || l2.Contains("geforce") || l2.Contains("quadro") || l2.Contains("rtx") ? "nvidia"
                    : l2.Contains("amd") || l2.Contains("radeon") ? "amd"
                    : l2.Contains("intel") || l2.Contains("arc") ? "intel"
                    : l2.Contains("qualcomm") || l2.Contains("adreno") ? "qualcomm" : "other";
                if (list.Any(g => g.Name == name)) continue;
                list.Add(new GpuInfo(name, vram, vendor));
            }
        }
        catch { }
        return list.OrderByDescending(g => g.VramBytes).ToList();
    }
}
