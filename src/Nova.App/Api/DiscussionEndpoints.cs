using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
        BaseAddress = new Uri("http://localhost:18800"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static void Initialize(AuthenticatedHttpClientFactory factory)
    {
        RedCompute = factory.CreateClient("http://localhost:18800", TimeSpan.FromSeconds(30));
    }

    public static void MapDiscussionEndpoints(this EndpointRegistry registry, NovaEngine engine)
    {
        var memory = engine.Memory;

        registry.MapGet("/api/discussions", "List discussions (filter: status, search)", async (HttpContext ctx, NovaDbContext db) =>
        {
            var status = ctx.Request.Query["status"].FirstOrDefault();
            var search = ctx.Request.Query["search"].FirstOrDefault();

            IQueryable<Discussion> query = db.Discussions.OrderByDescending(d => d.LastActivity);

            if (!string.IsNullOrEmpty(status))
                query = query.Where(d => d.Status == status);
            else
                query = query.Where(d => d.Status != "archived");

            if (!string.IsNullOrEmpty(search))
                query = query.Where(d => d.Title != null && d.Title.Contains(search));

            var userId = ctx.User.FindFirstValue("sub");
            if (userId != null && userId != "local-user")
                query = query.Where(d => d.OwnerId == null || d.OwnerId == "local-user" || d.OwnerId == userId);

            var discussions = await query.ToListAsync();
            return Results.Ok(discussions.Select(ToInfo));
        });

        registry.MapGet("/api/discussions/pending", "Count unread discussions", async (HttpContext ctx, NovaDbContext db) =>
        {
            IQueryable<Discussion> pendingQuery = db.Discussions
                .Where(d => d.Status != "archived" && d.SessionId != null);

            var userId = ctx.User.FindFirstValue("sub");
            if (userId != null && userId != "local-user")
                pendingQuery = pendingQuery.Where(d => d.OwnerId == null || d.OwnerId == "local-user" || d.OwnerId == userId);

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
            if (userId != null && userId != "local-user")
                syncQuery = syncQuery.Where(d => d.OwnerId == null || d.OwnerId == "local-user" || d.OwnerId == userId);

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

        registry.MapPost("/api/discussions", "Create a new discussion (starts a session)", async (HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = new Discussion
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Status = "idle",
                OwnerId = ctx.User.FindFirstValue("sub"),
            };

            try
            {
                memory.GenerateClaudeMd();

                var req = new HttpRequestMessage(HttpMethod.Post, "/ai-session/sessions")
                {
                    Content = JsonContent.Create(new { projectPath = memory.WorkspacePath }, options: JsonOptions),
                };
                req.Headers.Add("X-Caller-Info", "Nova");
                if (discussion.OwnerId != null)
                    req.Headers.Add("X-User-Id", discussion.OwnerId);
                var resp = await RedCompute.SendAsync(req);

                if (resp.IsSuccessStatusCode)
                {
                    var session = await resp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
                    if (session.TryGetProperty("id", out var idProp))
                        discussion.SessionId = idProp.GetString();
                }
            }
            catch
            {
                // RedCompute unavailable — discussion created without session
            }

            db.Discussions.Add(discussion);
            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion));
        });

        registry.MapGet("/api/discussions/{id}", "Get discussion metadata", async (string id, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            return Results.Ok(new { discussion = ToInfo(discussion) });
        });

        registry.MapPut("/api/discussions/{id}/title", "Update discussion title", async (string id, DiscussionTitleRequest request, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
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
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
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
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
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
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (discussion.Status is not "archived")
                discussion.Status = "stopped";

            await db.SaveChangesAsync();
            return Results.Ok(ToInfo(discussion));
        });

        registry.MapDelete("/api/discussions/{id}", "Archive a discussion", async (string id, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            discussion.Status = "archived";

            if (discussion.SessionId is not null)
            {
                try { await RedCompute.PostAsync($"/ai-session/sessions/{discussion.SessionId}/stop", null); }
                catch { /* best effort */ }
            }

            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion));
        });
        registry.MapGet("/api/discussions/search", "Search conversation content (query: q, limit)", async (HttpContext ctx, NovaDbContext db) =>
        {
            var q = ctx.Request.Query["q"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "Query parameter 'q' is required" });

            var limit = 20;
            if (ctx.Request.Query.TryGetValue("limit", out var lv) && int.TryParse(lv, out var parsed))
                limit = Math.Clamp(parsed, 1, 100);

            var snippetLen = 120;

            var matches = await db.Conversations
                .Where(m => m.Content.Contains(q) || (m.PartsJson != null && m.PartsJson.Contains(q)))
                .OrderByDescending(m => m.Timestamp)
                .Select(m => new { m.ContextId, m.Role, m.Content, m.PartsJson, m.Timestamp })
                .ToListAsync();

            var grouped = matches
                .GroupBy(m => m.ContextId)
                .Take(limit)
                .ToList();

            var discussionIds = grouped.Select(g => g.Key).ToList();
            IQueryable<Discussion> searchDiscQuery = db.Discussions
                .Where(d => discussionIds.Contains(d.Id));

            var userId = ctx.User.FindFirstValue("sub");
            if (userId != null && userId != "local-user")
                searchDiscQuery = searchDiscQuery.Where(d => d.OwnerId == null || d.OwnerId == "local-user" || d.OwnerId == userId);

            var discussions = await searchDiscQuery.ToDictionaryAsync(d => d.Id);

            grouped = grouped.Where(g => discussions.ContainsKey(g.Key)).ToList();

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
        });

        registry.MapPost("/api/discussions/{id}/event", "Inject an automation event into a discussion", async (string id, DiscussionEventRequest request, HttpContext ctx, NovaDbContext db, WebSocketBroadcaster? broadcaster) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
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
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (discussion.SessionId is null)
                return Results.BadRequest(new { error = "Discussion has no active session" });

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

            try
            {
                var resp = await RedCompute.PostAsJsonAsync(
                    $"/ai-session/sessions/{discussion.SessionId}/inject",
                    new { role = "assistant", content = request.Content }, JsonOptions);
                resp.EnsureSuccessStatusCode();
            }
            catch
            {
                return Results.StatusCode(502);
            }

            discussion.LastActivity = DateTime.UtcNow;
            discussion.MessageCount++;
            discussion.InjectedContext = request.Content;
            await db.SaveChangesAsync();

            broadcaster?.Broadcast("discussion.nova-message", new
            {
                discussionId = id,
                sessionId = discussion.SessionId,
                content = request.Content,
            });

            if (!string.IsNullOrWhiteSpace(request.Title))
            {
                discussion.Title = request.Title;
                await db.SaveChangesAsync();
            }

            return Results.Ok(new { success = true });
        });

        registry.MapPost("/api/discussions/{id}/message", "Send a message to a discussion (enriches with cross-discussion context)", async (string id, DiscussionMessageRequest request, HttpContext ctx, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (discussion.OwnerId != null && discussion.OwnerId != "local-user" && userId != "local-user" && discussion.OwnerId != userId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            if (discussion.SessionId is null)
                return Results.BadRequest(new { error = "Discussion has no active session" });

            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Content is required" });

            var now = DateTime.UtcNow;
            var cutoff = now.AddDays(-2);

            IQueryable<Discussion> contextQuery = db.Discussions
                .Where(d => d.Status != "archived" || (d.Status == "archived" && d.LastActivity >= cutoff));

            if (userId != null && userId != "local-user")
                contextQuery = contextQuery.Where(d => d.OwnerId == null || d.OwnerId == "local-user" || d.OwnerId == userId);

            var allDiscussions = await contextQuery
                .OrderByDescending(d => d.LastActivity)
                .ToListAsync();

            var ua = ctx.Request.Headers.UserAgent.ToString();
            var device = System.Text.RegularExpressions.Regex.IsMatch(ua, @"Mobile|Android|iPhone|iPad|iPod", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                ? "mobile" : "desktop";

            var input = request.InputMethod ?? "typed";
            var contextBlock = BuildNovaContext(allDiscussions, id, now, device, input);

            var injected = discussion.InjectedContext;
            var enrichedContent = contextBlock + "\n\n";
            if (injected != null)
            {
                enrichedContent += "<nova-prior-messages hint=\"You said this earlier in the conversation. The user can see it but it was injected before your session started.\">\n"
                    + injected
                    + "\n</nova-prior-messages>\n\n";
            }
            enrichedContent += request.Content;

            try
            {
                var resp = await RedCompute.PostAsJsonAsync(
                    $"/ai-session/sessions/{discussion.SessionId}/message",
                    new { content = enrichedContent, images = request.Images }, JsonOptions);
                resp.EnsureSuccessStatusCode();

                if (injected != null)
                {
                    discussion.InjectedContext = null;
                    await db.SaveChangesAsync();
                }

                return Results.Ok(new { success = true });
            }
            catch
            {
                return Results.StatusCode(502);
            }
        });
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
