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

/// <summary>What a provider returns; decides its section and which scopes show it.</summary>
internal enum ResultKind { App, Window, Tab, File, Command, Workspace }

/// <summary>The chip under the search box: everything, or one kind of result.</summary>
internal enum SearchScope { All, Apps, Windows, Tabs, Files, Commands }

internal static class Scopes
{
    /// <summary>The chips in order, with the prefix that selects each (PowerToys Run's keywords where they fit:
    /// <c>.</c> programs, <c>&lt;</c> Window Walker, <c>?</c> file search; <c>&gt;</c> commands as in VS Code; <c>^</c> tabs,
    /// which PowerToys doesn't have).</summary>
    public static readonly (SearchScope Scope, string Name, char? Prefix, string Placeholder)[] List =
    [
        (SearchScope.All, "All", null, "Apps, windows, files, commands…   @3 → workspace 3"),
        (SearchScope.Apps, "Apps", '.', "Search apps…"),
        (SearchScope.Windows, "Windows", '<', "Search open windows and browser tabs…"),
        (SearchScope.Tabs, "Tabs", '^', "Search browser tabs…"),
        (SearchScope.Files, "Files", '?', "Search files and folders…"),
        (SearchScope.Commands, "Commands", '>', "Search commands…"),
    ];

    public static SearchScope? ForPrefix(char c) =>
        List.FirstOrDefault(s => s.Prefix == c) is { Prefix: not null } hit ? hit.Scope : null;

    public static string Placeholder(SearchScope scope) => List.First(s => s.Scope == scope).Placeholder;

    /// <summary>The Windows scope (<c>&lt;</c>) covers everything already open: windows and browser tabs.</summary>
    public static bool Includes(SearchScope scope, ResultKind kind) => scope switch
    {
        SearchScope.All => true,
        SearchScope.Apps => kind == ResultKind.App,
        SearchScope.Windows => kind is ResultKind.Window or ResultKind.Tab,
        SearchScope.Tabs => kind == ResultKind.Tab,
        SearchScope.Files => kind == ResultKind.File,
        SearchScope.Commands => kind == ResultKind.Command,
        _ => false,
    };
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
    /// <summary>Title positions the provider already knows (GlazeIndex matches words anywhere in a path, which the
    /// local matcher can't reproduce); used instead of matching the query again.</summary>
    public int[]? FixedTitleMatches { get; init; }
    /// <summary>A folder → can open inside the launcher (folder browsing).</summary>
    public string? BrowsePath { get; init; }
    /// <summary>A section header in the mixed results: not selectable, no actions.</summary>
    public bool IsHeader { get; init; }
    /// <summary>A "Show all …" row: Enter switches to this scope.</summary>
    public SearchScope? ScopeLink { get; init; }

    public Visibility RowVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;
    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;

    public static ResultItem Header(string title, string hint = "") => new()
    {
        Id = "header:" + title,
        Title = title,
        Subtitle = hint,
        UsageKey = "",
        IsHeader = true,
        Actions = _ => [],
    };

    private int[]? _titleMatches;
    private int[]? _subtitleMatches;
    private int[]? _aliasMatches;

    public HighlightedText TitleText => new(Title, _titleMatches);
    public HighlightedText SubtitleText => new(Subtitle, _subtitleMatches, SubtitleMatchStart ?? 0);
    public HighlightedText AliasText => new(MatchedAlias ?? "", _aliasMatches);

