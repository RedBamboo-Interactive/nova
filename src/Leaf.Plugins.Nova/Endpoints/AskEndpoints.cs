using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

public class AskRequest
{
    public string Content { get; set; } = "";
    public int? TimeoutSeconds { get; set; }
    public string? DiscussionId { get; set; }
}

/// <summary>
/// POST /ask — the machine-friendly front door for agents and scripts: send content
/// into a discussion, wait for Nova to finish thinking, return the reply.
/// </summary>
public static class AskEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/ask", async (AskRequest request, HttpContext ctx, DiscussionStore store, AgentDirectory agents, MessagePipeline pipeline, RedComputeClient redCompute) =>
        {
            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "'content' is required", code = "missing_content" });

            var timeout = Math.Clamp(request.TimeoutSeconds ?? 60, 10, 300);
            var userId = ctx.User.FindFirstValue("sub");

            DiscussionRead? discussion;
            if (!string.IsNullOrWhiteSpace(request.DiscussionId))
            {
                discussion = await store.GetAsync(request.DiscussionId);
                if (discussion is null)
                    return Results.NotFound(new { error = $"Discussion '{request.DiscussionId}' not found. Omit discussionId to create a new one.", code = "discussion_not_found" });

                if (!OwnerScope.CanAccess(discussion.OwnerId, userId))
                    return Results.Json(new { error = "You do not have access to this discussion", code = "forbidden" }, statusCode: 403);
            }
            else
            {
                discussion = await store.CreateAsync(TitleFrom(request.Content), agents.NovaAgentId, userId);
            }

            // Baseline message count so we only consider assistant text produced after this send.
            var baseline = 0;
            if (discussion.SessionId is not null)
            {
                var pre = await redCompute.GetSessionAsync(discussion.SessionId);
                baseline = pre?.Messages.Count ?? 0;
            }

            var outcome = await pipeline.SendAsync(
                discussion, userId, request.Content, images: null,
                device: new ResolvedDevice { Name = "API", Type = "api", Platform = "api" }, input: "api");

            if (!outcome.Success)
                return Results.Json(new { error = outcome.ErrorMessage ?? "RedCompute is unavailable", code = outcome.ErrorCode ?? "redcompute_unavailable" }, statusCode: 502);

            var sessionId = outcome.SessionId!;

            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            var sawRunning = false;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ctx.RequestAborted);

                var snapshot = await redCompute.GetSessionAsync(sessionId);
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
        });
    }

    /// <summary>200 response with the error envelope plus the ids needed to follow up.</summary>
    private static IResult AskError(string code, string message, string discussionId, string sessionId)
        => Results.Ok(new
        {
            ok = false,
            error = new { code, message },
            discussionId,
            sessionId,
        });

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
