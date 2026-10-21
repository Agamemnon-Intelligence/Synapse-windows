using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Synapse;

namespace Synapse.App;

public partial class MainWindow : Window
{
    const string UiHost = "app.synapse";
    const string FileHost = "file.synapse";

    AppState? _app;
    BridgeHost? _bridge;
    WindowsPlatform? _platform;
    WebViewEngine? _engine;
    Tray? _tray;
    bool _quitting;
    bool _toldAboutTray;
    double _browserWidth = 540;
    HwndSource? _hwnd;
    bool _hotkey;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Start();
        Activated += (_, _) => { if (_app != null) _app.WindowFocused = true; };
        Deactivated += (_, _) => { if (_app != null) _app.WindowFocused = false; };
        SourceInitialized += (_, _) =>
        {
            _hwnd = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hwnd.AddHook(WndProc);
            ApplyTheme();
        };
    }

    public AppState? App => _app;

    bool _started;

    /// Starts the app. AppState (and with it the local API) comes up right away, even when Synapse
    /// starts hidden in the tray; the web views finish once the window is first shown.
    public void Start()
    {
        if (_started) return;
        _started = true;
        new WindowInteropHelper(this).EnsureHandle();
        _platform = new WindowsPlatform(() => IsVisible ? this : null, (title, body) => _tray?.Notify(title, body));
        _app = new AppState(_platform);
        _tray = new Tray(this, _app);
        _app.Changed += c => { if (c.Kind == ChangeKind.App) SyncBrowserPanel(); };
        ApplyStartup();
        ApplyHotkey();
        _ = InitWebViews();
    }

    async Task InitWebViews()
    {
        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException)
        {
            SplashText.Text = "Synapse needs the Microsoft Edge WebView2 Runtime, which is missing on this PC.\nInstall it (free, about 2 minutes), then open Synapse again.";
            SplashButton.Content = "Download WebView2 Runtime";
            SplashButton.Tag = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
            SplashButton.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebView, new CoreWebView2EnvironmentOptions { AreBrowserExtensionsEnabled = false });
            await Ui.EnsureCoreWebView2Async(env);
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception e)
        {
            SplashText.Text = "Couldn't start the interface: " + e.Message;
            return;
        }

        _engine = new WebViewEngine(Web);
        var browser = new BrowserAgent(_engine);
        _platform!.Browser = browser;
        _engine.StateChanged += () => { SyncBrowserToolbar(); browser.NotifyChanged(); };
        browser.Changed += SyncBrowserPanel;

        SetupUi(Ui.CoreWebView2);
        _bridge = new BridgeHost(_app!, json => { try { Ui.CoreWebView2.PostWebMessageAsString(json); } catch { } }, $"https://{FileHost}/?path=");
        _bridge.HostCommand += OnHostCommand;
        Ui.CoreWebView2.Navigate($"https://{UiHost}/index.html");
        SyncBrowserPanel();
        SyncBrowserToolbar();
    }

    // MARK: Interface web view

    void SetupUi(CoreWebView2 core)
    {
        var s = core.Settings;
        var dev = Debugger.IsAttached || Environment.GetEnvironmentVariable("SYNAPSE_DEVTOOLS") == "1";
        s.AreDevToolsEnabled = dev;
        s.AreBrowserAcceleratorKeysEnabled = dev;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = true;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        Ui.DefaultBackgroundColor = System.Drawing.Color.FromArgb(10, 11, 12);

        var ui = Path.Combine(AppContext.BaseDirectory, "ui");
        core.SetVirtualHostNameToFolderMapping(UiHost, ui, CoreWebView2HostResourceAccessKind.DenyCors);

        // Local files (attachments, generated images, previews) are served through file.synapse.
        core.AddWebResourceRequestedFilter($"https://{FileHost}/*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) =>
        {
            try
            {
                var uri = new Uri(e.Request.Uri);
                var path = System.Web.HttpUtility.ParseQueryString(uri.Query)["path"];
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK",
                    $"Content-Type: {Util.MimeType(path)}\r\nCache-Control: no-cache\r\nAccess-Control-Allow-Origin: https://{UiHost}");
            }
            catch
            {
                e.Response = core.Environment.CreateWebResourceResponse(null, 500, "Error", "");
            }
        };

        // The interface never navigates away; links open in the default browser.
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Host != UiHost)
            {
                e.Cancel = true;
                if (u.Scheme is "http" or "https" or "mailto") _platform?.OpenExternal(e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") _platform?.OpenExternal(e.Uri);
        };
        core.NavigationCompleted += (_, e) =>
        {
            Splash.Visibility = Visibility.Collapsed;
            if (!e.IsSuccess) Debug.WriteLine("UI navigation failed: " + e.WebErrorStatus);
        };
        core.ProcessFailed += (_, e) =>
        {
            // The renderer crashed or was killed: reload the interface (state lives in AppState).
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                Dispatcher.InvokeAsync(() => core.Reload());
        };
        core.WebMessageReceived += OnWebMessage;
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_bridge == null) return;
        string json;
        try { json = e.TryGetWebMessageAsString(); } catch { return; }
        // Drag and drop: WebView2 hands over File objects with their real paths.
        var files = e.AdditionalObjects?.OfType<CoreWebView2File>().Select(f => f.Path).ToArray();
        if (files is { Length: > 0 } && JsonNodeExt.ParseOrNull(json) is JsonObject msg)
        {
            var args = msg["args"] as JsonObject ?? [];
            var paths = new JsonArray();
            foreach (var p in files) paths.Add(p);
            args["paths"] = paths;
            msg["args"] = args;
            json = msg.ToJsonString();
        }
        _bridge.Receive(json);
    }

    void OnHostCommand(string cmd, JsonObject args)
    {
        switch (cmd)
        {
            case "settingsChanged":
                ApplyTheme();
                ApplyStartup();
                ApplyHotkey();
                break;
            case "quit":
                Quit();
                break;
            case "devtools":
                Ui.CoreWebView2?.OpenDevToolsWindow();
                break;
        }
    }

    /// Lets the tray and hotkey talk to the page ("newChat", "focusComposer").
    public void SendHost(string action)
    {
        try { Ui.CoreWebView2?.PostWebMessageAsString(new JsonObject { ["type"] = "host", ["data"] = new JsonObject { ["action"] = action } }.ToJsonString()); } catch { }
    }

    // MARK: Browser panel

    void SyncBrowserPanel()
    {
        if (_app == null) return;
        var show = _app.ShowBrowserPanel;
        var visible = BrowserPane.Visibility == Visibility.Visible;
        if (show != visible)
        {
            BrowserPane.Visibility = Splitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SplitterColumn.Width = new GridLength(show ? 5 : 0);
            BrowserColumn.Width = new GridLength(show ? _browserWidth : 0);
            BrowserColumn.MinWidth = show ? 380 : 0;
            if (show && Width < 1300 && WindowState == WindowState.Normal) Width = Math.Min(SystemParameters.WorkArea.Width, Width + _browserWidth * 0.6);
        }
        var b = _platform?.Browser;
        var busy = b is { AgentActive: true } && _app.Generating.Count > 0;
        AgentBanner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) AgentText.Text = "Synapse is using the browser" + (b!.LastAgentAction is { } a ? " · " + a : "");
    }

    void SyncBrowserToolbar()
    {
        if (_engine == null) return;
        BackButton.IsEnabled = _engine.CanGoBack;
        ForwardButton.IsEnabled = _engine.CanGoForward;
        ReloadButton.Content = _engine.IsLoading ? "" : "";
        ReloadButton.ToolTip = _engine.IsLoading ? "Stop" : "Reload";
        if (!Address.IsKeyboardFocused) Address.Text = _engine.Url;
        WebPlaceholder.Visibility = string.IsNullOrEmpty(_engine.Url) ? Visibility.Visible : Visibility.Collapsed;
    }

    void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) =>
        _browserWidth = Math.Max(380, BrowserColumn.ActualWidth);

    void Back_Click(object sender, RoutedEventArgs e) => _engine?.GoBack();
    void Forward_Click(object sender, RoutedEventArgs e) => _engine?.GoForward();
    void Reload_Click(object sender, RoutedEventArgs e) { if (_engine?.IsLoading == true) _engine.StopLoading(); else _engine?.Reload(); }

    void CloseBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_app == null) return;
        _app.ShowBrowserPanel = false;
        _app.NotifyApp();
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        MoreButton.ContextMenu.PlacementTarget = MoreButton;
        MoreButton.ContextMenu.IsOpen = true;
    }

    void OpenExternal_Click(object sender, RoutedEventArgs e) { if (_engine?.Url is { Length: > 0 } u) _platform?.OpenExternal(u); }
    void CopyAddress_Click(object sender, RoutedEventArgs e) { if (_engine?.Url is { Length: > 0 } u) _ = _platform?.SetClipboardText(u); }
    void DevTools_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.OpenDevToolsWindow();

    void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _platform?.Browser?.Load(Address.Text);
            Web.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            Address.Text = _engine?.Url ?? "";
            Web.Focus();
        }
    }

    void Address_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Dispatcher.InvokeAsync(Address.SelectAll);

    /// Stops the runs that are driving the browser so the user can take the wheel.
    void TakeOver_Click(object sender, RoutedEventArgs e)
    {
        if (_app == null) return;
        foreach (var id in _app.Generating.ToList())
            if (_app.FindChat(id) is { Capabilities.Browser: true }) _app.Stop(id);
        if (_platform?.Browser is { } b) { b.AgentActive = false; b.NotifyChanged(); }
        SyncBrowserPanel();
    }

    void SplashButton_Click(object sender, RoutedEventArgs e)
    {
        if (SplashButton.Tag is string url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    // MARK: Theme, startup, hotkey

    bool IsDark()
    {
        var t = AppSettings.Shared.Theme;
        if (t == "light") return false;
        if (t == "system")
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0;
        return true;
    }

    void ApplyTheme()
    {
        var dark = IsDark();
        var bg = dark ? Color.FromRgb(10, 11, 12) : Color.FromRgb(246, 246, 248);
        Background = new SolidColorBrush(bg);
        Splash.Background = Background;
        Resources["PaneBrush"] = new SolidColorBrush(dark ? Color.FromRgb(14, 15, 17) : Color.FromRgb(238, 238, 241));
        Resources["FieldBrush"] = new SolidColorBrush(dark ? Color.FromRgb(26, 27, 31) : Colors.White);
        Resources["TextBrush"] = new SolidColorBrush(dark ? Color.FromRgb(236, 236, 241) : Color.FromRgb(24, 24, 27));
        Splitter.Background = new SolidColorBrush(dark ? Color.FromRgb(22, 23, 26) : Color.FromRgb(225, 225, 230));
        if (Ui.CoreWebView2 != null) Ui.DefaultBackgroundColor = System.Drawing.Color.FromArgb(bg.R, bg.G, bg.B);
        if (_hwnd != null) Native.StyleTitleBar(_hwnd.Handle, dark, dark ? 0x0A0B0Cu : 0xF6F6F8u);
    }

    static void ApplyStartup()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run == null) return;
            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Synapse.exe");
            if (AppSettings.Shared.LaunchAtStartup) run.SetValue("Synapse", $"\"{exe}\" --background");
            else if (run.GetValue("Synapse") != null) run.DeleteValue("Synapse", false);
        }
        catch { }
    }

    void ApplyHotkey()
    {
        if (_hwnd == null) return;
        var want = AppSettings.Shared.GlobalHotkey;
        if (want && !_hotkey)
            _hotkey = Native.RegisterHotKey(_hwnd.Handle, 1, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE);
        else if (!want && _hotkey)
        {
            Native.UnregisterHotKey(_hwnd.Handle, 1);
            _hotkey = false;
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == 1)
        {
            if (IsActive && WindowState != WindowState.Minimized) Hide();
            else { ShowFromTray(); SendHost("focusComposer"); }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // MARK: Window lifecycle

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // bring to front even when another app has focus
        Topmost = false;
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Ui.Focus();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_quitting && AppSettings.Shared.CloseToTray && _app != null)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _tray?.Notify("Synapse is still running", "The local API and your websites stay available. Right-click the tray icon to quit.");
            }
            return;
        }
        base.OnClosing(e);
        if (!_quitting) Quit();
    }

    public void Quit()
    {
        if (_quitting && _app == null) return;
        _quitting = true;
        try { _app?.Shutdown(); } catch { }
        _app = null;
        if (_hotkey && _hwnd != null) Native.UnregisterHotKey(_hwnd.Handle, 1);
        _tray?.Dispose();
        _tray = null;
        Close();
        Application.Current.Shutdown();
    }
}
