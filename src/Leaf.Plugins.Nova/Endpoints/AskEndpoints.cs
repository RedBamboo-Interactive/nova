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
        group.MapPost("/ask", async (AskRequest request, HttpContext ctx, DiscussionStore store, AgentDirectory agents, MessagePipeline pipeline, RedComputeClient redCompute, EventInjector injector) =>
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

                if (!DiscussionAccessPolicy.CanRead(discussion, ctx))
                    return discussion.Confidential
                        ? Results.Json(new { error = "Discussion not found", code = "discussion_not_found" }, statusCode: 404)
                        : Results.Json(new { error = "You do not have access to this discussion", code = "forbidden" }, statusCode: 403);
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

            // An Agent instance asking is a note, not a person talking. It still needs its answer, so
            // unlike /event this asks for a session; only the way the words arrive differs.
            var noteSender = AsksAsNote(ctx.User)
                ? await EventSender.ResolveAsync(ctx.User, store, discussion, ctx.RequestAborted)
                : null;
            var outcome = noteSender is not null
                ? await AskAsNoteAsync(discussion, request.Content, noteSender, userId, timeout,
                    NovaComputeProvenance.RequestCause(ctx, discussion.OwnerId), store, pipeline, injector, ctx.RequestAborted,
                    DelegateEndpoints.CallerSessionId(ctx.User))
                : await pipeline.SendAsync(
                    discussion, userId, request.Content, images: null,
                    device: new ResolvedDevice { Name = "API", Type = "api", Platform = "api" }, input: "api",
                    humanInitiated: DiscussionAccessPolicy.IsHumanInitiated(ctx.User),
                    cause: NovaComputeProvenance.RequestCause(ctx, discussion.OwnerId),
                    callerSessionId: DelegateEndpoints.CallerSessionId(ctx.User));

            if (noteSender is not null)
                await SentNote.RecordAsync(ctx.User, store, injector, discussion, request.Content, outcome.MessageUid,
                    !outcome.Success, userId, ctx.RequestAborted);

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
        })
            .WithSummary("Sends content to Nova and waits for the reply, returning it in one response. Body: content (required), discussionId (omit to start a new discussion), timeoutSeconds (10 to 300, default 60); on timeout it returns ok false with the ids to follow up. From an Agent writing out of a discussion it arrives as a note the server attributes to that Agent, not as a person's message.");
    }

    /// <summary>
    /// An Agent instance writes out of a discussion. A Flow node run for an Agent has no discussion
    /// in its identity: its message is the turn's prompt and still needs Nova's context, so it stays
    /// an ordinary send.
    /// </summary>
    internal static bool AsksAsNote(ClaimsPrincipal principal)
        => DiscussionAccessPolicy.ReadSignedActor(principal) is { IsAgent: true, DiscussionId: not null };

    internal static async Task<SendMessageOutcome> AskAsNoteAsync(
        DiscussionRead discussion, string content, EventSender sender, string? userId, int timeoutSeconds,
        string? cause, DiscussionStore store, MessagePipeline pipeline, EventInjector injector, CancellationToken ct,
        string? callerSessionId = null)
    {
        if (DiscussionMutationPolicy.RejectSend(discussion))
            return new(false, discussion.SessionId, "closed", "Archived discussions are read-only.");
        if (DiscussionApplicationOwner.IsForeign(discussion))
            return new(false, discussion.SessionId, DiscussionApplicationOwner.ErrorCode,
                DiscussionApplicationOwner.Message(discussion));

        var result = await injector.InjectEventAsync(discussion, content, null, EventSender.DefaultSource,
            null, null, null, userId, null, ct, sender);

        // Started after the note is queued, so its creation drains the note.
        if (discussion.SessionId is null) pipeline.BeginSessionCreation(discussion, cause, callerSessionId);
        if (result.Delivery == EventDelivery.Queued && result.MessageUid is { } uid)
        {
            if (pipeline.PendingSessionCreation(discussion.Id) is { } creation)
            {
                try { await creation.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), ct); }
                catch (TimeoutException) { }
            }
            await injector.DeliverPendingAsync(discussion.EntityId, ct);
            result = await injector.QueuedStateAsync(discussion.EntityId, uid, ct);
        }

        if (result.Delivery is not (EventDelivery.Delivered or EventDelivery.Reused))
            return new(false, discussion.SessionId, result.ErrorCode ?? "event_delivery_failed",
                result.ErrorMessage ?? "The AI session did not take the note.");

        var current = await store.GetAsync(discussion.EntityId, ct);
        return new(true, current?.SessionId ?? discussion.SessionId, null, null, MessageUid: result.MessageUid);
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

        var reply = string.Join("\n\n", blocks
            .Select(b => NovaNoReply.WithoutTrailingMarker(b).Trim())
            .Where(b => b.Length > 0));
        return reply.Length > 0 ? reply : null;
    }

    private static string TitleFrom(string content)
    {
        var t = content.Trim().ReplaceLineEndings(" ");
        return t.Length > 60 ? t[..59] + "…" : t;
    }
}
