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
}

public class DiscussionTitleRequest
{
    public string? Title { get; set; }
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
                : filtered.Where(d => d.Status != "archived");

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
                .Where(d => d.Status != "archived" && d.SessionId != null)
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
                .Where(d => d.Status != "archived" && d.SessionId != null)
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
                    await store.PatchAsync(d.EntityId, new JsonObject { ["status"] = newStatus });
                    discussions[i] = d with { Status = newStatus };
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

            if (type == "live")
            {
                var existing = (await store.ListAsync()).Any(d => d.Type == "live" && d.AgentId == agentId && d.Status != "archived");
                if (existing)
                    return Results.Json(new { error = "A LIVE discussion already exists for this agent" }, statusCode: 409);
            }

            var discussion = await store.CreateAsync(null, agentId, UserId(ctx), type);

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
                .Where(d => d.Type == "live" && d.Status != "archived" && OwnerScope.CanAccess(d.OwnerId, userId))
                .Select(DiscussionStore.ToInfo);
            return Results.Ok(live);
        });

        group.MapGet("/discussions/search", async (HttpContext ctx, DiscussionStore store, IDiscussions discussions) =>
        {
            var q = ctx.Request.Query["q"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            var limit = 20;
            if (ctx.Request.Query.TryGetValue("limit", out var lv) && int.TryParse(lv, out var parsed))
                limit = Math.Clamp(parsed, 1, 100);

            const int snippetLen = 120;

            var matches = await discussions.SearchMessagesAsync(q);
            var userId = UserId(ctx);
            var byId = (await store.ListAsync())
                .Where(d => OwnerScope.CanAccess(d.OwnerId, userId))
                .ToDictionary(d => d.Id);

            var grouped = matches
                .Select(m => (Message: m, DiscussionId: m.Metadata["discussion_id"]?.GetValue<string>()))
                .Where(x => x.DiscussionId != null && byId.ContainsKey(x.DiscussionId))
                .GroupBy(x => x.DiscussionId!)
                .Take(limit)
                .ToList();

            var results = grouped.Select(g =>
            {
                byId.TryGetValue(g.Key, out var disc);
                var snippets = g.Take(3).Select(x =>
                {
                    var text = x.Message.Content;
                    if (string.IsNullOrEmpty(text))
                    {
                        var partsJson = x.Message.Metadata["parts_json"]?.GetValue<string>();
                        if (partsJson != null) text = ExtractTextFromParts(partsJson);
                    }
                    return new
                    {
                        role = x.Message.Role,
                        timestamp = x.Message.CreatedAt.UtcDateTime,
                        snippet = ExtractSnippet(text, q, snippetLen),
                    };
                });

                return new
                {
                    discussionId = g.Key,
                    title = disc?.Title,
                    status = disc?.Status,
                    lastActivity = disc?.LastActivity,
                    matchCount = g.Count(),
                    snippets,
                };
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
                .Take(limit)
                .ToList();

            var markdown = await exporter.ExportAsync(discussions, since);
            return Results.Text(markdown, "text/markdown");
        });

        group.MapGet("/discussions/{id}", async (string id, HttpContext ctx, DiscussionStore store, IDiscussions discussions) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            var records = await discussions.GetMessagesAsync(discussion.EntityId);
            return Results.Ok(new
            {
                discussion = DiscussionStore.ToInfo(discussion),
                messages = records.Select(m =>
                {
                    var partsJson = m.Metadata["parts_json"]?.GetValue<string>();
                    return new
                    {
                        id = m.Id.ToString(),
                        messageUid = m.Metadata["uid"]?.GetValue<string>(),
                        role = m.Role,
                        parts = MapParts(partsJson, m.Content),
                        timestamp = (m.Metadata["timestamp"]?.GetValue<string>() is { } ts ? DateTimeOffset.Parse(ts) : m.CreatedAt).UtcDateTime.ToString("o"),
                        senderAgentId = m.Metadata["sender_agent_id"]?.GetValue<string>(),
                        source = m.Metadata["source"]?.GetValue<string>(),
                    };
                }),
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
                .Where(d => d.Status != "archived" || d.LastActivity >= cutoff)
                .ToList();

            var own = discussion.AgentId != null
                ? all.Where(d => d.AgentId == discussion.AgentId).ToList()
                : all;
            var others = discussion.AgentId != null
                ? all.Where(d => d.AgentId != discussion.AgentId && d.Status != "archived").Take(5).ToList()
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

            if (discussion.Status is not "archived")
            {
                await store.PatchAsync(discussion.EntityId, new JsonObject { ["status"] = "stopped" });
                discussion = discussion with { Status = "stopped" };
            }
            return Results.Ok(DiscussionStore.ToInfo(discussion));
        });

        group.MapDelete("/discussions/{id}", async (string id, HttpContext ctx, DiscussionStore store, RedComputeClient redCompute, DiscussionActivity activity) =>
        {
            var discussion = await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            if (discussion.Type == "live")
                return Results.Json(new { error = "Live discussions cannot be archived" }, statusCode: 400);

            if (discussion.SessionId is not null)
            {
                try { await redCompute.StopAsync(discussion.SessionId); }
                catch { /* proceed with archive regardless */ }
            }

            await store.PatchAsync(discussion.EntityId, new JsonObject { ["status"] = "archived" });
            _ = activity.OnArchived(id, discussion.Title);
            return Results.Ok(DiscussionStore.ToInfo(discussion with { Status = "archived" }));
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

            // Day marker; PostAsync bumps message_count back to 1.
            await discussions.PostAsync(discussion.EntityId, "assistant", "New day. Timeline cleared.", new JsonObject
            {
                ["source"] = "event:system",
                ["uid"] = Guid.NewGuid().ToString("N"),
            });

            await events.PublishAsync("discussion.cleared", new JsonObject { ["discussionId"] = id });

            return Results.Ok(new { cleared = true, discussion = DiscussionStore.ToInfo(discussion with { SessionId = null, MessageCount = 1 }) });
        });

        group.MapPost("/discussions/{id}/rotate", async (string id, HttpContext ctx, DiscussionStore store, AgentDirectory agents, RedComputeClient redCompute, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, MessagePipeline pipeline, DiscussionActivity activity) =>
        {
            // "live" resolves to Nova's current LIVE discussion — the system:live-rotation
            // http-action automation calls this form because it cannot resolve the id itself.
            var discussion = id == "live"
                ? (await store.ListAsync()).FirstOrDefault(d =>
                    d.Type == "live" && d.Status != "archived"
                    && (agents.NovaAgentId == null || d.AgentId == null || d.AgentId == agents.NovaAgentId))
                : await store.GetAsync(id);
            if (discussion is null) return NotFound();
            if (!OwnerScope.CanAccess(discussion.OwnerId, UserId(ctx))) return Forbidden();

            if (discussion.Type != "live")
                return Results.Json(new { error = "Only LIVE discussions can be rotated" }, statusCode: 400);

            if (discussion.SessionId is not null)
            {
                try { await redCompute.StopAsync(discussion.SessionId); }
                catch { }
            }

            await store.PatchAsync(discussion.EntityId, new JsonObject
            {
                ["status"] = "archived",
                ["session_id"] = null,
            });
            _ = activity.OnArchived(discussion.Id, discussion.Title);

            var fresh = await store.CreateAsync(null, discussion.AgentId, discussion.OwnerId, "live");

            await events.PublishAsync("discussion.rotated", new JsonObject
            {
                ["oldDiscussionId"] = discussion.Id,
                ["newDiscussionId"] = fresh.Id,
                ["agentId"] = fresh.AgentId,
            });

            pipeline.BeginSessionCreation(fresh);

            return Results.Ok(new
            {
                archived = DiscussionStore.ToInfo(discussion with { Status = "archived", SessionId = null }),
                created = DiscussionStore.ToInfo(fresh),
            });
        });

        group.MapPost("/discussions/{id}/event", async (string id, DiscussionEventRequest request, HttpContext ctx, DiscussionStore store, EventInjector injector) =>
        {
            var discussion = await store.GetAsync(id);
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
            await discussions.PostAsync(discussion.EntityId, "assistant", request.Content, new JsonObject
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
            });

            _ = activity.OnNovaMessage(id, discussion.Title, request.Content);
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
                string.IsNullOrWhiteSpace(request.Content) ? "[image]" : request.Content);
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

        var isAgent = !remove && !string.IsNullOrEmpty(request.AgentId);
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

    private static string ExtractTextFromParts(string partsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(partsJson);
            var texts = doc.RootElement.EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() == "text"
                         && p.TryGetProperty("content", out _))
                .Select(p => p.GetProperty("content").GetString())
                .Where(t => !string.IsNullOrWhiteSpace(t));
            return string.Join(" ", texts);
        }
        catch { return ""; }
    }

    private static string ExtractSnippet(string? text, string query, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var idx = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return text.Length > maxLen ? text[..maxLen] + "..." : text;

        var start = Math.Max(0, idx - maxLen / 3);
        var end = Math.Min(text.Length, start + maxLen);
        start = Math.Max(0, end - maxLen);

        var snippet = text[start..end];
        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";
        return snippet;
    }
}
