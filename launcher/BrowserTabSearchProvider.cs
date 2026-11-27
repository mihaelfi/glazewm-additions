using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace GlazeLauncher;

internal sealed class BrowserTabSearchProvider(GlazeClient glaze, WindowIdentity identity, Actions actions) : IRefreshableSearchProvider
{
    private sealed record BrowserTab(WmWindow Host, uint ProcessId, string RuntimeId, string Title);

    private static readonly Condition BrowserContainers = new OrCondition(
        WithType(ControlType.Pane), WithType(ControlType.Group), WithType(ControlType.Custom),
        WithType(ControlType.ToolBar), WithType(ControlType.TitleBar), WithType(ControlType.Tab));
    private static readonly Condition TabItems = WithType(ControlType.TabItem);

    private IReadOnlyList<BrowserTab> _tabs = [];
    private WmSnapshot? _requestedSnapshot;
    private Task? _refresh;

    public int Priority => 72;

    public Task RefreshAsync(WmSnapshot snapshot)
    {
        _requestedSnapshot = snapshot;
        return _refresh is { IsCompleted: false } ? _refresh : _refresh = RefreshPendingAsync();
    }

    private async Task RefreshPendingAsync()
    {
        while (_requestedSnapshot is { } snapshot)
        {
            _requestedSnapshot = null;
            _tabs = await Task.Run(() => ReadTabs(snapshot));
        }
    }

    public IEnumerable<ProviderResult> Search(SearchContext context)
    {
        if (context.IsHome || context.Query.WorkspaceToken is not null || _tabs.Count == 0) yield break;

        var windows = context.Wm.Windows.ToDictionary(window => window.Id, StringComparer.Ordinal);
        foreach (var tab in _tabs)
        {
            var match = Fuzzy.Match(context.Query.Text, tab.Title);
            if (!match.Found || !windows.TryGetValue(tab.Host.Id, out var host) || host.Handle != tab.Host.Handle) continue;

            yield return new ProviderResult(new ResultItem
            {
                Id = $"tab:{host.Id}:{tab.RuntimeId}",
                Title = tab.Title,
                Subtitle = $"{BrowserName(host.ProcessName)} tab",
                Badge = "ws " + context.Wm.LabelFor(host.WorkspaceName),
                UsageKey = "browser-tabs:" + host.ProcessName.ToLowerInvariant(),
                IconPath = identity.ExePath(host),
                Glyph = "\uE774",
                Actions = ActionKits.Single("Switch to tab", "\uE774", () => ActivateTabAsync(tab)),
            }, match, host);
        }
    }

    private static List<BrowserTab> ReadTabs(WmSnapshot snapshot)
    {
        var tabs = new List<BrowserTab>();
        foreach (var host in snapshot.Windows)
        {
            if (BrowserName(host.ProcessName) is null || host.Handle == 0) continue;

            try
            {
                var hwnd = new IntPtr(host.Handle);
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) continue;
                foreach (AutomationElement tab in FindTabs(hwnd))
                {
                    var title = tab.Cached.Name.Trim();
                    if (title.Length == 0 || RuntimeId(tab) is not { } runtimeId) continue;
                    tabs.Add(new BrowserTab(host, pid, runtimeId, title));
                }
            }
            catch (Exception e) when (BrowserUnavailable(e)) { }
        }
        return tabs;
    }

    private async Task ActivateTabAsync(BrowserTab tab)
    {
        var host = glaze.Snapshot.Windows.FirstOrDefault(window => window.Id == tab.Host.Id && window.Handle == tab.Host.Handle);
        if (host is null) return;

        await actions.FocusWindow(host);
        if (!await Task.Run(() => SelectTab(tab))) Log.Info("browser tab is no longer available");
    }

    private static bool SelectTab(BrowserTab target)
    {
        try
        {
            var hwnd = new IntPtr(target.Host.Handle);
            Native.GetWindowThreadProcessId(hwnd, out uint currentPid);
            if (currentPid != target.ProcessId) return false;

            foreach (AutomationElement tab in FindTabs(hwnd))
            {
                if (RuntimeId(tab) != target.RuntimeId) continue;
                if (!tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)) return false;
                ((SelectionItemPattern)pattern).Select();
                return true;
            }
        }
        catch (Exception e) when (BrowserUnavailable(e)) { }
        return false;
    }

    private static IEnumerable<AutomationElement> FindTabs(IntPtr hwnd)
    {
        var strip = FindTabStrip(hwnd);
        if (strip is null) return [];

        var cache = new CacheRequest();
        cache.Add(AutomationElement.NameProperty);
        cache.Add(AutomationElement.RuntimeIdProperty);
        using (cache.Activate()) return strip.FindAll(TreeScope.Descendants, TabItems).Cast<AutomationElement>();
    }

    private static AutomationElement? FindTabStrip(IntPtr hwnd)
    {
        var cache = new CacheRequest();
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.ClassNameProperty);
        using (cache.Activate())
        {
            var pending = new Queue<(AutomationElement Node, int Depth)>();
            pending.Enqueue((AutomationElement.FromHandle(hwnd), 0));
            // Walk the browser's chrome only. Document controls and the page content beneath them are skipped.
            for (int visited = 0; visited < 180 && pending.TryDequeue(out var entry); visited++)
            {
                var node = entry.Node;
                if (node.Cached.ControlType == ControlType.Tab)
                {
                    var className = node.Cached.ClassName;
                    if (className is "TabContainer" or "EdgeVerticalTabContainerView"
                        || className.EndsWith("TabStripRegionView", StringComparison.Ordinal))
                        return node;
                }
                else if (entry.Depth < 12)
                {
                    foreach (AutomationElement child in node.FindAll(TreeScope.Children, BrowserContainers))
                        pending.Enqueue((child, entry.Depth + 1));
                }
            }
        }
        return null;
    }

    private static string? RuntimeId(AutomationElement tab) =>
        tab.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) is int[] { Length: > 0 } id
            ? string.Join('.', id)
            : null;

    private static Condition WithType(ControlType type) => new PropertyCondition(AutomationElement.ControlTypeProperty, type);

    private static string? BrowserName(string processName) => processName.ToLowerInvariant() switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        _ => null,
    };

    private static bool BrowserUnavailable(Exception error) =>
        error is ElementNotAvailableException or COMException or ArgumentException or InvalidOperationException or UnauthorizedAccessException;
}
