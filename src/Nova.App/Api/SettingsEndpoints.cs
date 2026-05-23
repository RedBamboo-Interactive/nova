using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nova.App.Services;

namespace Nova.App.Api;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app, MemoryManager memory)
    {
        app.MapGet("/api/settings", () =>
        {
            var identity = memory.ReadIdentity();
            var config = App.Config;

            return Results.Ok(new
            {
                identity,
                general = new
                {
                    config.Port,
                    config.RedComputeUrl,
                },
                tunnel = new
                {
                    config.Tunnel.Enabled,
                    config.Tunnel.Hostname,
                    hasToken = !string.IsNullOrEmpty(config.Tunnel.AccessToken),
                },
            });
        });

        app.MapPut("/api/settings/identity", (IdentityUpdateRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Content))
                return Results.BadRequest(new { error = "Identity content is required" });

            memory.WriteIdentity(request.Content);
            return Results.Ok(new { success = true });
        });

        app.MapPut("/api/settings/general", (GeneralSettingsRequest request) =>
        {
            var config = App.Config;

            if (request.RedComputeUrl != null)
                config.RedComputeUrl = request.RedComputeUrl;
            App.ConfigManager.Save();
            return Results.Ok(new { success = true });
        });
    }
}

public class IdentityUpdateRequest
{
    public string Content { get; set; } = "";
}

public class GeneralSettingsRequest
{
    public string? RedComputeUrl { get; set; }
}
