using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
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

    /// <summary>Stops RedCompute recovers from on its own after a restart (mirrors SessionStopReasons.IsRestartRecoverable).</summary>
    public static bool IsRestartRecoverableStop(string? stopReason)
        => stopReason is "maintenance_restart" or "orphaned_on_restart";

    /// <summary>Stops RedCompute resumes on the next prompt (mirrors SessionStopReasons.IsAutoResumable). Delegation semantics only; the discussion indicator uses <see cref="IsRestartRecoverableStop"/>.</summary>
    public static bool IsAutoResumableStop(string? stopReason)
        => IsRestartRecoverableStop(stopReason) || stopReason is "idle_timeout" or "idle_ttl_expired";

    public static string? FromSessionStatus(
        string? sessionStatus,
        string discussionType,
        string? stopReason = null)
        => sessionStatus switch
        {
            "Active" => Thinking,
            "Idle" or "Starting" => Idle,
            "Stopped" when IsRestartRecoverableStop(stopReason) => Idle,
            "Stopped" or "Error" => Stopped,
            _ => null,
        };
}

/// <summary>
/// Provenance for a discussion title. Automatic session refinement is allowed
/// only for titles Nova produced as a first-message fallback or prior session title.
/// </summary>
public static class DiscussionTitleSource
{
    public const string Fallback = "fallback";
    public const string Session = "session";
    public const string Generated = "generated";
    public const string Manual = "manual";
    public const string System = "system";
    public const string LegacyLocked = "legacy-locked";
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
    bool Confidential = false,
    long ConversationRevision = 0,
    long ReadConversationRevision = 0,
    string? LastProcessedSessionAssistantUid = null,
    string? SetupBootstrapMessageUid = null,
    string? TitleSource = null,
    string? OwnerPlugin = null,
    string? OwnerApp = null,
    IReadOnlyList<WorktreeAttachment>? Worktrees = null,
    ReactionCursor? ReactionsNoted = null);

