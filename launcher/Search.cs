using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace GlazeLauncher;

public enum ResultKind { App, Window, Command }

/// <summary>A built-in action (GlazeWM command, script, or launcher control).</summary>
internal sealed record LauncherCommand(string Title, string Subtitle, string Glyph, string? WmCommand, string? Internal = null)
{
    public string UsageKey => "cmd:" + Title;
}

internal sealed class ResultItem : INotifyPropertyChanged
{
    public required ResultKind Kind { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Glyph { get; init; } = "";
    public required string UsageKey { get; init; }
    public double Score { get; init; }

    public AppEntry? App { get; init; }
    public WmWindow? Window { get; init; }
    public LauncherCommand? Command { get; init; }
    /// <summary>For apps: windows of this app that are open right now.</summary>
    public IReadOnlyList<WmWindow> RunningWindows { get; init; } = [];

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); }
    }

    public Visibility SubtitleVisibility => Subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

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

internal static class Fuzzy
{
    /// <summary>Scores <paramref name="text"/> against a lower-case query; negative means no match.
    /// Prefix &gt; word-start &gt; initials ("vsc" → Visual Studio Code) &gt; substring &gt; subsequence.</summary>
    public static double Score(string query, string text)
    {
        if (query.Length == 0) return 0;
        if (text.Length == 0) return -1;
        var t = text.ToLowerInvariant();

        if (t.StartsWith(query, StringComparison.Ordinal)) return 1000 - Math.Min(t.Length - query.Length, 100);

        int idx = t.IndexOf(query, StringComparison.Ordinal);
        if (idx > 0)
        {
            bool wordStart = !char.IsLetterOrDigit(t[idx - 1]) || (char.IsUpper(text[idx]) && char.IsLower(text[idx - 1]));
            int end = idx + query.Length;
            bool wordEnd = end == t.Length || !char.IsLetterOrDigit(t[end]);
            if (wordStart && wordEnd) return 950 - Math.Min(idx, 50); // a whole word: "chrome" in "Google Chrome"
            if (wordStart) return 850 - Math.Min(idx, 50);
        }

        if (Initials(text).StartsWith(query, StringComparison.Ordinal)) return 780;
        if (idx > 0) return 600 - Math.Min(idx, 100);

        // Subsequence: every query char in order; reward consecutive runs and word starts.
        double score = 300;
        int ti = 0, run = 0;
        foreach (var qc in query)
        {
            int found = t.IndexOf(qc, ti);
            if (found < 0) return -1;
            if (found == ti && ti > 0) { run++; score += 8 * run; } else { run = 0; score -= Math.Min(found - ti, 20); }
            if (found == 0 || !char.IsLetterOrDigit(t[found - 1])) score += 15;
            ti = found + 1;
        }
        return Math.Max(score, 1);
    }

    private static string Initials(string text)
    {
        Span<char> buf = stackalloc char[Math.Min(text.Length, 64)];
        int n = 0;
        bool prevSep = true;
        for (int i = 0; i < text.Length && n < buf.Length; i++)
        {
            char c = text[i];
            bool sep = !char.IsLetterOrDigit(c);
            bool camel = i > 0 && char.IsUpper(c) && char.IsLower(text[i - 1]);
            if (!sep && (prevSep || camel)) buf[n++] = char.ToLowerInvariant(c);
            prevSep = sep;
        }
        return new string(buf[..n]);
    }
}

/// <summary>Builds the ranked result list from apps, open windows and commands.</summary>
internal sealed class SearchEngine(AppIndex apps, UsageStore usage, WindowIdentity identity)
{
    public const int MaxResults = 40;

    public static readonly IReadOnlyList<LauncherCommand> Commands =
    [
        new("Toggle tiling direction", "GlazeWM", "", "toggle-tiling-direction"),
        new("Toggle floating", "GlazeWM · focused window", "", "toggle-floating --centered"),
        new("Toggle fullscreen", "GlazeWM · focused window", "", "toggle-fullscreen"),
        new("Redraw windows", "GlazeWM", "", "wm-redraw"),
        new("Reload GlazeWM config", "GlazeWM", "", "wm-reload-config"),
        new("Pause / resume GlazeWM", "GlazeWM", "", "wm-toggle-pause"),
        new("Sync workspaces to monitors", "workspace.ps1 -Action Sync (Ctrl+Alt+R)", "",
            "shell-exec --hide-window powershell.exe -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -File %USERPROFILE%/.glzr/glazewm/scripts/workspace.ps1 -Action Sync"),
        new("Re-index apps", "Launcher", "", null, "reindex"),
        new("Quit launcher", "Launcher", "", null, "quit"),
    ];

