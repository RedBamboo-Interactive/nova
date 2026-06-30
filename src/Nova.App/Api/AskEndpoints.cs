using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost;
using RedBamboo.AppHost.Discovery;
using Nova.App.Data;
using Nova.App.Data.Entities;
using Nova.App.Services;

namespace Nova.App.Api;

public static class AskEndpoints
{
    public static void MapAskEndpoints(this EndpointRegistry registry, NovaEngine engine)
    {
        var memory = engine.Memory;

        registry.MapPost("/api/ask",
            "Ask Nova a question and wait for the answer — the machine-friendly front door for agents and scripts. " +
            "Sends content into a discussion (creates a new one titled from the content unless discussionId is provided), " +
            "waits up to timeoutSeconds for Nova to finish thinking, and returns { ok: true, reply, discussionId, sessionId }. " +
            "On timeout returns HTTP 200 with ok: false and error.code 'timeout' — the session keeps running; " +
            "poll GET /ai-session/sessions/{sessionId} for progress or call /api/ask again with the returned discussionId to continue the thread. " +
            "Returns 502 redcompute_unavailable when the RedCompute inference backend is down.",
            async (AskRequest request, HttpContext ctx, NovaDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Content))
                return ApiError.BadRequest("missing_content", "'content' is required");

            var timeout = Math.Clamp(request.TimeoutSeconds ?? 60, 10, 300);
            var userId = ctx.User.FindFirstValue("sub");

            Discussion? discussion;
            if (!string.IsNullOrWhiteSpace(request.DiscussionId))
            {
                discussion = await db.Discussions.FindAsync(request.DiscussionId);
                if (discussion is null)
                    return ApiError.NotFound("discussion_not_found",
                        $"Discussion '{request.DiscussionId}' not found. Omit discussionId to create a new one.");

                if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                    return ApiError.Forbidden("forbidden", "You do not have access to this discussion");
            }
            else
            {
                discussion = new Discussion
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Status = "idle",
                    OwnerId = userId,
                    AgentId = NovaMirror.AgentId,
                    Title = TitleFrom(request.Content),
                };
                db.Discussions.Add(discussion);
                await db.SaveChangesAsync();
            }

            // Baseline message count so we only consider assistant text produced after this send.
            var baseline = 0;
            if (discussion.SessionId is not null)
            {
                var pre = await ConversationExporter.FetchSessionAsync(discussion.SessionId);
                baseline = pre?.Messages.Count ?? 0;
            }

            var outcome = await DiscussionEndpoints.SendMessageCoreAsync(
                db, memory, discussion, userId, request.Content, images: null,
                device: new ResolvedDevice { Name = "API", Type = "api", Platform = "api" }, input: "api");

            if (!outcome.Success)
                return ApiError.BadGateway(
                    outcome.ErrorCode ?? "redcompute_unavailable",
                    outcome.ErrorMessage ?? "RedCompute is unavailable");

            var sessionId = outcome.SessionId!;

            discussion.LastActivity = DateTime.UtcNow;
            discussion.MessageCount++;
            await db.SaveChangesAsync();

            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            var sawRunning = false;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ctx.RequestAborted);

                var snapshot = await ConversationExporter.FetchSessionAsync(sessionId);
                if (snapshot is null) continue; // transient RedCompute hiccup — keep waiting

                if (snapshot.Status is "Active" or "Starting")
                {
                    sawRunning = true;
                    continue;
                }

                var reply = ExtractReply(snapshot.Messages, baseline);
                if (reply is not null)
                    return Results.Ok(new { ok = true, reply, discussionId = discussion.Id, sessionId });

                if (sawRunning || snapshot.Status is "Stopped" or "Error")
                    return AskError("no_reply",
                        $"The session finished (status: {snapshot.Status}) without producing a text reply.",
                        discussion.Id, sessionId);
            }

            return AskError("timeout",
                $"Nova did not reply within {timeout}s. The session may still be working — " +
                $"poll GET /ai-session/sessions/{sessionId} or ask again with discussionId '{discussion.Id}'.",
                discussion.Id, sessionId);
        })
        .WithRequestBody(new
        {
            type = "object",
            required = new[] { "content" },
            properties = new
            {
                content = new { type = "string", description = "The question or instruction for Nova." },
                timeoutSeconds = new { type = "integer", description = "Seconds to wait for the reply. Clamped to 10-300.", @default = 60 },
                discussionId = new { type = "string", description = "Existing discussion to continue. When omitted a new discussion is created, titled from the content." },
            },
        })
        .WithResponse(new
        {
            type = "object",
            properties = new
            {
                ok = new { type = "boolean" },
                reply = new { type = "string", description = "Nova's final assistant text. Present when ok is true." },
                discussionId = new { type = "string" },
                sessionId = new { type = "string" },
                error = new
                {
                    type = "object",
                    description = "Present when ok is false. code is 'timeout' or 'no_reply'; HTTP status stays 200 " +
                                  "because the discussionId/sessionId are still usable for follow-up.",
                },
            },
        });
    }

    /// <summary>200 response with the standard error envelope plus the ids needed to follow up.</summary>
    private static IResult AskError(string code, string message, string discussionId, string sessionId)
    {
        var payload = (Dictionary<string, object?>)ApiError.Of(code, message);
        payload["discussionId"] = discussionId;
        payload["sessionId"] = sessionId;
        return Results.Ok(payload);
    }

    /// <summary>
    /// Concatenate assistant text fragments produced after the baseline into the final reply.
    /// Stream fragments are merged; separate text blocks are joined with blank lines.
    /// </summary>
    private static string? ExtractReply(IReadOnlyList<SessionMessage> messages, int baseline)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();

        for (var i = baseline; i < messages.Count; i++)
        {
            var m = messages[i];
            if (m.Role == "assistant" && m.EventType == "text" && !string.IsNullOrEmpty(m.Content))
            {
                current.Append(m.Content);
            }
            else if (current.Length > 0)
            {
                blocks.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) blocks.Add(current.ToString());

        var reply = string.Join("\n\n", blocks.Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()));
        return reply.Length > 0 ? reply : null;
    }

    private static string TitleFrom(string content)
    {
        var t = content.Trim().ReplaceLineEndings(" ");
        return t.Length > 60 ? t[..59] + "…" : t;
    }
}

public class AskRequest
{
    public string Content { get; set; } = "";
    public int? TimeoutSeconds { get; set; }
    public string? DiscussionId { get; set; }
}
