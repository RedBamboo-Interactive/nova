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

    public static async Task<ComputeProvenance> CreateAsync(
        IEntityStore entities,
        AgentInfo agent, ComputeBeneficiary beneficiary,
        string route, IReadOnlyList<ComputeContextReference> context,
        string entrypointKind = "http", string? method = null,
        string? requestId = null, string? correlationId = null, string? parentJobId = null,
        CancellationToken ct = default)
    {
        var app = await ResolveAppAsync(entities, ct);
        return new(
            ComputeProvenance.CurrentSchemaVersion,
            new ComputeOrigin("redleaf", app, new ComputeEntrypoint(entrypointKind, route, method)),
            new ComputeActor("agent", agent.Name, agent.Id, agent.Slug,
                AgentDirectory.BuildAvatarUrl(agent.AvatarFilename)),
            beneficiary,
            context,
            new ComputeTrace(requestId, correlationId, parentJobId),
            ComputeProvenanceAssurance.Verified,
            DateTimeOffset.UtcNow);
    }

    private static async Task<ComputeAppReference> ResolveAppAsync(
        IEntityStore entities, CancellationToken ct)
    {
        var plugins = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "plugin",
            Search = NovaAppPlugin.PluginId,
            Limit = 50,
        }, ct);
        var plugin = plugins.FirstOrDefault(entity =>
            entity.Slug.Equals(NovaAppPlugin.PluginId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Nova plugin entity is missing");

        var icon = Str(plugin.Data, "icon") ?? Str(plugin.Data, "icon_url");
        var color = await ResolveColorAsync(entities, Str(plugin.Data, "color"), ct);
        return new ComputeAppReference(
            "plugin", plugin.Slug, plugin.Id.ToString(), plugin.Name, icon, color);
    }

    private static async Task<string?> ResolveColorAsync(
        IEntityStore entities, string? colorReference, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(colorReference)) return null;
        if (!Guid.TryParse(colorReference, out var colorId)) return colorReference;

        var color = await entities.GetAsync(colorId, ct);
        return color is { TypeSlug: "color" } ? Str(color.Data, "hex") : null;
    }

    private static string? Str(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var result) ? result : null;
}
