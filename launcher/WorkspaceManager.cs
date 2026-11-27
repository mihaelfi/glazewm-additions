using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlazeLauncher;

/// <summary>
/// Workspace groups: each monitor owns nine workspaces named "&lt;group&gt;&lt;slot&gt;". Group 1 is always the
/// Windows primary monitor; the other monitors follow left to right, then top to bottom.
/// </summary>
internal static class WorkspaceGroups
{
    /// <summary>Groups declared in GlazeWM's config (11–19 … 61–69).</summary>
    public const int MaxGroups = 6;

    /// <summary>Monitor indexes (positions in <see cref="WmSnapshot.Monitors"/>, which are GlazeWM's
    /// <c>bind_to_monitor</c> indexes) in group order: element 0 is group 1.</summary>
    public static List<int> Assign(IReadOnlyList<WmMonitor> monitors) => Enumerable.Range(0, monitors.Count)
        .OrderBy(i => monitors[i].IsPrimary ? 0 : 1)
        .ThenBy(i => monitors[i].X)
        .ThenBy(i => monitors[i].Y)
        .Take(MaxGroups)
        .ToList();

    public static int GroupOf(WmSnapshot snapshot, WmMonitor? monitor)
    {
        if (monitor is null) return 1;
        int index = IndexOf(snapshot, monitor);
        int position = index < 0 ? -1 : Assign(snapshot.Monitors).IndexOf(index);
        return position < 0 ? 1 : position + 1;
    }

    public static int IndexOf(WmSnapshot snapshot, WmMonitor monitor)
    {
        for (int i = 0; i < snapshot.Monitors.Count; i++)
            if (snapshot.Monitors[i] == monitor) return i;
        return -1;
    }

    /// <summary>"23" → group 2, slot 3. Only names inside the declared groups count.</summary>
    public static bool TryParse(string name, out int group, out int slot)
    {
        group = slot = 0;
        if (name.Length != 2 || name[0] < '1' || name[0] > '0' + MaxGroups || name[1] < '1' || name[1] > '9') return false;
        group = name[0] - '0';
        slot = name[1] - '0';
        return true;
    }

    /// <summary>
    /// The monitor the user is working on: the one holding the foreground window, or the one under the
    /// cursor when the desktop or taskbar is in front. GlazeWM's own focused monitor doesn't change when
    /// you click an empty desktop, because its focus follows windows.
    /// </summary>
    public static WmMonitor? TargetMonitor(WmSnapshot snapshot, IntPtr foreground)
    {
        if (snapshot.Monitors.Count <= 1) return snapshot.FocusedMonitor;
        bool desktop = foreground == IntPtr.Zero
            || Native.ClassName(foreground) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        var origin = Native.MonitorOrigin(desktop ? IntPtr.Zero : foreground);
        return snapshot.Monitors.FirstOrDefault(m => m.X == origin.X && m.Y == origin.Y)
            ?? snapshot.Monitors.FirstOrDefault(m => origin.X >= m.X && origin.X < m.X + m.Width && origin.Y >= m.Y && origin.Y < m.Y + m.Height)
            ?? snapshot.FocusedMonitor;
    }
}

