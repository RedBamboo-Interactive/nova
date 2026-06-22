using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RedBamboo.AppHost.Discovery;
using Nova.App.Services;

namespace Nova.App.Api;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this EndpointRegistry registry, MemoryManager memory)
    {
        registry.MapGet("/api/settings", "Get current settings including identity, docker, tunnel", () =>
        {
            var identity = memory.ReadIdentity();
            var config = App.Config;

            return Results.Ok(new
            {
                identity,
                general = new
                {
                    config.Port,
                },
                docker = new
                {
                    enabled = !string.IsNullOrEmpty(config.DockerImage),
                    image = config.DockerImage,
                },
                tunnel = new
                {
                    config.Tunnel.Enabled,
                    config.Tunnel.Hostname,
                    hasToken = !string.IsNullOrEmpty(config.Tunnel.AccessToken),
                },
            });
        });

        registry.MapPut("/api/settings/identity", "Update Nova's identity", (IdentityUpdateRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Identity content is required" });

            memory.WriteIdentity(request.Content);
            memory.GenerateClaudeMd();
            return Results.Ok(new { success = true });
        }).WithParam("content", "string", required: true,
            description: "Full identity markdown (replaces config/identity.md and regenerates CLAUDE.md)",
            location: ParamLocation.Body);

        registry.MapPut("/api/settings/docker", "Enable or disable Docker containerization for AI sessions. Set image to a Docker image name to enable, or null/empty to disable.", (DockerSettingsRequest request) =>
        {
            App.Config.DockerImage = string.IsNullOrWhiteSpace(request.Image) ? null : request.Image.Trim();
            App.ConfigManager.Save();
            return Results.Ok(new
            {
                success = true,
                enabled = !string.IsNullOrEmpty(App.Config.DockerImage),
                image = App.Config.DockerImage,
            });
        }).WithParam("image", "string",
            description: "Docker image name to run AI sessions in (e.g. 'redbamboo/sandbox'). Null or empty disables containerization",
            location: ParamLocation.Body);

    }
}

public class IdentityUpdateRequest
{
    public string Content { get; set; } = "";
}

public class DockerSettingsRequest
{
    public string? Image { get; set; }
}
