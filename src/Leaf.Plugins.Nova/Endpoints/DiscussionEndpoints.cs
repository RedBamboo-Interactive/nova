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
}

public class DiscussionTitleRequest
{
    public string? Title { get; set; }
}

public class DiscussionConfidentialRequest
{
    public bool Confidential { get; set; }
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
}

public class DiscussionMessageRequest
{
    public string Content { get; set; } = "";
    public ImageAttachmentDto[]? Images { get; set; }
    public string? InputMethod { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
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

            var userId = UserId(ctx);
            filtered = filtered.Where(d => OwnerScope.CanAccess(d.OwnerId, userId));

            return Results.Ok(filtered.Select(DiscussionStore.ToInfo));
        });

        group.MapGet("/discussions/pending", async (HttpContext ctx, DiscussionStore store, RedComputeClient redCompute) =>
        {
            var userId = UserId(ctx);
            var discussions = (await store.ListAsync())
                .Where(d => !DiscussionStatus.IsClosed(d.Status) && d.SessionId != null)
                // Heartbeat discussions never count as pending: no unread pill, no
                // badge — the heartbeat is a place you visit, not one that calls you.
                .Where(d => d.Type != HeartbeatService.DiscussionType)
                .Where(d => OwnerScope.CanAccess(d.OwnerId, userId))
                .ToList();

            if (discussions.Count == 0)
                return Results.Ok(new { count = 0 });

            // Side effect kept from the standalone app: refresh cached message counts
            // from RedCompute's session list before counting unread.
            var sessions = await redCompute.GetSessionsAsync();
            if (sessions != null)
            {
                var map = sessions.ToDictionary(s => s.Id);
                for (var i = 0; i < discussions.Count; i++)
                {
                    var d = discussions[i];
                    if (d.SessionId != null && map.TryGetValue(d.SessionId, out var s) && s.MessageCount > d.MessageCount)
                    {
                        await store.PatchAsync(d.EntityId, new JsonObject
                        {
                            ["message_count"] = s.MessageCount,
                            ["last_activity"] = DateTimeOffset.UtcNow.ToString("O"),
                        });
                        discussions[i] = d with { MessageCount = s.MessageCount, LastActivity = DateTime.UtcNow };
                    }
                }
            }

            var count = discussions.Count(d =>
                d.MessageCount > 0 && (d.LastReadAt == null || d.LastActivity > d.LastReadAt));

            return Results.Ok(new { count });
        });

        group.MapPost("/discussions/sync", async (HttpContext ctx, DiscussionStore store, RedComputeClient redCompute) =>
        {
            var userId = UserId(ctx);
            var discussions = (await store.ListAsync())
                .Where(d => !DiscussionStatus.IsClosed(d.Status) && d.SessionId != null)
                .Where(d => OwnerScope.CanAccess(d.OwnerId, userId))
                .ToList();

            if (discussions.Count == 0)
                return Results.Ok(new { synced = 0 });

            var sessions = await redCompute.GetSessionsAsync(50);
            if (sessions == null)
                return Results.Ok(discussions.Select(DiscussionStore.ToInfo)); // RedCompute unreachable — leave as-is

            var statuses = sessions.ToDictionary(s => s.Id, s => s.Status);
            for (var i = 0; i < discussions.Count; i++)
            {
                var d = discussions[i];
                if (d.SessionId == null) continue;
                statuses.TryGetValue(d.SessionId, out var rcStatus);

                // The session list is a recency window, not the full set — a quiet
                // session falling out of it is no evidence it stopped. Probe it
                // directly before declaring a live discussion dead; a null probe
                // (RedCompute unreachable) leaves the status unchanged.
                if (rcStatus == null && d.Status is "idle" or "thinking")
                    rcStatus = await redCompute.GetSessionStatusAsync(d.SessionId);

                var isAlive = rcStatus is "Active" or "Idle" or "Starting";

                string? newStatus = null;
                if (isAlive && d.Status == "stopped") newStatus = "idle";
                else if (!isAlive && rcStatus != null && d.Status is "idle" or "thinking") newStatus = "stopped";

                if (newStatus != null)
                {
                    // State-machine write: refused (null) if the discussion closed
                    // (archiving/archived) while this sweep was probing sessions.
                    var applied = await store.TrySetStatusAsync(d.EntityId, newStatus);
                    if (applied != null)
                        discussions[i] = d with { Status = applied };
                }
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

            var discussion = await store.CreateAsync(null, agentId, UserId(ctx), type,
                createReq?.QualityTier, createReq?.Provider);

            await events.PublishAsync("discussion.created", new JsonObject
            {
                ["discussionId"] = discussion.Id,
                ["agentId"] = discussion.AgentId,
                ["status"] = discussion.Status,
                ["type"] = discussion.Type,
            });

            if (discussion.Type != "live")
                _ = live.PostAsync("discussion", $"New discussion: \"{discussion.Title ?? "untitled"}\"");

            pipeline.BeginSessionCreation(discussion);

            return Results.Ok(DiscussionStore.ToInfo(discussion));
        });

        group.MapGet("/discussions/live", async (HttpContext ctx, DiscussionStore store) =>
        {
            var agentFilter = ctx.Request.Query["agent"].FirstOrDefault();
            var userId = UserId(ctx);
            var live = (await store.ListAsync(agentFilter))
                .Where(d => d.Type == "live" && !DiscussionStatus.IsClosed(d.Status) && OwnerScope.CanAccess(d.OwnerId, userId))
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

            var userId = UserId(ctx);
            var accessible = (await store.ListAsync())
                .Where(d => OwnerScope.CanAccess(d.OwnerId, userId))
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
            var userId = UserId(ctx);
            var since = ctx.Request.Query["since"].FirstOrDefault() is { } s
                ? DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind)
                : DateTime.UtcNow.AddDays(-7);
            var limit = 50;
            if (ctx.Request.Query.TryGetValue("limit", out var lv) && int.TryParse(lv, out var parsed))
                limit = Math.Clamp(parsed, 1, 200);

            var discussions = (await store.ListAsync())
                .Where(d => d.LastActivity >= since && OwnerScope.CanAccess(d.OwnerId, userId))
                .Where(d => !d.Confidential)
                .Take(limit)
                .ToList();

            var markdown = await exporter.ExportAsync(discussions, since);
            return Results.Text(markdown, "text/markdown");
        });

        group.MapGet("/discussions/{id}", async (string id, HttpContext ctx, DiscussionStore store, IDiscussions discussions, RedComputeClient redCompute) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            DateTime? since = null;
            if (ctx.Request.Query["since"].FirstOrDefault() is { } sv)
                since = DateTimeOffset.Parse(sv, null, System.Globalization.DateTimeStyles.RoundtripKind).UtcDateTime;

            int? tail = null;
            if (ctx.Request.Query["tail"].FirstOrDefault() is { } tv && int.TryParse(tv, out var tp))
                tail = tp;

            if (discussion.SessionId is not null)
            {
                var snapshot = await redCompute.GetSessionAsync(discussion.SessionId);
                if (snapshot is { Messages.Count: > 0 })
                {
                    var records = await discussions.GetMessagesAsync(discussion.EntityId);

                    var collapsed = ConversationExporter.CollapseMessages(snapshot.Messages);
                    var sessionMsgs = collapsed
                        .Where(m => m.EventType == "text" && !string.IsNullOrWhiteSpace(m.Content))
                        .Select(m => new
                        {
                            id = (string?)null,
                            // Carried through so API callers (agents reacting via
                            // /reactions) can address the same message the UI does.
                            // Null for records predating the uid rollout -- better
                            // than synthesising a key that would not match.
                            messageUid = m.MessageUid,
                            role = m.Role,
                            parts = new object[] { new { type = "text", content = ConversationExporter.StripInjectedTags(m.Content ?? ""), toolName = (string?)null, toolInput = (string?)null } },
                            timestamp = m.Timestamp.ToString("o"),
                            senderAgentId = (string?)null,
                            source = (string?)"session-transcript",
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

                    var merged = sessionMsgs.Concat(eventMsgs)
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
            var msgs = fallbackRecords.Select(m =>
            {
                var partsJson = m.Metadata["parts_json"]?.GetValue<string>();
                var ts = (m.Metadata["timestamp"]?.GetValue<string>() is { } t
                    ? DateTimeOffset.Parse(t)
                    : m.CreatedAt).UtcDateTime;
                return new
                {
                    id = (string?)m.Id.ToString(),
                    messageUid = m.Metadata["uid"]?.GetValue<string>(),
                    role = m.Role,
                    parts = MapParts(partsJson, m.Content),
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
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

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

        group.MapGet("/discussions/{id}/context", async (string id, HttpContext ctx, DiscussionStore store, MessagePipeline pipeline, GeoLocationService geo) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            var userId = UserId(ctx);
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId)) return Forbidden();

            var cutoff = DateTime.UtcNow.AddDays(-2);
            var all = (await store.ListAsync())
                .Where(d => OwnerScope.CanAccess(d.OwnerId, userId))
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
            var snapshot = NovaContextBuilder.BuildSnapshot(own, others, outfit, outfitAsset, geo.Location);
            return Results.Ok(snapshot);
        });

        group.MapGet("/discussions/{id}/export", async (string id, HttpContext ctx, DiscussionStore store, ConversationExporter exporter) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            var markdown = await exporter.ExportSingleAsync(discussion);
            return Results.Text(markdown, "text/markdown");
        });

        group.MapPut("/discussions/{id}/title", async (string id, DiscussionTitleRequest request, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            await store.PatchAsync(discussion.EntityId, new JsonObject { ["title"] = request.Title },
                name: request.Title ?? $"Discussion {id}");
            return Results.Ok(DiscussionStore.ToInfo(discussion with { Title = request.Title }));
        });

        group.MapPut("/discussions/{id}/confidential", async (string id, DiscussionConfidentialRequest request, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            await store.PatchAsync(discussion.EntityId, new JsonObject { ["confidential"] = request.Confidential });
            return Results.Ok(DiscussionStore.ToInfo(discussion with { Confidential = request.Confidential }));
        });

        group.MapPut("/discussions/{id}/read", async (string id, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            var now = DateTime.UtcNow;
            await store.PatchAsync(discussion.EntityId, new JsonObject { ["last_read_at"] = new DateTimeOffset(now).ToString("O") });
            return Results.Ok(DiscussionStore.ToInfo(discussion with { LastReadAt = now }));
        });

        group.MapPut("/discussions/{id}/activity", async (string id, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            var now = DateTime.UtcNow;
            await store.TouchAsync(discussion.EntityId);
            return Results.Ok(DiscussionStore.ToInfo(discussion with { LastActivity = now }));
        });

        group.MapPut("/discussions/{id}/stopped", async (string id, HttpContext ctx, DiscussionStore store) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            var result = await store.TrySetStatusAsync(discussion.EntityId, DiscussionStatus.Stopped);
            return Results.Ok(DiscussionStore.ToInfo(discussion with { Status = result ?? discussion.Status }));
        });

        group.MapDelete("/discussions/{id}", async (string id, HttpContext ctx, DiscussionStore store, DiscussionLifecycle lifecycle, DiscussionActivity activity) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            if (discussion.Type == "live")
                return Results.Json(new { error = "Live discussions cannot be archived" }, statusCode: 400);

            if (discussion.Type == HeartbeatService.DiscussionType)
                return Results.Json(new { error = "Heartbeat discussions are managed by the agent config — disable the heartbeat to remove it" }, statusCode: 400);

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
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

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

            // Day marker; the post bumps message_count back to 1.
            await store.PostMessageAsync(discussion.EntityId, "assistant", "New day. Timeline cleared.", new JsonObject
            {
                ["source"] = "event:system",
                ["uid"] = Guid.NewGuid().ToString("N"),
            });

            await events.PublishAsync("discussion.cleared", new JsonObject { ["discussionId"] = id });

            return Results.Ok(new { cleared = true, discussion = DiscussionStore.ToInfo(discussion with { SessionId = null, MessageCount = 1 }) });
        });

        group.MapPost("/discussions/{id}/rotate", async (string id, HttpContext ctx, DiscussionStore store, AgentDirectory agents, DiscussionLifecycle lifecycle, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, MessagePipeline pipeline, DiscussionActivity activity, HeartbeatService heartbeat) =>
        {
            // "live" resolves to Nova's current LIVE discussion — the system:live-rotation
            // http-action automation calls this form because it cannot resolve the id itself.
            var discussion = id == "live"
                ? (await store.ListAsync()).FirstOrDefault(d =>
                    d.Type == "live" && !DiscussionStatus.IsClosed(d.Status)
                    && (agents.NovaAgentId == null || d.AgentId == null || d.AgentId == agents.NovaAgentId))
                : await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            if (discussion.Type != "live")
                return Results.Json(new { error = "Only LIVE discussions can be rotated" }, statusCode: 400);

            // Same two-phase archive as DELETE; session_id stays until the finalizer
            // confirms the stop, so the session cannot be orphaned by a rotation.
            await lifecycle.BeginArchiveAsync(discussion);
            _ = activity.OnArchived(discussion.Id, discussion.Title, discussion.Confidential);

            var fresh = await store.CreateAsync(null, discussion.AgentId, discussion.OwnerId, "live");

            await events.PublishAsync("discussion.rotated", new JsonObject
            {
                ["oldDiscussionId"] = discussion.Id,
                ["newDiscussionId"] = fresh.Id,
                ["agentId"] = fresh.AgentId,
            });

            // LIVE rotation is the heartbeat's day boundary: end-of-day tick,
            // handoff, session reset (§5 of the heartbeat design).
            heartbeat.OnLiveRotated(fresh.AgentId);

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
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

            await injector.InjectAsync(discussion, request.Content, request.Type, request.Source,
                request.SenderAgentId, request.ReplyToDiscussionId, request.Metadata, UserId(ctx));

            return Results.Ok(new { success = true });
        });

        group.MapPost("/discussions/{id}/nova-message", async (string id, NovaMessageRequest request, HttpContext ctx, DiscussionStore store, IDiscussions discussions, RedComputeClient redCompute, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, DiscussionActivity activity) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

            string? partsJson = null;
            if (!string.IsNullOrEmpty(request.AudioUrl))
            {
                partsJson = JsonSerializer.Serialize(new object[]
                {
                    new { type = "text", content = request.Content },
                    new { type = "audio", content = request.AudioUrl },
                });
            }

            var uid = Guid.NewGuid().ToString("N");
            await store.PostMessageAsync(discussion.EntityId, "assistant", request.Content, new JsonObject
            {
                ["parts_json"] = partsJson,
                ["source"] = "nova-message",
                ["sender_agent_id"] = request.SenderAgentId,
                ["uid"] = uid,
            }, UserId(ctx));

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

            await events.PublishAsync("discussion.nova-message", new JsonObject
            {
                ["discussionId"] = id,
                ["content"] = request.Content,
                ["audioUrl"] = request.AudioUrl,
                ["senderAgentId"] = request.SenderAgentId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            });

            _ = activity.OnNovaMessage(id, discussion.Title, request.Content, discussion.Confidential);
            return Results.Ok(new { success = true });
        });

        group.MapPost("/discussions/{id}/message", async (string id, DiscussionMessageRequest request, HttpContext ctx, DiscussionStore store, MessagePipeline pipeline, DeviceResolver devices, LiveEvents live, DiscussionActivity activity) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            var userId = UserId(ctx);
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId)) return Forbidden();

            if (string.IsNullOrWhiteSpace(request.Content) && (request.Images == null || request.Images.Length == 0))
                return Results.BadRequest(new { error = "Content or at least one image is required" });

            var ua = ctx.Request.Headers.UserAgent.ToString();
            var browserId = ctx.Request.Headers.TryGetValue("X-Device-Id", out var did) ? did.ToString() : null;
            var resolved = await devices.ResolveAsync(ua, browserId);

            live.NoteDevice(resolved);

            var outcome = await pipeline.SendAsync(
                discussion, userId, request.Content, request.Images, resolved,
                request.InputMethod ?? "typed", request.Latitude, request.Longitude);

            if (!outcome.Success)
                return Results.Json(new { error = outcome.ErrorMessage, code = outcome.ErrorCode }, statusCode: 502);

            _ = activity.OnUserMessage(id, discussion.Title,
                string.IsNullOrWhiteSpace(request.Content) ? "[image]" : request.Content, discussion.Confidential);
            return Results.Ok(new { success = true, sessionId = outcome.SessionId, metadata = outcome.Metadata, messageUid = outcome.MessageUid });
        });

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
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId)) return Forbidden();

            var records = await discussions.GetReactionsAsync(discussion.EntityId);
            var reactions = AggregateReactions(records, userId);
            return Results.Ok(new { reactions });
        });

        group.MapGet("/event-types", async ([FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            var items = await entities.QueryAsync(new EntityQuery { TypeSlug = "event-type", Limit = 100 });
            var types = items.Select(item => new
            {
                key = item.Data["key"]?.GetValue<string>(),
                name = item.Name,
                icon = item.Data["icon"]?.GetValue<string>(),
                color = item.Data["color"]?.GetValue<string>(),
                description = item.Data["description"]?.GetValue<string>(),
            }).Where(t => t.key != null);
            return Results.Ok(types);
        });
    }

    private static async Task<IResult> SetReactionAsync(string id, ReactionRequest request, HttpContext ctx,
        DiscussionStore store, IDiscussions discussions, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, bool remove)
    {
        var discussion = await store.GetAsync(id);
        if (discussion is null) return NotFound();
        var userId = UserId(ctx);
        if (!OwnerScope.CanAccess(discussion.OwnerId, userId)) return Forbidden();

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
