using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Confidential discussions are a direct owner/owning-Agent channel. They never
/// inherit the ambient localhost superuser shortcut used by ordinary local data.
/// </summary>
public static class DiscussionAccessPolicy
{
    private const string TokenUseClaim = "token_use";
    private const string ExecutionTokenUse = "execution";
    private const string ExecutionIdentityClaim = "execution_identity";
    private const string LocalDefaultAuthenticationType = "LocalDefault";
    private const string LocalDefaultSubjectId = "local-user";
    private const string NovaAppId = NovaAppPlugin.PluginId;
    private const string NovaRoute = "/apps/nova";

    public static bool CanRead(DiscussionRead discussion, HttpContext context)
    {
        var userId = context.User.FindFirstValue("sub");
        if (!OwnerScope.CanAccess(discussion.OwnerId, userId)) return false;
        if (!discussion.Confidential) return true;
        if (string.IsNullOrWhiteSpace(discussion.OwnerId)
            || string.IsNullOrWhiteSpace(discussion.AgentId))
            return false;

        if (IsExecution(context.User))
            return TryReadExecution(context.User, out var execution)
                && execution is not null
                && execution.ActorKind.Equals("agent", StringComparison.OrdinalIgnoreCase)
                && string.Equals(execution.ActorId, discussion.AgentId,
                    StringComparison.OrdinalIgnoreCase)
                && execution.BeneficiaryKind.Equals("user", StringComparison.OrdinalIgnoreCase)
                && string.Equals(execution.BeneficiaryId, discussion.OwnerId,
                    StringComparison.OrdinalIgnoreCase);

        return IsExplicitHuman(context.User)
            && string.Equals(userId, discussion.OwnerId, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanManageConfidentiality(DiscussionRead discussion, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(discussion.OwnerId)) return false;

        var userId = context.User.FindFirstValue("sub");
        if (!string.Equals(userId, discussion.OwnerId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsExecution(context.User)) return IsExplicitHuman(context.User);
        if (string.Equals(userId, LocalDefaultSubjectId, StringComparison.OrdinalIgnoreCase))
            return false;

        return TryReadExecution(context.User, out var execution)
            && execution is not null
            && string.Equals(execution.AppId, NovaAppId, StringComparison.OrdinalIgnoreCase)
            && execution.ActorKind.Equals("app", StringComparison.OrdinalIgnoreCase)
            && string.Equals(execution.ActorStableId, NovaAppId, StringComparison.OrdinalIgnoreCase)
            && execution.BeneficiaryKind.Equals("user", StringComparison.OrdinalIgnoreCase)
            && string.Equals(execution.BeneficiaryId, discussion.OwnerId,
                StringComparison.OrdinalIgnoreCase)
            && execution.HasNovaBrowserContext
            && execution.ParentExecutionId is null;
    }

    public static bool IsExplicitHuman(ClaimsPrincipal principal)
        => principal.Identity?.IsAuthenticated == true
            && !string.Equals(principal.Identity.AuthenticationType,
                LocalDefaultAuthenticationType, StringComparison.Ordinal)
            && !IsExecution(principal);

    private static bool IsExecution(ClaimsPrincipal principal)
        => string.Equals(principal.FindFirstValue(TokenUseClaim), ExecutionTokenUse,
            StringComparison.OrdinalIgnoreCase);

    private static bool TryReadExecution(
        ClaimsPrincipal principal,
        out ParsedExecution? execution)
    {
        execution = null;
        var raw = principal.FindFirstValue(ExecutionIdentityClaim);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("actor", out var actor)
                || !root.TryGetProperty("beneficiary", out var beneficiary))
                return false;

            var appId = root.TryGetProperty("app", out var app)
                ? String(app, "id")
                : null;
            var actorKind = String(actor, "kind");
            var actorStableId = String(actor, "id");
            var actorId = String(actor, "entityId") ?? actorStableId;
            var beneficiaryKind = String(beneficiary, "kind");
            var beneficiaryId = String(beneficiary, "id");
            if (actorKind is null || actorId is null || beneficiaryKind is null)
                return false;

            var hasNovaBrowserContext = root.TryGetProperty("context", out var contexts)
                && contexts.ValueKind == JsonValueKind.Array
                && contexts.EnumerateArray().Any(item =>
                    string.Equals(String(item, "kind"), "browser",
                        StringComparison.OrdinalIgnoreCase)
                    && IsNovaRoute(String(item, "route")));
            execution = new ParsedExecution(
                appId, actorKind, actorStableId, actorId,
                beneficiaryKind, beneficiaryId, hasNovaBrowserContext,
                String(root, "parentExecutionId"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? String(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool IsNovaRoute(string? route)
        => string.Equals(route, NovaRoute, StringComparison.OrdinalIgnoreCase)
            || route?.StartsWith(NovaRoute + "/", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record ParsedExecution(
        string? AppId,
        string ActorKind,
        string? ActorStableId,
        string ActorId,
        string BeneficiaryKind,
        string? BeneficiaryId,
        bool HasNovaBrowserContext,
        string? ParentExecutionId);
}
