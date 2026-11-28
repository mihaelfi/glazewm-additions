using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GlazeLauncher;

/// <summary>
/// Ctrl+K › Open with › on file results: the user's own apps for the extension (<see cref="OpenWithRule"/> in
/// settings.json) first, then the apps Windows registers for it (<c>SHAssocEnumHandlers</c>, the list of Windows' own
/// Open with), then "Choose an app…", which searches the app catalog and indexed .exe files and can save the choice as
/// a rule (also as the Enter action for that extension). Built on the UI thread, like every action list; the Windows
/// handlers are COM objects of that thread, cached for a minute per extension.
/// </summary>
internal sealed class OpenWith(SettingsStore settings, ShellIndex shell, IndexClient index)
{
    /// <summary>Set at startup; <see cref="ActionKits.File"/> uses it (null: no Open with).</summary>
    public static OpenWith? Shared { get; set; }

    public const string Glyph = "\uE7AC"; // OpenWith
    private const string AppGlyph = "\uE8E5";
    private const int MaxHandlers = 12, MaxCandidates = 8;
    private static readonly TimeSpan HandlerCacheTime = TimeSpan.FromMinutes(1);

    /// <summary>New indexed .exe results for "Choose an app…" arrived (background thread): rebuild the rows.</summary>
    public event Action? CandidatesChanged;

    public SettingsStore Settings => settings;

    public static string ExtensionOf(string path) => Path.GetExtension(path).ToLowerInvariant();

    /// <summary>The user's rules for this file, in their order.</summary>
    public IReadOnlyList<OpenWithRule> RulesFor(string path)
    {
        var ext = ExtensionOf(path);
        return settings.Current.OpenWith.Where(r => (ext.Length > 0 && r.Extensions.Contains(ext)) || r.Extensions.Contains("*")).ToList();
    }

    /// <summary>The rule Enter uses for this file: the first with <see cref="OpenWithRule.Enter"/> for its extension,
    /// else the first for "*"; only when its program exists.</summary>
    public OpenWithRule? EnterRule(string path)
    {
        var ext = ExtensionOf(path);
        var rules = settings.Current.OpenWith;
        var rule = (ext.Length > 0 ? rules.FirstOrDefault(r => r.Enter && r.Extensions.Contains(ext)) : null)
                   ?? rules.FirstOrDefault(r => r.Enter && r.Extensions.Contains("*"));
        return rule is not null && File.Exists(rule.Exe) ? rule : null;
    }

    // ---- launching -----------------------------------------------------------------------------------

