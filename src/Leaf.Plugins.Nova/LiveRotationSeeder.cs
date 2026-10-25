using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Seeds Nova's daily LIVE rotation (<c>system:live-rotation</c>, 03:30 UTC) once per
/// installation. The standalone app seeded it as the <c>builtin:live-rotation</c>
/// action; that action no longer exists, so the seed is the same schedule expressed
/// the way the kernel migrated it: a workflow whose single action POSTs
/// <c>/api/apps/nova/discussions/live/rotate</c>.
/// <para>
/// "Once" is recorded on the Nova Agent (<see cref="ReferenceField"/>), mirroring the
/// heartbeat's <c>heartbeat_automation</c> reference. After that the automation belongs
/// to the user: it is never recreated if they removed it, never re-enabled if they
/// disabled it, and an archived or edited one is left alone. An automation that already
/// exists under <see cref="Slug"/> (the carried-over entity on upgraded installs) is
/// adopted untouched.
/// </para>
/// </summary>
public sealed class LiveRotationSeeder(
    IEntityStore entities,
    IWorkflowAutomations workflows,
    ILogger<LiveRotationSeeder> log)
{
    public const string Slug = "automation-nova-system-live-rotation";
    public const string Name = "system:live-rotation";
    public const string ReferenceField = "live_rotation_automation";
    public const string Schedule = "30 3 * * *";
    public const string RotateUrl = "http://127.0.0.1:18804/api/apps/nova/discussions/live/rotate";

    /// <summary>Returns the automation when this call seeded or adopted it; null when
    /// nothing was done (already seeded earlier, or not ready yet).</summary>
    public async Task<LeafEntity?> EnsureAsync(LeafEntity agent, CancellationToken ct = default)
    {
        // Re-read: callers hold a snapshot, and the marker must not be judged stale.
        var current = await entities.GetAsync(agent.Id, ct) ?? agent;
        if (current.Data[ReferenceField] is JsonValue marker
            && marker.TryGetValue<string>(out var seeded)
            && !string.IsNullOrWhiteSpace(seeded))
            return null;

        var existing = await entities.GetBySlugAsync("automation", Slug, ct);
        if (existing is null)
        {
            var users = await entities.QueryAsync(new EntityQuery { TypeSlug = "user", Limit = 2 }, ct);
            // No user yet: the first-run flow has not created one. Retry on a later
            // reconcile instead of authoring an unowned automation.
            if (users.Count == 0) return null;

            existing = await workflows.EnsureAsync(
                Definition(agent, users.Count == 1 ? users[0].Id.ToString() : null), ct);
            log.LogInformation("Seeded the daily LIVE rotation automation {AutomationId}", existing.Id);
        }

        await entities.PatchAsync(agent.Id,
            new JsonObject { [ReferenceField] = existing.Id.ToString() }, ct: ct);
        return existing;
    }

    internal static WorkflowAutomationDefinition Definition(LeafEntity agent, string? ownerId)
    {
        var beneficiary = ownerId is null
            ? new JsonObject
            {
                ["kind"] = "unreviewed",
                ["reason"] = "Live rotation has no explicitly authored beneficiary",
                ["authored"] = false,
            }
            : new JsonObject
            {
                ["kind"] = "user",
                ["id"] = ownerId,
                ["authored"] = true,
                ["authored_by"] = "plugin:nova:live-rotation",
                ["authored_at"] = DateTimeOffset.UtcNow,
            };
        var ownership = new JsonObject
        {
            ["app"] = "nova",
            ["actor_agent"] = agent.Id.ToString(),
            ["beneficiary"] = beneficiary,
        };
        if (ownerId is not null) ownership["user_id"] = ownerId;

        return new WorkflowAutomationDefinition
        {
            Slug = Slug,
            Name = Name,
            NodeType = "http-request",
            Enabled = true,
            TriggerNodeId = "scheduled-entry",
            WorkflowGraph = BuildGraph(),
            Trigger = new JsonObject
            {
                ["kind"] = "cron",
                ["expression"] = Schedule,
                ["timezone"] = "UTC",
                ["misfire_policy"] = "coalesce",
                ["migration_policy"] = "canonical",
                ["migration_reason"] = "The daily rotation has always run at 03:30 UTC",
            },
            ExecutionPolicy = new JsonObject
            {
                ["overlap"] = "forbid",
                ["timeout_seconds"] = 3600,
                ["lease_seconds"] = 300,
                ["max_failures"] = 20,
                ["retry_count"] = 0,
                ["retry_delay_seconds"] = 30,
                ["recovery"] = "at-least-once",
                ["recovery_reason"] = "Workflow recovery is constrained by each node contract; unsafe nodes require reconciliation",
            },
            Ownership = ownership,
            Metadata = new JsonObject
            {
                ["icon"] = "ph-bold ph-arrows-clockwise",
                ["owner_id"] = ownerId,
            },
            Description = "Daily LIVE discussion rotation",
            ReviewReason = "Nova default; disable or edit it in Automations",
        };
    }

    private static JsonObject BuildGraph() => new()
    {
        ["nodes"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "scheduled-entry",
                ["type"] = "trigger",
                ["position"] = new JsonObject { ["x"] = 80, ["y"] = 160 },
                ["data"] = new JsonObject
                {
                    ["label"] = "Scheduled entry",
                    ["config"] = new JsonObject { ["mode"] = "entrypoint" },
                },
            },
            new JsonObject
            {
                ["id"] = "action",
                ["type"] = "http-request",
                ["position"] = new JsonObject { ["x"] = 420, ["y"] = 160 },
                ["data"] = new JsonObject
                {
                    ["label"] = Name,
                    ["config"] = new JsonObject
                    {
                        ["url"] = RotateUrl,
                        ["method"] = "POST",
                        ["timeout"] = 60,
                    },
                },
            },
        },
        ["edges"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "scheduled-entry-to-action",
                ["source"] = "scheduled-entry",
                ["target"] = "action",
                ["sourceHandle"] = "onTrigger",
                ["targetHandle"] = "__event",
            },
        },
        ["viewport"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["zoom"] = 1 },
    };
}
