using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private static readonly HttpClient RedCompute = new()
    {
        BaseAddress = new Uri("http://localhost:18800"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static void MapDiscussionEndpoints(this IEndpointRouteBuilder app, NovaEngine engine)
    {
        var memory = engine.Memory;

        app.MapGet("/api/discussions", async (HttpContext ctx, NovaDbContext db) =>
        {
            var status = ctx.Request.Query["status"].FirstOrDefault();
            var search = ctx.Request.Query["search"].FirstOrDefault();

            IQueryable<Discussion> query = db.Discussions.OrderByDescending(d => d.LastActivity);

            if (!string.IsNullOrEmpty(status))
                query = query.Where(d => d.Status == status);

            if (!string.IsNullOrEmpty(search))
                query = query.Where(d => d.Title != null && d.Title.Contains(search));

            var discussions = await query.ToListAsync();
            return Results.Ok(discussions.Select(ToInfo));
        });

        app.MapGet("/api/discussions/pending", async (NovaDbContext db) =>
        {
            var count = await db.Discussions
                .Where(d => d.Status == "idle" && d.MessageCount > 0)
                .CountAsync();

            return Results.Ok(new { count });
        });

        app.MapPost("/api/discussions", async (NovaDbContext db) =>
        {
            var discussion = new Discussion
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Status = "idle",
            };

            try
            {
                memory.GenerateClaudeMd();

                var req = new HttpRequestMessage(HttpMethod.Post, "/ai-session/sessions")
                {
                    Content = JsonContent.Create(new { projectPath = memory.WorkspacePath }, options: JsonOptions),
                };
                req.Headers.Add("X-Caller-Info", "Nova");
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

        app.MapGet("/api/discussions/{id}", async (string id, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            return Results.Ok(new { discussion = ToInfo(discussion) });
        });

        app.MapPut("/api/discussions/{id}/title", async (string id, DiscussionTitleRequest request, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            discussion.Title = request.Title;
            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion));
        });

        app.MapDelete("/api/discussions/{id}", async (string id, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            discussion.Status = "archived";

            if (discussion.SessionId is not null)
            {
                try { await RedCompute.PostAsync($"/ai-session/sessions/{discussion.SessionId}/stop", null); }
                catch { /* best effort */ }
            }

            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion));
        });
        app.MapGet("/api/discussions/search", async (HttpContext ctx, NovaDbContext db) =>
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
            var discussions = await db.Discussions
                .Where(d => discussionIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id);

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
