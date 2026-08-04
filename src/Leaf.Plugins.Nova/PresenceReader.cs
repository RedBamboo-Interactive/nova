using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

public sealed record PresenceReading(
    string State,
    string? PlaceId,
    string? PlaceName,
    string? Address,
    double? Latitude,
    double? Longitude,
    string? Timezone,
    string? Confidence,
    string? Source,
    DateTimeOffset? ObservedAt)
{
    public string? Location => Address ?? PlaceName;
}

/// <summary>
/// Read-only consumer of the Presence extension's entity contract. Nova owns no
/// acquisition, geofencing, reverse geocoding, credentials, or location cache.
/// </summary>
public sealed class PresenceReader(IEntityStore entities)
{
    public async Task<PresenceReading?> CurrentAsync(string? userId = null, CancellationToken ct = default)
    {
        try { return await ReadAsync(userId, ct); }
        catch { return null; }
    }

    private async Task<PresenceReading?> ReadAsync(string? userId, CancellationToken ct)
    {
        var states = await entities.QueryAsync(new EntityQuery
        {
            TypeSlug = "presence-state",
            DataEquals = string.IsNullOrWhiteSpace(userId)
                ? null : new Dictionary<string, object?> { ["user"] = userId },
            Limit = 20,
        }, ct);
        var state = states.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
        if (state == null) return null;

        var placeId = Text(state.Data, "place");
        LeafEntity? place = Guid.TryParse(placeId, out var id)
            ? await entities.GetAsync(id, ct) : null;
        return new PresenceReading(
            Text(state.Data, "state") ?? "unknown",
            placeId,
            place?.Name ?? Text(state.Data, "location_label"),
            place == null ? null : Text(place.Data, "address"),
            place == null ? null : Number(place.Data, "latitude"),
            place == null ? null : Number(place.Data, "longitude"),
            place == null ? null : Text(place.Data, "timezone"),
            Text(state.Data, "confidence"),
            Text(state.Data, "source"),
            DateTimeOffset.TryParse(Text(state.Data, "observed_at"), out var observed) ? observed : null);
    }

    private static string? Text(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text)
            && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static double? Number(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}

/// <summary>Projects Presence events onto Nova's conversational LIVE timeline.</summary>
public sealed class PresenceLiveBridge(IPluginEvents events, LiveEvents live) : IDisposable
{
    private IDisposable? _subscription;

    public void Start() => _subscription ??= events.Subscribe("*", OnEventAsync);

    private async Task OnEventAsync(PluginEvent evt)
    {
        if (!evt.EventType.StartsWith("presence.", StringComparison.Ordinal)) return;
        var data = evt.Payload;
        var (source, content) = evt.EventType switch
        {
            "presence.place.entered" => ("location", $"Laurent arrived at {Text(data, "place") ?? "a place"}"),
            "presence.place.left" => ("location", $"Laurent left {Text(data, "place") ?? "a place"}"),
            "presence.activity.started" => ("steam", $"Started playing {Text(data, "title") ?? "a game"}"),
            "presence.activity.changed" => ("steam", ActivityChangedLine(data)),
            "presence.activity.stopped" => ("steam", $"Stopped playing {Text(data, "title") ?? Text(data, "previousTitle") ?? "a game"}"),
            "presence.weather.changed" => ("weather", WeatherLine(data)),
            _ => ("", ""),
        };
        if (source.Length > 0) await live.PostAsync(source, content, data);
    }

    private static string WeatherLine(JsonObject data)
    {
        var condition = Text(data, "condition") ?? "changed";
        var temperature = data["temperatureC"] is JsonValue value
            && value.TryGetValue<double>(out var number) ? $" at {number:F0}°C" : "";
        return $"Weather {condition}{temperature}";
    }

    private static string ActivityChangedLine(JsonObject data)
    {
        static string[] Names(JsonObject payload, string key) => payload[key] is JsonArray values
            ? values.Select(item => item?.GetValue<string>()).Where(item => item != null).Cast<string>().ToArray()
            : [];
        var title = Text(data, "title") ?? "the game";
        var joined = Names(data, "joined");
        var left = Names(data, "left");
        if (joined.Length > 0) return $"{string.Join(", ", joined)} joined {title}";
        if (left.Length > 0) return $"{string.Join(", ", left)} left {title}";
        return $"Gaming activity changed: {title}";
    }

    private static string? Text(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
