using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Who the archive reconciler stops a session as. The sweep runs detached, with no request and so
/// no ambient identity, and RedCompute rightly refuses a mutation without one: until 2026-11-25 its
/// stop never landed, and a session idled on until RedCompute parked it. The stop is the outcome
/// the archive asked for, so it is signed as the discussion's own work: Nova's Agent as the actor,
/// the discussion's owner as the beneficiary (a person or the system), cause app, with the archive
/// it serves in the context. RedCompute still bounds it to that owner's own sessions.
/// </summary>
public static class ArchiveSweepIdentity
{
    public const string Route = "/api/apps/nova/startup/archive-reconciler";

    /// <summary>The context reference naming the archive a stop serves.</summary>
    public const string ArchiveContextKind = "discussion-archive";

    /// <summary>Null when the discussion has no session or no Agent to sign for: the sweep then
    /// stops nothing and the bounded wait decides, as before.</summary>
    public static async Task<ComputeProvenance?> CreateAsync(
        IEntityStore entities, AgentDirectory agents, DiscussionRead discussion, CancellationToken ct = default)
    {
        if (discussion.SessionId is null || discussion.AgentId is null) return null;
        var agent = await agents.GetAgentAsync(discussion.AgentId, ct);
        if (agent is null) return null;
        var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(entities, discussion.OwnerId, ct);
        return await NovaComputeProvenance.CreateAsync(
            entities, agent, beneficiary, Route,
            [
                new ComputeContextReference("discussion", discussion.Id, discussion.EntityId.ToString()),
                new ComputeContextReference("session", discussion.SessionId),
                new ComputeContextReference(ArchiveContextKind, discussion.Id),
            ],
            entrypointKind: "startup", ct: ct, cause: ComputeCauses.App);
    }
}