    /// <summary>Fills <c>{path}</c>, <c>{folder}</c> (a drive root as <c>D:\.</c>, so a closing quote isn't escaped)
    /// and <c>{name}</c>.</summary>
    public static string Expand(string arguments, string path)
    {
        var folder = Path.GetDirectoryName(path) ?? path;
        if (folder.EndsWith('\\')) folder += ".";
        return arguments.Replace("{path}", path, StringComparison.OrdinalIgnoreCase)
            .Replace("{folder}", folder, StringComparison.OrdinalIgnoreCase)
            .Replace("{name}", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
    }

    public static void Launch(string exe, string arguments, string path)
    {
        var args = Expand(string.IsNullOrWhiteSpace(arguments) ? OpenWithRule.DefaultArguments : arguments, path);
        Log.Info($"open with {exe} {args}");
        Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        })?.Dispose();
    }

    // ---- the action rows -----------------------------------------------------------------------------

    /// <summary>The first rows of a file's actions: "Open with X" (Enter) and "Open with default app" when a rule is
    /// the Enter action for it, otherwise <paramref name="open"/>.</summary>
    public IReadOnlyList<ResultAction> DefaultActions(string path, ResultAction open)
    {
        if (EnterRule(path) is not { } rule) return [open];
        return
        [
            new ResultAction($"Open with {rule.Name}", Glyph, () => { Launch(rule.Exe, rule.Arguments, path); return Task.CompletedTask; },
                Detail: "your Enter app"),
            open with { Title = "Open with default app" },
        ];
    }

    /// <summary>The "Open with ›" row.</summary>
    public ResultAction Menu(string path) =>
        new("Open with", Glyph, null, Children: () => Rows(path), Flags: ActionFlags.NoShortcuts);

    private IReadOnlyList<ResultAction> Rows(string path)
    {
        var ext = ExtensionOf(path);
        var rows = new List<ResultAction>();
        var ruleExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in RulesFor(path))
        {
            ruleExes.Add(rule.Exe);
            if (!File.Exists(rule.Exe))
            {
                rows.Add(new ResultAction(rule.Name, Glyph, null, Detail: $"not found: {rule.Exe}", Flags: ActionFlags.Disabled));
                continue;
            }
            var r = rule;
            rows.Add(new ResultAction(rule.Name, Glyph, () => { Launch(r.Exe, r.Arguments, path); return Task.CompletedTask; },
                Detail: rule.Enter && ReferenceEquals(EnterRule(path), rule) ? "your app · ↵ default" : "your app"));
        }
        foreach (var handler in HandlersFor(ext))
        {
            // Windows' entry for an app the user already has a rule for would only repeat it.
            if (ruleExes.Contains(handler.Name)) continue;
            var h = handler;
            rows.Add(new ResultAction(handler.UiName, AppGlyph, () => { Invoke(h, path); return Task.CompletedTask; }, Detail: "Windows"));
        }
        rows.Add(new ResultAction("Choose an app…", "\uE721", null, Flags: ActionFlags.NoUsage,
            Input: text => ChooseRows(path, text),
            InputPlaceholder: "App name, .exe name or full path; ↵ picks"));
        return rows;
    }

    private IReadOnlyList<ResultAction> ChooseRows(string path, string text)
    {
        var candidates = Candidates(text);
        if (candidates.Count == 0)
            return [new ResultAction(text.Trim().Length == 0 ? "Type an app's name, an .exe name or a full path"
                : "No app or .exe matches", "", null, Flags: ActionFlags.Disabled)];
        return candidates.Select(c => new ResultAction(c.Name, AppGlyph, null, Detail: c.Exe,
            Children: () => ChoiceRows(c, path))).ToList();
    }

    /// <summary>What to do with a chosen app: open the file once, or also keep offering it (and use it for Enter).</summary>
    private IReadOnlyList<ResultAction> ChoiceRows(Candidate app, string path)
    {
        var ext = ExtensionOf(path);
        Task Open() { Launch(app.Exe, OpenWithRule.DefaultArguments, path); return Task.CompletedTask; }
        var rows = new List<ResultAction> { new("Open once", Glyph, Open) };
        if (ext.Length == 0) return rows;
        if (settings.IsBroken)
        {
            rows.Add(new ResultAction("settings.json has a JSON error; fix it to save apps", "", null, Flags: ActionFlags.Disabled));
            return rows;
        }
        rows.Add(new ResultAction($"Always offer for {ext}", Glyph, () => { AddRule(app.Name, app.Exe, ext, enter: false); return Open(); },
            Detail: "opens it now and adds it to Open with"));
        rows.Add(new ResultAction($"Always offer, and use for Enter on {ext}", Glyph, () => { AddRule(app.Name, app.Exe, ext, enter: true); return Open(); },
            Detail: "opens it now; Ctrl+K keeps the default app"));
        return rows;
    }

    /// <summary>Saves "offer <paramref name="exe"/> for <paramref name="ext"/>" (and Enter). Reuses the app's rule when
    /// it has one with the same Enter setting; an Enter rule goes first, so it wins over older ones.</summary>
    public bool AddRule(string name, string exe, string ext, bool enter)
    {
        bool saved = settings.Update(s =>
        {
            var sameExe = s.OpenWith.Where(r => r.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sameExe.Any(r => r.Extensions.Contains(ext) && (r.Enter || !enter))) return; // already there
            if (enter)
            {
                // The extension moves from the app's plain rule to its Enter rule.
                foreach (var r in sameExe.Where(r => !r.Enter && r.Extensions.Remove(ext) && r.Extensions.Count == 0)) s.OpenWith.Remove(r);
            }
            var rule = sameExe.FirstOrDefault(r => r.Enter == enter && s.OpenWith.Contains(r));
            if (rule is null)
            {
                rule = new OpenWithRule { Name = name, Exe = exe, Enter = enter };
                if (enter) s.OpenWith.Insert(0, rule); else s.OpenWith.Add(rule);
            }
            else if (enter)
            {
                s.OpenWith.Remove(rule);
                s.OpenWith.Insert(0, rule);
            }
            rule.Extensions.Add(ext);
        });
        Log.Info(saved ? $"open with: {name} ({exe}) for {ext}{(enter ? ", Enter" : "")}" : $"open with: rule for {ext} not saved");
        return saved;
    }

    // ---- choosing an app -----------------------------------------------------------------------------

    internal sealed record Candidate(string Name, string Exe);

    private readonly Lock _gate = new();
    private string? _exeWanted, _exeCachedFor;
    private bool _exeRunning;
    private IReadOnlyList<IndexHit> _exeCached = [];
    private readonly Dictionary<string, string?> _appExe = new(StringComparer.Ordinal);

    /// <summary>A typed full path to a program, then matching apps (Win32 programs; Store apps that open the file
    /// type are in Windows' list already), then indexed .exe files.</summary>
    public IReadOnlyList<Candidate> Candidates(string text)
    {
        var typed = text.Trim().Trim('"');
        var query = typed.ToLowerInvariant();
        if (query.Length == 0) return [];
        var list = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string exe) { if (list.Count < MaxCandidates && seen.Add(exe)) list.Add(new Candidate(name, exe)); }

        if (Path.IsPathFullyQualified(typed) && typed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(typed))
            Add(Path.GetFileNameWithoutExtension(typed), typed);

        foreach (var (app, exe, _) in shell.Apps
                     .Select(a => (App: a, Exe: ExeOf(a), Match: Fuzzy.Match(query, a.Name)))
                     .Where(x => x.Exe is not null && x.Match.Found && x.Match.Quality >= MatchQuality.Substring)
                     .OrderByDescending(x => x.Match).Take(5))
            Add(app.Name, exe!);

        if (query.Length >= 2 && !Path.IsPathFullyQualified(typed))
        {
            IReadOnlyList<IndexHit> hits;
            lock (_gate)
            {
                if (_exeCachedFor != query) RequestExes(query);
                hits = _exeCachedFor == query ? _exeCached : [];
            }
            foreach (var hit in hits.Where(h => !h.Folder && h.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsPackagedPath(h.Path)))
                Add(Path.GetFileNameWithoutExtension(hit.Name), hit.Path);
        }
        return list;
    }

    private void RequestExes(string query)
    {
        _exeWanted = query;
        if (_exeRunning) return;
        _exeRunning = true;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                string q;
                lock (_gate) q = _exeWanted!;
                var reply = await index.SearchAsync($"{q} *.exe", 12);
                bool latest;
                lock (_gate)
                {
                    _exeCachedFor = q;
                    _exeCached = reply?.Hits ?? [];
                    latest = _exeWanted == q;
                    if (latest) _exeRunning = false;
                }
                if (!latest) continue;
                if (reply is { Hits.Count: > 0 }) CandidatesChanged?.Invoke();
                return;
            }
        });
    }

    /// <summary>The program an app entry starts: its shortcut's target, or an AppsFolder ID that is a path
    /// (<c>{known folder GUID}\VS Code\Code.exe</c>). Null for Store apps and documents.</summary>
    private string? ExeOf(AppEntry app)
    {
        lock (_appExe)
        {
            if (_appExe.TryGetValue(app.Id, out var known)) return known;
            string? exe = null;
            if (app.TargetPath is { } target && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = target;
            else if (!app.IsPackaged && app.Id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = ResolveKnownFolderPath(app.Id);
            if (exe is not null && (IsPackagedPath(exe) || !File.Exists(exe))) exe = null;
            _appExe[app.Id] = exe;
            return exe;
        }
    }

    /// <summary>A Store app's program (<c>…\WindowsApps\…</c>) can't be started by path; Windows' list covers those apps.</summary>
    private static bool IsPackagedPath(string path) => path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveKnownFolderPath(string id)
    {
        if (Path.IsPathFullyQualified(id)) return id;
        if (id.Length < 40 || id[0] != '{' || id[37] != '}' || !Guid.TryParse(id[1..37], out var folderId)) return null;
        if (SHGetKnownFolderPath(in folderId, 0, IntPtr.Zero, out var p) < 0) return null;
        try { return Marshal.PtrToStringUni(p) is { } root ? root + id[38..] : null; } // id[38..] starts with '\'
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // ---- Windows' handlers ---------------------------------------------------------------------------

    /// <param name="Name">The program (a path for desktop apps).</param>
    internal sealed record WindowsHandler(string Name, string UiName, IAssocHandler Handler);

    private readonly Dictionary<string, (DateTime At, IReadOnlyList<WindowsHandler> List)> _handlers = new(StringComparer.Ordinal);

    /// <summary>The recommended handlers Windows lists in its own Open with (UI thread only).</summary>
    public IReadOnlyList<WindowsHandler> HandlersFor(string ext)
    {
        if (ext.Length == 0) return [];
        if (_handlers.TryGetValue(ext, out var cached) && DateTime.UtcNow - cached.At < HandlerCacheTime) return cached.List;
        var list = new List<WindowsHandler>();
        try
        {
            int hr = SHAssocEnumHandlers(ext, AssocFilterRecommended, out var handlers);
            if (hr >= 0 && handlers is not null)
            {
                var one = new IAssocHandler[1];
                while (list.Count < MaxHandlers && handlers.Next(1, one, out uint fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        one[0].GetName(out var name);
                        one[0].GetUIName(out var uiName);
                        if (!string.IsNullOrWhiteSpace(uiName)) list.Add(new WindowsHandler(name ?? "", uiName, one[0]));
                    }
                    catch (COMException) { }
                }
            }
        }
        catch (Exception e) { Log.Error($"open with: Windows' apps for {ext}", e); }
        _handlers[ext] = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>Opens the file with a Windows handler, the way Windows' Open with does (packaged apps included).</summary>
    public static void Invoke(WindowsHandler handler, string path)
    {
        Log.Info($"open with {handler.UiName} ({handler.Name}): {path}");
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item));
        var bhid = BhidDataObject;
        var iidData = typeof(System.Runtime.InteropServices.ComTypes.IDataObject).GUID;
        item.BindToHandler(IntPtr.Zero, ref bhid, ref iidData, out var data);
        handler.Handler.Invoke(data);
    }

    // ---- interop -------------------------------------------------------------------------------------

    private const int AssocFilterRecommended = 1;
    private static readonly Guid BhidDataObject = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHAssocEnumHandlers(string extension, int filter, out IEnumAssocHandlers handlers);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);

    [ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumAssocHandlers
    {
        [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IAssocHandler[] items, out uint fetched);
    }

    [ComImport, Guid("f04061ac-1659-4a3f-a954-775aa57fc083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAssocHandler
    {
        void GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetUIName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] out string path, out int index);
        [PreserveSig] int IsRecommended();
        void MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string description);
        void Invoke([MarshalAs(UnmanagedType.Interface)] object dataObject);
        void CreateInvoker([MarshalAs(UnmanagedType.Interface)] object dataObject, out IntPtr invoker);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
