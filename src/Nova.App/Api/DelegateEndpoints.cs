using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Discovery;

namespace Nova.App.Api;

public static class DelegateEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static HttpClient RedCompute = new()
    {
        BaseAddress = new Uri(App.Config.Suite.RedCompute),
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static HttpClient CodeRed = new()
    {
        BaseAddress = new Uri(App.Config.Suite.CodeRed),
        Timeout = TimeSpan.FromSeconds(5),
    };

    public static void Initialize(AuthenticatedHttpClientFactory factory)
    {
        RedCompute = factory.CreateClient(App.Config.Suite.RedCompute, TimeSpan.FromSeconds(30));
        CodeRed = factory.CreateClient(App.Config.Suite.CodeRed, TimeSpan.FromSeconds(5));
    }

    public static void MapDelegateEndpoints(this EndpointRegistry registry)
    {
        registry.MapPost("/api/delegate", "Delegate work to a CodeRed session: creates session on RedCompute, sends prompt, navigates CodeRed, registers completion callback that reports back to discussionId. Returns sessionId. Options: navigate (bool, default true), dockerImage (string, passed to session creation for containerized execution), model (string, e.g. 'fable', 'opus', 'sonnet', 'haiku'). To continue an existing session, provide sessionId instead of projectPath.", async (HttpContext ctx, DelegateRequest request) =>
        {
            bool isContinuation = !string.IsNullOrWhiteSpace(request.SessionId);
            if (!isContinuation && string.IsNullOrWhiteSpace(request.ProjectPath))
                return Results.BadRequest(new { error = "Either sessionId or projectPath is required" });
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "prompt is required" });

            // 1. Create session on RedCompute (skip if continuing existing session)
            string sessionId;
            if (isContinuation)
            {
                sessionId = request.SessionId!;
                try
                {
                    var infoResp = await RedCompute.GetAsync($"/ai-session/sessions/{sessionId}");
                    if (!infoResp.IsSuccessStatusCode)
                        return Results.Json(new { error = "session_not_found", message = $"Session '{sessionId}' not found on RedCompute" }, statusCode: 404);

                    var info = await infoResp.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
                    var status = info.GetProperty("session").GetProperty("status").GetString();
                    if (status is "Stopped" or "Error")
                    {
                        var resumeResp = await RedCompute.PostAsync($"/ai-session/sessions/{sessionId}/resume", null);
                        if (!resumeResp.IsSuccessStatusCode)
                            return Results.Json(new { error = "resume_failed", message = $"Session '{sessionId}' could not be resumed" }, statusCode: 502);
                    }
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = "redcompute_unavailable", message = ex.Message }, statusCode: 502);
                }
            }
            else
            {
                try
                {
                    var dockerImage = request.DockerImage ?? App.Config.DockerImage;
                    var model = request.Model;
                    var createBody = (dockerImage, model) switch
                    {
                        (not null, not null) => (object)new { projectPath = request.ProjectPath, dockerImage, model },
                        (not null, null) => new { projectPath = request.ProjectPath, dockerImage },
                        (null, not null) => (object)new { projectPath = request.ProjectPath, model },
                        _ => new { projectPath = request.ProjectPath },
                    };
                    var createReq = new HttpRequestMessage(HttpMethod.Post, "/ai-session/sessions")
                    {
                        Content = JsonContent.Create(createBody, options: JsonOptions),
                    };
                    createReq.Headers.Add("X-Caller-Info", "Nova");
                    var userId = ctx.User.FindFirstValue("sub");
                    if (userId != null)
                        createReq.Headers.Add("X-User-Id", userId);
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
                if (!isContinuation)
                {
                    try
                    {
                        await RedCompute.PostAsync($"/ai-session/sessions/{sessionId}/stop", null);
                        await RedCompute.PostAsync($"/ai-session/sessions/{sessionId}/dismiss", null);
                    }
                    catch { }
                }

                return Results.Json(new
                {
                    error = "prompt_send_failed",
                    message = isContinuation
                        ? $"Prompt could not be delivered to session '{sessionId}' after 3 attempts."
                        : $"Session created but prompt could not be delivered after 3 attempts. Session cleaned up.",
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
                continued = isContinuation,
            });
        })
        .WithParam("prompt", "string", required: true, description: "Task prompt delivered to the session", location: ParamLocation.Body)
        .WithParam("projectPath", "string", description: "Project to open for a new session. Required unless sessionId is given", location: ParamLocation.Body)
        .WithParam("sessionId", "string", description: "Existing session to continue — stopped sessions are auto-resumed", location: ParamLocation.Body)
        .WithParam("discussionId", "string", description: "Nova discussion that receives a <nova-event> completion callback", location: ParamLocation.Body)
        .WithParam("navigate", "boolean", description: "Navigate the CodeRed UI to the session", defaultValue: true, location: ParamLocation.Body)
        .WithParam("dockerImage", "string", description: "Docker image for containerized execution (defaults to Nova's configured image)", location: ParamLocation.Body)
        .WithParam("model", "string", description: "Model alias for the session, e.g. 'fable', 'opus', 'sonnet', 'haiku'", location: ParamLocation.Body);
    }
}

public class DelegateRequest
{
    public string? SessionId { get; set; }
    public string? ProjectPath { get; set; }
    public string Prompt { get; set; } = "";
    public string? DiscussionId { get; set; }
    public bool? Navigate { get; set; }
    public string? DockerImage { get; set; }
    public string? Model { get; set; }
}
