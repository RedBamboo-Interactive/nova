using System.Globalization;
using System.Text.Json;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

internal sealed record DelegationExchangeEntry(
    string Uid, string Kind, string Content, DateTimeOffset Timestamp, string? MessageUid = null);
internal sealed record DelegationExchangeView(
    string? Title, string? Repository, IReadOnlyList<DelegationExchangeEntry> Entries);

// The discussion supplies membership and event identity. Only the authorized worker
// transcript supplies brief and reply text. No provider tools or commentary leave it.
internal static class DelegationExchange
{
    internal sealed record Event(
        string Uid, string Kind, string Content, DateTimeOffset Timestamp,
        string? PromptUid, string? Repository, bool CompletedOwnTurn);

    public static async Task<DelegationExchangeView?> ReadAsync(
        IDiscussions discussions, RedComputeClient compute, DiscussionRead current, string sessionId,
        CancellationToken ct)
    {
        var events = await ReadEventsAsync(discussions, current, sessionId, ct);
        if (!events.Any(item => item.Kind is "brief" or "follow-up")) return null;

        var missing = events.Where(item => item.PromptUid is not null && item.Kind is ("brief" or "follow-up"))
            .Select(item => item.PromptUid!).ToHashSet(StringComparer.Ordinal);
        var transcript = new List<SessionMessage>();
        SessionPageInfo? session = null;
        string? before = null;
        string? epoch = null;
        // Keep an activity inspection bounded even in a very long-lived worker.
        // Older events retain their status and link to the canonical Code message.
        for (var pageIndex = 0; pageIndex < 8; pageIndex++)
        {
            var result = await compute.GetTranscriptPageAsync(sessionId, 500, before: before, ct: ct);
            if (result.StatusCode is 401 or 403 or 404) return null;
            if (!result.Success || result.Value is not { } page || !page.Page.BoundaryComplete
                || (epoch is not null && epoch != page.Page.Epoch))
                throw new InvalidOperationException("Delegated session transcript is unavailable");
            using var envelope = JsonDocument.Parse(result.Content);
            if (!envelope.RootElement.TryGetProperty("session", out var metadata)
                || !CanProject(current, metadata)) return null;
            session ??= page.Session;
            epoch = page.Page.Epoch;
            transcript.InsertRange(0, page.Messages);
            foreach (var message in page.Messages)
                if (IsPrompt(message) && message.MessageUid is { } uid) missing.Remove(uid);
            if (missing.Count == 0 || !page.Page.HasEarlier) break;
            if (page.Page.OldestCursor is not { Length: > 0 } cursor || cursor == before)
                throw new InvalidOperationException("Delegated session transcript did not advance");
            before = cursor;
        }

        return new(session?.Title, events.LastOrDefault(item => item.Repository is not null)?.Repository,
            ProjectEntries(events, transcript));
    }

    internal static bool CanProject(DiscussionRead current, JsonElement session)
        => !string.IsNullOrWhiteSpace(Text(session, "repositoryId"))
            && !string.IsNullOrWhiteSpace(current.OwnerId)
            && string.Equals(current.OwnerId, Text(session, "userId"), StringComparison.OrdinalIgnoreCase)
            && session.TryGetProperty("confidential", out var confidentiality)
            && (confidentiality.ValueKind == JsonValueKind.False
                || (confidentiality.ValueKind == JsonValueKind.True && current.Confidential));

    private static async Task<Event[]> ReadEventsAsync(
        IDiscussions discussions, DiscussionRead current, string sessionId, CancellationToken ct)
    {
        var found = new Dictionary<string, (long Id, Event Event)>(StringComparer.Ordinal);
        long? before = null;
        do
        {
            var page = await discussions.GetMessagePageAsync(current.EntityId, new(Limit: 1000, BeforeId: before), ct);
            foreach (var row in page.Messages)
                if (ParseEvent(row, sessionId) is { } item
                    && (!found.TryGetValue(item.Uid, out var previous) || previous.Id < row.Id))
                    found[item.Uid] = (row.Id, item);
            if (!page.HasEarlier) break;
            if (page.OldestId is not { } next || next == before)
                throw new InvalidOperationException("Delegation event history did not advance");
            before = next;
        } while (true);

        return found.Values.OrderBy(row => row.Event.Timestamp).ThenBy(row => row.Id)
            .Select(row => row.Event).ToArray();
    }

