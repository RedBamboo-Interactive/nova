using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Api;

public static class CallbackEndpoints
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static void MapCallbackEndpoints(this EndpointRegistry registry)
    {
        registry.MapPost("/api/callbacks/session-complete", "Callback from RedCompute when a delegated session finishes", async (HttpContext ctx) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }

            var sessionId = body.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            var status = body.TryGetProperty("status", out var st) ? st.GetString() : null;
            var discussionId = ctx.Request.Query["discussionId"].ToString();

            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(discussionId))
                return Results.BadRequest(new { error = "sessionId and discussionId are required" });

            var title = body.TryGetProperty("title", out var t) ? t.GetString() : null;
            var projectPath = body.TryGetProperty("projectPath", out var p) ? p.GetString() : null;

            var summary = status switch
            {
                "Idle" => $"Session {sessionId} completed{(title != null ? $": {title}" : "")}",
                "Stopped" => $"Session {sessionId} was stopped",
                "Error" or "Ended" => $"Session {sessionId} ended with status: {status}",
                _ => $"Session {sessionId} status: {status}",
            };

            var eventContent = $"""
                <nova-event source="callback:session-complete" type="session-complete">
                {summary}
                </nova-event>
                """;

            try
            {
                await Http.PostAsJsonAsync(
                    $"http://localhost:18803/api/discussions/{discussionId}/event",
                    new { content = eventContent, source = $"delegate:{sessionId}" });
            }
            catch (Exception ex)
            {
                App.LogService.Error("callbacks", $"Failed to inject event for session {sessionId}: {ex.Message}");
                return Results.Json(new { error = "event_injection_failed", message = ex.Message }, statusCode: 502);
            }

            App.LogService.Info("callbacks", $"Session {sessionId} completed, notified discussion {discussionId}");
            return Results.Ok(new { handled = true, sessionId, discussionId, status });
        });
    }
}
