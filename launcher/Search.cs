using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace GlazeLauncher;

/// <summary>A built-in action (GlazeWM command, script, or launcher control).</summary>
internal sealed record LauncherCommand(string Title, string Subtitle, string Glyph, string? WmCommand, string? Internal = null)
{
    public string UsageKey => "cmd:" + Title;
}

internal sealed class ResultItem : INotifyPropertyChanged
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Glyph { get; init; } = "";
    public required string UsageKey { get; init; }
    /// <summary>What this result can do, built on demand. The first action is the default (Enter).</summary>
    public required Func<ActionContext, IReadOnlyList<ResultAction>> Actions { get; init; }
    public string? IconPath { get; init; }
    public Guid? IconFolderId { get; init; }
    public int? SubtitleMatchStart { get; init; }
    /// <summary>The user's alias that found this result; shown as a tag after the title, which isn't highlighted then.</summary>
    public string? MatchedAlias { get; init; }

    private int[]? _titleMatches;
    private int[]? _subtitleMatches;
    private int[]? _aliasMatches;

    public HighlightedText TitleText => new(Title, _titleMatches);
    public HighlightedText SubtitleText => new(Subtitle, _subtitleMatches, SubtitleMatchStart ?? 0);
    public HighlightedText AliasText => new(MatchedAlias ?? "", _aliasMatches);

    public void Highlight(string query)
    {
        if (MatchedAlias is { } alias)
            _aliasMatches = Fuzzy.FindPositions(query, alias);
        else if (SubtitleMatchStart is { } start)
            _subtitleMatches = Fuzzy.FindPositions(query, Subtitle[start..]);
        else
            _titleMatches = Fuzzy.FindPositions(query, Title);
    }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new(nameof(Icon)));
            PropertyChanged?.Invoke(this, new(nameof(GlyphVisibility)));
        }
    }

    public Visibility SubtitleVisibility => Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AliasVisibility => MatchedAlias is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Icon is null && Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Parsed query: free text plus an optional "@N" workspace target.</summary>
internal readonly record struct Query(string Text, string? WorkspaceToken)
{
    private static readonly Regex Target = new(@"(?:^|\s)@(\d{1,2})(?=\s|$)", RegexOptions.Compiled);

    public static Query Parse(string raw)
    {
        var m = Target.Match(raw);
        if (!m.Success) return new Query(raw.Trim(), null);
        var text = (raw[..m.Index] + " " + raw[(m.Index + m.Length)..]).Trim();
        return new Query(text, m.Groups[1].Value);
    }
}

internal enum MatchQuality { None, Subsequence, Substring, Initials, WordStart, Strong, Exact }

internal readonly record struct TextMatch(MatchQuality Quality, double Score) : IComparable<TextMatch>
{
    public bool Found => Quality != MatchQuality.None;

    public int CompareTo(TextMatch other) => Quality == other.Quality
        ? Score.CompareTo(other.Score)
        : Quality.CompareTo(other.Quality);
}

internal static class Fuzzy
{
    /// <summary>Matches text against a nonempty, lower-case query.</summary>
    public static TextMatch Match(string query, string text, Span<int> positions = default)
    {
        if (query.Length == 0 || query.Length > text.Length) return default;
        var t = text.ToLowerInvariant();

        int idx = t.IndexOf(query, StringComparison.Ordinal);
        if (idx == 0)
        {
            RecordRange(positions, idx);
            return new(query.Length == t.Length ? MatchQuality.Exact : MatchQuality.Strong,
                1000 - Math.Min(t.Length - query.Length, 100));
        }
        if (idx > 0)
        {
            bool wordStart = !char.IsLetterOrDigit(t[idx - 1]) || (char.IsUpper(text[idx]) && char.IsLower(text[idx - 1]));
            if (wordStart)
            {
                int end = idx + query.Length;
                bool wordEnd = end == t.Length || !char.IsLetterOrDigit(t[end]);
                RecordRange(positions, idx);
                return new(wordEnd ? MatchQuality.Strong : MatchQuality.WordStart,
                    (wordEnd ? 950 : 850) - Math.Min(idx, 50));
            }
        }

        if (MatchesInitials(query, text, positions)) return new(MatchQuality.Initials, 780);
        if (idx > 0)
        {
            RecordRange(positions, idx);
            return new(MatchQuality.Substring, 600 - Math.Min(idx, 100));
        }

        // Subsequence: every query char in order; reward consecutive runs and word starts.
        double score = 300;
        int ti = 0, run = 0;
        for (int i = 0; i < query.Length; i++)
        {
            int found = t.IndexOf(query[i], ti);
            if (found < 0) return default;
            if (!positions.IsEmpty) positions[i] = found;
            if (found == ti && ti > 0) { run++; score += 8 * run; } else { run = 0; score -= Math.Min(found - ti, 20); }
            if (found == 0 || !char.IsLetterOrDigit(t[found - 1])) score += 15;
            ti = found + 1;
        }
        return new(MatchQuality.Subsequence, Math.Max(score, 1));
    }

    public static int[]? FindPositions(string query, string text)
    {
        if (query.Length == 0 || query.Length > text.Length) return null;
        Span<int> positions = query.Length <= 128 ? stackalloc int[query.Length] : new int[query.Length];
        return Match(query, text, positions).Found ? positions.ToArray() : null;
    }

