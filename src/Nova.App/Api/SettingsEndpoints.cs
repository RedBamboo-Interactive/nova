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
        registry.MapGet("/api/settings", "Get current settings including identity", () =>
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
        });

    }
}

public class IdentityUpdateRequest
{
    public string Content { get; set; } = "";
}
