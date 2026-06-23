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
        registry.MapGet("/api/settings", "Get current settings: docker, tunnel", () =>
        {
            var config = App.Config;

            return Results.Ok(new
            {
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

public class DockerSettingsRequest
{
    public string? Image { get; set; }
}
