using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GlazeLauncher;

/// <summary>
/// Brings back windows a dead GlazeWM left hidden.
/// <para>
/// GlazeWM hides the windows of workspaces that aren't displayed by cloaking them through the shell. When GlazeWM
/// dies, its watcher process shows them and puts them back on the taskbar but does not uncloak them, and a new
/// GlazeWM skips cloaked windows. So they stay invisible and unmanaged: on the taskbar, but out of every workspace
/// and out of the launcher. The launcher outlives GlazeWM, so it remembers the windows GlazeWM managed and uncloaks
/// the ones left behind: a few seconds after GlazeWM's process is gone, and again when a new GlazeWM connects
/// without them (which then adopts them like newly opened windows).
/// </para>
/// </summary>
internal sealed class WindowRescue
{
    private const int DWM_CLOAKED_SHELL = 2;
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(3);

    private readonly GlazeClient _glaze;
    private readonly Dispatcher _ui;
    private readonly Lock _gate = new();
    /// <summary>Handles the last GlazeWM managed, kept from its disconnect until the next instance is connected.</summary>
    private long[] _lastManaged = [];

    public WindowRescue(GlazeClient glaze, Dispatcher ui)
    {
        _glaze = glaze;
        _ui = ui;
        glaze.ConnectionChanged += OnConnectionChanged;
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!connected)
        {
            // The snapshot still holds the last tree the dead (or restarting) GlazeWM reported.
            var handles = _glaze.Snapshot.Windows.Select(w => w.Handle).Where(h => h != 0).ToArray();
            lock (_gate) _lastManaged = handles;
            _ = RescueAfterExitAsync();
            return;
        }

        long[] left;
        lock (_gate)
        {
            var managed = _glaze.Snapshot.Windows.Select(w => w.Handle).ToHashSet();
            left = [.. _lastManaged.Where(h => !managed.Contains(h))];
            _lastManaged = [];
        }
        // Not managed by the GlazeWM that's running now, so nothing has a reason to keep them cloaked.
        if (left.Length > 0) _ui.InvokeAsync(() => Rescue(left, "a new GlazeWM skipped them"));
    }

    private async Task RescueAfterExitAsync()
    {
        await Task.Delay(ExitGrace);
        // A dropped connection to a GlazeWM that's still running is not an exit: its hidden workspaces must stay hidden.
        if (_glaze.IsConnected || IsGlazeWmRunning()) return;
        long[] handles;
        lock (_gate) handles = _lastManaged;
        if (handles.Length > 0) await _ui.InvokeAsync(() => Rescue(handles, "GlazeWM exited"));
    }

    private static bool IsGlazeWmRunning()
    {
        var processes = Process.GetProcessesByName("glazewm");
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Uncloaks each window that still exists, is shell-cloaked and is on the current virtual desktop
    /// (windows on other virtual desktops are cloaked the same way, by Windows). Runs on the UI (STA) thread.</summary>
    public static int Rescue(IReadOnlyCollection<long> handles, string reason)
    {
        int count = 0;
        var names = new List<string>();
        try
        {
            IApplicationViewCollection? views = null;
            var desktops = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager)!)!;
            foreach (long handle in handles)
            {
                var hwnd = new IntPtr(handle);
                if (!IsWindow(hwnd) || CloakReason(hwnd) != DWM_CLOAKED_SHELL) continue;
                if (desktops.IsWindowOnCurrentVirtualDesktop(hwnd, out int onCurrent) != 0 || onCurrent == 0) continue;

                views ??= ViewCollection();
                if (views.GetViewForHwnd(hwnd, out var view) != 0 || view is null) continue;
                int hr = view.SetCloak(1, 0);
                Marshal.ReleaseComObject(view);
                if (hr != 0) { Log.Info($"uncloak 0x{handle:X} failed: 0x{hr:X8}"); continue; }
                count++;
                names.Add(ProcessName(hwnd));
            }
        }
        catch (Exception e) { Log.Error("window rescue", e); }
        if (count > 0) Log.Info($"uncloaked {count} window(s) GlazeWM left hidden ({reason}): {string.Join(", ", names)}");
        return count;
    }

    private static IApplicationViewCollection ViewCollection()
    {
        var shell = (IServiceProvider)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ImmersiveShell)!)!;
        var iid = typeof(IApplicationViewCollection).GUID;
        Marshal.ThrowExceptionForHR(shell.QueryService(ref iid, ref iid, out var collection));
        return (IApplicationViewCollection)collection;
    }

    private static int CloakReason(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int reason, sizeof(int)) == 0 ? reason : 0;

    private static string ProcessName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch { return $"0x{hwnd.ToInt64():X}"; }
    }

    // ---- interop -------------------------------------------------------------
    // The view collection and SetCloak(1, 0) mirror GlazeWM's own set_cloaked (undocumented shell API).

    private static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrent);
    }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        void Slot3(); void Slot4(); void Slot5();
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
    }

    /// <summary>IInspectable's three methods, then SetFocus, SwitchTo, TryInvokeBack, GetThumbnailWindow, GetMonitor,
    /// GetVisibility, then SetCloak.</summary>
    [ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationView
    {
        void Slot3(); void Slot4(); void Slot5(); void Slot6(); void Slot7(); void Slot8(); void Slot9(); void Slot10(); void Slot11();
        [PreserveSig] int SetCloak(uint type, int flag);
    }
}
