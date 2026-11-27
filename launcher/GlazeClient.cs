using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GlazeLauncher;

public sealed record WmMonitor(int X, int Y, int Width, int Height, double Scale, bool HasFocus)
{
    /// <summary>The Windows primary monitor always has its top-left corner at (0,0).</summary>
    public bool IsPrimary => X == 0 && Y == 0;
}

public sealed record WmWorkspace(string Id, string Name, string Label, bool IsDisplayed, bool HasFocus, int MonitorIndex);

public sealed record WmWindow(string Id, long Handle, string Title, string ProcessName, string? WorkspaceName, bool HasFocus);

public sealed class WmSnapshot
{
    public static readonly WmSnapshot Empty = new([], [], []);

    public WmSnapshot(IReadOnlyList<WmMonitor> monitors, IReadOnlyList<WmWorkspace> workspaces, IReadOnlyList<WmWindow> windows)
    {
        Monitors = monitors;
        Workspaces = workspaces;
        Windows = windows;
        FocusedMonitor = monitors.FirstOrDefault(m => m.HasFocus) ?? monitors.FirstOrDefault();
        FocusedWorkspace = workspaces.FirstOrDefault(w => w.HasFocus)
            ?? workspaces.FirstOrDefault(w => w.IsDisplayed && FocusedMonitor is not null && monitors[w.MonitorIndex] == FocusedMonitor);
    }

    public IReadOnlyList<WmMonitor> Monitors { get; }
    public IReadOnlyList<WmWorkspace> Workspaces { get; }
    public IReadOnlyList<WmWindow> Windows { get; }
    public WmMonitor? FocusedMonitor { get; }
    public WmWorkspace? FocusedWorkspace { get; }

    /// <summary>
    /// Maps a typed workspace number to a workspace name, mirroring workspace.ps1:
    /// one digit means "slot N in the focused monitor's group" (1N on the primary monitor,
    /// 2N on the secondary); two digits are taken literally.
    /// </summary>
    public string ResolveWorkspace(string typed)
    {
        if (typed.Length != 1) return typed;
        bool secondary = Monitors.Count >= 2 && FocusedMonitor is { IsPrimary: false };
        return (secondary ? "2" : "1") + typed;
    }

    public string LabelFor(string? workspaceName) =>
        workspaceName is null ? "?" : Workspaces.FirstOrDefault(w => w.Name == workspaceName)?.Label ?? workspaceName;
}

public sealed class GlazeException(string message) : Exception(message);

/// <summary>
/// WebSocket client for GlazeWM's IPC server (ws://127.0.0.1:6123). Keeps a live snapshot of
/// monitors / workspaces / windows: it re-queries the tree (debounced) whenever GlazeWM pushes an
/// event, and forwards window_managed events so launches can be routed to a workspace.
/// </summary>
public sealed class GlazeClient
{
    private const string Url = "ws://127.0.0.1:6123";

    private static readonly string[] Events =
    [
        "window_managed", "window_unmanaged", "focus_changed", "focused_container_moved",
        "workspace_activated", "workspace_deactivated", "workspace_updated",
        "monitor_added", "monitor_updated", "monitor_removed",
    ];

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, ConcurrentQueue<TaskCompletionSource<JsonElement>>> _pending = new();
    private readonly Timer _refreshTimer;
    private ClientWebSocket? _ws;

    public GlazeClient() => _refreshTimer = new Timer(_ => _ = RefreshSafe(), null, Timeout.Infinite, Timeout.Infinite);

    public WmSnapshot Snapshot { get; private set; } = WmSnapshot.Empty;
    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>Raised on a thread-pool thread for every newly managed window.</summary>
    public event Action<WmWindow>? WindowManaged;

    public void Start() => _ = Task.Run(RunAsync);

    public void Stop() => _cts.Cancel();

    private async Task RunAsync()
    {
        bool loggedDown = false;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await ws.ConnectAsync(new Uri(Url), _cts.Token);
                _ws = ws;
                Log.Info("connected to GlazeWM IPC");
                loggedDown = false;

                var receive = ReceiveLoop(ws);
                await SendAsync("sub --events " + string.Join(' ', Events));
                await RefreshAsync();
                await receive;
            }
            catch (Exception e) when (!_cts.IsCancellationRequested)
            {
                if (!loggedDown) Log.Info($"GlazeWM IPC unavailable: {e.Message}");
                loggedDown = true;
            }
            finally
            {
                _ws = null;
                FailPending();
            }

