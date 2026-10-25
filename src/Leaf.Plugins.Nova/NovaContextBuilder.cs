using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed class ContextSnapshot
{
    [JsonPropertyName("discussions")]
    public List<DiscussionEntry> Discussions { get; set; } = [];

    [JsonPropertyName("otherAgentDiscussions")]
    public List<DiscussionEntry> OtherAgentDiscussions { get; set; } = [];

    [JsonPropertyName("outfit")]
    public string? Outfit { get; set; }

    [JsonPropertyName("outfitAsset")]
    public string? OutfitAsset { get; set; }

    [JsonPropertyName("mood")]
    public string? Mood { get; set; }

    [JsonPropertyName("liveEvents")]
    public List<LiveEventEntry> LiveEvents { get; set; } = [];

    [JsonPropertyName("extensionContexts")]
    public List<ExtensionContextEntry> ExtensionContexts { get; set; } = [];

    public sealed class ExtensionContextEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("revision")]
        public string? Revision { get; set; }

        [JsonPropertyName("data")]
        public JsonObject? Data { get; set; }
    }

    public sealed class LiveEventEntry
    {
        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; }
    }

    public sealed class DiscussionEntry
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("messageCount")]
        public int MessageCount { get; set; }

        [JsonPropertyName("lastActivity")]
        public DateTime LastActivity { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = "idle";

        [JsonPropertyName("type")]
        public string Type { get; set; } = "chat";

        /// <summary>Start time; names an untitled LIVE. Null in snapshots persisted before it existed.</summary>
        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("agentId")]
        public string? AgentId { get; set; }
    }
}

/// <summary>
/// Builds the &lt;nova-context&gt; block delivered ahead of every user message: full
/// snapshot on the first message, delta against the previous snapshot afterwards.
/// The curl hints point at the plugin's kernel-hosted API (port 18804).
/// </summary>
/// <remarks>
/// Nearly everything interpolated here is externally sourced: discussion titles, LIVE event
/// sources and content (Sonos/Spotify track and artist names, automation output, posted events),
/// extension context (mail summaries, presence), outfit, mood, reaction lines, and device fields
/// parsed from the client's User-Agent. A value containing "&lt;/nova-context&gt;" would close the
/// block and make the rest read as the user's own message. So each block body is composed apart
/// from the frame and passed through <see cref="NovaEventText.Neutralize"/> as a whole (its fixed
/// text holds no Nova tag, so ordinary context is unchanged byte for byte), and every attribute of
/// the opening tag is escaped.
/// </remarks>
public static class NovaContextBuilder
{
    private const string ApiBase = "http://127.0.0.1:18804/api/apps/nova";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ContextSnapshot BuildSnapshot(
        List<DiscussionRead> ownDiscussions,
        List<DiscussionRead>? otherAgentDiscussions,
        string? outfit,
        string? outfitAsset,
        string? mood = null,
        List<ContextSnapshot.LiveEventEntry>? liveEvents = null,
        IReadOnlyList<PluginContextFragment>? extensionContexts = null)
    {
        return new ContextSnapshot
        {
            Discussions = ownDiscussions.Select(ToEntry).ToList(),
            OtherAgentDiscussions = otherAgentDiscussions?.Select(ToEntry).ToList() ?? [],
            Outfit = outfit,
            OutfitAsset = outfitAsset,
            Mood = mood,
            LiveEvents = liveEvents ?? [],
            ExtensionContexts = extensionContexts?.Select(fragment => new ContextSnapshot.ExtensionContextEntry
            {
                Id = fragment.Id,
                Source = fragment.Source,
                Content = fragment.Content,
                Revision = fragment.Revision,
                Data = fragment.Data?.DeepClone() as JsonObject,
            }).ToList() ?? [],
        };

        static ContextSnapshot.DiscussionEntry ToEntry(DiscussionRead d) => new()
        {
            Id = d.Id,
            Title = d.Title,
            MessageCount = d.MessageCount,
            Status = d.Status,
            LastActivity = d.LastActivity,
            Type = d.Type,
            CreatedAt = d.CreatedAt,
            AgentId = d.AgentId,
        };
    }

