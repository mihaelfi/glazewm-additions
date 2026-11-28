using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GlazeLauncher;

public partial class MainWindow : Window
{
    private const double WidthDip = 720;
    private static readonly Duration OpenDuration = new(TimeSpan.FromMilliseconds(125));

    private readonly GlazeClient _glaze;
    private readonly ShellIndex _shellIndex;
    private readonly SearchEngine _search;
    private readonly Actions _actions;
    private readonly IconCache _icons;

    private WmSnapshot _wm = WmSnapshot.Empty;
    private IntPtr _hwnd;
    private IntPtr _previousForeground;
    private bool _hiding;
    private long _openVersion;

    internal MainWindow(GlazeClient glaze, ShellIndex shellIndex, SearchEngine search, Actions actions, IconCache icons)
    {
        _glaze = glaze;
        _shellIndex = shellIndex;
        _search = search;
        _actions = actions;
        _icons = icons;
        InitializeComponent();

        Deactivated += (_, _) => HideLauncher(restoreFocus: false);
        PreviewKeyDown += OnPreviewKeyDown;
        _shellIndex.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(keepSelection: true); });
    }

    public IntPtr Handle => _hwnd;

    /// <summary>A provider has new results for the current query (file search): re-run it, keeping the selection.
    /// Safe from any thread.</summary>
    public void RefreshResults() => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(keepSelection: true); });

    /// <summary>Creates the HWND without showing the window, so the first show is instant.</summary>
    public IntPtr Prepare()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.StyleWindow(_hwnd, 0x3A, 0x40, 0x6E);
        return _hwnd;
    }

    public void Toggle()
    {
        if (IsVisible) HideLauncher(restoreFocus: true);
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        if (IsVisible) { Native.ForceForeground(_hwnd); return; }

        _previousForeground = Native.GetForegroundWindow();
        // The monitor the user is on (foreground window, or cursor over an empty desktop): the launcher opens
        // there, and a one-digit "@N" means that monitor's workspace group, the same rule as Alt+N.
        var snapshot = _glaze.Snapshot;
        _wm = snapshot.WithTarget(WorkspaceGroups.TargetMonitor(snapshot, _previousForeground));
        QueryBox.Text = "";
        Refresh(keepSelection: false);

        PlaceOn(_wm.TargetMonitor ?? _wm.FocusedMonitor);
        PlayOpenTransition();
        Show();
        Activate();
        Native.ForceForeground(_hwnd);
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
        Log.Info($"shown on ws {_wm.FocusedWorkspace?.Name ?? "?"}, foreground={Native.GetForegroundWindow() == _hwnd}");

        // The snapshot is kept current by IPC events, but titles can change without an event:
        // refresh once in the background and re-render without moving the selection.
        _ = RefreshSearchAsync(++_openVersion);
        _ = _shellIndex.RefreshIfStale();
    }

    private void PlayOpenTransition()
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        OpenOffset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, OpenDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        });
        LauncherContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.72, 1, OpenDuration)
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        });
    }

    private async Task RefreshSearchAsync(long openVersion)
    {
        if (_glaze.IsConnected)
        {
            try
            {
                var wm = await _glaze.RefreshAsync();
                if (!IsVisible || openVersion != _openVersion) return;
                _wm = wm.WithTarget(_wm.TargetMonitor);
                Refresh(keepSelection: true);
            }
            catch (Exception e) { Log.Info($"snapshot refresh: {e.Message}"); }
        }

        if (!IsVisible || openVersion != _openVersion) return;
        try
        {
            await _search.RefreshAsync(_wm);
            if (IsVisible && openVersion == _openVersion) Refresh(keepSelection: true);
        }
        catch (Exception e) { Log.Error("search provider refresh", e); }
    }

    public void HideLauncher(bool restoreFocus)
    {
        if (_hiding || !IsVisible) return;
        _hiding = true;
        try
        {
            Hide();
            CloseActions();
            OpenOffset.BeginAnimation(TranslateTransform.YProperty, null);
            LauncherContent.BeginAnimation(OpacityProperty, null);
            if (restoreFocus && _previousForeground != IntPtr.Zero && Native.IsWindow(_previousForeground))
                Native.SetForegroundWindow(_previousForeground);
        }
        finally { _hiding = false; }
    }

    /// <summary>Centres the window horizontally on the given monitor, a fifth of the way down.</summary>
    private void PlaceOn(WmMonitor? monitor)
    {
        if (monitor is null) return;
        int widthPx = (int)Math.Round(WidthDip * monitor.Scale);
        int x = monitor.X + (monitor.Width - widthPx) / 2;
        int y = monitor.Y + monitor.Height / 5;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // ---- debug helpers (driven over the pipe) -----------------------------------

    public void SetQuery(string text)
    {
        if (!IsVisible) ShowLauncher();
        QueryBox.Text = text;
        QueryBox.CaretIndex = text.Length;
    }

    public void DebugEnter(bool ctrl, bool shift)
    {
        // Hidden means the selection is stale (left over from the last use); don't act on it.
        if (IsVisible) HandleEnter((ctrl ? ModifierKeys.Control : 0) | (shift ? ModifierKeys.Shift : 0));
    }

    public void DebugActions() { if (IsVisible) OpenActions(); }

    public void DebugBack() { if (IsVisible && InActionMode) Back(); }

    /// <summary>Presses a key chord such as "ctrl+shift+3", "ctrl+w", "ctrl+k" or "right" through the normal key handling.</summary>
    public void DebugKey(string chord)
    {
        if (!IsVisible) return;
        var parts = chord.Split('+');
        var mods = ModifierKeys.None;
        foreach (var part in parts[..^1])
            mods |= part switch { "ctrl" => ModifierKeys.Control, "shift" => ModifierKeys.Shift, _ => ModifierKeys.None };
        var name = parts[^1];
        if (name.Length == 1 && char.IsDigit(name[0])) name = "D" + name;
        if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key)) { Log.Info($"debug key: unknown key '{name}'"); return; }
        HandleKey(key, mods);
    }

    public void SaveSnapshot(string path)
    {
        UpdateLayout();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var root = (FrameworkElement)Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        Log.Info($"snapshot saved: {path}");
    }

    // ---- search ----------------------------------------------------------------

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_settingText) return;
        if (InActionMode) RenderActions();
        else Refresh(keepSelection: false);
    }

    private Query CurrentQuery => Query.Parse(QueryBox.Text);

    private void Refresh(bool keepSelection)
    {
        // The search box holds the action filter while the action list is open; the results wait.
        if (InActionMode) return;

        var query = CurrentQuery;
        var previousId = keepSelection && Results.SelectedIndex > 0 ? (Results.SelectedItem as ResultItem)?.Id : null;
        var items = _search.Search(query, _wm);
        foreach (var item in items) LoadIcon(item);

        Results.ItemsSource = items;
        Results.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (items.Count > 0)
        {
            Results.SelectedIndex = previousId is null ? 0 : Math.Max(0, items.FindIndex(item => item.Id == previousId));
            Results.ScrollIntoView(Results.SelectedItem);
        }

        if (query.WorkspaceToken is { } token)
        {
            TargetText.Text = "→ ws " + _wm.LabelFor(_wm.ResolveWorkspace(token));
            TargetText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        else
        {
            TargetText.Text = _glaze.IsConnected ? "ws " + (_wm.FocusedWorkspace?.Label ?? "?") : "GlazeWM offline";
            TargetText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        }
        UpdateFooter();
    }

    private void LoadIcon(ResultItem item)
    {
        if (item.IconPath is not { } path) return;
        if (_icons.TryGet(path, out var cached, item.IconFolderId)) { item.Icon = cached; return; }
        _icons.Request(path, img => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => item.Icon = img), item.IconFolderId);
    }

    /// <summary>The selected result's actions, built for the current query's "@N" target.</summary>
    private IReadOnlyList<ResultAction> SelectedActions(out ResultItem? item)
    {
        item = Results.SelectedItem as ResultItem;
        if (item is null) return [];
        try { return item.Actions(CurrentContext()); }
        catch (Exception e)
        {
            Log.Error($"actions for {item.UsageKey}", e);
            return [];
        }
    }

    private ActionContext CurrentContext() =>
        new(_wm, CurrentQuery.WorkspaceToken is { } token ? _wm.ResolveWorkspace(token) : null);

    private void UpdateFooter()
    {
        if (InActionMode) { UpdateActionFooter(); return; }

        var actions = SelectedActions(out var item);
        if (item is null || actions.Count == 0)
        {
            Footer.Text = "Type to search apps, open windows and commands   Esc close";
            return;
        }

        // The default, then the action list key, then everything reachable by a shortcut.
        var parts = new List<string> { "↵ " + actions[0].Title };
        if (actions.Count > 1) parts.Add("Ctrl+K actions");
        foreach (var action in actions.Skip(1))
        {
            if (action.IsInert) continue; // e.g. "Run as administrator" on a Store app: listed in Ctrl+K with the reason
            if (action.Shortcut is { } chord) parts.Add($"{chord.Label} {action.Title}");
            else if (action.IsSubmenu && action.Hint is { } hint) parts.Add($"{hint} {action.Title}");
        }
        if (actions[0].Shifted is not null) parts.Add("+Shift: stay");
        Footer.Text = string.Join("   ", parts);
    }

    // ---- result actions ---------------------------------------------------------

    /// <summary>One level of the action list. A text-entry level (<paramref name="input"/>) builds its rows from the
    /// typed text instead of filtering a fixed list.</summary>
    private sealed class ActionLevel(string title, Func<IReadOnlyList<ResultAction>> source, ResultAction? input = null)
    {
        public string Title => title;
        public ResultAction? Input => input;
        public IReadOnlyList<ResultAction> Actions { get; private set; } = [];

        /// <summary>(Re)builds the list, e.g. after an action changed what it shows.</summary>
        public ActionLevel Load()
        {
            if (input is null) Actions = source();
            return this;
        }
    }

    private readonly List<ActionLevel> _levels = [];
    private ResultItem? _actionItem;
    private string _savedQuery = "";
    private ActionRow? _armed;
    private bool _settingText;

    private bool InActionMode => _actionItem is not null;

    /// <summary>Replaces the results with the selected result's actions (Ctrl+K, or → at the end of the query).</summary>
    private void OpenActions()
    {
        if (InActionMode) return;
        var actions = SelectedActions(out var item);
        if (item is null || actions.Count == 0) return;
        // The search box becomes the filter, so the "@N" context is captured now for rebuilding the list.
        var context = CurrentContext();

        _actionItem = item;
        _savedQuery = QueryBox.Text;
        _levels.Clear();
        _levels.Add(new ActionLevel(item.Title, () => item.Actions(context)).Load());

        Results.Visibility = Visibility.Collapsed;
        ActionList.Visibility = Visibility.Visible;
        SearchGlyph.Visibility = Visibility.Collapsed;
        Crumb.Visibility = Visibility.Visible;
        CrumbIcon.Source = item.Icon;
        CrumbGlyph.Text = item.Icon is null ? item.Glyph : "";
        SetQueryText("");
        RenderActions();
    }

    private void OpenSubmenu(ResultAction submenu)
    {
        ActionLevel level;
        try
        {
            level = submenu.Input is not null
                ? new ActionLevel(submenu.Title, () => [], submenu)
                : new ActionLevel(submenu.Title, submenu.Children!).Load();
        }
        catch (Exception e) { Log.Error($"submenu {submenu.Title}", e); return; }
        _levels.Add(level);
        SetQueryText("");
        RenderActions();
    }

    /// <summary>One level up: from a submenu to the actions, from the actions to the results.</summary>
    private void Back()
    {
        if (_levels.Count > 1)
        {
            _levels.RemoveAt(_levels.Count - 1);
            SetQueryText("");
            RenderActions();
            return;
        }

        var selectedId = _actionItem?.Id;
        CloseActions();
        SetQueryText(_savedQuery);
        Refresh(keepSelection: false);
        if (selectedId is not null && Results.ItemsSource is List<ResultItem> items && items.FindIndex(i => i.Id == selectedId) is >= 0 and var index)
        {
            Results.SelectedIndex = index;
            Results.ScrollIntoView(Results.SelectedItem);
            UpdateFooter();
        }
    }

    /// <summary>Leaves the action list without touching the search box (used when hiding).</summary>
    private void CloseActions()
    {
        if (!InActionMode) return;
        _actionItem = null;
        _levels.Clear();
        _armed = null;
        ActionList.ItemsSource = null;
        ActionList.Visibility = Visibility.Collapsed;
        Results.Visibility = Results.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Crumb.Visibility = Visibility.Collapsed;
        SearchGlyph.Visibility = Visibility.Visible;
        Placeholder.Text = PlaceholderText;
    }

    private const string PlaceholderText = "Apps, windows, commands…   @3 → workspace 3";

    private void SetQueryText(string text)
    {
        _settingText = true;
        try
        {
            QueryBox.Text = text;
            QueryBox.CaretIndex = text.Length;
        }
        finally { _settingText = false; }
        Placeholder.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderActions()
    {
        _armed = null;
        var level = _levels[^1];
        var filter = QueryBox.Text.Trim().ToLowerInvariant();
        bool top = _levels.Count == 1;
        Placeholder.Text = level.Input is { } prompt ? prompt.InputPlaceholder ?? "Type…" : "Filter actions…";

        List<ActionRow> rows;
        if (level.Input is { } input)
        {
            IReadOnlyList<ResultAction> built;
            try { built = input.Input!(QueryBox.Text); }
            catch (Exception e)
            {
                Log.Error($"input {input.Title}", e);
                built = [];
            }
            rows = built.Select((action, i) => new ActionRow(action, i == 0 && !action.IsInert, null)).ToList();
        }
        else if (filter.Length == 0)
        {
            rows = level.Actions.Select((action, i) => new ActionRow(action, top && i == 0, null)).ToList();
        }
        else
        {
            rows = level.Actions
                .Select((action, i) => (Action: action, Index: i, Match: Fuzzy.Match(filter, action.Title)))
                .Where(entry => entry.Match.Found)
                .OrderByDescending(entry => entry.Match)
                .ThenBy(entry => entry.Index)
                .Select(entry => new ActionRow(entry.Action, top && entry.Index == 0, Fuzzy.FindPositions(filter, entry.Action.Title)))
                .ToList();
        }

        ActionList.ItemsSource = rows;
        if (rows.Count > 0) ActionList.SelectedIndex = 0;
        CrumbText.Text = string.Join("  ›  ", _levels.Select(l => l.Title));
        UpdateFooter();
    }

    private void UpdateActionFooter()
    {
        bool input = _levels.Count > 0 && _levels[^1].Input is not null;
        if (ActionList.SelectedItem is not ActionRow row)
        {
            Footer.Text = input ? "Type, then ↵   Esc back" : "No matching actions   ← back   Esc back";
            return;
        }
        if (row.Action.IsInert)
        {
            Footer.Text = "Esc back";
            return;
        }
        if (row.IsArmed)
        {
            Footer.Text = "↵ again to confirm   any other key cancels";
            return;
        }
        var parts = new List<string> { row.Action.IsSubmenu ? "↵ or → open" : "↵ run" };
        if (row.Action.Shifted is { } shifted) parts.Add("Shift+↵ " + shifted.Title);
        // ← only goes back with an empty box; in a text-entry level it moves the caret otherwise.
        if (!input || QueryBox.Text.Length == 0) parts.Add("← back");
        parts.Add("Esc back");
        Footer.Text = string.Join("   ", parts);
    }

    /// <summary>Runs an action: opens a submenu, arms a confirmation, or hides the launcher and runs it.</summary>
    private async Task Execute(ResultItem item, ResultAction action, ActionRow? row)
    {
        if (action.IsInert) return;
        if (action.IsSubmenu)
        {
            if (!InActionMode) return;
            OpenSubmenu(action);
            return;
        }
        if (action.Flags.HasFlag(ActionFlags.Confirm))
        {
            // Only from the list, and only on a second press of the same row.
            if (row is null) return;
            if (!ReferenceEquals(_armed, row))
            {
                if (_armed is not null) _armed.IsArmed = false;
                _armed = row;
                row.IsArmed = true;
                UpdateFooter();
                return;
            }
        }

        var wm = _wm;
        if (action.Flags.HasFlag(ActionFlags.KeepOpen))
        {
            await _actions.Run(item, action, wm);
            if (IsVisible) AfterKeepOpen(action);
            return;
        }
        HideLauncher(restoreFocus: action.Flags.HasFlag(ActionFlags.RestoreFocus));
        await _actions.Run(item, action, wm);
    }

    /// <summary>After an action that keeps the launcher open: search its follow-up query (e.g. a new alias, to show
    /// it working), or rebuild the action list, whose rows may have changed (e.g. a removed alias).</summary>
    private void AfterKeepOpen(ResultAction action)
    {
        if (action.ThenQuery is { } query)
        {
            CloseActions();
            SetQueryText(query);
            Refresh(keepSelection: false);
            return;
        }
        if (!InActionMode) { Refresh(keepSelection: true); return; }

        try { foreach (var level in _levels) level.Load(); }
        catch (Exception e) { Log.Error("rebuilding the action list", e); }
        // A submenu that's now empty (its last alias removed) closes.
        while (_levels.Count > 1 && _levels[^1].Input is null && _levels[^1].Actions.Count == 0) _levels.RemoveAt(_levels.Count - 1);
        SetQueryText("");
        RenderActions();
    }

    /// <summary>Enter on the result list: a matching shortcut (Ctrl+↵, …) or the default action;
    /// Shift picks the default's "stay" variant.</summary>
    private void HandleEnter(ModifierKeys modifiers)
    {
        if (InActionMode)
        {
            if (ActionList.SelectedItem is not ActionRow row || _actionItem is not { } owner) return;
            var action = modifiers.HasFlag(ModifierKeys.Shift) && row.Action.Shifted is { } shifted ? shifted : row.Action;
            _ = Execute(owner, action, row);
            return;
        }

        var actions = SelectedActions(out var item);
        if (item is null || actions.Count == 0) return;
        var chord = KeyChord.From(Key.Enter, modifiers);
        var chosen = chord.Modifiers == ModifierKeys.None ? null : ResultAction.Find(actions, chord, allowConfirm: false);
        chosen ??= chord.Modifiers.HasFlag(ModifierKeys.Shift) && actions[0].Shifted is { } stay ? stay : actions[0];
        _ = Execute(item, chosen, null);
    }

    /// <summary>A Ctrl chord: runs the matching action of the selected result (or of the open list).</summary>
    private bool TryShortcut(Key key, ModifierKeys modifiers)
    {
        var chord = KeyChord.From(key, modifiers);
        if (!chord.Modifiers.HasFlag(ModifierKeys.Control)) return false;

        ResultItem? item;
        IReadOnlyList<ResultAction> actions;
        if (InActionMode) { item = _actionItem; actions = _levels[^1].Actions; }
        else actions = SelectedActions(out item);
        if (item is null || ResultAction.Find(actions, chord, allowConfirm: false) is not { } action) return false;
        _ = Execute(item, action, null);
        return true;
    }

    // ---- input -----------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    /// <summary>Launcher keys; returns false for keys the search box should get.</summary>
    private bool HandleKey(Key key, ModifierKeys mods)
    {
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);

        // A pending confirmation only survives a second Enter.
        if (_armed is not null && key is not (Key.Enter or Key.LeftShift or Key.RightShift))
        {
            _armed.IsArmed = false;
            _armed = null;
            UpdateFooter();
            if (key == Key.Escape) return true;
        }

        switch (key)
        {
            case Key.Escape:
                if (InActionMode) Back();
                else HideLauncher(restoreFocus: true);
                return true;
            case Key.Down: Move(1, wrap: true); return true;
            case Key.Up: Move(-1, wrap: true); return true;
            case Key.Tab: Move(shift ? -1 : 1, wrap: true); return true;
            case Key.PageDown: Move(8); return true;
            case Key.PageUp: Move(-8); return true;
            case Key.Enter:
                HandleEnter(mods);
                return true;
            case Key.K when ctrl && !shift:
                OpenActions();
                return true;
            case Key.Right when !ctrl && QueryBox.SelectionLength == 0 && QueryBox.CaretIndex == QueryBox.Text.Length:
                if (!InActionMode) OpenActions();
                else if (ActionList.SelectedItem is ActionRow { Action.IsSubmenu: true } row) OpenSubmenu(row.Action);
                else return false;
                return true;
            case Key.Left when InActionMode && QueryBox.Text.Length == 0:
                Back();
                return true;
        }

        return ctrl && TryShortcut(key, mods);
    }

    /// <summary>Moves the selection. With <paramref name="wrap"/> (↑ ↓ Tab), stepping past either end goes to the other
    /// end; Page Up / Page Down stop at the ends.</summary>
    private void Move(int delta, bool wrap = false)
    {
        var list = InActionMode ? ActionList : Results;
        int count = list.Items.Count;
        if (count == 0) return;
        int next = list.SelectedIndex + delta;
        list.SelectedIndex = wrap ? ((next % count) + count) % count : Math.Clamp(next, 0, count - 1);
        list.ScrollIntoView(list.SelectedItem);
        UpdateFooter();
    }

    private void Results_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ResultItem item) return;
        Results.SelectedItem = item;
        HandleEnter(Keyboard.Modifiers);
        e.Handled = true;
    }

    private void ActionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ActionRow row) return;
        ActionList.SelectedItem = row;
        HandleEnter(Keyboard.Modifiers);
        e.Handled = true;
    }
}

