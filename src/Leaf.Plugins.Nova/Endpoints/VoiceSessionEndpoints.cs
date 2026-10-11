using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

public static class VoiceSessionEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/discussions/{id}/voice-sessions", async (string id, HttpContext ctx,
            DiscussionStore store, VoiceSessionService voice) =>
        {
            NoStore(ctx);
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return Results.NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }
            var offerSdp = String(body, "offerSdp");
            if (string.IsNullOrWhiteSpace(offerSdp) || offerSdp.Length > 256_000)
                return Results.BadRequest(new { error = "invalid_sdp", message = "A valid SDP offer is required" });
            var result = await voice.StartAsync(discussion, offerSdp, String(body, "provider"),
                String(body, "model"), String(body, "voice"), ctx.RequestAborted);
            return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);
        });

        group.MapGet("/discussions/{id}/voice-sessions/{voiceSessionId}", async (string id,
            string voiceSessionId, HttpContext ctx, DiscussionStore store, VoiceSessionService voice) =>
        {
            NoStore(ctx);
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return Results.NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            var result = await voice.GetAsync(discussion, voiceSessionId, ctx.RequestAborted);
            return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);
        });

        group.MapPost("/discussions/{id}/voice-sessions/{voiceSessionId}/close", async (string id,
            string voiceSessionId, HttpContext ctx, DiscussionStore store, VoiceSessionService voice) =>
        {
            NoStore(ctx);
            var discussion = await store.GetAsync(id, ctx.RequestAborted);
            if (discussion is null) return Results.NotFound();
            if (!DiscussionAccessPolicy.CanRead(discussion, ctx)) return AccessDenied(discussion);
            var result = await voice.CloseAsync(discussion, voiceSessionId, ctx.RequestAborted);
            return Results.Content(result.Content, result.ContentType, statusCode: result.StatusCode);
        });

        group.MapPost("/callbacks/voice-session", async (HttpContext ctx, VoiceSessionService voice) =>
        {
            NoStore(ctx);
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is null || !System.Net.IPAddress.IsLoopback(ip))
                return Results.Json(new { error = "Local callers only" }, statusCode: 403);
            JsonElement body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted); }
            catch { return Results.BadRequest(new { error = "invalid_body" }); }
            return await voice.HandleCallbackAsync(body, ctx.RequestAborted)
                ? Results.Ok(new { handled = true })
                : Results.BadRequest(new { error = "invalid_voice_event" });
        });
    }

    private static string? String(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IResult AccessDenied(DiscussionRead discussion)
        => discussion.Confidential ? Results.NotFound() : Results.Forbid();

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}
