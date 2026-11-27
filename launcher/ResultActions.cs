using System.Windows.Input;

namespace GlazeLauncher;

[Flags]
internal enum ActionFlags
{
    None = 0,
    /// <summary>Needs a second press in the action list. Never runs from a shortcut on the result list.</summary>
    Confirm = 1,
    /// <summary>Shown in the danger colour (End task).</summary>
    Destructive = 2,
    /// <summary>Doesn't count towards frecency (Close, End task, Open file location).</summary>
    NoUsage = 4,
    /// <summary>Gives focus back to the window that was in front when the launcher opened.</summary>
    RestoreFocus = 8,
}

/// <summary>A key with modifiers that runs an action without opening the action list.</summary>
internal readonly record struct KeyChord(Key Key, ModifierKeys Modifiers)
{
    public static readonly KeyChord CtrlEnter = new(Key.Enter, ModifierKeys.Control);

    public static KeyChord CtrlDigit(int digit) => new(Key.D0 + digit, ModifierKeys.Control);

    /// <summary>Number-pad digits count as the digit keys; Alt is ignored (the launcher never sees it as a chord).</summary>
    public static KeyChord From(Key key, ModifierKeys modifiers)
    {
        if (key is >= Key.NumPad0 and <= Key.NumPad9) key = Key.D0 + (key - Key.NumPad0);
        return new KeyChord(key, modifiers & (ModifierKeys.Control | ModifierKeys.Shift));
    }

    public KeyChord WithoutShift => this with { Modifiers = Modifiers & ~ModifierKeys.Shift };

    public string Label
    {
        get
        {
            var key = Key switch
            {
                Key.Enter => "↵",
                >= Key.D0 and <= Key.D9 => ((int)(Key - Key.D0)).ToString(),
                _ => Key.ToString(),
            };
            var prefix = (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "");
            return prefix + key;
        }
    }
}

/// <summary>
/// Something a result can do. Providers build these on demand (only for the selected result), so the
/// first one in a result's list is its default (Enter).
/// </summary>
/// <param name="Run">Null when the action only opens <paramref name="Children"/>.</param>
/// <param name="Shortcut">Runs it straight from the result list. Shift added to it runs <paramref name="Shifted"/>.</param>
/// <param name="Hint">Shown where the shortcut goes when the action has none of its own (e.g. "Ctrl+1‒9" on a submenu).</param>
/// <param name="Detail">Muted text after the title.</param>
/// <param name="Shifted">The variant run with Shift held (Shift+Enter, Ctrl+Shift+N), e.g. "stay on this workspace".</param>
/// <param name="UsageWorkspace">Workspace the frecency is recorded for; null means the current one.</param>
internal sealed record ResultAction(
    string Title,
    string Glyph,
    Func<Task>? Run,
    KeyChord? Shortcut = null,
    Func<IReadOnlyList<ResultAction>>? Children = null,
    ActionFlags Flags = ActionFlags.None,
    string? Hint = null,
    string? Detail = null,
    string? ConfirmTitle = null,
    ResultAction? Shifted = null,
    string? UsageWorkspace = null)
{
    public bool IsSubmenu => Run is null && Children is not null;

    /// <summary>Finds the action for a chord in a list and its submenus. With Shift held, an action whose
    /// shortcut lacks Shift runs its <see cref="Shifted"/> variant (or itself when it has none).</summary>
    public static ResultAction? Find(IReadOnlyList<ResultAction> actions, KeyChord chord, bool allowConfirm)
    {
        var exact = FindExact(actions, chord, allowConfirm, depth: 0);
        if (exact is not null || !chord.Modifiers.HasFlag(ModifierKeys.Shift)) return exact;
        var plain = FindExact(actions, chord.WithoutShift, allowConfirm, depth: 0);
        return plain?.Shifted ?? plain;
    }

    private static ResultAction? FindExact(IReadOnlyList<ResultAction> actions, KeyChord chord, bool allowConfirm, int depth)
    {
        foreach (var action in actions)
        {
            if (action.Shortcut == chord && (allowConfirm || !action.Flags.HasFlag(ActionFlags.Confirm))) return action;
        }
        if (depth >= 2) return null;
        foreach (var action in actions)
        {
            if (action.Children is { } children && FindExact(children(), chord, allowConfirm, depth + 1) is { } hit) return hit;
        }
        return null;
    }
}

/// <summary>What a result's actions are built for: the snapshot at open time and the resolved "@N" target.</summary>
internal readonly record struct ActionContext(WmSnapshot Wm, string? TargetWorkspace);

/// <summary>Shared action sets, so providers compose them instead of repeating them.</summary>
internal static class ActionKits
{
    private const string StayHere = ", stay here";

