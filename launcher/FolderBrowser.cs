using System.Windows;

namespace GlazeLauncher;

internal enum BrowseSort { Name, Size, Modified }

/// <summary>
/// Folder browsing (→ on a folder): one folder's items from GlazeIndex, filtered as you type. The filter matches the
/// folder's own items first, then anything deeper under it (the service's scoped search), with the path inside the
/// folder as the subtitle. Like <see cref="FileSearchProvider"/>, it never waits on the pipe: <see cref="Items"/> answers
/// from the cache and starts a request, and <see cref="Changed"/> asks the window to render again when it arrives.
/// Everything runs on the UI thread except the requests.
/// </summary>
internal sealed class FolderBrowser(IndexClient client)
{
    private readonly record struct ListingKey(string Path, BrowseSort Sort, bool Reversed);

    private const int ListLimit = 2000;
    private const int DeepLimit = 40;

    private IndexListing? _listing;          // the current folder and sort, once loaded
    private ListingKey? _listingFor;
    private ListingKey? _listingWanted;
    private (string Path, string Text)? _deepFor;
    private (string Path, string Text)? _deepWanted;
    private IReadOnlyList<IndexHit> _deep = [];
    private ListingKey? _downFor; // the service didn't answer for this listing; not asked again

    /// <summary>The folder being browsed; null when not browsing.</summary>
    public string? Path { get; private set; }
    public BrowseSort Sort { get; private set; } = BrowseSort.Name;

    /// <summary>Raised on a background thread when a listing or a deep search arrived.</summary>
    public event Action? Changed;

    public void Open(string path)
    {
        Path = path.Length == 2 && path[1] == ':' ? path + "\\" : path;
        _downFor = null;
    }
    public void Close() => Path = null;

    /// <summary>The opposite of the sort's usual order (name A–Z, largest first, newest first).</summary>
    public bool Reversed { get; private set; }

    /// <summary>Ctrl+S: name → size → date, each in its usual order.</summary>
    public void NextSort()
    {
        Sort = Sort switch
        {
            BrowseSort.Name => BrowseSort.Size,
            BrowseSort.Size => BrowseSort.Modified,
            _ => BrowseSort.Name,
        };
        Reversed = false;
    }

    /// <summary>Ctrl+Shift+S: the same sort the other way round.</summary>
    public void ReverseSort() => Reversed = !Reversed;

    public string SortLabel => (Sort, Reversed) switch
    {
        (BrowseSort.Name, false) => "name A–Z",
        (BrowseSort.Name, true) => "name Z–A",
        (BrowseSort.Size, false) => "largest first",
        (BrowseSort.Size, true) => "smallest first",
        (_, false) => "newest first",
        _ => "oldest first",
    };

    /// <summary>The rows for the current folder and filter: what's cached now (requests start in the background).</summary>
    public List<ResultItem> Items(string filter)
    {
        var rows = new List<ResultItem>();
        if (Path is not { } path) return rows;

        var key = new ListingKey(path, Sort, Reversed);
        if (_downFor == key)
        {
            rows.Add(Info("File search isn't running", "Folder browsing needs the GlazeIndex service (docs/indexer.md)"));
            return rows;
        }
        if (_listingFor != key) RequestListing(key);
        if (_listing is not { } listing || _listingFor != key) return rows; // a few ms; the list fills when it arrives
        if (listing.Error is { } error)
        {
            rows.Add(Info(error, path));
            return rows;
        }

        var text = filter.Trim();
        var own = listing.Items.Where(h => !h.Hidden);
        if (text.Length == 0)
        {
            rows.AddRange(own.Select(h => Row(h, null, null)));
            if (listing.Count > listing.Items.Count)
                rows.Add(Info($"{listing.Count - listing.Items.Count:N0} more items", "Type to filter this folder"));
            return rows;
        }

        // The folder's own items that match, best first (the sort order breaks ties)...
        var query = text.ToLowerInvariant();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path.TrimEnd('\\') };
        foreach (var (hit, _, positions) in own
                     .Select((h, i) => (Hit: h, Index: i, Match: Fuzzy.Match(query, h.Name)))
                     .Where(x => x.Match.Found)
                     .OrderByDescending(x => x.Match).ThenBy(x => x.Index)
                     .Select(x => (x.Hit, x.Index, Fuzzy.FindPositions(query, x.Hit.Name))))
        {
            rows.Add(Row(hit, positions, null));
            shown.Add(hit.Path);
        }

