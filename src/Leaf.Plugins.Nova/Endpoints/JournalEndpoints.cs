using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>The Journal tab's read model: what an Agent did through a day. Reads only.</summary>
public static class JournalEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/journal", GetDayAsync)
            .WithSummary("Returns one Agent's journal day, 04:00 to 04:00 in its zone, computed on read: header, summary, entries (threads, ticks, automations) with their links, sessions, jobs, artifacts and commits, and counts per filter. Query: agent (id or slug, default Nova's Agent), day (YYYY-MM-DD, default the current journal day). Confidential discussions and jobs are left out entirely. partial is true, with what was unavailable, when a source couldn't be read.");

        group.MapGet("/journal/days", GetDaysAsync)
            .WithSummary("Returns the calendar for one Agent's month: each day with activity, and whether it has own, with-you or failed work. Query: agent (id or slug, default Nova's Agent), month (YYYY-MM, default the current month).");
    }

    internal static async Task<IResult> GetDayAsync(HttpContext ctx, JournalReadModel journal)
    {
        try
        {
            var day = await journal.ReadDayAsync(
                ctx.Request.Query["agent"].FirstOrDefault(), ctx.Request.Query["day"].FirstOrDefault(),
                ctx, ctx.RequestAborted);
            return Results.Json(day, JournalJson.Options);
        }
        catch (JournalRequestException ex) { return Refusal(ex); }
    }

    internal static async Task<IResult> GetDaysAsync(HttpContext ctx, JournalReadModel journal)
    {
        try
        {
            var month = await journal.ReadMonthAsync(
                ctx.Request.Query["agent"].FirstOrDefault(), ctx.Request.Query["month"].FirstOrDefault(),
                ctx, ctx.RequestAborted);
            return Results.Json(month, JournalJson.Options);
        }
        catch (JournalRequestException ex) { return Refusal(ex); }
    }

    private static IResult Refusal(JournalRequestException ex)
        => Results.Json(new { error = ex.Code, message = ex.Message }, statusCode: ex.Status);
}