    private static void RecordRange(Span<int> positions, int start)
    {
        for (int i = 0; i < positions.Length; i++) positions[i] = start + i;
    }

    private static bool MatchesInitials(string query, string text, Span<int> positions)
    {
        if (query.Length > 64) return false;

        int matched = 0;
        bool previousSeparator = true;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool separator = !char.IsLetterOrDigit(c);
            bool camel = i > 0 && char.IsUpper(c) && char.IsLower(text[i - 1]);
            if (!separator && (previousSeparator || camel))
            {
                if (char.ToLowerInvariant(c) != query[matched]) return false;
                if (!positions.IsEmpty) positions[matched] = i;
                if (++matched == query.Length) return true;
            }
            previousSeparator = separator;
        }
        return false;
    }
}

/// <summary>Merges provider results with one ranking policy.</summary>
internal sealed class SearchEngine(IReadOnlyList<ISearchProvider> providers, UsageStore usage, AliasBook aliases)
{
    private const int MaxResults = 40;
    private const int MaxRecents = 6;

    public Task RefreshAsync(WmSnapshot snapshot) => Task.WhenAll(
        providers.OfType<IRefreshableSearchProvider>().Select(provider => provider.RefreshAsync(snapshot)));

    public List<ResultItem> Search(Query query, WmSnapshot wm)
    {
        query = query with { Text = query.Text.ToLowerInvariant() };
        var currentWs = wm.FocusedWorkspace?.Name;
        bool home = query.Text.Length == 0;
        var recents = home
            ? usage.Top(currentWs, int.MaxValue).ToDictionary(entry => entry.Key, entry => entry.Score)
            : [];
        var context = new SearchContext(query, wm, recents, home ? AliasBook.NoHits : aliases.Match(query.Text));

        var results = new List<(ProviderResult Result, double Score)>(64);
        var pinned = new List<ResultItem>();
        foreach (var provider in providers)
        {
            foreach (var result in provider.Search(context))
            {
                if (result.Pinned) pinned.Add(result.Item);
                else if (home || result.Match.Found)
                {
                    double score = home
                        ? recents.GetValueOrDefault(result.Item.UsageKey)
                        : result.Match.Score + provider.Priority + UsageBoost(result.Item.UsageKey, currentWs);
                    results.Add((result, score));
                }
            }
        }

        if (home)
        {
            var recentItems = results.Where(entry => entry.Result.TargetWindow is null && entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .Take(MaxRecents);
            var openItems = results.Where(entry => entry.Result.TargetWindow is not null)
                .OrderByDescending(entry => OpenPriority(entry.Result.TargetWindow, currentWs))
                .ThenBy(entry => entry.Result.TargetWindow?.WorkspaceName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Result.Item.Title, StringComparer.OrdinalIgnoreCase);
            return pinned.Concat(recentItems.Concat(openItems).Take(MaxResults).Select(entry => entry.Result.Item)).ToList();
        }

        var ranked = pinned;
        ranked.AddRange(results.OrderByDescending(entry => entry.Result.Match.Quality)
            .ThenByDescending(entry => OpenPriority(entry.Result.TargetWindow, currentWs))
            .ThenByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Result.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Result.Item.Id, StringComparer.Ordinal)
            .Take(MaxResults)
            .Select(entry => entry.Result.Item));
        foreach (var item in ranked) item.Highlight(query.Text);
        return ranked;
    }

    private static int OpenPriority(WmWindow? window, string? currentWorkspace)
    {
        if (window is null) return 0;
        return currentWorkspace is not null && window.WorkspaceName is not null && window.WorkspaceName != currentWorkspace
            ? 2 : 1;
    }

    private double UsageBoost(string key, string? workspace) =>
        Math.Min(usage.Global(key) * 4, 12) + Math.Min(usage.ForWorkspace(workspace, key) * 8, 24);
}

/// <summary>
/// Lower-case identifiers for a window (process name, packaged AUMID, window AUMID), cached
/// per HWND. Used to tell which app a window belongs to.
/// </summary>
internal sealed class WindowIdentity
{
    private readonly Dictionary<long, (HashSet<string> Ids, string? ExePath)> _cache = new();

    public HashSet<string> For(WmWindow w) => Get(w).Ids;

    public string? ExePath(WmWindow w) => Get(w).ExePath;

    private (HashSet<string> Ids, string? ExePath) Get(WmWindow w)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(w.Handle, out var hit)) return hit;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (w.ProcessName.Length > 0) ids.Add(w.ProcessName.ToLowerInvariant());
        var hwnd = new IntPtr(w.Handle);
        var (exe, packaged) = Native.GetProcessIdentity(hwnd);
        if (packaged is not null) ids.Add(packaged.ToLowerInvariant());
        if (Native.GetWindowAumid(hwnd) is { } aumid) ids.Add(aumid.ToLowerInvariant());

        var entry = (ids, exe);
        lock (_cache)
        {
            if (_cache.Count > 500) _cache.Clear();
            _cache[w.Handle] = entry;
        }
        return entry;
    }
}
