using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

internal static class ConversationRevision
{
    public static long Acknowledge(long currentRevision, long currentReadRevision, long throughRevision)
        => Math.Max(currentReadRevision, Math.Min(throughRevision, currentRevision));
}

/// <summary>
/// Converts provider-neutral, human-visible assistant turns into Nova's durable
/// conversation revision. Timeline events and proactive messages have separate
/// persistence paths and are deliberately excluded from transcript settlement.
/// </summary>
public sealed class ConversationUnread(
    DiscussionStore store,
    IDiscussions discussions,
    RedComputeClient redCompute)
{
    public Task<DiscussionRead> EnsureBaselineAsync(
        DiscussionRead discussion, CancellationToken ct = default)
        => discussion.LastProcessedSessionAssistantUid is not null
            ? Task.FromResult(discussion)
            : ReconcileAsync(discussion, ct);

    public Task<DiscussionRead> ReconcileSettledAsync(
        DiscussionRead discussion, CancellationToken ct = default)
        => ReconcileAsync(discussion, ct);

    private async Task<DiscussionRead> ReconcileAsync(
        DiscussionRead discussion, CancellationToken ct)
    {
        if (discussion.Type == HeartbeatService.DiscussionType)
            return discussion;

        if (discussion.SessionId is null)
            return await store.ReconcileConversationAsync(discussion.EntityId, null, ct)
                ?? discussion;

        var snapshot = await redCompute.GetSessionAsync(discussion.SessionId, ct);
        if (snapshot is null || snapshot.Status != "Idle")
            return discussion;

        var records = await discussions.GetMessagesAsync(discussion.EntityId, ct: ct);
        var directMessageUids = records
            .Where(message => message.Role == "assistant"
                && message.Metadata["source"]?.GetValue<string>() == "nova-message")
            .Select(message => message.Metadata["uid"]?.GetValue<string>())
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .Select(uid => uid!)
            .ToHashSet(StringComparer.Ordinal);

        var latestUid = FindLatestAssistantUid(snapshot.Messages, directMessageUids);
        return await store.ReconcileConversationAsync(discussion.EntityId, latestUid, ct)
            ?? discussion;
    }

    internal static string? FindLatestAssistantUid(
        List<SessionMessage> rawMessages,
        IReadOnlySet<string> directMessageUids)
        => ConversationExporter.CollapseMessages(rawMessages)
            .Where(message => message.Role == "assistant"
                && message.EventType == "text"
                && !string.IsNullOrWhiteSpace(message.Content)
                && !string.IsNullOrWhiteSpace(message.MessageUid)
                && !directMessageUids.Contains(message.MessageUid))
            .OrderBy(message => message.Timestamp)
            .Select(message => message.MessageUid)
            .LastOrDefault();
}