    public List<ResultItem> Search(Query query, WmSnapshot wm)
    {
        var q = query.Text.ToLowerInvariant();
        var currentWs = wm.FocusedWorkspace?.Name;
        var results = new List<ResultItem>(64);

        // Index running windows by app so app rows can show "open on 12" and focus instead of relaunching.
        var windowsByApp = new Dictionary<AppEntry, List<WmWindow>>();
        var claimed = new HashSet<string>();
        foreach (var w in wm.Windows)
        {
            var ids = identity.For(w);
            foreach (var app in apps.Apps)
            {
                if (!app.MatchIds.Overlaps(ids)) continue;
                if (!windowsByApp.TryGetValue(app, out var list)) windowsByApp[app] = list = [];
                list.Add(w);
                claimed.Add(w.Id);
                break;
            }
        }

        if (q.Length == 0)
        {
            AddRecents(results, wm, currentWs, windowsByApp);
            foreach (var w in OrderedWindows(wm)) results.Add(WindowItem(w, wm, 0));
            return results.Take(MaxResults).ToList();
        }

        foreach (var app in apps.Apps)
        {
            double s = Fuzzy.Score(q, app.Name);
            if (s < 0) continue;
            // "chrome" should find Google Chrome (exe chrome.exe) before Chrome Remote Desktop.
            if (app.MatchIds.Contains(q)) s += 60;
            s += Boost(app.UsageKey, currentWs);
            results.Add(AppItem(app, wm, s, windowsByApp.GetValueOrDefault(app)));
        }

        foreach (var w in wm.Windows)
        {
            // Windows rank below the app they belong to; a title hit counts more than a process-name hit.
            double s = Math.Max(Fuzzy.Score(q, w.Title) - 120, Fuzzy.Score(q, w.ProcessName) - 220);
            if (s < 0) continue;
            results.Add(WindowItem(w, wm, s - 40 + Boost("win:" + w.ProcessName, currentWs) * 0.5));
        }

        if (query.WorkspaceToken is null)
        {
            foreach (var c in Commands)
            {
                double s = Fuzzy.Score(q, c.Title);
                if (s < 0) continue;
                results.Add(CommandItem(c, s - 80 + Boost(c.UsageKey, currentWs)));
            }
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        if (results.Count > MaxResults) results.RemoveRange(MaxResults, results.Count - MaxResults);
        return results;
    }

    private double Boost(string key, string? ws) =>
        Math.Min(usage.Global(key) * 25, 150) + Math.Min(usage.ForWorkspace(ws, key) * 60, 250);

    private void AddRecents(List<ResultItem> into, WmSnapshot wm, string? ws, Dictionary<AppEntry, List<WmWindow>> running)
    {
        var byKey = apps.Apps.ToDictionary(a => a.UsageKey);
        foreach (var (key, score) in usage.Top(ws, 6))
        {
            if (byKey.TryGetValue(key, out var app))
                into.Add(AppItem(app, wm, score, running.GetValueOrDefault(app)));
            else if (Commands.FirstOrDefault(c => c.UsageKey == key) is { } cmd)
                into.Add(CommandItem(cmd, score));
        }
    }

    /// <summary>Windows for the empty-query switcher: other workspaces first (that's where you
    /// want to jump), then the current one; each group by workspace then title.</summary>
    private static IEnumerable<WmWindow> OrderedWindows(WmSnapshot wm) =>
        wm.Windows
            .OrderBy(w => w.WorkspaceName == wm.FocusedWorkspace?.Name ? 1 : 0)
            .ThenBy(w => w.WorkspaceName, StringComparer.Ordinal)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase);

    private static ResultItem AppItem(AppEntry app, WmSnapshot wm, double score, List<WmWindow>? running)
    {
        running ??= [];
        string badge = running.Count == 0 ? "" :
            "● " + string.Join(" ", running.Select(w => wm.LabelFor(w.WorkspaceName)).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        return new ResultItem
        {
            Kind = ResultKind.App,
            Title = app.Name,
            Subtitle = running.Count == 0 ? "Application" : $"Application · {running.Count} open",
            Badge = badge,
            UsageKey = app.UsageKey,
            Score = score,
            App = app,
            RunningWindows = running,
        };
    }

    private static ResultItem WindowItem(WmWindow w, WmSnapshot wm, double score) => new()
    {
        Kind = ResultKind.Window,
        Title = string.IsNullOrWhiteSpace(w.Title) ? w.ProcessName : w.Title,
        Subtitle = $"Window · {w.ProcessName}",
        Badge = "ws " + wm.LabelFor(w.WorkspaceName),
        UsageKey = "win:" + w.ProcessName,
        Score = score,
        Window = w,
    };

    private static ResultItem CommandItem(LauncherCommand c, double score) => new()
    {
        Kind = ResultKind.Command,
        Title = c.Title,
        Subtitle = c.Subtitle,
        Glyph = c.Glyph,
        UsageKey = c.UsageKey,
        Score = score,
        Command = c,
    };
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
