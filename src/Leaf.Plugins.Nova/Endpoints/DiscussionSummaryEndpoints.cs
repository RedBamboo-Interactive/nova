using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

public static class DiscussionSummaryEndpoints
{
    public sealed record DefaultChange(bool? Enabled);
    public sealed record SummaryChange(string? ExpectedVersion, SummaryText? Text);

    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/discussion-summaries", ListAsync)
            .WithSummary("Read owner-authorized Nova discussion status projections and the per-person Fast auto-summary default. No inference.");
        group.MapGet("/discussion-summaries/settings", GetSettingsAsync)
            .WithSummary("Read the signed beneficiary's Nova auto-summary default. Off until the person opts in.");
        group.MapPut("/discussion-summaries/settings", PutSettingsAsync)
            .WithSummary("Owner-only. Persist the Nova default for discussions without an override. Body {enabled: boolean}.");
        group.MapGet("/discussions/{id}/summary", ReadAsync)
            .WithSummary("Read this authorized discussion's derived work status, next step, user action, source, evidence, effective preference, freshness and write version. No inference.");
        group.MapPut("/discussions/{id}/summary", AuthorAsync)
            .WithSummary("The person or owning signed Agent can correct the enabled summary. Body {expectedVersion, text:{status,label,overview,nextStep,userAction}}. Read GET first. Status: waiting, needs_you, in_progress, complete, unclear. A stale write returns 409 with current state. No transcript or work mutation.");
        group.MapPut("/discussions/{id}/summary/preference", OverrideAsync)
            .WithSummary("Set the per-discussion Auto-summary override as the person or owning signed Agent. Body {enabled:true|false|null}. Null follows the Nova default. No immediate inference or new turn.");
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, DiscussionStore store, DiscussionSummaries summaries)
    {
        var owner = DiscussionSummaries.Subject(ctx);
        if (owner is null) return Results.Unauthorized();
        var ct = ctx.RequestAborted;
        var defaultEnabled = await summaries.DefaultAsync(owner, ct);
        var list = (await store.ListOpenAsync(ct: ct)).Where(d => DiscussionSummaries.CanAccess(d, ctx))
            .Select(d => DiscussionSummaries.View(d, defaultEnabled));
        return Results.Ok(new { defaultEnabled, discussions = list });
    }

    private static async Task<IResult> GetSettingsAsync(HttpContext ctx, DiscussionSummaries summaries)
        => DiscussionSummaries.Subject(ctx) is { } owner
            ? Results.Ok(new { enabled = await summaries.DefaultAsync(owner, ctx.RequestAborted) })
            : Results.Unauthorized();

    private static async Task<IResult> PutSettingsAsync(HttpContext ctx, DiscussionSummaries summaries, DefaultChange change)
    {
        if (DiscussionSummaries.Subject(ctx) is not { } owner) return Results.Unauthorized();
        if (!DiscussionAccessPolicy.CanManageNovaOwnerSettings(owner, ctx) && !DiscussionAccessPolicy.IsPersonAtBrowser(ctx.User))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (change.Enabled is not { } enabled) return Results.BadRequest(new { error = "enabled_required" });
        await summaries.SetDefaultAsync(owner, enabled, ctx.RequestAborted);
        return Results.Ok(new { enabled });
    }

    private static async Task<DiscussionRead?> AuthorizedAsync(string id, HttpContext ctx, DiscussionStore store)
        => await store.GetAsync(id, ctx.RequestAborted) is { } d && DiscussionSummaries.CanAccess(d, ctx) ? d : null;

    private static async Task<IResult> ReadAsync(string id, HttpContext ctx, DiscussionStore store, DiscussionSummaries summaries)
        => await AuthorizedAsync(id, ctx, store) is { } d
            ? Results.Ok(DiscussionSummaries.View(d, await summaries.DefaultAsync(d.OwnerId!, ctx.RequestAborted)))
            : Results.NotFound();

    private static async Task<IResult> OverrideAsync(string id, HttpContext ctx, DiscussionStore store, DiscussionSummaries summaries,
        JsonElement change)
    {
        if (await AuthorizedAsync(id, ctx, store) is not { } d) return Results.NotFound();
        if (change.ValueKind != JsonValueKind.Object || !change.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
            return Results.BadRequest(new { error = "invalid_summary_preference" });
        return await summaries.SetOverrideAsync(d, ctx, enabled.ValueKind == JsonValueKind.Null ? null : enabled.GetBoolean()) is { } view
            ? Results.Ok(view) : Results.NotFound();
    }

    private static async Task<IResult> AuthorAsync(string id, HttpContext ctx, DiscussionStore store, DiscussionSummaries summaries,
        SummaryChange change)
    {
        if (await AuthorizedAsync(id, ctx, store) is not { } d) return Results.NotFound();
        var (status, view) = await summaries.AuthorAsync(d, ctx, change.ExpectedVersion, change.Text);
        return status switch
        {
            200 => Results.Ok(view),
            409 => Results.Conflict(new { error = "summary_changed_or_disabled", current = view }),
            400 => Results.BadRequest(new { error = "invalid_summary" }),
            _ => Results.NotFound(),
        };
    }
}
