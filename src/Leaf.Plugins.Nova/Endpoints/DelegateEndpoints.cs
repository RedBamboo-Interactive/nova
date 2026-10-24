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
    // Legacy opt-in hint. Delegation never broadcasts navigation; an explicit
    // human caller receives a local route in the response instead.
    public bool? Navigate { get; set; }
    public string? Model { get; set; }
    public string? QualityTier { get; set; }
    // Compatibility for callers using the old provider-specific terminology.
    public string? QualityMode { get; set; }
    public string? Provider { get; set; }
    // RedCompute's delivery policy, forwarded verbatim: after-current (default), steer-current or
    // interrupt-current. Only a continuation has a turn to steer or interrupt.
    public string? Delivery { get; set; }
    public DelegationDeploymentTarget? DeploymentVerificationTarget { get; set; }
}

public sealed record DelegationDeploymentTarget(string Service, string RunId);

/// <summary>
/// POST /delegate — delegate work to a CodeRed session: creates the session on
/// RedCompute, delivers the prompt, records it in the requesting discussion, and registers a
/// completion callback that reports back into the given discussion. Delegation
/// records a semantic discussion event but never changes a browser route.
/// </summary>
public static class DelegateEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/delegate", HandleDelegateAsync);
    }

    internal static async Task<IResult> HandleDelegateAsync(HttpContext ctx, DelegateRequest request, RedComputeClient redCompute,
        AgentDirectory agentDir, AgentWorkspaces workspaces, DiscussionStore discussions,
        [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities,
        EventInjector injector)
    {
        bool isContinuation = !string.IsNullOrWhiteSpace(request.SessionId);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return Results.BadRequest(new { error = "prompt is required" });
        if (DeliveryError(request.Delivery, isContinuation) is { } deliveryError)
            return Results.BadRequest(deliveryError);
        var delivery = request.Delivery ?? AfterCurrent;
        if (request.DeploymentVerificationTarget is { } target
            && (target.Service is not ("redleaf" or "redcompute") || string.IsNullOrWhiteSpace(target.RunId)
                || target.RunId.Length > 100 || target.RunId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            return Results.BadRequest(new { error = "invalid_deployment_target" });

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

        if (!isContinuation)
            request.Provider = EffectiveProvider(request, discussion, resolvedAgent);

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
        bool admissionUncertain = false;
        RedComputeClient.SendMessageResult? lastPromptResult = null;
        var promptMessageUid = Guid.NewGuid().ToString("N");
        var promptIdempotencyKey = $"nova-delegate:{sessionId}:{promptMessageUid}";
        // Every attempt sends this same body under the same key, so a retry is an idempotent
        // replay: RedCompute interrupts or steers only on a fresh admission, never on a replay.
        var promptBody = new Dictionary<string, object?>
        {
            ["content"] = request.Prompt,
            ["messageUid"] = promptMessageUid,
            ["delivery"] = delivery,
        };
        if (request.DeploymentVerificationTarget is not null)
            promptBody["deploymentVerificationTarget"] = request.DeploymentVerificationTarget;
        // Subscribe before dispatch so a fast prompt completion cannot outrun registration.
        // Compute binds this exact UID to its durable accepted input; no queued input means no completion.
        if (!string.IsNullOrWhiteSpace(request.DiscussionId))
        {
            try
            {
                var (completionUrl, questionUrl) = CallbackUrls(request.DiscussionId);
                await redCompute.RegisterCallbackAsync(sessionId, completionUrl, force: true, callbackId: promptMessageUid, promptMessageUid: promptMessageUid,
                    questionUrl: questionUrl);
            }
            catch { /* The post-admission registration below retries the same identity. */ }
        }
        for (int attempt = 0; attempt < 3 && !promptSent; attempt++)
        {
            try
            {
                var messageProvenance = await NovaComputeProvenance.CreateAsync(
                    entities, resolvedAgent, beneficiary,
                    "/api/apps/nova/delegate",
                    [.. baseContext, new ComputeContextReference("session", sessionId)],
                    method: "POST", ct: ctx.RequestAborted);
                var result = await redCompute.SendMessageDetailedAsync(sessionId, promptBody,
                    messageProvenance, ctx.RequestAborted,
                    idempotencyKey: promptIdempotencyKey);
                lastPromptResult = result;
                if (result.StatusCode >= 500 || result.Success && !IsPromptAccepted(result.Payload)) admissionUncertain = true;
                if (result.Success && IsPromptAccepted(result.Payload))
                {
                    promptSent = true;
                    break;
                }
                if (result.ErrorCode == "execution_identity_rejected") break;
            }
            catch { admissionUncertain = true; }

            if (!promptSent)
                await Task.Delay(500);
        }

        if (!promptSent)
        {
            var definitivelyRejected = IsAdmissionDefinitivelyRejected(lastPromptResult, admissionUncertain);
            var unacceptedConfirmed = false;
            if (definitivelyRejected && request.DiscussionId is not null)
            {
                try { unacceptedConfirmed = await redCompute.RemoveUnacceptedCallbackAsync(sessionId, promptMessageUid, promptMessageUid, ctx.RequestAborted); }
                catch { }
            }
            // A lost admission response must never cancel accepted work.
            if (!isContinuation && unacceptedConfirmed)
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
                    : unacceptedConfirmed ? "Session created but prompt admission was rejected. Session cleaned up." : "Prompt acceptance could not be confirmed. Session retained for inspection.",
                upstreamError = lastPromptResult?.ErrorCode,
            }, statusCode: 502);
        }

        var deliveryReport = ReportDelivery(delivery, lastPromptResult?.Payload);

        // 3. Record the delegation where it was requested. This is a system
        // event: it belongs in the visible discussion timeline but must not
        // become another model-facing user turn.
        bool delegationEventRecorded = false;
        if (discussion is not null)
        {
            try
            {
                var eventData = JsonSerializer.SerializeToElement(new
                {
                    sessionId,
                    repositoryId = resolvedRepository?.Id,
                    repository = resolvedRepository?.Name,
                    continued = isContinuation,
                    agent = resolvedAgent.Name,
                    agentId = resolvedAgent.Id,
                    promptMessageUid,
                    status = "started",
                    delivery = deliveryReport,
                }, ReportJson);
                // Interrupting or steering changes what the session's other turns mean, so the
                // timeline says it was requested; a later completion is read against this.
                await injector.InjectAsync(
                    discussion,
                    delivery == AfterCurrent
                        ? $"Delegated to Code session {sessionId}"
                        : $"Delegated to Code session {sessionId} ({delivery} requested)",
                    "system",
                    "delegation",
                    metadata: eventData,
                    idempotencyKey: $"delegation:{sessionId}:{promptMessageUid}",
                    ct: ctx.RequestAborted);
                delegationEventRecorded = true;
            }
            catch { }
        }

        // Navigation is a client concern. A global plugin event would route
        // every connected Leaf window, including windows that did not make
        // this request. Explicit human callers can follow this returned path
        // locally; Agent executions never receive a navigation instruction.
        var navigationPath = RequestedNavigationPath(
            ctx.User, request.Navigate, sessionId);

        // 4. Register completion callback with RedCompute
        bool callbackRegistered = false;
        if (request.DiscussionId != null)
        {
            try
            {
                var (callbackUrl, questionUrl) = CallbackUrls(request.DiscussionId);
                callbackRegistered = await redCompute.RegisterCallbackAsync(sessionId, callbackUrl, callbackId: promptMessageUid, promptMessageUid: promptMessageUid,
                    questionUrl: questionUrl);
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
            delegationEventRecorded,
            navigationPath,
            delivery = deliveryReport,
        });
    }

    internal const string AfterCurrent = "after-current";
    internal const string SteerCurrent = "steer-current";
    internal const string InterruptCurrent = "interrupt-current";
    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Null when the delivery is acceptable. Values are RedCompute's own vocabulary and are never
    /// translated or defaulted: an unknown value is refused rather than sent as another mode. A new
    /// session has no turn to steer or interrupt, so only the default is accepted there.
    /// </summary>
    internal static object? DeliveryError(string? delivery, bool isContinuation)
    {
        if (delivery is null) return null;
        if (delivery is not (AfterCurrent or SteerCurrent or InterruptCurrent))
            return new
            {
                error = "invalid_delivery",
                message = $"delivery must be '{AfterCurrent}', '{SteerCurrent}' or '{InterruptCurrent}'",
            };
        if (!isContinuation && delivery != AfterCurrent)
            return new
            {
                error = "delivery_requires_session",
                message = $"delivery '{delivery}' needs an existing session with a turn to act on; a new session accepts only '{AfterCurrent}'",
            };
        return null;
    }

    /// <summary>
    /// What RedCompute's admission response says happened to the prompt, never inferred.
    /// <para><c>effective</c> is the policy RedCompute holds for the queue item: steer-current is
    /// demoted to after-current (reason <c>steer_unavailable</c>) when the session is still starting,
    /// its provider cannot steer, the turn refuses the steer, or delivery is paused for maintenance.
    /// Only steer-current can still change after admission, so
    /// <c>effectiveFinal</c> is false while a steer is pending. A steer-current item delivered to an
    /// idle session ran as that session's next turn; the response does not distinguish this from
    /// joining an active turn.</para>
    /// <para><c>turnInterrupted</c> is false for after-current and steer-current, which never
    /// interrupt. For interrupt-current it is null: RedCompute interrupts only on a fresh admission to
    /// an active session and does not report whether it did.</para>
    /// <para><c>inputsAhead</c> is true when other caller inputs precede this one in the FIFO.
    /// interrupt-current interrupts the running turn but does not overtake queued input, so those
    /// run first. Null when the queue position cannot decide it (a pending steer overtakes ordinary
    /// input).</para>
    /// </summary>
    internal static DeliveryReport ReportDelivery(string requested, JsonElement? payload)
    {
        JsonElement item = default;
        var hasItem = payload is { ValueKind: JsonValueKind.Object } p
            && p.TryGetProperty("item", out item) && item.ValueKind == JsonValueKind.Object;
        var effective = hasItem ? Text(item, "delivery") : null;
        var state = hasItem ? Text(item, "state") : null;
        var itemId = hasItem ? Text(item, "id") : null;
        var errorCode = hasItem && item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? Text(error, "code") : null;
        var replayed = payload is { ValueKind: JsonValueKind.Object } r
            && r.TryGetProperty("existing", out var existing) && existing.ValueKind == JsonValueKind.True;
        var head = payload is { ValueKind: JsonValueKind.Object } q
            && q.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Object
                ? Text(queue, "headItemId") : null;

        bool? inputsAhead = state switch
        {
            null => null,
            "delivered" or "cancelled" => false,
            "pending" when effective is (AfterCurrent or InterruptCurrent) && itemId is not null =>
                head is not null && head != itemId,
            _ => null,
        };
        return new DeliveryReport(
            requested,
            effective,
            EffectiveFinal: effective is not null && (effective != SteerCurrent || state is "delivered" or "cancelled"),
            state,
            DemotionReason: requested != effective && effective is not null ? errorCode : null,
            TurnInterrupted: requested == InterruptCurrent ? null : false,
            inputsAhead,
            itemId,
            replayed);
    }

    internal sealed record DeliveryReport(
        string Requested, string? Effective, bool EffectiveFinal, string? State, string? DemotionReason,
        bool? TurnInterrupted, bool? InputsAhead, string? QueueItemId, bool Replayed);

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The webhooks a delegation registers for its discussion: completion, and the opt-in question
    /// notification so a worker parked on a question is surfaced instead of timing out.
    /// </summary>
    internal static (string Completion, string Question) CallbackUrls(string discussionId)
        => ($"http://127.0.0.1:18804/api/apps/nova/callbacks/session-complete?discussionId={discussionId}",
            $"http://127.0.0.1:18804/api/apps/nova/callbacks/session-question?discussionId={discussionId}");

    /// <summary>
    /// Provider for a new delegated session. An explicit provider (or one supplied by an
    /// explicitly named Agent) wins. Otherwise the session runs on the same provider as the
    /// calling discussion, resolved the way that discussion's own session is created. An explicit
    /// model without a provider is left to RedCompute so a provider is never paired with another
    /// provider's model. Null falls through to the suite default.
    /// </summary>
    internal static string? EffectiveProvider(DelegateRequest request, DiscussionRead? discussion, AgentInfo callingAgent)
    {
        if (!string.IsNullOrWhiteSpace(request.Provider)) return request.Provider;
        if (!string.IsNullOrWhiteSpace(request.Model)) return null;
        if (!string.IsNullOrWhiteSpace(discussion?.Provider)) return discussion.Provider;
        return string.IsNullOrWhiteSpace(callingAgent.Provider) ? null : callingAgent.Provider;
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

    internal static string? RequestedNavigationPath(
        ClaimsPrincipal principal, bool? navigate, string sessionId)
        => navigate == true && DiscussionAccessPolicy.IsExplicitHuman(principal)
            ? $"/apps/codered/sessions/{Uri.EscapeDataString(sessionId)}"
            : null;

    internal static bool IsExecutionIdentityFailure(Exception exception)
        => exception.GetType().Name.Equals(
            "ExecutionIdentityValidationException", StringComparison.Ordinal);

    internal static bool IsAdmissionDefinitivelyRejected(RedComputeClient.SendMessageResult? result, bool earlierOutcomeUnknown)
        => !earlierOutcomeUnknown && result is { Success: false, StatusCode: >= 400 and < 500 };

    internal static bool IsPromptAccepted(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } value) return false;
        return value.TryGetProperty("accepted", out var accepted)
                && accepted.ValueKind == JsonValueKind.True
            || value.TryGetProperty("sent", out var sent)
                && sent.ValueKind == JsonValueKind.True;
    }
}
