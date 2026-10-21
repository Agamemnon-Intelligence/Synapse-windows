namespace Synapse;

/// Hosts the static websites the AI (or you) build, each on its own localhost port.
public sealed class SiteManager(Action changed)
{
    public sealed record Site(string Name, string Folder, int? Port)
    {
        public string? Url => Port is { } p ? $"http://localhost:{p}/" : null;
    }

    public List<Site> Sites { get; private set; } = [];
    readonly Dictionary<string, HttpServer> _servers = new(StringComparer.OrdinalIgnoreCase);
    int _nextPort = 5500;

    public void Refresh()
    {
        var found = Directory.EnumerateDirectories(Paths.Sites).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);
        var updated = new List<Site>();
        foreach (var d in found)
        {
            var port = Sites.FirstOrDefault(s => s.Folder.Equals(d, StringComparison.OrdinalIgnoreCase))?.Port;
            updated.Add(new Site(Path.GetFileName(d), d, port));
        }
        // Keep running sites that live elsewhere (e.g. inside a project folder).
        foreach (var s in Sites.Where(s => s.Port != null && !updated.Any(u => u.Folder.Equals(s.Folder, StringComparison.OrdinalIgnoreCase))))
            updated.Add(s);
        Sites = updated;
        changed();
    }

    /// Writes files for a site and starts serving it.
    public Site Create(string name, List<(string Path, string Content)> files, string? parent = null)
    {
        var folder = Path.Combine(parent ?? Paths.Sites, Util.Slug(name));
        foreach (var (p, content) in files)
        {
            var rel = p.Trim().TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.GetFullPath(Path.Combine(folder, rel));
            if (!Util.IsInside(dest, folder)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, content, new System.Text.UTF8Encoding(false));
        }
        Refresh();
        return Start(folder);
    }

    public Site Start(string folder)
    {
        if (Sites.FirstOrDefault(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase) && s.Port != null) is { } existing) return existing;
        Exception? last = null;
        for (var i = 0; i < 50; i++)
        {
            var port = _nextPort++;
            var server = new HttpServer(port, HttpServer.StaticHandler(folder));
            try
            {
                server.Start();
                _servers[folder] = server;
                var site = new Site(Path.GetFileName(folder), folder, port);
                var idx = Sites.FindIndex(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0) Sites[idx] = site; else Sites.Add(site);
                changed();
                return site;
            }
            catch (Exception e) { last = e; }
        }
        throw last ?? new ApiException(0, "No free port");
    }

    public void Stop(string folder)
    {
        if (_servers.Remove(folder, out var s)) s.Stop();
        var idx = Sites.FindIndex(x => x.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) Sites[idx] = Sites[idx] with { Port = null };
        changed();
    }

    public void StopAll()
    {
        foreach (var s in _servers.Values) s.Stop();
        _servers.Clear();
    }

    public void Delete(string folder, IPlatform platform)
    {
        Stop(folder);
        platform.Trash(folder);
        Sites.RemoveAll(s => s.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));
        Refresh();
    }
}
