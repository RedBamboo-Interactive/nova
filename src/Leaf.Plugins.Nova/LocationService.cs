using System.Net.Http.Json;
using System.Text.Json;

namespace Leaf.Plugins.Nova;

public sealed record LocationReading(double Latitude, double Longitude, double? Accuracy, DateTime Timestamp, string? Zone, string? PlaceName, string? Timezone);

/// <summary>GPS position pushed by the frontend; zone detection + reverse geocoding.</summary>
public sealed class LocationService(LiveEvents live)
{
    private static readonly (string Name, double Lat, double Lng, double RadiusMeters)[] Zones =
    [
        ("Home",     47.3396, 8.5177, 100),
        ("Sihlcity", 47.3577, 8.5222, 200),
    ];

    private LocationReading? _latest;
    private (double Lat, double Lng)? _lastEventPos;
    private readonly HashSet<string> _insideZones = [];

    private string? _cachedPlaceName;
    private (double Lat, double Lng)? _cachedGeoPos;

    public LocationReading? Latest => _latest;

    public void UpdateLocation(double lat, double lng, double? accuracy, string? timezone = null)
    {
        var zone = DetectZone(lat, lng);
        var placeName = zone ?? ResolvePlace(lat, lng);
        _latest = new LocationReading(lat, lng, accuracy, DateTime.UtcNow, zone, placeName, timezone ?? _latest?.Timezone);

        bool zoneEventFired = false;
        foreach (var z in Zones)
        {
            var dist = HaversineMeters(lat, lng, z.Lat, z.Lng);
            var inside = dist <= z.RadiusMeters;
            var wasInside = _insideZones.Contains(z.Name);

            if (inside && !wasInside)
            {
                _insideZones.Add(z.Name);
                _ = live.PostAsync("location", $"Laurent arrived at {z.Name}", new { lat, lng, zone = z.Name });
                zoneEventFired = true;
            }
            else if (!inside && wasInside)
            {
                _insideZones.Remove(z.Name);
                _ = live.PostAsync("location", $"Laurent left {z.Name}", new { lat, lng, zone = z.Name });
                zoneEventFired = true;
            }
        }

        if (!zoneEventFired && _lastEventPos != null &&
            HaversineMeters(lat, lng, _lastEventPos.Value.Lat, _lastEventPos.Value.Lng) > 200)
        {
            var label = placeName ?? $"{lat:F4}, {lng:F4}";
            _ = live.PostAsync("location", $"Laurent is near {label}", new { lat, lng, place = placeName });
        }

        if (zoneEventFired || _lastEventPos == null ||
            HaversineMeters(lat, lng, _lastEventPos.Value.Lat, _lastEventPos.Value.Lng) > 200)
        {
            _lastEventPos = (lat, lng);
        }
    }

    private string? ResolvePlace(double lat, double lng)
    {
        if (_cachedGeoPos.HasValue && HaversineMeters(lat, lng, _cachedGeoPos.Value.Lat, _cachedGeoPos.Value.Lng) < 200)
            return _cachedPlaceName;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Leaf-Nova/1.0");
            var url = $"https://nominatim.openstreetmap.org/reverse?lat={lat}&lon={lng}&format=json&zoom=16&addressdetails=1";
            var resp = http.GetFromJsonAsync<JsonElement>(url).GetAwaiter().GetResult();
            var name = resp.TryGetProperty("display_name", out var dn) ? dn.GetString() : null;
            if (name != null)
            {
                var parts = name.Split(',');
                _cachedPlaceName = parts.Length >= 2 ? $"{parts[0].Trim()}, {parts[1].Trim()}" : parts[0].Trim();
            }
            else
            {
                _cachedPlaceName = null;
            }
            _cachedGeoPos = (lat, lng);
            return _cachedPlaceName;
        }
        catch
        {
            return _cachedPlaceName;
        }
    }

    private static string? DetectZone(double lat, double lng)
    {
        foreach (var z in Zones)
        {
            if (HaversineMeters(lat, lng, z.Lat, z.Lng) <= z.RadiusMeters)
                return z.Name;
        }
        return null;
    }

    private static double HaversineMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371000;
        var dLat = ToRad(lat2 - lat1);
        var dLng = ToRad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
              + Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2))
              * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRad(double deg) => deg * Math.PI / 180;
}

/// <summary>Coarse city-level IP geolocation, resolved once at startup.</summary>
public sealed class GeoLocationService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private string? _cached;

    public string? Location => _cached;

    public async Task ResolveAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("http://ip-api.com/json/?fields=city,country");
            using var doc = JsonDocument.Parse(json);
            var city = doc.RootElement.TryGetProperty("city", out var c) ? c.GetString() : null;
            var country = doc.RootElement.TryGetProperty("country", out var co) ? co.GetString() : null;
            if (city != null && country != null)
                _cached = $"{city}, {country}";
        }
        catch { }
    }
}