    /// <summary>Slots 1‒9 of the monitor the user is on, each with Ctrl+N and a Shift "stay here" variant.</summary>
    public static IReadOnlyList<ResultAction> WorkspaceSlots(WmSnapshot wm, string verb, string? current,
        Func<string, bool, Task> run)
    {
        int group = WorkspaceGroups.GroupOf(wm, wm.TargetMonitor ?? wm.FocusedMonitor);
        var list = new List<ResultAction>(9);
        for (int slot = 1; slot <= 9; slot++)
        {
            var name = $"{group}{slot}";
            var label = wm.LabelFor(name);
            var apps = wm.Windows.Where(w => w.WorkspaceName == name)
                .Select(w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
            var detail = name == current ? "current" : apps.Count == 0 ? "empty" : string.Join(", ", apps);
            list.Add(new ResultAction($"{verb} {label}", "\uE7C4", () => run(name, true),
                Shortcut: KeyChord.CtrlDigit(slot), Detail: detail, UsageWorkspace: name,
                Shifted: new ResultAction($"{verb} {label}{StayHere}", "\uE7C4", () => run(name, false), UsageWorkspace: name)));
        }
        return list;
    }

    /// <summary>Actions for an application. <paramref name="running"/> are its open windows.</summary>
    public static IReadOnlyList<ResultAction> App(Actions actions, AppEntry app, IReadOnlyList<WmWindow> running, ActionContext ctx)
    {
        var list = new List<ResultAction>(5);
        if (ctx.TargetWorkspace is { } target)
        {
            var label = ctx.Wm.LabelFor(target);
            list.Add(new ResultAction($"Open on workspace {label}", "\uE7C4", () => actions.LaunchOn(app, target, follow: true),
                UsageWorkspace: target,
                Shifted: new ResultAction($"Open on workspace {label}{StayHere}", "\uE7C4", () => actions.LaunchOn(app, target, follow: false),
                    UsageWorkspace: target)));
        }
        else if (running.Count > 0)
        {
            list.Add(new ResultAction(running.Count == 1 ? "Focus open window" : "Focus next open window", "\uE8A7",
                () => actions.FocusOrCycle(running, ctx.Wm)));
            list.Add(new ResultAction("Open new window", "\uE710", () => { actions.Launch(app); return Task.CompletedTask; },
                Shortcut: KeyChord.CtrlEnter));
        }
        else
        {
            list.Add(new ResultAction("Open", "\uE8E5", () => { actions.Launch(app); return Task.CompletedTask; }));
        }

        list.Add(new ResultAction("Open on workspace", "\uE7C4", null, Hint: "Ctrl+1‒9",
            Children: () => WorkspaceSlots(ctx.Wm, "Open on workspace", null,
                (ws, follow) => actions.LaunchOn(app, ws, follow))));

        // Win32 apps only: a packaged app's files sit in WindowsApps, which Explorer can't show.
        if (app.TargetPath is { } path && Actions.CanShowInExplorer(path))
            list.Add(new ResultAction("Open file location", "\uE838", () => { Actions.ShowInExplorer(path); return Task.CompletedTask; },
                Detail: path, Flags: ActionFlags.NoUsage));
        return list;
    }

    /// <summary>Actions for an open window.</summary>
    public static IReadOnlyList<ResultAction> Window(Actions actions, WmWindow window, string? exePath, ActionContext ctx)
    {
        var list = new List<ResultAction>(7);
        var here = ctx.Wm.FocusedWorkspace?.Name;
        if (ctx.TargetWorkspace is { } target)
        {
            var label = ctx.Wm.LabelFor(target);
            list.Add(new ResultAction($"Move to workspace {label}", "\uE7C4", () => actions.MoveWindow(window, target, follow: true),
                UsageWorkspace: target,
                Shifted: new ResultAction($"Move to workspace {label}{StayHere}", "\uE7C4", () => actions.MoveWindow(window, target, follow: false),
                    UsageWorkspace: target)));
        }
        else
        {
            list.Add(new ResultAction("Focus window", "\uE8A7", () => actions.FocusWindow(window)));
        }

        if (here is not null && window.WorkspaceName != here)
            list.Add(new ResultAction("Bring to this workspace", "\uE896", () => actions.MoveWindow(window, here, follow: true),
                Shortcut: KeyChord.CtrlEnter, UsageWorkspace: here));

        list.Add(new ResultAction("Move to workspace", "\uE7C4", null, Hint: "Ctrl+1‒9",
            Children: () => WorkspaceSlots(ctx.Wm, "Move to workspace", window.WorkspaceName,
                (ws, follow) => actions.MoveWindow(window, ws, follow))));

        if (exePath is not null && Actions.CanShowInExplorer(exePath))
            list.Add(new ResultAction("Open file location", "\uE838", () => { Actions.ShowInExplorer(exePath); return Task.CompletedTask; },
                Detail: exePath, Flags: ActionFlags.NoUsage));

        list.Add(new ResultAction("Close window", "\uE8BB", () => actions.CloseWindow(window),
            Shortcut: new KeyChord(Key.W, ModifierKeys.Control), Flags: ActionFlags.NoUsage | ActionFlags.RestoreFocus));
        list.Add(new ResultAction("End task", "\uE71A", () => { actions.EndTask(window); return Task.CompletedTask; },
            Detail: window.ProcessName + ".exe",
            ConfirmTitle: $"Press ↵ again to end {window.ProcessName}.exe (unsaved work is lost)",
            Flags: ActionFlags.Confirm | ActionFlags.Destructive | ActionFlags.NoUsage | ActionFlags.RestoreFocus));
        return list;
    }

    /// <summary>A result with one thing to do.</summary>
    public static Func<ActionContext, IReadOnlyList<ResultAction>> Single(string title, string glyph, Func<Task> run) =>
        _ => [new ResultAction(title, glyph, run)];
}
