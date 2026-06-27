using System.Net.Http;
using System.Text.Json;

namespace Nova.App.Services;

public class GeoLocationService
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
