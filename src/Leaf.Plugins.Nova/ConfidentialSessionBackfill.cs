using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

public sealed class ConfidentialSessionBackfill(
    DiscussionStore discussions,
    AgentDirectory agents,
    RedComputeClient redCompute,
    IEntityStore entities,
    ILogger<ConfidentialSessionBackfill> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3 && !ct.IsCancellationRequested; attempt++)
        {
            var pending = (await discussions.ListAsync(ct: ct))
                .Where(d => d.Confidential && d.SessionId is not null
                    && d.AgentId is not null && d.OwnerId is not null)
                .ToList();
            var failures = 0;
            foreach (var discussion in pending)
            {
                var agent = await agents.GetAgentAsync(discussion.AgentId!, ct);
                if (agent is null) { failures++; continue; }
                var beneficiary = await NovaComputeProvenance.ResolveBeneficiaryAsync(
                    entities, discussion.OwnerId, ct);
                var provenance = await NovaComputeProvenance.CreateAsync(
                    entities, agent, beneficiary,
                    "/api/apps/nova/startup/confidential-session-backfill",
                    [new ComputeContextReference("discussion", discussion.Id),
                     new ComputeContextReference("session", discussion.SessionId)],
                    entrypointKind: "startup", ct: ct);
                if (!await redCompute.SetConfidentialAsync(discussion.SessionId!, provenance, ct))
                    failures++;
            }

            if (failures == 0) return;
            logger.LogWarning(
                "Confidential session backfill attempt {Attempt} left {Failures} session(s) pending",
                attempt, failures);
            if (attempt < 3)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
        }
    }
}
