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
            var heartbeats = engine.Heartbeat?.GetAll() ?? [];
            return Results.Ok(new
            {
                heartbeats = heartbeats.Where(h => !h.Cancelled).Select(h => new
                {
                    name = h.Name,
                    description = h.Description,
                    intervalMinutes = h.IntervalMinutes,
                    lastRun = h.LastRun,
                })
            });
        });

        app.MapPost("/api/heartbeats", (HeartbeatCreateRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "Name and prompt are required" });

            engine.Heartbeat?.AddHeartbeat(new HeartbeatDefinition
            {
                Name = request.Name,
                Description = request.Description,
                Prompt = request.Prompt,
                IntervalMinutes = request.IntervalMinutes,
            });
            return Results.Ok(new { success = true, name = request.Name });
        });

        app.MapDelete("/api/heartbeats/{name}", (string name) =>
        {
            engine.Heartbeat?.RemoveHeartbeat(name);
            return Results.Ok(new { success = true });
        });

        // --- Scheduled Tasks ---

        app.MapGet("/api/schedule", () =>
        {
            var tasks = engine.Scheduler?.GetAll() ?? [];
            return Results.Ok(new
            {
                tasks = tasks.Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    nextRun = t.NextRun,
                    lastRun = t.LastRun,
                    recurring = t.Recurring,
                    enabled = t.Enabled,
                })
            });
        });

        app.MapPost("/api/schedule", (ScheduleCreateRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "Name and prompt are required" });

            engine.Scheduler?.AddTask(new ScheduledTask
            {
                Name = request.Name,
                Description = request.Description,
                Prompt = request.Prompt,
                NextRun = request.NextRun ?? DateTime.UtcNow,
                Recurring = request.Recurring,
                CronExpression = request.CronExpression,
                IntervalMinutes = request.IntervalMinutes,
                Enabled = true,
            });
            return Results.Ok(new { success = true, name = request.Name });
        });

        app.MapDelete("/api/schedule/{name}", (string name) =>
        {
            var removed = engine.Scheduler?.RemoveTask(name) ?? false;
            return Results.Ok(new { success = removed });
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
    public string? CronExpression { get; set; }
    public int IntervalMinutes { get; set; }
}
