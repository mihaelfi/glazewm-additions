using System.Diagnostics;

namespace GlazeLauncher;

/// <summary>The things result actions do: launch, focus, move, close, or run a command.</summary>
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

    /// <summary>Runs one of a result's actions and records it for frecency.</summary>
    public async Task Run(ResultItem item, ResultAction action, WmSnapshot wm)
    {
        if (action.Run is null) return;
        if (!action.Flags.HasFlag(ActionFlags.NoUsage))
            _usage.Record(item.UsageKey, action.UsageWorkspace ?? wm.FocusedWorkspace?.Name);
        Log.Info($"action '{action.Title}' on {item.UsageKey}");

        try
        {
            await action.Run();
        }
        catch (Exception e)
        {
            Log.Error($"action '{action.Title}' on {item.UsageKey}", e);
        }
    }

    /// <summary>Focuses an app's open window; repeated use cycles through its windows,
    /// starting with the ones on the current workspace.</summary>
    public Task FocusOrCycle(IReadOnlyList<WmWindow> runningWindows, WmSnapshot wm)
    {
        var windows = runningWindows
            .OrderBy(w => w.WorkspaceName == wm.FocusedWorkspace?.Name ? 0 : 1)
            .ToList();
        int focused = windows.FindIndex(w => w.HasFocus);
        return FocusWindow(windows[(focused + 1) % windows.Count]);
    }

    /// <summary>Starts the app and sends its next new window to a workspace.</summary>
    public async Task LaunchOn(AppEntry app, string workspace, bool follow)
    {
        lock (_pending) _pending.Add(new PendingLaunch(app, workspace, follow, DateTime.UtcNow + PendingTimeout));
        // Following: switch first so the window opens in place; the pending move is a fallback for
        // apps that take long to start, or if focus wandered off in the meantime.
        if (follow && _glaze.IsConnected) await TryCommand($"focus --workspace {workspace}");
        Launch(app);
    }

    public void Launch(AppEntry app)
    {
        Log.Info($"launch {app.Name} ({app.Id})");
        OpenShellItem(app.IconPath);
    }

    public static void OpenShellItem(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>Moves a window to a workspace; following switches there and focuses it.</summary>
    public async Task MoveWindow(WmWindow w, string workspace, bool follow)
    {
        bool moved = false;
        if (workspace != w.WorkspaceName) moved = await TryCommand($"move --workspace {workspace}", w.Id);
        if (follow) await FocusWindow(w, moved ? workspace : null);
    }

    /// <summary>Asks the window to close, as its close button would (the app may ask to save).</summary>
    public async Task CloseWindow(WmWindow w)
    {
        if (_glaze.IsConnected && await TryCommand("close", w.Id)) return;
        var hwnd = new IntPtr(w.Handle);
        if (Native.IsWindow(hwnd)) Native.PostMessage(hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Ends the window's process, like Task Manager's End task.</summary>
    public void EndTask(WmWindow w)
    {
        var hwnd = new IntPtr(w.Handle);
        if (!Native.IsWindow(hwnd)) { Log.Info($"end task: window {w.Id} is gone"); return; }
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) { Log.Info($"end task: refused for pid {pid}"); return; }
        try
        {
            using var process = Process.GetProcessById((int)pid);
            Log.Info($"end task: {process.ProcessName} (pid {pid})");
            process.Kill();
        }
        catch (Exception e)
        {
            // Elevated processes can't be opened from here; that's expected.
            Log.Info($"end task: pid {pid} failed: {e.Message}");
        }
    }

    /// <summary>A full file path outside WindowsApps: Explorer can't select items in packaged apps' folders.
    /// No disk access, because the footer builds actions on every selection change.</summary>
    public static bool CanShowInExplorer(string path) =>
        Path.IsPathFullyQualified(path) && !path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens Explorer with the file selected. ShellExecute, so no handles are inherited.</summary>
    public static void ShowInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    public async Task FocusWindow(WmWindow w, string? destination = null)
    {
        var hwnd = new IntPtr(w.Handle);
        if (_glaze.IsConnected)
        {
            var snapshot = _glaze.Snapshot;
            var workspace = destination
                ?? snapshot.Windows.FirstOrDefault(window => window.Id == w.Id)?.WorkspaceName
                ?? w.WorkspaceName;

            // Focus by ID changes GlazeWM's focus order but doesn't redraw hidden workspaces.
            if (workspace is not null && !snapshot.Workspaces.Any(ws => ws.Name == workspace && ws.IsDisplayed))
                await TryCommand($"focus --workspace {workspace}");

            // Focus by ID leaves a minimized window minimized, so the workspace looks empty.
            // Restore it after the switch, so it reappears on a displayed workspace, and wait until
            // GlazeWM has seen the restore: focusing a window it still thinks is minimized leaves
            // GlazeWM's focus on the previous window.
            if (RestoreIfMinimized(hwnd)) await WaitUntilNotMinimized(w.Id);

            if (await TryCommand($"focus --container-id {w.Id}")) return;
        }
        RestoreIfMinimized(hwnd);
        Native.ForceForeground(hwnd);
    }

    /// <summary>Restores a minimized window. GlazeWM sees the restore and returns the window to
    /// its state before it was minimized (tiling or floating).</summary>
    private static bool RestoreIfMinimized(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsIconic(hwnd)) return false;
        Native.ShowWindow(hwnd, Native.SW_RESTORE);
        Log.Info($"restored minimized window 0x{hwnd:X}");
        return true;
    }

    /// <summary>Waits briefly (only after a restore) for GlazeWM to process it.</summary>
    private async Task WaitUntilNotMinimized(string windowId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(600);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await _glaze.WindowStateAsync(windowId) != "minimized") return;
            }
            catch (Exception e)
            {
                Log.Info($"window state query failed: {e.Message}");
                return;
            }
            await Task.Delay(30);
        }
        Log.Info($"GlazeWM still reports window {windowId} as minimized; focusing anyway");
    }

    /// <summary>Switches to a workspace without touching any window.</summary>
    public Task GoToWorkspace(string workspace) => TryCommand($"focus --workspace {workspace}");

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
