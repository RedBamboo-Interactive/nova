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

                var resp = await RedCompute.PostAsJsonAsync("/claude/sessions", new
                {
                    projectPath = memory.WorkspacePath,
                }, JsonOptions);

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
                try { await RedCompute.PostAsync($"/claude/sessions/{discussion.SessionId}/stop", null); }
                catch { /* best effort */ }
            }

            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion));
        });
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
