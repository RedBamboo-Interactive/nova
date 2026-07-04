using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed class ResolvedDevice
{
    public string Name { get; init; } = "unknown";
    public string Type { get; init; } = "unknown";
    public string Platform { get; init; } = "unknown";
    public string? Room { get; init; }
    public string? EntityId { get; init; }

    public string ShortLabel => EntityId != null ? Name : $"{Platform} {Type}";
}

/// <summary>
/// Maps a browser (X-Device-Id + user agent) to a <c>speaker</c> entity, auto-registering
/// unknown browsers. The speaker TYPE is seeded by the smart-home plugin; instances
/// created here are just entities either plugin may use.
/// </summary>
public sealed class DeviceResolver(IEntityStore store)
{
    private Dictionary<string, DeviceRecord> _byBrowserId = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    public async Task<ResolvedDevice> ResolveAsync(string? userAgent, string? browserId, CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastRefresh > RefreshInterval)
            await RefreshDevicesAsync(ct);

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

            var registered = await AutoRegisterAsync(browserId, parsed, ct);
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

    private async Task<DeviceRecord?> AutoRegisterAsync(string browserId, ParsedUa parsed, CancellationToken ct)
    {
        try
        {
            var defaultName = parsed.Model ?? $"{parsed.Platform} {parsed.DeviceType}";
            var entity = await store.CreateAsync("speaker", defaultName, new JsonObject
            {
                ["browser_id"] = browserId,
                ["device_type"] = CapitalizeDeviceType(parsed.DeviceType),
                ["platform"] = parsed.Platform,
                ["room"] = parsed.DeviceType == "phone" ? "mobile" : "desk",
                ["default_volume"] = 50,
                ["spotify_device_id"] = browserId,
            }, ct);

            var record = new DeviceRecord
            {
                EntityId = entity.Id.ToString(),
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

    private async Task RefreshDevicesAsync(CancellationToken ct)
    {
        try
        {
            var items = await store.QueryAsync(new EntityQuery { TypeSlug = "speaker", Limit = 200 }, ct);
            var map = new Dictionary<string, DeviceRecord>();

            foreach (var item in items)
            {
                var data = item.Data;
                var bid = Str(data, "browser_id");
                if (string.IsNullOrEmpty(bid)) continue;

                map[bid] = new DeviceRecord
                {
                    EntityId = item.Id.ToString(),
                    Name = item.Name,
                    BrowserId = bid,
                    DeviceType = NormalizeDeviceType(Str(data, "device_type") ?? ""),
                    Platform = Str(data, "platform"),
                    Room = Str(data, "room"),
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

    private static string? Str(JsonObject data, string key)
    {
        var node = data[key];
        if (node is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : null;
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
