using System.Windows;
using Synapse;
using Forms = System.Windows.Forms;

namespace Synapse.App;

/// The notification-area icon: keeps Synapse (and its local API) running after the window closes.
sealed class Tray : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly MainWindow _window;

    public Tray(MainWindow window, AppState app)
    {
        _window = window;
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/synapse.ico"))?.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = stream != null ? new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application,
            Text = "Synapse",
            Visible = true,
        };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Synapse", null, (_, _) => window.ShowFromTray());
        menu.Items.Add("New Chat", null, (_, _) => { window.ShowFromTray(); window.SendHost("newChat"); });
        var unload = new Forms.ToolStripMenuItem("Unload Local Model", null, (_, _) => app.Engine.Stop());
        menu.Items.Add(unload);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Synapse", null, (_, _) => window.Quit());
        menu.Opening += (_, _) =>
        {
            unload.Enabled = app.Engine.State == EngineState.Running;
            unload.Text = app.Engine.State == EngineState.Running ? $"Unload {app.Engine.StatusText}" : "Unload Local Model";
        };
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) window.ShowFromTray(); };
        _icon.BalloonTipClicked += (_, _) => window.ShowFromTray();
    }

    /// Windows shows these as toast notifications.
    public void Notify(string title, string body)
    {
        try { _icon.ShowBalloonTip(5000, title, body.Length > 0 ? body : " ", Forms.ToolTipIcon.None); } catch { }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
