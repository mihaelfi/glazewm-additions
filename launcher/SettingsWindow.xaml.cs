using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace GlazeLauncher;

/// <summary>
/// The launcher's settings, in a normal window (the launcher popup closes when it loses focus, so it can't hold
/// them). Opened by the "Launcher settings" command, Ctrl+, in the launcher, or <c>GlazeLauncher.exe --settings</c>.
/// Pages: Open with (rules in settings.json), Aliases (settings.json through <see cref="AliasBook"/>), File index
/// (GlazeIndex's own settings, through its pipe). Every change is saved at once; there is no Save button.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>A list row: title, a muted line, an optional tag at the right, and what it stands for.</summary>
    public sealed record Row(string Title, string Subtitle, string? Tag = null, object? Value = null)
    {
        public Visibility TagVisibility => string.IsNullOrEmpty(Tag) ? Visibility.Collapsed : Visibility.Visible;
    }

    public sealed record PageItem(string Key, string Title, string Glyph);

    private readonly SettingsStore _settings;
    private readonly AliasBook _aliases;
    private IndexClient _index = new(); // its own back-off: a new one on Refresh retries at once
    private List<OpenWithRule> _rules = [];
    private bool _loading;
    private bool _indexLoaded;

    internal SettingsWindow(SettingsStore settings, AliasBook aliases)
    {
        _settings = settings;
        _aliases = aliases;
        InitializeComponent();
        Pages.ItemsSource = new List<PageItem>
        {
            new("openwith", "Open with", OpenWith.Glyph),
            new("aliases", "Aliases", ""),
            new("index", "File index", ""),
        };
        FileHint.Text = "Edit settings.json ›";
        SourceInitialized += (_, _) => Native.DarkTitleBar(new WindowInteropHelper(this).Handle);
        _settings.Changed += OnSettingsChanged;
        Closed += (_, _) => _settings.Changed -= OnSettingsChanged;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !IsTyping) Close(); };
        LoadRules(0);
        LoadAliases();
        Pages.SelectedIndex = 0;
    }

    /// <summary>Shows a page by key ("openwith", "aliases", "index").</summary>
    public void ShowPage(string key)
    {
        if (Pages.ItemsSource is List<PageItem> pages && pages.FindIndex(p => p.Key == key) is >= 0 and var i) Pages.SelectedIndex = i;
    }

    private bool IsTyping => Keyboard.FocusedElement is TextBox;

    /// <summary>settings.json changed (a hand edit, the launcher, or our own save): show it, unless a field is being typed in.</summary>
    private void OnSettingsChanged(LauncherSettings _) => Dispatcher.BeginInvoke(() =>
    {
        if (!IsLoaded || IsTyping) return;
        LoadRules(null);
        LoadAliases();
    });

    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var key = (Pages.SelectedItem as PageItem)?.Key;
        OpenWithPage.Visibility = key == "openwith" ? Visibility.Visible : Visibility.Collapsed;
        AliasPage.Visibility = key == "aliases" ? Visibility.Visible : Visibility.Collapsed;
        IndexPage.Visibility = key == "index" ? Visibility.Visible : Visibility.Collapsed;
        if (key == "index" && !_indexLoaded) _ = LoadIndexAsync();
    }

    private void FileHint_Click(object sender, MouseButtonEventArgs e)
    {
        try { Actions.OpenPath(_settings.FilePath); }
        catch (Exception ex) { Log.Error("open settings.json", ex); }
    }

    private void Field_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter commits a field: leaving it runs its LostFocus save.
        if (e.Key == Key.Enter) { Keyboard.ClearFocus(); e.Handled = true; }
    }

    // ==================================== Open with ====================================

    private static OpenWithRule Copy(OpenWithRule r) => new()
    {
        Name = r.Name, Exe = r.Exe, Arguments = r.Arguments, Extensions = [.. r.Extensions], Enter = r.Enter, Unknown = r.Unknown,
    };

    private static string Types(OpenWithRule r) => r.Extensions.Count == 0 ? "no file types yet" : string.Join(" ", r.Extensions);

    private void LoadRules(int? select)
    {
        _loading = true;
        int wanted = select ?? RuleList.SelectedIndex;
        _rules = _settings.Current.OpenWith.Select(Copy).ToList();
        RuleList.ItemsSource = _rules.Select(r => new Row(r.Name,
            $"{Types(r)}  ·  {(File.Exists(r.Exe) ? r.Exe : "not found: " + r.Exe)}", r.Enter ? "↵ Enter" : null)).ToList();
        RuleList.Visibility = _rules.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RuleEmpty.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RuleList.SelectedIndex = _rules.Count == 0 ? -1 : Math.Clamp(wanted, 0, _rules.Count - 1);
        _loading = false;
        ShowRule();
    }

    private OpenWithRule? SelectedRule => RuleList.SelectedIndex is >= 0 and var i && i < _rules.Count ? _rules[i] : null;

    private void RuleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) ShowRule();
    }

    private void ShowRule()
    {
        var rule = SelectedRule;
        RuleEditor.Visibility = rule is null ? Visibility.Collapsed : Visibility.Visible;
        RemoveRule.IsEnabled = rule is not null;
        RuleUp.IsEnabled = RuleList.SelectedIndex > 0;
        RuleDown.IsEnabled = rule is not null && RuleList.SelectedIndex < _rules.Count - 1;
        if (rule is null) return;
        _loading = true;
        RuleName.Text = rule.Name;
        RuleExe.Text = rule.Exe;
        RuleArgs.Text = rule.Arguments;
        RuleExts.Text = string.Join(", ", rule.Extensions);
        RuleEnter.IsChecked = rule.Enter;
        _loading = false;
    }

    /// <summary>Saves the whole list as shown; refused while settings.json has a JSON error.</summary>
    private bool SaveRules(string done)
    {
        int selected = RuleList.SelectedIndex;
        bool saved = _settings.Update(s => s.OpenWith = _rules.Select(Copy).ToList());
        RuleStatus.Text = saved ? done : "Not saved: settings.json has a JSON error. Fix it first (Edit settings.json, at the bottom left).";
        LoadRules(selected);
        return saved;
    }

    private void RuleField_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRule();

    private void RuleEnter_Click(object sender, RoutedEventArgs e) => CommitRule();

    private void CommitRule()
    {
        if (_loading || SelectedRule is not { } rule) return;
        var exe = RuleExe.Text.Trim().Trim('"');
        var notes = new List<string>();
        if (exe.Length == 0)
        {
            RuleStatus.Text = "A program is needed; the old one is kept.";
            RuleExe.Text = rule.Exe;
            exe = rule.Exe;
        }
        else if (!File.Exists(exe)) notes.Add($"{exe} doesn't exist (yet)");

        var types = new List<string>();
        foreach (var part in RuleExts.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (OpenWithRule.NormalizeExtension(part) is { } ext) { if (!types.Contains(ext)) types.Add(ext); }
            else notes.Add($"“{part}” isn't a file type");
        }
        var name = RuleName.Text.Trim();
        var updated = new OpenWithRule
        {
            Name = name.Length > 0 ? name : Path.GetFileNameWithoutExtension(exe),
            Exe = exe,
            Arguments = RuleArgs.Text.Trim().Length == 0 ? OpenWithRule.DefaultArguments : RuleArgs.Text.Trim(),
            Extensions = types,
            Enter = RuleEnter.IsChecked == true,
            Unknown = rule.Unknown,
        };
        if (updated.Name == rule.Name && updated.Exe == rule.Exe && updated.Arguments == rule.Arguments
            && updated.Enter == rule.Enter && updated.Extensions.SequenceEqual(rule.Extensions))
        {
            if (notes.Count > 0) RuleStatus.Text = string.Join("; ", notes) + ".";
            return;
        }
        if (updated.Enter && updated.Extensions.Count == 0) notes.Add("add file types for Enter to apply");
        _rules[RuleList.SelectedIndex] = updated;
        SaveRules(notes.Count > 0 ? "Saved. " + string.Join("; ", notes) + "." : $"Saved {updated.Name}.");
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the program",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        };
        if (dialog.ShowDialog(this) != true) return;
        var exe = dialog.FileName;
        string name = Path.GetFileNameWithoutExtension(exe);
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            var described = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
            if (!string.IsNullOrWhiteSpace(described) && described.Length <= 40) name = described.Trim();
        }
        catch (Exception) { }
        _rules.Add(new OpenWithRule { Name = name, Exe = exe });
        if (SaveRules($"Added {name}. Now type the file types it should open."))
        {
            RuleList.SelectedIndex = _rules.Count - 1;
            RuleExts.Focus();
        }
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is not { } rule) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the program",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            FileName = rule.Exe,
            InitialDirectory = Path.GetDirectoryName(rule.Exe) is { } d && Directory.Exists(d) ? d : "",
        };
        if (dialog.ShowDialog(this) != true) return;
        RuleExe.Text = dialog.FileName;
        CommitRule();
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRule is not { } rule) return;
        _rules.RemoveAt(RuleList.SelectedIndex);
        SaveRules($"Removed {rule.Name}.");
    }

    private void RuleUp_Click(object sender, RoutedEventArgs e) => MoveRule(-1);
    private void RuleDown_Click(object sender, RoutedEventArgs e) => MoveRule(1);

    private void MoveRule(int delta)
    {
        int i = RuleList.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _rules.Count) return;
        (_rules[i], _rules[j]) = (_rules[j], _rules[i]);
        RuleList.SelectedIndex = j; // SaveRules keeps the selection
        SaveRules("Order saved.");
    }

    // ==================================== Aliases ====================================

    private void LoadAliases()
    {
        var selected = (AliasList.SelectedItem as Row)?.Title;
        var rows = _settings.Current.Aliases.OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => new Row(a.Key, "→ " + (_aliases.Describe(a.Value) ?? $"{a.Value} (not found)"), KindOf(a.Value), a.Value))
            .ToList();
        AliasList.ItemsSource = rows;
        AliasList.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AliasEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        int index = selected is null ? -1 : rows.FindIndex(r => r.Title == selected);
        AliasList.SelectedIndex = index >= 0 ? index : rows.Count > 0 ? 0 : -1;
        ShowAlias();
    }

    private static string KindOf(string target) =>
        target.StartsWith("app:", StringComparison.Ordinal) ? "app"
        : target.StartsWith("folder:", StringComparison.Ordinal) ? "folder"
        : target.StartsWith("cmd:", StringComparison.Ordinal) ? "command" : "";

    private void AliasList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowAlias();

    private void ShowAlias()
    {
        var row = AliasList.SelectedItem as Row;
        AliasEditor.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        if (row is not null) AliasName.Text = row.Title;
    }

    private void AliasName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RenameAlias(); e.Handled = true; }
    }

    private void RenameAlias_Click(object sender, RoutedEventArgs e) => RenameAlias();

    private void RenameAlias()
    {
        if (AliasList.SelectedItem is not Row { Value: string target } row) return;
        if (AliasBook.Validate(AliasName.Text, out var alias) is { } error) { AliasStatus.Text = error; return; }
        if (alias.Length == 0 || alias == row.Title) { AliasStatus.Text = ""; return; }
        if (!_aliases.CanSave) { AliasStatus.Text = "Not saved: settings.json has a JSON error. Fix it first."; return; }
        if (_aliases.TargetOf(alias) is { } owner && owner != target)
        {
            AliasStatus.Text = $"“{alias}” is already the alias of {_aliases.Describe(owner) ?? owner}; remove it there first.";
            return;
        }
        _aliases.Remove(row.Title);
        _aliases.Add(alias, target);
        AliasStatus.Text = $"Renamed “{row.Title}” to “{alias}”.";
        Keyboard.ClearFocus();
        LoadAliases();
        if (AliasList.ItemsSource is List<Row> rows && rows.FindIndex(r => r.Title == alias) is >= 0 and var i) AliasList.SelectedIndex = i;
    }

    private void RemoveAlias_Click(object sender, RoutedEventArgs e)
    {
        if (AliasList.SelectedItem is not Row row) return;
        AliasStatus.Text = _aliases.Remove(row.Title) ? $"Removed “{row.Title}”." : "Not saved: settings.json has a JSON error. Fix it first.";
        Keyboard.ClearFocus();
        LoadAliases();
    }

    // ==================================== File index ====================================

    private static bool Ok(JsonObject reply) => reply["ok"]?.GetValue<bool>() == true;
    private static string Error(JsonObject reply) => reply["error"]?.ToString() ?? "failed";

    private void RefreshIndex_Click(object sender, RoutedEventArgs e)
    {
        _index = new IndexClient();
        _ = LoadIndexAsync();
    }

    private async Task LoadIndexAsync(string? message = null)
    {
        _indexLoaded = true;
        IndexMessage.Text = message ?? "";
        var status = await _index.StatusAsync();
        if (!Ok(status))
        {
            IndexBody.Visibility = Visibility.Collapsed;
            IndexStatus.Text = $"{Error(status)}. The index service GlazeIndex runs as a Windows service; install or start it " +
                               "from an administrator terminal (GlazeIndex.exe --install), then press Refresh.";
            return;
        }
        var settings = await _index.SettingsAsync();
        IndexBody.Visibility = Visibility.Visible;
        long memory = status["private_memory"]?.GetValue<long>() ?? 0;
        IndexStatus.Text = $"GlazeIndex {status["version"]} · {Format.Size(memory)} in memory · settings in {status["settings_file"]}. " +
                           "Changes apply at once, for every user of this PC.";

        DriveList.Children.Clear();
        foreach (var node in status["drives"]?.AsArray() ?? [])
        {
            if (node is not JsonObject d) continue;
            DriveList.Children.Add(DriveRow(d));
        }

        if (Ok(settings) && settings["settings"] is JsonObject s)
        {
            var excluded = (s["excluded"]?.AsArray() ?? []).Select(n => n?.ToString() ?? "").Where(p => p.Length > 0).ToList();
            ExcludedList.ItemsSource = excluded.Select(p => new Row(p, Directory.Exists(p) ? "" : "not found", Value: p)).ToList();
            ExcludedList.Visibility = excluded.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            ExcludedEmpty.Visibility = excluded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Demoted.Text = string.Join(", ", (s["demoted"]?.AsArray() ?? []).Select(n => n?.ToString()));
            DemotedAtRoot.Text = string.Join(", ", (s["demoted_at_root"]?.AsArray() ?? []).Select(n => n?.ToString()));
            SearchHidden.IsChecked = s["search_hidden"]?.GetValue<bool>() == true;
        }
        else IndexMessage.Text = $"Couldn't read the settings: {Error(settings)}";
    }

    private CheckBox DriveRow(JsonObject d)
    {
        string drive = d["drive"]?.ToString() ?? "?";
        string label = d["label"]?.ToString() ?? "";
        bool enabled = d["enabled"]?.GetValue<bool>() == true;
        bool supported = d["supported"]?.GetValue<bool>() ?? true;
        string state = d["state"]?.ToString() ?? "";
        var facts = new List<string>();
        if (d["file_system"]?.ToString() is { Length: > 0 } fs) facts.Add(fs + (d["usb"]?.GetValue<bool>() == true ? " · USB" : ""));
        if (d["total"]?.GetValue<long>() is > 0 and var total)
            facts.Add($"{Format.Size(d["free"]?.GetValue<long>() ?? 0)} free of {Format.Size(total)}");
        if (enabled && state.Length > 0) facts.Add(state);
        if (d["entries"]?.GetValue<long>() is > 0 and var entries) facts.Add($"{entries:N0} entries");
        if (!supported && d["reason"]?.ToString() is { Length: > 0 } reason) facts.Add(reason);

        var title = new TextBlock { Text = label.Length > 0 && label != drive ? $"{drive}  {label}" : drive, FontSize = 14 };
        var detail = new TextBlock { Text = string.Join("  ·  ", facts), FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush") };
        var box = new CheckBox
        {
            IsChecked = enabled,
            IsEnabled = supported && state != "not connected",
            Margin = new Thickness(0, 5, 0, 5),
            Content = new StackPanel { Children = { title, detail } },
        };
        box.Click += async (_, _) =>
        {
            bool on = box.IsChecked == true;
            IndexMessage.Text = on ? $"Turning on {drive}…" : $"Turning off {drive}…";
            var reply = await _index.SetDriveAsync(drive, on);
            await LoadIndexAsync(Ok(reply) ? (on ? $"{drive} is on; it is being scanned." : $"{drive} is off; its index was dropped.")
                : $"Couldn't change {drive}: {Error(reply)}");
        };
        return box;
    }

    private async void AddExcluded_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to leave out of search" };
        if (dialog.ShowDialog(this) != true) return;
        var reply = await _index.SetExcludedAsync(dialog.FolderName, true);
        await LoadIndexAsync(Ok(reply) ? $"{dialog.FolderName} is left out of search." : $"Couldn't exclude it: {Error(reply)}");
    }

    private async void RemoveExcluded_Click(object sender, RoutedEventArgs e)
    {
        if (ExcludedList.SelectedItem is not Row { Value: string path }) { IndexMessage.Text = "Select a folder first."; return; }
        var reply = await _index.SetExcludedAsync(path, false);
        await LoadIndexAsync(Ok(reply) ? $"{path} shows in search again." : $"Couldn't include it: {Error(reply)}");
    }

    private static JsonArray Names(string text) =>
        [.. text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(n => (JsonNode)n)];

    private async void SaveDemoted_Click(object sender, RoutedEventArgs e)
    {
        var reply = await _index.SetSettingsAsync(new JsonObject
        {
            ["demoted"] = Names(Demoted.Text),
            ["demoted_at_root"] = Names(DemotedAtRoot.Text),
        });
        await LoadIndexAsync(Ok(reply) ? "Saved; search ranks with the new names now." : $"Couldn't save: {Error(reply)}");
    }

    private async void SearchHidden_Click(object sender, RoutedEventArgs e)
    {
        bool on = SearchHidden.IsChecked == true;
        var reply = await _index.SetSettingsAsync(new JsonObject { ["search_hidden"] = on });
        await LoadIndexAsync(Ok(reply) ? (on ? "Hidden and system files show in search." : "Hidden and system files are left out of search.")
            : $"Couldn't save: {Error(reply)}");
    }

    // ==================================== debug ====================================

    /// <summary>Renders the window to a PNG (the pipe's <c>snapshot:settings</c>), like the launcher's own snapshot.</summary>
    public void SaveSnapshot(string path)
    {
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(bg);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
        Log.Info($"settings snapshot saved: {path}");
    }
}
