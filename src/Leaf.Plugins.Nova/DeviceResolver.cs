using System.Text.RegularExpressions;

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
/// Describes the request device from transport metadata only. Installed extensions
/// may contribute richer device or physical context through generic context slots.
/// </summary>
public sealed class DeviceResolver
{
    public Task<ResolvedDevice> ResolveAsync(
        string? userAgent, string? installationId, CancellationToken ct = default)
    {
        var parsed = ParseUserAgent(userAgent ?? "");
        return Task.FromResult(new ResolvedDevice
        {
            Name = parsed.Model ?? $"{parsed.Platform} {parsed.DeviceType}",
            Type = parsed.DeviceType,
            Platform = parsed.Platform,
        });
    }

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
