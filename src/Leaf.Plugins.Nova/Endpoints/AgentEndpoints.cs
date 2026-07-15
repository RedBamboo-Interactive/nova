using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// Agent picker + outfit browser endpoints. Read-only over the kernel's agent
/// entities; avatars are same-origin asset URLs now, so the old cross-service
/// proxy endpoints are gone.
/// </summary>
public static class AgentEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/agents", async (AgentDirectory agents) =>
        {
            var list = await agents.GetAgentsAsync();
            return Results.Ok(list.Select(a => new
            {
                id = a.Id,
                slug = a.Slug,
                name = a.Name,
                description = a.Description,
                status = a.Status,
                avatarUrl = AgentDirectory.BuildAvatarUrl(a.AvatarFilename) ?? "/nova-avatar.png",
            }));
        });

        group.MapGet("/avatar", async (AgentDirectory agents) =>
        {
            var novaId = agents.NovaAgentId;
            var agent = novaId != null ? await agents.GetAgentAsync(novaId) : null;
            var url = AgentDirectory.BuildAvatarUrl(agent?.AvatarFilename);
            return Results.Redirect(url ?? "/nova-avatar.png");
        });

        group.MapGet("/outfits", async (HttpContext ctx, AgentDirectory agents, [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            var agentId = ctx.Request.Query.TryGetValue("agentId", out var aid) && !string.IsNullOrEmpty(aid.ToString())
                ? aid.ToString() : agents.NovaAgentId;
            if (agentId == null || !Guid.TryParse(agentId, out var agentGuid))
                return Results.Ok(new { baseAvatarUrl = "/nova-avatar.png", currentOverride = (string?)null, outfits = Array.Empty<object>() });

            try
            {
                var agent = await entities.GetAsync(agentGuid);
                if (agent == null)
                    return Results.Ok(new { baseAvatarUrl = "/nova-avatar.png", currentOverride = (string?)null, outfits = Array.Empty<object>() });

                var baseAvRaw = Str(agent.Data, "avatar");
                var baseAvatarUrl = baseAvRaw != null
                    ? (baseAvRaw.StartsWith('/') || baseAvRaw.Contains("://") ? baseAvRaw : $"/api/assets/{baseAvRaw}")
                    : "/nova-avatar.png";

                var currentOverride = Str(agent.Data, "outfit");

                var items = await entities.QueryAsync(new EntityQuery
                {
                    TypeSlug = "outfit",
                    DataEquals = new Dictionary<string, object?> { ["agent"] = agentId },
                    Limit = 30,
                });

                var outfits = items
                    .OrderByDescending(o => o.CreatedAt)
                    .Select(o => (object)new
                    {
                        id = o.Id.ToString(),
                        name = o.Name,
                        url = Str(o.Data, "asset"),
                        prompt = Str(o.Data, "prompt"),
                        reasoning = Str(o.Data, "reasoning"),
                        nsfw = IsTruthy(o.Data, "nsfw"),
                        date = o.CreatedAt.UtcDateTime.ToString("o"),
                        active = o.Id.ToString() == currentOverride,
                    })
                    .ToList();

                return Results.Ok(new { baseAvatarUrl, currentOverride, outfits });
            }
            catch
            {
                return Results.Ok(new { baseAvatarUrl = "/nova-avatar.png", currentOverride = (string?)null, outfits = Array.Empty<object>() });
            }
        });

        group.MapPost("/outfits/select", async (HttpContext ctx, AgentDirectory agents, [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities, [FromKeyedServices(NovaAppPlugin.PluginId)] IPluginEvents events, LiveEvents live, DiscussionStore store, EventInjector injector) =>
        {
            JsonObject? body;
            try { body = await ctx.Request.ReadFromJsonAsync<JsonObject>(); }
            catch { return Results.BadRequest(new { error = "Invalid JSON" }); }

            var outfitId = body?["outfitId"]?.GetValue<string>();
            var discussionId = body?["discussionId"]?.GetValue<string>();

            var novaId = body?["agentId"]?.GetValue<string>() ?? agents.NovaAgentId;
            if (novaId == null || !Guid.TryParse(novaId, out var novaGuid))
                return Results.BadRequest(new { error = "No agent configured" });

            try
            {
                await entities.PatchAsync(novaGuid, new JsonObject { ["outfit"] = outfitId ?? "" });

                // Resolve asset URL from the outfit entity (the agent stores the ID, not the URL)
                string resolvedUrl = "";
                string? outfitName = null;
                if (!string.IsNullOrEmpty(outfitId) && Guid.TryParse(outfitId, out var outfitGuid))
                {
                    var outfit = await entities.GetAsync(outfitGuid);
                    if (outfit != null)
                    {
                        resolvedUrl = Str(outfit.Data, "asset") ?? "";
                        outfitName = outfit.Name;
                    }
                }

                await agents.GetAgentsAsync(forceRefresh: true);

                await events.PublishAsync("agent.avatar-changed", new JsonObject
                {
                    ["agentId"] = novaId,
                    ["url"] = resolvedUrl,
                });

                _ = string.IsNullOrEmpty(outfitId)
                    ? live.PostAsync("outfit", "Reset to base avatar", new { status = "reset" })
                    : live.PostAsync("outfit", $"Changed into \"{outfitName ?? "new outfit"}\"",
                        new { outfitId, outfitName, asset = resolvedUrl });


                return Results.Ok(new { success = true, url = resolvedUrl });
            }
            catch
            {
                return Results.StatusCode(502);
            }
        });

        group.MapPost("/outfits", async (HttpContext ctx, AgentDirectory agents, [FromKeyedServices(NovaAppPlugin.PluginId)] IEntityStore entities) =>
        {
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<JsonObject>()
                    ?? throw new InvalidOperationException("empty body");
                var assetUrl = body["url"]?.GetValue<string>();
                var prompt = body["prompt"]?.GetValue<string>();
                var name = body["name"]?.GetValue<string>() ?? "Outfit";
                var reasoning = body["reasoning"]?.GetValue<string>();
                var nsfw = body["nsfw"]?.GetValue<bool>() ?? false;

                var agentId = body["agentId"]?.GetValue<string>() ?? agents.NovaAgentId;

                var data = new JsonObject
                {
                    ["agent"] = agentId,
                    ["asset"] = assetUrl,
                    ["prompt"] = prompt,
                    ["nsfw"] = nsfw,
                };
                if (reasoning != null) data["reasoning"] = reasoning;

                var entity = await entities.CreateAsync("outfit", name, data);
                return Results.Ok(new { success = true, id = entity.Id.ToString() });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static string? Str(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : null;
    }

    private static bool IsTruthy(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s)) return s == "true";
        return false;
    }
}
