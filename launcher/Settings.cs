using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlazeLauncher;

/// <summary>
/// The user's launcher settings, <c>%LOCALAPPDATA%\GlazeLauncher\settings.json</c>. Treat an instance as
/// read-only: <see cref="SettingsStore.Update"/> works on a copy and swaps it in.
/// </summary>
internal sealed class LauncherSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The launcher hotkey. Read, not applied yet (Alt+Space is fixed for now).</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>Extra name → a result's stable ID (its usage key, e.g. <c>app:…</c> or <c>folder:…</c>). Not used yet.</summary>
    public Dictionary<string, string> Aliases { get; set; } = [];

    /// <summary>Folders whose items become results. Not used yet.</summary>
    public List<IndexedFolder> IndexedFolders { get; set; } = [];

    /// <summary>Keys this build doesn't know, kept so saving never drops them.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

internal sealed class IndexedFolder
{
    public string Path { get; set; } = "";
    public string? Badge { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
}

/// <summary>
/// Loads and saves <see cref="LauncherSettings"/>. A missing file is written with defaults, a broken one is
/// logged and left alone (the last good settings stay in use), saves are atomic, and hand edits are picked
/// up through a file watcher (no polling).
/// </summary>
internal sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        // Readable for hand editing ("Alt+Space", not "Alt+Space"); the file is never embedded in HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private string? _lastText;
    private bool _fileBroken;

    public SettingsStore(string? path = null) => _path = path ?? Paths.File("settings.json");

    public string FilePath => _path;

    public LauncherSettings Current { get; private set; } = new();

    /// <summary>Raised (on a background thread) after a reload changed the settings.</summary>
    public event Action<LauncherSettings>? Changed;

    public void Load()
    {
        if (!File.Exists(_path))
        {
            Log.Info($"settings: {_path} not found, writing defaults");
            Save(Current);
            return;
        }
        Reload(raise: false);
    }

    /// <summary>Starts watching the file for hand edits.</summary>
    public void Watch()
    {
        var dir = Path.GetDirectoryName(_path)!;
        _debounce = new Timer(_ => Reload(raise: true), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler changed = (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.Changed += changed;
        _watcher.Created += changed;
        _watcher.Renamed += (_, _) => _debounce.Change(300, Timeout.Infinite);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Changes a copy of the settings, saves it and makes it current. Refused (false) while the file
    /// on disk is broken, so a hand edit in progress is never overwritten.</summary>
    public bool Update(Action<LauncherSettings> change)
    {
        LauncherSettings next;
        lock (_gate)
        {
            if (_fileBroken)
            {
                Log.Info("settings: not saving, because settings.json has a JSON error; fix it first");
                return false;
            }
            next = JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(Current, Json), Json) ?? new();
            change(next);
        }
        Save(next);
        Changed?.Invoke(next);
        return true;
    }

    private void Reload(bool raise)
    {
        string text;
        try { text = File.ReadAllText(_path); }
        catch (FileNotFoundException) { return; }
        catch (Exception e)
        {
            Log.Info($"settings: read failed: {e.Message}");
            return;
        }

        LauncherSettings loaded;
        lock (_gate)
        {
            if (text == _lastText) return; // our own write, or an edit that changed nothing
            try
            {
                loaded = Normalize(JsonSerializer.Deserialize<LauncherSettings>(text, Json) ?? new());
            }
            catch (JsonException e)
            {
                // Keep the last good settings and leave the file for the user to fix.
                Log.Info($"settings: {_path} is not valid JSON ({e.Message}); keeping the previous settings");
                _fileBroken = true;
                return;
            }
            _fileBroken = false;
            _lastText = text;
            Current = loaded;
        }

        if (loaded.Version > LauncherSettings.CurrentVersion)
            Log.Info($"settings: version {loaded.Version} is newer than this build ({LauncherSettings.CurrentVersion}); unknown keys are kept");
        Log.Info($"settings: loaded ({loaded.Aliases.Count} aliases, {loaded.IndexedFolders.Count} indexed folders, hotkey {loaded.Hotkey})");
        if (raise) Changed?.Invoke(loaded);
    }

    private void Save(LauncherSettings settings)
    {
        var text = JsonSerializer.Serialize(settings, Json);
        lock (_gate)
        {
            try
            {
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, text);
                File.Move(tmp, _path, overwrite: true);
                _lastText = text;
                Current = settings;
            }
            catch (Exception e) { Log.Error("settings save", e); }
        }
    }

    /// <summary>Fills nulls a hand edit may leave (e.g. <c>"aliases": null</c>) and makes alias lookups case-insensitive.</summary>
    private static LauncherSettings Normalize(LauncherSettings s)
    {
        s.Hotkey ??= "Alt+Space";
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, target) in s.Aliases ?? [])
        {
            if (!string.IsNullOrWhiteSpace(alias) && !string.IsNullOrWhiteSpace(target)) aliases[alias.Trim()] = target;
        }
        s.Aliases = aliases;
        s.IndexedFolders = (s.IndexedFolders ?? []).Where(f => f is not null && !string.IsNullOrWhiteSpace(f.Path)).ToList();
        return s;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
