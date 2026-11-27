namespace GlazeLauncher;

internal readonly record struct SearchContext(
    Query Query, WmSnapshot Wm, IReadOnlyDictionary<string, double> Recents)
{
    public bool IsHome => Query.Text.Length == 0;
}

/// <param name="Pinned">Shown above every ranked result, e.g. the action a query asks for directly.</param>
internal readonly record struct ProviderResult(ResultItem Item, TextMatch Match, WmWindow? TargetWindow = null, bool Pinned = false);

/// <summary>Searches data already in memory. Each result supplies its own activation action.</summary>
internal interface ISearchProvider
{
    int Priority { get; }
    IEnumerable<ProviderResult> Search(SearchContext context);
}

/// <summary>Updates the cached data when the launcher opens.</summary>
internal interface IRefreshableSearchProvider : ISearchProvider
{
    Task RefreshAsync(WmSnapshot snapshot);
}

internal sealed class AppSearchProvider(ShellIndex index, WindowIdentity identity, Actions actions) : ISearchProvider
{
    private WmSnapshot? _previousSnapshot;
    private IReadOnlyList<AppEntry>? _previousApps;
    private Dictionary<AppEntry, List<WmWindow>> _running = [];

    public int Priority => 12;

    public IEnumerable<ProviderResult> Search(SearchContext context)
    {
        var apps = index.Apps;
        var runningByApp = RunningWindows(apps, context.Wm);
        foreach (var app in apps)
        {
            var match = default(TextMatch);
            if (context.IsHome)
            {
                if (!context.Recents.ContainsKey(app.UsageKey)) continue;
            }
            else
            {
                match = Fuzzy.Match(context.Query.Text, app.Name);
                if (!match.Found) continue;
                // Prefer the canonical app for its executable or app ID, e.g. Chrome over Chrome Remote Desktop.
                if (app.MatchIds.Contains(context.Query.Text))
                    match = new TextMatch(MatchQuality.Exact, Math.Max(match.Score, 980));
            }

            IReadOnlyList<WmWindow> running = runningByApp.GetValueOrDefault(app) ?? [];
            string badge = running.Count == 0 ? "" : "● " + string.Join(" ", running
                .Select(window => context.Wm.LabelFor(window.WorkspaceName))
                .Distinct().OrderBy(label => label, StringComparer.Ordinal));
            yield return new ProviderResult(new ResultItem
            {
                Id = app.UsageKey,
                Title = app.Name,
                Subtitle = running.Count == 0 ? "Application" : $"Application · {running.Count} open",
                Badge = badge,
                UsageKey = app.UsageKey,
                IconPath = app.IconPath,
                FooterText = running.Count > 0
                    ? "↵ focus open window   Ctrl+↵ new window   Ctrl+1‒9 open on workspace (+Shift: stay)   Esc close"
                    : "↵ open   Ctrl+1‒9 or @N open on workspace (+Shift: stay)   Esc close",
                Activate = (options, wm) => actions.ActivateApp(app, running, options, wm),
            }, match);
        }
    }

    private Dictionary<AppEntry, List<WmWindow>> RunningWindows(IReadOnlyList<AppEntry> apps, WmSnapshot wm)
    {
        if (ReferenceEquals(apps, _previousApps) && ReferenceEquals(wm, _previousSnapshot)) return _running;

        var running = new Dictionary<AppEntry, List<WmWindow>>();
        foreach (var window in wm.Windows)
        {
            var ids = identity.For(window);
            foreach (var app in apps)
            {
                if (!app.MatchIds.Overlaps(ids)) continue;
                if (!running.TryGetValue(app, out var windows)) running[app] = windows = [];
                windows.Add(window);
                break;
            }
        }
        _previousApps = apps;
        _previousSnapshot = wm;
        _running = running;
        return running;
    }
}

internal sealed class WindowSearchProvider(WindowIdentity identity, Actions actions) : ISearchProvider
{
    private const string SubtitlePrefix = "Window · ";

    public int Priority => 24;

