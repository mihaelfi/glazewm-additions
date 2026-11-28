namespace GlazeLauncher;

/// <summary>
/// Files and folders from the GlazeIndex service. Search never waits on the pipe: a query is sent in the background,
/// results arrive a few ms later and <see cref="Changed"/> asks the window to re-run the search, which then finds them
/// in the cache. Until then the previous query's results that still match are shown, so the list doesn't flicker.
/// Only the newest query is ever in flight. On the home screen, recently opened files and folders are listed.
/// </summary>
internal sealed class FileSearchProvider(IndexClient client, ShellIndex shell) : ISearchProvider
{
    public const string KeyPrefix = "path:";
    private const int Limit = 12;
    private const int MinLetters = 3;
    private const int MaxRecents = 8;

    private readonly Lock _gate = new();
    private string? _wanted;
    private bool _running;
    private string? _cachedQuery;
    private IReadOnlyList<IndexHit> _cached = [];

    /// <summary>Raised on a background thread when results for the latest query arrived.</summary>
    public event Action? Changed;

    public int Priority => 0;

    public IEnumerable<ProviderResult> Search(SearchContext context)
    {
        if (context.IsHome) return Recents(context);
        if (context.Query.WorkspaceToken is not null) return [];
        var text = context.Query.Text;
        if (text.Count(c => !char.IsWhiteSpace(c)) < MinLetters) return [];

        IReadOnlyList<IndexHit> hits;
        lock (_gate)
        {
            if (_cachedQuery == text) hits = _cached;
            else
            {
                Request(text);
                var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                hits = _cached.Where(h => terms.All(t => h.Path.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
            }
        }
        var known = shell.Folders.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return hits.Where(h => !known.Contains(h.Path)).Select(ToResult).ToList();
    }

    private void Request(string text)
    {
        if (_wanted == text) return;
        _wanted = text;
        if (_running) return;
        _running = true;
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            string query;
            lock (_gate) query = _wanted!;
            var reply = await client.SearchAsync(query, Limit);
            bool latest;
            lock (_gate)
            {
                // Unreachable: cache "nothing" for this query, so the re-search doesn't ask again.
                _cachedQuery = query;
                _cached = reply?.Hits ?? [];
                latest = _wanted == query;
                if (latest) _running = false;
            }
            if (latest)
            {
                if (reply is { Hits.Count: > 0 }) Changed?.Invoke();
                return;
            }
        }
    }

    private ProviderResult ToResult(IndexHit hit)
    {
        // Below every app that matches by name or word start; above loose (subsequence) matches.
        var match = new TextMatch(MatchQuality.Substring, 300 + Math.Clamp(hit.Score, 0, 1200) / 4.0);
        return new ProviderResult(Item(hit.Path, hit.Name, hit.Folder, hit.Size, hit.Highlight), match);
    }

    private ResultItem Item(string path, string name, bool folder, long? size, (int Start, int Length)? highlight)
    {
        var parent = Path.GetDirectoryName(path) ?? path;
        var sizeText = size is { } bytes ? Format.Size(bytes) + " · " : "";
        return new ResultItem
        {
            Id = KeyPrefix + path,
            Title = name.Length > 0 ? name : path,
            Subtitle = $"{(folder ? "Folder" : "File")} · {sizeText}{parent}",
            Glyph = folder ? "" : "",
            IconPath = path,
            UsageKey = KeyPrefix + path,
            FixedTitleMatches = highlight is { Length: > 0 } h && h.Start + h.Length <= name.Length
                ? Enumerable.Range(h.Start, h.Length).ToArray() : null,
            Actions = folder ? _ => ActionKits.Folder(path) : _ => ActionKits.File(path),
        };
    }

    /// <summary>Recently opened files and folders that still exist, for the home screen.</summary>
    private IEnumerable<ProviderResult> Recents(SearchContext context)
    {
        var list = new List<ProviderResult>();
        foreach (var (key, _) in context.Recents.Where(r => r.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                     .OrderByDescending(r => r.Value).Take(MaxRecents))
        {
            var path = key[KeyPrefix.Length..];
            bool folder = Directory.Exists(path);
            if (!folder && !File.Exists(path)) continue;
            list.Add(new ProviderResult(Item(path, Path.GetFileName(path.TrimEnd('\\')), folder, null, null), default));
        }
        return list;
    }
}

internal static class Format
{
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        < 1024L * 1024 * 1024 * 1024 => $"{bytes / 1073741824.0:0.#} GB",
        _ => $"{bytes / 1099511627776.0:0.##} TB",
    };
}
