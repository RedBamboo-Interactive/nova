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

    private static readonly HttpClient RedCompute = new()
    {
        BaseAddress = new Uri(App.Config.Suite.RedCompute),
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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

        registry.MapPost("/api/callbacks/agent-response", "Internal webhook — fires when an agent's session finishes responding to an inter-agent message. Fetches the response, delivers it to the originating discussion, and stops the session.", async (HttpContext ctx) =>
        {
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }

            var sessionId = body.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            var replyToDiscussionId = ctx.Request.Query["replyTo"].ToString();
            var respondingAgentId = ctx.Request.Query["agentId"].ToString();

            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(replyToDiscussionId))
                return Results.BadRequest(new { error = "sessionId and replyTo are required" });

            // Fetch the session history to extract the actual response
            // Small delay to ensure messages are persisted before we read them
            await Task.Delay(1000);

            string responseContent;
            try
            {
                var resp = await RedCompute.GetAsync($"/ai-session/sessions/{sessionId}");
                resp.EnsureSuccessStatusCode();
                var rawJson = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(rawJson);
                var messages = doc.RootElement.GetProperty("messages");

                var allMessages = messages.EnumerateArray().ToList();
                App.LogService.Info("callbacks", $"Agent response: session {sessionId} has {allMessages.Count} messages");

                var lastUserIdx = -1;
                for (int i = allMessages.Count - 1; i >= 0; i--)
                {
                    if (allMessages[i].GetProperty("role").GetString() == "user")
                    { lastUserIdx = i; break; }
                }

                var responseParts = new List<string>();
                for (int i = lastUserIdx + 1; i < allMessages.Count; i++)
                {
                    var m = allMessages[i];
                    if (m.GetProperty("role").GetString() == "assistant"
                        && m.GetProperty("eventType").GetString() == "text"
                        && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var text = c.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            responseParts.Add(text);
                    }
                }

                responseContent = string.Join("", responseParts);
                App.LogService.Info("callbacks", $"Agent response: extracted {responseParts.Count} parts, content='{responseContent}'");
                if (string.IsNullOrWhiteSpace(responseContent))
                    responseContent = "(no response)";
            }
            catch (Exception ex)
            {
                App.LogService.Error("callbacks", $"Failed to fetch session {sessionId} for agent response: {ex.Message}");
                responseContent = $"(failed to fetch response: {ex.Message})";
            }

            // Deliver the response to the originating discussion
            try
            {
                var agentIdParam = !string.IsNullOrEmpty(respondingAgentId)
                    ? respondingAgentId : (string?)null;

                await Http.PostAsJsonAsync(
                    $"http://127.0.0.1:18803/api/discussions/{replyToDiscussionId}/event",
                    new
                    {
                        content = responseContent,
                        source = $"agent-response:{sessionId}",
                        senderAgentId = agentIdParam,
                    });
            }
            catch (Exception ex)
            {
                App.LogService.Error("callbacks", $"Failed to deliver agent response to {replyToDiscussionId}: {ex.Message}");
                return Results.Json(new { error = "delivery_failed", message = ex.Message }, statusCode: 502);
            }

            // Stop the target session
            try
            {
                await RedCompute.PostAsync($"/ai-session/sessions/{sessionId}/stop", null);
            }
            catch
            {
                App.LogService.Warn("callbacks", $"Failed to stop session {sessionId} after agent response (may already be stopped)");
            }

            App.LogService.Info("callbacks", $"Agent response from session {sessionId} delivered to discussion {replyToDiscussionId}");
            return Results.Ok(new { handled = true, sessionId, replyToDiscussionId, respondingAgentId });
        })
        .WithAuth("local")
        .WithParam("replyTo", "string", required: true,
            description: "Discussion that receives the agent's response",
            location: ParamLocation.Query)
        .WithParam("agentId", "string",
            description: "Agent ID of the responding agent",
            location: ParamLocation.Query)
        .WithParam("sessionId", "string", required: true,
            description: "The session that produced the response",
            location: ParamLocation.Body);
    }
}
