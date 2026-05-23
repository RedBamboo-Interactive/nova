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

    public static void MapDiscussionEndpoints(this IEndpointRouteBuilder app, NovaEngine engine)
    {
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

            var result = discussions.Select(d => ToInfo(d, engine));
            return Results.Ok(result);
        });

        app.MapPost("/api/discussions", async (NovaDbContext db) =>
        {
            var discussion = new Discussion
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Status = "idle",
            };

            db.Discussions.Add(discussion);
            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion, engine));
        });

        app.MapGet("/api/discussions/{id}", async (string id, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            var messages = await db.Conversations
                .Where(m => m.ContextId == id)
                .OrderBy(m => m.Timestamp)
                .ToListAsync();

            return Results.Ok(new
            {
                discussion = ToInfo(discussion, engine),
                messages = messages.Select(ToMessageDto),
            });
        });

        app.MapPut("/api/discussions/{id}/title", async (string id, DiscussionTitleRequest request, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            discussion.Title = request.Title;
            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion, engine));
        });

        app.MapDelete("/api/discussions/{id}", async (string id, NovaDbContext db) =>
        {
            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            discussion.Status = "archived";
            await db.SaveChangesAsync();

            return Results.Ok(ToInfo(discussion, engine));
        });

        app.MapPost("/api/discussions/{id}/send", async (string id, DiscussionSendRequest request, NovaDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Message is required" });

            var discussion = await db.Discussions.FindAsync(id);
            if (discussion is null)
                return Results.NotFound(new { error = "Discussion not found" });

            if (discussion.Status == "archived")
                return Results.BadRequest(new { error = "Discussion is archived" });

            db.Conversations.Add(new ConversationRecord
            {
                ContextId = id,
                Role = "user",
                Content = request.Message,
            });
            discussion.MessageCount++;
            discussion.LastActivity = DateTime.UtcNow;
            discussion.Status = "thinking";
            await db.SaveChangesAsync();

            try
            {
                var response = await engine.ChatAsync(id, request.Message, ct);

                var parts = new List<object>();
                if (response.ToolCalls is { Count: > 0 })
                {
                    foreach (var tc in response.ToolCalls)
                    {
                        parts.Add(new { type = "tool_use", content = tc.Output ?? "", toolName = tc.Name, toolInput = tc.Input ?? "" });
                        if (tc.Output != null)
                            parts.Add(new { type = "tool_result", content = tc.Output, toolName = tc.Name });
                    }
                }
                parts.Add(new { type = "text", content = response.Text });

                db.Conversations.Add(new ConversationRecord
                {
                    ContextId = id,
                    Role = "assistant",
                    Content = response.Text,
                    PartsJson = JsonSerializer.Serialize(parts, JsonOptions),
                });
                discussion.MessageCount++;
                discussion.LastActivity = DateTime.UtcNow;
                discussion.Status = "idle";

                if (discussion.Title is null && discussion.MessageCount <= 2)
                    discussion.Title = Truncate(request.Message, 60);

                await db.SaveChangesAsync();

                return Results.Ok(new
                {
                    text = response.Text,
                    discussionId = id,
                    title = discussion.Title,
                    toolCalls = response.ToolCalls,
                });
            }
            catch (Exception ex)
            {
                discussion.Status = "idle";
                await db.SaveChangesAsync();
                return Results.Problem(ex.Message);
            }
        });
    }

    private static object ToInfo(Discussion d, NovaEngine engine)
    {
        var ctx = engine.GetContext(d.Id);
        var liveStatus = ctx?.Status switch
        {
            ConversationStatus.Thinking => "thinking",
            ConversationStatus.ToolUse => "thinking",
            _ => d.Status,
        };

        return new
        {
            d.Id,
            d.Title,
            status = liveStatus,
            d.CreatedAt,
            d.LastActivity,
            d.MessageCount,
        };
    }

    private static object ToMessageDto(ConversationRecord m)
    {
        List<object>? parts = null;

        if (m.PartsJson is not null)
        {
            try
            {
                parts = JsonSerializer.Deserialize<List<object>>(m.PartsJson, JsonOptions);
            }
            catch { }
        }

        parts ??= [new { type = "text", content = m.Content }];

        return new
        {
            id = m.Id.ToString(),
            m.Role,
            parts,
            timestamp = m.Timestamp.ToString("o"),
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[..(maxLength - 1)] + "…";
    }
}

public class DiscussionSendRequest
{
    public string Message { get; set; } = "";
}

public class DiscussionTitleRequest
{
    public string? Title { get; set; }
}
