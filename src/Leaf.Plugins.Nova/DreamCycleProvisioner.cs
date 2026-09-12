using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Installs Nova's portable default dream cycle only when the Agent has the three
/// local skills and no existing automation already expresses a dreaming layout.
/// Existing one-cron, three-cron, or edited workflows remain authoritative.
/// </summary>
public sealed class DreamCycleProvisioner(
    IEntityStore entities,
    IWorkflowAutomations workflows,
    ILogger<DreamCycleProvisioner> log)
{
    private static readonly string[] SkillSlugs =
        ["dreaming", "emotional-dreaming", "creative-dreaming"];

    public async Task<LeafEntity?> EnsureDefaultAsync(
        LeafEntity agent, CancellationToken ct = default)
    {
        if (!await HasAssignedSkillsAsync(agent, ct)) return null;

        var automations = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "automation",
            Limit = 500,
        }, ct);
        var flows = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "flow",
            Limit = 500,
        }, ct);
        if (automations.Concat(flows).Any(entity =>
                ReferencesDreamingSkill(entity, agent)))
            return null;

        var ownerId = agent.CreatedBy;
        if (string.IsNullOrWhiteSpace(ownerId)
            || ownerId.StartsWith("system", StringComparison.OrdinalIgnoreCase)
            || ownerId.StartsWith("plugin:", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning(
                "Skipping confidential dream-cycle provisioning for Agent {AgentId}: no user owner",
                agent.Id);
            return null;
        }

        var ownership = new JsonObject
        {
            ["app"] = "nova",
            ["actor_agent"] = agent.Id,
            ["user_id"] = ownerId,
            ["beneficiary"] = new JsonObject
            {
                ["kind"] = "user",
                ["id"] = ownerId,
                ["authored"] = true,
                ["authored_by"] = "plugin:nova:dream-cycle",
                ["authored_at"] = DateTimeOffset.UtcNow,
            },
        };

        return await workflows.EnsureAsync(new WorkflowAutomationDefinition
        {
            Slug = $"system-dream-cycle-{agent.Slug}",
            Name = $"system:dream-cycle:{agent.Slug}",
            NodeType = "nova-session",
            TriggerNodeId = "scheduled-entry",
            WorkflowGraph = BuildGraph(agent),
            NodeContext = new JsonObject { ["agent"] = agent.Id.ToString() },
            Enabled = true,
            Confidential = true,
            Trigger = new JsonObject
            {
                ["kind"] = "cron",
                ["expression"] = "0 4 * * *",
                ["timezone"] = TimeZoneInfo.Local.Id,
                ["misfire_policy"] = "coalesce",
                ["recovery_delay_seconds"] = 600,
                ["migration_policy"] = "canonical",
                ["migration_reason"] = "Portable Nova dream cycle uses installation-local wall-clock time",
            },
            ExecutionPolicy = new JsonObject
            {
                ["overlap"] = "forbid",
                ["timeout_seconds"] = 21_600,
                ["lease_seconds"] = 300,
                ["max_failures"] = 20,
                ["retry_count"] = 0,
                ["retry_delay_seconds"] = 30,
                ["recovery"] = "idempotency-key",
                ["recovery_reason"] = "Each sequential phase has a same-job durable checkpoint and a node-scoped child key",
            },
            Ownership = ownership,
            Metadata = new JsonObject
            {
                ["agent"] = agent.Id,
                ["owner_id"] = ownerId,
                ["portable_default"] = "nova/dream-cycle-v1",
            },
            Description = "Runs durable memory, emotional, and creative consolidation in sequence.",
            ReviewReason = "Portable Nova default; existing dreaming layouts are never replaced",
        }, ct);
    }

    private async Task<bool> HasAssignedSkillsAsync(LeafEntity agent, CancellationToken ct)
    {
        if (agent.Data["skills"] is not JsonArray assigned) return false;
        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in assigned)
        {
            if (item is not JsonValue value || !value.TryGetValue<string>(out var reference)
                || string.IsNullOrWhiteSpace(reference))
                continue;
            LeafEntity? skill = Guid.TryParse(reference, out var id)
                ? await entities.GetAsync(id, ct)
                : await entities.GetBySlugAsync(
                    "agent-skill", reference.Split('/').Last(), ct);
            if (skill?.TypeSlug == "agent-skill")
                resolved.Add(skill.Slug);
        }
        return SkillSlugs.All(resolved.Contains);
    }

    private static bool ReferencesDreamingSkill(LeafEntity entity, LeafEntity agent)
    {
        var data = entity.Data.ToJsonString();
        var text = $"{entity.Slug}\n{entity.Name}\n{data}";
        return ContainsAgentReference(entity.Data, agent)
            && SkillSlugs.Any(slug =>
                text.Contains(slug, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsAgentReference(JsonNode? node, LeafEntity agent)
    {
        if (node is JsonArray array)
            return array.Any(item => ContainsAgentReference(item, agent));
        if (node is not JsonObject obj) return false;
        foreach (var (key, value) in obj)
        {
            if (key is "agent" or "actor_agent"
                && value is JsonValue json
                && ((json.TryGetValue<Guid>(out var guid) && guid == agent.Id)
                    || (json.TryGetValue<string>(out var reference)
                        && (string.Equals(reference, agent.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(reference, agent.Slug, StringComparison.OrdinalIgnoreCase)))))
                return true;
            if (ContainsAgentReference(value, agent)) return true;
        }
        return false;
    }

    private static JsonObject BuildGraph(LeafEntity agent)
    {
        var nodes = new JsonArray
        {
            Node("scheduled-entry", "trigger", "Scheduled entry",
                new JsonObject { ["mode"] = "entrypoint" }, 80),
            SessionNode("dreaming", "Dreaming", agent,
                "Use $dreaming to consolidate recent activity into durable memory.", 400),
            SessionNode("emotional-dreaming", "Emotional dreaming", agent,
                "Use $emotional-dreaming to write the next mood state from recent conversations.", 720),
            SessionNode("creative-dreaming", "Creative dreaming", agent,
                "Use $creative-dreaming to develop and record proposals for open creative questions.", 1040),
        };
        return new JsonObject
        {
            ["nodes"] = nodes,
            ["edges"] = new JsonArray
            {
                Edge("scheduled-entry", "dreaming"),
                Edge("dreaming", "emotional-dreaming"),
                Edge("emotional-dreaming", "creative-dreaming"),
            },
            ["viewport"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1 },
        };
    }

    private static JsonObject SessionNode(
        string id, string label, LeafEntity agent, string prompt, double x)
        => Node(id, "nova-session", label, new JsonObject
        {
            ["agent"] = agent.Id,
            ["prompt"] = prompt,
            ["action_config"] = new JsonObject
            {
                ["qualityMode"] = "deep",
                ["timeout"] = 7_200,
                ["preCreateDiscussion"] = false,
            },
        }, x);

    private static JsonObject Node(
        string id, string type, string label, JsonObject config, double x)
        => new()
        {
            ["id"] = id,
            ["type"] = type,
            ["position"] = new JsonObject { ["x"] = x, ["y"] = 160 },
            ["data"] = new JsonObject
            {
                ["label"] = label,
                ["config"] = config,
            },
        };

    private static JsonObject Edge(string source, string target)
        => new()
        {
            ["id"] = $"{source}-to-{target}",
            ["source"] = source,
            ["target"] = target,
            ["sourceHandle"] = "onComplete",
            ["targetHandle"] = "__event",
        };
}
