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
        registry.MapPost("/api/callbacks/session-complete", "Internal webhook — RedCompute calls this when a delegated session finishes; it injects a session-complete <nova-event> into the target discussion. Not intended for direct use.", async (HttpContext ctx) =>
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
            var stopReason = body.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;

            var summary = (status, stopReason) switch
            {
                (_, "usage_limit") => $"Session {sessionId} paused — usage limit reached{(title != null ? $" ({title})" : "")}",
                ("Idle", _) => $"Session {sessionId} completed{(title != null ? $": {title}" : "")}",
                ("Stopped", _) => $"Session {sessionId} was stopped",
                ("Error" or "Ended", _) => $"Session {sessionId} ended with status: {status}",
                _ => $"Session {sessionId} status: {status}",
            };

            var eventContent = $"""
                <nova-event source="callback:session-complete" type="session-complete" stopReason="{stopReason ?? "unknown"}">
                {summary}
                </nova-event>
                """;

            try
            {
                await Http.PostAsJsonAsync(
                    $"http://127.0.0.1:18803/api/discussions/{discussionId}/event",
                    new { content = eventContent, source = $"delegate:{sessionId}" });
            }
            catch (Exception ex)
            {
                App.LogService.Error("callbacks", $"Failed to inject event for session {sessionId}: {ex.Message}");
                return Results.Json(new { error = "event_injection_failed", message = ex.Message }, statusCode: 502);
            }

            App.LogService.Info("callbacks", $"Session {sessionId} completed, notified discussion {discussionId}");
            return Results.Ok(new { handled = true, sessionId, discussionId, status });
        })
        .WithAuth("local")
        .WithParam("discussionId", "string", required: true,
            description: "Discussion that receives the session-complete event",
            location: ParamLocation.Query)
        .WithParam("sessionId", "string", required: true,
            description: "The RedCompute session that finished",
            location: ParamLocation.Body)
        .WithParam("status", "string",
            description: "Final session status (Idle, Stopped, Error, Ended)",
            location: ParamLocation.Body);
    }
}
