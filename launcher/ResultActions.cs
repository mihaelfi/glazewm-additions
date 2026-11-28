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
    /// <summary>The launcher stays open: afterwards it searches <see cref="ResultAction.ThenQuery"/> if set,
    /// otherwise it rebuilds the action list (e.g. after removing an alias).</summary>
    KeepOpen = 16,
    /// <summary>A row that only informs (e.g. why an alias can't be saved); Enter does nothing.</summary>
    Disabled = 32,
}

/// <summary>A key with modifiers that runs an action without opening the action list.</summary>
internal readonly record struct KeyChord(Key Key, ModifierKeys Modifiers)
{
    public static readonly KeyChord CtrlEnter = new(Key.Enter, ModifierKeys.Control);
    /// <summary>Run as administrator, as in the Start menu.</summary>
    public static readonly KeyChord CtrlShiftEnter = new(Key.Enter, ModifierKeys.Control | ModifierKeys.Shift);

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
/// <param name="Input">Opens a text-entry level: the search box takes text (placeholder <paramref name="InputPlaceholder"/>)
/// and the rows are built from it as you type, e.g. "Add alias".</param>
/// <param name="ThenQuery">With <see cref="ActionFlags.KeepOpen"/>: the query the launcher shows afterwards.</param>
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
    string? UsageWorkspace = null,
    Func<string, IReadOnlyList<ResultAction>>? Input = null,
    string? InputPlaceholder = null,
    string? ThenQuery = null)
{
    public bool IsSubmenu => Run is null && (Children is not null || Input is not null);

    /// <summary>Does nothing on Enter: an informational row.</summary>
    public bool IsInert => Flags.HasFlag(ActionFlags.Disabled) || (Run is null && !IsSubmenu);

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
    private const string AdminGlyph = ""; // Admin (shield)

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

        // Programs and desktop-bridge packaged apps. UWP apps can't be elevated: an inert row says so, and keeps
        // Ctrl+Shift+Enter from falling back to an ordinary launch. Documents and links (.url, .pdf) don't get it.
        if (app.CanRunAsAdmin)
            list.Add(new ResultAction("Run as administrator", AdminGlyph, () => actions.RunAsAdmin(app),
                Shortcut: KeyChord.CtrlShiftEnter));
        else if (app.IsPackaged)
            list.Add(new ResultAction("Run as administrator", AdminGlyph, null, Shortcut: KeyChord.CtrlShiftEnter,
                Detail: "not possible for this Store app", Flags: ActionFlags.Disabled));

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

    private const string TerminalGlyph = "";
    private const string CopyGlyph = "";
    private static readonly string[] Runnable = [".exe", ".bat", ".cmd", ".com", ".msi"];

    /// <summary>Opens a terminal in a folder: Windows Terminal when installed, otherwise PowerShell.</summary>
    private static (string Title, Actions.ShellKind Kind) MainShell => Actions.HasWindowsTerminal
        ? ("Open in Terminal", Actions.ShellKind.Terminal)
        : ($"Open {Actions.PowerShellName}", Actions.ShellKind.PowerShell);

    /// <summary>Actions for a folder from the file index: open, a shell in it (Ctrl+Enter, elevated with Ctrl+Shift+Enter).</summary>
    public static IReadOnlyList<ResultAction> Folder(string path)
    {
        var (shellTitle, shell) = MainShell;
        var list = new List<ResultAction>(8)
        {
            new("Open", "", () => { Actions.OpenPath(path); return Task.CompletedTask; }),
            new($"{shellTitle} here", TerminalGlyph, () => Actions.OpenShell(path, shell, admin: false), Shortcut: KeyChord.CtrlEnter),
            new($"{shellTitle} here as administrator", AdminGlyph, () => Actions.OpenShell(path, shell, admin: true),
                Shortcut: KeyChord.CtrlShiftEnter),
        };
        if (shell != Actions.ShellKind.PowerShell)
        {
            list.Add(new($"Open {Actions.PowerShellName} here", TerminalGlyph, () => Actions.OpenShell(path, Actions.ShellKind.PowerShell, admin: false),
                Shifted: new($"Open {Actions.PowerShellName} here as administrator", AdminGlyph,
                    () => Actions.OpenShell(path, Actions.ShellKind.PowerShell, admin: true))));
        }
        list.Add(new("Open Command Prompt here", TerminalGlyph, () => Actions.OpenShell(path, Actions.ShellKind.Cmd, admin: false),
            Shifted: new("Open Command Prompt here as administrator", AdminGlyph, () => Actions.OpenShell(path, Actions.ShellKind.Cmd, admin: true))));
        list.Add(ShowInFolder(path));
        list.Add(CopyPath(path));
        return list;
    }

    /// <summary>Actions for a file from the file index.</summary>
    public static IReadOnlyList<ResultAction> File(string path)
    {
        var folder = Path.GetDirectoryName(path) ?? path;
        var (shellTitle, shell) = MainShell;
        var list = new List<ResultAction>(6)
        {
            new("Open", "", () => { Actions.OpenPath(path); return Task.CompletedTask; }),
            ShowInFolder(path) with { Shortcut = KeyChord.CtrlEnter },
        };
        if (Runnable.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            list.Add(new("Run as administrator", AdminGlyph, () => Actions.RunFileAsAdmin(path), Shortcut: KeyChord.CtrlShiftEnter));
        list.Add(new($"{shellTitle} in its folder", TerminalGlyph, () => Actions.OpenShell(folder, shell, admin: false), Detail: folder));
        list.Add(CopyPath(path));
        return list;
    }

    private static ResultAction ShowInFolder(string path) =>
        new("Open file location", "", () => { Actions.ShowInExplorer(path); return Task.CompletedTask; }, Detail: Path.GetDirectoryName(path));

    private static ResultAction CopyPath(string path) =>
        new("Copy path", CopyGlyph, () => { Actions.CopyText(path); return Task.CompletedTask; }, Detail: path, Flags: ActionFlags.NoUsage);

    /// <summary>A result with one thing to do.</summary>
    public static Func<ActionContext, IReadOnlyList<ResultAction>> Single(string title, string glyph, Func<Task> run) =>
        _ => [new ResultAction(title, glyph, run)];

    /// <summary>A result's own actions followed by its alias actions.</summary>
    public static Func<ActionContext, IReadOnlyList<ResultAction>> WithAliases(AliasBook book, string target, string title,
        Func<ActionContext, IReadOnlyList<ResultAction>> actions) =>
        ctx => [.. actions(ctx), .. Aliases(book, target, title)];

    private const string AliasGlyph = ""; // Tag

    /// <summary>"Add alias…" (a text-entry level), and "Aliases ›" to remove one when the result has any.</summary>
    public static IReadOnlyList<ResultAction> Aliases(AliasBook book, string target, string title)
    {
        var list = new List<ResultAction>(2)
        {
            new("Add alias…", AliasGlyph, null, Flags: ActionFlags.NoUsage,
                Input: text => AliasInput(book, target, title, text),
                InputPlaceholder: $"Alias for {title}, ↵ saves"),
        };
        var existing = book.AliasesFor(target);
        if (existing.Count > 0)
        {
            list.Add(new ResultAction("Aliases", AliasGlyph, null, Detail: string.Join(", ", existing),
                Children: () => book.AliasesFor(target).Select(alias => new ResultAction(
                    $"Remove alias “{alias}”", "", () => { book.Remove(alias); return Task.CompletedTask; },
                    Flags: ActionFlags.NoUsage | ActionFlags.KeepOpen)).ToList()));
        }
        return list;
    }

    /// <summary>The row for the text typed in "Add alias…": save it, move it from another result, or why it can't be saved.</summary>
    private static IReadOnlyList<ResultAction> AliasInput(AliasBook book, string target, string title, string text)
    {
        if (AliasBook.Validate(text, out var alias) is { } error) return [Inert(error)];
        if (alias.Length == 0) return [];
        if (!book.CanSave) return [Inert("settings.json has a JSON error; fix it to save aliases")];

        var owner = book.TargetOf(alias);
        if (owner == target) return [Inert($"“{alias}” is already an alias of {title}")];
        return
        [
            new ResultAction(owner is null ? $"Add alias “{alias}”" : $"Move alias “{alias}” here", AliasGlyph,
                () => { book.Add(alias, target); return Task.CompletedTask; },
                Detail: owner is null ? $"for {title}" : $"from {book.Describe(owner) ?? owner}",
                Flags: ActionFlags.NoUsage | ActionFlags.KeepOpen, ThenQuery: alias),
        ];
    }

    private static ResultAction Inert(string title) => new(title, "", null, Flags: ActionFlags.Disabled);
}