public sealed record ArchivedDiscussionPage(
    IReadOnlyList<DiscussionRead> Items,
    string? NextCursor,
    bool HasMore);

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
public sealed class DiscussionStore(
    IEntityStore entities, IDiscussions discussions, AgentActivitySignals? activity = null)
{
    internal const int QueryPageSize = 500;
    internal const int MaxListedDiscussions = 10_000;
    private static readonly string[] OpenStatuses =
        [DiscussionStatus.Idle, DiscussionStatus.Thinking, DiscussionStatus.Stopped];
    private static readonly string[] ClosedStatuses =
        [DiscussionStatus.Archived, DiscussionStatus.Archiving];

    public static string Slug(string discussionId)
    {
        var sanitized = new string(discussionId.ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return $"discussion-nova-{sanitized}";
    }

    public Task<List<DiscussionRead>> ListAsync(
        string? filterAgentId = null, CancellationToken ct = default)
        => ListCoreAsync(null, null, filterAgentId, ct);

    /// <summary>
    /// Lists every discussion in the open status vocabulary without loading closed
    /// rows. Separate exact-status queries can race with a status transition, so
    /// lifecycle-critical lookups must use <see cref="ListByTypeAsync"/> instead.
    /// </summary>
    public async Task<List<DiscussionRead>> ListOpenAsync(
        string? filterAgentId = null, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, DiscussionRead>();
        foreach (var status in OpenStatuses)
        {
            foreach (var discussion in await ListCoreAsync(status, null, filterAgentId, ct))
                result[discussion.EntityId] = discussion;
        }
        return Sort(result.Values);
    }

    /// <summary>Lists every discussion with one exact durable status.</summary>
    public Task<List<DiscussionRead>> ListByStatusAsync(
        string status, string? filterAgentId = null, CancellationToken ct = default)
        => ListCoreAsync(status, null, filterAgentId, ct);

    /// <summary>
    /// Returns one stable, bounded archive page. The SDK can keyset only by entity
    /// ID, so the exact closed-status sets are scanned in bounded 500-row chunks,
    /// then the public cursor is applied to the product ordering
    /// (last_activity descending, discussion id ascending). This keeps the open
    /// listing isolated from archive volume and never exposes an unbounded wire
    /// response.
    /// </summary>
    public async Task<ArchivedDiscussionPage> ListArchivedPageAsync(
        int limit,
        string? cursor = null,
        string? titleSearch = null,
        string? filterAgentId = null,
        bool includeLive = true,
        bool includeHeartbeats = false,
        bool includeArchiving = true,
        Func<DiscussionRead, bool>? canRead = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var rows = await ListByStatusAsync(DiscussionStatus.Archived, filterAgentId, ct);
        if (includeArchiving)
            rows.AddRange(await ListByStatusAsync(DiscussionStatus.Archiving, filterAgentId, ct));

        IEnumerable<DiscussionRead> filtered = rows
            .Where(discussion => canRead?.Invoke(discussion) ?? true)
            .Where(discussion =>
                discussion.Type == "chat"
                || includeLive && discussion.Type == "live"
                || includeHeartbeats && discussion.Type == HeartbeatService.DiscussionType);
        if (!string.IsNullOrWhiteSpace(titleSearch))
            filtered = filtered.Where(discussion => discussion.Title?.Contains(
                titleSearch.Trim(), StringComparison.OrdinalIgnoreCase) == true);

        var ordered = filtered
            .OrderByDescending(discussion => discussion.LastActivity)
            .ThenBy(discussion => discussion.Id, StringComparer.Ordinal)
            .ToList();
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            var boundary = DecodeArchiveCursor(cursor);
            ordered = ordered.Where(discussion =>
                discussion.LastActivity < boundary.LastActivity
                || discussion.LastActivity == boundary.LastActivity
                    && string.CompareOrdinal(discussion.Id, boundary.Id) > 0)
                .ToList();
        }

        var page = ordered.Take(limit + 1).ToList();
        var hasMore = page.Count > limit;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var nextCursor = hasMore && page.Count > 0
            ? EncodeArchiveCursor(page[^1].LastActivity, page[^1].Id)
            : null;
        return new ArchivedDiscussionPage(page, nextCursor, hasMore);
    }

    /// <summary>
    /// Lists every discussion of one durable type in one stable query sequence.
    /// Callers filter status in memory so a concurrent status transition cannot make
    /// a lifecycle-critical LIVE or Heartbeat discussion disappear between queries.
    /// </summary>
    public Task<List<DiscussionRead>> ListByTypeAsync(
        string type, string? filterAgentId = null, CancellationToken ct = default)
        => ListCoreAsync(null, type, filterAgentId, ct);

    /// <summary>
    /// Lists all open discussions plus closed discussions active since a cutoff.
    /// The SDK has no JSON range filter, so closed statuses are read newest entity
    /// update first and stop once entity updates predate the activity cutoff.
    /// </summary>
    public async Task<List<DiscussionRead>> ListOpenAndRecentlyClosedAsync(
        DateTime cutoff, string? filterAgentId = null, CancellationToken ct = default)
    {
        var result = (await ListOpenAsync(filterAgentId, ct))
            .ToDictionary(discussion => discussion.EntityId);
        foreach (var status in ClosedStatuses)
        {
            foreach (var discussion in await ListRecentStatusAsync(
                status, cutoff, filterAgentId, ct))
                result[discussion.EntityId] = discussion;
        }
        return Sort(result.Values);
    }

    private async Task<List<DiscussionRead>> ListCoreAsync(
        string? status, string? type, string? filterAgentId, CancellationToken ct)
    {
        var filters = new Dictionary<string, object?> { ["app"] = "nova" };
        if (status is not null) filters["status"] = status;
        if (type is not null) filters["type"] = type;

        var items = new List<LeafEntity>();
        Guid? afterId = null;
        while (true)
        {
            var page = await entities.QueryAsync(new EntityQuery
            {
                TypeSlug = "discussion",
                DataEquals = filters,
                OrderById = true,
                AfterId = afterId,
                Limit = QueryPageSize,
            }, ct);

            if (page.Count == 0) break;
            if (items.Count + page.Count > MaxListedDiscussions)
                throw ListingBoundExceeded(status);

            items.AddRange(page);
            afterId = page[^1].Id;

            if (page.Count < QueryPageSize) break;
            if (items.Count == MaxListedDiscussions)
            {
                var overflow = await entities.QueryAsync(new EntityQuery
                {
                    TypeSlug = "discussion",
                    DataEquals = filters,
                    OrderById = true,
                    AfterId = afterId,
                    Limit = 1,
                }, ct);
                if (overflow.Count != 0) throw ListingBoundExceeded(status);
                break;
            }
        }

        var result = new List<DiscussionRead>();
        foreach (var item in items)
        {
            var d = Map(item);
            if (d == null) continue;
            if (filterAgentId != null && d.AgentId != null && d.AgentId != filterAgentId) continue;
            result.Add(d);
        }
        return Sort(result);
    }

    private async Task<List<DiscussionRead>> ListRecentStatusAsync(
        string status, DateTime cutoff, string? filterAgentId, CancellationToken ct)
    {
        var filters = new Dictionary<string, object?>
        {
            ["app"] = "nova",
            ["status"] = status,
        };
        var result = new List<DiscussionRead>();
        var offset = 0;
        while (true)
        {
            var page = await entities.QueryAsync(new EntityQuery
            {
                TypeSlug = "discussion",
                DataEquals = filters,
                Limit = QueryPageSize,
                Offset = offset,
            }, ct);

            if (page.Count == 0) break;
            if (offset + page.Count > MaxListedDiscussions)
                throw ListingBoundExceeded(status);

            foreach (var item in page)
            {
                var discussion = Map(item);
                if (discussion is null || discussion.LastActivity < cutoff) continue;
                if (filterAgentId is not null && discussion.AgentId is not null
                    && discussion.AgentId != filterAgentId) continue;
                result.Add(discussion);
            }

            offset += page.Count;
            if (page.Count < QueryPageSize || page[^1].UpdatedAt.UtcDateTime < cutoff)
                break;
            if (offset == MaxListedDiscussions)
            {
                var overflow = await entities.QueryAsync(new EntityQuery
                {
                    TypeSlug = "discussion",
                    DataEquals = filters,
                    Limit = 1,
                    Offset = offset,
                }, ct);
                if (overflow.Count != 0) throw ListingBoundExceeded(status);
                break;
            }
        }
        return Sort(result);
    }

    private static List<DiscussionRead> Sort(IEnumerable<DiscussionRead> discussions)
    {
        var result = discussions.ToList();
        result.Sort((a, b) => b.LastActivity.CompareTo(a.LastActivity));
        return result;
    }

    private static InvalidOperationException ListingBoundExceeded(string? status)
        => new($"Nova discussion listing exceeded the {MaxListedDiscussions}-entity safety bound"
            + (status is null ? "." : $" for status '{status}'."));

    public async Task<DiscussionRead?> GetAsync(string discussionId, CancellationToken ct = default)
    {
        var entity = await entities.GetBySlugAsync(Slug(discussionId), ct);
        return entity == null ? null : Map(entity);
    }

    public async Task<DiscussionRead?> GetAsync(Guid entityId, CancellationToken ct = default)
    {
        var entity = await entities.GetAsync(entityId, ct);
        return IsNovaDiscussion(entity) ? Map(entity!) : null;
    }

    public async Task<DiscussionRead?> GetBySessionIdAsync(
        string sessionId, CancellationToken ct = default)
    {
        var matches = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "discussion",
            DataEquals = new Dictionary<string, object?>
            {
                ["app"] = "nova",
                ["session_id"] = sessionId,
            },
            Limit = 2,
        }, ct);
        return Sort(matches.Where(IsNovaDiscussion).Select(Map)
            .OfType<DiscussionRead>()).FirstOrDefault();
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
            ["conversation_revision"] = 0,
            ["read_conversation_revision"] = 0,
            ["last_processed_session_assistant_uid"] = "",
        };
        if (qualityTier != null) data["quality_tier"] = qualityTier;
        if (provider != null) data["provider"] = provider;
        if (!string.IsNullOrWhiteSpace(title))
            data["title_source"] = DiscussionTitleSource.System;
        var entity = await discussions.CreateAsync(title, agentId, data, ct);
        return Map(entity)!;
    }

    /// <summary>
    /// Reuses the delivery discussion minted for one durable automation attempt. A
    /// recovered lease must not create a second user-visible thread for the same run.
    /// </summary>
    public async Task<DiscussionRead> GetOrCreateAutomationDeliveryAsync(
        Guid attemptJobId, string? agentId, string? ownerId, bool confidential = false,
        CancellationToken ct = default)
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
        if (mapped is not null)
        {
            if (confidential && !mapped.Confidential)
            {
                await PatchAsync(mapped.EntityId, new JsonObject
                {
                    ["confidential"] = true,
                    ["owner_id"] = ownerId,
                    ["owner_agent_id"] = agentId,
                }, ct: ct);
                return mapped with { Confidential = true, OwnerId = ownerId };
            }
            return mapped;
        }

        var discussionId = Guid.NewGuid().ToString("N")[..8];
        var data = new JsonObject
        {
            ["discussion_id"] = discussionId,
            ["app"] = "nova",
            ["owner_id"] = ownerId,
            ["owner_agent_id"] = agentId,
            ["confidential"] = confidential,
            ["type"] = "chat",
            ["automation_attempt_job_id"] = key,
            ["conversation_revision"] = 0,
            ["read_conversation_revision"] = 0,
            ["last_processed_session_assistant_uid"] = "",
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
            ["conversation_revision"] = 0,
            ["read_conversation_revision"] = 0,
            ["last_processed_session_assistant_uid"] = "",
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
        => DiscussionEntityGate.RunAsync(entityId, async () =>
        {
            await RequireNovaOwnedAsync(entityId, ct);
            await entities.PatchAsync(entityId, patch, name, ct);
        }, ct);

    /// <summary>
    /// Records that the discussion works in a worktree. Idempotent per path: attaching it again
    /// changes nothing and keeps the first <c>attachedAt</c>.
    /// </summary>
    public Task<(IReadOnlyList<WorktreeAttachment> Items, bool Added)?> AttachWorktreeAsync(
        Guid entityId, WorktreeAttachment attachment, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<(IReadOnlyList<WorktreeAttachment>, bool)?>(entityId, async () =>
        {
            var entity = await RequireNovaOwnedAsync(entityId, ct);
            if (entity is null || Map(entity) is null) return null;
            var items = WorktreeAttachment.Read(entity.Data) ?? [];
            if (items.Any(a => WorktreeAttachment.SamePath(a.Path, attachment.Path)))
                return (items, false);
            var updated = items.Append(attachment).ToList();
            await entities.PatchAsync(entityId, new JsonObject { [WorktreeAttachment.DataKey] = WorktreeAttachment.ToJson(updated) }, null, ct);
            return (updated, true);
        }, ct);

    /// <summary>Forgets an attachment by path. It never touches the worktree itself.</summary>
    public Task<(IReadOnlyList<WorktreeAttachment> Items, bool Removed)?> DetachWorktreeAsync(
        Guid entityId, string path, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<(IReadOnlyList<WorktreeAttachment>, bool)?>(entityId, async () =>
        {
            var entity = await RequireNovaOwnedAsync(entityId, ct);
            if (entity is null || Map(entity) is null) return null;
            var items = WorktreeAttachment.Read(entity.Data) ?? [];
            var kept = items.Where(a => !WorktreeAttachment.SamePath(a.Path, path)).ToList();
            if (kept.Count == items.Count) return (items, false);
            await entities.PatchAsync(entityId, new JsonObject { [WorktreeAttachment.DataKey] = WorktreeAttachment.ToJson(kept) }, null, ct);
            return (kept, true);
        }, ct);

    public Task TouchAsync(Guid entityId, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            if (entity is null || IsForeign(entity)) return;
            await entities.PatchAsync(entityId,
                new JsonObject { ["last_activity"] = DateTimeOffset.UtcNow.ToString("O") }, null, ct);
        }, ct);

    /// <summary>Applies an explicit user rename and returns the canonical entity view.</summary>
    public Task<DiscussionRead?> SetManualTitleAsync(
        Guid entityId, string? title, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<DiscussionRead?>(entityId, async () =>
        {
            var entity = await RequireNovaOwnedAsync(entityId, ct);
            if (entity is null || Map(entity) is null) return null;
            await entities.PatchAsync(entityId, new JsonObject
            {
                ["title"] = title,
                ["title_source"] = DiscussionTitleSource.Manual,
            }, title ?? $"Discussion {Str(entity.Data, "discussion_id")}", ct);
            var updated = await entities.GetAsync(entityId, ct);
            return updated is null ? null : Map(updated);
        }, ct);

    /// <summary>
    /// Applies an explicit AI-generated title. Generated titles are user-requested
    /// and therefore protected from later automatic session-title refinements.
    /// </summary>
    public Task<DiscussionRead?> SetGeneratedTitleAsync(
        Guid entityId, string title, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<DiscussionRead?>(entityId, async () =>
        {
            var entity = await RequireNovaOwnedAsync(entityId, ct);
            var current = entity is null ? null : Map(entity);
            if (entity is null || current is null || string.IsNullOrWhiteSpace(title)) return current;
            if (!string.Equals(current.Type, "chat", StringComparison.Ordinal)) return current;

            var normalized = title.Trim();
            if (current.TitleSource is DiscussionTitleSource.Generated
                && string.Equals(current.Title, normalized, StringComparison.Ordinal)) return current;
            await entities.PatchAsync(entityId, new JsonObject
            {
                ["title"] = normalized,
                ["title_source"] = DiscussionTitleSource.Generated,
            }, normalized, ct);
            var updated = await entities.GetAsync(entityId, ct);
            return updated is null ? null : Map(updated);
        }, ct);

    /// <summary>
    /// Applies the deterministic first-message title only while the discussion
    /// remains genuinely untitled. Retried or late requests cannot replace a
    /// title established by a user, system workflow, or session refinement.
    /// </summary>
    public Task<DiscussionRead?> TrySetFallbackTitleAsync(
        Guid entityId, string title, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<DiscussionRead?>(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            var current = entity is null ? null : Map(entity);
            if (entity is null || current is null) return null;
            if (DiscussionApplicationOwner.IsForeign(current)) return current;
            if (!string.IsNullOrWhiteSpace(current.Title)
                || current.TitleSource is not (null or DiscussionTitleSource.Fallback))
                return current;

            var normalized = BuildFallbackTitle(title);
            if (string.IsNullOrWhiteSpace(normalized)) return current;
            await entities.PatchAsync(entityId, new JsonObject
            {
                ["title"] = normalized,
                ["title_source"] = DiscussionTitleSource.Fallback,
            }, normalized, ct);
            var updated = await entities.GetAsync(entityId, ct);
            return updated is null ? null : Map(updated);
        }, ct);

    /// <summary>
    /// Recovers a semantic RedCompute title without weakening title ownership.
    /// Untagged legacy titles are classified lazily: only an exact match for the
    /// oldest accepted user-message fallback remains eligible; every other
    /// nonblank legacy title is locked before returning.
    /// </summary>
    public Task<DiscussionRead?> TryApplySessionTitleAsync(
        Guid entityId, string? title, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<DiscussionRead?>(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            var current = entity is null ? null : Map(entity);
            if (entity is null || current is null || string.IsNullOrWhiteSpace(title)) return current;
            if (!string.Equals(current.Type, "chat", StringComparison.Ordinal)) return current;
            if (DiscussionApplicationOwner.IsForeign(current)) return current;

            var source = current.TitleSource;
            var eligible = source is DiscussionTitleSource.Fallback or DiscussionTitleSource.Session;
            if (source is null)
            {
                if (string.IsNullOrWhiteSpace(current.Title))
                {
                    eligible = true;
                }
                else
                {
                    var firstAccepted = (await discussions.GetMessagesAsync(entityId, 1000, ct: ct))
                        .FirstOrDefault(message => string.Equals(
                            message.Metadata["source"]?.GetValue<string>(),
                            "user-message", StringComparison.Ordinal));
                    var legacyFallback = firstAccepted is null
                        ? null
                        : BuildFallbackTitle(ConversationExporter.StripInjectedTags(firstAccepted.Content));
                    eligible = !string.IsNullOrWhiteSpace(legacyFallback)
                        && string.Equals(current.Title, legacyFallback, StringComparison.Ordinal);
                    if (!eligible)
                    {
                        await entities.PatchAsync(entityId, new JsonObject
                        {
                            ["title_source"] = DiscussionTitleSource.LegacyLocked,
                        }, ct: ct);
                        var locked = await entities.GetAsync(entityId, ct);
                        return locked is null ? null : Map(locked);
                    }
                }
            }

            if (!eligible) return current;
            var normalized = title.Trim();
            if (source is DiscussionTitleSource.Session
                && string.Equals(current.Title, normalized, StringComparison.Ordinal)) return current;
            await entities.PatchAsync(entityId, new JsonObject
            {
                ["title"] = normalized,
                ["title_source"] = DiscussionTitleSource.Session,
            }, normalized, ct);
            var updated = await entities.GetAsync(entityId, ct);
            return updated is null ? null : Map(updated);
        }, ct);

    internal static string BuildFallbackTitle(string visibleText)
    {
        var title = visibleText.Trim();
        return title.Length > 60 ? title[..59] + "…" : title;
    }

    /// <summary>Message post routed through the per-entity gate (the kernel post also
    /// rewrites the discussion entity's data blob for message_count/last_activity).</summary>
    public Task PostMessageAsync(Guid entityId, string role, string content, JsonObject metadata, string? userId = null, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync(entityId, async () =>
        {
            await RequireNovaOwnedAsync(entityId, ct);
            await discussions.PostAsync(entityId, role, content, metadata, userId, ct);
        }, ct);

    public Task<bool> PostMessageIdempotentAsync(Guid entityId, string role, string content,
        JsonObject metadata, string idempotencyKey, string? userId = null,
        CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync(entityId, async () =>
        {
            await RequireNovaOwnedAsync(entityId, ct);
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
    /// Persists one proactive assistant message and advances the conversation
    /// revision in the same per-discussion critical section. Reused idempotency
    /// keys do neither, so retries cannot manufacture unread state.
    /// </summary>
    public Task<(bool Created, DiscussionRead? Discussion)> PostConversationMessageAsync(
        Guid entityId, string content, JsonObject metadata, string? idempotencyKey,
        string? userId = null, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync(entityId, async () =>
        {
            await RequireNovaOwnedAsync(entityId, ct);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existing = await discussions.GetMessagesAsync(entityId, ct: ct);
                if (existing.Any(message => message.Metadata["idempotency_key"] is JsonValue value
                    && value.TryGetValue<string>(out var key)
                    && string.Equals(key, idempotencyKey, StringComparison.Ordinal)))
                {
                    var reusedEntity = await entities.GetAsync(entityId, ct);
                    return (false, reusedEntity is null ? null : Map(reusedEntity));
                }
                metadata["idempotency_key"] = idempotencyKey;
            }

            await discussions.PostAsync(entityId, "assistant", content, metadata, userId, ct);
            var entity = await entities.GetAsync(entityId, ct);
            if (entity is null) return (true, null);
            var mapped = Map(entity);
            if (mapped is null) return (true, null);

            var revision = Long(entity.Data, "conversation_revision") ?? 0;
            var patch = new JsonObject
            {
                ["conversation_revision"] = revision + 1,
                ["last_processed_session_assistant_uid"] =
                    Str(entity.Data, "last_processed_session_assistant_uid") ?? "",
            };
            await entities.PatchAsync(entityId, patch, ct: ct);
            var updated = mapped with
            {
                ConversationRevision = revision + 1,
                LastProcessedSessionAssistantUid =
                    Str(entity.Data, "last_processed_session_assistant_uid") ?? "",
            };
            return (true, updated);
        }, ct);

    /// <summary>
    /// Baselines legacy discussions without notifying, then advances exactly
    /// once for each newly observed provider-neutral assistant turn uid.
    /// </summary>
    public Task<DiscussionRead?> ReconcileConversationAsync(
        Guid entityId, string? latestAssistantUid, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<DiscussionRead?>(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            if (entity is null) return null;
            var current = Map(entity);
            if (current is null) return null;
            if (DiscussionApplicationOwner.IsForeign(current)) return current;

            var normalizedUid = latestAssistantUid ?? "";
            if (current.LastProcessedSessionAssistantUid is null)
            {
                await entities.PatchAsync(entityId, new JsonObject
                {
                    ["conversation_revision"] = current.ConversationRevision,
                    ["read_conversation_revision"] = current.ConversationRevision,
                    ["last_processed_session_assistant_uid"] = normalizedUid,
                }, ct: ct);
                return current with
                {
                    ReadConversationRevision = current.ConversationRevision,
                    LastProcessedSessionAssistantUid = normalizedUid,
                };
            }

            if (normalizedUid.Length == 0
                || string.Equals(current.LastProcessedSessionAssistantUid, normalizedUid,
                    StringComparison.Ordinal))
                return current;

            var nextRevision = current.ConversationRevision + 1;
            await entities.PatchAsync(entityId, new JsonObject
            {
                ["conversation_revision"] = nextRevision,
                ["last_processed_session_assistant_uid"] = normalizedUid,
            }, ct: ct);
            return current with
            {
                ConversationRevision = nextRevision,
                LastProcessedSessionAssistantUid = normalizedUid,
            };
        }, ct);

    /// <summary>
    /// Acknowledges only the revision the client actually rendered. A newer
    /// revision arriving concurrently remains unread.
    /// </summary>
    public Task<DiscussionRead?> MarkConversationReadAsync(
        Guid entityId, long throughRevision, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<DiscussionRead?>(entityId, async () =>
        {
            var entity = await entities.GetAsync(entityId, ct);
            if (entity is null) return null;
            var current = Map(entity);
            if (current is null) return null;
            if (DiscussionApplicationOwner.IsForeign(current)) return current;

            var acknowledged = ConversationRevision.Acknowledge(
                current.ConversationRevision, current.ReadConversationRevision, throughRevision);
            var now = DateTime.UtcNow;
            await entities.PatchAsync(entityId, new JsonObject
            {
                ["read_conversation_revision"] = acknowledged,
                ["last_read_at"] = new DateTimeOffset(now).ToString("O"),
            }, ct: ct);
            return current with
            {
                ReadConversationRevision = acknowledged,
                LastReadAt = now,
            };
        }, ct);

    public Task<DiscussionRead?> ResetConversationReadAsync(
        Guid entityId, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<DiscussionRead?>(entityId, async () =>
        {
            var entity = await RequireNovaOwnedAsync(entityId, ct);
            if (entity is null) return null;
            var current = Map(entity);
            if (current is null) return null;
            await entities.PatchAsync(entityId, new JsonObject
            {
                ["conversation_revision"] = 0,
                ["read_conversation_revision"] = 0,
                ["last_processed_session_assistant_uid"] = "",
            }, ct: ct);
            return current with
            {
                ConversationRevision = 0,
                ReadConversationRevision = 0,
                LastProcessedSessionAssistantUid = "",
            };
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
            if (IsForeign(entity))
            {
                // Session-derived status belongs to the owner. Closing would also
                // stop the owner's session, so it is a refused lifecycle change.
                if (DiscussionStatus.IsClosed(to))
                    throw new DiscussionOwnedByApplicationException(Map(entity)!);
                return current;
            }

            var allowed = current switch
            {
                DiscussionStatus.Archived => false,
                DiscussionStatus.Archiving => to == DiscussionStatus.Archived,
                _ => true,
            };
            if (!allowed) return null;

            await entities.PatchAsync(entityId, new JsonObject { ["status"] = to }, null, ct);
            NotifyStatusWritten(entity, to);
            return to;
        }, ct);

    /// <summary>Observe-only: activity signals can never affect a status write.</summary>
    private void NotifyStatusWritten(LeafEntity entity, string status)
    {
        if (activity is null) return;
        try
        {
            if (Map(entity) is { } written)
                activity.StatusWritten(written with { Status = status });
        }
        catch
        {
            // Best effort by design.
        }
    }

    /// <summary>
    /// The sole exception to closed-state terminality: an explicit user reopen may
    /// move archived to stopped. Archiving remains terminal until its finalizer has
    /// confirmed the provider stopped. Ordinary status writers never call this.
    /// Already-open discussions are returned unchanged for endpoint idempotency.
    /// </summary>
    public Task<DiscussionRead?> TryReopenArchivedAsync(
        Guid entityId, CancellationToken ct = default)
        => DiscussionEntityGate.RunAsync<DiscussionRead?>(entityId, async () =>
        {
            var entity = await RequireNovaOwnedAsync(entityId, ct);
            if (entity is null) return null;
            var current = Map(entity);
            if (current is null || current.Status == DiscussionStatus.Archiving) return null;
            if (current.Status != DiscussionStatus.Archived) return current;
            if (current.Type != "chat" || string.IsNullOrWhiteSpace(current.SessionId)) return null;

            await entities.PatchAsync(entityId,
                new JsonObject { ["status"] = DiscussionStatus.Stopped }, null, ct);
            return current with { Status = DiscussionStatus.Stopped };
        }, ct);

    internal static string EncodeArchiveCursor(DateTime lastActivity, string id)
    {
        var json = JsonSerializer.Serialize(new ArchiveCursor(lastActivity, id));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static (DateTime LastActivity, string Id) DecodeArchiveCursor(string cursor)
    {
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var decoded = JsonSerializer.Deserialize<ArchiveCursor>(
                Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            if (decoded is null || string.IsNullOrWhiteSpace(decoded.Id))
                throw new FormatException();
            return (decoded.LastActivity, decoded.Id);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("The archive cursor is invalid.", nameof(cursor), ex);
        }
    }

    private sealed record ArchiveCursor(DateTime LastActivity, string Id);

    private static bool IsForeign(LeafEntity entity)
        => DiscussionApplicationOwner.IsForeign(Str(entity.Data, "owner_plugin"));

    /// <summary>
    /// Reads the entity for a lifecycle write and refuses one owned by another plugin.
    /// Session-derived housekeeping skips such discussions instead of calling this.
    /// </summary>
    private async Task<LeafEntity?> RequireNovaOwnedAsync(Guid entityId, CancellationToken ct)
    {
        var entity = await entities.GetAsync(entityId, ct);
        if (entity is not null && IsForeign(entity) && Map(entity) is { } current)
            throw new DiscussionOwnedByApplicationException(current);
        return entity;
    }

    private static bool IsNovaDiscussion(LeafEntity? entity)
        => entity is { TypeSlug: "discussion" }
            && Str(entity.Data, "app") == "nova";

    internal static DiscussionRead? Map(LeafEntity entity)
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
            Bool(d, "confidential") ?? false,
            Long(d, "conversation_revision") ?? 0,
            Long(d, "read_conversation_revision") ?? 0,
            d.ContainsKey("last_processed_session_assistant_uid")
                ? Str(d, "last_processed_session_assistant_uid") ?? ""
                : null,
            Str(d, "setup_bootstrap_message_uid"),
            Str(d, "title_source"),
            Str(d, "owner_plugin"),
            Str(d, "source_app"),
            WorktreeAttachment.Read(d),
            ReactionCursor.Read(d));
    }

    public static object ToInfo(DiscussionRead d) => new
    {
        id = d.Id,
        entityId = d.EntityId,
        title = d.Title,
        sessionId = d.SessionId,
        status = d.Status,
        titleSource = d.TitleSource,
        type = d.Type,
        createdAt = d.CreatedAt,
        lastActivity = d.LastActivity,
        messageCount = d.MessageCount,
        lastReadAt = d.LastReadAt,
        conversationRevision = d.ConversationRevision,
        readConversationRevision = d.ReadConversationRevision,
        agentId = d.AgentId,
        qualityTier = d.QualityTier,
        provider = d.Provider,
        confidential = d.Confidential,
        setupBootstrapMessageUid = d.SetupBootstrapMessageUid,
        worktrees = d.Worktrees ?? [],
        ownedBy = DiscussionApplicationOwner.IsForeign(d)
            ? new { app = d.OwnerApp }
            : null,
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

    private static long? Long(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var dbl)) return (long)dbl;
        return null;
    }

    private static DateTime? ParseUtc(string? value) =>
        value != null && DateTimeOffset.TryParse(value, out var t) ? t.UtcDateTime : null;
}
