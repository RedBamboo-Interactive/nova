using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nova.App.Services;

public sealed class ResolvedDevice
{
    public string Name { get; init; } = "unknown";
    public string Type { get; init; } = "unknown";
    public string Platform { get; init; } = "unknown";
    public string? Room { get; init; }
    public string? EntityId { get; init; }

    public string ShortLabel => EntityId != null ? Name : $"{Platform} {Type}";
}

public sealed class DeviceResolver
{
    private readonly HttpClient _redLeaf;
    private Dictionary<string, DeviceRecord> _byBrowserId = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DeviceResolver(HttpClient redLeaf)
    {
        _redLeaf = redLeaf;
    }

    public async Task<ResolvedDevice> ResolveAsync(string? userAgent, string? browserId)
    {
        if (DateTime.UtcNow - _lastRefresh > RefreshInterval)
            await RefreshDevicesAsync();

        var parsed = ParseUserAgent(userAgent ?? "");

        if (!string.IsNullOrEmpty(browserId))
        {
            if (_byBrowserId.TryGetValue(browserId, out var device))
            {
                return new ResolvedDevice
                {
                    Name = device.Name,
                    Type = device.DeviceType,
                    Platform = device.Platform ?? parsed.Platform,
                    Room = device.Room,
                    EntityId = device.EntityId,
                };
            }

            var registered = await AutoRegisterAsync(browserId, parsed);
            if (registered != null)
            {
                return new ResolvedDevice
                {
                    Name = registered.Name,
                    Type = registered.DeviceType,
                    Platform = registered.Platform ?? parsed.Platform,
                    Room = registered.Room,
                    EntityId = registered.EntityId,
                };
            }
        }

        return new ResolvedDevice
        {
            Name = parsed.Model ?? $"{parsed.Platform} {parsed.DeviceType}",
            Type = parsed.DeviceType,
            Platform = parsed.Platform,
        };
    }

    private async Task<DeviceRecord?> AutoRegisterAsync(string browserId, ParsedUa parsed)
    {
        try
        {
            var defaultName = parsed.Model ?? $"{parsed.Platform} {parsed.DeviceType}";
            var data = new Dictionary<string, object?>
            {
                ["browser_id"] = browserId,
                ["device_type"] = CapitalizeDeviceType(parsed.DeviceType),
                ["platform"] = parsed.Platform,
                ["room"] = parsed.DeviceType == "phone" ? "mobile" : "desk",
                ["default_volume"] = 50,
                ["spotify_device_id"] = browserId,
            };

            var body = new { name = defaultName, type_slug = "speaker", data };
            var resp = await _redLeaf.PostAsync("api/entities",
                new StringContent(JsonSerializer.Serialize(body, JsonOpts),
                    System.Text.Encoding.UTF8, "application/json"));

            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var entity = doc.RootElement;

            var record = new DeviceRecord
            {
                EntityId = entity.GetProperty("id").GetString()!,
                Name = defaultName,
                BrowserId = browserId,
                DeviceType = NormalizeDeviceType(CapitalizeDeviceType(parsed.DeviceType)),
                Platform = parsed.Platform,
            };

            _byBrowserId[browserId] = record;
            return record;
        }
        catch
        {
            return null;
        }
    }

    private async Task RefreshDevicesAsync()
    {
        try
        {
            var json = await _redLeaf.GetStringAsync("api/entities?type=speaker");
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");
            var map = new Dictionary<string, DeviceRecord>();

            foreach (var item in items.EnumerateArray())
            {
                var dataEl = item.GetProperty("data");
                var data = dataEl.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(dataEl.GetString()!).RootElement
                    : dataEl;

                var bid = data.TryGetProperty("browser_id", out var bidEl) ? bidEl.GetString() : null;
                if (string.IsNullOrEmpty(bid)) continue;

                var rawType = data.TryGetProperty("device_type", out var dt) ? dt.GetString() ?? "" : "";
                map[bid] = new DeviceRecord
                {
                    EntityId = item.GetProperty("id").GetString()!,
                    Name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    BrowserId = bid,
                    DeviceType = NormalizeDeviceType(rawType),
                    Platform = data.TryGetProperty("platform", out var p) ? p.GetString() : null,
                    Room = data.TryGetProperty("room", out var r) ? r.GetString() : null,
                };
            }

            _byBrowserId = map;
            _lastRefresh = DateTime.UtcNow;
        }
        catch
        {
            _lastRefresh = DateTime.UtcNow;
        }
    }

    private static string NormalizeDeviceType(string raw) => raw.ToLowerInvariant() switch
    {
        "smartphone" => "phone",
        "computer" => "computer",
        "tablet" => "tablet",
        "castvideo" or "castaudeo" => "tv",
        _ when raw.Contains("speaker", StringComparison.OrdinalIgnoreCase) => "speaker",
        _ => raw.ToLowerInvariant(),
    };

    private static string CapitalizeDeviceType(string type) => type.ToLowerInvariant() switch
    {
        "phone" => "Smartphone",
        "computer" => "Computer",
        "tablet" => "Tablet",
        _ => type,
    };

    private static ParsedUa ParseUserAgent(string ua)
    {
        var result = new ParsedUa();

        if (Regex.IsMatch(ua, @"iPhone", RegexOptions.IgnoreCase))
        {
            result.Platform = "iOS";
            result.DeviceType = "phone";
            result.Model = "iPhone";
        }
        else if (Regex.IsMatch(ua, @"iPad", RegexOptions.IgnoreCase))
        {
            result.Platform = "iPadOS";
            result.DeviceType = "tablet";
            result.Model = "iPad";
        }
        else if (Regex.IsMatch(ua, @"Android", RegexOptions.IgnoreCase))
        {
            result.Platform = "Android";
            var modelMatch = Regex.Match(ua, @"Android\s+[\d.]+;\s*(.+?)\)");
            if (modelMatch.Success)
            {
                var raw = modelMatch.Groups[1].Value.Trim();
                var buildIndex = raw.IndexOf(" Build/", StringComparison.OrdinalIgnoreCase);
                var model = buildIndex > 0 ? raw[..buildIndex].Trim() : raw;
                if (model.Length > 1) result.Model = model;
            }
            result.DeviceType = Regex.IsMatch(ua, @"Mobile", RegexOptions.IgnoreCase) ? "phone" : "tablet";
        }
        else if (Regex.IsMatch(ua, @"Windows", RegexOptions.IgnoreCase))
        {
            result.Platform = "Windows";
            result.DeviceType = "computer";
        }
        else if (Regex.IsMatch(ua, @"Macintosh", RegexOptions.IgnoreCase))
        {
            result.Platform = "macOS";
            result.DeviceType = "computer";
        }
        else if (Regex.IsMatch(ua, @"Linux", RegexOptions.IgnoreCase))
        {
            result.Platform = "Linux";
            result.DeviceType = "computer";
        }

        return result;
    }

    private sealed class ParsedUa
    {
        public string Platform { get; set; } = "unknown";
        public string DeviceType { get; set; } = "unknown";
        public string? Model { get; set; }
    }

    private sealed class DeviceRecord
    {
        public string EntityId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? BrowserId { get; set; }
        public string DeviceType { get; set; } = "unknown";
        public string? Platform { get; set; }
        public string? Room { get; set; }
    }
}
