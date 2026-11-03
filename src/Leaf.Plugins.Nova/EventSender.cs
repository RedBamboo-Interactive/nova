using System.Security.Claims;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Who wrote an event, as the server states it: taken from the caller's signed execution
/// identity, never from the request. An Agent's text reaches its session as a user-role turn, so
/// this is what tells the model and the reader that it isn't the person.
/// </summary>
public sealed record EventSender(string AgentId, string? DiscussionId, string? Title, RequestReference? RequestedIn = null)
{
    /// <summary>The source of a signed Agent's event that doesn't name one.</summary>
    public const string DefaultSource = "agent-note";

    private const int MaxTitleLength = 120;

    private const string StampedKey = "sender_stamped";
    private const string AgentKey = "sender_agent_id";
    private const string DiscussionKey = "sender_discussion_id";
    private const string TitleKey = "sender_title";
    private const string RequestDiscussionKey = "request_discussion_id";
    private const string RequestMessageKey = "request_message_uid";

    /// <summary>
    /// The sender of this call, or null when the caller isn't a signed Agent. A confidential
    /// discussion's title never reaches one that isn't confidential; neither does its id, so a
    /// note from there gives the target no hint of where it came from.
    /// </summary>
    public static async Task<EventSender?> ResolveAsync(
        ClaimsPrincipal principal, DiscussionStore store, DiscussionRead? target, CancellationToken ct)
    {
        if (DiscussionAccessPolicy.ReadSignedActor(principal) is not { IsAgent: true } actor) return null;
        if (actor.DiscussionId is not { } discussionId) return new(actor.Id, null, null);

        var from = await store.GetAsync(discussionId, ct);
        if (from is null) return new(actor.Id, discussionId, null);
        if (from.Confidential && target?.Confidential != true) return new(actor.Id, null, null);
        return new(actor.Id, discussionId, NovaEventText.Field(DiscussionDisplayTitle.For(from), MaxTitleLength));
    }

    /// <summary>Reads the sender back from a stream record; only a server-stamped one has it.</summary>
    public static EventSender? Read(JsonObject metadata)
    {
        if (metadata[StampedKey] is not JsonValue stamped
            || !stamped.TryGetValue<bool>(out var isStamped) || !isStamped
            || Text(metadata, AgentKey) is not { } agentId)
            return null;
        return new(agentId, Text(metadata, DiscussionKey), Text(metadata, TitleKey),
            Text(metadata, RequestDiscussionKey) is { } discussion && Text(metadata, RequestMessageKey) is { } message
                ? new RequestReference(discussion, message)
                : null);
    }

    public void WriteTo(JsonObject metadata)
    {
        metadata[StampedKey] = true;
        metadata[AgentKey] = AgentId;
        metadata[DiscussionKey] = DiscussionId;
        metadata[TitleKey] = Title;
        if (RequestedIn is null) return;
        metadata[RequestDiscussionKey] = RequestedIn.DiscussionId;
        metadata[RequestMessageKey] = RequestedIn.MessageUid;
    }

    /// <summary>What clients read: the overlay DTO, the live bus event, and the discussion detail.</summary>
    public object ToDto() => new { agentId = AgentId, discussionId = DiscussionId, title = Title };

    /// <summary>The frame's sender attributes, built only from escaped values.</summary>
    public string FrameAttributes(string? agentName)
    {
        var attributes = $" from-agent=\"{NovaEventText.Attribute(agentName ?? "An Agent")}\" from-agent-id=\"{NovaEventText.Attribute(AgentId)}\"";
        if (DiscussionId is not null) attributes += $" from-discussion=\"{NovaEventText.Attribute(DiscussionId)}\"";
        if (Title is not null) attributes += $" from-title=\"{NovaEventText.Attribute(Title, MaxTitleLength * 2)}\"";
        return attributes;
    }

    private static string? Text(JsonObject metadata, string key)
        => metadata[key] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text) ? text : null;
}

/// <summary>"He asked for this there": the message a signed Agent names when it starts work he asked for.</summary>
public sealed record RequestReference(string DiscussionId, string MessageUid);
