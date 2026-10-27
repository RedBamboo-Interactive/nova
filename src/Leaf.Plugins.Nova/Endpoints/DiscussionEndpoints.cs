using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Leaf.Sdk;

namespace Leaf.Plugins.Nova.Endpoints;

public class CreateDiscussionRequest
{
    public string? AgentId { get; set; }
    public string? Type { get; set; }
    public string? QualityTier { get; set; }
    public string? Provider { get; set; }
    public bool DeferSession { get; set; }
    public string? IdempotencyKey { get; set; }
}

public class DiscussionTitleRequest
{
    public string? Title { get; set; }
}

public class DiscussionConfidentialRequest
{
    public bool Confidential { get; set; }
}

public class AttachWorktreeRequest
{
    public string? Repository { get; set; }
    public string? Branch { get; set; }
    public string? Base { get; set; }
}

public class DetachWorktreeRequest
{
    public string? Path { get; set; }
}

public class DiscussionReadRequest
{
    public long? ConversationRevision { get; set; }
}

public class DiscussionEventRequest
{
    public string Content { get; set; } = "";
    public string? Type { get; set; }
    public string? Source { get; set; }
    public string? SenderAgentId { get; set; }
    public string? ReplyToDiscussionId { get; set; }
    public JsonElement? Metadata { get; set; }
    /// <summary>Optional caller key: a retry under it neither stores nor delivers the event twice.</summary>
    public string? IdempotencyKey { get; set; }
}

public class NovaMessageRequest
{
    public string Content { get; set; } = "";
    public string? Title { get; set; }
    public string? AudioUrl { get; set; }
    public string? SenderAgentId { get; set; }
    public string? IdempotencyKey { get; set; }
}

public class DiscussionMessageRequest
{
    public string Content { get; set; } = "";
    public ImageAttachmentDto[]? Images { get; set; }
    public InputPartDto[]? Input { get; set; }
    public string? InputMethod { get; set; }
    public string? Delivery { get; set; }
    public string? DisplayContent { get; set; }
    public string? MessageUid { get; set; }
}

public class ReactionRequest
{
    public string Emoji { get; set; } = "";
    public string? MessageKey { get; set; }
    public string? AgentId { get; set; }
    public string? AgentName { get; set; }
}

internal sealed record HistoryOverlayDto(
    string Id,
    string? MessageUid,
    string Role,
    object[] Parts,
    string Timestamp,
    string? SenderAgentId,
    string Source);

internal sealed record OverlayPageRead(
    IReadOnlyList<DiscussionMessage> Messages,
    bool HasEarlier,
    bool HasLater,
    long? OldestId,
    long? NewestId);

internal sealed class HistoryOverlayPageTooLargeException : Exception
{
    public HistoryOverlayPageTooLargeException()
        : base("One canonical history interval contains more than 10,000 Nova overlay records")
    {
    }
}

