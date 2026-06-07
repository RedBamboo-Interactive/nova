using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using Nova.App.Services;

namespace Nova.App.Api;

public static class AutomationEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapAutomationEndpoints(this EndpointRegistry registry, NovaEngine engine)
    {
        registry.MapGet("/api/automations", "List all automations", (HttpContext ctx) =>
        {
            var automations = engine.Automations?.GetAll() ?? [];
            var userId = ctx.User.FindFirstValue("sub");
            if (userId != null && userId != "local-user")
                automations = automations.Where(a => a.OwnerId == null || a.OwnerId == "local-user" || a.OwnerId == userId).ToList();

            return Results.Ok(new
            {
                automations = automations.Select(a => new
                {
                    a.Name,
                    a.Description,
                    a.Schedule,
                    a.Enabled,
                    a.RemoveOnTrigger,
                    a.Icon,
                    a.ActionType,
                    actionConfig = a.ActionConfigJson != null
                        ? JsonSerializer.Deserialize<JsonElement>(a.ActionConfigJson)
                        : (JsonElement?)null,
                    a.ReportToDiscussionId,
                    a.LastRun,
                    a.NextRun,
                    lastResult = a.LastResultJson != null
                        ? JsonSerializer.Deserialize<JsonElement>(a.LastResultJson)
                        : (JsonElement?)null,
                }),
            });
        });

        registry.MapPost("/api/automations", "Create an automation", (AutomationCreateRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required" });
            if (string.IsNullOrWhiteSpace(request.Schedule))
                return Results.BadRequest(new { error = "Schedule (cron expression) is required" });
            if (string.IsNullOrWhiteSpace(request.ActionType))
                return Results.BadRequest(new { error = "ActionType is required" });

            var automation = new Automation
            {
                Name = request.Name,
                Description = request.Description ?? "",
                Schedule = request.Schedule,
                Enabled = true,
                RemoveOnTrigger = request.RemoveOnTrigger,
                Icon = request.Icon,
                ActionType = request.ActionType,
                ActionConfigJson = request.ActionConfig != null
                    ? JsonSerializer.Serialize(request.ActionConfig, JsonOptions)
                    : null,
                ReportToDiscussionId = request.ReportToDiscussionId,
                OwnerId = ctx.User.FindFirstValue("sub"),
            };

            engine.Automations?.Add(automation);
            return Results.Ok(new { success = true, name = automation.Name, nextRun = automation.NextRun });
        });

        registry.MapGet("/api/automations/{name}", "Get automation details", (string name, HttpContext ctx) =>
        {
            var a = engine.Automations?.GetAll().FirstOrDefault(x => x.Name == name);
            if (a == null) return Results.NotFound(new { error = "Automation not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (a.OwnerId != null && a.OwnerId != "local-user" && userId != "local-user" && a.OwnerId != userId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            return Results.Ok(new
            {
                a.Name,
                a.Description,
                a.Schedule,
                a.Enabled,
                a.RemoveOnTrigger,
                a.ActionType,
                actionConfig = a.ActionConfigJson != null
                    ? JsonSerializer.Deserialize<JsonElement>(a.ActionConfigJson)
                    : (JsonElement?)null,
                a.ReportToDiscussionId,
                a.LastRun,
                a.NextRun,
                lastResult = a.LastResultJson != null
                    ? JsonSerializer.Deserialize<JsonElement>(a.LastResultJson)
                    : (JsonElement?)null,
            });
        });

        registry.MapPost("/api/automations/{name}/trigger", "Manually trigger an automation", async (string name, HttpContext ctx, CancellationToken ct) =>
        {
            if (engine.Automations == null)
                return Results.StatusCode(503);

            var a = engine.Automations.GetAll().FirstOrDefault(x => x.Name == name);
            if (a == null)
                return Results.NotFound(new { error = "Automation not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (a.OwnerId != null && a.OwnerId != "local-user" && userId != "local-user" && a.OwnerId != userId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            var result = await engine.Automations.TriggerAsync(name, ct);
            if (result == null)
                return Results.NotFound(new { error = "Automation not found" });

            return Results.Ok(new { success = true, result = new { result.Triggered, result.Summary, result.SessionId } });
        });

        registry.MapDelete("/api/automations/{name}", "Remove an automation", (string name, HttpContext ctx) =>
        {
            var a = engine.Automations?.GetAll().FirstOrDefault(x => x.Name == name);
            if (a == null) return Results.Ok(new { success = false });

            var userId = ctx.User.FindFirstValue("sub");
            if (a.OwnerId != null && a.OwnerId != "local-user" && userId != "local-user" && a.OwnerId != userId)
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            var removed = engine.Automations?.Remove(name) ?? false;
            return Results.Ok(new { success = removed });
        });
    }
}

public class AutomationCreateRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Schedule { get; set; } = "";
    public string? Icon { get; set; }
    public string ActionType { get; set; } = "";
    public JsonElement? ActionConfig { get; set; }
    public bool RemoveOnTrigger { get; set; }
    public string? ReportToDiscussionId { get; set; }
}