        // ...then deeper matches anywhere under it.
        var deepKey = (path, text);
        if (_deepFor != deepKey) RequestDeep(deepKey);
        if (_deepFor == deepKey)
        {
            foreach (var hit in _deep)
            {
                if (hit.Hidden || !shown.Add(hit.Path)) continue;
                var inside = System.IO.Path.GetDirectoryName(hit.Path) is { } parent && parent.Length > path.TrimEnd('\\').Length
                    ? parent[(path.TrimEnd('\\').Length + 1)..] : "";
                var where = inside.Length > 0 ? $"in {inside}" : "";
                rows.Add(Row(hit, hit.Highlight is { Length: > 0 } hl && hl.Start + hl.Length <= hit.Name.Length
                    ? Enumerable.Range(hl.Start, hl.Length).ToArray() : null, where));
            }
        }
        return rows;
    }

    private static ResultItem Row(IndexHit hit, int[]? matches, string? where)
    {
        var parts = new List<string>(4) { hit.Folder ? "Folder" : "File" };
        if (hit.Excluded) parts.Add("excluded from the index");
        else
        {
            parts.Add(Format.Size(hit.Size));
            if (hit.Folder) parts.Add(hit.Files == 1 ? "1 file" : $"{hit.Files:N0} files");
        }
        if (hit.ModifiedUnixMs > 0)
            parts.Add(DateTimeOffset.FromUnixTimeMilliseconds(hit.ModifiedUnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm"));
        if (where is { Length: > 0 }) parts.Add(where);
        return FileSearchProvider.Item(hit.Path, hit.Name, hit.Folder, null, null, string.Join(" · ", parts), matches);
    }

    /// <summary>A row that only informs (no actions).</summary>
    private static ResultItem Info(string title, string subtitle) => new()
    {
        Id = "browse-info:" + title,
        Title = title,
        Subtitle = subtitle,
        Glyph = "",
        UsageKey = "",
        Actions = _ => [],
    };

    private void RequestListing(ListingKey key)
    {
        if (_listingWanted == key) return;
        _listingWanted = key;
        _ = LoadListingAsync(key);
    }

    private async Task LoadListingAsync(ListingKey key)
    {
        var sort = key.Sort switch { BrowseSort.Size => "size", BrowseSort.Modified => "modified", _ => "name" };
        var listing = await client.ChildrenAsync(key.Path, sort, ListLimit).ConfigureAwait(false);
        if (key.Reversed && listing is { Error: null })
        {
            // The service sorts one way only: take the last items of its order (a second request when the folder has
            // more than fit) and turn them around.
            if (listing.Count > listing.Items.Count)
                listing = await client.ChildrenAsync(key.Path, sort, ListLimit, listing.Count - ListLimit).ConfigureAwait(false);
            // Folders stay above files by name either way (Z–A within each group).
            if (listing is not null)
                listing = listing with
                {
                    Items = key.Sort == BrowseSort.Name
                        ? listing.Items.Reverse().OrderBy(h => !h.Folder).ToList() // OrderBy is stable
                        : listing.Items.Reverse().ToList(),
                };
        }
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_listingWanted != key) return; // the user moved on
            if (listing is null) _downFor = key;
            else { _listing = listing; _listingFor = key; }
            _listingWanted = null;
        });
        Changed?.Invoke();
    }

    private void RequestDeep((string Path, string Text) key)
    {
        if (_deepWanted == key) return;
        _deepWanted = key;
        _ = LoadDeepAsync(key);
    }

    private async Task LoadDeepAsync((string Path, string Text) key)
    {
        var reply = await client.SearchInAsync(key.Path, key.Text, DeepLimit).ConfigureAwait(false);
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_deepWanted != key) return;
            _deep = reply?.Hits ?? [];
            _deepFor = key;
            _deepWanted = null;
        });
        Changed?.Invoke();
    }
}
