using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.WebSockets;
using Nova.App.Data;
using Nova.App.Data.Entities;
using Nova.App.Services;

namespace Nova.App.Api;

public static class DiscussionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static HttpClient RedCompute = new()
    {
        BaseAddress = new Uri(App.Config.Suite.RedCompute),
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static HttpClient RedLeaf = new()
    {
        BaseAddress = new Uri(App.Config.Suite.RedLeaf),
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static readonly ConcurrentDictionary<string, Task<string?>> _pendingSessions = new();

    private static RedLeafDiscussionReader? _redLeaf;
    private static AgentMemoryFactory? _agentMemoryFactory;
    private static GeoLocationService? _geo;
    private static DeviceResolver? _deviceResolver;
    private static string? _lastDeviceName;

    public static void Initialize(AuthenticatedHttpClientFactory factory, RedLeafDiscussionReader redLeaf, AgentMemoryFactory agentMemoryFactory, GeoLocationService? geo = null)
    {
        RedCompute = factory.CreateClient(App.Config.Suite.RedCompute, TimeSpan.FromSeconds(30));
        RedLeaf = factory.CreateClient(App.Config.Suite.RedLeaf, TimeSpan.FromSeconds(10));
        _redLeaf = redLeaf;
        _agentMemoryFactory = agentMemoryFactory;
        _geo = geo;
        _deviceResolver = new DeviceResolver(RedLeaf);
    }

    public static void MapDiscussionEndpoints(this EndpointRegistry registry, NovaEngine engine)
    {
        var memory = engine.Memory;

        registry.MapGet("/api/discussions", "List discussions, newest activity first", async (HttpContext ctx) =>
        {
            var status = ctx.Request.Query["status"].FirstOrDefault();
            var search = ctx.Request.Query["search"].FirstOrDefault();
            var agentFilter = ctx.Request.Query["agent"].FirstOrDefault();

            List<RedLeafDiscussionReader.DiscussionRead> discussions;
            try
            {
                discussions = await _redLeaf!.GetDiscussionsAsync(agentFilter);
            }
            catch (Exception ex)
            {
                return ApiError.ServiceUnavailable("redleaf_unavailable", $"RedLeaf is required for discussion reads: {ex.Message}");
            }

            IEnumerable<RedLeafDiscussionReader.DiscussionRead> filtered = discussions;
            if (!string.IsNullOrEmpty(status))
                filtered = filtered.Where(d => d.Status == status);
            else
                filtered = filtered.Where(d => d.Status != "archived");

            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(d => d.Title != null && d.Title.Contains(search, StringComparison.OrdinalIgnoreCase));

            var userId = ctx.User.FindFirstValue("sub");
            filtered = filtered.Where(d => OwnerScope.CanAccess(d.OwnerId, userId));

            return Results.Ok(filtered.Select(ToInfo));
        })
        .WithParam("status", "string", description: "Filter by status (idle, thinking, stopped, archived). Default: everything except archived", location: ParamLocation.Query)
        .WithParam("search", "string", description: "Substring match on the discussion title", location: ParamLocation.Query)
        .WithParam("agent", "string", description: "Filter by agent ID", location: ParamLocation.Query);

        registry.MapGet("/api/discussions/pending", "Count unread discussions. Side effect: refreshes cached message counts from RedCompute's session list before counting", async (HttpContext ctx, NovaDbContext db) =>
        {
            IQueryable<Discussion> pendingQuery = db.Discussions
                .Where(d => d.Status != "archived" && d.SessionId != null);

            var userId = ctx.User.FindFirstValue("sub");
            pendingQuery = pendingQuery.WhereCanAccess(userId);

            var discussions = await pendingQuery.ToListAsync();

            if (discussions.Count == 0)
                return Results.Ok(new { count = 0 });

            try
            {
                var sessions = await RedCompute.GetFromJsonAsync<List<RedComputeSession>>(
                    "/ai-session/sessions", JsonOptions);
                if (sessions != null)
                {
                    var map = sessions.ToDictionary(s => s.Id);
                    bool changed = false;
                    foreach (var d in discussions)
                    {
                        if (d.SessionId != null && map.TryGetValue(d.SessionId, out var s)
                            && s.MessageCount > d.MessageCount)
                        {
                            d.MessageCount = s.MessageCount;
                            d.LastActivity = DateTime.UtcNow;
                            changed = true;
                        }
                    }
                    if (changed) await db.SaveChangesAsync();
                }
            }
            catch { /* RedCompute unavailable — use cached data */ }

            var count = discussions.Count(d =>
                d.MessageCount > 0 && (d.LastReadAt == null || d.LastActivity > d.LastReadAt));

            return Results.Ok(new { count });
        });

        registry.MapPost("/api/discussions/sync", "Sync discussion statuses with RedCompute session liveness", async (HttpContext ctx, NovaDbContext db) =>
        {
            IQueryable<Discussion> syncQuery = db.Discussions
                .Where(d => d.Status != "archived" && d.SessionId != null);

            var userId = ctx.User.FindFirstValue("sub");
            syncQuery = syncQuery.WhereCanAccess(userId);

            var discussions = await syncQuery.ToListAsync();

            if (discussions.Count == 0)
                return Results.Ok(new { synced = 0 });

            Dictionary<string, string>? sessionStatuses;
            try
            {
                var sessions = await RedCompute.GetFromJsonAsync<List<RedComputeSession>>(
                    "/ai-session/sessions?limit=50", JsonOptions);
                sessionStatuses = sessions?.ToDictionary(s => s.Id, s => s.Status) ?? [];
            }
            catch
            {
                // RedCompute unreachable — can't determine liveness, leave statuses as-is
                return Results.Ok(discussions.Select(ToInfo));
            }

            foreach (var d in discussions)
            {
                if (d.SessionId == null) continue;
                var isAlive = sessionStatuses.TryGetValue(d.SessionId, out var rcStatus)
                    && rcStatus is "Active" or "Idle" or "Starting";

                if (isAlive && d.Status == "stopped")
                    d.Status = "idle";
                else if (!isAlive && d.Status is "idle" or "thinking")
                    d.Status = "stopped";
            }

            await db.SaveChangesAsync();
            return Results.Ok(discussions.Select(ToInfo));
        });

        registry.MapPost("/api/discussions", "Create a new discussion", async (HttpContext ctx, NovaDbContext db) =>
        {
            CreateDiscussionRequest? createReq = null;
            try { createReq = await ctx.Request.ReadFromJsonAsync<CreateDiscussionRequest>(JsonOptions); }
            catch { /* body is optional */ }

            var agentId = createReq?.AgentId ?? NovaMirror.AgentId;
            var type = createReq?.Type ?? "chat";

            if (type == "live")
            {
                var existing = await db.Discussions.AnyAsync(d => d.Type == "live" && d.AgentId == agentId && d.Status != "archived");
                if (existing)
                    return Results.Json(new { error = "A LIVE discussion already exists for this agent" }, statusCode: 409);
            }

            var discussion = new Discussion
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Status = "idle",
                Type = type,
                OwnerId = ctx.User.FindFirstValue("sub"),
                AgentId = agentId,
            };

            db.Discussions.Add(discussion);
            await db.SaveChangesAsync();

            var broadcaster = ctx.RequestServices.GetService<WebSocketBroadcaster>();
            broadcaster?.Broadcast("discussion.created", new
            {
                discussionId = discussion.Id,
                agentId = discussion.AgentId,
                status = discussion.Status,
                type = discussion.Type,
            });

            if (discussion.Type != "live")
                _ = LiveEventService.Instance?.PostAsync("discussion", $"New discussion: \"{discussion.Title ?? "untitled"}\"");

            var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
            var discId = discussion.Id;
            var ownerId = discussion.OwnerId;
            var discAgentId = discussion.AgentId;
            _pendingSessions[discId] = Task.Run(async () =>
            {
                try
                {
                    var agentMemory = await _agentMemoryFactory!.GetMemoryAsync(discAgentId);
                    var sessionId = await TryCreateSessionAsync(agentMemory, discAgentId, ownerId);
                    if (sessionId is null) return null;
                    using var scope = scopeFactory.CreateScope();
                    var scopedDb = scope.ServiceProvider.GetRequiredService<NovaDbContext>();
                    await scopedDb.Discussions
                        .Where(d => d.Id == discId && d.SessionId == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(d => d.SessionId, sessionId));
                    return sessionId;
                }
                catch { return null; }
                finally { _pendingSessions.TryRemove(discId, out _); }
            });

            return Results.Ok(ToInfo(discussion));
        });

        registry.MapGet("/api/discussions/{id}", "Get discussion metadata and local messages", async (string id, HttpContext ctx) =>
        {
            RedLeafDiscussionReader.DiscussionRead? discussion;
            List<RedLeafDiscussionReader.MessageRead> records;
            try
            {
                discussion = await _redLeaf!.GetDiscussionAsync(id);
                records = discussion is null ? [] : await _redLeaf.GetMessagesAsync(discussion.EntityId);
            }
            catch (Exception ex)
            {
                return ApiError.ServiceUnavailable("redleaf_unavailable", $"RedLeaf is required for discussion reads: {ex.Message}");
            }

            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            return Results.Ok(new
            {
                discussion = ToInfo(discussion),
                messages = records.Select(m => new
                {
                    id = m.Id.ToString(),
                    role = m.Role,
                    parts = !string.IsNullOrEmpty(m.PartsJson)
                        ? System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(m.PartsJson)!
                            .Select(p => new {
                                type = p.TryGetProperty("type", out var t) ? t.GetString() : "text",
                                content = p.TryGetProperty("content", out var c) ? c.GetString() : "",
                                toolName = p.TryGetProperty("toolName", out var tn) ? tn.GetString() : (string?)null,
                                toolInput = p.TryGetProperty("toolInput", out var ti) ? ti.GetString() : (string?)null,
                            }).ToArray()
                        : new[] { new { type = "text", content = m.Content, toolName = (string?)null, toolInput = (string?)null } },
                    timestamp = m.Timestamp.ToString("o"),
                    senderAgentId = m.SenderAgentId,
                    source = m.Source,
                }),
            });
        });

        registry.MapGet("/api/discussions/{id}/images", "Get stored images for a discussion", async (string id, HttpContext ctx) =>
        {
            RedLeafDiscussionReader.DiscussionRead? discussion;
            List<RedLeafDiscussionReader.MessageRead> records;
            try
            {
                discussion = await _redLeaf!.GetDiscussionAsync(id);
                records = discussion is null ? [] : await _redLeaf.GetMessagesAsync(discussion.EntityId);
            }
            catch (Exception ex)
            {
                return ApiError.ServiceUnavailable("redleaf_unavailable", $"RedLeaf is required for discussion reads: {ex.Message}");
            }

            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            var result = records
                .Where(m => m.PartsJson != null && m.Source == "user-message")
                .Select(r => new
                {
                    content = r.Content,
                    images = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement[]>(r.PartsJson!, JsonOptions),
                    timestamp = r.Timestamp.ToString("o"),
                });

            return Results.Ok(result);
        });

        registry.MapGet("/api/discussions/{id}/context", "Get full context snapshot for a discussion (for AI self-query)", async (string id, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-2);

            var baseQuery = db.Discussions
                .Where(d => d.Status != "archived" || (d.Status == "archived" && d.LastActivity >= cutoff))
                .WhereCanAccess(userId);

            var ownDiscussions = discussion.AgentId != null
                ? await baseQuery.Where(d => d.AgentId == discussion.AgentId).OrderByDescending(d => d.LastActivity).ToListAsync()
                : await baseQuery.OrderByDescending(d => d.LastActivity).ToListAsync();

            List<Discussion>? otherAgentDiscussions = null;
            if (discussion.AgentId != null)
            {
                otherAgentDiscussions = await baseQuery
                    .Where(d => d.AgentId != discussion.AgentId && d.Status != "archived")
                    .OrderByDescending(d => d.LastActivity)
                    .Take(5)
                    .ToListAsync();
            }

            string? currentOutfit = null;
            string? outfitAsset = null;
            if (discussion.AgentId != null)
            {
                try
                {
                    var agentJson = await RedLeaf.GetStringAsync($"api/entities/{discussion.AgentId}");
                    using var agentEntityDoc = JsonDocument.Parse(agentJson);
                    var agentDataEl = agentEntityDoc.RootElement.GetProperty("data");
                    var agentData = agentDataEl.ValueKind == JsonValueKind.String
                        ? JsonDocument.Parse(agentDataEl.GetString()!).RootElement : agentDataEl;
                    string? activeOutfitId = agentData.TryGetProperty("outfit", out var outfitRef)
                        && outfitRef.ValueKind == JsonValueKind.String
                        && outfitRef.GetString() is { Length: > 0 } ovStr && !ovStr.StartsWith('/')
                        ? ovStr : null;
                    if (!string.IsNullOrEmpty(activeOutfitId))
                    {
                        var outfitJson = await RedLeaf.GetStringAsync($"api/entities/{activeOutfitId}");
                        using var outfitDoc = JsonDocument.Parse(outfitJson);
                        var outfit = outfitDoc.RootElement;
                        var name = outfit.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                        using var od = JsonDocument.Parse(outfit.GetProperty("data").GetString()!);
                        var prompt = od.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() : null;
                        var asset = od.RootElement.TryGetProperty("asset", out var a) ? a.GetString() : null;
                        var reasoning = od.RootElement.TryGetProperty("reasoning", out var r) ? r.GetString() : null;
                        currentOutfit = $"You're wearing \"{name}\" today ({prompt}).";
                        if (reasoning != null) currentOutfit += $" You chose it because: {reasoning}";
                        if (asset != null)
                        {
                            outfitAsset = $"{App.Config.Suite.RedLeaf.TrimEnd('/')}{asset}";
                            currentOutfit += $"\nSee it: {outfitAsset}";
                        }
                    }
                }
                catch { }
            }

            var snapshot = NovaContextBuilder.BuildSnapshot(ownDiscussions, otherAgentDiscussions, currentOutfit, outfitAsset, _geo?.Location);
            return Results.Ok(snapshot);
        });

        registry.MapPut("/api/discussions/{id}/title", "Update discussion title", async (string id, DiscussionTitleRequest request, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            discussion.Title = request.Title;
            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion));
        });

        registry.MapPut("/api/discussions/{id}/read", "Mark discussion as read", async (string id, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            discussion.LastReadAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion));
        });

        registry.MapPut("/api/discussions/{id}/activity", "Update discussion last activity", async (string id, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            discussion.LastActivity = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion));
        });

        registry.MapPut("/api/discussions/{id}/stopped", "Mark discussion as stopped (session ended)", async (string id, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (discussion.Status is not "archived")
                discussion.Status = "stopped";

            await db.SaveChangesAsync();
            return Results.Ok(ToInfo(discussion));
        });

        registry.MapDelete("/api/discussions/{id}", "Archive a discussion and stop its remote RedCompute session (if it has one)", async (string id, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (discussion.Type == "live")
                return Results.Json(new { error = "Live discussions cannot be archived" }, statusCode: 400);

            if (discussion.SessionId is not null)
            {
                try
                {
                    var resp = await RedCompute.PostAsync($"/ai-session/sessions/{discussion.SessionId}/stop", null);
                    if (!resp.IsSuccessStatusCode)
                        App.LogService.Info("discussions", $"Session stop returned {(int)resp.StatusCode} during archive — proceeding");
                }
                catch (Exception ex)
                {
                    App.LogService.Info("discussions", $"Session stop failed during archive ({ex.GetType().Name}) — proceeding");
                }
            }

            discussion.Status = "archived";
            await db.SaveChangesAsync();
            _ = LiveEventService.Instance?.PostAsync("discussion", $"Archived: \"{discussion.Title ?? "untitled"}\"");
            return Results.Ok(ToInfo(discussion));
        });

        registry.MapGet("/api/discussions/search", "Full-text search across conversation content; returns matching discussions with highlighted snippets", async (HttpContext ctx) =>
        {
            var q = ctx.Request.Query["q"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            var limit = 20;
            if (ctx.Request.Query.TryGetValue("limit", out var lv) && int.TryParse(lv, out var parsed))
                limit = Math.Clamp(parsed, 1, 100);

            var snippetLen = 120;

            // Read-path cutover: server-side ILIKE over the nova-messages
            // records (capped at 1000 — match counts approximate beyond
            // that), discussion metadata from the entity list.
            List<RedLeafDiscussionReader.MessageRead> matches;
            List<RedLeafDiscussionReader.DiscussionRead> allDiscussions;
            try
            {
                matches = await _redLeaf!.SearchMessagesAsync(q);
                allDiscussions = await _redLeaf.GetDiscussionsAsync();
            }
            catch (Exception ex)
            {
                return ApiError.ServiceUnavailable("redleaf_unavailable", $"RedLeaf is required for discussion reads: {ex.Message}");
            }

            var userId = ctx.User.FindFirstValue("sub");
            var discussions = allDiscussions
                .Where(d => OwnerScope.CanAccess(d.OwnerId, userId))
                .ToDictionary(d => d.Id);

            var grouped = matches
                .Where(m => m.DiscussionId != null && discussions.ContainsKey(m.DiscussionId))
                .GroupBy(m => m.DiscussionId!)
                .Take(limit)
                .ToList();

            var results = grouped.Select(g =>
            {
                discussions.TryGetValue(g.Key, out var disc);
                var snippets = g.Take(3).Select(m =>
                {
                    var text = m.Content;
                    if (string.IsNullOrEmpty(text) && m.PartsJson != null)
                        text = ExtractTextFromParts(m.PartsJson);

                    var snippet = ExtractSnippet(text, q, snippetLen);
                    return new { role = m.Role, timestamp = m.Timestamp, snippet };
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
        })
        .WithParam("q", "string", required: true, description: "Text to find in message content", location: ParamLocation.Query)
        .WithParam("limit", "integer", description: "Max discussions returned (clamped to 1-100)", defaultValue: 20, location: ParamLocation.Query);

        registry.MapGet("/api/event-types", "Get event types for timeline rendering (from RedLeaf entities)", async (HttpContext ctx) =>
        {
            try
            {
                var response = await RedLeaf.GetStringAsync("api/entities?type=event-type&limit=100");
                using var doc = JsonDocument.Parse(response);
                var types = doc.RootElement.GetProperty("items").EnumerateArray().Select(item =>
                {
                    var dataEl = item.GetProperty("data");
                    var data = dataEl.ValueKind == JsonValueKind.String
                        ? JsonDocument.Parse(dataEl.GetString()!).RootElement
                        : dataEl;
                    return new
                    {
                        key = data.TryGetProperty("key", out var k) ? k.GetString() : null,
                        name = item.GetProperty("name").GetString(),
                        icon = data.TryGetProperty("icon", out var i) ? i.GetString() : null,
                        color = data.TryGetProperty("color", out var c) ? c.GetString() : null,
                        description = data.TryGetProperty("description", out var d) ? d.GetString() : null,
                    };
                }).Where(t => t.key != null).ToList();
                return Results.Ok(types);
            }
            catch (Exception ex)
            {
                return ApiError.ServiceUnavailable("redleaf_unavailable", $"RedLeaf is required for event types: {ex.Message}");
            }
        });

        registry.MapGet("/api/discussions/live", "Get LIVE discussion(s), optionally filtered by agent", async (HttpContext ctx) =>
        {
            var agentFilter = ctx.Request.Query["agent"].FirstOrDefault();

            List<RedLeafDiscussionReader.DiscussionRead> discussions;
            try { discussions = await _redLeaf!.GetDiscussionsAsync(agentFilter); }
            catch (Exception ex)
            {
                return ApiError.ServiceUnavailable("redleaf_unavailable", $"RedLeaf is required for discussion reads: {ex.Message}");
            }

            var userId = ctx.User.FindFirstValue("sub");
            var live = discussions
                .Where(d => d.Type == "live" && d.Status != "archived" && OwnerScope.CanAccess(d.OwnerId, userId))
                .Select(ToInfo);
            return Results.Ok(live);
        })
        .WithParam("agent", "string", description: "Filter by agent ID", location: ParamLocation.Query);

        registry.MapPost("/api/discussions/{id}/clear", "Clear all messages from a LIVE discussion (daily reset)", async (string id, HttpContext ctx, NovaDbContext db, WebSocketBroadcaster? broadcaster) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (discussion.Type != "live")
                return Results.Json(new { error = "Only LIVE discussions can be cleared" }, statusCode: 400);

            if (discussion.SessionId is not null)
            {
                try { await RedCompute.PostAsync($"/ai-session/sessions/{discussion.SessionId}/stop", null); }
                catch { }
                discussion.SessionId = null;
            }

            var deleted = await db.Conversations.Where(c => c.ContextId == id).ExecuteDeleteAsync();
            discussion.MessageCount = 0;
            discussion.LastContextJson = null;
            discussion.LastActivity = DateTime.UtcNow;
            await db.SaveChangesAsync();

            try
            {
                var disc = await _redLeaf!.GetDiscussionAsync(id);
                if (disc != null)
                {
                    var deleteResp = await RedLeaf.DeleteAsync($"api/streams/nova-messages/records?entity_id={disc.EntityId}");
                    deleteResp.EnsureSuccessStatusCode();
                }
            }
            catch (Exception ex)
            {
                App.LogService.Warn("discussions", $"RedLeaf stream clear failed for {id}: {ex.Message}");
            }

            var marker = new ConversationRecord
            {
                ContextId = id,
                Role = "assistant",
                Content = "New day. Timeline cleared.",
                Source = "event:system",
                Timestamp = DateTime.UtcNow,
            };
            db.Conversations.Add(marker);
            discussion.MessageCount = 1;
            await db.SaveChangesAsync();

            NovaMirror.PublishDiscussion(discussion);
            NovaMirror.PublishMessages([marker]);

            broadcaster?.Broadcast("discussion.cleared", new
            {
                discussionId = id,
            });

            return Results.Ok(new { cleared = deleted, discussion = ToInfo(discussion) });
        });

        registry.MapPost("/api/discussions/{id}/event", "Inject an automation event into a discussion", async (string id, DiscussionEventRequest request, HttpContext ctx, NovaDbContext db, WebSocketBroadcaster? broadcaster) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

            var role = request.Type is "assistant" or "system" ? request.Type : "user";
            var source = $"event:{request.Source ?? "automation"}";

            string? partsJson = null;
            if (request.Metadata is { } meta)
            {
                var parts = new object[]
                {
                    new { type = "text", content = request.Content },
                    new { type = "event_data", source = request.Source, data = meta },
                };
                partsJson = JsonSerializer.Serialize(parts, JsonOptions);
            }

            var record = new ConversationRecord
            {
                ContextId = id,
                Role = role,
                Content = request.Content,
                PartsJson = partsJson,
                Source = source,
                UserId = userId,
                SenderAgentId = request.SenderAgentId,
            };
            db.Conversations.Add(record);

            discussion.LastActivity = DateTime.UtcNow;
            discussion.MessageCount++;
            await db.SaveChangesAsync();
            NovaMirror.PublishDiscussion(discussion);

            try
            {
                var disc = await _redLeaf!.GetDiscussionAsync(id);
                if (disc != null)
                {
                    var streamRecord = new
                    {
                        data = new
                        {
                            discussion_id = id,
                            role,
                            content = request.Content,
                            parts_json = partsJson,
                            source,
                            sender_agent_id = request.SenderAgentId,
                            timestamp = record.Timestamp.ToString("O"),
                        },
                        entity_id = disc.EntityId,
                    };
                    await RedLeaf.PostAsJsonAsync("api/streams/nova-messages/records", streamRecord, JsonOptions);
                }
            }
            catch (Exception ex)
            {
                App.LogService.Warn("discussions", $"Direct RedLeaf write failed for event in {id}: {ex.Message}");
            }

            broadcaster?.Broadcast("discussion.event", new
            {
                discussionId = id,
                sessionId = discussion.SessionId,
                content = request.Content,
                source = request.Source ?? "automation",
                senderAgentId = request.SenderAgentId,
            });

            if (discussion.SessionId is not null && role != "system")
            {
                if (request.SenderAgentId is not null && request.ReplyToDiscussionId is not null)
                {
                    try
                    {
                        var callbackUrl = $"http://127.0.0.1:18803/api/callbacks/agent-response?replyTo={request.ReplyToDiscussionId}&agentId={discussion.AgentId}";
                        await RedCompute.PostAsJsonAsync(
                            $"/ai-session/sessions/{discussion.SessionId}/callback",
                            new { url = callbackUrl, force = true }, JsonOptions);
                    }
                    catch { }
                }

                try
                {
                    object messageBody = request.SenderAgentId is not null
                        ? new { content = request.Content, metadata = new { senderAgentId = request.SenderAgentId, senderName = await _agentMemoryFactory!.GetAgentNameAsync(request.SenderAgentId) } }
                        : new { content = request.Content };
                    await RedCompute.PostAsJsonAsync(
                        $"/ai-session/sessions/{discussion.SessionId}/message",
                        messageBody, JsonOptions);
                }
                catch { }
            }

            return Results.Ok(new { success = true });
        });

        registry.MapPost("/api/discussions/{id}/nova-message", "Inject a Nova-authored (assistant) message into a discussion without triggering inference", async (string id, NovaMessageRequest request, HttpContext ctx, NovaDbContext db, WebSocketBroadcaster? broadcaster) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

            string? partsJson = null;
            if (!string.IsNullOrEmpty(request.AudioUrl))
            {
                var parts = new List<object> { new { type = "text", content = request.Content } };
                parts.Add(new { type = "audio", content = request.AudioUrl });
                partsJson = System.Text.Json.JsonSerializer.Serialize(parts);
            }

            var record = new ConversationRecord
            {
                ContextId = id,
                Role = "assistant",
                Content = request.Content,
                PartsJson = partsJson,
                Source = "nova-message",
                UserId = userId,
                SenderAgentId = request.SenderAgentId,
            };
            db.Conversations.Add(record);

            discussion.LastActivity = DateTime.UtcNow;
            discussion.MessageCount++;
            discussion.InjectedContext = request.Content;

            if (!string.IsNullOrWhiteSpace(request.Title))
                discussion.Title = request.Title;

            await db.SaveChangesAsync();
            NovaMirror.PublishDiscussion(discussion);
            NovaMirror.PublishMessages([record]);

            // Best-effort inject into live session (message is already persisted above).
            // If the session isn't ready or RedCompute is down, the message will be
            // replayed by SendMessageCoreAsync when the user first messages the discussion.
            if (discussion.SessionId is not null)
            {
                try
                {
                    object? metadata = request.SenderAgentId is not null
                        ? new { senderAgentId = request.SenderAgentId }
                        : null;
                    var resp = await RedCompute.PostAsJsonAsync(
                        $"/ai-session/sessions/{discussion.SessionId}/inject",
                        new
                        {
                            role = "assistant",
                            content = request.Content,
                            audioUrl = string.IsNullOrEmpty(request.AudioUrl) ? null : request.AudioUrl,
                            metadata,
                        }, JsonOptions);
                    resp.EnsureSuccessStatusCode();
                }
                catch { /* best-effort — message is already in the DB */ }
            }

            broadcaster?.Broadcast("discussion.nova-message", new
            {
                discussionId = id,
                content = request.Content,
                audioUrl = request.AudioUrl,
                senderAgentId = request.SenderAgentId,
            });

            return Results.Ok(new { success = true });
        });

        registry.MapPost("/api/discussions/{id}/message", "Send a message to a discussion (enriches with cross-discussion context)", async (string id, DiscussionMessageRequest request, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (string.IsNullOrWhiteSpace(request.Content) && (request.Images == null || request.Images.Length == 0))
                return Results.BadRequest(new { error = "Content or at least one image is required" });

            var ua = ctx.Request.Headers.UserAgent.ToString();
            var browserId = ctx.Request.Headers.TryGetValue("X-Device-Id", out var did) ? did.ToString() : null;
            var resolved = _deviceResolver != null
                ? await _deviceResolver.ResolveAsync(ua, browserId)
                : new ResolvedDevice { Name = "unknown", Type = "unknown", Platform = "unknown" };

            if (resolved.Name != "unknown" && resolved.Name != _lastDeviceName)
            {
                if (_lastDeviceName != null)
                    _ = LiveEventService.Instance?.PostAsync("device", $"Switched to {resolved.Name}");
                _lastDeviceName = resolved.Name;
            }

            var outcome = await SendMessageCoreAsync(
                db, memory, discussion, userId,
                request.Content, request.Images, resolved, request.InputMethod ?? "typed");

            if (!outcome.Success)
                return ApiError.BadGateway(outcome.ErrorCode!, outcome.ErrorMessage!);

            return Results.Ok(new { success = true, sessionId = outcome.SessionId, metadata = outcome.Metadata });
        })
        .WithRequestBody(new
        {
            type = "object",
            description = "Either content or at least one image must be provided",
            properties = new
            {
                content = new { type = "string", description = "Message text (optional when images are provided). Delivered to the session enriched with a cross-discussion <nova-context> block." },
                images = new
                {
                    type = "array",
                    description = "Optional image attachments.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            mediaType = new { type = "string", description = "MIME type, e.g. image/png" },
                            base64 = new { type = "string", description = "Base64-encoded image data (no data: prefix)" },
                        },
                    },
                },
                inputMethod = new { type = "string", description = "How the message was produced (e.g. 'typed', 'voice'); surfaced to Nova in the context block.", @default = "typed" },
            },
        });
    }

    internal static async Task<string?> TryCreateSessionAsync(MemoryManager memory, string? agentId, string? ownerId)
    {
        memory.GenerateClaudeMd();

        var agentProvider = agentId != null ? await _agentMemoryFactory!.GetAgentProviderAsync(agentId) : null;

        var body = new Dictionary<string, object?>
        {
            ["projectPath"] = memory.WorkspacePath,
            ["qualityTier"] = App.Config.DefaultQualityMode ?? "standard",
        };

        if (agentProvider != null)
            body["provider"] = agentProvider;

        var req = new HttpRequestMessage(HttpMethod.Post, "/ai-session/sessions")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Add("X-Caller-Info", "Nova:agent");
        if (ownerId != null)
            req.Headers.Add("X-User-Id", ownerId);
        var resp = await RedCompute.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        var session = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return session.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
    }

    internal sealed record SendMessageOutcome(bool Success, string? SessionId, string? ErrorCode, string? ErrorMessage, Dictionary<string, object?>? Metadata = null);

    /// <summary>
    /// Shared message-send pipeline: lazily creates the RedCompute session, injects any
    /// pending context, enriches the content with the cross-discussion nova-context block,
    /// and delivers the message. Used by POST /api/discussions/{id}/message and POST /api/ask.
    /// </summary>
    internal static async Task<SendMessageOutcome> SendMessageCoreAsync(
        NovaDbContext db, MemoryManager memory, Discussion discussion, string? userId,
        string content, ImageAttachmentDto[]? images, ResolvedDevice device, string input)
    {
        if (discussion.SessionId is null && _pendingSessions.TryRemove(discussion.Id, out var pending))
        {
            discussion.SessionId = await pending;
            if (discussion.SessionId != null)
                await db.SaveChangesAsync();
        }

        if (discussion.SessionId is null)
        {
            try
            {
                var agentMemory = await _agentMemoryFactory!.GetMemoryAsync(discussion.AgentId);
                discussion.SessionId = await TryCreateSessionAsync(agentMemory, discussion.AgentId, discussion.OwnerId);
            }
            catch
            {
                return new(false, null, "redcompute_unavailable",
                    "RedCompute could not be reached to start a session. Check that it is running on its configured port.");
            }

            if (discussion.SessionId is null)
                return new(false, null, "redcompute_unavailable", "RedCompute refused to create a session.");

            await db.SaveChangesAsync();

            // Replay pending assistant messages (from nova-message on pre-created discussions)
            // into the new session so they appear as visible chat bubbles.
            var pendingMessages = await db.Conversations
                .Where(c => c.ContextId == discussion.Id && c.Role == "assistant" && c.Source == "nova-message")
                .OrderBy(c => c.Timestamp)
                .ToListAsync();

            foreach (var msg in pendingMessages)
            {
                try
                {
                    var resp = await RedCompute.PostAsJsonAsync(
                        $"/ai-session/sessions/{discussion.SessionId}/inject",
                        new { role = "assistant", content = msg.Content }, JsonOptions);
                    resp.EnsureSuccessStatusCode();
                }
                catch { /* best-effort — don't block the user's message */ }
            }
        }

        var priorMessage = discussion.InjectedContext;

        var now = DateTime.UtcNow;
        var cutoff = now.AddDays(-2);

        var baseQuery = db.Discussions
            .Where(d => d.Status != "archived" || (d.Status == "archived" && d.LastActivity >= cutoff))
            .WhereCanAccess(userId);

        var ownDiscussions = discussion.AgentId != null
            ? await baseQuery.Where(d => d.AgentId == discussion.AgentId).OrderByDescending(d => d.LastActivity).ToListAsync()
            : await baseQuery.OrderByDescending(d => d.LastActivity).ToListAsync();

        List<Discussion>? otherAgentDiscussions = null;
        if (discussion.AgentId != null)
        {
            otherAgentDiscussions = await baseQuery
                .Where(d => d.AgentId != discussion.AgentId && d.Status != "archived")
                .OrderByDescending(d => d.LastActivity)
                .Take(5)
                .ToListAsync();
        }

        var agentName = discussion.AgentId != null
            ? (await _agentMemoryFactory!.GetAgentNameAsync(discussion.AgentId))
            : null;

        string? currentOutfit = null;
        string? currentOutfitAsset = null;
        if (discussion.AgentId != null)
        {
            try
            {
                var agentJson = await RedLeaf.GetStringAsync($"api/entities/{discussion.AgentId}");
                using var agentEntityDoc = JsonDocument.Parse(agentJson);
                var agentDataEl = agentEntityDoc.RootElement.GetProperty("data");
                var agentData = agentDataEl.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(agentDataEl.GetString()!).RootElement : agentDataEl;
                string? activeOutfitId = agentData.TryGetProperty("outfit", out var outfitRef)
                    && outfitRef.ValueKind == JsonValueKind.String
                    && outfitRef.GetString() is { Length: > 0 } ovStr && !ovStr.StartsWith('/')
                    ? ovStr : null;
                if (!string.IsNullOrEmpty(activeOutfitId))
                {
                    var outfitJson = await RedLeaf.GetStringAsync($"api/entities/{activeOutfitId}");
                    using var outfitDoc = JsonDocument.Parse(outfitJson);
                    var outfit = outfitDoc.RootElement;
                    var name = outfit.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    using var od = JsonDocument.Parse(outfit.GetProperty("data").GetString()!);
                    var prompt = od.RootElement.TryGetProperty("prompt", out var p) ? p.GetString() : null;
                    var asset = od.RootElement.TryGetProperty("asset", out var a) ? a.GetString() : null;
                    var reasoning = od.RootElement.TryGetProperty("reasoning", out var r) ? r.GetString() : null;
                    currentOutfit = $"You're wearing \"{name}\" today ({prompt}).";
                    if (reasoning != null)
                        currentOutfit += $" You chose it because: {reasoning}";
                    if (asset != null)
                    {
                        currentOutfitAsset = $"{App.Config.Suite.RedLeaf.TrimEnd('/')}{asset}";
                        currentOutfit += $"\nSee it: {currentOutfitAsset}";
                    }
                }
            }
            catch { }
        }

        string? moodSummary = null;
        if (discussion.AgentId != null)
        {
            try
            {
                var agentMemory = await _agentMemoryFactory!.GetMemoryAsync(discussion.AgentId);
                var moodPath = Path.Combine(agentMemory.MemoryPath, "dreaming", "mood.md");
                if (File.Exists(moodPath))
                {
                    var lines = File.ReadAllLines(moodPath);
                    var energy = lines.FirstOrDefault(l => l.StartsWith("Energy:"))?.Trim();
                    var vibe = lines.FirstOrDefault(l => l.StartsWith("Vibe:"))?.Trim();
                    if (energy != null || vibe != null)
                        moodSummary = string.Join(", ", new[] { energy, vibe }.Where(s => s != null));
                }
            }
            catch { }
        }

        var currentSnapshot = NovaContextBuilder.BuildSnapshot(
            ownDiscussions, otherAgentDiscussions, currentOutfit, currentOutfitAsset,
            _geo?.Location, moodSummary);
        var previousSnapshot = NovaContextBuilder.DeserializeSnapshot(discussion.LastContextJson);

        string contextBlock;
        if (previousSnapshot == null || discussion.MessageCount == 0)
            contextBlock = NovaContextBuilder.BuildFullContext(currentSnapshot, discussion.Id, now, device, input, agentName);
        else
            contextBlock = NovaContextBuilder.BuildDeltaContext(currentSnapshot, previousSnapshot, discussion.Id, now, device, input, agentName);

        discussion.LastContextJson = NovaContextBuilder.SerializeSnapshot(currentSnapshot);

        var metadata = NovaContextBuilder.BuildMetadata(currentSnapshot, now, device, input, discussion.Id, agentName);

        var priorBlock = priorMessage != null
            ? $"\n<nova-prior-message role=\"assistant\">\n{priorMessage}\n</nova-prior-message>\n"
            : "";
        var enrichedContent = contextBlock + priorBlock + "\n" + content;

        try
        {
            var resp = await RedCompute.PostAsJsonAsync(
                $"/ai-session/sessions/{discussion.SessionId}/message",
                new { content = enrichedContent, images }, JsonOptions);
            resp.EnsureSuccessStatusCode();
        }
        catch
        {
            return new(false, discussion.SessionId, "redcompute_unavailable",
                "RedCompute could not deliver the message to the session.");
        }

        discussion.MessageCount++;
        discussion.LastActivity = now;

        if (discussion.InjectedContext != null)
        {
            discussion.InjectedContext = null;
        }

        if (images is { Length: > 0 })
        {
            db.Conversations.Add(new ConversationRecord
            {
                ContextId = discussion.Id,
                Role = "user",
                Content = content,
                PartsJson = System.Text.Json.JsonSerializer.Serialize(
                    images.Select(img => new { type = "image", mediaType = img.MediaType, base64 = img.Base64 }),
                    JsonOptions),
                Source = "user-message",
                Timestamp = now,
                UserId = userId,
            });
        }

        await db.SaveChangesAsync();

        return new(true, discussion.SessionId, null, null, metadata);
    }


    private static string FormatRelativeTime(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes < 1) return "just now";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}min ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
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

    private static object ToInfo(Discussion d)
    {
        return new
        {
            d.Id,
            d.Title,
            d.SessionId,
            d.Status,
            d.Type,
            d.CreatedAt,
            d.LastActivity,
            d.MessageCount,
            d.LastReadAt,
            d.AgentId,
        };
    }

    private static object ToInfo(RedLeafDiscussionReader.DiscussionRead d)
    {
        return new
        {
            d.Id,
            d.Title,
            d.SessionId,
            d.Status,
            d.Type,
            d.CreatedAt,
            d.LastActivity,
            d.MessageCount,
            d.LastReadAt,
            d.AgentId,
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..(maxLength - 1)] + "…";
    }
}

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
}

public class ImageAttachmentDto
{
    public string MediaType { get; set; } = "";
    public string Base64 { get; set; } = "";
}

public class RedComputeSession
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public int MessageCount { get; set; }
}
