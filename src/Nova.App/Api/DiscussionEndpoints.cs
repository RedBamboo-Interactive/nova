using System.Collections.Concurrent;
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

    private static readonly ConcurrentDictionary<string, Task<string?>> _pendingSessions = new();

    private static RedLeafDiscussionReader? _redLeaf;

    public static void Initialize(AuthenticatedHttpClientFactory factory, RedLeafDiscussionReader redLeaf)
    {
        RedCompute = factory.CreateClient(App.Config.Suite.RedCompute, TimeSpan.FromSeconds(30));
        _redLeaf = redLeaf;
    }

    public static void MapDiscussionEndpoints(this EndpointRegistry registry, NovaEngine engine)
    {
        var memory = engine.Memory;

        registry.MapGet("/api/discussions", "List discussions, newest activity first", async (HttpContext ctx) =>
        {
            var status = ctx.Request.Query["status"].FirstOrDefault();
            var search = ctx.Request.Query["search"].FirstOrDefault();

            // Read-path cutover: RedLeaf is the source of truth for
            // discussions. By decision it is a hard dependency — no local
            // fallback. Writes stay on SQLite (NovaMirror dual-writes).
            List<RedLeafDiscussionReader.DiscussionRead> discussions;
            try
            {
                discussions = await _redLeaf!.GetDiscussionsAsync();
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
        .WithParam("search", "string", description: "Substring match on the discussion title", location: ParamLocation.Query);

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
            var discussion = new Discussion
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Status = "idle",
                OwnerId = ctx.User.FindFirstValue("sub"),
            };

            db.Discussions.Add(discussion);
            await db.SaveChangesAsync();

            var scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
            var discId = discussion.Id;
            var ownerId = discussion.OwnerId;
            _pendingSessions[discId] = Task.Run(async () =>
            {
                try
                {
                    var sessionId = await TryCreateSessionAsync(memory, ownerId);
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
                    parts = new[] { new { type = "text", content = m.Content } },
                    timestamp = m.Timestamp.ToString("o"),
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

            if (discussion.SessionId is not null)
            {
                try
                {
                    var resp = await RedCompute.PostAsync($"/ai-session/sessions/{discussion.SessionId}/stop", null);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        var reason = $"Session stop failed (HTTP {(int)resp.StatusCode})";
                        App.LogService.Warn("discussions", $"Archive blocked — {reason}: {body}");
                        return Results.Json(new { error = reason }, statusCode: 502);
                    }
                }
                catch (Exception ex)
                {
                    var reason = $"Session stop failed ({ex.GetType().Name}: {ex.Message})";
                    App.LogService.Warn("discussions", $"Archive blocked — {reason}");
                    return Results.Json(new { error = reason }, statusCode: 502);
                }
            }

            discussion.Status = "archived";
            await db.SaveChangesAsync();
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

            db.Conversations.Add(new ConversationRecord
            {
                ContextId = id,
                Role = "user",
                Content = request.Content,
                Source = $"event:{request.Source ?? "automation"}",
                UserId = userId,
            });

            discussion.LastActivity = DateTime.UtcNow;
            discussion.MessageCount++;
            await db.SaveChangesAsync();

            if (discussion.SessionId is not null)
            {
                broadcaster?.Broadcast("discussion.event", new
                {
                    discussionId = id,
                    sessionId = discussion.SessionId,
                    content = request.Content,
                    source = request.Source ?? "automation",
                });

                try
                {
                    await RedCompute.PostAsJsonAsync(
                        $"/ai-session/sessions/{discussion.SessionId}/message",
                        new { content = request.Content }, JsonOptions);
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

            if (discussion.SessionId is not null)
            {
                try
                {
                    var resp = await RedCompute.PostAsJsonAsync(
                        $"/ai-session/sessions/{discussion.SessionId}/inject",
                        new { role = "assistant", content = request.Content }, JsonOptions);
                    resp.EnsureSuccessStatusCode();
                }
                catch
                {
                    return ApiError.BadGateway("redcompute_unavailable",
                        "RedCompute could not inject the message into the live session.");
                }
            }

            db.Conversations.Add(new ConversationRecord
            {
                ContextId = id,
                Role = "assistant",
                Content = request.Content,
                Source = "nova-message",
                UserId = userId,
            });

            discussion.LastActivity = DateTime.UtcNow;
            discussion.MessageCount++;
            discussion.InjectedContext = request.Content;

            if (!string.IsNullOrWhiteSpace(request.Title))
                discussion.Title = request.Title;

            await db.SaveChangesAsync();

            broadcaster?.Broadcast("discussion.nova-message", new
            {
                discussionId = id,
                content = request.Content,
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
            var device = System.Text.RegularExpressions.Regex.IsMatch(ua, @"Mobile|Android|iPhone|iPad|iPod", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                ? "mobile" : "desktop";

            var outcome = await SendMessageCoreAsync(
                db, memory, discussion, userId,
                request.Content, request.Images, device, request.InputMethod ?? "typed");

            if (!outcome.Success)
                return ApiError.BadGateway(outcome.ErrorCode!, outcome.ErrorMessage!);

            return Results.Ok(new { success = true, sessionId = outcome.SessionId });
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

    /// <summary>
    /// Create a RedCompute session for Nova's workspace. Returns the session id, or null
    /// when RedCompute refused. Throws when RedCompute is unreachable.
    /// </summary>
    internal static async Task<string?> TryCreateSessionAsync(MemoryManager memory, string? ownerId)
    {
        memory.GenerateClaudeMd();

        var body = new Dictionary<string, object?>
        {
            ["projectPath"] = memory.WorkspacePath,
            ["qualityTier"] = App.Config.DefaultQualityMode ?? "standard",
        };

        var req = new HttpRequestMessage(HttpMethod.Post, "/ai-session/sessions")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        req.Headers.Add("X-Caller-Info", "Nova");
        if (ownerId != null)
            req.Headers.Add("X-User-Id", ownerId);
        var resp = await RedCompute.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        var session = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        return session.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
    }

    internal sealed record SendMessageOutcome(bool Success, string? SessionId, string? ErrorCode, string? ErrorMessage);

    /// <summary>
    /// Shared message-send pipeline: lazily creates the RedCompute session, injects any
    /// pending context, enriches the content with the cross-discussion nova-context block,
    /// and delivers the message. Used by POST /api/discussions/{id}/message and POST /api/ask.
    /// </summary>
    internal static async Task<SendMessageOutcome> SendMessageCoreAsync(
        NovaDbContext db, MemoryManager memory, Discussion discussion, string? userId,
        string content, ImageAttachmentDto[]? images, string device, string input)
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
                discussion.SessionId = await TryCreateSessionAsync(memory, discussion.OwnerId);
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

        IQueryable<Discussion> contextQuery = db.Discussions
            .Where(d => d.Status != "archived" || (d.Status == "archived" && d.LastActivity >= cutoff));

        contextQuery = contextQuery.WhereCanAccess(userId);

        var allDiscussions = await contextQuery
            .OrderByDescending(d => d.LastActivity)
            .ToListAsync();

        var contextBlock = BuildNovaContext(allDiscussions, discussion.Id, now, device, input);
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

        return new(true, discussion.SessionId, null, null);
    }

    private static string BuildNovaContext(List<Discussion> discussions, string currentId, DateTime now, string device, string input)
    {
        var active = discussions.Where(d => d.Status != "archived").ToList();
        var archived = discussions.Where(d => d.Status == "archived" && d.MessageCount > 0).Take(10).ToList();

        var sb = new System.Text.StringBuilder();
        sb.Append($"<nova-context timestamp=\"{now:yyyy-MM-ddTHH:mm:ssZ}\" day=\"{now.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture)}\" device=\"{device}\" input=\"{input}\" discussion=\"{currentId}\">");

        if (active.Count > 0)
        {
            sb.Append("\nActive discussions:");
            foreach (var d in active)
            {
                var marker = d.Id == currentId ? " <- you are here" : "";
                sb.Append($"\n- [{d.Id}] \"{d.Title ?? "(untitled)"}\" . {d.MessageCount} msgs . {FormatRelativeTime(now - d.LastActivity)}{marker}");
            }
        }

        if (archived.Count > 0)
        {
            sb.Append("\n\nRecently archived:");
            foreach (var d in archived)
                sb.Append($"\n- [{d.Id}] \"{d.Title ?? "(untitled)"}\" . {d.MessageCount} msgs . archived {FormatRelativeTime(now - d.LastActivity)}");
        }

        sb.Append("\n</nova-context>");
        return sb.ToString();
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
            d.CreatedAt,
            d.LastActivity,
            d.MessageCount,
            d.LastReadAt,
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
            d.CreatedAt,
            d.LastActivity,
            d.MessageCount,
            d.LastReadAt,
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..(maxLength - 1)] + "…";
    }
}

public class DiscussionTitleRequest
{
    public string? Title { get; set; }
}

public class DiscussionEventRequest
{
    public string Content { get; set; } = "";
    public string? Source { get; set; }
}

public class NovaMessageRequest
{
    public string Content { get; set; } = "";
    public string? Title { get; set; }
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
