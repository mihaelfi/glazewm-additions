using System.Diagnostics;

namespace GlazeLauncher;

/// <summary>An entry from shell:AppsFolder (Start menu shortcuts + packaged apps, one list).</summary>
internal sealed class AppEntry
{
    public required string Name { get; init; }
    /// <summary>AppsFolder parsing name: an AUMID (e.g. "Chrome", "Pkg_hash!App") or a known-folder path.</summary>
    public required string Id { get; init; }
    public string? TargetPath { get; init; }

    /// <summary>Lower-case identifiers used to match this app to a running window
    /// (AUMID, exe name without extension, display name without spaces).</summary>
    public required HashSet<string> MatchIds { get; init; }

    public string IconPath => @"shell:AppsFolder\" + Id;
    public string UsageKey => "app:" + Id;
}

internal sealed class AppIndex(StaWorker worker)
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);

    // Packaged apps whose process name can't be derived from the AppsFolder entry.
    private static readonly Dictionary<string, string> PackagedProcessAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.WindowsNotepad"] = "notepad",
        ["Microsoft.WindowsTerminal"] = "windowsterminal",
        ["Microsoft.WindowsCalculator"] = "calculatorapp",
        ["Microsoft.Paint"] = "mspaint",
    };

    private DateTime _loadedAt = DateTime.MinValue;
    private Task? _loading;

    public IReadOnlyList<AppEntry> Apps { get; private set; } = [];
    public event Action? Changed;

    public Task RefreshIfStale()
    {
        if (DateTime.UtcNow - _loadedAt < Stale) return Task.CompletedTask;
        return Refresh();
    }

    public Task Refresh()
    {
        if (_loading is { IsCompleted: false }) return _loading;
        _loading = RefreshCore();
        return _loading;
    }

    private async Task RefreshCore()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var apps = await worker.Invoke(Enumerate);
            Apps = apps;
            _loadedAt = DateTime.UtcNow;
            Log.Info($"indexed {apps.Count} apps in {sw.ElapsedMilliseconds} ms");
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("app index", e);
        }
    }

    private static List<AppEntry> Enumerate()
    {
        var result = new List<AppEntry>(400);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var shellType = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application missing");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name ?? "";
                string id = item.Path ?? "";
                if (name.Length == 0 || id.Length == 0) continue;
                if (IsNoise(name, id)) continue;
                if (!seen.Add(id)) continue;

                string? target = null;
                try { target = item.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch { }
                if (string.IsNullOrWhiteSpace(target)) target = null;

                result.Add(new AppEntry { Name = name, Id = id, TargetPath = target, MatchIds = BuildMatchIds(name, id, target) });
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static bool IsNoise(string name, string id)
    {
        if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains(" uninstall", StringComparison.OrdinalIgnoreCase)) return true;
        if (id.EndsWith(".chm", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || id.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static HashSet<string> BuildMatchIds(string name, string id, string? target)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id.ToLowerInvariant() };
        ids.Add(NameKey(name));

        string? exe = target ?? (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? id : null);
        if (exe is not null && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            // Squirrel installers (Discord, Slack, ...) point at Update.exe; it is not the app's process.
            if (stem != "update") ids.Add(stem);
        }

        int bang = id.IndexOf('!');
        if (bang > 0)
        {
            // "Publisher.Product_hash!App" -> family "Publisher.Product"
            var family = id[..bang];
            int us = family.IndexOf('_');
            if (us > 0) family = family[..us];
            if (PackagedProcessAliases.TryGetValue(family, out var alias)) ids.Add(alias);
            int dot = family.LastIndexOf('.');
            ids.Add((dot >= 0 ? family[(dot + 1)..] : family).ToLowerInvariant());
        }
        return ids;
    }

    public static string NameKey(string s)
    {
        Span<char> buf = stackalloc char[Math.Min(s.Length, 256)];
        int n = 0;
        foreach (var c in s)
        {
            if (n == buf.Length) break;
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        }
        return new string(buf[..n]);
    }
}