    internal static Event? ParseEvent(DiscussionMessage row, string sessionId)
    {
        var source = row.Metadata["source"]?.ToString();
        var launch = source == "event:delegation";
        var callback = source == "event:delegate:" + sessionId;
        if ((!launch && !callback) || (callback && row.Role != "user") || EventSender.Read(row.Metadata) is not null
            || row.Metadata["parts_json"]?.ToString() is not { } parts
            || (launch && DelegationActivity.ParseLink(row)?.SessionId != sessionId)) return null;
        try
        {
            using var document = JsonDocument.Parse(parts);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var part in document.RootElement.EnumerateArray())
            {
                if (Text(part, "type") != "event_data" || !part.TryGetProperty("data", out var data)
                    || Text(data, "sessionId") != sessionId) continue;
                var prompt = Text(data, "promptMessageUid");
                if (!DelegationActivity.ValidSessionId(prompt)) prompt = null;
                var kind = launch ? Flag(data, "continued") ? "follow-up" : "brief" : "update";
                var uid = row.Metadata["uid"]?.ToString() ?? row.Id.ToString(CultureInfo.InvariantCulture);
                var complete = callback && prompt is not null
                    && Text(data, "turnId") == prompt && Text(data, "deliveredMessageUid") == prompt
                    && Flag(data, "executionObserved") && Text(data, "outcome") == "completed"
                    && Text(data, "status") == "Idle" && Text(data, "stopReason") == "completed"
                    && Text(data, "trigger") is null
                    && (!data.TryGetProperty("deliverySequence", out var sequence) || sequence.ValueKind == JsonValueKind.Null
                        || sequence.ValueKind == JsonValueKind.Number && sequence.TryGetInt32(out var number) && number <= 1);
                var content = callback ? RemoveCallbackFrame(row.Content) : row.Content;
                return new(uid, kind, content, row.CreatedAt, prompt, launch ? Text(data, "repository") : null, complete);
            }
        }
        catch (JsonException) { }
        return null;
    }

    internal static IReadOnlyList<DelegationExchangeEntry> ProjectEntries(
        IReadOnlyList<Event> events, IReadOnlyList<SessionMessage> transcript)
    {
        // A later retry/continuation is a different prompt. A user record can contain several
        // parts, but its first text record anchors the exact message stored for that dispatch.
        var prompts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < transcript.Count; index++)
            if (IsPrompt(transcript[index]) && transcript[index].MessageUid is { } uid)
                prompts.TryAdd(uid, index);
        var dispatches = events.Where(item => item.PromptUid is not null && item.Kind is ("brief" or "follow-up"))
            .Select(item => item.PromptUid!).ToHashSet(StringComparer.Ordinal);

        return events.Select(item =>
        {
            if (item.PromptUid is not { } uid || !prompts.TryGetValue(uid, out var index))
                return new DelegationExchangeEntry(item.Uid, item.Kind, item.Content, item.Timestamp,
                    item.Kind == "update" ? null : item.PromptUid);
            if (item.Kind is "brief" or "follow-up")
                return new(item.Uid, item.Kind, transcript[index].Content!, item.Timestamp, uid);
            if (item.CompletedOwnTurn && dispatches.Contains(uid)
                && ReadReply(transcript, index, item.Timestamp) is { } reply)
                return new(item.Uid, "reply", reply.Content, item.Timestamp, reply.Uid);
            return new(item.Uid, item.Kind, item.Content, item.Timestamp);
        }).ToArray();
    }

    private static (string Uid, string Content)? ReadReply(
        IReadOnlyList<SessionMessage> transcript, int promptIndex, DateTimeOffset reportedAt)
    {
        var promptUid = transcript[promptIndex].MessageUid;
        string? replyUid = null;
        var text = new List<string>();
        for (var index = promptIndex + 1; index < transcript.Count; index++)
        {
            var record = transcript[index];
            if (record.ParentToolUseId is not null) continue;
            if ((record.Role == "user" && record.MessageUid != promptUid) || record.TurnOrigin is not null)
                return null;
            if (record.EventType == "status")
            {
                if (string.Equals(record.Content, "idle", StringComparison.OrdinalIgnoreCase))
                    return replyUid is not null && record.Timestamp.ToUniversalTime() <= reportedAt.UtcDateTime
                        ? (replyUid, string.Join("\n\n", text)) : null;
                if (string.Equals(record.Content, "stopped", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(record.Content, "error", StringComparison.OrdinalIgnoreCase)) return null;
            }

            if (record.Role != "assistant" || record.EventType != "text" || record.Phase != "final_answer"
                || string.IsNullOrWhiteSpace(record.Content) || record.MessageUid is not { Length: > 0 } uid) continue;
            if (replyUid is not null && replyUid != uid) return null;
            replyUid = uid;
            text.Add(record.Content);
        }
        return null;
    }

    private static bool IsPrompt(SessionMessage record) => record.Role == "user" && record.EventType == "text"
        && !string.IsNullOrWhiteSpace(record.Content);
    private static string? Text(JsonElement value, string property) => DelegationActivity.String(value, property);
    private static bool Flag(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var state) && state.ValueKind == JsonValueKind.True;

    private static string RemoveCallbackFrame(string content)
    {
        var open = content.IndexOf('>');
        const string close = "</nova-event>";
        return content.StartsWith("<nova-event ", StringComparison.Ordinal) && open >= 0
            && content.EndsWith(close, StringComparison.Ordinal)
            ? content[(open + 1)..^close.Length].Trim() : content;
    }
}