/// <summary>One row of the action list.</summary>
internal sealed class ActionRow(ResultAction action, bool isDefault, int[]? matches) : INotifyPropertyChanged
{
    private bool _armed;

    public ResultAction Action => action;
    public string Glyph => action.Glyph;
    public string Detail => action.Detail ?? "";
    public Visibility DetailVisibility => Detail.Length > 0 && !IsArmed ? Visibility.Visible : Visibility.Collapsed;
    public bool IsDestructive => action.Flags.HasFlag(ActionFlags.Destructive);
    public bool IsInert => action.IsInert;

    public string ShortcutLabel => action.IsSubmenu
        ? (action.Hint is { } hint ? hint + "  ›" : "›")
        : action.IsInert ? ""
        : action.Shortcut?.Label ?? action.Hint ?? (isDefault ? "↵" : "");
    public Visibility ShortcutVisibility => ShortcutLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public HighlightedText TitleText => IsArmed
        ? new(action.ConfirmTitle ?? "Press ↵ again: " + action.Title, null)
        : new(action.Title, matches);

    public bool IsArmed
    {
        get => _armed;
        set
        {
            _armed = value;
            PropertyChanged?.Invoke(this, new(nameof(IsArmed)));
            PropertyChanged?.Invoke(this, new(nameof(TitleText)));
            PropertyChanged?.Invoke(this, new(nameof(DetailVisibility)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
