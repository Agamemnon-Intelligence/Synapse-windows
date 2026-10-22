using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Synapse;

namespace Synapse.App;

public partial class App : Application
{
    const string MutexName = @"Local\Synapse.SingleInstance";
    const string ActivateName = @"Local\Synapse.Activate";
    /// Set by the installer (or `Synapse.exe --quit`) so a running copy saves and exits before an update.
    const string QuitName = @"Local\Synapse.Quit";
    Mutex? _mutex;
    EventWaitHandle? _activate;
    EventWaitHandle? _quit;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // One Synapse per user: a second launch just brings the first one forward.
        _mutex = new Mutex(true, MutexName, out var first);
        var quit = e.Args.Contains("--quit");
        if (!first)
        {
            try { EventWaitHandle.OpenExisting(quit ? QuitName : ActivateName).Set(); } catch { }
            Shutdown();
            return;
        }
        if (quit)
        {
            Shutdown();
            return;
        }
        Native.SetCurrentProcessExplicitAppUserModelID("Synapse.Windows");
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log(a.ExceptionObject?.ToString() ?? "");
        TaskScheduler.UnobservedTaskException += (_, a) => { Log(a.Exception.ToString()); a.SetObserved(); };

        var window = new MainWindow();
        MainWindow = window;
        var background = e.Args.Contains("--background");
        if (background) window.Start();   // tray only; the window appears on first open
        else window.Show();

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        var thread = new Thread(() =>
        {
            while (_activate.WaitOne()) Dispatcher.InvokeAsync(window.ShowFromTray);
        }) { IsBackground = true, Name = "Synapse activation" };
        thread.Start();

        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        new Thread(() =>
        {
            if (_quit.WaitOne()) Dispatcher.InvokeAsync(window.Quit);
        }) { IsBackground = true, Name = "Synapse quit" }.Start();
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception.ToString());
        // Keep running: one failed handler shouldn't take the app (and the API) down.
        e.Handled = true;
    }

    static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(Paths.Logs, "errors.log"), $"[{DateTime.Now:O}] {text}\n\n"); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
