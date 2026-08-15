using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Discussion lifecycle vocabulary. <c>archiving</c> is the durable archive intent:
/// the discussion is closed to the user, but its RedCompute session has not been
/// confirmed stopped yet. Only the archive finalizer may move it on to
/// <c>archived</c>; no writer may ever leave either state otherwise.
/// </summary>
public static class DiscussionStatus
{
    public const string Idle = "idle";
    public const string Thinking = "thinking";
    public const string Stopped = "stopped";
    public const string Archiving = "archiving";
    public const string Archived = "archived";

    public static bool IsClosed(string status) => status is Archived or Archiving;

    public static string? FromSessionStatus(string? sessionStatus, string discussionType)
        => sessionStatus switch
        {
            "Active" => Thinking,
            "Idle" or "Starting" => Idle,
            "Stopped" or "Error" => Stopped,
            _ => null,
        };
}

/// <summary>
/// Per-discussion-entity write gate. The kernel's entity patch and message post are
/// both read-modify-write over the whole data JSON with no concurrency control, so
/// two overlapping writes lose one of them — even when they touch different keys
/// (a last_activity touch can silently revert a just-written archived status).
/// Every writer of Nova discussion entities lives in this process, so serializing
/// per entity here is sufficient to prevent those lost updates.
/// </summary>
public static class DiscussionEntityGate
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public static async Task<T> RunAsync<T>(Guid entityId, Func<Task<T>> action, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(entityId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try { return await action(); }
        finally { gate.Release(); }
    }

    public static Task RunAsync(Guid entityId, Func<Task> action, CancellationToken ct = default)
        => RunAsync(entityId, async () => { await action(); return true; }, ct);
}

/// <summary>
/// A discussion as the plugin sees it — a flattened view of the <c>discussion</c>
/// entity's data. <see cref="EntityId"/> keys the message/reaction streams.
/// </summary>
public sealed record DiscussionRead(
    string Id, string? Title, string? SessionId, string Status,
    DateTime CreatedAt, DateTime LastActivity, int MessageCount,
    DateTime? LastReadAt, string? OwnerId, Guid EntityId,
    string? AgentId, string Type = "chat",
    string? LastContextJson = null, string? InjectedContext = null,
    string? QualityTier = null, string? Provider = null,
    bool Confidential = false);

/// <summary>
/// Centralized owner-scoping rules for user-owned resources. A resource is accessible
/// when it has no owner, is owned by the local user, or the caller is the owner.
/// </summary>
public static class OwnerScope
{
    public static bool CanAccess(string? ownerId, string? userId)
        => ownerId is null || ownerId == "local-user" || userId == "local-user" || ownerId == userId;
}

/// <summary>
/// Discussion rows as kernel <c>discussion</c> entities (single store — the SQLite era
/// is over). Same slugs and data keys the standalone app mirrored, so existing rows
/// carry over untouched: <c>discussion-nova-{id}</c> with <c>data.app = "nova"</c>.
/// </summary>
public sealed class DiscussionStore(IEntityStore entities, IDiscussions discussions)
{
    public static string Slug(string discussionId)
    {
        var sanitized = new string(discussionId.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return $"discussion-nova-{sanitized}";
    }

    public async Task<List<DiscussionRead>> ListAsync(string? filterAgentId = null, CancellationToken ct = default)
    {
        var items = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "discussion",
            DataEquals = new Dictionary<string, object?> { ["app"] = "nova" },
            Limit = 500,
        }, ct);

