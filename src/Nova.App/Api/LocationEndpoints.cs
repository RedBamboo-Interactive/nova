using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using RedBamboo.AppHost.Discovery;
using Nova.App.Services;

namespace Nova.App.Api;

public static class LocationEndpoints
{
    public static void MapLocationEndpoints(this EndpointRegistry registry)
    {
        registry.MapPost("/api/location/update", "Update Laurent's current GPS position (called from the frontend every 120s)", async (HttpContext ctx) =>
        {
            var svc = ctx.RequestServices.GetRequiredService<LocationService>();

            double? lat = null, lng = null, accuracy = null;
            string? timezone = null;
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<LocationUpdateRequest>();
                lat = body?.Latitude;
                lng = body?.Longitude;
                accuracy = body?.Accuracy;
                timezone = body?.Timezone;
            }
            catch { return Results.BadRequest(new { error = "Invalid JSON" }); }

            if (lat == null || lng == null)
                return Results.BadRequest(new { error = "latitude and longitude are required" });

            svc.UpdateLocation(lat.Value, lng.Value, accuracy, timezone);
            return Results.Ok(new { success = true });
        });

        registry.MapGet("/api/location/current", "Get Laurent's latest known GPS location, zone, and accuracy", (HttpContext ctx) =>
        {
            var svc = ctx.RequestServices.GetRequiredService<LocationService>();
            var loc = svc.Latest;

            if (loc == null)
                return Results.Ok(new { available = false });

            return Results.Ok(new
            {
                available = true,
                latitude = loc.Latitude,
                longitude = loc.Longitude,
                accuracy = loc.Accuracy,
                timestamp = loc.Timestamp.ToString("o"),
                zone = loc.Zone,
                place = loc.PlaceName,
            });
        });
    }
}

public class LocationUpdateRequest
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Accuracy { get; set; }
    public string? Timezone { get; set; }
}
