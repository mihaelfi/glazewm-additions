using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace GlazeLauncher;

/// <summary>One file or folder the user opened recently (from Windows' Recent items).</summary>
internal sealed record RecentItem(string Path, bool Folder, DateTime Opened);

/// <summary>
/// Windows' own list of recently opened files and folders: the shortcuts in <c>%APPDATA%\Microsoft\Windows\Recent</c>,
/// which Explorer and most apps add to when you open something. It's what the user opened, unlike the index's recently
/// changed files, which are mostly app data. Read in the background, at most every few seconds; <see cref="Changed"/>
/// fires when a read finds something different.
/// </summary>
internal sealed class WindowsRecent
{
    private const int MaxItems = 40;
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(5);
    private static readonly string Folder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);

    private readonly Lock _gate = new();
    private IReadOnlyList<RecentItem> _items = [];
    private DateTime _readAt = DateTime.MinValue;
    private bool _reading;

    public event Action? Changed;

    /// <summary>The last read, newest first; starts a new read in the background when it's older than a few seconds.</summary>
    public IReadOnlyList<RecentItem> Items
    {
        get
        {
            lock (_gate)
            {
                if (!_reading && DateTime.UtcNow - _readAt > Fresh)
                {
                    _reading = true;
                    _ = Task.Run(Read);
                }
                return _items;
            }
        }
    }

    private void Read()
    {
        var items = new List<RecentItem>(MaxItems);
        try
        {
            var links = new DirectoryInfo(Folder).EnumerateFiles("*.lnk")
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxItems * 2);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in links)
            {
                // Shell items without a file system path (Control Panel, libraries) have no target path.
                if (Target(link.FullName) is not { Length: > 0 } path || !seen.Add(path)) continue;
                bool folder = Directory.Exists(path);
                if (!folder && !File.Exists(path)) continue;
                items.Add(new RecentItem(path, folder, link.LastWriteTime));
                if (items.Count == MaxItems) break;
            }
        }
        catch (Exception e) { Log.Error("reading Windows recent items", e); }

        bool changed;
        lock (_gate)
        {
            changed = !items.Select(i => i.Path).SequenceEqual(_items.Select(i => i.Path));
            _items = items;
            _readAt = DateTime.UtcNow;
            _reading = false;
        }
        if (changed)
        {
            Log.Info($"recent items: {items.Count} from {Folder}");
            Changed?.Invoke();
        }
    }

    private static bool _loggedFailure;

    private static string? Target(string lnk)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var path = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(path, path.Capacity, IntPtr.Zero, 0);
            return path.ToString();
        }
        catch (Exception e) when (e is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            if (!_loggedFailure) { _loggedFailure = true; Log.Info($"recent items: can't read {lnk}: {e.Message}"); }
            return null;
        }
        finally { if (link is not null) Marshal.ReleaseComObject(link); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    /// <summary>Only <c>GetPath</c>, the interface's first method, is declared.</summary>
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr findData, uint flags);
    }
}
