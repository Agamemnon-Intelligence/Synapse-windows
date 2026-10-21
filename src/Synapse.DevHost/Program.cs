using System.Text;
using System.Threading.Channels;
using Synapse;

// Runs the Synapse interface in any browser, backed by the real AppState, so the UI can be developed
// and tested without Windows. Usage: dotnet run --project src/Synapse.DevHost -- [--port 3939] [--home DIR]
var port = 3939;
string? home = null;
string? ui = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
    if (args[i] == "--home") home = args[i + 1];
    if (args[i] == "--ui") ui = args[i + 1];
}
if (home != null) Paths.Root = Path.GetFullPath(home);
ui ??= FindUi();

var main = new SingleThreadContext();
var clients = new List<Channel<string>>();
var gate = new object();
AppState app = null!;
BridgeHost bridge = null!;
await main.Run(() =>
{
    app = new AppState(new GenericPlatform());
    bridge = new BridgeHost(app, json =>
    {
        lock (gate) foreach (var c in clients) c.Writer.TryWrite(json);
    }, "/file?path=");
});

var server = new HttpServer(port, async (req, res) =>
{
    switch (req.Method, req.Path)
    {
        case ("GET", "/bridge/events"):
        {
            var ch = Channel.CreateUnbounded<string>();
            lock (gate) clients.Add(ch);
            try
            {
                await res.BeginStream(200, "text/event-stream");
                await res.Write("retry: 1000\n\n");
                while (!res.Closed)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(res.Aborted.Token);
                    cts.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        var msg = await ch.Reader.ReadAsync(cts.Token);
                        var sb = new StringBuilder();
                        foreach (var line in msg.Split('\n')) sb.Append("data: ").Append(line).Append('\n');
                        await res.Write(sb.Append('\n').ToString());
                    }
                    catch (OperationCanceledException) when (!res.Aborted.IsCancellationRequested)
                    {
                        await res.Write(": ping\n\n");   // keeps the connection open
                    }
                }
            }
            catch { }
            finally { lock (gate) clients.Remove(ch); }
            break;
        }
        case ("POST", "/bridge/send"):
        {
            var body = req.BodyText;
            main.Post(_ => bridge.Receive(body), null);
            await res.Send(204, null, []);
            break;
        }
        case ("GET", "/file"):
        {
            var path = req.QueryParam("path") ?? "";
            if (!File.Exists(path)) { await res.SendText(404, "Not found"); break; }
            await res.Send(200, new() { ["Content-Type"] = Util.MimeType(path), ["Cache-Control"] = "no-cache" }, await File.ReadAllBytesAsync(path));
            break;
        }
        default:
            await HttpServer.StaticHandler(ui)(req, res);
            break;
    }
});
server.Start();
Console.WriteLine($"Synapse dev host: http://127.0.0.1:{server.Port}/  (data: {Paths.Root}, ui: {ui})");
var quit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => quit.TrySetResult();
await quit.Task;
await main.Run(() => app.Shutdown());
server.Stop();

static string FindUi()
{
    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8 && dir != null; i++)
    {
        var candidate = Path.Combine(dir, "src", "Synapse.App", "ui");
        if (Directory.Exists(candidate)) return candidate;
        dir = Path.GetDirectoryName(dir);
    }
    return Path.Combine(Directory.GetCurrentDirectory(), "src", "Synapse.App", "ui");
}