            try { await Task.Delay(2000, _cts.Token); } catch (OperationCanceledException) { }
        }
    }

    private async Task ReceiveLoop(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, _cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            try { Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length)); }
            catch (Exception e) { Log.Error("ipc dispatch", e); }
            message.SetLength(0);
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("messageType").GetString();

        if (type == "client_response")
        {
            var key = root.GetProperty("clientMessage").GetString() ?? "";
            if (!_pending.TryGetValue(key, out var queue) || !queue.TryDequeue(out var tcs)) return;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                tcs.TrySetException(new GlazeException(err.GetString()!));
            else
                tcs.TrySetResult(root.TryGetProperty("data", out var data) ? data.Clone() : default);
        }
        else if (type == "event_subscription")
        {
            if (!root.TryGetProperty("data", out var data)) return;
            var eventType = data.GetProperty("eventType").GetString();
            if (eventType == "window_managed" && data.TryGetProperty("managedWindow", out var w))
            {
                var win = ParseWindow(w, null);
                try { WindowManaged?.Invoke(win); } catch (Exception e) { Log.Error("window_managed handler", e); }
            }
            _refreshTimer.Change(80, Timeout.Infinite);
        }
    }

    /// <summary>Sends a raw IPC message and awaits GlazeWM's reply.</summary>
    public async Task<JsonElement> SendAsync(string message, int timeoutMs = 2000)
    {
        var ws = _ws ?? throw new GlazeException("GlazeWM is not connected");
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending.GetOrAdd(message, _ => new()).Enqueue(tcs);

        await _sendLock.WaitAsync();
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, _cts.Token); }
        finally { _sendLock.Release(); }

        return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
    }

    /// <summary>Runs a WM command, optionally with a subject container (window/workspace id).
    /// Arguments must not be quoted: GlazeWM keeps quotes as part of the value.</summary>
    public Task<JsonElement> RunCommand(string command, string? subjectId = null) =>
        SendAsync(subjectId is null ? $"command {command}" : $"command --id {subjectId} {command}");

    public async Task<WmSnapshot> RefreshAsync()
    {
        var data = await SendAsync("query monitors");
        Snapshot = ParseSnapshot(data);
        return Snapshot;
    }

    private async Task RefreshSafe()
    {
        try { if (IsConnected) await RefreshAsync(); }
        catch (Exception e) { Log.Info($"refresh failed: {e.Message}"); }
    }

    private void FailPending()
    {
        foreach (var queue in _pending.Values)
            while (queue.TryDequeue(out var tcs)) tcs.TrySetException(new GlazeException("GlazeWM disconnected"));
    }

    // ---- parsing ---------------------------------------------------------------

    private static WmSnapshot ParseSnapshot(JsonElement data)
    {
        var monitors = new List<WmMonitor>();
        var workspaces = new List<WmWorkspace>();
        var windows = new List<WmWindow>();

        foreach (var m in data.GetProperty("monitors").EnumerateArray())
        {
            int index = monitors.Count;
            monitors.Add(new WmMonitor(
                m.GetProperty("x").GetInt32(), m.GetProperty("y").GetInt32(),
                m.GetProperty("width").GetInt32(), m.GetProperty("height").GetInt32(),
                m.TryGetProperty("scaleFactor", out var sf) ? sf.GetDouble() : 1.0,
                m.GetProperty("hasFocus").GetBoolean()));

            foreach (var ws in m.GetProperty("children").EnumerateArray())
            {
                if (ws.GetProperty("type").GetString() != "workspace") continue;
                var name = ws.GetProperty("name").GetString() ?? "";
                var display = ws.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : null;
                workspaces.Add(new WmWorkspace(
                    ws.GetProperty("id").GetString() ?? "", name, string.IsNullOrEmpty(display) ? name : display!,
                    ws.GetProperty("isDisplayed").GetBoolean(), ws.GetProperty("hasFocus").GetBoolean(), index));
                CollectWindows(ws, name, windows);
            }
        }
        return new WmSnapshot(monitors, workspaces, windows);
    }

    private static void CollectWindows(JsonElement node, string workspace, List<WmWindow> into)
    {
        if (!node.TryGetProperty("children", out var children)) return;
        foreach (var child in children.EnumerateArray())
        {
            var type = child.GetProperty("type").GetString();
            if (type == "window") into.Add(ParseWindow(child, workspace));
            else CollectWindows(child, workspace, into);
        }
    }

    private static WmWindow ParseWindow(JsonElement w, string? workspace) => new(
        w.GetProperty("id").GetString() ?? "",
        w.TryGetProperty("handle", out var h) ? h.GetInt64() : 0,
        w.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
        w.TryGetProperty("processName", out var p) ? p.GetString() ?? "" : "",
        workspace,
        w.TryGetProperty("hasFocus", out var f) && f.GetBoolean());
}
