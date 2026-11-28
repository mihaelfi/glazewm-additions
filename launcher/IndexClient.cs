using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GlazeLauncher;

/// <summary>One file or folder from GlazeIndex. <paramref name="Size"/> is a folder's total.
/// <paramref name="Highlight"/> is the matched word's start and length in <paramref name="Name"/> (UTF-16).</summary>
internal sealed record IndexHit(string Path, string Name, bool Folder, long Size, long ModifiedUnixMs, int Score, (int Start, int Length) Highlight);

internal sealed record IndexReply(string Query, IReadOnlyList<IndexHit> Hits, bool Ready);

/// <summary>
/// Client for the GlazeIndex service (indexer/, docs/indexer.md) on <c>\\.\pipe\GlazeIndex</c>: one request line, one
/// JSON reply per connection. When the service isn't running, it backs off for a while instead of waiting on every key.
/// </summary>
internal sealed class IndexClient
{
    private const string PipeName = "GlazeIndex";
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(15);
    private DateTime _retryAfter = DateTime.MinValue;
    private bool _loggedDown;

    public async Task<IndexReply?> SearchAsync(string query, int limit, CancellationToken cancel = default)
    {
        if (DateTime.UtcNow < _retryAfter) return null;
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(150, cancel);
            var request = Encoding.UTF8.GetBytes($"search\t{limit}\t{query.Replace('\t', ' ').Replace('\n', ' ')}\n");
            await pipe.WriteAsync(request, cancel);
            await pipe.FlushAsync(cancel);
            using var reply = new MemoryStream();
            await pipe.CopyToAsync(reply, cancel);
            if (_loggedDown) { Log.Info("GlazeIndex is reachable again"); _loggedDown = false; }
            return Parse(reply.ToArray());
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            _retryAfter = DateTime.UtcNow + Backoff;
            if (!_loggedDown) { Log.Info($"GlazeIndex not reachable ({e.GetType().Name}: {e.Message}); file search is off for now"); _loggedDown = true; }
            return null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Error("GlazeIndex reply", e);
            return null;
        }
    }

    private static IndexReply Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var hits = new List<IndexHit>();
        foreach (var r in root.GetProperty("results").EnumerateArray())
        {
            var highlight = r.GetProperty("highlight");
            hits.Add(new IndexHit(
                r.GetProperty("path").GetString() ?? "",
                r.GetProperty("name").GetString() ?? "",
                r.GetProperty("folder").GetBoolean(),
                r.GetProperty("size").GetInt64(),
                r.GetProperty("modified").GetInt64(),
                r.GetProperty("score").GetInt32(),
                (highlight[0].GetInt32(), highlight[1].GetInt32())));
        }
        return new IndexReply(root.GetProperty("query").GetString() ?? "", hits, root.GetProperty("ready").GetBoolean());
    }
}
