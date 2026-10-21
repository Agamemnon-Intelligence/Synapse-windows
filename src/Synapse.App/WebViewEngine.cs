using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Synapse;

namespace Synapse.App;

/// The browser panel's WebView2, as the surface BrowserAgent drives.
sealed class WebViewEngine : IWebEngine
{
    readonly WebView2 _view;
    bool _loading;

    public event Action? StateChanged;

    public WebViewEngine(WebView2 view)
    {
        _view = view;
        var core = view.CoreWebView2;
        core.NavigationStarting += (_, _) => { _loading = true; StateChanged?.Invoke(); };
        core.NavigationCompleted += (_, _) => { _loading = false; StateChanged?.Invoke(); };
        core.SourceChanged += (_, _) => StateChanged?.Invoke();
        core.HistoryChanged += (_, _) => StateChanged?.Invoke();
        core.DocumentTitleChanged += (_, _) => StateChanged?.Invoke();
        // Open target=_blank links in the same view.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? _)) core.Navigate(e.Uri);
        };
    }

    CoreWebView2 Core => _view.CoreWebView2;

    public async Task<string?> Eval(string script)
    {
        try { return await Core.ExecuteScriptAsync(script); }
        catch { return null; }
    }

    public void Navigate(string url)
    {
        try { Core.Navigate(url); } catch { }
    }

    public void GoBack() { if (Core.CanGoBack) Core.GoBack(); }
    public void GoForward() { if (Core.CanGoForward) Core.GoForward(); }
    public void Reload() => Core.Reload();
    public void StopLoading() => Core.Stop();
    public bool IsLoading => _loading;
    public bool CanGoBack => Core.CanGoBack;
    public bool CanGoForward => Core.CanGoForward;
    public string Url => Core.Source == "about:blank" ? "" : Core.Source;
    public string Title => Core.DocumentTitle;

    public async Task<byte[]?> CapturePng()
    {
        try
        {
            using var ms = new MemoryStream();
            await Core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