        var result = new List<DiscussionRead>();
        foreach (var item in items)
        {
            var d = Map(item);
            if (d == null) continue;
            if (filterAgentId != null && d.AgentId != null && d.AgentId != filterAgentId) continue;
            result.Add(d);
        }
        result.Sort((a, b) => b.LastActivity.CompareTo(a.LastActivity));
        return result;
    }

    public async Task<DiscussionRead?> GetAsync(string discussionId, CancellationToken ct = default)
    {
        var entity = await entities.GetBySlugAsync(Slug(discussionId), ct);
        return entity == null ? null : Map(entity);
    }

    public async Task<DiscussionRead> CreateAsync(string? title, string? agentId, string? ownerId, string type = "chat",
        string? qualityTier = null, string? provider = null, CancellationToken ct = default)
    {
        var discussionId = Guid.NewGuid().ToString("N")[..8];
        var data = new JsonObject
        {
            ["discussion_id"] = discussionId,
            ["app"] = "nova",
            ["owner_id"] = ownerId,
            ["type"] = type,
        };
        if (qualityTier != null) data["quality_tier"] = qualityTier;
        if (provider != null) data["provider"] = provider;
        var entity = await discussions.CreateAsync(title, agentId, data, ct);
        return Map(entity)!;
    }

    /// <summary>
    /// Reuses the delivery discussion minted for one durable automation attempt. A
    /// recovered lease must not create a second user-visible thread for the same run.
    /// </summary>
    public async Task<DiscussionRead> GetOrCreateAutomationDeliveryAsync(
        Guid attemptJobId, string? agentId, string? ownerId, CancellationToken ct = default)
    {
        var key = attemptJobId.ToString();
        var existing = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "discussion",
            DataEquals = new Dictionary<string, object?>
            {
                ["app"] = "nova",
                ["automation_attempt_job_id"] = key,
            },
            Limit = 2,
        }, ct);
        var mapped = existing.Select(Map).FirstOrDefault(discussion => discussion is not null);
        if (mapped is not null) return mapped;

        var discussionId = Guid.NewGuid().ToString("N")[..8];
        var data = new JsonObject
        {
            ["discussion_id"] = discussionId,
            ["app"] = "nova",
            ["owner_id"] = ownerId,
            ["type"] = "chat",
            ["automation_attempt_job_id"] = key,
        };
        var entity = await discussions.CreateAsync(null, agentId, data, ct);
        return Map(entity)!;
    }

    public async Task<(DiscussionRead Discussion, bool Created)> GetOrCreateIdempotentAsync(
        string idempotencyKey, string? agentId, string? ownerId, string type = "chat",
        string? qualityTier = null, string? provider = null, CancellationToken ct = default)
    {
        var existing = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "discussion",
            DataEquals = new Dictionary<string, object?>
            {
                ["app"] = "nova",
                ["creation_idempotency_key"] = idempotencyKey,
            },
            Limit = 2,
        }, ct);
        var mapped = existing.Select(Map).FirstOrDefault(discussion => discussion is not null);
        if (mapped is not null) return (mapped, false);

        var discussionId = Guid.NewGuid().ToString("N")[..8];
        var data = new JsonObject
        {
            ["discussion_id"] = discussionId,
            ["app"] = "nova",
            ["owner_id"] = ownerId,
            ["type"] = type,
            ["creation_idempotency_key"] = idempotencyKey,
        };
        if (qualityTier != null) data["quality_tier"] = qualityTier;
        if (provider != null) data["provider"] = provider;
        var entity = await discussions.CreateAsync(null, agentId, data, ct);
        return (Map(entity)!, true);
    }

    public async Task<DiscussionRead?> GetByCreationIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var existing = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "discussion",
            DataEquals = new Dictionary<string, object?>
            {
                ["app"] = "nova",
                ["creation_idempotency_key"] = idempotencyKey,
            },
            Limit = 1,
        }, ct);
        return existing.Select(Map).FirstOrDefault(discussion => discussion is not null);
    }

    public Task PatchAsync(Guid entityId, JsonObject patch, string? name = null, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync(entityId, () => entities.PatchAsync(entityId, patch, name, ct), ct);

    public Task TouchAsync(Guid entityId, CancellationToken ct = default)
        => PatchAsync(entityId, new JsonObject { ["last_activity"] = DateTimeOffset.UtcNow.ToString("O") }, ct: ct);

    /// <summary>Message post routed through the per-entity gate (the kernel post also
    /// rewrites the discussion entity's data blob for message_count/last_activity).</summary>
    public Task PostMessageAsync(Guid entityId, string role, string content, JsonObject metadata, string? userId = null, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync(entityId, () => discussions.PostAsync(entityId, role, content, metadata, userId, ct), ct);

    public Task<bool> PostMessageIdempotentAsync(Guid entityId, string role, string content,
        JsonObject metadata, string idempotencyKey, string? userId = null,
        CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync(entityId, async () =>
        {
            var existing = await discussions.GetMessagesAsync(entityId, ct: ct);
            if (existing.Any(message => message.Metadata["idempotency_key"] is JsonValue value
                && value.TryGetValue<string>(out var key)
                && string.Equals(key, idempotencyKey, StringComparison.Ordinal)))
                return false;
            metadata["idempotency_key"] = idempotencyKey;
            await discussions.PostAsync(entityId, role, content, metadata, userId, ct);
            return true;
        }, ct);

    /// <summary>
    /// The single write point for discussion status. Re-reads current state inside the
    /// per-entity gate, so the check and the write cannot interleave with other writers.
    /// Rules: closed states (<see cref="DiscussionStatus.Archiving"/>/<see cref="DiscussionStatus.Archived"/>)
    /// are terminal — the only transition out is archiving → archived (the finalizer's
    /// confirmation that the session is stopped). Returns the resulting status, or null
    /// when the transition was refused or the entity is gone.
    /// </summary>
    public Task<string?> TrySetStatusAsync(Guid entityId, string to, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<string?>(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            if (entity == null) return null;

            var current = Str(entity.Data, "status") ?? DiscussionStatus.Stopped;
            if (current == to) return current;

            var allowed = current switch
            {
                DiscussionStatus.Archived => false,
                DiscussionStatus.Archiving => to == DiscussionStatus.Archived,
                _ => true,
            };
            if (!allowed) return null;

            await entities.PatchAsync(entityId, new JsonObject { ["status"] = to }, null, ct);
            return to;
        }, ct);

    private static DiscussionRead? Map(LeafEntity entity)
    {
        var d = entity.Data;
        var id = Str(d, "discussion_id");
        if (id == null) return null;

        // Pre-cutover mirror writes lack data.title; fall back to the entity name,
        // normalizing its untitled placeholder back to null.
        var title = Str(d, "title") ?? (entity.Name == $"Discussion {id}" ? null : entity.Name);

        return new DiscussionRead(
            id, title, Str(d, "session_id"), Str(d, "status") ?? "stopped",
            ParseUtc(Str(d, "created_at")) ?? entity.CreatedAt.UtcDateTime,
            ParseUtc(Str(d, "last_activity")) ?? entity.UpdatedAt.UtcDateTime,
            Int(d, "message_count") ?? 0,
            ParseUtc(Str(d, "last_read_at")),
            Str(d, "owner_id"),
            entity.Id,
            Str(d, "agent"),
            Str(d, "type") ?? "chat",
            Str(d, "last_context_json"),
            Str(d, "injected_context"),
            Str(d, "quality_tier"),
            Str(d, "provider"),
            Bool(d, "confidential") ?? false);
    }

    public static object ToInfo(DiscussionRead d) => new
    {
        id = d.Id,
        entityId = d.EntityId,
        title = d.Title,
        sessionId = d.SessionId,
        status = d.Status,
        type = d.Type,
        createdAt = d.CreatedAt,
        lastActivity = d.LastActivity,
        messageCount = d.MessageCount,
        lastReadAt = d.LastReadAt,
        agentId = d.AgentId,
        qualityTier = d.QualityTier,
        provider = d.Provider,
        confidential = d.Confidential,
    };

    private static string? Str(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : null;
    }

    private static bool? Bool(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    private static int? Int(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var dbl)) return (int)dbl;
        return null;
    }

    private static DateTime? ParseUtc(string? value) =>
        value != null && DateTimeOffset.TryParse(value, out var t) ? t.UtcDateTime : null;
}
