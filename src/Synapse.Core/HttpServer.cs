using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Synapse;

public sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public string Query { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = [];

    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? QueryParam(string name)
    {
        foreach (var part in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (Uri.UnescapeDataString(kv[0].Replace('+', ' ')) == name)
                return kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
        }
        return null;
    }
}

/// Writes a response back on a connection. Supports both full and streamed bodies.
public sealed class HttpResponder(Stream stream)
{
    readonly SemaphoreSlim _gate = new(1, 1);
    public bool Closed { get; private set; }
    public CancellationTokenSource Aborted { get; } = new();

    static string Reason(int s) => s switch
    {
        200 => "OK", 204 => "No Content", 206 => "Partial Content", 400 => "Bad Request", 401 => "Unauthorized",
        403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 413 => "Payload Too Large",
        500 => "Internal Server Error", 502 => "Bad Gateway", 503 => "Service Unavailable", _ => "OK",
    };

    public async Task Send(int status, Dictionary<string, string>? headers, byte[] body)
    {
        var h = headers != null ? new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        h["Content-Length"] = body.Length.ToString();
        h["Connection"] = "close";
        h.TryAdd("Access-Control-Allow-Origin", "*");
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\n");
        foreach (var (k, v) in h) head.Append(k).Append(": ").Append(v).Append("\r\n");
        head.Append("\r\n");
        await Write(Encoding.UTF8.GetBytes(head.ToString()));
        if (body.Length > 0) await Write(body);
        End();
    }

    public Task SendText(int status, string text, string contentType = "text/plain; charset=utf-8") =>
        Send(status, new() { ["Content-Type"] = contentType }, Encoding.UTF8.GetBytes(text));

    public Task SendJson(object obj, int status = 200) =>
        Send(status, new() { ["Content-Type"] = "application/json" }, Encoding.UTF8.GetBytes(obj is string s ? s : Json.Serialize(obj)));

    public Task BeginStream(int status, string contentType, Dictionary<string, string>? extra = null)
    {
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {contentType}\r\nCache-Control: no-cache\r\nConnection: close\r\nAccess-Control-Allow-Origin: *\r\n");
        if (extra != null) foreach (var (k, v) in extra) head.Append(k).Append(": ").Append(v).Append("\r\n");
        head.Append("\r\n");
        return Write(Encoding.UTF8.GetBytes(head.ToString()));
    }

    public async Task Write(byte[] data)
    {
        if (Closed) return;
        await _gate.WaitAsync();
        try
        {
            await stream.WriteAsync(data);
            await stream.FlushAsync();
        }
        catch
        {
            Closed = true;
            Aborted.Cancel();
        }
        finally { _gate.Release(); }
    }

    public Task Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    public void End()
    {
        if (Closed) return;
        Closed = true;
        try { stream.Flush(); } catch { }
        try { stream.Dispose(); } catch { }
        Aborted.Cancel();
    }
}

/// A small HTTP/1.1 server bound to 127.0.0.1: the local OpenAI-compatible API, hosted websites
/// and the dev host. One request per connection keeps it simple and works with every client.
public sealed class HttpServer(int port, Func<HttpRequest, HttpResponder, Task> handler)
{
    public int Port { get; private set; } = port;
    TcpListener? _listener;
    CancellationTokenSource? _cts;

    public void Start()
    {
        var l = new TcpListener(IPAddress.Loopback, Port);
        l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, OperatingSystem.IsWindows());
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        _listener = l;
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(l, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
    }

