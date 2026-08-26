using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
}

public class ReactionRequest
{
    public string Emoji { get; set; } = "";
    public string? MessageKey { get; set; }
    public string? AgentId { get; set; }
    public string? AgentName { get; set; }
}

public static class DiscussionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string? UserId(HttpContext ctx) => ctx.User.FindFirstValue("sub");

    private static IResult Forbidden() => Results.Json(new { error = "Forbidden" }, statusCode: 403);
    private static IResult NotFound() => Results.NotFound(new { error = "Discussion not found" });
    private static IResult AccessDenied(DiscussionRead discussion)
        => discussion.Confidential ? NotFound() : Forbidden();

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/discussions", async (HttpContext ctx, DiscussionStore store) =>
        {
            var status = ctx.Request.Query["status"].FirstOrDefault();
            var search = ctx.Request.Query["search"].FirstOrDefault();
            var agentFilter = ctx.Request.Query["agent"].FirstOrDefault();

            var discussions = await store.ListAsync(agentFilter);

            IEnumerable<DiscussionRead> filtered = discussions;
            filtered = !string.IsNullOrEmpty(status)
                ? filtered.Where(d => d.Status == status)
                : filtered.Where(d => !DiscussionStatus.IsClosed(d.Status));

            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(d => d.Title != null && d.Title.Contains(search, StringComparison.OrdinalIgnoreCase));

            filtered = filtered.Where(d => DiscussionAccessPolicy.CanRead(d, ctx));

            return Results.Ok(filtered.Select(DiscussionStore.ToInfo));
        });

        group.MapGet("/discussions/pending", async (HttpContext ctx, DiscussionStore store) =>
        {
            var discussions = (await store.ListAsync())
                .Where(d => !DiscussionStatus.IsClosed(d.Status))
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
            var discussions = (await store.ListAsync())
                .Where(d => !DiscussionStatus.IsClosed(d.Status) && d.SessionId != null)
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
                var existing = (await store.ListAsync()).Any(d => d.Type == type && d.AgentId == agentId && !DiscussionStatus.IsClosed(d.Status));
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
            var live = (await store.ListAsync(agentFilter))
                .Where(d => d.Type == "live" && !DiscussionStatus.IsClosed(d.Status)
                    && DiscussionAccessPolicy.CanRead(d, ctx))
                .Select(DiscussionStore.ToInfo);
            return Results.Ok(live);
        });

        group.MapGet("/discussions/search", async (HttpContext ctx, DiscussionStore store, ISearch search) =>
        {
            var q = ctx.Request.Query["q"].FirstOrDefault();
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
            var result = await search.SearchConversationsAsync(new ConversationSearchQuery
            {
                Query = q,
                Streams = ["nova-messages", "session-messages"],
                Limit = 100,
                SnippetsPerConversation = 3,
            });

            var accessible = (await store.ListAsync())
                .Where(d => DiscussionAccessPolicy.CanRead(d, ctx))
                .ToList();
            var byEntityId = accessible.ToDictionary(d => d.EntityId);
            var bySessionId = new Dictionary<string, DiscussionRead>();
            foreach (var d in accessible)
                if (d.SessionId != null && !bySessionId.ContainsKey(d.SessionId))
                    bySessionId[d.SessionId] = d;

            // Kernel groups arrive best-ranked first; a discussion hit in both streams
            // merges into one result. Non-Nova conversations (other apps' sessions or
            // discussions) simply don't resolve and are skipped.
            var merged = new Dictionary<string, (DiscussionRead Discussion, int MatchCount, List<ConversationSearchHit> Hits)>();
            foreach (var g in result.Groups)
            {
                DiscussionRead? disc = null;
                if (g.EntityId is { } eid && byEntityId.TryGetValue(eid, out var direct))
                    disc = direct;
                else if (g.Entity is { TypeSlug: "ai-session" }
                    && g.Entity.Data["session_id"]?.GetValue<string>() is { } sid
                    && bySessionId.TryGetValue(sid, out var viaSession))
                    disc = viaSession;
                if (disc == null) continue;

                if (merged.TryGetValue(disc.Id, out var existing))
                    merged[disc.Id] = (disc, existing.MatchCount + g.HitCount, [.. existing.Hits, .. g.Hits]);
                else
                    merged[disc.Id] = (disc, g.HitCount, g.Hits.ToList());
            }

            var results = merged.Values
                .Take(limit)
                .Select(m => new
                {
                    discussionId = m.Discussion.Id,
                    title = m.Discussion.Title,
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

            var discussions = (await store.ListAsync())
                .Where(d => d.LastActivity >= since && DiscussionAccessPolicy.CanRead(d, ctx))
                .Where(d => !d.Confidential)
                .Take(limit)
                .ToList();

            var markdown = await exporter.ExportAsync(discussions, since);
            return Results.Text(markdown, "text/markdown");
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
                                    ? MapUserMessageParts(saved.Metadata["parts_json"]?.GetValue<string>(), content)
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
            var all = (await store.ListAsync())
                .Where(d => DiscussionAccessPolicy.CanRead(d, ctx))
                .Where(d => !DiscussionStatus.IsClosed(d.Status) || d.LastActivity >= cutoff)
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

            await store.PatchAsync(discussion.EntityId, new JsonObject { ["title"] = request.Title },
                name: request.Title ?? $"Discussion {id}");
            return Results.Ok(DiscussionStore.ToInfo(discussion with { Title = request.Title }));
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
                var oldSessionId = discussion.SessionId;
                await store.PatchAsync(discussion.EntityId, new JsonObject
                {
                    ["session_id"] = null,
                    ["status"] = DiscussionStatus.Idle,
                });
                await redCompute.DismissAsync(oldSessionId);
                return Results.Ok(new { sessionId = (string?)null, status = DiscussionStatus.Idle, initialized = false });
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

        group.MapDelete("/discussions/{id}", async (string id, HttpContext ctx, DiscussionStore store, DiscussionLifecycle lifecycle, DiscussionActivity activity) =>
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
            var candidates = id == "live" ? await store.ListAsync() : null;
            var discussion = id == "live"
                ? candidates!.FirstOrDefault(d =>
                    d.Type == "live" && !DiscussionStatus.IsClosed(d.Status)
                    && (agents.NovaAgentId == null || d.AgentId == null || d.AgentId == agents.NovaAgentId))
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

        group.MapPost("/discussions/{id}/event", async (string id, DiscussionEventRequest request, HttpContext ctx, DiscussionStore store, AgentDirectory agents, EventInjector injector) =>
        {
            // "live" resolves to the current LIVE discussion — the heartbeat posts
            // its note-events through this form since the LIVE id rotates daily.
            var discussion = id == "live"
                ? (await store.ListAsync()).FirstOrDefault(d =>
                    d.Type == "live" && !DiscussionStatus.IsClosed(d.Status)
                    && (agents.NovaAgentId == null || d.AgentId == null || d.AgentId == agents.NovaAgentId))
                : await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

            await injector.InjectAsync(discussion, request.Content, request.Type, request.Source,
                request.SenderAgentId, request.ReplyToDiscussionId, request.Metadata, UserId(ctx));

            return Results.Ok(new { success = true });
        });

        group.MapPost("/discussions/{id}/nova-message", async (string id, NovaMessageRequest request, HttpContext ctx, DiscussionStore store, ConversationUnread conversationUnread, RedComputeClient redCompute, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, DiscussionActivity activity) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

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
                namePatch = request.Title;
            }
            await store.PatchAsync(discussion.EntityId, patch, namePatch);

            // Best-effort inject into live session (message is already persisted above).
            // If the session isn't ready, SendAsync replays it when the user first
            // messages the discussion.
            if (discussion.SessionId is not null)
            {
                try
                {
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
                    });
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
            return Results.Ok(new { success = true, discussion = DiscussionStore.ToInfo(discussion) });
        });

        group.MapPost("/discussions/{id}/message", async (string id, DiscussionMessageRequest request, HttpContext ctx, DiscussionStore store, MessagePipeline pipeline, DeviceResolver devices, LiveEvents live, DiscussionActivity activity, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            var userId = UserId(ctx);
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

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

            var outcome = request.Input is { Length: > 0 }
                ? await pipeline.SendInputAsync(
                    discussion, userId, request.Input, resolved,
                    request.InputMethod ?? "typed", request.Delivery,
                    idempotencyKey, request.DisplayContent)
                : await pipeline.SendAsync(
                    discussion, userId, request.Content, request.Images, resolved,
                    request.InputMethod ?? "typed", request.Delivery,
                    idempotencyKey, request.DisplayContent);

            if (!outcome.Success)
            {
                var statusCode = outcome.ErrorCode switch
                {
                    "invalid_images" or "invalid_image" or "unsupported_image_type" or "missing_content" => 400,
                    "image_attachments_not_supported" or "file_attachments_not_supported" => 422,
                    "attachment_not_found" or "attachment_expired" or "attachment_forbidden" or "invalid_attachment" or "attachment_limit_exceeded" => 422,
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
                        : request.Content, discussion.Confidential);

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

        group.MapPost("/discussions/{id}/reactions", async (string id, ReactionRequest request, HttpContext ctx, DiscussionStore store, IDiscussions discussions, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
            await SetReactionAsync(id, request, ctx, store, discussions, events, remove: false));

        group.MapPost("/discussions/{id}/reactions/remove", async (string id, ReactionRequest request, HttpContext ctx, DiscussionStore store, IDiscussions discussions, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
            await SetReactionAsync(id, request, ctx, store, discussions, events, remove: true));

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

    private static async Task<IResult> SetReactionAsync(string id, ReactionRequest request, HttpContext ctx,
        DiscussionStore store, IDiscussions discussions, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, bool remove)
    {
        var discussion = await store.GetAsync(id);
        if (discussion is null) return NotFound();
        var userId = UserId(ctx);
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);

        if (string.IsNullOrWhiteSpace(request.Emoji) || string.IsNullOrWhiteSpace(request.MessageKey))
            return Results.BadRequest(new { error = "Emoji and messageKey are required" });

        // Applies to removes too: the aggregation key is (messageKey, emoji,
        // actorId), so attributing an agent's remove to the user would never
        // cancel its add and agent reactions would be permanent.
        var isAgent = !string.IsNullOrEmpty(request.AgentId);
        var actorId = isAgent ? request.AgentId! : userId ?? "anonymous";
        var actorName = isAgent ? (request.AgentName ?? "Agent") : (ctx.User.FindFirstValue("name") ?? "User");
        var actorType = isAgent ? "agent" : "user";

        await discussions.SetReactionAsync(discussion.EntityId,
            new ReactionChange(request.MessageKey!, request.Emoji.Trim(), remove, actorType, actorId, actorName));

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
            "text" => !string.IsNullOrWhiteSpace(message.Content),
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