    public static string BuildFullContext(
        ContextSnapshot snapshot,
        string currentId, DateTime now, ResolvedDevice device, string input, string? agentName,
        List<string>? reactionLines = null, IReadOnlyList<WorktreeAttachment>? worktrees = null,
        string? messageUid = null)
    {
        var sb = new StringBuilder();

        if (snapshot.Mood != null)
            sb.Append($"\nMood: {snapshot.Mood}");
        else
            sb.Append("\nRead memory/dreaming/mood.md for your current emotional state before responding.");

        var active = snapshot.Discussions.Where(d => !DiscussionStatus.IsClosed(d.Status)).ToList();
        var archived = snapshot.Discussions.Where(d => DiscussionStatus.IsClosed(d.Status) && d.MessageCount > 0).ToList();
        var title = TitleResolver(snapshot.Discussions);

        if (active.Count > 0)
        {
            sb.Append("\nActive discussions:");
            foreach (var d in active)
            {
                var marker = d.Id == currentId ? " <- you are here" : "";
                var liveTag = d.Type == "live" ? "[LIVE]" : "";
                sb.Append($"\n- {liveTag}[{d.Id}] \"{title(d)}\" . {d.MessageCount} msgs . {FormatRelativeTime(now - d.LastActivity)}{marker}");
            }
        }

        if (archived.Count > 0)
        {
            sb.Append("\n\nRecently archived:");
            foreach (var d in archived)
                sb.Append($"\n- [{d.Id}] \"{title(d)}\" . {d.MessageCount} msgs . archived {FormatRelativeTime(now - d.LastActivity)}");
        }

        if (snapshot.OtherAgentDiscussions.Count > 0)
        {
            sb.Append("\n\nOther agents' active discussions:");
            var otherTitle = TitleResolver(snapshot.OtherAgentDiscussions);
            foreach (var d in snapshot.OtherAgentDiscussions)
                sb.Append($"\n- [{d.Id}] \"{otherTitle(d)}\" . {d.MessageCount} msgs . {FormatRelativeTime(now - d.LastActivity)}");
        }

        sb.Append($"\n\nRecall any discussion: curl -s {ApiBase}/discussions/{{id}}/export");
        sb.Append($"\nSearch all discussions: curl -s \"{ApiBase}/discussions/search?q={{query}}\"");
        sb.Append($"\nGet full context snapshot: curl -s {ApiBase}/discussions/{{id}}/context");
        // The emoji is a JSON escape: a cp1252 shell turns a literal emoji into "??".
        sb.Append($"\nReact to a message (messageKey: the message=\"...\" uid on a context tag): curl -s -X POST {ApiBase}/discussions/{currentId}/reactions -H \"Authorization: Bearer $REDLEAF_EXECUTION_TOKEN\" -H \"Content-Type: application/json\" -d '{{\"emoji\":\"\\u2764\\ufe0f\",\"messageKey\":\"<message uid>\"}}'");

        var liveDisc = active.FirstOrDefault(d => d.Type == "live");
        if (liveDisc != null)
            sb.Append($"\nPost to LIVE timeline: curl -s -X POST {ApiBase}/discussions/{liveDisc.Id}/event -H \"Content-Type: application/json\" -d '{{\"content\":\"...\",\"source\":\"...\"}}'");

        if (snapshot.LiveEvents.Count > 0)
        {
            sb.Append("\n\nRecent LIVE events:");
            foreach (var ev in snapshot.LiveEvents)
            {
                var source = ev.Source.StartsWith("event:") ? ev.Source[6..] : ev.Source;
                sb.Append($"\n- [{source}] {ev.Content} ({FormatRelativeTime(now - ev.Timestamp)})");
            }
        }

        if (snapshot.ExtensionContexts.Count > 0)
        {
            sb.Append("\n\nInstalled extension context:");
            foreach (var context in snapshot.ExtensionContexts)
                sb.Append($"\n- [{context.Source}] {context.Content}");
        }

        if (reactionLines is { Count: > 0 })
        {
            sb.Append("\n\nReactions since your last message:");
            foreach (var r in reactionLines)
                sb.Append($"\n- {r}");
        }

        if (snapshot.Outfit != null)
            sb.Append($"\n\n{snapshot.Outfit}");

        AppendWorktrees(sb, worktrees, leadingBlankLine: true);

        return Frame(sb, now, device, input, currentId, agentName, messageUid, NoReplyHint);
    }

    // Constant text appended after neutralization: inside the body the marker would be made inert
    // like any other Nova tag, and no externally sourced value can produce it out here.
    internal const string NoReplyHint =
        "\n\nWhen a reaction or nothing at all says it better than words, make your entire reply exactly "
        + NovaNoReply.Marker + ": the turn then ends with no visible message.";

    internal const string TriggeredNoteHint =
        "\nNo one wrote anything. A reaction-only or silent reply (exactly " + NovaNoReply.Marker
        + ") is fine when words would add nothing.";

    internal const string SteeredNoteHint =
        "\nThis arrived while you were working; take it into account, it needs no reply of its own.";

