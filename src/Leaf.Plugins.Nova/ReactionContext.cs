using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>One add or remove from the <c>message-reactions</c> stream.</summary>
public sealed record ReactionEntry(
    long Id, string MessageKey, string Emoji, bool Remove,
    string ActorType, string ActorId, string ActorName, DateTimeOffset At)
{
    /// <summary>The message the key addresses: the UI keys a later segment of a turn as "uid:segment:N".</summary>
    public string TargetUid => ReactionContext.BaseUid(MessageKey);

    public static ReactionEntry Read(LeafRecord record)
    {
        var d = record.Data;
        return new(record.Id,
            Str(d, "message_key") ?? "",
            Str(d, "emoji") ?? "",
            Str(d, "action") == "remove",
            Str(d, "actor_type") ?? "user",
            Str(d, "actor_id") ?? record.UserId ?? "",
            Str(d, "actor_name") ?? "",
            record.CreatedAt);
    }

    private static string? Str(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>
/// The last reaction record a context or reaction note already carried to the agent. Reactions
/// after it are news; the timestamp bounds the stream read, the id breaks timestamp ties.
/// </summary>
public sealed record ReactionCursor(long Id, DateTimeOffset At)
{
    public const string DataKey = "reactions_noted";

    public JsonObject ToJson() => new() { ["id"] = Id, ["at"] = At.ToString("O") };

    public static ReactionCursor? Read(JsonObject data)
        => data[DataKey] is JsonObject cursor
            && cursor["id"] is JsonValue id && id.TryGetValue<long>(out var value)
            && cursor["at"] is JsonValue at && at.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
                ? new(value, parsed)
                : null;
}

/// <summary>A message a reaction can point at, as the agent should picture it.</summary>
public sealed record ReactionTarget(string Uid, string Role, string? SenderAgentId, string Content, DateTime At);

/// <summary>
/// What reactions point at: the discussion's visible messages by uid, and which one is the
/// agent's latest. Built from Nova's discussion stream (user messages, direct Nova messages,
/// events) and the canonical RedCompute transcript.
/// </summary>
public sealed class ReactionTranscript
{
    private readonly Dictionary<string, ReactionTarget> _targets = new(StringComparer.Ordinal);
    private readonly string? _agentId;

    private ReactionTranscript(string? agentId) => _agentId = agentId;

    public string? LatestAgentUid { get; private set; }

    public ReactionTarget? Find(string uid) => _targets.GetValueOrDefault(uid);

    public bool Contains(string uid) => _targets.ContainsKey(uid);

    public bool IsOwnAgentMessage(ReactionTarget target)
        => target.Role == "assistant"
            && (target.SenderAgentId is null || string.Equals(target.SenderAgentId, _agentId, StringComparison.OrdinalIgnoreCase));

    public static ReactionTranscript Build(
        IEnumerable<DiscussionMessage> streamMessages,
        IEnumerable<SessionMessage>? sessionMessages,
        string? agentId)
    {
        var transcript = new ReactionTranscript(agentId);
        // A message persisted in the discussion stream is what the UI shows, so it wins.
        foreach (var message in streamMessages)
        {
            var uid = message.Metadata["uid"] is JsonValue v && v.TryGetValue<string>(out var u) ? u : null;
            if (string.IsNullOrWhiteSpace(uid) || transcript._targets.ContainsKey(uid)) continue;
            var source = message.Metadata["source"] is JsonValue s && s.TryGetValue<string>(out var src) ? src : "";
            var role = source.StartsWith("event:", StringComparison.Ordinal) ? "event" : message.Role;
            if (role is not ("user" or "assistant" or "event")) continue;
            if (source == "queued-user-message") continue;
            var content = role == "user" ? ConversationExporter.StripInjectedTags(message.Content) : message.Content;
            if (string.IsNullOrWhiteSpace(content) || NovaNoReply.IsMarker(content)) continue;
            var sender = message.Metadata["sender_agent_id"] is JsonValue a && a.TryGetValue<string>(out var id) ? id : null;
            transcript._targets[uid] = new(uid, role, sender, content, message.CreatedAt.UtcDateTime);
        }

        if (sessionMessages is not null)
        {
            var turns = ConversationExporter.CollapseMessages(sessionMessages.ToList())
                .Where(message => message.EventType == "text"
                    && message.Role is "user" or "assistant"
                    && !string.IsNullOrWhiteSpace(message.MessageUid)
                    && !string.IsNullOrWhiteSpace(message.Content)
                    && !NovaNoReply.IsMarker(message.Content))
                .GroupBy(message => message.MessageUid!, StringComparer.Ordinal);
            foreach (var turn in turns)
            {
                if (transcript._targets.ContainsKey(turn.Key)) continue;
                // The final answer is what the person read; commentary stands in only without one.
                var selected = turn
                    .Select((message, index) => (message, index, priority: message.Phase switch
                    {
                        "final_answer" => 3,
                        null or "" => 2,
                        "commentary" => 1,
                        _ => 0,
                    }))
                    .OrderByDescending(item => item.priority)
                    .ThenByDescending(item => item.index)
                    .First().message;
                transcript._targets[turn.Key] = new(turn.Key, selected.Role, null, selected.Content!,
                    turn.Max(message => message.Timestamp));
            }
        }

        transcript.LatestAgentUid = transcript._targets.Values
            .Where(transcript.IsOwnAgentMessage)
            .OrderBy(target => target.At)
            .Select(target => target.Uid)
            .LastOrDefault();
        return transcript;
    }
}

/// <summary>
/// Renders reactions for the agent. Emoji carry no assigned meaning: each line says who did what
/// to which message (the agent's latest or an earlier one, and whose), with enough of the message
/// to read the emoji in context.
/// </summary>
public static partial class ReactionContext
{
    public const int ExcerptLength = 280;

    /// <summary>"uid:segment:2" (a later segment of one turn in the UI) addresses the turn "uid".</summary>
    public static string BaseUid(string messageKey)
    {
        var segment = messageKey.IndexOf(":segment:", StringComparison.Ordinal);
        return segment > 0 ? messageKey[..segment] : messageKey;
    }

    /// <summary>One line, at most <see cref="ExcerptLength"/> characters, never splitting a surrogate pair.</summary>
    public static string Excerpt(string content)
    {
        var line = Whitespace().Replace(content, " ").Trim();
        if (line.Length <= ExcerptLength) return line;
        var cut = ExcerptLength - 1;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut].TrimEnd() + "…";
    }

    /// <summary>
    /// The changes that still stand, in order: per actor, message and emoji, an add followed by a
    /// remove (or the reverse) inside <paramref name="entries"/> cancels out.
    /// </summary>
    public static List<ReactionEntry> Net(IEnumerable<ReactionEntry> entries)
    {
        var groups = new Dictionary<(string, string, string), (ReactionEntry First, ReactionEntry Last)>();
        foreach (var entry in entries.OrderBy(item => item.Id))
        {
            var key = (entry.TargetUid, entry.Emoji, entry.ActorId);
            groups[key] = groups.TryGetValue(key, out var seen) ? (seen.First, entry) : (entry, entry);
        }
        return groups.Values
            .Where(group => group.First.Remove == group.Last.Remove)
            .Select(group => group.Last)
            .OrderBy(entry => entry.Id)
            .ToList();
    }

    /// <summary>The reactions currently on each message: adds that no later remove cancelled.</summary>
    public static Dictionary<string, List<ReactionEntry>> Standing(IEnumerable<ReactionEntry> entries)
    {
        var state = new Dictionary<(string, string, string), ReactionEntry>();
        foreach (var entry in entries.OrderBy(item => item.Id))
        {
            var key = (entry.TargetUid, entry.Emoji, entry.ActorId);
            if (entry.Remove) state.Remove(key);
            else state[key] = entry;
        }
        return state.Values
            .GroupBy(entry => entry.TargetUid, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.Id).ToList(), StringComparer.Ordinal);
    }

    public static string Line(ReactionEntry entry, ReactionTranscript transcript, string? agentId, string? ownerName = null)
    {
        var self = entry.ActorType == "agent"
            && string.Equals(entry.ActorId, agentId, StringComparison.OrdinalIgnoreCase);
        var actor = self ? "You" : string.IsNullOrWhiteSpace(entry.ActorName) ? "Someone" : entry.ActorName;
        var sb = new StringBuilder(actor)
            .Append(entry.Remove ? " removed " : " reacted ")
            .Append(entry.Emoji)
            .Append(entry.Remove ? " from " : " to ");

        var target = transcript.Find(entry.TargetUid);
        if (target is null)
            return sb.Append("a message that is no longer in this discussion").ToString();

        if (transcript.IsOwnAgentMessage(target))
            sb.Append(target.Uid == transcript.LatestAgentUid ? "your latest message" : "an earlier message of yours");
        else if (target.Role == "assistant")
            sb.Append("another agent's message");
        else if (target.Role == "event")
            sb.Append("an event");
        else if (entry.ActorType == "user")
            sb.Append("their own message");
        else
            sb.Append(string.IsNullOrWhiteSpace(ownerName) ? "the user's message" : $"{ownerName}'s message");

        return sb.Append(": \"").Append(Excerpt(target.Content)).Append('"').ToString();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
