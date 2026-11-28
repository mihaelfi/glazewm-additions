using System.Text.Json.Nodes;

namespace GlazeLauncher;

/// <summary>GlazeIndex's settings requests (docs/indexer.md, Requests), for the File index page of the settings window.</summary>
internal sealed partial class IndexClient
{
    /// <summary>One JSON request. The reply, or an object with <c>ok: false</c> and an error when the service can't be
    /// reached or answered with something unreadable; never null.</summary>
    public async Task<JsonObject> RequestAsync(JsonObject request, CancellationToken cancel = default)
    {
        var reply = await SendAsync(request.ToJsonString(), cancel);
        if (reply is null) return Failed("GlazeIndex isn't running (or didn't answer)");
        try { return JsonNode.Parse(reply) as JsonObject ?? Failed("empty reply"); }
        catch (System.Text.Json.JsonException e) { return Failed($"unreadable reply: {e.Message}"); }

        static JsonObject Failed(string error) => new() { ["ok"] = false, ["error"] = error };
    }

    /// <summary><c>status</c>: every drive with its state, plus the service version.</summary>
    public Task<JsonObject> StatusAsync() => RequestAsync(new JsonObject { ["op"] = "status" });

    /// <summary><c>settings</c>: the service's settings (drives, excluded, demoted, demoted_at_root, search_hidden, …).</summary>
    public Task<JsonObject> SettingsAsync() => RequestAsync(new JsonObject { ["op"] = "settings" });

    /// <summary><c>settings.set</c>: replaces the given keys (lists whole). Replies with the new settings.</summary>
    public Task<JsonObject> SetSettingsAsync(JsonObject changes) =>
        RequestAsync(new JsonObject { ["op"] = "settings.set", ["settings"] = changes });

    /// <summary><c>drive</c>: turns indexing of a drive (<c>"D:"</c>) on or off.</summary>
    public Task<JsonObject> SetDriveAsync(string drive, bool enabled) =>
        RequestAsync(new JsonObject { ["op"] = "drive", ["drive"] = drive, ["enabled"] = enabled });

    /// <summary><c>exclude</c> with a reply (the settings window shows errors; <see cref="ExcludeAsync"/> only logs).</summary>
    public Task<JsonObject> SetExcludedAsync(string folder, bool excluded) =>
        RequestAsync(new JsonObject { ["op"] = "exclude", ["path"] = folder, ["excluded"] = excluded });
}
