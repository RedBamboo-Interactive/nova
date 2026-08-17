using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Nova.Endpoints;

public class DelegateRequest
{
    public string? SessionId { get; set; }
    public string? ProjectPath { get; set; }
    public string? Repository { get; set; }
    public string? Agent { get; set; }
    public string Prompt { get; set; } = "";
    public string? DiscussionId { get; set; }
    public bool? Navigate { get; set; }
    public string? Model { get; set; }
    public string? QualityTier { get; set; }
    // Compatibility for callers using the old provider-specific terminology.
    public string? QualityMode { get; set; }
    public string? Provider { get; set; }
}

/// <summary>
/// POST /delegate — delegate work to a CodeRed session: creates the session on
/// RedCompute, delivers the prompt, navigates the CodeRed UI, and registers a
/// completion callback that reports back into the given discussion.
/// </summary>
public static class DelegateEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/delegate", async (HttpContext ctx, DelegateRequest request, RedComputeClient redCompute,
            AgentDirectory agentDir, AgentWorkspaces workspaces, DiscussionStore discussions,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events) =>
        {
            bool isContinuation = !string.IsNullOrWhiteSpace(request.SessionId);
            if (string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "prompt is required" });

            var callerId = TrustedCallerId(ctx.User);
            if (callerId == null)
                return Results.Json(new
                {
                    error = "authentication_required",
                    message = "Delegation requires a signed user or execution identity; local-user is not delegated authority",
                }, statusCode: StatusCodes.Status401Unauthorized);

            var hasExplicitProjectPath = !isContinuation
                && !string.IsNullOrWhiteSpace(request.ProjectPath);
            LeafEntity? resolvedRepository = null;
            if (!isContinuation && !string.IsNullOrWhiteSpace(request.Repository))
            {
                resolvedRepository = Guid.TryParse(request.Repository, out var repositoryId)
                    ? await entities.GetAsync(repositoryId, ctx.RequestAborted)
                    : await entities.GetBySlugAsync("repository", request.Repository, ctx.RequestAborted);
                if (resolvedRepository is not { TypeSlug: "repository" })
                    return Results.Json(new
                    {
                        error = "repository_not_found",
                        message = $"Repository '{request.Repository}' not found",
                    }, statusCode: 404);

                var status = StringValue(resolvedRepository.Data, "status", "active");
                if (!string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new
                    {
                        error = "repository_inactive",
                        message = $"Repository '{resolvedRepository.Name}' is not active",
                    }, statusCode: 409);

                var repositoryPath = StringValue(resolvedRepository.Data, "local_path");
                if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
                    return Results.Json(new
                    {
                        error = "repository_checkout_unavailable",
                        message = $"Repository '{resolvedRepository.Name}' has no available local checkout",
                    }, statusCode: 409);

                request.ProjectPath = Path.GetFullPath(repositoryPath);
            }

            // Compatibility for callers that still send a physical path. Code sessions are
            // repository-backed, so resolve the path to one exact active Repository entity
            // instead of creating a session whose membership has to be guessed later.
            if (resolvedRepository is null && hasExplicitProjectPath)
            {
                var repositories = await entities.QueryAsync(new EntityQuery
                {
                    TypeSlug = "repository",
                    Limit = 500,
                }, ctx.RequestAborted);
                var matches = FindMatchingActiveRepositories(repositories, request.ProjectPath!);
                if (matches.Count == 0)
                    return Results.Json(new
                    {
                        error = "repository_not_found_for_path",
                        message = "projectPath must exactly match an active Repository entity checkout",
                    }, statusCode: StatusCodes.Status422UnprocessableEntity);
                if (matches.Count > 1)
                    return Results.Json(new
                    {
                        error = "repository_path_ambiguous",
                        message = "projectPath matches more than one active Repository entity",
                    }, statusCode: StatusCodes.Status409Conflict);

                resolvedRepository = matches[0];
                var repositoryPath = StringValue(resolvedRepository.Data, "local_path");
                if (!Directory.Exists(repositoryPath))
                    return Results.Json(new
                    {
                        error = "repository_checkout_unavailable",
                        message = $"Repository '{resolvedRepository.Name}' has no available local checkout",
                    }, statusCode: StatusCodes.Status409Conflict);
                request.ProjectPath = Path.GetFullPath(repositoryPath);
            }

            // Resolve agent if specified: provides workspace, provider, quality defaults.
            AgentInfo? resolvedAgent = null;
            if (!isContinuation && !string.IsNullOrWhiteSpace(request.Agent))
            {
                var agents = await agentDir.GetAgentsAsync(ct: ctx.RequestAborted);
                resolvedAgent = agents.FirstOrDefault(a =>
                    a.Id == request.Agent || a.Slug == request.Agent);
                if (resolvedAgent == null)
                    return Results.Json(new { error = "agent_not_found", message = $"Agent '{request.Agent}' not found" }, statusCode: 404);

                var ws = await workspaces.GetAsync(resolvedAgent.Id, ctx.RequestAborted);
                ws.GenerateClaudeMd();

                request.ProjectPath ??= ws.WorkspacePath;
                request.Provider ??= resolvedAgent.Provider;
                if (string.IsNullOrWhiteSpace(request.Model)
                    && string.IsNullOrWhiteSpace(request.QualityTier)
                    && string.IsNullOrWhiteSpace(request.QualityMode))
                    request.QualityTier = resolvedAgent.QualityTier;
            }

            if (!isContinuation && string.IsNullOrWhiteSpace(request.ProjectPath))
                return Results.BadRequest(new { error = "Either sessionId, repository, projectPath, or agent is required" });

            DiscussionRead? discussion = null;
            if (!string.IsNullOrWhiteSpace(request.DiscussionId))
            {
                discussion = await discussions.GetAsync(request.DiscussionId, ctx.RequestAborted);
                if (discussion == null)
                    return Results.Json(new
                    {
                        error = "discussion_not_found",
                        message = $"Discussion '{request.DiscussionId}' not found",
                    }, statusCode: StatusCodes.Status404NotFound);
                if (!DiscussionAccessPolicy.CanRead(discussion, ctx))
                    return discussion.Confidential
                        ? Results.Json(new
                        {
                            error = "discussion_not_found",
                            message = $"Discussion '{request.DiscussionId}' not found",
                        }, statusCode: StatusCodes.Status404NotFound)
                        : Results.Json(new
                        {
                            error = "forbidden",
                            message = "You do not have access to this discussion",
                        }, statusCode: StatusCodes.Status403Forbidden);
            }
            resolvedAgent ??= discussion?.AgentId != null
                ? await agentDir.GetAgentAsync(discussion.AgentId, ctx.RequestAborted)
                : agentDir.NovaAgentId != null
                    ? await agentDir.GetAgentAsync(agentDir.NovaAgentId, ctx.RequestAborted)
                    : null;
            if (resolvedAgent == null)
                return Results.Json(new { error = "agent_not_found", message = "No linked Agent entity is available for delegation" }, statusCode: 422);

            var ownerId = discussion?.OwnerId ?? callerId;
            var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, ownerId, ctx.RequestAborted);
            var baseContext = new List<ComputeContextReference>();
            if (request.DiscussionId != null)
                baseContext.Add(new("discussion", request.DiscussionId));
            if (resolvedRepository != null)
                baseContext.Add(new ComputeContextReference(
                    "repository",
                    Id: resolvedRepository.Slug,
                    EntityId: resolvedRepository.Id.ToString(),
                    NameSnapshot: resolvedRepository.Name));

            // 1. Create session on RedCompute (skip if continuing an existing session)
            string sessionId;
            if (isContinuation)
            {
                sessionId = request.SessionId!;
                using var raw = await redCompute.GetSessionRawAsync(sessionId);
                if (raw == null)
                    return Results.Json(new { error = "session_not_found", message = $"Session '{sessionId}' not found on RedCompute" }, statusCode: 404);

                var status = raw.RootElement.TryGetProperty("session", out var s)
                    && s.TryGetProperty("status", out var st) ? st.GetString() : null;
                if (status is "Stopped" or "Error")
                {
                    var resumeProvenance = await NovaComputeProvenance.CreateAsync(entities, resolvedAgent, beneficiary,
                        "/api/apps/nova/delegate",
                        [.. baseContext, new ComputeContextReference("session", sessionId)],
                        method: "POST", ct: ctx.RequestAborted);
                    if (!await redCompute.ResumeAsync(sessionId, resumeProvenance, ctx.RequestAborted))
                        return Results.Json(new { error = "resume_failed", message = $"Session '{sessionId}' could not be resumed" }, statusCode: 502);
                }
            }
            else
            {
                try
                {
                    var createBody = new Dictionary<string, object?> { ["projectPath"] = request.ProjectPath };
                    if (resolvedRepository is not null)
                        createBody["repositoryId"] = resolvedRepository.Id;
                    if (!string.IsNullOrWhiteSpace(request.Provider))
                        createBody["provider"] = request.Provider;
                    if (!string.IsNullOrWhiteSpace(request.Model))
                        createBody["model"] = request.Model;
                    else
                        createBody["qualityTier"] = request.QualityTier
                            ?? request.QualityMode
                            ?? "standard";

                    var createProvenance = await NovaComputeProvenance.CreateAsync(
                        entities, resolvedAgent, beneficiary,
                        "/api/apps/nova/delegate", baseContext, method: "POST", ct: ctx.RequestAborted);
                    var created = await redCompute.CreateSessionAsync(createBody,
                        createProvenance, ctx.RequestAborted);
                    if (created == null)
                        return Results.Json(new { error = "session_create_failed", message = "RedCompute refused to create the session" }, statusCode: 502);
                    sessionId = created;
                }
                catch (Exception ex) when (IsExecutionIdentityFailure(ex))
                {
                    return Results.Json(new
                    {
                        error = "execution_identity_rejected",
                        message = ex.Message,
                    }, statusCode: StatusCodes.Status403Forbidden);
                }
                catch (TaskCanceledException ex)
                {
                    return Results.Json(new
                    {
                        error = "redcompute_timeout",
                        message = ex.Message,
                    }, statusCode: StatusCodes.Status504GatewayTimeout);
                }
                catch (HttpRequestException ex)
                {
                    return Results.Json(new { error = "redcompute_unavailable", message = ex.Message }, statusCode: 502);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { error = "delegation_failed", message = ex.Message }, statusCode: 500);
                }
            }

            // 2. Send prompt and verify delivery
            bool promptSent = false;
            RedComputeClient.SendMessageResult? lastPromptResult = null;
            var promptMessageUid = Guid.NewGuid().ToString("N");
            var promptIdempotencyKey = $"nova-delegate:{sessionId}:{promptMessageUid}";
            for (int attempt = 0; attempt < 3 && !promptSent; attempt++)
            {
                try
                {
                    var messageProvenance = await NovaComputeProvenance.CreateAsync(
                        entities, resolvedAgent, beneficiary,
                        "/api/apps/nova/delegate",
                        [.. baseContext, new ComputeContextReference("session", sessionId)],
                        method: "POST", ct: ctx.RequestAborted);
                    var result = await redCompute.SendMessageDetailedAsync(sessionId,
                        new { content = request.Prompt, messageUid = promptMessageUid },
                        messageProvenance, ctx.RequestAborted,
                        idempotencyKey: promptIdempotencyKey);
                    lastPromptResult = result;
                    if (result.Success && IsPromptAccepted(result.Payload))
                    {
                        promptSent = true;
                        break;
                    }
                    if (result.ErrorCode == "execution_identity_rejected") break;
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
                        await redCompute.StopAsync(sessionId);
                        await redCompute.DismissAsync(sessionId);
                    }
                    catch { }
                }

                if (lastPromptResult is { ErrorCode: "execution_identity_rejected" })
                    return Results.Json(new
                    {
                        error = lastPromptResult.ErrorCode,
                        message = lastPromptResult.ErrorMessage,
                    }, statusCode: StatusCodes.Status403Forbidden);

                if (lastPromptResult is { ErrorCode: "redcompute_timeout" })
                    return Results.Json(new
                    {
                        error = lastPromptResult.ErrorCode,
                        message = lastPromptResult.ErrorMessage,
                    }, statusCode: StatusCodes.Status504GatewayTimeout);

                return Results.Json(new
                {
                    error = "prompt_send_failed",
                    message = isContinuation
                        ? $"Prompt could not be delivered to session '{sessionId}' after 3 attempts."
                        : "Session created but prompt could not be delivered after 3 attempts. Session cleaned up.",
                    upstreamError = lastPromptResult?.ErrorCode,
                }, statusCode: 502);
            }

            // 3. Navigate CodeRed (best effort): "codered.navigate" is a dotted event
            // type, so the plugin-events→WebSocket bridge forwards it un-namespaced —
            // the shell listens on /ws and routes to /apps/codered.
            if (request.Navigate != false)
            {
                try { await events.PublishAsync("codered.navigate", new JsonObject { ["session"] = sessionId }); }
                catch { }
            }

            // 4. Register completion callback with RedCompute
            bool callbackRegistered = false;
            if (request.DiscussionId != null)
            {
                try
                {
                    var callbackUrl = $"http://127.0.0.1:18804/api/apps/nova/callbacks/session-complete?discussionId={request.DiscussionId}";
                    callbackRegistered = await redCompute.RegisterCallbackAsync(sessionId, callbackUrl);
                }
                catch { }
            }

            return Results.Ok(new
            {
                sessionId,
                promptSent,
                callbackRegistered,
                continued = isContinuation,
                agent = resolvedAgent?.Name,
                repository = resolvedRepository?.Id,
            });
        });
    }

    private static string StringValue(JsonObject data, string key, string fallback = "") =>
        data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;

    internal static IReadOnlyList<LeafEntity> FindMatchingActiveRepositories(
        IEnumerable<LeafEntity> repositories,
        string projectPath) => repositories
        .Where(repository => repository.TypeSlug == "repository")
        .Where(repository => string.Equals(
            StringValue(repository.Data, "status", "active"),
            "active",
            StringComparison.OrdinalIgnoreCase))
        .Where(repository => PathsEqual(
            StringValue(repository.Data, "local_path"),
            projectPath))
        .ToList();

    internal static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            var leftPath = Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rightPath = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(leftPath, rightPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string? TrustedCallerId(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return null;
        var subject = principal.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(subject)
            || subject.Equals("local-user", StringComparison.OrdinalIgnoreCase)
                ? null : subject;
    }

    internal static bool IsExecutionIdentityFailure(Exception exception)
        => exception.GetType().Name.Equals(
            "ExecutionIdentityValidationException", StringComparison.Ordinal);

    internal static bool IsPromptAccepted(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } value) return false;
        return value.TryGetProperty("accepted", out var accepted)
                && accepted.ValueKind == JsonValueKind.True
            || value.TryGetProperty("sent", out var sent)
                && sent.ValueKind == JsonValueKind.True;
    }
}
