using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

public static class DiscussionGroupingEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/discussion-groups", ReadAsync)
            .WithSummary("Returns the caller's cached optional project grouping for open discussion rows. No inference. Rechecks discussion access; confidential and archived discussions are excluded. Canonical discussion titles/state are unchanged.");
        group.MapPost("/discussion-groups/refresh", RefreshAsync)
            .WithSummary("Refreshes the caller's derived discussion project groups using the configured Fast model when eligible titles change. Bounded, coalesced, tool-free background work relative to the UI; no discussion/session mutation. Returns the cached view on model failure with retryAfterSeconds.");
        group.MapPatch("/discussion-groups/corrections", CorrectAsync)
            .WithSummary("Save the caller explicit sidebar group corrections for eligible discussions. Body: array of discussionId and group; null group leaves a row ungrouped. Does not change discussion records or start inference.");
    }

    internal static async Task<IResult> CorrectAsync(HttpContext context, DiscussionGrouping grouping, DiscussionGroupCorrection[] corrections)
    {
        var subject = DiscussionGrouping.Subject(context);
        if (subject is null) return Results.Unauthorized();
        return await grouping.CorrectAsync(subject, context, corrections, context.RequestAborted)
            ? Results.NoContent() : Results.BadRequest(new { error = "invalid_grouping_correction" });
    }

    internal static async Task<IResult> ReadAsync(HttpContext context, DiscussionGrouping grouping)
    {
        var subject = DiscussionGrouping.Subject(context);
        return subject is null ? Results.Unauthorized()
            : Results.Ok(await grouping.ReadAsync(subject, context, context.RequestAborted));
    }

    internal static async Task<IResult> RefreshAsync(HttpContext context, DiscussionGrouping grouping)
    {
        var subject = DiscussionGrouping.Subject(context);
        return subject is null ? Results.Unauthorized()
            : Results.Ok(await grouping.RefreshAsync(subject, context, context.RequestAborted));
    }
}