    // The opening tag, the neutralized body, any constant trusted text, the closing tag.
    private static string Frame(StringBuilder body, DateTime now, ResolvedDevice device,
        string input, string currentId, string? agentName, string? messageUid = null, string? trusted = null)
    {
        var sb = new StringBuilder();
        AppendOpenTag(sb, now, device, input, currentId, agentName, messageUid);
        sb.Append(NovaEventText.Neutralize(body.ToString()));
        sb.Append(trusted);
        sb.Append("\n</nova-context>");
        return sb.ToString();
    }

    /// <summary>
    /// A reaction note: the envelope alone, so the chat view shows nothing for it. Its own turn when
    /// the person reacted to the agent's latest message while it was idle, or folded into the
    /// running turn (<paramref name="steered"/>).
    /// </summary>
    public static string BuildReactionNote(IReadOnlyList<string> reactionLines, DateTime now,
        ResolvedDevice device, string currentId, string? agentName, bool steered)
    {
        var sb = new StringBuilder("\nReactions since your last message:");
        foreach (var r in reactionLines)
            sb.Append($"\n- {r}");
        return Frame(sb, now, device, "reaction", currentId, agentName,
            trusted: steered ? SteeredNoteHint : TriggeredNoteHint);
    }

    public static string BuildDeltaContext(
        ContextSnapshot current, ContextSnapshot previous,
        string currentId, DateTime now, ResolvedDevice device, string input, string? agentName,
        List<string>? reactionLines = null, IReadOnlyList<WorktreeAttachment>? worktrees = null,
        string? messageUid = null)
    {
        var changes = new List<string>();

        var prevIds = previous.Discussions.Select(d => d.Id).ToHashSet();
        var currIds = current.Discussions.Select(d => d.Id).ToHashSet();
        var prevMap = previous.Discussions.ToDictionary(d => d.Id);
        var currMap = current.Discussions.ToDictionary(d => d.Id);
        var currTitle = TitleResolver(current.Discussions);
        var prevTitle = TitleResolver(previous.Discussions);

        foreach (var id in currIds.Except(prevIds))
        {
            var d = currMap[id];
            changes.Add($"New discussion [{d.Id}] \"{currTitle(d)}\" ({d.Status})");
        }

        foreach (var id in prevIds.Except(currIds))
        {
            var d = prevMap[id];
            changes.Add($"Discussion [{d.Id}] \"{prevTitle(d)}\" no longer visible");
        }

        foreach (var id in currIds.Intersect(prevIds))
        {
            var prev = prevMap[id];
            var curr = currMap[id];

            if (prev.Status != curr.Status)
                changes.Add($"Discussion [{id}] \"{currTitle(curr)}\" changed status: {prev.Status} -> {curr.Status}");

            var newMsgs = curr.MessageCount - prev.MessageCount;
            if (newMsgs > 0)
                changes.Add($"{newMsgs} new message{(newMsgs > 1 ? "s" : "")} in [{id}] \"{currTitle(curr)}\"");

            if (prev.Title != curr.Title && curr.Title != null)
                changes.Add($"Discussion [{id}] renamed to \"{curr.Title}\"");
        }

        var prevOther = previous.OtherAgentDiscussions.Select(d => d.Id).ToHashSet();
        var currOther = current.OtherAgentDiscussions.Select(d => d.Id).ToHashSet();
        var prevOtherMap = previous.OtherAgentDiscussions.ToDictionary(d => d.Id);
        var currOtherMap = current.OtherAgentDiscussions.ToDictionary(d => d.Id);
        var currOtherTitle = TitleResolver(current.OtherAgentDiscussions);
        var prevOtherTitle = TitleResolver(previous.OtherAgentDiscussions);

        foreach (var id in currOther.Except(prevOther))
        {
            var d = currOtherMap[id];
            changes.Add($"New discussion from another agent: [{d.Id}] \"{currOtherTitle(d)}\"");
        }

        foreach (var id in prevOther.Except(currOther))
        {
            var d = prevOtherMap[id];
            changes.Add($"Other agent discussion [{d.Id}] \"{prevOtherTitle(d)}\" no longer active");
        }

        if (current.Outfit != previous.Outfit)
        {
            if (current.Outfit != null)
                changes.Add($"Outfit changed: {current.Outfit}");
            else if (previous.Outfit != null)
                changes.Add("Outfit removed");
        }

        if (current.Mood != previous.Mood && current.Mood != null)
            changes.Add($"Mood changed: {current.Mood}");

        var prevEventTimestamps = previous.LiveEvents.Select(e => e.Timestamp).ToHashSet();
        var newEvents = current.LiveEvents
            .Where(e => !prevEventTimestamps.Contains(e.Timestamp))
            .ToList();
        foreach (var ev in newEvents)
        {
            var source = ev.Source.StartsWith("event:") ? ev.Source[6..] : ev.Source;
            changes.Add($"[{source}] {ev.Content}");
        }

        var previousExtensions = previous.ExtensionContexts
            .ToDictionary(item => $"{item.Source}:{item.Id}", StringComparer.Ordinal);
        var currentExtensions = current.ExtensionContexts
            .ToDictionary(item => $"{item.Source}:{item.Id}", StringComparer.Ordinal);
        foreach (var key in currentExtensions.Keys.Union(previousExtensions.Keys))
        {
            previousExtensions.TryGetValue(key, out var before);
            currentExtensions.TryGetValue(key, out var after);
            if (after == null)
                changes.Add($"Extension context [{before!.Source}] removed");
            else if (before == null || before.Revision != after.Revision || before.Content != after.Content)
                changes.Add($"[{after.Source}] {after.Content}");
        }

        var sb = new StringBuilder();

        if (reactionLines is { Count: > 0 })
            foreach (var r in reactionLines)
                changes.Add(r);

        if (changes.Count > 0)
        {
            sb.Append("\nChanges since last message:");
            foreach (var c in changes)
                sb.Append($"\n- {c}");
        }
        else
        {
            sb.Append("\nNo context changes since last message.");
        }

        AppendWorktrees(sb, worktrees, leadingBlankLine: false);

        return Frame(sb, now, device, input, currentId, agentName, messageUid);
    }

