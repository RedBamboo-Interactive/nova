using Nova.App.Data.Entities;
using RedBamboo.AppHost.Streams;

namespace Nova.App.Services;

/// <summary>
/// Mirrors Nova's data to RedLeaf: discussions as `discussion` entities,
/// chat messages as `nova-messages` records, AI invocations as
/// `nova-invocations` records, automation runs as `automation-runs` records.
/// All publishing is fire-and-forget; a null Client makes every call a no-op.
/// </summary>
public static class NovaMirror
{
    public static RedLeafStreamClient? Client { get; set; }
    public static string? AgentId { get; set; }
    public static string? AvatarUrl { get; set; }
    public static string? UserId { get; set; }

    public static string DiscussionSlug(string discussionId)
    {
        var sanitized = new string(discussionId.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return $"discussion-nova-{sanitized}";
    }

    public static void PublishDiscussion(Discussion d) => Client?.UpsertEntity(
        DiscussionSlug(d.Id), "discussion",
        d.Title ?? $"Discussion {d.Id}",
        new
        {
            discussion_id = d.Id,
            agent = d.AgentId ?? AgentId,
            // Nullable title mirrored verbatim — the entity name has a
            // placeholder fallback, so reads can't recover null from it.
            title = d.Title,
            status = d.Status,
            owner = UserId,
            owner_id = d.OwnerId,
            session_id = d.SessionId,
            message_count = d.MessageCount,
            created_at = new DateTimeOffset(DateTime.SpecifyKind(d.CreatedAt, DateTimeKind.Utc)).ToString("O"),
            last_activity = new DateTimeOffset(DateTime.SpecifyKind(d.LastActivity, DateTimeKind.Utc)).ToString("O"),
            last_read_at = d.LastReadAt is { } lr
                ? new DateTimeOffset(DateTime.SpecifyKind(lr, DateTimeKind.Utc)).ToString("O") : null,
            app = "nova",
        });

    public static void PublishMessages(IReadOnlyList<ConversationRecord> records)
    {
        if (Client is null) return;
        foreach (var m in records)
        {
            Client.EnqueueForEntity("nova-messages", DiscussionSlug(m.ContextId), new
            {
                discussion_id = m.ContextId,
                role = m.Role,
                content = m.Content,
                parts_json = m.PartsJson,
                source = m.Source,
                sender_agent_id = m.SenderAgentId,
                timestamp = new DateTimeOffset(DateTime.SpecifyKind(m.Timestamp, DateTimeKind.Utc)).ToString("O"),
            }, userId: m.UserId);
        }
    }

    public static void PublishInvocation(string purpose, string? contextId, string? promptSnippet,
        string? responseSnippet, long durationMs, bool success, string? model, string? userId) =>
        Client?.Enqueue("nova-invocations", new
        {
            purpose,
            context_id = contextId,
            prompt_snippet = promptSnippet,
            response_snippet = responseSnippet,
            duration_ms = durationMs,
            success,
            model,
        }, userId: userId);

    public static void PublishAutomationRun(string name, bool triggered, string? summary, string? error)
    {
        if (Client is null) return;
        var sanitized = new string(name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        Client.EnqueueForEntity("automation-runs", $"automation-nova-{sanitized}", new
        {
            automation = name,
            triggered,
            summary,
            error,
        });
    }
}
