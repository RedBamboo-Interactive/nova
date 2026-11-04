using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// Heartbeat management surface. Agent <c>live</c> owns the paired LIVE and
/// Heartbeat lifecycle. Schedule is stored on the canonical automation trigger and
/// execution settings are stored on its workflow action configuration. These endpoints trigger reconciliation, expose status, and let
/// the goodnight automation signal the day boundary directly.
/// </summary>
public static class HeartbeatEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/heartbeat/reconcile", async (HeartbeatService heartbeat) =>
            Results.Ok(await heartbeat.ReconcileAsync()))
            .WithSummary("Reconciles the heartbeat setup of every Agent with its automation and returns the result.");

        group.MapGet("/heartbeat/status", async (
            HeartbeatService heartbeat,
            [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            var agents = await entities.QueryAsync(new Leaf.Sdk.EntityQuery { TypeSlug = "agent", Limit = 50 });
            var result = new JsonArray();
            foreach (var agent in agents)
            {
                var config = await heartbeat.GetConfigurationAsync(agent);
                if (config == null) continue;
                var automation = HeartbeatService.AutomationId(agent.Data) is { } automationId
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
                    ["initiatives"] = HeartbeatService.AreInitiativesEnabled(agent.Data),
                    ["heartbeat"] = state,
                });
            }
            return Results.Ok(result);
        })
            .WithSummary("Returns the heartbeat status of each Agent that has one: schedule, timezone, enabled flag, quality tier, whether initiatives are on, and last tick.");

        group.MapPost("/heartbeat/{agentId}/rotate", async (string agentId, HeartbeatService heartbeat) =>
        {
            var fresh = await heartbeat.RotateAsync(agentId, "manual-reset");
            return Results.Ok(new { ok = true, discussionId = fresh?.Id });
        })
            .WithSummary("Rotates an Agent's heartbeat discussion (reason manual-reset) and returns the new discussion id.");

        // Alias: goodnight automation calls end-of-day, same as rotate.
        group.MapPost("/heartbeat/{agentId}/end-of-day", async (string agentId, HeartbeatService heartbeat) =>
        {
            var fresh = await heartbeat.RotateAsync(agentId, "goodnight");
            return Results.Ok(new { ok = true, discussionId = fresh?.Id });
        })
            .WithSummary("Ends the heartbeat day for an Agent by rotating its heartbeat discussion (reason goodnight), the same as rotate. Returns the new discussion id.");
    }
}
