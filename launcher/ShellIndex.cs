using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Management.Deployment;

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

internal sealed class ShellIndex(StaWorker worker)
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(2);
    private static readonly Regex PackagedAppId = new(
        @"^([a-z0-9.-]{3,50}_[a-hj-km-np-tv-z0-9]{13})!.+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
    public IReadOnlyList<KnownFolder> Folders { get; private set; } = [];
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
            var (apps, folders) = await worker.Invoke(() => (EnumerateApps(), KnownFolder.Enumerate()));
            Apps = apps;
            Folders = folders;
            _loadedAt = DateTime.UtcNow;
            Log.Info($"indexed {apps.Count} apps and {folders.Count} folders in {sw.ElapsedMilliseconds} ms");
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("shell index", e);
        }
    }

    private static List<AppEntry> EnumerateApps()
    {
        var result = new List<AppEntry>(400);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installedFamilies = GetInstalledPackageFamilies();

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
                if (IsNoise(name, id) || IsStoreListing(id)) continue;

                var packaged = PackagedAppId.Match(id);
                if (packaged.Success && installedFamilies is not null && !installedFamilies.Contains(packaged.Groups[1].Value))
                    continue;

                string? target = null;
                try { target = item.ExtendedProperty("System.Link.TargetParsingPath") as string; } catch { }
                if (string.IsNullOrWhiteSpace(target)) target = null;
                if ((!packaged.Success && IsStoreListing(target)) || !seen.Add(id)) continue;

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

    private static HashSet<string>? GetInstalledPackageFamilies()
    {
        try
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manager = new PackageManager();
            bool supportsStubStatus = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
            foreach (var package in manager.FindPackagesForUserWithPackageTypes("", PackageTypes.Main | PackageTypes.Optional))
            {
                if (!supportsStubStatus || !package.IsStub) installed.Add(package.Id.FamilyName);
            }
            return installed;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        {
            // Preserve search results if Windows cannot supply the current user's package inventory.
            Log.Error("installed package inventory", error);
            return null;
        }
    }

    private static bool IsStoreListing(string? path)
    {
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "ms-windows-store") return uri.Host.Equals("pdp", StringComparison.OrdinalIgnoreCase);
        return uri.Scheme is "https" or "http" && uri.Host.Equals("apps.microsoft.com", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/detail/", StringComparison.OrdinalIgnoreCase);
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
