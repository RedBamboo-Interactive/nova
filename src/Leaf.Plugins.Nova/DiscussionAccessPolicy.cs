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
        => !IsExecution(context.User)
            && IsExplicitHuman(context.User)
            && !string.IsNullOrWhiteSpace(discussion.OwnerId)
            && string.Equals(context.User.FindFirstValue("sub"), discussion.OwnerId,
                StringComparison.OrdinalIgnoreCase);

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

            var actorKind = String(actor, "kind");
            var actorId = String(actor, "entityId") ?? String(actor, "id");
            var beneficiaryKind = String(beneficiary, "kind");
            var beneficiaryId = String(beneficiary, "id");
            if (actorKind is null || actorId is null || beneficiaryKind is null)
                return false;

            execution = new ParsedExecution(
                actorKind, actorId, beneficiaryKind, beneficiaryId);
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

    private sealed record ParsedExecution(
        string ActorKind,
        string ActorId,
        string BeneficiaryKind,
        string? BeneficiaryId);
}