    public IEnumerable<ProviderResult> Search(SearchContext context)
    {
        foreach (var window in context.Wm.Windows)
        {
            var title = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
            var match = default(TextMatch);
            bool matchedProcess = false;
            if (!context.IsHome)
            {
                match = Fuzzy.Match(context.Query.Text, title);
                var process = Fuzzy.Match(context.Query.Text, window.ProcessName);
                // A process name is useful when a window has a generic title. Prefer a title of similar quality.
                process = process with { Score = process.Score - 60 };
                if (process.CompareTo(match) > 0)
                {
                    match = process;
                    matchedProcess = true;
                }
                if (!match.Found) continue;
            }

            yield return new ProviderResult(new ResultItem
            {
                Id = "window:" + window.Id,
                Title = title,
                Subtitle = SubtitlePrefix + window.ProcessName,
                SubtitleMatchStart = matchedProcess ? SubtitlePrefix.Length : null,
                Badge = "ws " + context.Wm.LabelFor(window.WorkspaceName),
                UsageKey = "win:" + window.ProcessName,
                IconPath = identity.ExePath(window),
                FooterText = "↵ focus   Ctrl+↵ bring here   Ctrl+1‒9 or @N move to workspace (+Shift: stay)   Esc close",
                Activate = (options, wm) => actions.ActivateWindow(window, options, wm),
            }, match, window);
        }
    }
}

internal sealed class FolderSearchProvider(ShellIndex index) : ISearchProvider
{
    public int Priority => 16;

    public IEnumerable<ProviderResult> Search(SearchContext context)
    {
        if (context.Query.WorkspaceToken is not null) yield break;

        foreach (var folder in index.Folders)
        {
            var id = $"folder:{folder.Id:D}";
            var match = Fuzzy.Match(context.Query.Text, folder.Name);
            if (context.IsHome ? !context.Recents.ContainsKey(id) : !match.Found) continue;

            yield return new ProviderResult(new ResultItem
            {
                Id = id,
                Title = folder.Name,
                Subtitle = $"Folder · {folder.Path}",
                Glyph = "\uE8B7",
                IconPath = folder.Path,
                IconFolderId = folder.Id,
                UsageKey = id,
                FooterText = "↵ open in File Explorer   Esc close",
                SupportsWorkspaceTarget = false,
                Activate = (_, _) =>
                {
                    Actions.OpenShellItem(folder.Path);
                    return Task.CompletedTask;
                },
            }, match);
        }
    }
}

/// <summary>A query that is only "@N" offers to switch to that workspace.</summary>
internal sealed class WorkspaceSearchProvider(Actions actions) : ISearchProvider
{
    public int Priority => 0;

    public IEnumerable<ProviderResult> Search(SearchContext context)
    {
        if (!context.IsHome || context.Query.WorkspaceToken is not { } token) yield break;

        // Same rule as "@N" for apps: one digit is a slot on the focused monitor, two are literal.
        var workspace = context.Wm.ResolveWorkspace(token);
        var label = context.Wm.LabelFor(workspace);
        yield return new ProviderResult(new ResultItem
        {
            Id = "workspace:goto",
            Title = $"Go to workspace {label}",
            Subtitle = context.Wm.Workspaces.Any(ws => ws.Name == workspace && ws.IsDisplayed)
                ? "GlazeWM · already shown"
                : "GlazeWM · switch workspace",
            Glyph = "",
            UsageKey = "cmd:Go to workspace",
            FooterText = "↵ go to workspace   ↓ pick a window or app to send there   Esc close",
            // Enter, Ctrl+1‒9 and a click all arrive with the resolved target workspace.
            Activate = (options, _) => actions.GoToWorkspace(options.TargetWorkspace ?? workspace),
        }, default, Pinned: true);
    }
}

internal sealed class CommandSearchProvider(Actions actions) : ISearchProvider
{
    private static readonly LauncherCommand[] Commands =
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

    public int Priority => 0;

    public IEnumerable<ProviderResult> Search(SearchContext context)
    {
        if (context.Query.WorkspaceToken is not null) yield break;

        foreach (var command in Commands)
        {
            var match = Fuzzy.Match(context.Query.Text, command.Title);
            if (context.IsHome ? !context.Recents.ContainsKey(command.UsageKey) : !match.Found) continue;

            yield return new ProviderResult(new ResultItem
            {
                Id = command.UsageKey,
                Title = command.Title,
                Subtitle = command.Subtitle,
                Glyph = command.Glyph,
                UsageKey = command.UsageKey,
                FooterText = "↵ run   Esc close",
                Activate = (_, _) => actions.RunCommand(command),
            }, match);
        }
    }
}
