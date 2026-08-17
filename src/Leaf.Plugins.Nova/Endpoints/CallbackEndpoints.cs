using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// RedCompute session webhooks. These paths are on the kernel's auth BypassPaths list
/// (RedCompute posts without a bearer — manifest-level public-path declaration is a
/// known contract gap), so every handler here re-checks that the caller is loopback:
/// bypass skips the JWT, the old app's "local" auth mode is emulated in-endpoint.
/// </summary>
public static class CallbackEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/callbacks/session-complete", async (HttpContext ctx, DiscussionStore store, EventInjector injector, LiveEvents live) =>
        {
            if (!IsLoopback(ctx))
                return Results.Json(new { error = "Local callers only" }, statusCode: 403);

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }

            var sessionId = body.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            var status = body.TryGetProperty("status", out var st) ? st.GetString() : null;
            var discussionId = ctx.Request.Query["discussionId"].ToString();

            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(discussionId))
                return Results.BadRequest(new { error = "sessionId and discussionId are required" });

            var title = body.TryGetProperty("title", out var t) ? t.GetString() : null;
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

            var discussion = await store.GetAsync(discussionId);
            if (discussion is null)
                return Results.Json(new { error = "event_injection_failed", message = "Discussion not found" }, statusCode: 502);

            await injector.InjectAsync(discussion, eventContent, null, $"delegate:{sessionId}");

            if (!discussion.Confidential)
                _ = live.PostAsync("callback", $"Delegated session completed{(title != null ? $": {title}" : "")}");
            return Results.Ok(new { handled = true, sessionId, discussionId, status });
        });

        group.MapPost("/callbacks/agent-response", async (HttpContext ctx, DiscussionStore store, EventInjector injector, RedComputeClient redCompute) =>
        {
            if (!IsLoopback(ctx))
                return Results.Json(new { error = "Local callers only" }, statusCode: 403);

            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }

            var sessionId = body.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            var replyToDiscussionId = ctx.Request.Query["replyTo"].ToString();
            var respondingAgentId = ctx.Request.Query["agentId"].ToString();

            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(replyToDiscussionId))
                return Results.BadRequest(new { error = "sessionId and replyTo are required" });

            // Small delay to ensure messages are persisted before we read them.
            await Task.Delay(1000);

            string responseContent;
            try
            {
                using var raw = await redCompute.GetSessionRawAsync(sessionId)
                    ?? throw new InvalidOperationException("session fetch failed");
                var messages = raw.RootElement.GetProperty("messages").EnumerateArray().ToList();

                var lastUserIdx = -1;
                for (int i = messages.Count - 1; i >= 0; i--)
                {
                    if (messages[i].GetProperty("role").GetString() == "user") { lastUserIdx = i; break; }
                }

                var responseParts = new List<string>();
                for (int i = lastUserIdx + 1; i < messages.Count; i++)
                {
                    var m = messages[i];
                    if (m.GetProperty("role").GetString() == "assistant"
                        && m.GetProperty("eventType").GetString() == "text"
                        && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var text = c.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                            responseParts.Add(text!);
                    }
                }

                responseContent = string.Join("", responseParts);
                if (string.IsNullOrWhiteSpace(responseContent))
                    responseContent = "(no response)";
            }
            catch (Exception ex)
            {
                responseContent = $"(failed to fetch response: {ex.Message})";
            }

            var discussion = await store.GetAsync(replyToDiscussionId);
            if (discussion is null)
                return Results.Json(new { error = "delivery_failed", message = "Target discussion not found" }, statusCode: 502);

            await injector.InjectAsync(discussion, responseContent, null, $"agent-response:{sessionId}",
                senderAgentId: string.IsNullOrEmpty(respondingAgentId) ? null : respondingAgentId);

            // Stop the responding session (may already be stopped).
            try { await redCompute.StopAsync(sessionId); }
            catch { }

            return Results.Ok(new { handled = true, sessionId, replyToDiscussionId, respondingAgentId });
        });
    }

    private static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip != null && System.Net.IPAddress.IsLoopback(ip);
    }
}
