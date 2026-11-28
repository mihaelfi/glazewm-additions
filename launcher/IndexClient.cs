using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlazeLauncher;

/// <summary>One file or folder from GlazeIndex. <paramref name="Size"/> is a folder's total and <paramref name="Files"/>
/// its file count. <paramref name="Highlight"/> is the matched word's start and length in <paramref name="Name"/> (UTF-16).</summary>
internal sealed record IndexHit(string Path, string Name, bool Folder, long Size, long ModifiedUnixMs, int Score,
    (int Start, int Length) Highlight, long Files = 0, bool Hidden = false, bool Excluded = false);

internal sealed record IndexReply(string Query, IReadOnlyList<IndexHit> Hits, bool Ready);

/// <summary>A folder's contents (<paramref name="Count"/> is the full number; <paramref name="Items"/> may be fewer), or
/// the service's <paramref name="Error"/>.</summary>
internal sealed record IndexListing(string Path, int Count, IReadOnlyList<IndexHit> Items, string? Error);

/// <summary>
/// Client for the GlazeIndex service (indexer/, docs/indexer.md) on <c>\\.\pipe\GlazeIndex</c>: one request line, one
/// JSON reply per connection. When the service isn't running, it backs off for a while instead of waiting on every key.
/// </summary>
internal sealed partial class IndexClient
{
    /// <summary>For actions that aren't tied to a provider (Exclude from the index). It keeps its own back-off.</summary>
    public static IndexClient Shared { get; } = new();

    private const string PipeName = "GlazeIndex";
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(15);
    private DateTime _retryAfter = DateTime.UtcNow.AddYears(-1);
    private bool _loggedDown;

    /// <summary>The launcher's search: the service reads filters (<c>*.zip</c>, <c>in:</c>, …) from the text.</summary>
    public async Task<IndexReply?> SearchAsync(string query, int limit, CancellationToken cancel = default)
    {
        var reply = await SendAsync($"search\t{limit}\t{query.Replace('\t', ' ').Replace('\n', ' ')}", cancel);
        return reply is null ? null : Parse(reply, ParseSearch);
    }

    /// <summary>Matches under <paramref name="folder"/> only (folder browsing).</summary>
    public async Task<IndexReply?> SearchInAsync(string folder, string text, int limit, CancellationToken cancel = default)
    {
        var request = new JsonObject { ["op"] = "search", ["query"] = text, ["in"] = folder, ["limit"] = limit };
        var reply = await SendAsync(request.ToJsonString(), cancel);
        return reply is null ? null : Parse(reply, ParseSearch);
    }

    /// <summary>A folder's items, sorted by <paramref name="sort"/> ("name", "size" or "modified").</summary>
    public async Task<IndexListing?> ChildrenAsync(string folder, string sort, int limit, int offset = 0, CancellationToken cancel = default)
    {
        var request = new JsonObject { ["op"] = "children", ["path"] = folder, ["sort"] = sort, ["limit"] = limit, ["offset"] = offset };
        var reply = await SendAsync(request.ToJsonString(), cancel);
        return reply is null ? null : Parse(reply, root =>
        {
            if (!root.GetProperty("ok").GetBoolean())
                return new IndexListing(folder, 0, [], root.TryGetProperty("error", out var e) ? e.GetString() : "failed");
            var items = root.GetProperty("items").EnumerateArray().Select(Hit).ToList();
            return new IndexListing(root.GetProperty("folder").GetProperty("path").GetString() ?? folder,
                root.GetProperty("count").GetInt32(), items, null);
        });
    }

    /// <summary>Hides a folder from search (<paramref name="excluded"/>) or brings it back. Logs the outcome.</summary>
    public async Task ExcludeAsync(string folder, bool excluded)
    {
        var request = new JsonObject { ["op"] = "exclude", ["path"] = folder, ["excluded"] = excluded };
        var reply = await SendAsync(request.ToJsonString(), default);
        string? error = reply is null ? "GlazeIndex isn't running" : Parse(reply, root =>
            root.GetProperty("ok").GetBoolean() ? "" : root.TryGetProperty("error", out var e) ? e.GetString() ?? "failed" : "failed");
        if (string.IsNullOrEmpty(error)) Log.Info($"{(excluded ? "excluded" : "included")} {folder} in the file index");
        else Log.Info($"couldn't {(excluded ? "exclude" : "include")} {folder}: {error}");
    }

    /// <summary>Sends one request line; null when the service is down (then it backs off) or the reply is unreadable.</summary>
    private async Task<byte[]?> SendAsync(string line, CancellationToken cancel)
    {
        if (DateTime.UtcNow < _retryAfter) return null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(150, cancel);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), cancel);
            await pipe.FlushAsync(cancel);
            using var reply = new MemoryStream();
            await pipe.CopyToAsync(reply, cancel);
            if (_loggedDown) { Log.Info("GlazeIndex is reachable again"); _loggedDown = false; }
            return reply.ToArray();
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            _retryAfter = DateTime.UtcNow + Backoff;
            if (!_loggedDown) { Log.Info($"GlazeIndex not reachable ({e.GetType().Name}: {e.Message}); file search is off for now"); _loggedDown = true; }
            return null;
        }
    }

    private static T? Parse<T>(byte[] json, Func<JsonElement, T> read) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return read(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Error("GlazeIndex reply", e);
            return null;
        }
    }

    private static IndexReply ParseSearch(JsonElement root) =>
        new(root.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "",
            root.TryGetProperty("results", out var results) ? results.EnumerateArray().Select(Hit).ToList() : [],
            root.TryGetProperty("ready", out var ready) && ready.GetBoolean());

    private static IndexHit Hit(JsonElement r)
    {
        var highlight = r.TryGetProperty("highlight", out var h) ? (h[0].GetInt32(), h[1].GetInt32()) : (0, 0);
        return new IndexHit(
            r.GetProperty("path").GetString() ?? "",
            r.GetProperty("name").GetString() ?? "",
            r.GetProperty("folder").GetBoolean(),
            r.GetProperty("size").GetInt64(),
            r.GetProperty("modified").GetInt64(),
            r.TryGetProperty("score", out var s) ? s.GetInt32() : 0,
            highlight,
            r.TryGetProperty("files", out var f) ? f.GetInt64() : 0,
            r.TryGetProperty("hidden", out var hid) && hid.GetBoolean(),
            r.TryGetProperty("excluded", out var ex) && ex.GetBoolean());
    }
}
