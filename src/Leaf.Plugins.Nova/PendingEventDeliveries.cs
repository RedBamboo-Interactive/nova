using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Leaf.Plugins.Nova;

/// <summary>What happened to one injected event.</summary>
public enum EventDelivery
{
    /// <summary>RedCompute admitted it into the discussion's session.</summary>
    Delivered,
    /// <summary>Persisted and durably queued; the session does not exist yet (being created, deferred,
    /// or reset), or earlier events are still queued ahead of it, or delivery is being retried.</summary>
    Queued,
    /// <summary>A system event: persisted for display, never forwarded by design.</summary>
    Recorded,
    /// <summary>An idempotency key that was already used, with no redelivery requested.</summary>
    Reused,
    /// <summary>The session exists but RedCompute did not admit it; retrying may succeed.</summary>
    Failed,
    /// <summary>It can never be delivered (refused permanently, attempts exhausted, dropped from a
    /// full queue, expired, no Agent). The discussion shows a notice saying so.</summary>
    Undeliverable,
    /// <summary>The target refused the write (closed, or scoped LIVE checks failed).</summary>
    Denied,
    /// <summary>Persisted and held for the session's next turn (delivery with-next): it starts nothing.</summary>
    Held,
}

public sealed record EventInjectionResult(
    EventDelivery Delivery, string? MessageUid, string? ErrorCode = null, string? ErrorMessage = null,
    // What became of the delivery policy, in the shape /delegate reports it.
    Endpoints.DelegateEndpoints.DeliveryReport? Report = null);

public enum EventDrainStatus { Drained, WaitingForSession, Failed, Busy }

