using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Discovery;

namespace Nova.App.Api;

public static class DelegateEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly HttpClient RedCompute = new()
    {
        BaseAddress = new Uri("http://localhost:18800"),
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly HttpClient CodeRed = new()
    {
        BaseAddress = new Uri("http://localhost:18801"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    public static void MapDelegateEndpoints(this EndpointRegistry registry)
    {
        registry.MapPost("/api/delegate", "Delegate work to a CodeRed session: creates session on RedCompute, sends prompt, navigates CodeRed, registers completion callback that reports back to discussionId. Returns sessionId. Options: navigate (bool, default true), dockerImage (string, passed to session creation for containerized execution)", async (DelegateRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.ProjectPath))
                return Results.BadRequest(new { error = "projectPath is required" });
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "prompt is required" });

            // 1. Create session on RedCompute
            string sessionId;
            try
            {
                var dockerImage = request.DockerImage ?? App.Config.DockerImage;
                var createBody = dockerImage != null
                    ? (object)new { projectPath = request.ProjectPath, dockerImage }
                    : new { projectPath = request.ProjectPath };
                var createReq = new HttpRequestMessage(HttpMethod.Post, "/ai-session/sessions")
                {
                    Content = JsonContent.Create(createBody, options: JsonOptions),
                };
                createReq.Headers.Add("X-Caller-Info", "Nova");
                var createResp = await RedCompute.SendAsync(createReq);

                if (!createResp.IsSuccessStatusCode)
                {
                    var err = await createResp.Content.ReadAsStringAsync();
                    return Results.Json(new { error = "session_create_failed", message = err },
                        statusCode: 502);
                }

                var session = await createResp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
                sessionId = session.GetProperty("id").GetString()!;
            }
            catch (Exception ex)
            {
                return Results.Json(new { error = "redcompute_unavailable", message = ex.Message },
                    statusCode: 502);
            }

            // 2. Send prompt and verify delivery
            bool promptSent = false;
            for (int attempt = 0; attempt < 3 && !promptSent; attempt++)
            {
                try
                {
                    var sendResp = await RedCompute.PostAsJsonAsync(
                        $"/ai-session/sessions/{sessionId}/message",
                        new { content = request.Prompt }, JsonOptions);

                    if (sendResp.IsSuccessStatusCode)
                    {
                        var result = await sendResp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
                        if (result.TryGetProperty("sent", out var sent) && sent.GetBoolean())
                        {
                            promptSent = true;
                            break;
                        }
                    }
                }
                catch { }

                if (!promptSent)
                    await Task.Delay(500);
            }

            if (!promptSent)
            {
                try
                {
                    await RedCompute.PostAsync($"/ai-session/sessions/{sessionId}/stop", null);
                    await RedCompute.PostAsync($"/ai-session/sessions/{sessionId}/dismiss", null);
                }
                catch { }

                return Results.Json(new
                {
                    error = "prompt_send_failed",
                    message = $"Session created but prompt could not be delivered after 3 attempts. Session cleaned up.",
                }, statusCode: 502);
            }

            // 3. Navigate CodeRed (best effort)
            if (request.Navigate != false)
            {
                try { await CodeRed.PostAsync($"/api/navigate?session={sessionId}", null); }
                catch { }
            }

            // 4. Register completion callback with RedCompute
            bool callbackRegistered = false;
            if (request.DiscussionId != null)
            {
                try
                {
                    var callbackUrl = $"http://localhost:18803/api/callbacks/session-complete?discussionId={request.DiscussionId}";
                    var cbResp = await RedCompute.PostAsJsonAsync(
                        $"/ai-session/sessions/{sessionId}/callback",
                        new { url = callbackUrl }, JsonOptions);
                    callbackRegistered = cbResp.IsSuccessStatusCode;
                }
                catch { }
            }

            return Results.Ok(new
            {
                sessionId,
                promptSent,
                callbackRegistered,
                message = $"Session delegated to {request.ProjectPath}",
            });
        });
    }
}

public class DelegateRequest
{
    public string ProjectPath { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? DiscussionId { get; set; }
    public bool? Navigate { get; set; }
    public string? DockerImage { get; set; }
}
