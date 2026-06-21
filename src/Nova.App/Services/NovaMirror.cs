using Nova.App.Data.Entities;
using RedBamboo.AppHost.Streams;

namespace Nova.App.Services;

/// <summary>
/// Mirrors Nova's data to RedLeaf (Phase 3 of the suite storage
/// consolidation): discussions as `discussion` entities (versioning off),
/// chat messages as `nova-messages` records, AI invocations as
/// `nova-invocations` records, and the identity/persona files as versioned
/// `agent-file` entities. All publishing is fire-and-forget; a null Client
/// (RedLeaf not configured yet) makes every call a no-op.
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

    public static void PublishAgentFile(string fileKey, string content) => Client?.UpsertEntity(
        $"nova-{fileKey}", "agent-file",
        $"Nova {AgentFileSync.DisplayName(fileKey)}",
        new { app = "nova", file_key = fileKey, content });

    public static string AutomationSlug(string name)
    {
        var sanitized = new string(name.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return $"automation-nova-{sanitized}";
    }

    private static HashSet<string> _knownAutomations = [];

    /// <summary>
    /// Publishes the full automation list. Run state (last_run, next_run,
    /// errors) is deliberately excluded — `automation` is a versioned type
    /// and only definition edits should produce version rows; per-run results
    /// go to the automation-runs stream instead. Automations that vanished
    /// since the last publish get a tombstone upsert.
    /// </summary>
    public static void PublishAutomations(IReadOnlyList<Automation> automations)
    {
        if (Client is null) return;

        var current = new HashSet<string>(automations.Select(a => a.Name));
        foreach (var a in automations)
        {
            Client.UpsertEntity(AutomationSlug(a.Name), "automation", a.Name, new
            {
                automation_name = a.Name,
                description = a.Description,
                schedule = a.Schedule,
                enabled = a.Enabled,
                action_type = a.ActionType,
                action_config_json = a.ActionConfigJson,
                report_to_discussion_id = a.ReportToDiscussionId,
                remove_on_trigger = a.RemoveOnTrigger,
                expires_at = a.ExpiresAt is { } exp ? new DateTimeOffset(DateTime.SpecifyKind(exp, DateTimeKind.Utc)).ToString("O") : null,
                max_failures = a.MaxFailures,
                owner_id = a.OwnerId,
                icon = a.Icon,
                app = "nova",
            });
        }

        foreach (var gone in _knownAutomations.Except(current))
            Client.UpsertEntity(AutomationSlug(gone), "automation", gone,
                new { automation_name = gone, removed = true, app = "nova" });

        _knownAutomations = current;
    }

    public static void PublishAutomationRun(string name, bool triggered, string? summary, string? error) =>
        Client?.EnqueueForEntity("automation-runs", AutomationSlug(name), new
        {
            automation = name,
            triggered,
            summary,
            error,
        });
}
