namespace GlazeLauncher;

/// <summary>An alias that matched the query, and how well.</summary>
internal readonly record struct AliasHit(TextMatch Match, string Alias);

/// <summary>
/// The user's aliases: extra names for a result, stored in <c>settings.json</c> as alias → the result's
/// usage key (<c>app:…</c>, <c>folder:…</c>, <c>cmd:…</c>). An alias matches a query exactly or by prefix
/// only, so short aliases don't turn up for unrelated queries. Reads build a small immutable index from the
/// current settings on first use after a change, so there's no event wiring or locking.
/// </summary>
internal sealed class AliasBook(SettingsStore settings, Func<string, string?> describe)
{
    public const int MaxLength = 40;

    private sealed record Index(LauncherSettings Source, (string Alias, string Target)[] Entries,
        Dictionary<string, string> ByAlias, Dictionary<string, List<string>> ByTarget);

    private Index? _index;

    private Index Current
    {
        get
        {
            var source = settings.Current;
            var index = _index;
            if (index is not null && ReferenceEquals(index.Source, source)) return index;

            var byAlias = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (alias, target) in source.Aliases)
            {
                var key = Normalize(alias);
                if (key.Length > 0) byAlias[key] = target;
            }
            var byTarget = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (alias, target) in byAlias)
            {
                if (!byTarget.TryGetValue(target, out var list)) byTarget[target] = list = [];
                list.Add(alias);
            }
            foreach (var list in byTarget.Values) list.Sort(StringComparer.Ordinal);
            _index = index = new Index(source, byAlias.Select(e => (e.Key, e.Value)).ToArray(), byAlias, byTarget);
            return index;
        }
    }

    /// <summary>Best alias hit per target for a lower-case query: exact, or the query is a prefix of the alias.</summary>
    public IReadOnlyDictionary<string, AliasHit> Match(string query)
    {
        var entries = Current.Entries;
        if (query.Length == 0 || entries.Length == 0) return NoHits;

        Dictionary<string, AliasHit>? hits = null;
        foreach (var (alias, target) in entries)
        {
            TextMatch match;
            if (alias == query) match = new TextMatch(MatchQuality.Exact, 1000);
            else if (alias.StartsWith(query, StringComparison.Ordinal))
                match = new TextMatch(MatchQuality.Strong, 1000 - Math.Min(alias.Length - query.Length, 100));
            else continue;

            hits ??= new(StringComparer.Ordinal);
            if (!hits.TryGetValue(target, out var best) || match.CompareTo(best.Match) > 0) hits[target] = new AliasHit(match, alias);
        }
        return hits ?? NoHits;
    }

    public IReadOnlyList<string> AliasesFor(string target) => Current.ByTarget.GetValueOrDefault(target) ?? [];

    public string? TargetOf(string alias) => Current.ByAlias.GetValueOrDefault(Normalize(alias));

    /// <summary>A display name for a target (the result's title), or null when it isn't known (e.g. uninstalled).</summary>
    public string? Describe(string target) => describe(target);

    /// <summary>False while settings.json has a JSON error: saving is refused until it's fixed.</summary>
    public bool CanSave => !settings.IsBroken;

    /// <summary>Adds an alias, moving it if another result had it.</summary>
    public bool Add(string alias, string target)
    {
        alias = Normalize(alias);
        bool saved = settings.Update(s =>
        {
            RemoveKey(s, alias);
            s.Aliases[alias] = target;
        });
        Log.Info(saved ? $"alias '{alias}' → {target}" : $"alias '{alias}' not saved");
        return saved;
    }

    public bool Remove(string alias)
    {
        alias = Normalize(alias);
        bool saved = settings.Update(s => RemoveKey(s, alias));
        Log.Info(saved ? $"alias '{alias}' removed" : $"alias '{alias}' not removed");
        return saved;
    }

    /// <summary>Checks typed text. Returns an error message, or null when <paramref name="alias"/> is usable
    /// (it's empty when nothing was typed).</summary>
    public static string? Validate(string text, out string alias)
    {
        alias = Normalize(text);
        if (alias.Contains('@')) return "An alias can't contain @ (it's the workspace target)";
        if (alias.Length > MaxLength) return $"An alias can be at most {MaxLength} characters";
        return null;
    }

    /// <summary>Lower case, trimmed, inner whitespace collapsed to single spaces: the form queries are matched in.</summary>
    public static string Normalize(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    // A copy made by SettingsStore.Update may compare keys case-sensitively, and hand edits may use any case.
    private static void RemoveKey(LauncherSettings s, string alias)
    {
        foreach (var key in s.Aliases.Keys.Where(k => Normalize(k) == alias).ToList()) s.Aliases.Remove(key);
    }

    public static readonly IReadOnlyDictionary<string, AliasHit> NoHits = new Dictionary<string, AliasHit>();
}
