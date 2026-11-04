using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// The person's rewind of a discussion. RedCompute decides whether it can happen and does it; this
/// route adds the access check and then corrects the discussion's own record. A refusal passes
/// through unchanged, so a client shows RedCompute's own words and reasons. Only a person may do this.
/// The call to RedCompute passes no provenance on purpose: Nova's provenance always names the Agent,
/// and the kernel's gateway instead derives the identity from the person's own browser token, which
/// is how RedCompute can tell a person asked.
/// </summary>
public static class DiscussionRollbackEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record RollbackBody(string? MessageUid);

    private sealed record Applied(
        string? RollbackId,
        string? MessageUid,
        List<string>? RemovedMessageUids,
        string? RestoredText,
        DateTimeOffset? FromCreatedAt,
        DateTimeOffset? RolledBackAt,
        bool AlreadyRolledBack);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/discussions/{id}/rollback", (string id, [FromBody] RollbackBody? body, HttpContext ctx,
                DiscussionStore store, RedComputeClient redCompute, LiveEvents live,
                [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
                RollbackAsync(id, body, ctx, store, redCompute, events, live.ForgetQuotesAsync))
            .WithSummary("Rewinds a discussion to just before one of the person's messages, named by messageUid. That message and everything after it leave the conversation for the agent and every client, and stay stored. Files are never changed. Only when the session reports supportsRollback and is idle: RedCompute's refusal comes back unchanged, with `reasons` for 409 rollback_not_idle. A discussion without a session answers 409 rollback_unsupported. 503 rollback_incomplete is safe to repeat, and a repeat changes nothing that already happened. Returns restoredText for the composer and the updated discussion. Clients hear `discussion.rolled_back`. Never automatic.");
    }

    internal static async Task<IResult> RollbackAsync(string id, RollbackBody? body, HttpContext ctx,
        DiscussionStore store, RedComputeClient redCompute, IPluginEvents events,
        Func<string, DiscussionRollback, CancellationToken, Task> forgetQuotes)
    {
        var ct = ctx.RequestAborted;
        var discussion = await store.GetAsync(id);
        if (discussion is null) return DiscussionEndpoints.NotFound();
        if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return DiscussionEndpoints.AccessDenied(discussion);
        if (!DiscussionAccessPolicy.IsPersonAtBrowser(ctx.User))
            return Results.Json(new
            {
                error = "rollback_requires_person",
                message = "Rewinding a discussion is a person's action, from the message menu in a browser. An Agent, a service or a request with no browser identity cannot do it.",
                reasons = Array.Empty<string>(),
            }, statusCode: StatusCodes.Status403Forbidden);
        if (DiscussionApplicationOwner.IsForeign(discussion)) return DiscussionApplicationOwner.Refusal(discussion);
        if (DiscussionMutationPolicy.RejectSend(discussion)) return DiscussionEndpoints.Closed();

        if (string.IsNullOrWhiteSpace(body?.MessageUid))
            return Results.Json(new { error = "invalid_request", message = "messageUid is required" }, statusCode: 400);
        if (string.IsNullOrWhiteSpace(discussion.SessionId))
            return Results.Json(new
            {
                error = "rollback_unsupported",
                message = "This discussion has no session to rewind.",
                reasons = Array.Empty<string>(),
            }, statusCode: 409);

        var result = await redCompute.RollbackSessionAsync(discussion.SessionId, body.MessageUid, ct);
        if (result.StatusCode is < 200 or >= 300)
            return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);

        var applied = Parse(result.Content);
        if (applied?.RollbackId is null)
            return Results.Json(new { error = "redcompute_unavailable", message = "RedCompute gave an unreadable answer." }, statusCode: 502);

        // Everything from here repeats safely: RedCompute answers a repeat with the same rollback.
        DiscussionRead? updated;
        try
        {
            var kept = await redCompute.GetSessionAsync(discussion.SessionId, ct);
            var rollback = new DiscussionRollback(
                applied.RollbackId, applied.RemovedMessageUids ?? [], applied.FromCreatedAt, applied.RolledBackAt);
            updated = await store.ApplyRollbackAsync(discussion.EntityId, rollback, kept?.Messages, ct);
            await forgetQuotes(id, rollback, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return Results.Json(new
            {
                error = "rollback_incomplete",
                message = "The agent has rewound, but this discussion's record has not caught up. Try again to finish it.",
                reasons = Array.Empty<string>(),
            }, statusCode: 503);
        }

        await events.PublishAsync("discussion.rolled_back", new JsonObject { ["discussionId"] = id }, ct);

        return Results.Ok(new
        {
            rolledBack = true,
            messageUid = applied.MessageUid,
            rollbackId = applied.RollbackId,
            removedMessageUids = applied.RemovedMessageUids ?? [],
            restoredText = applied.RestoredText,
            alreadyRolledBack = applied.AlreadyRolledBack,
            filesChanged = false,
            discussion = DiscussionStore.ToInfo(updated ?? discussion),
        });
    }

    private static Applied? Parse(string content)
    {
        try { return JsonSerializer.Deserialize<Applied>(content, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
