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
/// Resolves the installation identity authored by Presence. Unknown clients stay
/// anonymous here; Nova never creates fake speaker entities as a side effect of chat.
/// </summary>
public sealed class DeviceResolver(IEntityStore store)
{
    private Dictionary<string, ResolvedDevice> _byInstallationId = [];
    private DateTime _lastRefresh = DateTime.MinValue;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    public async Task<ResolvedDevice> ResolveAsync(
        string? userAgent, string? installationId, CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _lastRefresh > RefreshInterval) await RefreshAsync(ct);
        if (!string.IsNullOrWhiteSpace(installationId)
            && _byInstallationId.TryGetValue(installationId, out var registered))
            return registered;

        var parsed = ParseUserAgent(userAgent ?? "");
        return new ResolvedDevice
        {
            Name = parsed.Model ?? $"{parsed.Platform} {parsed.DeviceType}",
            Type = parsed.DeviceType,
            Platform = parsed.Platform,
        };
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var installations = await store.QueryAsync(new EntityQuery
            {
                TypeSlug = "client-installation",
                Limit = 500,
            }, ct);
            var devices = await store.QueryAsync(new EntityQuery { TypeSlug = "device", Limit = 500 }, ct);
            var byId = devices.ToDictionary(device => device.Id.ToString());
            var map = new Dictionary<string, ResolvedDevice>(StringComparer.Ordinal);
            foreach (var installation in installations)
            {
                var installationId = Text(installation.Data, "installation_id");
                var deviceId = Text(installation.Data, "device");
                if (installationId == null || deviceId == null || !byId.TryGetValue(deviceId, out var device)) continue;
                map[installationId] = new ResolvedDevice
                {
                    Name = device.Name,
                    Type = Text(device.Data, "device_class") ?? "unknown",
                    Platform = Text(device.Data, "platform") ?? "unknown",
                    Room = Text(device.Data, "room"),
                    EntityId = device.Id.ToString(),
                };
            }
            _byInstallationId = map;
        }
        catch
        {
            // Presence is optional and Nova remains usable while the entity API is unavailable.
        }
        finally { _lastRefresh = DateTime.UtcNow; }
    }

    private static string? Text(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static ParsedUa ParseUserAgent(string ua)
    {
        var result = new ParsedUa();
        if (Regex.IsMatch(ua, "iPhone", RegexOptions.IgnoreCase))
            (result.Platform, result.DeviceType, result.Model) = ("iOS", "phone", "iPhone");
        else if (Regex.IsMatch(ua, "iPad", RegexOptions.IgnoreCase))
            (result.Platform, result.DeviceType, result.Model) = ("iPadOS", "tablet", "iPad");
        else if (Regex.IsMatch(ua, "Android", RegexOptions.IgnoreCase))
            (result.Platform, result.DeviceType) = ("Android",
                Regex.IsMatch(ua, "Mobile", RegexOptions.IgnoreCase) ? "phone" : "tablet");
        else if (Regex.IsMatch(ua, "Windows", RegexOptions.IgnoreCase))
            (result.Platform, result.DeviceType) = ("Windows", "computer");
        else if (Regex.IsMatch(ua, "Macintosh", RegexOptions.IgnoreCase))
            (result.Platform, result.DeviceType) = ("macOS", "computer");
        else if (Regex.IsMatch(ua, "Linux", RegexOptions.IgnoreCase))
            (result.Platform, result.DeviceType) = ("Linux", "computer");
        return result;
    }

    private sealed class ParsedUa
    {
        public string Platform { get; set; } = "unknown";
        public string DeviceType { get; set; } = "unknown";
        public string? Model { get; set; }
    }
}