public static class DiscussionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string? UserId(HttpContext ctx) => ctx.User.FindFirstValue("sub");

    private static IResult Forbidden() => Results.Json(new { error = "Forbidden" }, statusCode: 403);
    private static IResult NotFound() => Results.NotFound(new { error = "Discussion not found" });
    private static IResult AccessDenied(DiscussionRead discussion)
        => discussion.Confidential ? NotFound() : Forbidden();
    private static IResult Closed()
        => Results.Json(new { error = "closed", message = "Archived discussions are read-only" }, statusCode: 409);

    private static readonly TimeSpan EventSessionWait = TimeSpan.FromSeconds(20);

    /// <summary>
    /// <c>success</c> means the event will reach the session exactly once: delivered now (200) or
    /// durably queued until the session exists (202). A system event is recorded for display only.
    /// Anything that did not and will not reach the session is a non-success the caller can act on.
    /// </summary>
    internal static IResult EventResponse(EventInjectionResult result) => result.Delivery switch
    {
        EventDelivery.Delivered or EventDelivery.Reused =>
            Results.Ok(new { success = true, delivery = "delivered", messageUid = result.MessageUid }),
        EventDelivery.Recorded =>
            Results.Ok(new { success = true, delivery = "recorded", messageUid = result.MessageUid }),
        EventDelivery.Queued => Results.Json(new
        {
            success = true, delivery = "queued", messageUid = result.MessageUid,
            message = "The discussion has no AI session yet; the event is stored and will be delivered, once, when it starts.",
        }, statusCode: StatusCodes.Status202Accepted),
        EventDelivery.Failed => Results.Json(new
        {
            success = false, delivery = "failed", messageUid = result.MessageUid, retryable = true,
            error = result.ErrorCode ?? "event_delivery_failed",
            message = result.ErrorMessage ?? "The event was stored but the AI session did not accept it. Retry with an idempotencyKey to avoid a duplicate.",
        }, statusCode: StatusCodes.Status502BadGateway),
        EventDelivery.Denied => Closed(),
        _ => Results.Json(new
        {
            success = false, delivery = "undeliverable", messageUid = result.MessageUid, retryable = false,
            error = result.ErrorCode ?? "event_undeliverable",
            message = result.ErrorMessage ?? "The event cannot be delivered to this discussion's AI session.",
        }, statusCode: StatusCodes.Status422UnprocessableEntity),
    };

    internal static async Task<IResult> PostEventAsync(string id, DiscussionEventRequest request, HttpContext ctx,
        DiscussionStore store, AgentDirectory agents, EventInjector injector, MessagePipeline pipeline)
    {
        // "live" resolves to the current LIVE discussion — the heartbeat posts
        // its note-events through this form since the LIVE id rotates daily.
        var discussion = await LiveDiscussionAlias.ResolveAsync(id, agents.NovaAgentId,
            i => store.GetAsync(i, ctx.RequestAborted),
            () => store.ListByTypeAsync("live", ct: ctx.RequestAborted));
        if (discussion is null) return NotFound();
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
        if (DiscussionApplicationOwner.IsForeign(discussion)) return DiscussionApplicationOwner.Refusal(discussion);
        if (DiscussionMutationPolicy.RejectSend(discussion)) return Closed();

        if (string.IsNullOrWhiteSpace(request.Content))
            return Results.BadRequest(new { error = "Content is required" });
        if (request.IdempotencyKey is { } requestKey && (string.IsNullOrWhiteSpace(requestKey) || requestKey.Length > 200))
            return Results.BadRequest(new { error = "invalid_idempotency_key", message = "idempotencyKey must be 1-200 characters" });

        var result = await injector.InjectEventAsync(discussion, request.Content, request.Type, request.Source,
            request.SenderAgentId, request.ReplyToDiscussionId, request.Metadata, UserId(ctx),
            request.IdempotencyKey, ctx.RequestAborted);

        // A discussion created a moment ago is still getting its session. The event is queued
        // and the creation delivers it; wait a bounded while so the answer can say delivered.
        if (result.Delivery == EventDelivery.Queued && result.MessageUid is { } queuedUid
            && pipeline.PendingSessionCreation(discussion.Id) is { } creation)
        {
            try { await creation.WaitAsync(EventSessionWait, ctx.RequestAborted); }
            catch (TimeoutException) { }
            await injector.DeliverPendingAsync(discussion.EntityId, ctx.RequestAborted);
            result = await injector.QueuedStateAsync(discussion.EntityId, queuedUid, ctx.RequestAborted);
        }

        return EventResponse(result);
    }

    internal static async Task<IResult> AttachWorktreeAsync(DiscussionRead discussion, AttachWorktreeRequest request,
        HttpContext ctx, DiscussionStore store, CodeWorktreeClient code, IEntityStore entities,
        IPluginEvents? events = null)
    {
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
        if (DiscussionApplicationOwner.IsForeign(discussion)) return DiscussionApplicationOwner.Refusal(discussion);
        if (DiscussionMutationPolicy.RejectSend(discussion)) return Closed();
        if (string.IsNullOrWhiteSpace(request.Repository) || string.IsNullOrWhiteSpace(request.Branch))
            return Results.BadRequest(new { error = "invalid_request", message = "repository and branch are required" });

        var (repository, repositoryError) = await DelegateEndpoints.ResolveRepositoryAsync(
            entities, request.Repository, ctx.RequestAborted);
        if (repositoryError is not null) return repositoryError;

        var outcome = await code.ResolveAsync(ctx, repository!, request.Branch, request.Base, ctx.RequestAborted);
        if (outcome.Failure is { } failure)
            return Results.Json(new { error = failure.Error, message = failure.Message }, statusCode: failure.StatusCode);
        var worktree = outcome.Worktree!;

        var attached = await store.AttachWorktreeAsync(discussion.EntityId,
            new WorktreeAttachment(worktree.RepositoryId, worktree.RepositoryName, worktree.Branch,
                worktree.Path.Replace('\\', '/'), DateTimeOffset.UtcNow), ctx.RequestAborted);
        if (attached is not var (items, added)) return NotFound();
        if (added) await PublishChangedAsync(events, discussion, ctx.RequestAborted);
        return Results.Ok(new
        {
            attached = added,
            created = worktree.Created,
            isCheckout = worktree.IsCheckout,
            items,
        });
    }

    internal static async Task<IResult> DetachWorktreeAsync(DiscussionRead discussion, DetachWorktreeRequest request,
        HttpContext ctx, DiscussionStore store, IPluginEvents? events = null)
    {
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
        if (DiscussionApplicationOwner.IsForeign(discussion)) return DiscussionApplicationOwner.Refusal(discussion);
        if (DiscussionMutationPolicy.RejectSend(discussion)) return Closed();
        if (string.IsNullOrWhiteSpace(request.Path))
            return Results.BadRequest(new { error = "invalid_request", message = "path is required" });

        var detached = await store.DetachWorktreeAsync(discussion.EntityId, request.Path, ctx.RequestAborted);
        if (detached is not var (items, removed)) return NotFound();
        if (removed) await PublishChangedAsync(events, discussion, ctx.RequestAborted);
        return Results.Ok(new { detached = removed, items });
    }

    // The header chips read the discussion record; this is the event that makes clients re-read it.
    private static async Task PublishChangedAsync(IPluginEvents? events, DiscussionRead discussion, CancellationToken ct)
    {
        if (events is null) return;
        try
        {
            await events.PublishAsync("discussion.changed", new JsonObject
            {
                ["discussionId"] = discussion.Id,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["confidential"] = discussion.Confidential,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }

    internal static async Task<IResult> WorktreeAttachmentsAsync(string? repositoryId, HttpContext ctx, DiscussionStore store)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
            return Results.BadRequest(new { error = "invalid_request", message = "repositoryId is required" });
        var matches = (await store.ListOpenAsync(ct: ctx.RequestAborted))
            .Where(d => DiscussionAccessPolicy.CanRead(d, ctx))
            .SelectMany(d => (d.Worktrees ?? [])
                .Where(a => string.Equals(a.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase))
                .Select(a => new
                {
                    discussionId = d.Id,
                    title = d.Title,
                    status = d.Status,
                    agentId = d.AgentId,
                    repositoryId = a.RepositoryId,
                    repositoryName = a.RepositoryName,
                    branch = a.Branch,
                    path = a.Path,
                    attachedAt = a.AttachedAt,
                }))
            .ToList();
        return Results.Ok(new { repositoryId, items = matches });
    }

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/maintenance", async (RedComputeClient redCompute, HttpContext context) =>
        {
            var result = await redCompute.GetMaintenanceStatusAsync(context.RequestAborted);
            return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);
        }).WithDescription("Authenticated update pause and aggregate active-turn progress; no private session details.");

        group.MapGet("/discussions/delegations", async (HttpContext ctx, DiscussionStore store, DelegationActivity activity) =>
        {
            var changed = ctx.Request.Query["sessionId"].Select(id => id ?? "").ToHashSet(StringComparer.Ordinal);
            if (changed.Count > 200 || changed.Any(id => !DelegationActivity.ValidSessionId(id)))
                return Results.BadRequest(new { error = "invalid_session_id" });
            return Results.Ok(await activity.ReadAsync(await store.ListOpenAsync(ct: ctx.RequestAborted), ctx,
                ctx.RequestAborted, changed, ctx.Request.Query["fresh"] == "true"));
        })
            .WithName("NovaDiscussionDelegations")
            .WithSummary("Ongoing originating-discussion delegations")
            .WithDescription("Authorized read-only activity for distinct durably linked delegated sessions, using canonical session and input-queue state. Unavailable observations preserve unresolved work without claiming completion; parent discussion status is unchanged.")
            .Produces<IReadOnlyDictionary<string, DiscussionDelegationActivity>>();

        group.MapGet("/discussions", async (HttpContext ctx, DiscussionStore store) =>
        {
            var status = ctx.Request.Query["status"].FirstOrDefault();
            var search = ctx.Request.Query["search"].FirstOrDefault();
            var agentFilter = ctx.Request.Query["agent"].FirstOrDefault();

            // Opt-in page envelope for archive mode. Callers that omit limit keep
            // the historical array response, while the UI never downloads the
            // entire archive. Authorization is applied before the cursor boundary.
            if (status == DiscussionStatus.Archived
                && ctx.Request.Query.TryGetValue("limit", out var archivedLimitValue))
            {
                if (!int.TryParse(archivedLimitValue.FirstOrDefault(), out var archivedLimit))
                    return Results.BadRequest(new { error = "invalid_limit" });
                var includeLive = ctx.Request.Query["includeLive"].FirstOrDefault() != "false";
                var includeHeartbeats = ctx.Request.Query["includeHeartbeats"].FirstOrDefault() == "true";
                var includeArchiving = ctx.Request.Query["includeArchiving"].FirstOrDefault() != "false";
                try
                {
                    var page = await store.ListArchivedPageAsync(
                        archivedLimit,
                        ctx.Request.Query["cursor"].FirstOrDefault(),
                        search,
                        agentFilter,
                        includeLive,
                        includeHeartbeats,
                        includeArchiving,
                        discussion => DiscussionAccessPolicy.CanRead(discussion, ctx),
                        ctx.RequestAborted);
                    return Results.Ok(new
                    {
                        items = page.Items.Select(DiscussionStore.ToInfo),
                        nextCursor = page.NextCursor,
                        hasMore = page.HasMore,
                    });
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { error = "invalid_cursor" });
                }
            }

            IEnumerable<DiscussionRead> filtered = !string.IsNullOrEmpty(status)
                ? await store.ListByStatusAsync(status, agentFilter, ctx.RequestAborted)
                : await store.ListOpenAsync(agentFilter, ctx.RequestAborted);

            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(d => d.Title != null && d.Title.Contains(search, StringComparison.OrdinalIgnoreCase));

            filtered = filtered.Where(d => DiscussionAccessPolicy.CanRead(d, ctx));

            return Results.Ok(filtered.Select(DiscussionStore.ToInfo));
        });

        group.MapGet("/discussions/pending", async (HttpContext ctx, DiscussionStore store) =>
        {
            var discussions = (await store.ListOpenAsync())
                // Heartbeat discussions never count as pending: no unread pill, no
                // badge — the heartbeat is a place you visit, not one that calls you.
                .Where(d => d.Type != HeartbeatService.DiscussionType)
                .Where(d => DiscussionAccessPolicy.CanRead(d, ctx))
                .ToList();

            var count = discussions.Count(d =>
                d.Status == DiscussionStatus.Idle
                && d.ConversationRevision > d.ReadConversationRevision);

            return Results.Ok(new { count });
        });

        group.MapPost("/discussions/sync", async (HttpContext ctx, DiscussionStore store,
            RedComputeClient redCompute, ConversationUnread conversationUnread) =>
        {
            var discussions = (await store.ListOpenAsync())
                .Where(d => d.SessionId != null)
                .Where(d => DiscussionAccessPolicy.CanRead(d, ctx))
                .ToList();

            if (discussions.Count == 0)
                return Results.Ok(new { synced = 0 });

            var sessions = await redCompute.GetSessionsAsync(50);
            if (sessions == null)
                return Results.Ok(discussions.Select(DiscussionStore.ToInfo)); // RedCompute unreachable — leave as-is

            var statuses = sessions.ToDictionary(s => s.Id);
            for (var i = 0; i < discussions.Count; i++)
            {
                var d = discussions[i];
                if (d.SessionId == null) continue;
                statuses.TryGetValue(d.SessionId, out var rcSession);

                // The session list is a recency window, not the full set — a quiet
                // session falling out of it is no evidence it stopped. Probe it
                // directly before declaring a live discussion dead; a null probe
                // (RedCompute unreachable) leaves the status unchanged.
                if (rcSession == null && d.Status is "idle" or "thinking")
                {
                    var state = await redCompute.GetSessionStateAsync(d.SessionId);
                    if (state is not null)
                        rcSession = new RedComputeClient.SessionListEntry(
                            d.SessionId, state.Status, 0, state.StopReason);
                }

                // Reconcile activity as well as liveness. Previously an Active
                // RedCompute session remained `idle` in the discussion entity,
                // so a refresh could turn a visibly running thread green.
                var newStatus = DiscussionStatus.FromSessionStatus(
                    rcSession?.Status, d.Type, rcSession?.StopReason);

                if (newStatus != null && newStatus != d.Status)
                {
                    // State-machine write: refused (null) if the discussion closed
                    // (archiving/archived) while this sweep was probing sessions.
                    var applied = await store.TrySetStatusAsync(d.EntityId, newStatus);
                    if (applied != null)
                        discussions[i] = d with { Status = applied };
                }

                if (newStatus == DiscussionStatus.Idle)
                    discussions[i] = await conversationUnread.ReconcileSettledAsync(discussions[i]);
            }

            return Results.Ok(discussions.Select(DiscussionStore.ToInfo));
        });

        group.MapPost("/discussions", async (HttpContext ctx, DiscussionStore store, AgentDirectory agents, MessagePipeline pipeline, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, LiveEvents live) =>
        {
            CreateDiscussionRequest? createReq = null;
            try { createReq = await ctx.Request.ReadFromJsonAsync<CreateDiscussionRequest>(JsonOptions); }
            catch { /* body is optional */ }

            var agentId = createReq?.AgentId ?? agents.NovaAgentId;
            var type = createReq?.Type ?? "chat";

            if (type is "live" or HeartbeatService.DiscussionType)
            {
                var existing = (await store.ListByTypeAsync(type, agentId, ctx.RequestAborted))
                    .Any(d => !DiscussionStatus.IsClosed(d.Status));
                if (existing)
                    return Results.Json(new { error = $"A {type.ToUpperInvariant()} discussion already exists for this agent" }, statusCode: 409);
            }

            DiscussionRead discussion;
            var created = true;
            if (!string.IsNullOrWhiteSpace(createReq?.IdempotencyKey))
            {
                (discussion, created) = await store.GetOrCreateIdempotentAsync(
                    createReq.IdempotencyKey, agentId, UserId(ctx), type,
                    createReq.QualityTier, createReq.Provider, ctx.RequestAborted);
            }
            else
            {
                discussion = await store.CreateAsync(null, agentId, UserId(ctx), type,
                    createReq?.QualityTier, createReq?.Provider, ctx.RequestAborted);
            }

            if (!created)
                return Results.Ok(DiscussionStore.ToInfo(discussion));

            await events.PublishAsync("discussion.created", new JsonObject
            {
                ["discussionId"] = discussion.Id,
                ["agentId"] = discussion.AgentId,
                ["status"] = discussion.Status,
                ["type"] = discussion.Type,
            });

            if (discussion.Type != "live")
                _ = live.PostAsync("discussion", $"New discussion: \"{discussion.Title ?? "untitled"}\"");

            // Delivery discussions populated through nova-message do not need an
            // empty provider session. Create it on the first user reply instead,
            // when MessagePipeline can also replay the persisted assistant post.
            if (createReq?.DeferSession != true)
                pipeline.BeginSessionCreation(discussion);

            return Results.Ok(DiscussionStore.ToInfo(discussion));
        });

        group.MapGet("/discussions/live", async (HttpContext ctx, DiscussionStore store) =>
        {
            var agentFilter = ctx.Request.Query["agent"].FirstOrDefault();
            var live = (await store.ListByTypeAsync("live", agentFilter, ctx.RequestAborted))
                .Where(d => !DiscussionStatus.IsClosed(d.Status)
                    && DiscussionAccessPolicy.CanRead(d, ctx))
                .Select(DiscussionStore.ToInfo);
            return Results.Ok(live);
        });

        group.MapGet("/discussions/search", async (
            HttpContext ctx,
            DiscussionStore store,
            ISearch search,
            ISearchAccessScopeProvider searchAccess) =>
        {
            var q = ctx.Request.Query["q"].FirstOrDefault();
            var requestedStatus = ctx.Request.Query["status"].FirstOrDefault();
            var agentFilter = ctx.Request.Query["agent"].FirstOrDefault();
            var includeLive = ctx.Request.Query.TryGetValue("includeLive", out var includeLiveValue)
                ? includeLiveValue.FirstOrDefault() != "false"
                : (bool?)null;
            var includeHeartbeats = ctx.Request.Query.TryGetValue("includeHeartbeats", out var includeHeartbeatsValue)
                ? includeHeartbeatsValue.FirstOrDefault() == "true"
                : (bool?)null;
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            var limit = 20;
            if (ctx.Request.Query.TryGetValue("limit", out var lv) && int.TryParse(lv, out var parsed))
                limit = Math.Clamp(parsed, 1, 100);

            // Thin adapter over the kernel conversation search. Two streams cover a
            // discussion's content: nova-messages (events, proactive posts, image
            // messages — keyed by the discussion entity) and session-messages (the
            // actual transcript, keyed by the ai-session entity and mapped back to
            // the discussion through its session_id).
            var accessScope = await searchAccess.ResolveAsync(ctx.User, ctx.RequestAborted);
            var result = await search.SearchConversationsAsync(new ConversationSearchQuery
            {
                Query = q,
                Streams = ["nova-messages", "session-messages"],
                Limit = 100,
                SnippetsPerConversation = 3,
                AccessScope = accessScope,
            }, ctx.RequestAborted);

            var byEntityId = new Dictionary<Guid, DiscussionRead?>();
            var bySessionId = new Dictionary<string, DiscussionRead?>();

            // Kernel groups arrive best-ranked first; a discussion hit in both streams
            // merges into one result. Non-Nova conversations (other apps' sessions or
            // discussions) simply don't resolve and are skipped.
            var merged = new Dictionary<string, (DiscussionRead Discussion, int MatchCount, List<ConversationSearchHit> Hits)>();
            foreach (var g in result.Groups)
            {
                DiscussionRead? disc = null;
                if (g.EntityId is { } eid)
                {
                    if (!byEntityId.TryGetValue(eid, out disc))
                    {
                        disc = await store.GetAsync(eid, ctx.RequestAborted);
                        byEntityId[eid] = disc;
                    }
                }
                if (disc is null && g.Entity is { TypeSlug: "ai-session" }
                    && g.Entity.Data["session_id"]?.GetValue<string>() is { } sid)
                {
                    if (!bySessionId.TryGetValue(sid, out disc))
                    {
                        disc = await store.GetBySessionIdAsync(sid, ctx.RequestAborted);
                        bySessionId[sid] = disc;
                    }
                }
                if (disc == null || !DiscussionAccessPolicy.CanRead(disc, ctx)) continue;
                if (requestedStatus == DiscussionStatus.Archived
                    && !DiscussionStatus.IsClosed(disc.Status)) continue;
                if (agentFilter is not null && disc.AgentId is not null
                    && disc.AgentId != agentFilter) continue;
                if (!MatchesSearchTypeScope(disc, includeLive, includeHeartbeats)) continue;

                if (merged.TryGetValue(disc.Id, out var existing))
                    merged[disc.Id] = (disc, existing.MatchCount + g.HitCount, [.. existing.Hits, .. g.Hits]);
                else
                    merged[disc.Id] = (disc, g.HitCount, g.Hits.ToList());
            }

            var displayTitle = DiscussionDisplayTitle.Resolver(
                merged.Values.Select(m => DiscussionDisplayTitle.Of(m.Discussion)));
            var results = merged.Values
                .Take(limit)
                .Select(m => new
                {
                    discussionId = m.Discussion.Id,
                    title = m.Discussion.Title,
                    // Agent-facing name: untitled LIVEs read "Live · <UTC start date>".
                    // The browser derives its own in the viewer's locale.
                    displayTitle = displayTitle(DiscussionDisplayTitle.Of(m.Discussion)),
                    status = m.Discussion.Status,
                    lastActivity = m.Discussion.LastActivity,
                    matchCount = m.MatchCount,
                    snippets = m.Hits
                        .OrderByDescending(h => h.CreatedAt)
                        .Take(3)
                        .Select(h => new
                        {
                            role = h.Role ?? "",
                            timestamp = h.CreatedAt.UtcDateTime,
                            snippet = h.Snippet.Replace("**", ""),
                        }),
                    discussion = DiscussionStore.ToInfo(m.Discussion),
                });

            return Results.Ok(new { query = q, results });
        });

        group.MapGet("/discussions/export", async (HttpContext ctx, DiscussionStore store, ConversationExporter exporter) =>
        {
            var since = ctx.Request.Query["since"].FirstOrDefault() is { } s
                ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTime.UtcNow.AddDays(-7);
            var limit = 50;
            if (ctx.Request.Query.TryGetValue("limit", out var lv) && int.TryParse(lv, out var parsed))
                limit = Math.Clamp(parsed, 1, 200);

            var discussions = (await store.ListOpenAndRecentlyClosedAsync(since, ct: ctx.RequestAborted))
                .Where(d => d.LastActivity >= since && DiscussionAccessPolicy.CanRead(d, ctx))
                .Where(d => !d.Confidential)
                .Take(limit)
                .ToList();

            var markdown = await exporter.ExportAsync(discussions, since);
            return Results.Text(markdown, "text/markdown");
        });

        group.MapGet("/discussions/{id}/history-page", async (
            string id,
            HttpContext ctx,
            DiscussionStore store,
            IDiscussions discussions,
            RedComputeClient redCompute,
            ConversationUnread conversationUnread) =>
        {
            var before = ctx.Request.Query["before"].FirstOrDefault();
            var after = ctx.Request.Query["after"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(before) && !string.IsNullOrWhiteSpace(after))
                return HistoryError(400, "invalid_pagination", "before and after are mutually exclusive");

            var limit = 500;
            var requestedLimit = ctx.Request.Query["limit"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(requestedLimit))
            {
                if (!int.TryParse(requestedLimit, out limit))
                    return HistoryError(400, "invalid_pagination", "limit must be an integer");
                limit = Math.Clamp(limit, 1, 500);
            }

            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var direction = !string.IsNullOrWhiteSpace(before)
                ? "before"
                : !string.IsNullOrWhiteSpace(after) ? "after" : "newest";
            var edge = direction == "before"
                ? HistoryPageCursorEdge.Oldest
                : HistoryPageCursorEdge.Newest;
            var sessionlessEpoch = discussion.SessionId is null
                ? $"discussion:{discussion.EntityId:N}"
                : null;

            HistoryPageCursor? cursor = null;
            try
            {
                var encoded = direction == "before" ? before : after;
                if (!string.IsNullOrWhiteSpace(encoded))
                {
                    cursor = HistoryPageCursorCodec.Decode(
                        encoded,
                        discussion.Id,
                        discussion.SessionId,
                        sessionlessEpoch,
                        edge);
                }
            }
            catch (HistoryPageCursorException ex)
            {
                return HistoryError(
                    ex.Code == "invalid_cursor" ? 400 : 409,
                    ex.Code,
                    ex.Message);
            }

            SessionTranscriptPage? transcript = null;
            if (discussion.SessionId is not null)
            {
                // An empty transcript has no Compute keyset anchor. Older requests
                // then advance only the overlay half of the composite cursor.
                var skipComputeBefore = direction == "before"
                    && cursor is { ComputeCursor: null };
                if (!skipComputeBefore)
                {
                    SessionTranscriptPageResult result;
                    try
                    {
                        result = await redCompute.GetTranscriptPageAsync(
                            discussion.SessionId,
                            limit,
                            before: direction == "before" ? cursor?.ComputeCursor : null,
                            after: direction == "after" ? cursor?.ComputeCursor : null,
                            ctx.RequestAborted);
                    }
                    catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        return HistoryError(503, "redcompute_unavailable", ex.Message);
                    }

                    if (!result.Success)
                    {
                        if (result.StatusCode == 200)
                            return HistoryError(502, "invalid_transcript_page", "RedCompute returned an invalid transcript page");

                        var upstreamError = ReadErrorCode(result.Content);
                        if (upstreamError == "transcript_cursor_stale")
                            return HistoryError(409, "history_cursor_stale", "The history cursor belongs to an earlier transcript epoch");
                        if (upstreamError == "transcript_cursor_mismatch")
                            return HistoryError(409, "history_cursor_mismatch", "The history cursor does not match this discussion");
                        return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);
                    }
                    transcript = result.Value!;
                }

                if (cursor?.Epoch is { } cursorEpoch
                    && transcript is not null
                    && !string.Equals(cursorEpoch, transcript.Page.Epoch, StringComparison.Ordinal))
                    return HistoryError(409, "history_cursor_stale", "The history cursor belongs to an earlier transcript epoch");

                // A discussion can begin with overlays before its first canonical
                // transcript record exists. If more than one canonical page then
                // appears, treating the newest page as an ordinary `after` append
                // would strand its older prefix because the prior composite cursor
                // had no Compute anchor. Force the client through its existing
                // per-discussion reset/newest path so it installs a truthful oldest
                // cursor and hasEarlier fact.
                if (RequiresCanonicalBackfillReset(direction, cursor, transcript))
                    return HistoryError(
                        409,
                        "history_cursor_stale",
                        "Canonical transcript history appeared behind an overlay-only cursor; restart at the newest page");

                if (transcript is not null)
                {
                    if (transcript.Messages.Any(message => message.RecordCreatedAt is null))
                        return HistoryError(502, "invalid_transcript_page", "RedCompute transcript records do not expose storage boundaries");

                    var sessionStatus = DiscussionStatus.FromSessionStatus(
                        transcript.Session.Status,
                        discussion.Type,
                        transcript.Session.StopReason);
                    if (sessionStatus is not null && sessionStatus != discussion.Status)
                    {
                        var applied = await store.TrySetStatusAsync(
                            discussion.EntityId,
                            sessionStatus,
                            ctx.RequestAborted);
                        if (applied is not null)
                            discussion = discussion with { Status = applied };
                    }
                    if (transcript.Session.Status == "Idle")
                    {
                        discussion = await conversationUnread.ReconcileSettledAsync(
                            discussion,
                            ctx.RequestAborted);
                    }

                    var titled = await store.TryApplySessionTitleAsync(
                        discussion.EntityId,
                        transcript.Session.Title,
                        ctx.RequestAborted);
                    if (titled is not null)
                        discussion = titled;
                }
            }

            var epoch = transcript?.Page.Epoch ?? cursor?.Epoch ?? sessionlessEpoch!;
            OverlayPageRead overlayPage;
            try
            {
                overlayPage = await ReadOverlayPageAsync(
                    discussions,
                    discussion,
                    transcript,
                    cursor,
                    direction,
                    limit,
                    ctx.RequestAborted);
            }
            catch (HistoryOverlayPageTooLargeException ex)
            {
                return HistoryError(413, "history_overlay_page_too_large", ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return HistoryError(503, "history_paging_unavailable", ex.Message);
            }

            var canonicalUserUids = (transcript?.Messages ?? [])
                .Where(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.MessageUid))
                .Select(message => message.MessageUid!)
                .ToHashSet(StringComparer.Ordinal);
            var canonicalAssistantUids = (transcript?.Messages ?? [])
                .Where(message => message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.MessageUid))
                .Select(message => message.MessageUid!)
                .ToHashSet(StringComparer.Ordinal);
            var overlays = ProjectHistoryOverlays(
                overlayPage.Messages,
                canonicalUserUids,
                canonicalAssistantUids,
                sessionBacked: discussion.SessionId is not null,
                allowUserBridges: direction != "before");

            var canonicalMessages = ProjectCanonicalHistoryMessages(transcript?.Messages ?? []);
            var oldestBoundary = BoundaryAt(canonicalMessages, first: true)
                ?? BoundaryAt(overlayPage.Messages, first: true)
                ?? cursor?.Boundary;
            var newestBoundary = MaxBoundary(
                BoundaryAt(canonicalMessages, first: false),
                BoundaryAt(overlayPage.Messages, first: false))
                ?? cursor?.Boundary;
            var oldestOverlayAnchor = canonicalMessages.Count > 0
                ? canonicalMessages[0].Id
                : overlayPage.OldestId ?? cursor?.OverlayAnchorId;
            var newestOverlayAnchor = ResolveNewestOverlayAnchor(
                direction,
                overlayPage,
                cursor);
            // A null cursor on an authoritative Compute response means that edge is
            // exhausted. Only retain the previous nested cursor when Compute was
            // intentionally skipped for an already overlay-only older request.
            var oldestComputeCursor = ResolveComputeCursor(transcript, cursor, oldest: true);
            var newestComputeCursor = ResolveComputeCursor(
                transcript,
                cursor,
                oldest: false,
                preservePreviousOnEmpty: direction == "after");

            var hasEarlier = (transcript?.Page.HasEarlier ?? false) || overlayPage.HasEarlier;
            var hasLater = (transcript?.Page.HasLater ?? false) || overlayPage.HasLater;
            var pageHasRecords = canonicalMessages.Count > 0 || overlayPage.Messages.Count > 0;
            var oldestCursor = oldestBoundary is null
                || !ShouldEmitHistoryCursor(direction, HistoryPageCursorEdge.Oldest, pageHasRecords, hasEarlier, hasLater)
                ? null : HistoryPageCursorCodec.Encode(new(
                discussion.Id,
                discussion.SessionId,
                epoch,
                HistoryPageCursorEdge.Oldest,
                oldestComputeCursor,
                oldestBoundary,
                oldestOverlayAnchor));
            var newestCursor = newestBoundary is null
                || !ShouldEmitHistoryCursor(direction, HistoryPageCursorEdge.Newest, pageHasRecords, hasEarlier, hasLater)
                ? null : HistoryPageCursorCodec.Encode(new(
                discussion.Id,
                discussion.SessionId,
                epoch,
                HistoryPageCursorEdge.Newest,
                newestComputeCursor,
                newestBoundary,
                newestOverlayAnchor));

            return Results.Ok(new
            {
                discussion = DiscussionStore.ToInfo(discussion),
                session = transcript?.Session,
                messages = canonicalMessages,
                overlays,
                page = new
                {
                    epoch,
                    direction,
                    oldestCursor,
                    newestCursor,
                    hasEarlier,
                    hasLater,
                    fromSequence = transcript?.Page.FromSequence,
                    throughSequence = transcript?.Page.ThroughSequence,
                    boundaryComplete = transcript?.Page.BoundaryComplete ?? true,
                },
            });
        });

        group.MapGet("/discussions/{id}", async (string id, HttpContext ctx, DiscussionStore store, IDiscussions discussions, RedComputeClient redCompute, ConversationUnread conversationUnread) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            DateTime? since = null;
            if (ctx.Request.Query["since"].FirstOrDefault() is { } sv)
                since = DateTimeOffset.Parse(sv, null, System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime;

            int? tail = null;
            if (ctx.Request.Query["tail"].FirstOrDefault() is { } tv && int.TryParse(tv, out var tp))
                tail = Math.Clamp(tp, 1, 10_000);

            if (discussion.SessionId is not null)
            {
                var snapshot = await redCompute.GetSessionAsync(discussion.SessionId, tail: tail);
                if (snapshot is { Messages.Count: > 0 })
                {
                    // This projection is consumed by Nova itself and by the embedded
                    // Meet Nova chat. Reconcile the backing session here so every
                    // surface observes one canonical status and conversation revision
                    // without depending on Nova's separate list-sync timer.
                    var sessionStatus = DiscussionStatus.FromSessionStatus(
                        snapshot.Status,
                        discussion.Type,
                        snapshot.StopReason);
                    if (sessionStatus is not null && sessionStatus != discussion.Status)
                    {
                        var applied = await store.TrySetStatusAsync(
                            discussion.EntityId,
                            sessionStatus,
                            ctx.RequestAborted);
                        if (applied is not null)
                            discussion = discussion with { Status = applied };
                    }
                    if (snapshot.Status == "Idle")
                        discussion = await conversationUnread.ReconcileSettledAsync(
                            discussion,
                            ctx.RequestAborted);

                    var records = await discussions.GetMessagesAsync(discussion.EntityId);
                    var userAttachmentsByUid = records
                        .Where(m => IsAcceptedUserMessageSource(m.Metadata["source"]?.GetValue<string>()))
                        .Where(m => !string.IsNullOrWhiteSpace(m.Metadata["uid"]?.GetValue<string>()))
                        .GroupBy(m => m.Metadata["uid"]!.GetValue<string>())
                        .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());

                    var collapsed = ConversationExporter.CollapseMessages(snapshot.Messages)
                        .Where(message => !(message.Role == "user"
                            && message.MessageUid == discussion.SetupBootstrapMessageUid))
                        .ToList();
                    var pendingUserUids = FindPendingUserMessageUids(
                        collapsed.Where(m => m.Role == "user").Select(m => m.MessageUid),
                        records
                            .Where(m => m.Metadata["source"]?.GetValue<string>() == "user-message")
                            .Select(m => m.Metadata["uid"]?.GetValue<string>()));
                    var pendingNovaMessageUids = FindPendingUserMessageUids(
                        collapsed.Where(m => m.Role == "assistant").Select(m => m.MessageUid),
                        records
                            .Where(m => m.Metadata["source"]?.GetValue<string>() == "nova-message")
                            .Select(m => m.Metadata["uid"]?.GetValue<string>()));
                    var sessionMsgs = collapsed
                        .Where(IsVisibleSessionMessage)
                        .Select(m =>
                        {
                            var content = m.EventType == "text"
                                ? ConversationExporter.StripInjectedTags(m.Content ?? "")
                                : m.ToolResult ?? m.Content ?? "";
                            var saved = m.Role == "user" && m.MessageUid is not null
                                ? userAttachmentsByUid.GetValueOrDefault(m.MessageUid)
                                : null;
                            return new
                            {
                                id = (string?)null,
                                // Carried through so API callers (agents reacting via
                                // /reactions) can address the same message the UI does.
                                // Null for records predating the uid rollout -- better
                                // than synthesising a key that would not match.
                                messageUid = m.MessageUid,
                                role = m.Role,
                                parts = saved is not null
                                    ? MapUserMessageParts(saved.Metadata["parts_json"]?.GetValue<string>(),
                                        ConversationExporter.StripInjectedTags(saved.Content))
                                    : MapSessionMessageParts(m, content),
                                timestamp = m.Timestamp.ToString("o"),
                                senderAgentId = (string?)null,
                                source = (string?)"session-transcript",
                            };
                        });

                    // RedCompute's session mirror can lag a successful send. Keep
                    // Nova's synchronously persisted copy visible until its stable
                    // uid appears in the session transcript, then let the transcript
                    // replace it without ever rendering a duplicate.
                    var pendingUserMsgs = records
                        .Where(m => m.Metadata["source"]?.GetValue<string>() == "user-message")
                        .Where(m => !string.IsNullOrWhiteSpace(m.Metadata["uid"]?.GetValue<string>()))
                        .GroupBy(m => m.Metadata["uid"]!.GetValue<string>())
                        .Select(g => g.OrderByDescending(m => m.CreatedAt).First())
                        .Where(m => pendingUserUids.Contains(m.Metadata["uid"]!.GetValue<string>()))
                        .Select(m => new
                        {
                            id = (string?)m.Id.ToString(),
                            messageUid = m.Metadata["uid"]?.GetValue<string>(),
                            role = m.Role,
                            parts = MapUserMessageParts(m.Metadata["parts_json"]?.GetValue<string>(), m.Content),
                            timestamp = m.CreatedAt.UtcDateTime.ToString("o"),
                            senderAgentId = (string?)null,
                            source = (string?)"user-message",
                        });

                    // Automation-created discussions exist before their RedCompute
                    // session. Their opening assistant message is persisted locally,
                    // then replayed into the session when the user first replies. The
                    // replay is deliberately best-effort, so keep the persisted copy
                    // unless its stable uid is actually present in the transcript.
                    var pendingNovaMsgs = records
                        .Where(m => m.Metadata["source"]?.GetValue<string>() == "nova-message")
                        .Where(m => !string.IsNullOrWhiteSpace(m.Metadata["uid"]?.GetValue<string>()))
                        .GroupBy(m => m.Metadata["uid"]!.GetValue<string>())
                        .Select(g => g.OrderByDescending(m => m.CreatedAt).First())
                        .Where(m => pendingNovaMessageUids.Contains(m.Metadata["uid"]!.GetValue<string>()))
                        .Select(m => new
                        {
                            id = (string?)m.Id.ToString(),
                            messageUid = m.Metadata["uid"]?.GetValue<string>(),
                            role = m.Role,
                            parts = MapParts(m.Metadata["parts_json"]?.GetValue<string>(), m.Content),
                            timestamp = m.CreatedAt.UtcDateTime.ToString("o"),
                            senderAgentId = m.Metadata["sender_agent_id"]?.GetValue<string>(),
                            source = (string?)"nova-message",
                        });

                    var eventMsgs = records
                        .Where(m => (m.Metadata["source"]?.GetValue<string>() ?? "").StartsWith("event:"))
                        .Select(m =>
                        {
                            var ts = (m.Metadata["timestamp"]?.GetValue<string>() is { } t
                                ? DateTimeOffset.Parse(t)
                                : m.CreatedAt).UtcDateTime;
                            return new
                            {
                                id = (string?)m.Id.ToString(),
                                messageUid = m.Metadata["uid"]?.GetValue<string>(),
                                role = m.Role,
                                parts = MapParts(m.Metadata["parts_json"]?.GetValue<string>(), m.Content),
                                timestamp = ts.ToString("o"),
                                senderAgentId = m.Metadata["sender_agent_id"]?.GetValue<string>(),
                                source = m.Metadata["source"]?.GetValue<string>(),
                            };
                        });

                    var merged = sessionMsgs.Concat(pendingUserMsgs).Concat(pendingNovaMsgs).Concat(eventMsgs)
                        .OrderBy(m => m.timestamp)
                        .AsEnumerable();

                    if (since.HasValue)
                        merged = merged.Where(m => DateTimeOffset.Parse(m.timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime > since.Value);
                    if (tail.HasValue)
                        merged = merged.TakeLast(tail.Value);

                    return Results.Ok(new
                    {
                        discussion = DiscussionStore.ToInfo(discussion),
                        messages = merged,
                    });
                }
            }

            // Fallback: nova-messages only (no active session or empty session transcript)
            var fallbackRecords = await discussions.GetMessagesAsync(discussion.EntityId);
            var msgs = fallbackRecords
                .Where(m => m.Metadata["source"]?.GetValue<string>() != "queued-user-message")
                .Select(m =>
            {
                var partsJson = m.Metadata["parts_json"]?.GetValue<string>();
                var isUserMessage = m.Metadata["source"]?.GetValue<string>() == "user-message";
                var ts = (m.Metadata["timestamp"]?.GetValue<string>() is { } t
                    ? DateTimeOffset.Parse(t)
                    : m.CreatedAt).UtcDateTime;
                return new
                {
                    id = (string?)m.Id.ToString(),
                    messageUid = m.Metadata["uid"]?.GetValue<string>(),
                    role = m.Role,
                    parts = isUserMessage
                        ? MapUserMessageParts(partsJson, m.Content)
                        : MapParts(partsJson, m.Content),
                    timestamp = ts.ToString("o"),
                    senderAgentId = m.Metadata["sender_agent_id"]?.GetValue<string>(),
                    source = m.Metadata["source"]?.GetValue<string>(),
                };
            }).AsEnumerable();

            if (since.HasValue)
                msgs = msgs.Where(m => DateTimeOffset.Parse(m.timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime > since.Value);
            if (tail.HasValue)
                msgs = msgs.TakeLast(tail.Value);

            return Results.Ok(new
            {
                discussion = DiscussionStore.ToInfo(discussion),
                messages = msgs,
            });
        });

        group.MapGet("/discussions/{id}/messages/{recordId:long}/output", async (
            string id,
            long recordId,
            HttpContext ctx,
            DiscussionStore store,
            RedComputeClient redCompute) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            if (string.IsNullOrWhiteSpace(discussion.SessionId))
                return Results.NotFound(new { error = "session_not_found" });
            if (recordId <= 0)
                return Results.BadRequest(new { error = "invalid_record_id" });

            using var upstream = await redCompute.OpenTranscriptOutputAsync(
                discussion.SessionId,
                recordId,
                ctx.Request.Headers.Range.ToString(),
                ctx.Request.Query["download"] == "true",
                ctx.RequestAborted);

            ctx.Response.StatusCode = (int)upstream.StatusCode;
            if (upstream.Content.Headers.ContentType is { } contentType)
                ctx.Response.ContentType = contentType.ToString();
            if (upstream.Content.Headers.ContentLength is { } contentLength)
                ctx.Response.ContentLength = contentLength;
            if (upstream.Content.Headers.ContentRange is { } contentRange)
                ctx.Response.Headers.ContentRange = contentRange.ToString();
            if (upstream.Headers.AcceptRanges.Count > 0)
                ctx.Response.Headers.AcceptRanges = string.Join(", ", upstream.Headers.AcceptRanges);
            if (upstream.Headers.ETag is { } etag)
                ctx.Response.Headers.ETag = etag.ToString();
            if (upstream.Content.Headers.LastModified is { } lastModified)
                ctx.Response.Headers.LastModified = lastModified.ToString("R");
            if (upstream.Content.Headers.ContentDisposition is { } disposition)
                ctx.Response.Headers.ContentDisposition = disposition.ToString();

            await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            return Results.Empty;
        })
            .WithName("NovaDiscussionTranscriptOutput")
            .WithSummary("Authorized transcript media output")
            .WithDescription("Streams one payload-backed RedCompute transcript output after enforcing access to its owning Nova discussion. Supports HTTP byte ranges.");

        group.MapGet("/discussions/{id}/artifacts", GetArtifactsAsync)
            .WithName("NovaDiscussionArtifacts")
            .WithSummary("Artifacts of a discussion")
            .WithDescription("Images, video, audio, documents, links and entity references the discussion shows, derived by RedCompute from the canonical transcript. Authorized like history-page; assistant-image targets are rewritten to this discussion's authorized output proxy. Query: kinds, origins, sinceSequence, limit, cursor.");

        group.MapGet("/discussions/{id}/images", async (string id, HttpContext ctx, DiscussionStore store, IDiscussions discussions) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var records = await discussions.GetMessagesAsync(discussion.EntityId);
            var result = records
                .Where(m => m.Metadata["parts_json"]?.GetValue<string>() != null
                    && m.Metadata["source"]?.GetValue<string>() == "user-message")
                .Select(m => new
                {
                    content = m.Content,
                    images = JsonSerializer.Deserialize<JsonElement[]>(m.Metadata["parts_json"]!.GetValue<string>(), JsonOptions),
                    timestamp = m.CreatedAt.UtcDateTime.ToString("o"),
                });

            return Results.Ok(result);
        });

        group.MapGet("/discussions/{id}/context", async (string id, HttpContext ctx, DiscussionStore store, MessagePipeline pipeline, ExtensionContributions extensions) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            var userId = UserId(ctx);
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var cutoff = DateTime.UtcNow.AddDays(-2);
            var all = (await store.ListOpenAndRecentlyClosedAsync(
                    cutoff, ct: ctx.RequestAborted))
                .Where(d => DiscussionAccessPolicy.CanRead(d, ctx))
                .Where(d => !d.Confidential || d.Id == id)
                .ToList();

            var own = discussion.AgentId != null
                ? all.Where(d => d.AgentId == discussion.AgentId).ToList()
                : all;
            var others = discussion.AgentId != null
                ? all.Where(d => d.AgentId != discussion.AgentId && !DiscussionStatus.IsClosed(d.Status)).Take(5).ToList()
                : null;

            var (outfit, outfitAsset) = await pipeline.ResolveOutfitContextAsync(discussion.AgentId);
            var extensionContexts = await extensions.CollectContextAsync(
                userId, discussion.AgentId, discussion.Id, "inspection");
            var snapshot = NovaContextBuilder.BuildSnapshot(
                own, others, outfit, outfitAsset,
                extensionContexts: extensionContexts);
            return Results.Ok(snapshot);
        });

        group.MapGet("/discussions/{id}/export", async (string id, HttpContext ctx, DiscussionStore store, ConversationExporter exporter) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var markdown = await exporter.ExportSingleAsync(discussion);
            return Results.Text(markdown, "text/markdown");
        });

        group.MapPut("/discussions/{id}/title", async (string id, DiscussionTitleRequest request, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var updated = await store.SetManualTitleAsync(
                discussion.EntityId, request.Title, ctx.RequestAborted);
            return updated is null ? NotFound() : Results.Ok(DiscussionStore.ToInfo(updated));
        });

        group.MapPost("/discussions/{id}/title/generate", async (
            string id, HttpContext ctx, DiscussionStore store, AgentDirectory agents,
            RedComputeClient redCompute,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            if (!string.Equals(discussion.Type, "chat", StringComparison.Ordinal))
                return Results.Json(new { error = "title_generation_not_supported", message = "Only chat discussions can generate a title." }, statusCode: 409);
            if (discussion.SessionId is null)
                return Results.Json(new { error = "session_required", message = "This discussion has no session transcript yet." }, statusCode: 409);

            var agent = discussion.AgentId is not null
                ? await agents.GetAgentAsync(discussion.AgentId, ctx.RequestAborted)
                : null;
            if (agent is null)
                return Results.Json(new { error = "missing_agent", message = "The discussion agent is unavailable." }, statusCode: 422);

            var snapshot = await redCompute.GetSessionAsync(
                discussion.SessionId, ctx.RequestAborted, tail: 500);
            if (snapshot is null)
                return Results.Json(new { error = "session_unavailable", message = "The discussion transcript is unavailable." }, statusCode: 503);
            var prompt = DiscussionTitleGeneration.BuildPrompt(discussion.Title, snapshot.Messages);
            if (prompt is null)
                return Results.Json(new { error = "transcript_empty", message = "There is not enough conversation to generate a title." }, statusCode: 409);

            var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(
                entities, discussion.OwnerId, ctx.RequestAborted);
            var provenance = await NovaComputeProvenance.CreateAsync(
                entities, agent, beneficiary,
                $"/api/apps/nova/discussions/{id}/title/generate",
                [new ComputeContextReference("discussion", id),
                 new ComputeContextReference("session", discussion.SessionId)],
                method: "POST", ct: ctx.RequestAborted);
            var generated = await redCompute.GenerateAsync(new
            {
                mode = "oneshot",
                qualityTier = "fast",
                system = "Write a concise, specific title for this conversation in at most six words. Output only the title, with no label, quotes, or ending punctuation.",
                messages = new[] { new { role = "user", content = prompt } },
                maxTokens = 48,
                confidential = discussion.Confidential,
            }, "Update Nova discussion title", provenance, ctx.RequestAborted);

            var title = DiscussionTitleGeneration.CleanGeneratedTitle(generated.Text);
            if (!generated.Success || title is null)
                return Results.Json(new
                {
                    error = "title_generation_failed",
                    message = generated.Error ?? "The fast model did not return a usable title.",
                    jobId = generated.JobId,
                }, statusCode: generated.StatusCode is >= 400 and < 600 ? generated.StatusCode : 502);

            var updated = await store.SetGeneratedTitleAsync(
                discussion.EntityId, title, ctx.RequestAborted);
            return updated is null
                ? NotFound()
                : Results.Ok(new
                {
                    discussion = DiscussionStore.ToInfo(updated),
                    jobId = generated.JobId,
                    qualityTier = "fast",
                });
        });

        group.MapPut("/discussions/{id}/title/fallback", async (string id, DiscussionTitleRequest request, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var updated = await store.TrySetFallbackTitleAsync(
                discussion.EntityId, request.Title ?? "", ctx.RequestAborted);
            return updated is null ? NotFound() : Results.Ok(DiscussionStore.ToInfo(updated));
        });

        // A session.updated event is only an invalidation signal. Re-read the
        // linked session here so an old pushed title can never win a race with
        // fresher RedCompute state or bypass the store's provenance policy.
        group.MapPut("/discussions/{id}/title/session", async (
            string id, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute) =>
        {
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            if (discussion.SessionId is null || !string.Equals(discussion.Type, "chat", StringComparison.Ordinal))
                return Results.Ok(DiscussionStore.ToInfo(discussion));

            var snapshot = await redCompute.GetSessionAsync(
                discussion.SessionId, ctx.RequestAborted, tail: 1);
            if (snapshot is null)
                return Results.Json(new { error = "RedCompute session unavailable", code = "session_unavailable" }, statusCode: 503);

            var updated = await store.TryApplySessionTitleAsync(
                discussion.EntityId, snapshot.Title, ctx.RequestAborted);
            return updated is null ? NotFound() : Results.Ok(DiscussionStore.ToInfo(updated));
        });

        group.MapPut("/discussions/{id}/confidential", async (string id,
            DiscussionConfidentialRequest request, HttpContext ctx, DiscussionStore store,
            AgentDirectory agents, RedComputeClient redCompute,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanManageConfidentiality(discussion, ctx))
                return AccessDenied(discussion);

            if (request.Confidential && !discussion.Confidential && discussion.SessionId is not null)
            {
                var agent = discussion.AgentId is not null
                    ? await agents.GetAgentAsync(discussion.AgentId, ctx.RequestAborted)
                    : null;
                if (agent is null)
                    return Results.Json(new { error = "missing_agent" }, statusCode: 422);
                var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(
                    entities, discussion.OwnerId, ctx.RequestAborted);
                var provenance = await NovaComputeProvenance.CreateAsync(
                    entities, agent, beneficiary,
                    $"/api/apps/nova/discussions/{id}/confidential",
                    [new ComputeContextReference("discussion", id),
                     new ComputeContextReference("session", discussion.SessionId)],
                    method: "PUT", ct: ctx.RequestAborted);
                if (!await redCompute.SetConfidentialAsync(
                        discussion.SessionId, provenance, ctx.RequestAborted))
                    return Results.Json(new
                    {
                        error = "confidentiality_propagation_failed",
                        message = "RedCompute did not accept the confidential session boundary",
                    }, statusCode: 502);
            }

            await store.PatchAsync(discussion.EntityId, new JsonObject { ["confidential"] = request.Confidential });
            return Results.Ok(DiscussionStore.ToInfo(discussion with { Confidential = request.Confidential }));
        });

        // Worktree attachments: where a discussion works inside a repository. The discussion keeps
        // its Agent-workspace cwd; this records the folders it touches so every turn can say so.
        group.MapGet("/discussions/{id}/worktrees", async (string id, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            return Results.Ok(new { items = discussion.Worktrees ?? [] });
        });

        group.MapPost("/discussions/{id}/worktrees", async (string id, AttachWorktreeRequest request,
            HttpContext ctx, DiscussionStore store, CodeWorktreeClient code,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
        {
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            return discussion is null ? NotFound() : await AttachWorktreeAsync(discussion, request, ctx, store, code, entities, events);
        });

        // Detaching only forgets the attachment; the worktree and its branch stay untouched.
        group.MapDelete("/discussions/{id}/worktrees", async (string id, [FromBody] DetachWorktreeRequest request,
            HttpContext ctx, DiscussionStore store,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
        {
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            return discussion is null ? NotFound() : await DetachWorktreeAsync(discussion, request, ctx, store, events);
        });

        // Every open discussion the caller can read that works in one of the repository's worktrees.
        group.MapGet("/worktree-attachments", (HttpContext ctx, DiscussionStore store) =>
            WorktreeAttachmentsAsync(ctx.Request.Query["repositoryId"].FirstOrDefault(), ctx, store));

        group.MapPut("/discussions/{id}/read", async (string id, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            DiscussionReadRequest? request = null;
            try { request = await ctx.Request.ReadFromJsonAsync<DiscussionReadRequest>(JsonOptions); }
            catch { /* compatibility with bodyless callers */ }
            var updated = await store.MarkConversationReadAsync(discussion.EntityId,
                request?.ConversationRevision ?? discussion.ConversationRevision);
            return Results.Ok(DiscussionStore.ToInfo(updated ?? discussion));
        });

        group.MapPut("/discussions/{id}/activity", async (string id, HttpContext ctx,
            DiscussionStore store, ConversationUnread conversationUnread) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var now = DateTime.UtcNow;
            await store.TouchAsync(discussion.EntityId);
            var updated = await conversationUnread.ReconcileSettledAsync(
                discussion with { LastActivity = now });
            var status = await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Idle);
            if (status is not null) updated = updated with { Status = status };
            return Results.Ok(DiscussionStore.ToInfo(updated));
        });

        group.MapPut("/discussions/{id}/stopped", async (string id, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var result = await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Stopped);
            return Results.Ok(DiscussionStore.ToInfo(discussion with { Status = result ?? discussion.Status }));
        });

        group.MapPost("/discussions/{id}/resume", async (string id, HttpContext ctx,
            DiscussionStore store, RedComputeClient redCompute, AgentDirectory agents,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            if (DiscussionStatus.IsClosed(discussion.Status))
                return Results.Json(new { error = "closed", message = "Archived discussions cannot be resumed" }, statusCode: 409);

            // A delivery discussion can be populated through nova-message before
            // its provider receives a turn. The attached RedCompute row is then
            // only an empty shell: there is no provider conversation/thread to
            // resume. Detach it and let SendAsync lazily create the configured
            // provider session on the user's first reply; that path already
            // replays the persisted nova-message into the fresh session.
            if (discussion.SessionId is null)
            {
                var status = await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Idle);
                return Results.Ok(new { sessionId = (string?)null, status = status ?? discussion.Status, initialized = false });
            }

            var probe = await redCompute.ProbeSessionForResumeAsync(discussion.SessionId);
            if (!probe.Reachable)
                return Results.Json(new { error = "redcompute_unavailable", message = "RedCompute could not be reached" }, statusCode: 503);

            if (!probe.Exists || string.IsNullOrWhiteSpace(probe.ProviderSessionId))
            {
                // Reopen must never discard the retained archive/session link.
                // Leave it visibly stopped so the user sees the provider history
                // is unavailable and can retry or inspect the read-only transcript.
                if (ctx.Request.Query["reopened"].FirstOrDefault() == "true")
                    return Results.Json(new
                    {
                        error = "resume_failed",
                        message = "The archived provider session is no longer available",
                    }, statusCode: 502);
                var oldSessionId = discussion.SessionId;
                await store.PatchAsync(discussion.EntityId, new JsonObject
                {
                    ["session_id"] = null,
                    ["status"] = DiscussionStatus.Idle,
                });
                await redCompute.DismissAsync(oldSessionId);
                return Results.Ok(new { sessionId = (string?)null, status = DiscussionStatus.Idle, initialized = false });
            }

            // A send from a stale client view can land on a session that is already
            // running; RedCompute answers that resume with a 500. Resume is idempotent
            // here: a live session is the outcome the caller wanted.
            if (probe.Status is "Active" or "Idle" or "Starting")
            {
                var liveStatus = await store.TrySetStatusAsync(discussion.EntityId,
                    DiscussionStatus.FromSessionStatus(probe.Status, discussion.Type) ?? DiscussionStatus.Idle);
                return Results.Ok(new { sessionId = discussion.SessionId, status = liveStatus ?? discussion.Status, initialized = true });
            }

            var agent = discussion.AgentId != null ? await agents.GetAgentAsync(discussion.AgentId, ctx.RequestAborted) : null;
            if (agent == null)
                return Results.Json(new { error = "missing_agent", message = "The discussion has no linked Agent entity" }, statusCode: 422);
            var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, discussion.OwnerId, ctx.RequestAborted);
            var provenance = await NovaComputeProvenance.CreateAsync(entities, agent, beneficiary,
                $"/api/apps/nova/discussions/{id}/resume",
                [new ComputeContextReference("discussion", id),
                 new ComputeContextReference("session", discussion.SessionId)], method: "POST",
                ct: ctx.RequestAborted);
            if (!await redCompute.ResumeAsync(discussion.SessionId, provenance, ctx.RequestAborted))
                return Results.Json(new { error = "resume_failed", message = "The provider session could not be resumed" }, statusCode: 502);

            var resumedStatus = await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Idle);
            return Results.Ok(new { sessionId = discussion.SessionId, status = resumedStatus ?? discussion.Status, initialized = true });
        });

        group.MapPost("/discussions/{id}/reopen", async (
            string id,
            HttpContext ctx,
            DiscussionStore store,
            DiscussionActivity activity,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
        {
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return NotFound();

            var decision = DiscussionMutationPolicy.Reopen(discussion, ctx);
            if (decision == DiscussionReopenDecision.AccessDenied) return AccessDenied(discussion);
            if (decision == DiscussionReopenDecision.Archiving)
                return Results.Json(new { error = "archiving", message = "Archive finalization is still in progress" }, statusCode: 409);
            if (decision == DiscussionReopenDecision.UnsupportedType)
                return Results.Json(new { error = "unsupported_type", message = "LIVE and Heartbeat discussions cannot be reopened" }, statusCode: 409);
            if (decision == DiscussionReopenDecision.MissingSession)
                return Results.Json(new { error = "missing_session", message = "This archive has no provider session to reopen" }, statusCode: 409);
            if (decision == DiscussionReopenDecision.AlreadyOpen)
                return Results.Ok(DiscussionStore.ToInfo(discussion));

            var reopened = await store.TryReopenArchivedAsync(discussion.EntityId, ctx.RequestAborted);
            if (reopened is null)
                return Results.Json(new { error = "archiving", message = "Archive finalization is still in progress" }, statusCode: 409);

            await events.PublishAsync("discussion.changed", new JsonObject
            {
                ["discussionId"] = id,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["confidential"] = reopened.Confidential,
            }, ctx.RequestAborted);
            _ = activity.OnReopened(id, reopened.Title, reopened.Confidential);
            return Results.Ok(DiscussionStore.ToInfo(reopened));
        });

        group.MapDelete("/discussions/{id}", async (string id, HttpContext ctx, DiscussionStore store, DiscussionLifecycle lifecycle, DiscussionActivity activity, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            if (discussion.Type == "live")
                return Results.Json(new { error = "Live discussions cannot be archived" }, statusCode: 400);

            if (discussion.Type == HeartbeatService.DiscussionType)
                return Results.Json(new { error = "Heartbeat discussions are managed by Agent LIVE — disable LIVE to remove the paired presence" }, statusCode: 400);

            if (DiscussionStatus.IsClosed(discussion.Status))
                return Results.Ok(DiscussionStore.ToInfo(discussion)); // idempotent

            // Two-phase: commit the archive intent now (wins any status race), stop
            // and verify the session in the background, archived once confirmed.
            var status = await lifecycle.BeginArchiveAsync(discussion);
            if (status == null) return NotFound();

            await events.PublishAsync("discussion.changed", new JsonObject
            {
                ["discussionId"] = id,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                ["confidential"] = discussion.Confidential,
            }, ctx.RequestAborted);
            _ = activity.OnArchived(id, discussion.Title, discussion.Confidential);
            return Results.Ok(DiscussionStore.ToInfo(discussion with { Status = status }));
        });

        group.MapPost("/discussions/{id}/clear", async (string id, HttpContext ctx, DiscussionStore store, IDiscussions discussions, RedComputeClient redCompute, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            if (discussion.Type != "live")
                return Results.Json(new { error = "Only LIVE discussions can be cleared" }, statusCode: 400);

            if (discussion.SessionId is not null)
            {
                try { await redCompute.StopAsync(discussion.SessionId); }
                catch { }
            }

            await discussions.ClearMessagesAsync(discussion.EntityId);

            await store.PatchAsync(discussion.EntityId, new JsonObject
            {
                ["session_id"] = null,
                ["message_count"] = 0,
                ["last_context_json"] = null,
                ["last_activity"] = DateTimeOffset.UtcNow.ToString("O"),
            });
            await store.ResetConversationReadAsync(discussion.EntityId);

            // Day marker; the post bumps message_count back to 1.
            await store.PostMessageAsync(discussion.EntityId, "assistant", "New day. Timeline cleared.", new JsonObject
            {
                ["source"] = "event:system",
                ["uid"] = Guid.NewGuid().ToString("N"),
            });

            await events.PublishAsync("discussion.cleared", new JsonObject { ["discussionId"] = id });

            var cleared = await store.GetAsync(id)
                ?? discussion with { SessionId = null, MessageCount = 1,
                    ConversationRevision = 0, ReadConversationRevision = 0,
                    LastProcessedSessionAssistantUid = "" };
            return Results.Ok(new { cleared = true, discussion = DiscussionStore.ToInfo(cleared) });
        });

        group.MapPost("/discussions/{id}/rotate", async (string id, HttpContext ctx, DiscussionStore store, AgentDirectory agents, DiscussionLifecycle lifecycle, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, MessagePipeline pipeline, DiscussionActivity activity, HeartbeatService heartbeat) =>
        {
            var idempotencyKey = ctx.Request.Headers["Idempotency-Key"].FirstOrDefault();
            // "live" resolves to Nova's current LIVE discussion — the system:live-rotation
            // http-action automation calls this form because it cannot resolve the id itself.
            var candidates = id == "live"
                ? await store.ListByTypeAsync("live", ct: ctx.RequestAborted)
                : null;
            var discussion = id == "live"
                ? LiveDiscussionAlias.FirstOpen(candidates!, agents.NovaAgentId)
                : await store.GetAsync(id);
            if (discussion is null && id == "live" && !string.IsNullOrWhiteSpace(idempotencyKey))
            {
                // Recover the narrow crash window after the old LIVE was marked
                // archiving but before the replacement entity was created.
                discussion = candidates!.FirstOrDefault(d => d.Type == "live"
                    && (agents.NovaAgentId == null || d.AgentId == null
                        || d.AgentId == agents.NovaAgentId));
            }
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var prior = await store.GetByCreationIdempotencyKeyAsync(
                    $"live-rotation:{idempotencyKey}", ctx.RequestAborted);
                if (prior is not null)
                {
                    pipeline.BeginSessionCreation(prior);
                    heartbeat.OnLiveRotated(prior.AgentId, idempotencyKey);
                    return Results.Ok(new
                    {
                        reused = true,
                        created = DiscussionStore.ToInfo(prior),
                    });
                }
            }

            if (discussion.Type != "live")
                return Results.Json(new { error = "Only LIVE discussions can be rotated" }, statusCode: 400);

            // Same two-phase archive as DELETE; session_id stays until the finalizer
            // confirms the stop, so the session cannot be orphaned by a rotation.
            await lifecycle.BeginArchiveAsync(discussion);
            _ = activity.OnArchived(discussion.Id, discussion.Title, discussion.Confidential);

            DiscussionRead fresh;
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                (fresh, _) = await store.GetOrCreateIdempotentAsync(
                    $"live-rotation:{idempotencyKey}", discussion.AgentId,
                    discussion.OwnerId, "live", ct: ctx.RequestAborted);
            }
            else
            {
                fresh = await store.CreateAsync(null, discussion.AgentId,
                    discussion.OwnerId, "live", ct: ctx.RequestAborted);
            }

            await events.PublishAsync("discussion.rotated", new JsonObject
            {
                ["oldDiscussionId"] = discussion.Id,
                ["newDiscussionId"] = fresh.Id,
                ["agentId"] = fresh.AgentId,
            });

            // LIVE rotation is the heartbeat's day boundary: end-of-day tick,
            // handoff, session reset (§5 of the heartbeat design).
            heartbeat.OnLiveRotated(fresh.AgentId, idempotencyKey);

            pipeline.BeginSessionCreation(fresh);

            return Results.Ok(new
            {
                archived = DiscussionStore.ToInfo(discussion with { Status = DiscussionStatus.Archiving }),
                created = DiscussionStore.ToInfo(fresh),
            });
        });

        group.MapPost("/discussions/{id}/event", (string id, DiscussionEventRequest request, HttpContext ctx, DiscussionStore store, AgentDirectory agents, EventInjector injector, MessagePipeline pipeline)
            => PostEventAsync(id, request, ctx, store, agents, injector, pipeline));

        group.MapPost("/discussions/{id}/nova-message", async (string id, NovaMessageRequest request,
            HttpContext ctx, DiscussionStore store, ConversationUnread conversationUnread,
            RedComputeClient redCompute, AgentDirectory agents,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events,
            DiscussionActivity activity, VoiceBackendForwarder voiceForwarder) =>
        {
            var discussion = await LiveDiscussionAlias.ResolveAsync(id, agents.NovaAgentId,
                i => store.GetAsync(i, ctx.RequestAborted),
                () => store.ListByTypeAsync("live", ct: ctx.RequestAborted));
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            if (DiscussionApplicationOwner.IsForeign(discussion)) return DiscussionApplicationOwner.Refusal(discussion);
            if (DiscussionMutationPolicy.RejectSend(discussion)) return Closed();

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

            // From here on use the concrete id: "live" is only a route alias.
            id = discussion.Id;
            discussion = await conversationUnread.EnsureBaselineAsync(discussion, ctx.RequestAborted);

            string? partsJson = null;
            if (!string.IsNullOrEmpty(request.AudioUrl))
            {
                partsJson = JsonSerializer.Serialize(new object[]
                {
                    new { type = "text", content = request.Content },
                    new { type = "audio", content = request.AudioUrl },
                });
            }

            var uid = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? Guid.NewGuid().ToString("N")
                : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(request.IdempotencyKey)));
            var messageMetadata = new JsonObject
            {
                ["parts_json"] = partsJson,
                ["source"] = "nova-message",
                ["sender_agent_id"] = request.SenderAgentId,
                ["uid"] = uid,
            };
            var (created, revisedDiscussion) = await store.PostConversationMessageAsync(
                discussion.EntityId, request.Content, messageMetadata, request.IdempotencyKey,
                UserId(ctx), ctx.RequestAborted);
            if (!created)
                return Results.Ok(new
                {
                    success = true,
                    reused = true,
                    discussion = DiscussionStore.ToInfo(revisedDiscussion ?? discussion),
                });
            discussion = revisedDiscussion ?? discussion;

            var patch = new JsonObject { ["injected_context"] = request.Content };
            string? namePatch = null;
            if (!string.IsNullOrWhiteSpace(request.Title))
            {
                patch["title"] = request.Title;
                patch["title_source"] = DiscussionTitleSource.System;
                namePatch = request.Title;
            }
            await store.PatchAsync(discussion.EntityId, patch, namePatch);

            discussion = await store.GetAsync(id, ctx.RequestAborted) ?? discussion;
            // Best-effort inject into live session (message is already persisted above).
            // If the session isn't ready, SendAsync replays it when the user first
            // messages the discussion.
            if (discussion.SessionId is not null)
            {
                try
                {
                    var agent = discussion.AgentId is null
                        ? null : await agents.GetAgentAsync(discussion.AgentId, ctx.RequestAborted);
                    if (agent is null) throw new InvalidOperationException("The discussion has no linked Agent");
                    var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(
                        entities, discussion.OwnerId, ctx.RequestAborted);
                    var provenance = await NovaComputeProvenance.CreateAsync(entities, agent, beneficiary,
                        $"/api/apps/nova/discussions/{discussion.Id}/nova-message",
                        [new ComputeContextReference("discussion", discussion.Id),
                         new ComputeContextReference("session", discussion.SessionId)],
                        method: "POST", ct: ctx.RequestAborted);
                    object? metadata = request.SenderAgentId is not null
                        ? new { senderAgentId = request.SenderAgentId }
                        : null;
                    await redCompute.InjectAsync(discussion.SessionId, new
                    {
                        role = "assistant",
                        content = request.Content,
                        audioUrl = string.IsNullOrEmpty(request.AudioUrl) ? null : request.AudioUrl,
                        metadata,
                        messageUid = uid,
                    }, provenance, ctx.RequestAborted);
                }
                catch { /* best-effort — message is already persisted */ }
            }

            var eventName = discussion.Confidential
                ? "discussion.changed"
                : "discussion.nova-message";
            await events.PublishAsync(eventName, discussion.Confidential
                ? new JsonObject
                {
                    ["discussionId"] = id,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["confidential"] = true,
                }
                : new JsonObject
                {
                    ["discussionId"] = id,
                    ["content"] = request.Content,
                    ["audioUrl"] = request.AudioUrl,
                    ["senderAgentId"] = request.SenderAgentId,
                    ["messageUid"] = uid,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["conversationRevision"] = discussion.ConversationRevision,
                    ["readConversationRevision"] = discussion.ReadConversationRevision,
                });

            _ = activity.OnNovaMessage(id, discussion.Title, request.Content, discussion.Confidential);
            // A proactive message never settles a session turn; tell an open voice directly.
            _ = voiceForwarder.OnDirectAssistantMessageAsync(id, uid, request.Content, CancellationToken.None);
            return Results.Ok(new { success = true, discussion = DiscussionStore.ToInfo(discussion) });
        });

        group.MapPost("/discussions/{id}/message", async (string id, DiscussionMessageRequest request, HttpContext ctx, DiscussionStore store, MessagePipeline pipeline, DeviceResolver devices, LiveEvents live, DiscussionActivity activity, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            var userId = UserId(ctx);
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            if (DiscussionApplicationOwner.IsForeign(discussion)) return DiscussionApplicationOwner.Refusal(discussion);
            if (DiscussionMutationPolicy.RejectSend(discussion)) return Closed();

            if (string.IsNullOrWhiteSpace(request.Content)
                && (request.Images == null || request.Images.Length == 0)
                && (request.Input == null || request.Input.Length == 0))
                return Results.BadRequest(new { error = "Content or at least one attachment is required" });

            var ua = ctx.Request.Headers.UserAgent.ToString();
            var browserId = ctx.Request.Headers.TryGetValue("X-Leaf-Installation-Id", out var leafId)
                ? leafId.ToString()
                : ctx.Request.Headers.TryGetValue("X-Device-Id", out var legacyId) ? legacyId.ToString() : null;
            var resolved = await devices.ResolveAsync(ua, browserId);

            live.NoteDevice(resolved);

            var idempotencyKey = ctx.Request.Headers["X-Idempotency-Key"].FirstOrDefault();

            var humanInitiated = DiscussionAccessPolicy.IsHumanInitiated(ctx.User);
            var outcome = request.Input is { Length: > 0 }
                ? await pipeline.SendInputAsync(
                    discussion, userId, request.Input, resolved,
                    request.InputMethod ?? "typed", request.Delivery,
                    idempotencyKey, request.DisplayContent, request.MessageUid,
                    humanInitiated: humanInitiated)
                : await pipeline.SendAsync(
                    discussion, userId, request.Content, request.Images, resolved,
                    request.InputMethod ?? "typed", request.Delivery,
                    idempotencyKey, request.DisplayContent, request.MessageUid,
                    humanInitiated: humanInitiated);

            if (!outcome.Success)
            {
                var statusCode = outcome.ErrorCode switch
                {
                    "closed" or DiscussionApplicationOwner.ErrorCode => 409,
                    "invalid_images" or "invalid_image" or "unsupported_image_type" or "missing_content" => 400,
                    "image_attachments_not_supported" or "file_attachments_not_supported" => 422,
                    "attachment_not_found" or "attachment_expired" or "attachment_forbidden" or "invalid_attachment" or "attachment_limit_exceeded" => 422,
                    ComputeMaintenanceException.ErrorCode => 503,
                    _ => 502,
                };
                return Results.Json(new
                {
                    error = outcome.ErrorCode ?? "delivery_failed",
                    message = outcome.ErrorMessage ?? "The message could not be delivered",
                }, statusCode: statusCode);
            }

            if (outcome.Disposition == "delivered")
            {
                _ = activity.OnUserMessage(id, discussion.Title,
                    string.IsNullOrWhiteSpace(request.Content)
                        ? request.Input is { Length: > 0 } ? "[attachment]" : "[image]"
                        : request.Content,
                    ctx.User.FindFirstValue("name"), discussion.Confidential);

                // Only a provider-accepted turn is a transcript message. Durable
                // admission while another turn runs is represented by the shared
                // queue ghost and converges through session.input-queue.updated.
                try
                {
                    await events.PublishAsync(
                        discussion.Confidential ? "discussion.changed" : "discussion.user-message",
                        discussion.Confidential
                            ? new JsonObject
                            {
                                ["discussionId"] = id,
                                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                                ["confidential"] = true,
                            }
                            : new JsonObject
                            {
                                ["discussionId"] = id,
                                ["sessionId"] = outcome.SessionId,
                                ["messageUid"] = outcome.MessageUid,
                            });
                }
                catch { /* best-effort convergence; reconnect/reselection revalidates */ }
            }

            return Results.Json(new
            {
                success = true,
                accepted = true,
                sessionId = outcome.SessionId,
                disposition = outcome.Disposition,
                queueItemId = outcome.QueueItemId,
                metadata = outcome.Metadata,
                messageUid = outcome.MessageUid,
                queue = outcome.Queue,
                item = outcome.Item,
            }, statusCode: outcome.Disposition == "delivered" ? 200 : 202);
        });

        // Discussion-scoped facade over RedCompute's canonical durable queue. The
        // browser never needs to discover or retain the backing session id, while
        // agents get the same inspect/cancel/retry/send-now control surface.
        group.MapGet("/discussions/{id}/input-queue", async (
            string id, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute) =>
            await ProxyInputQueueAsync(id, "" + ctx.Request.QueryString,
                HttpMethod.Get, ctx, store, redCompute, emptyWhenSessionless: true));

        group.MapGet("/discussions/{id}/input-queue/{itemId}", async (
            string id, string itemId, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute) =>
            await ProxyInputQueueAsync(id, $"/{Uri.EscapeDataString(itemId)}",
                HttpMethod.Get, ctx, store, redCompute));

        group.MapDelete("/discussions/{id}/input-queue/{itemId}", async (
            string id, string itemId, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute) =>
            await ProxyInputQueueAsync(id, $"/{Uri.EscapeDataString(itemId)}",
                HttpMethod.Delete, ctx, store, redCompute));

        group.MapPost("/discussions/{id}/input-queue/{itemId}/retry", async (
            string id, string itemId, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute) =>
            await ProxyInputQueueAsync(id, $"/{Uri.EscapeDataString(itemId)}/retry",
                HttpMethod.Post, ctx, store, redCompute));

        group.MapPost("/discussions/{id}/input-queue/send-now", async (
            string id, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute) =>
            await ProxyInputQueueAsync(id, "/send-now",
                HttpMethod.Post, ctx, store, redCompute));

        // ── Reactions ──────────────────────────────────────────────────

        group.MapPost("/discussions/{id}/reactions", async (string id, ReactionRequest request, HttpContext ctx, DiscussionStore store, IDiscussions discussions, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, DeviceResolver devices, ReactionTriggers triggers, ILogger<ReactionTriggers> logger) =>
            await SetReactionAsync(id, request, ctx, store, discussions, events, devices, triggers, logger, remove: false));

        group.MapPost("/discussions/{id}/reactions/remove", async (string id, ReactionRequest request, HttpContext ctx, DiscussionStore store, IDiscussions discussions, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, DeviceResolver devices, ReactionTriggers triggers, ILogger<ReactionTriggers> logger) =>
            await SetReactionAsync(id, request, ctx, store, discussions, events, devices, triggers, logger, remove: true));

        group.MapGet("/discussions/{id}/reactions", async (string id, HttpContext ctx, DiscussionStore store, IDiscussions discussions) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            var userId = UserId(ctx);
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            var records = await discussions.GetReactionsAsync(discussion.EntityId);
            var reactions = AggregateReactions(records, userId);
            return Results.Ok(new { reactions });
        });

        group.MapGet("/event-types", async ([FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            var items = await entities.QueryAsync(new EntityQuery { TypeSlug = "event-type", Limit = 1000 });
            var colors = await entities.QueryAsync(new EntityQuery { TypeSlug = "color", Limit = 1000 });

            static string? StringValue(LeafEntity entity, string key)
                => entity.Data[key] is JsonValue value && value.TryGetValue<string>(out var text)
                    ? text
                    : null;

            var colorByReference = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var color in colors)
            {
                var hex = StringValue(color, "hex");
                if (string.IsNullOrWhiteSpace(hex)) continue;
                colorByReference[color.Id.ToString()] = hex;
                colorByReference[color.Slug] = hex;
            }

            string? ResolveColor(LeafEntity entity)
            {
                var reference = StringValue(entity, "color");
                if (string.IsNullOrWhiteSpace(reference)) return null;
                return colorByReference.TryGetValue(reference, out var hex) ? hex : reference;
            }

            // Historical API-created event types can coexist with their canonical
            // system seed. Prefer event-type-{key}, then system ownership, so callers
            // always receive one stable definition for a source.
            var types = items
                .Select(item => new { Item = item, Key = StringValue(item, "key") })
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                .GroupBy(entry => entry.Key!, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(entry => string.Equals(
                        entry.Item.Slug,
                        $"event-type-{group.Key}",
                        StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(entry => string.Equals(
                        entry.Item.CreatedBy,
                        "system",
                        StringComparison.OrdinalIgnoreCase))
                    .ThenBy(entry => entry.Item.CreatedAt)
                    .First())
                .Select(entry => new
                {
                    key = entry.Key,
                    name = entry.Item.Name,
                    icon = StringValue(entry.Item, "icon"),
                    color = ResolveColor(entry.Item),
                    description = StringValue(entry.Item, "description"),
                });
            return Results.Ok(types);
        });
    }

    internal static bool MatchesSearchTypeScope(
        DiscussionRead discussion,
        bool? includeLive,
        bool? includeHeartbeats)
    {
        // Search historically covered every discussion type. Preserve that contract
        // for callers such as Ctrl+Shift+F unless a type scope is explicitly supplied.
        if (includeLive is null && includeHeartbeats is null)
            return true;

        return discussion.Type switch
        {
            "live" => includeLive is not false,
            HeartbeatService.DiscussionType => includeHeartbeats is true,
            _ => true,
        };
    }

    private static async Task<IResult> ProxyInputQueueAsync(
        string discussionId, string suffix, HttpMethod method, HttpContext ctx,
        DiscussionStore store, RedComputeClient redCompute, bool emptyWhenSessionless = false)
    {
        var discussion = await store.GetAsync(discussionId, ctx.RequestAborted);
        if (discussion is null) return NotFound();
        var userId = UserId(ctx);
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
        if (discussion.SessionId is null)
        {
            if (emptyWhenSessionless)
                return Results.Ok(new
                {
                    items = Array.Empty<object>(),
                    queue = new
                    {
                        depth = 0,
                        state = "empty",
                        blockedReason = (string?)null,
                        headItemId = (string?)null,
                        errorCode = (string?)null,
                    },
                });
            return Results.Json(new
            {
                error = "session_not_started",
                message = "This discussion has no RedCompute session yet",
            }, statusCode: 409);
        }

        ComputeProvenance? provenance = null;
        if (method != HttpMethod.Get && method != HttpMethod.Head)
        {
            var agents = ctx.RequestServices.GetRequiredService<AgentDirectory>();
            var agent = discussion.AgentId is null
                ? null
                : await agents.GetAgentAsync(discussion.AgentId, ctx.RequestAborted);
            if (agent is null)
                return Results.Json(new
                {
                    error = "missing_agent",
                    message = "The discussion has no linked Agent entity",
                }, statusCode: 422);
            var entities = ctx.RequestServices.GetRequiredKeyedService<IEntityStore>(NovaAppPlugin.PluginId);
            var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(
                entities, discussion.OwnerId, ctx.RequestAborted);
            provenance = await NovaComputeProvenance.CreateAsync(
                entities, agent, beneficiary,
                $"/api/apps/nova/discussions/{discussionId}/input-queue{suffix}",
                [new ComputeContextReference("discussion", discussionId),
                 new ComputeContextReference("session", discussion.SessionId)],
                method: method.Method, ct: ctx.RequestAborted);
        }

        var result = await redCompute.ProxyInputQueueAsync(
            discussion.SessionId, method, suffix, provenance, ctx.RequestAborted);
        return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);
    }

    /// <summary>
    /// Who is reacting. A signed Agent execution is that Agent, whatever the body says, so an Agent
    /// needs no hand-passed id. Without one, a body agentId still names the Agent (older recipes).
    /// A person is the caller. Anything else signed (an automation, a derived worker) is recorded
    /// as itself, never as the person, so it cannot pass for them or trigger a turn.
    /// Removes resolve the same way: the aggregation key is (messageKey, emoji, actorId), so an
    /// Agent's remove attributed to the user would never cancel its add.
    /// </summary>
    internal static (string Type, string Id, string Name) ResolveReactionActor(
        ReactionRequest request, ClaimsPrincipal user, string? userId)
    {
        var signed = DiscussionAccessPolicy.ReadSignedActor(user);
        if (signed is { IsAgent: true })
            return ("agent", signed.Id, signed.Name ?? request.AgentName ?? "Agent");
        if (!string.IsNullOrEmpty(request.AgentId))
            return ("agent", request.AgentId, request.AgentName ?? "Agent");
        if (signed is null || DiscussionAccessPolicy.IsHumanInitiated(user))
            return ("user", userId ?? "anonymous", user.FindFirstValue("name") ?? "User");
        return ("automation", signed.Id, signed.Name ?? "Automation");
    }

    private static async Task<IResult> SetReactionAsync(string id, ReactionRequest request, HttpContext ctx,
        DiscussionStore store, IDiscussions discussions, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events,
        DeviceResolver devices, ReactionTriggers triggers, ILogger logger, bool remove)
    {
        var discussion = await store.GetAsync(id);
        if (discussion is null) return NotFound();
        var userId = UserId(ctx);
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

        if (string.IsNullOrWhiteSpace(request.Emoji) || string.IsNullOrWhiteSpace(request.MessageKey))
            return Results.BadRequest(new { error = "Emoji and messageKey are required" });

        var (actorType, actorId, actorName) = ResolveReactionActor(request, ctx.User, userId);
        var humanInitiated = actorType == "user" && DiscussionAccessPolicy.IsHumanInitiated(ctx.User);

        await discussions.SetReactionAsync(discussion.EntityId,
            new ReactionChange(request.MessageKey!, request.Emoji.Trim(), remove, actorType, actorId, actorName));

        // Steering or starting a turn reads RedCompute and may wait out a debounce: never hold the
        // response for it, and never let the work inherit this request (its HTTP context and
        // execution scope end with it). The send carries its own Agent provenance.
        if (actorType == "user" && humanInitiated)
        {
            var device = await devices.ResolveAsync(ctx.Request.Headers.UserAgent.ToString(), null);
            var signal = new ReactionSignal(discussion, new ReactionEntry(0, request.MessageKey!,
                request.Emoji.Trim(), remove, actorType, actorId, actorName, DateTimeOffset.UtcNow), true, device);
            using (ExecutionContext.SuppressFlow())
                _ = Task.Run(async () =>
                {
                    try { await triggers.OnReactionAsync(signal); }
                    catch (Exception ex) { logger.LogWarning(ex, "Reaction trigger for discussion {DiscussionId} failed", id); }
                });
        }

        await events.PublishAsync("discussion.reaction", new JsonObject
        {
            ["discussionId"] = id,
            ["messageKey"] = request.MessageKey,
            ["emoji"] = request.Emoji.Trim(),
            ["action"] = remove ? "remove" : "add",
            ["actorId"] = actorId,
            ["actorName"] = actorName,
            ["actorType"] = actorType,
        });

        return Results.Ok(new { success = true });
    }

    private static Dictionary<string, object[]> AggregateReactions(IReadOnlyList<LeafRecord> records, string? currentUserId)
    {
        var state = new Dictionary<(string msgKey, string emoji, string actorId), (string name, string type)>();

        foreach (var rec in records)
        {
            var d = rec.Data;
            var msgKey = d["message_key"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(msgKey))
            {
                // backward compat: fall back to message_id
                var mid = d["message_id"];
                msgKey = mid is JsonValue v
                    ? (v.TryGetValue<long>(out var l) ? l.ToString() : v.TryGetValue<string>(out var ms) ? ms ?? "" : "")
                    : "";
            }
            var emoji = d["emoji"]?.GetValue<string>() ?? "";
            var action = d["action"]?.GetValue<string>() ?? "add";
            var actorId = d["actor_id"]?.GetValue<string>() ?? "";
            var actorName = d["actor_name"]?.GetValue<string>() ?? "";
            var actorType = d["actor_type"]?.GetValue<string>() ?? "user";

            var key = (msgKey, emoji, actorId);
            if (action == "add")
                state[key] = (actorName, actorType);
            else
                state.Remove(key);
        }

        var result = new Dictionary<string, object[]>();
        foreach (var g in state.GroupBy(kv => kv.Key.msgKey))
        {
            result[g.Key] = g
                .GroupBy(kv => kv.Key.emoji)
                .Select(eg => (object)new
                {
                    emoji = eg.Key,
                    count = eg.Count(),
                    actors = eg.Select(kv => new { id = kv.Key.actorId, name = kv.Value.name, type = kv.Value.type }).ToArray(),
                    userReacted = eg.Any(kv => kv.Key.actorId == currentUserId),
                })
                .ToArray();
        }
        return result;
    }

    private static IResult HistoryError(int status, string error, string message)
        => Results.Json(new { error, message }, statusCode: status);

    /// <summary>The page a discussion without an AI session yet reports: nothing to list, not an error.</summary>
    internal static readonly string EmptyArtifactsPageJson =
        """{"sessionId":null,"epoch":null,"throughSequence":0,"extractorVersion":null,"items":[],"page":{"nextCursor":null}}""";

    internal static async Task<IResult> GetArtifactsAsync(
        string id,
        HttpContext ctx,
        DiscussionStore store,
        RedComputeClient redCompute)
    {
        // Same resolution and access checks as history-page, confidential included.
        var discussion = await store.GetAsync(id);
        if (discussion is null) return NotFound();
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
        if (string.IsNullOrWhiteSpace(discussion.SessionId))
            return Results.Content(EmptyArtifactsPageJson, "application/json");

        var query = ctx.Request.Query;
        RedComputeClient.ProxyResult result;
        try
        {
            result = await redCompute.GetSessionArtifactsAsync(
                discussion.SessionId,
                query["kinds"].FirstOrDefault(),
                query["origins"].FirstOrDefault(),
                query["sinceSequence"].FirstOrDefault(),
                query["limit"].FirstOrDefault(),
                query["cursor"].FirstOrDefault(),
                ctx.RequestAborted);
        }
        catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            return HistoryError(503, "redcompute_unavailable", ex.Message);
        }

        if (result.StatusCode == 200)
        {
            var rewritten = RewriteArtifactTargets(result.Content, discussion.Id, discussion.SessionId);
            return rewritten is null
                ? HistoryError(502, "invalid_artifacts_page", "RedCompute returned an invalid artifacts page")
                : Results.Content(rewritten, "application/json");
        }

        var upstreamError = ReadErrorCode(result.Content);
        // A 404 that names the session is a real refusal. Any other 404 is a RedCompute that does
        // not serve the route yet; an empty panel would pass that off as "nothing was shared".
        if (result.StatusCode == 404 && upstreamError != "not_found")
            return HistoryError(503, "artifacts_unavailable", "RedCompute does not provide the artifacts endpoint yet");

        if (result.StatusCode is 400 or 403 or 404 or 409 or 413 or 503)
            return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);

        return HistoryError(502, "redcompute_error", $"RedCompute answered the artifacts read with HTTP {result.StatusCode}");
    }

    private static readonly System.Text.RegularExpressions.Regex SessionOutputTarget = new(
        @"^/ai-session/sessions/(?<session>[^/?#]+)/messages/(?<record>\d+)/output(?<rest>[?#].*)?$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// RedCompute addresses an assistant image by its own session route, which the browser cannot
    /// call. Nova serves the same bytes behind the discussion's access check, so those targets move
    /// to that route; every other field and target passes through untouched. Null when the body is
    /// not an artifacts page.
    /// </summary>
    internal static string? RewriteArtifactTargets(string content, string discussionId, string sessionId)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(content); }
        catch (JsonException) { return null; }
        if (root is not JsonObject page || page["items"] is not JsonArray items) return null;

        foreach (var item in items)
        {
            if (item is not JsonObject artifact
                || artifact["target"] is not JsonValue targetValue
                || !targetValue.TryGetValue<string>(out var target))
                continue;
            var match = SessionOutputTarget.Match(target);
            if (!match.Success || Uri.UnescapeDataString(match.Groups["session"].Value) != sessionId)
                continue;
            artifact["target"] =
                $"/api/apps/nova/discussions/{Uri.EscapeDataString(discussionId)}/messages/{match.Groups["record"].Value}/output{match.Groups["rest"].Value}";
        }
        return root.ToJsonString();
    }

    private static string? ReadErrorCode(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<OverlayPageRead> ReadOverlayPageAsync(
        IDiscussions discussions,
        DiscussionRead discussion,
        SessionTranscriptPage? transcript,
        HistoryPageCursor? cursor,
        string direction,
        int limit,
        CancellationToken ct)
    {
        var canonical = transcript?.Messages ?? [];
        if (discussion.SessionId is null || canonical.Count == 0 || direction == "after")
        {
            var direct = await discussions.GetMessagePageAsync(
                discussion.EntityId,
                new DiscussionMessagePageRequest(
                    Limit: limit,
                    BeforeId: direction == "before" ? cursor?.OverlayAnchorId : null,
                    AfterId: direction == "after"
                        ? ResolveOverlayAfterAnchor(cursor)
                        : null,
                    FromInclusive: direction == "after"
                        && cursor is { OverlayAnchorId: null, Boundary: { } boundary }
                            ? new DiscussionMessagePageBoundary(boundary.Timestamp, boundary.Id)
                            : null),
                ct);
            return new(
                direct.Messages,
                direct.HasEarlier,
                direct.HasLater,
                direct.OldestId,
                direct.NewestId);
        }

        var lower = ToSdkBoundary(canonical[0]);
        var upper = direction == "before" && cursor?.Boundary is { } priorBoundary
            ? new DiscussionMessagePageBoundary(priorBoundary.Timestamp, priorBoundary.Id)
            : null;
        var messages = await ReadCompleteOverlayIntervalAsync(
            discussions,
            discussion.EntityId,
            lower,
            upper,
            ct);

        var hasEarlier = transcript?.Page.HasEarlier ?? false;
        if (!hasEarlier)
        {
            hasEarlier = await HasVisibleOverlayBeforeAsync(
                discussions,
                discussion.EntityId,
                lower,
                sessionBacked: true,
                ct);
        }

        return new(
            messages,
            hasEarlier,
            HasLater: transcript?.Page.HasLater ?? direction == "before",
            messages.Count > 0 ? messages[0].Id : null,
            messages.Count > 0 ? messages[^1].Id : null);
    }

    internal static async Task<IReadOnlyList<DiscussionMessage>> ReadCompleteOverlayIntervalAsync(
        IDiscussions discussions,
        Guid discussionId,
        DiscussionMessagePageBoundary fromInclusive,
        DiscussionMessagePageBoundary? toExclusive,
        CancellationToken ct)
    {
        const int storagePageSize = 1000;
        const int maxAlignedOverlayRecords = 10_000;
        var result = new List<DiscussionMessage>();
        long? beforeId = null;

        while (true)
        {
            var page = await discussions.GetMessagePageAsync(
                discussionId,
                new DiscussionMessagePageRequest(
                    Limit: storagePageSize,
                    BeforeId: beforeId,
                    FromInclusive: fromInclusive,
                    ToExclusive: toExclusive),
                ct);
            if (page.Messages.Count == 0)
                break;

            result.InsertRange(0, page.Messages);
            if (result.Count > maxAlignedOverlayRecords)
                throw new HistoryOverlayPageTooLargeException();
            if (!page.HasEarlier || page.OldestId is null)
                break;
            beforeId = page.OldestId;
        }

        return result;
    }

    private static async Task<bool> HasVisibleOverlayBeforeAsync(
        IDiscussions discussions,
        Guid discussionId,
        DiscussionMessagePageBoundary boundary,
        bool sessionBacked,
        CancellationToken ct)
    {
        long? beforeId = null;
        while (true)
        {
            var page = await discussions.GetMessagePageAsync(
                discussionId,
                new DiscussionMessagePageRequest(
                    Limit: 1000,
                    BeforeId: beforeId,
                    ToExclusive: boundary),
                ct);
            if (page.Messages.Any(message => IsHistoryOverlaySourceVisible(
                message.Metadata["source"]?.GetValue<string>(),
                sessionBacked,
                allowUserBridges: false)))
                return true;
            if (!page.HasEarlier || page.OldestId is null)
                return false;
            beforeId = page.OldestId;
        }
    }

    private static DiscussionMessagePageBoundary ToSdkBoundary(SessionMessage message)
        => new(message.RecordCreatedAt!.Value, message.Id);

    internal static List<SessionMessage> ProjectCanonicalHistoryMessages(
        IReadOnlyList<SessionMessage> messages)
        => messages.Select(message =>
        {
            if (!string.Equals(message.Role, "user", StringComparison.Ordinal)
                || !string.Equals(message.EventType, "text", StringComparison.Ordinal))
                return message;

            return new SessionMessage
            {
                Id = message.Id,
                SessionId = message.SessionId,
                Role = message.Role,
                EventType = message.EventType,
                Content = ConversationExporter.StripInjectedTags(message.Content ?? ""),
                ToolName = message.ToolName,
                ToolInput = message.ToolInput,
                ToolResult = message.ToolResult,
                PayloadRef = message.PayloadRef,
                Phase = message.Phase,
                MessageId = message.MessageId,
                Epoch = message.Epoch,
                Sequence = message.Sequence,
                Timestamp = message.Timestamp,
                RecordCreatedAt = message.RecordCreatedAt,
                AttachmentsJson = message.AttachmentsJson,
                MessageUid = message.MessageUid,
                ParentToolUseId = message.ParentToolUseId,
                TurnOrigin = message.TurnOrigin,
            };
        }).ToList();

    private static HistoryPageBoundary? BoundaryAt(IReadOnlyList<SessionMessage> messages, bool first)
    {
        if (messages.Count == 0) return null;
        var message = first ? messages[0] : messages[^1];
        return message.RecordCreatedAt is { } createdAt
            ? new HistoryPageBoundary(createdAt, message.Id)
            : null;
    }

    private static HistoryPageBoundary? BoundaryAt(IReadOnlyList<DiscussionMessage> messages, bool first)
    {
        if (messages.Count == 0) return null;
        var message = first ? messages[0] : messages[^1];
        return new HistoryPageBoundary(message.CreatedAt, message.Id);
    }

    private static HistoryPageBoundary? MaxBoundary(
        HistoryPageBoundary? left,
        HistoryPageBoundary? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        var timestamp = left.Timestamp.CompareTo(right.Timestamp);
        return timestamp > 0 || (timestamp == 0 && left.Id >= right.Id) ? left : right;
    }

    internal static string? ResolveComputeCursor(
        SessionTranscriptPage? transcript,
        HistoryPageCursor? previous,
        bool oldest,
        bool preservePreviousOnEmpty = false)
        => transcript is null
            ? previous?.ComputeCursor
            : preservePreviousOnEmpty && transcript.Messages.Count == 0
                ? previous?.ComputeCursor
            : oldest ? transcript.Page.OldestCursor : transcript.Page.NewestCursor;

    internal static bool RequiresCanonicalBackfillReset(
        string direction,
        HistoryPageCursor? previous,
        SessionTranscriptPage? transcript)
        => direction == "after"
            && previous is { ComputeCursor: null }
            && transcript is { Messages.Count: > 0 }
            && transcript.Page.HasEarlier;

    internal static long? ResolveNewestOverlayAnchor(
        string direction,
        OverlayPageRead overlayPage,
        HistoryPageCursor? previous)
    {
        // Compute and Nova overlays advance independently. The overlay keyset may
        // advance only to an overlay row actually consumed, never to a later
        // canonical record id. When no overlay has existed yet, the cursor's
        // storage-time boundary keeps a later after-read from replaying old rows.
        return overlayPage.NewestId
            ?? (direction == "after" ? previous?.OverlayAnchorId : null);
    }

    internal static long? ResolveOverlayAfterAnchor(HistoryPageCursor? cursor)
        => cursor?.OverlayAnchorId ?? cursor?.Boundary?.Id;

    internal static bool ShouldEmitHistoryCursor(
        string direction,
        HistoryPageCursorEdge edge,
        bool pageHasRecords,
        bool hasEarlier,
        bool hasLater)
    {
        if (direction == "before" && edge == HistoryPageCursorEdge.Oldest
            && !pageHasRecords && !hasEarlier)
            return false;
        if (direction == "after" && edge == HistoryPageCursorEdge.Newest
            && !pageHasRecords && !hasLater)
            return false;
        return true;
    }

    internal static bool IsHistoryOverlaySourceVisible(
        string? source,
        bool sessionBacked,
        bool allowUserBridges)
    {
        if (source == "queued-user-message") return false;
        if (!sessionBacked) return true;
        if (source?.StartsWith("event:", StringComparison.Ordinal) == true) return true;
        if (source == "nova-message") return true;
        return allowUserBridges && source == "user-message";
    }

    internal static HistoryOverlayDto[] ProjectHistoryOverlays(
        IReadOnlyList<DiscussionMessage> records,
        IReadOnlySet<string> canonicalUserUids,
        IReadOnlySet<string> canonicalAssistantUids,
        bool sessionBacked,
        bool allowUserBridges)
    {
        var selected = new Dictionary<string, DiscussionMessage>(StringComparer.Ordinal);
        foreach (var message in records)
        {
            var source = message.Metadata["source"]?.GetValue<string>();
            if (!IsHistoryOverlaySourceVisible(source, sessionBacked, allowUserBridges))
                continue;

            var uid = message.Metadata["uid"]?.GetValue<string>();
            if (source == "user-message")
            {
                if (string.IsNullOrWhiteSpace(uid) || canonicalUserUids.Contains(uid))
                    continue;
            }
            else if (source == "nova-message"
                && !string.IsNullOrWhiteSpace(uid)
                && canonicalAssistantUids.Contains(uid))
            {
                continue;
            }

            // User and Nova bridge records are last-write-wins under their stable
            // UID. Ambient events remain distinct durable records.
            var key = source is "user-message" or "nova-message"
                && !string.IsNullOrWhiteSpace(uid)
                    ? $"{source}:{uid}"
                    : $"record:{message.Id}";
            if (!selected.TryGetValue(key, out var existing) || existing.Id < message.Id)
                selected[key] = message;
        }

        return selected.Values
            .OrderBy(message => message.CreatedAt)
            .ThenBy(message => message.Id)
            .Select(message =>
            {
                var source = message.Metadata["source"]?.GetValue<string>() ?? "";
                var timestamp = message.CreatedAt;
                if (source.StartsWith("event:", StringComparison.Ordinal)
                    && message.Metadata["timestamp"]?.GetValue<string>() is { } rawTimestamp
                    && DateTimeOffset.TryParse(rawTimestamp, out var eventTimestamp))
                    timestamp = eventTimestamp;
                return new HistoryOverlayDto(
                    message.Id.ToString(),
                    message.Metadata["uid"]?.GetValue<string>(),
                    message.Role,
                    source == "user-message"
                        ? MapUserMessageParts(message.Metadata["parts_json"]?.GetValue<string>(), message.Content)
                        : MapParts(message.Metadata["parts_json"]?.GetValue<string>(), message.Content),
                    timestamp.UtcDateTime.ToString("o"),
                    message.Metadata["sender_agent_id"]?.GetValue<string>(),
                    source);
            })
            .ToArray();
    }

    internal static object[] MapUserMessageParts(string? partsJson, string content)
    {
        if (string.IsNullOrEmpty(partsJson))
            return MapParts(null, content);

        var attachments = MapParts(partsJson, "");
        if (string.IsNullOrWhiteSpace(content))
            return attachments;

        return
        [
            new { type = (string?)"text", content = (string?)content, toolName = (string?)null, toolInput = (string?)null },
            .. attachments,
        ];
    }

    private static bool IsAcceptedUserMessageSource(string? source)
        => source is "user-message" or "queued-user-message";

    internal static bool IsVisibleSessionMessage(ConversationExporter.CollapsedMessage message)
        => message.EventType switch
        {
            // A silent settle is no message (the paged V2 path keeps its record; the client hides it).
            "text" => !string.IsNullOrWhiteSpace(message.Content) && !NovaNoReply.IsMarker(message.Content),
            "tool_use" => !string.IsNullOrWhiteSpace(message.ToolName),
            "tool_result" => !string.IsNullOrWhiteSpace(message.ToolResult)
                || !string.IsNullOrWhiteSpace(message.Content)
                || message.PayloadRef is not null,
            _ => false,
        };

    internal static object[] MapSessionMessageParts(
        ConversationExporter.CollapsedMessage message,
        string content)
        => message.EventType switch
        {
            "tool_use" =>
            [
                new
                {
                    type = (string?)"tool_use",
                    content = (string?)"",
                    toolName = message.ToolName,
                    toolInput = message.ToolInput,
                    payloadRef = (JsonElement?)null,
                },
            ],
            "tool_result" =>
            [
                new
                {
                    type = (string?)"tool_result",
                    content = (string?)content,
                    toolName = message.ToolName,
                    toolInput = message.ToolInput,
                    payloadRef = message.PayloadRef,
                },
            ],
            "text" =>
            [
                new
                {
                    type = (string?)"text",
                    content = (string?)content,
                    toolName = (string?)null,
                    toolInput = (string?)null,
                    payloadRef = (JsonElement?)null,
                    phase = message.Phase,
                },
            ],
            _ => MapParts(null, content),
        };

    internal static HashSet<string> FindPendingUserMessageUids(
        IEnumerable<string?> sessionUserUids,
        IEnumerable<string?> persistedUserUids)
    {
        var mirrored = sessionUserUids
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .Select(uid => uid!)
            .ToHashSet(StringComparer.Ordinal);

        return persistedUserUids
            .Where(uid => !string.IsNullOrWhiteSpace(uid))
            .Select(uid => uid!)
            .Where(uid => !mirrored.Contains(uid))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static object[] MapParts(string? partsJson, string content)
    {
        if (!string.IsNullOrEmpty(partsJson))
        {
            try
            {
                return JsonSerializer.Deserialize<JsonElement[]>(partsJson)!
                    .Select(p =>
                    {
                        var type = p.TryGetProperty("type", out var t) ? t.GetString() : "text";

                        // Event metadata parts store their payload under source/data —
                        // reproject into the DTO shape so the client can read it.
                        if (type == "event_data")
                        {
                            return (object)new
                            {
                                type,
                                content = p.TryGetProperty("data", out var d) ? d.GetRawText() : "",
                                toolName = p.TryGetProperty("source", out var s) ? s.GetString() : null,
                                toolInput = (string?)null,
                            };
                        }

                        if (type == "image")
                        {
                            var url = p.TryGetProperty("url", out var u) ? u.GetString() : null;
                            var assetId = p.TryGetProperty("assetId", out var a) ? a.GetString() : null;
                            var base64 = p.TryGetProperty("base64", out var b) ? b.GetString() : null;
                            var mediaType = p.TryGetProperty("mediaType", out var mt) ? mt.GetString() : "image/png";

                            return (object)new
                            {
                                type,
                                url = url ?? (assetId != null ? $"/api/assets/{assetId}" : null),
                                base64,
                                mediaType,
                            };
                        }

                        if (type == "attachment")
                        {
                            return (object)new
                            {
                                type = "text",
                                content = "",
                                toolName = (string?)null,
                                toolInput = (string?)null,
                                attachments = new[]
                                {
                                    new
                                    {
                                        id = p.TryGetProperty("id", out var id) ? id.GetString() : null,
                                        kind = p.TryGetProperty("kind", out var kind) ? kind.GetString() : "file",
                                        name = p.TryGetProperty("name", out var name) ? name.GetString() : "attachment",
                                        mediaType = p.TryGetProperty("mediaType", out var mediaType) ? mediaType.GetString() : "application/octet-stream",
                                        size = p.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                                        sha256 = p.TryGetProperty("sha256", out var sha) ? sha.GetString() : null,
                                        downloadUrl = p.TryGetProperty("downloadUrl", out var download) ? download.GetString() : null,
                                    },
                                },
                            };
                        }

                        return (object)new
                        {
                            type,
                            content = p.TryGetProperty("content", out var c) ? c.GetString() : "",
                            toolName = p.TryGetProperty("toolName", out var tn) ? tn.GetString() : null,
                            toolInput = p.TryGetProperty("toolInput", out var ti) ? ti.GetString() : null,
                        };
                    })
                    .ToArray();
            }
            catch { /* fall through to the plain-text shape */ }
        }
        return [new { type = (string?)"text", content = (string?)content, toolName = (string?)null, toolInput = (string?)null }];
    }

}
