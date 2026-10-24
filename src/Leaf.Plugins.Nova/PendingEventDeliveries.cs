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
    /// <summary>It can never be delivered (refused permanently, no Agent).</summary>
    Undeliverable,
    /// <summary>The target refused the write (closed, or scoped LIVE checks failed).</summary>
    Denied,
}

public sealed record EventInjectionResult(
    EventDelivery Delivery, string? MessageUid, string? ErrorCode = null, string? ErrorMessage = null);

public enum EventDrainStatus { Drained, WaitingForSession, Failed }

public sealed record EventDrainResult(
    EventDrainStatus Status, int Delivered, string? ErrorCode = null, string? ErrorMessage = null);

/// <summary>
/// One event persisted to a discussion before it could reach the session, in arrival order.
/// The stream record (content, source, sender) stays the source of truth; the entry keys it by
/// its uid and fixes the RedCompute admission key, so a repeated drain can't admit it twice.
/// </summary>
internal sealed record PendingEventDelivery(
    string Uid, string AdmissionKey, bool Framed, string? ReplyToDiscussionId);

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
    private const int MaxUndeliverable = 20;

    /// <summary>The RedCompute admission key for an event, the same on every delivery attempt.</summary>
    public static string AdmissionKey(string identity)
        => "nova-event:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));

    public static List<PendingEventDelivery> Read(JsonObject data)
    {
        var entries = new List<PendingEventDelivery>();
        if (data[QueueKey] is not JsonArray array) return entries;
        foreach (var node in array)
        {
            if (node is not JsonObject item) continue;
            var uid = Text(item, "uid");
            var key = Text(item, "admission_key");
            if (uid is null || key is null) continue;
            entries.Add(new(uid, key, item["framed"] is JsonValue framed && framed.TryGetValue<bool>(out var f) && f,
                Text(item, "reply_to_discussion_id")));
        }
        return entries;
    }

    public static JsonObject Patch(IReadOnlyList<PendingEventDelivery> entries) => new()
    {
        [QueueKey] = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject
        {
            ["uid"] = entry.Uid,
            ["admission_key"] = entry.AdmissionKey,
            ["framed"] = entry.Framed,
            ["reply_to_discussion_id"] = entry.ReplyToDiscussionId,
        }).ToArray()),
        [PendingFlagKey] = entries.Count > 0,
    };

    /// <summary>Records why an event left the queue without being delivered, newest last, bounded.</summary>
    public static JsonArray WithUndeliverable(JsonObject data, string uid, string error)
    {
        var kept = (data[UndeliverableKey] as JsonArray)?.Select(node => node?.DeepClone()).ToList() ?? [];
        kept.Add(new JsonObject { ["uid"] = uid, ["error"] = error, ["at"] = DateTimeOffset.UtcNow.ToString("O") });
        return new JsonArray(kept.Skip(Math.Max(0, kept.Count - MaxUndeliverable)).ToArray());
    }

    public static string? UndeliverableError(JsonObject data, string uid)
        => (data[UndeliverableKey] as JsonArray)?.OfType<JsonObject>()
            .LastOrDefault(item => Text(item, "uid") == uid) is { } item ? Text(item, "error") ?? "undeliverable" : null;

    private static string? Text(JsonObject item, string key)
        => item[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text : null;
}
