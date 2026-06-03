using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using Nova.App.Data;
using Nova.App.Services;

namespace Nova.App.Api;

public static class DiscussionExportEndpoints
{
    public static void MapDiscussionExportEndpoints(this EndpointRegistry registry)
    {
        registry.MapGet("/api/discussions/export", "Export conversations as markdown (query: since, limit)", async (HttpContext ctx, NovaDbContext db, string? since, int? limit) =>
        {
            var userId = ctx.User.FindFirstValue("sub");
            var sinceDate = since != null
                ? DateTime.Parse(since, null, DateTimeStyles.RoundtripKind)
                : DateTime.UtcNow.AddDays(-7);

            var markdown = await ConversationExporter.ExportAsync(db, sinceDate, limit ?? 50, userId);
            return Results.Text(markdown, "text/markdown");
        });
    }
}
