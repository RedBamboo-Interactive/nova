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
    internal static async Task<IResult> HandleSessionCompleteAsync(
        HttpContext ctx, DiscussionStore store, EventInjector injector, LiveEvents live)
    {
        if (!IsLoopback(ctx))
            return Results.Json(new { error = "Local callers only" }, statusCode: 403);

        JsonElement body;
        try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
        catch { return Results.BadRequest(new { error = "invalid_body" }); }

        var sessionId = body.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        string? callbackId = null;
        if (body.TryGetProperty("callbackId", out var callback))
        {
            if (callback.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(callback.GetString())
                || callback.GetString()!.Length > 200)
                return Results.BadRequest(new { error = "invalid_callback_id" });
            callbackId = callback.GetString();
        }
        var status = body.TryGetProperty("status", out var st) ? st.GetString() : null;
        var discussionId = ctx.Request.Query["discussionId"].ToString();

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(discussionId))
            return Results.BadRequest(new { error = "sessionId and discussionId are required" });

        var title = body.TryGetProperty("title", out var t) ? t.GetString() : null;
        var stopReason = body.TryGetProperty("stopReason", out var sr) ? sr.GetString() : null;
        var reason = body.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var deliveredMessageUid = body.TryGetProperty("deliveredMessageUid", out var delivered)
            && delivered.ValueKind == JsonValueKind.String ? delivered.GetString() : null;
        var promptMessageUid = body.TryGetProperty("promptMessageUid", out var prompt) && prompt.ValueKind == JsonValueKind.String ? prompt.GetString() : null;
        var executionObserved = body.TryGetProperty("executionObserved", out var observed) && observed.ValueKind == JsonValueKind.True;
        var provenEndedCompletion = status == "Ended" && reason == "completed"
            && promptMessageUid is not null && deliveredMessageUid is not null && executionObserved;

        var restartPause = status is "Stopped" or "Ended"
            && stopReason is "maintenance_restart" or "orphaned_on_restart";
        var outcome = restartPause || stopReason == "usage_limit" ? "paused"
            : status == "Idle" || provenEndedCompletion ? "completed"
            : status == "Cancelled" ? reason == "superseded" ? "superseded" : "cancelled"
            : status == "Error" || reason == "failed" ? "failed"
            : status == "Ended" ? "ended"
            : status == "Stopped" ? "stopped" : "status";

        var summary = (status, stopReason) switch
        {
            _ when restartPause => $"Session {sessionId} paused for restart; awaiting recovery, completion has not been reported",
            (_, "usage_limit") => $"Session {sessionId} paused — usage limit reached{(title != null ? $" ({title})" : "")}",
            _ when provenEndedCompletion => $"Session {sessionId} completed{(title != null ? $": {title}" : "")}",
            ("Idle", _) => $"Session {sessionId} completed{(title != null ? $": {title}" : "")}",
            ("Cancelled", _) => $"Session {sessionId} input was {(reason == "superseded" ? "superseded" : "cancelled")}; it did not complete",
            ("Stopped", _) => $"Session {sessionId} was stopped",
            ("Error" or "Ended", _) => $"Session {sessionId} ended with status: {status}",
            _ => $"Session {sessionId} status: {status}",
        };

        var eventContent = $"""
            <nova-event source="callback:session-{(outcome == "paused" ? "paused" : "complete")}" type="session-{(outcome == "paused" ? "paused" : "complete")}" stopReason="{stopReason ?? "unknown"}">
            {summary}
            </nova-event>
            """;

        var discussion = await store.GetAsync(discussionId);
        if (discussion is null)
            return Results.Json(new { error = "event_injection_failed", message = "Discussion not found" }, statusCode: 502);

        // Legacy senders remain compatible. A session ID alone cannot identify a
        // completion: later continuations of that same session must still arrive.
        var key = callbackId is null ? null : $"session-complete:{sessionId}:{discussionId}:{callbackId}";
        // Pause and eventual terminal delivery share prompt correlation, but not
        // deduplication identity. A pause must never swallow the later completion.
        if (key is not null && outcome == "paused") key += $":paused:{stopReason}";
        var metadata = JsonSerializer.SerializeToElement(new {
            sessionId, callbackId, promptMessageUid, status, stopReason, reason, deliveredMessageUid, executionObserved, outcome,
        });
        var forwarded = await injector.InjectAsync(discussion, eventContent, null, $"delegate:{sessionId}",
            metadata: metadata,
            idempotencyKey: key, redeliverOnReuse: key is not null,
            deliveryIdempotencyKey: key, ct: ctx.RequestAborted);
        // Do not introduce extra unkeyed retries from an older sender. Recovery
        // is safe only when the callback carries its stable delivery identity.
        if (key is not null && !forwarded && discussion.SessionId is not null)
            return Results.Json(new { error = "event_delivery_failed" }, statusCode: 502);

        if (!discussion.Confidential)
            await live.PostAsync("callback", $"Delegated session {outcome}{(title != null ? $": {title}" : "")}{(restartPause ? " — waiting to resume after restart" : "")}",
                idempotencyKey: key is null ? null : $"{key}:live", ct: ctx.RequestAborted);
        return Results.Ok(new { handled = true, sessionId, discussionId, status });
    }

    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/callbacks/session-complete", HandleSessionCompleteAsync);

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

        group.MapPost("/callbacks/external-conversation", async (
            HttpContext ctx, ExternalAgentConversationProvider provider) =>
        {
            if (!IsLoopback(ctx))
                return Results.Json(new { error = "Local callers only" }, statusCode: 403);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }
            var sessionId = body.TryGetProperty("sessionId", out var session)
                            && session.ValueKind == JsonValueKind.String
                ? session.GetString() : null;
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new { error = "sessionId is required" });
            await provider.NotifySettledAsync(sessionId, ctx.RequestAborted);
            return Results.Ok(new { handled = true, sessionId });
        });
    }

    private static bool IsLoopback(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        return ip != null && System.Net.IPAddress.IsLoopback(ip);
    }
}
