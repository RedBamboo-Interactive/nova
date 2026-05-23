using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nova.App.Data;
using Nova.App.Services;

namespace Nova.App.Api;

public static class DiscussionExportEndpoints
{
    public static void MapDiscussionExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/discussions/export", async (NovaDbContext db, string? since, int? limit) =>
        {
            var sinceDate = since != null
                ? DateTime.Parse(since, null, DateTimeStyles.RoundtripKind)
                : DateTime.UtcNow.AddDays(-7);

            var markdown = await ConversationExporter.ExportAsync(db, sinceDate, limit ?? 50);
            return Results.Text(markdown, "text/markdown");
        });
    }
}