    async Task AcceptLoop(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await l.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => Serve(client), CancellationToken.None);
        }
    }

    async Task Serve(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        HttpRequest? req;
        try { req = await Read(stream); }
        catch { return; }
        if (req == null) return;
        var res = new HttpResponder(stream);
        try { await handler(req, res); }
        catch (Exception e)
        {
            if (!res.Closed) await res.SendJson(new { error = new { message = e.Message } }, 500);
        }
        finally { res.End(); }
    }

    /// Reads headers and the full body (Content-Length or chunked).
    static async Task<HttpRequest?> Read(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int headerEnd = -1;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (headerEnd < 0)
        {
            var n = await stream.ReadAsync(chunk, timeout.Token);
            if (n == 0) return null;
            buffer.Write(chunk, 0, n);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
            if (buffer.Length > 1_000_000 && headerEnd < 0) return null;
        }
        var all = buffer.GetBuffer();
        var head = Encoding.UTF8.GetString(all, 0, headerEnd);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray());

        var bodyStart = headerEnd + 4;
        var have = (int)buffer.Length - bodyStart;
        byte[] body;
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            var raw = new MemoryStream();
            raw.Write(all, bodyStart, have);
            while (!EndsChunked(raw))
            {
                var n = await stream.ReadAsync(chunk, timeout.Token);
                if (n == 0) break;
                raw.Write(chunk, 0, n);
                if (raw.Length > 200_000_000) return null;
            }
            body = Dechunk(raw.ToArray());
        }
        else
        {
            var length = headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var len) ? len : 0;
            if (length > 200_000_000) return null;
            body = new byte[length];
            var copied = Math.Min(have, length);
            Array.Copy(all, bodyStart, body, 0, copied);
            while (copied < length)
            {
                var n = await stream.ReadAsync(body.AsMemory(copied, length - copied), timeout.Token);
                if (n == 0) return null;
                copied += n;
            }
        }
        var target = first[1];
        var q = target.IndexOf('?');
        var path = q >= 0 ? target[..q] : target;
        try { path = Uri.UnescapeDataString(path); } catch { }
        return new HttpRequest
        {
            Method = first[0].ToUpperInvariant(),
            Path = path,
            Query = q >= 0 ? target[(q + 1)..] : "",
            Headers = headers,
            Body = body,
        };
    }

    static bool EndsChunked(MemoryStream s)
    {
        if (s.Length < 5) return false;
        var b = s.GetBuffer();
        var n = (int)s.Length;
        return b[n - 5] == '0' && b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n';
    }

    static byte[] Dechunk(byte[] raw)
    {
        var output = new MemoryStream();
        var i = 0;
        while (i < raw.Length)
        {
            var lineEnd = IndexOf(raw, raw.Length, "\r\n"u8, i);
            if (lineEnd < 0) break;
            var sizeText = Encoding.ASCII.GetString(raw, i, lineEnd - i).Split(';')[0].Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out var size) || size == 0) break;
            var start = lineEnd + 2;
            if (start + size > raw.Length) break;
            output.Write(raw, start, size);
            i = start + size + 2;
        }
        return output.ToArray();
    }

    static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> needle, int from = 0)
    {
        var span = data.AsSpan(from, length - from);
        var i = span.IndexOf(needle);
        return i < 0 ? -1 : i + from;
    }

    /// Serves files from a folder.
    public static Func<HttpRequest, HttpResponder, Task> StaticHandler(string root) => async (req, res) =>
    {
        var rel = req.Path;
        if (rel.EndsWith('/')) rel += "index.html";
        string file;
        try { file = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, rel.TrimStart('/').Replace('/', System.IO.Path.DirectorySeparatorChar))); }
        catch { await res.SendText(400, "Bad path"); return; }
        if (!Util.IsInside(file, root)) { await res.SendText(404, "Not found"); return; }
        if (Directory.Exists(file)) file = System.IO.Path.Combine(file, "index.html");
        if (!File.Exists(file))
        {
            await res.Send(404, new() { ["Content-Type"] = "text/html; charset=utf-8" },
                Encoding.UTF8.GetBytes($"<h1>404</h1><p>{WebUtility.HtmlEncode(req.Path)} not found</p>"));
            return;
        }
        var mime = Util.MimeType(file);
        if (mime.StartsWith("text/") || mime is "application/json" or "image/svg+xml") mime += "; charset=utf-8";
        await res.Send(200, new() { ["Content-Type"] = mime, ["Cache-Control"] = "no-cache" }, await File.ReadAllBytesAsync(file));
    };
}
