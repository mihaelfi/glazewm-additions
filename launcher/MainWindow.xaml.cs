using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlazeLauncher;

public partial class MainWindow : Window
{
    private const double WidthDip = 720;

    private readonly GlazeClient _glaze;
    private readonly AppIndex _apps;
    private readonly SearchEngine _search;
    private readonly Actions _actions;
    private readonly IconCache _icons;
    private readonly WindowIdentity _identity;

    private WmSnapshot _wm = WmSnapshot.Empty;
    private IntPtr _hwnd;
    private IntPtr _previousForeground;
    private bool _hiding;

    internal MainWindow(GlazeClient glaze, AppIndex apps, SearchEngine search, Actions actions, IconCache icons, WindowIdentity identity)
    {
        _glaze = glaze;
        _apps = apps;
        _search = search;
        _actions = actions;
        _icons = icons;
        _identity = identity;
        InitializeComponent();

        Deactivated += (_, _) => HideLauncher(restoreFocus: false);
        PreviewKeyDown += OnPreviewKeyDown;
        _apps.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(keepSelection: true); });
    }

    public IntPtr Handle => _hwnd;

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
        _wm = _glaze.Snapshot;
        QueryBox.Text = "";
        Refresh(keepSelection: false);

        PlaceOn(_wm.FocusedMonitor);
        Show();
        Activate();
        Native.ForceForeground(_hwnd);
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
        Log.Info($"shown on ws {_wm.FocusedWorkspace?.Name ?? "?"}, foreground={Native.GetForegroundWindow() == _hwnd}");

        // The snapshot is kept current by IPC events, but titles can change without an event:
        // refresh once in the background and re-render without moving the selection.
        _ = RefreshSnapshotAsync();
        _ = _apps.RefreshIfStale();
    }

    private async Task RefreshSnapshotAsync()
    {
        if (!_glaze.IsConnected) return;
        try
        {
            var wm = await _glaze.RefreshAsync();
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsVisible) return;
                _wm = wm;
                Refresh(keepSelection: true);
            });
        }
        catch (Exception e) { Log.Info($"snapshot refresh: {e.Message}"); }
    }

    public void HideLauncher(bool restoreFocus)
    {
        if (_hiding || !IsVisible) return;
        _hiding = true;
        try
        {
            Hide();
            if (restoreFocus && _previousForeground != IntPtr.Zero && Native.IsWindow(_previousForeground))
                Native.SetForegroundWindow(_previousForeground);
        }
        finally { _hiding = false; }
    }

    /// <summary>Centres the window horizontally on the monitor GlazeWM has focused, a fifth of the way down.</summary>
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

    public void DebugEnter(bool ctrl, bool shift) =>
        _ = ActivateSelected(CurrentQuery.WorkspaceToken, alternate: ctrl, stay: shift);

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
        Refresh(keepSelection: false);
    }

    private Query CurrentQuery => Query.Parse(QueryBox.Text);

    private void Refresh(bool keepSelection)
    {
        var query = CurrentQuery;
        var previous = keepSelection ? Results.SelectedIndex : 0;
        var items = _search.Search(query, _wm);
        foreach (var item in items) LoadIcon(item);

        Results.ItemsSource = items;
        Results.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (items.Count > 0)
        {
            Results.SelectedIndex = Math.Clamp(previous, 0, items.Count - 1);
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
        string? path = item.Kind switch
        {
            ResultKind.App => item.App!.IconPath,
            ResultKind.Window => _identity.ExePath(item.Window!),
            _ => null,
        };
        if (path is null) return;
        if (_icons.TryGet(path, out var cached)) { item.Icon = cached; return; }
        _icons.Request(path, img => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => item.Icon = img));
    }

    private void UpdateFooter()
    {
        var item = Results.SelectedItem as ResultItem;
        Footer.Text = item?.Kind switch
        {
            ResultKind.App when item.RunningWindows.Count > 0 => "↵ focus open window   Ctrl+↵ new window   Ctrl+1‒9 open on workspace (+Shift: stay)   Esc close",
            ResultKind.App => "↵ open   Ctrl+1‒9 or @N open on workspace (+Shift: stay)   Esc close",
            ResultKind.Window => "↵ focus   Ctrl+↵ bring here   Ctrl+1‒9 or @N move to workspace (+Shift: stay)   Esc close",
            ResultKind.Command => "↵ run   Esc close",
            _ => "Type to search apps, open windows and commands   Esc close",
        };
    }

    // ---- input -----------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                HideLauncher(restoreFocus: true);
                e.Handled = true;
                return;
            case Key.Down: Move(1); e.Handled = true; return;
            case Key.Up: Move(-1); e.Handled = true; return;
            case Key.Tab: Move(shift ? -1 : 1); e.Handled = true; return;
            case Key.PageDown: Move(8); e.Handled = true; return;
            case Key.PageUp: Move(-8); e.Handled = true; return;
            case Key.Enter:
                _ = ActivateSelected(targetToken: CurrentQuery.WorkspaceToken, alternate: ctrl, stay: shift);
                e.Handled = true;
                return;
        }

        // Ctrl+1..9: open/move the selection to slot N on this monitor's workspace group.
        if (ctrl && e.Key is (>= Key.D1 and <= Key.D9) or (>= Key.NumPad1 and <= Key.NumPad9))
        {
            int digit = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad0 : e.Key - Key.D0;
            _ = ActivateSelected(targetToken: digit.ToString(), alternate: false, stay: shift);
            e.Handled = true;
        }
    }

    private void Move(int delta)
    {
        if (Results.Items.Count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, Results.Items.Count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
        UpdateFooter();
    }

    private void Results_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not ResultItem item) return;
        Results.SelectedItem = item;
        var mods = Keyboard.Modifiers;
        _ = ActivateSelected(CurrentQuery.WorkspaceToken, mods.HasFlag(ModifierKeys.Control), mods.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    private async Task ActivateSelected(string? targetToken, bool alternate, bool stay)
    {
        if (Results.SelectedItem is not ResultItem item) return;
        var wm = _wm;
        var target = targetToken is null ? null : wm.ResolveWorkspace(targetToken);
        HideLauncher(restoreFocus: false);
        await _actions.Activate(item, new ActivateOptions(target, alternate, stay), wm);
    }
}
