using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public static class NovaComputeProvenance
{
    public static async Task<ComputeBeneficiary> ResolveBeneficiaryAsync(
        IEntityStore entities, string? ownerId, CancellationToken ct = default)
    {
        if (ownerId == "system")
            return new ComputeBeneficiary("system", Reason: "Nova work for a system-owned discussion");
        if (string.IsNullOrWhiteSpace(ownerId) || ownerId == "local-user")
            return new ComputeBeneficiary("unknown");
        if (Guid.TryParse(ownerId, out var id))
        {
            var user = await entities.GetAsync(id, ct);
            if (user is { TypeSlug: "user" })
                return new ComputeBeneficiary("user", user.Id.ToString(), user.Name,
                    Str(user.Data, "avatar_url") ?? Str(user.Data, "avatar"));
        }
        return new ComputeBeneficiary("user", ownerId);
    }

    public static ComputeProvenance Create(AgentInfo agent, ComputeBeneficiary beneficiary,
        string route, IReadOnlyList<ComputeContextReference> context,
        string entrypointKind = "http", string? method = null,
        string? requestId = null, string? correlationId = null, string? parentJobId = null)
        => new(
            ComputeProvenance.CurrentSchemaVersion,
            new ComputeOrigin("redleaf",
                new ComputeAppReference("plugin", "nova", null, "Nova", "ph-fill ph-star-four"),
                new ComputeEntrypoint(entrypointKind, route, method)),
            new ComputeActor("agent", agent.Name, agent.Id, agent.Slug,
                AgentDirectory.BuildAvatarUrl(agent.AvatarFilename)),
            beneficiary,
            context,
            new ComputeTrace(requestId, correlationId, parentJobId),
            ComputeProvenanceAssurance.Verified,
            DateTimeOffset.UtcNow);

    private static string? Str(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
