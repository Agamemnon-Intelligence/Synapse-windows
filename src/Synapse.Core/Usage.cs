namespace Synapse;

public sealed class DayUsage
{
    public int Requests { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
}

public sealed class ProviderUsage
{
    /// "yyyy-MM-dd" in Pacific time, matching Google's daily reset.
    public Dictionary<string, DayUsage> Days { get; set; } = [];
    /// Request times in the last minute.
    public List<DateTime> MinuteStamps { get; set; } = [];
    public Dictionary<string, int> PerModelToday { get; set; } = [];
    public string PerModelDay { get; set; } = "";
    public RateLimitInfo? RateLimit { get; set; }
    public string? LastLimitError { get; set; }
    public DateTime? LastLimitErrorAt { get; set; }
}

public sealed class UsageTracker
{
    public Dictionary<string, ProviderUsage> Providers { get; private set; } = [];
    readonly Action _changed;
    DateTime _lastSave;
    bool _dirty;

    public UsageTracker(Action changed)
    {
        _changed = changed;
        Providers = Json.ReadFile<Dictionary<string, ProviderUsage>>(Paths.UsageFile) ?? [];
    }

    static readonly TimeZoneInfo Pacific = FindPacific();

    static TimeZoneInfo FindPacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        return TimeZoneInfo.Utc;
    }

    public static string DayKey(DateTime? utc = null) =>
        TimeZoneInfo.ConvertTimeFromUtc(utc ?? DateTime.UtcNow, Pacific).ToString("yyyy-MM-dd");

    public ProviderUsage Usage(ProviderId p) => Providers.TryGetValue(p.Key(), out var u) ? u : new ProviderUsage();
    public DayUsage Today(ProviderId p) => Usage(p).Days.TryGetValue(DayKey(), out var d) ? d : new DayUsage();
    public int RequestsLastMinute(ProviderId p) => Usage(p).MinuteStamps.Count(t => t > DateTime.UtcNow.AddSeconds(-60));

    ProviderUsage Get(ProviderId p)
    {
        if (!Providers.TryGetValue(p.Key(), out var u)) Providers[p.Key()] = u = new ProviderUsage();
        return u;
    }

    public void RecordRequest(ProviderId p, string model)
    {
        var u = Get(p);
        var key = DayKey();
        if (!u.Days.TryGetValue(key, out var day)) u.Days[key] = day = new DayUsage();
        day.Requests++;
        u.MinuteStamps = [.. u.MinuteStamps.Where(t => t > DateTime.UtcNow.AddSeconds(-60)), DateTime.UtcNow];
        if (u.PerModelDay != key) { u.PerModelToday = []; u.PerModelDay = key; }
        u.PerModelToday[model] = u.PerModelToday.GetValueOrDefault(model) + 1;
        // Keep 60 days of history.
        foreach (var k in u.Days.Keys.OrderBy(k => k).SkipLast(60).ToList()) u.Days.Remove(k);
        Save();
    }

    public void RecordTokens(ProviderId p, TokenUsage t)
    {
        var u = Get(p);
        var key = DayKey();
        if (!u.Days.TryGetValue(key, out var day)) u.Days[key] = day = new DayUsage();
        day.PromptTokens += t.Prompt;
        day.CompletionTokens += t.Completion;
        Save();
    }

    public void RecordRateLimit(ProviderId p, RateLimitInfo r)
    {
        Get(p).RateLimit = r;
        Save();
    }

    public void RecordLimitError(ProviderId p, string message)
    {
        var u = Get(p);
        u.LastLimitError = message;
        u.LastLimitErrorAt = DateTime.UtcNow;
        Save();
    }

    public void Reset(ProviderId p)
    {
        Providers.Remove(p.Key());
        Save(force: true);
    }

    void Save(bool force = false)
    {
        _dirty = true;
        _changed();
        if (!force && (DateTime.UtcNow - _lastSave).TotalSeconds < 2) return;
        Flush();
    }

    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        _lastSave = DateTime.UtcNow;
        try { Json.WriteFile(Paths.UsageFile, Providers); } catch { }
    }
}
