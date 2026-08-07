using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Supplies the effective agent avatar to RedLeaf's canonical entity display projection.
/// AgentDirectory already owns the outfit-over-base precedence used everywhere else in Nova.
/// </summary>
public sealed class NovaAgentEntityDisplayEnricher(AgentDirectory agents)
    : IEntityDisplayEnricher
{
    public string TypeSlug => "agent";

    public async Task<EntityDisplayEnrichment?> EnrichAsync(
        LeafEntity entity, CancellationToken ct = default)
    {
        var agent = await agents.GetAgentAsync(entity.Id.ToString(), ct);
        return Project(agent);
    }

    internal static EntityDisplayEnrichment? Project(AgentInfo? agent)
    {
        if (agent is null) return null;
        return new EntityDisplayEnrichment(
            Subtitle: "Agent",
            ImageUrl: AgentDirectory.BuildAvatarUrl(agent.AvatarFilename),
            ImageShape: "circle");
    }
}
