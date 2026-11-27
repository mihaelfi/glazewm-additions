using System.Diagnostics;

namespace GlazeLauncher;

/// <summary>How a result was activated.</summary>
internal readonly record struct ActivateOptions(
    /// <summary>Workspace name to send the app/window to (already resolved), or null.</summary>
    string? TargetWorkspace,
    /// <summary>Ctrl: for apps, start a new instance even if one is open; for windows, bring it here.</summary>
    bool Alternate,
    /// <summary>Shift: with a target workspace, stay on the current one (Hyprland's "silent").</summary>
    bool Stay);

/// <summary>Runs the action for a result: launch, focus, move, or a command.</summary>
internal sealed class Actions
{
    private static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(15);

    private readonly GlazeClient _glaze;
    private readonly UsageStore _usage;
    private readonly WindowIdentity _identity;
    private readonly List<PendingLaunch> _pending = [];

    private sealed record PendingLaunch(AppEntry App, string Workspace, bool Follow, DateTime Deadline);

    public Actions(GlazeClient glaze, UsageStore usage, WindowIdentity identity)
    {
        _glaze = glaze;
        _usage = usage;
        _identity = identity;
        _glaze.WindowManaged += OnWindowManaged;
    }

    public event Action<string>? InternalCommand;

    public async Task Activate(ResultItem item, ActivateOptions opt, WmSnapshot wm)
    {
        var currentWs = wm.FocusedWorkspace?.Name;
        _usage.Record(item.UsageKey, opt.TargetWorkspace ?? currentWs);

        try
        {
            await item.Activate(opt, wm);
        }
        catch (Exception e)
        {
            Log.Error($"activate {item.Title}", e);
        }
    }

    public async Task ActivateApp(AppEntry app, IReadOnlyList<WmWindow> runningWindows, ActivateOptions opt, WmSnapshot wm)
    {
        // Already open and no workspace given: focus it instead of starting another copy.
        // Repeated activation cycles through its windows.
        if (opt.TargetWorkspace is null && !opt.Alternate && runningWindows.Count > 0)
        {
            var windows = runningWindows
                .OrderBy(w => w.WorkspaceName == wm.FocusedWorkspace?.Name ? 0 : 1)
                .ToList();
            int focused = windows.FindIndex(w => w.HasFocus);
            var next = windows[(focused + 1) % windows.Count];
            await FocusWindow(next);
            return;
        }

        if (opt.TargetWorkspace is { } ws)
        {
            bool follow = !opt.Stay;
            lock (_pending) _pending.Add(new PendingLaunch(app, ws, follow, DateTime.UtcNow + PendingTimeout));
            // Following: switch first so the window opens in place; the pending move is a fallback for
            // apps that take long to start, or if focus wandered off in the meantime.
            if (follow && _glaze.IsConnected) await TryCommand($"focus --workspace {ws}");
        }

        Launch(app);
    }

    private static void Launch(AppEntry app)
    {
        Log.Info($"launch {app.Name} ({app.Id})");
        Process.Start(new ProcessStartInfo(app.IconPath /* shell:AppsFolder\<id> */)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    public async Task ActivateWindow(WmWindow w, ActivateOptions opt, WmSnapshot wm)
    {
        string? destination = opt.TargetWorkspace ?? (opt.Alternate ? wm.FocusedWorkspace?.Name : null);
        bool moved = false;
        if (destination is not null && destination != w.WorkspaceName)
        {
            moved = await TryCommand($"move --workspace {destination}", w.Id);
            if (opt.Stay) return;
        }
        await FocusWindow(w, moved ? destination : null);
    }

    public async Task FocusWindow(WmWindow w, string? destination = null)
    {
        if (_glaze.IsConnected)
        {
            var snapshot = _glaze.Snapshot;
            var workspace = destination
                ?? snapshot.Windows.FirstOrDefault(window => window.Id == w.Id)?.WorkspaceName
                ?? w.WorkspaceName;

            // Focus by ID changes GlazeWM's focus order but doesn't redraw hidden workspaces.
            if (workspace is not null && !snapshot.Workspaces.Any(ws => ws.Name == workspace && ws.IsDisplayed))
                await TryCommand($"focus --workspace {workspace}");

            if (await TryCommand($"focus --container-id {w.Id}")) return;
        }
        Native.ForceForeground(new IntPtr(w.Handle));
    }

    public async Task RunCommand(LauncherCommand c)
    {
        if (c.Internal is not null) { InternalCommand?.Invoke(c.Internal); return; }
        if (c.WmCommand is not null) await TryCommand(c.WmCommand);
    }

    private async Task<bool> TryCommand(string command, string? subject = null)
    {
        try
        {
            await _glaze.RunCommand(command, subject);
            return true;
        }
        catch (Exception e)
        {
            Log.Info($"command '{command}' failed: {e.Message}");
            return false;
        }
    }

    /// <summary>Routes a new window from a pending "@N" launch to its workspace.</summary>
    private void OnWindowManaged(WmWindow w)
    {
        PendingLaunch? match = null;
        lock (_pending)
        {
            var now = DateTime.UtcNow;
            _pending.RemoveAll(p => p.Deadline < now);
            if (_pending.Count == 0) return;
            var ids = _identity.For(w);
            match = _pending.FirstOrDefault(p => p.App.MatchIds.Overlaps(ids));
            if (match is not null) _pending.Remove(match);
        }
        if (match is null) return;

        Log.Info($"routing new {w.ProcessName} window to workspace {match.Workspace}");
        _ = Task.Run(async () =>
        {
            bool moved = await TryCommand($"move --workspace {match.Workspace}", w.Id);
            if (match.Follow) await FocusWindow(w, moved ? match.Workspace : null);
        });
    }
}