    public void Highlight(string query)
    {
        if (FixedTitleMatches is { } fixedMatches)
            _titleMatches = fixedMatches;
        else if (MatchedAlias is { } alias)
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

    private const int MaxListed = 400;

    /// <summary>Sections of the mixed results, in order, with how many rows each shows before "Show all".</summary>
    private static readonly (ResultKind Kind, string Name, int Cap, SearchScope Scope)[] Sections =
    [
        (ResultKind.Window, "Windows", 5, SearchScope.Windows),
        (ResultKind.App, "Apps", 4, SearchScope.Apps),
        (ResultKind.Tab, "Browser tabs", 3, SearchScope.Tabs),
        (ResultKind.Command, "Commands", 3, SearchScope.Commands),
        (ResultKind.File, "Files and folders", 5, SearchScope.Files),
    ];

    /// <summary>
    /// The rows for a query. In the All scope with text, the best match comes first as the top hit, then a section per
    /// kind of result with a few rows each and a "Show all" row that opens that scope. In one scope, everything matching
    /// is ranked in one list, and an empty query lists all of it. Header rows (<see cref="ResultItem.IsHeader"/>) are
    /// part of the list.
    /// </summary>
    public List<ResultItem> Search(Query query, WmSnapshot wm, SearchScope scope = SearchScope.All)
    {
        query = query with { Text = query.Text.ToLowerInvariant() };
        var currentWs = wm.FocusedWorkspace?.Name;
        bool home = query.Text.Length == 0;
        var recents = home
            ? usage.Top(currentWs, int.MaxValue).ToDictionary(entry => entry.Key, entry => entry.Score)
            : [];
        var context = new SearchContext(query, wm, recents, home ? AliasBook.NoHits : aliases.Match(query.Text), scope);

        var results = new List<(ProviderResult Result, double Score, ResultKind Kind)>(64);
        var pinned = new List<ResultItem>();
        foreach (var provider in providers)
        {
            if (!Scopes.Includes(scope, provider.Kind)) continue;
            foreach (var result in provider.Search(context))
            {
                if (result.Pinned) pinned.Add(result.Item);
                else if (home || result.Match.Found)
                {
                    double score = home
                        ? recents.GetValueOrDefault(result.Item.UsageKey)
                        : result.Match.Score + provider.Priority + UsageBoost(result.Item.UsageKey, currentWs);
                    results.Add((result, score, provider.Kind));
                }
            }
        }

        if (home && scope == SearchScope.All)
        {
            var recentItems = results.Where(entry => entry.Result.TargetWindow is null && entry.Score > 0)
                .OrderByDescending(entry => entry.Score)
                .Take(MaxRecents).Select(entry => entry.Result.Item).ToList();
            var openItems = results.Where(entry => entry.Result.TargetWindow is not null)
                .OrderByDescending(entry => OpenPriority(entry.Result.TargetWindow, currentWs))
                .ThenBy(entry => entry.Result.TargetWindow?.WorkspaceName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Result.Item.Title, StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults).Select(entry => entry.Result.Item).ToList();
            var rows = pinned;
            if (recentItems.Count > 0) rows.AddRange([ResultItem.Header("Recent"), .. recentItems]);
            if (openItems.Count > 0) rows.AddRange([ResultItem.Header("Open windows"), .. openItems]);
            return rows;
        }

        if (home)
        {
            // One scope, nothing typed: list all of it (open things in workspace order, the rest recent first, then A–Z).
            var listed = pinned;
            listed.AddRange(results
                .OrderByDescending(entry => OpenPriority(entry.Result.TargetWindow, currentWs))
                .ThenByDescending(entry => entry.Score)
                .ThenBy(entry => entry.Result.TargetWindow?.WorkspaceName, StringComparer.Ordinal)
                .ThenBy(entry => entry.Result.Item.Title, StringComparer.OrdinalIgnoreCase)
                .Take(MaxListed)
                .Select(entry => entry.Result.Item));
            return listed;
        }

        var ordered = results.OrderByDescending(entry => entry.Result.Match.Quality)
            .ThenByDescending(entry => OpenPriority(entry.Result.TargetWindow, currentWs))
            .ThenByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Result.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Result.Item.Id, StringComparer.Ordinal)
            .ToList();

        var ranked = pinned;
        if (scope != SearchScope.All)
        {
            ranked.AddRange(ordered.Take(MaxListed).Select(entry => entry.Result.Item));
        }
        else if (ordered.Count > 0)
        {
            // The top hit, then the sections without it.
            var top = ordered[0];
            ranked.Add(ResultItem.Header("Top hit"));
            ranked.Add(top.Result.Item);
            foreach (var (kind, name, cap, sectionScope) in Sections)
            {
                var inSection = ordered.Where(entry => entry.Kind == kind && !ReferenceEquals(entry.Result.Item, top.Result.Item)).ToList();
                if (inSection.Count == 0) continue;
                var prefix = Scopes.List.First(s => s.Scope == sectionScope).Prefix;
                ranked.Add(ResultItem.Header(name, prefix is { } p ? $"{p} for all" : "Ctrl+Tab for all"));
                ranked.AddRange(inSection.Take(cap).Select(entry => entry.Result.Item));
                // Files come 12 at a time from the service, so their total isn't known here.
                int total = inSection.Count + (kind == top.Kind ? 1 : 0);
                if (inSection.Count > cap || kind == ResultKind.File && inSection.Count >= cap)
                    ranked.Add(ShowAll(kind == ResultKind.File ? "Show more files and folders" : $"Show all {total} {name.ToLowerInvariant()}", sectionScope));
            }
        }
        foreach (var item in ranked) if (!item.IsHeader) item.Highlight(query.Text);
        return ranked;
    }

    private static ResultItem ShowAll(string title, SearchScope scope) => new()
    {
        Id = "showall:" + scope,
        Title = title,
        Glyph = "", // chevron right
        UsageKey = "",
        ScopeLink = scope,
        Actions = _ => [],
    };

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
