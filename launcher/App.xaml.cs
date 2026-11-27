using System.IO.Pipes;
using System.Windows;
using System.Windows.Interop;

namespace GlazeLauncher;

/// <summary>
/// Resident launcher process. The first instance owns the hotkey and the window; later
/// invocations (<c>GlazeLauncher.exe --toggle|--show|--hide|--reindex|--quit</c>) forward their
/// command over a named pipe and exit.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\GlazeLauncher.Instance";
    private const string PipeName = "GlazeLauncher";
    private const int HotkeyId = 0x4C31;

    // Alt+Space. GlazeWM must not bind it (its keyboard hook would swallow it) and
    // PowerToys Run must be off or rebound.
    private const uint HotkeyModifiers = Native.MOD_ALT | Native.MOD_NOREPEAT;
    private const uint HotkeyVk = 0x20; // VK_SPACE

    // Workspace keys, owned here instead of in GlazeWM's config: no process start per key press, and the
    // target monitor follows the foreground window or the cursor (see WorkspaceGroups.TargetMonitor).
    private const int FocusSlotHotkeyBase = 0x5100; // + slot: Alt+1…9
    private const int MoveSlotHotkeyBase = 0x5110;  // + slot: Alt+Shift+1…9
    private const int SyncHotkeyId = 0x5120;        // Ctrl+Alt+R
    private bool _workspaceHotkeys;

    private Mutex? _mutex;
    private MainWindow? _window;
    private WorkspaceManager? _workspaces;
    private GlazeClient? _glaze;
    private UsageStore? _usage;
    private ShellIndex? _shellIndex;
    private readonly CancellationTokenSource _cts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var command = ParseCommand(e.Args);
        // A replacement for a crashed instance: wait for the old one to go instead of talking to it.
        bool restart = e.Args.Contains("--restart");

        if (!restart && Mutex.TryOpenExisting(MutexName, out var existing))
        {
            existing.Dispose();
            SendToRunningInstance(command ?? "toggle");
            Shutdown();
            return;
        }

        // Started by GlazeWM's shell-exec (or anything else that spawns with handle inheritance on),
        // we could hold copies of the parent's handles, e.g. its IPC socket, and keep them alive after
        // the parent exits; Zebar's collector hit exactly this. Relaunch once via ShellExecute,
        // which inherits nothing, and let that copy be the resident instance.
        if (!e.Args.Contains("--detached"))
        {
            var args = command is null ? "--detached" : $"--detached --{command}";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true })?.Dispose();
            Shutdown();
            return;
        }

        bool owner;
        if (restart)
        {
            // The crashed process still holds the mutex for a moment; it is abandoned when the process ends.
            // If another instance keeps holding it (it was already restarted), leave quietly.
            _mutex = new Mutex(false, MutexName);
            try { owner = _mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owner = true; }
            if (!owner)
            {
                Log.Info($"restart (pid {Environment.ProcessId}): another instance is running, exiting");
                Shutdown();
                return;
            }
        }
        else
        {
            _mutex = new Mutex(true, MutexName, out owner);
            if (!owner)
            {
                SendToRunningInstance(command ?? "toggle");
                Shutdown();
                return;
            }
        }

        Log.Init();
        Log.Info($"starting (pid {Environment.ProcessId}{(restart ? ", after a crash" : "")})");

        // A small list window doesn't need the GPU. Software rendering keeps the GPU driver
        // (tens of MB on NVIDIA) out of the process; drawing stays well under a frame.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        InstallCrashHandling();

        var sta = new StaWorker("shell");
        var icons = new IconCache(sta);
        var shellIndex = _shellIndex = new ShellIndex(sta);
        _usage = new UsageStore();
        _usage.Load();
        _glaze = new GlazeClient();
        var identity = new WindowIdentity();
        var actions = new Actions(_glaze, _usage, identity);
        var search = new SearchEngine(
            [
                new AppSearchProvider(shellIndex, identity, actions),
                new WindowSearchProvider(identity, actions),
                new BrowserTabSearchProvider(_glaze, identity, actions),
                new FolderSearchProvider(shellIndex),
                new CommandSearchProvider(actions),
                new WorkspaceSearchProvider(actions),
            ],
            _usage);

        _workspaces = new WorkspaceManager(_glaze);

        _window = new MainWindow(_glaze, shellIndex, search, actions, icons);
        var hwnd = _window.Prepare();
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        RegisterHotkeyWithRetry(hwnd);
        // The workspace keys only mean something while GlazeWM runs; otherwise leave Alt+digits to other apps.
        _glaze.ConnectionChanged += connected => Dispatcher.BeginInvoke(() => SetWorkspaceHotkeys(hwnd, connected));

        actions.InternalCommand += cmd => Dispatcher.BeginInvoke(() => Execute(cmd));

        _glaze.Start();
        // Warm the icons in the background, with known folders ahead of the larger app catalog.
        _ = shellIndex.Refresh().ContinueWith(_ =>
        {
            foreach (var folder in shellIndex.Folders) icons.Request(folder.Path, knownFolderId: folder.Id);
            foreach (var app in shellIndex.Apps) icons.Request(app.IconPath);
        });
        _ = Task.Run(() => PipeServer(_cts.Token));

        if (command is "show" or "toggle") _window.ShowLauncher();
    }

    /// <summary>
    /// Keeps the launcher up. Errors on the UI thread are logged and skipped. If they repeat quickly,
    /// the UI is probably stuck in a failing state, so the launcher replaces itself with a fresh copy.
    /// An error on another thread ends the process; the launcher starts a fresh copy first.
    /// Both only happen if this instance ran for at least a minute, so a launcher that fails at
    /// startup doesn't restart in a loop. A normal Quit is not a crash and is not restarted.
    /// Windows' own restart is registered as well, for crashes in native code, but it only works where
    /// Windows Error Reporting is on (it is off on the development PC).
    /// </summary>
    private void InstallCrashHandling()
    {
        var recentUiErrors = new Queue<DateTime>();
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled (UI thread)", args.Exception);
            args.Handled = true;
            var now = DateTime.UtcNow;
            recentUiErrors.Enqueue(now);
            while (recentUiErrors.Count > 0 && now - recentUiErrors.Peek() > UiErrorWindow) recentUiErrors.Dequeue();
            if (recentUiErrors.Count < UiErrorLimit) return;

            Log.Error($"{UiErrorLimit} UI errors within {UiErrorWindow.TotalSeconds:0} s, replacing this instance");
            _usage?.Save();
            StartReplacement();
            Environment.Exit(1);
        };

        // Exceptions on other threads end the process; record why and start a replacement before it goes.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Error($"unhandled (background, terminating={args.IsTerminating})", args.ExceptionObject as Exception);
            if (args.IsTerminating) StartReplacement();
        };

        // A fire-and-forget task that failed; it doesn't end the process, but shouldn't go unnoticed.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // "--detached": the restarted copy is started by Windows, not GlazeWM, so it has no handles to drop.
        // No restart after a reboot or an update: GlazeWM starts the launcher at login.
        // "--restart": if our own replacement got there first, this copy exits quietly instead of toggling it.
        int hr = Native.RegisterApplicationRestart("--detached --restart", Native.RESTART_NO_REBOOT | Native.RESTART_NO_PATCH);
        if (hr != 0) Log.Info($"RegisterApplicationRestart failed: 0x{hr:X8}");
    }

    private const int UiErrorLimit = 5;
    private static readonly TimeSpan UiErrorWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinUptimeForRestart = TimeSpan.FromSeconds(60);
    /// <summary>Starts a fresh copy that takes over once this process has ended.
    /// ShellExecute, so the copy inherits none of our handles.</summary>
    private static void StartReplacement()
    {
        TimeSpan uptime;
        using (var self = System.Diagnostics.Process.GetCurrentProcess()) uptime = DateTime.Now - self.StartTime;
        if (uptime < MinUptimeForRestart)
        {
            Log.Error($"not restarting: the launcher failed {uptime.TotalSeconds:0} s after starting");
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--detached --restart")
            {
                UseShellExecute = true,
            })?.Dispose();
            Log.Info("started a replacement instance");
        }
        catch (Exception e) { Log.Error("could not start a replacement instance", e); }
    }

    /// <summary>Registers Alt+Space; if another program holds it (PowerToys Run), retries every
    /// 5 s so the launcher takes over as soon as it is released, without a restart.</summary>
    private void RegisterHotkeyWithRetry(IntPtr hwnd)
    {
        if (Native.RegisterHotKey(hwnd, HotkeyId, HotkeyModifiers, HotkeyVk)) { Log.Info("hotkey Alt+Space registered"); return; }
        Log.Error($"RegisterHotKey(Alt+Space) failed, error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}: " +
                  "another program owns it (PowerToys Run?). Retrying every 5 s; 'GlazeLauncher.exe --toggle' works meanwhile.");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            if (!Native.RegisterHotKey(hwnd, HotkeyId, HotkeyModifiers, HotkeyVk)) return;
            timer.Stop();
            Log.Info("hotkey Alt+Space registered (after retry)");
        };
        timer.Start();
    }

    private void SetWorkspaceHotkeys(IntPtr hwnd, bool on)
    {
        if (on == _workspaceHotkeys) return;
        _workspaceHotkeys = on;
        var failed = new List<string>();
        for (int slot = 1; slot <= 9; slot++)
        {
            uint vk = (uint)('0' + slot);
            if (on)
            {
                if (!Native.RegisterHotKey(hwnd, FocusSlotHotkeyBase + slot, Native.MOD_ALT | Native.MOD_NOREPEAT, vk)) failed.Add($"Alt+{slot}");
                if (!Native.RegisterHotKey(hwnd, MoveSlotHotkeyBase + slot, Native.MOD_ALT | Native.MOD_SHIFT | Native.MOD_NOREPEAT, vk)) failed.Add($"Alt+Shift+{slot}");
            }
            else
            {
                Native.UnregisterHotKey(hwnd, FocusSlotHotkeyBase + slot);
                Native.UnregisterHotKey(hwnd, MoveSlotHotkeyBase + slot);
            }
        }
        if (on && !Native.RegisterHotKey(hwnd, SyncHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x52)) failed.Add("Ctrl+Alt+R");
        if (!on) Native.UnregisterHotKey(hwnd, SyncHotkeyId);

        if (failed.Count > 0) Log.Error($"workspace hotkeys held by another program: {string.Join(", ", failed)}");
        else Log.Info(on ? "workspace hotkeys registered" : "workspace hotkeys released (GlazeWM disconnected)");
    }

    private static string? ParseCommand(string[] args)
    {
        foreach (var a in args)
        {
            var s = a.TrimStart('-', '/').ToLowerInvariant();
            if (s is "toggle" or "show" or "hide" or "quit" or "reindex" or "sync") return s;
        }
        return null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (id == HotkeyId) _window?.Toggle();
        else if (id is > FocusSlotHotkeyBase and <= FocusSlotHotkeyBase + 9) _ = _workspaces?.FocusSlot(id - FocusSlotHotkeyBase);
        else if (id is > MoveSlotHotkeyBase and <= MoveSlotHotkeyBase + 9) _ = _workspaces?.MoveToSlot(id - MoveSlotHotkeyBase);
        else if (id == SyncHotkeyId) _ = _workspaces?.SyncNow();
        else return IntPtr.Zero;
        handled = true;
        return IntPtr.Zero;
    }

    private void Execute(string command)
    {
        Log.Info($"command: {command}");
        switch (command)
        {
            case "toggle": _window?.Toggle(); break;
            case "show": _window?.ShowLauncher(); break;
            case "hide": _window?.HideLauncher(restoreFocus: true); break;
            case "reindex": _ = ReindexAsync(); break;
            case "sync": _ = _workspaces?.SyncNow(); break;
            // Debug helpers (pipe only): the Alt+N and Alt+Shift+N actions, without the keyboard.
            case var f when f.StartsWith("ws:focus:", StringComparison.Ordinal) && int.TryParse(f[9..], out int fs) && fs is >= 1 and <= 9:
                _ = _workspaces?.FocusSlot(fs); break;
            case var m when m.StartsWith("ws:move:", StringComparison.Ordinal) && int.TryParse(m[8..], out int ms) && ms is >= 1 and <= 9:
                _ = _workspaces?.MoveToSlot(ms); break;
            case "quit": Quit(); break;
            // Debug helpers (pipe only): type a query / render the window to snapshot.png.
            case "snapshot": _window?.SaveSnapshot(Paths.File("snapshot.png")); break;
            case var q when q.StartsWith("query:", StringComparison.Ordinal): _window?.SetQuery(q[6..]); break;
            case "enter" or "enter:ctrl" or "enter:shift": _window?.DebugEnter(command.EndsWith("ctrl"), command.EndsWith("shift")); break;
            // Crash-handling tests (pipe only): an error on the UI thread, which is logged and skipped,
            // and one on a background thread, which ends the process like a real crash.
            case "debug:throw":
                Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("debug:throw test")));
                break;
            case "debug:crash":
                new Thread(() => throw new InvalidOperationException("debug:crash test")) { IsBackground = true }.Start();
                break;
        }
    }

    private Task ReindexAsync() => _shellIndex?.Refresh() ?? Task.CompletedTask;

    private void Quit()
    {
        Log.Info("quitting");
        _cts.Cancel();
        _glaze?.Stop();
        _usage?.Save();
        if (_window is not null) Native.UnregisterHotKey(_window.Handle, HotkeyId);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _usage?.Save();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ---- single-instance pipe --------------------------------------------------

    private async Task PipeServer(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                var line = (await reader.ReadLineAsync(ct))?.Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(line)) await Dispatcher.InvokeAsync(() => Execute(line));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { Log.Error("pipe", e); await Task.Delay(500, CancellationToken.None); }
        }
    }

    private static void SendToRunningInstance(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(command);
        }
        catch (Exception e) { Log.Error($"send '{command}' to running instance", e); }
    }
}
