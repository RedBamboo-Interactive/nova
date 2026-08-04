using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// Heartbeat management surface. Provisioning is driven by the agent entity's
/// <c>heartbeat</c> config block; these endpoints trigger reconciliation (after a
/// config edit), expose per-agent status, and let the goodnight automation signal
/// the day boundary directly.
/// </summary>
public static class HeartbeatEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/heartbeat/reconcile", async (HeartbeatService heartbeat) =>
            Results.Ok(await heartbeat.ReconcileAsync()));

        group.MapGet("/heartbeat/status", async (
            HeartbeatService heartbeat,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            var agents = await entities.QueryAsync(new Leaf.Sdk.EntityQuery { TypeSlug = "agent", Limit = 50 });
            var result = new JsonArray();
            foreach (var agent in agents)
            {
                var config = HeartbeatConfig.Parse(agent.Data);
                if (config == null) continue;
                var automation = config.AutomationId is { } automationId
                    ? await entities.GetAsync(automationId)
                    : null;

                var discussion = await heartbeat.GetDiscussionAsync(agent.Id.ToString());
                JsonObject? state = null;
                if (discussion != null)
                {
                    var raw = await entities.GetAsync(discussion.EntityId);
                    state = new JsonObject
                    {
                        ["discussionId"] = discussion.Id,
                        ["status"] = discussion.Status,
                        ["sessionId"] = discussion.SessionId,
                        ["lastTickAt"] = raw?.Data["hb_last_tick_at"]?.DeepClone(),
                    };
                }

                result.Add(new JsonObject
                {
                    ["agent"] = agent.Slug,
                    ["automationId"] = config.AutomationId?.ToString(),
                    ["enabled"] = automation?.Data["enabled"]?.DeepClone(),
                    ["schedule"] = automation?.Data["trigger"]?["expression"]?.DeepClone(),
                    ["timezone"] = automation?.Data["trigger"]?["timezone"]?.DeepClone(),
                    ["qualityTier"] = config.QualityTier,
                    ["heartbeat"] = state,
                });
            }
            return Results.Ok(result);
        });

        group.MapPost("/heartbeat/{agentId}/rotate", async (string agentId, HeartbeatService heartbeat) =>
        {
            var fresh = await heartbeat.RotateAsync(agentId, "manual-reset");
            return Results.Ok(new { ok = true, discussionId = fresh?.Id });
        });

        // Alias: goodnight automation calls end-of-day, same as rotate.
        group.MapPost("/heartbeat/{agentId}/end-of-day", async (string agentId, HeartbeatService heartbeat) =>
        {
            var fresh = await heartbeat.RotateAsync(agentId, "goodnight");
            return Results.Ok(new { ok = true, discussionId = fresh?.Id });
        });
    }
}
