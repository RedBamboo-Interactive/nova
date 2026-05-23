using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nova.App.Services;

namespace Nova.App.Api;

public static class ScheduleEndpoints
{
    public static void MapScheduleEndpoints(this IEndpointRouteBuilder app, NovaEngine engine)
    {
        // --- Heartbeats ---

        app.MapGet("/api/heartbeats", () =>
        {
            // HeartbeatService is internal to engine; we'll expose via a method
            return Results.Ok(new { heartbeats = Array.Empty<object>() });
        });

        app.MapPost("/api/heartbeats", (HeartbeatCreateRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "Name and prompt are required" });

            // TODO: expose heartbeat add through engine
            return Results.Ok(new { success = true, name = request.Name });
        });

        app.MapDelete("/api/heartbeats/{name}", (string name) =>
        {
            // TODO: expose heartbeat remove through engine
            return Results.Ok(new { success = true });
        });

        // --- Scheduled Tasks ---

        app.MapGet("/api/schedule", () =>
        {
            return Results.Ok(new { tasks = Array.Empty<object>() });
        });

        app.MapPost("/api/schedule", (ScheduleCreateRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "Name and prompt are required" });

            // TODO: expose scheduler add through engine
            return Results.Ok(new { success = true, name = request.Name });
        });

        app.MapDelete("/api/schedule/{name}", (string name) =>
        {
            // TODO: expose scheduler remove through engine
            return Results.Ok(new { success = true });
        });
    }
}

public class HeartbeatCreateRequest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int IntervalMinutes { get; set; } = 60;
}

public class ScheduleCreateRequest
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Prompt { get; set; } = "";
    public DateTime? NextRun { get; set; }
    public bool Recurring { get; set; }
    public int IntervalMinutes { get; set; }
}
