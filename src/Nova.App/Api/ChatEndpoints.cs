using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nova.App.Services;

namespace Nova.App.Api;

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app, NovaEngine engine)
    {
        app.MapGet("/api/chat/contexts", () =>
        {
            var contexts = engine.GetAllContexts().Select(c => new
            {
                c.Id,
                status = c.Status.ToString().ToLowerInvariant(),
                c.CreatedAt,
                c.LastActivity,
            });
            return Results.Ok(contexts);
        });

        app.MapPost("/api/chat/send", async (ChatSendRequest request, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
                return Results.BadRequest(new { error = "Message is required" });

            var contextId = request.ContextId ?? Guid.NewGuid().ToString("N")[..8];

            try
            {
                var response = await engine.ChatAsync(contextId, request.Message, ct);
                return Results.Ok(new
                {
                    response.Text,
                    response.ContextId,
                    response.ToolCalls,
                });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // WebSocket endpoint for streaming chat will be added here
        // app.Map("/api/chat/stream", ...) — TODO: SSE or WebSocket streaming
    }
}

public class ChatSendRequest
{
    public string Message { get; set; } = "";
    public string? ContextId { get; set; }
}
