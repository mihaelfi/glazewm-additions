using System.Text.Json;

namespace GlazeLauncher;

/// <summary>
/// Frecency per item, globally and per workspace, stored in %LOCALAPPDATA%\GlazeLauncher\usage.json.
/// Each use adds 1 to a score that halves every <see cref="HalfLifeDays"/> days.
/// </summary>
internal sealed class UsageStore
{
    private const double HalfLifeDays = 10;
    private static readonly string FilePath = Paths.File("usage.json");

    private sealed class Entry
    {
        public double Score { get; set; }
        public DateTime Last { get; set; }
    }

    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private Timer? _saveTimer;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) { Log.Error("usage load", e); }
    }

    public void Record(string key, string? workspace)
    {
        var now = DateTime.UtcNow;
        Bump(key, now);
        if (workspace is not null) Bump(WsKey(workspace, key), now);
        ScheduleSave();
    }

    public double Global(string key) => Get(key);
    public double ForWorkspace(string? workspace, string key) => workspace is null ? 0 : Get(WsKey(workspace, key));

    /// <summary>Most-used keys for a workspace (falling back to global), best first.</summary>
    public IEnumerable<(string Key, double Score)> Top(string? workspace, int count)
    {
        var prefix = workspace is null ? null : $"ws:{workspace}|";
        return _entries.Keys
            .Where(k => !k.StartsWith("ws:", StringComparison.Ordinal))
            .Select(k => (Key: k, Score: Get(k) * 0.4 + (prefix is null ? 0 : Get(prefix + k))))
            .Where(x => x.Score > 0.05)
            .OrderByDescending(x => x.Score)
            .Take(count);
    }

    private static string WsKey(string workspace, string key) => $"ws:{workspace}|{key}";

    private double Get(string key)
    {
        if (!_entries.TryGetValue(key, out var e)) return 0;
        var days = (DateTime.UtcNow - e.Last).TotalDays;
        return e.Score * Math.Pow(0.5, days / HalfLifeDays);
    }

    private void Bump(string key, DateTime now)
    {
        var current = Get(key);
        lock (_entries) _entries[key] = new Entry { Score = current + 1, Last = now };
    }

    private void ScheduleSave()
    {
        _saveTimer ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
        _saveTimer.Change(1500, Timeout.Infinite);
    }

    public void Save()
    {
        try
        {
            Dictionary<string, Entry> snapshot;
            lock (_entries) snapshot = new(_entries);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) { Log.Error("usage save", e); }
    }
}
