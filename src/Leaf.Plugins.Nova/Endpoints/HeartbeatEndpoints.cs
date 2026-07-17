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
                        ["dayClosedAt"] = raw?.Data["hb_day_closed_at"]?.DeepClone(),
                    };
                }

                result.Add(new JsonObject
                {
                    ["agent"] = agent.Slug,
                    ["enabled"] = config.Enabled,
                    ["schedule"] = config.Schedule,
                    ["qualityTier"] = config.QualityTier,
                    ["heartbeat"] = state,
                });
            }
            return Results.Ok(result);
        });

        // The goodnight automation can close the day explicitly instead of waiting
        // for LIVE rotation or the 02:55 fallback.
        group.MapPost("/heartbeat/{agentId}/end-of-day", async (string agentId, HeartbeatService heartbeat) =>
        {
            await heartbeat.RunEndOfDayAsync(agentId, "goodnight");
            return Results.Ok(new { ok = true });
        });
    }
}
