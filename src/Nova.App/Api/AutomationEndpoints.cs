using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost;
using RedBamboo.AppHost.Discovery;
using Nova.App.Data;
using Nova.App.Services;

namespace Nova.App.Api;

public static class AutomationEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string ActionConfigDescription =
        "Shape depends on actionType. " +
        "ai-session: { prompt (string, required), systemPromptHint (string), preCreateDiscussion (bool — pre-creates a " +
        "discussion and instructs the AI to post its output there), timeout (seconds), qualityMode (string — quality tier: " +
        "'fast', 'standard', 'deep', 'research'; resolved by RedCompute to provider+model+effort; defaults to backend default when omitted) }. " +
        "http-check: { url (string, required), method ('GET'|'POST'|'PUT', default GET), condition: { field (dot-notation " +
        "JSON path, e.g. 'session.status'), equals (string) } } — without a condition any 2xx response counts as triggered. " +
        "builtin:backup: no config.";

    private static object AutomationProperties(bool forCreate) => new Dictionary<string, object>
    {
        ["name"] = new { type = "string", description = forCreate
            ? "Unique automation name — becomes the path key for get/update/trigger/delete."
            : "Ignored on update — the name comes from the URL path." },
        ["description"] = new { type = "string", description = "Human-readable purpose of the automation." },
        ["schedule"] = new { type = "string", description = "Cron expression (5-field, or 6-field with leading seconds), evaluated in UTC." },
        ["icon"] = new { type = "string", description = "Font Awesome icon name (e.g. 'fa-bell') shown in the Pulse panel." },
        ["actionType"] = new { type = "string", @enum = new[] { "ai-session", "http-check", "builtin:backup" } },
        ["actionConfig"] = new { type = "object", description = ActionConfigDescription },
        ["enabled"] = new { type = "boolean", description = forCreate ? "New automations are always created enabled." : "Enable or disable the schedule." },
        ["removeOnTrigger"] = new { type = "boolean", description = "One-shot watcher: delete the automation after it triggers." },
        ["reportToDiscussionId"] = new { type = "string", description = "Discussion that receives a <nova-event> when the automation triggers or permanently fails." },
        ["expiresAt"] = new { type = "string", description = "ISO 8601 UTC timestamp. After this the automation disables itself (and reports expiry if reportToDiscussionId is set)." },
        ["maxFailures"] = new { type = "integer", description = "Consecutive scheduled-run failures before the automation disables itself. 0 = default (20)." },
    };

    public static void MapAutomationEndpoints(this EndpointRegistry registry, NovaEngine engine)
    {
        registry.MapGet("/api/automations", "List all automations", (HttpContext ctx) =>
        {
            var automations = engine.Automations?.GetAll() ?? [];
            var userId = ctx.User.FindFirstValue("sub");
            automations = automations.Where(a => OwnerScope.CanAccess(a.OwnerId, userId)).ToList();

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

        registry.MapPost("/api/automations", "Create an automation (recurring task, watcher, or AI session on a cron schedule)", async (AutomationCreateRequest request, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name))
                return Results.BadRequest(new { error = "Name is required" });
            if (string.IsNullOrWhiteSpace(request.Schedule))
                return Results.BadRequest(new { error = "Schedule (cron expression) is required" });
            if (string.IsNullOrWhiteSpace(request.ActionType))
                return Results.BadRequest(new { error = "ActionType is required" });

            var automations = engine.Automations;
            if (automations == null)
                return ApiError.ServiceUnavailable("automations_unavailable", "The automation service has not started yet");

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
                ExpiresAt = request.ExpiresAt,
                MaxFailures = request.MaxFailures ?? 0,
                OwnerId = ctx.User.FindFirstValue("sub"),
            };

            try
            {
                await automations.AddAsync(automation);
            }
            catch (ArgumentException ex)
            {
                return ApiError.BadRequest("invalid_schedule", ex.Message);
            }
            return Results.Ok(new { success = true, name = automation.Name, nextRun = automation.NextRun });
        }).WithRequestBody(new
        {
            type = "object",
            required = new[] { "name", "schedule", "actionType" },
            properties = AutomationProperties(forCreate: true),
        });

        registry.MapPut("/api/automations/{name}", "Update an automation. Partial update: only the provided fields change. Changing schedule revalidates the cron expression and recomputes nextRun.", async (string name, AutomationUpdateRequest request, HttpContext ctx) =>
        {
            var automations = engine.Automations;
            if (automations == null)
                return ApiError.ServiceUnavailable("automations_unavailable", "The automation service has not started yet");

            var a = automations.GetAll().FirstOrDefault(x => x.Name == name);
            if (a == null)
                return ApiError.NotFound("automation_not_found", $"No automation named '{name}'");

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(a.OwnerId, userId))
                return ApiError.Forbidden("forbidden", "You do not have access to this automation");

            try
            {
                var updated = await automations.UpdateAsync(name, new AutomationUpdate
                {
                    Description = request.Description,
                    Schedule = request.Schedule,
                    Enabled = request.Enabled,
                    Icon = request.Icon,
                    ActionConfigJson = request.ActionConfig != null
                        ? JsonSerializer.Serialize(request.ActionConfig, JsonOptions)
                        : null,
                    ReportToDiscussionId = request.ReportToDiscussionId,
                    RemoveOnTrigger = request.RemoveOnTrigger,
                    ExpiresAt = request.ExpiresAt,
                    MaxFailures = request.MaxFailures,
                });

                if (updated == null)
                    return ApiError.NotFound("automation_not_found", $"No automation named '{name}'");

                return Results.Ok(new { success = true, name = updated.Name, enabled = updated.Enabled, nextRun = updated.NextRun });
            }
            catch (ArgumentException ex)
            {
                return ApiError.BadRequest("invalid_schedule", ex.Message);
            }
        }).WithRequestBody(new
        {
            type = "object",
            description = "All fields optional — omitted fields are left untouched.",
            properties = AutomationProperties(forCreate: false),
        });

        registry.MapGet("/api/automations/{name}", "Get automation details", (string name, HttpContext ctx) =>
        {
            var a = engine.Automations?.GetAll().FirstOrDefault(x => x.Name == name);
            if (a == null) return Results.NotFound(new { error = "Automation not found" });

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(a.OwnerId, userId))
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

        registry.MapPost("/api/automations/{name}/trigger", "Manually trigger an automation. Runs in the background and returns 202 immediately — check lastResult via GET /api/automations/{name} (or the reportToDiscussionId discussion) for the outcome. Failures count toward normal failure tracking.", (string name, HttpContext ctx) =>
        {
            var automations = engine.Automations;
            if (automations == null)
                return ApiError.ServiceUnavailable("automations_unavailable", "The automation service has not started yet");

            var a = automations.GetAll().FirstOrDefault(x => x.Name == name);
            if (a == null)
                return ApiError.NotFound("automation_not_found", $"No automation named '{name}'");

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(a.OwnerId, userId))
                return ApiError.Forbidden("forbidden", "You do not have access to this automation");

            _ = Task.Run(() => automations.TriggerAsync(name, CancellationToken.None));

            return Results.Accepted(value: new { ok = true, status = "started", name });
        });

        registry.MapDelete("/api/automations/{name}", "Remove an automation", async (string name, HttpContext ctx) =>
        {
            var automations = engine.Automations;
            if (automations == null)
                return ApiError.ServiceUnavailable("automations_unavailable", "The automation service has not started yet");

            var a = automations.GetAll().FirstOrDefault(x => x.Name == name);
            if (a == null)
                return ApiError.NotFound("automation_not_found", $"No automation named '{name}'");

            var userId = ctx.User.FindFirstValue("sub");
            if (!OwnerScope.CanAccess(a.OwnerId, userId))
                return Results.Json(new { error = "Forbidden" }, statusCode: 403);

            var removed = await automations.RemoveAsync(name);
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
    public DateTime? ExpiresAt { get; set; }
    public int? MaxFailures { get; set; }
}

public class AutomationUpdateRequest
{
    public string? Description { get; set; }
    public string? Schedule { get; set; }
    public bool? Enabled { get; set; }
    public string? Icon { get; set; }
    public JsonElement? ActionConfig { get; set; }
    public bool? RemoveOnTrigger { get; set; }
    public string? ReportToDiscussionId { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public int? MaxFailures { get; set; }
}