public sealed record EventDrainResult(
    EventDrainStatus Status, int Delivered, string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>
/// One event persisted to a discussion before it could reach the session, in arrival order.
/// The stream record (content, source, sender) stays the source of truth; the entry keys it by
/// its uid and fixes the RedCompute admission key, so a repeated drain can't admit it twice.
/// </summary>
internal sealed record PendingEventDelivery(
    string Uid, string AdmissionKey, bool Framed, string? ReplyToDiscussionId,
    DateTimeOffset? QueuedAt = null, int Attempts = 0);

/// <summary>
/// Events their sender held for the receiver's next turn (delivery <c>with-next</c>): persisted and
/// shown like any event, forwarded to nobody until Nova next gets an input admitted into the
/// discussion's session. Then each goes in as its own steer, under its own admission key, so it
/// joins that turn instead of starting one. Stored on the discussion entity beside the queue and
/// read and written only under <see cref="DiscussionEntityGate"/>. Nothing needed them by
/// definition, so the oldest and the stale ones leave without a notice.
/// </summary>
internal static class HeldEventDeliveries
{
    public const string Key = "held_event_deliveries";
    public const int MaxHeld = 50;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public static List<PendingEventDelivery> Read(JsonObject data) => PendingEventDeliveries.ReadList(data, Key);

    public static JsonObject Patch(IReadOnlyList<PendingEventDelivery> entries) => new()
    {
        [Key] = entries.Count == 0 ? null : PendingEventDeliveries.List(entries),
    };

    /// <summary>The held list with <paramref name="entry"/> added once, stale and surplus entries gone.</summary>
    public static List<PendingEventDelivery> With(List<PendingEventDelivery> held, PendingEventDelivery entry, DateTimeOffset now)
    {
        var kept = Fresh(held, now);
        if (!kept.Any(item => item.Uid == entry.Uid)) kept.Add(entry);
        return kept.Skip(Math.Max(0, kept.Count - MaxHeld)).ToList();
    }

    public static List<PendingEventDelivery> Fresh(List<PendingEventDelivery> held, DateTimeOffset now)
        => held.Where(item => item.QueuedAt is not { } at || now - at <= MaxAge).ToList();
}

/// <summary>
/// How long and how many events a discussion keeps for a session that isn't there, and how often
/// one event is tried before it is given up.
/// </summary>
internal sealed record EventQueuePolicy(int MaxQueued, TimeSpan MaxAge, int MaxAttempts)
{
    /// <summary>
    /// LIVE's queueable traffic is automation outcomes and heartbeat whispers: stale within hours,
    /// and the last 15 LIVE events reach Nova's message context anyway. Its display-only events
    /// (presence, camera, media, device, activity, callback summaries) are never queued.
    /// </summary>
    public static readonly EventQueuePolicy Live = new(20, TimeSpan.FromHours(2), 5);

    /// <summary>Chats, including deferred delivery discussions and per-item app discussions.</summary>
    public static readonly EventQueuePolicy Default = new(50, TimeSpan.FromHours(6), 5);

    public static EventQueuePolicy For(DiscussionRead discussion) => discussion.Type == "live" ? Live : Default;

    public string Age => MaxAge.TotalHours >= 1 ? $"{MaxAge.TotalHours:0.#} h" : $"{MaxAge.TotalMinutes:0} min";
}

/// <summary>
/// The durable per-discussion event queue, stored on the discussion entity. Read and written
/// only under <see cref="DiscussionEntityGate"/>.
/// </summary>
internal static class PendingEventDeliveries
{
    public const string QueueKey = "pending_event_deliveries";
    /// <summary>Queryable flag: true while the queue holds an entry (startup sweep).</summary>
    public const string PendingFlagKey = "event_delivery_pending";
    public const string UndeliverableKey = "undeliverable_events";
    /// <summary>Events dropped (overflow, expired) since the last count shown in the discussion.</summary>
    public const string DropsKey = "event_queue_drops";
    private const int MaxUndeliverable = 20;

    /// <summary>The RedCompute admission key for an event, the same on every delivery attempt.</summary>
    public static string AdmissionKey(string identity)
        => "nova-event:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    public static List<PendingEventDelivery> Read(JsonObject data) => ReadList(data, QueueKey);

    internal static List<PendingEventDelivery> ReadList(JsonObject data, string listKey)
    {
        var entries = new List<PendingEventDelivery>();
        if (data[listKey] is not JsonArray array) return entries;
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var uid = Text(item, "uid");
            var key = Text(item, "admission_key");
            if (uid is null || key is null) continue;
            entries.Add(new(uid, key, item["framed"] is JsonValue framed && framed.TryGetValue<bool>(out var f) && f,
                Text(item, "reply_to_discussion_id"),
                DateTimeOffset.TryParse(Text(item, "queued_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var at) ? at : null,
                item["attempts"] is JsonValue attempts && attempts.TryGetValue<int>(out var n) ? n : 0));
        }
        return entries;
    }

    public static JsonObject Patch(IReadOnlyList<PendingEventDelivery> entries) => new()
    {
        [QueueKey] = List(entries),
        [PendingFlagKey] = entries.Count > 0,
    };

    internal static JsonArray List(IReadOnlyList<PendingEventDelivery> entries)
        => new(entries.Select(entry => (JsonNode)new JsonObject
        {
            ["uid"] = entry.Uid,
            ["admission_key"] = entry.AdmissionKey,
            ["framed"] = entry.Framed,
            ["reply_to_discussion_id"] = entry.ReplyToDiscussionId,
            ["queued_at"] = entry.QueuedAt?.ToString("O"),
            ["attempts"] = entry.Attempts,
        }).ToArray());

    /// <summary>Records why an event left the queue without being delivered, newest last, bounded.</summary>
    public static JsonArray WithUndeliverable(JsonObject data, IEnumerable<(string Uid, string Error)> events)
    {
        var kept = (data[UndeliverableKey] as JsonArray)?.Select(node => node?.DeepClone()).ToList() ?? [];
        foreach (var (uid, error) in events)
            kept.Add(new JsonObject { ["uid"] = uid, ["error"] = error, ["at"] = DateTimeOffset.UtcNow.ToString("O") });
        return new JsonArray(kept.Skip(Math.Max(0, kept.Count - MaxUndeliverable)).ToArray());
    }

    public static string? UndeliverableError(JsonObject data, string uid)
        => (data[UndeliverableKey] as JsonArray)?.OfType<JsonObject>()
            .LastOrDefault(item => Text(item, "uid") == uid) is { } item ? Text(item, "error") ?? "undeliverable" : null;

    /// <summary>Drop counters awaiting their count in the discussion: overflow, expired, and whether
    /// the discussion was already told that dropping started.</summary>
    public static (int Overflow, int Expired, bool Notified) Drops(JsonObject data)
        => data[DropsKey] is JsonObject drops
            ? (Int(drops, "overflow"), Int(drops, "expired"), drops["notified"] is JsonValue v && v.TryGetValue<bool>(out var b) && b)
            : (0, 0, false);

    public static JsonObject DropsNode(int overflow, int expired, bool notified)
        => new() { ["overflow"] = overflow, ["expired"] = expired, ["notified"] = notified };

    private static int Int(JsonObject item, string key)
        => item[key] is JsonValue value && value.TryGetValue<int>(out var n) ? n : 0;

    private static string? Text(JsonObject item, string key)
        => item[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : null;
}
