using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

internal sealed record ExchangeNote(
    string Uid, string Direction, string Content, string? AgentId, DateTimeOffset Timestamp, bool Failed);

/// <summary>Read the current discussion's durable copies only. The session's injected
/// input carrier and the other discussion's mirror would repeat those same notes.</summary>
internal static partial class AgentNoteExchange
{
    public static async Task<IReadOnlyList<ExchangeNote>> ReadAsync(
        IDiscussions discussions, DiscussionRead current, DiscussionRead other, CancellationToken ct)
    {
        // Take a stable newest boundary so a discussion receiving notes while this
        // read runs cannot extend its work indefinitely.
        var newest = await discussions.GetMessagePageAsync(current.EntityId, new(Limit: 1), ct);
        if (newest.NewestId is not { } end) return [];
        var selected = new Dictionary<string, (long Id, ExchangeNote Note)>(StringComparer.Ordinal);
        const int pageSize = 1000;
        long after = 0;
        while (after < end)
        {
            var page = await discussions.GetMessagesAsync(current.EntityId, pageSize, after, ct);
            foreach (var row in page)
            {
                if (row.Id <= end && Project(row, current, other) is { } note
                    && (!selected.TryGetValue(note.Uid, out var previous) || row.Id > previous.Id))
                    selected[note.Uid] = (row.Id, note);
            }
            if (page.Count < pageSize) break;
            var next = page[^1].Id;
            if (next <= after) throw new InvalidOperationException("Discussion note exchange did not advance");
            after = next;
        }
        return selected.Values.OrderBy(row => row.Note.Timestamp).ThenBy(row => row.Id)
            .Select(row => row.Note).ToArray();
    }

    internal static ExchangeNote? Project(DiscussionMessage row, DiscussionRead current, DiscussionRead other)
    {
        var source = ReadText(row.Metadata, "source");
        if (source?.StartsWith("event:", StringComparison.Ordinal) != true) return null;
        var uid = ReadText(row.Metadata, "uid") ?? row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var timestamp = DateTimeOffset.TryParse(ReadText(row.Metadata, "timestamp"), out var stampedTime)
            ? stampedTime : row.CreatedAt;
        var sender = EventSender.Read(row.Metadata);

        if (source == "event:" + SentNote.Source && row.Role == "system" && sender is null)
        {
            var data = ReadSentData(row.Metadata);
            if (data is null || !SameDiscussion(ReadText(data, "targetDiscussionId"), other.Id)) return null;
            var failed = data["failed"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
            return new(uid, "sent", row.Content, current.AgentId, timestamp, failed);
        }

        // Match the trusted discussion stamp first. An old source convention is
        // only considered when the record predates signed sender stamps.
        var eventSource = source["event:".Length..];
        var lead = InternalLead().Match(row.Content);
        var isLegacySource = SameDiscussion(eventSource, other.Id)
            || (LegacySource().Match(eventSource) is { Success: true } legacy
                && SameDiscussion(legacy.Groups[1].Value, other.Id));
        var isDiscussionActivity = SameDiscussion(eventSource, "discussion:" + other.Id)
            && SameDiscussion(lead.Groups[1].Value, other.Id);
        if (sender is not null)
        {
            if (!SameDiscussion(sender.DiscussionId, other.Id)
                || (eventSource != EventSender.DefaultSource && !isLegacySource && !isDiscussionActivity))
                return null;
        }
        else if (!(lead.Success && (isLegacySource || isDiscussionActivity)))
        {
            return null;
        }

        var body = lead.Success ? row.Content[lead.Length..].Trim() : row.Content;
        return new(uid, "received", body.Length > 0 ? body : row.Content, sender?.AgentId ?? other.AgentId, timestamp, false);
    }

    private static JsonObject? ReadSentData(JsonObject metadata)
    {
        if (ReadText(metadata, "parts_json") is not { } json) return null;
        try
        {
            return (JsonNode.Parse(json) as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(part => ReadText(part, "type") == "event_data"
                    && ReadText(part, "source") == SentNote.Source)?["data"] as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    private static string? ReadText(JsonObject obj, string key)
        => obj[key] is JsonValue item && item.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : null;

    private static bool SameDiscussion(string? candidate, string id)
        => string.Equals(candidate, id, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\[internal\]\s*(?:From\s+([0-9a-f]{8})\b\s*(?:\([^\r\n)]*\))?\s*[:.]\s*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InternalLead();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*?-([0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacySource();

}