/// <summary>
/// Keeps GlazeWM's workspaces in step with the monitors, and runs the Alt+N keys.
/// <list type="bullet">
/// <item>Writes each group's monitor index into the <c>&amp;groupN_monitor</c> anchors in config.yaml and reloads it.</item>
/// <item>Workspaces whose monitor is gone merge into the same slot of group 1 (22 → 12): renamed when that slot
/// is free (keeps the layout), otherwise their windows move. Merged windows are remembered and go back when
/// their monitor returns, unless the user has moved them since.</item>
/// <item>Workspaces on the wrong monitor move to their own monitor as a whole.</item>
/// </list>
/// Runs at connect, after monitor changes (debounced, and only once Windows and GlazeWM agree on the monitor
/// count), after config reloads, and on demand (Ctrl+Alt+R).
/// </summary>
internal sealed class WorkspaceManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".glzr", "glazewm", "config.yaml");
    private static readonly TimeSpan MonitorSettleDelay = TimeSpan.FromMilliseconds(1200);
    private const int SettleAttempts = 10;

    private readonly GlazeClient _glaze;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Timer _syncTimer;
    private readonly string _memoryPath = Paths.File("workspace-merges.json");
    private Dictionary<string, MergedWindow> _merged = [];
    private string? _lastLayout;
    private bool _forceNext;
    /// <summary>Window states from just before a burst of monitor events (the client's snapshot is
    /// refreshed only after the event), so the sync can tell what the display change did to them.</summary>
    private Dictionary<string, string>? _statesBeforeChange;

    /// <summary>A window moved off a workspace whose monitor went away.</summary>
    private sealed record MergedWindow(string Origin, string Target);

    public WorkspaceManager(GlazeClient glaze)
    {
        _glaze = glaze;
        _syncTimer = new Timer(_ => _ = SyncSafe(), null, Timeout.Infinite, Timeout.Infinite);
        LoadMemory();
        glaze.ConnectionChanged += connected =>
        {
            if (connected) Schedule(TimeSpan.FromMilliseconds(300), force: true);
        };
        glaze.EventReceived += type =>
        {
            if (type.StartsWith("monitor_", StringComparison.Ordinal))
            {
                _statesBeforeChange ??= glaze.Snapshot.Windows.ToDictionary(w => w.Id, w => w.State);
                Schedule(MonitorSettleDelay, force: false);
            }
            else if (type == "user_config_changed") Schedule(TimeSpan.FromMilliseconds(300), force: true);
        };
    }

    // ---- keys ------------------------------------------------------------------

    /// <summary>Alt+N: workspace N in the group of the monitor the user is on.</summary>
    public async Task FocusSlot(int slot)
    {
        var snapshot = _glaze.Snapshot;
        var monitor = WorkspaceGroups.TargetMonitor(snapshot, Native.GetForegroundWindow());
        await TryCommand($"focus --workspace {WorkspaceGroups.GroupOf(snapshot, monitor)}{slot}");
    }

    /// <summary>Alt+Shift+N: move the foreground window to workspace N of its monitor's group and follow it.
    /// With the desktop in front there's no window to move, so it only switches.</summary>
    public async Task MoveToSlot(int slot)
    {
        var foreground = Native.GetForegroundWindow();
        var snapshot = _glaze.Snapshot;
        var window = snapshot.Windows.FirstOrDefault(w => w.Handle == foreground.ToInt64());
        if (window is null && foreground != IntPtr.Zero && _glaze.IsConnected)
        {
            // A window opened in the last few milliseconds may not be in the snapshot yet.
            try { snapshot = await _glaze.RefreshAsync(); } catch (Exception e) { Log.Info($"snapshot refresh: {e.Message}"); }
            window = snapshot.Windows.FirstOrDefault(w => w.Handle == foreground.ToInt64());
        }

        var monitor = WorkspaceGroups.TargetMonitor(snapshot, foreground);
        var workspace = $"{WorkspaceGroups.GroupOf(snapshot, monitor)}{slot}";
        if (window is not null && window.WorkspaceName != workspace)
            await TryCommand($"move --workspace {workspace}", window.Id);
        await TryCommand($"focus --workspace {workspace}");
        if (window is not null) await TryCommand($"focus --container-id {window.Id}");
    }

    public Task SyncNow() => SyncSafe(force: true);

    // ---- sync ------------------------------------------------------------------

    private void Schedule(TimeSpan delay, bool force)
    {
        if (force) _forceNext = true;
        _syncTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private async Task SyncSafe(bool force = false)
    {
        force |= _forceNext;
        _forceNext = false;
        if (!_glaze.IsConnected) return;
        await _syncLock.WaitAsync();
        try { await Sync(force); }
        catch (TimeoutException)
        {
            // GlazeWM can stop answering for a few seconds while Windows rearranges the displays.
            Log.Info("workspace sync: GlazeWM didn't answer, retrying in 2 s");
            Schedule(TimeSpan.FromSeconds(2), force: true);
        }
        catch (Exception e) { Log.Error("workspace sync", e); }
        finally { _syncLock.Release(); }
    }

    /// <summary>Seconds after a display change at which window positions are checked (then the watch stops).</summary>
    private static readonly int[] SettleChecks = [1, 2, 3, 5, 8, 13, 20, 30];
    private int _settleWatch;

    /// <summary>
    /// After a display change, Windows and some apps (Jellyfin, a Qt app) move windows back to where they were
    /// on that monitor setup, sometimes many seconds after GlazeWM has laid them out. GlazeWM keeps the right
    /// layout but doesn't put the windows back. For half a minute, compare real positions with GlazeWM's and
    /// redraw when they differ. A few GetWindowRect calls per check; nothing runs once the screens have settled.
    /// </summary>
    private void WatchDisplaySettle()
    {
        int watch = Interlocked.Increment(ref _settleWatch);
        _ = Task.Run(async () =>
        {
            int elapsed = 0;
            foreach (int at in SettleChecks)
            {
                await Task.Delay(TimeSpan.FromSeconds(at - elapsed));
                elapsed = at;
                if (watch != _settleWatch || !_glaze.IsConnected) return; // a newer change started its own watch
                try
                {
                    var stray = StrayWindows(await _glaze.RefreshAsync());
                    if (stray.Count == 0) continue;
                    Log.Info($"display settle (+{at} s): {string.Join(", ", stray)} out of place, redrawing");
                    await TryCommand("wm-redraw");
                }
                catch (Exception e) { Log.Info($"display settle check: {e.Message}"); }
            }
        });
    }

    /// <summary>Tiled windows of displayed workspaces that aren't where GlazeWM put them, and windows of hidden
    /// workspaces that are visible.</summary>
    private static List<string> StrayWindows(WmSnapshot snapshot)
    {
        const int Tolerance = 16; // GetWindowRect includes the invisible resize borders (about 7 px)
        var stray = new List<string>();
        foreach (var window in snapshot.Windows)
        {
            var hwnd = new IntPtr(window.Handle);
            if (window.Shown)
            {
                if (window.State != "tiling" || !Native.GetWindowRect(hwnd, out var rect) || Native.IsIconic(hwnd)) continue;
                if (Math.Abs(rect.Left - window.X) > Tolerance || Math.Abs(rect.Top - window.Y) > Tolerance)
                    stray.Add($"{window.ProcessName} at {rect.Left},{rect.Top} (should be {window.X},{window.Y})");
            }
            else if (window.State != "minimized" && Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd) && !Native.IsCloaked(hwnd))
            {
                stray.Add($"{window.ProcessName} visible on hidden workspace {window.WorkspaceName}");
            }
        }
        return stray;
    }

    private async Task Sync(bool force)
    {
        // Docking changes arrive as a burst; wait until Windows and GlazeWM report the same monitors.
        WmSnapshot snapshot = await _glaze.RefreshAsync();
        for (int attempt = 1; snapshot.Monitors.Count != Native.GetSystemMetrics(Native.SM_CMONITORS); attempt++)
        {
            if (attempt >= SettleAttempts)
            {
                Log.Info($"workspace sync skipped: Windows reports {Native.GetSystemMetrics(Native.SM_CMONITORS)} monitors, GlazeWM {snapshot.Monitors.Count}");
                return;
            }
            await Task.Delay(1000);
            snapshot = await _glaze.RefreshAsync();
        }

        var statesBefore = _statesBeforeChange;
        _statesBeforeChange = null;

        var layout = string.Join(";", snapshot.Monitors.Select(m => $"{m.X},{m.Y},{m.Width},{m.Height}"));
        if (!force && layout == _lastLayout) return; // monitor_updated also fires for changes that don't matter here
        Log.Info($"workspace sync: monitors {layout}");

        var groups = WorkspaceGroups.Assign(snapshot.Monitors);
        int primary = groups[0];
        var focusedBefore = snapshot.FocusedWorkspace?.Name;

        if (await WriteBindings(groups, primary)) snapshot = await _glaze.RefreshAsync();
        var (merged, emptied) = await MergeOrphans(snapshot, groups, primary);
        if (merged) snapshot = await _glaze.RefreshAsync();
        if (await RestoreReturned(snapshot, groups)) snapshot = await _glaze.RefreshAsync();
        await MoveMisplaced(snapshot, groups);
        if (statesBefore is not null) await RetileMaximized(await _glaze.RefreshAsync(), statesBefore);
        if (emptied.Count > 0) await RemoveEmptied(emptied, focusedBefore);
        await TryCommand("wm-redraw");
        if (force || statesBefore is not null || layout != _lastLayout) WatchDisplaySettle();

        PruneMemory(await _glaze.RefreshAsync());
        _lastLayout = layout;
    }

    /// <summary>
    /// A display change makes some apps maximize themselves (Chrome does), and GlazeWM takes a maximize as
    /// fullscreen, so the window covers its workspace and new windows tile underneath it. Windows that were
    /// tiling just before the change and are fullscreen now go back to tiling.
    /// </summary>
    private async Task RetileMaximized(WmSnapshot snapshot, Dictionary<string, string> statesBefore)
    {
        foreach (var window in snapshot.Windows)
        {
            if (window.State != "fullscreen" || statesBefore.GetValueOrDefault(window.Id) != "tiling") continue;
            if (await TryCommand("set-tiling", window.Id))
                Log.Info($"re-tiled {window.ProcessName}: it went fullscreen during the display change");
        }
    }

    /// <summary>GlazeWM only removes an empty workspace when it's switched away from, so a merged workspace
    /// left empty would stay on the bar. Visit each one, then return to where the user was.</summary>
    private async Task RemoveEmptied(List<string> emptied, string? focusedBefore)
    {
        var snapshot = await _glaze.RefreshAsync();
        var still = emptied.Where(name =>
            snapshot.Workspaces.Any(ws => ws.Name == name) && snapshot.Windows.All(w => w.WorkspaceName != name)).ToList();
        if (still.Count == 0) return;

        foreach (var name in still) await TryCommand($"focus --workspace {name}");
        var back = focusedBefore is null || still.Contains(focusedBefore)
            ? (focusedBefore is not null && WorkspaceGroups.TryParse(focusedBefore, out _, out int slot) ? $"1{slot}" : null)
            : focusedBefore;
        if (back is not null) await TryCommand($"focus --workspace {back}");
        Log.Info($"removed emptied workspaces {string.Join(", ", still)}");
    }

    /// <summary>Sets each <c>&amp;groupN_monitor</c> anchor to its monitor's index; groups without a
    /// monitor point at the primary. Reloads GlazeWM's config if anything changed.</summary>
    private async Task<bool> WriteBindings(List<int> groups, int primary)
    {
        string text;
        try { text = await File.ReadAllTextAsync(ConfigPath); }
        catch (Exception e) { Log.Error("read GlazeWM config", e); return false; }

        var updated = text;
        for (int group = 1; group <= WorkspaceGroups.MaxGroups; group++)
        {
            int index = group <= groups.Count ? groups[group - 1] : primary;
            var pattern = new Regex($@"(?m)^(\s*bind_to_monitor:\s*&group{group}_monitor[ \t]+)\d+(?=[ \t]*\r?$)");
            if (pattern.Matches(updated).Count != 1)
            {
                Log.Error($"GlazeWM config has no single '&group{group}_monitor' anchor; workspace bindings not updated");
                return false;
            }
            updated = pattern.Replace(updated, m => m.Groups[1].Value + index);
        }
        if (updated == text) return false;

        var temp = ConfigPath + ".launcher-tmp";
        await File.WriteAllTextAsync(temp, updated, new System.Text.UTF8Encoding(false));
        File.Replace(temp, ConfigPath, null);
        if (await TryCommand("wm-reload-config"))
        {
            Log.Info($"workspace bindings: {string.Join(" ", groups.Select((m, i) => $"{i + 1}x→monitor {m}"))}");
            return true;
        }

        // GlazeWM rejected the file: put the previous one back.
        await File.WriteAllTextAsync(temp, text, new System.Text.UTF8Encoding(false));
        File.Replace(temp, ConfigPath, null);
        await TryCommand("wm-reload-config");
        return false;
    }

    /// <summary>Workspaces of groups that have no monitor go to the same slot of group 1. Returns whether
    /// anything changed, and the workspaces left empty by moving their windows out.</summary>
    private async Task<(bool Changed, List<string> Emptied)> MergeOrphans(WmSnapshot snapshot, List<int> groups, int primary)
    {
        bool changed = false;
        var emptied = new List<string>();
        foreach (var workspace in snapshot.Workspaces)
        {
            if (!WorkspaceGroups.TryParse(workspace.Name, out int group, out int slot) || group <= groups.Count) continue;

            var target = $"1{slot}";
            var windows = snapshot.Windows.Where(w => w.WorkspaceName == workspace.Name).ToList();
            bool targetActive = snapshot.Workspaces.Any(ws => ws.Name == target);
            if (!targetActive)
            {
                // Renaming keeps the tiling layout.
                if (!await TryCommand($"update-workspace-config --workspace {workspace.Name} --name {target} --bind-to-monitor {primary}"))
                    continue;
            }
            else
            {
                foreach (var window in windows) await TryCommand($"move --workspace {target}", window.Id);
                emptied.Add(workspace.Name);
            }

            foreach (var window in windows) _merged[window.Id] = new MergedWindow(workspace.Name, target);
            Log.Info($"merged workspace {workspace.Name} into {target} ({windows.Count} windows, {(targetActive ? "moved" : "renamed")})");
            changed = true;
        }
        if (changed) SaveMemory();
        return (changed, emptied);
    }

    /// <summary>Merged windows whose monitor is back return to their original workspace, unless the user
    /// moved them since. A merged workspace that holds only its own windows is renamed back (keeps the layout).</summary>
    private async Task<bool> RestoreReturned(WmSnapshot snapshot, List<int> groups)
    {
        var returning = _merged
            .Where(entry => WorkspaceGroups.TryParse(entry.Value.Origin, out int group, out _) && group <= groups.Count)
            .ToList();
        if (returning.Count == 0) return false;

        foreach (var (key, value) in returning)
        {
            var window = snapshot.Windows.FirstOrDefault(w => w.Id == key);
            if (window is null || window.WorkspaceName != value.Target) _merged.Remove(key);
        }

        bool changed = false;
        foreach (var batch in returning.Where(entry => _merged.ContainsKey(entry.Key)).GroupBy(entry => entry.Value))
        {
            var (origin, target) = batch.Key;
            WorkspaceGroups.TryParse(origin, out int group, out _);
            var ids = batch.Select(entry => entry.Key).ToHashSet();
            var onTarget = snapshot.Windows.Where(w => w.WorkspaceName == target).ToList();
            bool onlyOurs = onTarget.Count > 0 && onTarget.All(w => ids.Contains(w.Id));
            bool originActive = snapshot.Workspaces.Any(ws => ws.Name == origin);

            if (onlyOurs && !originActive
                && await TryCommand($"update-workspace-config --workspace {target} --name {origin} --bind-to-monitor {groups[group - 1]}"))
            {
                Log.Info($"restored workspace {origin} from {target} (renamed back)");
            }
            else
            {
                foreach (var id in ids) await TryCommand($"move --workspace {origin}", id);
                Log.Info($"restored {ids.Count} windows from {target} to {origin}");
            }
            foreach (var id in ids) _merged.Remove(id);
            changed = true;
        }
        SaveMemory();
        return changed;
    }

    /// <summary>Moves workspaces that sit on another group's monitor to their own, as a whole (windows and layout).</summary>
    private async Task MoveMisplaced(WmSnapshot snapshot, List<int> groups)
    {
        // A move only reaches the neighbouring monitor, so allow a few passes.
        for (int pass = 0; pass < WorkspaceGroups.MaxGroups * 2; pass++)
        {
            var misplaced = snapshot.Workspaces.FirstOrDefault(ws =>
                WorkspaceGroups.TryParse(ws.Name, out int group, out _) && group <= groups.Count && ws.MonitorIndex != groups[group - 1]);
            if (misplaced is null) return;

            WorkspaceGroups.TryParse(misplaced.Name, out int g, out _);
            var from = snapshot.Monitors[misplaced.MonitorIndex];
            var to = snapshot.Monitors[groups[g - 1]];
            var direction = Direction(from, to);
            if (!await TryCommand($"move-workspace --direction {direction}", misplaced.Id))
            {
                Log.Info($"could not move workspace {misplaced.Name} {direction}");
                return;
            }
            snapshot = await _glaze.RefreshAsync();
        }
    }

    /// <summary>Direction from one monitor towards another: along the axis where they sit side by side,
    /// otherwise along the larger distance.</summary>
    private static string Direction(WmMonitor from, WmMonitor to)
    {
        bool rowsOverlap = from.Y < to.Y + to.Height && to.Y < from.Y + from.Height;
        bool columnsOverlap = from.X < to.X + to.Width && to.X < from.X + from.Width;
        int dx = (to.X + to.Width / 2) - (from.X + from.Width / 2);
        int dy = (to.Y + to.Height / 2) - (from.Y + from.Height / 2);
        bool horizontal = rowsOverlap || (!columnsOverlap && Math.Abs(dx) >= Math.Abs(dy));
        return horizontal ? (dx < 0 ? "left" : "right") : (dy < 0 ? "up" : "down");
    }

    // ---- merge memory ----------------------------------------------------------

    private void PruneMemory(WmSnapshot snapshot)
    {
        int before = _merged.Count;
        foreach (var key in _merged.Keys.ToList())
        {
            var window = snapshot.Windows.FirstOrDefault(w => w.Id == key);
            if (window is null || window.WorkspaceName != _merged[key].Target) _merged.Remove(key);
        }
        if (_merged.Count != before) SaveMemory();
    }

    private void LoadMemory()
    {
        try
        {
            if (File.Exists(_memoryPath))
                _merged = JsonSerializer.Deserialize<Dictionary<string, MergedWindow>>(File.ReadAllText(_memoryPath)) ?? [];
        }
        catch (Exception e) { Log.Error("load workspace merges", e); }
    }

    private void SaveMemory()
    {
        try { File.WriteAllText(_memoryPath, JsonSerializer.Serialize(_merged)); }
        catch (Exception e) { Log.Error("save workspace merges", e); }
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
}
