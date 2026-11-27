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

    private Mutex? _mutex;
    private MainWindow? _window;
    private GlazeClient? _glaze;
    private UsageStore? _usage;
    private AppIndex? _apps;
    private readonly CancellationTokenSource _cts = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var command = ParseCommand(e.Args);

        if (Mutex.TryOpenExisting(MutexName, out var existing))
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

        _mutex = new Mutex(true, MutexName, out bool owner);
        if (!owner)
        {
            SendToRunningInstance(command ?? "toggle");
            Shutdown();
            return;
        }

        Log.Init();
        Log.Info($"starting (pid {Environment.ProcessId})");

        // A small list window doesn't need the GPU. Software rendering keeps the GPU driver
        // (tens of MB on NVIDIA) out of the process; drawing stays well under a frame.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        DispatcherUnhandledException += (_, args) => { Log.Error("unhandled", args.Exception); args.Handled = true; };

        var sta = new StaWorker("shell");
        var icons = new IconCache(sta);
        var apps = _apps = new AppIndex(sta);
        _usage = new UsageStore();
        _usage.Load();
        _glaze = new GlazeClient();
        var identity = new WindowIdentity();
        var actions = new Actions(_glaze, _usage, identity);
        var search = new SearchEngine(apps, _usage, identity);

        _window = new MainWindow(_glaze, apps, search, actions, icons, identity);
        var hwnd = _window.Prepare();
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        RegisterHotkeyWithRetry(hwnd);

        actions.InternalCommand += cmd => Dispatcher.BeginInvoke(() => Execute(cmd));

        _glaze.Start();
        // Index apps, then warm the icon cache so the first search already has icons.
        _ = apps.Refresh().ContinueWith(_ => { foreach (var a in apps.Apps) icons.Request(a.IconPath); });
        _ = Task.Run(() => PipeServer(_cts.Token));

        if (command is "show" or "toggle") _window.ShowLauncher();
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

    private static string? ParseCommand(string[] args)
    {
        foreach (var a in args)
        {
            var s = a.TrimStart('-', '/').ToLowerInvariant();
            if (s is "toggle" or "show" or "hide" or "quit" or "reindex") return s;
        }
        return null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            _window?.Toggle();
            handled = true;
        }
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
            case "quit": Quit(); break;
            // Debug helpers (pipe only): type a query / render the window to snapshot.png.
            case "snapshot": _window?.SaveSnapshot(Paths.File("snapshot.png")); break;
            case var q when q.StartsWith("query:", StringComparison.Ordinal): _window?.SetQuery(q[6..]); break;
            case "enter" or "enter:ctrl" or "enter:shift": _window?.DebugEnter(command.EndsWith("ctrl"), command.EndsWith("shift")); break;
        }
    }

    private Task ReindexAsync() => _apps?.Refresh() ?? Task.CompletedTask;

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