    /// <summary>
    /// Where this discussion works, stated on every turn (not only when it changes) so a model
    /// resumed from any point still knows which folders are its working tree.
    /// </summary>
    private static void AppendWorktrees(StringBuilder sb, IReadOnlyList<WorktreeAttachment>? worktrees, bool leadingBlankLine)
    {
        if (worktrees is not { Count: > 0 }) return;
        if (leadingBlankLine) sb.Append('\n');
        foreach (var worktree in worktrees)
            sb.Append('\n').Append(worktree.ContextLine());
    }

    public static string SerializeSnapshot(ContextSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, JsonOpts);

    public static ContextSnapshot? DeserializeSnapshot(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<ContextSnapshot>(json, JsonOpts); }
        catch { return null; }
    }

    public static Dictionary<string, object?> BuildMetadata(
        ContextSnapshot snapshot, DateTime now, ResolvedDevice device, string input,
        string currentId, string? agentName)
    {
        var active = snapshot.Discussions.Where(d => !DiscussionStatus.IsClosed(d.Status)).ToList();
        var archived = snapshot.Discussions.Where(d => DiscussionStatus.IsClosed(d.Status)).ToList();

        return new Dictionary<string, object?>
        {
            ["timestamp"] = now.ToString("o"),
            ["day"] = now.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture),
            ["device"] = device.ShortLabel,
            ["deviceType"] = device.Type,
            ["devicePlatform"] = device.Platform,
            ["deviceRoom"] = device.Room,
            ["input"] = input,
            ["discussion"] = currentId,
            ["agent"] = agentName,
            ["activeDiscussionCount"] = active.Count,
            ["archivedDiscussionCount"] = archived.Count,
            ["otherAgentDiscussionCount"] = snapshot.OtherAgentDiscussions.Count,
            ["extensionContextCount"] = snapshot.ExtensionContexts.Count,
            ["outfit"] = snapshot.Outfit,
        };
    }

    private static void AppendOpenTag(StringBuilder sb, DateTime now, ResolvedDevice device,
        string input, string currentId, string? agentName, string? messageUid)
    {
        // Device name, type and platform come from the client's User-Agent; the agent name and room
        // are user-editable entities; a caller can choose the message uid.
        static string A(string value) => NovaEventText.AttributeText(value);
        var messageAttr = messageUid != null ? $" message=\"{A(messageUid)}\"" : "";
        var agentAttr = agentName != null ? $" agent=\"{A(agentName)}\"" : "";
        var roomAttr = device.Room != null ? $" room=\"{A(device.Room)}\"" : "";
        sb.Append($"<nova-context timestamp=\"{now:yyyy-MM-ddTHH:mm:ssZ}\" day=\"{now.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture)}\" device=\"{A(device.ShortLabel)}\" device_type=\"{A(device.Type)}\" platform=\"{A(device.Platform)}\" input=\"{A(input)}\" discussion=\"{A(currentId)}\"{messageAttr}{agentAttr}{roomAttr}>");
    }

    private static string FormatRelativeTime(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}min ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    private static Func<ContextSnapshot.DiscussionEntry, string> TitleResolver(
        IEnumerable<ContextSnapshot.DiscussionEntry> peers)
    {
        var resolve = DiscussionDisplayTitle.Resolver(peers.Select(DiscussionDisplayTitle.Of));
        return d => resolve(DiscussionDisplayTitle.Of(d)) ?? "(untitled)";
    }
}
