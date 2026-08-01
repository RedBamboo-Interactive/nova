using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Leaf.Plugins.Nova.Endpoints;

/// <summary>
/// Agent picker endpoints. Read-only over the kernel's agent entities; avatars
/// are same-origin asset URLs. Optional avatar experiences are contributed by
/// extensions through Nova's declared frontend slot.
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
                workspaceId = a.WorkspaceId,
                provider = a.Provider,
                qualityMode = a.QualityMode,
            }));
        });

        group.MapGet("/avatar", async (AgentDirectory agents) =>
        {
            var novaId = agents.NovaAgentId;
            var agent = novaId != null ? await agents.GetAgentAsync(novaId) : null;
            var url = AgentDirectory.BuildAvatarUrl(agent?.AvatarFilename);
            return Results.Redirect(url ?? "/nova-avatar.png");
        });
    }
}
